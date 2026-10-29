// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLLs and into the net8 tests (used on PC only).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Embedded.Graphics
{
    /// <summary>
    /// The graphics profile setting on PC (docs/design/pc-port.md §6.7, F7): the text is GameOptions_windows.json (data pack
    /// <c>462043511.f</c>), whose layout differs from Android's.
    /// <list type="bullet">
    /// <item><b>MEM rule</b> (settings <c>embedded.physics.keepPhysicsLayer</c>, default true): the token <c>Physics</c> is removed from
    /// every space-separated <c>excludedLoadingLayers</c> value (a <c>defaults</c> row or an <c>overrides</c> member of any profile
    /// type): <c>"Physics VFX"</c> → <c>"VFX"</c>. In the shipped file that is MEM <c>LowEnd</c>, the fallback when the native
    /// <c>GPUMEM</c> detection fails; <c>excludedLoadingLayers</c> feeds the process-wide <c>World.GlobalExcludeLayers</c>, so the
    /// in-process TS/GS Worlds would load their levels without physics ([HL] §5). The rule counts what it removed.</item>
    /// <item><c>graphics.profile</c> <c>mid</c>/<c>high</c> (and <c>ultra</c>, which has no block on PC: HighGPU, with a note):
    /// <c>profiles.GPU.selection</c> becomes <c>[{"&lt;MidGPU|HighGPU&gt;": []}]</c> (the PC catch-all form is an empty rule, as
    /// <c>{"LowGPU": []}</c>); the PC <c>priority</c> objects are empty, so nothing else changes; unless <c>graphics.fullProfile</c>,
    /// the chosen block's <c>reducedAiUpdateFrequency</c> takes the MidGPU value (no PC GPU block sets
    /// <c>excludedLoadingLayers</c>). <c>auto</c> changes nothing but the MEM rule.</item>
    /// </list>
    /// Every value comes from the player's own file; the text is edited in place (spans), so everything else stays byte for
    /// byte. Never throws: on any doubt the input is returned.
    /// </summary>
    public static class GameOptionsWindows
    {
        public const string FileName = "GameOptions_windows.json";
        public const string PhysicsLayer = "Physics";
        public const string LayersOption = "excludedLoadingLayers";
        public const string UltraNote = "ultra is not in GameOptions_windows.json: HighGPU";

        /// <summary>mid → MidGPU, high and ultra → HighGPU; null for auto or an unknown value.</summary>
        public static string GpuProfileOf(string choice)
        {
            switch (GameOptionsProfile.Normalize(choice))
            {
                case GameOptionsProfile.Mid: return "MidGPU";
                case GameOptionsProfile.High:
                case GameOptionsProfile.Ultra: return "HighGPU";
                default: return null;
            }
        }

        private static readonly GameOptionsProfile.Variant Variant = new GameOptionsProfile.Variant
        {
            FileName = FileName,
            ProfileOf = GpuProfileOf,
            Selection = profile => "[{" + MiniJson.Quote(profile) + ": []}]",
            RuleText = "one empty rule",
        };

        /// <summary>The MEM rule (when <paramref name="keepPhysicsLayer"/>), then the GPU profile. Never throws.</summary>
        public static GameOptionsRewrite Rewrite(string json, string choice, bool fullProfile, bool keepPhysicsLayer)
        {
            string text = json;
            int removed = 0;
            string memNote;
            if (!keepPhysicsLayer)
            {
                memNote = "MEM rule off";
            }
            else
            {
                string ruled;
                if (TryRemovePhysics(json, out ruled, out removed))
                {
                    text = ruled;
                    memNote = "MEM rule on, removed " + removed.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    memNote = "MEM rule on, but " + FileName + " could not be read; left alone";
                }
            }

            string normalized = GameOptionsProfile.Normalize(choice);
            GameOptionsRewrite result;
            if (normalized == null || normalized == GameOptionsProfile.Auto || string.IsNullOrEmpty(text))
            {
                result = new GameOptionsRewrite
                {
                    Json = text,
                    Choice = normalized ?? GameOptionsProfile.Auto,
                    Pinned = new List<string>(),
                    Summary = normalized == null
                        ? "graphics.profile '" + choice + "' is not one of auto, mid, high, ultra: the game picks its profile itself"
                        : "graphics.profile auto: the game picks its GPU profile itself",
                };
            }
            else
            {
                result = GameOptionsProfile.Rewrite(text, normalized, fullProfile, Variant);
                if (normalized == GameOptionsProfile.Ultra) result.Summary = UltraNote + "; " + result.Summary;
            }
            result.Changed = !string.Equals(result.Json, json, StringComparison.Ordinal);
            result.MemRule = keepPhysicsLayer ? "on, removed " + removed.ToString(CultureInfo.InvariantCulture) : "off";
            result.PhysicsRemoved = removed;
            result.Summary = result.Summary + "; " + memNote;
            return result;
        }

        /// <summary>
        /// Removes the token <see cref="PhysicsLayer"/> from every <see cref="LayersOption"/> string value (an object member
        /// <c>"excludedLoadingLayers": "..."</c> or a defaults row <c>["excludedLoadingLayers", "...", ...]</c>). False when the text
        /// does not parse (before or after) or the edit changed anything but those values.
        /// </summary>
        internal static bool TryRemovePhysics(string json, out string result, out int removed)
        {
            result = json;
            removed = 0;
            try
            {
                object before;
                if (string.IsNullOrEmpty(json) || !MiniJson.TryParse(json, out before)) return false;
                var sb = new StringBuilder(json.Length);
                int copied = 0;
                int pos = 0;
                while (pos < json.Length)
                {
                    char c = json[pos];
                    if (c != '"')
                    {
                        pos++;
                        continue;
                    }
                    string name;
                    int end = ReadString(json, pos, out name);
                    if (end < 0) return false;
                    pos = end;
                    if (name != LayersOption) continue;
                    int next = SkipWhitespace(json, pos);
                    if (next >= json.Length || (json[next] != ':' && json[next] != ',')) continue;
                    int valueStart = SkipWhitespace(json, next + 1);
                    if (valueStart >= json.Length || json[valueStart] != '"') continue;
                    string value;
                    int valueEnd = ReadString(json, valueStart, out value);
                    if (valueEnd < 0) return false;
                    int count;
                    string kept = WithoutToken(value, PhysicsLayer, out count);
                    pos = valueEnd;
                    if (count == 0) continue;
                    removed += count;
                    sb.Append(json, copied, valueStart - copied).Append(MiniJson.Quote(kept));
                    copied = valueEnd;
                }
                if (removed == 0) return true;
                sb.Append(json, copied, json.Length - copied);
                string text = sb.ToString();
                object after;
                if (!MiniJson.TryParse(text, out after)) return false;
                result = text;
                return true;
            }
            catch (Exception)
            {
                result = json;
                removed = 0;
                return false;
            }
        }

        /// <summary>"Physics VFX" → "VFX" (count 1); tokens are separated by spaces, compared ordinally.</summary>
        internal static string WithoutToken(string value, string token, out int count)
        {
            count = 0;
            if (string.IsNullOrEmpty(value)) return value ?? "";
            var kept = new List<string>();
            foreach (string part in value.Split(' '))
            {
                if (part.Length == 0) continue;
                if (part == token) count++;
                else kept.Add(part);
            }
            return count == 0 ? value : string.Join(" ", kept.ToArray());
        }

        private static int SkipWhitespace(string s, int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\r' || s[pos] == '\n')) pos++;
            return pos;
        }

        /// <summary><paramref name="pos"/> is at a '"'; returns the index after the closing quote (-1 when unterminated).</summary>
        private static int ReadString(string s, int pos, out string value)
        {
            var sb = new StringBuilder();
            value = null;
            for (int i = pos + 1; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"')
                {
                    value = sb.ToString();
                    return i + 1;
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (++i >= s.Length) return -1;
                char e = s[i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 >= s.Length) return -1;
                        sb.Append((char)int.Parse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            return -1;
        }
    }
}
