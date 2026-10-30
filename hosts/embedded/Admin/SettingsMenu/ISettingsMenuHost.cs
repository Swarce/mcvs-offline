// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Threading;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Embedded.Admin.SettingsMenu
{
    /// <summary>
    /// What the game's Settings menu code (docs/design/settings-menu.md §3: <see cref="UiNatives"/>, <see cref="SettingsMenuLayout"/>,
    /// <see cref="PerformancePage"/>) needs from the host. <see cref="AdminHost"/> implements it; the tests give a fake. Every
    /// member is thread-safe and never throws. The menu code reads it through a <c>Func&lt;ISettingsMenuHost&gt;</c> on every call
    /// (<c>() =&gt; AdminHooks.Host</c> in the game), so nothing happens while the offline server is down (pc-port P4).
    /// </summary>
    internal interface ISettingsMenuHost
    {
        /// <summary>settings <c>admin.settingsMenu.trim</c> (server copy, read per call; default true): the grid trim, both platforms.</summary>
        bool SettingsMenuTrim { get; }

        /// <summary>
        /// settings <c>admin.settingsMenu.performance</c> (server copy, read per call; default true) on Android; always false on PC,
        /// which keeps Steam's own PERFORMANCE tile and page: the tile re-dress, the page and R4.
        /// </summary>
        bool SettingsMenuPerformance { get; }

        /// <summary>The PC (Steam) host: <c>AdminHost.PlatformName</c> is <see cref="SettingsSchema.PlatformWindows"/>.</summary>
        bool IsWindows { get; }

        /// <summary>The "admin" logger; one line per step (the "[admin] settings menu: ..." lines).</summary>
        Log Log { get; }

        /// <summary>The Android PERFORMANCE page's values in settings.json (server copy).</summary>
        IPerformanceSettings PerformanceSettings { get; }

        /// <summary>
        /// Game thread: the client World's UIComponent (<c>UISceneExtentions.GetUI(GameApplication.Instance.World)</c>), never a
        /// match-server World's (skeptic E5: the native table is per MenuComponent); null when there is none yet.
        /// </summary>
        object ClientUi();

        /// <summary>Runs <paramref name="action"/> on the game thread at the start of the next frame; false when there is no game thread.</summary>
        bool Post(Action action);

        /// <summary><see cref="IAdminHost.Exit"/>: the restart of the PERFORMANCE page's prompt (§3.4).</summary>
        bool Exit(string reason, bool relaunch);
    }

    /// <summary>
    /// The Android PERFORMANCE page's values as settings.json holds them (server copy), each read through the schema
    /// (<see cref="SettingsSchema.Coerce"/>: a stored value of the wrong type reads as the default, as on the settings page).
    /// <see cref="Save"/> is the only writer; it never blocks the caller (the game thread).
    /// </summary>
    internal interface IPerformanceSettings
    {
        /// <summary><c>graphics.profile</c>: auto, mid, high or ultra.</summary>
        string GraphicsProfile { get; }

        /// <summary><c>graphics.msaa</c>: auto, off, 2x, 4x or 8x.</summary>
        string Msaa { get; }

        /// <summary><c>graphics.renderScale</c>: auto, or "0" to "100".</summary>
        string RenderScale { get; }

        /// <summary><c>graphics.lowResMaps</c>: auto, on or off.</summary>
        string LowResMaps { get; }

        /// <summary><c>graphics.fpsListVersion</c>: 0 (absent) is the game's own FRAME RATE LIMIT list.</summary>
        long FpsListVersion { get; }

        /// <summary>
        /// Stores one of the five keys above: validated by the schema (<c>graphics.fpsListVersion</c>, which the page never edits,
        /// takes a whole number from 0), then saved with <c>Settings.SetAndSave</c> off the calling thread. The getters return the
        /// new value from now on. A value equal to the stored one is not written again (true). False for another key or an invalid
        /// value. Never throws; a failed save is logged and the getters return the stored value again.
        /// </summary>
        bool Save(string key, object value);
    }

    /// <summary>
    /// The names, numbers and strings the Settings menu code and its tests share (docs/design/settings-menu.md §1, §3; the exported
    /// ActionScript of UI.swf, Android 1.17.32 and Steam 1.17.30).
    /// </summary>
    internal static class SettingsMenuNames
    {
        // ------------------------------------------------------------------ Flash natives (MenuComponent.OverrideNativeFunction)

        /// <summary>UIComponent.NativeEnterSection(string), <c>Action&lt;string&gt;</c>; MenuBase.onFocusIn calls it first.</summary>
        public const string EnterSection = "EnterSection";

        /// <summary>UIComponent.GoToForum(), <c>Action</c>; the FORUM tile (MenuSettings.as:282-284), Android's PERFORMANCE tile.</summary>
        public const string GoToForum = "GoToForum";

        /// <summary>GamePreferencesComponent, <c>Func&lt;bool&gt;</c> (MENU only); MenuGameplaySettings.setup :245.</summary>
        public const string CanChangeGraphicSetting = "CanChangeGraphicSetting";

        /// <summary>GameClientComponent, <c>Func&lt;bool&gt;</c>; MenuGameplaySettings.setup :229.</summary>
        public const string CanChangeMaxFramerate = "CanChangeMaxFramerate";

        /// <summary>UserPreferencesComponent, <c>Action&lt;string, object&gt;</c>; unknown keys are ignored by the game.</summary>
        public const string SetUserPreference = "SetUserPreference";

        /// <summary>UserPreferencesComponent, <c>Action</c>; the last call of every onListSelectorChange (:573).</summary>
        public const string SaveUserPreferences = "SaveUserPreferences";

        /// <summary>UserPreferencesComponent, <c>Func&lt;string, object&gt;</c>; null for unknown keys (skeptic E6, plan R3-A only).</summary>
        public const string GetUserPreference = "GetUserPreference";

        // ------------------------------------------------------------------ sections (Native.EnterSection) and menus

        /// <summary>The main-menu Settings grid (MenuSettings.as:36-40); every return to it.</summary>
        public const string MainSettingsSection = "settings_main_screen";

        /// <summary>The in-match Settings panel (frame 2): never laid out.</summary>
        public const string InGameSettingsSection = "pvp_settings_screen";

        /// <summary>Gameplay Settings (and the Android PERFORMANCE page, which reuses it).</summary>
        public const string GameplaySection = "settings_gameplay_screen";

        public const string SettingsMenu = "menu_settings";
        public const string GameplayMenu = "menu_gameplaySettings";

        // ------------------------------------------------------------------ the Settings panel (sprite 6718 Android, 7035 PC)

        public const string Panel = "mc_panel";

        /// <summary>The main-menu frame on Steam (MenuSettings.as:44-52, platforms 4 and 6).</summary>
        public const int SteamMainFrame = 1;

        public const int InGameFrame = 2;

        /// <summary>The main-menu frame on Android.</summary>
        public const int AndroidMainFrame = 3;

        public const int KayacFrame = 4;
        public const int ChineseFrame = 5;

        /// <summary>BtnSetting bounds, registration point top-left (both SWFs).</summary>
        public const double TileWidth = 465.35;

        public const double TileHeight = 355.05;

        /// <summary>A tile whose width is further than this from <see cref="TileWidth"/> aborts the layout (§3.2).</summary>
        public const double TileWidthTolerance = 2;

        /// <summary>ThinBtn's plate (<c>mc_bg</c>); its runtime <c>.width</c> is about 401 (txt and mc_new stick out).</summary>
        public const double ThinButtonPlateWidth = 365.8;

        public const string GeneralTile = "btn_general";
        public const string AudioTile = "btn_audio";
        public const string CustomizeTile = "btn_customize";
        public const string SocialTile = "btn_social";
        public const string AccountTile = "btn_account";
        public const string HelpTile = "btn_help";
        public const string ForumTile = "btn_forum";
        public const string LanguageTile = "btn_language";
        public const string PerformanceTile = "btn_performance";
        public const string CustomerButton = "btn_customer";
        public const string MoreGamesButton = "btn_more";
        public const string AboutButton = "btn_credits";

        /// <summary>Android's PERFORMANCE tile is btn_forum re-dressed with <c>setup({title, icon})</c> (BtnBase.as:116-133).</summary>
        public const string PerformanceTitle = "setting.Performance";

        public const string PerformanceIcon = "Menus/assets/png/icons/settings_performance.png";

        /// <summary>Native.GetPlatformId (PlatformId.cs): the frame choice and the btn_performance handler gate.</summary>
        public const int PlatformAndroid = 2;

        public const int PlatformWindows8 = 4;
        public const int PlatformSteam = 6;

        // ------------------------------------------------------------------ the Android PERFORMANCE page (menu_gameplaySettings)

        /// <summary>MenuGameplaySettings.mc_content (public var), the rows' parent inside <c>mc_panel.mc_scrollPane</c>.</summary>
        public const string Content = "mc_content";

        public const string ScrollPane = "mc_scrollPane";

        /// <summary>optionMC children (swc/settings/gameplay/optionMC.as; a dynamic class with <c>type</c> and <c>values</c>).</summary>
        public const string RowTitle = "tf_title";

        public const string RowValue = "tf_value";
        public const string RowList = "mc_listSelector";
        public const string RowSlider = "mc_scrollbar";
        public const string RowWarning = "mc_warning";

        /// <summary>The stock rows the page keeps (FRAME RATE LIMIT, translated title, 'i' button) or re-types (§3.3).</summary>
        public const string FramerateType = "MaxFramerate";

        public const string GraphicSettingType = "GraphicSetting";

        /// <summary>Our row types: the game ignores them (UserPreferencesComponent.cs:117-149), our natives store them.</summary>
        public const string GraphicsProfileRow = "mcvs.graphicsProfile";

        public const string MsaaRow = "mcvs.msaa";
        public const string RenderScaleRow = "mcvs.renderScale";
        public const string LowResMapsRow = "mcvs.lowResMaps";

        /// <summary>The page's rows in Steam's order (T-C3).</summary>
        public static readonly string[] PageRows = { GraphicsProfileRow, MsaaRow, RenderScaleRow, FramerateType, LowResMapsRow };

        /// <summary>Row titles: the game's translated GRAPHICS SETTINGS and FRAME RATE LIMIT (skeptic B2), English for the rest (owner Q5).</summary>
        public const string GraphicsTitle = "setting.GraphicSetting";

        public const string MsaaTitle = "ANTIALIASING";
        public const string RenderScaleTitle = "SCREEN SCALE";
        public const string FramerateTitle = "setting.FrameRateLimit";
        public const string LowResMapsTitle = "LOW RESOLUTION MAPS";

        /// <summary>Selector values, in index order (<c>graphics.profile</c> without auto shows AUTO; owner Q1).</summary>
        public static readonly string[] GraphicsValues = { "AUTO", "MID", "HIGH", "ULTRA" };

        public static readonly string[] MsaaValues = { "setting.OffButton", "2x", "4x", "8x" };
        public static readonly string[] OffOnValues = { "setting.OffButton", "setting.OnButton" };

        /// <summary>The game's own restart texts (GamePreferencesComponent.cs:203-227).</summary>
        public const string RestartMessage = "setting.GraphicSettingMsg";

        public const string ConfirmButton = "menu.ConfirmButton";
        public const string CancelButton = "menu.CancelButton";

        /// <summary><see cref="AdminHost.Exit"/>'s reason for the prompt's restart.</summary>
        public const string RestartReason = "performance";
    }

    /// <summary>
    /// What the Settings menu code remembers in this process, shared by <see cref="UiNatives"/>, <see cref="SettingsMenuLayout"/>
    /// and <see cref="PerformancePage"/> (one instance per registry; the tests make their own). Plain volatile flags: the natives
    /// and posted steps run on the game thread, and the timer only reads.
    /// </summary>
    internal sealed class SettingsMenuState
    {
        private volatile bool tileActive;
        private volatile bool tileEverActive;
        private volatile bool redressFailed;
        private volatile bool performanceMode;
        private volatile bool transforming;
        private volatile bool restartPending;
        private int layouts;
        private int registrations;

        /// <summary>
        /// Android: btn_forum reads PERFORMANCE on the grid shown now (set by <see cref="SettingsMenuLayout"/> only after every write
        /// of the step succeeded, cleared at every <c>EnterSection("settings_main_screen")</c> before the step runs). A tap on the
        /// tile opens the page only while it is set; otherwise GoToForum is the game's.
        /// </summary>
        public bool TileActive
        {
            get { return tileActive; }
            set
            {
                tileActive = value;
                if (value) tileEverActive = true;
            }
        }

        /// <summary>The tile was re-dressed at least once in this process (design §3.5's original R4 gate; see skeptic D2).</summary>
        public bool TileEverActive { get { return tileEverActive; } }

        /// <summary>
        /// A re-dress was tried in this process and failed: the stock FORUM tile is shown, so the page cannot be reached and R4 must
        /// leave GRAPHICS SETTINGS and FRAME RATE LIMIT in Gameplay Settings (settings never unreachable).
        /// </summary>
        public bool RedressFailed
        {
            get { return redressFailed; }
            set { redressFailed = value; }
        }

        /// <summary>
        /// The menu_gameplaySettings pushed now is the PERFORMANCE page: set by the tile's GoToForum, cleared by
        /// <c>EnterSection("settings_main_screen")</c> (every return to the grid). Gameplay is pushed by pure Flash, so it is clear there.
        /// </summary>
        public bool PerformanceMode
        {
            get { return performanceMode; }
            set { performanceMode = value; }
        }

        /// <summary>
        /// Skeptic E1: the page's transform is writing to the rows now (game thread). Its own <c>displayList</c> and slider
        /// <c>value</c> writes fire the menu's CHANGE handlers, which call SetUserPreference/SaveUserPreferences inside our call;
        /// those natives ignore every call while it is set.
        /// </summary>
        public bool Transforming
        {
            get { return transforming; }
            set { transforming = value; }
        }

        /// <summary>GRAPHICS or ANTIALIASING changed on the page: the next return to the grid shows the restart prompt once.</summary>
        public bool RestartPending
        {
            get { return restartPending; }
            set { restartPending = value; }
        }

        /// <summary>Grid steps that wrote the layout (for the log line).</summary>
        public int Layouts { get { return Interlocked.CompareExchange(ref layouts, 0, 0); } }

        public int CountLayout()
        {
            return Interlocked.Increment(ref layouts);
        }

        /// <summary>Re-ensure steps that had to put one of our delegates back (the game re-registered or removed the name; risk 1).</summary>
        public int Registrations { get { return Interlocked.CompareExchange(ref registrations, 0, 0); } }

        public int CountRegistration()
        {
            return Interlocked.Increment(ref registrations);
        }
    }

    /// <summary>
    /// <see cref="IPerformanceSettings"/> over a server's <see cref="Settings"/> (<see cref="AdminHost.PerformanceSettings"/>). A save
    /// runs on <see cref="Run"/> (the thread pool; the tests run it inline); until it is done the getters return the value being saved.
    /// Saves of one key may run in any order: each writes the key's latest pending value (one write at a time), so the file never
    /// ends with an older value than the last <see cref="Save"/>.
    /// </summary>
    internal sealed class PerformanceSettingsStore : IPerformanceSettings
    {
        private static readonly string[] Keys =
        {
            "graphics.profile", SettingsSchema.GraphicsMsaaKey, SettingsSchema.GraphicsRenderScaleKey, SettingsSchema.GraphicsLowResMapsKey,
            SettingsSchema.GraphicsFpsListVersionKey,
        };

        private readonly Settings settings;
        private readonly Log log;
        private readonly object gate = new object();
        private readonly object writeGate = new object();
        private readonly Dictionary<string, object> saving = new Dictionary<string, object>(StringComparer.Ordinal);

        public PerformanceSettingsStore(Settings settings, Log log)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            this.settings = settings;
            this.log = log;
            Run = work => ThreadPool.QueueUserWorkItem(state => work());
        }

        /// <summary>Where a save runs (default: the thread pool).</summary>
        public Action<Action> Run { get; set; }

        public string GraphicsProfile { get { return Text("graphics.profile", "auto"); } }

        public string Msaa { get { return Text(SettingsSchema.GraphicsMsaaKey, "auto"); } }

        public string RenderScale { get { return Text(SettingsSchema.GraphicsRenderScaleKey, "auto"); } }

        public string LowResMaps { get { return Text(SettingsSchema.GraphicsLowResMapsKey, "auto"); } }

        public long FpsListVersion
        {
            get
            {
                try
                {
                    object value = Current(SettingsSchema.GraphicsFpsListVersionKey);
                    long version;
                    return WholeNumber(value, out version) && version >= 0 ? version : 0;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        public bool Save(string key, object value)
        {
            try
            {
                if (Array.IndexOf(Keys, key) < 0) return false;
                object normalized;
                if (key == SettingsSchema.GraphicsFpsListVersionKey)
                {
                    long version;
                    if (!WholeNumber(value, out version) || version < 0) return false;
                    normalized = version;
                    if (FpsListVersion == version && Current(key) != null) return true;
                }
                else
                {
                    SettingDefinition def = SettingsSchema.Find(key);
                    string problem;
                    if (def == null || !SettingsSchema.Validate(def, value, out normalized, out problem)) return false;
                    if (Equals(Text(key, null), normalized)) return true;
                }
                lock (gate) saving[key] = normalized;
                Action<Action> run = Run;
                run(() => Write(key));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The latest pending value of <paramref name="key"/>, if any (an earlier run may have written it already). Pool thread.</summary>
        private void Write(string key)
        {
            lock (writeGate) WriteLatest(key);
        }

        private void WriteLatest(string key)
        {
            object value;
            lock (gate)
            {
                if (!saving.TryGetValue(key, out value)) return;
            }
            try
            {
                settings.SetAndSave(new Dictionary<string, object>(StringComparer.Ordinal) { { key, value } });
            }
            catch (Exception ex)
            {
                try
                {
                    if (log != null) log.Warn("admin: settings menu: " + key + " could not be saved", ex);
                }
                catch (Exception)
                {
                }
            }
            finally
            {
                lock (gate)
                {
                    object pending;
                    if (saving.TryGetValue(key, out pending) && Equals(pending, value)) saving.Remove(key);
                }
            }
        }

        /// <summary>The value being saved, else the stored one.</summary>
        private object Current(string key)
        {
            lock (gate)
            {
                object pending;
                if (saving.TryGetValue(key, out pending)) return pending;
            }
            return settings.Get(key);
        }

        /// <summary>The key read as the settings page reads it (a wrong type or an unknown option is the default).</summary>
        private string Text(string key, string fallback)
        {
            try
            {
                SettingDefinition def = SettingsSchema.Find(key);
                if (def == null) return fallback;
                bool invalid;
                object value = SettingsSchema.Coerce(def, Current(key), out invalid);
                string text = value as string;
                // A text of the right type that the schema refuses ("500" for the scale) is the default too.
                object normalized;
                string problem;
                if (text != null && !SettingsSchema.Validate(def, text, out normalized, out problem)) text = def.Default as string;
                return text ?? fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static bool WholeNumber(object value, out long result)
        {
            result = 0;
            if (value is long) result = (long)value;
            else if (value is int) result = (int)value;
            else if (value is double && Math.Floor((double)value) == (double)value && Math.Abs((double)value) < 1e15) result = (long)(double)value;
            else return false;
            return true;
        }
    }
}
