using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Patches;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;
using GameContext = McvsOffline.Embed.Tests.Patcher.RealGamePatchTests.GameContext;
using OpCodes = Mono.Cecil.Cil.OpCodes;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// The settings-page triggers of Game.dll (docs/design/settings-page.md §9.2, §9.4) applied to the player's REAL Game.dll
    /// (MCVS_REFS) and executed on net8 next to the unpatched one: 'adminpage' makes
    /// <c>PlayerProfileComponent.GetAccountMenuButtonList</c> return <c>AdminHooks.AccountMenuButtons(list)</c> and
    /// <c>ProcessAccountMenuButtonAction("mcvs_offline")</c> call <c>AdminHooks.OpenPage("account", World)</c> instead of the
    /// shipped body, every other action running that body as before; the optional 'forum' patch makes
    /// <c>UIComponent.GoToForum</c> call <c>AdminHooks.OpenPage("forum", World)</c>. The stand-in server records the hook calls.
    /// Skipped without the game's assemblies unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    [Collection("GameHooks")]
    public class RealAdminPagePatchTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const string GameDll = "Game.dll";

        /// <summary>
        /// The stand-in McvsOffline.Server of the settings-page patch tests: McvsOffline.AdminHooks with the signatures of §8.1,
        /// recording every call (the real hooks are hosts/embedded/Admin/AdminHooks.cs). OpenIfPageMarker answers true for
        /// <c>Marker</c> only.
        /// </summary>
        internal const string HooksSource = @"
namespace McvsOffline
{
    public static class AdminHooks
    {
        public static int ButtonCalls;
        public static object LastButtons;
        public static int OpenCalls;
        public static string LastTrigger;
        public static object LastWorld;
        public static int MarkerCalls;
        public static string LastUrl;
        public static object LastBrowser;
        public static string Marker = ""http://127.0.0.1:51780/_mcvs/ui/#news"";

        public static System.Collections.Generic.IEnumerable<object> AccountMenuButtons(System.Collections.Generic.IEnumerable<object> buttons)
        {
            ButtonCalls++; LastButtons = buttons; return buttons;
        }

        public static void OpenPage(string trigger, object world) { OpenCalls++; LastTrigger = trigger; LastWorld = world; }

