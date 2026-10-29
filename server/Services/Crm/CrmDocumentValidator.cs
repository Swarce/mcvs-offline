using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm.Collector;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>
    /// Checks a CRM document against what the client's parsers dereference without null checks
    /// (OP/StandardCrm.cs, OP/OfflineStore*.cs, OP/Iap*.cs, OP/PointCut*.cs, OP/NewsFeedData.cs, EN/CRMTableChanger.cs;
    /// report 04 §15).
    /// A 200 with a broken document is worse than an error: the TS has no fallback once it got a 200 (verify-C2 §1.7)
    /// and a single bad offline_store entry makes UnifiedStoreComponent throw for the whole shop. The builder uses
    /// this to drop bad store entries and to log everything else, so a tester's override cannot silently break CRM.
    /// </summary>
    public static class CrmDocumentValidator
    {
        /// <summary>
        /// Reward types the client resolves by reflection as "&lt;Type&gt;RewardItem" (Game.dll). Reset* types wipe data and
        /// must never be offered; MercPortrait is obsolete.
        /// </summary>
        private static readonly HashSet<string> RewardTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "AgentsCurrency", "Crate", "CrateTimerReduction", "Credits", "Daily", "DailyShop", "Gear", "GearSlot",
            "HardCurrency", "KillSignature", "LotteryCurrency", "MercAccessory", "MercSkin", "MercWeapon",
            "MercWeaponFragment", "Mod", "ModTier", "OpenFreeCrate", "PlayerXP", "SeasonTickets", "Shards", "SoftCurrency",
            "SubscriptionCard", "SubscriptionMultiplierCurrency", "TrialMerc", "TrialWeapon", "UnlockMerc", "UpgradeTokens",
            "VersusCoins", "WeaponCamo",
        };

        /// <summary>True when the client can build a reward from <paramref name="rewardId"/> and it is safe to grant.</summary>
        public static bool IsKnownRewardId(string rewardId)
        {
            if (string.IsNullOrEmpty(rewardId)) return false;
            string id = rewardId.Trim();
            if (id.EndsWith("_Bonus", StringComparison.Ordinal)) id = id.Substring(0, id.Length - "_Bonus".Length);
            int underscore = id.IndexOf('_');
            string type = underscore < 0 ? id : id.Substring(0, underscore);
            return RewardTypes.Contains(type);
        }

        public static List<string> Validate(Dictionary<string, object> doc)
        {
            var problems = new List<string>();
            if (doc == null)
            {
                problems.Add("document is not an object");
                return problems;
            }

            object expiry;
            if (doc.TryGetValue("expiry", out expiry) && expiry != null && !(expiry is string))
                problems.Add("expiry must be an ISO 8601 string or null");

            var game = JsonValue.GetObject(doc, "game");
            if (game == null) problems.Add("game must be an object (GameProfileSection/AgeGateChecker dereference it)");
            else ValidateGame(game, problems);

            var store = JsonValue.GetObject(doc, "offline_store");
            if (store == null) problems.Add("offline_store must be an object with a prices array");
            else
            {
                var prices = JsonValue.GetArray(store, "prices");
                if (prices == null) problems.Add("offline_store.prices must be an array");
                else
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < prices.Count; i++)
                    {
                        string p = CheckStoreItem(prices[i]);
                        if (p != null) problems.Add("offline_store.prices[" + i + "]: " + p);
                        string name = CrmDocumentBuilder.ItemName(prices[i]);
                        if (name != null && !names.Add(name)) problems.Add("offline_store.prices[" + i + "]: duplicate name " + name + " (only the first is used)");
                    }
                }
                if (store.ContainsKey("promotion") && !(store["promotion"] is Dictionary<string, object>))
                    problems.Add("offline_store.promotion must be an object or absent (never null)");
            }

            var iap = JsonValue.GetObject(doc, "iap");
            if (iap == null) problems.Add("iap must be an object (CrmConfig.IapStore.Items is read without null checks)");
            else ValidateIap(iap, problems);

            object pcs;
            if (doc.TryGetValue("pointcut_actions", out pcs) && pcs != null)
            {
                var list = pcs as List<object>;
                if (list == null) problems.Add("pointcut_actions must be an array");
                else
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        string p = CheckPointcutAction(list[i]);
                        if (p != null) problems.Add("pointcut_actions[" + i + "]: " + p);
                    }
                }
            }

            object fatigue;
            if (doc.TryGetValue("fatigue_groups", out fatigue) && fatigue != null)
            {
                var list = fatigue as List<object>;
                if (list == null) problems.Add("fatigue_groups must be an array");
                else
                {
                    foreach (object g in list)
                    {
                        if (CheckFatigueGroup(g) != null) problems.Add("fatigue_groups entries need a name and a limit array");
                    }
                }
            }

            var events = JsonValue.AsObject(JsonPath.Get(doc, "groupware.events"));
            if (events != null)
            {
                foreach (string key in new[] { "participation", "advertisement" })
                {
                    var list = JsonValue.GetArray(events, key);
                    if (list == null) continue;
                    foreach (object e in list)
                    {
                        if (string.IsNullOrEmpty(JsonValue.GetString(JsonValue.AsObject(e), "id")))
                            problems.Add("groupware.events." + key + " entries need an id");
                    }
                }
            }

            object news;
            if (doc.TryGetValue("newsfeeds", out news))
            {
                var list = news as List<object>;
                if (list == null) problems.Add("newsfeeds must be an array (NewsFeedData enumerates it; null or an object throws)");
                else
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        string p = CheckNewsfeed(list[i]);
                        if (p != null) problems.Add("newsfeeds[" + i + "]: " + p);
                    }
                }
            }
            return problems;
        }

        private static void ValidateGame(Dictionary<string, object> game, List<string> problems)
        {
            var tables = JsonValue.GetObject(game, "_OverrideTables");
            if (game.ContainsKey("_OverrideTables") && tables == null) problems.Add("game._OverrideTables must be an object");
            if (tables != null) ValidateOverrideTables(tables, "game._OverrideTables", problems);

            var serverConfig = JsonValue.GetObject(game, "_ServerConfig");
            if (serverConfig == null)
            {
                problems.Add("game._ServerConfig must be an object (profile creation casts it to JObject)");
                return;
            }
            foreach (var kv in serverConfig)
            {
                if (!(kv.Value is Dictionary<string, object>))
                    problems.Add("game._ServerConfig." + kv.Key + " must be an object (GameProfileSection casts it)");
            }
            var def = JsonValue.GetObject(serverConfig, "default");
            if (def == null)
            {
                problems.Add("game._ServerConfig.default must be an object");
                return;
            }
            if (!(JsonValue.Get(def, "IsDevelopment") is bool))
                problems.Add("game._ServerConfig.default.IsDevelopment must be a JSON boolean (the dev menu is on by default, verify-C5)");
            var mm = JsonValue.Get(def, "Matchmaking");
            if (mm != null && !(mm is Dictionary<string, object>)) problems.Add("game._ServerConfig.default.Matchmaking must be an object");
            foreach (string key in new[] { "UnsupportedLinkFrom", "UnsupportedLinkTo", "RegionsAllowed", "ValidateDatacenterForCountries" })
            {
                object v = JsonValue.Get(def, key);
                if (v != null && !(v is List<object>)) problems.Add("game._ServerConfig.default." + key + " must be an array");
            }
            var mirrored = JsonValue.GetObject(def, "_OverrideTables");
            if (mirrored != null) ValidateOverrideTables(mirrored, "game._ServerConfig.default._OverrideTables", problems);
        }

        private static void ValidateOverrideTables(Dictionary<string, object> tables, string where, List<string> problems)
        {
            foreach (var db in tables)
            {
                var dbObj = db.Value as Dictionary<string, object>;
                if (dbObj == null) { problems.Add(where + "." + db.Key + " must be an object"); continue; }
                foreach (var table in dbObj)
                {
                    var tableObj = table.Value as Dictionary<string, object>;
                    if (tableObj == null) { problems.Add(where + "." + db.Key + "." + table.Key + " must be an object"); continue; }
                    foreach (var field in tableObj)
                    {
                        if (field.Key.Split('@').Length != 2)
                            problems.Add(where + "." + db.Key + "." + table.Key + ": field changer '" + field.Key + "' needs exactly one '@'");
                        var cells = field.Value as Dictionary<string, object>;
                        if (cells == null) { problems.Add(where + "." + db.Key + "." + table.Key + "." + field.Key + " must be an object"); continue; }
                        foreach (var cell in cells)
                        {
                            object v = cell.Value;
                            if (v != null && !(v is string) && !JsonValue.IsNumber(v) && !(v is bool))
                                problems.Add(where + "." + db.Key + "." + table.Key + "." + field.Key + "." + cell.Key + " must be a string or a number");
                        }
                    }
                }
            }
        }

        private static void ValidateIap(Dictionary<string, object> iap, List<string> problems)
        {
            object prices;
            if (!iap.TryGetValue("prices", out prices) || prices == null) return; // tolerated: IapStoreData checks "is JArray"
            var list = prices as List<object>;
            if (list == null) { problems.Add("iap.prices must be an array"); return; }
            for (int i = 0; i < list.Count; i++)
            {
                string p = CheckIapItem(list[i]);
                if (p != null) problems.Add("iap.prices[" + i + "]: " + p);
            }
        }

        /// <summary>Why OP/IapStoreItem.Parse would throw on this iap entry, or null when it is fine.</summary>
        public static string CheckIapItem(object entry)
        {
            var item = entry as Dictionary<string, object>;
            if (item == null) return "must be an object";
            // IapStoreItem.Parse: (bool)Json["hidden"], (int)Json["quantity"], (float)Json["price"] are hard casts.
            if (!(JsonValue.Get(item, "hidden") is bool)) return "hidden must be a JSON boolean";
            object q = JsonValue.Get(item, "quantity");
            if (q != null && !JsonValue.AsLong(q).HasValue) return "quantity must be an integer";
            foreach (object b in JsonValue.GetArray(item, "bundle") ?? new List<object>())
            {
                if (!JsonValue.AsLong(JsonValue.Get(JsonValue.AsObject(b), "quantity")).HasValue) return "bundle entries need an integer quantity";
            }
            foreach (object bm in JsonValue.GetArray(item, "billing_methods") ?? new List<object>())
            {
                if (!JsonValue.AsDouble(JsonValue.Get(JsonValue.AsObject(bm), "price")).HasValue) return "billing_methods entries need a numeric price";
            }
            return null;
        }

        /// <summary>
        /// Repairs what can be repaired without guessing: drops pointcut actions, fatigue groups, iap items and event
        /// ids the client parsers would throw on (a single one fails the whole CRM refresh on the TS, which then has no
        /// fallback), and a malformed expiry; makes <c>newsfeeds</c> what NewsFeedData reads (<see cref="SanitizeNewsfeeds"/>).
        /// Returns the number of removals; each is logged.
        /// </summary>
        public static int Sanitize(Dictionary<string, object> doc, Log log)
        {
            log = log ?? Log.Null;
            int removed = 0;
            object value;
            if (doc.TryGetValue("expiry", out value) && value != null && !(value is string))
            {
                JsonPath.RemoveKey(doc, "expiry");
                log.Warn("CRM: removed a non-string expiry");
                removed++;
            }
            if (doc.TryGetValue("pointcut_actions", out value))
            {
                var list = value as List<object>;
                if (list == null)
                {
                    doc["pointcut_actions"] = new List<object>();
                    log.Warn("CRM: pointcut_actions was not an array; replaced with []");
                    removed++;
                }
                else removed += RemoveInvalid(list, CheckPointcutAction, "pointcut action", "id", log);
            }
            if (doc.TryGetValue("fatigue_groups", out value) && value != null)
            {
                var list = value as List<object>;
                if (list == null)
                {
                    JsonPath.RemoveKey(doc, "fatigue_groups");
                    log.Warn("CRM: fatigue_groups was not an array; removed");
                    removed++;
                }
                else removed += RemoveInvalid(list, CheckFatigueGroup, "fatigue group", "name", log);
            }
            var iapPrices = JsonValue.AsArray(JsonPath.Get(doc, "iap.prices"));
            if (iapPrices != null) removed += RemoveInvalid(iapPrices, CheckIapItem, "iap item", "_id", log);
            var events = JsonValue.AsObject(JsonPath.Get(doc, "groupware.events"));
            if (events != null)
            {
                foreach (string key in new[] { "participation", "advertisement" })
                {
                    var list = JsonValue.GetArray(events, key);
                    if (list != null) removed += RemoveInvalid(list, CheckEventReference, "event reference", "id", log);
                }
            }
            if (doc.TryGetValue("newsfeeds", out value))
            {
                var list = value as List<object>;
                if (list == null)
                {
                    // StandardCrm.Data deserializes any key that is present: null or an object makes NewsFeedData throw
                    // for the whole document.
                    doc["newsfeeds"] = new List<object>();
                    log.Warn("CRM: newsfeeds was not an array; replaced with []");
                    removed++;
                }
                else removed += SanitizeNewsfeeds(list, log);
            }
            return removed;
        }

        /// <summary>The text members of a newsfeeds entry (NewsFeedData reads each with SafeGetString).</summary>
        private static readonly string[] NewsfeedTextFields = { "news_id", "title", "body", "redirect", "asset", "youtube" };

        /// <summary>
        /// Makes every <c>newsfeeds</c> entry what OP/NewsFeedData.cs reads, with the value the client would have read:
        /// a text member that is not a string becomes the client's text of it (<c>SafeGetString</c>: numbers and
        /// booleans through <c>Convert.ChangeType</c>, anything else ""); <c>timestamp</c> becomes integer epoch seconds
        /// (the client parses its text with <c>Convert.ToInt64</c>; a fraction is cut off here, and a value without a
        /// number becomes 0, which the client, like the unparseable original, treats as older than 30 days). Entries
        /// that are not objects or have no <c>news_id</c> (the inbox id is "newsfeed_" + news_id) are removed. Returns
        /// the number of removals; every change is logged.
        /// </summary>
        private static int SanitizeNewsfeeds(List<object> list, Log log)
        {
            foreach (object e in list)
            {
                var entry = e as Dictionary<string, object>;
                if (entry == null) continue;
                foreach (string key in NewsfeedTextFields)
                {
                    object v;
                    if (!entry.TryGetValue(key, out v) || v is string) continue;
                    string text = ClientText(v);
                    entry[key] = text;
                    log.Warn("CRM: newsfeed '" + (JsonValue.GetString(entry, "news_id") ?? "?") + "': " + key + " was not a string; now \""
                             + text + "\"");
                }
                object timestamp;
                if (entry.TryGetValue("timestamp", out timestamp) && !(timestamp is long) && !(timestamp is int))
                {
                    long seconds = EpochSeconds(timestamp);
                    entry["timestamp"] = seconds;
                    log.Warn("CRM: newsfeed '" + (JsonValue.GetString(entry, "news_id") ?? "?")
                             + "': timestamp was not integer epoch seconds; now " + seconds.ToString(CultureInfo.InvariantCulture));
                }
            }
            int removed = RemoveInvalid(list, CheckNewsfeedEntry, "newsfeed", "news_id", log);
            WarnSharedAssets(list, log);
            return removed;
        }

        /// <summary>
        /// Warns (and changes nothing) when two newsfeed items share an <c>asset</c> and the first has no <c>end_date</c>:
        /// the PC client keys its static <c>NewsFeedData.Newsfeeds</c> by asset (first item wins) and gives every inbox
        /// message of that asset whose creation date differs the first item's <c>EndDate</c>, <c>default(DateTime)</c>
        /// without an <c>end_date</c>, so those messages expire at the next refresh (docs/design/pc-port.md §4.3).
        /// </summary>
        private static void WarnSharedAssets(List<object> list, Log log)
        {
            var first = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            foreach (object e in list)
            {
                var entry = e as Dictionary<string, object>;
                if (entry == null) continue;
                string asset = JsonValue.GetString(entry, "asset") ?? "";
                Dictionary<string, object> earlier;
                if (!first.TryGetValue(asset, out earlier))
                {
                    first[asset] = entry;
                    continue;
                }
                if (!string.IsNullOrEmpty(JsonValue.GetString(earlier, "end_date"))) continue;
                log.Warn("CRM: newsfeed '" + (JsonValue.GetString(entry, "news_id") ?? "?") + "' shares asset \"" + asset + "\" with '"
                         + (JsonValue.GetString(earlier, "news_id") ?? "?") + "', which has no end_date: on PC its inbox message "
                         + "expires at the next refresh (give each item its own asset, or the first one an end_date)");
            }
        }

        /// <summary>Why the client would make no proper inbox message of this newsfeeds entry, or null when it is fine.</summary>
        public static string CheckNewsfeed(object entry)
        {
            string problem = CheckNewsfeedEntry(entry);
            if (problem != null) return problem;
            var item = (Dictionary<string, object>)entry;
            foreach (string key in NewsfeedTextFields)
            {
                object v = JsonValue.Get(item, key);
                if (v != null && !(v is string)) return key + " must be a string";
            }
            object timestamp = JsonValue.Get(item, "timestamp");
            if (timestamp != null && !(timestamp is long) && !(timestamp is int)) return "timestamp must be integer epoch seconds";
            return null;
        }

        /// <summary>The problems Sanitize removes an entry for (after the members were converted).</summary>
        private static string CheckNewsfeedEntry(object entry)
        {
            var item = entry as Dictionary<string, object>;
            if (item == null) return "not an object";
            return string.IsNullOrEmpty(JsonValue.Get(item, "news_id") as string) ? "needs a news_id" : null;
        }

        /// <summary>What SafeGetString gives for a JSON value that is not a string (Convert.ChangeType, invariant culture).</summary>
        private static string ClientText(object value)
        {
            if (value is bool) return (bool)value ? "True" : "False";
            if (JsonValue.IsNumber(value)) return JsonValue.AsString(value) ?? "";
            return ""; // null, objects and arrays: SafeGet catches the failed JValue cast and returns the default
        }

        private static long EpochSeconds(object value)
        {
            double? d = value is bool ? null : JsonValue.AsDouble(value);
            if (!d.HasValue || double.IsNaN(d.Value) || double.IsInfinity(d.Value) || Math.Abs(d.Value) > 9e18) return 0;
            return (long)Math.Floor(d.Value);
        }

        private static string CheckFatigueGroup(object entry)
        {
            var g = entry as Dictionary<string, object>;
            if (g == null || !(JsonValue.Get(g, "name") is string) || !(JsonValue.Get(g, "limit") is List<object>))
                return "needs a name and a limit array";
            return null;
        }

        private static string CheckEventReference(object entry)
        {
            return string.IsNullOrEmpty(JsonValue.GetString(JsonValue.AsObject(entry), "id")) ? "needs an id" : null;
        }

        private static int RemoveInvalid(List<object> list, Func<object, string> check, string what, string idKey, Log log)
        {
            int removed = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                string problem = check(list[i]);
                if (problem == null) continue;
                log.Warn("CRM: " + what + " '" + (JsonValue.GetString(JsonValue.AsObject(list[i]), idKey) ?? "?") + "' removed: " + problem);
                list.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        /// <summary>Why the client would fail on this offline_store entry, or null when it is fine.</summary>
        public static string CheckStoreItem(object entry)
        {
            var item = entry as Dictionary<string, object>;
            if (item == null) return "not an object";
            object category = JsonValue.Get(item, "category");
            if (category != null)
            {
                var cats = category as List<object>;
                if (cats == null) return "category must be an array of strings";
                foreach (object c in cats) if (!(c is string)) return "category must be an array of strings";
            }
            var methods = JsonValue.GetArray(item, "billing_methods");
            // UnifiedShopOfflineCrmItem takes BillingMethods.First(): an empty list throws for the whole store.
            if (methods == null || methods.Count == 0) return "billing_methods must be a non-empty array";
            foreach (object m in methods)
            {
                var method = m as Dictionary<string, object>;
                if (method == null) return "billing_methods entries must be objects";
                var price = JsonValue.GetArray(method, "price");
                if (price == null) return "billing_methods[].price must be an array";
                object replaced = JsonValue.Get(method, "replaced_price");
                if (replaced != null && !(replaced is List<object>)) return "billing_methods[].replaced_price must be an array";
                foreach (object p in price)
                {
                    var po = p as Dictionary<string, object>;
                    if (po == null || !(JsonValue.Get(po, "currency") is string) || !JsonValue.AsDouble(JsonValue.Get(po, "price")).HasValue)
                        return "price entries need a currency string and a numeric price";
                }
            }
            if (item.ContainsKey("promotion") && !(item["promotion"] is Dictionary<string, object>))
                return "promotion must be an object or absent (never null)";

            string group = JsonValue.GetString(item, "group");
            if (!string.IsNullOrEmpty(group))
            {
                var bundle = JsonValue.GetArray(item, "bundle");
                if (bundle == null || bundle.Count == 0) return "group " + group + " needs a non-empty bundle array";
                foreach (object b in bundle)
                {
                    string reward = JsonValue.GetString(JsonValue.AsObject(b), "item");
                    if (!IsKnownRewardId(reward)) return "group " + group + " has an unknown or unsafe reward '" + reward + "'";
                }
                return null;
            }
            string id = JsonValue.GetString(item, "item");
            if (string.IsNullOrEmpty(id)) return "needs an item (or a group with a bundle)";
            if (!IsKnownRewardId(id)) return "unknown or unsafe reward '" + id + "'";
            return null;
        }

        /// <summary>Drops entries the client cannot parse; returns how many were removed.</summary>
        public static int RemoveInvalidStoreItems(List<object> prices, Log log)
        {
            int removed = 0;
            for (int i = prices.Count - 1; i >= 0; i--)
            {
                string problem = CheckStoreItem(prices[i]);
                if (problem == null) continue;
                (log ?? Log.Null).Warn("CRM: offline_store entry '" + (CrmDocumentBuilder.ItemName(prices[i]) ?? "?") + "' removed: " + problem);
                prices.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// The collector's invariants on one <c>_OverrideTables</c> tree of the merged document (crm.overlay included;
        /// docs/design/economy-collector.md §2.8), for the cells the collector wrote into it (<paramref name="cells"/>: its
        /// layer or the guard):
        /// <list type="bullet">
        /// <item>every collector cell still there is a non-empty string or an integral number (CRMTableCell throws on "",
        /// the typed readers re-read weights and counts as integers);</item>
        /// <item>every <c>Crate_Event_Special</c> row holding a <c>Weight#N</c> cell also holds a non-empty <c>Id@Type</c>
        /// (the shipped rows have none: <c>CrateRewards.FillDataWithTableImpl</c> would throw a NullReferenceException in
        /// the TS's table refill);</item>
        /// <item>every row with <c>Weight#1 &gt; 0</c> passes the preview-safety rule (§2.3.1,
        /// <see cref="CollectorContent.RowProblem"/>);</item>
        /// <item>at least one row has <c>Weight#1 &gt; 0</c> (an empty content table throws on claim, §0.3.6).</item>
        /// </list>
        /// Returns the problems, each prefixed with <paramref name="where"/>; empty when the tree is fine.
        /// </summary>
        public static List<string> CheckCollector(Dictionary<string, object> tables, IEnumerable<CollectorCell> cells, string where)
        {
            var problems = new List<string>();
            var db = JsonValue.AsObject(JsonValue.Get(tables, CollectorPlan.Database));
            if (cells != null)
            {
                foreach (CollectorCell cell in cells)
                {
                    var changer = JsonValue.AsObject(JsonValue.Get(JsonValue.AsObject(JsonValue.Get(db, cell.Table)), cell.Changer));
                    object value;
                    if (changer == null || !changer.TryGetValue(cell.Key, out value)) continue;
                    if (!IsCellValue(value))
                        problems.Add(where + "." + CollectorPlan.Database + "." + cell.Table + "." + cell.Changer + "." + cell.Key
                                     + " must be a non-empty string or an integer (it is " + Describe(value) + ")");
                }
            }

            var crate = JsonValue.AsObject(JsonValue.Get(db, CollectorGuard.CrateTable));
            var rows = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var weighted = new HashSet<string>(StringComparer.Ordinal);
            if (crate != null)
            {
                foreach (var field in crate)
                {
                    var cellsOfField = field.Value as Dictionary<string, object>;
                    if (cellsOfField == null) continue;
                    bool weight = field.Key.StartsWith("Id@Weight#", StringComparison.Ordinal);
                    foreach (string row in cellsOfField.Keys)
                    {
                        if (seen.Add(row)) rows.Add(row);
                        if (weight) weighted.Add(row);
                    }
                }
            }
            string at = where + "." + CollectorPlan.Database + "." + CollectorGuard.CrateTable;
            int drawable = 0;
            foreach (string row in rows)
            {
                string type = CrateText(crate, "Id@Type", row);
                if (weighted.Contains(row) && string.IsNullOrEmpty(type))
                {
                    problems.Add(at + " row " + row + " has a weight but no Id@Type (the TS's table refill would throw)");
                    continue;
                }
                object w;
                var weights = JsonValue.AsObject(JsonValue.Get(crate, "Id@Weight#1"));
                if (weights == null || !weights.TryGetValue(row, out w) || w == null) continue;
                double? weight = WeightOf(w);
                if (!weight.HasValue)
                {
                    problems.Add(at + " row " + row + ": Id@Weight#1 is not a number (" + Describe(w) + ")");
                    continue;
                }
                if (weight.Value <= 0) continue;
                string rowProblem = CollectorContent.RowProblem(type, CrateText(crate, "Id@Subtype", row), CrateText(crate, "Id@Subtype2", row),
                                                                CrateText(crate, "Id@BundledRewards", row));
                if (rowProblem != null) problems.Add(at + " row " + row + ": " + rowProblem);
                else drawable++;
            }
            if (drawable == 0) problems.Add(at + " has no row with Weight#1 > 0 (an empty crate throws when it is claimed)");
            return problems;
        }

        /// <summary>A cell value the collector may leave in a document: a non-empty string or an integral number.</summary>
        private static bool IsCellValue(object value)
        {
            string s = value as string;
            if (s != null) return s.Length > 0;
            if (value is bool || !JsonValue.IsNumber(value)) return false;
            if (value is long || value is int) return true;
            double? d = JsonValue.AsDouble(value);
            return d.HasValue && !double.IsNaN(d.Value) && !double.IsInfinity(d.Value) && Math.Floor(d.Value) == d.Value;
        }

        /// <summary>A content-table cell as the client's typed reader sees it (numbers by their invariant text); null when absent.</summary>
        private static string CrateText(Dictionary<string, object> crate, string field, string row)
        {
            var cells = JsonValue.AsObject(JsonValue.Get(crate, field));
            object value;
            if (cells == null || !cells.TryGetValue(row, out value) || value == null || value is bool) return null;
            string text = JsonValue.AsString(value);
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>A weight cell as <c>float.Parse(cell.ToString(), Invariant)</c> reads it; null for anything else (an
        /// operation such as "*2", a bool).</summary>
        private static double? WeightOf(object value)
        {
            if (value is bool) return null;
            if (JsonValue.IsNumber(value)) return JsonValue.AsDouble(value);
            double d;
            string s = value as string;
            if (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        private static string Describe(object value)
        {
            if (value == null) return "null";
            if (value is string) return "\"" + value + "\"";
            if (value is bool) return (bool)value ? "true" : "false";
            return JsonValue.AsString(value) ?? value.GetType().Name;
        }

        /// <summary>Why OP/PointCutAction would throw on this entry, or null when it is fine.</summary>
        public static string CheckPointcutAction(object entry)
        {
            var action = entry as Dictionary<string, object>;
            if (action == null) return "not an object";
            var pointcuts = JsonValue.GetArray(action, "pointcuts");
            var limit = JsonValue.GetArray(action, "limit");
            if (pointcuts == null) return "pointcuts must be an array";
            if (limit == null) return "limit must be an array";
            foreach (object pc in pointcuts)
            {
                var p = pc as Dictionary<string, object>;
                if (p == null) return "pointcuts entries must be objects";
                foreach (object c in JsonValue.GetArray(p, "conditions") ?? new List<object>())
                {
                    string cond = JsonValue.AsString(c) ?? "";
                    if (cond.Split(' ').Length < 3) return "condition '" + cond + "' must read '<field> <op> <value>'";
                }
            }
            foreach (object l in limit)
            {
                if (!(l is Dictionary<string, object>)) return "limit entries must be objects";
            }
            string item = JsonValue.GetString(action, "item");
            if (item != null && !IsKnownRewardId(item)) return "unknown or unsafe reward '" + item + "'";
            return null;
        }
    }
}
