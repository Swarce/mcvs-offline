using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Install;
using McvsOffline.Server.Core;
using McvsOffline.Tools.HdTextures;
using Xunit;
using Xunit.Abstractions;
using ContentCrc = McvsOffline.Server.Core.Content.Crc32;
using Gcbf = McvsOffline.Server.Core.Content.Gcbf;

namespace McvsOffline.Embed.Tests.HdTextures
{
    /// <summary>
    /// The full ASTC HD data pack (tools/hd-textures astc-pack, docs/design/hd-textures.md §6): the astcenc wrapper, the
    /// converter, the parts and their index, and the host accepting the result (InstallerData). Encoding needs ARM's astcenc
    /// (MCVS_ASTCENC, or on PATH; skipped without it); the real pack is checked when MCVS_ASTC_HD_OUT names its folder.
    /// </summary>
    public class AstcPackTests
    {
        private readonly ITestOutputHelper output;

        public AstcPackTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private Astcenc Encoder()
        {
            try
            {
                Astcenc a = Astcenc.Locate(null, out string how);
                a.TempDir = TestEnvironment.NewTempDir("astcenc");
                output.WriteLine("astcenc " + a.Path + " (" + how + ")");
                return a;
            }
            catch (ArgumentException ex)
            {
                if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_ASTCENC") == "1") throw;
                output.WriteLine("astcenc not available (" + ex.Message + "): skipped");
                return null;
            }
        }

        [Fact]
        public void BlockSizesAndFormatsFollowTheKhronosTable()
        {
            Assert.Equal(0x93B0u, Ktx.AstcFormat(4, 4, false));
            Assert.Equal(0x93B2u, Ktx.AstcFormat(5, 5, false));
            Assert.Equal(0x93B4u, Ktx.AstcFormat(6, 6, false));
            Assert.Equal(0x93D4u, Ktx.AstcFormat(6, 6, true));
            Assert.Equal(0u, Ktx.AstcFormat(7, 7, false));
            AstcSetting s = AstcSetting.Parse("5x5:medium", "thorough");
            Assert.Equal("5x5 -medium", s.ToString());
            Assert.Equal("thorough", AstcSetting.Parse("4x4", "thorough").Quality);
            Assert.Throws<ArgumentException>(() => AstcSetting.Parse("3x3", "thorough"));
            Assert.Throws<ArgumentException>(() => AstcSetting.Parse("6x6:best", "thorough"));
            Dictionary<string, AstcSetting> d = AstcPackBuilder.ParseSettings("lightmaps=6x6:medium,all=4x4", "thorough", AstcPackBuilder.DefaultSettings());
            Assert.Equal("4x4 -medium", d[HdIndex.Lightmaps].ToString());   // later items win, the quality stays
            Assert.Equal(400 * 16, Astcenc.DataSize(100, 100, 5, 5));
            Assert.Equal(17 * 17 * 16, Astcenc.DataSize(100, 100, 6, 6));
            Assert.Equal(4 * 16 + 16 + 16 + 16, AstcPackBuilder.GpuBytes(8, 8, 4, 6, 6));   // 8x8 is 2x2 blocks of 6x6
        }

        [Fact]
        public void AstcFilesAndDecodedKtxRoundTrip()
        {
            byte[] blocks = Enumerable.Range(0, 4 * 16).Select(i => (byte)i).ToArray();
            byte[] file = Astcenc.AstcFile(blocks, 10, 7, 5, 4);
            Assert.Equal(new byte[] { 0x13, 0xAB, 0xA1, 0x5C, 5, 4, 1, 10, 0, 0, 7, 0, 0, 1, 0, 0 }, file.Take(16));
            Assert.Equal(blocks, Astcenc.Blocks(file, 10, 7, 5, 4));
            Assert.Throws<InvalidDataException>(() => Astcenc.Blocks(file, 10, 7, 6, 6));

            // astcenc writes only the components the image uses: L, LA, RGB or RGBA, rows packed tightly.
            var image = new RgbaImage(3, 2);
            foreach ((uint format, int n, byte[] px, byte[] expected) in new[]
                     {
                         (0x1903u, 1, new byte[] { 9 }, new byte[] { 9, 9, 9, 255 }),
                         (0x8227u, 2, new byte[] { 9, 50 }, new byte[] { 9, 9, 9, 50 }),
                         (0x1907u, 3, new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3, 255 }),
                         (0x1908u, 4, new byte[] { 1, 2, 3, 4 }, new byte[] { 1, 2, 3, 4 }),
                     })
            {
                var ktx = new Ktx { Header = new uint[] { 0x04030201, 0x1401, 1, format, 0x8058, format, 3, 2, 0, 0, 1, 1, 0 } };
                var level = new byte[6 * n];
                for (int i = 0; i < 6; i++) Array.Copy(px, 0, level, i * n, n);
                ktx.Levels.Add(level);
                RgbaImage decoded = Astcenc.ReadUncompressedKtx(ktx.Write(), 3, 2);
                Assert.Equal(expected, decoded.Pixels.Skip(5 * 4).Take(4));
            }
            RgbaImage pattern = HdFixtures.Pattern(5, 3, 1, false);
            Assert.Equal(pattern.Pixels, Astcenc.ReadUncompressedKtx(Astcenc.UncompressedKtx(pattern), 5, 3).Pixels);
        }

