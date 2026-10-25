namespace McvsOffline.Server.Core.Content
{
    /// <summary>
    /// zlib-compatible CRC-32 (polynomial 0xEDB88320). <c>Compute(data, seed)</c> equals Python's
    /// <c>zlib.crc32(data, seed)</c>: the data pack names files by crc32(path) with seed 0 and checks GCBF blocks
    /// with seed 1 (report 11 §2, §3).
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        public static uint Compute(byte[] data, uint seed = 0)
        {
            return Compute(data, 0, data.Length, seed);
        }

        public static uint Compute(byte[] data, int offset, int count, uint seed = 0)
        {
            uint crc = ~seed;
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }
            return ~crc;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }
    }
}
