using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>
    /// The settings page's platform filter (docs/design/pc-port.md §3.6, §4.6): the PC host shows no Android-only key, its
    /// own wording of graphics.profile and the keepPhysicsLayer key; every other platform sees today's schema.
    /// </summary>
    public class SettingsSchemaPlatformTests
    {
        private static bool AndroidOnly(string key) =>
            key.StartsWith("hd.", StringComparison.Ordinal) || key.StartsWith("install.", StringComparison.Ordinal)
            || key.StartsWith("notifications.", StringComparison.Ordinal)
            || key == "diagnostics.publicLogCopy" || key == "diagnostics.threadDump"
            || key == SettingsSchema.AccountMenuHideDeadButtonsKey  // no effect on PC: no Facebook/Google/Restore entry
            // The Android PERFORMANCE page and its values: Steam keeps its own page (docs/design/settings-menu.md §1.1).
            || key == "admin.settingsMenu.performance" || key == "graphics.msaa" || key == "graphics.renderScale"
            || key == "graphics.lowResMaps" || key == "graphics.fpsListVersion";

        private static IEnumerable<string> AllKeys(List<SettingGroupView> views) =>
            views.SelectMany(v => v.Keys.Select(k => k.Key).Concat(v.Advanced.Select(a => a.Key)));

        [Theory]
        [InlineData("android")]
        [InlineData(null)]
        [InlineData("directory")]
        [InlineData("fake")]
        public void EveryOtherPlatformGetsTodaysSchema(string platform)
        {
            List<SettingGroupView> views = SettingsSchema.GroupsFor(platform);
            Assert.Equal(SettingsSchema.Groups.Select(g => g.Id), views.Select(v => v.Group.Id));
            Assert.Equal(SettingsSchema.Groups.Select(g => g.Label), views.Select(v => v.Group.Label));
            foreach (SettingGroupView view in views)
            {
                if (view.Group.Id == "advanced")
                {
                    Assert.Empty(view.Keys);
                    Assert.Equal(SettingsSchema.AdvancedKeys, view.Advanced);
                    Assert.Empty(view.AdvancedHelp);
                    continue;
                }
                Assert.Empty(view.Advanced);
                List<SettingDefinition> expected = SettingsSchema.Keys.Where(k => k.Group == view.Group.Id).ToList();
                Assert.Equal(expected.Count, view.Keys.Count);
                for (int i = 0; i < expected.Count; i++)
                {
                    Assert.Same(expected[i], view.Keys[i]); // the same definitions, texts included
                }
            }
            Assert.Contains("hd.enabled", AllKeys(views));
            Assert.Contains("install.manageInstaller", AllKeys(views));
            Assert.Contains(SettingsSchema.AccountMenuHideDeadButtonsKey, AllKeys(views));
            Assert.Contains("admin.settingsMenu.performance", AllKeys(views));
            Assert.Contains("graphics.renderScale", AllKeys(views));
            Assert.Contains("graphics.fpsListVersion", AllKeys(views));
            Assert.Contains("logging.keepStarts", AllKeys(views));
            Assert.DoesNotContain("embedded.physics.keepPhysicsLayer", AllKeys(views));
        }

        [Fact]
        public void WindowsLeavesOutTheAndroidOnlyKeys()
        {
            List<SettingGroupView> views = SettingsSchema.GroupsFor("windows");
            List<string> keys = AllKeys(views).ToList();
            Assert.DoesNotContain(keys, AndroidOnly);
            Assert.DoesNotContain("admin.accountMenu.hideDeadButtons", keys);
            Assert.Contains("admin.redeemCode", keys);
            // The trim of the Settings grid is the same on both platforms; the PERFORMANCE page is Android's only.
            Assert.Contains("admin.settingsMenu.trim", keys);
            Assert.DoesNotContain("admin.settingsMenu.performance", keys);
            Assert.DoesNotContain("graphics.msaa", keys);
            Assert.DoesNotContain("graphics.fpsListVersion", keys);
            // The PC host deletes the logs of older starts the same way (McvsOffline\logs and %TEMP%\McvsOffline\logs).
            Assert.Equal(keys.IndexOf("logging.fileLevel") + 1, keys.IndexOf("logging.keepStarts"));
            // Everything else is still there, in the same order.
            List<string> android = AllKeys(SettingsSchema.GroupsFor("android")).Where(k => !AndroidOnly(k)).ToList();
            Assert.Equal(android, keys.Where(k => k != "embedded.physics.keepPhysicsLayer").ToList());
            Assert.Equal(SettingsSchema.Groups.Select(g => g.Id), views.Select(v => v.Group.Id));
        }

        [Fact]
        public void WindowsHasThePcWordingOfTheGraphicsProfile()
        {
            SettingDefinition pc = SettingsSchema.GroupsFor("windows").Single(v => v.Group.Id == "graphics").Keys.Single(k => k.Key == "graphics.profile");
            SettingDefinition android = SettingsSchema.Find("graphics.profile");
            Assert.Contains("the game's choice for this PC", pc.Help);
            Assert.Contains("Ultra: HighGPU on PC, which has no High_Ultra", pc.Help);
            // GameOptions_windows.json's GPU selection (WR2 verify #18): no "MidGPU on cards it does not know" on PC.
            Assert.DoesNotContain("MidGPU on", pc.Help);
            Assert.Contains("HighGPU only with at least 700 MB of GPU memory and 4 cores", pc.Help);
            Assert.Contains("MidGPU with at least 550 MB of GPU memory, 4 GB of RAM and 4 cores, otherwise LowGPU", pc.Help);
            Assert.Contains("The in-game Graphics quality (Settings › Graphics) overrides it.", pc.Help);
            Assert.Contains("32-bit process", pc.Help);
            Assert.DoesNotContain("phone", pc.Help);
            Assert.Contains("phone", android.Help); // the catalogue itself is unchanged
            // Only the text differs: type, options, default and apply are the same key.
            Assert.Equal(android.Key, pc.Key);
            Assert.Equal(android.Type, pc.Type);
            Assert.Equal(android.Options, pc.Options);
            Assert.Equal(android.Default, pc.Default);
            Assert.Equal(android.Apply, pc.Apply);
            Assert.Equal(android.Label, pc.Label);
        }

        /// <summary>No PC GPU block sets excludedLoadingLayers: on PC fullProfile only moves reducedAiUpdateFrequency.</summary>
        [Fact]
        public void WindowsHasThePcWordingOfFullProfile()
        {
            SettingDefinition pc = SettingsSchema.GroupsFor("windows").SelectMany(v => v.Keys).Single(k => k.Key == "graphics.fullProfile");
            SettingDefinition android = SettingsSchema.Find("graphics.fullProfile");
            Assert.Contains("AI update rate", pc.Help);
            Assert.Contains("On PC this is the only difference", pc.Help);
            Assert.DoesNotContain("stays unloaded", pc.Help);
            Assert.DoesNotContain("Physics", pc.Confirm);
            Assert.Contains("bot AI twice as often", pc.Confirm);
            Assert.Contains("stays unloaded", android.Help);      // the catalogue itself is unchanged
            Assert.Contains("Physics layer", android.Confirm);
            Assert.Same(android, SettingsSchema.GroupsFor("android").SelectMany(v => v.Keys).Single(k => k.Key == "graphics.fullProfile"));
            Assert.Equal(android.Type, pc.Type);
            Assert.Equal(android.Default, pc.Default);
            Assert.Equal(android.Apply, pc.Apply);
            Assert.Equal("High profiles change world loading too", android.Label);
            Assert.NotEqual(android.Label, pc.Label);
            Assert.DoesNotContain("world loading", pc.Label);
            Assert.Contains("AI rate", pc.Label);
            Assert.Equal(android.ConfirmWhen, pc.ConfirmWhen);
            Assert.Equal(android.BackupBefore, pc.BackupBefore);
        }

        /// <summary>The PC has no Downloads copies, thread dumps or exit files, and cleans %TEMP%\McvsOffline\logs too.</summary>
        [Fact]
        public void WindowsHasThePcWordingOfKeepStarts()
        {
            SettingDefinition pc = SettingsSchema.GroupsFor("windows").SelectMany(v => v.Keys).Single(k => k.Key == "logging.keepStarts");
            SettingDefinition android = SettingsSchema.Find("logging.keepStarts");
            Assert.Contains(@"McvsOffline\logs and %TEMP%\McvsOffline\logs", pc.Help);
            Assert.Contains("crash and exception files", pc.Help);
            Assert.DoesNotContain("Downloads", pc.Help);
            Assert.DoesNotContain("thread dump", pc.Help);
            Assert.DoesNotContain("exit files", pc.Help);
            Assert.Contains("Downloads/MCVS-Offline", android.Help);   // the catalogue itself is unchanged
            Assert.Equal(android.Help, SettingsSchema.GroupsFor("android").SelectMany(v => v.Keys).Single(k => k.Key == "logging.keepStarts").Help);
            Assert.Equal(android.Type, pc.Type);
            Assert.Equal(android.Default, pc.Default);
            Assert.Equal(android.Apply, pc.Apply);
            Assert.Equal(android.Label, pc.Label);
        }

        /// <summary>WR2 F5: the PC game enables every level itself, so the setting filters game.log instead.</summary>
        [Fact]
        public void WindowsHasThePcWordingOfGameLogLevels()
        {
            SettingDefinition pc = SettingsSchema.GroupsFor("windows").SelectMany(v => v.Keys).Single(k => k.Key == "gameLog.levels");
            SettingDefinition android = SettingsSchema.Find("gameLog.levels");
            Assert.Contains("game.log keeps Fatal plus the levels chosen here", pc.Help);
            Assert.DoesNotContain("--game-log", pc.Help);
            Assert.Contains("--game-log all", android.Help);
            Assert.Equal(android.Help, SettingsSchema.GroupsFor("android").SelectMany(v => v.Keys).Single(k => k.Key == "gameLog.levels").Help);
            Assert.Equal(android.Default, pc.Default);
            Assert.Equal(android.Label, pc.Label);
        }

        [Fact]
        public void WindowsShowsKeepPhysicsLayerWithItsLabelAndDescription()
        {
            SettingGroupView advanced = SettingsSchema.GroupsFor("windows").Single(v => v.Group.Id == "advanced");
            int gate = advanced.Advanced.FindIndex(kv => kv.Key == "embedded.physics.stepGateTimeoutMs");
            KeyValuePair<string, string> keep = advanced.Advanced[gate + 1];
            Assert.Equal("embedded.physics.keepPhysicsLayer", keep.Key);
            Assert.Equal("Keep the levels' Physics layer", keep.Value);
            Assert.Contains("excludedLoadingLayers", advanced.AdvancedHelp[keep.Key]);
            Assert.Contains("GameOptions_windows.json", advanced.AdvancedHelp[keep.Key]);
            Assert.Single(advanced.AdvancedHelp);
            // Read-only: it is no editable key, so the page cannot write it.
            Assert.Null(SettingsSchema.Find("embedded.physics.keepPhysicsLayer"));
        }

        [Fact]
        public void TheServedSchemaFollowsTheHostsPlatform()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var host = new FakeAdminHost();
            host.Info.Platform = "android";
            server.Context.Features.Set<IAdminHost>(host);
            var client = new AdminTestClient(server);
            client.OpenSession();

            string android = MiniJson.Serialize(AdminTestClient.Json(client.Get("settings"))["groups"]);
            Assert.Contains("\"hd.enabled\"", android);
            Assert.Contains("\"notifications.request\"", android);
            Assert.DoesNotContain("keepPhysicsLayer", android);

            host.Info.Platform = "windows";
            var groups = (List<object>)AdminTestClient.Json(client.Get("settings"))["groups"];
            string windows = MiniJson.Serialize(groups);
            foreach (string key in new[] { "\"hd.enabled\"", "\"install.manageInstaller\"", "\"notifications.request\"",
                                           "\"diagnostics.publicLogCopy\"", "\"diagnostics.threadDump\"" })
                Assert.DoesNotContain(key, windows);
            Assert.Contains("this PC", windows);
            var keep = groups.Cast<Dictionary<string, object>>().Single(g => (string)g["id"] == "advanced")["keys"] as List<object>;
            var entry = keep.Cast<Dictionary<string, object>>().Single(e => (string)e["key"] == "embedded.physics.keepPhysicsLayer");
            Assert.Equal("Keep the levels' Physics layer", entry["label"]);
            Assert.Equal(false, entry["editable"]);
            Assert.Contains("Physics", (string)entry["help"]);
        }
    }
}