        [Fact]
        public void PartsStayBelowTheLimitAndTheLastTakesTheIndex()
        {
            var files = Enumerable.Range(0, 10).Select(i => new KeyValuePair<string, long>("Data/1/" + i + ".f", 1000)).ToList();
            List<List<int>> parts = AstcPackBuilder.PlanParts(files, 500, 3000);
            Assert.True(parts.Count >= 4);
            Assert.Equal(Enumerable.Range(0, 10), parts.SelectMany(p => p));
            Assert.All(parts.Take(parts.Count - 1), p => Assert.NotEmpty(p));
            Assert.Throws<IOException>(() => AstcPackBuilder.PlanParts(new List<KeyValuePair<string, long>> { new KeyValuePair<string, long>("Data/x.f", 5000) }, 0, 3000));
        }

        [Fact]
        public void TheReplacedListTheToolWritesIsTheOneTheHostReads()
        {
            string text = ReplacedList.Write("ASTC", "1.17.32", new[]
            {
                new ReplacedList.Row { Path = "Data/1/9921a.f", Size = 5000000, Crc = 0xdeadbeef, OriginalSize = 559357, OriginalCrc = 0x01020304 },
            });
            ReplacedFiles list = ReplacedFiles.Parse(text);
            Assert.Equal("ASTC", list.Variant);
            Assert.Equal(1, list.Count);
            Assert.True(list.TryGetOriginalSize("Data/1/9921a.f", 5000000, out long original));
            Assert.Equal(559357, original);
            Assert.False(list.TryGetOriginalSize("Data/1/9921a.f", 559357, out original));   // only while it has the listed size
            Assert.Throws<FormatException>(() => ReplacedFiles.Parse("mcvs-replaced\t2\tASTC\n"));
            Assert.Throws<FormatException>(() => ReplacedFiles.Parse("mcvs-replaced\t1\tASTC\nData/../x.f\t1\t0\t1\t0\n"));
        }

        // ------------------------------------------------------------------ with astcenc

        /// <summary>A file of Gameloft's ASTC pack: GCBF around a KTX of ASTC 6x6 blocks, no key/value data (pc-textures.md §2.2).</summary>
        private static byte[] AstcOriginal(Astcenc encoder, RgbaImage top, int levels)
        {
            var ktx = new Ktx { Header = new uint[] { 0x04030201, 0, 0, 0, 0x93B4, 0x1908, (uint)top.Width, (uint)top.Height, 0, 0, 1, (uint)levels, 0 } };
            RgbaImage level = top;
            for (int i = 0; i < levels; i++)
            {
                ktx.Levels.Add(encoder.Compress(level, 6, 6, "fastest", false));
                level = level.HalfSize();
            }
            return GcbfWriter.Encode(ktx.Write());
        }

