using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm;
using McvsOffline.Server.Services.Crm.Collector;
using McvsOffline.Server.Services.Social.LiveOps;
using Xunit;

namespace McvsOffline.Tests.Content
{
    /// <summary>CRM document assembly (CrmDocumentBuilder) and the client-shape checks (CrmDocumentValidator).</summary>
    public class CrmDocumentBuilderTests
    {
        private static Dictionary<string, object> Pre() => MiniJson.ParseObject(ContentTestSupport.SamplePreconfiguration());

        private static CrmContent Content(string storeJson = null, string economyJson = null)
        {
            var c = new CrmContent();
            c.OfflineStore = MiniJson.ParseObject(storeJson ?? @"{""prices"":[
              {""item"":""UnlockMerc_1"",""quantity"":1,""category"":[],""hidden"":false,""billing_methods"":[{""name"":""offline"",""type"":""offline"",""price"":[{""currency"":""VersusCoins"",""price"":1500},{""currency"":""HardCurrency"",""price"":150}],""replaced_price"":[]}]},
              {""item"":""WeaponCamo_1_Random"",""quantity"":1,""category"":[""#VersusCoins""],""hidden"":false,""billing_methods"":[{""name"":""offline"",""type"":""offline"",""price"":[{""currency"":""VersusCoins"",""price"":75}],""replaced_price"":[]}]}
            ]}");
            c.Overlay = MiniJson.ParseObject(@"{""tags"":[],""game"":{""13yolimitationpopup"":{""enabled"":false},""AgeGateParams"":{""Ignore"":true},
              ""CrashlyticsParams"":{""Enable"":false},""_OverrideTables"":{""MCVS_System"":{""GameFeatures"":{""Id@Enabled"":{""RedeemCoupons"":""1""}}}},
              ""_ServerConfig"":{""default"":{""IsDevelopment"":false,""UnsupportedLinkFrom"":[],""UnsupportedLinkTo"":[]}}},""iap"":{""prices"":[]}}");
            c.Economy = MiniJson.ParseObject(economyJson ?? @"{""multipliers"":{
              ""credits"":[{""database"":""MCVS_Meta"",""table"":""CreditsCurrencyMatchRewards"",""index"":""Division"",""ids"":[1,2],""fields"":[""VictoryAmount""],""capFields"":[""DailyCap""]}],
              ""versusCoins"":[{""database"":""MCVS_Meta"",""table"":""VersusCoinsMatchRewards"",""index"":""Division"",""ids"":[1,2],""fields"":[""VictoryAmount"",""DefeatAmount""],""capFields"":[""DailyCap""]}],
              ""xp"":[{""database"":""MCVS_Meta"",""table"":""PlayerLevelXP"",""index"":""Level"",""ids"":[1,2,3],""fields"":[""XPRequired""],""inverse"":true}]}}");
            return c;
        }

        private static object At(object root, string path) => JsonPath.Get(root, path);

        [Fact]
        public void DefaultDocumentIsCompleteAndValid()
        {
            var doc = CrmDocumentBuilder.Build(null, Content(), new CrmOptions(), Log.Null);
            Assert.Empty(CrmDocumentValidator.Validate(doc));
            Assert.IsType<Dictionary<string, object>>(doc["game"]);
            Assert.Equal(2, ((List<object>)At(doc, "offline_store.prices")).Count);
            Assert.Empty((List<object>)At(doc, "iap.prices"));
            Assert.Empty((List<object>)doc["pointcut_actions"]);

            // verify-C5 (a): the cheat menu is on by default over loopback, so IsDevelopment must be an explicit false.
            Assert.Equal(false, At(doc, "game._ServerConfig.default.IsDevelopment"));
            // verify-C4 §3.4: instant bot-filled PvP.
            Assert.Equal(1L, At(doc, "game._ServerConfig.default.Matchmaking.MinPlayers"));
            Assert.Equal(0L, At(doc, "game._ServerConfig.default.Matchmaking.MaxOpenTimeInSeconds"));
            Assert.Equal(true, At(doc, "game.AgeGateParams.Ignore"));
            Assert.Equal(false, At(doc, "game.CrashlyticsParams.Enable"));
            // RedeemCoupons in both places (TS reads game._OverrideTables, GS _ServerConfig.default._OverrideTables).
            Assert.Equal("1", At(doc, "game._OverrideTables.MCVS_System.GameFeatures.Id@Enabled.RedeemCoupons"));
            Assert.Equal("1", At(doc, "game._ServerConfig.default._OverrideTables.MCVS_System.GameFeatures.Id@Enabled.RedeemCoupons"));
            Assert.False(doc.ContainsKey("groupware"));
        }

