#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using McvsOffline.Embedded.Admin;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>
    /// The Steam (PC) build's platform (docs/design/pc-port.md §6.4): the game folder is the game root (D1), the system log is
    /// OutputDebugString, the logs are plain files, the page opens in the Steam overlay or the system browser (D2), "share" means
    /// McvsOffline\exports plus Explorer, the clipboard is user32, and the exit is the game's own quit plus a relaunch helper
    /// (C18). Not an <c>IInstallerPlatform</c> (no Java installer) and not an <c>INotificationPlatform</c>. The test seams
    /// (<see cref="GameRoot"/>, <see cref="Steam"/>, ...) are internal; the game uses the parameterless defaults.
    /// </summary>
    public sealed class WindowsPlatform : IEmbeddedPlatform, IPlatformDiagnostics, IAdminPlatform, IPagePlatform, IHostExtensions
    {
        /// <summary>Overrides the game root (tests; a game started outside its folder).</summary>
        public const string GameRootVariable = GameRootLocator.Variable;

        private readonly object gate = new object();
        private volatile EmbeddedHost host;
        private volatile Log platformLog;
        private SteamOverlay overlay;
        private SystemBrowser browser;
        private WindowsShare share;
        private WindowsClipboard clipboard;
        private WindowsExit exit;
        private volatile string installLine;

        // ------------------------------------------------------------------ test seams (null: the real thing)

        /// <summary>The game root instead of <see cref="GameRootLocator"/> (tests).</summary>
        internal string GameRoot { get; set; }

        internal ISteamApi Steam { get; set; }

        /// <summary>Starts the system browser for a URL (tests record it instead).</summary>
        internal Action<string> BrowserLauncher { get; set; }

        /// <summary>(program, arguments): Explorer and the relaunch helper (tests record them).</summary>
        internal Action<string, string> ProcessStarter { get; set; }

        internal IClipboardNative ClipboardNative { get; set; }

        internal Func<IList<ThreadSample>> ThreadSource { get; set; }

        /// <summary>Ends the process on the host's exit path (tests record it).</summary>
        internal Action Kill { get; set; }

        /// <summary>The runtime facts for the "[boot] runtime:" line (tests fix them).</summary>
        internal Func<RuntimeFacts> Runtime { get; set; }

        /// <summary>Runs the install check of §6.2 on a background thread (default true; tests turn it off or wait for it).</summary>
        internal bool CheckInstall { get; set; } = true;

        /// <summary>Set when the install check finished (tests).</summary>
        internal ManualResetEvent InstallChecked { get; } = new ManualResetEvent(false);

        // ------------------------------------------------------------------ IEmbeddedPlatform

        public string Name { get { return "windows"; } }

        public string EntryPoint { get { return "EngineBridge.Class1.Start"; } }

        public string ArgsReader { get { return "MainLoop_Start"; } }

        public string PortInUseHint
        {
            get
            {
                return "Another program holds the port, or Windows reserves it (Hyper-V/WSL excluded port ranges: " +
                       "netsh int ipv4 show excludedportrange protocol=tcp); pick a free port with 'McvsOffline.PcKit install --port <n>', " +
                       "which sets 'port' in McvsOffline\\settings.json (the port the server uses) and in the patched game.";
            }
        }

        /// <summary>The exe folder that holds Data\MCVS.game (<see cref="GameRootLocator"/>). Throws when there is none.</summary>
        public string ResolveGameRoot(Log log)
        {
            string root = GameRoot;
            if (!string.IsNullOrEmpty(root)) return GameRootLocator.Locate(root, null, null, File.Exists);
            return GameRootLocator.LocateForProcess();
        }

        /// <summary>OutputDebugString (a GUI-subsystem process has no console; DebugView shows it).</summary>
        public LogSink CreateSystemLogSink(LogLevel minLevel)
        {
            return new DebugOutputSink(minLevel);
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
                                       (int)settings.GetLong("gameLog.maxMessageChars", 8192), true);
#else
            return "game log: bridge not compiled in";
#endif
        }

        public string JavaStacks(string gameThreadComm)
        {
            return null;
        }

        /// <summary>Nothing (the logs are plain files; no phone-readable copy, no exit reasons).</summary>
        public void StartBackground(EmbeddedHost embeddedHost)
        {
        }

        /// <summary>"steam running yes, overlay enabled yes" and the install check's line once it ran.</summary>
        public string StatusExtras()
        {
            try
            {
                SteamOverlay o = Overlay();
                string text = "steam running " + YesNo(o.IsSteamRunning()) + ", overlay enabled " + YesNo(o.IsEnabled()) + "; " + PcHooks.Describe();
                string install = installLine;
                if (install != null) text += "; " + install;
                return text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string YesNo(bool? value)
        {
            return value.HasValue ? (value.Value ? "yes" : "no") : "unknown";
        }

        // ------------------------------------------------------------------ IHostExtensions

        /// <summary>PcHooks, the "[boot] runtime:" line and the install check (background).</summary>
        public void Configure(EmbeddedHost embeddedHost)
        {
            if (embeddedHost == null) return;
            host = embeddedHost;
            platformLog = embeddedHost.Logger.For("windows");
            try
            {
                PcHooks.Configure(embeddedHost.Logger.For("pc"), embeddedHost.Boot);
            }
            catch (Exception ex)
            {
                Warn("the PC hooks could not be configured", ex);
            }
            try
            {
                Func<RuntimeFacts> facts = Runtime;
                embeddedHost.Boot.Mark(RuntimeInfo.Format(facts != null ? facts() : RuntimeInfo.Read()));
            }
            catch (Exception ex)
            {
                Warn("the runtime could not be described", ex);
            }
            if (CheckInstall) StartInstallCheck(embeddedHost);
        }

        public IThreadSnapshot ThreadSnapshot()
        {
            return new WindowsThreads(ThreadSource);
        }

        public Action WatchAssemblyLoads(Action<Assembly> loaded)
        {
            if (loaded == null) return null;
            AssemblyLoadEventHandler handler = (sender, args) =>
            {
                try
                {
                    loaded(args.LoadedAssembly);
                }
                catch (Exception)
                {
                }
            };
            AppDomain.CurrentDomain.AssemblyLoad += handler;
            return () => AppDomain.CurrentDomain.AssemblyLoad -= handler;
        }

        private void StartInstallCheck(EmbeddedHost embeddedHost)
        {
            try
            {
                string root = embeddedHost.Paths.GameRoot;
                string dataDir = embeddedHost.Paths.DataDir;
                BootTrace boot = embeddedHost.Boot;
                var thread = new Thread(() =>
                {
                    try
                    {
                        InstallCheck.Result result = InstallCheck.Run(root, dataDir);
                        installLine = result.Line;
                        if (result.Stale) boot.Warn(result.Line);
                        else boot.Mark(result.Line);
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        InstallChecked.Set();
                    }
                });
                thread.IsBackground = true;
                thread.Name = "mcvs-install-check";
                thread.Start();
            }
            catch (Exception ex)
            {
                Warn("the install check could not start", ex);
                InstallChecked.Set();
            }
        }

        // ------------------------------------------------------------------ IAdminPlatform

        public int SdkInt { get { return 0; } }

        /// <summary>Kept for the interface; PageOpener uses <see cref="Open"/>.</summary>
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

        /// <summary>McvsOffline\exports\&lt;displayName&gt;, and Explorer unless subject is null (there is no share sheet on PC).</summary>
        public AdminShareResult ShareFile(string path, string mimeType, string displayName, string subject, string text)
        {
            try
            {
                return Share().ShareFile(path, displayName, subject);
            }
            catch (Exception ex)
            {
                Warn("admin: sharing a file failed", ex);
                return AdminShareResult.None("The file could not be shared.");
            }
        }

        /// <summary>McvsOffline\exports\&lt;subject&gt;.txt and Explorer.</summary>
        public AdminShareResult ShareText(string subject, string text)
        {
            try
            {
                return Share().ShareText(subject, text);
            }
            catch (Exception ex)
            {
                Warn("admin: sharing a text failed", ex);
                return AdminShareResult.None("The text could not be shared.");
            }
        }

        public bool CopyToClipboard(string label, string text)
        {
            try
            {
                return Clipboard().SetText(text);
            }
            catch (Exception ex)
            {
                Warn("admin: copying failed", ex);
                return false;
            }
        }

        /// <summary>No toast on PC: the text goes to the log.</summary>
        public void ShowToast(string text)
        {
            try
            {
                Log log = platformLog;
                if (log != null) log.Info("admin: toast (not shown on PC): " + text);
            }
            catch (Exception)
            {
            }
        }

        public bool ExitViaGame(bool relaunch)
        {
            return Exit().ViaGame(relaunch);
        }

        public bool ExitViaHost(bool relaunch)
        {
            return Exit().ViaHost(relaunch);
        }

        /// <summary>&lt;DataDir&gt;\exports, or null before the host knows the game folder.</summary>
        public string PublicDownloadsDir()
        {
            EmbeddedHost h = host ?? EmbeddedHost.Current;
            return h == null || h.Paths == null ? null : Path.Combine(h.Paths.DataDir, WindowsShare.FolderName);
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

        /// <summary>
        /// Everything but the toast; the relaunch stays unconfirmed until WR2 Q9 shows the helper brings the game back.
        /// </summary>
        public AdminPlatformCapabilities Capabilities()
        {
            return new AdminPlatformCapabilities
            {
                OpenPage = true,
                ShareFile = true,
                SaveToDownloads = true,
                ShareText = true,
                Copy = true,
                Toast = false,
                ExitViaGame = true,
                ExitViaHost = true,
                RelaunchUnconfirmed = true,
                OpenInBrowser = true,
            };
        }

        // ------------------------------------------------------------------ IPagePlatform

        public string[] OpenModes { get { return new[] { "overlay", "system" }; } }

        /// <summary>
        /// auto: the Steam overlay when it can be asked, else the system browser; overlay / system: that route only. The route used
        /// ("overlay", "system") or null. Returns quickly (the browser starts on its own thread). Never throws.
        /// </summary>
        public string Open(string url, string mode)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return null;
                switch (mode)
                {
                    case "overlay":
                        return Overlay().TryOpen(url) ? "overlay" : null;
                    case "system":
                        return Browser().Open(url) ? "system" : null;
                    default:
                        if (Overlay().TryOpen(url)) return "overlay";
                        return Browser().Open(url) ? "system" : null;
                }
            }
            catch (Exception ex)
            {
                Warn("admin: opening the page failed (" + mode + ")", ex);
                return null;
            }
        }

        // ------------------------------------------------------------------ parts (created on first use)

        private SteamOverlay Overlay()
        {
            lock (gate) return overlay ?? (overlay = new SteamOverlay(Steam, s => Warn(s, null)));
        }

        private SystemBrowser Browser()
        {
            lock (gate) return browser ?? (browser = new SystemBrowser(BrowserLauncher, s => Warn(s, null)));
        }

        private WindowsShare Share()
        {
            lock (gate) return share ?? (share = new WindowsShare(PublicDownloadsDir, ProcessStarter, s => Warn(s, null)));
        }

        private WindowsClipboard Clipboard()
        {
            lock (gate) return clipboard ?? (clipboard = new WindowsClipboard(ClipboardNative, s => Warn(s, null)));
        }

        private WindowsExit Exit()
        {
            lock (gate)
            {
                if (exit != null) return exit;
                exit = new WindowsExit
                {
                    Post = action =>
                    {
                        EmbeddedHost h = host ?? EmbeddedHost.Current;
                        AdminHost admin = h == null ? null : h.AdminHost;
                        GameReflection game = admin != null ? admin.Game : GameReflection.Load();
                        return game.Post(action, platformLog);
                    },
                    FlushLogsNow = ms =>
                    {
                        EmbeddedHost h = host ?? EmbeddedHost.Current;
                        if (h != null) h.FlushLogsNow(ms);
                    },
                    Warn = Warn,
                };
                if (ProcessStarter != null) exit.StartProcess = ProcessStarter;
                if (Kill != null) exit.Kill = Kill;
                return exit;
            }
        }

        private void Warn(string message, Exception ex)
        {
            try
            {
                Log log = platformLog;
                if (log != null) log.Warn(message, ex);
                else SelfLog.Write(SelfLog.Warn, message + (ex == null ? "" : ": " + ex));
            }
            catch (Exception)
            {
            }
        }
    }
}
#endif
