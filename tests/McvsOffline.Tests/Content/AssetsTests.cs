using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Assets;
using McvsOffline.Server.Services.Crm;
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

        private static List<object> Redeem(McvsServerHandle h, string code)
        {
            var response = ContentTestSupport.Get(h.Server, CouponPath(code));
            Assert.Equal(200, response.StatusCode);
            return (List<object>)ContentTestSupport.Json(response);
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
            var rewards = Redeem(h, "UNLOCKALL");
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
            Assert.True(h.Logs.Contains("coupon 'UNLOCKALL'"));
        }

        [Fact]
        public void CodesAreCaseInsensitiveAndAcceptANumericSuffix()
        {
            using var h = new McvsServerHandle();
            Assert.Equal(MercGroup[0], Types(Redeem(h, "unlockall"))[0]);
            // The game remembers claimed codes per profile; UNLOCKALL2 lets a player redeem again.
            Assert.Equal(MercGroup[0], Types(Redeem(h, "UnlockAll2"))[0]);
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
                var types = Types(Redeem(h, code));
                Assert.Equal(MercGroup, types.Take(MercGroup.Length));
                Assert.True(types.Count > MercGroup.Length, code);
            }
            Assert.Equal(MercGroup, Types(Redeem(h, "UNLOCKMERCS")));
            Assert.StartsWith("KillSignature_", Types(Redeem(h, "UNLOCKSIGNATURES"))[0]);
            Assert.Equal(4409, Types(Redeem(h, "UNLOCKCAMOS")).Count - MercGroup.Length);
        }

        [Fact]
        public void SettingsAddCodesAndCanDisableCoupons()
        {
            using var h = new McvsServerHandle();
            h.Server.Context.Settings.Override("assets", MiniJson.ParseObject(@"{""coupons"":{
                ""gift"":{""rewards"":[{""type"":""HardCurrency"",""quantity"":500},{""type"":""ResetProfile"",""quantity"":1},{""type"":""VersusCoins""}]},
                ""MIX"":{""groups"":[""mercs"",""nope""],""rewards"":[{""type"":""Crate_MOD_T1"",""quantity"":2}]},
                ""UNLOCKALL"":{""rewards"":[{""type"":""VersusCoins"",""quantity"":1}]}}}"));
            var gift = Redeem(h, "GIFT");
            Assert.Equal(2, gift.Count); // ResetProfile is never sent
            Assert.Equal(500L, JsonPath.Get(gift[0], "quantity"));
            Assert.Equal(1L, JsonPath.Get(gift[1], "quantity")); // default quantity
            Assert.Equal(MercGroup.Concat(new[] { "Crate_MOD_T1" }), Types(Redeem(h, "mix")));
            Assert.Equal(new[] { "VersusCoins" }, Types(Redeem(h, "UNLOCKALL"))); // settings replace built-in codes
            Assert.True(h.Logs.Contains("ResetProfile"));

            // (an override of "assets" shadows deeper keys, so replace the whole object)
            h.Server.Context.Settings.Override("assets", MiniJson.ParseObject(@"{""couponsEnabled"":false,""coupons"":{""gift"":{""rewards"":[]}}}"));
            Assert.Equal(404, ContentTestSupport.Get(h.Server, CouponPath("GIFT")).StatusCode);
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
