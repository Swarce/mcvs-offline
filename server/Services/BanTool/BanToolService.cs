// Module: BanTool - owner group: MISC (Social, Messaging, Alerts, Leaderboards, GameStats, Profanity, Chat, Clans, Wall, BanTool, Geo, Misc)
// BanTool admin API (absolute host bantoolv3.gameloft.org; not used by the game).
// Protocol: report 02 §4.11
// To implement:
//   api/campaign, api/action, api/asset, api/reason - only reachable if a host is redirected; answer minimally
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/misc.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.BanTool
{
    /// <summary>BanTool admin API (absolute host bantoolv3.gameloft.org; not used by the game).</summary>
    public static class BanToolService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: MISC). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
