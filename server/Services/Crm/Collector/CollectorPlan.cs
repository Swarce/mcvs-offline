using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Social.LiveOps;

namespace McvsOffline.Server.Services.Crm.Collector
{
    /// <summary>
    /// One SET cell of <c>game._OverrideTables.MCVS_Meta</c> (docs/design/economy-collector.md §2): the cell
    /// <c>&lt;Table&gt;["&lt;Index&gt;@&lt;Field&gt;"]["&lt;Key&gt;"] = Value</c>. <see cref="Field"/> is given with dots
    /// ("Weight.1") and written with '#' (<see cref="Changer"/>). <see cref="Value"/> is a non-empty string or a long,
    /// never "", null or a fraction (CRMTableCell throws on "", the typed readers on fractions).
    /// </summary>
    public sealed class CollectorCell
    {
        public CollectorCell(string table, string index, string field, string key, object value)
        {
            Table = table;
            Index = index;
            Field = field;
            Key = key;
            Value = value;
        }

        public string Table { get; private set; }

        public string Index { get; private set; }

        public string Field { get; private set; }

        public string Key { get; private set; }

        public object Value { get; private set; }

        /// <summary>The field changer's key as the document holds it: "Id@Weight#1".</summary>
        public string Changer
        {
            get { return Index + "@" + Field.Replace('.', '#'); }
        }

        public override string ToString()
        {
            return Table + "." + Changer + "." + Key + " = " + JsonValue.AsString(Value);
        }
    }

    /// <summary>
    /// The collector's static cells (§2.8): <see cref="Game"/> for <c>game._OverrideTables</c>, <see cref="Mirror"/> (the
    /// same minus the <c>Unlockable=true</c> cells, D10) for <c>game._ServerConfig.default._OverrideTables</c>.
    /// </summary>
    public sealed class CollectorLayer
    {
        public IList<CollectorCell> Game = new List<CollectorCell>();
        public IList<CollectorCell> Mirror = new List<CollectorCell>();

        /// <summary>The static assumptions' units and the weights they give (diagnostics, §4.2).</summary>
        public CollectorUnits Units;
        public CollectorWeights Weights;
    }

    /// <summary>
    /// A per-player replacement of one whole field changer of <c>game._OverrideTables.MCVS_Meta</c> (§2.9.2, §2.9.4):
    /// the cells it holds for this player, keys in ascending numeric order. Empty <see cref="Cells"/>: the changer is
    /// removed. Only changers the static layer also writes are patched.
    /// </summary>
    public sealed class FieldPatch
    {
        public FieldPatch(string table, string index, string field)
        {
            Table = table;
            Index = index;
            Field = field;
            Cells = new Dictionary<string, object>(StringComparer.Ordinal);
        }

        public string Database
        {
            get { return CollectorPlan.Database; }
        }

        public string Table { get; private set; }

        public string Index { get; private set; }

        /// <summary>With dots ("Weight.1"); see <see cref="Changer"/>.</summary>
        public string Field { get; private set; }

        /// <summary>The field changer's key as the document holds it: "Id@Weight#1".</summary>
        public string Changer
        {
            get { return Index + "@" + Field.Replace('.', '#'); }
        }

        public Dictionary<string, object> Cells { get; private set; }
    }

    /// <summary>
    /// The collector's CRM cells (docs/design/economy-collector.md §2.2-§2.7), pure: the same inputs give the same cells.
    /// <see cref="Static"/> is the layer of the static document (static assumptions of §4.2: every agent unlocked, nothing
    /// owned but the starting items); <see cref="PlayerCells"/> the per-player patches of the weights, the Unlockable
    /// window and the season emblems (§2.9.2). Both compute from an inventory through the same code: the static
    /// document is the per-player document of the static inventory.
    /// </summary>
    public static class CollectorPlan
    {
        public const string Database = "MCVS_Meta";
        public const string LootCratesTable = "LootCrates";
        public const string AvatarsTable = "KillsignatureAvatars";
        public const string BackgroundsTable = "KillsignatureBackgrounds";
        public const string LevelsTable = "PlayerLevelXP";

