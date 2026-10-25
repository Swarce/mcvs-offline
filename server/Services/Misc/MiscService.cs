// Module: Misc - owner group: MISC (Social, Messaging, Alerts, Leaderboards, GameStats, Profanity, Chat, Clans, Wall, BanTool, Geo, Misc)
// Catch-all for everything else: tracking (etsv2), marketing redirects, ads, game portal, ecomm/IAP...
// Protocol: report 02 §6 ('Safe generic stub'), §9
// To implement:
//   Register broad templates with Router.CatchAllOrder (or later) so specific routes always win.
//   Unknown GET -> 200 {} except array endpoints -> 200 []; keep logging via the router fallback / RequestLogger.
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/misc.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Misc
{
    /// <summary>Catch-all for everything else: tracking (etsv2), marketing redirects, ads, game portal, ecomm/IAP...</summary>
    public static class MiscService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: MISC). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
