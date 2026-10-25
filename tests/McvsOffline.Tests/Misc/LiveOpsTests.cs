using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Leaderboards;
using McvsOffline.Server.Services.Social;
using McvsOffline.Server.Services.Social.LiveOps;
using Xunit;

namespace McvsOffline.Tests.Misc
{
    /// <summary>
    /// Live ops (docs/LIVEOPS.md): the deterministic schedule, the CRM layer (participation ids, expiry, weekly
    /// modifiers), the generated events and the season rivals, through the composed server.
    /// </summary>
    public class LiveOpsTests
    {
        // Sunday 2026-10-25 12:00 UTC: season 2026-10-19 (Monday) .. 2026-10-26.
        private static readonly DateTime Sunday = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Wednesday = new DateTime(2026, 10, 21, 9, 30, 0, DateTimeKind.Utc);

        private static LiveOpsSettings Settings(int days = 7, int resetHour = 0, string anchor = null)
        {
            var s = new Settings();
            s.Override("liveops.seasonDays", (long)days);
            s.Override("liveops.dailyResetHour", (long)resetHour);
            if (anchor != null) s.Override("liveops.seasonStart", anchor);
            return LiveOpsSettings.From(s, null);
        }

        // ------------------------------------------------------------------------------------------ schedule

        [Fact]
        public void SeasonsStartOnMondayMidnightUtcAndDaysAtTheResetHour()
        {
            LiveOpsWindow season = LiveOpsSchedule.SeasonAt(Settings(), Sunday);
            Assert.Equal(new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc), season.Start);
            Assert.Equal(new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc), season.End);
            Assert.Equal(DayOfWeek.Monday, season.Start.DayOfWeek);
            Assert.Equal("2026101900x7", season.Key);
            Assert.Equal(season.Start, LiveOpsSchedule.SeasonAt(Settings(), season.Start).Start);   // a boundary belongs to the new season
            Assert.Equal(season.Start.AddDays(-7), LiveOpsSchedule.SeasonAt(Settings(), season.Start.AddTicks(-1)).Start);

