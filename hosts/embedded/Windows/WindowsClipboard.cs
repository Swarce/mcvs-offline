#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL.
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using McvsOffline.Embedded.Admin;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>The Win32 calls of <see cref="WindowsClipboard"/>, behind an interface so their order is unit-tested.</summary>
    public interface IClipboardNative
    {
        /// <summary>The game's top-level window (<c>Process.MainWindowHandle</c>), or zero.</summary>
        IntPtr MainWindow();

        /// <summary>A message-only window (class "STATIC", parent HWND_MESSAGE) on the calling thread, or zero.</summary>
        IntPtr CreateMessageWindow();

        bool DestroyWindow(IntPtr window);

        bool OpenClipboard(IntPtr owner);

        bool EmptyClipboard();

        IntPtr SetClipboardData(uint format, IntPtr memory);

        bool CloseClipboard();

        /// <summary>GlobalAlloc(GMEM_MOVEABLE, bytes).</summary>
        IntPtr GlobalAlloc(int bytes);

        IntPtr GlobalLock(IntPtr memory);

        bool GlobalUnlock(IntPtr memory);

        IntPtr GlobalFree(IntPtr memory);

        /// <summary>Copies the characters of <paramref name="text"/> and a terminating NUL to <paramref name="destination"/>.</summary>
        void CopyText(string text, IntPtr destination);

        /// <summary>Marshal.GetLastWin32Error() of the previous call.</summary>
        int LastError();

        void Sleep(int ms);
    }

    /// <summary>
    /// Text to the Windows clipboard (docs/design/pc-port.md §6.3; <c>GameManager.SetClipboard</c> only stores a private string, C4).
    /// The owner is the game's top-level window; when the process has none (yet), a message-only window created on the calling
    /// thread and destroyed after <c>CloseClipboard</c>. Never <c>OpenClipboard(NULL)</c>: <c>EmptyClipboard</c> would then set a
    /// NULL owner and <c>SetClipboardData</c> fails. <c>CF_UNICODETEXT</c> in <c>GMEM_MOVEABLE</c> memory; <c>OpenClipboard</c> is
    /// tried 5 times 50 ms apart while another program holds the clipboard; the memory is freed only when
    /// <c>SetClipboardData</c> failed (on success the system owns it). At most <see cref="AdminHost.MaxClipboardChars"/>
    /// characters. Never throws.
    /// </summary>
    public sealed class WindowsClipboard
    {
        public const int OpenAttempts = 5;
        public const int OpenRetryMs = 50;

        private readonly IClipboardNative native;
        private readonly Action<string> warn;

        public WindowsClipboard(IClipboardNative native, Action<string> warn)
        {
            this.native = native ?? new Win32Clipboard();
            this.warn = warn;
        }

        /// <summary>True once the clipboard holds <paramref name="text"/> (cut to the maximum length).</summary>
        public bool SetText(string text)
        {
            if (text == null) return false;
            if (text.Length > AdminHost.MaxClipboardChars) text = text.Substring(0, AdminHost.MaxClipboardChars);
            IntPtr owner = IntPtr.Zero;
            bool created = false;
            try
            {
                owner = native.MainWindow();
                if (owner == IntPtr.Zero)
                {
                    owner = native.CreateMessageWindow();
                    created = owner != IntPtr.Zero;
                    if (!created) return Fail("no window to own the clipboard (CreateWindowExW error " + native.LastError() + ")");
                }
                bool opened = false;
                for (int attempt = 1; attempt <= OpenAttempts; attempt++)
                {
                    if (native.OpenClipboard(owner))
                    {
                        opened = true;
                        break;
                    }
                    if (attempt < OpenAttempts) native.Sleep(OpenRetryMs);
                }
                if (!opened) return Fail("another program holds the clipboard (OpenClipboard error " + native.LastError() + ")");
                try
                {
                    return Put(text);
                }
                finally
                {
                    native.CloseClipboard();
                }
            }
            catch (Exception ex)
            {
                return Fail(ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (created)
                {
                    try
                    {
                        native.DestroyWindow(owner);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private bool Put(string text)
        {
            if (!native.EmptyClipboard()) return Fail("EmptyClipboard error " + native.LastError());
            IntPtr memory = native.GlobalAlloc((text.Length + 1) * 2);
            if (memory == IntPtr.Zero) return Fail("GlobalAlloc error " + native.LastError());
            bool owned = false;
            try
            {
                IntPtr target = native.GlobalLock(memory);
                if (target == IntPtr.Zero) return Fail("GlobalLock error " + native.LastError());
                try
                {
                    native.CopyText(text, target);
                }
                finally
                {
                    native.GlobalUnlock(memory);
                }
                if (native.SetClipboardData(Win32.ClipboardUnicodeText, memory) == IntPtr.Zero)
                    return Fail("SetClipboardData error " + native.LastError());
                owned = true;   // the system owns the memory now
                return true;
            }
            finally
            {
                if (!owned) native.GlobalFree(memory);
            }
        }

        private bool Fail(string reason)
        {
            try
            {
                if (warn != null) warn("admin: the clipboard was not set: " + reason);
            }
            catch (Exception)
            {
            }
            return false;
        }
    }

    /// <summary>The real <see cref="IClipboardNative"/> over <see cref="Win32"/>.</summary>
    internal sealed class Win32Clipboard : IClipboardNative
    {
        public IntPtr MainWindow()
        {
            using (Process process = Process.GetCurrentProcess()) return process.MainWindowHandle;
        }

        public IntPtr CreateMessageWindow()
        {
            return Win32.CreateWindowExW(0, "STATIC", "McvsOffline clipboard", 0, 0, 0, 0, 0, Win32.MessageOnlyParent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }

        public bool DestroyWindow(IntPtr window) { return Win32.DestroyWindow(window); }

        public bool OpenClipboard(IntPtr owner) { return Win32.OpenClipboard(owner); }

        public bool EmptyClipboard() { return Win32.EmptyClipboard(); }

        public IntPtr SetClipboardData(uint format, IntPtr memory) { return Win32.SetClipboardData(format, memory); }

        public bool CloseClipboard() { return Win32.CloseClipboard(); }

        public IntPtr GlobalAlloc(int bytes) { return Win32.GlobalAlloc(Win32.GlobalMoveable, new UIntPtr((uint)bytes)); }

        public IntPtr GlobalLock(IntPtr memory) { return Win32.GlobalLock(memory); }

        public bool GlobalUnlock(IntPtr memory) { return Win32.GlobalUnlock(memory); }

        public IntPtr GlobalFree(IntPtr memory) { return Win32.GlobalFree(memory); }

        public void CopyText(string text, IntPtr destination)
        {
            char[] chars = (text + "\0").ToCharArray();
            Marshal.Copy(chars, 0, destination, chars.Length);
        }

        public int LastError() { return Marshal.GetLastWin32Error(); }

        public void Sleep(int ms) { Thread.Sleep(ms); }
    }
}
#endif
