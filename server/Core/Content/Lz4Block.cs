using System;

namespace McvsOffline.Server.Core.Content
{
    /// <summary>
    /// Decoder for the LZ4 block format (no frame header), the only codec used by the 1.17.32 data pack
    /// (report 11 §0.2). Every read and write is bounds-checked; malformed input raises InvalidOperationException.
    /// </summary>
    public static class Lz4Block
    {
        /// <summary>
        /// Decompresses <paramref name="srcCount"/> bytes at <paramref name="src"/>[<paramref name="srcOffset"/>] into
        /// exactly <paramref name="dstCount"/> bytes at <paramref name="dst"/>[<paramref name="dstOffset"/>].
        /// </summary>
        public static void Decompress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset, int dstCount)
        {
            if (src == null) throw new ArgumentNullException("src");
            if (dst == null) throw new ArgumentNullException("dst");
            if (srcOffset < 0 || srcCount < 0 || srcOffset + srcCount > src.Length) throw new ArgumentOutOfRangeException("srcCount");
            if (dstOffset < 0 || dstCount < 0 || dstOffset + dstCount > dst.Length) throw new ArgumentOutOfRangeException("dstCount");

            int ip = srcOffset;
            int ipEnd = srcOffset + srcCount;
            int op = dstOffset;
            int opEnd = dstOffset + dstCount;

            if (srcCount == 0)
            {
                if (dstCount != 0) throw Corrupt("empty input");
                return;
            }

            while (true)
            {
                if (ip >= ipEnd) throw Corrupt("truncated sequence");
                int token = src[ip++];

                // Literals.
                int literalLength = token >> 4;
                if (literalLength == 15) literalLength += ReadLength(src, ref ip, ipEnd);
                if (literalLength > ipEnd - ip) throw Corrupt("literals run past the input");
                if (literalLength > opEnd - op) throw Corrupt("literals run past the output");
                Buffer.BlockCopy(src, ip, dst, op, literalLength);
                ip += literalLength;
                op += literalLength;

                if (ip == ipEnd) break; // the last sequence carries literals only

                // Match.
                if (ipEnd - ip < 2) throw Corrupt("truncated match offset");
                int offset = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                if (offset == 0 || offset > op - dstOffset) throw Corrupt("match offset out of range");
                int matchLength = token & 0x0F;
                if (matchLength == 15) matchLength += ReadLength(src, ref ip, ipEnd);
                matchLength += 4;
                if (matchLength > opEnd - op) throw Corrupt("match runs past the output");

                int from = op - offset;
                if (offset >= matchLength)
                {
                    Buffer.BlockCopy(dst, from, dst, op, matchLength);
                    op += matchLength;
                }
                else
                {
                    // Overlapping copy (run-length style): must go byte by byte.
                    for (int i = 0; i < matchLength; i++) dst[op++] = dst[from++];
                }
            }

            if (op != opEnd) throw Corrupt("decoded " + (op - dstOffset) + " bytes, expected " + dstCount);
        }

        private static int ReadLength(byte[] src, ref int ip, int ipEnd)
        {
            int length = 0;
            int b;
            do
            {
                if (ip >= ipEnd) throw Corrupt("truncated length");
                b = src[ip++];
                length += b;
                if (length > Num.IntMax / 2) throw Corrupt("length overflow");
            }
            while (b == 255);
            return length;
        }

        private static InvalidOperationException Corrupt(string what)
        {
            return new InvalidOperationException("corrupt LZ4 block: " + what);
        }
    }
}
