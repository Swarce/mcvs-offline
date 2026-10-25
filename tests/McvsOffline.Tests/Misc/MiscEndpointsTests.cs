using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Misc;
using McvsOffline.Server.Services.Profanity;
using Xunit;

namespace McvsOffline.Tests.Misc
{
    /// <summary>Profanity, geolocation, game stats, BanTool and the catch-all module.</summary>
    public class MiscEndpointsTests
    {
        [Fact]
        public void ProfanityFilterPassesNamesThroughByDefault()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("alice");
            // GM/PlayerProfileComponent.cs: the player name goes through chat/words/{lang}/filter on the TS.
            var r = s.Post("/chat/words/en/filter", p.OnBehalfToken, "message", "xX_Sn1per_Xx");
            Assert.Equal(200, r.StatusCode);
            var body = MiscServer.Obj(r);
            Assert.Equal(false, body["matches"]);
            Assert.Equal("xX_Sn1per_Xx", body["result"]);
            Assert.Equal("Bob", MiscServer.Obj(s.Post("/chat/words/filter", p.OnBehalfToken, "message", "Bob"))["result"]);
            // Even without a token the name check must not fail.
            Assert.Equal(200, s.Post("/chat/words/fr/filter", null, "message", "Jean").StatusCode);
        }

        [Fact]
        public void BlacklistedWordsAreMaskedAndWhitelistWins()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("bob");
            s.Set("profanity.blacklist", new List<object> { "darn" });
            var r = MiscServer.Obj(s.Post("/chat/words/en/filter", p.Token, "message", "Darn it, darnation"));
            Assert.Equal(true, r["matches"]);
            Assert.Equal("**** it, darnation", r["result"]);

            Assert.Equal(200, s.Post("/chat/words/en/heck", p.Token, "bad", "True").StatusCode);
            Assert.Equal("****", MiscServer.Obj(s.Post("/chat/words/en/filter", p.Token, "message", "heck"))["result"]);
            Assert.Equal("heck", MiscServer.Obj(s.Post("/chat/words/de/filter", p.Token, "message", "heck"))["result"]);
            Assert.Equal(200, s.Post("/chat/words/en/darn", p.Token, "bad", "False").StatusCode);
            Assert.Equal("darn", MiscServer.Obj(s.Post("/chat/words/en/filter", p.Token, "message", "darn"))["result"]);
            Assert.Equal(200, s.Post("/chat/words/en/heck/delete", p.Token).StatusCode);
            Assert.Equal("heck", MiscServer.Obj(s.Post("/chat/words/en/filter", p.Token, "message", "heck"))["result"]);

            // OP/Profanity.cs: every language object has blacklisted/whitelisted arrays.
            var words = MiscServer.Obj(s.Get("/chat/words", p.Token));
            Assert.IsType<List<object>>(JsonPath.Get(words, "all.blacklisted"));
            Assert.IsType<List<object>>(JsonPath.Get(words, "en.whitelisted"));

