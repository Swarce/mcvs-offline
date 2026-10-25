// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
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
        }

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
                host = new EmbeddedHost(logger, logger.For("embedded"));
                host.systemSink = system;
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

            string root = platform.ResolveGameRoot(Log);
            Paths = new EmbeddedPaths(root);
            Directory.CreateDirectory(Paths.LogsDir);
            try
            {
                Logger.AddSink(new RotatingFileLogSink(Paths.BootstrapLog, LogLevel.Debug, 512 * 1024, 3));
            }
            catch (Exception ex)
            {
                Log.Warn("cannot open " + Paths.BootstrapLog, ex);
            }
            Log.Info("game root " + Paths.GameRoot + "; data directory " + Paths.DataDir);

            Settings = Settings.Load(Paths.SettingsFile, Logger.For("settings"));
            AddEmbeddedDefaults();

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
