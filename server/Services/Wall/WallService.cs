// Module: Wall - owner group: MISC (Social, Messaging, Alerts, Leaderboards, GameStats, Profanity, Chat, Clans, Wall, BanTool, Geo, Misc)
// Walls of groups/accounts/events/game_objects (clan notifications).
// Protocol: report 02 §4.6
// To implement:
//   POST /{type}/{id}/wall; GET /{type}/{id}/wall (array)
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/misc.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Wall
{
    /// <summary>Walls of groups/accounts/events/game_objects (clan notifications).</summary>
    public static class WallService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: MISC). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
