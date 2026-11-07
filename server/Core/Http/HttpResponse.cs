using System;
using System.Collections.Generic;
using System.Text;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>
    /// An HTTP response produced by a route handler. Use the static factories. The server adds Date, Server,
    /// Content-Length (or chunked framing for streams) and Connection; it never adds an ETag (storage must not send
    /// one, verify-C3 §3.5).
    /// <para>
    /// Every JSON response the game parses must have a valid JSON body: an empty body becomes a null token and most
    /// callers then throw (report 02 §1.6), so prefer <c>Json(new Dictionary...)</c> ("{}") or an empty list ("[]").
    /// </para>
    /// </summary>
    public sealed class HttpResponse
    {
        public const string JsonContentType = "application/json; charset=utf-8";
        public const string TextContentType = "text/plain; charset=utf-8";

        public HttpResponse(int statusCode)
        {
            StatusCode = statusCode;
            Headers = new HttpHeaders();
            Body = new byte[0];
        }

        public int StatusCode { get; set; }

        /// <summary>Reason phrase override; null uses the standard phrase.</summary>
        public string ReasonPhrase { get; set; }

        public HttpHeaders Headers { get; private set; }

        public byte[] Body { get; set; }

        public string ContentType
        {
            get { return Headers.Get("Content-Type"); }
            set { Headers.Set("Content-Type", value); }
        }

        /// <summary>
        /// When set, the body is produced by this callback on the connection thread (chunked on HTTP/1.1,
        /// close-delimited on HTTP/1.0), e.g. for the alerts/chat event streams. The callback may block; it should
        /// return once <see cref="HttpResponseStream.IsStopping"/> becomes true. <see cref="Body"/> is ignored.
        /// </summary>
        public Action<HttpResponseStream> StreamBody { get; set; }

        /// <summary>Close the connection after this response.</summary>
        public bool CloseConnection { get; set; }

        /// <summary>
        /// The request log writes this exchange at DEBUG instead of INFO (an expected request nobody needs to read, such as
        /// a browser's <c>/favicon.ico</c>); a status of 400 or more is still INFO, 500 or more WARN.
        /// </summary>
        public bool LogAtDebug { get; set; }

        /// <summary>The body decoded as UTF-8 (tests, logs).</summary>
        public string BodyText { get { return Body == null || Body.Length == 0 ? "" : Encoding.UTF8.GetString(Body); } }

        public HttpResponse WithHeader(string name, string value)
        {
            Headers.Set(name, value);
            return this;
        }

        // ---------------------------------------------------------------------------------- factories

        /// <summary>A MiniJson value (Dictionary / List / scalars) serialized as JSON.</summary>
        public static HttpResponse Json(object value, int status = HttpStatus.Ok)
        {
            return RawJson(MiniJson.Serialize(value), status);
        }

        /// <summary>Already-serialized JSON text, sent verbatim (e.g. a stored profile section).</summary>
        public static HttpResponse RawJson(string json, int status = HttpStatus.Ok)
        {
            var r = new HttpResponse(status);
            r.ContentType = JsonContentType;
            r.Body = Encoding.UTF8.GetBytes(json ?? "");
            return r;
        }

        /// <summary>"{}" with status 200: the conservative answer for endpoints that only need to succeed.</summary>
        public static HttpResponse EmptyObject()
        {
            return RawJson("{}");
        }

        /// <summary>"[]" with status 200 (for endpoints the game parses as arrays).</summary>
        public static HttpResponse EmptyArray()
        {
            return RawJson("[]");
        }

        /// <summary>Plain text (e.g. pandora locate, encrypt_token: read with ReadAsStringAsync, not JSON).</summary>
        public static HttpResponse Text(string text, int status = HttpStatus.Ok, string contentType = TextContentType)
        {
            var r = new HttpResponse(status);
            r.ContentType = contentType;
            r.Body = Encoding.UTF8.GetBytes(text ?? "");
            return r;
        }

        public static HttpResponse Bytes(byte[] data, string contentType, int status = HttpStatus.Ok)
        {
            var r = new HttpResponse(status);
            if (contentType != null) r.ContentType = contentType;
            r.Body = data ?? new byte[0];
            return r;
        }

        /// <summary>A response without body (Content-Length: 0).</summary>
        public static HttpResponse Empty(int status = HttpStatus.Ok)
        {
            return new HttpResponse(status);
        }

        /// <summary>An error with a small JSON body {"error": message}.</summary>
        public static HttpResponse Error(int status, string message)
        {
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["error"] = message ?? HttpStatus.ReasonPhrase(status);
            return Json(body, status);
        }

        /// <summary>A streamed response; see <see cref="StreamBody"/>.</summary>
        public static HttpResponse Stream(Action<HttpResponseStream> writer, string contentType, int status = HttpStatus.Ok)
        {
            if (writer == null) throw new ArgumentNullException("writer");
            var r = new HttpResponse(status);
            r.ContentType = contentType;
            r.StreamBody = writer;
            return r;
        }
    }
}