        public static bool OpenIfPageMarker(string url, object browserComponent)
        {
            MarkerCalls++; LastUrl = url; LastBrowser = browserComponent; return url == Marker;
        }
    }
}";

        /// <summary>Bytes the 'adminpage' prefix of ProcessAccountMenuButtonAction adds (nine instructions, §9.2 A2).</summary>
        private const int ActionPrefixBytes = 1 + 5 + 5 + 2 + 5 + 1 + 5 + 5 + 1;

        private readonly ITestOutputHelper output;

        public RealAdminPagePatchTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private string Refs()
        {
            string dir = TestEnvironment.GameAssemblies;
            if (dir != null && File.Exists(Path.Combine(dir, GameDll))) return dir;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but MCVS_REFS has no Game.dll");
            output.WriteLine("the game's assemblies are not available (MCVS_REFS): skipped");
            return null;
        }

        private static readonly object PatchGate = new object();
        private static byte[] server;
        private static Dictionary<string, byte[]> pageOnly, pageAndForum;
        private static List<string> pageNotes, allNotes;

        /// <summary>
        /// The recording stand-in server and Game.dll patched with 'adminpage' alone and with 'adminpage' + 'forum' (patched once
        /// for the class).
        /// </summary>
        private static Dictionary<string, byte[]> Patched(string refs, out byte[] serverImage, out Dictionary<string, byte[]> withForum)
        {
            lock (PatchGate)
            {
                if (pageOnly == null)
                {
                    server = FixtureCompiler.Compile("McvsOffline.Server", HooksSource);
                    var notes = new List<string>();
                    pageOnly = RealGamePatchTests.PatchReal(refs, server, notes, new AdminPagePatch());
                    pageNotes = notes;
                    notes = new List<string>();
                    pageAndForum = RealGamePatchTests.PatchReal(refs, server, notes, new AdminPagePatch(), new ForumPatch());
                    allNotes = notes;
                }
                serverImage = server;
                withForum = pageAndForum;
                return pageOnly;
            }
        }

        private static MethodDefinition Method(ModuleDefinition module, string type, string name)
        {
            return module.GetType(type).Methods.Single(m => m.Name == name);
        }

        private static ModuleDefinition Read(byte[] image, string refs, DefaultAssemblyResolver resolver)
        {
            resolver.AddSearchDirectory(refs);
            foreach (string d in resolver.GetSearchDirectories().Where(d => d != refs).ToList()) resolver.RemoveSearchDirectory(d);
            return ModuleDefinition.ReadModule(new MemoryStream(image), new ReaderParameters { AssemblyResolver = resolver });
        }

        /// <summary>Cecil's "IL_0007: ldc.i4.2" rendering with every label moved back by <paramref name="shift"/> bytes.</summary>
        internal static string Shifted(Instruction instruction, int shift)
        {
            return Regex.Replace(instruction.ToString(), "IL_([0-9a-f]{4})", m => "IL_" + (Convert.ToInt32(m.Groups[1].Value, 16) - shift).ToString("x4"));
        }

        /// <summary>A call of the stand-in server's McvsOffline.AdminHooks::<paramref name="name"/> with exactly that signature.</summary>
        internal static void AssertHookCall(Instruction call, string name, string returnType, params string[] parameters)
        {
            Assert.Equal(OpCodes.Call, call.OpCode);
            var method = (MethodReference)call.Operand;
            Assert.Equal(AdminPagePatch.HooksType, method.DeclaringType.FullName);
            Assert.Equal("McvsOffline.Server", method.DeclaringType.Scope.Name);
            Assert.Equal(name, method.Name);
            Assert.False(method.HasThis);
            Assert.Equal(returnType, method.ReturnType.FullName);
            Assert.Equal(parameters, method.Parameters.Select(p => p.ParameterType.FullName));
        }

        /// <summary>An uninitialised <paramref name="type"/> whose GameObject belongs to <paramref name="world"/> (none when null).</summary>
        internal static object Component(Assembly engine, Type type, object world)
        {
            object component = RuntimeHelpers.GetUninitializedObject(type);
            if (world == null) return component;
            Type gameObject = engine.GetType("Gameloft.Engine.GameObject", true);
            object go = RuntimeHelpers.GetUninitializedObject(gameObject);
            gameObject.GetField("world", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(go, world);
            engine.GetType("Gameloft.Engine.Component", true).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                  .Single(f => f.FieldType == gameObject).SetValue(component, go);
            return component;
        }

        /// <summary>
        /// An uninitialised World of a signed-in client, enough for <c>World.Social.GetUserComponent&lt;SocialComponent&gt;()</c>
        /// to answer non-null: Synchronization with its client enabled, Authentication with a session of fed id "fed_id:mcvs",
        /// and Social with that user's SocialComponent ready; with <paramref name="crm"/> also a GameCrmManager without an
        /// IapStoreComponent as CRM. Every other global component (CRM too without <paramref name="crm"/>) is null.
        /// </summary>
        internal static object SignedInWorld(GameContext context, bool crm)
        {
            Assembly engine = context.Get("Gameloft.Engine"), platform = context.Get("Gameloft.OnlinePlatform");
            Type EngineType(string name) => engine.GetType("Gameloft.Engine." + name, true);
            Type worldType = EngineType("World"), componentType = EngineType("Component");
            object world = RuntimeHelpers.GetUninitializedObject(worldType);
            var globals = new Dictionary<Type, object>();

            object client = RuntimeHelpers.GetUninitializedObject(EngineType("ClientComponent"));
            FieldInfo status = componentType.GetField("status", Any);
            status.SetValue(client, Enum.ToObject(status.FieldType, 1));   // Component.StatusFlags.Enabled: Synchronization.IsClient
            object synchronization = RuntimeHelpers.GetUninitializedObject(EngineType("SynchronizationComponent"));
            EngineType("SynchronizationComponent").GetField("Client", Any).SetValue(synchronization, client);
            globals[EngineType("SynchronizationComponent")] = synchronization;

            object fedId = Activator.CreateInstance(platform.GetType("Gameloft.OnlinePlatform.Credential", true), "fed_id:mcvs");
            Type sessionType = platform.GetType("Gameloft.OnlinePlatform.Session", true);
            object session = RuntimeHelpers.GetUninitializedObject(sessionType);
            sessionType.GetField("<FedId>k__BackingField", Any).SetValue(session, fedId);
            object authentication = RuntimeHelpers.GetUninitializedObject(EngineType("AuthenticationComponent"));
            EngineType("AuthenticationComponent").GetField("<Session>k__BackingField", Any).SetValue(authentication, session);
            globals[EngineType("AuthenticationComponent")] = authentication;

            // ManagerBase.readyComponents: fed id -> component type -> its completion source.
            FieldInfo ready = EngineType("ManagerBase").GetField("readyComponents", Any);
            Type perUser = ready.FieldType.GetGenericArguments()[1], source = perUser.GetGenericArguments()[1];
            object socialReady = Activator.CreateInstance(source);
            source.GetMethod("SetResult").Invoke(socialReady, new[] { RuntimeHelpers.GetUninitializedObject(EngineType("SocialComponent")) });
            object socialComponents = Activator.CreateInstance(perUser);
            perUser.GetMethod("Add").Invoke(socialComponents, new object[] { EngineType("SocialComponent"), socialReady });
            object socialUsers = Activator.CreateInstance(ready.FieldType);
            ready.FieldType.GetMethod("TryAdd").Invoke(socialUsers, new[] { fedId, socialComponents });
            object social = Component(engine, EngineType("SocialManager"), world);
            ready.SetValue(social, socialUsers);
            globals[EngineType("SocialManager")] = social;

            if (crm)
            {
                object manager = Component(engine, context.Get("Game").GetType("Game.GameCrmManager", true), world);
                ready.SetValue(manager, Activator.CreateInstance(ready.FieldType));
                globals[EngineType("CrmManager")] = manager;
            }

            Type cacheType = context.Get("Gameloft.Util").GetType("Gameloft.Util.ConcurrentCache`2", true).MakeGenericType(typeof(Type), componentType);
            Delegate lookup = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(Type), componentType), new GlobalComponents(globals),
                                                      typeof(GlobalComponents).GetMethod(nameof(GlobalComponents.Get)).MakeGenericMethod(componentType));
            worldType.GetField("globalComponents", Any).SetValue(world, Activator.CreateInstance(cacheType, lookup));
            return world;
        }

        /// <summary>The global components of <see cref="SignedInWorld"/> by the type asked for, null for any other.</summary>
        public sealed class GlobalComponents
        {
            private readonly Dictionary<Type, object> components;

            public GlobalComponents(Dictionary<Type, object> components)
            {
                this.components = components;
            }

            public TComponent Get<TComponent>(Type type) where TComponent : class
            {
                return components.TryGetValue(type, out object component) ? (TComponent)component : null;
            }
        }

        /// <summary>"returned", or the type and message of what the call threw.</summary>
        internal static string Outcome(MethodInfo method, object target, params object[] arguments)
        {
            try
            {
                method.Invoke(target, arguments);
                return "returned";
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException.GetType().FullName + ": " + ex.InnerException.Message;
            }
        }

        // ------------------------------------------------------------------ IL

        [Fact]
        public void TheShippedMethodsHaveTheVerifiedFingerprints()
        {
            string refs = Refs();
            if (refs == null) return;
            using (var module = ModuleDefinition.ReadModule(Path.Combine(refs, GameDll)))
            {
                MethodDefinition list = Method(module, AdminPagePatch.ProfileType, "GetAccountMenuButtonList");
                Assert.Equal(AdminPagePatch.ButtonListFingerprint, IlPatch.Fingerprint(list));
                Assert.Equal(93, list.Body.Instructions.Count);
                Assert.Equal("System.Collections.Generic.List`1<System.Object>", Assert.Single(list.Body.Variables).VariableType.FullName);
                Assert.Equal("IL_014f: ldloc.0", list.Body.Instructions[91].ToString());
                Assert.Equal("IL_0150: ret", list.Body.Instructions[92].ToString());

                MethodDefinition action = Method(module, AdminPagePatch.ProfileType, "ProcessAccountMenuButtonAction");
                Assert.Equal(AdminPagePatch.ButtonActionFingerprint, IlPatch.Fingerprint(action));
                Assert.Equal(25, action.Body.Instructions.Count);
                Assert.Empty(action.Body.ExceptionHandlers);
                Assert.Equal("IL_0001: call Gameloft.Engine.World Gameloft.Engine.Component::get_World()", action.Body.Instructions[1].ToString());
                Assert.Equal("IL_0018: call System.Boolean System.String::op_Equality(System.String,System.String)",
                             action.Body.Instructions.Single(i => i.Offset == 0x18).ToString());

                MethodDefinition forum = Method(module, ForumPatch.UiType, "GoToForum");
                Assert.Equal(ForumPatch.ForumFingerprint, IlPatch.Fingerprint(forum));
                Assert.Equal(new[]
                {
                    "IL_0000: ldarg.0", "IL_0001: call Gameloft.Engine.World Gameloft.Engine.Component::get_World()",
                    "IL_0006: callvirt Gameloft.Engine.BrowserComponent Gameloft.Engine.World::get_Browser()",
                    "IL_000b: callvirt System.Void Gameloft.Engine.BrowserComponent::ShowForum()", "IL_0010: ret",
                }, forum.Body.Instructions.Select(i => i.ToString()));
            }
        }

        [Fact]
        public void TheAdminPageIlIsTheShippedCodeWithTheTwoChanges()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out _, out _);
            foreach (string n in pageNotes) output.WriteLine(n);
            Assert.Equal(2, pageNotes.Count);
            Assert.Contains(pageNotes, n => n.Contains("PlayerProfileComponent::GetAccountMenuButtonList") && n.Contains("AdminHooks.AccountMenuButtons(list)"));
            Assert.Contains(pageNotes, n => n.Contains("PlayerProfileComponent::ProcessAccountMenuButtonAction") && n.Contains("OpenPage(\"account\", World)"));

            using (var shippedResolver = new DefaultAssemblyResolver())
            using (var patchedResolver = new DefaultAssemblyResolver())
            using (ModuleDefinition shipped = Read(File.ReadAllBytes(Path.Combine(refs, GameDll)), refs, shippedResolver))
            using (ModuleDefinition patched = Read(images[GameDll], refs, patchedResolver))
            {
                Assert.Contains(patched.AssemblyReferences, r => r.Name == "McvsOffline.Server");

                // A1: ldloc.0; call AdminHooks::AccountMenuButtons; ret - everything before it as shipped, offsets included.
                MethodDefinition list0 = Method(shipped, AdminPagePatch.ProfileType, "GetAccountMenuButtonList");
                MethodDefinition list = Method(patched, AdminPagePatch.ProfileType, "GetAccountMenuButtonList");
                var code = list.Body.Instructions;
                Assert.Equal(list0.Body.Instructions.Count + 1, code.Count);
                Assert.Equal(list0.Body.Instructions.Take(92).Select(i => i.ToString()), code.Take(92).Select(i => i.ToString()));
                Assert.Equal("IL_014f: ldloc.0", code[91].ToString());
                AssertHookCall(code[92], "AccountMenuButtons", AdminPagePatch.ButtonList, AdminPagePatch.ButtonList);
                Assert.Equal(OpCodes.Ret, code[93].OpCode);
                Assert.Single(code, i => i.OpCode == OpCodes.Ret);
                Assert.Equal(list0.Body.Variables.Select(v => v.VariableType.FullName), list.Body.Variables.Select(v => v.VariableType.FullName));
                Assert.Empty(list.Body.ExceptionHandlers);

                // A2: the nine-instruction prefix of §9.2, then the shipped body unchanged (offsets shifted by the prefix only).
                MethodDefinition action0 = Method(shipped, AdminPagePatch.ProfileType, "ProcessAccountMenuButtonAction");
                MethodDefinition action = Method(patched, AdminPagePatch.ProfileType, "ProcessAccountMenuButtonAction");
                code = action.Body.Instructions;
                Assert.Equal(action0.Body.Instructions.Count + 9, code.Count);
                Assert.Equal(OpCodes.Ldarg_1, code[0].OpCode);
                Assert.Equal(OpCodes.Ldstr, code[1].OpCode);
                Assert.Equal(AdminPagePatch.ButtonAction, code[1].Operand);
                Assert.Equal("call System.Boolean System.String::op_Equality(System.String,System.String)", code[2].ToString().Substring(9));
                Assert.Equal(OpCodes.Brfalse_S, code[3].OpCode);
                Assert.Same(code[9], code[3].Operand);
                Assert.Equal(OpCodes.Ldstr, code[4].OpCode);
                Assert.Equal(AdminPagePatch.Trigger, code[4].Operand);
                Assert.Equal(OpCodes.Ldarg_0, code[5].OpCode);
                Assert.Equal("call Gameloft.Engine.World Gameloft.Engine.Component::get_World()", code[6].ToString().Substring(9));
                AssertHookCall(code[7], "OpenPage", "System.Void", "System.String", "System.Object");
                Assert.Equal(OpCodes.Ret, code[8].OpCode);
                Assert.Equal(ActionPrefixBytes, code[9].Offset);
                Assert.Equal(action0.Body.Instructions.Select(i => i.ToString()), code.Skip(9).Select(i => Shifted(i, ActionPrefixBytes)));
                Assert.Empty(action.Body.ExceptionHandlers);
                Assert.Empty(action.Body.Variables);

                // Nothing else of the profile component changed.
                TypeDefinition profile0 = shipped.GetType(AdminPagePatch.ProfileType), profile = patched.GetType(AdminPagePatch.ProfileType);
                Assert.Equal(profile0.Methods.Count, profile.Methods.Count);
                foreach (MethodDefinition m0 in profile0.Methods.Where(m => m.HasBody && m.Name != "GetAccountMenuButtonList" && m.Name != "ProcessAccountMenuButtonAction"))
                {
                    MethodDefinition m = profile.Methods.Single(x => x.FullName == m0.FullName);
                    Assert.Equal(IlPatch.Fingerprint(m0), IlPatch.Fingerprint(m));
                }
            }
        }

        // ------------------------------------------------------------------ execution

        [Fact]
        public void TheAccountActionOpensThePageAndEveryOtherActionRunsTheShippedBody()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage, out _);

            using (var game = new GameContext(refs, images, serverImage))
            using (var original = new GameContext(refs, new Dictionary<string, byte[]>(), serverImage))
            {
                Type hooks = game.Get("McvsOffline.Server").GetType(AdminPagePatch.HooksType, true);
                int Opens() => (int)hooks.GetField("OpenCalls").GetValue(null);
                Assembly engine = game.Get("Gameloft.Engine"), engine0 = original.Get("Gameloft.Engine");
                Type profile = game.Get("Game").GetType(AdminPagePatch.ProfileType, true), profile0 = original.Get("Game").GetType(AdminPagePatch.ProfileType, true);
                MethodInfo action = profile.GetMethod("ProcessAccountMenuButtonAction", Any), action0 = profile0.GetMethod("ProcessAccountMenuButtonAction", Any);
                object world = RuntimeHelpers.GetUninitializedObject(engine.GetType("Gameloft.Engine.World", true));
                object world0 = RuntimeHelpers.GetUninitializedObject(engine0.GetType("Gameloft.Engine.World", true));
                object component = Component(engine, profile, world), component0 = Component(engine0, profile0, world0);

                // The button's action: the hook with ("account", World), and the shipped body (World.Social...) never runs.
                Assert.Equal("returned", Outcome(action, component, AdminPagePatch.ButtonAction));
                Assert.Equal(1, Opens());
                Assert.Equal("account", hooks.GetField("LastTrigger").GetValue(null));
                Assert.Same(world, hooks.GetField("LastWorld").GetValue(null));
                Assert.NotEqual("returned", Outcome(action0, component0, AdminPagePatch.ButtonAction));   // the shipped body fails here: proof it would have run

                // Without a GameObject base.World is null: the hook gets null (it opens the page through GameApplication.Instance).
                Assert.Equal("returned", Outcome(action, Component(engine, profile, null), AdminPagePatch.ButtonAction));
                Assert.Equal(2, Opens());
                Assert.Null(hooks.GetField("LastWorld").GetValue(null));

                // Every other action: no hook call and exactly the shipped outcome (differential). On the bare World and without
                // one the shipped body fails at World.Social before it reads the action; on a signed-in client's World it reaches
                // the action compare, and "restore_purchase" goes on to World.CRM: without a CRM that fails, with a CRM that has
                // no IapStoreComponent it returns, as every other action does on both.
                var arms = new[]
                {
                    (Name: "bare World", Patched: component, Shipped: component0),
                    (Name: "no World", Patched: Component(engine, profile, null), Shipped: Component(engine0, profile0, null)),
                    (Name: "signed in, no CRM", Patched: Component(engine, profile, SignedInWorld(game, false)), Shipped: Component(engine0, profile0, SignedInWorld(original, false))),
                    (Name: "signed in, CRM", Patched: Component(engine, profile, SignedInWorld(game, true)), Shipped: Component(engine0, profile0, SignedInWorld(original, true))),
                };
                var shipped = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var arm in arms)
                {
                    foreach (string other in new[] { "restore_purchase", "x", "", null, "MCVS_OFFLINE", "mcvs_offline ", "showRedeem" })
                    {
                        string expected = Outcome(action0, arm.Shipped, other);
                        Assert.Equal(expected, Outcome(action, arm.Patched, other));
                        string name = arm.Name + ", action " + (other == null ? "null" : "\"" + other + "\"");
                        shipped[name] = expected;
                        output.WriteLine(name + ": " + expected);
                    }
                }
                Assert.Equal(2, Opens());
                Assert.Equal(0, hooks.GetField("ButtonCalls").GetValue(null));

                // The signed-in arms told the actions apart: the shipped body read the action there and ran restore_purchase's path.
                const string restoreWithoutCrm = "signed in, no CRM, action \"restore_purchase\"";
                Assert.StartsWith("System.NullReferenceException", shipped[restoreWithoutCrm]);
                Assert.All(shipped.Where(kv => kv.Key.StartsWith("signed in", StringComparison.Ordinal) && kv.Key != restoreWithoutCrm),
                           kv => Assert.Equal("returned", kv.Value));
            }
        }

        [Fact]
        public void TheForumButtonOpensThePage()
        {
            string refs = Refs();
            if (refs == null) return;
            Patched(refs, out byte[] serverImage, out Dictionary<string, byte[]> images);
            foreach (string n in allNotes) output.WriteLine(n);
            Assert.Equal(pageNotes, allNotes.Take(2));
            string note = Assert.Single(allNotes.Skip(2));
            Assert.Contains("UIComponent::GoToForum()", note);
            Assert.Contains("OpenPage(\"forum\", World)", note);

            using (var resolver = new DefaultAssemblyResolver())
            using (ModuleDefinition patched = Read(images[GameDll], refs, resolver))
            {
                MethodDefinition forum = Method(patched, ForumPatch.UiType, "GoToForum");
                var code = forum.Body.Instructions;
                Assert.Equal(5, code.Count);
                Assert.Equal("IL_0000: ldstr \"forum\"", code[0].ToString());
                Assert.Equal("IL_0005: ldarg.0", code[1].ToString());
                Assert.Equal("IL_0006: call Gameloft.Engine.World Gameloft.Engine.Component::get_World()", code[2].ToString());
                AssertHookCall(code[3], "OpenPage", "System.Void", "System.String", "System.Object");
                Assert.Equal("IL_0010: ret", code[4].ToString());
                Assert.Empty(forum.Body.Variables);
                Assert.Empty(forum.Body.ExceptionHandlers);
            }

            using (var game = new GameContext(refs, images, serverImage))
            using (var original = new GameContext(refs, new Dictionary<string, byte[]>(), serverImage))
            {
                Type hooks = game.Get("McvsOffline.Server").GetType(AdminPagePatch.HooksType, true);
                Assembly engine = game.Get("Gameloft.Engine"), engine0 = original.Get("Gameloft.Engine");
                Type ui = game.Get("Game").GetType(ForumPatch.UiType, true), ui0 = original.Get("Game").GetType(ForumPatch.UiType, true);
                object world = RuntimeHelpers.GetUninitializedObject(engine.GetType("Gameloft.Engine.World", true));
                Assert.Equal("returned", Outcome(ui.GetMethod("GoToForum", Any), Component(engine, ui, world)));
                Assert.Equal(1, hooks.GetField("OpenCalls").GetValue(null));
                Assert.Equal("forum", hooks.GetField("LastTrigger").GetValue(null));
                Assert.Same(world, hooks.GetField("LastWorld").GetValue(null));
                Assert.Equal("returned", Outcome(ui.GetMethod("GoToForum", Any), Component(engine, ui, null)));
                Assert.Equal(2, hooks.GetField("OpenCalls").GetValue(null));
                Assert.Null(hooks.GetField("LastWorld").GetValue(null));

                // The shipped body went to World.Browser.ShowForum() (here it fails on the bare World: proof it is not run any more).
                object world0 = RuntimeHelpers.GetUninitializedObject(engine0.GetType("Gameloft.Engine.World", true));
                string shipped = Outcome(ui0.GetMethod("GoToForum", Any), Component(engine0, ui0, world0));
                output.WriteLine("shipped GoToForum: " + shipped);
                Assert.NotEqual("returned", shipped);
            }
        }

        [Fact]
        public void EveryPatchedMethodJitsAndASecondRunIsRefused()
        {
            string refs = Refs();
            if (refs == null) return;
            Patched(refs, out byte[] serverImage, out Dictionary<string, byte[]> images);

            // With 'forum', and with every default Game.dll / Gameloft.Engine.dll patch plus 'forum' (the 'all' set of the patcher).
            byte[] hookServer = FixtureCompiler.Compile("McvsOffline.Server", RealGamePatchTests.HookServerSource);
            Dictionary<string, byte[]> all = RealGamePatchTests.PatchReal(refs, hookServer, null, RealGamePatchTests.DefaultGamePatches.Concat(new[] { new ForumPatch() }).ToArray());
            foreach (var (set, serverBytes) in new[] { (images, serverImage), (all, hookServer) })
            {
                using (var game = new GameContext(refs, set, serverBytes))
                {
                    Assembly g = game.Get("Game");
                    foreach (MethodInfo m in new[]
                    {
                        g.GetType(AdminPagePatch.ProfileType, true).GetMethod("GetAccountMenuButtonList", Any),
                        g.GetType(AdminPagePatch.ProfileType, true).GetMethod("ProcessAccountMenuButtonAction", Any),
                        g.GetType(ForumPatch.UiType, true).GetMethod("GoToForum", Any),
                    })
                    {
                        RuntimeHelpers.PrepareMethod(m.MethodHandle);   // InvalidProgramException on bad IL
                        output.WriteLine("JIT ok: " + m.DeclaringType + "." + m.Name);
                    }
                }
            }

            // Patched once: the fingerprints no longer match, for each patch.
            string dir = TestEnvironment.NewTempDir("adminpage-repatch");
            foreach (string f in Directory.GetFiles(refs, "*.dll")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            File.WriteAllBytes(Path.Combine(dir, GameDll), images[GameDll]);
            string message = Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(dir, serverImage, null, new AdminPagePatch())).Message;
            Assert.Contains("IL fingerprint", message);
            Assert.Contains("GetAccountMenuButtonList", message);
            Assert.Contains("IL fingerprint", Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(dir, serverImage, null, new ForumPatch())).Message);

            // One changed instruction in the action method ("restore_purchase" -> "restore") is refused before anything changes.
            string changed = TestEnvironment.NewTempDir("adminpage-changed");
            foreach (string f in Directory.GetFiles(refs, "*.dll")) File.Copy(f, Path.Combine(changed, Path.GetFileName(f)));
            using (var resolver = new DefaultAssemblyResolver())
            using (var module = ModuleDefinition.ReadModule(Path.Combine(refs, GameDll), new ReaderParameters { InMemory = true, AssemblyResolver = resolver }))
            {
                resolver.AddSearchDirectory(refs);
                Method(module, AdminPagePatch.ProfileType, "ProcessAccountMenuButtonAction").Body.Instructions
                      .Single(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "restore_purchase").Operand = "restore";
                module.Write(Path.Combine(changed, GameDll));
            }
            message = Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(changed, serverImage, null, new AdminPagePatch())).Message;
            Assert.Contains("ProcessAccountMenuButtonAction", message);
            Assert.Contains("IL fingerprint", message);

            // A server without the hooks, or with a hook of another signature, is refused.
            byte[] noHooks = FixtureCompiler.Compile("McvsOffline.Server", "namespace McvsOffline { public static class Bootstrap { public static void Init() { } } }");
            Assert.Contains("has no type McvsOffline.AdminHooks", Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(refs, noHooks, null, new AdminPagePatch())).Message);
            Assert.Contains("has no type McvsOffline.AdminHooks", Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(refs, noHooks, null, new ForumPatch())).Message);
            byte[] wrong = FixtureCompiler.Compile("McvsOffline.Server", HooksSource.Replace("public static void OpenPage(string trigger, object world)", "public static void OpenPage(string trigger)")
                                                                                     .Replace("LastWorld = world;", ""));
            message = Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(refs, wrong, null, new AdminPagePatch())).Message;
            Assert.Contains("OpenPage(System.String, System.Object)", message);
        }

        // ------------------------------------------------------------------ ILVerify

        /// <summary>ilverify from MCVS_ILVERIFY or ~/.dotnet/tools, or null (as RealDebloatPatchTests).</summary>
        internal static string IlVerifyTool()
        {
            string tool = Environment.GetEnvironmentVariable("MCVS_ILVERIFY");
            if (string.IsNullOrEmpty(tool)) tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "ilverify");
            return File.Exists(tool) ? tool : null;
        }

        /// <summary>Runs ilverify on <paramref name="file"/> with <paramref name="references"/>; the exit code and the output.</summary>
        internal static (int Exit, string Text) IlVerify(string tool, string file, IEnumerable<string> references, IEnumerable<string> arguments)
        {
            var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(file);
            foreach (string r in references)
            {
                psi.ArgumentList.Add("-r");
                psi.ArgumentList.Add(r);
            }
            foreach (string a in arguments) psi.ArgumentList.Add(a);
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

        /// <summary>ILVerify's findings ("[IL]: Error ..." lines), sorted.</summary>
        internal static List<string> Findings(string text)
        {
            return text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("[IL]") || l.StartsWith("[MD]")).OrderBy(l => l, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// RealGamePatchTests.HookServerSource compiled against the game's own mscorlib (MCVS_REFS) as the embedded build is,
        /// not against net8: the hook signatures then name the same System.String / System.Object as the game's assemblies,
        /// which ILVerify needs to match the patched calls with their targets.
        /// </summary>
        internal static byte[] GameBclServer(string refs)
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(RealGamePatchTests.HookServerSource, new CSharpParseOptions(LanguageVersion.CSharp7_3));
            CSharpCompilation compilation = CSharpCompilation.Create("McvsOffline.Server", new[] { tree },
                new[] { MetadataReference.CreateFromFile(Path.Combine(refs, "mscorlib.dll")) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            using (var stream = new MemoryStream())
            {
                var result = compilation.Emit(stream);
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
                return stream.ToArray();
            }
        }

        /// <summary>
        /// ILVerify on <paramref name="assembly"/> patched with <paramref name="patches"/> (server: <see cref="GameBclServer"/>): the
        /// methods matched by <paramref name="includes"/> (exactly <paramref name="methods"/> of them) have no finding before and
        /// after patching, and the whole assembly has exactly the shipped findings afterwards.
        /// </summary>
        internal static void AssertIlVerifyClean(ITestOutputHelper output, string refs, string assembly, IlPatch[] patches, string[] includes, int methods,
                                                 params string[] patchedMethods)
        {
            string tool = IlVerifyTool();
            if (tool == null)
            {
                output.WriteLine("ilverify not found (MCVS_ILVERIFY or ~/.dotnet/tools/ilverify): skipped");
                return;
            }
            byte[] serverImage = GameBclServer(refs);
            Dictionary<string, byte[]> images = RealGamePatchTests.PatchReal(refs, serverImage, null, patches);
            string dir = TestEnvironment.NewTempDir("admin-ilverify");
            string patchedFile = Path.Combine(dir, assembly), serverFile = Path.Combine(dir, "McvsOffline.Server.dll");
            File.WriteAllBytes(patchedFile, images[assembly]);
            File.WriteAllBytes(serverFile, serverImage);
            string shipped = Path.Combine(refs, assembly);
            var references = Directory.GetFiles(refs, "*.dll").Where(f => Path.GetFileName(f) != assembly).Concat(new[] { serverFile }).ToList();

            var arguments = includes.SelectMany(i => new[] { "-i", i }).Concat(new[] { "--statistics" }).ToList();
            foreach (string file in new[] { shipped, patchedFile })
            {
                var (exit, text) = IlVerify(tool, file, references, arguments);
                output.WriteLine((file == patchedFile ? "patched: " : "original: ") + text.Trim());
                Assert.True(exit == 0, "ilverify exit code " + exit + ":\n" + text);
                Assert.Contains("Methods verified: " + methods, text);
            }

            List<string> before = Findings(IlVerify(tool, shipped, references, new string[0]).Text);
            List<string> after = Findings(IlVerify(tool, patchedFile, references, new string[0]).Text);
            output.WriteLine("whole assembly: " + before.Count + " shipped findings, " + after.Count + " after patching");
            Assert.Equal(before, after);
            Assert.DoesNotContain(after, l => patchedMethods.Any(m => l.Contains(m + "(")));
        }

        [Fact]
        public void IlVerifyAcceptsThePatchedMethods()
        {
            string refs = Refs();
            if (refs == null) return;
            AssertIlVerifyClean(output, refs, GameDll, new IlPatch[] { new AdminPagePatch(), new ForumPatch() },
                                new[] { @"\]Game\.PlayerProfileComponent\.(GetAccountMenuButtonList|ProcessAccountMenuButtonAction)$", @"\]Game\.UIComponent\.GoToForum$" }, 3,
                                "PlayerProfileComponent::GetAccountMenuButtonList", "PlayerProfileComponent::ProcessAccountMenuButtonAction", "UIComponent::GoToForum");
        }

        // ------------------------------------------------------------------ the embedded server for full patcher runs

        /// <summary>
        /// The embedded server DLL of scripts/build.sh for full patcher runs with the default patches, or null (skipped) when it
        /// was not built. The default 'adminpage' and 'adminlink' call McvsOffline.AdminHooks (hosts/embedded/Admin/AdminHooks.cs):
        /// a build without it cannot be patched with the default options, which fails the run under MCVS_REQUIRE_EMBEDDED=1
        /// (skipped otherwise).
        /// </summary>
        internal static string EmbeddedServer(ITestOutputHelper output)
        {
            return EmbeddedServer(TestEnvironment.EmbeddedServerDll, Environment.GetEnvironmentVariable("MCVS_REQUIRE_EMBEDDED") == "1", output);
        }

        /// <summary><see cref="EmbeddedServer(ITestOutputHelper)"/> for <paramref name="dll"/>.</summary>
        internal static string EmbeddedServer(string dll, bool required, ITestOutputHelper output)
        {
            if (dll == null) return null;
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(dll))))
            {
                if (module.GetType(AdminPagePatch.HooksType) != null) return dll;
            }
            string missing = "the embedded build " + dll + " has no " + AdminPagePatch.HooksType + ": the default patches cannot be applied";
            if (required) throw new InvalidOperationException("MCVS_REQUIRE_EMBEDDED=1 but " + missing);
            output?.WriteLine(missing + ": skipped");
            return null;
        }

        [Fact]
        public void AnEmbeddedBuildWithoutTheHooksIsRefused()
        {
            string bare = Path.Combine(TestEnvironment.NewTempDir("embedded-nohooks"), "McvsOffline.Server.dll");
            File.WriteAllBytes(bare, FixtureCompiler.Compile("McvsOffline.Server", "namespace McvsOffline { public static class Bootstrap { public static void Init() { } } }"));
            string message = Assert.Throws<InvalidOperationException>(() => EmbeddedServer(bare, true, output)).Message;
            Assert.Contains("MCVS_REQUIRE_EMBEDDED=1 but the embedded build", message);
            Assert.Contains("has no McvsOffline.AdminHooks", message);
            Assert.Null(EmbeddedServer(bare, false, output));

            // A build with the hooks is used as built.
            string built = Path.Combine(TestEnvironment.NewTempDir("embedded-hooks"), "McvsOffline.Server.dll");
            File.WriteAllBytes(built, FixtureCompiler.Compile("McvsOffline.Server", HooksSource));
            Assert.Equal(built, EmbeddedServer(built, true, output));
        }
    }
}
