using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Graphics;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// GameOptions_windows.json on PC (docs/design/pc-port.md §6.7, F7): the MEM rule keeps the levels' Physics layer (MEM LowEnd's
    /// "Physics VFX" becomes "VFX"), graphics.profile picks MidGPU/HighGPU with the PC's empty catch-all rule, and the text is
    /// otherwise unchanged. Synthetic and the player's real file (MCVS_PC_REFS Data/462043511.f); and GraphicsHooks' PC path
    /// (the component instead of m_options) against a look-alike GameOptionManager.
    /// </summary>
    [Collection("GameHooks")]
    public class GameOptionsWindowsTests
    {
        /// <summary>The data-pack file of GameOptions_windows.json (decimal CRC32 name, [ES-V] claim 11).</summary>
        public const string RealFile = "462043511.f";

        /// <summary>The MEM rule's count on the shipped file (MEM LowEnd only): the "[boot] graphics:" value WR2 expects.</summary>
        public const int RealFileRemoved = 1;

        private readonly ITestOutputHelper output;

        public GameOptionsWindowsTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        internal const string LookAlike = @"{
    ""header"": {""file_version"": 1},
    ""profiles"": {
        ""MEM"": {
            ""defaults"": [{""Default"": [[""lowResMenu"", false, ""help""], [""excludedLoadingLayers"", """", ""List of layers""]]}],
            ""selection"": [{""HighEnd"": [[[""GPUMEM>=700""], [""CORES>=4""]]]}, {""LowEnd"": []}],
            ""overrides"": {
                ""HighEnd"": {},
                ""LowEnd"": {""lowResMenu"": true, ""excludedLoadingLayers"": ""Physics VFX"", ""FontScale"": 1.0}
            },
            ""priority"": {}
        },
        ""GPU"": {
            ""defaults"": [{""Default"": [[""reducedAiUpdateFrequency"", false, ""help""], [""renderTargetSizeScale"", 1.0, ""help""]]}],
            ""selection"": [{""HighGPU"": [[[""GPUMEM>=700""]]]}, {""MidGPU"": [[[""GPUMEM>=550""]]]}, {""LowGPU"": []}],
            ""overrides"": {
                ""HighGPU"": {},
                ""MidGPU"": {""enableBloom"": false, ""reducedAiUpdateFrequency"": true},
                ""LowGPU"": {""reducedAiUpdateFrequency"": true, ""anisotropicFiltering"": 1.0}
            },
            ""priority"": {}
        }
    }
}";

        private static object Parse(string json)
        {
            object value;
            Assert.True(MiniJson.TryParse(json, out value), "does not parse");
            return value;
        }

        private static Dictionary<string, object> At(object root, params string[] path)
        {
            object cur = root;
            foreach (string key in path) cur = ((Dictionary<string, object>)cur)[key];
            return (Dictionary<string, object>)cur;
        }

        /// <summary>The input with MEM LowEnd's layers set to <paramref name="layers"/>, as a tree.</summary>
        private static object WithLowEndLayers(string json, string layers)
        {
            object tree = Parse(json);
            At(tree, "profiles", "MEM", "overrides", "LowEnd")["excludedLoadingLayers"] = layers;
            return tree;
        }

        [Fact]
        public void AutoWithTheRuleRemovesOnlyPhysicsFromMemLowEnd()
        {
            GameOptionsRewrite r = GameOptionsWindows.Rewrite(LookAlike, "auto", false, true);
            Assert.True(r.Changed);
            Assert.Equal(1, r.PhysicsRemoved);
            Assert.Equal("on, removed 1", r.MemRule);
            Assert.Null(r.Profile);
            Assert.True(JsonValue.DeepEquals(WithLowEndLayers(LookAlike, "VFX"), Parse(r.Json)));
            Assert.Contains("\"FontScale\": 1.0", r.Json);   // everything else byte for byte
            Assert.Equal(LookAlike.Length - "Physics ".Length, r.Json.Length);
            Assert.EndsWith("; MEM rule on, removed 1", r.Summary);
            Assert.StartsWith("graphics.profile auto: the game picks its GPU profile itself", r.Summary);
        }

        [Fact]
        public void WithoutTheRuleTheTextIsUnchanged()
        {
            GameOptionsRewrite r = GameOptionsWindows.Rewrite(LookAlike, "auto", false, false);
            Assert.Same(LookAlike, r.Json);
            Assert.False(r.Changed);
            Assert.Equal("off", r.MemRule);
            Assert.EndsWith("; MEM rule off", r.Summary);
        }

        [Fact]
        public void MidAndHighBecomeASingleEmptyRule()
        {
            foreach (string choice in new[] { "mid", "high" })
            {
                GameOptionsRewrite r = GameOptionsWindows.Rewrite(LookAlike, choice, false, true);
                string profile = choice == "mid" ? "MidGPU" : "HighGPU";
                Assert.Equal(profile, r.Profile);
                object tree = Parse(r.Json);
                Dictionary<string, object> gpu = At(tree, "profiles", "GPU");
                Assert.True(JsonValue.DeepEquals(Parse("[{\"" + profile + "\": []}]"), gpu["selection"]));
                Assert.Equal("VFX", At(tree, "profiles", "MEM", "overrides", "LowEnd")["excludedLoadingLayers"]);
                Assert.Equal(true, At(gpu, "overrides", profile)["reducedAiUpdateFrequency"]);   // the MidGPU value (not fullProfile)
                Assert.Contains("the selection is one empty rule", r.Summary);
                Assert.True(JsonValue.DeepEquals(Parse("{}"), gpu["priority"]));   // nothing to step on PC
            }
            GameOptionsRewrite full = GameOptionsWindows.Rewrite(LookAlike, "high", true, true);
            Assert.False(At(Parse(full.Json), "profiles", "GPU", "overrides", "HighGPU").ContainsKey("reducedAiUpdateFrequency"));
        }

        [Fact]
        public void UltraIsHighGpuWithANote()
        {
            GameOptionsRewrite r = GameOptionsWindows.Rewrite(LookAlike, "Ultra", false, true);
            Assert.Equal("HighGPU", r.Profile);
            Assert.StartsWith(GameOptionsWindows.UltraNote + "; graphics.profile ultra: GameOptions GPU profile forced to HighGPU", r.Summary);
            Assert.Equal(GameOptionsWindows.Rewrite(LookAlike, "high", false, true).Json, r.Json);
        }

        [Fact]
        public void TheRewriteIsIdempotentAndLeavesBadInputAlone()
        {
            string once = GameOptionsWindows.Rewrite(LookAlike, "high", false, true).Json;
            GameOptionsRewrite twice = GameOptionsWindows.Rewrite(once, "high", false, true);
            Assert.Equal(once, twice.Json);
            Assert.Equal(0, twice.PhysicsRemoved);
            Assert.Equal("on, removed 0", twice.MemRule);

            GameOptionsRewrite bad = GameOptionsWindows.Rewrite("{not json", "high", false, true);
            Assert.Equal("{not json", bad.Json);
            Assert.False(bad.Changed);
            Assert.Contains("could not be read; left alone", bad.Summary);
            Assert.Null(GameOptionsWindows.Rewrite(null, "auto", false, true).Json);
            Assert.Equal("", GameOptionsWindows.Rewrite("", "auto", false, true).Json);
            GameOptionsRewrite unknown = GameOptionsWindows.Rewrite(LookAlike, "extreme", false, true);
            Assert.Contains("is not one of auto, mid, high, ultra", unknown.Summary);
            Assert.Null(unknown.Profile);
        }

        [Fact]
        public void PhysicsIsRemovedFromDefaultRowsAndEveryPositionButNothingElse()
        {
            string json = "{\"profiles\": {\"MEM\": {\"defaults\": [{\"Default\": [[\"excludedLoadingLayers\", \"Physics\", \"h\"]]}], " +
                          "\"overrides\": {\"A\": {\"excludedLoadingLayers\": \"VFX Physics Decals\"}, \"B\": {\"excludedLoadingLayers\": \"PhysicsDebug\"}, " +
                          "\"C\": {\"note\": \"excludedLoadingLayers\", \"x\": \"Physics\"}}}}}";
            string result;
            int removed;
            Assert.True(GameOptionsWindows.TryRemovePhysics(json, out result, out removed));
            Assert.Equal(2, removed);
            object tree = Parse(result);
            Assert.Equal("", ((List<object>)((List<object>)((Dictionary<string, object>)((List<object>)At(tree, "profiles", "MEM")["defaults"])[0])["Default"])[0])[1]);
            Assert.Equal("VFX Decals", At(tree, "profiles", "MEM", "overrides", "A")["excludedLoadingLayers"]);
            Assert.Equal("PhysicsDebug", At(tree, "profiles", "MEM", "overrides", "B")["excludedLoadingLayers"]);
            Assert.Equal("Physics", At(tree, "profiles", "MEM", "overrides", "C")["x"]);
            int count;
            Assert.Equal("VFX", GameOptionsWindows.WithoutToken("Physics  VFX Physics", "Physics", out count));
            Assert.Equal(2, count);
        }

        // ------------------------------------------------------------------ the player's real GameOptions_windows.json

        private string RealJson()
        {
            string pc = TestEnvironment.PcRefs;
            string file = pc == null ? null : Path.Combine(pc, "Data", RealFile);
            if (file == null || !File.Exists(file))
            {
                if (TestEnvironment.RequirePc) throw new InvalidOperationException("MCVS_REQUIRE_PC=1 but MCVS_PC_REFS has no Data/" + RealFile);
                output.WriteLine("the PC data pack is not available (MCVS_PC_REFS): skipped");
                return null;
            }
            byte[] bytes = File.ReadAllBytes(file);
            if (Gcbf.IsGcbf(bytes)) bytes = Gcbf.Decode(bytes, true);
            string text = new System.Text.UTF8Encoding(false).GetString(bytes);
            return text.Length > 0 && text[0] == '﻿' ? text.Substring(1) : text;
        }

        [Fact]
        public void TheRealFileKeepsPhysicsWithTheRule()
        {
            string json = RealJson();
            if (json == null) return;
            GameOptionsRewrite auto = GameOptionsWindows.Rewrite(json, "auto", false, true);
            output.WriteLine(auto.Summary);
            Assert.Equal(RealFileRemoved, auto.PhysicsRemoved);
            Assert.True(JsonValue.DeepEquals(WithLowEndLayers(json, "VFX"), Parse(auto.Json)));
            Assert.Equal(json.Length - "Physics ".Length, auto.Json.Length);
            Assert.DoesNotContain("\"Physics VFX\"", auto.Json);
            Assert.Contains("\"excludedLoadingLayers\": \"VFX\"", auto.Json);
            Assert.Same(json, GameOptionsWindows.Rewrite(json, "auto", false, false).Json);

            foreach (string choice in new[] { "mid", "high", "ultra" })
            {
                GameOptionsRewrite r = GameOptionsWindows.Rewrite(json, choice, false, true);
                output.WriteLine(choice + ": " + r.Summary);
                Assert.NotNull(r.Profile);
                Dictionary<string, object> gpu = At(Parse(r.Json), "profiles", "GPU");
                Assert.Single((List<object>)gpu["selection"]);
                Assert.Equal(true, At(gpu, "overrides", r.Profile)["reducedAiUpdateFrequency"]);
                // Everything outside GPU.selection and the chosen block is the MEM-ruled original.
                object expected = WithLowEndLayers(json, "VFX");
                Dictionary<string, object> expectedGpu = At(expected, "profiles", "GPU");
                expectedGpu["selection"] = gpu["selection"];
                At(expectedGpu, "overrides")[r.Profile] = At(gpu, "overrides", r.Profile);
                Assert.True(JsonValue.DeepEquals(expected, Parse(r.Json)), choice + " changed more than the selection and its block");
            }
        }

        /// <summary>
        /// The PC help text of graphics.profile (SettingsSchema, WR2 F7) states the shipped file's GPU selection: the catch-all
        /// rules of HighGPU (GPUMEM>=700, CORES>=4) and MidGPU (GPUMEM>=550, MEM_T>=4000, CORES>=4), then LowGPU930 / LowGPU; and
        /// no GPU block sets excludedLoadingLayers while every one but HighGPU reduces the AI rate (the fullProfile text).
        /// </summary>
        [Fact]
        public void ThePcHelpTextMatchesTheRealGpuSelection()
        {
            string json = RealJson();
            if (json == null) return;
            Dictionary<string, object> gpu = At(Parse(json), "profiles", "GPU");
            var rules = new Dictionary<string, List<List<string>>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (Dictionary<string, object> entry in ((List<object>)gpu["selection"]).Cast<Dictionary<string, object>>())
            {
                foreach (KeyValuePair<string, object> kv in entry)
                {
                    order.Add(kv.Key);
                    rules[kv.Key] = ((List<object>)kv.Value).Cast<List<object>>()
                        .Select(rule => rule.Cast<List<object>>().Select(c => (string)c[0]).ToList()).ToList();
                }
            }
            Assert.Equal(new[] { "HighGPU", "MidGPU", "LowGPU930", "LowGPU" }, order);
            Func<string, List<string>> catchAll = p => rules[p].Single(r => !r[0].StartsWith("GPU=", StringComparison.Ordinal));
            Assert.Equal(new[] { "GPUMEM>=700", "CORES>=4", "GRAPHICS_FEATURE_LEVEL>930" }, catchAll("HighGPU"));
            Assert.Equal(new[] { "GPUMEM>=550", "MEM_T>=4000", "CORES>=4", "GRAPHICS_FEATURE_LEVEL>930" }, catchAll("MidGPU"));
            Assert.Empty(rules["LowGPU"]);
            Dictionary<string, object> overrides = At(gpu, "overrides");
            foreach (KeyValuePair<string, object> block in overrides)
            {
                var values = (Dictionary<string, object>)block.Value;
                Assert.False(values.ContainsKey(GameOptionsWindows.LayersOption), block.Key + " sets excludedLoadingLayers");
                Assert.Equal(block.Key != "HighGPU", values.ContainsKey("reducedAiUpdateFrequency"));
            }

            string help = McvsOffline.Server.Services.Admin.SettingsSchema.GroupsFor("windows").SelectMany(v => v.Keys).Single(k => k.Key == "graphics.profile").Help;
            Assert.Contains("HighGPU only with at least 700 MB of GPU memory and 4 cores", help);
            Assert.Contains("MidGPU with at least 550 MB of GPU memory, 4 GB of RAM and 4 cores, otherwise LowGPU", help);
        }

        // ------------------------------------------------------------------ the PC in-game graphics options (LocalStorage)

        private const string GameLookAlike = @"
namespace Game
{
    public class LocalStorageComponent
    {
        public bool HasPerformance;
        public bool HasMember(string key) { return HasPerformance && key == ""Performance""; }
    }
    public class World { public LocalStorageComponent LocalStorage { get; set; } }
    public class Component { public World World { get; set; } }
    public class GamePreferencesComponent : Component
    {
        public static GamePreferencesComponent instance;
        private int initPerformance = -1;
        public int Performance { get; set; }
        public float RenderScale { get; set; }
        public float FPS { get; set; }
        public bool Vsync { get; set; }
        public bool FullScreen { get; set; }
        public void Loaded() { initPerformance = Performance; }
    }
}";

        [Fact]
        public void TheInGameOptionsAreReadOnceTheClientPreferencesAreLoaded()
        {
            using (var sandbox = new FixtureCompiler.Sandbox().With("Game", FixtureCompiler.Compile("Game", GameLookAlike)))
            {
                System.Reflection.Assembly game = sandbox.Get("Game");
                Type prefs = game.GetType("Game.GamePreferencesComponent", true);
                Type world = game.GetType("Game.World", true);
                Type storage = game.GetType("Game.LocalStorageComponent", true);
                var logger = new Logger();
                var sink = new MemoryLogSink(LogLevel.Debug, 100);
                logger.AddSink(sink);
                GraphicsHooks.ResetForTests();
                try
                {
                    GraphicsHooks.Configure("auto", false, logger.For("game"), new BootTrace(logger.For("boot")));
                    GraphicsHooks.ConfigureWindows(true);
                    Assert.Null(GraphicsHooks.ReadInGamePreferences(null));
                    Assert.Null(GraphicsHooks.CaptureInGame(prefs));                     // no instance yet
                    Assert.Contains("; in-game graphics options not read yet", GraphicsHooks.Describe());

                    object component = Activator.CreateInstance(prefs);
                    prefs.GetField("instance").SetValue(null, component);
                    prefs.GetProperty("Performance").SetValue(component, 2);
                    prefs.GetProperty("RenderScale").SetValue(component, 0.8f);
                    prefs.GetProperty("FPS").SetValue(component, 60f);
                    prefs.GetProperty("Vsync").SetValue(component, true);
                    prefs.GetProperty("FullScreen").SetValue(component, false);
                    Assert.Null(GraphicsHooks.CaptureInGame(prefs));                     // LoadPreferencesWindows has not finished
                    prefs.GetMethod("Loaded").Invoke(component, null);
                    object w = Activator.CreateInstance(world);
                    prefs.GetProperty("World").SetValue(component, w);
                    Assert.Null(GraphicsHooks.CaptureInGame(prefs));                     // a server World: no LocalStorage
                    object s = Activator.CreateInstance(storage);
                    world.GetProperty("LocalStorage").SetValue(w, s);
                    Assert.Equal("in-game graphics quality Performance not set (the game's own choice), RenderScale 0.8, FPS 60, Vsync true, FullScreen false",
                                 GraphicsHooks.DescribeInGame(GraphicsHooks.ReadInGamePreferences(prefs)));
                    storage.GetField("HasPerformance").SetValue(s, true);

                    const string expected = "in-game graphics quality Performance 2 (High), RenderScale 0.8, FPS 60, Vsync true, FullScreen false";
                    Assert.Equal(expected, GraphicsHooks.CaptureInGame(prefs));
                    Assert.Equal(expected, GraphicsHooks.InGame);
                    prefs.GetProperty("Performance").SetValue(component, 0);
                    Assert.Equal(expected, GraphicsHooks.CaptureInGame(prefs));           // the start values: read once
                    Assert.EndsWith("; " + expected, GraphicsHooks.Describe());
                    Assert.Single(sink.Snapshot(), e => e.Message.Contains("graphics: " + expected));
                    Assert.DoesNotContain(sink.Snapshot(), e => e.Message.Contains("GameOptions_SelectedProfile"));
                }
                finally
                {
                    prefs.GetField("instance").SetValue(null, null);
                    GraphicsHooks.ResetForTests();
                }
            }
        }

        [Theory]
        [InlineData(true, null, "HighGPU", "LowGPU", true)]     // WR2 boots 2-8: auto, in-game High
        [InlineData(true, null, "LowGPU", "LowGPU", false)]
        [InlineData(true, "high", "HighGPU", "LowGPU", false)]  // forced by graphics.profile
        [InlineData(false, null, "HighGPU", "MidGPU", false)]   // Android keeps its line
        public void ALoadedProfileOtherThanTheOptimumIsTheInGameChoiceOnPc(bool windows, string choice, string loaded, string optimal, bool overridden)
        {
            GraphicsHooks.ResetForTests();
            try
            {
                GraphicsHooks.Configure(choice ?? "auto", false, null, null);
                if (windows) GraphicsHooks.ConfigureWindows(true);
                GraphicsHooks.GameOptionsJson(windows ? LookAlike : "{}");
                var native = new Dictionary<string, string> { ["loaded"] = loaded, ["optimal"] = optimal, ["inGame"] = windows ? "in-game graphics quality Performance 2 (High)" : null };
                string line = GraphicsHooks.DescribeApplied(new Dictionary<string, object>(), native);
                output.WriteLine(line);
                Assert.Equal(overridden, line.Contains("GPU profile " + loaded + " (device optimum " + optimal + "), overridden by the in-game graphics quality"));
                Assert.Equal(overridden, GraphicsHooks.NativeSummary.Contains("overridden"));
                Assert.Equal(windows, line.Contains("; in-game graphics quality Performance 2 (High)"));
            }
            finally
            {
                GraphicsHooks.ResetForTests();
            }
        }

        [Fact]
        public void TheGraphicsLineOnPcNamesTheMemRule()
        {
            string json = RealJson() ?? LookAlike;
            int expected = json == LookAlike ? 1 : RealFileRemoved;
            var logger = new Logger();
            var sink = new MemoryLogSink(LogLevel.Debug, 100);
            logger.AddSink(sink);
            GraphicsHooks.ResetForTests();
            try
            {
                GraphicsHooks.Configure("auto", false, logger.For("game"), new BootTrace(logger.For("boot")));
                GraphicsHooks.ConfigureWindows(true);
                Assert.Contains("has not read GameOptions_windows.json", GraphicsHooks.Describe());
                string rewritten = GraphicsHooks.GameOptionsJson(json);
                Assert.DoesNotContain("\"Physics VFX\"", rewritten);
                Assert.Contains(sink.Snapshot(), e => e.Message.Contains("graphics: graphics.profile auto: the game picks its GPU profile itself; MEM rule on, removed " + expected));
                Assert.Equal("on, removed " + expected, GraphicsHooks.MemRule);
                Assert.Contains("MEM rule on, removed " + expected, GraphicsHooks.Describe());

                GraphicsHooks.ResetForTests();
                GraphicsHooks.Configure("auto", false, logger.For("game"), null);
                GraphicsHooks.ConfigureWindows(false);
                Assert.Same(json, GraphicsHooks.GameOptionsJson(json));
                Assert.Contains("MEM rule off", GraphicsHooks.Describe());
            }
            finally
            {
                GraphicsHooks.ResetForTests();
            }
        }

        // ------------------------------------------------------------------ GameOptionsApplied on PC (the component, no m_options)

        private const string EngineLookAlike = @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace Gameloft.Engine
{
    public static class MoreMarshal
    {
        public static int Destroyed;
        public static string NativeStringToString(IntPtr p) { return Marshal.PtrToStringAnsi(p); }
        public static void DestroyNativeString(IntPtr p) { Destroyed++; Marshal.FreeHGlobal(p); }
    }
    public static class GameOptionManager
    {
        static readonly string[] ints = { ""ThreadPoolSize"" };
        static readonly string[] floats = { ""renderTargetSizeScale"" };
        static readonly string[] bools = { ""enableShadowMap"", ""lowResMenu"" };
        static readonly string[] strings = { ""excludedLoadingLayers"", ""addShaderDefine"" };
        static IntPtr N(string s) { return Marshal.StringToHGlobalAnsi(s); }
        public static int GetIntOptionsCount() { return ints.Length; }
        public static int GetFloatOptionsCount() { return floats.Length; }
        public static int GetBooleanOptionsCount() { return bools.Length; }
        public static int GetStringOptionsCount() { return strings.Length; }
        public static IntPtr GetIntOptionName(int i) { return N(ints[i]); }
        public static IntPtr GetFloatOptionName(int i) { return N(floats[i]); }
        public static IntPtr GetBooleanOptionName(int i) { return N(bools[i]); }
        public static IntPtr GetStringOptionName(int i) { return N(strings[i]); }
        public static int GetOptionInt(string name) { return 8; }
        public static float GetOptionFloat(string name) { return 0.75f; }
        public static bool GetOptionBoolean(string name) { return name == ""enableShadowMap""; }
        public static IntPtr GetOptionString(string name) { return N(name == ""excludedLoadingLayers"" ? ""VFX"" : ""HIGH_END""); }
    }
    public class GameOptionPerformanceProfileComponent { }
}";

        [Fact]
        public void TheAppliedLineReadsGameOptionManagerOnPc()
        {
            using (var sandbox = new FixtureCompiler.Sandbox().With("Gameloft.Engine", FixtureCompiler.Compile("Gameloft.Engine", EngineLookAlike)))
            {
                System.Reflection.Assembly engine = sandbox.Get("Gameloft.Engine");
                object component = Activator.CreateInstance(engine.GetType("Gameloft.Engine.GameOptionPerformanceProfileComponent", true));
                GraphicsHooks.ResetForTests();
                try
                {
                    GraphicsHooks.Configure("auto", false, null, null);
                    GraphicsHooks.ConfigureWindows(true);
                    GraphicsHooks.GameOptionsJson(LookAlike);
                    GraphicsHooks.GameOptionsApplied(component);
                    string line = GraphicsHooks.Effective;
                    output.WriteLine(line);
                    Assert.Contains("; MEM rule on, removed 1", line);
                    Assert.Contains("; excludedLoadingLayers 'VFX'", line);
                    Assert.DoesNotContain("Physics excluded", line);
                    Assert.EndsWith("6 options: ThreadPoolSize=8, addShaderDefine='HIGH_END', enableShadowMap=true, excludedLoadingLayers='VFX', " +
                                    "lowResMenu=false, renderTargetSizeScale=0.75", line);
                    Assert.Equal(8, (int)engine.GetType("Gameloft.Engine.MoreMarshal").GetField("Destroyed").GetValue(null));   // every native string released
                }
                finally
                {
                    GraphicsHooks.ResetForTests();
                }
            }
        }

        [Fact]
        public void PhysicsInTheEffectiveLayersIsFlagged()
        {
            GraphicsHooks.ResetForTests();
            try
            {
                GraphicsHooks.Configure("auto", false, null, null);
                GraphicsHooks.ConfigureWindows(false);
                GraphicsHooks.GameOptionsApplied(new Dictionary<string, object> { ["excludedLoadingLayers"] = "Physics VFX" });
                Assert.Contains("excludedLoadingLayers 'Physics VFX' (Physics excluded!)", GraphicsHooks.Effective);
            }
            finally
            {
                GraphicsHooks.ResetForTests();
            }
        }
    }
}
