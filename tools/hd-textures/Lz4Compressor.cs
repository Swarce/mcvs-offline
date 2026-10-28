using System;
using System.IO;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// An LZ4 block compressor (the LZ4 block format: sequences of a token, literal length, literals, a 2-byte offset and a
    /// match length; the last sequence is literals only). Greedy with a 4-byte hash table, like LZ4's fast mode, and the
    /// format's end rules: the last 5 bytes are literals and no match starts in the last 12 bytes. Any LZ4 decoder reads
    /// it (the engine's, server/Core/Content/Lz4Block.cs, Python's lz4.block).
    /// </summary>
    internal static class Lz4Compressor
    {
        private const int MinMatch = 4;
        private const int HashLog = 16;
        private const int LastLiterals = 5;
        private const int MatchSafeDistance = 12;
        private const int MaxOffset = 65535;

        public static byte[] Compress(byte[] src, int offset, int count)
        {
            var output = new MemoryStream(count + count / 255 + 16);
            var table = new int[1 << HashLog];
            for (int i = 0; i < table.Length; i++) table[i] = -1;
            int end = offset + count;
            int anchor = offset;
            int p = offset;
            int matchLimit = end - MatchSafeDistance;
            while (p < matchLimit)
            {
                uint sequence = Read32(src, p);
                int h = (int)((sequence * 2654435761u) >> (32 - HashLog));
                int candidate = table[h];
                table[h] = p;
                if (candidate < 0 || p - candidate > MaxOffset || Read32(src, candidate) != sequence)
                {
                    p++;
                    continue;
                }
                // Extend backwards over pending literals, then forwards up to the last-literals boundary.
                while (p > anchor && candidate > offset && src[p - 1] == src[candidate - 1])
                {
                    p--;
                    candidate--;
                }
                int length = MinMatch;
                int limit = end - LastLiterals;
                while (p + length < limit && src[p + length] == src[candidate + length]) length++;
                WriteSequence(output, src, anchor, p - anchor, p - candidate, length);
                p += length;
                anchor = p;
                if (p - 2 >= offset && p - 2 < matchLimit)
                {
                    uint s2 = Read32(src, p - 2);
                    table[(int)((s2 * 2654435761u) >> (32 - HashLog))] = p - 2;
                }
            }
            WriteLastLiterals(output, src, anchor, end - anchor);
            return output.ToArray();
        }

        private static void WriteSequence(MemoryStream output, byte[] src, int literalStart, int literalLength, int matchOffset, int matchLength)
        {
            int ml = matchLength - MinMatch;
            int token = (Math.Min(literalLength, 15) << 4) | Math.Min(ml, 15);
            output.WriteByte((byte)token);
            if (literalLength >= 15) WriteLength(output, literalLength - 15);
            output.Write(src, literalStart, literalLength);
            output.WriteByte((byte)matchOffset);
            output.WriteByte((byte)(matchOffset >> 8));
            if (ml >= 15) WriteLength(output, ml - 15);
        }

        private static void WriteLastLiterals(MemoryStream output, byte[] src, int start, int length)
        {
            output.WriteByte((byte)(Math.Min(length, 15) << 4));
            if (length >= 15) WriteLength(output, length - 15);
            output.Write(src, start, length);
        }

        private static void WriteLength(MemoryStream output, int rest)
        {
            while (rest >= 255)
            {
                output.WriteByte(255);
                rest -= 255;
            }
            output.WriteByte((byte)rest);
        }

        private static uint Read32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }
    }
}
