using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services;

namespace McvsOffline.Server
{
    /// <summary>How a host wants the server set up. Null/unset members fall back to settings.json and defaults.</summary>
    public sealed class ServerOptions
    {
        public ServerOptions()
        {
            DataDir = "data";
            ConsoleLog = true;
            FileLog = true;
            ExtraSinks = new List<LogSink>();
        }

        /// <summary>Data directory (settings.json, accounts, profiles, logs). Standalone default ./data.</summary>
        public string DataDir { get; set; }

        /// <summary>The player's on-device-style data pack directory (.../files/Data); optional.</summary>
        public string DataPackDir { get; set; }

        /// <summary>Overrides settings.port for this run (0 = any free port, useful in tests).</summary>
        public int? Port { get; set; }

        /// <summary>Overrides settings.bind for this run.</summary>
        public string Bind { get; set; }

        /// <summary>Overrides settings.lanMode for this run.</summary>
        public bool? LanMode { get; set; }

        /// <summary>Overrides the console log level for this run.</summary>
        public LogLevel? ConsoleLevel { get; set; }

        public bool ConsoleLog { get; set; }

        public bool FileLog { get; set; }

        /// <summary>Additional sinks (e.g. logcat in the embedded host, a MemoryLogSink in tests).</summary>
        public List<LogSink> ExtraSinks { get; set; }

        public IClock Clock { get; set; }

        /// <summary>Replaces <see cref="ServiceRegistry.RegisterAll"/> (tests that want a bare router).</summary>
        public Action<Router, ServerContext> RegisterServices { get; set; }

        public HttpServerLimits Limits { get; set; }

        /// <summary>The boot trace to feed with requests (the embedded host shares its own); null: the server makes one.</summary>
        public BootTrace BootTrace { get; set; }

        /// <summary>Options of the server.log file (fallback directory, start-up size report); null: defaults.</summary>
        public SafeLogFileOptions FileLogOptions { get; set; }

        /// <summary>
        /// What the host knows about the game client (the embedded host probes the loaded Game.dll); registered in
        /// <c>context.Features</c>. null: unknown (the standalone host cannot see the client).
        /// </summary>
        public ClientCapabilities ClientCapabilities { get; set; }
    }

    /// <summary>
    /// The Federation emulator: builds the <see cref="ServerContext"/>, registers the core routes and every service
    /// module, and serves them over HTTP. Hosts (standalone console, embedded bootstrap) only create, start and stop it.
    /// <see cref="Handle"/> is the transport-independent entry point (also usable in-process without a socket).
    /// </summary>
    public sealed class McvsServer : IDisposable
    {
        /// <summary>WARN/ERROR entries kept for the settings page's "recent problems" (<see cref="ServerContext.Problems"/>).</summary>
        public const int ProblemCapacity = 200;

        private readonly object gate = new object();
        private readonly RequestLogger requestLogger;
        private readonly HttpServerLimits limits;
        private readonly Log gateLog;
        private HttpServer http;
        private bool disposed;
        private FileLogSink fileSink;
        private int lastHostWarningTick;
        private bool hostWarned;

        private McvsServer(ServerContext context, Router router, RequestLogger requestLogger, HttpServerLimits limits, BootTrace boot)
        {
            Context = context;
            Router = router;
            this.requestLogger = requestLogger;
            this.limits = limits ?? new HttpServerLimits();
            Boot = boot;
            gateLog = context.Logger.For("gate");
        }

        /// <summary>Milestones of the game's boot chain, recognised from the requests (see <see cref="BootTrace"/>).</summary>
        public BootTrace Boot { get; private set; }

        public ServerContext Context { get; private set; }

        public Router Router { get; private set; }

        public bool IsRunning { get { lock (gate) return http != null && http.IsRunning; } }

        /// <summary>Actual listening port (after <see cref="Start"/>).</summary>
        public int Port { get { return Context.Port; } }

