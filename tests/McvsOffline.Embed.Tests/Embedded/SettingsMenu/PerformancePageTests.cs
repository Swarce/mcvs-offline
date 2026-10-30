using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using McvsOffline.Embedded.Admin.SettingsMenu;
using McvsOffline.Embedded.Graphics;
using McvsOffline.Server.Services.Admin;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// The Android PERFORMANCE page and R4 (docs/design/settings-menu.md §3.3, §3.5, §4.1 T-C1..T-C6; skeptic B2, D2, E1-E3, E6) on
    /// the fake Flash tree: the natives are wired as UiNatives wires them (EnterSection appended, GoToForum replacing, the rest
    /// appended after the game's own), the grid's re-dress is taken as done, and the back end is a fake (WP-D's PerformancePrefs has
    /// its own tests).
    /// </summary>
    public class PerformancePageTests
    {
        private static readonly string[] DonorTypes = { "GraphicSetting", "InvertCameraX", "InvertCameraY", "CameraSmoothing" };

        /// <summary>One Settings menu with our natives, a host, a state and a back end.</summary>
        private sealed class Rig
        {
            public Rig(bool wire = true)
            {
                Ui = new FakeFlashTree();
                Host = new FakeSettingsMenuHost(Ui);
                State = new SettingsMenuState();
                BackEnd = new FakePageBackEnd();
                PerformancePage.UseBackEnd(State, BackEnd);
                if (wire) Wire();
            }

            public FakeFlashTree Ui { get; private set; }
            public FakeSettingsMenuHost Host { get; private set; }
            public SettingsMenuState State { get; private set; }
            public FakePageBackEnd BackEnd { get; private set; }

            /// <summary>"key=value" of every SetUserPreference, as Flash sent it (a spy after ours).</summary>
            public List<string> Sent { get; } = new List<string>();

            /// <summary>The engine's own state behind auto.</summary>
            public FakePageEngine Engine { get { return BackEnd.EngineFake; } }

            public FakePerformanceSettings Settings { get { return Host.Settings; } }

            private void Wire()
            {
                FakeFlashTree ui = Ui;
                ISettingsMenuHost host = Host;
                SettingsMenuState state = State;
                ui.OverrideNativeFunction("EnterSection", new Action<string>(section =>
                {
                    if (section != SettingsMenuNames.MainSettingsSection) return;
                    PerformancePage.LeaveMode(state);
                    state.TileActive = true;   // the grid's re-dress (WP-B) is taken as done
                    PerformancePage.ShowRestartPromptIfPending(host, state);
                }), false);
                ui.OverrideNativeFunction("GoToForum", new Action(() =>
                {
                    if (state.TileActive) PerformancePage.Open(host, state);
                    else ui.GoToForum();
                }), true, false);
                ui.OverrideNativeFunction("CanChangeGraphicSetting", new Func<object>(() => PerformancePage.CanChangeGraphicSetting(host, state)), false);
                ui.OverrideNativeFunction("CanChangeMaxFramerate", new Func<object>(() => PerformancePage.CanChangeMaxFramerate(host, state)), false);
                ui.OverrideNativeFunction("SetUserPreference", new Action<string, object>((k, v) => PerformancePage.OnSetUserPreference(host, state, k, v)), false);
                ui.OverrideNativeFunction("SaveUserPreferences", new Action(() => PerformancePage.OnSaveUserPreferences(host, state)), false);
                ui.OverrideNativeFunction("SetUserPreference", new Action<string, object>((k, v) => Sent.Add(k + "=" + FakeFlashTree.FormatAs(v))), false);
            }

            /// <summary>Settings, then the PERFORMANCE tile, then every posted step: the page as the player sees it.</summary>
            public void OpenPage()
            {
                Ui.OpenSettings();
                Assert.True(Ui.Tap("btn_forum"));
                Host.Drain();
            }

            /// <summary>The page's rows (optionMC) by their y.</summary>
            public List<FlashObject> RowsByY()
            {
                return Ui.Rows().OrderBy(r => r.Num("y")).ToList();
            }

            public string TitleOf(FlashObject row)
            {
                return row.Child("tf_title").Child("tf").Get("text") as string;
            }

            public List<string> Lines()
            {
                return Host.Lines("settings menu: ");
            }
        }

        private static List<string> StockAndroidRows()
        {
            var rows = FakeFlashTree.InitOptions().Where(o => o.Category != "setting.CustomizeSettingsGamepad").Select(o => o.Type).ToList();
            rows.Insert(2, "GraphicSetting");
            rows.Add("MaxFramerate");
            return rows;
        }

        // ------------------------------------------------------------------ T-C1: the mode

        [Fact]
        public void ThePerformanceTapOpensThePageAndEveryReturnToTheGridEndsTheMode()
        {
            var rig = new Rig();
            rig.Ui.OpenSettings();
            Assert.False(rig.State.PerformanceMode);
            Assert.True(rig.Ui.Tap("btn_forum"));
            Assert.True(rig.State.PerformanceMode);
            Assert.Equal(0, rig.Ui.Game.ForumCalls);              // the forum never opens on a PERFORMANCE tap
            Assert.Equal(1, rig.Host.Pending);                     // the push is posted, not done inside the tap's native
            Assert.Equal(new[] { "menu_settings" }, rig.Ui.Pushed);
            rig.Host.Drain();
            Assert.Equal(new[] { "menu_settings", "menu_gameplaySettings" }, rig.Ui.Pushed);
            Assert.Equal("menu_gameplaySettings", rig.Ui.Top.Name);
            // The page's own EnterSection("settings_gameplay_screen") does not end the mode.
            Assert.Equal("settings_gameplay_screen", rig.Ui.Game.Sections.Last());
            Assert.True(rig.State.PerformanceMode);
            Assert.Equal(SettingsMenuNames.PageRows, rig.RowsByY().Select(r => r.Get("type") as string));

            rig.Ui.Back();
            Assert.Equal("settings_main_screen", rig.Ui.Game.Sections.Last());
            Assert.False(rig.State.PerformanceMode);
            // GAMEPLAY SETTINGS from the grid is the stock page again (without the two rows: R4).
            rig.Ui.Tap("btn_general");
            rig.Host.Drain();
            Assert.Equal(StockAndroidRows().Where(t => t != "GraphicSetting" && t != "MaxFramerate"), rig.Ui.RowTypes());
            Assert.Equal("setting.GamePlaySetButton", rig.Ui.Top.Get("title"));
            Assert.Equal(1f, rig.Ui.Content.Num("alpha"));
            rig.Ui.AssertAllDisposed();
        }

        [Fact]
        public void LeaveModeClearsOnlyTheMode()
        {
            var state = new SettingsMenuState { PerformanceMode = true, RestartPending = true };
            PerformancePage.LeaveMode(state);
            Assert.False(state.PerformanceMode);
            Assert.True(state.RestartPending);
            PerformancePage.LeaveMode(null);
        }

        [Fact]
        public void OpenDoesNothingWhenSwitchedOffOnPcOrWithoutAGameThread()
        {
            var rig = new Rig();
            rig.Ui.OpenSettings();
            rig.Host.PerformanceSetting = false;
            PerformancePage.Open(rig.Host, rig.State);
            Assert.False(rig.State.PerformanceMode);
            Assert.Equal(0, rig.Host.Pending);
            Assert.Contains("settings menu: performance page not opened (switched off)", rig.Lines());

            rig.Host.PerformanceSetting = true;
            rig.Host.CanPost = false;
            PerformancePage.Open(rig.Host, rig.State);
            Assert.False(rig.State.PerformanceMode);
            Assert.Contains("settings menu: performance page not opened (no game thread)", rig.Lines());

            var pc = new FakeSettingsMenuHost(windows: true);
            var state = new SettingsMenuState();
            PerformancePage.Open(pc, state);
            Assert.False(state.PerformanceMode);
            Assert.Equal(0, pc.Pending);
            PerformancePage.Open(null, state);
            PerformancePage.Open(pc, null);
        }

        [Fact]
        public void ThePushIsDroppedWhenTheGridIsNoLongerOnTop()
        {
            var rig = new Rig();
            rig.Ui.OpenSettings();
            rig.Ui.Tap("btn_forum");
            rig.Ui.PushMenu("menu_about");   // another menu came first, in the same frame
            rig.Host.Drain();
            Assert.Equal("menu_about", rig.Ui.Top.Name);
            Assert.DoesNotContain("menu_gameplaySettings", rig.Ui.Pushed);
            Assert.False(rig.State.PerformanceMode);
            Assert.Contains("settings menu: performance page not opened (the menu on top is menu_about)", rig.Lines());

            // Back to the grid before the posted push ran: nothing is pushed, nothing is logged.
            var back = new Rig();
            back.Ui.OpenSettings();
            back.Ui.Tap("btn_forum");
            PerformancePage.LeaveMode(back.State);
            back.Host.Drain();
            Assert.Equal(new[] { "menu_settings" }, back.Ui.Pushed);
            Assert.Empty(back.Lines());
            rig.Ui.AssertAllDisposed();
        }

        // ------------------------------------------------------------------ T-C2: R4

        public static IEnumerable<object[]> R4Cases()
        {
            foreach (bool performance in new[] { true, false })
            foreach (bool redressFailed in new[] { true, false })
            foreach (bool mode in new[] { true, false })
            foreach (bool windows in new[] { true, false })
                yield return new object[] { performance, redressFailed, mode, windows };
        }

        [Theory]
        [MemberData(nameof(R4Cases))]
        public void OnlyTheSwitchOnWithoutAFailedRedressOutsideTheModeHidesTheRows(bool performance, bool redressFailed, bool mode, bool windows)
        {
            var host = new FakeSettingsMenuHost(windows: windows) { PerformanceSetting = performance };
            var state = new SettingsMenuState { RedressFailed = redressFailed, PerformanceMode = mode };
            PerformancePage.UseBackEnd(state, new FakePageBackEnd());
            bool hides = performance && !redressFailed && !mode && !windows;
            Assert.Equal(hides, PerformancePage.HidesGameplayRows(host, state));
            object graphic = PerformancePage.CanChangeGraphicSetting(host, state);
            object framerate = PerformancePage.CanChangeMaxFramerate(host, state);
            if (hides)
            {
                Assert.Equal(false, graphic);   // a boxed false: it wins the dispatch
                Assert.Equal(false, framerate);
            }
            else
            {
                Assert.Null(graphic);           // the game's answer stands
                Assert.Null(framerate);
            }
            // The transform is posted only in the mode, with the switch on, on Android.
            Assert.Equal(mode && performance && !windows ? 1 : 0, host.Pending);
        }

        [Fact]
        public void R4NeverAnswersWithoutAHostAndBeforeTheFirstGridVisitAlreadyHides()
        {
            var state = new SettingsMenuState();
            Assert.False(PerformancePage.HidesGameplayRows(null, state));
            Assert.Null(PerformancePage.CanChangeGraphicSetting(null, state));
            Assert.Null(PerformancePage.CanChangeMaxFramerate(null, state));
            Assert.Null(PerformancePage.CanChangeGraphicSetting(new FakeSettingsMenuHost(), null));
            // Skeptic D2 relaxed, owner answer 4: no tile re-dressed yet in this process, the rows still go.
            Assert.False(state.TileEverActive);
            Assert.True(PerformancePage.HidesGameplayRows(new FakeSettingsMenuHost(), state));
        }

        [Fact]
        public void GameplaySettingsLosesBothRowsInTheMenuAndInMatchWithoutAGapOrAnOrphanHeader()
        {
            var rig = new Rig();
            rig.Ui.OpenSettings();
            rig.Ui.Tap("btn_general");
            Assert.Equal(StockAndroidRows().Where(t => t != "GraphicSetting" && t != "MaxFramerate"), rig.Ui.RowTypes());
            List<string> layout = rig.Ui.ContentLayout();
            Assert.Equal("row:CameraSensitivity", layout[layout.IndexOf("category:menu.GeneralTxt") + 1]);
            Assert.Equal("row:ReturnToIronSightAfterReload", layout.Last());
            Assert.Equal(0, rig.Ui.FramerateWarnings);
            Assert.Equal(0, rig.Host.Pending);

            // In a match (owner answer 4): FRAME RATE LIMIT is gone from the pause menu's Gameplay page too.
            var match = new Rig();
            match.Ui.InGame = true;
            match.Ui.Game.InMenu = false;
            match.Ui.PushMenu("menu_gameplaySettings");
            Assert.DoesNotContain("MaxFramerate", match.Ui.RowTypes());
        }

        [Fact]
        public void TheRowsStayWhenTheSwitchIsOffOrTheRedressFailed()
        {
            var off = new Rig();
            off.Host.PerformanceSetting = false;
            off.Ui.OpenSettings();
            off.Ui.Tap("btn_general");
            Assert.Equal(StockAndroidRows(), off.Ui.RowTypes());

            var failed = new Rig();
            failed.State.RedressFailed = true;
            failed.Ui.OpenSettings();
            failed.Ui.Tap("btn_general");
            Assert.Equal(StockAndroidRows(), failed.Ui.RowTypes());
        }

        // ------------------------------------------------------------------ T-C3: the transform

        [Fact]
        public void TheTransformLeavesExactlyThePerformanceRowsInSteamsOrder()
        {
            var rig = new Rig();
            rig.OpenPage();
            // The input was the stock set: GraphicSetting spliced at 2, MaxFramerate last, category headers.
            Assert.Contains("mc_content.alpha=0", string.Join("\n", rig.Ui.HostWrites));
            List<FlashObject> rows = rig.RowsByY();
            Assert.Equal(new[] { "mcvs.graphicsProfile", "mcvs.msaa", "mcvs.renderScale", "MaxFramerate", "mcvs.lowResMaps" },
                         rows.Select(r => r.Get("type") as string));
            Assert.Equal(5, rig.Ui.Content.Children.Count);
            Assert.DoesNotContain(rig.Ui.Content.Children, c => c.Kind == "categoryMC");
            Assert.Equal(0f, rows[0].Num("y"));
            for (int i = 1; i < rows.Count; i++) Assert.Equal(rows[i - 1].Num("y") + rows[i - 1].Num("height") + 8, rows[i].Num("y"));
            Assert.Equal(2, rig.Ui.BoundsUpdates);   // setup()'s own, then the transform's: exactly once
            Assert.Equal(new[] { "setting.Performance" }, rig.Ui.FreemiumTitles);
            Assert.Equal("setting.Performance", rig.Ui.Top.Get("title"));
            Assert.Equal(1f, rig.Ui.Content.Num("alpha"));
            Assert.False(rig.State.Transforming);
            Assert.Contains("settings menu: performance page: 5 rows", rig.Lines());

            // Titles: GRAPHICS SETTINGS and FRAME RATE LIMIT translated (skeptic B2), English for the rest (owner answer 5).
            Assert.Equal(new[] { "setting.GraphicSetting", "ANTIALIASING", "SCREEN SCALE", "setting.FrameRateLimit", "LOW RESOLUTION MAPS" },
                         rows.Select(rig.TitleOf));
            Assert.Equal(new[] { "mcvs_graphicsProfile", "mcvs_msaa", "mcvs_renderScale", "mcvs_lowResMaps" },
                         rows.Where(r => r.Get("type") as string != "MaxFramerate").Select(r => r.Name));
            Assert.Equal(new object[] { "AUTO", "MID", "HIGH", "ULTRA" }, (IEnumerable<object>)rows[0].Get("values"));
            Assert.Equal(4f, rows[1].Child("mc_listSelector").Num("dataLength"));
            Assert.Equal(2f, rows[4].Child("mc_listSelector").Num("dataLength"));
            // FRAME RATE LIMIT keeps its 'i' button; the re-typed selectors have none.
            Assert.True(FakeFlashTree.Truthy(rows[3].Child("mc_warning").Get("visible")));
            Assert.True(rig.Ui.TapWarning(rows[3]));
            Assert.False(FakeFlashTree.Truthy(rows[1].Child("mc_warning").Get("visible")));
            Assert.Equal(0, rig.Ui.GenericCalls);
            Assert.Empty(rig.Ui.ManagedWrites);   // numbers went as float
            rig.Ui.AssertAllDisposed();
        }

        [Fact]
        public void AutoRowsShowWhatTheEngineHasNowAndSetRowsTheirValue()
        {
            var rig = new Rig();
            rig.Engine.Samples = 4;
            rig.Engine.Scale = 0.58f;   // (0.58 - 0.4) / 0.6 = 30 %
            rig.Engine.LowRes = true;
            rig.OpenPage();
            List<FlashObject> rows = rig.RowsByY();
            Assert.Equal(0f, rows[0].Child("mc_listSelector").Get("value"));   // AUTO
            Assert.Equal("AUTO", rows[0].Child("mc_listSelector").Get("text"));
            Assert.Equal(2f, rows[1].Child("mc_listSelector").Get("value"));   // 4x
            Assert.Equal("4x", rows[1].Child("mc_listSelector").Get("text"));
            Assert.Equal(0.3f, rows[2].Child("mc_scrollbar").Num("value"), 3);
            Assert.Equal(0.08f + 0.3f * 0.84f, rows[2].Child("mc_scrollbar").Child("slot").Child("mc_mask").Num("scaleX"), 3);
            Assert.Equal(1f, rows[4].Child("mc_listSelector").Get("value"));   // ON
            Assert.Equal("setting.OnButton", rows[4].Child("mc_listSelector").Get("text"));

            var set = new Rig();
            set.Settings.Store("graphics.profile", "high");
            set.Settings.Store(SettingsSchema.GraphicsMsaaKey, "2x");
            set.Settings.Store(SettingsSchema.GraphicsRenderScaleKey, "0");
            set.Settings.Store(SettingsSchema.GraphicsLowResMapsKey, "off");
            set.OpenPage();
            rows = set.RowsByY();
            Assert.Equal("HIGH", rows[0].Child("mc_listSelector").Get("text"));
            Assert.Equal("2x", rows[1].Child("mc_listSelector").Get("text"));
            Assert.Equal(0.0001f, rows[2].Child("mc_scrollbar").Num("value"), 5);   // setup()'s own 0 (MGS:333)
            Assert.Equal("setting.OffButton", rows[4].Child("mc_listSelector").Get("text"));
        }

        [Fact]
        public void ADonorThatIsMissingFallsBackToTheNextOne()
        {
            var rig = new Rig();
            rig.Ui.OpenSettings();
            rig.State.PerformanceMode = true;
            rig.Ui.PushMenu("menu_gameplaySettings");
            rig.Ui.Content.Children.Remove(rig.Ui.Row("CameraSmoothing"));
            rig.Ui.Content.Children.Remove(rig.Ui.Row("InvertCameraX"));
            rig.Host.Drain();
            Assert.Equal(SettingsMenuNames.PageRows, rig.RowsByY().Select(r => r.Get("type") as string));
            Assert.Contains("settings menu: performance page: 5 rows", rig.Lines());
        }

        // ------------------------------------------------------------------ T-C4: no blank page

        [Fact]
        public void AMissingRowRemovesNothingAndShowsThePageAgain()
        {
            // No FRAME RATE LIMIT row (GameOptions canChangeMaxFramerate false).
            var rig = new Rig();
            rig.Ui.Game.CanChangeMaxFramerateValue = false;
            rig.OpenPage();
            Assert.Equal(StockAndroidRows().Where(t => t != "MaxFramerate"), rig.Ui.RowTypes());
            Assert.Contains(rig.Ui.Content.Children, c => c.Kind == "categoryMC");
            Assert.Equal(1f, rig.Ui.Content.Num("alpha"));
            Assert.False(rig.State.Transforming);
            Assert.Contains("settings menu: performance page not built (no MaxFramerate row)", rig.Lines());
            Assert.DoesNotContain(rig.Ui.HostWrites, w => w.Contains(".type=") || w.Contains("removeChild"));

            // No slider donor at all (a game update renamed CameraSmoothing and CameraSensitivity).
            var slider = new Rig();
            slider.Ui.OpenSettings();
            slider.State.PerformanceMode = true;
            slider.Ui.PushMenu("menu_gameplaySettings");
            slider.Ui.Row("CameraSmoothing").Members["type"] = "CameraSmoothing2";
            slider.Ui.Row("CameraSensitivity").Members["type"] = "CameraSensitivity2";
            int children = slider.Ui.Content.Children.Count;
            Assert.Equal(0f, slider.Ui.Content.Num("alpha"));
            slider.Host.Drain();
            Assert.Equal(children, slider.Ui.Content.Children.Count);
            Assert.Equal(1f, slider.Ui.Content.Num("alpha"));
            Assert.Contains("settings menu: performance page not built (no donor row for mcvs.renderScale)", slider.Lines());
            Assert.Equal("setting.GamePlaySetButton", slider.Ui.Top.Get("title"));
            Assert.Empty(slider.Ui.FreemiumTitles);
            slider.Ui.AssertAllDisposed();
        }

        [Fact]
        public void ATransformThatThrowsHalfwayRemovesNothingAndShowsThePageAgain()
        {
            var rig = new Rig();
            rig.Ui.OpenSettings();
            rig.State.PerformanceMode = true;
            rig.Ui.PushMenu("menu_gameplaySettings");
            // The fourth re-type (LOW RESOLUTION MAPS on InvertCameraY) fails: three rows are re-typed by then.
            rig.Ui.Row("InvertCameraY").Setters["type"] = v => { throw new InvalidOperationException("probe: type write failed"); };
            int children = rig.Ui.Content.Children.Count;
            rig.Host.Drain();
            Assert.Equal(children, rig.Ui.Content.Children.Count);
            Assert.Contains(rig.Ui.Content.Children, c => c.Kind == "categoryMC");
            Assert.Equal(1f, rig.Ui.Content.Num("alpha"));
            Assert.False(rig.State.Transforming);
            Assert.Contains("settings menu: performance page not built (InvalidOperationException: probe: type write failed)", rig.Lines());
            Assert.Empty(rig.Settings.Saves);
            rig.Ui.AssertAllDisposed();
        }

        [Fact]
        public void TheContentIsShownAgainWhenAnotherMenuIsOnTopOrTheModeEndedFirst()
        {
            var rig = new Rig();
            rig.Ui.OpenSettings();
            rig.State.PerformanceMode = true;
            FlashObject page = rig.Ui.PushMenu("menu_gameplaySettings");
            var content = (FlashObject)page.Get("mc_content");
            Assert.Equal(0f, content.Num("alpha"));
            rig.Ui.PushMenu("menu_about");
            rig.Ui.SetStatic("managers.MenusManager", "getMenuByName", a => (a[0] as string) == "menu_gameplaySettings" ? page : null);
            rig.Host.Drain();
            Assert.Equal(1f, content.Num("alpha"));   // the cached page is not left invisible
            Assert.Contains("settings menu: performance page not built (the menu on top is menu_about)", rig.Lines());

            var left = new Rig();
            left.Ui.OpenSettings();
            left.State.PerformanceMode = true;
            left.Ui.PushMenu("menu_gameplaySettings");
            PerformancePage.LeaveMode(left.State);
            left.Host.Drain();
            Assert.Equal(1f, left.Ui.Content.Num("alpha"));
            Assert.Equal(StockAndroidRows(), left.Ui.RowTypes());
            Assert.DoesNotContain(left.Lines(), l => l.StartsWith("settings menu: performance page", StringComparison.Ordinal));
        }

        [Fact]
        public void TheGamesBackEndNeverLeavesABlankPage()
        {
            // No UseBackEnd: PerformancePrefs itself (a skeleton or the real one, whichever this build has).
            var ui = new FakeFlashTree();
            var host = new FakeSettingsMenuHost(ui);
            var state = new SettingsMenuState { PerformanceMode = true };
            ui.OverrideNativeFunction("CanChangeGraphicSetting", new Func<object>(() => PerformancePage.CanChangeGraphicSetting(host, state)), false);
            ui.PushMenu("menu_settings");
            ui.PushMenu("menu_gameplaySettings");
            host.Drain();
            Assert.Equal(1f, ui.Content.Num("alpha"));
            Assert.False(state.Transforming);
            Assert.Single(host.Lines("settings menu: performance page"));
            ui.AssertAllDisposed();
        }

        // ------------------------------------------------------------------ T-C5, T-C5b: the writes

        [Fact]
        public void AnAntialiasingChangeStoresTheChoiceNotTheBooleanTheMenuSends()
        {
            var rig = new Rig();
            rig.OpenPage();
            rig.Sent.Clear();
            FlashObject msaa = rig.Ui.Row("mcvs.msaa");
            rig.Ui.Select(msaa, 3);
            Assert.Equal(new[] { "mcvs.msaa=true" }, rig.Sent);    // Boolean(index), MGS:571
            Assert.Equal(new[] { "graphics.msaa=8x" }, rig.Settings.Saves);
            Assert.Equal(new[] { "msaa=8x" }, rig.BackEnd.Calls);
            Assert.True(rig.State.RestartPending);
            Assert.Contains("settings menu: performance: msaa 8x (restart pending)", rig.Lines());
            Assert.DoesNotContain(rig.Ui.Game.Writes, w => w.StartsWith("InvertCamera", StringComparison.Ordinal));

            // A change on one row never turns another row's auto into a value (the others are compared with what they show).
            rig.Ui.Select(rig.Ui.Row("MaxFramerate"), 1);
            Assert.Equal("MaxFramerate=1", rig.Ui.Game.Writes.Last());
            Assert.Equal(new[] { "graphics.msaa=8x" }, rig.Settings.Saves);
            Assert.Equal("auto", rig.Settings.GraphicsProfile);
            Assert.Equal("auto", rig.Settings.LowResMaps);
            Assert.Equal("auto", rig.Settings.RenderScale);

            rig.Ui.Select(msaa, 0);
            Assert.Equal("graphics.msaa=off", rig.Settings.Saves.Last());
        }

        [Fact]
        public void GraphicsAndLowResolutionMapsStoreTheirChoice()
        {
            var rig = new Rig();
            rig.Engine.LowRes = true;
            rig.OpenPage();
            rig.Ui.Select(rig.Ui.Row("mcvs.graphicsProfile"), 2);
            Assert.Equal(new[] { "graphics.profile=high" }, rig.Settings.Saves);
            Assert.True(rig.State.RestartPending);
            Assert.Empty(rig.Ui.Game.GraphicSettingChanges);       // the game's own GRAPHICS SETTINGS restart path never runs
            Assert.DoesNotContain(rig.Ui.Game.Writes, w => w.StartsWith("GraphicSetting", StringComparison.Ordinal));

            rig.State.RestartPending = false;
            rig.Ui.Select(rig.Ui.Row("mcvs.lowResMaps"), 0);       // shown ON (the engine's), now OFF
            Assert.Equal("graphics.lowResMaps=off", rig.Settings.Saves.Last());
            Assert.Equal("lowres=off", rig.BackEnd.Calls.Last());
            Assert.False(rig.State.RestartPending);
            Assert.False(rig.Engine.LowRes);
        }

        [Fact]
        public void TheTransformItselfStoresNothingFromAuto()
        {
            // Skeptic E1 (T-C5b): every key at auto, the engine's values differ from the donors', so every displayList and the
            // slider's value= fire CHANGE, and the menu calls SetUserPreference/SaveUserPreferences from inside the transform.
            var rig = new Rig();
            rig.Engine.Samples = 4;
            rig.Engine.Scale = 0.58f;
            rig.Engine.LowRes = true;
            int saves = rig.Ui.Game.Saves;
            rig.OpenPage();
            Assert.Contains("mcvs.msaa=true", rig.Sent);                  // the CHANGE handlers did run (InvertCameraX 0 → 4x)
            Assert.Contains("mcvs.lowResMaps=true", rig.Sent);
            Assert.Contains("mcvs.graphicsProfile=false", rig.Sent);      // GraphicSetting 1 → AUTO
            Assert.Contains(rig.Sent, s => s.StartsWith("mcvs.renderScale=0.3", StringComparison.Ordinal));
            Assert.True(rig.Ui.Game.Saves > saves);
            Assert.Empty(rig.Settings.Saves);
            Assert.Empty(rig.Settings.Refused);
            Assert.Empty(rig.BackEnd.Calls);
            Assert.False(rig.State.RestartPending);
            Assert.Equal("auto", rig.Settings.Msaa);
            Assert.Equal("auto", rig.Settings.RenderScale);
            Assert.Equal("auto", rig.Settings.LowResMaps);
            Assert.Equal("auto", rig.Settings.GraphicsProfile);
            Assert.Empty(rig.Engine.Writes);
            Assert.Contains("settings menu: performance page: 5 rows", rig.Lines());
        }

        // ------------------------------------------------------------------ T-C6: the write order

        [Fact]
        public void RowsAreRetypedBeforeAnythingThatFiresTheirHandlers()
        {
            var rig = new Rig();
            rig.Engine.Samples = 8;
            rig.Engine.Scale = 0.7f;
            rig.Ui.Game.Writes.Clear();
            rig.OpenPage();
            List<string> before = rig.Ui.HostWrites;
            string content = rig.Ui.Content.Path + ".";
            foreach (string type in new[] { "mcvs.graphicsProfile", "mcvs.msaa", "mcvs.renderScale", "mcvs.lowResMaps" })
            {
                string row = content + PerformancePage.RowName(type);
                int named = before.FindIndex(w => w.StartsWith(content, StringComparison.Ordinal) && w.EndsWith(".name=" + PerformancePage.RowName(type), StringComparison.Ordinal));
                int typed = before.IndexOf(row + ".type=" + type);
                List<int> later = before.Select((w, i) => new { w, i })
                                        .Where(x => x.w.StartsWith(row + ".", StringComparison.Ordinal) && x.i != typed).Select(x => x.i).ToList();
                Assert.True(named >= 0, type + ": no name write");
                Assert.True(typed > named, type + ": type before name");
                Assert.NotEmpty(later);
                Assert.True(later.Min() > typed, type + ": a write before the type: " + before[later.Min()]);
            }
            int values = before.IndexOf(content + "mcvs_msaa.values=[setting.OffButton,2x,4x,8x]");
            Assert.True(values > 0);
            Assert.True(before.IndexOf(content + "mcvs_msaa.mc_listSelector.dataLength=4") > values);
            Assert.True(before.IndexOf(content + "mcvs_msaa.mc_listSelector.displayList(3)") > before.IndexOf(content + "mcvs_msaa.mc_listSelector.dataLength=4"));
            // No SetUserPreference reached a donor's real preference, and the game stored nothing.
            Assert.NotEmpty(rig.Sent);
            Assert.DoesNotContain(rig.Sent, s => DonorTypes.Any(d => s.StartsWith(d + "=", StringComparison.Ordinal)));
            Assert.Empty(rig.Ui.Game.Writes);
        }

        // ------------------------------------------------------------------ SCREEN SCALE

        [Fact]
        public void ASliderMoveIsStoredAndAppliedOncePerValue()
        {
            var rig = new Rig();
            rig.Engine.Scale = 0.7f;   // shown 50 %
            rig.OpenPage();
            FlashObject scale = rig.Ui.Row("mcvs.renderScale");
            rig.Ui.Drag(scale, 0.25f);
            Assert.Equal(new[] { "graphics.renderScale=25" }, rig.Settings.Saves);
            Assert.Equal(new[] { "scale=25" }, rig.BackEnd.Calls);
            Assert.Equal(0.55f, rig.Engine.Scale.Value, 3);
            rig.Ui.Drag(scale, 0.252f);          // still 25 %
            Assert.Single(rig.Settings.Saves);
            rig.Ui.Drag(scale, 1f);
            Assert.Equal("graphics.renderScale=100", rig.Settings.Saves.Last());
            Assert.False(rig.State.RestartPending);
            // The shown auto value is no change: 50 % from auto is not stored.
            var auto = new Rig();
            auto.Engine.Scale = 0.7f;
            auto.OpenPage();
            PerformancePage.OnSetUserPreference(auto.Host, auto.State, "mcvs.renderScale", 0.5f);
            Assert.Empty(auto.Settings.Saves);
        }

        [Fact]
        public void WritesAreIgnoredWhileTransformingForOtherKeysAndWhenSwitchedOff()
        {
            var rig = new Rig();
            rig.OpenPage();
            rig.State.Transforming = true;
            PerformancePage.OnSetUserPreference(rig.Host, rig.State, "mcvs.renderScale", 0.2f);
            rig.Ui.Row("mcvs.msaa").Child("mc_listSelector").ListIndex = 3;
            PerformancePage.OnSaveUserPreferences(rig.Host, rig.State);
            Assert.Empty(rig.Settings.Saves);
            rig.State.Transforming = false;
            PerformancePage.OnSetUserPreference(rig.Host, rig.State, "CameraSmoothing", 0.2f);
            PerformancePage.OnSetUserPreference(rig.Host, rig.State, "mcvs.msaa", true);
            Assert.Empty(rig.Settings.Saves);
            rig.Host.PerformanceSetting = false;
            PerformancePage.OnSetUserPreference(rig.Host, rig.State, "mcvs.renderScale", 0.2f);
            PerformancePage.OnSaveUserPreferences(rig.Host, rig.State);
            Assert.Empty(rig.Settings.Saves);
            rig.Host.PerformanceSetting = true;
            PerformancePage.OnSaveUserPreferences(rig.Host, rig.State);
            Assert.Equal(new[] { "graphics.msaa=8x" }, rig.Settings.Saves);
            // Outside the mode (the stock Gameplay page) nothing is read.
            rig.State.PerformanceMode = false;
            rig.Ui.Row("mcvs.msaa").Child("mc_listSelector").ListIndex = 1;
            PerformancePage.OnSaveUserPreferences(rig.Host, rig.State);
            Assert.Single(rig.Settings.Saves);
            rig.Ui.AssertAllDisposed();
        }

        // ------------------------------------------------------------------ the restart prompt

        [Fact]
        public void TheRestartPromptShowsOnceBackOnTheGridAndYesRestarts()
        {
            var rig = new Rig();
            rig.OpenPage();
            rig.Ui.Select(rig.Ui.Row("mcvs.msaa"), 2);
            Assert.True(rig.State.RestartPending);
            Assert.Empty(rig.Ui.Popups);
            rig.Ui.Back();
            Assert.False(rig.State.RestartPending);
            Assert.Empty(rig.Ui.Popups);       // posted: after MenuSettings.setup()
            rig.Host.Drain();
            FakePopup popup = Assert.Single(rig.Ui.Popups);
            Assert.Equal("SHOW_CONFIRM", popup.EventName);
            Assert.Equal("setting.GraphicSetting", popup.Data["title"]);
            Assert.Equal("setting.GraphicSettingMsg", popup.Data["msg"]);
            Assert.Equal("menu.ConfirmButton", popup.Data["buttonLabelYes"]);   // the keys PopupConfirm.setLabels reads
            Assert.Equal("menu.CancelButton", popup.Data["buttonLabelNo"]);
            Assert.Equal(true, popup.Data["hideCloseButton"]);
            Assert.True(popup.HasCallback);
            Assert.Equal(FakePopupPriority.AtEnd, popup.Priority);
            Assert.Empty(rig.Host.Exits);
            popup.Answer(true);
            Assert.Equal(new[] { "performance relaunch" }, rig.Host.Exits);

            // Once: the next return to the grid shows nothing.
            rig.Ui.Tap("btn_forum");
            rig.Host.Drain();
            rig.Ui.Back();
            rig.Host.Drain();
            Assert.Single(rig.Ui.Popups);
        }

        [Fact]
        public void NoKeepsTheChoiceAndAFailedPostTriesAgainAtTheNextReturn()
        {
            var rig = new Rig();
            rig.OpenPage();
            rig.Ui.Select(rig.Ui.Row("mcvs.graphicsProfile"), 3);
            rig.Host.CanPost = false;
            rig.Ui.Back();
            Assert.True(rig.State.RestartPending);
            rig.Host.CanPost = true;
            PerformancePage.ShowRestartPromptIfPending(rig.Host, rig.State);
            rig.Host.Drain();
            FakePopup popup = Assert.Single(rig.Ui.Popups);
            popup.Answer(false);
            Assert.Empty(rig.Host.Exits);
            Assert.Equal("ultra", rig.Settings.GraphicsProfile);
            Assert.Contains("settings menu: restart prompt: later", rig.Lines());
            PerformancePage.ShowRestartPromptIfPending(null, rig.State);
            PerformancePage.ShowRestartPromptIfPending(rig.Host, null);
        }

        // ------------------------------------------------------------------ R3-A

        [Fact]
        public void TheSpikeLineIsLoggedOncePerRegistry()
        {
            var rig = new Rig();
            rig.OpenPage();
            rig.Ui.Back();
            rig.Ui.Tap("btn_forum");
            rig.Host.Drain();
            Assert.Equal(new[] { "settings menu: R3-A spike: _arOptions not readable; alpha write inside the native ok" },
                         rig.Host.Lines("settings menu: R3-A spike"));
            Assert.Equal(2, rig.Lines().Count(l => l == "settings menu: performance page: 5 rows"));
        }

        [Fact]
        public void GetUserPreferenceAnswersOnlyForOurRows()
        {
            var rig = new Rig(wire: false);
            rig.Engine.Samples = 2;
            rig.Engine.Scale = 1f;
            rig.Settings.Store(SettingsSchema.GraphicsLowResMapsKey, "on");
            rig.Settings.Store("graphics.profile", "mid");
            Assert.Equal(1f, PerformancePage.OnGetUserPreference(rig.Host, rig.State, "mcvs.graphicsProfile"));
            Assert.Equal(1f, PerformancePage.OnGetUserPreference(rig.Host, rig.State, "mcvs.msaa"));
            Assert.Equal(1f, PerformancePage.OnGetUserPreference(rig.Host, rig.State, "mcvs.renderScale"));
            Assert.Equal(1f, PerformancePage.OnGetUserPreference(rig.Host, rig.State, "mcvs.lowResMaps"));
            Assert.Null(PerformancePage.OnGetUserPreference(rig.Host, rig.State, "MaxFramerate"));
            Assert.Null(PerformancePage.OnGetUserPreference(rig.Host, rig.State, "InvertCameraX"));
            Assert.Null(PerformancePage.OnGetUserPreference(rig.Host, rig.State, null));
            rig.Host.PerformanceSetting = false;
            Assert.Null(PerformancePage.OnGetUserPreference(rig.Host, rig.State, "mcvs.msaa"));
        }

        // ------------------------------------------------------------------ never throws

        [Fact]
        public void NothingThrowsWithoutAHostAUiOrAState()
        {
            var host = new FakeSettingsMenuHost { Ui = null };
            var state = new SettingsMenuState { PerformanceMode = true };
            PerformancePage.UseBackEnd(state, new FakePageBackEnd());
            PerformancePage.Transform(host, state);
            PerformancePage.Transform(null, state);
            PerformancePage.Transform(host, null);
            PerformancePage.OnSetUserPreference(null, state, "mcvs.renderScale", 0.5f);
            PerformancePage.OnSetUserPreference(host, state, null, null);
            PerformancePage.OnSaveUserPreferences(host, state);
            PerformancePage.OnSaveUserPreferences(null, null);
            Assert.Null(PerformancePage.OnGetUserPreference(null, state, "mcvs.msaa"));
            Assert.Null(PerformancePage.CanChangeGraphicSetting(host, state));
            Assert.Equal(1, host.Pending);
            host.Drain();
            Assert.False(state.Transforming);
            Assert.Contains("settings menu: performance page not built (no UI)", host.Lines("settings menu: "));
            Assert.Empty(host.Settings.Saves);
        }
    }

    /// <summary>The engine statics behind the page's auto values, and what the back end applied.</summary>
    internal sealed class FakePageEngine : IEngineGraphics
    {
        public float? Scale = 0.7f;
        public int? Samples = 0;
        public bool? LowRes = false;
        public readonly List<string> Writes = new List<string>();

        public float? RenderTargetSizeScale { get { return Scale; } }

        public bool SetRenderTargetSizeScale(float scale)
        {
            Scale = scale;
            Writes.Add("scale=" + scale.ToString("0.###", CultureInfo.InvariantCulture));
            return true;
        }

        public int? AntialiasingSamples { get { return Samples; } }

        public bool? LowResMaps { get { return LowRes; } }

        public bool SetLowResMaps(bool value)
        {
            LowRes = value;
            Writes.Add("lowres=" + value);
            return true;
        }

        public IList<string> GlobalExcludeLayers { get { return null; } }

        public IList<int> FpsValues { get { return null; } }

        public int? MaxFramerateIndex { get { return null; } }

        public bool SetMaxFramerateIndex(int index)
        {
            return false;
        }
    }

    /// <summary>
    /// PerformancePrefs' page-facing members as the design states them (§3.4, T-D6): stores through the settings, applies to the engine,
    /// a new GRAPHICS or ANTIALIASING value sets RestartPending. Every call is in <see cref="Calls"/>.
    /// </summary>
    internal sealed class FakePageBackEnd : IPerformanceBackEnd
    {
        public readonly FakePageEngine EngineFake = new FakePageEngine();
        public readonly List<string> Calls = new List<string>();

        public IEngineGraphics Engine { get { return EngineFake; } }

        public int SliderOf(float scale)
        {
            return (int)Math.Round(Math.Max(0, Math.Min(1, (scale - 0.4) / 0.6)) * 100);
        }

        public bool SetGraphicsProfile(IPerformanceSettings settings, SettingsMenuState state, string choice)
        {
            Calls.Add("profile=" + choice);
            return Restart(settings, state, "graphics.profile", settings.GraphicsProfile, choice);
        }

        public bool SetMsaa(IPerformanceSettings settings, SettingsMenuState state, string choice)
        {
            Calls.Add("msaa=" + choice);
            return Restart(settings, state, SettingsSchema.GraphicsMsaaKey, settings.Msaa, choice);
        }

        public bool SetRenderScale(IEngineGraphics engine, IPerformanceSettings settings, string value)
        {
            Calls.Add("scale=" + value);
            if (!settings.Save(SettingsSchema.GraphicsRenderScaleKey, value)) return false;
            int v = int.Parse(value, CultureInfo.InvariantCulture);
            return engine.SetRenderTargetSizeScale(v > 97 ? 1f : 0.4f + 0.6f * v / 100f);
        }

        public bool SetLowResMaps(IEngineGraphics engine, IPerformanceSettings settings, string choice)
        {
            Calls.Add("lowres=" + choice);
            return settings.Save(SettingsSchema.GraphicsLowResMapsKey, choice) && engine.SetLowResMaps(choice == "on");
        }

        private static bool Restart(IPerformanceSettings settings, SettingsMenuState state, string key, string before, string choice)
        {
            if (!settings.Save(key, choice)) return false;
            if (before != choice) state.RestartPending = true;
            return true;
        }
    }
}
