using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>
    /// Checks a CRM document against what the client's parsers dereference without null checks
    /// (OP/StandardCrm.cs, OP/OfflineStore*.cs, OP/Iap*.cs, OP/PointCut*.cs, EN/CRMTableChanger.cs; report 04 §15).
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
                        var go = g as Dictionary<string, object>;
                        if (go == null || !(JsonValue.Get(go, "name") is string) || !(JsonValue.Get(go, "limit") is List<object>))
                            problems.Add("fatigue_groups entries need a name and a limit array");
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
                var item = list[i] as Dictionary<string, object>;
                if (item == null) { problems.Add("iap.prices[" + i + "] must be an object"); continue; }
                // IapStoreItem.Parse: (bool)Json["hidden"] and (int)Json["quantity"] are hard casts.
                if (!(JsonValue.Get(item, "hidden") is bool)) problems.Add("iap.prices[" + i + "].hidden must be a JSON boolean");
                object q = JsonValue.Get(item, "quantity");
                if (q != null && !JsonValue.AsLong(q).HasValue) problems.Add("iap.prices[" + i + "].quantity must be an integer");
                foreach (object b in JsonValue.GetArray(item, "bundle") ?? new List<object>())
                {
                    if (!JsonValue.AsLong(JsonValue.Get(JsonValue.AsObject(b), "quantity")).HasValue)
                        problems.Add("iap.prices[" + i + "].bundle entries need an integer quantity");
                }
                foreach (object bm in JsonValue.GetArray(item, "billing_methods") ?? new List<object>())
                {
                    if (!JsonValue.AsDouble(JsonValue.Get(JsonValue.AsObject(bm), "price")).HasValue)
                        problems.Add("iap.prices[" + i + "].billing_methods entries need a numeric price");
                }
            }
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
