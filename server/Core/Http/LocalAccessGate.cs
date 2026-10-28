using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>What <see cref="LocalAccessGate.Check(HttpRequest, GateOptions)"/> needs to know about the server, read per request.</summary>
    public sealed class GateOptions
    {
        public GateOptions()
        {
            HostCheck = true;
            OpenVia = "auto";
        }

        /// <summary>The port the server listens on (the only port allowed in a Host header of a <c>_mcvs</c> request).</summary>
        public int Port { get; set; }

        /// <summary>The listener is bound to a loopback address (the default 127.0.0.1).</summary>
        public bool BoundToLoopback { get; set; }

        /// <summary>settings.lanMode.</summary>
        public bool LanMode { get; set; }

        /// <summary>settings.security.hostCheck (default true): the Host allowlist on the Federation routes.</summary>
        public bool HostCheck { get; set; }

        /// <summary>The host part of settings.publicUrl, or null.</summary>
        public string PublicUrlHost { get; set; }

        /// <summary>settings.admin.openVia: "auto" (default), "game", "jni" or "post".</summary>
        public string OpenVia { get; set; }
    }

    /// <summary>
    /// The loopback/Host/CSRF gate in front of every route (design §5.2), called by <c>McvsServer.Handle</c> before the
    /// router. Rules, in order:
    /// <list type="number">
    /// <item>Federation routes (everything not under <c>/_mcvs</c>), only while the listener is bound to loopback, LAN mode
    /// is off and <c>security.hostCheck</c> is on: a Host header that is present must name 127.0.0.1, localhost or [::1]
    /// (any port) or the host of <c>publicUrl</c>; otherwise 404 (DNS rebinding). A missing Host (HTTP/1.0) is allowed.</item>
    /// <item><c>_mcvs</c> routes: the caller must be loopback (in-process requests count as loopback) and the Host must be
    /// 127.0.0.1, localhost or [::1] with no port or this server's port; otherwise 404 (also in LAN mode).</item>
    /// <item><c>OPTIONS</c> under <c>_mcvs</c> → 405, never a CORS header.</item>
    /// <item>Any other method than GET/HEAD under <c>_mcvs</c> needs <c>X-MCVS: 1</c>, an allowed <c>Origin</c> when one is
    /// sent (<c>null</c> is refused) and <c>Sec-Fetch-Site</c> <c>same-origin</c>/<c>none</c> when sent; otherwise 403
    /// <c>csrf</c>. The single exemption is the browser form POST of <c>WebView.postUrl</c> to <c>/_mcvs/ui</c> with
    /// <c>admin.openVia=post</c> (see <see cref="IsPostModeForm"/>).</item>
    /// <item>The body limit is enforced while the request is parsed, before its body is read (<see cref="BodyLimit"/>).</item>
    /// </list>
    /// Refusals answer the admin API's error shape <c>{"error": code, "message": ...}</c>.
    /// </summary>
    public static class LocalAccessGate
    {
        /// <summary>The CSRF header of every admin write; its value must be "1".</summary>
        public const string HeaderName = "X-MCVS";

        /// <summary>The settings page's session cookie.</summary>
        public const string SessionCookieName = "mcvs_s";

        /// <summary>The body limit under <c>/_mcvs/</c> without a valid session, and for unknown <c>_mcvs</c> routes.</summary>
        public const long AnonymousBodyLimit = 1024;

        /// <summary>Refusal reasons reported by <see cref="Check(HttpRequest, GateOptions, out string)"/>.</summary>
        public const string RefusedFederationHost = "federation-host";
        public const string RefusedRemote = "remote";
        public const string RefusedAdminHost = "admin-host";
        public const string RefusedOptions = "options";
        public const string RefusedCsrf = "csrf";

        /// <summary>True when the first path segment is <c>_mcvs</c> (ignoring case).</summary>
        public static bool IsAdminPath(HttpRequest request)
        {
            return request != null && request.Segments.Length > 0
                   && string.Equals(request.Segments[0], "_mcvs", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Null when the request may go to the router; otherwise the refusal to send instead.</summary>
        public static HttpResponse Check(HttpRequest request, GateOptions options)
        {
            string reason;
            return Check(request, options, out reason);
        }

        /// <summary>As <see cref="Check(HttpRequest, GateOptions)"/>; <paramref name="refusal"/> says which rule refused (Refused* constants).</summary>
        public static HttpResponse Check(HttpRequest request, GateOptions options, out string refusal)
        {
            refusal = null;
            if (request == null) return null;
            options = options ?? new GateOptions();
            string host = request.Headers.Get("Host");

            if (!IsAdminPath(request))
            {
                // Rule 1: the Federation routes refuse a foreign Host while only this device can reach them.
                if (!options.BoundToLoopback || options.LanMode || !options.HostCheck || host == null) return null;
                if (IsAllowedHost(host, options.Port, true) || IsPublicUrlHost(host, options.PublicUrlHost)) return null;
                refusal = RefusedFederationHost;
                return NotFound();
            }

            // Rule 2: _mcvs is for this device's own browser only.
            if (!IsLoopback(request.RemoteEndPoint))
            {
                refusal = RefusedRemote;
                return NotFound();
            }
            if (!IsAllowedHost(host, options.Port, false))
            {
                refusal = RefusedAdminHost;
                return NotFound();
            }

            // Rule 3.
            if (request.Method == "OPTIONS")
            {
                refusal = RefusedOptions;
                return Refuse(HttpStatus.MethodNotAllowed, "method_not_allowed", "This method is not allowed here.");
            }

            // Rule 4.
            if (IsUnsafeMethod(request.Method))
            {
                if (IsPostModeForm(request, options)) return null;
                if (!HasCsrfHeader(request.Headers) || !OriginAllowed(request.Headers, options.Port) || !FetchSiteAllowed(request.Headers))
                {
                    refusal = RefusedCsrf;
                    return Refuse(HttpStatus.Forbidden, "csrf", "This request was refused. Use the MCVS Offline page inside the game.");
                }
            }
            return null;
        }

        /// <summary>
        /// The one exemption of rule 4: <c>POST /_mcvs/ui</c> (or <c>/_mcvs/ui/</c>) when <c>admin.openVia</c> is "post",
        /// the body is an <c>application/x-www-form-urlencoded</c> form of at most 1 KiB, <c>Sec-Fetch-Site</c> is absent or
        /// "none" and <c>Origin</c> is absent or "null": exactly the browser-initiated form POST of <c>WebView.postUrl</c>,
        /// which can add no header. The one-time ticket in the body is the protection.
        /// </summary>
        public static bool IsPostModeForm(HttpRequest request, GateOptions options)
        {
            if (request.Method != "POST" || options == null || !string.Equals(options.OpenVia, "post", StringComparison.OrdinalIgnoreCase)) return false;
            string[] s = request.Segments;
            if (s.Length != 2 || !string.Equals(s[0], "_mcvs", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(s[1], "ui", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!IsMediaType(request.ContentType, "application/x-www-form-urlencoded")) return false;
            if (request.Body.Length > AnonymousBodyLimit) return false;
            string site = request.Headers.Get("Sec-Fetch-Site");
            if (site != null && !string.Equals(site.Trim(), "none", StringComparison.OrdinalIgnoreCase)) return false;
            string origin = request.Headers.Get("Origin");
            return origin == null || string.Equals(origin.Trim(), "null", StringComparison.Ordinal);
        }

        /// <summary>Null (in-process) | 127.0.0.0/8 | ::1 | ::ffff:127.x.y.z, by bytes.</summary>
        public static bool IsLoopback(IPEndPoint remote)
        {
            if (remote == null) return true;
            byte[] b;
            try
            {
                b = remote.Address.GetAddressBytes();
            }
            catch (Exception)
            {
                return false;
            }
            if (b.Length == 4) return b[0] == 127;
            if (b.Length != 16) return false;
            for (int i = 0; i < 10; i++)
            {
                if (b[i] != 0) return false;
            }
            if (b[10] == 0 && b[11] == 0)
            {
                // ::1 (and nothing else of ::/96).
                return b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] == 1;
            }
            return b[10] == 0xff && b[11] == 0xff && b[12] == 127; // IPv4-mapped 127.0.0.0/8
        }

        /// <summary>
        /// 127.0.0.1, localhost or [::1], case-insensitive, optionally followed by ":&lt;port&gt;"; with
        /// <paramref name="anyPort"/> false the port, when present, must equal <paramref name="port"/>.
        /// </summary>
        public static bool IsAllowedHost(string host, int port, bool anyPort)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string h = host.Trim();
            string name;
            string portText;
            if (!SplitHost(h, out name, out portText)) return false;
            if (!string.Equals(name, "127.0.0.1", StringComparison.Ordinal)
                && !string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "[::1]", StringComparison.Ordinal))
                return false;
            if (portText == null) return true;
            int value;
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value <= 0 || value > 65535) return false;
            return anyPort || value == port;
        }

        /// <summary>Exactly "http://" + an allowed host + ":" + port.</summary>
        public static bool IsAllowedOrigin(string origin, int port)
        {
            if (string.IsNullOrEmpty(origin)) return false;
            const string scheme = "http://";
            if (!origin.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = origin.Substring(scheme.Length);
            string name;
            string portText;
            if (!SplitHost(rest, out name, out portText) || portText == null) return false;
            return portText == port.ToString(CultureInfo.InvariantCulture) && IsAllowedHost(rest, port, false);
        }

        /// <summary>Anything but GET and HEAD.</summary>
        public static bool IsUnsafeMethod(string method)
        {
            return !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                   && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Header-time body limit (rule 5), asked by the HTTP parser after the headers and before the body. Under
        /// <c>/_mcvs/</c>: 1 KiB, unless the request carries a session cookie that <paramref name="isValidSessionCookie"/>
        /// accepts; then the route's limit from <paramref name="routeLimit"/> (method, decoded path; ≤ 0 or no delegate:
        /// 1 KiB). Other paths: -1, the server's global limit.
        /// </summary>
        /// <param name="path">The request-target as received (query and percent-encoding allowed).</param>
        public static long BodyLimit(string method, string path, HttpHeaders headers,
                                     Func<string, bool> isValidSessionCookie, Func<string, string, long> routeLimit)
        {
            string decoded = DecodedPath(path);
            if (!IsAdminDecodedPath(decoded)) return -1;
            if (isValidSessionCookie == null || headers == null) return AnonymousBodyLimit;
            bool session = false;
            foreach (string value in CookieValues(headers, SessionCookieName))
            {
                try
                {
                    if (isValidSessionCookie(value))
                    {
                        session = true;
                        break;
                    }
                }
                catch (Exception)
                {
                }
            }
            if (!session || routeLimit == null) return AnonymousBodyLimit;
            long limit;
            try
            {
                limit = routeLimit((method ?? "").ToUpperInvariant(), decoded);
            }
            catch (Exception)
            {
                limit = 0;
            }
            return limit > 0 ? limit : AnonymousBodyLimit;
        }

        /// <summary>Every value of the cookie <paramref name="name"/> in the request's Cookie headers.</summary>
        public static List<string> CookieValues(HttpHeaders headers, string name)
        {
            var values = new List<string>();
            if (headers == null) return values;
            foreach (string header in headers.GetAll("Cookie"))
            {
                foreach (string piece in header.Split(';'))
                {
                    int eq = piece.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!string.Equals(piece.Substring(0, eq).Trim(), name, StringComparison.Ordinal)) continue;
                    values.Add(piece.Substring(eq + 1).Trim());
                }
            }
            return values;
        }

        /// <summary>"/" + the decoded non-empty segments of a request-target, as <see cref="HttpRequest.Path"/> builds it.</summary>
        public static string DecodedPath(string target)
        {
            string rawPath, rawQuery;
            HttpRequest.SplitTarget(target ?? "/", out rawPath, out rawQuery);
            var segments = new List<string>();
            foreach (string piece in rawPath.Split('/'))
            {
                if (piece.Length > 0) segments.Add(UrlEncoding.Decode(piece, false));
            }
            return "/" + string.Join("/", segments.ToArray());
        }

        /// <summary>The media type of a Content-Type value (before ';'), compared ignoring case.</summary>
        public static bool IsMediaType(string contentType, string mediaType)
        {
            if (contentType == null) return false;
            int semi = contentType.IndexOf(';');
            string type = (semi < 0 ? contentType : contentType.Substring(0, semi)).Trim();
            return string.Equals(type, mediaType, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The host part of a URL ("http://192.168.1.20:51780/" → "192.168.1.20"), or null.</summary>
        public static string HostOfUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string u = url.Trim();
            int scheme = u.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) u = u.Substring(scheme + 3);
            int slash = u.IndexOfAny(new[] { '/', '?', '#' });
            if (slash >= 0) u = u.Substring(0, slash);
            int at = u.LastIndexOf('@');
            if (at >= 0) u = u.Substring(at + 1);
            string name;
            string port;
            if (!SplitHost(u, out name, out port) || name.Length == 0) return null;
            return name;
        }

        private static bool IsAdminDecodedPath(string decodedPath)
        {
            if (decodedPath.Length < 6) return false;
            int end = decodedPath.IndexOf('/', 1);
            string first = end < 0 ? decodedPath.Substring(1) : decodedPath.Substring(1, end - 1);
            return string.Equals(first, "_mcvs", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPublicUrlHost(string host, string publicUrlHost)
        {
            if (string.IsNullOrEmpty(publicUrlHost)) return false;
            string name;
            string port;
            if (!SplitHost(host.Trim(), out name, out port)) return false;
            return string.Equals(name, publicUrlHost, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"name[:port]" or "[v6][:port]"; false for anything malformed (empty port, several colons outside brackets).</summary>
        private static bool SplitHost(string host, out string name, out string port)
        {
            name = null;
            port = null;
            if (string.IsNullOrEmpty(host)) return false;
            if (host[0] == '[')
            {
                int close = host.IndexOf(']');
                if (close < 0) return false;
                name = host.Substring(0, close + 1);
                if (close == host.Length - 1) return true;
                if (host[close + 1] != ':' || close + 2 >= host.Length) return false;
                port = host.Substring(close + 2);
                return true;
            }
            int colon = host.IndexOf(':');
            if (colon < 0)
            {
                name = host;
                return true;
            }
            if (host.IndexOf(':', colon + 1) >= 0 || colon == host.Length - 1) return false;
            name = host.Substring(0, colon);
            port = host.Substring(colon + 1);
            return true;
        }

        private static bool HasCsrfHeader(HttpHeaders headers)
        {
            List<string> values = headers.GetAll(HeaderName);
            return values.Count == 1 && values[0].Trim() == "1";
        }

        private static bool OriginAllowed(HttpHeaders headers, int port)
        {
            foreach (string origin in headers.GetAll("Origin"))
            {
                if (!IsAllowedOrigin(origin.Trim(), port)) return false;
            }
            return true;
        }

        private static bool FetchSiteAllowed(HttpHeaders headers)
        {
            foreach (string site in headers.GetAll("Sec-Fetch-Site"))
            {
                string s = site.Trim();
                if (!string.Equals(s, "same-origin", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(s, "none", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static HttpResponse NotFound()
        {
            return Refuse(HttpStatus.NotFound, "not_found", "Not found.");
        }

        /// <summary>The admin API error shape, with the headers every admin response carries.</summary>
        public static HttpResponse Refuse(int status, string code, string message)
        {
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["error"] = code;
            body["message"] = message;
            HttpResponse response = HttpResponse.Json(body, status);
            response.Headers.Set("Cache-Control", "no-store");
            response.Headers.Set("X-Content-Type-Options", "nosniff");
            return response;
        }

        /// <summary>413 too_large with details.limit (header-time refusals and the admin API's own check).</summary>
        public static HttpResponse TooLarge(long limit)
        {
            var details = new Dictionary<string, object>(StringComparer.Ordinal);
            details["limit"] = limit;
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["error"] = "too_large";
            body["message"] = "The request is too large.";
            body["details"] = details;
            HttpResponse response = HttpResponse.Json(body, HttpStatus.PayloadTooLarge);
            response.Headers.Set("Cache-Control", "no-store");
            response.Headers.Set("X-Content-Type-Options", "nosniff");
            return response;
        }
    }
}
