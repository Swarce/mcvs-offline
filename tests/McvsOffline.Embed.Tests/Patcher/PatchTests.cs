using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher;
using McvsOffline.Patcher.Manifest;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>Each patch on its look-alike fixture: the patched code is executed, and broken shapes are refused.</summary>
    public class PatchTests
    {
        internal static byte[] Patch(byte[] fixture, IlPatch patch, byte[] server = null, int port = 51780)
        {
            return Patch(fixture, new List<IlPatch> { patch }, server, port, out _);
        }

        internal static byte[] Patch(byte[] fixture, List<IlPatch> patches, byte[] server, int port, out List<string> notes)
        {
            server = server ?? Fixtures.Server();
            using (var resolver = new DefaultAssemblyResolver())
            using (ModuleDefinition serverModule = ModuleDefinition.ReadModule(new MemoryStream(server)))
            {
                var context = new PatchContext(serverModule, port);
                var reader = new ReaderParameters { InMemory = true, ReadingMode = ReadingMode.Immediate, AssemblyResolver = resolver };
                byte[] patched = ApkPatcher.PatchAssembly(fixture, patches, context, reader, serverModule.Mvid, s => { });
                notes = context.Notes;
                return patched;
            }
        }

        private static object Call(Assembly assembly, string type, string method, object target = null, params object[] args)
        {
            Type t = assembly.GetType(type, true);
            MethodInfo m = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                            .Single(x => x.Name == method && x.GetParameters().Length == args.Length);
            try
            {
                return m.Invoke(target, args);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException;
            }
        }

        private static object StaticField(Assembly assembly, string type, string field)
        {
            return assembly.GetType(type, true).GetField(field).GetValue(null);
        }

        // ------------------------------------------------------------------ hook

        [Fact]
        public void HookCallsBootstrapInitThenTheOriginalBody()
        {
            byte[] server = Fixtures.Server();
            byte[] patched = Patch(FixtureCompiler.Compile("NicePlaySDK", Fixtures.NicePlaySdk), new BootstrapHookPatch(), server);

            using (var sandbox = new FixtureCompiler.Sandbox().With("NicePlaySDK", patched).With("McvsOffline.Server", server))
            {
                Assembly sdk = sandbox.Get("NicePlaySDK");
                Call(sdk, "Gameloft.NicePlaySDK.NicePlaySDKDLL", "OnInitialize");
                Assert.Equal(1, StaticField(sandbox.Get("McvsOffline.Server"), "McvsOffline.Bootstrap", "Calls"));
                Assert.Equal(1, StaticField(sdk, "Gameloft.NicePlaySDK.NicePlaySDKDLL", "OriginalBodyRuns"));
            }
        }

        [Fact]
        public void HookSwallowsAThrowingBootstrap()
        {
            byte[] server = Fixtures.Server(bootstrapThrows: true);
            byte[] patched = Patch(FixtureCompiler.Compile("NicePlaySDK", Fixtures.NicePlaySdk), new BootstrapHookPatch(), server);

            using (var sandbox = new FixtureCompiler.Sandbox().With("NicePlaySDK", patched).With("McvsOffline.Server", server))
            {
                Assembly sdk = sandbox.Get("NicePlaySDK");
                Call(sdk, "Gameloft.NicePlaySDK.NicePlaySDKDLL", "OnInitialize");
                Assert.Equal(1, StaticField(sdk, "Gameloft.NicePlaySDK.NicePlaySDKDLL", "OriginalBodyRuns"));
            }
        }

        [Fact]
        public void HookSurvivesAMissingServerAssembly()
        {
            byte[] patched = Patch(FixtureCompiler.Compile("NicePlaySDK", Fixtures.NicePlaySdk), new BootstrapHookPatch());

            using (var sandbox = new FixtureCompiler.Sandbox().With("NicePlaySDK", patched))
            {
                Assembly sdk = sandbox.Get("NicePlaySDK");
                Call(sdk, "Gameloft.NicePlaySDK.NicePlaySDKDLL", "OnInitialize");
                Assert.Equal(1, StaticField(sdk, "Gameloft.NicePlaySDK.NicePlaySDKDLL", "OriginalBodyRuns"));
            }
        }

        [Fact]
        public void HookRefusesAnAlreadyPatchedAssembly()
        {
            byte[] once = Patch(FixtureCompiler.Compile("NicePlaySDK", Fixtures.NicePlaySdk), new BootstrapHookPatch());
            var ex = Assert.Throws<PatchException>(() => Patch(once, new BootstrapHookPatch()));
            Assert.Contains("already patched", ex.Message);
        }

        [Fact]
        public void HookRequiresBootstrapInitInTheServerAssembly()
        {
            byte[] notAServer = FixtureCompiler.Compile("McvsOffline.Server", "namespace McvsOffline { public static class Other { } }");
            var ex = Assert.Throws<PatchException>(() =>
                Patch(FixtureCompiler.Compile("NicePlaySDK", Fixtures.NicePlaySdk), new BootstrapHookPatch(), notAServer));
            Assert.Contains("McvsOffline.Bootstrap", ex.Message);
        }

        [Fact]
        public void PatchedAssemblyGetsANewDeterministicMvid()
        {
            byte[] original = FixtureCompiler.Compile("NicePlaySDK", Fixtures.NicePlaySdk);
            byte[] server = Fixtures.Server();
            byte[] a = Patch(original, new BootstrapHookPatch(), server);
            byte[] b = Patch(original, new BootstrapHookPatch(), server);

            Assert.NotEqual(ApkPatcher.ReadMvid(original), ApkPatcher.ReadMvid(a));
            Assert.Equal(ApkPatcher.ReadMvid(a), ApkPatcher.ReadMvid(b));
        }

        // ------------------------------------------------------------------ geo

        [Fact]
        public void GeoPointsTheUrlAtTheEmbeddedServer()
        {
            byte[] patched = Patch(FixtureCompiler.Compile("Gameloft.Util", Fixtures.GameloftUtil), new GeoPatch(), port: 52001);

            using (var sandbox = new FixtureCompiler.Sandbox().With("Gameloft.Util", patched))
            {
                Assembly util = sandbox.Get("Gameloft.Util");
                object geo = Activator.CreateInstance(util.GetType("Gameloft.Util.Geolocalization"));
                Assert.Equal("http://127.0.0.1:52001/location/?ip=", Call(util, "Gameloft.Util.Geolocalization", "get_GeolocalizationUrl", geo));
                Assert.Equal("https://it.gameloft.org/dump/dump.edgelist", Call(util, "Gameloft.Util.Geolocalization", "get_EdgeListUrl", geo));
            }
        }

        [Fact]
        public void GeoRefusesAnUnexpectedUrl()
        {
            byte[] other = FixtureCompiler.Compile("Gameloft.Util", Fixtures.GameloftUtil.Replace("ip.ecomapis.gameloft.org", "geo.example"));
            Assert.Throws<PatchException>(() => Patch(other, new GeoPatch()));
        }

        // ------------------------------------------------------------------ bottile

        [Fact]
        public void BotTileStartsTheGsFlowAndLeavesOtherCallersAlone()
        {
            byte[] original = FixtureCompiler.Compile("Game", Fixtures.Game);
            using (var sandbox = new FixtureCompiler.Sandbox().With("Game", original))
            {
                object component = Activator.CreateInstance(sandbox.Get("Game").GetType("Game.GameClientComponent"));
                Assert.Equal("OfflineMatchWithBots", Call(sandbox.Get("Game"), "Game.GameClientComponent", "BotTile", component));
            }

            byte[] patched = Patch(original, new List<IlPatch> { new BotTilePatch() }, null, 51780, out List<string> notes);
            Assert.Contains(notes, n => n.Contains("IL_0007 ldc.i4.2"));
            using (var sandbox = new FixtureCompiler.Sandbox().With("Game", patched))
            {
                Assembly game = sandbox.Get("Game");
                object component = Activator.CreateInstance(game.GetType("Game.GameClientComponent"));
                Assert.Equal("OnlineAndTutorial", Call(game, "Game.GameClientComponent", "BotTile", component));
                Assert.Equal("OfflineMatchWithBots", Call(game, "Game.GameClientComponent", "SameLevel", component));
            }
        }

        [Fact]
        public void BotTileRefusesADifferentLambda()
        {
            string changed = Fixtures.Game.Replace(
                "RunTask((CancellationToken ct) => StartAutoMatchAsync(base.CancellationToken, MatchConnectionType.OfflineMatchWithBots), base.CancellationToken);\n        }\n        private void StartAutoMatchSameAsPlayerLevel",
                "RunTask((CancellationToken ct) => StartAutoMatchAsync(ct, MatchConnectionType.OfflineMatchWithBots), base.CancellationToken);\n        }\n        private void StartAutoMatchSameAsPlayerLevel");
            Assert.NotEqual(Fixtures.Game, changed);
            var ex = Assert.Throws<PatchException>(() => Patch(FixtureCompiler.Compile("Game", changed), new BotTilePatch()));
            Assert.Contains("does not match", ex.Message);
        }

        [Fact]
        public void BotTileRefusesDifferentEnumValues()
        {
            string changed = Fixtures.Game.Replace("{ Undefined, OnlineAndTutorial, OfflineMatchWithBots }", "{ OnlineAndTutorial, OfflineMatchWithBots, Undefined }");
            Assert.Throws<PatchException>(() => Patch(FixtureCompiler.Compile("Game", changed), new BotTilePatch()));
        }

        // ------------------------------------------------------------------ finder + lan

        [Fact]
        public void FinderListensOnAllInterfaces()
        {
            byte[] patched = Patch(FixtureCompiler.Compile("Gameloft.Engine", Fixtures.GameloftEngine), new FinderPatch());

            using (var sandbox = new FixtureCompiler.Sandbox().With("Gameloft.Engine", patched))
            {
                Assembly engine = sandbox.Get("Gameloft.Engine");
                Type finder = engine.GetType("Gameloft.Engine.LocalAnubis.Finder");
                object instance = Activator.CreateInstance(finder, null, null, 51750);
                Assert.Equal("http://*:51750/", engine.GetType("Gameloft.Engine.HTTPServer").GetField("Prefix").GetValue(instance));
            }
        }

        [Fact]
        public void FinderRefusesAnotherPrefix()
        {
            string changed = Fixtures.GameloftEngine.Replace("port, \"localhost\", \"http\")", "port, \"+\", \"http\")");
            Assert.NotEqual(Fixtures.GameloftEngine, changed);
            Assert.Throws<PatchException>(() => Patch(FixtureCompiler.Compile("Gameloft.Engine", changed), new FinderPatch()));
        }

        [Fact]
        public void LanCachesTheAdvertisedHostAndHonoursRemoteIp()
        {
            byte[] server = Fixtures.Server();
            byte[] patched = Patch(FixtureCompiler.Compile("Gameloft.Engine", Fixtures.GameloftEngine),
                                   new List<IlPatch> { new FinderPatch(), new LanPatch() }, server, 51780, out _);

            using (var sandbox = new FixtureCompiler.Sandbox().With("Gameloft.Engine", patched).With("McvsOffline.Server", server))
            {
                Assembly engine = sandbox.Get("Gameloft.Engine");
                Type appType = engine.GetType("Gameloft.Engine.GameApplication");
                Type argsType = engine.GetType("Gameloft.Engine.GameApplication+Arguments");
                object app = Activator.CreateInstance(appType);
                object args = Activator.CreateInstance(argsType);
                appType.GetProperty("Args").SetValue(app, args);
                appType.GetProperty("Instance").SetValue(null, app);
                Type manager = engine.GetType("Gameloft.Engine.LocalAnubisManager");
                Type settings = engine.GetType("Gameloft.Engine.ClientSettings");
                string FinderHost() => (string)settings.GetProperty("LocalAnubisFinderHost").GetValue(Activator.CreateInstance(settings));
                string PublicHost() => (string)manager.GetProperty("PublicHost").GetValue(null);

                // Single device: unchanged behaviour.
                Assert.Equal("localhost", FinderHost());
                Call(engine, "Gameloft.Engine.LocalAnubisManager", "Start");
                Assert.Equal(1, manager.GetField("Starts").GetValue(null));
                Assert.Equal("localhost", PublicHost());

                // P3: a guest's -RemoteIp points its Finder at the host.
                argsType.GetField("RemoteIp").SetValue(args, "192.168.1.50");
                Assert.Equal("192.168.1.50", FinderHost());

                // P2: a LAN host computes the advertised host once at Start (-GSRemoteIp wins) ...
                argsType.GetField("LocalAnubisHost").SetValue(args, "0.0.0.0");
                argsType.GetField("GSRemoteIp").SetValue(args, "192.168.1.9");
                Call(engine, "Gameloft.Engine.LocalAnubisManager", "Start");
                Assert.Equal("192.168.1.9", PublicHost());
                // ... and keeps it while the Controller swaps Args for the lobbyconfig cmd_args (verify-C6 §2.6).
                appType.GetProperty("Args").SetValue(app, Activator.CreateInstance(argsType));
                Assert.Equal("192.168.1.9", PublicHost());

                // A failure inside the Start prologue resets the cache and the game falls back to its own logic.
                appType.GetProperty("Instance").SetValue(null, null);
                Call(engine, "Gameloft.Engine.LocalAnubisManager", "Start");
                Assert.Equal(3, manager.GetField("Starts").GetValue(null));
            }
        }

        [Fact]
        public void LanRefusesAChangedGetPublicHost()
        {
            string changed = Fixtures.GameloftEngine.Replace("if (IPAddress.IsLoopback(Host)) return \"localhost\";", "if (Host == null) return \"none\";");
            Assert.NotEqual(Fixtures.GameloftEngine, changed);
            Assert.Throws<PatchException>(() => Patch(FixtureCompiler.Compile("Gameloft.Engine", changed), new LanPatch()));
        }

        // ------------------------------------------------------------------ selection

        [Fact]
        public void LanImpliesFinderAndHookIsAlwaysFirst()
        {
            Assert.Equal(new[] { "hook", "headless", "headless-engine", "physics", "finder", "lan" }, ApkPatcher.SelectPatches(new[] { "lan" }).Select(p => p.Id));
            Assert.Equal(new[] { "hook", "headless", "headless-engine", "physics" }, ApkPatcher.SelectPatches(new string[0]).Select(p => p.Id));
            Assert.Equal(new[] { "hook" }, ApkPatcher.SelectPatches(new string[0], headless: false).Select(p => p.Id));
            Assert.Equal(new[]
                         {
                             "hook", "headless", "headless-engine", "physics", "nativeonline", "waits", "geo", "bottile", "finder", "store", "signatures", "adminpage",
                             "debloat", "adminlink", "graphics",
                         },
                         ApkPatcher.SelectPatches(PatcherOptions.ParsePatchList("default")).Select(p => p.Id));
            Assert.Equal(new[] { "finder", "lan" }, PatcherOptions.ParsePatchList("lan"));
            Assert.Empty(PatcherOptions.ParsePatchList("none"));
            Assert.Equal(new[] { "adminlink", "adminpage", "bottile", "debloat", "finder", "forum", "game", "geo", "graphics", "lan", "nativeonline", "signatures", "store", "targetsdk", "waits" },
                         PatcherOptions.ParsePatchList("all"));
            Assert.Equal(new[] { "adminlink", "adminpage", "bottile", "debloat", "finder", "game", "geo", "graphics", "nativeonline", "signatures", "store", "targetsdk", "waits" },
                         PatcherOptions.ParsePatchList("default"));
            Assert.Equal(new[] { "targetsdk" }, PatcherOptions.ParsePatchList("targetsdk"));
            Assert.Equal(new[] { "game" }, PatcherOptions.ParsePatchList("game"));
            Assert.Equal(new[] { "hook", "headless", "headless-engine", "physics" }, ApkPatcher.SelectPatches(new[] { "game" }).Select(p => p.Id));   // no IL
            Assert.Equal(new[] { "signatures" }, PatcherOptions.ParsePatchList("signatures"));
            Assert.Equal(new[] { "debloat" }, PatcherOptions.ParsePatchList("debloat"));
            Assert.Throws<ArgumentException>(() => PatcherOptions.ParsePatchList("geo,unlockall"));

            // The settings-page triggers: adminpage and adminlink by default, forum only on request (--patch default,forum).
            Assert.Equal(new[] { "adminlink", "adminpage", "bottile", "debloat", "finder", "forum", "game", "geo", "graphics", "nativeonline", "signatures", "store", "targetsdk", "waits" },
                         PatcherOptions.ParsePatchList("default,forum"));
            Assert.Equal(new[] { "graphics" }, PatcherOptions.ParsePatchList("graphics"));
            Assert.Equal(new[] { "hook", "headless", "headless-engine", "physics", "graphics" }, ApkPatcher.SelectPatches(new[] { "graphics" }).Select(p => p.Id));
            Assert.Equal(new[] { "forum" }, PatcherOptions.ParsePatchList("FORUM"));
            Assert.DoesNotContain("forum", PatcherOptions.DefaultPatches);
            Assert.Equal(new[] { "lan", "forum" }, PatcherOptions.OptionalPatches.Except(PatcherOptions.DefaultPatches));
            Assert.Equal(new[] { "hook", "headless", "headless-engine", "physics", "adminpage", "forum" },
                         ApkPatcher.SelectPatches(new[] { "forum", "adminpage" }).Select(p => p.Id));
            Assert.Equal(new[] { "hook", "headless", "headless-engine", "physics", "adminlink" }, ApkPatcher.SelectPatches(new[] { "adminlink" }).Select(p => p.Id));
        }

        [Fact]
        public void ThePlayerDocsListTheDefaultPatchSetAndEveryOptionalPatch()
        {
            // A player who copies the documented --patch list must get the default set: without 'graphics' the graphics
            // profile silently does nothing.
            string setup = File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot, "docs", "PLAYER-SETUP.md"));
            string row = Assert.Single(setup.Split('\n'), l => l.StartsWith("| `--patch ", StringComparison.Ordinal) && l.Contains("this is the default set"));
            string list = row.Substring("| `--patch ".Length, row.IndexOf('`', "| `--patch ".Length) - "| `--patch ".Length);
            Assert.Equal(PatcherOptions.DefaultPatches, list.Split(','));
            foreach (string id in PatcherOptions.OptionalPatches)
                Assert.True(setup.Contains("\n- **" + id + "**"), "PLAYER-SETUP.md has no '- **" + id + "**' entry in its patch list");

            string readme = File.ReadAllText(Path.Combine(TestEnvironment.RepoRoot, "README.md"));
            string build = Assert.Single(readme.Split('\n'), l => l.StartsWith("| Player build (offline APK) |", StringComparison.Ordinal));
            foreach (string id in PatcherOptions.DefaultPatches)
                Assert.True(System.Text.RegularExpressions.Regex.IsMatch(build, @"[(, ]" + id + "[,;]"), "README.md's player-build row does not list '" + id + "'");
        }

        [Fact]
        public void EveryOptionalPatchIdHasItsPatchAndEveryPatchIdIsUnique()
        {
            Assert.Equal(ApkPatcher.AllPatches.Length, ApkPatcher.AllPatches.Select(p => p.Id).Distinct().Count());
            // "targetsdk" edits the manifest, two layouts and classes.dex (ManifestTargetSdk, BrowserLayoutInsets, DexTargetSdkEdits)
            // and "game" only the manifest (ManifestGame): no IL patch.
            string[] notIl = { ManifestTargetSdk.PatchId, ManifestGame.PatchId };
            foreach (string id in PatcherOptions.OptionalPatches.Concat(PatcherOptions.MandatoryPatches).Where(id => !notIl.Contains(id)))
                Assert.Contains(ApkPatcher.AllPatches, p => p.Id == id);
            foreach (string id in notIl)
            {
                Assert.Contains(id, PatcherOptions.DefaultPatches);
                Assert.DoesNotContain(ApkPatcher.AllPatches, p => p.Id == id);
            }
            Assert.Equal("Game.dll", Assert.Single(ApkPatcher.AllPatches, p => p.Id == "adminpage").AssemblyFile);
            Assert.Equal("Gameloft.Engine.dll", Assert.Single(ApkPatcher.AllPatches, p => p.Id == "adminlink").AssemblyFile);
            Assert.Equal("Game.dll", Assert.Single(ApkPatcher.AllPatches, p => p.Id == "forum").AssemblyFile);
            Assert.Equal("Gameloft.Engine.dll", Assert.Single(ApkPatcher.AllPatches, p => p.Id == "graphics").AssemblyFile);
        }

        [Fact]
        public void NoPatchTargetsATypemapPinnedAssembly()
        {
            foreach (IlPatch patch in ApkPatcher.AllPatches)
                Assert.False(KnownBuild.TypemapPinned.ContainsKey(patch.AssemblyFile), patch.Id + " targets " + patch.AssemblyFile);
        }
    }
}
