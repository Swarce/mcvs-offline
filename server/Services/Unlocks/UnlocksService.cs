// Module: Unlocks - owner: core (lead). Glue between STORAGE (inventory ledger), CONTENT (unlocks.json groups) and
// MISC (set_profile alerts) for settings.unlockAll.
// Protocol: verify-C5 (c) and "Recommended design"; docs/protocol/unlocks.md
//   GET  _mcvs/unlocks                  diagnostics: enabled, key count, per-account state (loopback only)
//   POST _mcvs/unlocks/grant?account=   queue a grant now for an account id or fed_id (loopback only, needs unlockAll)
// Conventions and core APIs: docs/MODULES.md.

using System;
using System.Collections.Generic;
using System.Net;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Unlocks
{
    /// <summary>settings.unlockAll through the IAP inventory ledger; see <see cref="UnlockGranter"/>.</summary>
    public static class UnlocksService
    {
        /// <summary>Registers this module. Called by <see cref="ServiceRegistry"/> after storage, assets and alerts.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("unlocks");
            var granter = new UnlockGranter(context, log);
            context.Features.Set(granter);
            context.Events.Subscribe(LocalProfileStore.ChangedTopic, granter.OnProfileChanged);
            if (granter.Enabled)
                log.Info("unlockAll is on: every profile the game saves gets the full unlock through the inventory ledger");

            router.Get("_mcvs/unlocks", "unlocks.status", request =>
            {
                if (!IsLocal(request)) return HttpResponse.Error(HttpStatus.NotFound, "not found");
                var body = new Dictionary<string, object>(StringComparer.Ordinal);
                body["enabled"] = granter.Enabled;
                body["keys"] = (long)granter.AllKeys().Count;
                body["accounts"] = granter.Status();
                return HttpResponse.Json(body);
            });

            router.Post("_mcvs/unlocks/grant", "unlocks.grant", request =>
            {
                if (!IsLocal(request)) return HttpResponse.Error(HttpStatus.NotFound, "not found");
                if (!granter.Enabled) return HttpResponse.Error(HttpStatus.Conflict, "settings.unlockAll is off");
                string account = request.Param("account") ?? "";
                if (account.StartsWith(McvsConstants.FedIdPrefix, StringComparison.Ordinal))
                    account = account.Substring(McvsConstants.FedIdPrefix.Length);
                if (account.Length == 0 || context.Accounts.FindById(account) == null)
                    return HttpResponse.Error(HttpStatus.NotFound, "unknown account '" + account + "'");
                var body = new Dictionary<string, object>(StringComparer.Ordinal);
                body["account"] = account;
                body["queued"] = granter.Enqueue(account);
                return HttpResponse.Json(body);
            });
        }

        private static bool IsLocal(HttpRequest request)
        {
            return request.RemoteEndPoint == null || IPAddress.IsLoopback(request.RemoteEndPoint.Address);
        }
    }
}
