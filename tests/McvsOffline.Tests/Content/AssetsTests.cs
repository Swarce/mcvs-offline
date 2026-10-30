using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Assets;
using McvsOffline.Server.Services.Crm;
using McvsOffline.Server.Services.Storage;
using McvsOffline.Server.Services.Unlocks;
using McvsOffline.Tests.Storage;
using Xunit;

namespace McvsOffline.Tests.Content
{
    /// <summary>Coupons (coupons/{cid}/{code}) and Iris assets (assets/{cid}/...).</summary>
    public class AssetsTests
    {
        /// <summary>The "mercs" unlock group: every displayed merc but the starting one (1), then every merc's mods.</summary>
        internal static readonly string[] MercGroup = Enumerable.Range(2, 20).Select(i => "UnlockMerc_" + i)
            .Concat(Enumerable.Range(1, 21).Select(i => "ModTier_" + i + "_4")).ToArray();

        private static string CouponPath(string code) => "/coupons/" + ContentTestSupport.ClientId + "/" + code;

        /// <summary>The game's redeem request: no token, the game's User-Agent, no browser header (RedeemPageCodeTests).</summary>
        private static HttpResponse GameGet(McvsServerHandle h, string code, params string[] extraHeaders)
        {
            var headers = new HttpHeaders();
            headers.Add("Host", "127.0.0.1:51780");
            headers.Add("User-Agent", "(" + ContentTestSupport.ClientId + "; mcvs)");
            for (int i = 0; i + 1 < extraHeaders.Length; i += 2) headers.Add(extraHeaders[i], extraHeaders[i + 1]);
            return h.Server.Handle(new HttpRequest("GET", CouponPath(code), headers, null));
        }

        /// <summary>An inbox code as the game gets it: 200 and its rewards.</summary>
        private static List<object> Redeem(McvsServerHandle h, string code)
        {
            var response = GameGet(h, code);
            Assert.Equal(200, response.StatusCode);
            return (List<object>)ContentTestSupport.Json(response);
        }

        /// <summary>What a code holds, whatever its delivery (the ledger codes answer the game [] instead).</summary>
        private static List<object> Payload(McvsServerHandle h, string code)
        {
            var catalog = CouponCatalog.Load(h.Server.Context.Content, h.Server.Context.Settings, null);
            List<object> rewards = catalog.Resolve(code, out _);
            Assert.NotNull(rewards);
            return rewards;
        }

        /// <summary>
        /// A player whose TS has loaded and saved (the sections R3 creates, then the R4 game profile), as the coupon's
        /// "current player" needs: with one such profile and no host, CurrentPlayerResolver answers "single".
        /// </summary>
        private static string SavePlayer(McvsServerHandle h, string user)
        {
            McvsServer server = h.Server;
            Account account = server.Context.Accounts.GetOrCreate("anonymous:" + user, "d");
            string token = server.Context.Tokens.Issue(account.Id, "anonymous:" + user, McvsConstants.ClientId,
                                                       StorageHarness.ServerScopes, "d", "game:mcvsoffline").Token;
            foreach (var section in new[] { "_mcvs.matchers", "_mcvs.snapshot", "inventory", "_inventory", "_mcvs.gameProfile" })
            {
                string form = UrlEncoding.EncodeForm(new[]
                {
                    new KeyValuePair<string, string>("access_token", token),
                    new KeyValuePair<string, string>("visibility", "restricted"),
                    new KeyValuePair<string, string>("object", section == "_mcvs.gameProfile" ? GameData.GameProfileDump : "{}"),
                });
                Assert.Equal(200, server.Handle(HttpRequest.Create("POST", "/profiles/me/myprofile/" + section, form)).StatusCode);
            }
            return account.Id;
        }

        /// <summary>Unlock batches without delays (the defaults wait 10 s around each one).</summary>
        private static void FastUnlocks(McvsServerHandle h, long batchSize = 400)
        {
            h.Server.Context.Settings.Override("unlocks", new Dictionary<string, object>
            {
                ["batchSize"] = batchSize, ["batchDelaySeconds"] = 0L, ["startDelaySeconds"] = 0L, ["streamWaitSeconds"] = 0L,
            });
        }

