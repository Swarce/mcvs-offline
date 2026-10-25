using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Pandora;
using Xunit;

namespace McvsOffline.Tests.Auth
{
    /// <summary>Service discovery: eve datacenters and urls, pandora locate, and how the client joins the URLs.</summary>
    public class EvePandoraTests
    {
        private const string Cid = FedFederation.GameClientId;

        [Fact]
        public void DatacentersIsAnArrayWithOnePreferredEntryNamedAfterSettings()
        {
            using var server = new AuthTestServer();
            // Exactly the request Federation.ListDatacenters builds: the eve URL from args.txt + a relative path.
            var response = server.Get("/config/" + Cid + "/datacenters");
            Assert.Equal(200, response.StatusCode);
            var list = MiniJson.ParseArray(response.BodyText);
            var entry = Assert.IsType<Dictionary<string, object>>(Assert.Single(list));
            Assert.Equal(true, entry["preferred"]);
            Assert.Equal(server.Context.Settings.DataCenter, entry["name"]);
            Assert.Equal("US", entry["country_code"]);
            Assert.Equal("mcvs", entry["name"]);
        }

        [Theory]
        [InlineData("/config/" + Cid + "/datacenters/mcvs/urls")]
        [InlineData("//config/" + Cid + "/datacenters/mcvs/urls")]          // DLC: "{eve}/config/..." with eve ending in '/'
        [InlineData("/config/" + Cid + "/datacenters/mdc/urls")]            // a Gameloft datacenter saved by an old install
        [InlineData("/config/1234:56789:1.0.0:android:googleplay/datacenters/mcvs/urls")]
        public void UrlsAnswersEveryDatacenterWithAStatuslessServiceMap(string target)
        {
            using var server = new AuthTestServer();
            var response = server.Get(target);
            Assert.Equal(200, response.StatusCode);
            var urls = MiniJson.ParseObject(response.BodyText);

            // verify-C2 §1.2: no status (an unknown one throws FormatException, a blocking one shows popups).
            Assert.False(urls.ContainsKey("status"));
            Assert.False(urls.ContainsKey("code"));
            foreach (string key in new[] { "pandora", "ecomm_api_root", "crm_iap", "etsv2", "marketing_site", "game_portal", "ads_agency" })
            {
                Assert.True(urls.ContainsKey(key), key);
                Assert.Equal("http://127.0.0.1:51780/" + key + "/", urls[key]);
            }
            // OP/Configuration.cs turns every value into a string with JToken.ToString(): they must be plain strings.
            Assert.All(urls.Values, v => Assert.IsType<string>(v));
        }

        [Fact]
        public void LocateAnswersPlainTextServiceUrlsForEveryFedService()
        {
            using var server = new AuthTestServer();
            foreach (string service in FedFederation.FedServices)
            {
                var response = server.Get("/locate/" + service + "?client_id=" + Uri.EscapeDataString(Cid));
                Assert.Equal(200, response.StatusCode);
                Assert.StartsWith("text/plain", response.ContentType);
                Assert.Equal("http://127.0.0.1:51780/" + service + "/", response.BodyText);
            }
        }

        [Theory]
        [InlineData("/locate?service=auth", "http://127.0.0.1:51780/auth/")]          // native Gaia "/locate" + "service="
        [InlineData("/locate/feeds", "http://127.0.0.1:51780/feeds/")]                 // native-only service names
        [InlineData("/locate/something_new", "http://127.0.0.1:51780/something_new/")] // unknown: answered, logged
        [InlineData("/locate/bad%20name", "http://127.0.0.1:51780/")]                 // not URL-safe: the root
        [InlineData("/locate", "http://127.0.0.1:51780/")]
        public void LocateVariants(string target, string expected)
        {
            using var server = new AuthTestServer();
            var response = server.Get(target);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal(expected, response.BodyText);
        }

        [Fact]
        public void LocateUsesPerServiceOverridesFromSettings()
        {
            using var server = new AuthTestServer();
            server.Context.Settings.Override("pandora.urls.storage", "http://192.168.1.20:51780/storage/");
            Assert.Equal("http://192.168.1.20:51780/storage/", server.Get("/locate/storage").BodyText);
            Assert.Equal("http://127.0.0.1:51780/auth/", server.Get("/locate/auth").BodyText);
        }

