// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests (the Android-only hook is added by
// McvsOffline.Embedded.Android.AndroidDiagnostics).
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Embedded.Diagnostics
{
    /// <summary>
    /// Unhandled-exception reporter, installed as the very first thing in <see cref="McvsOffline.Bootstrap.Init"/> (before
    /// the game's own handler, which GameApplication subscribes later, so ours runs first). Device test 1 RUN 2: the managed
    /// crash ran the AppDomain handlers, then Mono called exit(), which crashed the Adreno driver 4 ms later and left a
    /// misleading native tombstone; nothing was on disk. The report is therefore written synchronously and in order of
    /// cost: logcat (FATAL), a crash file (fsync), the queued log lines, then the phone-readable copy.
    /// <list type="bullet">
    /// <item><c>AppDomain.UnhandledException</c>: managed threads, native-to-managed callbacks (the game thread), and Java
    /// uncaught exceptions (JNIEnv.PropagateUncaughtException raises it).</item>
    /// <item>Android's <c>AndroidEnvironment.UnhandledExceptionRaiser</c> (added by the Android code; never sets Handled).</item>
    /// <item>Unobserved task faults, NON-FATAL: <c>TaskScheduler.add_UnobservedTaskException</c> is trimmed from the game's
    /// mscorlib, so the delegate is combined into its backing field by reflection.</item>
    /// </list>
    /// Guards: a per-thread re-entrancy flag; the same exception object is reported once (a fatal report of an object
    /// already reported as non-fatal still goes through); separate budgets of <see cref="NonFatalBudget"/> non-fatal and
    /// <see cref="FatalBudget"/> fatal reports per process, so routine unobserved task faults (MoreAsync.WithCancellation
    /// abandons the task it waits on) can never use up the report of the crash itself. On PC a third kind,
    /// <see cref="ReportProbablyFatal"/> (<see cref="ProbablyFatalBudget"/> per process), records the game's own Fatal lines for
    /// an exception it rethrows into native code, which no AppDomain handler sees there (docs/design/pc-port.md §6.4). Never throws, never waits on
    /// another thread without a budget. The report text goes through <c>LogRedaction.Redact</c> before it is written
    /// anywhere (an exception message may carry a page ticket; design §5.4).
    /// </summary>
    public static class CrashReporter
    {
        /// <summary>Non-fatal reports (unobserved task faults) written per process; later ones are dropped.</summary>
        public const int NonFatalBudget = 5;

        /// <summary>Fatal reports written per process (the first always is; more only for distinct exceptions).</summary>
        public const int FatalBudget = 3;

        /// <summary>"Probably fatal" reports (PC: the game's Fatal lines before it rethrows into native code) per process.</summary>
        public const int ProbablyFatalBudget = 3;

        // Severity of a report: a later report of the same exception object is a duplicate unless it is more severe.
        private const int NonFatalKind = 0;
        private const int ProbablyFatalKind = 1;
        private const int FatalKind = 2;

        private static int installed;
        private static int fatalReports;
        private static int nonFatalReports;
        private static int probablyFatalReports;
        private static int sequence;
        private static object lastReported;
        private static int lastReportedKind;
        private static volatile string crashDirectory;
        private static volatile string lastGameFatal;
        [ThreadStatic] private static bool inReport;

        /// <summary>Recent context (boot trace, watchdog, log files); must be cheap and never block.</summary>
        public static Func<string> Context;

        /// <summary>Drains the asynchronous log files synchronously (bounded by the budget in ms).</summary>
        public static Action<int> FlushLogs;

        /// <summary>Copies a crash file to the phone-readable folder (best effort, non-blocking).</summary>
        public static Action<string> Mirror;

        /// <summary>The installed hooks, e.g. "AppDomain ok, unobserved-tasks ok" (for the start-up line and the tests).</summary>
        public static string Hooks { get; private set; }

        /// <summary>Paths of the crash files written by this process.</summary>
        public static string LastCrashFile { get; private set; }

        public static void Install()
        {
            Install(null);
        }

        /// <summary>
        /// <see cref="Install()"/>, with the unobserved-task hook through <paramref name="subscribeUnobserved"/> first (the PC host
        /// subscribes the public <c>TaskScheduler.UnobservedTaskException</c> event, which the game's Android mscorlib lacks;
        /// docs/design/pc-port.md §6.1); the backing-field hook is the fallback. One of the two, never both (each report once).
        /// </summary>
        public static void Install(Func<EventHandler<UnobservedTaskExceptionEventArgs>, bool> subscribeUnobserved)
        {
            if (Interlocked.Exchange(ref installed, 1) != 0) return;
            string domain = "no", tasks = "no";
            try
            {
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
                domain = "ok";
            }
            catch (Exception)
            {
            }
            try
            {
                if (subscribeUnobserved != null && subscribeUnobserved(OnUnobserved)) tasks = "ok (event)";
            }
            catch (Exception)
            {
            }
            try
            {
                if (tasks == "no" && HookUnobservedTasks()) tasks = "ok";
            }
            catch (Exception)
            {
            }
            Hooks = "AppDomain " + domain + ", unobserved-tasks " + tasks;
        }

        /// <summary>Adds a hook name to <see cref="Hooks"/> (the Android raiser).</summary>
        public static void AddHook(string description)
        {
            Hooks = (Hooks == null ? "" : Hooks + ", ") + description;
        }

        /// <summary>Until this is set (the game folder is known), reports go to logcat only.</summary>
        public static void SetCrashDirectory(string directory)
        {
            crashDirectory = directory;
        }

        /// <summary>Where crash files go (null until the host knows the game folder); the PC native filter reads it.</summary>
        public static string CrashDirectory
        {
            get { return crashDirectory; }
        }

        /// <summary>
        /// The game's last Fatal log line, redacted and short (PC: set by the game-log bridge, read by the native crash filter,
        /// which cannot see the managed exception); null when there was none.
        /// </summary>
        public static string LastGameFatal
        {
            get { return lastGameFatal; }
            set { lastGameFatal = value; }
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            // UnhandledExceptionEventArgs.IsTerminating is trimmed from the game's mscorlib; on this runtime every unhandled
            // exception terminates the process.
            Report("AppDomain.UnhandledException", e == null ? null : e.ExceptionObject, true);
        }

        private static void OnUnobserved(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Report("TaskScheduler.UnobservedTaskException", UnobservedException(e), false);
        }

        /// <summary>UnobservedTaskExceptionEventArgs.Exception is trimmed from the game's mscorlib: property, else its field.</summary>
        private static object UnobservedException(object args)
        {
            if (args == null) return null;
            try
            {
                Type t = args.GetType();
                PropertyInfo p = t.GetProperty("Exception", BindingFlags.Instance | BindingFlags.Public);
                if (p != null) return p.GetValue(args, null);
                FieldInfo f = t.GetField("m_exception", BindingFlags.Instance | BindingFlags.NonPublic);
                return f == null ? null : f.GetValue(args);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// TaskScheduler.add_UnobservedTaskException is trimmed from the game's mscorlib; its static backing field
        /// (_unobservedTaskException in Mono's reference source, UnobservedTaskException on .NET) is not.
        /// </summary>
        private static bool HookUnobservedTasks()
        {
            var handler = new EventHandler<UnobservedTaskExceptionEventArgs>(OnUnobserved);
            foreach (string name in new[] { "_unobservedTaskException", "UnobservedTaskException" })
            {
                FieldInfo field = typeof(TaskScheduler).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
                if (field == null || field.FieldType != typeof(EventHandler<UnobservedTaskExceptionEventArgs>)) continue;
                field.SetValue(null, Delegate.Combine((Delegate)field.GetValue(null), handler));
                return true;
            }
            return false;
        }

        /// <summary>Tests only: a fresh budget and duplicate guard (the reporter is process-wide).</summary>
        internal static void ResetForTests()
        {
            Interlocked.Exchange(ref fatalReports, 0);
            Interlocked.Exchange(ref nonFatalReports, 0);
            Interlocked.Exchange(ref probablyFatalReports, 0);
            lastReported = null;
            lastReportedKind = NonFatalKind;
            lastGameFatal = null;
        }

        /// <summary>Writes one report (see the class remarks). Never throws.</summary>
        public static void Report(string source, object exception, bool fatal)
        {
            Report(source, exception, fatal ? FatalKind : NonFatalKind, null);
        }

        /// <summary>
        /// PC: the game logged an exception as Fatal and rethrows it into native code, where the process usually ends without
        /// any managed handler running (docs/design/pc-port.md §6.4). An <c>exception-*.txt</c> headed "PROBABLY FATAL" with the
        /// exception, <paramref name="detail"/> (the game's line and the managed stack of the logging thread) and the context.
        /// Its own budget; a later FATAL report of the same object still goes through. Never throws.
        /// </summary>
        public static void ReportProbablyFatal(string source, object exception, string detail)
        {
            Report(source, exception, ProbablyFatalKind, detail);
        }

        private static void Report(string source, object exception, int kind, string detail)
        {
            if (inReport) return;
            // The raiser, then AppDomain: the same object once. Only an equal or lesser report is a duplicate.
            if (exception != null && ReferenceEquals(exception, lastReported) && lastReportedKind >= kind) return;
            int used, budget;
            switch (kind)
            {
                case FatalKind:
                    used = Interlocked.Increment(ref fatalReports);
                    budget = FatalBudget;
                    break;
                case ProbablyFatalKind:
                    used = Interlocked.Increment(ref probablyFatalReports);
                    budget = ProbablyFatalBudget;
                    break;
                default:
                    used = Interlocked.Increment(ref nonFatalReports);
                    budget = NonFatalBudget;
                    break;
            }
            if (used > budget) return;
            bool fatal = kind == FatalKind;
            inReport = true;
            try
            {
                lastReported = exception;
                lastReportedKind = kind;
                int number = Interlocked.Increment(ref sequence);
                // Crash files are mirrored to Download/ and zipped for sharing: no ticket, cookie or password in them (§5.4).
                string text = Redact(Build(source, exception, kind, detail));
                SelfLog.Write(kind == NonFatalKind ? SelfLog.Error : SelfLog.Fatal, text);
                string path = WriteCrashFile(text, fatal, number);
                try
                {
                    Action<int> flush = FlushLogs;
                    if (flush != null) flush(200);
                }
                catch (Exception)
                {
                }
                try
                {
                    Action<string> mirror = Mirror;
                    if (path != null && mirror != null) mirror(path);
                }
                catch (Exception)
                {
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                inReport = false;
            }
        }

        private static string Build(string source, object exception, int kind, string detail)
        {
            var sb = new StringBuilder(4096);
            Thread t = Thread.CurrentThread;
            if (kind == ProbablyFatalKind)
                sb.Append("PROBABLY FATAL exception via ").Append(source)
                  .Append(" (the game logged it as Fatal and rethrows it into native code, where the process usually ends)\n");
            else
                sb.Append(kind == FatalKind ? "FATAL" : "NON-FATAL").Append(" unhandled exception via ").Append(source).Append('\n');
            sb.Append("time ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
              .Append(" (").Append(BootClock.Stamp()).Append(" since start), thread '").Append(t.Name ?? "")
              .Append("' managed ").Append(t.ManagedThreadId.ToString(CultureInfo.InvariantCulture))
              .Append(" tid ").Append(NativeThreads.CurrentTid().ToString(CultureInfo.InvariantCulture)).Append(HostProcess.Suffix(", ")).Append('\n');
            sb.Append(exception == null ? "(no exception object)" : exception.ToString()).Append('\n');
            var aggregate = exception as AggregateException;
            if (aggregate != null)
            {
                int i = 0;
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    sb.Append("inner[").Append(i.ToString(CultureInfo.InvariantCulture)).Append("] ").Append(inner).Append('\n');
                    i++;
                }
            }
            if (!string.IsNullOrEmpty(detail)) sb.Append(detail).Append('\n');
            try
            {
                Func<string> context = Context;
                string text = context == null ? null : context();
                if (!string.IsNullOrEmpty(text)) sb.Append("--- context ---\n").Append(text).Append('\n');
            }
            catch (Exception)
            {
            }
            return sb.ToString();
        }

        /// <summary>LogRedaction.Redact; the unredacted text if that ever fails (a report is never lost).</summary>
        private static string Redact(string text)
        {
            try
            {
                return McvsOffline.Server.Core.Http.LogRedaction.Redact(text) ?? text;
            }
            catch (Exception)
            {
                return text;
            }
        }

        private static string WriteCrashFile(string text, bool fatal, int number)
        {
            string dir = crashDirectory;
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                Directory.CreateDirectory(dir);
                string name = (fatal ? "crash-" : "exception-") + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) +
                              "-" + number.ToString(CultureInfo.InvariantCulture) + ".txt";
                string path = Path.Combine(dir, name);
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    FileSystem.FlushToDisk(fs);
                }
                LastCrashFile = path;
                SelfLog.Write(SelfLog.Info, "[crash] report written to " + path);
                return path;
            }
            catch (Exception ex)
            {
                SelfLog.Write(SelfLog.Error, "[crash] cannot write the crash file in " + dir + ": " + ex.GetType().FullName + ": " + ex.Message);
                return null;
            }
        }
    }
}
