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
