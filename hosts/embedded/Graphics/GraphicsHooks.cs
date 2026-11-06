// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using McvsOffline.Embedded.Admin.SettingsMenu;
using McvsOffline.Embedded.Graphics;
using McvsOffline.Server.Core;

namespace McvsOffline
{
    /// <summary>
    /// Entry points of patch 'graphics' (patcher/Patches/GraphicsPatch.cs) in <c>GameOptionPerformanceProfileComponent.OnCreate</c>
    /// of Gameloft.Engine.dll, the component of the GameDefinition that turns GameOptions_android.json into the engine's options:
    /// <list type="bullet">
    /// <item><see cref="GameOptionsJson"/> gets the JSON text between <c>AssetStream.ReadAllText</c> and
    /// <c>GameOptionManager.InitializeGameOption</c> (once per process: the native options are initialised once) and returns what
    /// the engine uses, rewritten by <see cref="GameOptionsProfile"/> for settings.json <c>graphics.profile</c> and, on Android, by
    /// <see cref="PerformancePrefs"/> for the PERFORMANCE page's <c>graphics.msaa</c> and FRAME RATE LIMIT list;</item>
    /// <item><see cref="GameOptionsApplied"/> gets <c>m_options</c> after <c>ApplyGameOptions</c> (every OnCreate) and logs the
    /// effective profile and options once as the "[boot] graphics:" line; on Android it then applies the page's SCREEN SCALE and LOW
    /// RESOLUTION MAPS after every call and schedules the FPS index migration once (docs/design/settings-menu.md §3.4).</item>
    /// </list>
    /// The signatures are part of the patch contract
    /// (GraphicsHooksContractTests). Both run on the game thread, are cheap and never throw (the patched IL guards them too).
    /// <para>On PC (<see cref="ConfigureWindows"/>, docs/design/pc-port.md §6.7) the text is GameOptions_windows.json, rewritten by
    /// <see cref="GameOptionsWindows"/> (the MEM rule keeps the levels' Physics layer), and <see cref="GameOptionsApplied"/> gets the
    /// component itself (PC has no <c>m_options</c>): the resolved options are read from <c>GameOptionManager</c> as the PC
    /// <c>ApplyGameOptions</c> reads them.</para>
    /// </summary>
    public static class GraphicsHooks
    {
        private static volatile string choice = GameOptionsProfile.Auto;
        private static volatile bool fullProfile;
        private static volatile Log log;
        private static volatile BootTrace boot;
        private static int jsonCalls;
        private static int appliedCalls;
        private static volatile string forcedProfile;
        private static volatile string rewriteSummary;
        private static volatile string effective;
        private static volatile bool windows;
        private static volatile bool keepPhysicsLayer = true;
        private static volatile string memRule;
        private static volatile string inGame;
        private static int inGameMarked;

        /// <summary>The PC in-game graphics options' type (<c>Game.dll</c>, <c>PC-EN/Game/GamePreferencesComponent.cs</c>), looked up only among the loaded assemblies.</summary>
        internal const string PreferencesTypeName = PreferencesType + ", " + PreferencesAssembly;

        /// <summary>The PC options-menu preferences the graphics lines show, in this order (LocalStorage member names).</summary>
        internal static readonly string[] InGameOptions = { "Performance", "RenderScale", "FPS", "Vsync", "FullScreen" };

        /// <summary>The most options the PC applied line lists (GameOptionManager has a few hundred).</summary>
        internal const int MaxNativeOptions = 200;

        /// <summary>Set by the embedded host once settings.json is read (before the game creates its GameDefinition).</summary>
        public static void Configure(string profileChoice, bool full, Log hostLog, BootTrace bootTrace)
        {
            choice = GameOptionsProfile.Normalize(profileChoice) ?? (profileChoice ?? GameOptionsProfile.Auto);
            fullProfile = full;
            log = hostLog;
            boot = bootTrace;
        }

        /// <summary>
        /// PC (EmbeddedHost when the platform is Windows): the text is GameOptions_windows.json, and with
        /// <paramref name="keepPhysics"/> (settings <c>embedded.physics.keepPhysicsLayer</c>) the MEM rule runs. Call after
        /// <see cref="Configure"/>.
        /// </summary>
        public static void ConfigureWindows(bool keepPhysics)
        {
            keepPhysicsLayer = keepPhysics;
            windows = true;
        }

        /// <summary>True after <see cref="ConfigureWindows"/>.</summary>
        public static bool IsWindows { get { return windows; } }

        /// <summary>The MEM rule's last result on PC ("on, removed 1", "off"), else null.</summary>
        public static string MemRule { get { return memRule; } }

        private static string OptionsFile { get { return windows ? GameOptionsWindows.FileName : "GameOptions_android.json"; } }

