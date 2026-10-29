// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLLs (Android and PC) and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Embedded.Diagnostics
{
    /// <summary>
    /// Keeps the logs of this start and of the previous N starts (settings <c>logging.keepStarts</c>, 0 to 10, default 1) and
    /// deletes older log files at every start (docs/design/settings-page.md §7.7).
    /// <list type="bullet">
    /// <item>A start is one host process. <see cref="Begin"/> (UI thread, in Prepare, milliseconds) reads <c>logs/starts.txt</c>
    /// (one line per start: <c>&lt;ISO UTC start&gt; &lt;platform&gt; &lt;version&gt;</c>, the last 50 kept), computes the cutoff and
    /// appends this start. The cutoff is the recorded start time of the oldest start kept (N = 0: this start's own time); a file
    /// whose last write is before it belongs to an older start. Not enough starts recorded yet, an unreadable starts.txt, or a clock
    /// that went back: no cutoff, and nothing is deleted by age.</item>
    /// <item>The four rotating logs begin a new file at every start (<see cref="SafeLogFileOptions.NewFileAtStart"/>), so their
    /// numbered files are whole starts and the rule works on whole files.</item>
    /// <item><see cref="StartCleanup"/> runs on the background thread <c>mcvs-log-retention</c>, after every log file of this start
    /// exists (after McvsServer.Create). Deletion is an allowlist of names in exactly two folders here (logs/ and the fallback log
    /// folder), never recursive: <see cref="IsFamilyFile"/> and <see cref="IsEventFile"/>. A family that has an open
    /// <see cref="SafeLogFile"/> is cleaned by that object under its I/O lock (no race with its rotation), and the file it writes
    /// is never deleted. Everything else (starts.txt, restart-requested.txt, temp files, sub-folders, unknown names) stays.</item>
    /// <item>At most <see cref="BudgetMs"/> of work, then the next start goes on; one INFO line (WARN when a file could not be
    /// deleted) in bootstrap.log, category <c>logs</c>. Never throws.</item>
    /// </list>
    /// The Downloads copies (PublicMirror) use <see cref="KeepStarts"/>, <see cref="CutoffUtc"/> and <see cref="ClockWentBackNow"/>; the
    /// exit traces (ExitReasons) <see cref="TraceCutoffUtc"/>.
    /// </summary>
    internal sealed class LogRetention
    {
        public const string StartsFile = "starts.txt";
        public const int DefaultKeepStarts = 1;
        public const int MaxKeepStarts = 10;
        public const int StartsKept = 50;
        public const int BudgetMs = 5000;

        /// <summary>The <see cref="NoCutoffReason"/> when a kept start is after this one.</summary>
        public const string ClockWentBack = "the clock went back";

        /// <summary>The rotating logs (each with its numbered files name.1 ... name.N).</summary>
        public static readonly string[] Families = { "bootstrap.log", "server.log", "game.log", "unknown-endpoints.jsonl" };

        /// <summary>The logs that can move to the fallback folder (unknown-endpoints.jsonl has none).</summary>
        private static readonly string[] FallbackFamilies = { "bootstrap.log", "server.log", "game.log" };

        private static readonly string[] EventKinds = { "crash", "exception", "threaddump", "exit" };

        private readonly string logsDir;
        private readonly ManualResetEvent done = new ManualResetEvent(false);
        private int started;

        private LogRetention(string logsDir, DateTime startedUtc, int keepStarts, DateTime? cutoffUtc, string noCutoffReason, DateTime? traceCutoffUtc)
        {
            this.logsDir = logsDir;
            StartedUtc = startedUtc;
            KeepStarts = keepStarts;
            CutoffUtc = cutoffUtc;
            NoCutoffReason = noCutoffReason;
            TraceCutoffUtc = traceCutoffUtc;
        }

        public int KeepStarts { get; private set; }

        public DateTime StartedUtc { get; private set; }

        /// <summary>Files last written before this belong to older starts; null: nothing is deleted by age.</summary>
        public DateTime? CutoffUtc { get; private set; }

        /// <summary>Why there is no cutoff ("only 0 starts recorded so far", <see cref="ClockWentBack"/>), or null.</summary>
        public string NoCutoffReason { get; private set; }

        /// <summary>A kept start is after this one: the time order of the starts (and of names made from the local time) is not reliable.</summary>
        public bool ClockWentBackNow
        {
            get { return CutoffUtc == null && string.Equals(NoCutoffReason, ClockWentBack, StringComparison.Ordinal); }
        }

        /// <summary>
        /// Exit traces (ExitReasons) of processes that ended before this are not saved again; null: all are. Equals
        /// <see cref="CutoffUtc"/> when N &gt;= 1; with N = 0 the previous start (see <see cref="TraceCutoff"/>).
        /// </summary>
        public DateTime? TraceCutoffUtc { get; private set; }

        /// <summary>
        /// Reads starts.txt, computes the cutoff from the starts recorded there and appends this start (keeping the last
        /// <see cref="StartsKept"/>). A file that cannot be read gives no cutoff; one that cannot be written is one WARN (the cutoff
        /// from the lines read still applies: the next start then does not know this one and keeps more, never less). Never throws.
        /// </summary>
        /// <param name="startLine">"&lt;platform&gt; &lt;version&gt;", written after the time.</param>
        public static LogRetention Begin(string logsDir, DateTime startedUtc, int keepStarts, string startLine, Log log)
        {
            int keep = (int)Num.Clamp(keepStarts, 0, MaxKeepStarts);
            log = log ?? Log.Null;
            try
            {
                string file = Path.Combine(logsDir, StartsFile);
                var lines = new List<string>();
                var times = new List<DateTime>();
                bool readable = true;
                try
                {
                    string text = AtomicFile.ReadAllTextOrNull(file);
                    if (text != null)
                    {
                        foreach (string raw in text.Split('\n'))
                        {
                            string line = raw.Trim();
                            DateTime time;
                            if (!TryParseStart(line, out time)) continue;   // damaged or empty: skipped
                            lines.Add(line);
                            times.Add(time);
                        }
                    }
                }
                catch (Exception ex)
                {
                    readable = false;
                    log.Warn(file + " could not be read: no logs are deleted by age at this start", ex);
                }
                string reason = null;
                DateTime? cutoff = readable ? Cutoff(times, startedUtc, keep, out reason) : null;
                if (!readable) reason = StartsFile + " could not be read";
                DateTime? traceCutoff = readable ? TraceCutoff(times, startedUtc, keep, cutoff) : null;
                lines.Add(FormatStart(startedUtc) + " " + (startLine ?? "").Trim());
                if (lines.Count > StartsKept) lines.RemoveRange(0, lines.Count - StartsKept);
                try
                {
                    AtomicFile.WriteAllText(file, string.Join("\n", lines.ToArray()) + "\n");
                }
                catch (Exception ex)
                {
                    log.Warn("cannot write " + file + " (the next start keeps more logs)", ex);
                }
                return new LogRetention(logsDir, startedUtc, keep, cutoff, reason, traceCutoff);
            }
            catch (Exception ex)
            {
                try { log.Warn("log retention could not start: no logs are deleted at this start", ex); } catch (Exception) { }
                return new LogRetention(logsDir, startedUtc, keep, null, "the start could not be recorded", null);
            }
        }

        /// <summary>
        /// previous = start times recorded before this one (file order). N = 0: this start; fewer than N recorded: none; a kept
        /// start after this one (the clock went back): none; otherwise the earliest time among the last N (file order is not time
        /// order once the clock was corrected: a start after a correction is still appended, and its files must stay).
        /// </summary>
        internal static DateTime? Cutoff(List<DateTime> previous, DateTime startedUtc, int keep, out string reason)
        {
            reason = null;
            if (keep <= 0) return startedUtc;
            if (previous.Count < keep)
            {
                reason = "only " + previous.Count.ToString(CultureInfo.InvariantCulture) + (previous.Count == 1 ? " start" : " starts") +
                         " recorded so far";
                return null;
            }
            DateTime earliest = previous[previous.Count - keep];
            for (int i = previous.Count - keep; i < previous.Count; i++)
            {
                if (previous[i] > startedUtc)
                {
                    reason = ClockWentBack;
                    return null;
                }
                if (previous[i] < earliest) earliest = previous[i];
            }
            return earliest;
        }

        /// <summary>
        /// The exit traces' cutoff (ExitReasons): <paramref name="cutoff"/> when N &gt;= 1. N = 0: the previous recorded start (the
        /// process that just ended ran after it, so its trace is saved, and the next start deletes it with this start's files); none
        /// recorded, or one after this start: null (all saved).
        /// </summary>
        internal static DateTime? TraceCutoff(List<DateTime> previous, DateTime startedUtc, int keep, DateTime? cutoff)
        {
            if (keep > 0) return cutoff;
            if (previous == null || previous.Count == 0) return null;
            DateTime last = previous[previous.Count - 1];
            return last > startedUtc ? (DateTime?)null : last;
        }

        /// <summary>"2026-10-29T08:14:03.020Z".</summary>
        internal static string FormatStart(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        private static bool TryParseStart(string line, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (string.IsNullOrEmpty(line)) return false;
            int space = line.IndexOf(' ');
            string token = space < 0 ? line : line.Substring(0, space);
            if (token.Length < 20 || token[4] != '-' || token[10] != 'T' || token[token.Length - 1] != 'Z') return false;
            return TimeFormat.TryParse(token, out utc);
        }

        /// <summary>
        /// Starts the cleanup on the thread <c>mcvs-log-retention</c> (once; later calls do nothing). <paramref name="owners"/> are
        /// the open log files of this process (null entries are ignored).
        /// </summary>
        public void StartCleanup(IList<SafeLogFile> owners, string fallbackDir, Log log)
        {
            if (Interlocked.Exchange(ref started, 1) != 0) return;
            log = log ?? Log.Null;
            var copy = new List<SafeLogFile>();
            if (owners != null)
            {
                foreach (SafeLogFile owner in owners)
                {
                    if (owner != null) copy.Add(owner);
                }
            }
            try
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        CleanResult result = Clean(logsDir, fallbackDir, copy, DeleteFile, BudgetMs);
                        if (result.Failed > 0) log.Warn(result.Line);
                        else log.Info(result.Line);
                    }
                    catch (Exception ex)
                    {
                        try { log.Warn("cleanup failed", ex); } catch (Exception) { }
                    }
                    finally
                    {
                        done.Set();
                    }
                });
                thread.IsBackground = true;   // no Thread.Priority: trimmed from the game's mscorlib
                thread.Name = "mcvs-log-retention";
                thread.Start();
            }
            catch (Exception ex)
            {
                done.Set();
                try { log.Warn("cleanup failed", ex); } catch (Exception) { }
            }
        }

        /// <summary>Waits for the cleanup thread (tests); false when it has not finished (or not started) in time.</summary>
        public bool Wait(int timeoutMs)
        {
            return done.WaitOne(timeoutMs, false);
        }

        private static bool DeleteFile(string path)
        {
            File.Delete(path);
            return !File.Exists(path);
        }

        /// <summary>
        /// One cleanup (tests call it directly). <paramref name="delete"/> returns true when the file is gone, false or an exception
        /// when not; the work stops once <paramref name="budgetMs"/> have passed (checked between files).
        /// </summary>
        internal CleanResult Clean(string logs, string fallbackDir, IList<SafeLogFile> owners, Func<string, bool> delete, int budgetMs)
        {
            var result = new CleanResult();
            string prefix = KeepStarts <= 0 ? "keeping only this start" : "keeping this start and " + KeepStarts.ToString(CultureInfo.InvariantCulture) + " previous";
            if (CutoffUtc == null)
            {
                result.Line = prefix + ": " + (NoCutoffReason ?? "no cutoff") + ", nothing deleted this time";
                return result;
            }
            DateTime cutoff = CutoffUtc.Value;
            prefix += " (since " + CutoffUtc.Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + ")";
            int startTick = Environment.TickCount;
            bool overBudget = false;
            Func<string, bool> attempt = path =>
            {
                if (overBudget)
                {
                    result.Stopped = true;
                    return false;
                }
                long size = 0;
                try { size = new FileInfo(path).Length; } catch (Exception) { }
                bool ok = false;
                try
                {
                    ok = delete(path);
                    if (!ok) result.Fail(null);
                }
                catch (Exception ex)
                {
                    result.Fail(ex);
                }
                if (ok)
                {
                    result.Deleted.Add(path);
                    result.Bytes += size;
                }
                if (Environment.TickCount - startTick >= budgetMs) overBudget = true;
                return ok;
            };

            var owned = new Dictionary<string, SafeLogFile>(StringComparer.Ordinal);
            var writing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (owners != null)
            {
                foreach (SafeLogFile owner in owners)
                {
                    if (owner == null) continue;
                    owned[Path.GetFileName(owner.Path)] = owner;
                    writing.Add(Path.GetFullPath(owner.Path));
                }
            }

            // logs/: rotating families without an owner this process, and the event files.
            string logsFull = string.IsNullOrEmpty(logs) ? null : Path.GetFullPath(logs);
            foreach (string file in ListFiles(logsFull))
            {
                string name = Path.GetFileName(file);
                string family;
                bool rotated;
                bool candidate = IsFamilyFile(name, out family, out rotated) ? !owned.ContainsKey(family) : IsEventFile(name);
                if (!candidate || writing.Contains(file) || !Before(file, cutoff)) continue;
                attempt(file);
            }
            // The open files delete their own older files (logs/ and their fallback folder) under their I/O lock.
            foreach (SafeLogFile owner in owned.Values)
            {
                owner.DeleteFilesBefore(cutoff, attempt, null, null);
            }
            // The fallback folder: only its own log names, never its parent (hd-install/) or anything else.
            string fallbackFull = string.IsNullOrEmpty(fallbackDir) ? null : Path.GetFullPath(fallbackDir);
            if (fallbackFull != null && !string.Equals(fallbackFull, logsFull, StringComparison.Ordinal))
            {
                foreach (string file in ListFiles(fallbackFull))
                {
                    string family;
                    bool rotated;
                    if (!IsFamilyFile(Path.GetFileName(file), out family, out rotated) || Array.IndexOf(FallbackFamilies, family) < 0) continue;
                    SafeLogFile owner;
                    if (owned.TryGetValue(family, out owner) && SameDirectory(owner.FallbackDirectory, fallbackFull)) continue;   // done above
                    if (writing.Contains(file) || !Before(file, cutoff)) continue;
                    attempt(file);
                }
            }
            result.Line = prefix + ": " + Summary(result, logsFull, fallbackFull, budgetMs);
            return result;
        }

        private static List<string> ListFiles(string directory)
        {
            var files = new List<string>();
            try
            {
                if (directory != null && Directory.Exists(directory)) files.AddRange(Directory.GetFiles(directory));
            }
            catch (Exception)
            {
            }
            files.Sort(StringComparer.Ordinal);
            return files;
        }

        private static bool Before(string file, DateTime cutoff)
        {
            try
            {
                return new FileInfo(file).LastWriteTimeUtc < cutoff;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SameDirectory(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>"deleted 23 files, 41.6 MB (server.log.2-5, game.log.1, crash 1, exit 2; fallback 3); ..." or "nothing to delete".</summary>
        private static string Summary(CleanResult result, string logsFull, string fallbackFull, int budgetMs)
        {
            if (result.Deleted.Count == 0 && result.Failed == 0 && !result.Stopped) return "nothing to delete";
            var sb = new StringBuilder();
            sb.Append("deleted ").Append(result.Deleted.Count.ToString(CultureInfo.InvariantCulture))
              .Append(result.Deleted.Count == 1 ? " file, " : " files, ")
              .Append((result.Bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)).Append(" MB");
            if (result.Deleted.Count > 0)
            {
                var numbers = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                var events = new Dictionary<string, int>(StringComparer.Ordinal);
                int fallback = 0;
                foreach (string path in result.Deleted)
                {
                    string dir = Path.GetDirectoryName(path);
                    if (fallbackFull != null && string.Equals(dir, fallbackFull, StringComparison.Ordinal) &&
                        !string.Equals(dir, logsFull, StringComparison.Ordinal))
                    {
                        fallback++;
                        continue;
                    }
                    string name = Path.GetFileName(path);
                    string family;
                    bool rotated;
                    if (IsFamilyFile(name, out family, out rotated))
                    {
                        List<int> list;
                        if (!numbers.TryGetValue(family, out list)) numbers[family] = list = new List<int>();
                        int number = 0;
                        if (rotated)
                        {
                            for (int i = family.Length + 1; i < name.Length; i++) number = number * 10 + (name[i] - '0');
                        }
                        list.Add(number);
                        continue;
                    }
                    foreach (string kind in EventKinds)
                    {
                        if (!name.StartsWith(kind + "-", StringComparison.Ordinal)) continue;
                        int n;
                        events.TryGetValue(kind, out n);
                        events[kind] = n + 1;
                        break;
                    }
                }
                var parts = new List<string>();
                foreach (string family in Families)
                {
                    List<int> list;
                    if (!numbers.TryGetValue(family, out list)) continue;
                    list.Sort();
                    if (list[0] == 0)
                    {
                        parts.Add(family);
                        list.RemoveAt(0);
                    }
                    if (list.Count > 0) parts.Add(family + "." + Ranges(list));
                }
                foreach (string kind in EventKinds)
                {
                    int n;
                    if (events.TryGetValue(kind, out n)) parts.Add(kind + " " + n.ToString(CultureInfo.InvariantCulture));
                }
                string details = string.Join(", ", parts.ToArray());
                if (fallback > 0) details += (details.Length > 0 ? "; " : "") + "fallback " + fallback.ToString(CultureInfo.InvariantCulture);
                if (details.Length > 0) sb.Append(" (").Append(details).Append(')');
            }
            if (result.Failed > 0)
            {
                sb.Append("; ").Append(result.Failed.ToString(CultureInfo.InvariantCulture)).Append(" could not be deleted");
                if (result.FirstError != null) sb.Append(" (").Append(result.FirstError).Append(')');
            }
            if (result.Stopped)
            {
                sb.Append("; stopped after ").Append((budgetMs / 1000).ToString(CultureInfo.InvariantCulture)).Append(" s, the rest at the next start");
            }
            return sb.ToString();
        }

        /// <summary>1, 2, 3, 5 -> "1-3,5".</summary>
        private static string Ranges(List<int> sorted)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < sorted.Count)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(sorted[i].ToString(CultureInfo.InvariantCulture));
                if (j > i) sb.Append('-').Append(sorted[j].ToString(CultureInfo.InvariantCulture));
                i = j + 1;
            }
            return sb.ToString();
        }

        /// <summary>crash-*.txt, exception-*.txt, threaddump-*.txt, exit-*-anr.txt, exit-*-tombstone.pb.</summary>
        internal static bool IsEventFile(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (HasShape(name, "crash-", ".txt") || HasShape(name, "exception-", ".txt") || HasShape(name, "threaddump-", ".txt")) return true;
            return HasShape(name, "exit-", "-anr.txt") || HasShape(name, "exit-", "-tombstone.pb");
        }

        private static bool HasShape(string name, string prefix, string suffix)
        {
            return name.Length > prefix.Length + suffix.Length && name.StartsWith(prefix, StringComparison.Ordinal) &&
                   name.EndsWith(suffix, StringComparison.Ordinal);
        }

        /// <summary>One of <see cref="Families"/> (<paramref name="rotated"/> false) or its "name.&lt;digits&gt;" (true).</summary>
        internal static bool IsFamilyFile(string name, out string family, out bool rotated)
        {
            family = null;
            rotated = false;
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string f in Families)
            {
                if (string.Equals(name, f, StringComparison.Ordinal))
                {
                    family = f;
                    return true;
                }
                if (name.Length <= f.Length + 1 || !name.StartsWith(f, StringComparison.Ordinal) || name[f.Length] != '.') continue;
                bool digits = name.Length - f.Length - 1 <= 9;   // an int in the summary
                for (int i = f.Length + 1; i < name.Length && digits; i++) digits = name[i] >= '0' && name[i] <= '9';
                if (!digits) continue;
                family = f;
                rotated = true;
                return true;
            }
            return false;
        }

        /// <summary>Whether an exit trace (ExitReasons) is saved: at or after the cutoff, or always without one.</summary>
        internal static bool KeepsExit(DateTime exitUtc, DateTime? cutoff)
        {
            return cutoff == null || exitUtc >= cutoff.Value;
        }

        /// <summary>What one cleanup did.</summary>
        internal sealed class CleanResult
        {
            public CleanResult()
            {
                Deleted = new List<string>();
            }

            public List<string> Deleted { get; private set; }

            public long Bytes { get; set; }

            public int Failed { get; private set; }

            /// <summary>"Type: message" of the first failure, or null.</summary>
            public string FirstError { get; private set; }

            /// <summary>The time budget ran out with files left.</summary>
            public bool Stopped { get; set; }

            /// <summary>The bootstrap.log line (without the "[logs]" category).</summary>
            public string Line { get; set; }

            public void Fail(Exception ex)
            {
                Failed++;
                if (FirstError == null && ex != null) FirstError = ex.GetType().Name + ": " + ex.Message;
            }
        }
    }
}
