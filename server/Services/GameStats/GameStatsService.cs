// Module: GameStats - owner group: MISC (Social, Messaging, Alerts, Leaderboards, GameStats, Profanity, Chat, Clans, Wall, BanTool, Geo, Misc)
// game_stats: matchmaking heuristics (all calls are try/catch on the game side).
// Protocol: report 02 §4.10
// To implement:
//   GET gamestats/users/queries/default, gamestats/users/queries/game_stats_by_time (array)
//   POST gamestats/users/me/events/default
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/misc.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.GameStats
{
    /// <summary>game_stats: matchmaking heuristics (all calls are try/catch on the game side).</summary>
    public static class GameStatsService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: MISC). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
