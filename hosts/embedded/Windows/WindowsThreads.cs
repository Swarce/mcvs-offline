#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using McvsOffline.Embedded.Diagnostics;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>One thread of the process as <see cref="WindowsThreads"/> sees it.</summary>
    public sealed class ThreadSample
    {
        public int Id { get; set; }

        /// <summary>ProcessThread.ThreadState ("Running", "Wait", ...).</summary>
        public string State { get; set; }

        /// <summary>ProcessThread.WaitReason when the state is Wait ("UserRequest", "Executive", ...), else null.</summary>
        public string WaitReason { get; set; }

        /// <summary>ProcessThread.TotalProcessorTime in milliseconds, or -1 when unreadable.</summary>
        public long CpuMs { get; set; }
    }

    /// <summary>
    /// The watchdog's stall snapshot on PC (docs/design/pc-port.md §5.2, §6.3; [WH] §4.5): there is no /proc on Windows, so the
    /// threads come from <c>Process.GetCurrentProcess().Threads</c>: per thread its id, state, wait reason (when waiting) and the
    /// processor time used since the previous snapshot, the game thread's line first. Thread ids are Win32 ids
    /// (<c>GetCurrentThreadId</c>, which <see cref="NativeThreads.CurrentTid"/> returns on PC). The thread source is injectable
    /// (tests feed fixed samples). Used on the watchdog thread only.
    /// </summary>
    public sealed class WindowsThreads : IThreadSnapshot
    {
        private readonly Func<IList<ThreadSample>> source;
        private Dictionary<int, long> lastCpu = new Dictionary<int, long>();

        /// <param name="source">The thread list; null: the current process's ProcessThread list.</param>
        public WindowsThreads(Func<IList<ThreadSample>> source)
        {
            this.source = source ?? ProcessThreads;
        }

        public string BusiestTitle
        {
            get { return "threads by processor time since the last snapshot (game thread first)"; }
        }

        public string Source
        {
            get { return "the process's thread list"; }
        }

        /// <summary>Windows threads have no kernel name the CLR exposes.</summary>
        public string Name(int tid)
        {
            return null;
        }

        public string Describe(int tid)
        {
            try
            {
                foreach (ThreadSample sample in source())
                {
                    if (sample != null && sample.Id == tid) return State(sample);
                }
                return "not in the process's thread list";
            }
            catch (Exception ex)
            {
                return "unreadable (" + ex.GetType().Name + ")";
            }
        }

        public List<string> Busiest(int gameTid, int max)
        {
            IList<ThreadSample> samples = source();
            var cpu = new Dictionary<int, long>();
            string gameLine = null;
            var lines = new List<KeyValuePair<long, string>>();
            foreach (ThreadSample sample in samples)
            {
                if (sample == null) continue;
                long previous;
                long delta = sample.CpuMs >= 0 && lastCpu.TryGetValue(sample.Id, out previous) ? sample.CpuMs - previous : 0;
                if (sample.CpuMs >= 0) cpu[sample.Id] = sample.CpuMs;
                if (sample.Id == gameTid && gameTid != 0)
                {
                    gameLine = Line(sample, delta) + " (game thread)";
                    continue;
                }
                if (delta > 0) lines.Add(new KeyValuePair<long, string>(delta, Line(sample, delta)));
            }
            lastCpu = cpu;
            lines.Sort((a, b) => b.Key.CompareTo(a.Key));
            var result = new List<string>();
            if (gameLine != null) result.Add(gameLine);
            for (int i = 0; i < lines.Count && result.Count < max; i++) result.Add(lines[i].Value);
            return result;
        }

        /// <summary>"1234 Wait (UserRequest), cpu 5000 ms +120 ms".</summary>
        private static string Line(ThreadSample sample, long delta)
        {
            return sample.Id.ToString(CultureInfo.InvariantCulture) + " " + State(sample) + " +" + delta.ToString(CultureInfo.InvariantCulture) + " ms";
        }

        /// <summary>"Wait (UserRequest), cpu 5000 ms".</summary>
        private static string State(ThreadSample sample)
        {
            string text = sample.State ?? "?";
            if (!string.IsNullOrEmpty(sample.WaitReason)) text += " (" + sample.WaitReason + ")";
            return text + ", cpu " + (sample.CpuMs < 0 ? "?" : sample.CpuMs.ToString(CultureInfo.InvariantCulture) + " ms");
        }

        private static IList<ThreadSample> ProcessThreads()
        {
            var list = new List<ThreadSample>();
            using (Process process = Process.GetCurrentProcess())
            {
                foreach (ProcessThread thread in process.Threads)
                {
                    var sample = new ThreadSample { Id = thread.Id, CpuMs = -1 };
                    try
                    {
                        ThreadState state = thread.ThreadState;
                        sample.State = state.ToString();
                        if (state == ThreadState.Wait) sample.WaitReason = thread.WaitReason.ToString();
                    }
                    catch (Exception)
                    {
                        sample.State = "?";   // the thread ended while the list was read
                    }
                    try
                    {
                        sample.CpuMs = (long)thread.TotalProcessorTime.TotalMilliseconds;
                    }
                    catch (Exception)
                    {
                    }
                    list.Add(sample);
                }
            }
            return list;
        }
    }
}
#endif