        [Fact]
        public void APcTextureBecomesAnAstcKtxWithTheOriginalsHeaderAtThePcSize()
        {
            Astcenc encoder = Encoder();
            if (encoder == null) return;
            RgbaImage pc = HdFixtures.Pattern(128, 128, 3, false);
            byte[] original = AstcOriginal(encoder, pc.HalfSize(), 7);
            AstcResult r = AstcConverter.Convert(original, HdFixtures.Dds(pc, true), new AstcSetting(5, 5, "fastest"), encoder);
            output.WriteLine("PSNR " + r.Psnr + " alpha " + r.AlphaPsnr + " Gameloft " + r.OriginalUpscaledPsnr + " / " + r.OriginalPsnr);
            Assert.True(Gcbf.IsGcbf(r.File));
            Ktx ktx = Ktx.Parse(Gcbf.Decode(r.File, true));
            Assert.Equal(new uint[] { 0x04030201, 0, 0, 0, 0x93B2, 0x1908, 128, 128, 0, 0, 1, 8, 0 }, ktx.Header);
            Assert.Equal(8, ktx.MipLevels);   // the chain ends at 1x1 as the original's does
            for (int i = 0; i < 8; i++) Assert.Equal(Astcenc.DataSize(Ktx.MipSize(128, i), Ktx.MipSize(128, i), 5, 5), ktx.Levels[i].Length);
            Assert.False(r.Opaque);
            Assert.False(r.Stretched);
            Assert.True(r.Psnr > 30, "PSNR " + r.Psnr);
            Assert.True(r.AlphaPsnr > 30, "alpha PSNR " + r.AlphaPsnr);
            Assert.Equal(ktx.Levels.Sum(l => (long)l.Length), r.GpuBytes);

            // An opaque original keeps the replacement opaque; a 1-level original stays 1 level.
            RgbaImage opaque = HdFixtures.Pattern(64, 64, 4, true);
            byte[] flat = AstcOriginal(encoder, opaque, 1);
            RgbaImage pcAlpha = HdFixtures.Pattern(128, 128, 4, false);
            AstcResult one = AstcConverter.Convert(flat, HdFixtures.Dds(pcAlpha, true), new AstcSetting(6, 6, "fastest"), encoder);
            Assert.True(one.Opaque);
            Assert.True(double.IsNaN(one.AlphaPsnr));
            Ktx k1 = Ktx.Parse(Gcbf.Decode(one.File, true));
            Assert.Equal(1, k1.MipLevels);
            RgbaImage decoded = encoder.Decompress(k1.Levels[0], 128, 128, 6, 6, false);
            Assert.True(decoded.IsOpaque());

            // Not the ASTC pack: refused.
            Assert.Throws<InvalidDataException>(() => AstcConverter.Convert(HdFixtures.AndroidKtxFile(pc.HalfSize(), 7), HdFixtures.Dds(pc, true),
                                                                             new AstcSetting(5, 5, "fastest"), encoder));
        }

        /// <summary>The top level of an <see cref="AstcOriginal"/> file, decoded.</summary>
        private static RgbaImage OriginalTop(Astcenc encoder, byte[] file)
        {
            Ktx ktx = Ktx.Parse(Gcbf.Decode(file, true));
            return encoder.Decompress(ktx.Levels[0], ktx.Width, ktx.Height, 6, 6, false);
        }

        /// <summary>Part of an image (x, y, width, height).</summary>
        private static RgbaImage Crop(RgbaImage image, int x, int y, int width, int height)
        {
            var part = new RgbaImage(width, height);
            for (int row = 0; row < height; row++) Array.Copy(image.Pixels, ((y + row) * image.Width + x) * 4, part.Pixels, row * width * 4, width * 4);
            return part;
        }

