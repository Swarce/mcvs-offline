using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using McvsOffline.Tools.HdTextures;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.HdTextures
{
    /// <summary>
    /// The converter on synthetic files (docs/design/hd-textures.md): one texture in both Android layouts, the texture table,
    /// and a whole pack (zip shape, manifest contract, lightmap groups, the ASTC refusal, the command line).
    /// </summary>
    public class HdPackTests
    {
        private readonly ITestOutputHelper output;

        public HdPackTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        // ------------------------------------------------------------------ one texture

        [Fact]
        public void ABareKtxBecomesTheSameKindOfFileAtThePcSize()
        {
            RgbaImage pc = HdFixtures.Pattern(64, 64, 1, true);
            byte[] android = HdFixtures.AndroidKtxFile(pc.HalfSize(), 6);   // 32x32, 32 ... 1
            ConversionResult r = TextureConverter.Convert(android, HdFixtures.Dds(pc, false), 2);
            output.WriteLine("PSNR " + r.Psnr.ToString("0.0", CultureInfo.InvariantCulture) + " dB, " + android.Length + " -> " + r.File.Length + " bytes");
            Assert.Equal("ktx", r.Layout);
            Assert.True(Gcbf.IsGcbf(r.File));
            Ktx ktx = Ktx.Parse(Gcbf.Decode(r.File, true));
            Ktx original = Ktx.Parse(Gcbf.Decode(android, true));
            Assert.Equal(original.Header.Take(6), ktx.Header.Take(6));          // endianness, glType ... glBaseInternalFormat
            Assert.Equal(new uint[] { 64, 64, 0, 0, 1, 7 }, ktx.Header.Skip(6).Take(6));
            Assert.Equal(HdFixtures.PackKeyValues, ktx.KeyValueData);
            Assert.Equal(7, r.MipLevels);
            for (int level = 0; level < 7; level++)
                Assert.Equal(Etc1.DataSize(Ktx.MipSize(64, level), Ktx.MipSize(64, level)), ktx.Levels[level].Length);
            Assert.Equal(32, r.OriginalWidth);
            Assert.Equal(6, r.OriginalMipLevels);
            Assert.True(r.Psnr > 28, "PSNR " + r.Psnr);
            Assert.True(double.IsNaN(r.AlphaPsnr));

            // A raw (unwrapped) original gets a raw replacement; a chain that stops early stops at the same size.
            byte[] raw = HdFixtures.EtcKtx(pc.HalfSize(), 3).Write();
            ConversionResult short3 = TextureConverter.Convert(raw, HdFixtures.Dds(pc, false, 2), 1);
            Assert.False(Gcbf.IsGcbf(short3.File));
            Ktx s = Ktx.Parse(short3.File);
            Assert.Equal(4, s.MipLevels);                                         // 64, 32, 16, 8 like 32, 16, 8
            Assert.Equal(8, Ktx.MipSize(s.Width, 3));
        }

        [Fact]
        public void ASplitAlphaZipKeepsItsLayoutAndCarriesThePcAlpha()
        {
            RgbaImage pc = HdFixtures.Pattern(64, 32, 2, false);
            byte[] android = HdFixtures.AndroidSplitFile(pc.HalfSize(), 6);
            ConversionResult r = TextureConverter.Convert(android, HdFixtures.Dds(pc, true), 2);
            output.WriteLine("PSNR " + r.Psnr.ToString("0.0", CultureInfo.InvariantCulture) + " dB, alpha " + r.AlphaPsnr.ToString("0.0", CultureInfo.InvariantCulture) + " dB");
            Assert.Equal("split", r.Layout);
            AndroidTexture back = AndroidTexture.Parse(r.File);
            Assert.Equal(new[] { "SPLIT", "rgb.ktx", "alpha.ktx" }, back.Entries.Select(e => e.Key));
            Assert.Equal(new byte[4], back.Entry("SPLIT"));
            Assert.True(back.Wrapped);
            Assert.True(Gcbf.IsGcbf(back.Entry("rgb.ktx")) && Gcbf.IsGcbf(back.Entry("alpha.ktx")));
            Assert.Equal(64, back.Rgb.Width);
            Assert.Equal(32, back.Alpha.Height);
            Assert.Equal(7, back.Alpha.MipLevels);
            Assert.Equal(HdFixtures.PackKeyValues, back.Alpha.KeyValueData);
            // The zip is laid out like Gameloft's: no extra fields, SPLIT's data at offset 35.
            Assert.Equal(new byte[4], r.File.Skip(35).Take(4));
            Assert.Equal(0, r.File[28] | r.File[29]);
            // Alpha is grey (R = G = B) and follows the PC's DXT5 alpha.
            RgbaImage alpha = Etc1.Decode(back.Alpha.Levels[0], 0, 64, 32);
            for (int i = 0; i < alpha.Pixels.Length; i += 4)
            {
                Assert.Equal(alpha.Pixels[i], alpha.Pixels[i + 1]);
                Assert.Equal(alpha.Pixels[i], alpha.Pixels[i + 2]);
            }
            Assert.True(r.AlphaPsnr > 35, "alpha PSNR " + r.AlphaPsnr);
            Assert.True(r.Psnr > 28, "PSNR " + r.Psnr);
        }

        [Fact]
        public void Dxt1UnderAnOpaqueSplitTextureGivesAWhiteAlpha()
        {
            RgbaImage pc = HdFixtures.Pattern(32, 32, 3, true);
            byte[] android = HdFixtures.AndroidSplitFile(pc.HalfSize(), 5);
            ConversionResult r = TextureConverter.Convert(android, HdFixtures.Dds(pc, false), 1);
            AndroidTexture back = AndroidTexture.Parse(r.File);
            foreach (byte[] level in back.Alpha.Levels)
            {
                RgbaImage a = Etc1.Decode(level, 0, 4, 4);
                Assert.All(Enumerable.Range(0, 16), i => Assert.Equal(255, a.Pixels[i * 4]));
            }
            Assert.Equal(double.PositiveInfinity, r.AlphaPsnr);
        }

        [Fact]
        public void WhatCannotBeConvertedIsRefusedWithAReason()
        {
            RgbaImage pc = HdFixtures.Pattern(32, 32, 4, true);
            byte[] dds = HdFixtures.Dds(pc, false);
            var astc = new Ktx { Header = new uint[] { 0x04030201, 0, 1, 0, Ktx.GlAstc6x6, 0x1908, 16, 16, 0, 0, 1, 1, 0 } };
            astc.Levels.Add(new byte[9 * 16]);
            Assert.Contains("not ETC1", Assert.Throws<InvalidDataException>(() => TextureConverter.Convert(GcbfWriter.Encode(astc.Write()), dds, 1)).Message);
            Assert.Contains("not a larger power-of-two multiple",
                            Assert.Throws<InvalidDataException>(() => TextureConverter.Convert(HdFixtures.AndroidKtxFile(pc, 1), dds, 1)).Message);
            Assert.Contains("not a larger power-of-two multiple",
                            Assert.Throws<InvalidDataException>(() => TextureConverter.Convert(HdFixtures.AndroidKtxFile(HdFixtures.Pattern(16, 8, 1, true), 1), dds, 1)).Message);
            byte[] noAlpha = StoredZipWriter.Build(false, new[] { new KeyValuePair<string, byte[]>("SPLIT", new byte[4]) });
            Assert.Contains("without SPLIT, rgb.ktx and alpha.ktx", Assert.Throws<InvalidDataException>(() => TextureConverter.Convert(noAlpha, dds, 1)).Message);
        }

        // ------------------------------------------------------------------ the table

        [Fact]
        public void TheBuiltInTableHoldsTheResearchScopeAndNoNames()
        {
            HdIndex index = HdIndex.Load();
            Assert.Equal(new[] { "Bootcamp", "Desert", "DogDays", "Favelas", "Port", "Rooftop", "Snow", "Stadium", "Stadium2" }, index.Levels.Select(l => l.Name));
            Assert.Equal("Map01", index.Levels.Single(l => l.Name == "Port").Alias);
            var counts = HdIndex.AllCategories.ToDictionary(c => c, c => index.Entries.Count(e => e.Category == c));
            output.WriteLine(string.Join(", ", counts.Select(kv => kv.Key + " " + kv.Value)));
            // pc-textures.md §3.2 / §7: 953 environment, 63 lightmaps, 705 third-person character, 33 equipment textures.
            Assert.Equal(953, counts[HdIndex.Surfaces]);
            Assert.Equal(63, counts[HdIndex.Lightmaps]);
            Assert.Equal(705, counts[HdIndex.Characters]);
            Assert.Equal(33, counts[HdIndex.Equipment]);
            Assert.Equal(1754, index.Select(HdIndex.DefaultCategories, null).Count);
            Assert.Equal(index.Entries.Count, index.Entries.Select(e => e.Hash).Distinct().Count());
            foreach (HdIndexEntry e in index.Entries)
            {
                Assert.Matches("^[0-9a-f]{8}$", e.Hash);
                Assert.True(e.Layout == "ktx" || e.Layout == "split");
                Assert.True(e.PcWidth == e.AndroidWidth << e.Doublings && e.PcHeight == e.AndroidHeight << e.Doublings && e.Doublings >= 1, e.Hash);
                if (e.Category == HdIndex.Lightmaps)
                {
                    Assert.Single(e.Levels);
                    Assert.Equal("lightmaps:" + e.Levels[0], e.Group);
                }
                else
                {
                    Assert.Null(e.Group);
                }
            }
            // Every level with lightmaps has all three kinds per lightmap, and the file holds hashes, not paths.
            Assert.All(index.Levels.Where(l => l.Name != "Bootcamp"), l => Assert.True(index.Entries.Count(e => e.Group == "lightmaps:" + l.Name) % 3 == 0, l.Name));
            string text;
            using (Stream s = typeof(HdIndex).Assembly.GetManifestResourceStream(HdIndex.ResourceName))
            using (var reader = new StreamReader(s))
                text = reader.ReadToEnd();
            foreach (string line in text.Split('\n').Where(l => l.StartsWith("tex\t", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain("/", line);
                Assert.DoesNotContain(".tga", line);
            }
            // Its own file in the repository is what the tool carries.
            Assert.Equal(File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot, "tools", "hd-textures", "hd-index.tsv")), text);
        }

        [Fact]
        public void TableRowsNamesScopesAndMaps()
        {
            var e = new HdIndexEntry { Hash = "0019921a" };
            Assert.Equal("1/9921a.f", e.AndroidPath);
            Assert.Equal("1675802.f", e.PcPath);
            Assert.Equal(DataPack.HashedPath("3d/Equipment/Grenade_Frag/Textures/Grenade_Frag_rom.tga"), e.AndroidPath);

            string level;
            Assert.Equal(HdIndex.Lightmaps, HdIndex.Categorize("Baked/Levels/Port/Port.Lightmap_0.dirirr.tga", out level));
            Assert.Equal("Port", level);
            Assert.Equal(HdIndex.Lightmaps, HdIndex.Categorize("Baked/Levels/Port/Port.Lightmap_12.lowend.tga", out level));
            Assert.Null(HdIndex.Categorize("Baked/Levels/Port/Port.IBLGlobalProbe.IBL.dds", out level));
            Assert.Null(HdIndex.Categorize("Baked/Levels/Port/Desert.Lightmap_0.tga", out level));
            Assert.Equal(HdIndex.Minimaps, HdIndex.Categorize("Levels/Port/Port_Multi.minimap.png", out level));
            Assert.Equal("Port", level);
            Assert.Equal(HdIndex.Surfaces, HdIndex.Categorize("3d/Environment/01_WorkTextures/ENV_GenericConcrete05_bc.tga", out level));
            Assert.Null(level);
            Assert.Equal(HdIndex.Characters, HdIndex.Categorize("3d/Characters/Male/Reco/RCc/TP/Textures/RCc_body_tint.tga", out level));
            Assert.Null(HdIndex.Categorize("3d/Characters/Male/Reco/RCc/FP/Textures/RCc_arms_bc.tga", out level));
            Assert.Equal(HdIndex.Equipment, HdIndex.Categorize("3d/Equipment/Grenade_Frag/Textures/Grenade_Frag_rom.tga", out level));
            Assert.Null(HdIndex.Categorize("3d/Weapons/AR01/Textures/AR01_bc.tga", out level));
            Assert.Null(HdIndex.Categorize("Menus/assets/png/mercs/Ronin_skin_02.png", out level));
            Assert.Equal(HdIndex.Props, HdIndex.Categorize("3d/Vehicles/CargoShip/Textures/CargoShip_rom.tga", out level));
            Assert.Equal(HdIndex.Vfx, HdIndex.Categorize("3d/VFX/Perlin4.tga", out level));
            Assert.Equal(HdIndex.Menus3d, HdIndex.Categorize("3d/Menus/Gold_Coin/Textures/gold_coin_bc.tga", out level));

            Assert.Equal(HdIndex.DefaultCategories, HdIndex.ParseScope(null));
            Assert.Equal(HdIndex.AllCategories, HdIndex.ParseScope("ALL"));
            Assert.Equal(new[] { "lightmaps", "equipment" }, HdIndex.ParseScope("equipment,Lightmaps"));
            Assert.Equal("equipment+lightmaps".Split('+').OrderBy(x => Array.IndexOf(HdIndex.AllCategories, x)), HdIndex.ParseScope("equipment+lightmaps"));
            Assert.Throws<ArgumentException>(() => HdIndex.ParseScope("weapons"));
            Assert.Equal("default", HdIndex.ScopeName(HdIndex.DefaultCategories));
            Assert.Equal("lightmaps+equipment", HdIndex.ScopeName(new[] { "lightmaps", "equipment" }));
            Assert.Equal("mcvs-hd-default-Port.zip", PackBuilder.DefaultFileName(HdIndex.DefaultCategories, new[] { "Port" }));
            Assert.Equal("mcvs-hd-all.zip", PackBuilder.DefaultFileName(HdIndex.AllCategories, null));

            HdIndex index = HdIndex.Load();
            Assert.Null(index.ParseMaps("all"));
            Assert.Equal(new[] { "Desert", "Port" }, index.ParseMaps("map01,Desert"));
            Assert.Contains("Port = Map01", Assert.Throws<ArgumentException>(() => index.ParseMaps("Moon")).Message);

            // Maps narrow surfaces, lightmaps and minimaps only.
            List<HdIndexEntry> port = index.Select(HdIndex.AllCategories, new[] { "Port" });
            Assert.All(port.Where(x => x.Category == HdIndex.Surfaces || x.Category == HdIndex.Lightmaps || x.Category == HdIndex.Minimaps),
                       x => Assert.Contains("Port", x.Levels));
            Assert.Equal(9, port.Count(x => x.Category == HdIndex.Lightmaps));
            Assert.Equal(705, port.Count(x => x.Category == HdIndex.Characters));
            Assert.True(port.Count(x => x.Category == HdIndex.Surfaces) > 100);

            // Write and parse back.
            HdIndex again = HdIndex.Parse(index.Write(new[] { "test" }));
            Assert.Equal(index.Entries.Count, again.Entries.Count);
            Assert.Equal(index.Write(new[] { "test" }), again.Write(new[] { "test" }));
            Assert.Throws<InvalidDataException>(() => HdIndex.Parse("tex\t00\tbad"));
        }

        // ------------------------------------------------------------------ the pack

        private sealed class Fixture
        {
            public string Root, Android, Pc;
            public HdIndex Index = new HdIndex();
            public Dictionary<string, byte[]> Originals = new Dictionary<string, byte[]>();

            public HdIndexEntry Add(string logicalPath, string category, string[] levels, bool split, int size, bool pcFile = true, int seed = 1)
            {
                string hash = Crc32.Compute(Encoding.UTF8.GetBytes(logicalPath)).ToString("x8", CultureInfo.InvariantCulture);
                RgbaImage pc = HdFixtures.Pattern(size, size, seed, !split);
                int levelsAndroid = (int)Math.Log2(size / 2) + 1;
                byte[] android = split ? HdFixtures.AndroidSplitFile(pc.HalfSize(), levelsAndroid) : HdFixtures.AndroidKtxFile(pc.HalfSize(), levelsAndroid);
                byte[] dds = GcbfWriter.Encode(HdFixtures.Dds(pc, split));
                var e = new HdIndexEntry
                {
                    Hash = hash, Category = category, Levels = levels, Layout = split ? "split" : "ktx",
                    AndroidSize = android.Length, AndroidWidth = size / 2, AndroidHeight = size / 2, AndroidMips = levelsAndroid,
                    PcSize = dds.Length, PcWidth = size, PcHeight = size, PcMips = levelsAndroid + 1,
                };
                string a = Path.Combine(Android, e.AndroidPath);
                Directory.CreateDirectory(Path.GetDirectoryName(a));
                File.WriteAllBytes(a, android);
                Originals[e.AndroidPath] = android;
                if (pcFile) File.WriteAllBytes(Path.Combine(Pc, e.PcPath), dds);
                Index.Entries.Add(e);
                return e;
            }
        }

        private static Fixture NewFixture(string tag)
        {
            var f = new Fixture { Root = TestEnvironment.NewTempDir(tag) };
            f.Android = Path.Combine(f.Root, "android", "Data");
            f.Pc = Path.Combine(f.Root, "pc", "Data");
            Directory.CreateDirectory(f.Android);
            Directory.CreateDirectory(f.Pc);
            File.WriteAllText(Path.Combine(f.Android, "MCVS.game"), "{}");
            f.Index.Levels.Add(new HdLevel { Name = "Testmap", Alias = "Map42" });
            f.Index.Levels.Add(new HdLevel { Name = "Othermap", Alias = null });
            f.Add("3d/Environment/01_WorkTextures/TEST_Concrete_bc.tga", HdIndex.Surfaces, new[] { "Testmap" }, false, 64);
            f.Add("3d/Environment/01_WorkTextures/TEST_Other_bc.tga", HdIndex.Surfaces, new[] { "Othermap" }, false, 32, seed: 2);
            f.Add("3d/Characters/Male/Test/TP/Textures/Test_body_bc.tga", HdIndex.Characters, new string[0], true, 64, seed: 3);
            f.Add("Baked/Levels/Testmap/Testmap.Lightmap_0.tga", HdIndex.Lightmaps, new[] { "Testmap" }, true, 32, seed: 4);
            f.Add("Baked/Levels/Testmap/Testmap.Lightmap_0.lowend.tga", HdIndex.Lightmaps, new[] { "Testmap" }, false, 32, seed: 5);
            f.Add("Baked/Levels/Othermap/Othermap.Lightmap_0.tga", HdIndex.Lightmaps, new[] { "Othermap" }, true, 32, seed: 6);
            f.Add("Baked/Levels/Othermap/Othermap.Lightmap_0.lowend.tga", HdIndex.Lightmaps, new[] { "Othermap" }, false, 32, pcFile: false, seed: 7);
            return f;
        }

        [Fact]
        public void APackHoldsTheReplacementsAndTheManifestOfTheContract()
        {
            Fixture f = NewFixture("hdpack");
            string zipPath = Path.Combine(f.Root, "out", "pack.zip");
            var log = new StringWriter();
            BuildSummary summary = PackBuilder.Build(new BuildOptions
            {
                PcData = f.Pc, AndroidData = f.Android, Categories = HdIndex.DefaultCategories, Output = zipPath, Threads = 2, Index = f.Index,
                NowUtc = new DateTime(2026, 10, 28, 20, 0, 0, DateTimeKind.Utc),
            }, log);
            output.WriteLine(log.ToString());

            // Othermap's lowend lightmap has no PC file: its whole group stays out, the rest goes in.
            Assert.Equal(5, summary.Files);
            Assert.Single(summary.Skipped);
            Assert.StartsWith("lightmaps:Othermap (2 files):", summary.Skipped[0]);
            Assert.Contains("no " + f.Index.Entries[6].PcPath + " in the PC Data folder", summary.Skipped[0]);
            Assert.False(File.Exists(zipPath + ".partial"));

            byte[] zipBytes = File.ReadAllBytes(zipPath);
            Assert.Equal(zipBytes.Length, summary.PackBytes);
            using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            {
                Assert.Equal("mcvs-hd.json", zip.Entries.Last().FullName);
                Assert.All(zip.Entries, e => Assert.Equal(e.Length, e.CompressedLength));
                var manifest = MiniJson.ParseObject(Encoding.UTF8.GetString(Read(zip.Entries.Last())));
                Assert.Equal(1L, manifest["format"]);
                Assert.Equal("hd-textures", manifest["kind"]);
                Assert.Equal("2026-10-28T20:00:00Z", manifest["created"]);
                Assert.Equal("1.17.32", manifest["gameVersion"]);
                Assert.Equal("ETC", manifest["pack"]);
                Assert.Equal("default", manifest["scope"]);
                Assert.Equal(new object[] { "surfaces", "lightmaps", "characters", "equipment" }, (List<object>)manifest["categories"]);
                Assert.Equal(new object[] { "Testmap", "Othermap" }, (List<object>)manifest["maps"]);
                var files = ((List<object>)manifest["files"]).Cast<Dictionary<string, object>>().ToList();
                Assert.Equal(5, files.Count);
                Assert.Equal(files.Select(x => (string)x["entry"]), zip.Entries.Take(5).Select(e => e.FullName));
                foreach (Dictionary<string, object> file in files)
                {
                    string entry = (string)file["entry"];
                    Assert.StartsWith("Data/", entry);
                    string androidPath = entry.Substring(5);
                    byte[] replacement = Read(zip.GetEntry(entry));
                    var original = (Dictionary<string, object>)file["original"];
                    var repl = (Dictionary<string, object>)file["replacement"];
                    Assert.Equal((long)f.Originals[androidPath].Length, original["size"]);
                    Assert.Equal(PackBuilder.Hex(Crc32.Compute(f.Originals[androidPath])), original["crc32"]);
                    Assert.Equal((long)replacement.Length, repl["size"]);
                    Assert.Equal(PackBuilder.Hex(Crc32.Compute(replacement)), repl["crc32"]);
                    Assert.Equal((long)original["width"] * 2, repl["width"]);
                    Assert.Equal((long)original["mipLevels"] + 1, repl["mipLevels"]);
                    HdIndexEntry e = f.Index.Entries.Single(x => x.AndroidPath == androidPath);
                    Assert.Equal(e.Hash, file["hash"]);
                    Assert.Equal(e.Category, file["category"]);
                    Assert.Equal(e.Levels, ((List<object>)file["levels"]).Cast<string>());
                    Assert.Equal(e.Group, file["group"]);
                    Assert.Equal(e.Layout, file["layout"]);
                    Assert.IsType<double>(file["psnr"]);
                    if (e.Layout == "split") Assert.IsType<double>(file["alphaPsnr"]);
                    else Assert.Null(file["alphaPsnr"]);
                    // The replacement is what the Android original is, at the PC size.
                    Assert.Equal(e.Layout, AndroidTexture.Parse(replacement).Layout);
                }
                // Lightmaps are named (built from the level and checked against the hash); other textures are not.
                Assert.Equal("Baked/Levels/Testmap/Testmap.Lightmap_0.tga", files.Single(x => (string)x["hash"] == f.Index.Entries[3].Hash)["logicalPath"]);
                Assert.Null(files.Single(x => (string)x["hash"] == f.Index.Entries[0].Hash)["logicalPath"]);
                var totals = (Dictionary<string, object>)manifest["totals"];
                Assert.Equal(5L, totals["files"]);
                Assert.Equal(files.Sum(x => (long)((Dictionary<string, object>)x["replacement"])["size"]), totals["replacementBytes"]);
            }
            // Entry data is 4-byte aligned.
            int p = 0;
            while (Ktx.U32(zipBytes, p) == 0x04034b50)
            {
                int n = zipBytes[p + 26] | (zipBytes[p + 27] << 8), x = zipBytes[p + 28] | (zipBytes[p + 29] << 8);
                Assert.Equal(0, (p + 30 + n + x) % 4);
                p += 30 + n + x + (int)Ktx.U32(zipBytes, p + 18);
            }

            // Maps: only Testmap's surface and lightmaps, plus the level-independent character.
            string portZip = Path.Combine(f.Root, "out2");
            BuildSummary narrow = PackBuilder.Build(new BuildOptions
            {
                PcData = f.Pc, AndroidData = f.Android, Categories = HdIndex.DefaultCategories, Maps = new[] { "Testmap" }, Output = portZip + "/", Threads = 1,
                Index = f.Index,
            }, new StringWriter());
            Assert.Equal(Path.Combine(portZip, "mcvs-hd-default-Testmap.zip"), narrow.PackPath);
            Assert.Equal(4, narrow.Files);
            Assert.Empty(narrow.Skipped);
            HdFixtures.DeleteDir(f.Root);
        }

        [Fact]
        public void TheAstcPackAndEmptyScopesAreRefused()
        {
            Fixture f = NewFixture("hdastc");
            HdIndexEntry first = f.Index.Entries[0];
            var astc = new Ktx { Header = new uint[] { 0x04030201, 0, 1, 0, Ktx.GlAstc6x6, 0x1908, 32, 32, 0, 0, 1, 1, 0 } };
            astc.Levels.Add(new byte[36 * 16]);
            File.WriteAllBytes(Path.Combine(f.Android, first.AndroidPath), GcbfWriter.Encode(astc.Write()));
            var options = new BuildOptions { PcData = f.Pc, AndroidData = f.Android, Output = Path.Combine(f.Root, "x.zip"), Index = f.Index };
            Assert.Contains("is the ASTC pack", Assert.Throws<InvalidDataException>(() => PackBuilder.Build(options, new StringWriter())).Message);
            options.Categories = new[] { HdIndex.Vfx };
            Assert.Contains("nothing to convert", Assert.Throws<ArgumentException>(() => PackBuilder.Build(options, new StringWriter())).Message);
            HdFixtures.DeleteDir(f.Root);
        }

        [Fact]
        public void TheCommandLineChecksItsArguments()
        {
            var console = new StringWriter();
            Assert.Equal(0, McvsOffline.Tools.HdTextures.Program.Run(new[] { "--help" }, console));
            Assert.Contains("mcvs-hd-textures build --android-data", console.ToString());
            Assert.Equal(2, McvsOffline.Tools.HdTextures.Program.Run(new string[0], new StringWriter()));
            Assert.Contains("unknown command 'probe'", Assert.Throws<ArgumentException>(() => McvsOffline.Tools.HdTextures.Program.Run(new[] { "probe" }, new StringWriter())).Message);
            Assert.Contains("unknown option", Assert.Throws<ArgumentException>(() => McvsOffline.Tools.HdTextures.Program.Run(new[] { "build", "--raw" }, new StringWriter())).Message);
            Assert.Throws<ArgumentException>(() => McvsOffline.Tools.HdTextures.Program.Run(new[] { "build", "--scope", "weapons", "--dry-run" }, new StringWriter()));
            Assert.Throws<ArgumentException>(() => McvsOffline.Tools.HdTextures.Program.Run(new[] { "build", "--maps", "Moon", "--dry-run" }, new StringWriter()));
            Assert.Contains("--android-data", Assert.Throws<ArgumentException>(() => McvsOffline.Tools.HdTextures.Program.Run(new[] { "build", "--pc", "/nowhere" }, new StringWriter())).Message);
            console = new StringWriter();
            Assert.Equal(0, McvsOffline.Tools.HdTextures.Program.Run(new[] { "build", "--dry-run", "--maps", "Port" }, console));
            output.WriteLine(console.ToString());
            Assert.Contains("lightmaps      9 files", console.ToString());
            Assert.Contains("characters   705 files", console.ToString());

            string root = TestEnvironment.NewTempDir("hdargs");
            Assert.Contains("not the PC game folder", Assert.Throws<ArgumentException>(() => McvsOffline.Tools.HdTextures.Program.ResolvePcData(root, new StringWriter())).Message);
            Directory.CreateDirectory(Path.Combine(root, "Data"));
            File.WriteAllBytes(Path.Combine(root, "Data", "1675802.f"), new byte[1]);
            Assert.Equal(Path.Combine(root, "Data"), McvsOffline.Tools.HdTextures.Program.ResolvePcData(root, new StringWriter()));
            Assert.Equal(Path.Combine(root, "Data"), McvsOffline.Tools.HdTextures.Program.ResolvePcData(Path.Combine(root, "Data"), new StringWriter()));
            Directory.CreateDirectory(Path.Combine(root, "files", "Data", "1"));
            Assert.Equal(Path.Combine(root, "files", "Data"), McvsOffline.Tools.HdTextures.Program.ResolveAndroidData(Path.Combine(root, "files")));
            Assert.Throws<ArgumentException>(() => McvsOffline.Tools.HdTextures.Program.ResolveAndroidData(root));
        }

        private static byte[] Read(ZipArchiveEntry e)
        {
            using (Stream s = e.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }
}
