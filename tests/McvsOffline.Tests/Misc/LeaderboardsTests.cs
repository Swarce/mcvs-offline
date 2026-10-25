using System;
using System.Collections.Generic;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Leaderboards;
using Xunit;

namespace McvsOffline.Tests.Misc
{
    /// <summary>Olympus leaderboards and the division-season ranking (report 02 §4.9, report 04 §9.2).</summary>
    public class LeaderboardsTests
    {
        private const string Season = "/leaderboards/desc/Leaderboard_Season42_Div3";

        private static Dictionary<string, object> Self(MiscServer s, TestPlayer p, string board = Season)
        {
            // Leaderboard.SelfData: GET leaderboards/{sort}/{name}?offset=0&limit=0
            return MiscServer.Obj(s.Get(board + "?offset=0&limit=0", p.OnBehalfToken));
        }

        private static void PostScore(MiscServer s, TestPlayer p, int score, string board = Season, params string[] extra)
        {
            var form = new List<string> { "score", score.ToString(), "display_name", "Player" + p.AccountId.Substring(0, 4),
                                          "_level", "12", "_avatar", "Avatar_01", "_leagueId", "3", "_fed_id", p.FedId };
            form.AddRange(extra);
            Assert.Equal(200, s.Post(board + "/me", p.OnBehalfToken, form.ToArray()).StatusCode);
        }

        [Fact]
        public void PlayerWithoutAScoreHasAnEmptyMyEntrySoTheClientPostsOne()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("alice");
            var self = Self(s, p);
            Assert.Empty((Dictionary<string, object>)self["my_entry"]);
            Assert.Empty((List<object>)self["data"]);
            Assert.Equal(100L, self["total_entries"]); // a full group of rivals
        }

        [Fact]
        public void BoardsAreCompletedToALeagueGroupOf100SoPromotionIsPossible()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("bob");
            PostScore(s, p, 1000); // well above every rival (topScore 400)
            var self = Self(s, p);
            var mine = (Dictionary<string, object>)self["my_entry"];
            Assert.Equal(1L, mine["rank"]);
            Assert.Equal(100L, self["total_entries"]);
            Assert.Equal(p.FedId, mine["credential"]);
            Assert.Equal(1000L, mine["score"]);
            Assert.Equal("12", mine["_level"]);
            Assert.Equal(p.FedId, mine["_fed_id"]);
            // limit=0 also lists the player's own entry (Leaderboard.SelfEntry reads data[0]).
            Assert.Equal(p.FedId, ((Dictionary<string, object>)Assert.Single((List<object>)self["data"]))["credential"]);

