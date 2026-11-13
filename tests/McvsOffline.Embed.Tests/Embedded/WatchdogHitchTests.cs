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
    /// was. A frame clock that stood for at least <see cref="GameThreadWatchdog.HitchMs"/> now gets one "HITCH:" notice with the
    /// snapshot taken during the block (and a dump for the first ones) and a "hitch over" line with about how long it stood; no
    /// STALL report. Before <see cref="GameThreadWatchdog.HitchArmed"/> hitches are only counted.
    /// </summary>
    [Collection(WatchdogHitchCollection.Name)]
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
                string hitch = notices.Single(n => n.StartsWith("[watchdog] HITCH: the game thread has not finished a frame for ", StringComparison.Ordinal));
                Assert.Contains("  server: server state", hitch);
                Assert.Contains("  game thread ", hitch);
                Assert.Matches(@"for \d+ ms; frame clock \(totalTime\) unchanged for \d+ ms", hitch);
                string over = notices.Single(n => n.StartsWith("[watchdog] hitch over:", StringComparison.Ordinal));
                int waited = int.Parse(Regex.Match(over, @"finished a frame again after about (\d+) ms \(hitch 1 of this start\)").Groups[1].Value);
                Assert.InRange(waited, 1300, 3000);
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

        /// <summary>
        /// Review finding (build 30): the probe watched the once-a-second beat, so a 0.9 s block (the match start) was seen only when
        /// the beat happened to be posted in its first 0.2 s, about one block in twelve. With the host's own settings (beat every
        /// 1000 ms, HitchMs 700) a 1 s block is now seen whatever its phase, and the snapshot is taken while the game thread is
        /// still blocked.
        /// </summary>
        [Fact]
        public void WithTheHostsSettingsEveryOneSecondBlockIsSeenWhileItLasts()
        {
            var notices = new ConcurrentQueue<string>();
            var reports = new ConcurrentQueue<string>();
            var duringBlock = new ConcurrentQueue<bool>();
            var game = new HitchGame3();
            HitchGame3.Instance = game;
            // The snapshot's first read (the server state) says whether the game thread was still blocked when it was taken.
            var watchdog = new GameThreadWatchdog(() =>
                {
                    duringBlock.Enqueue(game.Blocked);
                    return "server state";
                }, s => reports.Enqueue(s), null, 1000, null, null)
            {
                Notice = s => notices.Enqueue(s),
                HitchMs = GameThreadWatchdog.DefaultHitchMs,
                HitchSnapshots = 100,
            };
            Assert.True(watchdog.Bind(typeof(HitchGame3)));
            game.Start();
            watchdog.Start();
            try
            {
                Assert.True(SpinWait.SpinUntil(() => watchdog.Beats >= 1, 10000));
                // Seven phases against the beat (posted once a second) and against the 100 ms poll.
                int[] phases = { 0, 140, 290, 430, 570, 710, 860 };
                for (int i = 0; i < phases.Length; i++)
                {
                    Thread.Sleep(300 + phases[i]);
                    game.Block(1000);
                    Assert.True(SpinWait.SpinUntil(() => notices.Count(n => n.StartsWith("[watchdog] hitch over:", StringComparison.Ordinal)) == i + 1, 5000),
                                "phase " + phases[i] + " ms: notices:\n" + string.Join("\n", notices) + "\nreports:\n" + string.Join("\n", reports));
                    Assert.Equal(i + 1, notices.Count(n => n.StartsWith("[watchdog] HITCH:", StringComparison.Ordinal)));
                }
                Assert.Equal(phases.Length, watchdog.Hitches);
                Assert.Equal(phases.Length, duringBlock.Count);
                Assert.All(duringBlock, b => Assert.True(b, "a HITCH snapshot was taken after the block had ended"));
                Assert.DoesNotContain(reports, r => r.Contains("STALL"));
            }
            finally
            {
                watchdog.Stop();
                game.Stop();
                HitchGame3.Instance = null;
            }
        }

        /// <summary>
        /// Review finding (build 30): the loading blocks of a start used up the two dumps (and WARN lines, and files in Downloads)
        /// before the match. Before <see cref="GameThreadWatchdog.HitchArmed"/> a hitch is only counted; after it, the snapshot and
        /// the hitch's own dump (<see cref="GameThreadWatchdog.HitchDump"/>), never the stall's thread dump.
        /// </summary>
        [Fact]
        public void HitchesBeforeArmingAreOnlyCountedAndAHitchUsesItsOwnDump()
        {
            var notices = new ConcurrentQueue<string>();
            var stallDumps = new ConcurrentQueue<int>();
            var hitchDumps = new ConcurrentQueue<int>();
            bool armed = false;
            var game = new HitchGame4();
            HitchGame4.Instance = game;
            var watchdog = new GameThreadWatchdog(null, s => notices.Enqueue(s), null, 200, new[] { 5000 }, (tid, comm) => stallDumps.Enqueue(tid))
            {
                Notice = s => notices.Enqueue(s),
                HitchMs = 400,
                HitchArmed = () => Volatile.Read(ref armed),
                HitchDump = (tid, comm) => hitchDumps.Enqueue(tid),
            };
            Assert.True(watchdog.Bind(typeof(HitchGame4)));
            game.Start();
            watchdog.Start();
            try
            {
                Assert.True(SpinWait.SpinUntil(() => watchdog.Beats >= 3, 10000));
                game.Block(1000);
                Assert.True(SpinWait.SpinUntil(() => watchdog.Hitches == 1, 5000));
                Assert.True(SpinWait.SpinUntil(() => !game.Blocked, 5000));
                Thread.Sleep(300);
                Assert.DoesNotContain(notices, n => n.Contains("HITCH") || n.Contains("hitch over"));
                Assert.Empty(hitchDumps);
                Assert.Contains("1 hitches of 400 ms or more (1 before the probe was armed; longest ", watchdog.Describe());

                Volatile.Write(ref armed, true);
                Assert.True(SpinWait.SpinUntil(() => notices.Any(n => n.StartsWith("[watchdog] hitch probe armed: 1 hitch of 400 ms or more before", StringComparison.Ordinal)), 5000),
                            string.Join("\n", notices));
                game.Block(1000);
                Assert.True(SpinWait.SpinUntil(() => notices.Any(n => n.StartsWith("[watchdog] hitch over:", StringComparison.Ordinal)), 5000), string.Join("\n", notices));
                Assert.Single(notices, n => n.StartsWith("[watchdog] HITCH:", StringComparison.Ordinal));
                Assert.Contains(notices, n => n.Contains("(hitch 1 of this start)"));
                Assert.True(SpinWait.SpinUntil(() => hitchDumps.Count == 1, 5000));
                Assert.Empty(stallDumps);
                Assert.Equal(2, watchdog.Hitches);
            }
            finally
            {
                watchdog.Stop();
                game.Stop();
                HitchGame4.Instance = null;
            }
        }

        /// <summary>
        /// Android: in the background the native loop stops calling UpdateWorlds (the frame clock stands) and only
        /// <c>GameApplication.Interrupted</c> says so; that is not a hitch.
        /// </summary>
        [Fact]
        public void TheFrameClockStandingInTheBackgroundIsNoHitch()
        {
            var notices = new ConcurrentQueue<string>();
            var game = new HitchGame5();
            HitchGame5.Instance = game;
            var watchdog = new GameThreadWatchdog(null, s => notices.Enqueue(s), null, 200, new[] { 5000 }, null)
            {
                Notice = s => notices.Enqueue(s),
                HitchMs = 400,
            };
            Assert.True(watchdog.Bind(typeof(HitchGame5)));
            game.Start();
            watchdog.Start();
            try
            {
                Assert.True(SpinWait.SpinUntil(() => watchdog.Beats >= 3, 10000));
                game.Interrupted = true;
                Thread.Sleep(1500);
                game.Interrupted = false;
                Thread.Sleep(500);
                Assert.Equal(0, watchdog.Hitches);
                Assert.DoesNotContain(notices, n => n.Contains("HITCH"));

                // The same watchdog still sees a real block afterwards.
                game.Block(1000);
                Assert.True(SpinWait.SpinUntil(() => watchdog.Hitches == 1, 5000), string.Join("\n", notices));
            }
            finally
            {
                watchdog.Stop();
                game.Stop();
                HitchGame5.Instance = null;
            }
        }

        /// <summary>
        /// Build 32 (the owner's build-31 log had one managed dump for 14 hitches, and no times for the boot's hitches): before
        /// arming each hitch gets one line when it ends (up to <see cref="GameThreadWatchdog.EarlyHitchLines"/>); after arming the
        /// first <see cref="GameThreadWatchdog.HitchManagedDumps"/> hitches, at least <see cref="GameThreadWatchdog.HitchManagedDumpSpacingMs"/>
        /// apart, start the managed dump instead of the native one, then the native one as before.
        /// </summary>
        [Fact]
        public void EarlyHitchesGetALineAndTheFirstArmedOnesAManagedDump()
        {
            var notices = new ConcurrentQueue<string>();
            var nativeDumps = new ConcurrentQueue<int>();
            var managedDumps = new ConcurrentQueue<int>();
            bool armed = false;
            var game = new HitchGame6();
            HitchGame6.Instance = game;
            var watchdog = new GameThreadWatchdog(null, s => notices.Enqueue(s), null, 200, new[] { 5000 }, null)
            {
                Notice = s => notices.Enqueue(s),
                HitchMs = 400,
                HitchArmed = () => Volatile.Read(ref armed),
                HitchDumps = 5,
                HitchDump = (tid, comm) => nativeDumps.Enqueue(tid),
                HitchManagedDumps = 2,
                HitchManagedDumpSpacingMs = 0,
                HitchManagedDump = (tid, comm) => managedDumps.Enqueue(tid),
                EarlyHitchLines = 1,
            };
            Assert.True(watchdog.Bind(typeof(HitchGame6)));
            game.Start();
            watchdog.Start();
            try
            {
                Assert.True(SpinWait.SpinUntil(() => watchdog.Beats >= 3, 10000));
                for (int i = 1; i <= 2; i++)
                {
                    game.Block(1000);
                    Assert.True(SpinWait.SpinUntil(() => watchdog.Hitches == i, 5000));
                    Assert.True(SpinWait.SpinUntil(() => !game.Blocked, 5000));
                    Thread.Sleep(400);
                }
                // One line for the first early hitch (EarlyHitchLines 1), none for the second; no snapshot, no dump.
                Assert.Single(notices, n => n.StartsWith("[watchdog] hitch before the probe was armed: the frame clock stood about ", StringComparison.Ordinal));
                Assert.Contains(notices, n => n.Contains("(early hitch 1; no snapshot)"));
                Assert.DoesNotContain(notices, n => n.Contains("HITCH:"));
                Assert.Empty(nativeDumps);
                Assert.Empty(managedDumps);

                Volatile.Write(ref armed, true);
                Assert.True(SpinWait.SpinUntil(() => notices.Any(n => n.StartsWith("[watchdog] hitch probe armed: 2 hitches", StringComparison.Ordinal)), 5000),
                            string.Join("\n", notices));
                for (int i = 1; i <= 3; i++)
                {
                    game.Block(1000);
                    Assert.True(SpinWait.SpinUntil(() => watchdog.Hitches == 2 + i, 5000), string.Join("\n", notices));
                    Assert.True(SpinWait.SpinUntil(() => !game.Blocked, 5000));
                    Thread.Sleep(400);
                }
                Assert.True(SpinWait.SpinUntil(() => managedDumps.Count == 2 && nativeDumps.Count == 1, 5000),
                            managedDumps.Count + " managed, " + nativeDumps.Count + " native");
                Assert.Contains(notices, n => n.StartsWith("[watchdog] hitch 1: a managed thread dump (1 of at most 2 per start), off the game thread", StringComparison.Ordinal));
                Assert.Contains(notices, n => n.StartsWith("[watchdog] hitch 2: a managed thread dump (2 of at most 2 per start)", StringComparison.Ordinal));
                Assert.Single(notices, n => n.StartsWith("[watchdog] hitch before the probe was armed", StringComparison.Ordinal));
            }
            finally
            {
                watchdog.Stop();
                game.Stop();
                HitchGame6.Instance = null;
            }
        }

        /// <summary>
        /// Build 32 review: the spacing of the managed hitch dumps. A hitch that starts within
        /// <see cref="GameThreadWatchdog.HitchManagedDumpSpacingMs"/> of the last managed dump gets the native dump instead (one
        /// of <see cref="GameThreadWatchdog.HitchDumps"/>); a later one gets the next managed dump.
        /// </summary>
        [Fact]
        public void AHitchSoonAfterAManagedDumpGetsTheNativeOneAndALaterOneTheNextManagedOne()
        {
            var notices = new ConcurrentQueue<string>();
            var nativeDumps = new ConcurrentQueue<int>();
            var managedDumps = new ConcurrentQueue<int>();
            var game = new HitchGame7();
            HitchGame7.Instance = game;
            var watchdog = new GameThreadWatchdog(null, s => notices.Enqueue(s), null, 200, new[] { 5000 }, null)
            {
                Notice = s => notices.Enqueue(s),
                HitchMs = 400,
                HitchDumps = 5,
                HitchDump = (tid, comm) => nativeDumps.Enqueue(tid),
                HitchManagedDumps = 3,
                HitchManagedDumpSpacingMs = 4000,
                HitchManagedDump = (tid, comm) => managedDumps.Enqueue(tid),
            };
            Assert.True(watchdog.Bind(typeof(HitchGame7)));
            game.Start();
            watchdog.Start();
            try
            {
                Assert.True(SpinWait.SpinUntil(() => watchdog.Beats >= 3, 10000));
                // Hitch 1: managed. Hitch 2 starts about 1.8 s later: within the 4 s, so the native dump.
                for (int i = 1; i <= 2; i++)
                {
                    game.Block(1000);
                    Assert.True(SpinWait.SpinUntil(() => watchdog.Hitches == i, 5000), string.Join("\n", notices));
                    Assert.True(SpinWait.SpinUntil(() => !game.Blocked, 5000));
                    Thread.Sleep(400);
                }
                Assert.True(SpinWait.SpinUntil(() => managedDumps.Count == 1 && nativeDumps.Count == 1, 5000),
                            managedDumps.Count + " managed, " + nativeDumps.Count + " native");
                Assert.Single(notices, n => n.Contains(": a managed thread dump ("));
                // Hitch 3 starts more than 4 s after hitch 1: the second managed dump.
                Thread.Sleep(2500);
                game.Block(1000);
                Assert.True(SpinWait.SpinUntil(() => watchdog.Hitches == 3, 5000), string.Join("\n", notices));
                Assert.True(SpinWait.SpinUntil(() => managedDumps.Count == 2, 5000), managedDumps.Count + " managed, " + nativeDumps.Count + " native");
                Assert.Single(nativeDumps);
                Assert.Contains(notices, n => n.StartsWith("[watchdog] hitch 3: a managed thread dump (2 of at most 3 per start)", StringComparison.Ordinal));
            }
            finally
            {
                watchdog.Stop();
                game.Stop();
                HitchGame7.Instance = null;
            }
        }

        /// <summary>A "game thread" that drains the context every 16 ms and can be blocked for a while.</summary>
        internal class GameLoop
        {
            private readonly ConcurrentQueue<(SendOrPostCallback, object)> queue = new ConcurrentQueue<(SendOrPostCallback, object)>();
            private volatile bool stopped;
            private volatile bool blocked;
            private int blockMs;

            /// <summary>Like <c>GameApplication.totalTime</c>: advanced every frame (the watchdog reads it by reflection).</summary>
            internal double totalTime;

            /// <summary>Like <c>GameApplication.Interrupted</c>: true in the background, where no frame runs.</summary>
            public bool Interrupted { get; set; }

            public bool Blocked { get { return blocked; } }

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
                        if (block > 0)
                        {
                            blocked = true;
                            Thread.Sleep(block);
                            blocked = false;
                        }
                        if (Interrupted)
                        {
                            Thread.Sleep(16);
                            continue;
                        }
                        Volatile.Write(ref totalTime, totalTime + 0.016);
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

        private sealed class HitchGame3 : GameLoop
        {
            public static HitchGame3 Instance { get; set; }
        }

        private sealed class HitchGame4 : GameLoop
        {
            public static HitchGame4 Instance { get; set; }
        }

        private sealed class HitchGame5 : GameLoop
        {
            public static HitchGame5 Instance { get; set; }
        }

        private sealed class HitchGame6 : GameLoop
        {
            public static HitchGame6 Instance { get; set; }
        }

        private sealed class HitchGame7 : GameLoop
        {
            public static HitchGame7 Instance { get; set; }
        }
    }

    /// <summary>
    /// The probe reads the frame clock every 100 ms: with the whole suite running in parallel its thread (and the fake game's)
    /// can be scheduled hundreds of ms late, so these tests run on their own.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class WatchdogHitchCollection
    {
        public const string Name = "WatchdogHitch";
    }
}
