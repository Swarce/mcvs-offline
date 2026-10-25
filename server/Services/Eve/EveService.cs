// Module: Eve - owner group: AUTH (Eve, Pandora, Auth)
// Environment switcher (eve): the first requests on boot, service discovery.
// Protocol: report 02 §2.1-2.3, §2.5; verify-C2 §1.2, §3 rows 1-2; verify-C1 §2; ARCHITECTURE §2.2; docs/protocol/auth.md
//   GET  /                                       served by core.root ({"eve":"on",...}; Connectivity probe, report 02 §2.5)
//   GET  config/{clientId}/datacenters           -> [{"name":<Settings.DataCenter>,"status":"active","preferred":true,"country_code":"US"}]
//   GET  config/{clientId}/datacenters/{dc}/urls -> {"pandora":..., "ecomm_api_root":..., "crm_iap":..., ...}; no "status"
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/auth.md.

using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Pandora;

namespace McvsOffline.Server.Services.Eve
{
    /// <summary>Environment switcher (eve): the first requests on boot, service discovery.</summary>
    public static class EveService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("eve");

            // GET / stays with core.root, which already answers what Connectivity.IsInternetAvailable needs (any 2xx).

            router.Get("config/{clientId}/datacenters", "eve.datacenters", request => Datacenters(context, log, request));
            router.Get("config/{clientId}/datacenters/{dc}/urls", "eve.urls", request => Urls(context, log, request));
        }

        /// <summary>
        /// Datacenter list (OP/Federation.cs ListDatacenters/LoadDataCenter, OP/Datacenters.cs): a JSON array; the
        /// client takes the first entry with preferred=true and fails with "Incorrect datacenter" if its name is empty.
        /// The name is persisted by the client (SaveStream "datacenter") and repeated in lobbyconfigs cmd_args, so it
        /// comes from Settings.DataCenter (validated cmd-arg safe).
        /// </summary>
        internal static HttpResponse Datacenters(ServerContext context, Log log, HttpRequest request)
        {
            NoteClientId(log, request.RouteValue("clientId"));
            string country = request.Param("country");
            var dc = new Dictionary<string, object>(StringComparer.Ordinal);
            dc["name"] = context.Settings.DataCenter;
            dc["status"] = "active";
            dc["preferred"] = true;
            dc["country_code"] = string.IsNullOrEmpty(country) ? "US" : country;
            return HttpResponse.Json(new List<object> { dc });
        }

        /// <summary>
        /// Service map (OP/Configuration.cs): a JSON object of strings. "status" is deliberately absent: any value other
        /// than none/absent shows update or maintenance popups, and an unknown one throws FormatException (verify-C2
        /// §1.2). pandora is required for every fed service; ecomm_api_root and crm_iap are located by
        /// IapStoreComponent.InitNative, which throws without them (verify-C2 §1.9); the other keys avoid "Invalid
        /// online configuration" in tracking, browser, ads and game portal code. Answered for any datacenter name: a
        /// client that played online before may still have Gameloft's datacenter saved.
        /// </summary>
        internal static HttpResponse Urls(ServerContext context, Log log, HttpRequest request)
        {
            NoteClientId(log, request.RouteValue("clientId"));
            string dc = request.RouteValue("dc");
            if (!string.Equals(dc, context.Settings.DataCenter, StringComparison.Ordinal))
                log.Info("urls requested for datacenter '" + dc + "' (ours is '" + context.Settings.DataCenter + "'); answering the same service map");

            var urls = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string service in ServiceUrls.EveServices)
                urls[service] = ServiceUrls.For(context, request, service);
            return HttpResponse.Json(urls);
        }

        private static void NoteClientId(Log log, string clientId)
        {
            if (!string.Equals(clientId, McvsConstants.ClientId, StringComparison.Ordinal))
                log.Info("eve request for client id '" + clientId + "' (expected " + McvsConstants.ClientId + "); answering anyway");
        }
    }
}
