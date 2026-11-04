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
    /// PERFORMANCE. <see cref="UiNatives"/> starts a run (<see cref="Schedule"/>) from <c>EnterSection("settings_main_screen")</c>,
    /// which MenuBase.onFocusIn calls before <c>MenuSettings.setup()</c> on every focus-in, the first entry and every return; the
    /// run's first step is at the start of the next frame, after setup(). Build 20: setup() brings the stock grid back on every
    /// focus-in (on Steam with new tile instances), so a single step that did not land left the stock menu; a run therefore tries
    /// again on the next frames, finds Settings by name also below another menu, and reads the grid back until it has stayed laid
    /// out, laying it out again when the stock grid came back (see <see cref="Schedule"/>).
    /// <para>The step reads everything before it writes anything (the build-13 rule): <c>menu_settings</c> is on top of
    /// <c>MenusStack</c> or in it, its <c>mc_panel.currentFrame</c> is 1 on Steam or 3 on Android (never the in-match 2, Kayac 4 or
    /// Chinese 5: any other frame fails the step like a missing instance), every instance the plan names exists and every tile is
    /// <see cref="SettingsMenuNames.TileWidth"/> wide (±2). It then writes: on Android first <c>btn_forum.setup({title, icon})</c>
    /// (if that throws, nothing else moves); <c>visible=false</c> and <c>mouseEnabled=false</c> on the removed instances; absolute
    /// <c>x</c>/<c>y</c> as floats on the kept ones (this also ends Steam's relative drift of CUSTOMER CARE and ABOUT, PC
    /// MenuSettings.as:253-258). <see cref="SettingsMenuState.TileActive"/> is set only
    /// after every write succeeded. One log line per run: "settings menu: frame 3, 7 tiles + ABOUT laid out" (with "after N frames
    /// (why it waited)" and "laid out again Kx ..." when that happened) or "... not laid out (reason; gave up after N frames)".</para>
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

        // ------------------------------------------------------------------ the run (one per focus-in)

        /// <summary>Frames a run tries or watches at most: about 2 s at 60 fps (4 s at 30).</summary>
        public const int MaxFrames = 120;

        /// <summary>
        /// Frames a run keeps watching a laid-out grid at least (about 0.75 s at 60 fps): the menu transitions of a return end
        /// within it (MenuBase.onAnimate's 0.3 and 0.5 s alpha tweens, AND-AS MenuBase.as:224-249; the companion's 0.4 s state
        /// tweens plus delays, MenuBaseCompanion.as:44-104), so anything that puts the stock grid back by then is caught.
        /// </summary>
        public const int SettleFrames = 45;

        /// <summary>Consecutive frames the grid must be read back as laid out before the run stops (once <see cref="SettleFrames"/> passed).</summary>
        public const int StableFrames = 2;

        /// <summary>How far (panel units) a kept instance may sit from its slot and still count as laid out (Flash keeps x/y in twips).</summary>
        public const double PlaceTolerance = 0.5;

        /// <summary>
        /// <c>EnterSection("settings_main_screen")</c>: starts this focus-in's run (the previous run stops at its next frame) and posts
        /// its first frame to the game thread (the start of the next frame, after the stock <c>setup()</c>). One log line when it
        /// cannot. Never throws.
        /// <para>A run, on each frame: finds <c>menu_settings</c> (on top of <c>MenusStack</c>, else by name while it is in the
        /// stack: a submenu that is still leaving, or one opened already), reads the panel as <see cref="Apply"/> does, and lays the
        /// grid out when it is not laid out; a read that fails (no Settings in the stack, another frame, a missing instance, a width)
        /// is tried again on the next frame. Once laid out it reads the grid back every frame until it has been found laid out on
        /// <see cref="StableFrames"/> frames in a row after <see cref="SettleFrames"/> frames, and lays it out again whenever the
        /// stock grid came back (as <c>setup()</c> brings it back on every focus-in: AND-AS MenuSettings.as:42-215 re-dresses FORUM,
        /// shows MORE GAMES and moves ABOUT and CUSTOMER CARE; PC-AS :42-345 goes to panel frame 3 and back to 1, which places new
        /// tile instances, and sets the 4-column x's). It stops at <see cref="MaxFrames"/> at the latest. One log line per run: laid
        /// out (after how many frames and why it waited; how often it had to lay the grid out again) or not laid out (why, after how
        /// many frames). A run that never laid the grid out sets <see cref="SettingsMenuState.RedressFailed"/> as
        /// <see cref="Apply"/> does; a write that throws ends the run at once. Every frame after the first is posted with
        /// <see cref="ISettingsMenuHost.PostNextFrame"/>: <see cref="ISettingsMenuHost.Post"/> from a step would run in the same
        /// drain, not on the next frame.</para>
        /// </summary>
        public static void Schedule(ISettingsMenuHost host, SettingsMenuState state)
        {
            try
            {
                if (host == null || state == null) return;
                var run = new Run(host, state, state.BeginLayoutRun());
                if (!host.Post(run.Step)) Write(host, "settings menu: not laid out (no game thread)");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// One step at once, without a run (game thread, after MenuSettings.setup()): the read, then the writes, and exactly one log
        /// line when it lays out or finds a reason not to (both switches off logs nothing). Any frame but the plan's is such a
        /// reason, the in-match 2 too: only <c>settings_main_screen</c> starts a run (the pause panel enters
        /// <c>pvp_settings_screen</c>), so a wrong frame here is an anomaly. Never throws; every wrapper it got is disposed.
        /// <para><see cref="SettingsMenuState.RedressFailed"/> is set (for the process: the R4 gate) whenever the step meant to re-dress
        /// the tile and did not, also for a reason found while reading: a game update that moved an instance would otherwise hide
        /// FRAME RATE LIMIT from Gameplay Settings with no page to reach it.</para>
        /// </summary>
        public static void Apply(ISettingsMenuHost host, SettingsMenuState state)
        {
            if (host == null || state == null) return;
            state.TileActive = false;
            Outcome outcome = Attempt(host, state, false);
            if (outcome.Kind == OutcomeKind.Off) return;
            if (outcome.Kind == OutcomeKind.Wrote) Write(host, outcome.Text);
            else Fail(host, state, outcome.Redress, outcome.Text);
        }

        private enum OutcomeKind
        {
            /// <summary>Both switches off: nothing to do.</summary>
            Off,

            /// <summary>Not readable now (<see cref="Outcome.Text"/> says why): tried again on the next frame.</summary>
            Wait,

            /// <summary>A write or a read threw: the run ends.</summary>
            Fail,

            /// <summary>The grid was laid out now.</summary>
            Wrote,

            /// <summary>The grid was read back as laid out; nothing written.</summary>
            InPlace,
        }

        private sealed class Outcome
        {
            public static readonly Outcome Off = new Outcome { Kind = OutcomeKind.Off };

            public OutcomeKind Kind;

            /// <summary>The log line (Wrote, InPlace) or the reason (Wait, Fail).</summary>
            public string Text;

            public bool Redress;

            /// <summary>The trim is on: a laid-out grid can be read back (the re-dress alone cannot).</summary>
            public bool Watchable;

            /// <summary>Wait: menu_settings is not in the stack at all.</summary>
            public bool Gone;
        }

        /// <summary>
        /// One look at the grid: read everything, then write everything, unless <paramref name="verify"/> finds it laid out already.
        /// The re-dress cannot be read back, so on Android the grid counts as laid out only while the tile is active.
        /// </summary>
        private static Outcome Attempt(ISettingsMenuHost host, SettingsMenuState state, bool verify)
        {
            bool redress = false;
            try
            {
                bool trim = host.SettingsMenuTrim;
                bool windows = host.IsWindows;
                redress = !windows && host.SettingsMenuPerformance;
                if (!trim && !redress) return Outcome.Off;
                object ui = host.ClientUi();
                if (ui == null) return new Outcome { Kind = OutcomeKind.Wait, Text = "no UI", Redress = redress };
                using (var flash = new FlashScope(ui))
                {
                    LayoutPlan plan = Plan(windows, redress);
                    var found = new Dictionary<string, object>(StringComparer.Ordinal);
                    int frame;
                    bool gone;
                    string problem = Read(flash, plan, trim, redress, found, out frame, out gone);
                    if (problem != null) return new Outcome { Kind = OutcomeKind.Wait, Text = problem, Redress = redress, Gone = gone };
                    string done = Done(plan, frame, trim);
                    if (verify && trim && (!redress || state.TileActive) && InPlace(flash, plan, found))
                        return new Outcome { Kind = OutcomeKind.InPlace, Text = done, Redress = redress, Watchable = true };
                    // The tile reads FORUM until the writes below have all succeeded.
                    state.TileActive = false;
                    WriteGrid(flash, state, plan, found, trim, redress);
                    return new Outcome { Kind = OutcomeKind.Wrote, Text = done, Redress = redress, Watchable = trim };
                }
            }
            catch (Exception ex)
            {
                Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                return new Outcome { Kind = OutcomeKind.Fail, Text = cause.GetType().Name + ": " + cause.Message, Redress = redress };
            }
        }

        /// <summary>One focus-in's attempts, one per frame (game thread); see <see cref="Schedule"/>.</summary>
        private sealed class Run
        {
            private readonly ISettingsMenuHost host;
            private readonly SettingsMenuState state;
            private readonly int id;
            private int frames;
            private int writes;
            private int firstWrite;
            private int stable;
            private bool redress;
            private string done;
            private string firstProblem;
            private string lastProblem;

            public Run(ISettingsMenuHost host, SettingsMenuState state, int id)
            {
                this.host = host;
                this.state = state;
                this.id = id;
            }

            public void Step()
            {
                try
                {
                    // A newer focus-in owns the grid: no line and no flag from this run.
                    if (state.LayoutRun != id) return;
                    frames++;
                    Outcome outcome = Attempt(host, state, true);
                    redress = outcome.Redress;
                    switch (outcome.Kind)
                    {
                        case OutcomeKind.Off:
                            return;
                        case OutcomeKind.Fail:
                            Fail(host, state, redress, outcome.Text + Frames());
                            return;
                        case OutcomeKind.Wrote:
                            writes++;
                            if (firstWrite == 0) firstWrite = frames;
                            stable = 0;
                            done = outcome.Text;
                            lastProblem = null;
                            // Trim off: the re-dress in place cannot be read back, so there is nothing to watch.
                            if (!outcome.Watchable)
                            {
                                Finish();
                                return;
                            }
                            break;
                        case OutcomeKind.InPlace:
                            stable++;
                            done = outcome.Text;
                            lastProblem = null;
                            if (stable >= StableFrames && frames >= SettleFrames)
                            {
                                Finish();
                                return;
                            }
                            break;
                        default:
                            stable = 0;
                            lastProblem = outcome.Text;
                            if (writes == 0 && firstProblem == null) firstProblem = outcome.Text;
                            if (writes > 0)
                            {
                                // Laid out, then Settings left the stack (Back to the main menu): nothing left to watch.
                                if (outcome.Gone)
                                {
                                    lastProblem = null;
                                    Finish();
                                    return;
                                }
                                // Settings is there but cannot be read as laid out: its tile may read FORUM again.
                                state.TileActive = false;
                            }
                            break;
                    }
                    if (frames >= MaxFrames)
                    {
                        End(null);
                        return;
                    }
                    if (!host.PostNextFrame(Step)) End("no next frame");
                }
                catch (Exception)
                {
                }
            }

            /// <summary>The run stops before it could watch to the end: no next frame, or <see cref="MaxFrames"/>.</summary>
            private void End(string why)
            {
                if (writes > 0)
                {
                    if (why != null && lastProblem == null) lastProblem = why;
                    Finish();
                    return;
                }
                string problem = lastProblem ?? firstProblem ?? "not readable";
                Fail(host, state, redress, problem + (why == null ? "" : "; " + why) + Frames());
            }

            /// <summary>The laid-out line: when the first write waited, how long and why; how often the grid had to be laid out again.</summary>
            private void Finish()
            {
                string line = done;
                if (firstWrite > 1) line += " after " + firstWrite.ToString(CultureInfo.InvariantCulture) + " frames (" + firstProblem + ")";
                if (writes > 1) line += "; laid out again " + (writes - 1).ToString(CultureInfo.InvariantCulture) + "x after the stock grid came back";
                if (lastProblem != null) line += "; then " + lastProblem + Frames();
                Write(host, line);
            }

            private string Frames()
            {
                return frames > 1 ? "; gave up after " + frames.ToString(CultureInfo.InvariantCulture) + " frames" : "";
            }
        }

        // ------------------------------------------------------------------ the read and the writes

        /// <summary>
        /// Null when every instance the step needs is found (<paramref name="found"/>, <paramref name="frame"/>), else why not;
        /// <paramref name="gone"/> when menu_settings is not in the stack at all. Reads only.
        /// </summary>
        private static string Read(FlashScope flash, LayoutPlan plan, bool trim, bool redress, Dictionary<string, object> found,
                                   out int frame, out bool gone)
        {
            frame = 0;
            gone = false;
            object stack = flash.Class("gluic.components.menus", "MenusStack");
            if (stack == null) return "no MenusStack";
            object top = flash.Call(stack, "getMenuOnTopOfStack");
            string topName = top == null ? null : NameOf(flash, top);
            object menu = topName == SettingsMenuNames.SettingsMenu ? top : null;
            if (menu == null)
            {
                // Below the menu on top (a submenu on its way out, or one opened already), by name while it is in the stack:
                // MenusStack.getMenuIndexInStack(String) and getMenuByName (null for a disposable menu with no instance).
                double? index = FlashInterop.Number(flash.Call(stack, "getMenuIndexInStack", SettingsMenuNames.SettingsMenu));
                if (index.HasValue && index.Value >= 0) menu = flash.Call(stack, "getMenuByName", SettingsMenuNames.SettingsMenu);
                if (menu == null)
                {
                    gone = true;
                    if (top == null) return "no menu on top";
                    return "the menu on top is " + (topName ?? "unnamed") + ", no " + SettingsMenuNames.SettingsMenu + " in the stack";
                }
            }
            object panel = flash.Child(menu, SettingsMenuNames.Panel);
            if (panel == null) return "no " + SettingsMenuNames.Panel;
            frame = Frame(flash, panel);
            if (frame != plan.Frame) return "frame " + frame.ToString(CultureInfo.InvariantCulture);

            // Trim off: only the re-dress, in place (T-B7), so only btn_forum is read.
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
            if (redress && (!plan.RedressForum || !found.ContainsKey(SettingsMenuNames.ForumTile))) return "no tile to re-dress";
            return null;
        }

        /// <summary>The menu's name: <c>ASCharacter.Name</c>, else the AS <c>name</c>.</summary>
        private static string NameOf(FlashScope flash, object menu)
        {
            string name = GameReflection.Property(menu, "Name") as string;
            if (name == SettingsMenuNames.SettingsMenu) return name;
            string asName = flash.Get(menu, "name") as string;
            return asName ?? name;
        }

        /// <summary>Every kept instance in its slot (±<see cref="PlaceTolerance"/>) and every removed one invisible. Reads only.</summary>
        private static bool InPlace(FlashScope flash, LayoutPlan plan, Dictionary<string, object> found)
        {
            foreach (Placement p in plan.Kept)
            {
                double? x = flash.Number(found[p.Name], "x");
                double? y = flash.Number(found[p.Name], "y");
                if (x == null || y == null || Math.Abs(x.Value - (float)p.X) > PlaceTolerance || Math.Abs(y.Value - (float)p.Y) > PlaceTolerance)
                    return false;
            }
            foreach (string hidden in plan.Hidden)
            {
                object visible = flash.Get(found[hidden], "visible");
                if (!(visible is bool) || (bool)visible) return false;
            }
            return true;
        }

        /// <summary>
        /// The writes: the re-dress first (if it throws, nothing else moves), then hide, then place; a write that throws after the
        /// re-dress puts the stock FORUM dress back and rethrows. <see cref="SettingsMenuState.TileActive"/> only when all succeeded.
        /// </summary>
        private static void WriteGrid(FlashScope flash, SettingsMenuState state, LayoutPlan plan, Dictionary<string, object> found,
                                      bool trim, bool redress)
        {
            object forum = redress ? found[SettingsMenuNames.ForumTile] : null;
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
        }

        /// <summary>"settings menu: frame 3, 7 tiles + ABOUT laid out", or the trim-off re-dress line.</summary>
        private static string Done(LayoutPlan plan, int frame, bool trim)
        {
            string where = "settings menu: frame " + frame.ToString(CultureInfo.InvariantCulture) + ", ";
            if (!trim) return where + "PERFORMANCE tile re-dressed in place (trim off)";
            int tiles = 0;
            foreach (Placement p in plan.Kept)
            {
                if (IsTile(p)) tiles++;
            }
            return where + tiles.ToString(CultureInfo.InvariantCulture) + " tiles + ABOUT laid out";
        }

        /// <summary>
        /// The panel's frame, 1-based like the AS: the AS <c>currentFrame</c>, else <c>ASCharacter.CurrentFrame</c> + 1; 0 when
        /// neither reads. The native property is 0-based (libnativeengine's AS getter adds one to the same vtable call; the game's
        /// own C# does <c>GotoAndStop(GetMember("currentFrame") - 1)</c>, FirstPersonEquipmentComponent.cs:544), so read raw it
        /// would make Android's frame 3 the in-match 2 and Steam's 1 a 0.
        /// </summary>
        private static int Frame(FlashScope flash, object panel)
        {
            double? number = flash.Number(panel, "currentFrame");
            if (number.HasValue) return (int)number.Value;
            object frame = GameReflection.Property(panel, "CurrentFrame");
            return frame is int ? (int)frame + 1 : 0;
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
