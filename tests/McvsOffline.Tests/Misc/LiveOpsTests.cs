using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm.Collector;
using McvsOffline.Server.Services.Leaderboards;
using McvsOffline.Server.Services.Social;
using McvsOffline.Server.Services.Social.LiveOps;
using McvsOffline.Tests.Content;
using McvsOffline.Tests.Golden;
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
                                           "mcvs_season_2026101900x1_d05", "mcvs_season_2026101900x07_d05", "mcvs_season_2026101900x_d05",
                                           "mcvs_weekly_2026102500", "season_1", null })
            {
                Assert.False(LiveOpsSchedule.TryParseDivisionEvent(bad, out parsed, out division), bad);
            }
            Assert.Null(LiveOpsSchedule.TryParseDaily("mcvs_daily_2026102500x1"));
            Assert.Null(LiveOpsSchedule.TryParseDaily("mcvs_daily_2026102500x7"));
            Assert.Null(LiveOpsSchedule.TryParseWeekly("mcvs_weekly_2026102500x1"));   // not the form Key writes
            Assert.Null(LiveOpsSchedule.TryParseWeekly("mcvs_weekly_2026102500x07"));
            Assert.False(LiveOpsSchedule.TryParseBoard("mcvs_division_2026101900x7_d05", out parsed, out division));
            Assert.False(LiveOpsSchedule.TryParseBoard("Leaderboard_S1_D3", out parsed, out division));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(7)]
        [InlineData(28)]
        public void EverySeasonLengthGivesIdsThatParseBack(int days)
        {
            // liveops.seasonDays is 1..28: a one-day season has the bare "yyyyMMddHH" key (as a quest day).
            LiveOpsWindow season = LiveOpsSchedule.SeasonAt(Settings(days), Sunday);
            Assert.Equal(days, season.Days);
            Assert.Equal(days == 1 ? 10 : 12 + (days >= 10 ? 1 : 0), season.Key.Length);
            for (int d = 1; d <= 17; d++)
            {
                string id = LiveOpsSchedule.DivisionEventId(season, d);
                Assert.True(LiveOpsSchedule.TryParseDivisionEvent(id, out LiveOpsWindow parsed, out int division), id);
                Assert.Equal(d, division);
                Assert.Equal(season.Start, parsed.Start);
                Assert.Equal(season.End, parsed.End);
                Assert.Equal(id, LiveOpsSchedule.DivisionEventId(parsed, division));

                string board = LiveOpsSchedule.BoardName(season, d);
                Assert.True(LiveOpsSchedule.TryParseBoard(board, out parsed, out division), board);
                Assert.True(LiveOpsSchedule.TryParseBoard(board.Replace("Leaderboard", "Leaderboard_QA"), out parsed, out division), board);
                Assert.Equal(season.End, parsed.End);
                Assert.Equal(board, LiveOpsSchedule.BoardName(parsed, division));
            }
            string weekly = LiveOpsSchedule.WeeklyEventId(season);
            LiveOpsWindow w = LiveOpsSchedule.TryParseWeekly(weekly);
            Assert.NotNull(w);
            Assert.Equal(season.End, w.End);
            Assert.Equal(weekly, LiveOpsSchedule.WeeklyEventId(w));
            // A quest day's key never passes for a longer window, nor a season's for a quest day.
            Assert.Null(LiveOpsSchedule.TryParseDaily(LiveOpsSchedule.DailyPrefix + season.Key + (days == 1 ? "x1" : "")));
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

        [Theory]
        [InlineData(7)]
        [InlineData(3)]
        [InlineData(1)]
        public void EveryListedEventIsServedParserSafeAndDeterministic(int seasonDays)
        {
            var clock = new ManualClock(Wednesday);
            using var s = new MiscServer(clock);
            s.Set("liveops.seasonDays", (long)seasonDays);
            TestPlayer p = s.NewPlayer("quests");
            for (int day = 0; day < 15; day++)
            {
                List<string> ids = Participation(MiscServer.Obj(s.Get("/configs/users/me?access_token=t")));
                Assert.Equal(17 * 2 + 2 + (seasonDays > 1 && clock.UtcNow.AddSeconds(2) >= LiveOpsSchedule.SeasonAt(Settings(seasonDays), clock.UtcNow.AddSeconds(2)).End.AddDays(-1) ? 2 : 1),
                             ids.Count);
                // Every listed id has a definition (FindEvent), the division boards resolve to season boards.
                foreach (string id in ids.Where(i => i.StartsWith(LiveOpsSchedule.SeasonPrefix, StringComparison.Ordinal)))
                {
                    Assert.True(LiveOpsSchedule.TryParseDivisionEvent(id, out LiveOpsWindow w, out int division), id);
                    Assert.True(LiveOpsService.For(s.Context).TryGetSeasonBoard(LiveOpsSchedule.BoardName(w, division), out SeasonBoardInfo _), id);
                }
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

        // ------------------------------------------------------------------------------------------ the Cosmetic crate (economy-collector.md §3.2)

        /// <summary>The shipped collector <c>since</c>: Monday 2026-11-09 00:00 UTC.</summary>
        private static readonly DateTime Since = CollectorGuard.ShippedSince;

        /// <summary>Wednesday 2026-11-11 12:00 UTC: the day and the season started at or after <see cref="Since"/>.</summary>
        private static readonly DateTime AfterSince = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// WP-0's golden events (the daily quest and the weekly event of each of the 16 days, live ops on and off, text
        /// with <c>_version</c>) are served byte for byte with the collector off, collector.json missing or broken, or the
        /// store patch known missing; with the collector on, every window that starts before <c>since</c> keeps its text
        /// and only the windows from <c>since</c> on change (they carry the crate).
        /// </summary>
        [Theory]
        [InlineData("disabled")]
        [InlineData("contentMissing")]
        [InlineData("contentBroken")]
        [InlineData("storePatchMissing")]
        [InlineData("on")]
        public void LiveOpsCollectorOffMatchesGolden(string scenario)
        {
            Action<GoldenRun> setup;
            switch (scenario)
            {
                case "disabled": setup = run => run.Set("economy.collector.enabled", false); break;
                case "contentMissing": setup = run => TestContent.Install(run.Context).Hide(CollectorContent.FileName); break;
                case "contentBroken": setup = run => run.OverrideContent(CollectorContent.FileName, "{\"format\": "); break;
                case "storePatchMissing": setup = run => run.Context.Features.Get<ClientCapabilities>().StorePatch = false; break;
                case "on": setup = null; break;
                default: throw new ArgumentException(scenario);
            }
            List<GoldenOutput> events = GoldenScenarios.RunAll(setup)
                                                       .Where(o => o.Kind == GoldenScenarios.Daily || o.Kind == GoldenScenarios.Weekly).ToList();
            Assert.Equal(2 * 2 * GoldenScenarios.DayCount, events.Count);
            bool on = scenario == "on";
            List<GoldenOutput> kept = events.Where(o => !on || !o.LiveOps || WindowOf(o).Start < GoldenScenarios.Since).ToList();
            Assert.Empty(GoldenScenarios.Mismatches(kept));

            List<GoldenOutput> changed = events.Except(kept).ToList();
            int daysFromSince = (int)(GoldenScenarios.FirstDay.AddDays(GoldenScenarios.DayCount) - GoldenScenarios.Since).TotalDays;
            Assert.Equal(on ? 2 * daysFromSince : 0, changed.Count);       // the daily and the weekly event of 11-09 … 11-17
            Assert.Equal(changed.Count, GoldenScenarios.Mismatches(changed).Count);
            foreach (GoldenOutput o in changed)
            {
                Assert.Equal(200, o.Status);
                Assert.Contains(CollectorGuard.CrateId, (string)MiniJson.ParseObject(o.Text)["_template"]);
            }
            foreach (GoldenOutput o in kept.Where(o => o.LiveOps)) Assert.DoesNotContain(CollectorGuard.CrateId, o.Text);
        }

        [Fact]
        public void TheCrateFollowsTodaysRewardsInEveryWindowFromSince()
        {
            using var f = new LiveOpsFixture(AfterSince);
            LiveOpsCatalog catalog = f.LiveOps.Catalog;
            LiveOpsCollectorRewards collector = catalog.Collector;
            Assert.NotNull(collector);
            Assert.Null(catalog.CollectorProblem);
            Assert.Equal(Since, collector.Since);
            CollectorRuntime runtime = f.Runtime;
            int crateDays = 0, crateSeasons = 0;

            // Quest days at reset hours 0 and 6: a day that starts before since (2026-11-08 06:00) keeps its event.
            foreach (int resetHour in new[] { 0, 6 })
            {
                LiveOpsSettings s = Settings(7, resetHour);
                for (DateTime t = Since.AddDays(-3); t < Since.AddDays(10); t = t.AddHours(12))
                {
                    LiveOpsWindow day = LiveOpsSchedule.DayAt(s, t);
                    Dictionary<string, object> plain = LiveOpsEvents.DailyQuest(catalog, day);
                    Dictionary<string, object> served = LiveOpsEvents.DailyQuest(catalog, day, runtime);
                    Assert.Equal(day.Start >= Since, LiveOpsEvents.CratesIn(catalog, day, runtime));
                    if (day.Start < Since)
                    {
                        Assert.Equal(MiniJson.Serialize(plain), MiniJson.Serialize(served));
                        continue;
                    }
                    crateDays++;
                    List<Dictionary<string, object>> a = Achievements(served), p = Achievements(plain);
                    Assert.Equal(p.Count, a.Count);
                    // The day's first mission gets the crate after its rewards; the other missions and the bonus (with its
                    // Contracts crate: one crate per milestone) stay as they are.
                    Assert.Equal(p[0]["primary_tracker"], a[0]["primary_tracker"]);
                    Assert.Equal(JsonPath.Get(Milestone(p[0], 0), "milestone_value"), JsonPath.Get(Milestone(a[0], 0), "milestone_value"));
                    Assert.Equal(Rewards(p[0], 0).Concat(new[] { "Crate_Event_Special x1" }), Rewards(a[0], 0));
                    for (int i = 1; i < a.Count; i++) Assert.Equal(MiniJson.Serialize(p[i]), MiniJson.Serialize(a[i]));
                    Assert.Equal(new[] { "Crate_Contracts x1" }, Rewards(a.Last(), 0));
                    AssertOneCratePerMilestone(served);
                    Assert.NotEqual(plain["_version"], served["_version"]);
                    Assert.Null(TleTemplateValidator.Problem(served));
                }
            }

            // Seasons of 7 days and of 1 day: every tier gets its crates after today's rewards (tier 3 two: the final's,
            // which holds the event crate); the final stays as it is.
            foreach (int days in new[] { 7, 1 })
            {
                LiveOpsSettings s = Settings(days);
                LiveOpsModifier standard = catalog.Modifier("standard");
                for (DateTime t = Since.AddDays(-14); t < Since.AddDays(14); t = t.AddDays(1))
                {
                    LiveOpsWindow season = LiveOpsSchedule.SeasonAt(s, t);
                    Dictionary<string, object> plain = LiveOpsEvents.WeeklyEvent(catalog, season, standard);
                    Dictionary<string, object> served = LiveOpsEvents.WeeklyEvent(catalog, season, standard, runtime);
                    if (season.Start < Since)
                    {
                        Assert.Equal(MiniJson.Serialize(plain), MiniJson.Serialize(served));
                        continue;
                    }
                    crateSeasons++;
                    List<Dictionary<string, object>> a = Achievements(served), p = Achievements(plain);
                    Assert.Equal(p.Count, a.Count);
                    for (int i = 0; i < a.Count - 1; i++)
                    {
                        Assert.Equal(p[i]["primary_tracker"], a[i]["primary_tracker"]);
                        int milestones = ((List<object>)a[i]["milestones"]).Count;
                        Assert.Equal(3, milestones);
                        for (int m = 0; m < milestones; m++)
                        {
                            Assert.Equal(JsonPath.Get(Milestone(p[i], m), "milestone_value"), JsonPath.Get(Milestone(a[i], m), "milestone_value"));
                            Assert.Equal(Rewards(p[i], m).Concat(new[] { "Crate_Event_Special x" + (m == 2 ? 2 : 1) }), Rewards(a[i], m));
                        }
                    }
                    string eventCrate = catalog.EventCrates[(int)(LiveOpsSchedule.Ordinal(season) % catalog.EventCrates.Count)];
                    Assert.Equal(new[] { "HardCurrency x20", eventCrate + " x1" }, Rewards(a.Last(), 0));
                    Assert.Equal(MiniJson.Serialize(p.Last()), MiniJson.Serialize(a.Last()));
                    AssertOneCratePerMilestone(served);
                    Assert.NotEqual(plain["_version"], served["_version"]);
                    Assert.Null(TleTemplateValidator.Problem(served));
                }
            }
            Assert.Equal(2 * 2 * 10 - 1, crateDays);                       // 11-09 00:00 … 11-18 12:00, and from 11-09 06:00
            Assert.Equal(2 * 7 + 14, crateSeasons);                         // two weekly seasons of 7 days each, 14 one-day seasons

            // The catalogue's own lists are never changed by a call.
            Assert.Equal(new[] { "Crate_Contracts x1" }, catalog.DailyBonusRewards.Select(r => r.Item + " x" + r.Quantity));
            Assert.All(catalog.WeeklyMilestoneRewards, tier => Assert.DoesNotContain(tier, r => r.Item == CollectorGuard.CrateId));
        }

        [Fact]
        public void TogglingTheCollectorChangesTheKeyAndOnlyTheEventsFromSince()
        {
            var clock = new ManualClock(AfterSince);
            using var s = new MiscServer(clock);
            TestPlayer p = s.NewPlayer("toggle");
            LiveOpsService live = LiveOpsService.For(s.Context);
            ClientCapabilities client = s.Context.Features.Get<ClientCapabilities>();
            // Today and tomorrow, a day before since, the running season, the one before (kept alive) and the next one.
            string[] ids =
            {
                "mcvs_daily_2026111100", "mcvs_daily_2026111200", "mcvs_daily_2026110500",
                "mcvs_weekly_2026110900x7", "mcvs_weekly_2026110200x7", "mcvs_weekly_2026111600x7",
            };
            Dictionary<string, string> Versions()
            {
                var body = MiniJson.ParseArray(s.Get("/events?event_ids=" + string.Join(",", ids) + "&language=en", p.OnBehalfToken).BodyText);
                Assert.Equal(ids.Length, body.Count);
                return body.Cast<Dictionary<string, object>>().ToDictionary(e => (string)e["id"], e => (string)e["_version"]);
            }
            bool FromSince(string id) => (LiveOpsSchedule.TryParseDaily(id) ?? LiveOpsSchedule.TryParseWeekly(id)).Start >= Since;

            string key = live.Snapshot(clock.UtcNow).Key;
            string keyBefore = live.Snapshot(Wednesday).Key;
            Dictionary<string, string> on = Versions();

            s.Set("economy.collector.enabled", false);
            Assert.NotEqual(key, live.Snapshot(clock.UtcNow).Key);
            Dictionary<string, string> off = Versions();
            foreach (string id in ids)
            {
                if (FromSince(id)) Assert.NotEqual(on[id], off[id]);
                else Assert.Equal(on[id], off[id]);
            }
            // Before since the key changes as well (it is a cache key); the events there do not (above).
            Assert.NotEqual(keyBefore, live.Snapshot(Wednesday).Key);

            s.Set("economy.collector.enabled", true);
            Assert.Equal(key, live.Snapshot(clock.UtcNow).Key);
            Assert.Equal(on, Versions());

            // A setting the crate's presence does not depend on changes neither.
            s.Set("economy.collector.itemsPerCrate", 3L);
            s.Set("economy.collector.baseShare", 0.5);
            Assert.Equal(key, live.Snapshot(clock.UtcNow).Key);
            Assert.Equal(on, Versions());

            // The store patch known missing: no crate (as disabled); known present: the crate (another key than unknown).
            client.StorePatch = false;
            Assert.NotEqual(key, live.Snapshot(clock.UtcNow).Key);
            Assert.Equal(off, Versions());
            client.StorePatch = true;
            Assert.NotEqual(key, live.Snapshot(clock.UtcNow).Key);
            Assert.Equal(on, Versions());
            client.StorePatch = null;
            Assert.Equal(key, live.Snapshot(clock.UtcNow).Key);
        }

        public static IEnumerable<object[]> BrokenCollectorBlocks()
        {
            const string Crate = "{\"item\":\"Crate_Event_Special\",\"quantity\":1}";
            const string Tiers = "[[" + Crate + "],[" + Crate + "],[" + Crate + "]]";
            string Block(string since = "\"2026-11-09T00:00:00Z\"", string daily = "[]", string mission = "[" + Crate + "]", string tiers = Tiers,
                         string final = "[]", string extra = "")
                => "{\"since\":" + since + ",\"dailyBonus\":" + daily + ",\"dailyMission\":" + mission + ",\"weeklyMilestones\":" + tiers
                   + ",\"weeklyFinal\":" + final + extra + "}";
            return new[]
            {
                new object[] { "[]", "collector is not an object" },
                new object[] { "\"on\"", "collector is not an object" },
                new object[] { "{\"since\":\"2026-11-09T00:00:00Z\",\"dailyBonus\":[],\"weeklyMilestones\":" + Tiers + "}", "missing: dailyMission, weeklyFinal" },
                new object[] { Block(extra: ",\"weeklyFinals\":[]"), "unknown: weeklyFinals" },
                new object[] { Block(since: "\"next monday\""), "collector.since \"next monday\" is not an ISO 8601 time" },
                new object[] { Block(since: "20261109"), "collector.since 20261109 is not an ISO 8601 time" },
                new object[] { Block(since: "null"), "collector.since null is not an ISO 8601 time" },
                new object[] { Block(tiers: "[[" + Crate + "],[" + Crate + "]]"), "one reward list per weekly milestone tier (3)" },
                new object[] { Block(tiers: "[]"), "one reward list per weekly milestone tier (3)" },
                new object[] { Block(daily: Crate), "collector.dailyBonus must be a list of rewards" },
                new object[] { Block(tiers: "[[" + Crate + "],[" + Crate + "]," + Crate + "]"), "collector.weeklyMilestones[2] must be a list of rewards" },
                new object[] { Block(daily: "[{\"item\":\"Crate_Event_Special\",\"quantity\":0}]"), "collector.dailyBonus: the quantity of Crate_Event_Special must be a positive integer" },
                new object[] { Block(daily: "[{\"item\":\"Crate_Event_Special\",\"quantity\":1.5}]"), "the quantity of Crate_Event_Special must be a positive integer" },
                new object[] { Block(daily: "[{\"item\":\"Crate_Event_Special\",\"quantity\":\"1\"}]"), "the quantity of Crate_Event_Special must be a positive integer" },
                new object[] { Block(daily: "[{\"item\":\"Crate_Event_Special\"}]"), "the quantity of Crate_Event_Special must be a positive integer" },
                new object[] { Block(final: "[{\"quantity\":2}]"), "collector.weeklyFinal: a reward must be an {item, quantity} object" },
                new object[] { Block(final: "[7]"), "collector.weeklyFinal: a reward must be an {item, quantity} object" },
                new object[] { Block(final: "[{\"item\":\"Crate_Contracts\",\"quantity\":1}]"), "collector.weeklyFinal: Crate_Contracts is neither a currency nor Crate_Event_Special" },
                new object[] { Block(daily: "[{\"item\":\"crate_event_special\",\"quantity\":1}]"), "crate_event_special is neither a currency nor Crate_Event_Special" },
                new object[] { Block(daily: "[{\"item\":\"VersusCoins\",\"quantity\":100}]", mission: "[]", tiers: "[[],[],[]]", final: "[]"), "collector hands out no Crate_Event_Special" },
                new object[] { Block(mission: Crate), "collector.dailyMission must be a list of rewards" },
                // One crate per milestone (§3.1): the claim reveals only a milestone's last crate.
                new object[] { Block(daily: "[" + Crate + "]"), "collector.dailyBonus: the daily bonus already holds a crate (Crate_Contracts)" },
                new object[] { Block(final: "[" + Crate + "]"), "collector.weeklyFinal: the weekly final already holds a crate (the event crate)" },
                new object[] { Block(tiers: "[[" + Crate + "," + Crate + "],[],[]]"), "collector.weeklyMilestones[0]: Crate_Event_Special twice in one milestone" },
                new object[] { Block(mission: "[" + Crate + "," + Crate + "]"), "collector.dailyMission: Crate_Event_Special twice in one milestone" },
            };
        }

        /// <summary>A broken block is refused and logged; live ops run exactly as without it (no crate anywhere).</summary>
        [Theory]
        [MemberData(nameof(BrokenCollectorBlocks))]
        public void ABrokenCollectorBlockLeavesLiveOpsRunning(string block, string problem)
        {
            var logs = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(logs);
            LiveOpsCatalog broken = LiveOpsCatalog.Parse(LiveOpsContent.WithCollector(block), logger.For("liveops"));
            LiveOpsCatalog without = LiveOpsCatalog.Parse(LiveOpsContent.WithCollector(null), null);
            Assert.False(broken.IsEmpty);
            Assert.Null(broken.Collector);
            Assert.Contains(problem, broken.CollectorProblem);
            Assert.Equal("no collector block", without.CollectorProblem);
            LogEntry error = Assert.Single(logs.Snapshot(), e => e.Level >= LogLevel.Warn);
            Assert.Equal(LogLevel.Error, error.Level);
            Assert.Contains("collector block refused (" + broken.CollectorProblem + ")", error.Message);

            // Every event of two weeks from since, with a runtime that would hand out the crate, is the one without the block.
            using var f = new LiveOpsFixture(AfterSince);
            Assert.True(f.Runtime.CratesInWindow(AfterSince, Since));
            LiveOpsSettings s = Settings();
            for (DateTime t = Since.AddDays(-2); t < Since.AddDays(14); t = t.AddDays(1))
            {
                LiveOpsWindow day = LiveOpsSchedule.DayAt(s, t), season = LiveOpsSchedule.SeasonAt(s, t);
                Assert.Equal(MiniJson.Serialize(LiveOpsEvents.DailyQuest(without, day, f.Runtime)), MiniJson.Serialize(LiveOpsEvents.DailyQuest(broken, day, f.Runtime)));
                LiveOpsModifier m = broken.Modifier("standard");
                Assert.Equal(MiniJson.Serialize(LiveOpsEvents.WeeklyEvent(without, season, m, f.Runtime)),
                             MiniJson.Serialize(LiveOpsEvents.WeeklyEvent(broken, season, m, f.Runtime)));
                Assert.Equal(MiniJson.Serialize(LiveOpsEvents.DivisionSeason(without, season, 7)), MiniJson.Serialize(LiveOpsEvents.DivisionSeason(broken, season, 7)));
                Assert.DoesNotContain(CollectorGuard.CrateId, MiniJson.Serialize(LiveOpsEvents.DailyQuest(broken, day, f.Runtime)));
            }
        }

        [Fact]
        public void ABrokenCollectorBlockInAnOverrideStillServesEveryEvent()
        {
            var clock = new ManualClock(AfterSince);
            using var s = new MiscServer(clock);
            OverrideContent(s, LiveOpsCatalog.ContentName, LiveOpsContent.WithCollector("{\"since\":\"soon\"}"));
            TestPlayer p = s.NewPlayer("broken-block");
            List<string> ids = Participation(MiscServer.Obj(s.Get("/configs/users/me?access_token=t")));
            Assert.Equal(17 * 2 + 2 + 1, ids.Count);
            string body = s.Get("/events?event_ids=" + string.Join(",", ids) + "&language=en", p.OnBehalfToken).BodyText;
            Assert.Equal(ids.Count, MiniJson.ParseArray(body).Count);
            Assert.DoesNotContain(CollectorGuard.CrateId, body);
            Assert.Contains(s.Logs.Snapshot(), e => e.Level == LogLevel.Error && e.Message.Contains("collector block refused"));

            LiveOpsSnapshot snap = LiveOpsService.For(s.Context).Snapshot(clock.UtcNow);
            Assert.True(snap.On);
            Assert.Null(snap.CollectorSince);
            var page = MiscServer.Obj(s.Get("/_mcvs/liveops"));
            Assert.Equal(true, page["enabled"]);
            Assert.Equal("Guard", JsonPath.Get(page, "collector.phase"));   // since unknown: the guard from the shipped since
            Assert.Null(JsonPath.Get(page, "collector.since"));
            Assert.Empty((List<object>)JsonPath.Get(page, "collector.activeFor"));
            Assert.StartsWith("collector needs exactly the keys", (string)JsonPath.Get(page, "collector.block"));
        }

        [Fact]
        public void CollectorListsMayBeEmptyAsLongAsTheCrateIsHandedOut()
        {
            // The content-only fallback of §8: crates on tier 3 and the final only; a comment key; a date-only since.
            string text = LiveOpsContent.WithCollector("{\"_why\":\"fewer crate objects per screen\",\"since\":\"2026-11-09\",\"dailyBonus\":[],\"dailyMission\":[],"
                                                       + "\"weeklyMilestones\":[[],[],[{\"item\":\"Crate_Event_Special\",\"quantity\":2},"
                                                       + "{\"item\":\"VersusCoins\",\"quantity\":100}]],\"weeklyFinal\":[]}");
            LiveOpsCatalog catalog = LiveOpsCatalog.Parse(text, null);
            Assert.NotNull(catalog.Collector);
            Assert.Equal(Since, catalog.Collector.Since);
            Assert.Equal(DateTimeKind.Utc, catalog.Collector.Since.Kind);
            LiveOpsCatalog without = LiveOpsCatalog.Parse(LiveOpsContent.WithCollector(null), null);

            using var f = new LiveOpsFixture(AfterSince);
            LiveOpsWindow day = LiveOpsSchedule.DayAt(Settings(), AfterSince), season = LiveOpsSchedule.SeasonAt(Settings(), AfterSince);
            // An empty daily list leaves the daily quest (and its _version) as it is, even in a crate window.
            Assert.Equal(MiniJson.Serialize(LiveOpsEvents.DailyQuest(without, day)), MiniJson.Serialize(LiveOpsEvents.DailyQuest(catalog, day, f.Runtime)));
            List<Dictionary<string, object>> a = Achievements(LiveOpsEvents.WeeklyEvent(catalog, season, null, f.Runtime));
            List<Dictionary<string, object>> p = Achievements(LiveOpsEvents.WeeklyEvent(without, season, null));
            for (int i = 0; i < a.Count - 1; i++)
            {
                Assert.Equal(Rewards(p[i], 0), Rewards(a[i], 0));
                Assert.Equal(Rewards(p[i], 1), Rewards(a[i], 1));
                Assert.Equal(Rewards(p[i], 2).Concat(new[] { "Crate_Event_Special x2", "VersusCoins x100" }), Rewards(a[i], 2));
            }
            Assert.Equal(Rewards(p.Last(), 0), Rewards(a.Last(), 0));
        }

        [Fact]
        public void LiveOpsReadNoCollectorSettingDirectly()
        {
            // The only parser of economy.collector.* is CollectorOptions: no live-ops source names a key of it.
            string dir = Path.Combine(ContentTestSupport.RepoRoot, "server", "Services", "Social", "LiveOps");
            string[] files = Directory.GetFiles(dir, "*.cs");
            Assert.NotEmpty(files);
            foreach (string file in files) Assert.DoesNotMatch("\"economy[.\"]", File.ReadAllText(file));

            // What live ops depend on comes in as CollectorRuntime.InputsKey, through the settings fingerprint.
            using var f = new LiveOpsFixture(AfterSince);
            LiveOpsSettings before = f.LiveOps.Settings;
            Assert.Equal(f.Runtime.InputsKey, before.CollectorKey);
            Assert.EndsWith("|" + f.Runtime.InputsKey, before.Fingerprint);
            Assert.Same(before, f.LiveOps.Settings);                        // parsed once per input change
            f.Context.Settings.Override("economy.collector.enabled", false);
            LiveOpsSettings after = f.LiveOps.Settings;
            Assert.NotSame(before, after);
            Assert.EndsWith("|" + f.Runtime.InputsKey, after.Fingerprint);
            Assert.NotEqual(before.Fingerprint, after.Fingerprint);
            f.Content.Hide(CollectorContent.FileName);
            Assert.NotEqual(after.Fingerprint, f.LiveOps.Settings.Fingerprint);
            // Without a collector key the fingerprint is the one of before the collector.
            Assert.Null(LiveOpsSettings.From(new Settings(), null).CollectorKey);
            Assert.DoesNotContain("collector", LiveOpsSettings.From(new Settings(), null).Fingerprint);
        }

        [Fact]
        public void ACatalogReadThatThrowsNeverReachesTheRequest()
        {
            using var f = new LiveOpsFixture(AfterSince);
            LiveOpsService live = f.LiveOps;
            string daily = LiveOpsSchedule.DailyEventId(LiveOpsSchedule.DayAt(Settings(), AfterSince));

            // Before any read: live ops are off for that request, and the next call reads again.
            f.Content.Fail(LiveOpsCatalog.ContentName, new IOException("The process cannot access the file"));
            Assert.True(live.Catalog.IsEmpty);
            Assert.Null(live.FindEvent(daily, "en", AfterSince));
            Assert.False(live.Snapshot(AfterSince).On);
            Assert.True(live.TryGetSeasonBoard(LiveOpsSchedule.BoardName(LiveOpsSchedule.SeasonAt(Settings(), AfterSince), 3), out SeasonBoardInfo _));
            Assert.Single(f.Logs.Snapshot(), e => e.Level == LogLevel.Error && e.Message.Contains("liveops.json could not be read (IOException")
                                                  && e.Message.Contains("live ops are off until it can be read"));

            f.Content.Fail(LiveOpsCatalog.ContentName, null);
            LiveOpsCatalog read = live.Catalog;
            Assert.False(read.IsEmpty);
            Dictionary<string, object> served = live.FindEvent(daily, "en", AfterSince);
            Assert.NotNull(served);

            // After a read: the last one is kept (no event changes), one warning per failing streak.
            f.Content.Fail(LiveOpsCatalog.ContentName, new UnauthorizedAccessException("denied"));
            Assert.Same(read, live.Catalog);
            Assert.Same(read, live.Catalog);
            Assert.Equal(MiniJson.Serialize(served), MiniJson.Serialize(live.FindEvent(daily, "en", AfterSince)));
            Assert.True(live.Snapshot(AfterSince).On);
            Assert.Single(f.Logs.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.Contains("keeping the last read"));
            f.Content.Fail(LiveOpsCatalog.ContentName, null);
            Assert.Same(read, live.Catalog);                                // same text: not parsed again
            f.Content.Fail(LiveOpsCatalog.ContentName, new IOException("again"));
            Assert.Same(read, live.Catalog);
            Assert.Equal(2, f.Logs.Snapshot().Count(e => e.Level == LogLevel.Warn && e.Message.Contains("keeping the last read")));
        }

        [Fact]
        public void TheSnapshotAndTheDiagnosticsPageCarryTheCollector()
        {
            var clock = new ManualClock(Wednesday);
            using var s = new MiscServer(clock);
            LiveOpsService live = LiveOpsService.For(s.Context);
            LiveOpsSnapshot snap = live.Snapshot(clock.UtcNow);
            Assert.True(snap.On);
            Assert.Equal(Since, snap.CollectorSince);
            var page = MiscServer.Obj(s.Get("/_mcvs/liveops"));
            Assert.Equal("Dormant", JsonPath.Get(page, "collector.phase"));
            Assert.Equal("2026-11-09T00:00:00Z", JsonPath.Get(page, "collector.since"));
            Assert.Empty((List<object>)JsonPath.Get(page, "collector.activeFor"));
            Assert.Null(JsonPath.Get(page, "collector.block"));
            Assert.Null(JsonPath.Get(page, "collector.cratesServed"));

            // The last day before since: tomorrow's quest day and the announced next season get the crate, today's do not;
            // the TS fetches both today, so the document carries the guard (guardSince = since − 1 day, §2.12).
            clock.UtcNow = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
            page = MiscServer.Obj(s.Get("/_mcvs/liveops"));
            Assert.Equal("Guard", JsonPath.Get(page, "collector.phase"));
            Assert.Equal("before collector.since 2026-11-09T00:00:00Z", JsonPath.Get(page, "collector.reason"));
            Assert.Equal(new object[] { "mcvs_daily_2026110900", "mcvs_weekly_2026110900x7" }, (List<object>)JsonPath.Get(page, "collector.activeFor"));
            Assert.Null(JsonPath.Get(page, "collector.cratesServed"));      // the page builds the events, it does not serve them
            Assert.Contains("Crate_Event_Special", s.Get("/events/mcvs_daily_2026110900?language=en").BodyText);
            Assert.Equal("2026-11-08T12:00:00Z", JsonPath.Get(MiscServer.Obj(s.Get("/_mcvs/liveops")), "collector.cratesServed"));

            clock.UtcNow = AfterSince;
            page = MiscServer.Obj(s.Get("/_mcvs/liveops"));
            Assert.Equal("Active", JsonPath.Get(page, "collector.phase"));
            Assert.Equal("active since 2026-11-09T00:00:00Z", JsonPath.Get(page, "collector.reason"));
            Assert.Equal(new object[] { "mcvs_daily_2026111100", "mcvs_daily_2026111200", "mcvs_weekly_2026110900x7" },
                         (List<object>)JsonPath.Get(page, "collector.activeFor"));
            var weekly = ((List<object>)page["achievementEvents"]).Cast<Dictionary<string, object>>().Single(e => (string)e["id"] == "mcvs_weekly_2026110900x7");
            Assert.Equal(MiniJson.ParseObject(s.Get("/events/mcvs_weekly_2026110900x7?language=en").BodyText)["_version"], weekly["_version"]);

            s.Set("economy.collector.enabled", false);
            page = MiscServer.Obj(s.Get("/_mcvs/liveops"));
            Assert.Equal("Guard", JsonPath.Get(page, "collector.phase"));
            Assert.Equal("economy.collector.enabled is false", JsonPath.Get(page, "collector.reason"));
            Assert.Empty((List<object>)JsonPath.Get(page, "collector.activeFor"));

            // Live ops off: no events, but the snapshot still carries since (the guard starts there either way).
            s.Set("economy.collector.enabled", true);
            s.Set("liveops.enabled", false);
            snap = live.Snapshot(clock.UtcNow);
            Assert.False(snap.On);
            Assert.Equal(Since, snap.CollectorSince);
            page = MiscServer.Obj(s.Get("/_mcvs/liveops"));
            Assert.Equal("Guard", JsonPath.Get(page, "collector.phase"));
            Assert.Equal("live ops are off", JsonPath.Get(page, "collector.reason"));
        }

        /// <summary>
        /// The live-ops events of every window from two weeks before the shipped since to five weeks after it (quest days
        /// at reset hours 0 and 6, seasons of 7 and 1 days, every modifier) hold at most one crate per milestone (§3.1:
        /// ClaimAchievementMilestoneRequest reveals only the last crate of a milestone).
        /// </summary>
        [Fact]
        public void NoGeneratedMilestoneHoldsTwoCrates()
        {
            using var f = new LiveOpsFixture(AfterSince);
            LiveOpsCatalog catalog = f.LiveOps.Catalog;
            Assert.NotNull(catalog.Collector);
            int events = 0, withCrate = 0;
            foreach (int resetHour in new[] { 0, 6 })
                foreach (int days in new[] { 7, 1 })
                {
                    LiveOpsSettings s = Settings(days, resetHour);
                    for (DateTime t = Since.AddDays(-14); t < Since.AddDays(35); t = t.AddHours(12))
                    {
                        var served = new List<Dictionary<string, object>> { LiveOpsEvents.DailyQuest(catalog, LiveOpsSchedule.DayAt(s, t), f.Runtime) };
                        foreach (LiveOpsModifier m in catalog.Modifiers)
                            served.Add(LiveOpsEvents.WeeklyEvent(catalog, LiveOpsSchedule.SeasonAt(s, t), m, f.Runtime));
                        foreach (Dictionary<string, object> e in served)
                        {
                            AssertOneCratePerMilestone(e);
                            events++;
                            if (((string)e["_template"]).Contains(CollectorGuard.CrateId)) withCrate++;
                        }
                    }
                }
            Assert.True(withCrate > events / 2, events + " events, " + withCrate + " with the Cosmetic crate");
        }

        /// <summary>Every milestone of the event holds at most one crate reward (anything but a plain currency).</summary>
        private static void AssertOneCratePerMilestone(Dictionary<string, object> e)
        {
            foreach (Dictionary<string, object> achievement in Achievements(e))
                for (int m = 0; m < ((List<object>)achievement["milestones"]).Count; m++)
                {
                    List<string> crates = Rewards(achievement, m).Where(r => LiveOpsCatalog.IsCrate(r.Substring(0, r.LastIndexOf(" x", StringComparison.Ordinal)))).ToList();
                    Assert.True(crates.Count <= 1, e["id"] + " " + achievement["primary_tracker"] + " milestone " + m + ": " + string.Join(", ", crates));
                }
        }

        // ------------------------------------------------------------------------------------------ helpers

        private static LiveOpsWindow WindowOf(GoldenOutput o)
        {
            return o.Kind == GoldenScenarios.Daily ? LiveOpsSchedule.TryParseDaily(o.EventId) : LiveOpsSchedule.TryParseWeekly(o.EventId);
        }

        private static Dictionary<string, object> Milestone(Dictionary<string, object> achievement, int index)
        {
            return (Dictionary<string, object>)((List<object>)achievement["milestones"])[index];
        }

        /// <summary>"item xN" of each reward of a milestone, in order.</summary>
        private static List<string> Rewards(Dictionary<string, object> achievement, int milestone)
        {
            return ((List<object>)Milestone(achievement, milestone)["rewards"]).Cast<Dictionary<string, object>>()
                                                                             .Select(r => (string)r["item"] + " x" + (long)r["quantity"]).ToList();
        }

        /// <summary>Writes <c>&lt;data&gt;/content/&lt;name&gt;</c> of a test server (wins over the built-in file).</summary>
        internal static void OverrideContent(MiscServer s, string name, string text)
        {
            string dir = Path.Combine(s.Context.DataDir, "content");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), text, new UTF8Encoding(false));
        }

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

    /// <summary>The shipped liveops.json and variants of its <c>collector</c> block (economy-collector.md §3.2).</summary>
    internal static class LiveOpsContent
    {
        /// <summary>The built-in server/Content/liveops.json.</summary>
        public static string Shipped
        {
            get
            {
                Assert.True(new ContentProvider(null, null, null).TryReadContentText(LiveOpsCatalog.ContentName, out string text));
                return text;
            }
        }

        /// <summary>The shipped file with its <c>collector</c> block replaced by <paramref name="blockJson"/> (any JSON
        /// value), or removed when it is null.</summary>
        public static string WithCollector(string blockJson)
        {
            Dictionary<string, object> root = MiniJson.ParseObject(Shipped);
            root.Remove("collector");
            if (blockJson != null)
            {
                Assert.True(MiniJson.TryParse(blockJson, out object block), blockJson);
                root["collector"] = block;
            }
            return MiniJson.Serialize(root);
        }

        /// <summary>The shipped file with <c>collector.since</c> moved to <paramref name="since"/>.</summary>
        public static string WithSince(DateTime since)
        {
            Dictionary<string, object> root = MiniJson.ParseObject(Shipped);
            ((Dictionary<string, object>)root["collector"])["since"] = TimeFormat.Iso8601(since);
            return MiniJson.Serialize(root);
        }
    }

    /// <summary>
    /// The content a test server reads: the built-in files (or another provider's), some of them replaced, hidden
    /// (missing) or failing the way ContentProvider fails when the &lt;data&gt;/content override changes under it.
    /// </summary>
    internal sealed class TestContent : IContentProvider
    {
        private readonly IContentProvider inner;
        private readonly object gate = new object();
        private readonly Dictionary<string, string> replaced = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> failing = new Dictionary<string, Exception>(StringComparer.Ordinal);

        public TestContent(IContentProvider inner = null)
        {
            this.inner = inner ?? new ContentProvider(null, null, null);
        }

        /// <summary>Puts a TestContent over <paramref name="context"/>'s own content (call before the first request).</summary>
        public static TestContent Install(ServerContext context)
        {
            var content = new TestContent(context.Content);
            typeof(ServerContext).GetProperty(nameof(ServerContext.Content)).SetValue(context, content);
            Assert.Same(content, context.Content);
            return content;
        }

        /// <summary>Serves <paramref name="text"/> as <paramref name="name"/>; null: the file is missing.</summary>
        public TestContent Replace(string name, string text)
        {
            lock (gate) replaced[name] = text;
            return this;
        }

        public TestContent Hide(string name)
        {
            return Replace(name, null);
        }

        /// <summary>Reads of <paramref name="name"/> throw <paramref name="failure"/> (null: they work again).</summary>
        public TestContent Fail(string name, Exception failure)
        {
            lock (gate)
            {
                if (failure == null) failing.Remove(name);
                else failing[name] = failure;
            }
            return this;
        }

        public DataPack DataPack => inner.DataPack;

        public List<string> ContentNames => inner.ContentNames;

        public bool TryReadGameFile(string logicalPath, out byte[] data) => inner.TryReadGameFile(logicalPath, out data);

        public bool TryReadGameText(string logicalPath, out string text) => inner.TryReadGameText(logicalPath, out text);

        public bool TryReadContent(string name, out byte[] data)
        {
            if (Intercepted(name, out string text))
            {
                data = text == null ? null : Encoding.UTF8.GetBytes(text);
                return data != null;
            }
            return inner.TryReadContent(name, out data);
        }

        public bool TryReadContentText(string name, out string text)
        {
            if (Intercepted(name, out text)) return text != null;
            return inner.TryReadContentText(name, out text);
        }

        private bool Intercepted(string name, out string text)
        {
            lock (gate)
            {
                if (failing.TryGetValue(name, out Exception failure)) throw failure;
                return replaced.TryGetValue(name, out text);
            }
        }
    }

    /// <summary>A bare server context (no modules, no data pack) over <see cref="TestContent"/>, with its live ops and collector.</summary>
    internal sealed class LiveOpsFixture : IDisposable
    {
        private readonly TempDir dir = new TempDir();

        public LiveOpsFixture(DateTime utc)
        {
            var logger = new Logger();
            logger.AddSink(Logs);
            Clock = new ManualClock(utc);
            Context = new ServerContext(dir.Path, new Settings(), logger, Clock, Content);
        }

        public TestContent Content { get; } = new TestContent();

        public MemoryLogSink Logs { get; } = new MemoryLogSink();

        public ManualClock Clock { get; }

        public ServerContext Context { get; }

        public LiveOpsService LiveOps => LiveOpsService.For(Context);

        public CollectorRuntime Runtime => CollectorRuntime.For(Context);

        public void Dispose()
        {
            dir.Dispose();
        }
    }
}
