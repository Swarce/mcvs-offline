#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests (which run it with injected actions), never into
// the Android DLL.
using System;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>
    /// The body of <c>McvsOffline.Bootstrap.Init()</c> on PC (its <c>MCVS_WINDOWS</c> branch is the one call to <see cref="Init"/>,
    /// docs/design/pc-port.md §6.1): called by the patched <c>EngineBridge.Class1.Start</c> before <c>MainLoop.Start</c>, which
    /// reads args.txt after it returns. Same contract as on Android: idempotent, returns within milliseconds, never throws.
    /// <list type="number">
    /// <item><c>InstallDiagnostics</c>: the process id for the log lines (<see cref="HostProcess"/>), SelfLog writes through
    /// OutputDebugString, the crash reporter with the public <c>TaskScheduler.UnobservedTaskException</c> event, and our
    /// top-level exception filter over the exe's (<see cref="NativeCrashFilter"/>, docs/design/pc-port.md §6.4);</item>
    /// <item><c>InitCore</c>: the working directory becomes the exe folder (a shortcut elsewhere would make the engine's relative
    /// args.txt another file, [WH] §6.3), then <see cref="EmbeddedHost.Start"/> on a <see cref="WindowsPlatform"/>.</item>
    /// </list>
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
                    DebugOutput.Write(SelfLog.Error, "bootstrap failed: " + ex);
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

        internal static void InstallDiagnostics()
        {
            HostProcess.Id = CurrentProcessId();
            SelfLog.SetWriter(DebugOutput.Write);
            CrashReporter.Install(SubscribeUnobservedTasks);
            WindowsPlatform.InstallNativeCrashFilter();
        }

        private static int CurrentProcessId()
        {
            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess()) return process.Id;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>The public event (present in the .NET Framework's mscorlib, unlike the game's trimmed Android one).</summary>
        internal static bool SubscribeUnobservedTasks(EventHandler<UnobservedTaskExceptionEventArgs> handler)
        {
            TaskScheduler.UnobservedTaskException += handler;
            return true;
        }

        /// <summary>The exe folder, as the CLR host gives it; changes the working directory when it differs. Never throws.</summary>
        internal static void SetWorkingDirectory(string baseDirectory)
        {
            try
            {
                if (string.IsNullOrEmpty(baseDirectory)) return;
                if (!string.Equals(System.IO.Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\', '/'),
                                   System.IO.Path.GetFullPath(baseDirectory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    Environment.CurrentDirectory = baseDirectory;
            }
            catch (Exception)
            {
            }
        }

        private static EmbeddedHost InitCore()
        {
            SetWorkingDirectory(AppDomain.CurrentDomain.BaseDirectory);
            return EmbeddedHost.Start(new WindowsPlatform(), new EmbeddedHostOptions());
        }
    }
}
#endif
