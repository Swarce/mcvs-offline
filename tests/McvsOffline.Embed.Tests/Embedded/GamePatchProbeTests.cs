using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using McvsOffline.Embed.Tests.Patcher;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Patcher.Patches;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using Xunit;
using Xunit.Abstractions;
using GameContext = McvsOffline.Embed.Tests.Patcher.RealGamePatchTests.GameContext;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The embedded host's client probe (economy-collector §2.13, WP-H): the 'store' patch is seen in the player's REAL
    /// Game.dll (MCVS_REFS) once the patcher has patched it in memory, as <see cref="RealStorePatchTests"/> does, and not
    /// in the shipped one; no Game assembly or a failing probe is "unknown". The real-client tests are skipped without the
    /// game's assemblies unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    public class GamePatchProbeTests
    {
        private readonly ITestOutputHelper output;

        public GamePatchProbeTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private string Refs()
        {
            string dir = TestEnvironment.GameAssemblies;
            if (dir != null && File.Exists(Path.Combine(dir, "Game.dll"))) return dir;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but MCVS_REFS has no Game.dll");
            output.WriteLine("the game's assemblies are not available (MCVS_REFS): skipped");
            return null;
        }

        private static ClientCapabilities Probe(Assembly game, MemoryLogSink sink)
        {
            var logger = new Logger();
            logger.AddSink(sink);
            // Among other assemblies, as in the game process.
            return GamePatchProbe.Detect(new[] { typeof(object).Assembly, game, typeof(GamePatchProbe).Assembly }, logger.For("game"));
        }

        [Fact]
        public void TheTypeIsTheOneTheStorePatchAdds()
        {
            Assert.Equal("Game." + StorePatch.SafetyType, GamePatchProbe.StoreSafetyType);
        }

        [Fact]
        public void TheRealGameDllPatchedByThePatcherHasTheStorePatch()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = FixtureCompiler.Compile("McvsOffline.Server", RealGamePatchTests.HookServerSource);
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, null, new StorePatch());
            var sink = new MemoryLogSink();
            using (var game = new GameContext(refs, patched, server))
            {
                ClientCapabilities found = Probe(game.Get(GamePatchProbe.GameAssembly), sink);
                Assert.True(found.StorePatch);
            }
            Assert.True(sink.Contains("Game.dll: 'store' patch present"));
        }

        [Fact]
        public void TheShippedGameDllHasNot()
        {
            string refs = Refs();
            if (refs == null) return;
            var sink = new MemoryLogSink();
            using (var game = new GameContext(refs, new Dictionary<string, byte[]>(), null))
            {
                Assembly g = game.Get(GamePatchProbe.GameAssembly);
                Assert.Equal(Path.GetFullPath(Path.Combine(refs, "Game.dll")), Path.GetFullPath(g.Location));
                Assert.False(Probe(g, sink).StorePatch);
            }
            Assert.True(sink.Contains("Game.dll: 'store' patch missing"));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ALookAlikeGameAssemblyIsReadByName(bool patched)
        {
            string source = "namespace Game { public static class GameDll { public static void OnInitialize() { } } }" +
                            (patched ? " namespace Game { static class McvsStoreSafety { static int reported; } }" : "");
            var images = new Dictionary<string, byte[]> { { "Game.dll", FixtureCompiler.Compile("Game", source) } };
            using (var game = new GameContext(TestEnvironment.NewTempDir("probe"), images, null))
            {
                Assert.Equal(patched, Probe(game.Get(GamePatchProbe.GameAssembly), new MemoryLogSink()).StorePatch);
            }
        }

        [Fact]
        public void NoGameAssemblyIsUnknown()
        {
            var sink = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(sink);
            ClientCapabilities found = GamePatchProbe.Detect(new[] { typeof(object).Assembly, typeof(GamePatchProbe).Assembly }, logger.For("game"));
            Assert.NotNull(found);
            Assert.Null(found.StorePatch);
            Assert.True(sink.Contains("no Game assembly is loaded"));

            Assert.Null(GamePatchProbe.Detect(new Assembly[0], null).StorePatch);
            // The process-wide probe: whatever other tests have loaded meanwhile, it answers and does not throw.
            Assert.NotNull(GamePatchProbe.Detect());
        }

        [Fact]
        public void AFailingProbeIsUnknownAndLogged()
        {
            var sink = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(sink);
            ClientCapabilities found = GamePatchProbe.Detect(Throwing(), logger.For("game"));
            Assert.NotNull(found);
            Assert.Null(found.StorePatch);
            LogEntry warning = Assert.Single(sink.Snapshot(), e => e.Level == LogLevel.Warn);
            Assert.Contains("could not be probed", warning.Message);
            Assert.IsType<BadImageFormatException>(warning.Exception);
            // Without a logger, too.
            Assert.Null(GamePatchProbe.Detect(Throwing(), null).StorePatch);
        }

        private static IEnumerable<Assembly> Throwing()
        {
            yield return typeof(object).Assembly;
            throw new BadImageFormatException("broken");
        }

        [Theory]
        [InlineData(true, "client: 'store' patch present")]
        [InlineData(false, "client: Game.dll lacks the patcher's 'store' patch")]
        [InlineData(null, "client: 'store' patch unknown (assumed present)")]
        public void TheServerRegistersWhatTheHostReports(bool? storePatch, string logged)
        {
            var sink = new MemoryLogSink();
            var reported = new ClientCapabilities { StorePatch = storePatch };
            var options = new ServerOptions
            {
                DataDir = TestEnvironment.NewTempDir("caps"), Port = 0, ConsoleLog = false, FileLog = false,
                RegisterServices = (r, c) => { }, ClientCapabilities = reported,
            };
            options.ExtraSinks.Add(sink);
            using (McvsServer server = McvsServer.Create(options))
            {
                Assert.Same(reported, server.Context.Features.Get<ClientCapabilities>());
            }
            Assert.True(sink.Contains(logged));
        }

        [Fact]
        public void WithoutAReportTheServerRegistersUnknown()
        {
            var options = new ServerOptions { DataDir = TestEnvironment.NewTempDir("caps"), Port = 0, ConsoleLog = false, FileLog = false, RegisterServices = (r, c) => { } };
            using (McvsServer server = McvsServer.Create(options))
            {
                ClientCapabilities registered = server.Context.Features.Get<ClientCapabilities>();
                Assert.NotNull(registered);
                Assert.Null(registered.StorePatch);
            }
        }
    }
}
