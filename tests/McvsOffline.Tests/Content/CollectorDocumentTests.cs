using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm;
using McvsOffline.Server.Services.Crm.Collector;
using McvsOffline.Server.Services.Social.LiveOps;
using McvsOffline.Tests.Golden;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Content
{
    /// <summary>
    /// The collector in the served CRM documents (docs/design/economy-collector.md §2.8-§2.12, WP-D): the layer, the guard
    /// or nothing by phase, against WP-0's golden capture; the per-player documents; the mirror policy (D10); isolation
    /// (a failing plan or overlay serves the guard); and "a crate live ops hand out always has rows".
    /// </summary>
    public class CollectorDocumentTests
    {
        /// <summary>The shipped collector.since, which the golden run crosses (a Monday season start).</summary>
        private static readonly DateTime Since = GoldenScenarios.Since;

        /// <summary>A Tuesday of the first season with the collector active.</summary>
        private static readonly DateTime ActiveDay = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>A Wednesday before collector.since (Dormant).</summary>
        private static readonly DateTime DormantDay = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

        private const string Meta = "MCVS_Meta";

        private readonly ITestOutputHelper output;

        public CollectorDocumentTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        // ------------------------------------------------------------------------------------------ golden

        /// <summary>
        /// O7: with <c>economy.collector.enabled=false</c> every WP-0 output before <c>since</c> is byte-identical, and from
        /// <c>since</c> on each document is identical after removing exactly the 5 guard cells from each tree, which are
        /// there (live ops on and off; events always identical).
        /// </summary>
        [Fact]
        public void CollectorOffMatchesGolden()
        {
            List<GoldenOutput> outputs = GoldenScenarios.RunAll(run => run.Set("economy.collector.enabled", false));
            Assert.Equal(GoldenScenarios.Names(), outputs.Select(o => o.Name).ToList());
            Assert.Empty(GoldenScenarios.Mismatches(outputs.Where(o => o.Day < Since)));
            List<GoldenOutput> after = outputs.Where(o => o.Day >= Since).ToList();
            foreach (GoldenOutput o in after.Where(IsDocument)) AssertGuardInBothTrees(o);
            Assert.Empty(GoldenScenarios.Mismatches(after, o => IsDocument(o) ? Strip(o, IsGuardCell) : o.Text));
        }

        /// <summary>
        /// Dormant (§2.12): no cells at all. Live ops off: byte-identical before guardSince (the shipped since), the guard
        /// alone after it; live ops on: byte-identical before since.
        /// </summary>
        [Fact]
        public void DormantMatchesGolden()
        {
            DateTime guardSince = CollectorRuntime.GuardSince(Since);
            List<GoldenOutput> off = GoldenScenarios.Run(false);
            Assert.Empty(GoldenScenarios.Mismatches(off.Where(o => o.Day < guardSince)));
            List<GoldenOutput> guarded = off.Where(o => o.Day >= guardSince).ToList();
            foreach (GoldenOutput o in guarded.Where(IsDocument)) AssertGuardInBothTrees(o);
            Assert.Empty(GoldenScenarios.Mismatches(guarded, o => IsDocument(o) ? Strip(o, IsGuardCell) : o.Text));

            List<GoldenOutput> on = GoldenScenarios.Run(true);
            Assert.Empty(GoldenScenarios.Mismatches(on.Where(o => o.Day < Since)));
            foreach (GoldenOutput o in on.Where(o => o.Day < Since && IsDocument(o)))
                Assert.DoesNotContain("Crate_Event_Special", o.Text);
        }

        /// <summary>
        /// The collector active (live ops on, defaults, from since): every output is the golden one outside the collector's
        /// own field changers, so the store filter (the unlock-all player's offline_store), the events and every other cell
        /// are byte-identical; the per-player documents carry their own cells.
        /// </summary>
        [Fact]
        public void StoreFilterUnchangedWithCollector()
        {
            HashSet<string> changers = CollectorChangers();
            List<GoldenOutput> on = GoldenScenarios.Run(true);
            var byName = on.ToDictionary(o => o.Name, StringComparer.Ordinal);
            foreach (GoldenOutput o in on.Where(o => o.Day >= Since && IsDocument(o)))
            {
                Dictionary<string, object> game = GameOf(o);
                Assert.True(TypedRows(Tables(game, false), false) == 39, o.Name + ": the layer's rows");
                Assert.True(TypedRows(Tables(game, false), true) > 0, o.Name + ": a drawable row");
                if (o.Kind != GoldenScenarios.Player) continue;
                // The per-player mirror is the guard; the static one is the layer.
                Assert.Equal(CollectorPlanTests.Canonical(CollectorGuard.Cells), CollectorPlanTests.Canonical(CollectorCellsOf(Tables(game, true), changers)));
                string stat = byName[GoldenScenarios.NameOf(true, o.Day, GoldenScenarios.Static, null)].Text;
                Assert.NotEqual(stat, o.Text);
            }
            Assert.Empty(GoldenScenarios.Mismatches(on, o => IsDocument(o) && o.Day >= Since
                                                                ? Strip(o, (t, c, k) => changers.Contains(t + "\n" + c))
                                                                : o.Text));
        }

        // ------------------------------------------------------------------------------------------ never an empty crate

        public static IEnumerable<object[]> Combinations()
        {
            foreach (bool enabled in new[] { true, false })
                foreach (bool liveOps in new[] { true, false })
                    foreach (string content in new[] { "valid", "refused", "notJson" })
                        foreach (bool? patch in new bool?[] { true, false, null })
                            yield return new object[] { enabled, liveOps, content, patch };
        }

        /// <summary>
        /// D5/D9: for every combination of settings, content validity and store patch, and every day of the golden run,
        /// if live ops hand out Crate_Event_Special (in an event, or by <see cref="CollectorRuntime.CratesInWindow"/> for
        /// the day's or the season's window) then the served documents (static, per-player, minimal) have typed
        /// Crate_Event_Special rows in game._OverrideTables, and every document from guardSince on has the crate valid.
        /// </summary>
        [Theory]
        [MemberData(nameof(Combinations))]
        public void EveryServedCrateHasRows(bool enabled, bool liveOps, string content, bool? patch)
        {
            var minimal = CrmService.MinimalDocument(false, true);
            Assert.Empty(CrmDocumentValidator.CheckCollector(Tables(JsonValue.GetObject(minimal, "game"), false), null, "minimal"));
            int handedOut = 0;
            using (var run = new GoldenRun(GoldenScenarios.FirstDay + GoldenScenarios.TimeOfDay))
            {
                run.Set("economy.collector.enabled", enabled);
                if (!liveOps) run.Set("liveops.enabled", false);
                if (content == "refused") run.OverrideContent(CollectorContent.FileName, RefusedContent());
                if (content == "notJson") run.OverrideContent(CollectorContent.FileName, "{ not json");
                run.Context.Features.Set(new ClientCapabilities { StorePatch = patch });
                string token = run.NewPlayer("crates");
                run.SaveGameProfile(token, GoldenScenarios.DumpText(GoldenScenarios.Fresh));
                CollectorRuntime runtime = CollectorRuntime.For(run.Context);
                foreach (DateTime day in GoldenScenarios.Days())
                {
                    run.Clock.UtcNow = day + GoldenScenarios.TimeOfDay;
                    run.ReadGameProfile(token);
                    LiveOpsSettings s = LiveOpsService.For(run.Context).Settings;
                    LiveOpsWindow daily = LiveOpsSchedule.DayAt(s, run.Clock.UtcNow);
                    LiveOpsWindow season = LiveOpsSchedule.SeasonAt(s, run.Clock.UtcNow);
                    string dailyText = run.Get("/events/" + LiveOpsSchedule.DailyEventId(daily) + "?language=en", null).BodyText;
                    string weeklyText = run.Get("/events/" + LiveOpsSchedule.WeeklyEventId(season) + "?language=en", null).BodyText;
                    bool handsOut = dailyText.Contains("Crate_Event_Special") || weeklyText.Contains("Crate_Event_Special")
                                    || (liveOps && (runtime.CratesInWindow(daily.Start, Since) || runtime.CratesInWindow(season.Start, Since)));
                    var docs = new[]
                    {
                        ("static", GameMember(run.Get("/configs/users/me", null).BodyText)),
                        ("player", GameMember(run.Get("/configs/users/me", token).BodyText)),
                        ("minimal", JsonValue.GetObject(minimal, "game")),
                    };
                    foreach (var (name, game) in docs)
                    {
                        if (handsOut) Assert.True(TypedRows(Tables(game, false), true) > 0, day.ToString("yyyy-MM-dd") + " " + name + ": no typed row");
                        if (day >= CollectorRuntime.GuardSince(Since) || name == "minimal")
                        {
                            Assert.Empty(CrmDocumentValidator.CheckCollector(Tables(game, false), null, name));
                            Assert.Empty(CrmDocumentValidator.CheckCollector(Tables(game, true), null, name + " mirror"));
                        }
                    }
                    if (handsOut) handedOut++;
                }
            }
            // The defaults hand out crates from since on (not a vacuous pass).
            bool hands = enabled && liveOps && content == "valid" && patch != false;
            Assert.Equal(hands ? GoldenScenarios.Days().Count(d => d >= Since) : 0, handedOut);
        }

        // ------------------------------------------------------------------------------------------ isolation

        /// <summary>§2.8 isolation: a plan that throws, a plan with a bad value, an overlay that clears a weighted row's
        /// <c>Id@Type</c> or adds a sound row: the guard in both trees, the rest of the document kept, logged.</summary>
        [Fact]
        public void CollectorFailureServesGuard()
        {
            CollectorContent content = CollectorPlanTests.Shipped();
            LiveOpsSnapshot snapshot = CollectorPlanTests.Snapshot(ActiveDay);
            var logs = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(logs);
            Dictionary<string, object> without = CrmDocumentBuilder.Build(null, new CrmContent(), new CrmOptions(), Log.Null, snapshot, null, null);

            // A plan that throws.
            CrmCollector part = ActivePart(content);
            part.Plan = (c, o, s) => throw new InvalidOperationException("plan broke");
            Dictionary<string, object> doc = CrmDocumentBuilder.Build(null, new CrmContent(), new CrmOptions(), logger.For("crm"), snapshot, null, part);
            Assert.Equal(CollectorPhase.Guard, part.Phase);
            Assert.Contains("plan broke", part.Failure);
            Assert.True(logs.Contains("CRM: collector layer replaced by the guard: InvalidOperationException: plan broke"));
            AssertOnlyGuard(doc);
            Assert.Equal(MiniJson.Serialize(without), MiniJson.Serialize(StripTree(doc, IsGuardCell)));

            // A plan with a fraction: SetCell refuses it, and none of the layer's cells stay behind.
            part = ActivePart(content);
            part.Plan = (c, o, s) =>
            {
                CollectorLayer layer = CollectorPlan.Static(c, o, s);
                layer.Game.Add(new CollectorCell("LootCrates", "Id", "CratesCount", "Crate_Gold_4To10Place", 2.5));
                return layer;
            };
            doc = CrmDocumentBuilder.Build(null, new CrmContent(), new CrmOptions(), logger.For("crm"), snapshot, null, part);
            Assert.Equal(CollectorPhase.Guard, part.Phase);
            Assert.Contains("ArgumentException", part.Failure);
            AssertOnlyGuard(doc);

            // An overlay that clears the Id@Type of a weighted row, and one that adds a sound row with weight.
            foreach (string overlay in new[]
            {
                @"{""game"":{""_OverrideTables"":{""MCVS_Meta"":{""Crate_Event_Special"":{""Id@Type"":{""5"":null}}}}}}",
                @"{""game"":{""_OverrideTables"":{""MCVS_Meta"":{""Crate_Event_Special"":{""Id@Subtype"":{""3"":""Sound""},""Id@Subtype2"":{""3"":""2""}}}}}}",
                @"{""game"":{""_ServerConfig"":{""default"":{""_OverrideTables"":{""MCVS_Meta"":{""Crate_Event_Special"":{""Id@Weight#2"":{""60"":5}}}}}}}}",
            })
            {
                using var run = new GoldenRun(ActiveDay);
                run.Set("crm.overlay", MiniJson.ParseObject(overlay));
                string token = Player(run, "p", GoldenScenarios.DumpText(GoldenScenarios.Fresh));
                foreach (string served in new[] { run.Get("/configs/users/me", null).BodyText, run.Get("/configs/users/me", token).BodyText })
                    AssertOnlyGuard(MiniJson.ParseObject(served));
                Assert.True(run.Logs.Contains("CRM: collector layer replaced by the guard: "), overlay);
                var diag = MiniJson.ParseObject(run.Get("/_mcvs/crm", null).BodyText);
                Assert.Equal("Guard", JsonPath.Get(diag, "collector.phase"));
                Assert.Equal("Active", JsonPath.Get(diag, "collector.decided"));
                Assert.False(string.IsNullOrEmpty(JsonPath.Get(diag, "collector.failure") as string));
            }
        }

        // ------------------------------------------------------------------------------------------ mirror policy

        /// <summary>
        /// D10: the static mirror is the layer minus the Unlockable true cells (the Kayac false cells stay); the per-player
        /// mirror is the guard, with every non-collector mirror cell untouched.
        /// </summary>
        [Fact]
        public void MirrorPolicy()
        {
            using var run = new GoldenRun(ActiveDay);
            run.Set("economy", MiniJson.ParseObject(@"{""versusCoinsMultiplier"":2.0,""collector"":{""levelUpAvatars"":2}}"));
            string token = Player(run, "p", GoldenScenarios.DumpText(GoldenScenarios.Fresh));
            var stat = (Dictionary<string, object>)MiniJson.ParseObject(run.Get("/configs/users/me", null).BodyText)["game"];
            var player = (Dictionary<string, object>)MiniJson.ParseObject(run.Get("/configs/users/me", token).BodyText)["game"];
            CrmDocument doc = run.Context.Features.Get<CrmDocumentCache>().Get(0);
            Assert.Equal(CollectorPhase.Active, doc.CollectorPhase);
            CollectorLayer layer = doc.Collector.Layer;
            HashSet<string> changers = Changers(layer.Game);

            Assert.Equal(CollectorPlanTests.Canonical(layer.Game), CollectorPlanTests.Canonical(CollectorCellsOf(Tables(stat, false), changers)));
            Assert.Equal(CollectorPlanTests.Canonical(layer.Mirror), CollectorPlanTests.Canonical(CollectorCellsOf(Tables(stat, true), changers)));
            Assert.Equal(139 + 6, layer.Game.Count(CollectorPlan.IsUnlockableTrue));
            Assert.DoesNotContain(CollectorCellsOf(Tables(stat, true), changers), CollectorPlan.IsUnlockableTrue);
            Assert.Equal(75, CollectorCellsOf(Tables(stat, true), changers).Count(c => c.Table == CollectorPlan.AvatarsTable));
            Assert.Equal(49, CollectorCellsOf(Tables(stat, true), changers).Count(c => c.Table == CollectorPlan.LevelsTable));

            Assert.Equal(CollectorPlanTests.Canonical(CollectorGuard.Cells), CollectorPlanTests.Canonical(CollectorCellsOf(Tables(player, true), changers)));
            Func<string, string, string, bool> isCollector = (t, c, k) => changers.Contains(t + "\n" + c);
            string rest = MiniJson.Serialize(StripTables(Tables(stat, true), isCollector));
            Assert.Contains("VersusCoinsMatchRewards", rest);   // a non-collector mirror cell (the multiplier)
            Assert.Equal(rest, MiniJson.Serialize(StripTables(Tables(player, true), isCollector)));
            // The per-player game tree has the layer's changers (a fresh profile owns none of the Unlockable ids).
            Assert.Equal(changers.OrderBy(c => c, StringComparer.Ordinal),
                         Changers(CollectorCellsOf(Tables(player, false), changers)).OrderBy(c => c, StringComparer.Ordinal));
        }

        /// <summary>§2.8: the collector touches no table field that a MUL target uses (economy.json, the live-ops modifiers,
        /// the season rewards), so SET and MUL never meet in one cell.</summary>
        [Fact]
        public void CollectorDoesNotTouchMulTargets()
        {
            var content = new ContentProvider(null, null, Log.Null);
            var mul = new HashSet<string>(StringComparer.Ordinal);
            Assert.True(content.TryReadContentText(CrmContent.EconomyFile, out string economyText));
            foreach (var group in (Dictionary<string, object>)MiniJson.ParseObject(economyText)["multipliers"])
                foreach (Dictionary<string, object> target in (List<object>)group.Value)
                    foreach (string key in new[] { "fields", "capFields" })
                        foreach (object f in JsonValue.GetArray(target, key) ?? new List<object>())
                            mul.Add(target["database"] + "." + target["table"] + "." + target["index"] + "@" + ((string)f).Replace('.', '#'));
            Assert.True(content.TryReadContentText(LiveOpsCatalog.ContentName, out string liveOpsText));
            LiveOpsCatalog catalog = LiveOpsCatalog.Parse(liveOpsText, Log.Null);
            var cells = catalog.Modifiers.SelectMany(m => m.Cells).Concat(new[] { catalog.SeasonRewards }).Where(c => c != null).ToList();
            Assert.NotEmpty(cells);
            foreach (LiveOpsCells c in cells)
                foreach (string f in c.Fields) mul.Add(c.Database + "." + c.Table + "." + c.Index + "@" + f.Replace('.', '#'));
            output.WriteLine(mul.Count + " MUL changers");

            CollectorContent shipped = CollectorPlanTests.Shipped();
            var options = new CollectorOptions { LevelUpAvatars = 3 };
            var written = CollectorPlan.Static(shipped, options, CollectorPlanTests.Snapshot(ActiveDay)).Game.Concat(CollectorGuard.Cells);
            foreach (CollectorCell cell in written) Assert.DoesNotContain(Meta + "." + cell.Table + "." + cell.Changer, mul);
        }

        /// <summary>crm.overlay is applied after the collector: its cells win in the static document and in every
        /// per-player document (a patched key is replaced only where the collector's own value is).</summary>
        [Fact]
        public void OverlayWinsOverCollector()
        {
            using var run = new GoldenRun(ActiveDay);
            run.Set("crm.overlay", MiniJson.ParseObject(@"{""game"":{""_OverrideTables"":{""MCVS_Meta"":{
                ""LootCrates"":{""Id@CratesCount"":{""Crate_Event_Special"":7}},
                ""Crate_Event_Special"":{""Id@Weight#1"":{""2"":12345}},
                ""KillsignatureAvatars"":{""ID@Unlockable"":{""226"":""false""}}}}}}"));
            string token = Player(run, "p", GoldenScenarios.DumpText(GoldenScenarios.Fresh));
            var stat = MiniJson.ParseObject(run.Get("/configs/users/me", null).BodyText);
            var player = MiniJson.ParseObject(run.Get("/configs/users/me", token).BodyText);
            CrmDocument doc = run.Context.Features.Get<CrmDocumentCache>().Get(0);
            Assert.Equal(CollectorPhase.Active, doc.CollectorPhase);
            foreach (var root in new[] { stat, player })
            {
                var game = (Dictionary<string, object>)root["game"];
                Assert.Equal(7L, Cell(Tables(game, false), "LootCrates", "Id@CratesCount", "Crate_Event_Special"));
                Assert.Equal(12345L, Cell(Tables(game, false), "Crate_Event_Special", "Id@Weight#1", "2"));
                Assert.Equal("false", Cell(Tables(game, false), "KillsignatureAvatars", "ID@Unlockable", "226"));
                Assert.Empty(CrmDocumentValidator.CheckCollector(Tables(game, false), null, "game"));
            }
            Assert.Equal(7L, Cell(Tables((Dictionary<string, object>)stat["game"], true), "LootCrates", "Id@CratesCount", "Crate_Event_Special"));
            // The other rows are the player's own (a fresh profile has 20 agents locked: its weights differ from the static ones).
            object staticRow1 = Cell(Tables((Dictionary<string, object>)stat["game"], false), "Crate_Event_Special", "Id@Weight#1", "1");
            object playerRow1 = Cell(Tables((Dictionary<string, object>)player["game"], false), "Crate_Event_Special", "Id@Weight#1", "1");
            Assert.Equal(416290L, staticRow1);
            Assert.NotEqual(staticRow1, playerRow1);
        }

        // ------------------------------------------------------------------------------------------ per-player documents

        /// <summary>§2.9.5: the inventory is taken at a profile read; saves alone leave the document as it is (DoSaveProfile's
        /// refresh is recognised as equal), and one collector line is logged per rebuild.</summary>
        [Fact]
        public void PlayerDocumentChangesOnlyAfterProfileRead()
        {
            using var run = new GoldenRun(ActiveDay);
            string token = run.NewPlayer("p");
            string account = run.Context.Tokens.Resolve(token).AccountId;
            string stat = run.Get("/configs/users/me", null).BodyText;
            // No read yet: the static document.
            Assert.Equal(stat, run.Get("/configs/users/me", token).BodyText);

            run.SaveGameProfile(token, Dump(new[] { 1 }, new[] { 1 }));
            Assert.Equal(stat, run.Get("/configs/users/me", token).BodyText);
            run.ReadGameProfile(token);
            string first = run.Get("/configs/users/me", token).BodyText;
            Assert.NotEqual(stat, first);
            Assert.Equal(first, run.Get("/configs/users/me", token).BodyText);

            run.SaveGameProfile(token, Dump(new[] { 1, 225, 226 }, new[] { 1, 18 }));
            Assert.Equal(first, run.Get("/configs/users/me", token).BodyText);
            Assert.Equal(1, CollectorLines(run, account));

            run.ReadGameProfile(token);
            string second = run.Get("/configs/users/me", token).BodyText;
            Assert.NotEqual(first, second);
            Assert.Equal(second, run.Get("/configs/users/me", token).BodyText);
            Assert.Equal(2, CollectorLines(run, account));
            string line = run.Logs.Snapshot().Last(e => e.Message.StartsWith("CRM: collector for " + account + ": ")).Message;
            output.WriteLine(line);
            Assert.Contains(": base ", line);
            Assert.Contains(" Unlockable cells, parts known avatars|backgrounds|sounds|mercs", line);
        }

        /// <summary>§2.6: owned avatars and backgrounds get no Unlockable cell in the per-player document (true window and
        /// Kayac false cells alike); the static document has them all.</summary>
        [Fact]
        public void PlayerDocumentDropsOwnedUnlockableCells()
        {
            using var run = new GoldenRun(ActiveDay);
            string token = Player(run, "p", Dump(new[] { 1, 225, 226, 598 }, new[] { 1, 18 }));
            var stat = Tables((Dictionary<string, object>)MiniJson.ParseObject(run.Get("/configs/users/me", null).BodyText)["game"], false);
            var player = Tables((Dictionary<string, object>)MiniJson.ParseObject(run.Get("/configs/users/me", token).BodyText)["game"], false);
            foreach (string id in new[] { "225", "226", "227" }) Assert.Equal("true", Cell(stat, CollectorPlan.AvatarsTable, "ID@Unlockable", id));
            Assert.Equal("false", Cell(stat, CollectorPlan.AvatarsTable, "ID@Unlockable", "598"));
            Assert.Equal("true", Cell(stat, CollectorPlan.BackgroundsTable, "ID@Unlockable", "18"));

            foreach (string id in new[] { "225", "226", "598" }) Assert.Null(Cell(player, CollectorPlan.AvatarsTable, "ID@Unlockable", id));
            Assert.Equal("true", Cell(player, CollectorPlan.AvatarsTable, "ID@Unlockable", "227"));
            Assert.Equal("false", Cell(player, CollectorPlan.AvatarsTable, "ID@Unlockable", "599"));
            Assert.Null(Cell(player, CollectorPlan.BackgroundsTable, "ID@Unlockable", "18"));
            Assert.Equal("true", Cell(player, CollectorPlan.BackgroundsTable, "ID@Unlockable", "19"));
            var avatars = (Dictionary<string, object>)JsonPath.Get(player, Meta + "." + CollectorPlan.AvatarsTable + ".ID@Unlockable");
            Assert.Equal(137 + 74, avatars.Count);
            Assert.Equal(avatars.Keys.OrderBy(k => int.Parse(k)), avatars.Keys);   // ascending ids
            // The owned avatars also left the store.
            string text = run.Get("/configs/users/me", token).BodyText;
            Assert.DoesNotContain("\"KillSignature_Avatar_225\"", text);
            Assert.Contains("\"KillSignature_Avatar_227\"", text);
        }

        /// <summary>The agent list is parsed only while the collector is active (§2.9.1): before since a broken Mercs part
        /// is not even looked at, and becomes a problem once the collector is active.</summary>
        [Fact]
        public void MercsNotParsedWhenNotActive()
        {
            using var run = new GoldenRun(DormantDay);
            string dump = "{\"@version\":\"0.1\",\"$default\":{\"/Profile\":{\"@type\":\"PlayerProfileComponent\",\"KillSignatureAvatars\":[\"1\",\"2\"],"
                          + "\"KillSignatureBackgrounds\":[\"1\"],\"Mercs\":{\"oops\":true}}}}";
            string token = Player(run, "p", dump);
            string account = run.Context.Tokens.Resolve(token).AccountId;
            string text = run.Get("/configs/users/me", token).BodyText;
            Assert.DoesNotContain("\"KillSignature_Avatar_2\"", text);   // the store filter ran
            var diag = MiniJson.ParseObject(run.Get("/_mcvs/collector?account=" + account, null).BodyText);
            Assert.Equal("Dormant", diag["phase"]);
            Assert.Equal(false, JsonPath.Get(diag, "parts.mercs.known"));
            Assert.Equal("not read (the collector is not active)", JsonPath.Get(diag, "parts.mercs.problem"));
            Assert.Equal(true, JsonPath.Get(diag, "parts.avatars.known"));
            Assert.False(diag.ContainsKey("left"));

            run.Clock.UtcNow = ActiveDay;
            run.Get("/configs/users/me", token);
            diag = MiniJson.ParseObject(run.Get("/_mcvs/collector?account=" + account, null).BodyText);
            Assert.Equal("Active", diag["phase"]);
            Assert.Equal("Mercs is not an array", JsonPath.Get(diag, "parts.mercs.problem"));
            Assert.True(run.Logs.Contains("parts known avatars|backgrounds|sounds (mercs: Mercs is not an array)"));
        }

        /// <summary>§2.9: server Worlds fetch with the game: super credential, an account without a game profile: they get
        /// the static document (and so install the static mirror).</summary>
        [Fact]
        public void ServerSessionGetsStaticDocument()
        {
            using var run = new GoldenRun(ActiveDay);
            string player = Player(run, "p", GoldenScenarios.DumpText(GoldenScenarios.Fresh));
            HttpResponse authorize = run.Server.Handle(HttpRequest.Create("POST", "/authorize",
                "username=mcvsoffline&credential_type=game&password=mcvsoffline&device_id=d&client_id=" + Uri.EscapeDataString(McvsConstants.ClientId)
                + "&scope=" + Uri.EscapeDataString("auth config storage")));
            Assert.Equal(200, authorize.StatusCode);
            string server = (string)MiniJson.ParseObject(authorize.BodyText)["access_token"];
            string stat = run.Get("/configs/users/me", null).BodyText;
            Assert.Equal(stat, run.Get("/configs/users/me", server).BodyText);
            Assert.NotEqual(stat, run.Get("/configs/users/me", player).BodyText);
            Assert.Equal(run.Get("/configs/users/me/game", null).BodyText, run.Get("/configs/users/me/game", server).BodyText);
        }

        /// <summary>Selectors and explicit user paths stay static, whoever asks.</summary>
        [Fact]
        public void SelectorsStayStatic()
        {
            using var run = new GoldenRun(ActiveDay);
            string token = Player(run, "p", GoldenScenarios.DumpText(GoldenScenarios.UnlockAll));
            string account = run.Context.Tokens.Resolve(token).AccountId;
            Assert.Equal(run.Get("/configs/users/me/game", null).BodyText, run.Get("/configs/users/me/game", token).BodyText);
            Assert.Equal(run.Get("/configs/users/me", null).BodyText, run.Get("/configs/users/fed_id:" + account, token).BodyText);
            Assert.NotEqual(run.Get("/configs/users/me", null).BodyText, run.Get("/configs/users/me", token).BodyText);
        }

        /// <summary>
        /// An unreadable dump: every part unknown gives the static layer in the game tree (the mirror is still the guard)
        /// and the whole store; no dump at all gives the static document.
        /// </summary>
        [Fact]
        public void UnreadableDumpGetsTheStaticLayer()
        {
            using var run = new GoldenRun(ActiveDay);
            const string unreadable = "{\"a\":{\"KillSignatureAvatars\":[\"1\"],\"KillSignatureBackgrounds\":[\"1\"],\"KillSignatureSounds\":[\"1\"]},"
                                      + "\"b\":{\"KillSignatureAvatars\":[\"2\"],\"KillSignatureBackgrounds\":[\"2\"],\"KillSignatureSounds\":[\"2\"]}}";
            string token = Player(run, "p", unreadable);
            var stat = MiniJson.ParseObject(run.Get("/configs/users/me", null).BodyText);
            var player = MiniJson.ParseObject(run.Get("/configs/users/me", token).BodyText);
            Assert.Equal(MiniJson.Serialize(Tables((Dictionary<string, object>)stat["game"], false)),
                         MiniJson.Serialize(Tables((Dictionary<string, object>)player["game"], false)));
            Assert.Equal(MiniJson.Serialize(stat["offline_store"]), MiniJson.Serialize(player["offline_store"]));
            Assert.NotEqual(MiniJson.Serialize(Tables((Dictionary<string, object>)stat["game"], true)),
                            MiniJson.Serialize(Tables((Dictionary<string, object>)player["game"], true)));
            Assert.True(run.Logs.Contains("parts known none (avatars: KillSignatureAvatars appears more than once;"));
            Assert.True(run.Logs.Contains("not read (KillSignatureAvatars appears more than once); serving the whole store"));

            // A read of a player that never saved a dump: the static document.
            string other = run.NewPlayer("q");
            run.Get("/profiles/me/myprofile/_mcvs.gameProfile", other);
            Assert.Equal(run.Get("/configs/users/me", null).BodyText, run.Get("/configs/users/me", other).BodyText);
        }

        /// <summary>With crm.omitOwnedSignatures off and the collector active, the per-player document has the whole store
        /// and the collector cells (§5.6).</summary>
        [Fact]
        public void CollectorCellsWithoutTheStoreFilter()
        {
            using var run = new GoldenRun(ActiveDay);
            run.Set("crm.omitOwnedSignatures", false);
            string token = Player(run, "p", GoldenScenarios.DumpText(GoldenScenarios.UnlockAll));
            var stat = MiniJson.ParseObject(run.Get("/configs/users/me", null).BodyText);
            var player = MiniJson.ParseObject(run.Get("/configs/users/me", token).BodyText);
            Assert.Equal(MiniJson.Serialize(stat["offline_store"]), MiniJson.Serialize(player["offline_store"]));
            var tables = Tables((Dictionary<string, object>)player["game"], false);
            // Unlock-all owns every avatar and background: no Unlockable cell is left, and the weights are the plan's for
            // that inventory.
            Assert.Null(JsonPath.Get(tables, Meta + "." + CollectorPlan.AvatarsTable));
            Assert.Null(JsonPath.Get(tables, Meta + "." + CollectorPlan.BackgroundsTable));
            Assert.Equal(0L, Cell(tables, "Crate_Event_Special", "Id@Weight#1", "1"));
            CrmDocument doc = run.Context.Features.Get<CrmDocumentCache>().Get(0);
            IList<FieldPatch> expected = CollectorPlan.PlayerCells(doc.Collector.Content, doc.Collector.Options,
                                                                   PlayerInventory.Scan(GoldenScenarios.DumpText(GoldenScenarios.UnlockAll), true),
                                                                   doc.Collector.LiveOps);
            FieldPatch weights = expected.Single(p => p.Changer == "Id@Weight#1");
            Assert.Equal(MiniJson.Serialize(weights.Cells.ToDictionary(kv => kv.Key, kv => kv.Value)),
                         MiniJson.Serialize(JsonPath.Get(tables, Meta + ".Crate_Event_Special.Id@Weight#1")));
            output.WriteLine("unlock-all weights: " + MiniJson.Serialize(JsonPath.Get(tables, Meta + ".Crate_Event_Special.Id@Weight#1")));
            Assert.Empty(CrmDocumentValidator.CheckCollector(tables, null, "player"));
        }

        // ------------------------------------------------------------------------------------------ diagnostics

        /// <summary>GET _mcvs/crm has the collector block (phase, reason, cells, itemsPerCrate, static weights of §4.2);
        /// GET _mcvs/collector?account= what was read.</summary>
        [Fact]
        public void DiagnosticsShowTheCollector()
        {
            using var run = new GoldenRun(ActiveDay);
            string token = Player(run, "p", GoldenScenarios.DumpText(GoldenScenarios.Fresh));
            string account = run.Context.Tokens.Resolve(token).AccountId;
            run.Get("/configs/users/me", token);
            var crm = MiniJson.ParseObject(run.Get("/_mcvs/crm", null).BodyText);
            Assert.Equal("Active", JsonPath.Get(crm, "collector.phase"));
            Assert.StartsWith("active since 2026-11-09", (string)JsonPath.Get(crm, "collector.reason"));
            Assert.Equal(5L, JsonPath.Get(crm, "collector.itemsPerCrate"));
            Assert.Equal(510L, JsonPath.Get(crm, "collector.cells.game"));
            Assert.Equal(365L, JsonPath.Get(crm, "collector.cells.mirror"));
            Assert.Equal("guard", JsonPath.Get(crm, "collector.cells.playerMirror"));
            Assert.Equal(416290L, JsonPath.Get(crm, "collector.staticWeights.1"));
            Assert.Equal(117699L, JsonPath.Get(crm, "collector.staticWeights.37"));
            Assert.Equal(132301L, JsonPath.Get(crm, "collector.staticWeights.38"));
            Assert.Equal(0L, JsonPath.Get(crm, "collector.staticWeights.39"));

            var collector = MiniJson.ParseObject(run.Get("/_mcvs/collector?account=" + account, null).BodyText);
            Assert.Equal("Active", collector["phase"]);
            foreach (string part in new[] { "avatars", "backgrounds", "sounds", "mercs" }) Assert.Equal(true, JsonPath.Get(collector, "parts." + part + ".known"));
            var agents = (List<object>)collector["agents"];
            Assert.Equal(true, JsonPath.Get(agents.Single(a => (long)JsonPath.Get(a, "id") == 1), "unlocked"));
            Assert.StartsWith("base ", (string)collector["left"]);
            Assert.Equal(2L, JsonPath.Get(collector, "leftByCategory.agents"));   // Witch and Bli-P, locked in a new profile
            Assert.Equal(139L + 75 + 6, JsonPath.Get(collector, "unlockableCells"));
            Assert.Equal(39, ((Dictionary<string, object>)collector["weights"]).Count);
            Assert.Equal(400, run.Get("/_mcvs/collector", null).StatusCode);

            // Before since: dormant, and why.
            run.Clock.UtcNow = DormantDay;
            crm = MiniJson.ParseObject(run.Get("/_mcvs/crm", null).BodyText);
            Assert.Equal("Dormant", JsonPath.Get(crm, "collector.phase"));
            Assert.Contains("before collector.since", (string)JsonPath.Get(crm, "collector.reason"));
            Assert.Null(JsonPath.Get(crm, "collector.staticWeights"));
        }

        /// <summary>
        /// The phase is decided per request (§2.12): without live ops nothing makes the TS refetch at since, and the next
        /// fetch after since gets the guard; collector.json changing makes a new document.
        /// </summary>
        [Fact]
        public void DocumentFollowsThePhaseAndTheContent()
        {
            using var run = new GoldenRun(Since.AddSeconds(-10));
            run.Set("liveops.enabled", false);
            string before = run.Get("/configs/users/me", null).BodyText;
            Assert.DoesNotContain("Crate_Event_Special", before);
            run.Clock.UtcNow = Since.AddSeconds(-1);   // the decision runs ahead of the clock as live ops do
            Assert.Contains("Crate_Event_Special", run.Get("/configs/users/me", null).BodyText);

            run.Set("liveops.enabled", true);
            run.Clock.UtcNow = ActiveDay;
            CrmDocumentCache cache = run.Context.Features.Get<CrmDocumentCache>();
            CrmDocument shipped = cache.Get(0);
            Assert.Equal(CollectorPhase.Active, shipped.CollectorPhase);
            Assert.Contains("\"Crate.Cosmetic_Crate\"", shipped.Json);
            Assert.Same(shipped, cache.Get(0));
            var edited = MiniJson.ParseObject(CollectorPlanTests.ShippedJson);
            edited["note"] = "same rows, another text";
            run.OverrideContent(CollectorContent.FileName, MiniJson.Serialize(edited));
            CrmDocument first = cache.Get(0);
            Assert.NotSame(shipped, first);
            Assert.Equal(CollectorPhase.Active, first.CollectorPhase);
            run.OverrideContent(CollectorContent.FileName, RefusedContent());
            CrmDocument second = cache.Get(0);
            Assert.NotSame(first, second);
            Assert.Equal(CollectorPhase.Guard, second.CollectorPhase);
            Assert.Contains("collector.json", second.Collector.Status.Reason);
        }

        /// <summary>
        /// collector.since as the CRM reads it from liveops.json until the live-ops snapshot carries it (WP-E): the shipped
        /// block gives the compiled-in ShippedSince; a missing block or a since in another form gives none (the guard then
        /// starts at ShippedSince).
        /// </summary>
        [Fact]
        public void CollectorSinceComesFromLiveOpsJson()
        {
            Assert.True(new ContentProvider(null, null, Log.Null).TryReadContentText(LiveOpsCatalog.ContentName, out string shipped));
            DateTime? since = CrmDocumentCache.ParseCollectorSince(shipped);
            Assert.Equal(CollectorGuard.ShippedSince, since);
            Assert.Equal(DateTimeKind.Utc, since.Value.Kind);
            foreach (string text in new[] { null, "", "{", "{}", "{\"collector\":{}}", "{\"collector\":{\"since\":\"2026-11-09\"}}",
                                            "{\"collector\":{\"since\":\"2026-11-09T00:00:00+01:00\"}}", "{\"collector\":{\"since\":20261109}}" })
                Assert.Null(CrmDocumentCache.ParseCollectorSince(text));
            Assert.Equal(new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc),
                         CrmDocumentCache.ParseCollectorSince("{\"collector\":{\"since\":\"2026-11-16T00:00:00Z\"}}"));

            // An override that moves since a week later: the first week after the shipped since is the guard (live ops on,
            // before since, after guardSince), the next one the layer.
            using var run = new GoldenRun(ActiveDay);
            var liveOps = MiniJson.ParseObject(shipped);
            ((Dictionary<string, object>)liveOps["collector"])["since"] = "2026-11-16T00:00:00Z";
            run.OverrideContent(LiveOpsCatalog.ContentName, MiniJson.Serialize(liveOps));
            CrmDocumentCache cache = run.Context.Features.Get<CrmDocumentCache>();
            Assert.Equal(CollectorPhase.Guard, cache.Get(0).CollectorPhase);
            Assert.Contains("before collector.since 2026-11-16", cache.Get(0).Collector.Status.Reason);
            run.Clock.UtcNow = ActiveDay.AddDays(7);
            Assert.Equal(CollectorPhase.Active, cache.Get(0).CollectorPhase);
        }

        // ------------------------------------------------------------------------------------------ helpers

        private static CrmCollector ActivePart(CollectorContent content)
        {
            return new CrmCollector
            {
                Status = new CollectorStatus { Phase = CollectorPhase.Active, Reason = "test", Since = Since },
                Content = content,
                Options = new CollectorOptions(),
            };
        }

        /// <summary>A player whose dump was saved and read (as the TS does at login); returns the on-behalf token.</summary>
        private static string Player(GoldenRun run, string user, string dump)
        {
            string token = run.NewPlayer(user);
            run.SaveGameProfile(token, dump);
            run.ReadGameProfile(token);
            return token;
        }

        private static string Dump(int[] avatars, int[] backgrounds)
        {
            return "{\"@version\":\"0.1\",\"$default\":{\"/Profile\":{\"@type\":\"PlayerProfileComponent\",\"KillSignatureAvatars\":"
                   + MiniJson.Serialize(avatars.Select(a => (object)a.ToString()).ToList()) + ",\"KillSignatureBackgrounds\":"
                   + MiniJson.Serialize(backgrounds.Select(b => (object)b.ToString()).ToList()) + ",\"KillSignatureSounds\":[\"1\"],"
                   + "\"Mercs\":[],\"Squads\":[{\"Id\":\"0\",\"Mercs\":[\"1\"]}]}}}";
        }

        private static int CollectorLines(GoldenRun run, string account)
        {
            return run.Logs.Snapshot().Count(e => e.Message.StartsWith("CRM: collector for " + account + ": ", StringComparison.Ordinal));
        }

        /// <summary>The shipped collector.json with a sound row that has weight (§2.3.1): refused.</summary>
        private static string RefusedContent()
        {
            var doc = MiniJson.ParseObject(CollectorPlanTests.ShippedJson);
            var row = (Dictionary<string, object>)((List<object>)doc["rows"])[2];
            row["subtype"] = "Sound";
            row["subtype2"] = "2";
            row.Remove("bundle");
            return MiniJson.Serialize(doc);
        }

        private static bool IsDocument(GoldenOutput o)
        {
            return o.Kind == GoldenScenarios.Static || o.Kind == GoldenScenarios.Game || o.Kind == GoldenScenarios.Player;
        }

        /// <summary>The <c>game</c> member of a served document, parsed alone (the ~400 KB store is skipped).</summary>
        private static Dictionary<string, object> GameMember(string documentText)
        {
            Assert.True(McvsOffline.Server.Services.Storage.JsonSpanScanner.TryFindValue(documentText, new[] { "game" }, out int start, out int length));
            return MiniJson.ParseObject(documentText.Substring(start, length));
        }

        private static Dictionary<string, object> GameOf(GoldenOutput o)
        {
            var root = MiniJson.ParseObject(o.Text);
            return o.Kind == GoldenScenarios.Game ? root : (Dictionary<string, object>)root["game"];
        }

        /// <summary><c>game._OverrideTables</c> or (<paramref name="mirror"/>) <c>game._ServerConfig.default._OverrideTables</c>.</summary>
        private static Dictionary<string, object> Tables(Dictionary<string, object> game, bool mirror)
        {
            return (Dictionary<string, object>)JsonPath.Get(game, mirror ? "_ServerConfig.default._OverrideTables" : "_OverrideTables");
        }

        private static object Cell(Dictionary<string, object> tables, string table, string changer, string key)
        {
            var cells = JsonValue.AsObject(JsonValue.Get(JsonValue.AsObject(JsonValue.Get(JsonValue.AsObject(JsonValue.Get(tables, Meta)), table)), changer));
            return cells != null && cells.TryGetValue(key, out object value) ? value : null;
        }

        /// <summary>Rows of Crate_Event_Special with a non-empty Id@Type (and, when <paramref name="drawable"/>, Weight#1 &gt; 0).</summary>
        private static int TypedRows(Dictionary<string, object> tables, bool drawable)
        {
            var types = JsonValue.AsObject(JsonPath.Get(tables, Meta + ".Crate_Event_Special.Id@Type"));
            var weights = JsonValue.AsObject(JsonPath.Get(tables, Meta + ".Crate_Event_Special.Id@Weight#1"));
            if (types == null || weights == null) return 0;
            return types.Count(t => t.Value is string s && s.Length > 0
                                    && (!drawable || (weights.TryGetValue(t.Key, out object w) && (JsonValue.AsDouble(w) ?? 0) > 0)));
        }

        private static bool IsGuardCell(string table, string changer, string key)
        {
            return CollectorGuard.Cells.Any(c => c.Table == table && c.Changer == changer && c.Key == key);
        }

        /// <summary>Every field changer the collector writes with the default options ("table\nchanger").</summary>
        private static HashSet<string> CollectorChangers()
        {
            CollectorLayer layer = CollectorPlan.Static(CollectorPlanTests.Shipped(), new CollectorOptions(), CollectorPlanTests.Snapshot(ActiveDay));
            return Changers(layer.Game.Concat(CollectorGuard.Cells));
        }

        private static HashSet<string> Changers(IEnumerable<CollectorCell> cells)
        {
            return new HashSet<string>(cells.Select(c => c.Table + "\n" + c.Changer), StringComparer.Ordinal);
        }

        /// <summary>The cells of a tree in the given changers, as collector cells (values as parsed).</summary>
        private static List<CollectorCell> CollectorCellsOf(Dictionary<string, object> tables, HashSet<string> changers)
        {
            var cells = new List<CollectorCell>();
            var db = JsonValue.AsObject(JsonValue.Get(tables, Meta));
            if (db == null) return cells;
            foreach (var table in db)
                foreach (var changer in (Dictionary<string, object>)table.Value)
                {
                    if (!changers.Contains(table.Key + "\n" + changer.Key)) continue;
                    string[] parts = changer.Key.Split('@');
                    foreach (var cell in (Dictionary<string, object>)changer.Value)
                        cells.Add(new CollectorCell(table.Key, parts[0], parts[1].Replace('#', '.'), cell.Key, cell.Value));
                }
            return cells;
        }

        /// <summary>Both trees of the document must hold the 5 guard cells with their values.</summary>
        private static void AssertGuardInBothTrees(GoldenOutput o)
        {
            Dictionary<string, object> game = GameOf(o);
            foreach (bool mirror in new[] { false, true })
                foreach (CollectorCell cell in CollectorGuard.Cells)
                    Assert.True(JsonValue.DeepEquals(cell.Value, Cell(Tables(game, mirror), cell.Table, cell.Changer, cell.Key)),
                                o.Name + (mirror ? " mirror " : " ") + cell);
        }

        /// <summary>Both trees carry exactly the guard in the collector's tables (Crate_Event_Special entirely, the
        /// retargeted tier crates not at all).</summary>
        private static void AssertOnlyGuard(Dictionary<string, object> doc)
        {
            var game = (Dictionary<string, object>)doc["game"];
            HashSet<string> changers = CollectorChangers();
            foreach (bool mirror in new[] { false, true })
            {
                Dictionary<string, object> tables = Tables(game, mirror);
                Assert.Equal(CollectorPlanTests.Canonical(CollectorGuard.Cells), CollectorPlanTests.Canonical(CollectorCellsOf(tables, changers)));
                Assert.Equal(new[] { "Id@Subtype", "Id@Type", "Id@Weight#1" },
                             ((Dictionary<string, object>)JsonPath.Get(tables, Meta + ".Crate_Event_Special")).Keys.OrderBy(k => k, StringComparer.Ordinal));
                Assert.Empty(CrmDocumentValidator.CheckCollector(tables, CollectorGuard.Cells, mirror ? "mirror" : "game"));
            }
        }

        /// <summary>The output's text with the cells <paramref name="remove"/> picks taken out of both trees (emptied
        /// changers, tables and databases pruned), serialized again.</summary>
        private static string Strip(GoldenOutput o, Func<string, string, string, bool> remove)
        {
            var root = MiniJson.ParseObject(o.Text);
            if (o.Kind == GoldenScenarios.Game) StripGame(root, remove);
            else StripGame((Dictionary<string, object>)root["game"], remove);
            return MiniJson.Serialize(root);
        }

        private static Dictionary<string, object> StripTree(Dictionary<string, object> doc, Func<string, string, string, bool> remove)
        {
            var copy = JsonValue.DeepCloneObject(doc);
            StripGame((Dictionary<string, object>)copy["game"], remove);
            return copy;
        }

        private static void StripGame(Dictionary<string, object> game, Func<string, string, string, bool> remove)
        {
            StripTables(Tables(game, false), remove);
            StripTables(Tables(game, true), remove);
        }

        private static Dictionary<string, object> StripTables(Dictionary<string, object> tables, Func<string, string, string, bool> remove)
        {
            foreach (string db in tables.Keys.ToList())
            {
                var dbObj = (Dictionary<string, object>)tables[db];
                foreach (string table in dbObj.Keys.ToList())
                {
                    var tableObj = (Dictionary<string, object>)dbObj[table];
                    foreach (string changer in tableObj.Keys.ToList())
                    {
                        var cells = (Dictionary<string, object>)tableObj[changer];
                        foreach (string key in cells.Keys.ToList())
                            if (db == Meta && remove(table, changer, key)) JsonPath.RemoveKey(cells, key);
                        if (cells.Count == 0) JsonPath.RemoveKey(tableObj, changer);
                    }
                    if (tableObj.Count == 0) JsonPath.RemoveKey(dbObj, table);
                }
                if (dbObj.Count == 0) JsonPath.RemoveKey(tables, db);
            }
            return tables;
        }
    }
}
