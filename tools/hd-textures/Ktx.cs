using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// A KTX 1.1 texture as the game's data packs hold it (pc-textures.md §2): one 2D image (no array, one face), little-endian,
    /// with its key/value block and every mip level. The probe keeps every header field and size and only replaces the blocks.
    /// </summary>
    internal sealed class Ktx
    {
        public static readonly byte[] Identifier = { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };

        public const uint GlEtc1 = 0x8D64;           // GL_ETC1_RGB8_OES (the ETC pack)
        public const uint GlEtc2Rgb8 = 0x9274;       // GL_COMPRESSED_RGB8_ETC2
        public const uint GlAstcFirst = 0x93B0;      // GL_COMPRESSED_RGBA_ASTC_4x4_KHR
        public const uint GlAstcLast = 0x93BD;       // GL_COMPRESSED_RGBA_ASTC_12x12_KHR
        public const uint GlAstcSrgbFirst = 0x93D0;  // GL_COMPRESSED_SRGB8_ALPHA8_ASTC_4x4_KHR
        public const uint GlAstcSrgbLast = 0x93DD;
        public const uint GlAstc6x6 = 0x93B4;        // the ASTC pack (the S22)

        /// <summary>The 13 header words after the identifier: endianness, glType ... bytesOfKeyValueData.</summary>
        public uint[] Header = new uint[13];

        public byte[] KeyValueData = new byte[0];

        public List<byte[]> Levels = new List<byte[]>();

        public uint GlInternalFormat { get { return Header[4]; } }
        public int Width { get { return (int)Header[6]; } }
        public int Height { get { return (int)Header[7]; } }
        public int MipLevels { get { return Levels.Count; } }

        public bool IsAstc
        {
            get
            {
                uint f = GlInternalFormat;
                return (f >= GlAstcFirst && f <= GlAstcLast) || (f >= GlAstcSrgbFirst && f <= GlAstcSrgbLast);
            }
        }

        public bool IsAstcSrgb
        {
            get { return GlInternalFormat >= GlAstcSrgbFirst && GlInternalFormat <= GlAstcSrgbLast; }
        }

        private static readonly int[][] AstcFootprints =
        {
            new[] { 4, 4 }, new[] { 5, 4 }, new[] { 5, 5 }, new[] { 6, 5 }, new[] { 6, 6 }, new[] { 8, 5 }, new[] { 8, 6 },
            new[] { 8, 8 }, new[] { 10, 5 }, new[] { 10, 6 }, new[] { 10, 8 }, new[] { 10, 10 }, new[] { 12, 10 }, new[] { 12, 12 },
        };

        /// <summary>The GL format of a 2D ASTC block size (<c>GL_COMPRESSED_RGBA_ASTC_6x6_KHR</c> = 0x93B4, or its sRGB form 0x93D4); 0 when there is none.</summary>
        public static uint AstcFormat(int blockX, int blockY, bool srgb)
        {
            for (int i = 0; i < AstcFootprints.Length; i++)
            {
                if (AstcFootprints[i][0] == blockX && AstcFootprints[i][1] == blockY) return (uint)i + (srgb ? GlAstcSrgbFirst : GlAstcFirst);
            }
            return 0;
        }

        public bool IsEtc1Compatible
        {
            get { return GlInternalFormat == GlEtc1 || GlInternalFormat == GlEtc2Rgb8; }
        }

        /// <summary>Block footprint (width, height) and bytes per block, or null for an unsupported format.</summary>
        public int[] Block()
        {
            if (IsEtc1Compatible) return new[] { 4, 4, 8 };
            if (!IsAstc) return null;
            int index = (int)(GlInternalFormat >= GlAstcSrgbFirst ? GlInternalFormat - GlAstcSrgbFirst : GlInternalFormat - GlAstcFirst);
            return new[] { AstcFootprints[index][0], AstcFootprints[index][1], 16 };
        }

        public string FormatName
        {
            get
            {
                if (GlInternalFormat == GlEtc1) return "ETC1";
                if (GlInternalFormat == GlEtc2Rgb8) return "ETC2 RGB8";
                int[] b = Block();
                if (IsAstc && b != null) return "ASTC " + b[0] + "x" + b[1] + (GlInternalFormat >= GlAstcSrgbFirst ? " sRGB" : "");
                return "0x" + GlInternalFormat.ToString("x4", CultureInfo.InvariantCulture);
            }
        }

        public static bool LooksLikeKtx(byte[] data)
        {
            if (data == null || data.Length < 64) return false;
            for (int i = 0; i < Identifier.Length; i++)
            {
                if (data[i] != Identifier[i]) return false;
            }
            return true;
        }

        public static Ktx Parse(byte[] data)
        {
            if (!LooksLikeKtx(data)) throw new InvalidDataException("not a KTX 1.1 file");
            var ktx = new Ktx();
            for (int i = 0; i < 13; i++) ktx.Header[i] = U32(data, 12 + 4 * i);
            if (ktx.Header[0] != 0x04030201) throw new InvalidDataException("big-endian KTX is not supported");
            if (ktx.Header[8] != 0 || ktx.Header[9] != 0 || ktx.Header[10] != 1)
                throw new InvalidDataException("only plain 2D textures are supported (depth " + ktx.Header[8] + ", array " + ktx.Header[9] + ", faces " + ktx.Header[10] + ")");
            uint levels = ktx.Header[11] == 0 ? 1 : ktx.Header[11];
            int kv = checked((int)ktx.Header[12]);
            int p = 64;
            if (p + kv > data.Length) throw new InvalidDataException("key/value data runs past the end");
            ktx.KeyValueData = new byte[kv];
            Buffer.BlockCopy(data, p, ktx.KeyValueData, 0, kv);
            p += kv;
            for (int level = 0; level < levels; level++)
            {
                if (p + 4 > data.Length) throw new InvalidDataException("mip level " + level + " is missing");
                int size = checked((int)U32(data, p));
                p += 4;
                if (p + size > data.Length) throw new InvalidDataException("mip level " + level + " runs past the end");
                var bytes = new byte[size];
                Buffer.BlockCopy(data, p, bytes, 0, size);
                ktx.Levels.Add(bytes);
                p += size;
                p += (4 - size % 4) % 4;
            }
            return ktx;
        }

        public byte[] Write()
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(Identifier, 0, Identifier.Length);
                uint[] header = (uint[])Header.Clone();
                header[12] = (uint)KeyValueData.Length;
                foreach (uint w in header) WriteU32(ms, w);
                ms.Write(KeyValueData, 0, KeyValueData.Length);
                foreach (byte[] level in Levels)
                {
                    WriteU32(ms, (uint)level.Length);
                    ms.Write(level, 0, level.Length);
                    for (int i = 0; i < (4 - level.Length % 4) % 4; i++) ms.WriteByte(0);
                }
                return ms.ToArray();
            }
        }

        /// <summary>Width or height of mip <paramref name="level"/>.</summary>
        public static int MipSize(int size, int level)
        {
            int s = size >> level;
            return s < 1 ? 1 : s;
        }

        internal static uint U32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        internal static void WriteU32(Stream s, uint v)
        {
            s.WriteByte((byte)v);
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 24));
        }
    }
}
