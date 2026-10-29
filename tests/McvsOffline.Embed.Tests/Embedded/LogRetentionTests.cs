using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Server.Core;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The log retention per start (docs/design/settings-page.md §7.7): starts.txt, the cutoff, what is deleted and what never is.
    /// File times are set, nothing sleeps.
    /// </summary>
    public class LogRetentionTests
    {
        private static readonly DateTime S2 = new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);   // deleted with N = 1
        private static readonly DateTime S1 = new DateTime(2026, 10, 28, 19, 2, 11, 412, DateTimeKind.Utc);
        private static readonly DateTime S0 = new DateTime(2026, 10, 29, 8, 14, 3, 20, DateTimeKind.Utc);   // this start

        private static string StartsPath(string logs) => Path.Combine(logs, LogRetention.StartsFile);

        private static string[] StartLines(string logs) => File.ReadAllLines(StartsPath(logs));

        private static void Plant(string path, DateTime utc, string text = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text ?? Path.GetFileName(path) + "\n");
            File.SetLastWriteTimeUtc(path, utc);
        }

        private static bool DeleteFile(string path)
        {
            File.Delete(path);
            return !File.Exists(path);
        }

        /// <summary>Begin with starts.txt holding <paramref name="previous"/>, this start being <see cref="S0"/>.</summary>
        private static LogRetention BeginAfter(string logs, int keep, params DateTime[] previous)
        {
            Directory.CreateDirectory(logs);
            if (previous.Length > 0)
                File.WriteAllLines(StartsPath(logs), previous.Select(t => LogRetention.FormatStart(t) + " android 0.13.0"));
            return LogRetention.Begin(logs, S0, keep, "android 0.13.0", Log.Null);
        }

        // ------------------------------------------------------------------ starts.txt and the cutoff

        [Fact]
        public void BeginRecordsTheStartAndComputesTheCutoff()
        {
            string logs = Path.Combine(TestEnvironment.NewTempDir("logret-begin"), "logs");
            Directory.CreateDirectory(logs);

            // No file: nothing recorded, no cutoff with N = 1; one line written.
            LogRetention first = LogRetention.Begin(logs, S1, 1, "android 0.13.0", Log.Null);
            Assert.Null(first.CutoffUtc);
            Assert.Equal("only 0 starts recorded so far", first.NoCutoffReason);
            Assert.Equal(new[] { "2026-10-28T19:02:11.412Z android 0.13.0" }, StartLines(logs));

            // The second start keeps the first one: the cutoff is its start time.
            LogRetention second = LogRetention.Begin(logs, S0, 1, "windows 0.13.0", Log.Null);
            Assert.Equal(S1, second.CutoffUtc);
            Assert.Equal(DateTimeKind.Utc, second.CutoffUtc.Value.Kind);
            Assert.Null(second.NoCutoffReason);
            Assert.Equal(1, second.KeepStarts);
            Assert.Equal(S0, second.StartedUtc);
            Assert.Equal(new[] { "2026-10-28T19:02:11.412Z android 0.13.0", "2026-10-29T08:14:03.020Z windows 0.13.0" }, StartLines(logs));
        }

        /// <summary>
        /// starts.txt is in file order, not time order, once the clock was corrected (a start after the correction is still
        /// appended): the cutoff is the earliest kept start, so no kept start's files are older than it.
        /// </summary>
        [Fact]
        public void TheCutoffIsTheEarliestKeptStartWhenTheHistoryIsOutOfOrder()
        {
            string root = TestEnvironment.NewTempDir("logret-order");
            DateTime ahead = S0.AddHours(-1);    // A: the clock was ahead
            DateTime fixedAt = S0.AddHours(-2);  // B: after the correction, recorded after A
            LogRetention two = BeginAfter(Path.Combine(root, "n2"), 2, S2, ahead, fixedAt);
            Assert.Equal(fixedAt, two.CutoffUtc);   // not A: B's files, written before A's time, stay
            Assert.Null(two.NoCutoffReason);
            Assert.Equal(fixedAt, LogRetention.Cutoff(new List<DateTime> { ahead, fixedAt }, S0, 2, out string reason));
            Assert.Null(reason);
            // N = 1 still keeps the start recorded last.
            Assert.Equal(fixedAt, BeginAfter(Path.Combine(root, "n1"), 1, S2, ahead, fixedAt).CutoffUtc);
            // The window only: an earlier entry outside it does not count.
            Assert.Equal(fixedAt, LogRetention.Cutoff(new List<DateTime> { S2.AddDays(-1), ahead, fixedAt }, S0, 2, out _));

            // Delete with that history: B's last file stays, the start before A goes.
            string logs = Path.Combine(root, "n2");
            Plant(Path.Combine(logs, "server.log.1"), fixedAt.AddMinutes(50));   // B
            Plant(Path.Combine(logs, "server.log.2"), ahead.AddMinutes(30));     // A
            Plant(Path.Combine(logs, "server.log.3"), S2.AddMinutes(30));        // older
            two.Clean(logs, null, null, DeleteFile, 5000);
            Assert.True(File.Exists(Path.Combine(logs, "server.log.1")));
            Assert.True(File.Exists(Path.Combine(logs, "server.log.2")));
            Assert.False(File.Exists(Path.Combine(logs, "server.log.3")));
        }

        /// <summary>
        /// The exit traces: N &gt;= 1 uses the cutoff; N = 0 the previous start, so the trace of the process that just ended
        /// (after that start, before this one) is still saved.
        /// </summary>
        [Fact]
        public void TheTraceCutoffKeepsTheLastExitWithZeroStartsKept()
        {
            string root = TestEnvironment.NewTempDir("logret-trace");
            LogRetention zero = BeginAfter(Path.Combine(root, "n0"), 0, S2, S1);
            Assert.Equal(S0, zero.CutoffUtc);
            Assert.Equal(S1, zero.TraceCutoffUtc);
            Assert.True(LogRetention.KeepsExit(S1.AddMinutes(30), zero.TraceCutoffUtc));    // the process that just ended
            Assert.False(LogRetention.KeepsExit(S1.AddMinutes(-1), zero.TraceCutoffUtc));  // an older one: deleted with its start

            Assert.Equal(S1, BeginAfter(Path.Combine(root, "n1"), 1, S2, S1).TraceCutoffUtc);   // = CutoffUtc
            Assert.Null(BeginAfter(Path.Combine(root, "first"), 0).TraceCutoffUtc);            // nothing recorded: all saved
            Assert.Null(BeginAfter(Path.Combine(root, "back"), 0, S0.AddHours(1)).TraceCutoffUtc);   // the clock went back
            Assert.Null(BeginAfter(Path.Combine(root, "short"), 2, S1).TraceCutoffUtc);          // no cutoff yet

            LogRetention back = BeginAfter(Path.Combine(root, "clock"), 1, S0.AddHours(1));
            Assert.True(back.ClockWentBackNow);
            Assert.False(BeginAfter(Path.Combine(root, "few"), 2, S1).ClockWentBackNow);
        }

        [Fact]
        public void TheCutoffFollowsTheNumberOfStartsKept()
        {
            string root = TestEnvironment.NewTempDir("logret-cutoff");
            Assert.Equal(S0, BeginAfter(Path.Combine(root, "n0"), 0, S2, S1).CutoffUtc);   // N = 0: this start's own time
            Assert.Equal(S1, BeginAfter(Path.Combine(root, "n1"), 1, S2, S1).CutoffUtc);
            Assert.Equal(S2, BeginAfter(Path.Combine(root, "n2"), 2, S2, S1).CutoffUtc);

            LogRetention three = BeginAfter(Path.Combine(root, "n3"), 3, S2, S1);
            Assert.Null(three.CutoffUtc);
            Assert.Equal("only 2 starts recorded so far", three.NoCutoffReason);
            Assert.Equal("only 1 start recorded so far", BeginAfter(Path.Combine(root, "one"), 2, S1).NoCutoffReason);

            // A kept start after this one: the clock went back, nothing is deleted by age.
            LogRetention back = BeginAfter(Path.Combine(root, "clock"), 1, S2, S0.AddHours(1));
            Assert.Null(back.CutoffUtc);
            Assert.Equal("the clock went back", back.NoCutoffReason);

            // Out of range: clamped to 0..10.
            Assert.Equal(LogRetention.MaxKeepStarts, BeginAfter(Path.Combine(root, "many"), 99).KeepStarts);
            Assert.Equal(0, BeginAfter(Path.Combine(root, "neg"), -4).KeepStarts);
        }

        [Fact]
        public void DamagedLinesAreSkippedAndFiftyStartsAreKept()
        {
            string logs = Path.Combine(TestEnvironment.NewTempDir("logret-lines"), "logs");
            Directory.CreateDirectory(logs);
            var lines = new List<string> { "garbage", "", "2026-13-40T99:99:99.000Z android 0.12.0", "not-a-dateZ android" };
            for (int i = 0; i < 60; i++) lines.Add(LogRetention.FormatStart(S2.AddMinutes(i)) + " android 0.12.0");
            lines.Insert(30, "\u0000\u0001 broken");
            File.WriteAllLines(StartsPath(logs), lines);

            LogRetention retention = LogRetention.Begin(logs, S0, 1, "android 0.13.0", Log.Null);
            Assert.Equal(S2.AddMinutes(59), retention.CutoffUtc);   // the last good line
            string[] written = StartLines(logs);
            Assert.Equal(LogRetention.StartsKept, written.Length);
            Assert.Equal(LogRetention.FormatStart(S2.AddMinutes(11)) + " android 0.12.0", written[0]);
            Assert.Equal("2026-10-29T08:14:03.020Z android 0.13.0", written[49]);
            Assert.DoesNotContain(written, l => l.Contains("garbage") || l.Contains("broken"));
        }

        [Fact]
        public void AnUnwritableFolderStillGivesTheCutoffAndNeverThrows()
        {
            string root = TestEnvironment.NewTempDir("logret-unwritable");
            var memory = new MemoryLogSink(LogLevel.Debug, 50);
            var logger = new Logger();
            logger.AddSink(memory);
            // The logs folder cannot be created (a file is in the way; the tests run as root, so permissions would not stop it).
            string blocked = Path.Combine(root, "file-not-dir");
            File.WriteAllText(blocked, "x");
            LogRetention retention = LogRetention.Begin(Path.Combine(blocked, "logs"), S0, 0, "android", logger.For("logs"));
            Assert.Equal(S0, retention.CutoffUtc);   // still computed (N = 0 needs no history)
            Assert.Contains(memory.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.Contains("cannot write"));

            // starts.txt is a directory: not readable as a file, not replaceable; no cutoff with N = 1, one warning, no throw.
            var second = new MemoryLogSink(LogLevel.Debug, 50);
            var logger2 = new Logger();
            logger2.AddSink(second);
            string dirStarts = Path.Combine(root, "dir-starts");
            Directory.CreateDirectory(Path.Combine(dirStarts, LogRetention.StartsFile));
            LogRetention fromDir = LogRetention.Begin(dirStarts, S0, 1, "android", logger2.For("logs"));
            Assert.Null(fromDir.CutoffUtc);
            Assert.NotNull(fromDir.NoCutoffReason);
            Assert.Single(second.Snapshot(), e => e.Level == LogLevel.Warn);
            Assert.True(Directory.Exists(Path.Combine(dirStarts, LogRetention.StartsFile)));
        }

        // ------------------------------------------------------------------ what is deleted

        /// <summary>Files of three starts: S-2 before the cutoff, S-1 and S after it.</summary>
        private sealed class Layout
        {
            public string Root, Logs, Fallback, Data;
            public readonly List<string> Old = new List<string>();
            public readonly List<string> Kept = new List<string>();
        }

        private static Layout ThreeStarts(string prefix)
        {
            var l = new Layout();
            l.Root = TestEnvironment.NewTempDir(prefix);
            l.Data = Path.Combine(l.Root, "McvsOffline");
            l.Logs = Path.Combine(l.Data, "logs");
            l.Fallback = Path.Combine(l.Root, "internal", "McvsOffline", "logs");
            DateTime old = S1.AddHours(-2), previous = S1.AddMinutes(30), current = S0.AddSeconds(5);
            void Add(string path, DateTime time, bool deleted)
            {
                Plant(path, time);
                (deleted ? l.Old : l.Kept).Add(path);
            }
            foreach (string family in LogRetention.Families)
            {
                Add(Path.Combine(l.Logs, family), current, false);
                Add(Path.Combine(l.Logs, family + ".1"), previous, false);
                Add(Path.Combine(l.Logs, family + ".2"), old, true);
                Add(Path.Combine(l.Logs, family + ".3"), old.AddMinutes(-5), true);
            }
            foreach (string name in new[] { "crash-20261027-080500-000-1.txt", "exception-20261027-080600-000-2.txt",
                                            "threaddump-20261027-090000.txt", "exit-4242-anr.txt", "exit-4243-tombstone.pb" })
                Add(Path.Combine(l.Logs, name), old, true);
            foreach (string name in new[] { "crash-20261028-200000-000-1.txt", "threaddump-20261028-210000.txt", "exit-5151-anr.txt" })
                Add(Path.Combine(l.Logs, name), previous, false);
            foreach (string name in new[] { "bootstrap.log", "server.log.1", "game.log.2" }) Add(Path.Combine(l.Fallback, name), old, true);
            Add(Path.Combine(l.Fallback, "server.log"), previous, false);
            // Never, however old: not a log name, or not ours to delete.
            foreach (string name in new[] { "restart-requested.txt", LogRetention.StartsFile, "notes.txt", "x.tmp", "server.log.1.tmp",
                                            "starts.txt.1a2b.mcvs-tmp", "server.log.bak", "crash-.txt", "exit-1-other.txt", "game.log.x" })
                Add(Path.Combine(l.Logs, name), old, false);
            Add(Path.Combine(l.Logs, "sub", "server.log.4"), old, false);
            Add(Path.Combine(l.Fallback, "notes.txt"), old, false);
            Add(Path.Combine(l.Fallback, "unknown-endpoints.jsonl.1"), old, false);   // that family has no fallback
            Add(Path.Combine(l.Root, "internal", "McvsOffline", "hd-install", "installed.json"), old, false);
            Add(Path.Combine(l.Data, "settings.json"), old, false);
            Add(Path.Combine(l.Data, "logs.txt"), old, false);
            return l;
        }

        private static LogRetention RetentionFor(Layout l, int keep = 1)
        {
            return BeginAfter(l.Logs, keep, S2, S1);
        }

        private static void AssertLayout(Layout l)
        {
            foreach (string path in l.Old) Assert.False(File.Exists(path), path + " should be deleted");
            foreach (string path in l.Kept) Assert.True(File.Exists(path), path + " should be kept");
        }

        [Fact]
        public void CleanDeletesTheOlderStartsFilesAndNothingElse()
        {
            Layout l = ThreeStarts("logret-clean");
            LogRetention retention = RetentionFor(l);
            Assert.Equal(S1, retention.CutoffUtc);
            LogRetention.CleanResult result = retention.Clean(l.Logs, l.Fallback, new List<SafeLogFile>(), DeleteFile, LogRetention.BudgetMs);
            AssertLayout(l);
            Assert.Equal(l.Old.Count, result.Deleted.Count);
            Assert.Equal(0, result.Failed);
            Assert.False(result.Stopped);
            Assert.Equal("keeping this start and 1 previous (since 2026-10-28T19:02:11Z): deleted 16 files, 0.0 MB (bootstrap.log.2-3, " +
                         "server.log.2-3, game.log.2-3, unknown-endpoints.jsonl.2-3, crash 1, exception 1, threaddump 1, exit 2; fallback 3)",
                         result.Line);

            // Again: nothing left to delete.
            LogRetention.CleanResult again = retention.Clean(l.Logs, l.Fallback, null, DeleteFile, LogRetention.BudgetMs);
            Assert.Empty(again.Deleted);
            Assert.Equal("keeping this start and 1 previous (since 2026-10-28T19:02:11Z): nothing to delete", again.Line);
        }

        [Fact]
        public void KeepingOnlyThisStartDeletesThePreviousOneToo()
        {
            Layout l = ThreeStarts("logret-zero");
            LogRetention retention = RetentionFor(l, 0);
            Assert.Equal(S0, retention.CutoffUtc);
            LogRetention.CleanResult result = retention.Clean(l.Logs, l.Fallback, null, DeleteFile, LogRetention.BudgetMs);
            Assert.StartsWith("keeping only this start (since 2026-10-29T08:14:03Z): deleted ", result.Line);
            foreach (string family in LogRetention.Families)
            {
                Assert.True(File.Exists(Path.Combine(l.Logs, family)));   // this start's (written after S0)
                Assert.False(File.Exists(Path.Combine(l.Logs, family + ".1")));
            }
            Assert.False(File.Exists(Path.Combine(l.Logs, "exit-5151-anr.txt")));
            Assert.False(File.Exists(Path.Combine(l.Fallback, "server.log")));
            Assert.True(File.Exists(StartsPath(l.Logs)));
            Assert.True(File.Exists(Path.Combine(l.Logs, "restart-requested.txt")));
        }

        [Fact]
        public void OpenFilesDeleteTheirOwnOlderFilesAndKeepWhatTheyWrite()
        {
            Layout l = ThreeStarts("logret-owners");
            // server.log is open (and old, as if nothing was written yet this start); its fallback file too; game.log has no owner.
            string server = Path.Combine(l.Logs, "server.log");
            File.SetLastWriteTimeUtc(server, S2);
            string fallbackServer = Path.Combine(l.Fallback, "server.log");
            File.SetLastWriteTimeUtc(fallbackServer, S2);
            string game = Path.Combine(l.Logs, "game.log");
            File.SetLastWriteTimeUtc(game, S2);
            using (var owner = new SafeLogFile(server, new SafeLogFileOptions { Synchronous = true, FallbackDirectory = l.Fallback }))
            {
                LogRetention retention = RetentionFor(l);
                LogRetention.CleanResult result = retention.Clean(l.Logs, l.Fallback, new List<SafeLogFile> { owner, null }, DeleteFile,
                                                                  LogRetention.BudgetMs);
                Assert.True(File.Exists(server));                               // written by this process: never
                Assert.False(File.Exists(server + ".2"));                       // its older files: through the owner
                Assert.False(File.Exists(Path.Combine(l.Fallback, "server.log.1")));
                Assert.False(File.Exists(fallbackServer));                      // not the file it writes: old, deleted
                Assert.False(File.Exists(game));                                // no owner this process and old
                Assert.Contains("game.log, game.log.2-3", result.Line);
                Assert.Contains("server.log.2-3", result.Line);
                Assert.DoesNotContain("server.log,", result.Line);
            }

            // A log that moved to the fallback folder: the file it writes there stays, whatever its time.
            Layout f = ThreeStarts("logret-fallback-owner");
            string primary = Path.Combine(f.Logs, "bootstrap.log");
            File.Delete(primary);
            Directory.CreateDirectory(primary);   // cannot be written: the log continues in the fallback folder
            using (var owner = new SafeLogFile(primary, new SafeLogFileOptions { FallbackDirectory = f.Fallback, RetryDelaysMs = new[] { 10 } }))
            {
                owner.AppendLine("line");
                Assert.True(owner.Flush(10000));
                Assert.True(owner.UsingFallback);
                File.SetLastWriteTimeUtc(owner.Path, S2);
                RetentionFor(f).Clean(f.Logs, f.Fallback, new List<SafeLogFile> { owner }, DeleteFile, LogRetention.BudgetMs);
                Assert.True(File.Exists(owner.Path));
                Assert.False(File.Exists(Path.Combine(f.Logs, "bootstrap.log.2")));
            }
        }

        [Fact]
        public void WithoutACutoffNothingIsDeletedAndTheLineSaysWhy()
        {
            Layout l = ThreeStarts("logret-nocutoff");
            LogRetention retention = BeginAfter(l.Logs, 3, S2, S1);
            LogRetention.CleanResult result = retention.Clean(l.Logs, l.Fallback, null, DeleteFile, LogRetention.BudgetMs);
            Assert.Empty(result.Deleted);
            foreach (string path in l.Old) Assert.True(File.Exists(path), path);
            Assert.Equal("keeping this start and 3 previous: only 2 starts recorded so far, nothing deleted this time", result.Line);
        }

        [Fact]
        public void AFailedDeleteIsCountedAndTheBudgetStopsTheWork()
        {
            Layout l = ThreeStarts("logret-fail");
            string busy = Path.Combine(l.Logs, "crash-20261027-080500-000-1.txt");
            LogRetention retention = RetentionFor(l);
            LogRetention.CleanResult result = retention.Clean(l.Logs, l.Fallback, null,
                p => p == busy ? throw new IOException("in use") : DeleteFile(p), LogRetention.BudgetMs);
            Assert.True(File.Exists(busy));
            Assert.Equal(l.Old.Count - 1, result.Deleted.Count);
            Assert.Equal(1, result.Failed);
            Assert.EndsWith("; 1 could not be deleted (IOException: in use)", result.Line);

            // A delete that reports false (the file is still there) counts as well.
            Layout r = ThreeStarts("logret-false");
            LogRetention.CleanResult refused = RetentionFor(r).Clean(r.Logs, r.Fallback, null, p => !p.EndsWith(".pb") && DeleteFile(p),
                                                                     LogRetention.BudgetMs);
            Assert.Equal(1, refused.Failed);
            Assert.EndsWith("; 1 could not be deleted", refused.Line);

            // No time at all: the first file, then the rest at the next start.
            Layout b = ThreeStarts("logret-budget");
            LogRetention.CleanResult stopped = RetentionFor(b).Clean(b.Logs, b.Fallback, null, DeleteFile, 0);
            Assert.Single(stopped.Deleted);
            Assert.True(stopped.Stopped);
            Assert.Equal(0, stopped.Failed);
            Assert.EndsWith("; stopped after 0 s, the rest at the next start", stopped.Line);
            Assert.Equal(b.Old.Count - 1, b.Old.Count(File.Exists));
        }

        [Fact]
        public void TheLineCountsFilesAndMegabytes()
        {
            string root = TestEnvironment.NewTempDir("logret-line");
            string logs = Path.Combine(root, "logs");
            LogRetention retention = BeginAfter(logs, 1, S2, S1);
            DateTime old = S1.AddHours(-1);
            Plant(Path.Combine(logs, "server.log.2"), old, new string('x', 3 * 1024 * 1024));
            Plant(Path.Combine(logs, "server.log.3"), old, new string('x', 1024 * 1024 + 104858));
            Plant(Path.Combine(logs, "server.log.5"), old);
            Plant(Path.Combine(logs, "threaddump-20261028-101010.txt"), old);
            Plant(Path.Combine(logs, "threaddump-20261028-111010.txt"), old);
            LogRetention.CleanResult result = retention.Clean(logs, null, null, DeleteFile, LogRetention.BudgetMs);
            Assert.Equal("keeping this start and 1 previous (since 2026-10-28T19:02:11Z): deleted 5 files, 4.1 MB " +
                         "(server.log.2-3,5, threaddump 2)", result.Line);
            Assert.DoesNotContain('\n', result.Line);

            Plant(Path.Combine(logs, "exception-20261028-101010-000-1.txt"), old);
            Assert.Equal("keeping this start and 1 previous (since 2026-10-28T19:02:11Z): deleted 1 file, 0.0 MB (exception 1)",
                         retention.Clean(logs, null, null, DeleteFile, LogRetention.BudgetMs).Line);
        }

        [Fact]
        public void TheCleanupThreadWritesOneLine()
        {
            Layout l = ThreeStarts("logret-thread");
            var memory = new MemoryLogSink(LogLevel.Debug, 50);
            var logger = new Logger();
            logger.AddSink(memory);
            LogRetention retention = BeginAfter(l.Logs, 1, S2, S1);
            Assert.False(retention.Wait(0));
            retention.StartCleanup(null, l.Fallback, logger.For("logs"));
            retention.StartCleanup(null, l.Fallback, logger.For("logs"));   // once
            Assert.True(retention.Wait(30000));
            AssertLayout(l);
            LogEntry line = Assert.Single(memory.Snapshot());
            Assert.Equal(LogLevel.Info, line.Level);
            Assert.Equal("logs", line.Category);
            Assert.StartsWith("keeping this start and 1 previous (since 2026-10-28T19:02:11Z): deleted 16 files", line.Message);
        }

        [Fact]
        public void PatternsAndExitTraces()
        {
            foreach (string name in new[] { "crash-20261029-101010-000-1.txt", "exception-x.txt", "threaddump-20261029-101010.txt",
                                            "exit-123-anr.txt", "exit-123-tombstone.pb" })
                Assert.True(LogRetention.IsEventFile(name), name);
            foreach (string name in new[] { "crash-.txt", "crash-1.log", "exit-123.txt", "exit--anr", "server.log", "starts.txt",
                                            "restart-requested.txt", "mcvs-logs-20261029-101010.zip", "Crash-1.txt", "" })
                Assert.False(LogRetention.IsEventFile(name), name);

            Assert.True(LogRetention.IsFamilyFile("server.log", out string family, out bool rotated));
            Assert.Equal("server.log", family);
            Assert.False(rotated);
            Assert.True(LogRetention.IsFamilyFile("unknown-endpoints.jsonl.12", out family, out rotated));
            Assert.Equal("unknown-endpoints.jsonl", family);
            Assert.True(rotated);
            foreach (string name in new[] { "server.log.", "server.log.1a", "server.log.bak", "server.log.1.tmp", "server.logs", "Server.log",
                                            "server.log.12345678901", "other.log.1" })
                Assert.False(LogRetention.IsFamilyFile(name, out _, out _), name);

            Assert.False(LogRetention.KeepsExit(S1.AddSeconds(-1), S1));
            Assert.True(LogRetention.KeepsExit(S1, S1));
            Assert.True(LogRetention.KeepsExit(S1.AddSeconds(1), S1));
            Assert.True(LogRetention.KeepsExit(S2, null));
        }
    }
}
