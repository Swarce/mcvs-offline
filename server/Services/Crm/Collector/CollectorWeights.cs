using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace McvsOffline.Server.Services.Crm.Collector
{
    /// <summary>
    /// What is left for a player in every crate row, unweighted (docs/design/economy-collector.md §4.1): <c>uA</c> for the
    /// avatar row, <c>uB</c> for the background row, <c>uS</c>/<c>uK</c>/<c>uH</c> per carrier, skin and hidden-jersey row,
    /// 1 per locked agent row, <c>uC</c>/<c>uJ</c> for the camo and jersey rows, 0 for the exhausted row.
    /// </summary>
    public sealed class CollectorUnits
    {
        /// <summary>Units by row id.</summary>
        public Dictionary<int, long> Rows = new Dictionary<int, long>();

        public long Of(int rowId)
        {
            long units;
            return Rows.TryGetValue(rowId, out units) ? units : 0;
        }

        /// <summary>
        /// Counts the rows of <paramref name="content"/> for an inventory whose every part is known (the static
        /// assumptions stand in for an unknown part: <see cref="CollectorPlan.Effective"/>). <paramref name="unlockAvatars"/>
        /// are the avatars the document sends as <c>Unlockable=true</c> (§2.6): the random avatar rows draw from
        /// <c>avatars.drawable</c> and those.
        /// </summary>
        public static CollectorUnits Count(CollectorContent content, PlayerInventory inventory, ICollection<int> unlockAvatars)
        {
            var units = new CollectorUnits();
            var unlocked = new List<int>();
            foreach (int agent in content.AgentIds)
                if (IsUnlocked(inventory, agent)) unlocked.Add(agent);

            long avatars = 0;
            foreach (int a in content.AvatarsDrawable)
                if (!inventory.Avatars.Contains(a)) avatars++;
            var drawable = new HashSet<int>(content.AvatarsDrawable);
            foreach (int a in unlockAvatars)
                if (!drawable.Contains(a) && !inventory.Avatars.Contains(a)) avatars++;

            long backgrounds = 0;
            var bgDrawable = new HashSet<int>(content.BackgroundsDrawable);
            foreach (int b in content.BackgroundsDrawable)
                if (!inventory.Backgrounds.Contains(b)) backgrounds++;
            foreach (int b in content.BackgroundsUnlock)
                if (!bgDrawable.Contains(b) && !inventory.Backgrounds.Contains(b)) backgrounds++;

            long camos = 0, jerseys = 0;
            foreach (int agent in unlocked)
            {
                MercState merc = inventory.Mercs[agent];
                foreach (int camo in content.CamoPools[agent])
                    if (!merc.Camos.Contains(camo)) camos++;
                foreach (int jersey in content.JerseyPool)
                    if (!merc.Accessories.Contains(jersey)) jerseys++;
            }

            foreach (CollectorRow row in content.Rows)
            {
                long u = 0;
                switch (row.Category)
                {
                    case CollectorContent.Avatars:
                        u = avatars;
                        break;
                    case CollectorContent.Backgrounds:
                        u = backgrounds;
                        break;
                    case CollectorContent.Sounds:
                        u = inventory.Sounds.Contains(row.Item.Value) ? 0 : 1;
                        break;
                    case CollectorContent.Skins:
                        u = IsUnlocked(inventory, row.Merc.Value) && !inventory.Mercs[row.Merc.Value].Skins.Contains(row.Item.Value) ? 1 : 0;
                        break;
                    case CollectorContent.HiddenJerseys:
                        foreach (int agent in unlocked)
                            if (!inventory.Mercs[agent].Accessories.Contains(row.Item.Value)) u++;
                        break;
                    case CollectorContent.Agents:
                        u = IsUnlocked(inventory, row.Merc.Value) ? 0 : 1;
                        break;
                    case CollectorContent.Camos:
                        u = camos;
                        break;
                    case CollectorContent.Jerseys:
                        u = jerseys;
                        break;
                }
                units.Rows[row.Id] = u;
            }
            return units;
        }

        /// <summary>The units of every row of a category.</summary>
        public long Category(CollectorContent content, string category)
        {
            long sum = 0;
            foreach (CollectorRow row in content.Rows)
                if (row.Category == category) sum += Of(row.Id);
            return sum;
        }

        /// <summary>For the log: "base 663 left (avatars 368, …), paid 9365 left (camos 4409, jerseys 4956)".</summary>
        public string Summary(CollectorContent content)
        {
            var sb = new StringBuilder();
            Append(sb, content, "base", CollectorContent.BaseCategories);
            sb.Append(", ");
            Append(sb, content, "paid", CollectorContent.PaidCategories);
            return sb.ToString();
        }

        private void Append(StringBuilder sb, CollectorContent content, string tier, string[] categories)
        {
            long total = 0;
            var parts = new List<string>();
            foreach (string category in categories)
            {
                long n = Category(content, category);
                total += n;
                parts.Add(category + " " + n.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(tier).Append(' ').Append(total.ToString(CultureInfo.InvariantCulture)).Append(" left (")
              .Append(string.Join(", ", parts.ToArray())).Append(')');
        }

        private static bool IsUnlocked(PlayerInventory inventory, int agent)
        {
            MercState merc;
            return inventory.Mercs.TryGetValue(agent, out merc) && merc.Unlocked;
        }
    }

    /// <summary>
    /// The crate rows' <c>Weight#1</c> (docs/design/economy-collector.md §4.1): tiered with a base share, proportional to
    /// what is left inside each tier, so every remaining item of a tier is equally likely and a tier's categories finish
    /// together.
    /// <code>
    /// base = Σ weighted units of the no-source rows      paid = uC + uJ (weighted)
    /// share_base = baseShare if base &gt; 0 and paid &gt; 0; 1 if paid = 0; 0 if base = 0
    /// Weight#1(row) = round(1,000,000 × share_tier × u(row) / tier_total), at least 1 when u(row) &gt; 0, else 0
    /// Weight#1(exhausted) = 1 if base = paid = 0, else 0
    /// </code>
    /// Integers only: the client re-reads a weight with <c>float.Parse(cell.ToString(), Invariant)</c> (§2.3). Rounding is
    /// half to even, as the design's scratch scripts (Python <c>round</c>) computed §4.2.
    /// </summary>
    public sealed class CollectorWeights
    {
        public const double Scale = 1000000.0;

        /// <summary><c>Weight#1</c> by row id, in row order.</summary>
        public Dictionary<int, long> Rows = new Dictionary<int, long>();

        /// <summary>Weighted units of the no-source tier.</summary>
        public double Base;

        /// <summary>Weighted units of the paid tier.</summary>
        public double Paid;

        /// <summary>The no-source tier's share of the draws.</summary>
        public double BaseShare;

        public long Of(int rowId)
        {
            long weight;
            return Rows.TryGetValue(rowId, out weight) ? weight : 0;
        }

        public static CollectorWeights Compute(CollectorContent content, CollectorOptions options, CollectorUnits units)
        {
            var w = new CollectorWeights();
            foreach (CollectorRow row in content.Rows)
            {
                double u = Weighted(options, units, row);
                if (CollectorContent.IsBaseCategory(row.Category)) w.Base += u;
                else if (CollectorContent.IsPaidCategory(row.Category)) w.Paid += u;
            }
            w.BaseShare = w.Base > 0 && w.Paid > 0 ? options.BaseShare : w.Paid <= 0 ? 1.0 : 0.0;
            bool exhausted = w.Base <= 0 && w.Paid <= 0;
            foreach (CollectorRow row in content.Rows)
            {
                long weight;
                if (row.Category == CollectorContent.Exhausted) weight = exhausted ? 1 : 0;
                else if (CollectorContent.IsBaseCategory(row.Category)) weight = Weight(Weighted(options, units, row), w.BaseShare, w.Base);
                else weight = Weight(Weighted(options, units, row), 1.0 - w.BaseShare, w.Paid);
                w.Rows[row.Id] = weight;
            }
            return w;
        }

        private static double Weighted(CollectorOptions options, CollectorUnits units, CollectorRow row)
        {
            return units.Of(row.Id) * options.Weight(row.Category);
        }

        private static long Weight(double units, double share, double total)
        {
            if (units <= 0 || total <= 0) return 0;
            double scaled = Math.Round(Scale * share * units / total);
            return scaled < 1 ? 1 : (long)scaled;
        }
    }
}
