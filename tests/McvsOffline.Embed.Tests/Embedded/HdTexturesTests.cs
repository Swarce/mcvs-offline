using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Hd;
using McvsOffline.Tools.HdTextures;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The HD texture probe (docs/research/pc-textures.md WP1, WP3 route B): tools/hd-textures builds the zip from the player's
    /// texture (a synthetic ASTC 6x6 one like the S22's pack, and the real ETC1 file of MCVS_DATAPACK), the embedded host reads it
    /// (StoredZip), mounts it through a fake engine file system and names the copy the engine's lookup returns.
    /// </summary>
    [Collection("GameHooks")]
    public class HdTexturesTests : IDisposable
    {
        private readonly ITestOutputHelper output;

        public HdTexturesTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        public void Dispose()
        {
            HdTextures.ResetForTests();
        }

        /// <summary>A KTX1 like the ASTC pack's (pc-textures.md §2.2): RGBA ASTC 6x6, full mip chain, no key/value data.</summary>
        internal static byte[] AstcKtx(int size)
        {
            var ktx = new Ktx();
            ktx.Header = new uint[] { 0x04030201, 0, 1, 0, Ktx.GlAstc6x6, 0x1908, (uint)size, (uint)size, 0, 0, 1, 0, 0 };
            int levels = 0;
            for (int s = size; ; s = Math.Max(1, s / 2))
            {
                levels++;
                int blocks = ((s + 5) / 6) * ((s + 5) / 6);
                var data = new byte[blocks * 16];
                new Random(levels).NextBytes(data);
                ktx.Levels.Add(data);
                if (s == 1) break;
            }
            ktx.Header[11] = (uint)levels;
            return ktx.Write();
        }

        [Fact]
        public void Lz4LiteralBlocksAndGcbfRoundTripThroughTheServersDecoder()
        {
            foreach (int n in new[] { 0, 1, 14, 15, 16, 269, 270, 271, 131072, 131073, 300000 })
            {
                var data = new byte[n];
                new Random(n).NextBytes(data);
                byte[] lz4 = GcbfWriter.Lz4Literals(data);
                var back = new byte[n];
                Lz4Block.Decompress(lz4, 0, lz4.Length, back, 0, n);
                Assert.Equal(data, back);
                byte[] gcbf = GcbfWriter.Encode(data);
                Assert.True(Gcbf.IsGcbf(gcbf));
                Assert.Equal(data, Gcbf.Decode(gcbf, true));
            }
        }

        [Fact]
        public void SolidBlocksDecodeToTheirColour()
        {
            byte[] astc = ProbeTexture.AstcVoidExtent(ProbeColour.Magenta);
            Assert.Equal(new byte[] { 0xFC, 0xFD, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF }, astc);
            // Void-extent marker: bits 0-8 = 0x1FC, bit 9 = 0 (LDR), bits 10-11 set.
            ulong low = BitConverter.ToUInt64(astc, 0);
            Assert.Equal(0x1FCUL, low & 0x1FF);
            Assert.Equal(0UL, (low >> 9) & 1);
            Assert.Equal(3UL, (low >> 10) & 3);
            foreach (ProbeColour c in new[] { ProbeColour.Magenta, ProbeColour.Green, ProbeColour.Blue, ProbeColour.Yellow, ProbeColour.Cyan, ProbeColour.Orange })
            {
                byte[] rgb = ProbeTexture.DecodeEtc1Solid(ProbeTexture.Etc1Solid(c));
                Assert.InRange(Math.Abs(rgb[0] - c.R), 0, 6);
                Assert.InRange(Math.Abs(rgb[1] - c.G), 0, 6);
                Assert.InRange(Math.Abs(rgb[2] - c.B), 0, 6);
                Assert.Equal(0x02, ProbeTexture.Etc1Solid(c)[3]);   // differential mode, table 0, no flip
            }
        }

        [Fact]
        public void TheProbeZipHoldsThreeNamingsOfTheRecolouredTextureAndAManifest()
        {
            byte[] original = AstcKtx(256);
            ProbeResult r = ProbeBuilder.Build(GcbfWriter.Encode(original), null, false, new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal(ProbeBuilder.DefaultTexture, r.LogicalPath);
            Assert.Equal("8/dbd2f1.f", r.HashedPath);
            Assert.Equal("ASTC", r.Pack);
            Assert.Equal(new[] { "3d/Environment/01_WorkTextures/ENV_GenericConcrete05_bc.tga", "8/dbd2f1.f", "Data/8/dbd2f1.f" }, r.Variants.Select(v => v.Entry));
            Assert.Equal(new[] { "magenta", "green", "blue" }, r.Variants.Select(v => v.Colour.Name));

            string dir = TestEnvironment.NewTempDir("hdprobe");
            string zipPath = Path.Combine(dir, "hd-textures.zip");
            File.WriteAllBytes(zipPath, r.Zip);
            StoredZip zip = StoredZip.Open(zipPath);
            Assert.Equal(new[] { "mcvs-hd.json" }.Concat(r.Variants.Select(v => v.Entry)), zip.Entries.Select(e => e.Name));
            Assert.Equal(0, zip.CompressedEntries);
            foreach (ProbeVariant v in r.Variants)
            {
                byte[] stored = zip.ReadStored(zip.Find(v.Entry));
                Assert.Equal(v.Bytes, stored);
                Ktx ktx = Ktx.Parse(Gcbf.Decode(stored, true));
                Ktx source = Ktx.Parse(original);
                Assert.Equal(source.Header.Take(12), ktx.Header.Take(12));   // format, size, mip count: the original's
                Assert.Equal(source.Levels.Select(l => l.Length), ktx.Levels.Select(l => l.Length));
                byte[] block = ProbeTexture.AstcVoidExtent(v.Colour);
                Assert.All(ktx.Levels, level => Assert.Equal(block, level.Take(16).ToArray()));
                Assert.All(ktx.Levels, level => Assert.Equal(block, level.Skip(level.Length - 16).ToArray()));
            }
            // Entry data is 4-byte aligned (zipalign's extra field).
            byte[] bytes = File.ReadAllBytes(zipPath);
            foreach (StoredZipEntry e in zip.Entries)
            {
                int nameLength = bytes[e.LocalHeaderOffset + 26] | (bytes[e.LocalHeaderOffset + 27] << 8);
                int extraLength = bytes[e.LocalHeaderOffset + 28] | (bytes[e.LocalHeaderOffset + 29] << 8);
                Assert.Equal(0, (e.LocalHeaderOffset + 30 + nameLength + extraLength) % 4);
            }
            // The manifest the host reads.
            var manifest = MiniJson.ParseObject(Encoding.UTF8.GetString(zip.ReadStored(zip.Find("mcvs-hd.json"))));
            output.WriteLine(r.ManifestJson);
            Assert.Equal("probe", manifest["kind"]);
            var probe = (Dictionary<string, object>)manifest["probe"];
            Assert.Equal(ProbeBuilder.DefaultTexture, probe["logicalPath"]);
            Assert.Equal("ASTC 6x6 256x256, 9 mip levels", probe["texture"]);
            Assert.Equal(HdTextures.Crc(original), probe["originalCrc32"]);
            var variants = ((List<object>)probe["variants"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(HdTextures.Crc(r.Variants[0].Payload), variants[0]["payloadCrc32"]);
            Assert.Equal(HdTextures.Crc(r.Variants[0].Bytes), variants[0]["entryCrc32"]);

            // --raw: bare KTX, other colours.
            ProbeResult raw = ProbeBuilder.Build(original, ProbeBuilder.DefaultTexture, true, DateTime.UtcNow);
            Assert.Equal(new[] { "yellow", "cyan", "orange" }, raw.Variants.Select(v => v.Colour.Name));
            Assert.All(raw.Variants, v => Assert.True(Ktx.LooksLikeKtx(v.Bytes)));
        }

        [Fact]
        public void TheProbeRefusesWhatItCannotRecolour()
        {
            byte[] splitZip = Encoding.ASCII.GetBytes("PK\u0003\u0004SPLIT....");
            Assert.Contains("split-alpha zip", Assert.Throws<InvalidDataException>(() => ProbeBuilder.Build(splitZip, null, false, DateTime.UtcNow)).Message);
            var rgba8 = new Ktx { Header = new uint[] { 0x04030201, 0x1401, 1, 0x1908, 0x8058, 0x1908, 4, 4, 0, 0, 1, 1, 0 } };
            rgba8.Levels.Add(new byte[64]);
            Assert.Contains("supports ASTC", Assert.Throws<InvalidDataException>(() => ProbeBuilder.Build(rgba8.Write(), null, false, DateTime.UtcNow)).Message);
            Assert.Throws<InvalidDataException>(() => ProbeBuilder.Build(new byte[] { 1, 2, 3 }, null, false, DateTime.UtcNow));
        }

        [Fact]
        public void TheRealEtcTextureOfTheDataPackBecomesAnEtcProbe()
        {
            string data = Environment.GetEnvironmentVariable("MCVS_DATAPACK");
            var pack = string.IsNullOrEmpty(data) ? null : new DataPack(data);
            string file = pack == null ? null : pack.Locate(ProbeBuilder.DefaultTexture);
            if (file == null)
            {
                output.WriteLine("MCVS_DATAPACK not set or without " + ProbeBuilder.DefaultTexture + ": skipped");
                return;
            }
            string dir = TestEnvironment.NewTempDir("hdprobe-cli");
            string zipPath = Path.Combine(dir, "hd-textures.zip");
            var console = new StringWriter();
            Assert.Equal(0, McvsOffline.Tools.HdTextures.Program.Run(new[] { "probe", "--file", file, "--out", zipPath }, console));
            output.WriteLine(console.ToString());
            Assert.Contains("ETC1 256x256, 9 mip levels (ETC pack)", console.ToString());
            Assert.Contains("adb push " + zipPath + " /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline/hd/hd-textures.zip", console.ToString());
            StoredZip zip = StoredZip.Open(zipPath);
            Ktx magenta = Ktx.Parse(Gcbf.Decode(zip.ReadStored(zip.Find(ProbeBuilder.DefaultTexture)), true));
            Ktx source = Ktx.Parse(Gcbf.Decode(File.ReadAllBytes(file), true));
            Assert.Equal(source.KeyValueData, magenta.KeyValueData);
            Assert.Equal(source.Levels.Select(l => l.Length), magenta.Levels.Select(l => l.Length));
            Assert.Equal(ProbeTexture.Etc1Solid(ProbeColour.Magenta), magenta.Levels[0].Take(8).ToArray());

            // The same through --data, and the usage errors.
            Assert.Equal(0, McvsOffline.Tools.HdTextures.Program.Run(new[] { "probe", "--data", data, "--out", zipPath }, new StringWriter()));
            Assert.Throws<ArgumentException>(() => McvsOffline.Tools.HdTextures.Program.Run(new[] { "probe" }, new StringWriter()));
            Assert.Throws<ArgumentException>(() => McvsOffline.Tools.HdTextures.Program.Run(new[] { "probe", "--data", data, "--texture", "no/such.tga" }, new StringWriter()));
            Assert.Equal(2, McvsOffline.Tools.HdTextures.Program.Run(new string[0], new StringWriter()));
        }

        // ------------------------------------------------------------------ the host side

        private sealed class FakeEngine : IEngineFiles
        {
            public bool Available { get; set; } = true;
            public string SavePath { get; set; } = "/data/user/0/com.gameloft.android.ANMP.GloftMVHM/files";
            public string CachePath { get; set; } = "/data/user/0/com.gameloft.android.ANMP.GloftMVHM/cache";
            public string DataPath { get; set; }
            public Func<string, string, bool> Accept = (location, path) => true;
            public Func<string, byte[]> Read = path => null;
            public List<string> Calls = new List<string>();

            public bool PushZip(string location, string relativePath)
            {
                Calls.Add(location + " " + relativePath);
                return Accept(location, relativePath);
            }

            public bool PushZipFront(string relativePath)
            {
                Calls.Add("Front " + relativePath);
                return Accept("Front", relativePath);
            }

            public byte[] ReadAsset(string logicalPath)
            {
                Calls.Add("Read " + logicalPath);
                return Read(logicalPath);
            }
        }

        [Fact]
        public void RelativePathsClimbOutOfTheEngineRoots()
        {
            const string zip = "/storage/emulated/0/Android/data/pkg/files/McvsOffline/hd/hd-textures.zip";
            Assert.Equal("../../../../../storage/emulated/0/Android/data/pkg/files/McvsOffline/hd/hd-textures.zip",
                         HdTextures.Relative("/data/user/0/pkg/files", zip));
            Assert.Equal("McvsOffline/hd/hd-textures.zip", HdTextures.Relative("/storage/emulated/0/Android/data/pkg/files", zip));
            Assert.Equal("../McvsOffline/hd/hd-textures.zip", HdTextures.Relative("/storage/emulated/0/Android/data/pkg/files/Data/", zip));
            Assert.Null(HdTextures.Relative(null, zip));
            Assert.Null(HdTextures.Relative("relative/root", zip));
            Assert.Equal(new[] { "Save", "Cache" }, HdTextures.Attempts("auto", zip, "/a", "/b", "/c").Select(a => a[0]));
            Assert.Equal(new[] { "Front" }, HdTextures.Attempts("FRONT", zip, "/a", "/b", "/c").Select(a => a[0]));
            Assert.Equal(new[] { "Asset" }, HdTextures.Attempts("asset", zip, "/a", "/b", "/c").Select(a => a[0]));
            Assert.Equal(new[] { "Cache" }, HdTextures.Attempts("cache", zip, null, "/b", null).Select(a => a[0]));
            Assert.Empty(HdTextures.Attempts("save", zip, null, "/b", null));
            Assert.Equal("auto", HdTextures.NormalizeMode("weird"));
        }

        [Fact]
        public void MountTriesSaveThenCacheAndTheProbeNamesTheCopyTheEngineReturns()
        {
            string root = TestEnvironment.NewTempDir("hdmount");
            string zipPath = HdTextures.ZipPath(Path.Combine(root, "McvsOffline"));
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
            ProbeResult probe = ProbeBuilder.Build(GcbfWriter.Encode(AstcKtx(64)), null, false, DateTime.UtcNow);
            File.WriteAllBytes(zipPath, probe.Zip);
            var lines = new List<string>();

            // Save refuses, Cache accepts; the engine returns the hashed copy, GCBF-decoded.
            var engine = new FakeEngine
            {
                DataPath = Path.Combine(root, "Data"),
                Accept = (location, path) => location == "Cache",
                Read = path => probe.Variants[1].Payload,
            };
            string status = HdTextures.Mount(zipPath, true, "auto", engine, lines.Add);
            foreach (string l in lines) output.WriteLine(l);
            Assert.StartsWith("Save ../", engine.Calls[0]);
            Assert.StartsWith("Cache ../", engine.Calls[1]);
            Assert.EndsWith("/McvsOffline/hd/hd-textures.zip", engine.Calls[1]);
            Assert.Equal("Read " + ProbeBuilder.DefaultTexture, engine.Calls[2]);
            Assert.Contains(lines, l => l.StartsWith("hd textures: hd-textures.zip 4 entries", StringComparison.Ordinal) && l.Contains("kind probe") &&
                                        l.Contains("engine roots: Save '/data/user/0/"));
            Assert.Contains(lines, l => l.StartsWith("hd textures: mounted through Cache '../", StringComparison.Ordinal) && l.Contains("Save '../") && l.Contains("refused"));
            Assert.Contains(lines, l => l.Contains("probe " + ProbeBuilder.DefaultTexture + ": the engine returned the zip entry '8/dbd2f1.f' (hashed naming, green), GCBF-decoded"));
            Assert.Contains("entries mounted through Cache", status);
            Assert.Equal(status, HdTextures.Status);

            // The raw bytes of the logical entry; then the game's own copy (Data/ first); then nothing.
            lines.Clear();
            engine = new FakeEngine { Read = path => probe.Variants[0].Bytes };
            HdTextures.Mount(zipPath, true, "save", engine, lines.Add);
            Assert.Contains(lines, l => l.Contains("the engine returned the zip entry '" + ProbeBuilder.DefaultTexture + "' (logical naming, magenta), bytes as stored"));
            lines.Clear();
            HdTextures.Mount(zipPath, true, "auto", new FakeEngine { Read = path => AstcKtx(64) }, lines.Add);
            Assert.Contains(lines, l => l.Contains("the engine returned another copy") && l.Contains("not searched before Data/"));
            lines.Clear();
            HdTextures.Mount(zipPath, true, "auto", new FakeEngine { Read = path => throw new IOException("no such file") }, lines.Add);
            Assert.Contains(lines, l => l.Contains("the engine read nothing for " + ProbeBuilder.DefaultTexture + " (IOException: no such file)"));

            // front: relative to Data/.
            var front = new FakeEngine { DataPath = Path.Combine(root, "Data") };
            HdTextures.Mount(zipPath, true, "front", front, s => { });
            Assert.Equal("Front ../McvsOffline/hd/hd-textures.zip", front.Calls[0]);

            // Every engine refusal: Data/ stays in use.
            lines.Clear();
            Assert.Contains("did not mount", HdTextures.Mount(zipPath, true, "auto", new FakeEngine { Accept = (a, b) => false }, lines.Add));
        }

        [Fact]
        public void MissingBrokenOrDisabledZipsChangeNothing()
        {
            string root = TestEnvironment.NewTempDir("hdmissing");
            string zipPath = HdTextures.ZipPath(Path.Combine(root, "McvsOffline"));
            var engine = new FakeEngine();
            var lines = new List<string>();
            Assert.Equal("hd textures: none (no McvsOffline/hd/hd-textures.zip)", HdTextures.Mount(zipPath, true, "auto", engine, lines.Add));
            Assert.Empty(engine.Calls);

            Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
            File.WriteAllBytes(zipPath, Encoding.ASCII.GetBytes("not a zip at all, just text that is long enough"));
            Assert.Contains("is not a usable zip (no end-of-central-directory record)", HdTextures.Mount(zipPath, true, "auto", engine, lines.Add));
            Assert.Empty(engine.Calls);

            ProbeResult probe = ProbeBuilder.Build(AstcKtx(16), null, false, DateTime.UtcNow);
            byte[] cut = probe.Zip.Take(probe.Zip.Length - 30).ToArray();
            File.WriteAllBytes(zipPath, cut);
            Assert.Contains("is not a usable zip", HdTextures.Mount(zipPath, true, "auto", engine, lines.Add));
            byte[] flipped = (byte[])probe.Zip.Clone();
            flipped[200] ^= 0xFF;   // inside the manifest entry: its CRC fails, the manifest is ignored, the mount goes on
            File.WriteAllBytes(zipPath, flipped);
            Assert.Contains("entries mounted through Save", HdTextures.Mount(zipPath, true, "auto", engine, lines.Add));

            File.WriteAllBytes(zipPath, probe.Zip);
            engine.Calls.Clear();
            Assert.Equal("hd textures: off (settings hd.enabled false); hd-textures.zip is left unused", HdTextures.Mount(zipPath, false, "auto", engine, lines.Add));
            Assert.Empty(engine.Calls);
            Assert.Contains("not reachable", HdTextures.Mount(zipPath, true, "auto", new FakeEngine { Available = false }, lines.Add));
            Assert.Contains("not reachable", HdTextures.Mount(zipPath, true, "auto", null, lines.Add));
            // Without Gameloft.Engine (tests, a PC) the reflection side is unbound.
            Assert.False(new EngineFiles().Available);
        }
    }
}