        private static Dictionary<string, object> Inventory(McvsServerHandle h, string account) =>
            h.Server.Context.Features.Get<IProfileAccess>().Read(account, "inventory") as Dictionary<string, object>;

        private static string WaitForCode(McvsServerHandle h, string account, string code, string prefix)
        {
            UnlockGranter granter = h.Server.Context.Features.Get<UnlockGranter>();
            for (int i = 0; i < 400; i++)
            {
                if (granter.CodeStatus().TryGetValue(account, out object codes)
                    && ((Dictionary<string, object>)codes).TryGetValue(code, out object state)
                    && ((string)state).StartsWith(prefix, StringComparison.Ordinal))
                    return (string)state;
                Thread.Sleep(50);
            }
            throw new TimeoutException(code + " of " + account + ": " + MiniJson.Serialize(granter.CodeStatus()));
        }

        private static List<string> Types(List<object> rewards)
        {
            var list = new List<string>();
            foreach (object r in rewards) list.Add((string)((Dictionary<string, object>)r)["type"]);
            return list;
        }

        [Fact]
        public void UnlockAllStartsWithTheMercUnlockAndCoversEveryGroupInOrder()
        {
            using var h = new McvsServerHandle();
            var rewards = Payload(h, "UNLOCKALL");
            var types = Types(rewards);
            // verify-C5 (b): rewards are claimed in array order and merc-dependent items of a locked merc are lost,
            // so every merc (then every mod) comes first, as explicit per-merc rewards: UnlockMerc_ALL_MAX crashed the
            // reward display ("Could not find merc data for mercId 'ALL'", device test 3).
            Assert.Equal(MercGroup, types.Take(MercGroup.Length));
            Assert.DoesNotContain(types, t => t.Contains("_ALL"));
            Assert.DoesNotContain("UnlockMerc_1", types); // starting merc: its reward would become 40 random gear
            Assert.True(types.Count > 10000, "only " + types.Count + " rewards");
            Assert.Equal(types.Count, new HashSet<string>(types).Count);
            foreach (object r in rewards)
            {
                var o = (Dictionary<string, object>)r;
                Assert.Equal(1L, o["quantity"]);  // JSON integers (SafeGetInt)
                Assert.True(CrmDocumentValidator.IsKnownRewardId((string)o["type"]), (string)o["type"]);
            }
            int firstWeapon = types.FindIndex(t => t.StartsWith("MercWeapon_"));
            int firstSignature = types.FindIndex(t => t.StartsWith("KillSignature_"));
            int firstCamo = types.FindIndex(t => t.StartsWith("WeaponCamo_"));
            int firstAccessory = types.FindIndex(t => t.StartsWith("MercAccessory_"));
            int firstSkin = types.FindIndex(t => t.StartsWith("MercSkin_"));
            Assert.True(0 < firstWeapon && firstWeapon < firstSignature && firstSignature < firstCamo
                        && firstCamo < firstAccessory && firstAccessory < firstSkin);
            Assert.DoesNotContain("KillSignature_Avatar_1", types); // starting inventory
            Assert.Contains("KillSignature_Sound_5", types);
            Assert.Contains("GearSlot_1_6", types);
        }

        [Fact]
        public void CodesAreCaseInsensitiveAndAcceptANumericSuffix()
        {
            using var h = new McvsServerHandle();
            Assert.Equal(MercGroup[0], Types(Payload(h, "unlockall"))[0]);
            // The game remembers claimed codes per profile; UNLOCKALL2 lets a player redeem again.
            Assert.Equal(MercGroup[0], Types(Payload(h, "UnlockAll2"))[0]);
            Assert.Equal(404, ContentTestSupport.Get(h.Server, CouponPath("UNLOCKALLX")).StatusCode);
            Assert.Equal(404, ContentTestSupport.Get(h.Server, CouponPath("NOPE")).StatusCode);
            Assert.Equal(404, ContentTestSupport.Get(h.Server, CouponPath("2")).StatusCode);
        }

