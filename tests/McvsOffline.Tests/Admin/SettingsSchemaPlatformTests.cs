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
            || key == "diagnostics.publicLogCopy" || key == "diagnostics.threadDump";

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
            Assert.DoesNotContain("embedded.physics.keepPhysicsLayer", AllKeys(views));
        }

        [Fact]
        public void WindowsLeavesOutTheAndroidOnlyKeys()
        {
            List<SettingGroupView> views = SettingsSchema.GroupsFor("windows");
            List<string> keys = AllKeys(views).ToList();
            Assert.DoesNotContain(keys, AndroidOnly);
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
