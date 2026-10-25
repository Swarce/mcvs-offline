// Module: Messaging - owner group: MISC (Social, Messaging, Alerts, Leaderboards, GameStats, Profanity, Chat, Clans, Wall, BanTool, Geo, Misc)
// Messaging (Hermes): per-account mailboxes (tournament rewards, gifts, coupons).
// Protocol: report 02 §4.7; verify-C5 (b)
// To implement:
//   GET  messages/{transport}/me[?delete=delete]   (array; every message needs id and created)
//   POST messages/{transport}/{credential}, messages/{transport}/multicast
//   DELETE messages/{transport}/me (msgids in the body); POST transports/{transport}/endpoints/...
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/misc.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Messaging
{
    /// <summary>Messaging (Hermes): per-account mailboxes (tournament rewards, gifts, coupons).</summary>
    public static class MessagingService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: MISC). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
