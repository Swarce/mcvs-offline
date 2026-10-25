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
    /// stacks of the game thread and the UI thread (a JNI call stuck in Java shows up there).
    /// </para>
    /// Reflection only (no reference to Gameloft.Engine); everything that runs on the game thread is one field write in a
    /// try/catch. Off when GameApplication cannot be found (tests, other builds).
    /// </summary>
    public sealed class GameThreadWatchdog
    {
        private static readonly int[] DefaultReportAtMs = { 5000, 15000, 60000, 180000 };

        private readonly Func<string> serverState;
        private readonly Action<string> report;
        private readonly Func<string, string> javaStacks;
        private readonly int intervalMs;
        private readonly int[] ReportAtMs;
        private int lastBeat = Environment.TickCount;
        private int beatOutstanding;
        private volatile int gameTid;
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

        /// <param name="serverState">e.g. the boot trace's Describe().</param>
        /// <param name="report">Where snapshots go (logcat + bootstrap.log).</param>
        /// <param name="javaStacks">Java stacks of the threads whose name matches (Android); may be null.</param>
        /// <param name="intervalMs">Heartbeat period (tests use a short one).</param>
        /// <param name="reportAtMs">Stall durations that produce a snapshot (default 5, 15, 60, 180 s; Java stacks at the second).</param>
        public GameThreadWatchdog(Func<string> serverState, Action<string> report, Func<string, string> javaStacks, int intervalMs = 1000,
                                  int[] reportAtMs = null)
        {
            this.serverState = serverState;
            this.report = report ?? (s => { });
            this.javaStacks = javaStacks;
            this.intervalMs = intervalMs < 20 ? 20 : intervalMs;
            ReportAtMs = reportAtMs != null && reportAtMs.Length > 0 ? reportAtMs : DefaultReportAtMs;
        }

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
            return "game thread tid " + gameTid.ToString(CultureInfo.InvariantCulture) + " ('" + (gameThreadName ?? "?") +
                   "'), last work item " + (Environment.TickCount - lastBeat).ToString(CultureInfo.InvariantCulture) + " ms ago, " +
                   beats.ToString(CultureInfo.InvariantCulture) + " beats";
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
                        report("[watchdog] game thread is tid " + gameTid.ToString(CultureInfo.InvariantCulture) + " '" + gameThreadName + "'");
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
                }
                catch (Exception ex)
                {
                    report("[watchdog] " + ex.GetType().Name + ": " + ex.Message);
                    try { Thread.Sleep(10000); } catch (Exception) { }
                }
            }
        }

        /// <summary>Runs ON the game thread inside GameSynchronizationContext.ExecuteItems: must never throw.</summary>
        private void Beat(object state)
        {
            try
            {
                lastBeat = Environment.TickCount;
                beats++;
                if (gameTid == 0)
                {
                    gameTid = NativeThreads.CurrentTid();
                    gameThreadName = Thread.CurrentThread.Name;
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
                sb.Append("  game thread ").Append(tid.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(NativeThreads.Describe(tid, true)).Append('\n');
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
