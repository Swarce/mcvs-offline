using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Social;
using McvsOffline.Server.Services.Social.LiveOps;
using McvsOffline.Tests.Conformance;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Misc
{
    /// <summary>
    /// The Special Events (docs/LIVEOPS.md §8a) through the REAL client code: the CRM document through
    /// <c>StandardCrm.Data</c>, the events through <c>Json.Load</c>, <c>new SocialEvent(item)</c>, <c>ParseTemplate</c>,
    /// <c>TypeUtil.GetType</c> and <c>ArenaModeEvent.Initialize</c>, as RetrieveEventsAsync runs them; the TS's choice
    /// for a room (<c>TimeLimitedEventsComponent.GetInProgressEvents&lt;ArenaModeEvent&gt;().FirstOrDefault()</c>); the
    /// client's own <c>GameClientComponent.UpdateCurrentAndNextArenaEvent</c> and <c>GetArenaUIInfo</c> (the Battle popup's
    /// Special Event box: unlocked or not, and the null agent list an event without restrictions hands the Flash box); the TS → GS hand-over
    /// of <c>ArenaParams</c> through the game's Newtonsoft.
    /// <para>
    /// <see cref="ArenaClient"/> stands the client's GameClientComponent up in a World made of uninitialized game objects
    /// (a real World needs the native engine): only the fields those methods read are set (the clock through the game's
    /// own <c>Connection.LastServerTimeDelta</c>). The event images count as downloading (<c>IsLoading</c>), so
    /// <c>GetAssetPath</c> (native file lookup) is not reached; that they are files of the data pack is content-gen's check.
    /// </para>
    /// Skipped without the player's assemblies (MCVS_REFS), mandatory with MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    public class LiveOpsArenaConformanceTests
    {
        /// <summary>Friday 2026-10-23 15:00 UTC.</summary>
        private static readonly DateTime Start = new DateTime(2026, 10, 23, 15, 0, 0, DateTimeKind.Utc);

        private readonly ITestOutputHelper output;

        public LiveOpsArenaConformanceTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Fourteen simulated days, a CRM fetch every hour (and one an hour late around every window change): the TS
        /// always has exactly one Special Event in progress and the next one listed, both of the server's schedule; the
        /// client picks the same one the TS gives a room; GetArenaUIInfo never throws, reports the box unlocked for a player
        /// who owns one agent, shows the event's loc ids and a "next event" timer; between a window change and the late
        /// CRM fetch the already-listed next event has taken over. Every rotating mode shows up.
        /// </summary>
        [Theory]
        [MemberData(nameof(TestFolders.RealClientFolders), MemberType = typeof(TestFolders))]
        public void FourteenDaysAlwaysShowOneCurrentAndOneNextSpecialEvent(string platform, string folder)
        {
            GameClient game = GameClient.TryGetFrom(output, platform, folder, "Gameloft.Engine", "Game", "Gameloft.Util", "Newtonsoft.Json");
            if (game == null) return;
            game.RegisterGameAssemblies();
            var clock = new ManualClock(Start);
            using var s = new MiscServer(clock);
            TestPlayer p = s.NewPlayer("arena");
            var client = new ArenaClient(game, ownedAgent: "7");
            LiveOpsArena arena = LiveOpsService.For(s.Context).Catalog.Arena;
            Assert.NotNull(arena);
            var modes = new List<KeyValuePair<string, string>>();   // window key, mode, in order
            List<object> previous = null;
            DateTime previousExpiry = DateTime.MinValue;
            int checks = 0;
            for (int hour = 0; hour < 14 * 24; hour++)
            {
                DateTime now = clock.UtcNow;
                // A late CRM fetch: the list fetched before the window change still has the event that is now in progress.
                if (previous != null && now >= previousExpiry && now < previousExpiry.AddHours(1))
                {
                    client.SetTime(now);
                    client.SetEvents(previous);
                    AssertTheNextTookOver(client, now, "with the list fetched before " + TimeFormat.Iso8601(previousExpiry));
                    checks++;
                }

                Fetched fetched = Fetch(game, s, p, now);
                List<object> events = fetched.ArenaEvents;
                Assert.Equal(2, events.Count);
                client.SetTime(now);
                client.SetEvents(events);
                object current = AssertOneCurrentOneNext(client, now, "at " + TimeFormat.Iso8601(now));
                checks++;

                LiveOpsSnapshot snap = LiveOpsService.For(s.Context).Snapshot(now);
                Assert.Equal(snap.ArenaEventId, (string)GameClient.Get(current, "Id"));
                Assert.Equal(snap.ArenaWindow.End, Utc((DateTime)GameClient.Get(current, "EndDate")));
                Assert.True(fetched.Expiry <= snap.ArenaWindow.End, "the CRM must expire by the window's end");
                Assert.True(LiveOpsSchedule.TryParseArena(snap.ArenaEventId, out ArenaWindow w, out string mode));
                if (modes.Count == 0 || modes[modes.Count - 1].Key != w.Key) modes.Add(new KeyValuePair<string, string>(w.Key, mode));

                previous = events;
                previousExpiry = fetched.Expiry;
                clock.Advance(TimeSpan.FromHours(1));
            }
            output.WriteLine(platform + ": " + checks + " checks, windows: " + string.Join(", ", modes.Select(m => m.Key + " " + m.Value)));
            Assert.Equal(15, modes.Count);   // 2026-10-23 .. 2026-11-06: a 24 h window per day
            for (int i = 1; i < modes.Count; i++) Assert.NotEqual(modes[i - 1].Value, modes[i].Value);   // never twice in a row
            Assert.All(modes, m => Assert.True(arena.Mode(m.Value) != null && arena.Mode(m.Value).Rotate, m.Value));
            Assert.True(modes.Select(m => m.Value).Distinct().Count() >= 8, "the rotation repeats too soon");
        }

        /// <summary>
        /// A CRM fetch in the last second of a window (at boot, or with a clock a little ahead): the document expires at the
        /// next window's end, so it lists three Special Events, and the TS, whose list the fetch replaces, still has the one
        /// in progress. The client, which never clears its current event, and the TS agree before the window change and
        /// after it (OnEventsStarted, no refetch in between), so a match started in either second gets its
        /// <c>_arenaParams</c>.
        /// </summary>
        [Theory]
        [MemberData(nameof(TestFolders.RealClientFolders), MemberType = typeof(TestFolders))]
        public void AFetchJustBeforeAWindowChangeKeepsTheEventInProgress(string platform, string folder)
        {
            GameClient game = GameClient.TryGetFrom(output, platform, folder, "Gameloft.Engine", "Game", "Gameloft.Util", "Newtonsoft.Json");
            if (game == null) return;
            game.RegisterGameAssemblies();
            var clock = new ManualClock(Start);
            using var s = new MiscServer(clock);
            TestPlayer p = s.NewPlayer("boundary");
            var client = new ArenaClient(game, ownedAgent: "7");
            Fetched first = Fetch(game, s, p, Start);
            client.SetTime(Start);
            client.SetEvents(first.ArenaEvents);
            object held = AssertOneCurrentOneNext(client, Start, "at " + TimeFormat.Iso8601(Start));
            DateTime boundary = Utc((DateTime)GameClient.Get(held, "EndDate"));

            foreach (int secondsBefore in new[] { 3, 2, 1 })
            {
                DateTime now = boundary.AddSeconds(-secondsBefore);
                clock.UtcNow = now;
                Fetched late = Fetch(game, s, p, now);
                string when = secondsBefore + " s before " + TimeFormat.Iso8601(boundary);
                client.SetTime(now);
                client.SetEvents(late.ArenaEvents);
                List<object> inProgress = client.InProgress();
                Assert.True(inProgress.Count == 1, when + ": " + inProgress.Count + " Special Events in progress");
                Assert.Equal(GameClient.Get(held, "Id"), (string)GameClient.Get(inProgress[0], "Id"));
                client.Update();
                Assert.NotNull(client.TsChoice());
                Assert.Same(client.Current, client.TsChoice());
                Assert.Equal(boundary, Utc((DateTime)GameClient.Get(client.Next, "StartDate")));
                Assert.False((bool)client.UiInfo()["arenaLocked"], when + ": the Special Event box is locked");
                // Served 2 s or less before the change, the document expires at the next window's end and lists the one
                // after it too; earlier, it expires at the change.
                bool reachesPast = secondsBefore <= 2;
                Assert.Equal(reachesPast ? 3 : 2, late.ArenaEvents.Count);
                Assert.True(reachesPast ? late.Expiry > boundary : late.Expiry == boundary, when + ": the CRM expires at " + TimeFormat.Iso8601(late.Expiry));

                // The window changes before the next fetch (the expiry is the next window's end, or the fetch is late).
                client.SetTime(boundary.AddSeconds(1));
                inProgress = client.InProgress();
                Assert.True(inProgress.Count == 1, when + ", then after it: " + inProgress.Count + " Special Events in progress");
                client.Update();
                Assert.Same(inProgress[0], client.Current);
                Assert.Same(client.Current, client.TsChoice());
                Assert.Equal(boundary, Utc((DateTime)GameClient.Get(client.Current, "StartDate")));
                // The client's next event: the one after (listed in the last 2 s), or, until the refetch at the expiry,
                // still the one now in progress (UpdateCurrentAndNextArenaEvent never clears it either).
                if (reachesPast) Assert.True(Utc((DateTime)GameClient.Get(client.Next, "StartDate")) > boundary);
                else Assert.Same(client.Current, client.Next);
                Assert.True(late.Expiry <= Utc((DateTime)GameClient.Get(client.Current, "EndDate")));

                // Back to the window in progress for the next round.
                client.SetEvents(first.ArenaEvents);
                client.SetTime(now);
                client.Update();
            }
        }

        /// <summary>
        /// liveops.arenaRotation pins a mode (and liveops.arenaMap a map): for a fortnight of 6 h windows every Special
        /// Event the real client parses is Competitive Warzone on Port, with its rules (Dominion, best of three, no duplicate
        /// agents, the 1.5 win bonus), and the ArenaParams the TS writes into the room come back unchanged through the
        /// game's Newtonsoft (GameServerComponent deserializes <c>_arenaParams</c>).
        /// </summary>
        [Theory]
        [MemberData(nameof(TestFolders.RealClientFolders), MemberType = typeof(TestFolders))]
        public void APinnedModeStaysFixed(string platform, string folder)
        {
            GameClient game = GameClient.TryGetFrom(output, platform, folder, "Gameloft.Engine", "Game", "Gameloft.Util", "Newtonsoft.Json");
            if (game == null) return;
            game.RegisterGameAssemblies();
            var clock = new ManualClock(Start);
            using var s = new MiscServer(clock);
            s.Set("liveops.arenaRotation", "competitiveWarzone");
            s.Set("liveops.arenaMap", "Port");
            s.Set("liveops.arenaHours", 6L);
            TestPlayer p = s.NewPlayer("pinned");
            var client = new ArenaClient(game, ownedAgent: "1");
            Type jsonConvert = game.Type("Newtonsoft.Json", "Newtonsoft.Json.JsonConvert");
            Type arenaParams = game.Type("Game", "Game.ArenaParams");
            var windows = new HashSet<string>();
            for (int step = 0; step < 14 * 4; step++)
            {
                DateTime now = clock.UtcNow;
                Fetched fetched = Fetch(game, s, p, now);
                client.SetTime(now);
                client.SetEvents(fetched.ArenaEvents);
                object current = AssertOneCurrentOneNext(client, now, "at " + TimeFormat.Iso8601(now));
                foreach (object e in fetched.ArenaEvents)
                {
                    string id = GameClient.Get(e, "Id");
                    Assert.True(LiveOpsSchedule.TryParseArena(id, out ArenaWindow w, out string mode), id);
                    Assert.Equal("competitiveWarzone", mode);
                    Assert.Equal(6, w.Hours);
                    windows.Add(w.Key);
                    Assert.Equal("Arena.CompetitiveWarzone", (string)GameClient.Get(e, "EventName"));
                    Assert.Equal("HUD.Dominion", (string)GameClient.Get(e, "MatchTypeText"));
                    dynamic ap = GameClient.Get(e, "ArenaParams");
                    Assert.Equal("Dominion", ap.MatchType.ToString());
                    Assert.Equal("Port", (string)ap.MapName);
                    Assert.False((bool)ap.AllowDuplicateMercs);
                    Assert.Equal(new[] { "Arena3RoundSystem" }, ((IEnumerable)ap.MatchParametersSets).Cast<string>().ToArray());
                    Assert.Equal(1.5f, (float)ap.EndMatchRewardMultipliers.CreditsMultipliers.VictoryAmountMultiplier);
                    Assert.Equal(1.5f, (float)ap.EndMatchRewardMultipliers.VersusCoinsMultipliers.VictoryAmountMultiplier);
                    Assert.Equal(1f, (float)ap.EndMatchRewardMultipliers.CreditsMultipliers.DefeatAmountMultiplier);
                }
                // GameMatchmakingComponent.AddArenaEventParametersToArgs → room "_arenaParams" → GameServerComponent.
                object tsChoice = client.TsChoice();
                Assert.Same(current, tsChoice);
                string roomArgs = GameClient.CallStatic(jsonConvert, "SerializeObject", GameClient.Get(tsChoice, "ArenaParams"));
                MethodInfo deserialize = jsonConvert.GetMethods().First(m => m.Name == "DeserializeObject" && !m.IsGenericMethod
                                                                            && m.GetParameters().Select(x => x.ParameterType).SequenceEqual(new[] { typeof(string), typeof(Type) }));
                dynamic gs = deserialize.Invoke(null, new object[] { roomArgs, arenaParams });
                Assert.Equal("Dominion", gs.MatchType.ToString());
                Assert.Equal("Port", (string)gs.MapName);
                Assert.Equal(21, ((IEnumerable)gs.RestrictedAgents).Cast<object>().Count());
                Assert.False((bool)gs.ForcedMercModTier);
                clock.Advance(TimeSpan.FromHours(6));
            }
            Assert.Equal(14 * 4 + 1, windows.Count);   // every window once, plus the one after the last
        }

        /// <summary>
        /// The three traps, each shown on the real client and refused by <see cref="TleTemplateValidator"/>: a missing
        /// <c>img</c> throws in ArenaModeEvent.Initialize (which aborts every event on the TS); an "Any" or empty agent list
        /// initializes and GetArenaUIInfo does not throw, but it returns <c>arenaEventRestrictedAgents = null</c> and the
        /// Flash MatchTypeBox.setup then reads its <c>.length</c>; a
        /// "MercClass_" entry throws in Initialize; a gap between windows leaves the client with an event the TS no longer
        /// has (the GS then throws on the missing <c>_arenaParams</c>). The event the server generates passes all of them.
        /// </summary>
        [Theory]
        [MemberData(nameof(TestFolders.RealClientFolders), MemberType = typeof(TestFolders))]
        public void TheValidatorRefusesWhatBreaksTheRealClient(string platform, string folder)
        {
            GameClient game = GameClient.TryGetFrom(output, platform, folder, "Gameloft.Engine", "Game", "Gameloft.Util", "Newtonsoft.Json");
            if (game == null) return;
            game.RegisterGameAssemblies();
            LiveOpsCatalog catalog = LiveOpsCatalog.Parse(LiveOpsContent.Shipped, null);
            var window = new ArenaWindow(Start.AddHours(-3), 24);
            LiveOpsArenaMode havoc = catalog.Arena.Mode("havoc");
            Dictionary<string, object> good = LiveOpsEvents.ArenaMode(catalog, catalog.Arena, window, havoc, "Random");
            var client = new ArenaClient(game, ownedAgent: "3");
            client.SetTime(Start);

            Assert.Null(TleTemplateValidator.Problem(good));
            object ok = client.Parse(good, true);
            client.SetEvents(new List<object> { ok });
            Dictionary<string, object> info = client.UiInfo();
            Assert.False((bool)info["arenaLocked"]);
            Assert.Equal("Arena.Havoc", info["arenaEventName"]);
            Assert.NotNull(info["arenaEventRestrictedAgents"]);

            // 1. No img: Initialize throws (RetrieveEventsAsync rethrows it: no event at all reaches the menu).
            Dictionary<string, object> noImg = Mutate(good, t => t.Remove("img"));
            Assert.Contains("img", TleTemplateValidator.Problem(noImg));
            var thrown = Assert.ThrowsAny<Exception>(() => client.Parse(noImg, true));
            output.WriteLine(platform + ": no img -> " + Inner(thrown).GetType().Name);

            // 2. "Any" / empty: RestrictedAgents is null. GetArenaUIInfo does not throw (its LINQ chain is null-conditional
            //    on both builds) but hands the Flash box arenaEventRestrictedAgents = null, and MatchTypeBox.setup reads
            //    its .length (a TypeError in the lobby). With the server's list it is a list.
            foreach (List<object> agents in new[] { new List<object> { "Any" }, new List<object>(), new List<object> { "Merc_1", "Any_2" } })
            {
                Dictionary<string, object> open = Mutate(good, t => t["AgentRestrictions"] = Private(agents));
                Assert.NotNull(TleTemplateValidator.Problem(open));
                object e = client.Parse(open, true);
                Assert.Null(GameClient.Get(GameClient.Get(e, "ArenaParams"), "RestrictedAgents"));
                client.SetEvents(new List<object> { e });
                Dictionary<string, object> shown = client.UiInfo();
                Assert.Same(e, client.Current);
                Assert.True(shown.ContainsKey("arenaEventRestrictedAgents"));
                Assert.Null(shown["arenaEventRestrictedAgents"]);
            }

            // 3. MercClass_: int.Parse throws inside Initialize.
            Dictionary<string, object> byClass = Mutate(good, t => t["AgentRestrictions"] = Private(new List<object> { "MercClass_Assault" }));
            Assert.Contains("MercClass_", TleTemplateValidator.Problem(byClass));
            Assert.IsType<FormatException>(Inner(Assert.ThrowsAny<Exception>(() => client.Parse(byClass, true))));

            // 4. A gap: the client keeps the ended event (it never clears it) while the TS has none for the room.
            var later = new ArenaWindow(window.End.AddHours(1), 24);
            Dictionary<string, object> afterGap = LiveOpsEvents.ArenaMode(catalog, catalog.Arena, later, catalog.Arena.Mode("tdm"), "Random");
            Assert.Contains("gap", TleTemplateValidator.ArenaChainProblem(new[] { good, afterGap }, Start));
            Assert.Null(TleTemplateValidator.ArenaChainProblem(new[] { good, LiveOpsEvents.ArenaMode(catalog, catalog.Arena, window.Next, havoc, "Random") }, Start));
            client.SetEvents(new List<object> { client.Parse(good, true), client.Parse(afterGap, true) });
            client.SetTime(window.End.AddMinutes(30));
            Assert.NotNull(client.Current);   // still the ended event
            Assert.False((bool)client.UiInfo()["arenaLocked"]);   // the box stays open...
            Assert.Null(client.TsChoice());   // ...but the TS has nothing to put in the room
        }

        // ------------------------------------------------------------------------------------------ helpers

        private sealed class Fetched
        {
            public List<object> ArenaEvents = new List<object>();
            public DateTime Expiry;
        }

        /// <summary>The TS's RetrieveEventsAsync for the CRM document served at <paramref name="now"/>: every listed event
        /// through SocialEvent and ParseTemplate (an exception here would abort the whole list), the Special Events
        /// initialized (CanParticipate from the participation list, as the TS sets it).</summary>
        private static Fetched Fetch(GameClient game, MiscServer s, TestPlayer p, DateTime now)
        {
            Type crmData = game.Op("StandardCrm+Data");
            Type json = game.Type("Gameloft.Util", "Gameloft.Util.Json");
            Type socialEvent = game.Op("SocialEvent");
            string crmText = s.Get("/configs/users/me?access_token=t").BodyText;
            dynamic crm = GameClient.NewCrmData(crmData, crmText);
            List<string> ids = ((IEnumerable)crm.TimeLimitedEvents.Participation).Cast<object>().Select(x => (string)x).ToList();
            var fetched = new Fetched { Expiry = Utc((DateTime)crm.Expiry) };
            Assert.True(fetched.Expiry > now, "the CRM document expires in the future");
            string body = s.Get("/events?event_ids=" + string.Join(",", ids) + "&language=en", p.OnBehalfToken).BodyText;
            var items = ((IEnumerable)((dynamic)GameClient.CallStatic(json, "Load", body)).Children()).Cast<object>().ToList();
            Assert.Equal(ids.Count, items.Count);
            var parser = new ArenaParser(game);
            foreach (object item in items)
            {
                dynamic e = Activator.CreateInstance(socialEvent, item);
                string id = e.Id;
                object tle = parser.Initialize(e, ids.Contains(id), out string eventType);
                if (eventType == LiveOpsEvents.ArenaType) fetched.ArenaEvents.Add(tle);
            }
            return fetched;
        }

        /// <summary>Exactly one Special Event in progress and one in the future on the TS's component; the client's
        /// current one is the TS's choice and the next is the future one; the box is unlocked with both timers.</summary>
        private static object AssertOneCurrentOneNext(ArenaClient client, DateTime now, string when)
        {
            List<object> inProgress = client.InProgress(), future = client.Future();
            Assert.True(inProgress.Count == 1, when + ": " + inProgress.Count + " Special Events in progress");
            Assert.True(future.Count == 1, when + ": " + future.Count + " future Special Events");
            client.Update();
            Assert.Same(inProgress[0], client.Current);
            Assert.Same(inProgress[0], client.TsChoice());
            Assert.Same(future[0], client.Next);
            Assert.Equal(Utc((DateTime)GameClient.Get(inProgress[0], "EndDate")), Utc((DateTime)GameClient.Get(future[0], "StartDate")));
            Dictionary<string, object> info = client.UiInfo();
            Assert.False((bool)info["arenaLocked"], when + ": the Special Event box is locked");
            Assert.Equal((string)GameClient.Get(inProgress[0], "EventName"), info["arenaEventName"]);
            Assert.Equal((string)GameClient.Get(inProgress[0], "EventRule"), info["arenaEventRule"]);
            Assert.Equal((string)GameClient.Get(inProgress[0], "MatchTypeText"), info["arenaEventMatchType"]);
            // A list (here empty: the icons need the merc specs), never null: MatchTypeBox.setup reads its length.
            Assert.IsAssignableFrom<IEnumerable>(info["arenaEventRestrictedAgents"]);
            var left = (TimeSpan)info["arenaEventTimeLeftUntilEnd"];
            var next = (TimeSpan)info["nextArenaEventTimeLeftUntilStart"];
            Assert.True(left > TimeSpan.Zero && Math.Abs((left - next).TotalSeconds) < 5, when + ": timers " + left + " / " + next);
            CheckArenaParams(inProgress[0]);
            return inProgress[0];
        }

        /// <summary>The list fetched before a window change, after it (a late CRM fetch): the event listed as next is in
        /// progress, the client switches to it (OnEventsStarted → UpdateCurrentAndNextArenaEvent), the TS gives it to a
        /// room, and the box stays unlocked with that event's texts.</summary>
        private static void AssertTheNextTookOver(ArenaClient client, DateTime now, string when)
        {
            List<object> inProgress = client.InProgress();
            Assert.True(inProgress.Count == 1, when + ": " + inProgress.Count + " Special Events in progress");
            Assert.True(Utc((DateTime)GameClient.Get(inProgress[0], "StartDate")) <= now);
            client.Update();
            Assert.Same(inProgress[0], client.Current);
            Assert.Same(inProgress[0], client.TsChoice());
            Dictionary<string, object> info = client.UiInfo();
            Assert.False((bool)info["arenaLocked"], when + ": the Special Event box is locked");
            Assert.Equal((string)GameClient.Get(inProgress[0], "EventName"), info["arenaEventName"]);
        }

        /// <summary>What Initialize made of a server event: every agent (21 ids, no restriction in practice), a known
        /// match type, a loadable map, the multipliers, the image path. Also used by LiveOpsConformanceTests.</summary>
        internal static void CheckArenaParams(object tle)
        {
            dynamic ap = GameClient.Get(tle, "ArenaParams");
            Assert.NotNull((object)ap);
            var agents = ((IEnumerable)ap.RestrictedAgents).Cast<int>().ToList();
            Assert.Equal(Enumerable.Range(1, 21), agents);
            Assert.Contains(ap.MatchType.ToString(), TleTemplateValidator.ArenaMatchTypes);
            Assert.True(TleTemplateValidator.IsArenaMap((string)ap.MapName), (string)ap.MapName);
            Assert.False((bool)ap.ForcedMercModTier);
            Assert.True((float)ap.EndMatchRewardMultipliers.CreditsMultipliers.VictoryAmountMultiplier >= 1f);
            dynamic image = GameClient.Get(tle, "Image");
            Assert.StartsWith("Menus/assets/png/", (string)image.Asset);
            Assert.False(string.IsNullOrEmpty((string)GameClient.Get(tle, "EventName")));
            Assert.False(string.IsNullOrEmpty((string)GameClient.Get(tle, "Title")));
        }

        private static Dictionary<string, object> Mutate(Dictionary<string, object> e, Action<Dictionary<string, object>> change)
        {
            Dictionary<string, object> copy = JsonValue.DeepCloneObject(e);
            var template = MiniJson.ParseObject((string)copy["_template"]);
            change((Dictionary<string, object>)template["event_tuning"]);
            copy["_template"] = MiniJson.Serialize(template);
            return copy;
        }

        private static Dictionary<string, object> Private(object value)
        {
            return new Dictionary<string, object> { ["private"] = "1", ["value"] = value };
        }

        private static Exception Inner(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }

        private static DateTime Utc(DateTime value)
        {
            return value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        /// <summary>The TS's per-event steps of RetrieveEventsAsync (ParseTemplate, TypeUtil.GetType, Initialize).</summary>
        internal sealed class ArenaParser
        {
            private readonly GameClient game;
            private readonly object tleComponent;
            private readonly MethodInfo parseTemplate;
            private readonly Type tokenExt;
            private readonly Type typeUtil;

            public ArenaParser(GameClient game)
            {
                this.game = game;
                tleComponent = RuntimeHelpers.GetUninitializedObject(game.Type("Gameloft.Engine", "Gameloft.Engine.TimeLimitedEventsComponent"));
                GC.SuppressFinalize(tleComponent);
                parseTemplate = tleComponent.GetType().GetMethod("ParseTemplate");
                tokenExt = game.Type("Gameloft.Util", "Gameloft.Util.JTokenExtentions");
                typeUtil = game.Type("Gameloft.Engine", "Gameloft.Engine.TypeUtil");
            }

            /// <summary>The initialized event, or null for a type this test does not initialize (only ArenaModeEvent is:
            /// the others are LiveOpsConformanceTests').</summary>
            public object Initialize(dynamic socialEvent, bool canParticipate, out string eventType)
            {
                dynamic fields = socialEvent.Fields;
                dynamic template = parseTemplate.Invoke(tleComponent, new object[] { fields["_template"].ToString(), "en" });
                eventType = GameClient.CallStatic(tokenExt, "SafeGetString", template, "event_type", "");
                Type type = GameClient.CallStatic(typeUtil, "GetType", eventType);
                Assert.True(type != null, "TypeUtil cannot resolve " + eventType);
                if (eventType != LiveOpsEvents.ArenaType) return null;
                object tle = Activator.CreateInstance(type);
                GameClient.Set(tle, "CanParticipate", canParticipate);
                int priority = int.Parse(fields["_priority"].ToString(), CultureInfo.InvariantCulture);
                bool ok = GameClient.Call(tle, "Initialize", socialEvent, priority, template);
                Assert.True(ok, "ArenaModeEvent.Initialize returned false");
                return tle;
            }
        }

        /// <summary>
        /// The client's GameClientComponent in a World of uninitialized game objects: the global components the arena code
        /// asks for (Commander, Client with a Connection whose LastServerTimeDelta sets the server time, Synchronization,
        /// Authentication with a session, the ProfileManager with the player's profile owning one agent), the scene's
        /// MetaGame with a MercsComponent, and the TS's TimeLimitedEventsComponent whose event list the test sets.
        /// </summary>
        internal sealed class ArenaClient
        {
            private readonly GameClient game;
            private readonly object world;
            private readonly object connection;
            private readonly object tle;
            private readonly object client;
            private readonly Type arenaType;
            private readonly Type tleType;
            private readonly List<object> globals = new List<object>();

            public ArenaClient(GameClient game, string ownedAgent)
            {
                this.game = game;
                Type componentType = game.Type("Gameloft.Engine", "Gameloft.Engine.Component");
                Type gameObjectType = game.Type("Gameloft.Engine", "Gameloft.Engine.GameObject");
                arenaType = game.Type("Game", "Game.ArenaModeEvent");
                tleType = game.Type("Gameloft.Engine", "Gameloft.Engine.TimeLimitedEvent");
                world = Uninit(game.Type("Gameloft.Engine", "Gameloft.Engine.World"));

                // World.GetGlobalComponent<T>: a ConcurrentCache over the components below.
                Type cacheType = game.Type("Gameloft.Util", "Gameloft.Util.ConcurrentCache`2").MakeGenericType(typeof(Type), componentType);
                ParameterExpression t = Expression.Parameter(typeof(Type), "t");
                Func<Type, object> lookup = Lookup;
                Delegate factory = Expression.Lambda(typeof(Func<,>).MakeGenericType(typeof(Type), componentType),
                                                     Expression.Convert(Expression.Invoke(Expression.Constant(lookup), t), componentType), t).Compile();
                SetField(world, "globalComponents", Activator.CreateInstance(cacheType, factory));

                object root = NewObject(gameObjectType, "World");
                object Global(string assembly, string typeName)
                {
                    object c = Component(game.Type(assembly, typeName), root);
                    globals.Add(c);
                    return c;
                }

                Global("Gameloft.Engine", "Gameloft.Engine.CommanderComponent");
                object clientComponent = Global("Gameloft.Engine", "Gameloft.Engine.ClientComponent");
                FieldInfo status = Field(componentType, "status");
                SetField(clientComponent, "status", Enum.ToObject(status.FieldType, 1));   // StatusFlags.Enabled: IsClient
                connection = Uninit(game.Type("Gameloft.Engine", "Gameloft.Engine.Connection"));
                object link = Uninit(game.Type("Gameloft.Engine", "Gameloft.Engine.ClientLink"));
                SetField(link, "<Connection>k__BackingField", connection);
                SetField(clientComponent, "currentClientLink", link);
                object sync = Global("Gameloft.Engine", "Gameloft.Engine.SynchronizationComponent");
                SetField(sync, "Client", clientComponent);

                dynamic credential = game.Credential("fed_id:arena-conformance");
                object session = Uninit(game.Op("Session"));
                SetField(session, "<FedId>k__BackingField", (object)credential);
                object auth = Global("Gameloft.Engine", "Gameloft.Engine.AuthenticationComponent");
                SetField(auth, "<Session>k__BackingField", session);

                // MetaGame (MercsComponent) and the profile under the ProfileManager's root.
                object scene = NewObject(gameObjectType, "SceneComponents");
                object metaGame = NewObject(gameObjectType, "MetaGame");
                AddChild(scene, metaGame);
                SetField(world, "sceneComponents", scene);
                Component(game.Type("Game", "Game.MercsComponent"), metaGame);
                object profiles = NewObject(gameObjectType, "Profiles");
                object manager = Global("Game", "Game.ProfileManagerComponent");
                SetField(manager, "managerRoot", profiles);
                object profileObject = NewObject(gameObjectType, (string)credential.ToString());
                AddChild(profiles, profileObject);
                object profile = Component(game.Type("Game", "Game.PlayerProfileComponent"), profileObject);
                Type shortDef = game.Type("Game", "Game.Merc+ShortDef");
                object owned = Activator.CreateInstance(shortDef);
                GameClient.Set(owned, "Id", ownedAgent);
                GameClient.Set(owned, "Locked", false);
                object locked = Activator.CreateInstance(shortDef);
                GameClient.Set(locked, "Id", ownedAgent == "2" ? "3" : "2");
                SetField(profile, "mercs", game.List(shortDef, new[] { owned, locked }));
                // The auto-property's initializer (an empty list on every real profile): the PC build reads it unguarded.
                SetField(profile, "<MercsInTrial>k__BackingField", game.List(game.Type("Game", "Game.MercTrial")));

                tle = Component(game.Type("Gameloft.Engine", "Gameloft.Engine.TimeLimitedEventsComponent"), root);
                client = Component(game.Type("Game", "Game.GameClientComponent"), root);
                SetField(client, "_timeLimitedEvents", tle);
            }

            private object Lookup(Type type)
            {
                foreach (object c in globals)
                    if (type.IsInstanceOfType(c)) return c;
                return null;
            }

            /// <summary>The server time the client and the TS see (Connection.ServerTime = now + LastServerTimeDelta).</summary>
            public void SetTime(DateTime utc)
            {
                SetField(connection, "<LastServerTimeDelta>k__BackingField", (utc - DateTime.UtcNow).Ticks);
            }

            /// <summary>The TS's event list (a fresh RetrieveEventsAsync). The images count as downloading.</summary>
            public void SetEvents(List<object> events)
            {
                foreach (object e in events)
                    GameClient.Set(GameClient.Get(e, "Image"), "IsLoading", true);
                SetField(tle, "eventsList", game.List(tleType, events));
            }

            public object Parse(Dictionary<string, object> e, bool canParticipate)
            {
                Type json = game.Type("Gameloft.Util", "Gameloft.Util.Json");
                dynamic social = Activator.CreateInstance(game.Op("SocialEvent"), (object)GameClient.CallStatic(json, "Load", MiniJson.Serialize(e)));
                object parsed = new ArenaParser(game).Initialize(social, canParticipate, out string _);
                GameClient.Set(GameClient.Get(parsed, "Image"), "IsLoading", true);
                return parsed;
            }

            /// <summary>GameClientComponent.UpdateCurrentAndNextArenaEvent (OnEventsReceived/Started/Finished).</summary>
            public void Update()
            {
                GameClient.Call(client, "UpdateCurrentAndNextArenaEvent");
            }

            /// <summary>GameClientComponent.GetArenaUIInfo: what the Battle popup's Special Event box shows.</summary>
            public Dictionary<string, object> UiInfo()
            {
                Update();
                return GameClient.Call(client, "GetArenaUIInfo");
            }

            public object Current => GameClient.Get(client, "currentArenaEvent");

            public object Next => GameClient.Get(client, "nextArenaEvent");

            public List<object> InProgress() => ((IEnumerable)GameClient.CallGeneric(tle, "GetInProgressEvents", new[] { arenaType })).Cast<object>().ToList();

            public List<object> Future() => ((IEnumerable)GameClient.CallGeneric(tle, "GetFutureEvents", new[] { arenaType })).Cast<object>().ToList();

            /// <summary>GameMatchmakingComponent.AddArenaEventParametersToArgs: the event whose params go into the room.</summary>
            public object TsChoice() => InProgress().FirstOrDefault();

            private object NewObject(Type gameObjectType, string name)
            {
                object o = Uninit(gameObjectType);
                SetField(o, "name", name);
                SetField(o, "world", world);
                SetField(o, "children", Activator.CreateInstance(game.Type("Gameloft.Engine", "Gameloft.Engine.ObservableGameObjectList")));
                SetField(o, "components", Activator.CreateInstance(game.Type("Gameloft.Engine", "Gameloft.Engine.ObservableComponentList")));
                return o;
            }

            private static void AddChild(object parent, object child)
            {
                ((IList)Get(parent, "children")).Add(child);
            }

            private static object Component(Type type, object gameObject)
            {
                object c = Uninit(type);
                SetField(c, "<GameObject>k__BackingField", gameObject);
                ((IList)Get(gameObject, "components")).Add(c);
                return c;
            }

            private static object Uninit(Type type)
            {
                object o = RuntimeHelpers.GetUninitializedObject(type);
                GC.SuppressFinalize(o);   // World and GameObject finalizers release native state they never had
                return o;
            }

            private static FieldInfo Field(Type type, string name)
            {
                for (Type t = type; t != null; t = t.BaseType)
                {
                    FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (f != null) return f;
                }
                throw new MissingFieldException(type.FullName, name);
            }

            private static object Get(object target, string field)
            {
                return Field(target.GetType(), field).GetValue(target);
            }

            private static void SetField(object target, string field, object value)
            {
                Field(target.GetType(), field).SetValue(target, value);
            }
        }
    }
}
