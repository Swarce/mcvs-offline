using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm.Collector;
using McvsOffline.Server.Services.Social.LiveOps;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>
    /// The collector part of one CRM document (docs/design/economy-collector.md §2.8, §2.12). The caller sets the inputs
    /// (<see cref="Status"/>, from <see cref="CollectorRuntime.Status"/>, and while it is <c>Active</c> the
    /// <see cref="Content"/> and <see cref="Options"/> the layer is planned from); <see cref="CrmDocumentBuilder"/> sets
    /// the outputs: what the document carries (<see cref="Phase"/>), the layer, the cells written into each tree and the
    /// per-player mirror (§2.9.3).
    /// </summary>
    public sealed class CrmCollector
    {
        /// <summary>The runtime's decision for this document (input).</summary>
        public CollectorStatus Status;

        /// <summary>collector.json while <see cref="Status"/> is <c>Active</c> (input).</summary>
        public CollectorContent Content;

        /// <summary><c>economy.collector.*</c> (input; null: the defaults).</summary>
        public CollectorOptions Options;

        /// <summary>The planner, <see cref="CollectorPlan.Static"/>; a test may make it throw.</summary>
        internal Func<CollectorContent, CollectorOptions, LiveOpsSnapshot, CollectorLayer> Plan = CollectorPlan.Static;

        /// <summary>
        /// What the document carries: nothing (<c>Dormant</c>), the guard (<c>Guard</c>: decided so, or the layer failed)
        /// or the layer (<c>Active</c>). Only an <c>Active</c> document gets per-player cells.
        /// </summary>
        public CollectorPhase Phase;

        /// <summary>Why the layer (or the guard) was replaced by the guard, or null.</summary>
        public string Failure;

        /// <summary>The layer written (<c>Active</c> only).</summary>
        public CollectorLayer Layer;

        /// <summary>The live-ops snapshot the layer was planned with; the per-player cells use the same one.</summary>
        public LiveOpsSnapshot LiveOps;

        /// <summary>The cells written into <c>game._OverrideTables</c>: the layer's game cells or the guard; empty when Dormant.</summary>
        public IList<CollectorCell> GameCells = new List<CollectorCell>();

        /// <summary>The cells the mirror (<c>game._ServerConfig.default._OverrideTables</c>) carries: the layer's mirror cells
        /// (no <c>Unlockable=true</c> cell, D10) or the guard.</summary>
        public IList<CollectorCell> MirrorCells = new List<CollectorCell>();

        /// <summary>
        /// <c>Active</c> only: the static mirror with the collector's cells replaced by the guard, which every per-player
        /// document carries instead of the static mirror (D10, §2.9.3); null when that failed validation (the per-player
        /// documents then keep the static mirror, which is valid).
        /// </summary>
        public Dictionary<string, object> PlayerMirrorTables;

        /// <summary>A part that carries the guard whatever the clock says (the minimal document, §2.8).</summary>
        public static CrmCollector GuardOnly(string reason)
        {
            return new CrmCollector { Status = new CollectorStatus { Phase = CollectorPhase.Guard, Reason = reason } };
        }
    }

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
    /// <item><c>crm.overlay</c> from settings.json;</item>
    /// <item><c>newsfeeds</c>: our inbox item (<see cref="CrmNewsfeed"/>) appended to whatever the layers above put there.</item>
    /// </list>
    /// The collector's SET cells (<see cref="CrmCollector"/>, docs/design/economy-collector.md §2.8) go in after the
    /// live-ops cells and before the events and <c>crm.overlay</c> (which still wins): its layer, the 5-cell guard or
    /// nothing, by phase. Finally <c>game._OverrideTables</c> is pruned of empty/invalid changers and mirrored into
    /// <c>_ServerConfig.default._OverrideTables</c>: the TS applies the former, the GS applies and forwards the latter
    /// (report 04 §4.2: "put the same block in both places"); the mirror leaves out the collector's
    /// <c>Unlockable=true</c> cells (D10). The collector part is isolated: an exception, or a merged document that breaks
    /// its invariants (<see cref="CrmDocumentValidator.CheckCollector"/>), replaces it by the guard in both trees and keeps
    /// the rest of the document.
    /// </summary>
    public static class CrmDocumentBuilder
    {
        /// <summary>Ad pointcut types: the ad SDK cannot work offline, and they only add ad buttons.</summary>
        private static readonly string[] AdActionTypes = { "show_ad", "show_ad_button", "show_ad_banner" };

        public static Dictionary<string, object> Build(Dictionary<string, object> preconfig, CrmContent content, CrmOptions options, Log log)
        {
            return Build(preconfig, content, options, log, null);
        }

        /// <param name="liveOps">The live-ops layer (event ids, CRM expiry, weekly modifier cells); null for none.</param>
        public static Dictionary<string, object> Build(Dictionary<string, object> preconfig, CrmContent content, CrmOptions options, Log log,
                                                       LiveOpsSnapshot liveOps)
        {
            return Build(preconfig, content, options, log, liveOps, null);
        }

        /// <param name="liveOps">The live-ops layer (event ids, CRM expiry, weekly modifier cells); null for none.</param>
        /// <param name="newsfeeds">
        /// Our inbox items (<see cref="CrmNewsfeed.Build"/>), applied after <c>crm.overlay</c>: an empty list still writes
        /// <c>"newsfeeds": []</c>; null leaves <c>newsfeeds</c> as the layers made it (absent unless an overlay has it).
        /// </param>
        public static Dictionary<string, object> Build(Dictionary<string, object> preconfig, CrmContent content, CrmOptions options, Log log,
                                                       LiveOpsSnapshot liveOps, List<object> newsfeeds)
        {
            return Build(preconfig, content, options, log, liveOps, newsfeeds, null);
        }

        /// <param name="liveOps">The live-ops layer (event ids, CRM expiry, weekly modifier cells); null for none.</param>
        /// <param name="newsfeeds">Our inbox items (see the overload above); null leaves <c>newsfeeds</c> as the layers made it.</param>
        /// <param name="collector">
        /// The collector part (§2.12): its <see cref="CrmCollector.Status"/> says what to write, and the builder fills in
        /// what it wrote. Null: nothing (as <c>Dormant</c>).
        /// </param>
        public static Dictionary<string, object> Build(Dictionary<string, object> preconfig, CrmContent content, CrmOptions options, Log log,
                                                       LiveOpsSnapshot liveOps, List<object> newsfeeds, CrmCollector collector)
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
            ApplyLiveOpsCells(ChildObject(game, "_OverrideTables"), liveOps);
            ApplyCollector(ChildObject(game, "_OverrideTables"), collector, liveOps, log);
            ApplyEvents(doc, options, liveOps);
            if (liveOps != null && liveOps.Expiry.HasValue)
            {
                // The TS refetches CRM (and with it the events) when this passes: the next schedule step
                // (EN/CRMComponent.cs OnConstantUpdate; StandardCrm.Data.DeserializeExpiry). WireDate, not ISO 'T'
                // (LiveOpsSchedule.WireDate: culture-proof on the client).
                doc["expiry"] = LiveOpsSchedule.WireDate(liveOps.Expiry.Value);
            }

            if (options.Overlay != null) JsonPath.DeepMerge(doc, options.Overlay);
            ApplyNewsfeeds(doc, newsfeeds, log);
            CrmDocumentValidator.Sanitize(doc, log);

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
            FinishCollector(tables, mirrored, existing, collector, log);
            return doc;
        }

        // ---------------------------------------------------------------------------------------- collector

        /// <summary>
        /// SET cell (§2.8): <c>tables[db][table]["index@field"][key] = value</c>, replacing whatever the cell holds (MUL,
        /// ADD or SET: one operation per cell, CRMTableCell). <paramref name="field"/> is given with dots ("Weight.1") and
        /// written with '#' (EN/Database.cs ApplyCRMTableChanges). <paramref name="value"/>: a non-empty string or an
        /// integral long (an int is widened); never "", null, NaN or a fraction (ArgumentException).
        /// </summary>
        internal static void SetCell(Dictionary<string, object> tables, string db, string table, string index, string field,
                                     string key, object value)
        {
            if (tables == null) throw new ArgumentNullException("tables");
            if (string.IsNullOrEmpty(db) || string.IsNullOrEmpty(table) || string.IsNullOrEmpty(key))
                throw new ArgumentException("a SET cell needs a database, a table and a key");
            if (string.IsNullOrEmpty(index) || string.IsNullOrEmpty(field) || index.IndexOf('@') >= 0 || field.IndexOf('@') >= 0)
                throw new ArgumentException("'" + index + "@" + field + "' is not a field changer (index@field, one '@')");
            if (value is int) value = (long)(int)value;
            string text = value as string;
            if (text != null ? text.Length == 0 : !(value is long))
                throw new ArgumentException(db + "." + table + "." + index + "@" + field + "." + key
                                            + ": a SET cell holds a non-empty string or an integral long, not "
                                            + (value == null ? "null" : text != null ? "\"\"" : value.GetType().Name));
            var cells = ChildObject(ChildObject(ChildObject(tables, db), table), index + "@" + field.Replace('.', '#'));
            cells[key] = value;
        }

        /// <summary>
        /// Writes what the collector part asks for (§2.12): the layer while <c>Active</c>, the guard while <c>Guard</c>,
        /// nothing while <c>Dormant</c> (the document is then byte-identical to the one before the collector). A plan
        /// that throws is replaced by the guard.
        /// </summary>
        private static void ApplyCollector(Dictionary<string, object> tables, CrmCollector c, LiveOpsSnapshot liveOps, Log log)
        {
            if (c == null) return;
            c.LiveOps = liveOps;
            c.Phase = c.Status == null ? CollectorPhase.Dormant : c.Status.Phase;
            c.Failure = null;
            c.Layer = null;
            c.GameCells = new List<CollectorCell>();
            c.MirrorCells = new List<CollectorCell>();
            c.PlayerMirrorTables = null;
            if (c.Phase == CollectorPhase.Dormant) return;
            if (c.Phase == CollectorPhase.Active)
            {
                CollectorLayer layer = null;
                try
                {
                    if (c.Content == null) throw new InvalidOperationException(CollectorContent.FileName + " is not available");
                    layer = c.Plan(c.Content, c.Options ?? new CollectorOptions(), liveOps);
                    if (layer == null || layer.Game == null || layer.Mirror == null) throw new InvalidOperationException("the plan returned no layer");
                    WriteCells(tables, layer.Game);
                    c.Layer = layer;
                    c.GameCells = layer.Game;
                    c.MirrorCells = layer.Mirror;
                    return;
                }
                catch (Exception ex)
                {
                    if (layer != null && layer.Game != null) RemoveCells(tables, layer.Game);
                    c.Failure = ex.GetType().Name + ": " + ex.Message;
                    c.Phase = CollectorPhase.Guard;
                    log.Error("CRM: collector layer replaced by the guard: " + c.Failure, ex);
                }
            }
            WriteCells(tables, CollectorGuard.Cells);
            c.GameCells = CollectorGuard.Cells;
            c.MirrorCells = CollectorGuard.Cells;
        }

        /// <summary>
        /// After the mirror pass: leaves the <c>Unlockable=true</c> cells out of the mirror (D10; a cell an overlay put
        /// into <c>_ServerConfig.default._OverrideTables</c> itself stays), checks both merged trees
        /// (<see cref="CrmDocumentValidator.CheckCollector"/>), replaces the collector's cells by the guard in both when
        /// either fails, and precomputes the per-player mirror (§2.9.3).
        /// </summary>
        private static void FinishCollector(Dictionary<string, object> tables, Dictionary<string, object> mirrored,
                                            Dictionary<string, object> explicitMirror, CrmCollector c, Log log)
        {
            if (c == null || c.Phase == CollectorPhase.Dormant) return;
            try
            {
                if (c.Phase == CollectorPhase.Active)
                {
                    foreach (CollectorCell cell in c.Layer.Game)
                        if (CollectorPlan.IsUnlockableTrue(cell) && !HasCell(explicitMirror, cell) && HoldsValue(mirrored, cell)) RemoveCell(mirrored, cell);
                    PruneOverrideTables(mirrored, "game._ServerConfig.default._OverrideTables", Log.Null);
                }
                var problems = CrmDocumentValidator.CheckCollector(tables, c.GameCells, "game._OverrideTables");
                problems.AddRange(CrmDocumentValidator.CheckCollector(mirrored, c.MirrorCells, "game._ServerConfig.default._OverrideTables"));
                if (problems.Count > 0)
                {
                    string reason = problems[0] + (problems.Count > 1 ? " (and " + (problems.Count - 1).ToString(CultureInfo.InvariantCulture) + " more)" : "");
                    log.Warn("CRM: " + (c.Phase == CollectorPhase.Active ? "collector layer replaced by the guard: " : "guard cells restored: ") + reason);
                    UseGuard(tables, mirrored, c, reason);
                }
                if (c.Phase == CollectorPhase.Active) c.PlayerMirrorTables = PlayerMirror(mirrored, c, log);
            }
            catch (Exception ex)
            {
                string reason = ex.GetType().Name + ": " + ex.Message;
                log.Error("CRM: collector layer replaced by the guard: " + reason, ex);
                UseGuard(tables, mirrored, c, reason);
            }
        }

        private static void UseGuard(Dictionary<string, object> tables, Dictionary<string, object> mirrored, CrmCollector c, string reason)
        {
            ReplaceByGuard(tables, c.GameCells);
            ReplaceByGuard(mirrored, c.MirrorCells);
            c.Failure = reason;
            c.Phase = CollectorPhase.Guard;
            c.Layer = null;
            c.GameCells = CollectorGuard.Cells;
            c.MirrorCells = CollectorGuard.Cells;
            c.PlayerMirrorTables = null;
        }

        /// <summary>
        /// The per-player mirror (D10, §2.9.3): <paramref name="mirrored"/> with the collector's cells replaced by the
        /// guard; the other mirror cells stay. A collector key an overlay changed keeps the overlay's value; if that
        /// leaves the crate invalid, null (the per-player documents keep the static mirror).
        /// </summary>
        private static Dictionary<string, object> PlayerMirror(Dictionary<string, object> mirrored, CrmCollector c, Log log)
        {
            var copy = JsonValue.DeepCloneObject(mirrored);
            foreach (CollectorCell cell in c.MirrorCells)
                if (HoldsValue(copy, cell)) RemoveCell(copy, cell);
            foreach (CollectorCell cell in CollectorGuard.Cells)
                if (!HasCell(copy, cell)) SetCell(copy, CollectorPlan.Database, cell.Table, cell.Index, cell.Field, cell.Key, cell.Value);
            PruneOverrideTables(copy, "per-player game._ServerConfig.default._OverrideTables", Log.Null);
            var problems = CrmDocumentValidator.CheckCollector(copy, CollectorGuard.Cells, "per-player game._ServerConfig.default._OverrideTables");
            if (problems.Count == 0) return copy;
            log.Warn("CRM: the per-player mirror is not usable (" + problems[0] + "); per-player documents keep the static mirror");
            return null;
        }

        /// <summary>
        /// The guard in place of whatever the collector wrote (§2.8): every key it wrote goes (whatever it holds now), so
        /// does the whole <c>Crate_Event_Special</c> table (an overlay's rows on it included: the guard alone is a valid
        /// crate), then the 5 guard cells are set.
        /// </summary>
        private static void ReplaceByGuard(Dictionary<string, object> tables, IList<CollectorCell> written)
        {
            if (written != null) RemoveCells(tables, written);
            JsonPath.RemoveKey(JsonValue.AsObject(JsonValue.Get(tables, CollectorPlan.Database)), CollectorGuard.CrateTable);
            WriteCells(tables, CollectorGuard.Cells);
            PruneOverrideTables(tables, "game._OverrideTables", Log.Null);
        }

        private static void WriteCells(Dictionary<string, object> tables, IEnumerable<CollectorCell> cells)
        {
            foreach (CollectorCell cell in cells) SetCell(tables, CollectorPlan.Database, cell.Table, cell.Index, cell.Field, cell.Key, cell.Value);
        }

        private static void RemoveCells(Dictionary<string, object> tables, IEnumerable<CollectorCell> cells)
        {
            foreach (CollectorCell cell in cells) RemoveCell(tables, cell);
        }

        /// <summary>The changer of <paramref name="cell"/> in <paramref name="tables"/>, or null.</summary>
        private static Dictionary<string, object> ChangerOf(Dictionary<string, object> tables, CollectorCell cell)
        {
            var db = JsonValue.AsObject(JsonValue.Get(tables, CollectorPlan.Database));
            return JsonValue.AsObject(JsonValue.Get(JsonValue.AsObject(JsonValue.Get(db, cell.Table)), cell.Changer));
        }

        private static bool HasCell(Dictionary<string, object> tables, CollectorCell cell)
        {
            var changer = ChangerOf(tables, cell);
            return changer != null && changer.ContainsKey(cell.Key);
        }

        /// <summary>True when the tree still holds the collector's own value at the cell's key.</summary>
        private static bool HoldsValue(Dictionary<string, object> tables, CollectorCell cell)
        {
            var changer = ChangerOf(tables, cell);
            object value;
            return changer != null && changer.TryGetValue(cell.Key, out value) && JsonValue.DeepEquals(value, cell.Value);
        }

        private static void RemoveCell(Dictionary<string, object> tables, CollectorCell cell)
        {
            var changer = ChangerOf(tables, cell);
            if (changer != null) JsonPath.RemoveKey(changer, cell.Key);
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

        private static void ApplyEvents(Dictionary<string, object> doc, CrmOptions options, LiveOpsSnapshot liveOps)
        {
            var ids = new List<string>();
            foreach (string id in options.EventIds)
                if (!ids.Contains(id)) ids.Add(id);
            if (liveOps != null)
                foreach (string id in liveOps.EventIds)
                    if (!ids.Contains(id)) ids.Add(id);
            // No event ids -> TimeLimitedEventsComponent sets an empty list without a request (verify-C2 §1.8).
            if (ids.Count == 0) return;
            var participation = new List<object>();
            foreach (string id in ids)
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
        /// Our inbox items after every other layer (design §9.1): a <c>newsfeeds</c> array from the preconfiguration or
        /// an overlay is kept and ours are appended, each replacing an entry with the same <c>news_id</c>. The key is
        /// written even for an empty list, so a message that is turned off leaves the inbox at the next CRM fetch.
        /// </summary>
        private static void ApplyNewsfeeds(Dictionary<string, object> doc, List<object> newsfeeds, Log log)
        {
            if (newsfeeds == null) return;
            object value;
            var list = doc.TryGetValue("newsfeeds", out value) ? value as List<object> : null;
            if (list == null)
            {
                if (doc.ContainsKey("newsfeeds")) log.Warn("CRM: newsfeeds was not an array; replaced with []");
                list = new List<object>();
                doc["newsfeeds"] = list;
            }
            foreach (object item in newsfeeds)
            {
                string id = JsonValue.GetString(JsonValue.AsObject(item), "news_id");
                if (!string.IsNullOrEmpty(id))
                {
                    int replaced = list.RemoveAll(e => string.Equals(JsonValue.AsString(JsonValue.Get(JsonValue.AsObject(e), "news_id")), id,
                                                                     StringComparison.Ordinal));
                    if (replaced > 0) log.Debug("CRM: newsfeed '" + id + "' from the overlay replaced by ours");
                }
                list.Add(JsonValue.DeepClone(item));
            }
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
                    if (!string.IsNullOrEmpty(key)) cells[key] = CombineMul(JsonValue.Get(cells, key), factor);
                }
            }
        }

        /// <summary>
        /// "*f", or the product with a MUL cell already there (economy multiplier × weekly modifier: one cell holds one
        /// operation, CRMTableCell). A SET/ADD cell already there (preconfiguration, crm.overlay) is kept as it is.
        /// </summary>
        private static object CombineMul(object existing, double factor)
        {
            string text = JsonValue.AsString(existing);
            if (!string.IsNullOrEmpty(text))
            {
                double current;
                if (text[0] != '*') return existing;
                if (double.TryParse(text.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out current)) factor *= current;
            }
            return "*" + factor.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>The season modifier and the season-reward multiplier (docs/LIVEOPS.md "Modifiers").</summary>
        private static void ApplyLiveOpsCells(Dictionary<string, object> tables, LiveOpsSnapshot liveOps)
        {
            if (liveOps == null) return;
            foreach (LiveOpsCells cells in liveOps.Cells)
            {
                var fields = new List<object>();
                foreach (string f in cells.Fields) fields.Add(f);
                var ids = new List<object>();
                foreach (string id in cells.Ids) ids.Add(id);
                AddMul(tables, cells.Database, cells.Table, cells.Index, fields, ids, cells.Factor);
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
