// Module: BanTool - owner group: MISC
// BanTool admin API (absolute host https://bantoolv3.gameloft.org, OP/BanToolApi.cs). The game never calls it
// (no GetService<BanToolService> in Game/Engine); it is only reachable if a tool or a redirected host sends it here.
// Protocol: report 02 §4.11; docs/protocol/misc.md
//   POST api/campaign (JSON)          -> {"id": ..., "accounts": []}
//   GET  api/campaign?page&...        -> {"campaigns": [], "page": n, "total": 0}
//   GET  api/action, api/asset, api/reason   -> []
// Conventions and core APIs: docs/MODULES.md.

using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Misc;

namespace McvsOffline.Server.Services.BanTool
{
    /// <summary>
    /// BanTool: nobody is ever banned offline. Campaigns are acknowledged and discarded; the catalogues are empty.
    /// (The bans that matter to the game come from elsewhere and are answered "not banned" by their owners: the
    /// authorize 403 ban body (AUTH) and the profile's banned_from list (STORAGE).)
    /// </summary>
    public static class BanToolService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("bantool");

            router.Post("api/campaign", "bantool.campaign.create", request =>
            {
                log.Warn("BanTool campaign received and ignored (nobody is banned offline): " + request.BodyText);
                var body = Fed.Obj();
                body["id"] = Fed.NewId();
                body["accounts"] = new List<object>();
                return HttpResponse.Json(body);
            });
            router.Get("api/campaign", "bantool.campaign.list", request =>
            {
                var body = Fed.Obj();
                body["campaigns"] = new List<object>();
                body["page"] = (long)Fed.IntParam(request, "page", 1, 0, Num.IntMax);
                body["total"] = 0L;
                return HttpResponse.Json(body);
            });
            router.Get("api/action", "bantool.actions", request => HttpResponse.EmptyArray());
            router.Get("api/asset", "bantool.assets", request => HttpResponse.EmptyArray());
            router.Get("api/reason", "bantool.reasons", request => HttpResponse.EmptyArray());
        }
    }
}