        [Fact]
        public void SplitCodesUnlockMercsFirst()
        {
            using var h = new McvsServerHandle();
            foreach (string code in new[] { "UNLOCKWEAPONS", "UNLOCKGEARSLOTS", "UNLOCKCAMOS", "UNLOCKACCESSORIES", "UNLOCKSKINS" })
            {
                var types = Types(Payload(h, code));
                Assert.Equal(MercGroup, types.Take(MercGroup.Length));
                Assert.True(types.Count > MercGroup.Length, code);
            }
            Assert.Equal(MercGroup, Types(Payload(h, "UNLOCKMERCS")));
            Assert.StartsWith("KillSignature_", Types(Payload(h, "UNLOCKSIGNATURES"))[0]);
            Assert.Equal(4409, Types(Payload(h, "UNLOCKCAMOS")).Count - MercGroup.Length);
        }

        [Fact]
        public void PaidAndUnreleasedCodesAreOptInAndDisjointFromUnlockAll()
        {
            using var h = new McvsServerHandle();
            var all = new HashSet<string>(Types(Payload(h, "UNLOCKALL")));
            // Defined in the tables, never released (no shipped source, no live ecomm id, asset on disk): opt-in only.
            var unreleased = Types(Payload(h, "UNLOCKUNRELEASED"));
            Assert.Equal(Enumerable.Range(1513, 6).Select(i => "KillSignature_Avatar_" + i), unreleased);
            Assert.DoesNotContain(unreleased, all.Contains);
            // Paid-only (VIP pool camos and jerseys): the mercs first, without the mods; all of it is also in UNLOCKALL.
            var paid = Types(Payload(h, "UNLOCKPAID"));
            Assert.Equal(MercGroup.Take(20), paid.Take(20));
            Assert.DoesNotContain(paid, t => t.StartsWith("ModTier_", StringComparison.Ordinal));
            Assert.Equal(20 + 2684 + 2940, paid.Count);
            Assert.Equal(paid.Count, new HashSet<string>(paid).Count);
            Assert.All(paid, t => Assert.Contains(t, all));
            var camos = Types(Payload(h, "UNLOCKPAIDCAMOS"));
            var jerseys = Types(Payload(h, "UNLOCKPAIDACCESSORIES"));
            Assert.Equal(20 + 2684, camos.Count);
            Assert.All(camos.Skip(20), t => Assert.StartsWith("WeaponCamo_", t));
            Assert.Equal(20 + 2940, jerseys.Count);
            Assert.All(jerseys.Skip(20), t => Assert.StartsWith("MercAccessory_", t));
            Assert.Equal(404, ContentTestSupport.Get(h.Server, CouponPath("UNLOCKUNRELEASEDX")).StatusCode);
        }

        [Fact]
        public void SettingsAddCodesAndCanDisableCoupons()
        {
            using var h = new McvsServerHandle();
            h.Server.Context.Settings.Override("assets", MiniJson.ParseObject(@"{""coupons"":{
                ""gift"":{""rewards"":[{""type"":""HardCurrency"",""quantity"":500},{""type"":""ResetProfile"",""quantity"":1},{""type"":""VersusCoins""}]},
                ""MIX"":{""groups"":[""mercs"",""nope""],""rewards"":[{""type"":""Crate_MOD_T1"",""quantity"":2}]},
                ""UNLOCKALL"":{""delivery"":""inbox"",""rewards"":[{""type"":""VersusCoins"",""quantity"":1}]}}}"));
            var gift = Redeem(h, "GIFT");
            Assert.Equal(2, gift.Count); // ResetProfile is never sent
            Assert.Equal(500L, JsonPath.Get(gift[0], "quantity"));
            Assert.Equal(1L, JsonPath.Get(gift[1], "quantity")); // default quantity
            Assert.Equal(MercGroup.Concat(new[] { "Crate_MOD_T1" }), Types(Redeem(h, "mix")));
            Assert.Equal(new[] { "VersusCoins" }, Types(Redeem(h, "UNLOCKALL"))); // settings replace built-in codes, delivery included
            Assert.True(h.Logs.Contains("ResetProfile"));

            // (an override of "assets" shadows deeper keys, so replace the whole object)
            h.Server.Context.Settings.Override("assets", MiniJson.ParseObject(@"{""couponsEnabled"":false,""coupons"":{""gift"":{""rewards"":[]}}}"));
            Assert.Equal(404, ContentTestSupport.Get(h.Server, CouponPath("GIFT")).StatusCode);
        }

