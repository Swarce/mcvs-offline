// Module: Matchmaker - owner group: CONTENT (Crm, Matchmaker, Assets + server/Content + tools/content-gen)
// Matchmaker: lobby configs (cmd_args for the in-process TS/GS Worlds) and rooms.
// Protocol: ARCHITECTURE §2.2; verify-C2 §1.3-1.4; verify-C4; verify-C6 §5; report 05
// To implement:
//   GET  lobbyconfigs/{clientId}/{serverType}   ts: server_type "ts" + cmd_args with -Level Levels/Menus.level;
//                                                gs: server_type "gs", no -Level. cmd_args values: no '-' or spaces
//   POST rooms/{clientId}/quick_launch, rooms/{clientId}/automatch/{automatcher}; GET rooms/{clientId}
//   Use Settings.SuperCredential/SuperPassword/DataCenter and context.BaseUrlFor(request).
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/content.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Matchmaker
{
    /// <summary>Matchmaker: lobby configs (cmd_args for the in-process TS/GS Worlds) and rooms.</summary>
    public static class MatchmakerService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: CONTENT). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
