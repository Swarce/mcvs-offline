using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// The patch contract between the PC patches and McvsOffline.Server.dll (docs/design/pc-port.md §3.2, §3.9, §12.4): every server
    /// member the full PC plan calls resolves in the net452 stand-in (<see cref="PcHookServer"/>) and in the real PC server DLL of
    /// this test assembly's configuration (<see cref="TestEnvironment.PcServerDll"/>; skipped while it is missing, a failure under
    /// MCVS_REQUIRE_PC=1), and the witness names of §3.9 are the patches' constants and the literals in GamePatchProbe (WP2) and
    /// PcInstaller (WP3).
    /// </summary>
    public class PcHookContractTests
    {
        private static readonly BuildProfile Pc = BuildProfile.PcSteam6372049;

        private readonly ITestOutputHelper output;

        public PcHookContractTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>The server members the PC patches import (type, name, return, parameters), as §3.2 lists them.</summary>
        internal static readonly string[] ExpectedImports =
        {
            "System.Void McvsOffline.Bootstrap::Init()",
            "System.Void McvsOffline.GameHooks::HeadlessComponentException(System.String,System.String,System.Exception)",
            "System.Boolean McvsOffline.GameHooks::PhysicsMultithreading(System.Boolean,System.Boolean)",
            "System.Boolean McvsOffline.GameHooks::EnterPhysicsStep()",
            "System.Void McvsOffline.GameHooks::ExitPhysicsStep()",
            "System.Boolean McvsOffline.GameHooks::AllowNativeOnlineInit(System.String)",
            "System.Void McvsOffline.GameHooks::StoreItemException(System.Object,System.Exception)",
            "System.Collections.Generic.IEnumerable`1<System.Object> McvsOffline.AdminHooks::AccountMenuButtons(System.Collections.Generic.IEnumerable`1<System.Object>)",
            "System.Void McvsOffline.AdminHooks::OpenPage(System.String,System.Object)",
            "System.Boolean McvsOffline.AdminHooks::OpenIfPageMarker(System.String,System.Object)",
            "System.String McvsOffline.GraphicsHooks::GameOptionsJson(System.String)",
            "System.Void McvsOffline.GraphicsHooks::GameOptionsApplied(System.Object)",
            "System.Threading.Tasks.Task McvsOffline.PcHooks::QuickLaunch(System.Object,System.Object,System.Threading.CancellationToken)",
            "System.Void McvsOffline.PcHooks::DefaultAutomatchHost(System.Object)",
            "System.Void McvsOffline.PcHooks::ServerWorldFailed(System.Exception)",
            "System.Void McvsOffline.PcHooks::ComponentCreateFailed(System.Exception,System.Type)",
        };

        [Fact]
        public void EveryServerMemberThePcPlanCallsResolvesInTheStandIn()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            List<string> imports = Imports(pc, PcHookServer.Compile());
            foreach (string i in imports) output.WriteLine(i);
            Assert.Equal(ExpectedImports.OrderBy(i => i, StringComparer.Ordinal), imports);
        }

        [Fact]
        public void EveryServerMemberThePcPlanCallsResolvesInTheRealPcServer()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            string dll = TestEnvironment.PcServerDll;
            if (dll == null)
            {
                Assert.False(TestEnvironment.RequirePc, "MCVS_REQUIRE_PC=1 but the PC server DLL (artifacts/bin/McvsOffline.Server.Pc/<config>/) was not built");
                output.WriteLine("the PC server DLL was not built (scripts/build.sh): skipped");
                return;
            }
            output.WriteLine("PC server: " + dll);
            // ImportServerMethod checks every member (public static, exact signature) while the plan applies.
            List<string> imports = Imports(pc, File.ReadAllBytes(dll));
            Assert.Equal(ExpectedImports.OrderBy(i => i, StringComparer.Ordinal), imports);
            using (ModuleDefinition server = ModuleDefinition.ReadModule(dll))
            {
                Assert.Equal("McvsOffline.Server", server.Assembly.Name.Name);
                AssemblyNameReference corlib = server.AssemblyReferences.Single(r => r.Name == "mscorlib");
                Assert.Equal(new Version(4, 0, 0, 0), corlib.Version);
            }
        }

        /// <summary>The distinct server members the patched assemblies reference, after the full PC plan with <paramref name="server"/>.</summary>
        private static List<string> Imports(string pc, byte[] server)
        {
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, server, null, Pc, RealPc.FullPlan().ToArray());
            var result = new SortedSet<string>(StringComparer.Ordinal);
            foreach (byte[] image in patched.Values)
            {
                using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(image)))
                {
                    foreach (MemberReference m in module.GetMemberReferences())
                        if (m.DeclaringType.Scope.Name == "McvsOffline.Server") result.Add(m.FullName);
                }
            }
            return result.ToList();
        }

        /// <summary>§3.9: the witness constants, and the copies WP2 (GamePatchProbe) and WP3 (PcInstaller) look them up by.</summary>
        [Fact]
        public void TheWitnessNamesAreTheDesignsAndTheirCopiesAgree()
        {
            Assert.Equal("McvsOfflineInit", EngineBridgeHookPatch.HelperName);
            Assert.Equal(BootstrapHookPatch.HelperName, EngineBridgeHookPatch.HelperName);
            Assert.Equal("EngineBridge.Class1", EngineBridgeHookPatch.TypeName);
            Assert.Equal("Gameloft.Engine.McvsQuickLaunch", QuickLaunchPatch.HelperNamespace + "." + QuickLaunchPatch.HelperType);
            Assert.Equal("Gameloft.Engine.McvsWorldDiagnostics", HeadlessDiagnosticsPatch.HelperNamespace + "." + HeadlessDiagnosticsPatch.HelperType);
            Assert.Equal("McvsGraphics", GraphicsPatch.HelperType);
            Assert.Equal("McvsStoreSafety", StorePatch.SafetyType);
            Assert.Equal("Game." + StorePatch.SafetyType, McvsOffline.Embedded.GamePatchProbe.StoreSafetyType);

            // The copies, compared by value through reflection (a copy spelled differently still compares). GamePatchProbe's
            // constants come with WP2 and PcInstaller.Witnesses with WP3: on a branch that has neither yet, the comparison is
            // reported and skipped; as soon as one of them is there, every copy must be there and equal.
            var expected = new Dictionary<string, (string File, string Type, string Member)>(StringComparer.Ordinal)
            {
                ["hook-pc"] = (new EngineBridgeHookPatch().AssemblyFile, EngineBridgeHookPatch.TypeName, EngineBridgeHookPatch.HelperName),
                ["quicklaunch"] = (new QuickLaunchPatch().AssemblyFile, QuickLaunchPatch.HelperNamespace + "." + QuickLaunchPatch.HelperType, null),
                ["headless-diag"] = (new HeadlessDiagnosticsPatch().AssemblyFile, HeadlessDiagnosticsPatch.HelperNamespace + "." + HeadlessDiagnosticsPatch.HelperType, null),
                ["graphics"] = (new GraphicsPatch().AssemblyFile, "Gameloft.Engine." + GraphicsPatch.HelperType, null),
                ["store"] = (new StorePatch().AssemblyFile, "Game." + StorePatch.SafetyType, null),
            };
            var probeCopy = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HookPcType"] = expected["hook-pc"].Type,
                ["HookPcMethod"] = expected["hook-pc"].Member,
                ["QuickLaunchType"] = expected["quicklaunch"].Type,
                ["WorldDiagnosticsType"] = expected["headless-diag"].Type,
                ["GraphicsType"] = expected["graphics"].Type,
            };
            Type probe = typeof(McvsOffline.Embedded.GamePatchProbe);
            Type installer = typeof(McvsOffline.Patcher.Pc.PcInstaller);
            Dictionary<string, string> probeValues = probeCopy.Keys.ToDictionary(k => k, k => ConstantOrNull(probe, k), StringComparer.Ordinal);
            MemberInfo witnessTable = (MemberInfo)installer.GetField("Witnesses", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) ??
                                      installer.GetProperty("Witnesses", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (witnessTable == null && probeValues.Values.All(v => v == null))
            {
                output.WriteLine("neither GamePatchProbe's PC constants (WP2) nor PcInstaller.Witnesses (WP3) are on this branch: the copies are not compared");
                return;
            }

            foreach (var kv in probeCopy) Assert.True(kv.Value == probeValues[kv.Key], "GamePatchProbe." + kv.Key + " is " + (probeValues[kv.Key] ?? "missing") + ", expected " + kv.Value);
            Assert.True(witnessTable != null, "PcInstaller.Witnesses is missing");
            object table = witnessTable is FieldInfo f ? f.GetValue(null) : ((PropertyInfo)witnessTable).GetValue(null);
            var actual = new Dictionary<string, (string File, string Type, string Member)>(StringComparer.Ordinal);
            foreach (object entry in (System.Collections.IEnumerable)table)
            {
                string key = (string)entry.GetType().GetProperty("Key").GetValue(entry);
                object witness = entry.GetType().GetProperty("Value").GetValue(entry);
                Type w = witness.GetType();
                actual[key] = ((string)w.GetProperty("AssemblyFile").GetValue(witness), (string)w.GetProperty("TypeName").GetValue(witness),
                               (string)w.GetProperty("MemberName").GetValue(witness));
            }
            Assert.Equal(expected.OrderBy(kv => kv.Key, StringComparer.Ordinal), actual.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        }

        /// <summary>The value of the public const string <paramref name="name"/> of <paramref name="type"/>, or null when it has none.</summary>
        private static string ConstantOrNull(Type type, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
            return field != null && field.IsLiteral && field.FieldType == typeof(string) ? (string)field.GetRawConstantValue() : null;
        }
    }
}
