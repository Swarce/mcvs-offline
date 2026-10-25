using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Server.Core
{
    /// <summary>
    /// Where problems of the logging machinery itself go: never through a <see cref="Logger"/> (a broken file sink must not
    /// recurse into itself). Hosts install a writer: the embedded host writes straight to logcat, the default is stdout.
    /// Priorities are Android's: 3 debug, 4 info, 5 warn, 6 error, 7 fatal.
    /// </summary>
    public static class SelfLog
    {
        public const int Debug = 3;
        public const int Info = 4;
        public const int Warn = 5;
        public const int Error = 6;
        public const int Fatal = 7;

        private static volatile Action<int, string> writer;

        /// <summary>Replaces the destination (null restores stdout).</summary>
        public static void SetWriter(Action<int, string> destination)
        {
            writer = destination;
        }

        /// <summary>Never throws.</summary>
        public static void Write(int priority, string text)
        {
            try
            {
                Action<int, string> w = writer;
                if (w != null)
                {
                    w(priority, text);
                    return;
                }
                Console.WriteLine("McvsOffline " + text);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Knobs of a <see cref="SafeLogFile"/>.</summary>
    public sealed class SafeLogFileOptions
    {
        public SafeLogFileOptions()
        {
            MaxBytes = 5L * 1024 * 1024;
            MaxFiles = 5;
        }

        /// <summary>Rotation size (name.log -> name.log.1 ... name.log.N).</summary>
        public long MaxBytes { get; set; }

        public int MaxFiles { get; set; }

        /// <summary>Write on the caller's thread (rare, important lines such as unknown endpoints) instead of a writer thread.</summary>
        public bool Synchronous { get; set; }

        /// <summary>After 3 consecutive failures, continue in this directory (e.g. internal storage). Null: no fallback.</summary>
        public string FallbackDirectory { get; set; }

        /// <summary>Report the file's size at start through <see cref="SelfLog"/> (device diagnostics: "N bytes before this run").</summary>
        public bool ReportInitialSize { get; set; }

        /// <summary>Waits between attempts after a failed batch (the last one repeats). Default 1, 5, 30, 60 s.</summary>
        public int[] RetryDelaysMs { get; set; }
    }

    /// <summary>
    /// A log file that cannot silently stop working (device test 1: bootstrap.log and server.log arrived as 0-byte files
    /// while logcat had every line; the old RotatingFile set a "failed" flag on its first exception and never said a word,
    /// and it kept the file open for the whole process, so MTP, which refreshes a file's size only on IN_CLOSE_WRITE,
    /// kept showing a PC the size the file had when it was created: 0).
    /// <list type="bullet">
    /// <item>Callers only enqueue: O(1), no I/O on the caller's thread (the game and UI threads log through this). A
    /// background thread per file writes batches. A 4 MB queue cap drops the oldest lines and says how many.</item>
    /// <item>Every batch is open (OpenOrCreate, FileShare.Read: the mode AtomicFile already proved on the device), seek to
    /// the end, write, flush, <b>close</b>: no descriptor stays open, every observer (MTP included) sees the real size after
    /// each batch, and a crash loses at most the queue (<see cref="FlushNow"/> drains it on the crash path).</item>
    /// <item>Every failure names its stage (mkdir, stat, rotate, open, seek, write, flush, fsync, close) and goes to
    /// <see cref="SelfLog"/> (logcat) with the exception: the first three, then every 50th. The writer retries with a
    /// backoff (1, 5, 30, 60 s) and after 3 consecutive failures continues in <see cref="SafeLogFileOptions.FallbackDirectory"/>.</item>
    /// </list>
    /// Only APIs of the game's trimmed mscorlib: no File.Move (rotation renames through libc), no FileStream.Length, no
    /// Monitor.TryEnter(object, int).
    /// </summary>
    public sealed class SafeLogFile : IDisposable
    {
        private const int MaxQueuedChars = 4 * 1024 * 1024;
        private const int BatchDelayMs = 100;
        private static readonly int[] DefaultBackoffMs = { 1000, 5000, 30000, 60000 };
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private readonly object queueGate = new object();
        private readonly object ioGate = new object();
        private readonly Queue<string> queue = new Queue<string>();
        private readonly SafeLogFileOptions options;
        private readonly string fileName;
        private readonly long maxBytes;
        private readonly int maxFiles;
        private readonly Thread writer;
        private readonly ManualResetEvent writerExited = new ManualResetEvent(false);
        private int queuedChars;
        private long enqueued;     // lines accepted (queueGate)
        private long completed;    // lines written or dropped (queueGate)
        private long droppedUnreported;
        private long droppedTotal;
        private long size = -1;
        private int consecutiveFailures;
        private int reportedFailures;
        private long totalFailures;
        private bool usingFallback;
        private bool justSwitched;
        private volatile bool stopping;
        private volatile string path;
        private volatile string lastError;
        private long bytesWritten;

        public SafeLogFile(string path, SafeLogFileOptions options)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path required", "path");
            this.options = options ?? new SafeLogFileOptions();
            this.path = System.IO.Path.GetFullPath(path);
            fileName = System.IO.Path.GetFileName(this.path);
            maxBytes = Num.Max(4096L, this.options.MaxBytes);
            maxFiles = Num.Max(1, this.options.MaxFiles);
            if (this.options.ReportInitialSize) ReportInitialSize();
            if (!this.options.Synchronous)
            {
                writer = new Thread(Run);
                writer.IsBackground = true;
                writer.Name = "mcvs-log-" + fileName;
                writer.Start();
            }
        }

        /// <summary>The file currently written (changes once if the fallback directory is used).</summary>
        public string Path { get { return path; } }

        // Plain reads: aligned 64-bit loads are atomic on the 64-bit runtimes this runs on, and these are only for reports.
        public long BytesWritten { get { return bytesWritten; } }

        public long Dropped { get { return droppedTotal; } }

        public long Failures { get { return totalFailures; } }

        /// <summary>"stage: Type (0xHRESULT) message" of the last failure, or null.</summary>
        public string LastError { get { return lastError; } }

        public bool UsingFallback { get { return usingFallback; } }

        /// <summary>Queues one line (a newline is added). Never blocks on I/O, never throws.</summary>
        public void AppendLine(string line)
        {
            if (line == null || stopping) return;
            try
            {
                if (options.Synchronous)
                {
                    lock (ioGate) WriteBatchLocked(new List<string> { line }, false);
                    return;
                }
                lock (queueGate)
                {
                    while (queuedChars + line.Length > MaxQueuedChars && queue.Count > 0)
                    {
                        queuedChars -= queue.Dequeue().Length;
                        droppedUnreported++;
                        droppedTotal++;
                        completed++;
                    }
                    queue.Enqueue(line);
                    queuedChars += line.Length;
                    enqueued++;
                    if (queue.Count == 1) Monitor.PulseAll(queueGate);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Waits until every line queued before the call is written (or dropped), at most <paramref name="timeoutMs"/>.
        /// For tests and orderly shutdown; the crash path uses <see cref="FlushNow"/>, which does not wait for the writer.
        /// </summary>
        public bool Flush(int timeoutMs)
        {
            if (options.Synchronous) return true;
            int start = Environment.TickCount;
            try
            {
                lock (queueGate)
                {
                    long target = enqueued;
                    while (completed < target)
                    {
                        int left = timeoutMs - (Environment.TickCount - start);
                        if (left <= 0 || writer == null || !writer.IsAlive) return completed >= target;
                        Monitor.Wait(queueGate, Num.Min(left, 200));
                    }
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Crash path: writes whatever is queued on the calling thread, with fsync. Waits for the writer thread's current batch
        /// at most <paramref name="budgetMs"/> (polling the non-blocking Monitor.TryEnter(object, ref bool), the only TryEnter
        /// the trimmed mscorlib has). Never throws.
        /// </summary>
        public bool FlushNow(int budgetMs)
        {
            int start = Environment.TickCount;
            bool taken = false;
            try
            {
                while (true)
                {
                    Monitor.TryEnter(ioGate, ref taken);
                    if (taken) break;
                    if (Environment.TickCount - start > budgetMs) return false;
                    Thread.Sleep(5);
                }
                List<string> batch = Drain();
                if (batch.Count == 0) return true;
                bool ok = WriteBatchLocked(batch, true);
                lock (queueGate)
                {
                    completed += batch.Count;
                    Monitor.PulseAll(queueGate);
                }
                return ok;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (taken) Monitor.Exit(ioGate);
            }
        }

        /// <summary>One line for status reports and crash files.</summary>
        public string Describe()
        {
            long s = size;
            return fileName + ": " + (s < 0 ? "?" : s.ToString(CultureInfo.InvariantCulture)) + " B, " +
                   BytesWritten.ToString(CultureInfo.InvariantCulture) + " B written this run, " +
                   Dropped.ToString(CultureInfo.InvariantCulture) + " lines dropped, " +
                   Failures.ToString(CultureInfo.InvariantCulture) + " failures" +
                   (lastError == null ? "" : " (last: " + lastError + ")") + (usingFallback ? ", now in " + path : "");
        }

        public void Dispose()
        {
            if (stopping) return;
            stopping = true;
            try
            {
                lock (queueGate) Monitor.PulseAll(queueGate);
                if (writer != null) writerExited.WaitOne(2000, false);   // Thread.Join(int) is trimmed from the game's mscorlib
            }
            catch (Exception)
            {
            }
            FlushNow(500);
        }

        private void ReportInitialSize()
        {
            try
            {
                var info = new FileInfo(path);
                SelfLog.Write(SelfLog.Info, "[logfile] " + path + (info.Exists
                    ? " exists, " + info.Length.ToString(CultureInfo.InvariantCulture) + " bytes before this run"
                    : " does not exist yet"));
            }
            catch (Exception ex)
            {
                SelfLog.Write(SelfLog.Warn, "[logfile] cannot stat " + path + ": " + ex.GetType().FullName + ": " + ex.Message);
            }
        }

        private List<string> Drain()
        {
            var batch = new List<string>();
            lock (queueGate)
            {
                while (queue.Count > 0) batch.Add(queue.Dequeue());
                queuedChars = 0;
            }
            return batch;
        }

        private void Run()
        {
            try
            {
                RunLoop();
            }
            finally
            {
                writerExited.Set();
            }
        }

        private void RunLoop()
        {
            while (true)
            {
                try
                {
                    lock (queueGate)
                    {
                        while (queue.Count == 0 && !stopping) Monitor.Wait(queueGate, 1000);
                        if (queue.Count == 0) return;   // stopping, nothing left
                    }
                    if (!stopping) Thread.Sleep(BatchDelayMs);   // let a burst of lines become one open/write/close
                    List<string> batch = Drain();
                    if (batch.Count == 0) continue;
                    bool ok;
                    lock (ioGate) ok = WriteBatchLocked(batch, false);
                    if (ok)
                    {
                        lock (queueGate)
                        {
                            completed += batch.Count;
                            Monitor.PulseAll(queueGate);
                        }
                        continue;
                    }
                    Requeue(batch);
                    if (stopping) return;
                    if (justSwitched)
                    {
                        justSwitched = false;   // the fallback directory gets its first try at once
                        continue;
                    }
                    int[] backoff = options.RetryDelaysMs != null && options.RetryDelaysMs.Length > 0 ? options.RetryDelaysMs : DefaultBackoffMs;
                    Thread.Sleep(backoff[Num.Min(Num.Max(consecutiveFailures, 1), backoff.Length) - 1]);
                }
                catch (Exception)
                {
                    try { Thread.Sleep(1000); } catch (Exception) { }
                }
            }
        }

        private void Requeue(List<string> batch)
        {
            lock (queueGate)
            {
                var rest = new List<string>(queue);
                queue.Clear();
                queuedChars = 0;
                foreach (string s in batch)
                {
                    queue.Enqueue(s);
                    queuedChars += s.Length;
                }
                foreach (string s in rest)
                {
                    queue.Enqueue(s);
                    queuedChars += s.Length;
                }
                while (queuedChars > MaxQueuedChars && queue.Count > 0)
                {
                    queuedChars -= queue.Dequeue().Length;
                    droppedUnreported++;
                    droppedTotal++;
                    completed++;
                }
                Monitor.PulseAll(queueGate);
            }
        }

        /// <summary>Caller holds ioGate. Never throws; false when the batch could not be written.</summary>
        private bool WriteBatchLocked(List<string> batch, bool fsync)
        {
            string stage = "encode";
            string current = path;
            try
            {
                var sb = new StringBuilder();
                long lost;
                lock (queueGate)
                {
                    lost = droppedUnreported;
                    droppedUnreported = 0;
                }
                if (lost > 0) sb.Append("[logfile] ").Append(lost.ToString(CultureInfo.InvariantCulture)).Append(" lines dropped (queue full)\n");
                foreach (string line in batch) sb.Append(line).Append('\n');
                byte[] bytes = Utf8.GetBytes(sb.ToString());

                stage = "mkdir";
                string dir = System.IO.Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (size < 0)
                {
                    stage = "stat";
                    var info = new FileInfo(current);
                    size = info.Exists ? info.Length : 0;
                }
                if (size > 0 && size + bytes.Length > maxBytes)
                {
                    stage = "rotate";
                    Rotate(current);
                    size = 0;
                }
                stage = "open";
                using (var fs = new FileStream(current, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
                {
                    stage = "seek";
                    fs.Seek(0, SeekOrigin.End);
                    stage = "write";
                    fs.Write(bytes, 0, bytes.Length);
                    stage = "flush";
                    if (fsync) FileSystem.FlushToDisk(fs);
                    else fs.Flush();
                    stage = "close";
                }
                size += bytes.Length;
                bytesWritten += bytes.Length;
                if (consecutiveFailures > 0)
                {
                    SelfLog.Write(SelfLog.Info, "[logfile] " + current + " is writable again after " +
                                                consecutiveFailures.ToString(CultureInfo.InvariantCulture) + " failed attempts");
                }
                consecutiveFailures = 0;
                return true;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                totalFailures++;
                lastError = stage + ": " + ex.GetType().FullName + " (0x" + ex.HResult.ToString("x8", CultureInfo.InvariantCulture) + ") " + ex.Message;
                if (reportedFailures < 3 || consecutiveFailures % 50 == 0)
                {
                    reportedFailures++;
                    SelfLog.Write(SelfLog.Error, "[logfile] cannot write " + current + " at stage '" + stage + "' (attempt " +
                                                 consecutiveFailures.ToString(CultureInfo.InvariantCulture) + "): " + ex);
                }
                if (consecutiveFailures == 3 && !usingFallback && !string.IsNullOrEmpty(options.FallbackDirectory))
                {
                    usingFallback = true;
                    justSwitched = true;
                    path = System.IO.Path.Combine(options.FallbackDirectory, fileName);
                    size = -1;
                    SelfLog.Write(SelfLog.Warn, "[logfile] " + current + " keeps failing; continuing in " + path);
                }
                return false;
            }
        }

        private void Rotate(string current)
        {
            string oldest = current + "." + maxFiles.ToString(CultureInfo.InvariantCulture);
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = maxFiles - 1; i >= 1; i--)
            {
                string from = current + "." + i.ToString(CultureInfo.InvariantCulture);
                if (File.Exists(from)) FileSystem.Rename(from, current + "." + (i + 1).ToString(CultureInfo.InvariantCulture));
            }
            if (File.Exists(current)) FileSystem.Rename(current, current + ".1");
        }
    }
}
