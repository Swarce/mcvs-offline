// Module: Eve - owner group: AUTH (Eve, Pandora, Auth)
// Environment switcher (eve): the first requests on boot, service discovery.
// Protocol: report 02 §2.1-2.3, §2.5; verify-C2 §3 rows 1-2; verify-C1 §2; ARCHITECTURE §2.2
// To implement:
//   GET  /                                     (core.root answers {"eve":"on"} until this module overrides it)
//   GET  config/{clientId}/datacenters         -> [{"name":<Settings.DataCenter>,"preferred":true,...}]
//   GET  config/{clientId}/datacenters/{dc}/urls -> {pandora, ecomm_api_root, crm_iap, ...}; no "status" (or "none")
//   Hand out URLs from context.BaseUrlFor(request) (explicit port, trailing slash). '//config/...' already routes.
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/auth.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Eve
{
    /// <summary>Environment switcher (eve): the first requests on boot, service discovery.</summary>
    public static class EveService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: AUTH). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