        [Fact]
        public void SettingsDriveDevelopmentMatchmakingAgeGateAndCoupons()
        {
            var o = new CrmOptions { DevMenu = true, InstantBots = false, SkipAgeGate = false, RedeemCoupons = false,
                                     DeveloperIpRange = "0.0.0.0/0 ::/0" };
            var doc = CrmDocumentBuilder.Build(null, Content(), o, Log.Null);
            Assert.Equal(true, At(doc, "game._ServerConfig.default.IsDevelopment"));
            Assert.Equal("0.0.0.0/0 ::/0", At(doc, "game._ServerConfig.default.AdditionalDeveloperIpRange"));
            Assert.Null(At(doc, "game._ServerConfig.default.Matchmaking"));
            Assert.Equal(false, At(doc, "game.AgeGateParams.Ignore"));
            Assert.Null(At(doc, "game._OverrideTables.MCVS_System"));
            Assert.Null(At(doc, "game._ServerConfig.default._OverrideTables.MCVS_System"));
            Assert.Empty(CrmDocumentValidator.Validate(doc));

            var mm = new CrmOptions { MatchmakingOverrides = MiniJson.ParseObject(@"{""TeamReviewDurationInMilliseconds"":1000}") };
            doc = CrmDocumentBuilder.Build(null, Content(), mm, Log.Null);
            Assert.Equal(1L, At(doc, "game._ServerConfig.default.Matchmaking.MinPlayers"));
            Assert.Equal(1000L, At(doc, "game._ServerConfig.default.Matchmaking.TeamReviewDurationInMilliseconds"));
        }

        [Fact]
        public void PreconfigurationIsOverlaidNotReplaced()
        {
            var doc = CrmDocumentBuilder.Build(Pre(), Content(), new CrmOptions(), Log.Null);
            Assert.Empty(CrmDocumentValidator.Validate(doc));

            // Ad pointcuts dropped, the rest kept.
            var actions = (List<object>)doc["pointcut_actions"];
            Assert.Single(actions);
            Assert.Equal("keep_me", At(actions[0], "id"));

            // Preconfiguration game keys survive unless overlaid; empty changers are pruned, real ones kept.
            Assert.Equal(true, At(doc, "game.AgeGateParams.Ignore"));
            Assert.Equal(false, At(doc, "game.CrashlyticsParams.Enable"));
            Assert.Equal("WORLD_GAME_Default", ((List<object>)At(doc, "game.tags"))[0]);
            Assert.Null(At(doc, "game._OverrideTables.MCVS_Gameplay"));
            Assert.Equal("7", At(doc, "game._OverrideTables.MCVS_Meta.MercsList.Id@TrialDays.3"));
            Assert.Equal("7", At(doc, "game._ServerConfig.default._OverrideTables.MCVS_Meta.MercsList.Id@TrialDays.3"));

            // Our catalogue replaces the shipped one (default mode); the language-indexed iap becomes {"prices":[]}.
            var prices = (List<object>)At(doc, "offline_store.prices");
            Assert.Equal(2, prices.Count);
            Assert.Equal("VersusCoins", At(prices[0], "billing_methods.0.price.0.currency"));
            Assert.Single((Dictionary<string, object>)doc["iap"]);
            Assert.Empty((List<object>)At(doc, "iap.prices"));
        }

        [Fact]
        public void StoreModesMergeOrKeepTheShippedCatalogue()
        {
            var merge = CrmDocumentBuilder.Build(Pre(), Content(), new CrmOptions { OfflineStore = OfflineStoreMode.Merge }, Log.Null);
            var names = new List<string>();
            foreach (object p in (List<object>)At(merge, "offline_store.prices")) names.Add(CrmDocumentBuilder.ItemName(p));
            Assert.Equal(new[] { "UnlockMerc_1", "WeaponCamo_1_Random", "SoftCurrency_Pack1" }, names);
            Assert.Equal("VersusCoins", At(merge, "offline_store.prices.0.billing_methods.0.price.0.currency")); // ours wins

            var shipped = CrmDocumentBuilder.Build(Pre(), Content(), new CrmOptions { OfflineStore = OfflineStoreMode.Preconfig }, Log.Null);
            Assert.Equal("AgentsCurrency", At(shipped, "offline_store.prices.0.billing_methods.0.price.0.currency"));
            Assert.Empty(CrmDocumentValidator.Validate(shipped)); // replaced_price null is accepted (JValue enumerates empty)

            // Without a preconfiguration, "preconfig" falls back to ours.
            var none = CrmDocumentBuilder.Build(null, Content(), new CrmOptions { OfflineStore = OfflineStoreMode.Preconfig }, Log.Null);
            Assert.Equal(2, ((List<object>)At(none, "offline_store.prices")).Count);
        }

