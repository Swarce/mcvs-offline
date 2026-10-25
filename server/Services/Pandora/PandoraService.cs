// Module: Pandora - owner group: AUTH (Eve, Pandora, Auth)
// Service locator (pandora): plain-text base URL per Federation service.
// Protocol: report 02 §2.4; ARCHITECTURE §2.2
// To implement:
//   GET  locate/{service}?client_id=   -> text/plain base URL (no quotes, no newline)
//   GET  locate?service=               (native Gaia variant, report 02 §2.4 [S])
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/auth.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Pandora
{
    /// <summary>Service locator (pandora): plain-text base URL per Federation service.</summary>
    public static class PandoraService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: AUTH). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
