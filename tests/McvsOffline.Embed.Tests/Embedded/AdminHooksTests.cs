using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Admin;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// McvsOffline.AdminHooks, the entry points of the patched game code (design §8.1, §8.3, §9.2, §9.3), with look-alikes of the
    /// game's World, BrowserComponent and GameApplication and a recording IAdminPlatform.
    /// </summary>
    [Collection("GameHooks")]
    public class AdminHooksTests
    {
        private static readonly Regex PageUrl = new Regex(@"^http://127\.0\.0\.1:51780/_mcvs/ui/#t=([0-9a-f]{32})$");

        [Fact]
        public void AccountMenuButtonsAppendsOneEntryAtTheEnd()
        {
            using (var env = new HookEnvironment("hooks-buttons"))
            {
                var game = new List<object> { new { snsId = 1, isClickable = true, label = "setting.FacebookButton" }, new { customAction = "restore_purchase", label = "Social.RecoverProgressButton" } };
                object first = game[0], second = game[1];
                IEnumerable<object> result = AdminHooks.AccountMenuButtons(game);
                Assert.Same(game, result);   // the game's own List<object>, which ASValue.SetObject turns into an AS array
                Assert.Equal(3, game.Count);
                Assert.Same(first, game[0]);
                Assert.Same(second, game[1]);
                var entry = Assert.IsType<Dictionary<string, object>>(game[2]);
                Assert.Equal(4, entry.Count);
                Assert.Equal("mcvs_offline", entry["customAction"]);
                Assert.Equal(true, entry["isClickable"]);
                Assert.Equal("MCVS Offline", entry["label"]);
                Assert.Equal("Menus/assets/png/icons/Settings.png", entry["icon"]);
                Assert.Equal(AdminHooks.ButtonAction, entry["customAction"]);

                // Not a List<object>: a new list with every item; null: just ours.
                IEnumerable<object> lazy = new object[] { "a", "b" }.Select(x => x);
                var copied = Assert.IsType<List<object>>(AdminHooks.AccountMenuButtons(lazy));
                Assert.Equal(new object[] { "a", "b" }, copied.Take(2));
                Assert.Equal(3, copied.Count);
                var alone = Assert.IsType<List<object>>(AdminHooks.AccountMenuButtons(null));
                Assert.Single(alone);

                // An enumeration that throws: the input comes back, nothing escapes.
                IEnumerable<object> broken = Broken();
                Assert.Same(broken, AdminHooks.AccountMenuButtons(broken));

                // Turned off: unchanged.
                env.Server.Context.Settings.Set("admin.enabled", false);
                var untouched = new List<object> { "x" };
                Assert.Same(untouched, AdminHooks.AccountMenuButtons(untouched));
                Assert.Single(untouched);
                Assert.Null(AdminHooks.AccountMenuButtons(null));
            }

            // No server (not started, or no admin module): unchanged.
            AdminHooks.Host = null;
            var plain = new List<object> { "x" };
            Assert.Same(plain, AdminHooks.AccountMenuButtons(plain));
            Assert.Single(plain);
            using (var env = new HookEnvironment("hooks-noportal", admin: false))
            {
                Assert.Same(plain, AdminHooks.AccountMenuButtons(plain));
                Assert.Single(plain);
            }
        }

        private static IEnumerable<object> Broken()
        {
            yield return "a";
            throw new InvalidOperationException("probe");
        }

        [Fact]
        public void OpenPageOpensTheGamesBrowserWithAOneTimeTicket()
        {
            using (var env = new HookEnvironment("hooks-open"))
            {
                AdminHooks.OpenPage("account", env.Game.World);
                string opened = Assert.Single(env.Game.Browser.Opened);
                Assert.EndsWith("|True", opened);   // OpenUrl(url, true): the in-game browser
                string url = opened.Substring(0, opened.Length - 5);
                Match m = PageUrl.Match(url);
                Assert.True(m.Success, url);
                Assert.Empty(env.Platform.Calls);
                Assert.True(env.Logs.Contains("admin: page opened (trigger account, via game)"));
                Assert.DoesNotContain(env.Logs.Snapshot(), e => e.Format().Contains(m.Groups[1].Value));   // never logged
                // The game's own credential was read on the game thread for "the current player".
                Assert.Equal("anonymous:a1b2c3d4e5f6", env.Host.Describe().GameCredential);
                Assert.Equal("initialized", env.Host.Describe().BrowserStatus);
                string reason;
                Assert.True(env.Watchdog.IsPaused(out reason));   // the lease starts with the open
                Assert.Equal(AdminHost.PageLeaseReason, reason);

                // The ticket is a real one: the page can exchange it for a session (once).
                Assert.True(env.Exchange(m.Groups[1].Value));
                Assert.False(env.Exchange(m.Groups[1].Value));
            }
        }

        [Fact]
        public void OpenPageFallsBackToJniAndHonoursOpenVia()
        {
            using (var env = new HookEnvironment("hooks-via"))
            {
                // auto, native browser not initialised: SetParameters + OpenURL over JNI.
                env.Game.Browser.IsInGameBrowserInitialized = false;
                AdminHooks.OpenPage("forum", env.Game.World);
                Assert.Empty(env.Game.Browser.Opened);
                Assert.Equal(new[] { "OpenInGameBrowserJni" }, env.Platform.Calls);
                Assert.Matches(PageUrl, Assert.Single(env.Platform.Opened));
                Assert.Equal("not initialized", env.Host.Describe().BrowserStatus);
                Assert.True(env.Logs.Contains("admin: page opened (trigger forum, via jni)"));

                // game: the managed path only, whatever the native state.
                env.Platform.Calls.Clear();
                env.Server.Context.Settings.Set("admin.openVia", "game");
                AdminHooks.OpenPage("account", env.Game.World);
                Assert.Matches(PageUrl, Assert.Single(env.Game.Browser.Opened).Replace("|True", ""));
                Assert.Empty(env.Platform.Calls);

                // jni: JNI even when the native browser is initialised.
                env.Game.Browser.IsInGameBrowserInitialized = true;
                env.Game.Browser.Opened.Clear();
                env.Platform.Opened.Clear();
                env.Server.Context.Settings.Set("admin.openVia", "jni");
                AdminHooks.OpenPage("account", env.Game.World);
                Assert.Empty(env.Game.Browser.Opened);
                Assert.Equal(new[] { "OpenInGameBrowserJni" }, env.Platform.Calls);

                // post: the page URL without its fragment, the ticket as form data.
                env.Platform.Calls.Clear();
                env.Platform.Opened.Clear();
                env.Server.Context.Settings.Set("admin.openVia", "post");
                AdminHooks.OpenPage("account", env.Game.World);
                Assert.Equal(new[] { "OpenInGameBrowserWithPostData" }, env.Platform.Calls);
                Assert.Matches(@"^http://127\.0\.0\.1:51780/_mcvs/ui\|t=[0-9a-f]{32}$", Assert.Single(env.Platform.Opened));
                Assert.True(env.Logs.Contains("admin: page opened (trigger account, via post)"));

                // Nothing can open it: a warning, no lease.
                env.Platform.JniResult = false;
                env.Watchdog.PauseFor(0, null);
                AdminHooks.OpenPage("account", env.Game.World);
                Assert.True(env.Logs.Contains("admin: the page could not be opened (trigger account, openVia post)"));
                string reason;
                Assert.False(env.Watchdog.IsPaused(out reason));

                // The fragment's screen and result travel with the ticket (the server redirects to #s=..&r=..).
                Assert.True(PageOpener.SplitTicket("http://127.0.0.1:1/_mcvs/ui/#t=abc&s=saves&r=nosave", out string target, out string form));
                Assert.Equal("http://127.0.0.1:1/_mcvs/ui", target);
                Assert.Equal("t=abc&s=saves&r=nosave", form);
                Assert.True(PageOpener.SplitTicket("http://127.0.0.1:1/_mcvs/ui/#t=abc&s=logs", out target, out form));
                Assert.Equal("t=abc&s=logs", form);
                Assert.True(PageOpener.SplitTicket("http://127.0.0.1:1/_mcvs/ui/#t=abc&news&x=1", out target, out form));
                Assert.Equal("t=abc", form);
                Assert.False(PageOpener.SplitTicket("http://127.0.0.1:1/_mcvs/ui/#t=&s=saves", out target, out form));
                Assert.False(PageOpener.SplitTicket("http://127.0.0.1:1/_mcvs/ui/#news", out target, out form));
            }
        }

        [Fact]
        public void OpenPageWithoutAWorldGoesThroughTheGameThread()
        {
            using (var env = new HookEnvironment("hooks-null"))
            {
                AdminHooks.OpenPage("account", null);
                Assert.Empty(env.Game.Browser.Opened);   // posted, not run yet
                Assert.Equal(1, env.Game.App.Context.Pending);
                env.Game.App.Context.Drain();
                Assert.Matches(PageUrl, Assert.Single(env.Game.Browser.Opened).Replace("|True", ""));
                Assert.Equal("anonymous:a1b2c3d4e5f6", env.Host.Describe().GameCredential);

                // IAdminHost.OpenPage (server thread: the redeem code, the reopen after "Save the game now") is the same path.
                IAdminPortal portal = env.Server.Context.Features.Get<IAdminPortal>();
                Assert.True(portal.OpenPage(AdminTriggers.Resume, "saves"));
                Assert.Single(env.Game.Browser.Opened);
                env.Game.App.Context.Drain();
                Assert.Equal(2, env.Game.Browser.Opened.Count);
                Assert.Matches(@"^http://127\.0\.0\.1:51780/_mcvs/ui/#t=[0-9a-f]{32}&s=saves\|True$", env.Game.Browser.Opened[1]);
                Assert.True(env.Logs.Contains("admin: page opened (trigger resume, via game)"));

                // No game at all (GameApplication not created yet): JNI from the calling thread.
                FakeGame.Uninstall();
                env.Platform.Calls.Clear();
                Assert.True(env.Host.OpenPage("http://127.0.0.1:51780/_mcvs/ui/#t=" + new string('a', 32), "redeem"));
                Assert.Equal(new[] { "OpenInGameBrowserJni" }, env.Platform.Calls);
            }
        }

        [Fact]
        public void OpenIfPageMarkerHandlesOnlyTheMarker()
        {
            using (var env = new HookEnvironment("hooks-marker"))
            {
                IAdminPortal portal = env.Server.Context.Features.Get<IAdminPortal>();
                string marker = portal.PageMarkerUrl;
                Assert.Equal("http://127.0.0.1:51780/_mcvs/ui/#news", marker);

                Assert.False(AdminHooks.OpenIfPageMarker("https://www.youtube.com/watch?v=x", env.Game.Browser));
                Assert.False(AdminHooks.OpenIfPageMarker(marker + "x", env.Game.Browser));
                Assert.False(AdminHooks.OpenIfPageMarker(null, env.Game.Browser));
                Assert.Empty(env.Game.Browser.Opened);

                Assert.True(AdminHooks.OpenIfPageMarker(marker, env.Game.Browser));
                Assert.Matches(PageUrl, Assert.Single(env.Game.Browser.Opened).Replace("|True", ""));
                Assert.True(env.Logs.Contains("admin: page opened (trigger news, via game)"));

                // The URL our opener hands to BrowserComponent.OpenUrl carries a ticket: not the marker, passes through.
                string ours = env.Game.Browser.Opened[0].Replace("|True", "");
                Assert.False(AdminHooks.OpenIfPageMarker(ours, env.Game.Browser));

                // A server World: handled (ignored), as the original body returns there; unknown (null) counts as a server.
                env.Game.Browser.Opened.Clear();
                env.Game.World.IsServer = true;
                Assert.True(AdminHooks.OpenIfPageMarker(marker, env.Game.Browser));
                env.Game.World.IsServer = null;
                Assert.True(AdminHooks.OpenIfPageMarker(marker, env.Game.Browser));
                Assert.Empty(env.Game.Browser.Opened);
                env.Game.World.IsServer = false;

                // A World that throws: not handled (the original body runs), no exception.
                Assert.False(AdminHooks.OpenIfPageMarker(marker, new ThrowingBrowser()));
                Assert.False(AdminHooks.OpenIfPageMarker(marker, null));

                // Turned off: the marker passes through (the view-only page).
                env.Server.Context.Settings.Set("admin.enabled", false);
                Assert.False(AdminHooks.OpenIfPageMarker(marker, env.Game.Browser));
                Assert.Empty(env.Game.Browser.Opened);
                Assert.Empty(env.Platform.Calls);
            }

            AdminHooks.Host = null;
            Assert.False(AdminHooks.OpenIfPageMarker("http://127.0.0.1:51780/_mcvs/ui/#news", null));
            using (var env = new HookEnvironment("hooks-marker-noportal", admin: false))
            {
                Assert.False(AdminHooks.OpenIfPageMarker("http://127.0.0.1:51780/_mcvs/ui/#news", env.Game.Browser));
                AdminHooks.OpenPage("account", env.Game.World);   // no portal: nothing opens, nothing throws
                Assert.Empty(env.Game.Browser.Opened);
            }
        }

        [Fact]
        public void HooksNeverThrowIntoTheGame()
        {
            AdminHooks.Host = null;
            AdminHooks.OpenPage(null, null);
            AdminHooks.OpenPage("account", new object());
            using (var env = new HookEnvironment("hooks-throw"))
            {
                AdminHooks.OpenPage(null, new object());          // a "World" without a Browser: JNI in auto mode
                AdminHooks.OpenPage("account", new ThrowingBrowser());
                Assert.False(AdminHooks.OpenIfPageMarker(env.Server.Context.Features.Get<IAdminPortal>().PageMarkerUrl, new object()));
            }
        }

        /// <summary>A server with the admin module, an AdminHost over a recording platform and the fake game, installed as AdminHooks.Host.</summary>
        private sealed class HookEnvironment : IDisposable
        {
            public HookEnvironment(string name, bool admin = true)
            {
                Logs = new MemoryLogSink();
                Server = AdminFixtures.CreateServer(TestEnvironment.NewTempDir(name), Logs, admin);
                Game = FakeGame.Install();
                Platform = new RecordingAdminPlatform();
                Watchdog = new McvsOffline.Embedded.Diagnostics.GameThreadWatchdog(null, null, null);
                Host = new AdminHost(Server.Context, Platform, Game.Reflection, null) { Watchdog = () => Watchdog };
                Server.Context.Features.Set<IAdminHost>(Host);
                AdminHooks.Host = Host;
            }

            public MemoryLogSink Logs { get; }
            public McvsServer Server { get; }
            public FakeGame Game { get; }
            public RecordingAdminPlatform Platform { get; }
            public McvsOffline.Embedded.Diagnostics.GameThreadWatchdog Watchdog { get; }
            public AdminHost Host { get; }

            /// <summary>POST /_mcvs/api/v1/session with the ticket, as the page does: true on 200.</summary>
            public bool Exchange(string ticket)
            {
                var headers = new McvsOffline.Server.Core.Http.HttpHeaders();
                headers.Add("Host", "127.0.0.1:51780");
                headers.Add("Origin", "http://127.0.0.1:51780");
                headers.Add("X-MCVS", "1");
                headers.Add("Content-Type", "application/json");
                var request = new McvsOffline.Server.Core.Http.HttpRequest("POST", "/_mcvs/api/v1/session", headers,
                                                                          System.Text.Encoding.UTF8.GetBytes("{\"ticket\":\"" + ticket + "\"}"));
                request.RemoteEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 40000);
                return Server.Handle(request).StatusCode == 200;
            }

            public void Dispose()
            {
                AdminHooks.Host = null;
                FakeGame.Uninstall();
                Server.Dispose();
            }
        }
    }
}
