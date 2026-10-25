using System;
using System.Collections.Generic;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Alerts;
using Xunit;

namespace McvsOffline.Tests.Misc
{
    /// <summary>Clans (Osiris groups), walls and group chat (report 02 §4.1, §4.3, §4.6).</summary>
    public class ClansWallChatTests
    {
        private static string CreateClan(MiscServer s, TestPlayer owner, string name = "Alpha")
        {
            // As ClanApi.CreateClan sends it: custom fields with "_" and the _json_ prefix.
            var r = s.Post("/groups", owner.OnBehalfToken, "name", name, "category", "Clan", "description", "best clan",
                           "member_limit", "30", "membership", "open", "type", "clan", "visibility", "member",
                           "avatar", "Clan_Icon_3", "score", "0", "_minLevel", "_json_:5", "_tags", "_json_:[\"pvp\"]");
            Assert.Equal(200, r.StatusCode);
            return (string)MiscServer.Obj(r)["id"];
        }

        [Fact]
        public void ACreatedClanHasTheShapeClanParsingNeeds()
        {
            using var s = new MiscServer();
            TestPlayer owner = s.NewPlayer("alice");
            s.Post("/accounts/me", owner.OnBehalfToken, "name", "Alice");
            string id = CreateClan(s, owner);
            var clan = MiscServer.Obj(s.Get("/groups/" + id, owner.OnBehalfToken));
            Assert.Equal("Alpha", clan["name"]);
            Assert.Equal("Clan", clan["category"]);
            Assert.Equal("open", clan["membership"]);
            Assert.Equal(1L, clan["member_count"]);
            Assert.Equal(30L, clan["member_limit"]);
            Assert.Equal(5L, clan["_minLevel"]);
            Assert.Equal("pvp", Assert.Single((List<object>)clan["_tags"]));
            var ownerEntry = (Dictionary<string, object>)clan["owner"];
            Assert.Equal(owner.FedId, ownerEntry["credential"]);
            Assert.Equal("Alice", ownerEntry["name"]);
            var member = (Dictionary<string, object>)Assert.Single((List<object>)clan["members"]);
            Assert.Equal(owner.FedId, member["fed_id"]);
            Assert.Single((List<object>)clan["owners"]);
            Assert.Equal(404, s.Get("/groups/doesnotexist", owner.OnBehalfToken).StatusCode);
            Assert.Equal(400, s.Post("/groups", owner.OnBehalfToken, "category", "Clan").StatusCode);
        }

        [Fact]
        public void FieldsAre404UntilSetAndIncrementsAdd()
        {
            using var s = new MiscServer();
            TestPlayer owner = s.NewPlayer("bob");
            string id = CreateClan(s, owner);
            Assert.Equal(404, s.Get("/groups/" + id + "/fields/_weeklyScore", owner.OnBehalfToken).StatusCode);
            var inc = s.Post("/groups/" + id + "/fields/_weeklyScore", owner.OnBehalfToken, "object", "15", "operation", "inc");
            Assert.Equal(200, inc.StatusCode);
            Assert.Equal(15L, MiscServer.Obj(inc)["_weeklyScore"]);
            s.Post("/groups/" + id + "/fields/_weeklyScore", owner.OnBehalfToken, "object", "5", "operation", "dec");
            Assert.Equal("10", s.Get("/groups/" + id + "/fields/_weeklyScore", owner.OnBehalfToken).BodyText);
            s.Post("/groups/" + id + "/fields/score", owner.OnBehalfToken, "object", "120", "operation", "inc");
            Assert.Equal(120L, MiscServer.Obj(s.Get("/groups/" + id, owner.OnBehalfToken))["score"]);
            s.Post("/groups/" + id + "/fields/_motd", owner.OnBehalfToken, "object", "hello");
            Assert.Equal("\"hello\"", s.Get("/groups/" + id + "/fields/_motd", owner.OnBehalfToken).BodyText);
        }

