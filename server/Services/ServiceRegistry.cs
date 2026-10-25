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
            // AUTH group
            Register("eve", Eve.EveService.Register, router, context);
            Register("pandora", Pandora.PandoraService.Register, router, context);
            Register("auth", Auth.AuthService.Register, router, context);

            // STORAGE group
            Register("storage", Storage.StorageService.Register, router, context);

            // CONTENT group
            Register("crm", Crm.CrmService.Register, router, context);
            Register("matchmaker", Matchmaker.MatchmakerService.Register, router, context);
            Register("assets", Assets.AssetsService.Register, router, context);

            // MISC group
            Register("social", Social.SocialService.Register, router, context);
            Register("messaging", Messaging.MessagingService.Register, router, context);
            Register("alerts", Alerts.AlertsService.Register, router, context);
            Register("leaderboards", Leaderboards.LeaderboardsService.Register, router, context);
            Register("gamestats", GameStats.GameStatsService.Register, router, context);
            Register("profanity", Profanity.ProfanityService.Register, router, context);
            Register("chat", Chat.ChatService.Register, router, context);
            Register("clans", Clans.ClansService.Register, router, context);
            Register("wall", Wall.WallService.Register, router, context);
            Register("bantool", BanTool.BanToolService.Register, router, context);
            Register("geo", Geo.GeoService.Register, router, context);
            Register("misc", Misc.MiscService.Register, router, context);

            // Cross-module glue (core): settings.unlockAll through the inventory ledger. Uses STORAGE's IProfileAccess,
            // CONTENT's unlocks.json groups and MISC's alerts.push topic, so it registers after all of them.
            Register("unlocks", Unlocks.UnlocksService.Register, router, context);
        }

        private static void Register(string module, Action<Router, ServerContext> register, Router router, ServerContext context)
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
