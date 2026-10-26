using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// The device-test-1 patches (headless, headless-engine, nativeonline) applied to the player's REAL Game.dll and
    /// Gameloft.Engine.dll (MCVS_REFS), then loaded on net8 and executed: the safety net, the null-safe helpers, the
    /// early returns and the native-init gate behave as intended, and every patched method JITs. Skipped when the
    /// assemblies are absent, unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    [Collection("GameHooks")]
    public class RealGamePatchTests
    {
        internal const string HookServerSource = @"
namespace McvsOffline
{
    public static class Bootstrap { public static void Init() { } }

    public static class GameHooks
    {
        public static bool Allow;
        public static int AllowCalls;
        public static string LastDatacenter;
        public static int HeadlessCalls;
        public static string LastComponent;
        public static string LastEvent;
        public static System.Exception LastException;

        public static bool AllowNativeOnlineInit(string datacenter) { AllowCalls++; LastDatacenter = datacenter; return Allow; }

        public static void HeadlessComponentException(string component, string eventName, System.Exception exception)
        {
            HeadlessCalls++; LastComponent = component; LastEvent = eventName; LastException = exception;
        }

        // patch 'physics'
        public static int MtCalls;
        public static bool LastHeadless;
        public static bool LastRequested;
        public static bool MtAnswer;
        public static bool GateAnswer = true;
        public static int EnterCalls;
        public static int ExitCalls;

        public static bool PhysicsMultithreading(bool headless, bool requested) { MtCalls++; LastHeadless = headless; LastRequested = requested; return MtAnswer; }

        public static bool EnterPhysicsStep() { EnterCalls++; return GateAnswer; }

        public static void ExitPhysicsStep() { ExitCalls++; }

        // patch 'store'
        public static int StoreCalls;
        public static object LastStoreItem;
        public static System.Exception LastStoreException;

        public static void StoreItemException(object item, System.Exception exception) { StoreCalls++; LastStoreItem = item; LastStoreException = exception; }
    }
}";

        private readonly ITestOutputHelper output;

        public RealGamePatchTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private string Refs()
        {
            string dir = TestEnvironment.GameAssemblies;
            if (dir != null && File.Exists(Path.Combine(dir, "Game.dll")) && File.Exists(Path.Combine(dir, "Gameloft.Engine.dll"))) return dir;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but MCVS_REFS has no Game.dll / Gameloft.Engine.dll");
            output.WriteLine("the game's assemblies are not available (MCVS_REFS): skipped");
            return null;
        }

        /// <summary>Patched Game.dll and Gameloft.Engine.dll images, with the patcher's notes.</summary>
        internal static Dictionary<string, byte[]> PatchReal(string refs, byte[] server, List<string> notes, params IlPatch[] patches)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(refs);
                foreach (string d in resolver.GetSearchDirectories().Where(d => d != refs).ToList()) resolver.RemoveSearchDirectory(d);
                var reader = new ReaderParameters { AssemblyResolver = resolver, InMemory = true, ReadingMode = ReadingMode.Immediate };
                using (ModuleDefinition serverModule = ModuleDefinition.ReadModule(new MemoryStream(server)))
                {
                    var context = new PatchContext(serverModule, 51780);
                    foreach (var group in patches.GroupBy(p => p.AssemblyFile))
                    {
                        byte[] original = File.ReadAllBytes(Path.Combine(refs, group.Key));
                        result[group.Key] = ApkPatcher.PatchAssembly(original, group.ToList(), context, reader, serverModule.Mvid, s => { });
                    }
                    if (notes != null) notes.AddRange(context.Notes);
                }
            }
            return result;
        }

        private static readonly IlPatch[] DeviceOnePatches = { new HeadlessGamePatch(), new HeadlessEnginePatch(), new NativeOnlinePatch() };

        /// <summary>Every patch of Game.dll and Gameloft.Engine.dll this build applies by default, in the patcher's order.</summary>
        internal static readonly IlPatch[] DefaultGamePatches =
        {
            new HeadlessGamePatch(), new HeadlessEnginePatch(), new PhysicsPatch(), new NativeOnlinePatch(), new WaitsPatch(), new BotTilePatch(), new FinderPatch(),
            new StorePatch(), new SignaturesPatch(), new DebloatEnginePatch(),
        };

        [Fact]
        public void HeadlessSafetyNetCatchesOnlyInHeadlessWorlds()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = FixtureCompiler.Compile("McvsOffline.Server", HookServerSource);
            var notes = new List<string>();
            Dictionary<string, byte[]> patched = PatchReal(refs, server, notes, DeviceOnePatches);
            foreach (string n in notes) output.WriteLine(n);

            using (var game = new GameContext(refs, patched, server))
            {
                Assembly engine = game.Get("Gameloft.Engine");
                Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.GameHooks", true);
                Type component = engine.GetType("Gameloft.Engine.Component", true);
                Type world = engine.GetType("Gameloft.Engine.World", true);
                Type gameObject = engine.GetType("Gameloft.Engine.GameObject", true);
                Type eventType = engine.GetType("Gameloft.Engine.ComponentEventType", true);

                object w = RuntimeHelpers.GetUninitializedObject(world);
                FieldInfo headless = world.GetField("<Headless>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                object go = RuntimeHelpers.GetUninitializedObject(gameObject);
                gameObject.GetField("world", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(go, w);
                Type concrete = engine.GetType("Gameloft.Engine.TemporaryCacheComponent", true);
                object c = RuntimeHelpers.GetUninitializedObject(concrete);
                FieldInfo goField = component.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single(f => f.FieldType == gameObject);
                goField.SetValue(c, go);

                Type actionType = typeof(Action<>).MakeGenericType(component);
                MethodInfo getSafe = component.GetNestedType("Events", BindingFlags.Public | BindingFlags.NonPublic)
                                              .GetMethod("GetSafeAction", BindingFlags.Static | BindingFlags.NonPublic);
                var probe = new Probe();
                Delegate Wrap(bool throws)
                {
                    probe.Throws = throws;
                    Delegate action = Delegate.CreateDelegate(actionType, probe, typeof(Probe).GetMethod("Run").MakeGenericMethod(component));
                    return (Delegate)getSafe.Invoke(null, new[] { action, Enum.Parse(eventType, "Start") });
                }

                headless.SetValue(w, true);
                Wrap(true).DynamicInvoke(c);   // swallowed
                Assert.Equal(1, probe.Calls);
                Assert.Equal(1, hooks.GetField("HeadlessCalls").GetValue(null));
                Assert.Equal("Gameloft.Engine.TemporaryCacheComponent", hooks.GetField("LastComponent").GetValue(null));
                Assert.Equal("Start", hooks.GetField("LastEvent").GetValue(null));
                Assert.IsType<NullReferenceException>(hooks.GetField("LastException").GetValue(null));

                headless.SetValue(w, false);
                var client = Assert.Throws<TargetInvocationException>(() => Wrap(true).DynamicInvoke(c));
                Assert.IsType<NullReferenceException>(client.InnerException);   // client Worlds: unchanged, fail fast
                Assert.Equal(2, probe.Calls);
                Assert.Equal(1, hooks.GetField("HeadlessCalls").GetValue(null));

                headless.SetValue(w, true);
                Wrap(false).DynamicInvoke(c);
                Assert.Equal(3, probe.Calls);
                Wrap(false).DynamicInvoke(new object[] { null });   // a null component goes straight to the action
                Assert.Equal(4, probe.Calls);
                object orphan = RuntimeHelpers.GetUninitializedObject(concrete);   // no GameObject: original behaviour
                Assert.IsType<NullReferenceException>(Assert.Throws<TargetInvocationException>(() => Wrap(true).DynamicInvoke(orphan)).InnerException);
                Assert.Equal(1, hooks.GetField("HeadlessCalls").GetValue(null));

                // Without McvsOffline.Server.dll the report still happens (stdout) and nothing escapes.
                using (var bare = new GameContext(refs, patched, null))
                {
                    Assembly e2 = bare.Get("Gameloft.Engine");
                    Type c2 = e2.GetType("Gameloft.Engine.Component", true);
                    Type w2t = e2.GetType("Gameloft.Engine.World", true);
                    Type go2t = e2.GetType("Gameloft.Engine.GameObject", true);
                    object w2 = RuntimeHelpers.GetUninitializedObject(w2t);
                    w2t.GetField("<Headless>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w2, true);
                    object go2 = RuntimeHelpers.GetUninitializedObject(go2t);
                    go2t.GetField("world", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(go2, w2);
                    object comp2 = RuntimeHelpers.GetUninitializedObject(e2.GetType("Gameloft.Engine.TemporaryCacheComponent", true));
                    c2.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single(f => f.FieldType == go2t).SetValue(comp2, go2);
                    Type a2 = typeof(Action<>).MakeGenericType(c2);
                    var p2 = new Probe { Throws = true };
                    Delegate action = Delegate.CreateDelegate(a2, p2, typeof(Probe).GetMethod("Run").MakeGenericMethod(c2));
                    Delegate wrapped = (Delegate)c2.GetNestedType("Events", BindingFlags.Public | BindingFlags.NonPublic)
                                                   .GetMethod("GetSafeAction", BindingFlags.Static | BindingFlags.NonPublic)
                                                   .Invoke(null, new[] { action, Enum.Parse(e2.GetType("Gameloft.Engine.ComponentEventType", true), "Update") });
                    wrapped.DynamicInvoke(comp2);
                    Assert.Equal(1, p2.Calls);
                }
            }
        }

        [Fact]
        public void NativeOnlineInitRunsOnlyWhenTheHookAllowsIt()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = FixtureCompiler.Compile("McvsOffline.Server", HookServerSource);
            Dictionary<string, byte[]> patched = PatchReal(refs, server, null, new NativeOnlinePatch());

            using (var game = new GameContext(refs, patched, server))
            {
                Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.GameHooks", true);
                MethodInfo init = game.Get("Gameloft.Engine").GetType("Gameloft.Engine.OnlinePlatformComponent", true)
                                      .GetMethod("Init_private", BindingFlags.Static | BindingFlags.NonPublic);
                init.Invoke(null, new object[] { "mcvs", "", "{}" });   // denied: returns without touching libnativeengine
                Assert.Equal(1, hooks.GetField("AllowCalls").GetValue(null));
                Assert.Equal("mcvs", hooks.GetField("LastDatacenter").GetValue(null));

                hooks.GetField("Allow").SetValue(null, true);
                var native = Assert.Throws<TargetInvocationException>(() => init.Invoke(null, new object[] { "mcvs", "", "{}" }));
                Assert.IsType<DllNotFoundException>(native.InnerException);   // allowed: the original P/Invoke runs
                Assert.Equal(2, hooks.GetField("AllowCalls").GetValue(null));
            }

            using (var bare = new GameContext(refs, patched, null))
            {
                // McvsOffline.Server.dll missing: the helper fails inside the try and the native call is skipped.
                bare.Get("Gameloft.Engine").GetType("Gameloft.Engine.OnlinePlatformComponent", true)
                    .GetMethod("Init_private", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { "mcvs", "", "{}" });
            }
        }

        [Fact]
        public void ClientOnlyComponentsAreDroppedAndHelpersAreNullSafe()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = FixtureCompiler.Compile("McvsOffline.Server", HookServerSource);
            Dictionary<string, byte[]> patched = PatchReal(refs, server, null, DeviceOnePatches);

            using (var game = new GameContext(refs, patched, server))
            {
                Assembly engine = game.Get("Gameloft.Engine");
                Assembly gameDll = game.Get("Game");
                Type keep = engine.GetType("Gameloft.Engine.KeepInHeadlessModeAttribute", true);
                bool? Keep(Type t)
                {
                    object a = t.GetCustomAttributes(false).FirstOrDefault(keep.IsInstanceOfType);
                    return a == null ? (bool?)null : (bool)keep.GetProperty("Keep").GetValue(a);
                }
                Assert.Equal(false, Keep(gameDll.GetType("Game.InputSettingsComponent", true)));
                Assert.Equal(false, Keep(engine.GetType("Gameloft.Engine.SoundGroupSnapshotComponent", true)));
                Assert.Null(Keep(engine.GetType("Gameloft.Engine.GameInputManagerComponent", true)));   // kept (cheats use it)
                Assert.Equal(false, Keep(engine.GetType("Gameloft.Engine.LocalStorageComponent", true)));
                Assert.Equal(true, Keep(gameDll.GetType("Game.GameplayComponent", true)));

                foreach (var (type, method) in new[]
                {
                    ("Game.GameplayComponent", "McvsBindingToActionPreset"), ("Game.GameplayComponent", "McvsBindingToDefaultPreset"),
                    ("Game.FlameZoneComponent", "McvsTriggerEnd"), ("Game.BacktrackTimeDistortionExplosion", "McvsTriggerEnd"),
                })
                {
                    gameDll.GetType(type, true).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { null });
                }

                // GameInputManagerComponent.OnUpdate: headless returns after base.OnUpdate(); a client World runs the original
                // body (which fails here on an uninitialised object: proof that it ran).
                Type component = engine.GetType("Gameloft.Engine.Component", true);
                Type world = engine.GetType("Gameloft.Engine.World", true);
                Type gameObject = engine.GetType("Gameloft.Engine.GameObject", true);
                object w = RuntimeHelpers.GetUninitializedObject(world);
                FieldInfo headless = world.GetField("<Headless>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                object go = RuntimeHelpers.GetUninitializedObject(gameObject);
                gameObject.GetField("world", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(go, w);
                Type input = engine.GetType("Gameloft.Engine.GameInputManagerComponent", true);
                object manager = RuntimeHelpers.GetUninitializedObject(input);
                component.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single(f => f.FieldType == gameObject).SetValue(manager, go);
                MethodInfo onUpdate = input.GetMethod("OnUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                headless.SetValue(w, true);
                onUpdate.Invoke(manager, null);
                headless.SetValue(w, false);
                Assert.Throws<TargetInvocationException>(() => onUpdate.Invoke(manager, null));
            }
        }

        [Fact]
        public void EveryPatchedMethodJits()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = FixtureCompiler.Compile("McvsOffline.Server", HookServerSource);
            Dictionary<string, byte[]> patched = PatchReal(refs, server, null, DefaultGamePatches);
            using (var game = new GameContext(refs, patched, server))
            {
                var sites = new[]
                {
                    ("Game", "Game.InputSettingsComponent", "OnStart"),
                    ("Game", "Game.GameplayComponent", "OnStart"), ("Game", "Game.GameplayComponent", "OnStop"),
                    ("Game", "Game.GameplayComponent", "Ui_OnPauseGameplay"), ("Game", "Game.GameplayComponent", "Ui_OnUnPauseGameplay"),
                    ("Game", "Game.GameplayComponent", "McvsBindingToActionPreset"), ("Game", "Game.GameplayComponent", "McvsBindingToDefaultPreset"),
                    ("Game", "Game.FlameZoneComponent", "BroadcastEnd"), ("Game", "Game.BacktrackTimeDistortionExplosion", "FadeOutEffect"),
                    ("Game", "Game.SoundManagerGameComponent", "OnStart"), ("Game", "Game.SoundManagerGameComponent", "OnUpdate"),
                    ("Game", "Game.SoundManagerGameComponent", "OnStop"),
                    ("Gameloft.Engine", "Gameloft.Engine.GameInputManagerComponent", "OnStart"),
                    ("Gameloft.Engine", "Gameloft.Engine.GameInputManagerComponent", "OnUpdate"),
                    ("Gameloft.Engine", "Gameloft.Engine.Component+Events", "GetSafeAction"),
                    ("Gameloft.Engine", "Gameloft.Engine.McvsHeadlessSafeAction", "Invoke"),
                    ("Gameloft.Engine", "Gameloft.Engine.McvsHeadlessSafeAction", "Report"),
                    ("Gameloft.Engine", "Gameloft.Engine.McvsHeadlessSafeAction", "Forward"),
                    ("Gameloft.Engine", "Gameloft.Engine.OnlinePlatformComponent", "Init_private"),
                    ("Gameloft.Engine", "Gameloft.Engine.OnlinePlatformComponent", "McvsAllowNativeOnline"),
                    ("Gameloft.Engine", "Gameloft.Engine.PhysicsComponent", ".ctor"),
                    ("Gameloft.Engine", "Gameloft.Engine.PhysicsComponent", "UpdateSimulationSteps"),
                    ("Gameloft.Engine", "Gameloft.Engine.McvsPhysics", "UseMultithreading"),
                    ("Gameloft.Engine", "Gameloft.Engine.McvsPhysics", "EnterStep"),
                    ("Gameloft.Engine", "Gameloft.Engine.McvsPhysics", "ExitStep"),
                    ("Gameloft.Engine", "Gameloft.Engine.McvsPhysics", "ForwardMultithreading"),
                    ("Gameloft.Engine", "Gameloft.Engine.McvsPhysics", "ForwardEnter"),
                    ("Gameloft.Engine", "Gameloft.Engine.McvsPhysics", "ForwardExit"),
                    ("Gameloft.Engine", "Gameloft.Engine.TaskExtension", "WaitAndWork"),
                    ("Game", "Game.KillSignatureRewardItem", "GetAvailableId"),
                    ("Game", "Game.GameUnifiedStoreComponent+<>c__DisplayClass25_0", "<SelectItems>b__8"),
                    ("Game", "Game.McvsStoreSafety", "Report"), ("Game", "Game.McvsStoreSafety", "Forward"),
                    ("Game", "Game.PlayerProfileComponent", "GetSignatureAssetsList"), ("Game", "Game.GameOfflineStore", "GetKillSignItems"),
                };
                foreach (var (assembly, type, name) in sites)
                {
                    Type t = game.Get(assembly).GetType(type, true);
                    const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                    MethodBase[] methods = name == ".ctor" ? t.GetConstructors(all).Cast<MethodBase>().ToArray()
                                                           : t.GetMethods(all).Where(m => m.Name == name).Cast<MethodBase>().ToArray();
                    Assert.NotEmpty(methods);
                    foreach (MethodBase m in methods) RuntimeHelpers.PrepareMethod(m.MethodHandle);   // InvalidProgramException on bad IL
                    output.WriteLine("JIT ok: " + type + "." + name);
                }
            }
        }

        [Fact]
        public void PatchesRefuseChangedOrAlreadyPatchedCode()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = FixtureCompiler.Compile("McvsOffline.Server", HookServerSource);
            Dictionary<string, byte[]> once = PatchReal(refs, server, null, DefaultGamePatches);
            string dir = TestEnvironment.NewTempDir("repatch");
            foreach (string f in Directory.GetFiles(refs, "*.dll")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            foreach (var kv in once) File.WriteAllBytes(Path.Combine(dir, kv.Key), kv.Value);
            Assert.Throws<PatchException>(() => PatchReal(dir, server, null, new HeadlessGamePatch()));
            Assert.Throws<PatchException>(() => PatchReal(dir, server, null, new HeadlessEnginePatch()));
            Assert.Throws<PatchException>(() => PatchReal(dir, server, null, new NativeOnlinePatch()));
            Assert.Throws<PatchException>(() => PatchReal(dir, server, null, new PhysicsPatch()));
            Assert.Contains("IL fingerprint", Assert.Throws<PatchException>(() => PatchReal(dir, server, null, new WaitsPatch())).Message);
            Assert.Throws<PatchException>(() => PatchReal(dir, server, null, new StorePatch()));
            Assert.Contains("IL fingerprint", Assert.Throws<PatchException>(() => PatchReal(dir, server, null, new SignaturesPatch())).Message);
            Assert.Contains("IL fingerprint", Assert.Throws<PatchException>(() => PatchReal(dir, server, null, new DebloatEnginePatch())).Message);

            // A different Init_private body (ldarg.2 -> ldarg.1) is refused, and so is a server without GameHooks.
            string changed = TestEnvironment.NewTempDir("changed");
            foreach (string f in Directory.GetFiles(refs, "*.dll")) File.Copy(f, Path.Combine(changed, Path.GetFileName(f)));
            using (var resolver = new DefaultAssemblyResolver())
            using (var module = ModuleDefinition.ReadModule(Path.Combine(refs, "Gameloft.Engine.dll"), new ReaderParameters { InMemory = true, AssemblyResolver = resolver }))
            {
                resolver.AddSearchDirectory(refs);
                MethodDefinition init = module.GetType("Gameloft.Engine.OnlinePlatformComponent").Methods.Single(m => m.Name == "Init_private");
                init.Body.Instructions[2].OpCode = OpCodes.Ldarg_1;
                module.Write(Path.Combine(changed, "Gameloft.Engine.dll"));
            }
            Assert.Contains("does not match", Assert.Throws<PatchException>(() => PatchReal(changed, server, null, new NativeOnlinePatch())).Message);

            // One changed instruction anywhere in a fingerprinted body is refused (here: the step loop's ldarg.2 -> ldarg.1).
            string changedSteps = TestEnvironment.NewTempDir("changed-steps");
            foreach (string f in Directory.GetFiles(refs, "*.dll")) File.Copy(f, Path.Combine(changedSteps, Path.GetFileName(f)));
            using (var resolver = new DefaultAssemblyResolver())
            using (var module = ModuleDefinition.ReadModule(Path.Combine(refs, "Gameloft.Engine.dll"), new ReaderParameters { InMemory = true, AssemblyResolver = resolver }))
            {
                resolver.AddSearchDirectory(refs);
                MethodDefinition steps = module.GetType("Gameloft.Engine.PhysicsComponent").Methods.Single(m => m.Name == "UpdateSimulationSteps");
                steps.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldarg_2).OpCode = OpCodes.Ldarg_1;
                module.Write(Path.Combine(changedSteps, "Gameloft.Engine.dll"));
            }
            Assert.Contains("IL fingerprint", Assert.Throws<PatchException>(() => PatchReal(changedSteps, server, null, new PhysicsPatch())).Message);
            byte[] noHooks = FixtureCompiler.Compile("McvsOffline.Server", "namespace McvsOffline { public static class Bootstrap { public static void Init() { } } }");
            Assert.Contains("GameHooks", Assert.Throws<PatchException>(() => PatchReal(refs, noHooks, null, new NativeOnlinePatch())).Message);
        }

        [Fact]
        public void TheRealGameHooksHaveThePatchedSignatures()
        {
            Type hooks = typeof(McvsOffline.GameHooks);
            MethodInfo allow = hooks.GetMethod(nameof(McvsOffline.GameHooks.AllowNativeOnlineInit), new[] { typeof(string) });
            Assert.True(allow.IsStatic && allow.IsPublic && allow.ReturnType == typeof(bool));
            MethodInfo headless = hooks.GetMethod(nameof(McvsOffline.GameHooks.HeadlessComponentException), new[] { typeof(string), typeof(string), typeof(Exception) });
            Assert.True(headless.IsStatic && headless.IsPublic && headless.ReturnType == typeof(void));

            McvsOffline.GameHooks.Configure(false, null, null);
            Assert.False(McvsOffline.GameHooks.AllowNativeOnlineInit("mcvs"));
            McvsOffline.GameHooks.Configure(true, null, null);
            Assert.True(McvsOffline.GameHooks.AllowNativeOnlineInit("mcvs"));
            McvsOffline.GameHooks.Configure(false, null, null);
            McvsOffline.GameHooks.HeadlessComponentException("X", "Start", new InvalidOperationException("probe"));   // never throws

            MethodInfo mt = hooks.GetMethod(nameof(McvsOffline.GameHooks.PhysicsMultithreading), new[] { typeof(bool), typeof(bool) });
            Assert.True(mt.IsStatic && mt.IsPublic && mt.ReturnType == typeof(bool));
            MethodInfo enter = hooks.GetMethod(nameof(McvsOffline.GameHooks.EnterPhysicsStep), Type.EmptyTypes);
            Assert.True(enter.IsStatic && enter.IsPublic && enter.ReturnType == typeof(bool));
            MethodInfo exit = hooks.GetMethod(nameof(McvsOffline.GameHooks.ExitPhysicsStep), Type.EmptyTypes);
            Assert.True(exit.IsStatic && exit.IsPublic && exit.ReturnType == typeof(void));
        }

        public sealed class Probe
        {
            public int Calls;
            public bool Throws;

            public void Run<T>(T component)
            {
                Calls++;
                if (Throws) throw new NullReferenceException("probe");
            }
        }

        /// <summary>The game's assemblies from MCVS_REFS, the patched images first, the stand-in server; the BCL from net8.</summary>
        internal sealed class GameContext : AssemblyLoadContext, IDisposable
        {
            private readonly string directory;
            private readonly Dictionary<string, byte[]> images;

            public GameContext(string directory, Dictionary<string, byte[]> patched, byte[] server) : base("mcvs-real-patch-" + Guid.NewGuid().ToString("N"), true)
            {
                this.directory = directory;
                images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var kv in patched) images[Path.GetFileNameWithoutExtension(kv.Key)] = kv.Value;
                if (server != null) images["McvsOffline.Server"] = server;
            }

            public Assembly Get(string name)
            {
                return LoadFromAssemblyName(new AssemblyName(name));
            }

            protected override Assembly Load(AssemblyName name)
            {
                string simple = name.Name;
                byte[] image;
                if (images.TryGetValue(simple, out image)) return LoadFromStream(new MemoryStream(image));
                if (simple == "McvsOffline.Server") return null;
                if (simple == "mscorlib" || simple == "netstandard" || simple == "System" || simple.StartsWith("System.", StringComparison.Ordinal) ||
                    simple.StartsWith("Microsoft.", StringComparison.Ordinal))
                    return null;
                string path = Path.Combine(directory, simple + ".dll");
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }

            public void Dispose()
            {
                Unload();
            }
        }
    }
}
