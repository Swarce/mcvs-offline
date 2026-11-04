using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Embedded.Admin.SettingsMenu;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// Build 20 (owner, build 19 on both platforms): the trimmed grid on the first entry, the stock Settings menu again after a tab
    /// and Back. The grid comes from one run per <c>EnterSection("settings_main_screen")</c> (<see cref="SettingsMenuLayout.Schedule"/>),
    /// driven here through the real registry (<see cref="UiNatives"/> with its own steps) on the fake panels, frame by frame as the
    /// game runs them (<see cref="FakeSettingsMenuHost.RunFrame"/>: the context drain, then the Flash frame).
    /// <para>These tests do NOT reproduce the owner's report. The real gluic order is the fake's default: MenusStack.popMenu
    /// (gluic/components/menus/MenusStack.as:357-366) pops, then <c>focusGiven</c> sends FOCUS_IN synchronously (Menu.as:157-188),
    /// so EnterSection runs with Settings on top and <c>setup()</c> right after it (MenuSettings.as:468-473); nothing in the AS
    /// positions the panel again later. In that order build 19's single step lays the grid out too, Steam's new instances
    /// (<see cref="FakeFlashTree.SteamReplacesInstances"/>, the closest model of PC-AS :44-52) included. The rows that fail on build
    /// 19 are hypothetical models with no AS source: the popped submenu still answering <c>getMenuOnTopOfStack</c> for some frames
    /// (<see cref="FakeFlashTree.PoppedStaysOnTopFrames"/>, refuted by popMenu's order) and the stock positioning running again some
    /// frames after the return (<see cref="FakeFlashTree.RestockFramesOnReturn"/>). They pin what the run does if the device does
    /// either; the device precondition itself is for the next device run's log to name (the run's "focus-in N" line, its laid-out or
    /// not-laid-out line, or no line at all for a return: EnterSection never reached us).</para>
    /// </summary>
    public class SettingsMenuReturnTests
    {
        internal sealed class Rig
        {
            /// <summary>
            /// The real registry on the fake panels. <paramref name="sameFrame"/> false: the same-frame step does nothing (build 20's
            /// path: only the run lays the grid out, from the frame after the focus-in), for the tests of the run itself.
            /// </summary>
            public Rig(bool steam, bool sameFrame = true)
            {
                Steam = steam;
                Ui = new FakeFlashTree(steam);
                Host = new FakeSettingsMenuHost(Ui, steam);
                Natives = new UiNatives(() => Host, new SettingsMenuState());
                if (!sameFrame) Natives.Steps.SameFrame = (h, s, n) => { };
                Natives.Ensure();
            }

            public bool Steam { get; private set; }

            public FakeFlashTree Ui { get; private set; }

            public FakeSettingsMenuHost Host { get; private set; }

            public UiNatives Natives { get; private set; }

            public SettingsMenuState State { get { return Natives.State; } }

            /// <summary>The grid's lines (not the registry's, not the page's), in order.</summary>
            public List<string> GridLines
            {
                get
                {
                    return Host.Lines("settings menu: ").Where(l => l.StartsWith("settings menu: frame ", StringComparison.Ordinal) ||
                                                                    l.StartsWith("settings menu: not laid out", StringComparison.Ordinal)).ToList();
                }
            }

            /// <summary>The player looks at the grid: the game runs until nothing is posted, then another second of frames.</summary>
            public void Stay()
            {
                Host.Drain();
                Host.RunFrames(60);
            }

            /// <summary>Opens Settings from the main menu and stays.</summary>
            public void Open()
            {
                Ui.OpenSettings();
                Stay();
            }

            /// <summary>A tab (or ABOUT, or PERFORMANCE), some frames on it, then Back to the grid and stays.</summary>
            public void TabAndBack(string tab)
            {
                Assert.True(Ui.Tap(tab), tab + " took no tap");
                Host.Drain();
                Host.RunFrames(10);
                Assert.NotEqual(McvsMenuNames.Settings, Ui.Top.Name);
                Ui.Back();
                Stay();
            }
        }

        /// <summary>The end of the laid-out line of a run whose focus-in frame laid the grid out and that never saw the stock grid.</summary>
        internal const string SameFrameDone = " in the focus-in frame; stock grid drawn 0 frames";

        /// <summary>The trimmed grid of the platform, on the menu on top; on Android the PERFORMANCE tile active.</summary>
        private static void AssertLaidOut(Rig rig, string when)
        {
            Assert.True(rig.Ui.Top != null && rig.Ui.Top.Name == McvsMenuNames.Settings, when + ": Settings is not on top");
            SettingsMenuLayout.LayoutPlan plan = SettingsMenuLayout.Plan(rig.Steam, true);
            foreach (SettingsMenuLayout.Placement p in plan.Kept)
            {
                FlashObject item = rig.Ui.Instance(p.Name);
                Assert.True(item != null, when + ": no " + p.Name);
                Assert.True(item.Num("x") == (float)p.X && item.Num("y") == (float)p.Y,
                            when + ": " + p.Name + " at (" + item.Num("x") + ", " + item.Num("y") + "), not " + p);
                Assert.True((bool)item.Get("visible"), when + ": " + p.Name + " hidden");
            }
            foreach (string name in plan.Hidden)
            {
                FlashObject item = rig.Ui.Instance(name);
                Assert.False((bool)item.Get("visible"), when + ": " + name + " shown");
                Assert.False((bool)item.Get("mouseEnabled"), when + ": " + name + " takes taps");
            }
            if (!rig.Steam)
            {
                Assert.True("setting.Performance" == rig.Ui.Instance("btn_forum").Get("title") as string, when + ": the tile reads FORUM");
                Assert.True(rig.State.TileActive, when + ": the tile is not active");
            }
            Assert.False(rig.State.RedressFailed, when + ": RedressFailed");
            Assert.Equal(0, rig.Host.Pending);
        }

        private static string[] Tabs(bool steam)
        {
            return steam
                ? new[] { "btn_general", "btn_performance", "btn_audio", "btn_help", "btn_language", "btn_account", "btn_credits", "btn_general" }
                : new[] { "btn_general", "btn_forum", "btn_audio", "btn_help", "btn_language", "btn_account", "btn_credits", "btn_general", "btn_forum" };
        }

        // ------------------------------------------------------------------ every return ends laid out

        [Theory]
        [InlineData(false, 0, 0, false)]   // the gluic order: these three pass on build 19 too
        [InlineData(true, 0, 0, false)]
        [InlineData(true, 0, 0, true)]
        [InlineData(false, 3, 0, false)]   // model: the popped submenu answers getMenuOnTopOfStack for 3 frames
        [InlineData(true, 3, 0, true)]
        [InlineData(false, 0, 5, false)]   // model: the stock grid comes back 5 frames after the return
        [InlineData(true, 0, 5, true)]
        [InlineData(false, 0, 30, false)]
        [InlineData(true, 0, 30, false)]
        [InlineData(false, 8, 20, false)]
        [InlineData(true, 8, 20, true)]
        public void TheGridIsLaidOutAfterTheFirstEntryAndAfterEveryReturn(bool steam, int poppedOnTop, int restock, bool newInstances)
        {
            var rig = new Rig(steam);
            rig.Ui.PoppedStaysOnTopFrames = poppedOnTop;
            rig.Ui.RestockFramesOnReturn = restock;
            rig.Ui.SteamReplacesInstances = newInstances;
            rig.Open();
            AssertLaidOut(rig, "first entry");
            int entries = 1;
            foreach (string tab in Tabs(steam))
            {
                rig.TabAndBack(tab);
                entries++;
                AssertLaidOut(rig, "back from " + tab);
                // The page (Android's PERFORMANCE) has ended on the return.
                Assert.False(rig.State.PerformanceMode);
            }
            // One line per entry, each a laid-out line; and the grid stays laid out while the player stays.
            Assert.Equal(entries, rig.GridLines.Count);
            Assert.All(rig.GridLines, l => Assert.StartsWith("settings menu: frame " + (steam ? 1 : 3) + ", " + (steam ? 6 : 7) + " tiles + ABOUT laid out", l));
            rig.Host.RunFrames(200);
            AssertLaidOut(rig, "200 frames later");
            rig.Ui.AssertAllDisposed();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AStockGridThatComesBackAfterTheStepIsLaidOutAgainAndSaid(bool steam)
        {
            var rig = new Rig(steam);
            rig.Ui.RestockFramesOnReturn = 10;
            rig.Open();
            rig.TabAndBack("btn_audio");
            AssertLaidOut(rig, "back from AUDIO");
            Assert.Equal("settings menu: frame " + (steam ? "1, 6" : "3, 7") + " tiles + ABOUT laid out in the focus-in frame; laid out again 1x after the stock grid came back; stock grid drawn 1 frame",
                         rig.GridLines.Last());
            Assert.Equal(3, rig.State.Layouts);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ASubmenuStillOnTopDoesNotDelayTheGrid(bool steam)
        {
            // Settings is found by name below the leaving submenu, so the grid is right from the first frame after the return.
            var rig = new Rig(steam);
            rig.Ui.PoppedStaysOnTopFrames = 6;
            rig.Open();
            Assert.True(rig.Ui.Tap("btn_help"));
            rig.Host.Drain();
            rig.Ui.Back();
            Assert.Equal("menu_help", rig.Ui.StackTop.Name);
            rig.Host.RunFrame();
            Assert.Equal("menu_help", rig.Ui.StackTop.Name);
            Assert.False((bool)rig.Ui.Instance("btn_social").Get("visible"));
            Assert.Equal((float)SettingsMenuLayout.AboutX, rig.Ui.Instance("btn_credits").Num("x"));
            rig.Host.Drain();
            AssertLaidOut(rig, "back from HELP");
            Assert.Equal(2, rig.State.Layouts);
        }

        [Fact]
        public void TheRestartPromptAndTheGridBothComeOnTheReturnFromThePage()
        {
            var rig = new Rig(false);
            rig.Ui.RestockFramesOnReturn = 4;
            rig.Open();
            Assert.True(rig.Ui.Tap("btn_forum"));
            rig.Host.Drain();
            Assert.True(rig.State.PerformanceMode);
            rig.State.RestartPending = true;   // a GRAPHICS change on the page
            rig.Ui.Back();
            rig.Stay();
            AssertLaidOut(rig, "back from PERFORMANCE");
            Assert.Single(rig.Ui.Popups);
            Assert.False(rig.State.RestartPending);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AfterAMatchTheFreshSettingsMenuIsLaidOut(bool steam)
        {
            var rig = new Rig(steam);
            rig.Ui.RestockFramesOnReturn = 5;
            rig.Ui.SteamReplacesInstances = steam;
            rig.Open();
            rig.Ui.Back();                         // Settings closed
            rig.Ui.InGame = true;                  // the pause panel: pvp_settings_screen, no run
            rig.Ui.OpenSettings();
            rig.Host.Drain();
            Assert.Equal(2, rig.Ui.Panel.Frame);
            rig.Ui.Back();
            rig.Ui.InGame = false;                 // back in the main menu after the match
            rig.Open();
            AssertLaidOut(rig, "after a match");
            rig.TabAndBack("btn_general");
            AssertLaidOut(rig, "after a match, back from GAMEPLAY");
            Assert.Equal(3, rig.GridLines.Count);
        }

        // ------------------------------------------------------------------ the run itself

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OnceLaidOutTheRunOnlyReadsUntilItStops(bool steam)
        {
            var rig = new Rig(steam);
            rig.Ui.OpenSettings();
            rig.Host.Drain();
            // It watched the settle window, then two more frames found laid out, and stopped (nothing is posted any more).
            Assert.InRange(rig.Host.Frames, SettingsMenuLayout.SettleFrames, SettingsMenuLayout.SettleFrames + SettingsMenuLayout.StableFrames);
            int writes = rig.Ui.HostWrites.Count;
            rig.Host.RunFrames(300);
            Assert.Equal(writes, rig.Ui.HostWrites.Count);
            Assert.Equal(1, rig.State.Layouts);
            Assert.Equal(0, rig.Host.Pending);
            Assert.Equal(new[] { "settings menu: frame " + (steam ? "1, 6" : "3, 7") + " tiles + ABOUT laid out" + SameFrameDone }, rig.GridLines);
        }

        [Fact]
        public void ANewFocusInEndsTheRunningOneWithoutALine()
        {
            var rig = new Rig(false);
            rig.Ui.OpenSettings();
            rig.Host.RunFrame();                    // laid out, the run watches
            Assert.Equal(1, rig.State.Layouts);
            Assert.True(rig.Ui.Tap("btn_audio"));
            rig.Ui.Back();                          // the next focus-in before the first run is done
            rig.Host.Drain();
            Assert.Equal(new[] { "settings menu: frame 3, 7 tiles + ABOUT laid out" + SameFrameDone }, rig.GridLines);
            Assert.Equal(2, rig.State.Layouts);
            AssertLaidOut(rig, "second entry");
        }

        [Fact]
        public void WhileTheGridCannotBeReadTheTileIsInactiveAndLaterItIsLaidOut()
        {
            var rig = new Rig(false, sameFrame: false);
            FlashObject menu = rig.Ui.OpenSettings();
            FlashObject panel = menu.Child("mc_panel");
            FlashObject more = panel.Child("btn_more");
            panel.Children.Remove(more);
            rig.Host.RunFrames(5);
            Assert.False(rig.State.TileActive);
            Assert.True(rig.Ui.Tap("btn_forum"));   // FORUM as the game has it
            Assert.Equal(1, rig.Ui.Game.ForumCalls);
            Assert.False(rig.State.PerformanceMode);
            panel.Add(more);
            rig.Host.Drain();
            AssertLaidOut(rig, "once btn_more is there");
            Assert.Equal(new[] { "settings menu: frame 3, 7 tiles + ABOUT laid out after 6 frames (no btn_more); stock grid drawn 6 frames" }, rig.GridLines);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AGridThatNeverBecomesReadableIsOneLineAfterTheLastFrameAndKeepsTheGameplayRows(bool steam)
        {
            var rig = new Rig(steam, sameFrame: false);
            FlashObject menu = rig.Ui.OpenSettings();
            menu.Child("mc_panel").Children.Remove(rig.Ui.Instance("btn_help"));
            rig.Host.Drain();
            Assert.Equal(new[] { SettingsMenuLayoutTests.GaveUp("no btn_help") }, rig.GridLines);
            Assert.Equal(SettingsMenuLayout.MaxFrames, rig.Host.Frames);
            Assert.Equal(0, rig.State.Layouts);
            Assert.False(rig.State.TileActive);
            Assert.Equal(!steam, rig.State.RedressFailed);
            Assert.Empty(rig.Ui.HostWrites.Where(w => !w.Contains("Native")));
        }

        [Fact]
        public void WithoutANextFrameTheRunEndsAfterItsFirstLookAndSaysSo()
        {
            var rig = new Rig(false);
            rig.Host.CanPostNextFrame = false;
            rig.Open();
            Assert.Equal(new[] { "settings menu: frame 3, 7 tiles + ABOUT laid out" + SameFrameDone + "; then no next frame" }, rig.GridLines);
            Assert.True(rig.State.TileActive);

            var waiting = new Rig(false, sameFrame: false);
            waiting.Host.CanPostNextFrame = false;
            FlashObject menu = waiting.Ui.OpenSettings();
            menu.Child("mc_panel").Children.Remove(waiting.Ui.Instance("btn_more"));
            waiting.Host.Drain();
            Assert.Equal(new[] { "settings menu: not laid out (no btn_more; no next frame)" }, waiting.GridLines);
            Assert.True(waiting.State.RedressFailed);
        }

        // ------------------------------------------------------------------ the diagnostic, the tile through a run, an early Back

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void EveryFocusInLogsWhatItsFirstFrameFinds(bool steam, bool newInstances)
        {
            // Build 20's path (the same-frame step does nothing), so the first frame finds what setup() left; the same-frame
            // version is SettingsMenuSameFrameTests.TheFocusInLineSaysTheFocusInFrameLaidTheGridOut.
            var rig = new Rig(steam, sameFrame: false);
            rig.Ui.SteamReplacesInstances = newInstances;
            rig.Ui.PoppedStaysOnTopFrames = 3;
            rig.Open();
            int at = rig.Ui.Stack.FindIndex(m => m.Name == McvsMenuNames.Settings);
            string frame = ", panel frame " + (steam ? 1 : 3) + ", btn_social ";
            string tail = "; same frame: no look (" + (steam ? "GetLanguage" : "GetPlatformId") + " calls 0, looks 0)";
            rig.TabAndBack("btn_help");
            rig.TabAndBack("btn_audio");
            Assert.Equal(new[]
            {
                "settings menu: focus-in 1 (layouts so far: 0): on top menu_settings, menu_settings at " + at + " in the stack" + frame + "shown" + tail,
                // The submenu still on top, Settings found below it; btn_social as the last layout left it, or a new one.
                "settings menu: focus-in 2 (layouts so far: 1): on top menu_help, menu_settings at " + at + " in the stack" + frame + (newInstances ? "shown" : "hidden") + tail,
                "settings menu: focus-in 3 (layouts so far: 2): on top menu_audio, menu_settings at " + at + " in the stack" + frame + (newInstances ? "shown" : "hidden") + tail,
            }, rig.Host.Lines("settings menu: focus-in "));
            AssertLaidOut(rig, "back from AUDIO");
        }

        [Fact]
        public void AReadThatFailsOnAWidthAfterTheWriteKeepsTheTileActiveAndIsNoLayoutAgain()
        {
            var rig = new Rig(false);
            rig.Ui.OpenSettings();
            rig.Host.RunFrame();
            Assert.True(rig.State.TileActive);
            FlashObject general = rig.Ui.Instance("btn_general");
            object width = general.Get("width");
            general.Members["width"] = 300f;          // a scale tween on the tile, say: the same instances, the same dress
            rig.Host.RunFrames(3);
            Assert.True(rig.State.TileActive, "the tile reads PERFORMANCE, and a tap must open the page");
            Assert.True(PerformancePage.HidesGameplayRows(rig.Host, rig.State));
            general.Members["width"] = width;
            rig.Host.Drain();
            Assert.Equal(new[] { "settings menu: frame 3, 7 tiles + ABOUT laid out" + SameFrameDone }, rig.GridLines);
            Assert.Equal(1, rig.State.Layouts);
            AssertLaidOut(rig, "after the tween");
            Assert.True(rig.Ui.Tap("btn_forum"));
            rig.Host.Drain();
            Assert.Equal(0, rig.Ui.Game.ForumCalls);
            Assert.True(rig.State.PerformanceMode);
        }

        [Fact]
        public void AnotherPanelAfterTheWriteDropsTheTileAndItsReDressIsNotALayoutAgain()
        {
            var rig = new Rig(false);
            FlashObject menu = rig.Ui.OpenSettings();
            rig.Host.RunFrame();
            FlashObject panel = menu.Child("mc_panel");
            FlashObject more = panel.Child("btn_more");
            panel.Children.Remove(more);
            rig.Host.RunFrames(2);
            Assert.False(rig.State.TileActive);
            panel.Add(more);
            rig.Host.Drain();
            AssertLaidOut(rig, "once btn_more is back");
            Assert.Equal(new[] { "settings menu: frame 3, 7 tiles + ABOUT laid out in the focus-in frame; tile re-dressed again 1x after another panel; stock grid drawn 3 frames" }, rig.GridLines);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BackWhileTheRunWaitsEndsItAtOnceWithOneLine(bool steam)
        {
            var rig = new Rig(steam, sameFrame: false);
            FlashObject menu = rig.Ui.OpenSettings();
            menu.Child("mc_panel").Children.Remove(rig.Ui.Instance("btn_help"));
            rig.Host.RunFrames(3);
            rig.Ui.Back();                          // Settings closed before the grid could be laid out
            rig.Host.Drain();
            Assert.Equal(new[] { "settings menu: not laid out (no btn_help; Settings left after 4 frames)" }, rig.GridLines);
            Assert.Equal(4, rig.Host.Frames);
            Assert.Equal(0, rig.Host.Pending);
            // Settings was read and its panel is one this build cannot lay out: the Gameplay rows stay (the in-match gate needs no tile).
            Assert.Equal(!steam, rig.State.RedressFailed);
        }

        [Fact]
        public void TheSwitchesOffMidRunEndItWithoutALine()
        {
            var rig = new Rig(false);
            rig.Ui.OpenSettings();
            rig.Host.RunFrame();
            rig.Host.SettingsMenuTrim = false;
            rig.Host.PerformanceSetting = false;
            rig.Host.Drain();
            Assert.Empty(rig.GridLines);
            Assert.Equal(0, rig.Host.Pending);
        }
    }
}
