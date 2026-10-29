using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Server.Services.Social;
using McvsOffline.Server.Services.Social.LiveOps;
using Xunit;

namespace McvsOffline.Tests.Misc
{
    /// <summary>
    /// The Special Events (docs/LIVEOPS.md §9) on the server: the window grid and ids, the rotation, the pinned mode,
    /// the settings, the chain that keeps running once the TS has seen it, the catalogue's <c>arena</c> block, the
    /// validator's Arena rules, the CRM document (with the instant bot fill Classic uses) and the diagnostics page.
    /// The real client runs the same events in LiveOpsArenaConformanceTests.
    /// </summary>
    public class LiveOpsArenaTests
    {
        // Wednesday 2026-10-21 09:30 UTC.
        private static readonly DateTime Wednesday = new DateTime(2026, 10, 21, 9, 30, 0, DateTimeKind.Utc);

        private static LiveOpsSettings Settings(int hours = 24, int resetHour = 0)
        {
            var s = new Settings();
            s.Override("liveops.arenaHours", (long)hours);
            s.Override("liveops.dailyResetHour", (long)resetHour);
            return LiveOpsSettings.From(s, null);
        }

        private static LiveOpsCatalog Shipped()
        {
            return LiveOpsCatalog.Parse(LiveOpsContent.Shipped, null);
        }

        private static List<string> Participation(Dictionary<string, object> doc)
        {
            var list = JsonPath.Get(doc, "groupware.events.participation") as List<object> ?? new List<object>();
            return list.Select(x => (string)((Dictionary<string, object>)x)["id"]).ToList();
        }

        private static List<string> ArenaIds(Dictionary<string, object> doc)
        {
            return Participation(doc).Where(id => id.StartsWith(LiveOpsSchedule.ArenaPrefix, StringComparison.Ordinal)).ToList();
        }

        private static Dictionary<string, object> Tuning(Dictionary<string, object> e)
        {
            return (Dictionary<string, object>)MiniJson.ParseObject((string)e["_template"])["event_tuning"];
        }

        private static object Raw(Dictionary<string, object> e, string key)
        {
            return JsonValue.Get(JsonValue.GetObject(Tuning(e), key), "value");
        }

        // ------------------------------------------------------------------------------------------ schedule

        [Fact]
        public void WindowsFollowTheDailyResetGridAndIdsCarryWindowAndMode()
        {
            ArenaWindow day = LiveOpsSchedule.ArenaAt(Settings(), Wednesday);
            Assert.Equal(new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc), day.Start);
            Assert.Equal(new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc), day.End);
            Assert.Equal("2026102100x24h", day.Key);
            Assert.Equal(day.End, day.Next.Start);

            ArenaWindow late = LiveOpsSchedule.ArenaAt(Settings(24, 6), new DateTime(2026, 10, 21, 5, 59, 0, DateTimeKind.Utc));
            Assert.Equal(new DateTime(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc), late.Start);   // the quest day's reset
            ArenaWindow six = LiveOpsSchedule.ArenaAt(Settings(6, 6), Wednesday);
            Assert.Equal(new DateTime(2026, 10, 21, 6, 0, 0, DateTimeKind.Utc), six.Start);
            Assert.Equal(6, six.Hours);
            ArenaWindow week = LiveOpsSchedule.ArenaAt(Settings(168), Wednesday);
            Assert.Equal(DayOfWeek.Monday, week.Start.DayOfWeek);   // 2024-01-01 is a Monday
            Assert.Equal(week.Start, LiveOpsSchedule.ArenaAt(Settings(168), week.Start).Start);   // a boundary belongs to the new window
            Assert.Equal(week.Start.AddDays(-7), LiveOpsSchedule.ArenaAt(Settings(168), week.Start.AddTicks(-1)).Start);
            ArenaWindow odd = LiveOpsSchedule.ArenaAt(Settings(7), Wednesday);   // 7 h does not divide a day: still one grid
            Assert.True(odd.Contains(Wednesday));
            Assert.Equal(0, (odd.Start - new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks % (7 * LiveOpsSchedule.TicksPerHour));

            string id = LiveOpsSchedule.ArenaEventId(day, "blitzTDM");
            Assert.Equal("mcvs_arena_2026102100x24h_blitzTDM", id);
            Assert.True(LiveOpsSchedule.TryParseArena(id, out ArenaWindow parsed, out string mode));
            Assert.Equal(day.Start, parsed.Start);
            Assert.Equal(24, parsed.Hours);
            Assert.Equal("blitzTDM", mode);
            foreach (string bad in new[]
                     {
                         "mcvs_arena_2026102100x24h", "mcvs_arena_2026102100x24h_", "mcvs_arena_2026102100x0h_tdm", "mcvs_arena_2026102100x169h_tdm",
                         "mcvs_arena_2026102100x024h_tdm", "mcvs_arena_2026102100x24_tdm", "mcvs_arena_2026103200x24h_tdm", "mcvs_arena_2026102100x24h_t_dm",
                         "mcvs_arena_2026102100x24h_tdm!", "mcvs_daily_2026102100", null,
                     })
                Assert.False(LiveOpsSchedule.TryParseArena(bad, out ArenaWindow _, out string _), bad);
        }