        // ------------------------------------------------------------------ delivery (inbox-freeze report §3 F1)

        /// <summary>
        /// Every unlock code is delivered through the inventory ledger: the game rebuilt every reward of an unclaimed
        /// coupon message each time the inbox opened (and every 10 s while open), so UNLOCKCAMOS (4,450 rewards) froze it.
        /// The coupons.json flag is explicit, and the UNLOCK* name rule covers settings codes without one.
        /// </summary>
        [Fact]
        public void EveryUnlockCodeAndEveryRepeatAndCaseVariantIsALedgerCode()
        {
            using var h = new McvsServerHandle();
            var catalog = CouponCatalog.Load(h.Server.Context.Content, h.Server.Context.Settings, null);
            var shipped = (Dictionary<string, object>)MiniJson.ParseObject(File.ReadAllText(
                Path.Combine(ContentTestSupport.RepoRoot, "server", "Content", CouponCatalog.CouponsFile)))["codes"];
            Assert.Equal(12, shipped.Count);
            foreach (string code in shipped.Keys)
            {
                Assert.StartsWith(CouponCatalog.UnlockPrefix, code);
                Assert.Equal("ledger", JsonPath.Get(shipped[code], "delivery"));
                foreach (string variant in new[] { code, code.ToLowerInvariant(), code + "2", code.Substring(0, 3).ToLowerInvariant() + code.Substring(3) + "17" })
                {
                    CouponMatch match = catalog.Match(variant);
                    Assert.True(match != null, variant);
                    Assert.Equal(CouponCatalog.LedgerDelivery, match.Delivery);
                    Assert.Equal(code, match.Definition);
                    Assert.Equal(Types(match.Rewards), match.Keys); // every key once, in claim order
                }
            }
            h.Server.Context.Settings.Override("assets.coupons", MiniJson.ParseObject(
                @"{""unlockMine"":{""groups"":[""skins""]},""GIFT1"":{""rewards"":[{""type"":""HardCurrency""}]}}"));
            catalog = CouponCatalog.Load(h.Server.Context.Content, h.Server.Context.Settings, null);
            Assert.Equal(CouponCatalog.LedgerDelivery, catalog.Match("UNLOCKMINE").Delivery);
            Assert.Equal(CouponCatalog.InboxDelivery, catalog.Match("gift1").Delivery);
        }

        /// <summary>
        /// The content guard: no code the server ships sends more than 300 rewards to the inbox. A new UNLOCK code
        /// that was given "delivery": "inbox" would recreate the freeze.
        /// </summary>
        [Fact]
        public void NoShippedInboxCodeHoldsMoreThan300Rewards()
        {
            using var h = new McvsServerHandle();
            var catalog = CouponCatalog.Load(h.Server.Context.Content, h.Server.Context.Settings, null);
            List<CouponDescription> codes = catalog.Describe();
            Assert.Equal(12, codes.Count);
            foreach (CouponDescription code in codes)
            {
                Assert.False(code.TooLargeForTheInbox, code.Code + ": " + code.Rewards + " rewards for one inbox message");
                if (code.Delivery == CouponCatalog.InboxDelivery) Assert.True(code.Rewards <= CouponCatalog.MaxInboxRewards, code.Code);
                // A shipped ledger code holds only unlocks: nothing in it is skipped while the game says "redeemed".
                Assert.Equal(0, code.SkippedOnTheLedger);
            }
            // The largest built-in code, for scale: it could never be an inbox code.
            Assert.True(codes.Single(c => c.Code == "UNLOCKALL").Rewards > 11000);
            Assert.Equal(300, CouponCatalog.MaxInboxRewards);
        }

