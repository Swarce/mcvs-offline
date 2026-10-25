// Module: Assets - owner group: CONTENT (Crm, Matchmaker, Assets + server/Content + tools/content-gen)
// Iris assets and coupons (requests carry no token).
// Protocol: report 02 §9; verify-C5 (b); report 10
// To implement:
//   GET  assets/{clientId}, assets/{clientId}/{name}[/metadata[/{attr}]|/url]   (Range header)
//   GET  coupons/{clientId}/{code}   -> [{"type":"UnlockMerc_ALL_MAX","quantity":1}, ...]
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/content.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Assets
{
    /// <summary>Iris assets and coupons (requests carry no token).</summary>
    public static class AssetsService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: CONTENT). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
