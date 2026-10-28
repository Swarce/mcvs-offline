using System;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services
{
    /// <summary>
    /// The only place that knows every service module. Each module exposes
    /// <c>public static void Register(Router router, ServerContext context)</c> in its own folder and owns everything
    /// under that folder; modules never edit each other's files (see docs/MODULES.md for ownership and conventions).
    /// <para>
    /// Registration order only breaks ties between routes of equal <see cref="Route.Order"/>; specific templates
    /// beat catch-all templates whatever the order. Misc (the catch-all module) goes last.
    /// A module whose Register throws is logged and skipped so the rest of the server still starts (in the
    /// embedded build a crash here would take the game down).
    /// </para>
    /// </summary>
    public static class ServiceRegistry
    {
        public static void RegisterAll(Router router, ServerContext context)
        {
            RegisterAll(router, context, null);
        }

        /// <summary>
        /// As <see cref="RegisterAll(Router, ServerContext)"/>, with only the modules whose name <paramref name="include"/>
        /// accepts (null: all). For tests that leave a module out, e.g. the admin modules.
        /// </summary>
        public static void RegisterAll(Router router, ServerContext context, Func<string, bool> include)
        {
            Action<string, Action<Router, ServerContext>> Register = (module, register) =>
            {
                if (include == null || include(module)) RegisterModule(module, register, router, context);
            };

            // AUTH group
            Register("eve", Eve.EveService.Register);
            Register("pandora", Pandora.PandoraService.Register);
            Register("auth", Auth.AuthService.Register);

            // STORAGE group
            Register("storage", Storage.StorageService.Register);

            // CONTENT group
            Register("crm", Crm.CrmService.Register);
            Register("matchmaker", Matchmaker.MatchmakerService.Register);
            Register("assets", Assets.AssetsService.Register);

            // MISC group
            Register("social", Social.SocialService.Register);
            Register("messaging", Messaging.MessagingService.Register);
            Register("alerts", Alerts.AlertsService.Register);
            Register("leaderboards", Leaderboards.LeaderboardsService.Register);
            Register("gamestats", GameStats.GameStatsService.Register);
            Register("profanity", Profanity.ProfanityService.Register);
            Register("chat", Chat.ChatService.Register);
            Register("clans", Clans.ClansService.Register);
            Register("wall", Wall.WallService.Register);
            Register("bantool", BanTool.BanToolService.Register);
            Register("geo", Geo.GeoService.Register);
            Register("misc", Misc.MiscService.Register);

            // Cross-module glue (core): settings.unlockAll through the inventory ledger. Uses STORAGE's IProfileAccess,
            // CONTENT's unlocks.json groups and MISC's alerts.push topic, so it registers after all of them.
            Register("unlocks", Unlocks.UnlocksService.Register);

            // ADMIN group: the settings and diagnostics page (docs/design/settings-page.md). "admin" provides AdminApi and
            // IAdminPortal, which the other two build on. "admin-saves" goes last: it applies a staged import, restore,
            // relink or undo (and reverts one the game never loaded) before McvsServer.Start opens the port, so the game
            // never sees the state from before it. A second registration of any of the three is ignored.
            Register("admin", Admin.AdminService.Register);
            Register("admin-logs", Admin.Logs.LogsService.Register);
            Register("admin-saves", Admin.Saves.SavesService.Register);
        }

        private static void RegisterModule(string module, Action<Router, ServerContext> register, Router router, ServerContext context)
        {
            int before = router.Routes.Length;
            try
            {
                register(router, context);
                context.Log.Debug("module " + module + ": " + (router.Routes.Length - before) + " routes");
            }
            catch (Exception ex)
            {
                context.Log.Error("module " + module + " failed to register; its endpoints fall back to the unknown-endpoint handler", ex);
            }
        }
    }
}
