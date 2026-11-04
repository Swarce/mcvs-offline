using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Embedded.Admin.SettingsMenu;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// Build 21 (owner, build 20 on the phone: "the normal menu flashes for a split second before being set to our layout again" on
    /// every return to Settings): no render ever draws the stock grid. The fake host runs frames as GameApplication.UpdateWorlds
    /// does (<see cref="FakeSettingsMenuHost.RunFrame(Action)"/>: the context drain, the Flash update with the frame's tap or Back
    /// and every AS handler it runs synchronously, then the render), and the fake <c>setup()</c> calls the natives the AS calls, in
    /// its order (Android GetPlatformId through every setStringID and at addButtonListeners after the bottom row; Steam GetLanguage
    /// after the last tile write, PC-AS MenuSettings.as:367). Every render with Settings on top must show the platform's trimmed grid
    /// (each kept instance in its slot and shown, each removed one hidden; on Android btn_forum dressed as PERFORMANCE): after the
    /// first entry, after every tab and Back (PERFORMANCE and ABOUT included), after the page with the restart prompt, and after a
    /// match. The registry is the real one with its own steps, nothing swapped.
    /// <para>These fail on build 20: its run's first step is posted from EnterSection to the next frame's context drain, so the
    /// render of every focus-in frame draws what setup() left (on Android FORUM, MORE GAMES and the stock bottom row; on Steam the
    /// whole stock grid on new instances).</para>
    /// <para>Only build-20 members are used here (the registry, the plans, the fakes), so the file builds against build 20's code.</para>
    /// </summary>
    public class SettingsMenuRenderTests
    {
        private sealed class Rig
        {
            public Rig(bool steam)
            {
                Steam = steam;
                Ui = new FakeFlashTree(steam) { SteamReplacesInstances = steam };
                Host = new FakeSettingsMenuHost(Ui, steam);
                Natives = new UiNatives(() => Host, new SettingsMenuState());
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

            /// <summary>One frame whose input does <paramref name="input"/> (a tap or Back), then its render.</summary>
            public RenderPoint Frame(Action input)
            {
                Host.RunFrame(input);
                return Ui.Renders.Last();
            }

            /// <summary>The player looks at the menu: the game runs until nothing is posted, then another second of frames.</summary>
            public void Stay()
            {
                Host.Drain();
                Host.RunFrames(60);
            }

            /// <summary>Settings from the main menu, in a frame.</summary>
            public RenderPoint Open()
            {
                RenderPoint first = Frame(() => Ui.OpenSettings());
                Stay();
                return first;
            }

            /// <summary>A tile, ten frames on its menu, then Back to the grid in a frame: the render of that frame.</summary>
            public RenderPoint TabAndBack(string tab)
            {
                Frame(() => Assert.True(Ui.Tap(tab), tab + " took no tap"));
                Host.RunFrames(10);
                Assert.NotEqual(McvsMenuNames.Settings, Ui.Top.Name);
                RenderPoint back = Frame(() => Ui.Back());
                Stay();
                return back;
            }
        }

        /// <summary>
        /// Why <paramref name="render"/> (Settings on top, on the main panel) does not show the trimmed grid, or null when it does.
        /// The in-match panel (frame 2) is never laid out and not looked at.
        /// </summary>
        private static string Stock(Rig rig, RenderPoint render)
        {
            if (render.Top != McvsMenuNames.Settings || render.PanelFrame == SettingsMenuNames.InGameFrame) return null;
            bool about = rig.AboutOnMoreGames;
            SettingsMenuLayout.LayoutPlan plan = SettingsMenuReturnTests.PlanOf(rig.Steam, rig.Host.SettingsMenuPerformance, about);
            if (render.PanelFrame != plan.Frame) return "panel frame " + render.PanelFrame;
            var wrong = new List<string>();
            foreach (SettingsMenuLayout.Placement p in plan.Kept)
            {
                RenderedInstance item;
                if (!render.Instances.TryGetValue(p.Name, out item)) wrong.Add("no " + p.Name);
                else if (Math.Abs(item.X - (float)p.X) > 0.5 || Math.Abs(item.Y - (float)p.Y) > 0.5) wrong.Add(p.Name + " at (" + item.X + ", " + item.Y + ")");
                else if (!item.Visible) wrong.Add(p.Name + " hidden");
                else if (about && p.Name == "btn_more" && item.Text != "setting.CreditButton") wrong.Add("btn_more reads " + item.Text);
            }
            foreach (string name in plan.Hidden)
            {
                RenderedInstance item;
                if (render.Instances.TryGetValue(name, out item) && (item.Drawn || item.TakesTaps)) wrong.Add(name + " shown");
            }
            if (plan.RedressForum)
            {
                RenderedInstance forum;
                if (render.Instances.TryGetValue(SettingsMenuNames.ForumTile, out forum) && forum.Text != SettingsMenuNames.PerformanceTitle)
                    wrong.Add("btn_forum reads " + forum.Text);
                if (forum != null && forum.Icon != SettingsMenuNames.PerformanceIcon) wrong.Add("btn_forum shows " + forum.Icon);
            }
            return wrong.Count == 0 ? null : string.Join(", ", wrong);
        }

        /// <summary>Every render from <paramref name="from"/> on that drew Settings without the trimmed grid, with why.</summary>
        private static List<string> StockRenders(Rig rig, int from = 0)
        {
            var stock = new List<string>();
            foreach (RenderPoint r in rig.Ui.Renders.Skip(from))
            {
                string why = Stock(rig, r);
                if (why != null) stock.Add(r + ": " + why);
            }
            return stock;
        }

        private static int SettingsRenders(Rig rig)
        {
            return rig.Ui.Renders.Count(r => r.Top == McvsMenuNames.Settings && r.PanelFrame != SettingsMenuNames.InGameFrame);
        }

        private static string[] Tabs(bool steam, bool about = false)
        {
            // Build 23: ABOUT is MORE GAMES' button while it is drawn there.
            string credits = about ? "btn_more" : "btn_credits";
            return steam
                ? new[] { "btn_general", "btn_performance", "btn_audio", "btn_help", "btn_language", "btn_account", "btn_credits", "btn_general" }
                : new[] { "btn_general", "btn_forum", "btn_audio", "btn_help", "btn_language", "btn_account", credits, "btn_general", "btn_forum" };
        }

        // ------------------------------------------------------------------ no render draws the stock grid

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NoRenderDrawsTheStockGridOnTheFirstEntryOrAfterAnyTabAndBack(bool steam)
        {
            var rig = new Rig(steam);
            RenderPoint first = rig.Open();
            Assert.Equal(McvsMenuNames.Settings, first.Top);
            Assert.Null(Stock(rig, first));
            foreach (string tab in Tabs(steam, rig.AboutOnMoreGames))
            {
                RenderPoint back = rig.TabAndBack(tab);
                // The render of the frame whose Back ran setup(): build 20 drew the stock grid here.
                Assert.Equal(McvsMenuNames.Settings, back.Top);
                Assert.True(Stock(rig, back) == null, "back from " + tab + ": " + Stock(rig, back));
            }
            Assert.Empty(StockRenders(rig));
            Assert.True(SettingsRenders(rig) > Tabs(steam).Length * 60, "too few renders of Settings were looked at");
            Assert.False(rig.State.RedressFailed);
            rig.Ui.AssertAllDisposed();
        }

        [Fact]
        public void NoRenderDrawsTheStockGridAfterThePageAndItsRestartPrompt()
        {
            var rig = new Rig(false);
            rig.Open();
            rig.Frame(() => Assert.True(rig.Ui.Tap("btn_forum")));
            rig.Host.Drain();
            Assert.True(rig.State.PerformanceMode);
            rig.State.RestartPending = true;   // a GRAPHICS change on the page
            RenderPoint back = rig.Frame(() => rig.Ui.Back());
            Assert.Null(Stock(rig, back));
            rig.Stay();
            Assert.Single(rig.Ui.Popups);
            Assert.False(rig.State.RestartPending);
            Assert.Empty(StockRenders(rig));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NoRenderDrawsTheStockGridAfterAMatch(bool steam)
        {
            var rig = new Rig(steam);
            rig.Open();
            rig.Frame(() => rig.Ui.Back());                 // Settings closed
            rig.Ui.InGame = true;                           // the pause panel: pvp_settings_screen, never laid out
            rig.Frame(() => rig.Ui.OpenSettings());
            rig.Host.Drain();
            Assert.Equal(SettingsMenuNames.InGameFrame, rig.Ui.Panel.Frame);
            rig.Frame(() => rig.Ui.Back());
            rig.Ui.InGame = false;                          // back in the main menu after the match
            // The match restarted the UI's components: UIComponent.OnStart registers its natives again, clearing each list
            // (EnterSection, GetPlatformId), and the 2 s re-ensure puts ours back.
            rig.Ui.Game.RegisterUi(rig.Ui);
            rig.Natives.Ensure();
            int from = rig.Ui.Renders.Count;
            RenderPoint first = rig.Open();
            Assert.Null(Stock(rig, first));
            RenderPoint back = rig.TabAndBack("btn_general");
            Assert.Null(Stock(rig, back));
            Assert.Empty(StockRenders(rig, from));
            Assert.Empty(StockRenders(rig));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheGameStillGetsItsOwnPlatformAndLanguage(bool steam)
        {
            // Whatever we watch, the AS gets the game's answer (the last non-null result wins; ours is null).
            var rig = new Rig(steam);
            var platforms = new List<object>();
            var languages = new List<object>();
            rig.Ui.OverrideNativeFunction("MoveToCamera", new Action<string, string>((a, b) =>
            {
                platforms.Add(rig.Ui.Invoke("GetPlatformId"));
                languages.Add(rig.Ui.Invoke("GetLanguage"));
            }));
            rig.Open();
            rig.TabAndBack("btn_audio");
            Assert.Equal(new object[] { steam ? 6 : 2, steam ? 6 : 2 }, platforms);
            Assert.Equal(new object[] { "en", "en" }, languages);
            Assert.Empty(StockRenders(rig));
        }
    }
}
