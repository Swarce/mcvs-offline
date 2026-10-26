// Module: admin - owner: WP2 (design docs/design/settings-page.md §10.3).
// The in-game settings and diagnostics page: page, session, ping, status, settings, codes, clipboard and actions.
//   GET  /_mcvs/ui[/|/index.html]           the page (server/Content/admin/index.html, compiled in)
//   POST /_mcvs/ui                          post mode (admin.openVia=post): form t=<ticket> -> 303 + cookie
//   POST|DELETE /_mcvs/api/v1/session       one-time ticket -> session cookie mcvs_s; close
//   GET  /_mcvs/api/v1/ping                 liveness (no data), renews the host's watchdog lease with a session
//   GET  /_mcvs/api/v1/status[?storage=1]   server, host, player, problems, CRM, storage usage
//   GET|POST settings, POST settings/reset, GET settings/file, POST settings/repair, GET codes
//   POST actions/save-now|restart|crm-reload|unlock-now, POST clipboard
//   any  /_mcvs/api/{*rest}                 404 not_found (no unknown-endpoint logging)
// Security (LocalAccessGate in front of every route, tickets, sessions, CSRF): design §5. Features provided: AdminApi
// (the framework the admin-logs and admin-saves modules build on) and IAdminPortal (hosts and the CRM inbox item).

using System;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>The "admin" module: the settings page's framework, security, settings, status and actions.</summary>
    public static class AdminService
    {
        /// <summary>
        /// Registers the module. Called by <see cref="ServiceRegistry"/> after "unlocks" (and before "admin-logs" and
        /// "admin-saves", which build on <see cref="AdminApi"/>). A second call on the same context is ignored.
        /// </summary>
        public static void Register(Router router, ServerContext context)
        {
            Register(router, context, null);
        }

        /// <summary>As <see cref="Register(Router, ServerContext)"/>, with the exit sequence's timings (tests shorten them).</summary>
        public static void Register(Router router, ServerContext context, ExitTimings timings)
        {
            Log log = context.Logger.For("admin");
            AdminApi registered = context.Features.Get<AdminApi>();
            if (registered != null)
            {
                // Tests register the module themselves (with short timings) on top of a registry that may already have it.
                if (timings != null && registered.Exit != null) registered.Exit.Timings = timings;
                log.Debug("admin: the module is already registered; the second registration is ignored");
                return;
            }

            var security = new AdminSecurity(context.Clock);
            var saves = new GameSaveTracker(context.Events, context.Clock);
            var api = new AdminApi(router, context, security, saves, new CurrentPlayerResolver(context));
            var portal = new AdminPortal(api, security);
            var exit = new ExitSequence(
                () => context.Features.Get<IAdminHost>(),
                saves,
                () => api.CurrentPlayer(),
                account =>
                {
                    var store = context.Features.Get<LocalProfileStore>();
                    return store == null ? null : store.Locks.Acquire(account);
                },
                ms => context.Logger.Flush(ms),
                (screen, saved) => portal.OpenPage(AdminTriggers.Resume, screen, !saved),
                context.Clock, log, timings);
            api.Exit = exit;

            context.Features.Set(api);
            context.Features.Set<IAdminPortal>(portal);
            context.IsValidAdminSession = security.IsValidCookie;
            context.AdminRouteBodyLimit = security.RouteLimit;

            var settings = new SettingsApi(api);
            new PageRoutes(api, security).Register(router);
            new StatusApi(api, settings).Register();
            settings.Register();
            new ActionsApi(api, exit).Register();

            log.Info("admin: settings page at " + "http://127.0.0.1:" + context.Port + "/_mcvs/ui/"
                     + (api.Enabled ? "" : " (turned off: admin.enabled is false)"));
        }
    }
}
