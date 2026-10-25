using System;
using System.IO;
using System.Text;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>
    /// Buffered byte reader over a connection stream: reads CRLF (or bare LF) terminated lines and exact byte
    /// counts. Bytes that belong to the next (pipelined) request stay in the buffer.
    /// </summary>
    internal sealed class HttpConnectionReader
    {
        private readonly Stream stream;
        private readonly byte[] buffer;
        private int start;
        private int end;

        public HttpConnectionReader(Stream stream, int bufferSize = 16 * 1024)
        {
            this.stream = stream;
            buffer = new byte[bufferSize];
        }

        /// <summary>
        /// Reads one line (without its terminator), decoding bytes as ISO-8859-1. Returns null if the stream ends
        /// before any byte of the line. Throws <see cref="HttpProtocolException"/> (status <paramref name="tooLongStatus"/>)
        /// when the line exceeds <paramref name="maxBytes"/>, and IOException when the stream ends mid-line.
        /// </summary>
        public string ReadLine(int maxBytes, int tooLongStatus)
        {
            StringBuilder sb = null;
            int total = 0;
            while (true)
            {
                if (start == end)
                {
                    if (!Fill())
                    {
                        if (sb == null && total == 0) return null;
                        throw new IOException("connection closed in the middle of a line");
                    }
                }
                int lf = Array.IndexOf(buffer, (byte)'\n', start, end - start);
                int stop = lf >= 0 ? lf : end;
                int count = stop - start;
                total += count;
                if (total > maxBytes) throw new HttpProtocolException(tooLongStatus, "line longer than " + maxBytes + " bytes");
                if (sb == null) sb = new StringBuilder(Num.Max(count, 16));
                AppendLatin1(sb, buffer, start, count);
                if (lf >= 0)
                {
                    start = lf + 1;
                    int len = sb.Length;
                    if (len > 0 && sb[len - 1] == '\r') sb.Length = len - 1;
                    return sb.ToString();
                }
                start = end;
            }
        }

        /// <summary>Reads exactly <paramref name="count"/> bytes into <paramref name="destination"/>.</summary>
        public void ReadExact(byte[] destination, int offset, int count)
        {
            while (count > 0)
            {
                if (start == end)
                {
                    // Large bodies: read straight into the destination instead of through the buffer.
                    if (count >= buffer.Length)
                    {
                        int n = stream.Read(destination, offset, count);
                        if (n <= 0) throw new IOException("connection closed before the request body was complete");
                        offset += n;
                        count -= n;
                        continue;
                    }
                    if (!Fill()) throw new IOException("connection closed before the request body was complete");
                }
                int take = Num.Min(count, end - start);
                Buffer.BlockCopy(buffer, start, destination, offset, take);
                start += take;
                offset += take;
                count -= take;
            }
        }

        private bool Fill()
        {
            start = 0;
            end = 0;
            int n = stream.Read(buffer, 0, buffer.Length);
            if (n <= 0) return false;
            end = n;
            return true;
        }

        private static void AppendLatin1(StringBuilder sb, byte[] bytes, int offset, int count)
        {
            for (int i = 0; i < count; i++) sb.Append((char)bytes[offset + i]);
        }
    }
}
