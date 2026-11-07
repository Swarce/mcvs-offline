using System;
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
    /// <summary>The per-build helpers of <see cref="IlPatch"/> (docs/design/pc-port.md §3.4): ForBuild and SystemMethod.</summary>
    public class IlPatchBuildHelperTests
    {
        private readonly ITestOutputHelper output;

        public IlPatchBuildHelperTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>Exposes the protected helpers.</summary>
        private sealed class Probe : IlPatch
        {
            public override string Id { get { return "probe"; } }
            public override string AssemblyFile { get { return "Gameloft.Engine.dll"; } }
            public override string Description { get { return "test probe"; } }
            public override void Apply(ModuleDefinition module, PatchContext context) { }

            public string Pick(PatchContext ctx, string android, string pc) { return ForBuild(ctx, android, pc); }

            public static MethodReference System(ModuleDefinition module, string type, string name, bool instance, TypeReference returnType,
                                                 params TypeReference[] parameters)
            {
                return SystemMethod(module, type, name, instance, returnType, parameters);
            }
        }

        [Fact]
        public void ForBuildPicksTheBuildsValueAndRefusesAMissingTable()
        {
            var probe = new Probe();
            Assert.Equal("a", probe.Pick(new PatchContext(null, 1), "a", "p"));
            Assert.Equal("p", probe.Pick(new PatchContext(null, 1, BuildProfile.PcSteam6372049), "a", "p"));
            PatchException refused = Assert.Throws<PatchException>(() => probe.Pick(new PatchContext(null, 1, BuildProfile.PcSteam6372049), "a", null));
            Assert.Equal("probe is not supported on build pc-steam-6372049", refused.Message);
            Assert.Equal("probe is not supported on build android-1.17.32", Assert.Throws<PatchException>(() => probe.Pick(new PatchContext(null, 1), null, "p")).Message);
        }

        [Fact]
        public void SystemMethodResolvesThroughTheBclDirectory()
        {
            string folder = TestEnvironment.PcRefs;
            if (folder == null)
            {
                Assert.False(TestEnvironment.RequirePc, "MCVS_REQUIRE_PC=1 but MCVS_PC_REFS has no Steam install");
                output.WriteLine("the Steam install is not available (MCVS_PC_REFS): skipped");
                return;
            }
            ReaderParameters reader = TestEnvironment.PcReader(folder, null);
            using (reader.AssemblyResolver)
            using (ModuleDefinition engine = ModuleDefinition.ReadModule(Path.Combine(folder, "Gameloft.Engine.dll"), reader))
            {
                TestEnvironment.AssertPcCorlib(engine);
                AssemblyNameReference system = engine.AssemblyReferences.Single(r => r.Name == "System");
                var endPoint = new TypeReference("System.Net", "EndPoint", engine, system);
                MethodReference port = Probe.System(engine, "System.Net.IPEndPoint", "get_Port", true, engine.TypeSystem.Int32);
                Assert.Equal("System.Int32 System.Net.IPEndPoint::get_Port()", port.FullName);
                Assert.Equal("System", port.DeclaringType.Scope.Name);
                MethodReference local = Probe.System(engine, "System.Net.Sockets.TcpListener", "get_LocalEndpoint", true, endPoint);
                Assert.Equal(Path.Combine(TestEnvironment.Net452Refs, "System.dll"), local.Resolve().Module.FileName);
                Assert.Throws<PatchException>(() => Probe.System(engine, "System.Net.IPEndPoint", "NoSuchMember", true, engine.TypeSystem.Int32));
            }
        }
    }
}
