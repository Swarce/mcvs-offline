using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Embedded.Windows;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The watchdog's stall snapshot on PC (docs/design/pc-port.md §5.2, §6.3): WindowsThreads over injected ProcessThread samples
    /// (state, wait reason, CPU delta, game thread first), and GameThreadWatchdog writing those lines instead of the /proc ones.
    /// </summary>
    [Collection("GameHooks")]
    public class WindowsThreadsTests
    {
        private static List<ThreadSample> Samples(long gameCpu, long workerCpu, long idleCpu)
        {
            return new List<ThreadSample>
            {
                new ThreadSample { Id = 300, State = "Wait", WaitReason = "UserRequest", CpuMs = idleCpu },
                new ThreadSample { Id = 100, State = "Wait", WaitReason = "Executive", CpuMs = gameCpu },
                new ThreadSample { Id = 200, State = "Running", CpuMs = workerCpu },
                new ThreadSample { Id = 400, State = "Ready", CpuMs = -1 },
            };
        }

        [Fact]
        public void TheGameThreadComesFirstThenTheBusiestByProcessorTime()
        {
            List<ThreadSample> current = Samples(1000, 500, 20);
            var threads = new WindowsThreads(() => current);
            List<string> first = threads.Busiest(100, 12);
            Assert.Equal(new[] { "100 Wait (Executive), cpu 1000 ms +0 ms (game thread)" }, first);   // no deltas yet

            current = Samples(1010, 900, 20);
            List<string> second = threads.Busiest(100, 12);
            Assert.Equal(new[]
            {
                "100 Wait (Executive), cpu 1010 ms +10 ms (game thread)",
                "200 Running, cpu 900 ms +400 ms",
            }, second);
            Assert.Equal("Wait (Executive), cpu 1010 ms", threads.Describe(100));
            Assert.Equal("not in the process's thread list", threads.Describe(555));
            Assert.Null(threads.Name(100));
            Assert.Equal(1, threads.Busiest(100, 1).Count);
        }

        [Fact]
        public void AnUnreadableListIsReportedNotThrownByDescribe()
        {
            var threads = new WindowsThreads(() => { throw new InvalidOperationException("gone"); });
            Assert.Equal("unreadable (InvalidOperationException)", threads.Describe(1));
            Assert.Throws<InvalidOperationException>(() => threads.Busiest(1, 5));   // the watchdog reports the message
        }

        [Fact]
        public void TheRealProcessThreadListReadsOnThisMachine()
        {
            var threads = new WindowsThreads(null);
            List<string> lines = threads.Busiest(0, 50);
            Assert.NotNull(lines);
            Assert.DoesNotContain("not in", threads.Describe(System.Diagnostics.Process.GetCurrentProcess().Threads[0].Id));
        }

        [Fact]
        public void TheWatchdogWritesTheInjectedSnapshotInsteadOfProc()
        {
            var reports = new ConcurrentQueue<string>();
            FakeApp.Instance = new FakeApp();
            int cpu = 0;
            var snapshot = new WindowsThreads(() =>
            {
                cpu += 50;
                return new List<ThreadSample>
                {
                    new ThreadSample { Id = FakeApp.Instance.GameTid, State = "Wait", WaitReason = "UserRequest", CpuMs = 10 },
                    new ThreadSample { Id = 777, State = "Running", CpuMs = cpu },
                };
            });
            var watchdog = new GameThreadWatchdog(() => "server state", s => reports.Enqueue(s), null, 50, new[] { 300, 600 }, null) { Threads = snapshot };
            Assert.True(watchdog.Bind(typeof(FakeApp)));
            watchdog.Start();
            try
            {
                // One drained heartbeat tells the watchdog the game thread; then the "game thread" stops draining.
                var pump = new Thread(() =>
                {
                    SpinWait.SpinUntil(() => FakeApp.Instance.Context.Pending > 0, 5000);
                    FakeApp.Instance.GameTid = NativeThreads.CurrentTid();
                    FakeApp.Instance.Context.Drain();
                });
                pump.Start();
                pump.Join();
                SpinWait.SpinUntil(() => reports.Count(r => r.Contains("STALL")) >= 2, 10000);
                List<string> stalls = reports.Where(r => r.Contains("STALL")).ToList();
                Assert.True(stalls.Count >= 2, string.Join("\n", reports));
                Assert.Contains("  game thread " + watchdog.GameTid + ": Wait (UserRequest), cpu 10 ms\n", stalls[0]);
                Assert.Contains("  threads by processor time since the last snapshot (game thread first):\n", stalls[0]);
                Assert.Contains("(game thread)", stalls[0]);
                Assert.Contains("    777 Running, cpu ", stalls[1]);
                Assert.DoesNotContain("/proc", stalls[0]);
                Assert.DoesNotContain("syscall", stalls[0]);
            }
            finally
            {
                watchdog.Stop();
                FakeApp.Instance = null;
            }
        }

        [Fact]
        public void AThrowingSnapshotIsReportedWithItsSource()
        {
            var reports = new ConcurrentQueue<string>();
            FakeApp.Instance = new FakeApp();
            var watchdog = new GameThreadWatchdog(null, s => reports.Enqueue(s), null, 50, new[] { 300 }, null)
            {
                Threads = new WindowsThreads(() => { throw new InvalidOperationException("access denied"); }),
            };
            Assert.True(watchdog.Bind(typeof(FakeApp)));
            watchdog.Start();
            try
            {
                SpinWait.SpinUntil(() => reports.Any(r => r.Contains("STALL")), 10000);
                string stall = reports.First(r => r.Contains("STALL"));
                Assert.Contains("  game thread: unknown (no work item ever ran)\n", stall);
                Assert.Contains("  the process's thread list unreadable: access denied\n", stall);
            }
            finally
            {
                watchdog.Stop();
                FakeApp.Instance = null;
            }
        }

        private sealed class FakeApp
        {
            public static FakeApp Instance { get; set; }

            public QueueContext Context { get; } = new QueueContext();

            public volatile int GameTid;

            internal double totalTime;
            internal bool suspendUpdateInBackground;
        }

        private sealed class QueueContext : SynchronizationContext
        {
            private readonly ConcurrentQueue<KeyValuePair<SendOrPostCallback, object>> items = new ConcurrentQueue<KeyValuePair<SendOrPostCallback, object>>();

            public int Pending { get { return items.Count; } }

            public override void Post(SendOrPostCallback d, object state)
            {
                items.Enqueue(new KeyValuePair<SendOrPostCallback, object>(d, state));
            }

            public void Drain()
            {
                KeyValuePair<SendOrPostCallback, object> item;
                while (items.TryDequeue(out item)) item.Key(item.Value);
            }
        }
    }
}