        [Fact]
        public void IapPreconfigModePicksOneLanguage()
        {
            var doc = CrmDocumentBuilder.Build(Pre(), Content(), new CrmOptions { Iap = IapMode.Preconfig, IapLanguage = "de" }, Log.Null);
            Assert.Equal("Pack_A", At(doc, "iap.prices.0._id")); // unknown language -> "en"
            doc = CrmDocumentBuilder.Build(Pre(), Content(), new CrmOptions { Iap = IapMode.Preconfig, IapLanguage = "fr" }, Log.Null);
            Assert.Empty((List<object>)At(doc, "iap.prices"));
            Assert.Empty(CrmDocumentValidator.Validate(doc));
        }

        [Fact]
        public void PriceMultiplierScalesEveryPriceAndKeepsIntegers()
        {
            var doc = CrmDocumentBuilder.Build(null, Content(), new CrmOptions { PriceMultiplier = 0.333 }, Log.Null);
            Assert.Equal(500L, At(doc, "offline_store.prices.0.billing_methods.0.price.0.price"));
            Assert.Equal(50L, At(doc, "offline_store.prices.0.billing_methods.0.price.1.price"));
            Assert.Equal(25L, At(doc, "offline_store.prices.1.billing_methods.0.price.0.price"));

            doc = CrmDocumentBuilder.Build(null, Content(), new CrmOptions { PriceMultiplier = 0.001 }, Log.Null);
            Assert.Equal(1L, At(doc, "offline_store.prices.1.billing_methods.0.price.0.price")); // never free
        }

        [Fact]
        public void EconomyMultipliersBecomeOverrideTableCells()
        {
            var o = new CrmOptions { CreditsMultiplier = 2, VersusCoinsMultiplier = 1.5, XpMultiplier = 2, DailyCapMultiplier = 2 };
            var doc = CrmDocumentBuilder.Build(null, Content(), o, Log.Null);
            Assert.Equal("*2", At(doc, "game._OverrideTables.MCVS_Meta.CreditsCurrencyMatchRewards.Division@VictoryAmount.1"));
            Assert.Equal("*4", At(doc, "game._OverrideTables.MCVS_Meta.CreditsCurrencyMatchRewards.Division@DailyCap.2"));
            Assert.Equal("*1.5", At(doc, "game._OverrideTables.MCVS_Meta.VersusCoinsMatchRewards.Division@DefeatAmount.2"));
            Assert.Equal("*3", At(doc, "game._OverrideTables.MCVS_Meta.VersusCoinsMatchRewards.Division@DailyCap.1"));
            Assert.Equal("*0.5", At(doc, "game._OverrideTables.MCVS_Meta.PlayerLevelXP.Level@XPRequired.3"));
            // Mirrored for the GS, which computes match rewards.
            Assert.Equal("*2", At(doc, "game._ServerConfig.default._OverrideTables.MCVS_Meta.CreditsCurrencyMatchRewards.Division@VictoryAmount.1"));
            Assert.Empty(CrmDocumentValidator.Validate(doc));

            // Neutral multipliers add nothing.
            doc = CrmDocumentBuilder.Build(null, Content(), new CrmOptions(), Log.Null);
            Assert.Null(At(doc, "game._OverrideTables.MCVS_Meta"));

            // Only the cap multiplier: only the cap columns change.
            doc = CrmDocumentBuilder.Build(null, Content(), new CrmOptions { DailyCapMultiplier = 3 }, Log.Null);
            Assert.Null(At(doc, "game._OverrideTables.MCVS_Meta.CreditsCurrencyMatchRewards.Division@VictoryAmount"));
            Assert.Equal("*3", At(doc, "game._OverrideTables.MCVS_Meta.CreditsCurrencyMatchRewards.Division@DailyCap.1"));
        }

