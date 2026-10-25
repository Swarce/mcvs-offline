using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Content
{
    /// <summary>The CRM endpoints through the composed server (all modules registered, real server/Content).</summary>
    public class CrmServiceTests
    {
        private readonly ITestOutputHelper output;

        public CrmServiceTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void ConfigsUsersMeIsACompleteDocumentWithoutAPreconfiguration()
        {
            using var dir = new TempDir();
            var logs = new MemoryLogSink();
            using var server = ContentTestSupport.CreateServer(dir.Combine("data"), logs: logs);

            var response = ContentTestSupport.Get(server, "/configs/users/me?access_token=t");
            Assert.Equal(200, response.StatusCode);
            Assert.Null(response.Headers.Get("ETag")); // no ETag, no If-Match round trips
            var doc = ContentTestSupport.JsonObject(response);
            Assert.Empty(CrmDocumentValidator.Validate(doc));
            Assert.True(((List<object>)JsonPath.Get(doc, "offline_store.prices")).Count > 1000);
            Assert.Equal(false, JsonPath.Get(doc, "game._ServerConfig.default.IsDevelopment"));

            var diag = ContentTestSupport.JsonObject(ContentTestSupport.Get(server, "/_mcvs/crm"));
            Assert.Equal(false, diag["usesPreconfiguration"]);
            Assert.Empty((List<object>)diag["problems"]);
        }

        [Fact]
        public void SelectorsReturnTheSubTree()
        {
            using var dir = new TempDir();
            using var server = ContentTestSupport.CreateServer(dir.Combine("data"));

            // The GS parses this body directly as the game object and reads its top-level _ServerConfig.
            var game = ContentTestSupport.Get(server, "/configs/users/me/game?access_token=t");
            Assert.Equal(200, game.StatusCode);
            var gameObj = ContentTestSupport.JsonObject(game);
            Assert.Equal(1L, JsonPath.Get(gameObj, "_ServerConfig.default.Matchmaking.MinPlayers"));
            Assert.False(gameObj.ContainsKey("offline_store"));

            // The TS handshake reads a single boolean (EN/ServerComponent.cs:365).
            var crash = ContentTestSupport.Get(server, "/configs/users/me/game.CrashlyticsParams.Enable");
            Assert.Equal(200, crash.StatusCode);
            Assert.Equal("false", crash.BodyText);

            Assert.Equal("{\"prices\":[]}", ContentTestSupport.Get(server, "/configs/users/me/iap").BodyText);
            Assert.Equal(200, ContentTestSupport.Get(server, "/configs/users/me/game/_ServerConfig").StatusCode);
            Assert.Equal(200, ContentTestSupport.Get(server, "/configs/users/fed_id:abc/game").StatusCode);
            Assert.Equal(404, ContentTestSupport.Get(server, "/configs/users/me/game.NoSuchKey").StatusCode);
            Assert.Equal(404, ContentTestSupport.Get(server, "/configs/users/me/game..x").StatusCode);
        }

        [Fact]
        public void SettingsChangesRebuildTheDocument()
        {
            using var dir = new TempDir();
            using var server = ContentTestSupport.CreateServer(dir.Combine("data"));
            var before = ContentTestSupport.JsonObject(ContentTestSupport.Get(server, "/configs/users/me"));
            Assert.Equal(false, JsonPath.Get(before, "game._ServerConfig.default.IsDevelopment"));
            long basePrice = (long)JsonPath.Get(before, "offline_store.prices.0.billing_methods.0.price.0.price");

            server.Context.Settings.Override("devMenu", true);
            server.Context.Settings.Override("instantBots", false);
            server.Context.Settings.Override("economy", MiniJson.ParseObject(@"{""priceMultiplier"":2.0,""versusCoinsMultiplier"":2.0}"));
            var after = ContentTestSupport.JsonObject(ContentTestSupport.Get(server, "/configs/users/me"));
            Assert.Equal(true, JsonPath.Get(after, "game._ServerConfig.default.IsDevelopment"));
            Assert.Null(JsonPath.Get(after, "game._ServerConfig.default.Matchmaking"));
            Assert.Equal(basePrice * 2, JsonPath.Get(after, "offline_store.prices.0.billing_methods.0.price.0.price"));
            Assert.Equal("*2", JsonPath.Get(after, "game._ServerConfig.default._OverrideTables.MCVS_Meta.VersusCoinsMatchRewards.Division@VictoryAmount.1"));
        }

        [Fact]
        public void ContentOverridesInTheDataFolderWinAfterReload()
        {
            using var dir = new TempDir();
            using var server = ContentTestSupport.CreateServer(dir.Combine("data"));
            Assert.True(((List<object>)JsonPath.Get(ContentTestSupport.JsonObject(ContentTestSupport.Get(server, "/configs/users/me")), "offline_store.prices")).Count > 1000);

            Directory.CreateDirectory(dir.Combine("data", "content"));
            File.WriteAllText(dir.Combine("data", "content", "offline_store.json"),
                @"{""prices"":[{""item"":""UnlockMerc_2"",""quantity"":1,""billing_methods"":[{""price"":[{""currency"":""VersusCoins"",""price"":10}],""replaced_price"":[]}]}]}");
            Assert.Equal(200, ContentTestSupport.Post(server, "/_mcvs/crm/reload", "").StatusCode);
            var doc = ContentTestSupport.JsonObject(ContentTestSupport.Get(server, "/configs/users/me"));
            var prices = (List<object>)JsonPath.Get(doc, "offline_store.prices");
            Assert.Single(prices);
            Assert.Equal("UnlockMerc_2", JsonPath.Get(prices[0], "item"));
        }

        [Fact]
        public void PreconfigurationIsFoundNextToTheDataFolder()
        {
            // Embedded layout: <GlfRootPath>/McvsOffline (data) and <GlfRootPath>/Data (the game's pack), no explicit pack.
            using var dir = new TempDir();
            ContentTestSupport.CreateFakeDataPack(dir.Combine("Data"), ContentTestSupport.SamplePreconfiguration());
            using var server = ContentTestSupport.CreateServer(dir.Combine("McvsOffline"));
            var doc = ContentTestSupport.JsonObject(ContentTestSupport.Get(server, "/configs/users/me"));
            Assert.Equal("keep_me", JsonPath.Get(doc, "pointcut_actions.0.id"));
            Assert.Equal("7", JsonPath.Get(doc, "game._OverrideTables.MCVS_Meta.MercsList.Id@TrialDays.3"));
            Assert.Empty(CrmDocumentValidator.Validate(doc));
            Assert.Equal(true, ContentTestSupport.JsonObject(ContentTestSupport.Get(server, "/_mcvs/crm"))["usesPreconfiguration"]);
        }

        [Fact]
        public void APreconfigurationThatAppearsLaterIsPickedUp()
        {
            // Fresh install: the server starts before the game's installer has downloaded Data/.
            using var dir = new TempDir();
            using var server = ContentTestSupport.CreateServer(dir.Combine("McvsOffline"));
            var cache = server.Context.Features.Get<CrmDocumentCache>();
            Assert.NotNull(cache);
            var first = cache.Get(5000);
            Assert.False(first.HasPreconfiguration);
            Assert.False(cache.Preconfiguration.IsFinal);

            ContentTestSupport.CreateFakeDataPack(dir.Combine("Data"), ContentTestSupport.SamplePreconfiguration());
            cache.Preconfiguration.RetryIntervalMs = 0;
            // The retry runs in the background; the request that triggers it is answered with what is there.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            CrmDocument second = cache.Get(5000);
            while (!second.HasPreconfiguration && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(20);
                second = cache.Get(5000);
            }
            Assert.True(second.HasPreconfiguration);
            Assert.True(cache.Preconfiguration.IsFinal);
            Assert.Equal("keep_me", JsonPath.Get(second.Root, "pointcut_actions.0.id"));
        }

        [Fact]
        public void CorruptPreconfigurationIsIgnoredAndTheAnswerStaysValid()
        {
            using var dir = new TempDir();
            ContentTestSupport.CreateFakeDataPack(dir.Combine("pack"), "{ this is not json");
            var logs = new MemoryLogSink();
            using var server = ContentTestSupport.CreateServer(dir.Combine("data"), dir.Combine("pack"), logs);
            var response = ContentTestSupport.Get(server, "/configs/users/me");
            Assert.Equal(200, response.StatusCode);
            Assert.Empty(CrmDocumentValidator.Validate(ContentTestSupport.JsonObject(response)));
            Assert.True(logs.Contains("is not a JSON object"));
        }

        [Fact]
        public void MinimalDocumentIsValid()
        {
            var doc = CrmService.MinimalDocument(false, true);
            Assert.Empty(CrmDocumentValidator.Validate(doc));
            Assert.Equal(1L, JsonPath.Get(doc, "game._ServerConfig.default.Matchmaking.MinPlayers"));
        }

        /// <summary>
        /// The player's real data pack (GCBF/LZ4, crc32 names): the preconfiguration decodes, matches the decoded tree
        /// byte for byte (tools/datapack/gcbf.py output) and the overlaid document is valid. Skipped without the pack.
        /// </summary>
        [Fact]
        public void RealPreconfigurationFromTheDataPack()
        {
            string pack = ContentTestSupport.RealDataPack;
            if (pack == null)
            {
                output.WriteLine("no data pack (MCVS_DATAPACK): skipped");
                return;
            }
            Assert.True(File.Exists(Path.Combine(pack, "b", "de76cd6.f"))); // crc32("Config/crmpreconfiguration.json")
            var dataPack = new DataPack(pack);
            Assert.True(dataPack.TryRead(CrmPreconfiguration.LogicalPath, out byte[] decoded));
            string decodedDir = ContentTestSupport.DecodedDataPack;
            if (decodedDir != null && File.Exists(Path.Combine(decodedDir, "Config", "crmpreconfiguration.json")))
                Assert.Equal(File.ReadAllBytes(Path.Combine(decodedDir, "Config", "crmpreconfiguration.json")), decoded);

            using var dir = new TempDir();
            var logs = new MemoryLogSink();
            using var server = ContentTestSupport.CreateServer(dir.Combine("data"), pack, logs);
            var response = ContentTestSupport.Get(server, "/configs/users/me");
            Assert.Equal(200, response.StatusCode);
            var doc = ContentTestSupport.JsonObject(response);
            var problems = CrmDocumentValidator.Validate(doc);
            Assert.True(problems.Count == 0, string.Join("\n", problems));
            var diag = ContentTestSupport.JsonObject(ContentTestSupport.Get(server, "/_mcvs/crm"));
            Assert.Equal(true, diag["usesPreconfiguration"]);
            Assert.Equal(false, JsonPath.Get(doc, "game._ServerConfig.default.IsDevelopment"));
            Assert.Equal(true, JsonPath.Get(doc, "game.AgeGateParams.Ignore")); // the shipped file says false
            Assert.Empty((List<object>)JsonPath.Get(doc, "iap.prices"));
            // The ~175 KB of empty changers are gone, and no ad pointcut is left.
            Assert.True(response.Body.Length < 600 * 1024, "document is " + response.Body.Length + " bytes");
            foreach (object a in (List<object>)doc["pointcut_actions"])
                Assert.DoesNotContain("show_ad", JsonValue.GetString((Dictionary<string, object>)a, "type", ""));
            output.WriteLine("preconfiguration: " + diag["preconfiguration"] + "; document " + response.Body.Length + " bytes");
        }
    }
}