        [Fact]
        public void LanModeUrlsFollowTheHostHeader()
        {
            using var server = new AuthTestServer();
            server.Context.Settings.Override("lanMode", true);
            var request = HttpRequest.Create("GET", "/locate/storage?client_id=x");
            request.Headers.Set("Host", "192.168.1.23:51780");
            Assert.Equal("http://192.168.1.23:51780/storage/", server.Handle(request).BodyText);
        }

        /// <summary>
        /// The URLs survive Federation.FormatUri for every protocol a caller forces (LocalAnubis forces HTTPS), and
        /// RFC 3986 joining (FedRequestMessage.ResolveUri) lands every relative and absolute path on the host root.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("https")]
        [InlineData("http")]
        public void LocatedUrlsResolveToRootPathsThroughFormatUri(string protocol)
        {
            using var server = new AuthTestServer();
            var federation = server.Federation(protocol: protocol);
            string auth = federation.Locate("auth");
            Assert.Equal("http://127.0.0.1:51780/auth", auth);
            Assert.Equal("http://127.0.0.1:51780/authorize", new Uri(new Uri(auth), "authorize").AbsoluteUri);
            Assert.Equal("http://127.0.0.1:51780/users/fed_id%3Aabc", new Uri(new Uri(auth), "users/fed_id%3Aabc").AbsoluteUri);
            string storage = federation.Locate("storage");
            Assert.Equal("http://127.0.0.1:51780/profiles/me/myprofile", new Uri(new Uri(storage), "profiles/me/myprofile").AbsoluteUri);
            Assert.Equal("http://127.0.0.1:51780/profiles/x", new Uri(new Uri(storage), "/profiles/x").AbsoluteUri);
            // Non-fed services come straight from the eve config.
            Assert.Equal("http://127.0.0.1:51780/ecomm_api_root", federation.Locate("ecomm_api_root"));
            // EtsRequestMessage posts to path "": the trimmed service URL itself, i.e. POST /etsv2 (a MISC endpoint).
            Assert.Equal("http://127.0.0.1:51780/etsv2", new Uri(new Uri(federation.Locate("etsv2")), "").AbsoluteUri);
            // Lobby.Login(roomUser) only takes host and port of the located lobby URL.
            var lobby = new Uri(federation.Locate("lobby"));
            Assert.Equal(51780, lobby.Port);
        }

        [Fact]
        public void TheAnonymousBootSequenceRunsAsTheClientSendsIt()
        {
            using var server = new AuthTestServer();
            var federation = server.Federation();
            var session = server.OpenClient(federation: federation);

            var paths = federation.Exchanges.Select(e => e.Request.Method + " " + e.Request.RawTarget).ToList();
            Assert.Equal("GET /config/" + Cid + "/datacenters", paths[0]);
            Assert.Equal("GET /config/" + Cid + "/datacenters/mcvs/urls", paths[1]);
            Assert.Equal("GET /locate/auth?client_id=2620%3A66102%3A1.17.32%3Aandroid%3Agoogleplay", paths[2]);
            Assert.Equal("POST /authorize", paths[3]);
            Assert.StartsWith("GET /users/me?access_token=", paths[4]);
            Assert.Equal(5, paths.Count);
            Assert.All(federation.Exchanges, e => Assert.Equal(200, e.Response.StatusCode));
            Assert.All(federation.Exchanges, e => Assert.NotEqual("unknown", e.Request.RouteName));
            Assert.StartsWith("fed_id:", session.FedId);
        }

        [Fact]
        public void UrlSafetyCheckFlagsUrlsFormatUriWouldRewrite()
        {
            Assert.Null(ServiceUrls.CheckClientUrl("http://127.0.0.1:51780/auth/"));
            Assert.Null(ServiceUrls.CheckClientUrl("http://[::1]:51780/"));
            Assert.NotNull(ServiceUrls.CheckClientUrl("http://myhost/"));
            Assert.NotNull(ServiceUrls.CheckClientUrl("http://myhost:80/"));
            Assert.NotNull(ServiceUrls.CheckClientUrl("https://myhost:443"));
            Assert.NotNull(ServiceUrls.CheckClientUrl("127.0.0.1:51780"));
            Assert.NotNull(ServiceUrls.CheckClientUrl("http://[::1]/"));
        }

