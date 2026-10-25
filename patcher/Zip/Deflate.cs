using System.IO;
using System.IO.Compression;

namespace McvsOffline.Patcher.Zip
{
    /// <summary>Raw DEFLATE (zip method 8), no zlib header.</summary>
    public static class Deflate
    {
        public static byte[] Compress(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
                {
                    deflate.Write(data, 0, data.Length);
                }
                return output.ToArray();
            }
        }

        public static byte[] Decompress(byte[] raw, long expectedSize)
        {
            using (var input = new MemoryStream(raw))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(expectedSize > 0 && expectedSize < int.MaxValue ? (int)expectedSize : 0))
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }
    }
}
