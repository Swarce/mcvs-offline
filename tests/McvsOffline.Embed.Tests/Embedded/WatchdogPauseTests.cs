using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using McvsOffline.Embedded.Diagnostics;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The watchdog lease (design §8.5): while the settings page is open the game loop is paused on purpose (main activity
    /// paused), which must not produce STALL snapshots or thread dumps; when the lease ends normal detection resumes.
    /// </summary>
    public class WatchdogPauseTests
    {
        [Fact]
        public void APausedGameThreadIsNotAStallUntilTheLeaseEnds()
        {
            var reports = new ConcurrentQueue<string>();
            var dumps = new ConcurrentQueue<int>();
            PausedGame.Instance = new PausedGame();   // nobody drains its queue: the game thread runs no frame
            var watchdog = new GameThreadWatchdog(() => "server state", s => reports.Enqueue(s), comm => "    \"main\" RUNNABLE\n", 50,
                                                  new[] { 300, 600 }, (tid, comm) => dumps.Enqueue(tid));
            Assert.True(watchdog.Bind(typeof(PausedGame)));
            watchdog.PauseFor(1200, "MCVS Offline page open");
            watchdog.Start();
            try
            {
                Assert.True(SpinWait.SpinUntil(() => reports.Any(r => r.StartsWith("[watchdog] paused:", StringComparison.Ordinal)), 5000));
                Thread.Sleep(700);   // longer than both report thresholds
                Assert.DoesNotContain(reports, r => r.Contains("STALL"));
                Assert.Empty(dumps);
                Assert.Contains("paused: MCVS Offline page open", watchdog.Describe());

                // A renewed lease (the page pings) keeps it paused without a second line.
                watchdog.PauseFor(1000, "MCVS Offline page open");
                Thread.Sleep(300);
                Assert.DoesNotContain(reports, r => r.Contains("STALL"));

                // The lease runs out: "resumed", then the normal report after the first threshold, measured from the resume.
                Assert.True(SpinWait.SpinUntil(() => reports.Any(r => r == "[watchdog] resumed"), 5000));
                Assert.True(SpinWait.SpinUntil(() => reports.Any(r => r.Contains("STALL")), 5000));
                List<string> all = reports.ToList();
                int resumed = all.IndexOf("[watchdog] resumed");
                Assert.True(all.FindIndex(r => r.Contains("STALL")) > resumed);
                Assert.Equal(1, all.Count(r => r.StartsWith("[watchdog] paused:", StringComparison.Ordinal)));
                Assert.Equal(1, all.Count(r => r == "[watchdog] resumed"));
                Assert.Equal("[watchdog] paused: MCVS Offline page open", all.First(r => r.StartsWith("[watchdog] paused:", StringComparison.Ordinal)));
                Assert.DoesNotContain("paused:", watchdog.Describe());
            }
            finally
            {
                watchdog.Stop();
                PausedGame.Instance = null;
            }
        }

        [Fact]
        public void PauseForZeroEndsTheLeaseAndTheLatestCallWins()
        {
            var watchdog = new GameThreadWatchdog(null, null, null);
            string reason;
            Assert.False(watchdog.IsPaused(out reason));
            watchdog.PauseFor(600000, "share target");
            Assert.True(watchdog.IsPaused(out reason));
            Assert.Equal("share target", reason);
            watchdog.PauseFor(45000, "MCVS Offline page open");   // the player came back: 45 s, not 10 minutes
            Assert.True(watchdog.IsPaused(out reason));
            Assert.Equal("MCVS Offline page open", reason);
            watchdog.PauseFor(0, null);
            Assert.False(watchdog.IsPaused(out reason));
            watchdog.PauseFor(30, "short");
            Thread.Sleep(80);
            Assert.False(watchdog.IsPaused(out reason));   // expired by itself
            watchdog.PauseFor(-5, "negative ends it too");
            Assert.False(watchdog.IsPaused(out reason));
        }

        [Fact]
        public void EndingTheLeaseResumesStallDetection()
        {
            var reports = new ConcurrentQueue<string>();
            PausedGame2.Instance = new PausedGame2();
            var watchdog = new GameThreadWatchdog(null, s => reports.Enqueue(s), null, 50, new[] { 300 }, null);
            Assert.True(watchdog.Bind(typeof(PausedGame2)));
            watchdog.PauseFor(600000, "share target");
            watchdog.Start();
            try
            {
                Assert.True(SpinWait.SpinUntil(() => reports.Contains("[watchdog] paused: share target"), 5000));
                Thread.Sleep(500);
                Assert.DoesNotContain(reports, r => r.Contains("STALL"));
                watchdog.PauseFor(0, null);   // CloseBrowser
                Assert.True(SpinWait.SpinUntil(() => reports.Contains("[watchdog] resumed"), 5000));
                Assert.True(SpinWait.SpinUntil(() => reports.Any(r => r.Contains("STALL")), 5000));
            }
            finally
            {
                watchdog.Stop();
                PausedGame2.Instance = null;
            }
        }

        /// <summary>The members the watchdog reads from Gameloft.Engine.GameApplication.</summary>
        private sealed class PausedGame
        {
            public static PausedGame Instance { get; set; }

            public QueueContext Context { get; } = new QueueContext();
        }

        private sealed class PausedGame2
        {
            public static PausedGame2 Instance { get; set; }

            public QueueContext Context { get; } = new QueueContext();
        }

        /// <summary>A game-thread queue nobody drains.</summary>
        private sealed class QueueContext : SynchronizationContext
        {
            private int posted;

            public override void Post(SendOrPostCallback d, object state)
            {
                Interlocked.Increment(ref posted);
            }
        }
    }
}
