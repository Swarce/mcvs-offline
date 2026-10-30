using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using McvsOffline.Embed.Tests.Embedded.SettingsMenu;
using McvsOffline.Embedded.Admin.SettingsMenu;
using McvsOffline.Embedded.Graphics;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// WP-D of docs/design/settings-menu.md (§3.4, skeptic E7): the Android PERFORMANCE page's back end. The start-time JSON rewrite
    /// on a look-alike and on the player's real GameOptions_android.json (T-D1, T-D2), the FPS index migration keyed on the resolved
    /// list (T-D3, E7's VeryLowGPU case), SCREEN SCALE over the profile's value and over the level loads' reset from
    /// <c>m_options</c> (T-D4), LOW RESOLUTION MAPS from every start state (T-D5), GRAPHICS and ANTIALIASING with the restart flag
    /// (T-D6), the wiring in GraphicsHooks, and the engine members on the player's own assemblies.
    /// </summary>
    [Collection("GameHooks")]
    public class PerformancePrefsTests : IDisposable
    {
        /// <summary>
        /// The real file's shape, trimmed: GPU defaults rows with renderTargetMSAA (a help text that names the option again) and the
        /// FRAME RATE LIMIT list, a block that sets its own MSAA, and VeryLowGPU's own list.
        /// </summary>
        internal const string LookAlike = @"{
    ""header"": {""file_version"": 5},
    ""profiles"": {
        ""CPU"": {""defaults"": [{""Default"": []}], ""selection"": [], ""overrides"": [], ""priority"": []},
        ""GPU"": {
            ""defaults"": [{""Default"": [
                [""renderTargetSizeScale"", 0.7, ""help"", ""[;]""],
                [""renderTargetMSAA"", 0, ""Multisample anti-aliasing; \""renderTargetMSAA\"", 2, 4"", ""[0;16]""],
                [""excludedLoadingLayers"", ""Physics VFX"", ""help"", ""[;]""],
                [""maxFramerateValues"", ""30 60 -1"", ""List of max fps limit"", ""[;]""],
                [""reducedAiUpdateFrequency"", true, ""help"", ""[;]""]
            ]}],
            ""selection"": [{""HighGPU"": [[[""GPU$$Adreno""], [""GPU$$630""]]]}, {""VeryLowGPU"": [[[""GPU$$Mali""]]]}, {""MidGPU"": [[[""CORES>1""]]]}],
            ""overrides"": {
                ""High_Ultra"": {""addShaderDefine"": ""GOD_END"", ""renderTargetMSAA"": 2, ""excludedLoadingLayers"": ""-"", ""reducedAiUpdateFrequency"": false},
                ""HighGPU"": {""addShaderDefine"": ""HIGH_END"", ""excludedLoadingLayers"": ""-"", ""reducedAiUpdateFrequency"": false},
                ""MidGPU"": {""addShaderDefine"": ""MID_END"", ""renderTargetSizeScale"": 0.6, ""excludedLoadingLayers"": ""Physics""},
                ""VeryLowGPU"": {""renderTargetSizeScale"": 0.5, ""maxFramerateValues"": ""23 60 -1""}
            },
            ""priority"": {""High_Ultra"": ["""", ""HighGPU""], ""HighGPU"": [""High_Ultra"", ""MidGPU""], ""MidGPU"": [""HighGPU"", ""VeryLowGPU""], ""VeryLowGPU"": [""MidGPU"", """"]}
        }
    }
}";

        private readonly ITestOutputHelper output;

        public PerformancePrefsTests(ITestOutputHelper output)
        {
            this.output = output;
            GraphicsHooks.ResetForTests();
        }

        public void Dispose()
        {
            GraphicsHooks.ResetForTests();
        }

        // ------------------------------------------------------------------ helpers

        private static Dictionary<string, object> Gpu(string json) =>
            (Dictionary<string, object>)((Dictionary<string, object>)MiniJson.ParseObject(json)["profiles"])["GPU"];

        private static List<string> Blocks(string json) => ((Dictionary<string, object>)Gpu(json)["overrides"]).Keys.ToList();

        /// <summary>GPU defaults overlaid with one block: what the engine resolves for that profile.</summary>
        private static Dictionary<string, object> Effective(string json, string profile)
        {
            Dictionary<string, object> gpu = Gpu(json);
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (object row in (List<object>)((Dictionary<string, object>)((List<object>)gpu["defaults"])[0])["Default"])
            {
                var r = (List<object>)row;
                result[(string)r[0]] = r[1];
            }
            var block = (Dictionary<string, object>)((Dictionary<string, object>)gpu["overrides"])[profile];
            foreach (var kv in block) result[kv.Key] = kv.Value;
            return result;
        }

        /// <summary>The blocks the engine can land on for a profile choice: all of them for auto, the forced one otherwise.</summary>
        private static IEnumerable<string> Resolvable(string json, string choice) =>
            choice == "auto" ? Blocks(json) : new[] { GameOptionsProfile.GpuProfileOf(choice) };

        /// <summary>The parsed tree with the named option's values taken out (everything else must be untouched).</summary>
        private static object Without(string json, string option)
        {
            Dictionary<string, object> root = MiniJson.ParseObject(json);
            Dictionary<string, object> gpu = (Dictionary<string, object>)((Dictionary<string, object>)root["profiles"])["GPU"];
            var rows = (List<object>)((Dictionary<string, object>)((List<object>)gpu["defaults"])[0])["Default"];
            rows.RemoveAll(r => ((List<object>)r)[0] as string == option);
            foreach (object block in ((Dictionary<string, object>)gpu["overrides"]).Values) ((Dictionary<string, object>)block).Remove(option);
            return root;
        }

        private string RealGameOptions()
        {
            string decoded = Environment.GetEnvironmentVariable("MCVS_DATAPACK_DECODED");
            if (!string.IsNullOrEmpty(decoded) && File.Exists(Path.Combine(decoded, "GameOptions_android.json")))
                return File.ReadAllText(Path.Combine(decoded, "GameOptions_android.json"));
            string data = Environment.GetEnvironmentVariable("MCVS_DATAPACK");
            if (!string.IsNullOrEmpty(data) && new DataPack(data).TryReadText("GameOptions_android.json", out string text)) return text;
            Assert.False(Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1" && !string.IsNullOrEmpty(decoded),
                         "MCVS_DATAPACK_DECODED is set but has no GameOptions_android.json");
            output.WriteLine("GameOptions_android.json not available (MCVS_DATAPACK_DECODED, MCVS_DATAPACK): skipped");
            return null;
        }

        private IEnumerable<string> Files()
        {
            yield return LookAlike;
            string real = RealGameOptions();
            if (real != null) yield return real;
        }

        // ------------------------------------------------------------------ T-D1: graphics.msaa in the JSON

        [Fact]
        public void MsaaAutoLeavesTheTextByteIdentical()
        {
            foreach (string json in Files())
            {
                Assert.Same(json, PerformancePrefs.RewriteJson(json, "auto", false, out string summary));
                Assert.Contains("graphics.msaa auto", summary);
                Assert.Same(json, PerformancePrefs.RewriteJson(json, "AUTO", false, out summary));
                Assert.Same(json, PerformancePrefs.RewriteJson(json, "16x", false, out summary));   // not a choice: as auto
                string forced = GameOptionsProfile.Rewrite(json, "high", false).Json;
                Assert.Same(forced, PerformancePrefs.RewriteJson(forced, "auto", false, out summary));
            }
            Assert.Null(PerformancePrefs.RewriteJson(null, "4x", true, out string none));
            Assert.Same("{ nope", PerformancePrefs.RewriteJson("{ nope", "4x", true, out none));
            Assert.Contains("does not parse", none);
        }

        /// <summary>Catches an MSAA written only into the defaults row (a block with its own value would win) or only into one block.</summary>
        [Fact]
        public void AnMsaaChoiceReachesTheBlockTheEngineResolvesForEveryProfile()
        {
            foreach (string json in Files())
            {
                foreach (string profile in GameOptionsProfile.Choices)
                {
                    string text = profile == "auto" ? json : GameOptionsProfile.Rewrite(json, profile, false).Json;
                    foreach (var choice in new[] { Tuple.Create("off", 0), Tuple.Create("2x", 2), Tuple.Create("4x", 4), Tuple.Create("8x", 8) })
                    {
                        string rewritten = PerformancePrefs.RewriteJson(text, choice.Item1, false, out string summary);
                        Assert.True(MiniJson.TryParse(rewritten, out object _));
                        foreach (string block in Resolvable(text, profile))
                            Assert.Equal(choice.Item2, Convert.ToInt32(Effective(rewritten, block)["renderTargetMSAA"], CultureInfo.InvariantCulture));
                        // Nothing else moved, the help text that names the option included.
                        Assert.True(JsonValue.DeepEquals(Without(text, "renderTargetMSAA"), Without(rewritten, "renderTargetMSAA")), profile + " " + choice.Item1);
                        Assert.Contains("renderTargetMSAA " + choice.Item2, summary);
                    }
                }
            }
            // The look-alike's High_Ultra sets its own 2: replaced too.
            string ultra = PerformancePrefs.RewriteJson(LookAlike, "8x", false, out string s);
            Assert.Contains("2 places", s);
            Assert.Equal(8, Convert.ToInt32(Effective(ultra, "High_Ultra")["renderTargetMSAA"], CultureInfo.InvariantCulture));
            Assert.Contains("\\\"renderTargetMSAA\\\", 2, 4", ultra);
        }

        // ------------------------------------------------------------------ T-D2: the FRAME RATE LIMIT list

        [Fact]
        public void TheListRewriteTouchesEveryStockListAndLeavesVeryLowGpuAlone()
        {
            foreach (string json in Files())
            {
                string rewritten = PerformancePrefs.RewriteJson(json, "auto", true, out string summary);
                output.WriteLine(summary);
                Assert.NotSame(json, rewritten);
                Assert.DoesNotContain("\"30 60 -1\"", rewritten);
                Assert.Equal(json.Split("\"23 60 -1\"").Length, rewritten.Split("\"23 60 -1\"").Length);
                Assert.Equal(json.Split("\"30 60 -1\"").Length, rewritten.Split("\"30 60 90 120 -1\"").Length);
                foreach (string block in Blocks(json))
                {
                    object before = Effective(json, block)["maxFramerateValues"];
                    object after = Effective(rewritten, block)["maxFramerateValues"];
                    Assert.Equal((string)before == "30 60 -1" ? "30 60 90 120 -1" : before, after);
                }
                Assert.Equal("23 60 -1", Effective(rewritten, "VeryLowGPU")["maxFramerateValues"]);
                Assert.Equal("30 60 90 120 -1", Effective(rewritten, "MidGPU")["maxFramerateValues"]);
                Assert.True(JsonValue.DeepEquals(Without(json, "maxFramerateValues"), Without(rewritten, "maxFramerateValues")));

                // Both at once, on a forced profile.
                string both = PerformancePrefs.RewriteJson(GameOptionsProfile.Rewrite(json, "ultra", false).Json, "4x", true, out summary);
                Assert.Equal(4, Convert.ToInt32(Effective(both, "High_Ultra")["renderTargetMSAA"], CultureInfo.InvariantCulture));
                Assert.Equal("30 60 90 120 -1", Effective(both, "High_Ultra")["maxFramerateValues"]);
            }
        }

        [Fact]
        public void ThePcTextIsNeverRewritten()
        {
            GraphicsHooks.ConfigureWindows(true);
            Assert.Same(LookAlike, PerformancePrefs.RewriteJson(LookAlike, "4x", true, out string summary));
            Assert.Contains("PC", summary);
        }

        // ------------------------------------------------------------------ T-D3: the FPS index migration

        private static readonly List<int> Stock = PerformancePrefs.ParseFpsList(PerformancePrefs.StockFpsList);
        private static readonly List<int> Extended = PerformancePrefs.ParseFpsList(PerformancePrefs.ExtendedFpsList);
        private static readonly List<int> VeryLow = PerformancePrefs.ParseFpsList(PerformancePrefs.VeryLowFpsList);

        [Fact]
        public void EveryStoredIndexMapsToTheSameFrameRateOrTheNearestLowerOne()
        {
            Assert.Equal(new[] { 30, 60, 90, 120, -1 }, Extended);
            Assert.Equal(new[] { 0, 1, 4 }, Enumerable.Range(0, 3).Select(i => PerformancePrefs.MigrateFpsIndex(i, Stock, Extended)));
            Assert.Equal(new[] { 0, 1, 1, 1, 2 }, Enumerable.Range(0, 5).Select(i => PerformancePrefs.MigrateFpsIndex(i, Extended, Stock)));
            Assert.Equal(new[] { 0, 1, 1, 1, 2 }, Enumerable.Range(0, 5).Select(i => PerformancePrefs.MigrateFpsIndex(i, Extended, VeryLow)));
            Assert.Equal(new[] { 0, 1, 2 }, Enumerable.Range(0, 3).Select(i => PerformancePrefs.MigrateFpsIndex(i, Stock, VeryLow)));
            Assert.Equal(1, PerformancePrefs.MigrateFpsIndex(2, Stock, new List<int> { 30, 60 }));   // no unlimited: the highest limit
            Assert.Equal(0, PerformancePrefs.MigrateFpsIndex(0, Stock, new List<int> { 60, -1 }));   // nothing lower than 30: 0
            Assert.Equal(0, PerformancePrefs.MigrateFpsIndex(3, Stock, Extended));
            Assert.Equal(0, PerformancePrefs.MigrateFpsIndex(-1, Stock, Extended));

            // The rule itself, over every pair of lists: the same rate, else the highest lower one; unlimited stays unlimited.
            var lists = new[] { Stock, Extended, VeryLow };
            foreach (List<int> from in lists)
            {
                foreach (List<int> to in lists)
                {
                    for (int i = 0; i < from.Count; i++)
                    {
                        int fps = from[i];
                        int got = to[PerformancePrefs.MigrateFpsIndex(i, from, to)];
                        if (fps == -1) Assert.Equal(-1, got);
                        else if (to.Contains(fps)) Assert.Equal(fps, got);
                        else
                        {
                            List<int> lower = to.Where(v => v > 0 && v < fps).ToList();
                            Assert.Equal(lower.Count > 0 ? lower.Max() : to[0], got);
                        }
                    }
                }
            }
        }

        [Fact]
        public void TheMigrationRunsOnceAndMapsBackWhenTheListIsTheStockOneAgain()
        {
            var engine = new FakeEngineGraphics { Fps = Extended.ToList(), MaxIndex = 2 };
            var settings = new FakePerformanceSettings();
            string line = PerformancePrefs.MigrateFps(engine, settings);
            output.WriteLine(line);
            Assert.Equal(4, engine.MaxIndex);                 // the old UNLIMITED is UNLIMITED again, not 90
            Assert.Equal(1L, settings.FpsListVersion);
            Assert.Contains("index 2 -> 4", line);
            Assert.Equal(new[] { "graphics.fpsListVersion=1" }, settings.Saves);

            // A second run (the next start) changes nothing.
            Assert.Contains("kept", PerformancePrefs.MigrateFps(engine, settings));
            Assert.Equal(1, engine.IndexWrites);
            Assert.Single(settings.Saves);

            // The feature off: the stock list again; 120 → 60, the marker back to 0.
            engine.Fps = Stock.ToList();
            engine.MaxIndex = 3;
            PerformancePrefs.MigrateFps(engine, settings);
            Assert.Equal(1, engine.MaxIndex);
            Assert.Equal(0L, settings.FpsListVersion);
            Assert.Contains("kept", PerformancePrefs.MigrateFps(engine, settings));

            // UNLIMITED back to the stock UNLIMITED.
            engine.Fps = Extended.ToList();
            engine.MaxIndex = 2;
            PerformancePrefs.MigrateFps(engine, settings);
            engine.Fps = Stock.ToList();
            PerformancePrefs.MigrateFps(engine, settings);
            Assert.Equal(2, engine.MaxIndex);
            Assert.Equal(0L, settings.FpsListVersion);
        }

        /// <summary>Skeptic E7: keyed on the resolved list. Catches a migration that runs on the switch alone (VeryLowGPU keeps "23 60 -1").</summary>
        [Fact]
        public void VeryLowGpuKeepsItsIndexWithTheFeatureOn()
        {
            var engine = new FakeEngineGraphics { Fps = VeryLow.ToList(), MaxIndex = 2 };
            var settings = new FakePerformanceSettings();
            Assert.Contains("kept", PerformancePrefs.MigrateFps(engine, settings));
            Assert.Equal(2, engine.MaxIndex);
            Assert.Equal(0, engine.IndexWrites);
            Assert.Empty(settings.Saves);

            // A marker from another profile (an extended list at the last start): mapped onto VeryLowGPU's list.
            settings.Store(SettingsSchema.GraphicsFpsListVersionKey, 1L);
            engine.MaxIndex = 4;
            PerformancePrefs.MigrateFps(engine, settings);
            Assert.Equal(2, engine.MaxIndex);
            Assert.Equal(0L, settings.FpsListVersion);
        }

        /// <summary>
        /// Review: the marker lives in settings.json, the index in LocalStorage, and the marker is saved later. Catches a second
        /// mapping after the marker was lost: 120 and UNLIMITED are past the stock list's end and became 30.
        /// </summary>
        [Fact]
        public void ALostMarkerKeepsAnIndexThatIsAlreadyOnTheLongerList()
        {
            foreach (int stored in new[] { 3, 4 })
            {
                var engine = new FakeEngineGraphics { Fps = Extended.ToList(), MaxIndex = stored };
                var settings = new FakePerformanceSettings();
                string line = PerformancePrefs.MigrateFps(engine, settings);
                output.WriteLine(line);
                Assert.Equal(stored, engine.MaxIndex);
                Assert.Equal(0, engine.IndexWrites);
                Assert.Equal(1L, settings.FpsListVersion);
                Assert.Equal(new[] { "graphics.fpsListVersion=1" }, settings.Saves);
                Assert.Contains("kept", line);
                Assert.Contains("kept", PerformancePrefs.MigrateFps(engine, settings));
                Assert.Single(settings.Saves);
            }

            // A first run and the same start's marker then lost: the second run leaves the migrated index alone.
            var again = new FakeEngineGraphics { Fps = Extended.ToList(), MaxIndex = 2 };
            var first = new FakePerformanceSettings();
            PerformancePrefs.MigrateFps(again, first);
            Assert.Equal(4, again.MaxIndex);
            PerformancePrefs.MigrateFps(again, new FakePerformanceSettings());
            Assert.Equal(4, again.MaxIndex);
            Assert.Equal(1, again.IndexWrites);

            // Out of range on both lists: mapped as before (to 30).
            var garbage = new FakeEngineGraphics { Fps = Extended.ToList(), MaxIndex = 7 };
            PerformancePrefs.MigrateFps(garbage, new FakePerformanceSettings());
            Assert.Equal(0, garbage.MaxIndex);
        }

        [Fact]
        public void TheMigrationWaitsForTheIndexAndKeepsTheMarkerWhenTheWriteFails()
        {
            var settings = new FakePerformanceSettings();
            var engine = new FakeEngineGraphics { Fps = Extended.ToList(), MaxIndex = null };
            Assert.Null(PerformancePrefs.MigrateFps(engine, settings));   // preferences not loaded yet: try again later
            engine.Fps = null;
            engine.MaxIndex = 2;
            Assert.Null(PerformancePrefs.MigrateFps(engine, settings));
            Assert.Empty(settings.Saves);

            engine.Fps = Extended.ToList();
            engine.CanWriteIndex = false;
            Assert.Contains("could not be written", PerformancePrefs.MigrateFps(engine, settings));
            Assert.Equal(0L, settings.FpsListVersion);
            Assert.Empty(settings.Saves);

            // ApplyAfterGameOptions runs it too (the contract's entry point).
            engine.CanWriteIndex = true;
            PerformancePrefs.ApplyAfterGameOptions(engine, settings, null);
            Assert.Equal(4, engine.MaxIndex);
            Assert.Equal(1L, settings.FpsListVersion);
        }

        // ------------------------------------------------------------------ T-D4: SCREEN SCALE

        [Fact]
        public void TheScaleFormulaIsSteamsAndTheSliderShowsItBack()
        {
            Assert.Null(PerformancePrefs.ScaleOf("auto"));
            Assert.Null(PerformancePrefs.ScaleOf("101"));
            Assert.Null(PerformancePrefs.ScaleOf("-1"));
            Assert.Null(PerformancePrefs.ScaleOf(""));
            Assert.Null(PerformancePrefs.ScaleOf(null));
            Assert.Equal(0.4f, PerformancePrefs.ScaleOf("0").Value, 5);
            Assert.Equal(0.7f, PerformancePrefs.ScaleOf("50").Value, 5);
            Assert.Equal(0.982f, PerformancePrefs.ScaleOf("97").Value, 5);
            Assert.Equal(1f, PerformancePrefs.ScaleOf("98").Value);
            Assert.Equal(1f, PerformancePrefs.ScaleOf("100").Value);
            for (int v = 0; v <= 97; v++) Assert.Equal(v, PerformancePrefs.SliderOf(PerformancePrefs.ScaleOf(v.ToString(CultureInfo.InvariantCulture)).Value));
            Assert.Equal(100, PerformancePrefs.SliderOf(1f));
            Assert.Equal(33, PerformancePrefs.SliderOf(0.6f));
            Assert.Equal(0, PerformancePrefs.SliderOf(0.25f));
            Assert.Equal(100, PerformancePrefs.SliderOf(1.5f));
        }

        /// <summary>The profile's options as ChangeGraphicQuality reads them at every level load (MidGPU on the S22, HighGPU's dynamic scale).</summary>
        private static Dictionary<string, object> ProfileOptions(bool dynamic) => new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["renderTargetSizeScale"] = 0.6f, ["renderTargetSizeScaleMM"] = 1f, ["dynamicRenderTargetSizeScale"] = dynamic, ["enableBloom"] = true,
        };

        /// <summary>AND-CS GameApplication.ChangeGraphicQuality(map): what a level load does to the scale.</summary>
        private static void LoadLevel(FakeEngineGraphics engine, IDictionary options, bool menu)
        {
            if (menu)
            {
                if (options.Contains("renderTargetSizeScaleMM")) engine.Scale = Convert.ToSingle(options["renderTargetSizeScaleMM"], CultureInfo.InvariantCulture);
                return;
            }
            if (options.Contains("renderTargetSizeScale")) engine.Scale = Convert.ToSingle(options["renderTargetSizeScale"], CultureInfo.InvariantCulture);
            if (options.Contains("dynamicRenderTargetSizeScale") && (bool)options["dynamicRenderTargetSizeScale"]) engine.Scale = 0.83f;
        }

        /// <summary>Catches a scale written once at start: the next level load (menu or match) put the profile's value back.</summary>
        [Fact]
        public void OurScaleWinsOverTheProfileAndOverEveryLevelLoad()
        {
            foreach (var c in new[] { Tuple.Create("50", 0.7f), Tuple.Create("100", 1f), Tuple.Create("0", 0.4f) })
            {
                foreach (bool dynamic in new[] { false, true })
                {
                    PerformancePrefs.ResetForTests();
                    var engine = new FakeEngineGraphics { Scale = 0.6f };
                    var settings = new FakePerformanceSettings();
                    settings.Store(SettingsSchema.GraphicsRenderScaleKey, c.Item1);
                    Dictionary<string, object> options = ProfileOptions(dynamic);
                    string line = PerformancePrefs.Apply(engine, settings, options, false);
                    output.WriteLine(line);
                    Assert.Equal(c.Item2, engine.Scale.Value, 5);
                    Assert.Equal(c.Item2, engine.ScaleOld.Value, 5);
                    LoadLevel(engine, options, true);
                    Assert.Equal(c.Item2, engine.Scale.Value, 5);
                    LoadLevel(engine, options, false);
                    Assert.Equal(c.Item2, engine.Scale.Value, 5);
                    Assert.Equal(true, options["enableBloom"]);
                }
            }

            // auto: the profile's own values, untouched.
            PerformancePrefs.ResetForTests();
            var auto = new FakeEngineGraphics { Scale = 0.6f };
            Dictionary<string, object> stock = ProfileOptions(true);
            PerformancePrefs.Apply(auto, new FakePerformanceSettings(), stock, false);
            Assert.Equal(0.6f, auto.Scale);
            Assert.Null(auto.ScaleOld);
            Assert.Equal(ProfileOptions(true).OrderBy(k => k.Key, StringComparer.Ordinal), stock.OrderBy(k => k.Key, StringComparer.Ordinal));
        }

        [Fact]
        public void ALiveScaleChangeAppliesAtOnceAndAutoGivesTheProfileItsOptionsBack()
        {
            var engine = new FakeEngineGraphics { Scale = 0.6f };
            var settings = new FakePerformanceSettings();
            Dictionary<string, object> options = ProfileOptions(true);
            PerformancePrefs.Apply(engine, settings, options, false);

            Assert.True(PerformancePrefs.SetRenderScale(engine, settings, "30"));
            Assert.Equal(0.58f, engine.Scale.Value, 5);
            Assert.Equal("30", settings.RenderScale);
            LoadLevel(engine, options, false);
            Assert.Equal(0.58f, engine.Scale.Value, 5);

            Assert.True(PerformancePrefs.SetRenderScale(engine, settings, "auto"));
            Assert.Equal("auto", settings.RenderScale);
            LoadLevel(engine, options, true);
            Assert.Equal(1f, engine.Scale);
            LoadLevel(engine, options, false);
            Assert.Equal(0.83f, engine.Scale);   // the profile's dynamic scale again

            Assert.False(PerformancePrefs.SetRenderScale(engine, settings, "150"));
            Assert.False(PerformancePrefs.SetRenderScale(engine, settings, "0.5"));
            Assert.Equal(new[] { "graphics.renderScale=30", "graphics.renderScale=auto" }, settings.Saves);
            Assert.True(PerformancePrefs.SetRenderScale(engine, settings, "auto"));   // the same value: no second write
            Assert.Equal(2, settings.Saves.Count);
        }

        /// <summary>
        /// Review: SCREEN SCALE at auto shows the match scale. Catches the engine's current scale as the display: on the main menu
        /// (the only place the page exists) that is renderTargetSizeScaleMM, 1.0, so MidGPU's 0.6 showed as 100.
        /// </summary>
        [Fact]
        public void AutoScreenScaleShowsTheScaleAMatchRendersAtNotTheMenusOne()
        {
            var engine = new FakeEngineGraphics { Scale = 0.6f };
            Assert.Equal(33, PerformancePrefs.AutoRenderScaleSlider(engine));   // no options yet: the engine's own
            Assert.Null(PerformancePrefs.AutoMatchScale);

            Dictionary<string, object> options = ProfileOptions(false);
            PerformancePrefs.Apply(engine, new FakePerformanceSettings(), options, false);
            LoadLevel(engine, options, true);
            Assert.Equal(1f, engine.Scale);
            Assert.Equal(0.6f, PerformancePrefs.AutoMatchScale.Value, 5);
            Assert.Equal(33, PerformancePrefs.AutoRenderScaleSlider(engine));

            // An explicit scale and back to auto: the profile's own values, not ours.
            var settings = new FakePerformanceSettings();
            Assert.True(PerformancePrefs.SetRenderScale(engine, settings, "80"));
            Assert.True(PerformancePrefs.SetRenderScale(engine, settings, "auto"));
            LoadLevel(engine, options, true);
            Assert.Equal(33, PerformancePrefs.AutoRenderScaleSlider(engine));

            // A dynamic-scale profile: the engine's dynamic value, applied last at a match load.
            PerformancePrefs.ResetForTests();
            PerformancePrefs.DynamicMatchScale = () => 0.83f;
            Dictionary<string, object> dynamic = ProfileOptions(true);
            PerformancePrefs.Apply(engine, new FakePerformanceSettings(), dynamic, false);
            Assert.Equal(72, PerformancePrefs.AutoRenderScaleSlider(engine));
            PerformancePrefs.DynamicMatchScale = () => null;   // unreadable: the static entry
            Assert.Equal(33, PerformancePrefs.AutoRenderScaleSlider(engine));

            // No match entry: a match load keeps the menu's.
            PerformancePrefs.ResetForTests();
            PerformancePrefs.Apply(engine, new FakePerformanceSettings(), new Dictionary<string, object> { ["renderTargetSizeScaleMM"] = 0.7 }, false);
            Assert.Equal(50, PerformancePrefs.AutoRenderScaleSlider(engine));
        }

        [Fact]
        public void TheDynamicScaleIsTheEnginesFormula()
        {
            // 1080x2340 at 320 dpi on a 2.54" x 5.5" screen: (319.5 * 2) / (425.2 + 425.5) = 0.7512.
            Assert.Equal(0.7512f, PerformancePrefs.DynamicScaleOf(320, 1080, 2340, 2.54f, 5.5f).Value, 3);
            Assert.Equal(1f, PerformancePrefs.DynamicScaleOf(640, 1080, 2340, 2.54f, 5.5f).Value);       // capped at 1
            Assert.Equal(0.75f, PerformancePrefs.DynamicScaleOf(640, 2560, 1600, 6f, 3.75f).Value, 5);   // capped at 1920 px wide
            Assert.Null(PerformancePrefs.DynamicScaleOf(0, 1080, 2340, 2.54f, 5.5f));
            Assert.Null(PerformancePrefs.DynamicScaleOf(320, 1080, 2340, 0f, 0f));
        }

        // ------------------------------------------------------------------ T-D5: LOW RESOLUTION MAPS

        public static TheoryData<string[]> LayerStarts() => new TheoryData<string[]>
        {
            new[] { "Physics", "LowEndOnly" }, new[] { "Physics", "HighEndOnly" }, new string[0], new[] { "LowEndOnly", "Physics", "LowEndOnly", "HighEndOnly" },
        };

        /// <summary>Catches Steam's swap-only-when-present (no entry at all would stay without one) and duplicates after a second OnCreate.</summary>
        [Theory]
        [MemberData(nameof(LayerStarts))]
        public void LowResolutionMapsSetTheLayersFromEveryStartState(string[] start)
        {
            foreach (string choice in new[] { "on", "off" })
            {
                var engine = new FakeEngineGraphics { Layers = start.ToList(), LowRes = choice == "off" };
                Assert.True(PerformancePrefs.ApplyLowResMaps(engine, choice));
                bool on = choice == "on";
                Assert.Equal(on, engine.LowRes);
                Assert.Equal(1, engine.Layers.Count(l => l == (on ? "HighEndOnly" : "LowEndOnly")));
                Assert.DoesNotContain(on ? "LowEndOnly" : "HighEndOnly", engine.Layers);
                Assert.Equal(start.Contains("Physics"), engine.Layers.Contains("Physics"));
            }
            var unchanged = new FakeEngineGraphics { Layers = start.ToList(), LowRes = true };
            Assert.True(PerformancePrefs.ApplyLowResMaps(unchanged, "auto"));
            Assert.Equal(start, unchanged.Layers);
            Assert.Equal(true, unchanged.LowRes);
        }

        [Fact]
        public void LowResolutionMapsAtStartAndLive()
        {
            var engine = new FakeEngineGraphics { Layers = new List<string> { "Physics", "LowEndOnly" }, LowRes = false };
            var settings = new FakePerformanceSettings();
            settings.Store(SettingsSchema.GraphicsLowResMapsKey, "on");
            PerformancePrefs.Apply(engine, settings, null, false);
            Assert.Equal(new[] { "Physics", "HighEndOnly" }, engine.Layers);
            Assert.Equal(true, engine.LowRes);

            Assert.True(PerformancePrefs.SetLowResMaps(engine, settings, "off"));
            Assert.Equal(new[] { "Physics", "LowEndOnly" }, engine.Layers);
            Assert.Equal(false, engine.LowRes);
            Assert.Equal("off", settings.LowResMaps);
            Assert.False(PerformancePrefs.SetLowResMaps(engine, settings, "maybe"));
            Assert.True(PerformancePrefs.SetLowResMaps(engine, settings, "off"));
            Assert.Equal(new[] { "graphics.lowResMaps=off" }, settings.Saves);

            var missing = new FakeEngineGraphics { Layers = null };
            Assert.False(PerformancePrefs.ApplyLowResMaps(missing, "on"));
        }

        // ------------------------------------------------------------------ T-D6: GRAPHICS and ANTIALIASING

        [Fact]
        public void GraphicsAtItsCurrentValueRaisesNothingAndANewOneRestarts()
        {
            int resets = 0;
            PerformancePrefs.SelectedProfileReset = () => { resets++; return true; };
            var settings = new FakePerformanceSettings();
            var state = new SettingsMenuState();

            Assert.True(PerformancePrefs.SetGraphicsProfile(settings, state, "auto"));
            Assert.True(PerformancePrefs.SetGraphicsProfile(settings, state, "AUTO"));
            Assert.False(state.RestartPending);
            Assert.Empty(settings.Saves);
            Assert.Equal(0, resets);

            Assert.True(PerformancePrefs.SetGraphicsProfile(settings, state, "high"));
            Assert.True(state.RestartPending);
            Assert.Equal("high", settings.GraphicsProfile);
            Assert.Equal(new[] { "graphics.profile=high" }, settings.Saves);
            Assert.Equal(1, resets);

            state.RestartPending = false;
            Assert.True(PerformancePrefs.SetGraphicsProfile(settings, state, "high"));
            Assert.False(state.RestartPending);
            Assert.False(PerformancePrefs.SetGraphicsProfile(settings, state, "low"));
            Assert.False(state.RestartPending);
            Assert.Equal(1, resets);
        }

        [Fact]
        public void AntialiasingAtItsCurrentValueRaisesNothingAndANewOneRestarts()
        {
            var settings = new FakePerformanceSettings();
            var state = new SettingsMenuState();
            Assert.True(PerformancePrefs.SetMsaa(settings, state, "auto"));
            Assert.False(state.RestartPending);
            Assert.Empty(settings.Saves);
            Assert.True(PerformancePrefs.SetMsaa(settings, state, "8x"));
            Assert.True(state.RestartPending);
            Assert.Equal("8x", settings.Msaa);
            state.RestartPending = false;
            Assert.True(PerformancePrefs.SetMsaa(settings, state, "8x"));
            Assert.False(state.RestartPending);
            Assert.False(PerformancePrefs.SetMsaa(settings, state, "16x"));
            Assert.Equal(new[] { "graphics.msaa=8x" }, settings.Saves);
        }

        [Fact]
        public void ThePcHostChangesNothing()
        {
            GraphicsHooks.ConfigureWindows(true);
            var engine = new FakeEngineGraphics { Scale = 0.6f, Layers = new List<string> { "LowEndOnly" }, Fps = Extended.ToList(), MaxIndex = 2 };
            var settings = new FakePerformanceSettings();
            settings.Store(SettingsSchema.GraphicsRenderScaleKey, "50");
            settings.Store(SettingsSchema.GraphicsLowResMapsKey, "on");
            PerformancePrefs.ApplyAfterGameOptions(engine, settings, null);
            Assert.Equal(0.6f, engine.Scale);
            Assert.Equal(new[] { "LowEndOnly" }, engine.Layers);
            Assert.Equal(2, engine.MaxIndex);
            var state = new SettingsMenuState();
            Assert.False(PerformancePrefs.SetMsaa(settings, state, "4x"));
            Assert.False(PerformancePrefs.SetGraphicsProfile(settings, state, "high"));
            Assert.False(PerformancePrefs.SetRenderScale(engine, settings, "20"));
            Assert.False(PerformancePrefs.SetLowResMaps(engine, settings, "off"));
            Assert.False(state.RestartPending);
            Assert.Empty(settings.Saves);
        }

        // ------------------------------------------------------------------ GraphicsHooks: the wiring

        private static Settings HostSettings(string msaa, bool? performance, string renderScale = null)
        {
            var settings = new Settings();
            if (msaa != null) settings.Set(SettingsSchema.GraphicsMsaaKey, msaa);
            if (performance != null) settings.Set(SettingsSchema.SettingsMenuPerformanceKey, performance.Value);
            if (renderScale != null) settings.Set(SettingsSchema.GraphicsRenderScaleKey, renderScale);
            return settings;
        }

        [Fact]
        public void TheHooksRewriteFromTheHostsSettingsAndLogIt()
        {
            var logger = new Logger();
            var sink = new MemoryLogSink(LogLevel.Debug, 100);
            logger.AddSink(sink);
            GraphicsHooks.Configure("mid", false, logger.For("game"), null);
            PerformancePrefs.Configure(HostSettings("4x", null, "50"));   // the switch absent: its default, on

            string json = GraphicsHooks.GameOptionsJson(LookAlike);
            Assert.Equal(4, Convert.ToInt32(Effective(json, "MidGPU")["renderTargetMSAA"], CultureInfo.InvariantCulture));
            Assert.Equal("30 60 90 120 -1", Effective(json, "MidGPU")["maxFramerateValues"]);
            Assert.Equal(PerformancePrefs.RewriteJson(GameOptionsProfile.Rewrite(LookAlike, "mid", false).Json, "4x", true, out string _), json);
            List<string> lines = sink.Snapshot().Select(e => e.Message).ToList();
            Assert.Contains(lines, l => l.StartsWith("graphics: graphics.profile mid", StringComparison.Ordinal) && l.Contains("renderTargetMSAA 4")
                                        && l.Contains("frame rate limits '30 60 90 120 -1'"));

            // After every ApplyGameOptions: our scale into m_options (each OnCreate builds a new one).
            Dictionary<string, object> first = ProfileOptions(true);
            GraphicsHooks.GameOptionsApplied(first);
            Assert.Equal(0.7f, (float)first["renderTargetSizeScale"], 5);
            Assert.Equal(0.7f, (float)first["renderTargetSizeScaleMM"], 5);
            Assert.Equal(false, first["dynamicRenderTargetSizeScale"]);
            Dictionary<string, object> second = ProfileOptions(false);
            GraphicsHooks.GameOptionsApplied(second);
            Assert.Equal(0.7f, (float)second["renderTargetSizeScale"], 5);
            lines = sink.Snapshot().Select(e => e.Message).ToList();
            Assert.Single(lines, l => l.StartsWith("graphics: performance page: screen scale 50 -> 0.7", StringComparison.Ordinal));
            Assert.Contains("performance page: screen scale 50", GraphicsHooks.Describe());
            // The effective-options line still shows what the profile resolved (logged before ours).
            Assert.Contains("renderTargetSizeScale=0.6", GraphicsHooks.Effective);
        }

        [Fact]
        public void TheSwitchOffKeepsTheGamesListAndNoHostSettingsKeepTheText()
        {
            GraphicsHooks.Configure("auto", false, null, null);
            PerformancePrefs.Configure(HostSettings("auto", false));
            Assert.Same(LookAlike, GraphicsHooks.GameOptionsJson(LookAlike));

            GraphicsHooks.ResetForTests();
            GraphicsHooks.Configure("auto", false, null, null);
            PerformancePrefs.Configure(HostSettings("2x", false));
            string json = GraphicsHooks.GameOptionsJson(LookAlike);
            Assert.Contains("\"30 60 -1\"", json);
            Assert.Equal(2, Convert.ToInt32(Effective(json, "MidGPU")["renderTargetMSAA"], CultureInfo.InvariantCulture));

            // Before Configure (the tests of the profile, a host without the call): nothing of ours, m_options untouched.
            GraphicsHooks.ResetForTests();
            GraphicsHooks.Configure("auto", false, null, null);
            Assert.Same(LookAlike, GraphicsHooks.GameOptionsJson(LookAlike));
            Dictionary<string, object> options = ProfileOptions(true);
            GraphicsHooks.GameOptionsApplied(options);
            Assert.Equal(0.6f, options["renderTargetSizeScale"]);
            Assert.Equal(true, options["dynamicRenderTargetSizeScale"]);
            Assert.Null(PerformancePrefs.Summary);

            // PC: the host's values are never applied.
            GraphicsHooks.ResetForTests();
            GraphicsHooks.Configure("auto", false, null, null);
            GraphicsHooks.ConfigureWindows(true);
            PerformancePrefs.Configure(HostSettings("4x", true, "50"));
            Dictionary<string, object> pc = ProfileOptions(false);
            GraphicsHooks.GameOptionsApplied(pc);
            Assert.Equal(0.6f, pc["renderTargetSizeScale"]);
        }

        // ------------------------------------------------------------------ the engine members on the player's assemblies

        [Fact]
        public void TheEngineMembersResolveInTheRealAndroidAssemblies()
        {
            string dir = Environment.GetEnvironmentVariable("MCVS_REFS");
            if (string.IsNullOrEmpty(dir)) dir = "/home/user/mcvs-work/extracted/assemblies";
            if (!File.Exists(Path.Combine(dir, "Game.dll")) || !File.Exists(Path.Combine(dir, "Gameloft.Engine.dll")))
            {
                Assert.False(Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1", "MCVS_REQUIRE_REAL_CLIENT=1 but Game.dll/Gameloft.Engine.dll are missing in " + dir);
                output.WriteLine("Game.dll/Gameloft.Engine.dll not found (" + dir + "): skipped");
                return;
            }
            var context = new GameContext(dir);
            try
            {
                Assembly engine = context.LoadFromAssemblyPath(Path.Combine(dir, "Gameloft.Engine.dll"));
                Assembly game = context.LoadFromAssemblyPath(Path.Combine(dir, "Game.dll"));
                Func<string, Type> resolve = name => new[] { engine, game }.Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
                List<string> problems = PerformancePrefs.VerifyMembers(resolve);
                foreach (string p in problems) output.WriteLine(p);
                Assert.Empty(problems);
                // The check has teeth: a renamed member is reported.
                Assert.Contains(PerformancePrefs.VerifyMembers(name => name == PerformancePrefs.WorldTypeName ? typeof(string) : resolve(name)),
                                p => p.Contains("GlobalExcludeLayers"));
                Assert.Contains(PerformancePrefs.VerifyMembers(name => name == PerformancePrefs.LocalStorageTypeName ? typeof(string) : resolve(name)),
                                p => p.Contains("LocalStorageComponent.Save()"));
                Assert.False(PerformancePrefs.SelectedProfileReset());
                // Without a running game the seam reads nothing and writes nothing (the default context has no game).
                Assert.Null(PerformancePrefs.Engine.MaxFramerateIndex);
                Assert.False(PerformancePrefs.Engine.SetMaxFramerateIndex(1));
                Assert.Null(PerformancePrefs.Engine.GlobalExcludeLayers);
                GC.KeepAlive(context);
            }
            finally
            {
                context.Unload();
            }
        }

        /// <summary>The game's own assemblies from the folder; the BCL from net8.</summary>
        private sealed class GameContext : AssemblyLoadContext
        {
            private readonly string directory;

            public GameContext(string directory) : base("mcvs-performance-" + Guid.NewGuid().ToString("N"), isCollectible: true)
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

    /// <summary><see cref="IEngineGraphics"/> in memory; a null member reads as missing (null, setters false).</summary>
    internal sealed class FakeEngineGraphics : IEngineGraphics
    {
        public float? Scale { get; set; }
        public float? ScaleOld { get; set; }
        public int? Samples { get; set; } = 0;
        public bool? LowRes { get; set; } = false;
        public List<string> Layers { get; set; } = new List<string>();
        public List<int> Fps { get; set; }
        public int? MaxIndex { get; set; }
        public bool CanWriteIndex { get; set; } = true;
        public int IndexWrites { get; private set; }

        public float? RenderTargetSizeScale { get { return Scale; } }

        public bool SetRenderTargetSizeScale(float scale)
        {
            Scale = scale;
            ScaleOld = scale;
            return true;
        }

        public int? AntialiasingSamples { get { return Samples; } }

        public bool? LowResMaps { get { return Layers == null ? null : LowRes; } }

        public bool SetLowResMaps(bool value)
        {
            if (LowRes == null) return false;
            LowRes = value;
            return true;
        }

        public IList<string> GlobalExcludeLayers { get { return Layers; } }

        public IList<int> FpsValues { get { return Fps; } }

        public int? MaxFramerateIndex { get { return MaxIndex; } }

        public bool SetMaxFramerateIndex(int index)
        {
            if (!CanWriteIndex || MaxIndex == null) return false;
            MaxIndex = index;
            IndexWrites++;
            return true;
        }
    }
}