        /// <summary>MCVS_Meta <c>StartingInventory</c>: avatar 1, background 1, sound 1 and agent 1.</summary>
        public const int StartingItem = 1;

        /// <summary>The placement crates' content-table row that carries the season emblem (the only <c>Weight.1</c> row).</summary>
        public const string PlacementRow = "1";

        private const string True = "true";
        private const string False = "false";

        /// <summary>
        /// The static layer: the crate (§2.2, §2.3), the retargeted tier crates (§2.4), the season emblems (§2.5, while
        /// <c>seasonEmblems</c> and division seasons are on; none without a snapshot), the Unlockable cells (§2.6) and the
        /// optional level-up avatars (§2.7). The mirror leaves out the <c>true</c> cells (D10).
        /// </summary>
        public static CollectorLayer Static(CollectorContent content, CollectorOptions options, LiveOpsSnapshot snapshot)
        {
            PlayerInventory inventory = StaticInventory(content);
            List<int> window = UnlockWindow(content, options, inventory.Avatars);
            var layer = new CollectorLayer();
            layer.Units = CollectorUnits.Count(content, inventory, window);
            layer.Weights = CollectorWeights.Compute(content, options, layer.Units);
            var game = new List<CollectorCell>();

            foreach (CollectorRow row in content.Rows)
            {
                string key = Text(row.Id);
                bool exhausted = row.Category == CollectorContent.Exhausted;
                game.Add(new CollectorCell(content.Table, "Id", "Type", key, exhausted ? options.ExhaustedType : row.Type));
                game.Add(new CollectorCell(content.Table, "Id", "Subtype", key, exhausted ? options.ExhaustedSubtype : row.Subtype));
                if (row.Subtype2 != null && !exhausted) game.Add(new CollectorCell(content.Table, "Id", "Subtype2", key, row.Subtype2));
                game.Add(new CollectorCell(content.Table, "Id", "Weight.1", key, layer.Weights.Of(row.Id)));
                if (row.Bundle != null)
                {
                    game.Add(new CollectorCell(content.Table, "Id", "BundledRewards", key, row.Bundle));
                    game.Add(new CollectorCell(content.Table, "Id", "AlwaysGiveBundledRewards", key, True));
                }
            }

            // §2.2: items per crate = CratesCount, one Weight.1 draw per sub-crate (D1).
            game.Add(new CollectorCell(LootCratesTable, "Id", "LocId", content.CrateId, content.LocId));
            game.Add(new CollectorCell(LootCratesTable, "Id", "CratesCount", content.CrateId, (long)options.ItemsPerCrate));
            game.Add(new CollectorCell(LootCratesTable, "Id", "RewardChance.2", content.CrateId, 0L));

            int[] emblems = SeasonEmblems(content, options, snapshot, inventory.Avatars);
            for (int tier = 0; tier < content.Tiers.Count; tier++)
            {
                int band = options.SeasonCrates != null && tier < options.SeasonCrates.Length ? options.SeasonCrates[tier] : 0;
                if (band > 0)
                {
                    // §2.4 (D3): the tier's ranks 4-30 crates open the Cosmetic crate's table band × N times.
                    long seasonCrates = SeasonCratesCount(band, options, snapshot);
                    foreach (string crate in content.Retarget[tier])
                    {
                        game.Add(new CollectorCell(LootCratesTable, "Id", "ContentTableName", crate, content.Table));
                        game.Add(new CollectorCell(LootCratesTable, "Id", "CratesCount", crate, seasonCrates));
                        game.Add(new CollectorCell(LootCratesTable, "Id", "RewardChance.2", crate, 0L));
                        game.Add(new CollectorCell(LootCratesTable, "Id", "LocId", crate, content.LocId));
                        game.Add(new CollectorCell(LootCratesTable, "Id", "Image", crate, content.Image));
                        game.Add(new CollectorCell(LootCratesTable, "Id", "ImageShop", crate, content.Image));
                    }
                }
                if (emblems != null)
                {
                    for (int place = 0; place < content.Placement[tier].Length; place++)
                    {
                        string table = content.Placement[tier][place];
                        game.Add(new CollectorCell(table, "Id", "BundledRewards", PlacementRow, EmblemReward(emblems[place])));
                        game.Add(new CollectorCell(table, "Id", "AlwaysGiveBundledRewards", PlacementRow, True));
                    }
                }
            }

            foreach (KeyValuePair<int, string> cell in AvatarCells(content, inventory.Avatars, window))
                game.Add(new CollectorCell(AvatarsTable, "ID", "Unlockable", Text(cell.Key), cell.Value));
            foreach (int b in BackgroundUnlocks(content, inventory.Backgrounds))
                game.Add(new CollectorCell(BackgroundsTable, "ID", "Unlockable", Text(b), True));

            if (options.LevelUpAvatars > 0)
                foreach (int level in content.Levels)
                    game.Add(new CollectorCell(LevelsTable, "Level", "AvatarRewards", Text(level), (long)options.LevelUpAvatars));

            layer.Game = game;
            var mirror = new List<CollectorCell>();
            foreach (CollectorCell cell in game)
                if (!IsUnlockableTrue(cell)) mirror.Add(cell);
            layer.Mirror = mirror;
            return layer;
        }

