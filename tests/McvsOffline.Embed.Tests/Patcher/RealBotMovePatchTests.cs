using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using McvsOffline.Embed.Tests.Patcher.Pc;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;
using GameContext = McvsOffline.Embed.Tests.Patcher.RealGamePatchTests.GameContext;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// Patch 'botmove' (build 27) on the player's REAL Game.dll: Android (MCVS_REFS, executed on net8) and Steam (MCVS_PC_REFS,
    /// the IL). <c>BT_Action_MoveTo.adaptSpeed</c>'s first step from rest is 3*dt and <c>moveToVector</c> stops a bot whose speed
    /// is not above 0.05, so at the PC's 60 FPS limit (frames of about 16.57 ms) a resting bot never started. Skipped without the
    /// game's assemblies unless MCVS_REQUIRE_REAL_CLIENT=1 (Android) / MCVS_REQUIRE_PC=1 (Steam).
    /// </summary>
    [Collection("GameHooks")]
    public class RealBotMovePatchTests
    {
        /// <summary>The PC's frame period at FRAME RATE LIMIT 60: WCPToolkit's pacer ends each frame about 0.1 ms early.</summary>
        private const float PcFrame = 0.016566f;

        private readonly ITestOutputHelper output;

        public RealBotMovePatchTests(ITestOutputHelper output)
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

        private static byte[] Server()
        {
            return FixtureCompiler.Compile("McvsOffline.Server", RealGamePatchTests.HookServerSource);
        }

        private static MethodDefinition AdaptSpeed(ModuleDefinition module)
        {
            return module.GetType(BotMovePatch.TypeName).Methods.Single(m => m.Name == "adaptSpeed");
        }

        [Fact]
        public void TheRampIsTheSameOnBothBuilds()
        {
            string refs = Refs();
            if (refs == null) return;
            using (var module = ModuleDefinition.ReadModule(Path.Combine(refs, "Game.dll")))
            {
                string android = IlPatch.Fingerprint(AdaptSpeed(module));
                output.WriteLine("Android adaptSpeed " + android);
                Assert.Equal(BotMovePatch.AdaptSpeedFingerprint, android);
            }
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            ReaderParameters reader = RealPc.Reader(pc, null);
            using (reader.AssemblyResolver)
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(Path.Combine(pc, "Game.dll"))), reader))
            {
                string steam = IlPatch.Fingerprint(AdaptSpeed(module));
                output.WriteLine("Steam adaptSpeed " + steam);
                Assert.Equal(BotMovePatch.AdaptSpeedFingerprint, steam);
            }
        }

        /// <summary>
        /// The patched body: the six inserted instructions right after the ramp's <c>ldsfld acceleration; mul; add; stloc.0</c> and
        /// before the clamp's <c>ldloc.0; ldc.r4 1</c>; one call to the helper in the whole method (the decel branch has none);
        /// everything else as shipped.
        /// </summary>
        private static void AssertTheRampFeedsTheHelperOnce(ModuleDefinition patched, ModuleDefinition original)
        {
            MethodDefinition adaptSpeed = AdaptSpeed(patched);
            var code = adaptSpeed.Body.Instructions;
            Instruction call = Assert.Single(code, i => i.Operand is MethodReference m && m.Name == "Ramp");
            Assert.Equal("System.Single Gameloft.Sequences.McvsBotMove::Ramp(System.Single,System.Single,System.Single)", ((MethodReference)call.Operand).FullName);
            int at = code.IndexOf(call);
            string Text(Instruction i) => i.OpCode + (i.Operand is VariableDefinition v ? " V_" + v.Index : i.Operand is MethodReference m ? " " + m.Name : i.Operand is FieldReference f ? " " + f.Name : i.Operand is float x ? " " + x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "");
            Assert.Equal(new[]
            {
                "ldsfld acceleration", "mul", "add", "stloc.0",
                "ldloc.0", "ldloc.s V_11", "ldloca.s V_10", "call get_Seconds", "call Ramp", "stloc.0",
                "ldloc.0", "ldc.r4 1",
            }, Enumerable.Range(at - 8, 12).Select(i => Text(code[i])));
            MethodDefinition before = AdaptSpeed(original);
            Assert.Equal(before.Body.Instructions.Count + 6, code.Count);
            Assert.Equal(before.Body.Instructions.Take(at - 4).Select(Text), code.Take(at - 4).Select(Text));
            Assert.Equal(before.Body.Instructions.Skip(at - 4).Select(Text), code.Skip(at + 2).Select(Text));
            Assert.False(adaptSpeed.Body.HasExceptionHandlers);

            TypeDefinition helper = patched.GetType(BotMovePatch.HelperNamespace + "." + BotMovePatch.HelperType);
            Assert.True(helper.IsNotPublic && helper.IsAbstract && helper.IsSealed);
            Assert.Equal(new[] { "Forward", "Ramp" }, helper.Methods.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal("hooksMissing", Assert.Single(helper.Fields).Name);
            MethodDefinition forward = helper.Methods.Single(m => m.Name == "Forward");
            Assert.True(forward.IsPrivate && forward.ImplAttributes.HasFlag(Mono.Cecil.MethodImplAttributes.NoInlining));
            Assert.Contains(forward.Body.Instructions, i => i.Operand is MethodReference m &&
                            m.FullName == "System.Single McvsOffline.GameHooks::BotMoveRamp(System.Single,System.Single,System.Single)");
            Assert.Equal("System.Object", Assert.Single(helper.Methods.Single(m => m.Name == "Ramp").Body.ExceptionHandlers).CatchType.FullName);
        }

        [Fact]
        public void TheRampFeedsTheHelperOnce()
        {
            string refs = Refs();
            if (refs == null) return;
            var notes = new List<string>();
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, Server(), notes, new BotMovePatch());
            foreach (string n in notes) output.WriteLine(n);
            Assert.Equal(new[] { "Game.dll" }, patched.Keys);
            Assert.Contains(notes, n => n.Contains("McvsBotMove.Ramp") && n.Contains("embedded.bots.moveFromRest"));
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(patched["Game.dll"])))
            using (var original = ModuleDefinition.ReadModule(Path.Combine(refs, "Game.dll")))
            {
                AssertTheRampFeedsTheHelperOnce(module, original);
            }

            // The Steam Game.dll, as the PC installer patches it.
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            Dictionary<string, byte[]> steam = RealPc.Patch(pc, PcHookServer.Compile(), null, BuildProfile.PcSteam6372049, new BotMovePatch());
            ReaderParameters reader = RealPc.Reader(pc, PcHookServer.WriteToTempFolder());
            using (reader.AssemblyResolver)
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(steam["Game.dll"]), reader))
            using (var original = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(Path.Combine(pc, "Game.dll"))), reader))
            {
                TestEnvironment.AssertPcCorlib(module);
                AssertTheRampFeedsTheHelperOnce(module, original);
            }
        }

        [Fact]
        public void APatchedGameDllIsRefusedAndTheAndroidDefaultHasNoBotMove()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = Server();
            Dictionary<string, byte[]> once = RealGamePatchTests.PatchReal(refs, server, null, new BotMovePatch());
            string dir = TestEnvironment.NewTempDir("botmove-repatch");
            foreach (string f in Directory.GetFiles(refs, "*.dll")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            File.WriteAllBytes(Path.Combine(dir, "Game.dll"), once["Game.dll"]);
            Assert.Contains("already patched", Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(dir, server, null, new BotMovePatch())).Message);
            Directory.Delete(dir, true);
            // A server without the hook is refused before anything changes.
            byte[] old = FixtureCompiler.Compile("McvsOffline.Server", RealGamePatchTests.HookServerSource.Replace("BotMoveRamp(", "BotMoveRampOld("));
            Assert.Contains("BotMoveRamp", Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(refs, old, null, new BotMovePatch())).Message);

            // The default APK selects no 'botmove' (its Game.dll stays as the patcher made it before build 27); --patch botmove does.
            Assert.DoesNotContain(PatchPlan.For(BuildProfile.Android1_17_32, McvsOffline.Patcher.PatcherOptions.DefaultPatches), p => p is BotMovePatch);
            Assert.Contains(PatchPlan.For(BuildProfile.Android1_17_32, new[] { "botmove" }), p => p is BotMovePatch);
        }

        private static (MethodInfo Ramp, float Acceleration, Type Helper) Load(GameContext game)
        {
            Assembly gameDll = game.Get("Game");
            Type helper = gameDll.GetType(BotMovePatch.HelperNamespace + "." + BotMovePatch.HelperType, true);
            MethodInfo ramp = helper.GetMethod("Ramp", BindingFlags.Static | BindingFlags.NonPublic);
            float acceleration = (float)gameDll.GetType(BotMovePatch.TypeName, true).GetField("acceleration", BindingFlags.Static | BindingFlags.Public).GetValue(null);
            RuntimeHelpers.PrepareMethod(gameDll.GetType(BotMovePatch.TypeName, true).GetMethod("adaptSpeed", BindingFlags.Instance | BindingFlags.NonPublic).MethodHandle);
            return (ramp, acceleration, helper);
        }

        /// <summary>The game's own first step from rest, in float32 as adaptSpeed computes it: Sqrt(0) + dt * acceleration.</summary>
        private static float FirstStep(float dt, float acceleration)
        {
            return (float)Math.Sqrt(0f) + dt * acceleration;
        }

        [Fact]
        public void ARestingBotStartsAtThePcFramePeriod()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, Server(), null, new BotMovePatch());
            using (var bare = new GameContext(refs, patched, null))   // no McvsOffline.Server.dll: the IL decides alone
            {
                var (ramp, acceleration, _) = Load(bare);
                Assert.Equal(3f, acceleration);
                foreach (float dt in new[] { PcFrame, 1f / 120f, 1f / 90f, 0.0166f })
                {
                    float game = FirstStep(dt, acceleration);
                    Assert.True(game <= BotMovePatch.StartThreshold, "the game's own step at " + dt + " s is " + game + ": not a blocked start");
                    float step = (float)ramp.Invoke(null, new object[] { game, 0f, dt });
                    output.WriteLine("dt " + dt + ": game " + game.ToString("R") + " -> " + step.ToString("R"));
                    Assert.True(step > BotMovePatch.StartThreshold, "a resting bot stays put at dt " + dt + " (step " + step + ")");
                    Assert.Equal(BotMovePatch.FirstStep, step);
                }
            }
        }

        /// <summary>
        /// <c>adaptSpeed()</c> itself on an uninitialised <c>BT_Action_MoveTo</c> (no native engine on net8): a bot at rest (a
        /// <c>CharacterInputComponent</c> whose movement actions read 0), <c>forceDefaultSpeed</c> off, no deceleration (<c>decelDistance</c> -1),
        /// <c>Speed</c> 1, and a World whose frame lasted <paramref name="dt"/>.
        /// </summary>
        private static float AdaptSpeed(Assembly gameDll, Assembly engine, float dt)
        {
            Type moveToType = gameDll.GetType(BotMovePatch.TypeName, true);
            object moveTo = RuntimeHelpers.GetUninitializedObject(moveToType);
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            moveToType.GetField("forceDefaultSpeed", instance).SetValue(moveTo, false);
            moveToType.GetField("decelDistance", instance).SetValue(moveTo, -1f);
            moveToType.GetProperty("Speed", instance).SetValue(moveTo, 1f);
            Type worldType = engine.GetType("Gameloft.Engine.World", true);
            object world = RuntimeHelpers.GetUninitializedObject(worldType);
            worldType.GetField("time", instance).SetValue(world, (double)dt);
            moveToType.GetField("world", instance).SetValue(moveTo, world);
            Type blackBoardType = gameDll.GetType("Game.BlackBoardComponent", true);
            object blackBoard = RuntimeHelpers.GetUninitializedObject(blackBoardType);
            // A component at rest: its four movement actions hold the default InputState (Value 0); the Writer is created on first use.
            Type inputType = gameDll.GetType("Game.CharacterInputComponent", true);
            object input = RuntimeHelpers.GetUninitializedObject(inputType);
            Type actionType = engine.GetType("Gameloft.Engine.InputAction", true);
            foreach (string axis in new[] { "Forward", "Back", "StrafeLeft", "StrafeRight" })
                inputType.GetField(axis, instance).SetValue(input, RuntimeHelpers.GetUninitializedObject(actionType));
            blackBoardType.GetProperty("CharacterInput", instance).SetValue(blackBoard, input);
            moveToType.GetField("BB", instance).SetValue(moveTo, blackBoard);
            try
            {
                return (float)moveToType.GetMethod("adaptSpeed", instance).Invoke(moveTo, null);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException;
            }
        }

        [Fact]
        public void TheGamesOwnRampNowStartsARestingBot()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, Server(), null, new BotMovePatch());
            using (var original = new GameContext(refs, new Dictionary<string, byte[]>(), null))
            using (var bare = new GameContext(refs, patched, null))
            {
                Assembly stockGame = original.Get("Game"), stockEngine = original.Get("Gameloft.Engine");
                Assembly game = bare.Get("Game"), engine = bare.Get("Gameloft.Engine");
                // The bug, in the shipped code: at the PC's 60 FPS frame (and at 120 frames/s) the first step is not above 0.05.
                Assert.Equal(0.049698003f, AdaptSpeed(stockGame, stockEngine, PcFrame));
                Assert.True(AdaptSpeed(stockGame, stockEngine, 1f / 120f) <= BotMovePatch.StartThreshold);
                // Patched: the bot starts.
                Assert.Equal(BotMovePatch.FirstStep, AdaptSpeed(game, engine, PcFrame));
                Assert.Equal(BotMovePatch.FirstStep, AdaptSpeed(game, engine, 1f / 120f));
                // Frames of 1/60 s or longer: the shipped value, bit for bit.
                foreach (float dt in new[] { 1f / 60f, 0.025f, 1f / 30f, 0.1f })
                {
                    float stock = AdaptSpeed(stockGame, stockEngine, dt);
                    Assert.True(stock > BotMovePatch.StartThreshold);
                    Assert.Equal(BitConverter.SingleToInt32Bits(stock), BitConverter.SingleToInt32Bits(AdaptSpeed(game, engine, dt)));
                }
            }
        }

        [Fact]
        public void FramesOfASixtiethOrLongerAreUnchanged()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = Server();
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, null, new BotMovePatch());
            using (var bare = new GameContext(refs, patched, null))
            {
                var (ramp, acceleration, _) = Load(bare);
                foreach (float dt in new[] { 1f / 60f, 0.02f, 0.025f, 1f / 30f })
                {
                    foreach (float prevSq in new[] { 0f, 0.25f, 0.81f })
                    {
                        float game = (float)Math.Sqrt(prevSq) + dt * acceleration;
                        Assert.True(game > BotMovePatch.StartThreshold);
                        float step = (float)ramp.Invoke(null, new object[] { game, prevSq, dt });
                        Assert.Equal(BitConverter.SingleToInt32Bits(game), BitConverter.SingleToInt32Bits(step));
                    }
                }
            }
        }

        [Fact]
        public void IlVerifyAcceptsThePatchedMethods()
        {
            string refs = Refs();
            if (refs == null) return;
            RealAdminPagePatchTests.AssertIlVerifyClean(output, refs, "Game.dll", new IlPatch[] { new BotMovePatch() },
                                                        new[] { @"\]Gameloft\.Sequences\.BT_Action_MoveTo\.adaptSpeed$" }, 1,
                                                        "BT_Action_MoveTo::adaptSpeed", "McvsBotMove::Ramp", "McvsBotMove::Forward");
        }

        [Fact]
        public void TheHooksDecideWhenPresent()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = Server();
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, null, new BotMovePatch());
            using (var game = new GameContext(refs, patched, server))
            {
                var (ramp, _, helper) = Load(game);
                Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.GameHooks", true);

                // The hook's answer wins, both ways, with the exact arguments.
                hooks.GetField("BotEcho").SetValue(null, false);
                hooks.GetField("BotAnswer").SetValue(null, 0.0497f);   // the A/B run: the game's own step
                Assert.Equal(0.0497f, (float)ramp.Invoke(null, new object[] { 0.0497f, 0f, PcFrame }));
                Assert.Equal(1, hooks.GetField("BotCalls").GetValue(null));
                Assert.Equal(0.0497f, hooks.GetField("LastRamped").GetValue(null));
                Assert.Equal(0f, hooks.GetField("LastPrevSq").GetValue(null));
                Assert.Equal(PcFrame, hooks.GetField("LastDt").GetValue(null));
                hooks.GetField("BotAnswer").SetValue(null, 0.75f);
                Assert.Equal(0.75f, (float)ramp.Invoke(null, new object[] { 0.6f, 0.3025f, 0.02f }));
                Assert.Equal(0.3025f, hooks.GetField("LastPrevSq").GetValue(null));
                Assert.Equal(2, hooks.GetField("BotCalls").GetValue(null));

                // A hook that throws: the IL's own answer, and the latch keeps it from calling again.
                hooks.GetField("BotThrows").SetValue(null, true);
                Assert.Equal(BotMovePatch.FirstStep, (float)ramp.Invoke(null, new object[] { 0.0497f, 0f, PcFrame }));
                Assert.Equal(3, hooks.GetField("BotCalls").GetValue(null));
                Assert.True((bool)helper.GetField("hooksMissing", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
                hooks.GetField("BotThrows").SetValue(null, false);
                hooks.GetField("BotEcho").SetValue(null, true);
                Assert.Equal(0.6f, (float)ramp.Invoke(null, new object[] { 0.6f, 0.3025f, 0.02f }));
                Assert.Equal(3, hooks.GetField("BotCalls").GetValue(null));
            }
            using (var bare = new GameContext(refs, patched, null))
            {
                // McvsOffline.Server.dll missing: one failed bind, then the latch.
                var (ramp, _, helper) = Load(bare);
                Assert.False((bool)helper.GetField("hooksMissing", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
                Assert.Equal(BotMovePatch.FirstStep, (float)ramp.Invoke(null, new object[] { 0f, 0f, 0f }));
                Assert.True((bool)helper.GetField("hooksMissing", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
                Assert.Equal(float.NaN, (float)ramp.Invoke(null, new object[] { float.NaN, 0f, 0f }));   // NaN is never raised
            }
        }
    }
}
