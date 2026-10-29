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
    }
}
#endif
