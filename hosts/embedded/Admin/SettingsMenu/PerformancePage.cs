// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (reflection on Gameloft.Engine and Game): compiled into the embedded DLL and the net8 tests.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using McvsOffline.Embedded.Graphics;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded.Admin.SettingsMenu
{
    /// <summary>
    /// R3 and R4 on Android (docs/design/settings-menu.md §1.3, §1.4, §3.3, §3.5; skeptic B2, D1, D2, E1-E3, E6): the PERFORMANCE
    /// page is <c>menu_gameplaySettings</c> in Performance mode, and Gameplay Settings loses GRAPHICS SETTINGS and FRAME RATE LIMIT.
    /// <list type="bullet">
    /// <item><b>Mode.</b> <see cref="Open"/> (the tile's GoToForum) sets <see cref="SettingsMenuState.PerformanceMode"/> and posts
    /// <c>MenusManager.pushMenu("menu_gameplaySettings")</c>; <see cref="LeaveMode"/> (every <c>EnterSection("settings_main_screen")</c>)
    /// clears it.</item>
    /// <item><b>R4.</b> Outside the mode, with <see cref="HidesGameplayRows"/>, <see cref="CanChangeGraphicSetting"/> and
    /// <see cref="CanChangeMaxFramerate"/> return a boxed false, which wins the dispatch: AS never adds the two rows (MGS:229-254).
    /// Otherwise null (the game's answer stands). The gate is the design's §3.5 relaxed per skeptic D2 and owner answer 4 ("removed
    /// everywhere, in-match too"): <c>admin.settingsMenu.performance</c> on, no failed re-dress in this process
    /// (<see cref="SettingsMenuState.RedressFailed"/>), and, unless the Settings grid below is the in-match panel (frame 2), the
    /// grid shown now carries the PERFORMANCE tile (<see cref="SettingsMenuState.TileActive"/>): a main-menu visit whose grid step
    /// never ran (a dropped EnterSection, no post) keeps both rows, so they are never unreachable. The in-match Gameplay page loses
    /// them also before the first main-menu Settings visit (its panel never has the tile).</item>
    /// <item><b>Page (plan R3-B).</b> In the mode both CanChange* return null, so the stock rows are built; the transform, posted from
    /// <see cref="CanChangeGraphicSetting"/>, keeps MaxFramerate (FRAME RATE LIMIT, translated title, 'i' button; B2) and re-types
    /// donors into <see cref="SettingsMenuNames.PageRows"/>: GraphicSetting → GRAPHICS, the first two of InvertCameraX, InvertCameraY,
    /// EnableAimAssist, DoubleTapHoldToIronSightFire → ANTIALIASING and LOW RESOLUTION MAPS, CameraSmoothing (else CameraSensitivity)
    /// → SCREEN SCALE. Never a type the menu passes as an int (AimBehavior, AbilityButtonPlacement, IronSightMode) nor one it remaps
    /// (SprintSensitivity, PlayerFov). Per row: name (<c>mcvs_&lt;key&gt;</c>) and type first, then values and dataLength, then
    /// displayList, the slider value, mask and label, the title. Then it removes every other row and header, restacks with
    /// <c>y += height + 8</c> in the page order, calls <c>updateBounds()</c>, sets the title <c>setting.Performance</c>
    /// (FreemiumBar.updateTitle) and logs "performance page: 5 rows". It reads everything before it writes; any failure removes
    /// nothing and logs a reason (the stock rows, GRAPHICS SETTINGS and FRAME RATE LIMIT among them, stay usable).
    /// E1: the transform sets <see cref="SettingsMenuState.Transforming"/> around its writes, because its own <c>displayList</c>,
    /// <c>dataLength</c> and slider <c>value</c> writes fire onListSelectorChange/onBarScroll, whose SetUserPreference and
    /// SaveUserPreferences calls must store nothing (an "auto" key would become the displayed value, and a donor's real preference
    /// could be written). E3: the <c>mc_content.alpha = 0</c> write inside the native is [G]; the posted transform puts 1 back in
    /// every outcome (also on the cached menu when another one is on top by then).</item>
    /// <item><b>Shown values.</b> A key at auto shows what the engine has now (design §2): GRAPHICS AUTO, the current MSAA samples,
    /// for SCREEN SCALE the scale a match renders at (<see cref="PerformancePrefs.AutoRenderScaleSlider"/>: the page lives in the
    /// main menu, whose own scale is the menu's), the current LowResMaps.</item>
    /// <item><b>Writes.</b> <see cref="OnSetUserPreference"/>: a slider move of <c>mcvs.renderScale</c> is applied to the engine at
    /// once (every ENTER_FRAME of a drag sends one, ScrollBar.as:185-206) and only remembered; <see cref="OnSaveUserPreferences"/>
    /// stores the last one (one settings.json write per visit, never an older percent after a newer one: the page's own pop calls
    /// it, MGS:713, as after every selector change) and reads <c>mc_listSelector.value</c> (the true index; E2: no post needed) of
    /// the three selector rows, since the generic branch sends only <c>Boolean(index)</c> (MGS:571). Both ignore every call while
    /// transforming, and a value equal to the one shown before the change (the stored one, or for auto the engine's), so a change
    /// on one row never turns another row's auto into a value. Values go through <see cref="ISettingsMenuHost.PerformanceSettings"/>
    /// and <see cref="PerformancePrefs"/> (<see cref="IPerformanceBackEnd"/>; the tests give their own).</item>
    /// <item><b>Restart.</b> GRAPHICS and ANTIALIASING set <see cref="SettingsMenuState.RestartPending"/> while either differs from the
    /// value the game started with (PerformancePrefs; a change put back clears it); the next
    /// return to the grid posts <see cref="ShowRestartPromptIfPending"/>'s prompt (the game's own texts; Yes → <c>Exit("performance",
    /// true)</c>). Not a copy of Steam's prompt, which is informational only (B1).</item>
    /// <item><b>R3-A spike.</b> The first <see cref="CanChangeGraphicSetting"/> in the mode logs, once per registry, whether C# can read
    /// the private <c>_arOptions</c> and whether the alpha write inside the native went through (E3, E6): the device log decides
    /// whether plan R3-A (<see cref="OnGetUserPreference"/>) is worth building.</item>
    /// </list>
    /// </summary>
    internal static class PerformancePage
    {
        /// <summary>GRAPHICS, by index (owner answer 1: <c>graphics.profile</c>); the labels are <see cref="SettingsMenuNames.GraphicsValues"/>.</summary>
        internal static readonly string[] GraphicsChoices = { "auto", "mid", "high", "ultra" };

        /// <summary>ANTIALIASING, by index (<see cref="SettingsMenuNames.MsaaValues"/>); auto is not offered, the settings page has it.</summary>
        internal static readonly string[] MsaaChoices = { "off", "2x", "4x", "8x" };

        /// <summary>LOW RESOLUTION MAPS, by index (<see cref="SettingsMenuNames.OffOnValues"/>).</summary>
        internal static readonly string[] LowResMapsChoices = { "off", "on" };

        /// <summary>Selectors whose generic Boolean branch (MGS:571) we can take over, in order of preference.</summary>
        internal static readonly string[] SelectorDonors = { "InvertCameraX", "InvertCameraY", "EnableAimAssist", "DoubleTapHoldToIronSightFire" };

        /// <summary>Sliders with no remap in onBarScroll (MGS:487-509), in order of preference.</summary>
        internal static readonly string[] SliderDonors = { "CameraSmoothing", "CameraSensitivity" };

        /// <summary>The gap between rows (MGS:430).</summary>
        public const float RowGap = 8;

        private static readonly object BoxedFalse = false;

        /// <summary>What a registry's page remembers beside <see cref="SettingsMenuState"/> (the spike line, the tests' back end).</summary>
        private sealed class Memo
        {
            public int SpikeLogged;
            public volatile IPerformanceBackEnd BackEnd;

            /// <summary>SCREEN SCALE applied by a drag and not stored yet (0..100), else -1. Game thread.</summary>
            public int PendingScale = -1;

            /// <summary>What the slider showed before the first move of <see cref="PendingScale"/> (back there: nothing to store).</summary>
            public int ScaleBefore = -1;
        }

        // By reference (SettingsMenuState keeps object's Equals). One registry in the game; no ConditionalWeakTable: the Android
        // mscorlib is trimmed and has no GetOrCreateValue.
        private static readonly Dictionary<SettingsMenuState, Memo> Memos = new Dictionary<SettingsMenuState, Memo>();

        private static readonly IPerformanceBackEnd Prefs = new PrefsBackEnd();

        /// <summary>Tests: the back end the page of <paramref name="state"/> uses instead of <see cref="PerformancePrefs"/> (null: PerformancePrefs again).</summary>
        internal static void UseBackEnd(SettingsMenuState state, IPerformanceBackEnd backEnd)
        {
            MemoOf(state).BackEnd = backEnd;
        }

        // ------------------------------------------------------------------ the mode

        /// <summary>GoToForum while the tile is active: enter the mode and post the push. Never throws.</summary>
        public static void Open(ISettingsMenuHost host, SettingsMenuState state)
        {
            try
            {
                if (host == null || state == null) return;
                if (host.IsWindows || !host.SettingsMenuPerformance)
                {
                    Write(host, "settings menu: performance page not opened (switched off)");
                    return;
                }
                state.PerformanceMode = true;
                // Posted, not pushed from inside the tap's native: a push re-enters the menu stack (mech.md §3.3).
                if (!host.Post(() => Push(host, state)))
                {
                    state.PerformanceMode = false;
                    Write(host, "settings menu: performance page not opened (no game thread)");
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Game thread, the frame after the tap: the push, while the grid is still on top.</summary>
        private static void Push(ISettingsMenuHost host, SettingsMenuState state)
        {
            try
            {
                if (!state.PerformanceMode) return;   // the grid came back first
                object ui = host.ClientUi();
                if (ui == null)
                {
                    NotOpened(host, state, "no UI");
                    return;
                }
                using (var flash = new FlashScope(ui))
                {
                    object menu;
                    string top = TopMenu(flash, out menu);
                    if (top != SettingsMenuNames.SettingsMenu)
                    {
                        NotOpened(host, state, "the menu on top is " + (top ?? "unnamed"));
                        return;
                    }
                    object manager = flash.Class("managers", "MenusManager");
                    if (manager == null)
                    {
                        NotOpened(host, state, "no MenusManager");
                        return;
                    }
                    flash.Call(manager, "pushMenu", SettingsMenuNames.GameplayMenu);
                }
            }
            catch (Exception ex)
            {
                NotOpened(host, state, Describe(ex));
            }
        }

        private static void NotOpened(ISettingsMenuHost host, SettingsMenuState state, string reason)
        {
            state.PerformanceMode = false;
            Write(host, "settings menu: performance page not opened (" + reason + ")");
        }

        /// <summary>Every <c>EnterSection("settings_main_screen")</c>: the mode ends (T-C1).</summary>
        public static void LeaveMode(SettingsMenuState state)
        {
            if (state != null) state.PerformanceMode = false;
        }

        /// <summary>The R4 gate (T-C2): Gameplay Settings without GRAPHICS SETTINGS and FRAME RATE LIMIT now. Game thread.</summary>
        public static bool HidesGameplayRows(ISettingsMenuHost host, SettingsMenuState state)
        {
            try
            {
                if (host == null || state == null || host.IsWindows || !host.SettingsMenuPerformance || state.RedressFailed ||
                    state.PerformanceMode) return false;
                // Read last: the Flash lookup runs only when the flags already say hide.
                return state.TileActive || BelowIsInMatchGrid(host);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The Settings menu (the grid Gameplay Settings is opened from) shows the in-match panel, frame 2 (MenuSettings.setup:
        /// <c>GameSettings.isInGame ? 2 : ...</c>), which never has the PERFORMANCE tile. False when it is the main-menu panel, or
        /// when there is no UI or no such menu (then only <see cref="SettingsMenuState.TileActive"/> hides the rows).
        /// </summary>
        private static bool BelowIsInMatchGrid(ISettingsMenuHost host)
        {
            object ui = host.ClientUi();
            if (ui == null) return false;
            using (var flash = new FlashScope(ui))
            {
                object manager = flash.Class("managers", "MenusManager");
                object grid = manager == null ? null : flash.Call(manager, "getMenuByName", SettingsMenuNames.SettingsMenu);
                object panel = grid == null ? null : flash.Child(grid, SettingsMenuNames.Panel);
                double? frame = panel == null ? null : flash.Number(panel, "currentFrame");
                return frame == SettingsMenuNames.InGameFrame;
            }
        }

        // ------------------------------------------------------------------ the two natives MenuGameplaySettings.setup() asks

        /// <summary>CanChangeGraphicSetting: boxed false (R4), or null; in the mode it also posts the transform. Never throws.</summary>
        public static object CanChangeGraphicSetting(ISettingsMenuHost host, SettingsMenuState state)
        {
            try
            {
                if (host == null || state == null) return null;
                if (HidesGameplayRows(host, state)) return BoxedFalse;
                if (state.PerformanceMode && !host.IsWindows && host.SettingsMenuPerformance) Prepare(host, state);
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>CanChangeMaxFramerate: boxed false (R4), or null. Never throws.</summary>
        public static object CanChangeMaxFramerate(ISettingsMenuHost host, SettingsMenuState state)
        {
            return HidesGameplayRows(host, state) ? BoxedFalse : null;
        }

        /// <summary>
        /// Inside CanChangeGraphicSetting, before any row exists (MGS:245): post the transform, then hide <c>mc_content</c> until it
        /// has run (only once it is posted: the transform is what shows it again), and the R3-A spike line.
        /// </summary>
        private static void Prepare(ISettingsMenuHost host, SettingsMenuState state)
        {
            if (!host.Post(() => Transform(host, state)))
            {
                Write(host, "settings menu: performance page not built (no game thread)");
                return;
            }
            Memo memo = MemoOf(state);
            bool spike = Interlocked.Exchange(ref memo.SpikeLogged, 1) == 0;
            string alpha = "not tried";
            string options = "not tried";
            object ui = host.ClientUi();
            if (ui != null)
            {
                using (var flash = new FlashScope(ui))
                {
                    object menu = null;
                    try
                    {
                        if (TopMenu(flash, out menu) == SettingsMenuNames.GameplayMenu)
                        {
                            object content = flash.Get(menu, SettingsMenuNames.Content);
                            if (content != null)
                            {
                                flash.Set(content, "alpha", 0f);
                                alpha = "ok";
                            }
                            else
                            {
                                alpha = "no " + SettingsMenuNames.Content;
                            }
                        }
                        else
                        {
                            alpha = "not the page on top";
                            menu = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        alpha = "failed (" + Describe(ex) + ")";
                    }
                    try
                    {
                        if (spike && menu != null)
                        {
                            object list = flash.Get(menu, "_arOptions");
                            var items = list as ICollection;
                            options = list == null ? "not readable" :
                                      items != null ? "readable (" + items.Count.ToString(CultureInfo.InvariantCulture) + " entries)" : "readable (" + list.GetType().Name + ")";
                        }
                    }
                    catch (Exception ex)
                    {
                        options = "failed (" + Describe(ex) + ")";
                    }
                }
            }
            if (spike) Write(host, "settings menu: R3-A spike: _arOptions " + options + "; alpha write inside the native " + alpha);
        }

        // ------------------------------------------------------------------ the transform

        /// <summary>One re-typed row: the donor found, our type and how it is shown.</summary>
        private sealed class Retype
        {
            public string Type;
            public string Title;
            public object Row;
            public object Widget;          // mc_listSelector or mc_scrollbar
            public object Mask;            // slider: mc_scrollbar.slot.mc_mask
            public object ValueText;       // slider: tf_value
            public object TitleText;       // tf_title.tf
            public string[] Values;        // selector labels
            public int Shown;              // selector index, or slider 0..100
            public bool Slider;
        }

        /// <summary>
        /// Game thread, the top menu is <c>menu_gameplaySettings</c> in the mode: the page as above. One log line; <c>alpha</c> is back
        /// to 1 and <see cref="SettingsMenuState.Transforming"/> is false in every outcome. Never throws.
        /// </summary>
        public static void Transform(ISettingsMenuHost host, SettingsMenuState state)
        {
            if (host == null || state == null) return;
            try
            {
                object ui = host.ClientUi();
                if (ui == null)
                {
                    Write(host, "settings menu: performance page not built (no UI)");
                    return;
                }
                using (var flash = new FlashScope(ui))
                {
                    object content = null;
                    string line;
                    try
                    {
                        line = Build(host, state, flash, ref content);
                    }
                    catch (Exception ex)
                    {
                        line = "settings menu: performance page not built (" + Describe(ex) + ")";
                    }
                    finally
                    {
                        state.Transforming = false;
                        ShowContent(flash, content);
                    }
                    if (line != null) Write(host, line);
                }
            }
            catch (Exception ex)
            {
                state.Transforming = false;
                Write(host, "settings menu: performance page not built (" + Describe(ex) + ")");
            }
        }

        /// <summary>The page; the log line (null: nothing worth one). <paramref name="content"/> is the page's mc_content once found.</summary>
        private static string Build(ISettingsMenuHost host, SettingsMenuState state, FlashScope flash, ref object content)
        {
            const string NotBuilt = "settings menu: performance page not built (";
            if (!state.PerformanceMode) return null;   // back to the grid before this frame: the stock page, shown again
            if (host.IsWindows || !host.SettingsMenuPerformance) return NotBuilt + "switched off)";
            object menu;
            string top = TopMenu(flash, out menu);
            if (top != SettingsMenuNames.GameplayMenu) return NotBuilt + "the menu on top is " + (top ?? "unnamed") + ")";
            content = flash.Get(menu, SettingsMenuNames.Content);
            if (content == null) return NotBuilt + "no " + SettingsMenuNames.Content + ")";
            object panel = flash.Child(menu, "mc_panel");
            object scroll = panel == null ? null : flash.Child(panel, SettingsMenuNames.ScrollPane);
            if (scroll == null) return NotBuilt + "no " + SettingsMenuNames.ScrollPane + ")";
            object text = flash.Class("shared.utilities", "TextUtilities");
            if (text == null) return NotBuilt + "no TextUtilities)";

            // Read: every child, the rows by type ("item<k>" names; headers are unnamed categoryMC instances, MGS:300-312).
            double? count = flash.Number(content, "numChildren");
            if (count == null) return NotBuilt + "no numChildren)";
            var children = new List<object>();
            var rows = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < (int)count.Value; i++)
            {
                object child = flash.Call(content, "getChildAt", i);
                if (child == null) return NotBuilt + "child " + i.ToString(CultureInfo.InvariantCulture) + " unreadable)";
                children.Add(child);
                string name = flash.Get(child, "name") as string;
                if (name == null || !name.StartsWith("item", StringComparison.Ordinal)) continue;
                string type = flash.Get(child, "type") as string;
                if (type != null && !rows.ContainsKey(type)) rows[type] = child;
            }

            IPerformanceBackEnd backEnd = BackEnd(state);
            IPerformanceSettings settings = host.PerformanceSettings;
            if (settings == null) return NotBuilt + "no settings)";
            IEngineGraphics engine = backEnd.Engine;

            var retypes = new List<Retype>();
            var used = new List<string>();
            string problem =
                AddSelector(flash, retypes, rows, used, new[] { SettingsMenuNames.GraphicSettingType }, SettingsMenuNames.GraphicsProfileRow,
                            SettingsMenuNames.GraphicsTitle, SettingsMenuNames.GraphicsValues, Shown(backEnd, engine, settings, SettingsMenuNames.GraphicsProfileRow)) ??
                AddSelector(flash, retypes, rows, used, SelectorDonors, SettingsMenuNames.MsaaRow, SettingsMenuNames.MsaaTitle,
                            SettingsMenuNames.MsaaValues, Shown(backEnd, engine, settings, SettingsMenuNames.MsaaRow)) ??
                AddSlider(flash, retypes, rows, used, Shown(backEnd, engine, settings, SettingsMenuNames.RenderScaleRow)) ??
                AddSelector(flash, retypes, rows, used, SelectorDonors, SettingsMenuNames.LowResMapsRow, SettingsMenuNames.LowResMapsTitle,
                            SettingsMenuNames.OffOnValues, Shown(backEnd, engine, settings, SettingsMenuNames.LowResMapsRow));
            if (problem != null) return NotBuilt + problem + ")";
            object framerate;
            if (!rows.TryGetValue(SettingsMenuNames.FramerateType, out framerate)) return NotBuilt + "no " + SettingsMenuNames.FramerateType + " row)";

            // The page order (Steam's) and each row's height for the restack.
            var order = new List<object>();
            var heights = new List<double>();
            foreach (string type in SettingsMenuNames.PageRows)
            {
                object row = type == SettingsMenuNames.FramerateType ? framerate : retypes.Find(r => r.Type == type).Row;
                double? height = flash.Number(row, "height");
                if (height == null) return NotBuilt + type + " has no height)";
                order.Add(row);
                heights.Add(height.Value);
            }
            object freemium = flash.Class("elements.freemiumBar", "FreemiumBar");

            // Write. E1: our own displayList/dataLength/value= writes fire the menu's CHANGE handlers; the natives ignore them now.
            state.Transforming = true;
            foreach (Retype r in retypes)
            {
                flash.Set(r.Row, "name", RowName(r.Type));
                flash.Set(r.Row, "type", r.Type);
                if (r.Slider)
                {
                    float v = r.Shown / 100f;
                    flash.Set(r.Widget, "value", v == 0 ? 0.0001f : v);   // as setup() does (MGS:333)
                    flash.Set(r.Mask, "scaleX", v > 0.99f ? 1f : 0.08f + v * 0.84f);
                    flash.Call(text, "setStringID", r.ValueText, "menu.ScorePercent", new List<object> { (float)r.Shown });
                }
                else
                {
                    flash.Set(r.Row, "values", new List<object>(r.Values));
                    flash.Set(r.Widget, "dataLength", (float)r.Values.Length);
                    flash.Call(r.Widget, "displayList", (float)r.Shown);
                }
                flash.Call(text, "setStringID", r.TitleText, r.Title);
            }
            foreach (object child in children)
            {
                if (!order.Contains(child)) flash.Call(content, "removeChild", child);
            }
            double y = 0;
            for (int i = 0; i < order.Count; i++)
            {
                flash.Set(order[i], "y", (float)y);
                y += heights[i] + RowGap;
            }
            flash.Call(scroll, "updateBounds");
            flash.Set(menu, "title", SettingsMenuNames.PerformanceTitle);
            if (freemium != null) flash.Call(freemium, "updateTitle", SettingsMenuNames.PerformanceTitle);
            return "settings menu: performance page: " + order.Count.ToString(CultureInfo.InvariantCulture) + " rows";
        }

        /// <summary>Finds a selector donor (the first of <paramref name="donors"/> not yet used); null, or why not.</summary>
        private static string AddSelector(FlashScope flash, List<Retype> retypes, Dictionary<string, object> rows, List<string> used,
                                          string[] donors, string type, string title, string[] values, int shown)
        {
            foreach (string donor in donors)
            {
                object row;
                if (used.Contains(donor) || !rows.TryGetValue(donor, out row)) continue;
                object list = flash.Get(row, SettingsMenuNames.RowList);
                object titleText = Title(flash, row);
                if (list == null || titleText == null) continue;
                used.Add(donor);
                retypes.Add(new Retype
                {
                    Type = type, Title = title, Row = row, Widget = list, TitleText = titleText, Values = values,
                    Shown = Math.Max(0, Math.Min(values.Length - 1, shown)),
                });
                return null;
            }
            return "no donor row for " + type;
        }

        private static string AddSlider(FlashScope flash, List<Retype> retypes, Dictionary<string, object> rows, List<string> used, int shown)
        {
            foreach (string donor in SliderDonors)
            {
                object row;
                if (used.Contains(donor) || !rows.TryGetValue(donor, out row)) continue;
                object bar = flash.Get(row, SettingsMenuNames.RowSlider);
                object slot = bar == null ? null : flash.Get(bar, "slot");
                object mask = slot == null ? null : flash.Get(slot, "mc_mask");
                object valueText = flash.Get(row, SettingsMenuNames.RowValue);
                object titleText = Title(flash, row);
                if (mask == null || valueText == null || titleText == null) continue;
                used.Add(donor);
                retypes.Add(new Retype
                {
                    Type = SettingsMenuNames.RenderScaleRow, Title = SettingsMenuNames.RenderScaleTitle, Row = row, Widget = bar, Mask = mask,
                    ValueText = valueText, TitleText = titleText, Shown = Math.Max(0, Math.Min(100, shown)), Slider = true,
                });
                return null;
            }
            return "no donor row for " + SettingsMenuNames.RenderScaleRow;
        }

        private static object Title(FlashScope flash, object row)
        {
            object title = flash.Get(row, SettingsMenuNames.RowTitle);
            return title == null ? null : flash.Get(title, "tf");
        }

        /// <summary>Puts <c>mc_content.alpha</c> back to 1: the page's, else the cached Gameplay Settings menu's. Never throws.</summary>
        private static void ShowContent(FlashScope flash, object content)
        {
            try
            {
                if (content == null)
                {
                    object menu;
                    if (TopMenu(flash, out menu) != SettingsMenuNames.GameplayMenu)
                    {
                        object manager = flash.Class("managers", "MenusManager");
                        menu = manager == null ? null : flash.Call(manager, "getMenuByName", SettingsMenuNames.GameplayMenu);
                    }
                    if (menu != null) content = flash.Get(menu, SettingsMenuNames.Content);
                }
                if (content != null) flash.Set(content, "alpha", 1f);
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------ the page's writes

        /// <summary>
        /// SetUserPreference(key, value) as the game calls it (a slider value arrives as float, a selector as bool): a new SCREEN SCALE
        /// percent goes to the engine now and is stored by the next <see cref="OnSaveUserPreferences"/>. Game thread. Never throws.
        /// </summary>
        public static void OnSetUserPreference(ISettingsMenuHost host, SettingsMenuState state, string key, object value)
        {
            try
            {
                if (host == null || state == null || state.Transforming) return;
                // Selectors arrive here as Boolean(index) (MGS:571): OnSaveUserPreferences, right after, reads the index.
                if (key != SettingsMenuNames.RenderScaleRow || host.IsWindows || !host.SettingsMenuPerformance) return;
                double? v = FlashInterop.Number(value);
                if (v == null || double.IsNaN(v.Value)) return;
                int percent = (int)Math.Round(Math.Max(0, Math.Min(1, v.Value)) * 100);
                IPerformanceBackEnd backEnd = BackEnd(state);
                IPerformanceSettings settings = host.PerformanceSettings;
                IEngineGraphics engine = backEnd.Engine;
                if (settings == null) return;
                Memo memo = MemoOf(state);
                int shown = memo.PendingScale >= 0 ? memo.PendingScale : Shown(backEnd, engine, settings, key);
                if (percent == shown) return;
                // Not stored here: a drag sends a value per frame, and one file write per value could land out of order.
                if (memo.PendingScale < 0) memo.ScaleBefore = shown;
                memo.PendingScale = percent;
                float? scale = backEnd.ScaleOf(percent.ToString(CultureInfo.InvariantCulture));
                if (scale == null || engine == null || !engine.SetRenderTargetSizeScale(scale.Value))
                    Write(host, "settings menu: performance: screen scale " + percent.ToString(CultureInfo.InvariantCulture) + " not applied now");
            }
            catch (Exception ex)
            {
                Write(host, "settings menu: performance: screen scale not applied (" + Describe(ex) + ")");
            }
        }

        /// <summary>SaveUserPreferences(): the last SCREEN SCALE of a drag, then the selectors' true indices. Never throws.</summary>
        public static void OnSaveUserPreferences(ISettingsMenuHost host, SettingsMenuState state)
        {
            try
            {
                if (host == null || state == null || state.Transforming) return;
                if (host.IsWindows || !host.SettingsMenuPerformance) return;
                IPerformanceBackEnd backEnd = BackEnd(state);
                IPerformanceSettings settings = host.PerformanceSettings;
                if (settings == null) return;
                // Before the mode and top-menu checks: the page's own pop (MGS:713) arrives with the grid already on top.
                StorePendingScale(host, state, backEnd, settings);
                if (!state.PerformanceMode) return;
                object ui = host.ClientUi();
                if (ui == null) return;
                using (var flash = new FlashScope(ui))
                {
                    object menu;
                    if (TopMenu(flash, out menu) != SettingsMenuNames.GameplayMenu) return;   // also the page's own pop (MGS:713)
                    object content = flash.Get(menu, SettingsMenuNames.Content);
                    if (content == null) return;
                    IEngineGraphics engine = backEnd.Engine;
                    Store(host, state, flash, content, backEnd, engine, settings, SettingsMenuNames.GraphicsProfileRow, GraphicsChoices);
                    Store(host, state, flash, content, backEnd, engine, settings, SettingsMenuNames.MsaaRow, MsaaChoices);
                    Store(host, state, flash, content, backEnd, engine, settings, SettingsMenuNames.LowResMapsRow, LowResMapsChoices);
                }
            }
            catch (Exception ex)
            {
                Write(host, "settings menu: performance: not stored (" + Describe(ex) + ")");
            }
        }

        /// <summary>The SCREEN SCALE a drag left (already applied), stored once; none when the slider went back to where it was.</summary>
        private static void StorePendingScale(ISettingsMenuHost host, SettingsMenuState state, IPerformanceBackEnd backEnd, IPerformanceSettings settings)
        {
            Memo memo = MemoOf(state);
            int percent = Interlocked.Exchange(ref memo.PendingScale, -1);
            if (percent < 0 || percent == memo.ScaleBefore) return;
            string stored = percent.ToString(CultureInfo.InvariantCulture);
            bool ok = backEnd.SetRenderScale(backEnd.Engine, settings, stored);
            Write(host, "settings menu: performance: renderScale " + stored + (ok ? "" : " not stored"));
        }

        /// <summary>One selector row: its List.value, stored when it differs from what is shown for the key now.</summary>
        private static void Store(ISettingsMenuHost host, SettingsMenuState state, FlashScope flash, object content, IPerformanceBackEnd backEnd,
                                  IEngineGraphics engine, IPerformanceSettings settings, string type, string[] choices)
        {
            object row = flash.Child(content, RowName(type));
            if (row == null || (flash.Get(row, "type") as string) != type) return;
            object list = flash.Get(row, SettingsMenuNames.RowList);
            double? value = list == null ? null : flash.Number(list, "value");
            if (value == null) return;
            int index = (int)value.Value;
            if (index < 0 || index >= choices.Length || index == Shown(backEnd, engine, settings, type)) return;
            string choice = choices[index];
            bool stored;
            if (type == SettingsMenuNames.GraphicsProfileRow) stored = backEnd.SetGraphicsProfile(settings, state, choice);
            else if (type == SettingsMenuNames.MsaaRow) stored = backEnd.SetMsaa(settings, state, choice);
            else stored = backEnd.SetLowResMaps(engine, settings, choice);
            Write(host, "settings menu: performance: " + type.Substring("mcvs.".Length) + " " + choice + (stored ? "" : " not stored") +
                        (state.RestartPending ? " (restart pending)" : ""));
        }

        /// <summary>
        /// Plan R3-A only (skeptic E6): GetUserPreference(key) for our <c>mcvs.*</c> row types, so stock rows built from our
        /// descriptors show our values (a selector's index, the slider's 0..1); null for every other key (the game's answer stands). Never throws.
        /// </summary>
        public static object OnGetUserPreference(ISettingsMenuHost host, SettingsMenuState state, string key)
        {
            try
            {
                if (host == null || state == null || host.IsWindows || !host.SettingsMenuPerformance) return null;
                if (Array.IndexOf(SettingsMenuNames.PageRows, key) < 0 || key == SettingsMenuNames.FramerateType) return null;
                IPerformanceBackEnd backEnd = BackEnd(state);
                IPerformanceSettings settings = host.PerformanceSettings;
                if (settings == null) return null;
                int shown = Shown(backEnd, backEnd.Engine, settings, key);
                return key == SettingsMenuNames.RenderScaleRow ? shown / 100f : (float)shown;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ the restart prompt

        /// <summary>At a return to the grid with <see cref="SettingsMenuState.RestartPending"/>: the prompt, once (posted: after setup()). Never throws.</summary>
        public static void ShowRestartPromptIfPending(ISettingsMenuHost host, SettingsMenuState state)
        {
            try
            {
                if (host == null || state == null || !state.RestartPending) return;
                state.RestartPending = false;
                if (!host.Post(() => ShowRestartPrompt(host)))
                {
                    state.RestartPending = true;   // the next return to the grid tries again
                }
            }
            catch (Exception)
            {
            }
        }

        private static void ShowRestartPrompt(ISettingsMenuHost host)
        {
            try
            {
                object ui = host.ClientUi();
                if (ui == null)
                {
                    Write(host, "settings menu: restart prompt not shown (no UI); the change applies at the next start");
                    return;
                }
                // PopupConfirm.setLabels (AS) translates title, msg, buttonLabelYes and buttonLabelNo itself: the ids go as they are.
                var data = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "title", SettingsMenuNames.GraphicsTitle },
                    { "msg", SettingsMenuNames.RestartMessage },
                    { "buttonLabelYes", SettingsMenuNames.ConfirmButton },
                    { "buttonLabelNo", SettingsMenuNames.CancelButton },
                    { "hideCloseButton", true },
                };
                FlashInterop.ShowConfirmPopup(ui, "SHOW_CONFIRM", data, answer => Answered(host, answer));
                Write(host, "settings menu: restart prompt shown");
            }
            catch (Exception ex)
            {
                Write(host, "settings menu: restart prompt not shown (" + Describe(ex) + "); the change applies at the next start");
            }
        }

        private static void Answered(ISettingsMenuHost host, Dictionary<string, object> answer)
        {
            try
            {
                object result;
                bool yes = answer != null && answer.TryGetValue("result", out result) && result is bool && (bool)result;
                Write(host, "settings menu: restart prompt: " + (yes ? "restart" : "later"));
                if (yes) host.Exit(SettingsMenuNames.RestartReason, true);
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// What the row of <paramref name="type"/> shows now: the index of the stored choice, or for auto the engine's value (GRAPHICS:
        /// AUTO; ANTIALIASING: the current samples; SCREEN SCALE: 0..100, the scale a match renders at; LOW RESOLUTION MAPS: the current flag).
        /// </summary>
        internal static int Shown(IPerformanceBackEnd backEnd, IEngineGraphics engine, IPerformanceSettings settings, string type)
        {
            if (type == SettingsMenuNames.GraphicsProfileRow) return Math.Max(0, Array.IndexOf(GraphicsChoices, settings.GraphicsProfile));
            if (type == SettingsMenuNames.MsaaRow)
            {
                string msaa = settings.Msaa;
                if (msaa != PerformancePrefs.Auto) return Math.Max(0, Array.IndexOf(MsaaChoices, msaa));
                int? samples = engine == null ? null : engine.AntialiasingSamples;
                return samples == 8 ? 3 : samples == 4 ? 2 : samples == 2 ? 1 : 0;
            }
            if (type == SettingsMenuNames.RenderScaleRow)
            {
                int percent;
                if (int.TryParse(settings.RenderScale, NumberStyles.None, CultureInfo.InvariantCulture, out percent)) return Math.Min(100, percent);
                return Math.Max(0, Math.Min(100, backEnd.AutoRenderScaleSlider(engine)));
            }
            if (type == SettingsMenuNames.LowResMapsRow)
            {
                string maps = settings.LowResMaps;
                if (maps != PerformancePrefs.Auto) return maps == "on" ? 1 : 0;
                return engine != null && engine.LowResMaps == true ? 1 : 0;
            }
            return 0;
        }

        /// <summary>The instance name of our row of <paramref name="type"/> ("mcvs.msaa" → "mcvs_msaa").</summary>
        internal static string RowName(string type)
        {
            return type.Replace('.', '_');
        }

        private static IPerformanceBackEnd BackEnd(SettingsMenuState state)
        {
            IPerformanceBackEnd backEnd = MemoOf(state).BackEnd;
            return backEnd ?? Prefs;
        }

        private static Memo MemoOf(SettingsMenuState state)
        {
            lock (Memos)
            {
                Memo memo;
                if (!Memos.TryGetValue(state, out memo)) Memos[state] = memo = new Memo();
                return memo;
            }
        }

        /// <summary>The menu on top of MenusStack and its name (ASCharacter.Name, else the AS <c>name</c>), or null.</summary>
        private static string TopMenu(FlashScope flash, out object menu)
        {
            menu = null;
            object stack = flash.Class("gluic.components.menus", "MenusStack");
            if (stack == null) return null;
            menu = flash.Call(stack, "getMenuOnTopOfStack");
            if (menu == null) return null;
            string name = GameReflection.Property(menu, "Name") as string;
            if (name == SettingsMenuNames.SettingsMenu || name == SettingsMenuNames.GameplayMenu) return name;
            string asName = flash.Get(menu, "name") as string;
            return asName ?? name;
        }

        private static string Describe(Exception ex)
        {
            Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            return cause.GetType().Name + ": " + cause.Message;
        }

        private static void Write(ISettingsMenuHost host, string line)
        {
            try
            {
                Log log = host == null ? null : host.Log;
                if (log != null) log.Info(line);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>The game's back end: <see cref="PerformancePrefs"/> on <see cref="PerformancePrefs.Engine"/>.</summary>
        private sealed class PrefsBackEnd : IPerformanceBackEnd
        {
            public IEngineGraphics Engine { get { return PerformancePrefs.Engine; } }

            public int AutoRenderScaleSlider(IEngineGraphics engine)
            {
                return PerformancePrefs.AutoRenderScaleSlider(engine);
            }

            public float? ScaleOf(string renderScale)
            {
                return PerformancePrefs.ScaleOf(renderScale);
            }

            public bool SetGraphicsProfile(IPerformanceSettings settings, SettingsMenuState state, string choice)
            {
                return PerformancePrefs.SetGraphicsProfile(settings, state, choice);
            }

            public bool SetMsaa(IPerformanceSettings settings, SettingsMenuState state, string choice)
            {
                return PerformancePrefs.SetMsaa(settings, state, choice);
            }

            public bool SetRenderScale(IEngineGraphics engine, IPerformanceSettings settings, string value)
            {
                return PerformancePrefs.SetRenderScale(engine, settings, value);
            }

            public bool SetLowResMaps(IEngineGraphics engine, IPerformanceSettings settings, string choice)
            {
                return PerformancePrefs.SetLowResMaps(engine, settings, choice);
            }
        }
    }

    /// <summary>
    /// What <see cref="PerformancePage"/> needs of the Performance back end: <see cref="PerformancePrefs"/> in the game (its members
    /// one to one), a fake in the tests (<see cref="PerformancePage.UseBackEnd"/>).
    /// </summary>
    internal interface IPerformanceBackEnd
    {
        /// <summary><see cref="PerformancePrefs.Engine"/>.</summary>
        IEngineGraphics Engine { get; }

        /// <summary><see cref="PerformancePrefs.AutoRenderScaleSlider"/>: SCREEN SCALE's slider (0..100) while the key is auto.</summary>
        int AutoRenderScaleSlider(IEngineGraphics engine);

        /// <summary><see cref="PerformancePrefs.ScaleOf"/>: the engine scale of a stored percent (the page's live apply during a drag).</summary>
        float? ScaleOf(string renderScale);

        bool SetGraphicsProfile(IPerformanceSettings settings, SettingsMenuState state, string choice);

        bool SetMsaa(IPerformanceSettings settings, SettingsMenuState state, string choice);

        bool SetRenderScale(IEngineGraphics engine, IPerformanceSettings settings, string value);

        bool SetLowResMaps(IEngineGraphics engine, IPerformanceSettings settings, string choice);
    }
}
