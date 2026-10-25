// Module: Auth - owner group: AUTH (Eve, Pandora, Auth)
// auth (Janus): authorize, users/me, verify, encrypt_token, alias, compliance.
// Protocol: report 02 §3; verify-C2 §1.2-1.6, §3; ARCHITECTURE §3 'Accounts & auth'
// To implement:
//   POST authorize                      password / game / for_username grants -> {access_token, scope}
//   GET  authorize?grant_type=transfer_code
//   GET  users/me, users/{credential}, users?device_id= (-> [])
//   POST|GET users/{credential}/compliance
//   GET  encrypt_token (plain text), verify -> {credential, account, client_id}
//   POST games/mygame/alias, GET games/mygame/alias/{alias}
//   POST users/me/credentials, users/me/transfer_code, devices/mydevice
//   Core APIs: context.Accounts (AccountStore), context.Tokens (TokenService). 401, never 403.
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/auth.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Auth
{
    /// <summary>auth (Janus): authorize, users/me, verify, encrypt_token, alias, compliance.</summary>
    public static class AuthService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: AUTH). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
