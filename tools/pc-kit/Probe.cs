using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace McvsOffline.PcKit
{
    /// <summary>The outcome of one probe action.</summary>
    public sealed class ProbeOutcome
    {
        public ProbeOutcome(bool ok, string detail, int? code = null)
        {
            Ok = ok;
            Detail = detail;
            Code = code;
        }

        public bool Ok { get; }

        public string Detail { get; }

        /// <summary>A Win32 error code where one exists (HttpListenerException.ErrorCode).</summary>
        public int? Code { get; }
    }

    /// <summary>One display adapter as the registry's display class records it.</summary>
    public sealed class GpuAdapter
    {
        public string Name { get; set; }

        /// <summary>HardwareInformation.qwMemorySize (or MemorySize): the dedicated video memory in bytes; null when absent.</summary>
        public long? DedicatedBytes { get; set; }

        public string DriverVersion { get; set; }

        public string DriverDate { get; set; }
    }

    /// <summary>One display attached to the desktop and its current mode.</summary>
    public sealed class DisplayMode
    {
        /// <summary>\\.\DISPLAY1 and so on.</summary>
        public string Device { get; set; }

        /// <summary>The adapter that drives it (DISPLAY_DEVICE.DeviceString).</summary>
        public string Adapter { get; set; }

        public bool Primary { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        /// <summary>Hz; 0 or 1 is the hardware default.</summary>
        public int RefreshHz { get; set; }
    }

    /// <summary>The machine actions 'probe' takes (real: <see cref="RealProbeActions"/>; tests inject answers).</summary>
    public interface IProbeActions
    {
        /// <summary>HttpListener with <paramref name="prefix"/>: Start, then Stop (http.sys on Windows).</summary>
        ProbeOutcome HttpSys(string prefix);

        /// <summary>null when it cannot be told (not Windows).</summary>
        bool? IsElevated();

        /// <summary>The output of a read-only tool (netsh), or null when it cannot run.</summary>
        string RunTool(string file, string arguments);

        ProbeOutcome TcpBind(IPAddress address, int port);

        /// <summary>Creates and deletes a probe file in <paramref name="folder"/> (and the folder itself when it had to create it).</summary>
        ProbeOutcome FolderWritable(string folder);

        /// <summary>HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full "Release", or null.</summary>
        int? NetFrameworkRelease();

        /// <summary>Names of the running processes (without .exe).</summary>
        IList<string> ProcessNames();

        /// <summary>The display adapters (registry display class), or null when they cannot be read (not Windows).</summary>
        IList<GpuAdapter> GpuAdapters();

        /// <summary>The displays attached to the desktop with their current mode, or null when they cannot be read.</summary>
        IList<DisplayMode> Displays();

        /// <summary>The installed physical memory in bytes (GlobalMemoryStatusEx), or null.</summary>
        long? PhysicalMemoryBytes();
    }

    /// <summary>
    /// 'probe' (docs/design/pc-port.md §9.2, WR2 step 2, Q2): run before the game, as the normal user. Whether http.sys accepts
    /// the LocalAnubis Finder's <c>http://localhost:51750/</c>, whether the kit runs elevated (then the result is not
    /// representative), URL reservations and excluded port ranges naming 51750/51780, the server port, the install folder, the
    /// .NET Framework files and version, Steam, and tools that inject into games; and for the graphics and memory questions
    /// (WR2 F7, F9, Q5) the GPU adapters, the displays' refresh rates, the physical memory and the Windows Firewall rules that
    /// name the game.
    /// </summary>
    public static class Probe
    {
        public const string FinderPrefix = "http://localhost:51750/";
        public const int FinderPort = 51750;
        public const int ServerPort = 51780;

        /// <summary>4.5.2's Release value (the minimum the PC server targets).</summary>
        public const int Release452 = 379893;

        /// <summary>Processes of capture, overlay and monitoring tools that hook into games ([QL] §5); informational.</summary>
        public static readonly IReadOnlyList<string> InjectingTools = new[]
        {
            "obs64", "obs32", "obs", "Discord", "Medal", "NVIDIA Share", "nvsphelper64", "RTSS", "MSIAfterburner", "XSplit.Core", "bdcam",
            "fraps", "Overwolf", "action_x64", "GameBarFTServer", "Streamlabs OBS", "ReShade", "Playnite.DesktopApp",
        };

        /// <summary>Runs every probe and returns the report lines (also written to the kit log by the caller).</summary>
        public static List<string> Run(IProbeActions actions, string gameFolder, string bcl)
        {
            var lines = new List<string>();
            lines.Add("== MCVS Offline probe (run it before starting the game, from a normal, non-administrator prompt)");

            // (1) http.sys, the Finder's prefix.
            ProbeOutcome http = actions.HttpSys(FinderPrefix);
            lines.Add("http.sys " + FinderPrefix + ": " + HttpSysText(http));

            // (2) elevation.
            bool? elevated = actions.IsElevated();
            lines.Add("elevated: " + (elevated == null ? "unknown" : elevated.Value
                ? "YES - this probe is not representative of a normal start: run it from a normal prompt"
                : "no (as the game runs)"));

            // (3) URL reservations naming the Finder port.
            string urlacl = actions.RunTool("netsh", "http show urlacl");
            List<string> acl = LinesNaming(urlacl, FinderPort.ToString(CultureInfo.InvariantCulture));
            lines.Add("urlacl (netsh http show urlacl) naming " + FinderPort + ": " + (urlacl == null ? "netsh not available" : acl.Count == 0 ? "none" : ""));
            foreach (string l in acl) lines.Add("  " + l);

            // (4) the server port, and Windows' excluded port ranges.
            ProbeOutcome bind = actions.TcpBind(IPAddress.Loopback, ServerPort);
            lines.Add("bind 127.0.0.1:" + ServerPort + ": " + (bind.Ok ? "ok" : "FAILED: " + bind.Detail));
            string ranges = actions.RunTool("netsh", "int ipv4 show excludedportrange protocol=tcp");
            if (ranges == null)
            {
                lines.Add("excluded port ranges: netsh not available");
            }
            else
            {
                List<KeyValuePair<int, int>> excluded = ParseExcludedRanges(ranges);
                foreach (int port in new[] { FinderPort, ServerPort })
                {
                    KeyValuePair<int, int> hit = excluded.FirstOrDefault(r => r.Key <= port && port <= r.Value);
                    lines.Add("excluded port range covering " + port + ": " + (hit.Key == 0 && hit.Value == 0 ? "none" : "YES " + hit.Key + "-" + hit.Value + " (Windows reserved it)"));
                }
            }

            // (5) the install folder.
            if (string.IsNullOrEmpty(gameFolder))
            {
                lines.Add("install folder writable: unknown (the game folder was not found)");
            }
            else
            {
                ProbeOutcome writable = actions.FolderWritable(Path.Combine(gameFolder, "McvsOffline"));
                lines.Add("install folder writable (McvsOffline\\): " + (writable.Ok ? "yes" : "NO: " + writable.Detail));
            }

            // (6) the BCL folder the installer reads the game's assemblies with.
            if (string.IsNullOrEmpty(bcl))
            {
                lines.Add(".NET Framework folder: unknown");
            }
            else
            {
                var missing = new[] { "mscorlib.dll", "System.dll", "System.Core.dll" }.Where(f => !File.Exists(Path.Combine(bcl, f))).ToList();
                lines.Add(".NET Framework folder " + bcl + ": " + (missing.Count == 0 ? "mscorlib.dll, System.dll, System.Core.dll present" : "MISSING " + string.Join(", ", missing)));
            }

            // (7) the .NET Framework release.
            int? release = actions.NetFrameworkRelease();
            lines.Add(".NET Framework 4 Release: " + (release == null ? "unknown" : release.Value.ToString(CultureInfo.InvariantCulture) + " (" + ReleaseName(release.Value) + ")" +
                                                    (release.Value < Release452 ? " - older than 4.5.2: the offline mode needs 4.5.2 or newer" : "")));

            // (8) Steam, (9) injecting tools.
            IList<string> processes = actions.ProcessNames() ?? new List<string>();
            lines.Add("Steam running: " + (processes.Any(p => string.Equals(p, "steam", StringComparison.OrdinalIgnoreCase)) ? "yes" : "no (start Steam before the game)"));
            List<string> injectors = InjectingTools.Where(t => processes.Any(p => string.Equals(p, t, StringComparison.OrdinalIgnoreCase))).ToList();
            lines.Add("tools that hook into games: " + (injectors.Count == 0 ? "none seen" : string.Join(", ", injectors) + " (close them if the game misbehaves)"));

            // (10) graphics and memory: which GPU the engine may be looking at (GameOptions_windows.json picks by GPU name and memory).
            IList<GpuAdapter> gpus = actions.GpuAdapters();
            if (gpus == null) lines.Add("GPU adapters: unknown");
            else if (gpus.Count == 0) lines.Add("GPU adapters: none found");
            foreach (GpuAdapter gpu in gpus ?? new List<GpuAdapter>()) lines.Add("GPU adapter: " + DescribeGpu(gpu));
            IList<DisplayMode> displays = actions.Displays();
            if (displays == null) lines.Add("displays: unknown");
            else if (displays.Count == 0) lines.Add("displays: none attached");
            foreach (DisplayMode d in displays ?? new List<DisplayMode>()) lines.Add("display: " + DescribeDisplay(d));
            long? memory = actions.PhysicalMemoryBytes();
            lines.Add("physical memory: " + (memory.HasValue ? Megabytes(memory.Value) + " MB (the game is a 32-bit process: 2 GB of address space whatever the RAM)" : "unknown"));

            // (11) Windows Firewall rules for the game (Q5: the prompt for its listeners).
            string rules = actions.RunTool("netsh", FirewallArguments);
            if (rules == null)
            {
                lines.Add("firewall rules naming " + GameExe + ": netsh not available");
            }
            else
            {
                List<string> blocks = FirewallRulesNaming(rules, GameExe);
                lines.Add("firewall rules naming " + GameExe + ": " + (blocks.Count == 0 ? "none" : blocks.Count.ToString(CultureInfo.InvariantCulture)));
                foreach (string b in blocks) lines.Add("  " + b);
            }
            return lines;
        }

        public const string GameExe = "ModernCombatVersus.exe";

        /// <summary>Every rule with its program (read-only; works as a normal user).</summary>
        public const string FirewallArguments = "advfirewall firewall show rule name=all verbose";

        /// <summary>"Intel(R) HD Graphics 630, dedicated memory 128 MB, driver 27.20.100.8681 (2020-09-22)".</summary>
        public static string DescribeGpu(GpuAdapter gpu)
        {
            return (string.IsNullOrEmpty(gpu.Name) ? "(no name)" : gpu.Name) +
                   ", dedicated memory " + (gpu.DedicatedBytes.HasValue ? Megabytes(gpu.DedicatedBytes.Value) + " MB" : "unknown") +
                   ", driver " + (gpu.DriverVersion ?? "?") + (string.IsNullOrEmpty(gpu.DriverDate) ? "" : " (" + gpu.DriverDate + ")");
        }

        /// <summary>"\\.\DISPLAY1 (primary) 1920x1080 at 60 Hz on NVIDIA GeForce GTX 1060".</summary>
        public static string DescribeDisplay(DisplayMode d)
        {
            return (d.Device ?? "?") + (d.Primary ? " (primary)" : "") + " " + d.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                   d.Height.ToString(CultureInfo.InvariantCulture) + " at " +
                   (d.RefreshHz > 1 ? d.RefreshHz.ToString(CultureInfo.InvariantCulture) + " Hz" : "the hardware default rate") +
                   (string.IsNullOrEmpty(d.Adapter) ? "" : " on " + d.Adapter);
        }

        private static string Megabytes(long bytes)
        {
            return (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The rules of <c>netsh advfirewall firewall show rule name=all verbose</c> whose text names <paramref name="exe"/>, one line
        /// each (the rule's non-empty lines joined with " | "). The rules are the blank-line separated blocks; the labels are
        /// in Windows' language, so they are kept as they are.
        /// </summary>
        public static List<string> FirewallRulesNaming(string output, string exe)
        {
            var result = new List<string>();
            var block = new List<string>();
            foreach (string raw in (output ?? "").Replace("\r\n", "\n").Split('\n').Concat(new[] { "" }))
            {
                string line = raw.Trim();
                if (line.Length > 0)
                {
                    if (!line.All(c => c == '-')) block.Add(Regex.Replace(line, @"\s{2,}", " "));
                    continue;
                }
                if (block.Any(l => l.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0)) result.Add(string.Join(" | ", block));
                block.Clear();
            }
            return result;
        }

        public static string HttpSysText(ProbeOutcome http)
        {
            if (http.Ok) return "ok (Start and Stop succeeded as this user)";
            if (http.Code == 5) return "ACCESS DENIED (HttpListenerException 5: " + http.Detail + ") - the Finder cannot start for this user";
            if (http.Code == 32 || http.Code == 183) return "port in use (is the game running?) (HttpListenerException " + http.Code + ": " + http.Detail + ")";
            return "FAILED" + (http.Code.HasValue ? " (HttpListenerException " + http.Code.Value + ")" : "") + ": " + http.Detail;
        }

        /// <summary>Lines of a tool's output that contain <paramref name="token"/>, trimmed.</summary>
        public static List<string> LinesNaming(string output, string token)
        {
            if (output == null) return new List<string>();
            return output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l.Contains(token)).ToList();
        }

        /// <summary>The "start end" pairs of <c>netsh int ipv4 show excludedportrange</c> (headers and the administered-port marker '*' ignored).</summary>
        public static List<KeyValuePair<int, int>> ParseExcludedRanges(string output)
        {
            var ranges = new List<KeyValuePair<int, int>>();
            foreach (string line in (output ?? "").Split('\n'))
            {
                Match m = Regex.Match(line, @"^\s*(\d+)\s+(\d+)\b");
                if (!m.Success) continue;
                int start = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int end = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                if (start > 0 && end >= start && end <= 65535) ranges.Add(new KeyValuePair<int, int>(start, end));
            }
            return ranges;
        }

        /// <summary>The .NET Framework version a Release value means (the documented minimum values).</summary>
        public static string ReleaseName(int release)
        {
            if (release >= 533320) return "4.8.1";
            if (release >= 528040) return "4.8";
            if (release >= 461808) return "4.7.2";
            if (release >= 461308) return "4.7.1";
            if (release >= 460798) return "4.7";
            if (release >= 394802) return "4.6.2";
            if (release >= 394254) return "4.6.1";
            if (release >= 393295) return "4.6";
            if (release >= Release452) return "4.5.2";
            if (release >= 378675) return "4.5.1";
            return "4.5";
        }
    }

    /// <summary>The probe actions on the player's PC.</summary>
    public sealed class RealProbeActions : IProbeActions
    {
        public ProbeOutcome HttpSys(string prefix)
        {
            HttpListener listener = null;
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();
                listener.Stop();
                return new ProbeOutcome(true, "ok");
            }
            catch (HttpListenerException ex)
            {
                return new ProbeOutcome(false, ex.Message, ex.ErrorCode);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException || ex is InvalidOperationException || ex is ObjectDisposedException)
            {
                return new ProbeOutcome(false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (listener != null)
                {
                    try { listener.Close(); } catch (Exception) { }
                }
            }
        }

        public bool? IsElevated()
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public string RunTool(string file, string arguments)
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                // netsh writes in the console's OEM code page, which Process uses by default: make that code page decodable.
                McvsOffline.PcSpike.AnsiCodePage.RegisterCodePages();
                var info = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using (Process p = Process.Start(info))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    return output;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public ProbeOutcome TcpBind(IPAddress address, int port)
        {
            var listener = new TcpListener(address, port);
            try
            {
                listener.Start();
                return new ProbeOutcome(true, "ok");
            }
            catch (SocketException ex)
            {
                return new ProbeOutcome(false, ex.SocketErrorCode + " (" + ex.Message + "; is the game or another copy of the server running?)", ex.ErrorCode);
            }
            finally
            {
                listener.Stop();
            }
        }

        public ProbeOutcome FolderWritable(string folder)
        {
            bool created = !Directory.Exists(folder);
            string probe = Path.Combine(folder, "probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                return new ProbeOutcome(true, "ok");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new ProbeOutcome(false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try
                {
                    if (File.Exists(probe)) File.Delete(probe);
                    if (created && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                }
                catch (Exception)
                {
                }
            }
        }

        public int? NetFrameworkRelease()
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    object value = key == null ? null : key.GetValue("Release");
                    return value is int ? (int)value : (int?)null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public IList<string> ProcessNames()
        {
            var names = new List<string>();
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    using (p)
                    {
                        try { names.Add(p.ProcessName); } catch (Exception) { }
                    }
                }
            }
            catch (Exception)
            {
            }
            return names;
        }

        /// <summary>HKLM\SYSTEM\CurrentControlSet\Control\Class\{display class}\NNNN: what Device Manager and dxdiag show.</summary>
        public const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        public IList<GpuAdapter> GpuAdapters()
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                var adapters = new List<GpuAdapter>();
                using (var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DisplayClassKey))
                {
                    if (root == null) return null;
                    foreach (string sub in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)).OrderBy(n => n, StringComparer.Ordinal))
                    {
                        try
                        {
                            using (var key = root.OpenSubKey(sub))
                            {
                                string name = key == null ? null : key.GetValue("DriverDesc") as string;
                                if (name == null) continue;
                                adapters.Add(new GpuAdapter
                                {
                                    Name = name,
                                    DedicatedBytes = MemorySize(key.GetValue("HardwareInformation.qwMemorySize")) ?? MemorySize(key.GetValue("HardwareInformation.MemorySize")),
                                    DriverVersion = key.GetValue("DriverVersion") as string,
                                    DriverDate = key.GetValue("DriverDate") as string,
                                });
                            }
                        }
                        catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
                        {
                            // "Properties" and some driver keys are not readable as a normal user.
                        }
                    }
                }
                return adapters;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>A REG_QWORD, REG_DWORD or 4/8-byte REG_BINARY memory size in bytes.</summary>
        internal static long? MemorySize(object value)
        {
            if (value is long) return (long)value;
            if (value is int) return (long)(uint)(int)value;
            var bytes = value as byte[];
            if (bytes != null && bytes.Length == 8) return BitConverter.ToInt64(bytes, 0);
            if (bytes != null && bytes.Length == 4) return BitConverter.ToUInt32(bytes, 0);
            return null;
        }

        public IList<DisplayMode> Displays()
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                var displays = new List<DisplayMode>();
                for (uint i = 0; i < 16; i++)
                {
                    var device = new NativeDisplay.DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<NativeDisplay.DISPLAY_DEVICE>() };
                    if (!NativeDisplay.EnumDisplayDevices(null, i, ref device, 0)) break;
                    if ((device.StateFlags & NativeDisplay.AttachedToDesktop) == 0) continue;
                    var mode = new NativeDisplay.DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<NativeDisplay.DEVMODE>() };
                    if (!NativeDisplay.EnumDisplaySettings(device.DeviceName, NativeDisplay.EnumCurrentSettings, ref mode)) continue;
                    displays.Add(new DisplayMode
                    {
                        Device = device.DeviceName,
                        Adapter = device.DeviceString,
                        Primary = (device.StateFlags & NativeDisplay.PrimaryDevice) != 0,
                        Width = mode.dmPelsWidth,
                        Height = mode.dmPelsHeight,
                        RefreshHz = mode.dmDisplayFrequency,
                    });
                }
                return displays;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public long? PhysicalMemoryBytes()
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                var status = new NativeDisplay.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeDisplay.MEMORYSTATUSEX>() };
                return NativeDisplay.GlobalMemoryStatusEx(ref status) ? (long)status.ullTotalPhys : (long?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>user32/kernel32 declarations for the display modes and the physical memory (Windows only).</summary>
    internal static class NativeDisplay
    {
        public const int EnumCurrentSettings = -1;
        public const int AttachedToDesktop = 0x1;
        public const int PrimaryDevice = 0x4;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        /// <summary>DEVMODEW, display variant of the union.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE displayDevice, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    }
}
