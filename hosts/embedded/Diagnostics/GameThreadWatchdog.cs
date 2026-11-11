// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (reflection on Gameloft.Engine, Linux /proc): compiled into the embedded DLL and the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;

namespace McvsOffline.Embedded.Diagnostics
{
    /// <summary>
    /// Tells "the game thread is blocked" from "it is suspended" and says where it sits (device test 1, RUN 1: after the
    /// LocalAnubis datacenter request the next game-thread continuation never ran, with no Fatal and no ANR).
    /// <para>
    /// Once a second it posts one tiny work item to <c>GameApplication.Instance.Context</c> (the GameSynchronizationContext
    /// the game thread drains every frame in UpdateWorlds; at most one outstanding, so a blocked thread never accumulates
    /// ours) and reads the private <c>totalTime</c> (advanced every frame) and <c>suspendUpdateInBackground</c>. When no work
    /// item ran for 5, 15, 60 and 180 s it logs a snapshot: the lag, how long the frame clock has not moved, the server's last
    /// request, the game thread's kernel state, wchan and syscall (futex = a lock, ppoll/connect/recvfrom = network on the game
    /// thread, R = busy), the busiest threads since the previous snapshot, every thread in state D, and at 15 s the Java
    /// stacks of the game thread and the UI thread (a JNI call stuck in Java shows up there). With the 15 s and the 180 s
    /// snapshot it starts the optional thread dump (Android: <see cref="ThreadDump"/>, managed and native stacks) on a thread of
    /// its own, so a dump that blocks never stops the watchdog.
    /// </para>
    /// Reflection only (no reference to Gameloft.Engine); everything that runs on the game thread is a few field writes and one
    /// gettid() in a try/catch (a heartbeat that ever runs on another thread is reported once: the "it would have run in a
    /// pumping wait" reasoning of device test 2 relies on the beat running on the game thread). Off when GameApplication cannot
    /// be found (tests, other builds).
    /// <para>
    /// A lease (<see cref="PauseFor"/>, design §8.5) tells it that the game loop is paused on purpose: while the settings page
    /// is shown in the in-game browser the main activity is paused and the game thread runs no frame, which is not a stall.
    /// </para>
    /// <para>
    /// Hitches (build 30, <see cref="HitchMs"/>; the owner's S22 log had 1 to 5 s game-thread blocks on the results screen and at
    /// the match start, all under the first 5 s report): measured on the frame clock, not on the beat (a beat posted once a
    /// second saw a 0.9 s block only when it happened to be posted in its first 0.2 s). The loop then wakes every
    /// <see cref="HitchPollMs"/> ms and reads <c>totalTime</c>; when it has not moved for <see cref="HitchMs"/> (and the game is
    /// neither suspended nor <c>Interrupted</c>, i.e. in the background), one "HITCH:" notice with the same snapshot as a stall
    /// (no Java stacks), taken then, while the game thread is still blocked, and for the first <see cref="HitchDumps"/> the
    /// <see cref="HitchDump"/>; when the clock moves again, "hitch over" with about how long it stood (within one poll).
    /// Until <see cref="HitchArmed"/> says so (the host: the first match), hitches are only counted: the loading blocks of a
    /// start would otherwise use up the snapshots and dumps before the match screens the probe is there for.
    /// </para>
    /// </summary>
    public sealed class GameThreadWatchdog
    {
        private static readonly int[] DefaultReportAtMs = { 5000, 15000, 60000, 180000 };

        private readonly Func<string> serverState;
        private readonly Action<string> report;
        private readonly Func<string, string> javaStacks;
        private readonly Action<int, string> threadDump;
        private readonly int intervalMs;
        private readonly int[] ReportAtMs;
        private int lastBeat = Environment.TickCount;
        private int beatOutstanding;
        private volatile int gameTid;
        private volatile int otherBeatTid;
        private bool otherBeatReported;
        private volatile string gameThreadName;
        private int stallReports;
        private int beats;
        private volatile bool stopped;
        private IThreadSnapshot threads;
        private PropertyInfo instanceProperty;
        private PropertyInfo contextProperty;
        private FieldInfo suspendField;
        private FieldInfo totalTimeField;
        private SendOrPostCallback beatCallback;
        private readonly object pauseGate = new object();
        private int pausedUntil;               // Environment.TickCount at which the lease ends (under pauseGate)
        private bool pauseActive;              // a lease was granted and not ended (under pauseGate)
        private string pauseReason;            // under pauseGate
        private bool pauseReported;            // watchdog thread only
        private PropertyInfo interruptedProperty;
        private int lastPost;                  // watchdog thread only
        private bool hitchOpen;                // watchdog thread only
        private int hitchSince;                // watchdog thread only: when the open hitch's frame clock last moved (seen by a poll)
        private bool hitchReported;            // watchdog thread only: the open hitch came after arming
        private bool armed;                    // watchdog thread only
        private int hitches;
        private int armedHitches;
        private int longestHitchMs;

