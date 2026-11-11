// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using McvsOffline.Embedded.Admin;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Embedded.Graphics;
#if !MCVS_WINDOWS
using McvsOffline.Embedded.Hd;
#endif
using McvsOffline.Embedded.Notifications;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Embedded
{
    /// <summary>Knobs for <see cref="EmbeddedHost.Start"/>; the defaults are what the game uses.</summary>
    public sealed class EmbeddedHostOptions
    {
        public EmbeddedHostOptions()
        {
            StartAttempts = 15;
            RetryDelayMs = 2000;
            SystemLogLevel = LogLevel.Info;
            Watchdog = true;
            StatusIntervalMs = 60000;
            PortWatchIntervalMs = 2000;
        }

        /// <summary>Start the game-thread watchdog (settings diagnostics.watchdog can still turn it off).</summary>
        public bool Watchdog { get; set; }

        /// <summary>How often the status line (log files, boot trace, watchdog) is logged during the first 10 minutes; 0: never.</summary>
        public int StatusIntervalMs { get; set; }

        /// <summary>Overrides settings.port for this run (tests). Never 0: args.txt needs the real port up front.</summary>
        public int? Port { get; set; }

        /// <summary>Overrides the LocalAnubis Finder's default port 51750 for this run (tests).</summary>
        public int? FinderPort { get; set; }

        /// <summary>Overrides the bind address for this run (tests).</summary>
        public string Bind { get; set; }

        /// <summary>
        /// How often the server thread tries to bind the configured port when it could not be reserved before args.txt was
        /// written: with <c>embedded.portFallback</c> false (the port is pinned) or when no port at all could be bound. With the
        /// fallback on (the default) a busy port costs no retries: another port is used at once (docs/design/port-fallback.md).
        /// </summary>
        public int StartAttempts { get; set; }

        public int RetryDelayMs { get; set; }

        /// <summary>
        /// Binds and listens for the port reservation, with the address-reuse warning callback (tests; null: HttpServer.Listen).
        /// </summary>
        public Func<IPAddress, int, Action<string>, Socket> Listen { get; set; }

        /// <summary>
        /// How often the host checks whether the configured port came free while it defers to another copy of the game's server on
        /// the same data folder (then its own server takes the port over; docs/design/port-fallback.md §2).
        /// </summary>
        public int PortWatchIntervalMs { get; set; }

        /// <summary>Asks the program that holds a busy port what it is (tests: longer budgets on slow machines); null = <see cref="PortProbe"/>'s defaults.</summary>
        public Func<int, PortHolder> ProbePort { get; set; }

        public LogLevel SystemLogLevel { get; set; }

        /// <summary>Replaces the service modules (tests).</summary>
        public Action<Router, ServerContext> RegisterServices { get; set; }
    }

    /// <summary>
    /// Starts the offline backend inside the game process. Called once by <see cref="McvsOffline.Bootstrap.Init"/>
    /// from the patched <c>NicePlaySDKDLL.OnInitialize</c>, i.e. on the UI thread in
    /// <c>XamarinMainActivity.OnCreate</c>, before the Java GameInstaller and long before the native engine reads
    /// args.txt at <c>JNIBridge.NativeInit</c> (verify-C7 §5, verify-C1 §1.6). So:
    /// <list type="number">
    /// <item>synchronously (milliseconds): resolve the game root, create McvsOffline/, load settings.json and
    /// write the managed block of args.txt, so it is in place whatever happens next;</item>
    /// <item>still synchronously, before args.txt: reserve the server's port, 127.0.0.1:51780 (all interfaces in LAN mode). When
    /// it is busy, ask the holder (at most about 0.75 s): a working MCVS Encore server is used as it is (the dev loop's adb
    /// reverse), anything else makes this start listen on the next free port (51781..51789, then any), and args.txt, the eve
    /// URLs, the settings page and the 'geo' lookup follow it (docs/design/port-fallback.md). The LocalAnubis Finder's port
    /// (51750) is checked the same way;</item>
    /// <item>on a background thread: build the server (ServiceRegistry) on the reserved socket.</item>
    /// </list>
    /// Nothing here throws: every failure is logged (logcat + McvsOffline/logs/bootstrap.log) and the game boots anyway.
    /// <para>On PC (a platform named "windows", docs/design/pc-port.md §6.2) the same runs from the patched
    /// <c>EngineBridge.Class1.Start</c>, with these differences, chosen at run time from the platform (so the net8 tests see them):
    /// no install.*, hd.* or notifications.* defaults and <c>embedded.physics.keepPhysicsLayer</c> instead; <c>lanMode</c> forced
    /// off; GraphicsHooks on GameOptions_windows.json; a WARN for a <c>*_args.txt</c> that makes the engine ignore args.txt; no
    /// thread dump; and the platform's <see cref="IHostExtensions"/> (PcHooks, the runtime line, the watchdog's thread snapshot,
    /// the lazy patch probe's assembly-load subscription).</para>
    /// </summary>
    public sealed class EmbeddedHost
    {
        private readonly ManualResetEvent serverReady = new ManualResetEvent(false);
        private volatile McvsServer server;
        private volatile Exception serverError;
        private LogSink systemSink;
        private MemoryLogSink earlySink;
        private FileLogSink bootstrapSink;
        private SafeLogFile gameLogFile;
        private IPlatformDiagnostics diagnostics;
        private IAdminPlatform adminPlatform;
        private INotificationPlatform notificationPlatform;
        private string platformName = "directory";
        private string argsReader = "nobody";
        private string portInUseHint = "";
        private MemoryLogSink problemSink;
        private volatile AdminHost adminHost;
        private string fallbackLogDir;
        private bool isWindows;
        private IHostExtensions extensions;
        private volatile string argsWarning;
        private DateTime startedUtc;
        private IPAddress reservedAddress;
        private readonly ManualResetEvent portWatchStop = new ManualResetEvent(false);
        private volatile bool portWatching;
        private int finderPreferred = PortHooks.DefaultFinderPort;
        private bool finderMoved;

        private EmbeddedHost(Logger logger, Log log)
        {
            Logger = logger;
            Log = log;
        }

        /// <summary>The host started by <see cref="McvsOffline.Bootstrap.Init"/> (null before, or if it failed early).</summary>
        public static EmbeddedHost Current { get; private set; }

        /// <summary>Bootstrap logger (system log + bootstrap.log); the server has its own logger and server.log.</summary>
        public Logger Logger { get; private set; }

        public Log Log { get; private set; }

        public EmbeddedPaths Paths { get; private set; }

        public Settings Settings { get; private set; }

        /// <summary>Result of the args.txt merge, or null if it was skipped or failed.</summary>
        public ArgsMergeResult Args { get; private set; }

        public string DeviceId { get; private set; }

        /// <summary>The port the game uses for the offline server this start (the bound one; the configured one when deferring).</summary>
        public int Port { get; private set; }

        /// <summary>settings.port (or EmbeddedHostOptions.Port): the port the host prefers.</summary>
        public int ConfiguredPort { get; private set; }

        /// <summary>How the server port was chosen this start, or null when Prepare did not get that far.</summary>
        public PortReservation PortReservation { get; private set; }

        /// <summary>The LocalAnubis Finder port the game uses this start (51750 unless it was busy or the player set another).</summary>
        public int FinderPort { get; private set; }

        /// <summary>
        /// The Finder port the patched client contacts ('finderport' hook): <see cref="FinderPort"/>, except on a LAN guest
        /// (-RemoteIp) whose own Finder was moved: the client then still looks for the host's Finder on the unmoved port.
        /// </summary>
        public int ClientFinderPort { get; private set; }

        /// <summary>True while the host defers to another copy's server on its data folder and waits to take the port over.</summary>
        public bool WatchingPort { get { return portWatching; } }

        /// <summary>The boot trace ("[boot]" milestones), shared with the server (it adds the request milestones).</summary>
        public BootTrace Boot { get; private set; }

        /// <summary>The game-thread watchdog, or null when it is off.</summary>
        public GameThreadWatchdog Watchdog { get; private set; }

        /// <summary>The log retention of this start (settings logging.keepStarts, logs/starts.txt), or null before the settings are read.</summary>
        internal LogRetention Retention { get; private set; }

        /// <summary>Waits until the log retention thread has finished (tests); true when there is none.</summary>
        public bool WaitLogRetention(int timeoutMs)
        {
            LogRetention retention = Retention;
            return retention == null || retention.Wait(timeoutMs);
        }

        /// <summary>The copy of the game's own log entries (logs/game.log), or null.</summary>
        public SafeLogFile GameLogFile { get { return gameLogFile; } }

        /// <summary>logs/bootstrap.log, or null.</summary>
        public SafeLogFile BootstrapLogFile { get { return bootstrapSink == null ? null : bootstrapSink.File; } }

        /// <summary>The log files a phone-readable copy should include (existing or not).</summary>
        public string[] LogFiles
        {
            get
            {
                if (Paths == null) return new string[0];
                return new[] { Paths.BootstrapLog, Path.Combine(Paths.LogsDir, "server.log"), Path.Combine(Paths.LogsDir, "game.log") };
            }
        }

        /// <summary>Waits until every log line written so far is in its file (tests, orderly shutdown).</summary>
        public bool FlushLogs(int timeoutMs)
        {
            bool ok = Logger.Flush(timeoutMs);
            McvsServer s = server;
            if (s != null) ok &= s.Context.Logger.Flush(timeoutMs);
            if (gameLogFile != null) ok &= gameLogFile.Flush(timeoutMs);
            return ok;
        }

        /// <summary>Crash path: writes the queued lines on the calling thread (bounded); never throws.</summary>
        public void FlushLogsNow(int budgetMs)
        {
            try
            {
                Logger.FlushNow(budgetMs);
                McvsServer s = server;
                if (s != null) s.Context.Logger.FlushNow(budgetMs);
                if (gameLogFile != null) gameLogFile.FlushNow(budgetMs);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Context for crash reports and the status line: boot trace, watchdog, log files.</summary>
        public string DescribeState()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                if (Boot != null) sb.Append("boot: ").Append(Boot.Describe()).Append('\n');
                if (Watchdog != null) sb.Append("watchdog: ").Append(Watchdog.Describe()).Append('\n');
                if (bootstrapSink != null) sb.Append("log ").Append(bootstrapSink.File.Describe()).Append('\n');
                McvsServer s = server;
                if (s != null)
                {
                    foreach (string line in s.DescribeLogFiles()) sb.Append("log ").Append(line).Append('\n');
                }
                if (gameLogFile != null) sb.Append("log ").Append(gameLogFile.Describe()).Append('\n');
                sb.Append("native online SDK init calls: ").Append(GameHooks.NativeOnlineCalls)
                  .Append(", headless-World exceptions: ").Append(GameHooks.HeadlessReports)
                  .Append(", store items skipped: ").Append(GameHooks.StoreReports).Append('\n');
                sb.Append(GameHooks.DescribePhysics());
                sb.Append('\n').Append(GameHooks.DescribeBots());
                sb.Append('\n').Append(GameDataHooks.Describe());
                sb.Append('\n').Append(GraphicsHooks.Describe());
                string args = argsWarning;
                if (args != null) sb.Append('\n').Append(args);
                PortReservation ports = PortReservation;
                if (ports != null && ports.Outcome != PortOutcome.Configured)
                    sb.Append("\nport: ").Append(ports.Outcome).Append(", ").Append(ports.Port).Append(" (configured ").Append(ConfiguredPort).Append("): ").Append(ports.Reason);
                if (FinderPort != PortHooks.DefaultFinderPort) sb.Append("\nLocalAnubis Finder port: ").Append(FinderPort);
                if (ClientFinderPort != FinderPort) sb.Append(" (the client contacts ").Append(ClientFinderPort).Append(": LAN guest)");
#if !MCVS_WINDOWS
                sb.Append('\n').Append(HdInstaller.Status);
#endif
                string extras = diagnostics == null ? null : diagnostics.StatusExtras();
                if (!string.IsNullOrEmpty(extras)) sb.Append('\n').Append(extras);
            }
            catch (Exception)
            {
            }
            return sb.ToString();
        }

        /// <summary>The running server, or null while starting / after a failure.</summary>
        public McvsServer Server { get { return server; } }

        /// <summary>The settings page's view of this host (registered as the server's IAdminHost), or null before the server exists.</summary>
        internal AdminHost AdminHost { get { return adminHost; } }

        /// <summary>The lazy probe of the client's patches (the instance the server reads is its Target), or null before the server thread.</summary>
        public PatchWatcher Patches { get; private set; }

        /// <summary>One line on the phone-readable copy of the logs (set by the platform's diagnostics), or null.</summary>
        public Func<string> MirrorStatus { get; set; }

        /// <summary>Why the server could not start, or null.</summary>
        public Exception ServerError { get { return serverError; } }

        /// <summary>Waits until the server thread has finished (started or given up).</summary>
        public bool WaitForServer(int timeoutMs)
        {
            return serverReady.WaitOne(timeoutMs, false);
        }

        /// <summary>Runs both phases; never throws. Returns null only if not even a logger could be created.</summary>
        public static EmbeddedHost Start(IEmbeddedPlatform platform, EmbeddedHostOptions options)
        {
            BootClock.Touch();
            // Before anything of this process can write in logs/: every file it writes is at or after this time (LogRetention).
            DateTime startedUtc = DateTime.UtcNow;
            options = options ?? new EmbeddedHostOptions();
            EmbeddedHost host;
            try
            {
                var logger = new Logger();
                LogSink system = null;
                try
                {
                    system = platform.CreateSystemLogSink(options.SystemLogLevel);
                }
                catch (Exception)
                {
                }
                if (system != null) logger.AddSink(system);
                // Lines logged before the game folder (and so bootstrap.log) is known; replayed into the file.
                var early = new MemoryLogSink(LogLevel.Debug, 200);
                logger.AddSink(early);
                host = new EmbeddedHost(logger, logger.For("embedded"));
                host.systemSink = system;
                host.earlySink = early;
                host.startedUtc = startedUtc;
            }
            catch (Exception)
            {
                return null;
            }

            Current = host;
            try
            {
                host.Prepare(platform, options);
            }
            catch (Exception ex)
            {
                host.Log.Error("MCVS Encore could not prepare the offline backend; the game will start without it", ex);
                host.serverReady.Set();
            }
            return host;
        }

        private void Prepare(IEmbeddedPlatform platform, EmbeddedHostOptions options)
        {
            // Design §5.4: bootstrap.log, logcat and the problem list never carry a page ticket, a cookie or a password; the
            // server's own logger gets the same redactor in McvsServer.Create.
            Logger.Redactor = LogRedaction.Redact;
            // WARN/ERROR lines of this logger for the settings page's "recent problems" (IAdminHost.RecentProblems).
            problemSink = new MemoryLogSink(LogLevel.Warn, 100);
            Logger.AddSink(problemSink);
            Log.Info("MCVS Encore " + McvsVersion.Current + " embedded host starting (platform " + platform.Name + HostProcess.Suffix(", ") + ")");
            platformName = platform.Name;
            argsReader = platform.ArgsReader;
            portInUseHint = platform.PortInUseHint;
            diagnostics = platform as IPlatformDiagnostics;
            isWindows = platformName == "windows";
            extensions = platform as IHostExtensions;
            if (CrashReporter.Hooks != null) Logger.For("crash").Info("hooks: " + CrashReporter.Hooks);
            Boot = new BootTrace(Logger.For("boot"));
            Boot.Mark("MCVS Encore host started by the game (" + platform.EntryPoint + ")");

            string root = platform.ResolveGameRoot(Log);
            Paths = new EmbeddedPaths(root);
            CrashReporter.SetCrashDirectory(Paths.LogsDir);
            CrashReporter.FlushLogs = FlushLogsNow;
            CrashReporter.Context = DescribeState;
            try
            {
                Directory.CreateDirectory(Paths.LogsDir);
            }
            catch (Exception ex)
            {
                Log.Warn("cannot create " + Paths.LogsDir + " (the log files fall back to internal storage)", ex);
            }
            if (diagnostics != null)
            {
                string internalDir = diagnostics.FallbackLogDirectory();
                if (!string.IsNullOrEmpty(internalDir)) fallbackLogDir = Path.Combine(Path.Combine(internalDir, EmbeddedPaths.DataFolderName), "logs");
            }
            try
            {
                bootstrapSink = new FileLogSink(Paths.BootstrapLog, LogLevel.Debug, 512 * 1024, 3, LogFileOptions());
                foreach (LogEntry entry in earlySink.Snapshot()) bootstrapSink.Write(entry);
                Logger.AddSink(bootstrapSink);
            }
            catch (Exception ex)
            {
                Log.Warn("cannot open " + Paths.BootstrapLog, ex);
            }
            finally
            {
                Logger.RemoveSink(earlySink);
            }
            Log.Info("game root " + Paths.GameRoot + "; data directory " + Paths.DataDir);
            string restartReason = AdminHost.TakeRestartMarker(Paths.LogsDir);
            if (restartReason != null) Boot.Mark("previous exit: restart requested from the settings page (" + restartReason + ")");

            Settings = Settings.Load(Paths.SettingsFile, Logger.For("settings"));
            AddEmbeddedDefaults(platform);
            if (isWindows)
            {
                // LAN is not supported on PC (docs/design/pc-port.md X): the host's copy here, the server's in RunServer.
                if (Settings.LanMode) Log.Warn("settings lanMode is true, but LAN play is not supported on PC: running without it");
                Settings.Override("lanMode", false);
                MirrorStatus = () => "not needed on Windows: the logs are plain files in " + Paths.LogsDir;
            }
            // starts.txt and the cutoff (milliseconds); the deleting runs on its own thread once every log file of this start is
            // open (RunServer). Before game.log is opened: its earlier start's file is rotated by its own writer.
            int keepStarts = (int)Num.Clamp(Settings.GetLong("logging.keepStarts", LogRetention.DefaultKeepStarts), 0, LogRetention.MaxKeepStarts);
            Retention = LogRetention.Begin(Paths.LogsDir, startedUtc, keepStarts, platformName + " " + McvsVersion.Current, Logger.For("logs"));
            GameHooks.Configure(Settings.GetBool("embedded.nativeOnlineSdk", false), Logger.For("game"), Boot);
            Log dataLog = Logger.For("game");
            GameDataHooks.Configure(line => dataLog.Info(line));
            ConfigurePhysics();
            ConfigureBots();
            ConfigureGraphics();
            ConfigureExtensions();
            if (isWindows) CheckArgsPrefixFiles();
            ApplyGameLog();
#if !MCVS_WINDOWS
            // Before the Java GameInstaller (it starts after OnCreate returns): data already on the device must be accepted
            // without Gameloft's CDN (docs/INSTALL-OFFLINE.md).
            PrepareInstallerData(platform);
#endif
            ResolveAdminPlatform(platform);
            notificationPlatform = platform as INotificationPlatform;

            int port = options.Port ?? Settings.Port;
            if (port <= 0 || port > 65535)
            {
                Log.Warn("settings.port " + port + " cannot be used in args.txt; using " + McvsConstants.DefaultPort);
                port = McvsConstants.DefaultPort;
            }
            ConfiguredPort = port;
            Port = port;
            LanSupport.ConfiguredHost = Settings.GetString("embedded.lanAdvertiseHost", "");

            bool manageArgs = Settings.GetBool("embedded.manageArgs", true);
            byte[] existingArgs = ReadArgsFile();
            PortReservation reservation = ReservePort(options, port);
            PortReservation = reservation;
            if (reservation != null) Port = reservation.Port;
            FinderPort = ChooseFinderPort(options, manageArgs, existingArgs);
            ClientFinderPort = ChooseClientFinderPort(existingArgs);
            PortHooks.Configure(Port, ClientFinderPort);

            byte[] gameArgs = existingArgs;
            if (manageArgs)
            {
                try
                {
                    gameArgs = WriteArgs(Port, FinderPort, existingArgs);
                }
                catch (Exception ex)
                {
                    Log.Error("could not update " + Paths.ArgsFile + "; the game will not find the offline server", ex);
                }
            }
            else
            {
                Log.Warn("settings.embedded.manageArgs is false: args.txt is left alone");
            }
            CheckGameFollowsPort(manageArgs, gameArgs);

            if (reservation != null && reservation.Outcome == PortOutcome.Deferred)
            {
                // Never two servers on one port, nor (another copy of the game) on one data folder: the game uses the one that answers.
                StartLogCleanup(null);
                serverReady.Set();
                if (IsOwnData(reservation.Holder)) WatchDeferredPort(options, port, reservation);
            }
            else
            {
                var thread = new Thread(() => RunServer(options, port, reservation));
                thread.IsBackground = true;
                thread.Name = "mcvs-embedded-start";
                thread.Start();
            }

            StartDiagnostics(options);
        }

        /// <summary>
        /// The settings page's device side (design §8.6): resolves the platform's JNI table once, here on the UI thread, and logs
        /// the members that did not resolve (the capabilities that need them are off; Status › host › missingJni).
        /// </summary>
        private void ResolveAdminPlatform(IEmbeddedPlatform platform)
        {
            adminPlatform = platform as IAdminPlatform;
            if (adminPlatform == null) return;
            try
            {
                List<string> missing = adminPlatform.MissingJniMembers();
                if (missing != null && missing.Count > 0)
                    Log.Warn("settings page: " + missing.Count + " JNI member(s) did not resolve: " + string.Join(", ", missing.ToArray()));
            }
            catch (Exception ex)
            {
                Log.Warn("settings page: the JNI table could not be resolved", ex);
            }
        }

        /// <summary>The server's IAdminHost: registered after McvsServer.Create and before Start (design §8.2, §8.9).</summary>
        private AdminHost CreateAdminHost(McvsServer created)
        {
            try
            {
                // On PC this runs before the engine loads Game: GameReflection binds lazily and never loads it (pc-port §8.9).
                var admin = new AdminHost(created.Context, adminPlatform, GameReflection.Load(), created.Context.Logger.For("admin"))
                {
                    PlatformName = platformName,
                    DeviceId = DeviceId,
                    GameRoot = Paths.GameRoot,
                    LogsDir = Paths.LogsDir,
                    FallbackLogsDir = fallbackLogDir,
                    StartedUtc = startedUtc,
                    Watchdog = () => this.Watchdog,
                    Problems = problemSink,
                    MirrorStatus = () =>
                    {
                        Func<string> mirror = this.MirrorStatus;
                        return mirror == null ? null : mirror();
                    },
                    FlushLogs = this.FlushLogs,
                };
                created.Context.Features.Set<IAdminHost>(admin);
                adminHost = admin;
                return admin;
            }
            catch (Exception ex)
            {
                Log.Warn("settings page: the host side could not be set up", ex);
                return null;
            }
        }

        /// <summary>
        /// The one-time notification permission request at the first main menu (Android 13+, target-sdk.md WP-T5): the decision
        /// reads the server's copy of settings.json (the page edits that one; notifications.requestedAt is written there), the
        /// main menu is read on the game thread, and the dialog gets a watchdog lease. The page's status row follows it.
        /// </summary>
        private void StartNotificationPrompt(McvsServer created, AdminHost admin)
        {
            if (notificationPlatform == null) return;
            try
            {
                Settings serverSettings = created.Context.Settings;
                GameReflection game = admin != null ? admin.Game : GameReflection.Load();
                var prompt = new NotificationPrompt(notificationPlatform, () => serverSettings, Logger.For("notifications"))
                {
                    AtMainMenu = () => game.AtMainMenu(2000),
                    BrowserOpen = () => adminPlatform != null && adminPlatform.IsInGameBrowserOpen(),
                    Mark = s => Boot.Mark(s),
                };
                if (admin != null)
                {
                    prompt.Lease = (ms, reason) => admin.Lease(ms, reason);
                    admin.NotificationStatus = prompt.Describe;
                }
                prompt.Start();
            }
            catch (Exception ex)
            {
                Log.Warn("notifications: the permission request could not be set up", ex);
            }
        }

#if !MCVS_WINDOWS
        /// <summary>settings.json "install.*": the Java installer's state (InstallerData) and its progress in the log.</summary>
        private void PrepareInstallerData(IEmbeddedPlatform platform)
        {
            var installer = platform as IInstallerPlatform;
            if (installer == null) return;
            Log log = Logger.For("install");
            try
            {
                if (Settings.GetBool("install.monitorInstaller", true)) installer.MonitorInstaller(log, Boot);
                string internalDir = installer.InternalFilesDir();
                if (string.IsNullOrEmpty(internalDir))
                {
                    log.Warn("installer data: internal storage (getFilesDir) unknown; the installer's state is left alone");
                    return;
                }
                var options = new McvsOffline.Embedded.Install.InstallerDataOptions
                {
                    Manage = Settings.GetBool("install.manageInstaller", true),
                    SuppressUpdateCheck = Settings.GetBool("install.suppressUpdateCheck", true),
                    RebuildIndex = Settings.GetBool("install.rebuildIndex", true),
                    VerifyRestoredData = Settings.GetBool("install.verifyRestoredData", true),
                };
                var locations = new McvsOffline.Embedded.Install.InstallerDataLocations(internalDir, Paths.GameRoot);
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                // The HD texture pack (docs/design/hd-install.md) lives in the installer's index too: its files are made to match
                // before InstallerData looks at the data, and the pack is installed or removed after it, still before the installer.
                var hdOptions = new HdInstallerOptions { Enabled = Settings.GetBool("hd.enabled", true), ManageInstaller = options.Manage };
                Log hdLog = Logger.For("hd");
                options.HdOriginalSizes = HdInstaller.Prepare(locations, hdOptions, hdLog).OriginalSizes;
                McvsOffline.Embedded.Install.InstallerDataResult result =
                    McvsOffline.Embedded.Install.InstallerData.Prepare(locations, installer.InstallerPrefs(), options, log, now);
                Boot.Mark(result.Summary);
                HdResult hd = HdInstaller.Apply(locations, hdOptions, hdLog);
                Boot.Mark(hd.Summary.StartsWith("hd textures: ", StringComparison.Ordinal) ? "hd: " + hd.Summary.Substring(13) : hd.Summary);
                McvsOffline.Embedded.Install.PackIndex verify = result.IndexToVerify;
                if (verify != null && hd.IndexChanged)
                {
                    // The CRC check reads the index as it is now: the HD files carry their own CRCs.
                    string error;
                    byte[] raw = File.Exists(locations.PackInfo) ? File.ReadAllBytes(locations.PackInfo) : null;
                    verify = raw == null ? null : McvsOffline.Embedded.Install.PackIndex.TryParse(raw, out error);
                }
                if (verify != null) StartDataVerify(installer, locations, verify, result.WaitForInstaller, log);
            }
            catch (Exception ex)
            {
                log.Warn("installer data: preparation failed; the game's installer runs as usual", ex);
            }
        }

        /// <summary>The background CRC check waits this long for the installer to hand over to the game, polling every second.</summary>
        private const int DataVerifyHandOverTimeoutMs = 2 * 60 * 60 * 1000;

        /// <summary>After the hand-over, the game loads first.</summary>
        private const int DataVerifyAfterHandOverMs = 60000;

        /// <summary>When the installer's state cannot be read and it had nothing to download: a fixed wait from the start.</summary>
        private const int DataVerifyFallbackDelayMs = 90000;

        /// <summary>
        /// The CRC32 check of data whose Gameloft index was given back to the installer (only sizes were checked before it ran):
        /// a background thread that waits until the installer has handed over to the game (until then it may still be writing
        /// files it downloads, which would look damaged) and the game has loaded, then reads the whole pack with a 2 ms pause per
        /// MiB. A check that does not get through resumes at the next start (InstallerData.VerifyContent, verify-pending).
        /// </summary>
        private void StartDataVerify(IInstallerPlatform installer, McvsOffline.Embedded.Install.InstallerDataLocations locations,
                                     McvsOffline.Embedded.Install.PackIndex index, bool mustWait, Log log)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    int handedOver = 0;
                    int started = Environment.TickCount;
                    while (Environment.TickCount - started < DataVerifyHandOverTimeoutMs)
                    {
                        handedOver = installer.InstallerHandedOver();
                        if (handedOver != 0) break;
                        Thread.Sleep(1000);
                    }
                    if (handedOver > 0)
                    {
                        Thread.Sleep(DataVerifyAfterHandOverMs);
                    }
                    else if (handedOver < 0 && !mustWait)
                    {
                        // Every file passed the installer's size test, so it downloads nothing: nothing changes under the check.
                        Thread.Sleep(Math.Max(0, DataVerifyFallbackDelayMs - (Environment.TickCount - started)));
                    }
                    else
                    {
                        log.Info("installer data: the background CRC check is left for a later start (" +
                                 (handedOver < 0 ? "the installer's state cannot be read and it still has files to fetch"
                                                 : "the installer has not handed over to the game after " + (DataVerifyHandOverTimeoutMs / 60000) + " minutes") + ")");
                        return;
                    }
                    log.Info("installer data: checking the CRC32 of every file in the background (" + index.LogicalFiles + " files)");
                    McvsOffline.Embedded.Install.InstallerData.VerifyContent(locations, index, log, null, 2);
                }
                catch (Exception ex)
                {
                    log.Warn("installer data: background data check failed", ex);
                }
            });
            thread.IsBackground = true;   // (Thread.Priority is trimmed from the game's BCL; the pauses keep it light)
            thread.Name = "mcvs-data-check";
            thread.Start();
        }
