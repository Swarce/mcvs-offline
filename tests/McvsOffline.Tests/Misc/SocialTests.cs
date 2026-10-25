using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Social;
using Xunit;

namespace McvsOffline.Tests.Misc
{
    /// <summary>Osiris events, social profile, connections and requests (report 02 §4.4-4.5).</summary>
    public class SocialTests
    {
        private const string EventsJson = @"[
  {""id"":""crates_1"",""name"":""Crates"",""start_date"":""2020-01-01T00:00:00Z"",""end_date"":""2099-01-01T00:00:00Z"",
   ""_version"":3,""_priority"":2,""visual"":null,
   ""_template"":{""event_tuning"":{""event_type"":{""private"":""1"",""value"":""ShopCrateV2Event""}}}},
  {""id"":""season_1"",""name"":""Season"",""start_date"":""2026-10-19T00:00:00Z"",""end_date"":""2026-10-26T00:00:00Z"",
   ""_template"":""{\""event_tuning\"":{}}"",""_batch_tag"":""S1"",""_participation"":""(CurrentDivision == 3)"",
   ""tournament"":{""leaderboard"":{""name"":""Leaderboard_S1_D3"",""order"":""desc""},""type"":""award_by_rank"",""awards"":[]}},
  {""id"":""broken"",""name"":""No template""},
  {""id"":""bad_version"",""_template"":""{}"",""_version"":""v2""}
]";

        private static void WriteContent(MiscServer s, string name, string text)
        {
            string path = Path.Combine(s.Context.DataDir, "content", name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        [Fact]
        public void EventsAnswerAnArrayInRequestOrderWithoutUnknownIds()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("alice");
            // No content: a well-formed empty array (TimeLimitedEventsComponent iterates it).
            Assert.Equal("[]", s.Get("/events?event_ids=a,b&language=en", p.OnBehalfToken).BodyText);

            WriteContent(s, SocialEvents.ContentName, EventsJson);
            var list = MiscServer.Arr(s.Get("/events?event_ids=season_1,unknown,crates_1,broken&language=en", p.OnBehalfToken));
            Assert.Equal(2, list.Count);
            var season = (Dictionary<string, object>)list[0];
            var crates = (Dictionary<string, object>)list[1];
            Assert.Equal("season_1", season["id"]);
            Assert.Equal("S1", season["_batch_tag"]);
            Assert.Equal("Leaderboard_S1_D3", JsonPath.Get(season, "tournament.leaderboard.name"));

            // _template must be a string for item.Fields["_template"].Value<string>(); _version an integer string.
            Assert.IsType<string>(crates["_template"]);
            Assert.Equal("ShopCrateV2Event", JsonPath.Get(MiniJson.ParseObject((string)crates["_template"]), "event_tuning.event_type.value"));
            Assert.Equal("3", crates["_version"]);
            Assert.Equal("", crates["visual"]); // nulls would NRE in DownloadableAsset(string).Trim()
        }

        [Fact]
        public void SingleEventAndNormalizationRules()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("bob");
            WriteContent(s, SocialEvents.ContentName, EventsJson);
            Assert.Equal("season_1", MiscServer.Obj(s.Get("/events/season_1?language=en", p.OnBehalfToken))["id"]);
            Assert.Equal(404, s.Get("/events/broken", p.OnBehalfToken).StatusCode);
            Assert.Equal(404, s.Get("/events/none", p.OnBehalfToken).StatusCode);
            var bad = MiscServer.Obj(s.Get("/events/bad_version", p.OnBehalfToken));
            Assert.False(bad.ContainsKey("_version"));
            Assert.Equal("", bad["name"]);
        }

