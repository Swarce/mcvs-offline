using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Server;
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
                Assert.Contains("MCVS Encore host started by the game", bootstrap);
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
                Assert.Contains("watchdog: game thread tid ", host.DescribeState());
                Assert.DoesNotContain("[watchdog] off", bootstrap);
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
        /// Build 32: the embedded host tells its server that the game runs in this process, so the next start answers the
        /// profile lock an earlier start left (no SaveAndReleaseProfile: the app was killed) as released, over HTTP.
        /// </summary>
        [Fact]
        public void TheNextStartAnswersALockLeftByTheEarlierStartAsReleased()
        {
            string root = TestEnvironment.NewTempDir("host-owner");
            Directory.CreateDirectory(Path.Combine(root, "Data"));
            int port = TestEnvironment.FreePort();
            const string room = "LocalAnubis-Room-18478500-6c40-4424-9b53-e40b7e14c747";
            string Dump(string owner) => "{\"@version\":\"0.1\",\"$default\":{\"/Profile\":{\"@type\":\"Game.PlayerProfileComponent\"," +
                                         "\"PlayerName\":\"agent-1\",\"ProfileOwnerRoomId\":" + (owner == null ? "null" : "\"" + owner + "\"") + "}}}";
            string Token(EmbeddedHost h)
            {
                var account = h.Server.Context.Accounts.GetOrCreate("anonymous:owner-test", "device-owner-test");
                return h.Server.Context.Tokens.Issue(account.Id, "anonymous:owner-test", McvsConstants.ClientId,
                                                     new[] { "storage", "storage_ro", "storage_restricted" }, "device-owner-test", "game:mcvsoffline").Token;
            }
            string url = "http://127.0.0.1:" + port + "/profiles/me/myprofile/_mcvs.gameProfile";
            using (var http = new HttpClient())
            {
                EmbeddedHost first = EmbeddedHost.Start(new DirectoryPlatform(root, false), new EmbeddedHostOptions { Port = port });
                try
                {
                    Assert.True(first.WaitForServer(30000));
                    Assert.True(first.Server.Context.Features.Get<ClientCapabilities>().InProcessClient);
                    string token = Token(first);
                    var form = new FormUrlEncodedContent(new[]
                    {
                        new System.Collections.Generic.KeyValuePair<string, string>("visibility", "restricted"),
                        new System.Collections.Generic.KeyValuePair<string, string>("object", Dump(room)),
                        new System.Collections.Generic.KeyValuePair<string, string>("access_token", token),
                    });
                    Assert.Equal(HttpStatusCode.OK, http.PostAsync(url, form).Result.StatusCode);
                    Assert.Equal(Dump(room), http.GetStringAsync(url + "?access_token=" + token).Result);
                }
                finally
                {
                    first.Server?.Dispose();
                }

                EmbeddedHost second = EmbeddedHost.Start(new DirectoryPlatform(root, false), new EmbeddedHostOptions { Port = port });
                try
                {
                    Assert.True(second.WaitForServer(30000));
                    string token = Token(second);
                    Assert.Equal(Dump(null), http.GetStringAsync(url + "?access_token=" + token).Result);
                }
                finally
                {
                    second.Server?.Dispose();
                }
            }
            string logs = string.Join("\n", Array.ConvertAll(Directory.GetFiles(Path.Combine(root, "McvsOffline", "logs"), "*server.log*"), ReadShared));
            Assert.Contains("profile: cleared a room owner left by an earlier start: " + room, logs);
        }

        private static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
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
            Assert.Contains("MCVS Encore server", File.ReadAllText(Path.Combine(logs, "server.log.1")));
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

        // ------------------------------------------------------------------ port fallback (docs/design/port-fallback.md)

        /// <summary>
        /// The build-24 S22 case: another process holds the port, accepts connections and never answers (a frozen earlier copy
        /// of the game). Build 25 retried for 30 s and left args.txt on the busy port, so the game's eve request hung for good.
        /// </summary>
        [Fact]
        public void ASilentHolderOfThePortMovesTheServerAndArgsFollowAtOnce()
        {
            string root = TestEnvironment.NewTempDir("busy");
            using var blocker = new PortFallbackTests.SilentHolder();
            int port = blocker.Port;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // The connect budget is long (build 30: a slow CI runner must not time out the connect and misread the holder); the
            // read budget is the shipped 500 ms, which the bootstrap line below names.
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                   new EmbeddedHostOptions { Port = port, ProbePort = p => PortProbe.Probe(p, 5000, PortProbe.DefaultReadMs) });
            try
            {
                Assert.True(host.WaitForServer(30000));
                watch.Stop();
                Assert.Null(host.ServerError);
                Assert.NotNull(host.Server);
                int actual = host.Server.Port;
                Assert.NotEqual(port, actual);
                Assert.Equal(actual, host.Port);
                Assert.Equal(port, host.ConfiguredPort);
                Assert.Equal(PortOutcome.Fallback, host.PortReservation.Outcome);
                // The decision itself: one probe of about the 500 ms read budget, no retries (build 25 retried for 30 s).
                Assert.Equal(PortHolderKind.Silent, host.PortReservation.Holder.Kind);
                Assert.InRange(host.PortReservation.Holder.ElapsedMs, PortProbe.DefaultReadMs - 20, 3000);
                // The whole start (server included) stays far below build 25's 30 s of retries even on a slow runner.
                Assert.InRange(watch.ElapsedMilliseconds, 0, 15000);

                // args.txt was written with the bound port before the game could read it; the hooks follow too.
                var game = GameArgsSimulator.Parse(File.ReadAllBytes(Path.Combine(root, "args.txt")));
                Assert.Equal("http://127.0.0.1:" + actual + "/", game["EnvironmentSwitcherUrl"]);
                Assert.Equal("http://127.0.0.1:" + actual + "/", McvsOffline.PortHooks.ServerBase);
                Assert.Equal("http://127.0.0.1:" + actual + "/location/?ip=", McvsOffline.PortHooks.GeolocalizationUrl("http://127.0.0.1:51780/location/?ip="));
                using (var http = new HttpClient())
                {
                    Assert.Equal(HttpStatusCode.OK, http.GetAsync("http://127.0.0.1:" + actual + "/").Result.StatusCode);
                }

                // settings.json keeps the configured port; the server knows why it listens elsewhere.
                Assert.Equal(port, host.Server.Context.Settings.Port);
                Assert.Contains("was busy", host.Server.Context.PortNote);
                Assert.True(host.FlushLogs(10000));
                string bootstrap = File.ReadAllText(Path.Combine(root, "McvsOffline", "logs", "bootstrap.log"));
                Assert.Contains("port " + port + " is held by another program that does not answer as MCVS Encore: it accepted the connection but sent nothing in 500 ms; " +
                                "this start uses port " + actual + ": args.txt, the eve URLs, the settings page and the geo lookup follow it", bootstrap);
                Assert.Contains("port fallback: " + port + " busy, the offline server listens on " + actual, bootstrap);
                Assert.DoesNotContain("retrying", bootstrap);
                Assert.DoesNotContain("\"port\": " + actual, File.ReadAllText(Path.Combine(root, "McvsOffline", "settings.json")));
            }
            finally
            {
                host.Server?.Dispose();
                McvsOffline.PortHooks.Configure(0, 0);
            }
        }

        /// <summary>A holder that accepts and resets every connection (an adb reverse with no server on the PC side).</summary>
        [Fact]
        public void AResettingHolderOfThePortMovesTheServerToo()
        {
            string root = TestEnvironment.NewTempDir("reset");
            using var blocker = new PortFallbackTests.ResettingHolder();
            // Long probe budgets (build 30): on a slow GitHub runner the holder's thread accepted and reset the connection after
            // the shipped 500 ms read budget, and the probe read the holder as Silent. With 5 s/10 s the reset always arrives.
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                   new EmbeddedHostOptions { Port = blocker.Port, ProbePort = p => PortProbe.Probe(p, 5000, 10000) });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.NotNull(host.Server);
                Assert.NotEqual(blocker.Port, host.Server.Port);
                Assert.Equal(PortHolderKind.Closed, host.PortReservation.Holder.Kind);
                Assert.Equal("http://127.0.0.1:" + host.Server.Port + "/",
                             GameArgsSimulator.Parse(File.ReadAllBytes(Path.Combine(root, "args.txt")))["EnvironmentSwitcherUrl"]);
            }
            finally
            {
                host.Server?.Dispose();
                McvsOffline.PortHooks.Configure(0, 0);
            }
        }

        /// <summary>The dev loop: a working MCVS Encore server already answers on the port (a standalone through adb reverse).</summary>
        [Fact]
        public void AWorkingMcvsServerOnThePortIsUsedAndNoSecondServerStarts()
        {
            string root = TestEnvironment.NewTempDir("defer");
            string other = TestEnvironment.NewTempDir("standalone");
            using McvsServer standalone = McvsServer.Create(new ServerOptions { DataDir = other, Port = 0, ConsoleLog = false, FileLog = false });
            standalone.Start();
            int port = standalone.Port;
            WarmUp(port);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false), new EmbeddedHostOptions { Port = port, ProbePort = p => PortProbe.Probe(p, 5000, 10000) });
            try
            {
                Assert.True(host.WaitForServer(30000));
                watch.Stop();
                Assert.InRange(watch.ElapsedMilliseconds, 0, 3000);
                Assert.Null(host.Server);
                Assert.Null(host.ServerError);
                Assert.Equal(PortOutcome.Deferred, host.PortReservation.Outcome);
                Assert.Equal(port, host.Port);
                Assert.Equal("http://127.0.0.1:" + port + "/",
                             GameArgsSimulator.Parse(File.ReadAllBytes(Path.Combine(root, "args.txt")))["EnvironmentSwitcherUrl"]);
                Assert.True(host.FlushLogs(10000));
                string bootstrap = File.ReadAllText(Path.Combine(root, "McvsOffline", "logs", "bootstrap.log"));
                Assert.Contains("port " + port + " is held by a working MCVS Encore server", bootstrap);
                Assert.Contains("this game's own server stays off", bootstrap);
                Assert.DoesNotContain("another copy of the game is still running", bootstrap);
            }
            finally
            {
                McvsOffline.PortHooks.Configure(0, 0);
            }
        }

        /// <summary>
        /// Build 31: the owner's build-29 and build-30 logs had no [watchdog] line at all. With settings diagnostics.watchdog false
        /// one INFO line says so (and that the hitch probe is off with it), and the status line names the switch; the default stays on.
        /// </summary>
        [Fact]
        public void AWatchdogTurnedOffInTheSettingsSaysSoInTheLogAndTheStatusLine()
        {
            string root = TestEnvironment.NewTempDir("watchdog-off");
            Directory.CreateDirectory(Path.Combine(root, "McvsOffline"));
            File.WriteAllText(Path.Combine(root, "McvsOffline", "settings.json"), "{\"diagnostics\": {\"watchdog\": false}}");
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                   new EmbeddedHostOptions { Port = TestEnvironment.FreePort(), StatusIntervalMs = 0 });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.Null(host.Watchdog);
                Assert.Contains("watchdog: " + EmbeddedHost.WatchdogOffBySettings, host.DescribeState());
                Assert.Contains("hitch probe", EmbeddedHost.WatchdogOffBySettings);
                Assert.True(host.FlushLogs(10000));
                string bootstrap = File.ReadAllText(Path.Combine(root, "McvsOffline", "logs", "bootstrap.log"));
                Assert.Contains("INFO  [watchdog] " + EmbeddedHost.WatchdogOffLine, bootstrap);
                Assert.Contains("no HITCH: lines", EmbeddedHost.WatchdogOffLine);
                Assert.Contains("\"watchdog\": false", File.ReadAllText(Path.Combine(root, "McvsOffline", "settings.json")));
            }
            finally
            {
                host.Server?.Dispose();
            }
        }

        /// <summary>embedded.portFallback false pins the port: the build-25 behaviour (retries, server off, args.txt on that port).</summary>
        [Fact]
        public void APinnedBusyPortIsReportedNotThrownAndArgsKeepIt()
        {
            string root = TestEnvironment.NewTempDir("pinned");
            Directory.CreateDirectory(Path.Combine(root, "McvsOffline"));
            File.WriteAllText(Path.Combine(root, "McvsOffline", "settings.json"), "{\"embedded\": {\"portFallback\": false}}");
            using var blocker = new PortFallbackTests.SilentHolder();
            int port = blocker.Port;
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                   new EmbeddedHostOptions { Port = port, StartAttempts = 2, RetryDelayMs = 50 });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.Null(host.Server);
                Assert.IsAssignableFrom<SocketException>(host.ServerError);
                Assert.Equal(PortOutcome.Failed, host.PortReservation.Outcome);
                Assert.Equal("http://127.0.0.1:" + port + "/",
                             GameArgsSimulator.Parse(File.ReadAllBytes(Path.Combine(root, "args.txt")))["EnvironmentSwitcherUrl"]);
                Assert.True(host.FlushLogs(10000));
                Assert.Contains("embedded.portFallback is false", File.ReadAllText(Path.Combine(root, "McvsOffline", "logs", "bootstrap.log")));
            }
            finally
            {
                McvsOffline.PortHooks.Configure(0, 0);
            }
        }

        /// <summary>The player's own -EnvironmentSwitcherUrl keeps the game on the busy port: an ERROR says so, not a silent switch.</summary>
        [Fact]
        public void APlayersEnvironmentSwitcherUrlThatCannotFollowIsAnError()
        {
            string root = TestEnvironment.NewTempDir("override");
            using var blocker = new PortFallbackTests.SilentHolder();
            int port = blocker.Port;
            File.WriteAllText(Path.Combine(root, "args.txt"), "-EnvironmentSwitcherUrl http://127.0.0.1:" + port + "/\n");
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false), new EmbeddedHostOptions { Port = port });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.NotNull(host.Server);
                Assert.True(host.FlushLogs(10000));
                string bootstrap = File.ReadAllText(Path.Combine(root, "McvsOffline", "logs", "bootstrap.log"));
                Assert.Contains("ERROR", bootstrap);
                Assert.Contains("port fallback: the game will use -EnvironmentSwitcherUrl http://127.0.0.1:" + port + "/ from args.txt (set outside the managed block), " +
                                "not this start's server at http://127.0.0.1:" + host.Server.Port + "/", bootstrap);
            }
            finally
            {
                host.Server?.Dispose();
                McvsOffline.PortHooks.Configure(0, 0);
            }
        }

        /// <summary>A frozen copy holds the LocalAnubis Finder's port as well: the game's Finder and the patched client move with it.</summary>
        [Fact]
        public void ABusyFinderPortIsMovedThroughArgsAndTheHook()
        {
            string root = TestEnvironment.NewTempDir("finder");
            using var finder = new PortFallbackTests.SilentHolder(IPAddress.Any);
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                   new EmbeddedHostOptions { Port = TestEnvironment.FreePort(), FinderPort = finder.Port });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.NotEqual(finder.Port, host.FinderPort);
                var game = GameArgsSimulator.Parse(File.ReadAllBytes(Path.Combine(root, "args.txt")));
                Assert.Equal(host.FinderPort.ToString(), game["LocalAnubisFinderPort"]);
                Assert.Equal(host.FinderPort, McvsOffline.PortHooks.LocalAnubisFinderPort(51750));
                Assert.True(host.FlushLogs(10000));
                Assert.Contains("the LocalAnubis Finder port " + finder.Port + " is busy",
                                File.ReadAllText(Path.Combine(root, "McvsOffline", "logs", "bootstrap.log")));
            }
            finally
            {
                host.Server?.Dispose();
                McvsOffline.PortHooks.Configure(0, 0);
            }
        }

        /// <summary>
        /// One /_mcvs/status request to a freshly started server, as a running one has had: its first answer JITs the router and
        /// can take longer than the probe's 500 ms on a loaded test machine.
        /// </summary>
        private static void WarmUp(int port)
        {
            PortHolder warm = PortProbe.Probe(port, 5000, 10000);
            Assert.Equal(PortHolderKind.Mcvs, warm.Kind);
        }

        /// <summary>
        /// Holds a port like a copy of the game whose server is still being built: the socket listens (connections wait in the
        /// backlog) and nothing answers for <c>delayMs</c>; then every connection, the waiting ones too, gets an MCVS Encore
        /// /_mcvs/status answer naming <c>dataDir</c>.
        /// </summary>
        private sealed class StartingMcvsHolder : IDisposable
        {
            private readonly TcpListener listener;
            private volatile bool stopped;

            public StartingMcvsHolder(int delayMs, string dataDir)
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string body = "{\"server\": \"mcvs-offline\", \"version\": \"test\", \"dataDir\": \"" + dataDir.Replace("\\", "\\\\") + "\"}";
                byte[] answer = Encoding.UTF8.GetBytes("HTTP/1.0 200 OK\r\nContent-Type: application/json\r\nContent-Length: " +
                                                       Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body);
                new System.Threading.Thread(() =>
                {
                    System.Threading.Thread.Sleep(delayMs);
                    while (!stopped)
                    {
                        try
                        {
                            using Socket s = listener.AcceptSocket();
                            s.ReceiveTimeout = 2000;
                            s.Receive(new byte[4096]);
                            s.Send(answer);
                            s.Shutdown(SocketShutdown.Both);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }) { IsBackground = true }.Start();
            }

            public int Port { get; }

            public void Dispose()
            {
                stopped = true;
                listener.Stop();
            }
        }

        /// <summary>
        /// Two copies of the game started at the same moment on one data folder: the first holds the port and builds its server
        /// (silent for about a second). Its start marker makes the second wait for that server and use it, not start a second
        /// server on the same folder on another port.
        /// </summary>
        [Fact]
        public void ASecondCopyStartedAtTheSameMomentUsesTheFirstCopysServer()
        {
            string root = TestEnvironment.NewTempDir("twin");
            string data = Path.Combine(root, "McvsOffline");
            Directory.CreateDirectory(Path.Combine(data, "logs"));
            using var first = new StartingMcvsHolder(1200, data);
            int port = first.Port;
            StartMarker.Write(Path.Combine(data, "logs", StartMarker.FileName), port, StartMarker.Starting, DateTime.UtcNow);
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false), new EmbeddedHostOptions { Port = port, PortWatchIntervalMs = 60000 });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.Null(host.Server);
                Assert.Equal(PortOutcome.Deferred, host.PortReservation.Outcome);
                Assert.Contains("which was still starting", host.PortReservation.Reason);
                Assert.Equal(port, host.Port);
                Assert.Equal("http://127.0.0.1:" + port + "/",
                             GameArgsSimulator.Parse(File.ReadAllBytes(Path.Combine(root, "args.txt")))["EnvironmentSwitcherUrl"]);
                Assert.True(host.WatchingPort);
                Assert.True(host.FlushLogs(10000));
                string bootstrap = File.ReadAllText(Path.Combine(data, "logs", "bootstrap.log"));
                Assert.Contains("another copy of the game is still running", bootstrap);
                Assert.DoesNotContain("this start uses port", bootstrap);
            }
            finally
            {
                host.StopPortWatch();
                host.Server?.Dispose();
                McvsOffline.PortHooks.Configure(0, 0);
            }
        }

        /// <summary>
        /// Deferring to another copy's server on this game's data folder: a WARN (not INFO), and when that copy quits, this game's
        /// own server takes the same port over, so the running game keeps a server.
        /// </summary>
        [Fact]
        public void WhenTheOtherCopysServerStopsThisGamesOwnServerTakesThePortOver()
        {
            string root = TestEnvironment.NewTempDir("takeover");
            string data = Path.Combine(root, "McvsOffline");
            McvsServer other = McvsServer.Create(new ServerOptions { DataDir = data, Port = 0, ConsoleLog = false, FileLog = false });
            other.Start();
            int port = other.Port;
            WarmUp(port);
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false), new EmbeddedHostOptions { Port = port, PortWatchIntervalMs = 100, ProbePort = p => PortProbe.Probe(p, 5000, 10000) });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.Null(host.Server);
                Assert.Equal(PortOutcome.Deferred, host.PortReservation.Outcome);
                Assert.True(host.WatchingPort);
                System.Threading.Thread.Sleep(400);
                Assert.Null(host.Server);   // still held: no second listener

                other.Dispose();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (host.Server == null && watch.ElapsedMilliseconds < 15000) System.Threading.Thread.Sleep(50);
                Assert.NotNull(host.Server);
                Assert.Equal(port, host.Server.Port);
                Assert.Equal(PortOutcome.Configured, host.PortReservation.Outcome);
                Assert.False(host.WatchingPort);
                // Until now the game used the other copy's server (and may hold a live lock through it): locks are not followed.
                Assert.False(host.Server.Context.Features.Get<ClientCapabilities>().InProcessClient);
                using (var http = new HttpClient())
                {
                    Assert.Equal(HttpStatusCode.OK, http.GetAsync("http://127.0.0.1:" + port + "/").Result.StatusCode);
                }
                Assert.True(host.FlushLogs(10000));
                string bootstrap = File.ReadAllText(Path.Combine(data, "logs", "bootstrap.log"));
                Assert.Matches(@"WARN.*port " + port + @" is held by a working MCVS Encore server.*It uses this game's own data folder", bootstrap);
                Assert.Contains("this game's own server takes port " + port + " over", bootstrap);
                Assert.Contains("state=running", File.ReadAllText(Path.Combine(data, "logs", StartMarker.FileName)));
            }
            finally
            {
                host.StopPortWatch();
                host.Server?.Dispose();
                other.Dispose();
                McvsOffline.PortHooks.Configure(0, 0);
            }
        }

        /// <summary>The Android build's "SO_REUSEADDR not set" line, from the reserving bind, reaches bootstrap.log (build 25 logged it on the server's bind).</summary>
        [Fact]
        public void TheAddressReuseWarningOfTheReservingBindIsLogged()
        {
            string root = TestEnvironment.NewTempDir("reuse");
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false), new EmbeddedHostOptions
            {
                Port = TestEnvironment.FreePort(),
                Listen = (a, p, w) =>
                {
                    w?.Invoke("SO_REUSEADDR not set (setsockopt failed: test); right after a restart the port may stay busy for up to a minute");
                    return McvsOffline.Server.Core.Http.HttpServer.Listen(a, p, w);
                },
            });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.NotNull(host.Server);
                Assert.True(host.FlushLogs(10000));
                string bootstrap = File.ReadAllText(Path.Combine(root, "McvsOffline", "logs", "bootstrap.log"));
                Assert.Matches("WARN.*SO_REUSEADDR not set \\(setsockopt failed: test\\)", bootstrap);
            }
            finally
            {
                host.Server?.Dispose();
                McvsOffline.PortHooks.Configure(0, 0);
            }
        }

        /// <summary>
        /// A LAN guest's line names its host's server: after a fallback it is a WARN that the game uses that server, not an ERROR
        /// telling the player to remove the line.
        /// </summary>
        [Fact]
        public void AnEnvironmentSwitcherUrlForAnotherServerIsAWarningNotAnError()
        {
            string root = TestEnvironment.NewTempDir("guest");
            using var blocker = new PortFallbackTests.SilentHolder();
            int port = blocker.Port;
            File.WriteAllText(Path.Combine(root, "args.txt"), "-EnvironmentSwitcherUrl http://192.168.1.20:" + port + "/\n");
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false), new EmbeddedHostOptions { Port = port });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.NotNull(host.Server);
                Assert.Equal(PortOutcome.Fallback, host.PortReservation.Outcome);
                Assert.True(host.FlushLogs(10000));
                string bootstrap = File.ReadAllText(Path.Combine(root, "McvsOffline", "logs", "bootstrap.log"));
                Assert.DoesNotContain("it will not reach its server", bootstrap);
                Assert.Matches("WARN.*port fallback: the game uses -EnvironmentSwitcherUrl http://192\\.168\\.1\\.20:" + port +
                               "/ from args.txt \\(set outside the managed block\\), not this device's server \\(port " + host.Server.Port + "\\)", bootstrap);
            }
            finally
            {
                host.Server?.Dispose();
                McvsOffline.PortHooks.Configure(0, 0);
            }
        }

        /// <summary>
        /// A LAN guest (-RemoteIp) asks the host's Finder with the same client property: its own Finder moves, the port it asks the
        /// host on does not.
        /// </summary>
        [Fact]
        public void ALanGuestKeepsAskingTheHostsFinderPortWhenItsOwnFinderMoves()
        {
            string root = TestEnvironment.NewTempDir("guestfinder");
            using var finder = new PortFallbackTests.SilentHolder(IPAddress.Any);
            File.WriteAllText(Path.Combine(root, "args.txt"), "-RemoteIp 192.168.1.20\n");
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                   new EmbeddedHostOptions { Port = TestEnvironment.FreePort(), FinderPort = finder.Port });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.NotEqual(finder.Port, host.FinderPort);
                Assert.Equal(host.FinderPort.ToString(), GameArgsSimulator.Parse(File.ReadAllBytes(Path.Combine(root, "args.txt")))["LocalAnubisFinderPort"]);
                Assert.Equal(finder.Port, host.ClientFinderPort);
                Assert.Equal(finder.Port, McvsOffline.PortHooks.LocalAnubisFinderPort(51750));
                Assert.True(host.FlushLogs(10000));
                Assert.Contains("the client still looks for the host's Finder on " + finder.Port,
                                File.ReadAllText(Path.Combine(root, "McvsOffline", "logs", "bootstrap.log")));
            }
            finally
            {
                host.Server?.Dispose();
                McvsOffline.PortHooks.Configure(0, 0);
            }
        }

        /// <summary>A LAN host whose Finder moved tells its guests which -LocalAnubisFinderPort they need for this start.</summary>
        [Fact]
        public void ALanHostWhoseFinderMovedNamesThePortGuestsNeed()
        {
            string root = TestEnvironment.NewTempDir("lanfinder");
            Directory.CreateDirectory(Path.Combine(root, "McvsOffline"));
            File.WriteAllText(Path.Combine(root, "McvsOffline", "settings.json"), "{\"lanMode\": true}");
            using var finder = new PortFallbackTests.SilentHolder(IPAddress.Any);
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                   new EmbeddedHostOptions { Port = TestEnvironment.FreePort(), FinderPort = finder.Port });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.NotEqual(finder.Port, host.FinderPort);
                Assert.Equal(host.FinderPort, host.ClientFinderPort);
                Assert.True(host.FlushLogs(10000));
                Assert.Contains("LAN guests look for this host's LocalAnubis Finder on " + finder.Port + ": for this start they need -LocalAnubisFinderPort " +
                                host.FinderPort, File.ReadAllText(Path.Combine(root, "McvsOffline", "logs", "bootstrap.log")));
            }
            finally
            {
                host.Server?.Dispose();
                McvsOffline.PortHooks.Configure(0, 0);
            }
        }

        /// <summary>Nothing busy: the managed block is exactly build 25's, and a second start leaves args.txt alone.</summary>
        [Fact]
        public void WithFreePortsTheManagedBlockIsUnchanged()
        {
            string root = TestEnvironment.NewTempDir("same");
            int port = TestEnvironment.FreePort();
            EmbeddedHost first = EmbeddedHost.Start(new DirectoryPlatform(root, false), new EmbeddedHostOptions { Port = port });
            Assert.True(first.WaitForServer(30000));
            first.Server?.Dispose();
            byte[] written = File.ReadAllBytes(Path.Combine(root, "args.txt"));
            string expected =
                ArgsFile.BeginMarker + " " + ArgsFile.BlockHint + "\n" +
                "-ServerLocation Embedded\n-AnubisCommand QuickLaunch\n-EnvironmentSwitcherUrl http://127.0.0.1:" + port + "/\n" +
                "-SuperCredential game:mcvsoffline\n-SuperPassword mcvsoffline\n-DeviceId " + first.DeviceId + "\n-DataCenter mcvs\n" +
                ArgsFile.EndMarker + "\n";
            Assert.Equal(expected, Encoding.ASCII.GetString(written));
            Assert.Equal(51750, first.FinderPort);

            EmbeddedHost second = EmbeddedHost.Start(new DirectoryPlatform(root, false), new EmbeddedHostOptions { Port = port });
            try
            {
                Assert.True(second.WaitForServer(30000));
                Assert.Equal(PortOutcome.Configured, second.PortReservation.Outcome);
                Assert.Equal(written, File.ReadAllBytes(Path.Combine(root, "args.txt")));
                Assert.True(second.FlushLogs(10000));
                Assert.Contains("args.txt is up to date", File.ReadAllText(Path.Combine(root, "McvsOffline", "logs", "bootstrap.log")));
            }
            finally
            {
                second.Server?.Dispose();
                McvsOffline.PortHooks.Configure(0, 0);
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
