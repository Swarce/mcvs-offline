using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// The fakes behave as the game does (docs/design/settings-menu.md §4.1): the native table's dispatch and registration rules
    /// (MenuComponent.cs:765-924), the Settings panel frames and setup(), and Gameplay Settings' rows and change handlers (MGS:215-576),
    /// including the CHANGE events a host's own writes fire (skeptic E1). WP-A to WP-D build their tests on these.
    /// </summary>
    public class SettingsMenuFakesTests
    {
        // ------------------------------------------------------------------ the native table

        private sealed class Owner
        {
            public readonly List<string> Calls = new List<string>();

            public object Answer(object value)
            {
                Calls.Add("answer");
                return value;
            }
        }

        [Fact]
        public void EveryDelegateRunsAndTheLastNonNullAnswerWins()
        {
            var table = new FakeNativeTable();
            var calls = new List<string>();
            table.OverrideNativeFunction("CanChangeMaxFramerate", (Func<bool>)(() => { calls.Add("game"); return true; }));
            table.OverrideNativeFunction("CanChangeMaxFramerate", (Func<object>)(() => { calls.Add("ours"); return null; }), false);
            Assert.Equal(true, table.Invoke("CanChangeMaxFramerate"));   // null keeps the game's answer
            table.OverrideNativeFunction("CanChangeMaxFramerate", (Func<object>)(() => { calls.Add("last"); return false; }), false);
            Assert.Equal(false, table.Invoke("CanChangeMaxFramerate"));  // a boxed false wins
            Assert.Equal(new[] { "game", "ours", "game", "ours", "last" }, calls);
            Assert.Null(table.Invoke("NotRegistered"));
            Assert.Equal(true, table.CallFromFlash("NotRegistered", true));   // the AS stub's own answer
        }

        [Fact]
        public void RegistrationFlagsWorkAsTheEnginesDo()
        {
            var table = new FakeNativeTable();
            var game = new Owner();
            var ours = new Owner();
            table.OverrideNativeFunction("X", new Func<object, object>(game.Answer));
            int id = table.FuncIdOf("X");
            // (true, true): only the delegate with our Target goes, ours is appended last.
            table.OverrideNativeFunction("X", new Func<object, object>(ours.Answer), true, true);
            table.OverrideNativeFunction("X", new Func<object, object>(ours.Answer), true, true);
            Assert.Equal(new object[] { game, ours }, table.DelegatesOf("X").Select(d => d.Target).ToArray());
            // (false, _): appends, so two with our Target; the next (true, true) throws as SingleOrDefault does.
            table.OverrideNativeFunction("X", new Func<object, object>(ours.Answer), false, true);
            Assert.Throws<InvalidOperationException>(() => table.OverrideNativeFunction("X", new Func<object, object>(ours.Answer), true, true));
            // The game's default flags (true, false) clear the list: ours is gone (what a component's OnStart does).
            table.OverrideNativeFunction("X", new Func<object, object>(game.Answer));
            Assert.Equal(new object[] { game }, table.DelegatesOf("X").Select(d => d.Target).ToArray());
            Assert.Equal(id, table.FuncIdOf("X"));
            // Removal by name drops everything; registering again makes a new entry with a new FuncID.
            table.RemoveNativeFunctionOverride("X");
            Assert.False(table.IsRegistered("X"));
            Assert.Empty(table.DelegatesOf("X"));
            table.OverrideNativeFunction("X", new Func<object, object>(ours.Answer), true, true);
            Assert.NotEqual(id, table.FuncIdOf("X"));
            // Removal by delegate: from every entry, and an entry left empty goes.
            var only = new Func<object, object>(ours.Answer);
            table.OverrideNativeFunction("Y", only);
            table.RemoveNativeFunctionOverride((object)only);
            Assert.False(table.IsRegistered("Y"));
            Assert.False(table.IsRegistered("X"));   // same method and target: Delegate equality
        }

        [Fact]
        public void AThrowingDelegateStopsTheDispatch()
        {
            var table = new FakeNativeTable();
            int after = 0;
            table.OverrideNativeFunction("EnterSection", new Action<string>(s => { throw new InvalidOperationException("boom " + s); }));
            table.OverrideNativeFunction("EnterSection", new Action<string>(s => after++), false);
            var ex = Assert.Throws<FakeMenuInvocationException>(() => table.Invoke("EnterSection", "settings_main_screen"));
            Assert.Equal("EnterSection", ex.Native);
            Assert.Contains("boom settings_main_screen", ex.Message);
            Assert.Equal(0, after);
        }

        [Fact]
        public void ArgumentsArriveAsTheDelegateDeclaresThem()
        {
            var table = new FakeNativeTable();
            var seen = new List<object>();
            table.OverrideNativeFunction("SetUserPreference", new Action<string, object>((k, v) => seen.Add(v)));
            table.OverrideNativeFunction("ChangeGraphicSetting", new Action<int>(v => seen.Add(v)));
            table.Invoke("SetUserPreference", "MaxFramerate", 2);      // an AS int: a Number, so a float in an object parameter
            table.Invoke("SetUserPreference", "InvertCameraX", true);
            table.Invoke("SetUserPreference", "CameraSmoothing", 0.25);
            table.Invoke("ChangeGraphicSetting", 2f);
            table.Invoke("SetUserPreference", "Missing");             // a missing argument: null
            Assert.Equal(new object[] { 2f, true, 0.25f, 2, null }, seen.ToArray());
        }

        [Fact]
        public void TheGamesPreferencesIgnoreUnknownKeysAndMismatchedTypes()
        {
            var game = new FakeGameNatives();
            game.NativeSetUserPreference("mcvs.msaa", true);           // unknown: dropped
            game.NativeSetUserPreference("InvertCameraX", 1f);          // bool property, a number: dropped
            game.NativeSetUserPreference("MaxFramerate", 2f);           // numeric: converted to int
            game.NativeSetUserPreference("CameraSmoothing", 0.25f);
            Assert.Equal(new[] { "MaxFramerate=2", "CameraSmoothing=0.25" }, game.Writes);
            Assert.Equal(2, game.Preferences["MaxFramerate"]);
            Assert.Null(game.NativeGetUserPreference("mcvs.msaa"));
            Assert.Equal(1, game.NativeGetUserPreference("GraphicSetting"));
            Assert.Throws<NullReferenceException>(() => game.NativeSetUserPreference("InvertCameraX", null));
        }

        // ------------------------------------------------------------------ the Settings panel

        [Fact]
        public void AndroidOpensOnFrameThreeWithTheStockBottomRow()
        {
            var ui = new FakeFlashTree();
            ui.OpenSettings();
            FlashObject panel = ui.Panel;
            Assert.Equal(3, panel.Frame);
            Assert.Equal(new[]
            {
                "btn_close", "btn_forum", "btn_help", "btn_customize", "btn_audio", "btn_general", "btn_credits", "btn_language", "btn_social",
                "btn_more", "btn_customer", "btn_account",
            }, panel.Children.Select(c => c.Name).ToArray());
            Assert.Null(ui.Instance("btn_performance"));
            // AND-AS/MS:180-184 (absolute): CUSTOMER CARE, MORE GAMES, ABOUT.
            Assert.Equal(-564f, ui.Instance("btn_customer").Num("x"));
            Assert.Equal(-177f, ui.Instance("btn_more").Num("x"));
            Assert.Equal(208f, ui.Instance("btn_credits").Num("x"));
            Assert.True((bool)ui.Instance("btn_more").Get("visible"));
            Assert.Equal(6.35f, ui.Instance("btn_forum").Num("x"));
            Assert.Equal("setting.ForumButton", ui.Instance("btn_forum").Get("title"));
            Assert.Equal(465.35f, ui.Instance("btn_forum").Num("width"));
            // EnterSection comes first (MenuBase.onFocusIn), then setup(), whose last natives are two GetPlatformId.
            Assert.Equal("EnterSection", ui.Invoked.First());
            Assert.Equal(new[] { "GetPlatformId", "GetPlatformId" }, ui.Invoked.Skip(ui.Invoked.Count - 2));
            Assert.Equal(new[] { "settings_main_screen" }, ui.Game.Sections);
            Assert.Empty(ui.HostWrites);
            // Its taps: FORUM reaches C# (GoToForum), GAMEPLAY SETTINGS is pure Flash.
            Assert.True(ui.Tap("btn_forum"));
            Assert.Equal(1, ui.Game.ForumCalls);
            Assert.False(ui.Tap("btn_performance"));
        }

        [Theory]
        [InlineData(false, 1, 11)]
        [InlineData(false, 2, 6)]
        [InlineData(false, 4, 11)]
        [InlineData(false, 5, 9)]
        [InlineData(true, 2, 9)]
        [InlineData(true, 3, 11)]
        [InlineData(true, 4, 10)]
        [InlineData(true, 5, 8)]
        public void EveryFrameOfBothPanelsIsThere(bool steam, int frame, int instances)
        {
            var ui = new FakeFlashTree(steam);
            ui.OpenSettingsAtFrame(frame);
            Assert.Equal(frame, ui.Panel.Frame);
            Assert.Equal(instances, ui.Panel.Children.Count);
            Assert.Equal(FakeFlashTree.FrameInstances(steam, frame), ui.Panel.Children.Select(c => c.Name).ToList());
            Assert.Equal(frame == 2 ? "pvp_settings_screen" : "settings_main_screen", ui.Game.Sections.Single());
        }

        [Fact]
        public void TheInMatchPanelIsFrameTwo()
        {
            var ui = new FakeFlashTree { InGame = true };
            ui.OpenSettings();
            Assert.Equal(2, ui.Panel.Frame);
            Assert.Equal(new[] { "pvp_settings_screen" }, ui.Game.Sections);
            Assert.Null(ui.Instance("btn_forum"));
            var kayac = new FakeFlashTree { Kayac = true };
            kayac.OpenSettings();
            Assert.Equal(4, kayac.Panel.Frame);
            Assert.False((bool)kayac.Instance("btn_more").Get("visible"));
            Assert.Equal(22f, kayac.Instance("btn_credits").Num("x"));
        }

        [Fact]
        public void SteamMovesTheTilesAndDriftsCustomerCareAndAboutOnEveryReturn()
        {
            var ui = new FakeFlashTree(steam: true);
            ui.OpenSettings();
            Assert.Equal(1, ui.Panel.Frame);
            Assert.Equal(-948f, ui.Instance("btn_general").Num("x"));
            Assert.Equal(484f, ui.Instance("btn_performance").Num("x"));
            Assert.Equal(6.85f, ui.Instance("btn_social").Num("x"));
            Assert.False((bool)ui.Instance("btn_more").Get("visible"));
            float about = ui.Instance("btn_credits").Num("x");
            float customer = ui.Instance("btn_customer").Num("x");
            Assert.Equal(258.05f - 200.5f, about, 3);
            Assert.Equal(-613.9f + 200.5f, customer, 3);
            // The stock bug (PC-AS/MS:253-258): setup() runs again on every return from a submenu, and the writes are relative.
            Assert.True(ui.Tap("btn_general"));
            ui.Back();
            Assert.Equal(about - 200.5f, ui.Instance("btn_credits").Num("x"), 3);
            Assert.Equal(customer + 200.5f, ui.Instance("btn_customer").Num("x"), 3);
            Assert.Equal(new[] { "settings_main_screen", "settings_gameplay_screen", "settings_main_screen" }, ui.Game.Sections);
            // Steam's own PERFORMANCE tile pushes Gameplay Settings (the PC page).
            Assert.True(ui.Tap("btn_performance"));
            Assert.Equal("menu_gameplaySettings", ui.Top.Name);
        }

        [Fact]
        public void AHiddenOrMouseDisabledInstanceTakesNoTap()
        {
            var ui = new FakeFlashTree();
            ui.OpenSettings();
            ui.Instance("btn_forum").Members["visible"] = false;
            Assert.False(ui.Tap("btn_forum"));
            ui.Instance("btn_customer").Members["mouseEnabled"] = false;
            Assert.False(ui.Tap("btn_customer"));
            Assert.Equal(0, ui.Game.ForumCalls);
            Assert.Empty(ui.Taps);
        }

        // ------------------------------------------------------------------ Gameplay Settings

        private static readonly string[] StockAndroidRows =
        {
            "EnableAutoShoot", "IronSightMode", "GraphicSetting", "CameraSensitivity", "CameraSmoothing", "SprintSensitivity", "PlayerFov",
            "InvertCameraX", "InvertCameraY", "EnableAimAssist", "AimBehavior", "SingleTapExitIronSight", "DoubleTapHoldToIronSightFire",
            "AutoShootAlwaysActive", "AutoShootCancelsSprinting", "AutoShootSwitcher", "SprintCancelsReload", "ReturnToIronSightAfterReload",
            "MaxFramerate",
        };

        [Fact]
        public void TheStockPageHasBothRowsWhereTheGamePutsThem()
        {
            var ui = new FakeFlashTree();
            ui.OpenSettings();
            Assert.True(ui.Tap("btn_general"));
            Assert.Equal(StockAndroidRows, ui.RowTypes());
            List<string> layout = ui.ContentLayout();
            Assert.Equal("category:setting.Controls", layout[0]);
            Assert.Equal("category:menu.GeneralTxt", layout[3]);
            Assert.Equal("row:GraphicSetting", layout[4]);               // spliced in at index 2: the first GENERAL row
            Assert.Equal("category:setting.AdvancedOptions", layout[layout.IndexOf("row:AimBehavior") - 1]);   // skeptic D1
            Assert.Equal("row:MaxFramerate", layout.Last());              // pushed last, no header of its own (the gamepad rows are skipped)
            Assert.Equal(3, layout.Count(l => l.StartsWith("category:", StringComparison.Ordinal)));
            // Rows are named item<k> by their index in _arOptions and stacked with y += height + 8.
            Assert.Equal("item2", ui.Row("GraphicSetting").Name);
            Assert.Equal("item" + (ui.Options.Count - 1), ui.Row("MaxFramerate").Name);
            List<FlashObject> children = ui.Content.Children;
            for (int i = 1; i < children.Count; i++)
            {
                float gap = children[i - 1].Kind == "categoryMC" ? 10 : 8;
                Assert.Equal(children[i - 1].Num("y") + children[i - 1].Num("height") + gap, children[i].Num("y"));
            }
            FlashObject fps = ui.Row("MaxFramerate");
            Assert.Equal(new object[] { "30", "60", "setting.UncappedFPS" }, ((List<object>)fps.Get("values")).ToArray());
            Assert.True((bool)fps.Child("mc_warning").Get("visible"));
            Assert.True(ui.TapWarning(fps));
            Assert.False((bool)ui.Row("InvertCameraX").Child("mc_warning").Get("visible"));
            Assert.Equal("setting.FrameRateLimit", fps.Child("tf_title").Child("tf").Get("text"));
            Assert.Equal(1, ui.BoundsUpdates);
            // Building the page writes no preference (not initialised yet), but each selector's first displayList saves (MGS:573).
            Assert.Empty(ui.Game.Writes);
            Assert.True(ui.Game.Saves > 0);
            Assert.True(ui.GameplayInitialized);
        }

        [Fact]
        public void InAMatchTheGraphicsRowIsAbsentAndTheFrameRateRowStays()
        {
            var ui = new FakeFlashTree { InGame = true };
            ui.Game.InMenu = false;
            ui.OpenSettings();
            Assert.True(ui.Tap("btn_general"));
            Assert.DoesNotContain("GraphicSetting", ui.RowTypes());
            Assert.Equal("MaxFramerate", ui.RowTypes().Last());
            Assert.Equal(0, ui.BoundsUpdates);   // frame 2: no mask resize
        }

        [Fact]
        public void BoxedFalseFromAnAppendedDelegateLeavesBothRowsOutWithoutAGap()
        {
            var ui = new FakeFlashTree();
            ui.OverrideNativeFunction("CanChangeGraphicSetting", (Func<object>)(() => false), true, true);
            ui.OverrideNativeFunction("CanChangeMaxFramerate", (Func<object>)(() => false), true, true);
            ui.OpenSettings();
            ui.Tap("btn_general");
            Assert.Equal(StockAndroidRows.Where(t => t != "GraphicSetting" && t != "MaxFramerate"), ui.RowTypes());
            List<string> layout = ui.ContentLayout();
            Assert.Equal("row:CameraSensitivity", layout[layout.IndexOf("category:menu.GeneralTxt") + 1]);
            Assert.Equal("row:ReturnToIronSightAfterReload", layout.Last());
            Assert.Equal(0, ui.FramerateWarnings);
            // Without any C# override the AS stubs answer true: removing the game's delegate would show the rows (gameplay.md §2).
            var bare = new FakeFlashTree(registerGameNatives: false);
            bare.OpenSettings();
            bare.Tap("btn_general");
            Assert.Contains("GraphicSetting", bare.RowTypes());
            Assert.Contains("MaxFramerate", bare.RowTypes());
        }

        [Fact]
        public void TheHostsOwnWritesFireTheChangeHandlers()
        {
            // Skeptic E1: a selector's displayList and a slider's value= dispatch CHANGE once the page is initialised.
            var ui = new FakeFlashTree();
            var ours = new List<string>();
            ui.OverrideNativeFunction("SetUserPreference", new Action<string, object>((k, v) => ours.Add(k + "=" + FakeFlashTree.FormatAs(v))), false);
            ui.OverrideNativeFunction("SaveUserPreferences", new Action(() => ours.Add("save")), false);
            ui.OpenSettings();
            ui.Tap("btn_general");
            Assert.DoesNotContain(ours, o => o != "save");
            ours.Clear();
            int saves = ui.Game.Saves;

            FlashObject invertX = ui.Row("InvertCameraX");
            using (var flash = new McvsOffline.Embedded.Admin.SettingsMenu.FlashScope(ui))
            {
                object menu = flash.Call(flash.Class("gluic.components.menus", "MenusStack"), "getMenuOnTopOfStack");
                object content = flash.Get(menu, "mc_content");
                object row = flash.Child(content, invertX.Name);
                object list = flash.Get(row, "mc_listSelector");
                // A donor written before it is re-typed: the game stores the real preference.
                flash.Call(list, "displayList", 1);
                Assert.Equal(new[] { "InvertCameraX=true", "save" }, ours);
                Assert.Equal(new[] { "InvertCameraX=True" }, ui.Game.Writes);
                // Re-typed first: the key is ours, the game drops it; a 4-value list sends only Boolean(index).
                flash.Set(row, "type", "mcvs.msaa");
                flash.Set(row, "values", new List<object> { "setting.OffButton", "2x", "4x", "8x" });
                flash.Set(list, "dataLength", 4);
                ours.Clear();
                flash.Call(list, "displayList", 3);
                Assert.Equal(new[] { "mcvs.msaa=true", "save" }, ours);
                Assert.Equal(3.0, flash.Number(list, "value"));        // List.value already holds the index (skeptic E2)
                Assert.Equal("8x", ui.Row("mcvs.msaa").Child("mc_listSelector").Get("text"));
                Assert.Single(ui.Game.Writes);
                // The same index again: no CHANGE.
                ours.Clear();
                flash.Call(list, "displayList", 3);
                Assert.Empty(ours);

                // A slider: value= fires onBarScroll, which has no _isInitialized guard (MGS:487-509).
                object slider = flash.Get(flash.Child(content, ui.Row("CameraSmoothing").Name), "mc_scrollbar");
                flash.Set(slider, "value", 0.25f);
                Assert.Equal(new[] { "CameraSmoothing=0.25" }, ours);
                Assert.Equal("CameraSmoothing=0.25", ui.Game.Writes.Last());
            }
            Assert.True(ui.Game.Saves > saves);
            ui.AssertAllDisposed();
        }

        [Fact]
        public void ThePlayersOwnChangesReachTheNatives()
        {
            var ui = new FakeFlashTree();
            ui.OpenSettings();
            ui.Tap("btn_general");
            ui.Select(ui.Row("MaxFramerate"), 2);
            Assert.Equal("MaxFramerate=2", ui.Game.Writes.Last());
            Assert.Equal(1, ui.FramerateWarnings);
            ui.Select(ui.Row("GraphicSetting"), 2);
            Assert.Equal(new[] { 2 }, ui.Game.GraphicSettingChanges);
            ui.Drag(ui.Row("PlayerFov"), 1f);
            Assert.Equal("PlayerFov=120", ui.Game.Writes.Last());
            ui.Select(ui.Row("IronSightMode"), 1);
            Assert.False((bool)ui.Row("SingleTapExitIronSight").Get("visible"));
            // Back: Gameplay Settings saves and empties its content; the grid is set up again.
            int saves = ui.Game.Saves;
            ui.Back();
            Assert.Equal(saves + 1, ui.Game.Saves);
            Assert.Equal("menu_settings", ui.Top.Name);
            Assert.Equal("settings_main_screen", ui.Game.Sections.Last());
        }

        [Fact]
        public void TheHostCanPushThePageAndSetItsTitle()
        {
            var ui = new FakeFlashTree();
            ui.OpenSettings();
            using (var flash = new McvsOffline.Embedded.Admin.SettingsMenu.FlashScope(ui))
            {
                flash.Call(flash.Class("managers", "MenusManager"), "pushMenu", "menu_gameplaySettings");
                Assert.Equal("menu_gameplaySettings", ui.Top.Name);
                flash.Call(flash.Class("elements.freemiumBar", "FreemiumBar"), "updateTitle", "setting.Performance");
                object menu = flash.Call(flash.Class("gluic.components.menus", "MenusStack"), "getMenuOnTopOfStack");
                flash.Set(menu, "title", "setting.Performance");
                object content = flash.Get(menu, "mc_content");
                flash.Set(content, "alpha", 0f);
                object first = flash.Call(content, "getChildAt", 0);
                flash.Call(content, "removeChild", first);
                Assert.Throws<System.Reflection.TargetInvocationException>(() => flash.Call(content, "removeChild", first));
                object scroll = flash.Child(flash.Child(menu, "mc_panel"), "mc_scrollPane");
                flash.Call(scroll, "updateBounds");
                flash.Call(flash.Class("shared.utilities", "TextUtilities"), "setStringID", flash.Child(flash.Child(flash.Get(content, ui.Row("CameraSmoothing").Name), "tf_title"), "tf"), "SCREEN SCALE");
            }
            Assert.Equal(new[] { "menu_settings", "menu_gameplaySettings" }, ui.Pushed);
            Assert.Equal(new[] { "setting.Performance" }, ui.FreemiumTitles);
            Assert.Equal("setting.Performance", ui.Top.Get("title"));
            Assert.Equal(0f, ui.Content.Num("alpha"));
            Assert.Equal("row:EnableAutoShoot", ui.ContentLayout()[0]);   // the CONTROLS header is gone
            Assert.Equal(2, ui.BoundsUpdates);
            Assert.Equal("SCREEN SCALE", ui.Row("CameraSmoothing").Child("tf_title").Child("tf").Get("text"));
            Assert.Contains("managers.MenusManager.pushMenu(menu_gameplaySettings)", ui.HostWrites);
            ui.AssertAllDisposed();
        }
    }
}
