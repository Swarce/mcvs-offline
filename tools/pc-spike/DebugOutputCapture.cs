using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace McvsOffline.PcSpike
{
    /// <summary>
    /// The game's own log on PC. The Steam build has no log file: the engine's <c>DeviceLogger</c> (every non-Debug
    /// <c>Gameloft.Logs</c> entry, the managed exceptions included) goes to native code, and <c>NativeEngine.dll</c> imports
    /// <c>OutputDebugStringA/W</c>. This listener is the classic DBWIN protocol (what DebugView does, no administrator
    /// rights needed for the user's own session): it owns <c>DBWIN_BUFFER</c> and the two events, keeps the lines whose
    /// sender is ModernCombatVersus.exe and writes them to <c>&lt;data&gt;\game-debug\debug-&lt;time&gt;.log</c>. Only one
    /// listener can exist per session: when DebugView or a debugger already listens, it reports that and does nothing.
    /// Windows only; the writer thread never touches the console, so the game never waits on us (OutputDebugString waits
    /// up to 10 s for a listener that is busy).
    /// </summary>
    public sealed class DebugOutputCapture : IDisposable
    {
        public const string FolderName = "game-debug";
        private const int BufferSize = 4096;
        private const long MaxBytes = 512L * 1024 * 1024;

        private readonly EventWaitHandle bufferReady;
        private readonly EventWaitHandle dataReady;
        private readonly MemoryMappedFile buffer;
        private readonly MemoryMappedViewAccessor view;
        private readonly StreamWriter writer;
        private readonly Thread thread;
        private readonly Dictionary<int, bool> isGame = new Dictionary<int, bool>();
        private volatile bool stopping;
        private long written;

        private DebugOutputCapture(EventWaitHandle bufferReady, EventWaitHandle dataReady, MemoryMappedFile buffer, string path)
        {
            this.bufferReady = bufferReady;
            this.dataReady = dataReady;
            this.buffer = buffer;
            view = buffer.CreateViewAccessor(0, BufferSize, MemoryMappedFileAccess.Read);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            writer = new StreamWriter(path, true, new UTF8Encoding(false));
            LogPath = path;
            thread = new Thread(Loop) { IsBackground = true, Name = "dbwin" };
        }

        public string LogPath { get; }

        public long Lines { get; private set; }

        /// <summary>Starts listening; null (with the reason logged) when another listener owns the buffer or it fails.</summary>
        [SupportedOSPlatform("windows")]
        public static DebugOutputCapture TryStart(string dataDir, IToolLog log)
        {
            EventWaitHandle bufferReady = null, dataReady = null;
            MemoryMappedFile buffer = null;
            try
            {
                bool createdBuffer, createdData;
                bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset, "DBWIN_BUFFER_READY", out createdBuffer);
                dataReady = new EventWaitHandle(false, EventResetMode.AutoReset, "DBWIN_DATA_READY", out createdData);
                if (!createdBuffer || !createdData)
                {
                    log.Warn("game log: another debug-output listener is running (DebugView, a debugger?); the game's debug output is not captured. Close it and run 'start' again to capture it.");
                    bufferReady.Dispose();
                    dataReady.Dispose();
                    return null;
                }
                buffer = MemoryMappedFile.CreateOrOpen("DBWIN_BUFFER", BufferSize, MemoryMappedFileAccess.ReadWrite);
                string path = Path.Combine(dataDir, FolderName,
                                           "debug-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
                var capture = new DebugOutputCapture(bufferReady, dataReady, buffer, path);
                capture.thread.Start();
                log.Info("game log: capturing ModernCombatVersus.exe's debug output into " + path);
                return capture;
            }
            catch (Exception ex)
            {
                log.Warn("game log: cannot listen to debug output: " + ex.Message);
                if (buffer != null) buffer.Dispose();
                if (bufferReady != null) bufferReady.Dispose();
                if (dataReady != null) dataReady.Dispose();
                return null;
            }
        }

        private void Loop()
        {
            var bytes = new byte[BufferSize - 4];
            while (!stopping)
            {
                bufferReady.Set();
                if (!dataReady.WaitOne(500))
                {
                    // Idle: put everything on disk, so closing the window loses nothing.
                    try { writer.Flush(); } catch (Exception) { }
                    continue;
                }
                try
                {
                    int pid = view.ReadInt32(0);
                    view.ReadArray(4, bytes, 0, bytes.Length);
                    int length = Array.IndexOf(bytes, (byte)0);
                    if (length < 0) length = bytes.Length;
                    if (!IsGame(pid) || written > MaxBytes) continue;
                    string text = Encoding.UTF8.GetString(bytes, 0, length).TrimEnd('\r', '\n');
                    string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " [" + pid.ToString(CultureInfo.InvariantCulture) + "] " + text;
                    writer.WriteLine(line);
                    written += line.Length + 2;
                    Lines++;
                    if (written > MaxBytes) writer.WriteLine("(capture stopped at " + MaxBytes / (1024 * 1024) + " MB)");
                    if ((Lines & 63) == 0) writer.Flush();
                }
                catch (Exception)
                {
                    // A broken message must not end the capture.
                }
            }
        }

        private bool IsGame(int pid)
        {
            bool game;
            if (isGame.TryGetValue(pid, out game)) return game;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    game = string.Equals(p.ProcessName, TelemetrySampler.ProcessName, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception)
            {
                game = false;
            }
            isGame[pid] = game;
            return game;
        }

        public void Dispose()
        {
            stopping = true;
            thread.Join(TimeSpan.FromSeconds(2));
            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch (Exception)
            {
            }
            view.Dispose();
            buffer.Dispose();
            bufferReady.Dispose();
            dataReady.Dispose();
        }
    }
}