        /// <summary>
        /// The loop's period while <see cref="HitchMs"/> is on (at most the beat interval): a block of <see cref="HitchMs"/> plus
        /// one poll is always seen while it lasts (the clock is read once per poll, so the stall it sees starts up to one poll late).
        /// </summary>
        public const int HitchPollMs = 100;

        /// <summary>What EmbeddedHost sets <see cref="HitchMs"/> to: a frame clock that stood this long is a hitch.</summary>
        public const int DefaultHitchMs = 700;

        /// <summary>
        /// A frame clock (<c>totalTime</c>) that has not moved for at least this long is a hitch (0, the default: no hitch notices).
        /// Set before <see cref="Start"/>.
        /// </summary>
        public int HitchMs { get; set; }

        /// <summary>At most this many "HITCH:" snapshots per process after arming (later hitches are counted and get the "hitch over" line).</summary>
        public int HitchSnapshots { get; set; } = 8;

        /// <summary>The first this many hitches after arming also start <see cref="HitchDump"/> (or the thread dump when that is null).</summary>
        public int HitchDumps { get; set; } = 2;

        /// <summary>
        /// Polled by the watchdog thread until it returns true (null: armed from the start). Before that a hitch is only counted
        /// (no notice, snapshot or dump); then one "hitch probe armed" line with the count. Must not block. Set before <see cref="Start"/>.
        /// </summary>
        public Func<bool> HitchArmed { get; set; }

        /// <summary>
        /// (game thread tid, its comm): the dump a hitch starts, on a thread of its own (null: the stall's thread dump). The host
        /// passes a lighter one than the stall's: no Mono managed dump (it suspends every managed thread and so lengthens the
        /// player's hitch) and no file, logged at INFO (a hitch is not a problem). Set before <see cref="Start"/>.
        /// </summary>
        public Action<int, string> HitchDump { get; set; }

        /// <summary>Hitches seen so far (before and after arming).</summary>
        public int Hitches { get { return hitches; } }

        /// <param name="serverState">e.g. the boot trace's Describe().</param>
        /// <param name="report">Where snapshots go (logcat + bootstrap.log).</param>
        /// <param name="javaStacks">Java stacks of the threads whose name matches (Android); may be null.</param>
        /// <param name="intervalMs">Heartbeat period (tests use a short one).</param>
        /// <param name="reportAtMs">Stall durations that produce a snapshot (default 5, 15, 60, 180 s; Java stacks at the second).</param>
        /// <param name="threadDump">(game thread tid, its comm): dumps the stacks; called on a new thread after the second and
        /// the fourth snapshot. May be null.</param>
        public GameThreadWatchdog(Func<string> serverState, Action<string> report, Func<string, string> javaStacks, int intervalMs = 1000,
                                  int[] reportAtMs = null, Action<int, string> threadDump = null)
        {
            this.threadDump = threadDump;
            this.serverState = serverState;
            this.report = report ?? (s => { });
            this.javaStacks = javaStacks;
            this.intervalMs = intervalMs < 20 ? 20 : intervalMs;
            ReportAtMs = reportAtMs != null && reportAtMs.Length > 0 ? reportAtMs : DefaultReportAtMs;
        }

        /// <summary>
        /// Where the stall snapshot reads the threads of this process (docs/design/pc-port.md §5.2): null (the default) is
        /// <see cref="ProcThreadSnapshot"/> over /proc (Android, the tests); the PC host passes its <c>WindowsThreads</c>. Set
        /// before <see cref="Start"/>.
        /// </summary>
        public IThreadSnapshot Threads
        {
            get { return threads; }
            set { threads = value; }
        }

        /// <summary>
        /// Routine lines that are not problems (the game thread's tid, a lease's "paused: ..." and "resumed"): EmbeddedHost logs them
        /// at INFO, because WARN lines are the settings page's "recent problems" and every page open would add two. Null: they go
        /// to the report callback with the rest. Set before <see cref="Start"/>.
        /// </summary>
        public Action<string> Notice { get; set; }

        public int GameTid { get { return gameTid; } }

        public int Beats { get { return beats; } }

