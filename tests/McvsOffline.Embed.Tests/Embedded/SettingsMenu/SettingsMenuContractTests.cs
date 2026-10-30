using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Embedded.Admin;
using McvsOffline.Embedded.Admin.SettingsMenu;
using McvsOffline.Embedded.Graphics;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// The contracts of the Settings menu work (docs/design/settings-menu.md §4 WP0): AdminHost's switches and the PERFORMANCE values'
    /// store, the shared Flash interop over the fake UI, and the host wiring (EmbeddedHost hands its settings to PerformancePrefs).
    /// </summary>
    [Collection("GameHooks")]
    public class SettingsMenuContractTests
    {
        // ------------------------------------------------------------------ AdminHost as ISettingsMenuHost

        [Fact]
        public void TheSwitchesAreReadOnEveryCallAndPerformanceIsAndroidOnly()
        {
            using (var env = new HostEnvironment("sm-switches"))
            {
                ISettingsMenuHost host = env.Host;
                // Absent from settings.json: both on (the schema's defaults).
                Assert.True(host.SettingsMenuTrim);
                Assert.True(host.SettingsMenuPerformance);
                Assert.False(host.IsWindows);
                env.Server.Context.Settings.Set(SettingsSchema.SettingsMenuTrimKey, false);
                env.Server.Context.Settings.Set(SettingsSchema.SettingsMenuPerformanceKey, false);
                Assert.False(host.SettingsMenuTrim);       // live: the next call sees the page's change
                Assert.False(host.SettingsMenuPerformance);
                env.Server.Context.Settings.Set(SettingsSchema.SettingsMenuTrimKey, true);
                env.Server.Context.Settings.Set(SettingsSchema.SettingsMenuPerformanceKey, true);
                Assert.True(host.SettingsMenuTrim);
                Assert.True(host.SettingsMenuPerformance);

                // Steam keeps its own PERFORMANCE tile and page: the Android switch never applies there, the trim does.
                env.Host.PlatformName = SettingsSchema.PlatformWindows;
                Assert.True(host.IsWindows);
                Assert.False(host.SettingsMenuPerformance);
                Assert.True(host.SettingsMenuTrim);

                // A stored value of the wrong type reads as the default (on), never as off.
                env.Host.PlatformName = "android";
                env.Server.Context.Settings.Set(SettingsSchema.SettingsMenuPerformanceKey, "no");
                Assert.True(host.SettingsMenuPerformance);
            }
        }

        [Fact]
        public void TheGameThreadAndTheClientUiComeFromGameReflection()
        {
            using (var env = new HostEnvironment("sm-gamethread"))
            {
                ISettingsMenuHost host = env.Host;
                FakeUiExtensions.Ui = null;
                Assert.Null(host.ClientUi());
                // GameReflection's GetUI(GameApplication.Instance.World): the client World's UIComponent.
                var account = new FakeUi();
                FakeUiExtensions.Ui = account;
                Assert.Same(account, host.ClientUi());

                int ran = 0;
                Assert.True(host.Post(() => ran++));
                Assert.Equal(0, ran);                       // the start of the next frame, not now
                Assert.Equal(1, env.Game.App.Context.Pending);
                env.Game.App.Context.Drain();
                Assert.Equal(1, ran);
                Assert.False(host.Post(null));
                // No game thread: false, nothing queued.
                FakeGame.Uninstall();
                Assert.False(host.Post(() => ran++));
                Assert.Null(host.ClientUi());
            }
        }

        // ------------------------------------------------------------------ the PERFORMANCE values (PerformanceSettingsStore)

        [Fact]
        public void TheValuesAreReadAsTheSettingsPageReadsThem()
        {
            using (var env = new HostEnvironment("sm-read"))
            {
                IPerformanceSettings values = env.Host.PerformanceSettings;
                Settings settings = env.Server.Context.Settings;
                Assert.Equal("auto", values.GraphicsProfile);
                Assert.Equal("auto", values.Msaa);
                Assert.Equal("auto", values.RenderScale);
                Assert.Equal("auto", values.LowResMaps);
                Assert.Equal(0, values.FpsListVersion);

                settings.Set("graphics.profile", "High");
                settings.Set(SettingsSchema.GraphicsMsaaKey, "4X");
                settings.Set(SettingsSchema.GraphicsRenderScaleKey, "75");
                settings.Set(SettingsSchema.GraphicsLowResMapsKey, "on");
                settings.Set(SettingsSchema.GraphicsFpsListVersionKey, 1L);
                Assert.Equal("high", values.GraphicsProfile);
                Assert.Equal("4x", values.Msaa);
                Assert.Equal("75", values.RenderScale);
                Assert.Equal("on", values.LowResMaps);
                Assert.Equal(1, values.FpsListVersion);

                // Hand-edited nonsense reads as the default, as the page shows it: an unknown option, a wrong type, a number out of range.
                settings.Set(SettingsSchema.GraphicsMsaaKey, "16x");
                settings.Set(SettingsSchema.GraphicsRenderScaleKey, "500");
                settings.Set(SettingsSchema.GraphicsLowResMapsKey, true);
                settings.Set(SettingsSchema.GraphicsFpsListVersionKey, "1");
                Assert.Equal("auto", values.Msaa);
                Assert.Equal("auto", values.RenderScale);
                Assert.Equal("auto", values.LowResMaps);
                Assert.Equal(0, values.FpsListVersion);
                settings.Set(SettingsSchema.GraphicsFpsListVersionKey, 1.0);   // what a JSON reader may give back
                Assert.Equal(1, values.FpsListVersion);
            }
        }

        [Fact]
        public void ASaveIsValidatedAndNeverRunsOnTheCallingThread()
        {
            using (var env = new HostEnvironment("sm-save"))
            {
                PerformanceSettingsStore store = env.Host.PerformanceSettingsStore;
                var queued = new List<Action>();
                store.Run = work => queued.Add(work);
                string file = env.Server.Context.Settings.FilePath;
                Assert.NotNull(file);

                Assert.True(store.Save(SettingsSchema.GraphicsMsaaKey, "8X"));
                Assert.Single(queued);                                   // not written yet: the game thread never waits for the disk
                Assert.Equal("8x", store.Msaa);                         // but read back at once
                Assert.Null(env.Server.Context.Settings.Get(SettingsSchema.GraphicsMsaaKey));
                queued[0]();
                Assert.Equal("8x", env.Server.Context.Settings.Get(SettingsSchema.GraphicsMsaaKey));
                Assert.Equal("8x", Settings.Load(file, null).GetString(SettingsSchema.GraphicsMsaaKey, null));

                // The same value again: nothing to write.
                Assert.True(store.Save(SettingsSchema.GraphicsMsaaKey, "8x"));
                Assert.Single(queued);

                // Refused: not one of the page's keys, values the schema refuses, the marker as text or below 0.
                Assert.False(store.Save("graphics.fullProfile", true));
                Assert.False(store.Save(SettingsSchema.SettingsMenuTrimKey, false));
                Assert.False(store.Save(SettingsSchema.GraphicsMsaaKey, "16x"));
                Assert.False(store.Save(SettingsSchema.GraphicsMsaaKey, 4L));
                Assert.False(store.Save(SettingsSchema.GraphicsRenderScaleKey, "101"));
                Assert.False(store.Save(SettingsSchema.GraphicsRenderScaleKey, 50L));
                Assert.False(store.Save(SettingsSchema.GraphicsLowResMapsKey, "maybe"));
                Assert.False(store.Save(SettingsSchema.GraphicsFpsListVersionKey, "1"));
                Assert.False(store.Save(SettingsSchema.GraphicsFpsListVersionKey, -1L));
                Assert.False(store.Save(null, "auto"));
                Assert.Single(queued);

                Assert.True(store.Save(SettingsSchema.GraphicsRenderScaleKey, "0"));
                Assert.True(store.Save(SettingsSchema.GraphicsLowResMapsKey, "OFF"));
                Assert.True(store.Save("graphics.profile", "ultra"));
                Assert.True(store.Save(SettingsSchema.GraphicsFpsListVersionKey, 1));
                Assert.Equal(5, queued.Count);
                foreach (Action work in queued.Skip(1)) work();
                Settings reread = Settings.Load(file, null);
                Assert.Equal("0", reread.GetString(SettingsSchema.GraphicsRenderScaleKey, null));
                Assert.Equal("off", reread.GetString(SettingsSchema.GraphicsLowResMapsKey, null));
                Assert.Equal("ultra", reread.GetString("graphics.profile", null));
                Assert.Equal(1L, reread.GetLong(SettingsSchema.GraphicsFpsListVersionKey, 0));
                // The marker is written once.
                Assert.True(store.Save(SettingsSchema.GraphicsFpsListVersionKey, 1L));
                Assert.Equal(5, queued.Count);
            }
        }

        [Fact]
        public void AFailedSaveIsLoggedAndTheStoredValueComesBack()
        {
            var logs = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(logs);
            var settings = new Settings();   // in memory: Save throws ("not backed by a file")
            var store = new PerformanceSettingsStore(settings, logger.For("admin"));
            Action pending = null;
            store.Run = work => pending = work;
            Assert.True(store.Save(SettingsSchema.GraphicsLowResMapsKey, "on"));
            Assert.Equal("on", store.LowResMaps);
            pending();
            Assert.Equal("auto", store.LowResMaps);   // SetAndSave put the old value back; the pending one is gone
            Assert.Contains(logs.Snapshot(), e => e.Level == LogLevel.Warn && e.Message == "admin: settings menu: graphics.lowResMaps could not be saved");
            // The default runner is the thread pool: Save returns at once and the value lands.
            var file = new PerformanceSettingsStore(Settings.Load(Path.Combine(TestEnvironment.NewTempDir("sm-pool"), "settings.json"), null), null);
            Assert.True(file.Save(SettingsSchema.GraphicsMsaaKey, "2x"));
            Assert.Equal("2x", file.Msaa);
        }

        // ------------------------------------------------------------------ FlashInterop over the fake UI

        [Fact]
        public void TheFlashScopeReadsTheTreeAndDisposesEveryWrapper()
        {
            var ui = new FakeFlashTree();
            ui.OpenSettings();
            using (var flash = new FlashScope(ui))
            {
                object stack = flash.Class("gluic.components.menus", "MenusStack");
                object menu = flash.Call(stack, "getMenuOnTopOfStack");
                Assert.Equal("menu_settings", ((FlashCharacter)menu).Name);
                object panel = flash.Child(menu, "mc_panel");
                Assert.Equal(3, ((FlashCharacter)panel).CurrentFrame);
                Assert.Equal(3.0, flash.Number(panel, "currentFrame"));
                object forum = flash.Child(panel, "btn_forum");
                Assert.Equal(465.35, flash.Number(forum, "width").Value, 3);
                Assert.Equal(6.35, flash.Number(forum, "x").Value, 3);
                Assert.Null(flash.Child(panel, "btn_performance"));   // Android has no such instance
                Assert.Null(flash.Class("managers", "NoSuchClass"));
                Assert.Equal("setting.ForumButton", flash.Get(forum, "title"));
                flash.Set(forum, "x", (float)484);
                Assert.True(flash.Count > 0);
            }
            ui.AssertAllDisposed();
            Assert.Equal(new[] { "menu_settings.mc_panel.btn_forum.x=484" }, ui.HostWrites);
            Assert.Empty(ui.ManagedWrites);
            Assert.Equal(0, ui.GenericCalls);
            // A double is not an AS Number (ASValue.SetObject makes it a managed object): the fake flags it.
            using (var flash = new FlashScope(ui))
            {
                object menu = flash.Call(flash.Class("gluic.components.menus", "MenusStack"), "getMenuOnTopOfStack");
                flash.Set(flash.Child(flash.Child(menu, "mc_panel"), "btn_help"), "x", 12.5);
            }
            Assert.Equal(new[] { "menu_settings.mc_panel.btn_help.x" }, ui.ManagedWrites);
        }

        [Fact]
        public void TheNativeHelpersUseTheEnginesOwnTable()
        {
            var ui = new FakeFlashTree();
            // (true, true) twice with one target: one delegate of ours, after the game's.
            var ours = new Target();
            FlashInterop.OverrideNative(ui, "CanChangeMaxFramerate", new Func<object>(ours.No), true, true);
            FlashInterop.OverrideNative(ui, "CanChangeMaxFramerate", new Func<object>(ours.No), true, true);
            Assert.Equal(2, ui.DelegatesOf("CanChangeMaxFramerate").Count);
            Assert.Equal(1, ui.CountWithTarget("CanChangeMaxFramerate", ours));
            Assert.Equal(false, ui.Invoke("CanChangeMaxFramerate"));   // our boxed false is the last non-null
            // (true, false) replaces the game's delegate.
            FlashInterop.OverrideNative(ui, "GoToForum", new Action(ours.Forum), true, false);
            Assert.Single(ui.DelegatesOf("GoToForum"));
            ui.Invoke("GoToForum");
            Assert.Equal(1, ours.Forums);
            Assert.Equal(0, ui.Game.ForumCalls);
            FlashInterop.GoToForum(ui);   // UIComponent.GoToForum() itself: the stock body (or patch 'forum')
            Assert.Equal(1, ui.Game.ForumCalls);
            FlashInterop.RemoveNative(ui, "GoToForum");
            Assert.False(ui.IsRegistered("GoToForum"));
            Assert.Throws<MissingMethodException>(() => FlashInterop.OverrideNative(new object(), "x", new Func<object>(ours.No), true, true));
        }

        [Fact]
        public void TheConfirmPopupIsTheCallbackOverloadWithItsDefaultPriority()
        {
            var ui = new FakeFlashTree();
            bool? answer = null;
            var data = new Dictionary<string, object> { { "msg", SettingsMenuNames.RestartMessage } };
            FlashInterop.ShowConfirmPopup(ui, "SHOW_CONFIRM", data, r => answer = (bool)r["result"]);
            FakePopup popup = Assert.Single(ui.Popups);
            Assert.Equal("SHOW_CONFIRM", popup.EventName);
            Assert.Equal(FakePopupPriority.AtEnd, popup.Priority);   // the game's default, not 0
            Assert.Equal(SettingsMenuNames.RestartMessage, popup.Data["msg"]);
            Assert.True(popup.HasCallback);
            popup.Answer(true);
            Assert.True(answer);
            Assert.Throws<MissingMethodException>(() => FlashInterop.ShowConfirmPopup(new object(), "SHOW_CONFIRM", data, null));
        }

        // ------------------------------------------------------------------ the host wiring

        [Fact]
        public void TheHostHandsItsSettingsToThePerformanceValuesAtStart()
        {
            string root = TestEnvironment.NewTempDir("sm-host");
            Directory.CreateDirectory(Path.Combine(root, "Data"));
            PerformancePrefs.Configure(null);
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                   new EmbeddedHostOptions { Port = TestEnvironment.FreePort(), Watchdog = false, StatusIntervalMs = 0 });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.NotNull(host.Server);
                // The start-time values (graphics.msaa, the FPS list) come from the host's own copy, read before the game's options.
                Assert.Same(host.Settings, PerformancePrefs.HostSettings);
                Assert.NotNull(AdminHooks.Host);
            }
            finally
            {
                AdminHooks.Host = null;
                PerformancePrefs.Configure(null);
                host.Server?.Dispose();
                host.Logger.Dispose();
            }
        }

        private sealed class Target
        {
            public int Forums;

            public object No()
            {
                return false;
            }

            public void Forum()
            {
                Forums++;
            }
        }

        /// <summary>A server (not listening), an AdminHost over the fake game whose GameReflection knows FakeUiExtensions.</summary>
        private sealed class HostEnvironment : IDisposable
        {
            public HostEnvironment(string name)
            {
                Logs = new MemoryLogSink();
                Server = AdminFixtures.CreateServer(TestEnvironment.NewTempDir(name), Logs);
                Game = FakeGame.Install();
                var reflection = new GameReflection();
                Assert.True(reflection.Bind(typeof(FakeGameApplication), typeof(FakeProfile), typeof(FakeUiExtensions)));
                Host = new AdminHost(Server.Context, new RecordingAdminPlatform(), reflection, null);
            }

            public MemoryLogSink Logs { get; }
            public McvsServer Server { get; }
            public FakeGame Game { get; }
            public AdminHost Host { get; }

            public void Dispose()
            {
                FakeUiExtensions.Ui = null;
                FakeGame.Uninstall();
                Server.Dispose();
            }
        }
    }
}
