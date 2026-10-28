using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Patcher.Resources;
using McvsOffline.Patcher.Zip;

namespace McvsOffline.Patcher.Art
{
    /// <summary>What the art patch changes in the APK: replaced entries (same name and compression) and new ones.</summary>
    public sealed class ArtResult
    {
        /// <summary>Entry name -> new uncompressed content.</summary>
        public Dictionary<string, byte[]> Replaced { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        /// <summary>New entries, each written right after the named existing entry (STORED, like the icon PNGs).</summary>
        public Dictionary<string, List<(string Name, byte[] Data)>> AddedAfter { get; } =
            new Dictionary<string, List<(string Name, byte[] Data)>>(StringComparer.Ordinal);

        /// <summary>The new zip entry for a replaced one: same name, same compression method, same timestamps.</summary>
        public ZipEntry Entry(ZipEntry original)
        {
            byte[] data = Replaced[original.Name];
            return original.Method == ZipEntry.MethodStored ? ZipEntry.CreateStored(original.Name, data, original) : ZipEntry.CreateDeflated(original.Name, data, original);
        }

        public IEnumerable<ZipEntry> Additions(ZipEntry anchor)
        {
            if (!AddedAfter.TryGetValue(anchor.Name, out var added)) yield break;
            foreach (var (name, data) in added) yield return ZipEntry.CreateStored(name, data, anchor);
        }
    }

    /// <summary>
    /// The project's own launcher icon and launch screen (app-icon-and-launch-screen.md §4-§5, WP-1..WP-4), applied when
    /// art exists: built into the patcher from <c>art/android/</c> (<c>--art auto</c>, the default) or from a folder
    /// (<c>--art &lt;dir&gt;</c>). With no art (the repository ships only the README and an example), or with
    /// <c>--art stock</c>, nothing changes and the APK keeps Gameloft's icon and the stock launch screen byte for byte.
    /// <list type="bullet">
    /// <item>icon: the 20 shipped icon PNGs are replaced in place (<see cref="IconArt"/>); each must be the verified file.</item>
    /// <item>splash (art.json <c>splash</c>): v31 / night-v31 configurations of the launcher theme in resources.arsc
    /// (<see cref="SplashTheme"/>).</item>
    /// <item>themed icon (icon/monochrome.png): a monochrome layer for the adaptive icon (<see cref="MonochromeIcon"/>).</item>
    /// </list>
    /// Every input is checked first (art to the spec, the shipped entries' CRCs, the table's shape); on any problem nothing
    /// is written. resources.arsc stays STORED (required from targetSdk 30) and is edited in one pass.
    /// </summary>
    public static class ArtPatch
    {
        public const string Auto = "auto";
        public const string Stock = "stock";

        public const string ArscEntry = "resources.arsc";

        /// <summary>resources.arsc of 1.17.32 (875,808 bytes, STORED, CRC32 2a11d4f0).</summary>
        public const int ArscBytes = 875808;

        public const uint ArscCrc = 0x2a11d4f0;

        /// <summary>
        /// Tests only: the art "auto" sees instead of the patcher's embedded files. The test assembly sets an empty source, so
        /// the owner adding art to art/android/ does not change what the other patcher tests expect.
        /// </summary>
        internal static ArtSource BuiltInForTests { get; set; }

        /// <summary>The art to use for --art auto|stock|&lt;dir&gt;, or null for none.</summary>
        public static ArtSource Resolve(string art, ArtSource builtIn = null)
        {
            if (string.IsNullOrEmpty(art) || art == Auto)
            {
                ArtSource embedded = builtIn ?? BuiltInForTests ?? new EmbeddedArtSource();
                return embedded.Files.Count == 0 ? null : embedded;
            }
            if (art == Stock) return null;
            var folder = new DirectoryArtSource(art);
            if (folder.Files.Count == 0) throw new InvalidDataException("--art: " + art + " holds no art files (see art/android/README.md)");
            return folder;
        }

        public static ArtResult Prepare(ZipArchiveReader.Archive input, PatcherOptions options, PatchReport report, Action<string> log, ArtSource builtIn = null)
        {
            log = log ?? (s => { });
            ArtSource source = Resolve(options.Art, builtIn);
            if (source == null)
            {
                if (options.Art != Stock) report.Warnings.Add("art: no project art built in (art/android/ has no files yet): Gameloft's icon and the stock launch screen are kept");
                return null;
            }
            log("art: " + source.Description + " (" + source.Files.Count + " files)");

            List<string> unknown = source.Files.Where(f => f != ArtSettings.FileName && !f.StartsWith("icon/", StringComparison.Ordinal)).ToList();
            if (unknown.Count > 0)
                throw new InvalidDataException("art: unknown file(s) " + string.Join(", ", unknown) + " in " + source.Description + " (expected art.json and icon/...; see art/android/README.md)");
            ArtSettings settings = source.Contains(ArtSettings.FileName) ? ArtSettings.Parse(source.Read(ArtSettings.FileName)) : null;
            Dictionary<(IconLayer, IconDensity), IconArt.Export> icon = IconArt.Build(source, settings, report.Warnings);
            ArtSettings.SplashSettings splash = settings?.Splash;
            bool monochrome = icon != null && icon.Keys.Any(k => k.Item1 == IconLayer.Monochrome);
            if (icon == null && splash == null)
            {
                report.Warnings.Add("art: " + source.Description + " has neither icon files nor a splash colour: nothing to change");
                return null;
            }

            var result = new ArtResult();
            if (icon != null)
            {
                foreach (IconArt.StockEntry stock in IconArt.StockEntries)
                {
                    ZipEntry e = Original(input, stock.Name, stock.Bytes, stock.Crc, options, report);
                    IconArt.Export export = icon[(stock.Layer, stock.Density)];
                    result.Replaced[stock.Name] = export.Data;
                    report.Notes.Add("art: " + stock.Name + ": " + Describe(e) + " -> " + export.Image.Describe() + ", " + export.Data.Length + " bytes (" + export.Source + ")");
                }
            }

            if (splash != null || monochrome)
            {
                ZipEntry arscEntry = Original(input, ArscEntry, ArscBytes, ArscCrc, options, report);
                ArscTable table = ArscTable.Parse(arscEntry.GetData());
                if (splash != null) report.Notes.Add(SplashTheme.Apply(table, splash));
                if (monochrome)
                {
                    report.Notes.Add(MonochromeIcon.Apply(table));
                    AddMonochrome(input, icon, result, options, report);
                }
                byte[] arsc = table.ToArray();
                ArscTable.Parse(arsc);   // the written table must read back
                result.Replaced[ArscEntry] = arsc;
                report.Notes.Add("art: " + ArscEntry + ": " + arscEntry.UncompressedSize + " -> " + arsc.Length + " bytes, " +
                                 (arscEntry.Method == ZipEntry.MethodStored ? "STORED and 4-byte aligned as before" : "same compression as before"));
            }
            if (icon != null)
            {
                report.Notes.Add("art: launcher icon replaced (" + IconArt.StockEntries.Count + " PNGs" + (monochrome ? " + themed icon" : "") +
                                 "); a launcher may show the old icon until its cache refreshes (reboot, or clear the home app's cache)");
            }
            return result;
        }