        [Fact]
        public void UnsafePublicUrlIsLoggedOnLocate()
        {
            using var server = new AuthTestServer();
            server.Context.Settings.Override("publicUrl", "http://myhost/");
            Assert.Equal("http://myhost/auth/", server.Get("/locate/auth").BodyText);
            Assert.True(server.Logs.Contains("unsafe for the game client"));
        }

        // ------------------------------------------------------------------ service-prefixed paths (string joins)

        [Theory]
        [InlineData("POST", "/auth//authorize", "auth.authorize")]
        [InlineData("POST", "/auth/authorize", "auth.authorize")]
        [InlineData("GET", "/pandora//locate/asset", "pandora.locate")]                  // DownloadableContent: pandora + "/locate/asset"
        [InlineData("GET", "/pandora/locate?service=auth", "pandora.locate.query")]
        [InlineData("GET", "/config/" + Cid + "/datacenters", "eve.datacenters")]       // exact eve route still wins over "config/..."
        public void ServicePrefixedPathsReachTheRootRoute(string method, string target, string expectedRoute)
        {
            using var server = new AuthTestServer();
            var request = HttpRequest.Create(method, target, method == "POST" ? "username=p&credential_type=anonymous&password=x&device_id=d&client_id=c&scope=auth" : null);
            var response = server.Handle(request);
            Assert.Equal(200, response.StatusCode);
            if (expectedRoute.StartsWith("pandora.locate")) Assert.StartsWith("http://127.0.0.1:51780/", response.BodyText);
            if (expectedRoute == "auth.authorize") Assert.Contains("access_token", response.BodyText);
            if (target.StartsWith("/config")) Assert.Equal("eve.datacenters", request.RouteName);
            else
            {
                Assert.StartsWith(ServicePrefixAliases.RouteNamePrefix, request.RouteName);
                Assert.True(server.Logs.Contains("[" + expectedRoute + "]"), "log names the target route");
            }
        }

        [Fact]
        public void PrefixedPathsWithoutASpecificRouteReachCatchAllsUnchanged()
        {
            string seen = null;
            using var server = new AuthTestServer((router, context) =>
            {
                McvsOffline.Server.Services.ServiceRegistry.RegisterAll(router, context);
                // A broad catch-all like the MISC module's: it must see the path the client sent.
                router.Any("{*path}", "test.catchall", r => { seen = r.RouteValue("path"); return HttpResponse.EmptyObject(); });
            });
            // ads_agency is a service name we hand out but no module serves paths under it.
            Assert.Equal(200, server.Post("/ads_agency/events", "{}").StatusCode);
            Assert.Equal("ads_agency/events", seen);
            Assert.Equal(200, server.Post("/ads_agency", "x=1").StatusCode);
            Assert.Equal("ads_agency", seen);
            Assert.Equal(200, server.Get("/auth//nothing/here?x=1").StatusCode);
            Assert.Equal("auth/nothing/here", seen);

            // Tracking under the etsv2 prefix belongs to MISC's tracking route, ahead of any later catch-all.
            seen = null;
            var tracking = HttpRequest.Create("POST", "/etsv2/events", "{}", "application/json");
            Assert.Equal(200, server.Handle(tracking).StatusCode);
            Assert.Null(seen);
        }

        [Fact]
        public void PrefixedUnknownPathsAreLoggedWithTheOriginalPath()
        {
            using var server = new AuthTestServer();
            var response = server.Get("/storage/no/such/endpoint");
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("{}", response.BodyText);
            Assert.True(server.Logs.Contains("UNKNOWN ENDPOINT"));
            Assert.True(server.Logs.Contains("/storage/no/such/endpoint"));
        }

        [Fact]
        public void EveKeepsGetRootWithCore()
        {
            using var server = new AuthTestServer();
            var request = HttpRequest.Create("GET", "/");
            var response = server.Handle(request);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("on", MiniJson.ParseObject(response.BodyText)["eve"]);
            Assert.Equal("core.root", request.RouteName);
        }
    }
}
