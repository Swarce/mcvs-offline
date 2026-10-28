using System;
using System.IO;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>An 8-bit RGB colour with a name for the log and the manifest.</summary>
    internal sealed class ProbeColour
    {
        public ProbeColour(string name, byte r, byte g, byte b)
        {
            Name = name;
            R = r;
            G = g;
            B = b;
        }

        public string Name { get; }
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }

        public static readonly ProbeColour Magenta = new ProbeColour("magenta", 255, 0, 255);
        public static readonly ProbeColour Green = new ProbeColour("green", 0, 255, 0);
        public static readonly ProbeColour Blue = new ProbeColour("blue", 0, 64, 255);
        public static readonly ProbeColour Yellow = new ProbeColour("yellow", 255, 255, 0);
        public static readonly ProbeColour Cyan = new ProbeColour("cyan", 0, 255, 255);
        public static readonly ProbeColour Orange = new ProbeColour("orange", 255, 128, 0);
    }

    /// <summary>
    /// A solid-colour copy of a compressed texture with exactly the original's header, key/value data, size and mip chain: every
    /// block of every level becomes one constant-colour block, so no encoder is needed.
    /// <list type="bullet">
    /// <item>ASTC (any LDR block size, linear or sRGB): the void-extent block, 128 bits: bits 0-8 = 0x1FC (void extent), bit 9 =
    /// 0 (LDR), bits 10-11 = 11, bits 12-63 all ones (no extent coordinates), then R, G, B, A as UNORM16. The ASTC pack's
    /// shaders read one RGBA texture (alpha 1 here).</item>
    /// <item>ETC1 / ETC2 RGB8: a differential-mode block with the colour as the 5-bit base, zero deltas, table 0 and every pixel
    /// index 0 (base + 2: the colour within 1/32 per channel).</item>
    /// </list>
    /// </summary>
    internal static class ProbeTexture
    {
        public static Ktx Recolour(Ktx original, ProbeColour colour)
        {
            int[] block = original.Block();
            if (block == null)
                throw new InvalidDataException("texture format " + original.FormatName + " is not supported (ASTC, ETC1 or ETC2 RGB8 only)");
            byte[] pattern = original.IsAstc ? AstcVoidExtent(colour) : Etc1Solid(colour);
            var copy = new Ktx { Header = (uint[])original.Header.Clone(), KeyValueData = (byte[])original.KeyValueData.Clone() };
            for (int level = 0; level < original.Levels.Count; level++)
            {
                int w = Ktx.MipSize(original.Width, level);
                int h = Ktx.MipSize(original.Height, level);
                long blocks = (long)((w + block[0] - 1) / block[0]) * ((h + block[1] - 1) / block[1]);
                long expected = blocks * block[2];
                int actual = original.Levels[level].Length;
                if (actual != expected)
                    throw new InvalidDataException("mip level " + level + " holds " + actual + " bytes, " + expected + " expected for " + w + "x" + h + " " + original.FormatName);
                var data = new byte[actual];
                for (int o = 0; o < actual; o += pattern.Length) Buffer.BlockCopy(pattern, 0, data, o, pattern.Length);
                copy.Levels.Add(data);
            }
            return copy;
        }

        /// <summary>One ASTC LDR void-extent block of the colour (alpha opaque).</summary>
        public static byte[] AstcVoidExtent(ProbeColour colour)
        {
            var b = new byte[16];
            ulong low = 0xFFFFFFFFFFFFFDFCUL;
            for (int i = 0; i < 8; i++) b[i] = (byte)(low >> (8 * i));
            WriteU16(b, 8, (ushort)(colour.R * 257));
            WriteU16(b, 10, (ushort)(colour.G * 257));
            WriteU16(b, 12, (ushort)(colour.B * 257));
            WriteU16(b, 14, 0xFFFF);
            return b;
        }

        /// <summary>One ETC1 block (differential mode, flat) close to the colour.</summary>
        public static byte[] Etc1Solid(ProbeColour colour)
        {
            // Base colours are 5 bits per channel, expanded as (c << 3) | (c >> 2); table 0's index-0 modifier adds 2.
            int r = Five(colour.R), g = Five(colour.G), b = Five(colour.B);
            return new byte[]
            {
                (byte)(r << 3), (byte)(g << 3), (byte)(b << 3),
                0x02,          // table codewords 0/0, diff bit 1, flip 0
                0, 0, 0, 0,    // pixel indices: MSB 0, LSB 0 → modifier index 0 (+2)
            };
        }

        /// <summary>The colour an <see cref="Etc1Solid"/> block decodes to (for tests).</summary>
        public static byte[] DecodeEtc1Solid(byte[] block)
        {
            var rgb = new byte[3];
            for (int i = 0; i < 3; i++)
            {
                int c5 = block[i] >> 3;
                int c8 = (c5 << 3) | (c5 >> 2);
                rgb[i] = (byte)Math.Min(255, c8 + 2);
            }
            return rgb;
        }

        private static int Five(byte c)
        {
            // The 5-bit value whose expansion + 2 is nearest to c.
            int best = 0, bestError = int.MaxValue;
            for (int v = 0; v < 32; v++)
            {
                int decoded = Math.Min(255, ((v << 3) | (v >> 2)) + 2);
                int error = Math.Abs(decoded - c);
                if (error < bestError)
                {
                    best = v;
                    bestError = error;
                }
            }
            return best;
        }

        private static void WriteU16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }
    }
}
