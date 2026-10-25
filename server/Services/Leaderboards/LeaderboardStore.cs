using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Misc;
using McvsOffline.Server.Services.Social.LiveOps;

namespace McvsOffline.Server.Services.Leaderboards
{
    /// <summary>A score to store (POST leaderboards/{sort}/{name}/me or .../{entry}).</summary>
    public sealed class ScorePost
    {
        public ScorePost()
        {
            Fields = Fed.Obj();
        }

        /// <summary>The entry's credential ("fed_id:&lt;account&gt;" for players).</summary>
        public string Credential { get; set; }

        public long Score { get; set; }

        public string DisplayName { get; set; }

        /// <summary>"higher" / "lower" (replace only if better that way), null = always replace.</summary>
        public string ReplaceIf { get; set; }

        /// <summary>When the entry stops counting (expiration_date / expiration_duration), or null.</summary>
        public DateTime? ExpiresUtc { get; set; }

        /// <summary>Custom fields shown with the entry (_level, _avatar, _fed_id, _clan... or the "global" board's plain keys).</summary>
        public Dictionary<string, object> Fields { get; set; }
    }

    /// <summary>One position in a ranking.</summary>
    public sealed class RankedEntry
    {
        public string Credential;
        public string DisplayName;
        public long Score;
        public int Rank;
        public bool IsVirtual;
        public Dictionary<string, object> Fields;
        internal long Order;

        /// <summary>The entry as Olympus returned it: display_name, credential, index, rank, score + custom fields.</summary>
        public Dictionary<string, object> ToJson()
        {
            var o = Fed.Obj();
            o["display_name"] = DisplayName ?? "";
            o["credential"] = Credential;
            o["index"] = (long)(Rank - 1);
            o["rank"] = (long)Rank;
            o["score"] = Score;
            if (Fields != null)
            {
                foreach (var kv in Fields)
                {
                    if (o.ContainsKey(kv.Key)) continue;
                    o[kv.Key] = JsonValue.DeepClone(kv.Value);
                }
            }
            return o;
        }
    }

    /// <summary>
    /// Olympus leaderboards: one document per board in &lt;data&gt;/leaderboards/ (key "&lt;sort&gt;/&lt;name&gt;"), entries
    /// keyed by credential. Rankings are computed on read: best score first ("desc" boards: highest, "asc": lowest),
    /// ties go to real players before rivals, then to whoever got the score first. Descending boards are completed
    /// with <see cref="VirtualPlayers"/> up to the league group size unless disabled or excluded by settings.
    /// </summary>
    public sealed class LeaderboardStore
    {
        public const string StoreName = "leaderboards";

        private readonly ServerContext context;
        private readonly DocumentStore store;
        private readonly Log log;

        public LeaderboardStore(ServerContext context)
        {
            this.context = context;
            store = context.OpenStore(StoreName);
            log = context.Logger.For("leaderboards");
        }

        public static string BoardKey(string sort, string name)
        {
            return NormalizeSort(sort) + "/" + name;
        }