        [Fact]
        public void TheHoursSettingIsClampedAndInTheFingerprint()
        {
            Assert.Equal(1, Settings(0).ArenaHours);
            Assert.Equal(168, Settings(500).ArenaHours);
            Assert.Equal(24, LiveOpsSettings.Defaults().ArenaHours);
            Assert.True(LiveOpsSettings.Defaults().ArenaEvents);
            Assert.Equal("rotate", LiveOpsSettings.Defaults().ArenaRotation);
            Assert.Equal("Random", LiveOpsSettings.Defaults().ArenaMap);
            Assert.NotEqual(Settings(24).Fingerprint, Settings(12).Fingerprint);
            var s = new Settings();
            s.Override("liveops.arenaRotation", "  havoc ");
            Assert.Equal("havoc", LiveOpsSettings.From(s, null).ArenaRotation);
            Assert.NotEqual(LiveOpsSettings.Defaults().Fingerprint, LiveOpsSettings.From(s, null).Fingerprint);
        }

        // ------------------------------------------------------------------------------------------ rotation

        [Fact]
        public void TheRotationShowsEveryModeOncePerCycleAndNeverTwiceInARow()
        {
            LiveOpsArena arena = Shipped().Arena;
            List<LiveOpsArenaMode> pool = arena.RotationPool();
            Assert.Equal(22, pool.Count);
            Assert.DoesNotContain(pool, m => m.Id == "tripleElixir");   // no text of its own: only when pinned
            int n = pool.Count;
            var start = new ArenaWindow(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 24);
            var all = new List<string>();
            var w = start;
            for (int i = 0; i < n * 60; i++, w = w.Next) all.Add(arena.Rotating(w).Id);
            for (int c = 0; c < 60; c++)
                Assert.Equal(pool.Select(m => m.Id).OrderBy(x => x), all.Skip(c * n).Take(n).OrderBy(x => x));   // a shuffle of the pool
            for (int i = 1; i < all.Count; i++) Assert.NotEqual(all[i - 1], all[i]);
            Assert.NotEqual(all.Take(n), all.Skip(n).Take(n));   // each cycle shuffled anew
            // Deterministic: the same window gives the same mode, whatever was asked before; before 2024 too.
            var early = new ArenaWindow(new DateTime(2023, 12, 1, 0, 0, 0, DateTimeKind.Utc), 24);
            Assert.Equal(arena.Rotating(early).Id, Shipped().Arena.Rotating(early).Id);
            Assert.NotEqual(arena.Rotating(early.Next).Id, arena.Rotating(early).Id);
        }

        [Fact]
        public void SmallPoolsStillNeverRepeat()
        {
            foreach (int size in new[] { 1, 2, 3 })
            {
                var arena = new LiveOpsArena();
                for (int i = 0; i < size; i++) arena.Modes.Add(new LiveOpsArenaMode { Id = "m" + i });
                var w = new ArenaWindow(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 24);
                string previous = null;
                for (int i = 0; i < 200; i++, w = w.Next)
                {
                    string id = arena.Rotating(w).Id;
                    if (size > 1) Assert.NotEqual(previous, id);
                    previous = id;
                }
            }
        }

        // ------------------------------------------------------------------------------------------ CRM and events

