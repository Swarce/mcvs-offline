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
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// Every method the full PC plan changes or adds JITs (docs/design/pc-port.md §12.4, as RealGamePatchTests.EveryPatchedMethodJits):
    /// the patched assemblies are loaded on net8 with the stand-in server and <c>RuntimeHelpers.PrepareMethod</c> runs on each
    /// method whose IL fingerprint differs from the original's or that is new (the helper types <c>EngineBridge.Class1::McvsOfflineInit</c>,
    /// <c>McvsQuickLaunch</c>, <c>McvsWorldDiagnostics</c>, <c>McvsGraphics</c>, the headless/store/physics/… helpers), so bad IL is an
    /// InvalidProgramException here and not in the game.
    /// </summary>
    public class RealPcJitTests
    {
        private static readonly BuildProfile Pc = BuildProfile.PcSteam6372049;

        private readonly ITestOutputHelper output;

        public RealPcJitTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void EveryPatchedPcMethodJits()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, server, null, Pc, RealPc.FullPlan().ToArray());
            if (Environment.GetEnvironmentVariable("MCVS_DUMP_PATCHED") is string dump) foreach (var kv in patched) File.WriteAllBytes(Path.Combine(dump, kv.Key), kv.Value);
            var changed = new Dictionary<string, List<(int Token, string Name)>>(StringComparer.Ordinal);
            foreach (var kv in patched)
            {
                Dictionary<string, string> before = Fingerprints(File.ReadAllBytes(Path.Combine(pc, kv.Key)));
                var list = new List<(int, string)>();
                using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(kv.Value)))
                {
                    foreach (TypeDefinition type in RealPc.AllTypes(module.Types))
                    {
                        foreach (MethodDefinition m in type.Methods.Where(m => m.HasBody))
                        {
                            string old;
                            if (before.TryGetValue(Key(m), out old) && old == IlPatch.Fingerprint(m)) continue;
                            if (m.HasGenericParameters || type.HasGenericParameters) throw new InvalidOperationException("a generic patched method: " + m.FullName);
                            list.Add((m.MetadataToken.ToInt32(), m.FullName));
                        }
                    }
                }
                changed[Path.GetFileNameWithoutExtension(kv.Key)] = list;
            }

            using (var game = new RealGamePatchTests.GameContext(pc, patched, server))
            {
                int count = 0;
                foreach (var kv in changed)
                {
                    Module module = game.Get(kv.Key).ManifestModule;
                    foreach (var (token, name) in kv.Value)
                    {
                        MethodBase method = module.ResolveMethod(token);
                        try
                        {
                            RuntimeHelpers.PrepareMethod(method.MethodHandle);   // InvalidProgramException on bad IL
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException("JIT failed: " + name, ex);
                        }
                        output.WriteLine("JIT ok: " + name);
                        count++;
                    }
                }
                // Expected at least: hook-pc 2, quicklaunch 3 + 4 helpers, headless-diag 2 + 4, headless 13 + 2, graphics 1 + 4, ...
                Assert.True(count >= 40, "only " + count + " changed methods found");
                foreach (string helper in new[] { "Gameloft.Engine.McvsQuickLaunch", "Gameloft.Engine.McvsWorldDiagnostics", "Gameloft.Engine.McvsGraphics" })
                    Assert.Contains(changed["Gameloft.Engine"], c => c.Name.Contains(helper + "::"));
                Assert.Contains(changed["EngineBridge"], c => c.Name.Contains("EngineBridge.Class1::" + EngineBridgeHookPatch.HelperName));
                Assert.Contains(changed["Game"], c => c.Name.Contains("Game.McvsStoreSafety::"));
            }
        }

        /// <summary>The full name plus the generic arity (overloads that differ only by it share a full name).</summary>
        private static string Key(MethodDefinition m)
        {
            return m.FullName + " `" + m.GenericParameters.Count;
        }

        private static Dictionary<string, string> Fingerprints(byte[] image)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(image)))
            {
                foreach (TypeDefinition type in RealPc.AllTypes(module.Types))
                    foreach (MethodDefinition m in type.Methods.Where(m => m.HasBody)) result[Key(m)] = IlPatch.Fingerprint(m);
            }
            return result;
        }
    }
}
