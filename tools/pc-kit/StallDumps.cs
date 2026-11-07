using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using McvsOffline.Patcher.Pc;

namespace McvsOffline.PcKit
{
    /// <summary>
    /// 'watch' on a long stall (docs/design/pc-port.md §9.2, WR2 F4): the host's watchdog writes
    /// <c>[watchdog] STALL: no game-thread work item ran for N ms; ...; pid P</c> to bootstrap.log at 5, 15, 60 and 180 s of one
    /// stall. When a stall reaches <see cref="ThresholdMs"/> the kit takes one full-memory minidump of that game process from
    /// outside (<see cref="MiniDump"/>, the game itself cannot: its own filter's core.dmp of WR2 was 0 bytes) into
    /// <c>%LOCALAPPDATA%\McvsOffline\dumps\</c>: once per stall, at most <see cref="MaxPerWatch"/> per watch and
    /// <see cref="MaxKept"/> in the folder, only with <see cref="MinFreeBytes"/> free. 'collect' lists them with their sizes and
    /// never puts them in its zip (1.5-2 GB each).
    /// </summary>
    public sealed class StallDumps
    {
        public const int ThresholdMs = 60000;
        public const int MaxPerWatch = 3;
        public const int MaxKept = 6;
        public const long MinFreeBytes = 5L * 1024 * 1024 * 1024;
        public const string FolderName = "dumps";
        public const string Extension = ".dmp";

        private static readonly Regex StallLine = new Regex(@"\[watchdog\] STALL: no game-thread work item ran for (\d+) ms(?:.*?; pid (\d+))?", RegexOptions.CultureInvariant);

        private readonly KitEnvironment env;
        private readonly KitLog log;
        private readonly List<LogFollower> logs;
        private readonly Dictionary<int, int> lastLag = new Dictionary<int, int>();   // pid (0: unknown) -> lag of its last STALL line
        private readonly HashSet<int> dumped = new HashSet<int>();                      // pids whose current stall has its dump
        private int written;

        /// <param name="bootstrapLogs">The host's bootstrap.log files to follow (the game folder's and the %TEMP% fallback).</param>
        public StallDumps(KitEnvironment env, IEnumerable<string> bootstrapLogs, KitLog log)
        {
            this.env = env;
            this.log = log;
            logs = bootstrapLogs.Where(p => !string.IsNullOrEmpty(p)).Select(p => new LogFollower(p)).ToList();
        }

        /// <summary>%LOCALAPPDATA%\McvsOffline\dumps (outside the game folder: a Steam verify or uninstall never touches it).</summary>
        public static string Folder(KitEnvironment env)
        {
            string root = string.IsNullOrEmpty(env.LocalAppData) ? env.TempRoot : env.LocalAppData;
            return Path.Combine(root, GameFolder.DataDirName, FolderName);
        }

        /// <summary>The game folder's bootstrap.log and the %TEMP% fallback's (the list status reads too).</summary>
        public static List<string> BootstrapLogs(KitEnvironment env, string game)
        {
            return GameFolder.BootstrapLogs(game, env.TempRoot);
        }

        /// <summary>Dumps written by this watch.</summary>
        public int Written { get { return written; } }

