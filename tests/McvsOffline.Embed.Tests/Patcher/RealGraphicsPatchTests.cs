using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    /// Patch 'graphics' (docs/research/pc-textures.md WP0) on the player's REAL Gameloft.Engine.dll (MCVS_REFS), executed on net8:
    /// <c>GameOptionPerformanceProfileComponent.OnCreate</c> passes the GameOptions_android.json text through
    /// <c>GraphicsHooks.GameOptionsJson</c> and reports <c>m_options</c> after <c>ApplyGameOptions</c>; every failure leaves the text as
    /// read. Skipped without the game's assemblies unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    public class RealGraphicsPatchTests
    {
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly ITestOutputHelper output;

        public RealGraphicsPatchTests(ITestOutputHelper output)
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
        public void TheShippedOnCreateHasTheVerifiedFingerprint()
        {
            string refs = Refs();
            if (refs == null) return;
            using (var module = ModuleDefinition.ReadModule(Path.Combine(refs, "Gameloft.Engine.dll")))
            {
                MethodDefinition onCreate = module.GetType(GraphicsPatch.TypeName).Methods.Single(m => m.Name == "OnCreate");
                Assert.Equal(GraphicsPatch.OnCreateFingerprint, IlPatch.Fingerprint(onCreate));
                // The component the GameDefinition uses (Game.GameGameOptionsComponent) inherits OnCreate: the patch covers it.
                Assert.Null(module.GetType("Gameloft.Engine.GameGameOptionsComponent"));
            }
            using (var game = ModuleDefinition.ReadModule(Path.Combine(refs, "Game.dll")))
            {
                TypeDefinition options = game.GetType("Game.GameGameOptionsComponent");
                Assert.Equal(GraphicsPatch.TypeName, options.BaseType.FullName);
                Assert.DoesNotContain(options.Methods, m => m.Name == "OnCreate");
            }
        }

        [Fact]
        public void OnCreateGetsTwoCallsAndNothingElseChanges()
        {
            string refs = Refs();
            if (refs == null) return;
            var notes = new List<string>();
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, Server(), notes, new GraphicsPatch());
            foreach (string n in notes) output.WriteLine(n);
            Assert.Single(notes);
            Assert.Contains("GraphicsHooks.GameOptionsJson", notes[0]);
            using (var original = ModuleDefinition.ReadModule(Path.Combine(refs, "Gameloft.Engine.dll")))
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(patched["Gameloft.Engine.dll"])))
            {
                var before = original.GetType(GraphicsPatch.TypeName).Methods.Single(m => m.Name == "OnCreate").Body.Instructions;
                var code = module.GetType(GraphicsPatch.TypeName).Methods.Single(m => m.Name == "OnCreate").Body.Instructions;
                Assert.Equal(before.Count + 3, code.Count);
                int read = Enumerable.Range(0, code.Count).Single(i => code[i].Operand is MethodReference r && r.Name == "ReadAllText");
                Assert.Equal("System.String Gameloft.Engine.McvsGraphics::GameOptionsJson(System.String)", ((MethodReference)code[read + 1].Operand).FullName);
                Assert.Equal(OpCodes.Stloc_0, code[read + 2].OpCode);
                Assert.Equal(OpCodes.Ret, code[code.Count - 1].OpCode);
                Assert.Equal("System.Void Gameloft.Engine.McvsGraphics::OptionsApplied(Gameloft.Engine.GameOptionPerformanceProfileComponent)",
                             ((MethodReference)code[code.Count - 2].Operand).FullName);
                Assert.Equal(OpCodes.Ldarg_0, code[code.Count - 3].OpCode);
                Assert.Equal("ApplyGameOptions", ((MethodReference)code[code.Count - 4].Operand).Name);
                // Every original instruction is still there, in order, with its operand.
                var originalOps = before.Select(i => i.OpCode.Code + " " + (i.Operand is MethodReference r ? r.FullName : i.Operand is Instruction ? "" : i.Operand)).ToList();
                var kept = code.Where(i => !(i.Operand is MethodReference r && r.DeclaringType.Name == GraphicsPatch.HelperType))
                               .Select(i => i.OpCode.Code + " " + (i.Operand is MethodReference r ? r.FullName : i.Operand is Instruction ? "" : i.Operand)).ToList();
                kept.RemoveAt(kept.Count - 2);   // the ldarg.0 of OptionsApplied
                Assert.Equal(originalOps, kept);
            }
        }

        [Fact]
        public void TheHelpersForwardAndFallBackToTheTextAsRead()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = Server();
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, null, new GraphicsPatch());
            using (var game = new GameContext(refs, patched, server))
            {
                Assembly engine = game.Get("Gameloft.Engine");
                Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.GraphicsHooks", true);
                Type helper = engine.GetType("Gameloft.Engine.McvsGraphics", true);
                foreach (MethodInfo m in helper.GetMethods(Any | BindingFlags.DeclaredOnly)) RuntimeHelpers.PrepareMethod(m.MethodHandle);
                MethodInfo json = helper.GetMethod("GameOptionsJson", Any);
                MethodInfo applied = helper.GetMethod("OptionsApplied", Any);

                hooks.GetField("JsonAnswer").SetValue(null, "{\"rewritten\":1}");
                Assert.Equal("{\"rewritten\":1}", json.Invoke(null, new object[] { "{\"original\":1}" }));
                Assert.Equal("{\"original\":1}", hooks.GetField("LastJson").GetValue(null));
                hooks.GetField("JsonAnswer").SetValue(null, null);
                Assert.Equal("{\"original\":1}", json.Invoke(null, new object[] { "{\"original\":1}" }));   // null answer: the text as read
                hooks.GetField("JsonThrows").SetValue(null, true);
                Assert.Equal("{\"original\":1}", json.Invoke(null, new object[] { "{\"original\":1}" }));   // a throwing hook: the text as read
                Assert.Equal(3, hooks.GetField("JsonCalls").GetValue(null));

                Type componentType = engine.GetType(GraphicsPatch.TypeName, true);
                object component = RuntimeHelpers.GetUninitializedObject(componentType);
                var options = new Dictionary<string, object> { ["addShaderDefine"] = "HIGH_END" };
                componentType.GetField("m_options").SetValue(component, options);
                applied.Invoke(null, new[] { component });
                Assert.Same(options, hooks.GetField("LastOptions").GetValue(null));
                applied.Invoke(null, new object[] { null });
                Assert.Null(hooks.GetField("LastOptions").GetValue(null));
                Assert.Equal(2, hooks.GetField("AppliedCalls").GetValue(null));
            }

            // Without McvsOffline.Server.dll: the forwarders fail to compile inside the helpers' try, the text goes through unchanged.
            using (var bare = new GameContext(refs, patched, null))
            {
                Type helper = bare.Get("Gameloft.Engine").GetType("Gameloft.Engine.McvsGraphics", true);
                Assert.Equal("{\"original\":1}", helper.GetMethod("GameOptionsJson", Any).Invoke(null, new object[] { "{\"original\":1}" }));
                helper.GetMethod("OptionsApplied", Any).Invoke(null, new object[] { null });
            }
        }

        [Fact]
        public void TheRealHooksRewriteThroughThePatchedHelper()
        {
            string refs = Refs();
            if (refs == null) return;
            string dll = TestEnvironment.EmbeddedServerDll;
            byte[] server = dll != null ? File.ReadAllBytes(dll) : File.ReadAllBytes(typeof(McvsOffline.GraphicsHooks).Assembly.Location);
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, null, new GraphicsPatch());
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(patched["Gameloft.Engine.dll"])))
            {
                var imports = module.GetMemberReferences().OfType<MethodReference>().Where(m => m.DeclaringType.FullName == GraphicsPatch.HooksType)
                                    .Select(m => m.ReturnType.FullName + " " + m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) + ")")
                                    .OrderBy(s => s, StringComparer.Ordinal).ToList();
                Assert.Equal(new[] { "System.String GameOptionsJson(System.String)", "System.Void GameOptionsApplied(System.Object)" }, imports);
            }
        }
    }
}
