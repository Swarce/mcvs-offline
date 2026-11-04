using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Admin;
using McvsOffline.Embedded.Admin.SettingsMenu;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded.SettingsMenu
{
    /// <summary>
    /// Build 22's members on their own (SettingsMenuBuild22Tests drives them through the registry): the FORUM remap's copy and swap
    /// (<see cref="ForumRemap"/>), the breadcrumb (<see cref="SameFrameBreadcrumb"/>) and the host's side of the switch
    /// (<see cref="AdminHost.SettingsMenuSameFrame"/>, <see cref="AdminHost.SameFrameMarkerPaths"/>, <see cref="AdminHost.SwitchSameFrameOff"/>).
    /// </summary>
    [Collection("GameHooks")]
    public class SameFrameBreadcrumbTests
    {
        // ------------------------------------------------------------------ ForumRemap

        [Fact]
        public void TheRemapIsCopiedAndSwappedOnlyWhenItChanges()
        {
            var ui = new FakeFlashTree();
            var formatter = (FakeLocalizationFormatter)ui.World.LocalizationFormatter;
            formatter.Add("menu.Other", "menu.Else");                       // a Remap.json entry of the game's
            var original = (IDictionary)formatter.Map;
            Assert.Null(ForumRemap.Ensure(ui, true));
            Assert.NotSame(original, formatter.Map);
            Assert.False(original.Contains(new FakeLocId("setting.ForumButton")));   // the old one never changed
            Assert.Equal("setting.Performance", formatter.RemapLocId("setting.ForumButton"));
            Assert.Equal("menu.Else", formatter.RemapLocId("menu.Other"));
            Assert.True(ForumRemap.IsOn(ui));
            object installed = formatter.Map;
            Assert.Null(ForumRemap.Ensure(ui, true));                      // as asked already: no new dictionary
            Assert.Same(installed, formatter.Map);
            Assert.Null(ForumRemap.Ensure(ui, false));
            Assert.NotSame(installed, formatter.Map);
            Assert.Equal("setting.ForumButton", formatter.RemapLocId("setting.ForumButton"));
            Assert.Equal("menu.Else", formatter.RemapLocId("menu.Other"));
            Assert.False(ForumRemap.IsOn(ui));
        }

        [Fact]
        public void OffLeavesAGameEntryForTheSameIdAlone()
        {
            var ui = new FakeFlashTree();
            var formatter = (FakeLocalizationFormatter)ui.World.LocalizationFormatter;
            formatter.Add("setting.ForumButton", "setting.Community");
            Assert.Null(ForumRemap.Ensure(ui, false));
            Assert.Equal("setting.Community", formatter.RemapLocId("setting.ForumButton"));
        }

        [Fact]
        public void WithoutTheEngineMembersTheRemapSaysWhyAndNeverThrows()
        {
            var ui = new FakeFlashTree();
            Assert.Equal("no World", ForumRemap.Ensure(new object(), true));
            Assert.Equal("no UI", ForumRemap.Ensure(null, true));
            ui.World.LocalizationFormatter = null;
            Assert.Equal("no LocalizationFormatter", ForumRemap.Ensure(ui, true));
            ui.World.LocalizationFormatter = new object();
            Assert.Equal("no remapDictionary", ForumRemap.Ensure(ui, true));
            ui.World = null;
            Assert.Equal("no World", ForumRemap.Ensure(ui, true));
            Assert.False(ForumRemap.IsOn(ui));
        }

        [Fact]
        public void TheRemapLinesAreOnePerChange()
        {
            var host = new FakeSettingsMenuHost();
            var state = new SettingsMenuState();
            SettingsMenuLayout.EnsureRemap(host, state);
            SettingsMenuLayout.EnsureRemap(host, state);
            Assert.True(state.ForumRemapped);
            Assert.True(state.TileActive);
            host.PerformanceSetting = false;
            SettingsMenuLayout.EnsureRemap(host, state);
            Assert.False(state.ForumRemapped);
            host.Ui.World = null;
            host.PerformanceSetting = true;
            SettingsMenuLayout.EnsureRemap(host, state);
            SettingsMenuLayout.EnsureRemap(host, state);
            Assert.Equal(new[] { "settings menu: FORUM remap installed", "settings menu: FORUM remap removed", "settings menu: FORUM remap not installed (no World)" },
                         host.Lines("settings menu: FORUM remap"));
            // Steam: never touched.
            var steam = new FakeSettingsMenuHost(windows: true);
            SettingsMenuLayout.EnsureRemap(steam, new SettingsMenuState());
            Assert.False(((FakeLocalizationFormatter)steam.Ui.World.LocalizationFormatter).Contains("setting.ForumButton"));
            Assert.Empty(steam.Lines("settings menu: FORUM remap"));
        }

        // ------------------------------------------------------------------ SameFrameBreadcrumb

        [Fact]
        public void EnterWritesOnceInTheFirstFolderThatTakesItAndLeaveDeletesIt()
        {
            string dir = TestEnvironment.NewTempDir("sameframe-enter");
            string first = Path.Combine(dir, "missing", SettingsMenuNames.SameFrameMarkerFile);
            string second = Path.Combine(dir, SettingsMenuNames.SameFrameMarkerFile);
            var host = new FakeSettingsMenuHost { SameFrameMarkerPaths = new[] { first, second } };
            var state = new SettingsMenuState();
            SameFrameBreadcrumb.Enter(host, state, 7, "GetPlatformId");
            Assert.False(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.EndsWith(" focus-in 7 GetPlatformId\n", File.ReadAllText(second));
            Assert.Null(state.BreadcrumbProblem);
            Assert.Empty(Directory.GetFiles(dir).Where(f => f.EndsWith(".mcvs-tmp", StringComparison.Ordinal)));
            SameFrameBreadcrumb.Leave(state);
            Assert.False(File.Exists(second));
            // Once per process.
            SameFrameBreadcrumb.Enter(host, state, 8, "GetPlatformId");
            Assert.False(File.Exists(second));
            SameFrameBreadcrumb.Leave(state);
            SameFrameBreadcrumb.Leave(null);
            SameFrameBreadcrumb.Enter(null, state, 1, "x");
        }

        [Fact]
        public void CheckRunsOncePerProcess()
        {
            string dir = TestEnvironment.NewTempDir("sameframe-check");
            string marker = Path.Combine(dir, SettingsMenuNames.SameFrameMarkerFile);
            var host = new FakeSettingsMenuHost { SameFrameMarkerPaths = new[] { marker } };
            var state = new SettingsMenuState();
            SameFrameBreadcrumb.Check(host, state);
            Assert.True(state.BreadcrumbChecked);
            File.WriteAllText(marker, "x\n");                               // written by this process later: not a leftover
            SameFrameBreadcrumb.Check(host, state);
            Assert.Equal(0, host.SameFrameSwitchedOff);
            Assert.True(File.Exists(marker));
            Assert.Null(state.SameFrameSkipped);
        }

        // ------------------------------------------------------------------ AdminHost

        [Fact]
        public void TheHostReadsTheSwitchSavesItOffAndKnowsBothLogFolders()
        {
            string root = TestEnvironment.NewTempDir("sameframe-host");
            using (McvsServer server = AdminFixtures.CreateServer(root))
            {
                var host = new AdminHost(server.Context, null, new GameReflection(), null)
                {
                    LogsDir = Path.Combine(root, "logs"),
                    FallbackLogsDir = Path.Combine(root, "internal", "logs"),
                };
                Assert.True(host.SettingsMenuSameFrame);
                Assert.Equal(new[] { Path.Combine(root, "logs", SettingsMenuNames.SameFrameMarkerFile), Path.Combine(root, "internal", "logs", SettingsMenuNames.SameFrameMarkerFile) },
                             host.SameFrameMarkerPaths);
                Assert.True(host.SwitchSameFrameOff());
                Assert.False(host.SettingsMenuSameFrame);
                Assert.Equal(false, server.Context.Settings.Get(SettingsSchema.SettingsMenuSameFrameKey));
                // The page turns it back on (live): the next focus-in reads it.
                server.Context.Settings.SetAndSave(new Dictionary<string, object> { { SettingsSchema.SettingsMenuSameFrameKey, true } });
                Assert.True(host.SettingsMenuSameFrame);
                var bare = new AdminHost(server.Context, null, new GameReflection(), null);
                Assert.Empty(bare.SameFrameMarkerPaths);
            }
        }

        [Fact]
        public void StartChecksTheBreadcrumbOffTheGameThreadAndSavesTheSwitchOff()
        {
            string root = TestEnvironment.NewTempDir("sameframe-start");
            using (McvsServer server = AdminFixtures.CreateServer(root))
            {
                string logs = Path.Combine(root, "logs");
                Directory.CreateDirectory(logs);
                string marker = Path.Combine(logs, SettingsMenuNames.SameFrameMarkerFile);
                File.WriteAllText(marker, "2026-11-04T10:00:00.000Z focus-in 2 EnterSection\n");
                var host = new AdminHost(server.Context, null, new GameReflection(), null) { LogsDir = logs };
                AdminHooks.Host = host;
                UiNatives natives = UiNatives.Start(host);
                try
                {
                    Assert.NotNull(natives);
                    Assert.True(natives.State.BreadcrumbChecked);
                    Assert.False(File.Exists(marker));
                    Assert.Equal(false, server.Context.Settings.Get(SettingsSchema.SettingsMenuSameFrameKey));
                    Assert.StartsWith("settings menu: same frame skipped: the last start ended during it (marker " + marker + ")", natives.State.SameFrameSkipped);
                }
                finally
                {
                    if (natives != null) natives.Dispose();
                    AdminHooks.Host = null;
                }
            }
        }
    }
}
