using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;
using GameContext = McvsOffline.Embed.Tests.Patcher.RealGamePatchTests.GameContext;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using OpCodes = Mono.Cecil.Cil.OpCodes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// The 'debloat' patch applied to the player's REAL Gameloft.Engine.dll (MCVS_REFS) and executed on net8 next to the
    /// unpatched one: Crashlytics can no longer start, no push token is requested while local notifications work as before,
    /// Facebook and Play Games are not initialised at menu start, and a login tap on them ends with Failed instead of a lone
    /// Connecting. Two kinds of runs: without libnativeengine (a patched path that still reached native code would throw
    /// DllNotFoundException) and, when a C compiler is found, against a small stand-in libnativeengine that records the calls
    /// the real one would get. Skipped without the game's assemblies unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    [Collection("GameHooks")]
    public class RealDebloatPatchTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const string Engine = "Gameloft.Engine.dll";

        private readonly ITestOutputHelper output;

        public RealDebloatPatchTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private string Refs()
        {
            string dir = TestEnvironment.GameAssemblies;
            if (dir != null && File.Exists(Path.Combine(dir, Engine))) return dir;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but MCVS_REFS has no Gameloft.Engine.dll");
            output.WriteLine("the game's assemblies are not available (MCVS_REFS): skipped");
            return null;
        }

        private static readonly object PatchGate = new object();
        private static byte[] server;
        private static Dictionary<string, byte[]> patched;
        private static List<string> patchNotes;

        /// <summary>The stand-in server and Gameloft.Engine.dll patched with 'debloat' alone (patched once for the class).</summary>
        private static Dictionary<string, byte[]> Patched(string refs, out byte[] serverImage)
        {
            lock (PatchGate)
            {
                if (patched == null)
                {
                    server = FixtureCompiler.Compile("McvsOffline.Server", RealGamePatchTests.HookServerSource);
                    var notes = new List<string>();
                    patched = RealGamePatchTests.PatchReal(refs, server, notes, new DebloatEnginePatch());
                    patchNotes = notes;
                }
                serverImage = server;
                return patched;
            }
        }

        /// <summary>The nine patched methods: (type, name, parameter count).</summary>
        private static readonly (string Type, string Name, int Parameters)[] PatchedMethods =
        {
            (DebloatEnginePatch.CrashlyticsType, "Start", 0), (DebloatEnginePatch.CrashlyticsType, "Start_Native", 0),
            (DebloatEnginePatch.CrashlyticsType, "Native_Start", 0),
            (DebloatEnginePatch.NotificationType, "ShouldRegister", 0),
            (DebloatEnginePatch.NotificationSystemType, "RegisterForRemoteNotifications", 0),
            (DebloatEnginePatch.NotificationSystemType, "RegisterAsync", 1),
            (DebloatEnginePatch.SocialLibType, "Init", 3), (DebloatEnginePatch.SocialLibType, "Login", 2),
            (DebloatEnginePatch.SocialLibType, "TriggerAutoLogin", 2),
        };

        /// <summary>The type the patch adds, and its three methods: (name, parameter count).</summary>
        private const string Helper = "Gameloft.Engine." + DebloatEnginePatch.HelperType;

        private static readonly (string Name, int Parameters)[] HelperMethods = { ("Post", 2), ("Fail", 1), ("FailLater", 0) };

        private static MethodDefinition Find(ModuleDefinition module, string type, string name, int parameters)
        {
            return module.GetType(type).Methods.Single(m => m.Name == name && m.Parameters.Count == parameters && m.HasBody);
        }

        private static MethodDefinition MoveNext(MethodDefinition asyncMethod)
        {
            CustomAttribute a = asyncMethod.CustomAttributes.Single(c => c.AttributeType.Name == "AsyncStateMachineAttribute");
            return ((TypeReference)a.ConstructorArguments[0].Value).Resolve().Methods.Single(m => m.Name == "MoveNext");
        }

        // ------------------------------------------------------------------ IL

        [Fact]
        public void TheShippedMethodsHaveTheVerifiedFingerprints()
        {
            string refs = Refs();
            if (refs == null) return;
            using (var module = ModuleDefinition.ReadModule(Path.Combine(refs, Engine)))
            {
                string[] expected =
                {
                    DebloatEnginePatch.StartFingerprint, DebloatEnginePatch.StartNativeFingerprint, DebloatEnginePatch.NativeStartFingerprint,
                    DebloatEnginePatch.ShouldRegisterFingerprint, DebloatEnginePatch.RegisterForRemoteFingerprint,
                    DebloatEnginePatch.RegisterAsyncFingerprint, DebloatEnginePatch.InitFingerprint, DebloatEnginePatch.LoginFingerprint,
                    DebloatEnginePatch.TriggerAutoLoginFingerprint,
                };
                for (int i = 0; i < PatchedMethods.Length; i++)
                {
                    var (type, name, n) = PatchedMethods[i];
                    Assert.Equal(expected[i], IlPatch.Fingerprint(Find(module, type, name, n)));
                }
                // Unchanged methods the changes call or rely on: Register() (-> RegisterForRemoteNotifications()), the managed
                // CancelAllLocalNotifications(), RegisterAsync's lambda (this.Register()) and GameSynchronizationContext.Post.
                TypeDefinition system = module.GetType(DebloatEnginePatch.NotificationSystemType);
                Assert.Equal(DebloatEnginePatch.RegisterFingerprint, IlPatch.Fingerprint(Find(module, DebloatEnginePatch.NotificationSystemType, "Register", 0)));
                Assert.Equal(DebloatEnginePatch.CancelAllFingerprint, IlPatch.Fingerprint(Find(module, DebloatEnginePatch.NotificationSystemType, "CancelAllLocalNotifications", 0)));
                Assert.Equal(DebloatEnginePatch.RegisterLambdaFingerprint, IlPatch.Fingerprint(system.Methods.Single(m => m.Name.StartsWith("<RegisterAsync>b__"))));
                Assert.Equal(DebloatEnginePatch.ContextPostFingerprint, IlPatch.Fingerprint(Find(module, DebloatEnginePatch.GameContextType, "Post", 2)));
                Assert.Null(module.GetType(Helper));
                Assert.Equal(DebloatEnginePatch.RegisterAsyncBodyFingerprint, IlPatch.Fingerprint(MoveNext(Find(module, DebloatEnginePatch.NotificationSystemType, "RegisterAsync", 1))));
                Assert.Equal(DebloatEnginePatch.RegisterRemoteBodyFingerprint,
                             IlPatch.Fingerprint(MoveNext(Find(module, DebloatEnginePatch.NotificationType, "RegisterForRemoteNotificationsAsync", 1))));
                Assert.Equal(DebloatEnginePatch.InitBodyFingerprint, IlPatch.Fingerprint(MoveNext(Find(module, DebloatEnginePatch.SocialLibType, "Init", 3))));
                Assert.Equal(DebloatEnginePatch.LoginBodyFingerprint, IlPatch.Fingerprint(MoveNext(Find(module, DebloatEnginePatch.SocialLibType, "Login", 2))));
                Assert.Equal(DebloatEnginePatch.TriggerAutoLoginBodyFingerprint, IlPatch.Fingerprint(MoveNext(Find(module, DebloatEnginePatch.SocialLibType, "TriggerAutoLogin", 2))));
                Assert.Equal(DebloatEnginePatch.InitSocialBodyFingerprint, IlPatch.Fingerprint(MoveNext(Find(module, DebloatEnginePatch.SocialType, "InitSocial", 1))));
                Assert.Equal(DebloatEnginePatch.LoginImplBodyFingerprint, IlPatch.Fingerprint(MoveNext(Find(module, DebloatEnginePatch.SocialType, "LoginImpl", 2))));

                // The values the patch reads from the enums (and the native side's Sns numbering assumes).
                Assert.Equal(new[] { "Facebook", "GameCenter", "GooglePlayGames", "Msn", "SinaWeibo" },
                             module.GetType(DebloatEnginePatch.SnsType).Fields.Where(f => f.IsStatic).OrderBy(f => (int)f.Constant).Select(f => f.Name));
                TypeDefinition result = module.GetType(DebloatEnginePatch.SnsResultType);
                Assert.Equal(1, result.Fields.Single(f => f.Name == "NotSent").Constant);
                Assert.Equal(3, result.Fields.Single(f => f.Name == "Failed").Constant);
            }
        }

        [Fact]
        public void ThePatchedIlHasTheExpectedShapeAndNothingElseChanges()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out _);
            foreach (string n in patchNotes) output.WriteLine(n);
            Assert.Equal(7, patchNotes.Count);
            Assert.Equal(new[] { Engine }, images.Keys);

            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(refs);
                foreach (string d in resolver.GetSearchDirectories().Where(d => d != refs).ToList()) resolver.RemoveSearchDirectory(d);
                var reader = new ReaderParameters { AssemblyResolver = resolver };
                using (var original = ModuleDefinition.ReadModule(Path.Combine(refs, Engine), reader))
                using (var module = ModuleDefinition.ReadModule(new MemoryStream(images[Engine]), reader))
                {
                    foreach (var (type, name, n) in PatchedMethods)
                    {
                        MethodDefinition before = Find(original, type, name, n), after = Find(module, type, name, n);
                        var code = after.Body.Instructions;
                        int prologue;
                        switch (name)
                        {
                            case "ShouldRegister":
                                Assert.Equal(OpCodes.Ldc_I4_0, code[0].OpCode);
                                prologue = 1;
                                break;
                            case "RegisterForRemoteNotifications":
                                // ldarg.0; callvirt CancelAllLocalNotifications(); ret
                                Assert.Equal(new[] { Code.Ldarg_0, Code.Callvirt }, code.Take(2).Select(i => i.OpCode.Code));
                                Assert.Equal("System.Void " + DebloatEnginePatch.NotificationSystemType + "::CancelAllLocalNotifications()", ((MethodReference)code[1].Operand).FullName);
                                prologue = 2;
                                break;
                            case "RegisterAsync":
                                // ldarg.0; ldftn <RegisterAsync>b__15_0; newobj SendOrPostCallback; ldnull; call McvsDebloat.Post; pop;
                                // call Task.get_CompletedTask(); ret
                                Assert.Equal(new[] { Code.Ldarg_0, Code.Ldftn, Code.Newobj, Code.Ldnull, Code.Call, Code.Pop, Code.Call }, code.Take(7).Select(i => i.OpCode.Code));
                                MethodDefinition lambda = ((MethodReference)code[1].Operand).Resolve();
                                Assert.StartsWith("<RegisterAsync>b__", lambda.Name);
                                Assert.Equal(DebloatEnginePatch.NotificationSystemType, lambda.DeclaringType.FullName);
                                Assert.Equal(DebloatEnginePatch.RegisterLambdaFingerprint, IlPatch.Fingerprint(lambda));
                                Assert.Equal("System.Void System.Threading.SendOrPostCallback::.ctor(System.Object,System.IntPtr)", ((MethodReference)code[2].Operand).FullName);
                                FromTheGamesCorlib((MethodReference)code[2].Operand, refs);
                                Assert.Equal("System.Boolean " + Helper + "::Post(System.Threading.SendOrPostCallback,System.Object)", ((MethodReference)code[4].Operand).FullName);
                                var completed = (MethodReference)code[6].Operand;
                                Assert.Equal("System.Threading.Tasks.Task System.Threading.Tasks.Task::get_CompletedTask()", completed.FullName);
                                FromTheGamesCorlib(completed, refs);
                                prologue = 7;
                                break;
                            case "Init":
                                // ldarg.1; ldc.i4.0 (Facebook); beq.s SKIP; ldarg.1; ldc.i4.2 (GooglePlayGames); bne.un.s ORIG;
                                // SKIP: ldc.i4.1 (NotSent); call Task.FromResult<SnsResult>; ret; ORIG: the shipped stub
                                Assert.Equal(new[] { Code.Ldarg_1, Code.Ldc_I4_0, Code.Beq_S, Code.Ldarg_1, Code.Ldc_I4_2, Code.Bne_Un_S, Code.Ldc_I4_1, Code.Call },
                                             code.Take(8).Select(i => i.OpCode.Code));
                                Assert.Same(code[6], code[2].Operand);
                                Assert.Same(code[9], code[5].Operand);
                                var fromResult = (GenericInstanceMethod)code[7].Operand;
                                Assert.Equal("FromResult", fromResult.Name);
                                Assert.Equal("System.Threading.Tasks.Task", fromResult.DeclaringType.FullName);
                                Assert.Equal(DebloatEnginePatch.SnsResultType, Assert.Single(fromResult.GenericArguments).FullName);
                                Assert.Equal("System.Threading.Tasks.Task`1<!!0>", fromResult.ElementMethod.ReturnType.FullName);
                                FromTheGamesCorlib(fromResult.ElementMethod, refs);
                                prologue = 8;
                                break;
                            case "Login":
                            case "TriggerAutoLogin":
                                // ldarg.1; ldc.i4.0 (Facebook); beq.s SKIP; ldarg.1; ldc.i4.2 (GooglePlayGames); bne.un.s ORIG;
                                // SKIP: call McvsDebloat.FailLater(); ret; ORIG: the shipped stub
                                Assert.Equal(new[] { Code.Ldarg_1, Code.Ldc_I4_0, Code.Beq_S, Code.Ldarg_1, Code.Ldc_I4_2, Code.Bne_Un_S, Code.Call },
                                             code.Take(7).Select(i => i.OpCode.Code));
                                Assert.Same(code[6], code[2].Operand);
                                Assert.Same(code[8], code[5].Operand);
                                Assert.Equal("System.Threading.Tasks.Task`1<" + DebloatEnginePatch.SnsResultType + "> " + Helper + "::FailLater()",
                                             ((MethodReference)code[6].Operand).FullName);
                                prologue = 7;
                                break;
                            default:
                                prologue = 0;
                                break;
                        }
                        Assert.Equal(OpCodes.Ret, code[prologue].OpCode);
                        // The shipped body follows unchanged (unreachable), operand for operand.
                        Assert.Equal(before.Body.Instructions.Select(Text), code.Skip(prologue + 1).Select(Text));
                        Assert.Equal(before.Body.Variables.Select(v => v.VariableType.FullName), after.Body.Variables.Select(v => v.VariableType.FullName));
                        Assert.Empty(after.Body.ExceptionHandlers);
                        Assert.Equal(before.Attributes, after.Attributes);
                    }

                    HelperHasTheExpectedShape(module.GetType(Helper), refs);

                    // Nothing else in the assembly changed: the same types and members but the helper type, and every other body
                    // has its shipped fingerprint.
                    var patchedNames = PatchedMethods.Select(p => Find(original, p.Type, p.Name, p.Parameters).FullName).OrderBy(k => k, StringComparer.Ordinal).ToList();
                    List<(string Name, string Body)> Bodies(ModuleDefinition m) =>
                        m.GetTypes().Where(t => t.FullName != Helper).SelectMany(t => t.Methods).Where(x => x.HasBody).Select(x => (x.FullName, IlPatch.Fingerprint(x))).ToList();
                    List<(string Name, string Body)> was = Bodies(original), now = Bodies(module);
                    Assert.Equal(was.Select(b => b.Name), now.Select(b => b.Name));
                    Assert.Equal(patchedNames, was.Zip(now, (a, b) => (a.Name, Changed: a.Body != b.Body)).Where(x => x.Changed).Select(x => x.Name)
                                                  .OrderBy(k => k, StringComparer.Ordinal));
                    Assert.Equal(original.GetTypes().Select(t => t.FullName + "/" + t.Fields.Count + "/" + t.Methods.Count),
                                 module.GetTypes().Where(t => t.FullName != Helper).Select(t => t.FullName + "/" + t.Fields.Count + "/" + t.Methods.Count));
                    Assert.Equal(original.Types.Count + 1, module.Types.Count);
                    Assert.Equal(original.AssemblyReferences.Select(a => a.FullName), module.AssemblyReferences.Select(a => a.FullName));
                    output.WriteLine(now.Count + " method bodies compared, " + patchedNames.Count + " changed, " + HelperMethods.Length + " added");
                }
            }
        }

        /// <summary>
        /// The added <c>Gameloft.Engine.McvsDebloat</c>: a static internal class of three methods, instruction for instruction, every
        /// BCL member it uses from the game's own mscorlib and every engine member the shipped one.
        /// </summary>
        private static void HelperHasTheExpectedShape(TypeDefinition helper, string refs)
        {
            Assert.NotNull(helper);
            Assert.Equal(TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, helper.Attributes);
            Assert.Equal("System.Object", helper.BaseType.FullName);
            Assert.Empty(helper.Fields);
            Assert.Empty(helper.NestedTypes);
            Assert.Equal(HelperMethods.Select(m => m.Name).OrderBy(x => x), helper.Methods.Select(m => m.Name).OrderBy(x => x));
            string tcs = "System.Threading.Tasks.TaskCompletionSource`1<" + DebloatEnginePatch.SnsResultType + ">";
            var expected = new Dictionary<string, (string Signature, string[] Code)>
            {
                ["Post"] = ("System.Boolean " + Helper + "::Post(System.Threading.SendOrPostCallback,System.Object)", new[]
                {
                    "call " + DebloatEnginePatch.ApplicationType + " " + DebloatEnginePatch.ApplicationType + "::get_Instance()", "dup", "brtrue.s -> callvirt",
                    "pop", "ldc.i4.0", "ret",
                    "callvirt " + DebloatEnginePatch.GameContextType + " " + DebloatEnginePatch.ApplicationType + "::get_Context()", "dup", "brtrue.s -> ldarg.0",
                    "pop", "ldc.i4.0", "ret",
                    "ldarg.0", "ldarg.1", "callvirt System.Void System.Threading.SynchronizationContext::Post(System.Threading.SendOrPostCallback,System.Object)",
                    "ldc.i4.1", "ret",
                }),
                ["Fail"] = ("System.Void " + Helper + "::Fail(System.Object)", new[]
                {
                    "ldarg.0", "castclass " + tcs, "ldc.i4.3", "callvirt System.Boolean " + tcs + "::TrySetResult(!0)", "pop", "ret",
                }),
                ["FailLater"] = ("System.Threading.Tasks.Task`1<" + DebloatEnginePatch.SnsResultType + "> " + Helper + "::FailLater()", new[]
                {
                    "newobj System.Void " + tcs + "::.ctor()", "stloc.0", "ldnull", "ldftn System.Void " + Helper + "::Fail(System.Object)",
                    "newobj System.Void System.Threading.SendOrPostCallback::.ctor(System.Object,System.IntPtr)", "ldloc.0",
                    "call System.Boolean " + Helper + "::Post(System.Threading.SendOrPostCallback,System.Object)", "brtrue.s -> ldloc.0",
                    "ldloc.0", "call System.Void " + Helper + "::Fail(System.Object)",
                    "ldloc.0", "callvirt System.Threading.Tasks.Task`1<!0> " + tcs + "::get_Task()", "ret",
                }),
            };
            foreach (MethodDefinition m in helper.Methods)
            {
                Assert.True(m.IsStatic, m.FullName);
                Assert.Equal(m.Name == "Fail" ? MethodAttributes.Private : MethodAttributes.Assembly, m.Attributes & MethodAttributes.MemberAccessMask);
                Assert.Equal(expected[m.Name].Signature, m.FullName);
                Assert.Equal(expected[m.Name].Code, m.Body.Instructions.Select(i => Text(i).TrimEnd()));
                Assert.Empty(m.Body.ExceptionHandlers);
                Assert.Equal(m.Name == "FailLater" ? new[] { tcs } : new string[0], m.Body.Variables.Select(v => v.VariableType.FullName));
                foreach (MethodReference r in m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>())
                {
                    MethodDefinition resolved = r.Resolve();
                    Assert.NotNull(resolved);
                    if (r.DeclaringType.Namespace.StartsWith("System", StringComparison.Ordinal)) FromTheGamesCorlib(r, refs);
                    else Assert.Equal("Gameloft.Engine", resolved.Module.Assembly.Name.Name);
                }
            }
        }

        /// <summary>An instruction without its offset (the prologue moved them): opcode and what it refers to.</summary>
        private static string Text(Instruction i)
        {
            object o = i.Operand;
            string operand = o is Instruction target ? "-> " + target.OpCode
                           : o is VariableDefinition v ? "V_" + v.Index
                           : o is ParameterDefinition p ? "A_" + p.Index
                           : o is MemberReference r ? r.FullName : Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture);
            return i.OpCode + " " + operand;
        }

        /// <summary>The reference resolves into the game's own (trimmed) mscorlib, not into net8's.</summary>
        private static void FromTheGamesCorlib(MethodReference reference, string refs)
        {
            MethodDefinition resolved = reference.Resolve();
            Assert.NotNull(resolved);
            Assert.Equal("mscorlib", resolved.Module.Assembly.Name.Name);
            Assert.StartsWith(Path.GetFullPath(refs), Path.GetFullPath(resolved.Module.FileName));
        }

        [Fact]
        public void EveryPatchedMethodJitsWithTheDefaultPatchesAndASecondRunIsRefused()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);
            // Alone, and in the patcher's run of every default Game.dll / Gameloft.Engine.dll patch (debloat last).
            Dictionary<string, byte[]> all = RealGamePatchTests.PatchReal(refs, serverImage, null, RealGamePatchTests.DefaultGamePatches);
            foreach (Dictionary<string, byte[]> set in new[] { images, all })
            {
                using (var game = new GameContext(refs, set, serverImage))
                {
                    Assembly engine = game.Get("Gameloft.Engine");
                    foreach (var (type, name, n) in PatchedMethods.Concat(HelperMethods.Select(h => (Helper, h.Name, h.Parameters))))
                    {
                        MethodInfo m = engine.GetType(type, true).GetMethods(Any | BindingFlags.DeclaredOnly).Single(x => x.Name == name && x.GetParameters().Length == n);
                        RuntimeHelpers.PrepareMethod(m.MethodHandle);   // InvalidProgramException on bad IL
                    }
                }
            }
            output.WriteLine("JIT ok: " + PatchedMethods.Length + " patched and " + HelperMethods.Length + " added methods, alone and with the default patches");

            // Patched once: refused (the helper type is there), and with it renamed the fingerprints no longer match.
            string dir = TestEnvironment.NewTempDir("debloat-repatch");
            foreach (string f in Directory.GetFiles(refs, "*.dll")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            File.WriteAllBytes(Path.Combine(dir, Engine), images[Engine]);
            Assert.Contains("already patched", Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(dir, serverImage, null, new DebloatEnginePatch())).Message);
            using (var resolver = new DefaultAssemblyResolver())
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(images[Engine]), new ReaderParameters { InMemory = true, AssemblyResolver = resolver }))
            {
                resolver.AddSearchDirectory(refs);
                module.GetType(Helper).Name = DebloatEnginePatch.HelperType + "Renamed";
                module.Write(Path.Combine(dir, Engine));
            }
            Assert.Contains("IL fingerprint", Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(dir, serverImage, null, new DebloatEnginePatch())).Message);

            // One changed instruction in a body the patch only relies on (InitSocial's loop) is refused too.
            string changed = TestEnvironment.NewTempDir("debloat-changed");
            foreach (string f in Directory.GetFiles(refs, "*.dll")) File.Copy(f, Path.Combine(changed, Path.GetFileName(f)));
            using (var resolver = new DefaultAssemblyResolver())
            using (var module = ModuleDefinition.ReadModule(Path.Combine(refs, Engine), new ReaderParameters { InMemory = true, AssemblyResolver = resolver }))
            {
                resolver.AddSearchDirectory(refs);
                MethodDefinition loop = MoveNext(Find(module, DebloatEnginePatch.SocialType, "InitSocial", 1));
                Instruction trigger = loop.Body.Instructions.First(i => i.OpCode == OpCodes.Ldc_I4_1);
                trigger.OpCode = OpCodes.Ldc_I4_0;
                module.Write(Path.Combine(changed, Engine));
            }
            string message = Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(changed, serverImage, null, new DebloatEnginePatch())).Message;
            Assert.Contains("InitSocial", message);
            Assert.Contains("IL fingerprint", message);
        }

        [Fact]
        public void IlVerifyAcceptsThePatchedMethods()
        {
            string refs = Refs();
            if (refs == null) return;
            string tool = Environment.GetEnvironmentVariable("MCVS_ILVERIFY");
            if (string.IsNullOrEmpty(tool)) tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "ilverify");
            if (!File.Exists(tool))
            {
                output.WriteLine("ilverify not found (MCVS_ILVERIFY or ~/.dotnet/tools/ilverify): skipped");
                return;
            }
            Dictionary<string, byte[]> images = Patched(refs, out _);
            string dir = TestEnvironment.NewTempDir("debloat-ilverify");
            string patchedFile = Path.Combine(dir, Engine);
            File.WriteAllBytes(patchedFile, images[Engine]);
            string shipped = Path.Combine(refs, Engine);
            (int Exit, string Text) Verify(string file, IEnumerable<string> extra)
            {
                var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                psi.ArgumentList.Add(file);
                foreach (string r in Directory.GetFiles(refs, "*.dll").Where(f => Path.GetFileName(f) != Engine))
                {
                    psi.ArgumentList.Add("-r");
                    psi.ArgumentList.Add(r);
                }
                foreach (string a in extra) psi.ArgumentList.Add(a);
                string dotnetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
                if (Directory.Exists(dotnetRoot) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT"))) psi.Environment["DOTNET_ROOT"] = dotnetRoot;
                using (Process p = Process.Start(psi))
                {
                    var errors = new System.Text.StringBuilder();
                    p.ErrorDataReceived += (s, a) => { if (a.Data != null) lock (errors) errors.AppendLine(a.Data); };
                    p.BeginErrorReadLine();
                    string stdout = p.StandardOutput.ReadToEnd();
                    Assert.True(p.WaitForExit(300000), "ilverify did not finish");
                    p.WaitForExit();
                    lock (errors) return (p.ExitCode, stdout.Replace(file, "<input>") + errors);
                }
            }

            // The nine patched methods and the three added ones (ILVerify matches the patterns against
            // "[Assembly]Namespace.Type.Method"): no finding.
            var includes = new List<string>
            {
                "-i", @"\]Gameloft\.Engine\.Crashlytics\.(Start|Start_Native|Native_Start)$", "-i", @"\]Gameloft\.Engine\.NotificationComponent\.ShouldRegister$",
                "-i", @"\]Gameloft\.Engine\.NativeNotificationSystem\.(RegisterForRemoteNotifications|RegisterAsync)$",
                "-i", @"\]Gameloft\.Engine\.SocialLib_private\.(Init|Login|TriggerAutoLogin)$",
                "-i", @"\]Gameloft\.Engine\." + DebloatEnginePatch.HelperType + @"\.(Post|Fail|FailLater)$", "--statistics",
            };
            foreach (string file in new[] { shipped, patchedFile })
            {
                var (exit, text) = Verify(file, includes);
                output.WriteLine((file == patchedFile ? "patched: " : "original: ") + text.Trim());
                Assert.True(exit == 0, "ilverify exit code " + exit + ":\n" + text);
                // the include patterns matched all nine patched methods, and in the patched file the three added ones
                Assert.Contains("Methods verified: " + (file == patchedFile ? PatchedMethods.Length + HelperMethods.Length : PatchedMethods.Length), text);
            }

            // The whole assembly: the shipped engine has findings of its own (unsafe pointer code and the like); the patched
            // one has exactly the same, none of them in a patched method.
            static List<string> Findings(string text) => text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("[IL]") || l.StartsWith("[MD]"))
                                                                .OrderBy(l => l, StringComparer.Ordinal).ToList();
            List<string> before = Findings(Verify(shipped, new string[0]).Text), after = Findings(Verify(patchedFile, new string[0]).Text);
            output.WriteLine("whole assembly: " + before.Count + " shipped findings, " + after.Count + " after patching");
            Assert.Equal(before, after);
            Assert.DoesNotContain(after, l => PatchedMethods.Any(m => l.Contains(m.Type + "::" + m.Name + "(")) || l.Contains(Helper + "::"));
        }

        // ------------------------------------------------------------------ Crashlytics

        [Fact]
        public void CrashlyticsCanNoLongerStart()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);

            // Without libnativeengine: the shipped methods reach native code, the patched ones return at once.
            foreach (bool isPatched in new[] { false, true })
            {
                using (var e = new EngineScene(refs, isPatched ? images : new Dictionary<string, byte[]>(), serverImage, null))
                {
                    object crashlytics = e.New(DebloatEnginePatch.CrashlyticsType);
                    var start = e.Run(crashlytics, "Start");
                    var startNative = e.RunStatic(DebloatEnginePatch.CrashlyticsType, "Start_Native");
                    var nativeStart = e.Run(crashlytics, "Native_Start");
                    if (isPatched)
                    {
                        Assert.Null(start.Error);
                        Assert.Null(startNative.Error);
                        Assert.Null(nativeStart.Error);
                    }
                    else
                    {
                        Assert.IsType<DllNotFoundException>(start.Error);        // get_IsAvailable
                        Assert.IsType<DllNotFoundException>(startNative.Error);
                        Assert.IsType<ObjectDisposedException>(nativeStart.Error);   // no native instance pointer
                    }
                    Assert.False((bool)e.Get(crashlytics, "IsWaitingForAPI"));
                    Assert.False((bool)e.Get(crashlytics, "<IsReady>k__BackingField"));
                }
            }

            string stub = NativeStub.Library(output);
            if (stub == null) return;
            foreach (bool isPatched in new[] { false, true })
            {
                using (var e = new EngineScene(refs, isPatched ? images : new Dictionary<string, byte[]>(), serverImage, stub))
                {
                    NativeStub.Reset();
                    object crashlytics = e.Type(DebloatEnginePatch.CrashlyticsType).GetProperty("Instance").GetValue(null);   // the real constructor
                    Assert.Null(e.Run(crashlytics, "Start").Error);
                    Assert.Null(e.Run(crashlytics, "Native_Start").Error);
                    Assert.Null(e.RunStatic(DebloatEnginePatch.CrashlyticsType, "Start_Native").Error);
                    int[] starting = NativeStub.Events();
                    Assert.Null(e.Run(crashlytics, "LogMessage", "debloat").Error);
                    int[] logging = NativeStub.Events().Skip(starting.Length).ToArray();
                    output.WriteLine((isPatched ? "patched" : "original") + ": start " + string.Join(",", starting) + "; log " + string.Join(",", logging));
                    if (isPatched)
                    {
                        Assert.Empty(starting);                                   // not even Crashlytics_GetIsAvailable or Fabric
                        Assert.False((bool)e.Get(crashlytics, "IsWaitingForAPI"));
                    }
                    else
                    {
                        // IsAvailable, Fabric's constructor and IsInitialized, then both native start exports.
                        Assert.Equal(new[] { NativeStub.CrashlyticsIsAvailable, NativeStub.FabricCallback, NativeStub.FabricIsInitialized,
                                             NativeStub.CrashlyticsStartNative, NativeStub.CrashlyticsNativeStart, NativeStub.CrashlyticsStartNative }, starting);
                        Assert.True((bool)e.Get(crashlytics, "IsWaitingForAPI"));
                    }
                    Assert.Equal(new[] { NativeStub.CrashlyticsLogMessage }, logging);   // logging is unchanged
                    Assert.False((bool)e.Get(crashlytics, "<IsReady>k__BackingField"));
                }
            }
        }

        // ------------------------------------------------------------------ push

        [Fact]
        public void NoPushTokenIsRequestedAndLocalNotificationsStillWork()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);

            foreach (bool isPatched in new[] { false, true })
            {
                using (var e = new EngineScene(refs, isPatched ? images : new Dictionary<string, byte[]>(), serverImage, null))
                {
                    // NotificationComponent.ShouldRegister: the shipped one asks the validator (none: true; the default one reads
                    // the World), the patched one says no without asking.
                    object component = e.New(DebloatEnginePatch.NotificationType);
                    var noValidator = e.Run(component, "ShouldRegister");
                    e.Set(component, "<Validator>k__BackingField", Activator.CreateInstance(e.Type(DebloatEnginePatch.NotificationType + "+DefaultValidator")));
                    var defaultValidator = e.Run(component, "ShouldRegister");
                    e.Set(component, "<Validator>k__BackingField", null);
                    var registration = e.Run(component, "RegisterForRemoteNotificationsAsync", CancellationToken.None);
                    var task = (Task<bool>)registration.Value;
                    if (isPatched)
                    {
                        Assert.False((bool)noValidator.Value);
                        Assert.False((bool)defaultValidator.Value);
                        // The shipped "validation failure" path: a finished false before the connection wait (no World needed).
                        Assert.True(task.IsCompletedSuccessfully);
                        Assert.Equal("False", e.ResultOf(task));
                    }
                    else
                    {
                        Assert.True((bool)noValidator.Value);
                        Assert.IsType<NullReferenceException>(defaultValidator.Error);
                        Assert.False(task.IsCompletedSuccessfully);   // went on to the World and the connection gate
                    }

                    // NativeNotificationSystem: Register() still reaches libnativeengine (which export: the runs against the stand-in
                    // below), and RegisterAsync no longer waits for a token. Without a GameApplication it posts nothing.
                    object system = e.New(DebloatEnginePatch.NotificationSystemType);
                    e.Set(system, "self", new IntPtr(0x3000));
                    Assert.IsType<DllNotFoundException>(e.Run(system, "Register").Error);
                    Assert.IsType<DllNotFoundException>(e.Run(system, "RegisterForRemoteNotifications").Error);
                    var registerAsync = (Task)e.Run(system, "RegisterAsync", CancellationToken.None).Value;
                    var cancelled = (Task)e.Run(system, "RegisterAsync", new CancellationToken(true)).Value;
                    if (isPatched)
                    {
                        Assert.Same(Task.CompletedTask, registerAsync);
                        Assert.Same(Task.CompletedTask, cancelled);
                    }
                    else
                    {
                        // GameApplication.Instance.Context.Post(...) first: no GameApplication here.
                        Assert.IsType<NullReferenceException>(registerAsync.Exception?.InnerException);
                        Assert.IsType<NullReferenceException>(cancelled.Exception?.InnerException);
                    }
                    Assert.Null(e.Get(system, "DeviceTokenGate"));
                }
            }

            string stub = NativeStub.Library(output);
            if (stub == null) return;
            var local = new List<int[]>();
            foreach (bool isPatched in new[] { false, true })
            {
                using (var e = new EngineScene(refs, isPatched ? images : new Dictionary<string, byte[]>(), serverImage, stub))
                {
                    object system = e.New(DebloatEnginePatch.NotificationSystemType);
                    e.Set(system, "self", new IntPtr(0x3000));
                    // Register() -> RegisterForRemoteNotifications(): the shipped export (local-alarm purge, then the token request)
                    // before, now exactly the managed CancelAllLocalNotifications() (the same purge, no token request).
                    NativeStub.Reset();
                    Assert.Null(e.Run(system, "Register").Error);
                    Assert.Null(e.Run(system, "RegisterForRemoteNotifications").Error);
                    int native = isPatched ? NativeStub.CancelAllLocal : NativeStub.RegisterRemote;
                    Assert.Equal(new[] { native, native }, NativeStub.Events());

                    // RegisterAsync (the settings toggle) with a GameApplication: both post Register() to its Context; the shipped one
                    // then waits on DeviceTokenGate (locked: no token), the patched one is done. The posted item runs on the next
                    // turn of the queue, once.
                    object queue = e.GameLoop();
                    e.Set(system, "DeviceTokenGate", Activator.CreateInstance(e.FieldType(system, "DeviceTokenGate"), new object[] { true }));   // locked
                    NativeStub.Reset();
                    var toggled = (Task)e.Run(system, "RegisterAsync", CancellationToken.None).Value;
                    Assert.Empty(NativeStub.Events());
                    if (isPatched) Assert.Same(Task.CompletedTask, toggled);
                    else Assert.False(toggled.IsCompleted);
                    e.RunQueue(queue);
                    Assert.Equal(new[] { native }, NativeStub.Events());
                    e.RunQueue(queue);
                    Assert.Equal(new[] { native }, NativeStub.Events());
                    if (!isPatched) Assert.False(toggled.IsCompleted);
                    output.WriteLine((isPatched ? "patched" : "original") + " RegisterAsync: " + toggled.Status + ", native " + native);

                    // Local notifications (the Scheduler's INotificationSystem calls, the settings toggle's Start/Pause) reach
                    // libnativeengine exactly as before.
                    NativeStub.Reset();
                    Type argsType = e.Type("Gameloft.Engine.NotificationArgs");
                    object args = Activator.CreateInstance(argsType);
                    argsType.GetField("Delay").SetValue(args, TimeSpan.FromSeconds(90));
                    argsType.GetField("Type").SetValue(args, "absent_2days");
                    argsType.GetField("Message").SetValue(args, "come back");
                    MethodInfo schedule = e.Type(DebloatEnginePatch.NotificationSystemType).GetMethod("ScheduleLocalNotification", new[] { argsType, typeof(bool) });
                    schedule.Invoke(system, new[] { args, (object)true });
                    Assert.Null(e.Run(system, "CancelLocalNotification", "absent_2days").Error);
                    Assert.Null(e.Run(system, "CancelAllLocalNotifications").Error);
                    Assert.Null(e.Run(system, "Start").Error);
                    Assert.Null(e.Run(system, "Pause").Error);
                    local.Add(NativeStub.Events());
                }
            }
            output.WriteLine("local notifications: " + string.Join(",", local[1]));
            Assert.Equal(new[] { NativeStub.CancelLocal, NativeStub.ScheduleLocal + 90, NativeStub.CancelLocal, NativeStub.CancelAllLocal, NativeStub.StartSystem, NativeStub.PauseSystem },
                         local[0]);
            Assert.Equal(local[0], local[1]);
        }

        // ------------------------------------------------------------------ social

        [Fact]
        public void FacebookAndPlayGamesAreNotInitialisedAtMenuStart()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);

            // Without libnativeengine: SocialLib_private.Init answers NotSent for the two without native code, at once even with a
            // game loop (Init is not driven by a UI call); every other Sns still goes to the native Init.
            foreach (bool isPatched in new[] { false, true })
            {
                using (var e = new EngineScene(refs, isPatched ? images : new Dictionary<string, byte[]>(), serverImage, null))
                {
                    e.GameLoop();
                    object lib = e.SocialLib();
                    foreach (string sns in new[] { "Facebook", "GameCenter", "GooglePlayGames", "Msn", "SinaWeibo" })
                    {
                        Task task = e.Result(e.Run(lib, "Init", e.Sns(sns), true, CancellationToken.None));
                        if (isPatched && (sns == "Facebook" || sns == "GooglePlayGames"))
                        {
                            Assert.True(task.IsCompletedSuccessfully, sns);
                            Assert.Equal("NotSent", e.ResultOf(task));
                        }
                        else
                        {
                            Assert.IsType<DllNotFoundException>(task.Exception?.InnerException);
                        }
                    }
                }
            }

            // SocialComponent.InitSocial, the menu-start loop, against the stand-in libnativeengine (every native Init refused, as
            // snsconfig.json refuses GameCenter, Msn and SinaWeibo on Android): the patched loop asks native to init the same
            // Sns minus Facebook and GooglePlayGames, and runs the same auto-login checks for all five.
            string stub = NativeStub.Library(output);
            if (stub == null) return;
            foreach (bool needsAutoLogin in new[] { false, true })
            {
                foreach (bool trigger in new[] { true, false })
                {
                    var runs = new List<int[]>();
                    foreach (bool isPatched in new[] { false, true })
                    {
                        using (var e = new EngineScene(refs, isPatched ? images : new Dictionary<string, byte[]>(), serverImage, stub))
                        {
                            e.SocialLib();
                            object social = e.Social(trigger);
                            NativeStub.Reset();
                            NativeStub.NeedsAutoLogin(needsAutoLogin);
                            Task task = e.Result(e.Run(social, "InitSocial", CancellationToken.None));
                            Assert.True(task.IsCompletedSuccessfully, task.Exception?.ToString());
                            runs.Add(NativeStub.Events());
                        }
                    }
                    int t = trigger ? 1 : 0;
                    int[] inits(int[] events) => events.Where(x => x >= NativeStub.Init && x < NativeStub.Init + 100).ToArray();
                    output.WriteLine("needsAutoLogin " + needsAutoLogin + ", ShouldTriggerAutoLogin " + trigger + ": original " + string.Join(",", runs[0]) + "; patched " + string.Join(",", runs[1]));
                    // Init(sns, sns != GameCenter || ShouldTriggerAutoLogin): Facebook 0, GameCenter 1, GooglePlayGames 2, Msn 3, SinaWeibo 4.
                    Assert.Equal(new[] { NativeStub.Init + 1, NativeStub.Init + 10 + t, NativeStub.Init + 21, NativeStub.Init + 31, NativeStub.Init + 41 }, inits(runs[0]));
                    Assert.Equal(new[] { NativeStub.Init + 10 + t, NativeStub.Init + 31, NativeStub.Init + 41 }, inits(runs[1]));
                    Assert.Equal(runs[0].Where(x => x != NativeStub.Init + 1 && x != NativeStub.Init + 21), runs[1]);
                    Assert.DoesNotContain(runs[1], x => x >= NativeStub.Login && x < NativeStub.Login + 100);
                }
            }
        }

        [Fact]
        public void ALoginTapFailsAfterTheUiShowsConnecting()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);

            // Without libnativeengine: Login/TriggerAutoLogin answer Failed for the two without native code: on the next turn of
            // GameApplication.Context (not before the call has returned), at once without a GameApplication or Context.
            foreach (string loop in new[] { "no GameApplication", "no Context", "game loop" })
            {
                foreach (bool isPatched in new[] { false, true })
                {
                    using (var e = new EngineScene(refs, isPatched ? images : new Dictionary<string, byte[]>(), serverImage, null))
                    {
                        object queue = loop == "game loop" ? e.GameLoop() : loop == "no Context" ? e.GameLoop(false) : null;
                        object lib = e.SocialLib();
                        foreach (string method in new[] { "Login", "TriggerAutoLogin" })
                        {
                            foreach (string sns in new[] { "Facebook", "GameCenter", "GooglePlayGames" })
                            {
                                string what = method + " " + sns + ", " + loop;
                                Task task = e.Result(e.Run(lib, method, e.Sns(sns), CancellationToken.None));
                                if (isPatched && sns != "GameCenter")
                                {
                                    if (queue != null)
                                    {
                                        Assert.False(task.IsCompleted, what);
                                        e.RunQueue(queue);
                                    }
                                    Assert.True(task.IsCompletedSuccessfully, what);
                                    Assert.Equal("Failed", e.ResultOf(task));
                                }
                                else
                                {
                                    Assert.IsType<DllNotFoundException>(task.Exception?.InnerException);   // IsInitialized
                                }
                            }
                        }
                    }
                }
            }

            // SocialComponent.Login (UIComponent.NativeLogin, the account settings' Facebook and Google Play buttons) on the
            // state the patched menu start leaves: the Sns not initialised. Shipped: Connecting, then "SNS not initialized"
            // thrown (NativeLogin swallows it) and no result event. Patched: Connecting, the call returns (the UI then shows
            // "Connecting..."), and Failed (the failure popup, which resets the button) arrives on a later turn: of
            // GameApplication.Context when the tap ran on its own turn ("queue"), of the World's context LoginImpl captured when
            // it ran in a World update ("world", where a menu's Flash calls run) after the GameApplication turn posted it there;
            // at once only without a GameApplication ("none").
            string stub = NativeStub.Library(output);
            if (stub == null) return;
            foreach (string loop in new[] { "none", "queue", "world" })
            {
                foreach (bool needsAutoLogin in new[] { false, true })
                {
                    foreach (string sns in new[] { "Facebook", "GooglePlayGames", "GameCenter" })
                    {
                        foreach (bool isPatched in new[] { false, true })
                        {
                            using (var e = new EngineScene(refs, isPatched ? images : new Dictionary<string, byte[]>(), serverImage, stub))
                            {
                                e.SocialLib();
                                object social = e.Social(true);
                                var events = new List<string>();
                                e.OnLogin(social, events);
                                object queue = loop == "none" ? null : e.GameLoop();
                                object world = loop == "world" ? e.NewQueue() : queue;
                                NativeStub.Reset();
                                NativeStub.NeedsAutoLogin(needsAutoLogin);
                                (object, Exception) Tap() => e.Run(social, "Login", e.Sns(sns), CancellationToken.None);
                                Task task = e.Result(world == null ? Tap() : e.Within(world, Tap));
                                string what = (isPatched ? "patched" : "original") + " " + sns + (needsAutoLogin ? " (auto)" : "") + ", " + loop;
                                int n = (int)e.Sns(sns);
                                bool deferred = isPatched && sns != "GameCenter" && queue != null;
                                if (deferred)
                                {
                                    // Native.Login has returned: Connecting only, still logging in, sounds muted.
                                    Assert.False(task.IsCompleted, what);
                                    Assert.Equal(new[] { "Connecting/manual" }, events);
                                    Assert.True((bool)e.Run(social, "IsLoggingIn", e.Sns(sns)).Value, what);
                                    Assert.Equal(1, NativeStub.Events().Count(x => x == NativeStub.SetGroupVolume));
                                    e.RunQueue(queue);   // the next frame's UpdateWorlds
                                    if (loop == "world")
                                    {
                                        Assert.False(task.IsCompleted, what);   // LoginImpl's continuation was posted to the World's context
                                        Assert.Equal(new[] { "Connecting/manual" }, events);
                                        e.RunQueue(world);   // that World's update
                                    }
                                }
                                int[] native = NativeStub.Events();
                                output.WriteLine(what + ": " + task.Status + " " + string.Join(" ", events) + "; native " + string.Join(",", native));
                                Assert.False((bool)e.Run(social, "IsLoggingIn", e.Sns(sns)).Value, what);
                                Assert.Contains(NativeStub.NeedsAuto + n, native);
                                Assert.DoesNotContain(native, x => x == NativeStub.Login + n || x == NativeStub.TriggerAuto + n);   // no native login request
                                if (isPatched && sns != "GameCenter")
                                {
                                    Assert.True(task.IsCompletedSuccessfully, what);
                                    Assert.Equal("Failed", e.ResultOf(task));
                                    Assert.Equal(new[] { "Connecting/manual", "Failed/manual" }, events);
                                    Assert.DoesNotContain(NativeStub.IsInitialized + n, native);
                                    Assert.Equal(2, native.Count(x => x == NativeStub.SetGroupVolume));   // muted and unmuted again
                                }
                                else
                                {
                                    Assert.Equal(TaskStatus.Faulted, task.Status);
                                    Assert.Equal("SNS not initialized", Assert.IsType<InvalidOperationException>(task.Exception.InnerException).Message);
                                    Assert.Equal(new[] { "Connecting/manual" }, events);
                                }

                                // Exactly once: later turns change nothing.
                                foreach (object q in new[] { queue, world }.Where(q => q != null)) e.RunQueue(q);
                                Assert.Equal(isPatched && sns != "GameCenter" ? 2 : 1, events.Count);
                                Assert.Equal(native, NativeStub.Events());
                            }
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------------ fixtures

        /// <summary>
        /// One load context (original or patched Gameloft.Engine.dll), optionally with libnativeengine resolved to the stand-in
        /// <see cref="NativeStub"/>; reflection helpers that unwrap TargetInvocationException.
        /// </summary>
        private sealed class EngineScene : IDisposable
        {
            private readonly GameContext context;

            public EngineScene(string refs, Dictionary<string, byte[]> images, byte[] serverImage, string stub)
            {
                context = new GameContext(refs, images, serverImage);
                Assembly = context.Get("Gameloft.Engine");
                if (stub != null)
                    NativeLibrary.SetDllImportResolver(Assembly, (name, asm, path) => name == "nativeengine" ? NativeLibrary.Load(stub) : IntPtr.Zero);
            }

            public Assembly Assembly { get; }

            public Type Type(string name) => Assembly.GetType(name, true);

            /// <summary>An uninitialised instance whose finalizer (a "was not disposed" warning, a native Destruct) never runs.</summary>
            public object New(string type) => Uninitialized(Type(type));

            private static object Uninitialized(Type type)
            {
                object o = RuntimeHelpers.GetUninitializedObject(type);
                GC.SuppressFinalize(o);
                return o;
            }

            public object Sns(string name) => Enum.Parse(Type(DebloatEnginePatch.SnsType), name);

            public (object Value, Exception Error) Run(object target, string name, params object[] args) => Invoke(target, target.GetType(), name, args);

            public (object Value, Exception Error) RunStatic(string type, string name, params object[] args) => Invoke(null, Type(type), name, args);

            private static (object Value, Exception Error) Invoke(object target, Type type, string name, object[] args)
            {
                MethodInfo method = type.GetMethods(Any).Single(m => m.Name == name && m.GetParameters().Length == args.Length &&
                                                                          m.GetParameters().Zip(args, (p, a) => a == null || p.ParameterType.IsInstanceOfType(a)).All(ok => ok));
                try
                {
                    return (method.Invoke(target, args), null);
                }
                catch (TargetInvocationException ex)
                {
                    return (null, ex.InnerException);
                }
            }

            public Task Result((object Value, Exception Error) run)
            {
                Assert.Null(run.Error);
                return (Task)run.Value;
            }

            public string ResultOf(Task task) => task.GetType().GetProperty("Result").GetValue(task).ToString();

            private FieldInfo Field(object target, string name)
            {
                for (Type t = target.GetType(); t != null; t = t.BaseType)
                {
                    FieldInfo f = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                    if (f != null) return f;
                }
                throw new MissingFieldException(target.GetType().FullName, name);
            }

            public object Get(object target, string field) => Field(target, field).GetValue(target);

            public Type FieldType(object target, string field) => Field(target, field).FieldType;

            public void Set(object target, string field, object value) => Field(target, field).SetValue(target, value);

            /// <summary>
            /// Installs an uninitialised <c>GameApplication</c> as <c>GameApplication.Instance</c>, with a real
            /// <c>GameSynchronizationContext</c> as its <c>Context</c> (none with <paramref name="withContext"/> false): the game
            /// thread's work queue, which <c>UpdateWorlds</c> runs once per frame (<see cref="RunQueue"/> here). Returns the queue.
            /// </summary>
            public object GameLoop(bool withContext = true)
            {
                Type application = Type("Gameloft.Engine.GameApplication");
                object instance = Uninitialized(application);
                object queue = withContext ? NewQueue() : null;
                application.GetField("<Context>k__BackingField", Any).SetValue(instance, queue);
                application.GetField("<Instance>k__BackingField", Any).SetValue(null, instance);
                return queue;
            }

            /// <summary>A new GameSynchronizationContext (as each World has its own).</summary>
            public object NewQueue() => Activator.CreateInstance(Type(DebloatEnginePatch.GameContextType));

            /// <summary>One turn of a GameSynchronizationContext: every queued item runs, with the context current.</summary>
            public void RunQueue(object queue) => Assert.Null(Run(queue, "ExecuteItems").Error);

            /// <summary>Runs <paramref name="action"/> with <paramref name="current"/> as SynchronizationContext.Current (as a World's update does).</summary>
            public T Within<T>(object current, Func<T> action)
            {
                SynchronizationContext saved = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext((SynchronizationContext)current);
                try
                {
                    return action();
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(saved);
                }
            }

            /// <summary>An uninitialised SocialLib_private with a native pointer, installed as the process-wide instance.</summary>
            public object SocialLib()
            {
                object lib = New(DebloatEnginePatch.SocialLibType);
                Set(lib, "self", new IntPtr(0x4000));
                foreach (string f in new[] { "pendingRequests", "pendingAuthCodeRequests" }) Set(lib, f, Activator.CreateInstance(Field(lib, f).FieldType));
                Type(DebloatEnginePatch.SocialLibType).GetField("instance", Any).SetValue(null, lib);
                return lib;
            }

            /// <summary>
            /// An uninitialised SocialComponent of a GameObject in a World without global components (no Synchronization: the
            /// [Remote] SendLoginEvent only raises LoginEvent), with its user lists.
            /// </summary>
            public object Social(bool shouldTriggerAutoLogin)
            {
                object social = New(DebloatEnginePatch.SocialType);
                foreach (string f in new[] { "loggedUsers", "loggingUsers" }) Set(social, f, Activator.CreateInstance(Field(social, f).FieldType));
                Set(social, "<ShouldTriggerAutoLogin>k__BackingField", shouldTriggerAutoLogin);
                Type worldType = Type("Gameloft.Engine.World"), componentType = Type("Gameloft.Engine.Component"), gameObjectType = Type("Gameloft.Engine.GameObject");
                object world = Uninitialized(worldType);
                Type cacheType = context.Get("Gameloft.Util").GetType("Gameloft.Util.ConcurrentCache`2", true).MakeGenericType(typeof(Type), componentType);
                Delegate none = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(Type), componentType),
                                                        typeof(EngineScene).GetMethod(nameof(NoComponent), Any).MakeGenericMethod(componentType));
                worldType.GetField("globalComponents", Any).SetValue(world, Activator.CreateInstance(cacheType, none));
                object gameObject = Uninitialized(gameObjectType);
                gameObjectType.GetField("world", Any).SetValue(gameObject, world);
                componentType.GetFields(Any).Single(f => f.FieldType == gameObjectType).SetValue(social, gameObject);
                return social;
            }

            private static TComponent NoComponent<TComponent>(Type type) where TComponent : class
            {
                return null;
            }

            /// <summary>Records every LoginEvent as "Result/auto|manual".</summary>
            public void OnLogin(object social, List<string> events)
            {
                EventInfo login = Type(DebloatEnginePatch.SocialType).GetEvent("LoginEvent");
                Type data = login.EventHandlerType.GetGenericArguments()[0];
                var recorder = new LoginRecorder(events);
                login.AddEventHandler(social, Delegate.CreateDelegate(login.EventHandlerType, recorder, typeof(LoginRecorder).GetMethod(nameof(LoginRecorder.On)).MakeGenericMethod(data)));
            }

            public void Dispose()
            {
                context.Dispose();
            }
        }

        public sealed class LoginRecorder
        {
            private readonly List<string> events;

            public LoginRecorder(List<string> events)
            {
                this.events = events;
            }

            public void On<T>(object sender, T e)
            {
                Type t = e.GetType();
                events.Add(t.GetProperty("Result").GetValue(e) + "/" + ((bool)t.GetProperty("AutoLogin").GetValue(e) ? "auto" : "manual"));
            }
        }

        /// <summary>
        /// A stand-in libnativeengine.so with the entry points these tests reach (compiled with the system C compiler once per
        /// run): each records a code in a call log the test reads back. Every SocialLib request is refused (-1), no Sns is
        /// initialised or logged in, NeedsAutoLogin answers what the test sets.
        /// </summary>
        private static class NativeStub
        {
            public const int CrashlyticsIsAvailable = 1, CrashlyticsStartNative = 2, CrashlyticsNativeStart = 3, CrashlyticsLogMessage = 4,
                             FabricCallback = 5, FabricIsInitialized = 6;
            public const int RegisterRemote = 10, CancelLocal = 11, CancelAllLocal = 12, StartSystem = 13, PauseSystem = 14, ScheduleLocal = 1000;
            public const int Init = 100, IsInitialized = 200, NeedsAuto = 300, IsLoggedIn = 400, Login = 500, TriggerAuto = 600, SetGroupVolume = 700;

            private const string Source = @"
static int events[1024];
static int count;
static int needs_auto_login;
static void add(int v) { if (count < 1024) events[count++] = v; }
int mcvs_stub_count(void) { return count; }
int mcvs_stub_event(int i) { return i >= 0 && i < count ? events[i] : -1; }
void mcvs_stub_reset(void) { count = 0; needs_auto_login = 0; }
void mcvs_stub_needs_auto_login(int v) { needs_auto_login = v; }

void *Crashlytics_GetInstancePtr(void) { return (void *)0x1000; }
unsigned char Crashlytics_GetIsAvailable(void) { add(1); return 1; }
void Crashlytics_Start_Native(void) { add(2); }
void Crashlytics_Native_Start(void *self) { (void)self; add(3); }
void Crashlytics_LogMessage(void *self, const char *message) { (void)self; (void)message; add(4); }
void *Fabric_GetInstancePtr(void) { return (void *)0x2000; }
void Fabric_SetFabricInitializedCallback(void *self, void *callback) { (void)self; (void)callback; add(5); }
unsigned char Fabric_GetIsInitialized(void *self) { (void)self; add(6); return 0; }

void NativeNotificationSystem_RegisterForRemoteNotifications(void *self) { (void)self; add(10); }
void NativeNotificationSystem_CancelLocalNotification(void *self, const char *type) { (void)self; (void)type; add(11); }
void NativeNotificationSystem_CancelAllLocalNotifications(void *self) { (void)self; add(12); }
void NativeNotificationSystem_Start(void *self) { (void)self; add(13); }
void NativeNotificationSystem_Pause(void *self) { (void)self; add(14); }
void NativeNotificationSystem_ScheduleLocalNotification(void *self, int delay, const char *type, const char *message, const char *action,
                                                        int badge, const char *sound, const char *data)
{ (void)self; (void)type; (void)message; (void)action; (void)badge; (void)sound; (void)data; add(1000 + delay); }

int SocialLib_private_Init_private(void *self, int sns, unsigned char trigger) { (void)self; add(100 + sns * 10 + (trigger ? 1 : 0)); return -1; }
unsigned char SocialLib_private_IsInitialized(void *self, int sns) { (void)self; add(200 + sns); return 0; }
unsigned char SocialLib_private_NeedsAutoLogin_private(void *self, int sns) { (void)self; add(300 + sns); return (unsigned char)needs_auto_login; }
unsigned char SocialLib_private_IsLoggedIn(void *self, int sns) { (void)self; add(400 + sns); return 0; }
int SocialLib_private_Login_private(void *self, int sns) { (void)self; add(500 + sns); return -1; }
int SocialLib_private_TriggerAutoLogin_private(void *self, int sns) { (void)self; add(600 + sns); return -1; }
void VoxManager_SetGroupVolume_private(const char *label, float volume, float fade) { (void)label; (void)volume; (void)fade; add(700); }
";

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int CountFn();

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int EventFn(int index);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void ResetFn();

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void SetFn(int value);

            private static readonly object Gate = new object();
            private static bool built;
            private static string library;
            private static CountFn count;
            private static EventFn at;
            private static ResetFn reset;
            private static SetFn needsAutoLogin;

            /// <summary>The stub's path, or null (and a note) without a C compiler.</summary>
            public static string Library(ITestOutputHelper output)
            {
                lock (Gate)
                {
                    if (!built)
                    {
                        built = true;
                        string dir = TestEnvironment.NewTempDir("debloat-nativeengine-stub");
                        string source = Path.Combine(dir, "stub.c"), lib = Path.Combine(dir, "libnativeengine_debloat_stub.so");
                        File.WriteAllText(source, Source);
                        foreach (string cc in new[] { "cc", "gcc", "clang" })
                        {
                            try
                            {
                                var psi = new ProcessStartInfo(cc) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
                                foreach (string a in new[] { "-shared", "-fPIC", "-O0", "-o", lib, source }) psi.ArgumentList.Add(a);
                                using (Process p = Process.Start(psi))
                                {
                                    p.WaitForExit(60000);
                                    if (p.ExitCode == 0 && File.Exists(lib)) break;
                                }
                            }
                            catch (System.ComponentModel.Win32Exception)
                            {
                            }
                        }
                        if (File.Exists(lib))
                        {
                            IntPtr handle = NativeLibrary.Load(lib);
                            count = Marshal.GetDelegateForFunctionPointer<CountFn>(NativeLibrary.GetExport(handle, "mcvs_stub_count"));
                            at = Marshal.GetDelegateForFunctionPointer<EventFn>(NativeLibrary.GetExport(handle, "mcvs_stub_event"));
                            reset = Marshal.GetDelegateForFunctionPointer<ResetFn>(NativeLibrary.GetExport(handle, "mcvs_stub_reset"));
                            needsAutoLogin = Marshal.GetDelegateForFunctionPointer<SetFn>(NativeLibrary.GetExport(handle, "mcvs_stub_needs_auto_login"));
                            library = lib;
                        }
                    }
                    if (library == null)
                    {
                        if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                            throw new InvalidOperationException("no C compiler (cc, gcc, clang) for the stand-in libnativeengine");
                        output.WriteLine("no C compiler: the runs against a stand-in libnativeengine are skipped");
                    }
                    return library;
                }
            }

            public static void Reset() => reset();

            public static void NeedsAutoLogin(bool value) => needsAutoLogin(value ? 1 : 0);

            public static int[] Events() => Enumerable.Range(0, count()).Select(i => at(i)).ToArray();
        }
    }
}
