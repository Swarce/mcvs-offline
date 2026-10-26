using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm.Collector;
using McvsOffline.Server.Services.Social.LiveOps;
using McvsOffline.Tests.Conformance;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Misc
{
    /// <summary>
    /// Every live-ops event the server generates, run through the REAL client code the TS runs on it
    /// (EN/TimeLimitedEventsComponent.cs RetrieveEventsAsync, step by step): the CRM document through
    /// <c>StandardCrm.Data</c> (participation ids, expiry), the events answer through <c>Json.Load</c> and
    /// <c>new SocialEvent(item)</c>, the template through <c>ParseTemplate</c>, the type through <c>TypeUtil.GetType</c>,
    /// then the type's own <c>Initialize</c> (DivisionSeasonEvent, TimeLimitedAchievementEvent,
    /// TimeLimitedDailyQuestEvent, which create every reward with <c>RewardHelper.CreateRewardItem</c>), and the checks
    /// the TS later relies on without guarding (titles, milestones, rewards, the daily-quest pool selection).
    /// The run's liveops.json is an override whose <c>collector.since</c> falls inside it, so it covers the events
    /// without and with the Cosmetic crate (docs/design/economy-collector.md §3.2).
    /// Parsing runs under a day-first culture (de-DE), as on a European phone.
    /// Skipped without the player's assemblies (MCVS_REFS), mandatory with MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    public class LiveOpsConformanceTests
    {
        /// <summary>The run's <c>collector.since</c> (a content override): Monday 2026-10-26, three days into the run.</summary>
        private static readonly DateTime Since = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);

        private readonly ITestOutputHelper output;

        public LiveOpsConformanceTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Theory]
        [InlineData(7)]
        [InlineData(1)]   // liveops.seasonDays 1: a season (and its weekly event) a day, bare "yyyyMMddHH" keys
        public void EveryGeneratedEventInitializesInTheRealClient(int seasonDays)
        {
            GameClient game = GameClient.TryGet(output, "Gameloft.Engine", "Game", "Gameloft.Util");
            if (game == null) return;
            game.RegisterGameAssemblies();
            // TimeLimitedAchievementEvent.Initialize tags every reward with GameTrackingComponent.GetTleInfo, whose
            // static constructor calls two tracking functions of libnativeengine.so. They are answered by a stub
            // compiled here (the game's native code is never run).
            string stub = NativeTrackingStub.Install(game.Assembly("Gameloft.Engine"), output);
            if (stub == null)
            {
                Assert.False(GameClient.Required, "no C compiler for the native tracking stub; the achievement events cannot be initialized");
                return;
            }

            Type json = game.Type("Gameloft.Util", "Gameloft.Util.Json");
            Type tokenExt = game.Type("Gameloft.Util", "Gameloft.Util.JTokenExtentions");
            Type typeUtil = game.Type("Gameloft.Engine", "Gameloft.Engine.TypeUtil");
            Type crmData = game.Op("StandardCrm+Data");
            Type socialEvent = game.Op("SocialEvent");
            object tleComponent = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                game.Type("Gameloft.Engine", "Gameloft.Engine.TimeLimitedEventsComponent"));
            MethodInfo parseTemplate = tleComponent.GetType().GetMethod("ParseTemplate");
            Type rewardItem = game.Type("Game", "Game.RewardItem");

            CultureInfo saved = CultureInfo.CurrentCulture;
            CultureInfo dayFirst = TryCulture("de-DE");
            var clock = new ManualClock(new DateTime(2026, 10, 23, 15, 0, 0, DateTimeKind.Utc));
            using var s = new MiscServer(clock);
            s.Set("liveops.seasonDays", (long)seasonDays);
            // The Cosmetic crate from Monday 2026-10-26, the fourth day of the run.
            LiveOpsTests.OverrideContent(s, LiveOpsCatalog.ContentName, LiveOpsContent.WithSince(Since));
            LiveOpsCollectorRewards collector = LiveOpsService.For(s.Context).Catalog.Collector;
            Assert.True(collector != null && collector.Since == Since, "the override's collector block was not read");
            TestPlayer p = s.NewPlayer("conformance");
            var checkedIds = new HashSet<string>();
            var types = new Dictionary<string, int>();
            var crates = new Dictionary<string, int>();
            try
            {
                // 16 days from a Friday: two season changes, every daily rotation step of two weeks, the last-day
                // announcement of the next weekly event, and one hour after a daily reset.
                for (int step = 0; step < 16; step++)
                {
                    CultureInfo.CurrentCulture = dayFirst;
                    string crmText = s.Get("/configs/users/me?access_token=t").BodyText;
                    dynamic crm = Activator.CreateInstance(crmData, crmText);
                    List<string> ids = ((IEnumerable)crm.TimeLimitedEvents.Participation).Cast<object>().Select(x => (string)x).ToList();
                    LiveOpsSnapshot snap = LiveOpsService.For(s.Context).Snapshot(clock.UtcNow);
                    Assert.Equal(snap.EventIds, ids);
                    Assert.Equal(snap.Expiry.Value, Utc((DateTime)crm.Expiry));   // StandardCrm.Data.DeserializeExpiry (SafeGetDateTime)

                    string body = s.Get("/events?event_ids=" + string.Join(",", ids) + "&language=en", p.OnBehalfToken).BodyText;
                    dynamic token = GameClient.CallStatic(json, "Load", body);
                    var items = ((IEnumerable)token.Children()).Cast<object>().ToList();
                    Assert.Equal(ids.Count, items.Count);
                    foreach (object item in items)
                    {
                        dynamic e = Activator.CreateInstance(socialEvent, item);
                        string id = e.Id;
                        Assert.Contains(id, ids);
                        var raw = MiniJson.ParseObject(item.ToString());
                        Assert.True(TimeFormat.TryParse((string)raw["start_date"], out DateTime start));
                        Assert.True(TimeFormat.TryParse((string)raw["end_date"], out DateTime end));
                        Assert.Equal(start, Utc((DateTime)e.StartDate));
                        Assert.Equal(end, Utc((DateTime)e.EndDate));
                        Assert.Equal(int.Parse((string)raw["_version"], CultureInfo.InvariantCulture), (int)e.Revision);
                        if (!checkedIds.Add(id + "|" + raw["_version"])) continue;

                        // RetrieveEventsAsync: ParseTemplate(item.Fields["_template"].Value<string>(), language)
                        dynamic fields = e.Fields;
                        string templateText = fields["_template"].ToString();
                        dynamic template = parseTemplate.Invoke(tleComponent, new object[] { templateText, "en" });
                        string eventType = GameClient.CallStatic(tokenExt, "SafeGetString", template, "event_type", "");
                        Type type = GameClient.CallStatic(typeUtil, "GetType", eventType);
                        Assert.True(type != null, id + ": TypeUtil cannot resolve " + eventType);
                        object tle = Activator.CreateInstance(type);
                        int priority = int.Parse(fields["_priority"].ToString(), CultureInfo.InvariantCulture);
                        bool ok = GameClient.Call(tle, "Initialize", e, priority, template);
                        Assert.True(ok, id + ": " + eventType + ".Initialize returned false");
                        Assert.Equal(0, (int)GameClient.Get(tle, "TemplateVersion")); // kept: <= TimeLimitedEventsComponent.TemplateVersion (0)
                        types[eventType] = types.TryGetValue(eventType, out int n) ? n + 1 : 1;

                        if (eventType == "DivisionSeasonEvent")
                        {
                            CheckDivision(tle, raw);
                        }
                        else
                        {
                            CheckAchievements(tle, eventType, rewardItem);
                            bool withCrate = CheckRewards(tle, eventType, raw, start >= Since);
                            string kind = eventType + (withCrate ? " with" : " without") + " the Cosmetic crate";
                            crates[kind] = crates.TryGetValue(kind, out int c) ? c + 1 : 1;
                        }
                    }
                    clock.Advance(TimeSpan.FromHours(step == 3 ? 23 : 24));
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
            foreach (var kv in types) output.WriteLine(kv.Key + ": " + kv.Value + " distinct events initialized");
            foreach (var kv in crates) output.WriteLine(kv.Key + ": " + kv.Value);
            Assert.True(types.ContainsKey("DivisionSeasonEvent") && types["DivisionSeasonEvent"] >= 17 * 4);
            Assert.True(types.ContainsKey("TimeLimitedDailyQuestEvent") && types["TimeLimitedDailyQuestEvent"] >= 16);
            Assert.True(types.ContainsKey("TimeLimitedAchievementEvent") && types["TimeLimitedAchievementEvent"] >= 3);
            int Crates(string kind) => crates.TryGetValue(kind, out int n) ? n : 0;
            Assert.True(Crates("TimeLimitedDailyQuestEvent without the Cosmetic crate") >= 3);   // 10-23 … 10-25
            Assert.True(Crates("TimeLimitedDailyQuestEvent with the Cosmetic crate") >= 12);     // 10-26 … 11-08
            Assert.True(Crates("TimeLimitedAchievementEvent without the Cosmetic crate") >= 1);  // the season before since
            Assert.True(Crates("TimeLimitedAchievementEvent with the Cosmetic crate") >= 2);

            // RewardHelper.CreateRewardItem("Crate_Event_Special", n), as Initialize calls it: a crate of n.
            Type helper = game.Type("Game", "Game.RewardHelper");
            Type source = game.Type("Game", "Game.RewardItem+TrackingSource");
            MethodInfo create = helper.GetMethod("CreateRewardItem", new[] { typeof(string), typeof(int), source });
            foreach (int n in new[] { 1, 2, 5 })
            {
                object item = create.Invoke(null, new object[] { CollectorGuard.CrateId, n, Activator.CreateInstance(source) });
                Assert.Equal("Game.CrateRewardItem", item?.GetType().FullName);
                Assert.Equal(n, (int)GameClient.Get(item, "Quantity"));
                Assert.Equal(CollectorGuard.CrateId, (string)GameClient.Get(item, "Id"));
            }
            output.WriteLine("parsed under " + (dayFirst.Name.Length == 0 ? "a day-first copy of the invariant culture" : dayFirst.Name)
                             + " (dates as " + new DateTime(2026, 10, 5).ToString(dayFirst) + ")");
        }

        /// <summary>
        /// Why the dates have no ISO 'T' (LiveOpsSchedule.WireDate): with one, Newtonsoft turns the value into a DateTime
        /// token and SocialEvent re-reads it through the current culture.
        /// </summary>
        [Fact]
        public void IsoDatesWouldDependOnThePhonesCulture()
        {
            GameClient game = GameClient.TryGet(output, "Gameloft.Util");
            if (game == null) return;
            CultureInfo dayFirst = TryCulture("de-DE");
            Type json = game.Type("Gameloft.Util", "Gameloft.Util.Json");
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = dayFirst;
                dynamic iso = Activator.CreateInstance(game.Op("SocialEvent"), (object)GameClient.CallStatic(json, "Load", "{\"id\":\"a\",\"start_date\":\"2026-10-05T00:00:00Z\"}"));
                dynamic wire = Activator.CreateInstance(game.Op("SocialEvent"), (object)GameClient.CallStatic(json, "Load", "{\"id\":\"a\",\"start_date\":\"2026-10-05 00:00:00Z\"}"));
                output.WriteLine("ISO 'T' date under de-DE reads as " + ((DateTime)iso.StartDate).ToString("o", CultureInfo.InvariantCulture)
                                 + ", the wire format as " + ((DateTime)wire.StartDate).ToString("o", CultureInfo.InvariantCulture));
                Assert.Equal(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), Utc((DateTime)wire.StartDate));
                Assert.NotEqual(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), Utc((DateTime)iso.StartDate));
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        private static void CheckDivision(object tle, Dictionary<string, object> raw)
        {
            string participation = (string)raw["_participation"];
            int division = int.Parse(participation.Substring(participation.IndexOf("== ", StringComparison.Ordinal) + 3).TrimEnd(')'), CultureInfo.InvariantCulture);
            Assert.Equal(division, (int)GameClient.Get(tle, "Division"));
            Assert.Equal(raw["_batch_tag"], (string)GameClient.Get(tle, "SeasonId"));
            Assert.Equal(JsonPath.Get(raw, "tournament.leaderboard.name"), (string)GameClient.Get(tle, "LeaderboardId"));
            Assert.Equal("None", GameClient.Get(tle, "LeaderboardType").ToString());
            // DivisionData.LeaderboardId (QA profile) and the leaderboard service must agree on the name.
            string qa = ((string)GameClient.Get(tle, "LeaderboardId")).Replace("Leaderboard", "Leaderboard_QA");
            Assert.True(LiveOpsSchedule.TryParseBoard(qa, out LiveOpsWindow season, out int d));
            Assert.Equal(division, d);
        }

        /// <summary>
        /// Every reward of the template became a RewardItem with its id and quantity, in order (Initialize calls
        /// <c>RewardHelper.CreateRewardItem(item, quantity, source)</c> for each); in a window from <c>collector.since</c>
        /// the Cosmetic crate is a <c>Game.CrateRewardItem</c> after today's rewards in the daily bonus, in every milestone
        /// of every weekly objective and in the weekly final, and nowhere else; before it, nowhere. True when it was there.
        /// </summary>
        private static bool CheckRewards(object tle, string eventType, Dictionary<string, object> raw, bool crateWindow)
        {
            var template = MiniJson.ParseObject((string)raw["_template"]);
            var expected = ((List<object>)JsonPath.Get(template, "milestones.value")).Cast<Dictionary<string, object>>().ToList();
            var achievements = ((IEnumerable)GameClient.Get(tle, "Achievements")).Cast<object>().ToList();
            Assert.Equal(expected.Count, achievements.Count);
            bool weekly = eventType == "TimeLimitedAchievementEvent";
            int found = 0, all = 0;
            for (int i = 0; i < achievements.Count; i++)
            {
                string tracker = GameClient.Get(achievements[i], "MainTracker").ToString();
                Assert.Equal((string)expected[i]["primary_tracker"], tracker);
                var milestones = ((IEnumerable)GameClient.Get(achievements[i], "Milestones")).Cast<object>().ToList();
                var wanted = ((List<object>)expected[i]["milestones"]).Cast<Dictionary<string, object>>().ToList();
                Assert.Equal(wanted.Count, milestones.Count);
                all += milestones.Count;
                bool final = tracker == LiveOpsEvents.CompleteAll;
                for (int m = 0; m < milestones.Count; m++)
                {
                    var rewards = ((IEnumerable)GameClient.Get(milestones[m], "Rewards")).Cast<object>().ToList();
                    var want = ((List<object>)wanted[m]["rewards"]).Cast<Dictionary<string, object>>().ToList();
                    Assert.Equal(want.Count, rewards.Count);
                    for (int r = 0; r < rewards.Count; r++)
                    {
                        Assert.Equal((string)want[r]["item"], (string)GameClient.Get(rewards[r], "Id"));
                        Assert.Equal((long)want[r]["quantity"], (long)(int)GameClient.Get(rewards[r], "Quantity"));
                    }
                    var crates = rewards.Where(r => (string)GameClient.Get(r, "Id") == CollectorGuard.CrateId).ToList();
                    if (!crateWindow || !(weekly || final))
                    {
                        Assert.Empty(crates);
                        continue;
                    }
                    object crate = Assert.Single(crates);
                    Assert.Equal("Game.CrateRewardItem", crate.GetType().FullName);
                    Assert.Same(crate, rewards.Last());
                    Assert.Equal(weekly && final ? 2 : 1, (int)GameClient.Get(crate, "Quantity"));   // §3.1: 1 per tier and bonus, 2 in the final
                    found++;
                }
            }
            // The daily bonus alone; or every milestone of the weekly event (each objective's tiers and the final).
            if (crateWindow) Assert.Equal(weekly ? all : 1, found);
            return found > 0;
        }

        private static void CheckAchievements(object tle, string eventType, Type rewardItem)
        {
            string title = GameClient.Get(tle, "Title");
            Assert.NotNull(title);
            // OnTimeLimitedEventsReceived keys daily quests on this substring; the UI on "DailyQuest".
            Assert.Equal(eventType == "TimeLimitedDailyQuestEvent", title.Contains("DailyQuestTitle"));
            Assert.NotNull(GameClient.Get(tle, "Description"));
            dynamic banner = GameClient.Get(tle, "ImgBanner");
            dynamic menu = GameClient.Get(tle, "ImgMenu");
            Assert.False(string.IsNullOrEmpty((string)banner.Asset));
            Assert.False(string.IsNullOrEmpty((string)menu.Asset));
            Assert.False((bool)GameClient.Get(tle, "IsMegaEvent"));
            Assert.False((bool)GameClient.Get(tle, "HasUnlockCost"));
            Assert.Equal(0, (int)GameClient.Get(tle, "UserDuration"));

            var achievements = ((IEnumerable)GameClient.Get(tle, "Achievements")).Cast<object>().ToList();
            Assert.True(achievements.Count >= 2);
            int otherMilestones = 0;
            object completeAll = null;
            foreach (object a in achievements)
            {
                string tracker = GameClient.Get(a, "MainTracker").ToString();
                var milestones = ((IEnumerable)GameClient.Get(a, "Milestones")).Cast<object>().ToList();
                Assert.NotEmpty(milestones);
                Assert.False(string.IsNullOrEmpty((string)GameClient.Get(a, "LocId")));
                Assert.False(string.IsNullOrEmpty((string)GameClient.Get(a, "LocIdPlural")));
                int previous = 0;
                foreach (object m in milestones)
                {
                    int goal = GameClient.Get(m, "Goal");
                    Assert.True(goal > previous);
                    previous = goal;
                    var rewards = ((IEnumerable)GameClient.Get(m, "Rewards")).Cast<object>().ToList();
                    Assert.NotEmpty(rewards);
                    // A null RewardItem would throw in ClaimAchievementMilestoneRequest (reward.Claim).
                    Assert.All(rewards, r => Assert.True(r != null && rewardItem.IsInstanceOfType(r)));
                }
                if (tracker == "CompleteAllObjectives") completeAll = a;
                else otherMilestones += milestones.Count;
            }
            Assert.NotNull(completeAll);
            var final = ((IEnumerable)GameClient.Get(completeAll, "Milestones")).Cast<object>().ToList();
            Assert.Single(final);
            Assert.Equal(otherMilestones, (int)GameClient.Get(final[0], "Goal"));
            Assert.Equal(0, (int)GameClient.Call(tle, "GetPercentageProgression")); // divides by the milestone count
            if (eventType == "TimeLimitedDailyQuestEvent")
            {
                Assert.Null(GameClient.Get(tle, "PoolAchievementIndexes")); // a flat list: no random pick on the TS
                GameClient.Call(tle, "SelectRandomAchievementInPool");
                Assert.Equal(achievements.Count, ((IEnumerable)GameClient.Get(tle, "Achievements")).Cast<object>().Count());
            }
        }

        /// <summary>
        /// A two-function stand-in for libnativeengine.so's tracking entry points used by static constructors. Internal:
        /// other real-client tests that initialize achievement events install it too
        /// (<c>LiveOpsConformanceTests.NativeTrackingStub.Install(engine, output)</c>; once per process).
        /// </summary>
        internal static class NativeTrackingStub
        {
            private static readonly object Gate = new object();
            private static string installed;

            public static string Install(Assembly engine, ITestOutputHelper output)
            {
                lock (Gate)
                {
                    if (installed != null) return installed;
                    string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcvs-nativeengine-stub-" + Environment.ProcessId);
                    System.IO.Directory.CreateDirectory(dir);
                    string source = System.IO.Path.Combine(dir, "stub.c");
                    string library = System.IO.Path.Combine(dir, "libnativeengine_stub.so");
                    System.IO.File.WriteAllText(source,
                        "unsigned char TrackingManager_IsInFirstLaunch(void) { return 0; }\n" +
                        "void TrackingManager_TrackLoadingTimeStarted(unsigned int id, unsigned char pause) { (void)id; (void)pause; }\n" +
                        "void TrackingManager_TrackLoadingTimeFinished(unsigned int id) { (void)id; }\n");
                    foreach (string cc in new[] { "cc", "gcc", "clang" })
                    {
                        try
                        {
                            var psi = new System.Diagnostics.ProcessStartInfo(cc, "-shared -fPIC -o \"" + library + "\" \"" + source + "\"")
                            {
                                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
                            };
                            using var process = System.Diagnostics.Process.Start(psi);
                            process.WaitForExit(60000);
                            if (process.ExitCode == 0 && System.IO.File.Exists(library)) break;
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                        }
                    }
                    if (!System.IO.File.Exists(library))
                    {
                        output.WriteLine("no C compiler: native tracking stub not built");
                        return null;
                    }
                    try
                    {
                        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(engine, (name, asm, path) =>
                            name == "nativeengine" ? System.Runtime.InteropServices.NativeLibrary.Load(library) : IntPtr.Zero);
                    }
                    catch (InvalidOperationException)
                    {
                        // A resolver is already set for this assembly in this process.
                    }
                    installed = library;
                    return installed;
                }
            }
        }

        private static DateTime Utc(DateTime value)
        {
            return value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        /// <summary>The named day-first culture, or (invariant globalization, as in CI) a copy of the invariant culture
        /// with German date patterns, which is all JValue.ToString() and DateTime.TryParse look at.</summary>
        private static CultureInfo TryCulture(string name)
        {
            try
            {
                var c = new CultureInfo(name);
                if (c.DateTimeFormat.ShortDatePattern.StartsWith("dd", StringComparison.Ordinal)) return c;
            }
            catch (CultureNotFoundException)
            {
            }
            var synthetic = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            synthetic.DateTimeFormat.DateSeparator = ".";
            synthetic.DateTimeFormat.ShortDatePattern = "dd.MM.yyyy";
            synthetic.DateTimeFormat.LongTimePattern = "HH:mm:ss";
            return synthetic;
        }
    }
}
