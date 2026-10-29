using System;
using System.Collections.Generic;
using System.IO;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// A DDS texture as the PC build stores it (pc-textures.md §2.3): the 128-byte legacy header, FourCC DXT1 (BC1) or DXT5
    /// (BC3), one 2D image with its mip chain. No DX10 header, cubemap or volume occurs in the textures we convert.
    /// </summary>
    internal sealed class Dds
    {
        public const int HeaderSize = 128;
        private const uint MipMapCountFlag = 0x20000;
        private const uint FourCcFlag = 0x4;
        private const uint CubemapFlag = 0x200;

        public string FourCc { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public List<byte[]> Levels { get; } = new List<byte[]>();

        public bool IsBc1 => FourCc == "DXT1";
        public int BlockBytes => IsBc1 ? 8 : 16;

        public static bool LooksLikeDds(byte[] data)
        {
            return data != null && data.Length >= HeaderSize && data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ';
        }

        /// <summary>Reads the header and splits the mip levels (fewer when the file is cut short of the declared count).</summary>
        public static Dds Parse(byte[] data)
        {
            if (!LooksLikeDds(data)) throw new InvalidDataException("not a DDS file");
            uint flags = Ktx.U32(data, 8);
            int height = (int)Ktx.U32(data, 12);
            int width = (int)Ktx.U32(data, 16);
            uint mipCount = Ktx.U32(data, 28);
            uint pfFlags = Ktx.U32(data, 80);
            string fourCc = System.Text.Encoding.ASCII.GetString(data, 84, 4);
            uint caps2 = Ktx.U32(data, 112);
            if ((pfFlags & FourCcFlag) == 0) throw new InvalidDataException("uncompressed DDS is not supported");
            if (fourCc != "DXT1" && fourCc != "DXT5") throw new InvalidDataException("DDS format " + fourCc + " is not supported (DXT1 and DXT5 only)");
            if ((caps2 & CubemapFlag) != 0) throw new InvalidDataException("DDS cubemaps are not supported");
            if (width < 1 || height < 1 || width > 16384 || height > 16384) throw new InvalidDataException("bad DDS size " + width + "x" + height);
            var dds = new Dds { FourCc = fourCc, Width = width, Height = height };
            int levels = (flags & MipMapCountFlag) != 0 && mipCount > 0 ? (int)mipCount : 1;
            int p = HeaderSize;
            for (int level = 0; level < levels; level++)
            {
                int w = Ktx.MipSize(width, level), h = Ktx.MipSize(height, level);
                int size = ((w + 3) / 4) * ((h + 3) / 4) * dds.BlockBytes;
                if (p + size > data.Length)
                {
                    if (level == 0) throw new InvalidDataException("DDS top level runs past the end");
                    break;
                }
                var bytes = new byte[size];
                Buffer.BlockCopy(data, p, bytes, 0, size);
                dds.Levels.Add(bytes);
                p += size;
                if (w == 1 && h == 1) break;
            }
            return dds;
        }

        /// <summary>Decodes mip level <paramref name="level"/> to RGBA (DXT1: punch-through alpha 0 for index 3 of 3-colour blocks).</summary>
        public RgbaImage Decode(int level)
        {
            int w = Ktx.MipSize(Width, level), h = Ktx.MipSize(Height, level);
            return IsBc1 ? BcDecoder.DecodeBc1(Levels[level], w, h) : BcDecoder.DecodeBc3(Levels[level], w, h);
        }
    }

    /// <summary>BC1 (DXT1) and BC3 (DXT5) block decoding to RGBA8, as the Direct3D specification describes it.</summary>
    internal static class BcDecoder
    {
        public static RgbaImage DecodeBc1(byte[] data, int width, int height)
        {
            return Decode(data, width, height, false);
        }

        public static RgbaImage DecodeBc3(byte[] data, int width, int height)
        {
            return Decode(data, width, height, true);
        }

        private static RgbaImage Decode(byte[] data, int width, int height, bool bc3)
        {
            var image = new RgbaImage(width, height);
            int bw = (width + 3) / 4, bh = (height + 3) / 4;
            int blockBytes = bc3 ? 16 : 8;
            if (data.Length < bw * bh * blockBytes) throw new InvalidDataException("BC data too short for " + width + "x" + height);
            var colours = new byte[16];
            var alphas = new byte[8];
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int o = (by * bw + bx) * blockBytes;
                    ulong alphaBits = 0;
                    if (bc3)
                    {
                        AlphaPalette(data[o], data[o + 1], alphas);
                        for (int i = 0; i < 6; i++) alphaBits |= (ulong)data[o + 2 + i] << (8 * i);
                        o += 8;
                    }
                    int c0 = data[o] | (data[o + 1] << 8);
                    int c1 = data[o + 2] | (data[o + 3] << 8);
                    ColourPalette(c0, c1, bc3, colours);
                    uint indices = Ktx.U32(data, o + 4);
                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= height) break;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= width) break;
                            int i = py * 4 + px;
                            int ci = (int)((indices >> (2 * i)) & 3);
                            int d = (y * width + x) * 4;
                            image.Pixels[d] = colours[ci * 4];
                            image.Pixels[d + 1] = colours[ci * 4 + 1];
                            image.Pixels[d + 2] = colours[ci * 4 + 2];
                            image.Pixels[d + 3] = bc3 ? alphas[(int)((alphaBits >> (3 * i)) & 7)] : colours[ci * 4 + 3];
                        }
                    }
                }
            }
            return image;
        }

        /// <summary>The four RGBA colours of a BC1 colour block (BC3's colour block is always in four-colour mode).</summary>
        internal static void ColourPalette(int c0, int c1, bool forceFourColour, byte[] palette)
        {
            int r0 = Expand5(c0 >> 11), g0 = Expand6((c0 >> 5) & 63), b0 = Expand5(c0 & 31);
            int r1 = Expand5(c1 >> 11), g1 = Expand6((c1 >> 5) & 63), b1 = Expand5(c1 & 31);
            Set(palette, 0, r0, g0, b0, 255);
            Set(palette, 1, r1, g1, b1, 255);
            if (c0 > c1 || forceFourColour)
            {
                Set(palette, 2, (2 * r0 + r1 + 1) / 3, (2 * g0 + g1 + 1) / 3, (2 * b0 + b1 + 1) / 3, 255);
                Set(palette, 3, (r0 + 2 * r1 + 1) / 3, (g0 + 2 * g1 + 1) / 3, (b0 + 2 * b1 + 1) / 3, 255);
            }
            else
            {
                Set(palette, 2, (r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2, 255);
                Set(palette, 3, 0, 0, 0, 0);
            }
        }

        /// <summary>The eight alpha values of a BC3 alpha block.</summary>
        internal static void AlphaPalette(int a0, int a1, byte[] palette)
        {
            palette[0] = (byte)a0;
            palette[1] = (byte)a1;
            if (a0 > a1)
            {
                for (int k = 1; k <= 6; k++) palette[k + 1] = (byte)(((7 - k) * a0 + k * a1 + 3) / 7);
            }
            else
            {
                for (int k = 1; k <= 4; k++) palette[k + 1] = (byte)(((5 - k) * a0 + k * a1 + 2) / 5);
                palette[6] = 0;
                palette[7] = 255;
            }
        }

        private static void Set(byte[] p, int i, int r, int g, int b, int a)
        {
            p[i * 4] = (byte)r;
            p[i * 4 + 1] = (byte)g;
            p[i * 4 + 2] = (byte)b;
            p[i * 4 + 3] = (byte)a;
        }

        private static int Expand5(int v) => (v << 3) | (v >> 2);

        private static int Expand6(int v) => (v << 2) | (v >> 4);
    }
}
