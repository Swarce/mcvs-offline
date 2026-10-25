// Module: Geo - owner group: MISC (Social, Messaging, Alerts, Leaderboards, GameStats, Profanity, Chat, Clans, Wall, BanTool, Geo, Misc)
// IP geolocation (ip.ecomapis.gameloft.org), reached only with the patcher's 'geo' IL patch.
// Protocol: ARCHITECTURE §2.3 'geo'; verify-C2 §1.5, §3 last row
// To implement:
//   GET location/?ip=<ip>   -> {"country_info":{"iso2":"US"}} (fast: the TS handshake waits on it)
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/misc.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Geo
{
    /// <summary>IP geolocation (ip.ecomapis.gameloft.org), reached only with the patcher's 'geo' IL patch.</summary>
    public static class GeoService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: MISC). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
