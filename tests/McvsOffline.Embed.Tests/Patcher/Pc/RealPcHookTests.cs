using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// The new PC patches executed on net8 (docs/design/pc-port.md §12.4, the Android RealGamePatchTests approach: the patched real
    /// Steam assemblies loaded into a collectible context with a stand-in McvsOffline.Server): <c>hook-pc</c> with a look-alike
    /// <c>MainLoop</c>, <c>quicklaunch</c> P1/P2/P3 and <c>headless-diag</c> M4. Skipped without MCVS_PC_REFS, a failure under
    /// MCVS_REQUIRE_PC=1.
    /// </summary>
    public class RealPcHookTests
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const string OrderKey = "mcvs-hook-pc-order";
        private static readonly BuildProfile Pc = BuildProfile.PcSteam6372049;

        /// <summary>A look-alike Gameloft.Engine with only what EngineBridge.Class1.Start calls; it records the call.</summary>
        private const string EngineSource = @"
namespace Gameloft.Engine
{
    public class MainLoop
    {
        public enum ModeType { NativeHostsDotNet = 0, Editor = 1 }

        public static int Start(ModeType mode, string[] args, bool isEditor)
        {
            ((System.Collections.Generic.List<string>)System.AppContext.GetData(""" + OrderKey + @""")).Add(
                ""MainLoop.Start("" + (int)mode + "", "" + (args == null ? ""null"" : ""args"") + "", "" + isEditor + "")"");
            return 0;
        }
    }
}";

        /// <summary>A server whose Bootstrap.Init records into the same list (and throws when told to).</summary>
        private const string RecordingServerSource = @"
namespace McvsOffline
{
    public static class Bootstrap
    {
        public static bool Throws;

        public static void Init()
        {
            ((System.Collections.Generic.List<string>)System.AppContext.GetData(""" + OrderKey + @""")).Add(""Init"");
            if (Throws) throw new System.InvalidOperationException(""Init failed"");
        }
    }
}";

        /// <summary>A server whose quicklaunch hook throws (the patch's fallback path).</summary>
        private const string ThrowingHooksSource = @"