        public void Start()
        {
            if (threads == null) threads = new ProcThreadSnapshot(threadDump != null ? (Func<Func<string>, string>)(read => ThreadDump.WithDumpable(read)) : null);
            beatCallback = Beat;
            var thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "mcvs-watchdog";
            thread.Start();
        }

        public void Stop()
        {
            stopped = true;
        }

        /// <summary>A short status for crash reports.</summary>
        public string Describe()
        {
            string reason;
            string paused = IsPaused(out reason) ? ", paused: " + reason : "";
            int early = hitches - armedHitches;
            string hitchText = HitchMs > 0
                ? ", " + hitches.ToString(CultureInfo.InvariantCulture) + " hitches of " + HitchMs.ToString(CultureInfo.InvariantCulture) +
                  " ms or more (" + (early > 0 ? early.ToString(CultureInfo.InvariantCulture) + " before the probe was armed; " : "") +
                  "longest " + longestHitchMs.ToString(CultureInfo.InvariantCulture) + " ms)"
                : "";
            return "game thread tid " + gameTid.ToString(CultureInfo.InvariantCulture) + " ('" + (gameThreadName ?? "?") +
                   "'), last work item " + (Environment.TickCount - lastBeat).ToString(CultureInfo.InvariantCulture) + " ms ago, " +
                   beats.ToString(CultureInfo.InvariantCulture) + " beats" + hitchText + paused;
        }

        /// <summary>Treat the game thread as paused (not stalled) until now + ms; the latest call wins; 0 ends it. Thread-safe.</summary>
        public void PauseFor(int ms, string reason)
        {
            lock (pauseGate)
            {
                if (ms <= 0)
                {
                    pauseActive = false;
                    return;
                }
                pausedUntil = Environment.TickCount + ms;
                pauseActive = true;
                pauseReason = string.IsNullOrEmpty(reason) ? "paused" : reason;
            }
        }

        /// <summary>True while a lease of <see cref="PauseFor"/> runs.</summary>
        public bool IsPaused(out string reason)
        {
            lock (pauseGate)
            {
                reason = pauseReason;
                if (pauseActive && pausedUntil - Environment.TickCount <= 0) pauseActive = false;   // expired (wrap-safe)
                return pauseActive;
            }
        }

