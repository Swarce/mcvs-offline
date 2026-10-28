using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Server.Services.Crm;
using McvsOffline.Server.Services.Storage;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>GET status (design §4.3).</summary>
    public class StatusApiTests
    {
        private const string Ticket = "0123456789abcdef0123456789abcdef";

        [Fact]
        public void TheStandaloneStatusHasNoHost()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var client = new AdminTestClient(server);
            client.OpenSession();

            var response = client.Get("status");
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("no-store", response.Headers.Get("Cache-Control"));
            var body = AdminTestClient.Json(response);
            Assert.Equal(McvsVersion.Current, body["version"]);
            Assert.Equal(McvsVersion.Build, body["build"]);
            Assert.Null(body["host"]);
            Assert.Null(body["player"]);
            Assert.Equal(server.Context.InstanceId, JsonPath.Get(body, "server.instance"));
            Assert.Equal(51780L, JsonPath.Get(body, "server.port"));
            Assert.Equal("127.0.0.1", JsonPath.Get(body, "server.bind"));
            Assert.Equal(false, JsonPath.Get(body, "server.lanMode"));
            Assert.Equal(true, JsonPath.Get(body, "server.hostCheck"));
            Assert.Equal(server.Context.DataDir, JsonPath.Get(body, "server.dataDir"));
            Assert.Equal(false, JsonPath.Get(body, "settings.fallback"));
            Assert.Equal(0L, JsonPath.Get(body, "settings.restartPending"));
            Assert.Equal("idle", JsonPath.Get(body, "exit.state"));
            Assert.Null(JsonPath.Get(body, "exit.last"));
            Assert.True(((Dictionary<string, object>)body["staged"]).ContainsKey("pending"));
            Assert.True(JsonPath.Get(body, "crm.chars") is long chars && chars > 1000);
            CrmDocument doc = server.Context.Features.Get<CrmDocumentCache>().Get(0);
            Assert.Equal(doc.CollectorPhase.ToString(), JsonPath.Get(body, "crm.collectorPhase"));
            Assert.Equal(doc.Collector?.Failure ?? doc.Collector?.Status?.Reason, JsonPath.Get(body, "crm.collectorReason"));
            Assert.False(body.ContainsKey("storage"));
        }

        [Fact]
        public void TheHostAndThePlayerAreDescribed()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var host = new FakeAdminHost();
            host.Info.Platform = "android";
            host.Info.GameCredential = "anonymous:a1b2c3d4e5f6f9e8";
            host.Info.MissingJni = new List<string> { "android/os/StatFs.getAvailableBytes()J" };
            host.Info.CanExit = false;
            server.Context.Features.Set<IAdminHost>(host);
            var client = new AdminTestClient(server);
            client.OpenSession();

            var account = server.Context.Accounts.GetOrCreate("anonymous:a1b2c3d4e5f6f9e8");
            server.Context.Accounts.Update(account.Id, a => a.Data["alias"] = "123456789");
            var access = server.Context.Features.Get<IProfileAccess>();
            access.Set(account.Id, "_mcvs.gameProfile", new Dictionary<string, object> { ["@version"] = "0.1" });
            access.Set(account.Id, "level", 37L);

            var body = AdminTestClient.Json(client.Get("status"));
            Assert.Equal("android", JsonPath.Get(body, "host.platform"));
            Assert.Equal("anonymous:a1b2…f9e8", JsonPath.Get(body, "host.gameCredential"));
            Assert.DoesNotContain("a1b2c3d4e5f6f9e8", client.Get("status").BodyText);
            Assert.Equal(new List<object> { "android/os/StatFs.getAvailableBytes()J" }, JsonPath.Get(body, "host.missingJni"));
            Assert.Equal(false, JsonPath.Get(body, "host.capabilities.exit"));
            Assert.Equal(true, JsonPath.Get(body, "host.capabilities.clipboard"));

            Assert.Equal(account.Id, JsonPath.Get(body, "player.account"));
            Assert.Equal("fed_id:" + account.Id, JsonPath.Get(body, "player.fedId"));
            Assert.Equal("game", JsonPath.Get(body, "player.source"));
            Assert.Equal("123456789", JsonPath.Get(body, "player.alias"));
            Assert.Equal(37L, JsonPath.Get(body, "player.level"));
            Assert.Equal(true, JsonPath.Get(body, "player.hasGameProfile"));
            Assert.Equal(2L, JsonPath.Get(body, "player.revision"));
            Assert.NotNull(JsonPath.Get(body, "player.modified"));
            Assert.Null(JsonPath.Get(body, "player.lastGameSave"));

            // The game's own save (source "http") is the "last saved by the game" time.
            server.Context.Events.Publish(LocalProfileStore.ChangedTopic, new Dictionary<string, object>
            {
                ["account"] = account.Id, ["source"] = "http", ["deleted"] = false, ["selector"] = "_mcvs.gameProfile",
            });
            Assert.NotNull(AdminTestClient.At(client.Get("status"), "player.lastGameSave"));
        }

        [Fact]
        public void ProblemsComeFromBothLoggersNewestFirstWithoutSecrets()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var host = new FakeAdminHost();
            host.Problems = new List<string>
            {
                "2099-01-01T00:00:00.000Z WARN  [watchdog] game thread stalled 5 s",
                "not a formatted line",
            };
            server.Context.Features.Set<IAdminHost>(host);
            var client = new AdminTestClient(server);
            client.OpenSession();

            server.Context.Logger.For("crm").Warn("CRM document: something odd");
            server.Context.Logger.For("other").Error("opening http://127.0.0.1:51780/_mcvs/ui/#t=" + Ticket);
            server.Context.Logger.For("other").Info("just information");

            var problems = ((List<object>)AdminTestClient.At(client.Get("status"), "problems")).Cast<Dictionary<string, object>>().ToList();
            Assert.True(problems.Count <= 20);
            Assert.Equal("watchdog", problems[0]["category"]);       // the newest
            Assert.Equal("host", problems[0]["source"]);
            Assert.Equal("game thread stalled 5 s", problems[0]["message"]);
            Assert.Contains(problems, p => (string)p["category"] == "crm" && (string)p["message"] == "CRM document: something odd"
                                           && (string)p["level"] == "WARN" && (string)p["source"] == "server");
            Assert.Contains(problems, p => (string)p["message"] == "not a formatted line" && p["time"] == null);
            Assert.DoesNotContain(problems, p => ((string)p["message"]).Contains("just information"));
            string text = client.Get("status").BodyText;
            Assert.DoesNotContain(Ticket, text);
            Assert.Contains("#t=<redacted>", text);
        }

        [Fact]
        public void StorageUsageOnDemand()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var client = new AdminTestClient(server);
            client.OpenSession();
            var access = server.Context.Features.Get<IProfileAccess>();
            access.Set("0123456789abcdef01234567", "level", 3L);
            Directory.CreateDirectory(Path.Combine(server.Context.DataDir, "admin"));
            File.WriteAllText(Path.Combine(server.Context.DataDir, "admin", "x.txt"), "12345");

            var storage = (Dictionary<string, object>)AdminTestClient.At(client.Get("status?storage=1"), "storage");
            Assert.Equal(1L, JsonPath.Get(storage, "profiles.files"));
            Assert.True((long)JsonPath.Get(storage, "profiles.bytes") > 0);
            Assert.Equal(1L, JsonPath.Get(storage, "admin.files"));
            Assert.Equal(5L, JsonPath.Get(storage, "admin.bytes"));
            foreach (string kind in new[] { "backups", "blobs", "messages", "logs", "other" }) Assert.True(storage.ContainsKey(kind), kind);
            Assert.True((long)storage["total"] >= 5);
            Assert.Equal(false, storage["scanCapped"]);
        }

        [Fact]
        public void HostLinesAreParsed()
        {
            var parsed = StatusApi.ParseLine("2026-10-26T10:15:00.123Z ERROR [install] could not copy\nstack");
            Assert.Equal("2026-10-26T10:15:00.123Z", parsed["time"]);
            Assert.Equal("ERROR", parsed["level"]);
            Assert.Equal("install", parsed["category"]);
            Assert.Equal("could not copy\nstack", parsed["message"]);
            Assert.Equal("WARN", StatusApi.ParseLine("odd")["level"]);
        }
    }
}