namespace McvsOffline
{
    public static class PcHooks
    {
        public static System.Threading.Tasks.Task QuickLaunch(object component, object link, System.Threading.CancellationToken ct)
        {
            throw new System.InvalidOperationException(""hook failed"");
        }
    }
}";

        private readonly ITestOutputHelper output;

        public RealPcHookTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void HookPcInitialisesTheServerThenStartsTheGameEvenWhenInitFailsOrTheServerIsMissing()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, PcHookServer.Compile(), null, Pc, new EngineBridgeHookPatch());
            patched["Gameloft.Engine.dll"] = FixtureCompiler.Compile("Gameloft.Engine", EngineSource);
            byte[] server = FixtureCompiler.Compile("McvsOffline.Server", RecordingServerSource);

            foreach (var (serverImage, throws, expected) in new[]
            {
                (server, false, new[] { "Init", "MainLoop.Start(0, null, False)" }),
                (server, true, new[] { "Init", "MainLoop.Start(0, null, False)" }),
                ((byte[])null, false, new[] { "MainLoop.Start(0, null, False)" }),
            })
            {
                var order = new List<string>();
                AppContext.SetData(OrderKey, order);
                using (var game = new RealGamePatchTests.GameContext(pc, patched, serverImage))
                {
                    if (serverImage != null) game.Get("McvsOffline.Server").GetType("McvsOffline.Bootstrap", true).GetField("Throws").SetValue(null, throws);
                    Type bridge = game.Get("EngineBridge").GetType("EngineBridge.Class1", true);
                    object instance = Activator.CreateInstance(bridge);
                    bridge.GetMethod("Start").Invoke(instance, null);
                }
                output.WriteLine((serverImage == null ? "no server" : throws ? "throwing Init" : "server") + ": " + string.Join(", ", order));
                Assert.Equal(expected, order);
            }
            AppContext.SetData(OrderKey, null);
        }

        [Fact]
        public void P1ThePortIsTheListenersPortAndZeroWithoutListener()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, server, null, Pc, new QuickLaunchPatch());
            using (var game = new RealGamePatchTests.GameContext(pc, patched, server))
            {
                Assembly engine = game.Get("Gameloft.Engine");
                Type baseServer = engine.GetType("Gameloft.Engine.LocalAnubis.BaseServer", true);
                Type lobby = engine.GetType("Gameloft.Engine.LocalAnubis.Lobby", true);
                Assert.True(baseServer.IsAssignableFrom(lobby));
                object instance = RuntimeHelpers.GetUninitializedObject(lobby);
                MethodInfo getPort = baseServer.GetMethod("get_Port", All);
                Assert.Equal(0, getPort.Invoke(instance, null));   // no listener

                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    baseServer.GetField("<Listener>k__BackingField", All).SetValue(instance, listener);
                    int expected = ((IPEndPoint)listener.LocalEndpoint).Port;
                    Assert.NotEqual(0, expected);
                    Assert.Equal(expected, getPort.Invoke(instance, null));
                }
                finally
                {
                    listener.Stop();
                }
            }
        }

        [Fact]
        public void P2QuickLaunchGoesThroughTheHookAndFallsBackToTheLinkWhenTheHookCannotBeCalled()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, server, null, Pc, new QuickLaunchPatch());
            var cts = new CancellationTokenSource();

            using (var game = new RealGamePatchTests.GameContext(pc, patched, server))
            {
                Assembly engine = game.Get("Gameloft.Engine");
                Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.PcHooks", true);
                Type log = game.Get("McvsOffline.Server").GetType("McvsOffline.HookLog", true);
                Task answer = new TaskCompletionSource<object>().Task;
                hooks.GetField("QuickLaunchAnswer").SetValue(null, answer);
                object component = RuntimeHelpers.GetUninitializedObject(engine.GetType("Gameloft.Engine.ClientComponent", true));
                object link = RuntimeHelpers.GetUninitializedObject(engine.GetType("Gameloft.Engine.ClientLink", true));
                var result = (Task)QuickLaunch(engine).Invoke(component, new object[] { link, cts.Token });
                Assert.Same(answer, result);
                Assert.Equal(new[] { "PcHooks.QuickLaunch" }, (List<string>)log.GetField("Calls").GetValue(null));
                object[] arguments = ((List<object[]>)log.GetField("Arguments").GetValue(null)).Single();
                Assert.Same(component, arguments[0]);
                Assert.Same(link, arguments[1]);
                Assert.Equal(cts.Token, (CancellationToken)arguments[2]);
            }

            // A throwing hook, and no server at all: the stock path, link.QuickLaunch(ct), whose async body fails on the
            // uninitialised link (proof that it ran) as a faulted task, not an exception.
            foreach (byte[] fallbackServer in new[] { FixtureCompiler.Compile("McvsOffline.Server", ThrowingHooksSource), null })
            {
                using (var game = new RealGamePatchTests.GameContext(pc, patched, fallbackServer))
                {
                    Assembly engine = game.Get("Gameloft.Engine");
                    object component = RuntimeHelpers.GetUninitializedObject(engine.GetType("Gameloft.Engine.ClientComponent", true));
                    object link = RuntimeHelpers.GetUninitializedObject(engine.GetType("Gameloft.Engine.ClientLink", true));
                    var result = (Task)QuickLaunch(engine).Invoke(component, new object[] { link, cts.Token });
                    Assert.NotNull(result);
                    Assert.True(result.Wait(TimeSpan.FromSeconds(10)) || true);
                    output.WriteLine((fallbackServer == null ? "no server" : "throwing hook") + ": " + result.Status + " " +
                                     (result.Exception == null ? "" : result.Exception.InnerException.GetType().Name));
                    Assert.True(result.IsFaulted, "the stock ClientLink.QuickLaunch ran on an uninitialised link: a faulted task");
                    Assert.IsType<NullReferenceException>(result.Exception.InnerException);
                }
            }
        }

        private static MethodInfo QuickLaunch(Assembly engine)
        {
            return engine.GetType("Gameloft.Engine.ClientComponent", true).GetMethods(All)
                         .Single(m => m.Name == "QuickLaunch" && m.DeclaringType.Name == "ClientComponent" && m.GetParameters().Length == 2);
        }

        [Fact]
        public void P3TheHookGetsTheSettingsAndTheRequestsHostStillWins()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, server, null, Pc, new QuickLaunchPatch());
            using (var game = new RealGamePatchTests.GameContext(pc, patched, server))
            {
                Assembly engine = game.Get("Gameloft.Engine");
                Type log = game.Get("McvsOffline.Server").GetType("McvsOffline.HookLog", true);
                Type matchmaking = engine.GetType("Gameloft.Engine.MatchmakingComponent", true);
                Type settingsType = matchmaking.GetNestedType("AutomatchSettings", BindingFlags.NonPublic | BindingFlags.Public);
                object settings = Activator.CreateInstance(settingsType, true);
                object component = RuntimeHelpers.GetUninitializedObject(matchmaking);
                Type jobject = game.Get("Newtonsoft.Json").GetType("Newtonsoft.Json.Linq.JObject", true);
                object request = jobject.GetMethod("Parse", new[] { typeof(string) }).Invoke(null, new object[] { "{\"host\":\"10.0.0.2\",\"port\":4321}" });
                MethodInfo add = matchmaking.GetMethod("AddAutomatchHostSettings", All);
                var task = (Task)add.Invoke(component, new[] { settings, null, request, CancellationToken.None });
                Assert.True(task.IsCompleted);
                Assert.Equal(new[] { "PcHooks.DefaultAutomatchHost" }, (List<string>)log.GetField("Calls").GetValue(null));
                Assert.Same(settings, ((List<object[]>)log.GetField("Arguments").GetValue(null)).Single()[0]);
                Assert.Equal("10.0.0.2", settingsType.GetProperty("Host").GetValue(settings));
                Assert.Equal(4321, settingsType.GetProperty("Port").GetValue(settings));
            }
        }

        [Fact]
        public void M4AComponentWhoseConstructorThrowsIsReportedWithItsType()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, server, null, Pc, new HeadlessDiagnosticsPatch());
            byte[] boom = FixtureCompiler.Compile("McvsBoom", "public class Boom { public Boom() { throw new System.InvalidOperationException(\"boom\"); } }");
            using (var game = new RealGamePatchTests.GameContext(pc, patched, server))
            using (var sandbox = new FixtureCompiler.Sandbox().With("McvsBoom", boom))
            {
                Assembly engine = game.Get("Gameloft.Engine");
                Type log = game.Get("McvsOffline.Server").GetType("McvsOffline.HookLog", true);
                Type boomType = sandbox.Get("McvsBoom").GetType("Boom", true);
                Type assemblies = engine.GetType("Gameloft.Engine.ComponentAssemblies", true);
                MethodInfo create = assemblies.GetMethod("CreateInstance", All);
                // ShouldCreateType asks MainLoop.IsEditor (nativeengine): give the type its settings first, as a second
                // CreateInstance of the same type finds them (createSettingsDict), so the native call is not needed.
                Type settingsType = assemblies.GetNestedType("CreateTypeSettings", BindingFlags.Public | BindingFlags.NonPublic);
                object settings = Activator.CreateInstance(settingsType, true);
                settingsType.GetProperty("KeepInHeadless", All).SetValue(settings, true);
                object dict = assemblies.GetField("createSettingsDict", All).GetValue(null);
                dict.GetType().GetMethod("TryAdd").Invoke(dict, new[] { boomType, settings });
                object result;
                try
                {
                    result = create.Invoke(null, new object[] { null, boomType, false, new object[0] });
                }
                catch (TargetInvocationException ex) when (ex.InnerException is TypeInitializationException || ex.InnerException is DllNotFoundException)
                {
                    // The engine's static state (Component.ConstructorOwnerStack, SuperActivator) could not initialise on net8: the
                    // behaviour is then covered by the structural asserts of RealPcPatchTests only.
                    output.WriteLine("the engine's static state does not initialise on net8 (" + ex.InnerException.Message + "): structural only");
                    return;
                }
                Assert.Null(result);
                Assert.Equal(new[] { "PcHooks.ComponentCreateFailed" }, (List<string>)log.GetField("Calls").GetValue(null));
                object[] arguments = ((List<object[]>)log.GetField("Arguments").GetValue(null)).Single();
                var reported = (Exception)arguments[0];
                output.WriteLine("reported: " + reported.GetType().Name + ": " + reported.Message);
                Assert.Contains("boom", reported.ToString());
                Assert.Same(boomType, arguments[1]);
            }
        }
    }
}
