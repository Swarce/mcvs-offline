using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace McvsOffline.PcSpike
{
    /// <summary>One sample of the game process.</summary>
    public sealed class TelemetrySample
    {
        public DateTime Time;
        public double ElapsedSeconds;
        public int ProcessId;
        public long PrivateBytes;
        public long VirtualBytes;
        public long PeakVirtualBytes;
        public long WorkingSet;
        public long PeakWorkingSet;
        public long PagedBytes;
        public int Threads;
        public int Handles;
        public string Note;
    }

    /// <summary>The CSV format of telemetry-*.csv (pure; tested on any OS).</summary>
    public static class TelemetryCsv
    {
        public const string Header =
            "time,elapsed_s,pid,private_mb,virtual_mb,peak_virtual_mb,working_set_mb,peak_working_set_mb,paged_mb,threads,handles,note";

        public static string Row(TelemetrySample s)
        {
            var c = CultureInfo.InvariantCulture;
            return string.Join(",",
                s.Time.ToString("yyyy-MM-dd HH:mm:ss", c),
                s.ElapsedSeconds.ToString("0", c),
                s.ProcessId.ToString(c),
                Mb(s.PrivateBytes), Mb(s.VirtualBytes), Mb(s.PeakVirtualBytes), Mb(s.WorkingSet), Mb(s.PeakWorkingSet), Mb(s.PagedBytes),
                s.Threads.ToString(c), s.Handles.ToString(c),
                Quote(s.Note));
        }

        /// <summary>Megabytes with one decimal; empty when the value could not be read (-1).</summary>
        public static string Mb(long bytes)
        {
            return bytes < 0 ? "" : (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);
        }

        public static string Quote(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return text;
            return "\"" + text.Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// Peak private and virtual megabytes in a CSV this class wrote (for summary.txt); false when it has no rows.
        /// The file may still be open in a running 'start' or 'telemetry' (<see cref="TelemetrySampler"/> writes with
        /// FileShare.Read, so a plain File.ReadLines fails on Windows): it is opened with FileShare.ReadWrite, and a file
        /// that still cannot be read gives false with <paramref name="rows"/> = -1 instead of an exception.
        /// </summary>
        public static bool Peaks(string csvFile, out double privateMb, out double virtualMb, out int rows)
        {
            privateMb = 0;
            virtualMb = 0;
            rows = 0;
            try
            {
                using (var stream = new FileStream(csvFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    reader.ReadLine();
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] f = line.Split(',');
                        if (f.Length < 5) continue;
                        double p, v;
                        if (double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out p)) privateMb = Math.Max(privateMb, p);
                        if (double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) virtualMb = Math.Max(virtualMb, v);
                        rows++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                privateMb = 0;
                virtualMb = 0;
                rows = -1;
                return false;
            }
            return rows > 0;
        }
    }

    /// <summary>
    /// WP0 step 3 (pc-port.md §5, risk R2): samples ModernCombatVersus.exe's private bytes and virtual size (and working set,
    /// threads, handles) every few seconds into <c>&lt;data&gt;\telemetry\telemetry-&lt;time&gt;.csv</c>. The process is 32-bit
    /// without LARGE_ADDRESS_AWARE, so the virtual size column is the one to watch against 2048 MB. Notes typed in the
    /// console ("menu", "match start") land in the next row. On Windows it also records, every 30 s, the listening TCP and
    /// the UDP endpoints the game owns (<c>netstat -ano</c>) into <c>sockets-&lt;time&gt;.txt</c> (risk R7, unknown 8: does a GS/ENet
    /// socket bind a non-loopback address?).
    /// </summary>
    public sealed class TelemetrySampler : IDisposable
    {
        public const string ProcessName = "ModernCombatVersus";
        public const string FolderName = "telemetry";

        private readonly string folder;
        private readonly TimeSpan interval;
        private readonly IToolLog log;
        private readonly object noteGate = new object();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly HashSet<string> socketLines = new HashSet<string>(StringComparer.Ordinal);
        private Thread thread;
        private string pendingNote;

        public TelemetrySampler(string dataDir, TimeSpan interval, IToolLog log)
        {
            folder = Path.Combine(dataDir, FolderName);
            this.interval = interval;
            this.log = log;
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            CsvPath = Path.Combine(folder, "telemetry-" + stamp + ".csv");
            SocketsPath = Path.Combine(folder, "sockets-" + stamp + ".txt");
        }

        public string CsvPath { get; }

        public string SocketsPath { get; }

        public int Samples { get; private set; }

        /// <summary>True once the game process was seen and then went away (the standalone command stops then).</summary>
        public bool GameExited { get; private set; }

        public void AddNote(string note)
        {
            if (string.IsNullOrWhiteSpace(note)) return;
            lock (noteGate) pendingNote = pendingNote == null ? note.Trim() : pendingNote + " / " + note.Trim();
        }

        public void StartBackground()
        {
            thread = new Thread(() => Run(stop.Token)) { IsBackground = true, Name = "telemetry" };
            thread.Start();
        }

        /// <summary>Samples until <paramref name="token"/> is cancelled.</summary>
        public void Run(CancellationToken token)
        {
            Directory.CreateDirectory(folder);
            DateTime started = DateTime.Now;
            DateTime nextSockets = DateTime.MinValue;
            bool seen = false;
            bool waitingLogged = false;
            using (var writer = new StreamWriter(CsvPath, false, new UTF8Encoding(false)) { AutoFlush = true })
            {
                writer.WriteLine(TelemetryCsv.Header);
                log.Info("telemetry: sampling every " + interval.TotalSeconds + " s into " + CsvPath);
                while (!token.IsCancellationRequested)
                {
                    Process[] processes = Find();
                    if (processes.Length == 0)
                    {
                        if (seen && !GameExited)
                        {
                            GameExited = true;
                            log.Info("telemetry: the game exited");
                        }
                        if (!seen && !waitingLogged)
                        {
                            log.Info("telemetry: waiting for " + ProcessName + ".exe to start...");
                            waitingLogged = true;
                        }
                    }
                    else
                    {
                        if (!seen) log.Info("telemetry: found " + ProcessName + ".exe (pid " + processes[0].Id + ")");
                        seen = true;
                        GameExited = false;
                        string note;
                        lock (noteGate)
                        {
                            note = pendingNote;
                            pendingNote = null;
                        }
                        foreach (Process p in processes)
                        {
                            TelemetrySample sample = Sample(p, started);
                            sample.Note = note;
                            writer.WriteLine(TelemetryCsv.Row(sample));
                            Samples++;
                        }
                        if (OperatingSystem.IsWindows() && DateTime.Now >= nextSockets)
                        {
                            nextSockets = DateTime.Now.AddSeconds(30);
                            RecordSockets(processes.Select(p => p.Id).ToList());
                        }
                    }
                    foreach (Process p in processes) p.Dispose();
                    token.WaitHandle.WaitOne(interval);
                }
            }
        }

        private static Process[] Find()
        {
            try
            {
                return Process.GetProcessesByName(ProcessName);
            }
            catch (Exception)
            {
                return new Process[0];
            }
        }

        private static TelemetrySample Sample(Process p, DateTime started)
        {
            var s = new TelemetrySample { Time = DateTime.Now, ElapsedSeconds = (DateTime.Now - started).TotalSeconds, ProcessId = p.Id };
            try
            {
                p.Refresh();
            }
            catch (Exception)
            {
            }
            s.PrivateBytes = Read(() => p.PrivateMemorySize64);
            s.VirtualBytes = Read(() => p.VirtualMemorySize64);
            s.PeakVirtualBytes = Read(() => p.PeakVirtualMemorySize64);
            s.WorkingSet = Read(() => p.WorkingSet64);
            s.PeakWorkingSet = Read(() => p.PeakWorkingSet64);
            s.PagedBytes = Read(() => p.PagedMemorySize64);
            s.Threads = (int)Read(() => p.Threads.Count);
            s.Handles = (int)Read(() => p.HandleCount);
            return s;
        }

        private static long Read(Func<long> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>Appends the game's new netstat lines (deduplicated, with the time first seen).</summary>
        private void RecordSockets(List<int> pids)
        {
            try
            {
                var info = new ProcessStartInfo("netstat", "-ano")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                string output;
                using (Process netstat = Process.Start(info))
                {
                    output = netstat.StandardOutput.ReadToEnd();
                    netstat.WaitForExit(10000);
                }
                var fresh = new List<string>();
                foreach (string line in SocketLinesOf(output, pids))
                {
                    if (socketLines.Add(line)) fresh.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
                }
                if (fresh.Count > 0) File.AppendAllLines(SocketsPath, fresh);
            }
            catch (Exception ex)
            {
                log.Warn("telemetry: netstat failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The listening TCP sockets and the UDP endpoints in <c>netstat -ano</c> output owned by <paramref name="pids"/>,
        /// whitespace normalised (connections are left out: they come and go with every request). A listener on the
        /// LocalAnubis Finder port 51750 is kept whatever its owner: the Finder is an <c>HttpListener</c>, i.e. http.sys,
        /// which netstat shows as PID 4 (System) (unknown 7).
        /// </summary>
        public static List<string> SocketLinesOf(string netstatOutput, ICollection<int> pids)
        {
            var lines = new List<string>();
            foreach (string raw in netstatOutput.Split('\n'))
            {
                string[] f = raw.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 4) continue;
                if (f[0] != "TCP" && f[0] != "UDP") continue;
                int pid;
                if (!int.TryParse(f[f.Length - 1], NumberStyles.None, CultureInfo.InvariantCulture, out pid)) continue;
                if (!pids.Contains(pid) && !f[1].EndsWith(":51750", StringComparison.Ordinal)) continue;
                // A listening socket has no remote end ("0.0.0.0:0"); the state column ("LISTENING") is translated on some Windows.
                if (f[0] == "TCP" && f[2] != "0.0.0.0:0" && f[2] != "[::]:0") continue;
                lines.Add(string.Join(" ", f));
            }
            return lines;
        }

        public void Dispose()
        {
            stop.Cancel();
            if (thread != null) thread.Join(TimeSpan.FromSeconds(5));
            stop.Dispose();
        }
    }
}
