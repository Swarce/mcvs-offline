using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using McvsOffline.Embedded.Diagnostics;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The build-30 hitch probe of <see cref="GameThreadWatchdog"/>: the owner's S22 log had 1 to 5 s game-thread blocks on the
    /// results screen and at the match start, all under the first 5 s stall report, so the log never said where the game thread
    /// was. A block of at least <see cref="GameThreadWatchdog.HitchMs"/> now gets one "HITCH:" notice with the snapshot (and the
    /// thread dump for the first ones) and a "hitch over" line with how long the work item waited; no STALL report.
    /// </summary>
    public class WatchdogHitchTests
    {
        [Fact]
        public void ABlockOfASecondAndAHalfIsOneHitchWithItsLengthAndNoStall()
        {
            var reports = new ConcurrentQueue<string>();
            var notices = new ConcurrentQueue<string>();
            var dumps = new ConcurrentQueue<int>();
            var game = new HitchGame();
            HitchGame.Instance = game;
            var watchdog = new GameThreadWatchdog(() => "server state", s => reports.Enqueue(s), null, 200, new[] { 5000 }, (tid, comm) => dumps.Enqueue(tid))
            {
                Notice = s => notices.Enqueue(s),
                HitchMs = 400,
                HitchDumps = 1,
            };
            Assert.True(watchdog.Bind(typeof(HitchGame)));
            game.Start();
            watchdog.Start();
            try
            {
                Assert.True(SpinWait.SpinUntil(() => watchdog.Beats >= 5, 10000));
                Assert.DoesNotContain(notices, n => n.Contains("HITCH"));
                game.Block(1500);
                Assert.True(SpinWait.SpinUntil(() => notices.Any(n => n.StartsWith("[watchdog] hitch over:", StringComparison.Ordinal)), 10000),
                            "notices:\n" + string.Join("\n", notices) + "\nreports:\n" + string.Join("\n", reports));
                string hitch = notices.Single(n => n.StartsWith("[watchdog] HITCH: the game thread has not run a work item posted ", StringComparison.Ordinal));
                Assert.Contains("  server: server state", hitch);
                Assert.Contains("  game thread ", hitch);
                string over = notices.Single(n => n.StartsWith("[watchdog] hitch over:", StringComparison.Ordinal));
                int waited = int.Parse(Regex.Match(over, @"after (\d+) ms \(hitch 1 of this start\)").Groups[1].Value);
                Assert.InRange(waited, 400, 3000);
                Assert.True(SpinWait.SpinUntil(() => dumps.Count == 1, 5000));
                Assert.Equal(1, watchdog.Hitches);
                Assert.Contains("1 hitches of 400 ms or more (longest " + waited + " ms)", watchdog.Describe());

                // A second hitch: noticed and measured; no second dump (HitchDumps 1).
                game.Block(1000);
                Assert.True(SpinWait.SpinUntil(() => notices.Count(n => n.StartsWith("[watchdog] hitch over:", StringComparison.Ordinal)) == 2, 10000));
                Assert.Equal(2, watchdog.Hitches);
                Thread.Sleep(300);
                Assert.Single(dumps);
                Assert.DoesNotContain(reports, r => r.Contains("STALL"));
            }
            finally
            {
                watchdog.Stop();
                game.Stop();
                HitchGame.Instance = null;
            }
        }

        [Fact]
        public void WithoutHitchMsTheSameBlockLeavesNoLine()
        {
            var lines = new ConcurrentQueue<string>();
            var game = new HitchGame2();
            HitchGame2.Instance = game;
            var watchdog = new GameThreadWatchdog(null, s => lines.Enqueue(s), null, 200, new[] { 5000 }, null) { Notice = s => lines.Enqueue(s) };
            Assert.True(watchdog.Bind(typeof(HitchGame2)));
            game.Start();
            watchdog.Start();
            try
            {
                Assert.True(SpinWait.SpinUntil(() => watchdog.Beats >= 3, 10000));
                game.Block(1500);
                int beats = watchdog.Beats;
                Assert.True(SpinWait.SpinUntil(() => watchdog.Beats >= beats + 3, 10000));
                Assert.DoesNotContain(lines, l => l.Contains("HITCH") || l.Contains("hitch"));
                Assert.Equal(0, watchdog.Hitches);
                Assert.DoesNotContain("hitches", watchdog.Describe());
            }
            finally
            {
                watchdog.Stop();
                game.Stop();
                HitchGame2.Instance = null;
            }
        }

        /// <summary>A "game thread" that drains the context every 16 ms and can be blocked for a while.</summary>
        internal class GameLoop
        {
            private readonly ConcurrentQueue<(SendOrPostCallback, object)> queue = new ConcurrentQueue<(SendOrPostCallback, object)>();
            private volatile bool stopped;
            private int blockMs;

            public GameLoop()
            {
                Context = new LoopContext(this);
            }

            public SynchronizationContext Context { get; }

            public void Start()
            {
                new Thread(() =>
                {
                    while (!stopped)
                    {
                        int block = Interlocked.Exchange(ref blockMs, 0);
                        if (block > 0) Thread.Sleep(block);
                        while (queue.TryDequeue(out var item)) item.Item1(item.Item2);
                        Thread.Sleep(16);
                    }
                }) { IsBackground = true, Name = "fake-game" }.Start();
            }

            public void Block(int ms)
            {
                Interlocked.Exchange(ref blockMs, ms);
            }

            public void Stop()
            {
                stopped = true;
            }

            private sealed class LoopContext : SynchronizationContext
            {
                private readonly GameLoop loop;

                public LoopContext(GameLoop loop)
                {
                    this.loop = loop;
                }

                public override void Post(SendOrPostCallback d, object state)
                {
                    loop.queue.Enqueue((d, state));
                }
            }
        }

        private sealed class HitchGame : GameLoop
        {
            public static HitchGame Instance { get; set; }
        }

        private sealed class HitchGame2 : GameLoop
        {
            public static HitchGame2 Instance { get; set; }
        }
    }
}
