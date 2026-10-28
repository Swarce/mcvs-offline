// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests (the device side is IAdminPlatform).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using McvsOffline.Embedded.Android;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Embedded.Admin
{
    /// <summary>
    /// <see cref="IAdminHost"/> of the embedded host (design §8.2): what the settings page needs from the game process. The
    /// order and the policy live here, platform-independent and unit-tested; <see cref="IAdminPlatform"/> performs (Android:
    /// JNI) and <see cref="GameReflection"/> reaches the game thread. Registered by <see cref="EmbeddedHost"/> with
    /// <c>Features.Set&lt;IAdminHost&gt;</c> right after <c>McvsServer.Create</c>, before the port opens.
    /// <list type="bullet">
    /// <item>The game loop stops while the in-game browser is in front, so every open and every <see cref="PageAlive"/> grants
    /// the watchdog a 45 s lease (§8.5); a share chooser gets 10 minutes (the share target sends the game to the background);
    /// <see cref="CloseBrowser"/> ends it.</item>
    /// <item><see cref="Exit"/> writes <c>logs/restart-requested.txt</c>, flushes the logs and leaves through
    /// <c>admin.restartVia</c>: the game's own restart (closing a browser still shown first), with the host's restart as the
    /// fallback when the process is still alive 3 s later; <c>none</c> does nothing.</item>
    /// <item>Capabilities follow the platform's JNI table (<see cref="AndroidJni.Available"/>); without a platform (a PC, the
    /// tests' DirectoryPlatform) every capability is false and the share methods answer <c>none</c>.</item>
    /// </list>
    /// Every member is thread-safe and never throws. The page's one-time ticket URL is never logged.
    /// </summary>
    internal sealed class AdminHost : IAdminHost
    {
        /// <summary>Watchdog lease per page open and per request of the page (it pings every 10 s).</summary>
        public const int PageLeaseMs = 45000;

        /// <summary>Watchdog lease while a share target in its own task has the foreground.</summary>
        public const int ShareLeaseMs = 600000;

        public const string PageLeaseReason = "MCVS Offline page open";
        public const string ShareLeaseReason = "share target";

        /// <summary>logs/&lt;this&gt;: "&lt;iso time&gt; &lt;reason&gt;", read and deleted at the next start.</summary>
        public const string RestartMarkerFile = "restart-requested.txt";

        /// <summary>The longest text the clipboard takes (design §7.4) and the longest shared text (§4.7).</summary>
        public const int MaxClipboardChars = 128000;
        public const int MaxShareTextChars = 100000;

        private readonly ServerContext context;
        private readonly IAdminPlatform platform;
        private readonly GameReflection game;
        private readonly Log log;
        private readonly PageOpener opener;
        private volatile string gameCredential;
        private volatile string browserStatus = "unknown";
        private int exiting;
        private long freeBytes = -1;
        private int freeBytesTick;

        public AdminHost(ServerContext context, IAdminPlatform platform, GameReflection game, Log log)
        {
            if (context == null) throw new ArgumentNullException("context");
            this.context = context;
            this.platform = platform;
            this.game = game ?? new GameReflection();
            this.log = log ?? context.Logger.For("admin");
            opener = new PageOpener(this);
            PlatformName = platform == null ? "directory" : "android";
            ExitFallbackMs = 3000;
            QuitBrowserWaitMs = 500;
            LogFlushMs = 2000;
            Sleep = ms => Thread.Sleep(ms);
        }

        // ------------------------------------------------------------------ wiring (EmbeddedHost; tests set what they need)

        public string PlatformName { get; set; }

        public string DeviceId { get; set; }

        public string GameRoot { get; set; }

        public string LogsDir { get; set; }

        public string FallbackLogsDir { get; set; }

        /// <summary>The game-thread watchdog, read at call time (it starts after the server thread); may return null.</summary>
        public Func<GameThreadWatchdog> Watchdog { get; set; }

        /// <summary>WARN/ERROR entries of the host logger (bootstrap.log, logcat) since boot.</summary>
        public MemoryLogSink Problems { get; set; }

        /// <summary>One line on the copy of the logs in Download/MCVS-Offline; may be null.</summary>
        public Func<string> MirrorStatus { get; set; }

        /// <summary>Flushes the host, server and game logs (bounded by the argument in ms).</summary>
        public Func<int, bool> FlushLogs { get; set; }

        /// <summary>The notification permission's state for the status page (NotificationPrompt.Describe); null: no row.</summary>
        public Func<string> NotificationStatus { get; set; }

        /// <summary>How long the game's restart may take before the host's restart runs (§8.8: 3 s).</summary>
        public int ExitFallbackMs { get; set; }

        /// <summary>After closing a browser that is still shown, before the game's restart.</summary>
        public int QuitBrowserWaitMs { get; set; }

        public int LogFlushMs { get; set; }

        /// <summary>Thread.Sleep (tests record or shorten it).</summary>
        public Action<int> Sleep { get; set; }

        // ------------------------------------------------------------------ shared with PageOpener and AdminHooks

        public ServerContext Context { get { return context; } }

        public IAdminPlatform Platform { get { return platform; } }

        public GameReflection Game { get { return game; } }

        public Log Log { get { return log; } }

        public PageOpener Opener { get { return opener; } }

        /// <summary>The admin module's portal, or null (module not registered).</summary>
        public IAdminPortal Portal
        {
            get
            {
                try
                {
                    return context.Features.Get<IAdminPortal>();
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>The portal exists and admin.enabled is true.</summary>
        public bool PortalEnabled
        {
            get
            {
                IAdminPortal portal = Portal;
                return portal != null && portal.Enabled;
            }
        }

        /// <summary>settings admin.openVia (server copy, read per open): auto, game, jni or post.</summary>
        public string OpenVia
        {
            get { return Choice("admin.openVia", "auto", "auto", "game", "jni", "post"); }
        }

        /// <summary>settings admin.restartVia (server copy, read per exit): game, host or none.</summary>
        public string RestartVia
        {
            get { return Choice("admin.restartVia", "game", "game", "host", "none"); }
        }

        /// <summary>Game thread: remembers what the status page shows (the game's own credential, the native browser's state).</summary>
        public void ObserveWorld(object world)
        {
            if (world == null) return;
            try
            {
                string credential = GameReflection.AnonymousCredential(world);
                if (!string.IsNullOrEmpty(credential)) gameCredential = credential;
            }
            catch (Exception ex)
            {
                log.Debug("admin: the game's anonymous credential could not be read: " + ex.GetType().Name);
            }
            try
            {
                bool? initialized = GameReflection.IsInGameBrowserInitialized(GameReflection.Browser(world));
                if (initialized.HasValue) browserStatus = initialized.Value ? "initialized" : "not initialized";
            }
            catch (Exception)
            {
            }
        }

        /// <summary>The latest lease wins; 0 ends it.</summary>
        public void Lease(int ms, string reason)
        {
            try
            {
                Func<GameThreadWatchdog> get = Watchdog;
                GameThreadWatchdog watchdog = get == null ? null : get();
                if (watchdog != null) watchdog.PauseFor(ms, reason);
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------ IAdminHost

        public AdminHostInfo Describe()
        {
            var info = new AdminHostInfo
            {
                Platform = PlatformName,
                DeviceId = DeviceId,
                GameCredential = gameCredential,
                GameRoot = GameRoot,
                LogsDir = LogsDir,
                FallbackLogsDir = FallbackLogsDir,
                BrowserStatus = browserStatus,
                FreeBytes = -1,
                RestartVia = RestartVia,
                MissingJni = new List<string>(),
                MirrorStatus = "off",
                WatchdogStatus = "off",
            };
            try
            {
                info.Graphics = McvsOffline.GraphicsHooks.Describe();
                info.HdTextures = McvsOffline.Embedded.Hd.HdTextures.Status;
                Func<string> notifications = NotificationStatus;
                if (notifications != null) info.Notifications = notifications();
            }
            catch (Exception)
            {
            }
            try
            {
                Func<string> mirror = MirrorStatus;
                string text = mirror == null ? null : mirror();
                if (!string.IsNullOrEmpty(text)) info.MirrorStatus = text;
                Func<GameThreadWatchdog> get = Watchdog;
                GameThreadWatchdog watchdog = get == null ? null : get();
                if (watchdog != null) info.WatchdogStatus = watchdog.Describe();
            }
            catch (Exception)
            {
            }
            if (platform == null)
            {
                info.RestartVia = "none";
                return info;
            }
            try
            {
                int sdk = platform.SdkInt;
                List<string> missing = platform.MissingJniMembers() ?? new List<string>();
                info.SdkInt = sdk;
                info.MissingJni = missing;
                // From Android 13 AlarmManager is expected to start no activity for an alarm, whatever the target
                // (target-sdk.md §4.6), but no device has shown it yet (STATUS device-test item 9; device test 8 passed
                // checks 1-13 only as a whole). The page says the game may close without coming back, not that it will.
                info.RelaunchUnconfirmed = sdk >= 33;
                info.PublicLogsDir = platform.PublicDownloadsDir();
                info.BrowserOpen = platform.IsInGameBrowserOpen();
                info.FreeBytes = FreeBytes();
                bool jniBrowser = AndroidJni.Available(sdk, missing, AndroidJni.Common, AndroidJni.Browser);
                info.CanOpenPage = game.IsBound || jniBrowser;
                info.CanShareFile = sdk >= 21 && AndroidJni.Available(sdk, missing, AndroidJni.ShareFileUses(sdk));
                info.CanSaveToDownloads = sdk >= 29 && AndroidJni.Available(sdk, missing, AndroidJni.Common, AndroidJni.Downloads);
                info.CanShareText = AndroidJni.Available(sdk, missing, AndroidJni.Common, AndroidJni.ShareTextUse);
                info.CanCopy = AndroidJni.Available(sdk, missing, AndroidJni.Common, AndroidJni.Clipboard);
                info.CanToast = AndroidJni.Available(sdk, missing, AndroidJni.Toast);
                info.CanSave = game.CanSave;
                string via = info.RestartVia;
                bool host = AndroidJni.Available(sdk, missing, AndroidJni.Common, AndroidJni.ExitHost);
                bool own = AndroidJni.Available(sdk, missing, AndroidJni.Browser, AndroidJni.ExitGame);
                info.CanExit = via == "game" ? own || host : via == "host" && host;
            }
            catch (Exception ex)
            {
                log.Warn("admin: describing the host failed", ex);
            }
            return info;
        }

        /// <summary>The game root volume's free space, read at most every 10 s (status is polled; StatFs is a JNI call).</summary>
        private long FreeBytes()
        {
            if (string.IsNullOrEmpty(GameRoot)) return -1;
            int now = Environment.TickCount;
            if (freeBytesTick != 0 && now - freeBytesTick < 10000) return Interlocked.Read(ref freeBytes);
            long value = platform.FreeBytes(GameRoot);
            Interlocked.Exchange(ref freeBytes, value);
            freeBytesTick = now == 0 ? 1 : now;
            return value;
        }

        public bool OpenPage(string url, string trigger)
        {
            return opener.OpenFromServer(url, trigger);
        }

        public AdminShareResult ShareFile(string path, string mimeType, string displayName, string subject, string text)
        {
            try
            {
                if (platform == null) return AdminShareResult.None("Sharing files is not available here.");
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return AdminShareResult.None("The file to share is missing.");
                if (!IsSafeFileName(displayName) || string.IsNullOrEmpty(mimeType)) return AdminShareResult.None("The file name cannot be shared.");
                bool chooser = subject != null;
                if (chooser) Lease(ShareLeaseMs, ShareLeaseReason);
                AdminShareResult result = platform.ShareFile(path, mimeType, displayName, subject, text) ??
                                          AdminShareResult.None("The file could not be shared.");
                if (chooser && result.Method != "sheet") Lease(PageLeaseMs, PageLeaseReason);
                log.Info("admin: share " + displayName + " (" + mimeType + "): " + (result.Ok ? "ok" : "failed") + ", " + result.Method +
                         (result.PublicPath == null ? "" : ", " + result.PublicPath));
                return result;
            }
            catch (Exception ex)
            {
                log.Warn("admin: sharing a file failed", ex);
                Lease(PageLeaseMs, PageLeaseReason);
                return AdminShareResult.None("The file could not be shared.");
            }
        }

        public AdminShareResult ShareText(string subject, string text)
        {
            try
            {
                if (platform == null) return AdminShareResult.None("Sharing is not available here.");
                if (string.IsNullOrEmpty(text)) return AdminShareResult.None("There is nothing to share.");
                if (text.Length > MaxShareTextChars) text = text.Substring(0, MaxShareTextChars);
                Lease(ShareLeaseMs, ShareLeaseReason);
                AdminShareResult result = platform.ShareText(subject ?? "MCVS Offline", text) ?? AdminShareResult.None("The text could not be shared.");
                // Not Ok but "text": the sheet was started and may still open (the UI thread ran late), so the share lease stays.
                if (!result.Ok && result.Method != "text") Lease(PageLeaseMs, PageLeaseReason);
                return result;
            }
            catch (Exception ex)
            {
                log.Warn("admin: sharing a text failed", ex);
                Lease(PageLeaseMs, PageLeaseReason);
                return AdminShareResult.None("The text could not be shared.");
            }
        }

        public bool CopyToClipboard(string label, string text)
        {
            try
            {
                if (platform == null || text == null || text.Length > MaxClipboardChars) return false;
                return platform.CopyToClipboard(label ?? "MCVS Offline", text);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void ShowToast(string text)
        {
            try
            {
                if (platform != null && !string.IsNullOrEmpty(text)) platform.ShowToast(text);
            }
            catch (Exception)
            {
            }
        }

        public bool CloseBrowser()
        {
            bool closed = false;
            try
            {
                if (platform != null) closed = platform.QuitInGameBrowser();
            }
            catch (Exception)
            {
            }
            Lease(0, null);
            return closed;
        }

        public bool WaitForGameLoop(int timeoutMs)
        {
            try
            {
                return game.WaitForFrame(timeoutMs);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool RequestGameSave()
        {
            try
            {
                return game.RequestSave(log);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool Exit(string reason, bool relaunch)
        {
            try
            {
                string via = RestartVia;
                if (platform == null || via == "none")
                {
                    log.Warn("admin: exit requested (" + SafeReason(reason) + ") but " + (platform == null ? "this host cannot end the game" : "admin.restartVia is none"));
                    return false;
                }
                if (Interlocked.Exchange(ref exiting, 1) != 0)
                {
                    log.Warn("admin: exit already started");
                    return true;
                }
                WriteRestartMarker(reason);
                log.Info("admin: exit (" + SafeReason(reason) + ", relaunch " + (relaunch ? "yes" : "no") + ", via " + via + ")");
                Flush();
                bool started = via == "game" ? ExitThroughGame(relaunch) : ExitThroughHost(relaunch);
                if (!started)
                {
                    Interlocked.Exchange(ref exiting, 0);
                    DeleteRestartMarker();
                }
                return started;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref exiting, 0);
                log.Warn("admin: exit failed", ex);
                return false;
            }
        }

        public void PageAlive()
        {
            Lease(PageLeaseMs, PageLeaseReason);
        }

        public List<string> RecentProblems(int max)
        {
            var list = new List<string>();
            try
            {
                MemoryLogSink sink = Problems;
                if (sink == null || max <= 0) return list;
                List<LogEntry> entries = sink.Snapshot();
                for (int i = entries.Count - 1; i >= 0 && list.Count < max; i--)
                {
                    if (entries[i].Level >= LogLevel.Warn) list.Add(entries[i].Format());
                }
            }
            catch (Exception)
            {
            }
            return list;
        }

        // ------------------------------------------------------------------ exit paths (§8.8)

        /// <summary>The game's restart; the host's when the game's cannot start, or when the process outlives it by ExitFallbackMs.</summary>
        private bool ExitThroughGame(bool relaunch)
        {
            if (platform.IsInGameBrowserOpen())
            {
                // MainActivity must be resumed for finish() to run onPause → c() (the relaunch alarm and the kill).
                platform.QuitInGameBrowser();
                Sleep(QuitBrowserWaitMs);
            }
            // False means it never runs (a call the UI thread had not started is withdrawn): only one restart path goes ahead.
            if (!platform.ExitViaGame(relaunch))
            {
                log.Warn("admin: the game's restart is not available; using the host's restart");
                return ExitThroughHost(relaunch);
            }
            var fallback = new Thread(() =>
            {
                try
                {
                    Sleep(ExitFallbackMs);
                    log.Warn("admin: the process is still alive " + ExitFallbackMs.ToString(CultureInfo.InvariantCulture) +
                             " ms after the game's restart; using the host's restart");
                    Flush();
                    platform.ExitViaHost(relaunch);
                }
                catch (Exception)
                {
                }
            });
            fallback.IsBackground = true;
            fallback.Name = "mcvs-exit-fallback";
            fallback.Start();
            return true;
        }

        private bool ExitThroughHost(bool relaunch)
        {
            return platform.ExitViaHost(relaunch);
        }

        private void Flush()
        {
            try
            {
                Func<int, bool> flush = FlushLogs;
                if (flush != null) flush(LogFlushMs);
            }
            catch (Exception)
            {
            }
        }

        private string MarkerPath
        {
            get { return string.IsNullOrEmpty(LogsDir) ? null : Path.Combine(LogsDir, RestartMarkerFile); }
        }

        private void WriteRestartMarker(string reason)
        {
            string path = MarkerPath;
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(LogsDir);
                AtomicFile.WriteAllText(path, TimeFormat.Iso8601Millis(DateTime.UtcNow) + " " + SafeReason(reason) + "\n");
            }
            catch (Exception ex)
            {
                log.Warn("admin: cannot write " + path, ex);
            }
        }

        private void DeleteRestartMarker()
        {
            try
            {
                string path = MarkerPath;
                if (path != null && File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// At start: the reason of a restart requested from the page in the previous process (the marker is deleted), or null.
        /// "settings", "import", "user"... ("unknown" for an unreadable marker).
        /// </summary>
        public static string TakeRestartMarker(string logsDir)
        {
            try
            {
                if (string.IsNullOrEmpty(logsDir)) return null;
                string path = Path.Combine(logsDir, RestartMarkerFile);
                if (!File.Exists(path)) return null;
                string text = AtomicFile.ReadAllTextOrNull(path) ?? "";
                File.Delete(path);
                text = text.Trim();
                int space = text.IndexOf(' ');
                string reason = space < 0 ? "" : SafeReason(text.Substring(space + 1));
                return reason.Length == 0 ? "unknown" : reason;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>[a-z0-9-], at most 20 characters, "unknown" when empty (reasons and triggers in logs and files).</summary>
        internal static string SafeReason(string text)
        {
            if (string.IsNullOrEmpty(text)) return "unknown";
            var chars = new char[Math.Min(text.Length, 20)];
            int n = 0;
            foreach (char c in text)
            {
                if (n == chars.Length) break;
                char lower = char.ToLowerInvariant(c);
                if ((lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9') || lower == '-') chars[n++] = lower;
            }
            return n == 0 ? "unknown" : new string(chars, 0, n);
        }

        /// <summary>A display name for Download/MCVS-Offline: [A-Za-z0-9._-], 1-128 characters, not starting with '.'.</summary>
        internal static bool IsSafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 128 || name[0] == '.') return false;
            foreach (char c in name)
            {
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')) return false;
            }
            return true;
        }

        private string Choice(string key, string fallback, params string[] allowed)
        {
            try
            {
                string value = (context.Settings.GetString(key, fallback) ?? fallback).Trim().ToLowerInvariant();
                return Array.IndexOf(allowed, value) >= 0 ? value : fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}
