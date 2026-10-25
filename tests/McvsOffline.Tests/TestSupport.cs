using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;

namespace McvsOffline.Tests
{
    /// <summary>A fresh temporary directory, deleted on dispose.</summary>
    public sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcvs-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts)
        {
            var all = new List<string> { Path };
            all.AddRange(parts);
            return System.IO.Path.Combine(all.ToArray());
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (Exception) { }
        }
    }

    /// <summary>A parsed HTTP response read off a raw socket.</summary>
    public sealed class RawResponse
    {
        public int Status;
        public string StatusLine;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];
        public bool WasChunked;

        public string BodyText => Encoding.UTF8.GetString(Body);

        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>
    /// Minimal raw HTTP/1.1 client over a socket, so tests control exactly which bytes are sent
    /// (chunking, Expect, pipelining, keep-alive) and see exactly what the server returns.
    /// </summary>
    public sealed class RawHttpClient : IDisposable
    {
        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly byte[] buffer = new byte[65536];
        private int start;
        private int end;

        public RawHttpClient(int port)
        {
            client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            client.NoDelay = true;
            stream = client.GetStream();
            stream.ReadTimeout = 10000;
        }

        public void Send(string text) => Send(Encoding.UTF8.GetBytes(text));

        public void Send(byte[] bytes)
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>Reads one complete response (handles Content-Length, chunked, or close-delimited bodies).</summary>
        public RawResponse ReadResponse(bool headRequest = false)
        {
            var r = new RawResponse();
            r.StatusLine = ReadLine() ?? throw new IOException("connection closed before a response");
            r.Status = int.Parse(r.StatusLine.Split(' ')[1], CultureInfo.InvariantCulture);
            string line;
            while (!string.IsNullOrEmpty(line = ReadLine()))
            {
                int colon = line.IndexOf(':');
                r.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            if (headRequest || r.Status == 100 || r.Status == 204 || r.Status == 304) return r;
            string te = r.Header("Transfer-Encoding");
            if (te != null && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                r.WasChunked = true;
                var body = new MemoryStream();
                while (true)
                {
                    int size = int.Parse(ReadLine().Split(';')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (size == 0)
                    {
                        while (!string.IsNullOrEmpty(ReadLine())) { }
                        break;
                    }
                    body.Write(ReadExact(size));
                    ReadLine();
                }
                r.Body = body.ToArray();
            }
            else if (r.Header("Content-Length") != null)
            {
                r.Body = ReadExact(int.Parse(r.Header("Content-Length"), CultureInfo.InvariantCulture));
            }
            else
            {
                var body = new MemoryStream();
                while (true)
                {
                    if (start == end && !Fill()) break;
                    body.Write(buffer, start, end - start);
                    start = end;
                }
                r.Body = body.ToArray();
            }
            return r;
        }

        /// <summary>True when the server closed the connection (EOF) within the timeout.</summary>
        public bool IsClosedByServer(int timeoutMs = 3000)
        {
            stream.ReadTimeout = timeoutMs;
            try
            {
                if (start < end) return false;
                return !Fill();
            }
            catch (IOException)
            {
                return true; // reset counts as closed
            }
        }

        public void Dispose() => client.Dispose();

        private string ReadLine()
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (start == end && !Fill()) return sb.Length == 0 ? null : sb.ToString();
                byte b = buffer[start++];
                if (b == '\n')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                sb.Append((char)b);
            }
        }

        private byte[] ReadExact(int count)
        {
            var result = new byte[count];
            int off = 0;
            while (off < count)
            {
                if (start == end && !Fill()) throw new IOException("connection closed inside a body");
                int take = Math.Min(count - off, end - start);
                Buffer.BlockCopy(buffer, start, result, off, take);
                start += take;
                off += take;
            }
            return result;
        }

        private bool Fill()
        {
            start = 0;
            end = stream.Read(buffer, 0, buffer.Length);
            return end > 0;
        }
    }

    /// <summary>Starts an <see cref="HttpServer"/> on an ephemeral loopback port for a test.</summary>
    public sealed class TestHttpServer : IDisposable
    {
        public TestHttpServer(Func<HttpRequest, HttpResponse> handler, HttpServerLimits limits = null)
        {
            Logs = new MemoryLogSink();
            Logger = new Logger();
            Logger.AddSink(Logs);
            Server = new HttpServer(IPAddress.Loopback, 0, handler, Logger.For("http"), limits);
            Server.Start();
        }

        public HttpServer Server { get; }

        public Logger Logger { get; }

        public MemoryLogSink Logs { get; }

        public int Port => Server.Port;

        public RawHttpClient Connect() => new RawHttpClient(Port);

        public void Dispose()
        {
            Server.Stop();
            Logger.Dispose();
        }
    }
}
