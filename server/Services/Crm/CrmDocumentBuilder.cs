using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>
    /// Builds the complete CRM (Hestia) document returned by <c>configs/users/me</c>. Layers, last wins:
    /// <list type="number">
    /// <item>the player's <c>Config/crmpreconfiguration.json</c> (may be absent);</item>
    /// <item>crm_overlay.json (static game parameters we always send);</item>
    /// <item><c>offline_store</c> = our VersusCoins catalogue (mode <c>crm.offlineStore</c>), prices × economy.priceMultiplier;</item>
    /// <item><c>iap</c> = <c>{"prices":[]}</c> or the preconfiguration catalogue of one language (<c>crm.iap</c>);</item>
    /// <item>settings: <c>_ServerConfig.default.IsDevelopment</c> = devMenu (explicitly false otherwise: the dev cheat menu
    /// is on by default over loopback, verify-C5 (a)), <c>Matchmaking {MinPlayers:1, MaxOpenTimeInSeconds:0}</c> when
    /// instantBots (verify-C4 §3.4), age gate, the RedeemCoupons switch, economy multipliers as <c>_OverrideTables</c>;</item>
    /// <item><c>crm.overlay</c> from settings.json.</item>
    /// </list>
    /// Finally <c>game._OverrideTables</c> is pruned of empty/invalid changers and mirrored into
    /// <c>_ServerConfig.default._OverrideTables</c>: the TS applies the former, the GS applies and forwards the latter
    /// (report 04 §4.2: "put the same block in both places").
    /// </summary>
    public static class CrmDocumentBuilder
    {
        /// <summary>Ad pointcut types: the ad SDK cannot work offline, and they only add ad buttons.</summary>
        private static readonly string[] AdActionTypes = { "show_ad", "show_ad_button", "show_ad_banner" };

        public static Dictionary<string, object> Build(Dictionary<string, object> preconfig, CrmContent content, CrmOptions options, Log log)
        {
            log = log ?? Log.Null;
            content = content ?? new CrmContent();
            options = options ?? new CrmOptions();

            Dictionary<string, object> pre = preconfig == null ? null : JsonValue.DeepCloneObject(preconfig);
            var doc = pre ?? NewObject();

            ApplyPointcuts(doc, options, log);

            var overlay = JsonValue.DeepCloneObject(content.Overlay);
            object overlayIap;
            overlay.TryGetValue("iap", out overlayIap);
            JsonPath.RemoveKey(overlay, "iap");                       // handled by ApplyIap (the preconfig iap is per language)
            JsonPath.RemoveKey(overlay, "offline_store");             // handled by ApplyOfflineStore
            JsonPath.DeepMerge(doc, overlay);

            ApplyOfflineStore(doc, pre, content.OfflineStore, options, log);
            ApplyIap(doc, pre, overlayIap as Dictionary<string, object>, options, log);

            var game = ChildObject(doc, "game");
            ApplyGameParameters(game, options);
            ApplyMultipliers(ChildObject(game, "_OverrideTables"), content.Economy, options, log);
            ApplyEvents(doc, options);

            if (options.Overlay != null) JsonPath.DeepMerge(doc, options.Overlay);

            // The overlay may have replaced "game"; re-resolve before the final passes.
            game = ChildObject(doc, "game");
            var tables = ChildObject(game, "_OverrideTables");
            PruneOverrideTables(tables, "game._OverrideTables", log);
            var serverDefault = ChildObject(ChildObject(game, "_ServerConfig"), "default");
            var mirrored = JsonValue.DeepCloneObject(tables);
            var existing = JsonValue.AsObject(JsonValue.Get(serverDefault, "_OverrideTables"));
            if (existing != null) JsonPath.DeepMerge(mirrored, existing);
            PruneOverrideTables(mirrored, "game._ServerConfig.default._OverrideTables", log);
            serverDefault["_OverrideTables"] = mirrored;
            return doc;
        }

        // ---------------------------------------------------------------------------------------- sections

        private static void ApplyPointcuts(Dictionary<string, object> doc, CrmOptions options, Log log)
        {
            var result = new List<object>();
            var source = JsonValue.AsArray(JsonValue.Get(doc, "pointcut_actions"));
            int dropped = 0;
            if (source != null)
            {
                foreach (object action in source)
                {
                    string type = JsonValue.GetString(JsonValue.AsObject(action), "type", "");
                    if (!options.KeepAdPointcuts && Array.IndexOf(AdActionTypes, type) >= 0)
                    {
                        dropped++;
                        continue;
                    }
                    result.Add(action);
                }
            }
            foreach (object action in options.PointcutActions)
            {
                string problem = CrmDocumentValidator.CheckPointcutAction(action);
                if (problem != null)
                {
                    log.Warn("settings: crm.pointcutActions entry ignored: " + problem);
                    continue;
                }
                if (JsonValue.GetDouble(JsonValue.AsObject(action), "frequency", 0) <= 0)
                    log.Warn("settings: crm.pointcutActions entry '" + JsonValue.GetString(JsonValue.AsObject(action), "id", "?")
                             + "' has no positive frequency and never fires (OP/PointCutAction.cs defaults it to 0)");
                result.Add(JsonValue.DeepClone(action));
            }
            if (dropped > 0) log.Debug("CRM: dropped " + dropped + " ad pointcut action(s) (crm.keepAdPointcuts=false)");
            doc["pointcut_actions"] = result;
        }

        private static void ApplyOfflineStore(Dictionary<string, object> doc, Dictionary<string, object> pre,
                                              Dictionary<string, object> generated, CrmOptions options, Log log)
        {
            var ours = JsonValue.DeepCloneObject(generated ?? NewObject());
            if (!(JsonValue.Get(ours, "prices") is List<object>)) ours["prices"] = new List<object>();
            ScalePrices((List<object>)ours["prices"], options.PriceMultiplier);

            var shipped = JsonValue.AsObject(JsonValue.Get(pre, "offline_store"));
            Dictionary<string, object> store;
            switch (options.OfflineStore)
            {
                case OfflineStoreMode.Preconfig:
                    store = shipped != null ? JsonValue.DeepCloneObject(shipped) : ours;
                    break;
                case OfflineStoreMode.Merge:
                    store = ours;
                    if (shipped != null)
                    {
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        var prices = (List<object>)store["prices"];
                        foreach (object p in prices) names.Add(ItemName(p) ?? "");
                        foreach (object p in JsonValue.AsArray(JsonValue.Get(shipped, "prices")) ?? new List<object>())
                        {
                            string name = ItemName(p);
                            if (name != null && names.Add(name)) prices.Add(JsonValue.DeepClone(p));
                        }
                    }
                    break;
                default:
                    store = ours;
                    break;
            }
            if (!(JsonValue.Get(store, "prices") is List<object>)) store["prices"] = new List<object>();

            // "promotion": null passes the client's != null test and then fails (report 04 §15): drop it.
            if (store.ContainsKey("promotion") && !(store["promotion"] is Dictionary<string, object>)) JsonPath.RemoveKey(store, "promotion");

            int removed = CrmDocumentValidator.RemoveInvalidStoreItems((List<object>)store["prices"], log);
            if (removed > 0) log.Warn("CRM: removed " + removed + " invalid offline_store item(s) (see above)");
            doc["offline_store"] = store;
        }

        private static void ApplyIap(Dictionary<string, object> doc, Dictionary<string, object> pre,
                                     Dictionary<string, object> overlayIap, CrmOptions options, Log log)
        {
            // The client never registers its IapStoreComponent unless the CRM has an "iap" key, and several callers
            // read CrmConfig.IapStore.Items without null checks (GM/PlayerProfileComponent.cs:5971,6062): always send one.
            Dictionary<string, object> iap = null;
            if (options.Iap == IapMode.Preconfig)
            {
                var shipped = JsonValue.AsObject(JsonValue.Get(pre, "iap"));
                if (shipped != null && shipped.ContainsKey("prices"))
                {
                    iap = JsonValue.DeepCloneObject(shipped);
                }
                else if (shipped != null)
                {
                    // The preconfiguration is indexed by language; the TS fallback picks the profile language
                    // (EN/CRMComponent.cs:394-416). We serve every user the same document: crm.iapLanguage.
                    iap = JsonValue.AsObject(JsonValue.Get(shipped, options.IapLanguage ?? "en"))
                          ?? JsonValue.AsObject(JsonValue.Get(shipped, "en"));
                    if (iap == null) log.Warn("CRM: the preconfiguration iap has no '" + options.IapLanguage + "' catalogue; sending an empty one");
                    iap = iap == null ? null : JsonValue.DeepCloneObject(iap);
                }
            }
            if (iap == null) iap = overlayIap != null ? JsonValue.DeepCloneObject(overlayIap) : NewObject();
            if (!(JsonValue.Get(iap, "prices") is List<object>)) iap["prices"] = new List<object>();
            if (iap.ContainsKey("promotion") && !(iap["promotion"] is Dictionary<string, object>)) JsonPath.RemoveKey(iap, "promotion");
            doc["iap"] = iap;
        }

        private static void ApplyGameParameters(Dictionary<string, object> game, CrmOptions options)
        {
            // Without Ignore the TS shows the age gate on first boot (verify-C2 §1.5; AgeGateChecker.Handshake).
            ChildObject(game, "AgeGateParams")["Ignore"] = options.SkipAgeGate;

            if (!options.RedeemCoupons)
            {
                // Leave the shipped cell (0 = button hidden) alone.
                var features = JsonValue.AsObject(JsonPath.Get(game, "_OverrideTables.MCVS_System.GameFeatures"));
                var cells = features == null ? null : JsonValue.AsObject(JsonValue.Get(features, "Id@Enabled"));
                if (cells != null) JsonPath.RemoveKey(cells, "RedeemCoupons");
            }

            var serverDefault = ChildObject(ChildObject(game, "_ServerConfig"), "default");
            // GameServerComponent.OverrideFromCRM: IsDevelopment defaults to true, and every loopback client is in the
            // edge list, so the cheat menu is on unless we say false (verify-C5 (a)). Always send a JSON bool.
            serverDefault["IsDevelopment"] = options.DevMenu;
            if (!string.IsNullOrEmpty(options.DeveloperIpRange)) serverDefault["AdditionalDeveloperIpRange"] = options.DeveloperIpRange;

            if (options.InstantBots || options.MatchmakingOverrides != null)
            {
                var mm = ChildObject(serverDefault, "Matchmaking");
                if (options.InstantBots)
                {
                    // CloseRoom runs as soon as the human's handshake completes and tops the room up with 7 bots
                    // (verify-C4 §3.3-3.4). Integers: read with Value<int?>.
                    mm["MinPlayers"] = 1L;
                    mm["MaxOpenTimeInSeconds"] = 0L;
                }
                if (options.MatchmakingOverrides != null) JsonPath.DeepMerge(mm, options.MatchmakingOverrides);
            }
        }

        private static void ApplyEvents(Dictionary<string, object> doc, CrmOptions options)
        {
            // No event ids -> TimeLimitedEventsComponent sets an empty list without a request (verify-C2 §1.8).
            if (options.EventIds.Count == 0) return;
            var participation = new List<object>();
            foreach (string id in options.EventIds)
            {
                var o = NewObject();
                o["id"] = id;
                participation.Add(o);
            }
            var events = ChildObject(ChildObject(doc, "groupware"), "events");
            events["participation"] = participation;
            if (!(JsonValue.Get(events, "advertisement") is List<object>)) events["advertisement"] = new List<object>();
        }

        /// <summary>
        /// Economy multipliers as <c>_OverrideTables</c> MUL cells ("*1.5") on the rows listed in economy.json. Only
        /// existing rows can be changed (EN/Database.cs ApplyCRMTableChanges), which is why the generator lists the ids.
        /// </summary>
        private static void ApplyMultipliers(Dictionary<string, object> tables, Dictionary<string, object> economy, CrmOptions options, Log log)
        {
            var targets = JsonValue.AsObject(JsonValue.Get(economy, "multipliers"));
            if (targets == null) return;
            ApplyTargets(tables, JsonValue.AsArray(JsonValue.Get(targets, "credits")), options.CreditsMultiplier, options.DailyCapMultiplier, log);
            ApplyTargets(tables, JsonValue.AsArray(JsonValue.Get(targets, "versusCoins")), options.VersusCoinsMultiplier, options.DailyCapMultiplier, log);
            ApplyTargets(tables, JsonValue.AsArray(JsonValue.Get(targets, "xp")), options.XpMultiplier, 1.0, log);
        }

        private static void ApplyTargets(Dictionary<string, object> tables, List<object> targets, double multiplier, double capMultiplier, Log log)
        {
            if (targets == null) return;
            foreach (object t in targets)
            {
                var target = JsonValue.AsObject(t);
                string db = JsonValue.GetString(target, "database");
                string table = JsonValue.GetString(target, "table");
                string index = JsonValue.GetString(target, "index");
                var ids = JsonValue.GetArray(target, "ids");
                if (string.IsNullOrEmpty(db) || string.IsNullOrEmpty(table) || string.IsNullOrEmpty(index) || ids == null)
                {
                    log.Warn("economy.json: incomplete multiplier target ignored");
                    continue;
                }
                bool inverse = JsonValue.GetBool(target, "inverse", false);
                double factor = inverse ? 1.0 / multiplier : multiplier;
                AddMul(tables, db, table, index, JsonValue.GetArray(target, "fields"), ids, factor);
                AddMul(tables, db, table, index, JsonValue.GetArray(target, "capFields"), ids, factor * capMultiplier);
            }
        }

        private static void AddMul(Dictionary<string, object> tables, string db, string table, string index, List<object> fields,
                                   List<object> ids, double factor)
        {
            if (fields == null || IsOne(factor)) return;
            string value = "*" + factor.ToString("R", CultureInfo.InvariantCulture);
            var t = ChildObject(ChildObject(tables, db), table);
            foreach (object f in fields)
            {
                string field = JsonValue.AsString(f);
                if (string.IsNullOrEmpty(field)) continue;
                // '#' stands for '.' in a field name (EN/Database.cs ApplyCRMTableChanges).
                var cells = ChildObject(t, index + "@" + field.Replace('.', '#'));
                foreach (object id in ids)
                {
                    string key = JsonValue.AsString(id);
                    if (!string.IsNullOrEmpty(key)) cells[key] = value;
                }
            }
        }

        // ---------------------------------------------------------------------------------------- helpers

        /// <summary>
        /// Removes what the client would choke on or ignore: empty changers (the shipped preconfiguration has ~175 KB of
        /// them), field keys without exactly one '@' (CRMTableChanger throws), and non-scalar cell values.
        /// </summary>
        public static void PruneOverrideTables(Dictionary<string, object> tables, string where, Log log)
        {
            foreach (string db in new List<string>(tables.Keys))
            {
                var dbObj = tables[db] as Dictionary<string, object>;
                if (dbObj != null)
                {
                    foreach (string table in new List<string>(dbObj.Keys))
                    {
                        var tableObj = dbObj[table] as Dictionary<string, object>;
                        if (tableObj != null)
                        {
                            foreach (string field in new List<string>(tableObj.Keys))
                            {
                                var cells = tableObj[field] as Dictionary<string, object>;
                                if (cells == null || field.Split('@').Length != 2)
                                {
                                    if (cells == null || cells.Count > 0)
                                        log.Warn(where + "." + db + "." + table + ": invalid field changer '" + field + "' removed");
                                    JsonPath.RemoveKey(tableObj, field);
                                    continue;
                                }
                                foreach (string cell in new List<string>(cells.Keys))
                                {
                                    object v = cells[cell];
                                    bool scalar = v is string || JsonValue.IsNumber(v) || v is bool;
                                    if (!scalar)
                                    {
                                        if (v != null) log.Warn(where + "." + db + "." + table + "." + field + "." + cell + ": non-scalar value removed");
                                        JsonPath.RemoveKey(cells, cell);
                                    }
                                }
                                if (cells.Count == 0) JsonPath.RemoveKey(tableObj, field);
                            }
                        }
                        if (tableObj == null || tableObj.Count == 0) JsonPath.RemoveKey(dbObj, table);
                    }
                }
                if (dbObj == null || dbObj.Count == 0) JsonPath.RemoveKey(tables, db);
            }
        }

        /// <summary>Multiplies every price of every billing method (current and replaced) and keeps them integral.</summary>
        public static void ScalePrices(List<object> prices, double multiplier)
        {
            if (IsOne(multiplier)) return;
            foreach (object p in prices)
            {
                foreach (object bm in JsonValue.GetArray(JsonValue.AsObject(p), "billing_methods") ?? new List<object>())
                {
                    var method = JsonValue.AsObject(bm);
                    ScaleList(JsonValue.GetArray(method, "price"), multiplier);
                    ScaleList(JsonValue.GetArray(method, "replaced_price"), multiplier);
                }
            }
        }

        private static void ScaleList(List<object> list, double multiplier)
        {
            if (list == null) return;
            foreach (object e in list)
            {
                var entry = JsonValue.AsObject(e);
                double? price = entry == null ? null : JsonValue.AsDouble(JsonValue.Get(entry, "price"));
                if (!price.HasValue) continue;
                double scaled = Math.Round(price.Value * multiplier);
                entry["price"] = scaled < 1 ? 1L : (long)scaled;
            }
        }

        /// <summary>The offline_store name of an entry: "group" for bundles, else "item" (OP/OfflineStoreItem.cs).</summary>
        public static string ItemName(object entry)
        {
            var o = JsonValue.AsObject(entry);
            if (o == null) return null;
            string group = JsonValue.GetString(o, "group");
            return !string.IsNullOrEmpty(group) ? group : JsonValue.GetString(o, "item");
        }

        private static bool IsOne(double factor)
        {
            return Math.Abs(factor - 1.0) < 1e-9;
        }

        /// <summary>The child object at <paramref name="key"/>, created (or replacing a non-object) when needed.</summary>
        private static Dictionary<string, object> ChildObject(Dictionary<string, object> parent, string key)
        {
            object value;
            var child = parent.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
            if (child == null)
            {
                child = NewObject();
                parent[key] = child;
            }
            return child;
        }

        private static Dictionary<string, object> NewObject()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }
    }
}
