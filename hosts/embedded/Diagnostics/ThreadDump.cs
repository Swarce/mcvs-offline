// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Linux/Android (libc, libdl, /proc, Mono's exported runtime API). Compiled into the embedded DLL and the net8 tests: without
// Mono (net8 tests, CoreCLR) the managed part says "unavailable" and the native part still runs. Never throws.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace McvsOffline.Embedded.Diagnostics
{
    /// <summary>
    /// Where is a stalled thread? Two independent views, both read-only for the threads they look at:
    /// <list type="number">
    /// <item>Native, no suspension: for every thread its kernel state, wchan and /proc/self/task/tid/syscall (futex address
    /// and timeout, sp, pc; the file is 0400 and owned by root while the process is not dumpable, which is why device test 2
    /// printed "syscall ?", so PR_SET_DUMPABLE is raised for the reads and restored, as bionic's own crash handler does).
    /// For chosen threads a scan of the stack from sp: every word that points into executable memory just after a call
    /// instruction, named lib+offset (dladdr adds the nearest exported symbol) or, for JIT/AOT code, the managed method
    /// (mono_pmip). Memory is read through /proc/self/mem, so a bad address is an EIO, never a SIGSEGV. The stack scan is
    /// for 64-bit processes (arm64, x86_64); an armeabi-v7a install gets the thread table and the managed dump.</item>
    /// <item>Managed: Mono's own thread dump (mono_threads_request_thread_dump). The finalizer thread suspends each managed
    /// thread in turn with the same signal every GC uses, walks its managed frames and prints them with g_fprintf to C
    /// stdout, which on Android is /dev/null; fd 1 points at a file for the duration. Completion is the runtime's own
    /// thread_dump_requested flag going back to 0 (its address is decoded from the 4 instructions of the exported function
    /// and only used when they match exactly), else the file stops growing.</item>
    /// </list>
    /// Each section goes to <c>emit</c> as soon as it is ready, so a step that blocks (a runtime or linker lock held by the
    /// stalled thread) still leaves everything before it in the log. Run it on a thread of its own.
    /// </summary>
    public static class ThreadDump
    {
        private const int MaxScanBytes = 256 * 1024;
        private static int running;

        /// <param name="gameTid">The stalled thread (the watchdog's game thread); scanned first.</param>
        /// <param name="scanTids">More threads to scan (e.g. same comm as the game thread, the Finalizer).</param>
        /// <param name="managedDumpFile">Where Mono's dump is written (null: no managed dump).</param>
        /// <param name="managedTimeoutMs">How long to wait for the finalizer thread to finish it.</param>
        /// <param name="emit">Receives each section (multi-line text).</param>
        public static void Capture(int gameTid, IList<int> scanTids, string managedDumpFile, int managedTimeoutMs, Action<string> emit)
        {
            if (emit == null) return;
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            {
                Emit(emit, "[threaddump] a capture is already running");
                return;
            }
            try
            {
                var scan = new List<int>();
                if (gameTid > 0) scan.Add(gameTid);
                if (scanTids != null) foreach (int t in scanTids) if (t > 0 && !scan.Contains(t)) scan.Add(t);
                NativeView(scan, emit);
                if (managedDumpFile != null) ManagedDump(managedDumpFile, managedTimeoutMs, emit);
            }
            catch (Exception ex)
            {
                Emit(emit, "[threaddump] failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                running = 0;
            }
        }

        /// <summary>
        /// Runs <paramref name="read"/> with PR_SET_DUMPABLE raised and then restored, so this process's /proc/self/task/*/syscall
        /// (owner root while not dumpable) can be read; never throws because of the toggle.
        /// </summary>
        public static T WithDumpable<T>(Func<T> read)
        {
            int was = SafePrctl(PR_GET_DUMPABLE, 0);
            bool raised = was == 0 && SafePrctl(PR_SET_DUMPABLE, 1) == 0;
            try
            {
                return read();
            }
            finally
            {
                if (raised) SafePrctl(PR_SET_DUMPABLE, 0);
            }
        }

        /// <summary>Thread ids whose comm equals <paramref name="comm"/>, plus Mono's "Finalizer" (the managed dump runs on it).</summary>
        public static List<int> Related(int gameTid, string comm)
        {
            var result = new List<int>();
            try
            {
                foreach (int tid in NativeThreads.Tids())
                {
                    if (tid == gameTid) continue;
                    string c = (NativeThreads.Read("/proc/self/task/" + Str(tid) + "/comm") ?? "").Trim();
                    if (c == "Finalizer" || (!string.IsNullOrEmpty(comm) && c == comm)) result.Add(tid);
                }
            }
            catch (Exception)
            {
            }
            return result;
        }

        // ------------------------------------------------------------------------------------------------ native view

        private static void NativeView(List<int> scan, Action<string> emit)
        {
            int was = SafePrctl(PR_GET_DUMPABLE, 0);
            bool raised = was == 0 && SafePrctl(PR_SET_DUMPABLE, 1) == 0;
            SelfMemory memory = null;
            try
            {
                MemoryMap maps = MemoryMap.Read();
                memory = SelfMemory.Open(maps);
                var syscalls = new Dictionary<int, Syscall>();
                var sb = new StringBuilder();
                sb.Append("[threaddump] native view: dumpable ").Append(Str(was)).Append(raised ? " (raised to 1 for the reads)" : "")
                  .Append(", /proc/self/mem ").Append(!memory.Direct ? "open" : "unavailable (errno " + Str(memory.OpenErrno) + "): stacks read directly, futex words skipped").Append(", ").Append(Str(maps.Count))
                  .Append(" mappings, arch ").Append(maps.Arch).Append(", fd 1 -> ").Append(ReadLink("/proc/self/fd/1") ?? "?").Append('\n');
                sb.Append("  tid comm state wchan | syscall (futex: word -> mapping, op, timeout) | pc\n");
                foreach (int tid in NativeThreads.Tids())
                {
                    string dir = "/proc/self/task/" + Str(tid) + "/";
                    string comm = (NativeThreads.Read(dir + "comm") ?? "?").Trim();
                    string[] stat = NativeThreads.StatFields(tid);
                    string wchan = (NativeThreads.Read(dir + "wchan") ?? "?").Trim();
                    Syscall sc = Syscall.Parse(NativeThreads.Read(dir + "syscall"));
                    syscalls[tid] = sc;
                    sb.Append("  ").Append(Str(tid)).Append(" '").Append(comm).Append("' ").Append(stat == null ? "?" : stat[0])
                      .Append(' ').Append(wchan).Append(" | ").Append(sc.Describe(maps, memory)).Append('\n');
                }
                Emit(emit, sb.ToString());

                if (!StackScanSupported(maps.Arch, IntPtr.Size))
                {
                    Emit(emit, "[threaddump] native stacks skipped: the scan reads 64-bit arm64/x86_64 stacks, this process is arch " +
                               maps.Arch + " with " + Str(IntPtr.Size * 8) + "-bit pointers (the managed dump below still runs)");
                    return;
                }
                foreach (int tid in scan)
                {
                    Syscall sc;
                    if (!syscalls.TryGetValue(tid, out sc)) sc = Syscall.Parse(NativeThreads.Read("/proc/self/task/" + Str(tid) + "/syscall"));
                    if (sc.Raw == null) sc.Raw = "unreadable: " + ReadError("/proc/self/task/" + Str(tid) + "/syscall");
                    // Two passes: raw lib+offset first (no locks taken), then names (dladdr takes the linker lock, mono_pmip
                    // may take the domain lock); if the second pass blocks, the first is already in the log.
                    List<Frame> frames = ScanStack(sc, maps, memory, tid == scan[0] ? 64 : 24);
                    Emit(emit, FormatFrames(tid, sc, frames, false));
                    if (frames.Count > 0) Emit(emit, FormatFrames(tid, sc, frames, true));
                }
            }
            catch (Exception ex)
            {
                Emit(emit, "[threaddump] native view failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (memory != null) memory.Dispose();
                if (raised) SafePrctl(PR_SET_DUMPABLE, 0);
            }
        }

        private sealed class Frame
        {
            public long Slot;       // address of the stack word
            public long Value;      // the return address found there
            public bool Chained;    // part of a frame-pointer chain (arm64 {x29, x30} record)
            public MemoryMap.Region Region;
        }

        /// <summary>The stack scan reads 8-byte words and knows the call instructions of arm64 and x86_64 only; on any other
        /// process (armeabi-v7a: 4-byte words, Thumb/ARM calls) it would list garbage, so it is skipped there.</summary>
        public static bool StackScanSupported(string arch, int pointerSize)
        {
            return pointerSize == 8 && (arch == "arm64" || arch == "x86_64");
        }

        private static List<Frame> ScanStack(Syscall sc, MemoryMap maps, SelfMemory memory, int maxFrames)
        {
            var frames = new List<Frame>();
            if (sc.Sp == 0 || memory == null || !StackScanSupported(maps.Arch, IntPtr.Size)) return frames;
            MemoryMap.Region stack = maps.Find(sc.Sp);
            if (stack == null || !stack.Readable || !stack.Writable) return frames;
            long length = stack.End - sc.Sp;
            if (length > MaxScanBytes) length = MaxScanBytes;
            // Only a sleeping thread's stack stays put while it is read (its pages cannot be unmapped under us).
            if (memory.Direct && sc.Nr < 0) return frames;
            byte[] data = memory.ReadStable(sc.Sp & ~7L, (int)length);
            if (data == null) return frames;
            long baseAddr = sc.Sp & ~7L;
            var chainSlots = new HashSet<long>();
            if (maps.Arch == "arm64") MarkFrameChain(data, baseAddr, stack.End, chainSlots);
            for (int i = 0; i + 8 <= data.Length && frames.Count < maxFrames; i += 8)
            {
                long v = BitConverter.ToInt64(data, i);
                if (v == 0) continue;
                MemoryMap.Region r = maps.Find(v);
                if (r == null || !r.Executable) continue;
                if (!IsAfterCall(v, r, maps.Arch, memory)) continue;
                long slot = baseAddr + i;
                frames.Add(new Frame { Slot = slot, Value = v, Region = r, Chained = chainSlots.Contains(slot) });
            }
            return frames;
        }

        /// <summary>arm64 frame records: x29 points at {previous x29, x30}. Follows every chain that starts in the scanned
        /// range and keeps the slots holding x30 (the return addresses) of chains at least 3 records long.</summary>
        private static void MarkFrameChain(byte[] data, long baseAddr, long stackEnd, HashSet<long> slots)
        {
            long limit = baseAddr + data.Length;
            for (int i = 0; i + 16 <= data.Length; i += 8)
            {
                long fp = baseAddr + i;
                var chain = new List<long>();
                long cur = fp;
                for (int depth = 0; depth < 256; depth++)
                {
                    if (cur < baseAddr || cur + 16 > limit || (cur & 15) != 0) break;
                    long next = BitConverter.ToInt64(data, (int)(cur - baseAddr));
                    chain.Add(cur + 8);
                    if (next <= cur || next >= stackEnd) break;
                    cur = next;
                }
                if (chain.Count >= 3)
                {
                    foreach (long s in chain) slots.Add(s);
                    return;   // the lowest chain is the live one
                }
            }
        }

        private static bool IsAfterCall(long ret, MemoryMap.Region r, string arch, SelfMemory memory)
        {
            if (!r.Readable) return true;   // execute-only: cannot check, keep it
            if (arch == "arm64")
            {
                if ((ret & 3) != 0 || ret - 4 < r.Start) return false;
                byte[] b = r.Path != null && !r.Path.StartsWith("[", StringComparison.Ordinal) ? memory.ReadStable(ret - 4, 4) : memory.Read(ret - 4, 4);
                if (b == null && memory.Direct) return true;   // JIT code without /proc/self/mem: cannot check, keep it
                if (b == null) return false;
                uint insn = BitConverter.ToUInt32(b, 0);
                return (insn & 0xFC000000u) == 0x94000000u      // BL imm26
                    || (insn & 0xFFFFFC1Fu) == 0xD63F0000u;     // BLR Xn
            }
            if (arch == "x86_64")
            {
                if (ret - 7 < r.Start) return false;
                byte[] b = r.Path != null && !r.Path.StartsWith("[", StringComparison.Ordinal) ? memory.ReadStable(ret - 7, 7) : memory.Read(ret - 7, 7);
                if (b == null && memory.Direct) return true;
                if (b == null) return false;
                if (b[2] == 0xE8) return true;                                                  // call rel32
                if (b[5] == 0xFF && (b[6] & 0x38) == 0x10 && (b[6] & 0xC0) == 0xC0) return true;  // call reg
                if (b[4] == 0xFF && (b[5] & 0x38) == 0x10) return true;                         // call [reg+disp8] / sib
                if (b[1] == 0xFF && (b[2] & 0x38) == 0x10) return true;                         // call [rip+disp32] / [reg+disp32]
                if (b[3] == 0xFF && (b[4] & 0x38) == 0x10) return true;                         // call [reg+sib+disp8]
                return false;
            }
            return true;
        }

        private static string FormatFrames(int tid, Syscall sc, List<Frame> frames, bool named)
        {
            var sb = new StringBuilder();
            sb.Append("[threaddump] native stack of ").Append(Str(tid)).Append(named ? " (named)" : " (raw)").Append(": ");
            if (sc.Sp == 0) return sb.Append("no sp (syscall file: ").Append(sc.Raw ?? "unreadable").Append(")").ToString();
            sb.Append(Str(frames.Count)).Append(" return addresses after calls, from sp 0x").Append(Hex(sc.Sp)).Append(" up; * = frame-pointer chain")
              .Append(named ? "; only the frames with a name\n" : "\n");
            for (int n = 0; n < frames.Count; n++)
            {
                Frame f = frames[n];
                string name = named ? Name(f) : null;
                if (named && string.IsNullOrEmpty(name)) continue;   // the raw pass already has it
                sb.Append("  #").Append(Str(n)).Append(f.Chained ? " * " : "   ").Append("sp+0x").Append(Hex(f.Slot - sc.Sp)).Append("  ")
                  .Append(f.Region.Describe(f.Value));
                if (named) sb.Append("  ").Append(name);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>The managed method (JIT code in anonymous memory, AOT code in libaot-*.so) or the nearest exported symbol.</summary>
        private static string Name(Frame f)
        {
            try
            {
                // Mono's JIT code is unnamed anonymous memory and AOT code is in libaot-*.so. Nothing else goes to mono_pmip:
                // for an address it does not know it takes the domain lock (ART's "[anon:dalvik-jit-code-cache]" included).
                bool anonCode = f.Region.Path == null && f.Region.Owner == null;
                bool aot = f.Region.Path != null && f.Region.Path.IndexOf("libaot-", StringComparison.Ordinal) >= 0;
                DlInfo info = default(DlInfo);
                bool known = !anonCode && Dl.dladdr(new IntPtr(f.Value - 1), out info) != 0;
                if (known && info.dli_fname != IntPtr.Zero && !aot)
                    aot = (Marshal.PtrToStringAnsi(info.dli_fname) ?? "").IndexOf("libaot-", StringComparison.Ordinal) >= 0;
                if (anonCode || aot)
                {
                    string m = MonoRuntime.Pmip(f.Value - 1);
                    if (m != null) return "[managed]" + m;
                }
                if (known && info.dli_sname != IntPtr.Zero)
                    return Marshal.PtrToStringAnsi(info.dli_sname) + "+0x" + Hex(f.Value - info.dli_saddr.ToInt64());
            }
            catch (Exception ex)
            {
                return "(" + ex.GetType().Name + ")";
            }
            return "";
        }

        // ------------------------------------------------------------------------------------------------ managed view

        private static void ManagedDump(string path, int timeoutMs, Action<string> emit)
        {
            string binding = MonoRuntime.Bind();
            if (binding == null)
            {
                Emit(emit, "[threaddump] managed dump unavailable: " + MonoRuntime.BindError);
                return;
            }
            long flag = MonoRuntime.DumpFlagAddress();
            byte flagBefore = flag != 0 ? Marshal.ReadByte(new IntPtr(flag), 0) : (byte)0xFF;
            string fd1 = ReadLink("/proc/self/fd/1");
            int fd = Libc.open(path, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0x1A4 /* 0644 */);
            if (fd < 0)
            {
                Emit(emit, "[threaddump] managed dump: cannot create " + path + " (errno " + Str(Marshal.GetLastWin32Error()) + ")");
                return;
            }
            int saved = -1;
            bool redirected = false, finished = false;
            long size = 0;
            int started = Environment.TickCount, elapsed = 0;
            try
            {
                saved = Libc.dup(1);
                redirected = Libc.dup2(fd, 1) >= 0;
                if (!redirected)
                {
                    Emit(emit, "[threaddump] managed dump: dup2 failed (errno " + Str(Marshal.GetLastWin32Error()) + ")");
                    return;
                }
                if (flagBefore != 1) MonoRuntime.RequestThreadDump();   // 1: a dump is already pending (e.g. SIGQUIT)
                long lastSize = -1;
                int lastChange = Environment.TickCount;
                while ((elapsed = Environment.TickCount - started) < timeoutMs)
                {
                    Thread.Sleep(50);
                    size = Libc.lseek64(fd, 0, SEEK_CUR);   // shares the offset with fd 1: bytes written so far
                    if (flag != 0)
                    {
                        if (Marshal.ReadByte(new IntPtr(flag), 0) == 0) { finished = true; break; }
                        continue;
                    }
                    if (size != lastSize) { lastSize = size; lastChange = Environment.TickCount; }
                    else if (size > 0 && Environment.TickCount - lastChange > 1500) { finished = true; break; }
                }
            }
            finally
            {
                if (redirected)
                {
                    if (saved >= 0) Libc.dup2(saved, 1);
                    else
                    {
                        int devnull = Libc.open("/dev/null", O_WRONLY | O_CLOEXEC, 0);
                        if (devnull >= 0) { Libc.dup2(devnull, 1); Libc.close(devnull); }
                    }
                }
                if (saved >= 0) Libc.close(saved);
                Libc.close(fd);
            }
            var sb = new StringBuilder();
            sb.Append("[threaddump] Mono managed thread dump via ").Append(binding).Append(": ")
              .Append(finished ? "complete" : "NOT complete (the finalizer thread did not finish it: see its line in the native view)")
              .Append(" after ").Append(Str(elapsed)).Append(" ms, ").Append(Str(size)).Append(" bytes, completion by ")
              .Append(flag != 0 ? "thread_dump_requested @0x" + Hex(flag) : "file size").Append(", fd 1 was ").Append(fd1 ?? "?")
              .Append(", file ").Append(path).Append('\n');
            Emit(emit, sb.ToString());
            string text = null;
            try
            {
                using (var reader = new StreamReader(path)) text = reader.ReadToEnd();
            }
            catch (Exception ex)
            {
                Emit(emit, "[threaddump] cannot read " + path + ": " + ex.Message);
            }
            if (string.IsNullOrEmpty(text)) return;
            // One log entry per thread block ("\n\"name\"\n  at ..."), split further when long.
            var block = new StringBuilder();
            foreach (string line in text.Split(new[] { '\n' }, StringSplitOptions.None))
            {
                if (line.StartsWith("\"", StringComparison.Ordinal) && block.Length > 0)
                {
                    Emit(emit, block.ToString());
                    block.Length = 0;
                }
                if (block.Length + line.Length > 2800)
                {
                    Emit(emit, block.ToString());
                    block.Length = 0;
                }
                if (line.Length > 0) block.Append(block.Length == 0 ? "[threaddump] " : "").Append(line).Append('\n');
            }
            if (block.Length > 0) Emit(emit, block.ToString());
        }

        /// <summary>Mono's exported runtime API, bound by the first library name that resolves: "libmonosgen-2.0.so" (Android:
        /// already loaded, found by soname) or "__Internal" (desktop Mono: the executable; Android: libmonodroid's handle, whose
        /// DT_NEEDED is libmonosgen).</summary>
        private static class MonoRuntime
        {
            private static int bound;   // 0 unknown, 1 libmonosgen, 2 __Internal, -1 none
            public static string BindError = "not bound";

            public static string Bind()
            {
                if (bound == 0)
                {
                    try { Sgen.mono_threads_get_default_stacksize(); bound = 1; }
                    catch (Exception ex1)
                    {
                        try { Internal.mono_threads_get_default_stacksize(); bound = 2; }
                        catch (Exception ex2)
                        {
                            bound = -1;
                            BindError = "libmonosgen-2.0.so: " + ex1.GetType().Name + "; __Internal: " + ex2.GetType().Name;
                        }
                    }
                }
                return bound == 1 ? "libmonosgen-2.0.so" : bound == 2 ? "__Internal" : null;
            }

            public static void RequestThreadDump()
            {
                if (bound == 1) Sgen.mono_threads_request_thread_dump();
                else if (bound == 2) Internal.mono_threads_request_thread_dump();
            }

            public static string Pmip(long ip)
            {
                if (Bind() == null) return null;
                IntPtr s = bound == 1 ? Sgen.mono_pmip(new IntPtr(ip)) : Internal.mono_pmip(new IntPtr(ip));
                if (s == IntPtr.Zero) return null;
                string result = Marshal.PtrToStringAnsi(s);
                if (bound == 1) Sgen.mono_free(s); else Internal.mono_free(s);
                return result;
            }

            /// <summary>
            /// The address of the runtime's thread_dump_requested flag, decoded from the code of mono_threads_request_thread_dump
            /// (Mono 2020-02 arm64: adrp x8, page; mov w9, #1; strb w9, [x8, #off]; b mono_gc_finalize_notify), or 0 when the
            /// code is anything else (other ABI or Mono build). The function's address is the one Mono's P/Invoke resolution
            /// found (mono_lookup_pinvoke_call returns it from the method's cache).
            /// </summary>
            public static long DumpFlagAddress()
            {
                try
                {
                    if (Bind() == null) return 0;
                    Type t = bound == 1 ? typeof(Sgen) : typeof(Internal);
                    MethodInfo m = t.GetMethod("mono_threads_request_thread_dump", BindingFlags.Public | BindingFlags.Static);
                    if (m == null) return 0;
                    IntPtr excClass, excArg;
                    IntPtr fn = bound == 1 ? Sgen.mono_lookup_pinvoke_call(m.MethodHandle.Value, out excClass, out excArg)
                                           : Internal.mono_lookup_pinvoke_call(m.MethodHandle.Value, out excClass, out excArg);
                    if (fn == IntPtr.Zero) return 0;
                    MemoryMap maps = MemoryMap.Read();
                    MemoryMap.Region code = maps.Find(fn.ToInt64());
                    if (code == null || !code.Readable || !code.Executable || maps.Arch != "arm64") return 0;
                    uint w0 = (uint)Marshal.ReadInt32(fn), w1 = (uint)Marshal.ReadInt32(fn, 4),
                         w2 = (uint)Marshal.ReadInt32(fn, 8), w3 = (uint)Marshal.ReadInt32(fn, 12);
                    long flag = DecodeDumpFlag(w0, w1, w2, w3, fn.ToInt64());
                    if (flag == 0) return 0;
                    MemoryMap.Region data = maps.Find(flag);
                    return data != null && data.Readable && data.Writable && !data.Executable ? flag : 0;
                }
                catch (Exception)
                {
                    return 0;
                }
            }

            private static class Sgen
            {
                [DllImport("libmonosgen-2.0.so")] public static extern uint mono_threads_get_default_stacksize();
                [DllImport("libmonosgen-2.0.so")] public static extern void mono_threads_request_thread_dump();
                [DllImport("libmonosgen-2.0.so")] public static extern IntPtr mono_pmip(IntPtr ip);
                [DllImport("libmonosgen-2.0.so")] public static extern void mono_free(IntPtr p);
                [DllImport("libmonosgen-2.0.so")] public static extern IntPtr mono_lookup_pinvoke_call(IntPtr method, out IntPtr excClass, out IntPtr excArg);
            }

            private static class Internal
            {
                [DllImport("__Internal")] public static extern uint mono_threads_get_default_stacksize();
                [DllImport("__Internal")] public static extern void mono_threads_request_thread_dump();
                [DllImport("__Internal")] public static extern IntPtr mono_pmip(IntPtr ip);
                [DllImport("__Internal")] public static extern void mono_free(IntPtr p);
                [DllImport("__Internal")] public static extern IntPtr mono_lookup_pinvoke_call(IntPtr method, out IntPtr excClass, out IntPtr excArg);
            }
        }

        /// <summary>arm64: adrp x8, #page; orr w9, wzr, #1; strb w9, [x8, #imm12]; b #imm26 -> page + imm12, else 0.</summary>
        public static long DecodeDumpFlag(uint w0, uint w1, uint w2, uint w3, long pc)
        {
            if ((w0 & 0x9F00001Fu) != 0x90000008u) return 0;   // ADRP x8
            if (w1 != 0x320003E9u) return 0;                     // ORR w9, wzr, #1
            if ((w2 & 0xFFC003FFu) != 0x39000109u) return 0;   // STRB w9, [x8, #imm12]
            if ((w3 & 0xFC000000u) != 0x14000000u) return 0;   // B
            long imm = (((long)(w0 >> 5) & 0x7FFFF) << 2) | ((w0 >> 29) & 3);
            if ((imm & (1L << 20)) != 0) imm -= 1L << 21;
            return (pc & ~0xFFFL) + (imm << 12) + ((w2 >> 10) & 0xFFF);
        }

        // ------------------------------------------------------------------------------------------------ /proc helpers

        /// <summary>/proc/self/task/tid/syscall: "nr a0 a1 a2 a3 a4 a5 sp pc", "-1 sp pc" (not in a syscall) or "running".</summary>
        private struct Syscall
        {
            public string Raw;
            public long Nr, Sp, Pc;
            public long[] Args;

            public static Syscall Parse(string raw)
            {
                var s = new Syscall { Raw = raw == null ? null : raw.Trim(), Nr = -2 };
                if (string.IsNullOrEmpty(s.Raw) || s.Raw.StartsWith("running", StringComparison.Ordinal)) return s;
                string[] f = s.Raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                long nr;
                if (!long.TryParse(f[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out nr)) return s;
                s.Nr = nr;
                if (nr == -1 && f.Length >= 3) { s.Sp = ParseHex(f[1]); s.Pc = ParseHex(f[2]); return s; }
                if (f.Length < 9) return s;
                s.Args = new long[6];
                for (int i = 0; i < 6; i++) s.Args[i] = ParseHex(f[1 + i]);
                s.Sp = ParseHex(f[7]);
                s.Pc = ParseHex(f[8]);
                return s;
            }

            public string Describe(MemoryMap maps, SelfMemory memory)
            {
                if (Raw == null) return "syscall unreadable";
                if (Nr == -2) return Raw;
                var sb = new StringBuilder();
                bool futex = Args != null && ((maps.Arch == "arm64" && Nr == 98) || (maps.Arch == "x86_64" && Nr == 202));
                if (Nr == -1) sb.Append("not in a syscall");
                else if (futex)
                {
                    long addr = Args[0];
                    int op = (int)Args[1];
                    sb.Append("futex(").Append(maps.Describe(addr));
                    byte[] word = memory != null ? memory.Read(addr, 16) : null;
                    if (word != null)
                        sb.Append(" = [0x").Append(BitConverter.ToUInt32(word, 0).ToString("x", CultureInfo.InvariantCulture)).Append(" 0x")
                          .Append(BitConverter.ToUInt32(word, 4).ToString("x", CultureInfo.InvariantCulture)).Append(" 0x")
                          .Append(BitConverter.ToUInt32(word, 8).ToString("x", CultureInfo.InvariantCulture)).Append(" 0x")
                          .Append(BitConverter.ToUInt32(word, 12).ToString("x", CultureInfo.InvariantCulture)).Append(']');
                    sb.Append(", ").Append(FutexOp(op)).Append(", val 0x").Append(Hex(Args[2])).Append(", timeout ");
                    if (Args[3] == 0) sb.Append("none (untimed wait)");
                    else
                    {
                        byte[] ts = memory != null ? memory.Read(Args[3], 16) : null;
                        if (ts == null) sb.Append("0x").Append(Hex(Args[3]));
                        else sb.Append(Str(BitConverter.ToInt64(ts, 0))).Append('.').Append(BitConverter.ToInt64(ts, 8).ToString("D9", CultureInfo.InvariantCulture))
                               .Append((op & 0x7F) == 9 ? " s (absolute)" : " s");
                    }
                    sb.Append(')');
                }
                else
                {
                    sb.Append("syscall ").Append(Str(Nr));
                    string name = NativeThreads.SyscallName(Str(Nr));
                    if (name != null) sb.Append(" (").Append(name).Append(')');
                }
                if (Pc != 0) sb.Append(" | pc ").Append(maps.Describe(Pc));
                return sb.ToString();
            }

            private static string FutexOp(int op)
            {
                string kind;
                switch (op & 0x7F)
                {
                    case 0: kind = "WAIT"; break;
                    case 9: kind = "WAIT_BITSET"; break;
                    case 6: kind = "LOCK_PI"; break;
                    case 1: kind = "WAKE"; break;
                    default: kind = "op " + Str(op & 0x7F); break;
                }
                return kind + ((op & 0x80) != 0 ? "_PRIVATE" : "") + ((op & 0x100) != 0 ? "|CLOCK_REALTIME" : "");
            }
        }

        /// <summary>/proc/self/maps, with lib+offset names relative to each library's load base (= addresses in readelf).</summary>
        private sealed class MemoryMap
        {
            public sealed class Region
            {
                public long Start, End, Offset, Base;
                public string Perms, Path, Owner;
                public bool Readable { get { return Perms.Length > 0 && Perms[0] == 'r'; } }
                public bool Writable { get { return Perms.Length > 1 && Perms[1] == 'w'; } }
                public bool Executable { get { return Perms.Length > 2 && Perms[2] == 'x'; } }

                public string Describe(long addr)
                {
                    string name = Owner ?? Path;
                    if (name == null) return "0x" + Hex(addr) + " [anon " + Perms + "]";
                    int slash = name.LastIndexOf('/');
                    if (slash >= 0 && !name.StartsWith("[", StringComparison.Ordinal)) name = name.Substring(slash + 1);
                    long b = Owner != null || (Path != null && !Path.StartsWith("[", StringComparison.Ordinal)) ? Base : Start;
                    return name + "+0x" + Hex(addr - b) + (Owner != null ? " (" + (Path ?? "anon") + ")" : "") + " [" + Perms + "]";
                }
            }

            private readonly List<Region> regions = new List<Region>();
            public string Arch = "?";
            public int Count { get { return regions.Count; } }

            public static MemoryMap Read()
            {
                var map = new MemoryMap();
                string text = NativeThreads.Read("/proc/self/maps") ?? "";
                var bases = new Dictionary<string, long>();
                Region previousFile = null;
                foreach (string line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] f = MapsFields(line);
                    if (f == null) continue;
                    int dash = f[0].IndexOf('-');
                    if (dash < 0) continue;
                    var r = new Region
                    {
                        Start = ParseHex(f[0].Substring(0, dash)),
                        End = ParseHex(f[0].Substring(dash + 1)),
                        Perms = f[1],
                        Offset = ParseHex(f[2]),
                        Path = f[5]
                    };
                    if (r.Path != null && r.Path.Length == 0) r.Path = null;
                    bool file = r.Path != null && !r.Path.StartsWith("[", StringComparison.Ordinal);
                    if (file)
                    {
                        long b;
                        if (!bases.TryGetValue(r.Path, out b) || r.Start - r.Offset < b) bases[r.Path] = r.Start - r.Offset;
                        previousFile = r;
                    }
                    else if (previousFile != null && r.Start == previousFile.End && f[1].IndexOf('x') < 0 &&
                             (r.Path == null || r.Path == "[anon:.bss]"))
                    {
                        r.Owner = previousFile.Path;   // a library's .bss right after its data segment
                        previousFile = r;
                        r.Path = r.Path ?? "[anon]";
                    }
                    else previousFile = null;
                    map.regions.Add(r);
                }
                foreach (Region r in map.regions)
                {
                    string key = r.Owner ?? r.Path;
                    long b;
                    if (key != null && bases.TryGetValue(key, out b)) r.Base = b;
                }
                map.Arch = DetectArch(map);
                return map;
            }

            /// <summary>"start-end perms offset dev inode [path]" -> 6 fields (path may contain spaces), or null.</summary>
            private static string[] MapsFields(string line)
            {
                var f = new string[6];
                int pos = 0;
                for (int i = 0; i < 5; i++)
                {
                    while (pos < line.Length && line[pos] == ' ') pos++;
                    int start = pos;
                    while (pos < line.Length && line[pos] != ' ') pos++;
                    if (pos == start) return null;
                    f[i] = line.Substring(start, pos - start);
                }
                f[5] = pos < line.Length ? line.Substring(pos).Trim() : "";
                return f;
            }

            public Region Find(long addr)
            {
                int lo = 0, hi = regions.Count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    Region r = regions[mid];
                    if (addr < r.Start) hi = mid - 1;
                    else if (addr >= r.End) lo = mid + 1;
                    else return r;
                }
                return null;
            }

            public string Describe(long addr)
            {
                Region r = Find(addr);
                return r == null ? "0x" + Hex(addr) + " [unmapped]" : r.Describe(addr);
            }

            /// <summary>e_machine of the first ELF mapped at file offset 0 (libc and friends are always there).</summary>
            private static string DetectArch(MemoryMap map)
            {
                foreach (Region r in map.regions)
                {
                    if (r.Offset != 0 || !r.Readable || r.Path == null || !r.Path.EndsWith(".so", StringComparison.Ordinal)) continue;
                    try
                    {
                        if (Marshal.ReadInt32(new IntPtr(r.Start)) != 0x464C457F) continue;   // "\x7fELF"
                        int machine = Marshal.ReadInt16(new IntPtr(r.Start), 18);
                        return machine == 183 ? "arm64" : machine == 62 ? "x86_64" : machine == 40 ? "arm" : "machine " + Str(machine);
                    }
                    catch (Exception)
                    {
                    }
                }
                return "?";
            }
        }

        /// <summary>Reads this process's memory through /proc/self/mem: an unmapped address is an error, never a fault.</summary>
        /// <para>Without /proc/self/mem (not dumpable, policy) <see cref="ReadStable"/> falls back to plain loads, but only from
        /// memory that cannot go away while we read: the stack of a thread that sleeps, and library code. Everything else
        /// (heap words behind a futex) is then skipped.</para>
        private sealed class SelfMemory : IDisposable
        {
            private int fd = -1;
            private MemoryMap maps;

            public bool Direct { get { return fd < 0; } }
            public int OpenErrno;

            public static SelfMemory Open(MemoryMap maps)
            {
                int fd = Libc.open("/proc/self/mem", O_RDONLY | O_CLOEXEC, 0);
                return new SelfMemory { fd = fd, maps = maps, OpenErrno = fd < 0 ? Marshal.GetLastWin32Error() : 0 };
            }

            /// <summary>For a sleeping thread's stack and for code in a file mapping (direct loads allowed).</summary>
            public byte[] ReadStable(long addr, int count)
            {
                if (fd >= 0) return Read(addr, count);
                if (count <= 0 || addr <= 0) return null;
                MemoryMap.Region r = maps.Find(addr);
                if (r == null || !r.Readable || addr + count > r.End || (r.Path != null && r.Path.StartsWith("[v", StringComparison.Ordinal)))
                    return null;   // [vvar], [vdso], [vectors], [vsyscall]: never
                var buffer = new byte[count];
                Marshal.Copy(new IntPtr(addr), buffer, 0, count);
                return buffer;
            }

            /// <summary>Any address: /proc/self/mem only (an unmapped address is an EIO there).</summary>
            public byte[] Read(long addr, int count)
            {
                if (fd < 0 || count <= 0 || addr <= 0) return null;
                var buffer = new byte[count];
                long n = Libc.pread64(fd, buffer, new IntPtr(count), addr).ToInt64();
                if (n <= 0) return null;
                if (n < count)
                {
                    var shorter = new byte[n];
                    Array.Copy(buffer, shorter, (int)n);
                    return shorter.Length >= 4 ? shorter : null;
                }
                return buffer;
            }

            public void Dispose()
            {
                if (fd >= 0) Libc.close(fd);
                fd = -1;
            }
        }

        // ------------------------------------------------------------------------------------------------ libc

        private const int PR_GET_DUMPABLE = 3, PR_SET_DUMPABLE = 4;
        internal const int O_RDONLY = 0, O_WRONLY = 1, O_CREAT = 0x40, O_TRUNC = 0x200, O_CLOEXEC = 0x80000;   // Linux arm, arm64, x86_64
        internal const int SEEK_SET = 0, SEEK_CUR = 1, SEEK_END = 2;

        private static int SafePrctl(int option, long arg)
        {
            try { return Libc.prctl(option, new IntPtr(arg), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception) { return -1; }
        }

        private static string ReadError(string path)
        {
            try
            {
                using (var reader = new StreamReader(path)) reader.ReadToEnd();
                return "readable now";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + " (" + ex.Message + ")";
            }
        }

        private static string ReadLink(string path)
        {
            try
            {
                var buffer = new byte[512];
                long n = Libc.readlink(path, buffer, new IntPtr(buffer.Length)).ToInt64();
                return n > 0 ? Encoding.UTF8.GetString(buffer, 0, (int)n) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The C prototypes, typed for every ABI the APK ships (arm64-v8a and armeabi-v7a; x86_64 for the tests): size_t and
        /// ssize_t are pointer-sized (IntPtr), and a file offset is only ever passed as off64_t through the *64 entry points,
        /// which bionic (both ABIs) and glibc export. Plain lseek/pread take a 32-bit off_t on armeabi-v7a, where a C# long
        /// lands in a register pair the callee does not read. Internal: the tests (same assembly) call the real bindings.
        /// </summary>
        internal static class Libc
        {
            [DllImport("libc", SetLastError = true)] public static extern int prctl(int option, IntPtr a2, IntPtr a3, IntPtr a4, IntPtr a5);
            [DllImport("libc", SetLastError = true)] public static extern int open(string path, int flags, int mode);
            [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
            [DllImport("libc", SetLastError = true)] public static extern int dup(int fd);
            [DllImport("libc", SetLastError = true)] public static extern int dup2(int oldFd, int newFd);
            /// <summary>off64_t lseek64(int fd, off64_t offset, int whence)</summary>
            [DllImport("libc", SetLastError = true)] public static extern long lseek64(int fd, long offset, int whence);
            /// <summary>ssize_t pread64(int fd, void* buf, size_t count, off64_t offset)</summary>
            [DllImport("libc", SetLastError = true)] public static extern IntPtr pread64(int fd, byte[] buffer, IntPtr count, long offset);
            /// <summary>ssize_t readlink(const char* path, char* buf, size_t size)</summary>
            [DllImport("libc", SetLastError = true)] public static extern IntPtr readlink(string path, byte[] buffer, IntPtr size);
        }

        // C# structs are sequential by default (and StructLayoutAttribute is trimmed from the game's mscorlib).
        private struct DlInfo
        {
            public IntPtr dli_fname, dli_fbase, dli_sname, dli_saddr;
        }

        /// <summary>dladdr: bionic has it in libdl.so (a public NDK library); glibc in libdl.so.2 / libc.so.6.</summary>
        private static class Dl
        {
            private static int which;

            public static int dladdr(IntPtr addr, out DlInfo info)
            {
                if (which == 0)
                {
                    try { int r = Bionic.dladdr(addr, out info); which = 1; return r; } catch (Exception) { which = 2; }
                }
                if (which == 2)
                {
                    try { return Glibc.dladdr(addr, out info); } catch (Exception) { which = -1; }
                }
                if (which == 1) return Bionic.dladdr(addr, out info);
                info = default(DlInfo);
                return 0;
            }

            private static class Bionic { [DllImport("libdl.so")] public static extern int dladdr(IntPtr addr, out DlInfo info); }
            private static class Glibc { [DllImport("libdl.so.2")] public static extern int dladdr(IntPtr addr, out DlInfo info); }
        }

        // ------------------------------------------------------------------------------------------------ small helpers

        private static void Emit(Action<string> emit, string text)
        {
            try { emit(text.TrimEnd('\n')); } catch (Exception) { }
        }

        private static long ParseHex(string s)
        {
            if (s == null) return 0;
            if (s.StartsWith("0x", StringComparison.Ordinal)) s = s.Substring(2);
            long v;
            return long.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static string Hex(long v) { return v.ToString("x", CultureInfo.InvariantCulture); }

        private static string Str(long v) { return v.ToString(CultureInfo.InvariantCulture); }
    }
}