        public static McvsServer Create(ServerOptions options)
        {
            options = options ?? new ServerOptions();
            string dataDir = Path.GetFullPath(string.IsNullOrEmpty(options.DataDir) ? "data" : options.DataDir);
            Directory.CreateDirectory(dataDir);

            var logger = new Logger();
            // Tickets, session cookies, passwords and tokens never reach a sink (design §5.4).
            logger.Redactor = LogRedaction.Redact;
            // Before settings.json is loaded, so its problems are on the settings page's status too.
            var problems = new MemoryLogSink(LogLevel.Warn, ProblemCapacity);
            logger.AddSink(problems);
            // Console first with defaults, so problems loading settings.json are visible.
            ConsoleLogSink console = null;
            if (options.ConsoleLog)
            {
                console = new ConsoleLogSink(options.ConsoleLevel ?? LogLevel.Info);
                logger.AddSink(console);
            }
            foreach (var sink in options.ExtraSinks ?? new List<LogSink>()) logger.AddSink(sink);

            Settings settings = Settings.Load(Path.Combine(dataDir, Settings.FileName), logger.For("settings"));
            if (options.Port.HasValue) settings.Override("port", (long)options.Port.Value);
            if (!string.IsNullOrEmpty(options.Bind)) settings.Override("bind", options.Bind);
            if (options.LanMode.HasValue) settings.Override("lanMode", options.LanMode.Value);

            if (console != null)
            {
                console.MinLevel = options.ConsoleLevel ?? settings.ConsoleLogLevel;
                logger.Recompute();
            }
            FileLogSink fileSink = null;
            if (options.FileLog)
            {
                logger.AddSink(fileSink = new FileLogSink(Path.Combine(dataDir, "logs", "server.log"), settings.FileLogLevel,
                                               settings.LogFileMaxBytes, settings.LogFileCount, options.FileLogOptions));
            }

            Log log = logger.For("core");
            log.Info("MCVS Offline server " + McvsVersion.Current + ", data directory " + dataDir);

            DataPack pack = null;
            if (!string.IsNullOrEmpty(options.DataPackDir))
            {
                pack = new DataPack(options.DataPackDir);
                if (pack.IsValid) log.Info("data pack: " + pack.DataDirectory);
                else log.Warn("data pack directory " + pack.DataDirectory + " does not look like a game Data folder (no MCVS.game)");
            }
            else
            {
                log.Warn("no data pack configured; modules that need game files (e.g. the CRM default) will degrade");
            }
            var content = new ContentProvider(pack, Path.Combine(dataDir, "content"), logger.For("content"));

            var context = new ServerContext(dataDir, settings, logger, options.Clock, content);
            context.Problems = problems;
            ClientCapabilities client = options.ClientCapabilities ?? new ClientCapabilities();
            context.Features.Set(client);
            if (client.StorePatch == false)
                log.Warn("client: Game.dll lacks the patcher's 'store' patch (left out with --patches): the collector stays off");
            else
                log.Info("client: 'store' patch " + (client.StorePatch == true ? "present" : "unknown (assumed present)"));
            var requestLogger = new RequestLogger(logger, Path.Combine(dataDir, "logs", "unknown-endpoints.jsonl"),
                                                  () => settings.LogBodyLimit);
            var router = new Router(requestLogger);
            BootTrace boot = options.BootTrace ?? new BootTrace(logger.For("boot"));
            var server = new McvsServer(context, router, requestLogger, options.Limits, boot);
            server.fileSink = fileSink;

            CoreRoutes.Register(router, context);
            var register = options.RegisterServices ?? ServiceRegistry.RegisterAll;
            register(router, context);
            log.Info(router.Routes.Length + " routes registered");
            return server;
        }

        /// <summary>One line per log file of the server (status lines, crash reports).</summary>
        public List<string> DescribeLogFiles()
        {
            var lines = new List<string>();
            if (fileSink != null) lines.Add(fileSink.File.Describe());
            string unknown = requestLogger.DescribeUnknownFile();
            if (unknown != null) lines.Add(unknown);
            return lines;
        }