        public static string NormalizeSort(string sort)
        {
            return string.Equals(sort, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        }

        /// <summary>Stores a score. Returns false when the existing score was kept (replace_score_if).</summary>
        public bool Post(string sort, string name, ScorePost post)
        {
            sort = NormalizeSort(sort);
            DateTime now = context.Clock.UtcNow;
            bool replaced = store.Update(BoardKey(sort, name), doc =>
            {
                doc["name"] = name;
                doc["sort"] = sort;
                Dictionary<string, object> entries = Entries(doc);
                var existing = JsonValue.GetObject(entries, post.Credential);
                if (existing != null && !IsExpired(existing, now) && !ShouldReplace(sort, post.ReplaceIf, JsonValue.GetLong(existing, "score"), post.Score))
                {
                    // Keep the better score but refresh what the entry shows (name, level, avatar...).
                    if (!string.IsNullOrEmpty(post.DisplayName)) existing["display_name"] = post.DisplayName;
                    existing["fields"] = JsonValue.DeepCloneObject(post.Fields);
                    return false;
                }
                var entry = Fed.Obj();
                entry["credential"] = post.Credential;
                entry["display_name"] = post.DisplayName ?? "";
                entry["score"] = post.Score;
                entry["updated"] = TimeFormat.Iso8601Millis(now);
                entry["order"] = now.Ticks;
                entry["expires"] = post.ExpiresUtc.HasValue ? TimeFormat.Iso8601(post.ExpiresUtc.Value) : null;
                entry["fields"] = JsonValue.DeepCloneObject(post.Fields);
                entries[post.Credential] = entry;
                return true;
            });
            log.Info("score " + post.Score.ToString(CultureInfo.InvariantCulture) + " for " + post.Credential + " on " + sort + "/" + name
                     + (replaced ? "" : " kept the previous, better score (replace_score_if=" + post.ReplaceIf + ")"));
            return replaced;
        }

        public bool Delete(string sort, string name, string credential)
        {
            string key = BoardKey(sort, name);
            if (!store.Exists(key)) return false;
            return store.Update(key, doc => JsonPath.RemoveKey(Entries(doc), credential));
        }

        public Dictionary<string, object> GetSettings(string sort, string name)
        {
            Dictionary<string, object> doc = store.Read(BoardKey(sort, name));
            return JsonValue.GetObject(doc, "settings");
        }

        public void SetSettings(string sort, string name, Dictionary<string, object> settings)
        {
            sort = NormalizeSort(sort);
            store.Update(BoardKey(sort, name), doc =>
            {
                doc["name"] = name;
                doc["sort"] = sort;
                doc["settings"] = JsonValue.DeepCloneObject(settings);
            });
        }

        /// <summary>The full ranking of a board (real entries plus rivals), best first, ranks assigned.</summary>
        public List<RankedEntry> Ranking(string sort, string name)
        {
            sort = NormalizeSort(sort);
            DateTime now = context.Clock.UtcNow;
            Dictionary<string, object> doc = store.Read(BoardKey(sort, name));
            var list = new List<RankedEntry>();
            foreach (var kv in Entries(doc))
            {
                var e = kv.Value as Dictionary<string, object>;
                if (e == null || IsExpired(e, now)) continue;
                list.Add(new RankedEntry
                {
                    Credential = JsonValue.GetString(e, "credential") ?? kv.Key,
                    DisplayName = JsonValue.GetString(e, "display_name") ?? "",
                    Score = JsonValue.GetLong(e, "score"),
                    Fields = JsonValue.GetObject(e, "fields"),
                    Order = JsonValue.GetLong(e, "order"),
                });
            }

            int groupSize = GroupSize(doc);
            if (sort == "desc" && UsesVirtualPlayers(name) && list.Count < groupSize)
            {
                foreach (var v in Rivals(sort, name, list, groupSize, now))
                {
                    list.Add(new RankedEntry
                    {
                        Credential = JsonValue.GetString(v, "credential"),
                        DisplayName = JsonValue.GetString(v, "display_name"),
                        Score = JsonValue.GetLong(v, "score"),
                        Fields = v,
                        IsVirtual = true,
                    });
                }
            }

            bool ascending = sort == "asc";
            list.Sort((a, b) =>
            {
                int c = ascending ? a.Score.CompareTo(b.Score) : b.Score.CompareTo(a.Score);
                if (c != 0) return c;
                c = a.IsVirtual.CompareTo(b.IsVirtual); // real players first on ties
                if (c != 0) return c;
                c = a.Order.CompareTo(b.Order);
                return c != 0 ? c : string.CompareOrdinal(a.Credential, b.Credential);
            });
            for (int i = 0; i < list.Count; i++) list[i].Rank = i + 1;
            return list;
        }

        /// <summary>
        /// The rivals completing a board: a division season board gets <see cref="SeasonRivals"/> (calibrated to the
        /// division and the player's level, growing over the season, frozen after it), any other board the static
        /// <see cref="VirtualPlayers"/>.
        /// </summary>
        private List<Dictionary<string, object>> Rivals(string sort, string name, List<RankedEntry> real, int groupSize, DateTime now)
        {
            SeasonBoardInfo season;
            if (LiveOpsService.For(context).TryGetSeasonBoard(name, out season))
            {
                int level = SeasonRivals.PlayerLevel(real, season.DefaultLevel);
                return SeasonRivals.Generate(BoardKey(sort, name), season, groupSize - real.Count, groupSize, level, now);
            }
            return VirtualPlayers.Generate(BoardKey(sort, name), groupSize - real.Count, TopScore());
        }

        /// <summary>Whether simulated rivals complete this board (settings leaderboards.virtualPlayers / virtualExclude).</summary>
        public bool UsesVirtualPlayers(string name)
        {
            if (!context.Settings.GetBool("leaderboards.virtualPlayers", true)) return false;
            List<object> exclude = context.Settings.Get("leaderboards.virtualExclude") as List<object>;
            if (exclude == null) exclude = new List<object> { "global" };
            foreach (object pattern in exclude)
            {
                string p = JsonValue.AsString(pattern);
                if (string.IsNullOrEmpty(p)) continue;
                if (p.EndsWith("*", StringComparison.Ordinal)
                        ? name.StartsWith(p.Substring(0, p.Length - 1), StringComparison.OrdinalIgnoreCase)
                        : string.Equals(name, p, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private int GroupSize(Dictionary<string, object> doc)
        {
            long fromBoard = JsonValue.GetLong(JsonValue.GetObject(doc, "settings"), "group_size", 0);
            long size = fromBoard > 0 ? fromBoard : context.Settings.GetLong("leaderboards.groupSize", 100);
            return (int)Num.Clamp(size, 1, 1000);
        }

        private long TopScore()
        {
            return Num.Clamp(context.Settings.GetLong("leaderboards.virtualTopScore", 400), 0, Num.IntMax);
        }

        private static bool ShouldReplace(string sort, string replaceIf, long current, long candidate)
        {
            if (string.Equals(replaceIf, "higher", StringComparison.OrdinalIgnoreCase)) return candidate > current;
            if (string.Equals(replaceIf, "lower", StringComparison.OrdinalIgnoreCase)) return candidate < current;
            return true;
        }

        private static bool IsExpired(Dictionary<string, object> entry, DateTime now)
        {
            string expires = JsonValue.GetString(entry, "expires");
            DateTime at;
            return !string.IsNullOrEmpty(expires) && TimeFormat.TryParse(expires, out at) && at <= now;
        }

        private static Dictionary<string, object> Entries(Dictionary<string, object> doc)
        {
            if (doc == null) return Fed.Obj();
            var entries = JsonValue.GetObject(doc, "entries");
            if (entries == null)
            {
                entries = Fed.Obj();
                doc["entries"] = entries;
            }
            return entries;
        }
    }
}
