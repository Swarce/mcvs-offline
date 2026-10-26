// Module: Crm - owner group: CONTENT (Crm, Matchmaker, Assets + server/Content + tools/content-gen)
// CRM (Hestia) configuration: the complete config document the TS/GS read.
// Protocol: ARCHITECTURE §3 'CRM'; verify-C2 §1.7; verify-C4 §3.2-3.4; verify-C5 (a)(b)(d); report 04 §5; docs/protocol/content.md
//   GET configs/users/me                  complete document (player's Config/crmpreconfiguration.json + our overlay),
//                                         per player (CrmOwnedStoreItems): without the caller's owned kill signatures,
//                                         and with the collector's per-player cells (economy-collector.md §2.9)
//   GET configs/users/me/{*selector}      the sub-tree at a selector: "game" (GS/TS game-server session; the GS waits
//                                         forever without a 200), "game.CrashlyticsParams.Enable" (TS handshake),
//                                         "iap" (native IAP lib, report 04 §5.1); always the static document
//   GET _mcvs/crm, POST _mcvs/crm/reload   diagnostics for testers (not used by the game)
//   GET _mcvs/collector?account=<id>       what the collector read from a player's dump (economy-collector.md §5.7)

using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Crm.Collector;
using McvsOffline.Server.Services.Misc;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>CRM (Hestia) configuration: the complete config document the TS/GS read.</summary>
    public static class CrmService
    {
        /// <summary>How long a full-document request may wait for the startup load of the preconfiguration.</summary>
        public const int DefaultDocumentWaitMs = 5000;

        /// <summary>
        /// How long a selector request may wait: the TS gives <c>configs/users/me/game</c> only 1 s (WaitForCRM) before it
        /// keeps IsDevelopment=true, so this answers quickly even while the preconfiguration is still loading.
        /// </summary>
        public const int DefaultSelectorWaitMs = 300;

        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            var log = context.Logger.For("crm");
            var cache = new CrmDocumentCache(context, new CrmPreconfiguration(), log);
            context.Features.Set(cache);
            var owned = new CrmOwnedStoreItems(context, log);
            context.Events.Subscribe(StorageService.GameProfileReadTopic, owned.OnGameProfileRead);

            router.Get("configs/users/me", "crm.config", request => Config(context, cache, owned, log, request, null));
            router.Get("configs/users/me/{*selector}", "crm.config.selector",
                       request => Config(context, cache, owned, log, request, request.RouteValue("selector")));
            // Hestia also addresses users explicitly; the game only uses "me", but answer the same document.
            router.Get("configs/users/{user}", "crm.config.user", request => Config(context, cache, null, log, request, null));
            router.Get("configs/users/{user}/{*selector}", "crm.config.user.selector",
                       request => Config(context, cache, null, log, request, request.RouteValue("selector")));

            router.Get("_mcvs/crm", "crm.debug", request => Diagnostics(context, cache));
            router.Post("_mcvs/crm/reload", "crm.reload", request =>
            {
                cache.Invalidate();
                return Diagnostics(context, cache);
            });
            router.Get("_mcvs/collector", "crm.collector", request => CollectorDiagnostics(cache, owned, request));

            cache.StartWarmup();
        }

        private static HttpResponse Config(ServerContext context, CrmDocumentCache cache, CrmOwnedStoreItems owned, Log log,
                                           HttpRequest request, string selector)
        {
            string path = CrmDocument.NormalizeSelector(selector);
            try
            {
                int wait = path.Length == 0
                    ? (int)context.Settings.GetLong("crm.documentWaitMs", DefaultDocumentWaitMs)
                    : (int)context.Settings.GetLong("crm.selectorWaitMs", DefaultSelectorWaitMs);
                CrmDocument doc = cache.Get(wait);
                string json;
                if (doc.TryGetSelectorJson(path, out json))
                {
                    // The per-player document (economy-collector.md §2.9): for the store filter, or while the document
                    // carries the collector layer. Selectors and explicit user paths stay static.
                    bool omit = cache.Options.OmitOwnedSignatures;
                    if (path.Length == 0 && owned != null && (omit || doc.CollectorPhase == CollectorPhase.Active))
                    {
                        Caller caller = Fed.TryGetCaller(context, request);
                        if (caller != null) json = owned.JsonFor(doc, caller.AccountId, omit);
                    }
                    log.Debug("configs" + (path.Length == 0 ? "" : " selector '" + path + "'") + ": " + json.Length + " chars");
                    return HttpResponse.RawJson(json);
                }
                // Hestia answers 404 for a missing selector; every selector the client uses exists in our document.
                log.Warn("CRM selector '" + path + "' not found in the document (requested " + request.Path + ")");
                return HttpResponse.Error(HttpStatus.NotFound, "selector '" + path + "' not found");
            }
            catch (Exception ex)
            {
                // Never let CRM fail: the GS waits forever without a 200 and the TS has no fallback after one
                // (verify-C2 §1.7, verify-C4 §3.2). Serve the safe minimum instead.
                log.Error("CRM document failed; serving the minimal safe document", ex);
                var minimal = MinimalDocument(context.Settings.DevMenu, context.Settings.InstantBots);
                object value;
                if (path.Length == 0) value = minimal;
                else if (!JsonPath.TryGet(minimal, path, out value)) return HttpResponse.Error(HttpStatus.NotFound, "selector '" + path + "' not found");
                return HttpResponse.Json(value);
            }
        }

        private static HttpResponse Diagnostics(ServerContext context, CrmDocumentCache cache)
        {
            var doc = cache.Get(0);
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["preconfiguration"] = cache.Preconfiguration.Source;
            body["usesPreconfiguration"] = doc.HasPreconfiguration;
            body["built"] = TimeFormat.Iso8601(doc.BuiltUtc);
            body["chars"] = (long)doc.Json.Length;
            var store = JsonValue.AsArray(JsonPath.Get(doc.Root, "offline_store.prices"));
            body["offlineStoreItems"] = (long)(store == null ? 0 : store.Count);
            body["serverConfig"] = JsonPath.Get(doc.Root, "game._ServerConfig.default");
            var problems = new List<object>();
            foreach (string p in doc.Problems) problems.Add(p);
            body["problems"] = problems;
            body["collector"] = CollectorBlock(context, doc);
            return HttpResponse.Json(body);
        }

        /// <summary>
        /// The <c>collector</c> block of <c>GET _mcvs/crm</c> (economy-collector.md §2.13, §5.8): the phase the document
        /// carries and why, the cells of each tree, itemsPerCrate and the static weights (§4.2).
        /// </summary>
        private static Dictionary<string, object> CollectorBlock(ServerContext context, CrmDocument doc)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            CrmCollector c = doc.Collector;
            o["phase"] = doc.CollectorPhase.ToString();
            if (c == null) return o;
            if (c.Status != null)
            {
                o["decided"] = c.Status.Phase.ToString();
                o["reason"] = c.Status.Reason;
                o["since"] = c.Status.Since.HasValue ? (object)TimeFormat.Iso8601(c.Status.Since.Value) : null;
                o["guardSince"] = TimeFormat.Iso8601(CollectorRuntime.GuardSince(c.Status.Since));
            }
            if (c.Failure != null) o["failure"] = c.Failure;
            var cells = new Dictionary<string, object>(StringComparer.Ordinal);
            cells["game"] = (long)c.GameCells.Count;
            cells["mirror"] = (long)c.MirrorCells.Count;
            if (c.Phase == CollectorPhase.Active) cells["playerMirror"] = c.PlayerMirrorTables != null ? "guard" : "static";
            o["cells"] = cells;
            CollectorOptions options = c.Options ?? new CollectorOptions();
            o["enabled"] = options.Enabled;
            o["itemsPerCrate"] = (long)options.ItemsPerCrate;
            try
            {
                CollectorRuntime runtime = CollectorRuntime.For(context);
                CollectorContent content = runtime.Content;
                o["content"] = content != null ? "collector.json " + content.Hash : runtime.ContentProblem;
                bool? patch = runtime.StorePatch;
                o["storePatch"] = patch.HasValue ? (object)patch.Value : null;
            }
            catch (Exception ex)
            {
                o["content"] = "unavailable (" + ex.Message + ")";
            }
            if (c.Layer != null) o["staticWeights"] = CrmOwnedStoreItems.WeightsObject(c.Layer.Weights);
            return o;
        }

        private static HttpResponse CollectorDiagnostics(CrmDocumentCache cache, CrmOwnedStoreItems owned, HttpRequest request)
        {
            string account = request.Param("account");
            if (!string.IsNullOrEmpty(account) && account.StartsWith("fed_id:", StringComparison.Ordinal)) account = account.Substring("fed_id:".Length);
            if (string.IsNullOrEmpty(account)) return HttpResponse.Error(HttpStatus.BadRequest, "account=<account id> is required");
            return HttpResponse.Json(owned.Describe(cache.Get(0), account));
        }

        /// <summary>
        /// The smallest document the client accepts (game object, offline_store with prices, iap): used only when
        /// building the real one failed. It always carries the collector's guard (§2.8): a Cosmetic crate claimed while it
        /// is served pays VersusCoins instead of throwing.
        /// </summary>
        public static Dictionary<string, object> MinimalDocument(bool devMenu, bool instantBots)
        {
            var options = new CrmOptions { DevMenu = devMenu, InstantBots = instantBots };
            return CrmDocumentBuilder.Build(null, new CrmContent(), options, Log.Null, null, null,
                                            CrmCollector.GuardOnly("the minimal document always carries the guard"));
        }
    }
}