        /// <summary>
        /// The per-player patches of <c>game._OverrideTables</c> (§2.9.2): the crate's <c>Id@Weight#1</c> (every row), the
        /// avatars' and backgrounds' <c>ID@Unlockable</c> and, while the static layer has them, the placement tables'
        /// <c>Id@BundledRewards</c>. An unknown inventory part (or a null inventory) takes the static assumptions for
        /// that part only.
        /// </summary>
        public static IList<FieldPatch> PlayerCells(CollectorContent content, CollectorOptions options, PlayerInventory inventory,
                                                    LiveOpsSnapshot snapshot)
        {
            CollectorUnits units;
            CollectorWeights weights;
            return PlayerCells(content, options, inventory, snapshot, out units, out weights);
        }

        /// <summary><see cref="PlayerCells(CollectorContent, CollectorOptions, PlayerInventory, LiveOpsSnapshot)"/>, also
        /// returning what is left (for the log line of §2.9.6) and the weights.</summary>
        public static IList<FieldPatch> PlayerCells(CollectorContent content, CollectorOptions options, PlayerInventory inventory,
                                                    LiveOpsSnapshot snapshot, out CollectorUnits units, out CollectorWeights weights)
        {
            PlayerInventory effective = Effective(content, inventory);
            List<int> window = UnlockWindow(content, options, effective.Avatars);
            units = CollectorUnits.Count(content, effective, window);
            weights = CollectorWeights.Compute(content, options, units);
            var patches = new List<FieldPatch>();

            var weightPatch = new FieldPatch(content.Table, "Id", "Weight.1");
            foreach (CollectorRow row in content.Rows) weightPatch.Cells[Text(row.Id)] = weights.Of(row.Id);
            patches.Add(weightPatch);

            var avatars = new FieldPatch(AvatarsTable, "ID", "Unlockable");
            foreach (KeyValuePair<int, string> cell in AvatarCells(content, effective.Avatars, window)) avatars.Cells[Text(cell.Key)] = cell.Value;
            patches.Add(avatars);

            var backgrounds = new FieldPatch(BackgroundsTable, "ID", "Unlockable");
            foreach (int b in BackgroundUnlocks(content, effective.Backgrounds)) backgrounds.Cells[Text(b)] = True;
            patches.Add(backgrounds);

            int[] emblems = SeasonEmblems(content, options, snapshot, effective.Avatars);
            if (emblems != null)
            {
                for (int tier = 0; tier < content.Placement.Count; tier++)
                {
                    for (int place = 0; place < content.Placement[tier].Length; place++)
                    {
                        var patch = new FieldPatch(content.Placement[tier][place], "Id", "BundledRewards");
                        patch.Cells[PlacementRow] = EmblemReward(emblems[place]);
                        patches.Add(patch);
                    }
                }
            }
            return patches;
        }

