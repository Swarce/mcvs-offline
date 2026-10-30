using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Embedded.Admin.SettingsMenu;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// WP-B (docs/design/settings-menu.md §1.1, §1.2, §3.2, §4.1 T-B1..T-B7; skeptic A2): the main-menu Settings grid without SOCIAL
    /// MEDIA, FORUM, CUSTOMER CARE and MORE GAMES, and Android's btn_forum re-dressed as PERFORMANCE, on the fake panels of both SWFs.
    /// The step is posted from <c>EnterSection("settings_main_screen")</c> as UiNatives posts it (here a plain appended delegate), so it
    /// runs after the stock <c>setup()</c>, at the next <see cref="FakeSettingsMenuHost.Drain"/>.
    /// </summary>
    public class SettingsMenuLayoutTests
    {
        private const string Forum = "menu_settings.mc_panel.btn_forum";

        private const string PerformanceSetup =
            Forum + ".setup({title:setting.Performance,icon:Menus/assets/png/icons/settings_performance.png})";

        private sealed class Rig
        {
            public Rig(bool steam)
            {
                Host = new FakeSettingsMenuHost(windows: steam);
                State = new SettingsMenuState();
                SettingsMenuState state = State;
                FakeSettingsMenuHost host = Host;
                // What UiNatives' EnterSection does for the grid: clear the tile, post the step.
                Ui.OverrideNativeFunction("EnterSection", new Action<string>(section =>
                {
                    if (section != SettingsMenuNames.MainSettingsSection) return;
                    state.TileActive = false;
                    SettingsMenuLayout.Schedule(host, state);
                }), false);
            }

            public FakeSettingsMenuHost Host { get; private set; }

            public SettingsMenuState State { get; private set; }

            public FakeFlashTree Ui { get { return Host.Ui; } }

            public List<string> Lines { get { return Host.Lines("settings menu"); } }

            /// <summary>Opens Settings and runs the posted step (the next frame).</summary>
            public FlashObject Open()
            {
                FlashObject menu = Ui.OpenSettings();
                Host.Drain();
                return menu;
            }

            public float[] At(string name)
            {
                FlashObject item = Ui.Instance(name);
                return new[] { item.Num("x"), item.Num("y") };
            }

            /// <summary>Nothing went through a handle, nothing was logged as laid out.</summary>
            public void AssertNoWrites()
            {
                Assert.Empty(Ui.HostWrites);
                Assert.Equal(0, State.Layouts);
                Assert.False(State.TileActive);
            }

            public void AssertClean()
            {
                Ui.AssertAllDisposed();
                Assert.Empty(Ui.ManagedWrites);
                Assert.Equal(0, Ui.GenericCalls);
            }
        }

        // ------------------------------------------------------------------ T-B1: the plans

        private static void AssertPlacement(SettingsMenuLayout.Placement p, string name, double x, double y)
        {
            Assert.Equal(name, p.Name);
            Assert.Equal(x, p.X, 2);
            Assert.Equal(y, p.Y, 2);
        }

        [Fact]
        public void TheSteamPlanIsThreePlusThreeCentredWithAboutAlone()
        {
            SettingsMenuLayout.LayoutPlan plan = SettingsMenuLayout.Plan(true);
            Assert.Equal(1, plan.Frame);
            Assert.False(plan.RedressForum);
            Assert.Equal(7, plan.Kept.Count);
            AssertPlacement(plan.Kept[0], "btn_general", -715.6, -427.9);
            AssertPlacement(plan.Kept[1], "btn_audio", -232.7, -427.9);
            AssertPlacement(plan.Kept[2], "btn_account", 250.2, -427.9);
            AssertPlacement(plan.Kept[3], "btn_help", -715.6, -50.9);
            AssertPlacement(plan.Kept[4], "btn_language", -232.7, -50.9);
            AssertPlacement(plan.Kept[5], "btn_performance", 250.2, -50.9);
            AssertPlacement(plan.Kept[6], "btn_credits", -182.9, 354.1);
            Assert.Equal(new[] { "btn_social", "btn_forum", "btn_customer" }, plan.Hidden);
            Assert.Equal(10, plan.Lookups.Count);
            // The performance switch is Android's: Steam keeps its own tile and page.
            SettingsMenuLayout.LayoutPlan off = SettingsMenuLayout.Plan(true, false);
            Assert.Equal(plan.Kept.Select(p => p.ToString()), off.Kept.Select(p => p.ToString()));
            Assert.Equal(plan.Hidden, off.Hidden);
            AssertGeometry(plan, true);
        }

        [Fact]
        public void TheAndroidPlanIsFourPlusThreeWithPerformanceLastAndAboutAlone()
        {
            SettingsMenuLayout.LayoutPlan plan = SettingsMenuLayout.Plan(false);
            Assert.Equal(3, plan.Frame);
            Assert.True(plan.RedressForum);
            Assert.Equal(8, plan.Kept.Count);
            AssertPlacement(plan.Kept[0], "btn_general", -948, -427.9);
            AssertPlacement(plan.Kept[1], "btn_audio", -471.05, -427.9);
            AssertPlacement(plan.Kept[2], "btn_customize", 6.85, -427.9);
            AssertPlacement(plan.Kept[3], "btn_account", 484, -427.9);
            AssertPlacement(plan.Kept[4], "btn_help", -709.4, -50.9);
            AssertPlacement(plan.Kept[5], "btn_language", -232.0, -50.9);
            AssertPlacement(plan.Kept[6], "btn_forum", 245.4, -50.9);   // re-dressed as PERFORMANCE
            AssertPlacement(plan.Kept[7], "btn_credits", -182.9, 354.1);
            Assert.Equal(new[] { "btn_social", "btn_customer", "btn_more" }, plan.Hidden);
            Assert.Equal(11, plan.Lookups.Count);
            AssertGeometry(plan, false);
        }

        [Fact]
        public void WithoutPerformanceAndroidHidesForumAndCentresHelpAndLanguages()
        {
            SettingsMenuLayout.LayoutPlan plan = SettingsMenuLayout.Plan(false, false);
            Assert.False(plan.RedressForum);
            Assert.Equal(new[] { "btn_general", "btn_audio", "btn_customize", "btn_account", "btn_help", "btn_language", "btn_credits" },
                         plan.Kept.Select(p => p.Name));
            AssertPlacement(plan.Kept[4], "btn_help", -471.05, -50.9);
            AssertPlacement(plan.Kept[5], "btn_language", 6.85, -50.9);
            Assert.Equal(new[] { "btn_social", "btn_forum", "btn_customer", "btn_more" }, plan.Hidden);
            AssertGeometry(plan, false);
        }

        /// <summary>
        /// Every row is centred on panel x 0 (±1), ABOUT's plate is centred, no two tiles overlap, every looked-up name is an instance
        /// of the platform's main frame (from the timeline), and nothing is both kept and hidden.
        /// </summary>
        private static void AssertGeometry(SettingsMenuLayout.LayoutPlan plan, bool steam)
        {
            IList<string> timeline = FakeFlashTree.FrameInstances(steam, plan.Frame);
            foreach (string name in plan.Lookups) Assert.Contains(name, timeline);
            Assert.Equal(plan.Lookups.Count, plan.Lookups.Distinct().Count());
            List<SettingsMenuLayout.Placement> tiles = plan.Kept.Where(SettingsMenuLayout.IsTile).ToList();
            foreach (IGrouping<double, SettingsMenuLayout.Placement> row in tiles.GroupBy(p => p.Y))
            {
                double left = row.Min(p => p.X), right = row.Max(p => p.X) + SettingsMenuNames.TileWidth;
                Assert.InRange((left + right) / 2, -1, 1);
                List<double> xs = row.Select(p => p.X).OrderBy(x => x).ToList();
                for (int i = 1; i < xs.Count; i++) Assert.True(xs[i] - xs[i - 1] > SettingsMenuNames.TileWidth, "tiles overlap in row " + row.Key);
            }
            SettingsMenuLayout.Placement about = plan.Kept.Single(p => !SettingsMenuLayout.IsTile(p));
            Assert.Equal("btn_credits", about.Name);
            Assert.Equal(0, about.X + SettingsMenuNames.ThinButtonPlateWidth / 2, 2);
        }

        // ------------------------------------------------------------------ the live step

        [Fact]
        public void AndroidLaysOutTheGridAndReDressesForumAsPerformance()
        {
            var rig = new Rig(false);
            rig.Open();
            Assert.Equal(new[] { "settings menu: frame 3, 7 tiles + ABOUT laid out" }, rig.Lines);
            foreach (SettingsMenuLayout.Placement p in SettingsMenuLayout.Plan(false).Kept)
            {
                Assert.Equal((float)p.X, rig.At(p.Name)[0]);
                Assert.Equal((float)p.Y, rig.At(p.Name)[1]);
            }
            FlashObject forum = rig.Ui.Instance("btn_forum");
            Assert.Equal("setting.Performance", forum.Get("title"));
            Assert.Equal("Menus/assets/png/icons/settings_performance.png", forum.Get("icon"));
            Assert.Equal("setting.Performance", forum.Child("txt").Get("text"));
            Assert.Equal(PerformanceSetup, rig.Ui.HostWrites.First());   // the re-dress runs first
            Assert.True(rig.State.TileActive);
            Assert.True(rig.State.TileEverActive);
            Assert.False(rig.State.RedressFailed);
            Assert.Equal(1, rig.State.Layouts);
            rig.AssertClean();
            // The kept tiles still take taps; the PERFORMANCE tile is the FORUM tile's (GoToForum, which UiNatives takes over).
            Assert.True(rig.Ui.Tap("btn_forum"));
            Assert.Equal(1, rig.Ui.Game.ForumCalls);
        }

        [Fact]
        public void SteamLaysOutTheGridAndKeepsItsOwnPerformanceTile()
        {
            var rig = new Rig(true);
            rig.Host.PerformanceSetting = true;   // Android's switch: no effect on PC
            rig.Open();
            Assert.Equal(new[] { "settings menu: frame 1, 6 tiles + ABOUT laid out" }, rig.Lines);
            foreach (SettingsMenuLayout.Placement p in SettingsMenuLayout.Plan(true).Kept)
            {
                Assert.Equal((float)p.X, rig.At(p.Name)[0]);
                Assert.Equal((float)p.Y, rig.At(p.Name)[1]);
            }
            Assert.DoesNotContain(rig.Ui.HostWrites, w => w.Contains(".setup("));
            Assert.Equal("settingWindows.PerformanceTitle", rig.Ui.Instance("btn_performance").Get("title"));
            Assert.True((bool)rig.Ui.Instance("btn_performance").Get("visible"));
            Assert.False(rig.State.TileActive);
            Assert.False(rig.State.RedressFailed);
            rig.AssertClean();
            Assert.True(rig.Ui.Tap("btn_performance"));
            Assert.Equal("menu_gameplaySettings", rig.Ui.Top.Name);
        }

        // ------------------------------------------------------------------ T-B2: one missing lookup, zero writes

        public static IEnumerable<object[]> EveryLookup()
        {
            foreach (bool steam in new[] { true, false })
            {
                yield return new object[] { steam, "mc_panel" };
                foreach (string name in SettingsMenuLayout.Plan(steam).Lookups) yield return new object[] { steam, name };
            }
        }

        [Theory]
        [MemberData(nameof(EveryLookup))]
        public void AnyMissingInstanceMeansNoWriteAtAll(bool steam, string name)
        {
            var rig = new Rig(steam);
            FlashObject menu = rig.Ui.OpenSettings();
            FlashObject panel = menu.Child("mc_panel");
            if (name == "mc_panel") menu.Children.Remove(panel);
            else panel.Children.Remove(panel.Child(name));
            rig.Host.Drain();
            rig.AssertNoWrites();
            Assert.Equal(new[] { "settings menu: not laid out (no " + name + ")" }, rig.Lines);
            // Android: the tile stays FORUM, so R4 must leave the two rows in Gameplay Settings.
            Assert.Equal(!steam, rig.State.RedressFailed);
            rig.AssertClean();
        }

        [Fact]
        public void TheTwelveAndThirteenLookupsOfTheDesignAreCovered()
        {
            // The menu on top (T-B3) and mc_panel, plus every instance of the plan.
            Assert.Equal(12, 2 + SettingsMenuLayout.Plan(true).Lookups.Count);
            Assert.Equal(13, 2 + SettingsMenuLayout.Plan(false).Lookups.Count);
            Assert.Equal((12 - 1) + (13 - 1), EveryLookup().Count());
        }

        // ------------------------------------------------------------------ T-B3: other frames, other menus, other widths

        [Theory]
        [InlineData(false, 1)]
        [InlineData(false, 2)]
        [InlineData(false, 4)]
        [InlineData(false, 5)]
        [InlineData(true, 2)]
        [InlineData(true, 3)]
        [InlineData(true, 4)]
        [InlineData(true, 5)]
        public void AnyOtherPanelFrameIsLeftAlone(bool steam, int frame)
        {
            var rig = new Rig(steam);
            rig.Ui.OpenSettingsAtFrame(frame);
            rig.Host.Drain();
            SettingsMenuLayout.Apply(rig.Host, rig.State);   // also when something posts it on the pause panel
            rig.AssertNoWrites();
            // Only settings_main_screen posts the step, so any other frame (the in-match 2 too) is an anomaly: a line, and on
            // Android the R4 gate keeps GRAPHICS SETTINGS and FRAME RATE LIMIT in Gameplay Settings.
            Assert.All(rig.Lines, l => Assert.Equal("settings menu: not laid out (frame " + frame + ")", l));
            Assert.NotEmpty(rig.Lines);
            Assert.Equal(!steam, rig.State.RedressFailed);
            rig.AssertClean();
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        [InlineData(true, false)]
        public void TheMainFrameIsReadOneBasedWhicheverMemberAnswers(bool steam, bool asReadable)
        {
            // ASCharacter.CurrentFrame is 0-based (Android's 3 reads 2, the in-match frame; Steam's 1 reads 0); the AS currentFrame
            // is 1-based. Read raw, the property left Android's grid stock with no line and no RedressFailed.
            var rig = new Rig(steam);
            rig.Ui.OpenSettings();
            int frame = steam ? 1 : 3;
            Assert.Equal(frame, rig.Ui.Panel.Frame);
            if (!asReadable) Assert.True(rig.Ui.Panel.Getters.Remove("currentFrame"));   // only the native property answers
            rig.Host.Drain();
            Assert.Equal(new[] { "settings menu: frame " + frame + ", " + (steam ? 6 : 7) + " tiles + ABOUT laid out" }, rig.Lines);
            Assert.Equal(1, rig.State.Layouts);
            Assert.Equal(!steam, rig.State.TileActive);
            Assert.False(rig.State.RedressFailed);
            rig.AssertClean();
        }

        [Fact]
        public void TheInMatchSettingsPanelIsNeverTouched()
        {
            var rig = new Rig(false);
            rig.Ui.InGame = true;
            rig.Open();
            Assert.Equal(0, rig.Host.Pending);
            rig.AssertNoWrites();
            Assert.Empty(rig.Lines);
        }

        [Fact]
        public void AnotherMenuOnTopMeansNoWrite()
        {
            var rig = new Rig(false);
            rig.Ui.OpenSettings();
            rig.Ui.PushMenu("menu_about");   // before the posted step runs
            rig.Host.Drain();
            rig.AssertNoWrites();
            Assert.Equal(new[] { "settings menu: not laid out (the menu on top is menu_about)" }, rig.Lines);
            rig.AssertClean();
        }

        [Theory]
        [InlineData(false, "btn_audio", 400f, false)]
        [InlineData(false, "btn_forum", 467.5f, false)]
        [InlineData(false, "btn_forum", 467.3f, true)]
        [InlineData(false, "btn_general", 463.4f, true)]
        [InlineData(true, "btn_performance", 400f, false)]
        [InlineData(true, "btn_help", 372.28f, false)]   // the in-match 0.8 scale
        public void ATileOfAnotherWidthMeansNoWrite(bool steam, string tile, float width, bool laidOut)
        {
            var rig = new Rig(steam);
            rig.Ui.OpenSettings();
            rig.Ui.Instance(tile).Members["width"] = width;
            rig.Host.Drain();
            if (laidOut)
            {
                Assert.Single(rig.Lines, l => l.EndsWith("laid out", StringComparison.Ordinal));
                return;
            }
            rig.AssertNoWrites();
            Assert.Equal(new[] { "settings menu: not laid out (" + tile + " is " + width.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " wide)" }, rig.Lines);
        }

        [Fact]
        public void TheAboutButtonIsNotWidthChecked()
        {
            var rig = new Rig(false);
            rig.Ui.OpenSettings();
            Assert.Equal(FakeFlashTree.ThinButtonWidth, rig.Ui.Instance("btn_credits").Num("width"));
            rig.Host.Drain();
            Assert.Equal(-182.9f, rig.At("btn_credits")[0]);   // the plate, not .width/2 (about 200)
        }

        // ------------------------------------------------------------------ T-B4: absolute writes end Steam's drift

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(5)]
        public void SteamsDriftEndsWhateverTheNumberOfReturns(int returns)
        {
            var rig = new Rig(true);
            rig.Open();
            float[] about = rig.At("btn_credits");
            float[] performance = rig.At("btn_performance");
            for (int n = 0; n < returns; n++)
            {
                Assert.True(rig.Ui.Tap(n % 2 == 0 ? "btn_general" : "btn_language"));
                rig.Ui.Back();
                // setup() ran again: its relative write moved ABOUT and its column write moved the tiles back to 4 columns ...
                Assert.NotEqual(about[0], rig.Ui.Instance("btn_credits").Num("x"));
                Assert.Equal(484f, rig.Ui.Instance("btn_performance").Num("x"));
                rig.Host.Drain();
                // ... and the step puts them back where they were, not 200 further.
                Assert.Equal(about, rig.At("btn_credits"));
                Assert.Equal(performance, rig.At("btn_performance"));
            }
            Assert.Equal(-182.9f, rig.At("btn_credits")[0]);
            Assert.Equal(354.1f, rig.At("btn_credits")[1]);
            Assert.Equal(1 + returns, rig.State.Layouts);
            Assert.Equal(1 + returns, rig.Lines.Count);
            rig.AssertClean();
        }

        [Fact]
        public void StartingFromDriftedPositionsGivesTheSameLayout()
        {
            // Without the step (trim off) Steam drifts 200 units per return; one step then lands where a first opening does.
            var rig = new Rig(true);
            rig.Host.SettingsMenuTrim = false;
            rig.Open();
            float stock = rig.Ui.Instance("btn_credits").Num("x");
            for (int n = 0; n < 4; n++)
            {
                rig.Ui.Tap("btn_general");
                rig.Ui.Back();
                rig.Host.Drain();
            }
            Assert.Equal(stock - 4 * 200.5f, rig.Ui.Instance("btn_credits").Num("x"), 2);
            rig.AssertNoWrites();
            rig.Host.SettingsMenuTrim = true;
            rig.Ui.Tap("btn_general");
            rig.Ui.Back();
            rig.Host.Drain();
            Assert.Equal(-182.9f, rig.At("btn_credits")[0]);
            Assert.Equal(-715.6f, rig.At("btn_general")[0]);
        }

        // ------------------------------------------------------------------ T-B5: the removed instances

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void EveryRemovedInstanceIsHiddenAndTakesNoTap(bool steam)
        {
            var rig = new Rig(steam);
            rig.Open();
            SettingsMenuLayout.LayoutPlan plan = SettingsMenuLayout.Plan(steam);
            foreach (string name in plan.Hidden)
            {
                Assert.Contains("menu_settings.mc_panel." + name + ".visible=false", rig.Ui.HostWrites);
                Assert.Contains("menu_settings.mc_panel." + name + ".mouseEnabled=false", rig.Ui.HostWrites);
                Assert.False(rig.Ui.Tap(name), name + " took a tap");
            }
            Assert.Empty(rig.Ui.Taps);
            Assert.Equal(0, rig.Ui.Game.ForumCalls);
            Assert.DoesNotContain(rig.Ui.Invoked, n => n == "GoToCostumerCare" || n == "GoToFreeGames");
            // Nothing kept is hidden.
            foreach (SettingsMenuLayout.Placement p in plan.Kept)
            {
                Assert.True((bool)rig.Ui.Instance(p.Name).Get("visible"), p.Name);
                Assert.True((bool)rig.Ui.Instance(p.Name).Get("mouseEnabled"), p.Name);
            }
            // Android's setup() shows MORE GAMES again on every focus-in; the step hides it again.
            rig.Ui.Tap("btn_audio");
            rig.Ui.Back();
            rig.Host.Drain();
            foreach (string name in plan.Hidden) Assert.False((bool)rig.Ui.Instance(name).Get("visible"), name);
        }

        // ------------------------------------------------------------------ T-B6: the re-dress

        [Fact]
        public void TheReDressIsOneSetupWithExactlyTheTitleAndIcon()
        {
            var rig = new Rig(false);
            rig.Open();
            Assert.Equal(1, rig.Ui.HostWrites.Count(w => w.StartsWith(Forum + ".setup(", StringComparison.Ordinal)));
            Assert.Contains(PerformanceSetup, rig.Ui.HostWrites);
            // On every return to the grid (setup() puts FORUM back), once more.
            rig.Ui.Tap("btn_help");
            rig.Ui.Back();
            Assert.Equal("setting.ForumButton", rig.Ui.Instance("btn_forum").Get("title"));
            rig.Host.Drain();
            Assert.Equal(2, rig.Ui.HostWrites.Count(w => w == PerformanceSetup));
            Assert.Equal(2, rig.Ui.HostWrites.Count(w => w.StartsWith(Forum + ".setup(", StringComparison.Ordinal)));
            Assert.Equal("setting.Performance", rig.Ui.Instance("btn_forum").Get("title"));
            Assert.True(rig.State.TileActive);
        }

        [Fact]
        public void ASetupThatThrowsMeansNothingElseMovesAndTheTileStaysInactive()
        {
            var rig = new Rig(false);
            rig.Ui.OpenSettings();
            float[] help = rig.At("btn_help");
            rig.Ui.Instance("btn_forum").Methods["setup"] = a => { throw new InvalidOperationException("setup failed"); };
            rig.Host.Drain();
            Assert.Equal(new[] { PerformanceSetup }, rig.Ui.HostWrites);
            Assert.False(rig.State.TileActive);
            Assert.False(rig.State.TileEverActive);
            Assert.True(rig.State.RedressFailed);
            Assert.Equal(0, rig.State.Layouts);
            Assert.Equal(help, rig.At("btn_help"));
            Assert.True((bool)rig.Ui.Instance("btn_social").Get("visible"));
            Assert.Equal(new[] { "settings menu: not laid out (InvalidOperationException: setup failed)" }, rig.Lines);
            rig.AssertClean();
        }

        [Fact]
        public void AWriteThatThrowsAfterTheReDressPutsForumBack()
        {
            var rig = new Rig(false);
            rig.Ui.OpenSettings();
            rig.Ui.Instance("btn_language").Setters["x"] = v => { throw new InvalidOperationException("x refused"); };
            rig.Host.Drain();
            Assert.Equal(new[] { PerformanceSetup, Forum + ".setup({title:setting.ForumButton,icon:Menus/assets/png/icons/settings_forum.png})" },
                         rig.Ui.HostWrites.Where(w => w.Contains(".setup(")));
            Assert.Equal("setting.ForumButton", rig.Ui.Instance("btn_forum").Get("title"));
            Assert.False(rig.State.TileActive);
            Assert.True(rig.State.RedressFailed);
            Assert.Equal(0, rig.State.Layouts);
            Assert.Equal(new[] { "settings menu: not laid out (InvalidOperationException: x refused)" }, rig.Lines);
            rig.AssertClean();
        }

        [Fact]
        public void ASecondOpeningAfterAFailureStillLaysOutButTheFailureIsRemembered()
        {
            var rig = new Rig(false);
            rig.Ui.OpenSettings();
            rig.Ui.Instance("btn_forum").Methods["setup"] = a => { throw new InvalidOperationException("setup failed"); };
            rig.Host.Drain();
            rig.Ui.Back();
            rig.Open();   // a fresh panel (menu_settings is disposable)
            Assert.True(rig.State.TileActive);
            Assert.True(rig.State.RedressFailed);   // for the process: the R4 gate stays open (PerformancePage)
        }

        // ------------------------------------------------------------------ T-B7: the switches

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WithTheTrimAndPerformanceOffNothingIsWritten(bool steam)
        {
            var rig = new Rig(steam);
            rig.Host.SettingsMenuTrim = false;
            rig.Host.PerformanceSetting = false;
            rig.Open();
            rig.AssertNoWrites();
            Assert.Empty(rig.Lines);
            Assert.False(rig.State.RedressFailed);
            Assert.Equal(FakeFlashTree.TimelinePosition(steam, steam ? 1 : 3, "btn_help")[1], rig.At("btn_help")[1]);
            rig.AssertClean();
        }

        [Fact]
        public void WithTheTrimOffAndPerformanceOnOnlyTheReDressRunsInPlace()
        {
            var rig = new Rig(false);
            rig.Host.SettingsMenuTrim = false;
            FlashObject menu = rig.Ui.OpenSettings();
            menu.Child("mc_panel").Children.Remove(rig.Ui.Instance("btn_social"));   // only btn_forum is read
            rig.Host.Drain();
            Assert.Equal(new[] { PerformanceSetup }, rig.Ui.HostWrites);
            Assert.Equal(new[] { 6.35f, -50.9f }, rig.At("btn_forum"));   // the stock slot
            Assert.True((bool)rig.Ui.Instance("btn_more").Get("visible"));
            Assert.True(rig.State.TileActive);
            Assert.Equal(0, rig.State.Layouts);
            Assert.Equal(new[] { "settings menu: frame 3, PERFORMANCE tile re-dressed in place (trim off)" }, rig.Lines);
            rig.AssertClean();
        }

        [Fact]
        public void WithPerformanceOffAndroidHidesForumAndNeverReDressesIt()
        {
            var rig = new Rig(false);
            rig.Host.PerformanceSetting = false;
            rig.Open();
            Assert.DoesNotContain(rig.Ui.HostWrites, w => w.Contains(".setup("));
            Assert.Equal("setting.ForumButton", rig.Ui.Instance("btn_forum").Get("title"));
            Assert.False((bool)rig.Ui.Instance("btn_forum").Get("visible"));
            Assert.False(rig.Ui.Tap("btn_forum"));
            Assert.Equal(new[] { -471.05f, -50.9f }, rig.At("btn_help"));
            Assert.Equal(new[] { 6.85f, -50.9f }, rig.At("btn_language"));
            Assert.False(rig.State.TileActive);
            Assert.False(rig.State.RedressFailed);
            Assert.Equal(new[] { "settings menu: frame 3, 6 tiles + ABOUT laid out" }, rig.Lines);
        }

        [Fact]
        public void TheSwitchesAreReadOnEveryStep()
        {
            var rig = new Rig(false);
            rig.Open();
            Assert.True(rig.State.TileActive);
            // Off: the next opening (a fresh panel) is stock, and the tile is no longer active.
            rig.Ui.Back();
            rig.Host.SettingsMenuTrim = false;
            rig.Host.PerformanceSetting = false;
            rig.Ui.HostWrites.Clear();
            rig.Open();
            Assert.Empty(rig.Ui.HostWrites);
            Assert.False(rig.State.TileActive);
            Assert.True((bool)rig.Ui.Instance("btn_social").Get("visible"));
            Assert.Equal(FakeFlashTree.TimelinePosition(false, 3, "btn_account"), rig.At("btn_account"));
            // On again, without a restart.
            rig.Host.SettingsMenuTrim = true;
            rig.Host.PerformanceSetting = true;
            rig.Ui.Tap("btn_help");
            rig.Ui.Back();
            rig.Host.Drain();
            Assert.True(rig.State.TileActive);
            Assert.Equal(new[] { 484f, -427.9f }, rig.At("btn_account"));
        }

        // ------------------------------------------------------------------ no game thread, no UI, no host

        [Fact]
        public void NoGameThreadOrNoUiIsOneLine()
        {
            var rig = new Rig(false);
            rig.Host.CanPost = false;
            rig.Ui.OpenSettings();
            Assert.Equal(new[] { "settings menu: not laid out (no game thread)" }, rig.Lines);
            rig.AssertNoWrites();

            var noUi = new Rig(false);
            FakeFlashTree ui = noUi.Ui;
            ui.OpenSettings();
            noUi.Host.Ui = null;
            noUi.Host.Drain();
            Assert.Equal(new[] { "settings menu: not laid out (no UI)" }, noUi.Lines);
            Assert.True(noUi.State.RedressFailed);
            Assert.Empty(ui.HostWrites);
        }

        [Fact]
        public void NullHostOrStateDoesNothing()
        {
            SettingsMenuLayout.Schedule(null, new SettingsMenuState());
            SettingsMenuLayout.Apply(null, new SettingsMenuState());
            var host = new FakeSettingsMenuHost();
            host.Ui.OpenSettings();
            SettingsMenuLayout.Schedule(host, null);
            SettingsMenuLayout.Apply(host, null);
            Assert.Equal(0, host.Pending);
            Assert.Empty(host.Ui.HostWrites);
        }

        [Fact]
        public void AThrowingHandleIsOneLineAndNoWrite()
        {
            var rig = new Rig(true);
            rig.Ui.OpenSettings();
            rig.Ui.Instance("btn_audio").Throws = true;
            rig.Host.Drain();
            rig.AssertNoWrites();
            Assert.Equal(new[] { "settings menu: not laid out (InvalidOperationException: probe: native call failed)" }, rig.Lines);
            rig.AssertClean();
        }
    }
}
