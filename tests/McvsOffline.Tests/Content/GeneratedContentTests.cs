using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Assets;
using McvsOffline.Server.Services.Crm;
using McvsOffline.Server.Services.Crm.Collector;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Content
{
    /// <summary>The generated server/Content files as compiled into the server (tools/content-gen output).</summary>
    public class GeneratedContentTests
    {
        private static readonly ContentProvider Provider = new ContentProvider(null, null, Log.Null);
        private readonly ITestOutputHelper output;

        public GeneratedContentTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static Dictionary<string, object> Load(string name)
        {
            Assert.True(Provider.TryReadContentText(name, out string text), name + " is not compiled in");
            return MiniJson.ParseObject(text);
        }

        private static List<Dictionary<string, object>> StoreItems()
        {
            var list = new List<Dictionary<string, object>>();
            foreach (object o in (List<object>)Load("offline_store.json")["prices"]) list.Add((Dictionary<string, object>)o);
            return list;
        }

        [Fact]
        public void EveryOfflineStoreEntryParsesInTheClient()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in StoreItems())
            {
                string name = CrmDocumentBuilder.ItemName(item);
                Assert.Null(CrmDocumentValidator.CheckStoreItem(item));
                Assert.True(names.Add(name), "duplicate " + name);
                var prices = (List<object>)JsonPath.Get(item, "billing_methods.0.price");
                Assert.Equal("VersusCoins", JsonPath.Get(prices[0], "currency")); // first price = shown/charged by default
                foreach (object p in prices)
                {
                    object value = JsonPath.Get(p, "price");
                    Assert.IsType<long>(value);
                    Assert.True((long)value > 0);
                }
                foreach (object c in (List<object>)item["category"])
                {
                    string cat = ((string)c).ToUpperInvariant();
                    Assert.DoesNotContain("#IAP", cat);     // would hide skins (GetMercsSkinsList)
                    Assert.DoesNotContain("LOCKED", cat);   // would lock mercs/skins (IsMercItemLocked)
                }
            }
            Assert.True(names.Count > 1500);
        }

        [Fact]
        public void EveryCategoryIsPurchasableWhereTheClientShowsIt()
        {
            var items = StoreItems();
            var byName = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            foreach (var i in items) byName[CrmDocumentBuilder.ItemName(i)] = i;

            // Merc detail screen: UnlockMerc_1..21, VersusCoins plus an optional HC price.
            for (int m = 1; m <= 21; m++)
            {
                Assert.True(byName.ContainsKey("UnlockMerc_" + m), "UnlockMerc_" + m);
                Assert.Equal("HardCurrency", JsonPath.Get(byName["UnlockMerc_" + m], "billing_methods.0.price.1.currency"));
            }
            int skins = 0, avatars = 0, backgrounds = 0, hub = 0, packs = 0;
            foreach (var i in items)
            {
                string name = CrmDocumentBuilder.ItemName(i);
                var cats = (List<object>)i["category"];
                bool inHub = cats.Count > 0 && ((string)cats[0]).Split(' ')[0] == "#VersusCoins";
                if (name.StartsWith("MercSkin_")) { skins++; Assert.False(inHub); }
                else if (name.StartsWith("KillSignature_Avatar_")) { avatars++; Assert.False(inHub); }
                else if (name.StartsWith("KillSignature_Background_")) { backgrounds++; Assert.False(inHub); }
                else if (name.StartsWith("UnlockMerc_")) Assert.False(inHub);
                else
                {
                    // Everything else lives in the generic #VersusCoins hub tab (GameUnifiedStoreComponent:251-268).
                    Assert.True(inHub, name);
                    hub++;
                }
                if (i.ContainsKey("group"))
                {
                    packs++;
                    Assert.StartsWith("MCVSPack_", name);
                    foreach (string reserved in new[] { "UnlockMerc", "MercSkin", "KillSignature", "SoftCurrency", "HardCurrency" })
                        Assert.DoesNotContain(reserved, name);
                    var bundle = (List<object>)i["bundle"];
                    Assert.Equal(10, bundle.Count);
                    Assert.EndsWith("_Random", (string)JsonPath.Get(bundle[0], "item"));
                }
            }
            Assert.True(skins >= 39, "skins " + skins);
            Assert.Equal(1512, avatars); // 1513-1518 were never released: UNLOCKUNRELEASED only, not sold
            Assert.Equal(35, backgrounds);
            Assert.True(packs >= 40, "packs " + packs);
            foreach (string id in new[] { "MercWeapon_1_2", "WeaponCamo_1_Random", "MercAccessory_21_Random", "GearSlot_1_1",
                                          "ModTier_1_4", "KillSignature_Sound_2", "Crate_MOD_T3", "SubscriptionCard_30" })
                Assert.True(byName.ContainsKey(id), id);
            Assert.False(byName.ContainsKey("KillSignature_Sound_1")); // starting inventory
            for (int i = 1513; i <= 1518; i++) Assert.False(byName.ContainsKey("KillSignature_Avatar_" + i));
            output.WriteLine(items.Count + " offline_store entries, " + hub + " in the VersusCoins hub (" + packs + " packs)");
        }

        [Fact]
        public void UnlockGroupsAndCouponsAreConsistent()
        {
            var unlocks = Load("unlocks.json");
            var order = (List<object>)unlocks["order"];
            Assert.Equal("mercs", order[0]);
            Assert.Equal("skins", order[order.Count - 1]);
            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (object g in (List<object>)unlocks["groups"])
            {
                var group = (Dictionary<string, object>)g;
                groupIds.Add((string)group["id"]);
                long expanded = 0;
                foreach (object p in (List<object>)group["items"])
                {
                    var ids = RewardPattern.Expand((string)p);
                    expanded += ids.Count;
                    foreach (string id in ids) Assert.True(CrmDocumentValidator.IsKnownRewardId(id), id);
                }
                Assert.Equal(group["count"], expanded);
            }
            var mercGroup = new List<string>();
            foreach (object p in (List<object>)JsonPath.Get(unlocks, "groups.0.items")) mercGroup.AddRange(RewardPattern.Expand((string)p));
            Assert.Equal(AssetsTests.MercGroup, mercGroup);

            var coupons = Load("coupons.json");
            foreach (var code in (Dictionary<string, object>)coupons["codes"])
            {
                Assert.Equal(code.Key, CouponCatalog.Normalize(code.Key));
                foreach (object g in (List<object>)JsonPath.Get(code.Value, "groups")) Assert.Contains((string)g, groupIds);
            }
            // UNLOCKALL is exactly the full unlock ("order", what settings.unlockAll grants); the opt-in groups
            // (mercUnlocks, paid*, unreleased) are only reachable through their own codes.
            Assert.Equal(order, (List<object>)JsonPath.Get(coupons, "codes.UNLOCKALL.groups"));
            Assert.DoesNotContain((object)"unreleased", order);
        }

        [Fact]
        public void EconomyTargetsAndOverlayHaveTheExpectedShape()
        {
            var economy = Load("economy.json");
            foreach (string kind in new[] { "credits", "versusCoins", "xp" })
            {
                foreach (object t in (List<object>)JsonPath.Get(economy, "multipliers." + kind))
                {
                    var target = (Dictionary<string, object>)t;
                    Assert.Equal("MCVS_Meta", target["database"]);
                    Assert.NotEmpty((List<object>)target["ids"]);
                    Assert.NotEmpty((List<object>)target["fields"]);
                }
            }
            Assert.Equal("Division", JsonPath.Get(economy, "multipliers.versusCoins.0.index"));

            var overlay = Load("crm_overlay.json");
            Assert.Equal(false, JsonPath.Get(overlay, "game._ServerConfig.default.IsDevelopment"));
            Assert.Equal("1", JsonPath.Get(overlay, "game._OverrideTables.MCVS_System.GameFeatures.Id@Enabled.RedeemCoupons"));

            var manifest = Load("manifest.json");
            foreach (object f in (List<object>)manifest["files"])
                Assert.True(Provider.TryReadContent((string)JsonPath.Get(f, "name"), out _), (string)JsonPath.Get(f, "name"));
        }

        /// <summary>collector.json: the Cosmetic crate's content file (docs/design/economy-collector.md §3.4).</summary>
        [Fact]
        public void CollectorContentHasTheDesignedShape()
        {
            var collector = Load("collector.json");
            Assert.Equal(new[] { "format", "crate", "rows", "avatars", "backgrounds", "sounds", "camos", "jerseys", "season", "levels", "summary" },
                         collector.Keys);
            Assert.Equal(new[] { "id", "table", "locId", "image" }, ((Dictionary<string, object>)collector["crate"]).Keys);
            Assert.Equal("Crate_Event_Special", JsonPath.Get(collector, "crate.id"));
            Assert.Equal("Crate.Cosmetic_Crate", JsonPath.Get(collector, "crate.locId"));
            Assert.Equal(new[] { "base", "drawable", "unlock", "lock", "kayac", "reserved" }, ((Dictionary<string, object>)collector["avatars"]).Keys);
            Assert.Equal(new[] { "base", "drawable", "unlock" }, ((Dictionary<string, object>)collector["backgrounds"]).Keys);
            Assert.Equal(new[] { "tiers", "divisionTier", "retarget", "placement", "emblems" }, ((Dictionary<string, object>)collector["season"]).Keys);

            // The 39 rows of §2.3, ids 1..39, in category order; sounds only as bundles on random-avatar carriers (D8).
            var rows = (List<object>)collector["rows"];
            Assert.Equal(39, rows.Count);
            var categories = new List<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = (Dictionary<string, object>)rows[i];
                Assert.Equal((long)(i + 1), row["id"]);
                string category = (string)row["category"];
                if (categories.Count == 0 || categories[categories.Count - 1] != category) categories.Add(category);
                Assert.NotEqual("Sound", row["subtype"]);
                if (row.ContainsKey("bundle"))
                {
                    Assert.Equal("sounds", category);
                    Assert.Equal(new object[] { "KillSignature", "Avatar", "Random" }, new[] { row["type"], row["subtype"], row["subtype2"] });
                    Assert.Equal("KillSignature_Sound_" + row["item"], row["bundle"]);
                }
            }
            Assert.Equal(new[] { "avatars", "backgrounds", "sounds", "skins", "hiddenJerseys", "agents", "camos", "jerseys", "exhausted" },
                         categories);
            Assert.Equal("VersusCoins", JsonPath.Get(collector, "rows.38.type"));
            Assert.Equal("Tiers4", JsonPath.Get(collector, "rows.38.subtype"));

            Assert.Equal(21, ((Dictionary<string, object>)collector["camos"]).Count);
            Assert.Equal(7, ((List<object>)JsonPath.Get(collector, "season.tiers")).Count);
            Assert.Equal(17, ((List<object>)JsonPath.Get(collector, "season.divisionTier")).Count);
            Assert.Equal(12, ((List<object>)JsonPath.Get(collector, "season.emblems")).Count);
            Assert.Equal("2-50", collector["levels"]);
            Assert.Equal(596L, JsonPath.Get(collector, "summary.baseItems"));
            Assert.Equal(665L, JsonPath.Get(collector, "summary.baseTier"));
            Assert.Equal(9365L, JsonPath.Get(collector, "summary.paidPairs"));
            Assert.Equal(5624L, JsonPath.Get(collector, "summary.vipOnlyPairs"));
            Assert.Equal(1064L, JsonPath.Get(collector, "summary.kayacAvatars"));

            bool listed = false;
            foreach (object f in (List<object>)Load("manifest.json")["files"])
                if ((string)JsonPath.Get(f, "name") == "collector.json") { listed = true; Assert.Equal(39L, JsonPath.Get(f, "entries")); }
            Assert.True(listed, "manifest.json does not list collector.json");
        }

        /// <summary>The liveops.json collector block hands out the collector's crate from a season start on (§3.2).</summary>
        [Fact]
        public void LiveOpsCollectorBlockHandsOutTheCollectorCrate()
        {
            var liveops = Load("liveops.json");
            var block = (Dictionary<string, object>)liveops["collector"];
            Assert.Equal(new[] { "since", "dailyBonus", "weeklyMilestones", "weeklyFinal" }, block.Keys);

            string since = (string)block["since"];
            DateTime start = DateTime.ParseExact(since, "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture,
                                                 System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            Assert.Equal(DayOfWeek.Monday, start.DayOfWeek);
            Assert.Equal(TimeSpan.Zero, start.TimeOfDay);
            Assert.Equal(0, (start - new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks % TimeSpan.FromDays(7).Ticks);
            // CollectorGuard.ShippedSince is compiled in (§2.12): the guard starts there even without this block.
            Assert.Equal(CollectorGuard.ShippedSince, start);

            var milestones = (List<object>)block["weeklyMilestones"];
            Assert.Equal(((List<object>)JsonPath.Get(liveops, "weeklyEvents.milestoneRewards")).Count, milestones.Count);
            var lists = new List<object> { block["dailyBonus"], block["weeklyFinal"] };
            lists.AddRange(milestones);
            string crate = (string)JsonPath.Get(Load("collector.json"), "crate.id");
            int crates = 0;
            foreach (object list in lists)
            {
                // A list may be empty: that part gets no crate (the content-only fallback of §8).
                foreach (object reward in (List<object>)list)
                {
                    string item = (string)JsonPath.Get(reward, "item");
                    Assert.True((long)JsonPath.Get(reward, "quantity") > 0);
                    if (item.StartsWith("Crate_", StringComparison.Ordinal))
                    {
                        Assert.Equal(crate, item);
                        crates++;
                    }
                }
            }
            Assert.True(crates > 0, "the liveops.json collector block hands out no " + crate);
        }

        [Fact]
        public void CommittedContentIsUpToDateWithTheGenerator()
        {
            string decoded = ContentTestSupport.DecodedDataPack;
            if (decoded == null || !HasPython())
            {
                output.WriteLine("no decoded data pack or no python3: skipped");
                return;
            }
            var result = RunPython("tools/content-gen/content_gen.py", "--decoded", decoded, "--check");
            output.WriteLine(result.Item2);
            Assert.True(result.Item1 == 0, "server/Content is stale; run python3 -I tools/content-gen/content_gen.py\n" + result.Item2);
        }

        [Fact]
        public void GeneratorUnitTestsPass()
        {
            if (!HasPython())
            {
                output.WriteLine("no python3: skipped");
                return;
            }
            var result = RunPython("tools/content-gen/test_content_gen.py");
            output.WriteLine(result.Item2);
            Assert.True(result.Item1 == 0, result.Item2);
        }

        private static bool HasPython()
        {
            try
            {
                return RunProcess("python3", "--version").Item1 == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Tuple<int, string> RunPython(params string[] args)
        {
            var all = new List<string> { "-I" };
            for (int i = 0; i < args.Length; i++) all.Add(i == 0 ? Path.Combine(ContentTestSupport.RepoRoot, args[i]) : args[i]);
            return RunProcess("python3", all.ToArray());
        }

        private static Tuple<int, string> RunProcess(string file, params string[] args)
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = ContentTestSupport.RepoRoot,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120000);
            return Tuple.Create(p.ExitCode, stdout + stderr);
        }
    }
}
