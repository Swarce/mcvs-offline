#if MCVS_WINDOWS
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// PC DLL only (docs/design/pc-port.md §3.1, §5.2): the real ThreadDump.cs (libc, libdl, /proc, Mono) is excluded from
// McvsOffline.Server.Pc.csproj, and GameThreadWatchdog / EmbeddedHost reference it statically. Not under Windows/ on
// purpose: the net8 tests compile that folder next to the real ThreadDump.cs, and the two would collide.
using System;
using System.Collections.Generic;

namespace McvsOffline.Embedded.Diagnostics
{
    /// <summary>No in-process thread dump on the .NET Framework CLR ([WH] §4.5): the same signatures as ThreadDump.cs, as no-ops.</summary>
    public static class ThreadDump
    {
        public static void Capture(int gameTid, IList<int> scanTids, string managedDumpFile, int managedTimeoutMs, Action<string> emit)
        {
        }

        public static T WithDumpable<T>(Func<T> read)
        {
            return read();
        }

        public static List<int> Related(int gameTid, string comm)
        {
            return new List<int>();
        }
    }
}
#endif
