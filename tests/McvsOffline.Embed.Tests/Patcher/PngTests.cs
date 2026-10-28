using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Resources;
using McvsOffline.Patcher.Zip;
using Xunit;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>The art patch's PNG reader/validator, writer and resampler (app-icon-and-launch-screen.md §4.3, §5.1).</summary>
    public class PngTests
    {
        /// <summary>A PNG assembled chunk by chunk (CRCs computed), for the malformed cases.</summary>
        internal static byte[] Assemble(params (string Type, byte[] Data)[] chunks)
        {
            var output = new MemoryStream();
            output.Write(Png.Signature, 0, Png.Signature.Length);
            foreach (var (type, data) in chunks)
            {
                var head = new byte[8];
                head[0] = (byte)(data.Length >> 24); head[1] = (byte)(data.Length >> 16); head[2] = (byte)(data.Length >> 8); head[3] = (byte)data.Length;
                Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
                output.Write(head, 0, 8);
                output.Write(data, 0, data.Length);
                byte[] crcInput = head.Skip(4).Concat(data).ToArray();
                uint crc = Crc32.Compute(crcInput);
                output.Write(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc }, 0, 4);
            }
            return output.ToArray();
        }

        internal static byte[] Ihdr(int w, int h, byte depth, byte colorType, byte interlace = 0)
        {
            return new byte[] { (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, depth, colorType, 0, 0, interlace };
        }

        internal static byte[] Zlib(byte[] raw)
        {
            using (var buffer = new MemoryStream())
            {
                using (var z = new ZLibStream(buffer, CompressionLevel.Fastest, true)) z.Write(raw, 0, raw.Length);
                return buffer.ToArray();
            }
        }

        /// <summary>A 2x2 greyscale+alpha image with filter types None and Sub, written by hand.</summary>
        private static byte[] GreyAlpha2x2()
        {
            byte[] raw = { 0, 10, 255, 20, 128, 1, 30, 0, 10, 0 };   // row 0: (10,255) (20,128); row 1 Sub: (30,0) (40,0)
            return Assemble(("IHDR", Ihdr(2, 2, 8, Png.TypeGreyscaleAlpha)), ("tEXt", Encoding.ASCII.GetBytes("Comment\0x")), ("IDAT", Zlib(raw)), ("IEND", new byte[0]));
        }

        [Fact]
        public void DecodesEveryAcceptedColourTypeToRgba()
        {
            Png g = Png.Decode(GreyAlpha2x2());
            Assert.Equal(Png.TypeGreyscaleAlpha, g.ColorType);
            Assert.Equal(new byte[] { 10, 10, 10, 255, 20, 20, 20, 128, 30, 30, 30, 0, 40, 40, 40, 0 }, g.Rgba);
            Assert.True(g.HasTransparency);

            // RGB with a tRNS colour: that colour is transparent.
            byte[] rgb = { 0, 1, 2, 3, 9, 9, 9 };
            Png t = Png.Decode(Assemble(("IHDR", Ihdr(2, 1, 8, Png.TypeRgb)), ("tRNS", new byte[] { 0, 9, 0, 9, 0, 9 }), ("IDAT", Zlib(rgb)), ("IEND", new byte[0])));
            Assert.Equal(new byte[] { 1, 2, 3, 255, 9, 9, 9, 0 }, t.Rgba);

            // Up, Average and Paeth filters through the encoder's own choice on a noisy image, then back.
            var random = new Random(5);
            var pixels = new byte[37 * 23 * 4];
            random.NextBytes(pixels);
            var image = new Png(37, 23, pixels);
            Png back = Png.Decode(image.Encode());
            Assert.Equal(pixels, back.Rgba);
            Assert.Equal(Png.TypeRgba, back.ColorType);

            // Opaque images are written as RGB.
            Png opaque = Png.Solid(9, 0x123456);
            Assert.False(opaque.HasTransparency);
            byte[] file = opaque.Encode();
            Assert.Equal(Png.TypeRgb, Png.ReadHeader(file).ColorType);
            Assert.Equal(opaque.Rgba, Png.Decode(file).Rgba);
            Assert.Equal((byte)0x34, Png.Decode(file).Rgba[1]);
        }

        [Fact]
        public void RefusesDamagedAndUnsupportedFiles()
        {
            byte[] good = GreyAlpha2x2();
            void Refused(byte[] file, string reason)
            {
                var ex = Assert.Throws<InvalidDataException>(() => Png.Decode(file));
                Assert.Contains(reason, ex.Message);
            }

            byte[] flipped = (byte[])good.Clone();
            flipped[good.Length - 20] ^= 1;   // inside the IDAT data
            Refused(flipped, "CRC");
            Refused(good.Take(good.Length - 3).ToArray(), "truncated");
            Refused(good.Concat(new byte[] { 0 }).ToArray(), "after IEND");
            Refused(Encoding.ASCII.GetBytes("GIF89a"), "signature");
            byte[] idat = Zlib(new byte[] { 0, 1 });
            Refused(Assemble(("tEXt", new byte[] { 65, 0 }), ("IHDR", Ihdr(1, 1, 8, 0)), ("IDAT", idat), ("IEND", new byte[0])), "first chunk");
            Refused(Assemble(("IHDR", Ihdr(1, 1, 16, 0)), ("IDAT", idat), ("IEND", new byte[0])), "16-bit");
            Refused(Assemble(("IHDR", Ihdr(1, 1, 8, 3)), ("PLTE", new byte[3]), ("IDAT", idat), ("IEND", new byte[0])), "palette");
            Refused(Assemble(("IHDR", Ihdr(1, 1, 8, 0, 1)), ("IDAT", idat), ("IEND", new byte[0])), "interlaced");
            Refused(Assemble(("IHDR", Ihdr(1, 1, 8, 0)), ("acTL", new byte[8]), ("IDAT", idat), ("IEND", new byte[0])), "APNG");
            Refused(Assemble(("IHDR", Ihdr(1, 1, 8, 0)), ("IDAT", idat), ("tEXt", new byte[] { 65, 0 }), ("IDAT", idat), ("IEND", new byte[0])), "consecutive");
            Refused(Assemble(("IHDR", Ihdr(1, 1, 8, 0)), ("ABCD", new byte[1]), ("IDAT", idat), ("IEND", new byte[0])), "critical chunk ABCD");
            Refused(Assemble(("IHDR", Ihdr(1, 1, 8, 0)), ("IEND", new byte[0])), "no IDAT");
            Refused(Assemble(("IHDR", Ihdr(2, 1, 8, 0)), ("IDAT", idat), ("IEND", new byte[0])), "inflates to 2 bytes, expected 3");
            Refused(Assemble(("IHDR", Ihdr(1, 1, 8, 0)), ("IDAT", Zlib(new byte[] { 7, 1 })), ("IEND", new byte[0])), "filter type 7");
            Refused(Assemble(("IHDR", Ihdr(4096, 4096, 8, 6)), ("IDAT", idat), ("IEND", new byte[0])), "larger than 2048x2048");
            Refused(Assemble(("IHDR", Ihdr(1, 1, 8, 0)), ("IDAT", new byte[] { 1, 2, 3 }), ("IEND", new byte[0])), "zlib");
        }

        [Fact]
        public void DownscaleAveragesCoveredAreaOnPremultipliedAlpha()
        {
            // Opaque red next to transparent green: the colour stays red (premultiplied), alpha halves.
            var two = new Png(2, 2, new byte[] { 255, 0, 0, 255, 0, 255, 0, 0, 255, 0, 0, 255, 0, 255, 0, 0 });
            Assert.Equal(new byte[] { 255, 0, 0, 128 }, two.Downscale(1).Rgba);

            // A uniform image stays uniform at every non-integer ratio of the icon sizes, and alpha 255 stays 255.
            Png solid = Png.Solid(432, 0x10A0F0);
            foreach (int size in new[] { 324, 216, 162, 108, 81 })
            {
                Png s = solid.Downscale(size);
                Assert.Equal(size, s.Width);
                Assert.All(Enumerable.Range(0, size * size), i => Assert.Equal(new byte[] { 0x10, 0xA0, 0xF0, 0xFF }, s.Rgba.Skip(i * 4).Take(4).ToArray()));
            }

            // 3 -> 2: the middle source column is split half/half (coverage weights), so the mean is preserved.
            var row = new byte[3 * 3 * 4];
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                {
                    int i = (y * 3 + x) * 4;
                    row[i] = (byte)(x * 90);
                    row[i + 3] = 255;
                }
            Png half = new Png(3, 3, row).Downscale(2);
            Assert.Equal(30, half.Rgba[0]);              // (0 * 1 + 90 * 0.5) / 1.5
            Assert.Equal(150, half.Rgba[4]);             // (90 * 0.5 + 180 * 1) / 1.5
            Assert.Throws<InvalidOperationException>(() => Png.Solid(10, 0).Downscale(11));
        }

        [Fact]
        public void TheDiscFixtureIsTransparentOutsideTheLogo()
        {
            Png disc = ArtFixtures.Disc(432, 0xFFFFFF, 0.25);
            Assert.True(disc.HasTransparency);
            Assert.Equal((0.0, 0.0), McvsOffline.Patcher.Art.IconArt.OutsideZones(disc));
            var (visible, safe) = McvsOffline.Patcher.Art.IconArt.OutsideZones(ArtFixtures.Disc(432, 0xFFFFFF, 0.45));
            Assert.True(visible > 0 && safe > visible);
        }
    }
}
