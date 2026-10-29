using System.Linq;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Services.Misc;
using Xunit;

namespace McvsOffline.Tests.Misc
{
    /// <summary>
    /// The native connectivity tracker's route (docs/design/pc-port.md §4.1): any form, always 200 {}, one INFO line
    /// without the device id.
    /// </summary>
    public class ConnectivityTrackerTests
    {
        /// <summary>The PC spike's post (server.log #14), as the native tracker sends it: a leading '&amp;', no port.</summary>
        private const string SpikeBody =
            "&type=auth&status=1&timestamp=2026-10-28%2022%3A41%3A20&connectionstatus=&domain=mcvs"
            + "&client_id=2620%3A70425%3A1.17.30%3Asteam%3Asteam&device_id=04%3ABF%3A1B%3A6C%3AB9%3A62";

        private const string Path = "/online_connectivity/connectionstatus";

        private static HttpRequest Post(string body)
        {
            HttpRequest request = HttpRequest.Create("POST", Path, body, "application/x-www-form-urlencoded");
            request.Headers.Add("User-Agent", "connectivity_tracker/ GlWebTools/2.0 / ()");
            return request;
        }

        private static string[] ConnectivityLines(MiscServer server) =>
            server.Logs.Snapshot().Where(e => e.Message.StartsWith("connectivity:", System.StringComparison.Ordinal)).Select(e => e.Message).ToArray();

        [Fact]
        public void TheSpikesPostGets200AndOneLineWithoutTheDeviceId()
        {
            using var server = new MiscServer();
            HttpResponse response = server.Server.Handle(Post(SpikeBody));
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("{}", response.BodyText);
            string line = Assert.Single(ConnectivityLines(server));
            Assert.Equal("connectivity: type=auth status=1 details=\"\"", line);
            Assert.Equal(LogLevel.Info, server.Logs.Snapshot().First(e => e.Message == line).Level);
            // Nowhere in the logs: the MAC (the request log redacts it) and no unknown endpoint.
            Assert.False(server.Logs.Contains("04:BF:1B:6C:B9:62"));
            Assert.False(server.Logs.Contains("B9%3A62"));
            Assert.False(server.Logs.Contains("UNKNOWN ENDPOINT"));
            Assert.True(server.Logs.Contains("[misc.connectivity]"));
        }

        [Theory]
        [InlineData("type=gs&status=0&connectionstatus=Lost%20the%20game%20server&domain=mcvs&client_id=x&port=4000&device_id=x",
                    "connectivity: type=gs status=0 details=\"Lost the game server\"")]
        [InlineData("type=mpmatch&status=1&device_id=04-BF-1B-6C-B9-62", "connectivity: type=mpmatch status=1 details=\"\"")]
        [InlineData("type=something_new&status=7", "connectivity: type=something_new status=7 details=\"\"")]  // any type
        [InlineData("", "connectivity: type=- status=- details=\"\"")]
        [InlineData("garbage&&&=&==", "connectivity: type=- status=- details=\"\"")]
        public void AnyFormIsAnswered200(string body, string expected)
        {
            using var server = new MiscServer();
            HttpResponse response = server.Server.Handle(Post(body));
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("{}", response.BodyText);
            Assert.Equal(expected, Assert.Single(ConnectivityLines(server)));
        }

        [Fact]
        public void DetailsAreCutTo100CharactersAndKeptOnOneLine()
        {
            using var server = new MiscServer();
            string details = new string('x', 98) + "\"\n" + new string('y', 50);
            server.Server.Handle(Post("type=auth&status=1&connectionstatus=" + System.Uri.EscapeDataString(details)));
            string line = Assert.Single(ConnectivityLines(server));
            Assert.Equal("connectivity: type=auth status=1 details=\"" + new string('x', 98) + "' \"", line);
        }

        [Fact]
        public void AGetIsAnsweredToo()
        {
            using var server = new MiscServer();
            HttpResponse response = server.Get(Path + "?type=auth&status=0");
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("{}", response.BodyText);
            Assert.Equal("connectivity: type=auth status=0 details=\"\"", Assert.Single(ConnectivityLines(server)));
        }

        [Fact]
        public void AFaultInTheHandlerIsStill200()
        {
            HttpResponse response = ConnectivityTracker.Handle(Log.Null, null);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("{}", response.BodyText);
        }

        [Fact]
        public void PandoraLocatesItWithoutAWarning()
        {
            using var server = new MiscServer();
            HttpResponse response = server.Get("/locate/online_connectivity");
            Assert.Equal(200, response.StatusCode);
            Assert.EndsWith("/online_connectivity/", response.BodyText);
            Assert.False(server.Logs.Contains("unknown service"));

            HttpResponse native = server.Get("/locate?service=online_connectivity");
            Assert.EndsWith("/online_connectivity/", native.BodyText);
            Assert.False(server.Logs.Contains("unknown service"));
        }

        [Fact]
        public void TheExactRouteWinsOverTheServicePrefixAlias()
        {
            using var server = new MiscServer();
            HttpRequest request = Post(SpikeBody);
            server.Server.Handle(request);
            Assert.Equal(ConnectivityTracker.RouteName, request.RouteName);
            // The alias still serves other paths under the prefix ("/online_connectivity/<root route>").
            HttpRequest aliased = HttpRequest.Create("GET", "/online_connectivity/locate/auth");
            Assert.Equal(200, server.Server.Handle(aliased).StatusCode);
            Assert.True(server.Logs.Contains("service-prefixed path /online_connectivity/locate/auth -> /locate/auth"));
        }
    }
}
