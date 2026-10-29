// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLLs and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace McvsOffline.Embedded.Diagnostics
{
    /// <summary>
    /// What <see cref="GameThreadWatchdog"/>'s stall snapshot says about the threads of this process (docs/design/pc-port.md
    /// §5.2): Linux /proc on Android and in the tests (<see cref="ProcThreadSnapshot"/>), the <c>ProcessThread</c> list on Windows
    /// (<c>McvsOffline.Embedded.Windows.WindowsThreads</c>). Called on the watchdog thread only.
    /// </summary>
    public interface IThreadSnapshot
    {
        /// <summary>The thread's name as the system knows it (Linux comm), or null.</summary>
        string Name(int tid);

        /// <summary>One line for the game thread ("'comm' state S wchan ... syscall ..."). Never throws.</summary>
        string Describe(int tid);

        /// <summary>The heading of <see cref="Busiest"/>'s lines in the snapshot.</summary>
        string BusiestTitle { get; }

        /// <summary>What <see cref="Busiest"/> reads, for the "&lt;source&gt; unreadable: ..." line when it throws.</summary>
        string Source { get; }

        /// <summary>
        /// The busiest threads since the previous call (and the ones the platform flags as blocked), at most
        /// <paramref name="max"/> lines, the most interesting first. May throw when the thread list cannot be read; the
        /// watchdog reports the message.
        /// </summary>
        List<string> Busiest(int gameTid, int max);
    }

    /// <summary>The Linux snapshot over /proc/self/task (<see cref="NativeThreads"/>): what the watchdog always printed.</summary>
    public sealed class ProcThreadSnapshot : IThreadSnapshot
    {
        private readonly Func<Func<string>, string> withDumpable;
        private Dictionary<int, long> lastCpu = new Dictionary<int, long>();

        /// <param name="withDumpable">Runs a /proc read with PR_SET_DUMPABLE raised (the thread dump's helper), or null.</param>
        public ProcThreadSnapshot(Func<Func<string>, string> withDumpable)
        {
            this.withDumpable = withDumpable;
        }

        public string Name(int tid)
        {
            return (NativeThreads.Read("/proc/self/task/" + tid.ToString(CultureInfo.InvariantCulture) + "/comm") ?? "").Trim();
        }

        public string Describe(int tid)
        {
            // /proc/<tid>/syscall is root-owned while the process is not dumpable ("syscall ?" in device test 2): read it with
            // PR_SET_DUMPABLE raised when the thread dump is enabled (it does the same).
            return withDumpable != null ? withDumpable(() => NativeThreads.Describe(tid, true)) : NativeThreads.Describe(tid, true);
        }

        public string BusiestTitle
        {
            get { return "busiest threads since the last snapshot (and every thread in D state)"; }
        }

        public string Source
        {
            get { return "/proc/self/task"; }
        }

        public List<string> Busiest(int gameTid, int max)
        {
            var cpu = new Dictionary<int, long>();
            var lines = new List<KeyValuePair<long, string>>();
            foreach (int id in NativeThreads.Tids())
            {
                string[] f = NativeThreads.StatFields(id);
                long ticks = NativeThreads.CpuTicks(f);
                if (ticks < 0) continue;
                cpu[id] = ticks;
                long previous;
                long delta = lastCpu.TryGetValue(id, out previous) ? ticks - previous : 0;
                bool blocked = f[0] == "D";
                if (delta > 0 || blocked)
                {
                    lines.Add(new KeyValuePair<long, string>(blocked ? McvsOffline.Server.Core.Num.LongMax / 2 : delta,
                        id.ToString(CultureInfo.InvariantCulture) + " " + NativeThreads.Describe(id, blocked) + " +" + delta.ToString(CultureInfo.InvariantCulture) + " ticks"));
                }
            }
            lastCpu = cpu;
            lines.Sort((a, b) => b.Key.CompareTo(a.Key));
            var result = new List<string>();
            for (int i = 0; i < lines.Count && i < max; i++) result.Add(lines[i].Value);
            return result;
        }
    }
}
