using System;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// What McvsOffline.Server.dll and the patched assemblies may reference per build (docs/design/pc-port.md §7.2, P3): the PC DLL
    /// passes, the Android DLL fails on PC, a stand-in with System.Windows.Forms or a libc import fails, and a patched copy with
    /// a second mscorlib (which ILVerify does not notice) fails.
    /// </summary>
    public class ServerAssemblyCheckTests
    {
        private readonly ITestOutputHelper output;

        public ServerAssemblyCheckTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static readonly BuildProfile Pc = BuildProfile.PcSteam6372049;
        private static readonly BuildProfile Android = BuildProfile.Android1_17_32;

        [Fact]
        public void TheRealPcDllPassesOnPcAndFailsOnAndroid()
        {
            string dll = TestEnvironment.PcServerDll;
            if (dll == null)
            {
                Assert.False(TestEnvironment.RequirePc, "MCVS_REQUIRE_PC=1 but the PC server DLL of this configuration was not built");
                output.WriteLine("artifacts/bin/McvsOffline.Server.Pc not built: skipped");
                return;
            }
            byte[] bytes = File.ReadAllBytes(dll);
            ServerAssemblyCheck.Check(bytes, dll, Pc);
            output.WriteLine(string.Join("\n", ServerAssemblyCheck.References(bytes)));
            Assert.Throws<InvalidDataException>(() => ServerAssemblyCheck.Check(bytes, dll, Android));
        }

        [Fact]
        public void TheAndroidDllPassesOnAndroidAndFailsOnPc()
        {
            string dll = TestEnvironment.EmbeddedServerDll;
            if (dll == null)
            {
                Assert.False(Environment.GetEnvironmentVariable("MCVS_REQUIRE_EMBEDDED") == "1", "the embedded server DLL was not built");
                output.WriteLine("artifacts/bin/McvsOffline.Server.Embedded not built: skipped");
                return;
            }
            byte[] bytes = File.ReadAllBytes(dll);
            ServerAssemblyCheck.Check(bytes, dll, Android);
            InvalidDataException refused = Assert.Throws<InvalidDataException>(() => ServerAssemblyCheck.Check(bytes, dll, Pc));
            output.WriteLine(refused.Message);
            Assert.True(refused.Message.Contains("2.0.5.0") || refused.Message.Contains("Mono.Android") || refused.Message.Contains("Java.Interop"), refused.Message);
        }

        [Fact]
        public void TheStandInPassesAndBadStandInsFail()
        {
            ServerAssemblyCheck.Check(PcHookServer.Compile(), "stand-in", Pc);

            string forms = Assert.Throws<InvalidDataException>(() => ServerAssemblyCheck.Check(
                Compile("public static class F { public static object Make() { return new System.Windows.Forms.Form(); } }", "System.Windows.Forms.dll"), "forms", Pc)).Message;
            Assert.Contains("System.Windows.Forms", forms);
            Assert.Contains("never available in the PC game", forms);

            string libc = Assert.Throws<InvalidDataException>(() => ServerAssemblyCheck.Check(
                Compile("public static class N { [System.Runtime.InteropServices.DllImport(\"libc\")] public static extern int getpid(); }"), "libc", Pc)).Message;
            Assert.Contains("libc", libc);

            // kernel32, user32, steam_api are allowed (in any spelling Windows accepts).
            ServerAssemblyCheck.Check(Compile(
                "public static class W { [System.Runtime.InteropServices.DllImport(\"KERNEL32.dll\")] public static extern int GetCurrentThreadId();" +
                " [System.Runtime.InteropServices.DllImport(\"steam_api\")] public static extern bool SteamAPI_IsSteamRunning(); }"), "windows", Pc);

            string net8 = Assert.Throws<InvalidDataException>(() => ServerAssemblyCheck.Check(
                FixtureCompiler.Compile("McvsOffline.Server", "public static class X { }"), "net8", Pc)).Message;
            Assert.Contains("not the PC build", net8);   // a net8 build references System.Private.CoreLib or System.Runtime

            string named = Assert.Throws<InvalidDataException>(() => ServerAssemblyCheck.Check(
                Compile("public static class Y { }", assemblyName: "Other"), "other", Pc)).Message;
            Assert.Contains("expected McvsOffline.Server", named);
        }

        [Fact]
        public void APatchedCopyMayOnlyAddTheServerAndNeverASecondCorlib()
        {
            byte[] original = Compile("public static class G { }", assemblyName: "Game");
            ServerAssemblyCheck.CheckPatchedReferences("Game.dll", original, original, new Version(0, 0, 0, 0), Pc);

            byte[] withServer = AddReference(original, "McvsOffline.Server, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
            ServerAssemblyCheck.CheckPatchedReferences("Game.dll", original, withServer, new Version(0, 0, 0, 0), Pc);

            byte[] secondCorlib = AddReference(withServer, "mscorlib, Version=2.0.5.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e");
            string message = Assert.Throws<InvalidDataException>(() =>
                ServerAssemblyCheck.CheckPatchedReferences("Game.dll", original, secondCorlib, new Version(0, 0, 0, 0), Pc)).Message;
            output.WriteLine(message);
            Assert.Contains("mscorlib", message);

            byte[] runtime = AddReference(original, "System.Runtime, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
            Assert.Throws<InvalidDataException>(() => ServerAssemblyCheck.CheckPatchedReferences("Game.dll", original, runtime, new Version(0, 0, 0, 0), Pc));

            byte[] wrongServer = AddReference(original, "McvsOffline.Server, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null");
            Assert.Throws<InvalidDataException>(() => ServerAssemblyCheck.CheckPatchedReferences("Game.dll", original, wrongServer, new Version(0, 0, 0, 0), Pc));
        }

        [Fact]
        public void TheModuleNamesCompareLikeWindows()
        {
            Assert.Equal("kernel32", ServerAssemblyCheck.NativeModuleName("KERNEL32.DLL"));
            Assert.Equal("steam_api", ServerAssemblyCheck.NativeModuleName("steam_api"));
        }

        /// <summary>A net452 assembly referencing a type of every given reference assembly (so the AssemblyRef is emitted).</summary>
        private static byte[] Compile(string source, string extraReference = null, string assemblyName = "McvsOffline.Server")
        {
            string refs = TestEnvironment.Net452Refs;
            Assert.True(refs != null, "the net452 reference assemblies are part of the test setup (NuGet)");
            var references = new[] { "mscorlib.dll", "System.dll", "System.Core.dll" }.Concat(extraReference == null ? new string[0] : new[] { extraReference })
                .Select(f => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(refs, f))).ToList();
            var compilation = CSharpCompilation.Create(assemblyName, new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp7_3)) },
                                                       references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            using (var stream = new MemoryStream())
            {
                var result = compilation.Emit(stream);
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
                return stream.ToArray();
            }
        }

        private static byte[] AddReference(byte[] assembly, string fullName)
        {
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(assembly), new ReaderParameters { ReadingMode = ReadingMode.Deferred }))
            {
                module.AssemblyReferences.Add(AssemblyNameReference.Parse(fullName));
                var stream = new MemoryStream();
                module.Write(stream);
                return stream.ToArray();
            }
        }
    }
}