        [Fact]
        public void TheCrmListsThisWindowAndTheNextAndTheEventsAreServed()
        {
            var clock = new ManualClock(Wednesday);
            using var s = new MiscServer(clock);
            TestPlayer p = s.NewPlayer("arena");
            var doc = MiscServer.Obj(s.Get("/configs/users/me?access_token=t"));
            List<string> ids = ArenaIds(doc);
            Assert.Equal(2, ids.Count);
            Assert.StartsWith("mcvs_arena_2026102100x24h_", ids[0]);
            Assert.StartsWith("mcvs_arena_2026102200x24h_", ids[1]);
            Assert.Equal("2026-10-22 00:00:00Z", doc["expiry"]);
            // The same document fills the room with bots at once (CloseRoom's public branch, no arena check): instantBots.
            Assert.Equal(1L, JsonPath.Get(doc, "game._ServerConfig.default.Matchmaking.MinPlayers"));
            Assert.Equal(0L, JsonPath.Get(doc, "game._ServerConfig.default.Matchmaking.MaxOpenTimeInSeconds"));

            var events = MiniJson.ParseArray(s.Get("/events?event_ids=" + string.Join(",", ids) + "&language=en", p.OnBehalfToken).BodyText)
                                 .Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(ids, events.Select(e => (string)e["id"]));
            Assert.Null(TleTemplateValidator.ArenaChainProblem(events, Wednesday));
            foreach (Dictionary<string, object> e in events)
            {
                Assert.Null(TleTemplateValidator.Problem(e));
                Assert.Equal(LiveOpsEvents.ArenaType, Raw(e, "event_type"));
                Assert.Equal(Enumerable.Range(1, 21).Select(i => "Merc_" + i), ((List<object>)Raw(e, "AgentRestrictions")).Cast<string>());
                Assert.Equal("Random", Raw(e, "MapName"));
                Assert.StartsWith("Menus/assets/png/tournament/mode_", (string)Raw(e, "img"));
                Assert.Equal(3L, e["_priority"]);
                Assert.True((double)Raw(e, "CreditsVictoryAmountMultiplier") >= 1.25);
            }
            Assert.Equal("2026-10-21 00:00:00Z", events[0]["start_date"]);
            Assert.Equal(events[0]["end_date"], events[1]["start_date"]);

            // Every hour for two weeks: a current and a next window, the expiry no later than the window's end.
            for (int h = 0; h < 14 * 24; h++)
            {
                clock.Advance(TimeSpan.FromHours(1));
                LiveOpsSnapshot snap = LiveOpsService.For(s.Context).Snapshot(clock.UtcNow);
                Assert.True(snap.ArenaWindow.Contains(clock.UtcNow.AddSeconds(2)));
                Assert.True(snap.Expiry.Value <= snap.ArenaWindow.End && snap.Expiry.Value > clock.UtcNow);
                Assert.True(LiveOpsSchedule.TryParseArena(snap.NextArenaEventId, out ArenaWindow next, out string _));
                Assert.Equal(snap.ArenaWindow.End, next.Start);
            }
        }

        [Fact]
        public void APinnedModeAndMapStayFixed()
        {
            var clock = new ManualClock(Wednesday);
            using var s = new MiscServer(clock);
            s.Set("liveops.arenaRotation", "tdm");
            s.Set("liveops.arenaMap", "Stadium2");
            s.Set("liveops.arenaHours", 12L);
            LiveOpsService live = LiveOpsService.For(s.Context);
            for (int i = 0; i < 28; i++)
            {
                LiveOpsSnapshot snap = live.Snapshot(clock.UtcNow);
                Assert.EndsWith("x12h_tdm", snap.ArenaEventId);
                Assert.EndsWith("x12h_tdm", snap.NextArenaEventId);
                Dictionary<string, object> e = live.FindEvent(snap.ArenaEventId, "en", clock.UtcNow);
                Assert.Equal("TeamDeathMatch", Raw(e, "MatchType"));
                Assert.Equal("Stadium2", Raw(e, "MapName"));
                Assert.Equal("Arena.TDM", Raw(e, "EventName"));
                Assert.Empty((List<object>)Raw(e, "MatchParameterSets"));
                clock.Advance(TimeSpan.FromHours(12));
            }
            // tripleElixir is outside the rotation but can be pinned.
            s.Set("liveops.arenaRotation", "tripleElixir");
            Assert.EndsWith("_tripleElixir", live.Snapshot(clock.UtcNow).ArenaEventId);
            // An unknown mode or map: the rotation and each mode's own map, with a warning (once).
            s.Set("liveops.arenaRotation", "nosuchmode");
            s.Set("liveops.arenaMap", "Barrage");
            LiveOpsSnapshot rotating = live.Snapshot(clock.UtcNow);
            Assert.Equal(Shipped().Arena.Rotating(rotating.ArenaWindow).Id, rotating.ArenaEventId.Substring(rotating.ArenaEventId.LastIndexOf('_') + 1));
            Assert.Equal("Random", Raw(live.FindEvent(rotating.ArenaEventId, "en", clock.UtcNow), "MapName"));
            live.Snapshot(clock.UtcNow);
            Assert.Single(s.Logs.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.Contains("unknown Special Event mode 'nosuchmode'"));
            Assert.Single(s.Logs.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.Contains("'Barrage' is not a playable map"));
        }

