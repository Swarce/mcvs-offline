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
                Assert.Contains("\"keepStarts\": 1", settings);
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

        /// <summary>
        /// Log retention per start (docs/design/settings-page.md §7.7): every start begins new log files, records itself in
        /// logs/starts.txt and deletes the files of starts older than the previous one; the restart marker is still taken.
        /// </summary>
        [Fact]
        public void EachStartBeginsNewLogFilesAndDeletesOlderStarts()
        {
            string root = TestEnvironment.NewTempDir("logret-host");
            string logs = Path.Combine(root, "McvsOffline", "logs");
            Directory.CreateDirectory(logs);
            DateTime before = DateTime.UtcNow.AddHours(-1);
            void Plant(string name)
            {
                File.WriteAllText(Path.Combine(logs, name), name + "\n");
                File.SetLastWriteTimeUtc(Path.Combine(logs, name), before);
            }
            Plant("server.log.4");
            Plant("crash-20261028-101010-000-1.txt");
            Plant("notes.txt");

            EmbeddedHost first = Run(root, "first");
            Assert.Equal(1, first.Retention.KeepStarts);
            Assert.Null(first.Retention.CutoffUtc);   // nothing recorded before: nothing deleted by age
            Assert.True(File.Exists(Path.Combine(logs, "server.log.4")));
            Assert.Single(File.ReadAllLines(Path.Combine(logs, "starts.txt")));
            Assert.Contains("[logs] keeping this start and 1 previous: only 0 starts recorded so far, nothing deleted this time",
                            File.ReadAllText(Path.Combine(logs, "bootstrap.log")));
            File.WriteAllText(Path.Combine(logs, "restart-requested.txt"), "2026-10-29T08:00:00.000Z settings\n");
            File.SetLastWriteTimeUtc(Path.Combine(logs, "restart-requested.txt"), before);

            EmbeddedHost second = Run(root, "second");
            Assert.NotNull(second.Retention.CutoffUtc);
            string[] starts = File.ReadAllLines(Path.Combine(logs, "starts.txt"));
            Assert.Equal(2, starts.Length);
            Assert.All(starts, l => Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z directory \S+$", l));
            string bootstrap = File.ReadAllText(Path.Combine(logs, "bootstrap.log"));
            string previous = File.ReadAllText(Path.Combine(logs, "bootstrap.log.1"));
            Assert.Contains("embedded host starting", previous);
            Assert.Contains("run first", previous);
            Assert.DoesNotContain("run second", previous);
            Assert.Contains("run second", bootstrap);
            Assert.DoesNotContain("run first", bootstrap);
            Assert.Contains("restart requested from the settings page (settings)", bootstrap);
            Assert.False(File.Exists(Path.Combine(logs, "restart-requested.txt")));
            // server.log of the first run is now .1; the planted (older than both starts) files are gone.
            Assert.Contains("MCVS Offline server", File.ReadAllText(Path.Combine(logs, "server.log.1")));
            Assert.False(File.Exists(Path.Combine(logs, "server.log.4")));
            Assert.False(File.Exists(Path.Combine(logs, "server.log.5")));
            Assert.False(File.Exists(Path.Combine(logs, "crash-20261028-101010-000-1.txt")));
            Assert.True(File.Exists(Path.Combine(logs, "notes.txt")));
            Assert.Matches(@"\[logs\] keeping this start and 1 previous \(since [^)]+\): deleted 2 files, 0\.0 MB \(server\.log\.5, crash 1\)", bootstrap);
        }

        /// <summary>One start that ends completely (server and bootstrap logger closed), its retention finished.</summary>
        private static EmbeddedHost Run(string root, string marker)
        {
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                   new EmbeddedHostOptions { Port = TestEnvironment.FreePort(), Watchdog = false, StatusIntervalMs = 0 });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.NotNull(host.Server);
                Assert.True(host.WaitLogRetention(30000));
                host.Log.Info("run " + marker);
                Assert.True(host.FlushLogs(10000));
            }
            finally
            {
                host.Server?.Dispose();
                host.Logger.Dispose();   // the next "process" must not share this one's bootstrap.log writer
            }
            return host;
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
