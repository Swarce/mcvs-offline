using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Admin;
using McvsOffline.Embedded.Admin.SettingsMenu;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// The runtime native registry (docs/design/settings-menu.md §3.1, §4.1 WP-A: T-A1 to T-A5; skeptic E1, E4, E5) on the fake UI,
    /// whose native table has the engine's registration flags and dispatch rules and the game's own delegates. The steps the natives
    /// hand on (grid, page) are recorded here; their own behaviour is WP-B's and WP-C's.
    /// </summary>
    [Collection("GameHooks")]
    public class UiNativesTests
    {
        private readonly ITestOutputHelper output;

        public UiNativesTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        // ------------------------------------------------------------------ T-A1, T-A2: the registrations

        [Fact]
        public void AGameReRegistrationDropsOursAndOneEnsurePutsItBackLast()
        {
            // T-A1. Catches wrong flags and registering once only.
            var env = new Env();
            env.Natives.Ensure();
            env.AssertOursLast(UiNatives.AndroidNames);
            Assert.Single(env.Host.Lines("settings menu: natives registered (EnterSection, GoToForum, CanChangeGraphicSetting, " +
                                         "CanChangeMaxFramerate, SetUserPreference, SaveUserPreferences) on FakeFlashTree"));

            // Every component's OnStart again (a new level): the game's default flags clear each list, ours is gone.
            env.Ui.Game.RegisterAll(env.Ui);
            foreach (string name in UiNatives.AndroidNames) Assert.Equal(0, env.Ours(name));
            env.Natives.Ensure();
            env.AssertOursLast(UiNatives.AndroidNames);
            Assert.Equal(1, env.Natives.State.Registrations);
            Assert.Single(env.Host.Lines("settings menu: natives put back (EnterSection, GoToForum, CanChangeGraphicSetting, " +
                                         "CanChangeMaxFramerate, SetUserPreference, SaveUserPreferences; 1 so far)"));

            // GamePreferencesComponent.OnStop removes three names outright (the entries go, new FuncIDs come).
            int oldId = env.Ui.FuncIdOf("SaveUserPreferences");
            env.Ui.Game.StopPreferences(env.Ui);
            Assert.False(env.Ui.IsRegistered("SaveUserPreferences"));
            env.Natives.Ensure();
            Assert.True(env.Ui.IsRegistered("SaveUserPreferences"));
            Assert.NotEqual(oldId, env.Ui.FuncIdOf("SaveUserPreferences"));
            env.AssertOursLast(UiNatives.AndroidNames);
            Assert.Equal(2, env.Natives.State.Registrations);
            Assert.Single(env.Host.Lines("settings menu: natives put back (SetUserPreference, SaveUserPreferences; 2 so far)"));

            // The game's own delegates stay (appended natives), and run before ours.
            Assert.Equal(new object[] { env.Ui.Game, env.Slot("EnterSection") }, env.Ui.DelegatesOf("EnterSection").Select(d => d.Target).ToArray());
            env.Ui.OpenSettings();
            Assert.Equal(new[] { "settings_main_screen" }, env.Ui.Game.Sections);
        }

        [Fact]
        public void TenEnsuresLeaveExactlyOneOfOursPerNameAndTheGamesOwn()
        {
            // T-A2. Catches replaceOnlyMatchingDelegate misuse and growing lists.
            var env = new Env();
            for (int i = 0; i < 10; i++) env.Natives.Ensure();
            foreach (string name in UiNatives.AndroidNames)
            {
                Assert.Equal(1, env.Ours(name));
                // GoToForum is ours alone (E4); every other name keeps the game's delegate before ours.
                Assert.Equal(name == "GoToForum" ? 1 : 2, env.Ui.DelegatesOf(name).Count);
            }
            Assert.Equal(0, env.Natives.State.Registrations);   // nothing had to be put back
            Assert.Single(env.Host.Lines("settings menu: natives registered"));
            Assert.Empty(env.Host.Lines("settings menu: natives put back"));
        }

        [Fact]
        public void WithoutAReadableTableEveryTickRegistersAgainAndTheFlagsKeepOne()
        {
            // The private table missing (a renamed field): the flags alone must keep one delegate of ours per name, the game's
            // delegates stay, nothing is counted as put back.
            var table = new FakeNativeTable();
            var game = new FakeGameNatives();
            game.RegisterClient(table);
            game.RegisterPreferences(table);
            table.OverrideNativeFunction("EnterSection", new Action<string>(game.NativeEnterSection));
            var ui = new OpaqueUi(table);
            table.OverrideNativeFunction("GoToForum", new Action(ui.GoToForum));
            var opaqueHost = new OpaqueHost(new FakeSettingsMenuHost(), ui);
            var natives = new UiNatives(() => opaqueHost, new SettingsMenuState());
            Assert.Null(UiNatives.Delegates(ui, "EnterSection"));
            for (int i = 0; i < 10; i++) natives.Ensure();
            foreach (string name in UiNatives.AndroidNames)
            {
                Assert.Equal(1, table.CountWithTarget(name, natives.DelegateOf(name).Target));
                Assert.Same(natives.DelegateOf(name).Target, table.DelegatesOf(name).Last().Target);
                Assert.Equal(name == "GoToForum" ? 1 : 2, table.DelegatesOf(name).Count);
            }
            Assert.Equal(0, natives.State.Registrations);
            table.Invoke("GoToForum");
            Assert.Equal(1, ui.ForumCalls);   // our delegate calls the game's body on the UI it is registered on
        }

        [Fact]
        public void TheTableIsReadFromTheEnginesFieldShape()
        {
            var env = new Env();
            env.Natives.Ensure();
            IList<Delegate> list = UiNatives.Delegates(env.Ui, "CanChangeMaxFramerate");
            Assert.Equal(env.Ui.DelegatesOf("CanChangeMaxFramerate").ToArray(), list.ToArray());
            Assert.Empty(UiNatives.Delegates(env.Ui, "NoSuchNative"));
            Assert.True(UiNatives.IsTableShape(typeof(Dictionary<string, ShapedEntry>)));
            Assert.False(UiNatives.IsTableShape(typeof(Dictionary<int, ShapedEntry>)));
            Assert.False(UiNatives.IsTableShape(typeof(Dictionary<string, string>)));
            Assert.False(UiNatives.IsTableShape(typeof(List<Delegate>)));
        }

        [Fact]
        public void ANewClientUiGetsEveryNameAndTheGameThreadUiIsTheOneUsed()
        {
            // E5: the table is per MenuComponent; the host's client UI is the one registered on, and a new one gets everything.
            var env = new Env();
            env.Natives.Ensure();
            FakeFlashTree first = env.Ui;
            var second = new FakeFlashTree();
            env.Host.Ui = second;
            env.Natives.Ensure();
            Assert.Same(second, env.Natives.RegisteredUi);
            foreach (string name in UiNatives.AndroidNames) Assert.Equal(1, second.CountWithTarget(name, env.Slot(name)));
            Assert.Equal(2, env.Host.Lines("settings menu: natives registered").Count);
            Assert.Equal(0, env.Natives.State.Registrations);

            // The tile on the new UI runs the new UI's own GoToForum, not the old one's.
            second.OpenSettings();
            Assert.True(second.Tap("btn_forum"));
            Assert.Equal(1, second.Game.ForumCalls);
            Assert.Equal(0, first.Game.ForumCalls);

            // No client UI yet: nothing happens, nothing is logged.
            env.Host.Ui = null;
            env.Natives.Ensure();
            Assert.Same(second, env.Natives.RegisteredUi);
            Assert.Empty(env.Host.Lines("settings menu: natives not registered"));
        }

        [Fact]
        public void OnPcOnlyEnterSectionIsRegisteredAndSteamsNativesStayTheGames()
        {
            var env = new Env(windows: true);
            env.Natives.Ensure();
            Assert.Equal(new[] { "EnterSection" }, UiNatives.PcNames);
            Assert.Equal(1, env.Ours("EnterSection"));
            foreach (string name in UiNatives.AndroidNames.Where(n => n != "EnterSection"))
            {
                Assert.Equal(0, env.Ours(name));
                Assert.Single(env.Ui.DelegatesOf(name));
            }
            Assert.Single(env.Host.Lines("settings menu: natives registered (EnterSection) on FakeFlashTree"));

            // Steam's own tiles: FORUM is the game's, PERFORMANCE opens Steam's page; the grid step still runs on every return.
            env.Ui.OpenSettings();
            Assert.Equal(1, env.Steps.Count("Schedule"));
            Assert.True(env.Ui.Tap("btn_forum"));
            Assert.Equal(1, env.Ui.Game.ForumCalls);
            Assert.True(env.Ui.Tap("btn_performance"));
            Assert.Equal("menu_gameplaySettings", env.Ui.Top.Name);
            Assert.Equal(0, env.Steps.Count("Open"));
            Assert.Equal(0, env.Steps.Count("CanChangeGraphicSetting"));
            Assert.Contains("MaxFramerate", env.Ui.RowTypes());
        }

        [Fact]
        public void ANativeThatFailsToRegisterIsLoggedOncePerReason()
        {
            var env = new Env();
            env.Host.Ui = null;
            var broken = new BrokenUi();
            var brokenHost = new OpaqueHost(env.Host, broken);
            var natives = new UiNatives(() => brokenHost, new SettingsMenuState());
            for (int i = 0; i < 3; i++) natives.Ensure();
            Assert.Single(env.Host.Lines("settings menu: natives not registered (MissingMethodException: "));
        }

        // ------------------------------------------------------------------ T-A3: never throws into Flash

        [Fact]
        public void AStepThatThrowsAnswersNullAndTheDispatchGoesOn()
        {
            // T-A3. Catches P4 violations: a throw would reach Flash as MenuInvokationException (Fatal in the game's log).
            var env = new Env();
            env.Steps.ThrowEverywhere = true;
            env.Natives.Ensure();
            env.Natives.State.PerformanceMode = true;

            env.Ui.OpenSettings();   // EnterSection: every step throws
            Assert.False(env.Natives.State.PerformanceMode);           // the page ends even when LeaveMode throws
            Assert.Equal(1, env.Steps.Count("Schedule"));               // and the grid step is still tried after it
            Assert.Equal(1, env.Steps.Count("ShowRestartPromptIfPending"));
            Assert.Equal(true, env.Ui.CallFromFlash("CanChangeGraphicSetting", true));   // ours null: the game's answer stands
            Assert.Equal(true, env.Ui.CallFromFlash("CanChangeMaxFramerate", true));
            env.Ui.CallFromFlash("SetUserPreference", null, "InvertCameraX", true);
            env.Ui.CallFromFlash("SaveUserPreferences", null);
            env.Natives.State.TileActive = true;
            Assert.True(env.Ui.Tap("btn_forum"));
            Assert.Equal(0, env.Ui.Game.ForumCalls);   // the tile's page step threw: still not the forum

            // The game's own delegates ran as usual.
            Assert.Contains("InvertCameraX=True", env.Ui.Game.Writes);
            Assert.Equal(1, env.Ui.Game.Saves);

            // Again: one line per native, not one per call.
            env.Ui.Back();
            env.Ui.OpenSettings();
            env.Ui.CallFromFlash("CanChangeMaxFramerate", true);
            foreach (string native in new[] { "EnterSection (page)", "EnterSection (grid)", "EnterSection (restart prompt)", "CanChangeGraphicSetting",
                                              "CanChangeMaxFramerate", "SetUserPreference", "SaveUserPreferences", "GoToForum" })
            {
                Assert.Single(env.Host.Lines("settings menu: " + native + " failed (InvalidOperationException: step " ));
            }
        }

        [Fact]
        public void EveryDelegateBodyCatchesEverythingWhenCalledDirectly()
        {
            var env = new Env();
            env.Steps.ThrowEverywhere = true;
            env.Natives.State.TileActive = true;
            env.Natives.EnterSection("settings_main_screen");
            Assert.Null(env.Natives.CanChangeGraphicSetting());
            Assert.Null(env.Natives.CanChangeMaxFramerate());
            env.Natives.SetUserPreference("mcvs.msaa", true);
            env.Natives.SaveUserPreferences();
            env.Natives.GoToForum();
            // A host whose members throw (it never should): still nothing escapes.
            var natives = new UiNatives(() => { throw new InvalidOperationException("host"); }, new SettingsMenuState());
            natives.Ensure();
            natives.EnterSection("settings_main_screen");
            natives.GoToForum();
            Assert.Null(natives.CanChangeMaxFramerate());
            Assert.False(natives.Tick());
        }

        // ------------------------------------------------------------------ T-A4: the FORUM / PERFORMANCE tile (E4)

        [Fact]
        public void TheTileRunsTheGamesForumOnceOrThePageOnlyNeverBoth()
        {
            // T-A4. Catches double actions and the forum opening on a PERFORMANCE tap.
            var env = new Env();
            env.Natives.Ensure();
            // Replacing from the first registration on: no 2 s window where a tap runs the game's delegate as well (E4).
            Assert.Equal(new object[] { env.Slot("GoToForum") }, env.Ui.DelegatesOf("GoToForum").Select(d => d.Target).ToArray());

            env.Ui.OpenSettings();
            Assert.False(env.Natives.State.TileActive);
            Assert.True(env.Ui.Tap("btn_forum"));
            Assert.Equal(1, env.Ui.Game.ForumCalls);   // FORUM: the game's own body, exactly once
            Assert.Equal(0, env.Steps.Count("Open"));

            env.Natives.State.TileActive = true;       // the grid step re-dressed the tile
            Assert.True(env.Ui.Tap("btn_forum"));
            Assert.Equal(1, env.Ui.Game.ForumCalls);   // PERFORMANCE: ours only
            Assert.Equal(1, env.Steps.Count("Open"));
            Assert.Equal(1, env.Ui.Pushed.Count(p => p == "menu_gameplaySettings"));

            // The switch read on the tap: off is the game's forum again, even with the tile still showing PERFORMANCE.
            env.Host.PerformanceSetting = false;
            Assert.True(env.Ui.Tap("btn_forum"));
            Assert.Equal(2, env.Ui.Game.ForumCalls);
            Assert.Equal(1, env.Steps.Count("Open"));

            // After the game re-registers its GoToForum (UIComponent.OnStart), the next ensure takes the name back.
            env.Host.PerformanceSetting = true;
            env.Ui.Game.RegisterUi(env.Ui);
            env.Natives.Ensure();
            Assert.Equal(new object[] { env.Slot("GoToForum") }, env.Ui.DelegatesOf("GoToForum").Select(d => d.Target).ToArray());
            Assert.True(env.Ui.Tap("btn_forum"));
            Assert.Equal(2, env.Ui.Game.ForumCalls);
            Assert.Equal(2, env.Steps.Count("Open"));
        }

        [Fact]
        public void TheTileOpensTheRealPageStepWhenWpCIsIn()
        {
            // The same tap through the default steps: PerformancePage.Open posts exactly one push of Gameplay Settings. Until WP-C's
            // body is merged, Open is a skeleton and this test only checks that our delegate keeps the forum shut.
            var env = new Env(recordSteps: false);
            env.Natives.Ensure();
            env.Ui.OpenSettings();
            env.Natives.State.TileActive = true;
            Assert.True(env.Ui.Tap("btn_forum"));
            env.Host.Drain();
            Assert.Equal(0, env.Ui.Game.ForumCalls);
            if (!PageIsImplemented())
            {
                output.WriteLine("PerformancePage.Open is the WP0 skeleton: the push is checked once WP-C is merged");
                return;
            }
            Assert.Equal(1, env.Ui.Pushed.Count(p => p == "menu_gameplaySettings"));
            Assert.True(env.Natives.State.PerformanceMode);
        }

        // ------------------------------------------------------------------ T-A5: no host

        [Fact]
        public void WithoutAHostEveryDelegateAnswersNullAndNothingIsPosted()
        {
            // T-A5. Registered while the host was up; then AdminHooks.Host is null.
            var env = new Env();
            env.Natives.Ensure();
            env.HostUp = false;
            env.Natives.State.TileActive = true;
            env.Natives.State.RestartPending = true;
            env.Ui.OpenSettings();
            Assert.True(env.Natives.State.TileActive);   // EnterSection did nothing
            Assert.Equal(true, env.Ui.CallFromFlash("CanChangeGraphicSetting", true));
            Assert.Equal(true, env.Ui.CallFromFlash("CanChangeMaxFramerate", true));
            Assert.Null(env.Natives.CanChangeGraphicSetting());
            Assert.Null(env.Natives.CanChangeMaxFramerate());
            env.Ui.CallFromFlash("SetUserPreference", null, "mcvs.msaa", true);
            env.Ui.CallFromFlash("SaveUserPreferences", null);
            Assert.True(env.Ui.Tap("btn_forum"));
            Assert.Equal(0, env.Steps.Total);
            Assert.Equal(0, env.Host.Pending);
            Assert.False(env.Natives.Tick());
            Assert.Equal(0, env.Host.Pending);
            Assert.Empty(env.Ui.Popups);
            // The FORUM tile is the stock one then: the game's own body, as before the registry existed.
            Assert.Equal(1, env.Ui.Game.ForumCalls);

            // Never registered without a host either.
            var fresh = new FakeFlashTree();
            env.Host.Ui = fresh;
            var natives = new UiNatives(() => null, new SettingsMenuState());
            natives.Ensure();
            foreach (string name in UiNatives.AndroidNames) Assert.Equal(0, fresh.CountWithTarget(name, natives.DelegateOf(name).Target));
        }

        // ------------------------------------------------------------------ EnterSection and the other routes

        [Fact]
        public void TheMainGridClearsTheTileAndThePageAndRoutesItsSteps()
        {
            var env = new Env();
            env.Natives.Ensure();
            env.Natives.State.TileActive = true;
            env.Natives.State.PerformanceMode = true;
            env.Ui.OpenSettings();
            Assert.False(env.Natives.State.TileActive);
            Assert.True(env.Natives.State.TileEverActive);
            Assert.False(env.Natives.State.PerformanceMode);
            Assert.Equal(new[] { "LeaveMode", "Schedule", "ShowRestartPromptIfPending" }, env.Steps.Calls.ToArray());
            Assert.All(env.Steps.Hosts, h => Assert.Same(env.Host, h));
            Assert.All(env.Steps.States, s => Assert.Same(env.Natives.State, s));

            // Other sections are not the grid: Gameplay Settings (and the page) and the in-match panel.
            env.Steps.Calls.Clear();
            env.Natives.State.PerformanceMode = true;
            env.Natives.State.TileActive = true;
            Assert.True(env.Ui.Tap("btn_general"));
            env.Natives.EnterSection("pvp_settings_screen");
            env.Natives.EnterSection(null);
            env.Natives.EnterSection("SETTINGS_MAIN_SCREEN");
            Assert.True(env.Natives.State.PerformanceMode);
            Assert.True(env.Natives.State.TileActive);
            Assert.DoesNotContain("Schedule", env.Steps.Calls);
            Assert.DoesNotContain("LeaveMode", env.Steps.Calls);

            // Back to the grid (Gameplay Settings saves on its way out): once more, each time.
            env.Steps.Calls.Clear();
            env.Ui.Back();
            Assert.Equal(new[] { "OnSaveUserPreferences", "LeaveMode", "Schedule", "ShowRestartPromptIfPending" }, env.Steps.Calls.ToArray());
        }

        [Fact]
        public void TheCanChangeAnswersWinTheDispatchBecauseOursIsLast()
        {
            var env = new Env();
            env.Steps.CanChange = false;
            env.Natives.Ensure();
            Assert.Equal(false, env.Ui.CallFromFlash("CanChangeGraphicSetting", true));
            Assert.Equal(false, env.Ui.CallFromFlash("CanChangeMaxFramerate", true));

            // The game re-registers (a new level): its true until the next ensure, then ours again.
            env.Ui.Game.RegisterClient(env.Ui);
            env.Ui.Game.RegisterPreferences(env.Ui);
            Assert.Equal(true, env.Ui.CallFromFlash("CanChangeMaxFramerate", true));
            env.Natives.Ensure();
            Assert.Equal(false, env.Ui.CallFromFlash("CanChangeGraphicSetting", true));
            Assert.Equal(false, env.Ui.CallFromFlash("CanChangeMaxFramerate", true));

            // Null leaves the game's answer: Gameplay Settings keeps both rows.
            env.Steps.CanChange = null;
            Assert.Equal(true, env.Ui.CallFromFlash("CanChangeGraphicSetting", true));
            Assert.Equal(true, env.Ui.CallFromFlash("CanChangeMaxFramerate", true));
            env.Ui.OpenSettings();
            Assert.True(env.Ui.Tap("btn_general"));
            Assert.Contains("MaxFramerate", env.Ui.RowTypes());
            Assert.Contains("GraphicSetting", env.Ui.RowTypes());

            // Boxed false through the real menu: AS adds neither row (R4 as WP-C decides it).
            env.Ui.Back();
            env.Steps.CanChange = false;
            Assert.True(env.Ui.Tap("btn_general"));
            Assert.Equal("menu_gameplaySettings", env.Ui.Top.Name);
            Assert.NotEmpty(env.Ui.RowTypes());
            Assert.DoesNotContain("MaxFramerate", env.Ui.RowTypes());
            Assert.DoesNotContain("GraphicSetting", env.Ui.RowTypes());
        }

        [Fact]
        public void PreferenceWritesReachThePageButNotWhileItTransforms()
        {
            // E1: the transform's own displayList/value= writes fire onListSelectorChange/onBarScroll inside our call. While
            // Transforming is set, those natives reach no step at all; the game's own delegate is not ours to stop.
            var env = new Env();
            env.Natives.Ensure();
            env.Natives.State.Transforming = true;
            env.Ui.CallFromFlash("SetUserPreference", null, "mcvs.msaa", true);
            env.Ui.CallFromFlash("SetUserPreference", null, "mcvs.renderScale", 0.75);
            env.Ui.CallFromFlash("SaveUserPreferences", null);
            env.Natives.SetUserPreference("mcvs.lowResMaps", false);
            env.Natives.SaveUserPreferences();
            Assert.Equal(0, env.Steps.Total);
            Assert.Equal(1, env.Ui.Game.Saves);

            env.Natives.State.Transforming = false;
            env.Ui.CallFromFlash("SetUserPreference", null, "mcvs.renderScale", 0.75);
            env.Ui.CallFromFlash("SaveUserPreferences", null);
            Assert.Equal(new[] { "OnSetUserPreference", "OnSaveUserPreferences" }, env.Steps.Calls.ToArray());
            // As the game's dispatch converts it: an AS Number reaches the object parameter as float.
            Assert.Equal("mcvs.renderScale", env.Steps.Keys.Single());
            Assert.Equal(0.75f, env.Steps.Values.Single());
        }

        [Fact]
        public void TheDefaultStepsAreTheLayoutAndThePage()
        {
            var steps = new MenuSteps();
            Assert.Equal(typeof(SettingsMenuLayout).GetMethod("Schedule"), steps.Schedule.Method);
            Assert.Equal(typeof(PerformancePage).GetMethod("LeaveMode"), steps.LeaveMode.Method);
            Assert.Equal(typeof(PerformancePage).GetMethod("ShowRestartPromptIfPending"), steps.ShowRestartPromptIfPending.Method);
            Assert.Equal(typeof(PerformancePage).GetMethod("Open"), steps.Open.Method);
            Assert.Equal(typeof(PerformancePage).GetMethod("CanChangeGraphicSetting"), steps.CanChangeGraphicSetting.Method);
            Assert.Equal(typeof(PerformancePage).GetMethod("CanChangeMaxFramerate"), steps.CanChangeMaxFramerate.Method);
            Assert.Equal(typeof(PerformancePage).GetMethod("OnSetUserPreference"), steps.OnSetUserPreference.Method);
            Assert.Equal(typeof(PerformancePage).GetMethod("OnSaveUserPreferences"), steps.OnSaveUserPreferences.Method);
            var natives = new UiNatives(() => null, new SettingsMenuState());
            Assert.Equal(typeof(PerformancePage).GetMethod("Open"), natives.Steps.Open.Method);

            // Every name has its own target object (with true, true the engine throws on two delegates with one Target).
            object[] targets = UiNatives.AndroidNames.Select(n => natives.DelegateOf(n).Target).ToArray();
            Assert.Equal(targets.Length, targets.Distinct().Count());
            Assert.All(targets, t => Assert.NotSame(natives, t));
            Assert.Null(natives.DelegateOf("GetUserPreference"));
            // The delegates have the natives' own signatures (the dispatch converts the AS arguments to them).
            Assert.IsType<Action<string>>(natives.DelegateOf("EnterSection"));
            Assert.IsType<Action>(natives.DelegateOf("GoToForum"));
            Assert.IsType<Func<object>>(natives.DelegateOf("CanChangeGraphicSetting"));
            Assert.IsType<Func<object>>(natives.DelegateOf("CanChangeMaxFramerate"));
            Assert.IsType<Action<string, object>>(natives.DelegateOf("SetUserPreference"));
            Assert.IsType<Action>(natives.DelegateOf("SaveUserPreferences"));
        }

        // ------------------------------------------------------------------ the timer and Start

        [Fact]
        public void ATickPostsOneEnsureAndAStalledGameThreadGetsNoPile()
        {
            var env = new Env();
            Assert.True(env.Natives.Tick());
            Assert.False(env.Natives.Tick());   // the first has not run yet
            Assert.Equal(1, env.Host.Pending);
            Assert.Equal(0, env.Ours("EnterSection"));   // posted, not run inline
            env.Host.Drain();
            Assert.Equal(1, env.Ours("EnterSection"));
            Assert.True(env.Natives.Tick());
            env.Host.Drain();
            Assert.Equal(1, env.Ours("EnterSection"));

            // No game thread: nothing pending, and the next tick tries again.
            env.Host.CanPost = false;
            Assert.False(env.Natives.Tick());
            env.Host.CanPost = true;
            Assert.True(env.Natives.Tick());
            Assert.Equal(1, env.Host.Pending);
            env.Natives.Dispose();
            Assert.False(env.Natives.Tick());
            Assert.Throws<ObjectDisposedException>(() => env.Natives.StartTimer());
        }

        [Fact]
        public void TheTimerPostsTheFirstEnsureAtOnce()
        {
            var env = new Env();
            env.Natives.StartTimer();
            env.Natives.StartTimer();   // once
            try
            {
                var clock = Stopwatch.StartNew();
                while (env.Host.Pending == 0 && clock.ElapsedMilliseconds < 1500) Thread.Sleep(10);
                Assert.True(clock.ElapsedMilliseconds < UiNatives.EnsureIntervalMs, "the first post waited for the period");
                Assert.Equal(1, env.Host.Pending);
            }
            finally
            {
                env.Natives.Dispose();
            }
            env.Host.Drain();
            Assert.Equal(1, env.Ours("EnterSection"));
        }

        [Fact]
        public void StartRunsTheRegistryForTheHostAndALeftOverOneDoesNothing()
        {
            var logs = new MemoryLogSink();
            McvsServer server = AdminFixtures.CreateServer(TestEnvironment.NewTempDir("sm-natives"), logs);
            FakeGame game = FakeGame.Install();
            var ui = new FakeFlashTree();
            UiNatives first = null;
            UiNatives second = null;
            try
            {
                FlashTreeUiExtensions.Ui = ui;
                var reflection = new GameReflection();
                Assert.True(reflection.Bind(typeof(FakeGameApplication), typeof(FakeProfile), typeof(FlashTreeUiExtensions)));
                var admin = new AdminHost(server.Context, new RecordingAdminPlatform(), reflection, null);
                Assert.Null(UiNatives.Start(null));
                AdminHooks.Host = admin;
                first = UiNatives.Start(admin);
                Assert.NotNull(first);
                Assert.Same(first, UiNatives.Current);
                Assert.Same(admin, first.Host);
                var clock = Stopwatch.StartNew();
                while (game.App.Context.Pending == 0 && clock.ElapsedMilliseconds < 1500) Thread.Sleep(10);
                game.App.Context.Drain();
                foreach (string name in UiNatives.AndroidNames) Assert.Equal(1, ui.CountWithTarget(name, first.DelegateOf(name).Target));
                List<string> lines = logs.Snapshot().Select(e => e.Message).ToList();
                Assert.DoesNotContain(lines, m => m.Contains("natives not started"));
                Assert.Single(lines, "settings menu: natives registered (EnterSection, GoToForum, CanChangeGraphicSetting, " +
                                     "CanChangeMaxFramerate, SetUserPreference, SaveUserPreferences) on FakeFlashTree (World unknown)");

                // Another host takes over (tests start several): the old registry is stopped and sees no host.
                var other = new AdminHost(server.Context, new RecordingAdminPlatform(), reflection, null);
                AdminHooks.Host = other;
                Assert.Null(first.Host);
                second = UiNatives.Start(other);
                Assert.Same(second, UiNatives.Current);
                Assert.False(first.Tick());
                AdminHooks.Host = null;
                Assert.Null(second.Host);
                Assert.False(second.Tick());
            }
            finally
            {
                if (first != null) first.Dispose();
                if (second != null) second.Dispose();
                Assert.Null(UiNatives.Current);
                AdminHooks.Host = null;
                FlashTreeUiExtensions.Ui = null;
                FakeGame.Uninstall();
                server.Dispose();
            }
        }

        // ------------------------------------------------------------------ helpers

        private static bool PageIsImplemented()
        {
            try
            {
                PerformancePage.Open(new FakeSettingsMenuHost(), new SettingsMenuState());
                return true;
            }
            catch (NotImplementedException)
            {
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>A fake UI and host (Android unless <c>windows</c>), a registry over them and recorded steps.</summary>
        private sealed class Env
        {
            public Env(bool windows = false, bool recordSteps = true)
            {
                Ui = new FakeFlashTree(windows);
                Host = new FakeSettingsMenuHost(Ui, windows);
                Natives = new UiNatives(() => HostUp ? Host : null, new SettingsMenuState());
                Steps = new RecordingSteps(Ui);
                if (recordSteps) Natives.Steps = Steps.Steps;
            }

            public FakeFlashTree Ui { get; }
            public FakeSettingsMenuHost Host { get; }
            public UiNatives Natives { get; }
            public RecordingSteps Steps { get; }
            public bool HostUp { get; set; } = true;

            public object Slot(string name)
            {
                return Natives.DelegateOf(name).Target;
            }

            public int Ours(string name)
            {
                return Ui.CountWithTarget(name, Slot(name));
            }

            public void AssertOursLast(IEnumerable<string> names)
            {
                foreach (string name in names)
                {
                    IReadOnlyList<Delegate> list = Ui.DelegatesOf(name);
                    Assert.True(list.Count > 0, name + " has no delegates");
                    Assert.Same(Slot(name), list[list.Count - 1].Target);
                    Assert.Equal(1, Ui.CountWithTarget(name, Slot(name)));
                }
            }
        }

        /// <summary>The steps the natives hand on, recorded (and throwing or answering as the test sets).</summary>
        private sealed class RecordingSteps
        {
            private readonly FakeFlashTree ui;

            public RecordingSteps(FakeFlashTree ui = null)
            {
                this.ui = ui;
                Steps = new MenuSteps
                {
                    Schedule = (h, s) => Record("Schedule", h, s),
                    LeaveMode = s => { Record("LeaveMode", null, s); s.PerformanceMode = false; },
                    ShowRestartPromptIfPending = (h, s) => Record("ShowRestartPromptIfPending", h, s),
                    Open = (h, s) =>
                    {
                        Record("Open", h, s);
                        h.Post(() => { if (this.ui != null) this.ui.PushMenu("menu_gameplaySettings"); });
                        ((FakeSettingsMenuHost)h).Drain();
                    },
                    CanChangeGraphicSetting = (h, s) => { Record("CanChangeGraphicSetting", h, s); return CanChange; },
                    CanChangeMaxFramerate = (h, s) => { Record("CanChangeMaxFramerate", h, s); return CanChange; },
                    OnSetUserPreference = (h, s, k, v) => { Record("OnSetUserPreference", h, s); Keys.Add(k); Values.Add(v); },
                    OnSaveUserPreferences = (h, s) => Record("OnSaveUserPreferences", h, s),
                };
            }

            public MenuSteps Steps { get; }
            public bool ThrowEverywhere { get; set; }
            public object CanChange { get; set; }
            public List<string> Calls { get; } = new List<string>();
            public List<ISettingsMenuHost> Hosts { get; } = new List<ISettingsMenuHost>();
            public List<SettingsMenuState> States { get; } = new List<SettingsMenuState>();
            public List<string> Keys { get; } = new List<string>();
            public List<object> Values { get; } = new List<object>();

            public int Total { get { return Calls.Count; } }

            public int Count(string step)
            {
                return Calls.Count(c => c == step);
            }

            private void Record(string step, ISettingsMenuHost host, SettingsMenuState state)
            {
                Calls.Add(step);
                if (host != null) Hosts.Add(host);
                States.Add(state);
                if (ThrowEverywhere) throw new InvalidOperationException("step " + step);
            }
        }

        /// <summary>A UI whose native table the registry cannot read (no field of the engine's shape), forwarding to a fake table.</summary>
        private sealed class OpaqueUi
        {
            private readonly FakeNativeTable table;

            public OpaqueUi(FakeNativeTable table)
            {
                this.table = table;
            }

            public int ForumCalls { get; private set; }

            public object OverrideNativeFunction(string funcName, Delegate del, bool removePreviousOverrides = true, bool replaceOnlyMatchingDelegate = false)
            {
                return table.OverrideNativeFunction(funcName, del, removePreviousOverrides, replaceOnlyMatchingDelegate);
            }

            public void GoToForum()
            {
                ForumCalls++;
            }
        }

        /// <summary>A UI without OverrideNativeFunction (a renamed engine member).</summary>
        private sealed class BrokenUi
        {
        }

        /// <summary>The fake host with another object as its client UI.</summary>
        private sealed class OpaqueHost : ISettingsMenuHost
        {
            private readonly FakeSettingsMenuHost inner;
            private readonly object ui;

            public OpaqueHost(FakeSettingsMenuHost inner, object ui)
            {
                this.inner = inner;
                this.ui = ui;
            }

            public bool SettingsMenuTrim { get { return inner.SettingsMenuTrim; } }
            public bool SettingsMenuPerformance { get { return inner.SettingsMenuPerformance; } }
            public bool IsWindows { get { return inner.IsWindows; } }
            public Log Log { get { return inner.Log; } }
            public IPerformanceSettings PerformanceSettings { get { return inner.PerformanceSettings; } }

            public object ClientUi()
            {
                return ui;
            }

            public bool Post(Action action)
            {
                return inner.Post(action);
            }

            public bool Exit(string reason, bool relaunch)
            {
                return inner.Exit(reason, relaunch);
            }
        }

        private sealed class ShapedEntry
        {
            public List<Delegate> Delegates = new List<Delegate>();
        }
    }

    /// <summary>Game.UISceneExtentions.GetUI(World) for GameReflection over the fake game, answering a <see cref="FakeFlashTree"/>.</summary>
    internal static class FlashTreeUiExtensions
    {
        public static FakeFlashTree Ui;

        public static FakeFlashTree GetUI(FakeWorld world)
        {
            return world == null ? null : Ui;
        }
    }
}
