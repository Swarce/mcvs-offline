using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm;
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