            bool matches;
            Assert.Equal("a ** b", ProfanityService.Censor("a xy b", new HashSet<string> { "xy" }, null, out matches));
            Assert.True(matches);
        }

        [Fact]
        public void GeolocationAnswersTheShapeTheTsHandshakeReads()
        {
            using var s = new MiscServer();
            // UT/Geolocalization.cs GetRegion: Json.LoadObject(body)["country_info"]["iso2"]
            var r = s.Get("/location/?ip=127.0.0.1");
            Assert.Equal(200, r.StatusCode);
            Assert.Equal("US", JsonPath.Get(MiscServer.Obj(r), "country_info.iso2"));
            Assert.Equal("127.0.0.1", MiscServer.Obj(r)["ip"]);
            s.Set("geo.country", "fr");
            Assert.Equal("FR", JsonPath.Get(MiscServer.Obj(s.Get("/location?ip=10.0.0.2")), "country_info.iso2"));
            s.Set("geo.country", "nonsense");
            Assert.Equal("US", JsonPath.Get(MiscServer.Obj(s.Get("/location/10.0.0.2")), "country_info.iso2"));
            Assert.Equal(200, s.Get("/dump/dump.edgelist").StatusCode);
        }

        [Fact]
        public void GameStatsQueriesAreEmptyArraysAndEventsAreAccepted()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("carol");
            Assert.Equal("[]", s.Get("/gamestats/users/queries/game_stats_by_time?category=mm&start=1&end=2", p.OnBehalfToken).BodyText);
            Assert.Equal("[]", s.Get("/gamestats/users/queries/default?category=mm&group_keys=a,b", p.OnBehalfToken).BodyText);
            Assert.Equal(200, s.Post("/gamestats/users/me/events/default", p.OnBehalfToken, "category", "mm", "keys", "100,slot", "aggregates", "0,30,100").StatusCode);
        }

        [Fact]
        public void BanToolNeverBansAnyone()
        {
            using var s = new MiscServer();
            Assert.Equal("[]", s.Get("/api/action").BodyText);
            Assert.Equal("[]", s.Get("/api/reason").BodyText);
            Assert.Equal("[]", s.Get("/api/asset").BodyText);
            Assert.Empty((List<object>)MiscServer.Obj(s.Get("/api/campaign?page=1"))["campaigns"]);
            var create = s.Server.Handle(HttpRequest.Create("POST", "/api/campaign", "{\"comment\":\"x\",\"credentials\":[\"fed_id:a\"]}", "application/json"));
            Assert.Equal(200, create.StatusCode);
            Assert.Empty((List<object>)MiscServer.Obj(create)["accounts"]);
        }

        [Fact]
        public void TrackingMarketingAndPortalAreAcknowledged()
        {
            using var s = new MiscServer();
            foreach (string path in new[] { "/", "/ets", "/etsv2", "/tracking/v2" })
            {
                var r = s.Server.Handle(HttpRequest.Create("POST", path, "{\"ggi\":66102,\"events\":[]}", "application/json"));
                Assert.Equal(200, r.StatusCode);
                Assert.StartsWith("misc.tracking", RouteOf(s, "POST", path));
            }
            s.Set("tracking.disable", true);
            Assert.Equal(993, s.Server.Handle(HttpRequest.Create("POST", "/ets", "{}", "application/json")).StatusCode);

            var hit = s.Get("/redir/iphoneloading.php?from=MCVS&op=launch");
            Assert.Equal(200, hit.StatusCode);
            Assert.Contains("text/html", hit.ContentType);
            Assert.Contains("Not available offline", hit.BodyText);
            Assert.Equal("{}", s.Post("/ios/1916/public/gamePortal/gamePortal.wsgi", null, "op_code", "1").BodyText);
            Assert.Equal("{}", s.Post("/api/index.php", null, "report_type", "1").BodyText);
        }

        [Fact]
        public void UnknownListShapedGetsAnswerAnArrayAndAreStillLoggedAsUnknown()
        {
            using var s = new MiscServer();
            var list = s.Get("/somewhere/else/members?limit=3");
            Assert.Equal("[]", list.BodyText);
            var obj = s.Get("/somewhere/else/thing");
            Assert.Equal("{}", obj.BodyText);
            Assert.Equal("{}", s.Post("/somewhere/members", null, "a", "b").BodyText); // only GETs list
            Assert.True(s.Logs.Contains("/somewhere/else/members"));
            string unknown = File.ReadAllText(Path.Combine(s.Context.DataDir, "logs", "unknown-endpoints.jsonl"));
            Assert.Contains("/somewhere/else/members", unknown);
            Assert.Contains("/somewhere/else/thing", unknown);

            Assert.True(MiscService.LooksLikeList(HttpRequest.Create("GET", "/users?device_id=abc")));
            Assert.True(MiscService.LooksLikeList(HttpRequest.Create("GET", "/messages/x/me")));
            Assert.False(MiscService.LooksLikeList(HttpRequest.Create("GET", "/users/me")));
        }

        /// <summary>
        /// The MISC routes must not take paths that belong to other modules (which are stubs in this module's branch, so
        /// those requests must still reach the unknown-endpoint fallback here).
        /// </summary>
        [Theory]
        [InlineData("GET", "/config/2620:66102:1.17.32:android:googleplay/datacenters")]
        [InlineData("GET", "/locate/auth?client_id=x")]
        [InlineData("POST", "/authorize")]
        [InlineData("GET", "/users/me")]
        [InlineData("GET", "/users?device_id=x")]
        [InlineData("POST", "/games/mygame/alias")]
        [InlineData("GET", "/encrypt_token?nonce=1")]
        [InlineData("GET", "/verify?access_token=x")]
        [InlineData("GET", "/profiles/me/myprofile")]
        [InlineData("POST", "/profiles/me/myprofile")]
        [InlineData("GET", "/data/me/x")]
        [InlineData("GET", "/configs/users/me/game")]
        [InlineData("GET", "/lobbyconfigs/x/ts")]
        [InlineData("POST", "/rooms/x/quick_launch")]
        [InlineData("GET", "/assets/x/name")]
        [InlineData("GET", "/coupons/x/CODE")]
        public void OtherModulesPathsAreLeftAlone(string method, string path)
        {
            using var s = new MiscServer();
            string route = RouteOf(s, method, path);
            Assert.True(route == "unknown" || !IsMiscRoute(route), path + " was taken by " + route);
        }

        private static readonly string[] MiscPrefixes =
        {
            "social.", "messaging.", "alerts.", "leaderboards.", "gamestats.", "profanity.", "chat.", "clans.", "wall.",
            "bantool.", "geo.", "misc.",
        };

        private static bool IsMiscRoute(string name)
        {
            foreach (string prefix in MiscPrefixes) if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        private static string RouteOf(MiscServer s, string method, string path)
        {
            var request = HttpRequest.Create(method, path, method == "GET" ? null : "a=1");
            s.Server.Router.Dispatch(request);
            return request.RouteName;
        }
    }
}
