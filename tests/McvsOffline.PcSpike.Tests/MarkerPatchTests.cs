using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.PcSpike.Tests
{
    /// <summary>
    /// The WP0 step 2 marker edit of EngineBridge.Class1.Start: fingerprint-checked against the real Steam EngineBridge.dll
    /// (when the install is present), refused on any other body, references only mscorlib 4.0.0.0 members that exist in
    /// .NET Framework 4.5, and, executed on net8 with a Gameloft.Engine look-alike, writes the marker and then starts the
    /// game exactly as before.
    /// </summary>
    public class MarkerPatchTests
    {
        private readonly ITestOutputHelper output;

        public MarkerPatchTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static byte[] Patch(byte[] engineBridge, string marker, List<string> notes = null)
        {
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(engineBridge)))
            {
                var context = new PatchContext(null, 0);
                new EngineBridgeMarkerPatch(marker).Apply(module, context);
                if (notes != null) notes.AddRange(context.Notes);
                using (var stream = new MemoryStream())
                {
                    module.Write(stream);
                    return stream.ToArray();
                }
            }
        }

        private static string Marker() => Path.Combine(Path.GetTempPath(), "mcvs-pcspike-marker-" + Guid.NewGuid().ToString("N") + ".txt");

        [Fact]
        public void TheRealSteamClass1StartHasTheVerifiedFingerprint()
        {
            string pc = Support.PcInstall(output);
            if (pc == null) return;
            string file = Path.Combine(pc, "EngineBridge.dll");
            Assert.Equal(KnownPcBuild.EngineBridgeSha256, KnownPcBuild.Sha256(file));
            using (var module = ModuleDefinition.ReadModule(file))
            {
                Assert.Equal(Guid.Parse(KnownPcBuild.EngineBridgeMvid), module.Mvid);
                MethodDefinition start = module.GetType(EngineBridgeMarkerPatch.TypeName).Methods.Single(m => m.Name == "Start");
                output.WriteLine(string.Join("\n", start.Body.Instructions));
                Assert.Equal(EngineBridgeMarkerPatch.StartFingerprint, IlPatch.Fingerprint(start));
                Assert.Equal(new Version(1, 0, 0, 0), module.Assembly.Name.Version);
            }
        }

        [Fact]
        public void TheLookAlikeCompilesToTheSameBodySoTheOtherTestsRunWithoutTheInstall()
        {
            byte[] bridge = Support.EngineBridge(Support.Engine());
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(bridge)))
            {
                MethodDefinition start = module.GetType(EngineBridgeMarkerPatch.TypeName).Methods.Single(m => m.Name == "Start");
                Assert.Equal(EngineBridgeMarkerPatch.StartFingerprint, IlPatch.Fingerprint(start));
            }
        }

        [Fact]
        public void TheRealDllIsPatchedOnlyAtTheVerifiedSite()
        {
            string pc = Support.PcInstall(output);
            if (pc == null) return;
            string marker = Marker();
            var notes = new List<string>();
            byte[] original = File.ReadAllBytes(Path.Combine(pc, "EngineBridge.dll"));
            byte[] patched = Patch(original, marker, notes);
            foreach (string n in notes) output.WriteLine(n);
            Assert.Single(notes);

            using (var before = ModuleDefinition.ReadModule(new MemoryStream(original)))
            using (var after = ModuleDefinition.ReadModule(new MemoryStream(patched)))
            {
                TypeDefinition type = after.GetType(EngineBridgeMarkerPatch.TypeName);
                MethodDefinition start = type.Methods.Single(m => m.Name == "Start");
                var code = start.Body.Instructions;
                // try { call McvsSpikeMarker(); leave } catch (object) { pop; leave } + the original six instructions.
                Assert.Equal(OpCodes.Call, code[0].OpCode);
                Assert.Equal(EngineBridgeMarkerPatch.HelperName, ((MethodReference)code[0].Operand).Name);
                Assert.Equal(OpCodes.Leave, code[1].OpCode);
                Assert.Equal(OpCodes.Pop, code[2].OpCode);
                Assert.Equal(OpCodes.Leave, code[3].OpCode);
                MethodDefinition originalStart = before.GetType(EngineBridgeMarkerPatch.TypeName).Methods.Single(m => m.Name == "Start");
                Assert.Equal(originalStart.Body.Instructions.Select(i => i.OpCode + " " + i.Operand),
                             code.Skip(4).Select(i => i.OpCode + " " + i.Operand));
                Assert.Same(code[4], code[1].Operand);
                ExceptionHandler handler = Assert.Single(start.Body.ExceptionHandlers);
                Assert.Equal("System.Object", handler.CatchType.FullName);

                // Nothing else changed: same types, same other methods, one helper more.
                Assert.Equal(before.Types.Select(t => t.FullName), after.Types.Select(t => t.FullName));
                Assert.Equal(before.GetType(EngineBridgeMarkerPatch.TypeName).Methods.Select(m => m.Name).Concat(new[] { EngineBridgeMarkerPatch.HelperName }),
                             type.Methods.Select(m => m.Name));
                MethodDefinition helper = type.Methods.Single(m => m.Name == EngineBridgeMarkerPatch.HelperName);
                Assert.True(helper.IsPrivate && helper.IsStatic);
                Assert.True(helper.NoInlining);
                Assert.Equal(marker, helper.Body.Instructions[0].Operand);

                // Only the game's own references: mscorlib 4.0.0.0 and Gameloft.Engine 0.0.0.0, nothing from net8.
                Assert.Equal(before.AssemblyReferences.Select(r => r.FullName), after.AssemblyReferences.Select(r => r.FullName));
                Assert.Equal(before.Assembly.Name.FullName, after.Assembly.Name.FullName);
            }
        }

        [Fact]
        public void EveryAddedReferenceExistsInDotNetFramework45()
        {
            byte[] engine = Support.Engine();
            byte[] patched = Patch(Support.EngineBridge(engine), Marker());
            string dir = Support.NewTempDir("refs");
            try
            {
                File.WriteAllBytes(Path.Combine(dir, "Gameloft.Engine.dll"), engine);
                var resolver = new DefaultAssemblyResolver();
                resolver.RemoveSearchDirectory(".");
                resolver.RemoveSearchDirectory("bin");
                resolver.AddSearchDirectory(Support.Net45ReferenceAssemblies);
                resolver.AddSearchDirectory(dir);
                using (resolver)
                using (var module = ModuleDefinition.ReadModule(new MemoryStream(patched), new ReaderParameters { AssemblyResolver = resolver }))
                {
                    var references = module.GetMemberReferences().OfType<MethodReference>().ToList();
                    Assert.Contains(references, r => r.Name == "WriteAllText");
                    foreach (MethodReference reference in references)
                    {
                        MethodDefinition resolved = reference.Resolve();
                        Assert.True(resolved != null, reference.FullName + " does not resolve against .NET Framework 4.5");
                        if (reference.DeclaringType.Scope.Name == "mscorlib")
                            Assert.Equal("mscorlib", resolved.Module.Assembly.Name.Name);
                    }
                }
            }
            finally
            {
                Support.DeleteDir(dir);
            }
        }

        [Fact]
        public void ThePatchedStartWritesTheMarkerThenStartsTheGameAsBefore()
        {
            byte[] engine = Support.Engine();
            string marker = Marker();
            try
            {
                byte[] patched = Patch(Support.EngineBridge(engine), marker);
                using (var sandbox = new Support.Sandbox().With("Gameloft.Engine", engine).With("EngineBridge", patched))
                {
                    Type mainLoop = sandbox.Get("Gameloft.Engine").GetType("Gameloft.Engine.MainLoop", true);
                    object class1 = Activator.CreateInstance(sandbox.Get("EngineBridge").GetType("EngineBridge.Class1", true));
                    class1.GetType().GetMethod("Start").Invoke(class1, null);

                    Assert.Equal(1, mainLoop.GetField("Calls").GetValue(null));
                    Assert.Equal(0, (int)mainLoop.GetField("LastMode").GetValue(null));
                    Assert.Null(mainLoop.GetField("LastArgs").GetValue(null));
                    Assert.False((bool)mainLoop.GetField("LastIsEditor").GetValue(null));
                }
                string text = File.ReadAllText(marker);
                output.WriteLine(text);
                Assert.StartsWith(EngineBridgeMarkerPatch.MarkerHeader + "\r\nlocation=", text);
                Assert.Contains("\r\ncwd=" + Environment.CurrentDirectory + "\r\n", text);
                Assert.Contains("\r\ncommandline=", text);
            }
            finally
            {
                File.Delete(marker);
            }
        }

        [Fact]
        public void AFailingMarkerWriteDoesNotStopTheGame()
        {
            byte[] engine = Support.Engine();
            string unwritable = Path.Combine(Path.GetTempPath(), "mcvs-no-such-dir-" + Guid.NewGuid().ToString("N"), "marker.txt");
            byte[] patched = Patch(Support.EngineBridge(engine), unwritable);
            using (var sandbox = new Support.Sandbox().With("Gameloft.Engine", engine).With("EngineBridge", patched))
            {
                Type mainLoop = sandbox.Get("Gameloft.Engine").GetType("Gameloft.Engine.MainLoop", true);
                object class1 = Activator.CreateInstance(sandbox.Get("EngineBridge").GetType("EngineBridge.Class1", true));
                class1.GetType().GetMethod("Start").Invoke(class1, null);
                Assert.Equal(1, mainLoop.GetField("Calls").GetValue(null));
            }
            Assert.False(File.Exists(unwritable));
        }

        [Fact]
        public void AnotherBodyIsRefusedBeforeAnythingChanges()
        {
            byte[] engine = Support.Engine();
            string changed = Support.EngineBridgeSource.Replace("MainLoop.Start(MainLoop.ModeType.NativeHostsDotNet);",
                                                                "MainLoop.Start(MainLoop.ModeType.DotNetHostsNative);");
            byte[] bridge = Support.Compile("EngineBridge", changed, engine);
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(bridge)))
            {
                var ex = Assert.Throws<PatchException>(() => new EngineBridgeMarkerPatch(Marker()).Apply(module, new PatchContext(null, 0)));
                Assert.Contains("not the verified Steam 1.17.30 code", ex.Message);
                TypeDefinition type = module.GetType(EngineBridgeMarkerPatch.TypeName);
                Assert.DoesNotContain(type.Methods, m => m.Name == EngineBridgeMarkerPatch.HelperName);
                Assert.Empty(type.Methods.Single(m => m.Name == "Start").Body.ExceptionHandlers);
            }
        }

        [Fact]
        public void OurOwnCopyIsRefused()
        {
            byte[] patched = Patch(Support.EngineBridge(Support.Engine()), Marker());
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(patched)))
            {
                var ex = Assert.Throws<PatchException>(() => new EngineBridgeMarkerPatch(Marker()).Apply(module, new PatchContext(null, 0)));
                Assert.Contains("already has", ex.Message);
            }
        }

        [Fact]
        public void AnAssemblyThatIsNotOnMscorlib4IsRefused()
        {
            // The same body built against net8 (System.Runtime): the fingerprint matches, the corlib check does not.
            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(p => (Microsoft.CodeAnalysis.MetadataReference)Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p)).ToList();
            byte[] Build(string name, string source, params byte[][] extra)
            {
                var all = refs.Concat(extra.Select(e => (Microsoft.CodeAnalysis.MetadataReference)Microsoft.CodeAnalysis.MetadataReference.CreateFromImage(e))).ToList();
                var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(name,
                    new[] { Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source) }, all,
                    new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                                                                               optimizationLevel: Microsoft.CodeAnalysis.OptimizationLevel.Release));
                using (var stream = new MemoryStream())
                {
                    Assert.True(compilation.Emit(stream).Success);
                    return stream.ToArray();
                }
            }
            byte[] engine = Build("Gameloft.Engine", Support.EngineSource);
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(Build("EngineBridge", Support.EngineBridgeSource, engine))))
            {
                var ex = Assert.Throws<PatchException>(() => new EngineBridgeMarkerPatch(Marker()).Apply(module, new PatchContext(null, 0)));
                Assert.Contains("mscorlib 4", ex.Message);
            }
        }

        [Fact]
        public void IlVerifyAcceptsTheRealPatchedDll()
        {
            string pc = Support.PcInstall(output);
            if (pc == null) return;
            string tool = Environment.GetEnvironmentVariable("MCVS_ILVERIFY");
            if (string.IsNullOrEmpty(tool)) tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "ilverify");
            if (!File.Exists(tool))
            {
                output.WriteLine("ilverify not found (MCVS_ILVERIFY or ~/.dotnet/tools/ilverify): skipped");
                return;
            }
            string dir = Support.NewTempDir("ilverify");
            try
            {
                string patched = Path.Combine(dir, "EngineBridge.dll");
                File.WriteAllBytes(patched, Patch(File.ReadAllBytes(Path.Combine(pc, "EngineBridge.dll")), Marker()));
                foreach (string file in new[] { Path.Combine(pc, "EngineBridge.dll"), patched })
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    foreach (string a in new[] { file, "-r", Path.Combine(Support.Net45ReferenceAssemblies, "mscorlib.dll"), "-r", Path.Combine(pc, "Gameloft.Engine.dll"),
                                                 "--system-module", "mscorlib", "--statistics" })
                        psi.ArgumentList.Add(a);
                    string dotnetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
                    if (Directory.Exists(dotnetRoot) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT"))) psi.Environment["DOTNET_ROOT"] = dotnetRoot;
                    using (var p = System.Diagnostics.Process.Start(psi))
                    {
                        string text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                        Assert.True(p.WaitForExit(120000));
                        output.WriteLine(text.Trim());
                        Assert.True(p.ExitCode == 0, "ilverify exit code " + p.ExitCode + ":\n" + text);
                        Assert.Contains("Verified.", text);
                    }
                }
            }
            finally
            {
                Support.DeleteDir(dir);
            }
        }

        [Fact]
        public void TheMarkerPathMustBeAbsolute()
        {
            Assert.Throws<ArgumentException>(() => new EngineBridgeMarkerPatch("marker.txt"));
            Assert.Throws<ArgumentException>(() => new EngineBridgeMarkerPatch(""));
        }
    }
}
