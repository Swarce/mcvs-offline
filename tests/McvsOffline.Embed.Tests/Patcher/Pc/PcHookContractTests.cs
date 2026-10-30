using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            "System.Void McvsOffline.GameHooks::InboxRewardException(System.Object,System.Exception)",
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
            Assert.Equal("McvsInbox", InboxPatch.HelperType);
            Assert.Equal("Game." + InboxPatch.HelperType, McvsOffline.Embedded.GamePatchProbe.InboxType);

            // The copies: GamePatchProbe's constants (WP2) and PcInstaller.Witnesses (WP3), compared by value.
            Assert.Equal(EngineBridgeHookPatch.TypeName, McvsOffline.Embedded.GamePatchProbe.HookPcType);
            Assert.Equal(EngineBridgeHookPatch.HelperName, McvsOffline.Embedded.GamePatchProbe.HookPcMethod);
            Assert.Equal(QuickLaunchPatch.HelperNamespace + "." + QuickLaunchPatch.HelperType, McvsOffline.Embedded.GamePatchProbe.QuickLaunchType);
            Assert.Equal(HeadlessDiagnosticsPatch.HelperNamespace + "." + HeadlessDiagnosticsPatch.HelperType, McvsOffline.Embedded.GamePatchProbe.WorldDiagnosticsType);
            Assert.Equal("Gameloft.Engine." + GraphicsPatch.HelperType, McvsOffline.Embedded.GamePatchProbe.GraphicsType);
            Assert.Equal("Gameloft.Engine.McvsGameLoad", GameAssemblyLoadPatch.HelperNamespace + "." + GameAssemblyLoadPatch.HelperType);
            Assert.Equal(GameAssemblyLoadPatch.HelperNamespace + "." + GameAssemblyLoadPatch.HelperType, McvsOffline.Embedded.GamePatchProbe.GameLoadType);

            var expected = new Dictionary<string, (string File, string Type, string Member)>(StringComparer.Ordinal)
            {
                ["hook-pc"] = (new EngineBridgeHookPatch().AssemblyFile, EngineBridgeHookPatch.TypeName, EngineBridgeHookPatch.HelperName),
                ["quicklaunch"] = (new QuickLaunchPatch().AssemblyFile, QuickLaunchPatch.HelperNamespace + "." + QuickLaunchPatch.HelperType, null),
                ["headless-diag"] = (new HeadlessDiagnosticsPatch().AssemblyFile, HeadlessDiagnosticsPatch.HelperNamespace + "." + HeadlessDiagnosticsPatch.HelperType, null),
                ["graphics"] = (new GraphicsPatch().AssemblyFile, "Gameloft.Engine." + GraphicsPatch.HelperType, null),
                ["gameload"] = (new GameAssemblyLoadPatch().AssemblyFile, GameAssemblyLoadPatch.HelperNamespace + "." + GameAssemblyLoadPatch.HelperType, null),
                ["store"] = (new StorePatch().AssemblyFile, "Game." + StorePatch.SafetyType, null),
                ["inbox"] = (new InboxPatch().AssemblyFile, "Game." + InboxPatch.HelperType, null),
            };
            var actual = McvsOffline.Patcher.Pc.PcInstaller.Witnesses.ToDictionary(
                kv => kv.Key, kv => (kv.Value.AssemblyFile, kv.Value.TypeName, kv.Value.MemberName), StringComparer.Ordinal);
            Assert.Equal(expected.OrderBy(kv => kv.Key, StringComparer.Ordinal), actual.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        }
    }
}
