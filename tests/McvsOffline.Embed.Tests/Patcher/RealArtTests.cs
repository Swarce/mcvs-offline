using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher;
using McvsOffline.Patcher.Art;
using McvsOffline.Patcher.Manifest;
using McvsOffline.Patcher.Resources;
using McvsOffline.Patcher.Tools;
using McvsOffline.Patcher.Zip;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// The art patch against the player's real 1.17.32 APK (MCVS_APK; never in the repository): the fingerprints of every
    /// entry it replaces, the table round trip, and aapt2's view of the result (app-icon-and-launch-screen.md §5.4).
    /// Synthetic art only (risk R6).
    /// </summary>
    public class RealArtTests
    {
        private readonly ITestOutputHelper output;

        public RealArtTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private string Apk()
        {
            string apk = TestEnvironment.GameApk;
            if (apk != null) return apk;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but the original APK (MCVS_APK) is missing");
            output.WriteLine("original APK (MCVS_APK) not available: skipped");
            return null;
        }

        private const string ArtJson = "{\"splash\": {\"background\": \"#10181F\", \"backgroundNight\": \"#000000\", \"iconBackground\": \"#223344\", \"iconPreferred\": true}}";

        [Fact]
        public void TheRealApkShipsTheVerifiedIconFilesAndTable()
        {
            string apk = Apk();
            if (apk == null) return;
            ZipArchiveReader.Archive input = ZipArchiveReader.Read(apk);
            foreach (IconArt.StockEntry stock in IconArt.StockEntries)
            {
                ZipEntry e = input.Find(stock.Name);
                Assert.True(e != null, stock.Name);
                Assert.Equal(ZipEntry.MethodStored, e.Method);
                Assert.Equal(stock.Crc, e.Crc32);
                Assert.Equal(stock.Bytes, e.UncompressedSize);
                // §2.3: 48 dp-sized bitmaps everywhere (the default-config files are copies of hdpi), RGB or white greyscale.
                var (width, height, depth, colorType) = Png.ReadHeader(e.GetData());
                int expected = stock.Name.StartsWith("res/mipmap/", StringComparison.Ordinal) ? 72 : stock.Density.Pixels(48);
                Assert.Equal((expected, expected, (byte)8), (width, height, depth));
                Assert.Equal(stock.Layer == IconLayer.Background ? Png.TypeGreyscale : Png.TypeRgb, colorType);
                Png.Decode(e.GetData());   // the validator accepts Gameloft's own files
            }

            ZipEntry arsc = input.Find(ArtPatch.ArscEntry);
            Assert.Equal(ZipEntry.MethodStored, arsc.Method);
            Assert.Equal(ArtPatch.ArscCrc, arsc.Crc32);
            Assert.Equal(ArtPatch.ArscBytes, arsc.UncompressedSize);
            byte[] data = arsc.GetData();
            ArscTable table = ArscTable.Parse(data);
            Assert.Equal(data, table.ToArray());

            // The launcher theme exactly as SplashTheme expects it (§5.2).
            ArscPackage package = table.Package(0x7f);
            Assert.Equal("style", package.TypeName(SplashTheme.StyleTypeId));
            Assert.Equal(426, package.TypeSpec(SplashTheme.StyleTypeId).EntryCount);
            Assert.Equal(ArscTypeSpec.ConfigVersion, package.TypeSpec(SplashTheme.StyleTypeId).Flags(SplashTheme.ThemeEntry));
            var configs = package.Types(SplashTheme.StyleTypeId).Where(t => t.Has(SplashTheme.ThemeEntry))
                                 .Select(t => (ArscConfig.Describe(t.Config), t.Entry(SplashTheme.ThemeEntry).Parent)).ToList();
            Assert.Equal(SplashTheme.ShippedConfigs, configs);
            Assert.All(package.Types(SplashTheme.StyleTypeId).Where(t => t.Has(SplashTheme.ThemeEntry)), t => Assert.Empty(t.Entry(SplashTheme.ThemeEntry).Items));
            Assert.DoesNotContain(package.Types(SplashTheme.StyleTypeId), t => ArscConfig.SdkVersion(t.Config) >= 31);

            // The themed icon's inputs (WP-4).
            ZipEntry xml = input.Find(MonochromeIcon.AdaptiveIconEntry);
            Assert.Equal((MonochromeIcon.AdaptiveIconCrc, (long)MonochromeIcon.AdaptiveIconBytes), (xml.Crc32, xml.UncompressedSize));
            Assert.Equal(MonochromeIcon.AdaptiveIcon(false), xml.GetData());
            ZipEntry spare = input.Find(MonochromeIcon.DefaultFile);
            Assert.Equal((MonochromeIcon.DefaultFileCrc, (long)MonochromeIcon.DefaultFileBytes), (spare.Crc32, spare.UncompressedSize));
            Assert.Equal(ArscTypeSpec.ConfigDensity | ArscTypeSpec.ConfigVersion, package.TypeSpec(MonochromeIcon.MipmapTypeId).Flags(1));
            foreach (IconDensity d in IconDensity.All) Assert.Null(input.Find(MonochromeIcon.DensityFile(d)));
        }

        [Fact]
        public void StockAndNoArtChangeNothing()
        {
            string apk = Apk();
            if (apk == null) return;
            ZipArchiveReader.Archive input = ZipArchiveReader.Read(apk);
            var report = new PatchReport();
            Assert.Null(ArtPatch.Prepare(input, new PatcherOptions { Art = ArtPatch.Stock }, report, null, ArtFixtures.IconMasters()));
            Assert.Null(ArtPatch.Prepare(input, new PatcherOptions(), report, null, new MemoryArtSource()));
            Assert.Empty(report.Notes);
            Assert.Single(report.Warnings);   // "no project art built in"
        }

        /// <summary>The art committed to art/android/ (what every player's default run applies) patches the real APK unforced.</summary>
        [Fact]
        public void TheBuiltInArtPatchesTheRealApk()
        {
            string apk = Apk();
            if (apk == null) return;
            var embedded = new EmbeddedArtSource();
            if (embedded.Files.Count == 0)
            {
                output.WriteLine("art/android/ holds no art yet: skipped");
                return;
            }
            var report = new PatchReport();
            ArtResult result = ArtPatch.Prepare(ZipArchiveReader.Read(apk), new PatcherOptions(), report, output.WriteLine, embedded);
            foreach (string n in report.Notes.Concat(report.Warnings)) output.WriteLine(n);
            Assert.True(result != null, string.Join("; ", report.Warnings));
            Assert.DoesNotContain(report.Warnings, w => w.Contains("--force", StringComparison.Ordinal));
        }

        /// <summary>
        /// The real APK with every art option (icon from masters, themed icon, light and dark splash): the replaced and added
        /// entries, and aapt2's dumps: the resource table gains exactly the expected lines, badging is unchanged, the adaptive
        /// icon has its third layer, and the result is aligned.
        /// </summary>
        [Fact]
        public void Aapt2SeesExactlyTheArtChangesInTheRealApk()
        {
            string apk = Apk();
            if (apk == null) return;
            ZipArchiveReader.Archive input = ZipArchiveReader.Read(apk);
            MemoryArtSource art = ArtFixtures.IconMasters(monochrome: true, artJson: ArtJson);
            string dir = TestEnvironment.NewTempDir("real-art");
            var report = new PatchReport();
            ArtResult result = ArtPatch.Prepare(input, new PatcherOptions { Art = ArtFixtures.WriteDirectory(art, Path.Combine(dir, "art")) }, report, output.WriteLine);
            foreach (string n in report.Notes) output.WriteLine(n);
            Assert.Empty(report.Warnings);   // the real entries match every fingerprint: nothing is forced

            var expectedReplaced = IconArt.StockEntries.Select(s => s.Name)
                                          .Concat(new[] { ArtPatch.ArscEntry, MonochromeIcon.AdaptiveIconEntry, MonochromeIcon.DefaultFile });
            Assert.Equal(expectedReplaced.OrderBy(n => n, StringComparer.Ordinal), result.Replaced.Keys.OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(IconArt.StockEntries.Count + IconDensity.All.Count + 6, report.Notes.Count);   // + splash, mipmap, XML, spare file, arsc size, summary
            Assert.Contains(report.Notes, n => n == "art: resources.arsc: 875808 -> " + result.Replaced[ArtPatch.ArscEntry].Length + " bytes, STORED and 4-byte aligned as before");

            // The resource-only part of the APK, as the patcher composes it (same order, replacements, additions).
            var entries = new List<ZipEntry>();
            foreach (ZipEntry e in input.Entries.Where(e => e.Name == ManifestPatch.EntryName || e.Name == ArtPatch.ArscEntry || e.Name.StartsWith("res/", StringComparison.Ordinal)))
            {
                entries.Add(result.Replaced.ContainsKey(e.Name) ? result.Entry(e) : e);
                entries.AddRange(result.Additions(e));
            }
            string patched = Path.Combine(dir, "res-patched.apk"), original = Path.Combine(dir, "res-original.apk");
            File.WriteAllBytes(patched, ZipArchiveWriter.Write(entries));
            File.WriteAllBytes(original, ZipArchiveWriter.Write(input.Entries.Where(e => e.Name == ManifestPatch.EntryName || e.Name == ArtPatch.ArscEntry ||
                                                                                       e.Name.StartsWith("res/", StringComparison.Ordinal)).ToList()));
            try
            {
                ZipArchiveReader.Archive back = ZipArchiveReader.Read(patched);
                foreach (var kv in result.Replaced) Assert.Equal(kv.Value, back.Find(kv.Key).GetData());
                Assert.Equal(ZipEntry.MethodStored, back.Find(ArtPatch.ArscEntry).Method);
                Assert.All(IconDensity.All, d => Assert.NotNull(back.Find(MonochromeIcon.DensityFile(d))));

                if (TestEnvironment.Aapt2 == null)
                {
                    output.WriteLine("aapt2 not found (set MCVS_BUILD_TOOLS): dumps skipped");
                    return;
                }
                List<string> added = ArtAapt2.AddedLines(ArtAapt2.Resources(original), ArtAapt2.Resources(patched));
                var expected = IconDensity.All.Select(d => "(" + d.Name + "-v4) (file) " + MonochromeIcon.DensityFile(d) + " type=PNG").ToList();
                expected.AddRange(new[]
                {
                    "(v31) (style) size=3 parent=0x01030242", "0x0101062c=#ff10181f", "0x01010630=#ff223344", "0x01010668=1",
                    "(night-v31) (style) size=3 parent=0x01030242", "0x0101062c=#ff000000", "0x01010630=#ff223344", "0x01010668=1",
                });
                Assert.Equal(expected, added);
                Assert.Equal(Aapt2Dump.Badging(original), Aapt2Dump.Badging(patched));
                int code = BuildTools.Run(TestEnvironment.Aapt2, new[] { "dump", "xmltree", "--file", MonochromeIcon.AdaptiveIconEntry, patched }, null, out string tree);
                Assert.Equal(0, code);
                output.WriteLine(tree);
                Assert.Contains("E: monochrome (line=1)", tree);
                Assert.Contains("http://schemas.android.com/apk/res/android:drawable(0x01010199)=@0x7f030004", tree);
                string zipalign = TestEnvironment.BuildToolsDir == null ? null : Path.Combine(TestEnvironment.BuildToolsDir, "zipalign");
                if (zipalign != null && File.Exists(zipalign))
                    Assert.Equal(0, BuildTools.Run(zipalign, new[] { "-c", "-p", "4", patched }, null, out _));
            }
            finally
            {
                File.Delete(patched);
                File.Delete(original);
            }
        }

        /// <summary>A dry run of the whole patcher with --art &lt;dir&gt;: one report line per replaced or added file.</summary>
        [Fact]
        public void TheDryRunReportsEveryArtFile()
        {
            string apk = Apk();
            string server = RealAdminPagePatchTests.EmbeddedServer(output);
            if (apk == null) return;
            if (server == null)
            {
                if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_EMBEDDED") == "1")
                    throw new InvalidOperationException("MCVS_REQUIRE_EMBEDDED=1 but the embedded server DLL is missing");
                output.WriteLine("embedded server DLL not available: skipped");
                return;
            }
            string dir = ArtFixtures.WriteDirectory(ArtFixtures.IconMasters(artJson: ArtJson), TestEnvironment.NewTempDir("real-art-dry"));
            PatchReport stock = ApkPatcher.Run(new PatcherOptions { InputApk = apk, ServerDll = server, DryRun = true, Art = ArtPatch.Stock }, null);
            PatchReport report = ApkPatcher.Run(new PatcherOptions { InputApk = apk, ServerDll = server, DryRun = true, Art = dir }, output.WriteLine);
            List<string> art = report.Notes.Where(n => n.StartsWith("art: ", StringComparison.Ordinal)).ToList();
            foreach (string n in art) output.WriteLine(n);
            Assert.Equal(stock.Notes, report.Notes.Where(n => !art.Contains(n)));
            Assert.Equal(IconArt.StockEntries.Count + 3, art.Count);   // one per PNG, the splash table edit, the arsc size, the summary
            Assert.Equal(stock.Modified.Concat(IconArt.StockEntries.Select(s => s.Name)).Append(ArtPatch.ArscEntry).OrderBy(n => n, StringComparer.Ordinal),
                         report.Modified.OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(stock.Added, report.Added);
            Assert.Contains(art, n => n == "art: res/mipmap-xxxhdpi-v4/ic_launcher_foreground.png: 192x192 RGB, 69211 bytes -> 432x432 RGBA, " +
                                           ArtFixtures.Disc(432, 0xF0C040, 0.25).Encode().Length + " bytes (icon/foreground.png)");
            Assert.DoesNotContain(report.Warnings, w => w.StartsWith("art:", StringComparison.Ordinal));
        }
    }
}