        /// <summary>Binds the configured address and port and starts serving.</summary>
        public void Start()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("McvsServer");
                if (http != null) throw new InvalidOperationException("already started");
                IPAddress address = ParseBind(Context.Settings.Bind);
                Context.ResetStop();
                var server = new HttpServer(address, Context.Settings.Port, Handle, Context.Logger.For("http"), limits);
                server.RequestBodyLimit = BodyLimitFor;
                server.Start();
                http = server;
                Context.Port = server.Port;
                Context.Log.Info("serving the Federation at " + Context.BaseUrl + " (bound to " + server.LocalEndPoint + ")");
            }
        }

        /// <summary>Stops serving; safe to call more than once.</summary>
        public void Stop()
        {
            HttpServer server;
            lock (gate)
            {
                server = http;
                http = null;
            }
            if (server == null) return;
            Context.SignalStop();
            server.Stop();
        }

        /// <summary>Checks one request with the <see cref="LocalAccessGate"/>, routes it and logs it. Never throws.</summary>
        public HttpResponse Handle(HttpRequest request)
        {
            int started = Environment.TickCount;
            try { Boot.RequestStarted(request); } catch (Exception) { }
            HttpResponse response = Refusal(request);
            if (response == null) response = Router.Dispatch(request);
            int elapsed = Environment.TickCount - started;
            try
            {
                requestLogger.LogRequest(request, response, elapsed);
            }
            catch (Exception)
            {
            }
            try { Boot.RequestFinished(request, response); } catch (Exception) { }
            return response;
        }

        /// <summary>
        /// The gate's answer for a refused request (logged under the route name "core.gate"), or null. Reads
        /// security.hostCheck, lanMode, bind, publicUrl and admin.openVia per request, so the file-only switches apply
        /// without a restart.
        /// </summary>
        private HttpResponse Refusal(HttpRequest request)
        {
            try
            {
                string refusal;
                HttpResponse response = LocalAccessGate.Check(request, CurrentGateOptions(), out refusal);
                if (response == null) return null;
                request.SetRoute("core.gate", RouteValues.Empty);
                if (refusal == LocalAccessGate.RefusedFederationHost) WarnRefusedHost(request);
                return response;
            }
            catch (Exception ex)
            {
                // A gate bug must not open the admin routes: refuse them, let the game's routes through.
                gateLog.Error("gate failed for #" + request.Id, ex);
                if (!LocalAccessGate.IsAdminPath(request)) return null;
                request.SetRoute("core.gate", RouteValues.Empty);
                return LocalAccessGate.Refuse(HttpStatus.NotFound, "not_found", "Not found.");
            }
        }

        private GateOptions CurrentGateOptions()
        {
            Settings settings = Context.Settings;
            bool loopback;
            try
            {
                loopback = IPAddress.IsLoopback(ParseBind(settings.Bind));
            }
            catch (ArgumentException)
            {
                loopback = false;
            }
            return new GateOptions
            {
                Port = Context.Port,
                BoundToLoopback = loopback,
                LanMode = settings.LanMode,
                HostCheck = settings.GetBool("security.hostCheck", true),
                PublicUrlHost = LocalAccessGate.HostOfUrl(settings.PublicUrl),
                OpenVia = settings.GetString("admin.openVia", "auto"),
            };
        }

        /// <summary>At most one WARN per minute: a page in a browser trying to reach the server by another name.</summary>
        private void WarnRefusedHost(HttpRequest request)
        {
            lock (gate)
            {
                int now = Environment.TickCount;
                if (hostWarned && now - lastHostWarningTick < 60000) return;
                hostWarned = true;
                lastHostWarningTick = now;
            }
            string host = request.Host ?? "";
            if (host.Length > 100) host = host.Substring(0, 100) + "...";
            gateLog.Warn("gate: refused Host '" + host + "' for " + Truncate(request.Path, 200) + " (DNS rebinding?)");
        }

        private long BodyLimitFor(string method, string target, HttpHeaders headers)
        {
            return LocalAccessGate.BodyLimit(method, target, headers, Context.IsValidAdminSession, Context.AdminRouteBodyLimit);
        }

        private static string Truncate(string s, int max)
        {
            return s == null || s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
            }
            Stop();
            try { Boot.Dispose(); } catch (Exception) { }
            try { requestLogger.Dispose(); } catch (Exception) { }
            Context.Logger.Dispose();
        }

        /// <summary>"127.0.0.1", "0.0.0.0" / "*" (any IPv4), "::" (dual-stack), "localhost", or any literal address.</summary>
        public static IPAddress ParseBind(string bind)
        {
            string b = (bind ?? "").Trim();
            if (b.Length == 0 || b == "localhost") return IPAddress.Loopback;
            if (b == "*" || b == "0.0.0.0") return IPAddress.Any;
            if (b == "::" || b == "[::]") return IPAddress.IPv6Any;
            IPAddress address;
            if (IPAddress.TryParse(b.Trim('[', ']'), out address)) return address;
            throw new ArgumentException("invalid bind address '" + bind + "'");
        }
    }
}
