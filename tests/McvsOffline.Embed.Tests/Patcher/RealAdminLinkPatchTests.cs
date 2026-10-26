using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;
using GameContext = McvsOffline.Embed.Tests.Patcher.RealGamePatchTests.GameContext;
using OpCodes = Mono.Cecil.Cil.OpCodes;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// The 'adminlink' patch (docs/design/settings-page.md §9.3) applied to the player's REAL Gameloft.Engine.dll (MCVS_REFS)
    /// and executed on net8 next to the unpatched one: <c>BrowserComponent.OpenUrl(url, ingame)</c> first asks
    /// <c>AdminHooks.OpenIfPageMarker(url, this)</c>; for the inbox marker the stand-in answers true and the shipped body (the
    /// server-World check, tracking, the youtube rewrite, URLOpened, the native OpenURL) does not run, for every other URL the
    /// method behaves exactly as shipped. Skipped without the game's assemblies unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    [Collection("GameHooks")]
    public class RealAdminLinkPatchTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const string Engine = "Gameloft.Engine.dll";
        private const string Marker = "http://127.0.0.1:51780/_mcvs/ui/#news";

        /// <summary>Bytes the prefix adds (ldarg.1, ldarg.0, call, brfalse.s, ret; §9.3 L1).</summary>
        private const int PrefixBytes = 1 + 1 + 5 + 2 + 1;

        private readonly ITestOutputHelper output;

        public RealAdminLinkPatchTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private string Refs()
        {
            string dir = TestEnvironment.GameAssemblies;
            if (dir != null && File.Exists(Path.Combine(dir, Engine)) && File.Exists(Path.Combine(dir, "Game.dll"))) return dir;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but MCVS_REFS has no Gameloft.Engine.dll / Game.dll");
            output.WriteLine("the game's assemblies are not available (MCVS_REFS): skipped");
            return null;
        }

        private static readonly object PatchGate = new object();
        private static byte[] server;
        private static Dictionary<string, byte[]> patched;
        private static List<string> patchNotes;

        /// <summary>The recording stand-in server and Gameloft.Engine.dll patched with 'adminlink' alone (patched once for the class).</summary>
        private static Dictionary<string, byte[]> Patched(string refs, out byte[] serverImage)
        {
            lock (PatchGate)
            {
                if (patched == null)
                {
                    server = FixtureCompiler.Compile("McvsOffline.Server", RealAdminPagePatchTests.HooksSource);
                    var notes = new List<string>();
                    patched = RealGamePatchTests.PatchReal(refs, server, notes, new AdminLinkPatch());
                    patchNotes = notes;
                }
                serverImage = server;
                return patched;
            }
        }

        private static MethodDefinition OpenUrl(ModuleDefinition module)
        {
            return module.GetType(AdminLinkPatch.BrowserType).Methods.Single(m => m.Name == "OpenUrl" && m.Parameters.Count == 2);
        }

        private static string Handler(ExceptionHandler h, int shift)
        {
            string Label(Instruction i) => i == null ? "end" : "IL_" + (i.Offset - shift).ToString("x4");
            return h.HandlerType + " " + Label(h.TryStart) + "-" + Label(h.TryEnd) + " -> " + Label(h.HandlerStart) + "-" + Label(h.HandlerEnd) + " " + h.CatchType?.FullName;
        }

        // ------------------------------------------------------------------ IL

        [Fact]
        public void TheShippedMethodHasTheVerifiedFingerprintAndEveryOpenerCallsIt()
        {
            string refs = Refs();
            if (refs == null) return;
            using (var resolver = new DefaultAssemblyResolver())
            using (var engine = ModuleDefinition.ReadModule(Path.Combine(refs, Engine), new ReaderParameters { AssemblyResolver = resolver }))
            using (var game = ModuleDefinition.ReadModule(Path.Combine(refs, "Game.dll"), new ReaderParameters { AssemblyResolver = resolver }))
            {
                resolver.AddSearchDirectory(refs);
                MethodDefinition openUrl = OpenUrl(engine);
                Assert.Equal(AdminLinkPatch.OpenUrlFingerprint, IlPatch.Fingerprint(openUrl));
                Assert.Equal(73, openUrl.Body.Instructions.Count);
                Assert.Equal("Catch IL_0071-IL_00ac -> IL_00ac-IL_00af System.Object", Handler(Assert.Single(openUrl.Body.ExceptionHandlers), 0));
                Assert.Equal("IL_0000: ldarg.0", openUrl.Body.Instructions[0].ToString());
                Assert.Equal("IL_0001: call Gameloft.Engine.World Gameloft.Engine.Component::get_World()", openUrl.Body.Instructions[1].ToString());

                // The Flash native OpenUrl (UIComponent.OpenUrl, MenuComponent.NativeOpenUrl) and MenuManager.Hyperlink all end in it,
                // so the inbox button reaches the prefix whichever registration of the native wins.
                MethodDefinition ui = game.GetType("Game.UIComponent").Methods.Single(m => m.Name == "OpenUrl" && m.Parameters.Count == 2);
                Assert.Equal("bee1818b1ee4aea9b06dc51e0ce18a900a40bf96c3127666c9a4b7908825001c", IlPatch.Fingerprint(ui));
                Assert.Equal(15, ui.Body.Instructions.Count);
                bool CallsOpenUrl(MethodDefinition m) => m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "OpenUrl" &&
                                                                                       r.DeclaringType.FullName == AdminLinkPatch.BrowserType && r.Parameters.Count == 2);
                Assert.True(CallsOpenUrl(ui));
                Assert.True(CallsOpenUrl(engine.GetType("Gameloft.Engine.MenuComponent").Methods.Single(m => m.Name == "NativeOpenUrl")));
                Assert.True(CallsOpenUrl(engine.GetType("Gameloft.Engine.MenuManager").Methods.Single(m => m.Name == "Hyperlink")));
                foreach (ModuleDefinition module in new[] { engine, game })
                {
                    foreach (MethodDefinition m in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody && CallsOpenUrl(m)))
                        output.WriteLine("calls BrowserComponent.OpenUrl: " + m.FullName);
                }
            }
        }

        [Fact]
        public void ThePatchedIlIsThePrefixAndTheShippedBody()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out _);
            foreach (string n in patchNotes) output.WriteLine(n);
            string note = Assert.Single(patchNotes);
            Assert.Contains("BrowserComponent::OpenUrl(System.String,System.Boolean)", note);
            Assert.Contains("OpenIfPageMarker(url, this)", note);

            using (var resolver = new DefaultAssemblyResolver())
            using (ModuleDefinition shipped = ModuleDefinition.ReadModule(Path.Combine(refs, Engine), new ReaderParameters { AssemblyResolver = resolver }))
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(images[Engine]), new ReaderParameters { AssemblyResolver = resolver }))
            {
                resolver.AddSearchDirectory(refs);
                Assert.Contains(module.AssemblyReferences, r => r.Name == "McvsOffline.Server");
                MethodDefinition original = OpenUrl(shipped), openUrl = OpenUrl(module);
                var code = openUrl.Body.Instructions;
                Assert.Equal(original.Body.Instructions.Count + 5, code.Count);
                Assert.Equal(OpCodes.Ldarg_1, code[0].OpCode);
                Assert.Equal(OpCodes.Ldarg_0, code[1].OpCode);
                RealAdminPagePatchTests.AssertHookCall(code[2], "OpenIfPageMarker", "System.Boolean", "System.String", "System.Object");
                Assert.Equal(OpCodes.Brfalse_S, code[3].OpCode);
                Assert.Same(code[5], code[3].Operand);
                Assert.Equal(OpCodes.Ret, code[4].OpCode);
                Assert.Equal(PrefixBytes, code[5].Offset);

                // The shipped body and its youtube.com try block, unchanged but for the offsets; the same locals.
                Assert.Equal(original.Body.Instructions.Select(i => i.ToString()), code.Skip(5).Select(i => RealAdminPagePatchTests.Shifted(i, PrefixBytes)));
                Assert.Equal(original.Body.ExceptionHandlers.Select(h => Handler(h, 0)), openUrl.Body.ExceptionHandlers.Select(h => Handler(h, PrefixBytes)));
                Assert.Equal(original.Body.Variables.Select(v => v.VariableType.FullName), openUrl.Body.Variables.Select(v => v.VariableType.FullName));
                Assert.True(openUrl.Body.InitLocals);
                Assert.Equal(original.Parameters.Select(p => p.ParameterType.FullName + " " + p.Name + " " + p.IsOptional + " " + p.Constant),
                             openUrl.Parameters.Select(p => p.ParameterType.FullName + " " + p.Name + " " + p.IsOptional + " " + p.Constant));

                // Nothing else of the browser component changed.
                TypeDefinition type0 = shipped.GetType(AdminLinkPatch.BrowserType), type = module.GetType(AdminLinkPatch.BrowserType);
                Assert.Equal(type0.Methods.Count, type.Methods.Count);
                foreach (MethodDefinition m0 in type0.Methods.Where(m => m.HasBody && m != original))
                    Assert.Equal(IlPatch.Fingerprint(m0), IlPatch.Fingerprint(type.Methods.Single(x => x.FullName == m0.FullName)));
            }
        }

        // ------------------------------------------------------------------ execution

        /// <summary>One side of the differential: a BrowserComponent of a client World whose URLOpened event records every URL.</summary>
        private sealed class Browser
        {
            public readonly List<string> Opened = new List<string>();
            public readonly object Component;
            public readonly object World;
            private readonly MethodInfo openUrl;

            public Browser(Assembly engine, bool? isServer, bool attached = true, bool native = true)
            {
                Type type = engine.GetType(AdminLinkPatch.BrowserType, true);
                World = attached ? RuntimeHelpers.GetUninitializedObject(engine.GetType("Gameloft.Engine.World", true)) : null;
                if (World != null) World.GetType().GetField("<IsServer>k__BackingField", Any).SetValue(World, isServer);
                Component = RealAdminPagePatchTests.Component(engine, type, World);
                type.GetField("URLOpened", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Component, new EventHandler<string>((sender, url) => Opened.Add(url)));
                // A non-zero native handle: the shipped body goes all the way to the P/Invoke (no "disposed" exception first).
                if (native) type.GetField("self", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Component, new IntPtr(1));
                openUrl = type.GetMethods(Any).Single(m => m.Name == "OpenUrl" && m.GetParameters().Length == 2);
            }

            /// <summary>"returned" or what was thrown, then the URLs URLOpened saw.</summary>
            public string Open(string url, bool ingame = true)
            {
                string outcome;
                try
                {
                    openUrl.Invoke(Component, new object[] { url, ingame });
                    outcome = "returned";
                }
                catch (TargetInvocationException ex)
                {
                    // The P/Invoke's message lists the probed paths, which depend on where the engine was loaded from: the type says enough.
                    Exception e = ex.InnerException;
                    outcome = e is DllNotFoundException || e is EntryPointNotFoundException ? e.GetType().FullName : e.GetType().FullName + ": " + e.Message;
                }
                return outcome + " | opened: " + string.Join(", ", Opened);
            }
        }

        [Fact]
        public void TheMarkerOpensThePageAndEveryOtherUrlTakesTheShippedPath()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);

            using (var game = new GameContext(refs, images, serverImage))
            using (var original = new GameContext(refs, new Dictionary<string, byte[]>(), serverImage))
            {
                Type hooks = game.Get("McvsOffline.Server").GetType(AdminLinkPatch.HooksType, true);
                int Calls() => (int)hooks.GetField("MarkerCalls").GetValue(null);
                Assembly engine = game.Get("Gameloft.Engine"), engine0 = original.Get("Gameloft.Engine");

                // The marker: the hook got (url, this), and nothing of the shipped body ran (no URLOpened, no native call).
                var browser = new Browser(engine, false);
                Assert.Equal("returned | opened: ", browser.Open(Marker));
                Assert.Equal(1, Calls());
                Assert.Equal(Marker, hooks.GetField("LastUrl").GetValue(null));
                Assert.Same(browser.Component, hooks.GetField("LastBrowser").GetValue(null));
                Assert.Equal(0, hooks.GetField("OpenCalls").GetValue(null));   // the stand-in only answers; the real hook opens the page
                Assert.Equal("returned | opened: ", new Browser(engine, false).Open(Marker, false));
                // Also from a component without a World: the shipped body would throw on base.World.IsServer.
                Assert.Equal("returned | opened: ", new Browser(engine, null, attached: false).Open(Marker));
                Assert.StartsWith("System.NullReferenceException", new Browser(engine0, null, attached: false).Open(Marker));
                // What the shipped method does with the marker URL itself (this is what an unpatched game does: it loads the page).
                string shippedMarker = new Browser(engine0, false).Open(Marker);
                output.WriteLine("shipped, marker: " + shippedMarker);
                Assert.EndsWith("| opened: " + Marker, shippedMarker);
                Assert.True(shippedMarker.StartsWith("System.DllNotFoundException") || shippedMarker.StartsWith("System.EntryPointNotFoundException"), shippedMarker);
                Assert.Equal(3, Calls());

                // Every other URL (the stand-in answers false): exactly the shipped outcome, URLOpened included (differential).
                string[] urls =
                {
                    "https://www.example.com/page", "https://www.youtube.com/watch?v=RXclfM3i-Rw", "https://www.youtube.com/channel/UCF1C_Ptm9Pdtpbo1n0C42LQ",
                    "https://www.facebook.com/moderncombatversusgame", "http://127.0.0.1:51780/_mcvs/ui/#t=0123456789abcdef0123456789abcdef",
                    "http://127.0.0.1:51781/_mcvs/ui/#news", "http://127.0.0.1:51780/_mcvs/ui/", "HTTP://127.0.0.1:51780/_MCVS/UI/#NEWS", "", null,
                };
                int calls = Calls();
                foreach (string url in urls)
                {
                    foreach (bool? isServer in new bool?[] { false, true, null })
                    {
                        foreach (bool ingame in new[] { true, false })
                        {
                            string expected = new Browser(engine0, isServer).Open(url, ingame);
                            Assert.Equal(expected, new Browser(engine, isServer).Open(url, ingame));
                            Assert.Equal(++calls, Calls());
                            Assert.Equal(url, hooks.GetField("LastUrl").GetValue(null));
                            if (isServer == false && ingame) output.WriteLine((url ?? "null") + ": " + expected);
                        }
                    }
                    Assert.Equal(new Browser(engine0, null, attached: false).Open(url), new Browser(engine, null, attached: false).Open(url));
                    calls++;
                }
                // The youtube rewrite in the try block still works, and a client World reaches the native call for every URL.
                Assert.Contains("opened: https://www.youtube.com/embed/RXclfM3i-Rw", new Browser(engine, false).Open(urls[1]));
                Assert.Equal("returned | opened: ", new Browser(engine, true).Open(urls[0]));   // server Worlds return before anything
            }
        }

        [Fact]
        public void ThePatchedMethodJitsAndASecondRunIsRefused()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);
            using (var game = new GameContext(refs, images, serverImage))
            {
                foreach (MethodInfo m in game.Get("Gameloft.Engine").GetType(AdminLinkPatch.BrowserType, true).GetMethods(Any).Where(m => m.Name == "OpenUrl"))
                {
                    RuntimeHelpers.PrepareMethod(m.MethodHandle);   // InvalidProgramException on bad IL
                    output.WriteLine("JIT ok: " + m);
                }
            }

            string dir = TestEnvironment.NewTempDir("adminlink-repatch");
            foreach (string f in Directory.GetFiles(refs, "*.dll")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            File.WriteAllBytes(Path.Combine(dir, Engine), images[Engine]);
            string message = Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(dir, serverImage, null, new AdminLinkPatch())).Message;
            Assert.Contains("IL fingerprint", message);
            Assert.Contains("BrowserComponent.OpenUrl", message);

            byte[] noHooks = FixtureCompiler.Compile("McvsOffline.Server", "namespace McvsOffline { public static class Bootstrap { public static void Init() { } } }");
            Assert.Contains("has no type McvsOffline.AdminHooks", Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(refs, noHooks, null, new AdminLinkPatch())).Message);
            byte[] wrong = FixtureCompiler.Compile("McvsOffline.Server", RealAdminPagePatchTests.HooksSource.Replace("public static bool OpenIfPageMarker(string url, object browserComponent)",
                                                                                                                  "public static void OpenIfPageMarker(string url, object browserComponent)")
                                                                                                         .Replace("return url == Marker;", ""));
            Assert.Contains("OpenIfPageMarker(System.String, System.Object)", Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(refs, wrong, null, new AdminLinkPatch())).Message);
        }

        [Fact]
        public void IlVerifyAcceptsThePatchedMethod()
        {
            string refs = Refs();
            if (refs == null) return;
            RealAdminPagePatchTests.AssertIlVerifyClean(output, refs, Engine, new IlPatch[] { new AdminLinkPatch() },
                                                        new[] { @"\]Gameloft\.Engine\.BrowserComponent\.OpenUrl$" }, 1, "BrowserComponent::OpenUrl");
        }
    }
}
