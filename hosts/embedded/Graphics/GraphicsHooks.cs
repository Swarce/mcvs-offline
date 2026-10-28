// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
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
    /// the engine uses, rewritten by <see cref="GameOptionsProfile"/> for settings.json <c>graphics.profile</c>;</item>
    /// <item><see cref="GameOptionsApplied"/> gets <c>m_options</c> after <c>ApplyGameOptions</c> (every OnCreate) and logs the
    /// effective profile and options once as the "[boot] graphics:" line.</item>
    /// </list>
    /// The signatures are part of the patch contract
    /// (GraphicsHooksContractTests). Both run on the game thread, are cheap and never throw (the patched IL guards them too).
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

        /// <summary>Set by the embedded host once settings.json is read (before the game creates its GameDefinition).</summary>
        public static void Configure(string profileChoice, bool full, Log hostLog, BootTrace bootTrace)
        {
            choice = GameOptionsProfile.Normalize(profileChoice) ?? (profileChoice ?? GameOptionsProfile.Auto);
            fullProfile = full;
            log = hostLog;
            boot = bootTrace;
        }

        /// <summary>
        /// Called by the patched <c>OnCreate</c> with the text of GameOptions_android.json before the native options are
        /// initialised; returns the text the engine uses (never null when the input is not).
        /// </summary>
        public static string GameOptionsJson(string json)
        {
            try
            {
                int n = Interlocked.Increment(ref jsonCalls);
                if (json == null) return null;
                GameOptionsRewrite rewrite = GameOptionsProfile.Rewrite(json, choice, fullProfile);
                forcedProfile = rewrite.Profile;
                rewriteSummary = rewrite.Summary;
                if (n <= 3) Mark("graphics: " + rewrite.Summary + (rewrite.Changed ? "" : " (text unchanged)"));
                return rewrite.Json ?? json;
            }
            catch (Exception)
            {
                return json;
            }
        }

        /// <summary>
        /// Called by the patched <c>OnCreate</c> after <c>ApplyGameOptions</c> with the component's <c>m_options</c>
        /// (<c>Dictionary&lt;string, object&gt;</c> of every resolved option) or null.
        /// </summary>
        public static void GameOptionsApplied(object options)
        {
            try
            {
                int n = Interlocked.Increment(ref appliedCalls);
                if (n > 1) return;
                string line = DescribeApplied(options as IDictionary, NativeProfiles());
                effective = line;
                Mark(line);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>One status-line part: the setting and what the engine got.</summary>
        public static string Describe()
        {
            string c = choice;
            if (jsonCalls == 0 && appliedCalls == 0)
                return "graphics: graphics.profile " + c + "; the game has not read GameOptions_android.json through patch 'graphics' (yet)";
            string forced = forcedProfile;
            return "graphics: graphics.profile " + c + (forced == null ? " (the game's own choice)" : ", forced " + forced) +
                   (fullProfile ? ", fullProfile" : "") + "; " + (NativeSummary ?? "native profile unknown");
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
            if (options == null)
            {
                sb.Append("; options unknown (m_options is null)");
                return sb.ToString();
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
        /// DestroyNativeString as the game does for GetOptionString), DeviceGPU, DeviceModel, and the in-game graphic setting
        /// (AppPreferences "GameOptions_SelectedProfile", GamePreferencesComponent.NativeChangeGraphicSetting). Never throws.
        /// </summary>
        private static Dictionary<string, string> NativeProfiles()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                Type manager = Type.GetType("Gameloft.Engine.GameOptionManager, Gameloft.Engine", false);
                Type marshal = Type.GetType("Gameloft.Engine.MoreMarshal, Gameloft.Engine", false);
                if (manager == null) return result;
                result["loaded"] = NativeString(manager, marshal, "GetLoadedProfileName", "GPU");
                result["optimal"] = NativeString(manager, marshal, "GetOptimalProfileName", "GPU");
                result["gpu"] = StaticString(manager, "DeviceGPU");
                result["model"] = StaticString(manager, "DeviceModel");
                Type prefs = Type.GetType("Gameloft.Engine.AppPreferences, Gameloft.Engine", false);
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
                return p == null ? null : p.GetValue(null, null) as string;
            }
            catch (Exception)
            {
                return null;
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
        }
    }
}
