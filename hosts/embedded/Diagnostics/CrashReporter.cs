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
    /// abandons the task it waits on) can never use up the report of the crash itself. Never throws, never waits on
    /// another thread without a budget.
    /// </summary>
    public static class CrashReporter
    {
        /// <summary>Non-fatal reports (unobserved task faults) written per process; later ones are dropped.</summary>
        public const int NonFatalBudget = 5;

        /// <summary>Fatal reports written per process (the first always is; more only for distinct exceptions).</summary>
        public const int FatalBudget = 3;

        private static int installed;
        private static int fatalReports;
        private static int nonFatalReports;
        private static int sequence;
        private static object lastReported;
        private static bool lastReportedFatal;
        private static volatile string crashDirectory;
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
                if (HookUnobservedTasks()) tasks = "ok";
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
            lastReported = null;
            lastReportedFatal = false;
        }

        /// <summary>Writes one report (see the class remarks). Never throws.</summary>
        public static void Report(string source, object exception, bool fatal)
        {
            if (inReport) return;
            // The raiser, then AppDomain: the same object once. Only an equal or lesser report is a duplicate.
            if (exception != null && ReferenceEquals(exception, lastReported) && (lastReportedFatal || !fatal)) return;
            int used = fatal ? Interlocked.Increment(ref fatalReports) : Interlocked.Increment(ref nonFatalReports);
            if (used > (fatal ? FatalBudget : NonFatalBudget)) return;
            inReport = true;
            try
            {
                lastReported = exception;
                lastReportedFatal = fatal;
                int number = Interlocked.Increment(ref sequence);
                string text = Build(source, exception, fatal);
                SelfLog.Write(fatal ? SelfLog.Fatal : SelfLog.Error, text);
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

        private static string Build(string source, object exception, bool fatal)
        {
            var sb = new StringBuilder(4096);
            Thread t = Thread.CurrentThread;
            sb.Append(fatal ? "FATAL" : "NON-FATAL").Append(" unhandled exception via ").Append(source).Append('\n');
            sb.Append("time ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
              .Append(" (").Append(BootClock.Stamp()).Append(" since start), thread '").Append(t.Name ?? "")
              .Append("' managed ").Append(t.ManagedThreadId.ToString(CultureInfo.InvariantCulture))
              .Append(" tid ").Append(NativeThreads.CurrentTid().ToString(CultureInfo.InvariantCulture)).Append('\n');
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
