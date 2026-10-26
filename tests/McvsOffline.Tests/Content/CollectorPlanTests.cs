using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm;
using McvsOffline.Server.Services.Crm.Collector;
using McvsOffline.Server.Services.Social.LiveOps;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Content
{
    /// <summary>
    /// The collector's content file, options, weights and CRM cells (docs/design/economy-collector.md §2-§4, WP-C), on the
    /// shipped server/Content/collector.json. Where the design's numbers come from its scratch scripts (cells2.py:
    /// cells-game.json, cells-mirror.json, cells-true.json, cells-false.json) the expected cells are pinned as canonical
    /// hashes, so the comparison runs without those files.
    /// </summary>
    public class CollectorPlanTests
    {
        /// <summary>A Wednesday of the season that starts on Monday 2026-10-26 (ordinal 147).</summary>
        internal static readonly DateTime Week147 = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

        private static readonly Lazy<string> ShippedText = new Lazy<string>(() =>
        {
            Assert.True(new ContentProvider(null, null, Log.Null).TryReadContentText(CollectorContent.FileName, out string text));
            return text;
        });

        private readonly ITestOutputHelper output;

        public CollectorPlanTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        // ------------------------------------------------------------------------------------------ fixtures

        /// <summary>The shipped collector.json, parsed (a new instance per call: tests may not share mutable state).</summary>
        internal static CollectorContent Shipped()
        {
            string problem;
            CollectorContent content = CollectorContent.Parse(ShippedText.Value, out problem);
            Assert.True(content != null, problem);
            return content;
        }

        internal static string ShippedJson => ShippedText.Value;

        internal static LiveOpsSnapshot Snapshot(DateTime utc, int seasonDays = 7, double multiplier = 1.0, bool divisionSeasons = true)
        {
            var settings = new Settings();
            settings.Override("liveops.seasonDays", (long)seasonDays);
            settings.Override("liveops.seasonRewardMultiplier", multiplier);
            settings.Override("liveops.divisionSeasons", divisionSeasons);
            LiveOpsSettings s = LiveOpsSettings.From(settings, Log.Null);
            return new LiveOpsSnapshot { Settings = s, Season = LiveOpsSchedule.SeasonAt(s, utc), Day = LiveOpsSchedule.DayAt(s, utc) };
        }

        /// <summary>The design's reference profile (§4.2, §4.3): nothing but the starting items, the 19 obtainable agents.</summary>
        internal static PlayerInventory Fresh(CollectorContent content, params int[] lockedAgents)
        {
            PlayerInventory inventory = CollectorPlan.StaticInventory(content);
            foreach (int agent in lockedAgents) inventory.Mercs.Remove(agent);
            return inventory;
        }

        /// <summary>Everything the crate can give is owned (§5.1).</summary>
        internal static PlayerInventory UnlockAll(CollectorContent content)
        {
            PlayerInventory inventory = CollectorPlan.StaticInventory(content);
            foreach (int a in content.AvatarsDrawable.Concat(content.AvatarsUnlock).Concat(content.AvatarsLock).Concat(content.AvatarsBase))
                inventory.Avatars.Add(a);
            foreach (int b in content.BackgroundsDrawable.Concat(content.BackgroundsUnlock)) inventory.Backgrounds.Add(b);
            foreach (int s in content.SoundIds) inventory.Sounds.Add(s);
            foreach (int agent in content.AgentIds)
            {
                MercState merc = inventory.Mercs[agent];
                merc.Camos.UnionWith(content.CamoPools[agent]);
                merc.Accessories.UnionWith(content.JerseyPool);
            }
            foreach (CollectorRow row in content.Rows)
            {
                if (row.Category == CollectorContent.Skins) inventory.Mercs[row.Merc.Value].Skins.Add(row.Item.Value);
                if (row.Category == CollectorContent.HiddenJerseys)
                    foreach (MercState merc in inventory.Mercs.Values) merc.Accessories.Add(row.Item.Value);
            }
            return inventory;
        }

        private static PlayerInventory OwnsAllPaid(CollectorContent content, PlayerInventory inventory)
        {
            foreach (int agent in content.AgentIds)
            {
                if (!inventory.Mercs.TryGetValue(agent, out MercState merc)) continue;
                merc.Camos.UnionWith(content.CamoPools[agent]);
                merc.Accessories.UnionWith(content.JerseyPool);
            }
            return inventory;
        }

        private static Dictionary<int, long> Weights(IList<FieldPatch> patches)
        {
            FieldPatch weights = patches.Single(p => p.Changer == "Id@Weight#1");
            return weights.Cells.ToDictionary(kv => int.Parse(kv.Key), kv => (long)kv.Value);
        }

        private static FieldPatch Patch(IList<FieldPatch> patches, string table, string changer)
        {
            return patches.Single(p => p.Table == table && p.Changer == changer);
        }

        private static IEnumerable<CollectorCell> Cells(IEnumerable<CollectorCell> cells, string table, string changer)
        {
            return cells.Where(c => c.Table == table && c.Changer == changer);
        }

        private static object Value(IEnumerable<CollectorCell> cells, string table, string changer, string key)
        {
            return cells.Single(c => c.Table == table && c.Changer == changer && c.Key == key).Value;
        }

        /// <summary>"Table\tChanger\tKey\ts:value|n:value" lines, sorted ordinally: the form the oracle hashes were taken in.</summary>
        internal static string Canonical(IEnumerable<CollectorCell> cells)
        {
            var lines = new List<string>();
            foreach (CollectorCell c in cells)
                lines.Add(c.Table + "\t" + c.Changer + "\t" + c.Key + "\t" + (c.Value is string s ? "s:" + s : "n:" + ((long)c.Value).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines);
        }

        private static string Sha256(string text)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }

        // ------------------------------------------------------------------------------------------ content

        /// <summary>The generated collector.json parses and yields the 39 rows of §2.3 and the sets of §1.</summary>
        [Fact]
        public void GeneratedContentParsesToTheRowsOfTheDesign()
        {
            CollectorContent c = Shipped();
            Assert.Equal("Crate_Event_Special", c.CrateId);
            Assert.Equal("Crate_Event_Special", c.Table);
            Assert.Equal("Crate.Cosmetic_Crate", c.LocId);
            Assert.Equal("Menus/assets/png/Crates/Crates/crate_Legendary_Open.png", c.Image);

            var expected = new List<string>
            {
                "avatars KillSignature/Avatar/Random", "backgrounds KillSignature/Background/Random",
            };
            for (int s = 2; s <= 5; s++) expected.Add("sounds KillSignature/Avatar/Random+KillSignature_Sound_" + s);
            foreach (string skin in new[] { "2/5", "4/6", "6/4", "8/7", "9/3", "9/4", "10/7", "11/3", "11/4", "12/4", "15/6", "17/6", "17/11", "17/12", "19/4", "20/4" })
                expected.Add("skins MercSkin/" + skin);
            foreach (int j in new[] { 112, 143, 144, 152, 153, 212, 213, 214, 268, 269, 270, 271 }) expected.Add("hiddenJerseys MercAccessory/Random/" + j);
            expected.Add("agents UnlockMerc/17");
            expected.Add("agents UnlockMerc/21");
            expected.Add("camos WeaponCamo/Random/Random");
            expected.Add("jerseys MercAccessory/Random/Random");
            expected.Add("exhausted VersusCoins/Tiers4");
            Assert.Equal(expected, c.Rows.Select(r => r.Category + " " + r.Type + "/" + r.Subtype + (r.Subtype2 == null ? "" : "/" + r.Subtype2)
                                                      + (r.Bundle == null ? "" : "+" + r.Bundle)));
            Assert.Equal(Enumerable.Range(1, 39), c.Rows.Select(r => r.Id));

            Assert.Equal(304, c.AvatarsBase.Count);
            Assert.Equal(139, c.AvatarsUnlock.Count);
            Assert.Equal(75, c.AvatarsLock.Count);
            Assert.Equal(230, c.AvatarsDrawable.Count);        // 165 base already unlockable + 65 other drawable
            Assert.Equal(1066, c.AvatarsKayac.Count);
            Assert.Equal(36, c.AvatarsReserved.Count);
            Assert.Equal(new[] { 18, 6, 18 }, new[] { c.BackgroundsBase.Count, c.BackgroundsUnlock.Count, c.BackgroundsDrawable.Count });
            Assert.Equal(new[] { 2, 3, 4, 5 }, c.SoundIds);
            Assert.Equal(Enumerable.Range(1, 21), c.AgentIds);
            Assert.Equal(4409, c.CamoPools.Values.Sum(p => p.Count));
            Assert.Equal(236, c.JerseyPool.Count);
            Assert.Equal(7, c.Tiers.Count);
            Assert.Equal(12, c.Emblems.Count);
            Assert.Equal(new[] { 269, 270, 271, 272 }, c.Emblems[8]);          // IX is out of id order
            Assert.Equal(Enumerable.Range(2, 49), c.Levels);
        }

        private static readonly Dictionary<string, (string Text, Action<Dictionary<string, object>> Mutate, string Reason)> Broken =
            new Dictionary<string, (string, Action<Dictionary<string, object>>, string)>
        {
            ["not JSON"] = ("not json", null, "not a JSON object"),
            ["format 2"] = (null, d => d["format"] = 2L, "format 2"),
            ["no crate"] = (null, d => d.Remove("crate"), "crate"),
            ["crate id is not Crate_Event_Special"] = (null, d => JsonPath.Set(d, "crate.id", "Crate_Event_1"), "crate.id Crate_Event_1 is not Crate_Event_Special"),
            ["crate table is not Crate_Event_Special"] = (null, d => JsonPath.Set(d, "crate.table", "Crate_Event_1"), "crate.table Crate_Event_1 is not Crate_Event_Special"),
            ["sound row with weight"] = (null, d => Row(d, 3)["subtype"] = "Sound", "cannot be previewed"),
            ["sound row without carrier"] = (null, d => { Row(d, 3)["subtype"] = "Sound"; Row(d, 3)["subtype2"] = "2"; Row(d, 3).Remove("bundle"); }, "cannot be previewed"),
            ["bundle on a skin row"] = (null, d => Row(d, 7)["bundle"] = "KillSignature_Sound_2", "non-carrier"),
            ["bundle that is no sound"] = (null, d => Row(d, 1)["bundle"] = "Gear_Random_1_Random", "carrier bundle"),
            ["fixed background"] = (null, d => Row(d, 2)["subtype2"] = "3", "cannot be previewed"),
            ["empty subtype2"] = (null, d => Row(d, 7)["subtype2"] = "", "non-empty"),
            ["unknown category"] = (null, d => Row(d, 37)["category"] = "vip", "unknown category"),
            ["exhausted row not a tier"] = (null, d => Row(d, 39)["subtype"] = "Gold", "cannot be previewed"),
            ["no exhausted row"] = (null, d => ((List<object>)d["rows"]).RemoveAt(38), "exactly one exhausted row"),
            ["ids out of order"] = (null, d => Row(d, 5)["id"] = 7L, "ids must run"),
            ["Kayac in base"] = (null, d => JsonPath.Set(d, "avatars.base", "61-76,443"), "Kayac or reserved"),
            ["unlock outside base"] = (null, d => JsonPath.Set(d, "avatars.unlock", "225-253,598"), "not in avatars.base"),
            ["lock not Kayac"] = (null, d => JsonPath.Set(d, "avatars.lock", "598-672,61"), "not a Kayac avatar"),
            ["broken camo ranges"] = (null, d => JsonPath.Set(d, "camos.3", "2,x"), "camos.3"),
            ["reversed jersey range"] = (null, d => JsonPath.Set(d, "jerseys", "5-1"), "jerseys"),
            ["six tiers"] = (null, d => ((List<object>)JsonPath.Get(d, "season.tiers")).RemoveAt(6), "season.tiers"),
            ["three-emblem numeral"] = (null, d => ((List<object>)JsonPath.Get(d, "season.emblems.0")).RemoveAt(3), "4 emblems"),
            ["two-crate placement"] = (null, d => ((List<object>)JsonPath.Get(d, "season.placement.2")).RemoveAt(0), "season.placement"),
        };

        public static IEnumerable<object[]> BrokenContents() => Broken.Keys.Select(k => new object[] { k });

        private static Dictionary<string, object> Row(Dictionary<string, object> doc, int id)
        {
            return (Dictionary<string, object>)((List<object>)doc["rows"])[id - 1];
        }

        /// <summary>A broken file or a §2.3.1 violation: null and why, logged as an error.</summary>
        [Theory]
        [MemberData(nameof(BrokenContents))]
        public void ParseRefusesABrokenFileOrAPreviewHazard(string name)
        {
            var (text, mutate, reason) = Broken[name];
            if (text == null)
            {
                var doc = MiniJson.ParseObject(ShippedJson);
                mutate(doc);
                text = MiniJson.Serialize(doc);
            }
            Assert.Null(CollectorContent.Parse(text, out string problem));
            Assert.Contains(reason, problem);
            var sink = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(sink);
            Assert.Null(CollectorContent.Parse(text, logger.For("test")));
            Assert.Contains(sink.Snapshot(), e => e.Level == LogLevel.Error && e.Message.Contains(reason));
        }

        [Fact]
        public void RowProblemIsThePreviewRuleOfTheDesign()
        {
            Assert.Null(CollectorContent.RowProblem("KillSignature", "Avatar", "Random", null));
            Assert.Null(CollectorContent.RowProblem("KillSignature", "Background", "Random", null));
            Assert.Null(CollectorContent.RowProblem("KillSignature", "Avatar", "Random", "KillSignature_Sound_4"));
            Assert.Null(CollectorContent.RowProblem("MercSkin", "17", "11", null));
            Assert.Null(CollectorContent.RowProblem("MercAccessory", "Random", "112", null));
            Assert.Null(CollectorContent.RowProblem("MercAccessory", "Random", "Random", null));
            Assert.Null(CollectorContent.RowProblem("UnlockMerc", "21", null, null));
            Assert.Null(CollectorContent.RowProblem("WeaponCamo", "Random", "Random", null));
            Assert.Null(CollectorContent.RowProblem("VersusCoins", "Tiers0", null, null));
            Assert.NotNull(CollectorContent.RowProblem("KillSignature", "Sound", "2", null));        // KeyNotFoundException in GetPreview (D8)
            Assert.NotNull(CollectorContent.RowProblem("KillSignature", "Background", "7", null));   // a fixed background has no image either
            Assert.NotNull(CollectorContent.RowProblem("KillSignature", "Avatar", "Random", "KillSignature_Avatar_5"));
            Assert.NotNull(CollectorContent.RowProblem("KillSignature", "Background", "Random", "KillSignature_Sound_2"));
            Assert.NotNull(CollectorContent.RowProblem("UpgradeTokens", "Tiers3", null, null));
            Assert.NotNull(CollectorContent.RowProblem("VersusCoins", "Tiers4", "1", null));
            Assert.NotNull(CollectorContent.RowProblem("WeaponCamo", "Random", "5", null));
            Assert.NotNull(CollectorContent.RowProblem(null, null, null, null));
        }

        // ------------------------------------------------------------------------------------------ options

        private static (CollectorOptions, MemoryLogSink) Options(params (string, object)[] values)
        {
            var settings = new Settings();
            foreach (var (key, value) in values) settings.Override("economy.collector." + key, value);
            var sink = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(sink);
            return (CollectorOptions.FromSettings(settings, logger.For("test")), sink);
        }

        [Fact]
        public void OptionsDefaultToTheDesign()
        {
            var (o, sink) = Options();
            Assert.Empty(sink.Snapshot());
            Assert.True(o.Enabled);
            Assert.Equal(5, o.ItemsPerCrate);                       // O3
            Assert.Equal(0.75, o.BaseShare);
            Assert.Equal(new Dictionary<string, double>
            {
                ["avatars"] = 1, ["backgrounds"] = 1, ["sounds"] = 1, ["skins"] = 1, ["hiddenJerseys"] = 1, ["agents"] = 2, ["camos"] = 1, ["jerseys"] = 1,
            }, o.Weights);
            Assert.Equal(new[] { 1, 1, 2, 2, 3, 3, 4 }, o.SeasonCrates);
            Assert.Equal(40, o.MaxSeasonItems);
            Assert.True(o.SeasonEmblems);
            Assert.Equal(160, o.UnlockableWindow);
            Assert.Equal(0, o.LevelUpAvatars);
            Assert.Equal("VersusCoins_Tiers4", o.ExhaustedReward);
            Assert.Equal("VersusCoins", o.ExhaustedType);
            Assert.Equal("Tiers4", o.ExhaustedSubtype);
            Assert.Equal(new CollectorOptions().Fingerprint, o.Fingerprint);
        }

        [Theory]
        [InlineData("itemsPerCrate", 12L, 10)]
        [InlineData("itemsPerCrate", 0L, 1)]
        [InlineData("maxSeasonItems", 5L, 10)]
        [InlineData("maxSeasonItems", 1000L, 100)]
        [InlineData("unlockableWindow", -1L, 0)]
        [InlineData("unlockableWindow", 1000L, 400)]
        [InlineData("levelUpAvatars", 9L, 3)]
        public void OutOfRangeIntegersAreClampedAndLogged(string key, long value, int expected)
        {
            var (o, sink) = Options((key, value));
            int actual = key switch
            {
                "itemsPerCrate" => o.ItemsPerCrate,
                "maxSeasonItems" => o.MaxSeasonItems,
                "unlockableWindow" => o.UnlockableWindow,
                _ => o.LevelUpAvatars,
            };
            Assert.Equal(expected, actual);
            Assert.Contains(sink.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.StartsWith("settings: economy.collector." + key + " "));
        }

        [Fact]
        public void OtherOptionsAreClampedOrReplacedAndLogged()
        {
            var (o, sink) = Options(("baseShare", 1.5), ("weights", new Dictionary<string, object> { ["camos"] = 500L, ["avatars"] = 0.5, ["vip"] = 3L, ["sounds"] = "x" }),
                                    ("seasonCrates", new List<object> { 11L, 0L, 2L, 2L, -1L, 3L, 4L }), ("itemsPerCrate", 2.5), ("enabled", "maybe"),
                                    ("unknownKey", 1L));
            Assert.Equal(1.0, o.BaseShare);
            Assert.Equal(100.0, o.Weight("camos"));
            Assert.Equal(0.5, o.Weight("avatars"));
            Assert.Equal(1.0, o.Weight("sounds"));
            Assert.Equal(0.0, o.Weight("vip"));
            Assert.Equal(new[] { 10, 0, 2, 2, 0, 3, 4 }, o.SeasonCrates);
            Assert.Equal(5, o.ItemsPerCrate);
            Assert.True(o.Enabled);
            string log = string.Join("\n", sink.Snapshot().Select(e => e.Message));
            output.WriteLine(log);
            foreach (string key in new[] { "baseShare", "weights.camos", "weights.vip", "weights.sounds", "seasonCrates[0]", "seasonCrates[4]", "itemsPerCrate", "enabled", "unknownKey" })
                Assert.Contains("settings: economy.collector." + key, log);

            (o, sink) = Options(("seasonCrates", new List<object> { 1L, 2L }));
            Assert.Equal(new[] { 1, 1, 2, 2, 3, 3, 4 }, o.SeasonCrates);
            Assert.Contains(sink.Snapshot(), e => e.Message.StartsWith("settings: economy.collector.seasonCrates "));
        }

        /// <summary>
        /// The opt-out (O7) written the plain ways switches the collector off; a value that cannot be read keeps the §4.4
        /// default (on) and is logged as an error, not a warning.
        /// </summary>
        [Theory]
        [InlineData(false, false)]
        [InlineData(0L, false)]
        [InlineData("false", false)]
        [InlineData("0", false)]
        [InlineData("off", false)]
        [InlineData("OFF", false)]
        [InlineData(" no ", false)]
        [InlineData(true, true)]
        [InlineData(1L, true)]
        [InlineData("on", true)]
        [InlineData("Yes", true)]
        public void TheOptOutReadsThePlainSpellings(object value, bool enabled)
        {
            var (o, sink) = Options(("enabled", value));
            Assert.Equal(enabled, o.Enabled);
            Assert.DoesNotContain(sink.Snapshot(), e => e.Level >= LogLevel.Warn);

            // "collector": false instead of the object: read as enabled with the other defaults, logged.
            var settings = new Settings();
            settings.Override("economy.collector", value);
            sink = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(sink);
            o = CollectorOptions.FromSettings(settings, logger.For("test"));
            Assert.Equal(enabled, o.Enabled);
            Assert.Equal(new CollectorOptions { Enabled = enabled }.Fingerprint, o.Fingerprint);
            Assert.Contains(sink.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.StartsWith("settings: economy.collector is ")
                                                  && e.Message.Contains("economy.collector.enabled = " + (enabled ? "true" : "false")));

            (o, sink) = Options(("seasonEmblems", value));
            Assert.Equal(enabled, o.SeasonEmblems);
        }

        [Theory]
        [InlineData("maybe")]
        [InlineData("disable")]
        [InlineData(2.5)]
        public void AnUnreadableOptOutStaysOnAndIsAnError(object value)
        {
            var (o, sink) = Options(("enabled", value));
            Assert.True(o.Enabled);
            Assert.Contains(sink.Snapshot(), e => e.Level == LogLevel.Error && e.Message.StartsWith("settings: economy.collector.enabled = ")
                                                  && e.Message.Contains("the collector stays on"));

            var settings = new Settings();
            settings.Override("economy.collector", value);
            sink = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(sink);
            Assert.True(CollectorOptions.FromSettings(settings, logger.For("test")).Enabled);
            Assert.Contains(sink.Snapshot(), e => e.Level == LogLevel.Error && e.Message.StartsWith("settings: economy.collector = ")
                                                  && e.Message.Contains("the collector stays on"));

            (o, sink) = Options(("seasonEmblems", value));
            Assert.True(o.SeasonEmblems);
            Assert.Contains(sink.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.StartsWith("settings: economy.collector.seasonEmblems = "));
        }

        /// <summary>
        /// §4.4 lists UpgradeTokens tiers too, but only VersusCoins tier rows pass the preview rule (§2.3.1) and content-gen:
        /// anything else is logged and replaced by the default.
        /// </summary>
        [Theory]
        [InlineData("VersusCoins_Tiers0", true)]
        [InlineData("VersusCoins_Tiers6", true)]
        [InlineData("VersusCoins_Tiers7", false)]
        [InlineData("UpgradeTokens_Tiers3", false)]
        [InlineData("VersusCoins_Tiers", false)]
        [InlineData("HardCurrency", false)]
        public void ExhaustedRewardIsAVersusCoinsTier(string value, bool accepted)
        {
            var (o, sink) = Options(("exhaustedReward", value));
            Assert.Equal(accepted ? value : "VersusCoins_Tiers4", o.ExhaustedReward);
            Assert.Equal(!accepted, sink.Snapshot().Any(e => e.Message.StartsWith("settings: economy.collector.exhaustedReward ")));
            CollectorLayer layer = CollectorPlan.Static(Shipped(), o, Snapshot(Week147));
            Assert.Equal(o.ExhaustedSubtype, Value(layer.Game, "Crate_Event_Special", "Id@Subtype", "39"));
        }

        // ------------------------------------------------------------------------------------------ static layer

        /// <summary>§4.2: base 663 units, paid 9,365, and the weights of the table (sum exactly 1,000,000).</summary>
        [Fact]
        public void StaticWeightsAreThoseOfTheDesign()
        {
            CollectorContent content = Shipped();
            CollectorLayer layer = CollectorPlan.Static(content, new CollectorOptions(), Snapshot(Week147));
            Assert.Equal(663.0, layer.Weights.Base);
            Assert.Equal(9365.0, layer.Weights.Paid);
            Assert.Equal(368, layer.Units.Of(1));
            Assert.Equal(23, layer.Units.Of(2));
            var expected = new Dictionary<int, long> { [1] = 416290, [2] = 26018, [35] = 0, [36] = 0, [37] = 117699, [38] = 132301, [39] = 0 };
            for (int row = 3; row <= 22; row++) expected[row] = 1131;
            for (int row = 23; row <= 34; row++) expected[row] = 23756;
            foreach (CollectorRow row in content.Rows)
            {
                Assert.Equal(expected[row.Id], layer.Weights.Of(row.Id));
                Assert.Equal(expected[row.Id], Value(layer.Game, "Crate_Event_Special", "Id@Weight#1", row.Id.ToString()));
            }
            Assert.Equal(1000000L, expected.Values.Sum());
            Assert.Equal("base 663 left (avatars 368, backgrounds 23, sounds 4, skins 16, hiddenJerseys 252, agents 0), paid 9365 left (camos 4409, jerseys 4956)",
                         layer.Units.Summary(content));
        }

        /// <summary>
        /// The design's cells (cells2.py, which computed them with itemsPerCrate 3, before O3): the game layer of §2.10 (510
        /// cells: LootCrates 87, crate rows 161, placement 42, avatars 214, backgrounds 6), the mirror (365), the 139
        /// <c>true</c> and the 75 Kayac <c>false</c> avatar cells, as canonical hashes of the oracle files.
        /// </summary>
        [Fact]
        public void StaticLayerMatchesTheDesignCells()
        {
            CollectorLayer layer = CollectorPlan.Static(Shipped(), new CollectorOptions { ItemsPerCrate = 3 }, Snapshot(Week147));
            var byTable = layer.Game.GroupBy(c => c.Table == "Crate_Event_Special" || !c.Table.StartsWith("Crate_") ? c.Table : "placement")
                               .ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(new Dictionary<string, int>
            {
                ["Crate_Event_Special"] = 161, ["LootCrates"] = 87, ["placement"] = 42, ["KillsignatureAvatars"] = 214, ["KillsignatureBackgrounds"] = 6,
            }, byTable);
            Assert.Equal(510, layer.Game.Count);
            Assert.Equal(365, layer.Mirror.Count);
            Assert.Equal("872d562e5ccc6c0ce96711a315aa2ab91226e2dc0aa246b09c0b91db6c1a1aea", Sha256(Canonical(layer.Game)));
            Assert.Equal("a7d632a81d587fed041f71345cc6b96ef2edf5c27a147249d905fb79cf240f47", Sha256(Canonical(layer.Mirror)));
            var avatars = Cells(layer.Game, "KillsignatureAvatars", "ID@Unlockable").ToList();
            Assert.Equal("7469331f38b8bd1b5626eb31c064b2ef725ca28deb6f8e466aa1b21d0fc3bc88", Sha256(Canonical(avatars.Where(c => (string)c.Value == "true"))));
            Assert.Equal("c723e617f37b650e61b137abfcbe43abf76a6050d9317ed82f85d7dda0d77f53", Sha256(Canonical(avatars.Where(c => (string)c.Value == "false"))));

            // The keys of a changer are emitted in ascending numeric order.
            List<int> keys = avatars.Select(c => int.Parse(c.Key)).ToList();
            Assert.Equal(keys.OrderBy(k => k), keys);
        }

        /// <summary>
        /// With <c>MCVS_COLLECTOR_ORACLE</c> naming the folder of the design's scratch files (cells-game.json,
        /// cells-mirror.json): the same comparison cell by cell, readable on failure.
        /// </summary>
        [Fact]
        public void StaticLayerMatchesTheOracleFilesWhenPresent()
        {
            string dir = Environment.GetEnvironmentVariable("MCVS_COLLECTOR_ORACLE");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "cells-game.json")))
            {
                output.WriteLine("MCVS_COLLECTOR_ORACLE not set: skipped (StaticLayerMatchesTheDesignCells pins the files' hashes)");
                return;
            }
            CollectorLayer layer = CollectorPlan.Static(Shipped(), new CollectorOptions { ItemsPerCrate = 3 }, Snapshot(Week147));
            foreach (var (file, cells) in new[] { ("cells-game.json", layer.Game), ("cells-mirror.json", layer.Mirror) })
            {
                var oracle = (Dictionary<string, object>)MiniJson.ParseObject(File.ReadAllText(Path.Combine(dir, file)))["MCVS_Meta"];
                var expected = new List<CollectorCell>();
                foreach (var table in oracle)
                    foreach (var field in (Dictionary<string, object>)table.Value)
                        foreach (var cell in (Dictionary<string, object>)field.Value)
                        {
                            string[] changer = field.Key.Split('@');
                            expected.Add(new CollectorCell(table.Key, changer[0], changer[1].Replace('#', '.'), cell.Key, cell.Value));
                        }
                Assert.Equal(Canonical(expected).Split('\n'), Canonical(cells).Split('\n'));
            }
        }

        /// <summary>§2.10: ≤ 600 collector cells per tree, with the optional level-up avatars (49 cells) at their maximum.</summary>
        [Fact]
        public void LayersStayWithinBudget()
        {
            CollectorContent content = Shipped();
            var options = new CollectorOptions { LevelUpAvatars = 3, UnlockableWindow = 400, ItemsPerCrate = 10 };
            CollectorLayer layer = CollectorPlan.Static(content, options, Snapshot(Week147));
            output.WriteLine("game " + layer.Game.Count + ", mirror " + layer.Mirror.Count);
            Assert.Equal(510 + 49, layer.Game.Count);
            Assert.Equal(365 + 49, layer.Mirror.Count);
            Assert.True(layer.Game.Count <= 600);
            Assert.True(layer.Mirror.Count <= 600);
            Assert.Equal(49, Cells(layer.Mirror, "PlayerLevelXP", "Level@AvatarRewards").Count(c => (long)c.Value == 3));

            // The per-player game tree: the static cells with the patched changers replaced (a fresh profile is the largest).
            foreach (PlayerInventory inventory in new[] { Fresh(content, 17, 21), UnlockAll(content), null })
            {
                IList<FieldPatch> patches = CollectorPlan.PlayerCells(content, options, inventory, Snapshot(Week147));
                var patched = new HashSet<string>(patches.Select(p => p.Table + "|" + p.Changer));
                int count = layer.Game.Count(c => !patched.Contains(c.Table + "|" + c.Changer)) + patches.Sum(p => p.Cells.Count);
                Assert.True(count <= 600, count + " cells");
            }
            Assert.Equal(5, CollectorGuard.Cells.Count);
        }

        /// <summary>D10: the static mirror is the game layer minus the Unlockable <c>true</c> cells (order kept).</summary>
        [Fact]
        public void MirrorIsTheGameLayerWithoutTheTrueCells()
        {
            CollectorContent content = Shipped();
            foreach (var options in new[] { new CollectorOptions(), new CollectorOptions { UnlockableWindow = 0, LevelUpAvatars = 2 } })
            {
                CollectorLayer layer = CollectorPlan.Static(content, options, Snapshot(Week147));
                var withoutTrue = layer.Game.Where(c => !((c.Table == "KillsignatureAvatars" || c.Table == "KillsignatureBackgrounds")
                                                         && c.Changer == "ID@Unlockable" && (string)c.Value == "true")).ToList();
                Assert.Equal(withoutTrue, layer.Mirror);
                Assert.DoesNotContain(layer.Mirror, c => (c.Value as string) == "true" && c.Changer == "ID@Unlockable");
                Assert.Equal(75, Cells(layer.Mirror, "KillsignatureAvatars", "ID@Unlockable").Count());   // Kayac false cells stay (O5)
            }
        }

        /// <summary>§2.3: never "" (CRMTableCell throws), null, or a fraction (the typed readers take integers).</summary>
        [Fact]
        public void CellsAreNeverEmptyNullOrFractions()
        {
            CollectorContent content = Shipped();
            var all = new List<(string, object)>();
            foreach (var options in new[] { new CollectorOptions(), new CollectorOptions { LevelUpAvatars = 3, BaseShare = 0.333, Weights = new Dictionary<string, double> { ["avatars"] = 0.7, ["camos"] = 3.3 } } })
            {
                CollectorLayer layer = CollectorPlan.Static(content, options, Snapshot(Week147));
                all.AddRange(layer.Game.Concat(layer.Mirror).Select(c => (c.ToString(), c.Value)));
                foreach (PlayerInventory inventory in new[] { Fresh(content, 17, 21), UnlockAll(content), OwnsAllPaid(content, Fresh(content)) })
                    foreach (FieldPatch patch in CollectorPlan.PlayerCells(content, options, inventory, Snapshot(Week147)))
                        all.AddRange(patch.Cells.Select(kv => (patch.Table + "." + patch.Changer + "." + kv.Key, kv.Value)));
            }
            all.AddRange(CollectorGuard.Cells.Select(c => (c.ToString(), c.Value)));
            Assert.NotEmpty(all);
            foreach (var (where, value) in all)
            {
                Assert.True(value is long || (value is string s && s.Length > 0), where + " holds " + (value == null ? "null" : value.GetType().Name));
                Assert.False(value is string text && (text.Trim().Length == 0), where);
            }
        }

        /// <summary>
        /// §2.5: the static document names the emblems of the season that ended (2026-10-26 → III: 264/263/262 for 1st,
        /// 2nd, 3rd; 2026-11-02 → IV: 268/267/266), also on other season lengths, in all 21 placement tables.
        /// </summary>
        [Theory]
        [InlineData("2026-10-28T12:00:00Z", 7, 264, 263, 262)]
        [InlineData("2026-10-26T00:00:00Z", 7, 264, 263, 262)]
        [InlineData("2026-11-01T23:59:59Z", 7, 264, 263, 262)]
        [InlineData("2026-11-02T00:00:00Z", 7, 268, 267, 266)]
        [InlineData("2026-10-26T12:00:00Z", 1, 272, 271, 270)]   // 1-day seasons: the ended one is day 1028 → IX (269-272)
        [InlineData("2026-10-27T12:00:00Z", 1, 292, 291, 290)]   // day 1029 → X (289-292)
        [InlineData("2026-10-26T12:00:00Z", 14, 256, 255, 254)]  // 14-day seasons: 2026-10-19 starts season 73, the ended 72 → I
        public void SeasonNumeralFollowsTheEndedSeason(string at, int seasonDays, int first, int second, int third)
        {
            CollectorContent content = Shipped();
            LiveOpsSnapshot snapshot = Snapshot(DateTime.Parse(at, null, System.Globalization.DateTimeStyles.AdjustToUniversal), seasonDays);
            var ended = new LiveOpsWindow(snapshot.Season.Start.AddDays(-seasonDays), seasonDays);
            int numeral = (int)(LiveOpsSchedule.Ordinal(ended) % 12);
            Assert.Equal(numeral, CollectorPlan.Numeral(snapshot.Season, 12));
            int[] expected = { content.Emblems[numeral][3], content.Emblems[numeral][2], content.Emblems[numeral][1] };
            output.WriteLine(at + " x" + seasonDays + ": season " + snapshot.Season.Key + ", ended ordinal " + LiveOpsSchedule.Ordinal(ended) + " → " + string.Join("/", expected));
            Assert.Equal(new[] { first, second, third }, expected);

            CollectorLayer layer = CollectorPlan.Static(content, new CollectorOptions(), snapshot);
            foreach (string[] tier in content.Placement)
            {
                for (int place = 0; place < 3; place++)
                {
                    Assert.Equal("KillSignature_Avatar_" + expected[place], Value(layer.Game, tier[place], "Id@BundledRewards", "1"));
                    Assert.Equal("true", Value(layer.Game, tier[place], "Id@AlwaysGiveBundledRewards", "1"));
                }
            }
            Assert.Equal(42, layer.Game.Count(c => c.Table.EndsWith("Place") && !c.Table.Contains("To")));
        }

        [Fact]
        public void NoEmblemsWithoutSeasonEmblemsOrDivisionSeasons()
        {
            CollectorContent content = Shipped();
            Assert.DoesNotContain(CollectorPlan.Static(content, new CollectorOptions { SeasonEmblems = false }, Snapshot(Week147)).Game, c => c.Changer == "Id@BundledRewards" && c.Key == "1" && c.Table != content.Table);
            Assert.DoesNotContain(CollectorPlan.Static(content, new CollectorOptions(), Snapshot(Week147, divisionSeasons: false)).Game, c => c.Table.EndsWith("FirstPlace"));
            Assert.DoesNotContain(CollectorPlan.Static(content, new CollectorOptions(), null).Game, c => c.Table.EndsWith("FirstPlace"));
            Assert.DoesNotContain(CollectorPlan.PlayerCells(content, new CollectorOptions { SeasonEmblems = false }, Fresh(content), Snapshot(Week147)),
                                  p => p.Changer == "Id@BundledRewards");
        }

        /// <summary>§2.4: CratesCount = max(1, min(band × N, ⌊maxSeasonItems / ⌈multiplier⌉⌋)) on both crates of each tier.</summary>
        [Theory]
        [InlineData(1.0, 5, new[] { 5, 5, 10, 10, 15, 15, 20 })]
        [InlineData(2.5, 5, new[] { 5, 5, 10, 10, 13, 13, 13 })]     // q = 3: ⌊40 / 3⌋ = 13
        [InlineData(100.0, 5, new[] { 1, 1, 1, 1, 1, 1, 1 })]       // ⌊40 / 100⌋ = 0 → 1
        [InlineData(1.0, 10, new[] { 10, 10, 20, 20, 30, 30, 40 })]
        [InlineData(0.5, 1, new[] { 1, 1, 2, 2, 3, 3, 4 })]
        public void TierCratesCarryBandTimesItemsCappedByMaxSeasonItems(double multiplier, int itemsPerCrate, int[] expected)
        {
            CollectorContent content = Shipped();
            CollectorLayer layer = CollectorPlan.Static(content, new CollectorOptions { ItemsPerCrate = itemsPerCrate }, Snapshot(Week147, multiplier: multiplier));
            Assert.Equal((long)itemsPerCrate, Value(layer.Game, "LootCrates", "Id@CratesCount", "Crate_Event_Special"));
            for (int tier = 0; tier < 7; tier++)
            {
                foreach (string crate in content.Retarget[tier])
                {
                    Assert.Equal((long)expected[tier], Value(layer.Game, "LootCrates", "Id@CratesCount", crate));
                    Assert.Equal("Crate_Event_Special", Value(layer.Game, "LootCrates", "Id@ContentTableName", crate));
                    Assert.Equal(0L, Value(layer.Game, "LootCrates", "Id@RewardChance#2", crate));
                    Assert.Equal("Crate.Cosmetic_Crate", Value(layer.Game, "LootCrates", "Id@LocId", crate));
                    Assert.Equal(content.Image, Value(layer.Game, "LootCrates", "Id@Image", crate));
                    Assert.Equal(content.Image, Value(layer.Game, "LootCrates", "Id@ImageShop", crate));
                }
            }
            Assert.Equal(87, layer.Game.Count(c => c.Table == "LootCrates"));

            // A band of 0 leaves the tier's two crates alone (today's gear piece).
            layer = CollectorPlan.Static(content, new CollectorOptions { SeasonCrates = new[] { 0, 1, 2, 2, 3, 3, 0 } }, Snapshot(Week147, multiplier: multiplier));
            Assert.Equal(87 - 24, layer.Game.Count(c => c.Table == "LootCrates"));
            Assert.DoesNotContain(layer.Game, c => c.Key == "Crate_Starter_4To10Place" || c.Key == "Crate_Master_11To20Place");
        }

        // ------------------------------------------------------------------------------------------ Unlockable window

        /// <summary>§2.6: the window sends the first N unowned ids of the 139 as <c>true</c> (160 = all, 50, 0 = none); the
        /// avatar row's units follow it (uA).</summary>
        [Theory]
        [InlineData(160, 139)]
        [InlineData(50, 50)]
        [InlineData(0, 0)]
        public void UnlockableWindowSizes(int window, int trueCells)
        {
            CollectorContent content = Shipped();
            var options = new CollectorOptions { UnlockableWindow = window };
            CollectorLayer layer = CollectorPlan.Static(content, options, Snapshot(Week147));
            var cells = Cells(layer.Game, "KillsignatureAvatars", "ID@Unlockable").ToList();
            Assert.Equal(content.AvatarsUnlock.Take(trueCells).Select(a => a.ToString()), cells.Where(c => (string)c.Value == "true").Select(c => c.Key));
            Assert.Equal(75, cells.Count(c => (string)c.Value == "false"));
            Assert.Equal(229 + trueCells, layer.Units.Of(1));

            // Per player: owned ids leave the window, which moves on to the next unowned ones.
            PlayerInventory inventory = Fresh(content);
            foreach (int a in content.AvatarsUnlock.Take(10)) inventory.Avatars.Add(a);
            inventory.Avatars.Add(600);                                          // an owned Kayac avatar needs no false cell
            FieldPatch patch = Patch(CollectorPlan.PlayerCells(content, options, inventory, Snapshot(Week147)), "KillsignatureAvatars", "ID@Unlockable");
            var expectedTrue = content.AvatarsUnlock.Skip(10).Take(trueCells).Select(a => a.ToString()).ToList();
            Assert.Equal(expectedTrue, patch.Cells.Where(kv => (string)kv.Value == "true").Select(kv => kv.Key));
            Assert.Equal(74, patch.Cells.Count(kv => (string)kv.Value == "false"));
            Assert.False(patch.Cells.ContainsKey("600"));
            List<int> keys = patch.Cells.Keys.Select(int.Parse).ToList();
            Assert.Equal(keys.OrderBy(k => k), keys);
        }

        // ------------------------------------------------------------------------------------------ per-player weights

        /// <summary>§5.1: everything owned → only row 39 has weight (1) and no Unlockable cell is sent.</summary>
        [Fact]
        public void UnlockAllInventoryLeavesOnlyTheExhaustedRow()
        {
            CollectorContent content = Shipped();
            IList<FieldPatch> patches = CollectorPlan.PlayerCells(content, new CollectorOptions(), UnlockAll(content), Snapshot(Week147),
                                                                  out CollectorUnits units, out CollectorWeights weights);
            Dictionary<int, long> w = Weights(patches);
            Assert.Equal(39, w.Count);
            Assert.Equal(1L, w[39]);
            Assert.All(w.Where(kv => kv.Key != 39), kv => Assert.Equal(0L, kv.Value));
            Assert.Empty(Patch(patches, "KillsignatureAvatars", "ID@Unlockable").Cells);
            Assert.Empty(Patch(patches, "KillsignatureBackgrounds", "ID@Unlockable").Cells);
            Assert.Equal(21, patches.Count(p => p.Changer == "Id@BundledRewards"));                  // 39 weights + 21 emblem cells (§5.1)
            Assert.Equal(60, patches.Sum(p => p.Cells.Count));
            Assert.Equal("base 0 left (avatars 0, backgrounds 0, sounds 0, skins 0, hiddenJerseys 0, agents 0), paid 0 left (camos 0, jerseys 0)",
                         units.Summary(content));
        }

        /// <summary>§4.1/§5.2: base empty → the paid tier gets 100 %; paid empty → the base tier gets 100 %.</summary>
        [Fact]
        public void AnEmptyTierGivesTheOtherEveryDraw()
        {
            CollectorContent content = Shipped();
            var options = new CollectorOptions();

            // Base empty: every no-source item owned, the VIP pools untouched.
            PlayerInventory baseOwned = UnlockAll(content);
            foreach (MercState merc in baseOwned.Mercs.Values)
            {
                merc.Camos.Clear();
                merc.Camos.Add(1);
                merc.Accessories.RemoveWhere(j => content.JerseyPool.Contains(j));
            }
            Dictionary<int, long> w = Weights(CollectorPlan.PlayerCells(content, options, baseOwned, Snapshot(Week147)));
            Assert.All(content.Rows.Where(r => CollectorContent.IsBaseCategory(r.Category)), r => Assert.Equal(0L, w[r.Id]));
            Assert.Equal(470796L, w[37]);           // 1,000,000 × 4,409 / 9,365
            Assert.Equal(529204L, w[38]);
            Assert.Equal(0L, w[39]);

            // Paid empty: every camo and visible jersey owned, nothing else.
            w = Weights(CollectorPlan.PlayerCells(content, options, OwnsAllPaid(content, Fresh(content)), Snapshot(Week147)));
            Assert.Equal(0L, w[37]);
            Assert.Equal(0L, w[38]);
            Assert.Equal(0L, w[39]);
            long baseTotal = content.Rows.Where(r => CollectorContent.IsBaseCategory(r.Category)).Sum(r => w[r.Id]);
            Assert.InRange(baseTotal, 999990L, 1000010L);   // rounding of each row
            Assert.Equal(555053L, w[1]);                     // 1,000,000 × 368 / 663

            // The same through the formula alone.
            var units = new CollectorUnits();
            foreach (CollectorRow row in content.Rows) units.Rows[row.Id] = row.Category == CollectorContent.Camos ? 10 : 0;
            CollectorWeights only = CollectorWeights.Compute(content, options, units);
            Assert.Equal(0.0, only.BaseShare);
            Assert.Equal(1000000L, only.Of(37));
            Assert.Equal(0L, only.Of(39));
        }

        /// <summary>§4.1: a row with something left never rounds to 0, and the category weights scale the units.</summary>
        [Fact]
        public void ARowWithUnitsKeepsAtLeastWeightOne()
        {
            CollectorContent content = Shipped();
            var units = new CollectorUnits();
            foreach (CollectorRow row in content.Rows) units.Rows[row.Id] = 0;
            units.Rows[1] = 10000000;
            units.Rows[3] = 1;
            units.Rows[37] = 5;
            CollectorWeights w = CollectorWeights.Compute(content, new CollectorOptions(), units);
            Assert.Equal(750000L, w.Of(1));
            Assert.Equal(1L, w.Of(3));                 // 0.075 rounds to 0: at least 1
            Assert.Equal(250000L, w.Of(37));
            Assert.Equal(0L, w.Of(4));

            var options = new CollectorOptions();
            options.Weights["sounds"] = 3;
            options.Weights["avatars"] = 0;            // 0 removes the category
            units.Rows[1] = 4;
            w = CollectorWeights.Compute(content, options, units);
            Assert.Equal(3.0, w.Base);
            Assert.Equal(0L, w.Of(1));
            Assert.Equal(750000L, w.Of(3));
        }

        /// <summary>Owned skins, sounds and agents weigh 0; their categories' other rows keep theirs.</summary>
        [Fact]
        public void OwnedSkinsSoundsAndAgentsWeighZero()
        {
            CollectorContent content = Shipped();
            PlayerInventory inventory = Fresh(content, 21);
            inventory.Mercs[2].Skins.Add(5);       // row 7
            inventory.Mercs[17].Skins.Add(11);     // row 19
            inventory.Sounds.Add(3);               // row 4
            Dictionary<int, long> w = Weights(CollectorPlan.PlayerCells(content, new CollectorOptions(), inventory, Snapshot(Week147)));
            Assert.Equal(0L, w[7]);
            Assert.Equal(0L, w[19]);
            Assert.Equal(0L, w[4]);
            Assert.Equal(0L, w[35]);               // Witch (17) owned
            Assert.True(w[36] > 0);                // Bli-P (21) locked: weights.agents units
            Assert.True(w[3] > 0 && w[8] > 0 && w[18] > 0);
            Assert.InRange(w[36], 2 * w[3] - 1, 2 * w[3] + 1);   // 2 units against 1 (rounding aside)
        }

        /// <summary>A locked agent: its skins weigh 0, it lacks no hidden jersey and its camo/jersey pairs are not counted.</summary>
        [Fact]
        public void LockedAgentZerosItsSkinsAndItsPairs()
        {
            CollectorContent content = Shipped();
            IList<FieldPatch> patches = CollectorPlan.PlayerCells(content, new CollectorOptions(), Fresh(content, 17), Snapshot(Week147),
                                                                  out CollectorUnits units, out CollectorWeights weights);
            Dictionary<int, long> w = Weights(patches);
            foreach (int row in new[] { 18, 19, 20 }) Assert.Equal(0L, w[row]);   // the skins of agent 17
            Assert.Equal(1, units.Of(35));
            for (int row = 23; row <= 34; row++) Assert.Equal(20, units.Of(row));
            Assert.Equal(4409 - content.CamoPools[17].Count, units.Of(37));
            Assert.Equal(4956 - 236, units.Of(38));
            Assert.True(w[35] > 0);
        }

        /// <summary>§4.2's per-player example: the 19 obtainable agents, Witch and Bli-P locked → base 640 units.</summary>
        [Fact]
        public void PerPlayerExampleOfTheDesign()
        {
            CollectorContent content = Shipped();
            CollectorPlan.PlayerCells(content, new CollectorOptions(), Fresh(content, 17, 21), Snapshot(Week147), out CollectorUnits units, out CollectorWeights weights);
            Assert.Equal(640.0, weights.Base);                          // 368 + 23 + 4 + 13 + 12 × 19 + 2 × 2
            Assert.Equal(13, units.Category(content, CollectorContent.Skins));
            Assert.Equal(228, units.Category(content, CollectorContent.HiddenJerseys));
            Assert.Equal(2, units.Category(content, CollectorContent.Agents));
            Assert.Equal(0.75, weights.BaseShare);
        }

        public static IEnumerable<object[]> Parts() => new[] { "avatars", "backgrounds", "sounds", "mercs" }.Select(p => new object[] { p });

        /// <summary>§2.9.1: an unknown inventory part takes the static assumptions for that part only.</summary>
        [Theory]
        [MemberData(nameof(Parts))]
        public void UnknownPartFallsBackToStaticForThatPartOnly(string part)
        {
            CollectorContent content = Shipped();
            var options = new CollectorOptions { UnlockableWindow = 50 };
            LiveOpsSnapshot snapshot = Snapshot(Week147);
            Func<PlayerInventory> player = () =>
            {
                PlayerInventory p = Fresh(content, 17);
                p.Avatars.UnionWith(content.AvatarsUnlock.Take(5));
                p.Avatars.Add(264);                                   // this season's ★★★ (III)
                p.Avatars.Add(598);
                p.Avatars.Add(content.AvatarsDrawable[10]);
                p.Backgrounds.Add(18);
                p.Backgrounds.Add(24);
                p.Sounds.Add(2);
                return p;
            };
            IList<FieldPatch> known = CollectorPlan.PlayerCells(content, options, player(), snapshot, out CollectorUnits knownUnits, out _);
            IList<FieldPatch> statics = CollectorPlan.PlayerCells(content, options, null, snapshot, out CollectorUnits staticUnits, out _);
            PlayerInventory partial = player();
            switch (part)
            {
                case "avatars": partial.AvatarsKnown = false; partial.Avatars.Clear(); break;
                case "backgrounds": partial.BackgroundsKnown = false; partial.Backgrounds.Clear(); break;
                case "sounds": partial.SoundsKnown = false; partial.Sounds.Clear(); break;
                default: partial.MercsKnown = false; partial.Mercs.Clear(); break;
            }
            IList<FieldPatch> mixed = CollectorPlan.PlayerCells(content, options, partial, snapshot, out CollectorUnits mixedUnits, out _);

            string Json(IList<FieldPatch> patches, string table, string changer) => MiniJson.Serialize(Patch(patches, table, changer).Cells);
            bool avatarsStatic = part == "avatars";
            Assert.Equal(Json(avatarsStatic ? statics : known, "KillsignatureAvatars", "ID@Unlockable"), Json(mixed, "KillsignatureAvatars", "ID@Unlockable"));
            Assert.Equal(Json(avatarsStatic ? statics : known, "Crate_Gold_FirstPlace", "Id@BundledRewards"), Json(mixed, "Crate_Gold_FirstPlace", "Id@BundledRewards"));
            Assert.Equal((avatarsStatic ? staticUnits : knownUnits).Of(1), mixedUnits.Of(1));
            Assert.Equal(Json(part == "backgrounds" ? statics : known, "KillsignatureBackgrounds", "ID@Unlockable"), Json(mixed, "KillsignatureBackgrounds", "ID@Unlockable"));
            Assert.Equal((part == "backgrounds" ? staticUnits : knownUnits).Of(2), mixedUnits.Of(2));
            for (int row = 3; row <= 6; row++) Assert.Equal((part == "sounds" ? staticUnits : knownUnits).Of(row), mixedUnits.Of(row));
            for (int row = 7; row <= 38; row++) Assert.Equal((part == "mercs" ? staticUnits : knownUnits).Of(row), mixedUnits.Of(row));

            // The parts really differ from the static assumptions, so each assertion above decides something.
            Assert.NotEqual(knownUnits.Of(1), staticUnits.Of(1));
            Assert.NotEqual(knownUnits.Of(2), staticUnits.Of(2));
            Assert.NotEqual(knownUnits.Of(3), staticUnits.Of(3));
            Assert.NotEqual(knownUnits.Of(35), staticUnits.Of(35));
            Assert.Equal("KillSignature_Avatar_268", Patch(known, "Crate_Gold_FirstPlace", "Id@BundledRewards").Cells["1"]);
        }

        /// <summary>
        /// D4: per star count, the first emblem the player lacks from the season's numeral on (cycling I..XII); all 12
        /// owned → the season's numeral.
        /// </summary>
        [Fact]
        public void PlayerEmblemIsTheNextUnownedOfTheSameStars()
        {
            CollectorContent content = Shipped();
            LiveOpsSnapshot snapshot = Snapshot(Week147);                   // ended season: III (261-264)
            string[] Emblems(PlayerInventory p)
            {
                IList<FieldPatch> patches = CollectorPlan.PlayerCells(content, new CollectorOptions(), p, snapshot);
                return content.Placement.SelectMany(t => t).Select(table => (string)Patch(patches, table, "Id@BundledRewards").Cells["1"])
                              .Distinct().ToArray();
            }
            string[] Expected(int first, int second, int third) => new[] { first, second, third }.Select(i => "KillSignature_Avatar_" + i).ToArray();

            PlayerInventory inventory = Fresh(content);
            Assert.Equal(Expected(264, 263, 262), Emblems(inventory));
            inventory.Avatars.Add(264);                                     // III ★★★ owned → IV ★★★
            Assert.Equal(Expected(268, 263, 262), Emblems(inventory));
            inventory.Avatars.Add(268);                                     // and IV ★★★ → V ★★★ (273-276)
            inventory.Avatars.Add(262);                                     // III ★ → IV ★
            Assert.Equal(Expected(276, 263, 266), Emblems(inventory));
            foreach (int[] numeral in content.Emblems) inventory.Avatars.Add(numeral[3]);
            Assert.Equal(Expected(264, 263, 266), Emblems(inventory));      // all 12 ★★★ owned → the season's numeral

            // Cycling past XII back to I.
            inventory = Fresh(content);
            for (int n = 2; n < 12; n++) inventory.Avatars.Add(content.Emblems[n][2]);
            Assert.Equal(Expected(264, 255, 262), Emblems(inventory));      // ★★ of III..XII owned → I ★★ (255)
        }

        /// <summary>The same inputs give the same cells, whatever order the inventory sets were filled in.</summary>
        [Fact]
        public void WeightsAreDeterministic()
        {
            CollectorContent content = Shipped();
            PlayerInventory a = Fresh(content, 17), b = Fresh(content, 17);
            var owned = content.AvatarsUnlock.Take(40).Concat(content.AvatarsDrawable.Take(30)).ToList();
            foreach (int id in owned) a.Avatars.Add(id);
            owned.Reverse();
            foreach (int id in owned) b.Avatars.Add(id);
            a.Mercs[3].Camos.UnionWith(new[] { 5, 6, 7 });
            b.Mercs[3].Camos.UnionWith(new[] { 7, 6, 5 });
            string Text(PlayerInventory p) => string.Join("\n", CollectorPlan.PlayerCells(content, new CollectorOptions(), p, Snapshot(Week147))
                                                                             .Select(x => x.Table + "." + x.Changer + MiniJson.Serialize(x.Cells)));
            Assert.Equal(Text(a), Text(b));
            Assert.Equal(Text(a), Text(a));
            Assert.Equal(Canonical(CollectorPlan.Static(content, new CollectorOptions(), Snapshot(Week147)).Game),
                         Canonical(CollectorPlan.Static(Shipped(), new CollectorOptions(), Snapshot(Week147)).Game));
        }

        /// <summary>§2.3.1 on every tree: each row with weight is one the client can preview.</summary>
        [Fact]
        public void EveryWeightedRowPassesThePreviewRule()
        {
            CollectorContent content = Shipped();
            var optionSets = new[]
            {
                new CollectorOptions(),
                new CollectorOptions { ExhaustedReward = "VersusCoins_Tiers0", Weights = new Dictionary<string, double> { ["sounds"] = 1 } },
            };
            foreach (CollectorOptions options in optionSets)
            {
                CollectorLayer layer = CollectorPlan.Static(content, options, Snapshot(Week147));
                var trees = new List<List<CollectorCell>> { layer.Game.ToList(), layer.Mirror.ToList(), CollectorGuard.Cells.ToList() };
                foreach (PlayerInventory inventory in new[] { Fresh(content, 17, 21), UnlockAll(content), OwnsAllPaid(content, Fresh(content)) })
                {
                    // The per-player tree: the layer with the player's weights.
                    Dictionary<int, long> w = Weights(CollectorPlan.PlayerCells(content, options, inventory, Snapshot(Week147)));
                    trees.Add(layer.Game.Select(c => c.Changer == "Id@Weight#1" && c.Table == content.Table
                                                         ? new CollectorCell(c.Table, c.Index, c.Field, c.Key, w[int.Parse(c.Key)]) : c).ToList());
                }
                foreach (List<CollectorCell> tree in trees)
                {
                    var rows = tree.Where(c => c.Table == "Crate_Event_Special").GroupBy(c => c.Key);
                    int weighted = 0;
                    foreach (var row in rows)
                    {
                        var cells = row.ToDictionary(c => c.Changer, c => c.Value);
                        if (!cells.TryGetValue("Id@Weight#1", out object weight) || (long)weight <= 0) continue;
                        weighted++;
                        string problem = CollectorContent.RowProblem(cells["Id@Type"] as string, cells["Id@Subtype"] as string,
                                                                     cells.GetValueOrDefault("Id@Subtype2") as string,
                                                                     cells.GetValueOrDefault("Id@BundledRewards") as string);
                        Assert.True(problem == null, "row " + row.Key + ": " + problem);
                    }
                    Assert.True(weighted > 0, "a tree without a weighted row would throw on the TS (§0.3.6)");
                }
            }
        }

        // ------------------------------------------------------------------------------------------ guard

        [Fact]
        public void GuardIsTheFiveCellsOfTheDesign()
        {
            Assert.Equal(new[]
            {
                "Crate_Event_Special.Id@Type.1 = VersusCoins",
                "Crate_Event_Special.Id@Subtype.1 = Tiers4",
                "Crate_Event_Special.Id@Weight#1.1 = 1",
                "LootCrates.Id@LocId.Crate_Event_Special = Crate.Cosmetic_Crate",
                "LootCrates.Id@RewardChance#2.Crate_Event_Special = 0",
            }, CollectorGuard.Cells.Select(c => c.ToString()));
            // The bench's guard table (cells-guard.json): the 3 content-table cells.
            Assert.Equal("9faf4b612c5b585eda8020a3090210845709c216683338c23eb2c9bfd2cfd7ea", Sha256(Canonical(CollectorGuard.Cells.Take(3))));
            Assert.Equal(new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc), CollectorGuard.ShippedSince);
            Assert.Equal(DateTimeKind.Utc, CollectorGuard.ShippedSince.Kind);
            Assert.Equal(DayOfWeek.Monday, CollectorGuard.ShippedSince.DayOfWeek);
            Assert.NotSame(CollectorGuard.Cells, CollectorGuard.Cells);
        }
    }
}
