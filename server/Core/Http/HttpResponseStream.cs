using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>
    /// Writer handed to <see cref="HttpResponse.StreamBody"/>. Every Write is sent and flushed immediately (one
    /// chunk on HTTP/1.1), which is what line-oriented readers such as the alert listener expect
    /// (report 02 §4.8: "data: {json}" lines; an immediately-ending 200 makes the client busy-loop, so keep the
    /// stream open and write keep-alive lines while waiting).
    /// Write throws IOException once the client has gone away; let it propagate.
    /// </summary>
    public sealed class HttpResponseStream
    {
        private static readonly byte[] CrLf = { 13, 10 };

        private readonly Stream stream;
        private readonly bool chunked;
        private readonly WaitHandle stopSignal;

        internal HttpResponseStream(Stream stream, bool chunked, WaitHandle stopSignal)
        {
            this.stream = stream;
            this.chunked = chunked;
            this.stopSignal = stopSignal;
        }

        /// <summary>True once the server is shutting down: finish the stream.</summary>
        public bool IsStopping
        {
            get { return stopSignal != null && stopSignal.WaitOne(0); }
        }

        /// <summary>Sleeps up to <paramref name="milliseconds"/>; returns true early if the server is stopping.</summary>
        public bool WaitForStop(int milliseconds)
        {
            if (stopSignal == null)
            {
                Thread.Sleep(milliseconds);
                return false;
            }
            return stopSignal.WaitOne(milliseconds);
        }

        public void Write(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            Write(bytes, 0, bytes.Length);
        }

        public void Write(byte[] data, int offset, int count)
        {
            if (count <= 0) return;
            if (chunked)
            {
                byte[] header = Encoding.ASCII.GetBytes(count.ToString("x", CultureInfo.InvariantCulture) + "\r\n");
                stream.Write(header, 0, header.Length);
                stream.Write(data, offset, count);
                stream.Write(CrLf, 0, 2);
            }
            else
            {
                stream.Write(data, offset, count);
            }
            stream.Flush();
        }
    }
}
