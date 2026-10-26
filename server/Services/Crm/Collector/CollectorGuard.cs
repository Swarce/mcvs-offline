using System;
using System.Collections.Generic;

namespace McvsOffline.Server.Services.Crm.Collector
{
    /// <summary>
    /// The guard (docs/design/economy-collector.md §2.8, D9): five compiled-in cells that make <c>Crate_Event_Special</c>
    /// a valid crate of one 70-VersusCoins row, sent whenever the collector layer is not (phase <c>Guard</c>, a build or
    /// validation failure, the minimal document) and as the per-player mirror. From <see cref="ShippedSince"/> on a stale
    /// Cosmetic crate (a milestone claimed after a switch-off) then shows "COSMETIC CRATE, CONTAINS 1 ITEM" and pays 70
    /// VersusCoins instead of throwing in <c>CrateRewardItem.OnClaimed</c> on the TS (an empty content table throws,
    /// §0.3.6). Not the <c>CratesCount=0</c> variant: that ends in an empty <c>NotifyCrateReceived</c>.
    /// </summary>
    public static class CollectorGuard
    {
        /// <summary>
        /// The <c>since</c> of the shipped server/Content/liveops.json <c>collector</c> block, compiled in so the guard
        /// starts even when that block is missing or broken (<c>guardSince = min(since, ShippedSince)</c>, §2.12).
        /// GeneratedContentTests asserts it equals the shipped file.
        /// </summary>
        public static readonly DateTime ShippedSince = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);

        public const string CrateTable = "Crate_Event_Special";
        public const string CrateId = "Crate_Event_Special";
        public const string LocId = "Crate.Cosmetic_Crate";

        /// <summary>The 5 guard cells (a new list on every call: callers may keep or change it).</summary>
        public static IList<CollectorCell> Cells
        {
            get
            {
                return new List<CollectorCell>
                {
                    new CollectorCell(CrateTable, "Id", "Type", "1", "VersusCoins"),
                    new CollectorCell(CrateTable, "Id", "Subtype", "1", "Tiers4"),
                    new CollectorCell(CrateTable, "Id", "Weight.1", "1", 1L),
                    new CollectorCell(CollectorPlan.LootCratesTable, "Id", "LocId", CrateId, LocId),
                    new CollectorCell(CollectorPlan.LootCratesTable, "Id", "RewardChance.2", CrateId, 0L),
                };
            }
        }
    }
}
