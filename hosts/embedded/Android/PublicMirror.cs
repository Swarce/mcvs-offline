// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Android only: compiled into the embedded DLL (MCVS_EMBEDDED), never into the net8 build.
#if MCVS_EMBEDDED
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace McvsOffline.Embedded.Android
{
    /// <summary>
    /// Copies of our logs and crash files in Download/MCVS-Offline/, readable on the phone with the Files app (no PC, no
    /// adb): on Android 11 and later other apps cannot open Android/data. The game targets SDK 29 and has no storage
    /// permission, but MediaProvider lets an app create files (any MIME type) and sub-folders in Download/ by direct path;
    /// a file whose database row already exists (left by an earlier install, now owned by nobody) gives EEXIST, so every
    /// session writes its own names: <c>&lt;yyyyMMdd-HHmmss&gt;-bootstrap.log.txt</c>, ... Only Android 11+ (SDK 30); any failure
    /// gives one logcat line and turns the mirror off for the session. Background thread only; the crash path copies its
    /// file synchronously without waiting for the periodic copy.
    /// </summary>
    internal sealed class PublicMirror
    {
        public const string FolderName = "MCVS-Offline";
        private const long TailBytes = 2L * 1024 * 1024;
        private const int KeepSessions = 5;
        private const int IntervalMs = 30000;

        private readonly string[] sources;
        private readonly string sessionPrefix;
        private readonly Dictionary<string, long> copied = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly object gate = new object();
        private readonly Action<string> log;
        private volatile string directory;
        private volatile bool disabled;

        public PublicMirror(string[] sources, Action<string> log)
        {
            this.sources = sources;
            this.log = log ?? (s => { });
            sessionPrefix = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-";
        }

        /// <summary>The folder, once it is known to be writable.</summary>
        public string Directory { get { return directory; } }

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
                CopyTail(path, Path.Combine(directory, Path.GetFileName(path)), 256 * 1024);
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
            try
            {
                int sdk = AndroidDiagnostics.SdkInt();
                if (sdk < 30)
                {
                    log("[mirror] Android API " + sdk.ToString(CultureInfo.InvariantCulture) + " (< 30): no copy of the logs in Download/");
                    disabled = true;
                    return;
                }
                string target = Path.Combine(AndroidDiagnostics.DownloadsDir(), FolderName);
                System.IO.Directory.CreateDirectory(target);
                // Probe: creating a file is the operation that MediaProvider may refuse.
                string probe = Path.Combine(target, sessionPrefix + "session.txt");
                using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    byte[] text = System.Text.Encoding.UTF8.GetBytes("MCVS Offline session " + sessionPrefix.TrimEnd('-') + "\n");
                    fs.Write(text, 0, text.Length);
                }
                directory = target;
                Prune();
                log("[mirror] copies of the logs go to " + target + " (Files app > Downloads > " + FolderName + ")");
            }
            catch (Exception ex)
            {
                disabled = true;
                log("[mirror] Download/" + FolderName + " is not writable, no copy on the phone: " + ex.GetType().Name + ": " + ex.Message);
                return;
            }
            while (!disabled)
            {
                try
                {
                    lock (gate) CopyAll();
                }
                catch (Exception ex)
                {
                    disabled = true;
                    log("[mirror] copy failed, stopping: " + ex.GetType().Name + ": " + ex.Message);
                }
                Thread.Sleep(IntervalMs);
            }
        }

        /// <summary>Caller holds gate.</summary>
        private void CopyAll()
        {
            foreach (string source in sources)
            {
                var info = new FileInfo(source);
                if (!info.Exists) continue;
                long previous;
                if (copied.TryGetValue(source, out previous) && previous == info.Length) continue;
                CopyTail(source, Path.Combine(directory, sessionPrefix + info.Name + ".txt"), TailBytes);
                copied[source] = info.Length;
            }
        }

        /// <summary>File.Copy and File.WriteAllBytes are trimmed from the game's mscorlib: streams only.</summary>
        private static void CopyTail(string source, string destination, long maxBytes)
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                long length = new FileInfo(source).Length;
                if (length > maxBytes) input.Seek(length - maxBytes, SeekOrigin.Begin);
                var buffer = new byte[64 * 1024];
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, n);
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
                if (name.Length > 16 && name[8] == '-' && !sessions.Contains(name.Substring(0, 16))) sessions.Add(name.Substring(0, 16));
            }
            for (int i = 0; i < sessions.Count - KeepSessions; i++)
            {
                foreach (string f in files)
                {
                    if (!Path.GetFileName(f).StartsWith(sessions[i], StringComparison.Ordinal)) continue;
                    try { File.Delete(f); } catch (Exception) { }
                }
            }
        }
    }
}
#endif
