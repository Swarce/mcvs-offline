#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL. The declarations
// compile everywhere; they only fail when called off Windows.
using System;
using System.Runtime.InteropServices;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>
    /// The Win32 imports of the PC host (docs/design/pc-port.md §6.3): every one <c>ExactSpelling = true</c> with the exact
    /// export name (<c>*W</c> for text), <c>CharSet.Unicode</c> where a string is passed and <c>SetLastError = true</c> where the
    /// error is read. Checked by PcPInvokeTests on the built PC DLL. The Steam imports are in <see cref="SteamApiNative"/>.
    /// </summary>
    internal static class Win32
    {
        /// <summary>CF_UNICODETEXT.</summary>
        public const uint ClipboardUnicodeText = 13;

        /// <summary>GMEM_MOVEABLE (clipboard memory must be movable).</summary>
        public const uint GlobalMoveable = 0x0002;

        /// <summary>HWND_MESSAGE: the parent of a message-only window.</summary>
        public static readonly IntPtr MessageOnlyParent = new IntPtr(-3);

        [DllImport("kernel32", EntryPoint = "GetCurrentThreadId", ExactSpelling = true)]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32", EntryPoint = "OutputDebugStringW", ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern void OutputDebugStringW(string text);

        [DllImport("kernel32", EntryPoint = "GlobalAlloc", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [DllImport("kernel32", EntryPoint = "GlobalLock", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32", EntryPoint = "GlobalUnlock", ExactSpelling = true, SetLastError = true)]
        public static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32", EntryPoint = "GlobalFree", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GlobalFree(IntPtr memory);

        [DllImport("user32", EntryPoint = "OpenClipboard", ExactSpelling = true, SetLastError = true)]
        public static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32", EntryPoint = "EmptyClipboard", ExactSpelling = true, SetLastError = true)]
        public static extern bool EmptyClipboard();

        [DllImport("user32", EntryPoint = "SetClipboardData", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr SetClipboardData(uint format, IntPtr memory);

        [DllImport("user32", EntryPoint = "CloseClipboard", ExactSpelling = true, SetLastError = true)]
        public static extern bool CloseClipboard();

        [DllImport("user32", EntryPoint = "CreateWindowExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width,
                                                    int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32", EntryPoint = "DestroyWindow", ExactSpelling = true, SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr window);

        // ---------------------------------------------------------------- the native crash filter (NativeCrashFilter, §6.4)
        // Called from inside a top-level exception filter: no string marshalling (paths and names go through pinned char[]
        // buffers, the report through a pinned byte[]), so a crash report allocates nothing on the managed heap.

        /// <summary>GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT.</summary>
        public const uint ModuleFromAddressNoRef = 0x00000004 | 0x00000002;

        /// <summary>GENERIC_WRITE.</summary>
        public const uint GenericWrite = 0x40000000;

        /// <summary>FILE_SHARE_READ.</summary>
        public const uint FileShareRead = 0x00000001;

        /// <summary>CREATE_NEW (never overwrites).</summary>
        public const uint CreateNew = 1;

        /// <summary>FILE_ATTRIBUTE_NORMAL.</summary>
        public const uint FileAttributeNormal = 0x80;

        /// <summary>INVALID_HANDLE_VALUE.</summary>
        public static readonly IntPtr InvalidHandle = new IntPtr(-1);

        /// <summary>Takes and returns a LPTOP_LEVEL_EXCEPTION_FILTER (a WINAPI function pointer); no error is set.</summary>
        [DllImport("kernel32", EntryPoint = "SetUnhandledExceptionFilter", ExactSpelling = true)]
        public static extern IntPtr SetUnhandledExceptionFilter(IntPtr filter);

        [DllImport("kernel32", EntryPoint = "GetModuleHandleExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

        [DllImport("kernel32", EntryPoint = "GetModuleFileNameW", ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern uint GetModuleFileNameW(IntPtr module, [Out] char[] buffer, uint size);

        /// <summary><paramref name="path"/> is NUL-terminated.</summary>
        [DllImport("kernel32", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(char[] path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32", EntryPoint = "WriteFile", ExactSpelling = true, SetLastError = true)]
        public static extern bool WriteFile(IntPtr file, byte[] buffer, uint count, out uint written, IntPtr overlapped);

        [DllImport("kernel32", EntryPoint = "FlushFileBuffers", ExactSpelling = true, SetLastError = true)]
        public static extern bool FlushFileBuffers(IntPtr file);

        [DllImport("kernel32", EntryPoint = "CloseHandle", ExactSpelling = true, SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
#endif
