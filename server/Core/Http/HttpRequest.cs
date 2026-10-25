using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>
    /// A parsed HTTP request.
    /// <para>
    /// Paths: <see cref="RawTarget"/> is exactly what the client sent; <see cref="RawPath"/> is its path part
    /// (still percent-encoded). <see cref="Segments"/> are the decoded, non-empty path segments: empty segments are
    /// dropped, so "//config/x" and "/config/x/" route like "/config/x" (verify-C1 §2: DLC builds "{url}/config/..."
    /// with a trailing-slash base), and a "%2F" inside a segment stays inside that segment. Fed ids reach us both as
    /// "fed_id%3A..." and "fed_id:..." (verify-C3 §2.1); both decode to the same segment, and the raw form stays
    /// available through <see cref="RawSegments"/>.
    /// </para>
    /// <para>
    /// Parameters: GET carries them in the query, POST in an x-www-form-urlencoded body, DELETE in both
    /// (report 02 §1.4). <see cref="Param"/> looks in the form first, then the query.
    /// </para>
    /// </summary>
    public sealed class HttpRequest
    {
        private string bodyText;
        private ParamCollection form;
        private bool jsonParsed;
        private object json;
        private Dictionary<string, object> items;

        public HttpRequest(string method, string target, HttpHeaders headers, byte[] body)
        {
            if (string.IsNullOrEmpty(method)) throw new ArgumentException("method required", "method");
            Method = method.ToUpperInvariant();
            RawTarget = target ?? "/";
            Headers = headers ?? new HttpHeaders();
            Body = body ?? new byte[0];
            ProtocolVersion = "HTTP/1.1";
            ReceivedUtc = DateTime.UtcNow;
            RouteValues = RouteValues.Empty;

            string rawPath, rawQuery;
            SplitTarget(RawTarget, out rawPath, out rawQuery);
            RawPath = rawPath;
            RawQuery = rawQuery;
            Query = ParamCollection.Parse(rawQuery);

            var raw = new List<string>();
            foreach (string piece in rawPath.Split('/'))
            {
                if (piece.Length > 0) raw.Add(piece);
            }
            RawSegments = raw.ToArray();
            Segments = new string[RawSegments.Length];
            for (int i = 0; i < RawSegments.Length; i++) Segments[i] = UrlEncoding.Decode(RawSegments[i], false);
            Path = "/" + string.Join("/", Segments);
        }

        /// <summary>Builds a request in memory (tests, in-process dispatch). Body text is sent as UTF-8.</summary>
        public static HttpRequest Create(string method, string target, string body = null, string contentType = null)
        {
            var headers = new HttpHeaders();
            headers.Add("Host", "127.0.0.1");
            byte[] bytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
            if (body != null) headers.Add("Content-Type", contentType ?? "application/x-www-form-urlencoded; charset=utf-8");
            return new HttpRequest(method, target, headers, bytes);
        }

        /// <summary>Sequential id assigned by the server (for correlating log lines).</summary>
        public long Id { get; set; }

        /// <summary>Upper-case method (GET, POST, DELETE...).</summary>
        public string Method { get; private set; }

        /// <summary>The request-target exactly as received.</summary>
        public string RawTarget { get; private set; }

        /// <summary>Path part of the target, percent-encoded, as received (starts with '/').</summary>
        public string RawPath { get; private set; }

        /// <summary>Query string without '?', still encoded; null when the target had no '?'.</summary>
        public string RawQuery { get; private set; }

        /// <summary>Normalized decoded path: "/" + Segments joined by "/". For display and simple checks.</summary>
        public string Path { get; private set; }

        /// <summary>Decoded non-empty path segments.</summary>
        public string[] Segments { get; private set; }

        /// <summary>Non-empty path segments as received (percent-encoding intact).</summary>
        public string[] RawSegments { get; private set; }

        public ParamCollection Query { get; private set; }

        public HttpHeaders Headers { get; private set; }

        public byte[] Body { get; private set; }

        /// <summary>"HTTP/1.1" or "HTTP/1.0".</summary>
        public string ProtocolVersion { get; set; }

        public IPEndPoint RemoteEndPoint { get; set; }

        public IPEndPoint LocalEndPoint { get; set; }

        public DateTime ReceivedUtc { get; set; }

        /// <summary>Whether the client asked to keep the connection open (computed by the parser).</summary>
        public bool KeepAlive { get; set; }

        /// <summary>Name of the matched route ("unknown" for the fallback); set by the router.</summary>
        public string RouteName { get; private set; }

        /// <summary>Values captured by the route template; set by the router.</summary>
        public RouteValues RouteValues { get; private set; }

        public string ContentType { get { return Headers.Get("Content-Type"); } }

        public string Host { get { return Headers.Get("Host"); } }

        public bool IsHead { get { return Method == "HEAD"; } }

        /// <summary>Body decoded as UTF-8 (BOM stripped).</summary>
        public string BodyText
        {
            get
            {
                if (bodyText == null)
                {
                    string text = Body.Length == 0 ? "" : Encoding.UTF8.GetString(Body);
                    if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
                    bodyText = text;
                }
                return bodyText;
            }
        }

        public bool IsFormBody
        {
            get
            {
                string ct = ContentType;
                return ct != null && ct.IndexOf("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public bool IsJsonBody
        {
            get
            {
                string ct = ContentType;
                return ct != null && ct.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        /// <summary>Form parameters (empty unless the body is application/x-www-form-urlencoded).</summary>
        public ParamCollection Form
        {
            get
            {
                if (form == null) form = IsFormBody && Body.Length > 0 ? ParamCollection.Parse(BodyText) : ParamCollection.Empty;
                return form;
            }
        }

        /// <summary>The body parsed as JSON when the content type says JSON; null when absent or invalid.</summary>
        public object Json
        {
            get
            {
                if (!jsonParsed)
                {
                    jsonParsed = true;
                    object value;
                    if (IsJsonBody && Body.Length > 0 && MiniJson.TryParse(BodyText, out value)) json = value;
                }
                return json;
            }
        }

        /// <summary>A request parameter from the form body, else from the query string; null if absent.</summary>
        public string Param(string name)
        {
            return Form.Get(name) ?? Query.Get(name);
        }

        public bool HasParam(string name)
        {
            return Form.Contains(name) || Query.Contains(name);
        }

        /// <summary>Decoded value captured by the route template, or null.</summary>
        public string RouteValue(string name)
        {
            return RouteValues.Get(name);
        }

        /// <summary>Per-request scratch space for modules (e.g. a resolved token).</summary>
        public Dictionary<string, object> Items
        {
            get
            {
                if (items == null) items = new Dictionary<string, object>(StringComparer.Ordinal);
                return items;
            }
        }

        internal void SetRoute(string name, RouteValues values)
        {
            RouteName = name;
            RouteValues = values ?? RouteValues.Empty;
        }

        public override string ToString()
        {
            return Method + " " + RawTarget;
        }

        /// <summary>
        /// Splits a request-target into path and query. Accepts origin-form ("/a?b"), absolute-form
        /// ("http://h:p/a?b", as sent to proxies) and drops any fragment.
        /// </summary>
        internal static void SplitTarget(string target, out string rawPath, out string rawQuery)
        {
            string t = target ?? "";
            int hash = t.IndexOf('#');
            if (hash >= 0) t = t.Substring(0, hash);

            int schemeEnd = t.IndexOf("://", StringComparison.Ordinal);
            if (t.Length > 0 && t[0] != '/' && schemeEnd > 0)
            {
                int pathStart = t.IndexOf('/', schemeEnd + 3);
                int queryStart = t.IndexOf('?', schemeEnd + 3);
                if (pathStart < 0 || (queryStart >= 0 && queryStart < pathStart)) t = "/" + (queryStart >= 0 ? t.Substring(queryStart) : "");
                else t = t.Substring(pathStart);
            }

            int q = t.IndexOf('?');
            if (q >= 0)
            {
                rawPath = t.Substring(0, q);
                rawQuery = t.Substring(q + 1);
            }
            else
            {
                rawPath = t;
                rawQuery = null;
            }
            if (rawPath.Length == 0 || rawPath[0] != '/') rawPath = "/" + rawPath;
        }
    }
}
