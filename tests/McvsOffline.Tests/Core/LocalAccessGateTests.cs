using System.Collections.Generic;
using System.Net;
using System.Text;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using Xunit;

namespace McvsOffline.Tests.Core
{
    /// <summary>The loopback/Host/CSRF gate (design §5.2 rules 2-4), directly and through McvsServer.Handle.</summary>
    public class LocalAccessGateTests
    {
        private const int Port = 51780;

        private static GateOptions Options(string openVia = "auto") =>
            new GateOptions { Port = Port, BoundToLoopback = true, LanMode = false, HostCheck = true, OpenVia = openVia };

        private static HttpRequest Request(string method, string path, string host = "127.0.0.1:51780", string remote = "127.0.0.1",
                                           string body = null, string contentType = "application/json")
        {
            var headers = new HttpHeaders();
            if (host != null) headers.Add("Host", host);
            if (body != null && contentType != null) headers.Add("Content-Type", contentType);
            var request = new HttpRequest(method, path, headers, body == null ? null : Encoding.UTF8.GetBytes(body));
            request.RemoteEndPoint = remote == null ? null : new IPEndPoint(IPAddress.Parse(remote), 40000);
            return request;
        }

        [Theory]
        [InlineData("127.0.0.1", true)]
        [InlineData("127.1.2.3", true)]
        [InlineData("::1", true)]
        [InlineData("::ffff:127.0.0.2", true)]
        [InlineData("192.168.1.5", false)]
        [InlineData("::ffff:192.168.1.5", false)]
        [InlineData("::2", false)]
        [InlineData("fe80::1", false)]
        [InlineData(null, true)]
        public void LoopbackIsDecidedByBytes(string address, bool expected)
        {
            IPEndPoint remote = address == null ? null : new IPEndPoint(IPAddress.Parse(address), 1);
            Assert.Equal(expected, LocalAccessGate.IsLoopback(remote));
        }

        [Theory]
        [InlineData("127.0.0.1", false, true)]
        [InlineData("127.0.0.1:51780", false, true)]
        [InlineData("localhost:51780", false, true)]
        [InlineData("LOCALHOST", false, true)]
        [InlineData("[::1]:51780", false, true)]
        [InlineData("[::1]", false, true)]
        [InlineData("127.0.0.1:51781", false, false)]
        [InlineData("127.0.0.1:51781", true, true)]
        [InlineData("localhost:1234", true, true)]
        [InlineData("evil.com:51780", false, false)]
        [InlineData("evil.com", true, false)]
        [InlineData("127.0.0.1.evil.com", true, false)]
        [InlineData("127.0.0.1:", true, false)]
        [InlineData("127.0.0.1:abc", true, false)]
        [InlineData("", true, false)]
        [InlineData(null, true, false)]
        public void HostAllowlist(string host, bool anyPort, bool expected)
        {
            Assert.Equal(expected, LocalAccessGate.IsAllowedHost(host, Port, anyPort));
        }

        [Theory]
        [InlineData("http://127.0.0.1:51780", true)]
        [InlineData("http://localhost:51780", true)]
        [InlineData("http://[::1]:51780", true)]
        [InlineData("http://127.0.0.1", false)]
        [InlineData("http://127.0.0.1:51781", false)]
        [InlineData("https://127.0.0.1:51780", false)]
        [InlineData("http://evil.com:51780", false)]
        [InlineData("null", false)]
        [InlineData("", false)]
        public void OriginAllowlist(string origin, bool expected)
        {
            Assert.Equal(expected, LocalAccessGate.IsAllowedOrigin(origin, Port));
        }

        [Theory]
        [InlineData("127.0.0.1", "127.0.0.1", true)]
        [InlineData("::1", "127.0.0.1:51780", true)]
        [InlineData("::ffff:127.0.0.2", "localhost:51780", true)]
        [InlineData(null, "[::1]:51780", true)]
        [InlineData("192.168.1.5", "127.0.0.1:51780", false)]
        [InlineData("127.0.0.1", "127.0.0.1:51781", false)]
        [InlineData("127.0.0.1", "evil.com:51780", false)]
        [InlineData("127.0.0.1", "", false)]
        [InlineData("127.0.0.1", null, false)]
        public void AdminGetsNeedLoopbackAndAnAllowedHost(string remote, string host, bool allowed)
        {
            string refusal;
            HttpResponse response = LocalAccessGate.Check(Request("GET", "/_mcvs/status", host, remote), Options(), out refusal);
            Assert.Equal(allowed, response == null);
            if (!allowed)
            {
                Assert.Equal(404, response.StatusCode);
                Assert.Equal("not_found", MiniJson.ParseObject(response.BodyText)["error"]);
                Assert.Equal("no-store", response.Headers.Get("Cache-Control"));
                Assert.Null(response.Headers.Get("Access-Control-Allow-Origin"));
            }
        }

