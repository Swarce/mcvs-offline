// Module: Alerts - owner group: MISC (Social, Messaging, Alerts, Leaderboards, GameStats, Profanity, Chat, Clans, Wall, BanTool, Geo, Misc)
// Alerts (Kairos): the long-lived alert stream.
// Protocol: report 02 §4.8
// To implement:
//   GET  alerts/me?push_method=streaming...   text/event-stream, keep it open (HttpResponse.Stream), 'data: {json}'
//   POST /alerts/{receiver}
//   Never send {"type":"connection"} (kicks the player). An immediately-ending 200 makes the client busy-loop.
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/misc.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Alerts
{
    /// <summary>Alerts (Kairos): the long-lived alert stream.</summary>
    public static class AlertsService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: MISC). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
