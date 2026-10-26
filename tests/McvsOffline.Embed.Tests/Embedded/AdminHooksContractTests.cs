using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using McvsOffline.Embed.Tests.Patcher;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Patches;
using McvsOffline.Server.Services.Admin;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The patch contract of design §8.1/§9.2–§9.4 between the patcher and the embedded host: McvsOffline.AdminHooks
    /// (hosts/embedded/Admin/AdminHooks.cs, compiled into this assembly and into the embedded server DLL) has exactly the
    /// three methods 'adminpage', 'adminlink' and 'forum' import, with exactly their signatures. A drift on either side
    /// would make the patcher refuse the server ("has no public static ...") or, worse, call a method that is not there.
    /// </summary>
    public class AdminHooksContractTests
    {
        /// <summary>The imported methods as Cecil names them: "return name(parameters)", in ordinal order.</summary>
        private static readonly string[] Contract =
        {
            "System.Boolean OpenIfPageMarker(System.String,System.Object)",                       // adminlink
            "System.Collections.Generic.IEnumerable`1<System.Object> AccountMenuButtons(System.Collections.Generic.IEnumerable`1<System.Object>)", // adminpage
            "System.Void OpenPage(System.String,System.Object)",                                  // adminpage, forum
        };

        private static readonly IlPatch[] HookPatches = { new AdminPagePatch(), new ForumPatch(), new AdminLinkPatch() };

        private readonly ITestOutputHelper output;

        public AdminHooksContractTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void TheCompiledHooksHaveExactlyTheContractsMethods()
        {
            Type hooks = typeof(McvsOffline.AdminHooks);
            Assert.Equal(AdminPagePatch.HooksType, hooks.FullName);
            Assert.Equal(AdminLinkPatch.HooksType, hooks.FullName);
            Assert.Equal(ForumPatch.HooksType, hooks.FullName);
            Assert.True(hooks.IsPublic && hooks.IsAbstract && hooks.IsSealed, "AdminHooks must be a public static class");

            MethodInfo[] methods = hooks.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.All(methods, m => Assert.True(m.IsStatic, m.Name + " is not static"));
            Assert.Equal(Contract, methods.Select(Signature).OrderBy(s => s, StringComparer.Ordinal));

            // The values the patched IL compares with and passes are the ones the host and the admin module use.
            Assert.Equal(AdminPagePatch.ButtonAction, McvsOffline.AdminHooks.ButtonAction);
            Assert.Equal(AdminTriggers.Account, AdminPagePatch.Trigger);
            Assert.Equal(AdminTriggers.Forum, ForumPatch.Trigger);
        }

        /// <summary>
        /// The real patches, run with this assembly as the server: each import must find its method in the hooks compiled
        /// from hosts/embedded, and together they import exactly the contract.
        /// </summary>
        [Fact]
        public void ThePatchesImportExactlyTheCompiledHooks()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = File.ReadAllBytes(typeof(McvsOffline.AdminHooks).Assembly.Location);
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, null, HookPatches);
            Assert.Equal(new[] { "Game.dll", "Gameloft.Engine.dll" }, patched.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(Contract, patched.Values.SelectMany(image => Imports(image).Select(i => i.Key)).Distinct().OrderBy(s => s, StringComparer.Ordinal));
        }

        /// <summary>
        /// The embedded server DLL of scripts/build.sh (the one the patcher puts into the APK): Cecil finds the three hooks
        /// there with the contract's signatures, and adminpage, adminlink and forum patch the real assemblies against it, every
        /// import pointing at McvsOffline.Server.
        /// </summary>
        [Fact]
        public void TheRealEmbeddedDllHasTheHooksAndThePatchesApplyAgainstIt()
        {
            string dll = TestEnvironment.EmbeddedServerDll;
            if (dll == null)
            {
                Assert.True(Environment.GetEnvironmentVariable("MCVS_REQUIRE_EMBEDDED") != "1", "MCVS_REQUIRE_EMBEDDED=1 but the embedded server DLL is missing");
                output.WriteLine("embedded server DLL not built: skipped");
                return;
            }
            output.WriteLine(dll);
            byte[] server = File.ReadAllBytes(dll);
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(server)))
            {
                TypeDefinition hooks = module.GetType(AdminPagePatch.HooksType);
                Assert.NotNull(hooks);
                Assert.True(hooks.IsPublic && hooks.IsAbstract && hooks.IsSealed, "AdminHooks must be a public static class");
                Assert.Equal(Contract, hooks.Methods.Where(m => m.IsPublic).Select(m =>
                {
                    Assert.True(m.IsStatic, m.Name + " is not static");
                    return Signature(m);
                }).OrderBy(s => s, StringComparer.Ordinal));
            }

            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, null, HookPatches);
            var imports = patched.Values.SelectMany(image => Imports(image)).ToList();
            Assert.Equal(Contract, imports.Select(i => i.Key).Distinct().OrderBy(s => s, StringComparer.Ordinal));
            Assert.All(imports, i => Assert.Equal("McvsOffline.Server", i.Value));
        }

        // ------------------------------------------------------------------ helpers

        private string Refs()
        {
            string dir = TestEnvironment.GameAssemblies;
            if (dir != null && File.Exists(Path.Combine(dir, "Game.dll")) && File.Exists(Path.Combine(dir, "Gameloft.Engine.dll"))) return dir;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but MCVS_REFS has no Game.dll and Gameloft.Engine.dll");
            output.WriteLine("the game's assemblies are not available (MCVS_REFS): skipped");
            return null;
        }

        /// <summary>The method references to the hooks type in a patched assembly: signature → the assembly it points to.</summary>
        private static List<KeyValuePair<string, string>> Imports(byte[] image)
        {
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(image)))
            {
                return module.GetMemberReferences().OfType<MethodReference>()
                             .Where(m => m.DeclaringType.FullName == AdminPagePatch.HooksType)
                             .Select(m => new KeyValuePair<string, string>(Signature(m), m.DeclaringType.Scope.Name))
                             .ToList();
            }
        }

        private static string Signature(MethodReference m) =>
            m.ReturnType.FullName + " " + m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) + ")";

        private static string Signature(MethodInfo m) =>
            CecilName(m.ReturnType) + " " + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => CecilName(p.ParameterType))) + ")";

        /// <summary>A reflection type in Cecil's FullName form ("System.Collections.Generic.IEnumerable`1&lt;System.Object&gt;").</summary>
        private static string CecilName(Type t) =>
            t.IsGenericType ? t.GetGenericTypeDefinition().FullName + "<" + string.Join(",", t.GetGenericArguments().Select(CecilName)) + ">" : t.FullName;
    }
}
