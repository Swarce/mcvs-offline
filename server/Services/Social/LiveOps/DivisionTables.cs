using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Social.LiveOps
{
    /// <summary>One division of <c>MCVS_Meta.DivisionsList</c> (GM/TieredDivision.cs).</summary>
    public sealed class DivisionSpec
    {
        public int Id;
        public int PromotionZone;
        public int DemotionZone;
        public int TrophiesWin;
        public int TrophiesLose;
    }

    /// <summary>
    /// The division numbers the rivals are calibrated against, read at run time from the player's own data pack
    /// (<c>Design/MCVS_Meta.xlsx</c>: <c>DivisionsList</c> zones and division trophies, <c>PlayerLevelXP.DivisionTrophiesBonus</c>),
    /// never copied into our content. A division match pays <c>DivisionTrophies.Win + DivisionTrophiesBonus(level)</c> for a
    /// win and <c>DivisionTrophies.Lose</c> for a loss (GM/GameplayComponent.cs ApplyTrophiesResult).
    /// <para>
    /// Only the two tables are cut out of the 1.6 MB file and parsed. While the pack is missing (fresh install, tests
    /// without game data) the catalogue's fallbacks are used (zones 20/10 %, a win worth max(2, level), a loss 0) and
    /// the read is retried at most once a minute.
    /// </para>
    /// </summary>
    public sealed class DivisionTables
    {
        public const string LogicalPath = "Design/MCVS_Meta.xlsx";

        private readonly IClock clock;
        private readonly Func<string> read;
        private readonly Log log;
        private readonly object gate = new object();
        private Dictionary<int, DivisionSpec> divisions;
        private Dictionary<int, int> levelBonus;
        private DateTime nextAttempt = DateTime.MinValue;

        public DivisionTables(IClock clock, Func<string> read, Log log)
        {
            this.clock = clock;
            this.read = read;
            this.log = log ?? Log.Null;
        }

        public bool IsLoaded
        {
            get
            {
                Load();
                lock (gate) return divisions != null;
            }
        }

        /// <summary>The division's spec, or null when the tables are not available.</summary>
        public DivisionSpec Division(int id)
        {
            Load();
            lock (gate)
            {
                DivisionSpec spec;
                return divisions != null && divisions.TryGetValue(id, out spec) ? spec : null;
            }
        }

        /// <summary>DivisionTrophiesBonus of a player level (the nearest listed level), or null without tables.</summary>
        public int? LevelBonus(int level)
        {
            Load();
            lock (gate)
            {
                if (levelBonus == null || levelBonus.Count == 0) return null;
                int bonus;
                if (levelBonus.TryGetValue(level, out bonus)) return bonus;
                int best = -1;
                long bestDistance = Num.LongMax;
                foreach (var kv in levelBonus)
                {
                    long distance = Num.Max(kv.Key - level, level - kv.Key);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = kv.Value;
                    }
                }
                return best;
            }
        }

        private void Load()
        {
            lock (gate)
            {
                if (divisions != null) return;
                DateTime now = clock.UtcNow;
                if (now < nextAttempt) return;
                nextAttempt = now.AddMinutes(1);
            }
            string text = null;
            try
            {
                text = read();
            }
            catch (Exception ex)
            {
                log.Warn("liveops: reading " + LogicalPath + " failed: " + ex.Message);
            }
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var divisionTable = ParseTable(text, "DivisionsList");
                var levels = ParseTable(text, "PlayerLevelXP");
                Dictionary<int, DivisionSpec> parsedDivisions = Divisions(divisionTable);
                Dictionary<int, int> parsedLevels = Levels(levels);
                if (parsedDivisions == null || parsedDivisions.Count == 0)
                {
                    log.Warn("liveops: " + LogicalPath + " has no usable DivisionsList; using fallback zones");
                    return;
                }
                lock (gate)
                {
                    divisions = parsedDivisions;
                    levelBonus = parsedLevels ?? new Dictionary<int, int>();
                }
                log.Info("liveops: division tables read from the data pack (" + parsedDivisions.Count + " divisions, "
                         + (parsedLevels == null ? 0 : parsedLevels.Count) + " levels)");
            }
            catch (Exception ex)
            {
                log.Warn("liveops: " + LogicalPath + " could not be parsed: " + ex.Message);
            }
        }

        private static Dictionary<int, DivisionSpec> Divisions(Dictionary<string, object> table)
        {
            List<object> fields = JsonValue.GetArray(table, "Fields");
            List<object> rows = JsonValue.GetArray(table, "Rows");
            if (fields == null || rows == null) return null;
            int id = fields.IndexOf("Id"), promo = fields.IndexOf("Divisions.PromotionZone"), demo = fields.IndexOf("Divisions.DemotionZone");
            int win = fields.IndexOf("DivisionTrophies.Win"), lose = fields.IndexOf("DivisionTrophies.Lose");
            if (id < 0 || promo < 0 || demo < 0) return null;
            var result = new Dictionary<int, DivisionSpec>();
            foreach (object r in rows)
            {
                var row = r as List<object>;
                if (row == null) continue;
                int? key = Cell(row, id);
                if (!key.HasValue) continue;
                result[key.Value] = new DivisionSpec
                {
                    Id = key.Value,
                    PromotionZone = (int)Num.Clamp(Cell(row, promo) ?? 0, 0, 100),
                    DemotionZone = (int)Num.Clamp(Cell(row, demo) ?? 0, 0, 100),
                    TrophiesWin = Cell(row, win) ?? 0,
                    TrophiesLose = Cell(row, lose) ?? 0,
                };
            }
            return result;
        }

        private static Dictionary<int, int> Levels(Dictionary<string, object> table)
        {
            List<object> fields = JsonValue.GetArray(table, "Fields");
            List<object> rows = JsonValue.GetArray(table, "Rows");
            if (fields == null || rows == null) return null;
            int level = fields.IndexOf("Level"), bonus = fields.IndexOf("DivisionTrophiesBonus");
            if (level < 0 || bonus < 0) return null;
            var result = new Dictionary<int, int>();
            foreach (object r in rows)
            {
                var row = r as List<object>;
                if (row == null) continue;
                int? key = Cell(row, level);
                if (key.HasValue) result[key.Value] = Cell(row, bonus) ?? 0;
            }
            return result;
        }

        private static int? Cell(List<object> row, int index)
        {
            if (index < 0 || index >= row.Count) return null;
            double? d = JsonValue.AsDouble(row[index]);
            if (!d.HasValue || double.IsNaN(d.Value) || d.Value > 1e9 || d.Value < -1e9) return null;
            return (int)Math.Round(d.Value);
        }

        /// <summary>
        /// The object of a top-level key (<c>{"Fields": [...], "Rows": [...]}</c>) cut out of the design file and parsed
        /// on its own, so the rest of the file (other tables) is never materialised. Null when absent.
        /// </summary>
        public static Dictionary<string, object> ParseTable(string text, string name)
        {
            string marker = "\"" + name + "\"";
            int at = 0;
            while ((at = text.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
            {
                int i = at + marker.Length;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i < text.Length && text[i] == ':')
                {
                    i++;
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                    if (i < text.Length && text[i] == '{')
                    {
                        int end = MatchingBrace(text, i);
                        if (end > i)
                        {
                            object parsed;
                            if (MiniJson.TryParse(text.Substring(i, end - i + 1), out parsed)) return parsed as Dictionary<string, object>;
                        }
                        return null;
                    }
                }
                at += marker.Length;
            }
            return null;
        }

        private static int MatchingBrace(string text, int open)
        {
            int depth = 0;
            bool inString = false;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth == 0) return c == '}' ? i : -1;
                }
            }
            return -1;
        }
    }
}