            // TieredDivision.GetStatusForRank with a 40% promotion zone and 10% demotion zone:
            long rank = (long)mine["rank"], total = (long)self["total_entries"];
            Assert.True(rank <= Math.Floor(40 * total / 100.0));
        }

        [Fact]
        public void LowAndZeroScoresRankLowAndTheRankFollowsTheScore()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("carol");
            PostScore(s, p, 0);
            var zero = (Dictionary<string, object>)Self(s, p)["my_entry"];
            Assert.True((long)zero["rank"] >= 91, "a player with no trophies falls into the demotion zone (rank " + zero["rank"] + ")");

            PostScore(s, p, 200); // ~50% of topScore: mid-table
            long mid = (long)((Dictionary<string, object>)Self(s, p)["my_entry"])["rank"];
            Assert.InRange(mid, 15, 60);

            PostScore(s, p, 380);
            long high = (long)((Dictionary<string, object>)Self(s, p)["my_entry"])["rank"];
            Assert.True(high < mid);
        }

        [Fact]
        public void TopListsRanksInOrderWithRivalsAndTheSelfRowMatchesMyEntry()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("dave");
            PostScore(s, p, 150);
            var top = MiscServer.Obj(s.Get(Season + "?offset=0&limit=100", p.OnBehalfToken));
            var data = (List<object>)top["data"];
            Assert.Equal(100, data.Count);
            long previousScore = long.MaxValue;
            for (int i = 0; i < data.Count; i++)
            {
                var e = (Dictionary<string, object>)data[i];
                Assert.Equal((long)(i + 1), e["rank"]);
                Assert.Equal((long)i, e["index"]);
                Assert.True((long)e["score"] <= previousScore);
                previousScore = (long)e["score"];
                Assert.False(string.IsNullOrEmpty((string)e["display_name"]));
                Assert.StartsWith("fed_id:", (string)e["credential"]);
            }
            var mine = (Dictionary<string, object>)top["my_entry"];
            // GM/LeaderboardsComponent.cs GetEntriesObject finds the player's row by comparing credentials.
            Assert.Contains(data, e => (string)((Dictionary<string, object>)e)["credential"] == (string)mine["credential"]);
        }

        [Fact]
        public void RivalsAreDeterministicPerBoard()
        {
            var a = VirtualPlayers.Generate("desc/board", 10, 400);
            var b = VirtualPlayers.Generate("desc/board", 10, 400);
            var c = VirtualPlayers.Generate("desc/other", 10, 400);
            Assert.Equal(MiniJson.Serialize(a), MiniJson.Serialize(b));
            Assert.NotEqual(MiniJson.Serialize(a), MiniJson.Serialize(c));
            for (int i = 1; i < a.Count; i++) Assert.True((long)a[i - 1]["score"] >= (long)a[i]["score"]);
        }

        [Fact]
        public void AroundMeShowsAWindowContainingThePlayer()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("erin");
            PostScore(s, p, 120);
            // Leaderboard.AroundMe(0, DefaultAroundMeSize = 4)
            var around = MiscServer.Obj(s.Get(Season + "/me?offset=0&limit=4", p.OnBehalfToken));
            var data = (List<object>)around["data"];
            Assert.Equal(4, data.Count);
            Assert.Contains(data, e => (string)((Dictionary<string, object>)e)["credential"] == p.FedId);
            var local = MiscServer.Obj(s.Get(Season + "/me/location?offset=0&limit=4&around_me=True", p.OnBehalfToken));
            Assert.Contains((List<object>)local["data"], e => (string)((Dictionary<string, object>)e)["credential"] == p.FedId);
            var friends = MiscServer.Obj(s.Get(Season + "/me/friends?offset=0&limit=100&connection_type=friend", p.OnBehalfToken));
            Assert.Equal(1L, friends["total_entries"]);
            Assert.Equal(1L, ((Dictionary<string, object>)friends["my_entry"])["rank"]);
        }

        [Fact]
        public void ReplaceScoreIfHigherKeepsTheBestScore()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("frank");
            PostScore(s, p, 300, Season, "replace_score_if", "higher");
            PostScore(s, p, 100, Season, "replace_score_if", "higher");
            Assert.Equal(300L, ((Dictionary<string, object>)Self(s, p)["my_entry"])["score"]);
            PostScore(s, p, 100); // always (the game's divisions post)
            Assert.Equal(100L, ((Dictionary<string, object>)Self(s, p)["my_entry"])["score"]);
        }

        [Fact]
        public void GlobalAndAscendingBoardsHaveNoRivalsAndRivalsCanBeTurnedOff()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("gina");
            // GM/GameServerComponent.cs RegisterUser: leaderboard "global", plain custom keys.
            PostScore(s, p, 1761400000, "/leaderboards/desc/global", "fed_id", p.FedId, "language", "en", "last_purchase", "");
            var global = Self(s, p, "/leaderboards/desc/global");
            Assert.Equal(1L, global["total_entries"]);
            Assert.Equal("en", ((Dictionary<string, object>)global["my_entry"])["language"]);

            PostScore(s, p, 50, "/leaderboards/asc/fastest");
            Assert.Equal(1L, Self(s, p, "/leaderboards/asc/fastest")["total_entries"]);

            s.Set("leaderboards.virtualPlayers", false);
            Assert.Equal(0L, Self(s, p)["total_entries"]);
            PostScore(s, p, 5);
            Assert.Equal(1L, Self(s, p)["total_entries"]);
        }

        [Fact]
        public void LeagueGroupSizeComesFromTheBoardSettings()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("hank");
            Assert.Equal(200, s.Post(Season + "/settings", p.OnBehalfToken, "type", "league", "group_size", "50", "expiration_duration", "604800").StatusCode);
            PostScore(s, p, 10);
            Assert.Equal(50L, Self(s, p)["total_entries"]);
            var settings = MiscServer.Obj(s.Get(Season + "/settings", p.OnBehalfToken));
            Assert.Equal("league", settings["container_type"]);
            Assert.Equal(50L, settings["group_size"]);
        }

        [Fact]
        public void EntriesCanBeDeletedAndExpire()
        {
            var clock = new McvsOffline.Server.Core.ManualClock(new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc));
            using var s = new MiscServer(clock);
            TestPlayer p = s.NewPlayer("ivy");
            PostScore(s, p, 10);
            Assert.Equal(200, s.Post(Season + "/me/delete", p.OnBehalfToken).StatusCode);
            Assert.Empty((Dictionary<string, object>)Self(s, p)["my_entry"]);

            PostScore(s, p, 10, Season, "expiration_duration", "60");
            Assert.NotEmpty((Dictionary<string, object>)Self(s, p)["my_entry"]);
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Empty((Dictionary<string, object>)Self(s, p)["my_entry"]);

            PostScore(s, p, 10);
            Assert.Equal(200, s.Delete(Season + "/" + Uri.EscapeDataString(p.FedId), p.OnBehalfToken).StatusCode);
            Assert.Empty((Dictionary<string, object>)Self(s, p)["my_entry"]);
        }

        [Fact]
        public void ArbitraryEntriesAndBadScores()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("jack");
            Assert.Equal(200, s.Post(Season + "/team_alpha", s.GameServer.Token, "credential", "team_alpha", "score", "77", "display_name", "Alpha").StatusCode);
            var entry = MiscServer.Obj(s.Get(Season + "/team_alpha", s.GameServer.Token));
            Assert.Equal("team_alpha", ((Dictionary<string, object>)entry["my_entry"])["credential"]);
            Assert.Equal(400, s.Post(Season + "/me", p.OnBehalfToken, "score", "lots").StatusCode);
            Assert.Equal(401, s.Post(Season + "/me", null, "score", "1").StatusCode);
        }
    }
}
