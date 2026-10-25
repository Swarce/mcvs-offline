using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Alerts;
using Xunit;

namespace McvsOffline.Tests.Misc
{
    /// <summary>Hermes mailboxes as the game server and the TS use them (report 02 §4.7).</summary>
    public class MessagingTests
    {
        private static readonly string TournamentGift = "{\"gifts\":[{\"name\":\"Credits\",\"value\":\"500\"}],\"message\":\"Well done\"}";

        [Fact]
        public void GameServerGiftReachesThePlayersSecuredMailboxWithEveryFieldTheClientNeeds()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("alice");

            // As GM/RewardComponent.cs SendTournamentRewardHermes posts it (game server's own session).
            var post = s.Post("/messages/secured/" + Uri.EscapeDataString(p.FedId), s.GameServer.Token,
                "body", TournamentGift, "delay", "0", "rollover", "False", "message_label", "room1",
                "replace_matching_label", "True", "type", "gift", "alert_kairos", "True",
                "created", "2026-10-24T12:00:00Z", "expirationDate", "2026-10-31T12:00:00Z", "title", "Reward",
                "from", "Tournament room", "gift_type", "LiveTournament", "roomId", "room1", "quantityPaid", "0");
            Assert.Equal(200, post.StatusCode);

            // The TS reads it with the player's on-behalf session.
            var list = MiscServer.Arr(s.Get("/messages/secured/me", p.OnBehalfToken));
            var m = Assert.IsType<Dictionary<string, object>>(Assert.Single(list));
            Assert.False(string.IsNullOrEmpty((string)m["id"]));
            Assert.Equal("2026-10-24T12:00:00Z", m["created"]);
            Assert.Equal("gift", m["type"]);
            Assert.Equal(TournamentGift, m["body"]);
            Assert.Equal("Tournament room", m["from"]);
            Assert.Equal("LiveTournament", m["gift_type"]);
            Assert.Equal("2026-10-31T12:00:00Z", m["expirationDate"]);
            Assert.Equal("Reward", m["title"]);
            Assert.Equal("room1", m["roomId"]);
            // Delivery controls are not message fields.
            Assert.False(m.ContainsKey("access_token"));
            Assert.False(m.ContainsKey("alert_kairos"));
            Assert.False(m.ContainsKey("message_label"));

            // Reading does not delete; the other transport is separate.
            Assert.Single(MiscServer.Arr(s.Get("/messages/secured/me", p.OnBehalfToken)));
            Assert.Empty(MiscServer.Arr(s.Get("/messages/inbox/me", p.OnBehalfToken)));
        }

        [Fact]
        public void MessagesWithoutCreatedGetOneAndFromDefaultsToTheSender()
        {
            using var s = new MiscServer(new ManualClock(new DateTime(2026, 10, 25, 9, 30, 0, DateTimeKind.Utc)));
            TestPlayer p = s.NewPlayer("bob");
            Assert.Equal(200, s.Post("/messages/inbox/me", p.Token, "body", "hello").StatusCode);
            var m = (Dictionary<string, object>)Assert.Single(MiscServer.Arr(s.Get("/messages/inbox/me", p.Token)));
            Assert.Equal("2026-10-25T09:30:00Z", m["created"]);
            Assert.Equal(p.FedId, m["from"]);
            Assert.Equal("hello", m["body"]);
            Assert.False(m.ContainsKey("type"));
        }

