// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLLs and the net8 tests. Only the PC host sets it (WindowsBoot), so the
// Android lines it feeds are unchanged.
using System.Globalization;

namespace McvsOffline.Embedded.Diagnostics
{
    /// <summary>
    /// The game process id for the log lines that must be tied to one run of the game (docs/design/pc-port.md §6.4, WR2 F3/F4):
    /// <c>embedded host starting</c> and the watchdog's STALL report. bootstrap.log holds every start of the game, and the kit's
    /// 'watch' takes its dump of the process a STALL line names. 0 = not set (Android, where logcat carries the pid, and tests):
    /// the lines then read as before.
    /// </summary>
    public static class HostProcess
    {
        private static volatile int id;

        /// <summary>The process id, or 0 when the host did not set it.</summary>
        public static int Id
        {
            get { return id; }
            set { id = value; }
        }

        /// <summary>", pid 1234", or "" when <see cref="Id"/> is 0.</summary>
        public static string Suffix(string separator)
        {
            int value = id;
            return value == 0 ? "" : separator + "pid " + value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
