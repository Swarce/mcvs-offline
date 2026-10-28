using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embedded.Graphics;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// settings.json graphics.profile (docs/research/pc-textures.md WP0): GameOptionsProfile's rewrite of GameOptions_android.json
    /// on a small look-alike and on the player's real file (MCVS_DATAPACK_DECODED or MCVS_DATAPACK), and GraphicsHooks, the side
    /// of patch 'graphics' in the embedded server. The real-file test also pins what each choice changes against the MidGPU
    /// profile the S22 gets today, and that none of them touches the threading options of docs/STATUS.md.
    /// </summary>
    [Collection("GameHooks")]
    public class GraphicsProfileTests : IDisposable
    {
        /// <summary>The shape of the real file, trimmed: GPU defaults rows, a selection list, overrides and priority.</summary>
        internal const string LookAlike = @"{
    ""header"": {""file_version"": 5},
    ""profiles"": {
        ""CPU"": {""defaults"": [{""Default"": []}], ""selection"": [], ""overrides"": [], ""priority"": []},
        ""GPU"": {
            ""defaults"": [{""Default"": [
                [""lowEndMaterial"", true, ""help"", ""[;]""],
                [""renderTargetSizeScale"", 0.7, ""help"", ""[;]""],
                [""enableRenderThread"", false, ""help"", ""[;]""],
                [""excludedLoadingLayers"", ""Physics VFX"", ""help"", ""[;]""],
                [""reducedAiUpdateFrequency"", true, ""help"", ""[;]""]
            ]}],
            ""selection"": [{""HighGPU"": [[[""GPU$$Adreno""], [""GPU$$630""]]]}, {""MidGPU"": [[[""CORES>1""]]]}],
            ""overrides"": {
                ""High_Ultra"": {""lowEndMaterial"": false, ""addShaderDefine"": ""GOD_END"", ""anisotropicFiltering"": 3.0, ""enableRenderThread"": true, ""excludedLoadingLayers"": ""-"", ""reducedAiUpdateFrequency"": false},
                ""HighGPU"": {""lowEndMaterial"": false, ""addShaderDefine"": ""HIGH_END"", ""anisotropicFiltering"": 2.0, ""enableRenderThread"": true, ""excludedLoadingLayers"": ""-"", ""reducedAiUpdateFrequency"": false},
                ""MidGPU"": {""lowEndMaterial"": false, ""addShaderDefine"": ""MID_END"", ""renderTargetSizeScale"": 0.6, ""enableRenderThread"": true, ""excludedLoadingLayers"": ""Physics""}
            },
            ""priority"": {""High_Ultra"": ["""", ""HighGPU""], ""HighGPU"": [""High_Ultra"", ""MidGPU""], ""MidGPU"": [""HighGPU"", ""LowGPU""]}
        }
    }
}";

        private readonly ITestOutputHelper output;

        public GraphicsProfileTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        public void Dispose()
        {
            GraphicsHooks.ResetForTests();
        }

        private static Dictionary<string, object> Gpu(string json) =>
            (Dictionary<string, object>)((Dictionary<string, object>)MiniJson.ParseObject(json)["profiles"])["GPU"];

        private static Dictionary<string, object> Block(string json, string profile) =>
            (Dictionary<string, object>)((Dictionary<string, object>)Gpu(json)["overrides"])[profile];

        [Fact]
        public void AutoAndUnknownValuesLeaveTheTextAlone()
        {
            foreach (string choice in new[] { "auto", "AUTO", " auto ", null, "", "potato", "HighGPU" })
            {
                GameOptionsRewrite r = GameOptionsProfile.Rewrite(LookAlike, choice, false);
                Assert.Same(LookAlike, r.Json);
                Assert.False(r.Changed);
                Assert.Null(r.Profile);
                Assert.Empty(r.Pinned);
                Assert.Contains("graphics.profile", r.Summary);
            }
            Assert.Equal("auto", GameOptionsProfile.Normalize("Auto"));
            Assert.Null(GameOptionsProfile.Normalize("medium"));
            Assert.Equal(new[] { "auto", "mid", "high", "ultra" }, GameOptionsProfile.Choices);
            Assert.Null(GameOptionsProfile.GpuProfileOf("auto"));
            Assert.Equal("MidGPU", GameOptionsProfile.GpuProfileOf("mid"));
            Assert.Equal("HighGPU", GameOptionsProfile.GpuProfileOf("High"));
            Assert.Equal("High_Ultra", GameOptionsProfile.GpuProfileOf("ultra"));
        }

        [Theory]
        [InlineData("mid", "MidGPU")]
        [InlineData("high", "HighGPU")]
        [InlineData("ultra", "High_Ultra")]
        public void AForcedProfileIsTheOnlySelectionRuleAndHasNoPriorityStep(string choice, string profile)
        {
            GameOptionsRewrite r = GameOptionsProfile.Rewrite(LookAlike, choice, true);
            output.WriteLine(r.Summary);
            Assert.True(r.Changed);
            Assert.Equal(profile, r.Profile);
            Dictionary<string, object> gpu = Gpu(r.Json);
            var selection = (List<object>)gpu["selection"];
            var rule = (Dictionary<string, object>)Assert.Single(selection);
            Assert.Equal(profile, rule.Keys.Single());
            Assert.Equal("[[[\"CORES>1\"]]]", MiniJson.Serialize(rule[profile]));
            Assert.Equal("[\"\",\"\"]", MiniJson.Serialize(((Dictionary<string, object>)gpu["priority"])[profile]));
            // fullProfile: the blocks themselves are untouched, and so is everything outside the GPU selection and priority.
            Dictionary<string, object> before = MiniJson.ParseObject(LookAlike);
            Dictionary<string, object> after = MiniJson.ParseObject(r.Json);
            Assert.True(JsonValue.DeepEquals(Gpu(LookAlike)["overrides"], gpu["overrides"]));
            Assert.True(JsonValue.DeepEquals(Gpu(LookAlike)["defaults"], gpu["defaults"]));
            Assert.True(JsonValue.DeepEquals(before["header"], after["header"]));
            Assert.True(JsonValue.DeepEquals(((Dictionary<string, object>)before["profiles"])["CPU"], ((Dictionary<string, object>)after["profiles"])["CPU"]));
            Assert.Empty(r.Pinned);
        }

        [Fact]
        public void HighAndUltraKeepTheWorldOptionsAtTheMidValuesUnlessFullProfile()
        {
            GameOptionsRewrite high = GameOptionsProfile.Rewrite(LookAlike, "high", false);
            output.WriteLine(high.Summary);
            Dictionary<string, object> block = Block(high.Json, "HighGPU");
            Assert.Equal("Physics", block["excludedLoadingLayers"]);            // MidGPU's own value
            Assert.Equal(true, block["reducedAiUpdateFrequency"]);              // MidGPU has none: the GPU default
            Assert.Equal("HIGH_END", block["addShaderDefine"]);                 // the graphics options stay Gameloft's
            Assert.Equal(2.0, block["anisotropicFiltering"]);
            Assert.IsType<double>(block["anisotropicFiltering"]);               // floats stay floats for the native parser
            Assert.Equal(new[] { "excludedLoadingLayers 'Physics' (HighGPU: '-')", "reducedAiUpdateFrequency true (HighGPU: false)" }, high.Pinned);
            Assert.Contains("world options kept at the MidGPU values", high.Summary);
            Assert.Contains("\"anisotropicFiltering\":2.0", high.Json);
            // The other blocks are not touched.
            Assert.True(JsonValue.DeepEquals(Block(LookAlike, "High_Ultra"), Block(high.Json, "High_Ultra")));

            GameOptionsRewrite mid = GameOptionsProfile.Rewrite(LookAlike, "mid", false);
            Assert.Empty(mid.Pinned);
            Assert.True(JsonValue.DeepEquals(Block(LookAlike, "MidGPU"), Block(mid.Json, "MidGPU")));
            Assert.Contains("already equal MidGPU's", mid.Summary);

            GameOptionsRewrite full = GameOptionsProfile.Rewrite(LookAlike, "ultra", true);
            Assert.Equal("-", Block(full.Json, "High_Ultra")["excludedLoadingLayers"]);
            Assert.Equal(false, Block(full.Json, "High_Ultra")["reducedAiUpdateFrequency"]);
            Assert.Contains("fullProfile true", full.Summary);
        }

        [Fact]
        public void AFileWithoutTheBlockOrBrokenJsonIsLeftAlone()
        {
            string withoutUltra = LookAlike.Replace("\"High_Ultra\": {", "\"Other\": {").Replace("\"High_Ultra\": [", "\"Other\": [");
            GameOptionsRewrite r = GameOptionsProfile.Rewrite(withoutUltra, "ultra", false);
            Assert.Same(withoutUltra, r.Json);
            Assert.Contains("no GPU profile 'High_Ultra'", r.Summary);
            Assert.Same("{ nope", GameOptionsProfile.Rewrite("{ nope", "high", false).Json);
            Assert.Null(GameOptionsProfile.Rewrite(null, "high", false).Json);
            Assert.Equal("", GameOptionsProfile.Rewrite("", "high", false).Json);
            Assert.Same("[1,2]", GameOptionsProfile.Rewrite("[1,2]", "high", false).Json);
        }

        // ------------------------------------------------------------------ the player's real GameOptions_android.json

        private string RealGameOptions()
        {
            string decoded = Environment.GetEnvironmentVariable("MCVS_DATAPACK_DECODED");
            if (!string.IsNullOrEmpty(decoded) && File.Exists(Path.Combine(decoded, "GameOptions_android.json")))
                return File.ReadAllText(Path.Combine(decoded, "GameOptions_android.json"));
            string data = Environment.GetEnvironmentVariable("MCVS_DATAPACK");
            if (!string.IsNullOrEmpty(data) && new DataPack(data).TryReadText("GameOptions_android.json", out string text)) return text;
            output.WriteLine("GameOptions_android.json not available (MCVS_DATAPACK_DECODED, MCVS_DATAPACK): skipped");
            return null;
        }

        /// <summary>GPU defaults overlaid with one block: the options the engine resolves for that profile (no Specific/MEM).</summary>
        private static Dictionary<string, object> Effective(string json, string profile)
        {
            Dictionary<string, object> gpu = Gpu(json);
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (object row in (List<object>)((Dictionary<string, object>)((List<object>)gpu["defaults"])[0])["Default"])
            {
                var r = (List<object>)row;
                result[(string)r[0]] = r[1];
            }
            foreach (var kv in Block(json, profile)) result[kv.Key] = kv.Value;
            return result;
        }

        private static List<string> Diff(Dictionary<string, object> from, Dictionary<string, object> to) =>
            to.Keys.Union(from.Keys).OrderBy(k => k, StringComparer.Ordinal)
              .Where(k => !JsonValue.DeepEquals(from.TryGetValue(k, out object a) ? a : null, to.TryGetValue(k, out object b) ? b : null))
              .Select(k => k + " " + MiniJson.Serialize(from.TryGetValue(k, out object a) ? a : null) + " -> " + MiniJson.Serialize(to.TryGetValue(k, out object b) ? b : null))
              .ToList();

        /// <summary>The options that decide threads and physics (docs/STATUS.md device test 2): no choice may change them.</summary>
        private static readonly string[] ThreadingOptions =
        {
            "enableRenderThread", "renderThreadMode", "deferredPhysics", "multithreadedPhysics", "multithreadedAnimations", "ThreadPoolSize",
            "PostProcessMutex",
        };

        [Fact]
        public void TheRealFileRewritesForEveryChoiceAndOnlyTheDocumentedOptionsMove()
        {
            string json = RealGameOptions();
            if (json == null)
            {
                Assert.False(Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1" &&
                             !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MCVS_DATAPACK_DECODED")),
                             "MCVS_DATAPACK_DECODED is set but has no GameOptions_android.json");
                return;
            }
            // Gameloft's catch-all is the last rule, as the rewrite assumes; the blocks exist.
            var selection = (List<object>)Gpu(json)["selection"];
            Assert.Equal("{\"MidGPU\":[[[\"CORES>1\"]]]}", MiniJson.Serialize(selection.Last()));
            Dictionary<string, object> mid = Effective(json, "MidGPU");

            foreach (string choice in new[] { "mid", "high", "ultra" })
            {
                foreach (bool full in new[] { false, true })
                {
                    GameOptionsRewrite r = GameOptionsProfile.Rewrite(json, choice, full);
                    output.WriteLine(choice + (full ? " (fullProfile)" : "") + ": " + r.Summary);
                    Assert.True(r.Changed);
                    Assert.True(MiniJson.TryParse(r.Json, out object parsed));
                    // Every other profile family and every other GPU block is byte-for-byte the same value.
                    var before = (Dictionary<string, object>)MiniJson.ParseObject(json)["profiles"];
                    var after = (Dictionary<string, object>)((Dictionary<string, object>)parsed)["profiles"];
                    foreach (string family in before.Keys.Where(k => k != "GPU")) Assert.True(JsonValue.DeepEquals(before[family], after[family]), family);
                    var blocksBefore = (Dictionary<string, object>)Gpu(json)["overrides"];
                    var blocksAfter = (Dictionary<string, object>)Gpu(r.Json)["overrides"];
                    foreach (string block in blocksBefore.Keys.Where(k => k != r.Profile))
                        Assert.True(JsonValue.DeepEquals(blocksBefore[block], blocksAfter[block]), block);

                    Dictionary<string, object> effective = Effective(r.Json, r.Profile);
                    List<string> changes = Diff(mid, effective);
                    output.WriteLine("  vs MidGPU: " + (changes.Count == 0 ? "(none)" : string.Join("; ", changes)));
                    foreach (string option in ThreadingOptions)
                    {
                        Assert.True(JsonValue.DeepEquals(mid.TryGetValue(option, out object m) ? m : null, effective.TryGetValue(option, out object e) ? e : null),
                                    choice + " changes " + option);
                    }
                    if (!full)
                    {
                        Assert.Equal(mid["excludedLoadingLayers"], effective["excludedLoadingLayers"]);
                        Assert.Equal(mid["reducedAiUpdateFrequency"], effective["reducedAiUpdateFrequency"]);
                    }
                }
            }

            // The documented differences (docs/STATUS.md, "Graphics profile"): what high and ultra change against MidGPU.
            Dictionary<string, object> high = Effective(GameOptionsProfile.Rewrite(json, "high", false).Json, "HighGPU");
            Assert.Equal("HIGH_END", high["addShaderDefine"]);
            Assert.Equal(false, high["lowLightMap"]);
            Assert.Equal(false, high["enableLowResMaps"]);
            Assert.Equal(true, high["enableShadowMap"]);
            Assert.Equal(true, high["enableBloom"]);
            Assert.Equal(true, high["dynamicRenderTargetSizeScale"]);
            Assert.Equal("Physics", high["excludedLoadingLayers"]);
            Dictionary<string, object> ultra = Effective(GameOptionsProfile.Rewrite(json, "ultra", false).Json, "High_Ultra");
            Assert.Equal("GOD_END", ultra["addShaderDefine"]);
            Assert.Equal(false, ultra["lowResAssets"]);
            Assert.Equal(false, ultra["lowCubeMap"]);
            Assert.Equal(true, ultra["reducedAiUpdateFrequency"]);
            Assert.Equal("-", Effective(GameOptionsProfile.Rewrite(json, "ultra", true).Json, "High_Ultra")["excludedLoadingLayers"]);
        }

        // ------------------------------------------------------------------ GraphicsHooks (patch 'graphics')

        [Fact]
        public void TheHooksRewriteRunTheFirstHookOnceAndLogTheEffectiveOptions()
        {
            var logger = new Logger();
            var sink = new MemoryLogSink(LogLevel.Debug, 100);
            logger.AddSink(sink);
            var boot = new BootTrace(logger.For("boot"));
            int firstHook = 0;
            GraphicsHooks.ResetForTests();
            GraphicsHooks.Configure("High", false, logger.For("game"), boot, () => firstHook++);
            Assert.Contains("has not read GameOptions_android.json", GraphicsHooks.Describe());

            string json = GraphicsHooks.GameOptionsJson(LookAlike);
            Assert.Equal(GameOptionsProfile.Rewrite(LookAlike, "high", false).Json, json);
            Assert.Equal(1, firstHook);
            Assert.Null(GraphicsHooks.GameOptionsJson(null));
            Assert.Equal(1, firstHook);

            var options = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["renderTargetSizeScale"] = 0.6f, ["addShaderDefine"] = "HIGH_END", ["enableShadowMap"] = true, ["ThreadPoolSize"] = 8,
            };
            GraphicsHooks.GameOptionsApplied(options);
            GraphicsHooks.GameOptionsApplied(new Dictionary<string, object>());   // later Worlds: not logged again
            GraphicsHooks.GameOptionsApplied(null);
            Assert.Equal(1, firstHook);
            string line = GraphicsHooks.Effective;
            output.WriteLine(line);
            Assert.StartsWith("graphics: effective GPU profile unknown, settings graphics.profile high (forced HighGPU)", line);
            Assert.EndsWith("4 options: ThreadPoolSize=8, addShaderDefine='HIGH_END', enableShadowMap=true, renderTargetSizeScale=0.6", line);
            List<string> lines = sink.Snapshot().Select(e => e.Message).ToList();
            Assert.Contains(lines, l => l.Contains("graphics: graphics.profile high: GameOptions GPU profile forced to HighGPU"));
            Assert.Contains(lines, l => l.Contains("graphics: effective GPU profile"));
            Assert.Contains("forced HighGPU", GraphicsHooks.Describe());
        }

        [Fact]
        public void TheHooksNeverThrowAndPassTheTextThroughWhenSomethingFails()
        {
            GraphicsHooks.ResetForTests();
            GraphicsHooks.Configure("ultra", true, null, null, () => throw new InvalidOperationException("mount failed"));
            Assert.Equal(GameOptionsProfile.Rewrite(LookAlike, "ultra", true).Json, GraphicsHooks.GameOptionsJson(LookAlike));
            Assert.Equal("not json", GraphicsHooks.GameOptionsJson("not json"));
            GraphicsHooks.GameOptionsApplied("not a dictionary");
            Assert.Contains("options unknown", GraphicsHooks.Effective);

            GraphicsHooks.ResetForTests();
            GraphicsHooks.Configure("auto", false, null, null, null);
            Assert.Same(LookAlike, GraphicsHooks.GameOptionsJson(LookAlike));
            Assert.Contains("graphics.profile auto (the game's own choice)", GraphicsHooks.Describe());
        }
    }
}
