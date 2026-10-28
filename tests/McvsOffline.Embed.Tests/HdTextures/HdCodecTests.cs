using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using McvsOffline.Server.Core.Content;
using McvsOffline.Tools.HdTextures;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.HdTextures
{
    /// <summary>
    /// The codecs of tools/hd-textures on synthetic data: LZ4 and GCBF (read back by the server's decoder), BC1/BC3 decoding,
    /// ETC1 decoding against hand-computed blocks and encoding quality, the zip writers.
    /// </summary>
    public class HdCodecTests
    {
        private readonly ITestOutputHelper output;

        public HdCodecTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        public static IEnumerable<object[]> Lz4Inputs()
        {
            foreach (int n in new[] { 0, 1, 5, 12, 13, 64, 1000, 65536, 70000, 300000 })
            {
                var random = new byte[n];
                new Random(n).NextBytes(random);
                yield return new object[] { "random " + n, random };
                yield return new object[] { "zeros " + n, new byte[n] };
                var text = new byte[n];
                for (int i = 0; i < n; i++) text[i] = (byte)"the quick brown fox jumps over the lazy dog "[i % 44];
                yield return new object[] { "text " + n, text };
            }
            var mixed = new byte[200000];
            var r = new Random(7);
            for (int i = 0; i < mixed.Length; i++) mixed[i] = (byte)(i % 3000 < 1500 ? r.Next(256) : (i / 7) & 0xFF);
            yield return new object[] { "mixed", mixed };
        }

        [Theory]
        [MemberData(nameof(Lz4Inputs))]
        public void Lz4BlocksRoundTripThroughTheServersDecoder(string name, byte[] data)
        {
            byte[] packed = Lz4Compressor.Compress(data, 0, data.Length);
            var back = new byte[data.Length];
            Lz4Block.Decompress(packed, 0, packed.Length, back, 0, data.Length);
            Assert.Equal(data, back);
            if (name.StartsWith("zeros", StringComparison.Ordinal) && data.Length >= 1000) Assert.True(packed.Length < data.Length / 50, name + ": " + packed.Length);
            byte[] literals = GcbfWriter.Lz4Literals(data, 0, data.Length);
            Lz4Block.Decompress(literals, 0, literals.Length, back, 0, data.Length);
            Assert.Equal(data, back);
        }

        [Fact]
        public void GcbfUsesThePacksEightBlockSplitAndDecodes()
        {
            foreach (int n in new[] { 0, 7, 8, 43856, 699224, 2796380 })
            {
                var data = new byte[n];
                new Random(n).NextBytes(data);
                for (int i = 0; i < n / 2; i++) data[i] = (byte)(i / 64);
                byte[] gcbf = GcbfWriter.Encode(data);
                Assert.True(Gcbf.IsGcbf(gcbf));
                Assert.Equal(data, Gcbf.Decode(gcbf, true));
                uint entries = Ktx.U32(gcbf, 4);
                Assert.Equal(n >= 8 ? 9u : 2u, entries);
                if (n >= 8)
                {
                    // Seek table: (0, header end), then every n / 8 bytes, the last block taking the remainder; sentinel (n, file size).
                    for (int i = 0; i < 8; i++) Assert.Equal((uint)(i * (n / 8)), Ktx.U32(gcbf, 8 + 8 * i));
                    Assert.Equal((uint)n, Ktx.U32(gcbf, 8 + 8 * 8));
                    Assert.Equal((uint)gcbf.Length, Ktx.U32(gcbf, 8 + 8 * 8 + 4));
                }
            }
        }

        [Fact]
        public void Bc1AndBc3BlocksDecodeAsTheSpecificationSays()
        {
            // BC1, four-colour mode (c0 > c1): red 0xF800 and blue 0x001F; indices 0, 1, 2, 3 in the first row.
            byte[] bc1 = { 0x00, 0xF8, 0x1F, 0x00, 0xE4, 0x00, 0x00, 0x00 };
            RgbaImage four = BcDecoder.DecodeBc1(bc1, 4, 4);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, four.Pixels.Take(4));
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, four.Pixels.Skip(4).Take(4));
            Assert.Equal(new byte[] { 170, 0, 85, 255 }, four.Pixels.Skip(8).Take(4));
            Assert.Equal(new byte[] { 85, 0, 170, 255 }, four.Pixels.Skip(12).Take(4));
            // BC1, three-colour mode (c0 <= c1): index 2 is the mean, index 3 transparent black.
            byte[] threeColour = { 0x1F, 0x00, 0x00, 0xF8, 0xE4, 0x00, 0x00, 0x00 };
            RgbaImage three = BcDecoder.DecodeBc1(threeColour, 4, 4);
            Assert.Equal(new byte[] { 127, 0, 127, 255 }, three.Pixels.Skip(8).Take(4));
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, three.Pixels.Skip(12).Take(4));
            // BC3: alpha block 8-value mode (a0 > a1) and 6-value mode with 0 and 255; colour block always four-colour.
            var palette = new byte[8];
            BcDecoder.AlphaPalette(200, 60, palette);
            Assert.Equal(new byte[] { 200, 60, 180, 160, 140, 120, 100, 80 }, palette);
            BcDecoder.AlphaPalette(60, 200, palette);
            Assert.Equal(new byte[] { 60, 200, 88, 116, 144, 172, 0, 255 }, palette);
            byte[] bc3 = new byte[16];
            bc3[0] = 200;
            bc3[1] = 60;
            bc3[2] = 0x08;   // pixel 0 index 0, pixel 1 index 1
            Array.Copy(threeColour, 0, bc3, 8, 8);
            RgbaImage withAlpha = BcDecoder.DecodeBc3(bc3, 4, 4);
            Assert.Equal(200, withAlpha.Pixels[3]);
            Assert.Equal(60, withAlpha.Pixels[7]);
            Assert.Equal(new byte[] { 85, 0, 170 }, withAlpha.Pixels.Skip(8).Take(3));    // four-colour despite c0 <= c1
            Assert.Equal(new byte[] { 170, 0, 85 }, withAlpha.Pixels.Skip(12).Take(3));
            // Partial blocks: a 2x2 image reads the top-left of the block.
            Assert.Equal(16, BcDecoder.DecodeBc1(bc1, 2, 2).Pixels.Length);
        }

        [Fact]
        public void DdsHeadersAndMipLevelsAreRead()
        {
            RgbaImage image = HdFixtures.Pattern(64, 32, 1, false);
            Dds dds = Dds.Parse(HdFixtures.Dds(image, true));
            Assert.Equal("DXT5", dds.FourCc);
            Assert.Equal(64, dds.Width);
            Assert.Equal(32, dds.Height);
            Assert.Equal(7, dds.Levels.Count);   // 64x32 ... 1x1
            Assert.Equal(64 * 32, dds.Levels[0].Length);
            Assert.Equal(16, dds.Levels[6].Length);
            Assert.True(RgbaImage.Psnr(dds.Decode(0), image, 0, 4) > 25);
            Dds one = Dds.Parse(HdFixtures.Dds(image, false, 1));
            Assert.Single(one.Levels);
            Assert.True(one.IsBc1);
            Assert.Throws<InvalidDataException>(() => Dds.Parse(new byte[64]));
            byte[] dxt3 = HdFixtures.Dds(image, true);
            dxt3[87] = (byte)'3';
            Assert.Contains("DXT1 and DXT5 only", Assert.Throws<InvalidDataException>(() => Dds.Parse(dxt3)).Message);
        }

        [Fact]
        public void Etc1BlocksDecodeAsComputedByHand()
        {
            // Differential mode, no flip: base 1 = (10, 20, 0) in 5 bits = (82, 165, 0); delta (+2, -1, +3) gives (99, 156, 24).
            // Tables 1 (5/17) and 6 (33/106). Pixel (0,0) index 3 (-17), (3,3) index 1 (+106), the rest index 0 (+small).
            uint hi = (uint)(10 << 27 | 2 << 24 | 20 << 19 | (-1 & 7) << 16 | 0 << 11 | 3 << 8 | 1 << 5 | 6 << 2 | 2);
            uint lo = (1u << 16) | 1u        // pixel (0,0), bit 0: msb 1, lsb 1
                    | (1u << 15);            // pixel (3,3), bit 15: lsb 1
            byte[] block = { (byte)(hi >> 24), (byte)(hi >> 16), (byte)(hi >> 8), (byte)hi, (byte)(lo >> 24), (byte)(lo >> 16), (byte)(lo >> 8), (byte)lo };
            var rgba = new byte[64];
            Etc1.DecodeBlock(block, 0, rgba);
            Assert.Equal(new byte[] { 65, 148, 0, 255 }, rgba.Take(4));                      // (0,0): 82-17, 165-17, clamp(0-17)
            Assert.Equal(new byte[] { 87, 170, 5, 255 }, rgba.Skip(4).Take(4));              // (1,0): +5
            Assert.Equal(new byte[] { 132, 189, 57, 255 }, rgba.Skip(8).Take(4));            // (2,0): second subblock, +33
            Assert.Equal(new byte[] { 205, 255, 130, 255 }, rgba.Skip(60).Take(4));          // (3,3): +106, green clamps

            // Individual mode with flip: top subblock (4-bit 15, 0, 8) = (255, 0, 136), bottom (0, 15, 1) = (0, 255, 17); table 0.
            hi = 15u << 28 | (uint)(0 << 24 | 0 << 20 | 15 << 16 | 8 << 12 | 1 << 8 | 0 << 5 | 0 << 2 | 0 | 1);
            block = new byte[] { (byte)(hi >> 24), (byte)(hi >> 16), (byte)(hi >> 8), (byte)hi, 0, 0, 0, 0 };
            Etc1.DecodeBlock(block, 0, rgba);
            Assert.Equal(new byte[] { 255, 2, 138, 255 }, rgba.Take(4));                     // y = 0: top, +2
            Assert.Equal(new byte[] { 2, 255, 19, 255 }, rgba.Skip(8 * 4).Take(4));          // y = 2: bottom
        }

        [Fact]
        public void TheEncoderNeverLosesToTheMeanColourEncodingAndKeepsDeltasInRange()
        {
            var rnd = new Random(11);
            var encoder = new Etc1Encoder();
            var block = new byte[8];
            var decoded = new byte[64];
            for (int n = 0; n < 3000; n++)
            {
                // Mixed content: smooth, noisy, saturated (clamping), two-colour edges.
                int kind = n % 4;
                int br = rnd.Next(256), bg = rnd.Next(256), bb = rnd.Next(256);
                for (int i = 0; i < 16; i++)
                {
                    int x = i % 4, y = i / 4;
                    int[] c = kind == 0 ? new[] { br + x * 3, bg + y * 2, bb }
                            : kind == 1 ? new[] { rnd.Next(256), rnd.Next(256), rnd.Next(256) }
                            : kind == 2 ? new[] { rnd.Next(2) * 255, rnd.Next(2) * 255, br }
                            : (x + y < 3 ? new[] { br, bg, bb } : new[] { 255 - br, bb, bg });
                    for (int ch = 0; ch < 3; ch++) encoder.Input[i * 3 + ch] = Math.Max(0, Math.Min(255, c[ch]));
                }
                encoder.EncodeBlock(block, 0);
                Etc1.DecodeBlock(block, 0, decoded);
                int error = 0;
                for (int i = 0; i < 16; i++)
                {
                    for (int ch = 0; ch < 3; ch++) error += Sq(decoded[i * 4 + ch] - encoder.Input[i * 3 + ch]);
                }
                Assert.True(error <= MeanColourError(encoder.Input), "block " + n + ": " + error + " > " + MeanColourError(encoder.Input));
                if ((block[3] & 2) != 0)
                {
                    // Differential: base + delta stays inside 0..31 (decoders differ on overflow).
                    for (int ch = 0; ch < 3; ch++)
                    {
                        int q = block[ch] >> 3, d = block[ch] & 7;
                        int second = q + (d >= 4 ? d - 8 : d);
                        Assert.InRange(second, 0, 31);
                    }
                }
            }
        }

        [Fact]
        public void EncodedImagesComeBackClose()
        {
            RgbaImage gradient = new RgbaImage(64, 64);
            for (int i = 0; i < gradient.Pixels.Length; i += 4)
            {
                int x = (i / 4) % 64, y = (i / 4) / 64;
                gradient.Pixels[i] = (byte)(x * 4);
                gradient.Pixels[i + 1] = (byte)(y * 4);
                gradient.Pixels[i + 2] = (byte)((x + y) * 2);
                gradient.Pixels[i + 3] = 255;
            }
            double g = RgbaImage.Psnr(Etc1.Decode(Etc1.Encode(gradient, 2), 0, 64, 64), gradient, 0, 3);
            RgbaImage pattern = HdFixtures.Pattern(128, 96, 3, true);
            double p = RgbaImage.Psnr(Etc1.Decode(Etc1.Encode(pattern, 4), 0, 128, 96), pattern, 0, 3);
            RgbaImage grey = HdFixtures.Pattern(64, 64, 4, false).AlphaAsGrey();
            double a = RgbaImage.Psnr(Etc1.Decode(Etc1.Encode(grey, 1), 0, 64, 64), grey, 0, 1);
            output.WriteLine("PSNR gradient " + g.ToString("0.0") + " dB, pattern " + p.ToString("0.0") + " dB, grey ramp " + a.ToString("0.0") + " dB");
            Assert.True(g > 38, "gradient " + g);
            Assert.True(p > 32, "pattern " + p);
            Assert.True(a > 40, "grey " + a);
            // Odd sizes: edge blocks repeat the last row/column; one thread and many give the same bytes.
            RgbaImage odd = HdFixtures.Pattern(13, 7, 5, true);
            byte[] once = Etc1.Encode(odd, 1);
            Assert.Equal(Etc1.DataSize(13, 7), once.Length);
            Assert.Equal(once, Etc1.Encode(odd, 4));
            double oddPsnr = RgbaImage.Psnr(Etc1.Decode(once, 0, 13, 7), odd, 0, 3);
            Assert.True(oddPsnr > 20, "odd " + oddPsnr);   // steep per-channel ramps over 7 rows: hard for ETC1's shared modifier
            // Flat colours are exact where ETC1 can be.
            RgbaImage flat = TextureConverter.Solid(8, 8, 255);
            Assert.Equal(double.PositiveInfinity, RgbaImage.Psnr(Etc1.Decode(Etc1.Encode(flat, 1), 0, 8, 8), flat, 0, 3));
        }

        [Fact]
        public void ZipWritersMakeTheTwoShapes()
        {
            var files = new[]
            {
                new KeyValuePair<string, byte[]>("SPLIT", new byte[4]),
                new KeyValuePair<string, byte[]>("rgb.ktx", Enumerable.Range(0, 1001).Select(i => (byte)i).ToArray()),
                new KeyValuePair<string, byte[]>("alpha.ktx", new byte[] { 1, 2, 3 }),
            };
            foreach (bool align in new[] { false, true })
            {
                byte[] zip = StoredZipWriter.Build(align, files);
                using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
                {
                    Assert.Equal(files.Select(f => f.Key), archive.Entries.Select(e => e.FullName));
                    foreach (ZipArchiveEntry e in archive.Entries)
                    {
                        Assert.Equal(e.Length, e.CompressedLength);
                        using (Stream s = e.Open())
                        using (var ms = new MemoryStream())
                        {
                            s.CopyTo(ms);
                            Assert.Equal(files.First(f => f.Key == e.FullName).Value, ms.ToArray());
                        }
                    }
                }
                int p = 0;
                foreach (KeyValuePair<string, byte[]> f in files)
                {
                    Assert.Equal(0x04034b50u, Ktx.U32(zip, p));
                    Assert.Equal(20, zip[p + 4]);                                     // version 2.0, as Gameloft's
                    int nameLength = zip[p + 26] | (zip[p + 27] << 8), extra = zip[p + 28] | (zip[p + 29] << 8);
                    int data = p + 30 + nameLength + extra;
                    if (align) Assert.Equal(0, data % 4);
                    else Assert.Equal(0, extra);                                      // the split zips have no extra field
                    Assert.Equal(Crc32.Compute(f.Value), Ktx.U32(zip, p + 14));
                    p = data + f.Value.Length;
                }
            }
            // The split shape is byte-for-byte what the pack's zips look like: SPLIT's data at 35, rgb.ktx's header at 39.
            byte[] split = StoredZipWriter.Build(false, files);
            Assert.Equal(new byte[4], split.Skip(35).Take(4));
            Assert.Equal(0x04034b50u, Ktx.U32(split, 39));
        }

        /// <summary>Error of the simplest ETC1 encoding: differential mode, subblock means, best table (both flips).</summary>
        private static int MeanColourError(int[] input)
        {
            int best = int.MaxValue;
            for (int flip = 0; flip < 2; flip++)
            {
                int[] q0 = Mean5(input, Etc1.Subblocks[flip][0]), q1 = Mean5(input, Etc1.Subblocks[flip][1]);
                for (int c = 0; c < 3; c++) q1[c] = Math.Max(q0[c] - 4, Math.Min(q0[c] + 3, q1[c]));
                int e = BestTable(input, Etc1.Subblocks[flip][0], q0) + BestTable(input, Etc1.Subblocks[flip][1], q1);
                best = Math.Min(best, e);
            }
            return best;
        }

        private static int[] Mean5(int[] input, int[] idx)
        {
            var q = new int[3];
            for (int c = 0; c < 3; c++)
            {
                double m = idx.Sum(i => input[i * 3 + c]) / 8.0;
                int bestQ = 0;
                for (int v = 1; v < 32; v++)
                {
                    if (Math.Abs(Etc1.Expand5(v) - m) < Math.Abs(Etc1.Expand5(bestQ) - m)) bestQ = v;
                }
                q[c] = bestQ;
            }
            return q;
        }

        private static int BestTable(int[] input, int[] idx, int[] q)
        {
            int best = int.MaxValue;
            for (int t = 0; t < 8; t++)
            {
                int total = 0;
                foreach (int i in idx)
                {
                    int pixel = int.MaxValue;
                    foreach (int m in Etc1.Modifiers[t])
                    {
                        int e = 0;
                        for (int c = 0; c < 3; c++) e += Sq(Etc1.Clamp(Etc1.Expand5(q[c]) + m) - input[i * 3 + c]);
                        pixel = Math.Min(pixel, e);
                    }
                    total += pixel;
                }
                best = Math.Min(best, total);
            }
            return best;
        }

        private static int Sq(int v) => v * v;
    }
}
