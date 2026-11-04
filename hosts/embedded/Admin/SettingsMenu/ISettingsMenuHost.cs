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
        /// settings <c>admin.settingsMenu.sameFrame</c> (server copy, read per call; default true): our Flash work inside the game's
        /// natives, the Android dress before <c>setup()</c> (<see cref="SettingsMenuLayout.PreDress"/>) and, with the trim on, the
        /// same-frame step (<see cref="SettingsMenuLayout.SameFrame"/>); off, only the run lays the grid out, from the next frame
        /// (build 20). Both platforms.
        /// </summary>
        bool SettingsMenuSameFrame { get; }

        /// <summary>
        /// Where the same-frame breadcrumb may be (<see cref="SameFrameBreadcrumb"/>), in order: <see cref="SettingsMenuNames.SameFrameMarkerFile"/>
        /// in the host's log folder (the folder of <c>restart-requested.txt</c>: the data folder's <c>logs</c>, McvsOffline\logs on
        /// PC), then in the fallback log folder the log files move to when that one cannot be written (Android internal storage,
        /// %TEMP%\McvsOffline\logs on PC). Empty when the host knows neither. Never null.
        /// </summary>
        string[] SameFrameMarkerPaths { get; }

        /// <summary>
        /// Stores <c>admin.settingsMenu.sameFrame</c> = false in the server copy and saves it now (the settings page shows the switch
        /// off). False when it could not be saved (logged). Never throws; it writes settings.json, so not per frame.
        /// </summary>
        bool SwitchSameFrameOff();

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

        /// <summary>
        /// Runs <paramref name="action"/> on the game thread one drain after the current one (GameSynchronizationContext.DelayedPost):
        /// from a step that is itself running in this frame's drain, at the start of the next frame, where <see cref="Post"/> would
        /// run it in the same drain. The grid's retries (<see cref="SettingsMenuLayout"/>) go this way. False when it cannot.
        /// </summary>
        bool PostNextFrame(Action action);

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

        /// <summary>
        /// UIComponent.GetPlatformId, <c>Func&lt;int&gt;</c> (UIComponent.cs:1455, PC :1535). Android's same-frame grid step watches it
        /// (<see cref="SettingsMenuLayout.SameFrame"/>): <c>addButtonListeners()</c>, the last call of <c>MenuSettings.setup()</c>
        /// (AND-AS MenuSettings.as:214), calls it at :363 after the last stock write (:213).
        /// </summary>
        public const string GetPlatformId = "GetPlatformId";

        /// <summary>
        /// The same-frame step's breadcrumb in the host's log folder (<see cref="SameFrameBreadcrumb"/>): it exists only while a
        /// process's first look runs inside the native and until the next frame.
        /// </summary>
        public const string SameFrameMarkerFile = "settings-menu-same-frame.txt";

        // ------------------------------------------------------------------ build 22: what Android's setup() writes, made to draw nothing

        /// <summary>
        /// Android's removed instances sit this far below the panel (panel units; y + alpha 0, which setup() never writes: AND-AS
        /// MenuSettings.as:42-215 writes no y but mc_blocker's and no alpha), so its <c>btn_more.visible = true</c> (:210) and the
        /// bottom row's x (:211-212) draw nothing.
        /// </summary>
        public const double OffPanelY = 4000;

        /// <summary>
        /// btn_more.x as our writes leave it: a value setup() never writes, so the same-frame gate sees its own :212 write
        /// (<see cref="StockMoreGamesX"/>) come back.
        /// </summary>
        public const double MoreGamesMarkerX = 0;

        /// <summary>setup()'s btn_more.x (AND-AS MenuSettings.as:212, <c>_loc1_[2][1]</c>).</summary>
        public const double StockMoreGamesX = -177;

        /// <summary>
        /// setup()'s ABOUT x whenever MORE GAMES is shown (AND-AS :213, <c>_loc1_[2][2]</c>; :210 has just set btn_more.visible true
        /// on every Android but Kayac's). Build 22 leaves ABOUT there by design, so that write is a no-op: ThinBtn's own timeline
        /// moves its children on every button state (_library.swf sprite 1047: mc_bg x 0, 8.85 to 17.25 and txt x -2.9 to 2.25 on
        /// frames 15-44, focus_in to enabled_end), so a shift of the children cannot hold ABOUT centred.
        /// </summary>
        public const double AndroidAboutX = 208;

        /// <summary>BtnBase's icon (<c>public var mc_image:ImageContainer</c>, BtnBase.as:33; set at :130-134 when it reads true).</summary>
        public const string IconSlot = "mc_image";

        /// <summary>The name btn_forum's dressed PERFORMANCE icon gets, so neither the slot nor a child by name reaches it from setup().</summary>
        public const string PerformanceIconName = "mcvs_perfIcon";

        /// <summary>
        /// MenuComponent.NativeGetLanguage, <c>Func&lt;string&gt;</c> (registered in the MenuComponent constructor, MenuComponent.cs:345,
        /// PC :334). Steam's same-frame grid step watches it: PC-AS MenuSettings.as:367 calls it after the last tile write (:364).
        /// </summary>
        public const string GetLanguage = "GetLanguage";

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
        private volatile SettingsMenuLayout.SameFrame sameFrame;
        private int layouts;
        private int registrations;
        private int layoutRun;
        private int breadcrumbChecked;
        private int breadcrumbWritten;
        private string breadcrumbPath;
        private string breadcrumbProblem;
        private volatile string sameFrameSkipped;
        private volatile bool sameFrameBlocked;
        private volatile bool forumRemapped;
        private volatile string remapReported;
        private volatile string preDressText;
        private volatile string preDressDone;
        private int preDressRun;

        /// <summary>
        /// Android: btn_forum reads PERFORMANCE on the grid shown now (set by <see cref="SettingsMenuLayout"/> after every write of a
        /// re-dress succeeded, or while the FORUM remap is on, <see cref="ForumRemapped"/>; cleared at every
        /// <c>EnterSection("settings_main_screen")</c> before the steps run). A tap on the tile opens the page only while it is set;
        /// otherwise GoToForum is the game's.
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

        /// <summary>
        /// GRAPHICS or ANTIALIASING differs on the page from the value the game started with: the next return to the grid shows the
        /// restart prompt once (a change put back clears it; PerformancePrefs).
        /// </summary>
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

        /// <summary>
        /// The grid's current run (<see cref="SettingsMenuLayout.Schedule"/>): every <c>EnterSection("settings_main_screen")</c> starts
        /// a new one, and a run whose number is no longer this one stops at its next frame without a word (a newer focus-in owns
        /// the grid).
        /// </summary>
        public int LayoutRun { get { return Interlocked.CompareExchange(ref layoutRun, 0, 0); } }

        public int BeginLayoutRun()
        {
            return Interlocked.Increment(ref layoutRun);
        }

        /// <summary>
        /// The same-frame grid step of the current run, armed by <see cref="SettingsMenuLayout.Schedule"/> and closed by the run's
        /// first frame or the next focus-in (game thread); null when none is armed.
        /// </summary>
        internal SettingsMenuLayout.SameFrame SameFrame
        {
            get { return sameFrame; }
            set { sameFrame = value; }
        }

        // ------------------------------------------------------------------ build 22: the FORUM remap and the dress before setup()

        /// <summary>
        /// Android: the client World's remap turns setting.ForumButton into setting.Performance (<see cref="ForumRemap"/>), as the
        /// last <c>EnterSection("settings_main_screen")</c> found or made it. While it is set the stock dress of btn_forum writes
        /// PERFORMANCE, so the tile reads PERFORMANCE on every grid whatever our steps did.
        /// </summary>
        public bool ForumRemapped
        {
            get { return forumRemapped; }
            set { forumRemapped = value; }
        }

        /// <summary>The remap state last logged ("installed", "removed" or the reason), so each change is one line.</summary>
        public string RemapReported
        {
            get { return remapReported; }
            set { remapReported = value; }
        }

        /// <summary>
        /// The run (<see cref="LayoutRun"/>) whose focus-in may dress the panel before <c>setup()</c> (<see cref="SettingsMenuLayout.PreDress"/>):
        /// set by <see cref="SettingsMenuLayout.Schedule"/> when the same-frame switch allows our Flash work inside a native and the
        /// run's first frame is posted (it deletes the breadcrumb); 0 otherwise.
        /// </summary>
        public int PreDressRun
        {
            get { return Interlocked.CompareExchange(ref preDressRun, 0, 0); }
            set { Interlocked.Exchange(ref preDressRun, value); }
        }

        /// <summary>What the dress before setup() did, for the run's focus-in line ("kept (...)", "laid out (...)"), or null.</summary>
        public string PreDressText
        {
            get { return preDressText; }
            set { preDressText = value; }
        }

        /// <summary>
        /// The laid-out line of <see cref="PreDressRun"/>'s dress before setup() when it left the grid laid out (written or found
        /// so), or null: the run then counts it as its write at frame 0, as a same-frame layout.
        /// </summary>
        public string PreDressDone
        {
            get { return preDressDone; }
            set { preDressDone = value; }
        }

        // ------------------------------------------------------------------ the same-frame step's breadcrumb (SameFrameBreadcrumb)

        /// <summary>True for the first caller only: the check of the previous process's marker runs once per process.</summary>
        public bool BeginBreadcrumbCheck()
        {
            return Interlocked.CompareExchange(ref breadcrumbChecked, 1, 0) == 0;
        }

        /// <summary>The check has run in this process (at start, or at the first arm).</summary>
        public bool BreadcrumbChecked { get { return Interlocked.CompareExchange(ref breadcrumbChecked, 0, 0) != 0; } }

        /// <summary>True for the first caller only: the marker is written before the process's first Flash work inside a native, never again.</summary>
        public bool BeginBreadcrumb()
        {
            return Interlocked.CompareExchange(ref breadcrumbWritten, 1, 0) == 0;
        }

        /// <summary>The marker this process wrote and has not deleted yet (the step is running, or its frame has not ended), or null.</summary>
        public string BreadcrumbPath
        {
            get { return Interlocked.CompareExchange(ref breadcrumbPath, null, null); }
            set { Interlocked.Exchange(ref breadcrumbPath, value); }
        }

        /// <summary>Takes <see cref="BreadcrumbPath"/>: the one caller that gets it deletes the marker.</summary>
        public string TakeBreadcrumbPath()
        {
            return Interlocked.Exchange(ref breadcrumbPath, null);
        }

        /// <summary>Why the marker could not be written or deleted (for the next focus-in line, once), or null.</summary>
        public string BreadcrumbProblem
        {
            get { return Interlocked.CompareExchange(ref breadcrumbProblem, null, null); }
            set { Interlocked.Exchange(ref breadcrumbProblem, value); }
        }

        /// <summary>Takes <see cref="BreadcrumbProblem"/> (it is reported once).</summary>
        public string TakeBreadcrumbProblem()
        {
            return Interlocked.Exchange(ref breadcrumbProblem, null);
        }

        /// <summary>
        /// The line of the check that found the previous process's marker (the step was skipped for this install), or null. The
        /// focus-in line then says "same frame: off (skipped: ...)" while the step stays off.
        /// </summary>
        public string SameFrameSkipped
        {
            get { return sameFrameSkipped; }
            set { sameFrameSkipped = value; }
        }

        /// <summary>
        /// The marker was found but <c>admin.settingsMenu.sameFrame</c> could not be saved as off: no step in this process whatever
        /// the setting reads (the marker stays, so the next start skips it too).
        /// </summary>
        public bool SameFrameBlocked
        {
            get { return sameFrameBlocked; }
            set { sameFrameBlocked = value; }
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
