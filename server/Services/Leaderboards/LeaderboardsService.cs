// Module: Leaderboards - owner group: MISC (Social, Messaging, Alerts, Leaderboards, GameStats, Profanity, Chat, Clans, Wall, BanTool, Geo, Misc)
// Leaderboards (Olympus), incl. the division/season data.
// Protocol: report 02 §4.9
// To implement:
//   POST leaderboards/{sort}/{name}/me; GET leaderboards/{sort}/{name}[/me/location|/me/friends|/{cred}]
//   LeaderboardData must be an object with data, total_entries and a sane my_entry (divisions use my_entry.rank).
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/misc.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Leaderboards
{
    /// <summary>Leaderboards (Olympus), incl. the division/season data.</summary>
    public static class LeaderboardsService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: MISC). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
