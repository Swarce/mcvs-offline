using System;
using System.IO;
using System.Threading.Tasks;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// ETC1 (<c>GL_ETC1_RGB8_OES</c>) decoding and encoding, written from the Khronos specification
    /// (OES_compressed_ETC1_RGB8_texture). A block is 8 bytes, big-endian: two subblocks (2x4 side by side, or 4x2 one above
    /// the other with the flip bit), each with a base colour (RGB444 twice in individual mode, RGB555 plus a 3-bit signed
    /// delta in differential mode), a modifier table (3 bits) and a 2-bit modifier index per pixel, added to all three
    /// channels and clamped.
    /// The block search is <see cref="Etc1Encoder"/> (our own code); its error is the summed squared RGB difference, the
    /// measure PSNR reports.
    /// </summary>
    internal static class Etc1
    {
        public const int BlockBytes = 8;

        /// <summary>Modifier per table (8) and index (0: +a, 1: +b, 2: -a, 3: -b).</summary>
        internal static readonly int[][] Modifiers =
        {
            new[] { 2, 8, -2, -8 }, new[] { 5, 17, -5, -17 }, new[] { 9, 29, -9, -29 }, new[] { 13, 42, -13, -42 },
            new[] { 18, 60, -18, -60 }, new[] { 24, 80, -24, -80 }, new[] { 33, 106, -33, -106 }, new[] { 47, 183, -47, -183 },
        };

        /// <summary>Pixel indices (y * 4 + x) of each subblock: [flip][subblock][8].</summary>
        internal static readonly int[][][] Subblocks =
        {
            new[] { new[] { 0, 1, 4, 5, 8, 9, 12, 13 }, new[] { 2, 3, 6, 7, 10, 11, 14, 15 } },
            new[] { new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, new[] { 8, 9, 10, 11, 12, 13, 14, 15 } },
        };

        public static int DataSize(int width, int height)
        {
            return ((width + 3) / 4) * ((height + 3) / 4) * BlockBytes;
        }

        // ------------------------------------------------------------------ decoding

        /// <summary>Decodes one block into 16 RGBA pixels (y * 4 + x), alpha 255.</summary>
        public static void DecodeBlock(byte[] data, int offset, byte[] rgba)
        {
            uint hi = (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
            uint lo = (uint)(data[offset + 4] << 24 | data[offset + 5] << 16 | data[offset + 6] << 8 | data[offset + 7]);
            bool diff = ((hi >> 1) & 1) != 0;
            bool flip = (hi & 1) != 0;
            int t0 = (int)((hi >> 5) & 7), t1 = (int)((hi >> 2) & 7);
            int r0, g0, b0, r1, g1, b1;
            if (diff)
            {
                int qr = (int)((hi >> 27) & 31), qg = (int)((hi >> 19) & 31), qb = (int)((hi >> 11) & 31);
                int dr = SignExtend3((int)((hi >> 24) & 7)), dg = SignExtend3((int)((hi >> 16) & 7)), db = SignExtend3((int)((hi >> 8) & 7));
                r0 = Expand5(qr);
                g0 = Expand5(qg);
                b0 = Expand5(qb);
                r1 = Expand5((qr + dr) & 31);
                g1 = Expand5((qg + dg) & 31);
                b1 = Expand5((qb + db) & 31);
            }
            else
            {
                r0 = (int)((hi >> 28) & 15) * 17;
                r1 = (int)((hi >> 24) & 15) * 17;
                g0 = (int)((hi >> 20) & 15) * 17;
                g1 = (int)((hi >> 16) & 15) * 17;
                b0 = (int)((hi >> 12) & 15) * 17;
                b1 = (int)((hi >> 8) & 15) * 17;
            }
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    bool second = flip ? y >= 2 : x >= 2;
                    int bit = x * 4 + y;
                    int k = (int)(((lo >> (16 + bit)) & 1) << 1 | ((lo >> bit) & 1));
                    int m = Modifiers[second ? t1 : t0][k];
                    int o = (y * 4 + x) * 4;
                    rgba[o] = Clamp((second ? r1 : r0) + m);
                    rgba[o + 1] = Clamp((second ? g1 : g0) + m);
                    rgba[o + 2] = Clamp((second ? b1 : b0) + m);
                    rgba[o + 3] = 255;
                }
            }
        }

        public static RgbaImage Decode(byte[] data, int offset, int width, int height)
        {
            int bw = (width + 3) / 4, bh = (height + 3) / 4;
            if (data.Length - offset < bw * bh * BlockBytes) throw new InvalidDataException("ETC1 data too short for " + width + "x" + height);
            var image = new RgbaImage(width, height);
            var block = new byte[64];
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    DecodeBlock(data, offset + (by * bw + bx) * BlockBytes, block);
                    for (int y = 0; y < 4 && by * 4 + y < height; y++)
                    {
                        for (int x = 0; x < 4 && bx * 4 + x < width; x++)
                            Buffer.BlockCopy(block, (y * 4 + x) * 4, image.Pixels, ((by * 4 + y) * width + bx * 4 + x) * 4, 4);
                    }
                }
            }
            return image;
        }

        // ------------------------------------------------------------------ encoding

        /// <summary>Encodes the RGB of <paramref name="image"/> (alpha ignored); block rows run in parallel.</summary>
        public static byte[] Encode(RgbaImage image, int maxThreads)
        {
            int bw = (image.Width + 3) / 4, bh = (image.Height + 3) / 4;
            var output = new byte[bw * bh * BlockBytes];
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxThreads) };
            if (bw * bh < 64 || maxThreads == 1)
            {
                var encoder = new Etc1Encoder();
                for (int by = 0; by < bh; by++) EncodeRow(image, by, bw, output, encoder);
                return output;
            }
            Parallel.For(0, bh, options, () => new Etc1Encoder(), (by, state, encoder) =>
            {
                EncodeRow(image, by, bw, output, encoder);
                return encoder;
            }, encoder => { });
            return output;
        }

        private static void EncodeRow(RgbaImage image, int by, int bw, byte[] output, Etc1Encoder encoder)
        {
            int w = image.Width, h = image.Height;
            byte[] px = image.Pixels;
            int[] block = encoder.Input;
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                {
                    int sy = Math.Min(by * 4 + y, h - 1);
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, w - 1);
                        int s = (sy * w + sx) * 4, d = (y * 4 + x) * 3;
                        block[d] = px[s];
                        block[d + 1] = px[s + 1];
                        block[d + 2] = px[s + 2];
                    }
                }
                encoder.EncodeBlock(output, (by * bw + bx) * BlockBytes);
            }
        }

        internal static int Expand5(int q) => (q << 3) | (q >> 2);

        internal static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        private static int SignExtend3(int v) => v >= 4 ? v - 8 : v;
    }
}
