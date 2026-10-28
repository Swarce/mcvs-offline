using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using McvsOffline.Server.Core.Content;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// Writes the engine's GCBF container (report 11 §3; the reader is server/Core/Content/DataPack.cs): "GCBF", N, N
    /// seek-table pairs, then one 36-byte "padA" header + LZ4 payload per block. The texture files of the 1.17.32 pack are
    /// split into <see cref="TextureBlocks"/> blocks of equal size (the last takes the remainder), so that is what
    /// <see cref="Encode(byte[])"/> writes; a block that LZ4 cannot shrink is stored as one literal run.
    /// </summary>
    internal static class GcbfWriter
    {
        /// <summary>Blocks per file in the pack's textures (every texture file has 9 seek-table entries).</summary>
        public const int TextureBlocks = 8;

        private const uint BlockMagic = 0x41646170;   // "padA" on disk
        private const uint CodecLz4 = 0x4C7A3420;     // 'Lz4 '

        /// <summary>GCBF with the pack's eight-block split.</summary>
        public static byte[] Encode(byte[] raw)
        {
            var sizes = new List<int>();
            int per = raw.Length / TextureBlocks;
            if (per == 0)
            {
                sizes.Add(raw.Length);
            }
            else
            {
                for (int i = 0; i < TextureBlocks - 1; i++) sizes.Add(per);
                sizes.Add(raw.Length - per * (TextureBlocks - 1));
            }
            return Encode(raw, sizes);
        }

        /// <summary>GCBF with the given block sizes (they must add up to the data length).</summary>
        public static byte[] Encode(byte[] raw, IList<int> blockSizes)
        {
            var offsets = new int[blockSizes.Count];
            int sum = 0;
            for (int i = 0; i < blockSizes.Count; i++)
            {
                offsets[i] = sum;
                sum += blockSizes[i];
            }
            if (sum != raw.Length) throw new ArgumentException("block sizes add up to " + sum + ", not " + raw.Length);
            var blocks = new byte[blockSizes.Count][];
            Parallel.For(0, blockSizes.Count, i => blocks[i] = Block(raw, offsets[i], blockSizes[i]));
            int entries = blockSizes.Count + 1;
            using (var ms = new MemoryStream())
            {
                ms.Write(new[] { (byte)'G', (byte)'C', (byte)'B', (byte)'F' }, 0, 4);
                Ktx.WriteU32(ms, (uint)entries);
                uint fileOffset = (uint)(8 + 8 * entries);
                for (int i = 0; i < blocks.Length; i++)
                {
                    Ktx.WriteU32(ms, (uint)offsets[i]);
                    Ktx.WriteU32(ms, fileOffset);
                    fileOffset += (uint)blocks[i].Length;
                }
                Ktx.WriteU32(ms, (uint)raw.Length);
                Ktx.WriteU32(ms, fileOffset);
                foreach (byte[] b in blocks) ms.Write(b, 0, b.Length);
                return ms.ToArray();
            }
        }

        private static byte[] Block(byte[] raw, int offset, int count)
        {
            byte[] payload = Lz4Compressor.Compress(raw, offset, count);
            if (payload.Length > count + count / 255 + 16) payload = Lz4Literals(raw, offset, count);
            using (var b = new MemoryStream(payload.Length + 36))
            {
                uint dataCrc = Crc32.Compute(raw, offset, count, 1);
                Ktx.WriteU32(b, BlockMagic);
                Ktx.WriteU32(b, CodecLz4);
                Ktx.WriteU32(b, (uint)payload.Length);
                Ktx.WriteU32(b, (uint)count);
                Ktx.WriteU32(b, Crc32.Compute(payload, 1));
                Ktx.WriteU32(b, dataCrc);
                Ktx.WriteU32(b, (uint)count);
                Ktx.WriteU32(b, 0);
                Ktx.WriteU32(b, 0);
                b.Write(payload, 0, payload.Length);
                return b.ToArray();
            }
        }

        /// <summary>An LZ4 block holding the bytes as one literal run (valid for every LZ4 decoder).</summary>
        public static byte[] Lz4Literals(byte[] data, int offset, int length)
        {
            using (var ms = new MemoryStream(length + length / 255 + 16))
            {
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
                ms.Write(data, offset, length);
                return ms.ToArray();
            }
        }
    }
}