        /// <summary>(lag ms, pid or 0) of a STALL line, or null.</summary>
        public static Tuple<int, int> ParseStall(string line)
        {
            if (line == null) return null;
            Match m = StallLine.Match(line);
            if (!m.Success) return null;
            int lag, pid = 0;
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out lag)) return null;
            if (m.Groups[2].Success) int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out pid);
            return Tuple.Create(lag, pid);
        }

        /// <summary>Reads the new lines of the logs; takes a dump for a stall that just reached the threshold. Never throws.</summary>
        public void Poll(IList<int> gamePids)
        {
            foreach (LogFollower follower in logs)
            {
                List<string> lines;
                try
                {
                    lines = follower.ReadNew();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (string line in lines) Feed(line, gamePids);
            }
        }

        /// <summary>One bootstrap.log line (tests call it directly).</summary>
        public void Feed(string line, IList<int> gamePids)
        {
            if (line.IndexOf("embedded host starting", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("[watchdog] the game thread runs again", StringComparison.Ordinal) >= 0)
            {
                // A new process, or the stall is over: the next STALL line starts a new stall. The recovery line names no pid;
                // with several game processes (a headless helper is not one: one exe per game) all are reset.
                lastLag.Clear();
                dumped.Clear();
                return;
            }
            Tuple<int, int> stall = ParseStall(line);
            if (stall == null) return;
            int lag = stall.Item1, pid = stall.Item2;
            int previous;
            if (lastLag.TryGetValue(pid, out previous) && lag < previous) dumped.Remove(pid);   // a shorter lag: a new stall
            lastLag[pid] = lag;
            if (lag < ThresholdMs || dumped.Contains(pid)) return;
            dumped.Add(pid);
            int target = Target(pid, gamePids);
            if (target == 0)
            {
                log.Info("watch: STALL of " + Seconds(lag) + " but " + (pid == 0 ? "no single game process to dump" : "pid " + pid + " is not a running game process") + "; no dump");
                return;
            }
            TakeDump(target, lag);
        }

        /// <summary>The STALL line's pid when it is a running game; without a pid (an older host) the only running game.</summary>
        private static int Target(int pid, IList<int> gamePids)
        {
            if (gamePids == null || gamePids.Count == 0) return 0;
            if (pid != 0) return gamePids.Contains(pid) ? pid : 0;
            return gamePids.Count == 1 ? gamePids[0] : 0;
        }

        private void TakeDump(int pid, int lagMs)
        {
            string folder = Folder(env);
            if (written >= MaxPerWatch)
            {
                log.Info("watch: STALL of " + Seconds(lagMs) + " in pid " + pid + "; this watch already wrote " + written + " dumps, no more");
                return;
            }
            try
            {
                Directory.CreateDirectory(folder);
                int kept = Directory.GetFiles(folder, "*" + Extension).Length;
                if (kept >= MaxKept)
                {
                    log.Warn("watch: STALL of " + Seconds(lagMs) + " in pid " + pid + "; no dump: " + kept + " dumps are already in " + folder +
                             " (send them with the report, then delete them)");
                    return;
                }
                if (env.WriteDump == null)
                {
                    log.Warn("watch: STALL of " + Seconds(lagMs) + " in pid " + pid + "; this kit cannot write dumps");
                    return;
                }
                long free = env.FreeBytes == null ? -1 : env.FreeBytes(folder);
                if (free >= 0 && free < MinFreeBytes)
                {
                    log.Warn("watch: STALL of " + Seconds(lagMs) + " in pid " + pid + "; no dump: only " + Gb(free) + " free on the drive of " + folder +
                             " (a dump needs about 2 GB, the kit keeps " + Gb(MinFreeBytes) + " free)");
                    return;
                }
                string name = GameFolder.ProcessName + "-" + pid.ToString(CultureInfo.InvariantCulture) + "-" + KitServer.Stamp(env.Now()) +
                              "-stall-" + (lagMs / 1000).ToString(CultureInfo.InvariantCulture) + "s" + Extension;
                string path = Path.Combine(folder, name);
                string partial = path + ".partial";
                log.Info("watch: STALL of " + Seconds(lagMs) + " in pid " + pid + ": writing a full dump to " + path + " (the game pauses while it is written)");
                var clock = Stopwatch.StartNew();
                try
                {
                    long size = env.WriteDump(pid, partial);
                    File.Move(partial, path);
                    written++;
                    log.Info("watch: dump written: " + path + ", " + size.ToString(CultureInfo.InvariantCulture) + " bytes (" + Gb(size) + ") in " +
                             clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s; 'collect' reports it, send it separately");
                }
                catch (Exception ex)
                {
                    TryDelete(partial);
                    log.Warn("watch: the dump of pid " + pid + " failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Warn("watch: no dump: " + folder + ": " + ex.Message);
            }
        }

        /// <summary>The dumps in <see cref="Folder"/>: "path\tsize bytes\ttime" lines for 'collect' (never their contents).</summary>
        public static List<string> Listing(KitEnvironment env)
        {
            var lines = new List<string>();
            string folder = Folder(env);
            if (!Directory.Exists(folder)) return lines;
            try
            {
                foreach (FileInfo f in new DirectoryInfo(folder).GetFiles("*" + Extension).OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    lines.Add(f.FullName + "\t" + f.Length.ToString(CultureInfo.InvariantCulture) + " bytes\t" +
                              f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                lines.Add(folder + "\tunreadable: " + ex.Message);
            }
            return lines;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        private static string Seconds(int ms)
        {
            return (ms / 1000).ToString(CultureInfo.InvariantCulture) + " s";
        }

        private static string Gb(long bytes)
        {
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }
    }

    /// <summary>
    /// Follows a log file another process appends to (shared read): only lines written after the first <see cref="ReadNew"/>
    /// when the file already existed then; from the start of a file that appears later or is replaced (the host renames
    /// bootstrap.log to bootstrap.log.1 at each start: a file that is shorter than what was read, or whose first bytes differ, is
    /// a new one; each start's first line has its own time stamp). A partial last line waits for its end.
    /// </summary>
    public sealed class LogFollower
    {
        private readonly string path;
        private long position = -1;
        private readonly StringBuilder partial = new StringBuilder();
        private Decoder decoder = Encoding.UTF8.GetDecoder();   // a character split between two reads stays whole
        private byte[] head = new byte[0];                       // the file's first bytes (up to HeadBytes) as last seen

        /// <summary>How many first bytes tell one file from the next.</summary>
        public const int HeadBytes = 256;

        public LogFollower(string path)
        {
            this.path = path;
        }

        public string Path { get { return path; } }

        public List<string> ReadNew()
        {
            var lines = new List<string>();
            if (!File.Exists(path))
            {
                if (position < 0) position = 0;   // it appears later: read it from its start
                return lines;
            }
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long length = stream.Length;
                byte[] first = ReadHead(stream, length);
                if (position < 0)
                {
                    position = length;   // what was there before the watch is not news
                    head = first;
                    return lines;
                }
                if (length < position || !SameStart(head, first))
                {
                    position = 0;        // replaced (the host's rotation at a start)
                    partial.Clear();
                    decoder = Encoding.UTF8.GetDecoder();
                }
                head = first;
                if (length == position) return lines;
                stream.Position = position;
                var buffer = new byte[(int)Math.Min(length - position, 4 * 1024 * 1024)];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                position += read;
                var chars = new char[read + 2];   // UTF-8: at most one char per byte, plus one held back
                int count = decoder.GetChars(buffer, 0, read, chars, 0);
                partial.Append(chars, 0, count);
            }
            return Split(lines);
        }

        private static byte[] ReadHead(FileStream stream, long length)
        {
            var bytes = new byte[(int)Math.Min(length, HeadBytes)];
            stream.Position = 0;
            int read = 0;
            while (read < bytes.Length)
            {
                int n = stream.Read(bytes, read, bytes.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < bytes.Length) Array.Resize(ref bytes, read);
            return bytes;
        }

        /// <summary>Whether the shorter of the two heads is a prefix of the other (the file only grew).</summary>
        private static bool SameStart(byte[] before, byte[] now)
        {
            int n = Math.Min(before.Length, now.Length);
            for (int i = 0; i < n; i++)
            {
                if (before[i] != now[i]) return false;
            }
            return true;
        }

        private List<string> Split(List<string> lines)
        {
            string text = partial.ToString();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                lines.Add(text.Substring(start, i - start).TrimEnd('\r'));
                start = i + 1;
            }
            partial.Clear();
            partial.Append(text, start, text.Length - start);
            return lines;
        }
    }
}
