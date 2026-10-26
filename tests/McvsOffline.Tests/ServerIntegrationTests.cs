using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using Xunit;

namespace McvsOffline.Tests
{
    /// <summary>The composed server (McvsServer + ServiceRegistry) over HTTP, as the hosts run it.</summary>
    public class ServerIntegrationTests
    {
        private static McvsServer StartServer(TempDir dir, MemoryLogSink logs, bool lan = false)
        {
            var options = new ServerOptions
            {
                DataDir = dir.Combine("data"),
                Port = 0,
                ConsoleLog = false,
                LanMode = lan,
            };
            options.ExtraSinks.Add(logs);
            var server = McvsServer.Create(options);
            server.Start();
            return server;
        }

        [Fact]
        public async Task RootUnknownEndpointsAndLogsWork()
        {
            using var dir = new TempDir();
            var logs = new MemoryLogSink();
            using (var server = StartServer(dir, logs))
            using (var http = new HttpClient())
            {
                string baseUrl = "http://127.0.0.1:" + server.Port + "/";
                Assert.Equal(baseUrl, server.Context.BaseUrl);

                var root = await http.GetAsync(baseUrl);
                Assert.Equal(200, (int)root.StatusCode);
                var rootJson = MiniJson.ParseObject(await root.Content.ReadAsStringAsync());
                Assert.Equal("on", rootJson["eve"]);

                var unknown = await http.PostAsync(baseUrl + "some/unknown/endpoint?client_id=x",
                    new StringContent("username=abc&credential_type=anonymous", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));
                Assert.Equal(200, (int)unknown.StatusCode);
                Assert.Equal("{}", await unknown.Content.ReadAsStringAsync());

                var status = MiniJson.ParseObject(await http.GetStringAsync(baseUrl + "_mcvs/status"));
                Assert.Equal(server.Context.InstanceId, status["instance"]);
                Assert.Equal("***", JsonPath.Get(status, "settings.superPassword"));

                // Diagnostics only answer this device's own names (design §5.2): a rebinding page's Host gets a 404.
                var rebound = new HttpRequestMessage(HttpMethod.Get, baseUrl + "_mcvs/status");
                rebound.Headers.Host = "evil.example:" + server.Port;
                Assert.Equal(404, (int)(await http.SendAsync(rebound)).StatusCode);
                var crmReload = await http.PostAsync(baseUrl + "_mcvs/crm/reload", new StringContent(""));
                Assert.Equal(403, (int)crmReload.StatusCode);

                var routes = MiniJson.ParseArray(await http.GetStringAsync(baseUrl + "_mcvs/routes"));
                Assert.True(routes.Count >= 3);
            }

            Assert.True(logs.Contains("UNKNOWN ENDPOINT"));
            Assert.True(logs.Contains("username = abc"));
            Assert.True(logs.Contains("[core.root]"));
            string unknownLog = File.ReadAllText(dir.Combine("data", "logs", "unknown-endpoints.jsonl"));
            Assert.Contains("/some/unknown/endpoint", unknownLog);
            string serverLog = File.ReadAllText(dir.Combine("data", "logs", "server.log"));
            Assert.Contains("POST /some/unknown/endpoint?client_id=x -> 200 [unknown]", serverLog);
            Assert.True(File.Exists(dir.Combine("data", "settings.json")));
            Assert.True(File.Exists(dir.Combine("data", "instance.json")));
        }

        [Fact]
        public void InstanceIdIsStableAcrossRestarts()
        {
            using var dir = new TempDir();
            string first;
            using (var s = StartServer(dir, new MemoryLogSink())) first = s.Context.InstanceId;
            using (var s = StartServer(dir, new MemoryLogSink())) Assert.Equal(first, s.Context.InstanceId);
            Assert.StartsWith("mcvs", first);
        }

        [Fact]
        public void LanModeBuildsUrlsFromTheHostHeader()
        {
            using var dir = new TempDir();
            using var server = StartServer(dir, new MemoryLogSink(), lan: true);
            var guest = HttpRequest.Create("GET", "/");
            guest.Headers.Set("Host", "192.168.1.23:51780");
            Assert.Equal("http://192.168.1.23:51780/", server.Context.BaseUrlFor(guest));
            guest.Headers.Set("Host", "192.168.1.23");
            Assert.Equal("http://192.168.1.23:" + server.Port + "/", server.Context.BaseUrlFor(guest));
            guest.Headers.Set("Host", "evil/path");
            Assert.Equal(server.Context.BaseUrl, server.Context.BaseUrlFor(guest));
        }

        [Fact]
        public void HandleWorksInProcessWithoutASocket()
        {
            using var dir = new TempDir();
            var options = new ServerOptions { DataDir = dir.Combine("data"), ConsoleLog = false, FileLog = false };
            using var server = McvsServer.Create(options);
            var response = server.Handle(HttpRequest.Create("GET", "/"));
            Assert.Equal(200, response.StatusCode);
            Assert.Contains("\"eve\":\"on\"", response.BodyText);
        }

        [Fact]
        public void BindAddressesParse()
        {
            Assert.Equal("127.0.0.1", McvsServer.ParseBind("127.0.0.1").ToString());
            Assert.Equal("0.0.0.0", McvsServer.ParseBind("*").ToString());
            Assert.Equal("::", McvsServer.ParseBind("::").ToString());
            Assert.Equal("127.0.0.1", McvsServer.ParseBind("localhost").ToString());
            Assert.Throws<ArgumentException>(() => McvsServer.ParseBind("not an address"));
        }

        [Fact]
        public void EventHubDeliversAndIsolatesFailures()
        {
            var hub = new EventHub(null);
            object received = null;
            using (hub.Subscribe("message.stored", p => throw new InvalidOperationException()))
            using (hub.Subscribe("message.stored", p => received = p))
            {
                Assert.Equal(2, hub.Publish("message.stored", "acc1"));
            }
            Assert.Equal("acc1", received);
            Assert.Equal(0, hub.Publish("message.stored", "acc2"));
        }
    }
}