        [Fact]
        public void SwitchedOffBeforeTheTsHasOneThereIsNone()
        {
            using var s = new MiscServer(new ManualClock(Wednesday));
            TestPlayer p = s.NewPlayer("off");
            LiveOpsService live = LiveOpsService.For(s.Context);
            string id = live.Snapshot(Wednesday).ArenaEventId;
            foreach (var setting in new[] { new KeyValuePair<string, object>("liveops.arenaEvents", false),
                                            new KeyValuePair<string, object>("liveops.arenaRotation", "off"),
                                            new KeyValuePair<string, object>("liveops.enabled", false) })
            {
                s.Set("liveops.arenaEvents", true);
                s.Set("liveops.arenaRotation", "rotate");
                s.Set("liveops.enabled", true);
                s.Set(setting.Key, setting.Value);
                Assert.Empty(ArenaIds(MiscServer.Obj(s.Get("/configs/users/me?access_token=t"))));
                Assert.Null(live.Snapshot(Wednesday).ArenaEventId);
                Assert.Equal("[]", s.Get("/events?event_ids=" + id + "&language=en", p.OnBehalfToken).BodyText);
            }
        }

        /// <summary>
        /// Once the TS has a Special Event, the client keeps it until the game restarts: switching them off (any of the
        /// three ways) keeps the chain going, pinned to the mode of the window that was in progress, current and next
        /// window, and the events stay served. A restarted server starts clean.
        /// </summary>
        [Fact]
        public void OnceTheTsHasOneTheChainRunsUntilTheGameRestarts()
        {
            var dir = new TempDir();
            try
            {
                var clock = new ManualClock(Wednesday);
                string served;
                using (var s = new MiscServer(clock, dir))
                {
                    TestPlayer p = s.NewPlayer("kept");
                    LiveOpsService live = LiveOpsService.For(s.Context);
                    LiveOpsSnapshot on = live.Snapshot(clock.UtcNow);
                    served = on.ArenaEventId.Substring(on.ArenaEventId.LastIndexOf('_') + 1);
                    string body = s.Get("/events?event_ids=" + on.ArenaEventId + "," + on.NextArenaEventId + "&language=en", p.OnBehalfToken).BodyText;
                    Assert.Equal(2, MiniJson.ParseArray(body).Count);

                    foreach (var setting in new[] { new KeyValuePair<string, object>("liveops.arenaEvents", false),
                                                    new KeyValuePair<string, object>("liveops.arenaRotation", "off"),
                                                    new KeyValuePair<string, object>("liveops.enabled", false) })
                    {
                        s.Set("liveops.arenaEvents", true);
                        s.Set("liveops.arenaRotation", "rotate");
                        s.Set("liveops.enabled", true);
                        s.Set(setting.Key, setting.Value);
                        for (int h = 0; h < 72; h += 5)
                        {
                            DateTime now = clock.UtcNow.AddHours(h);
                            LiveOpsSnapshot kept = live.Snapshot(now);
                            Assert.True(kept.ArenaEventId != null, setting.Key + " +" + h + "h: the chain stopped");
                            Assert.EndsWith("_" + served, kept.ArenaEventId);
                            Assert.EndsWith("_" + served, kept.NextArenaEventId);
                            Assert.True(kept.Expiry.HasValue && kept.Expiry.Value <= kept.ArenaWindow.End);
                            Assert.NotNull(live.FindEvent(kept.ArenaEventId, "en", now));
                        }
                        var doc = MiscServer.Obj(s.Get("/configs/users/me?access_token=t"));
                        Assert.Equal(2, ArenaIds(doc).Count);
                        Assert.Equal(true, JsonPath.Get(MiscServer.Obj(s.Get("/_mcvs/liveops")), "arena.kept"));
                    }
                    Assert.Contains(s.Logs.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.Contains("keep running (" + served + ") until the game restarts"));
                }
                using (var restarted = new MiscServer(clock, dir))
                {
                    restarted.Set("liveops.arenaEvents", false);
                    Assert.Null(LiveOpsService.For(restarted.Context).Snapshot(clock.UtcNow).ArenaEventId);
                }
            }
            finally
            {
                dir.Dispose();
            }
        }

