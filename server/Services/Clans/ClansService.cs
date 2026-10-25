// Module: Clans - owner group: MISC (Social, Messaging, Alerts, Leaderboards, GameStats, Profanity, Chat, Clans, Wall, BanTool, Geo, Misc)
// Clans (Osiris groups).
// Protocol: report 02 §4.3
// To implement:
//   POST groups, groups/{id}[...]; GET groups/{id}, groups?..., groups/categories/{cat}/find/... (arrays)
//   groups/{id}/members[...], groups/{id}/fields/{field} (404 = default value)
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/misc.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Clans
{
    /// <summary>Clans (Osiris groups).</summary>
    public static class ClansService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: MISC). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
