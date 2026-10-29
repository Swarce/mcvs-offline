#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests (which run it with injected actions), never into
// the Android DLL.
using System;
using System.Threading;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>
    /// The body of <c>McvsOffline.Bootstrap.Init()</c> on PC (its <c>MCVS_WINDOWS</c> branch is the one call to <see cref="Init"/>,
    /// docs/design/pc-port.md §6.1): called by the patched <c>EngineBridge.Class1.Start</c> before <c>MainLoop.Start</c>, which
    /// reads args.txt after it returns. Same contract as on Android: idempotent, returns within milliseconds, never throws.
    /// </summary>
    public static class WindowsBoot
    {
        private static int initialized;

        /// <summary>The running host (kept here so nothing is collected while the game runs).</summary>
        public static EmbeddedHost Host { get; private set; }

        /// <summary>Step 1: where the logging machinery reports its own problems, and the crash reporter (tests replace it).</summary>
        internal static Action InstallDiagnosticsAction = InstallDiagnostics;

        /// <summary>Step 2: the working directory and <see cref="EmbeddedHost.Start"/> on a <see cref="WindowsPlatform"/> (tests replace it).</summary>
        internal static Func<EmbeddedHost> StartAction = InitCore;

        public static void Init()
        {
            if (Interlocked.Exchange(ref initialized, 1) != 0) return;
            BootClock.Touch();
            try
            {
                InstallDiagnosticsAction();
            }
            catch (Exception)
            {
            }
            try
            {
                Host = StartAction();
            }
            catch (Exception ex)
            {
                // Never let a server problem prevent the game from starting.
                try
                {
                    Console.WriteLine("McvsOffline: bootstrap failed: " + ex);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>Tests: lets <see cref="Init"/> run again.</summary>
        internal static void ResetForTests()
        {
            Interlocked.Exchange(ref initialized, 0);
            Host = null;
        }

        private static void InstallDiagnostics()
        {
            // WP2 adds the OutputDebugString writer for SelfLog and the UnobservedTaskException subscription (§6.1).
            CrashReporter.Install();
        }

        private static EmbeddedHost InitCore()
        {
            try
            {
                // The game is started from Steam with the exe folder as working directory; a shortcut elsewhere would make
                // the engine's relative args.txt another file ([WH] §6.3).
                Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            }
            catch (Exception)
            {
            }
            return EmbeddedHost.Start(new WindowsPlatform(), new EmbeddedHostOptions());
        }
    }
}
#endif
