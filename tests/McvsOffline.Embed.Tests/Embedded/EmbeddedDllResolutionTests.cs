using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// Every type and member the built embedded McvsOffline.Server.dll uses resolves in the game's own trimmed assemblies
    /// (MCVS_REFS): the type and member reference tables, and every operand of every method body (generic instantiations
    /// included), and none of them is a linker stub that only throws. A member the linker removed would otherwise surface
    /// on the phone as a MissingMethodException the first time that code runs (the diagnostics code paths run rarely: on a
    /// crash, a stall or a failing log file).
    /// </summary>
    public class EmbeddedDllResolutionTests
    {
        private readonly ITestOutputHelper output;

        public EmbeddedDllResolutionTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void EveryReferenceResolvesInTheGamesAssemblies()
        {
            string dll = TestEnvironment.EmbeddedServerDll;
            string refs = TestEnvironment.GameAssemblies;
            if (dll == null || refs == null)
            {
                Assert.True(Environment.GetEnvironmentVariable("MCVS_REQUIRE_EMBEDDED") != "1",
                            "MCVS_REQUIRE_EMBEDDED=1 but the embedded DLL or MCVS_REFS is missing");
                output.WriteLine("embedded DLL or MCVS_REFS not available: skipped");
                return;
            }
            output.WriteLine(dll);
            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(refs);
                foreach (string d in resolver.GetSearchDirectories().Where(d => d != refs).ToList()) resolver.RemoveSearchDirectory(d);
                using (ModuleDefinition module = ModuleDefinition.ReadModule(dll, new ReaderParameters { AssemblyResolver = resolver, InMemory = true }))
                {
                    var references = module.AssemblyReferences.Select(a => a.Name).ToList();
                    output.WriteLine("assembly references: " + string.Join(", ", references));
                    foreach (string name in references) Assert.Contains(name, KnownBuild.ServerAllowedReferences);
                    Assert.Contains("Gameloft.Logs", references);   // the game-log bridge

                    var problems = new SortedSet<string>(StringComparer.Ordinal);
                    int types = 0, members = 0, operands = 0;
                    foreach (TypeReference t in module.GetTypeReferences())
                    {
                        types++;
                        if (Resolve(t) == null) problems.Add("type " + t.FullName);
                    }
                    foreach (MemberReference m in module.GetMemberReferences())
                    {
                        members++;
                        Check(m, problems);
                    }
                    foreach (TypeDefinition type in module.GetTypes())
                    {
                        foreach (MethodDefinition method in type.Methods.Where(x => x.HasBody))
                        {
                            foreach (Instruction i in method.Body.Instructions)
                            {
                                if (i.Operand is MemberReference member && !(member is IMemberDefinition))
                                {
                                    operands++;
                                    Check(member, problems);
                                }
                                else if (i.Operand is TypeReference tr && !(tr is TypeDefinition))
                                {
                                    operands++;
                                    if (Resolve(tr) == null) problems.Add("type " + tr.FullName + " in " + method.FullName);
                                }
                            }
                        }
                    }
                    output.WriteLine("type refs " + types + ", member refs " + members + ", body operands " + operands + ", problems " + problems.Count);
                    foreach (string p in problems) output.WriteLine("  " + p);
                    Assert.Empty(problems);

                    var imports = module.ModuleReferences.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                    output.WriteLine("P/Invoke modules: " + string.Join(", ", imports));
                    Assert.Equal(new[] { "libc", "liblog.so", "nativeengine" }, imports);   // nativeengine: the IAP state in the status line
                }
            }
        }

        private static TypeDefinition Resolve(TypeReference type)
        {
            try
            {
                if (type is GenericParameter) return null;
                return type.GetElementType().Resolve();
            }
            catch (AssemblyResolutionException)
            {
                return null;
            }
        }

        private static void Check(MemberReference member, SortedSet<string> problems)
        {
            if (member.DeclaringType is GenericParameter) return;
            try
            {
                if (member is MethodReference method)
                {
                    MethodDefinition resolved = method.Resolve();
                    if (resolved == null) problems.Add("method " + method.FullName);
                    else if (IsStub(resolved)) problems.Add("stub " + resolved.FullName);
                }
                else if (member is FieldReference field)
                {
                    if (field.Resolve() == null) problems.Add("field " + field.FullName);
                }
            }
            catch (AssemblyResolutionException ex)
            {
                problems.Add("unresolvable assembly for " + member.FullName + ": " + ex.Message);
            }
        }

        /// <summary>A body the linker reduced to "throw new NotSupportedException()" (or PlatformNotSupportedException).</summary>
        private static bool IsStub(MethodDefinition method)
        {
            if (!method.HasBody) return false;
            var code = method.Body.Instructions;
            if (code.Count > 6 || !code.Any(i => i.OpCode == OpCodes.Throw)) return false;
            return code.Any(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference r &&
                                 (r.DeclaringType.Name == "NotSupportedException" || r.DeclaringType.Name == "PlatformNotSupportedException" ||
                                  r.DeclaringType.Name == "NotImplementedException"));
        }
    }
}