        // ------------------------------------------------------------------------------------------ inventories

        /// <summary>
        /// The static assumptions (§4.2) as an inventory: every agent of the crate unlocked with only its default skin and
        /// camo ("1") and no jersey, and only the starting avatar, background and sound.
        /// </summary>
        public static PlayerInventory StaticInventory(CollectorContent content)
        {
            var inventory = new PlayerInventory { AvatarsKnown = true, BackgroundsKnown = true, SoundsKnown = true, MercsKnown = true };
            inventory.Avatars.Add(StartingItem);
            inventory.Backgrounds.Add(StartingItem);
            inventory.Sounds.Add(StartingItem);
            foreach (int agent in content.AgentIds) inventory.Mercs[agent] = DefaultAgent(true);
            return inventory;
        }

        /// <summary>
        /// <paramref name="inventory"/> with every unknown part replaced by the static assumptions (§2.9.1: an unknown part
        /// falls back for that part only); a null inventory is the static one. The input is not changed.
        /// </summary>
        public static PlayerInventory Effective(CollectorContent content, PlayerInventory inventory)
        {
            PlayerInventory assumed = StaticInventory(content);
            if (inventory == null) return assumed;
            var effective = new PlayerInventory { AvatarsKnown = true, BackgroundsKnown = true, SoundsKnown = true, MercsKnown = true };
            effective.Avatars = inventory.AvatarsKnown && inventory.Avatars != null ? inventory.Avatars : assumed.Avatars;
            effective.Backgrounds = inventory.BackgroundsKnown && inventory.Backgrounds != null ? inventory.Backgrounds : assumed.Backgrounds;
            effective.Sounds = inventory.SoundsKnown && inventory.Sounds != null ? inventory.Sounds : assumed.Sounds;
            effective.Mercs = inventory.MercsKnown && inventory.Mercs != null ? inventory.Mercs : assumed.Mercs;
            return effective;
        }

        private static MercState DefaultAgent(bool unlocked)
        {
            var merc = new MercState { Unlocked = unlocked };
            merc.Skins.Add(StartingItem);
            merc.Camos.Add(StartingItem);
            return merc;
        }

        // ------------------------------------------------------------------------------------------ cells

        /// <summary>
        /// The avatars sent as <c>Unlockable=true</c> (§2.6): the first <c>unlockableWindow</c> ids, ascending, of
        /// <c>avatars.unlock</c> that the player does not own.
        /// </summary>
        public static List<int> UnlockWindow(CollectorContent content, CollectorOptions options, ICollection<int> ownedAvatars)
        {
            var window = new List<int>();
            foreach (int a in content.AvatarsUnlock)
            {
                if (window.Count >= options.UnlockableWindow) break;
                if (!ownedAvatars.Contains(a)) window.Add(a);
            }
            return window;
        }

        /// <summary>The window's <c>true</c> cells and the <c>false</c> cells of the unowned <c>avatars.lock</c> (O5,
        /// never windowed), by ascending id.</summary>
        private static List<KeyValuePair<int, string>> AvatarCells(CollectorContent content, ICollection<int> owned, List<int> window)
        {
            var cells = new List<KeyValuePair<int, string>>();
            foreach (int a in window) cells.Add(new KeyValuePair<int, string>(a, True));
            foreach (int a in content.AvatarsLock)
                if (!owned.Contains(a)) cells.Add(new KeyValuePair<int, string>(a, False));
            cells.Sort((x, y) => x.Key.CompareTo(y.Key));
            return cells;
        }

