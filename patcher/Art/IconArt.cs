using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Patcher.Resources;

namespace McvsOffline.Patcher.Art
{
    public enum IconLayer
    {
        /// <summary>Adaptive icon foreground, 108 dp (mipmap/ic_launcher_foreground, 0x7f030003).</summary>
        Foreground,

        /// <summary>Adaptive icon background, 108 dp (mipmap/ic_launcher_background, 0x7f030002).</summary>
        Background,

        /// <summary>The pre-Android-8 bitmap icon, 48 dp, already shaped (mipmap/ic_launcher PNGs, 0x7f030001).</summary>
        Legacy,

        /// <summary>Themed-icon layer, 108 dp, one colour plus alpha (Android 13+; mipmap/ic_launcher_foreground1, 0x7f030004).</summary>
        Monochrome,
    }

    /// <summary>One density bucket of the launcher icon.</summary>
    public sealed class IconDensity
    {
        private IconDensity(string name, ushort dpi)
        {
            Name = name;
            Dpi = dpi;
        }

        /// <summary>ldpi ... xxxhdpi: the folder name under art/android/icon/ and the APK's mipmap-&lt;name&gt;-v4 qualifier.</summary>
        public string Name { get; }

        public ushort Dpi { get; }

        /// <summary>Pixels of <paramref name="dp"/> dp in this bucket (dp * dpi / 160; exact for 48 and 108 dp).</summary>
        public int Pixels(int dp)
        {
            return dp * Dpi / 160;
        }

        public static readonly IconDensity Ldpi = new IconDensity("ldpi", 120);
        public static readonly IconDensity Mdpi = new IconDensity("mdpi", 160);
        public static readonly IconDensity Hdpi = new IconDensity("hdpi", 240);
        public static readonly IconDensity Xhdpi = new IconDensity("xhdpi", 320);
        public static readonly IconDensity Xxhdpi = new IconDensity("xxhdpi", 480);
        public static readonly IconDensity Xxxhdpi = new IconDensity("xxxhdpi", 640);

        public static readonly IReadOnlyList<IconDensity> All = new[] { Ldpi, Mdpi, Hdpi, Xhdpi, Xxhdpi, Xxxhdpi };

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// The launcher icon part of the art patch (app-icon-and-launch-screen.md §4.1, §4.3, §5.1, WP-1): the owner's layers,
    /// validated and exported to every density, replace the 20 shipped icon PNGs in place, under the same entry names, so
    /// resources.arsc and the adaptive-icon XML stay as they are.
    /// <para>
    /// Sources, per layer: <c>icon/&lt;density&gt;/&lt;file&gt;</c> (exact size, copied byte for byte) wins over the master
    /// <c>icon/&lt;layer&gt;.png</c> (the xxxhdpi size or an exact multiple of it, area-averaged down to each density by
    /// <see cref="Png.Downscale"/>); the background can instead be a colour (art.json <c>icon.background</c>). If any icon
    /// art is present, foreground, background and legacy must resolve for all six densities, or nothing is written: a
    /// half-replaced icon (new adaptive icon, old Gameloft legacy icon) never ships.
    /// </para>
    /// </summary>
    public static class IconArt
    {
        /// <summary>Design size of the adaptive layers (foreground, background, monochrome): 108 dp, of which 72 dp are visible.</summary>
        public const int LayerDp = 108;

        /// <summary>Design size of the legacy icon.</summary>
        public const int LegacyDp = 48;

        /// <summary>Each file that goes into the APK: at most 512 KiB; all icon files together at most 4 MiB (§4.3).</summary>
        public const int MaxEntryBytes = 512 * 1024, MaxTotalBytes = 4 * 1024 * 1024;

        /// <summary>Masters are resampled, not shipped: they may be larger, up to <see cref="Png.MaxDimension"/> pixels.</summary>
        public const int MaxMasterBytes = 16 * 1024 * 1024;

        public static int Dp(IconLayer layer)
        {
            return layer == IconLayer.Legacy ? LegacyDp : LayerDp;
        }

        public static int Size(IconLayer layer, IconDensity density)
        {
            return density.Pixels(Dp(layer));
        }

        /// <summary>art/android path of a layer's master: icon/foreground.png, icon/background.png, icon/legacy.png, icon/monochrome.png.</summary>
        public static string MasterFile(IconLayer layer)
        {
            return "icon/" + layer.ToString().ToLowerInvariant() + ".png";
        }

        /// <summary>art/android path of a per-density export, named like Android Studio's mipmap output: icon/xxhdpi/ic_launcher_foreground.png.</summary>
        public static string DensityFile(IconLayer layer, IconDensity density)
        {
            return "icon/" + density.Name + "/" + ExportName(layer);
        }

