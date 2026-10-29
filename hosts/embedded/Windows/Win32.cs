#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL. The declarations
// compile everywhere; they only fail when called off Windows.
using System.Runtime.InteropServices;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>
    /// The Win32 imports of the PC host (docs/design/pc-port.md §6.3): every one <c>ExactSpelling = true</c> with the exact
    /// export name (<c>*W</c> for text), <c>CharSet.Unicode</c> where a string is passed and <c>SetLastError = true</c> where the
    /// error is read. Checked by PcPInvokeTests on the built PC DLL.
    /// </summary>
    internal static class Win32
    {
        [DllImport("kernel32", EntryPoint = "GetCurrentThreadId", ExactSpelling = true)]
        public static extern uint GetCurrentThreadId();
    }
}
#endif
