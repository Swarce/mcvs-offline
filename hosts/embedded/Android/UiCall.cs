// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (compiled into the net8 tests too): AndroidJni.RunOnUiThread posts it to the main looper.
using System;
using System.Threading;

namespace McvsOffline.Embedded.Android
{
    /// <summary>
    /// One action for the UI thread that its caller waits for and withdraws when the wait ends first. A wait that simply timed
    /// out used to leave the action queued: the game's restart (<c>MainActivity.d.a(true)</c>) could still run after AdminHost had
    /// started the host's restart (two alarms, finish and finishAndRemoveTask), and a clipboard copy or a text share reported as
    /// failed could still happen later. <see cref="Run"/> is what gets posted; <see cref="Wait"/> waits and withdraws an action
    /// the UI thread has not started by then, which then never runs. One that has started runs to its end on the UI thread.
    /// </summary>
    internal sealed class UiCall
    {
        /// <summary>The action was not posted (it never runs).</summary>
        public const int NotPosted = -1;

        /// <summary>No wait was asked for (it runs when the UI thread gets to it), or it started and had not ended in time.</summary>
        public const int Pending = 0;

        /// <summary>It ran without an exception.</summary>
        public const int Done = 1;

        /// <summary>It ran and threw (reported through the failure callback).</summary>
        public const int Failed = 2;

        /// <summary>The UI thread had not started it when the wait ended: withdrawn, it never runs.</summary>
        public const int Withdrawn = 3;

        /// <summary>How much longer <see cref="Wait"/> waits for an action that started just before its wait ended.</summary>
        public const int StartedGraceMs = 100;

        private const int Queued = 0;
        private const int Started = 1;
        private const int Ended = 2;
        private const int Cancelled = 3;

        private readonly Action action;
        private readonly Action<Exception> failed;
        private readonly ManualResetEvent ended = new ManualResetEvent(false);
        private int state = Queued;
        private int outcome = Pending;

        public UiCall(Action action, Action<Exception> failed)
        {
            if (action == null) throw new ArgumentNullException("action");
            this.action = action;
            this.failed = failed;
        }

        /// <summary>
        /// Done, or Pending: the action ran, or it runs on the UI thread (started, or posted without a wait) and can no longer be
        /// withdrawn. False for NotPosted, Withdrawn and Failed.
        /// </summary>
        public static bool InEffect(int result)
        {
            return result == Done || result == Pending;
        }

        /// <summary>The UI thread's part (the posted action): does nothing once withdrawn; never throws.</summary>
        public void Run()
        {
            if (Interlocked.CompareExchange(ref state, Started, Queued) != Queued) return;
            int result = Done;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                result = Failed;
                try
                {
                    if (failed != null) failed(ex);
                }
                catch (Exception)
                {
                }
            }
            finally
            {
                Interlocked.Exchange(ref outcome, result);
                Interlocked.Exchange(ref state, Ended);
                ended.Set();
            }
        }

        /// <summary>
        /// The caller's part, once <see cref="Run"/> is posted. <paramref name="waitMs"/> &lt;= 0: <see cref="Pending"/> at once
        /// (nothing is withdrawn). Otherwise up to waitMs: <see cref="Done"/> or <see cref="Failed"/> once it ran;
        /// <see cref="Withdrawn"/> when the UI thread had not started it (it never will); <see cref="Pending"/> when it had started
        /// but not ended <see cref="StartedGraceMs"/> later (it ends on the UI thread; the outcome is unknown).
        /// </summary>
        public int Wait(int waitMs)
        {
            if (waitMs <= 0) return Pending;
            if (!ended.WaitOne(waitMs, false))
            {
                if (Interlocked.CompareExchange(ref state, Cancelled, Queued) == Queued) return Withdrawn;
                if (!ended.WaitOne(StartedGraceMs, false)) return Pending;
            }
            return Interlocked.CompareExchange(ref outcome, Pending, Pending);
        }
    }
}
