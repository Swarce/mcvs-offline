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
            byte[] server = Fixtures.Server();
            byte[] patched = Patch(FixtureCompiler.Compile("Gameloft.Util", Fixtures.GameloftUtil), new GeoPatch(), server, port: 52001);

            using (var sandbox = new FixtureCompiler.Sandbox().With("Gameloft.Util", patched).With("McvsOffline.Server", server))
            {
                Assembly util = sandbox.Get("Gameloft.Util");
                object geo = Activator.CreateInstance(util.GetType("Gameloft.Util.Geolocalization"));
                // Nothing decided yet (or the host never ran): the port baked at patch time.
                Assert.Equal("http://127.0.0.1:52001/location/?ip=", Call(util, "Gameloft.Util.Geolocalization", "get_GeolocalizationUrl", geo));
                Assert.Equal("https://it.gameloft.org/dump/dump.edgelist", Call(util, "Gameloft.Util.Geolocalization", "get_EdgeListUrl", geo));

                // Build 26: the port the host bound at this start (a port fallback), read at every call.
                Call(sandbox.Get("McvsOffline.Server"), "McvsOffline.PortHooks", "Configure", null, 51783, 0);
                Assert.Equal("http://127.0.0.1:51783/location/?ip=", Call(util, "Gameloft.Util.Geolocalization", "get_GeolocalizationUrl", geo));
                Call(sandbox.Get("McvsOffline.Server"), "McvsOffline.PortHooks", "Configure", null, 0, 0);
                Assert.Equal("http://127.0.0.1:52001/location/?ip=", Call(util, "Gameloft.Util.Geolocalization", "get_GeolocalizationUrl", geo));
            }
        }

        [Fact]
        public void GeoNeedsTheHookInTheServer()
        {
            byte[] noHooks = FixtureCompiler.Compile("McvsOffline.Server", "namespace McvsOffline { public static class Bootstrap { public static void Init() { } } }");
            PatchException ex = Assert.Throws<PatchException>(() => Patch(FixtureCompiler.Compile("Gameloft.Util", Fixtures.GameloftUtil), new GeoPatch(), noHooks));
            Assert.Contains("McvsOffline.PortHooks", ex.Message);
        }

        // ------------------------------------------------------------------ finderport

        [Fact]
        public void FinderPortFollowsThePortTheHostChose()
        {
            byte[] server = Fixtures.Server();
            byte[] patched = Patch(FixtureCompiler.Compile("Gameloft.Engine", Fixtures.GameloftEngine), new List<IlPatch> { new FinderPortPatch(), new LanPatch() },
                                   server, 51780, out List<string> notes);
            Assert.Contains(notes, n => n.Contains("LocalAnubisFinderPort = McvsOffline.PortHooks.LocalAnubisFinderPort(51750)"));

            using (var sandbox = new FixtureCompiler.Sandbox().With("Gameloft.Engine", patched).With("McvsOffline.Server", server))
            {
                Assembly engine = sandbox.Get("Gameloft.Engine");
                Type appType = engine.GetType("Gameloft.Engine.GameApplication");
                object app = Activator.CreateInstance(appType);
                appType.GetProperty("Args").SetValue(app, Activator.CreateInstance(engine.GetType("Gameloft.Engine.GameApplication+Arguments")));
                appType.GetProperty("Instance").SetValue(null, app);
                Type settings = engine.GetType("Gameloft.Engine.ClientSettings");
                int FinderPort() => (int)settings.GetProperty("LocalAnubisFinderPort").GetValue(Activator.CreateInstance(settings));

                Assert.Equal(51750, FinderPort());
                Call(sandbox.Get("McvsOffline.Server"), "McvsOffline.PortHooks", "Configure", null, 51780, 51752);
                Assert.Equal(51752, FinderPort());
                // The 'lan' P3 site of the same constructor still works next to it.
                Assert.Equal("localhost", (string)settings.GetProperty("LocalAnubisFinderHost").GetValue(Activator.CreateInstance(settings)));
            }
        }

        [Fact]
        public void FinderPortRefusesAnotherConstant()
        {
            string changed = Fixtures.GameloftEngine.Replace("LocalAnubisFinderPort { get; set; } = 51750;", "LocalAnubisFinderPort { get; set; } = 51751;");
            Assert.NotEqual(Fixtures.GameloftEngine, changed);
            Assert.Throws<PatchException>(() => Patch(FixtureCompiler.Compile("Gameloft.Engine", changed), new FinderPortPatch()));
        }

        [Fact]
        public void GeoRefusesAnUnexpectedUrl()
        {
            byte[] other = FixtureCompiler.Compile("Gameloft.Util", Fixtures.GameloftUtil.Replace("ip.ecomapis.gameloft.org", "geo.example"));
            Assert.Throws<PatchException>(() => Patch(other, new GeoPatch()));
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
            Assert.Equal(new[] { "hook", "headless", "headless-engine", "physics", "finderport", "finder", "lan" }, ApkPatcher.SelectPatches(new[] { "lan" }).Select(p => p.Id));
            Assert.Equal(new[] { "hook", "headless", "headless-engine", "physics", "finderport" }, ApkPatcher.SelectPatches(new string[0]).Select(p => p.Id));
            Assert.Equal(new[] { "hook" }, ApkPatcher.SelectPatches(new string[0], headless: false).Select(p => p.Id));
            Assert.Equal(new[]
                         {
                             "hook", "headless", "headless-engine", "physics", "finderport", "nativeonline", "waits", "geo", "finder", "store", "signatures", "inbox", "adminpage",
                             "debloat", "adminlink", "graphics", "crmtables", "jsonnorm",
                         },
                         ApkPatcher.SelectPatches(PatcherOptions.ParsePatchList("default")).Select(p => p.Id));
            Assert.Equal(new[] { "finder", "lan" }, PatcherOptions.ParsePatchList("lan"));
            Assert.Empty(PatcherOptions.ParsePatchList("none"));
            Assert.Equal(new[] { "adminlink", "adminpage", "botmove", "crmtables", "debloat", "finder", "forum", "game", "geo", "graphics", "inbox", "jsonnorm", "lan", "nativeonline", "signatures", "store", "targetsdk", "waits" },
                         PatcherOptions.ParsePatchList("all"));
            Assert.Equal(new[] { "adminlink", "adminpage", "crmtables", "debloat", "finder", "game", "geo", "graphics", "inbox", "jsonnorm", "nativeonline", "signatures", "store", "targetsdk", "waits" },
                         PatcherOptions.ParsePatchList("default"));
            Assert.Equal(new[] { "targetsdk" }, PatcherOptions.ParsePatchList("targetsdk"));
            Assert.Equal(new[] { "game" }, PatcherOptions.ParsePatchList("game"));
            Assert.Equal(new[] { "hook", "headless", "headless-engine", "physics", "finderport" }, ApkPatcher.SelectPatches(new[] { "game" }).Select(p => p.Id));   // no IL
            Assert.Equal(new[] { "signatures" }, PatcherOptions.ParsePatchList("signatures"));
            Assert.Equal(new[] { "debloat" }, PatcherOptions.ParsePatchList("debloat"));
            Assert.Throws<ArgumentException>(() => PatcherOptions.ParsePatchList("geo,unlockall"));

            // The settings-page triggers: adminpage and adminlink by default, forum only on request (--patch default,forum).
            Assert.Equal(new[] { "adminlink", "adminpage", "crmtables", "debloat", "finder", "forum", "game", "geo", "graphics", "inbox", "jsonnorm", "nativeonline", "signatures", "store", "targetsdk", "waits" },
                         PatcherOptions.ParsePatchList("default,forum"));
            Assert.Equal(new[] { "graphics" }, PatcherOptions.ParsePatchList("graphics"));
            Assert.Equal(new[] { "hook", "headless", "headless-engine", "physics", "finderport", "graphics" }, ApkPatcher.SelectPatches(new[] { "graphics" }).Select(p => p.Id));
            Assert.Equal(new[] { "forum" }, PatcherOptions.ParsePatchList("FORUM"));
            Assert.DoesNotContain("forum", PatcherOptions.DefaultPatches);
            Assert.Equal(new[] { "lan", "forum", "botmove" }, PatcherOptions.OptionalPatches.Except(PatcherOptions.DefaultPatches));
            // Build 27: 'botmove' only on request (the default APK's Game.dll is unchanged), after every earlier IL patch (build 30's crmtables and jsonnorm follow it).
            Assert.DoesNotContain("botmove", PatcherOptions.DefaultPatches);
            Assert.Equal(new[] { "botmove" }, PatcherOptions.ParsePatchList("BotMove"));
            Assert.Equal(new[] { "hook", "headless", "headless-engine", "physics", "finderport", "graphics", "botmove" },
                         ApkPatcher.SelectPatches(new[] { "botmove", "graphics" }).Select(p => p.Id));
            Assert.Equal(new[] { "hook", "headless", "headless-engine", "physics", "finderport", "adminpage", "forum" },
                         ApkPatcher.SelectPatches(new[] { "forum", "adminpage" }).Select(p => p.Id));
            Assert.Equal(new[] { "hook", "headless", "headless-engine", "physics", "finderport", "adminlink" }, ApkPatcher.SelectPatches(new[] { "adminlink" }).Select(p => p.Id));
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
            Assert.Equal("Game.dll", Assert.Single(ApkPatcher.AllPatches, p => p.Id == "botmove").AssemblyFile);
        }

        [Fact]
        public void NoPatchTargetsATypemapPinnedAssembly()
        {
            foreach (IlPatch patch in ApkPatcher.AllPatches)
                Assert.False(KnownBuild.TypemapPinned.ContainsKey(patch.AssemblyFile), patch.Id + " targets " + patch.AssemblyFile);
        }
    }
}
