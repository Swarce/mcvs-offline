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
                data = Files.TryGetValue(name, out string text) ? System.Text.Encoding.UTF8.GetBytes(text) : null;
                return data != null;
            }

            public bool TryReadContentText(string name, out string text)
            {
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
            }
            yield return Shipped.AddDays(-60);
            yield return Shipped.AddDays(60);
        }

        /// <summary>§2.12 written out once more, as the test's own reading of the design.</summary>
        private static CollectorPhase Expected(bool enabled, bool liveOps, ContentState content, bool? patch, DateTime? since, DateTime now)
        {
            bool active = enabled && liveOps && content == ContentState.Valid && patch != false && since.HasValue && now >= since.Value;
            if (active) return CollectorPhase.Active;
            DateTime guardSince = since.HasValue && since.Value < Shipped ? since.Value : Shipped;
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
            Assert.Equal(CollectorPhase.Dormant, r.Status(Shipped.AddSeconds(-1), true, Shipped).Phase);
            Assert.Equal(CollectorPhase.Active, r.Status(Shipped.AddDays(-13), true, Shipped.AddDays(-14)).Phase);   // an earlier since
            Assert.Equal(CollectorPhase.Dormant, r.Status(Shipped.AddDays(-15), true, Shipped.AddDays(-14)).Phase);

            // A later since: the guard still starts at the shipped since (guardSince = min).
            s = r.Status(Shipped.AddDays(1), true, later);
            Assert.Equal(CollectorPhase.Guard, s.Phase);
            Assert.Equal("before collector.since 2026-11-23T00:00:00Z", s.Reason);
            Assert.Equal(CollectorPhase.Active, r.Status(later, true, later).Phase);

            s = r.Status(Shipped.AddDays(1), true, null);
            Assert.Equal(CollectorPhase.Guard, s.Phase);
            Assert.Contains("no collector.since", s.Reason);
            s = r.Status(Shipped.AddDays(-1), true, null);
            Assert.Equal(CollectorPhase.Dormant, s.Phase);
            Assert.Contains("guard from 2026-11-09T00:00:00Z", s.Reason);

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
                            CollectorLayer layer = CollectorPlan.Static(f.Runtime.Content, f.Runtime.Options, CollectorPlanTests.Snapshot(Shipped));
                            Assert.Contains(layer.Game, c => c.Table == f.Runtime.Content.Table && c.Changer == "Id@Weight#1" && (long)c.Value > 0);
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

        [Fact]
        public void GuardSinceIsTheEarlierOfSinceAndTheShippedSince()
        {
            Assert.Equal(Shipped, CollectorRuntime.GuardSince(null));
            Assert.Equal(Shipped, CollectorRuntime.GuardSince(Shipped.AddDays(7)));
            Assert.Equal(Shipped.AddDays(-7), CollectorRuntime.GuardSince(Shipped.AddDays(-7)));
        }
    }
}
