#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests (which drive it with a fake native layer), never into
// the Android DLL.
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>The Win32 calls of <see cref="NativeCrashFilter"/> (tests replace them).</summary>
    internal interface ICrashFilterNative
    {
        /// <summary>SetUnhandledExceptionFilter: installs <paramref name="filter"/>, returns the previous filter (or zero).</summary>
        IntPtr SetFilter(IntPtr filter);

        uint CurrentThreadId();

        /// <summary>
        /// The file name of the module that contains <paramref name="address"/> into <paramref name="buffer"/> (not
        /// NUL-terminated); its length, or 0 when no module contains it. <paramref name="moduleBase"/> is the module's base.
        /// </summary>
        int ModuleOf(IntPtr address, char[] buffer, out IntPtr moduleBase);

        /// <summary>Creates the NUL-terminated <paramref name="path"/> (never overwrites); a handle, or <see cref="IntPtr.Zero"/>.</summary>
        IntPtr Create(char[] path);

        bool Write(IntPtr file, byte[] data, int count);

        void Flush(IntPtr file);

        void Close(IntPtr file);

        /// <summary>
        /// Install, while the process is healthy: compiles these methods and binds their imports (resolves the entry points and
        /// builds the marshalling stubs), so the crash path does not do it. Throws what the runtime throws.
        /// </summary>
        void Prepare();
    }

    /// <summary>kernel32 (<see cref="Win32"/>): pinned buffers only, nothing on the managed heap once prepared.</summary>
    internal sealed class Win32CrashFilterNative : ICrashFilterNative
    {
        /// <summary>The <see cref="Win32"/> imports these methods call (a test compares the list with their IL).</summary>
        internal static readonly string[] Imports =
        {
            "SetUnhandledExceptionFilter", "GetCurrentThreadId", "GetModuleHandleExW", "GetModuleFileNameW", "CreateFileW", "WriteFile",
            "FlushFileBuffers", "CloseHandle",
        };

        public void Prepare()
        {
            foreach (MethodInfo method in typeof(Win32CrashFilterNative).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
            }
            foreach (string name in Imports)
            {
                Marshal.Prelink(typeof(Win32).GetMethod(name, BindingFlags.Static | BindingFlags.Public));
            }
        }

        public IntPtr SetFilter(IntPtr filter)
        {
            return Win32.SetUnhandledExceptionFilter(filter);
        }

        public uint CurrentThreadId()
        {
            return Win32.GetCurrentThreadId();
        }

        public int ModuleOf(IntPtr address, char[] buffer, out IntPtr moduleBase)
        {
            moduleBase = IntPtr.Zero;
            IntPtr module;
            if (!Win32.GetModuleHandleExW(Win32.ModuleFromAddressNoRef, address, out module) || module == IntPtr.Zero) return 0;
            moduleBase = module;
            uint length = Win32.GetModuleFileNameW(module, buffer, (uint)buffer.Length);
            return length >= buffer.Length ? 0 : (int)length;
        }

        public IntPtr Create(char[] path)
        {
            IntPtr handle = Win32.CreateFileW(path, Win32.GenericWrite, Win32.FileShareRead, IntPtr.Zero, Win32.CreateNew, Win32.FileAttributeNormal, IntPtr.Zero);
            return handle == Win32.InvalidHandle ? IntPtr.Zero : handle;
        }

        public bool Write(IntPtr file, byte[] data, int count)
        {
            uint written;
            return Win32.WriteFile(file, data, (uint)count, out written, IntPtr.Zero) && written == (uint)count;
        }

        public void Flush(IntPtr file)
        {
            Win32.FlushFileBuffers(file);
        }

        public void Close(IntPtr file)
        {
            Win32.CloseHandle(file);
        }
    }

    /// <summary>
    /// Our top-level exception filter on PC (docs/design/pc-port.md §6.4, WR2 F3). ModernCombatVersus.exe installs a filter of
    /// its own in WinMain (<c>SetUnhandledExceptionFilter(0x406e10)</c> at 0x40341c) that writes <c>core.dmp</c> and returns
    /// EXCEPTION_CONTINUE_SEARCH without calling the filter it replaced, the CLR's: so no <c>AppDomain.UnhandledException</c>
    /// runs for a crash on the game thread (a native fault, or a managed exception that unwinds into native code) and
    /// <see cref="CrashReporter"/> saw none of WR2's three crashes. Installed from Bootstrap (after WinMain's call, so it is on
    /// top), it writes <c>logs\crash-&lt;utc&gt;-native.txt</c> (code and name, flags, address with its module and offset,
    /// the access-violation operation and target, the parameters, thread, pid, the game's last Fatal line), then calls the
    /// previous (the game's) filter and returns its result, so core.dmp and Windows Error Reporting behave as before.
    /// <para>
    /// It may run on any thread, in a damaged process (WR2's access violation came from an address-space-tight 32-bit process
    /// whose own MiniDumpWriteDump had failed), and it runs once, so Install compiles its path ahead while the process is
    /// healthy (<see cref="PrepareCrashPath"/>): the filter and its helpers, the kernel32 imports and their stubs, a dry run of
    /// the formatting. The report is then formatted into preallocated buffers and written with CreateFileW/WriteFile through
    /// pinned arrays (no managed allocation), flushed and only then extended with the host's context (boot trace, watchdog;
    /// that part allocates) and followed by a flush of the log files. What managed code cannot compile ahead stays lazy: the
    /// runtime's reverse-P/Invoke stub behind our function pointer (built on its first call), the forward stub of the game's
    /// filter if <see cref="RuntimeHelpers.PrepareDelegate"/> leaves it to the first call, and the dispatch stubs of the
    /// interface calls that only the crash makes (Create, Write, Flush, Close). One report per process (the first crashing
    /// thread); a filter further down that calls back into ours on the same thread gets EXCEPTION_CONTINUE_SEARCH (tracked by
    /// thread id, not a [ThreadStatic], whose storage a thread allocates on first use). The delegate is a static root for the
    /// life of the process (plus a GCHandle), declared WINAPI (stdcall on x86).
    /// </para>
    /// </summary>
    internal static class NativeCrashFilter
    {
        /// <summary>EXCEPTION_CONTINUE_SEARCH.</summary>
        public const int ContinueSearch = 0;

        /// <summary>The CLR's exception code for a managed exception that reaches native code.</summary>
        public const uint ClrException = 0xE0434352;

        public const string FileSuffix = "-native.txt";

        /// <summary>LPTOP_LEVEL_EXCEPTION_FILTER: LONG WINAPI (EXCEPTION_POINTERS*).</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int TopLevelFilter(IntPtr exceptionPointers);

        private static readonly object installGate = new object();
        private static readonly byte[] report = new byte[8192];
        private static readonly char[] path = new char[1024];
        private static readonly char[] module = new char[520];
        private static int length;
        private static int installed;
        private static int reported;
        private static int filterThread;   // the thread id inside the filter (0: none), for re-entry
        private static ICrashFilterNative native;
        private static TopLevelFilter ours;
        private static GCHandle oursHandle;
        private static IntPtr oursPointer;
        private static IntPtr previousPointer;
        private static TopLevelFilter previous;
        private static int processId;
        private static string fallbackDirectory;

        /// <summary>The clock of the report (tests fix it).</summary>
        internal static Func<DateTime> Clock = () => DateTime.UtcNow;

        /// <summary>The previous (the game's) filter, zero when there was none.</summary>
        internal static IntPtr PreviousFilter { get { return previousPointer; } }

        /// <summary>Our filter's function pointer (zero before <see cref="Install"/>).</summary>
        internal static IntPtr FilterPointer { get { return oursPointer; } }

        /// <summary>The report written by this process, or null.</summary>
        internal static string LastReport { get; private set; }

        /// <summary>The first step of <see cref="PrepareCrashPath"/> that failed ("Name: ExceptionType"), or null.</summary>
        internal static string PrepareFailure { get; private set; }

        /// <summary>
        /// Installs the filter once; returns the description for the "[crash] hooks:" line ("native filter ok (previous filter
        /// 0x00406E10)", or "native filter no (...)"). Never throws.
        /// </summary>
        public static string Install(ICrashFilterNative api, int pid, string fallbackDir)
        {
            lock (installGate)
            {
                if (installed != 0) return "native filter ok (already installed)";
                try
                {
                    native = api;
                    processId = pid;
                    fallbackDirectory = fallbackDir;
                    CreateFolder(fallbackDir);   // CreateFileW does not create the folder of the report
                    ours = Filter;
                    oursHandle = GCHandle.Alloc(ours);
                    oursPointer = Marshal.GetFunctionPointerForDelegate(ours);
                    IntPtr old = api.SetFilter(oursPointer);
                    if (old == oursPointer) old = IntPtr.Zero;
                    previousPointer = old;
                    previous = old == IntPtr.Zero ? null : (TopLevelFilter)Marshal.GetDelegateForFunctionPointer(old, typeof(TopLevelFilter));
                    installed = 1;
                    string description = "native filter ok (previous filter " + (old == IntPtr.Zero ? "none" : "0x" + old.ToInt64().ToString("X8", CultureInfo.InvariantCulture)) + ")";
                    PrepareCrashPath(api);
                    return PrepareFailure == null ? description : description + "; crash path not compiled ahead (" + PrepareFailure + ")";
                }
                catch (Exception ex)
                {
                    return "native filter no (" + ex.GetType().Name + ")";
                }
            }
        }

        /// <summary>Tests: puts the previous filter back and forgets everything.</summary>
        internal static void ResetForTests()
        {
            lock (installGate)
            {
                try
                {
                    if (installed != 0 && native != null) native.SetFilter(previousPointer);
                }
                catch (Exception)
                {
                }
                installed = 0;
                reported = 0;
                previous = null;
                previousPointer = IntPtr.Zero;
                if (oursHandle.IsAllocated) oursHandle.Free();
                ours = null;
                oursPointer = IntPtr.Zero;
                native = null;
                LastReport = null;
                PrepareFailure = null;
                filterThread = 0;
                Clock = () => DateTime.UtcNow;
            }
        }

        private static void CreateFolder(string directory)
        {
            try
            {
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Install: compiles the crash path once, while the process is healthy, each step on its own (the first failure goes to
        /// <see cref="PrepareFailure"/>). Every method of this class and of the native layer, the imports with their
        /// marshalling stubs (<see cref="ICrashFilterNative.Prepare"/>), both delegates, and a dry run of the formatting and of
        /// the path on an empty access violation (which also compiles what they call: the clock, CrashReporter's getters).
        /// </summary>
        private static void PrepareCrashPath(ICrashFilterNative api)
        {
            PrepareFailure = null;
            foreach (MethodInfo method in typeof(NativeCrashFilter).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                try
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                }
                catch (Exception ex)
                {
                    PrepareFailed(method.Name, ex);
                }
            }
            try
            {
                api.Prepare();
            }
            catch (Exception ex)
            {
                PrepareFailed("imports", ex);
            }
            try
            {
                RuntimeHelpers.PrepareDelegate(ours);
                if (previous != null) RuntimeHelpers.PrepareDelegate(previous);
            }
            catch (Exception ex)
            {
                PrepareFailed("delegates", ex);
            }
            DryRun(api);
        }

        private static void DryRun(ICrashFilterNative api)
        {
            IntPtr pointers = IntPtr.Zero;
            try
            {
                // EXCEPTION_POINTERS and an EXCEPTION_RECORD: an access violation reading 0 at address 0, 2 parameters.
                int ptr = IntPtr.Size;
                int information = ptr == 8 ? 32 : 20;
                int size = 2 * ptr + information + 15 * ptr;
                pointers = Marshal.AllocHGlobal(size);
                for (int i = 0; i < size; i++) Marshal.WriteByte(pointers, i, 0);
                IntPtr record = new IntPtr(pointers.ToInt64() + 2 * ptr);
                Marshal.WriteIntPtr(pointers, 0, record);
                Marshal.WriteInt32(record, 0, unchecked((int)0xC0000005));
                Marshal.WriteInt32(record, 8 + 2 * ptr, 2);
                DateTime now = Clock();
                string unused = CrashReporter.CrashDirectory;
                Format(pointers, now, api);
                BuildPath(fallbackDirectory ?? unused ?? "dry-run", now);
            }
            catch (Exception ex)
            {
                PrepareFailed("dry run", ex);
            }
            finally
            {
                length = 0;
                path[0] = '\0';
                if (pointers != IntPtr.Zero) Marshal.FreeHGlobal(pointers);
            }
        }

        private static void PrepareFailed(string step, Exception ex)
        {
            if (PrepareFailure == null) PrepareFailure = step + ": " + ex.GetType().Name;
        }

        /// <summary>The filter (any thread). Never throws.</summary>
        internal static int Filter(IntPtr exceptionPointers)
        {
            bool owner = false;
            try
            {
                int thread = CurrentThread();
                if (thread != 0)
                {
                    if (Volatile.Read(ref filterThread) == thread) return ContinueSearch;   // a filter further down called back into ours
                    owner = Interlocked.CompareExchange(ref filterThread, thread, 0) == 0;
                }
                if (Interlocked.Exchange(ref reported, 1) == 0)
                {
                    try
                    {
                        WriteReport(exceptionPointers);
                    }
                    catch (Exception)
                    {
                    }
                }
                TopLevelFilter next = previous;
                if (next == null) return ContinueSearch;
                try
                {
                    return next(exceptionPointers);
                }
                catch (Exception)
                {
                    return ContinueSearch;
                }
            }
            finally
            {
                if (owner) Volatile.Write(ref filterThread, 0);
            }
        }

        private static int CurrentThread()
        {
            try
            {
                ICrashFilterNative api = native;
                return api == null ? 0 : (int)api.CurrentThreadId();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // ------------------------------------------------------------------ the report (preallocated buffers)

        private static void WriteReport(IntPtr pointers)
        {
            ICrashFilterNative api = native;
            if (api == null) return;
            DateTime now = Clock();
            Format(pointers, now, api);
            IntPtr file = IntPtr.Zero;
            string directory = CrashReporter.CrashDirectory;
            if (BuildPath(directory, now)) file = api.Create(path);
            if (file == IntPtr.Zero && BuildPath(fallbackDirectory, now)) file = api.Create(path);
            if (file == IntPtr.Zero) return;
            bool ok = api.Write(file, report, length);
            api.Flush(file);
            if (ok) AppendContext(api, file);
            api.Close(file);
            AfterReport();
        }

        /// <summary>The second part, which allocates: the host's context (boot trace, watchdog), redacted.</summary>
        private static void AppendContext(ICrashFilterNative api, IntPtr file)
        {
            try
            {
                Func<string> context = CrashReporter.Context;
                string text = context == null ? null : context();
                if (string.IsNullOrEmpty(text)) return;
                try
                {
                    text = McvsOffline.Server.Core.Http.LogRedaction.Redact(text) ?? text;
                }
                catch (Exception)
                {
                }
                byte[] bytes = Encoding.UTF8.GetBytes("--- context ---\n" + text + "\n");
                api.Write(file, bytes, bytes.Length);
                api.Flush(file);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>The report's path for later lines and the tests, a log line, and the queued log lines on disk.</summary>
        private static void AfterReport()
        {
            try
            {
                int end = Array.IndexOf(path, '\0');
                LastReport = new string(path, 0, end < 0 ? path.Length : end);
                SelfLog.Write(SelfLog.Fatal, "[crash] native exception: report written to " + LastReport);
            }
            catch (Exception)
            {
            }
            try
            {
                Action<int> flush = CrashReporter.FlushLogs;
                if (flush != null) flush(200);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>&lt;directory&gt;\crash-yyyyMMdd-HHmmss-fff-native.txt, NUL-terminated, into <see cref="path"/>.</summary>
        private static bool BuildPath(string directory, DateTime now)
        {
            if (string.IsNullOrEmpty(directory)) return false;
            int n = directory.Length;
            // directory + separator + "crash-" (6) + 8 + 1 + 6 + 1 + 3 + suffix + NUL
            if (n + 1 + 25 + FileSuffix.Length + 1 > path.Length) return false;
            directory.CopyTo(0, path, 0, n);
            int at = n;
            if (path[at - 1] != '\\' && path[at - 1] != '/') path[at++] = Path.DirectorySeparatorChar;
            at = PutChars(path, at, "crash-");
            at = PutDigits(path, at, now.Year, 4);
            at = PutDigits(path, at, now.Month, 2);
            at = PutDigits(path, at, now.Day, 2);
            path[at++] = '-';
            at = PutDigits(path, at, now.Hour, 2);
            at = PutDigits(path, at, now.Minute, 2);
            at = PutDigits(path, at, now.Second, 2);
            path[at++] = '-';
            at = PutDigits(path, at, now.Millisecond, 3);
            at = PutChars(path, at, FileSuffix);
            path[at] = '\0';
            return true;
        }

        private static int PutChars(char[] buffer, int at, string text)
        {
            text.CopyTo(0, buffer, at, text.Length);
            return at + text.Length;
        }

        private static int PutDigits(char[] buffer, int at, int value, int digits)
        {
            for (int i = digits - 1; i >= 0; i--)
            {
                buffer[at + i] = (char)('0' + value % 10);
                value /= 10;
            }
            return at + digits;
        }

        /// <summary>Tests: the text <see cref="Format"/> produced last.</summary>
        internal static string FormattedText()
        {
            return Encoding.UTF8.GetString(report, 0, length);
        }

        /// <summary>The report text (UTF-8) into <see cref="report"/>; <see cref="length"/> is its size. Allocates nothing.</summary>
        internal static void Format(IntPtr pointers, DateTime now, ICrashFilterNative api)
        {
            length = 0;
            Put("FATAL native unhandled exception (top-level filter; the game's own filter runs next and writes core.dmp, then Windows Error Reporting)\n");
            Put("time ");
            PutNumber(now.Year, 4); Put("-"); PutNumber(now.Month, 2); Put("-"); PutNumber(now.Day, 2); Put("T");
            PutNumber(now.Hour, 2); Put(":"); PutNumber(now.Minute, 2); Put(":"); PutNumber(now.Second, 2); Put(".");
            PutNumber(now.Millisecond, 3); Put("Z, pid ");
            PutNumber(processId, 0);
            Put(", thread tid ");
            PutNumber(api.CurrentThreadId(), 0);
            Put("\n");
            IntPtr record = pointers == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(pointers);
            if (record == IntPtr.Zero)
            {
                Put("no exception record\n");
            }
            else
            {
                int ptr = IntPtr.Size;
                uint code = (uint)Marshal.ReadInt32(record, 0);
                uint flags = (uint)Marshal.ReadInt32(record, 4);
                IntPtr address = Marshal.ReadIntPtr(record, 8 + ptr);
                int count = Marshal.ReadInt32(record, 8 + 2 * ptr);
                int information = ptr == 8 ? 32 : 20;
                if (count < 0) count = 0;
                if (count > 15) count = 15;
                Put("code 0x"); PutHex((ulong)code, 8); Put(" ("); Put(CodeName(code)); Put("), flags 0x"); PutHex(flags, 0);
                if ((flags & 1) != 0) Put(" (noncontinuable)");
                Put("\naddress 0x"); PutHex(Address(address), 2 * ptr);
                IntPtr moduleBase;
                int name = 0;
                try
                {
                    name = api.ModuleOf(address, module, out moduleBase);
                }
                catch (Exception)
                {
                    moduleBase = IntPtr.Zero;
                }
                if (name > 0)
                {
                    Put(" in "); PutChars(module, name); Put(" +0x"); PutHex(Address(address) - Address(moduleBase), 0);
                    Put(" (base 0x"); PutHex(Address(moduleBase), 2 * ptr); Put(")");
                }
                else
                {
                    Put(" (in no loaded module)");
                }
                Put("\n");
                if ((code == 0xC0000005 || code == 0xC0000006) && count >= 2)
                {
                    ulong operation = Address(Marshal.ReadIntPtr(record, information));
                    ulong target = Address(Marshal.ReadIntPtr(record, information + ptr));
                    Put(operation == 0 ? "reading" : operation == 1 ? "writing" : operation == 8 ? "executing (DEP)" : "accessing");
                    Put(" 0x"); PutHex(target, 2 * ptr); Put("\n");
                }
                if (count > 0)
                {
                    Put("parameters");
                    for (int i = 0; i < count; i++)
                    {
                        Put(" 0x"); PutHex(Address(Marshal.ReadIntPtr(record, information + i * ptr)), 0);
                    }
                    Put("\n");
                }
                if (code == ClrException) Put("a managed exception reached native code: see the game's last Fatal line and any exception-*.txt of this run\n");
            }
            Put("game's last Fatal line: ");
            string fatal = CrashReporter.LastGameFatal;
            Put(string.IsNullOrEmpty(fatal) ? "(none)" : fatal);
            Put("\n");
        }

        private static ulong Address(IntPtr value)
        {
            return IntPtr.Size == 8 ? (ulong)value.ToInt64() : (uint)value.ToInt32();
        }

        internal static string CodeName(uint code)
        {
            switch (code)
            {
                case 0xC0000005: return "access violation";
                case 0xC0000006: return "in-page error";
                case ClrException: return "managed exception (CLR)";
                case 0xE06D7363: return "C++ exception";
                case 0xC00000FD: return "stack overflow";
                case 0xC0000409: return "stack buffer overrun / fail fast";
                case 0xC0000374: return "heap corruption";
                case 0xC0000017: return "no memory";
                case 0xC0000094: return "integer divide by zero";
                case 0xC0000095: return "integer overflow";
                case 0xC000001D: return "illegal instruction";
                case 0xC0000096: return "privileged instruction";
                case 0xC000008E: return "float divide by zero";
                case 0x80000003: return "breakpoint";
                case 0xC0000420: return "assertion failure";
                default: return "unknown";
            }
        }

        // UTF-8 into the report buffer, truncated at its end.

        private static void Put(string text)
        {
            for (int i = 0; i < text.Length; i++) PutChar(text[i]);
        }

        private static void PutChars(char[] text, int count)
        {
            for (int i = 0; i < count; i++) PutChar(text[i]);
        }

        private static void PutChar(char c)
        {
            if (c < 0x80)
            {
                PutByte((byte)c);
            }
            else if (c < 0x800)
            {
                PutByte((byte)(0xC0 | (c >> 6)));
                PutByte((byte)(0x80 | (c & 0x3F)));
            }
            else if (char.IsSurrogate(c))
            {
                PutByte((byte)'?');   // a pair would need both halves; never in the texts written here
            }
            else
            {
                PutByte((byte)(0xE0 | (c >> 12)));
                PutByte((byte)(0x80 | ((c >> 6) & 0x3F)));
                PutByte((byte)(0x80 | (c & 0x3F)));
            }
        }

        private static void PutByte(byte b)
        {
            if (length < report.Length) report[length++] = b;
        }

        /// <summary>Decimal, at least <paramref name="digits"/> digits (0: as many as needed).</summary>
        private static void PutNumber(long value, int digits)
        {
            if (value < 0)
            {
                PutByte((byte)'-');
                value = -value;
            }
            int n = 1;
            for (long v = value / 10; v > 0; v /= 10) n++;
            if (n < digits) n = digits;
            for (int i = n - 1; i >= 0; i--)
            {
                long scale = 1;
                for (int k = 0; k < i; k++) scale *= 10;
                PutByte((byte)('0' + (int)(value / scale % 10)));
            }
        }

        /// <summary>Upper-case hex, at least <paramref name="digits"/> digits (0: as many as needed).</summary>
        private static void PutHex(ulong value, int digits)
        {
            int n = 1;
            for (ulong v = value >> 4; v > 0; v >>= 4) n++;
            if (n < digits) n = digits;
            for (int i = n - 1; i >= 0; i--)
            {
                int nibble = (int)((value >> (4 * i)) & 0xF);
                PutByte((byte)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10));
            }
        }
    }
}
#endif
