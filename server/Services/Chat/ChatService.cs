// Module: Chat - owner group: MISC (Social, Messaging, Alerts, Leaderboards, GameStats, Profanity, Chat, Clans, Wall, BanTool, Geo, Misc)
// Group chat (clan chat): subscribe, listen stream, commands.
// Protocol: report 02 §4.1
// To implement:
//   POST chat/channels/{channel}/subscribe, chat/rooms/{room}/subscribe -> {cmd_url (ends with '/'), listen_url}
//   GET  {listen_url} (stream, one JSON object per line); POST {cmd_url}[members/{cred}|invite|...]
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/misc.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Chat
{
    /// <summary>Group chat (clan chat): subscribe, listen stream, commands.</summary>
    public static class ChatService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: MISC). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