        /// <summary>
        /// UNLOCKCAMOS through the game's request: 200 with an empty list (the game shows "redeem successful" and makes an
        /// empty inbox message), and the merc keys, then the 4,409 camos, arrive in the player's inventory ledger.
        /// </summary>
        [Fact]
        public void ALedgerCodeAnswersAnEmptyListAndGrantsItsKeysThroughTheLedger()
        {
            using var h = new McvsServerHandle();
            FastUnlocks(h);
            string account = SavePlayer(h, "coupon1");
            Assert.Empty(Inventory(h, account));

            HttpResponse response = GameGet(h, "unlockCamos");
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("[]", response.BodyText);
            Assert.True(h.Logs.Contains("coupon 'unlockCamos' (UNLOCKCAMOS): 4450 unlock keys queued for " + account
                                        + " through the inventory ledger, at most 13 batch(es)"), "redeem log line");
            Assert.Equal("done (4450 keys written)", WaitForCode(h, account, "UNLOCKCAMOS", "done"));

            var inventory = Inventory(h, account);
            Assert.Equal(MercGroup, inventory.Keys.Take(MercGroup.Length));
            Assert.Equal(4409, inventory.Keys.Count(k => k.StartsWith("WeaponCamo_", StringComparison.Ordinal)));
            Assert.Equal(4450, inventory.Count);
            Assert.All(inventory.Values, v => Assert.Equal(1L, v));
            Assert.True(h.Logs.Contains("unlock code UNLOCKCAMOS: " + account + ": done, 4450 keys written in 13 batch(es)"), "finished log line");
            LocalProfileStore store = h.Server.Context.Features.Get<LocalProfileStore>();
            Assert.Contains(store.Backups.List(account), b => b.Reason == UnlockGranter.CodeBackupReason);

            // A numbered repeat is a no-op for keys already owned (the duplicate compensation of an inbox claim is gone).
            Assert.Equal("[]", GameGet(h, "UNLOCKCAMOS2").BodyText);
            Assert.Equal("done (nothing missing)", WaitForCode(h, account, "UNLOCKCAMOS2", "done"));
            Assert.Equal(4450, Inventory(h, account).Count);
            Assert.Empty(h.Server.Context.Features.Get<UnlockGranter>().PendingCodes());

            // A small inbox code is unchanged, MCVSOFFLINE still does not exist for the game.
            h.Server.Context.Settings.Override("assets.coupons", MiniJson.ParseObject(
                @"{""GIFT2"":{""rewards"":[{""type"":""HardCurrency"",""quantity"":500},{""type"":""UnlockMerc_3""}]}}"));
            Assert.Equal(new[] { "HardCurrency", "UnlockMerc_3" }, Types(Redeem(h, "gift2")));
            Assert.Equal(404, GameGet(h, "MCVSOFFLINE").StatusCode);
        }

        [Fact]
        public void ALedgerCodeIsRefusedInLanModeAndNothingIsWritten()
        {
            using var h = new McvsServerHandle(lan: true);
            FastUnlocks(h);
            string account = SavePlayer(h, "lan1");
            Assert.Equal(404, GameGet(h, "UNLOCKMERCS").StatusCode);
            Assert.True(h.Logs.Contains("coupon 'UNLOCKMERCS' (UNLOCKMERCS) refused, nothing written: LAN mode is on"));
            AssertNothingGranted(h, account);
        }