        [Fact]
        public void AdminRefusalsHoldInLanModeToo()
        {
            var lan = Options();
            lan.LanMode = true;
            lan.BoundToLoopback = false;
            Assert.Equal(404, LocalAccessGate.Check(Request("GET", "/_mcvs/status", "192.168.1.20:51780", "192.168.1.5"), lan).StatusCode);
            Assert.Equal(404, LocalAccessGate.Check(Request("GET", "/_mcvs/status", "192.168.1.20:51780", "127.0.0.1"), lan).StatusCode);
            Assert.Null(LocalAccessGate.Check(Request("GET", "/_mcvs/status"), lan));
            // Encoded and differently cased first segments are the same route for the router, so the gate sees them too.
            Assert.Equal(404, LocalAccessGate.Check(Request("GET", "/%5Fmcvs/status", "evil.com"), Options()).StatusCode);
            Assert.Equal(404, LocalAccessGate.Check(Request("GET", "/_MCVS/status", "evil.com"), Options()).StatusCode);
        }

        [Fact]
        public void OptionsUnderMcvsIs405WithoutCors()
        {
            HttpResponse response = LocalAccessGate.Check(Request("OPTIONS", "/_mcvs/api/v1/settings"), Options());
            Assert.Equal(405, response.StatusCode);
            Assert.Equal("method_not_allowed", MiniJson.ParseObject(response.BodyText)["error"]);
            foreach (var header in response.Headers) Assert.DoesNotContain("Access-Control", header.Key);
        }

        [Fact]
        public void WritesNeedTheHeaderASameOriginAndNoCrossSiteFetch()
        {
            HttpResponse Write(params (string, string)[] headers)
            {
                var request = Request("POST", "/_mcvs/api/v1/settings", body: "{}");
                foreach (var (name, value) in headers) request.Headers.Add(name, value);
                return LocalAccessGate.Check(request, Options());
            }

            Assert.Null(Write(("X-MCVS", "1")));
            Assert.Null(Write(("X-MCVS", "1"), ("Origin", "http://127.0.0.1:51780")));
            Assert.Null(Write(("X-MCVS", "1"), ("Sec-Fetch-Site", "same-origin")));
            Assert.Null(Write(("X-MCVS", "1"), ("Sec-Fetch-Site", "none")));

            Assert.Equal(403, Write().StatusCode);
            Assert.Equal(403, Write(("X-MCVS", "0")).StatusCode);
            Assert.Equal(403, Write(("X-MCVS", "1"), ("Origin", "null")).StatusCode);
            Assert.Equal(403, Write(("X-MCVS", "1"), ("Origin", "http://evil.com")).StatusCode);
            Assert.Equal(403, Write(("X-MCVS", "1"), ("Origin", "http://127.0.0.1:51781")).StatusCode);
            Assert.Equal(403, Write(("X-MCVS", "1"), ("Sec-Fetch-Site", "cross-site")).StatusCode);
            Assert.Equal(403, Write(("X-MCVS", "1"), ("Sec-Fetch-Site", "same-site")).StatusCode);
            Assert.Equal("csrf", MiniJson.ParseObject(Write().BodyText)["error"]);

            // DELETE and PUT are writes too; GET and HEAD are not.
            var delete = Request("DELETE", "/_mcvs/api/v1/session", body: "{}");
            Assert.Equal(403, LocalAccessGate.Check(delete, Options()).StatusCode);
            Assert.Null(LocalAccessGate.Check(Request("HEAD", "/_mcvs/ui"), Options()));
        }

        [Fact]
        public void FederationWritesNeedNoHeader()
        {
            Assert.Null(LocalAccessGate.Check(Request("POST", "/authorize", body: "a=b", contentType: "application/x-www-form-urlencoded"), Options()));
            Assert.Null(LocalAccessGate.Check(Request("OPTIONS", "/authorize"), Options()));
        }

