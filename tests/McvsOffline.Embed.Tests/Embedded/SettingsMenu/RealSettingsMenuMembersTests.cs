using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using McvsOffline.Embedded.Admin;
using McvsOffline.Embedded.Admin.SettingsMenu;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// Every game member the Settings menu code (docs/design/settings-menu.md §4.1 WP-A) uses exists with the expected signature in
    /// the player's real assemblies, found by the lookups the code runs (<see cref="GameReflection.FindPlainMethod"/> on the runtime
    /// type, <see cref="UiNatives.IsTableShape"/>). Android (MCVS_REFS; skipped without the assemblies unless
    /// MCVS_REQUIRE_REAL_CLIENT=1): the native table, the natives the registry takes over and the game's delegates it sits next to,
    /// GoToForum and the confirm popup, and the engine statics of the PERFORMANCE page. Steam (MCVS_PC_REFS; when present, or
    /// MCVS_REQUIRE_PC=1): only what PC uses, the native table and EnterSection. Catches a game update renaming anything.
    /// </summary>
    public class RealSettingsMenuMembersTests
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public;

        private readonly ITestOutputHelper output;

        public RealSettingsMenuMembersTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        public static TheoryData<string> Platforms()
        {
            var data = new TheoryData<string> { "android" };
            if (File.Exists(Path.Combine(PcRefs, "Game.dll")) || Environment.GetEnvironmentVariable("MCVS_REQUIRE_PC") == "1") data.Add("pc");
            return data;
        }

        private static string PcRefs
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("MCVS_PC_REFS");
                return string.IsNullOrEmpty(dir) ? "/home/user/mcvs-work/pc/install" : dir;
            }
        }

        [Theory]
        [MemberData(nameof(Platforms))]
        public void TheNativeTableAndEnterSectionResolve(string platform)
        {
            Inspect(platform, (resolve, problems) =>
            {
                Type menu = resolve("Gameloft.Engine.MenuComponent");
                Type ui = resolve("Game.UIComponent");
                Assert.NotNull(menu);
                Assert.NotNull(ui);
                Assert.True(menu.IsAssignableFrom(ui), "UIComponent is a MenuComponent");

                // FlashInterop finds them on the runtime type (UIComponent), as the registry calls them.
                MethodInfo overrideNative = GameReflection.FindPlainMethod(ui, "OverrideNativeFunction", typeof(string), typeof(Delegate), typeof(bool), typeof(bool));
                Expect(problems, overrideNative != null && overrideNative.ReturnType == typeof(object), "MenuComponent.OverrideNativeFunction(string, Delegate, bool, bool) : object");
                MethodInfo remove = GameReflection.FindPlainMethod(ui, "RemoveNativeFunctionOverride", typeof(string));
                Expect(problems, remove != null && remove.ReturnType == typeof(void), "MenuComponent.RemoveNativeFunctionOverride(string) : void");

                // The per-instance table the re-ensure reads (by name; skeptic E5: an instance field, not a static).
                FieldInfo table = menu.GetField(UiNatives.TableField, BindingFlags.Instance | BindingFlags.NonPublic);
                Expect(problems, table != null && UiNatives.IsTableShape(table.FieldType),
                       "MenuComponent." + UiNatives.TableField + " : Dictionary<string, X { List<Delegate> Delegates }>");
                Expect(problems, menu.GetField(UiNatives.TableField, BindingFlags.Static | BindingFlags.NonPublic) == null, "the table is per instance");

                // The game's own EnterSection delegate takes one string, as ours (UIComponent.cs:1449, PC :1529).
                MethodInfo enter = ui.GetMethod("NativeEnterSection", Instance, null, new[] { typeof(string) }, null);
                Expect(problems, enter != null && enter.ReturnType == typeof(void), "UIComponent.NativeEnterSection(string) : void");

                // The client UI (E5): UISceneExtentions.GetUI(World) : UIComponent.
                Type world = resolve("Gameloft.Engine.World");
                MethodInfo getUi = GameReflection.FindStaticMethod(resolve("Game.UISceneExtentions"), "GetUI", world);
                Expect(problems, getUi != null && getUi.ReturnType == ui, "UISceneExtentions.GetUI(World) : UIComponent");
            });
        }

        [Fact]
        public void TheAndroidNativesThePageTakesOverResolve()
        {
            Inspect("android", (resolve, problems) =>
            {
                Type ui = resolve("Game.UIComponent");

                // GoToForum (E4: our delegate calls the game's body itself).
                MethodInfo forum = GameReflection.FindPlainMethod(ui, "GoToForum");
                Expect(problems, forum != null && forum.ReturnType == typeof(void) && !forum.IsStatic, "UIComponent.GoToForum() : void");

                // ShowGenericConfirmPopup: exactly one callback overload with an optional enum priority defaulting to AtEnd.
                List<MethodInfo> popups = ui.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "ShowGenericConfirmPopup" && !m.IsGenericMethodDefinition)
                    .Where(m =>
                    {
                        ParameterInfo[] p = m.GetParameters();
                        return p.Length == 4 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(Dictionary<string, object>) &&
                               p[2].ParameterType == typeof(Action<Dictionary<string, object>>) && p[3].ParameterType.IsEnum && p[3].IsOptional;
                    }).ToList();
                Expect(problems, popups.Count == 1, "UIComponent.ShowGenericConfirmPopup(string, Dictionary<string, object>, Action<Dictionary<string, object>>, PopupPriority = AtEnd)");
                if (popups.Count == 1)
                {
                    ParameterInfo priority = popups[0].GetParameters()[3];
                    Expect(problems, Enum.GetName(priority.ParameterType, priority.RawDefaultValue) == "AtEnd", "PopupPriority's default is AtEnd");
                    Expect(problems, popups[0].ReturnType == typeof(void), "the callback overload returns void (not the async Task<bool> one)");
                }

                // The game's delegates next to ours: CanChange* as bools (ours: boxed false or null), the preferences natives.
                PropertyInfo maxFramerate = resolve("Game.GameClientComponent").GetProperty("CanChangeMaxFramerate", Static);
                Expect(problems, maxFramerate != null && maxFramerate.PropertyType == typeof(bool), "GameClientComponent.CanChangeMaxFramerate : static bool");
                Type preferences = resolve("Game.GamePreferencesComponent");
                PropertyInfo graphic = preferences.GetProperty("NativeCanChangeGraphicSetting", BindingFlags.Instance | BindingFlags.Public);
                Expect(problems, graphic != null && graphic.PropertyType == typeof(bool), "GamePreferencesComponent.NativeCanChangeGraphicSetting : bool");
                FieldInfo fps = preferences.GetField("FpsValues", Static);
                Expect(problems, fps != null && fps.FieldType == typeof(List<int>), "GamePreferencesComponent.FpsValues : static List<int>");
                Type userPreferences = resolve("Gameloft.Engine.UserPreferencesComponent");
                Expect(problems, userPreferences != null && userPreferences.IsAssignableFrom(preferences), "GamePreferencesComponent is a UserPreferencesComponent");
                MethodInfo set = userPreferences.GetMethod("NativeSetUserPreference", Instance, null, new[] { typeof(string), typeof(object) }, null);
                Expect(problems, set != null && set.ReturnType == typeof(void), "UserPreferencesComponent.NativeSetUserPreference(string, object) : void");
                MethodInfo save = userPreferences.GetMethod("NativeSaveUserPreferences", Instance, null, Type.EmptyTypes, null);
                Expect(problems, save != null && save.ReturnType == typeof(void), "UserPreferencesComponent.NativeSaveUserPreferences() : void");
                MethodInfo get = userPreferences.GetMethod("NativeGetUserPreference", Instance, null, new[] { typeof(string) }, null);
                Expect(problems, get != null && get.ReturnType == typeof(object), "UserPreferencesComponent.NativeGetUserPreference(string) : object");

                // The PERFORMANCE page's engine statics (design §2; the same members on both platforms).
                Type post = resolve("Gameloft.Engine.PostProcessManager");
                PropertyInfo scale = post.GetProperty("RenderTargetSizeScale", Static);
                Expect(problems, scale != null && scale.PropertyType == typeof(float) && scale.CanWrite, "PostProcessManager.RenderTargetSizeScale : static float { get; set; }");
                FieldInfo scaleOld = post.GetField("RenderTargetSizeScaleOld", Static);
                Expect(problems, scaleOld != null && scaleOld.FieldType == typeof(float), "PostProcessManager.RenderTargetSizeScaleOld : static float");
                PropertyInfo msaa = post.GetProperty("RenderTargetAntialiasingSetting", Static);
                Expect(problems, msaa != null && msaa.PropertyType == resolve("Gameloft.Engine.AntialiasingSetting"), "PostProcessManager.RenderTargetAntialiasingSetting : static AntialiasingSetting");
                Type aa = resolve("Gameloft.Engine.AntialiasingSetting");
                Expect(problems, aa != null && aa.IsEnum && new[] { "None", "Multisample2x", "Multisample4x", "Multisample8x" }.All(n => Enum.GetNames(aa).Contains(n)),
                       "AntialiasingSetting has None, Multisample2x, Multisample4x, Multisample8x");
                FieldInfo layers = resolve("Gameloft.Engine.World").GetField("GlobalExcludeLayers", Static);
                Expect(problems, layers != null && layers.FieldType == typeof(List<string>), "World.GlobalExcludeLayers : static List<string>");
                FieldInfo lowRes = resolve("Game.GameGameOptionsComponent").GetField("LowResMaps", Static);
                Expect(problems, lowRes != null && lowRes.FieldType == typeof(bool), "GameGameOptionsComponent.LowResMaps : static bool");
            });
        }

        // ------------------------------------------------------------------ the assemblies

        /// <summary>Loads the platform's Gameloft.Engine.dll and Game.dll by path in a collectible context and runs the checks.</summary>
        private void Inspect(string platform, Action<Func<string, Type>, List<string>> check)
        {
            string dir = platform == "pc" ? PcDir() : Refs();
            if (dir == null) return;
            var context = new GameContext(dir);
            try
            {
                Assembly engine = context.LoadFromAssemblyPath(Path.Combine(dir, "Gameloft.Engine.dll"));
                Assembly game = context.LoadFromAssemblyPath(Path.Combine(dir, "Game.dll"));
                Func<string, Type> resolve = name => new[] { engine, game }.Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
                var problems = new List<string>();
                check(resolve, problems);
                foreach (string p in problems) output.WriteLine(platform + ": " + p);
                Assert.Empty(problems);
                GC.KeepAlive(context);
            }
            finally
            {
                context.Unload();
            }
        }

        private static void Expect(List<string> problems, bool ok, string what)
        {
            if (!ok) problems.Add(what);
        }

        private string Refs()
        {
            string dir = Environment.GetEnvironmentVariable("MCVS_REFS");
            if (string.IsNullOrEmpty(dir)) dir = "/home/user/mcvs-work/extracted/assemblies";
            if (File.Exists(Path.Combine(dir, "Game.dll")) && File.Exists(Path.Combine(dir, "Gameloft.Engine.dll"))) return dir;
            Assert.False(Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1", "MCVS_REQUIRE_REAL_CLIENT=1 but Game.dll/Gameloft.Engine.dll are missing in " + dir);
            output.WriteLine("Game.dll/Gameloft.Engine.dll not found (" + dir + "): skipped");
            return null;
        }

        private string PcDir()
        {
            string dir = PcRefs;
            if (File.Exists(Path.Combine(dir, "Game.dll")) && File.Exists(Path.Combine(dir, "Gameloft.Engine.dll"))) return dir;
            Assert.False(Environment.GetEnvironmentVariable("MCVS_REQUIRE_PC") == "1", "MCVS_REQUIRE_PC=1 but Game.dll/Gameloft.Engine.dll are missing in " + dir);
            output.WriteLine("PC Game.dll/Gameloft.Engine.dll not found (" + dir + "): skipped");
            return null;
        }

        /// <summary>The game's own assemblies from the folder; the BCL from net8.</summary>
        private sealed class GameContext : AssemblyLoadContext
        {
            private readonly string directory;

            public GameContext(string directory) : base("mcvs-settings-menu-" + Guid.NewGuid().ToString("N"), isCollectible: true)
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
