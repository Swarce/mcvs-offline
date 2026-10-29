using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.PcKit.Tests
{
    /// <summary>
    /// The publish-time check of the server DLL the kit embeds (§9.1): the real PC DLL of this configuration passes when it was
    /// built (mandatory under MCVS_REQUIRE_PC=1), and stand-ins without a hook, without Gameloft.Logs or with a wrong reference fail.
    /// </summary>
    public class PublishCheckTests
    {
        private readonly ITestOutputHelper output;

        public PublishCheckTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static string RepoRoot
        {
            get
            {
                string dir = AppContext.BaseDirectory;
                while (dir != null && !File.Exists(Path.Combine(dir, "McvsOffline.sln"))) dir = Path.GetDirectoryName(dir);
                return dir;
            }
        }

        private static string Net452
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("MCVS_NET452_REFS");
                if (string.IsNullOrEmpty(dir))
                {
                    dir = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", ".nuget", "packages", "microsoft.netframework.referenceassemblies.net452",
                                       "1.0.3", "build", ".NETFramework", "v4.5.2");
                }
                return File.Exists(Path.Combine(dir, "mscorlib.dll")) ? dir : null;
            }
        }

        [Fact]
        public void TheRealPcServerDllOfThisConfigurationPasses()
        {
            string config = Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string dll = Path.Combine(RepoRoot, "artifacts", "bin", "McvsOffline.Server.Pc", config, "McvsOffline.Server.dll");
            if (!File.Exists(dll))
            {
                Assert.False(Environment.GetEnvironmentVariable("MCVS_REQUIRE_PC") == "1", "MCVS_REQUIRE_PC=1 but " + dll + " was not built");
                output.WriteLine(dll + " not built: skipped");
                return;
            }
            List<string> problems = ServerHookContract.Check(File.ReadAllBytes(dll));
            if (problems.Count == 1 && problems[0].Contains("MCVS_GAMELOG") && Environment.GetEnvironmentVariable("MCVS_REQUIRE_PC") != "1")
            {
                // A build without the Steam install (no Gameloft.Logs.dll) is fine for the tests, never for a published kit.
                output.WriteLine(problems[0] + ": not publishable, skipped");
                return;
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
            Assert.Equal(0, RunEntryPoint("check-server", dll));
        }

        [Fact]
        public void AStandInWithEveryHookPassesAndOneWithoutAHookOrTheGameLogFails()
        {
            if (Net452 == null)
            {
                output.WriteLine("the net452 reference assemblies are missing: skipped");
                return;
            }
            Assert.Empty(ServerHookContract.Check(Compile(StandIn(null), withGameLog: true)));

            List<string> noGameLog = ServerHookContract.Check(Compile(StandIn(null), withGameLog: false));
            Assert.Contains(noGameLog, p => p.Contains("built without MCVS_GAMELOG"));

            List<string> missing = ServerHookContract.Check(Compile(StandIn("ComponentCreateFailed"), withGameLog: true));
            Assert.Single(missing);
            Assert.Contains("ComponentCreateFailed", missing[0]);
        }

        [Fact]
        public void TheEntryPointRefusesBadArgumentsAndAMissingFile()
        {
            Assert.Equal(2, RunEntryPoint());
            Assert.Equal(1, RunEntryPoint("check-server", Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".dll")));
        }

        private static int RunEntryPoint(params string[] args)
        {
            TextWriter o = Console.Out, e = Console.Error;
            try
            {
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
                return PublishCheck.Main(args);
            }
            finally
            {
                Console.SetOut(o);
                Console.SetError(e);
            }
        }

        /// <summary>C# for a server with every §3.2 hook (except <paramref name="leaveOut"/>), from the contract table itself.</summary>
        private static string StandIn(string leaveOut)
        {
            var src = new System.Text.StringBuilder();
            src.Append("using System; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks;\n");
            src.Append("[assembly: System.Reflection.AssemblyVersion(\"0.0.0.0\")]\n");
            src.Append("namespace McvsOffline.Server.Core { public static class LogsUser { public static object Use() { return typeof(Gameloft.Logs.Marker); } } }\n");
            foreach (var group in ServerHookContract.Hooks.Where(h => h.Method != leaveOut).GroupBy(h => h.Type))
            {
                string ns = group.Key.Substring(0, group.Key.LastIndexOf('.'));
                string type = group.Key.Substring(group.Key.LastIndexOf('.') + 1);
                src.Append("namespace ").Append(ns).Append(" { public static class ").Append(type).Append(" {\n");
                foreach (ServerHook h in group)
                {
                    string ret = CSharp(h.ReturnType);
                    src.Append("public static ").Append(ret).Append(' ').Append(h.Method).Append('(')
                       .Append(string.Join(", ", h.Parameters.Select((p, i) => CSharp(p) + " a" + i))).Append(") { ")
                       .Append(ret == "void" ? "" : "return default(" + ret + "); ").Append("}\n");
                }
                src.Append("} }\n");
            }
            return src.ToString();
        }

        private static string CSharp(string type)
        {
            return type == "System.Void" ? "void" : type.Replace("`1<", "<");
        }

        private static byte[] Compile(string source, bool withGameLog)
        {
            var refs = new[] { "mscorlib.dll", "System.dll", "System.Core.dll" }
                .Select(f => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(Net452, f))).ToList();
            var logs = CSharpCompilation.Create("Gameloft.Logs", new[] { CSharpSyntaxTree.ParseText(
                    "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")] namespace Gameloft.Logs { public class Marker { } }") }, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            if (withGameLog) refs.Add(logs.ToMetadataReference());
            else source = source.Replace("typeof(Gameloft.Logs.Marker)", "null");
            var compilation = CSharpCompilation.Create("McvsOffline.Server", new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp7_3)) },
                                                       refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using (var stream = new MemoryStream())
            {
                var result = compilation.Emit(stream);
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
                return stream.ToArray();
            }
        }
    }
}