        private static HttpRequest PostModeForm(string body = null, string origin = "null", string fetchSite = "none",
                                                string contentType = "application/x-www-form-urlencoded", string path = "/_mcvs/ui")
        {
            var request = Request("POST", path, body: body ?? "t=0123456789abcdef0123456789abcdef", contentType: contentType);
            if (origin != null) request.Headers.Add("Origin", origin);
            if (fetchSite != null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
            return request;
        }

        [Fact]
        public void ThePostModeFormIsTheOnlyExemption()
        {
            var post = Options("post");
            Assert.Null(LocalAccessGate.Check(PostModeForm(), post));
            Assert.Null(LocalAccessGate.Check(PostModeForm(path: "/_mcvs/ui/"), post));
            Assert.Null(LocalAccessGate.Check(PostModeForm(origin: null, fetchSite: null), post));
            Assert.Null(LocalAccessGate.Check(PostModeForm(contentType: "application/x-www-form-urlencoded; charset=UTF-8"), post));

            Assert.Equal(403, LocalAccessGate.Check(PostModeForm(), Options("auto")).StatusCode);
            Assert.Equal(403, LocalAccessGate.Check(PostModeForm(), Options("jni")).StatusCode);
            Assert.Equal(403, LocalAccessGate.Check(PostModeForm(fetchSite: "cross-site"), post).StatusCode);
            Assert.Equal(403, LocalAccessGate.Check(PostModeForm(fetchSite: "same-origin"), post).StatusCode);
            Assert.Equal(403, LocalAccessGate.Check(PostModeForm(origin: "http://evil.com"), post).StatusCode);
            Assert.Equal(403, LocalAccessGate.Check(PostModeForm(body: "{\"t\":\"x\"}", contentType: "application/json"), post).StatusCode);
            Assert.Equal(403, LocalAccessGate.Check(PostModeForm(body: "t=" + new string('a', 2048)), post).StatusCode);
            Assert.Equal(403, LocalAccessGate.Check(PostModeForm(path: "/_mcvs/api/v1/session"), post).StatusCode);
            Assert.Equal(403, LocalAccessGate.Check(PostModeForm(path: "/_mcvs/ui/index.html"), post).StatusCode);
            // Loopback and Host are still required.
            var remote = PostModeForm();
            remote.RemoteEndPoint = new IPEndPoint(IPAddress.Parse("192.168.1.5"), 1);
            Assert.Equal(404, LocalAccessGate.Check(remote, post).StatusCode);
        }

        [Fact]
        public void BodyLimitIsOneKibibyteWithoutASession()
        {
            var headers = new HttpHeaders();
            long Limit(string target, HttpHeaders h) =>
                LocalAccessGate.BodyLimit("POST", target, h, cookie => cookie == "good", (m, p) => p == "/_mcvs/api/v1/settings" ? 65536 : 0);

            Assert.Equal(-1, Limit("/authorize", headers));
            Assert.Equal(-1, Limit("/profiles/me/myprofile/_mcvs.gameProfile", headers));
            Assert.Equal(1024, Limit("/_mcvs/api/v1/settings", headers));
            Assert.Equal(1024, Limit("/%5Fmcvs/api/v1/settings?x=1", headers));
            var bad = new HttpHeaders();
            bad.Add("Cookie", "mcvs_s=bad");
            Assert.Equal(1024, Limit("/_mcvs/api/v1/settings", bad));
            var good = new HttpHeaders();
            good.Add("Cookie", "theme=dark; mcvs_s=good");
            Assert.Equal(65536, Limit("/_mcvs/api/v1/settings", good));
            Assert.Equal(1024, Limit("/_mcvs/api/v1/unknown", good));
            Assert.Equal(1024, LocalAccessGate.BodyLimit("POST", "/_mcvs/api/v1/settings", good, null, null));
        }

        // ------------------------------------------------------------------ through McvsServer.Handle

        private static McvsServer CreateServer(TempDir dir, MemoryLogSink logs = null)
        {
            var options = new ServerOptions { DataDir = dir.Combine("data"), Port = Port, ConsoleLog = false, FileLog = false };
            if (logs != null) options.ExtraSinks.Add(logs);
            return McvsServer.Create(options);
        }

        [Fact]
        public void LegacyDiagnosticsRoutesPassTheGate()
        {
            using var dir = new TempDir();
            using var server = CreateServer(dir);

            var status = server.Handle(Request("GET", "/_mcvs/status"));
            Assert.Equal(200, status.StatusCode);
            Assert.Equal("***", JsonPath.Get(MiniJson.ParseObject(status.BodyText), "settings.superPassword"));
            Assert.DoesNotContain("\"superPassword\":\"" + Settings.DefaultSuperPassword, status.BodyText);

            var lan = server.Handle(Request("GET", "/_mcvs/status", remote: "192.168.1.5"));
            Assert.Equal(404, lan.StatusCode);
            Assert.Equal("core.gate", LastRoute(server, "/_mcvs/status", "192.168.1.5"));
            Assert.Equal(404, server.Handle(Request("GET", "/_mcvs/status", host: "evil.com:51780")).StatusCode);
            Assert.Equal(404, server.Handle(Request("GET", "/_mcvs/routes", host: "127.0.0.1:51781")).StatusCode);
            Assert.Equal(200, server.Handle(Request("GET", "/_mcvs/routes", host: "localhost:51780")).StatusCode);

            var reload = Request("POST", "/_mcvs/crm/reload", body: "", contentType: "application/x-www-form-urlencoded");
            Assert.Equal(403, server.Handle(reload).StatusCode);
            reload.Headers.Add("X-MCVS", "1");
            Assert.Equal(200, server.Handle(reload).StatusCode);
        }

        private static string LastRoute(McvsServer server, string path, string remote)
        {
            var request = Request("GET", path, remote: remote);
            server.Handle(request);
            return request.RouteName;
        }
    }
}
