using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>Serializes an <see cref="HttpResponse"/> onto a connection.</summary>
    internal static class HttpResponseWriter
    {
        public const string ServerHeader = "McvsOffline/" + McvsVersion.Current;

        private static readonly byte[] LastChunk = Encoding.ASCII.GetBytes("0\r\n\r\n");

        /// <summary>
        /// Writes the response. Returns false when the connection must be closed afterwards (close requested, or a
        /// close-delimited stream on HTTP/1.0).
        /// </summary>
        public static bool Write(Stream output, HttpRequest request, HttpResponse response, bool keepAlive, WaitHandle stopSignal)
        {
            bool http11 = request == null || request.ProtocolVersion != "HTTP/1.0";
            bool head = request != null && request.IsHead;
            int status = response.StatusCode;
            bool bodyAllowed = HttpStatus.AllowsBody(status);
            bool streaming = response.StreamBody != null && bodyAllowed;
            bool chunked = streaming && http11;
            if (streaming && !http11) keepAlive = false; // HTTP/1.0 stream ends when the connection closes

            var sb = new StringBuilder(256);
            sb.Append(http11 ? "HTTP/1.1 " : "HTTP/1.0 ")
              .Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(response.ReasonPhrase ?? HttpStatus.ReasonPhrase(status)).Append("\r\n");

            if (!response.Headers.Contains("Date"))
                sb.Append("Date: ").Append(DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
            if (!response.Headers.Contains("Server"))
                sb.Append("Server: ").Append(ServerHeader).Append("\r\n");

            foreach (var header in response.Headers)
            {
                if (IsManagedHeader(header.Key)) continue;
                sb.Append(header.Key).Append(": ").Append(Sanitize(header.Value)).Append("\r\n");
            }

            byte[] body = response.Body ?? new byte[0];
            if (chunked)
            {
                sb.Append("Transfer-Encoding: chunked\r\n");
                sb.Append("Cache-Control: no-cache\r\n");
            }
            else if (!streaming && bodyAllowed)
            {
                sb.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }
            if (http11)
            {
                if (!keepAlive) sb.Append("Connection: close\r\n");
            }
            else
            {
                sb.Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
            }
            sb.Append("\r\n");

            byte[] head0 = Encoding.UTF8.GetBytes(sb.ToString());
            bool sendBody = bodyAllowed && !head && !streaming && body.Length > 0;
            if (sendBody && body.Length <= 64 * 1024)
            {
                // One write for small responses (fewer packets with Nagle disabled).
                var combined = new byte[head0.Length + body.Length];
                Buffer.BlockCopy(head0, 0, combined, 0, head0.Length);
                Buffer.BlockCopy(body, 0, combined, head0.Length, body.Length);
                output.Write(combined, 0, combined.Length);
            }
            else
            {
                output.Write(head0, 0, head0.Length);
                if (sendBody) output.Write(body, 0, body.Length);
            }
            output.Flush();

            if (streaming && !head)
            {
                response.StreamBody(new HttpResponseStream(output, chunked, stopSignal));
                if (chunked)
                {
                    output.Write(LastChunk, 0, LastChunk.Length);
                    output.Flush();
                }
            }
            return keepAlive;
        }

        /// <summary>A minimal response for protocol errors (written before closing the connection).</summary>
        public static void WriteError(Stream output, int status, string message)
        {
            byte[] body = Encoding.UTF8.GetBytes("{\"error\":" + MiniJson.Quote(message ?? HttpStatus.ReasonPhrase(status)) + "}");
            string head = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + HttpStatus.ReasonPhrase(status) + "\r\n"
                + "Date: " + DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture) + "\r\n"
                + "Server: " + ServerHeader + "\r\n"
                + "Content-Type: " + HttpResponse.JsonContentType + "\r\n"
                + "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                + "Connection: close\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            output.Write(headBytes, 0, headBytes.Length);
            output.Write(body, 0, body.Length);
            output.Flush();
        }

        private static bool IsManagedHeader(string name)
        {
            return string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase);
        }

        private static string Sanitize(string value)
        {
            // Never let a handler inject header lines.
            if (value == null) return "";
            if (value.IndexOf('\r') < 0 && value.IndexOf('\n') < 0) return value;
            return value.Replace("\r", " ").Replace("\n", " ");
        }
    }
}
