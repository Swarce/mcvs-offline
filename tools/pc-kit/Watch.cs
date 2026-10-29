using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Patcher.Pc;
using McvsOffline.PcSpike;

namespace McvsOffline.PcKit
{
    /// <summary>
    /// 'watch' (docs/design/pc-port.md §9.2, WR2 steps 4, 11, 12; Q5, Q6): while the game runs, until it exits or Ctrl+C, into
    /// <c>McvsOffline\kit\watch-&lt;stamp&gt;\</c>: memory every 2 s (the spike's <see cref="TelemetrySampler"/>; the game is 32-bit
    /// without LARGE_ADDRESS_AWARE, so the virtual size is shown as a share of 2048 MB), the game's OutputDebugString output
    /// (<see cref="DebugOutputCapture"/>, Windows), and the game's listening TCP and UDP endpoints every 10 s (<c>netstat -ano</c>:
    /// the Havok debugger on 25001+, the Finder on 51750, Lobby/Controller, the GS UDP socket).
    /// </summary>
    public static class Watch
    {
        public const string FolderPrefix = "watch-";
        public const string EndpointsFile = "endpoints.txt";
        public static readonly TimeSpan MemoryInterval = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan EndpointInterval = TimeSpan.FromSeconds(10);
        public const double AddressSpaceMb = 2048;

        public static int Run(KitEnvironment env, string kitFolder, KitLog log)
        {
            string folder = Path.Combine(kitFolder, FolderPrefix + KitServer.Stamp(env.Now()));
            Directory.CreateDirectory(folder);
            log.Info("watch: recording into " + folder + " until the game exits (Ctrl+C stops earlier)");
            using (var stop = new StopSignal())
            using (var telemetry = new TelemetrySampler(folder, MemoryInterval, log))
            {
                DebugOutputCapture capture = OperatingSystem.IsWindows() ? DebugOutputCapture.TryStart(folder, log) : null;
                try
                {
                    telemetry.StartBackground();
                    var previous = new List<string>();
                    bool seen = false;
                    while (!stop.IsRequested)
                    {
                        List<int> pids = GamePids();
                        if (pids.Count > 0)
                        {
                            seen = true;
                            string memory = MemoryLine(pids[0]);
                            if (memory != null) log.Info("watch: " + memory);
                            List<string> endpoints = Endpoints(pids);
                            if (endpoints != null && !endpoints.SequenceEqual(previous))
                            {
                                var text = new StringBuilder();
                                text.Append(env.Now().ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(" (pid ").Append(string.Join(",", pids)).Append(")\r\n");
                                foreach (string e in endpoints) text.Append("  ").Append(e).Append("\r\n");
                                File.AppendAllText(Path.Combine(folder, EndpointsFile), text.ToString(), new UTF8Encoding(false));
                                log.Info("watch: endpoints changed: " + string.Join(" | ", endpoints));
                                previous = endpoints;
                            }
                        }
                        else if (seen)
                        {
                            log.Info("watch: the game exited");
                            break;
                        }
                        stop.Wait((int)EndpointInterval.TotalMilliseconds);
                    }
                }
                finally
                {
                    if (capture != null) capture.Dispose();
                }
                log.Info("watch: " + telemetry.Samples + " memory samples in " + telemetry.CsvPath);
            }
            return PcExitCodes.Ok;
        }

        /// <summary>"private 812.3 MB, virtual 1534.0 MB (75% of 2048 MB)" for the game process, or null.</summary>
        public static string MemoryLine(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return Format(p.PrivateMemorySize64, p.VirtualMemorySize64);
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        public static string Format(long privateBytes, long virtualBytes)
        {
            double virtualMb = virtualBytes / (1024.0 * 1024.0);
            return "private " + TelemetryCsv.Mb(privateBytes) + " MB, virtual " + TelemetryCsv.Mb(virtualBytes) + " MB (" +
                   (virtualMb / AddressSpaceMb * 100).ToString("0", CultureInfo.InvariantCulture) + "% of 2048 MB)";
        }

        private static List<int> GamePids()
        {
            var pids = new List<int>();
            try
            {
                foreach (Process p in Process.GetProcessesByName(GameFolder.ProcessName))
                {
                    using (p) pids.Add(p.Id);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is NotSupportedException || ex is System.ComponentModel.Win32Exception)
            {
            }
            return pids;
        }

        /// <summary>The game's listening TCP sockets and UDP endpoints (and http.sys' 51750), or null when netstat cannot run.</summary>
        private static List<string> Endpoints(List<int> pids)
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                var info = new ProcessStartInfo("netstat", "-ano") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using (Process netstat = Process.Start(info))
                {
                    string output = netstat.StandardOutput.ReadToEnd();
                    netstat.WaitForExit(10000);
                    return TelemetrySampler.SocketLinesOf(output, pids);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
