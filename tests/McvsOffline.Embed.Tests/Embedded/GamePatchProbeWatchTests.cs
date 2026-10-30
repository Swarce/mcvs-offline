using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Patcher.Patches;
using McvsOffline.Patcher.Pc;
using McvsOffline.Server.Core;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The lazy patch probe of the PC host (docs/design/pc-port.md §6.2, §3.9): Game loads after the bootstrap, so StorePatch is
    /// filled when it loads and the "[boot] patches:" line is written once; the witness literals equal the §3.9 names.
    /// </summary>
    public class GamePatchProbeWatchTests
    {
        private const string EngineBridgeSource = @"namespace EngineBridge { public class Class1 { private static void McvsOfflineInit() { } public void Start() { } } }";
        private const string EngineSource = @"namespace Gameloft.Engine {
            internal static class McvsQuickLaunch { } internal static class McvsWorldDiagnostics { } internal static class McvsGraphics { }
            internal static class McvsGameLoad { } }";
        private const string EngineWithoutGameLoadSource = @"namespace Gameloft.Engine {
            internal static class McvsQuickLaunch { } internal static class McvsWorldDiagnostics { } internal static class McvsGraphics { } }";
        private const string Root = "C:\\Steam\\steamapps\\common\\Modern Combat Versus";
        private const string StockEngineSource = @"namespace Gameloft.Engine { public class World { } }";
        private const string GameSource = @"namespace Game { internal static class McvsStoreSafety { } internal static class McvsInbox { } }";
        private const string GameWithoutInboxSource = @"namespace Game { internal static class McvsStoreSafety { } }";

        /// <summary>Nothing loaded yet (the test process's own list could hold another test's sandboxed "Game").</summary>
        private static IEnumerable<Assembly> Nothing() { return new Assembly[0]; }

        /// <summary>A subscription the test fires by hand.</summary>
        private sealed class Loads
        {
            public Action<Assembly> Callback;
            public int Subscribed;
            public int Unsubscribed;

            public Action Watch(Action<Assembly> loaded)
            {
                Subscribed++;
                Callback = loaded;
                return () => Unsubscribed++;
            }
        }

        private static (Logger, MemoryLogSink, BootTrace) Logs()
        {
            var logger = new Logger();
            var sink = new MemoryLogSink(LogLevel.Debug, 200);
            logger.AddSink(sink);
            return (logger, sink, new BootTrace(logger.For("boot")));
        }

        [Fact]
        public void TheWitnessLiteralsAreTheDesignsNames()
        {
            Assert.Equal("EngineBridge.Class1", GamePatchProbe.HookPcType);
            Assert.Equal(BootstrapHookPatch.HelperName, GamePatchProbe.HookPcMethod);
            Assert.Equal("McvsOfflineInit", GamePatchProbe.HookPcMethod);
            Assert.Equal("Gameloft.Engine.McvsQuickLaunch", GamePatchProbe.QuickLaunchType);
            Assert.Equal("Gameloft.Engine.McvsWorldDiagnostics", GamePatchProbe.WorldDiagnosticsType);
            Assert.Equal("Gameloft.Engine." + GraphicsPatch.HelperType, GamePatchProbe.GraphicsType);
            Assert.Equal(GameAssemblyLoadPatch.HelperNamespace + "." + GameAssemblyLoadPatch.HelperType, GamePatchProbe.GameLoadType);
            Assert.Equal("Game." + StorePatch.SafetyType, GamePatchProbe.StoreSafetyType);
            Assert.Equal(QuickLaunchPatch.HelperNamespace + "." + QuickLaunchPatch.HelperType, GamePatchProbe.QuickLaunchType);
            Assert.Equal(HeadlessDiagnosticsPatch.HelperNamespace + "." + HeadlessDiagnosticsPatch.HelperType, GamePatchProbe.WorldDiagnosticsType);
        }

        [Fact]
        public void GameLoadedLaterFillsStorePatchAndWritesThePatchesLineOnce()
        {
            var (logger, sink, boot) = Logs();
            using (var sandbox = new FixtureCompiler.Sandbox()
                .With("EngineBridge", FixtureCompiler.Compile("EngineBridge", EngineBridgeSource))
                .With("Gameloft.Engine", FixtureCompiler.Compile("Gameloft.Engine", EngineSource))
                .With("Game", FixtureCompiler.Compile("Game", GameSource)))
            {
                var loads = new Loads();
                var target = new ClientCapabilities();
                PatchWatcher watcher = GamePatchProbe.Watch(target, logger.For("game"), loads.Watch, boot, "/games/mcvs", Nothing);
                Assert.Equal(1, loads.Subscribed);
                Assert.True(watcher.Watching);
                Assert.Null(target.StorePatch);   // Game is not loaded yet
                Assert.Same(target, watcher.Target);

                loads.Callback(sandbox.Get("EngineBridge"));
                loads.Callback(sandbox.Get("Gameloft.Engine"));
                Assert.Null(watcher.PatchesLine);
                loads.Callback(typeof(object).Assembly);   // not watched
                loads.Callback(sandbox.Get("Game"));
                Assert.True(target.StorePatch);
                Assert.Equal("patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes, store yes, inbox yes", watcher.PatchesLine);
                loads.Callback(sandbox.Get("Game"));       // seen once
                List<string> lines = sink.Snapshot().Select(e => e.Message).ToList();
                Assert.Single(lines, l => l.Contains("patches: hook-pc"));
                Assert.Contains(lines, l => l.Contains("Game is not loaded yet: the client's patches are probed when it loads"));
                // Sandbox assemblies have no file: they are reported, with a WARN (not from McvsOffline\bin).
                Assert.Contains(sink.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.Contains("assemblies: Game (no file) (elsewhere): the .exe.config redirect did not apply"));

                loads.Callback(null);
                Assert.Equal(0, loads.Unsubscribed);       // McvsOffline.Server and Gameloft.Util not seen yet
            }
        }

        [Fact]
        public void AnAssemblyLoadedWhileSubscribingIsSeen()
        {
            // Game loads on the game thread while the server thread subscribes: the list of loaded assemblies is taken after
            // the subscription is in place, so a load in between is in the list even though the callback never saw it.
            var (logger, _, boot) = Logs();
            using (var sandbox = new FixtureCompiler.Sandbox().With("Game", FixtureCompiler.Compile("Game", GameSource)))
            {
                var loads = new Loads();
                var loadedNow = new List<Assembly>();
                Func<Action<Assembly>, Action> subscribe = callback =>
                {
                    Action stop = loads.Watch(callback);
                    loadedNow.Add(sandbox.Get("Game"));   // loaded just before the handler was attached: no callback
                    return stop;
                };
                var target = new ClientCapabilities();
                int asked = 0;
                PatchWatcher watcher = GamePatchProbe.Watch(target, logger.For("game"), subscribe, boot, null, () =>
                {
                    asked++;
                    Assert.Equal(1, loads.Subscribed);     // asked only once subscribed
                    return loadedNow.ToArray();
                });
                Assert.Equal(1, asked);
                Assert.True(target.StorePatch);
                loads.Callback(sandbox.Get("Game"));       // seen both ways: counted once
                Assert.True(watcher.Watching);
            }
        }

        [Fact]
        public void AStockEngineSaysNo()
        {
            var (logger, sink, boot) = Logs();
            using (var sandbox = new FixtureCompiler.Sandbox()
                .With("Gameloft.Engine", FixtureCompiler.Compile("Gameloft.Engine", StockEngineSource))
                .With("Game", FixtureCompiler.Compile("Game", "namespace Game { public class X { } }")))
            {
                var loads = new Loads();
                var target = new ClientCapabilities();
                PatchWatcher watcher = GamePatchProbe.Watch(target, logger.For("game"), loads.Watch, boot, null, Nothing);
                loads.Callback(sandbox.Get("Game"));
                loads.Callback(sandbox.Get("Gameloft.Engine"));
                Assert.False(target.StorePatch);
                Assert.Equal("patches: hook-pc unknown (not loaded), quicklaunch NO, headless-diag NO, graphics NO, gameload NO, store NO, inbox NO", watcher.PatchesLine);
                Assert.Contains(sink.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.Contains("patches: hook-pc unknown"));
                Assert.StartsWith("Game loaded from outside McvsOffline\\bin (elsewhere); Gameloft.Engine loaded from outside McvsOffline\\bin (elsewhere); " +
                                  "the 'quicklaunch' patch is missing; the 'headless-diag' patch is missing", target.NotActive);
                Assert.EndsWith("the 'gameload' patch is missing; the 'store' patch is missing; the 'inbox' patch is missing", target.NotActive);
            }
        }

        /// <summary>The five redirected assemblies of a start: from McvsOffline\bin, except the names in <paramref name="fromGameFolder"/>.</summary>
        private static Func<Assembly, string> Locations(params string[] fromGameFolder)
        {
            return a =>
            {
                string name = a.GetName().Name;
                return Array.IndexOf(fromGameFolder, name) >= 0 ? Root + "\\" + name + ".dll" : Root + "\\McvsOffline\\bin\\" + name + ".dll";
            };
        }

        private static FixtureCompiler.Sandbox Start(string engineSource, string gameSource)
        {
            return new FixtureCompiler.Sandbox()
                .With("EngineBridge", FixtureCompiler.Compile("EngineBridge", EngineBridgeSource))
                .With("Gameloft.Engine", FixtureCompiler.Compile("Gameloft.Engine", engineSource))
                .With("Game", FixtureCompiler.Compile("Game", gameSource));
        }

        /// <summary>WR2 F2: every assembly from McvsOffline\bin and every witness yes: active, no ERROR line.</summary>
        [Fact]
        public void AStartThatRunsOurFilesIsActive()
        {
            var (logger, sink, boot) = Logs();
            using (FixtureCompiler.Sandbox sandbox = Start(EngineSource, GameSource))
            {
                var loads = new Loads();
                var target = new ClientCapabilities();
                PatchWatcher watcher = GamePatchProbe.Watch(target, logger.For("game"), loads.Watch, boot, Root, Nothing);
                watcher.LocationOf = Locations();
                foreach (string name in new[] { "EngineBridge", "Gameloft.Engine", "Game" }) loads.Callback(sandbox.Get(name));
                Assert.Equal("patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes, store yes, inbox yes", watcher.PatchesLine);
                Assert.Null(watcher.NotActive);
                Assert.Null(target.NotActive);
                Assert.DoesNotContain(sink.Snapshot(), e => e.Level >= LogLevel.Warn);
                Assert.Contains(sink.Snapshot(), e => e.Message.Contains("assemblies: Game " + Root + "\\McvsOffline\\bin\\Game.dll (McvsOffline\\bin)"));
            }
        }

        /// <summary>
        /// WR2 F2, the WR2 start: Game from the game folder. The server's capabilities say NOT ACTIVE, and the boot trace has one
        /// ERROR "install: installed, NOT ACTIVE (Game loaded from the game folder)" once the patches line is written.
        /// </summary>
        [Fact]
        public void GameFromTheGameFolderIsNotActiveAndAnError()
        {
            var (logger, sink, boot) = Logs();
            using (FixtureCompiler.Sandbox sandbox = Start(EngineSource, "namespace Game { public class X { } }"))
            {
                var loads = new Loads();
                var target = new ClientCapabilities();
                PatchWatcher watcher = GamePatchProbe.Watch(target, logger.For("game"), loads.Watch, boot, Root, Nothing);
                watcher.LocationOf = Locations("Game");
                loads.Callback(sandbox.Get("EngineBridge"));
                loads.Callback(sandbox.Get("Gameloft.Engine"));
                loads.Callback(sandbox.Get("Game"));
                Assert.Equal("Game loaded from the game folder; the 'store' patch is missing; the 'inbox' patch is missing", watcher.NotActive);
                Assert.Equal(watcher.NotActive, target.NotActive);
                Assert.Equal("installed, NOT ACTIVE (Game loaded from the game folder; the 'store' patch is missing; the 'inbox' patch is missing)", PatchWatcher.NotActiveText(target.NotActive));
                LogEntry error = Assert.Single(sink.Snapshot(), e => e.Level == LogLevel.Error);
                Assert.Contains("install: installed, NOT ACTIVE (Game loaded from the game folder; the 'store' patch is missing; the 'inbox' patch is missing): the game runs its own files", error.Message);
                Assert.Contains(sink.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.Contains("assemblies: Game " + Root + "\\Game.dll (GAME FOLDER): the .exe.config redirect did not apply"));
                Assert.Contains(boot.Milestones(), m => m.Contains(" ERROR install: installed, NOT ACTIVE (Game loaded from the game folder"));

                // An assembly that loads after the patches line from the game folder: reported at once, with every reason so far.
                using (var late = new FixtureCompiler.Sandbox().With("Gameloft.Util", FixtureCompiler.Compile("Gameloft.Util", "namespace Gameloft.Util { public class U { } }")))
                {
                    watcher.LocationOf = Locations("Game", "Gameloft.Util");
                    loads.Callback(late.Get("Gameloft.Util"));
                }
                Assert.Equal("Game loaded from the game folder; the 'store' patch is missing; the 'inbox' patch is missing; Gameloft.Util loaded from the game folder", target.NotActive);
                Assert.Equal(2, sink.Snapshot().Count(e => e.Level == LogLevel.Error));
            }
        }

        /// <summary>WR2 F2: the files from McvsOffline\bin but a witness missing (an engine patched without gameload) is not active either.</summary>
        [Fact]
        public void AMissingWitnessIsNotActive()
        {
            var (logger, sink, boot) = Logs();
            using (FixtureCompiler.Sandbox sandbox = Start(EngineWithoutGameLoadSource, GameSource))
            {
                var loads = new Loads();
                var target = new ClientCapabilities();
                PatchWatcher watcher = GamePatchProbe.Watch(target, logger.For("game"), loads.Watch, boot, Root, Nothing);
                watcher.LocationOf = Locations();
                foreach (string name in new[] { "EngineBridge", "Gameloft.Engine", "Game" }) loads.Callback(sandbox.Get(name));
                Assert.Equal("patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload NO, store yes, inbox yes", watcher.PatchesLine);
                Assert.Equal("the 'gameload' patch is missing", target.NotActive);
                Assert.Single(sink.Snapshot(), e => e.Level == LogLevel.Error && e.Message.Contains("install: installed, NOT ACTIVE (the 'gameload' patch is missing)"));
            }
        }

        /// <summary>
        /// Build 17: a Game.dll from bin with the 'store' patch but without 'inbox' (an install the kit made before build 17, or a
        /// Game.dll copied by hand) is NOT ACTIVE, so the page shows the error; InboxPatch is false, StorePatch true.
        /// </summary>
        [Fact]
        public void AGameWithoutTheInboxPatchIsNotActive()
        {
            var (logger, sink, boot) = Logs();
            using (FixtureCompiler.Sandbox sandbox = Start(EngineSource, GameWithoutInboxSource))
            {
                var loads = new Loads();
                var target = new ClientCapabilities();
                PatchWatcher watcher = GamePatchProbe.Watch(target, logger.For("game"), loads.Watch, boot, Root, Nothing);
                watcher.LocationOf = Locations();
                foreach (string name in new[] { "EngineBridge", "Gameloft.Engine", "Game" }) loads.Callback(sandbox.Get(name));
                Assert.Equal("patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes, store yes, inbox NO", watcher.PatchesLine);
                Assert.Equal("the 'inbox' patch is missing", target.NotActive);
                Assert.True(target.StorePatch);
                Assert.False(target.InboxPatch);
                Assert.Single(sink.Snapshot(), e => e.Level == LogLevel.Error && e.Message.Contains("install: installed, NOT ACTIVE (the 'inbox' patch is missing)"));
            }
        }

        /// <summary>
        /// An install without the default 'store' (--patch none): Game from bin without McvsStoreSafety is active. The witness of a
        /// patch install.json does not list is "off (not applied)", not NO; StorePatch stays false for the collector, as before WR2 F2.
        /// </summary>
        [Fact]
        public void AWitnessOfAPatchNotAppliedIsOffNotNo()
        {
            var mandatoryOnly = new HashSet<string> { "hook-pc", "headless", "headless-engine", "physics", "quicklaunch", "headless-diag", "graphics", "gameload" };
            var (logger, sink, boot) = Logs();
            using (FixtureCompiler.Sandbox sandbox = Start(EngineSource, "namespace Game { public class X { } }"))
            {
                var loads = new Loads();
                var target = new ClientCapabilities();
                PatchWatcher watcher = GamePatchProbe.Watch(target, logger.For("game"), loads.Watch, boot, Root, Nothing);
                watcher.LocationOf = Locations();
                string askedRoot = null;
                watcher.AppliedOf = root => { askedRoot = root; return mandatoryOnly; };
                foreach (string name in new[] { "EngineBridge", "Gameloft.Engine", "Game" }) loads.Callback(sandbox.Get(name));
                Assert.Equal(Root, askedRoot);
                Assert.Equal("patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes, store off (not applied), inbox off (not applied)", watcher.PatchesLine);
                Assert.Null(watcher.NotActive);
                Assert.Null(target.NotActive);
                Assert.False(target.StorePatch);
                Assert.DoesNotContain(sink.Snapshot(), e => e.Level >= LogLevel.Error);
                Assert.Contains(sink.Snapshot(), e => e.Level == LogLevel.Info && e.Message.Contains("store off (not applied)"));
            }

            // 'store' applied but its witness missing: NO and NOT ACTIVE (a mandatory one too, whatever the list says).
            var withStore = new HashSet<string>(mandatoryOnly) { "store" };
            (logger, sink, boot) = Logs();
            using (FixtureCompiler.Sandbox sandbox = Start(EngineWithoutGameLoadSource, "namespace Game { public class X { } }"))
            {
                var loads = new Loads();
                var target = new ClientCapabilities();
                PatchWatcher watcher = GamePatchProbe.Watch(target, logger.For("game"), loads.Watch, boot, Root, Nothing);
                watcher.LocationOf = Locations();
                watcher.AppliedOf = root => withStore;
                foreach (string name in new[] { "EngineBridge", "Gameloft.Engine", "Game" }) loads.Callback(sandbox.Get(name));
                Assert.Equal("patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload NO, store NO, inbox off (not applied)", watcher.PatchesLine);
                Assert.Equal("the 'gameload' patch is missing; the 'store' patch is missing", target.NotActive);
            }
        }

        /// <summary>The host reads the applied ids from the install.json the kit writes (options.patches); anything else is unknown (null).</summary>
        [Fact]
        public void AppliedPatchesComeFromInstallJson()
        {
            string root = TestEnvironment.NewTempDir("applied");
            try
            {
                Assert.Null(GamePatchProbe.AppliedPatches(root));
                Assert.Null(GamePatchProbe.AppliedPatches(null));
                Directory.CreateDirectory(Path.Combine(root, "McvsOffline"));
                string file = Path.Combine(root, "McvsOffline", "install.json");
                var manifest = new McvsOffline.Patcher.Pc.InstallManifest { Port = 51780, WrittenUtc = "2026-11-02T18:00:00Z" };
                manifest.Patches.AddRange(new[] { "hook-pc", "headless", "gameload" });
                File.WriteAllText(file, manifest.ToJson());
                ICollection<string> applied = GamePatchProbe.AppliedPatches(root);
                Assert.Equal(new[] { "gameload", "headless", "hook-pc" }, applied.OrderBy(x => x, StringComparer.Ordinal));
                Assert.DoesNotContain("store", applied);

                File.WriteAllText(file, "{ \"format\": 2, \"options\": { \"patches\": [\"store\"] } }");
                Assert.Null(GamePatchProbe.AppliedPatches(root));
                File.WriteAllText(file, "{ \"format\": 1, \"options\": { \"port\": 1 } }");
                Assert.Null(GamePatchProbe.AppliedPatches(root));
                File.WriteAllText(file, "not json");
                Assert.Null(GamePatchProbe.AppliedPatches(root));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void WithoutABootTraceItProbesLikeDetectAndStopsWatchingOnceGameIsSeen()
        {
            var (logger, sink, _) = Logs();
            using (var sandbox = new FixtureCompiler.Sandbox().With("Game", FixtureCompiler.Compile("Game", GameSource)))
            {
                var loads = new Loads();
                var target = new ClientCapabilities();
                PatchWatcher watcher = GamePatchProbe.Watch(target, logger.For("game"), loads.Watch, null, null, Nothing);
                loads.Callback(sandbox.Get("Game"));
                Assert.True(target.StorePatch);
                Assert.Equal(1, loads.Unsubscribed);      // Android-like: only the store patch was wanted
                Assert.False(watcher.Watching);
                Assert.Null(watcher.PatchesLine);
                Assert.DoesNotContain(sink.Snapshot(), e => e.Message.Contains("assemblies:"));
                Assert.Null(target.NotActive);   // Android: never judged
                Assert.Null(watcher.NotActive);
            }
        }

        [Fact]
        public void WithoutASubscriptionItIsTodaysProbe()
        {
            var (logger, sink, _) = Logs();
            var target = new ClientCapabilities();
            PatchWatcher watcher = GamePatchProbe.Watch(target, logger.For("game"), null, null, null, () => new[] { typeof(object).Assembly });
            Assert.False(watcher.Watching);
            Assert.Null(target.StorePatch);   // no Game assembly in the test process
            Assert.Contains(sink.Snapshot(), e => e.Message == "no Game assembly is loaded: the client's patches are unknown");
        }

        [Theory]
        [InlineData("C:\\Steam\\steamapps\\common\\Modern Combat Versus\\McvsOffline\\bin\\Game.dll", "McvsOffline\\bin")]
        [InlineData("C:\\Steam\\steamapps\\common\\Modern Combat Versus\\McvsOffline\\BIN\\Game.dll", "McvsOffline\\bin")]
        [InlineData("C:\\Steam\\steamapps\\common\\Modern Combat Versus\\Game.dll", "GAME FOLDER")]
        [InlineData("D:\\elsewhere\\Game.dll", "elsewhere")]
        [InlineData("", "elsewhere")]
        public void WhereAnAssemblyCameFrom(string location, string expected)
        {
            Assert.Equal(expected, PatchWatcher.Where(location, "C:\\Steam\\steamapps\\common\\Modern Combat Versus\\"));
        }

        /// <summary>
        /// Build 17's Game load as .NET Framework 4.8 reports it inside the AssemblyLoad event (Environment.StackTrace: our handler,
        /// the event, the loader internals, then GameReflection.Load's partial-name Type.GetType).
        /// </summary>
        private const string Build17Stack =
            "   at System.Environment.GetStackTrace(Exception e, Boolean needFileInfo)\r\n" +
            "   at System.Environment.get_StackTrace()\r\n" +
            "   at McvsOffline.Embedded.PatchWatcher.ReportLoader(String name, Boolean byEvent)\r\n" +
            "   at McvsOffline.Embedded.PatchWatcher.ReportLocation(String name, Assembly assembly, Boolean byEvent)\r\n" +
            "   at McvsOffline.Embedded.PatchWatcher.Loaded(Assembly assembly, Boolean byEvent)\r\n" +
            "   at McvsOffline.Embedded.Windows.WindowsPlatform.<>c__DisplayClass62_0.<WatchAssemblyLoads>b__0(Object sender, AssemblyLoadEventArgs args)\r\n" +
            "   at System.AppDomain.OnAssemblyLoadEvent(RuntimeAssembly LoadedAssembly)\r\n" +
            "   at System.Reflection.RuntimeAssembly._nLoad(AssemblyName fileName, String codeBase, Evidence assemblySecurity, RuntimeAssembly locationHint, StackCrawlMark& stackMark, IntPtr pPrivHostBinder, Boolean throwOnFileNotFound, Boolean forIntrospection, Boolean suppressSecurityChecks)\r\n" +
            "   at System.Reflection.RuntimeAssembly.InternalLoadAssemblyName(AssemblyName assemblyRef, Evidence assemblySecurity, RuntimeAssembly reqAssembly, StackCrawlMark& stackMark, IntPtr pPrivHostBinder, Boolean throwOnFileNotFound, Boolean forIntrospection, Boolean suppressSecurityChecks)\r\n" +
            "   at System.RuntimeTypeHandle._GetTypeByName(String name, Boolean throwOnError, Boolean ignoreCase, Boolean reflectionOnly, StackCrawlMarkHandle stackMark, IntPtr pPrivHostBinder, Boolean loadTypeFromPartialName, ObjectHandleOnStack type)\r\n" +
            "   at System.RuntimeTypeHandle.GetTypeByName(String name, Boolean throwOnError, Boolean ignoreCase, Boolean reflectionOnly, StackCrawlMark& stackMark, IntPtr pPrivHostBinder, Boolean loadTypeFromPartialName)\r\n" +
            "   at System.Type.GetType(String typeName, Boolean throwOnError)\r\n" +
            "   at McvsOffline.Embedded.Admin.GameReflection.Load()\r\n" +
            "   at McvsOffline.Embedded.EmbeddedHost.CreateAdminHost(McvsServer created)\r\n" +
            "   at McvsOffline.Embedded.EmbeddedHost.RunServer(EmbeddedHostOptions options, Int32 port)\r\n" +
            "   at System.Threading.ThreadHelper.ThreadStart()";

        /// <summary>Build 18: the stack starts at the loader's caller (Type.GetType, then whoever called it); handler and loader frames are cut.</summary>
        [Fact]
        public void TheLoadStackStartsAtWhoeverAskedForTheLoad()
        {
            Assert.Equal("   at System.Type.GetType(String typeName, Boolean throwOnError)\n" +
                         "   at McvsOffline.Embedded.Admin.GameReflection.Load()\n" +
                         "   at McvsOffline.Embedded.EmbeddedHost.CreateAdminHost(McvsServer created)\n" +
                         "   at McvsOffline.Embedded.EmbeddedHost.RunServer(EmbeddedHostOptions options, Int32 port)\n" +
                         "   at System.Threading.ThreadHelper.ThreadStart()",
                         PatchWatcher.LoadStack(Build17Stack, PatchWatcher.MaxStackFrames));

            // The engine's own load (gameload's Assembly.Load(AssemblyName)): Assembly.Load is cut, its caller comes first.
            string engine = "   at System.AppDomain.OnAssemblyLoadEvent(RuntimeAssembly LoadedAssembly)\n" +
                            "   at System.Reflection.RuntimeAssembly._nLoad(AssemblyName fileName)\n" +
                            "   at System.Reflection.Assembly.Load(AssemblyName assemblyRef)\n" +
                            "   at Gameloft.Engine.ComponentAssemblies.GetAssembly(String fullPath, String name)\n";
            Assert.Equal("   at Gameloft.Engine.ComponentAssemblies.GetAssembly(String fullPath, String name)", PatchWatcher.LoadStack(engine, 25));

            // A localized "at" (German Windows), and no event frame (another runtime): our leading frames are still cut.
            string german = "   bei System.Environment.get_StackTrace()\n   bei McvsOffline.Embedded.PatchWatcher.LoadedByEvent(Assembly assembly)\n" +
                            "   bei System.Reflection.RuntimeAssembly.nLoad(AssemblyName fileName)\n   bei Game.Caller.Run()";
            Assert.Equal("   bei Game.Caller.Run()", PatchWatcher.LoadStack(german, 25));

            Assert.Null(PatchWatcher.LoadStack(null, 25));
            Assert.Null(PatchWatcher.LoadStack("   at System.AppDomain.OnAssemblyLoadEvent(RuntimeAssembly a)\n   at System.Reflection.RuntimeAssembly._nLoad()", 25));
        }

        [Fact]
        public void TheLoadStackKeepsAtMost25Frames()
        {
            var sb = new System.Text.StringBuilder("   at System.AppDomain.OnAssemblyLoadEvent(RuntimeAssembly a)\n");
            for (int i = 0; i < 40; i++) sb.Append("   at Caller.F").Append(i).Append("()\n");
            string[] lines = PatchWatcher.LoadStack(sb.ToString(), PatchWatcher.MaxStackFrames).Split('\n');
            Assert.Equal(26, lines.Length);
            Assert.Equal("   at Caller.F0()", lines[0]);
            Assert.Equal("   at Caller.F24()", lines[24]);
            Assert.Equal("   ... 15 more frames", lines[25]);
            Assert.Equal(25, PatchWatcher.MaxStackFrames);
        }

        /// <summary>
        /// Build 18: a redirected assembly from the game folder gets a "load stack:" WARN with the loader's frames right after its
        /// "assemblies:" line (not an "assemblies:" line itself: the kit's PcBootActivation must not count it); one from
        /// McvsOffline\bin does not, and one already loaded when the watch started says its loader is unknown.
        /// </summary>
        [Fact]
        public void AGameFolderLoadLogsWhoLoadedIt()
        {
            var (logger, sink, boot) = Logs();
            using (FixtureCompiler.Sandbox sandbox = Start(EngineSource, GameSource))
            {
                var loads = new Loads();
                PatchWatcher watcher = GamePatchProbe.Watch(new ClientCapabilities(), logger.For("game"), loads.Watch, boot, Root, Nothing);
                watcher.LocationOf = Locations("EngineBridge", "Game");
                int asked = 0;
                watcher.StackOf = () =>
                {
                    asked++;
                    return Build17Stack;
                };
                loads.Callback(sandbox.Get("Gameloft.Engine"));
                Assert.Equal(0, asked);
                loads.Callback(sandbox.Get("Game"));
                Assert.Equal(1, asked);
                watcher.Loaded(sandbox.Get("EngineBridge"));   // found loaded, not by the event: no stack to show
                Assert.Equal(1, asked);

                List<LogEntry> entries = sink.Snapshot().ToList();
                int game = entries.FindIndex(e => e.Message.Contains("assemblies: Game " + Root + "\\Game.dll (GAME FOLDER)"));
                Assert.True(game >= 0);
                LogEntry stack = entries[game + 1];
                Assert.Equal(LogLevel.Warn, stack.Level);
                Assert.EndsWith(" s load stack: Game (GAME FOLDER) was loaded by (innermost first):\n" +
                                "   at System.Type.GetType(String typeName, Boolean throwOnError)\n" +
                                "   at McvsOffline.Embedded.Admin.GameReflection.Load()\n" +
                                "   at McvsOffline.Embedded.EmbeddedHost.CreateAdminHost(McvsServer created)\n" +
                                "   at McvsOffline.Embedded.EmbeddedHost.RunServer(EmbeddedHostOptions options, Int32 port)\n" +
                                "   at System.Threading.ThreadHelper.ThreadStart()", stack.Message);
                Assert.Single(entries, e => e.Message.Contains("load stack: Game"));
                Assert.Contains(entries, e => e.Level == LogLevel.Warn &&
                                              e.Message.EndsWith(" s load stack: EngineBridge (GAME FOLDER): already loaded when the host started watching, so its loader is unknown"));
                Assert.DoesNotContain(entries, e => e.Message.Contains("load stack: Gameloft.Engine"));

                // The kit reads the same log: the stack lines are not "assemblies:" lines and add no reason.
                string log = string.Join("\n", entries.Select(e => "2026-10-30T11:29:13.033Z WARN  [boot] " + e.Message));
                PcBootActivation read = PcBootActivation.Read(log);
                Assert.Equal(3, read.Assemblies.Count);
                Assert.Equal(new[] { "Game loaded from the game folder", "EngineBridge loaded from the game folder" }, read.Reasons.ToArray());
            }
        }

        /// <summary>Without a seam the stack is the real one of the loading thread (here: the test's own frames).</summary>
        [Fact]
        public void TheRealStackNamesTheCaller()
        {
            var (logger, sink, boot) = Logs();
            using (FixtureCompiler.Sandbox sandbox = Start(EngineSource, GameSource))
            {
                var loads = new Loads();
                PatchWatcher watcher = GamePatchProbe.Watch(new ClientCapabilities(), logger.For("game"), loads.Watch, boot, Root, Nothing);
                watcher.LocationOf = Locations("Game");
                loads.Callback(sandbox.Get("Game"));
                LogEntry stack = Assert.Single(sink.Snapshot(), e => e.Message.Contains("load stack: Game (GAME FOLDER) was loaded by"));
                Assert.Contains(nameof(TheRealStackNamesTheCaller), stack.Message);
                Assert.DoesNotContain("PatchWatcher", stack.Message);
                Assert.DoesNotContain("System.Environment", stack.Message);
            }
        }
    }
}
