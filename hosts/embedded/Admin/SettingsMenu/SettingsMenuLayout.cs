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
    /// run's first step is at the start of the next frame, after setup(). Build 20, a mitigation and a diagnostic for the owner's
    /// build-19 report (the stock menu after a tab and Back, both platforms), not a proven fix: setup() brings the stock grid back on
    /// every focus-in (on Steam with new tile instances), so whatever kept build 19's single step from landing left the stock menu.
    /// Which precondition failed on the device is not known: in the gluic order (MenusStack.popMenu pops before focusGiven, which
    /// sends FOCUS_IN synchronously, then setup() at once) a single step finds Settings on top with the grid readable. A run therefore
    /// tries again on the next frames, finds Settings by name also below another menu, reads the grid back until it has stayed laid
    /// out and lays it out again when the stock grid came back (see <see cref="Schedule"/>); its first frame logs the focus-in as it
    /// finds it (<c>settings menu: focus-in N ...</c>), so the next device run names the precondition.
    /// <para>Build 21 (owner, build 20: "the normal menu flashes for a split second before being set to our layout again"): the
    /// run's first step comes one frame after setup(), so the render of the focus-in frame drew the stock grid. Now the grid is
    /// laid out in the focus-in frame itself, inside the AS call stack of setup() after its last stock write and before that
    /// frame's render (<see cref="SameFrame"/>: Steam at its GetLanguage, Android at addButtonListeners' GetPlatformId). The run
    /// stays as the safety net: its first frame finds the grid laid out and says "laid out in the focus-in frame", or lays it
    /// out as before; every laid-out line ends with how many renders drew a grid that was not ours ("stock grid drawn N
    /// frames").</para>
    /// <para>Build 22 (owner, build 21 on the S22: "it flashes the more games and the forum button after, the trim remains"): on
    /// Android every visible effect of setup() is made to draw nothing, so no render can show the stock tiles whatever the timing:
    /// the engine's LocId remap turns setup()'s FORUM title into PERFORMANCE (<see cref="EnsureRemap"/>, <see cref="ForumRemap"/>),
    /// and <see cref="PreDress"/>, inside EnterSection's native before setup(), puts the icon out of setup()'s reach and the removed
    /// tiles off the panel (y, alpha, mouse: properties setup() never writes), and leaves ABOUT at setup()'s own x. The same-frame
    /// step then only tidies (its gate: MORE GAMES shown again at setup()'s x, false whenever unreadable) and the run reads back.
    /// <c>admin.settingsMenu.sameFrame</c> switches all Flash work inside natives off (PreDress and the same-frame step), and
    /// <see cref="SameFrameBreadcrumb"/> turns it off by itself after a start that ended inside it. Steam is unchanged.</para>
    /// <para>Build 23 (owner, build 22 on the S22): P1, the PERFORMANCE title was centred on its tile, because our re-dress ran
    /// before setup() turns BtnSimple's needAlign off (BtnSimple.as:92-95; MenuSettings.as:110-120): every btn_forum.setup of ours
    /// on Android goes through <see cref="ForumSetup"/> (needAlign off first, a centred title put back). P2, ABOUT sat right of
    /// centre at setup()'s 208: with <c>admin.settingsMenu.aboutCentre</c> on, <see cref="PreDress"/> draws ABOUT on MORE GAMES'
    /// button, which setup() itself shows at its centred -177 (:210, :212) and titles through the remap
    /// (<see cref="MoreGamesRemap"/>), the real ABOUT off the panel; the tap goes to <see cref="AboutRedirect"/>. P3, a stutter on
    /// the gear: no same-frame look when the dress before setup() proves it has nothing left to do
    /// (<see cref="SettingsMenuState.PreDressSealed"/>), and the lines carry timings (<see cref="SettingsMenuTimings"/>).</para>
    /// <para>The step reads everything before it writes anything (the build-13 rule): <c>menu_settings</c> is on top of
    /// <c>MenusStack</c> or in it, its <c>mc_panel.currentFrame</c> is 1 on Steam or 3 on Android (never the in-match 2, Kayac 4 or
    /// Chinese 5: any other frame fails the step like a missing instance), every instance the plan names exists and every tile is
    /// <see cref="SettingsMenuNames.TileWidth"/> wide (±2). It then writes: on Android first <c>btn_forum.setup({title, icon})</c>
    /// (if that throws, nothing else moves); <c>visible=false</c> and <c>mouseEnabled=false</c> on the removed instances; absolute
    /// <c>x</c>/<c>y</c> as floats on the kept ones (this also ends Steam's relative drift of CUSTOMER CARE and ABOUT, PC
    /// MenuSettings.as:253-258). <see cref="SettingsMenuState.TileActive"/> is set only
    /// after every write succeeded. One log line per run: "settings menu: frame 3, 7 tiles + ABOUT laid out" (with "after N frames
    /// (why it waited)" and "laid out again Kx ..." when that happened) or "... not laid out (reason; gave up after N frames)", or
    /// "(reason; Settings left after N frames)".</para>
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
        /// centre; the owner asked for ABOUT centred, so the plate's centre it is (T-B1 pins it). Steam only since build 22: Android
        /// keeps ABOUT at setup()'s own <see cref="SettingsMenuNames.AndroidAboutX"/>.
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
            return Plan(windows, performance, false);
        }

        /// <summary>
        /// Build 23: with <paramref name="aboutOnMoreGames"/> (Android only; <see cref="PreDress"/> decides it per run) ABOUT is drawn on
        /// MORE GAMES' button: btn_more is kept at setup()'s own <see cref="SettingsMenuNames.StockMoreGamesX"/> (AND-AS :212) and
        /// btn_credits is removed (off the panel, its x at <see cref="SettingsMenuNames.AboutMarkerX"/>). Otherwise build 22's grid.
        /// </summary>
        public static LayoutPlan Plan(bool windows, bool performance, bool aboutOnMoreGames)
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
            bool about = aboutOnMoreGames;
            var hidden = new List<string>
            {
                SettingsMenuNames.SocialTile, SettingsMenuNames.CustomerButton, about ? SettingsMenuNames.AboutButton : SettingsMenuNames.MoreGamesButton,
            };
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
            // Build 23: MORE GAMES' button, titled ABOUT by the remap, where setup() puts it (AND-AS :212). Build 22: ABOUT where setup()
            // puts it (:213), so its last stock write draws nothing new (ThinBtn's timeline moves its children, so they cannot be
            // shifted to centre the plate; SettingsMenuNames.AndroidAboutX).
            if (about) kept.Add(new Placement(SettingsMenuNames.MoreGamesButton, SettingsMenuNames.StockMoreGamesX, ButtonsY));
            else kept.Add(new Placement(SettingsMenuNames.AboutButton, SettingsMenuNames.AndroidAboutX, ButtonsY));
            return new LayoutPlan(SettingsMenuNames.AndroidMainFrame, kept, hidden, performance);
        }

        /// <summary>
        /// A BtnSetting of the plan (width-checked); the ThinBtn kept below the tiles is not (ABOUT, or build 23's MORE GAMES drawn as
        /// ABOUT, whose runtime width is about 401: grid.md:83).
        /// </summary>
        public static bool IsTile(Placement placement)
        {
            return placement != null && placement.Name != SettingsMenuNames.AboutButton && placement.Name != SettingsMenuNames.MoreGamesButton;
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

        /// <summary>Half a twip (Flash keeps x in twips, 0.05 units): tells setup()'s btn_more.x -177 from the timeline's -176.9.</summary>
        public const double MarkerTolerance = 0.025;

        /// <summary>
        /// <c>EnterSection("settings_main_screen")</c>: starts this focus-in's run (the previous run stops at its next frame), arms its
        /// same-frame step (<see cref="SameFrame"/>, trim on; the previous one ends) and posts the run's first frame to the game
        /// thread (the start of the next frame, after the stock <c>setup()</c> and the same-frame step). One log line when it
        /// cannot. Never throws.
        /// <para>A run, on each frame: finds <c>menu_settings</c> (on top of <c>MenusStack</c>, else by name while it is in the
        /// stack: a submenu that is still leaving, or one opened already), reads the panel as <see cref="Apply"/> does, and lays the
        /// grid out when it is not laid out; a read that fails (no Settings in the stack, another frame, a missing instance, a width)
        /// is tried again on the next frame, until Settings has left the stack after a frame found it there (then one "not laid out
        /// (...; Settings left after N frames)" line at once). Once laid out it reads the grid back every frame until it has been found laid out on
        /// <see cref="StableFrames"/> frames in a row after <see cref="SettleFrames"/> frames, and lays it out again whenever the
        /// stock grid came back (as <c>setup()</c> brings it back on every focus-in: AND-AS MenuSettings.as:42-215 re-dresses FORUM,
        /// shows MORE GAMES and moves ABOUT and CUSTOMER CARE; PC-AS :42-345 goes to panel frame 3 and back to 1, which places new
        /// tile instances, and sets the 4-column x's). It stops at <see cref="MaxFrames"/> at the latest. One log line per run: laid
        /// out (after how many frames and why it waited; how often it had to lay the grid out again) or not laid out (why, after how
        /// many frames). A run that never laid the grid out sets <see cref="SettingsMenuState.RedressFailed"/> as
        /// <see cref="Apply"/> does, also when Settings left early (the in-match grid's R4 gate needs no tile, so a panel this build
        /// cannot lay out must show the Gameplay rows), on Android with the FORUM remap on only when btn_forum is not on the panel
        /// (<see cref="Outcome.Covered"/>); a write that throws ends the run at once. After a write,
        /// <see cref="SettingsMenuState.TileActive"/> stays on through a failed read unless the panel is another one (another frame,
        /// an instance missing): only then can the tile read FORUM again within the run. "laid out again" counts the writes that
        /// found the stock grid back, not the re-dresses after another panel. Every frame after the first is posted with
        /// <see cref="ISettingsMenuHost.PostNextFrame"/>: <see cref="ISettingsMenuHost.Post"/> from a step would run in the same
        /// drain, not on the next frame.</para>
        /// <para>The first frame ends the same-frame step: a grid it laid out counts as the run's write at frame 0 (the line says "in
        /// the focus-in frame"), and the run goes on reading it back as after any write. "stock grid drawn N frames" counts the
        /// frames whose start found a grid that was not ours (not laid out yet, the stock grid back, another panel): each was drawn
        /// by the render before it.</para>
        /// </summary>
        public static void Schedule(ISettingsMenuHost host, SettingsMenuState state)
        {
            try
            {
                if (host == null || state == null) return;
                // Once per process (UiNatives.Start runs it at start, off the game thread): a breadcrumb of the last start.
                SameFrameBreadcrumb.Check(host, state);
                int id = state.BeginLayoutRun();
                var run = new Run(host, state, id);
                // The previous focus-in's same-frame step ends here; this one's is armed for the rest of this frame (UiNatives
                // registers the native it watches right after the dress before setup()).
                Disarm(state);
                state.PreDressRun = 0;
                state.PreDressText = null;
                state.PreDressDone = null;
                state.PreDressSealed = 0;
                state.SealProblem = null;
                state.AboutOnMoreGames = false;
                bool inNative = InNativeAllowed(host, state);
                if (host.SettingsMenuTrim && inNative) state.SameFrame = new SameFrame(id, host.IsWindows);
                if (!host.Post(run.Step))
                {
                    Disarm(state);
                    Write(host, "settings menu: not laid out (no game thread)");
                    return;
                }
                // The run's first frame deletes the breadcrumb, so only a posted run lets PreDress work inside the native.
                if (inNative && !host.IsWindows) state.PreDressRun = id;
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

            /// <summary>
            /// Wait: the panel is not the one laid out (no mc_panel, a frame other than the plan's, an instance missing), so the tile
            /// may be a stock FORUM again. Not for a width, which leaves the instances (and the tile's dress) as they were.
            /// </summary>
            public bool Changed;

            /// <summary>Wrote while watching: the read-back found a kept instance off its slot or a removed one visible.</summary>
            public bool Restocked;

            /// <summary>InPlace on Android: setup() had shown a removed instance again, off the panel (drawn as nothing; hidden again now).</summary>
            public bool OffPanel;

            /// <summary>The FORUM remap was on: the tile reads PERFORMANCE whatever the step did.</summary>
            public bool Remapped;

            /// <summary>
            /// The remap covers the tile: it was on, and btn_forum is on the panel (on the plan's frame, or the read stopped at a
            /// width, which leaves the instances as they were). Only then does a run that never laid the grid out leave
            /// <see cref="SettingsMenuState.RedressFailed"/> alone: with no tile there is no page to reach.
            /// </summary>
            public bool Covered;
        }

        /// <summary>
        /// One look at the grid: read everything, then write everything, unless <paramref name="verify"/> finds it laid out already.
        /// The re-dress cannot be read back, so on Android the grid counts as laid out only while the tile is active.
        /// </summary>
        private static Outcome Attempt(ISettingsMenuHost host, SettingsMenuState state, bool verify)
        {
            bool redress = false;
            bool remapped = false;
            Dictionary<string, object> found = null;
            try
            {
                bool trim = host.SettingsMenuTrim;
                bool windows = host.IsWindows;
                redress = !windows && host.SettingsMenuPerformance;
                if (!trim && !redress) return Outcome.Off;
                remapped = redress && state.ForumRemapped;
                object ui = host.ClientUi();
                if (ui == null) return new Outcome { Kind = OutcomeKind.Wait, Text = "no UI", Redress = redress, Remapped = remapped };
                using (var flash = new FlashScope(ui))
                {
                    // Build 23: the bottom row the dress before setup() chose for this run (ABOUT on MORE GAMES, or build 22's).
                    LayoutPlan plan = Plan(windows, redress, !windows && state.AboutOnMoreGames);
                    found = new Dictionary<string, object>(StringComparer.Ordinal);
                    int frame;
                    bool gone;
                    bool changed;
                    string problem = Read(flash, plan, trim, redress, found, out frame, out gone, out changed);
                    if (problem != null)
                    {
                        // The read stops at the first instance missing, so btn_forum may be there unread (a missing HELP).
                        bool covered = remapped && (!changed || found.ContainsKey(SettingsMenuNames.ForumTile) ||
                                                    frame == plan.Frame && TileOnPanel(flash));
                        return new Outcome { Kind = OutcomeKind.Wait, Text = problem, Redress = redress, Gone = gone, Changed = changed, Remapped = remapped, Covered = covered };
                    }
                    string done = Done(plan, frame, trim);
                    var offPanel = new List<string>();
                    bool placed = verify && trim && InPlace(flash, plan, found, !windows, false, offPanel);
                    // Build 22: the tile is read back too (the remap, or our re-dress, and the icon), also with the trim off.
                    bool tile = !redress || TileReads(flash, state, found[SettingsMenuNames.ForumTile]);
                    if (verify && (placed || !trim) && tile && (trim || !windows && state.ForumRemapped))
                    {
                        if (redress) state.TileActive = true;
                        // MORE GAMES shown again by setup() but off the panel (nothing drawn): hidden again, its marker back.
                        Tidy(flash, found, offPanel);
                        return new Outcome { Kind = OutcomeKind.InPlace, Text = done, Redress = redress, Watchable = trim, OffPanel = offPanel.Count > 0, Remapped = remapped };
                    }
                    // Without the remap the tile reads FORUM until the writes below have all succeeded.
                    if (!remapped) state.TileActive = false;
                    var title = new TitleReport();
                    try
                    {
                        WriteGrid(flash, state, plan, found, trim, redress, !windows, title);
                    }
                    finally
                    {
                        ReportTitle(host, state, title);
                    }
                    return new Outcome { Kind = OutcomeKind.Wrote, Text = done, Redress = redress, Watchable = trim, Restocked = verify && trim && !placed, Remapped = remapped };
                }
            }
            catch (Exception ex)
            {
                Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                bool covered = remapped && found != null && found.ContainsKey(SettingsMenuNames.ForumTile);
                return new Outcome { Kind = OutcomeKind.Fail, Text = cause.GetType().Name + ": " + cause.Message, Redress = redress, Remapped = remapped, Covered = covered };
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
            private int restocks;
            private int firstWrite;
            private int stable;
            private int stockFrames;
            private int offPanelFrames;
            private bool sameFrame;
            private bool leavePosted;
            private bool redress;
            private bool seen;
            private string done;
            private string firstProblem;
            private string lastProblem;
            private string heldProblem;
            private double? frameStart;
            private SettingsMenuTimings timings;

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
                    // The frame after the focus-in's (this run's or a newer one's): the process lived through our work inside the
                    // natives and the rest of that frame. Its render may still be running on the render thread (Render returns
                    // after starting World.RenderAsync, GameApplication.cs:923, and only the next Render waits for it, :900), so
                    // the breadcrumb goes one frame later (one field read when there is none).
                    if (!leavePosted)
                    {
                        leavePosted = true;
                        SameFrameBreadcrumb.LeaveNextFrame(host, state);
                    }
                    // A newer focus-in owns the grid: no line and no flag from this run.
                    if (state.LayoutRun != id) return;
                    frames++;
                    // Build 23: this frame timed (log text only; nothing below reads a time).
                    frameStart = SettingsMenuTimer.Now(host, state);
                    timings = SettingsMenuTimer.Of(state, id);
                    if (timings != null && frameStart != null) timings.FrameStarted(frameStart.Value);
                    try
                    {
                        Frame();
                    }
                    finally
                    {
                        double? now = timings == null || frameStart == null ? null : SettingsMenuTimer.Now(host, state);
                        if (now != null) timings.FrameCost(frames, now.Value - frameStart.Value);
                    }
                }
                catch (Exception)
                {
                }
            }

            /// <summary>One frame of the run (see <see cref="Schedule"/>).</summary>
            private void Frame()
            {
                if (frames == 1)
                {
                    // The focus-in frame is over (its render came after the same-frame step, if that ran): end the step and
                    // count a layout it made, or the dress before setup() left, as this run's write at frame 0.
                    SameFrame arm = TakeArm(state, id);
                    string before = state.PreDressRun == id ? state.PreDressDone : null;
                    // The focus-in as the first frame finds it (after setup() and the same-frame step), for the device logs.
                    if (timings != null) timings.FocusInFrameDt = FrameDelta(host);
                    string probe = Probe(host, state, id, arm);
                    Write(host, probe == null || timings == null ? probe : probe + timings.FocusInText());
                    // PERFORMANCE switched off after a dress the dress before setup() did not undo (the same-frame switch off,
                    // or no panel then): the FORUM icon back now, outside any native.
                    Write(host, RestoreIcon(host, state));
                    if (arm != null && arm.LaidOut)
                    {
                        sameFrame = true;
                        writes = 1;
                        done = arm.Done;
                    }
                    else if (before != null)
                    {
                        sameFrame = true;
                        writes = 1;
                        done = before;
                    }
                }
                Outcome outcome = Attempt(host, state, true);
                // Whether a run that never laid out sets RedressFailed: not while the remap covers a tile on the panel. Settings
                // gone after a frame found it keeps the reason of that frame (the line below says it too).
                if (!outcome.Gone || !seen) redress = outcome.Redress && !outcome.Covered;
                switch (outcome.Kind)
                {
                    case OutcomeKind.Off:
                        if (frames == 1 && timings != null) Write(host, timings.StockText(id));
                        return;
                    case OutcomeKind.Fail:
                        Fail(host, state, redress, outcome.Text + Frames());
                        return;
                    case OutcomeKind.Wrote:
                        if (writes > 0 && outcome.Restocked) restocks++;
                        if (writes == 0) firstWrite = frames;
                        writes++;
                        // The render before this frame showed a grid that is not ours (stock, or another panel).
                        stockFrames++;
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
                        if (outcome.OffPanel) offPanelFrames++;
                        if (writes == 0) firstWrite = frames;
                        writes = Math.Max(writes, 1);
                        // Trim off: the tile read back (the remap and its icon) is all there is to watch.
                        if (!outcome.Watchable || stable >= StableFrames && frames >= SettleFrames)
                        {
                            Finish();
                            return;
                        }
                        break;
                    default:
                        stable = 0;
                        lastProblem = outcome.Text;
                        if (writes == 0 && firstProblem == null) firstProblem = outcome.Text;
                        // Not laid out yet, or another panel: the render before this frame did not show our grid.
                        if (!outcome.Gone && (writes == 0 || outcome.Changed)) stockFrames++;
                        if (outcome.Gone)
                        {
                            // Laid out, then Settings left the stack (Back to the main menu): nothing left to watch.
                            if (writes > 0)
                            {
                                lastProblem = null;
                                Finish();
                                return;
                            }
                            // Never laid out, and Settings has left since a frame found it: nothing left to wait for. Still a
                            // run that never laid the grid out (RedressFailed): Settings was read, and what failed is its panel.
                            if (seen)
                            {
                                Fail(host, state, redress, heldProblem + "; Settings left after " + frames.ToString(CultureInfo.InvariantCulture) + " frames");
                                return;
                            }
                            break;
                        }
                        seen = true;
                        heldProblem = outcome.Text;
                        // A different panel than the one laid out: its tile may read FORUM again. Not for a width: the
                        // instances, and the tile's PERFORMANCE dress, are the ones written (a tap still opens the page).
                        if (writes > 0 && outcome.Changed && !outcome.Remapped) state.TileActive = false;
                        break;
                }
                if (frames >= MaxFrames)
                {
                    End(null);
                    return;
                }
                if (!host.PostNextFrame(Step)) End("no next frame");
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
                if (sameFrame) line += " in the focus-in frame";
                else if (firstWrite > 1) line += " after " + firstWrite.ToString(CultureInfo.InvariantCulture) + " frames (" + firstProblem + ")";
                if (restocks > 0) line += "; laid out again " + restocks.ToString(CultureInfo.InvariantCulture) + "x after the stock grid came back";
                int redresses = writes - 1 - restocks;
                if (redresses > 0) line += "; tile re-dressed again " + redresses.ToString(CultureInfo.InvariantCulture) + "x after another panel";
                if (offPanelFrames > 0)
                    line += "; MORE GAMES shown off the panel " + offPanelFrames.ToString(CultureInfo.InvariantCulture) + (offPanelFrames == 1 ? " frame" : " frames");
                line += "; stock grid drawn " + stockFrames.ToString(CultureInfo.InvariantCulture) + (stockFrames == 1 ? " frame" : " frames");
                if (lastProblem != null) line += "; then " + lastProblem + Frames();
                if (timings != null && frameStart != null)
                {
                    double? now = SettingsMenuTimer.Now(host, state);
                    if (now != null) line += timings.RunText(now.Value - frameStart.Value, frames);
                }
                Write(host, line);
            }

            /// <summary>The engine's length of the frame running now, ms (the host's read), or null. Never throws.</summary>
            private static double? FrameDelta(ISettingsMenuHost host)
            {
                try
                {
                    return host.FrameDeltaMs();
                }
                catch (Exception)
                {
                    return null;
                }
            }

            private string Frames()
            {
                return frames > 1 ? "; gave up after " + frames.ToString(CultureInfo.InvariantCulture) + " frames" : "";
            }
        }

        // ------------------------------------------------------------------ the focus-in frame (build 21)

        /// <summary>
        /// Looks (a read, then the writes when the grid is not laid out) the same-frame step takes at most per focus-in; one is
        /// what a focus-in needs on either platform. Android's check before a look is not one.
        /// </summary>
        public const int MaxSameFrameLooks = 4;

        /// <summary>
        /// One focus-in's same-frame step (build 21; docs/design/settings-menu.md §3.2): the grid laid out in the frame of the
        /// focus-in, inside the AS call stack of <c>MenuSettings.setup()</c>, after its last stock write and before the frame's Flash
        /// render, so the stock grid is never drawn. <see cref="Schedule"/> arms it (trim on) before <c>setup()</c> runs, and
        /// <see cref="UiNatives"/> then appends an observer on <see cref="Native"/> (it returns null, so the game's answer stands:
        /// the last non-null result wins, MenuComponent.cs:869-924) for the rest of the frame; the run's first frame closes it and
        /// removes the observer (other menus never pay for it). Each call of the native while armed comes to
        /// <see cref="OnNative"/>:
        /// <list type="bullet">
        /// <item>Steam watches <c>GetLanguage</c>: PC-AS MenuSettings.as:367, right after the last tile write (:364 btn_social.x;
        /// then only btn_customer.txt.x for "id" and addButtonListeners). The other GetLanguage of the path, adjustToViewport's
        /// (:633), runs from MenuBase.onFocusIn :330, before EnterSection (:339), so before the arm. Every armed call looks: a
        /// call before setup() would find the grid laid out (a return) or be undone and looked at again at :367.</item>
        /// <item>Android watches <c>GetPlatformId</c>, whose last calls in setup() are addButtonListeners' (AND-AS :214 → :363),
        /// after the last stock writes (:210-213). TextUtilities.setStringID calls it 3 times per label (TextUtilities.as:92, :99),
        /// about 40 times per focus-in, so a look runs only when MORE GAMES is where :210-212 put it (shown, x -177;
        /// <see cref="SameFrame.MoreGamesShown"/>): our writes leave it hidden at x <see cref="SettingsMenuNames.MoreGamesMarkerX"/>,
        /// which setup() never writes, so the gate is false until :212 and true from :363 on, after the FORUM dress (:116-119) and
        /// every other stock write. After the look MORE GAMES is ours again, so later calls (the second :363 call, FreemiumBar's
        /// title) only read it. Build 22: the dress before setup() (<see cref="PreDress"/>) has made those stock writes draw
        /// nothing, so this look only tidies (MORE GAMES hidden again) and catches what PreDress could not do.</item>
        /// </list>
        /// A look is <see cref="Attempt"/> with the read-back, as a run's frame does it; it writes no log line and sets no
        /// <see cref="SettingsMenuState.RedressFailed"/> (the run does both, on its frames), and nothing escapes it (a delegate that
        /// throws is rethrown into Flash). <see cref="Busy"/> keeps our own writes' natives (btn_forum.setup → setStringID →
        /// GetPlatformId) out of it. Game thread only.
        /// </summary>
        internal sealed class SameFrame
        {
            private FlashScope scope;
            private object panel;
            private bool panelRead;

            public SameFrame(int run, bool windows)
            {
                Run = run;
                Windows = windows;
                Native = windows ? SettingsMenuNames.GetLanguage : SettingsMenuNames.GetPlatformId;
            }

            /// <summary>The run (<see cref="SettingsMenuState.LayoutRun"/>) it belongs to.</summary>
            public int Run { get; private set; }

            public bool Windows { get; private set; }

            /// <summary>The native it watches: <c>GetLanguage</c> on Steam, <c>GetPlatformId</c> on Android.</summary>
            public string Native { get; private set; }

            /// <summary>Calls of <see cref="Native"/> while armed.</summary>
            public int Calls;

            /// <summary>Looks taken (at most <see cref="MaxSameFrameLooks"/>).</summary>
            public int Looks;

            /// <summary>Looks that wrote the grid.</summary>
            public int Writes;

            /// <summary>A look is running (our own writes call the native again).</summary>
            public bool Busy;

            /// <summary>A look's read or write threw: no more looks in this frame (the run tries again on its frames).</summary>
            public bool Failed;

            /// <summary>The frame is over (or a newer focus-in came): calls are ignored.</summary>
            public bool Closed;

            /// <summary>The last look laid the grid out, or found it laid out.</summary>
            public bool LaidOut;

            /// <summary>The last look's laid-out line (<see cref="Done"/>), when <see cref="LaidOut"/>.</summary>
            public string Done;

            /// <summary>Why the last look did not lay the grid out.</summary>
            public string Problem;

            /// <summary>The observer is registered (<see cref="UiNatives"/>).</summary>
            public bool Watching;

            /// <summary>Why the observer could not be registered (then only the run lays the grid out).</summary>
            public string Refused;

            /// <summary>Removes the observer (set by <see cref="UiNatives"/>; run once by <see cref="Close"/>).</summary>
            public Action Release;

            /// <summary>
            /// "same frame: laid out (GetPlatformId calls 17, looks 1)", "same frame: not laid out, no btn_help (...)", "same frame:
            /// no look (...)", "same frame: not watched (why)" for the focus-in line.
            /// </summary>
            public string Describe()
            {
                if (Refused != null) return "same frame: not watched (" + Refused + ")";
                if (!Watching) return "same frame: not watched";
                string counts = " (" + Native + " calls " + Calls.ToString(CultureInfo.InvariantCulture) + ", looks " +
                                Looks.ToString(CultureInfo.InvariantCulture) + ")";
                if (LaidOut) return "same frame: laid out" + counts;
                if (Problem != null) return "same frame: not laid out, " + Problem + counts;
                return "same frame: no look" + counts;
            }

            /// <summary>
            /// Android's gate before a look (build 22): the Settings panel is on its main frame and MORE GAMES (<c>btn_more</c>) is
            /// shown at x <see cref="SettingsMenuNames.StockMoreGamesX"/> (±<see cref="MarkerTolerance"/>), as setup() has just left
            /// it (AND-AS :210, :212). False
            /// whenever any of it cannot be read (build 21's gate looked then, and spent its looks before setup()). The panel is looked
            /// up once per arm and kept until <see cref="Close"/>; MORE GAMES is looked up on every call (a gotoAndStop places new
            /// instances). False without a UI.
            /// <para>Build 23, with ABOUT drawn on MORE GAMES' button (<paramref name="about"/>): btn_more is ours and shown already, so
            /// the gate is setup()'s last bottom-row write instead, <c>btn_credits.x = 208</c> (:213) over our marker
            /// <see cref="SettingsMenuNames.AboutMarkerX"/> (±<see cref="MarkerTolerance"/>).</para>
            /// </summary>
            internal bool MoreGamesShown(ISettingsMenuHost host, bool about)
            {
                object ui = host.ClientUi();
                if (ui == null) return false;
                if (!panelRead)
                {
                    panelRead = true;
                    scope = new FlashScope(ui);
                    panel = SettingsPanel(scope);
                }
                if (panel == null) return false;
                using (var flash = new FlashScope(ui))
                {
                    if (Frame(flash, panel) != SettingsMenuNames.AndroidMainFrame) return false;
                    if (about)
                    {
                        object credits = flash.Child(panel, SettingsMenuNames.AboutButton);
                        double? at = credits == null ? null : flash.Number(credits, "x");
                        return at != null && Math.Abs(at.Value - SettingsMenuNames.AndroidAboutX) <= MarkerTolerance;
                    }
                    object more = flash.Child(panel, SettingsMenuNames.MoreGamesButton);
                    if (more == null) return false;
                    object visible = flash.Get(more, "visible");
                    if (!(visible is bool) || !(bool)visible) return false;
                    // Half a twip: setup()'s -177 (AND-AS :212), not the timeline's -176.9 (and_settings_frames.txt), which a
                    // panel no step has written yet shows from setup()'s gotoAndStop on.
                    double? x = flash.Number(more, "x");
                    return x != null && Math.Abs(x.Value - SettingsMenuNames.StockMoreGamesX) <= MarkerTolerance;
                }
            }

            /// <summary>Ignores every later call, disposes the kept panel and removes the observer. Never throws.</summary>
            public void Close()
            {
                if (Closed) return;
                Closed = true;
                try
                {
                    if (scope != null) scope.Dispose();
                }
                catch (Exception)
                {
                }
                scope = null;
                panel = null;
                Action release = Release;
                Release = null;
                try
                {
                    if (release != null) release();
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// The watched native was called (<see cref="UiNatives"/>' observer; game thread, inside the game's native dispatch and so
        /// inside the AS call stack of the focus-in): while this run's step is armed, one look (see <see cref="SameFrame"/>).
        /// Never throws and never logs.
        /// </summary>
        public static void OnNative(ISettingsMenuHost host, SettingsMenuState state, string native)
        {
            SameFrame arm = state == null ? null : state.SameFrame;
            if (host == null || arm == null || arm.Closed || arm.Busy || native != arm.Native) return;
            arm.Busy = true;
            double? started = null;
            try
            {
                if (state.LayoutRun != arm.Run) return;
                arm.Calls++;
                // Build 23: the gate and the looks timed (log text only).
                started = SettingsMenuTimer.Now(host, state);
                if (arm.Looks >= MaxSameFrameLooks || arm.Failed) return;
                // Our first Flash work inside a native in this process: the breadcrumb first (once; Android's PreDress wrote it).
                SameFrameBreadcrumb.Enter(host, state, arm.Run, arm.Native);
                if (!arm.Windows)
                {
                    if (!arm.MoreGamesShown(host, state.AboutOnMoreGames)) return;
                    // setup() ran since the last look, its FORUM dress (AND-AS MenuSettings.as:116-119) included: without the remap
                    // the tile reads FORUM until a look has re-dressed it.
                    if (!state.ForumRemapped) state.TileActive = false;
                }
                arm.Looks++;
                Outcome outcome = Attempt(host, state, true);
                switch (outcome.Kind)
                {
                    case OutcomeKind.Wrote:
                    case OutcomeKind.InPlace:
                        if (outcome.Kind == OutcomeKind.Wrote) arm.Writes++;
                        arm.LaidOut = true;
                        arm.Done = outcome.Text;
                        arm.Problem = null;
                        break;
                    case OutcomeKind.Off:
                        arm.LaidOut = false;
                        break;
                    default:
                        arm.LaidOut = false;
                        arm.Problem = outcome.Text;
                        if (outcome.Kind == OutcomeKind.Fail) arm.Failed = true;
                        break;
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                if (started != null)
                {
                    SettingsMenuTimings timings = SettingsMenuTimer.Of(state, arm.Run);
                    double? now = SettingsMenuTimer.Now(host, state);
                    if (timings != null && now != null)
                    {
                        timings.SameFrame += now.Value - started.Value;
                        timings.SameFrameCalls++;
                    }
                }
                arm.Busy = false;
            }
        }

        /// <summary>Closes the armed same-frame step, if any (a newer focus-in, another section, no run). Never throws.</summary>
        public static void Disarm(SettingsMenuState state)
        {
            try
            {
                if (state == null) return;
                SameFrame arm = state.SameFrame;
                state.SameFrame = null;
                if (arm != null) arm.Close();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>The run's first frame: closes this run's same-frame step and returns it (null when it has none).</summary>
        private static SameFrame TakeArm(SettingsMenuState state, int id)
        {
            SameFrame arm = state.SameFrame;
            if (arm == null || arm.Run != id) return null;
            state.SameFrame = null;
            arm.Close();
            return arm;
        }

        /// <summary><c>menu_settings.mc_panel</c> (Settings on top of MenusStack, else by name in it), or null. Reads only.</summary>
        internal static object SettingsPanel(FlashScope flash)
        {
            object stack = flash.Class("gluic.components.menus", "MenusStack");
            if (stack == null) return null;
            object top = flash.Call(stack, "getMenuOnTopOfStack");
            object menu = top != null && NameOf(flash, top) == SettingsMenuNames.SettingsMenu ? top : null;
            if (menu == null)
            {
                double? index = FlashInterop.Number(flash.Call(stack, "getMenuIndexInStack", SettingsMenuNames.SettingsMenu));
                if (index.HasValue && index.Value >= 0) menu = flash.Call(stack, "getMenuByName", SettingsMenuNames.SettingsMenu);
            }
            return menu == null ? null : flash.Child(menu, SettingsMenuNames.Panel);
        }

        // ------------------------------------------------------------------ build 22: before setup() (Android)

        /// <summary>
        /// Our Flash work inside the game's natives is allowed now: <c>admin.settingsMenu.sameFrame</c> on and no breadcrumb of the
        /// last start blocking it (<see cref="SameFrameBreadcrumb"/>).
        /// </summary>
        internal static bool InNativeAllowed(ISettingsMenuHost host, SettingsMenuState state)
        {
            return host.SettingsMenuSameFrame && !state.SameFrameBlocked;
        }

        /// <summary>
        /// <c>EnterSection("settings_main_screen")</c>, Android, first of the grid's steps: the FORUM remap (<see cref="ForumRemap"/>)
        /// in while <c>admin.settingsMenu.performance</c> is on, out while it is off. Pure C# (no Flash), so not behind the
        /// same-frame switch. While it is on <see cref="SettingsMenuState.TileActive"/> is set at once: setup()'s own dress will write
        /// PERFORMANCE, so a tap opens the page. One line per change: "settings menu: FORUM remap installed", "... removed", "... not
        /// installed (why)". Never throws.
        /// </summary>
        public static void EnsureRemap(ISettingsMenuHost host, SettingsMenuState state)
        {
            try
            {
                if (host == null || state == null || host.IsWindows) return;
                bool on = host.SettingsMenuPerformance;
                object ui = host.ClientUi();
                if (ui == null) return;
                string problem = ForumRemap.Ensure(ui, on);
                state.ForumRemapped = on && problem == null;
                // setup()'s own dress now writes PERFORMANCE: the tile is the page's from this focus-in on (a tap, the R4 gate).
                if (state.ForumRemapped) state.TileActive = true;
                string report = problem != null ? (on ? "not installed (" : "not removed (") + problem + ")" : on ? "installed" : "removed";
                string before = state.RemapReported;
                if (report == before) return;
                state.RemapReported = report;
                // Off from the start: nothing was ever installed, so nothing to say.
                if (before == null && problem == null && !on) return;
                Write(host, "settings menu: FORUM remap " + report);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Build 22 (owner, build 21 on the S22: "it flashes the more games and the forum button after, the trim remains"):
        /// <c>EnterSection("settings_main_screen")</c>, Android, synchronously inside EnterSection's native, which MenuBase.onFocusIn
        /// calls before <c>MenuSettings.setup()</c> in the same AS stack (MenuBase.as:339, AND-AS MenuSettings.as:468-473). It
        /// leaves the panel so that every visible effect of setup() draws nothing new, whatever the timing of the steps after it:
        /// <list type="bullet">
        /// <item>FORUM's title: the remap (<see cref="EnsureRemap"/>) makes setup()'s own dress write PERFORMANCE.</item>
        /// <item>FORUM's icon: dressed once per instance, then out of setup()'s reach (<see cref="SlotOut"/>).</item>
        /// <item>MORE GAMES (and SOCIAL MEDIA and CUSTOMER CARE, alike): off the panel (y, alpha, mouse, which setup() never
        /// writes), so its <c>btn_more.visible = true</c> and the bottom row's x (:210-212) draw nothing, and the hidden MORE GAMES
        /// cannot take ABOUT's taps (it is above ABOUT, depth 99 over 69).</item>
        /// <item>ABOUT at <see cref="SettingsMenuNames.AndroidAboutX"/>, where :213 puts it anyway. Build 23, with
        /// <c>admin.settingsMenu.aboutCentre</c> on and our GoToFreeGames in place: ABOUT is drawn on MORE GAMES' button instead
        /// (kept at setup()'s -177, titled ABOUT by <see cref="MoreGamesRemap"/>, put in only after the dress read back), and the
        /// real ABOUT is off the panel like the removed tiles, its x at the marker <see cref="SettingsMenuNames.AboutMarkerX"/>. Any
        /// failure takes the remap out and dresses build 22's bottom row in the same call; every EnterSection that does not draw it
        /// takes the remap out.</item>
        /// <item>The kept tiles in their slots: setup() never moves them on Android.</item>
        /// </list>
        /// On a return the instances are the ones written before (setup()'s gotoAndStop(3) on frame 3 places none), so this only
        /// reads them back ("kept"). On the first entry the panel is still on frame 1: it goes to frame 3 first (setup()'s own goto is
        /// then a no-op) and the new instances are dressed ("dressed now"). The read is the run's (any frame but 3, a missing
        /// instance or a width: nothing written, and the same-frame step and the run lay the grid out as in build 21).
        /// <para>Only for the run <see cref="Schedule"/> allowed (<see cref="SettingsMenuState.PreDressRun"/>: the same-frame switch
        /// on, the run's first frame posted): this is Flash work inside a native (setters, gotoAndStop, btn_forum.setup), so the
        /// breadcrumb is written before its first read. What it did goes to the run's focus-in line ("before setup: ..."); it writes
        /// no line itself (but the MORE GAMES remap's and the PERFORMANCE title's own lines) and never throws.</para>
        /// <para>Build 23 (F1): after its last write it proves from reads that a look after setup() would change nothing
        /// (<see cref="SealProblem"/>), and only then sets <see cref="SettingsMenuState.PreDressSealed"/>; else the reason goes to the
        /// focus-in line ("not skipped: ...") and the look runs as in build 22.</para>
        /// </summary>
        public static void PreDress(ISettingsMenuHost host, SettingsMenuState state)
        {
            if (host == null || state == null) return;
            int id = state.LayoutRun;
            string text = null;
            // Build 23: the MORE GAMES remap stays in only when this call drew ABOUT on MORE GAMES' button and read it back.
            bool aboutDrawn = false;
            object ui = null;
            try
            {
                if (host.IsWindows || state.PreDressRun != id) return;
                bool trim = host.SettingsMenuTrim;
                bool redress = host.SettingsMenuPerformance;
                // PERFORMANCE switched off after a dress: the icon goes back within setup()'s reach (its FORUM dress then shows).
                bool restore = !redress && state.TileEverActive;
                if (!trim && !redress && !restore) return;
                ui = host.ClientUi();
                if (ui == null)
                {
                    text = "not dressed (no UI)";
                    return;
                }
                SameFrameBreadcrumb.Enter(host, state, id, SettingsMenuNames.EnterSection);
                using (var flash = new FlashScope(ui))
                {
                    object panel = SettingsPanel(flash);
                    if (panel == null)
                    {
                        text = "not dressed (no " + SettingsMenuNames.SettingsMenu + " panel)";
                        return;
                    }
                    int frame = Frame(flash, panel);
                    string moved = "";
                    if (frame == 1)
                    {
                        // The first entry: setup()'s own gotoAndStop(3) (AND-AS :44), now, so its instances can be dressed first.
                        flash.Call(panel, "gotoAndStop", (float)SettingsMenuNames.AndroidMainFrame);
                        frame = Frame(flash, panel);
                        moved = "panel frame 1 to " + frame.ToString(CultureInfo.InvariantCulture) + ", ";
                    }
                    // Build 23: ABOUT on MORE GAMES' button only while MORE GAMES' tap is ours (UiNatives checked it just now).
                    bool about = trim && host.SettingsMenuAboutCentre;
                    string aboutWhy = null;
                    if (about && !state.FreeGamesReplaced)
                    {
                        about = false;
                        aboutWhy = "no " + SettingsMenuNames.GoToFreeGames;
                    }
                    LayoutPlan plan = Plan(false, redress, about);
                    if (frame != plan.Frame)
                    {
                        text = "not dressed (" + moved + "frame " + frame.ToString(CultureInfo.InvariantCulture) + ")";
                        return;
                    }
                    if (restore)
                    {
                        object tile = flash.Child(panel, SettingsMenuNames.ForumTile);
                        object dressed = tile == null ? null : DressedIcon(flash, tile);
                        if (dressed != null) PutIconBack(flash, tile, dressed);
                        if (!trim)
                        {
                            if (dressed != null) text = "FORUM icon put back";
                            return;
                        }
                    }
                    var found = new Dictionary<string, object>(StringComparer.Ordinal);
                    int read;
                    bool gone;
                    bool changed;
                    string problem = Read(flash, plan, trim, redress, found, out read, out gone, out changed);
                    if (problem != null)
                    {
                        text = "not dressed (" + moved + problem + ")";
                        return;
                    }
                    string done = Done(plan, frame, trim);
                    object forum = redress ? found[SettingsMenuNames.ForumTile] : null;
                    bool placed = !trim || InPlace(flash, plan, found, true, true, new List<string>());
                    var title = new TitleReport();
                    string icon;
                    bool kept;
                    if (placed && (!redress || TileReads(flash, state, forum)))
                    {
                        kept = true;
                        if (redress) state.TileActive = true;
                        icon = redress ? (flash.Get(forum, SettingsMenuNames.IconSlot) == null ? "icon slot null" : "icon slot kept (still reachable)") : null;
                        // Build 23: a title centred earlier in this menu (a fallback's setup) put back; never part of the read-back.
                        if (redress) TitleRepair(flash, state, forum, Found(found, SettingsMenuNames.HelpTile), null, true, title);
                    }
                    else
                    {
                        kept = false;
                        if (!state.ForumRemapped) state.TileActive = false;
                        try
                        {
                            icon = WriteGrid(flash, state, plan, found, trim, redress, true, title);
                        }
                        catch (Exception ex)
                        {
                            // Build 23: a write of ABOUT's dress refused: build 22's dress below instead. Build 22's own rethrows.
                            if (!about) throw;
                            aboutWhy = "dress refused: " + Reason(ex);
                            icon = null;
                        }
                    }
                    if (about)
                    {
                        // Build 23: read back, then the remap; any failure takes the remap out and dresses build 22's bottom row.
                        if (aboutWhy == null && !InPlace(flash, plan, found, true, true, new List<string>())) aboutWhy = "read-back " + Misplaced(flash, plan, found);
                        if (aboutWhy == null)
                        {
                            string refused = MoreGamesRemap.Ensure(ui, true);
                            if (refused != null) aboutWhy = "remap refused: " + refused;
                            ReportMoreGames(host, state, refused == null ? "installed" : "not installed (" + refused + ")");
                        }
                        if (aboutWhy != null)
                        {
                            about = false;
                            MoreGamesRemap.Ensure(ui, false);
                            plan = Plan(false, redress, false);
                            kept = false;
                            icon = WriteGrid(flash, state, plan, found, trim, redress, true, title);
                        }
                        else
                        {
                            aboutDrawn = true;
                            state.AboutOnMoreGames = true;
                        }
                    }
                    string details = Details(state, trim, redress, icon, about, aboutWhy, title.Text);
                    text = kept ? "kept (" + details + ")" : (moved.Length > 0 ? "dressed now (" + moved : "laid out (") + details + ")";
                    ReportTitle(host, state, title);
                    if (redress && !state.ForumRemapped)
                    {
                        // Without the remap setup()'s FORUM dress comes after this one: the look and the run re-dress after it.
                        state.TileActive = false;
                    }
                    else
                    {
                        // setup()'s writes now draw nothing: the run counts this as its write at frame 0.
                        state.PreDressDone = done;
                    }
                    // Build 23 (F1): the seal, proven from reads after the last write; never inferred from PreDressDone alone.
                    // ABOUT wanted on MORE GAMES' button but not drawn there: build 22's look stays (the safe side of a fallback).
                    string unsealed = trim && host.SettingsMenuAboutCentre && !about
                        ? "ABOUT " + FlashInterop.Text(SettingsMenuNames.AndroidAboutX) + " (" + (aboutWhy ?? "not dressed") + ")"
                        : SealProblem(flash, state, plan, found, trim, redress, forum, about, ui);
                    if (unsealed == null) state.PreDressSealed = id;
                    else state.SealProblem = unsealed;
                }
            }
            catch (Exception ex)
            {
                text = "failed (" + Reason(ex) + ")";
            }
            finally
            {
                if (!aboutDrawn) MoreGamesOut(host, state, ui);
                if (text != null && state.PreDressRun == id) state.PreDressText = text;
            }
        }

        /// <summary>A found instance, or null.</summary>
        private static object Found(Dictionary<string, object> found, string name)
        {
            object item;
            return found.TryGetValue(name, out item) ? item : null;
        }

        /// <summary>
        /// Build 23 (F1): why this dress leaves the same-frame look something to do, or null when it does not: the FORUM remap on and
        /// the icon out of setup()'s reach (or no re-dress), every kept and removed instance read back as the strict plan wants, and
        /// with ABOUT on MORE GAMES its remap in. setup()'s other writes are then the plan's own values or properties it never writes
        /// (title, btn_more.visible/x, btn_credits.x), so no look after setup() could change what is drawn. Reads only; never throws.
        /// </summary>
        private static string SealProblem(FlashScope flash, SettingsMenuState state, LayoutPlan plan, Dictionary<string, object> found, bool trim,
                                          bool redress, object forum, bool about, object ui)
        {
            try
            {
                if (!trim) return "trim off";
                if (redress && !state.ForumRemapped) return "FORUM remap off";
                if (redress && DressedIcon(flash, forum) == null) return "icon within setup()'s reach";
                if (!InPlace(flash, plan, found, true, true, new List<string>())) return "read-back " + Misplaced(flash, plan, found);
                if (about && !MoreGamesRemap.IsOn(ui)) return "MORE GAMES remap off";
                return null;
            }
            catch (Exception ex)
            {
                return Reason(ex);
            }
        }

        /// <summary>The first instance the strict read-back finds out of place ("btn_credits.x 208", "btn_more.y"), for a log line. Reads only.</summary>
        private static string Misplaced(FlashScope flash, LayoutPlan plan, Dictionary<string, object> found)
        {
            foreach (Placement p in plan.Kept)
            {
                double? x = flash.Number(found[p.Name], "x");
                double? y = flash.Number(found[p.Name], "y");
                if (x == null || Math.Abs(x.Value - (float)p.X) > PlaceTolerance) return p.Name + ".x " + (x == null ? "unreadable" : FlashInterop.Text(x.Value));
                if (y == null || Math.Abs(y.Value - (float)p.Y) > PlaceTolerance) return p.Name + ".y " + (y == null ? "unreadable" : FlashInterop.Text(y.Value));
                if (ReadsRemoved(flash, found[p.Name])) return p.Name + " reads removed";
            }
            foreach (string hidden in plan.Hidden)
            {
                var one = new LayoutPlan(plan.Frame, new Placement[0], new[] { hidden }, false);
                if (!InPlace(flash, one, found, true, true, new List<string>())) return hidden + " not off the panel";
            }
            return "unknown";
        }

        /// <summary>
        /// Build 23: the MORE GAMES remap out (every EnterSection whose dress did not draw ABOUT on MORE GAMES' button, so setup()
        /// titles btn_more as the game does), and its state line once per change: "settings menu: MORE GAMES remap removed", "...
        /// installed", "... not removed (why)". Android only. Never throws.
        /// </summary>
        private static void MoreGamesOut(ISettingsMenuHost host, SettingsMenuState state, object ui)
        {
            try
            {
                state.AboutOnMoreGames = false;
                if (host.IsWindows) return;
                if (ui == null) ui = host.ClientUi();
                if (ui == null) return;
                string problem = MoreGamesRemap.Ensure(ui, false);
                ReportMoreGames(host, state, problem == null ? "removed" : "not removed (" + problem + ")");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>One "settings menu: MORE GAMES remap ..." line per change of state (no removal line while it was never installed).</summary>
        private static void ReportMoreGames(ISettingsMenuHost host, SettingsMenuState state, string report)
        {
            string before = state.MoreGamesReported;
            if (report == before) return;
            // A removal is news only after an install (or a removal that failed): otherwise nothing was ever in.
            bool removal = report == "removed" || report.StartsWith("not removed", StringComparison.Ordinal);
            if (removal && before != "installed" && (before == null || !before.StartsWith("not removed", StringComparison.Ordinal))) return;
            state.MoreGamesReported = report;
            Write(host, "settings menu: MORE GAMES remap " + report);
        }

        /// <summary>
        /// Android, <c>admin.settingsMenu.performance</c> off after a dress in this process: when btn_forum's icon is still out of
        /// setup()'s reach (<see cref="DressedIcon"/>), the slot and name back and the stock FORUM dress (<see cref="Undress"/>), so the
        /// tile reads and shows FORUM again. The run's first frame, outside any native and so not behind the same-frame switch;
        /// <see cref="PreDress"/> does the same before setup() when it may, and then this finds nothing. "settings menu: FORUM icon put
        /// back after setup()" when it did, else null. Never throws.
        /// </summary>
        private static string RestoreIcon(ISettingsMenuHost host, SettingsMenuState state)
        {
            try
            {
                if (host.IsWindows || host.SettingsMenuPerformance || !state.TileEverActive) return null;
                object ui = host.ClientUi();
                if (ui == null) return null;
                using (var flash = new FlashScope(ui))
                {
                    object panel = SettingsPanel(flash);
                    if (panel == null || Frame(flash, panel) != SettingsMenuNames.AndroidMainFrame) return null;
                    object tile = flash.Child(panel, SettingsMenuNames.ForumTile);
                    if (tile == null || DressedIcon(flash, tile) == null) return null;
                    Undress(flash, tile, true);
                    return "settings menu: FORUM icon put back after setup()";
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// "FORUM remap on, icon slot null, MORE GAMES off the panel, ABOUT 208" (the parts that apply). Build 23: "ABOUT on MORE GAMES
        /// (-177), ABOUT tile off the panel" while ABOUT is drawn on MORE GAMES' button; "ABOUT 208 (why)" when the switch is on and
        /// that could not be (with it off the text is build 22's); then what was done to the PERFORMANCE title, if anything.
        /// </summary>
        private static string Details(SettingsMenuState state, bool trim, bool redress, string icon, bool about, string aboutWhy, string title)
        {
            var parts = new List<string>();
            if (redress) parts.Add("FORUM remap " + (state.ForumRemapped ? "on" : "off"));
            if (icon != null) parts.Add(icon);
            if (trim && about)
            {
                parts.Add("ABOUT on MORE GAMES (" + FlashInterop.Text(SettingsMenuNames.StockMoreGamesX) + ")");
                parts.Add("ABOUT tile off the panel");
            }
            else if (trim)
            {
                parts.Add("MORE GAMES off the panel");
                parts.Add("ABOUT " + FlashInterop.Text(SettingsMenuNames.AndroidAboutX) + (aboutWhy == null ? "" : " (" + aboutWhy + ")"));
            }
            if (title != null) parts.Add(title);
            return string.Join(", ", parts.ToArray());
        }

        // ------------------------------------------------------------------ the read and the writes

        /// <summary>
        /// Null when every instance the step needs is found (<paramref name="found"/>, <paramref name="frame"/>), else why not;
        /// <paramref name="gone"/> when menu_settings is not in the stack at all, <paramref name="changed"/> when its panel is not the
        /// plan's (no mc_panel, another frame, an instance missing). Reads only.
        /// </summary>
        private static string Read(FlashScope flash, LayoutPlan plan, bool trim, bool redress, Dictionary<string, object> found,
                                   out int frame, out bool gone, out bool changed)
        {
            frame = 0;
            gone = false;
            changed = true;
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
                    string wrong = width == null ? p.Name + " has no width"
                        : Math.Abs(width.Value - SettingsMenuNames.TileWidth) > SettingsMenuNames.TileWidthTolerance
                            ? p.Name + " is " + FlashInterop.Text(width.Value) + " wide" : null;
                    if (wrong != null)
                    {
                        changed = false;
                        return wrong;
                    }
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
            changed = false;
            return null;
        }

        /// <summary>
        /// The diagnostic line of a focus-in (its run's first frame, after <c>setup()</c> and the same-frame step): what is on top
        /// of <c>MenusStack</c>, where <c>menu_settings</c> is in it, the panel's frame and whether <c>btn_social</c> is visible, with
        /// the layouts so far, then what the same-frame step did (<see cref="SameFrame.Describe"/>). Nothing in either AS shows
        /// btn_social again (AND-AS MenuSettings.as:42-215, PC-AS :42-345 only move it or leave it), so "btn_social shown" after a
        /// layout means new instances (Steam's frame 3 and back, or a re-created menu) that no step has written yet, "hidden" that
        /// a step wrote these instances (the same-frame step writes before this line). Null with both switches off. Reads only;
        /// never throws.
        /// </summary>
        private static string Probe(ISettingsMenuHost host, SettingsMenuState state, int id, SameFrame arm)
        {
            string head = "settings menu: focus-in " + id.ToString(CultureInfo.InvariantCulture) + " (layouts so far: " +
                          state.Layouts.ToString(CultureInfo.InvariantCulture) + "): ";
            string tail = "";
            try
            {
                if (!host.SettingsMenuTrim && !(host.SettingsMenuPerformance && !host.IsWindows)) return null;
                tail = "; " + (arm != null ? arm.Describe() + NotSkipped(state, id)
                    : id != 0 && state.PreDressSealed == id ? "same frame: not needed (dressed before setup)"
                    : !host.SettingsMenuTrim ? "same frame: off (trim off)"
                    : state.SameFrameBlocked ? "same frame: off (a breadcrumb of the last start is still there)"
                    : !host.SettingsMenuSameFrame ? "same frame: off (" + McvsOffline.Server.Services.Admin.SettingsSchema.SettingsMenuSameFrameKey + " off)"
                    : "same frame: not armed");
                string crumb = state.TakeBreadcrumbProblem();
                if (crumb != null) tail += "; " + crumb;
                if (state.PreDressRun == id && state.PreDressText != null) tail = "; before setup: " + state.PreDressText + tail;
                object ui = host.ClientUi();
                if (ui == null) return head + "no UI" + tail;
                using (var flash = new FlashScope(ui))
                {
                    object stack = flash.Class("gluic.components.menus", "MenusStack");
                    if (stack == null) return head + "no MenusStack" + tail;
                    object top = flash.Call(stack, "getMenuOnTopOfStack");
                    string topName = top == null ? null : NameOf(flash, top);
                    var parts = new List<string> { "on top " + (top == null ? "nothing" : topName ?? "unnamed") };
                    double? index = FlashInterop.Number(flash.Call(stack, "getMenuIndexInStack", SettingsMenuNames.SettingsMenu));
                    object menu = topName == SettingsMenuNames.SettingsMenu ? top : null;
                    if (index.HasValue && index.Value >= 0)
                    {
                        parts.Add(SettingsMenuNames.SettingsMenu + " at " + FlashInterop.Text(index.Value) + " in the stack");
                        if (menu == null) menu = flash.Call(stack, "getMenuByName", SettingsMenuNames.SettingsMenu);
                    }
                    else if (menu == null)
                    {
                        parts.Add("no " + SettingsMenuNames.SettingsMenu + " in the stack");
                    }
                    object panel = menu == null ? null : flash.Child(menu, SettingsMenuNames.Panel);
                    if (menu != null && panel == null) parts.Add("no " + SettingsMenuNames.Panel);
                    if (panel != null)
                    {
                        parts.Add("panel frame " + Frame(flash, panel).ToString(CultureInfo.InvariantCulture));
                        object social = flash.Child(panel, SettingsMenuNames.SocialTile);
                        object visible = social == null ? null : flash.Get(social, "visible");
                        parts.Add(SettingsMenuNames.SocialTile + (social == null ? " missing" : !(visible is bool) ? " unreadable" : (bool)visible ? " shown" : " hidden"));
                    }
                    return head + string.Join(", ", parts) + tail;
                }
            }
            catch (Exception ex)
            {
                Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                return head + cause.GetType().Name + ": " + cause.Message + tail;
            }
        }

        /// <summary>Build 23 (F1): " (not skipped: why)" when this run's dress before setup() ran and did not seal the grid, else "".</summary>
        private static string NotSkipped(SettingsMenuState state, int id)
        {
            string why = state.PreDressRun == id ? state.SealProblem : null;
            return why == null ? "" : " (not skipped: " + why + ")";
        }

        /// <summary>The menu's name: <c>ASCharacter.Name</c>, else the AS <c>name</c>.</summary>
        internal static string NameOf(FlashScope flash, object menu)
        {
            string name = GameReflection.Property(menu, "Name") as string;
            if (name == SettingsMenuNames.SettingsMenu) return name;
            string asName = flash.Get(menu, "name") as string;
            return asName ?? name;
        }

        /// <summary>
        /// Every kept instance in its slot (±<see cref="PlaceTolerance"/>) and every removed one invisible. On Android (build 22) a
        /// removed instance also counts as invisible while it is off the panel (alpha 0, y at <see cref="SettingsMenuNames.OffPanelY"/>):
        /// setup() shows MORE GAMES again on every focus-in (AND-AS :210), drawn as nothing; those go to <paramref name="offPanel"/>.
        /// <paramref name="strict"/> (the dress before setup()): every removed one hidden and off the panel, MORE GAMES at its
        /// marker x. On Android a kept instance that still reads removed (<see cref="ReadsRemoved"/>: btn_forum hidden while
        /// PERFORMANCE was off, kept again now that it is on) is not in place either. Reads only.
        /// </summary>
        private static bool InPlace(FlashScope flash, LayoutPlan plan, Dictionary<string, object> found, bool android, bool strict,
                                    List<string> offPanel)
        {
            foreach (Placement p in plan.Kept)
            {
                double? x = flash.Number(found[p.Name], "x");
                double? y = flash.Number(found[p.Name], "y");
                if (x == null || y == null || Math.Abs(x.Value - (float)p.X) > PlaceTolerance || Math.Abs(y.Value - (float)p.Y) > PlaceTolerance)
                    return false;
                if (android && ReadsRemoved(flash, found[p.Name])) return false;
            }
            foreach (string hidden in plan.Hidden)
            {
                object visible = flash.Get(found[hidden], "visible");
                bool shown = !(visible is bool) || (bool)visible;
                if (!android)
                {
                    if (shown) return false;
                    continue;
                }
                double? alpha = flash.Number(found[hidden], "alpha");
                double? y = flash.Number(found[hidden], "y");
                bool off = alpha != null && alpha.Value <= 0.001 && y != null && y.Value >= SettingsMenuNames.OffPanelY - PlaceTolerance;
                if (strict)
                {
                    if (shown || !off) return false;
                    double? marker = MarkerX(hidden);
                    if (marker == null) continue;
                    double? x = flash.Number(found[hidden], "x");
                    if (x == null || Math.Abs(x.Value - marker.Value) > PlaceTolerance) return false;
                    continue;
                }
                if (shown && !off) return false;
                if (shown) offPanel.Add(hidden);
            }
            return true;
        }

        /// <summary>
        /// The x our writes leave on a removed bottom-row button (a value setup() never writes, so the same-frame gate sees setup()'s
        /// own write come back): MORE GAMES in build 22's grid, ABOUT in build 23's; null for any other instance.
        /// </summary>
        private static double? MarkerX(string hidden)
        {
            if (hidden == SettingsMenuNames.MoreGamesButton) return SettingsMenuNames.MoreGamesMarkerX;
            if (hidden == SettingsMenuNames.AboutButton) return SettingsMenuNames.AboutMarkerX;
            return null;
        }

        /// <summary>
        /// Android: the instance reads as one of our removed ones: <c>visible</c> false, <c>alpha</c> 0 or <c>mouseEnabled</c> false
        /// (Android's setup() writes none of them on a tile or ABOUT: AND-AS MenuSettings.as:42-215 has only btn_more.visible, :210).
        /// A value that cannot be read does not count, so an unreadable property never makes every frame a write. Reads only.
        /// </summary>
        private static bool ReadsRemoved(FlashScope flash, object item)
        {
            object visible = flash.Get(item, "visible");
            if (visible is bool && !(bool)visible) return true;
            object mouse = flash.Get(item, "mouseEnabled");
            if (mouse is bool && !(bool)mouse) return true;
            double? alpha = flash.Number(item, "alpha");
            return alpha != null && alpha.Value <= 0.001;
        }

        /// <summary>btn_forum is on Settings' panel (<see cref="SettingsPanel"/>). Reads only.</summary>
        private static bool TileOnPanel(FlashScope flash)
        {
            object panel = SettingsPanel(flash);
            return panel != null && flash.Child(panel, SettingsMenuNames.ForumTile) != null;
        }

        /// <summary>The off-panel instances setup() showed again (<see cref="InPlace"/>): hidden again, MORE GAMES at its marker x.</summary>
        private static void Tidy(FlashScope flash, Dictionary<string, object> found, List<string> offPanel)
        {
            foreach (string name in offPanel)
            {
                flash.Set(found[name], "visible", false);
                flash.Set(found[name], "mouseEnabled", false);
                double? marker = MarkerX(name);
                if (marker != null) flash.Set(found[name], "x", (float)marker.Value);
            }
        }

        /// <summary>
        /// Android: btn_forum reads PERFORMANCE now: its text (the FORUM remap, or a re-dress of ours with no setup() since,
        /// <see cref="SettingsMenuState.TileActive"/>) and its icon (<see cref="IconDressed"/>). Reads only.
        /// </summary>
        private static bool TileReads(FlashScope flash, SettingsMenuState state, object forum)
        {
            return (state.TileActive || state.ForumRemapped) && IconDressed(flash, forum);
        }

        /// <summary>
        /// The icon is PERFORMANCE's: BtnBase's slot reads null (the icon is out of setup()'s reach, so the one we dressed stays;
        /// <see cref="SlotOut"/>), or the icon it reaches shows <see cref="SettingsMenuNames.PerformanceIcon"/> (ImageContainer's
        /// public <c>img</c> getter, gluic/elements/ImageContainer.as:202-205). Reads only.
        /// </summary>
        private static bool IconDressed(FlashScope flash, object forum)
        {
            object icon = flash.Get(forum, SettingsMenuNames.IconSlot);
            if (icon == null) return true;
            return flash.Get(icon, "img") as string == SettingsMenuNames.PerformanceIcon;
        }

        /// <summary>
        /// Build 22: puts btn_forum's icon, PERFORMANCE's now, out of the reach of BtnBase.setup (BtnBase.as:130:
        /// <c>if (Boolean(this.mc_image) &amp;&amp; Boolean(param1.icon))</c>), so setup()'s FORUM dress (AND-AS :116-119) leaves it: the
        /// slot <c>mc_image</c> set to null, and the container renamed (<see cref="SettingsMenuNames.PerformanceIconName"/>), which
        /// covers both ways gameswf may resolve <c>this.mc_image</c> (the declared var, or a child by name). Read back: the slot reads
        /// null. When it still reaches the icon both are put back (the look and the run then re-dress the icon after setup(), as in
        /// build 21). "icon slot null", or "icon slot kept (why)". Never throws.
        /// </summary>
        private static string SlotOut(FlashScope flash, object forum)
        {
            object icon = null;
            string why = "still reachable";
            try
            {
                icon = flash.Get(forum, SettingsMenuNames.IconSlot);
                if (icon == null) return "icon slot null";
                flash.Set(forum, SettingsMenuNames.IconSlot, null);
                try
                {
                    flash.Set(icon, "name", SettingsMenuNames.PerformanceIconName);
                }
                catch (Exception ex)
                {
                    why = "rename refused, " + Reason(ex);
                }
                if (flash.Get(forum, SettingsMenuNames.IconSlot) == null) return "icon slot null";
            }
            catch (Exception ex)
            {
                why = Reason(ex);
            }
            if (icon != null) PutIconBack(flash, forum, icon);
            return "icon slot kept (" + why + ")";
        }

        /// <summary>
        /// The icon container <see cref="SlotOut"/> put out of setup()'s reach, or null when the slot still reaches an icon (the stock
        /// tile, or one put back): the slot reads null and the container is the child renamed
        /// <see cref="SettingsMenuNames.PerformanceIconName"/>, or still named <c>mc_image</c> when gameswf refused the rename (SlotOut
        /// counts the null slot alone). Reads only.
        /// </summary>
        private static object DressedIcon(FlashScope flash, object forum)
        {
            if (flash.Get(forum, SettingsMenuNames.IconSlot) != null) return null;
            return flash.Child(forum, SettingsMenuNames.PerformanceIconName) ?? flash.Child(forum, SettingsMenuNames.IconSlot);
        }

        /// <summary>Undoes <see cref="SlotOut"/> on <paramref name="icon"/> (the container): its name and the slot. Never throws.</summary>
        private static void PutIconBack(FlashScope flash, object forum, object icon)
        {
            try
            {
                flash.Set(icon, "name", SettingsMenuNames.IconSlot);
            }
            catch (Exception)
            {
            }
            try
            {
                if (flash.Get(forum, SettingsMenuNames.IconSlot) == null) flash.Set(forum, SettingsMenuNames.IconSlot, icon);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// The writes: the re-dress first (if it throws, nothing else moves), then hide, then place; a write that throws after the
        /// re-dress puts the stock FORUM dress back and rethrows (not while the FORUM remap is on: the tile reads PERFORMANCE then
        /// whatever happens, and its tap opens the page). <see cref="SettingsMenuState.TileActive"/> only when all succeeded.
        /// <para>Build 22 on Android (<paramref name="android"/>): the re-dress is the full one (title and icon) only while setup()
        /// can still reach the icon, then <see cref="SlotOut"/>; once the icon is out of reach the title alone, and nothing while
        /// the remap is on. A removed instance also gets y <see cref="SettingsMenuNames.OffPanelY"/>, alpha 0 and
        /// <c>mouseChildren=false</c> (setup() writes none of them), MORE GAMES x <see cref="SettingsMenuNames.MoreGamesMarkerX"/>; a
        /// kept one that still reads removed (<see cref="ReadsRemoved"/>) gets <c>visible</c>, alpha 1 and the mouse back first.
        /// Returns the icon's state ("icon slot null" or "icon slot kept (...)"), or null without the re-dress.</para>
        /// </summary>
        private static string WriteGrid(FlashScope flash, SettingsMenuState state, LayoutPlan plan, Dictionary<string, object> found,
                                        bool trim, bool redress, bool android, TitleReport title)
        {
            object forum = redress ? found[SettingsMenuNames.ForumTile] : null;
            object help;
            if (!found.TryGetValue(SettingsMenuNames.HelpTile, out help)) help = null;
            string icon = null;
            if (redress)
            {
                if (!android || flash.Get(forum, SettingsMenuNames.IconSlot) != null)
                {
                    ForumSetup(flash, state, forum, help, Dress(SettingsMenuNames.PerformanceTitle, SettingsMenuNames.PerformanceIcon), android, title);
                    if (android) icon = SlotOut(flash, forum);
                }
                else
                {
                    if (!state.ForumRemapped) ForumSetup(flash, state, forum, help, Title(SettingsMenuNames.PerformanceTitle), android, title);
                    else if (android) TitleRepair(flash, state, forum, help, null, true, title);
                    icon = "icon slot null";
                }
            }
            if (trim)
            {
                try
                {
                    foreach (string hidden in plan.Hidden)
                    {
                        flash.Set(found[hidden], "visible", false);
                        flash.Set(found[hidden], "mouseEnabled", false);
                        if (!android) continue;
                        flash.Set(found[hidden], "mouseChildren", false);
                        flash.Set(found[hidden], "alpha", 0f);
                        flash.Set(found[hidden], "y", (float)SettingsMenuNames.OffPanelY);
                        double? marker = MarkerX(hidden);
                        if (marker != null) flash.Set(found[hidden], "x", (float)marker.Value);
                    }
                    // Absolute, as float (an AS Number; ASValue.SetObject makes a double a managed object): this ends Steam's drift.
                    foreach (Placement p in plan.Kept)
                    {
                        // Android: an instance removed by an earlier plan (btn_forum while PERFORMANCE was off) is drawn and tappable
                        // again; setup() never writes these, so nothing else would.
                        if (android && ReadsRemoved(flash, found[p.Name]))
                        {
                            flash.Set(found[p.Name], "visible", true);
                            flash.Set(found[p.Name], "alpha", 1f);
                            flash.Set(found[p.Name], "mouseEnabled", true);
                            flash.Set(found[p.Name], "mouseChildren", true);
                        }
                        flash.Set(found[p.Name], "x", (float)p.X);
                        flash.Set(found[p.Name], "y", (float)p.Y);
                    }
                }
                catch (Exception)
                {
                    // The tile must not read PERFORMANCE while a tap on it still reaches the game's GoToForum.
                    if (redress && !state.ForumRemapped) Undress(flash, forum, android);
                    throw;
                }
                state.CountLayout();
            }
            if (redress) state.TileActive = true;
            return icon;
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

        /// <summary>BtnBase.setup with a title only: the icon branch (BtnBase.as:130) is skipped.</summary>
        private static Dictionary<string, object> Title(string title)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "title", title } };
        }

        /// <summary>
        /// The stock FORUM dress back, the icon's slot and name first (<see cref="SlotOut"/> undone); on Android through
        /// <see cref="ForumSetup"/> (needAlign off first, so the title stays at the tile's own height). Never throws.
        /// </summary>
        private static void Undress(FlashScope flash, object forum, bool android)
        {
            try
            {
                object icon = DressedIcon(flash, forum);
                if (icon != null) PutIconBack(flash, forum, icon);
                ForumSetup(flash, null, forum, null, Dress(ForumTitle, ForumIcon), android, null);
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------ build 23: the PERFORMANCE tile's title (P1)

        /// <summary>What <see cref="ForumSetup"/> and <see cref="TitleRepair"/> did to btn_forum's title, for the focus-in line.</summary>
        internal sealed class TitleReport
        {
            /// <summary>"needAlign off, title y 294.2", "title y put back (177.53 to 294.2; ...)", "title y kept (290.7)", or null.</summary>
            public string Note;

            /// <summary>Why a centred title could not be put back ("Type: message", "reads 177.53 after write"), or null.</summary>
            public string Failure;

            /// <summary>btn_help's title y next to ours, once per process ("title ref 294.2, btn_help 294.2"), or null.</summary>
            public string Reference;

            /// <summary>The notes joined for the details of the focus-in line, or null.</summary>
            public string Text
            {
                get
                {
                    if (Note == null) return Reference;
                    return Reference == null ? Note : Note + ", " + Reference;
                }
            }
        }

        /// <summary>
        /// Build 23 (owner, build 22 on the S22: "the PERFORMANCE title is drawn in the middle of the tile over its icon"): every
        /// <c>btn_forum.setup(dict)</c> of ours. BtnSimple.setup centres the title while <c>needAlign</c> is true (BtnSimple.as:92-95:
        /// <c>txt.y = height / 2</c>, 177.5 for this tile, which has neither mc_hitzone nor mc_bg), and needAlign defaults to true
        /// (:25); the stock code sets it false just before its own setup (AND-AS MenuSettings.as:110-120), but our dress before
        /// setup() runs first, on the new instances of the first entry, and no stock code writes that txt.y again. So on Android:
        /// <list type="number">
        /// <item><c>needAlign = false</c> first and read back (BtnSetting is a dynamic class, swc/settings/BtnSetting.as:6, so a
        /// write that skipped the setter would make a dynamic member the getter ignores: only the read-back tells).</item>
        /// <item>The setup itself, as before (it throws as before).</item>
        /// <item><see cref="TitleRepair"/>: a title read as centred (below <see cref="SettingsMenuNames.TitleCentredBelow"/>) gets
        /// <see cref="SettingsMenuNames.TileTitleY"/> back, read back too.</item>
        /// </list>
        /// Steps 1 and 3 never throw and never change the outcome of the caller (the run's verify does not read the title, so a
        /// refusal costs only the cosmetic title of build 22). On PC (<paramref name="android"/> false) exactly <c>setup(dict)</c>.
        /// </summary>
        private static void ForumSetup(FlashScope flash, SettingsMenuState state, object forum, object help, Dictionary<string, object> dict,
                                       bool android, TitleReport title)
        {
            if (!android)
            {
                flash.Call(forum, "setup", dict);
                return;
            }
            string align = null;
            try
            {
                flash.Set(forum, SettingsMenuNames.NeedAlign, false);
                object back = flash.Get(forum, SettingsMenuNames.NeedAlign);
                if (!(back is bool) || (bool)back) align = "needAlign not taken (reads " + Shown(back) + ")";
            }
            catch (Exception ex)
            {
                align = "needAlign refused: " + Reason(ex);
            }
            flash.Call(forum, "setup", dict);
            TitleRepair(flash, state, forum, help, align, false, title);
        }

        /// <summary>
        /// Build 23: btn_forum's title back at the tile's own height when it reads as centred, never otherwise (285 to 300 is the
        /// timeline's animated 290.7 to 294.2 with a margin; the target is the constant 294.2, never btn_help's y, which animates
        /// too). <paramref name="align"/>: null when needAlign was taken, else what happened to it; <paramref name="kept"/>: no
        /// setup ran now (the dress before setup() found the tile laid out). Fills <paramref name="title"/> (may be null). Never throws.
        /// </summary>
        private static void TitleRepair(FlashScope flash, SettingsMenuState state, object forum, object help, string align, bool kept,
                                        TitleReport title)
        {
            string note;
            try
            {
                object txt = TitleOf(flash, forum);
                double? y = txt == null ? null : flash.Number(txt, "y");
                if (y == null)
                {
                    note = (align == null ? (kept ? "" : "needAlign off, ") : align + ", ") + "title y unreadable";
                }
                else if (y.Value >= SettingsMenuNames.TitleCentredBelow)
                {
                    // Kept: a note only for a y outside the timeline's own band (no setup ran now, nothing was written).
                    string at = FlashInterop.Text(y.Value);
                    bool usual = y.Value >= SettingsMenuNames.TitleOkMin && y.Value <= SettingsMenuNames.TitleOkMax;
                    note = kept ? (usual ? null : "title y kept (" + at + ")") : (align == null ? "needAlign off" : align) + ", title y " + at;
                }
                else
                {
                    string failure = null;
                    try
                    {
                        flash.Set(txt, "y", (float)SettingsMenuNames.TileTitleY);
                        double? after = flash.Number(txt, "y");
                        if (after == null || Math.Abs(after.Value - SettingsMenuNames.TileTitleY) > PlaceTolerance)
                            failure = "reads " + (after == null ? "nothing" : FlashInterop.Text(after.Value)) + " after write";
                    }
                    catch (Exception ex)
                    {
                        failure = Reason(ex);
                    }
                    string from = FlashInterop.Text(y.Value) + " to " + FlashInterop.Text(SettingsMenuNames.TileTitleY);
                    string why = align == null ? (kept ? "" : "; needAlign off") : "; " + align;
                    if (failure == null)
                    {
                        note = "title y put back (" + from + why + ")";
                    }
                    else
                    {
                        note = "title y not put back (" + failure + why + ")";
                        if (title != null) title.Failure = failure;
                    }
                }
                if (state != null && help != null && title != null && state.BeginTitleRef())
                {
                    object helpTxt = TitleOf(flash, help);
                    double? helpY = helpTxt == null ? null : flash.Number(helpTxt, "y");
                    title.Reference = "title ref " + FlashInterop.Text(SettingsMenuNames.TileTitleY) + ", " + SettingsMenuNames.HelpTile + " " +
                                      (helpY == null ? "unreadable" : FlashInterop.Text(helpY.Value));
                }
            }
            catch (Exception ex)
            {
                note = "title y unreadable (" + Reason(ex) + ")";
            }
            if (title != null) title.Note = note;
        }

        /// <summary>BtnBase's title field: the member <c>txt</c>, else the child of that name; null when neither reads. Reads only.</summary>
        private static object TitleOf(FlashScope flash, object tile)
        {
            object txt = flash.Get(tile, SettingsMenuNames.TitleSlot);
            if (txt == null || txt is string || txt is bool || FlashInterop.Number(txt) != null) txt = flash.Child(tile, SettingsMenuNames.TitleSlot);
            return txt;
        }

        /// <summary>A read value for a log line ("true", "null", "3", "String x").</summary>
        private static string Shown(object value)
        {
            if (value == null) return "null";
            if (value is bool) return (bool)value ? "true" : "false";
            double? number = FlashInterop.Number(value);
            if (number != null) return FlashInterop.Text(number.Value);
            return value.GetType().Name;
        }

        /// <summary>Once per process: "settings menu: PERFORMANCE title y not put back (...); the title stays centred as in build 22".</summary>
        private static void ReportTitle(ISettingsMenuHost host, SettingsMenuState state, TitleReport title)
        {
            if (title == null || title.Failure == null || !state.BeginTitleFailLog()) return;
            Write(host, "settings menu: PERFORMANCE title y not put back (" + title.Failure + "); the title stays centred as in build 22");
        }

        private static string Reason(Exception ex)
        {
            Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            return cause.GetType().Name + ": " + cause.Message;
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
                if (log != null && line != null) log.Info(line);
            }
            catch (Exception)
            {
            }
        }
    }
}