        [Fact]
        public void ALedgerCodeIsRefusedWhenThePlayerIsAmbiguousAndNothingIsWritten()
        {
            using var h = new McvsServerHandle();
            FastUnlocks(h);
            string first = SavePlayer(h, "two1");
            string second = SavePlayer(h, "two2"); // two profiles with progress, no host, no login of the game: who redeemed?
            Assert.Equal(404, GameGet(h, "UNLOCKSKINS").StatusCode);
            Assert.True(h.Logs.Contains("coupon 'UNLOCKSKINS' (UNLOCKSKINS) refused, nothing written: the current player is unknown or ambiguous"));
            AssertNothingGranted(h, first);
            AssertNothingGranted(h, second);
        }

        /// <summary>A web page (browser headers, its own User-Agent) must not unlock the player's items with a GET.</summary>
        [Fact]
        public void ALedgerCodeFromAWebPageIsRefused()
        {
            using var h = new McvsServerHandle();
            FastUnlocks(h);
            string account = SavePlayer(h, "web1");
            Assert.Equal(404, GameGet(h, "UNLOCKALL", "Sec-Fetch-Site", "cross-site").StatusCode);
            Assert.Equal(404, ContentTestSupport.Get(h.Server, CouponPath("UNLOCKALL")).StatusCode); // no game User-Agent
            Assert.True(h.Logs.Contains("refused, nothing written: a web browser's request"));
            Assert.True(h.Logs.Contains("refused, nothing written: not the game's User-Agent"));
            AssertNothingGranted(h, account);
        }

        /// <summary>An inbox code above 300 rewards is refused before the game sees it, with a log line and a page warning.</summary>
        [Fact]
        public void AnOversizedInboxCodeIsRefused()
        {
            using var h = new McvsServerHandle();
            h.Server.Context.Settings.Override("assets.coupons", MiniJson.ParseObject(
                @"{""BIGGIFT"":{""groups"":[""camos""]},""UNLOCKINBOX"":{""delivery"":""inbox"",""groups"":[""gearSlots""]}}"));
            Assert.Equal(404, GameGet(h, "BIGGIFT").StatusCode);
            Assert.Contains(h.Logs.Snapshot(), e => e.Level == LogLevel.Warn
                && e.Message.Contains("coupon 'BIGGIFT' (BIGGIFT) refused: 4409 rewards in one inbox message would freeze the game's inbox (at most 300)"));
            CouponDescription big = CouponCatalog.Load(h.Server.Context.Content, h.Server.Context.Settings, null).Describe().Single(c => c.Code == "BIGGIFT");
            Assert.True(big.TooLargeForTheInbox);
            // At most 300 is fine, also for an UNLOCK code whose definition sends it to the inbox (109 gear slots).
            Assert.Equal(109, Redeem(h, "UNLOCKINBOX").Count);
        }

        private static void AssertNothingGranted(McvsServerHandle h, string account)
        {
            Thread.Sleep(100);
            Assert.Empty(Inventory(h, account));
            UnlockGranter granter = h.Server.Context.Features.Get<UnlockGranter>();
            Assert.Empty(granter.CodeStatus());
            Assert.Empty(granter.PendingCodes());
            Assert.DoesNotContain(h.Server.Context.Features.Get<LocalProfileStore>().Backups.List(account), b => b.Reason == UnlockGranter.CodeBackupReason);
        }

        [Theory]
        [InlineData("A_{1-3}", "A_1,A_2,A_3")]
        [InlineData("W_7_{2,4-5,9}", "W_7_2,W_7_4,W_7_5,W_7_9")]
        [InlineData("Plain_1", "Plain_1")]
        [InlineData("P{1}S", "P1S")]
        public void RewardPatternsExpand(string pattern, string expected)
        {
            Assert.Equal(expected, string.Join(",", RewardPattern.Expand(pattern)));
        }

        [Theory]
        [InlineData("A_{3-1}")]
        [InlineData("A_{x}")]
        [InlineData("A_{1}{2}")]
        [InlineData("A_{1")]
        [InlineData("A_1}")]
        [InlineData("A_{0-100000000}")]
        public void BadRewardPatternsAreRejected(string pattern)
        {
            Assert.Throws<FormatException>(() => RewardPattern.Expand(pattern));
        }

