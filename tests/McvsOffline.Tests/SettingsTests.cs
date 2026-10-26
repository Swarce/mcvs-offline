using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using Xunit;

namespace McvsOffline.Tests
{
    public class SettingsTests
    {
        [Fact]
        public void MissingFileIsCreatedWithDefaults()
        {
            using var dir = new TempDir();
            string path = dir.Combine("settings.json");
            var s = Settings.Load(path, null);
            Assert.True(File.Exists(path));
            Assert.Equal(51780, s.Port);
            Assert.Equal("127.0.0.1", s.Bind);
            Assert.False(s.UnlockAll);
            Assert.False(s.DevMenu);
            Assert.True(s.InstantBots);
            Assert.False(s.LanMode);
            Assert.Equal(1.0, s.CreditsMultiplier);
            Assert.Equal("game:mcvsoffline", s.SuperCredential);
            Assert.Equal("mcvsoffline", s.SuperPassword);
            var written = MiniJson.ParseObject(File.ReadAllText(path));
            Assert.Equal(51780L, written["port"]);
            Assert.True(written.ContainsKey("economy"));
        }

        [Fact]
        public void UserValuesAndUnknownKeysArePreservedAndNewDefaultsAdded()
        {
            using var dir = new TempDir();
            string path = dir.Combine("settings.json");
            File.WriteAllText(path, "{\"port\":12345,\"unlockAll\":true,\"economy\":{\"creditsMultiplier\":2.5},\"storage\":{\"myModuleKey\":7}}");
            var s = Settings.Load(path, null);
            Assert.Equal(12345, s.Port);
            Assert.True(s.UnlockAll);
            Assert.Equal(2.5, s.CreditsMultiplier);
            Assert.Equal(1.0, s.VersusCoinsMultiplier);
            Assert.Equal(7L, s.GetLong("storage.myModuleKey", 0));
            Assert.True(s.GetBool("storage.absent", true));

            var written = MiniJson.ParseObject(File.ReadAllText(path));
            Assert.Equal(12345L, written["port"]);
            Assert.Equal(7L, JsonPath.Get(written, "storage.myModuleKey"));
            Assert.Equal(1.0, JsonPath.Get(written, "economy.versusCoinsMultiplier")); // new default added
        }

        [Fact]
        public void InvalidFileIsLeftUntouchedAndDefaultsAreUsed()
        {
            using var dir = new TempDir();
            string path = dir.Combine("settings.json");
            File.WriteAllText(path, "{\"port\": 1234,,}");
            var logs = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(logs);
            var s = Settings.Load(path, logger.For("settings"));
            Assert.Equal(51780, s.Port);
            Assert.Equal("{\"port\": 1234,,}", File.ReadAllText(path));
            Assert.True(logs.Contains("not a valid JSON object"));

            // The in-memory defaults are never written over the player's file, whoever asks (a host adding its keys).
            Assert.True(s.IsFallback);
            s.Set("embedded.manageArgs", true);
            Assert.Throws<System.InvalidOperationException>(() => s.Save());
            Assert.Equal("{\"port\": 1234,,}", File.ReadAllText(path));
            Assert.True(s.GetBool("embedded.manageArgs", false)); // still usable in memory
        }

        [Fact]
        public void AReadableFileIsNotAFallback()
        {
            using var dir = new TempDir();
            string path = dir.Combine("settings.json");
            Assert.False(Settings.Load(path, null).IsFallback); // created
            Assert.False(Settings.Load(path, null).IsFallback); // read back
            Assert.False(new Settings().IsFallback);
        }

        [Fact]
        public void OverridesWinButAreNeverSaved()
        {
            using var dir = new TempDir();
            string path = dir.Combine("settings.json");
            var s = Settings.Load(path, null);
            s.Override("port", 0L);
            s.Override("economy", MiniJson.ParseObject("{\"creditsMultiplier\":3}"));
            Assert.Equal(0, s.Port);
            Assert.Equal(3.0, s.CreditsMultiplier);
            s.Set("devMenu", true);
            s.Save();
            var written = MiniJson.ParseObject(File.ReadAllText(path));
            Assert.Equal(51780L, written["port"]);
            Assert.Equal(true, written["devMenu"]);
            Assert.Equal(0L, JsonPath.Get(s.Snapshot(), "port"));
        }

