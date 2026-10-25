using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests
{
    /// <summary>Data-pack access: crc32 names, LZ4 blocks and GCBF containers (report 11), on synthetic files.</summary>
    public class DataPackTests
    {
        private const uint CodecLz4 = 0x4C7A3420;
        private const uint CodecRaw = 0x52617720;

        private readonly ITestOutputHelper output;

        public DataPackTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void Crc32MatchesZlib()
        {
            byte[] data = Encoding.ASCII.GetBytes("123456789");
            Assert.Equal(0xCBF43926u, Crc32.Compute(data));
            Assert.Equal(0xDC8F2D65u, Crc32.Compute(data, 1));                                 // zlib.crc32(b"123456789", 1)
            Assert.Equal(0xCCC4CE45u, Crc32.Compute(Encoding.ASCII.GetBytes("hello world"), 1)); // zlib.crc32(b"hello world", 1)
        }

        [Theory]
        [InlineData("Config/Settings.tx", "c/a0f90c8.f")]
        [InlineData("SceneComponents.tx", "1/52218da.f")]
        [InlineData("Levels/Menus.level", "9/634dd17.f")]
        [InlineData("Levels/Connection.level", "8/3f9efb0.f")]
        [InlineData("Config/crmpreconfiguration.json", "b/de76cd6.f")]
        public void LogicalPathsHashToOnDiskNames(string logical, string hashed)
        {
            Assert.Equal(hashed, DataPack.HashedPath(logical));
        }

        [Fact]
        public void Lz4LiteralOnlyBlock()
        {
            byte[] literals = Encoding.ASCII.GetBytes("hello world");
            var block = new List<byte> { (byte)(literals.Length << 4) };
            block.AddRange(literals);
            Assert.Equal("hello world", Lz4(block.ToArray(), literals.Length));
        }

        [Fact]
        public void Lz4OverlappingMatchAndLengthExtensions()
        {
            // "abc" + match(offset 3, length 12) + "!"  -> "abcabcabcabcabc!"
            var b1 = new List<byte> { 0x38, (byte)'a', (byte)'b', (byte)'c', 3, 0, 0x10, (byte)'!' };
            Assert.Equal("abcabcabcabcabc!", Lz4(b1.ToArray(), 16));

            // 20 literals (15 + extension 5)
            var b2 = new List<byte> { 0xF0, 5 };
            b2.AddRange(Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQRST"));
            Assert.Equal("ABCDEFGHIJKLMNOPQRST", Lz4(b2.ToArray(), 20));

            // "z" + run (offset 1, length 4+15+80 = 99) + "z" -> 101 'z'
            var b3 = new List<byte> { 0x1F, (byte)'z', 1, 0, 80, 0x10, (byte)'z' };
            Assert.Equal(new string('z', 101), Lz4(b3.ToArray(), 101));
        }

        [Fact]
        public void Lz4RejectsCorruptInput()
        {
            Assert.Throws<InvalidOperationException>(() => Lz4(new byte[] { 0x10, (byte)'a', 5, 0, 0x00 }, 10)); // offset beyond output
            Assert.Throws<InvalidOperationException>(() => Lz4(new byte[] { 0x50, (byte)'a' }, 5));               // literals past input
            Assert.Throws<InvalidOperationException>(() => Lz4(new byte[] { 0x20, (byte)'a', (byte)'b' }, 3));   // wrong output size
        }

        [Fact]
        public void GcbfWithRawAndLz4BlocksDecodes()
        {
            byte[] gcbf = BuildGcbf(
                (CodecRaw, Encoding.ASCII.GetBytes("Hello, "), Encoding.ASCII.GetBytes("Hello, ")),
                (CodecLz4, Encoding.ASCII.GetBytes("abcabcabcabcabc!"), new byte[] { 0x38, (byte)'a', (byte)'b', (byte)'c', 3, 0, 0x10, (byte)'!' }));
            Assert.True(Gcbf.IsGcbf(gcbf));
            Assert.Equal("Hello, abcabcabcabcabc!", Encoding.ASCII.GetString(Gcbf.Decode(gcbf, true)));
        }

        [Fact]
        public void GcbfCorruptionIsDetected()
        {
            byte[] gcbf = BuildGcbf((CodecRaw, Encoding.ASCII.GetBytes("payload"), Encoding.ASCII.GetBytes("payload")));
            gcbf[gcbf.Length - 1] ^= 0xFF;
            Assert.Throws<DataPackException>(() => Gcbf.Decode(gcbf, true));
            Assert.Throws<DataPackException>(() => Gcbf.Decode(Encoding.ASCII.GetBytes("GCBF\x05\0\0\0"), true));
        }

        [Fact]
        public void DataPackReadsHashedGcbfAndPlainFiles()
        {
            using var dir = new TempDir();
            string data = dir.Combine("files", "Data");
            Directory.CreateDirectory(Path.Combine(data, "Config"));
            File.WriteAllText(Path.Combine(data, "MCVS.game"), "{}");
            File.WriteAllText(Path.Combine(data, "Config", "EngineConfig.json"), "{\"plain\":true}");
            byte[] json = Encoding.UTF8.GetBytes("{\"game\":{\"x\":1}}");
            string hashed = Path.Combine(data, DataPack.HashedPath("Config/crmpreconfiguration.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(hashed));
            File.WriteAllBytes(hashed, BuildGcbf((CodecRaw, json, json)));

            var pack = new DataPack(dir.Combine("files")); // the parent of Data/ is accepted too
            Assert.Equal(Path.GetFullPath(data), pack.DataDirectory);
            Assert.True(pack.IsValid);
            Assert.True(pack.TryReadText("Config/crmpreconfiguration.json", out string crm));
            Assert.Equal("{\"game\":{\"x\":1}}", crm);
            Assert.True(pack.TryReadText("Config/EngineConfig.json", out string engine));
            Assert.Contains("plain", engine);
            Assert.False(pack.TryRead("Config/missing.json", out _));
        }

        [Fact]
        public void ContentProviderPrefersOverridesAndRejectsTraversal()
        {
            using var dir = new TempDir();
            var provider = new ContentProvider(null, dir.Path, null);
            Assert.Contains("manifest.json", provider.ContentNames); // compiled in from server/Content
            Assert.True(provider.TryReadContentText("manifest.json", out string builtIn));
            Assert.Equal(1L, MiniJson.ParseObject(builtIn)["schema"]);

            File.WriteAllText(dir.Combine("manifest.json"), "{\"schema\":99}");
            Assert.True(provider.TryReadContentText("manifest.json", out string overridden));
            Assert.Contains("99", overridden);
            Assert.False(provider.TryReadContent("../secret", out _));
            Assert.False(provider.TryReadGameFile("Config/crmpreconfiguration.json", out _)); // no data pack configured
        }

        /// <summary>Runs against the player's real data pack when MCVS_DATAPACK points to it (never committed).</summary>
        [Fact]
        public void RealDataPackDecodesWhenAvailable()
        {
            string dir = Environment.GetEnvironmentVariable("MCVS_DATAPACK");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                output.WriteLine("MCVS_DATAPACK not set: skipped");
                return;
            }
            var pack = new DataPack(dir);
            Assert.True(pack.IsValid);
            Assert.True(pack.TryReadText("Config/crmpreconfiguration.json", out string crm));
            var doc = MiniJson.ParseObject(crm);
            Assert.True(doc.ContainsKey("game"));
            Assert.True(doc.ContainsKey("offline_store"));
            Assert.True(pack.TryRead("SceneComponents.tx", out byte[] scene));
            Assert.True(scene.Length > 1000);
            output.WriteLine("crmpreconfiguration.json: " + crm.Length + " chars; SceneComponents.tx: " + scene.Length + " bytes");
        }

        private static string Lz4(byte[] block, int decodedSize)
        {
            var dst = new byte[decodedSize];
            Lz4Block.Decompress(block, 0, block.Length, dst, 0, decodedSize);
            return Encoding.ASCII.GetString(dst);
        }

        /// <summary>Builds a GCBF file the way the engine's writer does (report 11 §3).</summary>
        private static byte[] BuildGcbf(params (uint codec, byte[] raw, byte[] payload)[] blocks)
        {
            int n = blocks.Length + 1;
            int headerEnd = 8 + 8 * n;
            var body = new MemoryStream();
            var table = new List<(uint raw, uint file)>();
            uint rawOffset = 0;
            foreach (var (codec, raw, payload) in blocks)
            {
                table.Add((rawOffset, (uint)(headerEnd + body.Length)));
                Write(body, 0x41646170u, codec, (uint)payload.Length, (uint)raw.Length, Crc32.Compute(payload, 1), Crc32.Compute(raw, 1), (uint)raw.Length, 0u, 0u);
                body.Write(payload);
                rawOffset += (uint)raw.Length;
            }
            table.Add((rawOffset, (uint)(headerEnd + body.Length)));
            var file = new MemoryStream();
            file.Write(Encoding.ASCII.GetBytes("GCBF"));
            Write(file, (uint)n);
            foreach (var (raw, fileOffset) in table) Write(file, raw, fileOffset);
            body.Position = 0;
            body.CopyTo(file);
            return file.ToArray();
        }

        private static void Write(Stream s, params uint[] values)
        {
            foreach (uint v in values) s.Write(BitConverter.GetBytes(v));
        }
    }
}
