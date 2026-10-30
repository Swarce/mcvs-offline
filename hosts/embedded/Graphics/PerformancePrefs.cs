// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using McvsOffline.Embedded.Admin.SettingsMenu;
using McvsOffline.Server.Core;

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
    /// stored with <see cref="SettingsMenuState.RestartPending"/> when the value changes (T-D6).</item>
    /// </list>
    /// WP-D of the design; the bodies are filled in there.
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

        /// <summary>The host's settings as read at start (EmbeddedHost.ConfigureGraphics); the start-time values come from here.</summary>
        private static volatile Settings hostSettings;

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

        // ------------------------------------------------------------------ start: the JSON rewrite (T-D1, T-D2)

        /// <summary>
        /// GameOptions_android.json with <paramref name="msaa"/> (a <see cref="MsaaChoices"/> value) and, with
        /// <paramref name="extendFpsList"/>, the longer FRAME RATE LIMIT list. The input itself when nothing changes; never null for
        /// a non-null input. <paramref name="summary"/>: one phrase for the "[boot] graphics:" line.
        /// </summary>
        public static string RewriteJson(string json, string msaa, bool extendFpsList, out string summary)
        {
            throw new NotImplementedException("WP-D: settings-menu.md §3.4");
        }

        /// <summary>off 0, 2x 2, 4x 4, 8x 8; null for auto and anything else.</summary>
        public static int? MsaaSamples(string choice)
        {
            throw new NotImplementedException("WP-D: settings-menu.md §3.4");
        }

        // ------------------------------------------------------------------ start: after ApplyGameOptions (T-D3, T-D4, T-D5)

        /// <summary>
        /// GraphicsHooks.GameOptionsApplied on Android, after the profile set its own values: SCREEN SCALE, LOW RESOLUTION MAPS and the
        /// FPS index migration as above, on <paramref name="engine"/> (<see cref="Engine"/> in the game). Never throws.
        /// </summary>
        public static void ApplyAfterGameOptions(IEngineGraphics engine, IPerformanceSettings settings, Log log)
        {
            throw new NotImplementedException("WP-D: settings-menu.md §3.4, skeptic E7");
        }

        /// <summary>
        /// The stored <c>MaxFramerate</c> index on list <paramref name="from"/> mapped to list <paramref name="to"/>: the same frame rate,
        /// else the nearest lower one (unlimited, -1, stays unlimited); 0 when nothing fits (T-D3).
        /// </summary>
        public static int MigrateFpsIndex(int stored, IList<int> from, IList<int> to)
        {
            throw new NotImplementedException("WP-D: settings-menu.md §3.4, skeptic E7");
        }

        // ------------------------------------------------------------------ SCREEN SCALE

        /// <summary><c>graphics.renderScale</c> "0".."100" → <c>RenderTargetSizeScale</c> (v &gt; 97: 1, else 0.4 + 0.6 v/100); null for auto.</summary>
        public static float? ScaleOf(string renderScale)
        {
            throw new NotImplementedException("WP-D: settings-menu.md §2 (PC-CS GamePreferencesComponent.cs:496-497)");
        }

        /// <summary>The slider position 0..100 that shows <paramref name="scale"/>: (s − 0.4)/0.6, clamped (the page's initial display).</summary>
        public static int SliderOf(float scale)
        {
            throw new NotImplementedException("WP-D: settings-menu.md §2");
        }

        /// <summary>Live, game thread: stores <c>graphics.renderScale</c> (0..100, or auto) and applies it at once (auto: from the next start).</summary>
        public static bool SetRenderScale(IEngineGraphics engine, IPerformanceSettings settings, string value)
        {
            throw new NotImplementedException("WP-D: settings-menu.md §3.4");
        }

        // ------------------------------------------------------------------ LOW RESOLUTION MAPS, ANTIALIASING, GRAPHICS

        /// <summary>
        /// <c>on</c>: <c>GlobalExcludeLayers</c> gets HighEndOnly and loses LowEndOnly, <c>LowResMaps</c> true; <c>off</c>: the reverse;
        /// also when neither entry is in the list (T-D5). auto changes nothing. False when the engine's members are missing.
        /// </summary>
        public static bool ApplyLowResMaps(IEngineGraphics engine, string choice)
        {
            throw new NotImplementedException("WP-D: settings-menu.md §2 (PC-CS GamePreferencesComponent.cs:479-486)");
        }

        /// <summary>Live: stores <c>graphics.lowResMaps</c> and applies it for the next level load.</summary>
        public static bool SetLowResMaps(IEngineGraphics engine, IPerformanceSettings settings, string choice)
        {
            throw new NotImplementedException("WP-D: settings-menu.md §3.4");
        }

        /// <summary>Stores <c>graphics.msaa</c>; a new value sets <see cref="SettingsMenuState.RestartPending"/>.</summary>
        public static bool SetMsaa(IPerformanceSettings settings, SettingsMenuState state, string choice)
        {
            throw new NotImplementedException("WP-D: settings-menu.md §3.4");
        }

        /// <summary>
        /// GRAPHICS (owner answer 1): stores <c>graphics.profile</c> (auto, mid, high, ultra) and resets AppPreferences
        /// <c>GameOptions_SelectedProfile</c> to 0, so AUTO is the game's own choice for this phone [G reachable]. The current value
        /// raises nothing; a new one sets <see cref="SettingsMenuState.RestartPending"/> (T-D6).
        /// </summary>
        public static bool SetGraphicsProfile(IPerformanceSettings settings, SettingsMenuState state, string choice)
        {
            throw new NotImplementedException("WP-D: settings-menu.md §3.4");
        }

        // ------------------------------------------------------------------ the engine

        /// <summary>The engine's statics by reflection on the loaded assemblies (LoadedTypes, never a load by name); null members read as null.</summary>
        public static IEngineGraphics Engine
        {
            get { throw new NotImplementedException("WP-D: settings-menu.md §3.4"); }
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
