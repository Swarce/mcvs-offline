using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm;
using McvsOffline.Server.Services.Crm.Collector;
using McvsOffline.Server.Services.Social.LiveOps;
using McvsOffline.Tests.Content;
using McvsOffline.Tests.Golden;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Conformance
{
    /// <summary>
    /// The Cosmetic crate against the REAL client code (docs/design/economy-collector.md §6 "WP-F", tests 1-8): every tree
    /// the server can emit, parsed by <c>StandardCrm.Data</c>, turned into changers by <c>CRMComponent.GetChanger</c> and
    /// applied by <c>Database.Table.ApplyCRMTableChanges</c> to the real MCVS_Meta tables (<see cref="RealTables"/>), then
    /// read back by the game's own typed tables (<c>CratesSpecsCollection</c>, <c>CrateRewards</c>,
    /// <c>SignatureItemDataCollection</c>), its reward factory (<c>RewardHelper.CreateRewardItem</c>, as the crate preview
    /// calls it) and its draw (<c>GenerateRewards</c>, <c>PickRandomWeightedItem</c>), on the store-patched Game.dll the
    /// collector requires (D11). Also the shape of the agent list the game's Templates writer produces (WP-B's fixture) and
    /// the cost of parsing and applying the documents. Skipped without MCVS_REFS and the data pack, mandatory with
    /// MCVS_REQUIRE_REAL_CLIENT=1. The class runs alone (<see cref="CollectorTimingCollection"/>): test 8 measures.
    /// </summary>
    [Collection(CollectorTimingCollection.Name)]
    public class CollectorConformanceTests
    {
        /// <summary>A Tuesday of the first season with the collector active (the shipped since is Monday 2026-11-09).</summary>
        private static readonly DateTime ActiveDay = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

        private const string CrateId = CollectorGuard.CrateId;
        private const string CrateTable = CollectorGuard.CrateTable;
        private const string LocId = CollectorGuard.LocId;
        private const string CrateImage = "Menus/assets/png/Crates/Crates/crate_Legendary_Open.png";

        private static readonly string[] Tiers = { "Starter", "Bronze", "Silver", "Gold", "Platinum", "Diamond", "Master" };

        /// <summary>§2.4: the default <c>seasonCrates</c> bands, Starter..Master.</summary>
        private static readonly int[] Bands = { 1, 1, 2, 2, 3, 3, 4 };

        /// <summary>§2.3: the RewardId of rows 1-39 (Type_Subtype_Subtype2), written out here, not read from collector.json.</summary>
        private static readonly string[] RewardIds = BuildRewardIds();

        private readonly ITestOutputHelper output;

        public CollectorConformanceTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        // ------------------------------------------------------------------------------------------ 1. CRMTableChanger

        /// <summary>
        /// Test 1: every tree the server can emit, taken from served documents (the static document, the <c>game</c>
        /// selector, the per-player documents of a fresh and an unlock-all profile, a static document with the most cells
        /// (levelUpAvatars 3, itemsPerCrate 10), the guard of a switched-off collector and the minimal document), in both
        /// trees (<c>game._OverrideTables</c> and the mirror <c>game._ServerConfig.default._OverrideTables</c>): every
        /// changer parses and applies, and every collector cell is a SET whose target cell holds the intended value.
        /// </summary>
        [Fact]
        public void EveryServedTreeAppliesToTheRealTables()
        {
            RealTables real = RealTables.TryGet(output);
            if (real == null) return;
            Assert.NotNull(real.Game.GetType("Game.McvsStoreSafety"));   // the store-patched Game.dll (D11)
            Assert.Equal(2, real.PatchNotes.Count);

            var trees = new List<Tree>();
            using (var run = new GoldenRun(ActiveDay))
            {
                string freshDump = GoldenScenarios.DumpText(GoldenScenarios.Fresh);
                string unlockAllDump = GoldenScenarios.UnlockAllDump();
                string fresh = Player(run, "fresh", freshDump);
                string unlockAll = Player(run, "unlockAll", unlockAllDump);
                CrmDocument doc = run.Context.Features.Get<CrmDocumentCache>().Get(0);
                Assert.Equal(CollectorPhase.Active, doc.CollectorPhase);
                trees.AddRange(Served(real, "static", Ok(run.Get("/configs/users/me", null)), doc.Collector.GameCells, doc.Collector.MirrorCells));
                trees.AddRange(Trees(real, "game selector", real.LoadObject(Ok(run.Get("/configs/users/me/game", null))),
                                     doc.Collector.GameCells, doc.Collector.MirrorCells));
                trees.AddRange(Served(real, "per-player fresh", Ok(run.Get("/configs/users/me", fresh)), PlayerGameCells(doc, freshDump), CollectorGuard.Cells));
                trees.AddRange(Served(real, "per-player unlock-all", Ok(run.Get("/configs/users/me", unlockAll)), PlayerGameCells(doc, unlockAllDump),
                                      CollectorGuard.Cells));
            }
            using (var run = new GoldenRun(ActiveDay))
            {
                run.Set("economy.collector.levelUpAvatars", 3L);
                run.Set("economy.collector.itemsPerCrate", 10L);
                CrmDocument doc = run.Context.Features.Get<CrmDocumentCache>().Get(0);
                Assert.Equal(CollectorPhase.Active, doc.CollectorPhase);
                Assert.Contains(doc.Collector.MirrorCells, c => c.Table == CollectorPlan.LevelsTable);
                trees.AddRange(Served(real, "static, most cells", Ok(run.Get("/configs/users/me", null)), doc.Collector.GameCells, doc.Collector.MirrorCells));
            }
            using (var run = new GoldenRun(ActiveDay))
            {
                run.Set("economy.collector.enabled", false);
                Assert.Equal(CollectorPhase.Guard, run.Context.Features.Get<CrmDocumentCache>().Get(0).CollectorPhase);
                trees.AddRange(Served(real, "guard", Ok(run.Get("/configs/users/me", null)), CollectorGuard.Cells, CollectorGuard.Cells));
            }
            trees.AddRange(Served(real, "minimal", MiniJson.Serialize(CrmService.MinimalDocument(false, true)), CollectorGuard.Cells, CollectorGuard.Cells));

            Assert.Equal(14, trees.Count);
            foreach (Tree tree in trees)
            {
                int applied = CheckTree(real, tree);
                output.WriteLine(tree.Name + ": " + tree.Cells.Count + " collector cells, " + applied + " MCVS_Meta table changers applied");
                Assert.True(tree.Cells.Count <= 600, tree.Name + ": " + tree.Cells.Count + " cells");
            }
        }

        // ------------------------------------------------------------------------------------------ 2. CratesSpecsCollection

        /// <summary>
        /// Test 2: <c>CratesSpecsCollection</c> filled from the applied LootCrates. Shipped: the crate has two reward rounds
        /// (RewardChance.2 = 100), one sub-crate and its own LocId. With the layer: one round (100/0), <c>CratesCount</c> =
        /// itemsPerCrate N, the Cosmetic crate's name; the 14 tier crates retargeted to its content table with
        /// <c>max(1, min(band × N, ⌊40 / ⌈multiplier⌉⌋))</c> sub-crates (N = 1, 3, 10; multiplier 1 and 100). The guard:
        /// the name and 100/0, nothing else.
        /// </summary>
        [Fact]
        public void CratesSpecsCollectionReadsTheCrateAndTheTierCrates()
        {
            RealTables real = RealTables.TryGet(output);
            if (real == null) return;
            CollectorContent content = CollectorPlanTests.Shipped();

            object shipped = real.Fill("Game", "Game.CratesSpecsCollection", real.NewTable(CollectorPlan.LootCratesTable));
            AssertCrate(Element(shipped, CrateId), CrateTable, 2, 1, new[] { 100f, 100f }, "menu.LegendaryPremium", "shipped");
            foreach (string crate in TierCrates())
            {
                object specs = Element(shipped, crate);
                Assert.Equal(crate, (string)GameClient.Get(specs, "ContentTableName"));
                Assert.Equal(2, (int)GameClient.Get(specs, "RewardsCount"));   // §0.3.2: the gear row on Weight.2
            }

            foreach (int n in new[] { 1, 3, 10 })
            {
                foreach (double multiplier in new[] { 1.0, 100.0 })
                {
                    string label = "N " + n + ", multiplier " + multiplier.ToString(CultureInfo.InvariantCulture);
                    string text = Built(content, new CollectorOptions { ItemsPerCrate = n }, CollectorPlanTests.Snapshot(ActiveDay, multiplier: multiplier));
                    object specs = real.Fill("Game", "Game.CratesSpecsCollection", AppliedTable(real, GameOf(real, text), CollectorPlan.LootCratesTable));
                    AssertCrate(Element(specs, CrateId), CrateTable, 1, n, new[] { 100f, 0f }, LocId, label);
                    Assert.Equal(CrateImage, (string)GameClient.Get(Element(specs, CrateId), "Image"));
                    long q = (long)Math.Ceiling(multiplier);
                    for (int tier = 0; tier < Tiers.Length; tier++)
                    {
                        long expected = Math.Max(1, Math.Min(Bands[tier] * n, 40 / q));
                        foreach (string crate in TierCrates(tier))
                        {
                            object tierSpecs = Element(specs, crate);
                            AssertCrate(tierSpecs, CrateTable, 1, (int)expected, new[] { 100f, 0f }, LocId, label + ", " + crate);
                            Assert.Equal(CrateImage, (string)GameClient.Get(tierSpecs, "Image"));
                            Assert.Equal(CrateImage, (string)GameClient.Get(tierSpecs, "ImageShop"));
                        }
                    }
                }
            }

            object guard = real.Fill("Game", "Game.CratesSpecsCollection",
                                     AppliedTable(real, GameOf(real, MiniJson.Serialize(CrmService.MinimalDocument(false, true))), CollectorPlan.LootCratesTable));
            AssertCrate(Element(guard, CrateId), CrateTable, 1, 1, new[] { 100f, 0f }, LocId, "guard");
            foreach (string crate in TierCrates())
            {
                Assert.Equal(crate, (string)GameClient.Get(Element(guard, crate), "ContentTableName"));
                Assert.Equal(2, (int)GameClient.Get(Element(guard, crate), "RewardsCount"));
            }
        }

        // ------------------------------------------------------------------------------------------ 3. CrateRewards

        /// <summary>
        /// Test 3: <c>CrateRewards</c> filled from the applied content table: the 39 rows of §2.3 with exactly their
        /// RewardIds, the carriers' bundles always given, <c>Weight.1</c> as served (static, fresh and unlock-all player),
        /// no <c>Weight.2</c>, rows 40-100 absent. Shipped: no typed row at all. The guard: one row, VersusCoins_Tiers4,
        /// weight 1. The 21 placement tables: row 1 (the only <c>Weight.1</c> row, HardCurrency) bundles the season's emblem,
        /// always given; their gear piece stays on <c>Weight.2</c> (§0.3.2).
        /// </summary>
        [Fact]
        public void CrateRewardsReadTheContentTables()
        {
            RealTables real = RealTables.TryGet(output);
            if (real == null) return;
            Assert.Empty(Elements(real.Fill("Game", "Game.CratesInventoryComponent+CrateRewards", real.NewTable(CrateTable))));

            var served = new List<(string, string, CollectorPhase)>();
            using (var run = new GoldenRun(ActiveDay))
            {
                string fresh = Player(run, "fresh", GoldenScenarios.DumpText(GoldenScenarios.Fresh));
                string unlockAll = Player(run, "unlockAll", GoldenScenarios.UnlockAllDump());
                served.Add(("static", Ok(run.Get("/configs/users/me", null)), CollectorPhase.Active));
                served.Add(("per-player fresh", Ok(run.Get("/configs/users/me", fresh)), CollectorPhase.Active));
                served.Add(("per-player unlock-all", Ok(run.Get("/configs/users/me", unlockAll)), CollectorPhase.Active));
            }
            served.Add(("minimal", MiniJson.Serialize(CrmService.MinimalDocument(false, true)), CollectorPhase.Guard));

            foreach (var (name, text, phase) in served)
            {
                object game = GameOf(real, text);
                Dictionary<string, object> crate = ElementsById(real.Fill("Game", "Game.CratesInventoryComponent+CrateRewards", AppliedTable(real, game, CrateTable)));
                Dictionary<string, object> weights = ServedCells(text, CrateTable, "Id@Weight#1");
                if (phase == CollectorPhase.Guard)
                {
                    object row = Assert.Single(crate.Values);
                    Assert.Equal("1", (string)GameClient.Get(row, "Id"));
                    Assert.Equal("VersusCoins_Tiers4", (string)GameClient.Get(row, "RewardId"));
                    Assert.Equal(1f, Weight(row, 0));
                    Assert.Equal(0f, Weight(row, 1));
                    continue;
                }
                Assert.Equal(Enumerable.Range(1, 39).Select(i => i.ToString(CultureInfo.InvariantCulture)), crate.Keys.OrderBy(int.Parse));
                for (int id = 1; id <= 39; id++)
                {
                    object row = crate[id.ToString(CultureInfo.InvariantCulture)];
                    Assert.Equal(RewardIds[id - 1], (string)GameClient.Get(row, "RewardId"));
                    bool carrier = id >= 4 && id <= 7;
                    Assert.Equal(carrier ? "KillSignature_Sound_" + (id - 2) : null, (string)GameClient.Get(row, "BundledRewards"));
                    Assert.Equal(carrier, (bool)GameClient.Get(row, "AlwaysGiveBundledRewards"));
                    Assert.Equal(0, (int)GameClient.Get(row, "FixedQuantity"));
                    Assert.Equal((float)(long)weights[id.ToString(CultureInfo.InvariantCulture)], Weight(row, 0));
                    Assert.Equal(0f, Weight(row, 1));
                }
                Assert.Contains(crate.Values, r => Weight(r, 0) > 0);
                if (name == "per-player unlock-all")
                    Assert.Equal(new[] { "1" }, crate.Where(kv => Weight(kv.Value, 0) > 0).Select(kv => kv.Key));   // §5.1

                // §2.5: ranks 1-3. The season from Monday 2026-11-09 follows the one numbered V (273): 1st ★★★ 276, 2nd 275, 3rd 274.
                int[] emblems = name == "static" ? new[] { 276, 275, 274 } : null;
                for (int tier = 0; tier < Tiers.Length; tier++)
                {
                    string[] places = { "FirstPlace", "SecondPlace", "ThirdPlace" };
                    for (int place = 0; place < 3; place++)
                    {
                        string table = "Crate_" + Tiers[tier] + "_" + places[place];
                        Dictionary<string, object> rows = ElementsById(real.Fill("Game", "Game.CratesInventoryComponent+CrateRewards", AppliedTable(real, game, table)));
                        object first = rows["1"];
                        string bundle = (string)GameClient.Get(first, "BundledRewards");
                        Assert.Equal("KillSignature_Avatar_" + ServedCells(text, table, "Id@BundledRewards")["1"].ToString().Split('_').Last(), bundle);
                        if (emblems != null) Assert.Equal("KillSignature_Avatar_" + emblems[place], bundle);
                        Assert.True((bool)GameClient.Get(first, "AlwaysGiveBundledRewards"), table);
                        Assert.StartsWith("HardCurrency", (string)GameClient.Get(first, "RewardId"));
                        // Round 0 always draws row 1, so its bundle follows unconditionally; the gear piece stays on round 1.
                        Assert.Equal(new[] { "1" }, rows.Where(kv => Weight(kv.Value, 0) > 0).Select(kv => kv.Key));
                        Assert.Equal(0f, Weight(first, 1));
                        Assert.Contains(rows.Values, r => Weight(r, 1) > 0 && ((string)GameClient.Get(r, "RewardId")).StartsWith("Gear_Random_", StringComparison.Ordinal));
                        Assert.NotNull(RealTables.Row(real.NewTable(CollectorPlan.AvatarsTable), "ID", bundle.Substring("KillSignature_Avatar_".Length)));
                    }
                }
                output.WriteLine(name + ": " + crate.Count + " rows, drawable " + string.Join(",", crate.Where(kv => Weight(kv.Value, 0) > 0).Select(kv => kv.Key)));
            }
        }

        // ------------------------------------------------------------------------------------------ 4. reward items, preview safety

        /// <summary>
        /// Test 4: <c>RewardHelper.CreateRewardItem(RewardId, 1, source)</c> (what <c>GetPreview</c> calls for every
        /// drawable row) gives the expected class for every row and for the four sound bundles; on the patched Game.dll a
        /// bundled sound and a random avatar that found nothing (Subtype2 null) answer <c>IsAvailable</c> instead of
        /// throwing. Preview safety (§2.3.1): every row with <c>Weight.1 &gt; 0</c> in every emitted tree is in the safe set and
        /// a KillSignature row has an image (a Sound row would not: pinned). Every image and loc id the crate's display uses
        /// is in the data pack, and the exhausted row's VersusCoins tier pays 70.
        /// </summary>
        [Fact]
        public void RewardItemsPerRowAndBundleAreSafeToPreview()
        {
            RealTables real = RealTables.TryGet(output);
            if (real == null) return;
            Type helper = real.Type("Game", "Game.RewardHelper");
            Type sourceType = real.Type("Game", "Game.RewardItem+TrackingSource");
            MethodInfo create = helper.GetMethod("CreateRewardItem", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(int), sourceType }, null);
            object source = Activator.CreateInstance(sourceType);
            object Create(string reward) => create.Invoke(null, new[] { reward, 1, source }) ?? throw new InvalidOperationException(reward + ": no reward item");

            for (int id = 1; id <= 39; id++)
            {
                string rewardId = RewardIds[id - 1];
                object item = Create(rewardId);
                string type = rewardId.Split('_')[0];
                Assert.Equal(ExpectedClass(type), item.GetType().FullName);
                switch (type)
                {
                    case "KillSignature":
                        Assert.True((bool)GameClient.Get(item, "IsRandom"), rewardId);
                        break;
                    case "MercAccessory":
                        Assert.True((bool)GameClient.Get(item, "IsRandomMerc"), rewardId);
                        Assert.Equal(id == 39, (bool)GameClient.Get(item, "IsRandomAccessory"));
                        break;
                }
            }

            // The bundles: sounds, claimed through IsAvailable on the reveal (§2.13); the store patch makes it answer.
            Type profileType = real.Type("Game", "Game.PlayerProfileComponent");
            object profile = RuntimeHelpers.GetUninitializedObject(profileType);
            var sounds = new List<string> { "1" };
            profileType.GetProperty("KillSignatureSounds", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(profile, sounds);
            MethodInfo isAvailable = real.Type("Game", "Game.KillSignatureRewardItem").GetMethod("IsAvailable", new[] { profileType });
            for (int n = 2; n <= 5; n++)
            {
                object sound = Create("KillSignature_Sound_" + n);
                Assert.Equal("Game.KillSignatureRewardItem", sound.GetType().FullName);
                Assert.Equal("Sound", (string)GameClient.Get(sound, "Subtype"));
                Assert.Equal(n.ToString(CultureInfo.InvariantCulture), (string)GameClient.Get(sound, "Subtype2"));
                Assert.False((bool)GameClient.Get(sound, "IsRandom"));
                Assert.True((bool)isAvailable.Invoke(sound, new[] { profile }));
                sounds.Add(n.ToString(CultureInfo.InvariantCulture));
                Assert.False((bool)isAvailable.Invoke(sound, new[] { profile }));
                // The hazard D8 avoids: a sound has no image, so a weighted sound row would throw in GetPreview.
                Assert.Null(GameClient.Call(sound, "GetImage", new object[] { null }));
            }
            object nothingLeft = Create("KillSignature_Avatar_Random");
            GameClient.Set(nothingLeft, "Subtype2", null);   // what OnPrepareToClaim leaves when no avatar is left
            Assert.False((bool)isAvailable.Invoke(nothingLeft, new[] { profile }));

            // Preview safety on every emitted tree.
            var texts = new List<(string, string)>();
            using (var run = new GoldenRun(ActiveDay))
            {
                texts.Add(("static", Ok(run.Get("/configs/users/me", null))));
                texts.Add(("per-player fresh", Ok(run.Get("/configs/users/me", Player(run, "fresh", GoldenScenarios.DumpText(GoldenScenarios.Fresh))))));
                texts.Add(("per-player unlock-all", Ok(run.Get("/configs/users/me", Player(run, "unlockAll", GoldenScenarios.UnlockAllDump())))));
            }
            CollectorContent content = CollectorPlanTests.Shipped();
            var onlySounds = new CollectorOptions();
            foreach (string category in onlySounds.Weights.Keys.ToList()) onlySounds.Weights[category] = category == CollectorContent.Sounds ? 1 : 0;
            texts.Add(("carriers only", Built(content, onlySounds, CollectorPlanTests.Snapshot(ActiveDay))));
            texts.Add(("minimal", MiniJson.Serialize(CrmService.MinimalDocument(false, true))));
            int checkedRows = 0;
            foreach (var (name, text) in texts)
            {
                object game = GameOf(real, text);
                foreach (object tree in new[] { game, MirrorOf(game) })
                {
                    Dictionary<string, object> rows = ElementsById(real.Fill("Game", "Game.CratesInventoryComponent+CrateRewards", AppliedTable(real, tree, CrateTable)));
                    Assert.Contains(rows.Values, r => Weight(r, 0) > 0);
                    foreach (object row in rows.Values.Where(r => Weight(r, 0) > 0))
                    {
                        string type = (string)GameClient.Get(row, "Type");
                        string subtype = (string)GameClient.Get(row, "Subtype");
                        string subtype2 = (string)GameClient.Get(row, "Subtype2");
                        Assert.True(PreviewSafe(type, subtype, subtype2), name + ": row " + GameClient.Get(row, "Id") + " " + GameClient.Get(row, "RewardId"));
                        Assert.Null(CollectorContent.RowProblem(type, subtype, subtype2, (string)GameClient.Get(row, "BundledRewards")));
                        object item = Create((string)GameClient.Get(row, "RewardId"));
                        if (type == "KillSignature")
                        {
                            string image = (string)GameClient.Call(item, "GetImage", new object[] { null });
                            Assert.False(string.IsNullOrEmpty(image), name + ": row " + GameClient.Get(row, "Id") + " has no image");
                            Assert.True(real.Exists(image), image);
                            Assert.True(LocExists(real, (string)GameClient.Call(item, "GetName", new object[] { null })));
                        }
                        checkedRows++;
                    }
                }
            }
            output.WriteLine(checkedRows + " drawable rows checked in " + texts.Count * 2 + " trees");

            // What the crate's display references: its name and image, the content line, the tier crates' image.
            Assert.True(LocExists(real, LocId), LocId);
            Assert.True(LocExists(real, "menu.ShopCrateContent"));
            Assert.True(real.Exists(CrateImage), CrateImage);
            Assert.Equal(LocId, content.LocId);
            Assert.Equal(CrateImage, content.Image);

            // Row 1 (exhausted): the VersusCoins type route, and its tier pays 70 VersusCoins (VersusCoinsTiers).
            object tiers = real.Fill("Game", "Game.CurrencyTiersCollection", real.NewTable("VersusCoinsTiers"));
            object tiers4 = Element(tiers, "Tiers4");
            Assert.Equal(70, (int)GameClient.Get(tiers4, "QuantityMin"));
            Assert.Equal(70, (int)GameClient.Get(tiers4, "QuantityMax"));
        }

        // ------------------------------------------------------------------------------------------ 5. the empty-table hazard

        /// <summary>
        /// Test 5 (the reason for D9): the shipped content table has no typed row, and <c>GenerateRewards</c> on an empty
        /// list throws <c>ArgumentOutOfRangeException</c> (round 0 reads <c>rewards[0]</c>; no LastChance fallback). On the
        /// guard's table <c>PickRandomWeightedItem</c> returns the VersusCoins row for round 0 and nothing for round 1.
        /// </summary>
        [Fact]
        public void AnEmptyContentTableThrowsAndTheGuardDraws()
        {
            RealTables real = RealTables.TryGet(output);
            if (real == null) return;
            Type inventoryType = real.Type("Game", "Game.CratesInventoryComponent");
            Type rewardType = real.Type("Game", "Game.CratesInventoryComponent+CrateReward");
            Type specsType = real.Type("Game", "Game.CrateSpecs");
            Type randomType = real.Type("Game", "Game.GameRandom");
            Type sourceType = real.Type("Game", "Game.RewardItem+TrackingSource");
            Type profileType = real.Type("Game", "Game.PlayerProfileComponent");
            Type listType = typeof(List<>).MakeGenericType(rewardType);
            object component = RuntimeHelpers.GetUninitializedObject(inventoryType);
            object rng = Activator.CreateInstance(randomType, 7);
            object specs = Element(real.Fill("Game", "Game.CratesSpecsCollection", real.NewTable(CollectorPlan.LootCratesTable)), CrateId);

            object shipped = real.Fill("Game", "Game.CratesInventoryComponent+CrateRewards", real.NewTable(CrateTable));
            IList empty = (IList)Activator.CreateInstance(listType);
            foreach (object row in Elements(shipped)) empty.Add(row);
            Assert.Equal(0, empty.Count);
            MethodInfo generate = inventoryType.GetMethod("GenerateRewards", new[] { specsType, listType, profileType, randomType, sourceType });
            var thrown = Assert.Throws<TargetInvocationException>(() => generate.Invoke(component, new[] { specs, empty, null, rng, Activator.CreateInstance(sourceType) }));
            Assert.IsType<ArgumentOutOfRangeException>(thrown.InnerException);

            object guardTable = AppliedTable(real, GameOf(real, MiniJson.Serialize(CrmService.MinimalDocument(false, true))), CrateTable);
            IList pool = (IList)Activator.CreateInstance(listType);
            foreach (object row in Elements(real.Fill("Game", "Game.CratesInventoryComponent+CrateRewards", guardTable))) pool.Add(row);
            MethodInfo pick = inventoryType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                                           .Single(m => m.Name == "PickRandomWeightedItem" && m.GetParameters().Length == 3);
            object first = pick.Invoke(component, new[] { pool, rng, 0 });
            Assert.NotNull(first);
            Assert.Equal("VersusCoins_Tiers4", (string)GameClient.Get(first, "RewardId"));
            Assert.Null(pick.Invoke(component, new[] { pool, rng, 1 }));
        }

        // ------------------------------------------------------------------------------------------ 6. SignatureItemDataCollection

        /// <summary>
        /// Test 6: <c>SignatureItemDataCollection</c> filled from the applied signature tables. Game layer: 369 unlockable
        /// avatars (the 230 shipped non-Kayac ones plus the 139 <c>true</c> cells), none of the 1,066 Kayac ids, and 24
        /// backgrounds; mirror (no <c>true</c> cells): 230 avatars, none Kayac. The counts are sets of ids, so they do not
        /// depend on itemsPerCrate (the design computed them at 3, the default is 5).
        /// </summary>
        [Fact]
        public void SignatureItemDataCollectionReadsTheUnlockableSets()
        {
            RealTables real = RealTables.TryGet(output);
            if (real == null) return;
            CollectorContent content = CollectorPlanTests.Shipped();
            var kayac = new HashSet<string>(content.AvatarsKayac.Select(a => a.ToString(CultureInfo.InvariantCulture)));
            Assert.Equal(1066, kayac.Count);

            List<string> Unlockable(object game, string table) =>
                Elements(real.Fill("Game", "Game.PlayerProfileComponent+SignatureItemDataCollection", game == null ? real.NewTable(table) : AppliedTable(real, game, table)))
                    .Where(x => (bool)GameClient.Get(x, "unlockable")).Select(x => (string)GameClient.Get(x, "Id")).ToList();

            List<string> shippedAvatars = Unlockable(null, CollectorPlan.AvatarsTable);
            output.WriteLine("shipped: " + shippedAvatars.Count + " unlockable avatars, " + shippedAvatars.Count(kayac.Contains) + " of them Kayac; "
                             + Unlockable(null, CollectorPlan.BackgroundsTable).Count + " backgrounds");
            Assert.Equal(75, shippedAvatars.Count(kayac.Contains));
            Assert.Equal(18, Unlockable(null, CollectorPlan.BackgroundsTable).Count);

            foreach (int n in new[] { 3, 5 })
            {
                object game = GameOf(real, Built(content, new CollectorOptions { ItemsPerCrate = n }, CollectorPlanTests.Snapshot(ActiveDay)));
                List<string> avatars = Unlockable(game, CollectorPlan.AvatarsTable);
                Assert.Equal(369, avatars.Count);
                Assert.Empty(avatars.Where(kayac.Contains));
                Assert.Equal(shippedAvatars.Count - 75 + 139, avatars.Count);
                Assert.Equal(24, Unlockable(game, CollectorPlan.BackgroundsTable).Count);

                List<string> mirror = Unlockable(MirrorOf(game), CollectorPlan.AvatarsTable);
                Assert.Equal(230, mirror.Count);
                Assert.Empty(mirror.Where(kayac.Contains));
                Assert.Equal(18, Unlockable(MirrorOf(game), CollectorPlan.BackgroundsTable).Count);
            }
        }

        // ------------------------------------------------------------------------------------------ 7. the agent element shape

        /// <summary>
        /// Test 7: a <c>List&lt;Merc.ShortDef&gt;</c> (agent 4 unlocked with skins 1 and 3, camos 1 and 12, jersey 112; agent 17
        /// as constructed) written by the game's own Templates writer under a <c>/Profile</c> node, as the profile dump holds
        /// it (TemplateActivator.CreateTemplate, the WP-B probe technique): the output matches WP-B's
        /// <see cref="PlayerInventoryTests.WriterMercsFixture"/> key for key and reads to the same agent states.
        /// </summary>
        [Fact]
        public void TheGameWritesTheAgentListAsTheFixture()
        {
            RealTables real = RealTables.TryGet(output);
            if (real == null) return;
            Type shortDef = real.Type("Game", "Game.Merc+ShortDef");
            IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(shortDef));
            object unlocked = Activator.CreateInstance(shortDef, "4");
            GameClient.Set(unlocked, "Locked", false);
            GameClient.Set(unlocked, "Skins", new List<string> { "1", "3" });
            GameClient.Set(unlocked, "Camos", new List<string> { "1", "12" });
            GameClient.Set(unlocked, "Accessories", new List<string> { "112" });
            list.Add(unlocked);
            list.Add(Activator.CreateInstance(shortDef, "17"));

            Assembly templates = real.Assembly("Gameloft.Templates");
            Type activator = templates.GetType("Gameloft.Templates.TemplateActivator", true);
            Type node = templates.GetType("Gameloft.Templates.TNode", true);
            Type tobject = templates.GetType("Gameloft.Templates.TObject", true);
            Type tname = templates.GetType("Gameloft.Templates.TName", true);
            MethodInfo createTemplate = activator.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "CreateTemplate" && m.GetParameters().Length == 2);
            object mercs = createTemplate.Invoke(null, new object[] { list, null });
            node.GetProperty("Name").SetValue(mercs, tname.GetMethod("PropertyName", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { "Mercs" }));
            object profile = Activator.CreateInstance(tobject);
            node.GetProperty("Name").SetValue(profile, tname.GetMethod("ChildName", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { "Profile" }));
            node.GetProperty("DeclaredType").SetValue(profile, "PlayerProfileComponent");
            MethodInfo add = node.GetMethod("Add", new[] { node, typeof(bool) });
            add.Invoke(profile, new[] { mercs, (object)true });
            object root = Activator.CreateInstance(tobject);
            add.Invoke(root, new[] { profile, (object)true });
            string written = (string)tobject.GetMethod("ToString", new[] { typeof(string) }).Invoke(root, new object[] { "j" });
            output.WriteLine(written);

            object fixtureTree = MiniJson.Parse(PlayerInventoryTests.WriterMercsFixture);
            object writtenTree = MiniJson.Parse(written);
            Assert.Equal(KeyPaths(fixtureTree), KeyPaths(writtenTree));
            Assert.True(JsonValue.DeepEquals(fixtureTree, writtenTree), "the writer's agent list differs from WriterMercsFixture");
            output.WriteLine(string.Equals(Compact(written), Compact(PlayerInventoryTests.WriterMercsFixture), StringComparison.Ordinal)
                                 ? "byte-identical to the fixture (whitespace aside)" : "same keys and values, member order differs");

            PlayerInventory fromWriter = PlayerInventory.Scan(written, true);
            PlayerInventory fromFixture = PlayerInventory.Scan(PlayerInventoryTests.WriterMercsFixture, true);
            Assert.True(fromWriter.MercsKnown, string.Join("; ", fromWriter.Problems.Values));
            Assert.Equal(fromFixture.Mercs.Keys.OrderBy(k => k), fromWriter.Mercs.Keys.OrderBy(k => k));
            foreach (int id in fromFixture.Mercs.Keys)
            {
                MercState a = fromFixture.Mercs[id], b = fromWriter.Mercs[id];
                Assert.Equal(a.Unlocked, b.Unlocked);
                Assert.Equal(a.Skins.OrderBy(x => x), b.Skins.OrderBy(x => x));
                Assert.Equal(a.Camos.OrderBy(x => x), b.Camos.OrderBy(x => x));
                Assert.Equal(a.Accessories.OrderBy(x => x), b.Accessories.OrderBy(x => x));
            }
            Assert.True(fromWriter.Mercs[4].Unlocked);
            Assert.False(fromWriter.Mercs[17].Unlocked);
        }

        // ------------------------------------------------------------------------------------------ 8. timing

        /// <summary>
        /// Test 8: the cost on net8 (the design's device estimates scale it by 3-5 for Mono/ARM, §2.10). Parse
        /// (<c>StandardCrm.Data</c>, which <c>JObject.Parse</c>s the text) of the per-player documents, fresh and unlock-all,
        /// with the layer and without (collector off: the guard), and the apply of each tree; 1 cold + 5 warm runs each.
        /// Logged per return to the menu (6 parses + 2 game-layer applies) and per match (2 mirror applies); asserted: warm
        /// apply under 60 ms per tree, at most 600 collector cells per tree, per-player text growth at most 15 %.
        /// </summary>
        [Fact]
        public void ParsingAndApplyingStayCheap()
        {
            RealTables real = RealTables.TryGet(output);
            if (real == null) return;
            var with = new Dictionary<string, string>();
            var without = new Dictionary<string, string>();
            var cells = new Dictionary<string, int>();
            foreach (bool enabled in new[] { true, false })
            {
                using var run = new GoldenRun(ActiveDay);
                run.Set("economy.collector.enabled", enabled);
                Dictionary<string, string> texts = enabled ? with : without;
                foreach (string dump in new[] { GoldenScenarios.Fresh, GoldenScenarios.UnlockAll })
                {
                    string dumpText = dump == GoldenScenarios.Fresh ? GoldenScenarios.DumpText(dump) : GoldenScenarios.UnlockAllDump();
                    texts[dump] = Ok(run.Get("/configs/users/me", Player(run, dump, dumpText)));
                    if (enabled) cells[dump] = PlayerGameCells(run.Context.Features.Get<CrmDocumentCache>().Get(0), dumpText).Count;
                }
                if (!enabled) continue;
                CrmDocument doc = run.Context.Features.Get<CrmDocumentCache>().Get(0);
                with["static"] = Ok(run.Get("/configs/users/me", null));
                cells["static"] = doc.Collector.GameCells.Count;
                cells["static mirror"] = doc.Collector.MirrorCells.Count;
            }
            cells["per-player mirror"] = CollectorGuard.Cells.Count;

            Type crmData = real.Type("Gameloft.OnlinePlatform", "Gameloft.OnlinePlatform.StandardCrm+Data");
            var parse = new Dictionary<string, double>();
            foreach (var (name, texts) in new[] { ("with", with), ("without", without) })
                foreach (var kv in texts.Where(t => t.Key != "static"))
                {
                    double[] runs = Time(() => Activator.CreateInstance(crmData, kv.Value));
                    parse[name + " " + kv.Key] = runs.Skip(1).Min();
                    output.WriteLine("parse " + kv.Key + " " + name + " the layer (" + kv.Value.Length + " chars): cold " + Ms(runs[0]) + ", warm " + Ms(runs.Skip(1).Min()));
                }

            var apply = new Dictionary<string, double>();
            foreach (var (name, tree) in new[]
            {
                ("static game", GameOf(real, with["static"])), ("static mirror", MirrorOf(GameOf(real, with["static"]))),
                ("fresh game", GameOf(real, with[GoldenScenarios.Fresh])), ("unlock-all game", GameOf(real, with[GoldenScenarios.UnlockAll])),
                ("per-player mirror", MirrorOf(GameOf(real, with[GoldenScenarios.Fresh]))),
                ("fresh game without the layer", GameOf(real, without[GoldenScenarios.Fresh])),
            })
            {
                List<object> changers = real.TableChangers(real.Changer(tree)).Values.Where(c => real.HasTable((string)GameClient.Get(c, "TableName"))).ToList();
                double ApplyOnce()
                {
                    List<object> tables = changers.Select(c => real.NewTable((string)GameClient.Get(c, "TableName"))).ToList();
                    var watch = Stopwatch.StartNew();
                    for (int t = 0; t < tables.Count; t++) GameClient.Call(tables[t], "ApplyCRMTableChanges", changers[t]);
                    return watch.Elapsed.TotalMilliseconds;
                }
                double cold = ApplyOnce();
                double warm = Enumerable.Range(0, 5).Select(_ => ApplyOnce()).Min();
                // A busy machine (the other test assembly runs at the same time) can slow all five warm runs: measure
                // again before concluding that the apply itself is slow.
                int rounds = 1;
                for (; warm >= 60 && rounds < 5; rounds++) warm = Math.Min(warm, Enumerable.Range(0, 5).Select(_ => ApplyOnce()).Min());
                apply[name] = warm;
                output.WriteLine("apply " + name + " (" + changers.Count + " MCVS_Meta tables): cold " + Ms(cold) + ", warm " + Ms(warm)
                                 + (rounds > 1 ? " (best of " + rounds * 5 + " warm runs)" : ""));
                Assert.True(warm < 60, name + ": warm apply " + Ms(warm));
            }

            foreach (var kv in cells)
            {
                output.WriteLine(kv.Key + ": " + kv.Value + " collector cells");
                Assert.True(kv.Value <= 600, kv.Key + ": " + kv.Value + " cells");
            }
            foreach (string dump in new[] { GoldenScenarios.Fresh, GoldenScenarios.UnlockAll })
            {
                double growth = (with[dump].Length - without[dump].Length) / (double)without[dump].Length;
                output.WriteLine("per-player " + dump + ": " + without[dump].Length + " -> " + with[dump].Length + " chars (+" + (growth * 100).ToString("F1", CultureInfo.InvariantCulture) + " %)");
                Assert.True(growth <= 0.15, dump + ": +" + (growth * 100).ToString("F1", CultureInfo.InvariantCulture) + " %");

                double menu = 6 * parse["with " + dump] + 2 * apply[dump == GoldenScenarios.Fresh ? "fresh game" : "unlock-all game"];
                double menuWithout = 6 * parse["without " + dump] + 2 * apply["fresh game without the layer"];
                output.WriteLine("per return to the menu, " + dump + " (6 parses + 2 game-layer applies): " + Ms(menu) + " (" + Ms(menuWithout)
                                 + " without the layer, +" + Ms(menu - menuWithout) + "; Mono/ARM x3-5)");
            }
            output.WriteLine("per match (2 mirror applies): " + Ms(2 * apply["static mirror"]) + " (static mirror; the per-player mirror "
                             + Ms(2 * apply["per-player mirror"]) + ")");
        }

        // ------------------------------------------------------------------------------------------ 9. one crate per milestone

        /// <summary>
        /// Test 9 (§3.1, one crate per milestone): the real <c>ClaimAchievementMilestoneRequest</c> (its async state machine)
        /// sets the claim's reward list to a crate's generated items, <c>result.Rewards = crateRewardItem.RewardsGenerated</c>,
        /// a replace, not an append, and adds every other reward to that list; <c>ClaimAchievementMilestone</c> then reveals
        /// <c>result.Rewards</c> in the crate lottery whenever <c>IsCrate</c>. So a milestone holding two crates reveals only the
        /// last one, and the currencies before a crate are credited unseen; live ops never serve two crates in one milestone
        /// (LiveOpsTests.NoGeneratedMilestoneHoldsTwoCrates, LiveOpsConformanceTests).
        /// </summary>
        [Fact]
        public void AMilestoneRevealsOnlyItsLastCrate()
        {
            string refs = GameClient.RefsDirectory;
            string path = System.IO.Path.Combine(refs, "Game.dll");
            if (!System.IO.File.Exists(path))
            {
                if (GameClient.Required) throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but " + path + " is missing");
                output.WriteLine("Game.dll not found in MCVS_REFS: skipped");
                return;
            }
            using (var resolver = new Mono.Cecil.DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(refs);
                var reader = new Mono.Cecil.ReaderParameters { AssemblyResolver = resolver, InMemory = true };
                using (Mono.Cecil.ModuleDefinition game = Mono.Cecil.ModuleDefinition.ReadModule(path, reader))
                {
                    Mono.Cecil.TypeDefinition component = game.GetType("Game.TimeLimitedAchievementsComponent");
                    Assert.NotNull(component);

                    List<string> request = Calls(StateMachine(component, "ClaimAchievementMilestoneRequest"));
                    output.WriteLine("ClaimAchievementMilestoneRequest calls: " + string.Join(", ", request));
                    int replace = request.IndexOf("Game.CrateRewardItem::get_RewardsGenerated");
                    Assert.True(replace >= 0, "no CrateRewardItem.RewardsGenerated read");
                    Assert.Equal("Game.RequestRewardsResult::set_Rewards", request[replace + 1]);
                    Assert.Equal(1, request.Count(c => c == "Game.RequestRewardsResult::set_Rewards"));
                    // result.Rewards.Count == 0 (an empty crate is an error), and result.Rewards.Add(reward) for any other reward.
                    Assert.Equal("System.Collections.Generic.List`1<Game.RewardItem>::get_Count", request[replace + 3]);
                    Assert.True(Enumerable.Range(0, request.Count - 1).Any(i => request[i] == "Game.RequestRewardsResult::get_Rewards"
                                                                                && request[i + 1] == "System.Collections.Generic.List`1<Game.RewardItem>::Add"),
                                "the other rewards are not appended");

                    List<string> claim = Calls(StateMachine(component, "ClaimAchievementMilestone"));
                    Assert.Contains("Game.RequestRewardsResult::get_IsCrate", claim);
                    Assert.Contains("Game.LotteryComponent::OpenSchematicLottery", claim);
                    Assert.True(claim.IndexOf("Game.RequestRewardsResult::get_IsCrate") < claim.IndexOf("Game.LotteryComponent::OpenSchematicLottery"));
                }
            }
        }

        /// <summary>The <c>MoveNext</c> of the compiler's state machine for the async method <paramref name="name"/>.</summary>
        private static Mono.Cecil.MethodDefinition StateMachine(Mono.Cecil.TypeDefinition type, string name)
        {
            Mono.Cecil.TypeDefinition machine = type.NestedTypes.Single(t => t.Name.StartsWith("<" + name + ">", StringComparison.Ordinal));
            return machine.Methods.Single(m => m.Name == "MoveNext");
        }

        /// <summary>"DeclaringType::Name" of every call and callvirt, in IL order.</summary>
        private static List<string> Calls(Mono.Cecil.MethodDefinition method)
        {
            return method.Body.Instructions
                         .Where(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Call || i.OpCode == Mono.Cecil.Cil.OpCodes.Callvirt)
                         .Select(i => (Mono.Cecil.MethodReference)i.Operand)
                         .Select(m => m.DeclaringType.FullName + "::" + m.Name).ToList();
        }

        // ------------------------------------------------------------------------------------------ 10. the TLE screens' cost

        /// <summary>
        /// Test 10 (preview cost, device check 1): every crate object on a TLE screen runs <c>CrateRewardItem.IsAvailable</c>
        /// (<c>GetCrateRewards(...).Any(...IsAvailable)</c>: the content rows in table order until one is available) and
        /// <c>GetPreview</c> (<c>GetObject</c>, so <c>IsAvailable</c>, on every spawnable row); a random avatar row's
        /// <c>IsAvailable</c> rebuilds the 1,518-entry avatar list (<c>GetSignatureAssetsList</c>: <c>List.Contains</c> against
        /// the owned ids per entry). On the real <c>CrateRewards</c> fill of every served tree: no avatar row before the first
        /// row that is not a signature (so <c>IsAvailable</c> never builds the list, even when nothing is left) and at most one
        /// spawnable avatar row (the preview builds it at most once per crate object). Logged: the crate objects of the
        /// served daily and weekly events, the round-2 layout for comparison, and the net8 cost of one build over the real
        /// avatar ids with 50 to 1,400 owned, per weekly screen (Mono/ARM ×3-5; device check 1's target is +0.5 s).
        /// </summary>
        [Fact]
        public void ATleScreenBuildsTheAvatarListAtMostOncePerCrate()
        {
            RealTables real = RealTables.TryGet(output);
            if (real == null) return;
            var served = new List<(string Name, string Text)>();
            string weeklyText, dailyText;
            using (var run = new GoldenRun(ActiveDay))
            {
                served.Add(("static", Ok(run.Get("/configs/users/me", null))));
                served.Add(("per-player fresh", Ok(run.Get("/configs/users/me", Player(run, "fresh", GoldenScenarios.DumpText(GoldenScenarios.Fresh))))));
                served.Add(("per-player unlock-all", Ok(run.Get("/configs/users/me", Player(run, "unlockAll", GoldenScenarios.UnlockAllDump())))));
                LiveOpsSettings s = LiveOpsService.For(run.Context).Settings;
                weeklyText = Ok(run.Get("/events/" + LiveOpsSchedule.WeeklyEventId(LiveOpsSchedule.SeasonAt(s, ActiveDay)) + "?language=en", null));
                dailyText = Ok(run.Get("/events/" + LiveOpsSchedule.DailyEventId(LiveOpsSchedule.DayAt(s, ActiveDay)) + "?language=en", null));
            }
            served.Add(("minimal", MiniJson.Serialize(CrmService.MinimalDocument(false, true))));
            int weeklyCrates = CrateObjects(weeklyText), dailyCrates = CrateObjects(dailyText);
            Assert.Equal(12, weeklyCrates);                 // 4 objectives × 3 tiers; none on the final (§3.1)
            Assert.Equal(1, dailyCrates);                   // the day's first mission

            foreach (var (name, text) in served)
            {
                var (isAvailable, preview) = AvatarListBuilds(real, GameOf(real, text));
                output.WriteLine(name + ": " + isAvailable + " avatar-list builds in IsAvailable at most, " + preview + " in the preview");
                Assert.Equal(0, isAvailable);
                Assert.True(preview <= 1, name + ": " + preview + " spawnable avatar rows");
            }
            // The round-2 layout (exhausted last, avatar carriers) for comparison: what the review measured.
            var round2 = MiniJson.ParseObject(CollectorPlanTests.ShippedJson);
            var rows = (List<object>)round2["rows"];
            object exhausted = rows[0];
            rows.RemoveAt(0);
            rows.Add(exhausted);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = (Dictionary<string, object>)rows[i];
                row["id"] = (long)(i + 1);
                if ((string)row["category"] == CollectorContent.Sounds) row["subtype"] = "Avatar";
            }
            CollectorContent oldContent = CollectorContent.Parse(MiniJson.Serialize(round2), out string problem);
            Assert.True(oldContent != null, problem);
            var (oldAvailable, oldPreview) = AvatarListBuilds(real, GameOf(real, Built(oldContent, new CollectorOptions(), CollectorPlanTests.Snapshot(ActiveDay))));
            output.WriteLine("round-2 layout: " + oldAvailable + " in IsAvailable at most, " + oldPreview + " in the preview");
            Assert.Equal(5, oldAvailable);
            Assert.Equal(5, oldPreview);

            // One build on net8: GetSignatureAssetsList's loop and GetAvailableId's filter over the real avatar ids.
            object avatars = real.Fill("Game", "Game.PlayerProfileComponent+SignatureItemDataCollection",
                                       AppliedTable(real, GameOf(real, served[0].Text), CollectorPlan.AvatarsTable));
            List<SignatureEntry> entries = Elements(avatars).Select(x => new SignatureEntry { Id = (string)GameClient.Get(x, "Id"), Unlockable = (bool)GameClient.Get(x, "unlockable") }).ToList();
            Assert.True(entries.Count >= 1518, entries.Count + " avatars");
            var noNew = new List<string>();
            foreach (int owned in new[] { 50, 300, 700, 1400 })
            {
                List<string> ownedIds = entries.Select(e => e.Id).Take(owned).ToList();
                double build = Time(() =>
                {
                    foreach (SignatureEntry e in entries)
                    {
                        e.Locked = !ownedIds.Contains(e.Id);
                        e.IsNew = noNew.Contains(e.Id);
                    }
                    GC.KeepAlive(entries.Where(e => e.Locked && e.Unlockable).ToList());
                }).Skip(1).Min();
                output.WriteLine("one avatar-list build with " + owned + " owned: " + Ms(build) + "; weekly screen (" + weeklyCrates + " crate objects × "
                                 + "at most 1): " + Ms(weeklyCrates * build) + " (round-2 layout, 13 × " + (oldAvailable + oldPreview) + "): "
                                 + Ms(13 * (oldAvailable + oldPreview) * build) + "; Mono/ARM ×3-5");
            }
        }

        /// <summary>A crate entry of an event's milestones (one reward object each, whatever its quantity).</summary>
        private static int CrateObjects(string eventText)
        {
            var template = MiniJson.ParseObject((string)MiniJson.ParseObject(eventText)["_template"]);
            int n = 0;
            foreach (Dictionary<string, object> achievement in (List<object>)JsonPath.Get(template, "milestones.value"))
                foreach (Dictionary<string, object> milestone in (List<object>)achievement["milestones"])
                    foreach (Dictionary<string, object> reward in (List<object>)milestone["rewards"])
                        if ((string)reward["item"] == CrateId) n++;
            return n;
        }

        /// <summary>
        /// For the crate's <c>CrateRewards</c> as the client fills them from <paramref name="game"/>: the avatar rows
        /// <c>IsAvailable</c> can reach (those before the first row that is not a signature: a currency, skin, jersey, agent or
        /// camo row is available or answers without the avatar list) and the spawnable avatar rows <c>GetPreview</c> builds.
        /// </summary>
        private static (int IsAvailable, int Preview) AvatarListBuilds(RealTables real, object game)
        {
            List<object> rows = Elements(real.Fill("Game", "Game.CratesInventoryComponent+CrateRewards", AppliedTable(real, game, CrateTable)));
            Assert.NotEmpty(rows);
            bool Avatar(object r) => (string)GameClient.Get(r, "Type") == "KillSignature" && (string)GameClient.Get(r, "Subtype") == "Avatar";
            int reachable = 0;
            foreach (object r in rows)
            {
                if ((string)GameClient.Get(r, "Type") != "KillSignature") break;
                if (Avatar(r)) reachable++;
            }
            int preview = rows.Count(r => Avatar(r) && (bool)GameClient.Call(r, "IsSpawnable"));
            return (reachable, preview);
        }

        private sealed class SignatureEntry
        {
            public string Id;
            public bool Locked;
            public bool IsNew;
            public bool Unlockable;
        }

        // ------------------------------------------------------------------------------------------ 11. the draw

        /// <summary>
        /// Test 11: the real <c>PickRandomWeightedItem</c> (round 0, what every sub-crate of N draws) over the served
        /// <c>CrateRewards</c>: 20,000 draws follow the served weights (rows with a share of 2 % or more within 1.5 points), a
        /// weight-0 row is never drawn, the unlock-all player's table only draws the exhausted row; the four carriers bundle
        /// exactly their sound (<c>GetBundleRewards</c>), always given. The claim itself (<c>GenerateRewards</c> with the
        /// carriers' <c>IsAvailable</c>, the reveal) needs a live profile: device checks 2-4.
        /// </summary>
        [Fact]
        public void TheServedTableDrawsByItsWeights()
        {
            RealTables real = RealTables.TryGet(output);
            if (real == null) return;
            Type inventoryType = real.Type("Game", "Game.CratesInventoryComponent");
            Type rewardType = real.Type("Game", "Game.CratesInventoryComponent+CrateReward");
            Type listType = typeof(List<>).MakeGenericType(rewardType);
            object component = RuntimeHelpers.GetUninitializedObject(inventoryType);
            MethodInfo pick = inventoryType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                                           .Single(m => m.Name == "PickRandomWeightedItem" && m.GetParameters().Length == 3);
            var served = new List<(string, string)>();
            using (var run = new GoldenRun(ActiveDay))
            {
                served.Add(("static", Ok(run.Get("/configs/users/me", null))));
                served.Add(("per-player fresh", Ok(run.Get("/configs/users/me", Player(run, "fresh", GoldenScenarios.DumpText(GoldenScenarios.Fresh))))));
                served.Add(("per-player unlock-all", Ok(run.Get("/configs/users/me", Player(run, "unlockAll", GoldenScenarios.UnlockAllDump())))));
            }
            foreach (var (name, text) in served)
            {
                List<object> rows = Elements(real.Fill("Game", "Game.CratesInventoryComponent+CrateRewards", AppliedTable(real, GameOf(real, text), CrateTable)));
                IList pool = (IList)Activator.CreateInstance(listType);
                foreach (object row in rows) pool.Add(row);
                double total = rows.Sum(r => (double)Weight(r, 0));
                object rng = Activator.CreateInstance(real.Type("Game", "Game.GameRandom"), 11);
                var drawn = new Dictionary<string, int>(StringComparer.Ordinal);
                const int Draws = 20000;
                for (int i = 0; i < Draws; i++)
                {
                    object row = pick.Invoke(component, new[] { pool, rng, 0 });
                    Assert.NotNull(row);
                    string id = (string)GameClient.Get(row, "Id");
                    drawn[id] = drawn.TryGetValue(id, out int n) ? n + 1 : 1;
                }
                foreach (object row in rows)
                {
                    string id = (string)GameClient.Get(row, "Id");
                    double share = Weight(row, 0) / total;
                    double seen = drawn.TryGetValue(id, out int n) ? n / (double)Draws : 0;
                    if (share == 0) Assert.True(seen == 0, name + ": row " + id + " has weight 0 but was drawn");
                    else if (share >= 0.02) Assert.True(Math.Abs(seen - share) < 0.015, name + ": row " + id + " drawn " + seen + ", weight share " + share);
                    var bundles = ((IEnumerable)GameClient.Call(row, "GetBundleRewards")).Cast<object>().Select(b => (string)GameClient.Get(b, "FullType")).ToList();
                    bool carrier = (string)GameClient.Get(row, "BundledRewards") != null;
                    if (carrier)
                    {
                        Assert.Equal(new[] { "KillSignature_Sound_" + (int.Parse(id, CultureInfo.InvariantCulture) - 2) }, bundles);
                        Assert.True((bool)GameClient.Get(row, "AlwaysGiveBundledRewards"));
                        Assert.Equal("KillSignature_Background_Random", (string)GameClient.Get(row, "RewardId"));
                    }
                    else
                    {
                        Assert.Empty(bundles);
                    }
                }
                if (name == "per-player unlock-all") Assert.Equal(new[] { "1" }, drawn.Keys);
                output.WriteLine(name + ": " + drawn.Count + " rows drawn in " + Draws + " draws; most often " + string.Join(", ",
                                 drawn.OrderByDescending(kv => kv.Value).Take(4).Select(kv => "row " + kv.Key + " " + (kv.Value * 100.0 / Draws).ToString("F1", CultureInfo.InvariantCulture) + " %")));
            }
        }

        // ------------------------------------------------------------------------------------------ trees

        private sealed class Tree
        {
            public string Name;
            public object Params;
            public IList<CollectorCell> Cells;
        }

        private static string Ok(McvsOffline.Server.Core.Http.HttpResponse response)
        {
            Assert.Equal(200, response.StatusCode);
            return response.BodyText;
        }

        /// <summary>The two trees of a served document as the client parses it (<c>StandardCrm.Data.RawGameParameters</c>).</summary>
        private static IEnumerable<Tree> Served(RealTables real, string name, string text, IList<CollectorCell> game, IList<CollectorCell> mirror)
        {
            return Trees(real, name, GameOf(real, text), game, mirror);
        }

        private static IEnumerable<Tree> Trees(RealTables real, string name, object gameParams, IList<CollectorCell> game, IList<CollectorCell> mirror)
        {
            yield return new Tree { Name = name, Params = gameParams, Cells = game };
            yield return new Tree { Name = name + " mirror", Params = MirrorOf(gameParams), Cells = mirror };
        }

        private static object GameOf(RealTables real, string documentText)
        {
            object data = Activator.CreateInstance(real.Type("Gameloft.OnlinePlatform", "Gameloft.OnlinePlatform.StandardCrm+Data"), documentText);
            return GameClient.Get(data, "RawGameParameters") ?? throw new InvalidOperationException("the document has no game parameters");
        }

        /// <summary><c>game._ServerConfig.default</c>: the parameters server Worlds install (its <c>_OverrideTables</c> is the mirror).</summary>
        private static object MirrorOf(object gameParams)
        {
            object serverConfig = Index(gameParams, "_ServerConfig") ?? throw new InvalidOperationException("no _ServerConfig");
            return Index(serverConfig, "default") ?? throw new InvalidOperationException("no _ServerConfig.default");
        }

        /// <summary><c>jObject[name]</c> on the game's Newtonsoft.</summary>
        private static object Index(object jObject, string name)
        {
            PropertyInfo item = jObject.GetType().GetProperties()
                                       .First(p => p.Name == "Item" && p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType == typeof(string));
            return item.GetValue(jObject, new object[] { name });
        }

        /// <summary>
        /// Applies every MCVS_Meta table changer of the tree to fresh tables and checks each collector cell: its table is in
        /// MCVS_Meta, the changer has it as a SET of the intended value (as CRMTableCell reads it), the field and the row
        /// exist, and after the apply the row's cell holds that value; no other cell in the collector's field changers.
        /// Returns the number of table changers applied.
        /// </summary>
        private int CheckTree(RealTables real, Tree tree)
        {
            Dictionary<string, object> changers = real.TableChangers(real.Changer(tree.Params));
            real.Changer(tree.Params, "Design/MCVS_Gameplay.xlsx");   // the other database's changer parses too
            var applied = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in changers)
            {
                object table = real.Applied(kv.Value);
                if (table != null) applied[kv.Key] = table;
            }
            foreach (IGrouping<string, CollectorCell> group in tree.Cells.GroupBy(c => c.Table))
            {
                Assert.True(real.HasTable(group.Key), tree.Name + ": MCVS_Meta has no table " + group.Key);
                Assert.True(changers.TryGetValue(group.Key, out object tableChanger), tree.Name + ": no changer for " + group.Key);
                Dictionary<string, Dictionary<string, (object Value, string Operator)>> served = ChangerCells(tableChanger);
                object table = applied[group.Key];
                foreach (IGrouping<string, CollectorCell> field in group.GroupBy(c => c.Changer))
                {
                    Assert.True(served.TryGetValue(field.Key, out var cells), tree.Name + ": no field changer " + group.Key + "." + field.Key);
                    Assert.Equal(field.Select(c => c.Key).OrderBy(k => k, StringComparer.Ordinal), cells.Keys.OrderBy(k => k, StringComparer.Ordinal));
                    int index = RealTables.FieldIndex(table, field.First().Field);
                    Assert.True(index >= 0, tree.Name + ": " + group.Key + " has no field " + field.First().Field);
                    foreach (CollectorCell cell in field)
                    {
                        object expected = ClientValue(cell.Value);
                        Assert.Equal("SET", cells[cell.Key].Operator);
                        Assert.Equal(expected, cells[cell.Key].Value);
                        object[] row = RealTables.Row(table, cell.Index, cell.Key);
                        Assert.True(row != null, tree.Name + ": " + cell + ": no such row");
                        Assert.True(Equals(expected, row[index]), tree.Name + ": " + cell + ": the table holds " + row[index]);
                    }
                }
            }
            return applied.Count;
        }

        /// <summary>The parsed changer's cells: field changer ("Id@Weight#1") → key → (CellNewValue, Operator).</summary>
        private static Dictionary<string, Dictionary<string, (object Value, string Operator)>> ChangerCells(object tableChanger)
        {
            var result = new Dictionary<string, Dictionary<string, (object, string)>>(StringComparer.Ordinal);
            foreach (object fieldChanger in (IEnumerable)GameClient.Get(tableChanger, "FieldChangers"))
            {
                string key = (string)GameClient.Get(fieldChanger, "IndexField") + "@" + (string)GameClient.Get(fieldChanger, "FieldToChange");
                var cells = new Dictionary<string, (object, string)>(StringComparer.Ordinal);
                foreach (object cell in (IEnumerable)GameClient.Get(fieldChanger, "TableCells"))
                    cells[(string)GameClient.Get(cell, "CellRefValue")] = (GameClient.Get(cell, "CellNewValue"), GameClient.Get(cell, "Operator").ToString());
                result[key] = cells;
            }
            return result;
        }

        /// <summary>What CRMTableCell makes of a served value (EN/CRMTableCell.cs): numbers, also numeric strings, become doubles.</summary>
        private static object ClientValue(object value)
        {
            if (value is long l) return (double)l;
            string text = (string)value;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? (object)d : text;
        }

        /// <summary>The per-player game layer: the static cells with each patched field changer replaced by the player's cells.</summary>
        private static List<CollectorCell> PlayerGameCells(CrmDocument doc, string dump)
        {
            CrmCollector c = doc.Collector;
            IList<FieldPatch> patches = CollectorPlan.PlayerCells(c.Content, c.Options, PlayerInventory.Scan(dump, true), c.LiveOps);
            var patched = new HashSet<string>(patches.Select(p => p.Table + "\n" + p.Changer), StringComparer.Ordinal);
            List<CollectorCell> cells = c.GameCells.Where(x => !patched.Contains(x.Table + "\n" + x.Changer)).ToList();
            foreach (FieldPatch p in patches)
                foreach (var kv in p.Cells) cells.Add(new CollectorCell(p.Table, p.Index, p.Field, kv.Key, kv.Value));
            return cells;
        }

        /// <summary>A player whose dump was saved and read (as the TS does at login); returns the on-behalf token.</summary>
        private static string Player(GoldenRun run, string user, string dump)
        {
            string token = run.NewPlayer(user);
            run.SaveGameProfile(token, dump);
            run.ReadGameProfile(token);
            return token;
        }

        /// <summary>A document built with the collector active for these options and snapshot (the builder's own cells).</summary>
        private static string Built(CollectorContent content, CollectorOptions options, LiveOpsSnapshot snapshot)
        {
            var part = new CrmCollector
            {
                Status = new CollectorStatus { Phase = CollectorPhase.Active, Reason = "conformance", Since = CollectorGuard.ShippedSince },
                Content = content,
                Options = options,
            };
            Dictionary<string, object> doc = CrmDocumentBuilder.Build(null, new CrmContent(), new CrmOptions(), Log.Null, snapshot, null, part);
            Assert.Equal(CollectorPhase.Active, part.Phase);
            return MiniJson.Serialize(doc);
        }

        /// <summary>The served cells of one field changer of <c>game._OverrideTables.MCVS_Meta</c>, or null.</summary>
        private static Dictionary<string, object> ServedCells(string documentText, string table, string changer)
        {
            return JsonValue.AsObject(JsonPath.Get(MiniJson.ParseObject(documentText), "game._OverrideTables.MCVS_Meta." + table + "." + changer));
        }

        private static object AppliedTable(RealTables real, object gameParams, string table)
        {
            Dictionary<string, object> changers = real.TableChangers(real.Changer(gameParams));
            return changers.TryGetValue(table, out object changer) ? real.Applied(changer) : real.NewTable(table);
        }

        // ------------------------------------------------------------------------------------------ typed tables

        private static List<object> Elements(object collection) => ((IEnumerable)collection).Cast<object>().ToList();

        private static Dictionary<string, object> ElementsById(object collection)
        {
            return Elements(collection).ToDictionary(x => (string)GameClient.Get(x, "Id"), x => x, StringComparer.Ordinal);
        }

        private static object Element(object collection, string id)
        {
            return Elements(collection).SingleOrDefault(x => string.Equals((string)GameClient.Get(x, "Id"), id, StringComparison.OrdinalIgnoreCase))
                   ?? throw new KeyNotFoundException(id);
        }

        private static float Weight(object crateReward, int round) => (float)GameClient.Call(crateReward, "GetWeight", round);

        private static void AssertCrate(object specs, string content, int rewardsCount, int cratesCount, float[] chances, string locId, string label)
        {
            Assert.True(content == (string)GameClient.Get(specs, "ContentTableName"), label + ": ContentTableName " + GameClient.Get(specs, "ContentTableName"));
            Assert.True(rewardsCount == (int)GameClient.Get(specs, "RewardsCount"), label + ": RewardsCount " + GameClient.Get(specs, "RewardsCount"));
            Assert.True(cratesCount == (int)GameClient.Get(specs, "CratesCount"), label + ": CratesCount " + GameClient.Get(specs, "CratesCount"));
            Assert.Equal(chances, (float[])GameClient.Get(specs, "RewardsChances"));
            Assert.True(locId == (string)GameClient.Get(specs, "LocId"), label + ": LocId " + GameClient.Get(specs, "LocId"));
        }

        private static IEnumerable<string> TierCrates(int tier)
        {
            yield return "Crate_" + Tiers[tier] + "_4To10Place";
            yield return "Crate_" + Tiers[tier] + "_11To20Place";
        }

        private static IEnumerable<string> TierCrates() => Enumerable.Range(0, Tiers.Length).SelectMany(TierCrates);

        // ------------------------------------------------------------------------------------------ rewards

        private static string[] BuildRewardIds()
        {
            // The exhausted row first (§2.3: CrateRewardItem.IsAvailable stops there), the sounds on background carriers.
            var ids = new List<string> { "VersusCoins_Tiers4", "KillSignature_Avatar_Random", "KillSignature_Background_Random" };
            for (int sound = 2; sound <= 5; sound++) ids.Add("KillSignature_Background_Random");
            foreach (string skin in new[] { "2_5", "4_6", "6_4", "8_7", "9_3", "9_4", "10_7", "11_3", "11_4", "12_4", "15_6", "17_6", "17_11", "17_12", "19_4", "20_4" })
                ids.Add("MercSkin_" + skin);
            foreach (int jersey in new[] { 112, 143, 144, 152, 153, 212, 213, 214, 268, 269, 270, 271 }) ids.Add("MercAccessory_Random_" + jersey);
            ids.Add("UnlockMerc_17");
            ids.Add("UnlockMerc_21");
            ids.Add("WeaponCamo_Random_Random");
            ids.Add("MercAccessory_Random_Random");
            return ids.ToArray();
        }

        private static string ExpectedClass(string type)
        {
            switch (type)
            {
                case "KillSignature": return "Game.KillSignatureRewardItem";
                case "MercSkin": return "Game.MercSkinRewardItem";
                case "MercAccessory": return "Game.MercAccessoryRewardItem";
                case "UnlockMerc": return "Game.UnlockMercRewardItem";
                case "WeaponCamo": return "Game.WeaponCamoRewardItem";
                case "VersusCoins": return "Game.VersusCoinsRewardItem";
                default: throw new ArgumentException(type);
            }
        }

        /// <summary>§2.3.1, written out here: the rewards a row with Weight.1 &gt; 0 may hold (the preview builds them safely).</summary>
        private static bool PreviewSafe(string type, string subtype, string subtype2)
        {
            bool numeric = int.TryParse(subtype, NumberStyles.None, CultureInfo.InvariantCulture, out _);
            switch (type)
            {
                case "KillSignature": return (subtype == "Avatar" || subtype == "Background") && subtype2 == "Random";
                case "MercSkin": return numeric && int.TryParse(subtype2, NumberStyles.None, CultureInfo.InvariantCulture, out _);
                case "MercAccessory": return subtype == "Random" && (subtype2 == "Random" || int.TryParse(subtype2, NumberStyles.None, CultureInfo.InvariantCulture, out _));
                case "UnlockMerc": return numeric && subtype2 == null;
                case "WeaponCamo": return subtype == "Random" && subtype2 == "Random";
                case "VersusCoins": return subtype != null && subtype.StartsWith("Tiers", StringComparison.Ordinal) && subtype2 == null;
                default: return false;
            }
        }

        /// <summary>A loc id "Table.Id" is in Localization/&lt;Table&gt;_en.json.</summary>
        private static bool LocExists(RealTables real, string locId)
        {
            int dot = locId.IndexOf('.');
            string text = real.ReadText("Localization/" + locId.Substring(0, dot) + "_en.json");
            if (text == null) return false;
            string id = locId.Substring(dot + 1);
            return ((List<object>)MiniJson.Parse(text)).Cast<Dictionary<string, object>>().Any(e => (string)e["Id"] == id);
        }

        // ------------------------------------------------------------------------------------------ misc

        /// <summary>Every key path of a JSON tree with its value kind, ordered (list elements by index).</summary>
        private static List<string> KeyPaths(object tree)
        {
            var paths = new List<string>();
            void Walk(object value, string path)
            {
                if (value is Dictionary<string, object> obj)
                {
                    foreach (var kv in obj) Walk(kv.Value, path + "/" + kv.Key);
                }
                else if (value is List<object> list)
                {
                    for (int i = 0; i < list.Count; i++) Walk(list[i], path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                    if (list.Count == 0) paths.Add(path + " []");
                }
                else
                {
                    paths.Add(path + " " + (value == null ? "null" : value.GetType().Name));
                }
            }
            Walk(tree, "");
            paths.Sort(StringComparer.Ordinal);
            return paths;
        }

        private static string Compact(string json) => new string(json.Where(c => !char.IsWhiteSpace(c)).ToArray());

        private static double[] Time(Action action)
        {
            var runs = new double[6];
            for (int i = 0; i < runs.Length; i++)
            {
                var watch = Stopwatch.StartNew();
                action();
                runs[i] = watch.Elapsed.TotalMilliseconds;
            }
            return runs;
        }

        private static string Ms(double ms) => ms.ToString("F1", CultureInfo.InvariantCulture) + " ms";
    }

    /// <summary>The collector conformance tests run alone: their timing (test 8) must not measure the other tests.</summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class CollectorTimingCollection
    {
        public const string Name = "Collector conformance (timing)";
    }
}
