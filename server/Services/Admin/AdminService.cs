// Module: admin - owner: WP2 (design docs/design/settings-page.md §10.3).
// The in-game settings and diagnostics page: page, session, ping, status, settings, codes, clipboard and actions.

using System;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>The "admin" module (skeleton of the day-0 contract commit).</summary>
    public static class AdminService
    {
        /// <summary>Registers the module. Called by <see cref="ServiceRegistry"/> after "unlocks".</summary>
        public static void Register(Router router, ServerContext context)
        {
            throw new NotImplementedException();
        }
    }
}
