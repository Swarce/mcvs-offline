using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Server.Core;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// Build 18: our host never makes the runtime load a game assembly (docs/design/pc-port.md §8.9). Build 17's
    /// GameReflection.Load ran <c>Type.GetType("Game.PlayerProfileComponent, Game")</c> at boot, a partial-name bind the
    /// .exe.config codeBase does not apply to, so the stock Game.dll next to the exe loaded before the engine's own full-name
    /// load and the engine got that one back. Here GameReflection.cs and LoadedTypes.cs, as shipped, run in a load context that
    /// can serve a look-alike "Game" by partial name and records every request for it.
    /// </summary>
    public class GameLoadOrderTests
    {
        private const string EngineSource = @"namespace Gameloft.Engine {
            public sealed class GameApplication {
                public static GameApplication Instance { get; set; }
                public object World { get; set; }
                public System.Threading.SynchronizationContext Context { get; set; }
            } }";

        private const string GameSource = @"namespace Game {
            public class PlayerProfileComponent { }
            public static class UISceneExtentions { public static object GetUI(object world) { return world; } } }";

        // What build 17 did, from an assembly in the same kind of context: the control that shows the recorder sees it.
        private const string PartialNameSource = @"public static class PartialName {
            public static System.Type Profile() { return System.Type.GetType(""Game.PlayerProfileComponent, Game"", false); } }";

        private static readonly Lazy<byte[]> Probe = new Lazy<byte[]>(() => FixtureCompiler.Compile("Probe",
            new[] { FixtureCompiler.RepoSource("hosts", "embedded", "Admin", "GameReflection.cs"), FixtureCompiler.RepoSource("hosts", "embedded", "LoadedTypes.cs") },
            new[] { File.ReadAllBytes(typeof(Log).Assembly.Location) }));

        /// <summary>A collectible context that serves the fixtures by simple name (a partial name included) and records each request.</summary>
        private sealed class Recorder : AssemblyLoadContext, IDisposable
        {
            private readonly Dictionary<string, byte[]> images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            private readonly List<string> asked = new List<string>();
            private readonly List<string> loaded = new List<string>();
            private readonly AssemblyLoadEventHandler onLoad;

            public Recorder() : base("gameload-" + Guid.NewGuid().ToString("N"), true)
            {
                onLoad = (sender, args) =>
                {
                    if (GetLoadContext(args.LoadedAssembly) != this) return;
                    lock (loaded) loaded.Add(args.LoadedAssembly.GetName().Name);
                };
                AppDomain.CurrentDomain.AssemblyLoad += onLoad;
            }

            public Recorder With(string name, byte[] image)
            {
                images[name] = image;
                return this;
            }

            /// <summary>How often the runtime asked this context for <paramref name="name"/> (our own Get calls included).</summary>
            public int Asked(string name)
            {
                lock (asked) return asked.Count(n => n == name);
            }

            /// <summary>How often an assembly named <paramref name="name"/> loaded into this context (the AssemblyLoad event).</summary>
            public int Loaded(string name)
            {
                lock (loaded) return loaded.Count(n => n == name);
            }

            public Assembly Get(string name)
            {
                return LoadFromAssemblyName(new AssemblyName(name));
            }

            /// <summary>Points the sandboxed LoadedTypes at this context's assemblies (the process holds other tests' "Game"s).</summary>
            public void Isolate(Assembly probe)
            {
                Func<IEnumerable<Assembly>> mine = () => AppDomain.CurrentDomain.GetAssemblies().Where(a => GetLoadContext(a) == this);
                probe.GetType("McvsOffline.Embedded.LoadedTypes", true).GetProperty("Source", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, mine);
            }

            protected override Assembly Load(AssemblyName name)
            {
                lock (asked) asked.Add(name.Name);
                byte[] image;
                return images.TryGetValue(name.Name, out image) ? LoadFromStream(new MemoryStream(image)) : null;
            }

            public void Dispose()
            {
                AppDomain.CurrentDomain.AssemblyLoad -= onLoad;
                Unload();
            }
        }

        private static Recorder Fixtures()
        {
            return new Recorder().With("Probe", Probe.Value)
                                 .With("Gameloft.Engine", FixtureCompiler.Compile("Gameloft.Engine", EngineSource))
                                 .With("Game", FixtureCompiler.Compile("Game", GameSource));
        }

        private static object Call(object target, string method, params object[] args)
        {
            Type type = target as Type ?? target.GetType();
            MethodInfo m = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                               .Single(x => x.Name == method && x.GetParameters().Length == args.Length);
            return m.Invoke(target is Type ? null : target, args);
        }

        private static object Get(object target, string property)
        {
            return target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        }

        /// <summary>Every public member of GameReflection that needs a bound type, once.</summary>
        private static void UseEverything(object game)
        {
            Get(game, "IsBound");
            Get(game, "CanSave");
            Call(game, "Application");
            Call(game, "ClientWorld");
            Call(game, "ClientUi");
            Call(game, "Post", (Action)(() => { }), null);
            Call(game, "AtMainMenu", 0);
            Call(game, "WaitForFrame", 0);
            Call(game, "RequestSave", new object[] { null });
            Call(game, "FindPlayerProfile", new object());
        }

        /// <summary>The control: a partial-name Type.GetType from a sandboxed assembly does reach the recorder, and loads Game.</summary>
        [Fact]
        public void APartialNameLoadIsSeen()
        {
            using (Recorder context = Fixtures().With("PartialName", FixtureCompiler.Compile("PartialName", PartialNameSource)))
            {
                Type found = (Type)Call(context.Get("PartialName").GetType("PartialName", true), "Profile");
                Assert.NotNull(found);
                Assert.Equal(1, context.Asked("Game"));
                Assert.Equal(1, context.Loaded("Game"));
            }
        }

        /// <summary>
        /// The PC order: the bootstrap (GameReflection.Load) runs with Gameloft.Engine loaded and Game not yet. Nothing asks for
        /// Game, whatever is used; GameApplication binds; once the engine has loaded Game, the next use binds its types.
        /// </summary>
        [Fact]
        public void LoadNeverLoadsGameAndBindsOnceTheEngineHas()
        {
            using (Recorder context = Fixtures())
            {
                Assembly probe = context.Get("Probe");
                context.Isolate(probe);
                Type reflection = probe.GetType("McvsOffline.Embedded.Admin.GameReflection", true);

                // Before the engine: nothing binds, nothing is asked for.
                object early = Call(reflection, "Load");
                UseEverything(early);
                Assert.False((bool)Get(early, "IsBound"));
                Assert.Equal(0, context.Asked("Gameloft.Engine"));
                Assert.Equal(0, context.Asked("Game"));

                // The engine loaded (PC: by EngineBridge, through the codeBase): GameApplication binds on the next use.
                context.Get("Gameloft.Engine");
                object game = Call(reflection, "Load");
                Assert.True((bool)Get(game, "IsBound"));
                Assert.True((bool)Get(early, "IsBound"));
                Assert.False((bool)Get(game, "CanSave"));
                UseEverything(game);
                UseEverything(early);
                Assert.Null(Get(game, "PlayerProfileComponent"));
                Assert.Null(Get(game, "UiExtensions"));
                Assert.Equal(0, context.Asked("Game"));
                Assert.Equal(0, context.Loaded("Game"));

                // The engine loads Game itself (gameload's full name): the next use binds Game's types, from that assembly.
                Assembly loadedGame = context.Get("Game");
                Assert.True((bool)Get(game, "CanSave"));
                Assert.Same(loadedGame.GetType("Game.PlayerProfileComponent"), Get(game, "PlayerProfileComponent"));
                Assert.Same(loadedGame.GetType("Game.UISceneExtentions"), Get(game, "UiExtensions"));
                Call(game, "FindPlayerProfile", new object());
                Assert.True((bool)Get(early, "CanSave"));
                Assert.Equal(1, context.Asked("Game"));   // the test's own load only
                Assert.Equal(1, context.Loaded("Game"));
            }
        }

        /// <summary>The Android order: Game and Gameloft.Engine are loaded before the bootstrap, so Load binds everything at once.</summary>
        [Fact]
        public void WhenTheGameIsLoadedLoadBindsAtOnce()
        {
            using (Recorder context = Fixtures())
            {
                Assembly probe = context.Get("Probe");
                context.Isolate(probe);
                context.Get("Gameloft.Engine");
                Assembly loadedGame = context.Get("Game");
                object game = Call(probe.GetType("McvsOffline.Embedded.Admin.GameReflection", true), "Load");
                Assert.Same(loadedGame.GetType("Game.PlayerProfileComponent"), Get(game, "PlayerProfileComponent"));
                Assert.Same(loadedGame.GetType("Game.UISceneExtentions"), Get(game, "UiExtensions"));
                Assert.True((bool)Get(game, "CanSave"));
                Assert.Equal(1, context.Asked("Game"));
            }
        }

        // ------------------------------------------------------------------ source scan

        /// <summary>A string literal that ends in a redirected assembly's bare name: "T, Game", "T, Gameloft.Engine".</summary>
        private static readonly Regex PartialQualifiedName =
            new Regex(@"""[^""\r\n]*,\s*(Game|Gameloft\.Engine|Gameloft\.Util|EngineBridge)\s*""", RegexOptions.CultureInvariant);

        /// <summary>A load by a redirected assembly's bare name.</summary>
        private static readonly Regex BareNameLoad =
            new Regex(@"(Assembly\.Load|LoadWithPartialName|\.Load|new\s+AssemblyName|CreateInstance)\s*\(\s*""(Game|Gameloft\.Engine|Gameloft\.Util|EngineBridge)""",
                      RegexOptions.CultureInvariant);

        /// <summary>Type.GetType outside the one fallback that may use it (LoadedTypes.EngineType, Android only) and RuntimeInfo (mscorlib).</summary>
        private static readonly string[] TypeGetTypeAllowed = { "LoadedTypes.cs", "RuntimeInfo.cs" };

        [Fact]
        public void TheScanCatchesBuild17()
        {
            Assert.Matches(PartialQualifiedName, "public const string PlayerProfileTypeName = \"Game.PlayerProfileComponent, Game\";");
            Assert.Matches(PartialQualifiedName, "Type.GetType(\"Gameloft.Engine.GameApplication, Gameloft.Engine\", false)");
            Assert.Matches(BareNameLoad, "Assembly.Load(\"Game\")");
            Assert.Matches(BareNameLoad, "Assembly.LoadWithPartialName(\"Gameloft.Util\")");
            Assert.Matches(BareNameLoad, "Assembly.Load(new AssemblyName(\"Game\"))");
            Assert.DoesNotMatch(PartialQualifiedName, "LoadedTypes.Find(LoadedTypes.GameAssembly, \"Game.PlayerProfileComponent\")");
            Assert.DoesNotMatch(PartialQualifiedName, "\"Game loaded from the game folder\"");
        }

        /// <summary>No code in hosts/ or server/ names a redirected assembly by its bare name again (comments are not code).</summary>
        [Fact]
        public void NoPartialNameOfARedirectedAssemblyComesBack()
        {
            var problems = new List<string>();
            foreach (string top in new[] { "hosts", "server" })
            {
                string root = Path.Combine(TestEnvironment.RepoRoot, top);
                foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(TestEnvironment.RepoRoot, file);
                    if (relative.Split(Path.DirectorySeparatorChar).Any(p => p == "obj" || p == "bin")) continue;
                    string[] lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string code = lines[i].Trim();
                        if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("*", StringComparison.Ordinal)) continue;
                        if (PartialQualifiedName.IsMatch(code) || BareNameLoad.IsMatch(code))
                            problems.Add(relative + ":" + (i + 1) + ": " + code);
                        if (code.Contains("Type.GetType(") && !TypeGetTypeAllowed.Contains(Path.GetFileName(file)))
                            problems.Add(relative + ":" + (i + 1) + ": Type.GetType (use LoadedTypes): " + code);
                    }
                }
            }
            Assert.True(problems.Count == 0, "partial-name loads of a redirected assembly (docs/design/pc-port.md §8.9):\n" + string.Join("\n", problems));
        }
    }
}