        [Fact]
        public void JoinLeaveUpdateAndSearch()
        {
            using var s = new MiscServer();
            TestPlayer owner = s.NewPlayer("carol"), other = s.NewPlayer("dave");
            string id = CreateClan(s, owner, "Night Owls");
            Assert.Single(MiscServer.Arr(s.Get("/groups?category=Clan&limit=10&offset=0", other.OnBehalfToken)));
            Assert.Single(MiscServer.Arr(s.Get("/groups/categories/Clan/find/keyword?keywords=owls&limit=10", other.OnBehalfToken)));
            Assert.Empty(MiscServer.Arr(s.Get("/groups/categories/Clan/find/keyword?keywords=eagles", other.OnBehalfToken)));
            Assert.Single(MiscServer.Arr(s.Get("/groups/categories/Clan/find/matchmaking_score?limit=10&score=5", other.OnBehalfToken)));

            // Join (credential=me): immediate 200, nobody approves requests offline.
            var join = s.Post("/groups/" + id + "/members", other.OnBehalfToken, "credential", "me", "alert_kairos", "True");
            Assert.Equal(200, join.StatusCode);
            Assert.Equal(2, MiscServer.Arr(s.Get("/groups/" + id + "/members?limit=100&offset=0&owners=False", owner.OnBehalfToken)).Count);
            Assert.Single(MiscServer.Arr(s.Get("/groups/" + id + "/members?limit=100&offset=0&owners=True", owner.OnBehalfToken)));
            Assert.Equal(200, s.Get("/groups/" + id + "/members/" + Uri.EscapeDataString(other.FedId), owner.OnBehalfToken).StatusCode);

            Assert.Equal(200, s.Post("/groups/" + id + "/members/" + Uri.EscapeDataString(other.FedId), other.OnBehalfToken, "_rank", "_json_:\"member\"", "_score", "_json_:7").StatusCode);
            var member = MiscServer.Obj(s.Get("/groups/" + id + "/members/" + Uri.EscapeDataString(other.FedId), owner.OnBehalfToken));
            Assert.Equal("member", member["_rank"]);
            Assert.Equal(7L, member["_score"]);

            var update = s.Post("/groups/" + id, owner.OnBehalfToken, "description", "new", "promote_owners", "[\"" + other.FedId + "\"]");
            Assert.Equal(2, ((List<object>)MiscServer.Obj(update)["owners"]).Count);

            // The owner leaves: the other member keeps the clan; the last one leaving deletes it.
            var left = s.Post("/groups/" + id + "/members/me/delete", owner.OnBehalfToken);
            Assert.Equal(200, left.StatusCode);
            Assert.Equal(1L, MiscServer.Obj(s.Get("/groups/" + id, other.OnBehalfToken))["member_count"]);
            s.Post("/groups/" + id + "/members/me/delete", other.OnBehalfToken);
            Assert.Equal(404, s.Get("/groups/" + id, other.OnBehalfToken).StatusCode);
        }

        [Fact]
        public void InvitationsAnswerTheGroupAndDeleteRemovesTheClan()
        {
            using var s = new MiscServer();
            TestPlayer owner = s.NewPlayer("erin"), other = s.NewPlayer("frank");
            string id = CreateClan(s, owner);
            var invite = s.Post("/groups/" + id + "/members", owner.OnBehalfToken, "credential", other.FedId);
            Assert.Equal(id, JsonPath.Get(MiscServer.Obj(invite), "group.id"));
            Assert.Equal(1L, MiscServer.Obj(s.Get("/groups/" + id, owner.OnBehalfToken))["member_count"]);
            Assert.Equal(200, s.Post("/groups/" + id + "/delete", owner.OnBehalfToken).StatusCode);
            Assert.Equal(404, s.Get("/groups/" + id, owner.OnBehalfToken).StatusCode);
            Assert.Equal("{}", s.Get("/groups/config", owner.OnBehalfToken).BodyText);
        }

