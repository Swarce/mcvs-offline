using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using Xunit;

namespace McvsOffline.Tests.Core
{
    /// <summary>Settings.LoadError, MoveAsideAndReset and SetAndSave (design §4.4, the settings page's repair and writes).</summary>
    public class SettingsRepairTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 26, 10, 15, 0, DateTimeKind.Utc);

        [Fact]
        public void ABrokenFileSetsLoadErrorAndRepairMovesItAside()
        {
            using var dir = new TempDir();
            string path = dir.Combine("settings.json");
            const string broken = "{ \"port\": 51780, \"superPassword\": \"mine\", oops }";
            File.WriteAllText(path, broken);

            Settings settings = Settings.Load(path, null);
            Assert.True(settings.IsFallback);
            Assert.Contains("offset", settings.LoadError);
            Assert.Throws<InvalidOperationException>(() => settings.Save());

            string moved = settings.MoveAsideAndReset(Now);
            Assert.Equal(path + ".broken-20261026T101500Z", moved);
            Assert.Equal(broken, File.ReadAllText(moved));
            Assert.False(settings.IsFallback);
            Assert.Null(settings.LoadError);
            var saved = MiniJson.ParseObject(File.ReadAllText(path));
            Assert.Equal(51780L, saved["port"]);
            Assert.Equal(Settings.DefaultSuperPassword, saved["superPassword"]);

            // Saving works again, and a second repair is refused (nothing to repair).
            settings.Set("instantBots", false);
            settings.Save();
            Assert.Throws<InvalidOperationException>(() => settings.MoveAsideAndReset(Now));
            Assert.False(Settings.Load(path, null).IsFallback);
        }

        [Fact]
        public void AFileThatIsNotAnObjectAndNameClashes()
        {
            using var dir = new TempDir();
            string path = dir.Combine("settings.json");
            File.WriteAllText(path, "[1, 2]");
            File.WriteAllText(path + ".broken-20261026T101500Z", "earlier");

            Settings settings = Settings.Load(path, null);
            Assert.True(settings.IsFallback);
            Assert.Equal("the file is not a JSON object", settings.LoadError);
            string moved = settings.MoveAsideAndReset(Now);
            Assert.Equal(path + ".broken-20261026T101500Z-2", moved);
            Assert.Equal("earlier", File.ReadAllText(path + ".broken-20261026T101500Z"));
            Assert.Equal("[1, 2]", File.ReadAllText(moved));
        }

        [Fact]
        public void AFileThatVanishedIsNotAnError()
        {
            using var dir = new TempDir();
            string path = dir.Combine("settings.json");
            File.WriteAllText(path, "not json");
            Settings settings = Settings.Load(path, null);
            File.Delete(path);
            Assert.Null(settings.MoveAsideAndReset(Now));
            Assert.True(File.Exists(path));
        }

        [Fact]
        public void RepairNeedsAFallbackBackedByAFile()
        {
            Assert.Throws<InvalidOperationException>(() => new Settings().MoveAsideAndReset(Now));
            using var dir = new TempDir();
            Settings fine = Settings.Load(dir.Combine("settings.json"), null);
            Assert.False(fine.IsFallback);
            Assert.Null(fine.LoadError);
            Assert.Throws<InvalidOperationException>(() => fine.MoveAsideAndReset(Now));
        }

        [Fact]
        public void SetAndSaveWritesEverythingOrNothing()
        {
            using var dir = new TempDir();
            string path = dir.Combine("settings.json");
            Settings settings = Settings.Load(path, null);
            settings.SetAndSave(new Dictionary<string, object> { ["economy.xpMultiplier"] = 2.0, ["admin.redeemCode"] = "ABCD" });
            var saved = MiniJson.ParseObject(File.ReadAllText(path));
            Assert.Equal(2.0, JsonPath.Get(saved, "economy.xpMultiplier"));
            Assert.Equal("ABCD", JsonPath.Get(saved, "admin.redeemCode"));

            // Make the save fail (a directory where the file should go): every value comes back, new keys disappear.
            File.Delete(path);
            Directory.CreateDirectory(path);
            Assert.ThrowsAny<Exception>(() => settings.SetAndSave(new Dictionary<string, object>
            {
                ["economy.xpMultiplier"] = 5.0,
                ["liveops.seasonDays"] = 14L,
            }));
            Assert.Equal(2.0, settings.GetDouble("economy.xpMultiplier", 0));
            Assert.Null(settings.Get("liveops.seasonDays"));
            Assert.Null(settings.Get("liveops"));
        }
    }
}
