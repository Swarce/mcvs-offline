using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core.Content;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// Writes the engine's GCBF container (report 11 §3; the reader is server/Core/Content/DataPack.cs, the reference encoder
    /// tools/datapack/gcbf.py): "GCBF", N, N seek-table pairs, then one 36-byte "padA" header + payload per block of up to 128 KiB.
    /// Every block uses the 'Lz4 ' codec, the only one the 1.17.32 pack uses, with a literal-only LZ4 block (one sequence: the
    /// token, the literal length, the bytes; valid for every LZ4 decoder, no compression needed for a probe).
    /// </summary>
    internal static class GcbfWriter
    {
        public const int BlockSize = 131072;
        private const uint BlockMagic = 0x41646170;   // "padA" on disk
        private const uint CodecLz4 = 0x4C7A3420;     // 'Lz4 '

        public static byte[] Encode(byte[] raw)
        {
            var chunks = new List<byte[]>();
            for (int o = 0; o < raw.Length; o += BlockSize)
            {
                int n = Math.Min(BlockSize, raw.Length - o);
                var c = new byte[n];
                Buffer.BlockCopy(raw, o, c, 0, n);
                chunks.Add(c);
            }
            if (chunks.Count == 0) chunks.Add(new byte[0]);
            int entries = chunks.Count + 1;
            var blocks = new List<byte[]>();
            foreach (byte[] c in chunks)
            {
                byte[] payload = Lz4Literals(c);
                using (var b = new MemoryStream())
                {
                    Ktx.WriteU32(b, BlockMagic);
                    Ktx.WriteU32(b, CodecLz4);
                    Ktx.WriteU32(b, (uint)payload.Length);
                    Ktx.WriteU32(b, (uint)c.Length);
                    Ktx.WriteU32(b, Crc32.Compute(payload, 1));
                    Ktx.WriteU32(b, Crc32.Compute(c, 1));
                    Ktx.WriteU32(b, (uint)c.Length);
                    Ktx.WriteU32(b, 0);
                    Ktx.WriteU32(b, 0);
                    b.Write(payload, 0, payload.Length);
                    blocks.Add(b.ToArray());
                }
            }
            using (var ms = new MemoryStream())
            {
                ms.Write(new[] { (byte)'G', (byte)'C', (byte)'B', (byte)'F' }, 0, 4);
                Ktx.WriteU32(ms, (uint)entries);
                uint rawOffset = 0;
                uint fileOffset = (uint)(8 + 8 * entries);
                for (int i = 0; i < chunks.Count; i++)
                {
                    Ktx.WriteU32(ms, rawOffset);
                    Ktx.WriteU32(ms, fileOffset);
                    rawOffset += (uint)chunks[i].Length;
                    fileOffset += (uint)blocks[i].Length;
                }
                Ktx.WriteU32(ms, rawOffset);
                Ktx.WriteU32(ms, fileOffset);
                foreach (byte[] b in blocks) ms.Write(b, 0, b.Length);
                return ms.ToArray();
            }
        }

        /// <summary>An LZ4 block holding <paramref name="data"/> as one literal run.</summary>
        public static byte[] Lz4Literals(byte[] data)
        {
            using (var ms = new MemoryStream(data.Length + data.Length / 255 + 16))
            {
                int length = data.Length;
                if (length < 15)
                {
                    ms.WriteByte((byte)(length << 4));
                }
                else
                {
                    ms.WriteByte(0xF0);
                    int rest = length - 15;
                    while (rest >= 255)
                    {
                        ms.WriteByte(255);
                        rest -= 255;
                    }
                    ms.WriteByte((byte)rest);
                }
                ms.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }
    }
}
