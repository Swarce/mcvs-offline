using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Embedded.Admin.SettingsMenu;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// Build 21's same-frame step (<see cref="SettingsMenuLayout.SameFrame"/>, docs/design/settings-menu.md §3.2): armed by
    /// <c>EnterSection("settings_main_screen")</c>, driven by an observer on GetPlatformId (Android) or GetLanguage (Steam) that is
    /// registered only for the rest of that frame, never throwing into Flash, and idempotent with build 20's run, which stays as
    /// the safety net and says in its lines which path laid the grid out. <see cref="SettingsMenuRenderTests"/> pins what the player
    /// sees; these pin the mechanics.
    /// </summary>
    public class SettingsMenuSameFrameTests
    {
        private const string Main = "settings_main_screen";

        /// <summary>
        /// Android's GetPlatformId calls after EnterSection in one focus-in of the fake: 11 labels through setStringID (3 each),
        /// the platform 4/6 test (2), addButtonListeners (2), FreemiumBar's title (3). Our own re-dress's calls are not counted (a
        /// look is running).
        /// </summary>
        private const int AndroidCalls = 11 * 3 + 2 + 2 + 3;

        private static SettingsMenuReturnTests.Rig NewRig(bool steam)
        {
            var rig = new SettingsMenuReturnTests.Rig(steam);
            rig.Ui.SteamReplacesInstances = steam;
            return rig;
        }

        private static string Native(bool steam)
        {
            return steam ? SettingsMenuNames.GetLanguage : SettingsMenuNames.GetPlatformId;
        }

        private static object ObserverTarget(SettingsMenuReturnTests.Rig rig, bool steam)
        {
            return rig.Natives.DelegateOf(Native(steam)).Target;
        }

        // ------------------------------------------------------------------ the lines say which path laid the grid out

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheFocusInLineSaysTheFocusInFrameLaidTheGridOut(bool steam)
        {
            var rig = NewRig(steam);
            rig.Ui.PoppedStaysOnTopFrames = 3;
            rig.Open();
            int at = rig.Ui.Stack.FindIndex(m => m.Name == McvsMenuNames.Settings);
            rig.TabAndBack("btn_help");
            rig.TabAndBack("btn_audio");
            string frame = ", panel frame " + (steam ? 1 : 3) + ", btn_social hidden";
            string tail = "; same frame: laid out (" + Native(steam) + " calls " + (steam ? 1 : AndroidCalls) + ", looks 1)";
            Assert.Equal(new[]
            {
                // The focus-in frame wrote the grid before this line (one layout per focus-in), the new Steam instances too.
                "settings menu: focus-in 1 (layouts so far: 1): on top menu_settings, menu_settings at " + at + " in the stack" + frame + tail,
                "settings menu: focus-in 2 (layouts so far: 2): on top menu_help, menu_settings at " + at + " in the stack" + frame + tail,
                "settings menu: focus-in 3 (layouts so far: 3): on top menu_audio, menu_settings at " + at + " in the stack" + frame + tail,
            }, rig.Host.Lines("settings menu: focus-in "));
            string done = "settings menu: frame " + (steam ? "1, 6" : "3, 7") + " tiles + ABOUT laid out" + SettingsMenuReturnTests.SameFrameDone;
            Assert.Equal(new[] { done, done, done }, rig.GridLines);
            // The run found it laid out on its first frame and wrote nothing more.
            Assert.Equal(3, rig.State.Layouts);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WithoutTheSameFrameStepTheRunSaysItDrewTheStockGridOnce(bool steam)
        {
            // Build 20's path (the safety net alone): laid out on the run's first frame, one render of the stock grid.
            var rig = new SettingsMenuReturnTests.Rig(steam, sameFrame: false);
            rig.Open();
            rig.TabAndBack("btn_audio");
            string done = "settings menu: frame " + (steam ? "1, 6" : "3, 7") + " tiles + ABOUT laid out; stock grid drawn 1 frame";
            Assert.Equal(new[] { done, done }, rig.GridLines);
        }

        // ------------------------------------------------------------------ the observer: only next to the game's, only for the frame

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheObserverSitsAfterTheGamesDelegateOnlyUntilTheNextFrame(bool steam)
        {
            var rig = NewRig(steam);
            object target = ObserverTarget(rig, steam);
            Assert.Equal(0, rig.Ui.CountWithTarget(Native(steam), target));
            rig.Ui.OpenSettings();
            // The rest of the focus-in frame: the game's delegate first, ours last.
            IReadOnlyList<Delegate> list = rig.Ui.DelegatesOf(Native(steam));
            Assert.Equal(2, list.Count);
            Assert.NotSame(target, list[0].Target);
            Assert.Same(target, list[1].Target);
            // The run's first frame removes it; the game's stays.
            rig.Host.RunFrame();
            Assert.Equal(0, rig.Ui.CountWithTarget(Native(steam), target));
            Assert.Single(rig.Ui.DelegatesOf(Native(steam)));
            Assert.Null(rig.State.SameFrame);
            // Neither native of the other platform is ever touched.
            Assert.Equal(0, rig.Ui.CountWithTarget(Native(!steam), ObserverTarget(rig, !steam)));
            rig.Host.Drain();
            rig.Ui.AssertAllDisposed();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OtherMenusCallingTheNativeReachNoStep(bool steam)
        {
            var rig = NewRig(steam);
            var calls = new List<int>();
            Action<ISettingsMenuHost, SettingsMenuState, string> real = rig.Natives.Steps.SameFrame;
            rig.Natives.Steps.SameFrame = (h, s, n) =>
            {
                calls.Add(rig.Host.Frames);
                real(h, s, n);
            };
            rig.Ui.OpenSettings();                           // before the first frame: the focus-in's frame is 0
            Assert.NotEmpty(calls);
            Assert.All(calls, f => Assert.Equal(0, f));
            rig.Stay();
            int before = calls.Count;
            // Gameplay Settings (every row through setStringID), a while on it, Back: only the focus-in frame's calls reach us.
            Assert.True(rig.Ui.Tap("btn_general"));
            rig.Host.RunFrames(30);
            int returnFrame = rig.Host.Frames;
            rig.Ui.Back();
            rig.Host.Drain();
            Assert.True(calls.Count > before);
            Assert.True(calls.Skip(before).All(f => f == returnFrame), "a call outside a focus-in frame reached the step");
            rig.Ui.Back();                                  // Settings closed: the main menu
            rig.Host.RunFrames(30);
            int after = calls.Count;
            rig.Ui.PushMenu("menu_main");
            rig.Ui.SetStringId(rig.Ui.FreemiumTitle, "menu.Play", new object[0]);
            if (steam) rig.Ui.CallFromFlash(SettingsMenuNames.GetLanguage, "en");
            Assert.Equal(after, calls.Count);
        }

        [Fact]
        public void WithoutTheGamesDelegateTheNativeIsNotWatchedAndTheRunLaysTheGridOut()
        {
            // Ours alone would answer null to Flash: no platform. So it is never registered alone.
            var rig = NewRig(false);
            rig.Ui.RemoveNativeFunctionOverride("GetPlatformId");
            rig.Ui.OpenSettings();
            Assert.False(rig.Ui.IsRegistered("GetPlatformId"));
            rig.Host.Drain();
            Assert.EndsWith("; same frame: not watched (no game delegate on GetPlatformId)", rig.Host.Lines("settings menu: focus-in ").Single());
            Assert.Equal(new[] { "settings menu: frame 3, 7 tiles + ABOUT laid out; stock grid drawn 1 frame" }, rig.GridLines);
            Assert.True(rig.State.TileActive);
        }

        [Fact]
        public void AnotherSectionInTheSameFrameEndsTheStep()
        {
            var rig = NewRig(false);
            object target = ObserverTarget(rig, false);
            rig.Ui.OpenSettings();
            Assert.Equal(1, rig.Ui.CountWithTarget("GetPlatformId", target));
            Assert.True(rig.Ui.Tap("btn_general"));          // EnterSection("settings_gameplay_screen") in the same frame
            Assert.Equal(0, rig.Ui.CountWithTarget("GetPlatformId", target));
            Assert.Null(rig.State.SameFrame);
            rig.Host.Drain();
            rig.Ui.AssertAllDisposed();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheInMatchPanelArmsNothing(bool steam)
        {
            var rig = NewRig(steam);
            rig.Ui.InGame = true;
            rig.Ui.OpenSettings();
            Assert.Null(rig.State.SameFrame);
            Assert.Equal(0, rig.Ui.CountWithTarget(Native(steam), ObserverTarget(rig, steam)));
            rig.Host.Drain();
            Assert.Empty(rig.Ui.HostWrites.Where(w => w.Contains("mc_panel")));
            Assert.Empty(rig.GridLines);
        }

        [Fact]
        public void WithTheTrimOffNothingIsArmedAndTheRunReDressesTheTile()
        {
            // The re-dress alone cannot be told from the stock grid before setup() ends, so it stays build 20's (one frame late).
            var rig = NewRig(false);
            rig.Host.SettingsMenuTrim = false;
            rig.Ui.OpenSettings();
            Assert.Null(rig.State.SameFrame);
            Assert.Equal(0, rig.Ui.CountWithTarget("GetPlatformId", ObserverTarget(rig, false)));
            rig.Host.Drain();
            Assert.EndsWith("; same frame: off (trim off)", rig.Host.Lines("settings menu: focus-in ").Single());
            Assert.Equal(new[] { "settings menu: frame 3, PERFORMANCE tile re-dressed in place (trim off); stock grid drawn 1 frame" }, rig.GridLines);
        }

        // ------------------------------------------------------------------ inside the native: one look, after the last stock write, never a throw

        [Fact]
        public void AndroidLooksOnceAtAddButtonListenersAndItsOwnReDressDoesNotComeBack()
        {
            var rig = NewRig(false);
            int lookedAt = -1;
            Action<ISettingsMenuHost, SettingsMenuState, string> real = rig.Natives.Steps.SameFrame;
            rig.Natives.Steps.SameFrame = (h, s, n) =>
            {
                int layouts = s.Layouts;
                real(h, s, n);
                if (s.Layouts != layouts) lookedAt = s.SameFrame.Calls;
            };
            rig.Ui.OpenSettings();
            // The first addButtonListeners call after the bottom row (AND-AS :213 → :363): 11 labels x 3, then the 4/6 test (2).
            Assert.Equal(11 * 3 + 2 + 1, lookedAt);
            Assert.Equal(1, rig.State.SameFrame.Looks);
            Assert.Equal(AndroidCalls, rig.State.SameFrame.Calls);
            // Our btn_forum.setup's own setStringID went through GetPlatformId inside the look (depth 2) and was not looked at.
            Assert.Equal(2, rig.Ui.MaxDepth);
            Assert.True(rig.State.TileActive);
            Assert.Equal("setting.Performance", rig.Ui.Instance("btn_forum").Get("title"));
            rig.Host.Drain();
            rig.Ui.AssertAllDisposed();
        }

        [Fact]
        public void SteamLooksAtEveryArmedGetLanguageSoAnEarlyOneIsLookedAtAgainAfterSetup()
        {
            // A GetLanguage between EnterSection and setup() (none in the AS; a listener of MoveToCamera's event, say) looks too:
            // on the first entry it writes the instances setup() then replaces, and :367 lays the new ones out.
            var rig = NewRig(true);
            rig.Ui.OverrideNativeFunction("MoveToCamera", new Action<string, string>((a, b) => rig.Ui.CallFromFlash("GetLanguage", "en")));
            rig.Open();
            rig.TabAndBack("btn_audio");
            List<string> lines = rig.Host.Lines("settings menu: focus-in ");
            Assert.EndsWith("; same frame: laid out (GetLanguage calls 2, looks 2)", lines[0]);
            Assert.EndsWith("; same frame: laid out (GetLanguage calls 2, looks 2)", lines[1]);
            string done = "settings menu: frame 1, 6 tiles + ABOUT laid out" + SettingsMenuReturnTests.SameFrameDone;
            Assert.Equal(new[] { done, done }, rig.GridLines);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AStepThatThrowsReachesNoFlashAndTheRunLaysTheGridOut(bool steam)
        {
            var rig = NewRig(steam);
            rig.Natives.Steps.SameFrame = (h, s, n) => { throw new InvalidOperationException("boom"); };
            rig.Ui.OpenSettings();                           // no FakeMenuInvocationException
            rig.Host.Drain();
            string done = "settings menu: frame " + (steam ? "1, 6" : "3, 7") + " tiles + ABOUT laid out; stock grid drawn 1 frame";
            Assert.Equal(new[] { done }, rig.GridLines);
            Assert.Empty(rig.Host.Lines("settings menu: " + Native(steam)));
        }

        [Fact]
        public void AWriteThatThrowsInTheFocusInFrameReachesNoFlashAndTheRunSaysSo()
        {
            var rig = NewRig(false);
            rig.Ui.SettingsBuilt = menu => menu.Child("mc_panel").Child("btn_social").Throws = true;
            int lines = rig.Host.Lines("settings menu: ").Count;
            rig.Ui.OpenSettings();                           // the look's write throws inside GetPlatformId: no exception into AS
            // The look put FORUM back (the tile must not read PERFORMANCE while it still forwards to the game), said nothing and
            // took no second look in that frame.
            Assert.Equal("setting.ForumButton", rig.Ui.Instance("btn_forum").Get("title"));
            Assert.False(rig.State.TileActive);
            Assert.False(rig.State.RedressFailed);
            Assert.Equal(1, rig.State.SameFrame.Looks);
            Assert.Equal(lines, rig.Host.Lines("settings menu: ").Count);
            rig.Host.Drain();
            Assert.EndsWith("; same frame: not laid out, InvalidOperationException: probe: native call failed (GetPlatformId calls " + AndroidCalls + ", looks 1)",
                            rig.Host.Lines("settings menu: focus-in ").Single());
            // The run, the safety net, tries and fails the same way: one line, and the Gameplay rows stay (R4).
            Assert.Equal(new[] { "settings menu: not laid out (InvalidOperationException: probe: native call failed)" }, rig.GridLines);
            Assert.True(rig.State.RedressFailed);
        }

        [Fact]
        public void AFocusInFrameThatCannotLayOutLeavesItToTheRunAndBackStillEndsIt()
        {
            // No ABOUT: every armed call looks (ABOUT cannot be read), at most MaxSameFrameLooks times, each a read that waits.
            var rig = NewRig(false);
            rig.Ui.SettingsBuilt = menu => { FlashObject panel = menu.Child("mc_panel"); panel.Children.Remove(panel.Child("btn_credits")); };
            rig.Ui.OpenSettings();
            Assert.Equal(SettingsMenuLayout.MaxSameFrameLooks, rig.State.SameFrame.Looks);
            Assert.Empty(rig.Ui.HostWrites.Where(w => !w.Contains("Native")));
            rig.Host.RunFrames(3);
            // Ten labels now (no ABOUT to dress).
            Assert.EndsWith("; same frame: not laid out, no btn_credits (GetPlatformId calls " + (AndroidCalls - 3) + ", looks " + SettingsMenuLayout.MaxSameFrameLooks + ")",
                            rig.Host.Lines("settings menu: focus-in ").Single());
            rig.Ui.Back();                                   // Settings closed before anything could be laid out
            rig.Host.Drain();
            Assert.Equal(new[] { "settings menu: not laid out (no btn_credits; Settings left after 4 frames)" }, rig.GridLines);
            Assert.True(rig.State.RedressFailed);
            Assert.Equal(0, rig.Host.Pending);
        }

        [Fact]
        public void ANewFocusInBeforeTheNextFrameReplacesTheStepAndBothFocusInsAreLaidOut()
        {
            var rig = NewRig(false);
            object target = ObserverTarget(rig, false);
            rig.Ui.OpenSettings();
            SettingsMenuLayout.SameFrame first = rig.State.SameFrame;
            Assert.True(rig.Ui.Tap("btn_audio"));
            rig.Ui.Back();                                   // the second focus-in, same frame
            Assert.True(first.Closed);
            Assert.NotSame(first, rig.State.SameFrame);
            Assert.True(rig.State.SameFrame.LaidOut);
            Assert.Equal(1, rig.Ui.CountWithTarget("GetPlatformId", target));
            rig.Host.Drain();
            Assert.Equal(new[] { "settings menu: frame 3, 7 tiles + ABOUT laid out" + SettingsMenuReturnTests.SameFrameDone }, rig.GridLines);
            Assert.Equal(2, rig.State.Layouts);
            Assert.Equal(0, rig.Ui.CountWithTarget("GetPlatformId", target));
            rig.Ui.AssertAllDisposed();
        }

        [Fact]
        public void TheStepDoesNothingWithoutAHostOrOnceClosed()
        {
            var state = new SettingsMenuState();
            var host = new FakeSettingsMenuHost();
            SettingsMenuLayout.OnNative(null, state, SettingsMenuNames.GetPlatformId);
            SettingsMenuLayout.OnNative(host, null, SettingsMenuNames.GetPlatformId);
            SettingsMenuLayout.OnNative(host, state, SettingsMenuNames.GetPlatformId);
            var arm = new SettingsMenuLayout.SameFrame(state.BeginLayoutRun(), false);
            state.SameFrame = arm;
            SettingsMenuLayout.OnNative(host, state, SettingsMenuNames.GetLanguage);   // not the native it watches
            Assert.Equal(0, arm.Calls);
            SettingsMenuLayout.Disarm(state);
            Assert.True(arm.Closed);
            SettingsMenuLayout.OnNative(host, state, SettingsMenuNames.GetPlatformId);
            Assert.Equal(0, arm.Calls);
            Assert.Empty(host.Ui.HostWrites);
        }
    }
}