        [Fact]
        public void TheDiagnosticsPageShowsTheChain()
        {
            using var s = new MiscServer(new ManualClock(Wednesday));
            var page = MiscServer.Obj(s.Get("/_mcvs/liveops"));
            Assert.Equal(true, JsonPath.Get(page, "arena.listed"));
            Assert.Equal(false, JsonPath.Get(page, "arena.kept"));
            Assert.Equal("2026-10-21T00:00:00Z", JsonPath.Get(page, "arena.current.start"));
            Assert.Equal("2026-10-22T00:00:00Z", JsonPath.Get(page, "arena.next.start"));
            Assert.Equal((long)21, JsonPath.Get(page, "arena.current.agents"));
            Assert.Equal(7, ((List<object>)JsonPath.Get(page, "arena.upcoming")).Count);
            Assert.Equal(23, ((List<object>)JsonPath.Get(page, "arena.modes")).Count);
            Assert.Contains("tripleElixir*", (List<object>)JsonPath.Get(page, "arena.modes"));
            Assert.Null(JsonPath.Get(page, "arena.problem"));
            Assert.Equal(24L, JsonPath.Get(page, "settings.arenaHours"));
            Assert.Contains(JsonPath.Get(page, "arena.current.id"), (List<object>)page["eventIds"]);
        }

        // ------------------------------------------------------------------------------------------ catalogue

        [Fact]
        public void TheShippedCatalogueHasTheModesTheMapsAndTheSettingsPageChoices()
        {
            LiveOpsCatalog c = Shipped();
            Assert.Null(c.ArenaProblem);
            Assert.Equal(23, c.Arena.Modes.Count);
            // content-gen derives the playable maps from MapsList and the level files: the validator's list is the same.
            Assert.Equal(TleTemplateValidator.ArenaMaps, c.Arena.Maps);
            Assert.Equal(new[] { "rotate", "off" }.Concat(c.Arena.Modes.Select(m => m.Id)), SettingsSchema.ArenaRotationChoices);
            Assert.Equal(new[] { "Random" }.Concat(c.Arena.Maps), SettingsSchema.Find("liveops.arenaMap").Options);
            Assert.Equal(SettingsSchema.ArenaRotationChoices, SettingsSchema.Find("liveops.arenaRotation").Options);
            foreach (LiveOpsArenaMode m in c.Arena.Modes)
            {
                Assert.Equal(21, m.Agents.Count);
                Assert.Null(TleTemplateValidator.Problem(LiveOpsEvents.ArenaMode(c, c.Arena, new ArenaWindow(Wednesday, 24), m, "Random")));
            }
            LiveOpsArenaMode competitive = c.Arena.Mode("competitiveRegular");
            Assert.Equal("None", competitive.MatchType);
            Assert.False(competitive.AllowDuplicates);
            Assert.Equal(1.5, competitive.CoinsVictory);
            Assert.Equal(1.0, competitive.CoinsDefeat);
            Assert.Equal(1.25, c.Arena.Mode("havoc").CreditsVictory);
        }

