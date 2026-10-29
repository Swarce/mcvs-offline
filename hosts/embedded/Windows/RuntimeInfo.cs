#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL.
#pragma warning disable CA1416   // the registry calls are Windows-only; off Windows they throw and the value is "?"
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>What the "[boot] runtime:" line reports (docs/design/pc-port.md §3.8, [WH] §7.3).</summary>
    public sealed class RuntimeFacts
    {
        public string Clr { get; set; }
        public string MscorlibVersion { get; set; }
        public int? Release { get; set; }
        public string FrameworkDescription { get; set; }
        public bool Is64BitProcess { get; set; }
        public bool? LargeAddressAware { get; set; }
        public string OsBuild { get; set; }
    }

    /// <summary>
    /// The .NET Framework and Windows the game runs on, as one line: <c>runtime: CLR &lt;v&gt;, mscorlib &lt;file version&gt;, Release
    /// &lt;n&gt; (&lt;name&gt;), &lt;FrameworkDescription&gt;; process x86, LAA no; OS &lt;build&gt;</c>. <c>Environment.Version</c> is
    /// 4.0.30319.42000 on every 4.6+ runtime, so the mscorlib file version and the registry <c>Release</c> say which one it is.
    /// <c>RuntimeInformation.FrameworkDescription</c> is read by reflection (4.7.1+; absent in the net452 reference assemblies).
    /// Never throws: what cannot be read is "?".
    /// </summary>
    public static class RuntimeInfo
    {
        /// <summary>The NDP v4 Full "Release" values and their names (the first entry whose value is not above the release wins).</summary>
        private static readonly int[] Releases = { 533320, 528040, 461808, 461308, 460798, 394802, 394254, 393295, 379893, 378675, 378389 };
        private static readonly string[] Names = { "4.8.1", "4.8", "4.7.2", "4.7.1", "4.7", "4.6.2", "4.6.1", "4.6", "4.5.2", "4.5.1", "4.5" };

        /// <summary>"4.8" for 528040 and up to the next entry; "before 4.5" below 378389.</summary>
        public static string ReleaseName(int release)
        {
            for (int i = 0; i < Releases.Length; i++)
            {
                if (release >= Releases[i]) return Names[i];
            }
            return "before 4.5";
        }

        /// <summary>The line without the "[boot] " prefix.</summary>
        public static string Format(RuntimeFacts facts)
        {
            if (facts == null) facts = new RuntimeFacts();
            string release = facts.Release.HasValue
                ? facts.Release.Value.ToString(CultureInfo.InvariantCulture) + " (" + ReleaseName(facts.Release.Value) + ")"
                : "? (unknown)";
            return "runtime: CLR " + (facts.Clr ?? "?") + ", mscorlib " + (facts.MscorlibVersion ?? "?") + ", Release " + release + ", " +
                   (facts.FrameworkDescription ?? "FrameworkDescription unknown (before 4.7.1)") +
                   "; process " + (facts.Is64BitProcess ? "x64" : "x86") +
                   ", LAA " + (facts.LargeAddressAware.HasValue ? (facts.LargeAddressAware.Value ? "yes" : "no") : "?") +
                   "; OS " + (facts.OsBuild ?? "?");
        }

        /// <summary>The address space of the 32-bit game without LAA, the yardstick of the WR2 memory figures.</summary>
        public const long AddressSpaceMb = 2048;

        /// <summary>
        /// "memory: private 1001 MB, virtual 1592 MB (77 % of 2048 MB), GC heap 85 MB" (WR2 F9: the [status] line says how close
        /// the process is to its 2 GB without a 'watch' running).
        /// </summary>
        public static string FormatMemory(long privateBytes, long virtualBytes, long gcBytes)
        {
            long mb = 1024 * 1024;
            long percent = virtualBytes * 100 / (AddressSpaceMb * mb);
            return "memory: private " + (privateBytes / mb).ToString(CultureInfo.InvariantCulture) + " MB, virtual " +
                   (virtualBytes / mb).ToString(CultureInfo.InvariantCulture) + " MB (" + percent.ToString(CultureInfo.InvariantCulture) + " % of " +
                   AddressSpaceMb.ToString(CultureInfo.InvariantCulture) + " MB), GC heap " + (gcBytes / mb).ToString(CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary><see cref="FormatMemory"/> of this process now, or null. Never throws.</summary>
        public static string DescribeMemory()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                    return FormatMemory(process.PrivateMemorySize64, process.VirtualMemorySize64, GC.GetTotalMemory(false));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The facts of this process. Never throws.</summary>
        public static RuntimeFacts Read()
        {
            var facts = new RuntimeFacts();
            Try(() => facts.Clr = Environment.Version.ToString());
            Try(() => facts.MscorlibVersion = FileVersionInfo.GetVersionInfo(typeof(object).Assembly.Location).ProductVersion);
            Try(() =>
            {
                object value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", null);
                if (value is int) facts.Release = (int)value;
            });
            Try(() =>
            {
                Type info = Type.GetType("System.Runtime.InteropServices.RuntimeInformation, mscorlib", false) ??
                            Type.GetType("System.Runtime.InteropServices.RuntimeInformation", false);
                PropertyInfo p = info == null ? null : info.GetProperty("FrameworkDescription", BindingFlags.Public | BindingFlags.Static);
                if (p != null) facts.FrameworkDescription = p.GetValue(null, null) as string;
            });
            Try(() => facts.Is64BitProcess = Environment.Is64BitProcess);
            Try(() =>
            {
                using (Process process = Process.GetCurrentProcess()) facts.LargeAddressAware = IsLargeAddressAware(process.MainModule.FileName);
            });
            Try(() =>
            {
                const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
                object build = Registry.GetValue(key, "CurrentBuildNumber", null);
                object ubr = Registry.GetValue(key, "UBR", null);
                if (build != null) facts.OsBuild = Convert.ToString(build, CultureInfo.InvariantCulture) + (ubr is int ? "." + ((int)ubr).ToString(CultureInfo.InvariantCulture) : "");
            });
            if (facts.OsBuild == null) Try(() => facts.OsBuild = Environment.OSVersion.VersionString);
            return facts;
        }

        /// <summary>
        /// IMAGE_FILE_LARGE_ADDRESS_AWARE (0x0020) in the PE file header of <paramref name="exe"/>; null when the file is not a PE
        /// image or cannot be read.
        /// </summary>
        public static bool? IsLargeAddressAware(string exe)
        {
            try
            {
                using (var stream = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return null;   // "MZ"
                    stream.Position = 0x3C;
                    int peOffset = reader.ReadInt32();
                    if (peOffset <= 0 || peOffset + 24 > stream.Length) return null;
                    stream.Position = peOffset;
                    if (reader.ReadUInt32() != 0x00004550) return null;                    // "PE\0\0"
                    stream.Position = peOffset + 4 + 18;                                    // FileHeader.Characteristics
                    return (reader.ReadUInt16() & 0x0020) != 0;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Try(Action read)
        {
            try
            {
                read();
            }
            catch (Exception)
            {
            }
        }
    }
}
#endif