        [Fact]
        public void MissingAssetsAre404AndLocalOnesAreServed()
        {
            using var h = new McvsServerHandle();
            string root = "/assets/" + ContentTestSupport.ClientId;
            Assert.Equal("[]", ContentTestSupport.Get(h.Server, root).BodyText);
            Assert.Equal(404, ContentTestSupport.Get(h.Server, root + "/tech_en.json").StatusCode);
            Assert.Equal(404, ContentTestSupport.Get(h.Server, root + "/GameOptions/metadata/hash").StatusCode);
            Assert.True(h.Logs.Contains("asset 'tech_en.json' not available locally"));

            string dir = h.Combine("data", "assets");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "tech_en.json"), "{\"a\":1}");
            File.WriteAllBytes(Path.Combine(dir, "sub", "img.png"), Encoding.ASCII.GetBytes("0123456789"));

            var json = ContentTestSupport.Get(h.Server, root + "/tech_en.json");
            Assert.Equal(200, json.StatusCode);
            Assert.Equal("{\"a\":1}", json.BodyText);
            Assert.StartsWith("application/json", json.ContentType);

            var list = (List<object>)ContentTestSupport.Json(ContentTestSupport.Get(h.Server, root));
            Assert.Equal(2, list.Count);
            Assert.Equal("sub/img.png", JsonPath.Get(list[0], "name"));
            Assert.Equal(LocalAssets.Sha256Hex(Encoding.ASCII.GetBytes("0123456789")), JsonPath.Get(list[0], "hash"));

            var hash = ContentTestSupport.JsonObject(ContentTestSupport.Get(h.Server, root + "/sub/img.png/metadata/hash"));
            Assert.Equal("84d89877f0d4041efb6bf91a16f0248f2fd573e6af05c19f96bedb9f882f7882", hash["hash"]);
            Assert.Equal(10L, ContentTestSupport.JsonObject(ContentTestSupport.Get(h.Server, root + "/sub/img.png/metadata"))["size"]);
            Assert.Equal("http://127.0.0.1:51780/assets/" + ContentTestSupport.ClientId + "/sub/img.png",
                         ContentTestSupport.Get(h.Server, root + "/sub/img.png/url").BodyText);

            var part = ContentTestSupport.Get(h.Server, root + "/sub/img.png", range: "bytes=2-4");
            Assert.Equal(206, part.StatusCode);
            Assert.Equal("234", part.BodyText);
            Assert.Equal("bytes 2-4/10", part.Headers.Get("Content-Range"));
            Assert.Equal("789", ContentTestSupport.Get(h.Server, root + "/sub/img.png", range: "bytes=-3").BodyText);
            Assert.Equal("89", ContentTestSupport.Get(h.Server, root + "/sub/img.png", range: "bytes=8-").BodyText);
            Assert.Equal(416, ContentTestSupport.Get(h.Server, root + "/sub/img.png", range: "bytes=20-30").StatusCode);
            Assert.Equal(200, ContentTestSupport.Get(h.Server, root + "/sub/img.png", range: "items=1-2").StatusCode);

            Assert.Equal(404, ContentTestSupport.Get(h.Server, root + "/..%2Fsettings.json").StatusCode);
            Assert.Equal(404, ContentTestSupport.Get(h.Server, root + "/sub/../tech_en.json").StatusCode);
        }

        [Theory]
        [InlineData("a.png", true)]
        [InlineData("dir/a.png", true)]
        [InlineData("../a", false)]
        [InlineData("a/../b", false)]
        [InlineData("/etc/passwd", false)]
        [InlineData("c:\\x", false)]
        [InlineData("a//b", false)]
        [InlineData("", false)]
        public void AssetNamesMustStayInsideTheFolder(string name, bool safe)
        {
            Assert.Equal(safe, LocalAssets.IsSafeName(name));
        }
    }
}
