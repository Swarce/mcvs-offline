using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Admin;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// Every game member the PC host reaches by reflection exists with the expected shape in the player's real PC
    /// Gameloft.Engine.dll, Gameloft.OnlinePlatform.dll and Game.dll (MCVS_PC_REFS, docs/design/pc-port.md §8.3, §6.6, §6.7, [CR] F4,
    /// F6), found by the same lookups: PcHooks' quick launch and automatch members, GraphicsHooks' GameOptionManager reads,
    /// WindowsExit's MenuManager.ExitAppAndroid, the watchdog's GameApplication members, and GameReflection.Verify (the settings
    /// page's members). Skipped without the files unless MCVS_REQUIRE_PC=1.
    /// </summary>
    public class PcHooksReflectionTests
    {
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;

        private readonly ITestOutputHelper output;

        public PcHooksReflectionTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private string Refs()
        {
            string dir = TestEnvironment.PcRefs;
            if (dir != null && File.Exists(Path.Combine(dir, "Gameloft.OnlinePlatform.dll")) && File.Exists(Path.Combine(dir, "Game.dll"))) return dir;
            Assert.False(TestEnvironment.RequirePc, "MCVS_REQUIRE_PC=1 but MCVS_PC_REFS lacks the PC assemblies");
            output.WriteLine("the PC assemblies are not available (MCVS_PC_REFS): skipped");
            return null;
        }

        private sealed class Problems
        {
            public readonly List<string> List = new List<string>();

            public T Need<T>(T value, string what) where T : class
            {
                if (value == null) List.Add("missing: " + what);
                return value;
            }

            public void Check(bool ok, string what)
            {
                if (!ok) List.Add("wrong: " + what);
            }
        }

        private static PropertyInfo Prop(Problems p, Type type, string name, BindingFlags flags, Type expected = null)
        {
            if (type == null) return null;
            PropertyInfo property = p.Need(type.GetProperty(name, flags), type.FullName + "." + name);
            if (property != null && expected != null) p.Check(property.PropertyType == expected, type.FullName + "." + name + " is " + property.PropertyType);
            return property;
        }

        private static MethodInfo Method(Problems p, Type type, string name, BindingFlags flags, Type[] parameters, Type returns = null)
        {
            if (type == null) return null;
            MethodInfo method = p.Need(type.GetMethod(name, flags, null, parameters, null),
                                       type.FullName + "." + name + "(" + string.Join(", ", parameters.Select(t => t.Name)) + ")");
            if (method != null && returns != null) p.Check(method.ReturnType == returns, type.FullName + "." + name + " returns " + method.ReturnType);
            return method;
        }

        [Fact]
        public void EveryMemberThePcHostUsesResolvesInTheRealAssemblies()
        {
            string dir = Refs();
            if (dir == null) return;
            var context = new PcContext(dir);
            try
            {
                Assembly engine = context.LoadFromAssemblyPath(Path.Combine(dir, "Gameloft.Engine.dll"));
                Assembly online = context.LoadFromAssemblyPath(Path.Combine(dir, "Gameloft.OnlinePlatform.dll"));
                Assembly game = context.LoadFromAssemblyPath(Path.Combine(dir, "Game.dll"));
                var p = new Problems();
                Func<Assembly, string, Type> type = (a, name) => p.Need(a.GetType(name, false), name);

                // P2: ClientComponent.QuickLaunch(ClientLink, CancellationToken) → PcHooks.QuickLaunch.
                Type link = type(engine, PcHooks.EngineLinkType);
                Type settings = Prop(p, link, "Settings", PublicInstance)?.PropertyType;
                PropertyInfo location = Prop(p, settings, "ServerLocation", PublicInstance);
                p.Check(location != null && location.PropertyType.IsEnum && Enum.GetNames(location.PropertyType).Contains(PcHooks.EmbeddedLocation),
                        "ClientSettings.ServerLocation has Embedded");
                Prop(p, settings, "ServerType", PublicInstance, typeof(string));
                Type room = type(online, "Gameloft.OnlinePlatform.Room");
                PropertyInfo linkRoom = Prop(p, link, "Room", PublicInstance, room);
                p.Check(linkRoom != null && linkRoom.CanWrite, "ClientLink.Room has a setter");
                Method(p, link, "QuickLaunch", PublicInstance, new[] { typeof(CancellationToken) }, typeof(Task));
                Type component = type(engine, "Gameloft.Engine.ClientComponent");
                Type session = type(online, PcHooks.SessionType);
                Type authentication = Prop(p, component, "Authentication", PublicInstance)?.PropertyType;
                Prop(p, authentication, "Session", PublicInstance, session);
                Type manager = type(engine, PcHooks.ManagerType);
                p.Check(link == null || link.Assembly.GetType(PcHooks.ManagerType, false) == manager, "LocalAnubisManager is in ClientLink's assembly");
                Prop(p, manager, "Started", PublicStatic, typeof(bool));
                Method(p, manager, "Start", PublicStatic, new[] { typeof(CancellationToken) }, typeof(Task));
                Type finder = Prop(p, manager, "Finder", PublicStatic)?.PropertyType;
                Prop(p, finder, "PublicHost", PublicInstance, typeof(string));
                Prop(p, finder, "PublicPort", PublicInstance, typeof(int));
                Type lobby = Prop(p, manager, "Lobby", PublicStatic)?.PropertyType;
                Prop(p, lobby, "Port", PublicInstance, typeof(int));
                Prop(p, Prop(p, lobby, "Controller", PublicInstance)?.PropertyType, "Port", PublicInstance, typeof(int));
                MethodInfo getService = session?.GetMethod("GetService", PublicInstance, null, Type.EmptyTypes, null);
                p.Check(getService != null && getService.IsGenericMethodDefinition, "Session.GetService<T>()");
                Type matchmaker = type(online, PcHooks.MatchmakerType);
                if (getService != null && matchmaker != null) p.Need(getService.MakeGenericMethod(matchmaker), "GetService<MatchmakerService>");
                Method(p, matchmaker, "QuickLaunch", PublicInstance, new[] { typeof(string), typeof(CancellationToken), typeof(string), typeof(int?) },
                       room == null ? null : typeof(Task<>).MakeGenericType(room));
                p.Check(session == null || session.Assembly.GetType(PcHooks.MatchmakerType, false) == matchmaker, "MatchmakerService is in Session's assembly");
                Prop(p, room, "Id", PublicInstance, typeof(string));
                Prop(p, room, "ControllerHost", PublicInstance, typeof(string));
                Prop(p, room, "ControllerPort", PublicInstance, typeof(int));

                // P3: MatchmakingComponent.AutomatchSettings (protected nested).
                Type automatch = type(engine, "Gameloft.Engine.MatchmakingComponent+AutomatchSettings");
                PropertyInfo host = Prop(p, automatch, "Host", PublicInstance, typeof(string));
                PropertyInfo port = Prop(p, automatch, "Port", PublicInstance, typeof(int?));
                p.Check(host != null && host.CanWrite && port != null && port.CanWrite, "AutomatchSettings.Host/Port have setters");

                // GraphicsHooks on PC (§6.7): the resolved options and the native profile names.
                Type options = type(engine, "Gameloft.Engine.GameOptionManager");
                Type marshal = type(engine, "Gameloft.Engine.MoreMarshal");
                foreach (string kind in new[] { "Int", "Float", "Boolean", "String" })
                {
                    Method(p, options, "Get" + kind + "OptionsCount", PublicStatic, Type.EmptyTypes, typeof(int));
                    Method(p, options, "Get" + kind + "OptionName", PublicStatic, new[] { typeof(int) }, typeof(IntPtr));
                }
                Method(p, options, "GetOptionInt", PublicStatic, new[] { typeof(string) }, typeof(int));
                Method(p, options, "GetOptionFloat", PublicStatic, new[] { typeof(string) }, typeof(float));
                Method(p, options, "GetOptionBoolean", PublicStatic, new[] { typeof(string) }, typeof(bool));
                Method(p, options, "GetOptionString", PublicStatic, new[] { typeof(string) }, typeof(IntPtr));
                Method(p, options, "GetLoadedProfileName", PublicStatic, new[] { typeof(string) }, typeof(IntPtr));
                Method(p, options, "GetOptimalProfileName", PublicStatic, new[] { typeof(string) }, typeof(IntPtr));
                foreach (string device in new[] { "DeviceTotalMemory", "DeviceGraphicsFeatureLevel", "DeviceCPUNumCores" }) Prop(p, options, device, PublicStatic, typeof(int));
                foreach (string device in new[] { "DeviceGPU", "DeviceModel" }) Prop(p, options, device, PublicStatic, typeof(string));
                Method(p, marshal, "NativeStringToString", PublicStatic, new[] { typeof(IntPtr) }, typeof(string));
                Method(p, marshal, "DestroyNativeString", PublicStatic, new[] { typeof(IntPtr) });
                p.Check(engine.GetType("Gameloft.Engine.GameOptionPerformanceProfileComponent", false) != null, "GameOptionPerformanceProfileComponent (the component the hook gets)");

                // GraphicsHooks.ReadInGamePreferences (WR2 F7): the PC options menu's LocalStorage preferences.
                p.Check(McvsOffline.GraphicsHooks.PreferencesTypeName == "Game.GamePreferencesComponent, Game", "PreferencesTypeName names Game.dll");
                Type preferences = type(game, "Game.GamePreferencesComponent");
                FieldInfo instance = preferences?.GetField("instance", PublicStatic);
                p.Check(instance != null && instance.FieldType == preferences, "GamePreferencesComponent.instance");
                p.Check(preferences?.GetField("initPerformance", BindingFlags.NonPublic | BindingFlags.Instance)?.FieldType == typeof(int),
                        "GamePreferencesComponent.initPerformance (int)");
                var expectedTypes = new Dictionary<string, Type>
                {
                    ["Performance"] = typeof(int), ["RenderScale"] = typeof(float), ["FPS"] = typeof(float), ["Vsync"] = typeof(bool), ["FullScreen"] = typeof(bool),
                };
                Assert.Equal(expectedTypes.Keys, McvsOffline.GraphicsHooks.InGameOptions);
                foreach (string option in McvsOffline.GraphicsHooks.InGameOptions) Prop(p, preferences, option, PublicInstance, expectedTypes[option]);
                Type worldType = Prop(p, preferences, "World", PublicInstance)?.PropertyType;
                Type storageType = Prop(p, worldType, "LocalStorage", PublicInstance)?.PropertyType;
                Method(p, storageType, "HasMember", PublicInstance, new[] { typeof(string) }, typeof(bool));

                // WindowsExit (§6.6) and the watchdog.
                Method(p, type(engine, "Gameloft.Engine.MenuManager"), McvsOffline.Embedded.Windows.WindowsExit.ExitMethod, PublicStatic, Type.EmptyTypes, typeof(void));
                Type application = type(engine, "Gameloft.Engine.GameApplication");
                var watchdog = new McvsOffline.Embedded.Diagnostics.GameThreadWatchdog(null, null, null);
                p.Check(watchdog.Bind(application), "GameThreadWatchdog binds GameApplication");
                p.Check(application?.GetField("totalTime", BindingFlags.NonPublic | BindingFlags.Instance)?.FieldType == typeof(double), "GameApplication.totalTime");
                p.Check(application?.GetField("suspendUpdateInBackground", BindingFlags.NonPublic | BindingFlags.Instance)?.FieldType == typeof(bool),
                        "GameApplication.suspendUpdateInBackground");

                // The settings page's members (GameReflection, shared with Android).
                Func<string, Type> resolve = name => new[] { engine, game, online }.Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
                List<string> pageProblems = GameReflection.Verify(resolve);
                foreach (string problem in pageProblems) output.WriteLine("GameReflection: " + problem);
                p.List.AddRange(pageProblems.Select(x => "GameReflection: " + x));

                foreach (string problem in p.List) output.WriteLine(problem);
                Assert.Empty(p.List);
                GC.KeepAlive(context);
            }
            finally
            {
                context.Unload();
            }
        }

        /// <summary>The game's own assemblies from the Steam folder; the BCL from net8.</summary>
        private sealed class PcContext : AssemblyLoadContext
        {
            private readonly string directory;

            public PcContext(string directory) : base("mcvs-pc-reflection-" + Guid.NewGuid().ToString("N"), isCollectible: true)
            {
                this.directory = directory;
            }

            protected override Assembly Load(AssemblyName name)
            {
                string simple = name.Name;
                if (simple == "mscorlib" || simple == "netstandard" || simple == "System" || simple.StartsWith("System.", StringComparison.Ordinal) ||
                    simple.StartsWith("Microsoft.", StringComparison.Ordinal))
                    return null;
                string path = Path.Combine(directory, simple + ".dll");
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }
        }
    }
}
