using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using McvsOffline.Tools.HdTextures;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.HdTextures
{
    /// <summary>
    /// Synthetic textures shaped like the two builds' files (docs/research/pc-textures.md §2): PC DDS DXT1/DXT5 with mips
    /// (a small BC1/BC3 encoder of our own: endpoints from the block's extremes) and Android ETC1 KTX, bare or as split-alpha
    /// zips, GCBF-wrapped like the pack's. Plus the real-data locations (MCVS_PC_INSTALL, MCVS_DATAPACK).
    /// </summary>
    internal static class HdFixtures
    {
        /// <summary>The KTX key/value data of the ETC pack's files (crnlib's FourCC and the orientation).</summary>
        public static readonly byte[] PackKeyValues = KeyValues(("CRNLIB_FOURCC", "ETC1"), ("KTXorientation", "S=r,T=d"));

        /// <summary>A test pattern: smooth gradients, a few edges and some noise; alpha a ramp unless opaque.</summary>
        public static RgbaImage Pattern(int width, int height, int seed, bool opaque)
        {
            var rnd = new Random(seed);
            var image = new RgbaImage(width, height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int o = (y * width + x) * 4;
                    double u = (double)x / width, v = (double)y / height;
                    int edge = ((x / 16) + (y / 16)) % 2 == 0 ? 30 : 0;
                    image.Pixels[o] = Clamp(60 + 140 * u + edge + rnd.Next(-8, 9));
                    image.Pixels[o + 1] = Clamp(90 + 100 * Math.Sin(6 * v + seed) + rnd.Next(-8, 9));
                    image.Pixels[o + 2] = Clamp(120 + 80 * (u - v) + edge);
                    image.Pixels[o + 3] = opaque ? (byte)255 : Clamp(255 * u);
                }
            }
            return image;
        }

        /// <summary>A DDS DXT1/DXT5 of the image with a full mip chain (box-filtered), as the PC build stores textures.</summary>
        public static byte[] Dds(RgbaImage top, bool dxt5, int levels = -1)
        {
            var images = new List<RgbaImage> { top };
            while ((levels < 0 && (images[images.Count - 1].Width > 1 || images[images.Count - 1].Height > 1)) || (levels > 0 && images.Count < levels))
                images.Add(images[images.Count - 1].HalfSize());
            using (var ms = new MemoryStream())
            {
                var h = new byte[128];
                Encoding.ASCII.GetBytes("DDS ").CopyTo(h, 0);
                Put(h, 4, 124);
                Put(h, 8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);
                Put(h, 12, (uint)top.Height);
                Put(h, 16, (uint)top.Width);
                Put(h, 28, (uint)images.Count);
                Put(h, 76, 32);
                Put(h, 80, 0x4);
                Encoding.ASCII.GetBytes(dxt5 ? "DXT5" : "DXT1").CopyTo(h, 84);
                Put(h, 108, 0x1000 | 0x8 | 0x400000);
                ms.Write(h, 0, h.Length);
                foreach (RgbaImage image in images)
                {
                    byte[] blocks = EncodeBc(image, dxt5);
                    ms.Write(blocks, 0, blocks.Length);
                }
                return ms.ToArray();
            }
        }

        /// <summary>BC1 (or BC3) blocks: endpoints from the block's darkest and brightest pixel, nearest palette index.</summary>
        public static byte[] EncodeBc(RgbaImage image, bool bc3)
        {
            int bw = (image.Width + 3) / 4, bh = (image.Height + 3) / 4;
            int size = bc3 ? 16 : 8;
            var output = new byte[bw * bh * size];
            var palette = new byte[16];
            var alphas = new byte[8];
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    var px = new int[16][];
                    for (int i = 0; i < 16; i++)
                    {
                        int x = Math.Min(bx * 4 + i % 4, image.Width - 1), y = Math.Min(by * 4 + i / 4, image.Height - 1);
                        int o = (y * image.Width + x) * 4;
                        px[i] = new int[] { image.Pixels[o], image.Pixels[o + 1], image.Pixels[o + 2], image.Pixels[o + 3] };
                    }
                    int d = (by * bw + bx) * size;
                    if (bc3)
                    {
                        int amax = 0, amin = 255;
                        foreach (int[] p in px)
                        {
                            amax = Math.Max(amax, p[3]);
                            amin = Math.Min(amin, p[3]);
                        }
                        if (amax == amin && amax < 255) amax++;
                        if (amax == amin) amin--;
                        BcDecoder.AlphaPalette(amax, amin, alphas);
                        output[d] = (byte)amax;
                        output[d + 1] = (byte)amin;
                        ulong bits = 0;
                        for (int i = 0; i < 16; i++)
                        {
                            int best = 0;
                            for (int k = 1; k < 8; k++)
                            {
                                if (Math.Abs(alphas[k] - px[i][3]) < Math.Abs(alphas[best] - px[i][3])) best = k;
                            }
                            bits |= (ulong)best << (3 * i);
                        }
                        for (int i = 0; i < 6; i++) output[d + 2 + i] = (byte)(bits >> (8 * i));
                        d += 8;
                    }
                    int[] lo = px[0], hi = px[0];
                    foreach (int[] p in px)
                    {
                        if (p[0] + p[1] + p[2] < lo[0] + lo[1] + lo[2]) lo = p;
                        if (p[0] + p[1] + p[2] > hi[0] + hi[1] + hi[2]) hi = p;
                    }
                    int c0 = To565(hi), c1 = To565(lo);
                    if (c0 == c1)
                    {
                        if (c1 > 0) c1--;
                        else c0++;
                    }
                    if (c0 < c1)
                    {
                        int t = c0;
                        c0 = c1;
                        c1 = t;
                    }
                    BcDecoder.ColourPalette(c0, c1, bc3, palette);
                    output[d] = (byte)c0;
                    output[d + 1] = (byte)(c0 >> 8);
                    output[d + 2] = (byte)c1;
                    output[d + 3] = (byte)(c1 >> 8);
                    uint indices = 0;
                    for (int i = 0; i < 16; i++)
                    {
                        int best = 0, bestE = int.MaxValue;
                        for (int k = 0; k < 4; k++)
                        {
                            int e = Sq(palette[k * 4] - px[i][0]) + Sq(palette[k * 4 + 1] - px[i][1]) + Sq(palette[k * 4 + 2] - px[i][2]);
                            if (e < bestE)
                            {
                                bestE = e;
                                best = k;
                            }
                        }
                        indices |= (uint)best << (2 * i);
                    }
                    output[d + 4] = (byte)indices;
                    output[d + 5] = (byte)(indices >> 8);
                    output[d + 6] = (byte)(indices >> 16);
                    output[d + 7] = (byte)(indices >> 24);
                }
            }
            return output;
        }

        /// <summary>An ETC1 KTX of the image like the pack's (header, key/value data, a mip chain of <paramref name="levels"/>).</summary>
        public static Ktx EtcKtx(RgbaImage top, int levels, bool alphaAsGrey = false)
        {
            var ktx = new Ktx
            {
                Header = new uint[] { 0x04030201, 0, 0, 0, Ktx.GlEtc1, 0x1907, (uint)top.Width, (uint)top.Height, 0, 0, 1, (uint)levels, 0 },
                KeyValueData = (byte[])PackKeyValues.Clone(),
            };
            RgbaImage level = alphaAsGrey ? top.AlphaAsGrey() : top;
            for (int i = 0; i < levels; i++)
            {
                if (i > 0) level = level.HalfSize();
                ktx.Levels.Add(Etc1.Encode(level, 2));
            }
            return ktx;
        }

        /// <summary>A bare Android texture file: GCBF around an ETC1 KTX.</summary>
        public static byte[] AndroidKtxFile(RgbaImage top, int levels)
        {
            return GcbfWriter.Encode(EtcKtx(top, levels).Write());
        }

        /// <summary>A split-alpha Android texture file: a stored zip SPLIT + rgb.ktx + alpha.ktx, each KTX GCBF-wrapped.</summary>
        public static byte[] AndroidSplitFile(RgbaImage top, int levels)
        {
            return StoredZipWriter.Build(false, new[]
            {
                new KeyValuePair<string, byte[]>("SPLIT", new byte[4]),
                new KeyValuePair<string, byte[]>("rgb.ktx", GcbfWriter.Encode(EtcKtx(top, levels).Write())),
                new KeyValuePair<string, byte[]>("alpha.ktx", GcbfWriter.Encode(EtcKtx(top, levels, true).Write())),
            });
        }

        public static byte[] KeyValues(params (string Key, string Value)[] pairs)
        {
            using (var ms = new MemoryStream())
            {
                foreach ((string key, string value) in pairs)
                {
                    byte[] kv = Encoding.UTF8.GetBytes(key + "\0" + value + "\0");
                    ms.Write(BitConverter.GetBytes(kv.Length), 0, 4);
                    ms.Write(kv, 0, kv.Length);
                    for (int i = 0; i < (4 - kv.Length % 4) % 4; i++) ms.WriteByte(0);
                }
                return ms.ToArray();
            }
        }

        /// <summary>The PC install's Data folder (MCVS_PC_INSTALL), or null after logging a skip; MCVS_REQUIRE_PC=1 fails instead.</summary>
        public static string PcData(ITestOutputHelper output)
        {
            string dir = Environment.GetEnvironmentVariable("MCVS_PC_INSTALL");
            if (string.IsNullOrEmpty(dir)) dir = "/home/user/mcvs-work/pc/install";
            string data = Path.Combine(dir, "Data");
            if (File.Exists(Path.Combine(data, "1675802.f"))) return data;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_PC") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_PC=1 but " + dir + " has no Steam install of the game");
            output.WriteLine("the Steam install is not available (MCVS_PC_INSTALL=" + dir + "): skipped");
            return null;
        }

        /// <summary>The ETC pack's Data folder (MCVS_DATAPACK), or null after logging a skip.</summary>
        public static string AndroidData(ITestOutputHelper output)
        {
            string dir = Environment.GetEnvironmentVariable("MCVS_DATAPACK");
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "MCVS.game")) && File.Exists(Path.Combine(dir, "8", "dbd2f1.f"))) return dir;
            output.WriteLine("MCVS_DATAPACK (the ETC pack's Data folder) is not set: skipped");
            return null;
        }

        public static void DeleteDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static int To565(int[] p) => ((p[0] >> 3) << 11) | ((p[1] >> 2) << 5) | (p[2] >> 3);

        private static int Sq(int v) => v * v;

        private static byte Clamp(double v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v)));

        private static void Put(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }
    }
}