        public static string ExportName(IconLayer layer)
        {
            switch (layer)
            {
                case IconLayer.Foreground: return "ic_launcher_foreground.png";
                case IconLayer.Background: return "ic_launcher_background.png";
                case IconLayer.Legacy: return "ic_launcher.png";
                default: return "ic_launcher_monochrome.png";
            }
        }

        /// <summary>A shipped icon entry of 1.17.32 (§2.3: STORED, size and CRC32 from the zip directory).</summary>
        public sealed class StockEntry
        {
            public StockEntry(string name, IconLayer layer, IconDensity density, int bytes, uint crc)
            {
                Name = name;
                Layer = layer;
                Density = density;
                Bytes = bytes;
                Crc = crc;
            }

            public string Name { get; }

            public IconLayer Layer { get; }

            /// <summary>The density whose export the entry gets. The default-config files (res/mipmap/...) get mdpi: the
            /// framework decodes a no-density resource as 160 dpi (the shipped hdpi copies there are a Gameloft quirk).</summary>
            public IconDensity Density { get; }

            public int Bytes { get; }

            public uint Crc { get; }
        }

        /// <summary>The 20 icon PNGs the art patch replaces: ic_launcher x7, ic_launcher_background x7, ic_launcher_foreground x6.</summary>
        public static readonly IReadOnlyList<StockEntry> StockEntries = new[]
        {
            new StockEntry("res/mipmap/ic_launcher.png", IconLayer.Legacy, IconDensity.Mdpi, 12414, 0x666099d7),
            new StockEntry("res/mipmap-ldpi-v4/ic_launcher.png", IconLayer.Legacy, IconDensity.Ldpi, 3345, 0xbdae05fd),
            new StockEntry("res/mipmap-mdpi-v4/ic_launcher.png", IconLayer.Legacy, IconDensity.Mdpi, 5811, 0x6e31ac2d),
            new StockEntry("res/mipmap-hdpi-v4/ic_launcher.png", IconLayer.Legacy, IconDensity.Hdpi, 12414, 0x666099d7),
            new StockEntry("res/mipmap-xhdpi-v4/ic_launcher.png", IconLayer.Legacy, IconDensity.Xhdpi, 21031, 0xf0220f0e),
            new StockEntry("res/mipmap-xxhdpi-v4/ic_launcher.png", IconLayer.Legacy, IconDensity.Xxhdpi, 44087, 0x66ec0e3b),
            new StockEntry("res/mipmap-xxxhdpi-v4/ic_launcher.png", IconLayer.Legacy, IconDensity.Xxxhdpi, 75314, 0xc3b9f1e6),
            new StockEntry("res/mipmap/ic_launcher_background.png", IconLayer.Background, IconDensity.Mdpi, 103, 0x66d1875e),
            new StockEntry("res/mipmap-ldpi-v4/ic_launcher_background.png", IconLayer.Background, IconDensity.Ldpi, 85, 0x6b12b715),
            new StockEntry("res/mipmap-mdpi-v4/ic_launcher_background.png", IconLayer.Background, IconDensity.Mdpi, 91, 0x1b94c04c),
            new StockEntry("res/mipmap-hdpi-v4/ic_launcher_background.png", IconLayer.Background, IconDensity.Hdpi, 103, 0x66d1875e),
            new StockEntry("res/mipmap-xhdpi-v4/ic_launcher_background.png", IconLayer.Background, IconDensity.Xhdpi, 118, 0x50976dc3),
            new StockEntry("res/mipmap-xxhdpi-v4/ic_launcher_background.png", IconLayer.Background, IconDensity.Xxhdpi, 166, 0x41e5472d),
            new StockEntry("res/mipmap-xxxhdpi-v4/ic_launcher_background.png", IconLayer.Background, IconDensity.Xxxhdpi, 228, 0x2af98fdb),
            new StockEntry("res/mipmap-ldpi-v4/ic_launcher_foreground.png", IconLayer.Foreground, IconDensity.Ldpi, 3339, 0x498cf086),
            new StockEntry("res/mipmap-mdpi-v4/ic_launcher_foreground.png", IconLayer.Foreground, IconDensity.Mdpi, 5744, 0x845dd923),
            new StockEntry("res/mipmap-hdpi-v4/ic_launcher_foreground.png", IconLayer.Foreground, IconDensity.Hdpi, 12142, 0x8dd2b27d),
            new StockEntry("res/mipmap-xhdpi-v4/ic_launcher_foreground.png", IconLayer.Foreground, IconDensity.Xhdpi, 20424, 0x359ec2ab),
            new StockEntry("res/mipmap-xxhdpi-v4/ic_launcher_foreground.png", IconLayer.Foreground, IconDensity.Xxhdpi, 42239, 0x8b03d5ca),
            new StockEntry("res/mipmap-xxxhdpi-v4/ic_launcher_foreground.png", IconLayer.Foreground, IconDensity.Xxxhdpi, 69211, 0xb4e9bd07),
        };

