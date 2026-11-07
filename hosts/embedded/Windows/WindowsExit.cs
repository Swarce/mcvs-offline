#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>
    /// The two ways the settings page ends the PC game for a restart (docs/design/pc-port.md §6.6, C18). AdminHost decides which
    /// and in what order (the 3 s fallback to the host's path); these only perform.
    /// <list type="bullet">
    /// <item><see cref="ViaGame"/>: the relaunch helper (when asked), then <c>Gameloft.Engine.MenuManager.ExitAppAndroid()</c>
    /// posted to the game thread: the game's own quit (<c>WCPToolkit::ExitApp</c>, then the exe's <c>TerminateProcess</c>).</item>
    /// <item><see cref="ViaHost"/>: the helper (if not started yet), the logs flushed on this thread, then
    /// <c>Process.GetCurrentProcess().Kill()</c>. Nothing runs at a normal exit of this game, so nothing may rely on shutdown
    /// hooks.</item>
    /// </list>
    /// The helper is a hidden PowerShell that waits for this process to end (the exe's single-instance mutex makes an early
    /// second start exit silently), waits 2 s for Steam to notice, then starts the game the Steam way
    /// (<c>steam://rungameid/723780</c>). Whether it survives the game's exit is WR2 Q9 (RelaunchUnconfirmed until then).
    /// </summary>
    public sealed class WindowsExit
    {
        public const string SteamAppId = "723780";
        public const string MenuManagerType = "Gameloft.Engine.MenuManager";
        public const string ExitMethod = "ExitAppAndroid";

        private int helperStarted;

        public WindowsExit()
        {
            ProcessId = () =>
            {
                using (Process p = Process.GetCurrentProcess()) return p.Id;
            };
            StartProcess = StartHidden;
            Kill = () =>
            {
                using (Process p = Process.GetCurrentProcess()) p.Kill();
            };
            ExitApp = InvokeExitAppAndroid;
        }

        /// <summary>Posts an action to the game thread (GameReflection.Post); false when there is no game thread. Required.</summary>
        public Func<Action, bool> Post { get; set; }

        /// <summary>Flushes the host, server and game logs on the calling thread (EmbeddedHost.FlushLogsNow); may be null.</summary>
        public Action<int> FlushLogsNow { get; set; }

        /// <summary>Where failures go; may be null.</summary>
        public Action<string, Exception> Warn { get; set; }

        /// <summary>This process's id (tests fix it).</summary>
        public Func<int> ProcessId { get; set; }

        /// <summary>(program, arguments): starts a hidden process (tests record it).</summary>
        public Action<string, string> StartProcess { get; set; }

        /// <summary>Ends this process (tests record it).</summary>
        public Action Kill { get; set; }

        /// <summary>The game's quit, run on the game thread (tests record it).</summary>
        public Action ExitApp { get; set; }

        /// <summary>
        /// The arguments of powershell.exe for the relaunch helper of process <paramref name="pid"/>: wait for it to end (60 s at
        /// most), 2 s more, then <c>Start-Process 'steam://rungameid/723780'</c>.
        /// </summary>
        public static string HelperArguments(int pid)
        {
            return "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Wait-Process -Id " + pid.ToString(CultureInfo.InvariantCulture) +
                   " -Timeout 60 -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; Start-Process 'steam://rungameid/" + SteamAppId + "'\"";
        }

        /// <summary>The game's quit on the game thread (after the helper when <paramref name="relaunch"/>). True once posted.</summary>
        public bool ViaGame(bool relaunch)
        {
            try
            {
                Func<Action, bool> post = Post;
                if (post == null) return false;
                if (relaunch) StartHelper();
                Action exit = ExitApp;
                bool posted = post(() => exit());
                if (!posted) Report("admin: the game thread is not known; the game's quit (MenuManager.ExitAppAndroid) was not posted", null);
                return posted;
            }
            catch (Exception ex)
            {
                Report("admin: the game's quit could not be posted", ex);
                return false;
            }
        }

        /// <summary>The helper (when <paramref name="relaunch"/> and not yet started), the logs, then the kill. True unless the kill failed.</summary>
        public bool ViaHost(bool relaunch)
        {
            try
            {
                if (relaunch) StartHelper();
                Action<int> flush = FlushLogsNow;
                if (flush != null)
                {
                    try
                    {
                        flush(2000);
                    }
                    catch (Exception)
                    {
                    }
                }
                Kill();
                return true;
            }
            catch (Exception ex)
            {
                Report("admin: the host's exit failed", ex);
                return false;
            }
        }

        /// <summary>Starts the relaunch helper once per process. Never throws.</summary>
        public void StartHelper()
        {
            if (Interlocked.Exchange(ref helperStarted, 1) != 0) return;
            try
            {
                StartProcess("powershell.exe", HelperArguments(ProcessId()));
            }
            catch (Exception ex)
            {
                Report("admin: the relaunch helper did not start (start the game from Steam yourself)", ex);
            }
        }

        private static void StartHidden(string program, string arguments)
        {
            var info = new ProcessStartInfo(program, arguments) { UseShellExecute = false, CreateNoWindow = true };
            using (Process.Start(info))
            {
            }
        }

        /// <summary>public static Gameloft.Engine.MenuManager.ExitAppAndroid() (PC-EN/MenuManager.cs:374-379).</summary>
        private static void InvokeExitAppAndroid()
        {
            Type type = LoadedTypes.EngineType(MenuManagerType);
            MethodInfo method = type == null ? null : type.GetMethod(ExitMethod, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (method == null) throw new MissingMethodException(MenuManagerType, ExitMethod);
            method.Invoke(null, null);
        }

        private void Report(string text, Exception ex)
        {
            try
            {
                Action<string, Exception> warn = Warn;
                if (warn != null) warn(text, ex);
            }
            catch (Exception)
            {
            }
        }
    }
}
#endif
