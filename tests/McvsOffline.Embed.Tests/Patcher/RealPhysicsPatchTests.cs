using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;
using GameContext = McvsOffline.Embed.Tests.Patcher.RealGamePatchTests.GameContext;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// The device-test-2 patches applied to the player's REAL Gameloft.Engine.dll (MCVS_REFS) and executed on net8:
    /// 'physics' (headless Worlds get single-threaded Havok; every Havok step inside a process-wide gate) and 'waits' (the
    /// pumping WaitAndWork helpers re-check every 100 ms). Skipped without the game's assemblies unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    [Collection("GameHooks")]
    public class RealPhysicsPatchTests
    {
        private readonly ITestOutputHelper output;

        public RealPhysicsPatchTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private string Refs()
        {
            string dir = TestEnvironment.GameAssemblies;
            if (dir != null && File.Exists(Path.Combine(dir, "Gameloft.Engine.dll"))) return dir;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but MCVS_REFS has no Gameloft.Engine.dll");
            output.WriteLine("the game's assemblies are not available (MCVS_REFS): skipped");
            return null;
        }

        private static byte[] Server()
        {
            return FixtureCompiler.Compile("McvsOffline.Server", RealGamePatchTests.HookServerSource);
        }

        [Fact]
        public void TheShippedMethodsHaveTheVerifiedFingerprints()
        {
            string refs = Refs();
            if (refs == null) return;
            using (var module = ModuleDefinition.ReadModule(Path.Combine(refs, "Gameloft.Engine.dll")))
            {
                TypeDefinition physics = module.GetType("Gameloft.Engine.PhysicsComponent");
                TypeDefinition extension = module.GetType("Gameloft.Engine.TaskExtension");
                Assert.Equal(PhysicsPatch.CtorFingerprint, IlPatch.Fingerprint(physics.Methods.Single(m => m.IsConstructor && !m.IsStatic)));
                Assert.Equal(PhysicsPatch.StepsFingerprint, IlPatch.Fingerprint(physics.Methods.Single(m => m.Name == "UpdateSimulationSteps")));
                var waits = extension.Methods.Where(m => m.Name == "WaitAndWork").ToDictionary(m => m.Parameters[0].ParameterType.Name);
                Assert.Equal(WaitsPatch.TaskFingerprint, IlPatch.Fingerprint(waits["Task"]));
                Assert.Equal(WaitsPatch.SignalFingerprint, IlPatch.Fingerprint(waits["ManualResetEventSlim"]));
            }
        }

        [Fact]
        public void TheConstructorAsksTheHelperInsteadOfTheGameOption()
        {
            string refs = Refs();
            if (refs == null) return;
            var notes = new List<string>();
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, Server(), notes, new PhysicsPatch());
            foreach (string n in notes) output.WriteLine(n);
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(patched["Gameloft.Engine.dll"])))
            {
                MethodDefinition ctor = module.GetType("Gameloft.Engine.PhysicsComponent").Methods.Single(m => m.IsConstructor && !m.IsStatic);
                var code = ctor.Body.Instructions;
                int site = Enumerable.Range(0, code.Count).Single(i => code[i].Operand is MethodReference r && r.Name == "ConstructNative");
                // ldarg.0 (this, for ConstructNative); ldarg.0; call McvsPhysics::UseMultithreading(PhysicsComponent); callvirt ConstructNative(bool)
                Assert.Equal(OpCodes.Ldarg_0, code[site - 3].OpCode);
                Assert.Equal(OpCodes.Ldarg_0, code[site - 2].OpCode);
                var helper = (MethodReference)code[site - 1].Operand;
                Assert.Equal("System.Boolean Gameloft.Engine.McvsPhysics::UseMultithreading(Gameloft.Engine.PhysicsComponent)", helper.FullName);
                Assert.True(IsCall(code[site - 4], "Gameloft.Engine.Component", ".ctor"));   // after the base constructor: GameObject is set
                Assert.DoesNotContain(code, i => i.Operand is MethodReference r && r.Name == "get_PhysicsMultithreaded");
                Assert.Equal(104, code.Count);   // one instruction more than the original 103, nothing else touched

                MethodDefinition steps = module.GetType("Gameloft.Engine.PhysicsComponent").Methods.Single(m => m.Name == "UpdateSimulationSteps");
                Assert.Single(steps.Body.ExceptionHandlers);
                Assert.Equal(ExceptionHandlerType.Finally, steps.Body.ExceptionHandlers[0].HandlerType);
                Assert.Single(steps.Body.Instructions, i => i.Operand is MethodReference r && r.Name == "UpdateStep_private");
            }
        }

        [Fact]
        public void ServerWorldsAskForSingleThreadedHavok()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = Server();
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, null, new PhysicsPatch());
            using (var game = new GameContext(refs, patched, server))
            {
                Assembly engine = game.Get("Gameloft.Engine");
                Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.GameHooks", true);
                var probe = new PhysicsProbe(engine);
                MethodInfo use = engine.GetType("Gameloft.Engine.McvsPhysics", true).GetMethod("UseMultithreading", BindingFlags.Static | BindingFlags.NonPublic);

                // The IL hands the game's option and World.Headless to GameHooks and returns its answer.
                probe.SetOption(true);
                hooks.GetField("MtAnswer").SetValue(null, false);
                Assert.False((bool)use.Invoke(null, new[] { probe.Component(headless: true) }));
                Assert.Equal(1, hooks.GetField("MtCalls").GetValue(null));
                Assert.True((bool)hooks.GetField("LastHeadless").GetValue(null));
                Assert.True((bool)hooks.GetField("LastRequested").GetValue(null));

                hooks.GetField("MtAnswer").SetValue(null, true);
                Assert.True((bool)use.Invoke(null, new[] { probe.Component(headless: false) }));
                Assert.False((bool)hooks.GetField("LastHeadless").GetValue(null));

                probe.SetOption(false);
                use.Invoke(null, new[] { probe.Component(headless: true) });
                Assert.False((bool)hooks.GetField("LastRequested").GetValue(null));

                // No component, or one without a GameObject (World null): counted as a client World, never a crash.
                use.Invoke(null, new object[] { null });
                Assert.False((bool)hooks.GetField("LastHeadless").GetValue(null));
                use.Invoke(null, new[] { RuntimeHelpers.GetUninitializedObject(probe.PhysicsType) });
                Assert.False((bool)hooks.GetField("LastHeadless").GetValue(null));
                Assert.Equal(5, hooks.GetField("MtCalls").GetValue(null));
            }

            // Without McvsOffline.Server.dll the IL decides alone: multithreaded only when the game asks for it AND the World is a client.
            using (var bare = new GameContext(refs, patched, null))
            {
                Assembly engine = bare.Get("Gameloft.Engine");
                var probe = new PhysicsProbe(engine);
                MethodInfo use = engine.GetType("Gameloft.Engine.McvsPhysics", true).GetMethod("UseMultithreading", BindingFlags.Static | BindingFlags.NonPublic);
                probe.SetOption(true);
                Assert.False((bool)use.Invoke(null, new[] { probe.Component(headless: true) }));
                Assert.True((bool)use.Invoke(null, new[] { probe.Component(headless: false) }));
                Assert.True((bool)use.Invoke(null, new object[] { null }));
                probe.SetOption(false);
                Assert.False((bool)use.Invoke(null, new[] { probe.Component(headless: false) }));
            }
        }

        [Fact]
        public void EveryHavokStepRunsInsideTheGate()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = Server();
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, null, new PhysicsPatch());
            using (var game = new GameContext(refs, patched, server))
            {
                Assembly engine = game.Get("Gameloft.Engine");
                Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.GameHooks", true);
                var probe = new PhysicsProbe(engine);
                MethodInfo steps = probe.PhysicsType.GetMethod("UpdateSimulationSteps", BindingFlags.Instance | BindingFlags.NonPublic);
                object physics = probe.Component(headless: false);

                steps.Invoke(physics, new object[] { 0, 0.016f });
                Assert.Equal(1, hooks.GetField("EnterCalls").GetValue(null));
                Assert.Equal(1, hooks.GetField("ExitCalls").GetValue(null));

                // A step that throws (no native world here: UpdateStep_private refuses a disposed component) still releases the gate.
                var thrown = Assert.Throws<TargetInvocationException>(() => steps.Invoke(physics, new object[] { 2, 0.016f }));
                Assert.IsType<ObjectDisposedException>(thrown.InnerException);
                Assert.Equal(2, hooks.GetField("EnterCalls").GetValue(null));
                Assert.Equal(2, hooks.GetField("ExitCalls").GetValue(null));

                // Gate not taken (off, or timed out): no Exit.
                hooks.GetField("GateAnswer").SetValue(null, false);
                steps.Invoke(physics, new object[] { 0, 0.016f });
                Assert.Equal(3, hooks.GetField("EnterCalls").GetValue(null));
                Assert.Equal(2, hooks.GetField("ExitCalls").GetValue(null));
            }
            using (var bare = new GameContext(refs, patched, null))
            {
                // Without McvsOffline.Server.dll: no gate, the loop runs as before.
                var probe = new PhysicsProbe(bare.Get("Gameloft.Engine"));
                probe.PhysicsType.GetMethod("UpdateSimulationSteps", BindingFlags.Instance | BindingFlags.NonPublic)
                     .Invoke(probe.Component(headless: false), new object[] { 0, 0.016f });
            }
        }

        [Fact]
        public void PumpingWaitsPickUpWorkThatArrivesWithoutAWakeUp()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = Server();
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, null, new WaitsPatch());

            // The patched helper: an item queued with DelayedPost (no WorkItemAdded) completes the task it waits for.
            using (var game = new GameContext(refs, patched, server))
            {
                var waits = new WaitProbe(game.Get("Gameloft.Engine"));
                Assert.True(waits.WaitOnTask(TimeSpan.FromSeconds(5), wakeAfterMs: -1), "WaitAndWork(Task, ExecuteContexts) missed a DelayedPost item");
                Assert.True(waits.WaitOnSignal(TimeSpan.FromSeconds(5), wakeAfterMs: -1), "WaitAndWork(ManualResetEventSlim, ExecuteContexts) missed a DelayedPost item");
            }

            // The original helper sleeps until something raises WorkItemAdded (here a Post after 1.5 s): the lost wake-up the patch closes.
            using (var original = new GameContext(refs, new Dictionary<string, byte[]>(), null))
            {
                var waits = new WaitProbe(original.Get("Gameloft.Engine"));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Assert.True(waits.WaitOnTask(TimeSpan.FromSeconds(10), wakeAfterMs: 1500));
                Assert.True(watch.ElapsedMilliseconds >= 1400, "the unpatched wait returned after " + watch.ElapsedMilliseconds + " ms");
            }
        }

        private static bool IsCall(Instruction i, string type, string name)
        {
            return (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference m && m.Name == name && m.DeclaringType.FullName == type;
        }

        /// <summary>Uninitialised PhysicsComponents on a fake GameObject/World (no native engine on net8).</summary>
        private sealed class PhysicsProbe
        {
            private readonly Type component, world, gameObject;

            public PhysicsProbe(Assembly engine)
            {
                PhysicsType = engine.GetType("Gameloft.Engine.PhysicsComponent", true);
                component = engine.GetType("Gameloft.Engine.Component", true);
                world = engine.GetType("Gameloft.Engine.World", true);
                gameObject = engine.GetType("Gameloft.Engine.GameObject", true);
            }

            public Type PhysicsType { get; }

            public void SetOption(bool multithreaded)
            {
                PhysicsType.GetProperty("PhysicsMultithreaded", BindingFlags.Static | BindingFlags.Public).SetValue(null, multithreaded);
            }

            public object Component(bool headless)
            {
                object w = RuntimeHelpers.GetUninitializedObject(world);
                world.GetField("<Headless>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, headless);
                object go = RuntimeHelpers.GetUninitializedObject(gameObject);
                gameObject.GetField("world", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(go, w);
                object c = RuntimeHelpers.GetUninitializedObject(PhysicsType);
                component.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single(f => f.FieldType == gameObject).SetValue(c, go);
                return c;
            }
        }

        /// <summary>
        /// Runs <c>TaskExtension.WaitAndWork(x, ExecuteContexts)</c> on a thread that owns a GameSynchronizationContext, while
        /// another thread queues the completing item with DelayedPost (which raises no WorkItemAdded) and, optionally, later posts
        /// a no-op with Post (which does).
        /// </summary>
        private sealed class WaitProbe
        {
            private readonly Type context, extension, options;

            public WaitProbe(Assembly engine)
            {
                context = engine.GetType("Gameloft.Engine.GameSynchronizationContext", true);
                extension = engine.GetType("Gameloft.Engine.TaskExtension", true);
                options = engine.GetType("Gameloft.Engine.WaitAndWorkOptions", true);
            }

            public bool WaitOnTask(TimeSpan limit, int wakeAfterMs)
            {
                var tcs = new TaskCompletionSource<bool>();
                return Run(limit, wakeAfterMs, () => tcs.TrySetResult(true), tcs.Task, typeof(Task));
            }

            public bool WaitOnSignal(TimeSpan limit, int wakeAfterMs)
            {
                var signal = new ManualResetEventSlim(false);
                return Run(limit, wakeAfterMs, () => signal.Set(), signal, typeof(ManualResetEventSlim));
            }

            private bool Run(TimeSpan limit, int wakeAfterMs, Action complete, object target, Type targetType)
            {
                object ctx = null;
                var ready = new ManualResetEventSlim(false);
                Exception failure = null;
                var waiter = new Thread(() =>
                {
                    try
                    {
                        ctx = Activator.CreateInstance(context);   // registers to this thread: WaitAndWork pumps it
                        ready.Set();
                        MethodInfo wait = extension.GetMethod("WaitAndWork", new[] { targetType, options });
                        wait.Invoke(null, new[] { target, Enum.Parse(options, "ExecuteContexts") });
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        ready.Set();
                    }
                });
                waiter.IsBackground = true;
                waiter.Start();
                Assert.True(ready.Wait(5000));
                Thread.Sleep(200);   // the waiter is asleep in WaitAny by now
                SendOrPostCallback callback = _ => complete();
                context.GetMethod("DelayedPost").Invoke(ctx, new object[] { callback, null });
                if (wakeAfterMs >= 0)
                {
                    Thread.Sleep(wakeAfterMs);
                    SendOrPostCallback noop = _ => { };
                    context.GetMethod("Post").Invoke(ctx, new object[] { noop, null });
                }
                bool done = waiter.Join(limit);
                if (failure != null) throw new InvalidOperationException("WaitAndWork failed", failure);
                return done;
            }
        }
    }
}