        /// <summary>The unowned of <c>backgrounds.unlock</c> (§2.6), ascending.</summary>
        private static List<int> BackgroundUnlocks(CollectorContent content, ICollection<int> owned)
        {
            var ids = new List<int>();
            foreach (int b in content.BackgroundsUnlock)
                if (!owned.Contains(b)) ids.Add(b);
            return ids;
        }

        /// <summary>True for the cells the mirror leaves out (D10): <c>Unlockable=true</c> of avatars and backgrounds.</summary>
        public static bool IsUnlockableTrue(CollectorCell cell)
        {
            return (cell.Table == AvatarsTable || cell.Table == BackgroundsTable) && cell.Field == "Unlockable"
                   && string.Equals(cell.Value as string, True, StringComparison.Ordinal);
        }

        /// <summary>
        /// <c>CratesCount</c> of a retargeted tier crate (§2.4): <c>max(1, min(band × itemsPerCrate, ⌊maxSeasonItems / q⌋))</c>
        /// with <c>q = ⌈liveops.seasonRewardMultiplier⌉</c> (1 when ≤ 1): the multiplier's MUL cell multiplies the number
        /// of tier crates of a claim, each opening <c>CratesCount</c> sub-crates.
        /// </summary>
        public static long SeasonCratesCount(int band, CollectorOptions options, LiveOpsSnapshot snapshot)
        {
            double multiplier = snapshot != null && snapshot.Settings != null ? snapshot.Settings.SeasonRewardMultiplier : 1.0;
            // -Floor(-x): Math.Ceiling is not in the game's trimmed mscorlib.
            long q = multiplier <= 1 || double.IsNaN(multiplier) ? 1 : (long)-Math.Floor(-multiplier);
            long count = (long)band * options.ItemsPerCrate;
            long cap = options.MaxSeasonItems / q;
            if (count > cap) count = cap;
            return count < 1 ? 1 : count;
        }

        // ------------------------------------------------------------------------------------------ season emblems

        /// <summary>
        /// The emblems of 1st, 2nd and 3rd place (★★★, ★★, ★; §2.5), or null when none are sent (<c>seasonEmblems</c>
        /// off, division seasons off, no snapshot). Per star count: starting at the numeral of the season that ended and
        /// cycling through I..XII, the first emblem <paramref name="ownedAvatars"/> lacks; all 12 owned: the season's
        /// numeral (the claim then pays its duplicate compensation). The static inventory gets the season's numeral.
        /// </summary>
        public static int[] SeasonEmblems(CollectorContent content, CollectorOptions options, LiveOpsSnapshot snapshot,
                                          ICollection<int> ownedAvatars)
        {
            if (!options.SeasonEmblems || snapshot == null || snapshot.Season == null) return null;
            if (snapshot.Settings != null && !snapshot.Settings.DivisionSeasons) return null;
            int numerals = content.Emblems.Count;
            int start = Numeral(snapshot.Season, numerals);
            var result = new int[3];
            for (int place = 0; place < 3; place++)
            {
                int stars = 3 - place;
                result[place] = content.Emblems[start][stars];
                for (int step = 0; step < numerals; step++)
                {
                    int id = content.Emblems[(start + step) % numerals][stars];
                    if (!ownedAvatars.Contains(id))
                    {
                        result[place] = id;
                        break;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// The numeral (0 = I) of the season that ended when <paramref name="season"/> started:
        /// <c>Ordinal(endedSeason) mod numerals</c>, <c>endedSeason = new LiveOpsWindow(season.Start − season.Days, season.Days)</c>.
        /// </summary>
        public static int Numeral(LiveOpsWindow season, int numerals)
        {
            var ended = new LiveOpsWindow(season.Start.AddDays(-season.Days), season.Days);
            long n = LiveOpsSchedule.Ordinal(ended) % numerals;
            return (int)(n < 0 ? n + numerals : n);
        }

        private static string EmblemReward(int avatar)
        {
            return "KillSignature_Avatar_" + Text(avatar);
        }

        private static string Text(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
