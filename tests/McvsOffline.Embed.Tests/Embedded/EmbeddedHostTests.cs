using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Server.Core;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    [Collection("GameHooks")]
    public class EmbeddedHostTests
    {
        [Fact]
        public void StartsTheServerAndWritesArgsNextToData()
        {
            string root = TestEnvironment.NewTempDir("host");
            Directory.CreateDirectory(Path.Combine(root, "Data"));
            int port = TestEnvironment.FreePort();

            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false), new EmbeddedHostOptions { Port = port });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.Null(host.ServerError);
                Assert.NotNull(host.Server);
                Assert.Equal(Path.Combine(root, "McvsOffline"), host.Server.Context.DataDir);

                using (var http = new HttpClient())
                {
                    HttpResponseMessage response = http.GetAsync("http://127.0.0.1:" + port + "/").Result;
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }

                byte[] args = File.ReadAllBytes(Path.Combine(root, "args.txt"));
                var game = GameArgsSimulator.Parse(args);
                Assert.Equal("Embedded", game["ServerLocation"]);
                Assert.Equal("QuickLaunch", game["AnubisCommand"]);
                Assert.Equal("http://127.0.0.1:" + port + "/", game["EnvironmentSwitcherUrl"]);
                Assert.Equal(host.DeviceId, game["DeviceId"]);
                Assert.Equal("mcvs", game["DataCenter"]);   // no Federation asks eve for the datacenter list (device test 1)
                Assert.False(game.ContainsKey("LocalAnubisHost"));

                Assert.True(File.Exists(Path.Combine(root, "McvsOffline", "settings.json")));
                Assert.True(File.Exists(Path.Combine(root, "McvsOffline", "device-id.txt")));
                Assert.True(host.FlushLogs(10000));
                string bootstrap = File.ReadAllText(Path.Combine(root, "McvsOffline", "logs", "bootstrap.log"));
                Assert.Contains("[boot] +", bootstrap);
                Assert.Contains("MCVS Offline host started by the game", bootstrap);
                Assert.Contains("offline server listening at http://127.0.0.1:" + port + "/", bootstrap);
                Assert.Contains("args.txt updated", bootstrap);
                string serverLog = File.ReadAllText(Path.Combine(root, "McvsOffline", "logs", "server.log"));
                Assert.Contains("GET / -> 200", serverLog);
                string settings = File.ReadAllText(Path.Combine(root, "McvsOffline", "settings.json"));
                Assert.Contains("\"manageArgs\": true", settings);
                Assert.Contains("\"nativeOnlineSdk\": false", settings);
                Assert.Contains("\"levels\": \"error warning\"", settings);
                Assert.Contains("\"watchdog\": true", settings);
                Assert.False(McvsOffline.GameHooks.AllowNativeOnlineInit("mcvs"));
                Assert.Contains("native online SDK init requested", string.Join("\n", host.Boot.Milestones()));
                Assert.Contains("boot: ", host.DescribeState());
                Assert.Contains("server.log: ", host.DescribeState());
            }
            finally
            {
                host.Server?.Dispose();
            }

            // Second launch: same device id, args.txt untouched.
            DateTime written = File.GetLastWriteTimeUtc(Path.Combine(root, "args.txt"));
            byte[] before = File.ReadAllBytes(Path.Combine(root, "args.txt"));
            EmbeddedHost again = EmbeddedHost.Start(new DirectoryPlatform(root, false), new EmbeddedHostOptions { Port = port });
            try
            {
                Assert.True(again.WaitForServer(30000));
                Assert.Equal(host.DeviceId, again.DeviceId);
                Assert.False(again.Args.Changed);
                Assert.Equal(before, File.ReadAllBytes(Path.Combine(root, "args.txt")));
                Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(root, "args.txt")));
            }
            finally
            {
                again.Server?.Dispose();
            }
        }

        [Fact]
        public void BusyPortIsReportedNotThrownAndArgsAreStillWritten()
        {
            string root = TestEnvironment.NewTempDir("busy");
            var blocker = new TcpListener(IPAddress.Loopback, 0);
            blocker.Start();
            int port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            try
            {
                EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                       new EmbeddedHostOptions { Port = port, StartAttempts = 2, RetryDelayMs = 50 });
                Assert.True(host.WaitForServer(30000));
                Assert.Null(host.Server);
                Assert.IsAssignableFrom<SocketException>(host.ServerError);
                Assert.Equal("http://127.0.0.1:" + port + "/",
                             GameArgsSimulator.Parse(File.ReadAllBytes(Path.Combine(root, "args.txt")))["EnvironmentSwitcherUrl"]);
            }
            finally
            {
                blocker.Stop();
            }
        }

        [Fact]
        public void ManageArgsFalseLeavesArgsTxtAlone()
        {
            string root = TestEnvironment.NewTempDir("noargs");
            Directory.CreateDirectory(Path.Combine(root, "McvsOffline"));
            File.WriteAllText(Path.Combine(root, "McvsOffline", "settings.json"), "{\"embedded\": {\"manageArgs\": false}}");
            File.WriteAllText(Path.Combine(root, "args.txt"), "-Offline\n");

            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                   new EmbeddedHostOptions { Port = TestEnvironment.FreePort() });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.Equal("-Offline\n", File.ReadAllText(Path.Combine(root, "args.txt")));
                Assert.Null(host.Args);
            }
            finally
            {
                host.Server?.Dispose();
            }
        }

        /// <summary>
        /// A settings.json with a syntax error survives the game start byte for byte: the host runs on the defaults in
        /// memory and does not add its embedded.* keys to the file (which would replace it with the defaults).
        /// </summary>
        [Fact]
        public void AMalformedSettingsFileSurvivesTheStartByteForByte()
        {
            string root = TestEnvironment.NewTempDir("badsettings");
            Directory.CreateDirectory(Path.Combine(root, "McvsOffline"));
            string path = Path.Combine(root, "McvsOffline", "settings.json");
            const string broken = "{\n  \"unlockAll\": true\n  \"devMenu\": true\n}\n"; // missing comma
            File.WriteAllText(path, broken);

            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                   new EmbeddedHostOptions { Port = TestEnvironment.FreePort() });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.NotNull(host.Server);
                Assert.True(host.Settings.IsFallback);
                Assert.False(host.Server.Context.Settings.UnlockAll); // defaults in memory
                Assert.True(File.Exists(Path.Combine(root, "args.txt"))); // the game still finds the server
            }
            finally
            {
                host.Server?.Dispose();
            }
            Assert.Equal(broken, File.ReadAllText(path));
        }

        [Fact]
        public void LanModeAddsLocalAnubisHostAndListensOnAllInterfaces()
        {
            string root = TestEnvironment.NewTempDir("lan");
            Directory.CreateDirectory(Path.Combine(root, "McvsOffline"));
            File.WriteAllText(Path.Combine(root, "McvsOffline", "settings.json"), "{\"lanMode\": true}");

            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                   new EmbeddedHostOptions { Port = TestEnvironment.FreePort() });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.NotNull(host.Server);
                Assert.Equal("0.0.0.0", GameArgsSimulator.Parse(File.ReadAllBytes(Path.Combine(root, "args.txt")))["LocalAnubisHost"]);
                Assert.Equal("0.0.0.0", host.Server.Context.Settings.Bind);
            }
            finally
            {
                host.Server?.Dispose();
            }
        }

        [Fact]
        public void PlatformDiagnosticsAreUsedWhenThePlatformHasThem()
        {
            string root = TestEnvironment.NewTempDir("diag");
            var platform = new DiagnosticPlatform(root);
            EmbeddedHost host = EmbeddedHost.Start(platform, new EmbeddedHostOptions { Port = TestEnvironment.FreePort() });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.Equal("error warning", platform.Levels);
                Assert.NotNull(platform.GameLog);
                Assert.Equal(Path.Combine(root, "McvsOffline", "logs", "game.log"), platform.GameLog.Path);
                Assert.Same(host, platform.Started);
                platform.GameLog.AppendLine("12:00:00.000 Info [Online] {Federation.cs}.LoadConfig:304 | LoadConfig for c on mcvs");
                Assert.True(host.FlushLogs(10000));
                Assert.Contains("LoadConfig for c on mcvs", File.ReadAllText(platform.GameLog.Path));
                Assert.True(host.FlushLogs(10000));
                Assert.Contains("[gamelog] Gameloft.Logs levels: applied error warning", File.ReadAllText(Path.Combine(root, "McvsOffline", "logs", "bootstrap.log")));
            }
            finally
            {
                host.Server?.Dispose();
            }
        }

        [Fact]
        public void PlatformFailureNeverThrows()
        {
            EmbeddedHost host = EmbeddedHost.Start(new FailingPlatform(), new EmbeddedHostOptions());

            Assert.NotNull(host);
            Assert.True(host.WaitForServer(1000));
            Assert.Null(host.Server);
        }

        private sealed class DiagnosticPlatform : IEmbeddedPlatform, IPlatformDiagnostics
        {
            private readonly string root;

            public DiagnosticPlatform(string root)
            {
                this.root = root;
            }

            public string Levels;
            public SafeLogFile GameLog;
            public EmbeddedHost Started;

            public string Name => "diagnostic";

            public string EntryPoint => "test host";

            public string ArgsReader => "nobody";

            public string PortInUseHint => "";

            public string ResolveGameRoot(Log log) => root;

            public LogSink CreateSystemLogSink(LogLevel minLevel) => null;

            public void InstallCrashHooks() { }

            public string FallbackLogDirectory() => Path.Combine(root, "internal");

            public string ApplyGameLog(Settings settings, SafeLogFile gameLog)
            {
                Levels = settings.GetString("gameLog.levels", "?");
                GameLog = gameLog;
                return "applied " + Levels;
            }

            public string JavaStacks(string gameThreadComm) => null;

            public void StartBackground(EmbeddedHost host)
            {
                Started = host;
            }

            public string StatusExtras() => "extras ok";
        }

        private sealed class FailingPlatform : IEmbeddedPlatform
        {
            public string Name => "failing";

            public string EntryPoint => "test host";

            public string ArgsReader => "nobody";

            public string PortInUseHint => "";

            public string ResolveGameRoot(Log log) => throw new InvalidOperationException("no storage");

            public LogSink CreateSystemLogSink(LogLevel minLevel) => throw new InvalidOperationException("no logcat");
        }
    }
}
