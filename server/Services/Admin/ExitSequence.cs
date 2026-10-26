using System;
using System.Collections.Generic;
using System.Threading;
using McvsOffline.Server.Core;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>The waits of <see cref="ExitSequence"/> (design §8.8); tests shorten them.</summary>
    public sealed class ExitTimings
    {
        public ExitTimings()
        {
            ResponseDelayMs = 300;
            GameLoopTimeoutMs = 8000;
            SaveTimeoutMs = 15000;
            SaveQuietMs = 1000;
            FlushTimeoutMs = 2000;
        }

        /// <summary>Before the first step, so the 202 answer is on the wire before the browser closes.</summary>
        public int ResponseDelayMs { get; set; }

        public int GameLoopTimeoutMs { get; set; }

        public int SaveTimeoutMs { get; set; }

        /// <summary>After the save: no further save for this long (the TS's dump POST and merge are done).</summary>
        public int SaveQuietMs { get; set; }

        public int FlushTimeoutMs { get; set; }
    }

    /// <summary>What to run: "save-now" (then reopen the page on Screen) or "restart" (then exit with Reason).</summary>
    internal sealed class ExitRequest
    {
        public const string SaveNow = "save-now";
        public const string Restart = "restart";

        public string Kind { get; set; }

        /// <summary>save-now: the screen to reopen ("saves"...), or null.</summary>
        public string Screen { get; set; }

        /// <summary>restart: "settings", "import" or "user".</summary>
        public string Reason { get; set; }

        /// <summary>restart: go on even when the game did not save (explicit consent on the page).</summary>
        public bool RestartWithoutSave { get; set; }
    }

    /// <summary>
    /// Save-then-exit (design §4.5, §8.8). The game loop is paused while the page is open and the game never saves when it
    /// is killed, so both "Save the game now" and "Restart" first close the browser and let the game save:
    /// <list type="number">
    /// <item>toast; 2. close the browser; 3. wait for the game loop (≤ 8 s); 4. ask the game to save; 5. wait for a save
    /// of the current account that began after the request, then 1 s without another (≤ 15 s);</item>
    /// <item>save-now: 6. reopen the page with a fresh ticket on its screen (<c>&amp;r=nosave</c> without a save);</item>
    /// <item>restart: 7. without a save and without consent: toast, <c>last = cancelled</c>, stop; 8. take the account's
    /// profile lock (no new save can start), flush the logs, <c>IAdminHost.Exit(reason, relaunch: true)</c>; when that
    /// fails: release the lock, toast, <c>last = failed</c>.</item>
    /// </list>
    /// One sequence at a time. Every step is logged as <c>admin: exit &lt;kind&gt;: &lt;step&gt;</c>. The host primitives
    /// never throw; everything else is caught, so the state always returns to idle unless the process is ending.
    /// </summary>
    internal sealed class ExitSequence
    {
        public const string Idle = "idle";
        public const string Saving = "saving";
        public const string Restarting = "restarting";

        public const string ToastSaving = "MCVS Offline: saving…";
        public const string ToastRestarting = "MCVS Offline: saving, then restarting the game…";
        public const string ToastNotSaved = "MCVS Offline: the game did not save, so it was not restarted.";
        public const string ToastFailed = "MCVS Offline: restart failed. Close the game yourself.";

        private readonly object gate = new object();
        private readonly Func<IAdminHost> host;
        private readonly GameSaveTracker saves;
        private readonly Func<CurrentPlayer> player;
        private readonly Func<string, IDisposable> lockAccount;
        private readonly Func<int, bool> flush;
        private readonly Func<string, bool, bool> reopen;
        private readonly IClock clock;
        private readonly Log log;
        private volatile ExitTimings timings;
        private string state = Idle;
        private CurrentPlayer current;
        private Dictionary<string, object> last;

        /// <param name="host">The host at call time (may return null).</param>
        /// <param name="player">Resolves the current player when a sequence begins.</param>
        /// <param name="lockAccount">Takes an account's profile lock (LocalProfileStore.Locks.Acquire); may return null.</param>
        /// <param name="flush">Flushes the server logs within the given milliseconds.</param>
        /// <param name="reopen">(screen, saved) → opens the page again with a fresh ticket.</param>
        public ExitSequence(Func<IAdminHost> host, GameSaveTracker saves, Func<CurrentPlayer> player, Func<string, IDisposable> lockAccount,
                            Func<int, bool> flush, Func<string, bool, bool> reopen, IClock clock, Log log, ExitTimings timings)
        {
            this.host = host;
            this.saves = saves;
            this.player = player;
            this.lockAccount = lockAccount;
            this.flush = flush;
            this.reopen = reopen;
            this.clock = clock ?? SystemClock.Instance;
            this.log = log ?? Log.Null;
            this.timings = timings ?? new ExitTimings();
        }

        public string State
        {
            get { lock (gate) return state; }
        }

        /// <summary>The waits (tests replace them; a sequence reads them as it goes).</summary>
        public ExitTimings Timings
        {
            get { return timings; }
            set { timings = value ?? new ExitTimings(); }
        }

        /// <summary>The player the running sequence is for (null when idle or unknown).</summary>
        public CurrentPlayer Player
        {
            get { lock (gate) return current; }
        }

        /// <summary>The last sequence's outcome {kind, result, at, message}, or null.</summary>
        public Dictionary<string, object> Last
        {
            get { lock (gate) return last == null ? null : new Dictionary<string, object>(last, StringComparer.Ordinal); }
        }

        /// <summary>
        /// Marks a sequence as running (state "saving" or "restarting") and resolves its player; false with the 409 code
        /// ("restarting" or "busy") when one already runs.
        /// </summary>
        public bool TryBegin(ExitRequest request, out string refusal)
        {
            lock (gate)
            {
                if (state != Idle)
                {
                    refusal = state == Restarting ? "restarting" : "busy";
                    return false;
                }
                state = request.Kind == ExitRequest.Restart ? Restarting : Saving;
            }
            CurrentPlayer who = null;
            try
            {
                who = player == null ? null : player();
            }
            catch (Exception ex)
            {
                log.Warn("admin: exit " + request.Kind + ": the current player could not be resolved", ex);
            }
            lock (gate) current = who;
            refusal = null;
            return true;
        }

        /// <summary>Runs a begun sequence on a background thread after <see cref="ExitTimings.ResponseDelayMs"/>.</summary>
        public void Start(ExitRequest request)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    if (timings.ResponseDelayMs > 0) Thread.Sleep(timings.ResponseDelayMs);
                }
                catch (Exception)
                {
                }
                Run(request);
            });
            thread.IsBackground = true;
            thread.Name = "mcvs-admin-exit";
            thread.Start();
        }

        /// <summary>The sequence itself (synchronous; <see cref="TryBegin"/> first). Never throws.</summary>
        public void Run(ExitRequest request)
        {
            bool restart = request.Kind == ExitRequest.Restart;
            string kind = restart ? ExitRequest.Restart : ExitRequest.SaveNow;
            IDisposable accountLock = null;
            bool exiting = false;
            try
            {
                IAdminHost h = host == null ? null : host();
                if (h == null)
                {
                    Finish(kind, "failed", "The game cannot do this here.");
                    return;
                }
                CurrentPlayer who = Player;
                string account = who == null ? null : who.AccountId;

                Step(kind, "toast");
                h.ShowToast(restart ? ToastRestarting : ToastSaving);
                Step(kind, "close the browser");
                bool closed = h.CloseBrowser();
                Step(kind, "wait for the game loop" + (closed ? "" : " (the browser was not closed)"));
                bool loop = h.WaitForGameLoop(timings.GameLoopTimeoutMs);
                if (!loop) log.Warn("admin: exit " + kind + ": the game loop did not run within " + timings.GameLoopTimeoutMs + " ms");

                DateTime t0 = clock.UtcNow;
                Step(kind, "request the game's save" + (account == null ? " (current player unknown)" : " of " + account));
                bool requested = h.RequestGameSave();
                bool saved = false;
                if (!requested) log.Warn("admin: exit " + kind + ": the game could not be asked to save");
                else if (account != null && saves != null)
                {
                    saved = saves.WaitForSave(account, t0, timings.SaveTimeoutMs, timings.SaveQuietMs);
                    Step(kind, saved ? "the game saved" : "no save within " + timings.SaveTimeoutMs + " ms");
                }

                if (!restart)
                {
                    Step(kind, "reopen the page" + (saved ? "" : " (not saved)"));
                    bool reopened = reopen != null && reopen(request.Screen, saved);
                    if (!reopened) log.Warn("admin: exit " + kind + ": the page could not be reopened");
                    Finish(kind, saved ? "saved" : "nosave",
                           saved ? "The game saved." : "The game did not save.");
                    return;
                }

                if (!saved && !request.RestartWithoutSave)
                {
                    Step(kind, "cancelled: no save");
                    h.ShowToast(ToastNotSaved);
                    Finish(kind, "cancelled", "The game did not save, so it was not restarted.");
                    return;
                }

                if (account != null && lockAccount != null)
                {
                    Step(kind, "lock the profile of " + account);
                    accountLock = lockAccount(account);
                }
                Step(kind, "flush the logs");
                if (flush != null) flush(timings.FlushTimeoutMs);
                Step(kind, "exit (" + (request.Reason ?? "user") + ")");
                if (h.Exit(request.Reason ?? "user", true))
                {
                    exiting = true;
                    // The lock stays held: no save may start while the process ends.
                    Finish(kind, "exiting", saved ? "The game saved and is restarting." : "The game is restarting without a save.");
                    return;
                }
                Step(kind, "the exit failed");
                h.ShowToast(ToastFailed);
                Finish(kind, "failed", "Restart failed: close the game yourself.");
            }
            catch (Exception ex)
            {
                log.Error("admin: exit " + kind + " failed", ex);
                Finish(kind, "failed", "Something went wrong: " + ex.Message);
            }
            finally
            {
                if (!exiting)
                {
                    if (accountLock != null)
                    {
                        try { accountLock.Dispose(); } catch (Exception) { }
                    }
                    lock (gate)
                    {
                        state = Idle;
                        current = null;
                    }
                }
            }
        }

        private void Step(string kind, string step)
        {
            log.Info("admin: exit " + kind + ": " + step);
        }

        private void Finish(string kind, string result, string message)
        {
            var entry = new Dictionary<string, object>(StringComparer.Ordinal);
            entry["kind"] = kind;
            entry["result"] = result;
            entry["at"] = TimeFormat.Iso8601Millis(clock.UtcNow);
            entry["message"] = message;
            lock (gate) last = entry;
            log.Info("admin: exit " + kind + ": " + result + " (" + message + ")");
        }
    }
}
