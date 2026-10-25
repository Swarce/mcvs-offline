// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Core.Storage;

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
        }

        /// <summary>Start the game-thread watchdog (settings diagnostics.watchdog can still turn it off).</summary>
        public bool Watchdog { get; set; }

        /// <summary>How often the status line (log files, boot trace, watchdog) is logged during the first 10 minutes; 0: never.</summary>
        public int StatusIntervalMs { get; set; }

        /// <summary>Overrides settings.port for this run (tests). Never 0: args.txt needs the real port up front.</summary>
        public int? Port { get; set; }

        /// <summary>Overrides the bind address for this run (tests).</summary>
        public string Bind { get; set; }

        /// <summary>How often to try binding the port before giving up (another process may still hold it).</summary>
        public int StartAttempts { get; set; }

        public int RetryDelayMs { get; set; }

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
    /// <item>on a background thread: build the server (ServiceRegistry) and bind 127.0.0.1:51780 (all interfaces in
    /// LAN mode), retrying while the port is busy.</item>
    /// </list>
    /// Nothing here throws: every failure is logged (logcat + McvsOffline/logs/bootstrap.log) and the game boots anyway.
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
        private string fallbackLogDir;

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

        public int Port { get; private set; }

        /// <summary>The boot trace ("[boot]" milestones), shared with the server (it adds the request milestones).</summary>
        public BootTrace Boot { get; private set; }

        /// <summary>The game-thread watchdog, or null when it is off.</summary>
        public GameThreadWatchdog Watchdog { get; private set; }

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
                host.Log.Error("MCVS Offline could not prepare the offline backend; the game will start without it", ex);
                host.serverReady.Set();
            }
            return host;
        }

        private void Prepare(IEmbeddedPlatform platform, EmbeddedHostOptions options)
        {
            Log.Info("MCVS Offline " + McvsVersion.Current + " embedded host starting (platform " + platform.Name + ")");
            diagnostics = platform as IPlatformDiagnostics;
            if (CrashReporter.Hooks != null) Logger.For("crash").Info("hooks: " + CrashReporter.Hooks);
            Boot = new BootTrace(Logger.For("boot"));
            Boot.Mark("MCVS Offline host started by the game (NicePlaySDKDLL.OnInitialize)");

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

            Settings = Settings.Load(Paths.SettingsFile, Logger.For("settings"));
            AddEmbeddedDefaults();
            GameHooks.Configure(Settings.GetBool("embedded.nativeOnlineSdk", false), Logger.For("game"), Boot);
            ConfigurePhysics();
            ApplyGameLog();
            // Before the Java GameInstaller (it starts after OnCreate returns): data already on the device must be accepted
            // without Gameloft's CDN (docs/INSTALL-OFFLINE.md).
            PrepareInstallerData(platform);

            int port = options.Port ?? Settings.Port;
            if (port <= 0 || port > 65535)
            {
                Log.Warn("settings.port " + port + " cannot be used in args.txt; using " + McvsConstants.DefaultPort);
                port = McvsConstants.DefaultPort;
            }
            Port = port;
            LanSupport.ConfiguredHost = Settings.GetString("embedded.lanAdvertiseHost", "");

            if (Settings.GetBool("embedded.manageArgs", true))
            {
                try
                {
                    WriteArgs(port);
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

            var thread = new Thread(() => RunServer(options, port));
            thread.IsBackground = true;
            thread.Name = "mcvs-embedded-start";
            thread.Start();

            StartDiagnostics(options);
        }

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
                McvsOffline.Embedded.Install.InstallerDataResult result =
                    McvsOffline.Embedded.Install.InstallerData.Prepare(locations, installer.InstallerPrefs(), options, log, now);
                Boot.Mark(result.Summary);
                if (result.IndexToVerify != null) StartDataVerify(locations, result.IndexToVerify, log);
            }
            catch (Exception ex)
            {
                log.Warn("installer data: preparation failed; the game's installer runs as usual", ex);
            }
        }

        /// <summary>
        /// The CRC32 check of data whose Gameloft index was just given back to the installer (sizes were checked before it ran):
        /// a background thread that waits until the game has loaded, then reads the whole pack with a 2 ms pause per MiB.
        /// </summary>
        private void StartDataVerify(McvsOffline.Embedded.Install.InstallerDataLocations locations, McvsOffline.Embedded.Install.PackIndex index, Log log)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    Thread.Sleep(90000);
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

        private SafeLogFileOptions LogFileOptions()
        {
            return new SafeLogFileOptions { FallbackDirectory = fallbackLogDir, ReportInitialSize = true };
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
                    if (diagnostics != null && Settings.GetBool("diagnostics.threadDump", true))
                    {
                        // Device test 2: the game thread slept in a futex and nothing said where. Managed stacks of every thread
                        // (Mono's own dump) and native stacks of the game thread, into logcat, bootstrap.log and logs/threaddump-*.txt.
                        Log dumpLog = Logger.For("threaddump");
                        string logsDir = Paths.LogsDir;
                        threadDump = (tid, comm) => ThreadDump.Capture(tid, ThreadDump.Related(tid, comm),
                            Path.Combine(logsDir, "threaddump-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt"),
                            10000, s => dumpLog.Warn(s.StartsWith("[threaddump] ", StringComparison.Ordinal) ? s.Substring(13) : s));
                    }
                    Watchdog = new GameThreadWatchdog(() => Boot.Describe(),
                                                      s => watchdogLog.Warn(s.StartsWith("[watchdog] ", StringComparison.Ordinal) ? s.Substring(11) : s),
                                                      javaStacks, 1000, null, threadDump);
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

        /// <summary>Adds this host's keys to settings.json so players can find them (existing values are kept).</summary>
        private void AddEmbeddedDefaults()
        {
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
                Settings.Set("diagnostics.publicLogCopy", true);
                changed = true;
            }
            // Keys added after the first device builds: added one by one, so an existing settings.json gets them too.
            changed |= AddDefault("diagnostics.threadDump", true);
            changed |= AddDefault("embedded.physics.serverMultithreaded", false);
            changed |= AddDefault("embedded.physics.clientMultithreaded", true);
            changed |= AddDefault("embedded.physics.stepGate", true);
            changed |= AddDefault("embedded.physics.stepGateTimeoutMs", GameHooks.DefaultStepGateTimeoutMs);
            changed |= AddDefault("install.manageInstaller", true);
            changed |= AddDefault("install.suppressUpdateCheck", true);
            changed |= AddDefault("install.rebuildIndex", true);
            changed |= AddDefault("install.verifyRestoredData", true);
            changed |= AddDefault("install.monitorInstaller", true);
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

        private void WriteArgs(int port)
        {
            string path = Paths.ArgsFile;
            byte[] existing = File.Exists(path) ? File.ReadAllBytes(path) : null;
            DeviceId = DeviceIdentity.LoadOrCreate(Paths.DataDir, ArgsFile.ReadManagedValue(existing, "DeviceId"), Log);
            List<ArgEntry> managed = ManagedArgs.Build(Settings, DeviceId, port, Log);
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
                return;
            }
            AtomicFile.WriteAllBytes(path, result.Content);
            var written = new List<string>();
            foreach (var entry in result.Written) written.Add(entry.ToString());
            Log.Info("args.txt updated (" + path + "): " + string.Join(" ", written.ToArray()));
            Boot.Mark("args.txt updated: the native engine reads it at JNIBridge.NativeInit");
        }

        private void RunServer(EmbeddedHostOptions options, int port)
        {
            try
            {
                var serverOptions = new ServerOptions
                {
                    DataDir = Paths.DataDir,
                    DataPackDir = Paths.DataPackDir,
                    Port = port,
                    // The bootstrap logger's system sink would duplicate lines: the server gets its own.
                    ConsoleLog = false,
                    FileLog = true,
                    BootTrace = Boot,
                    FileLogOptions = LogFileOptions(),
                };
                // Sinks are thread-safe, so the server shares the platform sink (logcat) with the bootstrap logger.
                if (systemSink != null) serverOptions.ExtraSinks.Add(systemSink);
                if (!string.IsNullOrEmpty(options.Bind)) serverOptions.Bind = options.Bind;
                else if (Settings.LanMode && IsLoopbackBind(Settings.Bind)) serverOptions.Bind = "0.0.0.0";
                if (options.RegisterServices != null) serverOptions.RegisterServices = options.RegisterServices;

                McvsServer created = McvsServer.Create(serverOptions);
                int attempts = options.StartAttempts < 1 ? 1 : options.StartAttempts;
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        created.Start();
                        server = created;
                        Log.Info("offline server running at " + created.Context.BaseUrl);
                        Boot.Mark("offline server listening at " + created.Context.BaseUrl);
                        return;
                    }
                    catch (SocketException ex)
                    {
                        if (attempt >= attempts)
                        {
                            serverError = ex;
                            Log.Error("cannot listen on port " + port + " after " + attempt + " attempts (" + ex.Message +
                                      "). If a standalone server is connected with 'adb reverse', the game will use that one.");
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
                try
                {
                    Log.Error("the offline server failed to start", ex);
                }
                catch (Exception)
                {
                }
            }
            finally
            {
                serverReady.Set();
            }
        }

        private static bool IsLoopbackBind(string bind)
        {
            string b = (bind ?? "").Trim();
            return b.Length == 0 || b == "127.0.0.1" || b == "localhost" || b == "::1";
        }
    }
}