        [Fact]
        public void InvalidStoreItemsAreDroppedAndNullPromotionRemoved()
        {
            string store = @"{""promotion"":null,""prices"":[
              {""item"":""ResetProfile"",""quantity"":1,""billing_methods"":[{""price"":[{""currency"":""VersusCoins"",""price"":1}],""replaced_price"":[]}]},
              {""item"":""NotAType_1"",""quantity"":1,""billing_methods"":[{""price"":[{""currency"":""VersusCoins"",""price"":1}],""replaced_price"":[]}]},
              {""item"":""MercSkin_1_2"",""quantity"":1,""billing_methods"":[]},
              {""item"":""MercSkin_1_3"",""quantity"":1,""promotion"":null,""billing_methods"":[{""price"":[{""currency"":""VersusCoins"",""price"":1}],""replaced_price"":[]}]},
              {""group"":""MCVSPack_X"",""bundle"":[{""item"":""WeaponCamo_1_Random"",""quantity"":1}],""billing_methods"":[{""price"":[{""currency"":""VersusCoins"",""price"":1}],""replaced_price"":[]}]},
              {""item"":""KillSignature_Avatar_2"",""quantity"":1,""billing_methods"":[{""price"":[{""currency"":""VersusCoins"",""price"":100}],""replaced_price"":[]}]}
            ]}";
            var doc = CrmDocumentBuilder.Build(null, Content(store), new CrmOptions(), Log.Null);
            var names = new List<string>();
            foreach (object p in (List<object>)At(doc, "offline_store.prices")) names.Add(CrmDocumentBuilder.ItemName(p));
            Assert.Equal(new[] { "MCVSPack_X", "KillSignature_Avatar_2" }, names);
            Assert.False(((Dictionary<string, object>)doc["offline_store"]).ContainsKey("promotion"));
            Assert.Empty(CrmDocumentValidator.Validate(doc));
        }

        [Fact]
        public void PruningRemovesInvalidChangersThatWouldThrowInTheClient()
        {
            var overlay = MiniJson.ParseObject(@"{""game"":{""_OverrideTables"":{""MCVS_Meta"":{
                ""T"":{""NoSeparator"":{""1"":""2""},""Id@A@B"":{""1"":""2""},""Id@Ok"":{""1"":""5"",""2"":{""x"":1},""3"":null},""Id@Empty"":{}},
                ""U"":""not an object""}}}}");
            var o = new CrmOptions { Overlay = overlay };
            var doc = CrmDocumentBuilder.Build(null, Content(), o, Log.Null);
            var t = (Dictionary<string, object>)At(doc, "game._OverrideTables.MCVS_Meta.T");
            Assert.Equal(new[] { "Id@Ok" }, new List<string>(t.Keys));
            Assert.Equal(new[] { "1" }, new List<string>(((Dictionary<string, object>)t["Id@Ok"]).Keys));
            Assert.Null(At(doc, "game._OverrideTables.MCVS_Meta.U"));
            Assert.Empty(CrmDocumentValidator.Validate(doc));
        }

        [Fact]
        public void SettingsOverlayEventsAndPointcutsAreApplied()
        {
            var o = new CrmOptions
            {
                Overlay = MiniJson.ParseObject(@"{""game"":{""ReviewPopupParams"":{""Ignore"":false}},""newsfeeds"":[]}"),
                EventIds = new List<string> { "evt_1" },
                PointcutActions = MiniJson.ParseArray(@"[
                  {""id"":""daily"",""type"":""give_object"",""frequency"":1,""item"":""HardCurrency"",""quantity"":20,
                   ""pointcuts"":[{""name"":""launch"",""conditions"":[""first_time == False""]}],""limit"":[{""max"":1,""interval"":86400}]},
                  {""id"":""broken"",""type"":""give_object"",""pointcuts"":[]},
                  {""id"":""reset"",""type"":""give_object"",""item"":""ResetProfile"",""pointcuts"":[],""limit"":[]}]"),
            };
            var doc = CrmDocumentBuilder.Build(null, Content(), o, Log.Null);
            Assert.Equal(false, At(doc, "game.ReviewPopupParams.Ignore"));
            Assert.Empty((List<object>)doc["newsfeeds"]);
            Assert.Equal("evt_1", At(doc, "groupware.events.participation.0.id"));
            Assert.Empty((List<object>)At(doc, "groupware.events.advertisement"));
            var actions = (List<object>)doc["pointcut_actions"];
            Assert.Single(actions);
            Assert.Equal("daily", At(actions[0], "id"));
            Assert.Empty(CrmDocumentValidator.Validate(doc));
        }

        [Fact]
        public void ValidatorReportsWhatTheClientWouldDereference()
        {
            var doc = MiniJson.ParseObject(@"{""game"":{""_ServerConfig"":{""default"":{""IsDevelopment"":""false""},""x"":1},
                ""_OverrideTables"":{""MCVS_Meta"":{""T"":{""bad"":{}}}}},
              ""offline_store"":{""prices"":[{""item"":""UnlockMerc_1""}],""promotion"":null},
              ""iap"":{""prices"":[{""_id"":""a""}]},
              ""pointcut_actions"":[{""id"":""p"",""pointcuts"":[{""conditions"":[""broken""]}]}],
              ""fatigue_groups"":[{""name"":""f""}]}");
            var problems = CrmDocumentValidator.Validate(doc);
            string all = string.Join("\n", problems);
            Assert.Contains("IsDevelopment must be a JSON boolean", all);
            Assert.Contains("_ServerConfig.x must be an object", all);
            Assert.Contains("needs exactly one '@'", all);
            Assert.Contains("billing_methods must be a non-empty array", all);
            Assert.Contains("offline_store.promotion", all);
            Assert.Contains("iap.prices[0]: hidden", all);
            Assert.Contains("limit must be an array", all);
            Assert.Contains("fatigue_groups", all);

            Assert.Contains("document is not an object", string.Join("", CrmDocumentValidator.Validate(null)));
            Assert.Contains("game must be an object", string.Join("", CrmDocumentValidator.Validate(new Dictionary<string, object>())));
        }

        [Fact]
        public void OverridesThatWouldBreakTheClientParsersAreRepaired()
        {
            var o = new CrmOptions
            {
                Overlay = MiniJson.ParseObject(@"{""expiry"":12,
                  ""fatigue_groups"":[{""name"":""ok"",""limit"":[]},{""name"":""no limit""}],
                  ""iap"":{""prices"":[{""_id"":""good"",""hidden"":true},{""_id"":""bad""}]},
                  ""groupware"":{""events"":{""participation"":[{""id"":""e1""},{""x"":1}]}}}"),
            };
            var doc = CrmDocumentBuilder.Build(null, Content(), o, Log.Null);
            Assert.False(doc.ContainsKey("expiry"));
            Assert.Single((List<object>)doc["fatigue_groups"]);
            Assert.Equal("good", At(doc, "iap.prices.0._id"));
            Assert.Single((List<object>)At(doc, "iap.prices"));
            Assert.Single((List<object>)At(doc, "groupware.events.participation"));
            Assert.Empty(CrmDocumentValidator.Validate(doc));

            var broken = MiniJson.ParseObject(@"{""pointcut_actions"":{""not"":""a list""}}");
            Assert.Equal(1, CrmDocumentValidator.Sanitize(broken, Log.Null));
            Assert.Empty((List<object>)broken["pointcut_actions"]);
        }

        [Theory]
        [InlineData("UnlockMerc_ALL_MAX", true)]
        [InlineData("WeaponCamo_3_Random", true)]
        [InlineData("HardCurrency", true)]
        [InlineData("VersusCoins_Bonus", true)]
        [InlineData("KillSignature_Sound_2", true)]
        [InlineData("ResetProfile", false)]
        [InlineData("ResetHardCurrency", false)]
        [InlineData("MercPortrait", false)]
        [InlineData("SoftCurrencyPack_HC_A", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void RewardIdsAreCheckedAgainstTheClientsRewardTypes(string id, bool known)
        {
            Assert.Equal(known, CrmDocumentValidator.IsKnownRewardId(id));
        }

        // ------------------------------------------------------------------------------------------ collector (§2.8)

        private static CrmCollector Part(CollectorPhase phase)
        {
            return new CrmCollector
            {
                Status = new CollectorStatus { Phase = phase, Reason = "test" },
                Content = phase == CollectorPhase.Active ? CollectorPlanTests.Shipped() : null,
                Options = new CollectorOptions(),
            };
        }

        [Fact]
        public void SetCellWritesOneSetCellWithHashForDots()
        {
            var tables = new Dictionary<string, object>();
            CrmDocumentBuilder.SetCell(tables, "MCVS_Meta", "LootCrates", "Id", "RewardChance.2", "Crate_Event_Special", 0L);
            CrmDocumentBuilder.SetCell(tables, "MCVS_Meta", "LootCrates", "Id", "LocId", "Crate_Event_Special", "crm.CrateCustomization");
            CrmDocumentBuilder.SetCell(tables, "MCVS_Meta", "Crate_Event_Special", "Id", "Weight.1", "1", 7);   // an int is widened
            Assert.Equal("{\"MCVS_Meta\":{\"LootCrates\":{\"Id@RewardChance#2\":{\"Crate_Event_Special\":0},\"Id@LocId\":{\"Crate_Event_Special\":\"crm.CrateCustomization\"}},"
                         + "\"Crate_Event_Special\":{\"Id@Weight#1\":{\"1\":7}}}}", MiniJson.Serialize(tables));
            Assert.IsType<long>(At(tables, "MCVS_Meta.Crate_Event_Special.Id@Weight#1.1"));

            // A SET replaces whatever the cell holds (one operation per cell): a MUL from an earlier layer too.
            ((Dictionary<string, object>)At(tables, "MCVS_Meta.Crate_Event_Special.Id@Weight#1"))["1"] = "*2";
            CrmDocumentBuilder.SetCell(tables, "MCVS_Meta", "Crate_Event_Special", "Id", "Weight.1", "1", 3L);
            Assert.Equal(3L, At(tables, "MCVS_Meta.Crate_Event_Special.Id@Weight#1.1"));

            foreach (object bad in new object[] { "", null, 2.5, 2.0, true, double.NaN })
                Assert.Throws<System.ArgumentException>(() => CrmDocumentBuilder.SetCell(tables, "MCVS_Meta", "LootCrates", "Id", "CratesCount", "x", bad));
            Assert.Throws<System.ArgumentException>(() => CrmDocumentBuilder.SetCell(tables, "MCVS_Meta", "LootCrates", "Id@X", "CratesCount", "x", 1L));
            Assert.Throws<System.ArgumentException>(() => CrmDocumentBuilder.SetCell(tables, "MCVS_Meta", "LootCrates", "Id", "CratesCount", "", 1L));
            Assert.Null(At(tables, "MCVS_Meta.LootCrates.Id@CratesCount"));
        }

        /// <summary>Dormant writes nothing (byte-identical to a build without the collector); Guard writes the 5 guard
        /// cells into both trees; Active writes the layer (mirror without the true cells) after the live-ops cells.</summary>
        [Fact]
        public void CollectorPartWritesNothingTheGuardOrTheLayer()
        {
            LiveOpsSnapshot snapshot = CollectorPlanTests.Snapshot(CollectorPlanTests.Week147);
            string without = MiniJson.Serialize(CrmDocumentBuilder.Build(Pre(), Content(), new CrmOptions(), Log.Null, snapshot, null));

            CrmCollector dormant = Part(CollectorPhase.Dormant);
            Assert.Equal(without, MiniJson.Serialize(CrmDocumentBuilder.Build(Pre(), Content(), new CrmOptions(), Log.Null, snapshot, null, dormant)));
            Assert.Equal(CollectorPhase.Dormant, dormant.Phase);
            Assert.Empty(dormant.GameCells);

            CrmCollector guard = Part(CollectorPhase.Guard);
            var doc = CrmDocumentBuilder.Build(Pre(), Content(), new CrmOptions(), Log.Null, snapshot, null, guard);
            Assert.Equal(CollectorPhase.Guard, guard.Phase);
            Assert.Null(guard.Failure);
            foreach (string tree in new[] { "game._OverrideTables", "game._ServerConfig.default._OverrideTables" })
            {
                Assert.Equal("{\"Id@Type\":{\"1\":\"VersusCoins\"},\"Id@Subtype\":{\"1\":\"Tiers4\"},\"Id@Weight#1\":{\"1\":1}}",
                             MiniJson.Serialize(At(doc, tree + ".MCVS_Meta.Crate_Event_Special")));
                Assert.Equal("{\"Id@LocId\":{\"Crate_Event_Special\":\"crm.CrateCustomization\"},\"Id@RewardChance#2\":{\"Crate_Event_Special\":0}}",
                             MiniJson.Serialize(At(doc, tree + ".MCVS_Meta.LootCrates")));
                Assert.Equal("7", At(doc, tree + ".MCVS_Meta.MercsList.Id@TrialDays.3"));   // the preconfiguration's cells stay
            }
            Assert.Empty(CrmDocumentValidator.Validate(doc));

            CrmCollector active = Part(CollectorPhase.Active);
            doc = CrmDocumentBuilder.Build(Pre(), Content(), new CrmOptions(), Log.Null, snapshot, null, active);
            Assert.Equal(CollectorPhase.Active, active.Phase);
            Assert.Same(active.Layer.Game, active.GameCells);
            Assert.Equal(510, active.GameCells.Count);
            Assert.Equal(365, active.MirrorCells.Count);
            Assert.Equal("true", At(doc, "game._OverrideTables.MCVS_Meta.KillsignatureAvatars.ID@Unlockable.225"));
            Assert.Null(At(doc, "game._ServerConfig.default._OverrideTables.MCVS_Meta.KillsignatureAvatars.ID@Unlockable.225"));
            Assert.Equal("false", At(doc, "game._ServerConfig.default._OverrideTables.MCVS_Meta.KillsignatureAvatars.ID@Unlockable.598"));
            Assert.Null(At(doc, "game._ServerConfig.default._OverrideTables.MCVS_Meta.KillsignatureBackgrounds"));
            Assert.Equal(5L, At(doc, "game._OverrideTables.MCVS_Meta.LootCrates.Id@CratesCount.Crate_Event_Special"));
            Assert.Equal("KillSignature_Avatar_264", At(doc, "game._OverrideTables.MCVS_Meta.Crate_Gold_FirstPlace.Id@BundledRewards.1"));
            Assert.Empty(CrmDocumentValidator.Validate(doc));
            // The per-player mirror: the preconfiguration's cells and the guard, nothing else of the collector.
            Assert.Equal("{\"MercsList\":{\"Id@TrialDays\":{\"3\":\"7\"}},\"Crate_Event_Special\":{\"Id@Type\":{\"1\":\"VersusCoins\"},"
                         + "\"Id@Subtype\":{\"1\":\"Tiers4\"},\"Id@Weight#1\":{\"1\":1}},\"LootCrates\":{\"Id@LocId\":{\"Crate_Event_Special\":"
                         + "\"crm.CrateCustomization\"},\"Id@RewardChance#2\":{\"Crate_Event_Special\":0}}}",
                         MiniJson.Serialize(active.PlayerMirrorTables["MCVS_Meta"]));
        }

        /// <summary>A cell an overlay puts into <c>_ServerConfig.default._OverrideTables</c> itself stays in the mirror (the
        /// mirror pass lets it win), and one that breaks the crate there replaces the layer by the guard in both trees.</summary>
        [Fact]
        public void TheMirrorKeepsExplicitCellsAndIsChecked()
        {
            LiveOpsSnapshot snapshot = CollectorPlanTests.Snapshot(CollectorPlanTests.Week147);
            var o = new CrmOptions
            {
                Overlay = MiniJson.ParseObject(@"{""game"":{""_ServerConfig"":{""default"":{""_OverrideTables"":{""MCVS_Meta"":{
                    ""KillsignatureAvatars"":{""ID@Unlockable"":{""225"":""true""}}}}}}}}"),
            };
            CrmCollector active = Part(CollectorPhase.Active);
            var doc = CrmDocumentBuilder.Build(null, Content(), o, Log.Null, snapshot, null, active);
            Assert.Equal(CollectorPhase.Active, active.Phase);
            Assert.Equal("true", At(doc, "game._ServerConfig.default._OverrideTables.MCVS_Meta.KillsignatureAvatars.ID@Unlockable.225"));
            Assert.Null(At(doc, "game._ServerConfig.default._OverrideTables.MCVS_Meta.KillsignatureAvatars.ID@Unlockable.226"));

            o.Overlay = MiniJson.ParseObject(@"{""game"":{""_ServerConfig"":{""default"":{""_OverrideTables"":{""MCVS_Meta"":{
                ""Crate_Event_Special"":{""Id@Type"":{""2"":""""}}}}}}}}");
            active = Part(CollectorPhase.Active);
            var logs = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(logs);
            doc = CrmDocumentBuilder.Build(null, Content(), o, logger.For("crm"), snapshot, null, active);
            Assert.Equal(CollectorPhase.Guard, active.Phase);
            Assert.Contains("game._ServerConfig.default._OverrideTables.MCVS_Meta.Crate_Event_Special", active.Failure);
            Assert.True(logs.Contains("CRM: collector layer replaced by the guard: "));
            foreach (string tree in new[] { "game._OverrideTables", "game._ServerConfig.default._OverrideTables" })
            {
                Assert.Equal("{\"Id@Type\":{\"1\":\"VersusCoins\"},\"Id@Subtype\":{\"1\":\"Tiers4\"},\"Id@Weight#1\":{\"1\":1}}",
                             MiniJson.Serialize(At(doc, tree + ".MCVS_Meta.Crate_Event_Special")));
                Assert.Equal("{\"Id@LocId\":{\"Crate_Event_Special\":\"crm.CrateCustomization\"},\"Id@RewardChance#2\":{\"Crate_Event_Special\":0}}",
                             MiniJson.Serialize(At(doc, tree + ".MCVS_Meta.LootCrates")));   // no tier crate retargeted
                Assert.Null(At(doc, tree + ".MCVS_Meta.Crate_Gold_FirstPlace"));
                Assert.Null(At(doc, tree + ".MCVS_Meta.KillsignatureAvatars"));
            }
            Assert.Null(active.PlayerMirrorTables);
        }

        [Fact]
        public void CheckCollectorFindsWhatWouldThrowOnTheClient()
        {
            Dictionary<string, object> Tables(string crate) => MiniJson.ParseObject("{\"MCVS_Meta\":{\"Crate_Event_Special\":" + crate + "}}");
            var guard = CollectorGuard.Cells;

            Assert.Empty(CrmDocumentValidator.CheckCollector(Tables(@"{""Id@Type"":{""1"":""VersusCoins""},""Id@Subtype"":{""1"":""Tiers4""},""Id@Weight#1"":{""1"":1}}"),
                                                             guard, "t"));
            // A weight without a type (NullReferenceException in the TS's table refill).
            Assert.Contains("row 2 has a weight but no Id@Type",
                            Assert.Single(CrmDocumentValidator.CheckCollector(Tables(
                                @"{""Id@Type"":{""1"":""VersusCoins""},""Id@Subtype"":{""1"":""Tiers4""},""Id@Weight#1"":{""1"":1},""Id@Weight#2"":{""2"":0}}"), guard, "t")));
            // A sound row with weight (KeyNotFoundException in every preview), and one without weight (fine).
            Assert.Contains("cannot be previewed",
                            Assert.Single(CrmDocumentValidator.CheckCollector(Tables(
                                @"{""Id@Type"":{""1"":""VersusCoins"",""2"":""KillSignature""},""Id@Subtype"":{""1"":""Tiers4"",""2"":""Sound""},
                                  ""Id@Subtype2"":{""2"":""2""},""Id@Weight#1"":{""1"":1,""2"":5}}"), guard, "t")));
            Assert.Empty(CrmDocumentValidator.CheckCollector(Tables(
                @"{""Id@Type"":{""1"":""VersusCoins"",""2"":""KillSignature""},""Id@Subtype"":{""1"":""Tiers4"",""2"":""Sound""},
                  ""Id@Subtype2"":{""2"":""2""},""Id@Weight#1"":{""1"":1,""2"":0}}"), guard, "t"));
            // Nothing drawable (an empty crate throws on claim); the table missing altogether.
            Assert.Contains("no row with Weight#1 > 0", Assert.Single(CrmDocumentValidator.CheckCollector(Tables(
                @"{""Id@Type"":{""1"":""VersusCoins""},""Id@Subtype"":{""1"":""Tiers4""},""Id@Weight#1"":{""1"":0}}"), guard, "t")));
            Assert.Contains("no row with Weight#1 > 0", Assert.Single(CrmDocumentValidator.CheckCollector(new Dictionary<string, object>(), guard, "t")));
            // A collector cell an overlay made empty, fractional or a bool; a weight that is an operation.
            foreach (string bad in new[] { @"""""", "1.5", "true" })
                Assert.Contains("must be a non-empty string or an integer", string.Join("\n", CrmDocumentValidator.CheckCollector(Tables(
                    @"{""Id@Type"":{""1"":""VersusCoins""},""Id@Subtype"":{""1"":" + bad + @"},""Id@Weight#1"":{""1"":1}}"), guard, "t")));
            Assert.Contains("Id@Weight#1 is not a number", string.Join("\n", CrmDocumentValidator.CheckCollector(Tables(
                @"{""Id@Type"":{""1"":""VersusCoins""},""Id@Subtype"":{""1"":""Tiers4""},""Id@Weight#1"":{""1"":""*2""}}"), null, "t")));
            // A numeric-looking string weight is what the client parses: fine.
            Assert.Empty(CrmDocumentValidator.CheckCollector(Tables(
                @"{""Id@Type"":{""1"":""VersusCoins""},""Id@Subtype"":{""1"":""Tiers4""},""Id@Weight#1"":{""1"":""3""}}"), null, "t"));
        }

        [Fact]
        public void PreconfigurationCompactionDropsEmptyChangersAndOtherLanguages()
        {
            var doc = Pre();
            CrmPreconfiguration.Compact(doc, new[] { "en" });
            Assert.Null(At(doc, "game._OverrideTables.MCVS_Gameplay"));
            Assert.NotNull(At(doc, "game._OverrideTables.MCVS_Meta"));
            Assert.Equal(new[] { "en" }, new List<string>(((Dictionary<string, object>)doc["iap"]).Keys));
            CrmPreconfiguration.Compact(doc, new string[0]);
            Assert.Empty((Dictionary<string, object>)doc["iap"]);
        }
    }
}
