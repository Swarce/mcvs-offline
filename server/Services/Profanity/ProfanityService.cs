// Module: Profanity - owner group: MISC (Social, Messaging, Alerts, Leaderboards, GameStats, Profanity, Chat, Clans, Wall, BanTool, Geo, Misc)
// Profanity filter: on the critical path for setting the player name.
// Protocol: report 02 §4.2
// To implement:
//   POST chat/words/{lang}/filter, chat/words/filter  -> {"matches":false,"result":<message>}
//   GET chat/words; POST chat/words/{lang}/{word}[/delete]
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/misc.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Profanity
{
    /// <summary>Profanity filter: on the critical path for setting the player name.</summary>
    public static class ProfanityService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: MISC). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