        [Fact]
        public void ObjectOverridesMergeIntoTheFileInsteadOfHidingIt()
        {
            using var dir = new TempDir();
            string path = dir.Combine("settings.json");
            File.WriteAllText(path, "{\"assets\":{\"couponsEnabled\":true,\"coupons\":{\"A\":1}},\"economy\":{\"xpMultiplier\":4}}");
            var s = Settings.Load(path, null);

            // A parent override only replaces the keys it names (CONTENT request): deeper paths fall through.
            s.Override("assets", MiniJson.ParseObject("{\"couponsEnabled\":false}"));
            Assert.False(s.GetBool("assets.couponsEnabled", true));
            Assert.Equal(1L, s.GetLong("assets.coupons.A", 0));

            // A child override is visible through its parent object and the snapshot.
            s.Override("economy.priceMultiplier", 2.5);
            Dictionary<string, object> economy = s.GetObject("economy");
            Assert.Equal(2.5, economy["priceMultiplier"]);
            Assert.Equal(4L, economy["xpMultiplier"]);
            Assert.Equal(2.5, JsonPath.Get(s.Snapshot(), "economy.priceMultiplier"));

            // A scalar override over an object replaces it; later overrides apply on top, and Set stays visible.
            s.Override("economy", 7L);
            Assert.Equal(7L, s.Get("economy"));
            s.Set("devMenu", true);
            Assert.True(s.DevMenu);
            Assert.Equal(7L, s.Get("economy"));
        }

        /// <summary>
        /// The Cosmetic crate's opt-out is discoverable (docs/design/economy-collector.md §4.4, O7): a new settings.json has
        /// <c>economy.collector.enabled: true</c> and nothing else of the collector; an existing file gets it added and keeps
        /// its own collector keys; the plain opt-out (<c>"collector": false</c>) is kept and read as off.
        /// </summary>
        [Fact]
        public void CreateDefaultsHasCollectorEnabled()
        {
            Dictionary<string, object> defaults = Settings.CreateDefaults();
            Assert.Equal(true, JsonPath.Get(defaults, "economy.collector.enabled"));
            Assert.Single((Dictionary<string, object>)JsonPath.Get(defaults, "economy.collector"));
            Assert.True(McvsOffline.Server.Services.Crm.Collector.CollectorOptions.FromSettings(new Settings(), null).Enabled);

            using var dir = new TempDir();
            string path = dir.Combine("settings.json");
            Settings.Load(path, null);
            Assert.Equal(true, JsonPath.Get(MiniJson.ParseObject(File.ReadAllText(path)), "economy.collector.enabled"));

            File.WriteAllText(path, "{\"economy\":{\"xpMultiplier\":2,\"collector\":{\"itemsPerCrate\":3}}}");
            var s = Settings.Load(path, null);
            var written = MiniJson.ParseObject(File.ReadAllText(path));
            Assert.Equal(true, JsonPath.Get(written, "economy.collector.enabled"));
            Assert.Equal(3L, JsonPath.Get(written, "economy.collector.itemsPerCrate"));
            Assert.Equal(2L, JsonPath.Get(written, "economy.xpMultiplier"));
            Assert.Equal(3, McvsOffline.Server.Services.Crm.Collector.CollectorOptions.FromSettings(s, null).ItemsPerCrate);

            foreach (string optOut in new[] { "{\"economy\":{\"collector\":false}}", "{\"economy\":{\"collector\":{\"enabled\":false}}}" })
            {
                File.WriteAllText(path, optOut);
                s = Settings.Load(path, null);
                Assert.False(McvsOffline.Server.Services.Crm.Collector.CollectorOptions.FromSettings(s, null).Enabled, optOut);
                Assert.DoesNotContain("\"enabled\":true", File.ReadAllText(path).Replace(" ", ""));
            }
        }

        [Fact]
        public void ValuesThatWouldBreakCmdArgsFallBackToDefaults()
        {
            // The Controller splits lobbyconfigs cmd_args on '-' (verify-C2 §1.4).
            using var dir = new TempDir();
            string path = dir.Combine("settings.json");
            File.WriteAllText(path, "{\"superCredential\":\"game:mcvs-offline\",\"superPassword\":\"has space\",\"dataCenter\":\"\"}");
            var s = Settings.Load(path, null);
            Assert.Equal(Settings.DefaultSuperCredential, s.SuperCredential);
            Assert.Equal(Settings.DefaultSuperPassword, s.SuperPassword);
            Assert.Equal(Settings.DefaultDataCenter, s.DataCenter);
            Assert.Contains("game:mcvs-offline", File.ReadAllText(path)); // the player's file is not rewritten
            Assert.True(Settings.IsCmdArgSafe("http://127.0.0.1:51780/"));
            Assert.False(Settings.IsCmdArgSafe("mcvs-stub"));
        }
    }
}
