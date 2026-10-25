using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>Size and time limits for the HTTP server. Defaults are generous: profile saves are multi-MB forms.</summary>
    public sealed class HttpServerLimits
    {
        public int MaxRequestLineBytes = 1024 * 1024;
        public int MaxHeaderLineBytes = 64 * 1024;
        public int MaxHeaderCount = 200;
        public long MaxBodyBytes = 64L * 1024 * 1024;

        /// <summary>Idle time allowed between requests on a kept-alive connection (and per blocking read).</summary>
        public int IdleTimeoutMs = 120 * 1000;

        /// <summary>Per blocking write; long-lived streams rely on it to notice dead clients.</summary>
        public int WriteTimeoutMs = 60 * 1000;

        public int MaxConnections = 512;
    }

    /// <summary>
    /// Reads one HTTP/1.x request from a connection: request line, headers, then the body framed by
    /// Transfer-Encoding: chunked or Content-Length. Answers "Expect: 100-continue" with an interim
    /// "100 Continue" before reading the body: Mono's HttpWebRequest-based client sends it on POSTs and may
    /// otherwise wait before sending large form bodies (report 02 §1.5, verify-C3 §7.8).
    /// </summary>
    internal static class HttpRequestParser
    {
        private static readonly byte[] ContinueResponse = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");

        /// <summary>
        /// Returns the next request, or null if the client closed the connection cleanly before sending one.
        /// Throws <see cref="HttpProtocolException"/> for malformed requests and IOException for transport errors.
        /// </summary>
        public static HttpRequest Read(HttpConnectionReader reader, Stream output, HttpServerLimits limits,
                                       IPEndPoint remote, IPEndPoint local)
        {
            // Request line (RFC 7230 §3.5: ignore empty lines before it).
            string line;
            int blankLines = 0;
            do
            {
                line = reader.ReadLine(limits.MaxRequestLineBytes, HttpStatus.UriTooLong);
                if (line == null) return null;
                if (++blankLines > 8) throw new HttpProtocolException(HttpStatus.BadRequest, "too many empty lines");
            }
            while (line.Length == 0);

            string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) throw new HttpProtocolException(HttpStatus.BadRequest, "malformed request line: " + Truncate(line, 200));
            string method = parts[0];
            string target = DecodeTarget(parts[1]);
            string version = parts[2];
            if (!version.StartsWith("HTTP/1.", StringComparison.Ordinal))
                throw new HttpProtocolException(HttpStatus.HttpVersionNotSupported, "unsupported protocol " + version);
            foreach (char c in method)
            {
                if (c <= ' ' || c > '~') throw new HttpProtocolException(HttpStatus.BadRequest, "invalid method");
            }

            // Headers.
            var headers = new HttpHeaders();
            string lastName = null;
            int count = 0;
            while (true)
            {
                string h = reader.ReadLine(limits.MaxHeaderLineBytes, HttpStatus.RequestHeaderFieldsTooLarge);
                if (h == null) throw new IOException("connection closed inside the request headers");
                if (h.Length == 0) break;
                if (++count > limits.MaxHeaderCount)
                    throw new HttpProtocolException(HttpStatus.RequestHeaderFieldsTooLarge, "too many headers");
                if ((h[0] == ' ' || h[0] == '\t') && lastName != null)
                {
                    // Obsolete line folding: continuation of the previous header value.
                    var values = headers.GetAll(lastName);
                    string folded = values[values.Count - 1] + " " + h.Trim();
                    headers.Remove(lastName);
                    for (int i = 0; i < values.Count - 1; i++) headers.Add(lastName, values[i]);
                    headers.Add(lastName, folded);
                    continue;
                }
                int colon = h.IndexOf(':');
                if (colon <= 0) throw new HttpProtocolException(HttpStatus.BadRequest, "malformed header line: " + Truncate(h, 200));
                string name = h.Substring(0, colon);
                if (name.IndexOf(' ') >= 0 || name.IndexOf('\t') >= 0)
                    throw new HttpProtocolException(HttpStatus.BadRequest, "whitespace in header name");
                headers.Add(name, h.Substring(colon + 1).Trim());
                lastName = name;
            }

            bool http11 = version == "HTTP/1.1";
            bool keepAlive = http11 ? !headers.HasToken("Connection", "close") : headers.HasToken("Connection", "keep-alive");

            // Body framing.
            bool chunked = false;
            long contentLength = 0;
            string te = headers.Get("Transfer-Encoding");
            if (te != null)
            {
                string[] codings = te.Split(',');
                if (!string.Equals(codings[codings.Length - 1].Trim(), "chunked", StringComparison.OrdinalIgnoreCase))
                    throw new HttpProtocolException(HttpStatus.NotImplemented, "unsupported Transfer-Encoding: " + te);
                chunked = true;
            }
            else
            {
                // Repeated headers or a list ("12, 12") are accepted when every value agrees (RFC 7230 §3.3.2).
                long? length = null;
                foreach (string headerValue in headers.GetAll("Content-Length"))
                {
                    foreach (string piece in headerValue.Split(','))
                    {
                        long value;
                        if (!long.TryParse(piece.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value))
                            throw new HttpProtocolException(HttpStatus.BadRequest, "invalid Content-Length");
                        if (length.HasValue && length.Value != value)
                            throw new HttpProtocolException(HttpStatus.BadRequest, "conflicting Content-Length values");
                        length = value;
                    }
                }
                contentLength = length ?? 0;
                if (contentLength > limits.MaxBodyBytes)
                    throw new HttpProtocolException(HttpStatus.PayloadTooLarge, "body of " + contentLength + " bytes exceeds the limit");
            }

            string expect = headers.Get("Expect");
            if (expect != null)
            {
                if (!string.Equals(expect.Trim(), "100-continue", StringComparison.OrdinalIgnoreCase))
                    throw new HttpProtocolException(HttpStatus.ExpectationFailed, "unsupported Expect: " + expect);
                if (http11 && (chunked || contentLength > 0))
                {
                    output.Write(ContinueResponse, 0, ContinueResponse.Length);
                    output.Flush();
                }
            }

            byte[] body;
            if (chunked)
            {
                body = ReadChunkedBody(reader, headers, limits);
            }
            else if (contentLength > 0)
            {
                body = new byte[contentLength];
                reader.ReadExact(body, 0, (int)contentLength);
            }
            else
            {
                body = new byte[0];
            }

            var request = new HttpRequest(method, target, headers, body);
            request.ProtocolVersion = version;
            request.KeepAlive = keepAlive;
            request.RemoteEndPoint = remote;
            request.LocalEndPoint = local;
            return request;
        }

        private static byte[] ReadChunkedBody(HttpConnectionReader reader, HttpHeaders headers, HttpServerLimits limits)
        {
            using (var body = new MemoryStream())
            {
                while (true)
                {
                    string sizeLine = reader.ReadLine(4096, HttpStatus.BadRequest);
                    if (sizeLine == null) throw new IOException("connection closed inside a chunked body");
                    int semi = sizeLine.IndexOf(';'); // chunk extensions are ignored
                    string hex = (semi >= 0 ? sizeLine.Substring(0, semi) : sizeLine).Trim();
                    long size;
                    if (hex.Length == 0 || hex.Length > 15
                        || !long.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out size))
                    {
                        throw new HttpProtocolException(HttpStatus.BadRequest, "invalid chunk size '" + Truncate(sizeLine, 40) + "'");
                    }
                    if (size == 0) break;
                    if (body.Length + size > limits.MaxBodyBytes)
                        throw new HttpProtocolException(HttpStatus.PayloadTooLarge, "chunked body exceeds the limit");
                    var chunk = new byte[size];
                    reader.ReadExact(chunk, 0, (int)size);
                    body.Write(chunk, 0, chunk.Length);
                    string end = reader.ReadLine(16, HttpStatus.BadRequest);
                    if (end == null || end.Length != 0) throw new HttpProtocolException(HttpStatus.BadRequest, "missing CRLF after chunk");
                }
                // Trailer section: header lines until an empty line; merged into the headers.
                int trailers = 0;
                while (true)
                {
                    string t = reader.ReadLine(limits.MaxHeaderLineBytes, HttpStatus.RequestHeaderFieldsTooLarge);
                    if (t == null || t.Length == 0) break;
                    if (++trailers > limits.MaxHeaderCount)
                        throw new HttpProtocolException(HttpStatus.RequestHeaderFieldsTooLarge, "too many trailers");
                    int colon = t.IndexOf(':');
                    if (colon > 0) headers.Add(t.Substring(0, colon), t.Substring(colon + 1).Trim());
                }
                return body.ToArray();
            }
        }

        /// <summary>The request line is read as Latin-1; re-decode raw (non-%-encoded) UTF-8 bytes in the target.</summary>
        private static string DecodeTarget(string latin1)
        {
            bool ascii = true;
            foreach (char c in latin1)
            {
                if (c > 0x7F) { ascii = false; break; }
            }
            if (ascii) return latin1;
            var bytes = new byte[latin1.Length];
            for (int i = 0; i < latin1.Length; i++) bytes[i] = (byte)latin1[i];
            return Encoding.UTF8.GetString(bytes);
        }

        private static string Truncate(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