        /// <summary>A white emblem on black, edges antialiased (4x4 samples), alpha the same shape: like the 3D menus' icons.</summary>
        private static RgbaImage Emblem(int size)
        {
            var image = new RgbaImage(size, size);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int covered = 0;
                    for (int sy = 0; sy < 4; sy++)
                    {
                        for (int sx = 0; sx < 4; sx++)
                        {
                            double u = (x + (sx + 0.5) / 4) / size - 0.5, v = (y + (sy + 0.5) / 4) / size - 0.5, r = u * u + v * v;
                            if ((r < 0.16 && r > 0.09) || (Math.Abs(u) < 0.06 && Math.Abs(v) < 0.3) || (v > 0.1 && v < 0.3 && Math.Abs(u) < 0.3 - v)) covered++;
                        }
                    }
                    int o = (y * size + x) * 4;
                    byte c = (byte)(covered * 255 / 16);
                    image.Pixels[o] = c;
                    image.Pixels[o + 1] = c;
                    image.Pixels[o + 2] = c;
                    image.Pixels[o + 3] = c;
                }
            }
            return image;
        }

        [Fact]
        public void AnOriginalWhoseSizeIsNotAPowerOfTwoTakesThePcTextureOfTheSamePicture()
        {
            Astcenc encoder = Encoder();
            if (encoder == null) return;
            var s = new AstcSetting(5, 5, "fastest");

            // The ASTC pack keeps a few textures at the artist's size (85x85, 1024x778) where the ETC pack and the PC have
            // them stretched to a power of two: taken, with a full chain at the PC size, checked against the plan's levels.
            RgbaImage pc = HdFixtures.Pattern(128, 128, 5, true);
            byte[] npot = AstcOriginal(encoder, pc.Bilinear(48, 40), 6);
            AstcResult st = AstcConverter.Convert(npot, HdFixtures.Dds(pc, false), s, encoder);
            output.WriteLine("stretched: PSNR " + st.Psnr + " Gameloft " + st.OriginalUpscaledPsnr + " / " + st.OriginalPsnr);
            Assert.True(st.Stretched);
            Assert.Equal(48, st.OriginalWidth);
            Ktx ktx = Ktx.Parse(Gcbf.Decode(st.File, true));
            Assert.Equal(new uint[] { 0x04030201, 0, 0, 0, 0x93B2, 0x1908, 128, 128, 0, 0, 1, 8, 0 }, ktx.Header);
            for (int i = 0; i < 8; i++) Assert.Equal(Astcenc.DataSize(Ktx.MipSize(128, i), Ktx.MipSize(128, i), 5, 5), ktx.Levels[i].Length);
            Assert.True(st.Psnr > 30, "PSNR " + st.Psnr);
            Assert.True(st.OriginalPsnr > 30, "PSNR of the original at its size " + st.OriginalPsnr);
            Assert.Equal(TextureConverter.LevelCount(7, 1), st.MipLevels);   // what the plan counts for the ETC pack's 64x64 with 7 levels

            // A chain that stops early stops as early at the PC size; one level stays one level.
            Assert.Equal(6, AstcConverter.Convert(AstcOriginal(encoder, pc.Bilinear(48, 40), 4), HdFixtures.Dds(pc, false), s, encoder).MipLevels);
            Assert.Equal(1, AstcConverter.Convert(AstcOriginal(encoder, pc.Bilinear(48, 40), 1), HdFixtures.Dds(pc, false), s, encoder).MipLevels);

            // Hard edges at a factor that is not a power of two (the 200x200 menu emblem whose PC texture is 512x512): far
            // below 30 dB at the original's size although it is the same picture; the check at the reduced size takes it.
            RgbaImage emblem = Emblem(256);
            byte[] small = AstcOriginal(encoder, Emblem(50), 6);   // the same shape drawn at the artist's size
            AstcResult em = AstcConverter.Convert(small, HdFixtures.Dds(emblem, true), s, encoder);
            output.WriteLine("emblem: at its size " + em.OriginalPsnr + " dB, reduced " + AstcConverter.StretchedPsnr(OriginalTop(encoder, small), Dds.Parse(HdFixtures.Dds(emblem, true))) + " dB");
            Assert.True(em.Stretched);
            Assert.True(em.OriginalPsnr < 30, "emblem PSNR at its size " + em.OriginalPsnr);
            Assert.Equal(9, em.MipLevels);

            // Another picture, or the same picture moved by a fifth, is refused: Gameloft's file stays.
            byte[] other = AstcOriginal(encoder, HdFixtures.Pattern(48, 40, 99, true), 6);
            Assert.Contains("not the original 48x40 stretched", Assert.Throws<InvalidDataException>(() =>
                AstcConverter.Convert(other, HdFixtures.Dds(HdFixtures.Pattern(128, 128, 1, true), false), s, encoder)).Message);
            byte[] moved = AstcOriginal(encoder, Crop(emblem, 51, 51, 205, 205).Bilinear(50, 50), 6);
            output.WriteLine("emblem moved by a fifth: " + AstcConverter.StretchedPsnr(OriginalTop(encoder, moved), Dds.Parse(HdFixtures.Dds(emblem, true))) + " dB");
            Assert.Contains("stretched", Assert.Throws<InvalidDataException>(() => AstcConverter.Convert(moved, HdFixtures.Dds(emblem, true), s, encoder)).Message);

            // Only an original that is not a power of two: a power-of-two original keeps the power-of-two rule.
            byte[] square = AstcOriginal(encoder, pc.HalfSize(), 7);
            Assert.Contains("power-of-two multiple", Assert.Throws<InvalidDataException>(() =>
                AstcConverter.Convert(square, HdFixtures.Dds(HdFixtures.Pattern(256, 128, 5, true), false), s, encoder)).Message);
            // A PC texture not larger in both directions is not taken either.
            Assert.Contains("not larger", Assert.Throws<InvalidDataException>(() =>
                AstcConverter.Convert(npot, HdFixtures.Dds(HdFixtures.Pattern(64, 32, 5, true), false), s, encoder)).Message);
        }

        /// <summary>A synthetic ASTC pack (files/ with Data/ and Gameloft's kind of index) and a PC install holding two of its textures larger.</summary>
        private sealed class Mirror
        {
            public string Files;
            public string Pc;
            public HdIndex Index;
            public long Bytes;
            public int Count;
            public string[] Replaced;
        }

        private static Mirror MakeMirror(Astcenc encoder, string dir)
        {
            var m = new Mirror { Files = Path.Combine(dir, "mirror", "files"), Pc = Path.Combine(dir, "pc", "Data") };
            Directory.CreateDirectory(m.Pc);
            var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            // Two textures: a 64 -> 128 chain and a 1024 -> 2048 one whose 4x4 replacement passes 2 MiB (becomes split).
            var rows = new StringBuilder("level\tPort\tMap01\n");
            int n = 0;
            foreach ((uint hash, int size, int levels) in new[] { (0x0019921au, 64, 7), (0x1abcdefu, 1024, 11) })
            {
                RgbaImage pc = HdFixtures.Pattern(size * 2, size * 2, n + 7, n == 0);
                byte[] original = AstcOriginal(encoder, pc.HalfSize(), levels);
                byte[] dds = HdFixtures.Dds(pc, true);
                string hex = hash.ToString("x", CultureInfo.InvariantCulture);
                data["Data/" + hex.Substring(0, 1) + "/" + hex.Substring(1) + ".f"] = original;
                File.WriteAllBytes(Path.Combine(m.Pc, hash.ToString(CultureInfo.InvariantCulture) + ".f"), dds);
                rows.Append("tex\t").Append(hash.ToString("x8")).Append("\tsurfaces\tPort\tktx\t").Append(original.Length).Append('\t').Append(size).Append('\t').Append(size)
                    .Append('\t').Append(levels).Append('\t').Append(dds.Length).Append('\t').Append(size * 2).Append('\t').Append(size * 2).Append('\t').Append(levels + 1).Append('\n');
                n++;
            }
            m.Replaced = data.Keys.ToArray();
            data["Data/MCVS.game"] = Encoding.ASCII.GetBytes("game");
            var rnd = new Random(5);
            var big = new byte[2 * PackIndex.SplitSize + 777];
            rnd.NextBytes(big);
            data["Data/a/4309cd.f"] = big;
            data["Data/Config/EngineConfig.json"] = Encoding.ASCII.GetBytes("{}");
            var entries = new List<PackEntry>();
            foreach (KeyValuePair<string, byte[]> f in data.OrderBy(f => f.Key.Substring(0, f.Key.LastIndexOf('/')), StringComparer.Ordinal).ThenBy(f => f.Key, StringComparer.Ordinal))
            {
                string path = Path.Combine(m.Files, f.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, f.Value);
                int[] pieces = PackIndex.PieceSizes(f.Value.Length);
                var crcs = new List<uint>();
                long o = 0;
                foreach (int p in pieces)
                {
                    crcs.Add(Crc32.Update(0, f.Value, (int)o, p));
                    o += p;
                }
                // Gameloft's kind of index: real CRCs and non-zero archive offsets and ZIP sizes.
                var like = pieces.Select((p, i) => new PackEntry { Offset = 1000 + i, ZipSize = p / 2 + 40 }).ToList();
                entries.AddRange(PackIndex.EntriesFor(f.Key, f.Value.Length, crcs, false, like));
                m.Bytes += f.Value.Length;
                m.Count++;
            }
            string indexPath = Path.Combine(m.Files, "McvsOffline", "install", "pack.info");
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath));
            File.WriteAllBytes(indexPath, PackIndex.Write(entries).Raw);
            m.Index = HdIndex.Parse(rows.ToString());
            return m;
        }

        [Fact]
        public void TheWholePackIsWrittenAsPartsWithAnIndexTheHostAccepts()
        {
            Astcenc encoder = Encoder();
            if (encoder == null) return;
            string dir = TestEnvironment.NewTempDir("astc-pack");
            Mirror m = MakeMirror(encoder, dir);
            string outDir = Path.Combine(dir, "out");
            var options = new AstcOptions
            {
                AstcFiles = m.Files, PcData = m.Pc, Output = outDir, Encoder = encoder, Index = m.Index, Categories = new[] { HdIndex.Surfaces },
                Settings = AstcPackBuilder.ParseSettings("surfaces=4x4", "fastest", AstcPackBuilder.DefaultSettings()),
                PartLimit = 8L * 1024 * 1024, Threads = 2, EncodeOnly = true,
            };
            var log = new StringWriter();
            AstcPackBuilder.Build(options, log);
            Assert.Contains("0 textures already finished", log.ToString());
            Assert.Empty(Directory.GetFiles(outDir, "*.zip"));

            // The second run resumes: nothing is encoded again; then the parts.
            options.EncodeOnly = false;
            log = new StringWriter();
            AstcPackSummary summary = AstcPackBuilder.Build(options, log);
            output.WriteLine(log.ToString());
            Assert.Contains("2 textures already finished", log.ToString());
            Assert.Equal(2, summary.Replaced);
            Assert.Equal(m.Count, summary.Files);
            Assert.True(summary.Parts.Count >= 2);
            Assert.False(Directory.Exists(Path.Combine(outDir, "work")));

            // Every part is a stored zip below the limit; SHA256SUMS.txt lists them.
            string[] sums = File.ReadAllLines(Path.Combine(outDir, "SHA256SUMS.txt"));
            Assert.Equal(summary.Parts.Count, sums.Length);
            string root = Path.Combine(dir, "phone");
            foreach (string part in summary.Parts)
            {
                Assert.True(new FileInfo(part).Length < options.PartLimit);
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(part))).ToLowerInvariant();
                Assert.Contains(hash + "  " + Path.GetFileName(part), sums);
                using (ZipArchive zip = ZipFile.OpenRead(part))
                {
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        Assert.Equal(e.Length, e.CompressedLength);   // stored
                        Assert.True(e.FullName.StartsWith("files/", StringComparison.Ordinal) || e.FullName == "README.txt", e.FullName);
                    }
                }
                ZipFile.ExtractToDirectory(part, root, true);
            }
            using (ZipArchive last = ZipFile.OpenRead(summary.Parts[summary.Parts.Count - 1]))
            {
                Assert.NotNull(last.GetEntry("files/McvsOffline/install/pack.info"));
                Assert.NotNull(last.GetEntry("files/McvsOffline/install/replaced.tsv"));
                Assert.NotNull(last.GetEntry("README.txt"));
            }

            // Unchanged files are Gameloft's bytes; replaced ones are ASTC 4x4 at the PC size.
            string files = Path.Combine(root, "files");
            Assert.Equal(File.ReadAllBytes(Path.Combine(m.Files, "Data", "a", "4309cd.f")), File.ReadAllBytes(Path.Combine(files, "Data", "a", "4309cd.f")));
            Ktx big = Ktx.Parse(Gcbf.Decode(File.ReadAllBytes(Path.Combine(files, "Data", "1", "abcdef.f")), true));
            Assert.Equal(0x93B0u, big.GlInternalFormat);
            Assert.Equal(2048, big.Width);
            Assert.Equal(12, big.MipLevels);

            // The host: a fresh install with the pushed files/ (no installer index yet) gets the shipped index, as the ASTC pack.
            string internalDir = Path.Combine(dir, "internal");
            Directory.CreateDirectory(internalDir);
            var loc = new InstallerDataLocations(internalDir, files);
            var known = new[] { new InstallerData.KnownPack("ASTC", m.Count, m.Bytes), new InstallerData.KnownPack("ETC", m.Count, m.Bytes + 1) };
            var logger = new Logger();
            var lines = new MemoryLogSink(LogLevel.Debug, 1000);
            logger.AddSink(lines);
            var prefs = new MapPrefs();
            InstallerDataResult result = InstallerData.Prepare(loc, prefs, new InstallerDataOptions { KnownPacks = known }, logger.For("install"), 1);
            output.WriteLine(string.Join("\n", lines.Snapshot().Select(e => e.Message)));
            Assert.Equal("installer data: index restored from the saved index (ASTC with 2 replaced files, " + m.Count + " files, " +
                         PackIndex.Parse(File.ReadAllBytes(loc.SavedIndex)).Entries.Count + " entries)", result.Summary);
            Assert.Equal("ASTC", prefs.GetString(InstallerData.DataTypeKey));
            PackIndex shipped = PackIndex.Parse(File.ReadAllBytes(loc.PackInfo));
            Assert.True(InstallerData.Check(shipped, files).Complete);
            Assert.True(shipped.EntriesOf("Data/1/abcdef.f").Count > 1);   // grew past 2 MiB: split, every piece in the journal
            Assert.Contains(".\\\\Data\\1abcdef.f.split_0002", File.ReadAllText(loc.Journal));
            Assert.Equal(0, InstallerData.VerifyContent(loc, result.IndexToVerify, logger.For("install"), null, 0));
        }

        private sealed class MapPrefs : IInstallerPrefs
        {
            private readonly Dictionary<string, object> values = new Dictionary<string, object>();

            public string GetString(string key) => values.TryGetValue(key, out object v) ? v as string : null;

            public int GetInt(string key, int defaultValue) => values.TryGetValue(key, out object v) && v is int i ? i : defaultValue;

            public bool SetString(string key, string value)
            {
                values[key] = value;
                return true;
            }

            public bool SetInt(string key, int value)
            {
                values[key] = value;
                return true;
            }
        }

        // ------------------------------------------------------------------ the real pack (MCVS_ASTC_HD_OUT)

        /// <summary>
        /// The pack astc-pack wrote from Gameloft's real ASTC pack and the Steam install: every part matches SHA256SUMS.txt and
        /// stays below 380 MiB; the files/ tree it unpacks to is accepted by the host's own InstallerData as the complete ASTC
        /// install, on a fresh install and over the ETC index the owner's phone has; the host's background CRC check passes on
        /// every replaced file, and every other entry's CRC32 is Gameloft's.
        /// </summary>
        [Fact]
        public void TheRealPackIsACompleteAstcInstallForTheHost()
        {
            string outDir = Environment.GetEnvironmentVariable("MCVS_ASTC_HD_OUT");
            if (string.IsNullOrEmpty(outDir) || !Directory.Exists(outDir))
            {
                output.WriteLine("MCVS_ASTC_HD_OUT (the folder astc-pack wrote) is not set: skipped");
                return;
            }
            string[] parts = Directory.GetFiles(outDir, AstcPackBuilder.PartPrefix + "*.zip").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(parts);
            string[] sums = File.ReadAllLines(Path.Combine(outDir, "SHA256SUMS.txt"));
            Assert.Equal(parts.Length, sums.Length);
            string dir = TestEnvironment.NewTempDir("astc-real");
            string files = Path.Combine(dir, "files");
            var entries = new Dictionary<string, (string part, long length)>(StringComparer.Ordinal);
            try
            {
                // 1. Parts: checksums, size limit, stored entries. The tree is laid out with sparse files of the entries' sizes
                //    (the installer's size check), plus the real McvsOffline/install files.
                foreach (string part in parts)
                {
                    Assert.True(new FileInfo(part).Length < AstcPackBuilder.DefaultPartLimit, part);
                    using (FileStream s = File.OpenRead(part))
                    {
                        string hash = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
                        Assert.Contains(hash + "  " + Path.GetFileName(part), sums);
                    }
                    using (ZipArchive zip = ZipFile.OpenRead(part))
                    {
                        foreach (ZipArchiveEntry e in zip.Entries)
                        {
                            Assert.Equal(e.Length, e.CompressedLength);
                            if (e.FullName == "README.txt") continue;
                            Assert.StartsWith("files/", e.FullName);
                            string rel = e.FullName.Substring(6);
                            Assert.False(entries.ContainsKey(rel), rel + " is in two parts");
                            entries[rel] = (part, e.Length);
                            string path = Path.Combine(files, rel.Replace('/', Path.DirectorySeparatorChar));
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            if (rel.StartsWith("McvsOffline/", StringComparison.Ordinal))
                            {
                                e.ExtractToFile(path, true);
                                continue;
                            }
                            using (var f = new FileStream(path, FileMode.Create, FileAccess.Write)) f.SetLength(e.Length);
                        }
                    }
                }
                PackIndex index = PackIndex.Parse(File.ReadAllBytes(Path.Combine(files, "McvsOffline", "install", "pack.info")));
                ReplacedFiles replaced = ReplacedFiles.Parse(File.ReadAllText(Path.Combine(files, "McvsOffline", "install", "replaced.tsv")));
                output.WriteLine(parts.Length + " parts, " + index.LogicalFiles + " files, " + index.LogicalBytes + " bytes, " + replaced.Count + " replaced");
                Assert.Equal(15747, index.LogicalFiles);
                Assert.Equal(index.LogicalFiles, entries.Keys.Count(k => k.StartsWith("Data/", StringComparison.Ordinal)));
                Assert.True(replaced.Count > 2000, replaced.Count + " replaced");
                Assert.True(InstallerData.Check(index, files).Complete, InstallerData.Check(index, files).Describe());

                // 2. A fresh install (the files/ tree pushed, the installer has no index): restored as the complete ASTC pack.
                string expected = "ASTC with " + replaced.Count + " replaced files, 15747 files, " + index.Entries.Count + " entries";
                (InstallerDataResult fresh, MapPrefs freshPrefs, InstallerDataLocations loc) = Prepare(dir, "fresh", files, null);
                Assert.Equal("installer data: index restored from the saved index (" + expected + ")", fresh.Summary);
                Assert.Equal("ASTC", freshPrefs.GetString(InstallerData.DataTypeKey));
                Assert.Equal(index.Raw, File.ReadAllBytes(loc.PackInfo));

                // 3. The owner's phone: the installer still has the ETC index (gl_ver set); the pushed ASTC index replaces it.
                string etcIndex = EtcIndexPath();
                if (etcIndex != null)
                {
                    File.WriteAllBytes(Path.Combine(files, "McvsOffline", "install", "pack.info"), index.Raw);
                    (InstallerDataResult over, MapPrefs overPrefs, InstallerDataLocations overLoc) = Prepare(dir, "over-etc", files, File.ReadAllBytes(etcIndex));
                    Assert.Equal("installer data: index switched to the saved index (" + expected + ")", over.Summary);
                    Assert.Equal("ASTC", overPrefs.GetString(InstallerData.DataTypeKey));
                    Assert.Equal(index.Raw, File.ReadAllBytes(overLoc.PackInfo));
                    Assert.NotNull(over.IndexToVerify);
                }
                else
                {
                    output.WriteLine("no ETC pack.info next to MCVS_DATAPACK: the switch from the ETC index is not checked");
                }

                // 4. Content: every entry's CRC32 from the bytes in the parts, and the host's own background check on the
                //    replaced files (extracted part by part, then made sparse again).
                var byPart = entries.Where(e => e.Key.StartsWith("Data/", StringComparison.Ordinal)).GroupBy(e => e.Value.part);
                var logger = new Logger();
                var lines = new MemoryLogSink(LogLevel.Debug, 1000);
                logger.AddSink(lines);
                int checkedFiles = 0;
                foreach (var group in byPart)
                {
                    var replacedHere = new List<string>();
                    using (ZipArchive zip = ZipFile.OpenRead(group.Key))
                    {
                        foreach (var e in group)
                        {
                            ZipArchiveEntry z = zip.GetEntry("files/" + e.Key);
                            byte[] data;
                            using (Stream s = z.Open())
                            using (var ms = new MemoryStream((int)z.Length))
                            {
                                s.CopyTo(ms);
                                data = ms.ToArray();
                            }
                            long offset = 0;
                            foreach (PackEntry p in index.EntriesOf(e.Key))
                            {
                                Assert.Equal((uint)p.Crc, Crc32.Update(0, data, (int)offset, p.Size));
                                offset += p.Size;
                            }
                            Assert.Equal(data.Length, offset);
                            checkedFiles++;
                            if (replaced.TryGetOriginalSize(e.Key, data.Length, out long _))
                            {
                                File.WriteAllBytes(Path.Combine(files, e.Key.Replace('/', Path.DirectorySeparatorChar)), data);
                                replacedHere.Add(e.Key);
                            }
                        }
                    }
                    if (replacedHere.Count == 0) continue;
                    var subset = PackIndex.Write(replacedHere.SelectMany(index.EntriesOf).ToList());
                    var vloc = new InstallerDataLocations(Path.Combine(dir, "verify-internal"), files);
                    Assert.Equal(0, InstallerData.VerifyContent(vloc, subset, logger.For("install"), null, 0));
                    foreach (string rel in replacedHere)
                    {
                        string path = Path.Combine(files, rel.Replace('/', Path.DirectorySeparatorChar));
                        long length = new FileInfo(path).Length;
                        File.Delete(path);
                        using (var f = new FileStream(path, FileMode.Create, FileAccess.Write)) f.SetLength(length);
                    }
                }
                Assert.Equal(15747, checkedFiles);
                output.WriteLine(string.Join("\n", lines.Snapshot().Select(e => e.Message).Where(m => m.Contains("background check"))));
            }
            finally
            {
                HdFixtures.DeleteDir(dir);
            }
        }

        private static (InstallerDataResult, MapPrefs, InstallerDataLocations) Prepare(string dir, string name, string files, byte[] installerIndex)
        {
            string internalDir = Path.Combine(dir, name + "-internal");
            Directory.CreateDirectory(Path.Combine(internalDir, "prefs"));
            var loc = new InstallerDataLocations(internalDir, files);
            if (installerIndex != null)
            {
                File.WriteAllBytes(loc.PackInfo, installerIndex);
                File.WriteAllText(loc.GlVer, InstallerData.ApkVersionCode);
            }
            // The journal and InsTime of an earlier run go, so each case starts as the owner's phone does after the README's steps.
            if (File.Exists(loc.Journal)) File.Delete(loc.Journal);
            var prefs = new MapPrefs();
            if (installerIndex != null) prefs.SetString(InstallerData.DataTypeKey, "ETC");
            var logger = new Logger();
            var lines = new MemoryLogSink(LogLevel.Debug, 1000);
            logger.AddSink(lines);
            InstallerDataResult result = InstallerData.Prepare(loc, prefs, new InstallerDataOptions(), logger.For("install"), 1);
            return (result, prefs, loc);
        }

        /// <summary>The ETC pack's Gameloft index (next to the ETC mirror the tests use), or null.</summary>
        private static string EtcIndexPath()
        {
            string data = Environment.GetEnvironmentVariable("MCVS_DATAPACK");
            if (string.IsNullOrEmpty(data)) return null;
            foreach (string candidate in new[] { Path.Combine(data, "..", "..", "pack.info"), Path.Combine(data, "..", "McvsOffline", "install", "pack.info") })
            {
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            return null;
        }
    }
}