        /// <summary>
        /// Called by the patched <c>OnCreate</c> with the text of GameOptions_android.json (GameOptions_windows.json on PC) before
        /// the native options are initialised; returns the text the engine uses (never null when the input is not).
        /// </summary>
        public static string GameOptionsJson(string json)
        {
            try
            {
                int n = Interlocked.Increment(ref jsonCalls);
                if (json == null) return null;
                GameOptionsRewrite rewrite = windows ? GameOptionsWindows.Rewrite(json, choice, fullProfile, keepPhysicsLayer)
                                                     : GameOptionsProfile.Rewrite(json, choice, fullProfile);
                if (rewrite.MemRule != null) memRule = rewrite.MemRule;
                forcedProfile = rewrite.Profile;
                rewriteSummary = rewrite.Summary;
                string text = rewrite.Json ?? json;
                string performance = windows ? null : RewritePerformance(ref text);
                bool changed = !string.Equals(text, json, StringComparison.Ordinal);
                if (n <= 3) Mark("graphics: " + rewrite.Summary + (changed ? "" : " (text unchanged)") + (performance != null ? "; " + performance : ""));
                return text;
            }
            catch (Exception)
            {
                return json;
            }
        }

        /// <summary>
        /// Android: the PERFORMANCE page's start-time values on top of the profile rewrite (<see cref="PerformancePrefs.RewriteJson"/>:
        /// <c>graphics.msaa</c>, and the longer FRAME RATE LIMIT list while <c>admin.settingsMenu.performance</c> is on), from the
        /// host's copy of the settings; the phrase for the boot line, or null before <see cref="PerformancePrefs.Configure"/>.
        /// </summary>
        private static string RewritePerformance(ref string text)
        {
            IPerformanceSettings values = PerformancePrefs.HostValues;
            if (values == null) return null;
            string phrase;
            string rewritten = PerformancePrefs.RewriteJson(text, values.Msaa, PerformancePrefs.HostPerformanceSwitch, out phrase);
            if (rewritten != null) text = rewritten;
            return phrase;
        }

