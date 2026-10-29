using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Patcher.Patches;
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
            internal static class McvsQuickLaunch { } internal static class McvsWorldDiagnostics { } internal static class McvsGraphics { } }";
        private const string StockEngineSource = @"namespace Gameloft.Engine { public class World { } }";
        private const string GameSource = @"namespace Game { internal static class McvsStoreSafety { } }";

        /// <summary>Nothing loaded yet (the test process's own list could hold another test's sandboxed "Game").</summary>
        private static readonly Assembly[] Nothing = new Assembly[0];

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
                Assert.Equal("patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, store yes", watcher.PatchesLine);
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
                Assert.Equal("patches: hook-pc unknown (not loaded), quicklaunch NO, headless-diag NO, graphics NO, store NO", watcher.PatchesLine);
                Assert.Contains(sink.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.Contains("patches: hook-pc unknown"));
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
            }
        }

        [Fact]
        public void WithoutASubscriptionItIsTodaysProbe()
        {
            var (logger, sink, _) = Logs();
            var target = new ClientCapabilities();
            PatchWatcher watcher = GamePatchProbe.Watch(target, logger.For("game"), null, null, null, new[] { typeof(object).Assembly });
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
    }
}