        [Fact]
        public void AnEventProviderFeatureWinsOverTheContentFile()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("carol");
            WriteContent(s, SocialEvents.ContentName, EventsJson);
            s.Context.Features.Set<ISocialEventProvider>(new FixedProvider());
            var list = MiscServer.Arr(s.Get("/events?event_ids=season_1,rolling", p.OnBehalfToken));
            Assert.Equal(2, list.Count);
            Assert.Equal("from provider", ((Dictionary<string, object>)list[0])["name"]);
            Assert.Equal("rolling", ((Dictionary<string, object>)list[1])["id"]);
        }

        [Fact]
        public void EventParticipationCommandsSucceed()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("dave");
            Assert.Equal(200, s.Post("/events/e1/participants/me", p.OnBehalfToken).StatusCode);
            Assert.Equal(200, s.Post("/events/e1/participants/me/award", p.OnBehalfToken).StatusCode);
            Assert.Equal(200, s.Delete("/events/e1/participants/me", p.OnBehalfToken).StatusCode);
        }

        [Fact]
        public void SocialProfileKeepsTheNameAndListsClanGroups()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("erin");
            var before = MiscServer.Obj(s.Get("/accounts/" + Uri.EscapeDataString(p.Credential), p.OnBehalfToken));
            Assert.Equal(p.FedId, before["credential"]);
            Assert.Equal("", before["name"]);
            Assert.Empty((List<object>)before["groups"]);

            Assert.Equal(200, s.Post("/accounts/me", p.OnBehalfToken, "name", "Erin", "language", "en", "country", "US").StatusCode);
            Assert.Equal(200, s.Post("/accounts/me/status_line", p.OnBehalfToken, "status_line", "online").StatusCode);
            string clanId = (string)MiscServer.Obj(s.Post("/groups", p.OnBehalfToken, "name", "Alpha", "category", "Clan"))["id"];

            var after = MiscServer.Obj(s.Get("/accounts/" + Uri.EscapeDataString(p.FedId), p.Token));
            Assert.Equal("Erin", after["name"]);
            Assert.Equal("online", after["status_line"]);
            Assert.Equal(clanId, Assert.Single((List<object>)after["groups"]));
            Assert.Equal("Erin", MiscServer.Obj(s.Get("/accounts/me", p.Token))["name"]);
            Assert.Equal(404, s.Get("/accounts/garbage", p.Token).StatusCode);
        }

        [Fact]
        public void ConnectionsAndRequestsAreEmptyListsAndCommandsSucceed()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("frank");
            Assert.Equal("[]", s.Get("/accounts/me/connections/friend?game=mygame", p.Token).BodyText);
            Assert.Equal("[]", s.Get("/accounts/me/requests?request_type=connection_approval&limit=50", p.Token).BodyText);
            Assert.Equal("[]", s.Get("/accounts/me/requests/sent?limit=10", p.Token).BodyText);
            Assert.Equal(0L, MiscServer.Obj(s.Get("/accounts/me/connections/friend/count?game=mygame", p.Token))["count"]);
            Assert.Equal(200, s.Post("/accounts/me/connections/friend", p.Token, "target_credential", "fed_id:abc", "required_approval", "True", "alert_kairos", "True").StatusCode);
            Assert.Equal(200, s.Post("/accounts/me/connections/friend/fed_id:abc/delete", p.Token).StatusCode);
            Assert.Equal(200, s.Post("/accounts/me/requests/r1/accept", p.Token).StatusCode);
            Assert.Equal(200, s.Post("/accounts/me/requests/sent/r1/cancel", p.Token).StatusCode);
            Assert.Equal(200, s.Post("/accounts/me/connections/friend/import", p.Token, "from_credential", "facebook:x", "secret", "y").StatusCode);
        }

        private sealed class FixedProvider : ISocialEventProvider
        {
            public Dictionary<string, object> FindEvent(string eventId, string language, DateTime utcNow)
            {
                if (eventId != "season_1" && eventId != "rolling") return null;
                return new Dictionary<string, object>
                {
                    ["id"] = eventId,
                    ["name"] = "from provider",
                    ["start_date"] = "2026-10-19T00:00:00Z",
                    ["end_date"] = "2026-10-26T00:00:00Z",
                    ["_template"] = "{}",
                };
            }
        }
    }
}
