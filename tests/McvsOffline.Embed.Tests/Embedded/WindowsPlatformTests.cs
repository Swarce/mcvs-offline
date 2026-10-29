using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Embedded.Windows;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// WindowsPlatform member by member (docs/design/pc-port.md §6.4), the game-root rules, the page routes, the runtime line, the
    /// OutputDebugString writer, the install check and WindowsBoot's diagnostics.
    /// </summary>
    [Collection("GameHooks")]
    public class WindowsPlatformTests
    {
        /// <summary>A game folder with Data\MCVS.game (the marker of a game root).</summary>
        internal static string GameFolder(string prefix)
        {
            string root = TestEnvironment.NewTempDir(prefix);
            Directory.CreateDirectory(Path.Combine(root, "Data"));
            File.WriteAllText(Path.Combine(root, "Data", "MCVS.game"), "{}");
            return root;
        }

        [Fact]
        public void NamesAndTexts()
        {
            var platform = new WindowsPlatform();
            Assert.Equal("windows", platform.Name);
            Assert.Equal("EngineBridge.Class1.Start", platform.EntryPoint);
            Assert.Equal("MainLoop_Start", platform.ArgsReader);
            Assert.Contains("netsh int ipv4 show excludedportrange protocol=tcp", platform.PortInUseHint);
            // The real action: install --port sets settings.json "port", which the server listens on (not only install.json).
            Assert.Contains("McvsOffline.PcKit install --port <n>", platform.PortInUseHint);
            Assert.Contains("McvsOffline\\settings.json", platform.PortInUseHint);
            Assert.DoesNotContain("run the kit's install again", platform.PortInUseHint);
            Assert.IsType<DebugOutputSink>(platform.CreateSystemLogSink(LogLevel.Info));
            Assert.Equal(Path.GetTempPath(), platform.FallbackLogDirectory());
            Assert.Null(platform.JavaStacks("main"));
            Assert.Equal(0, platform.SdkInt);
            Assert.Empty(platform.MissingJniMembers());
            Assert.False(platform.OpenInGameBrowserWithPostData("u", "p") || platform.IsInGameBrowserOpen() || platform.QuitInGameBrowser());
            Assert.Equal(new[] { "overlay", "system" }, platform.OpenModes);
            platform.InstallCrashHooks();
            platform.StartBackground(null);
        }

        [Fact]
        public void ItIsNeitherAnInstallerNorANotificationPlatform()
        {
            string[] interfaces = typeof(WindowsPlatform).GetInterfaces().Select(i => i.Name).ToArray();
            Assert.DoesNotContain("IInstallerPlatform", interfaces);
            Assert.DoesNotContain("INotificationPlatform", interfaces);
            Assert.Contains("IPagePlatform", interfaces);
            Assert.Contains("IHostExtensions", interfaces);
            Assert.Contains("IAdminPlatform", interfaces);
            Assert.Contains("IPlatformDiagnostics", interfaces);
        }

        [Fact]
        public void CapabilitiesAreExactlyTheDesigns()
        {
            AdminPlatformCapabilities caps = new WindowsPlatform().Capabilities();
            Assert.True(caps.OpenPage && caps.ShareFile && caps.SaveToDownloads && caps.ShareText && caps.Copy);
            Assert.True(caps.ExitViaGame && caps.ExitViaHost && caps.OpenInBrowser);
            Assert.False(caps.Toast);
            Assert.True(caps.RelaunchUnconfirmed);   // until WR2 Q9
        }

        [Fact]
        public void TheGameRootNeedsDataMcvsGame()
        {
            string root = GameFolder("winroot");
            string empty = TestEnvironment.NewTempDir("winroot-empty");
            Func<string, bool> exists = File.Exists;
            Assert.Equal(root, GameRootLocator.Locate(null, root, null, exists));
            Assert.Equal(root, GameRootLocator.Locate(null, root + Path.DirectorySeparatorChar, null, exists));
            Assert.Equal(root, GameRootLocator.Locate(null, empty, () => root, exists));       // the main module's folder
            Assert.Equal(root, GameRootLocator.Locate(root, empty, null, exists));             // MCVS_GAME_ROOT first
            var error = Assert.Throws<InvalidOperationException>(() => GameRootLocator.Locate(null, empty, () => { throw new InvalidOperationException("x"); }, exists));
            Assert.Contains(Path.Combine("Data", "MCVS.game"), error.Message);
            Assert.Contains(empty, error.Message);
            Assert.Throws<InvalidOperationException>(() => GameRootLocator.Locate(null, null, null, exists));

            Assert.Equal(root, new WindowsPlatform { GameRoot = root }.ResolveGameRoot(Log.Null));
            Assert.Throws<InvalidOperationException>(() => new WindowsPlatform { GameRoot = empty }.ResolveGameRoot(Log.Null));
        }

        // ------------------------------------------------------------------ the page routes (§6.5)

        private static WindowsPlatform PagePlatform(SteamOverlayTests.FakeSteam steam, List<string> browsed)
        {
            return new WindowsPlatform { Steam = steam, BrowserLauncher = url => { lock (browsed) browsed.Add(url); } };
        }

        private static void WaitFor(List<string> list, int count)
        {
            SpinWait.SpinUntil(() => { lock (list) return list.Count >= count; }, 5000);
        }

        [Fact]
        public void AutoTakesTheOverlayWhenEnabledElseTheSystemBrowser()
        {
            var steam = new SteamOverlayTests.FakeSteam();
            var browsed = new List<string>();
            WindowsPlatform platform = PagePlatform(steam, browsed);
            Assert.Equal("overlay", platform.Open("http://127.0.0.1:51780/_mcvs/ui/#t=1", "auto"));
            Assert.Equal("http://127.0.0.1:51780/_mcvs/ui/#t=1", steam.OpenedUrl);
            Assert.True(platform.OpenInGameBrowserJni("http://x/#t=2"));
            steam.Enabled = false;
            Assert.Equal("system", platform.Open("http://x/#t=3", "auto"));
            WaitFor(browsed, 1);
            Assert.Equal(new[] { "http://x/#t=3" }, browsed);
        }

        [Fact]
        public void OverlayAndSystemTakeOnlyTheirRoute()
        {
            var steam = new SteamOverlayTests.FakeSteam { Enabled = false };
            var browsed = new List<string>();
            WindowsPlatform platform = PagePlatform(steam, browsed);
            Assert.Null(platform.Open("http://x/#t=1", "overlay"));
            Thread.Sleep(50);
            lock (browsed) Assert.Empty(browsed);
            steam.Enabled = true;
            Assert.Equal("system", platform.Open("http://x/#t=2", "system"));
            WaitFor(browsed, 1);
            Assert.Null(steam.OpenedUrl);
            Assert.Null(platform.Open(null, "auto"));
        }

        [Fact]
        public void StatusExtrasNameSteamAndTheOverlay()
        {
            var steam = new SteamOverlayTests.FakeSteam();
            var platform = new WindowsPlatform { Steam = steam };
            Assert.StartsWith("steam running yes, overlay enabled yes; pc: server World failures", platform.StatusExtras());
            steam.Enabled = false;
            steam.Running = false;
            Assert.StartsWith("steam running no, overlay enabled no", platform.StatusExtras());
            steam.Throw = new DllNotFoundException();
            Assert.StartsWith("steam running unknown, overlay enabled unknown", platform.StatusExtras());
        }

        // ------------------------------------------------------------------ the runtime line (§3.8)

        [Fact]
        public void TheRuntimeLineHasTheDocumentedShape()
        {
            string line = RuntimeInfo.Format(new RuntimeFacts
            {
                Clr = "4.0.30319.42000",
                MscorlibVersion = "4.8.9037.0 built by: NET481REL1",
                Release = 533325,
                FrameworkDescription = ".NET Framework 4.8.9037.0",
                Is64BitProcess = false,
                LargeAddressAware = false,
                OsBuild = "22631.4317",
            });
            Assert.Equal("runtime: CLR 4.0.30319.42000, mscorlib 4.8.9037.0 built by: NET481REL1, Release 533325 (4.8.1), .NET Framework 4.8.9037.0; " +
                         "process x86, LAA no; OS 22631.4317", line);
            Assert.StartsWith("runtime: CLR ?, mscorlib ?, Release ? (unknown), FrameworkDescription unknown (before 4.7.1); process x86, LAA ?; OS ?",
                              RuntimeInfo.Format(null));
            Assert.Equal("4.5.2", RuntimeInfo.ReleaseName(379893));
            Assert.Equal("4.5.2", RuntimeInfo.ReleaseName(393294));
            Assert.Equal("4.6", RuntimeInfo.ReleaseName(393295));
            Assert.Equal("4.7.2", RuntimeInfo.ReleaseName(461814));
            Assert.Equal("4.8", RuntimeInfo.ReleaseName(528049));
            Assert.Equal("before 4.5", RuntimeInfo.ReleaseName(1));
            Assert.StartsWith("runtime: CLR ", RuntimeInfo.Format(RuntimeInfo.Read()));   // this machine: never throws
        }

        [Fact]
        public void LargeAddressAwareIsReadFromThePeHeader()
        {
            string dir = TestEnvironment.NewTempDir("laa");
            byte[] image = new byte[512];
            image[0] = (byte)'M';
            image[1] = (byte)'Z';
            BitConverter.GetBytes(0x80).CopyTo(image, 0x3C);
            Encoding.ASCII.GetBytes("PE\0\0").CopyTo(image, 0x80);
            string exe = Path.Combine(dir, "game.exe");
            BitConverter.GetBytes((ushort)0x0102).CopyTo(image, 0x80 + 4 + 18);
            File.WriteAllBytes(exe, image);
            Assert.False(RuntimeInfo.IsLargeAddressAware(exe));
            BitConverter.GetBytes((ushort)0x0122).CopyTo(image, 0x80 + 4 + 18);
            File.WriteAllBytes(exe, image);
            Assert.True(RuntimeInfo.IsLargeAddressAware(exe));
            File.WriteAllText(exe, "not a PE file");
            Assert.Null(RuntimeInfo.IsLargeAddressAware(exe));
            Assert.Null(RuntimeInfo.IsLargeAddressAware(Path.Combine(dir, "missing.exe")));
            string pc = TestEnvironment.PcRefs;
            if (pc != null) Assert.False(RuntimeInfo.IsLargeAddressAware(Path.Combine(pc, "ModernCombatVersus.exe")));   // [CR] §1 row 3: no LAA
        }

        // ------------------------------------------------------------------ OutputDebugString

        [Fact]
        public void DebugOutputSplitsLongTextAndNamesTheLevel()
        {
            var written = new List<string>();
            Action<string> previous = DebugOutput.Writer;
            DebugOutput.Writer = written.Add;
            try
            {
                DebugOutput.Write(SelfLog.Warn, "file sink failed");
                Assert.Equal(new[] { "McvsOffline W file sink failed\n" }, written);
                written.Clear();
                new DebugOutputSink(LogLevel.Debug).Write(new LogEntry { Category = "boot", Message = new string('x', DebugOutput.ChunkChars + 5), Level = LogLevel.Info });
                Assert.Equal(2, written.Count);
                Assert.StartsWith("McvsOffline [boot] xxx", written[0]);
                Assert.Equal("McvsOffline " + new string('x', 12) + "\n", written[1]);   // "[boot] " + 2005 x: 2012 characters
            }
            finally
            {
                DebugOutput.Writer = previous;
            }
            DebugOutput.Write(SelfLog.Info, "off Windows the native writer gives up quietly");
        }

        // ------------------------------------------------------------------ the install check (§6.2)

        [Fact]
        public void TheInstallCheckComparesTheOriginalsWithInstallJson()
        {
            string root = GameFolder("installcheck");
            string data = Path.Combine(root, "McvsOffline");
            Directory.CreateDirectory(data);
            Assert.Contains("not installed by the kit", InstallCheck.Run(root, data).Line);
            var sb = new StringBuilder("{\"format\": 1, \"kind\": \"mcvs-pc-install\", \"originals\": {");
            for (int i = 0; i < InstallCheck.Originals.Length; i++)
            {
                string file = Path.Combine(root, InstallCheck.Originals[i]);
                File.WriteAllText(file, "original " + i);
                sb.Append(i == 0 ? "" : ", ").Append('"').Append(InstallCheck.Originals[i]).Append("\": {\"sha256\": \"").Append(InstallCheck.Sha256(file).ToUpperInvariant()).Append("\"}");
            }
            sb.Append("}, \"options\": {\"port\": 51780}}");
            File.WriteAllText(Path.Combine(data, InstallCheck.ManifestFile), sb.ToString());
            InstallCheck.Result current = InstallCheck.Run(root, data);
            Assert.False(current.Stale);
            Assert.Equal("install: current", current.Line);

            File.WriteAllText(Path.Combine(root, "Game.dll"), "Steam updated me");
            InstallCheck.Result stale = InstallCheck.Run(root, data);
            Assert.True(stale.Stale);
            Assert.StartsWith("install: stale: Steam updated the game (Game.dll changed since the install); run the kit's uninstall", stale.Line);
            File.Delete(Path.Combine(root, "Gameloft.Util.dll"));
            Assert.Contains("Gameloft.Util.dll missing", InstallCheck.Run(root, data).Line);

            File.WriteAllText(Path.Combine(data, InstallCheck.ManifestFile), "{\"format\": 2, \"originals\": {}}");
            Assert.Equal("install: unknown install.json format; no check", InstallCheck.Run(root, data).Line);
            File.WriteAllText(Path.Combine(data, InstallCheck.ManifestFile), "{not json");
            Assert.Contains("does not parse", InstallCheck.Run(root, data).Line);
        }

        /// <summary>
        /// WR2 F2: the status line's "install:" part is the error "installed, NOT ACTIVE (...)" whenever the running process shows
        /// the redirect or a witness failed, whatever the file check said ("current" is dropped, anything else is kept).
        /// </summary>
        [Fact]
        public void TheInstallLineSaysNotActiveWhenThePatchedClientIsNotRunning()
        {
            Assert.Equal("install: current", InstallCheck.Describe("install: current", null));
            Assert.Null(InstallCheck.Describe(null, null));
            Assert.Equal("install: installed, NOT ACTIVE (Game loaded from the game folder)", InstallCheck.Describe("install: current", "Game loaded from the game folder"));
            Assert.Equal("install: installed, NOT ACTIVE (Game loaded from the game folder)", InstallCheck.Describe(null, "Game loaded from the game folder"));
            Assert.Equal("install: installed, NOT ACTIVE (the 'store' patch is missing); install: stale: Steam updated the game (Game.dll changed since the install)",
                         InstallCheck.Describe("install: stale: Steam updated the game (Game.dll changed since the install)", "the 'store' patch is missing"));
        }

        // ------------------------------------------------------------------ Configure (IHostExtensions) and WindowsBoot

        [Fact]
        public void ConfigureWritesTheRuntimeLineAndWiresPcHooks()
        {
            PcHooks.ResetForTests();
            string root = GameFolder("winconfigure");
            var sink = new MemoryLogSink(LogLevel.Debug, 200);
            var started = new List<string>();
            var platform = new WindowsPlatform
            {
                GameRoot = root,
                Runtime = () => new RuntimeFacts { Clr = "4.0.30319.42000", Release = 528040, LargeAddressAware = false },
                ProcessStarter = (program, args) => { lock (started) started.Add(program); },
            };
            EmbeddedHost host = EmbeddedHost.Start(platform, new EmbeddedHostOptions { Port = TestEnvironment.FreePort(), Watchdog = false, StatusIntervalMs = 0 });
            try
            {
                host.Logger.AddSink(sink);
                Assert.True(platform.InstallChecked.WaitOne(10000));
                Assert.True(host.FlushLogs(10000));
                string log = File.ReadAllText(host.Paths.BootstrapLog);
                Assert.Contains("[boot] ", log);
                Assert.Contains("runtime: CLR 4.0.30319.42000, mscorlib ?, Release 528040 (4.8)", log);
                Assert.Contains("process x86, LAA no", log);
                Assert.Contains("install: no McvsOffline\\install.json (not installed by the kit); no check", log);
                Assert.Contains("install: no McvsOffline", platform.StatusExtras());

                // PcHooks writes into the host's log, category "pc".
                PcHooks.DefaultAutomatchHost(new object());
                Assert.True(host.FlushLogs(10000));
                Assert.Contains("[pc] quicklaunch: the automatch host could not be set", File.ReadAllText(host.Paths.BootstrapLog));
                Assert.Equal(Path.Combine(root, "McvsOffline", "exports"), platform.PublicDownloadsDir());

                // The page's "Save" (no subject) only copies into exports; "Share" also opens Explorer.
                string file = Path.Combine(TestEnvironment.NewTempDir("winplatform-share"), "logs.zip");
                File.WriteAllText(file, "zip");
                AdminShareResult saved = platform.ShareFile(file, "application/zip", "mcvs-logs.zip", null, "MCVS Offline logs");
                Assert.Equal("file", saved.Method);
                Assert.True(File.Exists(Path.Combine(root, "McvsOffline", "exports", "mcvs-logs.zip")));
                lock (started) Assert.Empty(started);
                AdminShareResult shared = platform.ShareFile(file, "application/zip", "mcvs-logs.zip", "MCVS Offline logs", "MCVS Offline logs");
                Assert.Equal("explorer", shared.Method);
                Assert.Equal("McvsOffline\\exports\\mcvs-logs (2).zip", shared.PublicPath);
                lock (started) Assert.Equal(new[] { "explorer.exe" }, started);
            }
            finally
            {
                Assert.True(host.WaitForServer(30000));
                host.Server?.Dispose();
                PcHooks.ResetForTests();
            }
        }

        [Fact]
        public void TheAssemblyLoadSubscriptionSeesLoadsAndEnds()
        {
            var seen = new List<string>();
            Action stop = new WindowsPlatform().WatchAssemblyLoads(a => { lock (seen) seen.Add(a.GetName().Name); });
            Assert.NotNull(stop);
            byte[] image = FixtureCompiler.Compile("WatchMe" + Guid.NewGuid().ToString("N"), "public class X { }");
            using (var sandbox = new FixtureCompiler.Sandbox())
            {
                System.Reflection.Assembly loaded = sandbox.LoadFromStream(new MemoryStream(image));
                lock (seen) Assert.Contains(loaded.GetName().Name, seen);
                stop();
                int count;
                lock (seen) count = seen.Count;
                sandbox.LoadFromStream(new MemoryStream(FixtureCompiler.Compile("WatchMeNot" + Guid.NewGuid().ToString("N"), "public class Y { }")));
                lock (seen) Assert.Equal(count, seen.Count);
            }
            Assert.Null(new WindowsPlatform().WatchAssemblyLoads(null));
        }

        [Fact]
        public void WindowsBootRunsItsStepsOnceAndNeverThrows()
        {
            Action diagnostics = WindowsBoot.InstallDiagnosticsAction;
            Func<EmbeddedHost> start = WindowsBoot.StartAction;
            var steps = new List<string>();
            try
            {
                WindowsBoot.ResetForTests();
                WindowsBoot.InstallDiagnosticsAction = () => { steps.Add("diagnostics"); throw new InvalidOperationException("x"); };
                WindowsBoot.StartAction = () => { steps.Add("start"); throw new InvalidOperationException("y"); };
                WindowsBoot.Init();
                WindowsBoot.Init();
                Assert.Equal(new[] { "diagnostics", "start" }, steps);
                Assert.Null(WindowsBoot.Host);
            }
            finally
            {
                WindowsBoot.InstallDiagnosticsAction = diagnostics;
                WindowsBoot.StartAction = start;
                WindowsBoot.ResetForTests();
            }
        }

        [Fact]
        public void TheUnobservedTaskEventIsThePublicOne()
        {
            int calls = 0;
            EventHandler<UnobservedTaskExceptionEventArgs> handler = (s, e) => Interlocked.Increment(ref calls);
            Assert.True(WindowsBoot.SubscribeUnobservedTasks(handler));
            TaskScheduler.UnobservedTaskException -= handler;
        }

        [Fact]
        public void SettingTheWorkingDirectoryNeverThrows()
        {
            // (Changing it for real would disturb the tests running in parallel: only the no-op and failure paths here.)
            string before = Environment.CurrentDirectory;
            WindowsBoot.SetWorkingDirectory(before);
            WindowsBoot.SetWorkingDirectory(Path.Combine(TestEnvironment.NewTempDir("cwd"), "missing"));
            WindowsBoot.SetWorkingDirectory(null);
            Assert.Equal(before, Environment.CurrentDirectory);
        }
    }
}
