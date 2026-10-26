using McvsOffline.Server;
using McvsOffline.Server.Core.Http;
using Xunit;

namespace McvsOffline.Tests.Core
{
    /// <summary>
    /// Service-prefix aliases ("/auth//authorize" → "/authorize") never lead into a /_mcvs route: the gate judges a
    /// request by its own path, so "/storage/_mcvs/..." must not reach the diagnostics or settings-page routes behind its
    /// back (design §5.2).
    /// </summary>
    public class PrefixAliasGateTests
    {
        private static McvsServer Create(TempDir dir) =>
            McvsServer.Create(new ServerOptions { DataDir = dir.Combine("data"), Port = 51780, ConsoleLog = false, FileLog = false });

        private static HttpRequest Remote(string method, string target, string body = null)
        {
            var request = HttpRequest.Create(method, target, body);
            request.RemoteEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.5"), 40000);
            request.Headers.Set("Host", "127.0.0.1:51780");
            return request;
        }

        [Fact]
        public void PrefixedMcvsPathsAreNotTheDiagnosticsRoutes()
        {
            using var dir = new TempDir();
            using var server = Create(dir);

            var direct = HttpRequest.Create("GET", "/_mcvs/storage");
            Assert.Equal(200, server.Handle(direct).StatusCode);
            Assert.Equal("storage.diag.list", direct.RouteName);

            foreach (string target in new[] { "/storage/_mcvs/storage", "/auth/_mcvs/storage", "/storage/auth/_mcvs/storage" })
            {
                var prefixed = Remote("GET", target);
                server.Handle(prefixed);
                Assert.NotEqual("storage.diag.list", prefixed.RouteName);
            }

            // A write through a prefix reaches neither the CRM reload nor the unlock grant.
            var reload = Remote("POST", "/auth/_mcvs/crm/reload", "");
            server.Handle(reload);
            Assert.NotEqual("crm.reload", reload.RouteName);
            var grant = Remote("POST", "/storage/_mcvs/unlocks/grant", "account=x");
            server.Handle(grant);
            Assert.NotEqual("unlocks.grant", grant.RouteName);

            // Ordinary aliases still work.
            var locate = server.Handle(HttpRequest.Create("GET", "/pandora//locate/auth"));
            Assert.Equal(200, locate.StatusCode);
            Assert.Equal("http://127.0.0.1:51780/auth/", locate.BodyText);
        }
    }
}
