using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Server.Services.Crm;
using McvsOffline.Tests.Admin;
using Xunit;

namespace McvsOffline.Tests.Content
{
    /// <summary>
    /// The "MCVS Offline" inbox item of the CRM document (design docs/design/settings-page.md §9.1, WP7): CrmNewsfeed,
    /// the newsfeeds layer of CrmDocumentBuilder, the cache key, the newsfeeds checks of CrmDocumentValidator, and the
    /// document players get, compared byte for byte with the one before the item existed.
    /// </summary>
    public class CrmNewsfeedTests
    {
        /// <summary>A Wednesday; its week started on Monday 2026-10-26 00:00 UTC.</summary>
        internal static readonly DateTime Wednesday = new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Utc);

        /// <summary>2026-10-26T00:00:00Z (a Monday) in epoch seconds.</summary>
        internal const long MondayEpoch = 1792972800;

        /// <summary>The marker of the server on the default port (IAdminPortal.PageMarkerUrl).</summary>
        internal const string Marker = "http://127.0.0.1:51780/_mcvs/ui/#news";

        /// <summary>Our item as the document carries it on <see cref="Wednesday"/> (key order of design §9.1).</summary>
        internal const string ItemJson =
            "{\"news_id\":\"mcvsoffline_FromMCVS\",\"title\":\"MCVS Offline\","
            + "\"body\":\"Settings, saves and logs of your offline game. Tap the button to open them.\","
            + "\"redirect\":\"http://127.0.0.1:51780/_mcvs/ui/#news\",\"asset\":\"\",\"youtube\":\"\",\"timestamp\":1792972800,"
            + "\"end_date\":\"2026-12-02T00:00:00Z\"}";

        private static List<object> Newsfeeds(Dictionary<string, object> doc) => (List<object>)doc["newsfeeds"];

        private static Dictionary<string, object> Doc(McvsServer server) =>
            ContentTestSupport.JsonObject(ContentTestSupport.Get(server, "/configs/users/me"));

        /// <summary>Every module but the admin modules (admin, admin-logs, admin-saves).</summary>
        private static bool WithoutAdmin(string module) => !module.StartsWith("admin", StringComparison.Ordinal);

        /// <summary>The composed server with a fake portal (and a fake host) instead of the admin module's.</summary>
        private static McvsServer ServerWithPortal(TempDir dir, FakeAdminPortal portal, IClock clock, MemoryLogSink logs = null)
        {
            var options = new ServerOptions
            {
                DataDir = dir.Combine("data"),
                Port = AdminTestClient.DefaultPort,
                ConsoleLog = false,
                FileLog = false,
                Clock = clock,
                RegisterServices = (r, c) =>
                {
                    ServiceRegistry.RegisterAll(r, c, WithoutAdmin);
                    c.Features.Set<IAdminPortal>(portal);
                    c.Features.Set<IAdminHost>(new FakeAdminHost());
                },
            };
            if (logs != null) options.ExtraSinks.Add(logs);
            return McvsServer.Create(options);
        }

        // ------------------------------------------------------------------------------------------------ the item

        [Fact]
        public void TheItemHasTheDocumentedKeysTypesAndValues()
        {
            List<object> list = CrmNewsfeed.Build(new CrmOptions(), new FakeAdminPortal(), Wednesday);
            var item = (Dictionary<string, object>)Assert.Single(list);
            Assert.Equal(new[] { "news_id", "title", "body", "redirect", "asset", "youtube", "timestamp", "end_date" }, item.Keys.ToArray());
            foreach (string key in new[] { "news_id", "title", "body", "redirect", "asset", "youtube", "end_date" }) Assert.IsType<string>(item[key]);
            Assert.IsType<long>(item["timestamp"]);
            Assert.Equal(ItemJson, MiniJson.Serialize(item));

            // The link is the fixed marker: no ticket, nothing that works as a credential (I2).
            Assert.Equal(Marker, item["redirect"]);
            Assert.DoesNotContain("t=", (string)item["redirect"]);
            // "From" + the sender: InboxMessage takes the '_' part containing "From" (GM/InboxMessage.cs:147-152).
            Assert.Equal("FromMCVS", CrmNewsfeed.NewsId.Split('_').Single(p => p.Contains("From")));
        }

