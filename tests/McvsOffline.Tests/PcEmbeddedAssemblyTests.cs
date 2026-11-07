using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests
{
    /// <summary>
    /// Guards the PC server build (server/McvsOffline.Server.Pc.csproj, docs/design/pc-port.md §5.1, §12.1): the DLL the
    /// Steam game process loads from McvsOffline\bin. It needs no game file, so it is always built (this project
    /// references it, without its output) and always checked: the DLL of this test's own configuration,
    /// artifacts/bin/McvsOffline.Server.Pc/&lt;config&gt;/McvsOffline.Server.dll.
    /// </summary>
    public class PcEmbeddedAssemblyTests
    {
        private const string Net452Token = "b77a5c561934e089";

        /// <summary>Names that would mean a second corlib, the Android BCL or a .NET Core facade inside the game (P3).</summary>
        private static readonly string[] RefusedReferences =
        {
            "Mono.Android", "Java.Interop", "netstandard", "System.Runtime", "System.Private.CoreLib", "Mono.Cecil",
            "McvsOffline.Patcher", "Newtonsoft.Json", "Gameloft.Engine", "Game", "Gameloft.OnlinePlatform",
        };

        private static readonly string[] AllowedModuleRefs = { "kernel32", "user32", "steam_api" };

        private readonly ITestOutputHelper output;

        public PcEmbeddedAssemblyTests(ITestOutputHelper output)
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

        /// <summary>The PC DLL of this test's configuration (the folder name of the test's own output, e.g. "release").</summary>
        internal static string PcServerDll
        {
            get
            {
                string config = Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return Path.Combine(RepoRoot, "artifacts", "bin", "McvsOffline.Server.Pc", config, "McvsOffline.Server.dll");
            }
        }

        private static ModuleDefinition Read()
        {
            string dll = PcServerDll;
            Assert.True(File.Exists(dll), dll + " is missing: build server/McvsOffline.Server.Pc.csproj (part of McvsOffline.sln)");
            return ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(dll)));
        }

        private static string Token(AssemblyNameReference reference) =>
            string.Concat((reference.PublicKeyToken ?? new byte[0]).Select(b => b.ToString("x2")));

        private static bool HasGameLog(ModuleDefinition module) => module.AssemblyReferences.Any(r => r.Name == "Gameloft.Logs");

        [Fact]
        public void TheDllIsTheServerAtVersionZero()
        {
            using ModuleDefinition module = Read();
            Assert.Equal("McvsOffline.Server", module.Assembly.Name.Name);
            Assert.Equal(new Version(0, 0, 0, 0), module.Assembly.Name.Version); // what the patched assemblies reference
            Assert.Equal(TargetRuntime.Net_4_0, module.Runtime);
            Assert.Equal(ModuleKind.Dll, module.Kind);
            Assert.False(module.Attributes.HasFlag(ModuleAttributes.Required32Bit)); // AnyCPU: the 32-bit game loads it
        }

        [Fact]
        public void AssemblyRefsAreExactlyTheFramework4Corlib()
        {
            using ModuleDefinition module = Read();
            foreach (AssemblyNameReference r in module.AssemblyReferences)
                output.WriteLine(r.FullName);
            var expected = new List<string> { "mscorlib", "System", "System.Core" };
            if (HasGameLog(module)) expected.Add("Gameloft.Logs");
            Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal),
                         module.AssemblyReferences.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));
            foreach (AssemblyNameReference r in module.AssemblyReferences)
            {
                Assert.DoesNotContain(r.Name, RefusedReferences);
                if (r.Name == "Gameloft.Logs")
                {
                    Assert.Equal(new Version(1, 0, 0, 0), r.Version);
                    continue;
                }
                // One corlib: 4.0.0.0 b77a5c561934e089, never Xamarin's 2.0.5.0 7cec85d7bea7798e.
                Assert.Equal(new Version(4, 0, 0, 0), r.Version);
                Assert.Equal(Net452Token, Token(r));
            }
        }

        [Fact]
        public void ModuleRefsAreWindowsOnly()
        {
            using ModuleDefinition module = Read();
            var imports = new List<string>();
            foreach (TypeDefinition type in module.GetTypes())
            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.IsPInvokeImpl || method.PInvokeInfo == null) continue;
                imports.Add(method.PInvokeInfo.Module.Name + "!" + (method.PInvokeInfo.EntryPoint ?? method.Name));
            }
            output.WriteLine("P/Invokes: " + string.Join(", ", imports));
            foreach (ModuleReference reference in module.ModuleReferences)
                Assert.Contains(reference.Name, AllowedModuleRefs); // no libc, libdl, liblog.so, __Internal, nativeengine
            Assert.Contains("kernel32!MoveFileExW", imports);      // the PC rename (WindowsFiles, §4.6)
            Assert.DoesNotContain(imports, i => i.StartsWith("libc!", StringComparison.Ordinal));
        }

        [Fact]
        public void TheEntryPointAndThePcHooksHaveTheirContractSignatures()
        {
            using ModuleDefinition module = Read();
            AssertHook(module, "McvsOffline.Bootstrap", "Init", "System.Void");
            AssertHook(module, "McvsOffline.PcHooks", "QuickLaunch", "System.Threading.Tasks.Task",
                       "System.Object", "System.Object", "System.Threading.CancellationToken");
            AssertHook(module, "McvsOffline.PcHooks", "DefaultAutomatchHost", "System.Void", "System.Object");
            AssertHook(module, "McvsOffline.PcHooks", "ServerWorldFailed", "System.Void", "System.Exception");
            AssertHook(module, "McvsOffline.PcHooks", "ComponentCreateFailed", "System.Void", "System.Exception", "System.Type");
            Assert.NotNull(module.GetType("McvsOffline.Embedded.Windows.WindowsPlatform"));
        }

        private static void AssertHook(ModuleDefinition module, string typeName, string name, string returnType, params string[] parameters)
        {
            TypeDefinition type = module.GetType(typeName);
            Assert.True(type != null, typeName + " missing");
            Assert.True(type.IsPublic, typeName + " is not public");
            MethodDefinition method = type.Methods.SingleOrDefault(m => m.Name == name && m.Parameters.Count == parameters.Length);
            Assert.True(method != null, typeName + "::" + name + " missing");
            Assert.True(method.IsPublic && method.IsStatic, typeName + "::" + name + " is not public static");
            Assert.Equal(returnType, method.ReturnType.FullName);
            Assert.Equal(parameters, method.Parameters.Select(p => p.ParameterType.FullName).ToArray());
            // BCL types only (IlPatch.ImportServerMethod): mscorlib, System or System.Core.
            foreach (TypeReference t in method.Parameters.Select(p => p.ParameterType).Concat(new[] { method.ReturnType }))
            {
                var scope = t.Scope as AssemblyNameReference;
                Assert.True(scope != null && new[] { "mscorlib", "System", "System.Core" }.Contains(scope.Name), t.FullName + " from " + t.Scope);
            }
        }

        [Fact]
        public void NoAndroidInstallerHdOrVerificationCode()
        {
            using ModuleDefinition module = Read();
            List<TypeDefinition> types = module.GetTypes().ToList();
            Assert.DoesNotContain(types, t => t.Namespace.StartsWith("McvsOffline.Embedded.Install", StringComparison.Ordinal));
            Assert.DoesNotContain(types, t => t.Namespace.StartsWith("McvsOffline.Embedded.Hd", StringComparison.Ordinal));
            Assert.DoesNotContain(types, t => t.Namespace.StartsWith("McvsOffline.Embedded.Android", StringComparison.Ordinal));
            Assert.DoesNotContain(types, t => t.Name == "IInstallerPlatform");
            Assert.DoesNotContain(types, t => t.Name == "AndroidPlatform");
            TypeDefinition reflection = types.FirstOrDefault(t => t.Name == "GameReflection");
            if (reflection != null) Assert.DoesNotContain(reflection.Methods, m => m.Name == "Verify");
            // The Mono thread dump is replaced by the no-op stub (ThreadDumpStub.cs): no libc behind it.
            TypeDefinition dump = types.FirstOrDefault(t => t.Name == "ThreadDump");
            if (dump != null) Assert.DoesNotContain(dump.Methods, m => m.IsPInvokeImpl);
        }

        /// <summary>The one type of the PC DLL that may bind a type by name: RuntimeInfo (mscorlib's RuntimeInformation, §3.8).</summary>
        private const string ByNameAllowed = "McvsOffline.Embedded.Windows.RuntimeInfo";

        /// <summary>
        /// A call that can make the runtime load an assembly by name or from a file: <c>Type.GetType(string…)</c>,
        /// <c>Assembly.Load*</c>/<c>LoadFrom</c>/<c>LoadWithPartialName</c>, <c>AppDomain.Load</c>/<c>CreateInstance*</c>/<c>ExecuteAssembly*</c>,
        /// <c>Activator.CreateInstance*(string…)</c> and <c>new AssemblyName(string)</c>. A loaded <c>Assembly</c>'s own
        /// <c>GetType(string)</c> loads nothing and is not one.
        /// </summary>
        private static bool IsRuntimeLoad(MethodReference m)
        {
            string type = m.DeclaringType.FullName;
            bool stringFirst = m.Parameters.Count > 0 && m.Parameters[0].ParameterType.FullName == "System.String";
            switch (type)
            {
                case "System.Type":
                    return m.Name == "GetType" && stringFirst;
                case "System.Reflection.Assembly":
                    return m.Name.StartsWith("Load", StringComparison.Ordinal) || m.Name == "UnsafeLoadFrom" ||
                           m.Name.StartsWith("ReflectionOnlyLoad", StringComparison.Ordinal);
                case "System.AppDomain":
                    return m.Name == "Load" || m.Name.StartsWith("CreateInstance", StringComparison.Ordinal) ||
                           m.Name.StartsWith("ExecuteAssembly", StringComparison.Ordinal);
                case "System.Activator":
                    return m.Name.StartsWith("CreateInstance", StringComparison.Ordinal) && stringFirst;
                case "System.Reflection.AssemblyName":
                    return m.Name == ".ctor" && stringFirst;
                default:
                    return false;
            }
        }

        private static string Outermost(TypeDefinition type)
        {
            while (type.DeclaringType != null) type = type.DeclaringType;
            return type.FullName;
        }

        /// <summary>Every runtime-load call of the module as "Caller::Method -> callee", lambdas and iterators under their outer type.</summary>
        private static List<(string Owner, string Line)> RuntimeLoads(ModuleDefinition module)
        {
            var found = new List<(string, string)>();
            foreach (TypeDefinition type in module.GetTypes())
            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (Instruction i in method.Body.Instructions)
                {
                    var callee = i.Operand as MethodReference;
                    if (callee == null || (i.OpCode.Code != Code.Call && i.OpCode.Code != Code.Callvirt && i.OpCode.Code != Code.Newobj &&
                                           i.OpCode.Code != Code.Ldftn && i.OpCode.Code != Code.Ldvirtftn)) continue;
                    if (IsRuntimeLoad(callee)) found.Add((Outermost(type), type.FullName + "::" + method.Name + " -> " + callee.FullName));
                }
            }
            return found;
        }

        /// <summary>
        /// Build 18 (docs/design/pc-port.md §8.9): the PC DLL never makes the runtime load an assembly by name. The .exe.config
        /// codeBase applies to full references only, so a partial name (build 17's <c>Type.GetType("Game.PlayerProfileComponent, Game")</c>)
        /// loads the STOCK file next to the exe. This is the PC build as shipped (MCVS_WINDOWS), which the embed tests do not
        /// compile: in particular <c>LoadedTypes.EngineType</c> has no partial-name fallback here.
        /// </summary>
        [Fact]
        public void OnlyRuntimeInfoLoadsByName()
        {
            using ModuleDefinition module = Read();
            List<(string Owner, string Line)> loads = RuntimeLoads(module);
            foreach ((string _, string line) in loads) output.WriteLine(line);
            // The detector sees RuntimeInfo's two Type.GetType calls (the control).
            Assert.Contains(loads, l => l.Owner == ByNameAllowed && l.Line.Contains("System.Type::GetType(System.String,System.Boolean)"));
            Assert.Empty(loads.Where(l => l.Owner != ByNameAllowed).Select(l => l.Line));

            TypeDefinition loadedTypes = module.GetType("McvsOffline.Embedded.LoadedTypes");
            Assert.True(loadedTypes != null, "McvsOffline.Embedded.LoadedTypes missing");
            MethodDefinition engineType = loadedTypes.Methods.Single(m => m.Name == "EngineType");
            Assert.DoesNotContain(engineType.Body.Instructions, i => i.Operand is MethodReference m && m.DeclaringType.FullName == "System.Type" && m.Name == "GetType");
            // ... and GameReflection binds through LoadedTypes, not by itself.
            TypeDefinition reflection = module.GetTypes().Single(t => t.FullName == "McvsOffline.Embedded.Admin.GameReflection");
            Assert.Contains(reflection.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions),
                            i => i.Operand is MethodReference m && m.DeclaringType.FullName == "McvsOffline.Embedded.LoadedTypes" && m.Name == "Find");
        }

        [Fact]
        public void IlVerifyAgainstNet452IsClean()
        {
            string tool = IlVerifyTool();
            string bcl = Net452Refs();
            if (tool == null || bcl == null)
            {
                Assert.True(Environment.GetEnvironmentVariable("MCVS_REQUIRE_PC") != "1",
                            "MCVS_REQUIRE_PC=1 but ilverify (" + tool + ") or the net452 reference assemblies (" + bcl + ") are missing");
                output.WriteLine("ilverify or the net452 reference assemblies not found: skipped");
                return;
            }
            var references = new List<string> { Path.Combine(bcl, "mscorlib.dll"), Path.Combine(bcl, "System.dll"), Path.Combine(bcl, "System.Core.dll") };
            using (ModuleDefinition module = Read())
            {
                if (HasGameLog(module))
                {
                    string logs = Path.Combine(PcRefs(), "Gameloft.Logs.dll");
                    if (!File.Exists(logs))
                    {
                        Assert.True(Environment.GetEnvironmentVariable("MCVS_REQUIRE_PC") != "1", logs + " is missing");
                        output.WriteLine("the DLL references Gameloft.Logs but " + logs + " is missing: skipped");
                        return;
                    }
                    references.Add(logs);
                }
            }
            var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add(PcServerDll);
            foreach (string r in references)
            {
                info.ArgumentList.Add("-r");
                info.ArgumentList.Add(r);
            }
            info.ArgumentList.Add("--system-module");
            info.ArgumentList.Add("mscorlib");
            info.ArgumentList.Add("--statistics");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
            {
                string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(typeof(object).Assembly.Location)));
                if (root != null) info.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(root);
            }
            using Process process = Process.Start(info);
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            output.WriteLine(stdout.Trim());
            Assert.True(process.ExitCode == 0, "ilverify exit code " + process.ExitCode + ":\n" + stdout + stderr);
            Assert.Contains("Verified.", stdout);
        }

        private static string IlVerifyTool()
        {
            string tool = Environment.GetEnvironmentVariable("MCVS_ILVERIFY");
            if (string.IsNullOrEmpty(tool)) tool = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", ".dotnet", "tools", "ilverify");
            return File.Exists(tool) ? tool : null;
        }

        private static string Net452Refs()
        {
            string dir = Environment.GetEnvironmentVariable("MCVS_NET452_REFS");
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", ".nuget", "packages",
                                   "microsoft.netframework.referenceassemblies.net452", "1.0.3", "build", ".NETFramework", "v4.5.2");
            return File.Exists(Path.Combine(dir, "mscorlib.dll")) ? dir : null;
        }

        private static string PcRefs()
        {
            string dir = Environment.GetEnvironmentVariable("MCVS_PC_REFS");
            return string.IsNullOrEmpty(dir) ? "/home/user/mcvs-work/pc/install" : dir;
        }
    }
}