        /// <summary>One exported image: what goes into the APK for a layer at a density, and where it came from.</summary>
        public sealed class Export
        {
            public Export(IconLayer layer, IconDensity density, byte[] data, Png image, string source)
            {
                Layer = layer;
                Density = density;
                Data = data;
                Image = image;
                Source = source;
            }

            public IconLayer Layer { get; }

            public IconDensity Density { get; }

            /// <summary>The PNG file as it goes into the APK.</summary>
            public byte[] Data { get; }

            public Png Image { get; }

            /// <summary>For the report: "icon/foreground.png, 864 px master resampled" ...</summary>
            public string Source { get; }
        }

        /// <summary>Every art file the icon part understands (anything else under icon/ is refused as a probable typo).</summary>
        public static IEnumerable<string> KnownFiles()
        {
            foreach (IconLayer layer in Enum.GetValues(typeof(IconLayer)))
            {
                yield return MasterFile(layer);
                foreach (IconDensity d in IconDensity.All) yield return DensityFile(layer, d);
            }
        }

        /// <summary>
        /// Builds every export of the icon art in <paramref name="source"/>, or returns null when there is none (no icon file
        /// and no icon.background colour). Throws <see cref="InvalidDataException"/> for art that is incomplete or not to the
        /// spec (§4.3); doubtful but usable art adds a line to <paramref name="warnings"/>.
        /// </summary>
        public static Dictionary<(IconLayer, IconDensity), Export> Build(ArtSource source, ArtSettings settings, ICollection<string> warnings)
        {
            var known = new HashSet<string>(KnownFiles(), StringComparer.Ordinal);
            List<string> iconFiles = source.Files.Where(f => f.StartsWith("icon/", StringComparison.Ordinal)).ToList();
            List<string> unknown = iconFiles.Where(f => !known.Contains(f)).ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidDataException("art: unknown file(s) " + string.Join(", ", unknown) + " in " + source.Description +
                                               " (expected icon/<layer>.png masters and icon/<density>/ic_launcher*.png exports; see art/android/README.md)");
            }
            uint? backgroundColor = settings?.IconBackground;
            if (iconFiles.Count == 0)
            {
                // A colour alone is not an icon (art.json copied from the example before the icon is drawn).
                if (backgroundColor.HasValue) warnings?.Add("art: art.json icon.background is not used until the icon files exist (icon/foreground.png, icon/legacy.png)");
                return null;
            }
            if (backgroundColor.HasValue && source.Contains(MasterFile(IconLayer.Background)))
                throw new InvalidDataException("art: both icon/background.png and art.json icon.background are given; keep one");

            var exports = new Dictionary<(IconLayer, IconDensity), Export>();
            var missing = new List<string>();
            int total = 0;
            foreach (IconLayer layer in Enum.GetValues(typeof(IconLayer)))
            {
                bool hasAny = source.Contains(MasterFile(layer)) || IconDensity.All.Any(d => source.Contains(DensityFile(layer, d))) ||
                              layer == IconLayer.Background && backgroundColor.HasValue;
                if (layer == IconLayer.Monochrome && !hasAny) continue;   // the themed icon is optional
                if (!hasAny)
                {
                    missing.Add(MasterFile(layer) + (layer == IconLayer.Background ? " (or art.json icon.background)" : ""));
                    continue;
                }

                Png master = null;
                byte[] masterBytes = null;
                if (source.Contains(MasterFile(layer)))
                {
                    masterBytes = source.Read(MasterFile(layer));
                    if (masterBytes.Length > MaxMasterBytes) throw new InvalidDataException("art: " + MasterFile(layer) + " is larger than 16 MiB");
                    master = Decode(MasterFile(layer), masterBytes);
                    int unit = Size(layer, IconDensity.Xxxhdpi);
                    if (master.Width != master.Height || master.Width % unit != 0)
                    {
                        throw new InvalidDataException("art: " + MasterFile(layer) + " is " + master.Width + "x" + master.Height + "; a master must be square, " + unit +
                                                       "x" + unit + " (xxxhdpi) or an exact multiple (" + 2 * unit + ", " + 3 * unit + "...)");
                    }
                    CheckContent(layer, MasterFile(layer), master, warnings);
                }
                foreach (IconDensity density in IconDensity.All)
                {
                    int size = Size(layer, density);
                    string file = DensityFile(layer, density);
                    Export export;
                    if (source.Contains(file))
                    {
                        byte[] bytes = source.Read(file);
                        Png image = Decode(file, bytes);
                        if (image.Width != size || image.Height != size)
                            throw new InvalidDataException("art: " + file + " is " + image.Width + "x" + image.Height + ", expected " + size + "x" + size + " (" + Dp(layer) + " dp at " + density + ")");
                        CheckContent(layer, file, image, warnings);
                        export = new Export(layer, density, bytes, image, file);
                    }
                    else if (master != null)
                    {
                        if (master.Width == size) export = new Export(layer, density, masterBytes, master, MasterFile(layer));
                        else
                        {
                            Png scaled = master.Downscale(size);
                            export = new Export(layer, density, scaled.Encode(), scaled, MasterFile(layer) + ", " + master.Width + " px master resampled");
                        }
                    }
                    else if (layer == IconLayer.Background && backgroundColor.HasValue)
                    {
                        Png solid = Png.Solid(size, backgroundColor.Value);
                        export = new Export(layer, density, solid.Encode(), solid, "art.json icon.background " + ArtSettings.FormatColor(backgroundColor.Value));
                    }
                    else
                    {
                        missing.Add(file);
                        continue;
                    }
                    if (export.Data.Length > MaxEntryBytes)
                        throw new InvalidDataException("art: the " + layer.ToString().ToLowerInvariant() + " icon at " + density + " is " + export.Data.Length / 1024 + " KiB (from " + export.Source + "); at most 512 KiB per file");
                    total += export.Data.Length;
                    exports[(layer, density)] = export;
                }
            }
            if (missing.Count > 0)
            {
                throw new InvalidDataException("art: the icon is incomplete in " + source.Description + ", nothing was written; missing " + string.Join(", ", missing.Distinct()) +
                                               " (foreground, background and legacy are needed for all six densities, from a master or per-density files)");
            }
            if (total > MaxTotalBytes) throw new InvalidDataException("art: the icon files add up to " + total / 1024 + " KiB; at most 4 MiB");
            return exports;
        }

