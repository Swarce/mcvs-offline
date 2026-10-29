#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL.
using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>
    /// The Steam (PC) build's platform (docs/design/pc-port.md §6.4): the game folder is the game root (D1), the logs are
    /// plain files, the page opens in the Steam overlay or the system browser. Not an <c>IInstallerPlatform</c> (no Java
    /// installer) and not an <c>INotificationPlatform</c>.
    /// <para>Day-0 skeleton with safe defaults: every member answers "cannot" or nothing, so the host runs and the page shows
    /// every action as unavailable until WP2 fills in the overlay, browser, share, clipboard and exit routes.</para>
    /// </summary>
    public sealed class WindowsPlatform : IEmbeddedPlatform, IPlatformDiagnostics, IAdminPlatform, IPagePlatform
    {
        /// <summary>Overrides the game root (tests; a game started outside its folder).</summary>
        public const string GameRootVariable = "MCVS_GAME_ROOT";

        // ------------------------------------------------------------------ IEmbeddedPlatform

        public string Name { get { return "windows"; } }

        public string EntryPoint { get { return "EngineBridge.Class1.Start"; } }

        public string ArgsReader { get { return "MainLoop_Start"; } }

        public string PortInUseHint
        {
            get
            {
                return "Another program holds the port, or Windows reserves it (Hyper-V/WSL excluded port ranges: " +
                       "netsh int ipv4 show excludedportrange protocol=tcp); change 'port' and run the kit's install again.";
            }
        }

        /// <summary>The exe folder (AppDomain base directory), or <see cref="GameRootVariable"/>. Throws when neither is known.</summary>
        public string ResolveGameRoot(Log log)
        {
            string root = Environment.GetEnvironmentVariable(GameRootVariable);
            if (string.IsNullOrEmpty(root)) root = AppDomain.CurrentDomain.BaseDirectory;
            if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("the game folder is unknown");
            return Path.GetFullPath(root);
        }

        /// <summary>A GUI-subsystem process has no console; the OutputDebugString sink comes with WP2.</summary>
        public LogSink CreateSystemLogSink(LogLevel minLevel)
        {
            return null;
        }

        // ------------------------------------------------------------------ IPlatformDiagnostics

        public void InstallCrashHooks()
        {
            // Nothing: the CLR raises AppDomain.UnhandledException for every thread.
        }

        /// <summary>%TEMP%; the host appends McvsOffline\logs.</summary>
        public string FallbackLogDirectory()
        {
            try
            {
                return Path.GetTempPath();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public string ApplyGameLog(Settings settings, SafeLogFile gameLog)
        {
#if MCVS_GAMELOG
            return GameLogBridge.Apply(settings.GetString("gameLog.levels", "error warning"), settings.GetString("gameLog.mute", "profile:debug"), gameLog,
                                       (int)settings.GetLong("gameLog.maxMessageChars", 8192));
#else
            return "game log: bridge not compiled in";
#endif
        }

        public string JavaStacks(string gameThreadComm)
        {
            return null;
        }

        public void StartBackground(EmbeddedHost host)
        {
        }

        public string StatusExtras()
        {
            return null;
        }

        // ------------------------------------------------------------------ IAdminPlatform

        public int SdkInt { get { return 0; } }

        public bool OpenInGameBrowserJni(string url)
        {
            return Open(url, "auto") != null;
        }

        public bool OpenInGameBrowserWithPostData(string url, string postData)
        {
            return false;
        }

        public bool IsInGameBrowserOpen()
        {
            return false;
        }

        public bool QuitInGameBrowser()
        {
            return false;
        }

        public AdminShareResult ShareFile(string path, string mimeType, string displayName, string subject, string text)
        {
            return AdminShareResult.None("Sharing files is not available on this PC build yet.");
        }

        public AdminShareResult ShareText(string subject, string text)
        {
            return AdminShareResult.None("Sharing is not available on this PC build yet.");
        }

        public bool CopyToClipboard(string label, string text)
        {
            return false;
        }

        public void ShowToast(string text)
        {
        }

        public bool ExitViaGame(bool relaunch)
        {
            return false;
        }

        public bool ExitViaHost(bool relaunch)
        {
            return false;
        }

        public string PublicDownloadsDir()
        {
            return null;
        }

        public long FreeBytes(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return -1;
                return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public List<string> MissingJniMembers()
        {
            return new List<string>();
        }

        /// <summary>Nothing yet (Day-0); the relaunch stays unconfirmed until WR2 Q9 shows the helper brings the game back.</summary>
        public AdminPlatformCapabilities Capabilities()
        {
            return new AdminPlatformCapabilities { RelaunchUnconfirmed = true };
        }

        // ------------------------------------------------------------------ IPagePlatform

        public string[] OpenModes { get { return new[] { "overlay", "system" }; } }

        /// <summary>Day-0: no route yet (null); WP2 adds the Steam overlay and the system browser (§6.5).</summary>
        public string Open(string url, string mode)
        {
            return null;
        }
    }
}
#endif