        [Theory]
        [InlineData("2026-10-26T00:00:00", "2026-10-26")] // Monday 00:00 is its own week
        [InlineData("2026-10-28T09:30:00", "2026-10-26")]
        [InlineData("2026-11-01T23:59:59", "2026-10-26")] // Sunday, last second of the week
        [InlineData("2026-11-02T00:00:00", "2026-11-02")]
        [InlineData("2027-01-01T12:00:00", "2026-12-28")] // across the year
        [InlineData("2024-03-03T08:00:00", "2024-02-26")] // across a leap day
        public void TheTimestampIsMondayOfTheCurrentWeek(string now, string monday)
        {
            DateTime utc = DateTime.SpecifyKind(DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
            DateTime expected = DateTime.SpecifyKind(DateTime.Parse(monday, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
            Assert.Equal(expected, CrmNewsfeed.WeekStart(utc));
            Assert.Equal(DateTimeKind.Utc, CrmNewsfeed.WeekStart(utc).Kind);
            var item = (Dictionary<string, object>)Assert.Single(CrmNewsfeed.Build(new CrmOptions(), new FakeAdminPortal(), utc));
            Assert.Equal(TimeFormat.ToUnixSeconds(expected), item["timestamp"]);
            // Always younger than the client's 30-day cut-off (InboxComponent.UpdateNewsFeedItem).
            Assert.False(TimeFormat.FromUnixSeconds((long)item["timestamp"]).AddDays(30) < utc);
            // end_date: week start + 37 days, ISO 8601 UTC, at least 30 days after the timestamp (pc-port.md §4.3: the PC
            // inbox takes the item's EndDate as the message's expiry whenever the timestamp moved).
            Assert.Equal(expected.AddDays(37).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), item["end_date"]);
            DateTime end;
            Assert.True(TimeFormat.TryParse((string)item["end_date"], out end));
            Assert.True(end.ToUniversalTime() >= TimeFormat.FromUnixSeconds((long)item["timestamp"]).AddDays(30));
            Assert.True(end.ToUniversalTime() > utc.AddDays(30)); // never expires before the client's own 30 days would
        }

        [Fact]
        public void NoItemWhenTurnedOffDisabledOrWithoutAPortal()
        {
            Assert.Empty(CrmNewsfeed.Build(new CrmOptions { NewsfeedLink = false }, new FakeAdminPortal(), Wednesday));
            Assert.Empty(CrmNewsfeed.Build(new CrmOptions(), new FakeAdminPortal { Enabled = false }, Wednesday));
            Assert.Empty(CrmNewsfeed.Build(new CrmOptions(), null, Wednesday));
            Assert.Empty(CrmNewsfeed.Build(new CrmOptions(), new FakeAdminPortal { Marker = null }, Wednesday));
            Assert.Empty(CrmNewsfeed.Build(new CrmOptions(), new FakeAdminPortal { Throws = true }, Wednesday));
            Assert.NotNull(CrmNewsfeed.Build(null, null, Wednesday));
        }

        [Fact]
        public void TheOptionDefaultsToOnAndFollowsTheSetting()
        {
            Assert.True(new CrmOptions().NewsfeedLink);
            var settings = new Settings();
            Assert.True(CrmOptions.FromSettings(settings, Log.Null).NewsfeedLink);
            string on = CrmOptions.FromSettings(settings, Log.Null).Fingerprint;
            settings.Override("crm.newsfeedLink", false);
            CrmOptions off = CrmOptions.FromSettings(settings, Log.Null);
            Assert.False(off.NewsfeedLink);
            Assert.NotEqual(on, off.Fingerprint);
        }

        // ------------------------------------------------------------------------------------------------ the builder

        private static CrmOptions WithOverlay(string overlayJson) => new CrmOptions { Overlay = MiniJson.ParseObject(overlayJson) };

        private const string OverlayNews = @"{""newsfeeds"":[
            {""news_id"":""season_FromGameloft"",""title"":""A tester's message"",""body"":""Hello"",""redirect"":"""",""timestamp"":1792972800},
            {""news_id"":""mcvsoffline_FromMCVS"",""title"":""stale"",""redirect"":""http://example.invalid/""}]}";

        [Fact]
        public void OursIsAppendedLastAndReplacesAnOverlayEntryWithOurId()
        {
            List<object> ours = CrmNewsfeed.Build(new CrmOptions(), new FakeAdminPortal(), Wednesday);
            var doc = CrmDocumentBuilder.Build(null, new CrmContent(), WithOverlay(OverlayNews), Log.Null, null, ours);
            List<object> list = Newsfeeds(doc);
            Assert.Equal(2, list.Count);
            Assert.Equal("season_FromGameloft", JsonPath.Get(list[0], "news_id"));
            Assert.Equal("A tester's message", JsonPath.Get(list[0], "title"));
            Assert.Equal(ItemJson, MiniJson.Serialize(list[1]));
            Assert.Empty(CrmDocumentValidator.Validate(doc));
            // The builder copies: the caller's list is never aliased into the document.
            Assert.NotSame(ours[0], list[1]);
        }

        [Fact]
        public void AnEmptyListStillWritesTheKeyAndKeepsOverlayEntries()
        {
            var doc = CrmDocumentBuilder.Build(null, new CrmContent(), new CrmOptions(), Log.Null, null, new List<object>());
            Assert.Empty(Newsfeeds(doc));
            Assert.Equal("newsfeeds", doc.Keys.Last()); // appended: every other key keeps its place
            Assert.Empty(CrmDocumentValidator.Validate(doc));

            doc = CrmDocumentBuilder.Build(null, new CrmContent(), WithOverlay(OverlayNews), Log.Null, null, new List<object>());
            Assert.Equal(new object[] { "season_FromGameloft", "mcvsoffline_FromMCVS" }, Newsfeeds(doc).Select(e => JsonPath.Get(e, "news_id")).ToArray());
        }

        [Fact]
        public void WithoutNewsfeedsTheDocumentIsAsBefore()
        {
            var options = new CrmOptions();
            var before = CrmDocumentBuilder.Build(MiniJson.ParseObject(ContentTestSupport.SamplePreconfiguration()), new CrmContent(), options, Log.Null, null);
            var after = CrmDocumentBuilder.Build(MiniJson.ParseObject(ContentTestSupport.SamplePreconfiguration()), new CrmContent(), options, Log.Null, null, null);
            Assert.False(after.ContainsKey("newsfeeds"));
            Assert.Equal(MiniJson.Serialize(before), MiniJson.Serialize(after));
            Assert.False(CrmService.MinimalDocument(false, true).ContainsKey("newsfeeds"));
        }

        [Fact]
        public void AnOverlayNewsfeedsThatIsNotAnArrayIsReplaced()
        {
            var logs = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(logs);
            List<object> ours = CrmNewsfeed.Build(new CrmOptions(), new FakeAdminPortal(), Wednesday);
            var doc = CrmDocumentBuilder.Build(null, new CrmContent(), WithOverlay(@"{""newsfeeds"":{""news_id"":""x""}}"), logger.For("crm"), null, ours);
            Assert.Equal(ItemJson, MiniJson.Serialize(Assert.Single(Newsfeeds(doc))));
            Assert.True(logs.Contains("newsfeeds was not an array; replaced with []"));

            // Without our list (the legacy overloads), the sanitizer repairs it.
            doc = CrmDocumentBuilder.Build(null, new CrmContent(), WithOverlay(@"{""newsfeeds"":null}"), Log.Null, null);
            Assert.Empty(Newsfeeds(doc));
        }

        // ------------------------------------------------------------------------------------------------ the validator

        private static List<string> NewsfeedProblems(Dictionary<string, object> doc) =>
            CrmDocumentValidator.Validate(doc).Where(p => p.StartsWith("newsfeeds", StringComparison.Ordinal)).ToList();

        [Fact]
        public void SanitizeMakesTheEntriesWhatTheClientReads()
        {
            var doc = MiniJson.ParseObject(@"{""newsfeeds"":[
                ""text"", 5, null, [], {""title"":""no id""}, {""news_id"":""""}, {""news_id"":{""x"":1}},
                {""news_id"":42,""title"":true,""body"":{""x"":1},""redirect"":null,""asset"":1.5,""youtube"":[],""timestamp"":""1792972800""},
                {""news_id"":""b"",""timestamp"":1792972800.7},
                {""news_id"":""c"",""timestamp"":""next monday""},
                {""news_id"":""d"",""timestamp"":false},
                {""news_id"":""e"",""title"":""  kept as it is  "",""timestamp"":1792972800,""end_date"":""2026-11-30T00:00:00Z""}]}");
            Assert.Equal(11, NewsfeedProblems(doc).Count); // every entry but "e"
            var logs = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(logs);

            Assert.Equal(7, CrmDocumentValidator.Sanitize(doc, logger.For("crm")));
            Assert.Empty(NewsfeedProblems(doc));
            string expected = "[" +
                "{\"news_id\":\"42\",\"title\":\"True\",\"body\":\"\",\"redirect\":\"\",\"asset\":\"1.5\",\"youtube\":\"\",\"timestamp\":1792972800}," +
                "{\"news_id\":\"b\",\"timestamp\":1792972800}," +
                "{\"news_id\":\"c\",\"timestamp\":0}," +
                "{\"news_id\":\"d\",\"timestamp\":0}," +
                "{\"news_id\":\"e\",\"title\":\"  kept as it is  \",\"timestamp\":1792972800,\"end_date\":\"2026-11-30T00:00:00Z\"}]";
            Assert.Equal(expected, MiniJson.Serialize(doc["newsfeeds"]));
            // Logged like the pointcut checks: one line per removal and per changed member.
            Assert.Equal(4, logs.Snapshot().Count(e => e.Message.Contains("CRM: newsfeed '?' removed: not an object")));
            Assert.Equal(3, logs.Snapshot().Count(e => e.Message.Contains("removed: needs a news_id")));
            Assert.True(logs.Contains("CRM: newsfeed '42': title was not a string; now \"True\""));
            Assert.True(logs.Contains("CRM: newsfeed 'c': timestamp was not integer epoch seconds; now 0"));
            Assert.All(logs.Snapshot(), e => Assert.Equal(LogLevel.Warn, e.Level));

            // A clean document is left alone.
            Assert.Equal(0, CrmDocumentValidator.Sanitize(doc, Log.Null));
        }

        /// <summary>
        /// PC: items sharing an asset inherit the first one's EndDate (pc-port.md §4.3); the validator warns when that first
        /// item has none, and changes nothing.
        /// </summary>
        [Fact]
        public void SanitizeWarnsWhenItemsShareAnAssetWithoutAnEndDate()
        {
            var doc = MiniJson.ParseObject(@"{""newsfeeds"":[
                {""news_id"":""a"",""asset"":""x"",""timestamp"":1792972800},
                {""news_id"":""b"",""asset"":""x"",""timestamp"":1792972800},
                {""news_id"":""c"",""asset"":""y"",""timestamp"":1792972800,""end_date"":""2026-12-02T00:00:00Z""},
                {""news_id"":""d"",""asset"":""y"",""timestamp"":1792972800},
                {""news_id"":""e"",""asset"":""z"",""timestamp"":1792972800}]}");
            string before = MiniJson.Serialize(doc);
            var logs = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(logs);
            Assert.Equal(0, CrmDocumentValidator.Sanitize(doc, logger.For("crm")));
            Assert.Equal(before, MiniJson.Serialize(doc));
            LogEntry warning = Assert.Single(logs.Snapshot());
            Assert.Equal(LogLevel.Warn, warning.Level);
            Assert.Contains("newsfeed 'b' shares asset \"x\" with 'a', which has no end_date", warning.Message);
        }

        /// <summary>
        /// Items without an asset (missing, or empty like our own item) share no key in the PC client's newsfeed table: an
        /// overlay item with neither asset nor end_date before ours, and more assetless items after it, warn nothing.
        /// </summary>
        [Fact]
        public void SanitizeIgnoresItemsWithoutAnAssetWhenCheckingSharedAssets()
        {
            var doc = MiniJson.ParseObject(@"{""newsfeeds"":[
                {""news_id"":""overlay1"",""asset"":"""",""timestamp"":1792972800},
                " + ItemJson + @",
                {""news_id"":""overlay2"",""asset"":"""",""timestamp"":1792972800},
                {""news_id"":""overlay3"",""timestamp"":1792972800},
                {""news_id"":""overlay4"",""timestamp"":1792972800}]}");
            string before = MiniJson.Serialize(doc);
            var logs = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(logs);
            Assert.Equal(0, CrmDocumentValidator.Sanitize(doc, logger.For("crm")));
            Assert.Equal(before, MiniJson.Serialize(doc));
            Assert.Empty(logs.Snapshot());
        }

        [Theory]
        [InlineData("null")]
        [InlineData("\"x\"")]
        [InlineData("{}")]
        [InlineData("7")]
        public void SanitizeReplacesANewsfeedsThatIsNotAnArray(string value)
        {
            var doc = MiniJson.ParseObject("{\"a\":1,\"newsfeeds\":" + value + ",\"z\":2}");
            Assert.Contains(CrmDocumentValidator.Validate(doc), p => p.StartsWith("newsfeeds must be an array", StringComparison.Ordinal));
            Assert.Equal(1, CrmDocumentValidator.Sanitize(doc, Log.Null));
            Assert.Equal("{\"a\":1,\"newsfeeds\":[],\"z\":2}", MiniJson.Serialize(doc));
        }

        [Fact]
        public void ValidateNamesTheProblemsOfEachEntry()
        {
            var doc = MiniJson.ParseObject(@"{""newsfeeds"":[{""news_id"":""a"",""timestamp"":1.5},{""news_id"":""b"",""body"":7},{""title"":""t""},3]}");
            var problems = CrmDocumentValidator.Validate(doc).Where(p => p.StartsWith("newsfeeds", StringComparison.Ordinal)).ToList();
            Assert.Equal(new[]
            {
                "newsfeeds[0]: timestamp must be integer epoch seconds", "newsfeeds[1]: body must be a string",
                "newsfeeds[2]: needs a news_id", "newsfeeds[3]: not an object",
            }, problems);
        }

        // ------------------------------------------------------------------------------------------------ the served document

        [Fact]
        public void TheDocumentCarriesTheItemWithTheAdminPortalsMarker()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"), clock: new ManualClock(Wednesday));
            var client = new AdminTestClient(server);
            HttpResponse response = ContentTestSupport.Get(server, "/configs/users/me?access_token=t");
            var doc = ContentTestSupport.JsonObject(response);
            Assert.Empty(CrmDocumentValidator.Validate(doc));
            object item = Assert.Single(Newsfeeds(doc));
            Assert.Equal(ItemJson, MiniJson.Serialize(item));
            Assert.Equal(client.Portal.PageMarkerUrl, JsonPath.Get(item, "redirect"));
            Assert.True(client.Portal.IsPageMarker((string)JsonPath.Get(item, "redirect"))); // what 'adminlink' checks
            // No ticket anywhere in what any caller can read without a token (I2).
            Assert.DoesNotContain("#t=", response.BodyText);
            Assert.DoesNotContain("/_mcvs/ui/#t", response.BodyText);
            // The GS/TS selector is unaffected.
            Assert.DoesNotContain("newsfeeds", ContentTestSupport.Get(server, "/configs/users/me/game").BodyText);
        }

        [Fact]
        public void TurnedOffDisabledOrNoPortalGiveAnEmptyList()
        {
            using var dir = new TempDir();
            using (var server = AdminTestClient.CreateServer(dir.Combine("a"), clock: new ManualClock(Wednesday)))
            {
                server.Context.Settings.Override("crm.newsfeedLink", false);
                Assert.Empty(Newsfeeds(Doc(server)));
                server.Context.Settings.Override("crm.newsfeedLink", true);
                server.Context.Settings.Override("admin.enabled", false);
                Assert.Empty(Newsfeeds(Doc(server)));
            }

            // No admin module at all: only the CRM module is registered.
            var options = new ServerOptions
            {
                DataDir = dir.Combine("b"), Port = AdminTestClient.DefaultPort, ConsoleLog = false, FileLog = false,
                Clock = new ManualClock(Wednesday), RegisterServices = CrmService.Register,
            };
            using (var server = McvsServer.Create(options))
            {
                Assert.Null(server.Context.Features.Get<IAdminPortal>());
                var doc = Doc(server);
                Assert.Empty(Newsfeeds(doc));
                Assert.Empty(CrmDocumentValidator.Validate(doc));
            }

            using (var server = ServerWithPortal(dir, new FakeAdminPortal { Enabled = false }, new ManualClock(Wednesday)))
                Assert.Empty(Newsfeeds(Doc(server)));
        }

        /// <summary>The "CRM document built" lines so far, waiting up to 15 s for the first.</summary>
        private static List<string> WaitForBuilds(MemoryLogSink logs)
        {
            List<string> Builds() => logs.Snapshot().Where(e => e.Message != null && e.Message.StartsWith("CRM document built", StringComparison.Ordinal))
                                         .Select(e => e.Message).ToList();
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (Builds().Count == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
            return Builds();
        }

        /// <summary>
        /// The warm-up's document serves the first request (the TS waits only 1 s for configs/users/me/game): without a
        /// data pack the warm-up is ready before the admin module registers its portal (it registers after the CRM
        /// module), so it waits for the portal instead of building a document without the item that the first request
        /// would rebuild on its own thread.
        /// </summary>
        [Fact]
        public void TheWarmupDocumentHasTheItemAndServesTheFirstRequest()
        {
            using var dir = new TempDir();
            var logs = new MemoryLogSink();
            // No data pack, the default crm.usePreconfiguration: the preconfiguration load is quick.
            using var server = AdminTestClient.CreateServer(dir.Combine("data"), logs, clock: new ManualClock(Wednesday));
            string warmup = Assert.Single(WaitForBuilds(logs));
            Assert.Contains("inbox item on", warmup);

            Assert.Equal(200, ContentTestSupport.Get(server, "/configs/users/me/game").StatusCode);
            Assert.Single(Newsfeeds(Doc(server)));
            Assert.Single(WaitForBuilds(logs)); // both from the warm-up's document
        }

        /// <summary>Without the admin module the warm-up stops waiting and builds the document without the item.</summary>
        [Fact]
        public void WithoutTheAdminModuleTheWarmupStillBuilds()
        {
            using var dir = new TempDir();
            var logs = new MemoryLogSink();
            var options = new ServerOptions
            {
                DataDir = dir.Combine("data"), Port = AdminTestClient.DefaultPort, ConsoleLog = false, FileLog = false,
                Clock = new ManualClock(Wednesday), RegisterServices = (r, c) => ServiceRegistry.RegisterAll(r, c, WithoutAdmin),
            };
            options.ExtraSinks.Add(logs);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var server = McvsServer.Create(options);
            string warmup = Assert.Single(WaitForBuilds(logs));
            Assert.Contains("inbox item off", warmup);
            Assert.True(watch.ElapsedMilliseconds >= CrmDocumentCache.PortalWaitMs - 100, watch.ElapsedMilliseconds + " ms");
            Assert.Empty(Newsfeeds(Doc(server)));
            Assert.Single(WaitForBuilds(logs));
        }

        [Fact]
        public void TheCachedDocumentIsRebuiltWhenTheItemChanges()
        {
            using var dir = new TempDir();
            var clock = new ManualClock(Wednesday);
            var portal = new FakeAdminPortal();
            using var server = ServerWithPortal(dir, portal, clock);
            var cache = server.Context.Features.Get<CrmDocumentCache>();

            CrmDocument first = cache.Get(5000);
            Assert.Same(first, cache.Get(0)); // nothing changed: the cached document
            Assert.Equal(ItemJson, MiniJson.Serialize(Assert.Single(Newsfeeds(first.Root))));

            // Only the portal changes (the admin module registers after the CRM warm-up; admin.enabled): no settings edit.
            portal.Enabled = false;
            CrmDocument off = cache.Get(0);
            Assert.NotSame(first, off);
            Assert.Empty(Newsfeeds(off.Root));
            portal.Enabled = true;
            Assert.Single(Newsfeeds(cache.Get(0).Root));

            // The port changed (the marker follows it).
            portal.Marker = "http://127.0.0.1:51999/_mcvs/ui/#news";
            Assert.Equal(portal.Marker, JsonPath.Get(Assert.Single(Newsfeeds(cache.Get(0).Root)), "redirect"));

            // The setting, as the page writes it (Set) and as a tester overrides it.
            server.Context.Settings.Set("crm.newsfeedLink", false);
            Assert.Empty(Newsfeeds(Doc(server)));
            server.Context.Settings.Set("crm.newsfeedLink", true);
            Assert.Single(Newsfeeds(Doc(server)));
        }

        [Fact]
        public void ANewWeekGetsANewTimestamp()
        {
            using var dir = new TempDir();
            var clock = new ManualClock(new DateTime(2026, 11, 1, 23, 59, 0, DateTimeKind.Utc)); // Sunday
            using var server = ServerWithPortal(dir, new FakeAdminPortal(), clock);
            Assert.Equal(MondayEpoch, JsonPath.Get(Assert.Single(Newsfeeds(Doc(server))), "timestamp"));
            clock.Advance(TimeSpan.FromSeconds(59));
            Assert.Equal(MondayEpoch, JsonPath.Get(Assert.Single(Newsfeeds(Doc(server))), "timestamp"));
            clock.Advance(TimeSpan.FromSeconds(1)); // Monday 2026-11-02 00:00:00
            Assert.Equal(MondayEpoch + 7 * 86400, JsonPath.Get(Assert.Single(Newsfeeds(Doc(server))), "timestamp"));
            clock.Advance(TimeSpan.FromDays(6.5));
            Assert.Equal(MondayEpoch + 7 * 86400, JsonPath.Get(Assert.Single(Newsfeeds(Doc(server))), "timestamp"));
        }

        [Fact]
        public void ThePagesSwitchRemovesAndRestoresTheItem()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"), clock: new ManualClock(Wednesday));
            var client = new AdminTestClient(server);
            client.OpenSession();
            Assert.Single(Newsfeeds(Doc(server)));
            foreach (bool on in new[] { false, true })
            {
                string fingerprint = (string)AdminTestClient.Json(client.Get("settings"))["fingerprint"];
                var body = new Dictionary<string, object>
                {
                    ["changes"] = new Dictionary<string, object> { ["crm.newsfeedLink"] = on },
                    ["fingerprint"] = fingerprint,
                };
                HttpResponse saved = client.Post("settings", body);
                Assert.True(saved.StatusCode == 200, saved.BodyText);
                Assert.Equal(on ? 1 : 0, Newsfeeds(Doc(server)).Count);
            }
        }

        [Fact]
        public void AnOverlayEntryIsKeptAndOneWithOurIdReplaced()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"), clock: new ManualClock(Wednesday));
            server.Context.Settings.Override("crm.overlay", MiniJson.ParseObject(OverlayNews));
            var doc = Doc(server);
            List<object> list = Newsfeeds(doc);
            Assert.Equal(2, list.Count);
            Assert.Equal("season_FromGameloft", JsonPath.Get(list[0], "news_id"));
            Assert.Equal(ItemJson, MiniJson.Serialize(list[1]));
            Assert.Empty(CrmDocumentValidator.Validate(doc));

            // A broken overlay value cannot break the document for the client.
            server.Context.Settings.Override("crm.overlay", MiniJson.ParseObject(@"{""newsfeeds"":{""oops"":true}}"));
            Assert.Equal(ItemJson, MiniJson.Serialize(Assert.Single(Newsfeeds(Doc(server)))));
        }

        [Fact]
        public void TheDiagnosticsStillWork()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"), clock: new ManualClock(Wednesday));
            HttpResponse diag = ContentTestSupport.Get(server, "/_mcvs/crm");
            Assert.Equal(200, diag.StatusCode);
            var body = ContentTestSupport.JsonObject(diag);
            Assert.Empty((List<object>)body["problems"]);
            Assert.Equal(false, body["usesPreconfiguration"]);
            Assert.Equal((long)ContentTestSupport.Get(server, "/configs/users/me").BodyText.Length, body["chars"]);

            var reload = HttpRequest.Create("POST", "/_mcvs/crm/reload", "");
            reload.Headers.Add("X-MCVS", "1");
            Assert.Equal(200, server.Handle(reload).StatusCode);
            Assert.Single(Newsfeeds(Doc(server)));
        }

        // ------------------------------------------------------------------------------------------------ before vs after

        /// <summary>
        /// The CRM document of the commit before WP7 (bf0efd2c352027bd08972511bc33faadf67e5ab7), produced by this exact setup
        /// (<see cref="BaselineServer"/>: every module plus admin, port 51780, clock <see cref="Wednesday"/>, a fresh data
        /// folder, no data pack or the sample preconfiguration, no token) on that commit: SHA-256 of the UTF-8 body and its
        /// length. If generated content (server/Content) or another CRM layer changes on purpose, rerun this test: the
        /// failure message gives the new values for the document without the item.
        /// </summary>
        private static readonly Dictionary<string, KeyValuePair<string, int>> Baseline = new Dictionary<string, KeyValuePair<string, int>>
        {
            ["nopre /configs/users/me"] = Pair("ad66b2a1ee6515f6e29b8bc109772857854d19727dc64de16d9490c850f8386f", 403140),
            ["nopre /configs/users/me/game"] = Pair("e858e7b62289f535bf3781e245c2763ebbdd2162ae479743581f3c5b1d57e18b", 2426),
            ["pre /configs/users/me"] = Pair("d1a7a22b9e2656d8e861f9ab4545a6ee89cad47e7b0957f4ecd64de43e4eaa3e", 403331),
            ["pre /configs/users/me/game"] = Pair("47fd2d8359ba4dde9ced85d360c5ee30e2397b2fed474c1e7c5b2c21a717b2b8", 2548),
        };

        private static KeyValuePair<string, int> Pair(string sha256, int length) => new KeyValuePair<string, int>(sha256, length);

        private static McvsServer BaselineServer(TempDir dir, bool preconfiguration)
        {
            string pack = preconfiguration ? ContentTestSupport.CreateFakeDataPack(dir.Combine("pack"), ContentTestSupport.SamplePreconfiguration()) : null;
            return AdminTestClient.CreateServer(dir.Combine("data"), clock: new ManualClock(Wednesday), configure: o => o.DataPackDir = pack);
        }

        private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        private static void AssertBaseline(string name, string text)
        {
            KeyValuePair<string, int> expected = Baseline[name];
            string actual = Sha256(text);
            Assert.True(expected.Key == actual && expected.Value == text.Length,
                        name + " differs from the document before WP7: now sha256 " + actual + ", " + text.Length + " chars");
        }

        /// <summary>
        /// What players get is the document of before plus the documented item, appended as the last member, and with
        /// crm.newsfeedLink off the document of before plus <c>"newsfeeds": []</c> (the empty list that removes the
        /// message from an inbox, design §9.1); every other byte is unchanged. The selector the GS/TS read is unchanged.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PlayersGetTheDocumentOfBeforePlusTheItem(bool preconfiguration)
        {
            string variant = preconfiguration ? "pre" : "nopre";
            using var dir = new TempDir();
            using var server = BaselineServer(dir, preconfiguration);

            server.Context.Settings.Override("crm.newsfeedLink", false);
            string off = ContentTestSupport.Get(server, "/configs/users/me").BodyText;
            const string emptyTail = ",\"newsfeeds\":[]}";
            Assert.EndsWith(emptyTail, off);
            AssertBaseline(variant + " /configs/users/me", off.Substring(0, off.Length - emptyTail.Length) + "}");
            AssertBaseline(variant + " /configs/users/me/game", ContentTestSupport.Get(server, "/configs/users/me/game").BodyText);

            server.Context.Settings.Override("crm.newsfeedLink", true);
            string on = ContentTestSupport.Get(server, "/configs/users/me").BodyText;
            string itemTail = ",\"newsfeeds\":[" + ItemJson + "]}";
            Assert.EndsWith(itemTail, on);
            AssertBaseline(variant + " /configs/users/me", on.Substring(0, on.Length - itemTail.Length) + "}");
            AssertBaseline(variant + " /configs/users/me/game", ContentTestSupport.Get(server, "/configs/users/me/game").BodyText);
        }
    }

    /// <summary>
    /// An <see cref="IAdminPortal"/> with settable state, recording <see cref="OpenPage"/> calls (shared by the WP7 tests).
    /// </summary>
    internal sealed class FakeAdminPortal : IAdminPortal
    {
        private readonly object gate = new object();
        private readonly List<string> opened = new List<string>();

        public FakeAdminPortal()
        {
            Enabled = true;
            Marker = CrmNewsfeedTests.Marker;
            OpenPageResult = true;
        }

        public bool Enabled { get; set; }

        public string Marker { get; set; }

        /// <summary>Every member throws (a broken portal must cost nothing but the item).</summary>
        public bool Throws { get; set; }

        public bool OpenPageResult { get; set; }

        /// <summary>"trigger|screen" of every OpenPage call.</summary>
        public List<string> Opened
        {
            get { lock (gate) return new List<string>(opened); }
        }

        bool IAdminPortal.Enabled
        {
            get
            {
                if (Throws) throw new InvalidOperationException("fake portal");
                return Enabled;
            }
        }

        public string PageMarkerUrl
        {
            get
            {
                if (Throws) throw new InvalidOperationException("fake portal");
                return Marker;
            }
        }

        public bool IsPageMarker(string url) => Enabled && url != null && url == Marker;

        public string MintPageUrl(string trigger, string screen) => Enabled ? "http://127.0.0.1:51780/_mcvs/ui/#t=" + new string('a', 32) : null;

        public bool OpenPage(string trigger, string screen)
        {
            lock (gate) opened.Add(trigger + "|" + screen);
            return OpenPageResult;
        }
    }
}
