// Module: Crm - owner group: CONTENT (Crm, Matchmaker, Assets + server/Content + tools/content-gen)
// CRM (Hestia) configuration: the complete config document the TS/GS read.
// Protocol: ARCHITECTURE §3 'CRM'; verify-C2 §1.7; verify-C4 §3.2-3.4; verify-C5 (a)(b)(d); report 04 §5; docs/protocol/content.md
//   GET configs/users/me                  complete document (player's Config/crmpreconfiguration.json + our overlay),
//                                         without the caller's owned kill signatures (CrmOwnedStoreItems)
//   GET configs/users/me/{*selector}      the sub-tree at a selector: "game" (GS/TS game-server session; the GS waits
//                                         forever without a 200), "game.CrashlyticsParams.Enable" (TS handshake),
//                                         "iap" (native IAP lib, report 04 §5.1)
//   GET _mcvs/crm, POST _mcvs/crm/reload   diagnostics for testers (not used by the game)

using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
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

            router.Get("_mcvs/crm", "crm.debug", request => Diagnostics(cache));
            router.Post("_mcvs/crm/reload", "crm.reload", request =>
            {
                cache.Invalidate();
                return Diagnostics(cache);
            });

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
                    if (path.Length == 0 && owned != null && context.Settings.GetBool("crm.omitOwnedSignatures", true))
                    {
                        Caller caller = Fed.TryGetCaller(context, request);
                        if (caller != null) json = owned.JsonFor(doc, caller.AccountId);
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

        private static HttpResponse Diagnostics(CrmDocumentCache cache)
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
            return HttpResponse.Json(body);
        }

        /// <summary>
        /// The smallest document the client accepts (game object, offline_store with prices, iap): used only when
        /// building the real one failed.
        /// </summary>
        public static Dictionary<string, object> MinimalDocument(bool devMenu, bool instantBots)
        {
            var options = new CrmOptions { DevMenu = devMenu, InstantBots = instantBots };
            return CrmDocumentBuilder.Build(null, new CrmContent(), options, Log.Null);
        }
    }
}