        /// <summary>WP-4: the adaptive icon XML with the monochrome layer, the spare slot's default file and six density files.</summary>
        private static void AddMonochrome(ZipArchiveReader.Archive input, Dictionary<(IconLayer, IconDensity), IconArt.Export> icon, ArtResult result,
                                          PatcherOptions options, PatchReport report)
        {
            ZipEntry xml = Original(input, MonochromeIcon.AdaptiveIconEntry, MonochromeIcon.AdaptiveIconBytes, MonochromeIcon.AdaptiveIconCrc, options, report);
            if (!xml.GetData().AsSpan().SequenceEqual(MonochromeIcon.AdaptiveIcon(false)))
                throw new InvalidDataException("art: " + MonochromeIcon.AdaptiveIconEntry + " is not the shipped two-layer adaptive icon; the themed icon is not added");
            result.Replaced[MonochromeIcon.AdaptiveIconEntry] = MonochromeIcon.AdaptiveIcon(true);
            report.Notes.Add("art: " + MonochromeIcon.AdaptiveIconEntry + ": <adaptive-icon> + <monochrome android:drawable=@0x" + MonochromeIcon.SpareId.ToString("x8") + ">");

            ZipEntry spare = Original(input, MonochromeIcon.DefaultFile, MonochromeIcon.DefaultFileBytes, MonochromeIcon.DefaultFileCrc, options, report);
            IconArt.Export mdpi = icon[(IconLayer.Monochrome, IconDensity.Mdpi)];
            result.Replaced[MonochromeIcon.DefaultFile] = mdpi.Data;
            report.Notes.Add("art: " + MonochromeIcon.DefaultFile + ": " + Describe(spare) + " -> " + mdpi.Image.Describe() + ", " + mdpi.Data.Length + " bytes (" + mdpi.Source + ")");
            foreach (IconDensity density in IconDensity.All)
            {
                string name = MonochromeIcon.DensityFile(density);
                if (input.Find(name) != null) throw new InvalidDataException("art: the APK already has " + name + " (patch the original APK)");
                string anchor = MonochromeIcon.AnchorEntry(density);
                if (input.Find(anchor) == null) throw new InvalidDataException("art: the APK has no " + anchor);
                IconArt.Export export = icon[(IconLayer.Monochrome, density)];
                if (!result.AddedAfter.TryGetValue(anchor, out var list)) result.AddedAfter[anchor] = list = new List<(string, byte[])>();
                list.Add((name, export.Data));
                report.Notes.Add("art: " + name + " (new): " + export.Image.Describe() + ", " + export.Data.Length + " bytes (" + export.Source + ")");
            }
        }

        /// <summary>The shipped entry, checked against its fingerprint: missing is always refused, other bytes only with --force.</summary>
        private static ZipEntry Original(ZipArchiveReader.Archive input, string name, int bytes, uint crc, PatcherOptions options, PatchReport report)
        {
            ZipEntry e = input.Find(name) ?? throw new InvalidDataException("art: the APK has no " + name + ": not the Modern Combat Versus " + KnownBuild.Version + " APK");
            if (e.Crc32 == crc && e.UncompressedSize == bytes) return e;
            string message = "art: " + name + " is not the shipped file (" + e.UncompressedSize + " bytes, CRC " + e.Crc32.ToString("x8") +
                             "; expected " + bytes + " bytes, CRC " + crc.ToString("x8") + ")";
            if (!options.Force) throw new InvalidDataException(message + ": nothing was written (use --art stock, or --force to replace it anyway)");
            report.Warnings.Add(message + "; replaced anyway (--force)");
            return e;
        }

        private static string Describe(ZipEntry e)
        {
            try
            {
                var (width, height, _, colorType) = Png.ReadHeader(e.GetData());
                return width + "x" + height + " " + Png.ColorTypeName(colorType) + ", " + e.UncompressedSize + " bytes";
            }
            catch (InvalidDataException)
            {
                return e.UncompressedSize + " bytes";
            }
        }
    }
}
