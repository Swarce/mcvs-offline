using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using McvsOffline.Embedded.Admin.SettingsMenu;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// Build 23 (the owner's build-22 report on the S22; docs/design/settings-menu.md §3.2, "Build 23"):
    /// <list type="bullet">
    /// <item>P1: the PERFORMANCE title drawn in the middle of its tile. BtnSimple.setup centres <c>txt.y</c> while needAlign is true
    /// (BtnSimple.as:92-95, default true :25); our re-dress before setup() ran before MenuSettings.as:110-120 turns it off. The fake
    /// models both, and the refusals.</item>
    /// <item>P2: ABOUT right of centre. ABOUT is drawn on MORE GAMES' button at setup()'s own -177 (:212) through the remap
    /// setting.FreeGamesButton → setting.CreditButton, the real ABOUT goes off the panel, and MORE GAMES' tap opens ABOUT unless the
    /// focus says it was the top bar's Gameloft button.</item>
    /// <item>P3: the stutter. F1: no same-frame look when the dress before setup() proved it has nothing left to do; M: timings in the
    /// existing lines.</item>
    /// </list>
    /// Every test fails on build 22's SettingsMenu code (e1bb9b9) unless it says it is a regression check. The file uses no member
    /// build 22 lacks (new ones by name, by reflection), so it builds against those sources.
    /// </summary>
    public class SettingsMenuBuild23Tests
    {
        private const string Perf = "setting.Performance";
        private const string About = "setting.CreditButton";
        private const string MoreGames = "setting.FreeGamesButton";
        private const float TitleY = 294.2f;
        private const string Notes = "settings menu: focus-in ";

        private readonly ITestOutputHelper output;

        public SettingsMenuBuild23Tests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private sealed class Rig
        {
            public Rig(bool steam = false, bool about = true, bool unsealed = false, IconSlotMode icon = IconSlotMode.Slot)
            {
                Ui = new FakeFlashTree(steam) { SteamReplacesInstances = steam, IconSlot = icon };
                Host = new FakeSettingsMenuHost(Ui, steam) { AboutCentreSetting = about };
                Natives = new UiNatives(() => Host, new SettingsMenuState());
                if (unsealed)
                {
                    Natives.Steps.PreDress = (h, s) =>
                    {
                        SettingsMenuLayout.PreDress(h, s);
                        SettingsMenuReturnTests.Rig.Unseal(s);
                    };
                }
                Natives.Ensure();
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

            public string LastFocusIn { get { return Host.Lines(Notes).Last(); } }

            public void Frame(Action input)
            {
                Host.RunFrame(input);
            }

            public void Stay()
            {
                Host.Drain();
                Host.RunFrames(60);
            }

            /// <summary>The gear: a new Settings menu (a disposable menu: SwfMain.as:606, MenusStack createMenu).</summary>
            public void Open()
            {
                Frame(() => Ui.OpenSettings());
                Stay();
            }

            /// <summary>Back out of Settings (the menu is gone; the next gear tap builds a new one).</summary>
            public void Close()
            {
                Frame(() => Ui.Back());
                Stay();
            }

            public float Title(string tile)
            {
                return Ui.Instance(tile).Child("txt").Num("y");
            }

            /// <summary>The MORE GAMES remap is in the client World's remap (the fake formatter's own map, no production member).</summary>
            public bool MoreGamesRemapped
            {
                get
                {
                    var formatter = Ui.World == null ? null : Ui.World.LocalizationFormatter as FakeLocalizationFormatter;
                    return formatter != null && formatter.RemapLocId(MoreGames) == About;
                }
            }
        }

        // ------------------------------------------------------------------ P1: the PERFORMANCE title at the tile's own height

        [Theory]
        [InlineData(true)]    // the FORUM remap on: our full re-dress of a fresh panel's tile (SML WriteGrid)
        [InlineData(false)]   // no remap (no World): the re-dress, then the title-only one on later frames
        public void EveryGearTap_PerformanceTitleSitsAtTheTileHeight(bool remap)
        {
            // Build 22: txt.y 177.5 (BtnSimple centres it: our setup ran while needAlign was still true), on every gear tap.
            var rig = new Rig(about: false);
            if (!remap) rig.Ui.World = null;
            for (int tap = 0; tap < 3; tap++)
            {
                rig.Frame(() => rig.Ui.OpenSettings());
                Assert.Equal(TitleY, rig.Title("btn_forum"), 2);
                rig.Stay();
                Assert.Equal(TitleY, rig.Title("btn_forum"), 2);
                Assert.Equal(Perf, rig.Ui.TileText("btn_forum"));
                Assert.Equal(TitleY, rig.Title("btn_help"), 2);   // the stock tiles: setup() turned needAlign off first
                rig.Close();
            }
            Assert.Empty(rig.Ui.HostWrites.Where(w => w.Contains(".txt.y")));   // needAlign did it: no repair needed
            Assert.Contains("needAlign off, title y 294.2", rig.Host.Lines(Notes).First());
        }

        [Fact]
        public void TheStockForumPutBackAfterAFailedWrite_KeepsItsTitleAtTheTileHeight()
        {
            // Build 22: Undress's stock setup with needAlign true centres FORUM's title too.
            var rig = new Rig(about: false);
            rig.Ui.World = null;
            rig.Ui.SettingsBuilt = menu => menu.Child("mc_panel").Child("btn_social").ThrowsOnWrite = true;
            rig.Frame(() => rig.Ui.OpenSettings());
            Assert.Equal("setting.ForumButton", rig.Ui.TileText("btn_forum"));
            Assert.Equal(TitleY, rig.Title("btn_forum"), 2);
            rig.Stay();
            Assert.Equal(TitleY, rig.Title("btn_forum"), 2);
        }

        [Theory]
        [InlineData(1, "needAlign refused: InvalidOperationException: probe: needAlign write refused")]   // WriteMode.Throws
        [InlineData(2, "needAlign not taken (reads true)")]                                               // WriteMode.Ignored
        public void ANeedAlignThatDoesNotTake_TheCentredTitleIsPutBack(int mode, string why)
        {
            var rig = new Rig(about: false);
            rig.Ui.NeedAlignWrite = (WriteMode)mode;
            rig.Open();
            Assert.Equal(TitleY, rig.Title("btn_forum"), 2);
            string line = rig.Host.Lines(Notes).Single();
            Assert.Contains("title y put back (177.5", line);
            Assert.Contains(why, line);
            Assert.Contains(rig.Ui.HostWrites, w => w == "menu_settings.mc_panel.btn_forum.txt.y=294.2");
        }

        [Fact]
        public void NeitherWriteTakes_OneLineOnly_AndEverythingElseIsBuild22()
        {
            var plain = new Rig(about: false);
            var rig = new Rig(about: false);
            rig.Ui.NeedAlignWrite = WriteMode.Throws;
            rig.Ui.TitleYWrite = WriteMode.Throws;
            foreach (Rig r in new[] { plain, rig })
            {
                for (int tap = 0; tap < 3; tap++)
                {
                    r.Open();
                    if (tap < 2) r.Close();
                }
            }
            Assert.Equal(new[] { "settings menu: PERFORMANCE title y not put back (InvalidOperationException: probe: txt.y write refused); " +
                                 "the title stays centred as in build 22" }, rig.Host.Lines("settings menu: PERFORMANCE title"));
            Assert.Contains("title y not put back (", rig.Host.Lines(Notes).First());
            Assert.True(rig.Title("btn_forum") < 260);
            // The rest is build 22's: the tile reads and opens PERFORMANCE, nothing failed, no extra layout.
            Assert.Null(rig.Ui.Instance("btn_forum").Get("mc_image"));
            Assert.Equal(Perf, rig.Ui.TileText("btn_forum"));
            Assert.False(rig.State.RedressFailed);
            Assert.Equal(plain.GridLines, rig.GridLines);
            Assert.Equal(plain.State.Layouts, rig.State.Layouts);
            rig.Frame(() => Assert.True(rig.Ui.Tap("btn_forum")));
            rig.Host.RunFrames(2);
            Assert.Equal(McvsMenuNames.Gameplay, rig.Ui.Pushed.Last());
        }

        [Fact]
        public void Regression_AnAnimatedTitleOnAReturnIsNotWrittenOver()
        {
            // Regression check (build 22 passes): the timeline's pressed and focus states put txt at 290.7; nothing writes over it.
            var rig = new Rig(about: false);
            rig.Open();
            rig.Ui.Instance("btn_forum").Child("txt").Members["y"] = 290.7f;
            rig.Ui.Instance("btn_help").Child("txt").Members["y"] = 290.7f;
            int from = rig.Ui.HostWrites.Count;
            rig.Frame(() => Assert.True(rig.Ui.Tap("btn_audio")));
            rig.Host.RunFrames(5);
            rig.Frame(() => rig.Ui.Back());
            rig.Stay();
            Assert.Empty(rig.Ui.HostWrites.Skip(from).Where(w => w.Contains(".txt.y")));
            Assert.Equal(290.7f, rig.Title("btn_forum"), 2);
        }

        [Fact]
        public void Regression_SteamReadsAndWritesNoTitle()
        {
            var rig = new Rig(steam: true);
            rig.Open();
            rig.Frame(() => Assert.True(rig.Ui.Tap("btn_audio")));
            rig.Frame(() => rig.Ui.Back());
            rig.Stay();
            Assert.Empty(rig.Ui.HostWrites.Where(w => w.Contains("needAlign") || w.Contains(".txt")));
            Assert.Empty(rig.Ui.HostReads.Where(w => w.Contains("needAlign") || w.Contains(".txt")));
        }

        // ------------------------------------------------------------------ P2: ABOUT on MORE GAMES' button

        /// <summary>The ThinBtns a render of Android's main panel drew, and any that takes taps without being drawn.</summary>
        private static List<string> Bottom(RenderPoint render)
        {
            var row = new List<string>();
            foreach (string name in new[] { "btn_more", "btn_credits", "btn_customer" })
            {
                RenderedInstance item;
                if (!render.Instances.TryGetValue(name, out item)) continue;
                if (item.Drawn) row.Add(name + " (" + item.X + ", " + item.Y + ") " + item.Text);
                else if (item.TakesTaps) row.Add(name + " takes taps");
            }
            return row;
        }

        [Fact]
        public void GearTapsAndReturns_OneAboutDrawnAtSetupsCentre()
        {
            // Build 22: ABOUT at (208, 354.1). Renders also sampled between the dress and setup() (never a real render point:
            // what setup()'s writes start from).
            var rig = new Rig();
            rig.Natives.Steps.PreDress = (h, s) =>
            {
                SettingsMenuLayout.PreDress(h, s);
                rig.Ui.Render(-1);
            };
            for (int tap = 0; tap < 5; tap++)
            {
                rig.Open();
                if (tap == 2)
                {
                    for (int back = 0; back < 2; back++)
                    {
                        rig.Frame(() => Assert.True(rig.Ui.Tap("btn_more")));
                        rig.Host.RunFrames(5);
                        Assert.Equal("menu_about", rig.Ui.Top.Name);
                        rig.Frame(() => rig.Ui.Back());
                        rig.Stay();
                    }
                }
                rig.Close();
            }
            List<RenderPoint> panels = rig.Ui.Renders.Where(r => r.Top == McvsMenuNames.Settings && r.PanelFrame == 3).ToList();
            Assert.True(panels.Count > 300);
            foreach (RenderPoint r in panels)
            {
                List<string> row = Bottom(r);
                Assert.True(row.Count == 1 && row[0].StartsWith("btn_more (-177, 354.1) ", StringComparison.Ordinal), r + ": " + string.Join("; ", row));
                Assert.NotEqual(MoreGames, r.Instances["btn_more"].Text);
                if (r.Frame > 0) Assert.Equal(About, r.Instances["btn_more"].Text);
            }
            Assert.Equal(7, rig.Host.Lines(Notes).Count(l => l.Contains("ABOUT on MORE GAMES (-177), ABOUT tile off the panel")));
            Assert.DoesNotContain(rig.Host.Lines("settings menu: "), l => l.Contains("wide") || l.Contains("not laid out"));
            Assert.Single(rig.Host.Lines("settings menu: MORE GAMES remap installed"));
            Assert.Equal(2, rig.Ui.Pushed.Count(p => p == "menu_about"));
            Assert.Equal(0, rig.Ui.Game.FreeGamesCalls);
            Assert.Equal(2, rig.Host.Lines("settings menu: ABOUT opened from MORE GAMES (focus btn_more)").Count);
            rig.Ui.AssertAllDisposed();
        }

        [Fact]
        public void TheTopBarsGameloftButton_StillGoesToTheGame()
        {
            var rig = new Rig();
            rig.Open();
            rig.Frame(() => rig.Ui.TapGameloftButton());
            rig.Host.RunFrames(3);
            Assert.Equal(1, rig.Ui.Game.FreeGamesCalls);
            Assert.DoesNotContain("menu_about", rig.Ui.Pushed);
            Assert.Single(rig.Host.Lines("settings menu: MORE GAMES tap passed to the game (focus on btn_gameLoft)"));
        }

        [Theory]
        [InlineData(1)]   // FocusReadMode.Throws
        [InlineData(2)]   // FocusReadMode.Function
        public void AnUnreadableFocus_AboutStillOpensWhileSettingsIsOnTop_OneLine(int mode)
        {
            var rig = new Rig();
            rig.Ui.FocusRead = (FocusReadMode)mode;
            rig.Open();
            for (int tap = 0; tap < 2; tap++)
            {
                rig.Frame(() => Assert.True(rig.Ui.Tap("btn_more")));
                rig.Host.RunFrames(3);
                Assert.Equal("menu_about", rig.Ui.Top.Name);
                rig.Frame(() => rig.Ui.Back());
                rig.Stay();
            }
            Assert.Single(rig.Host.Lines("settings menu: focus unreadable ("));
            Assert.Equal(2, rig.Host.Lines("settings menu: ABOUT opened from MORE GAMES (focus unreadable: ").Count);
            Assert.Equal(0, rig.Ui.Game.FreeGamesCalls);
        }

        [Fact]
        public void ADoubleTap_OnePush()
        {
            var rig = new Rig();
            rig.Open();
            rig.Frame(() =>
            {
                Assert.True(rig.Ui.Tap("btn_more"));
                Assert.True(rig.Ui.Tap("btn_more"));
            });
            rig.Host.RunFrames(3);
            Assert.Single(rig.Ui.Pushed.Where(p => p == "menu_about"));
            Assert.Single(rig.Host.Lines("settings menu: ABOUT not opened (the menu on top is menu_about)"));
        }

        [Theory]
        [InlineData(true)]    // btn_credits.y ignores our write: the read-back fails
        [InlineData(false)]   // btn_credits.alpha 0 refused: the dress fails
        public void TheAboutDressCannotBeDone_Build22sBottomRowAndNoRemap(bool readBack)
        {
            var rig = new Rig();
            rig.Ui.SettingsBuilt = menu =>
            {
                FlashObject credits = menu.Child("mc_panel").Child("btn_credits");
                if (credits == null) return;
                if (readBack) credits.Setters["y"] = v => { };
                else credits.Setters["alpha"] = v => { if (FakeFlashTree.ToFloat(v) == 0) throw new InvalidOperationException("probe: alpha refused"); credits.Members["alpha"] = v; };
            };
            rig.Open();
            Assert.False(rig.MoreGamesRemapped);
            Assert.Contains(readBack ? "ABOUT 208 (read-back btn_credits not off the panel)" : "ABOUT 208 (dress refused: InvalidOperationException: probe: alpha refused)",
                            rig.LastFocusIn);
            RenderPoint last = rig.Ui.Renders.Last();
            Assert.Equal(new[] { "btn_credits (208, 354.1) " + About }, Bottom(last));
            Assert.False(rig.State.RedressFailed);
            rig.Frame(() => Assert.True(rig.Ui.Tap("btn_credits")));
            Assert.Equal("menu_about", rig.Ui.Top.Name);
        }

        [Fact]
        public void TwoAboutsDrawn_BothOpenAbout()
        {
            // The remap in and the real ABOUT back on the panel (an undo that did not take): every visible button is ABOUT.
            var rig = new Rig();
            rig.Open();
            FlashObject credits = rig.Ui.Instance("btn_credits");
            credits.Members["visible"] = true;
            credits.Members["mouseEnabled"] = true;
            credits.Members["alpha"] = 1f;
            credits.Members["y"] = 354.1f;
            rig.Frame(() => Assert.True(rig.Ui.Tap("btn_credits")));
            Assert.Equal("menu_about", rig.Ui.Top.Name);
            rig.Frame(() => rig.Ui.Back());
            rig.Stay();
            rig.Frame(() => Assert.True(rig.Ui.Tap("btn_more")));
            rig.Host.RunFrames(3);
            Assert.Equal("menu_about", rig.Ui.Top.Name);
            Assert.Equal(0, rig.Ui.Game.FreeGamesCalls);
        }

        [Fact]
        public void TheStockGoToFreeGamesBackFromOnStart_IsRepairedAtTheFocusIn()
        {
            var rig = new Rig();
            // UIComponent.OnStart's AddBrowserOverrides (default flags: the list replaced by the game's own).
            rig.Ui.OverrideNativeFunction("GoToFreeGames", new Action(rig.Ui.GoToFreeGames));
            rig.Open();
            Assert.Contains("ABOUT on MORE GAMES (-177)", rig.LastFocusIn);
            rig.Frame(() => Assert.True(rig.Ui.Tap("btn_more")));
            rig.Host.RunFrames(3);
            Assert.Equal("menu_about", rig.Ui.Top.Name);
            Assert.Equal(0, rig.Ui.Game.FreeGamesCalls);
        }

        [Fact]
        public void Regression_KayacAndTheMatchPanel_NoDressAndTheGamesTap()
        {
            // Kayac: setup()'s gotoAndStop(4) places new, stock instances after the dress (as in build 22); MORE GAMES stays hidden
            // by setup() (:210), so nothing reads ABOUT twice and the real ABOUT is the game's.
            var kayac = new Rig();
            kayac.Ui.Kayac = true;
            kayac.Open();
            Assert.Equal(4, kayac.Ui.Panel.Frame);
            Assert.False((bool)kayac.Ui.Instance("btn_more").Get("visible"));
            kayac.Frame(() => Assert.True(kayac.Ui.Tap("btn_credits")));
            Assert.Equal("menu_about", kayac.Ui.Top.Name);
            var match = new Rig();
            match.Ui.InGame = true;
            match.Open();
            Assert.False(match.MoreGamesRemapped);
            match.Frame(() => match.Ui.TapGameloftButton());
            Assert.Equal(1, match.Ui.Game.FreeGamesCalls);
        }

        [Fact]
        public void ALanguageReload_StillReadsAbout()
        {
            var rig = new Rig();
            rig.Open();
            rig.Ui.ReloadStringTable();
            Assert.Equal(About, rig.Ui.TileText("btn_more"));
            Assert.Equal(Perf, rig.Ui.TileText("btn_forum"));
        }

        [Theory]
        [InlineData(true)]    // admin.settingsMenu.aboutCentre off
        [InlineData(false)]   // admin.settingsMenu.sameFrame off (nothing before setup())
        public void Regression_SwitchOffOrNoWorkInNatives_IsBuild22(bool switchOff)
        {
            var rig = new Rig(about: !switchOff);
            if (!switchOff) rig.Host.SettingsMenuSameFrame = false;
            rig.Open();
            rig.Close();
            rig.Open();
            Assert.False(rig.MoreGamesRemapped);
            Assert.Empty(rig.Host.Lines("settings menu: MORE GAMES remap"));
            Assert.Equal(new[] { "btn_credits (208, 354.1) " + About }, Bottom(rig.Ui.Renders.Last()));
            if (switchOff) Assert.Contains("MORE GAMES off the panel, ABOUT 208", rig.LastFocusIn);
            rig.Frame(() => rig.Ui.TapGameloftButton());
            Assert.Equal(1, rig.Ui.Game.FreeGamesCalls);
        }

        [Fact]
        public void Regression_Steam_NoRedirectNoRemap_AboutAtThePlateCentre()
        {
            var rig = new Rig(steam: true);
            rig.Open();
            Assert.False(rig.MoreGamesRemapped);
            Assert.All(rig.Ui.DelegatesOf("GoToFreeGames"), d => Assert.Same(rig.Ui, d.Target));
            Assert.Equal(-182.9f, rig.Ui.Instance("btn_credits").Num("x"), 1);
            Assert.Single(rig.Host.Lines("settings menu: natives registered (EnterSection) on"));
        }

        // ------------------------------------------------------------------ P3/F1: no look when the dress proved it needless

        [Fact]
        public void Sealed_NoObserverInTheFocusInFrame_FewerFlashCalls_TapsRight()
        {
            var rig = new Rig();
            var arm = new Rig(unsealed: true);
            int before = rig.Ui.Handed.Count;
            rig.Ui.OpenSettings();
            int sealedOps = rig.Ui.Handed.Count - before;
            Assert.Single(rig.Ui.DelegatesOf("GetPlatformId"));   // the game's only
            before = arm.Ui.Handed.Count;
            arm.Ui.OpenSettings();
            int armOps = arm.Ui.Handed.Count - before;
            Assert.Equal(2, arm.Ui.DelegatesOf("GetPlatformId").Count);
            output.WriteLine("Flash handles in the focus-in frame: sealed " + sealedOps + ", with the look " + armOps);
            Assert.True(sealedOps * 2 < armOps, sealedOps + " vs " + armOps);   // the fake: 195 vs 696
            rig.Stay();
            Assert.EndsWith("; same frame: not needed (dressed before setup)", rig.LastFocusIn);
            Assert.Equal(new[] { "settings menu: frame 3, 7 tiles + ABOUT laid out in the focus-in frame; stock grid drawn 0 frames" }, rig.GridLines);
            rig.Frame(() => Assert.True(rig.Ui.Tap("btn_more")));
            rig.Host.RunFrames(3);
            Assert.Equal("menu_about", rig.Ui.Top.Name);
            rig.Frame(() => rig.Ui.Back());
            rig.Stay();
            rig.Frame(() => Assert.True(rig.Ui.Tap("btn_forum")));
            rig.Host.RunFrames(3);
            Assert.Equal(McvsMenuNames.Gameplay, rig.Ui.Top.Name);
        }

        [Theory]
        [InlineData(true)]    // the icon slot cannot be emptied: setup()'s FORUM icon must be re-dressed after it
        [InlineData(false)]   // ABOUT's dress fell back to build 22's
        public void NotSealed_Build22sLook(bool icon)
        {
            var rig = new Rig(icon: icon ? IconSlotMode.Stuck : IconSlotMode.Slot);
            if (!icon)
            {
                rig.Ui.SettingsBuilt = menu =>
                {
                    FlashObject credits = menu.Child("mc_panel").Child("btn_credits");
                    if (credits != null) credits.Setters["y"] = v => { };
                };
            }
            rig.Ui.OpenSettings();
            Assert.Equal(2, rig.Ui.DelegatesOf("GetPlatformId").Count);
            rig.Stay();
            Assert.Contains("; same frame: laid out (GetPlatformId calls ", rig.LastFocusIn);
            Assert.Contains(icon ? "(not skipped: icon within setup()'s reach)" : "(not skipped: ABOUT 208 (read-back btn_credits not off the panel))",
                            rig.LastFocusIn);
            Assert.Equal(Perf, rig.Ui.TileText("btn_forum"));
            Assert.Equal(SettingsMenuNames.PerformanceIcon, rig.Ui.TileIcon("btn_forum"));
        }

        [Fact]
        public void Regression_SteamKeepsItsLook()
        {
            var rig = new Rig(steam: true);
            rig.Ui.OpenSettings();
            Assert.Equal(2, rig.Ui.DelegatesOf("GetLanguage").Count);
            rig.Stay();
            Assert.Contains("; same frame: laid out (GetLanguage calls ", rig.LastFocusIn);
        }

        // ------------------------------------------------------------------ P3/M: the timings in the lines

        /// <summary>Gives the state build 23's clock over <paramref name="read"/> (by name); false on build 22's code.</summary>
        private static bool SetClock(SettingsMenuState state, Func<double> read)
        {
            Assembly code = typeof(SettingsMenuState).Assembly;
            Type clock = code.GetType("McvsOffline.Embedded.Admin.SettingsMenu.SettingsMenuClock");
            MethodInfo of = clock == null ? null : clock.GetMethod("Of", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            PropertyInfo slot = typeof(SettingsMenuState).GetProperty("Clock", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (of == null || slot == null) return false;
            slot.SetValue(state, of.Invoke(null, new object[] { read }));
            return true;
        }

        [Fact]
        public void Timings_TheFocusInLineSaysWhatEachStepTook()
        {
            var rig = new Rig();
            double now = 1000;
            Assert.True(SetClock(rig.State, () => now), "no timings (build 22)");
            rig.Host.FrameDelta = () => 41.04;
            Action<ISettingsMenuHost, SettingsMenuState> remap = rig.Natives.Steps.Remap;
            Action<ISettingsMenuHost, SettingsMenuState> dress = rig.Natives.Steps.PreDress;
            rig.Natives.Steps.Remap = (h, s) => { remap(h, s); now += 0.1; };
            rig.Natives.Steps.PreDress = (h, s) => { dress(h, s); now += 3.6; };
            rig.Open();
            rig.Close();
            rig.Open();
            List<string> lines = rig.Host.Lines(Notes);
            const string Ms = "; ms: EnterSection 3.7 (page 0.0, remap 0.1, grid 0.0, before setup 3.6, prompt 0.0), same frame 0.0; " +
                              "focus-in frame dt 41.0; to the run's first frame 0.0";
            Assert.EndsWith("; same frame: not needed (dressed before setup)" + Ms + "; first in this process", lines[0]);
            Assert.EndsWith("; same frame: not needed (dressed before setup)" + Ms, lines[1]);
            Assert.All(rig.GridLines, l => Assert.Contains(" laid out in the focus-in frame; stock grid drawn 0 frames; ours 0.0 ms over ", l));
        }

        [Fact]
        public void Timings_AClockThatFails_OneLineAndBuild22Text()
        {
            var rig = new Rig();
            int calls = 0;
            Assert.True(SetClock(rig.State, () => { if (calls++ > 0) throw new InvalidOperationException("clock gone"); return 0; }), "no timings (build 22)");
            rig.Open();
            rig.Close();
            rig.Open();
            Assert.Equal(new[] { "settings menu: timings unavailable (InvalidOperationException: clock gone)" }, rig.Host.Lines("settings menu: timings"));
            Assert.All(rig.Host.Lines(Notes), l => Assert.DoesNotContain("; ms:", l));
            Assert.All(rig.GridLines, l => Assert.EndsWith(" in the focus-in frame; stock grid drawn 0 frames", l));
        }

        [Fact]
        public void Timings_BothSwitchesOff_OneLineAndNoFlashWrite()
        {
            var rig = new Rig();
            Assert.True(SetClock(rig.State, () => 5), "no timings (build 22)");
            rig.Host.SettingsMenuTrim = false;
            rig.Host.PerformanceSetting = false;
            rig.Host.FrameDelta = () => 16.7;
            rig.Open();
            Assert.Equal(new[] { "settings menu: focus-in 1 timings (stock): focus-in frame dt 16.7, to the run's first frame 0.0 ms" },
                         rig.Host.Lines(Notes));
            Assert.Empty(rig.Ui.HostWrites.Where(w => !w.Contains("Native")));
        }
    }
}
