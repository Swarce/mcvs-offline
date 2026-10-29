using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// The re-fingerprinted Engine/Game patches executed on the real PC assemblies (docs/design/pc-port.md §8.6, §8.7, §12.4): twins
    /// of the Android tests for <c>nativeonline</c>, <c>adminlink</c>, <c>adminpage</c> and <c>graphics</c>, with the net452
    /// stand-in server (<see cref="PcHookServer"/>). Where the patched code falls through to the shipped body, the outcome is
    /// compared with the unpatched method on the same uninitialised object (differential). Skipped without MCVS_PC_REFS.
    /// </summary>
    public class RealPcGameHooksTests
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const string Marker = "http://127.0.0.1:51780/_mcvs/ui/#news";
        private static readonly BuildProfile Pc = BuildProfile.PcSteam6372049;

        private readonly ITestOutputHelper output;

        public RealPcGameHooksTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static string Outcome(MethodInfo method, object target, params object[] arguments)
        {
            try
            {
                method.Invoke(target, arguments);
                return "returned";
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException.GetType().FullName;
            }
        }

        private static List<string> Calls(RealGamePatchTests.GameContext game)
        {
            return (List<string>)game.Get("McvsOffline.Server").GetType("McvsOffline.HookLog", true).GetField("Calls").GetValue(null);
        }

        private static object[] LastArguments(RealGamePatchTests.GameContext game)
        {
            return ((List<object[]>)game.Get("McvsOffline.Server").GetType("McvsOffline.HookLog", true).GetField("Arguments").GetValue(null)).Last();
        }

        [Fact]
        public void NativeOnlineInitRunsOnlyWhenTheHookAllowsIt()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, server, null, Pc, new NativeOnlinePatch());
            using (var game = new RealGamePatchTests.GameContext(pc, patched, server))
            {
                Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.GameHooks", true);
                MethodInfo init = game.Get("Gameloft.Engine").GetType(NativeOnlinePatch.TypeName, true).GetMethod("Init_private", All);
                Assert.Equal("returned", Outcome(init, null, "mcvs", "", "{}"));   // denied: libnativeengine is never touched
                Assert.Equal(new[] { "GameHooks.AllowNativeOnlineInit" }, Calls(game));
                Assert.Equal("mcvs", LastArguments(game)[0]);
                hooks.GetField("AllowNativeOnline").SetValue(null, true);
                string allowed = Outcome(init, null, "mcvs", "", "{}");   // allowed: the UTF-8 marshalling, then the P/Invoke
                output.WriteLine("allowed: " + allowed);
                Assert.True(allowed == typeof(DllNotFoundException).FullName || allowed == typeof(EntryPointNotFoundException).FullName, allowed);
                Assert.Equal(2, Calls(game).Count);
            }
            using (var bare = new RealGamePatchTests.GameContext(pc, patched, null))
            {
                // No McvsOffline.Server.dll: the helper fails inside the try and the native call is skipped.
                MethodInfo init = bare.Get("Gameloft.Engine").GetType(NativeOnlinePatch.TypeName, true).GetMethod("Init_private", All);
                Assert.Equal("returned", Outcome(init, null, "mcvs", "", "{}"));
            }
        }

        [Fact]
        public void TheMarkerOpensThePageAndEveryOtherUrlTakesTheShippedPath()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, server, null, Pc, new AdminLinkPatch());
            using (var game = new RealGamePatchTests.GameContext(pc, patched, server))
            using (var original = new RealGamePatchTests.GameContext(pc, new Dictionary<string, byte[]>(), server))
            {
                Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.AdminHooks", true);
                Type browser = game.Get("Gameloft.Engine").GetType(AdminLinkPatch.BrowserType, true);
                Type browser0 = original.Get("Gameloft.Engine").GetType(AdminLinkPatch.BrowserType, true);
                MethodInfo open = browser.GetMethod("OpenUrl", All), open0 = browser0.GetMethod("OpenUrl", All);
                object component = RuntimeHelpers.GetUninitializedObject(browser), component0 = RuntimeHelpers.GetUninitializedObject(browser0);

                hooks.GetField("OpenIfPageMarkerAnswer").SetValue(null, true);   // the hook took it: nothing of the shipped body runs
                Assert.Equal("returned", Outcome(open, component, Marker, true));
                Assert.Equal(new[] { "AdminHooks.OpenIfPageMarker" }, Calls(game));
                Assert.Equal(Marker, LastArguments(game)[0]);
                Assert.Same(component, LastArguments(game)[1]);
                string shipped = Outcome(open0, component0, Marker, true);
                output.WriteLine("shipped, marker: " + shipped);
                Assert.NotEqual("returned", shipped);   // the shipped body fails on an uninitialised component: proof it would have run

                hooks.GetField("OpenIfPageMarkerAnswer").SetValue(null, false);   // every other URL: the shipped outcome
                foreach (string url in new[] { "https://www.example.com/page", "https://www.youtube.com/watch?v=RXclfM3i-Rw", "", null })
                {
                    foreach (bool ingame in new[] { true, false })
                    {
                        Assert.Equal(Outcome(open0, RuntimeHelpers.GetUninitializedObject(browser0), url, ingame),
                                     Outcome(open, RuntimeHelpers.GetUninitializedObject(browser), url, ingame));
                    }
                }
                Assert.Equal(1 + 8, Calls(game).Count);
            }
        }

        [Fact]
        public void TheAccountActionOpensThePageAndEveryOtherActionRunsTheShippedStub()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, server, null, Pc, new AdminPagePatch());
            using (var game = new RealGamePatchTests.GameContext(pc, patched, server))
            using (var original = new RealGamePatchTests.GameContext(pc, new Dictionary<string, byte[]>(), server))
            {
                Type profile = game.Get("Game").GetType(AdminPagePatch.ProfileType, true), profile0 = original.Get("Game").GetType(AdminPagePatch.ProfileType, true);
                MethodInfo action = profile.GetMethod("ProcessAccountMenuButtonAction", All), action0 = profile0.GetMethod("ProcessAccountMenuButtonAction", All);
                object world;
                object component = Component(game, profile, out world), component0 = Component(original, profile0, out _);

                Assert.Equal("returned", Outcome(action, component, AdminPagePatch.ButtonAction));
                Assert.Equal(new[] { "AdminHooks.OpenPage" }, Calls(game));
                Assert.Equal(AdminPagePatch.Trigger, LastArguments(game)[0]);
                Assert.Same(world, LastArguments(game)[1]);
                Assert.NotEqual("returned", Outcome(action0, component0, AdminPagePatch.ButtonAction));   // the shipped stub (World.Social) fails here

                foreach (string other in new[] { "restore_purchase", "x", "", null, "MCVS_OFFLINE", "mcvs_offline ", "showRedeem" })
                    Assert.Equal(Outcome(action0, Component(original, profile0, out _), other), Outcome(action, Component(game, profile, out _), other));
                Assert.Single(Calls(game));
            }
        }

        /// <summary>An uninitialised PlayerProfileComponent on a GameObject in an uninitialised World.</summary>
        private static object Component(RealGamePatchTests.GameContext game, Type type, out object world)
        {
            Assembly engine = game.Get("Gameloft.Engine");
            Type gameObject = engine.GetType("Gameloft.Engine.GameObject", true);
            world = RuntimeHelpers.GetUninitializedObject(engine.GetType("Gameloft.Engine.World", true));
            object go = RuntimeHelpers.GetUninitializedObject(gameObject);
            gameObject.GetField("world", All).SetValue(go, world);
            object component = RuntimeHelpers.GetUninitializedObject(type);
            engine.GetType("Gameloft.Engine.Component", true).GetFields(All).Single(f => f.FieldType == gameObject).SetValue(component, go);
            return component;
        }

        [Fact]
        public void TheGraphicsHelpersForwardTheComponentItselfAndFallBackToTheTextAsRead()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, server, null, Pc, new GraphicsPatch());
            using (var game = new RealGamePatchTests.GameContext(pc, patched, server))
            {
                Assembly engine = game.Get("Gameloft.Engine");
                Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.GraphicsHooks", true);
                Type helper = engine.GetType("Gameloft.Engine." + GraphicsPatch.HelperType, true);
                foreach (MethodInfo m in helper.GetMethods(All | BindingFlags.DeclaredOnly)) RuntimeHelpers.PrepareMethod(m.MethodHandle);
                MethodInfo json = helper.GetMethod("GameOptionsJson", All), applied = helper.GetMethod("OptionsApplied", All);

                hooks.GetField("GameOptionsJsonAnswer").SetValue(null, "{\"rewritten\":1}");
                Assert.Equal("{\"rewritten\":1}", json.Invoke(null, new object[] { "{\"original\":1}" }));
                Assert.Equal("{\"original\":1}", LastArguments(game)[0]);
                hooks.GetField("GameOptionsJsonAnswer").SetValue(null, null);
                Assert.Equal("{\"original\":1}", json.Invoke(null, new object[] { "{\"original\":1}" }));   // null answer: the text as read

                Type componentType = engine.GetType(GraphicsPatch.TypeName, true);
                Assert.Null(componentType.GetField("m_options", All));   // PC: no options field, the component itself goes to the host
                object component = RuntimeHelpers.GetUninitializedObject(componentType);
                applied.Invoke(null, new[] { component });
                Assert.Equal("GraphicsHooks.GameOptionsApplied", Calls(game).Last());
                Assert.Same(component, LastArguments(game)[0]);
                applied.Invoke(null, new object[] { null });
                Assert.Null(LastArguments(game)[0]);
            }

            // Without McvsOffline.Server.dll: the forwarders fail inside the helpers' try, the text goes through unchanged.
            using (var bare = new RealGamePatchTests.GameContext(pc, patched, null))
            {
                Type helper = bare.Get("Gameloft.Engine").GetType("Gameloft.Engine." + GraphicsPatch.HelperType, true);
                Assert.Equal("{\"original\":1}", helper.GetMethod("GameOptionsJson", All).Invoke(null, new object[] { "{\"original\":1}" }));
                helper.GetMethod("OptionsApplied", All).Invoke(null, new object[] { null });
            }
        }
    }
}
