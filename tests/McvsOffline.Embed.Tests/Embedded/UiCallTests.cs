using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using McvsOffline.Embedded.Android;
using McvsOffline.Server.Services.Admin;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The UI-thread calls of the Android host (AndroidJni.RunOnUiThread): a call the UI thread has not started when its wait ends
    /// is withdrawn and never runs late (review of WP5: a timed-out game restart stayed queued and ran after the host's restart;
    /// a copy reported as failed could still land); one already started runs to its end. With a queue as the UI thread.
    /// </summary>
    public class UiCallTests
    {
        [Fact]
        public void ACallTheUiThreadRunsInTimeReportsItsOutcome()
        {
            using (var ui = new PumpedUiThread())
            {
                int runs = 0;
                var call = new UiCall(() => Interlocked.Increment(ref runs), null);
                ui.Post(call.Run);
                Assert.Equal(UiCall.Done, call.Wait(5000));
                Assert.Equal(1, runs);

                var failures = new List<Exception>();
                var failing = new UiCall(() => { throw new InvalidOperationException("probe: MainActivity.d is null"); }, ex => failures.Add(ex));
                ui.Post(failing.Run);
                Assert.Equal(UiCall.Failed, failing.Wait(5000));
                Assert.Equal("probe: MainActivity.d is null", Assert.Single(failures).Message);
            }
        }

        [Fact]
        public void ACallStillQueuedWhenTheWaitEndsIsWithdrawnAndNeverRuns()
        {
            var ui = new UiQueue();   // the UI thread is busy: nothing runs until Drain
            int runs = 0;
            var call = new UiCall(() => Interlocked.Increment(ref runs), null);
            ui.Post(call.Run);
            int started = Environment.TickCount;
            Assert.Equal(UiCall.Withdrawn, call.Wait(200));
            Assert.InRange(Environment.TickCount - started, 150, 2000);   // no grace for a call that never started
            ui.Drain();   // the UI thread frees up
            Assert.Equal(0, runs);
            Assert.False(UiCall.InEffect(UiCall.Withdrawn));

            // Without a wait nothing is withdrawn: it runs when the UI thread gets to it.
            var later = new UiCall(() => Interlocked.Increment(ref runs), null);
            ui.Post(later.Run);
            Assert.Equal(UiCall.Pending, later.Wait(0));
            Assert.True(UiCall.InEffect(UiCall.Pending));
            ui.Drain();
            Assert.Equal(1, runs);
        }

        [Fact]
        public void ACallThatStartedRunsToItsEndAndIsReportedPending()
        {
            using (var ui = new PumpedUiThread())
            using (var inside = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                int runs = 0;
                var call = new UiCall(() =>
                {
                    inside.Set();
                    release.Wait(10000);
                    Interlocked.Increment(ref runs);
                }, null);
                ui.Post(call.Run);
                Assert.True(inside.Wait(5000));
                Assert.Equal(UiCall.Pending, call.Wait(100));   // started: not withdrawn, outcome unknown
                release.Set();
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref runs) == 1, 5000));
            }
        }

        /// <summary>The UI thread takes the call around the moment the wait ends: whatever the timing, a withdrawn call never runs and
        /// no call runs twice.</summary>
        [Fact]
        public void WithdrawingRacesTheUiThreadSafely()
        {
            var random = new Random(1234);
            for (int i = 0; i < 300; i++)
            {
                var ui = new UiQueue();
                int runs = 0;
                var call = new UiCall(() => Interlocked.Increment(ref runs), null);
                ui.Post(call.Run);
                int delay = random.Next(0, 6);
                var drainer = new Thread(() =>
                {
                    Thread.Sleep(delay);
                    ui.Drain();
                });
                drainer.Start();
                int result = call.Wait(3);
                drainer.Join();
                ui.Drain();   // late: nothing left to run
                if (result == UiCall.Withdrawn) Assert.Equal(0, runs);
                else Assert.Equal(1, runs);
                Assert.Contains(result, new[] { UiCall.Withdrawn, UiCall.Done, UiCall.Pending });
            }
        }

        [Fact]
        public void TheShareAndClipboardResultsFollowTheOutcome()
        {
            AdminShareResult done = AndroidShare.TextShareResult(UiCall.Done);
            Assert.True(done.Ok);
            Assert.Equal("text", done.Method);

            // Started but not ended within the wait: the sheet may still open (AdminHost keeps the share lease).
            AdminShareResult pending = AndroidShare.TextShareResult(UiCall.Pending);
            Assert.False(pending.Ok);
            Assert.Equal("text", pending.Method);
            Assert.Contains("may still appear", pending.Message);

            AdminShareResult withdrawn = AndroidShare.TextShareResult(UiCall.Withdrawn);
            Assert.False(withdrawn.Ok);
            Assert.Equal("none", withdrawn.Method);
            Assert.Contains("Try again", withdrawn.Message);
            Assert.Equal("none", AndroidShare.TextShareResult(UiCall.Failed).Method);
            Assert.Equal("none", AndroidShare.TextShareResult(UiCall.NotPosted).Method);

            // A copy is reported only once it ran (an unfinished one is unknown: the page asks the player to copy by hand).
            Assert.True(AndroidShare.ClipboardResult(UiCall.Done));
            Assert.False(AndroidShare.ClipboardResult(UiCall.Pending));
            Assert.False(AndroidShare.ClipboardResult(UiCall.Withdrawn));
            Assert.False(AndroidShare.ClipboardResult(UiCall.Failed));

            // IAdminHost: members other than ShareFile, WaitForGameLoop and Exit return within a second.
            Assert.True(AndroidShare.QuickUiWaitMs + UiCall.StartedGraceMs <= 900);
        }
    }

    /// <summary>A UI thread's message queue that runs only when <see cref="Drain"/> is called.</summary>
    internal sealed class UiQueue
    {
        private readonly ConcurrentQueue<Action> items = new ConcurrentQueue<Action>();

        public void Post(Action action)
        {
            items.Enqueue(action);
        }

        public void Drain()
        {
            Action action;
            while (items.TryDequeue(out action)) action();
        }
    }

    /// <summary>A UI thread that runs what is posted until disposed.</summary>
    internal sealed class PumpedUiThread : IDisposable
    {
        private readonly UiQueue queue = new UiQueue();
        private readonly Thread thread;
        private volatile bool stop;

        public PumpedUiThread()
        {
            thread = new Thread(() =>
            {
                while (!stop)
                {
                    queue.Drain();
                    Thread.Sleep(2);
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        public void Post(Action action)
        {
            queue.Post(action);
        }

        public void Dispose()
        {
            stop = true;
            thread.Join(5000);
        }
    }
}
