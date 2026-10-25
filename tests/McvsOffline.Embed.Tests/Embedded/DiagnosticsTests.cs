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
                string nonFatal = Assert.Single(Directory.GetFiles(dir, "exception-*.txt"));
                Assert.Contains("inner[0] System.TimeoutException: t", File.ReadAllText(nonFatal));
            }
            finally
            {
                CrashReporter.Context = null;
                CrashReporter.FlushLogs = null;
                CrashReporter.Mirror = null;
                CrashReporter.SetCrashDirectory(null);
            }
        }

        [Fact]
        public void WatchdogReportsAStalledGameThreadAndItsRecovery()
        {
            var reports = new ConcurrentQueue<string>();
            FakeGameApplication.Instance = new FakeGameApplication();
            var watchdog = new GameThreadWatchdog(() => "last request #5 GET /config/c/datacenters", s => reports.Enqueue(s),
                                                  comm => "    \"main\" RUNNABLE\n", 50, new[] { 400, 800 });
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
                Thread.Sleep(1600);
                List<string> stalls = reports.Where(r => r.Contains("STALL")).ToList();
                Assert.Equal(2, stalls.Count);
                Assert.Contains("server: last request #5", stalls[0]);
                Assert.Contains("game thread " + watchdog.GameTid + ": '", stalls[0]);
                Assert.Contains(" syscall ", stalls[0]);
                Assert.DoesNotContain("Java stacks", stalls[0]);
                Assert.Contains("Java stacks:\n    \"main\" RUNNABLE", stalls[1]);
                Assert.Equal(1, FakeGameApplication.Instance.Context.Pending);   // at most one heartbeat outstanding

                FakeGameApplication.Instance.Context.Drain();
                Thread.Sleep(200);
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
