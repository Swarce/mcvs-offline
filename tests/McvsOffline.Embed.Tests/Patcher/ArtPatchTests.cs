using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher;
using McvsOffline.Patcher.Art;
using McvsOffline.Patcher.Resources;
using McvsOffline.Patcher.Signing;
using McvsOffline.Patcher.Zip;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// The art patch on synthetic art and a synthetic APK (app-icon-and-launch-screen.md §4.3 validation, §5.1 icon
    /// replacement, §5.3 --art auto|stock|&lt;dir&gt;): what is accepted, what is refused, and that nothing else changes.
    /// </summary>
    public class ArtPatchTests
    {
        private readonly ITestOutputHelper output;

        public ArtPatchTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private const string SplashJson = @"{
  // comments and trailing commas are fine
  ""splash"": { ""background"": ""#10181F"", ""backgroundNight"": ""#000000"", ""iconPreferred"": true, },
}";

        [Fact]
        public void ArtJsonIsStrictAboutKeysAndColours()
        {
            ArtSettings s = ArtSettings.Parse(Encoding.UTF8.GetBytes(SplashJson));
            Assert.Equal(0x10181Fu, s.Splash.Background);
            Assert.Equal(0u, s.Splash.BackgroundNight);
            Assert.True(s.Splash.IconPreferred);
            Assert.False(s.Splash.WindowBackground);
            Assert.True(s.Splash.HasNight);
            Assert.Null(s.IconBackground);

            ArtSettings icon = ArtSettings.Parse(Encoding.UTF8.GetBytes("{\"icon\": {\"background\": \"#ff8800\"}, \"splash\": null}"));
            Assert.Equal(0xFF8800u, icon.IconBackground);
            Assert.Null(icon.Splash);
            Assert.False(ArtSettings.Parse(Encoding.UTF8.GetBytes("{\"splash\": {\"background\": \"#000000\", \"backgroundNight\": \"#000000\"}}")).Splash.HasNight);

            void Refused(string json, string reason)
            {
                var ex = Assert.Throws<InvalidDataException>(() => ArtSettings.Parse(Encoding.UTF8.GetBytes(json)));
                Assert.Contains(reason, ex.Message);
            }
            Refused("{\"splash\": {\"backround\": \"#000000\"}}", "unknown key splash.backround");
            Refused("{\"label\": \"MCVS Offline\"}", "unknown key label");
            Refused("{\"splash\": {\"background\": \"#00000080\"}}", "\"#RRGGBB\"");
            Refused("{\"splash\": {\"background\": \"red\"}}", "\"#RRGGBB\"");
            Refused("{\"splash\": {\"background\": 1}}", "must be a string");
            Refused("{\"splash\": {\"iconPreferred\": true}}", "splash.background is required");
            Refused("{\"splash\": {\"background\": \"#000000\", \"iconPreferred\": \"yes\"}}", "true or false");
            Refused("{\"splash\": {\"background\": \"#000000\", \"iconBackgroundNight\": \"#000000\"}}", "needs splash.iconBackground");
            Refused("[1]", "must be a JSON object");
            Refused("{", "not valid JSON");
        }

        [Fact]
        public void MastersAreExportedToEveryDensity()
        {
            MemoryArtSource art = ArtFixtures.IconMasters();
            var warnings = new List<string>();
            var exports = IconArt.Build(art, null, warnings);
            Assert.Empty(warnings);
            Assert.Equal(18, exports.Count);
            foreach (var export in exports.Values)
            {
                int size = IconArt.Size(export.Layer, export.Density);
                Png decoded = Png.Decode(export.Data);
                Assert.Equal(size, decoded.Width);
                Assert.Equal(size, decoded.Height);
                Assert.True(export.Data.Length <= IconArt.MaxEntryBytes);
            }
            Assert.Equal(new[] { 81, 108, 162, 216, 324, 432 }, IconDensity.All.Select(d => IconArt.Size(IconLayer.Foreground, d)));
            Assert.Equal(new[] { 36, 48, 72, 96, 144, 192 }, IconDensity.All.Select(d => IconArt.Size(IconLayer.Legacy, d)));
            // An exact-size master goes in byte for byte; a larger one is resampled.
            Assert.Equal(art.Content["icon/foreground.png"], exports[(IconLayer.Foreground, IconDensity.Xxxhdpi)].Data);
            Assert.Equal("icon/foreground.png", exports[(IconLayer.Foreground, IconDensity.Xxxhdpi)].Source);
            Assert.Equal("icon/background.png, 864 px master resampled", exports[(IconLayer.Background, IconDensity.Xxxhdpi)].Source);
            Assert.True(exports[(IconLayer.Foreground, IconDensity.Ldpi)].Image.HasTransparency);
            Assert.False(exports[(IconLayer.Background, IconDensity.Ldpi)].Image.HasTransparency);
        }

        [Fact]
        public void PerDensityFilesWinAndBackgroundCanBeAColour()
        {
            MemoryArtSource art = ArtFixtures.IconMasters();
            art.Content.Remove("icon/background.png");
            byte[] tuned = ArtFixtures.Disc(144, 0x00FF00, 0.4).Encode();
            art.Content["icon/xxhdpi/ic_launcher.png"] = tuned;
            var settings = ArtSettings.Parse(Encoding.UTF8.GetBytes("{\"icon\": {\"background\": \"#204060\"}}"));
            var exports = IconArt.Build(art, settings, null);
            Assert.Equal(tuned, exports[(IconLayer.Legacy, IconDensity.Xxhdpi)].Data);
            Assert.Equal("icon/xxhdpi/ic_launcher.png", exports[(IconLayer.Legacy, IconDensity.Xxhdpi)].Source);
            Png background = exports[(IconLayer.Background, IconDensity.Mdpi)].Image;
            Assert.Equal(108, background.Width);
            Assert.Equal(new byte[] { 0x20, 0x40, 0x60, 0xFF }, background.Rgba.Take(4).ToArray());
            Assert.StartsWith("art.json icon.background #204060", exports[(IconLayer.Background, IconDensity.Mdpi)].Source);

            // Per-density files alone are a complete layer too (no master needed).
            MemoryArtSource exported = ArtFixtures.IconMasters();
            exported.Content.Remove("icon/legacy.png");
            foreach (IconDensity d in IconDensity.All) exported.Content[IconArt.DensityFile(IconLayer.Legacy, d)] = ArtFixtures.Disc(IconArt.Size(IconLayer.Legacy, d), 0x123456, 0.4).Encode();
            Assert.Equal(18, IconArt.Build(exported, null, null).Count);
        }

        [Fact]
        public void IncompleteOrOffSpecArtIsRefused()
        {
            void Refused(MemoryArtSource art, string reason, ArtSettings settings = null)
            {
                var ex = Assert.Throws<InvalidDataException>(() => IconArt.Build(art, settings, new List<string>()));
                output.WriteLine(ex.Message);
                Assert.Contains(reason, ex.Message);
            }

            MemoryArtSource noLegacy = ArtFixtures.IconMasters();
            noLegacy.Content.Remove("icon/legacy.png");
            Refused(noLegacy, "missing icon/legacy.png");

            MemoryArtSource partial = ArtFixtures.IconMasters();
            partial.Content.Remove("icon/legacy.png");
            partial.Content["icon/mdpi/ic_launcher.png"] = ArtFixtures.Disc(48, 0, 0.4).Encode();
            Refused(partial, "missing icon/ldpi/ic_launcher.png, icon/hdpi/ic_launcher.png");

            MemoryArtSource onlyForeground = new MemoryArtSource();
            onlyForeground.Content["icon/foreground.png"] = ArtFixtures.Disc(432, 0, 0.25).Encode();
            Refused(onlyForeground, "missing icon/background.png (or art.json icon.background), icon/legacy.png");

            MemoryArtSource odd = ArtFixtures.IconMasters();
            odd.Content["icon/foreground.png"] = ArtFixtures.Disc(500, 0, 0.25).Encode();
            Refused(odd, "500x500; a master must be square, 432x432 (xxxhdpi) or an exact multiple");

            MemoryArtSource small = ArtFixtures.IconMasters();
            small.Content["icon/legacy.png"] = ArtFixtures.Disc(96, 0, 0.25).Encode();
            Refused(small, "icon/legacy.png is 96x96");

            MemoryArtSource wrongDensity = ArtFixtures.IconMasters();
            wrongDensity.Content["icon/hdpi/ic_launcher_foreground.png"] = ArtFixtures.Disc(108, 0, 0.25).Encode();
            Refused(wrongDensity, "icon/hdpi/ic_launcher_foreground.png is 108x108, expected 162x162 (108 dp at hdpi)");

            MemoryArtSource typo = ArtFixtures.IconMasters();
            typo.Content["icon/Foreground.png"] = typo.Content["icon/foreground.png"];
            Refused(typo, "unknown file(s) icon/Foreground.png");

            MemoryArtSource both = ArtFixtures.IconMasters();
            Refused(both, "keep one", ArtSettings.Parse(Encoding.UTF8.GetBytes("{\"icon\": {\"background\": \"#000000\"}}")));

            MemoryArtSource broken = ArtFixtures.IconMasters();
            byte[] bad = (byte[])broken.Content["icon/legacy.png"].Clone();
            bad[bad.Length - 30] ^= 0xFF;
            broken.Content["icon/legacy.png"] = bad;
            Refused(broken, "art: icon/legacy.png: damaged PNG");

            MemoryArtSource opaqueMono = ArtFixtures.IconMasters();
            opaqueMono.Content["icon/monochrome.png"] = Png.Solid(432, 0xFFFFFF).Encode();
            Refused(opaqueMono, "themed icon is a white shape on transparency");

            MemoryArtSource huge = ArtFixtures.IconMasters();
            var noise = new byte[432 * 432 * 4];
            new Random(1).NextBytes(noise);
            huge.Content["icon/foreground.png"] = new Png(432, 432, noise).Encode();   // incompressible: ~730 KiB
            Refused(huge, "at most 512 KiB per file");

            Assert.Null(IconArt.Build(new MemoryArtSource(), null, null));
            Assert.Null(IconArt.Build(new MemoryArtSource(), ArtSettings.Parse(Encoding.UTF8.GetBytes(SplashJson)), null));
            // art.json copied from the example (an icon.background colour) before any icon is drawn: no icon change.
            var colourOnly = new List<string>();
            Assert.Null(IconArt.Build(new MemoryArtSource(), ArtSettings.Parse(Encoding.UTF8.GetBytes("{\"icon\": {\"background\": \"#000000\"}}")), colourOnly));
            Assert.Contains(colourOnly, w => w.Contains("icon.background is not used until the icon files exist"));
        }

        [Fact]
        public void DoubtfulArtIsAcceptedWithAWarning()
        {
            MemoryArtSource opaque = ArtFixtures.IconMasters(monochrome: true);
            opaque.Content["icon/foreground.png"] = ArtFixtures.Gradient(432).Encode();
            var mono = ArtFixtures.Disc(432, 0xFFFFFF, 0.25);
            mono.Rgba[0] = 1;   // a second colour, in a transparent pixel: ignored
            mono.Rgba[(216 * 432 + 216) * 4] = 0x80;   // a second colour inside the disc
            opaque.Content["icon/monochrome.png"] = mono.Encode();
            var warnings = new List<string>();
            IconArt.Build(opaque, null, warnings);
            foreach (string w in warnings) output.WriteLine(w);
            Assert.Contains(warnings, w => w.StartsWith("art: icon/foreground.png is fully opaque", StringComparison.Ordinal));
            Assert.Contains(warnings, w => w.StartsWith("art: icon/monochrome.png uses more than one colour", StringComparison.Ordinal));

            MemoryArtSource big = ArtFixtures.IconMasters();
            big.Content["icon/foreground.png"] = ArtFixtures.Disc(432, 0xF0C040, 0.32).Encode();   // 69 dp disc: past the 66 dp circle, inside 72 dp
            warnings.Clear();
            IconArt.Build(big, null, warnings);
            Assert.Contains(warnings, w => w.Contains("outside the 66 dp safe circle"));
            big.Content["icon/foreground.png"] = ArtFixtures.Disc(432, 0xF0C040, 0.48).Encode();
            warnings.Clear();
            IconArt.Build(big, null, warnings);
            Assert.Contains(warnings, w => w.Contains("outside the visible 72 dp square"));

            MemoryArtSource holes = ArtFixtures.IconMasters();
            holes.Content["icon/background.png"] = ArtFixtures.Disc(432, 0x204060, 0.5).Encode();
            warnings.Clear();
            IconArt.Build(holes, null, warnings);
            Assert.Contains(warnings, w => w.StartsWith("art: icon/background.png has transparent pixels", StringComparison.Ordinal));
        }

        [Fact]
        public void ArtOptionResolvesBuiltInStockAndFolders()
        {
            Assert.Null(ArtPatch.Resolve("auto", new MemoryArtSource()));
            Assert.Null(ArtPatch.Resolve(null, new MemoryArtSource()));
            MemoryArtSource builtIn = ArtFixtures.IconMasters();
            Assert.Same(builtIn, ArtPatch.Resolve("auto", builtIn));
            Assert.Null(ArtPatch.Resolve("stock", builtIn));
            Assert.Null(ArtPatch.Resolve("auto"));   // this test assembly sees no built-in art (ArtFixtures.NoBuiltInArt)

            string dir = ArtFixtures.WriteDirectory(builtIn, TestEnvironment.NewTempDir("art-dir"));
            File.WriteAllText(Path.Combine(dir, ".DS_Store"), "x");
            File.WriteAllText(Path.Combine(dir, "art.example.json"), "{}");
            ArtSource folder = ArtPatch.Resolve(dir);
            Assert.Equal(new[] { "icon/background.png", "icon/foreground.png", "icon/legacy.png" }, folder.Files);
            Assert.Equal(builtIn.Content["icon/legacy.png"], folder.Read("icon/legacy.png"));
            Assert.Throws<DirectoryNotFoundException>(() => ArtPatch.Resolve(Path.Combine(dir, "missing")));
            Assert.Throws<InvalidDataException>(() => ArtPatch.Resolve(TestEnvironment.NewTempDir("art-empty")));
        }

        [Fact]
        public void EmbeddedArtSourceMatchesTheRepositoryFolder()
        {
            // What McvsOffline.Patcher.csproj embeds is exactly art/android/ minus documentation and examples.
            string folder = Path.Combine(TestEnvironment.RepoRoot, "art", "android");
            Assert.True(File.Exists(Path.Combine(folder, "README.md")), "art/android/README.md (the artist's spec) is missing");
            var onDisk = new DirectoryArtSource(folder).Files;
            Assert.Equal(onDisk, new EmbeddedArtSource().Files);
            Assert.DoesNotContain(new EmbeddedArtSource().Files, f => f.EndsWith(".md", StringComparison.Ordinal) || f.EndsWith(".example.json", StringComparison.Ordinal));
            // The shipped example is valid art.json.
            string example = Path.Combine(folder, "art.example.json");
            Assert.True(File.Exists(example));
            Assert.NotNull(ArtSettings.Parse(File.ReadAllBytes(example)).Splash);
        }

        // ---- the whole patcher on a synthetic APK ----

        /// <summary>The icon entries and table of a 1.17.32-shaped APK, with synthetic (not Gameloft) pictures.</summary>
        private static byte[] SyntheticApk()
        {
            var entries = new List<ZipEntry>
            {
                ZipEntry.CreateDeflated("AndroidManifest.xml", AxmlWriter.Write(AxmlWriter.GameLikeManifest()), null),
                ZipEntry.CreateStored("assemblies/NicePlaySDK.dll", FixtureCompiler.Compile("NicePlaySDK", Fixtures.NicePlaySdk), null),
                ZipEntry.CreateDeflated("lib/arm64-v8a/libmonodroid.so", new byte[2000], null),
                ZipEntry.CreateStored("res/raw/args.txt", Encoding.ASCII.GetBytes(KnownBuild.RawArgsOriginal), null),
                ZipEntry.CreateDeflated(MonochromeIcon.AdaptiveIconEntry, MonochromeIcon.AdaptiveIcon(false), null),
                ZipEntry.CreateStored(MonochromeIcon.DefaultFile, ArtFixtures.Gradient(72).Encode(), null),
            };
            foreach (IconArt.StockEntry stock in IconArt.StockEntries)
                entries.Add(ZipEntry.CreateStored(stock.Name, ArtFixtures.Gradient(stock.Density.Pixels(48)).Encode(), null));
            entries.Add(ZipEntry.CreateStored("res/mipmap-hdpi-v4/iconiab.png", ArtFixtures.Gradient(10).Encode(), null));
            entries.Add(ZipEntry.CreateStored(ArtPatch.ArscEntry, SyntheticArsc.Build(), null));
            return ZipArchiveWriter.Write(entries);
        }

        private PatchReport Run(string dir, string art, bool force = true, bool dryRun = false, string name = "offline.apk")
        {
            string apk = Path.Combine(dir, "in.apk");
            if (!File.Exists(apk)) File.WriteAllBytes(apk, SyntheticApk());
            string server = Path.Combine(dir, "McvsOffline.Server.dll");
            if (!File.Exists(server)) File.WriteAllBytes(server, Fixtures.Server());
            var options = new PatcherOptions
            {
                InputApk = apk,
                ServerDll = server,
                OutputApk = Path.Combine(dir, name),
                Patches = PatcherOptions.ParsePatchList("none"),
                Keystore = Path.Combine(dir, "key.p12"),
                Signer = SignerChoice.BuiltIn,
                Art = art,
                Force = force,
                DryRun = dryRun,
                SkipServerReferenceCheck = true,
                TestOnlyHookIsMandatory = true,
            };
            return ApkPatcher.Run(options, s => output.WriteLine(s));
        }

        /// <summary>Every entry of an APK except the signature files: name, method, stored bytes.</summary>
        private static List<(string, ushort, byte[])> Content(string apk)
        {
            return ZipArchiveReader.Read(apk).Entries.Where(e => !BuiltInSigner.IsSignatureFile(e.Name)).Select(e => (e.Name, e.Method, e.RawData)).ToList();
        }

        [Fact]
        public void NoArtGivesTheSameApkAsStock()
        {
            string dir = TestEnvironment.NewTempDir("art-none");
            PatchReport stock = Run(dir, "stock", name: "stock.apk");
            PatchReport auto = Run(dir, "auto", name: "auto.apk");
            Assert.DoesNotContain(stock.Warnings, w => w.StartsWith("art:", StringComparison.Ordinal));
            Assert.Contains(auto.Warnings, w => w.StartsWith("art: no project art built in", StringComparison.Ordinal));
            Assert.DoesNotContain(auto.Notes, n => n.StartsWith("art:", StringComparison.Ordinal));
            var a = Content(Path.Combine(dir, "stock.apk"));
            var b = Content(Path.Combine(dir, "auto.apk"));
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].Item1, b[i].Item1);
                Assert.Equal(a[i].Item2, b[i].Item2);
                Assert.True(a[i].Item3.AsSpan().SequenceEqual(b[i].Item3), a[i].Item1);
            }
            Assert.DoesNotContain(ArtPatch.ArscEntry, auto.Modified);
            Assert.DoesNotContain(auto.Modified, m => m.StartsWith("res/", StringComparison.Ordinal) && m != KnownBuild.RawArgsEntry);
        }

        [Fact]
        public void ArtReplacesTheIconEntriesInPlaceAndAddsTheThemedIcon()
        {
            string dir = TestEnvironment.NewTempDir("art-full");
            MemoryArtSource art = ArtFixtures.IconMasters(monochrome: true, artJson: SplashJson);
            string artDir = ArtFixtures.WriteDirectory(art, Path.Combine(dir, "art"));
            PatchReport report = Run(dir, artDir);
            foreach (string n in report.Notes.Where(n => n.StartsWith("art", StringComparison.Ordinal))) output.WriteLine(n);

            // Synthetic stand-ins are not the shipped files: --force replaces them, with a note per entry.
            Assert.Equal(IconArt.StockEntries.Count + 2, report.Warnings.Count(w => w.Contains("is not the shipped file") && w.EndsWith("(--force)", StringComparison.Ordinal)));
            foreach (IconArt.StockEntry stock in IconArt.StockEntries)
            {
                Assert.Contains(stock.Name, report.Modified);
                Assert.Single(report.Notes, n => n.StartsWith("art: " + stock.Name + ": ", StringComparison.Ordinal));
            }
            Assert.Contains(ArtPatch.ArscEntry, report.Modified);
            Assert.Contains(MonochromeIcon.AdaptiveIconEntry, report.Modified);
            Assert.Contains(MonochromeIcon.DefaultFile, report.Modified);
            foreach (IconDensity d in IconDensity.All) Assert.Contains(MonochromeIcon.DensityFile(d), report.Added);
            Assert.Contains(report.Notes, n => n.StartsWith("art: resources.arsc: style 0x7f0b017e Theme.acp.notitlebar.fullscreen", StringComparison.Ordinal) && n.Contains("+night-v31"));
            Assert.Contains(report.Notes, n => n.StartsWith("art: resources.arsc: mipmap 0x7f030004", StringComparison.Ordinal));

            ZipArchiveReader.Archive input = ZipArchiveReader.Read(Path.Combine(dir, "in.apk"));
            ZipArchiveReader.Archive patched = ZipArchiveReader.Read(Path.Combine(dir, "offline.apk"));
            var exports = IconArt.Build(art, ArtSettings.Parse(art.Content["art.json"]), null);
            foreach (IconArt.StockEntry stock in IconArt.StockEntries)
            {
                ZipEntry e = patched.Find(stock.Name);
                Assert.Equal(ZipEntry.MethodStored, e.Method);
                Png image = Png.Decode(e.GetData());
                Assert.Equal(IconArt.Size(stock.Layer, stock.Density), image.Width);
                Assert.Equal(exports[(stock.Layer, stock.Density)].Data, e.GetData());
            }
            // res/mipmap/ic_launcher.png (no density) gets the mdpi export, not the hdpi one Gameloft put there.
            Assert.Equal(48, Png.Decode(patched.Find("res/mipmap/ic_launcher.png").GetData()).Width);
            Assert.Equal(ZipEntry.MethodStored, patched.Find(ArtPatch.ArscEntry).Method);
            Assert.Equal(ZipEntry.MethodDeflated, patched.Find(MonochromeIcon.AdaptiveIconEntry).Method);
            Assert.Equal(MonochromeIcon.AdaptiveIcon(true), patched.Find(MonochromeIcon.AdaptiveIconEntry).GetData());
            Assert.Equal(108, Png.Decode(patched.Find(MonochromeIcon.DefaultFile).GetData()).Width);
            // Each density's themed layer comes right after that density's foreground.
            var names = patched.Entries.Select(e => e.Name).ToList();
            foreach (IconDensity d in IconDensity.All)
            {
                Assert.Equal(names.IndexOf(MonochromeIcon.AnchorEntry(d)) + 1, names.IndexOf(MonochromeIcon.DensityFile(d)));
                Assert.Equal(IconArt.Size(IconLayer.Monochrome, d), Png.Decode(patched.Find(MonochromeIcon.DensityFile(d)).GetData()).Width);
            }
            // Everything else is byte-identical (ApkPatcher.VerifyUnchanged checked it too).
            var changed = new HashSet<string>(report.Modified, StringComparer.Ordinal);
            foreach (ZipEntry e in input.Entries.Where(e => !changed.Contains(e.Name)))
                Assert.True(e.RawData.AsSpan().SequenceEqual(patched.Find(e.Name).RawData), e.Name);
            Assert.True(patched.Find("res/mipmap-hdpi-v4/iconiab.png") != null);
        }

        [Fact]
        public void SplashOnlyLeavesTheIconAlone()
        {
            string dir = TestEnvironment.NewTempDir("art-splash");
            var art = new MemoryArtSource();
            art.Content["art.json"] = Encoding.UTF8.GetBytes("{\"splash\": {\"background\": \"#000000\"}}");
            PatchReport report = Run(dir, ArtFixtures.WriteDirectory(art, Path.Combine(dir, "art")));
            Assert.Equal(new[] { ArtPatch.ArscEntry }, report.Modified.Where(m => m.StartsWith("res", StringComparison.Ordinal) && m != KnownBuild.RawArgsEntry).ToArray());
            Assert.Contains(report.Notes, n => n.Contains("+v31 (windowSplashScreenBackground #000000)") && !n.Contains("night"));
            ArscPackage package = ArscTable.Parse(ZipArchiveReader.Read(Path.Combine(dir, "offline.apk")).Find(ArtPatch.ArscEntry).GetData()).Package(0x7f);
            Assert.Equal("v31", ArscConfig.Describe(package.Types(0x0b).Last().Config));
        }

        [Fact]
        public void ArtIsRefusedOnEntriesThatAreNotTheShippedOnesUnlessForced()
        {
            string dir = TestEnvironment.NewTempDir("art-refuse");
            string artDir = ArtFixtures.WriteDirectory(ArtFixtures.IconMasters(), Path.Combine(dir, "art"));
            // (The whole patcher already refuses this synthetic APK without --force, at its assembly MVIDs.)
            var ex = Assert.Throws<InvalidDataException>(() => ArtPatch.Prepare(ZipArchiveReader.Read(SyntheticApk()), new PatcherOptions { Art = artDir }, new PatchReport(), null));
            Assert.Contains("res/mipmap/ic_launcher.png is not the shipped file", ex.Message);
            Assert.Contains("--art stock", ex.Message);

            // A dry run checks everything and writes nothing.
            PatchReport dry = Run(dir, artDir, dryRun: true);
            Assert.Equal(IconArt.StockEntries.Count, dry.Notes.Count(n => n.StartsWith("art: res/", StringComparison.Ordinal)));
            Assert.False(File.Exists(Path.Combine(dir, "offline.apk")));
        }
    }
}