#endif

        /// <summary>
        /// settings.json "embedded.physics" (device test 2, patch 'physics'): the defaults are the fix (single-threaded Havok for
        /// the in-process TS/GS Worlds, one World's physics step at a time); each key reverts one part for an A/B run.
        /// </summary>
        private void ConfigurePhysics()
        {
            bool server = Settings.GetBool("embedded.physics.serverMultithreaded", false);
            bool client = Settings.GetBool("embedded.physics.clientMultithreaded", true);
            bool gate = Settings.GetBool("embedded.physics.stepGate", true);
            long timeout = Settings.GetLong("embedded.physics.stepGateTimeoutMs", GameHooks.DefaultStepGateTimeoutMs);
            if (timeout < 1 || timeout > 60000) timeout = GameHooks.DefaultStepGateTimeoutMs;
            GameHooks.ConfigurePhysics(server, client, gate, (int)timeout);
            if (server || !client || !gate || timeout != GameHooks.DefaultStepGateTimeoutMs)
            {
                Log.Warn("settings embedded.physics differs from the defaults: serverMultithreaded " + server + ", clientMultithreaded " + client +
                         ", stepGate " + gate + ", stepGateTimeoutMs " + timeout + " (A/B run; the defaults are the device-test-2 fix)");
            }
        }

        /// <summary>
        /// settings.json "embedded.bots" (patch 'botmove', build 27): <c>moveFromRest</c> (default true) lets a resting bot take its
        /// first step on frames shorter than 1/60 s (the PC's 60 FPS limit); false is the game's own code, for an A/B run (the
        /// "bots:" lines still count the blocked starts). Without the patch the key changes nothing.
        /// </summary>
        private void ConfigureBots()
        {
            bool moveFromRest = Settings.GetBool("embedded.bots.moveFromRest", true);
            GameHooks.ConfigureBots(moveFromRest);
            if (!moveFromRest)
                Log.Warn("settings embedded.bots differs from the defaults: moveFromRest False (A/B run: a resting bot starts only on frames of at least 1/60 s)");
        }

        /// <summary>
        /// settings.json "graphics" (patch 'graphics', docs/research/pc-textures.md WP0): which of Gameloft's GPU profiles the engine
        /// gets (GameOptionsProfile; GameOptionsWindows with the MEM rule on PC). Applies at the next start.
        /// </summary>
        private void ConfigureGraphics()
        {
            string profile = Settings.GetString("graphics.profile", GameOptionsProfile.Auto);
            bool full = Settings.GetBool("graphics.fullProfile", false);
            GraphicsHooks.Configure(profile, full, Logger.For("game"), Boot);
            PerformancePrefs.Configure(Settings);
            string file = "GameOptions_android.json";
            if (isWindows)
            {
                bool keep = Settings.GetBool("embedded.physics.keepPhysicsLayer", true);
                GraphicsHooks.ConfigureWindows(keep);
                file = GameOptionsWindows.FileName;
                if (!keep) Log.Warn("settings embedded.physics.keepPhysicsLayer is false: a MEM profile that excludes the levels' Physics layer applies to the server Worlds too");
                ConfigureFrameRate();
            }
            string normalized = GameOptionsProfile.Normalize(profile);
            if (normalized == null)
                Log.Warn("settings graphics.profile '" + profile + "' is not one of auto, mid, high, ultra: the game picks its GPU profile itself");
            else if (normalized != GameOptionsProfile.Auto || full)
                Log.Info("settings graphics.profile " + normalized + (full ? ", graphics.fullProfile true" : "") +
                         " (applied when the game reads " + file + "; needs patch 'graphics')");
        }

        /// <summary>
        /// PC, settings.json <c>graphics.pcFpsLimit</c> (patch 'fpslimit', build 27; PcFrameRate): read from this copy until the server
        /// runs (<see cref="WatchFrameRateSetting"/> then switches to the copy the settings page saves). "game" (the default) keeps the
        /// in-game FPS LIMIT; anything else is logged here once, and the "graphics: frame rate limit" line follows when the game sets
        /// its limit.
        /// </summary>
        private void ConfigureFrameRate()
        {
            Settings hostSettings = Settings;
            GraphicsHooks.ConfigureFrameRate(() => hostSettings.Get(PcFrameRate.SettingsKey), null);
            object raw = Settings.Get(PcFrameRate.SettingsKey);
            string choice = PcFrameRate.Normalize(raw);
            if (choice == null)
                Log.Warn("settings " + PcFrameRate.SettingsKey + " '" + Convert.ToString(raw, CultureInfo.InvariantCulture) + "' is not one of " +
                         string.Join(", ", PcFrameRate.Options) + ": the in-game FPS LIMIT applies");
            else if (PcFrameRate.Overrides(choice))
                Log.Info("settings " + PcFrameRate.SettingsKey + " " + choice + " (replaces the in-game FPS LIMIT when the game sets it; needs patch 'fpslimit')");
        }

        /// <summary>
        /// PC: from now on <c>graphics.pcFpsLimit</c> is read from the server's copy of settings.json, and a save of it on the settings
        /// page reaches the pacer at once (GraphicsHooks.FrameRateSettingChanged, posted to the game thread). Never throws.
        /// </summary>
        private void WatchFrameRateSetting(McvsServer created, AdminHost admin)
        {
            if (!isWindows) return;
            try
            {
                Settings serverSettings = created.Context.Settings;
                GameReflection game = admin != null ? admin.Game : GameReflection.Load();
                Log gameLog = Logger.For("game");
                GraphicsHooks.ConfigureFrameRate(() => serverSettings.Get(PcFrameRate.SettingsKey), action => game.Post(action, gameLog));
                serverSettings.Changed += keys =>
                {
                    foreach (string key in keys)
                    {
                        if (key == PcFrameRate.SettingsKey || key == "graphics")
                        {
                            GraphicsHooks.FrameRateSettingChanged();
                            return;
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                Log.Warn("graphics: changes of " + PcFrameRate.SettingsKey + " on the settings page apply at the next start only", ex);
            }
        }

        /// <summary>The platform's own pieces (PC: PcHooks, the runtime line, the install check). Never throws.</summary>
        private void ConfigureExtensions()
        {
            if (extensions == null) return;
            try
            {
                extensions.Configure(this);
            }
            catch (Exception ex)
            {
                Log.Warn("the platform's host extensions could not be configured", ex);
            }
        }

        /// <summary>
        /// The PC engine reads "&lt;prefix&gt;_args.txt" instead of args.txt when one is in the game folder ([CR] F9, [WH] §6.2): our
        /// managed block would be ignored and the game would go online. One WARN per file, and the same sentence in the status.
        /// </summary>
        private void CheckArgsPrefixFiles()
        {
            try
            {
                var found = new List<string>();
                foreach (string path in Directory.GetFiles(Paths.GameRoot))
                {
                    string name = Path.GetFileName(path);
                    if (name.Length > ArgsFile.FileName.Length + 1 && name.EndsWith("_" + ArgsFile.FileName, StringComparison.OrdinalIgnoreCase))
                        found.Add(name);
                }
                if (found.Count == 0) return;
                found.Sort(StringComparer.OrdinalIgnoreCase);
                var lines = new List<string>();
                foreach (string name in found)
                {
                    string line = name + " present: the engine ignores args.txt";
                    lines.Add(line);
                    Boot.Warn(line);
                }
                argsWarning = string.Join("; ", lines.ToArray()) + " (remove it, or the game goes online)";
            }
            catch (Exception ex)
            {
                Log.Warn("the game folder could not be listed for *_args.txt", ex);
            }
        }

        private SafeLogFileOptions LogFileOptions()
        {
            // A new file at every start: name.1 is the previous start, and the log retention deletes whole starts.
            return new SafeLogFileOptions { FallbackDirectory = fallbackLogDir, ReportInitialSize = true, NewFileAtStart = true };
        }

        /// <summary>settings.json "gameLog" (see GameLogBridge): the game's own log levels and the logs/game.log copy.</summary>
        private void ApplyGameLog()
        {
            if (diagnostics == null) return;
            try
            {
                if (Settings.GetBool("gameLog.file", true))
                {
                    SafeLogFileOptions o = LogFileOptions();
                    o.MaxBytes = 5L * 1024 * 1024;
                    o.MaxFiles = 3;
                    gameLogFile = new SafeLogFile(Path.Combine(Paths.LogsDir, "game.log"), o);
                }
                string applied = diagnostics.ApplyGameLog(Settings, gameLogFile);
                if (applied != null) Logger.For("gamelog").Info("Gameloft.Logs levels: " + applied);
            }
            catch (Exception ex)
            {
                Logger.For("gamelog").Warn("the game's log settings could not be applied", ex);
            }
        }

        /// <summary>"[watchdog] x" → "x": the logger's category says it already.</summary>
        internal static string WithoutTag(string line, string tag)
        {
            return line != null && line.StartsWith(tag, StringComparison.Ordinal) ? line.Substring(tag.Length) : line;
        }

        private void StartDiagnostics(EmbeddedHostOptions options)
        {
            try
            {
                if (options.Watchdog && Settings.GetBool("diagnostics.watchdog", true))
                {
                    Log watchdogLog = Logger.For("watchdog");
                    Func<string, string> javaStacks = null;
                    if (diagnostics != null) javaStacks = diagnostics.JavaStacks;
                    Action<int, string> threadDump = null;
                    Action<int, string> hitchDump = null;
                    // No in-process thread dump on the .NET Framework CLR ([WH] §4.5): the PC build has only ThreadDumpStub.
                    if (diagnostics != null && !isWindows && Settings.GetBool("diagnostics.threadDump", true))
                    {
                        // Device test 2: the game thread slept in a futex and nothing said where. Managed stacks of every thread
                        // (Mono's own dump) and native stacks of the game thread, into logcat, bootstrap.log and logs/threaddump-*.txt.
                        Log dumpLog = Logger.For("threaddump");
                        string logsDir = Paths.LogsDir;
                        threadDump = (tid, comm) => ThreadDump.Capture(tid, ThreadDump.Related(tid, comm),
                            Path.Combine(logsDir, "threaddump-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt"),
                            10000, s => dumpLog.Warn(WithoutTag(s, "[threaddump] ")));
                        // A hitch (build 30) is no problem: the native view only (no Mono managed dump, which suspends every managed
                        // thread and would lengthen the player's hitch; no file, so nothing in Downloads), at INFO.
                        hitchDump = (tid, comm) => ThreadDump.Capture(tid, ThreadDump.Related(tid, comm), null, 0,
                            s => dumpLog.Info(WithoutTag(s, "[threaddump] ")));
                    }
                    // Stall snapshots at WARN (the page's "recent problems"); the tid and the page's pause/resume at INFO.
                    Watchdog = new GameThreadWatchdog(() => Boot.Describe(), s => watchdogLog.Warn(WithoutTag(s, "[watchdog] ")),
                                                      javaStacks, 1000, null, threadDump)
                    {
                        Notice = s => watchdogLog.Info(WithoutTag(s, "[watchdog] ")),
                        // Build 30: the 1-5 s blocks of the owner's results screen and match start are under the first stall
                        // report; a hitch notice (INFO) says where the game thread is, the first two with the native stacks.
                        // Armed at the first match (the GS lobbyconfigs request): the loading blocks of the start are only counted.
                        HitchMs = GameThreadWatchdog.DefaultHitchMs,
                        HitchArmed = () => Boot.GsLobbyConfigs > 0,
                        HitchDump = hitchDump,
                    };
                    if (extensions != null)
                    {
                        try
                        {
                            Watchdog.Threads = extensions.ThreadSnapshot();
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("the platform's thread snapshot is not available; the watchdog reads /proc", ex);
                        }
                    }
                    Watchdog.Start();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("the game-thread watchdog could not start", ex);
            }
            try
            {
                Boot.StartMonitor();
            }
            catch (Exception)
            {
            }
            try
            {
                if (diagnostics != null) diagnostics.StartBackground(this);
            }
            catch (Exception ex)
            {
                Log.Warn("platform diagnostics could not start", ex);
            }
            if (options.StatusIntervalMs > 0)
            {
                var status = new Thread(() => StatusLoop(options.StatusIntervalMs));
                status.IsBackground = true;
                status.Name = "mcvs-status";
                status.Start();
            }
        }

        /// <summary>The status line: during the first 10 minutes, then every 10 minutes.</summary>
        private void StatusLoop(int intervalMs)
        {
            Log status = Logger.For("status");
            int started = Environment.TickCount;
            while (true)
            {
                try
                {
                    bool early = Environment.TickCount - started < 10 * 60 * 1000;
                    Thread.Sleep(early ? intervalMs : 10 * 60 * 1000);
                    status.Info(DescribeState().Replace("\n", "; "));
                }
                catch (Exception)
                {
                    try { Thread.Sleep(60000); } catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// Adds this host's keys to settings.json so players can find them (existing values are kept). Only the keys the platform
        /// has a use for (docs/design/pc-port.md §6.2): install.* for an installer platform, notifications.* for a notification
        /// platform, hd.* and the phone's log copy and thread dump not on PC, embedded.physics.keepPhysicsLayer only on PC.
        /// </summary>
        private void AddEmbeddedDefaults(IEmbeddedPlatform platform)
        {
            bool android = platformName == "android";
            bool changed = false;
            if (Settings.Get("embedded.manageArgs") == null)
            {
                Settings.Set("embedded.manageArgs", true);
                changed = true;
            }
            if (Settings.Get("embedded.lanAdvertiseHost") == null)
            {
                Settings.Set("embedded.lanAdvertiseHost", "");
                changed = true;
            }
            // Device test 1: the native online SDK (GaiaInit) stays off unless the player turns it back on.
            if (Settings.Get("embedded.nativeOnlineSdk") == null)
            {
                Settings.Set("embedded.nativeOnlineSdk", false);
                changed = true;
            }
            if (Settings.Get("gameLog") == null)
            {
                Settings.Set("gameLog.levels", "error warning");
                // Profile Debug entries dump whole profile documents (up to 1.4 MB) through the game's logcat writer on the game
                // thread; server.log has every request body anyway.
                Settings.Set("gameLog.mute", "profile:debug");
                Settings.Set("gameLog.file", true);
                changed = true;
            }
            if (Settings.Get("diagnostics") == null)
            {
                Settings.Set("diagnostics.watchdog", true);
                if (android) Settings.Set("diagnostics.publicLogCopy", true);
                changed = true;
            }
            // Keys added after the first device builds: added one by one, so an existing settings.json gets them too.
            if (android) changed |= AddDefault("diagnostics.threadDump", true);
            changed |= AddDefault("logging.keepStarts", (long)LogRetention.DefaultKeepStarts);
            changed |= AddDefault("embedded.physics.serverMultithreaded", false);
            changed |= AddDefault("embedded.physics.clientMultithreaded", true);
            changed |= AddDefault("embedded.physics.stepGate", true);
            changed |= AddDefault("embedded.physics.stepGateTimeoutMs", GameHooks.DefaultStepGateTimeoutMs);
            // Build 27 (patch 'botmove'): both platforms; inert without the patch (not in the default APK).
            changed |= AddDefault("embedded.bots.moveFromRest", true);
            // Build 26: false pins settings.port (no fallback to another port when it is busy at start; docs/design/port-fallback.md).
            changed |= AddDefault("embedded.portFallback", true);
#if !MCVS_WINDOWS
            if (platform is IInstallerPlatform)
            {
                changed |= AddDefault("install.manageInstaller", true);
                changed |= AddDefault("install.suppressUpdateCheck", true);
                changed |= AddDefault("install.rebuildIndex", true);
                changed |= AddDefault("install.verifyRestoredData", true);
                changed |= AddDefault("install.monitorInstaller", true);
            }
#endif
            changed |= AddDefault("graphics.profile", GameOptionsProfile.Auto);
            changed |= AddDefault("graphics.fullProfile", false);
#if !MCVS_WINDOWS
            if (!isWindows) changed |= AddDefault("hd.enabled", true);
#endif
            if (platform is INotificationPlatform)
            {
                changed |= AddDefault(NotificationPermission.RequestKey, true);
                changed |= AddDefault(NotificationPermission.RequestedAtKey, "");
            }
            if (isWindows) changed |= AddDefault("embedded.physics.keepPhysicsLayer", true);
            // Build 27 (patch 'fpslimit', PC only): "game" keeps the in-game FPS LIMIT; the settings page offers 30 to 240 and unlimited.
            if (isWindows) changed |= AddDefault(PcFrameRate.SettingsKey, PcFrameRate.Game);
            if (!changed) return;
            if (Settings.IsFallback)
            {
                // The player's settings.json has a syntax error (or cannot be read): the defaults are only in memory and
                // the file must stay exactly as it is, so the player can fix it.
                Log.Warn(Paths.SettingsFile + " could not be read: running with the default settings and leaving the file unchanged");
                return;
            }
            try
            {
                Settings.Save();
            }
            catch (Exception ex)
            {
                Log.Warn("cannot save " + Paths.SettingsFile, ex);
            }
        }

        private bool AddDefault(string key, object value)
        {
            if (Settings.Get(key) != null) return false;
            Settings.Set(key, value);
            return true;
        }

        /// <summary>The current args.txt, or null when it does not exist or cannot be read (logged).</summary>
        private byte[] ReadArgsFile()
        {
            try
            {
                string path = Paths.ArgsFile;
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception ex)
            {
                Log.Warn("cannot read " + Paths.ArgsFile, ex);
                return null;
            }
        }

        /// <summary>Writes the managed block; returns the file as the game will read it.</summary>
        private byte[] WriteArgs(int port, int finderPort, byte[] existing)
        {
            string path = Paths.ArgsFile;
            DeviceId = DeviceIdentity.LoadOrCreate(Paths.DataDir, ArgsFile.ReadManagedValue(existing, "DeviceId"), Log);
            List<ArgEntry> managed = ManagedArgs.Build(Settings, DeviceId, port, finderPort, Log);
            ArgsMergeResult result = ArgsFile.Merge(existing, managed);
            Args = result;
            foreach (string warning in result.Warnings) Log.Warn("args.txt: " + warning);
            foreach (string key in result.OverriddenKeys)
            {
                Log.Warn("args.txt: -" + key + " is set outside the managed block; the player's value is used");
            }
            if (!result.Changed)
            {
                Log.Info("args.txt is up to date (" + path + ")");
                return result.Content;
            }
            AtomicFile.WriteAllBytes(path, result.Content);
            var written = new List<string>();
            foreach (var entry in result.Written) written.Add(entry.ToString());
            Log.Info("args.txt updated (" + path + "): " + string.Join(" ", written.ToArray()));
            Boot.Mark("args.txt updated: the native engine reads it at " + argsReader);
            return result.Content;
        }

        private void RunServer(EmbeddedHostOptions options, int port, PortReservation reservation)
        {
            Socket listener = reservation == null ? null : reservation.Listener;
            try
            {
                var serverOptions = new ServerOptions
                {
                    DataDir = Paths.DataDir,
                    DataPackDir = Paths.DataPackDir,
                    // The configured port: settings.port stays what the player set; the reserved socket carries the bound one.
                    Port = port,
                    Listener = listener,
                    PortNote = reservation != null && reservation.Outcome == PortOutcome.Fallback ? "settings port " + port + " was busy at start" : null,
                    // The bootstrap logger's system sink would duplicate lines: the server gets its own.
                    ConsoleLog = false,
                    FileLog = true,
                    BootTrace = Boot,
                    FileLogOptions = LogFileOptions(),
                    ClientCapabilities = WatchPatches().Target,
                };
                // LAN is not supported on PC: the server's own copy of the settings too (never saved).
                if (isWindows) serverOptions.LanMode = false;
                // Sinks are thread-safe, so the server shares the platform sink (logcat) with the bootstrap logger.
                if (systemSink != null) serverOptions.ExtraSinks.Add(systemSink);
                string bind = BindAddress(options);
                if (bind != null) serverOptions.Bind = bind;
                if (options.RegisterServices != null) serverOptions.RegisterServices = options.RegisterServices;

                McvsServer created = McvsServer.Create(serverOptions);
                if (reservation != null) reservation.Adopted();   // the server closes it from here on, even if Start never runs
                StartLogCleanup(created);
                AdminHost admin = CreateAdminHost(created);
                WatchFrameRateSetting(created, admin);
                // A reserved socket cannot be busy: one Start. Otherwise (pinned port, or nothing could be bound) the build-25 retries.
                int attempts = listener != null || options.StartAttempts < 1 ? 1 : options.StartAttempts;
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        created.Start();
                        server = created;
                        // The game's hooks (Settings › Account button, inbox marker) work from here on.
                        if (admin != null) AdminHooks.Host = admin;
                        if (admin != null) Admin.SettingsMenu.UiNatives.Start(admin);
                        StartNotificationPrompt(created, admin);
                        StartMarker.Write(StartMarkerPath, created.Port, StartMarker.Running, DateTime.UtcNow);
                        Log.Info("offline server running at " + created.Context.BaseUrl);
                        Boot.Mark("offline server listening at " + created.Context.BaseUrl);
                        return;
                    }
                    catch (SocketException ex)
                    {
                        if (attempt >= attempts)
                        {
                            serverError = ex;
                            StartMarker.Write(StartMarkerPath, port, StartMarker.Stopped, DateTime.UtcNow);
                            Log.Error("cannot listen on port " + port + " after " + attempt + " attempts (" + ex.Message + "). " + portInUseHint);
                            created.Dispose();
                            return;
                        }
                        Log.Warn("port " + port + " is busy (" + ex.Message + "); retrying in " + options.RetryDelayMs + " ms");
                        Thread.Sleep(options.RetryDelayMs);
                    }
                }
            }
            catch (Exception ex)
            {
                serverError = ex;
                if (reservation != null) reservation.Release();   // not adopted: McvsServer.Create failed
                StartMarker.Write(StartMarkerPath, port, StartMarker.Stopped, DateTime.UtcNow);
                try
                {
                    Log.Error("the offline server failed to start", ex);
                }
                catch (Exception)
                {
                }
                StartLogCleanup(null);   // without the server's files (once: a second call does nothing)
            }
            finally
            {
                serverReady.Set();
            }
        }

        /// <summary>
        /// The log retention's thread, once every log file of this start has its SafeLogFile (bootstrap.log, game.log, and the
        /// server's server.log and unknown-endpoints.jsonl): a family without one is not written by this process. Never throws.
        /// </summary>
        private void StartLogCleanup(McvsServer created)
        {
            try
            {
                LogRetention retention = Retention;
                if (retention == null) return;
                var owners = new List<SafeLogFile>();
                if (bootstrapSink != null) owners.Add(bootstrapSink.File);
                if (gameLogFile != null) owners.Add(gameLogFile);
                if (created != null) owners.AddRange(created.OpenLogFiles());
                retention.StartCleanup(owners, fallbackLogDir, Logger.For("logs"));
            }
            catch (Exception ex)
            {
                try { Log.Warn("the log cleanup could not start", ex); } catch (Exception) { }
            }
        }

        /// <summary>
        /// The client's patches for the server (ServerOptions.ClientCapabilities): probed now, and on PC again when Game loads (the
        /// bootstrap runs before it there), with the "[boot] assemblies:" and "[boot] patches:" lines.
        /// </summary>
        private PatchWatcher WatchPatches()
        {
            Func<Action<System.Reflection.Assembly>, Action> watchLoads = null;
            if (extensions != null) watchLoads = extensions.WatchAssemblyLoads;
            PatchWatcher watcher = GamePatchProbe.Watch(new ClientCapabilities(), Logger.For("game"), watchLoads, isWindows ? Boot : null, Paths.GameRoot);
            Patches = watcher;
            return watcher;
        }

        /// <summary>The address the server listens on: EmbeddedHostOptions.Bind (tests), every interface in LAN mode, else null (settings.bind).</summary>
        private string BindAddress(EmbeddedHostOptions options)
        {
            if (!string.IsNullOrEmpty(options.Bind)) return options.Bind;
            if (Settings.LanMode && IsLoopbackBind(Settings.Bind)) return "0.0.0.0";
            return null;
        }

        /// <summary>
        /// The port fallback (docs/design/port-fallback.md), before args.txt is written: binds the configured port, or decides what
        /// to do when it is busy, with one log line that says why. Null when the bind address itself is unusable (the server
        /// thread then reports it). Never throws.
        /// </summary>
        private PortReservation ReservePort(EmbeddedHostOptions options, int port)
        {
            PortReservation r;
            bool fallback = Settings.GetBool("embedded.portFallback", true);
            try
            {
                IPAddress address = McvsServer.ParseBind(BindAddress(options) ?? Settings.Bind);
                reservedAddress = address;
                string marker = StartMarkerPath;
                r = PortReservation.Reserve(address, port, fallback, options.Listen, options.ProbePort, m => Log.Warn(m),
                                            p => StartMarker.IsStarting(marker, p, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                Log.Warn("the server port could not be reserved before args.txt (" + ex.Message + "); the server thread binds port " + port + " itself");
                return null;
            }
            string url = ManagedArgs.EnvironmentSwitcherUrl(r.Port);
            // This start's server is being built on the reserved socket: a second copy started now waits for it (StartMarker).
            if (r.Listener != null) StartMarker.Write(StartMarkerPath, r.Port, StartMarker.Starting, DateTime.UtcNow);
            switch (r.Outcome)
            {
                case PortOutcome.Deferred:
                    string line = r.Reason + ": the game uses it at " + url + " (a standalone server through adb reverse, or another copy of the game); " +
                                  "this game's own server stays off, so its settings page is that server's";
                    if (IsOwnData(r.Holder))
                    {
                        // The likeliest deferral to end badly: the other copy may be quit at any time. WatchDeferredPort takes over then.
                        Log.Warn(line + ". It uses this game's own data folder: another copy of the game is still running; quit it before playing " +
                                 "(when its server stops, this game's own server takes port " + port + " over)");
                    }
                    else
                    {
                        Log.Info(line);
                    }
                    Boot.Mark("offline server: the MCVS Encore server already answering on port " + port + " is used");
                    break;
                case PortOutcome.Fallback:
                    Log.Warn(r.Reason + "; this start uses port " + r.Port + ": args.txt, the eve URLs, the settings page and the geo lookup follow it; " +
                             "settings.json keeps " + port + " and the next start tries it first" + (isWindows ? "" : " (a phone restart frees it)"));
                    Boot.Mark("port fallback: " + port + " busy, the offline server listens on " + r.Port);
                    if (Settings.LanMode)
                        Log.Warn("LAN guests must use -EnvironmentSwitcherUrl http://<this device's address>:" + r.Port + "/ for this start, not port " + port);
                    if (!string.IsNullOrEmpty(Settings.PublicUrl))
                        Log.Warn("settings publicUrl " + Settings.PublicUrl + " is what the server hands out, not the bound port " + r.Port);
                    break;
                case PortOutcome.Failed:
                    if (!fallback && r.Reason != null)
                        Log.Warn(r.Reason + "; embedded.portFallback is false, so the server keeps trying port " + port + " (args.txt names it)");
                    else
                        Log.Error("no port could be reserved for the offline server (" + (r.BindError == null ? "?" : r.BindError.Message) +
                                  "); the server thread tries port " + port + " again");
                    break;
            }
            return r;
        }

        /// <summary>True when the answering server uses this game's own McvsOffline folder (another copy of the game).</summary>
        private bool IsOwnData(PortHolder holder)
        {
            try
            {
                if (holder == null || string.IsNullOrEmpty(holder.DataDir)) return false;
                StringComparison cmp = isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                string theirs = Path.GetFullPath(holder.DataDir).TrimEnd(Path.DirectorySeparatorChar);
                string ours = Path.GetFullPath(Paths.DataDir).TrimEnd(Path.DirectorySeparatorChar);
                return string.Equals(theirs, ours, cmp);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>logs/server-start.txt (<see cref="StartMarker"/>).</summary>
        private string StartMarkerPath
        {
            get { return Paths == null ? null : Path.Combine(Paths.LogsDir, StartMarker.FileName); }
        }

        /// <summary>
        /// Deferring to another copy of the game's server on this data folder: a background thread tries to bind the configured
        /// port every <see cref="EmbeddedHostOptions.PortWatchIntervalMs"/>. The bind only succeeds once that server has let the
        /// port go (never two listeners), and then this game's own server starts on it: port and args.txt are unchanged, so the
        /// game's next requests reach it. Never throws.
        /// </summary>
        private void WatchDeferredPort(EmbeddedHostOptions options, int port, PortReservation reservation)
        {
            IPAddress address = reservedAddress;
            if (address == null) return;
            Func<IPAddress, int, Action<string>, Socket> listen = options.Listen ?? ((a, p, w) => HttpServer.Listen(a, p, w));
            int interval = options.PortWatchIntervalMs > 0 ? options.PortWatchIntervalMs : 2000;
            try
            {
                portWatching = true;
                var thread = new Thread(() =>
                {
                    try
                    {
                        while (!portWatchStop.WaitOne(interval))
                        {
                            Socket socket;
                            try
                            {
                                socket = listen(address, port, null);
                            }
                            catch (SocketException)
                            {
                                continue;   // still held: the other server is up
                            }
                            if (portWatchStop.WaitOne(0))
                            {
                                try { socket.Close(); } catch (Exception) { }
                                return;
                            }
                            reservation.TakeOver(socket);
                            Log.Warn("the MCVS Encore server of the other copy of the game (port " + port + ", this game's data folder) has stopped; " +
                                     "this game's own server takes port " + port + " over, and the game's next requests reach it");
                            Boot.Mark("offline server: took port " + port + " over after the other copy's server stopped");
                            portWatching = false;
                            RunServer(options, port, reservation);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        try { Log.Warn("the port watch stopped", ex); } catch (Exception) { }
                    }
                    finally
                    {
                        portWatching = false;
                    }
                });
                thread.IsBackground = true;
                thread.Name = "mcvs-port-watch";
                thread.Start();
            }
            catch (Exception ex)
            {
                portWatching = false;
                Log.Warn("the port watch could not start", ex);
            }
        }

        /// <summary>Stops the port watch of a deferring start (tests).</summary>
        internal void StopPortWatch()
        {
            portWatchStop.Set();
        }

        /// <summary>
        /// The Finder port for the 'finderport' hook. A LAN guest (args.txt sets -RemoteIp; the 'lan' patch points
        /// ClientSettings.LocalAnubisFinderHost at the host) contacts the host's Finder with the same property, so a move of its own
        /// Finder must not move the port it asks the host on: the unmoved port then (the player's -LocalAnubisFinderPort still wins).
        /// </summary>
        private int ChooseClientFinderPort(byte[] existingArgs)
        {
            if (!finderMoved) return FinderPort;
            try
            {
                string remoteIp = ArgsFile.ReadEffectiveValue(existingArgs, "RemoteIp");
                if (string.IsNullOrEmpty(remoteIp) || remoteIp.Trim().Length == 0) return FinderPort;
                Log.Info("LAN guest (-RemoteIp " + remoteIp.Trim() + "): this device's own LocalAnubis Finder uses " + FinderPort +
                         ", the client still looks for the host's Finder on " + finderPreferred);
                return finderPreferred;
            }
            catch (Exception)
            {
                return FinderPort;
            }
        }

        /// <summary>
        /// The LocalAnubis Finder's port for this start: the player's -LocalAnubisFinderPort when args.txt sets one, else 51750, or
        /// the next free port when that is busy (only when args.txt is managed: the managed block carries the moved port).
        /// </summary>
        private int ChooseFinderPort(EmbeddedHostOptions options, bool manageArgs, byte[] existingArgs)
        {
            int preferred = options.FinderPort ?? PortHooks.DefaultFinderPort;
            finderPreferred = preferred;
            finderMoved = false;
            try
            {
                string user = manageArgs ? ArgsFile.ReadUserValue(existingArgs, "LocalAnubisFinderPort")
                                         : ArgsFile.ReadEffectiveValue(existingArgs, "LocalAnubisFinderPort");
                if (user != null)
                {
                    int value;
                    if (!int.TryParse(user, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 1 || value > 65535) return 0;
                    if (!FinderPortCheck.IsFree(value))
                    {
                        Log.Error("args.txt sets -LocalAnubisFinderPort " + value + ", and that port is busy: the game's LocalAnubis Finder cannot start " +
                                  "(the main menu does not load); remove the line so the host can pick a free port");
                    }
                    return value;
                }
                if (!manageArgs) return preferred;
                int chosen = FinderPortCheck.Choose(preferred, null);
                if (chosen != preferred)
                {
                    Log.Warn("the LocalAnubis Finder port " + preferred + " is busy (a frozen earlier copy of the game?); this start uses " + chosen +
                             " (-LocalAnubisFinderPort in args.txt; the patched client follows it)");
                    Boot.Mark("port fallback: LocalAnubis Finder " + preferred + " busy, the game uses " + chosen);
                    finderMoved = true;
                    if (Settings.LanMode)
                        Log.Warn("LAN guests look for this host's LocalAnubis Finder on " + preferred + ": for this start they need -LocalAnubisFinderPort " +
                                 chosen + " in their own args.txt (it moves their own Finder too), or quick launch against this host fails");
                }
                return chosen;
            }
            catch (Exception ex)
            {
                Log.Warn("the LocalAnubis Finder port could not be checked", ex);
                return preferred;
            }
        }

        /// <summary>After a port fallback: an ERROR when args.txt still sends the game elsewhere (the player's own line, or unmanaged).</summary>
        private void CheckGameFollowsPort(bool manageArgs, byte[] gameArgs)
        {
            PortReservation r = PortReservation;
            if (r == null || r.Outcome != PortOutcome.Fallback) return;
            try
            {
                string expected = ManagedArgs.EnvironmentSwitcherUrl(r.Port);
                string actual = ArgsFile.ReadEffectiveValue(gameArgs, "EnvironmentSwitcherUrl");
                if (string.Equals(actual, expected, StringComparison.Ordinal)) return;
                string where = manageArgs ? " (set outside the managed block)" : " (embedded.manageArgs is false)";
                if (actual != null && !NamesLoopbackPort(actual, r.ConfiguredPort))
                {
                    // A server the player chose on purpose (a LAN host, a PC server): the game reaches it, whatever this device's port.
                    Log.Warn("port fallback: the game uses -EnvironmentSwitcherUrl " + actual + " from args.txt" + where + ", not this device's server (port " +
                             r.Port + "); fine when that is the server you mean (a LAN host, a PC server), otherwise remove the line");
                    return;
                }
                Log.Error("port fallback: the game will use -EnvironmentSwitcherUrl " + (actual ?? "(none)") + " from args.txt" + where + ", not this start's server at " +
                          expected + ": it will not reach its server" + (actual == null ? "" : " (the busy port, unless a server you start yourself answers there, " +
                                                                                            "e.g. a standalone through adb reverse)") +
                          "; remove that line (or point it at this URL)");
            }
            catch (Exception ex)
            {
                Log.Warn("args.txt could not be checked after the port fallback", ex);
            }
        }

        /// <summary>True when <paramref name="url"/> is http://127.0.0.1, localhost or [::1] on <paramref name="port"/>: the busy port.</summary>
        internal static bool NamesLoopbackPort(string url, int port)
        {
            Uri uri;
            if (url == null || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)) return false;
            string host = uri.Host.ToLowerInvariant();
            bool loopback = host == "127.0.0.1" || host == "localhost" || host == "[::1]" || host == "::1";
            return loopback && uri.Port == port;
        }

        private static bool IsLoopbackBind(string bind)
        {
            string b = (bind ?? "").Trim();
            return b.Length == 0 || b == "127.0.0.1" || b == "localhost" || b == "::1";
        }
    }
}