        [Fact]
        public void TheGamesOwnDateFormatIsKeptAsSent()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("bea");
            // DateTimeExtension.ToISO8601: "yyyy-MM-dd HH:mm:ssZ" (space, not 'T'); the client DateTime.Parses it back.
            s.Post("/messages/secured/me", p.OnBehalfToken, "body", "x", "created", "2026-10-25 10:00:00Z");
            s.Post("/messages/secured/me", p.OnBehalfToken, "body", "y", "created", "not a date");
            var list = MiscServer.Arr(s.Get("/messages/secured/me", p.OnBehalfToken));
            Assert.Equal("2026-10-25 10:00:00Z", ((Dictionary<string, object>)list[0])["created"]);
            Assert.NotEqual("not a date", ((Dictionary<string, object>)list[1])["created"]);
        }

        [Fact]
        public void DeleteRemovesTheListedIdsAndDeleteDeleteReadsAndClears()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("carol");
            for (int i = 0; i < 3; i++) s.Post("/messages/secured/me", p.OnBehalfToken, "body", "m" + i);
            var ids = new List<string>();
            foreach (Dictionary<string, object> m in MiscServer.Arr(s.Get("/messages/secured/me", p.OnBehalfToken))) ids.Add((string)m["id"]);
            Assert.Equal(3, ids.Count);

            // As EN/MessagingTransport.cs ConfirmProcessed sends it: token in the query and the body, msgids in the body.
            var del = s.Delete("/messages/secured/me", p.OnBehalfToken, "msgids", ids[0] + "," + ids[2]);
            Assert.Equal(200, del.StatusCode);
            Assert.Equal(2L, MiscServer.Obj(del)["deleted"]);
            var left = MiscServer.Arr(s.Get("/messages/secured/me", p.OnBehalfToken));
            Assert.Equal(ids[1], ((Dictionary<string, object>)Assert.Single(left))["id"]);

            Assert.Single(MiscServer.Arr(s.Get("/messages/secured/me?delete=delete", p.OnBehalfToken)));
            Assert.Empty(MiscServer.Arr(s.Get("/messages/secured/me", p.OnBehalfToken)));
        }

        [Fact]
        public void ReplaceMatchingLabelKeepsOnlyTheNewestMessageOfThatLabel()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("dave");
            // GM/GameServerComponent.cs MakeSureProfileIsUnlocked posts this on every locked-profile login.
            for (int i = 0; i < 3; i++)
            {
                s.Post("/messages/secured/" + p.FedId, p.OnBehalfToken, "body", "", "message_label", "multipleConnectionDetected",
                       "replace_matching_label", "True", "alert_kairos", "True", "type", "multipleConnectionDetected", "roomId", "r" + i);
            }
            s.Post("/messages/secured/me", p.OnBehalfToken, "body", "other", "message_label", "x");
            var list = MiscServer.Arr(s.Get("/messages/secured/me", p.OnBehalfToken));
            Assert.Equal(2, list.Count);
            Assert.Contains(list, m => (string)((Dictionary<string, object>)m)["roomId"] == "r2");
        }

        [Fact]
        public void DelayedMessagesAppearWhenDue()
        {
            var clock = new ManualClock(new DateTime(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc));
            using var s = new MiscServer(clock);
            TestPlayer p = s.NewPlayer("erin");
            s.Post("/messages/inbox/me", p.Token, "body", "later", "delay", "60");
            Assert.Empty(MiscServer.Arr(s.Get("/messages/inbox/me", p.Token)));
            clock.Advance(TimeSpan.FromSeconds(61));
            Assert.Single(MiscServer.Arr(s.Get("/messages/inbox/me", p.Token)));
        }

        [Fact]
        public void BrokenGiftsAreRejectedAndPushTransportsDropped()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("frank");
            // The client throws CorruptedGiftException for these and would never consume them.
            Assert.Equal(400, s.Post("/messages/secured/me", p.Token, "body", "", "type", "gift").StatusCode);
            Assert.Equal(400, s.Post("/messages/secured/me", p.Token, "body", "not json", "type", "gift").StatusCode);
            Assert.Equal(200, s.Post("/messages/secured/me", p.Token, "body", "", "type", "gift", "gift_type", "Credits").StatusCode);

            Assert.Equal(200, s.Post("/messages/gcm/me", p.Token, "body", "push").StatusCode);
            Assert.Empty(MiscServer.Arr(s.Get("/messages/gcm/me", p.Token)));
            Assert.Equal(200, s.Post("/transports/gcm/endpoints/abc", p.Token).StatusCode);
            Assert.Equal(200, s.Post("/transports/gcm/endpoints/APA91b:x/y-z_", p.Token).StatusCode);
            Assert.Equal(200, s.Post("/transports/gcm/endpoints/delete", p.Token).StatusCode);
            // Clan join-request push notifications (no body, localizations) go to transport "any".
            var pn = s.Post("/messages/any/multicast", p.Token, "credentials", "[\"" + p.FedId + "\"]", "localizations", "{}", "default_localization", "en");
            Assert.Equal(200, pn.StatusCode);
            Assert.Empty(MiscServer.Arr(s.Get("/messages/any/me", p.Token)));
        }

        [Fact]
        public void MulticastDeliversToEveryCredential()
        {
            using var s = new MiscServer();
            TestPlayer a = s.NewPlayer("m1"), b = s.NewPlayer("m2");
            var r = s.Post("/messages/inbox/multicast", s.GameServer.Token, "body", TournamentGift, "type", "gift",
                           "credentials", "[\"" + a.FedId + "\",\"" + b.FedId + "\"]", "localizations", "{\"en\":\"Hi\"}");
            Assert.Equal(2L, MiscServer.Obj(r)["count"]);
            Assert.Single(MiscServer.Arr(s.Get("/messages/inbox/me", a.Token)));
            var m = (Dictionary<string, object>)Assert.Single(MiscServer.Arr(s.Get("/messages/inbox/me", b.Token)));
            Assert.Equal("{\"en\":\"Hi\"}", m["localizations"]);
        }

        [Fact]
        public void UnknownOrMissingTokensGet401()
        {
            using var s = new MiscServer();
            Assert.Equal(401, s.Get("/messages/secured/me", "mcvs00000000.ffffffffffffffffffffffffffffffff").StatusCode);
            Assert.Equal(401, s.Get("/messages/secured/me").StatusCode);
        }

        [Fact]
        public void AlertKairosNotifiesTheRecipientsStream()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("gina");
            AlertHub hub = AlertHub.For(s.Context);
            using (var sub = hub.Subscribe(new[] { p.FedId }, new string[0], null))
            {
                s.Post("/messages/secured/" + p.FedId, s.GameServer.Token, "body", TournamentGift, "type", "gift", "alert_kairos", "True");
                s.Post("/messages/secured/" + p.FedId, s.GameServer.Token, "body", "quiet"); // no alert_kairos
                List<string> alerts = sub.Take(1000, null);
                var alert = MiniJson.ParseObject(Assert.Single(alerts));
                Assert.Equal("message", alert["type"]);
                Assert.Equal("secured", alert["transport"]);
            }
        }

        [Fact]
        public void OtherModulesCanPostThroughTheEventHub()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("hank");
            var payload = new Dictionary<string, object>
            {
                ["to"] = p.FedId,
                ["body"] = TournamentGift,
                ["type"] = "gift",
                ["fields"] = new Dictionary<string, object> { ["title"] = "Unlock" },
            };
            Assert.Equal(1, s.Context.Events.Publish("messaging.post", payload));
            var m = (Dictionary<string, object>)Assert.Single(MiscServer.Arr(s.Get("/messages/secured/me", p.Token)));
            Assert.Equal("Unlock", m["title"]);
        }

        [Fact]
        public void MailboxesSurviveARestart()
        {
            using var dir = new TempDir();
            string token;
            using (var s = new MiscServer(null, dir))
            {
                TestPlayer p = s.NewPlayer("ivy");
                token = p.Token;
                s.Post("/messages/secured/me", p.Token, "body", "kept");
            }
            using (var s = new MiscServer(null, dir))
            {
                var m = (Dictionary<string, object>)Assert.Single(MiscServer.Arr(s.Get("/messages/secured/me", token)));
                Assert.Equal("kept", m["body"]);
            }
        }
    }
}
