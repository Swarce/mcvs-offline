using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Diagnostics;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>The crash reporter and the game-thread watchdog, off the device.</summary>
    public class DiagnosticsTests
    {
        [Fact]
        public void CrashReportsGoToAFileOnceEachAndNeverThrow()
        {
            string dir = TestEnvironment.NewTempDir("crash");
            CrashReporter.Install();
            CrashReporter.ResetForTests();
            Assert.Contains("AppDomain ok", CrashReporter.Hooks);
            Assert.Contains("unobserved-tasks ok", CrashReporter.Hooks);   // the backing field exists on net8 too

            int flushes = 0;
            var mirrored = new List<string>();
            CrashReporter.SetCrashDirectory(dir);
            CrashReporter.Context = () => "boot: 26 requests, last #26 GET /verify";
            CrashReporter.FlushLogs = budget => flushes++;
            CrashReporter.Mirror = p => mirrored.Add(p);
            try
            {
                var ex = new NullReferenceException("probe from Game.InputSettingsComponent.OnStart");
                CrashReporter.Report("AppDomain.UnhandledException", ex, true);
                CrashReporter.Report("AppDomain.UnhandledException", ex, true);   // same object (raiser, then AppDomain): once

                string[] files = Directory.GetFiles(dir, "crash-*.txt");
                Assert.Single(files);
                string text = File.ReadAllText(files[0]);
                Assert.StartsWith("FATAL unhandled exception via AppDomain.UnhandledException", text);
                Assert.Contains("probe from Game.InputSettingsComponent.OnStart", text);
                Assert.Contains("--- context ---\nboot: 26 requests", text);
                Assert.Equal(1, flushes);
                Assert.Equal(new[] { files[0] }, mirrored);
                Assert.Equal(files[0], CrashReporter.LastCrashFile);

                CrashReporter.Context = () => throw new InvalidOperationException("context broken");
                CrashReporter.Report("TaskScheduler.UnobservedTaskException", new AggregateException(new TimeoutException("t")), false);
                // Any() rather than Single(): the hook is live, so a real unobserved fault of another test may land here too.
                Assert.Contains(Directory.GetFiles(dir, "exception-*.txt"),
                                f => File.ReadAllText(f).Contains("inner[0] System.TimeoutException: t"));
            }
            finally
            {
                CrashReporter.Context = null;
                CrashReporter.FlushLogs = null;
                CrashReporter.Mirror = null;
                CrashReporter.SetCrashDirectory(null);
                CrashReporter.ResetForTests();
            }
        }

        /// <summary>
        /// Review finding: one shared budget of 5 let five unobserved task faults (routine: MoreAsync.WithCancellation never
        /// observes the task it abandons) silence the FATAL report that follows. Fatal and non-fatal budgets are separate.
        /// </summary>
        [Fact]
        public void NonFatalReportsNeverUseUpTheFatalReport()
        {
            string dir = TestEnvironment.NewTempDir("crash-budget");
            CrashReporter.Install();
            CrashReporter.ResetForTests();
            int flushes = 0;
            var mirrored = new List<string>();
            CrashReporter.SetCrashDirectory(dir);
            CrashReporter.FlushLogs = budget => flushes++;
            CrashReporter.Mirror = p => mirrored.Add(p);
            try
            {
                // An object first reported as an unobserved fault, then as the crash, gets its fatal report too.
                var both = new InvalidOperationException("first unobserved, then fatal");
                CrashReporter.Report("TaskScheduler.UnobservedTaskException", both, false);
                CrashReporter.Report("AppDomain.UnhandledException", both, true);
                Assert.Single(Directory.GetFiles(dir, "crash-*.txt"));

                // Use up the rest of the non-fatal budget and more.
                for (int i = 0; i < CrashReporter.NonFatalBudget + 3; i++)
                    CrashReporter.Report("TaskScheduler.UnobservedTaskException",
                                         new AggregateException(new OperationCanceledException("abandoned " + i)), false);
                // Exactly the budget, whatever the source (the hook is live: a real unobserved fault would count too).
                Assert.Equal(CrashReporter.NonFatalBudget, Directory.GetFiles(dir, "exception-*.txt").Length);
                int flushesBefore = flushes;

                // The crash after them is still reported: logcat, crash file, flush, mirror.
                var crash = new NullReferenceException("probe: InputSettingsComponent.OnStart after the non-fatal budget");
                CrashReporter.Report("AndroidEnvironment.UnhandledExceptionRaiser", crash, true);
                CrashReporter.Report("AppDomain.UnhandledException", crash, true);   // same object: once
                string[] crashes = Directory.GetFiles(dir, "crash-*.txt");
                Assert.Equal(2, crashes.Length);
                string file = Assert.Single(crashes, f => File.ReadAllText(f).Contains("after the non-fatal budget"));
                Assert.StartsWith("FATAL unhandled exception via AndroidEnvironment.UnhandledExceptionRaiser", File.ReadAllText(file));
                Assert.Equal(flushesBefore + 1, flushes);
                Assert.Equal(file, mirrored.Last());
                Assert.Equal(file, CrashReporter.LastCrashFile);

                // The fatal budget is bounded too.
                for (int i = 0; i < CrashReporter.FatalBudget + 2; i++)
                    CrashReporter.Report("AppDomain.UnhandledException", new Exception("fatal " + i), true);
                Assert.Equal(CrashReporter.FatalBudget, Directory.GetFiles(dir, "crash-*.txt").Length);
            }
            finally
            {
                CrashReporter.FlushLogs = null;
                CrashReporter.Mirror = null;
                CrashReporter.SetCrashDirectory(null);
                CrashReporter.ResetForTests();
            }
        }

        /// <summary>Design §5.4: crash and exception files are mirrored to Download/ and zipped for sharing: no ticket or password in them.</summary>
        [Fact]
        public void CrashFilesAreRedacted()
        {
            string dir = TestEnvironment.NewTempDir("crash-redact");
            CrashReporter.Install();
            CrashReporter.ResetForTests();
            CrashReporter.SetCrashDirectory(dir);
            const string ticket = "0123456789abcdef0123456789abcdef";
            try
            {
                CrashReporter.Context = () => "last request POST /auth/authorize password=hunter2secret";
                CrashReporter.Report("AppDomain.UnhandledException",
                                     new InvalidOperationException("could not open http://127.0.0.1:51780/_mcvs/ui/#t=" + ticket + " (cookie mcvs_s=" + ticket + ticket + ")"), true);
                string text = File.ReadAllText(Assert.Single(Directory.GetFiles(dir, "crash-*.txt")));
                Assert.Contains("/_mcvs/ui/#t=<redacted>", text);
                Assert.Contains("mcvs_s=<redacted>", text);
                Assert.Contains("password=hunt…<redacted>", text);
                Assert.DoesNotContain(ticket, text);
                Assert.DoesNotContain("hunter2secret", text);
                Assert.Contains("FATAL unhandled exception via AppDomain.UnhandledException", text);
            }
            finally
            {
                CrashReporter.Context = null;
                CrashReporter.SetCrashDirectory(null);
                CrashReporter.ResetForTests();
            }
        }

        /// <summary>
        /// Design §7.5, D7 (the 0-byte uploads): every copy goes through a new temp file renamed over the old copy, one media scan per
        /// cycle lists what changed, rotated logs, unknown endpoints and this session's dumps are mirrored, old sessions and diagnostics
        /// pruned, leftover temp files deleted.
        /// </summary>
        [Fact]
        public void TheLogMirrorWritesThroughTempFilesAndAsksForAScan()
        {
            string downloads = TestEnvironment.NewTempDir("mirror-downloads");
            string logs = TestEnvironment.NewTempDir("mirror-logs");
            string target = Path.Combine(downloads, "MCVS-Offline");
            Directory.CreateDirectory(target);
            // Leftovers: a killed copy's temp file, seven old sessions, 25 old crash copies, a file of an earlier install.
            File.WriteAllText(Path.Combine(target, "20200101-000000-server.log.txt.3.tmp"), "partial");
            for (int i = 1; i <= 7; i++) File.WriteAllText(Path.Combine(target, "2020010" + i + "-000000-bootstrap.log.txt"), "old");
            for (int i = 0; i < 25; i++)
            {
                string crash = Path.Combine(target, "crash-20200101-0000" + i.ToString("00") + "-000-1.txt");
                File.WriteAllText(crash, "old crash");
                File.SetLastWriteTimeUtc(crash, new DateTime(2020, 1, 1, 0, 0, i, DateTimeKind.Utc));
            }
            File.WriteAllText(Path.Combine(target, "notes.txt"), "not ours");

            string server = Path.Combine(logs, "server.log"), bootstrap = Path.Combine(logs, "bootstrap.log"), game = Path.Combine(logs, "game.log");
            File.WriteAllText(server, "server line 1\n");
            File.WriteAllText(server + ".1", "the end of the previous session\n");
            File.WriteAllText(bootstrap, "boot\n");
            File.WriteAllText(Path.Combine(logs, "unknown-endpoints.jsonl"), "{\"path\":\"/x\"}\n");
            File.WriteAllText(Path.Combine(logs, "threaddump-20261026-101500.txt"), "dump");
            File.WriteAllText(Path.Combine(logs, "exit-1234-anr.txt"), "anr");
            string oldDump = Path.Combine(logs, "exception-20200101-000000-000-1.txt");
            File.WriteAllText(oldDump, "an older session's exception");
            File.SetLastWriteTimeUtc(oldDump, DateTime.UtcNow.AddDays(-2));

            var scans = new List<string[]>();
            var lines = new List<string>();
            var mirror = new McvsOffline.Embedded.Android.PublicMirror(new[] { bootstrap, server, game }, logs, s => lines.Add(s))
            {
                SdkInt = () => 34,
                DownloadsDir = () => downloads,
                Scan = paths => scans.Add(paths),
            };
            Assert.Equal("starting", mirror.Describe());
            Assert.True(mirror.Open());
            string p = mirror.SessionPrefix;
            Assert.True(File.Exists(Path.Combine(target, p + "session.txt")));
            Assert.Empty(Directory.GetFiles(target, "*.tmp"));
            Assert.Equal(new[] { Path.Combine(target, p + "session.txt") }, Assert.Single(scans));
            // 5 sessions kept (ours and the four newest old ones), 20 diagnostics, the foreign file untouched.
            Assert.Equal(4, Directory.GetFiles(target, "2020010?-000000-bootstrap.log.txt").Length);
            Assert.False(File.Exists(Path.Combine(target, "20200103-000000-bootstrap.log.txt")));
            Assert.Equal(20, Directory.GetFiles(target, "crash-*.txt").Length);
            Assert.False(File.Exists(Path.Combine(target, "crash-20200101-000004-000-1.txt")));
            Assert.True(File.Exists(Path.Combine(target, "crash-20200101-000005-000-1.txt")));
            Assert.True(File.Exists(Path.Combine(target, "notes.txt")));

            scans.Clear();
            mirror.CopyAll();
            string[] scanned = Assert.Single(scans);
            var expected = new[]
            {
                p + "bootstrap.log.txt", p + "server.log.txt", p + "server.log.1.txt", p + "unknown-endpoints.jsonl.txt",
                "threaddump-20261026-101500.txt", "exit-1234-anr.txt",
            };
            Assert.Equal(expected.Select(n => Path.Combine(target, n)).OrderBy(x => x), scanned.OrderBy(x => x));
            Assert.Equal("server line 1\n", File.ReadAllText(Path.Combine(target, p + "server.log.txt")));
            Assert.Equal("the end of the previous session\n", File.ReadAllText(Path.Combine(target, p + "server.log.1.txt")));
            Assert.False(File.Exists(Path.Combine(target, "exception-20200101-000000-000-1.txt")));   // an older session's: not again
            Assert.Empty(Directory.GetFiles(target, "*.tmp"));
            Assert.StartsWith("active, 7 files, last copy ", mirror.Describe());

            // Nothing changed: no copy, no scan.
            scans.Clear();
            mirror.CopyAll();
            Assert.Empty(scans);

            // The log grows: only that copy is rewritten (through a temp file) and scanned; a rotation copies .1 once more.
            File.AppendAllText(server, "server line 2\n");
            mirror.CopyAll();
            Assert.Equal(new[] { Path.Combine(target, p + "server.log.txt") }, Assert.Single(scans));
            Assert.Equal("server line 1\nserver line 2\n", File.ReadAllText(Path.Combine(target, p + "server.log.txt")));
            scans.Clear();
            File.WriteAllText(server + ".1", "rotated again\n");
            mirror.CopyAll();
            Assert.Equal(new[] { Path.Combine(target, p + "server.log.1.txt") }, Assert.Single(scans));

            // The crash path copies at once and scans.
            scans.Clear();
            string crashFile = Path.Combine(logs, "crash-20261026-101600-000-1.txt");
            File.WriteAllText(crashFile, "FATAL");
            mirror.CopyCrashFile(crashFile);
            Assert.Contains(Path.Combine(target, "crash-20261026-101600-000-1.txt"), Assert.Single(scans));
            // Still at most 20 crash/thread-dump/exception/exit copies in all: the oldest crash copies made room.
            Assert.Equal(20, Directory.GetFiles(target, "crash-*.txt").Length + Directory.GetFiles(target, "threaddump-*").Length +
                             Directory.GetFiles(target, "exit-*").Length);
            Assert.True(File.Exists(Path.Combine(target, "crash-20261026-101600-000-1.txt")));
            Assert.True(File.Exists(Path.Combine(target, "threaddump-20261026-101500.txt")));
            Assert.True(File.Exists(Path.Combine(target, "exit-1234-anr.txt")));
        }

        [Fact]
        public void TheLogMirrorStaysOffBeforeAndroid11()
        {
            var lines = new List<string>();
            var mirror = new McvsOffline.Embedded.Android.PublicMirror(new string[0], null, s => lines.Add(s))
            {
                SdkInt = () => 29,
                DownloadsDir = () => TestEnvironment.NewTempDir("mirror-29"),
            };
            Assert.False(mirror.Open());
            Assert.Equal("off: Android API 29 (< 30)", mirror.Describe());
            Assert.Contains("[mirror] Android API 29 (< 30): no copy of the logs in Download/", lines);
        }

        [Fact]
        public void WatchdogReportsAStalledGameThreadAndItsRecovery()
        {
            var reports = new ConcurrentQueue<string>();
            var dumps = new ConcurrentQueue<int>();
            FakeGameApplication.Instance = new FakeGameApplication();
            var watchdog = new GameThreadWatchdog(() => "last request #5 GET /config/c/datacenters", s => reports.Enqueue(s),
                                                  comm => "    \"main\" RUNNABLE\n", 50, new[] { 400, 800 },
                                                  (tid, comm) => dumps.Enqueue(tid));
            Assert.True(watchdog.Bind(typeof(FakeGameApplication)));
            watchdog.Start();
            try
            {
                // A pumping game thread: no report.
                var pump = new Thread(() =>
                {
                    int until = Environment.TickCount + 600;
                    while (Environment.TickCount < until)
                    {
                        FakeGameApplication.Instance.Context.Drain();
                        FakeGameApplication.Instance.totalTime += 0.016;
                        Thread.Sleep(10);
                    }
                });
                pump.Start();
                pump.Join();
                Assert.True(watchdog.Beats > 0);
                Assert.Empty(reports.Where(r => r.Contains("STALL")));
                Assert.NotEqual(0, watchdog.GameTid);   // gettid() through libc on Linux

                // The game thread stops draining its queue: snapshots at 400 and 800 ms, Java stacks in the second.
                SpinWait.SpinUntil(() => reports.Count(r => r.Contains("STALL")) >= 2, 5000);
                List<string> stalls = reports.Where(r => r.Contains("STALL")).ToList();
                Assert.Equal(2, stalls.Count);
                Assert.Contains("server: last request #5", stalls[0]);
                Assert.Contains("game thread " + watchdog.GameTid + ": '", stalls[0]);
                Assert.Contains(" syscall ", stalls[0]);
                Assert.DoesNotContain("Java stacks", stalls[0]);
                Assert.Contains("Java stacks:\n    \"main\" RUNNABLE", stalls[1]);
                SpinWait.SpinUntil(() => dumps.Count > 0, 5000);   // the thread dump starts with the second snapshot
                Assert.Equal(new[] { watchdog.GameTid }, dumps.ToArray());
                Assert.Equal(1, FakeGameApplication.Instance.Context.Pending);   // at most one heartbeat outstanding

                // A recovered game thread keeps draining its queue; a single drain can race the watchdog's loop.
                SpinWait.SpinUntil(() =>
                {
                    FakeGameApplication.Instance.Context.Drain();
                    return reports.Any(r => r.Contains("runs again"));
                }, 5000);
                Assert.Contains(reports, r => r.Contains("runs again"));

                // In the background the game does not update: not a stall.
                FakeGameApplication.Instance.suspendUpdateInBackground = true;
                int before = reports.Count(r => r.Contains("STALL"));
                Thread.Sleep(1000);
                Assert.Equal(before, reports.Count(r => r.Contains("STALL")));
            }
            finally
            {
                watchdog.Stop();
                FakeGameApplication.Instance = null;
            }
        }

        [Fact]
        public void NativeThreadsDescribeThisProcess()
        {
            int tid = NativeThreads.CurrentTid();
            Assert.True(tid > 0);
            Assert.Contains(tid, NativeThreads.Tids());
            string line = NativeThreads.Describe(tid, true);
            Assert.Contains(" state R", line);   // the calling thread is running
            Assert.Equal("futex: waiting for a lock or condition", NativeThreads.SyscallName("98 0x7 0x80 0x0"));
            Assert.Equal("in user space", NativeThreads.SyscallName("running"));
            Assert.True(NativeThreads.CpuTicks(NativeThreads.StatFields(tid)) >= 0);
        }

        [Fact]
        public void ThreadDumpShowsWhereABlockedThreadSleeps()
        {
            var gate = new object();
            int blockedTid = 0;
            var entered = new ManualResetEventSlim(false);
            var sections = new List<string>();
            string file = Path.Combine(Path.GetTempPath(), "mcvs-threaddump-test-" + Guid.NewGuid().ToString("N") + ".txt");
            Thread blocked = null;
            Monitor.Enter(gate);
            try
            {
                blocked = new Thread(() =>
                {
                    blockedTid = NativeThreads.CurrentTid();
                    entered.Set();
                    lock (gate) { }   // sleeps in a futex until the test lets go
                });
                blocked.IsBackground = true;
                blocked.Start();
                entered.Wait(5000);
                SpinWait.SpinUntil(() => (NativeThreads.Read("/proc/self/task/" + blockedTid + "/wchan") ?? "").Contains("futex"), 5000);

                ThreadDump.Capture(blockedTid, ThreadDump.Related(blockedTid, "no-such-comm"), file, 1000, s => sections.Add(s));
            }
            finally
            {
                Monitor.Exit(gate);
                blocked?.Join(5000);
                try { File.Delete(file); } catch (Exception) { }
            }
            string all = string.Join("\n", sections);
            Assert.StartsWith("[threaddump] native view: dumpable ", sections[0]);
            string line = sections[0].Split('\n').Single(l => l.StartsWith("  " + blockedTid + " ", StringComparison.Ordinal));
            Assert.Contains("futex(", line);   // syscall readable (dumpable raised and restored)
            Assert.Contains("timeout none (untimed wait)", line);
            Assert.Contains("[threaddump] native stack of " + blockedTid + " (raw):", all);
            Assert.Contains("libc", sections.First(s => s.StartsWith("[threaddump] native stack of " + blockedTid + " (named)", StringComparison.Ordinal)));
            Assert.Contains("[threaddump] managed dump unavailable", all);   // CoreCLR: no Mono runtime exports
        }

        [Fact]
        public void ThreadDumpFindsTheDumpFlagOfTheShippedRuntime()
        {
            // libmonosgen-2.0.so (Mono 2020-02/165f4b03417, arm64) at 0x1da64c: mono_threads_request_thread_dump.
            Assert.Equal(0x3735d0L, ThreadDump.DecodeDumpFlag(0xb0000cc8u, 0x320003e9u, 0x39174109u, 0x1400f9d5u, 0x1da64cL));
            Assert.Equal(0L, ThreadDump.DecodeDumpFlag(0xb0000cc8u, 0x52800029u, 0x39174109u, 0x1400f9d5u, 0x1da64cL));   // other code
            Assert.Equal(0L, ThreadDump.DecodeDumpFlag(0xd503201fu, 0x320003e9u, 0x39174109u, 0x1400f9d5u, 0x1da64cL));
        }

        /// <summary>The members the watchdog reads from Gameloft.Engine.GameApplication, by the same names.</summary>
        private sealed class FakeGameApplication
        {
            public static FakeGameApplication Instance { get; set; }

            public FakeContext Context { get; } = new FakeContext();

            internal double totalTime;
            internal bool suspendUpdateInBackground;
        }

        private sealed class FakeContext : SynchronizationContext
        {
            private readonly ConcurrentQueue<KeyValuePair<SendOrPostCallback, object>> items = new ConcurrentQueue<KeyValuePair<SendOrPostCallback, object>>();

            public int Pending { get { return items.Count; } }

            public override void Post(SendOrPostCallback d, object state)
            {
                items.Enqueue(new KeyValuePair<SendOrPostCallback, object>(d, state));
            }

            public void Drain()
            {
                while (items.TryDequeue(out var item)) item.Key(item.Value);
            }
        }
    }
}