        /// <summary>Resolves the Gameloft.Engine members; false (and one line) when the game is not there.</summary>
        public bool Bind(Type gameApplication)
        {
            try
            {
                if (gameApplication == null) return false;
                instanceProperty = gameApplication.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                contextProperty = gameApplication.GetProperty("Context", BindingFlags.Public | BindingFlags.Instance);
                suspendField = gameApplication.GetField("suspendUpdateInBackground", BindingFlags.NonPublic | BindingFlags.Instance);
                totalTimeField = gameApplication.GetField("totalTime", BindingFlags.NonPublic | BindingFlags.Instance);
                interruptedProperty = gameApplication.GetProperty("Interrupted", BindingFlags.Public | BindingFlags.Instance);
                return instanceProperty != null && contextProperty != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Run()
        {
            try
            {
                if (instanceProperty == null)
                {
                    Type app = LoadedTypes.EngineType("Gameloft.Engine.GameApplication");
                    if (!Bind(app))
                    {
                        report("[watchdog] Gameloft.Engine.GameApplication not found: game-thread watchdog off");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                report("[watchdog] reflection failed, watchdog off: " + ex.Message);
                return;
            }

            double lastTotal = -1;
            int lastTotalChange = Environment.TickCount;
            int hitchFrom = lastTotalChange;   // the hitch probe's: the clock last moved, or the game was last paused or in the background
            bool announced = false;
            bool hitchProbe = HitchMs > 0 && totalTimeField != null;
            if (HitchMs > 0 && !hitchProbe) report("[watchdog] GameApplication.totalTime not found: no hitch probe");
            int tick = hitchProbe ? Math.Min(intervalMs, HitchPollMs) : intervalMs;
            lastPost = Environment.TickCount - intervalMs;
            while (!stopped)
            {
                try
                {
                    Thread.Sleep(tick);
                    object app = instanceProperty.GetValue(null, null);
                    if (app == null)
                    {
                        lastBeat = Environment.TickCount;   // not created yet: nothing to watch
                        continue;
                    }
                    if (Environment.TickCount - lastPost >= intervalMs - tick / 2 && Interlocked.CompareExchange(ref beatOutstanding, 1, 0) == 0)
                    {
                        var context = contextProperty.GetValue(app, null) as SynchronizationContext;
                        lastPost = Environment.TickCount;
                        if (context != null) context.Post(beatCallback, null);
                        else beatOutstanding = 0;
                    }
                    if (!announced && gameTid != 0)
                    {
                        announced = true;
                        Note("[watchdog] game thread is tid " + gameTid.ToString(CultureInfo.InvariantCulture) + " '" + gameThreadName + "'");
                    }
                    if (!otherBeatReported && otherBeatTid != 0)
                    {
                        otherBeatReported = true;
                        report("[watchdog] a heartbeat ran on tid " + otherBeatTid.ToString(CultureInfo.InvariantCulture) + ", not on the game thread " +
                               gameTid.ToString(CultureInfo.InvariantCulture) + ": another thread drains GameApplication.Context too");
                    }

                    // The settings page (§8.5): the game loop is paused on purpose; behave as in the background.
                    string pauseText;
                    if (IsPaused(out pauseText))
                    {
                        if (!pauseReported)
                        {
                            pauseReported = true;
                            Note("[watchdog] paused: " + pauseText);
                        }
                        lastBeat = Environment.TickCount;
                        hitchFrom = lastBeat;   // the frame clock stands while paused: not a hitch, then or after
                        stallReports = 0;
                        hitchOpen = false;
                        continue;
                    }
                    if (pauseReported)
                    {
                        pauseReported = false;
                        lastBeat = Environment.TickCount;   // the lease ended: the next stall is measured from here
                        Note("[watchdog] resumed");
                    }

                    double total = totalTimeField == null ? -2 : (double)totalTimeField.GetValue(app);
                    int now = Environment.TickCount;
                    bool moved = total != lastTotal;
                    if (moved)
                    {
                        lastTotal = total;
                        lastTotalChange = now;
                        hitchFrom = now;
                    }
                    bool suspended = suspendField != null && (bool)suspendField.GetValue(app);
                    if (hitchProbe)
                    {
                        // Android: in the background the native loop stops calling UpdateWorlds and only Interrupted says so.
                        bool background = suspended || (interruptedProperty != null && (bool)interruptedProperty.GetValue(app, null));
                        if (background) hitchFrom = now;
                        CheckHitch(now, moved, background, hitchFrom);
                    }
                    int lag = Environment.TickCount - lastBeat;
                    if (lag < ReportAtMs[0])
                    {
                        if (stallReports > 0) report("[watchdog] the game thread runs again (after " + stallReports.ToString(CultureInfo.InvariantCulture) + " stall reports)");
                        stallReports = 0;
                        continue;
                    }
                    if (suspended)
                    {
                        lastBeat = Environment.TickCount;   // in the background the game does not update: not a stall
                        continue;
                    }
                    if (stallReports >= ReportAtMs.Length || lag < ReportAtMs[stallReports]) continue;
                    stallReports++;
                    report(Snapshot(lag, Environment.TickCount - lastTotalChange, suspended, stallReports == 2));
                    if (stallReports == 2 || stallReports == 4) StartThreadDump();
                }
                catch (Exception ex)
                {
                    report("[watchdog] " + ex.GetType().Name + ": " + ex.Message);
                    try { Thread.Sleep(10000); } catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// The hitch probe (watchdog thread, every poll): a frame clock that has stood for <see cref="HitchMs"/> opens a hitch (the
        /// snapshot now, while the game thread is still blocked, and the dump for the first ones); the clock moving again (or
        /// the game going to the background) closes it.
        /// </summary>
        private void CheckHitch(int now, bool moved, bool background, int clockMovedAt)
        {
            if (!armed) Arm();
            if (hitchOpen)
            {
                if (moved || background) CloseHitch(now, background);
                return;
            }
            int standing = now - clockMovedAt;
            if (moved || background || standing < HitchMs) return;
            hitchOpen = true;
            hitchSince = clockMovedAt;
            Interlocked.Increment(ref hitches);
            hitchReported = armed;
            if (!armed) return;
            int n = Interlocked.Increment(ref armedHitches);
            if (n <= HitchSnapshots)
                Note(Snapshot(standing, standing, false, false, "HITCH: the game thread has not finished a frame for "));
            if (n <= HitchDumps) StartThreadDump(HitchDump ?? threadDump);
        }

        /// <summary>Once <see cref="HitchArmed"/> is true: one line with the hitches counted before.</summary>
        private void Arm()
        {
            Func<bool> gate = HitchArmed;
            if (gate != null)
            {
                try
                {
                    if (!gate()) return;
                }
                catch (Exception)
                {
                    return;
                }
            }
            armed = true;
            if (gate == null) return;
            int early = hitches;
            Note("[watchdog] hitch probe armed: " + early.ToString(CultureInfo.InvariantCulture) + (early == 1 ? " hitch" : " hitches") + " of " +
                 HitchMs.ToString(CultureInfo.InvariantCulture) + " ms or more before (longest " + longestHitchMs.ToString(CultureInfo.InvariantCulture) +
                 " ms); from now on each one gets a snapshot");
        }

        /// <summary>The open hitch ended: "hitch over" with about how long the frame clock stood (when it got a notice).</summary>
        private void CloseHitch(int now, bool background)
        {
            hitchOpen = false;
            int stood = now - hitchSince;
            if (stood > longestHitchMs) longestHitchMs = stood;
            if (!hitchReported) return;
            Note("[watchdog] hitch over: " + (background ? "the game went to the background after " : "the game thread finished a frame again after about ") +
                 stood.ToString(CultureInfo.InvariantCulture) + " ms (hitch " + armedHitches.ToString(CultureInfo.InvariantCulture) + " of this start)");
        }

        private void Note(string line)
        {
            Action<string> notice = Notice;
            if (notice != null) notice(line);
            else report(line);
        }

        /// <summary>The thread dump runs on its own thread: it may block (a runtime lock held by the stalled thread).</summary>
        private void StartThreadDump()
        {
            StartThreadDump(threadDump);
        }

        private void StartThreadDump(Action<int, string> dump)
        {
            int tid = gameTid;
            if (dump == null || tid == 0) return;
            string comm = ThreadName(tid);
            var thread = new Thread(() =>
            {
                try
                {
                    dump(tid, comm);
                }
                catch (Exception ex)
                {
                    report("[watchdog] thread dump failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            });
            thread.IsBackground = true;
            thread.Name = "mcvs-threaddump";
            thread.Start();
        }

        /// <summary>Runs ON the game thread inside GameSynchronizationContext.ExecuteItems: must never throw.</summary>
        private void Beat(object state)
        {
            try
            {
                lastBeat = Environment.TickCount;
                beats++;
                int tid = NativeThreads.CurrentTid();
                if (gameTid == 0)
                {
                    gameTid = tid;
                    gameThreadName = Thread.CurrentThread.Name;
                }
                else if (tid != 0 && tid != gameTid && otherBeatTid == 0)
                {
                    otherBeatTid = tid;
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                beatOutstanding = 0;
            }
        }

        private IThreadSnapshot Snapshotter()
        {
            IThreadSnapshot current = threads;
            if (current == null) threads = current = new ProcThreadSnapshot(null);
            return current;
        }

        private string ThreadName(int tid)
        {
            try
            {
                return Snapshotter().Name(tid) ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private string Snapshot(int lagMs, int frameClockMs, bool suspended, bool withJava, string headline = "STALL: no game-thread work item ran for ")
        {
            var sb = new StringBuilder();
            sb.Append("[watchdog] ").Append(headline).Append(lagMs.ToString(CultureInfo.InvariantCulture))
              .Append(" ms; frame clock (totalTime) unchanged for ").Append(frameClockMs.ToString(CultureInfo.InvariantCulture))
              .Append(" ms; suspendUpdateInBackground=").Append(suspended ? "true" : "false")
              .Append(HostProcess.Suffix("; ")).Append('\n');
            try
            {
                if (serverState != null) sb.Append("  server: ").Append(serverState()).Append('\n');
            }
            catch (Exception)
            {
            }
            int tid = gameTid;
            string comm = null;
            IThreadSnapshot source = Snapshotter();
            if (tid != 0)
            {
                sb.Append("  game thread ").Append(tid.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(source.Describe(tid)).Append('\n');
                comm = ThreadName(tid);
            }
            else
            {
                sb.Append("  game thread: unknown (no work item ever ran)\n");
            }
            try
            {
                List<string> lines = source.Busiest(tid, 12);
                sb.Append("  ").Append(source.BusiestTitle).Append(":\n");
                foreach (string line in lines) sb.Append("    ").Append(line).Append('\n');
            }
            catch (Exception ex)
            {
                sb.Append("  ").Append(source.Source).Append(" unreadable: ").Append(ex.Message).Append('\n');
            }
            if (withJava && javaStacks != null)
            {
                try
                {
                    string stacks = javaStacks(comm);
                    if (!string.IsNullOrEmpty(stacks)) sb.Append("  Java stacks:\n").Append(stacks);
                }
                catch (Exception ex)
                {
                    sb.Append("  Java stacks unavailable: ").Append(ex.GetType().Name).Append('\n');
                }
            }
            return sb.ToString();
        }
    }
}
