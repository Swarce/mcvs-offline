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
        private Dictionary<int, long> lastCpu = new Dictionary<int, long>();
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
        /// Routine lines that are not problems (the game thread's tid, a lease's "paused: ..." and "resumed"): EmbeddedHost logs them
        /// at INFO, because WARN lines are the settings page's "recent problems" and every page open would add two. Null: they go
        /// to the report callback with the rest. Set before <see cref="Start"/>.
        /// </summary>
        public Action<string> Notice { get; set; }

        public int GameTid { get { return gameTid; } }

        public int Beats { get { return beats; } }

        public void Start()
        {
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
            return "game thread tid " + gameTid.ToString(CultureInfo.InvariantCulture) + " ('" + (gameThreadName ?? "?") +
                   "'), last work item " + (Environment.TickCount - lastBeat).ToString(CultureInfo.InvariantCulture) + " ms ago, " +
                   beats.ToString(CultureInfo.InvariantCulture) + " beats" + paused;
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
                    Type app = Type.GetType("Gameloft.Engine.GameApplication, Gameloft.Engine", false);
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
            bool announced = false;
            while (!stopped)
            {
                try
                {
                    Thread.Sleep(intervalMs);
                    object app = instanceProperty.GetValue(null, null);
                    if (app == null)
                    {
                        lastBeat = Environment.TickCount;   // not created yet: nothing to watch
                        continue;
                    }
                    if (Interlocked.CompareExchange(ref beatOutstanding, 1, 0) == 0)
                    {
                        var context = contextProperty.GetValue(app, null) as SynchronizationContext;
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
                        stallReports = 0;
                        continue;
                    }
                    if (pauseReported)
                    {
                        pauseReported = false;
                        lastBeat = Environment.TickCount;   // the lease ended: the next stall is measured from here
                        Note("[watchdog] resumed");
                    }

                    double total = totalTimeField == null ? -2 : (double)totalTimeField.GetValue(app);
                    if (total != lastTotal)
                    {
                        lastTotal = total;
                        lastTotalChange = Environment.TickCount;
                    }
                    bool suspended = suspendField != null && (bool)suspendField.GetValue(app);
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

        private void Note(string line)
        {
            Action<string> notice = Notice;
            if (notice != null) notice(line);
            else report(line);
        }

        /// <summary>The thread dump runs on its own thread: it may block (a runtime lock held by the stalled thread).</summary>
        private void StartThreadDump()
        {
            int tid = gameTid;
            Action<int, string> dump = threadDump;
            if (dump == null || tid == 0) return;
            string comm = (NativeThreads.Read("/proc/self/task/" + tid.ToString(CultureInfo.InvariantCulture) + "/comm") ?? "").Trim();
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

        private string Snapshot(int lagMs, int frameClockMs, bool suspended, bool withJava)
        {
            var sb = new StringBuilder();
            sb.Append("[watchdog] STALL: no game-thread work item ran for ").Append(lagMs.ToString(CultureInfo.InvariantCulture))
              .Append(" ms; frame clock (totalTime) unchanged for ").Append(frameClockMs.ToString(CultureInfo.InvariantCulture))
              .Append(" ms; suspendUpdateInBackground=").Append(suspended ? "true" : "false").Append('\n');
            try
            {
                if (serverState != null) sb.Append("  server: ").Append(serverState()).Append('\n');
            }
            catch (Exception)
            {
            }
            int tid = gameTid;
            string comm = null;
            if (tid != 0)
            {
                // /proc/<tid>/syscall is root-owned while the process is not dumpable ("syscall ?" in device test 2): read it with
                // PR_SET_DUMPABLE raised when the thread dump is enabled (it does the same).
                string state = threadDump != null ? ThreadDump.WithDumpable(() => NativeThreads.Describe(tid, true)) : NativeThreads.Describe(tid, true);
                sb.Append("  game thread ").Append(tid.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(state).Append('\n');
                comm = (NativeThreads.Read("/proc/self/task/" + tid.ToString(CultureInfo.InvariantCulture) + "/comm") ?? "").Trim();
            }
            else
            {
                sb.Append("  game thread: unknown (no work item ever ran)\n");
            }
            try
            {
                var cpu = new Dictionary<int, long>();
                var lines = new List<KeyValuePair<long, string>>();
                foreach (int id in NativeThreads.Tids())
                {
                    string[] f = NativeThreads.StatFields(id);
                    long ticks = NativeThreads.CpuTicks(f);
                    if (ticks < 0) continue;
                    cpu[id] = ticks;
                    long previous;
                    long delta = lastCpu.TryGetValue(id, out previous) ? ticks - previous : 0;
                    bool blocked = f[0] == "D";
                    if (delta > 0 || blocked)
                    {
                        lines.Add(new KeyValuePair<long, string>(blocked ? McvsOffline.Server.Core.Num.LongMax / 2 : delta,
                            id.ToString(CultureInfo.InvariantCulture) + " " + NativeThreads.Describe(id, blocked) + " +" + delta.ToString(CultureInfo.InvariantCulture) + " ticks"));
                    }
                }
                lastCpu = cpu;
                lines.Sort((a, b) => b.Key.CompareTo(a.Key));
                sb.Append("  busiest threads since the last snapshot (and every thread in D state):\n");
                for (int i = 0; i < lines.Count && i < 12; i++) sb.Append("    ").Append(lines[i].Value).Append('\n');
            }
            catch (Exception ex)
            {
                sb.Append("  /proc/self/task unreadable: ").Append(ex.Message).Append('\n');
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
