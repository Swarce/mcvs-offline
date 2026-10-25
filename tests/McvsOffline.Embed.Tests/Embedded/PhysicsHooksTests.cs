using System;
using System.Diagnostics;
using System.Threading;
using McvsOffline;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>GameHooks' side of the 'physics' patch: which Worlds get multithreaded Havok, and the step gate.</summary>
    [Collection("GameHooks")]
    public class PhysicsHooksTests : IDisposable
    {
        public void Dispose()
        {
            GameHooks.ConfigurePhysics(false, true, true, GameHooks.DefaultStepGateTimeoutMs);
        }

        [Fact]
        public void OnlyTheClientKeepsMultithreadedHavokByDefault()
        {
            GameHooks.ConfigurePhysics(false, true, true, GameHooks.DefaultStepGateTimeoutMs);
            Assert.False(GameHooks.PhysicsMultithreading(headless: true, requested: true));
            Assert.True(GameHooks.PhysicsMultithreading(headless: false, requested: true));
            Assert.False(GameHooks.PhysicsMultithreading(headless: false, requested: false));   // the game's own "no" always wins
            Assert.False(GameHooks.PhysicsMultithreading(headless: true, requested: false));

            GameHooks.ConfigurePhysics(true, false, true, GameHooks.DefaultStepGateTimeoutMs);   // A/B: everything reverted / inverted
            Assert.True(GameHooks.PhysicsMultithreading(headless: true, requested: true));
            Assert.False(GameHooks.PhysicsMultithreading(headless: false, requested: true));
            Assert.Contains("Havok worlds built", GameHooks.DescribePhysics());
        }

        [Fact]
        public void TheGateLetsOneWorldStepAtATime()
        {
            GameHooks.ConfigurePhysics(false, true, true, 5000);
            int inside = 0, maxInside = 0, rounds = 0, notTaken = 0;
            var threads = new Thread[3];
            for (int t = 0; t < threads.Length; t++)
            {
                threads[t] = new Thread(() =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        if (!GameHooks.EnterPhysicsStep())
                        {
                            Interlocked.Increment(ref notTaken);
                            continue;
                        }
                        int now = Interlocked.Increment(ref inside);
                        if (now > maxInside) maxInside = now;
                        Thread.SpinWait(2000);
                        Interlocked.Decrement(ref inside);
                        Interlocked.Increment(ref rounds);
                        GameHooks.ExitPhysicsStep();
                    }
                });
                threads[t].Start();
            }
            foreach (Thread t in threads) Assert.True(t.Join(20000));
            Assert.Equal(0, notTaken);
            Assert.Equal(150, rounds);
            Assert.Equal(1, maxInside);
        }

        [Fact]
        public void AStuckStepNeverHoldsTheOtherWorldForLongerThanTheTimeout()
        {
            GameHooks.ConfigurePhysics(false, true, true, 300);
            var holding = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            bool holderTook = false;
            var holder = new Thread(() =>
            {
                holderTook = GameHooks.EnterPhysicsStep();
                holding.Set();
                release.Wait(10000);
                GameHooks.ExitPhysicsStep();
            });
            holder.Start();
            Assert.True(holding.Wait(5000));
            Assert.True(holderTook);
            try
            {
                int timeouts = GameHooks.PhysicsTimeouts;
                var watch = Stopwatch.StartNew();
                Assert.False(GameHooks.EnterPhysicsStep());   // not taken: the caller steps without the gate (and must not exit)
                Assert.InRange(watch.ElapsedMilliseconds, 250, 3000);
                Assert.Equal(timeouts + 1, GameHooks.PhysicsTimeouts);
                GameHooks.ExitPhysicsStep();                    // a stray exit from a thread that does not own the gate is ignored
            }
            finally
            {
                release.Set();
                holder.Join(5000);
            }
            // Released: the next step takes it at once, and a waiter is woken when the holder leaves.
            Assert.True(GameHooks.EnterPhysicsStep());
            var waiterTook = false;
            var waiter = new Thread(() => { waiterTook = GameHooks.EnterPhysicsStep(); if (waiterTook) GameHooks.ExitPhysicsStep(); });
            waiter.Start();
            Thread.Sleep(50);
            GameHooks.ExitPhysicsStep();
            Assert.True(waiter.Join(5000));
            Assert.True(waiterTook);
        }

        [Fact]
        public void TheGateCanBeTurnedOff()
        {
            GameHooks.ConfigurePhysics(false, true, false, GameHooks.DefaultStepGateTimeoutMs);
            int steps = GameHooks.PhysicsSteps;
            Assert.False(GameHooks.EnterPhysicsStep());
            Assert.Equal(steps, GameHooks.PhysicsSteps);
            GameHooks.PhysicsMultithreading(headless: false, requested: true);
            Assert.EndsWith("step gate off", GameHooks.DescribePhysics());
        }
    }
}