        [Fact]
        public void ABrokenArenaBlockCostsTheModesNotTheRestOfLiveOps()
        {
            Dictionary<string, object> root = MiniJson.ParseObject(LiveOpsContent.Shipped);
            var modes = (List<object>)JsonPath.Get(root, "arena.modes");
            ((Dictionary<string, object>)modes[0]).Remove("img");
            ((Dictionary<string, object>)modes[1])["agents"] = new List<object> { "Any" };
            ((Dictionary<string, object>)modes[2])["agents"] = new List<object>();
            ((Dictionary<string, object>)modes[3])["map"] = "Barrage";
            ((Dictionary<string, object>)modes[4])["matchType"] = "CaptureFlag";
            ((Dictionary<string, object>)modes[5])["id"] = "with_underscore";
            ((Dictionary<string, object>)modes[6])["agents"] = new List<object> { "MercClass_Assault" };
            LiveOpsCatalog c = LiveOpsCatalog.Parse(MiniJson.Serialize(root), null);
            Assert.Equal(23 - 7, c.Arena.Modes.Count);
            Assert.StartsWith("modes left out: havoc: no img", c.ArenaProblem);
            Assert.Null(c.Arena.Mode("override"));

            root["arena"] = "nonsense";
            c = LiveOpsCatalog.Parse(MiniJson.Serialize(root), null);
            Assert.Null(c.Arena);
            Assert.False(c.IsEmpty);
            Assert.NotNull(c.Collector);
            root.Remove("arena");
            Assert.Equal("no arena block", LiveOpsCatalog.Parse(MiniJson.Serialize(root), null).ArenaProblem);

            // Without modes there is no Special Event, and every other event is still listed.
            using var s = new MiscServer(new ManualClock(Wednesday));
            LiveOpsTests.OverrideContent(s, LiveOpsCatalog.ContentName, MiniJson.Serialize(root));
            var doc = MiscServer.Obj(s.Get("/configs/users/me?access_token=t"));
            Assert.Empty(ArenaIds(doc));
            Assert.Equal(37, Participation(doc).Count);
        }

        // ------------------------------------------------------------------------------------------ validator

        private static Dictionary<string, object> Good()
        {
            LiveOpsCatalog c = Shipped();
            return LiveOpsEvents.ArenaMode(c, c.Arena, new ArenaWindow(Wednesday, 24), c.Arena.Mode("havoc"), "Random");
        }

        private static string ProblemWith(Action<Dictionary<string, object>> change)
        {
            Dictionary<string, object> e = Good();
            var template = MiniJson.ParseObject((string)e["_template"]);
            change((Dictionary<string, object>)template["event_tuning"]);
            e["_template"] = MiniJson.Serialize(template);
            return TleTemplateValidator.Problem(e);
        }

        private static Dictionary<string, object> P(object value) => new Dictionary<string, object> { ["private"] = "1", ["value"] = value };

        [Fact]
        public void TheValidatorRefusesEveryArenaTrap()
        {
            Assert.Null(TleTemplateValidator.Problem(Good()));
            Assert.Contains("img", ProblemWith(t => t.Remove("img")));
            Assert.Contains("img", ProblemWith(t => t["img"] = P("  ")));
            Assert.Contains("img", ProblemWith(t => t["img"] = "Menus/assets/png/tournament/mode_all.png"));   // a bare value: ParseElement drops it
            Assert.Null(ProblemWith(t => t["img"] = new Dictionary<string, object> { ["type"] = "text", ["value"] = new Dictionary<string, object> { ["en"] = "a.png" } }));
            Assert.Contains("AgentRestrictions", ProblemWith(t => t.Remove("AgentRestrictions")));
            Assert.Contains("AgentRestrictions", ProblemWith(t => t["AgentRestrictions"] = P(new List<object>())));
            Assert.Contains("AgentRestrictions", ProblemWith(t => t["AgentRestrictions"] = P("Merc_1")));
            Assert.Contains("Any", ProblemWith(t => t["AgentRestrictions"] = P(new List<object> { "Merc_1", "Any" })));
            Assert.Contains("MercClass_", ProblemWith(t => t["AgentRestrictions"] = P(new List<object> { "MercClass_Tank" })));
            foreach (object bad in new object[] { "Merc_", "Merc_x", "_3", "Merc_07", "3", 3L, "Merc 3" })
                Assert.NotNull(ProblemWith(t => t["AgentRestrictions"] = P(new List<object> { bad })));
            Assert.Contains("MatchType", ProblemWith(t => t["MatchType"] = P("CaptureFlag")));
            Assert.Contains("MatchType", ProblemWith(t => t["MatchType"] = P(2L)));
            Assert.Null(ProblemWith(t => t["MatchType"] = P("None")));
            Assert.Contains("MapName", ProblemWith(t => t["MapName"] = P("Barrage")));
            Assert.Contains("MapName", ProblemWith(t => t["MapName"] = P("Port_Tutorial")));
            Assert.Null(ProblemWith(t => t["MapName"] = P("Port")));
            Assert.Null(ProblemWith(t => t.Remove("MapName")));
            Assert.Contains("MatchParameterSets", ProblemWith(t => t["MatchParameterSets"] = P("ArenaHavoc")));
            Assert.Contains("MatchParameterSets", ProblemWith(t => t["MatchParameterSets"] = P(new List<object> { 1L })));
            Assert.Contains("Multiplier", ProblemWith(t => t["CreditsMVPAmountMultiplier"] = P("x2")));
            Assert.Contains("AllowDuplicateMercs", ProblemWith(t => t["AllowDuplicateMercs"] = P("yes")));
            Assert.Contains("EventName", ProblemWith(t => t.Remove("EventName")));
            Assert.Contains("MatchTypeText", ProblemWith(t => t["MatchTypeText"] = P("")));
            // Other event types are not held to these rules.
            Assert.Null(ProblemWith(t => { t["event_type"] = P("ShopCrateV2Event"); t.Remove("img"); }));
        }

