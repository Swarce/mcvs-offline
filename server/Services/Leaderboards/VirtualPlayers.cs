using System;
using System.Collections.Generic;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Names;
using McvsOffline.Server.Services.Misc;

namespace McvsOffline.Server.Services.Leaderboards
{
    /// <summary>
    /// The simulated rivals that fill a leaderboard up to its league group size.
    /// <para>
    /// Why: division seasons promote or demote by rank. At season end the TS reads <c>my_entry.rank</c> and
    /// <c>total_entries</c> (<c>GM/DivisionsComponent.cs OnNewSeasonStartedServer</c>) and applies
    /// <c>TieredDivision.GetStatusForRank</c>: promoted when rank &lt;= floor(PromotionZone% x total), demoted when
    /// rank &gt;= total - floor(DemotionZone% x total) + 1. Alone on a board (rank 1 of 1) a player can never be
    /// promoted (floor(40% x 1) = 0). Olympus put players in league groups of 100 (<c>CreateLeague(groupSize: 100)</c>),
    /// so the board is completed to that size with deterministic rivals (report 04 §9.2: "the player's
    /// division-trophy rank among simulated players").
    /// </para>
    /// <para>
    /// Scores: rival k of n (k = 0 is the best) scores topScore x ((n - k) / (n + 1))^2, jittered by +-10%, so few
    /// rivals score high and many score low. A player needs about 36% of topScore for the top 40%, 64% for the top
    /// 20%, 81% for the top 10%; only a near-zero score falls into the demotion zone. Everything derives from the
    /// board name: the same board always shows the same rivals.
    /// </para>
    /// <para>
    /// Names and emblems (<see cref="RivalIdentity"/>, build 28): from the rival's id (board and slot k) alone, so a rival
    /// keeps them when the number of real players on the board changes; unique on the board.
    /// </para>
    /// </summary>
    public static class VirtualPlayers
    {
        /// <summary><see cref="Generate(string, int, long, GamerTags, IEnumerable{string})"/> with the built-in names and no real players.</summary>
        public static List<Dictionary<string, object>> Generate(string boardKey, int count, long topScore)
        {
            return Generate(boardKey, count, topScore, null, null);
        }

        /// <summary>
        /// <paramref name="count"/> rivals for a board, best first. Each entry has credential, display_name, score and
        /// the custom fields the leaderboard UI reads (_fed_id, _level, _killSignatureEmblem, _killSignatureBg), plus
        /// "_mcvs_bot": true. No rival takes a name of <paramref name="realNames"/> (the real players on the board).
        /// </summary>
        public static List<Dictionary<string, object>> Generate(string boardKey, int count, long topScore, GamerTags tags,
                                                                IEnumerable<string> realNames)
        {
            var result = new List<Dictionary<string, object>>(Num.Max(0, count));
            if (count <= 0) return result;
            ulong state = Seed(boardKey);
            var scores = new List<long>(count);
            for (int k = 0; k < count; k++)
            {
                double fraction = (double)(count - k) / (count + 1);
                double jitter = 0.9 + 0.2 * NextUnit(ref state);
                scores.Add((long)Math.Round(topScore * fraction * fraction * jitter));
            }
            scores.Sort((a, b) => b.CompareTo(a));

            HashSet<string> taken = RivalIdentity.Taken(realNames);
            for (int k = 0; k < count; k++)
            {
                string id = Ids.Sha1Hex("mcvs-offline/leaderboard-rival/v1|" + boardKey + "|" + k).Substring(0, 24);
                string name = RivalIdentity.Name(tags, id, taken);
                SkipNameDraws(ref state);
                // Better-ranked rivals tend to have higher levels (1-50, like the player levels).
                int level = (int)Num.Clamp(50 - (k * 45 / Num.Max(1, count)) - (int)(NextRaw(ref state) % 6), 1, 50);
                var entry = Fed.Obj();
                entry["credential"] = Fed.FedIdOf(id);
                entry["display_name"] = name;
                entry["score"] = scores[k];
                entry["_fed_id"] = Fed.FedIdOf(id);
                entry["_level"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture);
                entry["_mcvs_bot"] = true;
                RivalIdentity.SetEmblem(entry, tags, id);
                result.Add(entry);
            }
            return result;
        }

        /// <summary>
        /// Advances <paramref name="state"/> by the two draws a rival's name took up to build 27 (a callsign and a number).
        /// Names now come from the rival's id (<see cref="RivalIdentity"/>); keeping the draws keeps every level here and
        /// every pace and level of <see cref="SeasonRivals"/> as they were, also in a season that is running.
        /// </summary>
        public static void SkipNameDraws(ref ulong state)
        {
            NextRaw(ref state);
            NextRaw(ref state);
        }

        private static ulong Seed(string text)
        {
            // FNV-1a 64: stable across runtimes (unlike string.GetHashCode or System.Random's seeding).
            ulong hash = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(text ?? ""))
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
            return hash == 0 ? 0x9E3779B97F4A7C15UL : hash;
        }

        private static ulong NextRaw(ref ulong state)
        {
            // xorshift64*
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            return state * 2685821657736338717UL;
        }

        private static double NextUnit(ref ulong state)
        {
            return (NextRaw(ref state) >> 11) * (1.0 / 9007199254740992.0);
        }
    }
}
