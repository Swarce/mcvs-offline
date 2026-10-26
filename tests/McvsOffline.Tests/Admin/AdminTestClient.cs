using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Tests.Admin
{
    /// <summary>
    /// An in-process client of the admin API over <see cref="McvsServer.Handle"/>, with what the game's WebView sends:
    /// a loopback <c>RemoteEndPoint</c> (127.0.0.1:40000), <c>Host: 127.0.0.1:51780</c>, a same-origin <c>Origin</c> and
    /// <c>X-MCVS: 1</c> on writes, JSON bodies, and a cookie jar for <c>mcvs_s</c>. Shared by the admin test packages
    /// (owned by WP2; other packages use it without editing it).
    /// <para>
    /// Paths that do not start with '/' are relative to <c>/_mcvs/api/v1/</c>. Every header can be changed or removed
    /// through the properties before a call (null removes it), or a request can be built with <see cref="Build"/> and
    /// sent with <see cref="Send(HttpRequest)"/>.
    /// </para>
    /// </summary>
    public sealed class AdminTestClient
    {
        public const int DefaultPort = 51780;
        public const string ApiPrefix = "/_mcvs/api/v1/";

        public AdminTestClient(McvsServer server, int port = DefaultPort)
        {
            Server = server ?? throw new ArgumentNullException(nameof(server));
            Host = "127.0.0.1:" + port;
            Origin = "http://127.0.0.1:" + port;
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 40000);
            UserAgent = "Mozilla/5.0 (Linux; Android 16; SM-S901B; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/141.0 Mobile Safari/537.36";
            SendCsrfHeader = true;
        }

        /// <summary>
        /// A composed server (every module plus <c>admin</c>) that is not listening, on port 51780, without console or file
        /// logs. <paramref name="register"/> runs after the admin module (e.g. admin-logs and admin-saves).
        /// </summary>
        public static McvsServer CreateServer(string dataDir, MemoryLogSink logs = null, Action<Router, ServerContext> register = null,
                                              IClock clock = null, Action<ServerOptions> configure = null)
        {
            var options = new ServerOptions
            {
                DataDir = dataDir,
                Port = DefaultPort,
                ConsoleLog = false,
                FileLog = false,
                Clock = clock,
                RegisterServices = (r, c) =>
                {
                    ServiceRegistry.RegisterAll(r, c);
                    AdminService.Register(r, c);
                    register?.Invoke(r, c);
                },
            };
            if (logs != null) options.ExtraSinks.Add(logs);
            configure?.Invoke(options);
            return McvsServer.Create(options);
        }

        public McvsServer Server { get; }

        /// <summary>The <c>mcvs_s</c> cookie value (null: none). Updated from every <c>Set-Cookie</c> answer.</summary>
        public string SessionCookie { get; set; }

        public string Host { get; set; }

        /// <summary>Sent on non-GET requests only (as browsers do for same-origin XHR).</summary>
        public string Origin { get; set; }

        public string UserAgent { get; set; }

        /// <summary>Sent on every request when set (e.g. "cross-site").</summary>
        public string SecFetchSite { get; set; }

        /// <summary>Whether non-GET requests carry <c>X-MCVS: 1</c>.</summary>
        public bool SendCsrfHeader { get; set; }

        public IPEndPoint RemoteEndPoint { get; set; }

        /// <summary>The portal of the server under test.</summary>
        public IAdminPortal Portal => Server.Context.Features.Get<IAdminPortal>();

        /// <summary>A fresh one-time ticket through <see cref="IAdminPortal.MintPageUrl"/> (the 32 hex after "#t=").</summary>
        public string MintTicket(string trigger = AdminTriggers.Test, string screen = null)
        {
            string url = Portal?.MintPageUrl(trigger, screen) ?? throw new InvalidOperationException("the admin portal is disabled or not registered");
            return TicketOf(url);
        }

        /// <summary>The ticket in a page URL ("...#t=&lt;32 hex&gt;[&amp;...]"), or null.</summary>
        public static string TicketOf(string url)
        {
            if (url == null) return null;
            int t = url.IndexOf("#t=", StringComparison.Ordinal);
            if (t < 0) return null;
            int start = t + 3;
            int end = url.IndexOf('&', start);
            return end < 0 ? url.Substring(start) : url.Substring(start, end - start);
        }

        /// <summary>Mints a ticket and exchanges it for a session (POST session); the cookie lands in the jar.</summary>
        public HttpResponse OpenSession(string trigger = AdminTriggers.Test)
        {
            var body = new Dictionary<string, object>(StringComparer.Ordinal) { ["ticket"] = MintTicket(trigger) };
            HttpResponse response = Post("session", body);
            if (response.StatusCode != 200)
                throw new InvalidOperationException("POST session answered " + response.StatusCode + ": " + response.BodyText);
            return response;
        }

        public HttpResponse Get(string path) => Send("GET", path, null, null);

        /// <summary>POST with a JSON body: a string is sent as it is, anything else is serialized with MiniJson.</summary>
        public HttpResponse Post(string path, object body) => Send("POST", path, BodyText(body ?? "{}"), "application/json; charset=utf-8");

        public HttpResponse Delete(string path, object body = null) => Send("DELETE", path, BodyText(body ?? "{}"), "application/json; charset=utf-8");

        public HttpResponse Send(string method, string path, string body, string contentType)
        {
            return Send(Build(method, path, body, contentType));
        }

        /// <summary>Sends a request through <see cref="McvsServer.Handle"/> and applies its <c>Set-Cookie</c>.</summary>
        public HttpResponse Send(HttpRequest request)
        {
            HttpResponse response = Server.Handle(request);
            foreach (string cookie in response.Headers.GetAll("Set-Cookie")) Apply(cookie);
            return response;
        }

        /// <summary>The request <see cref="Send(string, string, string, string)"/> would send.</summary>
        public HttpRequest Build(string method, string path, string body, string contentType)
        {
            var headers = new HttpHeaders();
            if (Host != null) headers.Add("Host", Host);
            if (UserAgent != null) headers.Add("User-Agent", UserAgent);
            bool unsafeMethod = method != "GET" && method != "HEAD";
            if (unsafeMethod && Origin != null) headers.Add("Origin", Origin);
            if (unsafeMethod && SendCsrfHeader) headers.Add("X-MCVS", "1");
            if (SecFetchSite != null) headers.Add("Sec-Fetch-Site", SecFetchSite);
            if (SessionCookie != null) headers.Add("Cookie", "mcvs_s=" + SessionCookie);
            if (body != null && contentType != null) headers.Add("Content-Type", contentType);
            string target = path.StartsWith("/", StringComparison.Ordinal) ? path : ApiPrefix + path;
            var request = new HttpRequest(method, target, headers, body == null ? null : Encoding.UTF8.GetBytes(body));
            request.RemoteEndPoint = RemoteEndPoint;
            return request;
        }

        /// <summary>The response body as a JSON object.</summary>
        public static Dictionary<string, object> Json(HttpResponse response) => MiniJson.ParseObject(response.BodyText);

        /// <summary>The value at a dotted path of the response body.</summary>
        public static object At(HttpResponse response, string path) => JsonPath.Get(Json(response), path);

        private void Apply(string setCookie)
        {
            const string prefix = "mcvs_s=";
            if (setCookie == null || !setCookie.StartsWith(prefix, StringComparison.Ordinal)) return;
            int semi = setCookie.IndexOf(';');
            string value = semi < 0 ? setCookie.Substring(prefix.Length) : setCookie.Substring(prefix.Length, semi - prefix.Length);
            bool expired = setCookie.IndexOf("Max-Age=0", StringComparison.OrdinalIgnoreCase) >= 0;
            SessionCookie = expired || value.Length == 0 ? null : value;
        }

        private static string BodyText(object body) => body as string ?? MiniJson.Serialize(body);
    }
}
