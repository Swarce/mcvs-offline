using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Misc;
using McvsOffline.Server.Services.Social.LiveOps;

namespace McvsOffline.Server.Services.Leaderboards
{
    /// <summary>
    /// The rivals of a division season board (<c>mcvs_division_&lt;season&gt;_d&lt;nn&gt;_Leaderboard[_QA]</c>): a league group
    /// of 100 whose scores grow over the season, so that promotion takes real play and the result is decided when the
    /// season ends.
    /// <para>
    /// <b>Calibration (final scores).</b> A division match pays <c>w = DivisionTrophies.Win + DivisionTrophiesBonus(level)</c>
    /// for a win and <c>l = DivisionTrophies.Lose</c> for a loss (GM/GameplayComponent.cs ApplyTrophiesResult; values from
    /// the player's data pack, the level from the player's own board entry <c>_level</c>). The rival at the promotion
    /// line (rank P = ⌊PromotionZone% × 100⌋, TieredDivision.GetPromotionMaxRank) ends the season with
    /// <c>S = matchesPerDay[division] × seasonDays × (w + l) / 2 × difficulty × rivalFactor</c>, i.e. as much as a player
    /// at a 50 % win rate playing <c>matchesPerDay</c> matches a day. The best rival ends with <c>topFactor × S</c>, the
    /// rival at the demotion line (rank total − ⌊DemotionZone% × total⌋, GetDemotionMinRank − 1) with
    /// <c>safeFraction × S</c>, the last with 0; values in between follow a convex (top) and linear (bottom) curve with
    /// ±6 % jitter, clamped so the two lines are exact: a player is promoted iff their trophies are at least S (ties
    /// rank real players first), and demoted iff below the safe line. Master (no promotion zone) anchors S at rank
    /// <c>masterTopRank</c>.
    /// </para>
    /// <para>
    /// <b>Progress.</b> Rival i shows <c>final_i × x^a_i</c>, x = elapsed fraction of the season, a_i in [0.8, 1.25]
    /// (some start fast, some finish strong). After the end x = 1: the board is frozen, which is what the TS reads at
    /// the next season start (SelfData on the old board, GM/DivisionsComponent.cs OnNewSeasonStartedServer), however
    /// late that is. Everything derives from the board name, the clock and the player's entry.
    /// </para>
    /// </summary>
    public static class SeasonRivals
    {
        private const double TopCurve = 1.6;

        /// <summary>The rivals (best final score first) as leaderboard entries at <paramref name="now"/>.</summary>
        public static List<Dictionary<string, object>> Generate(string boardKey, SeasonBoardInfo info, int count, int groupSize,
                                                                int playerLevel, DateTime now)
        {
            var result = new List<Dictionary<string, object>>();
            if (count <= 0) return result;
            double[] finals = FinalScores(boardKey, info, count, groupSize, playerLevel);
            double x = info.Season.Progress(now);
            ulong state = LiveOpsHash.Fnv64("mcvs-offline/season-rival/v1|" + boardKey);
            for (int i = 0; i < count; i++)
            {
                double pace = 0.8 + 0.45 * LiveOpsHash.Unit(ref state);
                double shown = x >= 1 ? finals[i] : finals[i] * Math.Pow(x, pace);
                string id = Ids.Sha1Hex("mcvs-offline/season-rival/v1|" + boardKey + "|" + i.ToString(CultureInfo.InvariantCulture)).Substring(0, 24);
                string name = VirtualPlayers.Callsign(ref state);
                int level = (int)Num.Clamp(playerLevel + 4 - (i * 9 / Num.Max(1, count)) - LiveOpsHash.Below(ref state, 5), 1, 50);
                var entry = Fed.Obj();
                entry["credential"] = Fed.FedIdOf(id);
                entry["display_name"] = name;
                entry["score"] = (long)Math.Round(shown);
                entry["_fed_id"] = Fed.FedIdOf(id);
                entry["_level"] = level.ToString(CultureInfo.InvariantCulture);
                entry["_mcvs_bot"] = true;
                result.Add(entry);
            }
            return result;
        }

