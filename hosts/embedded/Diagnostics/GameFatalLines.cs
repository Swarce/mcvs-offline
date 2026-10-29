// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (no Gameloft.Logs type): compiled into the embedded DLLs and the net8 tests. Only the PC host hands one
// to GameLogBridge (WindowsPlatform.ApplyGameLog), so Android is unchanged.
using System;
using System.Text;

namespace McvsOffline.Embedded.Diagnostics
{
    /// <summary>
    /// The game's own Fatal log lines on PC (docs/design/pc-port.md §6.4, WR2 F3). The exe's top-level exception filter never
    /// chains, so no AppDomain handler runs for an exception that unwinds into native code; the game logs it first, though:
    /// <c>MenuComponent.OnInvokeDelegate</c> catches the exception of a menu callback, logs it as Fatal, calls
    /// <c>GameApplication.TrackUnhandledException</c> (a second Fatal line, with the inner exception) and rethrows into the native
    /// caller (PCD Gameloft.Engine/MenuComponent.cs:849-890); <c>UpdateWorlds</c> does the same through TrackUnhandledException
    /// (GameApplication.cs:1347). Each such incident becomes one "PROBABLY FATAL" <c>exception-*.txt</c> with the exception and
    /// the managed stack of the logging thread; the TrackUnhandledException line of an incident already reported (its exception
    /// is in the chain of the reported one) is not reported again. Every Fatal line also becomes
    /// <see cref="CrashReporter.LastGameFatal"/>, which the native filter's report quotes. Never throws.
    /// </summary>
    public sealed class GameFatalLines
    {
        public const string MenuSource = "MenuComponent.OnInvokeDelegate";
        public const string TrackSource = "GameApplication.TrackUnhandledException";

        /// <summary>Longest <see cref="CrashReporter.LastGameFatal"/> kept (the filter copies it into a fixed buffer).</summary>
        public const int LastFatalChars = 600;

        /// <summary>Longest game line quoted in a report (its exception text is in the report already).</summary>
        public const int LineChars = 8192;

        private readonly Action<string, object, string> report;
        private readonly Func<string> stack;
        private readonly object gate = new object();
        private Exception lastReported;

        /// <param name="report">(source, exception, detail): <see cref="CrashReporter.ReportProbablyFatal"/> in the game.</param>
        /// <param name="stack">The managed stack of the calling thread (default Environment.StackTrace; tests fix it).</param>
        public GameFatalLines(Action<string, object, string> report, Func<string> stack = null)
        {
            this.report = report;
            this.stack = stack ?? (() => Environment.StackTrace);
        }

        /// <summary><see cref="MenuSource"/>, <see cref="TrackSource"/> or null: whether a Fatal line announces a rethrow.</summary>
        public static string CrashCandidate(string member, string message)
        {
            if (member == "OnInvokeDelegate" || (message != null && message.StartsWith(MenuSource, StringComparison.Ordinal))) return MenuSource;
            if (member == "TrackUnhandledException" || message == "UnhandledException") return TrackSource;
            return null;
        }

        /// <summary>
        /// One Fatal entry of the game's log, on the thread that logged it. <paramref name="line"/> is the line as game.log has it
        /// (redacted, with the exception text).
        /// </summary>
        public void Fatal(string member, string message, Exception exception, string line)
        {
            try
            {
                CrashReporter.LastGameFatal = Short(line);
                string source = CrashCandidate(member, message);
                if (source == null) return;
                lock (gate)
                {
                    if (exception != null && InChain(lastReported, exception)) return;
                    if (exception != null) lastReported = exception;
                }
                var detail = new StringBuilder();
                detail.Append("--- the game's Fatal line ---\n").Append(Cut(line, LineChars)).Append('\n');
                string trace = null;
                try
                {
                    trace = stack();
                }
                catch (Exception)
                {
                }
                detail.Append("--- managed stack of the logging thread ---\n").Append(trace ?? "(unavailable)");
                Action<string, object, string> sink = report;
                if (sink != null) sink(source, exception, detail.ToString());
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Whether <paramref name="exception"/> is <paramref name="reported"/> or one of its inner exceptions.</summary>
        internal static bool InChain(Exception reported, Exception exception)
        {
            int depth = 0;
            for (Exception e = reported; e != null && depth < 16; e = e.InnerException, depth++)
            {
                if (ReferenceEquals(e, exception)) return true;
            }
            return false;
        }

        /// <summary>The line on one line ("\n" as " | "), at most <see cref="LastFatalChars"/> characters.</summary>
        internal static string Short(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            string text = Cut(line, LastFatalChars);
            return text.Replace("\r", "").Replace("\n", " | ");
        }

        private static string Cut(string text, int max)
        {
            if (text == null) return "";
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}
