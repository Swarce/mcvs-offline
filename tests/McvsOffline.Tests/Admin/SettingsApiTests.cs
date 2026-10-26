using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Server.Services.Storage;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>GET/POST settings, reset, file, repair and codes over McvsServer.Handle (design §4.4).</summary>
    public class SettingsApiTests
    {
        private sealed class Rig : IDisposable
        {
            private readonly TempDir dir = new TempDir();

            public Rig(string settingsJson = null)
            {
                Directory.CreateDirectory(DataDir);
                if (settingsJson != null) File.WriteAllText(SettingsPath, settingsJson);
                Logs = new MemoryLogSink();
                Server = AdminTestClient.CreateServer(DataDir, Logs);
                Client = new AdminTestClient(Server);
                Client.OpenSession();
            }

            public string DataDir => dir.Combine("data");
            public string SettingsPath => dir.Combine("data", "settings.json");
            public McvsServer Server { get; }
            public AdminTestClient Client { get; }
            public MemoryLogSink Logs { get; }
            public AdminApi Api => Server.Context.Features.Get<AdminApi>();

            public Dictionary<string, object> Disk() => MiniJson.ParseObject(File.ReadAllText(SettingsPath));

            public McvsOffline.Server.Core.Http.HttpResponse Save(Dictionary<string, object> changes, bool force = false, string fingerprint = null)
            {
                var body = new Dictionary<string, object> { ["changes"] = changes, ["force"] = force };
                if (fingerprint != null) body["fingerprint"] = fingerprint;
                return Client.Post("settings", body);
            }

            public void Dispose()
            {
                Server.Dispose();
                dir.Dispose();
            }
        }

        private static Dictionary<string, object> Entry(Dictionary<string, object> settings, string key)
        {
            foreach (Dictionary<string, object> group in ((List<object>)settings["groups"]).Cast<Dictionary<string, object>>())
            foreach (Dictionary<string, object> entry in ((List<object>)group["keys"]).Cast<Dictionary<string, object>>())
                if ((string)entry["key"] == key) return entry;
            return null;
        }

        [Fact]
        public void GetDescribesEveryGroupWithValues()
        {
            using var rig = new Rig();
            var body = AdminTestClient.Json(rig.Client.Get("settings"));
            Assert.Equal(rig.SettingsPath, body["file"]);
            Assert.Equal(false, body["fallback"]);
            Assert.Null(body["error"]);
            Assert.Matches("^[0-9]+:[0-9]+$", (string)body["fingerprint"]);
            Assert.Empty((List<object>)body["restartPending"]);
            var groups = ((List<object>)body["groups"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(new[] { "gameplay", "economy", "unlocks", "liveops", "diagnostics", "advanced" }, groups.Select(g => (string)g["id"]));
            Assert.Equal("Unlocks & codes", groups[2]["label"]);

            var coins = Entry(body, "economy.versusCoinsMultiplier");
            Assert.Equal("number", coins["type"]);
            Assert.Equal(0.1, coins["min"]);
            Assert.Equal(20.0, coins["max"]);
            Assert.Equal(1.0, coins["value"]);
            Assert.Equal(1.0, coins["default"]);
            Assert.Equal("menu", coins["apply"]);
            Assert.Equal(true, coins["editable"]);
            Assert.True(coins.ContainsKey("confirm"));
            Assert.Equal(400L, Entry(body, "unlocks.batchSize")["value"]);
            Assert.Equal("enable", Entry(body, "devMenu")["confirmWhen"]);

            var password = Entry(body, "superPassword");
            Assert.Equal(false, password["editable"]);
            Assert.Equal("\u2022\u2022\u2022\u2022 (default)", password["display"]);
            Assert.False(password.ContainsKey("value"));
            Assert.Equal("not set", Entry(body, "crm.overlay")["display"]);
            Assert.Equal(51780L, Entry(body, "port")["value"]);
            Assert.DoesNotContain("mcvsoffline\"", rig.Client.Get("settings").BodyText.Replace("game:mcvsoffline\"", ""));

            var presets = ((List<object>)body["presets"]).Cast<Dictionary<string, object>>().ToList();
            var relaxed = (Dictionary<string, object>)presets.Single(p => (string)p["id"] == "relaxed")["values"];
            Assert.Equal(0.5, relaxed["economy.priceMultiplier"]);
        }

        [Fact]
        public void PostWritesSettingsJsonAndLogsEachChange()
        {
            using var rig = new Rig();
            var response = rig.Save(new Dictionary<string, object> { ["economy.versusCoinsMultiplier"] = 2.0, ["instantBots"] = false });
            Assert.Equal(200, response.StatusCode);
            var body = AdminTestClient.Json(response);
            Assert.Equal(true, body["ok"]);
            Assert.Equal(false, body["restartRequired"]);
            Assert.Null(body["backup"]);
            Assert.Null(body["overwrittenCopy"]);
            var applied = ((List<object>)body["applied"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Contains(applied, a => (string)a["key"] == "economy.versusCoinsMultiplier" && (string)a["apply"] == "menu");

            var disk = rig.Disk();
            Assert.Equal(2.0, JsonPath.Get(disk, "economy.versusCoinsMultiplier"));
            Assert.Equal(false, disk["instantBots"]);
            Assert.Equal(2.0, rig.Server.Context.Settings.VersusCoinsMultiplier);
            Assert.Equal(2.0, Entry((Dictionary<string, object>)body["settings"], "economy.versusCoinsMultiplier")["value"]);
            Assert.True(rig.Logs.Contains("admin: settings economy.versusCoinsMultiplier 1 -> 2 (menu)"));
            Assert.True(rig.Logs.Contains("admin: settings instantBots true -> false (menu)"));

            // Nested keys are created where needed.
            Assert.Equal(200, rig.Save(new Dictionary<string, object> { ["storage.backups.keep"] = 5L, ["geo.country"] = "DE" }).StatusCode);
            Assert.Equal(5L, JsonPath.Get(rig.Disk(), "storage.backups.keep"));
            Assert.Equal("DE", JsonPath.Get(rig.Disk(), "geo.country"));
        }

        [Fact]
        public void OneBadValueWritesNothing()
        {
            using var rig = new Rig();
            string before = File.ReadAllText(rig.SettingsPath);
            var response = rig.Save(new Dictionary<string, object>
            {
                ["economy.xpMultiplier"] = 3.0,
                ["liveops.seasonDays"] = 99L,
                ["geo.country"] = "germany",
            });
            Assert.Equal(400, response.StatusCode);
            Assert.Equal("invalid", AdminTestClient.At(response, "error"));
            var problems = ((List<object>)AdminTestClient.At(response, "details.problems")).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(new[] { "liveops.seasonDays", "geo.country" }, problems.Select(p => (string)p["key"]));
            Assert.All(problems, p => Assert.False(string.IsNullOrEmpty((string)p["message"])));
            Assert.Equal(before, File.ReadAllText(rig.SettingsPath));
            Assert.Equal(1.0, rig.Server.Context.Settings.XpMultiplier);
        }

        [Theory]
        [InlineData("port")]
        [InlineData("superPassword")]
        [InlineData("crm.overlay")]
        [InlineData("security.hostCheck")]
        [InlineData("admin.enabled")]
        [InlineData("no.such.key")]
        public void KeysOutsideTheAllowlistAreRefused(string key)
        {
            using var rig = new Rig();
            string before = File.ReadAllText(rig.SettingsPath);
            var response = rig.Save(new Dictionary<string, object> { [key] = 1L, ["economy.xpMultiplier"] = 2.0 });
            Assert.Equal(400, response.StatusCode);
            Assert.Equal("not_editable", AdminTestClient.At(response, "error"));
            Assert.Equal(new List<object> { key }, AdminTestClient.At(response, "details.keys"));
            Assert.Equal(before, File.ReadAllText(rig.SettingsPath));
        }

        [Fact]
        public void AnExternalEditIsNotOverwrittenWithoutForce()
        {
            using var rig = new Rig();
            string fingerprint = (string)AdminTestClient.At(rig.Client.Get("settings"), "fingerprint");

            var edited = rig.Disk();
            edited["handEdited"] = "by the player on a PC";
            File.WriteAllText(rig.SettingsPath, MiniJson.Serialize(edited, true));
            File.SetLastWriteTimeUtc(rig.SettingsPath, DateTime.UtcNow.AddSeconds(5));

            var conflict = rig.Save(new Dictionary<string, object> { ["economy.xpMultiplier"] = 2.0 }, fingerprint: fingerprint);
            Assert.Equal(409, conflict.StatusCode);
            Assert.Equal("file_changed", AdminTestClient.At(conflict, "error"));
            Assert.Equal("by the player on a PC", rig.Disk()["handEdited"]);

            var forced = rig.Save(new Dictionary<string, object> { ["economy.xpMultiplier"] = 2.0 }, force: true, fingerprint: fingerprint);
            Assert.Equal(200, forced.StatusCode);
            string copy = (string)AdminTestClient.At(forced, "overwrittenCopy");
            Assert.StartsWith("settings.json.bak-", copy);
            Assert.Contains("by the player on a PC", File.ReadAllText(Path.Combine(rig.DataDir, copy)));
            Assert.Equal(2.0, JsonPath.Get(rig.Disk(), "economy.xpMultiplier"));
            Assert.False(rig.Disk().ContainsKey("handEdited"));

            // The page's own save is the new reference: no conflict now; an old fingerprint is one, though.
            Assert.Equal(200, rig.Save(new Dictionary<string, object> { ["economy.xpMultiplier"] = 3.0 }).StatusCode);
            Assert.Equal(409, rig.Save(new Dictionary<string, object> { ["economy.xpMultiplier"] = 4.0 }, fingerprint: fingerprint).StatusCode);
        }

        [Fact]
        public void ForcedOverwritesKeepTheNewestThreeCopies()
        {
            using var rig = new Rig();
            var clock = rig.Server.Context.Clock;
            for (int i = 0; i < 5; i++)
            {
                File.AppendAllText(rig.SettingsPath, " ");
                File.SetLastWriteTimeUtc(rig.SettingsPath, DateTime.UtcNow.AddSeconds(10 + i));
                Assert.Equal(200, rig.Save(new Dictionary<string, object> { ["economy.xpMultiplier"] = 1.0 + i }, force: true).StatusCode);
            }
            Assert.Equal(3, Directory.GetFiles(rig.DataDir, "settings.json.bak-*").Length);
        }

        [Fact]
        public void RiskyChangesBackUpTheCurrentPlayerFirst()
        {
            using var rig = new Rig();
            var host = new FakeAdminHost();
            host.Info.GameCredential = "anonymous:player1";
            rig.Server.Context.Features.Set<IAdminHost>(host);
            string account = rig.Server.Context.Accounts.GetOrCreate("anonymous:player1").Id;

            // Before admin-saves sets the delegate: a warning, the change is still saved.
            var noDelegate = rig.Save(new Dictionary<string, object> { ["devMenu"] = true });
            Assert.Equal(200, noDelegate.StatusCode);
            Assert.Null(AdminTestClient.At(noDelegate, "backup"));
            Assert.Single((List<object>)AdminTestClient.At(noDelegate, "warnings"));
            Assert.Equal(200, rig.Save(new Dictionary<string, object> { ["devMenu"] = false }).StatusCode);

            var calls = new List<string>();
            rig.Api.CreateBackup = (a, reason) =>
            {
                calls.Add(a + ":" + reason);
                return new BackupInfo { Id = "20261026T101500123Z-r5-" + reason, Reason = reason };
            };
            var enable = rig.Save(new Dictionary<string, object> { ["devMenu"] = true, ["economy.xpMultiplier"] = 2.0 });
            Assert.Equal(200, enable.StatusCode);
            Assert.Equal(new[] { account + ":presettings" }, calls);
            Assert.Equal("20261026T101500123Z-r5-presettings", AdminTestClient.At(enable, "backup.id"));
            Assert.Equal(true, AdminTestClient.At(enable, "restartRequired"));

            // Already on: no backup. Turning it off: none either. A changed season length ("any"): one.
            rig.Save(new Dictionary<string, object> { ["devMenu"] = true });
            rig.Save(new Dictionary<string, object> { ["devMenu"] = false });
            Assert.Single(calls);
            rig.Save(new Dictionary<string, object> { ["liveops.seasonDays"] = 7L });  // unchanged
            Assert.Single(calls);
            rig.Save(new Dictionary<string, object> { ["liveops.seasonDays"] = 14L, ["unlockAll"] = true });
            Assert.Equal(2, calls.Count);

            // No current player: a warning instead.
            host.Info.GameCredential = null;
            host.Info.DeviceId = "unknown-device";
            var unknown = rig.Save(new Dictionary<string, object> { ["liveops.seasonDays"] = 21L });
            Assert.Equal(200, unknown.StatusCode);
            Assert.Null(AdminTestClient.At(unknown, "backup"));
            Assert.Single((List<object>)AdminTestClient.At(unknown, "warnings"));
        }

        [Fact]
        public void RestartKeysArePendingUntilTheNextStart()
        {
            using var rig = new Rig();
            Assert.Equal(200, rig.Save(new Dictionary<string, object> { ["devMenu"] = true, ["logging.fileLevel"] = "info" }).StatusCode);
            var pending = (List<object>)AdminTestClient.At(rig.Client.Get("settings"), "restartPending");
            Assert.Equal(new List<object> { "devMenu", "logging.fileLevel" }, pending.OrderBy(k => (string)k).ToList());
            Assert.Equal(2L, AdminTestClient.At(rig.Client.Get("status"), "settings.restartPending"));
            Assert.Equal(200, rig.Save(new Dictionary<string, object> { ["devMenu"] = false }).StatusCode);
            Assert.Equal(new List<object> { "logging.fileLevel" }, AdminTestClient.At(rig.Client.Get("settings"), "restartPending"));
        }

        [Fact]
        public void ResetWritesTheDefaults()
        {
            using var rig = new Rig();
            rig.Save(new Dictionary<string, object> { ["economy.xpMultiplier"] = 3.0, ["liveops.modifiers"] = "coinRush" });
            var reset = rig.Client.Post("settings/reset", new Dictionary<string, object> { ["keys"] = new List<object> { "economy.xpMultiplier", "liveops.modifiers" } });
            Assert.Equal(200, reset.StatusCode);
            Assert.Equal(1.0, JsonPath.Get(rig.Disk(), "economy.xpMultiplier"));
            Assert.Equal("rotate", JsonPath.Get(rig.Disk(), "liveops.modifiers"));
            Assert.Equal(400, rig.Client.Post("settings/reset", new Dictionary<string, object> { ["keys"] = new List<object> { "port" } }).StatusCode);
            Assert.Equal(400, rig.Client.Post("settings/reset", "{}").StatusCode);
        }

        [Fact]
        public void ABrokenFileIsShownMaskedAndRepaired()
        {
            const string broken = "{\n  \"port\": 51780,\n  \"superPassword\" : \"MySecret_42\",\n  \"economy\": { oops }\n}\n";
            using var rig = new Rig(broken);

            var get = AdminTestClient.Json(rig.Client.Get("settings"));
            Assert.Equal(true, get["fallback"]);
            Assert.Contains("offset", (string)get["error"]);
            var status = rig.Client.Get("status");
            Assert.Equal(true, AdminTestClient.At(status, "settings.fallback"));

            var file = rig.Client.Get("settings/file");
            Assert.Equal(200, file.StatusCode);
            Assert.StartsWith("text/plain", file.ContentType);
            Assert.Contains("\"superPassword\" : \"***\"", file.BodyText);
            Assert.DoesNotContain("MySecret_42", file.BodyText);
            Assert.Contains("{ oops }", file.BodyText);

            var refused = rig.Save(new Dictionary<string, object> { ["economy.xpMultiplier"] = 2.0 });
            Assert.Equal(409, refused.StatusCode);
            Assert.Equal("settings_fallback", AdminTestClient.At(refused, "error"));

            Assert.Equal(400, rig.Client.Post("settings/repair", new Dictionary<string, object> { ["confirm"] = "yes" }).StatusCode);
            var repair = rig.Client.Post("settings/repair", new Dictionary<string, object> { ["confirm"] = "move-aside" });
            Assert.Equal(200, repair.StatusCode);
            string movedTo = (string)AdminTestClient.At(repair, "movedTo");
            Assert.StartsWith("settings.json.broken-", movedTo);
            Assert.Equal(true, AdminTestClient.At(repair, "restartRequired"));
            Assert.Equal(broken, File.ReadAllText(Path.Combine(rig.DataDir, movedTo)));

            Assert.Equal(200, rig.Save(new Dictionary<string, object> { ["economy.xpMultiplier"] = 2.0 }).StatusCode);
            Assert.Equal(2.0, JsonPath.Get(rig.Disk(), "economy.xpMultiplier"));
            Assert.Equal(404, rig.Client.Get("settings/file").StatusCode);
            Assert.Equal(409, rig.Client.Post("settings/repair", new Dictionary<string, object> { ["confirm"] = "move-aside" }).StatusCode);
        }

        [Fact]
        public void SettingsFileIsOnlyForABrokenFile()
        {
            using var rig = new Rig();
            Assert.Equal(404, rig.Client.Get("settings/file").StatusCode);
        }

        [Fact]
        public void MaskingWorksOnBrokenJson()
        {
            Assert.Equal("{\"superPassword\":\"***\"}", SettingsApi.MaskSuperPassword("{\"superPassword\":\"abc\\\"def\"}"));
            Assert.Equal("a \"superPassword\"\n:\n \"***\", \"superPassword\": \"***\"",
                         SettingsApi.MaskSuperPassword("a \"superPassword\"\n:\n \"x\", \"superPassword\": \"y\""));
            Assert.Equal("\"superPassword\": \"***\"", SettingsApi.MaskSuperPassword("\"superPassword\": \"unterminated"));
            Assert.Equal("\"superPassword\": 42", SettingsApi.MaskSuperPassword("\"superPassword\": 42"));
            Assert.Equal("nothing", SettingsApi.MaskSuperPassword("nothing"));
        }

        [Fact]
        public void CodesListTheRedeemCodes()
        {
            using var rig = new Rig();
            rig.Server.Context.Settings.Override("assets.coupons", MiniJson.ParseObject("{\"GIFT500\":{\"rewards\":[{\"type\":\"HardCurrency\",\"quantity\":500}],\"description\":\"500 gold\"}}"));
            var body = AdminTestClient.Json(rig.Client.Get("codes"));
            var codes = ((List<object>)body["codes"]).Cast<Dictionary<string, object>>().ToList();
            var unlockAll = codes.Single(c => (string)c["code"] == "UNLOCKALL");
            Assert.Equal(true, unlockAll["numericSuffix"]);
            Assert.False(string.IsNullOrEmpty((string)unlockAll["description"]));
            var gift = codes.Single(c => (string)c["code"] == "GIFT500");
            Assert.Equal("500 gold", gift["description"]);
            Assert.Equal(false, gift["numericSuffix"]);
            Assert.Equal("MCVSOFFLINE", body["pageCode"]);
            Assert.Equal(true, body["enabled"]);
        }
    }
}