            // Before the anchor and with another length: still a grid, floor not truncation.
            LiveOpsWindow early = LiveOpsSchedule.SeasonAt(Settings(3, 0, "2026-10-01T00:00:00Z"), new DateTime(2026, 9, 29, 1, 0, 0, DateTimeKind.Utc));
            Assert.Equal(new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc), early.Start);
            Assert.Equal(3, early.Days);

            LiveOpsWindow day = LiveOpsSchedule.DayAt(Settings(7, 6), new DateTime(2026, 10, 25, 5, 59, 0, DateTimeKind.Utc));
            Assert.Equal(new DateTime(2026, 10, 24, 6, 0, 0, DateTimeKind.Utc), day.Start);
            Assert.Equal("2026102406", day.Key);
            Assert.Equal(new DateTime(2026, 10, 25, 6, 0, 0, DateTimeKind.Utc),
                         LiveOpsSchedule.DayAt(Settings(7, 6), new DateTime(2026, 10, 25, 6, 0, 0, DateTimeKind.Utc)).Start);
        }

        [Fact]
        public void IdsAndBoardNamesCarryTheirWindow()
        {
            LiveOpsWindow season = LiveOpsSchedule.SeasonAt(Settings(), Sunday);
            string id = LiveOpsSchedule.DivisionEventId(season, 5);
            Assert.Equal("mcvs_season_2026101900x7_d05", id);
            Assert.True(LiveOpsSchedule.TryParseDivisionEvent(id, out LiveOpsWindow parsed, out int division));
            Assert.Equal(season.Start, parsed.Start);
            Assert.Equal(7, parsed.Days);
            Assert.Equal(5, division);

            string board = LiveOpsSchedule.BoardName(season, 17);
            Assert.Equal("mcvs_division_2026101900x7_d17_Leaderboard", board);
            // The TS turns "Leaderboard" into "Leaderboard_QA" for a developer profile (every loopback player).
            Assert.True(LiveOpsSchedule.TryParseBoard(board.Replace("Leaderboard", "Leaderboard_QA"), out parsed, out division));
            Assert.Equal(17, division);
            Assert.Equal(season.End, parsed.End);

            Assert.Equal(season.Start, LiveOpsSchedule.TryParseWeekly(LiveOpsSchedule.WeeklyEventId(season)).Start);
            LiveOpsWindow day = LiveOpsSchedule.DayAt(Settings(), Sunday);
            Assert.Equal(day.Start, LiveOpsSchedule.TryParseDaily(LiveOpsSchedule.DailyEventId(day)).Start);

            foreach (string bad in new[] { "mcvs_season_2026101900x7", "mcvs_season_2026101900x7_d5", "mcvs_season_20261019x7_d05",
                                           "mcvs_season_2026133100x7_d05", "mcvs_season_2026101900x99_d05", "mcvs_daily_2026102500x1",
                                           "mcvs_weekly_2026102500", "season_1", null })
            {
                Assert.False(LiveOpsSchedule.TryParseDivisionEvent(bad, out parsed, out division), bad);
            }
            Assert.Null(LiveOpsSchedule.TryParseDaily("mcvs_daily_2026102500x1"));
            Assert.Null(LiveOpsSchedule.TryParseWeekly("mcvs_weekly_2026102500"));
            Assert.False(LiveOpsSchedule.TryParseBoard("mcvs_division_2026101900x7_d05", out parsed, out division));
            Assert.False(LiveOpsSchedule.TryParseBoard("Leaderboard_S1_D3", out parsed, out division));
        }

        [Fact]
        public void WireDatesHaveNoIsoTSoNewtonsoftKeepsThemStrings()
        {
            string date = LiveOpsSchedule.WireDate(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal("2026-10-05 00:00:00Z", date);
            Assert.NotEqual('T', date[10]); // Newtonsoft's ISO date sniffing requires s[10] == 'T'
            Assert.True(TimeFormat.TryParse(date, out DateTime back));
            Assert.Equal(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), back);
        }

        // ------------------------------------------------------------------------------------------ CRM layer

        [Fact]
        public void CrmListsTheSeasonTheDayAndTheWeekAndExpiresAtTheNextStep()
        {
            var clock = new ManualClock(Wednesday);
            using var s = new MiscServer(clock);
            var doc = MiscServer.Obj(s.Get("/configs/users/me?access_token=t"));
            List<string> ids = Participation(doc);
            LiveOpsWindow season = LiveOpsSchedule.SeasonAt(Settings(), Wednesday);
            LiveOpsWindow next = new LiveOpsWindow(season.End, 7);
            for (int d = 1; d <= 17; d++)
            {
                Assert.Contains(LiveOpsSchedule.DivisionEventId(season, d), ids);
                Assert.Contains(LiveOpsSchedule.DivisionEventId(next, d), ids);
            }
            Assert.Contains("mcvs_daily_2026102100", ids);
            Assert.Contains("mcvs_daily_2026102200", ids);
            Assert.Contains("mcvs_weekly_2026101900x7", ids);
            Assert.DoesNotContain("mcvs_weekly_2026102600x7", ids); // announced only on the last day
            Assert.Equal(17 * 2 + 2 + 1, ids.Count);
            Assert.Equal("2026-10-22 00:00:00Z", doc["expiry"]);

            // Last day: next week's event is announced and the document expires with the season.
            clock.UtcNow = new DateTime(2026, 10, 25, 23, 0, 0, DateTimeKind.Utc);
            doc = MiscServer.Obj(s.Get("/configs/users/me?access_token=t"));
            ids = Participation(doc);
            Assert.Contains("mcvs_weekly_2026102600x7", ids);
            Assert.Equal("2026-10-26 00:00:00Z", doc["expiry"]);

            // A document served just before a boundary already belongs to the next step (the TS refetches CRM on every
            // frame while the expiry is in the past).
            clock.UtcNow = new DateTime(2026, 10, 25, 23, 59, 59, DateTimeKind.Utc);
            doc = MiscServer.Obj(s.Get("/configs/users/me?access_token=t"));
            Assert.Equal("2026-10-27 00:00:00Z", doc["expiry"]);
            Assert.Contains("mcvs_season_2026102600x7_d01", Participation(doc));
            Assert.Contains("mcvs_season_2026110200x7_d01", Participation(doc));
            Assert.Contains("mcvs_daily_2026102600", Participation(doc));
        }

        [Fact]
        public void TheExpiryIsAlwaysAheadOfTheClock()
        {
            var clock = new ManualClock(new DateTime(2026, 10, 18, 22, 0, 0, DateTimeKind.Utc));
            using var s = new MiscServer(clock);
            s.Set("liveops.dailyResetHour", 5L);
            LiveOpsService live = LiveOpsService.For(s.Context);
            for (int i = 0; i < 24 * 9; i++)
            {
                DateTime now = clock.UtcNow;
                LiveOpsSnapshot snap = live.Snapshot(now);
                Assert.True(snap.Expiry.Value > now.AddSeconds(1), "expiry " + snap.Expiry + " at " + now);
                Assert.True(snap.Season.Contains(now.AddSeconds(2)) && snap.Day.Contains(now.AddSeconds(2)));
                Assert.Equal(5, snap.Day.Start.Hour);
                clock.Advance(TimeSpan.FromMinutes(59.99));
            }
        }

        [Fact]
        public void DisabledLiveOpsLeaveTheCrmAsBefore()
        {
            using var s = new MiscServer(new ManualClock(Wednesday));
            s.Set("liveops.enabled", false);
            var doc = MiscServer.Obj(s.Get("/configs/users/me?access_token=t"));
            Assert.Null(JsonPath.Get(doc, "groupware.events.participation"));
            Assert.False(doc.ContainsKey("expiry"));
            TestPlayer p = s.NewPlayer("off");
            Assert.Equal("[]", s.Get("/events?event_ids=mcvs_daily_2026102100,mcvs_season_2026101900x7_d01&language=en", p.OnBehalfToken).BodyText);

            // Switching parts off removes only those ids.
            s.Set("liveops.enabled", true);
            s.Set("liveops.dailyQuests", false);
            s.Set("liveops.weeklyEvents", false);
            List<string> ids = Participation(MiscServer.Obj(s.Get("/configs/users/me?access_token=t")));
            Assert.Equal(34, ids.Count);
            Assert.All(ids, id => Assert.StartsWith("mcvs_season_", id));
        }

        [Fact]
        public void WeeklyModifiersRotateAndCombineWithTheEconomyMultipliers()
        {
            var clock = new ManualClock(Wednesday);
            using var s = new MiscServer(clock);
            LiveOpsService live = LiveOpsService.For(s.Context);
            LiveOpsCatalog catalog = live.Catalog;
            var seen = new List<string>();
            for (int week = 0; week < catalog.Rotation.Count; week++)
            {
                LiveOpsModifier m = live.Snapshot(clock.UtcNow).Modifier;
                seen.Add(m.Id);
                clock.Advance(TimeSpan.FromDays(7));
            }
            Assert.Equal(catalog.Rotation.OrderBy(x => x), seen.OrderBy(x => x)); // every modifier once per cycle

            // Fixed coinRush (x1.5) with economy.versusCoinsMultiplier 2: one MUL cell "*3" per field, both table copies.
            s.Set("liveops.modifiers", "coinRush");
            s.Set("economy.versusCoinsMultiplier", 2.0);
            var doc = MiscServer.Obj(s.Get("/configs/users/me?access_token=t"));
            Assert.Equal("*3", JsonPath.Get(doc, "game._OverrideTables.MCVS_Meta.VersusCoinsMatchRewards.Division@VictoryAmount.5"));
            Assert.Equal("*3", JsonPath.Get(doc, "game._OverrideTables.MCVS_Meta.VersusCoinsMatchRewards.Division@DailyCap.17"));
            Assert.Equal("*3", JsonPath.Get(doc, "game._ServerConfig.default._OverrideTables.MCVS_Meta.VersusCoinsMatchRewards.Division@VictoryAmount.5"));

            s.Set("liveops.modifiers", "trophyFever");
            s.Set("liveops.seasonRewardMultiplier", 2.0);
            doc = MiscServer.Obj(s.Get("/configs/users/me?access_token=t"));
            Assert.Equal("*1.5", JsonPath.Get(doc, "game._OverrideTables.MCVS_Meta.PlayerLevelXP.Level@DivisionTrophiesBonus.46"));
            Assert.Equal("*2", JsonPath.Get(doc, "game._OverrideTables.MCVS_Meta.PVPDivisionRewards.Rank@Division03.1"));
            Assert.Equal("*2", JsonPath.Get(doc, "game._OverrideTables.MCVS_Meta.VersusCoinsMatchRewards.Division@VictoryAmount.5")); // economy only

            s.Set("liveops.modifiers", "off");
            s.Set("liveops.seasonRewardMultiplier", 1.0);
            doc = MiscServer.Obj(s.Get("/configs/users/me?access_token=t"));
            Assert.Null(JsonPath.Get(doc, "game._OverrideTables.MCVS_Meta.PlayerLevelXP.Level@DivisionTrophiesBonus"));
            Assert.Null(JsonPath.Get(doc, "game._OverrideTables.MCVS_Meta.PVPDivisionRewards"));
        }

        // ------------------------------------------------------------------------------------------ events

        [Fact]
        public void EveryListedEventIsServedParserSafeAndDeterministic()
        {
            var clock = new ManualClock(Wednesday);
            using var s = new MiscServer(clock);
            TestPlayer p = s.NewPlayer("quests");
            for (int day = 0; day < 15; day++)
            {
                List<string> ids = Participation(MiscServer.Obj(s.Get("/configs/users/me?access_token=t")));
                string query = "/events?event_ids=" + string.Join(",", ids) + "&language=en";
                string body = s.Get(query, p.OnBehalfToken).BodyText;
                var events = MiniJson.ParseArray(body);
                Assert.Equal(ids, events.Select(e => (string)((Dictionary<string, object>)e)["id"]).ToList());
                foreach (Dictionary<string, object> e in events)
                {
                    Assert.Null(TleTemplateValidator.Problem(e));
                    Assert.IsType<string>(e["_template"]);
                    Assert.True(int.TryParse((string)e["_version"], NumberStyles.None, CultureInfo.InvariantCulture, out int v) && v > 0);
                    Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:00:00Z$", (string)e["start_date"]);
                }
                Assert.Equal(body, s.Get(query, p.OnBehalfToken).BodyText); // same clock, same answer
                clock.Advance(TimeSpan.FromDays(1));
            }
        }

        [Fact]
        public void DailyQuestsRotateFromTheCatalogue()
        {
            LiveOpsCatalog catalog = Catalog();
            var lineups = new HashSet<string>();
            var trackers = new HashSet<string>();
            DateTime start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 28; i++)
            {
                var day = new LiveOpsWindow(start.AddDays(i), 1);
                var e = LiveOpsEvents.DailyQuest(catalog, day);
                var achievements = Achievements(e);
                Assert.Equal(catalog.DailyCount + 1, achievements.Count);
                var picked = achievements.Take(catalog.DailyCount).Select(a => (string)a["primary_tracker"]).ToList();
                Assert.Equal(picked.Count, picked.Distinct().Count());
                Assert.Equal("CompleteAllObjectives", achievements.Last()["primary_tracker"]);
                Assert.Equal((long)catalog.DailyCount, JsonPath.Get((Dictionary<string, object>)((List<object>)achievements.Last()["milestones"])[0], "milestone_value"));
                lineups.Add(string.Join(",", picked));
                foreach (string t in picked) trackers.Add(t);
                Assert.Contains("DailyQuestTitle", (string)Tuning(e)["title"]);
                Assert.Equal(MiniJson.Serialize(e), MiniJson.Serialize(LiveOpsEvents.DailyQuest(catalog, day)));
            }
            Assert.True(lineups.Count >= 10, "only " + lineups.Count + " different daily line-ups in 4 weeks");
            Assert.Equal(catalog.Objectives.Count(o => o.DailyGoals != null), trackers.Count);
        }

        [Fact]
        public void WeeklyEventsHaveAThemeScaledGoalsAndTheSeasonCrate()
        {
            LiveOpsCatalog catalog = Catalog();
            var season = new LiveOpsWindow(new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc), 7);
            var standard = catalog.Modifier("standard");
            var e = LiveOpsEvents.WeeklyEvent(catalog, season, standard);
            var achievements = Achievements(e);
            Assert.Equal(catalog.WeeklyCount + 1, achievements.Count);
            Assert.False(((string)Tuning(e)["title"]).Contains("DailyQuest"));
            Assert.False(Tuning(e).ContainsKey("tag_text"));
            var final = achievements.Last();
            Assert.Equal("CompleteAllObjectives", final["primary_tracker"]);
            var finalRewards = (List<object>)((Dictionary<string, object>)((List<object>)final["milestones"])[0])["rewards"];
            string crate = catalog.EventCrates[(int)(LiveOpsSchedule.Ordinal(season) % catalog.EventCrates.Count)];
            Assert.Contains(finalRewards, r => (string)((Dictionary<string, object>)r)["item"] == crate);
            Assert.Equal(e["start_date"], "2026-10-19 00:00:00Z");
            Assert.Equal(e["end_date"], "2026-10-26 00:00:00Z");

            // A boosted week shows the tag; a 14-day season doubles the goals and is another event (other revision).
            var boosted = LiveOpsEvents.WeeklyEvent(catalog, season, catalog.Modifier("coinRush"));
            Assert.Equal(catalog.WeeklyBoostedTag, Tuning(boosted)["tag_text"]);
            Assert.Equal(e["_version"], boosted["_version"]); // the tag alone must not reset the player's progress
            var longSeason = new LiveOpsWindow(season.Start, 14);
            var twoWeeks = Achievements(LiveOpsEvents.WeeklyEvent(catalog, longSeason, standard));
            foreach (var a in twoWeeks.Take(twoWeeks.Count - 1))
            {
                LiveOpsObjective o = catalog.Objectives.First(x => x.Tracker == (string)a["primary_tracker"]);
                var goals = ((List<object>)a["milestones"]).Select(m => (long)((Dictionary<string, object>)m)["milestone_value"]).ToList();
                Assert.Equal(o.WeeklyGoals.Select(g => (long)g * 2), goals);
            }
            Assert.Equal(new[] { 1, 2, 3 }, LiveOpsEvents.ScaleGoals(new List<int> { 1, 1, 2 }, 7));
            Assert.Equal(new[] { 1, 2 }, LiveOpsEvents.ScaleGoals(new List<int> { 2, 3 }, 1));
        }

        [Fact]
        public void DivisionSeasonEventsCarryTheFieldsDivisionSeasonEventReads()
        {
            LiveOpsCatalog catalog = Catalog();
            var season = new LiveOpsWindow(new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc), 7);
            var e = LiveOpsEvents.DivisionSeason(catalog, season, 9);
            Assert.Equal("mcvs_season_2026101900x7", e["_batch_tag"]);
            Assert.Equal("(CurrentDivision == 9)", e["_participation"]);
            Assert.Equal("mcvs_division_2026101900x7_d09_Leaderboard", JsonPath.Get(e, "tournament.leaderboard.name"));
            Assert.Equal("desc", JsonPath.Get(e, "tournament.leaderboard.order"));
            Assert.Equal("DivisionSeasonEvent", JsonPath.Get(MiniJson.ParseObject((string)e["_template"]), "event_tuning.event_type.value"));
            Assert.Null(TleTemplateValidator.Problem(e));
        }

        [Fact]
        public void PersistentIdsOfPastWindowsAreStillServed()
        {
            using var s = new MiscServer(new ManualClock(Wednesday));
            TestPlayer p = s.NewPlayer("old");
            // The TS adds its profile's division Data.Id and its kept TleAchievements to every request.
            var list = MiscServer.Arr(s.Get("/events?event_ids=mcvs_season_2026090700x7_d03,mcvs_daily_2026090100,mcvs_weekly_2026083100x7&language=en",
                                            p.OnBehalfToken));
            Assert.Equal(3, list.Count);
            Assert.Equal("2026-09-14 00:00:00Z", ((Dictionary<string, object>)list[0])["end_date"]);
        }

        // ------------------------------------------------------------------------------------------ rivals

        [Fact]
        public void TheSeasonBoardIsAGroupOfHundredCalibratedToThePromotionLine()
        {
            var clock = new ManualClock(Wednesday);
            using var s = new MiscServer(clock);
            TestPlayer p = s.NewPlayer("climber");
            LiveOpsWindow season = LiveOpsSchedule.SeasonAt(Settings(), Wednesday);
            string board = LiveOpsSchedule.BoardName(season, 8).Replace("Leaderboard", "Leaderboard_QA");
            Assert.True(LiveOpsService.For(s.Context).TryGetSeasonBoard(board, out SeasonBoardInfo info));
            long promo = (long)SeasonRivals.PromotionScore(info, 30);
            int promotionRank = (int)Math.Floor(info.PromotionZone * 100 / 100.0);
            int demotionMinRank = 100 - (int)Math.Floor(info.DemotionZone * 100 / 100.0) + 1;
            long safe = (long)Math.Floor(info.SafeFraction * promo);

            Post(s, p, board, promo, 30);
            clock.UtcNow = season.End.AddDays(3); // the TS reads the old board when the next season starts, maybe days later
            var self = MiscServer.Obj(s.Get("/leaderboards/desc/" + board + "?limit=0", p.OnBehalfToken));
            Assert.Equal(100L, self["total_entries"]);
            long rank = (long)JsonPath.Get(self, "my_entry.rank");
            Assert.True(rank <= promotionRank, "rank " + rank + " with exactly the promotion score " + promo);

            Post(s, p, board, promo - 1, 30, at: Wednesday);
            clock.UtcNow = season.End.AddDays(3);
            rank = (long)JsonPath.Get(MiscServer.Obj(s.Get("/leaderboards/desc/" + board + "?limit=0", p.OnBehalfToken)), "my_entry.rank");
            Assert.Equal(promotionRank + 1, rank);

            Post(s, p, board, safe, 30, at: Wednesday);
            clock.UtcNow = season.End.AddDays(3);
            rank = (long)JsonPath.Get(MiscServer.Obj(s.Get("/leaderboards/desc/" + board + "?limit=0", p.OnBehalfToken)), "my_entry.rank");
            Assert.True(rank < demotionMinRank, "safe score " + safe + " ranked " + rank);

            Post(s, p, board, Math.Max(0, safe - 1), 30, at: Wednesday);
            clock.UtcNow = season.End.AddDays(3);
            rank = (long)JsonPath.Get(MiscServer.Obj(s.Get("/leaderboards/desc/" + board + "?limit=0", p.OnBehalfToken)), "my_entry.rank");
            Assert.True(rank >= demotionMinRank, "below the safe line ranked " + rank);
        }

        [Fact]
        public void RivalsProgressDuringTheSeasonAndFreezeAfterIt()
        {
            var clock = new ManualClock(new DateTime(2026, 10, 19, 6, 0, 0, DateTimeKind.Utc));
            using var s = new MiscServer(clock);
            TestPlayer p = s.NewPlayer("watcher");
            LiveOpsWindow season = LiveOpsSchedule.SeasonAt(Settings(), clock.UtcNow);
            string board = LiveOpsSchedule.BoardName(season, 5);
            Post(s, p, board, 0, 20);

            long Total() => MiscServer.Obj(s.Get("/leaderboards/desc/" + board + "?offset=0&limit=100", p.OnBehalfToken))["data"] is List<object> data
                ? data.Sum(e => (long)((Dictionary<string, object>)e)["score"]) : -1;

            long monday = Total();
            clock.UtcNow = season.Start.AddDays(3.5);
            long midweek = Total();
            clock.UtcNow = season.End.AddMinutes(-1);
            long sunday = Total();
            clock.UtcNow = season.End.AddDays(1);
            long after = Total();
            clock.UtcNow = season.End.AddDays(40);
            long muchLater = Total();
            Assert.True(monday < midweek && midweek < sunday && sunday <= after, monday + " " + midweek + " " + sunday + " " + after);
            Assert.Equal(after, muchLater);

            // Same board, same rivals (names and credentials), whatever the time.
            var a = MiscServer.Obj(s.Get("/leaderboards/desc/" + board + "?offset=0&limit=5", p.OnBehalfToken))["data"];
            var b = MiscServer.Obj(s.Get("/leaderboards/desc/" + board + "?offset=0&limit=5", p.OnBehalfToken))["data"];
            Assert.Equal(MiniJson.Serialize(a), MiniJson.Serialize(b));
        }

        [Fact]
        public void RivalsScaleWithThePlayersLevelAndTheDivision()
        {
            using var s = new MiscServer(new ManualClock(Wednesday));
            LiveOpsService live = LiveOpsService.For(s.Context);
            LiveOpsWindow season = LiveOpsSchedule.SeasonAt(Settings(), Wednesday);
            Assert.True(live.TryGetSeasonBoard(LiveOpsSchedule.BoardName(season, 3), out SeasonBoardInfo bronze));
            Assert.True(live.TryGetSeasonBoard(LiveOpsSchedule.BoardName(season, 15), out SeasonBoardInfo diamond));
            Assert.True(SeasonRivals.PromotionScore(bronze, 40) > SeasonRivals.PromotionScore(bronze, 5));
            // Per trophy of a win, a higher division asks for more matches.
            Assert.True(SeasonRivals.PromotionScore(diamond, 20) / diamond.TrophiesPerWin(20) > SeasonRivals.PromotionScore(bronze, 20) / bronze.TrophiesPerWin(20));

            s.Set("liveops.difficulty", 2.0);
            Assert.True(live.TryGetSeasonBoard(LiveOpsSchedule.BoardName(season, 3), out SeasonBoardInfo harder));
            Assert.Equal(Math.Ceiling(2 * bronze.MatchesPerDay * 7 * 0.5 * bronze.TrophiesPerWin(10)), SeasonRivals.PromotionScore(harder, 10), 0);

            double[] finals = SeasonRivals.FinalScores("k", bronze, 99, 100, 10);
            for (int i = 1; i < finals.Length; i++) Assert.True(finals[i] <= finals[i - 1]);
            Assert.True(finals[0] >= bronze.TopFactor * SeasonRivals.PromotionScore(bronze, 10) * 0.9);
        }

        [Fact]
        public void OtherBoardsKeepTheStaticRivals()
        {
            using var s = new MiscServer(new ManualClock(Wednesday));
            TestPlayer p = s.NewPlayer("other");
            var top = MiscServer.Obj(s.Get("/leaderboards/desc/Leaderboard_S1_D3?offset=0&limit=1", p.OnBehalfToken));
            Assert.Equal(100L, top["total_entries"]);
            long best = (long)((Dictionary<string, object>)((List<object>)top["data"])[0])["score"];
            Assert.InRange(best, 300, 450); // leaderboards.virtualTopScore 400 ± 10 %
        }

        [Fact]
        public void TheDiagnosticsPageDescribesTheSchedule()
        {
            using var s = new MiscServer(new ManualClock(Wednesday));
            var page = MiscServer.Obj(s.Get("/_mcvs/liveops"));
            Assert.Equal(true, page["enabled"]);
            Assert.Equal("mcvs_season_2026101900x7", JsonPath.Get(page, "season.id"));
            Assert.Equal("mcvs_daily_2026102100", JsonPath.Get(page, "day.id"));
            Assert.Equal(37, ((List<object>)page["eventIds"]).Count);
            Assert.Equal(2, ((List<object>)page["achievementEvents"]).Count);
            Assert.NotNull(page["modifier"]);
        }

        // ------------------------------------------------------------------------------------------ division tables

        [Fact]
        public void DivisionTablesAreCutOutOfTheDesignFile()
        {
            string design = "{\"Other\": {\"Fields\": [\"Id\"], \"Rows\": [[\"x}\\\"{\"]]}, " +
                            "\"DivisionsList\": {\"Fields\": [\"Id\", \"Divisions.PromotionZone\", \"Divisions.DemotionZone\", \"DivisionTrophies.Lose\", " +
                            "\"DivisionTrophies.Win\"], \"Rows\": [[1, 50, 0, 0, 0], [17, 0, 30, 25, 3]]}, " +
                            "\"PlayerLevelXP\": {\"Fields\": [\"Level\", \"DivisionTrophiesBonus\"], \"Rows\": [[1, 2], [2, 2], [3, 3]]}}";
            var tables = new DivisionTables(new ManualClock(Wednesday), () => design, null);
            Assert.Equal(50, tables.Division(1).PromotionZone);
            Assert.Equal(30, tables.Division(17).DemotionZone);
            Assert.Equal(25, tables.Division(17).TrophiesLose);
            Assert.Equal(3, tables.Division(17).TrophiesWin);
            Assert.Equal(3, tables.LevelBonus(3));
            Assert.Equal(3, tables.LevelBonus(40)); // nearest level
            Assert.Null(tables.Division(2));

            int reads = 0;
            var clock = new ManualClock(Wednesday);
            var missing = new DivisionTables(clock, () => { reads++; return null; }, null);
            Assert.Null(missing.Division(1));
            Assert.Null(missing.Division(1));
            Assert.Equal(1, reads); // retried at most once a minute
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Null(missing.LevelBonus(1));
            Assert.Equal(2, reads);
        }

        [Fact]
        public void RealDivisionTablesFromTheDataPack()
        {
            string path = System.IO.Path.Combine(Environment.GetEnvironmentVariable("MCVS_DATAPACK_DECODED") ?? "/home/user/mcvs-work/datapack-decoded",
                                                 "Design", "MCVS_Meta.xlsx");
            if (!System.IO.File.Exists(path)) return; // no game data on this machine
            string text = System.IO.File.ReadAllText(path);
            var tables = new DivisionTables(new ManualClock(Wednesday), () => text, null);
            Assert.Equal(50, tables.Division(1).PromotionZone);     // Starter: top half promoted, nobody demoted
            Assert.Equal(0, tables.Division(1).DemotionZone);
            Assert.Equal(0, tables.Division(17).PromotionZone);     // Master
            Assert.Equal(30, tables.Division(17).DemotionZone);
            Assert.Equal(46, tables.LevelBonus(46));                // a win pays the player level in division trophies
            Assert.Equal(2, tables.LevelBonus(1));
        }

        // ------------------------------------------------------------------------------------------ helpers

        private static LiveOpsCatalog Catalog()
        {
            string text = new McvsOffline.Server.Core.Content.ContentProvider(null, null, null).TryReadContentText(LiveOpsCatalog.ContentName, out string t) ? t : null;
            LiveOpsCatalog c = LiveOpsCatalog.Parse(text, null);
            Assert.False(c.IsEmpty);
            return c;
        }

        private static List<string> Participation(Dictionary<string, object> doc)
        {
            var list = JsonPath.Get(doc, "groupware.events.participation") as List<object> ?? new List<object>();
            return list.Select(x => (string)((Dictionary<string, object>)x)["id"]).ToList();
        }

        private static Dictionary<string, object> Tuning(Dictionary<string, object> e)
        {
            var template = MiniJson.ParseObject((string)e["_template"]);
            var tuning = (Dictionary<string, object>)template["event_tuning"];
            var values = new Dictionary<string, object>();
            foreach (var kv in tuning) values[kv.Key] = ((Dictionary<string, object>)kv.Value)["value"];
            return values;
        }

        private static List<Dictionary<string, object>> Achievements(Dictionary<string, object> e)
        {
            var template = MiniJson.ParseObject((string)e["_template"]);
            return ((List<object>)JsonPath.Get(template, "milestones.value")).Cast<Dictionary<string, object>>().ToList();
        }

        private static void Post(MiscServer s, TestPlayer p, string board, long score, int level, DateTime? at = null)
        {
            if (at.HasValue) s.Clock.UtcNow = at.Value;
            var r = s.Post("/leaderboards/desc/" + board + "/me", p.OnBehalfToken, "score", score.ToString(CultureInfo.InvariantCulture),
                           "display_name", "Me", "_level", level.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(200, r.StatusCode);
        }
    }
}
