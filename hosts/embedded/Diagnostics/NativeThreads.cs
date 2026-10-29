// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (Linux /proc and libc): compiled into the embedded DLLs and into the net8 tests. The PC DLL has no
// libc import: CurrentTid is GetCurrentThreadId there, and the /proc readers just find nothing (docs/design/pc-port.md §3.1).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace McvsOffline.Embedded.Diagnostics
{
    /// <summary>
    /// What the kernel says about the threads of this process (/proc/self/task/&lt;tid&gt;/{comm,stat,wchan,syscall}). Reading the
    /// other threads of one's own thread group needs no permission. procfs files report size 0, so they are read to EOF with
    /// a StreamReader (Mono's File.ReadAllBytes would trust the 0). Every method returns null or "?" instead of throwing.
    /// </summary>
    public static class NativeThreads
    {
        private static volatile bool noGettid;

        /// <summary>The calling thread's kernel id, or 0.</summary>
        public static int CurrentTid()
        {
            if (noGettid) return 0;
            try
            {
#if MCVS_WINDOWS
                return (int) McvsOffline.Embedded.Windows.Win32.GetCurrentThreadId();
#else
                return NativeMethods.gettid();
#endif
            }
            catch (Exception)
            {
                noGettid = true;
                return 0;
            }
        }

        public static string Read(string path)
        {
            try
            {
                using (var reader = new StreamReader(path)) return reader.ReadToEnd();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>stat(5) fields after "pid (comm)": [0] state ... [11] utime [12] stime; null if unreadable.</summary>
        public static string[] StatFields(int tid)
        {
            string stat = Read("/proc/self/task/" + tid.ToString(CultureInfo.InvariantCulture) + "/stat");
            if (stat == null) return null;
            int close = stat.LastIndexOf(')');
            if (close < 0 || close + 2 >= stat.Length) return null;
            return stat.Substring(close + 2).Split(' ');
        }

        /// <summary>"'comm' state S wchan futex_wait_queue syscall 98 0x... (futex)".</summary>
        public static string Describe(int tid, bool detail)
        {
            string dir = "/proc/self/task/" + tid.ToString(CultureInfo.InvariantCulture) + "/";
            string comm = (Read(dir + "comm") ?? "?").Trim();
            string[] f = StatFields(tid);
            var sb = new StringBuilder();
            sb.Append('\'').Append(comm).Append("' state ").Append(f == null ? "?" : f[0]);
            if (detail)
            {
                sb.Append(" wchan ").Append((Read(dir + "wchan") ?? "?").Trim());
                string syscall = (Read(dir + "syscall") ?? "?").Trim();
                sb.Append(" syscall ").Append(syscall);
                string name = SyscallName(syscall);
                if (name != null) sb.Append(" (").Append(name).Append(')');
            }
            return sb.ToString();
        }

        /// <summary>The thread ids of this process, or an empty list.</summary>
        public static List<int> Tids()
        {
            var tids = new List<int>();
            try
            {
                foreach (string dir in Directory.GetDirectories("/proc/self/task"))
                {
                    int id;
                    if (int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out id)) tids.Add(id);
                }
            }
            catch (Exception)
            {
            }
            return tids;
        }

        /// <summary>utime + stime in clock ticks, or -1.</summary>
        public static long CpuTicks(string[] statFields)
        {
            if (statFields == null || statFields.Length < 13) return -1;
            long u, s;
            if (!long.TryParse(statFields[11], NumberStyles.None, CultureInfo.InvariantCulture, out u)) return -1;
            if (!long.TryParse(statFields[12], NumberStyles.None, CultureInfo.InvariantCulture, out s)) return -1;
            return u + s;
        }

        /// <summary>arm64 syscall numbers that tell a lock wait from network or I/O ("running" = in user space).</summary>
        public static string SyscallName(string syscallFile)
        {
            if (string.IsNullOrEmpty(syscallFile)) return null;
            if (syscallFile.StartsWith("running", StringComparison.Ordinal)) return "in user space";
            int space = syscallFile.IndexOf(' ');
            string number = space < 0 ? syscallFile : syscallFile.Substring(0, space);
            switch (number)
            {
                case "98": return "futex: waiting for a lock or condition";
                case "73": return "ppoll: waiting for a socket or pipe";
                case "22": return "epoll_pwait: event loop idle";
                case "63": return "read";
                case "64": return "write";
                case "203": return "connect: network";
                case "206": return "sendto: network";
                case "207": return "recvfrom: network";
                case "101": return "nanosleep";
                case "115": return "clock_nanosleep";
                case "29": return "ioctl (binder, GPU)";
                case "56": return "openat";
                case "57": return "close";
                default: return null;
            }
        }

#if MCVS_ANDROID || !MCVS_EMBEDDED
        private static class NativeMethods
        {
            [DllImport("libc", SetLastError = true)]
            public static extern int gettid();
        }
#endif
    }
}
