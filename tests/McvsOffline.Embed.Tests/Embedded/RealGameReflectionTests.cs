using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using McvsOffline.Embedded.Admin;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// Every game member <see cref="GameReflection"/> uses exists with the expected signature in the player's real
    /// Gameloft.Engine.dll, Game.dll and Gameloft.OnlinePlatform.dll (MCVS_REFS), found by the same lookups the host runs:
    /// GameApplication.Instance/World/Context, World.Browser/Authentication/IsServer/EnumerateActiveComponents(Type,
    /// ComponentGroupOptions), BrowserComponent.World/IsInGameBrowserInitialized/OpenUrl(string, bool),
    /// AuthenticationComponent.AnonymousCredential/FedId and the Credential's string conversion, PlayerProfileComponent.FedId and
    /// ForceSaveProfile(CancellationToken) returning Task. Skipped without the assemblies unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    public class RealGameReflectionTests
    {
        private readonly ITestOutputHelper output;

        public RealGameReflectionTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void EveryMemberResolvesInTheRealAssemblies()
        {
            string dir = Refs();
            if (dir == null) return;
            var context = new GameContext(dir);
            try
            {
                Assembly engine = context.LoadFromAssemblyPath(Path.Combine(dir, "Gameloft.Engine.dll"));
                Assembly game = context.LoadFromAssemblyPath(Path.Combine(dir, "Game.dll"));
                Assembly online = context.LoadFromAssemblyPath(Path.Combine(dir, "Gameloft.OnlinePlatform.dll"));
                Func<string, Type> resolve = name => new[] { engine, game, online }.Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);

                var problems = GameReflection.Verify(resolve);
                foreach (string p in problems) output.WriteLine(p);
                Assert.Empty(problems);

                // The HD texture mount (hosts/embedded/Hd/EngineFiles.cs): GlfStream roots, the three PushZip entry points, the
                // front search path and AssetStream.ReadAllBytes.
                var files = McvsOffline.Embedded.Hd.EngineFiles.Verify(resolve);
                foreach (string p in files) output.WriteLine(p);
                Assert.Empty(files);

                // The host binds to the real static side; without a running game there is no instance and no game thread.
                var reflection = new GameReflection();
                Assert.True(reflection.Bind(resolve("Gameloft.Engine.GameApplication"), resolve("Game.PlayerProfileComponent")));
                Assert.True(reflection.IsBound);
                Assert.True(reflection.CanSave);
                Assert.Null(reflection.Application());
                Assert.Null(reflection.ClientWorld());
                Assert.False(reflection.WaitForFrame(10));
                Assert.False(reflection.Post(() => { }, null));

                // The credential's own conversion, executed: "anonymous:<name>" as the game sends it.
                Type credential = resolve("Gameloft.OnlinePlatform.Credential");
                object value = Activator.CreateInstance(credential, "anonymous:a1b2c3d4e5f60718");
                Assert.Equal("anonymous:a1b2c3d4e5f60718", GameReflection.CredentialText(value));
                Assert.Null(GameReflection.CredentialText(Activator.CreateInstance(credential)));   // default(Credential): no text
                GC.KeepAlive(context);
            }
            finally
            {
                context.Unload();
            }
        }

        private string Refs()
        {
            string dir = Environment.GetEnvironmentVariable("MCVS_REFS");
            if (string.IsNullOrEmpty(dir)) dir = "/home/user/mcvs-work/extracted/assemblies";
            if (File.Exists(Path.Combine(dir, "Game.dll")) && File.Exists(Path.Combine(dir, "Gameloft.Engine.dll"))) return dir;
            Assert.False(Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1", "MCVS_REQUIRE_REAL_CLIENT=1 but Game.dll/Gameloft.Engine.dll are missing in " + dir);
            output.WriteLine("Game.dll/Gameloft.Engine.dll not found (" + dir + "): skipped");
            return null;
        }

        /// <summary>The game's own assemblies from the folder; the BCL from net8.</summary>
        private sealed class GameContext : AssemblyLoadContext
        {
            private readonly string directory;

            public GameContext(string directory) : base("mcvs-game-reflection-" + Guid.NewGuid().ToString("N"), isCollectible: true)
            {
                this.directory = directory;
            }

            protected override Assembly Load(AssemblyName name)
            {
                string simple = name.Name;
                if (simple == "mscorlib" || simple == "netstandard" || simple == "System" || simple.StartsWith("System.", StringComparison.Ordinal) ||
                    simple.StartsWith("Microsoft.", StringComparison.Ordinal))
                    return null;
                string path = Path.Combine(directory, simple + ".dll");
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }
        }
    }
}
