// Module: Social - owner group: MISC (Social, Messaging, Alerts, Leaderboards, GameStats, Profanity, Chat, Clans, Wall, BanTool, Geo, Misc)
// Social (Osiris): events (TLE/seasons), connections, requests, social profile.
// Protocol: report 02 §4.4-4.5
// To implement:
//   GET  events/{id}, events?event_ids=  (array!)
//   accounts/me/connections/{type}[...], accounts/me/requests[...]  (arrays)
//   GET accounts/{credential}; POST accounts/me, accounts/me/status_line
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/misc.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Social
{
    /// <summary>Social (Osiris): events (TLE/seasons), connections, requests, social profile.</summary>
    public static class SocialService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: MISC). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
