// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Compiled into McvsOffline.Server.Embedded.csproj (Android, MCVS_ANDROID) and McvsOffline.Server.Pc.csproj (Steam PC,
// MCVS_WINDOWS), the DLLs that run inside the game process, and, without either, into the net8 tests.
// Wiring: ARCHITECTURE §2.3 "Embedded wiring"; verify-C7 §5-§7; verify-C1 (args.txt format).
using System;
using System.Threading;
using McvsOffline.Embedded;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Server.Core;

namespace McvsOffline
{
    /// <summary>
    /// Entry point of the offline server inside the game. The patcher rewrites
    /// <c>Gameloft.NicePlaySDK.NicePlaySDKDLL.OnInitialize</c> (invoked reflectively, and unguarded, by
    /// <c>XamarinMainActivity.OnCreate</c> before <c>MainLoop.Start</c>, the Java GameInstaller and native init) so that
    /// it first runs <c>try { McvsOffline.Bootstrap.Init(); } catch { }</c> (verify-C7 §5). On PC the patch <c>hook-pc</c> does
    /// the same in <c>EngineBridge.Class1.Start</c>, before <c>MainLoop.Start</c>, and the work is
    /// <see cref="McvsOffline.Embedded.Windows.WindowsBoot"/>'s (docs/design/pc-port.md §6.1, §8.2).
    /// <para>Contract: idempotent, returns within milliseconds, and never throws into the game's OnCreate.</para>
    /// </summary>
    public static class Bootstrap
    {
#if MCVS_WINDOWS
        /// <summary>The running host (kept by WindowsBoot so nothing is collected while the game runs).</summary>
        public static EmbeddedHost Host { get { return McvsOffline.Embedded.Windows.WindowsBoot.Host; } }

        public static void Init()
        {
            McvsOffline.Embedded.Windows.WindowsBoot.Init();
        }
#else
        private static int initialized;

        /// <summary>The running host (kept here so nothing is collected while the game runs).</summary>
        public static EmbeddedHost Host { get; private set; }

        public static void Init()
        {
            if (Interlocked.Exchange(ref initialized, 1) != 0) return;
            BootClock.Touch();
            try
            {
                // First, before anything can fail: where the logging machinery reports its own problems, and the crash
                // reporter (ours runs before the game's own handler, which GameApplication subscribes later).
                InstallDiagnostics();
            }
            catch (Exception)
            {
            }
            try
            {
                // A separate method: if anything in it fails to JIT on this runtime, the exception surfaces here.
                InitCore();
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

        private static void InstallDiagnostics()
        {
#if MCVS_ANDROID
            SelfLog.SetWriter(McvsOffline.Embedded.Android.Logcat.Write);
#endif
            CrashReporter.Install();
#if MCVS_ANDROID
            McvsOffline.Embedded.Android.AndroidDiagnostics.InstallCrashHook();
#endif
        }

        private static void InitCore()
        {
#if MCVS_ANDROID
            Host = EmbeddedHost.Start(new McvsOffline.Embedded.Android.AndroidPlatform(), new EmbeddedHostOptions());
#else
            // Off-device (a PC running the embedded host for testing): the game root comes from MCVS_GAME_ROOT.
            string root = Environment.GetEnvironmentVariable("MCVS_GAME_ROOT");
            if (string.IsNullOrEmpty(root)) root = System.IO.Path.Combine(Environment.CurrentDirectory, "game-root");
            Host = EmbeddedHost.Start(new DirectoryPlatform(root, true), new EmbeddedHostOptions());
#endif
        }
#endif
    }
}
