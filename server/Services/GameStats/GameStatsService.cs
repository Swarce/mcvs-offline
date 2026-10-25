// Module: GameStats - owner group: MISC
// game_stats: matchmaking heuristics. Every call is wrapped in try/catch on the game side
// (GM/GameMatchmakingComponent.cs:1267-1290,1325-1345).
// Protocol: report 02 §4.10; docs/protocol/misc.md
//   GET  gamestats/users/queries/default?category&group_keys            -> []
//   GET  gamestats/users/queries/game_stats_by_time?category&group_keys&start&end   -> [] (array of {key1,key2,sum_events})
//   POST gamestats/users/me/events/default   category, keys=a,b, aggregates=x,y,z   -> {}
// Conventions and core APIs: docs/MODULES.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.GameStats
{
    /// <summary>
    /// game_stats. The game posts matchmaking samples (score bucket, slot, players-per-minute, expected lobby time)
    /// and reads back the players-per-second distribution of its score bucket to estimate lobby times. Offline the
    /// lobby is filled with bots at once (CRM MinPlayers 1, MaxOpenTimeInSeconds 0, verify-C4), so the queries
    /// answer "no data" (an empty array: the client iterates it, <c>(obj.Token as JArray).Where</c>) and the
    /// samples are only logged (DEBUG).
    /// </summary>
    public static class GameStatsService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("gamestats");
            router.Get("gamestats/users/queries/{query}", "gamestats.query", r => HttpResponse.EmptyArray());
            router.Post("gamestats/users/{credential}/events/{kind}", "gamestats.event", r =>
            {
                log.Debug("game stats sample " + (r.Param("category") ?? "?") + " keys=" + (r.Param("keys") ?? "") + " aggregates=" + (r.Param("aggregates") ?? ""));
                return HttpResponse.EmptyObject();
            });
        }
    }
}
