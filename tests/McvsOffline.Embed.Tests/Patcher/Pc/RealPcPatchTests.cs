using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// The PC patch set (docs/design/pc-port.md §8, tests §12.4) on the real Steam assemblies (MCVS_PC_REFS, read only; copies in
    /// temp folders when a test writes): every site's fingerprint, each patch alone and the full plan, the cross-build refusals
    /// before any change, a second application refused, the exact inserted IL, the reference set (P3) and ILVerify before/after.
    /// Every module is read through <see cref="TestEnvironment.PcReader"/> and the corlib asserted with
    /// <see cref="TestEnvironment.AssertPcCorlib"/> (C12). Skipped without the files, a failure under MCVS_REQUIRE_PC=1.
    /// </summary>
    public class RealPcPatchTests
    {
        private static readonly BuildProfile Pc = BuildProfile.PcSteam6372049;

        private readonly ITestOutputHelper output;

        public RealPcPatchTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>Every PC site (patch id, file, type, method, the parameter count or -1, fingerprint) as the design's tables give it.</summary>
        internal static readonly (string Patch, string File, string Type, string Method, int Parameters, string Fingerprint)[] Sites =
        {
            ("hook-pc", "EngineBridge.dll", "EngineBridge.Class1", "Start", 0, EngineBridgeHookPatch.PcStartFingerprint),
            ("hook-pc", "EngineBridge.dll", "EngineBridge.Class1", ".ctor", 0, EngineBridgeHookPatch.PcCtorFingerprint),
            ("quicklaunch", "Gameloft.Engine.dll", "Gameloft.Engine.LocalAnubis.BaseServer", "get_Port", 0, QuickLaunchPatch.PcPortFingerprint),
            ("quicklaunch", "Gameloft.Engine.dll", "Gameloft.Engine.ClientComponent", "QuickLaunch", 2, QuickLaunchPatch.PcQuickLaunchFingerprint),
            ("quicklaunch", "Gameloft.Engine.dll", "Gameloft.Engine.MatchmakingComponent", "AddAutomatchHostSettings", 4, QuickLaunchPatch.PcAutomatchHostFingerprint),
            ("headless-diag", "Gameloft.Engine.dll", "Gameloft.Engine.ServerManager/<_AddClientAsync>d__20", "MoveNext", 0, HeadlessDiagnosticsPatch.PcAddClientFingerprint),
            ("headless-diag", "Gameloft.Engine.dll", "Gameloft.Engine.ComponentAssemblies", "CreateInstance", 4, HeadlessDiagnosticsPatch.PcCreateInstanceFingerprint),
            ("headless", "Game.dll", "Game.InputSettingsComponent", "OnStart", 0, HeadlessGamePatch.PcInputSettingsStartFingerprint),
            ("headless", "Game.dll", "Game.SoundManagerGameComponent", "OnStart", 0, HeadlessGamePatch.PcSoundStartFingerprint),
            ("headless", "Game.dll", "Game.SoundManagerGameComponent", "OnUpdate", 0, HeadlessGamePatch.PcSoundUpdateFingerprint),
            ("headless", "Game.dll", "Game.SoundManagerGameComponent", "OnStop", 0, HeadlessGamePatch.PcSoundStopFingerprint),
            ("headless", "Game.dll", "Game.FlameZoneComponent", "BroadcastEnd", 0, HeadlessGamePatch.PcFlameZoneEndFingerprint),
            ("headless", "Game.dll", "Game.BacktrackTimeDistortionExplosion", "FadeOutEffect", 0, HeadlessGamePatch.PcBacktrackFadeFingerprint),
            ("headless", "Game.dll", "Game.GamePreferencesComponent", "LoadPreferencesWindows", 0, HeadlessGamePatch.PcLoadPreferencesFingerprint),
            ("headless", "Game.dll", "Game.SteamAchievementsComponent", "OnStart", 0, HeadlessGamePatch.PcSteamStartFingerprint),
            ("headless", "Game.dll", "Game.SteamAchievementsComponent", "OnStop", 0, HeadlessGamePatch.PcSteamStopFingerprint),
            ("headless", "Game.dll", "Game.GameClientComponent", "OnCreate", 0, HeadlessGamePatch.PcClientCreateFingerprint),
            ("headless", "Game.dll", "Game.GameplayComponent", "OnAppFocusLost", 0, HeadlessGamePatch.PcFocusLostFingerprint),
            ("headless", "Game.dll", "Game.GameplayComponent", "OnUpdate", 0, HeadlessGamePatch.PcGameplayUpdateFingerprint),
            ("headless", "Game.dll", "Game.TutorialComponent", "OnStart", 0, HeadlessGamePatch.PcTutorialStartFingerprint),
            ("graphics", "Gameloft.Engine.dll", "Gameloft.Engine.GameOptionPerformanceProfileComponent", "OnCreate", 0, GraphicsPatch.OnCreateFingerprint),
            ("gameload", "Gameloft.Engine.dll", "Gameloft.Engine.ComponentAssemblies", "GetAssembly", 2, GameAssemblyLoadPatch.PcGetAssemblyFingerprint),
            ("nativeonline", "Gameloft.Engine.dll", "Gameloft.Engine.OnlinePlatformComponent", "Init_private", 3, NativeOnlinePatch.PcInitFingerprint),
            ("waits", "Gameloft.Engine.dll", "Gameloft.Engine.TaskExtension", "WaitAndWork(System.Threading.Tasks.Task", 2, WaitsPatch.PcTaskFingerprint),
            ("waits", "Gameloft.Engine.dll", "Gameloft.Engine.TaskExtension", "WaitAndWork(System.Threading.ManualResetEventSlim", 2, WaitsPatch.PcSignalFingerprint),
            ("adminlink", "Gameloft.Engine.dll", "Gameloft.Engine.BrowserComponent", "OpenUrl", 2, AdminLinkPatch.PcOpenUrlFingerprint),
            ("adminpage", "Game.dll", "Game.PlayerProfileComponent", "GetAccountMenuButtonList", 0, AdminPagePatch.PcButtonListFingerprint),
            ("adminpage", "Game.dll", "Game.PlayerProfileComponent", "ProcessAccountMenuButtonAction", 1, AdminPagePatch.PcButtonActionFingerprint),
            ("physics", "Gameloft.Engine.dll", "Gameloft.Engine.PhysicsComponent", ".ctor", 0, PhysicsPatch.CtorFingerprint),
            ("physics", "Gameloft.Engine.dll", "Gameloft.Engine.PhysicsComponent", "UpdateSimulationSteps", 2, PhysicsPatch.StepsFingerprint),
            ("store", "Game.dll", "Game.KillSignatureRewardItem", "GetAvailableId", 1, StorePatch.GetAvailableIdFingerprint),
            ("signatures", "Game.dll", "Game.PlayerProfileComponent", "GetSignatureAssetsList", -1, SignaturesPatch.AssetsListFingerprint),
            ("signatures", "Game.dll", "Game.GameOfflineStore", "GetKillSignItems", -1, SignaturesPatch.KillSignItemsFingerprint),
            ("inbox", "Game.dll", "Game.InboxComponent", "GenerateRewardsObjects", 3, InboxPatch.GenerateRewardsFingerprint),
            ("inbox", "Game.dll", "Game.InboxComponent", "GetObject", 2, InboxPatch.GetObjectFingerprint),
            ("inbox", "Game.dll", "Game.InboxComponent", "RefreshUI", 0, InboxPatch.PcRefreshFingerprint),
            ("inbox", "Game.dll", "Game.InboxComponent/<ClaimRewardsForMessage>d__57", "MoveNext", 0, InboxPatch.PcClaimFingerprint),
            ("inbox", "Game.dll", "Game.KillSignatureRewardItem", "get_IsRandom", 0, InboxPatch.PcIsRandomFingerprint),
            ("forum", "Game.dll", "Game.UIComponent", "GoToForum", -1, ForumPatch.ForumFingerprint),
        };

        [Fact]
        public void EveryPcSiteHasTheDesignsFingerprint()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            ReaderParameters reader = RealPc.Reader(pc, null);
            using (reader.AssemblyResolver)
            {
                var modules = new Dictionary<string, ModuleDefinition>(StringComparer.Ordinal);
                ModuleDefinition Module(string file)
                {
                    ModuleDefinition m;
                    if (!modules.TryGetValue(file, out m))
                    {
                        m = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(Path.Combine(pc, file))), reader);
                        TestEnvironment.AssertPcCorlib(m);
                        modules[file] = m;
                    }
                    return m;
                }
                var wrong = new List<string>();
                foreach (var site in Sites)
                {
                    MethodDefinition method = RealPc.Method(Module(site.File), site.Type, site.Method, site.Parameters);
                    string actual = IlPatch.Fingerprint(method);
                    output.WriteLine(site.Patch + " " + method.FullName + " " + actual);
                    if (actual != site.Fingerprint) wrong.Add(method.FullName + ": " + actual + ", expected " + site.Fingerprint);
                }
                foreach (ModuleDefinition m in modules.Values) m.Dispose();
                Assert.True(wrong.Count == 0, string.Join("\n", wrong));
            }
        }

        [Fact]
        public void EachPcPatchAppliesAloneAndTheFullPlanApplies()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            IReadOnlyList<IlPatch> plan = RealPc.FullPlan();
            foreach (IlPatch patch in plan)
            {
                var notes = new List<string>();
                Dictionary<string, byte[]> alone = RealPc.Patch(pc, PcHookServer.Compile(), notes, Pc, patch);
                Assert.Equal(new[] { patch.AssemblyFile }, alone.Keys);
                Assert.NotEmpty(notes);
                output.WriteLine(patch.Id + ": " + string.Join(" | ", notes));
            }
            var all = new List<string>();
            Dictionary<string, byte[]> full = RealPc.Patch(pc, PcHookServer.Compile(), all, Pc, plan.ToArray());
            Assert.Equal(new[] { "EngineBridge.dll", "Game.dll", "Gameloft.Engine.dll", "Gameloft.Util.dll" }, full.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Contains(all, n => n.Contains("(M3)"));
            Assert.Contains(all, n => n.Contains("(P1)"));
        }

        [Fact]
        public void EveryPatchRefusesTheOtherBuildBeforeChangingAnything()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            string serverDir = PcHookServer.WriteToTempFolder();
            ReaderParameters reader = RealPc.Reader(pc, serverDir);
            using (reader.AssemblyResolver)
            using (ModuleDefinition serverModule = ModuleDefinition.ReadModule(new MemoryStream(server), reader))
            {
                // PC files, Android profile: every PC patch refuses (PC-only ids by their table, shared ids by the corlib check).
                var android = new PatchContext(serverModule, 51780, BuildProfile.Android1_17_32);
                foreach (IlPatch patch in RealPc.FullPlan()) AssertRefusedUnchanged(pc, patch.AssemblyFile, reader, patch, android, "PC file, Android profile");

                // PC files, PC profile: the Android-only patches refuse (§8.8).
                var pcContext = new PatchContext(serverModule, 51780, Pc);
                foreach (IlPatch patch in new IlPatch[] { new BootstrapHookPatch(), new FinderPatch(), new LanPatch(), new DebloatEnginePatch() })
                {
                    string file = patch.AssemblyFile == "NicePlaySDK.dll" ? "GameManager.dll" : patch.AssemblyFile;
                    AssertRefusedUnchanged(pc, file, reader, patch, pcContext, "Android-only patch, PC profile");
                }
            }

            // Android files, PC profile.
            string refs = TestEnvironment.GameAssemblies;
            if (refs == null)
            {
                Assert.True(Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") != "1", "MCVS_REQUIRE_REAL_CLIENT=1 but MCVS_REFS is missing");
                output.WriteLine("the Android assemblies are not available (MCVS_REFS): the Android-file half is skipped");
                return;
            }
            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(refs);
                var androidReader = new ReaderParameters { AssemblyResolver = resolver, InMemory = true, ReadingMode = ReadingMode.Immediate };
                using (ModuleDefinition serverModule = ModuleDefinition.ReadModule(new MemoryStream(FixtureCompiler.Compile("McvsOffline.Server", RealGamePatchTests.HookServerSource))))
                {
                    var pcContext = new PatchContext(serverModule, 51780, Pc);
                    foreach (IlPatch patch in RealPc.FullPlan())
                    {
                        string file = patch.AssemblyFile == "EngineBridge.dll" ? "NicePlaySDK.dll" : patch.AssemblyFile;   // no EngineBridge on Android
                        AssertRefusedUnchanged(refs, file, androidReader, patch, pcContext, "Android file, PC profile");
                    }
                }
            }
        }

        /// <summary>
        /// Applies <paramref name="patch"/> to <paramref name="file"/> (after <paramref name="change"/>, when given) and asserts it is
        /// refused with nothing changed: the same methods, types and no note. Returns the refusal.
        /// </summary>
        private PatchException AssertRefusedUnchanged(string folder, string file, ReaderParameters reader, IlPatch patch, PatchContext context, string what,
                                                      Action<ModuleDefinition> change = null)
        {
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(Path.Combine(folder, file))), reader))
            {
                change?.Invoke(module);
                string before = RealPc.MethodsDigest(module);
                int types = module.Types.Count;
                PatchException refused = Assert.Throws<PatchException>(() => patch.Apply(module, context));
                output.WriteLine(what + ", " + patch.Id + " on " + file + ": " + refused.Message.Split('\n')[0]);
                Assert.Equal(before, RealPc.MethodsDigest(module));
                Assert.Equal(types, module.Types.Count);
                Assert.Empty(context.Notes);
                return refused;
            }
        }

        /// <summary>§8.2: hook-pc takes mscorlib 4 only with the framework's token (b77a5c561934e089).</summary>
        [Fact]
        public void HookPcRefusesAnMscorlib4WithAnotherToken()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            ReaderParameters reader = RealPc.Reader(pc, PcHookServer.WriteToTempFolder());
            using (reader.AssemblyResolver)
            using (ModuleDefinition serverModule = ModuleDefinition.ReadModule(new MemoryStream(server), reader))
            {
                byte[] androidToken = Enumerable.Range(0, 8).Select(i => Convert.ToByte(BuildProfile.Android1_17_32.CorlibPublicKeyToken.Substring(2 * i, 2), 16)).ToArray();
                PatchException refused = AssertRefusedUnchanged(pc, "EngineBridge.dll", reader, new EngineBridgeHookPatch(), new PatchContext(serverModule, 51780, Pc),
                                                                "mscorlib 4.0.0.0 with the Android token", m =>
                                                                {
                                                                    AssemblyNameReference corlib = m.AssemblyReferences.Single(r => r.Name == "mscorlib");
                                                                    Assert.Equal(new Version(4, 0, 0, 0), corlib.Version);
                                                                    corlib.PublicKeyToken = androidToken;
                                                                    Assert.Same(corlib, m.TypeSystem.CoreLibrary);
                                                                });
                Assert.StartsWith("EngineBridge.dll does not reference mscorlib 4.x", refused.Message);
            }
        }

        [Fact]
        public void ASecondApplicationIsRefused()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            Dictionary<string, byte[]> once = RealPc.Patch(pc, server, null, Pc, RealPc.FullPlan().ToArray());
            HashSet<string> changed = ChangedMethods(pc, once);
            string copy = RealPc.CopyWith(pc, once);
            try
            {
                // Through the installer's path: refused.
                foreach (IlPatch patch in RealPc.FullPlan())
                {
                    PatchException refused = Assert.Throws<PatchException>(() => RealPc.Patch(copy, server, null, Pc, patch));
                    AssertSecondApplication(patch, refused, changed);
                }

                // The patch itself on the patched module: refused before any change.
                ReaderParameters reader = RealPc.Reader(copy, PcHookServer.WriteToTempFolder());
                using (reader.AssemblyResolver)
                using (ModuleDefinition serverModule = ModuleDefinition.ReadModule(new MemoryStream(server), reader))
                {
                    foreach (IlPatch patch in RealPc.FullPlan())
                    {
                        PatchException refused = AssertRefusedUnchanged(copy, patch.AssemblyFile, reader, patch, new PatchContext(serverModule, 51780, Pc), "second application");
                        AssertSecondApplication(patch, refused, changed);
                    }
                }
            }
            finally
            {
                Directory.Delete(copy, true);
            }
        }

        /// <summary>
        /// A second application is refused for being one, not for an unrelated failure: the message says "already patched" (a
        /// witness or helper is there; headless: a type already marked [KeepInHeadlessMode]), or it names first a method of the
        /// patch's assembly whose body the first application changed (a site check: fingerprint, shape or literal).
        /// </summary>
        private void AssertSecondApplication(IlPatch patch, PatchException refused, HashSet<string> changed)
        {
            string first = refused.Message.Split('\n')[0];
            output.WriteLine(patch.Id + ": " + first);
            if (first.Contains("already patched") || first.Contains("already has [KeepInHeadlessMode]")) return;
            Assert.True(changed.Any(m => m.StartsWith(patch.AssemblyFile + " ", StringComparison.Ordinal) &&
                                         first.StartsWith(m.Substring(patch.AssemblyFile.Length + 1) + " ", StringComparison.Ordinal)),
                        patch.Id + ": the second application was refused for another reason: " + first);
        }

        /// <summary>
        /// The methods whose body <paramref name="patched"/> changed, as "file name" for both spellings a refusal starts with: the
        /// full name ("System.Void T::M()") and the short one ("T::M:" without the trailing colon, as the literal checks write it).
        /// </summary>
        private static HashSet<string> ChangedMethods(string pc, Dictionary<string, byte[]> patched)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            ReaderParameters reader = RealPc.Reader(pc, PcHookServer.WriteToTempFolder());
            using (reader.AssemblyResolver)
            {
                foreach (var kv in patched)
                {
                    using (ModuleDefinition before = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(Path.Combine(pc, kv.Key))), reader))
                    using (ModuleDefinition after = ModuleDefinition.ReadModule(new MemoryStream(kv.Value), reader))
                    {
                        // By full name and fingerprint: a few full names repeat (methods that differ only in a modreq).
                        var original = new HashSet<string>(RealPc.AllTypes(before.Types).SelectMany(t => t.Methods).Where(m => m.HasBody)
                                                                 .Select(m => m.FullName + " " + IlPatch.Fingerprint(m)), StringComparer.Ordinal);
                        var names = new HashSet<string>(original.Select(o => o.Substring(0, o.LastIndexOf(' '))), StringComparer.Ordinal);
                        foreach (MethodDefinition m in RealPc.AllTypes(after.Types).SelectMany(t => t.Methods).Where(m => m.HasBody))
                        {
                            if (!names.Contains(m.FullName) || original.Contains(m.FullName + " " + IlPatch.Fingerprint(m))) continue;
                            result.Add(kv.Key + " " + m.FullName);
                            result.Add(kv.Key + " " + m.DeclaringType.FullName + "::" + m.Name + ":");
                        }
                    }
                }
            }
            return result;
        }

        [Fact]
        public void PatchedAssembliesReferenceTheirOriginalsReferencesPlusTheServerOnly()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, PcHookServer.Compile(), null, Pc, RealPc.FullPlan().ToArray());
            foreach (var kv in patched)
            {
                List<string> original, now;
                using (ModuleDefinition m = ModuleDefinition.ReadModule(Path.Combine(pc, kv.Key))) original = m.AssemblyReferences.Select(r => r.FullName).ToList();
                using (ModuleDefinition m = ModuleDefinition.ReadModule(new MemoryStream(kv.Value))) now = m.AssemblyReferences.Select(r => r.FullName).ToList();
                List<string> expected = kv.Key == "Gameloft.Util.dll"
                    ? original
                    : original.Concat(new[] { "McvsOffline.Server, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null" }).ToList();
                output.WriteLine(kv.Key + ": " + string.Join("; ", now));
                Assert.Equal(expected.OrderBy(r => r, StringComparer.Ordinal), now.OrderBy(r => r, StringComparer.Ordinal));
                Assert.Single(now, r => r.StartsWith("mscorlib,", StringComparison.Ordinal));
                Assert.Contains("mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", now);
            }
        }

        [Fact]
        public void TheInsertedIlIsExact()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, PcHookServer.Compile(), null, Pc, RealPc.FullPlan().ToArray());
            ReaderParameters reader = RealPc.Reader(pc, PcHookServer.WriteToTempFolder());
            using (reader.AssemblyResolver)
            using (ModuleDefinition bridge = ModuleDefinition.ReadModule(new MemoryStream(patched["EngineBridge.dll"]), reader))
            using (ModuleDefinition engine = ModuleDefinition.ReadModule(new MemoryStream(patched["Gameloft.Engine.dll"]), reader))
            using (ModuleDefinition game = ModuleDefinition.ReadModule(new MemoryStream(patched["Game.dll"]), reader))
            {
                TestEnvironment.AssertPcCorlib(engine);

                // hook-pc: the marker, try { McvsOfflineInit(); } catch (object) { the marker with the exception }, then the six
                // original instructions
                const string trace = "System.Void EngineBridge.Class1::McvsOfflineHookTrace(System.Object)";
                MethodDefinition start = RealPc.Method(bridge, "EngineBridge.Class1", "Start", 0);
                Assert.Equal(new[]
                {
                    "IL_0000: ldnull", "IL_0001: call " + trace,
                    "IL_0006: call System.Void EngineBridge.Class1::McvsOfflineInit()", "IL_000b: leave IL_001a",
                    "IL_0010: call " + trace, "IL_0015: leave IL_001a",
                    "IL_001a: ldc.i4.0", "IL_001b: ldnull", "IL_001c: ldc.i4.0",
                    "IL_001d: call System.Int32 Gameloft.Engine.MainLoop::Start(Gameloft.Engine.MainLoop/ModeType,System.String[],System.Boolean)",
                    "IL_0022: pop", "IL_0023: ret",
                }, start.Body.Instructions.Select(i => i.ToString()));
                Assert.Equal(EngineBridgeHookPatch.PcPatchedStartFingerprint, IlPatch.Fingerprint(start));
                ExceptionHandler h = Assert.Single(start.Body.ExceptionHandlers);
                Assert.Equal("System.Object", h.CatchType.FullName);
                Assert.Equal(0x6, h.TryStart.Offset);
                Assert.Equal(0x10, h.HandlerStart.Offset);
                Assert.Equal(0x1a, h.HandlerEnd.Offset);
                MethodDefinition init = RealPc.Method(bridge, "EngineBridge.Class1", EngineBridgeHookPatch.HelperName, 0);
                Assert.True(init.IsPrivate && init.IsStatic && init.ImplAttributes.HasFlag(MethodImplAttributes.NoInlining));
                Assert.Equal(new[] { "IL_0000: call System.Void McvsOffline.Bootstrap::Init()", "IL_0005: ret" }, init.Body.Instructions.Select(i => i.ToString()));
                Assert.Equal(EngineBridgeHookPatch.PcCtorFingerprint, IlPatch.Fingerprint(RealPc.Method(bridge, "EngineBridge.Class1", ".ctor", 0)));
                // The guard: try { McvsOfflineHookWrite(error); } catch (object) { pop }
                MethodDefinition guard = RealPc.Method(bridge, "EngineBridge.Class1", EngineBridgeHookPatch.TraceName, 1);
                Assert.True(guard.IsPrivate && guard.IsStatic && guard.ImplAttributes.HasFlag(MethodImplAttributes.NoInlining));
                Assert.Equal(new[] { "ldarg.0", "call System.Void EngineBridge.Class1::McvsOfflineHookWrite(System.Object)", "leave", "pop", "leave", "ret" },
                             guard.Body.Instructions.Select(i => i.OpCode.Code == Code.Leave ? "leave" : i.ToString().Substring(i.ToString().IndexOf(' ') + 1)));
                ExceptionHandler g = Assert.Single(guard.Body.ExceptionHandlers);
                Assert.Equal("System.Object", g.CatchType.FullName);
                Assert.Same(guard.Body.Instructions[0], g.TryStart);
                Assert.Same(guard.Body.Instructions[3], g.HandlerStart);
                Assert.All(new[] { guard.Body.Instructions[2], guard.Body.Instructions[4] }, l => Assert.Same(guard.Body.Instructions[5], l.Operand));
                // The writer calls mscorlib only (and the kernel32 import); the import is GetCurrentProcessId, nothing else.
                MethodDefinition writer = RealPc.Method(bridge, "EngineBridge.Class1", EngineBridgeHookPatch.WriteName, 1);
                Assert.True(writer.IsPrivate && writer.IsStatic && writer.ImplAttributes.HasFlag(MethodImplAttributes.NoInlining));
                List<MethodReference> calls = writer.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToList();
                Assert.All(calls.Where(c => c.DeclaringType.FullName != "EngineBridge.Class1"), c => Assert.Equal("mscorlib", c.DeclaringType.Scope.Name));
                Assert.Equal(new[] { EngineBridgeHookPatch.PidName }, calls.Where(c => c.DeclaringType.FullName == "EngineBridge.Class1").Select(c => c.Name));
                Assert.Contains(writer.Body.Instructions, i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == EngineBridgeHookPatch.MarkerFile);
                Assert.Contains(writer.Body.Instructions, i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == EngineBridgeHookPatch.MarkerFolder);
                Assert.Contains(calls, c => c.FullName == "System.String System.IO.Path::GetTempPath()");
                Assert.Contains(calls, c => c.FullName == "System.Void System.IO.File::AppendAllText(System.String,System.String)");
                ExceptionHandler p = Assert.Single(writer.Body.ExceptionHandlers);
                Assert.Equal("System.Object", p.CatchType.FullName);
                Assert.Equal(EngineBridgeHookPatch.PidName, ((MethodReference)p.TryStart.Operand).Name);
                MethodDefinition pid = RealPc.Method(bridge, "EngineBridge.Class1", EngineBridgeHookPatch.PidName, 0);
                Assert.True(pid.IsPInvokeImpl && pid.IsPrivate && pid.IsStatic && !pid.HasBody);
                Assert.Equal("GetCurrentProcessId", pid.PInvokeInfo.EntryPoint);
                Assert.Equal("kernel32.dll", pid.PInvokeInfo.Module.Name);
                Assert.Equal("System.UInt32", pid.ReturnType.FullName);
                Assert.Equal(new[] { "kernel32.dll" }, bridge.ModuleReferences.Select(r => r.Name));
                Assert.Equal(new[] { "Start", ".ctor", EngineBridgeHookPatch.HelperName, EngineBridgeHookPatch.PidName, EngineBridgeHookPatch.WriteName, EngineBridgeHookPatch.TraceName },
                             bridge.GetType("EngineBridge.Class1").Methods.Select(m => m.Name));

                // quicklaunch P1: Android's body
                Assert.Equal(QuickLaunchPatch.AndroidPortFingerprint, IlPatch.Fingerprint(RealPc.Method(engine, "Gameloft.Engine.LocalAnubis.BaseServer", "get_Port", 0)));
                // P2: one call into the helper
                MethodDefinition quickLaunch = RealPc.Method(engine, "Gameloft.Engine.ClientComponent", "QuickLaunch", 2);
                Assert.Equal(new[]
                {
                    "IL_0000: ldarg.0", "IL_0001: ldarg.1", "IL_0002: ldarg.2",
                    "IL_0003: call System.Threading.Tasks.Task Gameloft.Engine.McvsQuickLaunch::QuickLaunch(Gameloft.Engine.ClientComponent,Gameloft.Engine.ClientLink,System.Threading.CancellationToken)",
                    "IL_0008: ret",
                }, quickLaunch.Body.Instructions.Select(i => i.ToString()));
                Assert.False(quickLaunch.Body.HasVariables);
                TypeDefinition helper = engine.GetType(QuickLaunchPatch.HelperNamespace + "." + QuickLaunchPatch.HelperType);
                Assert.True(helper.IsNotPublic && helper.IsAbstract && helper.IsSealed);
                Assert.Equal(new[] { "CallDefault", "CallQuickLaunch", "DefaultAutomatchHost", "QuickLaunch" }, helper.Methods.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal));
                Assert.Contains("call System.Threading.Tasks.Task McvsOffline.PcHooks::QuickLaunch(System.Object,System.Object,System.Threading.CancellationToken)",
                                RealPc.Method(engine, helper.FullName, "CallQuickLaunch", 3).Body.Instructions.Select(i => i.ToString()).Single(s => s.Contains("PcHooks")));
                MethodDefinition fallback = RealPc.Method(engine, helper.FullName, "QuickLaunch", 3);
                Assert.Contains(fallback.Body.Instructions, i => i.OpCode == OpCodes.Callvirt && i.Operand.ToString() ==
                                "System.Threading.Tasks.Task Gameloft.Engine.ClientLink::QuickLaunch(System.Threading.CancellationToken)");
                Assert.Equal("System.Object", Assert.Single(fallback.Body.ExceptionHandlers).CatchType.FullName);
                Assert.True(RealPc.Method(engine, helper.FullName, "CallQuickLaunch", 3).ImplAttributes.HasFlag(MethodImplAttributes.NoInlining));
                // P3: the prologue, then the stock body
                MethodDefinition automatch = RealPc.Method(engine, "Gameloft.Engine.MatchmakingComponent", "AddAutomatchHostSettings", 4);
                Assert.Equal("IL_0000: ldarg.1", automatch.Body.Instructions[0].ToString());
                Assert.Equal("IL_0001: call System.Void Gameloft.Engine.McvsQuickLaunch::DefaultAutomatchHost(System.Object)",
                             automatch.Body.Instructions[1].ToString());
                Assert.Equal("IL_0006: ldarg.3", automatch.Body.Instructions[2].ToString());
                Assert.Equal(26 + 2, automatch.Body.Instructions.Count);

                // headless-diag M1: the handler starts with the report; M4: ldarg.1 + the report
                MethodDefinition moveNext = RealPc.Method(engine, "Gameloft.Engine.ServerManager/<_AddClientAsync>d__20", "MoveNext", 0);
                ExceptionHandler m1 = moveNext.Body.ExceptionHandlers.Single(x => x.CatchType != null && x.CatchType.FullName == "System.Exception" && x.HandlerStart.OpCode == OpCodes.Call);
                Assert.Equal("System.Void Gameloft.Engine.McvsWorldDiagnostics::ServerWorldFailed(System.Exception)", m1.HandlerStart.Operand.ToString());
                Assert.Equal(OpCodes.Leave_S, m1.HandlerStart.Next.OpCode);
                ExceptionHandler cancelled = moveNext.Body.ExceptionHandlers.Single(x => x.CatchType != null && x.CatchType.FullName == "System.OperationCanceledException");
                Assert.Equal(OpCodes.Pop, cancelled.HandlerStart.OpCode);
                MethodDefinition create = RealPc.Method(engine, "Gameloft.Engine.ComponentAssemblies", "CreateInstance", 4);
                ExceptionHandler m4 = create.Body.ExceptionHandlers.Single(x => x.HandlerType == ExceptionHandlerType.Catch);
                Assert.Equal(OpCodes.Ldarg_1, m4.HandlerStart.OpCode);
                Assert.Equal("System.Void Gameloft.Engine.McvsWorldDiagnostics::ComponentCreateFailed(System.Exception,System.Type)", m4.HandlerStart.Next.Operand.ToString());
                Assert.Equal(OpCodes.Leave_S, m4.HandlerStart.Next.Next.OpCode);
                TypeDefinition diagnostics = engine.GetType("Gameloft.Engine." + HeadlessDiagnosticsPatch.HelperType);
                Assert.True(diagnostics.IsNotPublic && diagnostics.IsAbstract && diagnostics.IsSealed);
                foreach (string name in new[] { "ServerWorldFailed", "ComponentCreateFailed" })
                {
                    MethodDefinition guarded = diagnostics.Methods.Single(m => m.Name == name);
                    Assert.Equal("System.Object", Assert.Single(guarded.Body.ExceptionHandlers).CatchType.FullName);
                    MethodDefinition forward = diagnostics.Methods.Single(m => m.Name == "Forward" + name);
                    Assert.True(forward.ImplAttributes.HasFlag(MethodImplAttributes.NoInlining));
                    Assert.Contains(forward.Body.Instructions, i => i.Operand is MethodReference r && r.DeclaringType.FullName == "McvsOffline.PcHooks" && r.Name == name);
                }

                // gameload: the stock body plus one call between the name and new AssemblyName(...), inside the try
                MethodDefinition getAssembly = RealPc.Method(engine, "Gameloft.Engine.ComponentAssemblies", "GetAssembly", 2);
                Assert.Equal(45 + 1, getAssembly.Body.Instructions.Count);
                Assert.Equal(new[]
                {
                    "IL_0027: ldarg.1",
                    "IL_0028: call System.String Gameloft.Engine.McvsGameLoad::Qualify(System.String)",
                    "IL_002d: newobj System.Void System.Reflection.AssemblyName::.ctor(System.String)",
                    "IL_0032: call System.Reflection.Assembly System.Reflection.Assembly::Load(System.Reflection.AssemblyName)",
                }, getAssembly.Body.Instructions.Skip(16).Take(4).Select(i => i.ToString()));
                ExceptionHandler loadCatch = Assert.Single(getAssembly.Body.ExceptionHandlers);
                Assert.Equal(0x27, loadCatch.TryStart.Offset);
                Assert.Equal("System.Object", loadCatch.CatchType.FullName);
                TypeDefinition gameLoad = engine.GetType(GameAssemblyLoadPatch.HelperNamespace + "." + GameAssemblyLoadPatch.HelperType);
                Assert.True(gameLoad.IsNotPublic && gameLoad.IsAbstract && gameLoad.IsSealed);
                MethodDefinition qualify = Assert.Single(gameLoad.Methods);
                Assert.True(qualify.IsAssembly && qualify.IsStatic);
                Assert.Equal(new[]
                {
                    "ldarg.0", "ldstr Game", "call System.Boolean System.String::op_Equality(System.String,System.String)", "brfalse -> #6",
                    "ldstr Game, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null", "ret", "ldarg.0", "ret",
                }, Shape(qualify));
                Assert.Equal("mscorlib", ((MethodReference)qualify.Body.Instructions[2].Operand).DeclaringType.Scope.Name);
                Assert.DoesNotContain(gameLoad.Methods.SelectMany(m => m.Body.Instructions), i => i.Operand is MemberReference r && r.DeclaringType != null &&
                                                                                                 r.DeclaringType.Scope.Name == "McvsOffline.Server");
                // nativeonline: the guard branches to the last instruction (the ret)
                MethodDefinition initPrivate = RealPc.Method(engine, "Gameloft.Engine.OnlinePlatformComponent", "Init_private", 3);
                Instruction brfalse = initPrivate.Body.Instructions.First(i => i.OpCode == OpCodes.Brfalse || i.OpCode == OpCodes.Brfalse_S);
                Assert.Same(initPrivate.Body.Instructions.Last(), brfalse.Operand);
                Assert.Equal(OpCodes.Ret, initPrivate.Body.Instructions.Last().OpCode);
                Assert.Equal(23 + 8, initPrivate.Body.Instructions.Count);

                // graphics: OptionsApplied forwards the component itself (no m_options on PC)
                MethodDefinition applied = RealPc.Method(engine, "Gameloft.Engine.McvsGraphics", "OptionsApplied", 1);
                Assert.Equal(new[] { "ldarg.0", "call", "leave.s", "pop", "leave.s", "ret" }, applied.Body.Instructions.Select(i => i.OpCode.Name));
                // graphics: OnCreate is the original plus three instructions: the JSON hook right after AssetStream::ReadAllText,
                // ldarg.0 + OptionsApplied(this) before the final ret.
                using (ModuleDefinition originalEngine = ModuleDefinition.ReadModule(Path.Combine(pc, "Gameloft.Engine.dll"), reader))
                {
                    const string component = "Gameloft.Engine.GameOptionPerformanceProfileComponent";
                    MethodDefinition before = RealPc.Method(originalEngine, component, "OnCreate", 0);
                    MethodDefinition onCreate = RealPc.Method(engine, component, "OnCreate", 0);
                    int read = IndexOfCall(before, "Gameloft.Engine.AssetStream", "ReadAllText");
                    const string jsonHook = "call System.String Gameloft.Engine.McvsGraphics::GameOptionsJson(System.String)";
                    const string appliedHook = "call System.Void Gameloft.Engine.McvsGraphics::OptionsApplied(" + component + ")";
                    var inserts = new SortedDictionary<int, string[]>
                    {
                        [read + 1] = new[] { jsonHook },
                        [before.Body.Instructions.Count - 1] = new[] { "ldarg.0", appliedHook },
                    };
                    Assert.Equal(Expected(before, inserts, null), Shape(onCreate));
                    List<string> shape = Shape(onCreate);
                    Assert.Equal(jsonHook, shape[IndexOfCall(onCreate, "Gameloft.Engine.AssetStream", "ReadAllText") + 1]);
                    Assert.Equal(new[] { "ldarg.0", appliedHook, "ret" }, shape.Skip(shape.Count - 3));
                    Assert.Equal(before.Body.Instructions.Count + 3, onCreate.Body.Instructions.Count);

                    // waits: in both WaitAndWork overloads the original plus "ldc.i4 100" right before WaitAny, which is now the timed
                    // WaitAny(WaitHandle[], int) of the PC corlib (mscorlib 4).
                    const string timed = "call System.Int32 System.Threading.WaitHandle::WaitAny(System.Threading.WaitHandle[],System.Int32)";
                    foreach (string overload in new[] { "WaitAndWork(System.Threading.Tasks.Task", "WaitAndWork(System.Threading.ManualResetEventSlim" })
                    {
                        MethodDefinition stock = RealPc.Method(originalEngine, "Gameloft.Engine.TaskExtension", overload, 2);
                        MethodDefinition waits = RealPc.Method(engine, "Gameloft.Engine.TaskExtension", overload, 2);
                        int wait = IndexOfCall(stock, "System.Threading.WaitHandle", "WaitAny");
                        Assert.Equal("System.Int32 System.Threading.WaitHandle::WaitAny(System.Threading.WaitHandle[])",
                                     ((MethodReference)stock.Body.Instructions[wait].Operand).FullName);
                        List<string> expected = Expected(stock, new SortedDictionary<int, string[]> { [wait] = new[] { "ldc.i4 " + WaitsPatch.SliceMs } },
                                                         s => s.StartsWith("call System.Int32 System.Threading.WaitHandle::WaitAny(", StringComparison.Ordinal) ? timed : s);
                        Assert.Equal(expected, Shape(waits));
                        int timedWait = IndexOfCall(waits, "System.Threading.WaitHandle", "WaitAny");
                        var waitAny = (MethodReference)waits.Body.Instructions[timedWait].Operand;
                        Assert.Equal(timed, "call " + waitAny.FullName);
                        Assert.Equal("mscorlib", waitAny.DeclaringType.Scope.Name);
                        Instruction slice = waits.Body.Instructions[timedWait - 1];
                        Assert.True(slice.OpCode == OpCodes.Ldc_I4 || slice.OpCode == OpCodes.Ldc_I4_S, overload + ": " + slice);
                        Assert.Equal(100, Convert.ToInt32(slice.Operand, System.Globalization.CultureInfo.InvariantCulture));
                        Assert.Equal(OpCodes.Ldloc_3, waits.Body.Instructions[timedWait - 2].OpCode);
                        Assert.Equal(stock.Body.Instructions.Count + 1, waits.Body.Instructions.Count);
                    }
                }

                // adminpage: A1 hook before the single ret (IL_0115 before), A2 prefix with the corlib's String.op_Equality
                MethodDefinition list = RealPc.Method(game, "Game.PlayerProfileComponent", "GetAccountMenuButtonList", 0);
                Instruction hook = list.Body.Instructions[list.Body.Instructions.Count - 2];
                Assert.Equal("System.Collections.Generic.IEnumerable`1<System.Object> McvsOffline.AdminHooks::AccountMenuButtons(System.Collections.Generic.IEnumerable`1<System.Object>)",
                             hook.Operand.ToString());
                Assert.Equal(0x115, hook.Offset);
                MethodDefinition action = RealPc.Method(game, "Game.PlayerProfileComponent", "ProcessAccountMenuButtonAction", 1);
                Assert.Equal(new[] { "ldarg.1", "ldstr", "call", "brfalse.s", "ldstr", "ldarg.0", "call", "call", "ret" },
                             action.Body.Instructions.Take(9).Select(i => i.OpCode.Name));
                var equality = (MethodReference)action.Body.Instructions[2].Operand;
                Assert.Equal("System.Boolean System.String::op_Equality(System.String,System.String)", equality.FullName);
                Assert.Equal("mscorlib", equality.DeclaringType.Scope.Name);
                Assert.Equal(6 + 9, action.Body.Instructions.Count);
            }
        }

        /// <summary>The index of the single call to <paramref name="type"/>::<paramref name="name"/> in <paramref name="method"/>.</summary>
        private static int IndexOfCall(MethodDefinition method, string type, string name)
        {
            var code = method.Body.Instructions;
            return Enumerable.Range(0, code.Count).Single(i => code[i].Operand is MethodReference r && r.DeclaringType.FullName == type && r.Name == name);
        }

        /// <summary>
        /// One line per instruction: the opcode (short branch and ldc.i4.s forms written long, so a re-encoding does not count) and
        /// the operand (members by full name; a branch by its target's index).
        /// </summary>
        private static List<string> Shape(MethodDefinition method)
        {
            var code = method.Body.Instructions;
            return code.Select(i => Line(i, target => code.IndexOf(target))).ToList();
        }

        /// <summary>
        /// The shape of <paramref name="original"/> with <paramref name="insertBefore"/> (original index → lines) inserted and each
        /// original line passed through <paramref name="replace"/>; branch targets follow their instruction to its new index.
        /// </summary>
        private static List<string> Expected(MethodDefinition original, SortedDictionary<int, string[]> insertBefore, Func<string, string> replace)
        {
            var code = original.Body.Instructions;
            Func<int, int> moved = index => index + insertBefore.Where(p => p.Key <= index).Sum(p => p.Value.Length);
            var result = new List<string>();
            for (int i = 0; i < code.Count; i++)
            {
                string[] inserted;
                if (insertBefore.TryGetValue(i, out inserted)) result.AddRange(inserted);
                string line = Line(code[i], target => moved(code.IndexOf(target)));
                result.Add(replace == null ? line : replace(line));
            }
            return result;
        }

        private static string Line(Instruction i, Func<Instruction, int> index)
        {
            string op = i.OpCode.OperandType == OperandType.ShortInlineBrTarget ? i.OpCode.Name.Substring(0, i.OpCode.Name.Length - 2) : i.OpCode.Name;
            if (i.OpCode == OpCodes.Ldc_I4_S) return "ldc.i4 " + ((sbyte)i.Operand).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (i.Operand == null) return op;
            if (i.Operand is Instruction target) return op + " -> #" + index(target);
            if (i.Operand is Instruction[] targets) return op + " -> " + string.Join(",", targets.Select(t => "#" + index(t)));
            if (i.Operand is MemberReference member) return op + " " + member.FullName;
            return op + " " + Convert.ToString(i.Operand, System.Globalization.CultureInfo.InvariantCulture);
        }

        [Fact]
        public void IlVerifyFindsWhatTheOriginalsHaveAndNothingMore()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            string tool = RealAdminPagePatchTests.IlVerifyTool();
            if (tool == null)
            {
                Assert.False(TestEnvironment.RequirePc, "MCVS_REQUIRE_PC=1 but ilverify is missing (MCVS_ILVERIFY or ~/.dotnet/tools/ilverify)");
                output.WriteLine("ilverify not found: skipped");
                return;
            }
            byte[] server = PcHookServer.Compile();
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, server, null, Pc, RealPc.FullPlan().ToArray());
            string dir = TestEnvironment.NewTempDir("pc-ilverify");
            string serverFile = Path.Combine(dir, "McvsOffline.Server.dll");
            File.WriteAllBytes(serverFile, server);
            string[] bcl = { "mscorlib", "System", "System.Core", "System.Net.Http", "System.Xml", "System.Xml.Linq", "System.IO.Compression",
                             "System.Windows.Forms", "System.Drawing", "System.Runtime.Serialization", "System.Data", "System.Numerics" };
            List<string> managed = RealPc.ManagedAssemblies(pc);
            foreach (var kv in patched.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                string file = Path.Combine(dir, kv.Key);
                File.WriteAllBytes(file, kv.Value);
                var references = bcl.Select(b => Path.Combine(TestEnvironment.Net452Refs, b + ".dll"))
                    .Concat(managed.Where(m => Path.GetFileName(m) != kv.Key)).Concat(new[] { serverFile }).ToList();
                var arguments = new[] { "--system-module", "mscorlib" };
                List<string> before = RealAdminPagePatchTests.Findings(RealAdminPagePatchTests.IlVerify(tool, Path.Combine(pc, kv.Key), references, arguments).Text);
                List<string> after = RealAdminPagePatchTests.Findings(RealAdminPagePatchTests.IlVerify(tool, file, references, arguments).Text);
                output.WriteLine(kv.Key + ": " + before.Count + " findings in the original, " + after.Count + " after patching");
                foreach (string added in after.Except(before)) output.WriteLine("  new: " + added);
                foreach (string gone in before.Except(after)) output.WriteLine("  gone: " + gone);
                Assert.Equal(before, after);
            }
        }
    }

    /// <summary>Shared helpers of the PC real-file tests (RealPc*Tests, PcHookContractTests).</summary>
    internal static class RealPc
    {
        public static readonly string[] PatchedFiles = { "EngineBridge.dll", "Game.dll", "Gameloft.Engine.dll", "Gameloft.Util.dll" };

        /// <summary>MCVS_PC_REFS with the four patched files and the net452 BCL, or null (skipped; a failure under MCVS_REQUIRE_PC=1).</summary>
        public static string Folder(ITestOutputHelper output)
        {
            string dir = TestEnvironment.PcRefs;
            bool ok = dir != null && TestEnvironment.Net452Refs != null && PatchedFiles.All(f => File.Exists(Path.Combine(dir, f)));
            if (ok) return dir;
            if (TestEnvironment.RequirePc)
                throw new InvalidOperationException("MCVS_REQUIRE_PC=1 but MCVS_PC_REFS (" + dir + ") or the net452 reference assemblies (MCVS_NET452_REFS) are missing");
            output?.WriteLine("the Steam install (MCVS_PC_REFS) or the net452 reference assemblies are not available: skipped");
            return null;
        }

        public static ReaderParameters Reader(string gameFolder, string serverFolder)
        {
            return TestEnvironment.PcReader(gameFolder, serverFolder);
        }

        /// <summary>Every PC patch id the build offers (mandatory, default, optional), in the plan's order.</summary>
        public static IReadOnlyList<IlPatch> FullPlan()
        {
            PatchIds ids = PatchPlan.Ids(BuildProfile.PcSteam6372049);
            return PatchPlan.For(BuildProfile.PcSteam6372049, ids.Default.Concat(ids.Optional));
        }

        /// <summary>The patched images of <paramref name="gameFolder"/> (as the PC installer patches them: AssemblyPatcher, PcReader).</summary>
        public static Dictionary<string, byte[]> Patch(string gameFolder, byte[] server, List<string> notes, BuildProfile build, params IlPatch[] patches)
        {
            string serverDir = TestEnvironment.NewTempDir("pc-server");
            File.WriteAllBytes(Path.Combine(serverDir, "McvsOffline.Server.dll"), server);
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            ReaderParameters reader = Reader(gameFolder, serverDir);
            using (reader.AssemblyResolver)
            using (ModuleDefinition serverModule = ModuleDefinition.ReadModule(new MemoryStream(server), reader))
            {
                var context = new PatchContext(serverModule, 51780, build);
                foreach (var group in patches.GroupBy(p => p.AssemblyFile))
                {
                    byte[] original = File.ReadAllBytes(Path.Combine(gameFolder, group.Key));
                    result[group.Key] = AssemblyPatcher.Patch(original, group.ToList(), context, reader, serverModule.Mvid, s => { });
                }
                if (notes != null) notes.AddRange(context.Notes);
            }
            return result;
        }

        /// <summary>A temp copy of the managed files of <paramref name="gameFolder"/> with <paramref name="patched"/> over them.</summary>
        public static string CopyWith(string gameFolder, Dictionary<string, byte[]> patched)
        {
            string dir = TestEnvironment.NewTempDir("pc-copy");
            foreach (string f in ManagedAssemblies(gameFolder)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            File.Copy(Path.Combine(gameFolder, "ModernCombatVersus.exe"), Path.Combine(dir, "ModernCombatVersus.exe"));
            foreach (var kv in patched) File.WriteAllBytes(Path.Combine(dir, kv.Key), kv.Value);
            return dir;
        }

        /// <summary>The managed DLLs of the game folder (NativeEngine, WCPToolkit, nativegame and steam_api are native).</summary>
        public static List<string> ManagedAssemblies(string gameFolder)
        {
            var result = new List<string>();
            foreach (string f in Directory.GetFiles(gameFolder, "*.dll").OrderBy(f => f, StringComparer.Ordinal))
            {
                using (var stream = File.OpenRead(f))
                using (var pe = new System.Reflection.PortableExecutable.PEReader(stream))
                {
                    if (pe.HasMetadata) result.Add(f);
                }
            }
            return result;
        }

        /// <summary>
        /// The method <paramref name="name"/> of <paramref name="typeName"/> (nested types with '/'); <paramref name="parameters"/>
        /// -1 for any count; a name with '(' selects by the start of the full parameter list.
        /// </summary>
        public static MethodDefinition Method(ModuleDefinition module, string typeName, string name, int parameters)
        {
            string[] path = typeName.Split('/');
            TypeDefinition type = module.GetType(path[0]);
            Assert.True(type != null, module.Name + ": no type " + path[0]);
            for (int i = 1; i < path.Length; i++) type = type.NestedTypes.Single(t => t.Name == path[i]);
            string simple = name, prefix = null;
            int paren = name.IndexOf('(');
            if (paren >= 0)
            {
                simple = name.Substring(0, paren);
                prefix = name.Substring(paren + 1);
            }
            var matches = type.Methods.Where(m => m.Name == simple && (parameters < 0 || m.Parameters.Count == parameters) &&
                                                  (prefix == null || string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)).StartsWith(prefix, StringComparison.Ordinal)))
                              .ToList();
            Assert.True(matches.Count == 1, type.FullName + "::" + name + ": " + matches.Count + " matches");
            return matches[0];
        }

        /// <summary>A digest of every method body's fingerprint (the "module unchanged" check of the refusals).</summary>
        public static string MethodsDigest(ModuleDefinition module)
        {
            var lines = new List<string>();
            foreach (TypeDefinition type in AllTypes(module.Types))
            {
                foreach (MethodDefinition m in type.Methods.Where(m => m.HasBody)) lines.Add(m.FullName + " " + IlPatch.Fingerprint(m));
                lines.Add(type.FullName + " attributes " + type.CustomAttributes.Count + " methods " + type.Methods.Count);
            }
            lines.Sort(StringComparer.Ordinal);
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return string.Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines))).Select(b => b.ToString("x2")));
        }

        public static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (TypeDefinition type in types)
            {
                yield return type;
                foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
            }
        }
    }
}
