using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// The converter on the real files (MCVS_PC_INSTALL: the Steam build; MCVS_DATAPACK: the 1.17.32 ETC pack): the table
    /// against both, a handful of real conversions (a map surface, lightmaps, a character with alpha) with their PSNR and the
    /// phone's own, and an estimate of the default scope's encode time on this machine. Skipped without the files.
    /// </summary>
    public class HdRealDataTests
    {
        private const string Surface = "08dbd2f1";          // 3d/Environment/01_WorkTextures/ENV_GenericConcrete05_bc.tga (Data/8/dbd2f1.f)
        private const string Equipment = "0019921a";        // 3d/Equipment/Grenade_Frag/Textures/Grenade_Frag_rom.tga, 1024 -> 2048
        private const string PortLightmap1 = "94a41656";    // Baked/Levels/Port/Port.Lightmap_1.tga, split, 64 -> 128
        private const string PortLowend1 = "0ec89eb2";      // Baked/Levels/Port/Port.Lightmap_1.lowend.tga, ktx with mips

        private readonly ITestOutputHelper output;

        public HdRealDataTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void TheTableMatchesThePackAndThePcInstall()
        {
            string pc = HdFixtures.PcData(output), android = HdFixtures.AndroidData(output);
            if (pc == null || android == null) return;
            HdIndex index = HdIndex.Load();
            var wrong = new List<string>();
            foreach (HdIndexEntry e in index.Entries)
            {
                var a = new FileInfo(Path.Combine(android, e.AndroidPath));
                var p = new FileInfo(Path.Combine(pc, e.PcPath));
                if (!a.Exists || a.Length != e.AndroidSize) wrong.Add(e.AndroidPath + " android " + (a.Exists ? a.Length.ToString(CultureInfo.InvariantCulture) : "missing"));
                if (!p.Exists || p.Length != e.PcSize) wrong.Add(e.PcPath + " pc " + (p.Exists ? p.Length.ToString(CultureInfo.InvariantCulture) : "missing"));
            }
            Assert.Empty(wrong);
            // The hashes are the logical paths' CRC-32 (two the research names).
            Assert.Contains(index.Entries, e => e.AndroidPath == DataPack.HashedPath("3d/Equipment/Grenade_Frag/Textures/Grenade_Frag_rom.tga"));
            Assert.Contains(index.Entries, e => e.AndroidPath == DataPack.HashedPath("Baked/Levels/Port/Port.Lightmap_0.tga") && e.Group == "lightmaps:Port");
        }

        [Fact]
        public void RealTexturesConvertWithTheirLayoutAndBeatThePhonesOwnQuality()
        {
            string pc = HdFixtures.PcData(output), android = HdFixtures.AndroidData(output);
            if (pc == null || android == null) return;
            HdIndex index = HdIndex.Load();
            HdIndexEntry character = index.Entries.Where(e => e.Category == HdIndex.Characters && e.Layout == "split" && e.AndroidWidth == 256).OrderBy(e => e.Hash).First();
            string[] hashes = { Surface, Equipment, PortLightmap1, PortLowend1, character.Hash };
            long texels = 0;
            var clock = Stopwatch.StartNew();
            foreach (string hash in hashes)
            {
                HdIndexEntry e = index.Entries.Single(x => x.Hash == hash);
                byte[] original = File.ReadAllBytes(Path.Combine(android, e.AndroidPath));
                ConversionResult r = TextureConverter.Convert(original, File.ReadAllBytes(Path.Combine(pc, e.PcPath)), Environment.ProcessorCount);
                texels += r.Texels;
                output.WriteLine(e.AndroidPath + " " + e.Category + " " + r.Layout + " " + r.OriginalWidth + " -> " + r.Width + ": PSNR " + F(r.Psnr) +
                                 (double.IsNaN(r.AlphaPsnr) ? "" : ", alpha " + F(r.AlphaPsnr)) + " dB (the phone's own: " + F(r.OriginalPsnr) + " dB), " +
                                 original.Length + " -> " + r.File.Length + " bytes, " + r.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s");
                Assert.Equal(e.Layout, r.Layout);
                Assert.Equal(e.PcWidth, r.Width);
                Assert.Equal(e.AndroidMips + e.Doublings, r.MipLevels);
                Assert.True(r.Psnr >= 24, e.AndroidPath + ": " + r.Psnr);
                // At the PC size against the PC texture, the replacement is at least as faithful as the phone's file is at its size.
                Assert.True(r.Psnr >= r.OriginalPsnr - 1.0, e.AndroidPath + ": " + r.Psnr + " < " + r.OriginalPsnr);
                if (r.Layout == "split") Assert.True(r.AlphaPsnr >= 30, e.AndroidPath + " alpha " + r.AlphaPsnr);

                // Same container, wrapping, header fields and key/value data as the original; the new size.
                AndroidTexture before = AndroidTexture.Parse(original), after = AndroidTexture.Parse(r.File);
                Assert.Equal(before.Wrapped, after.Wrapped);
                Assert.Equal(before.Entries.Select(x => x.Key), after.Entries.Select(x => x.Key));
                Assert.Equal(before.Rgb.Header.Take(6), after.Rgb.Header.Take(6));
                Assert.Equal(before.Rgb.KeyValueData, after.Rgb.KeyValueData);
                Assert.Equal((uint)e.PcHeight, after.Rgb.Header[7]);
                if (before.Layout == "split")
                {
                    Assert.Equal(before.Entry("SPLIT"), after.Entry("SPLIT"));
                    Assert.Equal(before.Alpha.KeyValueData, after.Alpha.KeyValueData);
                    // Gameloft's zip has its SPLIT data at 35 and no extra fields; so does ours.
                    Assert.Equal(original.Take(30).Skip(26), r.File.Take(30).Skip(26));
                }
                else
                {
                    Assert.Equal(GcbfWriter.TextureBlocks + 1, (int)Ktx.U32(r.File, 4));
                }
            }
            double rate = texels / 1e6 / clock.Elapsed.TotalSeconds;
            long scope = index.Select(HdIndex.DefaultCategories, null).Sum(e => e.OutputTexels);
            output.WriteLine("throughput " + rate.ToString("0.0", CultureInfo.InvariantCulture) + " Mtexel/s on " + Environment.ProcessorCount + " threads; " +
                             "the default scope is " + (scope / 1e6).ToString("#,0", CultureInfo.InvariantCulture) + " Mtexel: about " +
                             (scope / 1e6 / rate / 60).ToString("0", CultureInfo.InvariantCulture) + " min on this machine");
        }

        [Fact]
        public void ARealPackOfAFewTexturesFollowsTheContract()
        {
            string pc = HdFixtures.PcData(output), android = HdFixtures.AndroidData(output);
            if (pc == null || android == null) return;
            string dir = TestEnvironment.NewTempDir("hdreal");
            var log = new StringWriter();
            BuildSummary summary = PackBuilder.Build(new BuildOptions
            {
                PcData = pc, AndroidData = android, Categories = HdIndex.DefaultCategories, Maps = new[] { "Port", "Snow" }, Output = dir,
                OnlyHashes = new HashSet<string> { Surface, PortLightmap1, PortLowend1 },
            }, log);
            output.WriteLine(log.ToString());
            Assert.Equal(3, summary.Files);
            Assert.EndsWith("mcvs-hd-default-Port+Snow.zip", summary.PackPath);
            using (var zip = ZipFile.OpenRead(summary.PackPath))
            {
                var manifest = MiniJson.ParseObject(Encoding.UTF8.GetString(Read(zip.GetEntry("mcvs-hd.json"))));
                foreach (Dictionary<string, object> f in ((List<object>)manifest["files"]).Cast<Dictionary<string, object>>())
                {
                    string entry = (string)f["entry"];
                    byte[] original = File.ReadAllBytes(Path.Combine(android, entry.Substring(5)));
                    Assert.Equal(PackBuilder.Hex(Crc32.Compute(original)), ((Dictionary<string, object>)f["original"])["crc32"]);
                    Assert.Equal(PackBuilder.Hex(Crc32.Compute(Read(zip.GetEntry(entry)))), ((Dictionary<string, object>)f["replacement"])["crc32"]);
                }
                var named = ((List<object>)manifest["files"]).Cast<Dictionary<string, object>>().Single(f => (string)f["hash"] == PortLightmap1);
                Assert.Equal("Baked/Levels/Port/Port.Lightmap_1.tga", named["logicalPath"]);
                Assert.Equal("lightmaps:Port", named["group"]);
            }
            HdFixtures.DeleteDir(dir);
        }

        private static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

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
