using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;
using GameContext = McvsOffline.Embed.Tests.Patcher.RealGamePatchTests.GameContext;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// PC patch 'fpslimit' (build 27) on the player's REAL Steam Game.dll (MCVS_PC_REFS): the two calls that give WCPToolkit's pacer
    /// its frame rate limit (<c>GamePreferencesComponent.LoadPreferencesWindows</c> and <c>OnPreferencesChangedWindows</c> "FPS")
    /// pass the value through <c>Game.McvsFrameRate.Limit</c> → <c>GraphicsHooks.FrameRateLimit</c>. Skipped without the Steam files
    /// unless MCVS_REQUIRE_PC=1.
    /// </summary>
    public class RealFpsLimitPatchTests
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly BuildProfile Pc = BuildProfile.PcSteam6372049;

        private readonly ITestOutputHelper output;

        public RealFpsLimitPatchTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static ModuleDefinition Read(byte[] image, ReaderParameters reader)
        {
            return ModuleDefinition.ReadModule(new MemoryStream(image), reader);
        }

        private static MethodDefinition Load(ModuleDefinition module)
        {
            return module.GetType(FpsLimitPatch.TypeName).Methods.Single(m => m.Name == "LoadPreferencesWindows");
        }

        private static MethodDefinition Changed(ModuleDefinition module)
        {
            return module.GetType(FpsLimitPatch.TypeName).Methods.Single(m => m.Name == "OnPreferencesChangedWindows");
        }

        private static List<string> SetterCallers(ModuleDefinition module)
        {
            return RealPc.AllTypes(module.Types).SelectMany(t => t.Methods).Where(m => m.HasBody)
                         .SelectMany(m => m.Body.Instructions.Where(i => i.Operand is MethodReference r && r.Name == FpsLimitPatch.Setter &&
                                                                         r.DeclaringType.FullName == FpsLimitPatch.SetterOwner).Select(i => m.FullName))
                         .ToList();
        }

        [Fact]
        public void TheShippedGameSetsItsLimitAtExactlyTheTwoVerifiedSites()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            ReaderParameters reader = RealPc.Reader(pc, null);
            using (reader.AssemblyResolver)
            using (ModuleDefinition module = Read(File.ReadAllBytes(Path.Combine(pc, "Game.dll")), reader))
            {
                output.WriteLine("LoadPreferencesWindows " + IlPatch.Fingerprint(Load(module)));
                output.WriteLine("OnPreferencesChangedWindows " + IlPatch.Fingerprint(Changed(module)));
                Assert.Equal(FpsLimitPatch.PcLoadFingerprint, IlPatch.Fingerprint(Load(module)));
                Assert.Equal(FpsLimitPatch.PcChangedFingerprint, IlPatch.Fingerprint(Changed(module)));
                Assert.Equal(new[] { Load(module).FullName, Changed(module).FullName }.OrderBy(n => n, StringComparer.Ordinal),
                             SetterCallers(module).OrderBy(n => n, StringComparer.Ordinal));
                // The values: the local the start value is in, and the FPS property of the FPS LIMIT row.
                Assert.Equal("IL_0977: ldloc.0", Load(module).Body.Instructions.Single(i => i.Offset == 0x977).ToString());
                Assert.Equal("IL_0179: call System.Single Game.GamePreferencesComponent::get_FPS()",
                             Changed(module).Body.Instructions.Single(i => i.Offset == 0x179).ToString());
            }

            // What the install sees: patch 'headless' (mandatory, earlier in the plan) has already put its H8 guard into the first.
            Dictionary<string, byte[]> headless = RealPc.Patch(pc, PcHookServer.Compile(), null, Pc, new HeadlessGamePatch());
            ReaderParameters reader2 = RealPc.Reader(pc, PcHookServer.WriteToTempFolder());
            using (reader2.AssemblyResolver)
            using (ModuleDefinition module = Read(headless["Game.dll"], reader2))
            {
                output.WriteLine("LoadPreferencesWindows after headless " + IlPatch.Fingerprint(Load(module)));
                Assert.Equal(FpsLimitPatch.PcLoadAfterHeadlessFingerprint, IlPatch.Fingerprint(Load(module)));
                Assert.Equal(FpsLimitPatch.PcChangedFingerprint, IlPatch.Fingerprint(Changed(module)));
            }
        }

        private static string Text(Instruction i)
        {
            return i.OpCode + (i.Operand is MethodReference m ? " " + m.DeclaringType.Name + "::" + m.Name : i.Operand is VariableDefinition v ? " V_" + v.Index : "");
        }

        /// <summary>One call to the helper right before each setter call, after the shipped value; nothing else differs.</summary>
        private static void AssertEachSiteFeedsTheHelper(ModuleDefinition patched, ModuleDefinition original)
        {
            Assert.Equal(2, SetterCallers(patched).Count);
            foreach (var pair in new[] { (Load(patched), Load(original), "ldloc.0"), (Changed(patched), Changed(original), "call GamePreferencesComponent::get_FPS") })
            {
                var code = pair.Item1.Body.Instructions;
                Instruction call = Assert.Single(code, i => i.Operand is MethodReference m && m.Name == "Limit");
                Assert.Equal("System.Single Game.McvsFrameRate::Limit(System.Single)", ((MethodReference)call.Operand).FullName);
                int at = code.IndexOf(call);
                Assert.Equal(new[] { "call GameApplication::get_Instance" }, new[] { Text(code[at - (pair.Item3 == "ldloc.0" ? 2 : 3)]) });
                Assert.Equal(pair.Item3, Text(code[at - 1]));
                Assert.Equal("callvirt GameApplication::set_WCPFramerateLimit", Text(code[at + 1]));
                var before = pair.Item2.Body.Instructions.Select(Text).ToList();
                var after = code.Select(Text).ToList();
                after.RemoveAt(at);
                Assert.Equal(before, after);
                Assert.Equal(pair.Item2.Body.ExceptionHandlers.Count, pair.Item1.Body.ExceptionHandlers.Count);
            }

            TypeDefinition helper = patched.GetType(FpsLimitPatch.HelperNamespace + "." + FpsLimitPatch.HelperType);
            Assert.True(helper.IsNotPublic && helper.IsAbstract && helper.IsSealed);
            Assert.Equal(new[] { "Forward", "Limit" }, helper.Methods.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal("hooksMissing", Assert.Single(helper.Fields).Name);
            MethodDefinition forward = helper.Methods.Single(m => m.Name == "Forward");
            Assert.True(forward.IsPrivate && forward.ImplAttributes.HasFlag(Mono.Cecil.MethodImplAttributes.NoInlining));
            Assert.Contains(forward.Body.Instructions, i => i.Operand is MethodReference m &&
                            m.FullName == "System.Single McvsOffline.GraphicsHooks::FrameRateLimit(System.Single)");
            Assert.Equal("System.Object", Assert.Single(helper.Methods.Single(m => m.Name == "Limit").Body.ExceptionHandlers).CatchType.FullName);
        }

        [Fact]
        public void EachSitePassesItsValueThroughTheHelperAloneAndInTheFullPlan()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            var notes = new List<string>();
            Dictionary<string, byte[]> alone = RealPc.Patch(pc, server, notes, Pc, new FpsLimitPatch());
            foreach (string n in notes) output.WriteLine(n);
            Assert.Equal(new[] { "Game.dll" }, alone.Keys);
            Assert.Contains(notes, n => n.Contains("McvsFrameRate.Limit") && n.Contains("graphics.pcFpsLimit"));
            Assert.Equal(2, notes.Count);
            // The default plan (headless first, fpslimit last): the same two insertions.
            Assert.Equal("fpslimit", PatchPlan.For(Pc, PatchPlan.Ids(Pc).Default).Last().Id);
            Dictionary<string, byte[]> plan = RealPc.Patch(pc, server, null, Pc, PatchPlan.For(Pc, PatchPlan.Ids(Pc).Default).Where(p => p.AssemblyFile == "Game.dll").ToArray());
            Dictionary<string, byte[]> headlessOnly = RealPc.Patch(pc, server, null, Pc, new HeadlessGamePatch());
            ReaderParameters reader = RealPc.Reader(pc, PcHookServer.WriteToTempFolder());
            using (reader.AssemblyResolver)
            {
                using (ModuleDefinition module = Read(alone["Game.dll"], reader))
                using (ModuleDefinition original = Read(File.ReadAllBytes(Path.Combine(pc, "Game.dll")), reader))
                {
                    TestEnvironment.AssertPcCorlib(module);
                    AssertEachSiteFeedsTheHelper(module, original);
                }
                using (ModuleDefinition module = Read(plan["Game.dll"], reader))
                using (ModuleDefinition withHeadless = Read(headlessOnly["Game.dll"], reader))
                {
                    AssertEachSiteFeedsTheHelper(module, withHeadless);
                }
            }
        }

        [Fact]
        public void APatchedFileAnOldServerAndTheAndroidBuildAreRefused()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            Dictionary<string, byte[]> once = RealPc.Patch(pc, server, null, Pc, new FpsLimitPatch());
            string dir = RealPc.CopyWith(pc, once);
            Assert.Contains("already patched", Assert.Throws<PatchException>(() => RealPc.Patch(dir, server, null, Pc, new FpsLimitPatch())).Message);
            Directory.Delete(dir, true);
            // A server without the hook is refused before anything changes.
            byte[] old = PcHookServer.Compile(PcHookServer.Source.Replace("FrameRateLimit(float", "FrameRateLimitOld(float"));
            Assert.Contains("FrameRateLimit", Assert.Throws<PatchException>(() => RealPc.Patch(pc, old, null, Pc, new FpsLimitPatch())).Message);
            // Android: an id of the other build (neither site exists there).
            Assert.Contains("fpslimit", PatchPlan.Ids(BuildProfile.Android1_17_32).Excluded);
            Assert.DoesNotContain("fpslimit", McvsOffline.Patcher.PatcherOptions.OptionalPatches);
        }

        private static (MethodInfo Limit, Type Helper) Helper(GameContext game)
        {
            Type helper = game.Get("Game").GetType(FpsLimitPatch.HelperNamespace + "." + FpsLimitPatch.HelperType, true);
            return (helper.GetMethod("Limit", All), helper);
        }

        private static List<string> Calls(GameContext game)
        {
            return (List<string>)game.Get("McvsOffline.Server").GetType("McvsOffline.HookLog", true).GetField("Calls").GetValue(null);
        }

        private static List<object[]> Arguments(GameContext game)
        {
            return (List<object[]>)game.Get("McvsOffline.Server").GetType("McvsOffline.HookLog", true).GetField("Arguments").GetValue(null);
        }

        /// <summary>
        /// The FPS LIMIT row's change on an uninitialised component whose FPS is <paramref name="fps"/>: the shipped and the patched
        /// method both end in a NullReferenceException at <c>GameApplication.Instance</c> (null without the native engine), so the
        /// hook call that comes first is the proof of the order.
        /// </summary>
        private static string ChangeFps(GameContext game, float fps)
        {
            Type type = game.Get("Game").GetType(FpsLimitPatch.TypeName, true);
            object component = RuntimeHelpers.GetUninitializedObject(type);
            type.GetProperty("FPS", All).SetValue(component, fps);
            try
            {
                type.GetMethod("OnPreferencesChangedWindows", All).Invoke(component, new object[] { component, type.GetProperty("FPS", All) });
                return "returned";
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException.GetType().FullName;
            }
        }

        [Fact]
        public void TheRowsValueReachesTheHookBeforeTheSetterAndTheHookDecides()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, server, null, Pc, new FpsLimitPatch());
            using (var original = new GameContext(pc, new Dictionary<string, byte[]>(), server))
            using (var game = new GameContext(pc, patched, server))
            {
                // Shipped: straight to the (null) application, no hook.
                Assert.Equal(typeof(NullReferenceException).FullName, ChangeFps(original, 120f));
                Assert.Empty(Calls(original));
                // Patched: the hook gets the row's value first, then the same setter call.
                Assert.Equal(typeof(NullReferenceException).FullName, ChangeFps(game, 120f));
                Assert.Equal(new[] { "GraphicsHooks.FrameRateLimit" }, Calls(game));
                Assert.Equal(120f, Arguments(game).Last()[0]);

                var (limit, helper) = Helper(game);
                Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.GraphicsHooks", true);
                // NaN answer = echo: the game's value bit for bit.
                foreach (float value in new[] { 30f, 60f, 90f, 120f })
                    Assert.Equal(value, (float)limit.Invoke(null, new object[] { value }));
                hooks.GetField("FrameRateLimitAnswer").SetValue(null, 1000f);
                Assert.Equal(1000f, (float)limit.Invoke(null, new object[] { 60f }));
                // A hook that throws: the game's value, and the latch keeps it from calling again.
                hooks.GetField("FrameRateLimitThrows").SetValue(null, true);
                int calls = Calls(game).Count;
                Assert.Equal(60f, (float)limit.Invoke(null, new object[] { 60f }));
                Assert.True((bool)helper.GetField("hooksMissing", All).GetValue(null));
                hooks.GetField("FrameRateLimitThrows").SetValue(null, false);
                Assert.Equal(90f, (float)limit.Invoke(null, new object[] { 90f }));
                Assert.Equal(calls + 1, Calls(game).Count);
            }
            using (var bare = new GameContext(pc, patched, null))
            {
                // McvsOffline.Server.dll missing: the game's value, one failed bind, then the latch.
                var (limit, helper) = Helper(bare);
                Assert.Equal(60f, (float)limit.Invoke(null, new object[] { 60f }));
                Assert.True((bool)helper.GetField("hooksMissing", All).GetValue(null));
                Assert.Equal(typeof(NullReferenceException).FullName, ChangeFps(bare, 30f));
            }
        }
    }
}