        /// <summary>The promotion-line score S (see the class remarks), at least 1.</summary>
        public static double PromotionScore(SeasonBoardInfo info, int playerLevel)
        {
            double perMatch = 0.5 * info.TrophiesPerWin(playerLevel) + 0.5 * (info.TrophiesLose > 0 ? info.TrophiesLose : 0);
            double s = info.MatchesPerDay * info.Season.Days * perMatch * info.Difficulty * info.RivalFactor;
            double ceiling = -Math.Floor(-s);
            return ceiling < 1.0 ? 1.0 : ceiling;
        }

        /// <summary>Final scores, best first, with the promotion and demotion lines exact.</summary>
        public static double[] FinalScores(string boardKey, SeasonBoardInfo info, int count, int groupSize, int playerLevel)
        {
            var v = new double[count];
            double promo = PromotionScore(info, playerLevel);
            double top = Math.Max(promo, info.TopFactor * promo);
            double safe = Math.Floor(Math.Min(promo, info.SafeFraction * promo));

            int promotionRank = (int)Math.Floor(info.PromotionZone * groupSize / 100.0);
            if (promotionRank < 1) promotionRank = info.MasterTopRank;
            int iP = (int)Num.Clamp(promotionRank - 1, 0, count - 1);
            int iD = -1;
            if (info.DemotionZone > 0)
            {
                int demotionMinRank = groupSize - (int)Math.Floor(info.DemotionZone * groupSize / 100.0) + 1;
                iD = demotionMinRank - 2;
                if (iD <= iP || iD > count - 1) iD = -1;
            }

            ulong state = LiveOpsHash.Fnv64("mcvs-offline/season-curve/v1|" + boardKey);
            for (int i = 0; i < count; i++)
            {
                double value;
                if (i <= iP)
                {
                    double t = iP == 0 ? 1.0 : (double)i / iP;
                    value = promo + (top - promo) * Math.Pow(1.0 - t, TopCurve);
                }
                else if (iD > 0 && i <= iD)
                {
                    value = promo + (safe - promo) * (i - iP) / (double)(iD - iP);
                }
                else
                {
                    int from = iD > 0 ? iD : iP;
                    double start = iD > 0 ? safe : promo;
                    int span = (int)Num.Max(1, count - 1 - from);
                    value = start * (1.0 - (i - from) / (double)span);
                }
                double jitter = 0.94 + 0.12 * LiveOpsHash.Unit(ref state);
                v[i] = Math.Max(0.0, value * jitter);
            }
            // The lines are exact: everyone above a line stays at or above it, everyone below stays below.
            for (int i = 0; i < count; i++)
            {
                if (i < iP) v[i] = Math.Max(v[i], promo);
                else if (i == iP) v[i] = promo;
                else v[i] = Math.Min(v[i], promo - 1);
                if (iD > 0)
                {
                    if (i < iD) v[i] = Math.Max(v[i], safe);
                    else if (i == iD) v[i] = safe;
                    else v[i] = Math.Min(v[i], Math.Max(0.0, safe - 1));
                }
                v[i] = Math.Max(0.0, Math.Round(v[i]));
            }
            var sorted = new List<double>(v);
            sorted.Sort((a, b) => b.CompareTo(a));
            return sorted.ToArray();
        }

        /// <summary>The level the board is calibrated for: the best real player's <c>_level</c>, else the default.</summary>
        public static int PlayerLevel(IEnumerable<RankedEntry> realEntries, int defaultLevel)
        {
            int best = 0;
            foreach (RankedEntry e in realEntries)
            {
                long? level = JsonValue.AsLong(JsonValue.Get(e.Fields, "_level"));
                if (level.HasValue && level.Value > best) best = (int)Num.Clamp(level.Value, 1, 1000);
            }
            return best > 0 ? best : defaultLevel;
        }
    }
}