        private static Png Decode(string file, byte[] bytes)
        {
            try
            {
                return Png.Decode(bytes);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException("art: " + file + ": " + ex.Message);
            }
        }

        /// <summary>§4.3 content checks: foreground transparency and safe zone, monochrome alpha and single colour.</summary>
        private static void CheckContent(IconLayer layer, string file, Png image, ICollection<string> warnings)
        {
            if (layer == IconLayer.Foreground)
            {
                if (!image.HasTransparency)
                {
                    warnings?.Add("art: " + file + " is fully opaque: it hides the background layer and fills the whole mask, like the stock icon " +
                                  "(draw the logo on transparency; the splash then also shows it larger)");
                    return;
                }
                (double outsideVisible, double outsideSafe) = OutsideZones(image);
                if (outsideVisible > 0)
                    warnings?.Add("art: " + file + ": " + Percent(outsideVisible) + " of the drawn pixels lie outside the visible 72 dp square (always cut off)");
                else if (outsideSafe > 0)
                    warnings?.Add("art: " + file + ": " + Percent(outsideSafe) + " of the drawn pixels lie outside the 66 dp safe circle (round masks cut them)");
            }
            else if (layer == IconLayer.Monochrome)
            {
                if (!image.HasTransparency) throw new InvalidDataException("art: " + file + " has no transparent pixels; the themed icon is a white shape on transparency");
                var colors = new HashSet<int>();
                byte[] p = image.Rgba;
                for (int i = 0; i < p.Length && colors.Count < 2; i += 4)
                {
                    if (p[i + 3] != 0) colors.Add(p[i] << 16 | p[i + 1] << 8 | p[i + 2]);
                }
                if (colors.Count > 1) warnings?.Add("art: " + file + " uses more than one colour; the launcher keeps only the alpha channel and tints it");
            }
        }

        /// <summary>Share of the drawn pixels (alpha >= 128) outside the central 72 dp square and outside the central 66 dp circle.</summary>
        internal static (double OutsideVisible, double OutsideSafe) OutsideZones(Png image)
        {
            int size = image.Width;
            double center = size / 2.0, visibleHalf = size * 36.0 / LayerDp, safeRadius = size * 33.0 / LayerDp;
            long drawn = 0, outsideVisible = 0, outsideSafe = 0;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (image.Rgba[(y * size + x) * 4 + 3] < 128) continue;
                    drawn++;
                    double dx = x + 0.5 - center, dy = y + 0.5 - center;
                    if (Math.Abs(dx) > visibleHalf || Math.Abs(dy) > visibleHalf) outsideVisible++;
                    if (dx * dx + dy * dy > safeRadius * safeRadius) outsideSafe++;
                }
            }
            return drawn == 0 ? (0, 0) : ((double)outsideVisible / drawn, (double)outsideSafe / drawn);
        }

        private static string Percent(double share)
        {
            return share < 0.001 ? "less than 0.1 %" : (share * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " %";
        }
    }
}
