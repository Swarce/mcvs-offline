using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services;
using McvsOffline.Server.Services.Social;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Misc
{
    /// <summary>
    /// Conformance: the REAL client stack (Gameloft.OnlinePlatform.dll + its Newtonsoft.Json, from the player's APK,
    /// never committed) talks to the MISC services over HTTP and parses every answer with its own code: Hermes
    /// messages and gifts, the Kairos stream, LeaderboardData, SocialEvent, SocialProfile, Clan/ClanMember, wall posts,
    /// the chat room stream, the profanity filter and game_stats.
    /// <para>
    /// The assemblies are loaded at run time from MCVS_REFS (default /home/user/mcvs-work/extracted/assemblies, as for
    /// the embedded build) into an isolated load context; the test is skipped when they are absent (and fails instead
    /// with MCVS_REQUIRE_REAL_CLIENT=1). The server is the complete one: the sessions are opened through the AUTH module.
    /// </para>
    /// </summary>
    public class ClientConformanceTests
    {
        private const string ClientId = "2620:66102:1.17.32:android:googleplay";
        private const string ServerScopes = "storage_restricted chat message storage_ro leaderboard_override social_trophies storage social_group leaderboard_ro social_event clusters config message_secured leaderboard_restricted message_broadcast alert auth lobby leaderboard social social_clan";

        private readonly ITestOutputHelper output;

        public ClientConformanceTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public async Task RealClientParsesEveryMiscAnswer()
        {
            GameClient client = GameClient.TryLoad();
            if (client == null)
            {
                output.WriteLine("Gameloft.OnlinePlatform.dll not found (set MCVS_REFS to the APK's assemblies folder): skipped");
                return;
            }

            using var dir = new TempDir();
            string data = dir.Combine("data");
            Directory.CreateDirectory(Path.Combine(data, "content", "social"));
            File.WriteAllText(Path.Combine(data, "content", "social", "events.json"), @"[
 {""id"":""season_S1_D3"",""name"":""Season"",""start_date"":""2026-10-19T00:00:00Z"",""end_date"":""2099-10-26T00:00:00Z"",""_version"":2,
  ""_priority"":1,""_batch_tag"":""S1"",""_participation"":""(CurrentDivision == 3)"",
  ""_template"":{""event_tuning"":{""event_type"":{""private"":""1"",""value"":""Game.DivisionSeasonEvent""}}},
  ""tournament"":{""leaderboard"":{""name"":""Leaderboard_S1_D3"",""order"":""desc""},""type"":""award_by_rank"",
     ""awards"":[{""gifts"":[{""name"":""Credits"",""value"":100}],""rank_interval"":[1,10]}]}}]");

            var options = new ServerOptions { DataDir = data, Port = 0, ConsoleLog = false, FileLog = false };
            // The complete server: eve, pandora and authorize are the AUTH module's real ones.
            using var server = McvsServer.Create(options);
            server.Start();
            var ct = new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;
            List<string> scopes = ServerScopes.Split(' ').ToList();

            dynamic fed = client.New("Federation", ClientId, "http://127.0.0.1:" + server.Port + "/", "mcvs", 60000, 3, client.Enum("Federation+Protocol", "HTTPS"));
            dynamic player = await GameClient.Await(fed.OpenSession(client.Credential("anonymous:conformance1"), "pw", "device1", null, new List<string> { "auth", "lobby", "config" }, ct));
            dynamic ts = await GameClient.Await(fed.OpenSessionForUser(client.Credential("game:mcvsoffline"), "mcvsoffline", client.Credential("anonymous:conformance1"), "device1", null, scopes, ct));
            dynamic gs = await GameClient.Await(fed.OpenSession(client.Credential("game:mcvsoffline"), "mcvsoffline", "mcvsGameServer", null, scopes, ct));
            string fedId = (string)ts.FedId.ToString();
            Assert.Equal((string)player.FedId.ToString(), fedId);

            // Kairos + Hermes ------------------------------------------------------------------------------------
            var alerts = new Recorder();
            dynamic listener = client.Service(ts, "AlertService").GetAlert();
            alerts.Attach(listener, "AlertReceived");
            listener.StartListening();
            var connection = new Recorder();
            dynamic connectionListener = client.Service(ts, "AlertService").GetAlert();
            connection.Attach(connectionListener, "AlertReceived");
            dynamic connectionOnly = client.List("AlertType", client.Static("AlertType", "Connection"));
            connectionListener.StartListening(connectionOnly);
            await Task.Delay(300, ct);

            var custom = new Dictionary<string, string>
            {
                ["created"] = DateTime.UtcNow.ToString("yyyy'-'MM'-'dd' 'HH':'mm':'ss'Z'"),
                ["expirationDate"] = DateTime.UtcNow.AddDays(7).ToString("yyyy'-'MM'-'dd' 'HH':'mm':'ss'Z'"),
                ["title"] = "Reward", ["from"] = "Room", ["gift_type"] = "LiveTournament", ["roomId"] = "r1",
            };
            string gift = "{\"gifts\":[{\"name\":\"Credits\",\"value\":\"500\"}],\"message\":\"gg\"}";
            dynamic gsMessaging = client.Service(gs, "MessagingService").GetMessaging("secured");
            Assert.True((bool)await GameClient.Await(gsMessaging.PostMessage(ts.FedId, gift, true, true, true, default(TimeSpan), null, "r1", true, null, false, custom, ct)));
            dynamic alert = alerts.WaitFirst(5000);
            Assert.NotNull(alert);
            Assert.Equal("message", (string)alert.Type.ToString());
            Assert.Equal("secured", (string)alert.Transport);

            dynamic tsMessaging = client.Service(ts, "MessagingService").GetMessaging("secured");
            dynamic messages = await GameClient.Await(tsMessaging.RetrieveMessages(ct, false));
            Assert.Equal(1, (int)messages.Count);
            dynamic giftData = messages.Messages[0];
            Assert.True((bool)giftData.IsGift);
            Assert.Equal("Credits", (string)giftData.Gifts[0].Item);
            Assert.Equal(500, (int)giftData.Gifts[0].Quantity);
            Assert.Equal("LiveTournament", (string)giftData.GiftType);
            Assert.Equal("gg", (string)giftData.Message);
            Assert.True(((DateTime)giftData.CreationDate).Year >= 2026);
            await GameClient.Await(tsMessaging.DeleteMessages(ct, new[] { (string)giftData.Id }));
            Assert.Equal(0, (int)(await GameClient.Await(tsMessaging.RetrieveMessages(ct, false))).Count);
            Assert.Equal(0, connection.Count);

            // Olympus -------------------------------------------------------------------------------------------------
            dynamic board = client.Service(ts, "LeaderboardService").GetLeaderboard("Leaderboard_QA_S1_D3", client.Enum("Leaderboard+LeaderboardSort", "Descending"));
            dynamic before = await GameClient.Await(board.SelfData(ct));
            Assert.False((bool)before.myEntry.Credential.IsValid);
            dynamic props = client.New("CustomProperties");
            props.Add("_level", "12");
            props.Add("_leagueId", 3);
            props.Add("_fed_id", ts.FedId);
            await GameClient.Await(board.PostScore(260, "Conformance", props, client.Enum("Leaderboard+ReplaceWhen", "Always"), ct, null, null));
            dynamic self = await GameClient.Await(board.SelfData(ct));
            Assert.Equal(100, (int)self.totalEntries);
            Assert.InRange((int)self.myEntry.Rank, 1, 100);
            Assert.Equal(260, (int)self.myEntry.Score);
            Assert.Equal(fedId, (string)self.myEntry.FedId);
            dynamic top = await GameClient.Await(board.Top(0, 100, ct, null));
            Assert.Equal(100, (int)top.entries.Count);
            dynamic around = await GameClient.Await(board.AroundMe(0, 4, ct, "me"));
            Assert.Equal(4, (int)around.entries.Count);
            dynamic friends = await GameClient.Await(board.Friends(ct, 0, 100));
            Assert.Equal(1, (int)friends.myEntry.Rank);

            // Osiris events and social -----------------------------------------------------------------------------
            dynamic social = client.Service(ts, "SocialService");
            dynamic events = await GameClient.Await(social.GetEvents(new[] { "season_S1_D3", "unknown" }, "en", ct));
            Assert.Equal(1, (int)events.Count);
            dynamic ev = events[0];
            Assert.Equal(2, (int)ev.Revision);
            Assert.Equal("Leaderboard_S1_D3", (string)ev.LeaderboardId);
            Assert.Equal("String", ev.Fields["_template"].Type.ToString());
            Assert.Equal(0, (int)(await GameClient.Await(social.ListConnections("friend", null, null, null, ct))).Count);
            Assert.Equal(0, (int)(await GameClient.Await(social.GetPendingRequests(null, null, null, ct, null))).Count);
            dynamic profiles = client.Service(ts, "SocialProfileService");
            await GameClient.Await(profiles.UpdateSocialProfile(ct, "Conformance", "en", "US"));
            dynamic profile = await GameClient.Await(profiles.GetSocialProfile(ct, ts.Credential));
            Assert.Equal("Conformance", (string)profile.Name);
            dynamic filtered = await GameClient.Await(client.Service(ts, "ProfanityService").FilterMessage("en", "Conformance_01", ct));
            Assert.False((bool)filtered.HasBadWords);
            Assert.Equal("Conformance_01", (string)filtered.CensoredMessage);

            // Clans and the clan wall -------------------------------------------------------------------------------
            dynamic clans = client.Service(ts, "ClanService");
            dynamic clan = await GameClient.Await(clans.CreateClan(ct, "ConformanceClan", "Clan", "desc", 30, client.Static("ClanMembership", "Open"),
                                                                   null, null, "Icon_1", 0, null, null, null, 30, null));
            string clanId = (string)clan.Id;
            dynamic mine = await GameClient.Await(clans.GetClan(ct, clanId, true));
            Assert.Equal(1, (int)mine.Members.Count);
            Assert.NotNull(mine.Me);
            Assert.True((bool)mine.IsOwner(ts.FedId));
            dynamic scored = await GameClient.Await(clans.IncrementClanField(ct, clanId, "score", 15, false));
            Assert.Equal(15L, (long)scored.Score);
            await GameClient.Await(mine.SendNotification(ct, client.Enum("ClanNotifictionType", "JoinedClan"), mine.Me, mine.Me));
            dynamic wall = await GameClient.Await(mine.GetLastAlerts(ct));
            Assert.Equal(1, (int)wall.Count);
            Assert.NotNull(wall[0].ExtraFields);
            Assert.Equal("JoinedClan", wall[0].ExtraFields.Type.ToString());

            // Group chat ---------------------------------------------------------------------------------------------
            dynamic room = await GameClient.Await(client.Service(ts, "ChatService").SubscribeToRoom("clan_" + clanId, "en", null, ct));
            Assert.EndsWith("/", (string)room.CommandUri.ToString());
            var chat = new Recorder();
            chat.Attach(room, "ChatMessageReceived");
            Task listening = (Task)room.StartListening(ct);
            await Task.Delay(300, ct);
            dynamic sender = client.New("ChatUser");
            sender.Credential = ts.Credential;
            sender.Nickname = "Conformance";
            Assert.True((bool)await GameClient.Await(room.SendMessage(sender, "hello clan", client.Enum("ChatApi+MessageType", "message"), null, ct)));
            dynamic message = chat.WaitFirst(5000);
            Assert.NotNull(message);
            Assert.Equal("hello clan", (string)message.Message);
            Assert.Equal("hello clan", (string)message.OriginalMessage);
            room.StopListening();

            // game_stats ---------------------------------------------------------------------------------------------
            object statsRequest = client.Type("GameStatsApi").GetMethod("GetGameStatsByTime").Invoke(null, new object[] { ts, "MatchmakingDistribution", null, 1, 2 });
            dynamic stats = await GameClient.Await(ts.JsonRequest((dynamic)statsRequest, ct));
            Assert.Equal("Array", stats.Token.Type.ToString());

            await GameClient.Await(listener.StopListening(ct));
            await GameClient.Await(connectionListener.StopListening(ct));
            server.Stop();
        }

        /// <summary>
        /// Differential check of <see cref="TleTemplateValidator"/> against the game's own template parser
        /// (<c>Gameloft.Engine.TimeLimitedEventsComponent.ParseTemplate</c>): every template the validator drops must make
        /// the real parser throw, and every template it keeps must parse.
        /// </summary>
        [Fact]
        public void TemplateValidatorAgreesWithTheRealTemplateParser()
        {
            GameClient client = GameClient.TryLoad();
            Type tle = client == null ? null : client.TypeIn("Gameloft.Engine", "Gameloft.Engine.TimeLimitedEventsComponent");
            if (tle == null)
            {
                output.WriteLine("Gameloft.Engine.dll not found (set MCVS_REFS to the APK's assemblies folder): skipped");
                return;
            }
            object component = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(tle);
            MethodInfo parse = tle.GetMethod("ParseTemplate");
            foreach (string template in TleTemplateValidatorTests.Accepted.Concat(TleTemplateValidatorTests.Rejected))
            {
                string thrown = null;
                try
                {
                    parse.Invoke(component, new object[] { template, "en" });
                }
                catch (TargetInvocationException ex)
                {
                    thrown = ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
                }
                string problem = TleTemplateValidator.Problem(new Dictionary<string, object> { ["_template"] = template });
                output.WriteLine((thrown == null ? "parses " : "throws ") + template + (thrown == null ? "" : "  <- " + thrown));
                Assert.True((thrown != null) == (problem != null),
                            template + ": real parser " + (thrown ?? "parsed it") + "; validator " + (problem ?? "kept it"));
            }
        }

        /// <summary>Records the arguments of a client event (EventHandler&lt;T&gt; or the chat delegate).</summary>
        private sealed class Recorder
        {
            private readonly List<object> items = new List<object>();

            public int Count { get { lock (items) return items.Count; } }

            public void Attach(object target, string eventName)
            {
                EventInfo e = ((object)target).GetType().GetEvent(eventName);
                // A (object, object) method binds to EventHandler<Alert> and ChatMessageReceivedDelegate (contravariance).
                e.AddEventHandler(target, Delegate.CreateDelegate(e.EventHandlerType, this, GetType().GetMethod(nameof(OnEvent))));
            }

            public void OnEvent(object sender, object args)
            {
                lock (items)
                {
                    items.Add(args);
                    Monitor.PulseAll(items);
                }
            }

            public object WaitFirst(int timeoutMs)
            {
                lock (items)
                {
                    if (items.Count == 0) Monitor.Wait(items, timeoutMs);
                    return items.Count > 0 ? items[0] : null;
                }
            }
        }

        /// <summary>
        /// The player's client assemblies, loaded in their own context (their Newtonsoft.Json 7 included). Helpers return
        /// dynamic so calls bind on the runtime types (a statically typed object argument would bind as object).
        /// </summary>
        private sealed class GameClient
        {
            private readonly AssemblyLoadContext context;
            private readonly string directory;
            private readonly Assembly onlinePlatform;

            private GameClient(AssemblyLoadContext context, string directory, Assembly onlinePlatform)
            {
                this.context = context;
                this.directory = directory;
                this.onlinePlatform = onlinePlatform;
            }

            /// <summary>A type from another game assembly (e.g. Gameloft.Engine), or null when that assembly is absent.</summary>
            public Type TypeIn(string assembly, string typeName)
            {
                string path = Path.Combine(directory, assembly + ".dll");
                return File.Exists(path) ? context.LoadFromAssemblyPath(path).GetType(typeName, true) : null;
            }

            public static GameClient TryLoad()
            {
                string dir = Environment.GetEnvironmentVariable("MCVS_REFS");
                if (string.IsNullOrEmpty(dir)) dir = "/home/user/mcvs-work/extracted/assemblies";
                string path = Path.Combine(dir, "Gameloft.OnlinePlatform.dll");
                if (!File.Exists(path))
                {
                    if (Conformance.GameClient.Required) throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but " + path + " is missing");
                    return null;
                }
                var context = new AssemblyLoadContext("mcvs-game-client");
                context.Resolving += (ctx, name) =>
                {
                    // The game's own libraries only: the BCL (mscorlib, System, System.*) comes from the runtime.
                    if (name.Name == "mscorlib" || name.Name == "System" || name.Name.StartsWith("System.", StringComparison.Ordinal)) return null;
                    string candidate = Path.Combine(dir, name.Name + ".dll");
                    return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
                };
                return new GameClient(context, dir, context.LoadFromAssemblyPath(path));
            }

            public Type Type(string name)
            {
                return onlinePlatform.GetType("Gameloft.OnlinePlatform." + name, true);
            }

            public dynamic New(string type, params object[] args)
            {
                return Activator.CreateInstance(Type(type), args);
            }

            public dynamic Credential(string text)
            {
                return New("Credential", text);
            }

            public dynamic Enum(string type, string value)
            {
                return System.Enum.Parse(Type(type), value);
            }

            public dynamic Static(string type, string member)
            {
                return Type(type).GetField(member, BindingFlags.Public | BindingFlags.Static).GetValue(null);
            }

            public dynamic List(string elementType, params object[] items)
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type(elementType)));
                foreach (object item in items) list.Add(item);
                return list;
            }

            /// <summary>session.GetService&lt;T&gt;() for a service type name.</summary>
            public dynamic Service(object session, string serviceType)
            {
                MethodInfo getService = session.GetType().GetMethod("GetService").MakeGenericMethod(Type(serviceType));
                return getService.Invoke(session, null);
            }

            /// <summary>Awaits a Task or Task&lt;T&gt; returned through dynamic and gives its result (null for a plain Task).</summary>
            public static async Task<dynamic> Await(object task)
            {
                var t = (Task)task;
                await t;
                PropertyInfo result = t.GetType().GetProperty("Result");
                return result == null || result.PropertyType.Name == "VoidTaskResult" ? null : result.GetValue(t);
            }
        }
    }
}
