using System;
using System.Linq;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using Xunit;

namespace McvsOffline.Tests.Core
{
    /// <summary>
    /// Rule 1 of the gate (design §5.2): while the server listens on loopback only, a Federation request whose Host names
    /// another machine (DNS rebinding) is refused; LAN mode, a public bind and security.hostCheck=false turn it off.
    /// </summary>
    public class FederationHostCheckTests
    {
        private static McvsServer Create(TempDir dir, MemoryLogSink logs, Action<ServerOptions> configure = null)
        {
            var options = new ServerOptions { DataDir = dir.Combine("data"), Port = 51780, ConsoleLog = false, FileLog = false };
            options.ExtraSinks.Add(logs);
            configure?.Invoke(options);
            return McvsServer.Create(options);
        }

        private static HttpResponse Get(McvsServer server, string target, string host)
        {
            var headers = new HttpHeaders();
            if (host != null) headers.Add("Host", host);
            return server.Handle(new HttpRequest("GET", target, headers, null));
        }

        private const string Datacenters = "/config/2620:66102:1.17.32:android:googleplay/datacenters";

        [Fact]
        public void AForeignHostIsRefusedAndWarnedOncePerMinute()
        {
            using var dir = new TempDir();
            var logs = new MemoryLogSink();
            using var server = Create(dir, logs);

            Assert.Equal(200, Get(server, Datacenters, "127.0.0.1:51780").StatusCode);
            Assert.Equal(200, Get(server, Datacenters, "localhost:1234").StatusCode);
            Assert.Equal(200, Get(server, Datacenters, null).StatusCode);   // HTTP/1.0 without Host
            Assert.Equal(200, Get(server, "/", "[::1]:51780").StatusCode);

            var refused = Get(server, Datacenters, "evil.com:51780");
            Assert.Equal(404, refused.StatusCode);
            Assert.Equal(404, Get(server, "/", "evil.com").StatusCode);
            Assert.Equal(404, Get(server, Datacenters, "rebind.attacker.example").StatusCode);

            var warnings = logs.Snapshot().Where(e => e.Message.Contains("gate: refused Host")).ToList();
            Assert.Single(warnings);
            Assert.Equal(LogLevel.Warn, warnings[0].Level);
            Assert.Contains("'evil.com:51780'", warnings[0].Message);
            Assert.Contains("DNS rebinding?", warnings[0].Message);
            Assert.True(logs.Contains("[core.gate]"));
        }

        [Fact]
        public void ThePublicUrlHostIsAllowed()
        {
            using var dir = new TempDir();
            using var server = Create(dir, new MemoryLogSink());
            server.Context.Settings.Override("publicUrl", "http://phone.lan:51780/");
            Assert.Equal(200, Get(server, Datacenters, "phone.lan:51780").StatusCode);
            Assert.Equal(200, Get(server, Datacenters, "PHONE.LAN").StatusCode);
            Assert.Equal(404, Get(server, Datacenters, "other.lan:51780").StatusCode);
        }

        [Fact]
        public void LanModeAPublicBindOrTheSwitchTurnTheCheckOff()
        {
            using var dir = new TempDir();
            using (var lan = Create(dir, new MemoryLogSink(), o => o.LanMode = true))
                Assert.Equal(200, Get(lan, Datacenters, "192.168.1.20:51780").StatusCode);

            using (var any = Create(dir, new MemoryLogSink(), o => o.Bind = "0.0.0.0"))
                Assert.Equal(200, Get(any, Datacenters, "192.168.1.20:51780").StatusCode);

            using (var off = Create(dir, new MemoryLogSink()))
            {
                Assert.Equal(404, Get(off, Datacenters, "192.168.1.20:51780").StatusCode);
                off.Context.Settings.Override("security.hostCheck", false);
                Assert.Equal(200, Get(off, Datacenters, "192.168.1.20:51780").StatusCode);
                // The _mcvs routes keep their own check whatever the switch says.
                Assert.Equal(404, Get(off, "/_mcvs/status", "192.168.1.20:51780").StatusCode);
            }
        }
    }
}