        [Fact]
        public void TheChainCheckFindsGapsOverlapsAndAMissingNextWindow()
        {
            LiveOpsCatalog c = Shipped();
            LiveOpsArenaMode m = c.Arena.Mode("tdm");
            Dictionary<string, object> At(DateTime start, int hours) => LiveOpsEvents.ArenaMode(c, c.Arena, new ArenaWindow(start, hours), m, "Random");
            DateTime day = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
            Assert.Null(TleTemplateValidator.ArenaChainProblem(new[] { At(day, 24), At(day.AddDays(1), 24) }, Wednesday));
            Assert.Null(TleTemplateValidator.ArenaChainProblem(new[] { At(day.AddDays(1), 24), At(day, 24) }, Wednesday));   // any order
            Assert.Contains("gap", TleTemplateValidator.ArenaChainProblem(new[] { At(day, 24), At(day.AddDays(1).AddHours(1), 24) }, Wednesday));
            Assert.Contains("overlap", TleTemplateValidator.ArenaChainProblem(new[] { At(day, 24), At(day.AddHours(12), 24) }, Wednesday));
            Assert.Contains("not listed", TleTemplateValidator.ArenaChainProblem(new[] { At(day, 24) }, Wednesday));
            Assert.Contains("in progress", TleTemplateValidator.ArenaChainProblem(new[] { At(day.AddDays(1), 24), At(day.AddDays(2), 24) }, Wednesday));
            Assert.Contains("in progress", TleTemplateValidator.ArenaChainProblem(new Dictionary<string, object>[0], Wednesday));
        }

        /// <summary>The zero-code route (special-event.md §6a, a hand-written event in content/social/events.json) goes
        /// through the same validator: a template that would break the client is dropped, the rest is served.</summary>
        [Fact]
        public void AHandWrittenArenaEventWithoutImgIsDropped()
        {
            using var s = new MiscServer(new ManualClock(Wednesday));
            TestPlayer p = s.NewPlayer("poc");
            Dictionary<string, object> good = Good(), bad = Good();
            good["id"] = "mcvs_arena_poc";
            var template = MiniJson.ParseObject((string)bad["_template"]);
            ((Dictionary<string, object>)template["event_tuning"]).Remove("img");
            bad["_template"] = MiniJson.Serialize(template);
            bad["id"] = "mcvs_arena_poc_noimg";
            string path = System.IO.Path.Combine(s.Context.DataDir, "content", "social", "events.json");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, MiniJson.Serialize(new Dictionary<string, object> { ["events"] = new List<object> { good, bad } }));
            Assert.Equal("social/events.json", SocialEvents.ContentName);
            var served = MiniJson.ParseArray(s.Get("/events?event_ids=mcvs_arena_poc,mcvs_arena_poc_noimg&language=en", p.OnBehalfToken).BodyText);
            Assert.Equal(new[] { "mcvs_arena_poc" }, served.Select(e => (string)((Dictionary<string, object>)e)["id"]));
        }
    }
}
