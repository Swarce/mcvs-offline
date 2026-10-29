using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Server.Services.Crm;
using McvsOffline.Server.Services.Crm.Collector;
using McvsOffline.Server.Services.Social.LiveOps;
using McvsOffline.Server.Services.Storage;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>The settings catalogue (design §3) and its validation.</summary>
    public class SettingsSchemaTests
    {
        /// <summary>§3.1, in page order.</summary>
        private static readonly string[] EditableKeys =
        {
            "instantBots", "devMenu", "leaderboards.virtualPlayers", "geo.country",
            "graphics.profile", "graphics.fullProfile", "hd.enabled",
            "economy.versusCoinsMultiplier", "economy.creditsMultiplier", "economy.xpMultiplier", "economy.priceMultiplier", "crm.dailyCapMultiplier",
            "economy.collector.enabled", "economy.collector.itemsPerCrate",
            "unlockAll", "assets.couponsEnabled", "crm.redeemCoupons", "unlocks.batchSize", "admin.redeemCode",
            "liveops.enabled", "liveops.divisionSeasons", "liveops.dailyQuests", "liveops.weeklyEvents", "liveops.seasonDays",
            "liveops.seasonStart", "liveops.dailyResetHour", "liveops.modifiers", "liveops.difficulty", "liveops.seasonRewardMultiplier",
            "crm.newsfeedLink",
            "storage.backups.keep", "storage.backups.intervalMinutes", "storage.autoRestoreCorrupt", "logging.fileLevel", "logging.keepStarts",
            "gameLog.levels",
            "gameLog.mute", "gameLog.file", "diagnostics.watchdog", "diagnostics.threadDump", "diagnostics.publicLogCopy", "tracking.disable",
        };

        private static SettingDefinition Def(string key) => SettingsSchema.Find(key) ?? throw new Exception(key + " not editable");

        private static object Default(string key) => Def(key).Default;

        [Fact]
        public void TheCatalogueIsComplete()
        {
            Assert.Equal(EditableKeys, SettingsSchema.Keys.Select(k => k.Key).ToArray());
            var groups = SettingsSchema.Groups.Select(g => g.Id).ToList();
            Assert.Equal(new[] { "gameplay", "graphics", "economy", "unlocks", "liveops", "diagnostics", "advanced" }, groups);
            foreach (SettingDefinition def in SettingsSchema.Keys)
            {
                Assert.False(string.IsNullOrEmpty(def.Label), def.Key);
                Assert.NotNull(def.Help);
                Assert.Contains(def.Type, new[] { "bool", "int", "number", "enum", "string", "tokens" });
                Assert.Contains(def.Apply, new[] { "live", "menu", "restart" });
                Assert.Contains(def.Group, groups);
                Assert.True(def.Editable);
                Assert.NotNull(def.Default);
                Assert.True(SettingsSchema.Validate(def, def.Default, out object normalized, out string problem), def.Key + ": " + problem);
                Assert.True(JsonValue.DeepEquals(def.Default, normalized), def.Key);
                if (def.Confirm != null) Assert.Contains(def.ConfirmWhen, new[] { "any", "enable", "decrease" });
                if (def.BackupBefore != null) Assert.Contains(def.BackupBefore, new[] { "any", "enable" });
            }
            Assert.Equal("enable", Def("devMenu").BackupBefore);
            Assert.Equal("enable", Def("unlockAll").BackupBefore);
            Assert.Equal("any", Def("liveops.seasonDays").BackupBefore);
            Assert.Equal("any", Def("liveops.seasonStart").BackupBefore);
            Assert.Equal("decrease", Def("storage.backups.keep").ConfirmWhen);
            Assert.Null(Def("storage.backups.keep").BackupBefore);
        }

        /// <summary>The log retention's setting (host key, read once at start): 0..10 previous starts, default 1.</summary>
        [Fact]
        public void KeepStartsIsAnIntegerFromZeroToTenAppliedAtTheNextStart()
        {
            SettingDefinition def = Def("logging.keepStarts");
            Assert.Equal("diagnostics", def.Group);
            Assert.Equal("int", def.Type);
            Assert.Equal(1L, def.Default);
            Assert.Equal(0d, def.Min.Value);
            Assert.Equal(10d, def.Max.Value);
            Assert.Equal("restart", def.Apply);
            Assert.Contains("Saves, settings and the zips you saved are never deleted", def.Help);
            Assert.Null(JsonPath.Get(Settings.CreateDefaults(), "logging.keepStarts"));   // not a standalone server key
            Assert.True(SettingsSchema.Validate(def, 0L, out object normalized, out _));
            Assert.Equal(0L, normalized);
            Assert.True(SettingsSchema.Validate(def, 10L, out normalized, out _));
            Assert.False(SettingsSchema.Validate(def, 11L, out _, out _));
            Assert.False(SettingsSchema.Validate(def, -1L, out _, out _));
        }

        [Fact]
        public void DefaultsAreTheModulesDefaults()
        {
            Dictionary<string, object> core = Settings.CreateDefaults();
            foreach (string key in new[] { "instantBots", "devMenu", "unlockAll", "economy.versusCoinsMultiplier", "economy.creditsMultiplier",
                                           "economy.xpMultiplier", "economy.priceMultiplier", "logging.fileLevel", "economy.collector.enabled" })
                Assert.True(JsonValue.DeepEquals(JsonPath.Get(core, key), Default(key)), key);

            var storage = new StorageSettings(new Settings());
            Assert.Equal((long)storage.BackupsKeep, Default("storage.backups.keep"));
            Assert.Equal(storage.BackupIntervalMinutes, Default("storage.backups.intervalMinutes"));
            Assert.Equal(storage.AutoRestoreCorrupt, Default("storage.autoRestoreCorrupt"));

            LiveOpsSettings liveOps = LiveOpsSettings.Defaults();
            Assert.Equal(liveOps.Enabled, Default("liveops.enabled"));
            Assert.Equal(liveOps.DivisionSeasons, Default("liveops.divisionSeasons"));
            Assert.Equal(liveOps.DailyQuests, Default("liveops.dailyQuests"));
            Assert.Equal(liveOps.WeeklyEvents, Default("liveops.weeklyEvents"));
            Assert.Equal((long)liveOps.SeasonDays, Default("liveops.seasonDays"));
            Assert.Equal((long)liveOps.DailyResetHour, Default("liveops.dailyResetHour"));
            Assert.Equal(TimeFormat.Iso8601(liveOps.SeasonAnchor), Default("liveops.seasonStart"));
            Assert.Equal(liveOps.Modifiers, Default("liveops.modifiers"));
            Assert.Equal(liveOps.Difficulty, Default("liveops.difficulty"));
            Assert.Equal(liveOps.SeasonRewardMultiplier, Default("liveops.seasonRewardMultiplier"));

            var collector = new CollectorOptions();
            Assert.Equal(collector.Enabled, Default("economy.collector.enabled"));
            Assert.Equal((long)collector.ItemsPerCrate, Default("economy.collector.itemsPerCrate"));
            Assert.Equal(5L, Default("economy.collector.itemsPerCrate"));   // O3

            var crm = new CrmOptions();
            Assert.Equal(crm.DailyCapMultiplier, Default("crm.dailyCapMultiplier"));
            Assert.Equal(crm.RedeemCoupons, Default("crm.redeemCoupons"));
            Assert.Equal(crm.InstantBots, Default("instantBots"));

            // Read inline by their modules (GeoService, LeaderboardStore, AssetsService, UnlockGranter, MiscService) and the
            // embedded host's AddEmbeddedDefaults.
            Assert.Equal("US", Default("geo.country"));
            Assert.Equal(true, Default("leaderboards.virtualPlayers"));
            Assert.Equal(true, Default("assets.couponsEnabled"));
            Assert.Equal(400L, Default("unlocks.batchSize"));
            Assert.Equal(false, Default("tracking.disable"));
            Assert.Equal("error warning", Default("gameLog.levels"));
            Assert.Equal("profile:debug", Default("gameLog.mute"));
            Assert.Equal(true, Default("gameLog.file"));
            Assert.Equal(true, Default("diagnostics.watchdog"));
            Assert.Equal(true, Default("diagnostics.threadDump"));
            Assert.Equal(true, Default("diagnostics.publicLogCopy"));
            Assert.Equal("MCVSOFFLINE", Default("admin.redeemCode"));
            Assert.Equal(true, Default("crm.newsfeedLink"));

            // Read by the embedded host (EmbeddedHost.ConfigureGraphics, AddEmbeddedDefaults) and patch 'graphics'.
            Assert.Equal("auto", Default("graphics.profile"));
            Assert.Equal(new[] { "auto", "mid", "high", "ultra" }, Def("graphics.profile").Options);
            Assert.Equal("restart", Def("graphics.profile").Apply);
            Assert.Equal(false, Default("graphics.fullProfile"));
            Assert.Equal("enable", Def("graphics.fullProfile").ConfirmWhen);
            Assert.Null(Def("graphics.fullProfile").BackupBefore);
            Assert.Equal(true, Default("hd.enabled"));
            Assert.Equal("restart", Def("hd.enabled").Apply);
        }

        [Theory]
        [InlineData("port")]
        [InlineData("bind")]
        [InlineData("publicUrl")]
        [InlineData("lanMode")]
        [InlineData("superCredential")]
        [InlineData("superPassword")]
        [InlineData("dataCenter")]
        [InlineData("pandora.urls.storage")]
        [InlineData("embedded.manageArgs")]
        [InlineData("embedded.lanAdvertiseHost")]
        [InlineData("crm.overlay")]
        [InlineData("crm.pointcutActions")]
        [InlineData("crm.developerIpRange")]
        [InlineData("assets.coupons")]
        [InlineData("admin.enabled")]
        [InlineData("admin.openVia")]
        [InlineData("admin.restartVia")]
        [InlineData("security.hostCheck")]
        [InlineData("security")]
        [InlineData("install.manageInstaller")]
        [InlineData("economy")]
        [InlineData("nothing.like.this")]
        [InlineData("")]
        [InlineData(null)]
        public void DeniedAndUnknownKeysAreNeverEditable(string key)
        {
            Assert.Null(SettingsSchema.Find(key));
        }

        [Fact]
        public void NoEditableKeyIsOnTheDenylist()
        {
            foreach (SettingDefinition def in SettingsSchema.Keys) Assert.False(SettingsSchema.IsDenied(def.Key), def.Key);
            Assert.True(SettingsSchema.IsDenied("pandora"));
            Assert.Contains(SettingsSchema.AdvancedKeys, kv => kv.Key == "superPassword");
            Assert.Contains(SettingsSchema.AdvancedKeys, kv => kv.Key == "security.hostCheck");
        }

        private static (bool ok, object value) Check(string key, object value)
        {
            bool ok = SettingsSchema.Validate(Def(key), value, out object normalized, out string problem);
            if (!ok) Assert.False(string.IsNullOrEmpty(problem));
            return (ok, normalized);
        }

        [Fact]
        public void BoolsAreJsonBooleans()
        {
            Assert.Equal((true, (object)true), Check("instantBots", true));
            Assert.False(Check("instantBots", "true").ok);
            Assert.False(Check("instantBots", 1L).ok);
            Assert.False(Check("instantBots", null).ok);
        }

        [Fact]
        public void IntsAreWholeNumbersInThePageRange()
        {
            Assert.Equal((true, (object)14L), Check("liveops.seasonDays", 14L));
            Assert.Equal((true, (object)14L), Check("liveops.seasonDays", 14.0));
            Assert.False(Check("liveops.seasonDays", 14.5).ok);
            Assert.False(Check("liveops.seasonDays", 0L).ok);
            Assert.False(Check("liveops.seasonDays", 29L).ok);
            Assert.False(Check("liveops.seasonDays", "7").ok);
            Assert.True(Check("storage.backups.keep", 0L).ok);
            Assert.False(Check("storage.backups.keep", 201L).ok);
        }

        [Fact]
        public void NumbersAreFiniteAndInRange()
        {
            Assert.Equal((true, (object)2.0), Check("economy.versusCoinsMultiplier", 2L));
            Assert.Equal((true, (object)0.1), Check("economy.versusCoinsMultiplier", 0.1));
            Assert.True(Check("economy.versusCoinsMultiplier", 20.0).ok);
            Assert.False(Check("economy.versusCoinsMultiplier", 20.5).ok);
            Assert.False(Check("economy.versusCoinsMultiplier", 0.0).ok);
            Assert.False(Check("economy.versusCoinsMultiplier", double.NaN).ok);
            Assert.False(Check("economy.versusCoinsMultiplier", double.PositiveInfinity).ok);
            Assert.False(Check("economy.versusCoinsMultiplier", "2").ok);
            Assert.True(Check("economy.priceMultiplier", 0.05).ok);
            Assert.False(Check("economy.priceMultiplier", 0.04).ok);
        }

        [Fact]
        public void EnumsAndTokensComeFromTheirOptions()
        {
            Assert.Equal((true, (object)"coinRush"), Check("liveops.modifiers", "COINRUSH"));
            Assert.False(Check("liveops.modifiers", "doubleXp").ok);
            Assert.False(Check("liveops.modifiers", 1L).ok);
            Assert.Equal((true, (object)"info"), Check("logging.fileLevel", "info"));

            Assert.Equal((true, (object)"warning error"), Check("gameLog.levels", " Warning  error "));
            Assert.Equal((true, (object)""), Check("gameLog.levels", ""));
            Assert.False(Check("gameLog.levels", "error error").ok);
            Assert.False(Check("gameLog.levels", "fatal").ok);
        }

        [Theory]
        [InlineData("geo.country", "DE", true)]
        [InlineData("geo.country", "de", false)]
        [InlineData("geo.country", "DEU", false)]
        [InlineData("geo.country", "", false)]
        [InlineData("admin.redeemCode", "", true)]
        [InlineData("admin.redeemCode", "OPENPAGE1", true)]
        [InlineData("admin.redeemCode", "ABC", false)]
        [InlineData("admin.redeemCode", "abcd", false)]
        [InlineData("admin.redeemCode", "ABCDEFGHIJKLMNOPQRSTU", false)]
        [InlineData("admin.redeemCode", "OPEN-PAGE", false)]
        [InlineData("liveops.seasonStart", "2024-01-01T00:00:00Z", true)]
        [InlineData("liveops.seasonStart", "2026-02-29T00:00:00Z", false)]
        [InlineData("liveops.seasonStart", "2024-01-01 00:00:00Z", false)]
        [InlineData("liveops.seasonStart", "2024-01-01T00:00:00+01:00", false)]
        [InlineData("gameLog.mute", "", true)]
        [InlineData("gameLog.mute", "profile:debug", true)]
        [InlineData("gameLog.mute", "profile:debug Network:all", true)]
        [InlineData("gameLog.mute", " profile:debug", true)]
        [InlineData("gameLog.mute", "profile:verbose", false)]
        [InlineData("gameLog.mute", "profile:debug  net:all", false)]
        [InlineData("gameLog.mute", "pro_file:debug", false)]
        [InlineData("gameLog.mute", ":debug", false)]
        public void StringsMatchTheirPatterns(string key, string value, bool valid)
        {
            Assert.Equal(valid, Check(key, value).ok);
        }

        [Fact]
        public void StoredValuesOfTheWrongTypeShowTheDefault()
        {
            Assert.Equal(true, SettingsSchema.Coerce(Def("instantBots"), null, out bool invalid));
            Assert.False(invalid);
            Assert.Equal(true, SettingsSchema.Coerce(Def("instantBots"), "yes", out invalid));
            Assert.True(invalid);
            Assert.Equal(2.0, SettingsSchema.Coerce(Def("economy.xpMultiplier"), 2L, out invalid));
            Assert.False(invalid);
            Assert.Equal(5L, SettingsSchema.Coerce(Def("liveops.seasonDays"), 5.0, out invalid));
            Assert.Equal(1.0, SettingsSchema.Coerce(Def("economy.xpMultiplier"), new Dictionary<string, object>(), out invalid));
            Assert.True(invalid);
            Assert.Equal("rotate", SettingsSchema.Coerce(Def("liveops.modifiers"), "nonsense", out invalid));
            Assert.True(invalid);
        }

        /// <summary>
        /// The Cosmetic crate's two keys (docs/design/economy-collector.md §4.4): a switch and a 1-10 stepper in the Economy
        /// group, read at the next CRM fetch; switching asks first (the running events restart). Both show what the
        /// collector reads (<see cref="SettingsSchema.Read"/>), flagged when settings.json holds something else: a kill
        /// switch it cannot read is off (O7), not the default; the shorthand <c>"collector": false</c> is off.
        /// </summary>
        [Fact]
        public void TheCosmeticCrateKeys()
        {
            SettingDefinition enabled = Def("economy.collector.enabled");
            Assert.Equal(("economy", "bool", "menu", "Cosmetic crate"), (enabled.Group, enabled.Type, enabled.Apply, enabled.Label));
            Assert.Contains("daily missions, weekly events and season rewards", enabled.Help);
            Assert.Contains("Off restores the original rewards", enabled.Help);
            Assert.Contains("progress is lost", enabled.Confirm);
            Assert.Equal(("any", null), (enabled.ConfirmWhen, enabled.BackupBefore));
            SettingDefinition items = Def("economy.collector.itemsPerCrate");
            Assert.Equal(("economy", "int", "menu", "Items per Cosmetic crate"), (items.Group, items.Type, items.Apply, items.Label));
            Assert.Equal((1.0, 10.0, 1.0), (items.Min.Value, items.Max.Value, items.Step.Value));
            Assert.True(Check("economy.collector.itemsPerCrate", 10L).ok);
            Assert.False(Check("economy.collector.itemsPerCrate", 11L).ok);
            Assert.False(Check("economy.collector.itemsPerCrate", 0L).ok);
            Assert.False(Check("economy.collector.itemsPerCrate", 2.5).ok);
            Assert.False(Check("economy.collector.enabled", "off").ok);

            object Shown(SettingDefinition def, object collector, out string note)
            {
                var settings = new Settings();
                settings.Override("economy.collector", collector);
                return SettingsSchema.Read(def, settings, out note);
            }
            Dictionary<string, object> Block(string key, object value) => new Dictionary<string, object> { [key] = value };
            string n;

            foreach (var (stored, shown, readable) in new (object, bool, bool)[]
                     { ("maybe", false, false), (2.5, false, false), ("off", false, true), (0L, false, true), ("yes", true, true), (1L, true, true) })
            {
                Assert.Equal(shown, Shown(enabled, Block("enabled", stored), out n));
                Assert.Equal("settings.json holds " + MiniJson.Serialize(stored)
                             + (readable ? ", which the server reads as " + (shown ? "on." : "off.") : ", which the server cannot read as on or off: the Cosmetic crate is off."), n);
                Assert.Equal(shown, CollectorOptions.ReadEnabled(stored));

                // The same values as the whole of economy.collector (the shorthand).
                Assert.Equal(shown, Shown(enabled, stored, out n));
                Assert.StartsWith("settings.json holds " + MiniJson.Serialize(stored) + " as economy.collector, which ", n);
                Assert.Equal(5L, Shown(items, stored, out n));
                Assert.Null(n);
            }
            Assert.Equal(true, Shown(enabled, null, out n));
            Assert.Null(n);
            Assert.Equal(false, Shown(enabled, Block("enabled", false), out n));
            Assert.Null(n);
            // "collector": false is the documented shorthand: off, and not flagged.
            Assert.Equal(false, Shown(enabled, false, out n));
            Assert.Null(n);
            Assert.Equal(true, Shown(enabled, true, out n));
            Assert.Null(n);
            Assert.Equal(true, Shown(enabled, Block("itemsPerCrate", 3L), out n));
            Assert.Null(n);

            // itemsPerCrate as the collector reads it: numeric strings, clamped to 1..10, the default for the rest.
            Assert.Equal(3L, Shown(items, Block("itemsPerCrate", 3L), out n));
            Assert.Null(n);
            Assert.Equal(3L, Shown(items, Block("itemsPerCrate", 3.0), out n));
            Assert.Null(n);
            Assert.Equal(8L, Shown(items, Block("itemsPerCrate", "8"), out n));
            Assert.Equal("settings.json holds \"8\"; the server uses 8.", n);
            Assert.Equal(10L, Shown(items, Block("itemsPerCrate", 15L), out n));
            Assert.Equal("settings.json holds 15; the server uses 10.", n);
            Assert.Equal(1L, Shown(items, Block("itemsPerCrate", 0L), out n));
            Assert.Equal("settings.json holds 0; the server uses 1.", n);
            Assert.Equal(5L, Shown(items, Block("itemsPerCrate", "many"), out n));
            Assert.Equal("settings.json holds \"many\"; the server uses 5.", n);
            Assert.Equal(5L, Shown(items, Block("itemsPerCrate", true), out n));
            Assert.NotNull(n);

            // The other keys: the default for a value of the wrong type, with the generic note.
            var other = new Settings();
            other.Override("instantBots", "maybe");
            Assert.Equal(true, SettingsSchema.Read(Def("instantBots"), other, out n));
            Assert.Equal(SettingsSchema.InvalidNote, n);

            // The presets keep to the currency keys.
            foreach (SettingPreset preset in SettingsSchema.Presets)
                Assert.DoesNotContain(preset.Values.Keys, k => k.StartsWith("economy.collector.", StringComparison.Ordinal));
        }

        [Fact]
        public void PresetsFillTheEconomyKeys()
        {
            var presets = SettingsSchema.Presets.ToDictionary(p => p.Id);
            Assert.Equal(new[] { "standard", "relaxed", "fast" }, SettingsSchema.Presets.Select(p => p.Id));
            Assert.Equal(2.0, presets["relaxed"].Values["economy.versusCoinsMultiplier"]);
            Assert.Equal(0.5, presets["relaxed"].Values["economy.priceMultiplier"]);
            Assert.Equal(1.0, presets["relaxed"].Values["economy.xpMultiplier"]);
            Assert.Equal(5.0, presets["fast"].Values["economy.versusCoinsMultiplier"]);
            foreach (SettingPreset preset in SettingsSchema.Presets)
            foreach (var kv in preset.Values)
                Assert.True(SettingsSchema.Validate(Def(kv.Key), kv.Value, out _, out _), preset.Id + " " + kv.Key);
        }
    }
}
