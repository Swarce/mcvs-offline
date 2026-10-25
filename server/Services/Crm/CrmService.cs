// Module: Crm - owner group: CONTENT (Crm, Matchmaker, Assets + server/Content + tools/content-gen)
// CRM (Hestia) configuration: the complete config document the TS/GS read.
// Protocol: ARCHITECTURE §3 'CRM'; verify-C2 §1.7; verify-C4 §3.4; verify-C5 (a)(b); report 04
// To implement:
//   GET  configs/users/me                 complete document (based on the player's Config/crmpreconfiguration.json)
//   GET  configs/users/me/{*selector}     e.g. configs/users/me/game (GS waits forever without a 200)
//   Overlay: Matchmaking {MinPlayers:1, MaxOpenTimeInSeconds:0} if Settings.InstantBots, IsDevelopment from
//   Settings.DevMenu, _OverrideTables tweaks, generated offline_store.
//   Core APIs: context.Content.TryReadGameText(...), context.Content.TryReadContentText(...).
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/content.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>CRM (Hestia) configuration: the complete config document the TS/GS read.</summary>
    public static class CrmService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: CONTENT). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