        [Fact]
        public void ClanWallKeepsNotificationsNewestFirstAndAlertsTheClanTarget()
        {
            using var s = new MiscServer();
            TestPlayer owner = s.NewPlayer("gina");
            s.Post("/accounts/me", owner.OnBehalfToken, "name", "Gina");
            string id = CreateClan(s, owner);
            using (var sub = AlertHub.For(s.Context).Subscribe(new[] { "fed_id:someone", "group:" + id }, new string[0], null))
            {
                for (int i = 0; i < 4; i++)
                {
                    string text = "{\"Type\":" + i + ",\"Receiver\":null,\"TriggeredBy\":{\"credential\":\"" + owner.FedId + "\"}}";
                    Assert.Equal(200, s.Post("/groups/" + id + "/wall", owner.OnBehalfToken, "text", text, "language", "en",
                                             "activity_type", "game_post", "alert_kairos", "True").StatusCode);
                }
                Assert.Equal(4, sub.Take(500, null).Count);
            }
            // OP/Clan.cs GetLastAlerts: ViewWall(limit: 3)
            var posts = MiscServer.Arr(s.Get("/groups/" + id + "/wall?sort_type=chronological&limit=3", owner.OnBehalfToken));
            Assert.Equal(3, posts.Count);
            var newest = (Dictionary<string, object>)posts[0];
            Assert.Contains("\"Type\":3", (string)newest["text"]);
            Assert.Equal("game_post", newest["type"]);
            Assert.Equal(owner.FedId, JsonPath.Get(newest, "actor.credential"));
            Assert.Equal("Gina", JsonPath.Get(newest, "actor.nickname"));
            Assert.Equal("group:" + id, JsonPath.Get(newest, "target.credential"));
            Assert.False(string.IsNullOrEmpty((string)newest["creation"]));
            Assert.Equal("[]", s.Get("/accounts/me/wall", owner.Token).BodyText);
            Assert.Equal(200, s.Post("/accounts/me/wall", owner.Token, "text", "hi").StatusCode);
            Assert.Single(MiscServer.Arr(s.Get("/accounts/" + Uri.EscapeDataString(owner.FedId) + "/wall", owner.Token)));
        }

        [Fact]
        public void ChatSubscribeGivesUrlsThatRouteBackHereAndTheStreamCarriesMessages()
        {
            using var s = new MiscServer();
            s.Set("chat.keepAliveSeconds", 1L);
            s.Start();
            TestPlayer a = s.NewPlayer("hank"), b = s.NewPlayer("ivy");
            s.Post("/accounts/me", a.OnBehalfToken, "name", "Hank");

            var sub = MiscServer.Obj(s.Post("/chat/rooms/clan_abc/subscribe", a.OnBehalfToken, "language", "en"));
            string cmdUrl = (string)sub["cmd_url"];
            string listenUrl = (string)sub["listen_url"];
            Assert.EndsWith("/", cmdUrl); // the client appends "members/..." and "invite"
            Assert.Equal("", sub["status"]);
            Assert.StartsWith(s.Context.BaseUrl, listenUrl);

            string listenPath = listenUrl.Substring(s.Context.BaseUrl.Length - 1);
            using (var stream = new StreamingResponse(s.Server.Port, listenPath + "?access_token=" + Uri.EscapeDataString(b.OnBehalfToken)))
            {
                Assert.Equal(200, stream.Status);
                var info = MiniJson.ParseObject(stream.ReadDataLine());
                Assert.Equal("room_info", info["type"]);
                Assert.Equal(cmdUrl, info["cmd_url"]);

                string cmdPath = cmdUrl.Substring(s.Context.BaseUrl.Length - 1);
                var send = s.Post(cmdPath, a.OnBehalfToken, "msg", "hello clan", "user", "{\"nickname\":\"Hank\",\"_level\":12}",
                                  "message_type", "message", "_original_message", "hello clan", "_sub_type", "Message");
                Assert.Equal(200, send.StatusCode);
                var message = MiniJson.ParseObject(stream.ReadDataLine());
                Assert.Equal("message", message["type"]);
                Assert.Equal("hello clan", message["msg"]);
                Assert.Equal(a.FedId, JsonPath.Get(message, "sender.credential"));
                Assert.Equal("Hank", JsonPath.Get(message, "sender.nickname"));
                Assert.Equal("Message", message["_sub_type"]);
                Assert.False(string.IsNullOrEmpty((string)message["sent"]));
                Assert.False(string.IsNullOrEmpty((string)message["id"]));

                // Blank keep-alive lines in between (the client skips whitespace-only lines).
                Assert.Equal("", stream.ReadLine(3000));
            }

            Assert.Equal(2, MiscServer.Arr(s.Get(cmdUrl.Substring(s.Context.BaseUrl.Length - 1) + "members?offset=0&limit=100", a.OnBehalfToken)).Count);
            Assert.Equal(200, s.Post("/chat/rooms/clan_abc/members/" + Uri.EscapeDataString(b.FedId) + "/mute", a.OnBehalfToken, "limit", "60").StatusCode);
            Assert.Equal("mute", MiscServer.Obj(s.Post("/chat/rooms/clan_abc/subscribe", b.OnBehalfToken, "language", "en"))["status"]);
            Assert.Equal(200, s.Post("/chat/rooms/clan_abc/invite", a.OnBehalfToken, "recipient", b.FedId).StatusCode);
        }
    }
}
