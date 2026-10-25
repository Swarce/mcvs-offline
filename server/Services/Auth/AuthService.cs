// Module: Auth - owner group: AUTH (Eve, Pandora, Auth)
// auth (Janus): authorize, users/me, verify, encrypt_token, alias, compliance.
// Protocol: report 02 §3; verify-C2 §1.2-1.6, §3; ARCHITECTURE §3 'Accounts & auth'; docs/protocol/auth.md
//   POST authorize                          password / game / for_username grants -> {access_token, token_type, expires_in, scope}
//   GET  authorize?grant_type=transfer_code
//   GET  users/me, users/{credential}       {account, credentials[]}; unknown non-me credential -> 404
//   GET  users?device_id=                   -> []
//   POST|GET users/{credential}/compliance  -> {"default":{date, compliant:true, reasons:{}}}
//   GET  users/{credential}/games/{clientId}
//   POST users/me/credentials, users/me/transfer_code, devices/mydevice
//   GET|POST encrypt_token (plain text), verify -> {credential, account, client_id, scope}
//   POST|GET games/mygame/alias -> {alias}; GET games/mygame/alias/{alias} -> {credential}
// Bad or unknown tokens get 401, never 403. Other modules check tokens through IAuthenticator (context.Features).
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/auth.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Auth
{
    /// <summary>auth (Janus): authorize, users/me, verify, encrypt_token, alias, compliance.</summary>
    public static class AuthService
    {
        /// <summary>Module data store (&lt;data&gt;/auth/): transfer codes.</summary>
        public const string StoreName = "auth";

        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("auth");
            var authenticator = new Authenticator(context, log);
            context.Features.Set<IAuthenticator>(authenticator);

            var aliases = new AliasRegistry(context.Accounts, log);
            var transferCodes = new TransferCodes(context.OpenStore(StoreName), context.Clock,
                                                  () => context.Settings.GetLong("auth.transferCodeHours", 168));
            var endpoints = new AuthEndpoints(context, log, authenticator, aliases, transferCodes);

            router.Post("authorize", "auth.authorize", endpoints.Authorize);
            router.Get("authorize", "auth.authorize", endpoints.Authorize);

            router.Get("users", "auth.users.by_device", endpoints.AccountsForDevice);
            router.Get("users/{credential}", "auth.users.get", endpoints.GetAccount);
            router.Post("users/me/credentials", "auth.users.link", endpoints.LinkCredential);
            router.Post("users/me/transfer_code", "auth.users.transfer_code", endpoints.CreateTransferCode);
            router.Post("users/{credential}/compliance", "auth.users.compliance", endpoints.Compliance);
            router.Get("users/{credential}/compliance", "auth.users.compliance", endpoints.Compliance);
            router.Get("users/{credential}/games/{clientId}", "auth.users.games", endpoints.UserPlaysGame);
            router.Post("devices/mydevice", "auth.devices.mydevice", endpoints.SetDeviceInfo);

            router.Get("encrypt_token", "auth.encrypt_token", endpoints.EncryptToken);
            router.Post("encrypt_token", "auth.encrypt_token", endpoints.EncryptToken);
            router.Get("verify", "auth.verify", endpoints.Verify);
            router.Post("verify", "auth.verify", endpoints.Verify);

            router.Post("games/{game}/alias", "auth.alias", endpoints.GetOrCreateAlias);
            router.Get("games/{game}/alias", "auth.alias", endpoints.GetOrCreateAlias);
            router.Get("games/{game}/alias/{alias}", "auth.alias.lookup", endpoints.LookupAlias);
        }
    }
}
