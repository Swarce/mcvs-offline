// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Embedded.Graphics
{
    /// <summary>What <see cref="GameOptionsProfile.Rewrite"/> did to GameOptions_android.json.</summary>
    public sealed class GameOptionsRewrite
    {
        /// <summary>The text the engine gets (the input itself when nothing changed).</summary>
        public string Json { get; internal set; }

        /// <summary>True when <see cref="Json"/> differs from the input.</summary>
        public bool Changed { get; internal set; }

        /// <summary>The settings value as understood: auto, mid, high or ultra.</summary>
        public string Choice { get; internal set; }

        /// <summary>The GameOptions GPU profile that now wins (MidGPU, HighGPU, High_Ultra), or null for auto.</summary>
        public string Profile { get; internal set; }

        /// <summary>World options set back to the MidGPU values ("excludedLoadingLayers 'Physics' (HighGPU: '-')"); empty when none.</summary>
        public List<string> Pinned { get; internal set; }

        /// <summary>One sentence for the boot trace.</summary>
        public string Summary { get; internal set; }
    }

    /// <summary>
    /// The graphics profile setting (docs/research/pc-textures.md WP0; settings.json <c>graphics.profile</c>). The game picks its
    /// GPU profile natively: <c>GameOptionPerformanceProfileComponent.OnCreate</c> hands the whole GameOptions_android.json to
    /// <c>GameOptionManager.InitializeGameOption</c>, which walks <c>profiles.GPU.selection</c> (rules on GPU$$, MODEL$$, CORES...;
    /// the first match wins, the catch-all is the last rule <c>{"MidGPU": [[["CORES&gt;1"]]]}</c>) and applies that profile's
    /// <c>overrides</c> on top of the GPU defaults. Patch 'graphics' passes the text through <c>GraphicsHooks.GameOptionsJson</c>
    /// first, and this class rewrites it so that Gameloft's own block for the chosen profile wins:
    /// <list type="bullet">
    /// <item><c>profiles.GPU.selection</c> becomes the single rule <c>[{"&lt;profile&gt;": [[["CORES&gt;1"]]]}]</c>, Gameloft's own
    /// catch-all condition, so the native selection lands on the chosen block on every phone;</item>
    /// <item><c>profiles.GPU.priority.&lt;profile&gt;</c> becomes <c>["", ""]</c> (no step up or down; <c>""</c> already ends the
    /// chain for High_Ultra and VeryLowGPU), so the chosen block is the one that applies;</item>
    /// <item>unless <c>graphics.fullProfile</c> is true, the chosen block's process-wide world options get the MidGPU values
    /// (<see cref="WorldOptions"/>): <c>excludedLoadingLayers</c> feeds the static <c>World.GlobalExcludeLayers</c> and
    /// <c>reducedAiUpdateFrequency</c> the static <c>BT_Root.AiModulo</c>, which the in-process TS/GS Worlds share with the
    /// client (pc-textures.md, critic note 1). HighGPU and High_Ultra would otherwise load the levels' Physics layer into
    /// the server Worlds and run bot AI twice as often.</item>
    /// </list>
    /// Every option value of the chosen block comes from the player's own file; nothing is invented. <c>auto</c> (the default)
    /// and anything unexpected leave the text exactly as it was. Never throws.
    /// </summary>
    public static class GameOptionsProfile
    {
        public const string Auto = "auto";
        public const string Mid = "mid";
        public const string High = "high";
        public const string Ultra = "ultra";

        /// <summary>The settings choices, in page order.</summary>
        public static readonly string[] Choices = { Auto, Mid, High, Ultra };

        /// <summary>The block whose values the pinned world options get.</summary>
        public const string BaselineProfile = "MidGPU";

        /// <summary>The options of a GPU block that change process-wide statics read by every World, server Worlds included.</summary>
        public static readonly string[] WorldOptions = { "excludedLoadingLayers", "reducedAiUpdateFrequency" };

        /// <summary>The selection rule Gameloft uses as its catch-all (every phone has more than one core).</summary>
        public const string CatchAllCondition = "CORES>1";

        /// <summary>mid → MidGPU, high → HighGPU, ultra → High_Ultra; null for auto or an unknown value.</summary>
        public static string GpuProfileOf(string choice)
        {
            switch (Normalize(choice))
            {
                case Mid: return "MidGPU";
                case High: return "HighGPU";
                case Ultra: return "High_Ultra";
                default: return null;
            }
        }

        /// <summary>The settings value in lower case, or null when it is not one of <see cref="Choices"/>.</summary>
        public static string Normalize(string choice)
        {
            string c = (choice ?? "").Trim().ToLowerInvariant();
            foreach (string known in Choices)
            {
                if (c == known) return known;
            }
            return null;
        }

        /// <summary>Rewrites <paramref name="json"/> for <paramref name="choice"/> (see the class remarks). Never throws.</summary>
        public static GameOptionsRewrite Rewrite(string json, string choice, bool fullProfile)
        {
            var result = new GameOptionsRewrite { Json = json, Choice = Normalize(choice) ?? Auto, Pinned = new List<string>() };
            try
            {
                string normalized = Normalize(choice);
                if (normalized == null)
                {
                    result.Summary = "graphics.profile '" + choice + "' is not one of auto, mid, high, ultra: the game picks its profile itself";
                    return result;
                }
                if (normalized == Auto)
                {
                    result.Summary = "graphics.profile auto: the game picks its GPU profile itself (GameOptions_android.json unchanged)";
                    return result;
                }
                if (string.IsNullOrEmpty(json))
                {
                    result.Summary = "graphics.profile " + normalized + ": GameOptions_android.json is empty; left alone";
                    return result;
                }
                string profile = GpuProfileOf(normalized);
                object parsed;
                if (!MiniJson.TryParse(json, out parsed))
                {
                    result.Summary = "graphics.profile " + normalized + ": GameOptions_android.json does not parse; left alone";
                    return result;
                }
                Dictionary<string, object> gpu = Path(parsed, "profiles", "GPU");
                Dictionary<string, object> overrides = gpu == null ? null : gpu.ContainsKey("overrides") ? gpu["overrides"] as Dictionary<string, object> : null;
                Dictionary<string, object> chosen = overrides == null || !overrides.ContainsKey(profile) ? null : overrides[profile] as Dictionary<string, object>;
                if (gpu == null || !(gpu.ContainsKey("selection") && gpu["selection"] is List<object>) || chosen == null)
                {
                    result.Summary = "graphics.profile " + normalized + ": GameOptions_android.json has no GPU profile '" + profile + "' (or no selection list); left alone";
                    return result;
                }

                string text = json;
                // 1. The selection: one rule, Gameloft's own catch-all, naming the chosen block.
                string rule = "[{" + MiniJson.Quote(profile) + ": [[[" + MiniJson.Quote(CatchAllCondition) + "]]]}]";
                if (!Replace(ref text, rule, "profiles", "GPU", "selection"))
                {
                    result.Summary = "graphics.profile " + normalized + ": the GPU selection list could not be located in the text; left alone";
                    return result;
                }

                // 2. The world options of the chosen block, back at the MidGPU values (unless graphics.fullProfile).
                if (!fullProfile)
                {
                    Dictionary<string, object> baseline = overrides.ContainsKey(BaselineProfile) ? overrides[BaselineProfile] as Dictionary<string, object> : null;
                    var block = new Dictionary<string, object>(chosen, StringComparer.Ordinal);
                    bool pinned = false;
                    foreach (string option in WorldOptions)
                    {
                        object want = baseline != null && baseline.ContainsKey(option) ? baseline[option] : DefaultValue(gpu, option);
                        object have = chosen.ContainsKey(option) ? chosen[option] : DefaultValue(gpu, option);
                        if (want == null || JsonValue.DeepEquals(want, have)) continue;
                        block[option] = want;
                        pinned = true;
                        result.Pinned.Add(option + " " + Show(want) + " (" + profile + ": " + Show(have) + ")");
                    }
                    if (pinned && !Replace(ref text, MiniJson.Serialize(block), "profiles", "GPU", "overrides", profile))
                    {
                        result.Summary = "graphics.profile " + normalized + ": the " + profile + " block could not be located in the text; left alone";
                        result.Pinned.Clear();
                        return result;
                    }
                }

                // 3. No priority step away from the chosen block.
                Dictionary<string, object> priority = gpu.ContainsKey("priority") ? gpu["priority"] as Dictionary<string, object> : null;
                bool steps = priority != null && priority.ContainsKey(profile);
                if (steps && !Replace(ref text, "[\"\", \"\"]", "profiles", "GPU", "priority", profile)) steps = false;

                object check;
                if (!MiniJson.TryParse(text, out check))
                {
                    result.Summary = "graphics.profile " + normalized + ": the rewritten GameOptions_android.json does not parse; left alone";
                    result.Pinned.Clear();
                    return result;
                }
                result.Json = text;
                result.Changed = !string.Equals(text, json, StringComparison.Ordinal);
                result.Profile = profile;
                result.Summary = "graphics.profile " + normalized + ": GameOptions GPU profile forced to " + profile + " (the selection is one '" +
                                 CatchAllCondition + "' rule" + (steps ? ", no priority step" : "") + "); " +
                                 (fullProfile ? "graphics.fullProfile true: its world options apply unchanged"
                                              : result.Pinned.Count == 0 ? "its world options already equal " + BaselineProfile + "'s"
                                                                         : "world options kept at the " + BaselineProfile + " values: " + string.Join(", ", result.Pinned.ToArray()));
                return result;
            }
            catch (Exception ex)
            {
                result.Json = json;
                result.Changed = false;
                result.Profile = null;
                result.Pinned.Clear();
                result.Summary = "graphics.profile " + choice + ": GameOptions_android.json could not be rewritten (" + ex.GetType().Name + "); left alone";
                return result;
            }
        }

        /// <summary>The value of <paramref name="option"/> in the GPU block's "defaults" list ([name, value, help, range] rows), or null.</summary>
        public static object DefaultValue(Dictionary<string, object> gpu, string option)
        {
            var defaults = gpu == null || !gpu.ContainsKey("defaults") ? null : gpu["defaults"] as List<object>;
            if (defaults == null) return null;
            foreach (object entry in defaults)
            {
                var named = entry as Dictionary<string, object>;
                if (named == null) continue;
                foreach (KeyValuePair<string, object> kv in named)
                {
                    var rows = kv.Value as List<object>;
                    if (rows == null) continue;
                    foreach (object r in rows)
                    {
                        var row = r as List<object>;
                        if (row != null && row.Count >= 2 && string.Equals(row[0] as string, option, StringComparison.Ordinal)) return row[1];
                    }
                }
            }
            return null;
        }

        private static Dictionary<string, object> Path(object root, params string[] keys)
        {
            object cur = root;
            foreach (string key in keys)
            {
                var o = cur as Dictionary<string, object>;
                if (o == null || !o.ContainsKey(key)) return null;
                cur = o[key];
            }
            return cur as Dictionary<string, object>;
        }

        /// <summary>Replaces the value at the key path with <paramref name="replacement"/> (raw JSON); false when not found.</summary>
        private static bool Replace(ref string text, string replacement, params string[] path)
        {
            int start, length;
            if (!JsonSpanScanner.TryFindValue(text, path, out start, out length)) return false;
            text = text.Substring(0, start) + replacement + text.Substring(start + length);
            return true;
        }

        private static string Show(object value)
        {
            if (value is string) return "'" + value + "'";
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is double) return ((double)value).ToString("0.###", CultureInfo.InvariantCulture);
            return value == null ? "(unset)" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
