// Module: Storage - owner group: STORAGE (Storage)
// Seshat profile storage: one JSON document per account, the game's only progression store.
// Protocol: verify-C3 (normative: status codes, ETag, merge, selectors); report 03; report 02 §9
// To implement:
//   GET|POST profiles/{credential}/myprofile[/{*selector}]   include_fields, operation=merge, visibility
//   POST profiles/{credential}/myprofile/delete; GET profiles/{credential}/myprofile/backups (-> [])
//   GET|POST data/{credential}/{selector}; profiles/me/myprofile/visibility; profiles/location/{cred}
//   Saves answer exactly 200; no ETag; never 403/404 for existing data; nested include_fields.
//   Core APIs: context.OpenStore("profiles") (atomic, per-key lock), JsonPath.DeepMerge/Set/Project.
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/storage.md.

using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>Seshat profile storage: one JSON document per account, the game's only progression store.</summary>
    public static class StorageService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            // Not implemented yet (owner: STORAGE). Until routes are added here, these endpoints reach the router
            // fallback, which logs the full request as an unknown endpoint and answers 200 {}.
        }
    }
}