        /// <summary>
        /// Called by the patched <c>OnCreate</c> after <c>ApplyGameOptions</c> with the component's <c>m_options</c>
        /// (<c>Dictionary&lt;string, object&gt;</c> of every resolved option) or null; on PC with the component itself (no
        /// <c>m_options</c>), whose options are then read from <c>GameOptionManager</c>.
        /// </summary>
        public static void GameOptionsApplied(object options)
        {
            try
            {
                int n = Interlocked.Increment(ref appliedCalls);
                if (n == 1)
                {
                    IDictionary resolved = options as IDictionary;
                    if (resolved == null && options != null) resolved = ReadNativeOptions(FindType(options, "Gameloft.Engine.GameOptionManager"),
                                                                                            FindType(options, "Gameloft.Engine.MoreMarshal"));
                    string line = DescribeApplied(resolved, NativeProfiles());
                    effective = line;
                    Mark(line);
                    if (windows) CaptureInGame();
                }
                // After every ApplyGameOptions (each one adds its layer entry to World.GlobalExcludeLayers and builds a new m_options).
                if (!windows) ApplyPerformance(options as IDictionary, n == 1);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Android, after the profile set its values: the PERFORMANCE page's SCREEN SCALE (also into <paramref name="options"/>, which
        /// every level load reads again) and LOW RESOLUTION MAPS from the host's copy of the settings, logged the first time; the FPS
        /// index migration is scheduled then (<see cref="PerformancePrefs.ScheduleFpsMigration"/>). Nothing before
        /// <see cref="PerformancePrefs.Configure"/>.
        /// </summary>
        private static void ApplyPerformance(IDictionary options, bool first)
        {
            IPerformanceSettings values = PerformancePrefs.HostValues;
            if (values == null) return;
            string line = PerformancePrefs.Apply(PerformancePrefs.Engine, values, options, false);
            if (!first) return;
            if (line != null) Mark("graphics: " + line);
            PerformancePrefs.ScheduleFpsMigration(text => Mark("graphics: " + text));
        }

        /// <summary>One status-line part: the setting and what the engine got.</summary>
        public static string Describe()
        {
            string c = choice;
            string frame = windows ? "; " + (FrameRateSummary ?? "frame rate limit not passed through patch 'fpslimit' yet") : "";
            if (jsonCalls == 0 && appliedCalls == 0)
                return "graphics: graphics.profile " + c + "; the game has not read " + OptionsFile + " through patch 'graphics' (yet)" + frame;
            string forced = forcedProfile;
            string mem = memRule;
            string game = windows ? CaptureInGame() : null;
            return "graphics: graphics.profile " + c + (forced == null ? " (the game's own choice)" : ", forced " + forced) +
                   (fullProfile ? ", fullProfile" : "") + (mem != null ? ", MEM rule " + mem : "") + "; " + (NativeSummary ?? "native profile unknown") +
                   (windows ? "; " + (game ?? "in-game graphics options not read yet") : "") +
                   frame +
                   (!windows && PerformancePrefs.Summary != null ? "; " + PerformancePrefs.Summary : "");
        }

        /// <summary>
        /// PC: "in-game graphics quality Performance 2 (High), RenderScale 0.8, FPS 60, Vsync true, FullScreen true", or null until
        /// the client World's <c>GamePreferencesComponent</c> has loaded them. Performance and RenderScale are the start values the
        /// component keeps (<c>initPerformance</c>, <c>initRenderScale</c>): the native GameOptions init reads <c>Performance</c>
        /// once per process, so those are the ones this run's profiles came from, even when the line is first read after the
        /// player changed Settings › Graphics (then ", changed in-game to N (applies at the next start)" follows). FPS, Vsync and
        /// FullScreen apply at once and are the values when the line was first read. Read once.
        /// </summary>
        public static string InGame { get { return inGame; } }

        /// <summary>
        /// PC: reads <see cref="InGame"/> the first time <c>GamePreferencesComponent</c> is ready (from <see cref="Describe"/>, i.e.
        /// the status line and the settings page, or from <see cref="GameOptionsApplied"/>) and writes it once as a
        /// "[boot] graphics:" line. Never throws.
        /// </summary>
        private static string CaptureInGame()
        {
            string known = inGame;
            if (known != null) return known;
            try
            {
                return CaptureInGame(LoadedType(PreferencesAssembly, PreferencesType));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private const string PreferencesAssembly = "Game";
        private const string PreferencesType = "Game.GamePreferencesComponent";

        /// <summary>
        /// <paramref name="typeName"/> from an assembly named <paramref name="assemblyName"/> that is already loaded, or null: the
        /// status thread must never be the one that makes the runtime load Game.dll (its load and redirect belong to the game).
        /// </summary>
        private static Type LoadedType(string assemblyName, string typeName)
        {
            return Embedded.LoadedTypes.Find(assemblyName, typeName);
        }

        /// <summary><see cref="CaptureInGame()"/> with the preferences type given (tests pass a look-alike).</summary>
        internal static string CaptureInGame(Type preferences)
        {
            string known = inGame;
            if (known != null) return known;
            try
            {
                string line = DescribeInGame(ReadInGamePreferences(preferences));
                if (line == null) return null;
                inGame = line;
                if (Interlocked.Exchange(ref inGameMarked, 1) == 0) Mark("graphics: " + line);
                return line;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The PC options-menu preferences (<see cref="InGameOptions"/>) of <c>GamePreferencesComponent.instance</c>
        /// (<c>PC-EN/Game/GamePreferencesComponent.cs:101-145</c>) as name → text (the current values), plus the start values
        /// "Performance.start" and "RenderScale.start" (<c>initPerformance</c>, <c>initRenderScale</c>, set at the end of
        /// <c>LoadPreferencesWindows</c>) and "Performance.set": whether the World's LocalStorage (<c>MyLocalStorage</c>) had the
        /// member at start (the native GameOptions init applies it only then, docs/design/pc-port.md §6.7). "true"/"false" from
        /// <c>HasMember</c> while Performance is still the start value; once the menu has changed it (which writes the member)
        /// "true" for a start value other than 0 (an absent member loads as 0) and "unknown" for 0. Null while <paramref name="preferences"/> or the instance is null, before its
        /// <c>LoadPreferencesWindows</c> has finished (<c>initPerformance</c> is still -1), and for a World without a LocalStorage
        /// (the server Worlds). Never throws.
        /// </summary>
        internal static IDictionary<string, string> ReadInGamePreferences(Type preferences)
        {
            if (preferences == null) return null;
            try
            {
                FieldInfo instance = preferences.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                object component = instance == null ? null : instance.GetValue(null);
                if (component == null) return null;
                FieldInfo init = preferences.GetField("initPerformance", BindingFlags.NonPublic | BindingFlags.Instance);
                object started = init == null ? null : init.GetValue(component);
                if (!(started is int) || (int)started == -1) return null;
                object storage = LocalStorageOf(component);
                if (storage == null) return null;
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string name in InGameOptions)
                {
                    PropertyInfo p = preferences.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    object value = p == null || p.GetIndexParameters().Length != 0 ? null : p.GetValue(component, null);
                    if (value != null) result[name] = Show(value);
                }
                result["Performance.start"] = Show(started);
                FieldInfo initScale = preferences.GetField("initRenderScale", BindingFlags.NonPublic | BindingFlags.Instance);
                object startScale = initScale == null ? null : initScale.GetValue(component);
                if (startScale != null) result["RenderScale.start"] = Show(startScale);
                bool changed = !string.Equals(Get(result, "Performance"), result["Performance.start"], StringComparison.Ordinal);
                MethodInfo has = storage.GetType().GetMethod("HasMember", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                object set = has == null ? null : has.Invoke(storage, new object[] { "Performance" });
                if (changed) result["Performance.set"] = (int)started != 0 ? "true" : "unknown";
                else if (set is bool) result["Performance.set"] = (bool)set ? "true" : "false";
                return result;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary><c>component.World.LocalStorage</c> (a <c>LocalStorageComponent</c>), or null.</summary>
        private static object LocalStorageOf(object component)
        {
            PropertyInfo world = component.GetType().GetProperty("World", BindingFlags.Public | BindingFlags.Instance);
            object w = world == null ? null : world.GetValue(component, null);
            if (w == null) return null;
            PropertyInfo storage = w.GetType().GetProperty("LocalStorage", BindingFlags.Public | BindingFlags.Instance);
            return storage == null ? null : storage.GetValue(w, null);
        }

        /// <summary>
        /// "in-game graphics quality Performance 2 (High), RenderScale 0.8, FPS 60, Vsync true, FullScreen true" with the start
        /// Performance and RenderScale (the current ones when no start value is given), "Performance not set (the game's own
        /// choice)" when the LocalStorage had no such member at start, "Performance 0 (Low) or not set (the game's own choice)"
        /// when that cannot be told any more, and ", changed in-game to N (applies at the next start)" (RenderScale: "(applied at
        /// once)") after a value the menu changed since; null for null.
        /// </summary>
        internal static string DescribeInGame(IDictionary<string, string> prefs)
        {
            if (prefs == null) return null;
            var sb = new StringBuilder("in-game graphics quality ");
            string now = Get(prefs, "Performance");
            string performance = Get(prefs, "Performance.start") ?? now;
            string set = Get(prefs, "Performance.set");
            if (set == "false") sb.Append("Performance not set (the game's own choice)");
            else
            {
                sb.Append("Performance ").Append(performance ?? "?").Append(QualityName(performance));
                if (set == "unknown") sb.Append(" or not set (the game's own choice)");
            }
            if (now != null && performance != null && now != performance)
                sb.Append(", changed in-game to ").Append(now).Append(QualityName(now)).Append(" (applies at the next start)");
            for (int i = 1; i < InGameOptions.Length; i++)
            {
                string value = Get(prefs, InGameOptions[i]);
                string start = Get(prefs, InGameOptions[i] + ".start");
                if (start != null && value != null && !SameScale(start, value))
                    sb.Append(", ").Append(InGameOptions[i]).Append(' ').Append(start).Append(", changed in-game to ").Append(value).Append(" (applied at once)");
                else if (start != null || value != null) sb.Append(", ").Append(InGameOptions[i]).Append(' ').Append(start ?? value);
            }
            return sb.ToString();
        }

        /// <summary>Two RenderScale texts compared as the game does (<c>OnPreferencesSavedWindows</c>: <c>ToString("0.00")</c>).</summary>
        private static bool SameScale(string a, string b)
        {
            float x, y;
            if (float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out x) && float.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                return x.ToString("0.00", CultureInfo.InvariantCulture) == y.ToString("0.00", CultureInfo.InvariantCulture);
            return a == b;
        }

        /// <summary>Settings › Graphics quality as the menu stores it: 0 Low, 1 Mid, 2 High (default render scales 0.4/0.6/0.8).</summary>
        private static string QualityName(string performance)
        {
            switch (performance)
            {
                case "0": return " (Low)";
                case "1": return " (Mid)";
                case "2": return " (High)";
                default: return "";
            }
        }

        /// <summary>The summary of the last rewrite (null before the hook ran).</summary>
        public static string RewriteSummary { get { return rewriteSummary; } }

        /// <summary>The "[boot] graphics:" effective-options line (null before the hook ran).</summary>
        public static string Effective { get { return effective; } }

        private static volatile string nativeSummary;

        /// <summary>"GPU profile HighGPU (device optimum MidGPU)", read once after the options were applied.</summary>
        public static string NativeSummary { get { return nativeSummary; } }

        /// <summary>
        /// "graphics: effective GPU profile HighGPU (...); N options: a=1, b=true, ..." from the resolved options (sorted by name)
        /// and what the native side reports (<paramref name="native"/>: name → value, may be empty).
        /// </summary>
        internal static string DescribeApplied(IDictionary options, IDictionary<string, string> native)
        {
            var sb = new StringBuilder("graphics: effective ");
            string loaded = Get(native, "loaded");
            string optimal = Get(native, "optimal");
            string summary = "GPU profile " + (loaded ?? "unknown") + (optimal != null ? " (device optimum " + optimal + ")" : "");
            // PC: the native GameOptions init applies MyLocalStorage › Performance (the in-game Graphics quality) on top of the
            // selection; without a forced profile only that makes the loaded profile differ from the optimum (WR2 boots 2-8).
            if (windows && loaded != null && optimal != null && loaded != optimal && forcedProfile == null)
                summary += ", overridden by the in-game graphics quality";
            string memProfile = Get(native, "memLoaded");
            if (memProfile != null) summary += ", MEM profile " + memProfile;
            nativeSummary = summary;
            sb.Append(summary);
            sb.Append(", settings graphics.profile ").Append(choice);
            string forced = forcedProfile;
            if (forced != null) sb.Append(" (forced ").Append(forced).Append(fullProfile ? ", fullProfile" : "").Append(')');
            string gpu = Get(native, "gpu");
            string model = Get(native, "model");
            if (gpu != null || model != null) sb.Append("; device GPU '").Append(gpu ?? "?").Append("', model '").Append(model ?? "?").Append('\'');
            string selected = Get(native, "selectedProfile");
            if (selected != null) sb.Append("; in-game graphic setting GameOptions_SelectedProfile ").Append(selected);
            string game = Get(native, "inGame");
            if (game != null) sb.Append("; ").Append(game);
            string memory = Get(native, "memory");
            string level = Get(native, "featureLevel");
            string cores = Get(native, "cores");
            if (memory != null || level != null || cores != null)
                sb.Append("; device memory ").Append(memory ?? "?").Append(", feature level ").Append(level ?? "?").Append(", cores ").Append(cores ?? "?");
            string mem = memRule;
            if (mem != null) sb.Append("; MEM rule ").Append(mem);
            if (options == null)
            {
                sb.Append(windows ? "; options unknown (GameOptionManager not readable)" : "; options unknown (m_options is null)");
                return sb.ToString();
            }
            if (windows)
            {
                object layers = options.Contains("excludedLoadingLayers") ? options["excludedLoadingLayers"] : null;
                bool physics = layers is string && Array.IndexOf(((string)layers).Split(' '), "Physics") >= 0;
                sb.Append("; excludedLoadingLayers ").Append(Show(layers ?? "")).Append(physics ? " (Physics excluded!)" : "");
            }
            var keys = new List<string>();
            foreach (object k in options.Keys)
            {
                if (k != null) keys.Add(Convert.ToString(k, CultureInfo.InvariantCulture));
            }
            keys.Sort(StringComparer.Ordinal);
            sb.Append("; ").Append(keys.Count.ToString(CultureInfo.InvariantCulture)).Append(" options:");
            for (int i = 0; i < keys.Count; i++)
            {
                sb.Append(i == 0 ? " " : ", ").Append(keys[i]).Append('=').Append(Show(options[keys[i]]));
            }
            return sb.ToString();
        }

        private static string Show(object value)
        {
            if (value == null) return "null";
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is float) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            var s = value as string;
            if (s != null) return "'" + s + "'";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string Get(IDictionary<string, string> map, string key)
        {
            string v;
            return map != null && map.TryGetValue(key, out v) && !string.IsNullOrEmpty(v) ? v : null;
        }

        /// <summary>
        /// What the engine reports, by reflection on Gameloft.Engine (absent in tests): GameOptionManager.GetLoadedProfileName("GPU")
        /// and GetOptimalProfileName("GPU") (native strings made with MoreMarshal_CreateNativeString, released with
        /// DestroyNativeString as the game does for GetOptionString), DeviceGPU, DeviceModel, and the in-game graphic setting: on
        /// Android AppPreferences "GameOptions_SelectedProfile" (GamePreferencesComponent.NativeChangeGraphicSetting); on PC, which
        /// keeps it in LocalStorage "Performance" instead, <see cref="InGame"/> when it is already readable. Never throws.
        /// </summary>
        private static Dictionary<string, string> NativeProfiles()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                Type manager = Embedded.LoadedTypes.EngineType("Gameloft.Engine.GameOptionManager");
                Type marshal = Embedded.LoadedTypes.EngineType("Gameloft.Engine.MoreMarshal");
                if (manager == null) return result;
                result["loaded"] = NativeString(manager, marshal, "GetLoadedProfileName", "GPU");
                result["optimal"] = NativeString(manager, marshal, "GetOptimalProfileName", "GPU");
                result["gpu"] = StaticString(manager, "DeviceGPU");
                result["model"] = StaticString(manager, "DeviceModel");
                if (windows)
                {
                    result["memLoaded"] = NativeString(manager, marshal, "GetLoadedProfileName", "MEM");
                    result["memory"] = StaticString(manager, "DeviceTotalMemory");
                    result["featureLevel"] = StaticString(manager, "DeviceGraphicsFeatureLevel");
                    result["cores"] = StaticString(manager, "DeviceCPUNumCores");
                    string game = CaptureInGame();
                    if (game != null) result["inGame"] = game;
                    return result;
                }
                Type prefs = Embedded.LoadedTypes.EngineType("Gameloft.Engine.AppPreferences");
                MethodInfo getInt = prefs == null ? null
                    : prefs.GetMethod("GetSecurePreferenceAsInt", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(int) }, null);
                if (getInt != null)
                {
                    object v = getInt.Invoke(null, new object[] { "GameOptions_SelectedProfile", 0 });
                    if (v != null) result["selectedProfile"] = Convert.ToString(v, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception)
            {
            }
            return result;
        }

        private static string NativeString(Type manager, Type marshal, string method, string argument)
        {
            try
            {
                MethodInfo get = manager.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                MethodInfo toString = marshal == null ? null
                    : marshal.GetMethod("NativeStringToString", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(IntPtr) }, null);
                MethodInfo destroy = marshal == null ? null
                    : marshal.GetMethod("DestroyNativeString", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(IntPtr) }, null);
                if (get == null || toString == null || destroy == null || get.ReturnType != typeof(IntPtr)) return null;
                var ptr = (IntPtr)get.Invoke(null, new object[] { argument });
                if (ptr == IntPtr.Zero) return null;
                try
                {
                    return toString.Invoke(null, new object[] { ptr }) as string;
                }
                finally
                {
                    destroy.Invoke(null, new object[] { ptr });
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string StaticString(Type type, string property)
        {
            try
            {
                PropertyInfo p = type.GetProperty(property, BindingFlags.Public | BindingFlags.Static);
                object value = p == null ? null : p.GetValue(null, null);
                return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The type <paramref name="fullName"/> from the assembly of <paramref name="component"/>'s type or a base type.</summary>
        private static Type FindType(object component, string fullName)
        {
            for (Type t = component.GetType(); t != null; t = t.BaseType)
            {
                Type found = t.Assembly.GetType(fullName, false, false);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// The resolved options as the PC <c>ApplyGameOptions</c> reads them (<c>PC-EN/GameOptionPerformanceProfileComponent.cs:37-77</c>):
        /// for Int, Float, Boolean and String, <c>Get&lt;T&gt;OptionsCount()</c>, <c>Get&lt;T&gt;OptionName(i)</c> (a native string,
        /// released with <c>MoreMarshal.DestroyNativeString</c>) and <c>GetOption&lt;T&gt;(name)</c> (a native string for String); at
        /// most <see cref="MaxNativeOptions"/> entries. Null when the manager is not there. Never throws.
        /// </summary>
        internal static IDictionary ReadNativeOptions(Type manager, Type marshal)
        {
            if (manager == null) return null;
            var options = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                foreach (string kind in new[] { "Int", "Float", "Boolean", "String" })
                {
                    MethodInfo count = manager.GetMethod("Get" + kind + "OptionsCount", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    MethodInfo name = manager.GetMethod("Get" + kind + "OptionName", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
                    MethodInfo value = manager.GetMethod("GetOption" + kind, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                    if (count == null || name == null || value == null) continue;
                    int n = Convert.ToInt32(count.Invoke(null, null), CultureInfo.InvariantCulture);
                    for (int i = 0; i < n && options.Count < MaxNativeOptions; i++)
                    {
                        string option = NameAt(name, marshal, i);
                        if (string.IsNullOrEmpty(option) || options.ContainsKey(option)) continue;
                        options[option] = kind == "String" ? NativeString(manager, marshal, "GetOptionString", option)
                                                           : value.Invoke(null, new object[] { option });
                    }
                }
            }
            catch (Exception)
            {
            }
            return options;
        }

        private static string NameAt(MethodInfo name, Type marshal, int index)
        {
            object raw = name.Invoke(null, new object[] { index });
            var text = raw as string;
            if (text != null || !(raw is IntPtr)) return text;
            var ptr = (IntPtr)raw;
            if (ptr == IntPtr.Zero || marshal == null) return null;
            MethodInfo toString = marshal.GetMethod("NativeStringToString", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(IntPtr) }, null);
            MethodInfo destroy = marshal.GetMethod("DestroyNativeString", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(IntPtr) }, null);
            if (toString == null) return null;
            try
            {
                return toString.Invoke(null, new object[] { ptr }) as string;
            }
            finally
            {
                if (destroy != null) destroy.Invoke(null, new object[] { ptr });
            }
        }

        private static void Mark(string text)
        {
            BootTrace b = boot;
            if (b != null)
            {
                b.Mark(text);
                return;
            }
            Log l = log;
            if (l != null) l.Info(text);
            else SelfLog.Write(SelfLog.Info, "[boot] " + text);
        }

        // ------------------------------------------------------------------ frame rate (patch 'fpslimit', PC, build 27)

        private static readonly object frameGate = new object();
        private static volatile Func<object> frameSetting;
        private static volatile Func<Action, bool> framePost;
        private static bool frameGameKnown;
        private static float frameGame;
        private static float frameLimit;
        private static string frameChoice;
        private static string frameLine;

        /// <summary>The game's VSYNC (LocalStorage "Vsync" of the client World's preferences), or null (tests replace it).</summary>
        internal static Func<bool?> VsyncReader = ReadVsync;

        /// <summary>Sets <c>GameApplication.Instance.WCPFramerateLimit</c> on the game thread (tests replace it).</summary>
        internal static Action<float> WcpLimitSetter = SetWcpLimit;

        /// <summary>
        /// PC (EmbeddedHost): where <c>graphics.pcFpsLimit</c> is read (the host's copy at start, the server's, which the settings
        /// page saves, once it runs) and how a change reaches the game thread (<c>GameApplication.Context</c>; null: not yet).
        /// </summary>
        public static void ConfigureFrameRate(Func<object> setting, Func<Action, bool> post)
        {
            frameSetting = setting;
            framePost = post;
        }

        /// <summary>
        /// Called by the patched <c>GamePreferencesComponent</c> (patch 'fpslimit', <c>Game.McvsFrameRate.Limit</c>) on the game
        /// thread with the value the game is about to give <c>GameApplication.WCPFramerateLimit</c>: the start value
        /// (<c>LoadPreferencesWindows</c>) and every FPS LIMIT change (<c>OnPreferencesChangedWindows</c>). Returns the limit the
        /// pacer gets (<see cref="PcFrameRate.Resolve"/>): with <c>graphics.pcFpsLimit</c> "game" (the default) the game's own value
        /// bit for bit (30 to 120; only a NaN, infinite, zero or negative one is replaced), otherwise the setting's. Logs one
        /// "graphics: frame rate limit ..." line whenever the limit, the in-game value, the setting or VSYNC differ from the last
        /// line. Never throws (the game's value on any failure).
        /// </summary>
        public static float FrameRateLimit(float gameValue)
        {
            try
            {
                Func<object> read = frameSetting;
                object raw = read == null ? null : read();
                string choice = PcFrameRate.Normalize(raw);
                float limit = PcFrameRate.Resolve(gameValue, choice);
                lock (frameGate)
                {
                    frameGameKnown = true;
                    frameGame = gameValue;
                    frameLimit = limit;
                    frameChoice = choice;
                }
                ReportFrameRate(gameValue, raw, choice, limit, null);
                return limit;
            }
            catch (Exception)
            {
                return gameValue;
            }
        }

        /// <summary>
        /// Settings page (the server's settings.json saved <c>graphics.pcFpsLimit</c>): the new limit goes to the pacer at once, on the
        /// game thread, from the in-game value the game last passed through <see cref="FrameRateLimit"/>. Before that first call
        /// (the game has not set its limit yet, or Game.dll lacks patch 'fpslimit') nothing is set: the value applies when the game
        /// sets its limit. Never throws.
        /// </summary>
        public static void FrameRateSettingChanged()
        {
            try
            {
                Func<object> read = frameSetting;
                object raw = read == null ? null : read();
                bool known;
                lock (frameGate) known = frameGameKnown;
                if (!known)
                {
                    Info("graphics: settings " + PcFrameRate.SettingsKey + " " + ShowSetting(raw) + " saved; it applies when the game sets its " +
                         "frame rate limit (at start and when FPS LIMIT changes; needs patch 'fpslimit')");
                    return;
                }
                Func<Action, bool> post = framePost;
                if (post == null || !post(ApplyFrameRateSetting))
                    Warn("graphics: settings " + PcFrameRate.SettingsKey + " " + ShowSetting(raw) + " saved, but the game thread could not be " +
                         "reached; it applies at the next start or FPS LIMIT change");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>On the game thread: the setting as it is now, over the game's last value, to the pacer.</summary>
        private static void ApplyFrameRateSetting()
        {
            Func<object> read = frameSetting;
            object raw = read == null ? null : read();
            string choice = PcFrameRate.Normalize(raw);
            float game;
            lock (frameGate) game = frameGame;
            float limit = PcFrameRate.Resolve(game, choice);
            try
            {
                WcpLimitSetter(limit);
            }
            catch (Exception ex)
            {
                Warn("graphics: the frame rate limit " + limit.ToString("R", CultureInfo.InvariantCulture) + " could not be set (" +
                     ex.GetType().Name + ": " + ex.Message + "); it applies at the next start or FPS LIMIT change");
                return;
            }
            lock (frameGate)
            {
                frameLimit = limit;
                frameChoice = choice;
            }
            ReportFrameRate(game, raw, choice, limit, " (from the settings page)");
        }

        private static void ReportFrameRate(float game, object raw, string choice, float limit, string suffix)
        {
            bool? vsync = null;
            try
            {
                Func<bool?> reader = VsyncReader;
                if (reader != null) vsync = reader();
            }
            catch (Exception)
            {
            }
            string line = "graphics: " + PcFrameRate.Describe(game, raw, choice, limit, vsync);
            lock (frameGate)
            {
                if (line == frameLine) return;
                frameLine = line;
            }
            Info(line + (suffix ?? ""));
        }

        /// <summary>
        /// "frame rate limit 144 from settings graphics.pcFpsLimit (in-game FPS LIMIT 60 not used); VSYNC on: ..." for the status
        /// block, or null before the game passed its limit through patch 'fpslimit'.
        /// </summary>
        public static string FrameRateSummary
        {
            get
            {
                string line;
                lock (frameGate) line = frameLine;
                return line == null ? null : line.Substring("graphics: ".Length);
            }
        }

        /// <summary>
        /// "; pacer limit 144 (settings graphics.pcFpsLimit)" for the bots' world line while the setting replaces the in-game value,
        /// else null.
        /// </summary>
        internal static string FrameRateOverrideNote()
        {
            lock (frameGate)
            {
                if (!frameGameKnown || !PcFrameRate.Overrides(frameChoice)) return null;
                return "; pacer limit " + frameLimit.ToString("R", CultureInfo.InvariantCulture) + " (settings " + PcFrameRate.SettingsKey + ")";
            }
        }

        private static string ShowSetting(object raw)
        {
            return raw == null ? PcFrameRate.Game + " (not set)" : "'" + Convert.ToString(raw, CultureInfo.InvariantCulture) + "'";
        }

        /// <summary><c>GameApplication.Instance.WCPFramerateLimit = limit</c> by reflection on the loaded engine (never a load).</summary>
        private static void SetWcpLimit(float limit)
        {
            Type app = Embedded.LoadedTypes.EngineType("Gameloft.Engine.GameApplication");
            if (app == null) throw new InvalidOperationException("Gameloft.Engine.GameApplication is not loaded");
            PropertyInfo instance = app.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            object application = instance == null ? null : instance.GetValue(null, null);
            if (application == null) throw new InvalidOperationException("GameApplication.Instance is null");
            PropertyInfo wcp = app.GetProperty("WCPFramerateLimit", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo set = wcp == null ? null : wcp.GetSetMethod();
            if (set == null) throw new InvalidOperationException("GameApplication has no WCPFramerateLimit setter");
            set.Invoke(application, new object[] { limit });
        }

        /// <summary>
        /// The game's VSYNC as its LocalStorage holds it: <c>GamePreferencesComponent.instance.World.LocalStorage</c>'s JObject
        /// <c>mData</c>, <c>Property("Vsync").Value</c> as text ("True"/"False"), true when the member is missing (the game's
        /// default; it writes true then). During <c>LoadPreferencesWindows</c> the <c>Vsync</c> property is not loaded yet, the
        /// member is (every VSYNC change writes it). No generic reflection: the Android BCL stubs MakeGenericMethod, and this file
        /// is compiled there too. Null when it cannot be read.
        /// </summary>
        internal static bool? ReadVsync()
        {
            return ReadVsync(LoadedType(PreferencesAssembly, PreferencesType));
        }

        /// <summary><see cref="ReadVsync()"/> with the preferences type given (tests pass a look-alike).</summary>
        internal static bool? ReadVsync(Type preferences)
        {
            try
            {
                if (preferences == null) return null;
                FieldInfo instance = preferences.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                object component = instance == null ? null : instance.GetValue(null);
                if (component == null) return null;
                object storage = LocalStorageOf(component);
                if (storage == null) return null;
                FieldInfo dataField = null;
                for (Type t = storage.GetType(); t != null && dataField == null; t = t.BaseType)
                    dataField = t.GetField("mData", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                object data = dataField == null ? null : dataField.GetValue(storage);
                if (data == null) return null;
                MethodInfo property = data.GetType().GetMethod("Property", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (property == null) return null;
                object member = property.Invoke(data, new object[] { "Vsync" });
                if (member == null) return true;
                PropertyInfo valueProperty = member.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                object value = valueProperty == null ? null : valueProperty.GetValue(member, null);
                bool parsed;
                return value != null && bool.TryParse(value.ToString(), out parsed) ? (bool?)parsed : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Info(string text)
        {
            Log l = log;
            if (l != null) l.Info(text);
            else SelfLog.Write(SelfLog.Info, text);
        }

        private static void Warn(string text)
        {
            Log l = log;
            if (l != null) l.Warn(text);
            else SelfLog.Write(SelfLog.Warn, text);
        }

        /// <summary>Tests: back to the state before <see cref="Configure"/>.</summary>
        internal static void ResetForTests()
        {
            choice = GameOptionsProfile.Auto;
            fullProfile = false;
            log = null;
            boot = null;
            jsonCalls = 0;
            appliedCalls = 0;
            forcedProfile = null;
            rewriteSummary = null;
            effective = null;
            nativeSummary = null;
            windows = false;
            keepPhysicsLayer = true;
            memRule = null;
            inGame = null;
            inGameMarked = 0;
            frameSetting = null;
            framePost = null;
            lock (frameGate)
            {
                frameGameKnown = false;
                frameGame = 0f;
                frameLimit = 0f;
                frameChoice = null;
                frameLine = null;
            }
            VsyncReader = ReadVsync;
            WcpLimitSetter = SetWcpLimit;
            PerformancePrefs.ResetForTests();
        }
    }
}
