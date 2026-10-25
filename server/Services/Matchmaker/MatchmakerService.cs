// Module: Matchmaker - owner group: CONTENT (Crm, Matchmaker, Assets + server/Content + tools/content-gen)
// Matchmaker: lobby configs (cmd_args for the in-process TS/GS Worlds).
// Protocol: ARCHITECTURE §2.2; verify-C2 §1.3-1.4, §3 row 8; verify-C4 §4.2; verify-C6 §2.6, §5; report 06 §4.5-4.8;
// docs/protocol/content.md
//   GET  lobbyconfigs/{clientId}/{serverType}   ts: server_type "ts" + cmd_args with -Level Levels/Menus.level;
//                                                gs: server_type "gs", no -Level. cmd_args values: no '-' or spaces
// Rooms (quick_launch, automatch, find rooms) are served by the in-process LocalAnubis Finder (localhost:51750) in
// Embedded mode (EN/ClientLink.cs OverrideAnubisHost): they only reach the Federation when the client is NOT in
// Embedded mode, which this project does not support. They are answered conservatively and logged loudly.

using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Matchmaker
{
    /// <summary>Matchmaker: lobby configs (cmd_args for the in-process TS/GS Worlds).</summary>
    public static class MatchmakerService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            var log = context.Logger.For("matchmaker");

            router.Get("lobbyconfigs/{clientId}/{serverType}", "matchmaker.lobbyconfigs",
                       request => LobbyConfig(context, log, request));

            // Not expected in Embedded mode: see the header. GET rooms is a read: an empty list is harmless.
            router.Get("rooms/{clientId}", "matchmaker.rooms.find", request =>
            {
                log.Warn("GET " + request.Path + " reached the Federation: in Embedded mode room searches go to the local "
                         + "Finder (localhost:51750). Is -ServerLocation Embedded set? Answering [].");
                return HttpResponse.EmptyArray();
            });
            router.Post("rooms/{clientId}/quick_launch", "matchmaker.rooms.quicklaunch", request => NotEmbedded(log, request));
            router.Post("rooms/{clientId}/automatch/{automatcher}", "matchmaker.rooms.automatch", request => NotEmbedded(log, request));
        }

        private static HttpResponse LobbyConfig(ServerContext context, Log log, HttpRequest request)
        {
            string serverType = (request.RouteValue("serverType") ?? "").Trim().ToLowerInvariant();
            if (!IsSafeServerType(serverType))
            {
                log.Warn("lobbyconfigs: invalid server type '" + serverType + "'");
                return HttpResponse.Error(HttpStatus.NotFound, "unknown server type");
            }
            if (serverType != "ts" && serverType != "gs")
            {
                log.Warn("lobbyconfigs: unexpected server type '" + serverType + "' (the game uses ts and gs); "
                         + "answering a gs-style config without -Level");
            }
            var options = MatchmakerOptions.FromSettings(context.Settings);
            var config = LobbyConfigBuilder.Build(serverType, context.BaseUrlFor(request), context.Port, context.Settings, options, log);
            var game = (Dictionary<string, object>)config["game"];
            log.Debug("lobbyconfigs " + serverType + ": cmd_args \"" + game["cmd_args"] + "\"");
            return HttpResponse.Json(config);
        }

        private static HttpResponse NotEmbedded(Log log, HttpRequest request)
        {
            log.Warn(request.Method + " " + request.Path + " reached the Federation: rooms are created by the in-process "
                     + "LocalAnubis Finder in Embedded mode only (args.txt: -ServerLocation Embedded -AnubisCommand QuickLaunch).");
            return HttpResponse.Error(HttpStatus.NotFound, "rooms are served by the local LocalAnubis Finder (Embedded mode)");
        }

        private static bool IsSafeServerType(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 32) return false;
            foreach (char c in s)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return false;
            }
            return true;
        }
    }
}
