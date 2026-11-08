using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embedded.Admin.SettingsMenu;
using McvsOffline.Server.Core;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// Build 22 (owner, build 21 on the S22: "it flashes the more games and the forum button after, the trim remains"; design
    /// docs/design/settings-menu.md §3.2): Android's <c>setup()</c> stock writes draw nothing whatever the timing of our steps,
    /// because the panel is dressed before setup() runs (SettingsMenuLayout.PreDress, inside EnterSection's native) on
    /// properties setup() never writes, and its FORUM title is remapped to PERFORMANCE by the engine itself; plus the switch
    /// <c>admin.settingsMenu.sameFrame</c> and the breadcrumb that turns it off after a start that ended inside our Flash work.
    /// The fakes model the AS: setup()'s gotoAndStop(3) (new instances on the first entry), BtnBase.setup's
    /// <c>if (Boolean(this.mc_image) &amp;&amp; icon)</c> in three resolution models, ImageContainer's reload, FormatString through the
    /// World's LocId remap and ReloadStringTable, and what each render draws (alpha, y off the panel, the drawn text and icon).
    /// <para>Every test here fails on build 21's SettingsMenu code (9ca6bf3) unless it says it is a regression check; the file uses
    /// only members build 21 has (the rest is on the fakes), so it builds against those sources. The last section (the review of
    /// build 22) fails on 05a2028's code instead, except its regression check.</para>
    /// </summary>
    public class SettingsMenuBuild22Tests
    {
        private const string Main = "settings_main_screen";
        private const string Perf = "setting.Performance";
        private const string PerfIcon = "Menus/assets/png/icons/settings_performance.png";
        private const string ForumIcon = "Menus/assets/png/icons/settings_forum.png";

        /// <summary>Where Android's setup() puts ABOUT (AND-AS :213), and so build 22's slot for it.</summary>
        private const float AboutX = 208f;

        private sealed class Rig
        {
            public Rig(bool steam = false, bool look = true, IconSlotMode icon = IconSlotMode.Slot)
            {
                Steam = steam;
                Ui = new FakeFlashTree(steam) { SteamReplacesInstances = steam, IconSlot = icon };
                Host = new FakeSettingsMenuHost(Ui, steam);
                Natives = new UiNatives(() => Host, new SettingsMenuState());
                // look false: the same-frame observer is never called (the device's 1b) or does nothing.
                if (!look) Natives.Steps.SameFrame = (h, s, n) => { };
                Natives.Ensure();
            }

            public bool Steam { get; private set; }

            /// <summary>Build 23's default: ABOUT drawn on MORE GAMES' button (Android, the trim and the same-frame switch on).</summary>
            public bool AboutOnMoreGames
            {
                get { return !Steam && Host.SettingsMenuAboutCentre && Host.SettingsMenuTrim && Host.SettingsMenuSameFrame && Ui.World != null; }
            }

            public FakeFlashTree Ui { get; private set; }
            public FakeSettingsMenuHost Host { get; private set; }
            public UiNatives Natives { get; private set; }
            public SettingsMenuState State { get { return Natives.State; } }

            public List<string> GridLines
            {
                get
                {
                    return Host.Lines("settings menu: ").Where(l => l.StartsWith("settings menu: frame ", StringComparison.Ordinal) ||
                                                                    l.StartsWith("settings menu: not laid out", StringComparison.Ordinal)).ToList();
                }
            }

            public void Frame(Action input)
            {
                Host.RunFrame(input);
            }

            public void Stay()
            {
                Host.Drain();
                Host.RunFrames(60);
            }

            public void Open()
            {
                Frame(() => Ui.OpenSettings());
                Stay();
            }

            /// <summary>A tab, ten frames on it, Back in a frame (whose render is the focus-in frame's), then stays.</summary>
            public int TabAndBack(string tab)
            {
                Frame(() => Assert.True(Ui.Tap(tab), tab + " took no tap"));
                Host.RunFrames(10);
                int from = Ui.Renders.Count;
                Frame(() => Ui.Back());
                Stay();
                return from;
            }
        }

        /// <summary>
        /// What a render of Android's main Settings panel drew that is not the trimmed grid: a removed instance drawn or taking taps,
        /// a kept tile off its slot, FORUM's text or icon, ABOUT off setup()'s own x. Null when nothing.
        /// </summary>
        private static string Stock(RenderPoint render, bool about = false)
        {
            if (render.Top != McvsMenuNames.Settings || render.PanelFrame != 3) return null;
            var wrong = new List<string>();
            foreach (SettingsMenuLayout.Placement p in SettingsMenuReturnTests.PlanOf(false, true, about).Kept)
            {
                RenderedInstance item;
                if (!render.Instances.TryGetValue(p.Name, out item)) wrong.Add("no " + p.Name);
                else if (!item.Drawn) wrong.Add(p.Name + " not drawn");
                else if (p.Name == "btn_credits") { if (Math.Abs(item.X - AboutX) > 0.5) wrong.Add("ABOUT at " + item.X); }
                else if (Math.Abs(item.X - (float)p.X) > 0.5 || Math.Abs(item.Y - (float)p.Y) > 0.5) wrong.Add(p.Name + " at (" + item.X + ", " + item.Y + ")");
                // Build 23: ABOUT drawn on MORE GAMES' button, through the remap.
                else if (p.Name == "btn_more" && item.Text != "setting.CreditButton") wrong.Add("btn_more reads " + item.Text);
            }
            foreach (string name in new[] { "btn_social", "btn_customer", about ? "btn_credits" : "btn_more" })
            {
                RenderedInstance item;
                if (render.Instances.TryGetValue(name, out item) && (item.Drawn || item.TakesTaps))
                    wrong.Add(name + " drawn at (" + item.X + ", " + item.Y + ")" + (item.TakesTaps ? ", taking taps" : ""));
            }
            RenderedInstance forum;
            if (render.Instances.TryGetValue("btn_forum", out forum))
            {
                if (forum.Text != Perf) wrong.Add("btn_forum reads " + forum.Text);
                if (forum.Icon != PerfIcon) wrong.Add("btn_forum shows " + forum.Icon);
            }
            return wrong.Count == 0 ? null : string.Join(", ", wrong);
        }

        private static List<string> StockRenders(Rig rig, int from)
        {
            var stock = new List<string>();
            foreach (RenderPoint r in rig.Ui.Renders.Skip(from))
            {
                string why = Stock(r, rig.AboutOnMoreGames);
                if (why != null) stock.Add(r + ": " + why);
            }
            return stock;
        }

        // ------------------------------------------------------------------ nothing stock is drawable, whatever the look does

        [Theory]
        [InlineData(0)]   // IconSlotMode.Slot
        [InlineData(1)]   // IconSlotMode.ChildByName
        public void Return_ObserverNeverCalled_NothingStockIsDrawn(int mode)
        {
            var icon = (IconSlotMode)mode;
            // Build 21: the render of the Back frame draws MORE GAMES at (-177, 354.1), FORUM and ABOUT at 208 (the run fixes it
            // one frame later).
            var rig = new Rig(look: false, icon: icon);
            rig.Open();
            foreach (string tab in new[] { "btn_audio", "btn_help", "btn_forum", "btn_language", rig.AboutOnMoreGames ? "btn_more" : "btn_credits" })
            {
                int from = rig.TabAndBack(tab);
                Assert.Equal(new string[0], StockRenders(rig, from));
            }
            // The run, the safety net, found every return drawn as ours; MORE GAMES was shown again by setup(), off the panel.
            Assert.All(rig.GridLines.Skip(1), l => Assert.Equal(
                "settings menu: frame 3, 7 tiles + ABOUT laid out" + SettingsMenuReturnTests.SealedDoneFor(rig.AboutOnMoreGames), l));
            rig.Ui.AssertAllDisposed();
        }

        [Fact]
        public void Return_LookThrows_NothingStockIsDrawn()
        {
            var rig = new Rig();
            rig.Natives.Steps.SameFrame = (h, s, n) => { throw new InvalidOperationException("the device's native fault, say"); };
            rig.Open();
            int from = rig.TabAndBack("btn_general");
            Assert.Equal(new string[0], StockRenders(rig, from));
            Assert.False(rig.State.RedressFailed);
        }

        [Fact]
        public void FirstEntry_ObserverNeverCalled_GridTrimmedInTheFocusInFrame()
        {
            // The first entry's panel is still on frame 1 at EnterSection: the dress goes to frame 3 first (setup()'s own goto is
            // then a no-op) and dresses the instances setup() will use. Build 21: the render of the opening frame is the stock grid.
            var rig = new Rig(look: false);
            rig.Frame(() => rig.Ui.OpenSettings());
            RenderPoint first = rig.Ui.Renders.Last();
            Assert.Equal(McvsMenuNames.Settings, first.Top);
            Assert.Equal(3, first.PanelFrame);
            Assert.Null(Stock(first, rig.AboutOnMoreGames));
            Assert.Contains("menu_settings.mc_panel.gotoAndStop(3)", rig.Ui.HostWrites);
        }

        [Fact]
        public void Return_TrimOff_PerformanceOn_TileReadsPerformanceInTheFocusInFrame()
        {
            // Build 21 does not cover this case (no same-frame step without the trim): the Back frame draws FORUM.
            var rig = new Rig();
            rig.Host.SettingsMenuTrim = false;
            rig.Open();
            int from = rig.TabAndBack("btn_audio");
            RenderPoint back = rig.Ui.Renders[from];
            Assert.Equal(Perf, back.Instances["btn_forum"].Text);
            Assert.Equal(PerfIcon, back.Instances["btn_forum"].Icon);
            // The stock slot: nothing moves without the trim, and MORE GAMES is the game's.
            Assert.Equal(6.35f, back.Instances["btn_forum"].X);
            Assert.True(back.Instances["btn_more"].Drawn);
        }

        [Fact]
        public void LanguageReload_TileStaysPerformance()
        {
            // Regression check (build 21 passes it too in the fake, whose reload order is the registration order).
            var rig = new Rig();
            rig.Open();
            rig.TabAndBack("btn_language");
            rig.Ui.ReloadStringTable();
            Assert.Equal(Perf, rig.Ui.TileText("btn_forum"));
            rig.Host.RunFrames(5);
            Assert.Equal(Perf, rig.Ui.TileText("btn_forum"));
        }

        [Fact]
        public void Return_MoreGamesShownBySetupOffThePanel_CountsAsInPlace()
        {
            var rig = new Rig(look: false);
            // Build 22's bottom row (MORE GAMES removed by us): admin.settingsMenu.aboutCentre off.
            rig.Host.AboutCentreSetting = false;
            rig.Open();
            int layouts = rig.State.Layouts;
            rig.TabAndBack("btn_audio");
            // Build 21: "laid out; ... stock grid drawn 1 frame" and a layout per return.
            Assert.Equal("settings menu: frame 3, 7 tiles + ABOUT laid out in the focus-in frame; MORE GAMES shown off the panel 1 frame; stock grid drawn 0 frames",
                         rig.GridLines.Last());
            Assert.Equal(layouts, rig.State.Layouts);
            // Tidied by the run: hidden again, at its marker x, no taps.
            FlashObject more = rig.Ui.Instance("btn_more");
            Assert.False((bool)more.Get("visible"));
            Assert.Equal(0f, more.Num("x"));
            Assert.Equal(0f, more.Num("alpha"));
            Assert.False(rig.Ui.Tap("btn_more"));
            Assert.True(rig.Ui.Tap("btn_credits"));
        }

        [Fact]
        public void EarlyGate_UnreadableAbout_NoLook()
        {
            // Build 21's gate looked whenever ABOUT could not be read and spent its 4 looks before setup()'s dress.
            var rig = new Rig();
            rig.Ui.SettingsBuilt = menu => { FlashObject panel = menu.Child("mc_panel"); if (panel.Child("btn_credits") != null) panel.Children.Remove(panel.Child("btn_credits")); };
            rig.Ui.OpenSettings();
            Assert.Equal(0, rig.State.SameFrame.Looks);
            Assert.True(rig.State.SameFrame.Calls > 30);
        }

        [Fact]
        public void TileActive_SetInEnterSection_BeforeSetup_AndR4HidesTheRowsInTheFocusInFrame()
        {
            // MoveToCamera runs between EnterSection and setup() (MenuSettings.onFocusIn, AND-AS :468-473).
            var rig = new Rig(look: false);
            var atMoveToCamera = new List<bool>();
            rig.Ui.OverrideNativeFunction("MoveToCamera", new Action<string, string>((a, b) => atMoveToCamera.Add(rig.State.TileActive)));
            rig.Open();
            rig.Frame(() => Assert.True(rig.Ui.Tap("btn_audio")));
            rig.Frame(() =>
            {
                rig.Ui.Back();
                // Still the focus-in frame: build 21 has the tile inactive until the run (or the look) re-dressed it.
                Assert.True(rig.State.TileActive);
                Assert.True(PerformancePage.HidesGameplayRows(rig.Host, rig.State));
            });
            Assert.Equal(new[] { true, true }, atMoveToCamera);
            // And a tap in the next frame opens the page, not the forum.
            rig.Frame(() => Assert.True(rig.Ui.Tap("btn_forum")));
            rig.Host.Drain();
            Assert.Equal(0, rig.Ui.Game.ForumCalls);
            Assert.Equal(McvsMenuNames.Gameplay, rig.Ui.Top.Name);
        }

        [Theory]
        [InlineData(0)]   // IconSlotMode.Slot
        [InlineData(1)]   // IconSlotMode.ChildByName
        public void IconSlot_OutOfReach_TheStockIconIsSkippedOnEveryReturn(int mode)
        {
            var icon = (IconSlotMode)mode;
            var rig = new Rig(icon: icon);
            rig.Open();
            int loads = rig.Ui.IconLoads;
            rig.TabAndBack("btn_audio");
            rig.TabAndBack("btn_help");
            // Build 21: setup()'s FORUM icon, then our PERFORMANCE again, two loads per return (an empty icon until COMPLETE).
            Assert.Equal(loads, rig.Ui.IconLoads);
            Assert.Equal(PerfIcon, rig.Ui.TileIcon("btn_forum"));
            Assert.Contains("; before setup: kept (FORUM remap on, icon slot null, " +
                            (rig.AboutOnMoreGames ? "ABOUT on MORE GAMES (-177), ABOUT tile off the panel" : "MORE GAMES off the panel, ABOUT 208") + ")",
                            rig.Host.Lines("settings menu: focus-in ").Last());
        }

        [Fact]
        public void IconSlotStuck_TheLookReDressesTheIconInsideSetup()
        {
            // The third model: neither the null slot nor the rename keeps setup() from the icon. The look after setup()'s last
            // write re-dresses it, so the focus-in frame still draws PERFORMANCE, and the line says so.
            var rig = new Rig(icon: IconSlotMode.Stuck);
            rig.Open();
            int from = rig.TabAndBack("btn_audio");
            Assert.Equal(new string[0], StockRenders(rig, from));
            Assert.Contains("icon slot kept (still reachable)", rig.Host.Lines("settings menu: focus-in ").Last());
            Assert.Equal("mc_image", FakeFlashTree.IconContainer(rig.Ui.Instance("btn_forum")).Name);
        }

        // ------------------------------------------------------------------ the remap, and without it

        [Fact]
        public void PerformanceOff_NoRemap_ForumStock()
        {
            // Regression check: the switch off leaves the game's FORUM tile, title, icon and tap.
            var rig = new Rig();
            rig.Host.PerformanceSetting = false;
            rig.Open();
            rig.TabAndBack("btn_audio");
            var formatter = (FakeLocalizationFormatter)rig.Ui.World.LocalizationFormatter;
            Assert.False(formatter.Contains("setting.ForumButton"));
            Assert.Equal("setting.ForumButton", rig.Ui.TileText("btn_forum"));
            Assert.False((bool)rig.Ui.Instance("btn_forum").Get("visible"));   // hidden by the trim (design §3.3)
        }

        [Fact]
        public void TurningPerformanceOffTakesTheRemapOutAndPutsTheIconBack()
        {
            var rig = new Rig();
            rig.Host.SettingsMenuTrim = false;
            rig.Open();
            var formatter = (FakeLocalizationFormatter)rig.Ui.World.LocalizationFormatter;
            Assert.True(formatter.Contains("setting.ForumButton"));
            object before = formatter.Map;
            rig.Host.PerformanceSetting = false;
            rig.TabAndBack("btn_audio");
            Assert.False(formatter.Contains("setting.ForumButton"));
            Assert.NotSame(before, formatter.Map);                         // copy and swap, never changed in place
            Assert.Equal(1, rig.Host.Lines("settings menu: FORUM remap installed").Count);
            Assert.Equal(1, rig.Host.Lines("settings menu: FORUM remap removed").Count);
            Assert.Equal("setting.ForumButton", rig.Ui.TileText("btn_forum"));
            Assert.Equal(ForumIcon, rig.Ui.TileIcon("btn_forum"));
            // Both switches off: no focus-in line for the return, as before.
            Assert.Single(rig.Host.Lines("settings menu: focus-in "));
        }

        [Fact]
        public void WithoutTheRemapAWriteThatThrowsPutsTheStockForumBackWithItsIcon()
        {
            // The remap cannot be installed (no World): build 21's re-dress, and its undress when a later write fails, now with the
            // icon's slot and name put back so setup()'s own FORUM icon reaches it again.
            var rig = new Rig(look: false);
            rig.Ui.World = null;
            rig.Open();
            Assert.Equal(Perf, rig.Ui.TileText("btn_forum"));
            Assert.Equal(PerfIcon, rig.Ui.TileIcon("btn_forum"));
            Assert.Null(rig.Ui.Instance("btn_forum").Get("mc_image"));
            rig.Ui.Instance("btn_customer").ThrowsOnWrite = true;
            rig.TabAndBack("btn_audio");
            FlashObject forum = rig.Ui.Instance("btn_forum");
            Assert.Equal("setting.ForumButton", rig.Ui.TileText("btn_forum"));
            Assert.Equal(ForumIcon, rig.Ui.TileIcon("btn_forum"));
            Assert.Equal("mc_image", FakeFlashTree.IconContainer(forum).Name);
            Assert.Same(FakeFlashTree.IconContainer(forum), forum.Get("mc_image"));
            Assert.False(rig.State.TileActive);
            Assert.True(rig.Ui.Tap("btn_forum"));
            Assert.Equal(1, rig.Ui.Game.ForumCalls);
        }

        // ------------------------------------------------------------------ admin.settingsMenu.sameFrame and the breadcrumb

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SameFrameOff_ArmsNothingAndWritesNothingInTheFocusInFrame(bool steam)
        {
            var rig = new Rig(steam);
            rig.Host.SettingsMenuSameFrame = false;
            rig.Ui.OpenSettings();
            // Build 21 arms whenever the trim is on.
            Assert.Null(rig.State.SameFrame);
            Assert.Empty(rig.Ui.HostWrites.Where(w => w.StartsWith("menu_settings", StringComparison.Ordinal)));
            rig.Stay();
            Assert.EndsWith("; same frame: off (admin.settingsMenu.sameFrame off)", rig.Host.Lines("settings menu: focus-in ").Single());
            Assert.Equal("settings menu: frame " + (steam ? "1, 6" : "3, 7") + " tiles + ABOUT laid out; stock grid drawn 1 frame", rig.GridLines.Single());
        }

        private static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mcvs-sameframe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheBreadcrumbExistsOnlyFromTheFirstWorkInANativeToTwoFramesLater(bool steam)
        {
            string dir = TempDir();
            try
            {
                string marker = Path.Combine(dir, "settings-menu-same-frame.txt");
                var rig = new Rig(steam);
                rig.Host.SameFrameMarkerPaths = new[] { marker };
                var seen = new List<bool>();
                rig.Ui.OverrideNativeFunction("MoveToCamera", new Action<string, string>((a, b) => seen.Add(File.Exists(marker))));
                bool atRender = false;
                rig.Frame(() => rig.Ui.OpenSettings());
                atRender = File.Exists(marker);
                // Android: written by the dress before setup() (MoveToCamera comes after EnterSection); Steam: by the first look,
                // at setup()'s GetLanguage. Either way it is there when the focus-in frame is drawn.
                Assert.Equal(new[] { !steam }, seen);
                Assert.True(atRender);
                Assert.Contains(" focus-in 1 ", File.ReadAllText(marker));
                rig.Host.RunFrame();                         // the run's first frame: the focus-in frame's render may still run
                Assert.True(File.Exists(marker));
                rig.Host.RunFrame();                         // the frame after: that render has ended on either render thread mode
                Assert.False(File.Exists(marker));
                rig.Stay();
                // Once per process: later focus-ins go through the same natives and write nothing.
                rig.TabAndBack("btn_audio");
                Assert.Equal(new[] { !steam, false }, seen);
                Assert.False(File.Exists(marker));
                Assert.Empty(Directory.GetFiles(dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ALeftoverBreadcrumbSkipsTheStepWithOneLineAndSwitchingItBackOnRetries(bool steam)
        {
            string dir = TempDir();
            string fallback = TempDir();
            try
            {
                string marker = Path.Combine(dir, "settings-menu-same-frame.txt");
                string other = Path.Combine(fallback, "settings-menu-same-frame.txt");
                File.WriteAllText(marker, "2026-11-04T10:00:00.000Z focus-in 3 GetPlatformId\n");
                File.WriteAllText(other, "2026-11-03T10:00:00.000Z focus-in 1 EnterSection\n");
                var rig = new Rig(steam);
                rig.Host.SameFrameMarkerPaths = new[] { marker, other };
                rig.Ui.OpenSettings();
                // The previous start ended inside our work: the switch is off for this install, no step, no dress before setup().
                Assert.Null(rig.State.SameFrame);
                Assert.Equal(1, rig.Host.SameFrameSwitchedOff);
                Assert.False(rig.Host.SettingsMenuSameFrame);
                Assert.False(File.Exists(marker));
                Assert.False(File.Exists(other));
                LogEntry warn = rig.Host.Logs.Snapshot().Single(e => e.Level == LogLevel.Warn);
                Assert.Equal("settings menu: same frame skipped: the last start ended during it (marker " + marker +
                             "); admin.settingsMenu.sameFrame is now off; switch it back on to retry", warn.Message);
                rig.Stay();
                Assert.Equal("settings menu: frame " + (steam ? "1, 6" : "3, 7") + " tiles + ABOUT laid out; stock grid drawn 1 frame", rig.GridLines.Single());
                // The owner switches it back on: the next focus-in has the step again (and writes the breadcrumb once).
                rig.Host.SettingsMenuSameFrame = true;
                rig.Frame(() => Assert.True(rig.Ui.Tap("btn_audio")));
                rig.Frame(() => rig.Ui.Back());
                Assert.True(File.Exists(marker));
                rig.Stay();
                Assert.False(File.Exists(marker));
                // Build 23 on Android: the seal (F1) leaves MORE GAMES' tidy to the run's first frame.
                Assert.EndsWith(steam ? SettingsMenuReturnTests.SameFrameDone : SettingsMenuReturnTests.SealedDoneFor(rig.AboutOnMoreGames), rig.GridLines.Last());
                Assert.Single(rig.Host.Logs.Snapshot().Where(e => e.Level == LogLevel.Warn));
            }
            finally
            {
                Directory.Delete(dir, true);
                Directory.Delete(fallback, true);
            }
        }

        [Fact]
        public void ABreadcrumbThatCannotBeSavedOffKeepsTheStepOffInThisProcessAndTheMarkerStays()
        {
            string dir = TempDir();
            try
            {
                string marker = Path.Combine(dir, "settings-menu-same-frame.txt");
                File.WriteAllText(marker, "x\n");
                var rig = new Rig();
                rig.Host.SameFrameMarkerPaths = new[] { marker };
                rig.Host.CanSaveSameFrame = false;
                rig.Open();
                rig.TabAndBack("btn_audio");
                Assert.True(rig.Host.SettingsMenuSameFrame);   // the setting reads on, but the step stays off
                Assert.Null(rig.State.SameFrame);
                Assert.True(File.Exists(marker));             // so the next start skips it too
                // The key still reads on, so the line says the one way to retry.
                Assert.EndsWith("admin.settingsMenu.sameFrame could not be saved as off, so it stays off while the marker is there; delete " +
                                marker + " to retry", rig.Host.Logs.Snapshot().Single(e => e.Level == LogLevel.Warn).Message);
                Assert.EndsWith("; same frame: off (a breadcrumb of the last start is still there)", rig.Host.Lines("settings menu: focus-in ").Last());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ABreadcrumbThatCannotBeWrittenIsSaidOnceAndChangesNothing()
        {
            string dir = TempDir();
            try
            {
                var rig = new Rig();
                rig.Host.SameFrameMarkerPaths = new[] { Path.Combine(dir, "missing", "settings-menu-same-frame.txt") };
                rig.Open();
                rig.TabAndBack("btn_audio");
                List<string> lines = rig.Host.Lines("settings menu: focus-in ");
                Assert.EndsWith("; breadcrumb not written (DirectoryNotFoundException)", lines[0]);
                Assert.DoesNotContain("breadcrumb", lines[1]);
                Assert.All(rig.GridLines, l => Assert.EndsWith(SettingsMenuReturnTests.SealedDoneFor(rig.AboutOnMoreGames), l));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void TheInMatchPanelWritesNoBreadcrumbAndNothingBeforeSetup()
        {
            // Regression check (build 21 does nothing on the pause panel either).
            string dir = TempDir();
            try
            {
                var rig = new Rig();
                rig.Host.SameFrameMarkerPaths = new[] { Path.Combine(dir, "settings-menu-same-frame.txt") };
                rig.Ui.InGame = true;
                rig.Ui.OpenSettings();
                Assert.Empty(Directory.GetFiles(dir));
                Assert.Empty(rig.Ui.HostWrites.Where(w => w.StartsWith("menu_settings", StringComparison.Ordinal)));
                rig.Stay();
                Assert.Empty(rig.GridLines);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // ------------------------------------------------------------------ review of build 22 (each fails on 05a2028's code)

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void PerformanceOffThenOnInTheSameSettings_TheTileIsDrawnAndTappableAgain(bool sameFrame)
        {
            // The admin page sits inside Settings (ACCOUNT > MCVS Encore), so menu_settings and its instances stay while the owner
            // switches PERFORMANCE off and on again. 05a2028: btn_forum is kept again with only x and y written, so it stays
            // visible=false, alpha 0 and mouse off, is read back as in place, and R4 still hides the Gameplay rows.
            var rig = new Rig();
            rig.Host.SettingsMenuSameFrame = sameFrame;
            rig.Open();
            rig.Host.PerformanceSetting = false;
            rig.TabAndBack("btn_audio");
            Assert.False(rig.Ui.Tap("btn_forum"));                  // removed while PERFORMANCE is off (design §3.3)
            rig.Host.PerformanceSetting = true;
            int from = rig.TabAndBack("btn_audio");
            FlashObject forum = rig.Ui.Instance("btn_forum");
            Assert.True((bool)forum.Get("visible"));
            Assert.Equal(1f, forum.Num("alpha"));
            Assert.True((bool)forum.Get("mouseEnabled"));
            Assert.True((bool)forum.Get("mouseChildren"));
            Assert.Equal(245.4f, forum.Num("x"));
            Assert.Equal(-50.9f, forum.Num("y"));
            RenderPoint last = rig.Ui.Renders.Last();
            Assert.True(last.Instances["btn_forum"].Drawn);
            Assert.Null(Stock(last, rig.AboutOnMoreGames));
            if (sameFrame) Assert.Equal(new string[0], StockRenders(rig, from));
            Assert.True(PerformancePage.HidesGameplayRows(rig.Host, rig.State));
            rig.Frame(() => Assert.True(rig.Ui.Tap("btn_forum")));
            rig.Host.Drain();
            Assert.Equal(0, rig.Ui.Game.ForumCalls);
            Assert.Equal(McvsMenuNames.Gameplay, rig.Ui.Top.Name);
            rig.Ui.AssertAllDisposed();
        }

        [Theory]
        [InlineData(false, false)]   // no btn_forum on frame 3 (a game update)
        [InlineData(true, false)]    // Kayac: setup() goes to frame 4, which has no btn_forum
        [InlineData(false, true)]
        public void RemapOn_NoTileOnThePanel_TheGameplayRowsStay(bool kayac, bool sameFrame)
        {
            // 05a2028: the remap counted as the tile whatever the read found, so a run that never found btn_forum set no
            // RedressFailed while EnsureRemap's TileActive hid GRAPHICS SETTINGS and FRAME RATE LIMIT with no page to reach.
            var rig = new Rig();
            rig.Host.SettingsMenuSameFrame = sameFrame;
            rig.Ui.Kayac = kayac;
            if (!kayac)
            {
                rig.Ui.SettingsBuilt = menu =>
                {
                    FlashObject panel = menu.Child("mc_panel");
                    if (panel.Frame == 3 && panel.Child("btn_forum") != null) panel.Children.Remove(panel.Child("btn_forum"));
                };
            }
            rig.Open();
            rig.Host.Drain();
            Assert.True(((FakeLocalizationFormatter)rig.Ui.World.LocalizationFormatter).Contains("setting.ForumButton"));
            Assert.True(rig.State.RedressFailed);
            Assert.False(PerformancePage.HidesGameplayRows(rig.Host, rig.State));
            Assert.StartsWith("settings menu: not laid out (" + (kayac ? "frame 4" : "no btn_forum"), rig.GridLines.Single());
        }

        [Fact]
        public void RemapOn_AWidthTheRunCannotLayOut_TheTileStillCoversTheRows()
        {
            // Regression check of the other side: the instances are the timeline's, btn_forum among them, so the remapped tile
            // opens the page and RedressFailed stays clear (as in 05a2028).
            var rig = new Rig();
            rig.Ui.SettingsBuilt = menu =>
            {
                FlashObject help = menu.Child("mc_panel").Child("btn_help");
                if (help != null) help.Members["width"] = 300f;
            };
            rig.Open();
            rig.Host.Drain();
            Assert.StartsWith("settings menu: not laid out (btn_help is 300 wide", rig.GridLines.Single());
            Assert.False(rig.State.RedressFailed);
            Assert.True(PerformancePage.HidesGameplayRows(rig.Host, rig.State));
        }

        [Theory]
        [InlineData(true, true)]     // gameswf refuses the rename: the container is still mc_image, its slot null
        [InlineData(false, false)]   // the same-frame switch off: no dress before setup(), the run puts the icon back
        [InlineData(true, false)]
        public void PerformanceSwitchedOff_TheForumIconComesBack(bool refuseRename, bool sameFrame)
        {
            // 05a2028 looked the dressed icon up by its new name only, and only inside the dress before setup() (sameFrame on).
            var rig = new Rig();
            rig.Ui.RefuseIconRename = refuseRename;
            rig.Host.SettingsMenuTrim = false;
            rig.Host.SettingsMenuSameFrame = sameFrame;
            rig.Open();
            FlashObject forum = rig.Ui.Instance("btn_forum");
            Assert.Null(forum.Get("mc_image"));                     // out of setup()'s reach, either way
            Assert.Equal(refuseRename ? "mc_image" : "mcvs_perfIcon", FakeFlashTree.IconContainer(forum).Name);
            rig.Host.PerformanceSetting = false;
            rig.TabAndBack("btn_audio");
            Assert.Equal("setting.ForumButton", rig.Ui.TileText("btn_forum"));
            Assert.Equal(ForumIcon, rig.Ui.TileIcon("btn_forum"));
            Assert.Equal("mc_image", FakeFlashTree.IconContainer(forum).Name);
            Assert.Same(FakeFlashTree.IconContainer(forum), forum.Get("mc_image"));
            // The stock icon branch reaches it again: the next return's setup() dresses it itself.
            int loads = rig.Ui.IconLoads;
            rig.TabAndBack("btn_help");
            Assert.Equal(ForumIcon, rig.Ui.TileIcon("btn_forum"));
            Assert.Equal(loads, rig.Ui.IconLoads);
            if (!sameFrame) Assert.Single(rig.Host.Lines("settings menu: FORUM icon put back after setup()"));
            rig.Ui.AssertAllDisposed();
        }

        [Fact]
        public void WithoutTheRemapAndARefusedRenameAWriteThatThrowsStillPutsTheForumIconBack()
        {
            var rig = new Rig(look: false);
            rig.Ui.World = null;
            rig.Ui.RefuseIconRename = true;
            rig.Open();
            FlashObject forum = rig.Ui.Instance("btn_forum");
            Assert.Null(forum.Get("mc_image"));
            Assert.Equal(PerfIcon, rig.Ui.TileIcon("btn_forum"));
            rig.Ui.Instance("btn_customer").ThrowsOnWrite = true;
            rig.TabAndBack("btn_audio");
            Assert.Equal("setting.ForumButton", rig.Ui.TileText("btn_forum"));
            Assert.Equal(ForumIcon, rig.Ui.TileIcon("btn_forum"));
            Assert.Same(FakeFlashTree.IconContainer(forum), forum.Get("mc_image"));
            Assert.True(rig.Ui.Tap("btn_forum"));
            Assert.Equal(1, rig.Ui.Game.ForumCalls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WithTheRenderThreadTheBreadcrumbOutlivesTheFocusInFramesRender(bool steam)
        {
            // GameApplication.Render with the render thread on returns while the frame is still drawn (World.RenderAsync, :923);
            // the next Render waits for it (:900), after the next frame's context drain. 05a2028 deleted the marker in that drain,
            // so a native fault in the first draw of the dressed panel left no marker and crashed again at every start.
            string dir = TempDir();
            try
            {
                string marker = Path.Combine(dir, "settings-menu-same-frame.txt");
                var rig = new Rig(steam);
                rig.Host.SameFrameMarkerPaths = new[] { marker };
                rig.Host.RenderThread = true;
                var atRenderEnd = new Dictionary<int, bool>();
                rig.Host.RenderEnded = frame => atRenderEnd[frame] = File.Exists(marker);
                rig.Frame(() => rig.Ui.OpenSettings());
                int focusIn = rig.Host.Frames;
                rig.Host.RunFrame();
                Assert.True(atRenderEnd[focusIn], "the marker was gone before the focus-in frame's render ended");
                Assert.True(File.Exists(marker));
                rig.Host.RunFrame();
                Assert.False(File.Exists(marker));
                rig.Stay();
                Assert.Empty(Directory.GetFiles(dir));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
