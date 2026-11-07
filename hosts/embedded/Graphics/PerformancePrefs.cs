// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using McvsOffline.Embedded.Admin;
using McvsOffline.Embedded.Admin.SettingsMenu;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Embedded.Graphics
{
    /// <summary>
    /// The values of the Android PERFORMANCE page and how the engine gets them (docs/design/settings-menu.md §2, §3.4; skeptic C1,
    /// E7). Android only: on PC (<see cref="GraphicsHooks.IsWindows"/>) every member does nothing and the JSON is left as it is.
    /// <list type="bullet">
    /// <item><b>At start, in the JSON rewrite</b> (<see cref="GraphicsHooks.GameOptionsJson"/>): <c>graphics.msaa</c> other than auto
    /// puts <c>renderTargetMSAA</c> 0/2/4/8 in the block the engine resolves (auto: the text is byte-identical); with
    /// <c>admin.settingsMenu.performance</c> on, every <c>maxFramerateValues</c> "30 60 -1" becomes "30 60 90 120 -1" (VeryLowGPU's
    /// "23 60 -1" stays).</item>
    /// <item><b>After ApplyGameOptions</b> (<see cref="GraphicsHooks.GameOptionsApplied"/>), when not auto: SCREEN SCALE
    /// (<c>RenderTargetSizeScale</c> and <c>RenderTargetSizeScaleOld</c>, Steam's formula <c>v &gt; 0.97 ? 1 : 0.4 + 0.6 v</c>) wins over
    /// the profile's own value; LOW RESOLUTION MAPS (<c>GameGameOptionsComponent.LowResMaps</c> and <c>World.GlobalExcludeLayers</c>
    /// HighEndOnly/LowEndOnly, also when neither entry is there); then the one-time FPS index migration, keyed on the resolved
    /// <c>GamePreferencesComponent.FpsValues</c> (E7), marker <c>graphics.fpsListVersion</c>, and mapped back when the feature is off.</item>
    /// <item><b>Live</b> (the page): SCREEN SCALE at once; LOW RESOLUTION MAPS for the next level load; GRAPHICS and ANTIALIASING
    /// stored, with <see cref="SettingsMenuState.RestartPending"/> while either differs from the start-time value (T-D6).</item>
    /// </list>
    /// <para>SCREEN SCALE and the level loads: every <c>World.LoadLevel</c> calls <c>GameApplication.ChangeGraphicQuality</c>
    /// (AND-CS GameApplication.cs:1465-1502), which sets the scale again from the component's <c>m_options</c>:
    /// <c>renderTargetSizeScaleMM</c> on a menu map, <c>renderTargetSizeScale</c> and then <c>dynamicRenderTargetSizeScale</c> on a
    /// match map. A static written once would last until the next level, so our value also goes into <c>m_options</c> (the
    /// dictionary the hook gets): both scales, and the dynamic scale off. It is the 3D resolution in the menus and in matches, as on
    /// Steam, which has one scale for both.</para>
    /// <para>The FPS migration needs the stored index, which the client World's preferences load after the options (their
    /// <c>InitializeFromLocalStorage</c> already applies it to the longer list: a stored UNLIMITED reads 90 until the migration).
    /// <see cref="GraphicsHooks"/> therefore leaves it to <see cref="ScheduleFpsMigration"/>: a 1 s timer posts the step to the game
    /// thread until the index is readable, and the marker goes into the server's copy of the settings (the one the settings page
    /// edits and saves), not the host's start-time copy, which a later save of the server's copy would overwrite.</para>
    /// </summary>
    internal static class PerformancePrefs
    {
        public const string Auto = "auto";

        /// <summary><c>graphics.msaa</c> values and the samples each gives (auto: none written).</summary>
        public static readonly string[] MsaaChoices = { Auto, "off", "2x", "4x", "8x" };

        /// <summary><c>graphics.lowResMaps</c> values.</summary>
        public static readonly string[] LowResMapsChoices = { Auto, "on", "off" };

        /// <summary>GameOptions_android.json's header default (:302-305) and what the page offers instead (owner answer 3).</summary>
        public const string StockFpsList = "30 60 -1";

        public const string ExtendedFpsList = "30 60 90 120 -1";

        /// <summary>The VeryLowGPU list (:1011), left as it is.</summary>
        public const string VeryLowFpsList = "23 60 -1";

        /// <summary><c>graphics.fpsListVersion</c> once the stored index belongs to <see cref="ExtendedFpsList"/>.</summary>
        public const long ExtendedFpsListVersion = 1;

        /// <summary>The GameOptions names of the two options the start-time rewrite changes.</summary>
        public const string MsaaOption = "renderTargetMSAA";

        public const string FpsListOption = "maxFramerateValues";

        /// <summary>The <c>m_options</c> entries ChangeGraphicQuality reads at every level load (see the class remarks).</summary>
        public const string ScaleOption = "renderTargetSizeScale";

        public const string MenuScaleOption = "renderTargetSizeScaleMM";
        public const string DynamicScaleOption = "dynamicRenderTargetSizeScale";

        /// <summary>World.GlobalExcludeLayers entries of LOW RESOLUTION MAPS (GameGameOptionsComponent.cs:76-81).</summary>
        public const string HighEndLayer = "HighEndOnly";

        public const string LowEndLayer = "LowEndOnly";

        /// <summary><see cref="ScheduleFpsMigration"/>: one try per second, for at most this many; the host's copy from this try on.</summary>
        internal const int MigrationTries = 180;

        internal const int MigrationHostCopyFrom = 60;

        /// <summary>The host's settings as read at start (EmbeddedHost.ConfigureGraphics); the start-time values come from here.</summary>
        private static volatile Settings hostSettings;

        /// <summary>The <c>m_options</c> of the last <see cref="GraphicsHooks.GameOptionsApplied"/>, and its values before ours.</summary>
        private static volatile IDictionary gameOptions;

        private static Dictionary<string, object> profileOptions;
        private static readonly object optionsGate = new object();
        private static volatile string summary;
        private static int migrationScheduled;
        private static volatile bool migrationDone;
        private static Timer migrationTimer;
        private static int migrationPosted;
        private static int migrationTicks;
        private static GameReflection poster;

        /// <summary>
        /// EmbeddedHost.ConfigureGraphics, before the game creates its GameDefinition: the settings the start-time steps read
        /// (<c>graphics.msaa</c>, <c>graphics.renderScale</c>, <c>graphics.lowResMaps</c>, <c>admin.settingsMenu.performance</c>,
        /// <c>graphics.fpsListVersion</c>). Never throws.
        /// </summary>
        public static void Configure(Settings settings)
        {
            hostSettings = settings;
        }

        /// <summary>The settings <see cref="Configure"/> got, or null.</summary>
        internal static Settings HostSettings { get { return hostSettings; } }

        /// <summary>The page's values in the host's copy (read through the schema), or null before <see cref="Configure"/>.</summary>
        internal static IPerformanceSettings HostValues
        {
            get
            {
                Settings s = hostSettings;
                return s == null ? null : new PerformanceSettingsStore(s, null);
            }
        }

        /// <summary><c>admin.settingsMenu.performance</c> in the host's copy (default true); false before <see cref="Configure"/>.</summary>
        internal static bool HostPerformanceSwitch
        {
            get
            {
                Settings s = hostSettings;
                if (s == null) return false;
                try
                {
                    return s.GetBool(SettingsSchema.SettingsMenuPerformanceKey, true);
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        /// <summary>What the start-time steps did, for the status line ("performance page: ..."), or null.</summary>
        public static string Summary { get { return summary; } }

        // ------------------------------------------------------------------ start: the JSON rewrite (T-D1, T-D2)

        /// <summary>
        /// GameOptions_android.json with <paramref name="msaa"/> (a <see cref="MsaaChoices"/> value) and, with
        /// <paramref name="extendFpsList"/>, the longer FRAME RATE LIMIT list. The input itself when nothing changes; never null for
        /// a non-null input. <paramref name="summary"/>: one phrase for the "[boot] graphics:" line.
        /// <para>Every value of the option in the file is rewritten: the GPU defaults row (<c>["renderTargetMSAA", 0, help, range]</c>)
        /// and any block that sets it (<c>"renderTargetMSAA": 2</c>), so whichever block the native selection lands on resolves to
        /// ours; only a number is replaced for MSAA, only the exact "30 60 -1" for the list.</para>
        /// </summary>
        public static string RewriteJson(string json, string msaa, bool extendFpsList, out string summary)
        {
            summary = null;
            try
            {
                if (json == null)
                {
                    summary = "no GameOptions text";
                    return null;
                }
                if (GraphicsHooks.IsWindows)
                {
                    summary = "PC: the PERFORMANCE values are Steam's own";
                    return json;
                }
                int? samples = MsaaSamples(msaa);
                string msaaName = NormalizeChoice(msaa, MsaaChoices) ?? msaa;
                if (samples == null && !extendFpsList)
                {
                    summary = "graphics.msaa " + (msaaName ?? Auto) + ", frame rate limits as the game has them";
                    return json;
                }
                object parsed;
                if (!MiniJson.TryParse(json, out parsed))
                {
                    summary = "graphics.msaa " + msaaName + ": the text does not parse; left alone";
                    return json;
                }
                string text = json;
                var parts = new List<string>();
                if (samples != null)
                {
                    string value = samples.Value.ToString(CultureInfo.InvariantCulture);
                    int n = ReplaceOptionValues(ref text, MsaaOption, token => IsNumberToken(token) ? value : null);
                    parts.Add(n > 0 ? "graphics.msaa " + msaaName + ": " + MsaaOption + " " + value + " (" + Places(n) + ")"
                                    : "graphics.msaa " + msaaName + ": no numeric " + MsaaOption + " in the file; left alone");
                }
                else
                {
                    parts.Add("graphics.msaa auto");
                }
                if (extendFpsList)
                {
                    string stock = MiniJson.Quote(StockFpsList);
                    string extended = MiniJson.Quote(ExtendedFpsList);
                    int n = ReplaceOptionValues(ref text, FpsListOption, token => token == stock ? extended : null);
                    parts.Add(n > 0 ? "frame rate limits '" + ExtendedFpsList + "' (" + Places(n) + "; other lists left alone)"
                                    : "no '" + StockFpsList + "' frame rate list in the file; left alone");
                }
                object check;
                if (!MiniJson.TryParse(text, out check))
                {
                    summary = "the rewritten PERFORMANCE values do not parse; left alone";
                    return json;
                }
                summary = string.Join(", ", parts.ToArray());
                return string.Equals(text, json, StringComparison.Ordinal) ? json : text;
            }
            catch (Exception ex)
            {
                summary = "the PERFORMANCE values could not be written (" + ex.GetType().Name + "); left alone";
                return json;
            }
        }

        /// <summary>off 0, 2x 2, 4x 4, 8x 8; null for auto and anything else.</summary>
        public static int? MsaaSamples(string choice)
        {
            switch (NormalizeChoice(choice, MsaaChoices))
            {
                case "off": return 0;
                case "2x": return 2;
                case "4x": return 4;
                case "8x": return 8;
                default: return null;
            }
        }

        /// <summary>
        /// Replaces the value after every key token <paramref name="option"/> (followed by <c>:</c> in an object or <c>,</c> in a
        /// defaults row) with what <paramref name="replace"/> returns for its raw token (null: keep). Strings are skipped as wholes,
        /// so a key inside a help text never matches. Returns how many values hold ours now (already equal ones included); the
        /// text is rebuilt only when one changed.
        /// </summary>
        private static int ReplaceOptionValues(ref string text, string option, Func<string, string> replace)
        {
            string key = MiniJson.Quote(option);
            var sb = new StringBuilder(text.Length + 16);
            int count = 0;
            int changed = 0;
            int copied = 0;
            int pos = 0;
            while (pos < text.Length)
            {
                if (text[pos] != '"')
                {
                    pos++;
                    continue;
                }
                int end = StringEnd(text, pos);
                if (end < 0) break;
                bool match = end - pos == key.Length && string.CompareOrdinal(text, pos, key, 0, key.Length) == 0;
                pos = end;
                if (!match) continue;
                int p = SkipWhitespace(text, end);
                if (p >= text.Length || (text[p] != ':' && text[p] != ',')) continue;
                int start = SkipWhitespace(text, p + 1);
                int valueEnd = TokenEnd(text, start);
                if (valueEnd <= start) continue;
                string token = text.Substring(start, valueEnd - start);
                string replacement = replace(token);
                pos = valueEnd;
                if (replacement == null) continue;
                count++;
                if (replacement == token) continue;
                sb.Append(text, copied, start - copied).Append(replacement);
                copied = valueEnd;
                changed++;
            }
            if (changed == 0) return count;
            sb.Append(text, copied, text.Length - copied);
            text = sb.ToString();
            return count;
        }

        /// <summary>The index after the string literal at <paramref name="pos"/> (a quote), or -1.</summary>
        private static int StringEnd(string s, int pos)
        {
            for (int i = pos + 1; i < s.Length; i++)
            {
                if (s[i] == '\\') i++;
                else if (s[i] == '"') return i + 1;
            }
            return -1;
        }

        /// <summary>The index after a scalar token (string, number, literal) at <paramref name="pos"/>; <paramref name="pos"/> for none.</summary>
        private static int TokenEnd(string s, int pos)
        {
            if (pos >= s.Length) return pos;
            if (s[pos] == '"') return Math.Max(pos, StringEnd(s, pos));
            if (s[pos] == '{' || s[pos] == '[') return pos;
            int i = pos;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && !char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        private static int SkipWhitespace(string s, int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
            return pos;
        }

        private static bool IsNumberToken(string token)
        {
            double ignored;
            return token.Length > 0 && (char.IsDigit(token[0]) || token[0] == '-')
                   && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out ignored);
        }

        private static string Places(int n)
        {
            return n == 1 ? "1 place" : n.ToString(CultureInfo.InvariantCulture) + " places";
        }

        // ------------------------------------------------------------------ start: after ApplyGameOptions (T-D3, T-D4, T-D5)

        /// <summary>
        /// GraphicsHooks.GameOptionsApplied on Android, after the profile set its own values: SCREEN SCALE, LOW RESOLUTION MAPS and the
        /// FPS index migration as above, on <paramref name="engine"/> (<see cref="Engine"/> in the game). Never throws.
        /// </summary>
        public static void ApplyAfterGameOptions(IEngineGraphics engine, IPerformanceSettings settings, Log log)
        {
            string line = Apply(engine, settings, null, true);
            try
            {
                if (log != null && line != null) log.Info("graphics: " + line);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// <see cref="ApplyAfterGameOptions(IEngineGraphics, IPerformanceSettings, Log)"/> with the component's <paramref name="options"/>
        /// (<c>m_options</c>, may be null): our scale also goes there, for ChangeGraphicQuality. With <paramref name="migrateNow"/> false
        /// the FPS step is left to <see cref="ScheduleFpsMigration"/>. Returns the "performance page: ..." phrase (null on PC or without
        /// settings). Never throws.
        /// </summary>
        internal static string Apply(IEngineGraphics engine, IPerformanceSettings settings, IDictionary options, bool migrateNow)
        {
            try
            {
                if (GraphicsHooks.IsWindows || engine == null || settings == null) return null;
                var parts = new List<string>();

                string renderScale = settings.RenderScale;
                CaptureOptions(options);
                float? scale = ScaleOf(renderScale);
                if (scale == null) parts.Add("screen scale auto (" + ShowScale(engine.RenderTargetSizeScale) + ")");
                else
                {
                    bool set = engine.SetRenderTargetSizeScale(scale.Value);
                    WriteScaleOptions(scale.Value);
                    parts.Add("screen scale " + renderScale + " -> " + ShowScale(scale) + (set ? "" : " (PostProcessManager not found)"));
                }

                string lowRes = NormalizeChoice(settings.LowResMaps, LowResMapsChoices) ?? Auto;
                if (lowRes == Auto) parts.Add("low resolution maps auto (" + ShowBool(engine.LowResMaps) + ")");
                else parts.Add("low resolution maps " + lowRes + (ApplyLowResMaps(engine, lowRes) ? "" : " (members not found)"));

                if (migrateNow) parts.Add(MigrateFps(engine, settings) ?? "frame rate limit index not readable yet");
                string line = "performance page: " + string.Join(", ", parts.ToArray());
                summary = line;
                return line;
            }
            catch (Exception ex)
            {
                return "performance page: not applied (" + ex.GetType().Name + ")";
            }
        }

        /// <summary>Keeps <paramref name="options"/> and, the first time this dictionary is seen, its scale entries before ours.</summary>
        private static void CaptureOptions(IDictionary options)
        {
            if (options == null) return;
            lock (optionsGate)
            {
                if (ReferenceEquals(options, gameOptions)) return;
                var before = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string key in new[] { ScaleOption, MenuScaleOption, DynamicScaleOption })
                {
                    if (options.Contains(key)) before[key] = options[key];
                }
                profileOptions = before;
                gameOptions = options;
            }
        }

        /// <summary>m_options: both scales ours, the dynamic scale off (its key only when the file has it).</summary>
        private static void WriteScaleOptions(float scale)
        {
            IDictionary options = gameOptions;
            if (options == null) return;
            lock (optionsGate)
            {
                options[ScaleOption] = scale;
                options[MenuScaleOption] = scale;
                if (options.Contains(DynamicScaleOption)) options[DynamicScaleOption] = false;
            }
        }

        /// <summary>m_options: the scale entries back at the profile's values (SCREEN SCALE set to auto on the page).</summary>
        private static void RestoreScaleOptions()
        {
            IDictionary options = gameOptions;
            if (options == null) return;
            lock (optionsGate)
            {
                Dictionary<string, object> before = profileOptions;
                foreach (string key in new[] { ScaleOption, MenuScaleOption, DynamicScaleOption })
                {
                    object value;
                    if (before != null && before.TryGetValue(key, out value)) options[key] = value;
                    else if (options.Contains(key)) options.Remove(key);
                }
            }
        }

        /// <summary>
        /// The stored <c>MaxFramerate</c> index on list <paramref name="from"/> mapped to list <paramref name="to"/>: the same frame rate,
        /// else the nearest lower one (unlimited, -1, stays unlimited); 0 when nothing fits (T-D3).
        /// </summary>
        public static int MigrateFpsIndex(int stored, IList<int> from, IList<int> to)
        {
            if (to == null || to.Count == 0 || from == null || stored < 0 || stored >= from.Count) return 0;
            int fps = from[stored];
            if (fps <= 0)
            {
                int unlimited = to.IndexOf(fps);
                if (unlimited >= 0) return unlimited;
                fps = 0x7fffffff;   // no unlimited entry: the highest limit (the trimmed mscorlib has no int.MaxValue)
            }
            int exact = to.IndexOf(fps);
            if (exact >= 0) return exact;
            int best = -1;
            for (int i = 0; i < to.Count; i++)
            {
                if (to[i] > 0 && to[i] < fps && (best < 0 || to[i] > to[best])) best = i;
            }
            return best >= 0 ? best : 0;
        }

        /// <summary>"30 60 -1" as numbers.</summary>
        internal static List<int> ParseFpsList(string list)
        {
            var result = new List<int>();
            foreach (string part in list.Split(' '))
            {
                if (part.Length > 0) result.Add(int.Parse(part, CultureInfo.InvariantCulture));
            }
            return result;
        }

        private static bool SameList(IList<int> a, IList<int> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// The FPS index migration (E7): keyed on the list the engine resolved, not on the switch. The extended list with marker 0
        /// maps the index from the stock list and writes marker 1; any other list with marker 1 maps it back from the extended list and
        /// writes 0; otherwise nothing changes (VeryLowGPU's "23 60 -1" with marker 0 included). The phrase for the log, or null while
        /// the list or the stored index cannot be read (try again later). A failed index write leaves the marker as it was.
        /// <para>The index lives in the game's LocalStorage, the marker in settings.json, saved later and off the game thread: a marker
        /// lost after the index was written (a failed save, a deleted or reset settings.json) reads 0 again. On the extended list a
        /// stored index past the stock list's end (120, UNLIMITED) can only be in the extended meaning already, so it is kept and only
        /// the marker written; mapped again it would read as nothing and become 30. Index 2 stays ambiguous (stock UNLIMITED,
        /// extended 90) and is read as the stock one.</para>
        /// </summary>
        internal static string MigrateFps(IEngineGraphics engine, IPerformanceSettings settings)
        {
            IList<int> now = engine.FpsValues;
            int? stored = engine.MaxFramerateIndex;
            if (now == null || stored == null) return null;
            var resolved = new List<int>(now);
            long version = settings.FpsListVersion;
            bool extendedNow = SameList(resolved, ParseFpsList(ExtendedFpsList));
            string list = "frame rate limits " + Join(resolved);
            IList<int> from;
            long target;
            if (extendedNow && version == 0)
            {
                from = ParseFpsList(StockFpsList);
                target = ExtendedFpsListVersion;
                if (stored.Value >= from.Count && stored.Value < resolved.Count)
                {
                    bool marked = settings.Save(SettingsSchema.GraphicsFpsListVersionKey, target);
                    return list + ", index " + stored.Value.ToString(CultureInfo.InvariantCulture) + " kept (already on this list), graphics.fpsListVersion " +
                           target.ToString(CultureInfo.InvariantCulture) + (marked ? "" : " not saved");
                }
            }
            else if (!extendedNow && version == ExtendedFpsListVersion)
            {
                from = ParseFpsList(ExtendedFpsList);
                target = 0;
            }
            else return list + ", index " + stored.Value.ToString(CultureInfo.InvariantCulture) + " kept (graphics.fpsListVersion " +
                        version.ToString(CultureInfo.InvariantCulture) + ")";
            int mapped = MigrateFpsIndex(stored.Value, from, resolved);
            string move = "index " + stored.Value.ToString(CultureInfo.InvariantCulture) + " -> " + mapped.ToString(CultureInfo.InvariantCulture);
            if (mapped != stored.Value && !engine.SetMaxFramerateIndex(mapped))
                return list + ", " + move + " could not be written (graphics.fpsListVersion stays " + version.ToString(CultureInfo.InvariantCulture) + ")";
            bool saved = settings.Save(SettingsSchema.GraphicsFpsListVersionKey, target);
            return list + ", " + move + " (from '" + Join(from) + "'), graphics.fpsListVersion " + target.ToString(CultureInfo.InvariantCulture) +
                   (saved ? "" : " not saved");
        }

        private static string Join(IList<int> values)
        {
            var parts = new string[values.Count];
            for (int i = 0; i < values.Count; i++) parts[i] = values[i].ToString(CultureInfo.InvariantCulture);
            return string.Join(" ", parts);
        }

        /// <summary>
        /// The game: <see cref="MigrateFps"/> once the client World's preferences are loaded. A 1 s timer posts the step to the game
        /// thread (one at a time) for at most <see cref="MigrationTries"/> s; it writes the marker into the server's copy of the
        /// settings (<c>AdminHooks.Host</c>) and only from try <see cref="MigrationHostCopyFrom"/> on, without a server, into the host's.
        /// Once per process; Android only. Never throws.
        /// </summary>
        internal static void ScheduleFpsMigration(Action<string> report)
        {
            try
            {
                if (GraphicsHooks.IsWindows || Interlocked.Exchange(ref migrationScheduled, 1) != 0) return;
                migrationTimer = new Timer(state => MigrationTick(report), null, 1000, 1000);
            }
            catch (Exception)
            {
            }
        }

        private static void MigrationTick(Action<string> report)
        {
            try
            {
                int tick = Interlocked.Increment(ref migrationTicks);
                if (migrationDone || tick > MigrationTries)
                {
                    StopMigration();
                    if (!migrationDone && report != null) report("frame rate limit index not readable within " + MigrationTries + " s; no migration");
                    migrationDone = true;
                    return;
                }
                if (Interlocked.CompareExchange(ref migrationPosted, 1, 0) != 0) return;
                AdminHost host = AdminHooks.Host;
                IPerformanceSettings settings = host != null ? host.PerformanceSettings : tick >= MigrationHostCopyFrom ? HostValues : null;
                if (settings == null)
                {
                    Interlocked.Exchange(ref migrationPosted, 0);
                    return;
                }
                Action step = () =>
                {
                    try
                    {
                        if (migrationDone) return;
                        string line = MigrateFps(Engine, settings);
                        if (line == null) return;
                        migrationDone = true;
                        StopMigration();
                        summary = (summary ?? "performance page:") + "; " + line;
                        if (report != null) report(line);
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        Interlocked.Exchange(ref migrationPosted, 0);
                    }
                };
                bool posted = host != null ? ((ISettingsMenuHost)host).Post(step) : Poster().Post(step, null);
                if (!posted) Interlocked.Exchange(ref migrationPosted, 0);
            }
            catch (Exception)
            {
                Interlocked.Exchange(ref migrationPosted, 0);
            }
        }

        private static GameReflection Poster()
        {
            GameReflection p = Interlocked.CompareExchange(ref poster, null, null);
            if (p != null) return p;
            p = GameReflection.Load();
            return Interlocked.CompareExchange(ref poster, p, null) ?? p;
        }

        private static void StopMigration()
        {
            Timer t = Interlocked.Exchange(ref migrationTimer, null);
            if (t != null) t.Dispose();
        }

        // ------------------------------------------------------------------ SCREEN SCALE

        /// <summary><c>graphics.renderScale</c> "0".."100" → <c>RenderTargetSizeScale</c> (v &gt; 97: 1, else 0.4 + 0.6 v/100); null for auto.</summary>
        public static float? ScaleOf(string renderScale)
        {
            string s = (renderScale ?? "").Trim();
            if (s.Length < 1 || s.Length > 3) return null;
            int value = 0;
            foreach (char c in s)
            {
                if (c < '0' || c > '9') return null;
                value = value * 10 + (c - '0');
            }
            if (value > 100) return null;
            double v = value / 100.0;
            // Steam's formula (PC-CS GamePreferencesComponent.cs:496-497), in double as there, then to float.
            return Convert.ToSingle(v > 0.97 ? 1.0 : 0.4 + v * 0.6, CultureInfo.InvariantCulture);
        }

        /// <summary>The slider position 0..100 that shows <paramref name="scale"/>: (s − 0.4)/0.6, clamped (the page's initial display).</summary>
        public static int SliderOf(float scale)
        {
            if (float.IsNaN(scale)) return 100;
            double v = (scale - 0.4) / 0.6 * 100.0;
            if (v <= 0) return 0;
            if (v >= 100) return 100;
            return (int)Math.Round(v, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// SCREEN SCALE's slider while <c>graphics.renderScale</c> is auto (the page's initial display, design §2): the scale a match
        /// renders at (<see cref="AutoMatchScale"/>), else <paramref name="engine"/>'s current one, else 100. Not the current one first:
        /// the page exists only in the main menu, where every level load set the menu scale (<c>renderTargetSizeScaleMM</c>, 1.0 in
        /// GameOptions_android.json:284-285), so the S22's MidGPU would show 100 while its matches render at 0.6, and a write of the
        /// shown value would then force matches to 1.0. The page still treats a write equal to this display as no change (E1).
        /// </summary>
        public static int AutoRenderScaleSlider(IEngineGraphics engine)
        {
            try
            {
                float? scale = AutoMatchScale;
                if (scale == null && engine != null) scale = engine.RenderTargetSizeScale;
                return scale == null ? 100 : SliderOf(scale.Value);
            }
            catch (Exception)
            {
                return 100;
            }
        }

        /// <summary>
        /// The scale the profile gives a match (ChangeGraphicQuality on a match map, see the class remarks), from the profile's own
        /// <c>m_options</c> values (before ours): its dynamic scale when <c>dynamicRenderTargetSizeScale</c> is true (applied last,
        /// <see cref="DynamicMatchScale"/>), else <c>renderTargetSizeScale</c>, else <c>renderTargetSizeScaleMM</c> (a match load
        /// without either keeps the menu's). Null before the first ApplyGameOptions or when no entry reads as a number.
        /// </summary>
        public static float? AutoMatchScale
        {
            get
            {
                try
                {
                    Dictionary<string, object> before;
                    lock (optionsGate) before = profileOptions;
                    if (before == null) return null;
                    object value;
                    if (before.TryGetValue(DynamicScaleOption, out value) && IsTrue(value))
                    {
                        Func<float?> dynamicScale = DynamicMatchScale;
                        float? dynamic = dynamicScale == null ? null : dynamicScale();
                        if (dynamic != null) return dynamic;
                    }
                    float? scale;
                    if (before.TryGetValue(ScaleOption, out value) && (scale = ToScale(value)) != null) return scale;
                    if (before.TryGetValue(MenuScaleOption, out value) && (scale = ToScale(value)) != null) return scale;
                    return null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// The engine's dynamic scale (<c>PerformanceOptionApplierComponent.CalculateDynamicRenderTargetScale</c>, from
        /// <c>PlatformInfo</c>), or null when unreadable (tests replace it).
        /// </summary>
        internal static Func<float?> DynamicMatchScale = ReadDynamicScale;

        /// <summary>AND-CS PerformanceOptionApplierComponent.cs:403-420, in float as there; null for a value that is not a number.</summary>
        internal static float? DynamicScaleOf(int densityDpi, int pixelWidth, int pixelHeight, float inchWidth, float inchHeight)
        {
            float num = ((float)densityDpi - 0.5f) * 2f / ((float)pixelWidth / inchWidth + (float)pixelHeight / inchHeight);
            if ((float)pixelWidth * num > 1920f) num = 1920f / (float)pixelWidth;
            if (num > 1f) num = 1f;
            return float.IsNaN(num) || float.IsInfinity(num) || num <= 0f ? (float?)null : num;
        }

        private static float? ReadDynamicScale()
        {
            try
            {
                Type info = LoadedTypes.Find(LoadedTypes.EngineAssembly, PlatformInfoTypeName);
                if (info == null) return null;
                Func<string, object> read = name =>
                {
                    PropertyInfo p = info.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                    return p == null ? null : p.GetValue(null, null);
                };
                object dpi = read("DensityDpi"), pw = read("ScreenPixelWidth"), ph = read("ScreenPixelHeight");
                object iw = read("ScreenInchWidth"), ih = read("ScreenInchHeight");
                if (!(dpi is int) || !(pw is int) || !(ph is int) || !(iw is float) || !(ih is float)) return null;
                return DynamicScaleOf((int)dpi, (int)pw, (int)ph, (float)iw, (float)ih);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsTrue(object value)
        {
            if (value is bool) return (bool)value;
            string s = value as string;
            return s != null && string.Equals(s.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static float? ToScale(object value)
        {
            try
            {
                if (value == null || value is bool) return null;
                float f = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                return float.IsNaN(f) || float.IsInfinity(f) ? (float?)null : f;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Live, game thread: stores <c>graphics.renderScale</c> (0..100, or auto) and applies it at once (auto: from the next start).</summary>
        public static bool SetRenderScale(IEngineGraphics engine, IPerformanceSettings settings, string value)
        {
            try
            {
                if (GraphicsHooks.IsWindows || settings == null) return false;
                string v = (value ?? "").Trim().ToLowerInvariant();
                float? scale = ScaleOf(v);
                if (scale == null && v != Auto) return false;
                if (scale != null) v = int.Parse(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                if (!settings.Save(SettingsSchema.GraphicsRenderScaleKey, v)) return false;
                if (scale == null)
                {
                    // The profile's own scales return at the next level load (ChangeGraphicQuality); the static stays until then.
                    RestoreScaleOptions();
                    return true;
                }
                if (engine != null) engine.SetRenderTargetSizeScale(scale.Value);
                WriteScaleOptions(scale.Value);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ LOW RESOLUTION MAPS, ANTIALIASING, GRAPHICS

        /// <summary>
        /// <c>on</c>: <c>GlobalExcludeLayers</c> gets HighEndOnly and loses LowEndOnly, <c>LowResMaps</c> true; <c>off</c>: the reverse;
        /// also when neither entry is in the list (T-D5). auto changes nothing. False when the engine's members are missing.
        /// </summary>
        public static bool ApplyLowResMaps(IEngineGraphics engine, string choice)
        {
            try
            {
                if (GraphicsHooks.IsWindows || engine == null) return false;
                string c = NormalizeChoice(choice, LowResMapsChoices);
                if (c == null) return false;
                if (c == Auto) return true;
                bool on = c == "on";
                IList<string> layers = engine.GlobalExcludeLayers;
                if (layers == null || engine.LowResMaps == null) return false;
                string exclude = on ? HighEndLayer : LowEndLayer;
                string keep = on ? LowEndLayer : HighEndLayer;
                // Steam swaps only when the opposite entry is there (PC-CS GamePreferencesComponent.cs:479-486); here the list ends
                // with exactly one of the two whatever it held (ApplyGameOptions adds one more at every OnCreate).
                while (layers.Remove(keep))
                {
                }
                while (layers.Remove(exclude))
                {
                }
                layers.Add(exclude);
                return engine.SetLowResMaps(on);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Live: stores <c>graphics.lowResMaps</c> and applies it for the next level load.</summary>
        public static bool SetLowResMaps(IEngineGraphics engine, IPerformanceSettings settings, string choice)
        {
            try
            {
                if (GraphicsHooks.IsWindows || settings == null) return false;
                string c = NormalizeChoice(choice, LowResMapsChoices);
                if (c == null || !settings.Save(SettingsSchema.GraphicsLowResMapsKey, c)) return false;
                // auto: the profile's own value from the next start (the levels already loaded keep their layers either way).
                if (c != Auto) ApplyLowResMaps(engine, c);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Stores <c>graphics.msaa</c>; a new value sets <see cref="SettingsMenuState.RestartPending"/> as <see cref="UpdateRestartPending"/> says.</summary>
        public static bool SetMsaa(IPerformanceSettings settings, SettingsMenuState state, string choice)
        {
            try
            {
                if (GraphicsHooks.IsWindows || settings == null) return false;
                string c = NormalizeChoice(choice, MsaaChoices);
                if (c == null) return false;
                if (c == settings.Msaa) return true;
                if (!settings.Save(SettingsSchema.GraphicsMsaaKey, c)) return false;
                UpdateRestartPending(settings, state);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// GRAPHICS (owner answer 1): stores <c>graphics.profile</c> (auto, mid, high, ultra) and resets AppPreferences
        /// <c>GameOptions_SelectedProfile</c> to 0, so AUTO is the game's own choice for this phone [G reachable]. The current value
        /// raises nothing; a new one sets <see cref="SettingsMenuState.RestartPending"/> as <see cref="UpdateRestartPending"/> says (T-D6).
        /// </summary>
        public static bool SetGraphicsProfile(IPerformanceSettings settings, SettingsMenuState state, string choice)
        {
            try
            {
                if (GraphicsHooks.IsWindows || settings == null) return false;
                string c = GameOptionsProfile.Normalize(choice);
                if (c == null) return false;
                if (c == GameOptionsProfile.Normalize(settings.GraphicsProfile)) return true;
                if (!settings.Save("graphics.profile", c)) return false;
                Func<bool> reset = SelectedProfileReset;
                if (reset != null) reset();
                UpdateRestartPending(settings, state);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// After a store of GRAPHICS or ANTIALIASING: a restart is pending while <c>graphics.profile</c> or <c>graphics.msaa</c> differs
        /// from the value this process started with (<see cref="HostValues"/>, the host's copy, which nothing saves or reloads), so a
        /// change put back before leaving the page asks for nothing. Without the host's copy (<see cref="Configure"/> not run) every
        /// change counts.
        /// </summary>
        private static void UpdateRestartPending(IPerformanceSettings settings, SettingsMenuState state)
        {
            if (state == null) return;
            IPerformanceSettings start = HostValues;
            if (start == null)
            {
                state.RestartPending = true;
                return;
            }
            state.RestartPending = GameOptionsProfile.Normalize(settings.GraphicsProfile) != GameOptionsProfile.Normalize(start.GraphicsProfile)
                                   || (NormalizeChoice(settings.Msaa, MsaaChoices) ?? Auto) != (NormalizeChoice(start.Msaa, MsaaChoices) ?? Auto);
        }

        /// <summary>
        /// What <see cref="SetGraphicsProfile"/> calls after a change (tests count the calls): AppPreferences
        /// <c>GameOptions_SelectedProfile</c> to 0 when it is not, saved as the game saves it: <c>SetSecurePreference</c>, then the
        /// client World's <c>LocalStorage.Save()</c> (GamePreferencesComponent.NativeChangeGraphicSetting, AND-CS :186-194, which
        /// stores value − 1 of the stock row's step). Not <c>AppPreferences.SavePreferences()</c>: that only walks settings components
        /// marked [AppPreferencesClass], and none is. True when it is 0 now and, after a change, saved.
        /// </summary>
        internal static Func<bool> SelectedProfileReset = ResetSelectedProfile;

        private static bool ResetSelectedProfile()
        {
            try
            {
                Type prefs = LoadedTypes.Find(LoadedTypes.EngineAssembly, AppPreferencesTypeName);
                if (prefs == null) return false;
                const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
                MethodInfo get = prefs.GetMethod("GetSecurePreferenceAsInt", Static, null, new[] { typeof(string), typeof(int) }, null);
                MethodInfo set = prefs.GetMethod("SetSecurePreference", Static, null, new[] { typeof(string), typeof(int) }, null);
                if (get == null || set == null) return false;
                object now = get.Invoke(null, new object[] { "GameOptions_SelectedProfile", 0 });
                if (now is int && (int)now == 0) return true;
                set.Invoke(null, new object[] { "GameOptions_SelectedProfile", 0 });
                object world = GameEngineGraphics.ClientWorld();
                PropertyInfo storage = world == null ? null : world.GetType().GetProperty("LocalStorage", BindingFlags.Public | BindingFlags.Instance);
                object local = storage == null ? null : storage.GetValue(world, null);
                MethodInfo save = local == null ? null : local.GetType().GetMethod("Save", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (save == null) return false;
                object saved = save.Invoke(local, null);
                return !(saved is bool) || (bool)saved;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The choice in lower case when it is one of <paramref name="choices"/>, else null.</summary>
        private static string NormalizeChoice(string choice, string[] choices)
        {
            string c = (choice ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(choices, c) >= 0 ? c : null;
        }

        private static string ShowScale(float? scale)
        {
            return scale == null ? "unknown" : scale.Value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string ShowBool(bool? value)
        {
            return value == null ? "unknown" : value.Value ? "on" : "off";
        }

        // ------------------------------------------------------------------ the engine

        private static readonly GameEngineGraphics engineInstance = new GameEngineGraphics();

        /// <summary>The engine's statics by reflection on the loaded assemblies (LoadedTypes, never a load by name); null members read as null.</summary>
        public static IEngineGraphics Engine
        {
            get { return engineInstance; }
        }

        /// <summary>Tests: back to the state before the hooks ran (the switches and <see cref="SelectedProfileReset"/> included).</summary>
        internal static void ResetForTests()
        {
            StopMigration();
            hostSettings = null;
            lock (optionsGate)
            {
                gameOptions = null;
                profileOptions = null;
            }
            summary = null;
            migrationScheduled = 0;
            migrationDone = false;
            migrationPosted = 0;
            migrationTicks = 0;
            SelectedProfileReset = ResetSelectedProfile;
            DynamicMatchScale = ReadDynamicScale;
        }

        // The game's types the engine seam uses (AND-CS), found among the loaded assemblies only.
        internal const string PostProcessTypeName = "Gameloft.Engine.PostProcessManager";
        internal const string GameOptionsTypeName = "Game.GameGameOptionsComponent";
        internal const string WorldTypeName = "Gameloft.Engine.World";
        internal const string PreferencesTypeName = "Game.GamePreferencesComponent";
        internal const string ApplicationTypeName = "Gameloft.Engine.GameApplication";
        internal const string AppPreferencesTypeName = "Gameloft.Engine.AppPreferences";
        internal const string ProfileComponentTypeName = "Gameloft.Engine.GameOptionPerformanceProfileComponent";
        internal const string PlatformInfoTypeName = "Gameloft.Engine.PlatformInfo";
        internal const string LocalStorageTypeName = "Gameloft.Engine.LocalStorageComponent";

        /// <summary>
        /// Every member <see cref="Engine"/>, <see cref="SelectedProfileReset"/> and <see cref="DynamicMatchScale"/> use, looked up as they look them up but never
        /// called, plus what the class remarks rely on (<c>GameApplication.ChangeGraphicQuality(string)</c> and the component's
        /// <c>m_options</c>); one line per problem. The tests run it on the player's own assemblies, so a game update that renames
        /// one shows up there and not as a row that silently does nothing.
        /// </summary>
        internal static List<string> VerifyMembers(Func<string, Type> resolve)
        {
            var problems = new List<string>();
            const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
            const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;
            Func<string, Type> type = name =>
            {
                Type t = resolve(name);
                if (t == null) problems.Add(name + " not found");
                return t;
            };
            Action<Type, string, Func<Type, bool>, string> staticField = (t, name, ok, what) =>
            {
                FieldInfo f = t == null ? null : t.GetField(name, Static);
                if (t != null && (f == null || !ok(f.FieldType))) problems.Add(t.FullName + "." + name + ": no public static " + what + " field");
            };

            Type post = type(PostProcessTypeName);
            if (post != null)
            {
                PropertyInfo scale = post.GetProperty("RenderTargetSizeScale", Static);
                if (scale == null || scale.PropertyType != typeof(float) || !scale.CanWrite)
                    problems.Add(PostProcessTypeName + ".RenderTargetSizeScale: no public static float property with a setter");
                PropertyInfo aa = post.GetProperty("RenderTargetAntialiasingSetting", Static);
                if (aa == null || !aa.PropertyType.IsEnum) problems.Add(PostProcessTypeName + ".RenderTargetAntialiasingSetting: no public static enum property");
                else
                {
                    foreach (string name in new[] { "None", "Multisample2x", "Multisample4x", "Multisample8x" })
                    {
                        if (Array.IndexOf(Enum.GetNames(aa.PropertyType), name) < 0) problems.Add(aa.PropertyType.FullName + " has no " + name);
                    }
                }
            }
            staticField(post, "RenderTargetSizeScaleOld", ft => ft == typeof(float), "float");
            staticField(type(GameOptionsTypeName), "LowResMaps", ft => ft == typeof(bool), "bool");
            Type world = type(WorldTypeName);
            staticField(world, "GlobalExcludeLayers", ft => typeof(IList<string>).IsAssignableFrom(ft), "IList<string>");
            if (world != null && world.GetProperty("UserPreferences", Instance) == null) problems.Add(WorldTypeName + ".UserPreferences: no public property");
            if (world != null && world.GetProperty("LocalStorage", Instance) == null) problems.Add(WorldTypeName + ".LocalStorage: no public property");
            Type storage = type(LocalStorageTypeName);
            if (storage != null && storage.GetMethod("Save", Instance, null, Type.EmptyTypes, null) == null) problems.Add(LocalStorageTypeName + ".Save() not found");
            Type prefs = type(PreferencesTypeName);
            staticField(prefs, "FpsValues", ft => typeof(IList<int>).IsAssignableFrom(ft), "IList<int>");
            if (prefs != null)
            {
                PropertyInfo max = prefs.GetProperty("MaxFramerate", Instance);
                if (max == null || max.PropertyType != typeof(int)) problems.Add(PreferencesTypeName + ".MaxFramerate: no public int property");
                if (GameEngineGraphics.Inherited(prefs, "NativeSetUserPreference", typeof(string), typeof(object)) == null)
                    problems.Add(PreferencesTypeName + ": no NativeSetUserPreference(string, object)");
                if (GameEngineGraphics.Inherited(prefs, "NativeSaveUserPreferences") == null) problems.Add(PreferencesTypeName + ": no NativeSaveUserPreferences()");
            }
            Type app = type(ApplicationTypeName);
            if (app != null)
            {
                if (app.GetProperty("Instance", Static) == null) problems.Add(ApplicationTypeName + ".Instance: no public static property");
                if (app.GetProperty("World", Instance) == null) problems.Add(ApplicationTypeName + ".World: no public property");
                if (app.GetMethod("ChangeGraphicQuality", Instance, null, new[] { typeof(string) }, null) == null)
                    problems.Add(ApplicationTypeName + ".ChangeGraphicQuality(string) not found (the level loads' scale reset)");
            }
            Type profile = type(ProfileComponentTypeName);
            FieldInfo options = profile == null ? null : profile.GetField("m_options", Instance);
            if (profile != null && (options == null || !typeof(IDictionary).IsAssignableFrom(options.FieldType)))
                problems.Add(ProfileComponentTypeName + ".m_options: no public dictionary field");
            MethodInfo dynamic = profile == null ? null : GameEngineGraphics.Inherited(profile, "CalculateDynamicRenderTargetScale");
            if (profile != null && (dynamic == null || dynamic.ReturnType != typeof(float)))
                problems.Add(ProfileComponentTypeName + ": no float CalculateDynamicRenderTargetScale() (the formula DynamicScaleOf copies)");
            Type info = type(PlatformInfoTypeName);
            if (info != null)
            {
                string[] names = { "DensityDpi", "ScreenPixelWidth", "ScreenPixelHeight", "ScreenInchWidth", "ScreenInchHeight" };
                Type[] types = { typeof(int), typeof(int), typeof(int), typeof(float), typeof(float) };
                for (int i = 0; i < names.Length; i++)
                {
                    PropertyInfo prop = info.GetProperty(names[i], Static);
                    if (prop == null || prop.PropertyType != types[i]) problems.Add(PlatformInfoTypeName + "." + names[i] + ": no public static " + types[i].Name + " property");
                }
            }
            Type appPrefs = type(AppPreferencesTypeName);
            if (appPrefs != null)
            {
                if (appPrefs.GetMethod("GetSecurePreferenceAsInt", Static, null, new[] { typeof(string), typeof(int) }, null) == null)
                    problems.Add(AppPreferencesTypeName + ".GetSecurePreferenceAsInt(string, int) not found");
                if (appPrefs.GetMethod("SetSecurePreference", Static, null, new[] { typeof(string), typeof(int) }, null) == null)
                    problems.Add(AppPreferencesTypeName + ".SetSecurePreference(string, int) not found");
            }
            return problems;
        }

        /// <summary>
        /// <see cref="IEngineGraphics"/> on the running game: Gameloft.Engine's PostProcessManager, World and GameApplication, Game's
        /// GameGameOptionsComponent and GamePreferencesComponent (AND-CS; the same members on PC), found among the loaded assemblies on
        /// every use until they are there. The stored index is the client World's (<c>GameApplication.Instance.World.UserPreferences</c>),
        /// written as the menu writes it (<c>NativeSetUserPreference</c>, which applies the limit, then <c>NativeSaveUserPreferences</c>).
        /// </summary>
        private sealed class GameEngineGraphics : IEngineGraphics
        {
            private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
            private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;

            private static Type EngineType(string name)
            {
                return LoadedTypes.Find(LoadedTypes.EngineAssembly, name);
            }

            private static Type GameType(string name)
            {
                return LoadedTypes.Find(LoadedTypes.GameAssembly, name);
            }

            private static Type PostProcess { get { return EngineType(PostProcessTypeName); } }

            private static PropertyInfo StaticProperty(Type type, string name)
            {
                return type == null ? null : type.GetProperty(name, Static);
            }

            private static FieldInfo StaticField(Type type, string name)
            {
                return type == null ? null : type.GetField(name, Static);
            }

            public float? RenderTargetSizeScale
            {
                get
                {
                    try
                    {
                        PropertyInfo p = StaticProperty(PostProcess, "RenderTargetSizeScale");
                        object v = p == null ? null : p.GetValue(null, null);
                        return v is float ? (float?)(float)v : null;
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }

            public bool SetRenderTargetSizeScale(float scale)
            {
                try
                {
                    Type type = PostProcess;
                    PropertyInfo p = StaticProperty(type, "RenderTargetSizeScale");
                    if (p == null || p.PropertyType != typeof(float) || !p.CanWrite) return false;
                    p.SetValue(null, scale, null);
                    FieldInfo old = StaticField(type, "RenderTargetSizeScaleOld");
                    if (old != null && old.FieldType == typeof(float)) old.SetValue(null, scale);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            public int? AntialiasingSamples
            {
                get
                {
                    try
                    {
                        PropertyInfo p = StaticProperty(PostProcess, "RenderTargetAntialiasingSetting");
                        object v = p == null ? null : p.GetValue(null, null);
                        if (v == null) return null;
                        switch (v.ToString())
                        {
                            case "None": return 0;
                            case "Multisample2x": return 2;
                            case "Multisample4x":
                            case "Multisample4xr": return 4;
                            case "Multisample8x": return 8;
                            case "Multisample16x": return 16;
                            default: return null;
                        }
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }

            public bool? LowResMaps
            {
                get
                {
                    try
                    {
                        FieldInfo f = StaticField(GameType(GameOptionsTypeName), "LowResMaps");
                        object v = f == null ? null : f.GetValue(null);
                        return v is bool ? (bool?)(bool)v : null;
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }

            public bool SetLowResMaps(bool value)
            {
                try
                {
                    FieldInfo f = StaticField(GameType(GameOptionsTypeName), "LowResMaps");
                    if (f == null || f.FieldType != typeof(bool)) return false;
                    f.SetValue(null, value);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            public IList<string> GlobalExcludeLayers
            {
                get
                {
                    try
                    {
                        FieldInfo f = StaticField(EngineType(WorldTypeName), "GlobalExcludeLayers");
                        return f == null ? null : f.GetValue(null) as IList<string>;
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }

            public IList<int> FpsValues
            {
                get
                {
                    try
                    {
                        FieldInfo f = StaticField(GameType(PreferencesTypeName), "FpsValues");
                        var list = f == null ? null : f.GetValue(null) as IList<int>;
                        return list == null ? null : new List<int>(list);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }

            /// <summary>The client World (<c>GameApplication.Instance.World</c>), or null before it exists.</summary>
            internal static object ClientWorld()
            {
                Type app = EngineType(ApplicationTypeName);
                PropertyInfo instance = StaticProperty(app, "Instance");
                object application = instance == null ? null : instance.GetValue(null, null);
                if (application == null) return null;
                PropertyInfo world = application.GetType().GetProperty("World", Instance);
                return world == null ? null : world.GetValue(application, null);
            }

            /// <summary>The client World's UserPreferences (a GamePreferencesComponent), or null before it exists.</summary>
            private static object Preferences()
            {
                object w = ClientWorld();
                if (w == null) return null;
                PropertyInfo prefs = w.GetType().GetProperty("UserPreferences", Instance);
                return prefs == null ? null : prefs.GetValue(w, null);
            }

            public int? MaxFramerateIndex
            {
                get
                {
                    try
                    {
                        object prefs = Preferences();
                        PropertyInfo p = prefs == null ? null : prefs.GetType().GetProperty("MaxFramerate", Instance);
                        object v = p == null ? null : p.GetValue(prefs, null);
                        return v is int ? (int?)(int)v : null;
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }

            public bool SetMaxFramerateIndex(int index)
            {
                try
                {
                    object prefs = Preferences();
                    if (prefs == null) return false;
                    MethodInfo set = Inherited(prefs.GetType(), "NativeSetUserPreference", typeof(string), typeof(object));
                    MethodInfo save = Inherited(prefs.GetType(), "NativeSaveUserPreferences");
                    if (set == null || save == null) return false;
                    set.Invoke(prefs, new object[] { "MaxFramerate", index });
                    save.Invoke(prefs, null);
                    return MaxFramerateIndex == index;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            /// <summary>A non-public instance method declared on <paramref name="type"/> or a base type, by exact parameters.</summary>
            internal static MethodInfo Inherited(Type type, string name, params Type[] parameters)
            {
                for (Type t = type; t != null; t = t.BaseType)
                {
                    MethodInfo m = t.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                                               null, parameters, null);
                    if (m != null) return m;
                }
                return null;
            }
        }
    }

    /// <summary>
    /// The engine statics the PERFORMANCE page reads and writes (the same members on both platforms, design §2): a seam, so
    /// <see cref="PerformancePrefs"/> runs on the game in the app and on a fake in the tests. Game thread; a getter returns null
    /// when its member is missing, a setter false.
    /// </summary>
    internal interface IEngineGraphics
    {
        /// <summary>Gameloft.Engine.PostProcessManager.RenderTargetSizeScale.</summary>
        float? RenderTargetSizeScale { get; }

        /// <summary>Sets RenderTargetSizeScale and RenderTargetSizeScaleOld (the Huawei-SDK profile reads the old one [G]).</summary>
        bool SetRenderTargetSizeScale(float scale);

        /// <summary>PostProcessManager.RenderTargetAntialiasingSetting as samples: 0 (None), 2, 4, 8.</summary>
        int? AntialiasingSamples { get; }

        /// <summary>Game.GameGameOptionsComponent.LowResMaps.</summary>
        bool? LowResMaps { get; }

        bool SetLowResMaps(bool value);

        /// <summary>Gameloft.Engine.World.GlobalExcludeLayers itself (changed in place), or null.</summary>
        IList<string> GlobalExcludeLayers { get; }

        /// <summary>Game.GamePreferencesComponent.FpsValues (as resolved from GameOptions <c>maxFramerateValues</c>), or null.</summary>
        IList<int> FpsValues { get; }

        /// <summary>The game's stored <c>MaxFramerate</c> index (LocalStorage), or null.</summary>
        int? MaxFramerateIndex { get; }

        bool SetMaxFramerateIndex(int index);
    }
}
