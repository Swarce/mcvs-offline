using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Embedded.Notifications;
using McvsOffline.Embedded.Windows;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// EmbeddedHost on a Windows platform (docs/design/pc-port.md §6.2, §12.2 EmbeddedHostTests): the defaults it writes, lanMode
    /// forced off, GraphicsHooks.ConfigureWindows, PcHooks, the *_args.txt warning, and P1 on the host side (a boot adds only
    /// args.txt and McvsOffline\ to the game folder). Android's defaults stay as they were.
    /// </summary>
    [Collection("GameHooks")]
    public class WindowsEmbeddedHostTests
    {
        /// <summary>The top-level entries of the Steam depot (build 6372049), as WP0 listed them.</summary>
        internal static readonly string[] DepotFiles =
        {
            "EngineBridge.dll", "Game.dll", "GameManager.dll", "Gameloft.Aspects.dll", "Gameloft.Collections.dll", "Gameloft.Engine.Math.dll",
            "Gameloft.Engine.dll", "Gameloft.Logs.dll", "Gameloft.OnlinePlatform.dll", "Gameloft.Sequences.dll", "Gameloft.Templates.dll",
            "Gameloft.Util.dll", "ModernCombatVersus.exe", "NativeEngine.dll", "Newtonsoft.Json.dll", "Shaders_high_end.pak", "Shaders_low_end.pak",
            "WCPToolkit.dll", "nativegame.dll", "steam_api.dll", "steam_appid.txt",
        };

        internal static readonly string[] DepotFolders = { "Assets", "Data", "Strings", "_CommonRedist" };

        /// <summary>A copy of the depot's names (empty files) with Data\MCVS.game.</summary>
        internal static string DepotRoot(string prefix)
        {
            string root = TestEnvironment.NewTempDir(prefix);
            foreach (string folder in DepotFolders) Directory.CreateDirectory(Path.Combine(root, folder));
            foreach (string file in DepotFiles) File.WriteAllBytes(Path.Combine(root, file), new byte[0]);
            File.WriteAllText(Path.Combine(root, "Data", "MCVS.game"), "{}");
            return root;
        }

        private static WindowsPlatform Platform(string root)
        {
            return new WindowsPlatform { GameRoot = root, Steam = new SteamOverlayTests.FakeSteam(), CheckInstall = false };
        }

        private static EmbeddedHost Boot(WindowsPlatform platform)
        {
            EmbeddedHost host = EmbeddedHost.Start(platform, new EmbeddedHostOptions { Port = TestEnvironment.FreePort(), Watchdog = false, StatusIntervalMs = 0 });
            Assert.NotNull(host);
            Assert.True(host.WaitForServer(30000));
            Assert.Null(host.ServerError);
            return host;
        }

        private static Dictionary<string, object> SettingsFile(EmbeddedHost host)
        {
            return (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(host.Paths.SettingsFile));
        }

        [Fact]
        public void APcHostWritesOnlyThePcDefaults()
        {
            string root = DepotRoot("pchost-defaults");
            EmbeddedHost host = Boot(Platform(root));
            try
            {
                Dictionary<string, object> settings = SettingsFile(host);
                Assert.False(settings.ContainsKey("install"));
                Assert.False(settings.ContainsKey("hd"));
                Assert.False(settings.ContainsKey("notifications"));
                var diagnostics = (Dictionary<string, object>)settings["diagnostics"];
                Assert.Equal(new[] { "watchdog" }, diagnostics.Keys.ToArray());
                var physics = (Dictionary<string, object>)((Dictionary<string, object>)settings["embedded"])["physics"];
                Assert.Equal(true, physics["keepPhysicsLayer"]);
                Assert.Equal("auto", ((Dictionary<string, object>)settings["graphics"])["profile"]);
                Assert.True(host.FlushLogs(10000));
                string log = File.ReadAllText(host.Paths.BootstrapLog);
                Assert.Contains("MCVS Offline host started by the game (EngineBridge.Class1.Start)", log);
                Assert.Contains("args.txt updated: the native engine reads it at MainLoop_Start", log);
                Assert.Contains("runtime: CLR ", log);
                Assert.Contains("not needed on Windows: the logs are plain files in " + host.Paths.LogsDir, host.MirrorStatus());
                Assert.Contains("steam running yes, overlay enabled yes", host.DescribeState());
            }
            finally
            {
                host.Server?.Dispose();
                GraphicsHooks.ResetForTests();
                PcHooks.ResetForTests();
            }
        }

        [Fact]
        public void LanModeIsForcedOffWithOneWarning()
        {
            string root = DepotRoot("pchost-lan");
            Directory.CreateDirectory(Path.Combine(root, "McvsOffline"));
            File.WriteAllText(Path.Combine(root, "McvsOffline", "settings.json"), "{\"lanMode\": true}");
            EmbeddedHost host = Boot(Platform(root));
            try
            {
                Assert.False(host.Settings.LanMode);
                Assert.False(host.Server.Context.Settings.LanMode);
                Assert.Equal("127.0.0.1", host.Server.Context.BaseUrl.Split('/')[2].Split(':')[0]);
                Assert.True(host.FlushLogs(10000));
                string log = File.ReadAllText(host.Paths.BootstrapLog);
                Assert.Single(log.Split('\n'), l => l.Contains("WARN") && l.Contains("lanMode is true, but LAN play is not supported on PC"));
                Assert.Equal(true, SettingsFile(host)["lanMode"]);   // the file keeps the player's value
            }
            finally
            {
                host.Server?.Dispose();
                GraphicsHooks.ResetForTests();
                PcHooks.ResetForTests();
            }
        }

        [Fact]
        public void GraphicsHooksGetTheWindowsFileAndThePhysicsSetting()
        {
            string root = DepotRoot("pchost-graphics");
            EmbeddedHost host = Boot(Platform(root));
            try
            {
                Assert.True(GraphicsHooks.IsWindows);
                Assert.Contains("GameOptions_windows.json", GraphicsHooks.Describe());
                Assert.NotEqual(GameOptionsWindowsTests.LookAlike, GraphicsHooks.GameOptionsJson(GameOptionsWindowsTests.LookAlike));   // MEM rule on
            }
            finally
            {
                host.Server?.Dispose();
                GraphicsHooks.ResetForTests();
            }

            File.WriteAllText(Path.Combine(root, "McvsOffline", "settings.json"), "{\"embedded\": {\"physics\": {\"keepPhysicsLayer\": false}}}");
            host = Boot(Platform(root));
            try
            {
                Assert.True(GraphicsHooks.IsWindows);
                Assert.Same(GameOptionsWindowsTests.LookAlike, GraphicsHooks.GameOptionsJson(GameOptionsWindowsTests.LookAlike));
                Assert.Equal("off", GraphicsHooks.MemRule);
                Assert.True(host.FlushLogs(10000));
                Assert.Contains("keepPhysicsLayer is false", File.ReadAllText(host.Paths.BootstrapLog));
            }
            finally
            {
                host.Server?.Dispose();
                GraphicsHooks.ResetForTests();
                PcHooks.ResetForTests();
            }

            // Android-like hosts never configure the Windows path.
            string other = TestEnvironment.NewTempDir("pchost-directory");
            EmbeddedHost directory = EmbeddedHost.Start(new DirectoryPlatform(other, false), new EmbeddedHostOptions { Port = TestEnvironment.FreePort(), Watchdog = false, StatusIntervalMs = 0 });
            try
            {
                Assert.True(directory.WaitForServer(30000));
                Assert.False(GraphicsHooks.IsWindows);
            }
            finally
            {
                directory.Server?.Dispose();
            }
        }

        [Fact]
        public void AnArgsPrefixFileGivesTheWarningAndTheStatusLine()
        {
            string root = DepotRoot("pchost-prefix");
            File.WriteAllText(Path.Combine(root, "steam_args.txt"), "-Online");
            EmbeddedHost host = Boot(Platform(root));
            try
            {
                Assert.True(host.FlushLogs(10000));
                string log = File.ReadAllText(host.Paths.BootstrapLog);
                Assert.Contains("WARN", log.Split('\n').Single(l => l.Contains("steam_args.txt present: the engine ignores args.txt")));
                Assert.Contains("steam_args.txt present: the engine ignores args.txt (remove it, or the game goes online)", host.DescribeState());
            }
            finally
            {
                host.Server?.Dispose();
                GraphicsHooks.ResetForTests();
                PcHooks.ResetForTests();
            }
        }

        [Fact]
        public void ABootAddsOnlyArgsTxtAndMcvsOfflineToTheGameFolder()
        {
            string root = DepotRoot("pchost-p1");
            File.WriteAllText(Path.Combine(root, "args.txt"), "-Width 1280\r\n");   // the player's own args.txt is merged, not replaced
            string[] before = Directory.GetFileSystemEntries(root).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var sizes = DepotFiles.ToDictionary(f => f, f => new FileInfo(Path.Combine(root, f)).Length);
            EmbeddedHost host = Boot(Platform(root));
            try
            {
                Assert.True(host.FlushLogs(10000));
            }
            finally
            {
                host.Server?.Dispose();
                GraphicsHooks.ResetForTests();
                PcHooks.ResetForTests();
            }
            string[] after = Directory.GetFileSystemEntries(root).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            string[] expected = before.Union(new[] { "args.txt", "McvsOffline" }).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, after);
            foreach (string folder in DepotFolders.Where(f => f != "Data")) Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(root, folder)));
            Assert.Equal(new[] { "MCVS.game" }, Directory.GetFileSystemEntries(Path.Combine(root, "Data")).Select(Path.GetFileName).ToArray());
            foreach (string file in DepotFiles) Assert.Equal(sizes[file], new FileInfo(Path.Combine(root, file)).Length);
            Assert.StartsWith("-Width 1280", File.ReadAllText(Path.Combine(root, "args.txt")));
        }

        [Fact]
        public void TheDepotNamesAreTheRealOnes()
        {
            string pc = TestEnvironment.PcRefs;
            if (pc == null)
            {
                Assert.False(TestEnvironment.RequirePc, "MCVS_REQUIRE_PC=1 but MCVS_PC_REFS is missing");
                return;
            }
            string[] real = Directory.GetFileSystemEntries(pc).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(DepotFiles.Concat(DepotFolders).OrderBy(n => n, StringComparer.Ordinal).ToArray(), real);
        }

        // ------------------------------------------------------------------ Android keeps its defaults

        private sealed class AndroidLikePlatform : IEmbeddedPlatform, IInstallerPlatform, INotificationPlatform
        {
            private readonly string root;

            public AndroidLikePlatform(string root)
            {
                this.root = root;
            }

            public string Name { get { return "android"; } }
            public string EntryPoint { get { return "NicePlaySDKDLL.OnInitialize"; } }
            public string ArgsReader { get { return "JNIBridge.NativeInit"; } }
            public string PortInUseHint { get { return "adb reverse"; } }
            public string ResolveGameRoot(Log log) { return root; }
            public LogSink CreateSystemLogSink(LogLevel minLevel) { return null; }
            public string InternalFilesDir() { return null; }
            public McvsOffline.Embedded.Install.IInstallerPrefs InstallerPrefs() { return null; }
            public void MonitorInstaller(Log log, BootTrace boot) { }
            public int InstallerHandedOver() { return 1; }
            public int SdkInt { get { return 34; } }
            public int TargetSdk() { return 33; }
            public bool? Declared() { return true; }
            public bool? Granted() { return true; }
            public bool Request() { return true; }
        }

        [Fact]
        public void AnAndroidHostStillGetsEveryAndroidDefault()
        {
            string root = TestEnvironment.NewTempDir("androidhost-defaults");
            Directory.CreateDirectory(Path.Combine(root, "Data"));
            EmbeddedHost host = EmbeddedHost.Start(new AndroidLikePlatform(root), new EmbeddedHostOptions { Port = TestEnvironment.FreePort(), Watchdog = false, StatusIntervalMs = 0 });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Dictionary<string, object> settings = SettingsFile(host);
                Assert.Equal(new[] { "manageInstaller", "suppressUpdateCheck", "rebuildIndex", "verifyRestoredData", "monitorInstaller" },
                             ((Dictionary<string, object>)settings["install"]).Keys.ToArray());
                Assert.Equal(true, ((Dictionary<string, object>)settings["hd"])["enabled"]);
                Assert.Equal(new[] { "request", "requestedAt" }, ((Dictionary<string, object>)settings["notifications"]).Keys.ToArray());
                var diagnostics = (Dictionary<string, object>)settings["diagnostics"];
                Assert.Equal(true, diagnostics["publicLogCopy"]);
                Assert.Equal(true, diagnostics["threadDump"]);
                Assert.False(((Dictionary<string, object>)((Dictionary<string, object>)settings["embedded"])["physics"]).ContainsKey("keepPhysicsLayer"));
                Assert.False(GraphicsHooks.IsWindows);
            }
            finally
            {
                host.Server?.Dispose();
            }
        }
    }

    /// <summary>
    /// The Windows-only one-line delegations in the built PC DLL (docs/design/pc-port.md §3.1, §12.2): Bootstrap.Init calls
    /// WindowsBoot.Init, which reaches EmbeddedHost.Start with a new WindowsPlatform; FileSystem.Rename calls WindowsFiles.Rename
    /// (WP1's MoveFileExW behind FileSystem.Retry once it lands); nothing calls into Install/ or Hd/. Together with the net8 tests
    /// running WindowsBoot, WindowsFiles and WindowsThreads, every #if MCVS_WINDOWS line is covered or a one-line delegation.
    /// </summary>
    public class PcHostWiringTests
    {
        private readonly ITestOutputHelper output;

        public PcHostWiringTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private ModuleDefinition PcDll()
        {
            string dll = TestEnvironment.PcServerDll;
            if (dll == null)
            {
                if (TestEnvironment.RequirePc) throw new InvalidOperationException("MCVS_REQUIRE_PC=1 but the PC server DLL of this configuration is not built");
                output.WriteLine("the PC server DLL is not built: skipped");
                return null;
            }
            return ModuleDefinition.ReadModule(dll, new ReaderParameters { InMemory = true });
        }

        private static IEnumerable<MethodReference> Calls(MethodDefinition method)
        {
            return method.Body.Instructions.Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj)
                         .Select(i => (MethodReference)i.Operand);
        }

        private static MethodDefinition Method(ModuleDefinition module, string type, string name)
        {
            TypeDefinition t = module.GetType(type);
            Assert.True(t != null, type + " is missing");
            MethodDefinition m = t.Methods.FirstOrDefault(x => x.Name == name);
            Assert.True(m != null, type + "::" + name + " is missing");
            return m;
        }

        [Fact]
        public void BootstrapInitReachesEmbeddedHostStartWithAWindowsPlatform()
        {
            using (ModuleDefinition module = PcDll())
            {
                if (module == null) return;
                MethodDefinition init = Method(module, "McvsOffline.Bootstrap", "Init");
                Assert.Equal(new[] { "McvsOffline.Embedded.Windows.WindowsBoot::Init" }, Calls(init).Select(c => c.DeclaringType.FullName + "::" + c.Name).ToArray());
                MethodDefinition core = Method(module, "McvsOffline.Embedded.Windows.WindowsBoot", "InitCore");
                List<string> calls = Calls(core).Select(c => c.DeclaringType.FullName + "::" + c.Name).ToList();
                Assert.Contains("McvsOffline.Embedded.Windows.WindowsPlatform::.ctor", calls);
                Assert.Contains("McvsOffline.Embedded.EmbeddedHost::Start", calls);
                Assert.True(calls.IndexOf("McvsOffline.Embedded.Windows.WindowsPlatform::.ctor") < calls.IndexOf("McvsOffline.Embedded.EmbeddedHost::Start"));
                Assert.Contains("McvsOffline.Embedded.Windows.WindowsBoot::InstallDiagnostics",
                                Method(module, "McvsOffline.Embedded.Windows.WindowsBoot", ".cctor").Body.Instructions
                                    .Where(i => i.OpCode == OpCodes.Ldftn).Select(i => ((MethodReference)i.Operand).DeclaringType.FullName + "::" + ((MethodReference)i.Operand).Name));
            }
        }

        [Fact]
        public void TheOtherWindowsDelegationsAreOneCall()
        {
            using (ModuleDefinition module = PcDll())
            {
                if (module == null) return;
                MethodDefinition tid = Method(module, "McvsOffline.Embedded.Diagnostics.NativeThreads", "CurrentTid");
                Assert.Contains(Calls(tid), c => c.DeclaringType.FullName == "McvsOffline.Embedded.Windows.Win32" && c.Name == "GetCurrentThreadId");
                Assert.DoesNotContain(Calls(tid), c => c.Name == "gettid");

                MethodDefinition rename = Method(module, "McvsOffline.Server.Core.Storage.FileSystem", "Rename");
                List<MethodReference> renameCalls = Calls(rename).ToList();
                output.WriteLine("FileSystem.Rename calls: " + string.Join(", ", renameCalls.Select(c => c.DeclaringType.Name + "." + c.Name)));
                Assert.Contains(renameCalls, c => c.DeclaringType.Name == "WindowsFiles" && c.Name == "Rename");
                TypeDefinition windowsFiles = module.Types.FirstOrDefault(t => t.Name == "WindowsFiles");
                Assert.NotNull(windowsFiles);
                MethodDefinition moveFile = windowsFiles.Methods.FirstOrDefault(m => m.Name == "MoveFileExW" && m.IsPInvokeImpl);
                if (moveFile != null)
                {
                    // WP1's final body: MoveFileExW through FileSystem.Retry.
                    Assert.Contains(windowsFiles.Methods.Where(m => m.HasBody).SelectMany(Calls), c => c.Name == "Retry" && c.DeclaringType.Name == "FileSystem");
                }
                else
                {
                    output.WriteLine("WindowsFiles has no MoveFileExW yet (WP1's day-0 stand-in)");
                }
            }
        }

        /// <summary>
        /// WR2 F3: WindowsBoot's first step sets the pid and installs our top-level filter after the crash reporter; the filter is
        /// created by WindowsPlatform with the kernel32 implementation; the game-log bridge gets the Fatal-line watcher on PC.
        /// </summary>
        [Fact]
        public void TheCrashCaptureIsWiredIntoTheBoot()
        {
            using (ModuleDefinition module = PcDll())
            {
                if (module == null) return;
                List<string> boot = Calls(Method(module, "McvsOffline.Embedded.Windows.WindowsBoot", "InstallDiagnostics"))
                                        .Select(c => c.DeclaringType.Name + "::" + c.Name).ToList();
                output.WriteLine("InstallDiagnostics: " + string.Join(", ", boot));
                int pid = boot.IndexOf("HostProcess::set_Id");
                int reporter = boot.FindIndex(c => c.StartsWith("CrashReporter::Install", StringComparison.Ordinal));
                int filter = boot.IndexOf("WindowsPlatform::InstallNativeCrashFilter");
                Assert.True(pid >= 0 && reporter > pid && filter > reporter, string.Join(", ", boot));

                List<string> install = Calls(Method(module, "McvsOffline.Embedded.Windows.WindowsPlatform", "InstallNativeCrashFilter"))
                                           .Select(c => c.DeclaringType.Name + "::" + c.Name).ToList();
                Assert.Contains("Win32CrashFilterNative::.ctor", install);
                Assert.Contains("NativeCrashFilter::Install", install);
                Assert.Contains("CrashReporter::AddHook", install);
                Assert.Contains("WindowsPlatform::InstallNativeCrashFilter",
                                Calls(Method(module, "McvsOffline.Embedded.Windows.WindowsPlatform", "InstallCrashHooks")).Select(c => c.DeclaringType.Name + "::" + c.Name));

                List<string> gameLog = Calls(Method(module, "McvsOffline.Embedded.Windows.WindowsPlatform", "ApplyGameLog"))
                                           .Select(c => c.DeclaringType.Name + "::" + c.Name + "/" + c.Parameters.Count).ToList();
                Assert.Contains("GameFatalLines::.ctor/2", gameLog);
                Assert.Contains("GameLogBridge::Apply/6", gameLog);
                // The bridge reads LogEntry.Member and hands Fatal entries to the watcher.
                List<string> entry = Method(module, "McvsOffline.Embedded.Diagnostics.GameLogBridge", "EntryAdded").Body.Instructions
                                         .Select(i => i.Operand as MemberReference).Where(r => r != null).Select(r => r.Name).ToList();
                Assert.Contains("Member", entry);
                Assert.Contains("Fatal", entry);
            }
        }

        [Fact]
        public void NothingCallsIntoInstallOrHdAndThereIsNoThreadDump()
        {
            using (ModuleDefinition module = PcDll())
            {
                if (module == null) return;
                Assert.DoesNotContain(module.Types, t => t.Namespace.StartsWith("McvsOffline.Embedded.Install", StringComparison.Ordinal) ||
                                                         t.Namespace.StartsWith("McvsOffline.Embedded.Hd", StringComparison.Ordinal));
                var targets = module.Types.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                                    .Select(i => i.Operand as MemberReference).Where(r => r != null)
                                    .Select(r => r.DeclaringType == null ? "" : r.DeclaringType.FullName).ToList();
                Assert.DoesNotContain(targets, t => t.StartsWith("McvsOffline.Embedded.Install", StringComparison.Ordinal) ||
                                                    t.StartsWith("McvsOffline.Embedded.Hd", StringComparison.Ordinal));
                TypeDefinition dump = module.GetType("McvsOffline.Embedded.Diagnostics.ThreadDump");
                Assert.NotNull(dump);   // the stub
                Assert.DoesNotContain(dump.Methods, m => m.IsPInvokeImpl);
                Assert.Equal(3, dump.Methods.Count(m => m.IsPublic && m.IsStatic));
            }
        }
    }
}
