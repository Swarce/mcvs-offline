// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent file logic (compiled into the net8 tests too); the Android parts (SDK level, the Download folder, the
// media scan over JNI) are passed in by AndroidPlatform.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Embedded.Android
{
    /// <summary>
    /// Copies of our logs and crash files in Download/MCVS-Offline/, readable on the phone with the Files app (no PC, no
    /// adb): on Android 11 and later other apps cannot open Android/data. The game targets SDK 29 and has no storage
    /// permission, but MediaProvider lets an app create files (any MIME type) and sub-folders in Download/ by direct path;
    /// a file whose database row already exists (left by an earlier install, now owned by nobody) gives EEXIST, so every
    /// session writes its own names: <c>&lt;yyyyMMdd-HHmmss&gt;-bootstrap.log.txt</c>, ... Only Android 11+ (SDK 30); any failure
    /// gives one log line and turns the mirror off for the session. Background thread only; the crash path copies its
    /// file synchronously without waiting for the periodic copy.
    /// <para>
    /// The 0-byte uploads (design §7.5, D7): a file created through FUSE gets a MediaStore row with <c>IS_PENDING=1</c> and no
    /// <c>_size</c>, closing it tells MediaProvider nothing, and an in-place <c>O_TRUNC</c> rewrite leaves a stale (or 0) size. So
    /// every copy is written to a new <c>&lt;dest&gt;.&lt;n&gt;.tmp</c>, the old copy is deleted and the temp file renamed over it
    /// (libc rename), and one media scan per copy cycle (<c>MediaScannerConnection.scanFile</c>, no permission needed) clears
    /// <c>IS_PENDING</c> and records the real size. Besides the current logs it mirrors their rotated <c>.1</c> files (once per
    /// rotation), <c>unknown-endpoints.jsonl</c> (as <c>.jsonl.txt</c>), and this session's <c>threaddump-*</c>,
    /// <c>exception-*</c> and <c>exit-*-anr</c> files (crash and exception files are redacted when written). Every file is copied
    /// again while its size or time changes: a thread dump is written for up to 10 s after it is created and an ANR trace (up to
    /// 8 MB) is streamed in by the exit-reasons thread while the first cycles run, so a copy taken early would stay truncated.
    /// It keeps 5 sessions and at most 20 crash/thread-dump/exception/exit copies, and deletes leftover <c>*.tmp</c> files of a
    /// killed copy.
    /// </para>
    /// </summary>
    internal sealed class PublicMirror
    {
        public const string FolderName = "MCVS-Offline";
        private const long TailBytes = 2L * 1024 * 1024;
        private const long DiagnosticBytes = 1024 * 1024;
        private const long CrashBytes = 256 * 1024;
        private const int KeepSessions = 5;
        public const int KeepDiagnostics = 20;
        private const int IntervalMs = 30000;

        /// <summary>Prefixes of the single-file diagnostics copied under their own names (pruned to <see cref="KeepDiagnostics"/>).</summary>
        private static readonly string[] DiagnosticPrefixes = { "crash-", "threaddump-", "exception-", "exit-" };

        private readonly string[] sources;
        private readonly string logsDir;
        private readonly string sessionPrefix;
        private readonly DateTime sessionStartUtc;
        private readonly Dictionary<string, string> copied = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> changed = new List<string>();
        private readonly HashSet<string> written = new HashSet<string>(StringComparer.Ordinal);
        private readonly object gate = new object();
        private readonly Action<string> log;
        private volatile string directory;
        private volatile bool disabled;
        private volatile string state = "starting";
        private int tempCounter;
        private int copies;
        private int lastCopyTick;

        /// <param name="sources">The current log files (bootstrap.log, server.log, game.log), existing or not.</param>
        /// <param name="logsDir">McvsOffline/logs: rotated files, unknown endpoints, crash and thread-dump files.</param>
        public PublicMirror(string[] sources, string logsDir, Action<string> log)
        {
            this.sources = sources ?? new string[0];
            this.logsDir = logsDir;
            this.log = log ?? (s => { });
            sessionStartUtc = DateTime.UtcNow;
            sessionPrefix = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-";
            SdkInt = () => 0;
            DownloadsDir = () => null;
        }

        /// <summary>Build.VERSION.SDK_INT (the mirror runs on 30 and later).</summary>
        public Func<int> SdkInt { get; set; }

        /// <summary>The public Download folder.</summary>
        public Func<string> DownloadsDir { get; set; }

        /// <summary>Asks the media scanner to (re)index these paths; may be null (no scan).</summary>
        public Action<string[]> Scan { get; set; }

        /// <summary>The folder, once it is known to be writable.</summary>
        public string Directory { get { return directory; } }

        /// <summary>This session's file name prefix ("yyyyMMdd-HHmmss-").</summary>
        public string SessionPrefix { get { return sessionPrefix; } }

        /// <summary>One line for the settings page: "active, 3 files, last copy 12 s ago", "starting", or "off: ...".</summary>
        public string Describe()
        {
            if (directory == null || disabled) return state;
            int last = lastCopyTick;
            string age = last == 0 ? "no copy yet" : "last copy " + ((Environment.TickCount - last) / 1000).ToString(CultureInfo.InvariantCulture) + " s ago";
            return "active, " + copies.ToString(CultureInfo.InvariantCulture) + " files, " + age;
        }

        public void Start()
        {
            var thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "mcvs-mirror";
            thread.Start();
        }

        /// <summary>Crash path: copy one file now, unless the periodic copy holds the lock (then nothing waits).</summary>
        public void CopyCrashFile(string path)
        {
            if (disabled || directory == null || path == null) return;
            bool taken = false;
            try
            {
                Monitor.TryEnter(gate, ref taken);
                if (!taken) return;
                string name = Path.GetFileName(path);
                CopyIfChanged(path, name.EndsWith(".txt", StringComparison.Ordinal) ? name : name + ".txt", CrashBytes);
                CopyAll();
            }
            catch (Exception)
            {
            }
            finally
            {
                if (taken) Monitor.Exit(gate);
            }
        }

        private void Run()
        {
            if (!Open()) return;
            while (!disabled)
            {
                try
                {
                    lock (gate) CopyAll();
                }
                catch (Exception ex)
                {
                    disabled = true;
                    state = "off: copy failed (" + ex.GetType().Name + ")";
                    log("[mirror] copy failed, stopping: " + ex.GetType().Name + ": " + ex.Message);
                }
                Thread.Sleep(IntervalMs);
            }
        }

        /// <summary>Creates the folder and the session probe; false (and the mirror off) when Download/ is not writable.</summary>
        public bool Open()
        {
            try
            {
                int sdk = SdkInt();
                if (sdk < 30)
                {
                    state = "off: Android API " + sdk.ToString(CultureInfo.InvariantCulture) + " (< 30)";
                    log("[mirror] Android API " + sdk.ToString(CultureInfo.InvariantCulture) + " (< 30): no copy of the logs in Download/");
                    disabled = true;
                    return false;
                }
                string downloads = DownloadsDir();
                if (string.IsNullOrEmpty(downloads)) throw new IOException("the Download folder is unknown");
                string target = Path.Combine(downloads, FolderName);
                System.IO.Directory.CreateDirectory(target);
                lock (gate)
                {
                    DeleteLeftoverTemps(target);
                    // Probe: creating a file is the operation that MediaProvider may refuse.
                    WriteThroughTemp(Path.Combine(target, sessionPrefix + "session.txt"),
                                     System.Text.Encoding.UTF8.GetBytes("MCVS Offline session " + sessionPrefix.TrimEnd('-') + "\n"));
                    directory = target;
                    Prune();
                    FlushScan();
                }
                state = "active";
                log("[mirror] copies of the logs go to " + target + " (Files app > Downloads > " + FolderName + ")");
                return true;
            }
            catch (Exception ex)
            {
                disabled = true;
                state = "off: Download/" + FolderName + " is not writable (" + ex.GetType().Name + ")";
                log("[mirror] Download/" + FolderName + " is not writable, no copy on the phone: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>One copy cycle (the caller holds the gate; tests call it directly after <see cref="Open"/>).</summary>
        public void CopyAll()
        {
            if (directory == null) return;
            foreach (string source in sources)
            {
                CopyIfChanged(source, sessionPrefix + Path.GetFileName(source) + ".txt", TailBytes);
                // The rotated file: once per rotation (its size and time change only then); usually the end of the previous session.
                CopyIfChanged(source + ".1", sessionPrefix + Path.GetFileName(source) + ".1.txt", TailBytes);
            }
            if (!string.IsNullOrEmpty(logsDir) && System.IO.Directory.Exists(logsDir))
            {
                CopyIfChanged(Path.Combine(logsDir, "unknown-endpoints.jsonl"), sessionPrefix + "unknown-endpoints.jsonl.txt", TailBytes);
                foreach (string file in System.IO.Directory.GetFiles(logsDir))
                {
                    string name = Path.GetFileName(file);
                    if (!IsSessionDiagnostic(name)) continue;
                    // This session's files only: older ones were mirrored by their own session (and may have been pruned since).
                    if (!copied.ContainsKey(file) && new FileInfo(file).LastWriteTimeUtc < sessionStartUtc.AddMinutes(-10)) continue;
                    // Again while it is being written (size or time changed); a finished file is not copied again, so the copies
                    // the pruning removed stay removed.
                    CopyIfChanged(file, name.EndsWith(".txt", StringComparison.Ordinal) ? name : name + ".txt", DiagnosticBytes);
                }
            }
            if (changed.Count > 0)
            {
                PruneDiagnostics();
                FlushScan();
            }
        }

        /// <summary>threaddump-*.txt, exception-*.txt, exit-*-anr.txt (crash files are copied by the crash path).</summary>
        private static bool IsSessionDiagnostic(string name)
        {
            if (name.EndsWith(".tmp", StringComparison.Ordinal)) return false;
            if (name.StartsWith("threaddump-", StringComparison.Ordinal) || name.StartsWith("exception-", StringComparison.Ordinal))
                return name.EndsWith(".txt", StringComparison.Ordinal);
            return name.StartsWith("exit-", StringComparison.Ordinal) && name.EndsWith("-anr.txt", StringComparison.Ordinal);
        }

        private void CopyIfChanged(string source, string destName, long maxBytes)
        {
            var info = new FileInfo(source);
            if (!info.Exists) return;
            string signature = info.Length.ToString(CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            string previous;
            if (copied.TryGetValue(source, out previous) && previous == signature) return;
            Copy(source, Path.Combine(directory, destName), maxBytes);
            copied[source] = signature;
        }

        /// <summary>The tail of source into destination through a temp file (File.Copy is trimmed from the game's mscorlib: streams only).</summary>
        private void Copy(string source, string destination, long maxBytes)
        {
            byte[] data;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long length = input.Length;
                if (length > maxBytes) input.Seek(length - maxBytes, SeekOrigin.Begin);
                int size = (int)Math.Min(length, maxBytes);
                data = new byte[size];
                int read = 0, n;
                while (read < size && (n = input.Read(data, read, size - read)) > 0) read += n;
                if (read < size)
                {
                    var cut = new byte[read];
                    Array.Copy(data, cut, read);
                    data = cut;
                }
            }
            WriteThroughTemp(destination, data);
        }

        /// <summary>Writes a new temp file, deletes the old copy, renames the temp file into place; queues the path for the scan.</summary>
        private void WriteThroughTemp(string destination, byte[] data)
        {
            string temp = destination + "." + Interlocked.Increment(ref tempCounter).ToString(CultureInfo.InvariantCulture) + ".tmp";
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(data, 0, data.Length);
                }
                if (File.Exists(destination)) File.Delete(destination);
                FileSystem.Rename(temp, destination);
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch (Exception)
                {
                }
                throw;
            }
            if (!changed.Contains(destination)) changed.Add(destination);
            written.Add(destination);
            copies = written.Count;
            lastCopyTick = Environment.TickCount;
            if (lastCopyTick == 0) lastCopyTick = 1;
        }

        /// <summary>One scan request for every path written since the last one.</summary>
        private void FlushScan()
        {
            if (changed.Count == 0) return;
            string[] paths = changed.ToArray();
            changed.Clear();
            Action<string[]> scan = Scan;
            if (scan == null) return;
            try
            {
                scan(paths);
            }
            catch (Exception ex)
            {
                log("[mirror] media scan failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>A killed copy leaves "&lt;name&gt;.&lt;n&gt;.tmp": ours, deleted at the next start.</summary>
        private static void DeleteLeftoverTemps(string target)
        {
            foreach (string f in System.IO.Directory.GetFiles(target, "*.tmp"))
            {
                try { File.Delete(f); } catch (Exception) { }
            }
        }

        /// <summary>Keeps the newest sessions; files from an earlier install are not ours any more and stay.</summary>
        private void Prune()
        {
            var files = new List<string>(System.IO.Directory.GetFiles(directory));
            files.Sort(StringComparer.Ordinal);
            var sessions = new List<string>();
            foreach (string f in files)
            {
                string name = Path.GetFileName(f);
                if (name.Length > 16 && name[8] == '-' && name[15] == '-' && !sessions.Contains(name.Substring(0, 16))) sessions.Add(name.Substring(0, 16));
            }
            for (int i = 0; i < sessions.Count - KeepSessions; i++)
            {
                foreach (string f in files)
                {
                    if (!Path.GetFileName(f).StartsWith(sessions[i], StringComparison.Ordinal)) continue;
                    try { File.Delete(f); } catch (Exception) { }
                }
            }
            PruneDiagnostics();
        }

        /// <summary>At most <see cref="KeepDiagnostics"/> crash/thread-dump/exception/exit copies, the oldest (by time, then name) go.</summary>
        private void PruneDiagnostics()
        {
            var diagnostics = new List<FileInfo>();
            foreach (string f in System.IO.Directory.GetFiles(directory))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                foreach (string prefix in DiagnosticPrefixes)
                {
                    if (name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        diagnostics.Add(new FileInfo(f));
                        break;
                    }
                }
            }
            if (diagnostics.Count <= KeepDiagnostics) return;
            diagnostics.Sort((a, b) =>
            {
                int c = a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            for (int i = 0; i < diagnostics.Count - KeepDiagnostics; i++)
            {
                try { File.Delete(diagnostics[i].FullName); } catch (Exception) { }
            }
        }
    }
}
