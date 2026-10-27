using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm.Collector;
using McvsOffline.Server.Services.Social.LiveOps;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Content
{
    /// <summary>
    /// The one predicate (docs/design/economy-collector.md §2.12): <see cref="CollectorRuntime.Status"/> for every
    /// combination of its inputs, and <see cref="CollectorRuntime.CratesInWindow"/> never handing out a crate the document
    /// at that window's start would not carry.
    /// </summary>
    public class CollectorRuntimeTests
    {
        private static readonly DateTime Shipped = CollectorGuard.ShippedSince;

        private readonly ITestOutputHelper output;

        public CollectorRuntimeTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        public enum ContentState { Valid, Missing, Broken }

        /// <summary>Content served from memory, so a test can also make collector.json missing.</summary>
        private sealed class MemoryContent : IContentProvider
        {
            public readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>Thrown by the content reads while set (as ContentProvider throws when the override changes
            /// between its File.Exists and its read).</summary>
            public Exception Fail;

            public DataPack DataPack => null;

            public List<string> ContentNames => Files.Keys.ToList();

            public bool TryReadGameFile(string logicalPath, out byte[] data)
            {
                data = null;
                return false;
            }

            public bool TryReadGameText(string logicalPath, out string text)
            {
                text = null;
                return false;
            }

            public bool TryReadContent(string name, out byte[] data)
            {
                if (Fail != null) throw Fail;
                data = Files.TryGetValue(name, out string text) ? System.Text.Encoding.UTF8.GetBytes(text) : null;
                return data != null;
            }

            public bool TryReadContentText(string name, out string text)
            {
                if (Fail != null) throw Fail;
                return Files.TryGetValue(name, out text);
            }
        }

        private sealed class Fixture : IDisposable
        {
            public readonly TempDir Dir = new TempDir();
            public readonly MemoryContent Content = new MemoryContent();
            public readonly MemoryLogSink Logs = new MemoryLogSink();
            public readonly ServerContext Context;

            public Fixture(IContentProvider content = null)
            {
                var logger = new Logger();
                logger.AddSink(Logs);
                Context = new ServerContext(Dir.Path, new Settings(), logger, new ManualClock(Shipped), content ?? Content);
                Set(ContentState.Valid);
            }

            public CollectorRuntime Runtime => CollectorRuntime.For(Context);

            public void Set(ContentState state)
            {
                if (state == ContentState.Missing) Content.Files.Remove(CollectorContent.FileName);
                else Content.Files[CollectorContent.FileName] = state == ContentState.Valid ? CollectorPlanTests.ShippedJson : BrokenJson;
            }

            public void Set(bool? storePatch)
            {
                Context.Features.Set(new ClientCapabilities { StorePatch = storePatch });
            }

            public void Enable(bool enabled)
            {
                Context.Settings.Override("economy.collector.enabled", enabled);
            }

            public void Dispose()
            {
                Dir.Dispose();
            }
        }

        /// <summary>The shipped file with a sound row that has weight (§2.3.1): parses as JSON, refused all the same.</summary>
        private static string BrokenJson
        {
            get
            {
                var doc = MiniJson.ParseObject(CollectorPlanTests.ShippedJson);
                var row = (Dictionary<string, object>)((List<object>)doc["rows"])[2];
                row["subtype"] = "Sound";
                row["subtype2"] = "2";
                row.Remove("bundle");
                return MiniJson.Serialize(doc);
            }
        }

        private static readonly DateTime?[] Sinces = { null, Shipped, Shipped.AddDays(-14), Shipped.AddDays(14) };

        private static IEnumerable<DateTime> Moments()
        {
            foreach (DateTime boundary in new[] { Shipped.AddDays(-14), Shipped, Shipped.AddDays(14) })
            {
                yield return boundary.AddSeconds(-1);
                yield return boundary;
                yield return boundary.AddHours(36);
                // guardSince = since − Lead
                yield return boundary - CollectorGuard.Lead - TimeSpan.FromSeconds(1);
                yield return boundary - CollectorGuard.Lead;
            }
            yield return Shipped.AddDays(-60);
            yield return Shipped.AddDays(60);
        }

        /// <summary>§2.12 written out once more, as the test's own reading of the design.</summary>
        private static CollectorPhase Expected(bool enabled, bool liveOps, ContentState content, bool? patch, DateTime? since, DateTime now)
        {
            bool active = enabled && liveOps && content == ContentState.Valid && patch != false && since.HasValue && now >= since.Value;
            if (active) return CollectorPhase.Active;
            DateTime guardSince = (since.HasValue && since.Value < Shipped ? since.Value : Shipped).AddDays(-1);
            return now >= guardSince ? CollectorPhase.Guard : CollectorPhase.Dormant;
        }

        /// <summary>
        /// The phase table: enabled × live ops × content valid/missing/broken × store patch true/false/null × since
        /// unknown/shipped/earlier/later × moments before and after since and guardSince.
        /// </summary>
        [Fact]
        public void PhaseTableCoversEveryCombination()
        {
            using var f = new Fixture();
            int cases = 0;
            var seen = new HashSet<CollectorPhase>();
            foreach (bool enabled in new[] { true, false })
            {
                f.Enable(enabled);
                foreach (ContentState content in Enum.GetValues(typeof(ContentState)))
                {
                    f.Set(content);
                    foreach (bool? patch in new bool?[] { true, false, null })
                    {
                        f.Set(patch);
                        foreach (bool liveOps in new[] { true, false })
                            foreach (DateTime? since in Sinces)
                                foreach (DateTime now in Moments())
                                {
                                    CollectorStatus status = f.Runtime.Status(now, liveOps, since);
                                    CollectorPhase expected = Expected(enabled, liveOps, content, patch, since, now);
                                    Assert.True(expected == status.Phase,
                                                $"enabled={enabled} liveOps={liveOps} content={content} patch={patch} since={since:u} now={now:u}: "
                                                + $"expected {expected}, got {status.Phase} ({status.Reason})");
                                    Assert.False(string.IsNullOrEmpty(status.Reason));
                                    Assert.Equal(since, status.Since);
                                    seen.Add(status.Phase);
                                    cases++;
                                }
                    }
                }
            }
            output.WriteLine(cases + " cases");
            Assert.Equal(3, seen.Count);
        }

        /// <summary>The table's landmarks, with their reasons (shown in <c>GET _mcvs/crm</c>).</summary>
        [Fact]
        public void PhaseReasonsNameTheFirstMissingCondition()
        {
            using var f = new Fixture();
            CollectorRuntime r = f.Runtime;
            DateTime later = Shipped.AddDays(14);

            CollectorStatus s = r.Status(Shipped, true, Shipped);
            Assert.Equal(CollectorPhase.Active, s.Phase);
            Assert.Equal("active since 2026-11-09T00:00:00Z", s.Reason);
            // The day before since: the guard (live ops already list that day's quests and announce the week).
            s = r.Status(Shipped.AddSeconds(-1), true, Shipped);
            Assert.Equal(CollectorPhase.Guard, s.Phase);
            Assert.Equal("before collector.since 2026-11-09T00:00:00Z", s.Reason);
            Assert.Equal(CollectorPhase.Guard, r.Status(Shipped.AddDays(-1), true, Shipped).Phase);
            s = r.Status(Shipped.AddDays(-1).AddSeconds(-1), true, Shipped);
            Assert.Equal(CollectorPhase.Dormant, s.Phase);
            Assert.Equal("before collector.since 2026-11-09T00:00:00Z (guard from 2026-11-08T00:00:00Z)", s.Reason);
            Assert.Equal(CollectorPhase.Active, r.Status(Shipped.AddDays(-13), true, Shipped.AddDays(-14)).Phase);   // an earlier since
            Assert.Equal(CollectorPhase.Guard, r.Status(Shipped.AddDays(-15), true, Shipped.AddDays(-14)).Phase);
            Assert.Equal(CollectorPhase.Dormant, r.Status(Shipped.AddDays(-15).AddSeconds(-1), true, Shipped.AddDays(-14)).Phase);

            // A later since: the guard still starts at the shipped since (guardSince = min).
            s = r.Status(Shipped.AddDays(1), true, later);
            Assert.Equal(CollectorPhase.Guard, s.Phase);
            Assert.Equal("before collector.since 2026-11-23T00:00:00Z", s.Reason);
            Assert.Equal(CollectorPhase.Active, r.Status(later, true, later).Phase);

            s = r.Status(Shipped.AddDays(1), true, null);
            Assert.Equal(CollectorPhase.Guard, s.Phase);
            Assert.Contains("no collector.since", s.Reason);
            s = r.Status(Shipped.AddDays(-2), true, null);
            Assert.Equal(CollectorPhase.Dormant, s.Phase);
            Assert.Contains("guard from 2026-11-08T00:00:00Z", s.Reason);

            Assert.Equal("live ops are off", r.Status(Shipped.AddDays(1), false, Shipped).Reason);

            f.Set(false);
            s = r.Status(Shipped.AddDays(1), true, Shipped);
            Assert.Equal(CollectorPhase.Guard, s.Phase);
            Assert.Equal("store patch missing", s.Reason);
            f.Set((bool?)null);
            Assert.Equal(CollectorPhase.Active, r.Status(Shipped.AddDays(1), true, Shipped).Phase);   // unknown: assumed present

            f.Set(ContentState.Broken);
            s = r.Status(Shipped.AddDays(1), true, Shipped);
            Assert.Equal(CollectorPhase.Guard, s.Phase);
            Assert.StartsWith("collector.json: row 3: ", s.Reason);
            f.Set(ContentState.Missing);
            Assert.StartsWith("collector.json: missing", r.Status(Shipped.AddDays(1), true, Shipped).Reason);
            f.Set(ContentState.Valid);

            f.Enable(false);
            s = r.Status(Shipped.AddDays(1), true, Shipped);
            Assert.Equal(CollectorPhase.Guard, s.Phase);
            Assert.Equal("economy.collector.enabled is false", s.Reason);
        }

        /// <summary>
        /// <c>CratesInWindow</c> true ⇒ <c>Status</c> Active at that window's start (and for the rest of the window), for
        /// every input combination, quest-day reset hour and season grid; and the layer the document then carries has a
        /// typed, weighted crate row, so a crate live ops hand out can always be opened.
        /// </summary>
        [Fact]
        public void EveryCrateWindowHasRows()
        {
            using var f = new Fixture();
            // The quest days and seasons of several grids around the shipped since (each window once).
            var windows = new List<LiveOpsWindow>();
            var keys = new HashSet<string>();
            foreach (var (days, hour, anchor) in new[] { (7, 0, (string)null), (7, 5, null), (1, 23, null), (14, 0, null), (7, 0, "2026-11-11T00:00:00Z") })
            {
                var settings = new Settings();
                settings.Override("liveops.seasonDays", (long)days);
                settings.Override("liveops.dailyResetHour", (long)hour);
                if (anchor != null) settings.Override("liveops.seasonStart", anchor);
                LiveOpsSettings s = LiveOpsSettings.From(settings, Log.Null);
                for (DateTime t = Shipped.AddDays(-21); t < Shipped.AddDays(35); t = t.AddHours(1))
                    foreach (LiveOpsWindow w in new[] { LiveOpsSchedule.DayAt(s, t), LiveOpsSchedule.SeasonAt(s, t) })
                        if (keys.Add(w.Key)) windows.Add(w);
            }
            int withCrates = 0;
            foreach (bool enabled in new[] { true, false })
            {
                f.Enable(enabled);
                foreach (ContentState content in Enum.GetValues(typeof(ContentState)))
                {
                    f.Set(content);
                    foreach (bool? patch in new bool?[] { true, false, null })
                    {
                        f.Set(patch);
                        foreach (DateTime? since in Sinces)
                        {
                            foreach (LiveOpsWindow w in windows)
                            {
                                if (!f.Runtime.CratesInWindow(w.Start, since)) continue;
                                withCrates++;
                                foreach (DateTime at in new[] { w.Start, w.Start.AddHours(1), w.End.AddSeconds(-1) })
                                {
                                    CollectorStatus status = f.Runtime.Status(at, true, since);
                                    Assert.True(status.Phase == CollectorPhase.Active,
                                                $"window {w.Key} hands out crates (since {since:u}) but the document at {at:u} is {status.Phase}: {status.Reason}");
                                }
                            }
                        }
                        if (enabled && content == ContentState.Valid && patch != false)
                        {
                            // The crate live ops hand out is the guard's Crate_Event_Special (whatever collector.json says):
                            // its own content table gets typed, weighted rows and its LootCrates row the item count.
                            CollectorLayer layer = CollectorPlan.Static(f.Runtime.Content, f.Runtime.Options, CollectorPlanTests.Snapshot(Shipped));
                            List<string> weighted = layer.Game.Where(c => c.Table == CollectorGuard.CrateTable && c.Changer == "Id@Weight#1" && (long)c.Value > 0)
                                                              .Select(c => c.Key).ToList();
                            Assert.NotEmpty(weighted);
                            foreach (string row in weighted)
                                Assert.Contains(layer.Game, c => c.Table == CollectorGuard.CrateTable && c.Changer == "Id@Type" && c.Key == row);
                            Assert.Contains(layer.Game, c => c.Table == CollectorPlan.LootCratesTable && c.Changer == "Id@CratesCount"
                                                             && c.Key == CollectorGuard.CrateId && (long)c.Value > 0);
                        }
                    }
                }
            }
            output.WriteLine(windows.Count + " windows, " + withCrates + " with crates");
            Assert.True(withCrates > 0);
        }

        [Fact]
        public void CratesInWindowNeedsEveryInputButLiveOps()
        {
            using var f = new Fixture();
            CollectorRuntime r = f.Runtime;
            Assert.True(r.CratesInWindow(Shipped, Shipped));
            Assert.True(r.CratesInWindow(Shipped.AddDays(7), Shipped));
            Assert.False(r.CratesInWindow(Shipped.AddDays(-1), Shipped));          // the day before since: no crate
            Assert.False(r.CratesInWindow(Shipped, null));
            f.Set(false);
            Assert.False(r.CratesInWindow(Shipped, Shipped));
            f.Set((bool?)null);
            Assert.True(r.CratesInWindow(Shipped, Shipped));
            f.Set(ContentState.Broken);
            Assert.False(r.CratesInWindow(Shipped, Shipped));
            f.Set(ContentState.Missing);
            Assert.False(r.CratesInWindow(Shipped, Shipped));
            f.Set(ContentState.Valid);
            f.Enable(false);
            Assert.False(r.CratesInWindow(Shipped, Shipped));
        }

        [Fact]
        public void ForKeepsOneInstancePerServer()
        {
            using var f = new Fixture();
            CollectorRuntime runtime = CollectorRuntime.For(f.Context);
            Assert.Same(runtime, CollectorRuntime.For(f.Context));
            Assert.Same(runtime, f.Context.Features.Get<CollectorRuntime>());
            Assert.Null(runtime.StorePatch);                    // no ClientCapabilities registered: unknown
        }

        /// <summary>Options are parsed once per settings snapshot of <c>economy.collector</c>, and again when it changes.</summary>
        [Fact]
        public void OptionsFollowTheSettings()
        {
            using var f = new Fixture();
            CollectorOptions first = f.Runtime.Options;
            Assert.Equal(5, first.ItemsPerCrate);
            Assert.Same(first, f.Runtime.Options);
            f.Context.Settings.Override("liveops.seasonDays", 3L);                 // another subtree: same options
            Assert.Same(first, f.Runtime.Options);
            f.Context.Settings.Override("economy.collector.itemsPerCrate", 12L);
            CollectorOptions second = f.Runtime.Options;
            Assert.Equal(10, second.ItemsPerCrate);
            Assert.Contains(f.Logs.Snapshot(), e => e.Message.StartsWith("settings: economy.collector.itemsPerCrate = 12"));
            Assert.Same(second, f.Runtime.Options);
        }

        /// <summary>collector.json comes through the content provider (the &lt;data&gt;/content override wins) and is re-read
        /// when it changes; each change is logged once.</summary>
        [Fact]
        public void ContentIsReReadWhenTheFileChanges()
        {
            using var dir = new TempDir();
            string overrides = dir.Combine("content");
            Directory.CreateDirectory(overrides);
            using var f = new Fixture(new ContentProvider(null, overrides, Log.Null));
            CollectorRuntime r = f.Runtime;
            Assert.NotNull(r.Content);
            Assert.Null(r.ContentProblem);
            Assert.Equal(39, r.Content.Rows.Count);

            string file = Path.Combine(overrides, CollectorContent.FileName);
            File.WriteAllText(file, BrokenJson);
            Assert.Null(r.Content);
            Assert.StartsWith("row 3: ", r.ContentProblem);
            Assert.Null(r.Content);
            Assert.Single(f.Logs.Snapshot(), e => e.Level == LogLevel.Error && e.Message.Contains("collector.json refused"));

            File.WriteAllText(file, "{");
            Assert.Equal("not a JSON object", r.ContentProblem);
            File.Delete(file);
            Assert.NotNull(r.Content);
        }

        /// <summary>
        /// A collector.json for another crate or table is refused (the guard covers only Crate_Event_Special): the guard,
        /// and live ops hand out no crate whose table the layer would not fill.
        /// </summary>
        [Theory]
        [InlineData("crate.id")]
        [InlineData("crate.table")]
        public void ContentForAnotherCrateKeepsTheCollectorInactive(string path)
        {
            using var f = new Fixture();
            var doc = MiniJson.ParseObject(CollectorPlanTests.ShippedJson);
            JsonPath.Set(doc, path, "Crate_Event_1");
            f.Content.Files[CollectorContent.FileName] = MiniJson.Serialize(doc);
            CollectorStatus status = f.Runtime.Status(Shipped.AddDays(1), true, Shipped);
            Assert.Equal(CollectorPhase.Guard, status.Phase);
            Assert.Equal("collector.json: " + path + " Crate_Event_1 is not Crate_Event_Special", status.Reason.Substring(0, status.Reason.IndexOf(" (")));
            Assert.False(f.Runtime.CratesInWindow(Shipped, Shipped));
            Assert.Null(f.Runtime.Content);
        }

        /// <summary>
        /// A content read that throws never escapes Status, CratesInWindow, InputsKey or Content: before any read the file
        /// counts as unreadable (the guard); after one the last read is kept, so InputsKey and the running events do not
        /// flip on a transient failure (§3.3). Each failing streak is logged once.
        /// </summary>
        [Fact]
        public void AContentReadThatThrowsNeverEscapes()
        {
            using var f = new Fixture();
            CollectorRuntime r = f.Runtime;
            f.Content.Fail = new FileNotFoundException("collector.json vanished");
            CollectorStatus s = r.Status(Shipped.AddDays(1), true, Shipped);
            Assert.Equal(CollectorPhase.Guard, s.Phase);
            Assert.Equal("collector.json: unreadable (collector.json vanished)", s.Reason);
            Assert.False(r.CratesInWindow(Shipped, Shipped));
            Assert.Null(r.Content);
            Assert.Equal("unreadable (collector.json vanished)", r.ContentProblem);
            Assert.Contains("|no-content|", r.InputsKey);
            Assert.Single(f.Logs.Snapshot(), e => e.Level == LogLevel.Error && e.Message.Contains("could not be read"));

            f.Content.Fail = null;
            Assert.Equal(CollectorPhase.Active, r.Status(Shipped.AddDays(1), true, Shipped).Phase);
            string key = r.InputsKey;
            Assert.Contains("|content|", key);

            f.Content.Fail = new IOException("sharing violation");
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(CollectorPhase.Active, r.Status(Shipped.AddDays(1), true, Shipped).Phase);
                Assert.True(r.CratesInWindow(Shipped, Shipped));
                Assert.Equal(key, r.InputsKey);
                Assert.NotNull(r.Content);
                Assert.Null(r.ContentProblem);
            }
            Assert.Single(f.Logs.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.Contains("keeping the last read"));

            // A refused file read last stays refused while reads fail; a new failing streak is logged again.
            f.Content.Fail = null;
            f.Set(ContentState.Broken);
            Assert.StartsWith("row 3: ", r.ContentProblem);
            f.Content.Fail = new IOException("again");
            Assert.StartsWith("row 3: ", r.ContentProblem);
            Assert.Equal(CollectorPhase.Guard, r.Status(Shipped.AddDays(1), true, Shipped).Phase);
            Assert.Equal(2, f.Logs.Snapshot().Count(e => e.Level == LogLevel.Warn && e.Message.Contains("keeping the last read")));
            f.Content.Fail = null;
            f.Set(ContentState.Valid);
            Assert.NotNull(r.Content);
        }

        /// <summary>
        /// Readers on several threads while the &lt;data&gt;/content override is rewritten (temp file + rename) and deleted:
        /// no call throws (ContentProvider checks File.Exists, then reads).
        /// </summary>
        [Fact]
        public void ConcurrentCallsNeverThrowWhileTheOverrideChanges()
        {
            using var dir = new TempDir();
            string overrides = dir.Combine("content");
            Directory.CreateDirectory(overrides);
            using var f = new Fixture(new ContentProvider(null, overrides, Log.Null));
            CollectorRuntime r = f.Runtime;
            string file = Path.Combine(overrides, CollectorContent.FileName);
            string temp = file + ".tmp";
            string[] texts = { CollectorPlanTests.ShippedJson, BrokenJson };
            var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            long calls = 0;
            int writes = 0;
            bool done = false;
            var readers = new List<System.Threading.Thread>();
            for (int t = 0; t < 4; t++)
            {
                var thread = new System.Threading.Thread(() =>
                {
                    while (!System.Threading.Volatile.Read(ref done))
                    {
                        try
                        {
                            r.Status(Shipped.AddDays(1), true, Shipped);
                            r.CratesInWindow(Shipped, Shipped);
                            Assert.NotNull(r.InputsKey);
                            _ = r.Content;
                            _ = r.ContentProblem;
                            System.Threading.Interlocked.Increment(ref calls);
                        }
                        catch (Exception ex)
                        {
                            failures.Enqueue(ex);
                        }
                    }
                });
                thread.IsBackground = true;
                readers.Add(thread);
                thread.Start();
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 1500)
            {
                try
                {
                    File.WriteAllText(temp, texts[writes % 2]);
                    File.Move(temp, file, true);
                    if (writes % 3 == 2) File.Delete(file);
                }
                catch (IOException)
                {
                    // The writer's own race on a platform that locks open files: not what this test is about.
                }
                writes++;
            }
            System.Threading.Volatile.Write(ref done, true);
            foreach (var thread in readers) Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
            output.WriteLine(calls + " calls, " + writes + " writes, " + failures.Count + " failures");
            Assert.True(failures.IsEmpty, failures.IsEmpty ? null : failures.Count + " calls threw, the first: " + failures.First());
            Assert.True(calls > 0);
        }

        [Fact]
        public void MissingContentIsLoggedOnce()
        {
            using var f = new Fixture();
            f.Set(ContentState.Missing);
            for (int i = 0; i < 3; i++) Assert.Null(f.Runtime.Content);
            Assert.Single(f.Logs.Snapshot(), e => e.Level == LogLevel.Error && e.Message.Contains("collector.json is missing"));
        }

        /// <summary>Live ops key their events on enabled, content validity and the store patch (§3.2).</summary>
        [Fact]
        public void InputsKeyChangesWithEnabledContentAndStorePatch()
        {
            using var f = new Fixture();
            var keys = new HashSet<string>();
            foreach (bool enabled in new[] { true, false })
            {
                f.Enable(enabled);
                foreach (ContentState content in new[] { ContentState.Valid, ContentState.Missing })
                {
                    f.Set(content);
                    foreach (bool? patch in new bool?[] { true, false, null })
                    {
                        f.Set(patch);
                        Assert.True(keys.Add(f.Runtime.InputsKey), f.Runtime.InputsKey);
                    }
                }
            }
            f.Set(ContentState.Broken);
            Assert.Contains(f.Runtime.InputsKey, keys);                 // broken = missing: neither hands out crates
            f.Context.Settings.Override("economy.collector.itemsPerCrate", 3L);
            Assert.Contains(f.Runtime.InputsKey, keys);                 // other options are no input of CratesInWindow
        }

        /// <summary>The shipped content and default settings on a standalone server (store patch unknown): Active from the
        /// shipped since, the guard never before it.</summary>
        [Fact]
        public void ShippedContentIsActiveFromTheShippedSince()
        {
            using var f = new Fixture(new ContentProvider(null, null, Log.Null));
            Assert.Equal(CollectorPhase.Active, f.Runtime.Status(Shipped, true, Shipped).Phase);
            Assert.Equal(CollectorPhase.Dormant, f.Runtime.Status(new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc), true, Shipped).Phase);
            Assert.True(f.Runtime.CratesInWindow(Shipped, Shipped));
            Assert.DoesNotContain(f.Logs.Snapshot(), e => e.Level >= LogLevel.Warn);
        }

        /// <summary>guardSince = min(since, ShippedSince) − Lead (one day: the next day's quests and the next week's event
        /// are listed, and fetched, a day before their window starts).</summary>
        [Fact]
        public void GuardSinceIsADayBeforeTheEarlierOfSinceAndTheShippedSince()
        {
            Assert.Equal(TimeSpan.FromDays(1), CollectorGuard.Lead);
            Assert.Equal(Shipped.AddDays(-1), CollectorRuntime.GuardSince(null));
            Assert.Equal(Shipped.AddDays(-1), CollectorRuntime.GuardSince(Shipped.AddDays(7)));
            Assert.Equal(Shipped.AddDays(-8), CollectorRuntime.GuardSince(Shipped.AddDays(-7)));
        }

        /// <summary>
        /// The latch (D9, §2.12): once live ops served an event carrying the crate, Status is never Dormant again, whatever
        /// since or the clock does (the phase table otherwise unchanged: Active stays Active). It is written to the data
        /// folder and read back by the next server; a broken file counts as set.
        /// </summary>
        [Fact]
        public void TheLatchKeepsTheGuardForever()
        {
            DateTime early = Shipped.AddDays(-30);
            DateTime overrideSince = Shipped.AddDays(-14);
            string dir;
            using (var f = new Fixture())
            {
                dir = f.Dir.Path;
                CollectorRuntime r = f.Runtime;
                Assert.Null(r.CratesServedAt);
                Assert.Equal(CollectorPhase.Dormant, r.Status(early, true, Shipped).Phase);
                r.NoteCratesServed("mcvs_daily_2026102600", overrideSince, overrideSince.AddHours(12));
                Assert.Equal(overrideSince.AddHours(12), r.CratesServedAt);
                Assert.Equal(overrideSince, r.CratesServedWindow);
                Assert.Single(f.Logs.Snapshot(), e => e.Message.StartsWith("Cosmetic crates served for the first time (mcvs_daily_2026102600)"));
                // The override removed (since back to the shipped one) and the clock set back: the guard stays.
                CollectorStatus s = r.Status(early, true, Shipped);
                Assert.Equal(CollectorPhase.Guard, s.Phase);
                Assert.Equal("before collector.since 2026-11-09T00:00:00Z (guard kept: Cosmetic crates were served at 2026-10-26T12:00:00Z)", s.Reason);
                Assert.Equal(CollectorPhase.Guard, r.Status(early, false, null).Phase);
                Assert.Equal(CollectorPhase.Active, r.Status(overrideSince, true, overrideSince).Phase);
                // Later windows change nothing; an earlier one is remembered (and written) too.
                r.NoteCratesServed("mcvs_daily_2026102700", overrideSince.AddDays(1), overrideSince.AddDays(1));
                Assert.Equal(overrideSince, r.CratesServedWindow);
                r.NoteCratesServed("mcvs_daily_2026102000", overrideSince.AddDays(-6), overrideSince.AddDays(2));
                Assert.Equal(overrideSince.AddDays(-6), r.CratesServedWindow);
                Assert.Equal(overrideSince.AddHours(12), r.CratesServedAt);
                Assert.Single(f.Logs.Snapshot(), e => e.Message.StartsWith("Cosmetic crates served for the first time"));
                string text = File.ReadAllText(Path.Combine(dir, CollectorRuntime.LatchFileName));
                Assert.Contains("\"cratesServed\": \"2026-10-26T12:00:00Z\"", text);
                Assert.Contains("\"event\": \"mcvs_daily_2026102000\"", text);

                // The next server on the same data folder.
                var logger = new Logger();
                var next = new ServerContext(dir, new Settings(), logger, new ManualClock(early), f.Content);
                CollectorRuntime again = CollectorRuntime.For(next);
                Assert.Equal(overrideSince.AddHours(12), again.CratesServedAt);
                Assert.Equal(overrideSince.AddDays(-6), again.CratesServedWindow);
                Assert.Equal(CollectorPhase.Guard, again.Status(early, true, Shipped).Phase);

                // A broken file: the guard, as if crates had been served.
                File.WriteAllText(Path.Combine(dir, CollectorRuntime.LatchFileName), "{ not json");
                var logs = new MemoryLogSink();
                var brokenLogger = new Logger();
                brokenLogger.AddSink(logs);
                var broken = new ServerContext(dir, new Settings(), brokenLogger, new ManualClock(early), f.Content);
                Assert.Equal(CollectorPhase.Guard, CollectorRuntime.For(broken).Status(early, true, Shipped).Phase);
                Assert.True(logs.Contains(CollectorRuntime.LatchFileName + " is not valid"));
            }
        }
    }
}
