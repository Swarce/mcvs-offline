// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (reflection on Gameloft.Engine and Game): compiled into the embedded DLL and the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded.Admin.SettingsMenu
{
    /// <summary>
    /// R1 and R2 (docs/design/settings-menu.md §1.1, §1.2, §3.2; skeptic A1-A3): the main-menu Settings grid without SOCIAL MEDIA,
    /// FORUM, CUSTOMER CARE and MORE GAMES, the tiles left centred and ABOUT alone below them; on Android btn_forum re-dressed as
    /// PERFORMANCE. <see cref="UiNatives"/> posts <see cref="Apply"/> from <c>EnterSection("settings_main_screen")</c>, which
    /// MenuBase.onFocusIn calls before <c>MenuSettings.setup()</c>; the step runs at the start of the next frame, after setup().
    /// <para>The step reads everything before it writes anything (the build-13 rule): the menu on top of <c>MenusStack</c> is
    /// <c>menu_settings</c>, its <c>mc_panel.currentFrame</c> is 1 on Steam or 3 on Android (never the in-match 2, Kayac 4 or
    /// Chinese 5), every instance the plan names exists and every tile is <see cref="SettingsMenuNames.TileWidth"/> wide (±2). It then
    /// writes: on Android first <c>btn_forum.setup({title, icon})</c> (if that throws, nothing else moves); <c>visible=false</c> and
    /// <c>mouseEnabled=false</c> on the removed instances; absolute <c>x</c>/<c>y</c> as floats on the kept ones (this also ends Steam's
    /// relative drift of CUSTOMER CARE and ABOUT, PC MenuSettings.as:253-258). <see cref="SettingsMenuState.TileActive"/> is set only
    /// after every write succeeded. One log line: "settings menu: frame 3, 7 tiles + ABOUT laid out" or "... not laid out (reason)".</para>
    /// <para>Settings (read per step): <c>admin.settingsMenu.trim</c> off → no relayout; <c>admin.settingsMenu.performance</c> on
    /// (Android) → the re-dress, in place when the trim is off (T-B7). Performance off with the trim on: FORUM stays hidden and row 2
    /// is HELP and LANGUAGES under row 1's middle columns.</para>
    /// </summary>
    internal static class SettingsMenuLayout
    {
        /// <summary>One kept instance and where it goes (panel units, origin at the viewport centre, +50 in y; skeptic A3).</summary>
        internal sealed class Placement
        {
            public Placement(string name, double x, double y)
            {
                Name = name;
                X = x;
                Y = y;
            }

            public string Name { get; private set; }

            public double X { get; private set; }

            public double Y { get; private set; }

            public override string ToString()
            {
                return Name + " (" + FlashInterop.Text(X) + ", " + FlashInterop.Text(Y) + ")";
            }
        }

        /// <summary>A platform's grid (§1.1, §1.2): the frame it applies to, where the kept instances go and which ones hide.</summary>
        internal sealed class LayoutPlan
        {
            public LayoutPlan(int frame, IList<Placement> kept, IList<string> hidden, bool redressForum)
            {
                Frame = frame;
                Kept = new List<Placement>(kept ?? new Placement[0]);
                Hidden = new List<string>(hidden ?? new string[0]);
                RedressForum = redressForum;
            }

            /// <summary><see cref="SettingsMenuNames.SteamMainFrame"/> or <see cref="SettingsMenuNames.AndroidMainFrame"/>.</summary>
            public int Frame { get; private set; }

            /// <summary>The tiles (BtnSetting, width-checked) and ABOUT (a ThinBtn, placed by its plate), in reading order.</summary>
            public List<Placement> Kept { get; private set; }

            /// <summary>Instances that get <c>visible=false</c> and <c>mouseEnabled=false</c>.</summary>
            public List<string> Hidden { get; private set; }

            /// <summary>Android: btn_forum becomes PERFORMANCE (it is in <see cref="Kept"/>, not in <see cref="Hidden"/>).</summary>
            public bool RedressForum { get; private set; }

            /// <summary>
            /// Every panel instance the step looks up (kept, then hidden): 10 on Steam, 11 on Android; with the menu on top and
            /// <c>mc_panel</c> these are the 12 and 13 lookups of T-B2.
            /// </summary>
            public List<string> Lookups
            {
                get
                {
                    var names = new List<string>();
                    foreach (Placement p in Kept) names.Add(p.Name);
                    names.AddRange(Hidden);
                    return names;
                }
            }
        }

        // ------------------------------------------------------------------ the plans (§1.1, §1.2; pure)

        /// <summary>Steam columns: Gameloft's own centred 3-wide grid (Kayac frame 4 of both SWFs; grid.md §3).</summary>
        private static readonly double[] SteamColumns = { -715.6, -232.7, 250.2 };

        /// <summary>Android row 1: the stock frame-3 columns, already centred (centre 0.675; S22 screen x 152-1849).</summary>
        private static readonly double[] AndroidRow1 = { -948, -471.05, 6.85, 484 };

        /// <summary>Android row 2 with PERFORMANCE: row 1's pitch (about 477.4), centred on row 1's centre (grid.md §4).</summary>
        private static readonly double[] AndroidRow2 = { -709.4, -232.0, 245.4 };

        /// <summary>Android row 2 without PERFORMANCE: row 1's two middle columns (centre 0.575).</summary>
        private static readonly double[] AndroidRow2Pair = { -471.05, 6.85 };

        public const double Row1Y = -427.9;
        public const double Row2Y = -50.9;
        public const double ButtonsY = 354.1;

        /// <summary>
        /// ABOUT alone, centred on its 365.8-wide plate, not on its runtime <c>.width</c> (about 401). Skeptic A2: Gameloft's own
        /// one-button constant is -177 (the middle button's x, MenuSettings.as:208/226), which leaves the plate 5.9 units right of
        /// centre; the owner asked for ABOUT centred, so the plate's centre it is (T-B1 pins it).
        /// </summary>
        public const double AboutX = -SettingsMenuNames.ThinButtonPlateWidth / 2;

        /// <summary>The stock FORUM dress (AND MenuSettings.as:116-119), put back when a write after the re-dress fails.</summary>
        public const string ForumTitle = "setting.ForumButton";

        public const string ForumIcon = "Menus/assets/png/icons/settings_forum.png";

        /// <summary>The grid of §1.1 (<paramref name="windows"/>: Steam, frame 1) or §1.2 (Android, frame 3, with PERFORMANCE). Pure (T-B1).</summary>
        public static LayoutPlan Plan(bool windows)
        {
            return Plan(windows, true);
        }

        /// <summary>
        /// The grid for the switches now. On Android with <paramref name="performance"/> off, FORUM stays hidden (design §3.3) and HELP
        /// and LANGUAGES sit under row 1's two middle columns. Steam ignores <paramref name="performance"/>: its own tile stays.
        /// </summary>
        public static LayoutPlan Plan(bool windows, bool performance)
        {
            if (windows)
            {
                return new LayoutPlan(SettingsMenuNames.SteamMainFrame, new[]
                {
                    new Placement(SettingsMenuNames.GeneralTile, SteamColumns[0], Row1Y),
                    new Placement(SettingsMenuNames.AudioTile, SteamColumns[1], Row1Y),
                    new Placement(SettingsMenuNames.AccountTile, SteamColumns[2], Row1Y),
                    new Placement(SettingsMenuNames.HelpTile, SteamColumns[0], Row2Y),
                    new Placement(SettingsMenuNames.LanguageTile, SteamColumns[1], Row2Y),
                    new Placement(SettingsMenuNames.PerformanceTile, SteamColumns[2], Row2Y),
                    new Placement(SettingsMenuNames.AboutButton, AboutX, ButtonsY),
                },
                // Not btn_more: Steam's setup() hides it on every focus-in (PC MenuSettings.as:255).
                new[] { SettingsMenuNames.SocialTile, SettingsMenuNames.ForumTile, SettingsMenuNames.CustomerButton }, false);
            }
            var kept = new List<Placement>
            {
                new Placement(SettingsMenuNames.GeneralTile, AndroidRow1[0], Row1Y),
                new Placement(SettingsMenuNames.AudioTile, AndroidRow1[1], Row1Y),
                new Placement(SettingsMenuNames.CustomizeTile, AndroidRow1[2], Row1Y),
                new Placement(SettingsMenuNames.AccountTile, AndroidRow1[3], Row1Y),
            };
            var hidden = new List<string> { SettingsMenuNames.SocialTile, SettingsMenuNames.CustomerButton, SettingsMenuNames.MoreGamesButton };
            if (performance)
            {
                kept.Add(new Placement(SettingsMenuNames.HelpTile, AndroidRow2[0], Row2Y));
                kept.Add(new Placement(SettingsMenuNames.LanguageTile, AndroidRow2[1], Row2Y));
                kept.Add(new Placement(SettingsMenuNames.ForumTile, AndroidRow2[2], Row2Y));
            }
            else
            {
                kept.Add(new Placement(SettingsMenuNames.HelpTile, AndroidRow2Pair[0], Row2Y));
                kept.Add(new Placement(SettingsMenuNames.LanguageTile, AndroidRow2Pair[1], Row2Y));
                hidden.Insert(1, SettingsMenuNames.ForumTile);
            }
            kept.Add(new Placement(SettingsMenuNames.AboutButton, AboutX, ButtonsY));
            return new LayoutPlan(SettingsMenuNames.AndroidMainFrame, kept, hidden, performance);
        }

        /// <summary>A BtnSetting of the plan (width-checked); ABOUT is the one ThinBtn.</summary>
        public static bool IsTile(Placement placement)
        {
            return placement != null && placement.Name != SettingsMenuNames.AboutButton;
        }

        // ------------------------------------------------------------------ the step

        /// <summary>Posts <see cref="Apply"/> to the game thread; one log line when it cannot. Never throws.</summary>
        public static void Schedule(ISettingsMenuHost host, SettingsMenuState state)
        {
            try
            {
                if (host == null || state == null) return;
                if (!host.Post(() => Apply(host, state))) Write(host, "settings menu: not laid out (no game thread)");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Game thread, after MenuSettings.setup(): the step above on <see cref="ISettingsMenuHost.ClientUi"/>. Logs exactly one line
        /// when it lays out or finds a reason not to (the in-match frame 2 logs nothing: that panel opens every match; both switches
        /// off logs nothing either). Never throws; every wrapper it got is disposed.
        /// <para><see cref="SettingsMenuState.RedressFailed"/> is set (for the process: the R4 gate) whenever the step meant to re-dress
        /// the tile and did not, also for a reason found while reading: a game update that moved an instance would otherwise hide
        /// FRAME RATE LIMIT from Gameplay Settings with no page to reach it.</para>
        /// </summary>
        public static void Apply(ISettingsMenuHost host, SettingsMenuState state)
        {
            if (host == null || state == null) return;
            bool redress = false;
            try
            {
                state.TileActive = false;
                bool trim = host.SettingsMenuTrim;
                bool windows = host.IsWindows;
                redress = !windows && host.SettingsMenuPerformance;
                if (!trim && !redress) return;
                object ui = host.ClientUi();
                if (ui == null)
                {
                    Fail(host, state, redress, "no UI");
                    return;
                }
                using (var flash = new FlashScope(ui))
                {
                    string done;
                    bool silent;
                    string problem = TryApply(flash, state, Plan(windows, redress), trim, redress, out done, out silent);
                    if (problem == null) Write(host, done);
                    else if (!silent) Fail(host, state, redress, problem);
                }
            }
            catch (Exception ex)
            {
                Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Fail(host, state, redress, cause.GetType().Name + ": " + cause.Message);
            }
        }

        /// <summary>
        /// Null when done (<paramref name="done"/> is the log line), else why not (<paramref name="silent"/>: not worth a line). Reads
        /// everything before it writes anything; a write that throws after the re-dress puts the stock FORUM dress back and rethrows.
        /// </summary>
        private static string TryApply(FlashScope flash, SettingsMenuState state, LayoutPlan plan, bool trim, bool redress, out string done, out bool silent)
        {
            done = null;
            silent = false;
            object stack = flash.Class("gluic.components.menus", "MenusStack");
            if (stack == null) return "no MenusStack";
            object menu = flash.Call(stack, "getMenuOnTopOfStack");
            if (menu == null) return "no menu on top";
            string name = GameReflection.Property(menu, "Name") as string;
            if (name != SettingsMenuNames.SettingsMenu)
            {
                string asName = flash.Get(menu, "name") as string;
                if (asName != null) name = asName;
            }
            if (name != SettingsMenuNames.SettingsMenu) return "the menu on top is " + (name ?? "unnamed");
            object panel = flash.Child(menu, SettingsMenuNames.Panel);
            if (panel == null) return "no " + SettingsMenuNames.Panel;
            int frame = Frame(flash, panel);
            if (frame != plan.Frame)
            {
                // The in-match pause panel has no tile to re-dress, and it is not worth a line on every match.
                silent = frame == SettingsMenuNames.InGameFrame;
                return "frame " + frame.ToString(CultureInfo.InvariantCulture);
            }

            // Trim off: only the re-dress, in place (T-B7), so only btn_forum is read.
            var found = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (Placement p in plan.Kept)
            {
                if (!trim && p.Name != SettingsMenuNames.ForumTile) continue;
                object item = flash.Child(panel, p.Name);
                if (item == null) return "no " + p.Name;
                if (IsTile(p))
                {
                    double? width = flash.Number(item, "width");
                    if (width == null) return p.Name + " has no width";
                    if (Math.Abs(width.Value - SettingsMenuNames.TileWidth) > SettingsMenuNames.TileWidthTolerance)
                        return p.Name + " is " + FlashInterop.Text(width.Value) + " wide";
                }
                found[p.Name] = item;
            }
            if (trim)
            {
                foreach (string hidden in plan.Hidden)
                {
                    object item = flash.Child(panel, hidden);
                    if (item == null) return "no " + hidden;
                    found[hidden] = item;
                }
            }
            object forum = null;
            if (redress && (!plan.RedressForum || !found.TryGetValue(SettingsMenuNames.ForumTile, out forum))) return "no tile to re-dress";

            // The writes: the re-dress first (if it throws, nothing else moves), then hide, then place.
            if (redress) flash.Call(forum, "setup", Dress(SettingsMenuNames.PerformanceTitle, SettingsMenuNames.PerformanceIcon));
            if (trim)
            {
                try
                {
                    foreach (string hidden in plan.Hidden)
                    {
                        flash.Set(found[hidden], "visible", false);
                        flash.Set(found[hidden], "mouseEnabled", false);
                    }
                    // Absolute, as float (an AS Number; ASValue.SetObject makes a double a managed object): this ends Steam's drift.
                    foreach (Placement p in plan.Kept)
                    {
                        flash.Set(found[p.Name], "x", (float)p.X);
                        flash.Set(found[p.Name], "y", (float)p.Y);
                    }
                }
                catch (Exception)
                {
                    // The tile must not read PERFORMANCE while a tap on it still reaches the game's GoToForum.
                    if (redress) Undress(flash, forum);
                    throw;
                }
                state.CountLayout();
            }
            if (redress) state.TileActive = true;

            string where = "settings menu: frame " + frame.ToString(CultureInfo.InvariantCulture) + ", ";
            if (!trim)
            {
                done = where + "PERFORMANCE tile re-dressed in place (trim off)";
                return null;
            }
            int tiles = 0;
            foreach (Placement p in plan.Kept)
            {
                if (IsTile(p)) tiles++;
            }
            done = where + tiles.ToString(CultureInfo.InvariantCulture) + " tiles + ABOUT laid out";
            return null;
        }

        /// <summary>The panel's frame: <c>ASCharacter.CurrentFrame</c>, else the AS <c>currentFrame</c>; 0 when neither reads.</summary>
        private static int Frame(FlashScope flash, object panel)
        {
            object frame = GameReflection.Property(panel, "CurrentFrame");
            if (frame is int) return (int)frame;
            double? number = flash.Number(panel, "currentFrame");
            return number.HasValue ? (int)number.Value : 0;
        }

        /// <summary>BtnBase.setup's argument (an IDictionary becomes an AS object; AdminHooks.cs:78).</summary>
        private static Dictionary<string, object> Dress(string title, string icon)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "title", title }, { "icon", icon } };
        }

        private static void Undress(FlashScope flash, object forum)
        {
            try
            {
                flash.Call(forum, "setup", Dress(ForumTitle, ForumIcon));
            }
            catch (Exception)
            {
            }
        }

        private static void Fail(ISettingsMenuHost host, SettingsMenuState state, bool redress, string problem)
        {
            if (redress) state.RedressFailed = true;
            Write(host, "settings menu: not laid out (" + problem + ")");
        }

        private static void Write(ISettingsMenuHost host, string line)
        {
            try
            {
                Log log = host.Log;
                if (log != null) log.Info(line);
            }
            catch (Exception)
            {
            }
        }
    }
}
