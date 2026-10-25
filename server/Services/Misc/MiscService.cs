// Module: Misc - owner group: MISC
// Known-harmless endpoints (tracking, marketing hits, in-game browser pages, game portal, chat abuse reports) and the
// array-aware unknown-endpoint fallback.
// Protocol: report 02 §6 ('Safe generic stub'), §9; docs/protocol/misc.md
// Conventions and core APIs: docs/MODULES.md.

using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Misc
{
    /// <summary>
    /// The catch-all module. Everything here is registered with <see cref="Router.CatchAllOrder"/> or broad templates,
    /// so any module's specific route wins.
    /// <list type="bullet">
    /// <item><b>Tracking</b> (eve "etsv2", <c>EN/EtsRequestMessage.cs</c>): JSON event batches POSTed to the etsv2 URL
    /// exactly (path ""), answered 200 (the only status the client treats as delivered,
    /// <c>EN/TrackingComponent.cs SendTrackingRequestAsync</c>). With settings <c>tracking.disable</c> the answer
    /// is ETS's own "993 blocked, disable tracking" code, after which the TS stops sending queued tracking.</item>
    /// <item><b>Marketing hits</b> and the <b>in-game browser</b> (eve "marketing_site": <c>redir/iphoneloading.php</c>,
    /// <c>redir/?...</c>, <c>EN/TrackingHitsComponent.cs</c>, <c>EN/BrowserComponent.cs</c>): hits only check the status;
    /// the browser shows a short "not available offline" page.</item>
    /// <item><b>Game portal</b> (<c>ios/1916/public/gamePortal/gamePortal.wsgi</c>, no caller in the game) and the chat
    /// <b>abuse report</b> (<c>https://cmt.gameloft.com/api/index.php</c>, reached only if that host is redirected): 200 {}.</item>
    /// <item><b>Unknown endpoints</b> still go to the core fallback (logged in full, also to
    /// logs/unknown-endpoints.jsonl); this module only changes the answer to "[]" where the path looks like a list
    /// endpoint (report 02 §6: an object where the client expects an array breaks it).</item>
    /// </list>
    /// </summary>
    public static class MiscService
    {
        /// <summary>
        /// Last path segments of Federation endpoints that answer arrays (report 02 §6): connections, requests,
        /// members, wall, events lists, group searches, message lists, game-stats queries.
        /// </summary>
        private static readonly string[] ArraySegments =
        {
            "events", "groups", "members", "connections", "requests", "sent", "wall", "rooms", "channels", "friends",
            "game_stats_by_time", "matches", "backups",
        };

        public const string OfflinePage =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
            + "<title>MCVS Offline</title></head><body style=\"background:#111;color:#ddd;font-family:sans-serif;text-align:center;padding:2em\">"
            + "<h2>Not available offline</h2><p>This page came from Gameloft's servers, which are gone. "
            + "MCVS Offline runs the game without them.</p></body></html>";

        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("misc");

            // Tracking (ETS). Eve hands out the etsv2 URL; cover the usual shapes, including the bare base URL.
            RouteHandler tracking = request => Tracking(context, log, request);
            router.Post("", "misc.tracking.root", tracking, Router.CatchAllOrder);
            foreach (string prefix in new[] { "ets", "etsv2", "tracking" })
            {
                router.Post(prefix, "misc.tracking", tracking, Router.CatchAllOrder);
                router.Post(prefix + "/{*rest}", "misc.tracking", tracking, Router.CatchAllOrder);
            }

            // Marketing hits and in-game browser pages.
            router.Any("redir/{*rest}", "misc.marketing", request =>
                HttpResponse.Text(OfflinePage, HttpStatus.Ok, "text/html; charset=utf-8"), Router.CatchAllOrder);

            // Game portal (no caller in the game) and the chat abuse report (cmt.gameloft.com/api/index.php).
            router.Any("ios/{*rest}", "misc.gameportal", request => HttpResponse.EmptyObject(), Router.CatchAllOrder);
            router.Post("api/index.php", "misc.chat.report", request =>
            {
                log.Info("chat abuse report received and ignored (no moderators offline)");
                return HttpResponse.EmptyObject();
            }, Router.CatchAllOrder);

            InstallArrayAwareFallback(router, log);
        }

        private static HttpResponse Tracking(ServerContext context, Log log, HttpRequest request)
        {
            if (context.Settings.GetBool("tracking.disable", false))
            {
                // ETS "GGIbanned_BlockedDisableTracking": the TS turns its queued tracking off (EN/TrackingComponent.cs).
                var blocked = HttpResponse.EmptyObject();
                blocked.StatusCode = 993;
                blocked.ReasonPhrase = "Blocked Disable Tracking";
                return blocked;
            }
            return HttpResponse.EmptyObject();
        }

        /// <summary>
        /// Wraps the router's fallback: the core one still logs the unknown endpoint in full; for list-shaped paths
        /// the answer becomes "[]" instead of "{}".
        /// </summary>
        private static void InstallArrayAwareFallback(Router router, Log log)
        {
            FallbackHandler previous = router.Fallback ?? router.DefaultFallback;
            router.Fallback = (request, reason) =>
            {
                HttpResponse response = previous(request, reason) ?? HttpResponse.EmptyObject();
                if (response.StatusCode == HttpStatus.Ok && response.BodyText == "{}" && LooksLikeList(request))
                {
                    log.Debug("unknown list-shaped endpoint " + request.Method + " " + request.Path + " answered []");
                    return HttpResponse.EmptyArray();
                }
                return response;
            };
        }

        /// <summary>True for GETs whose last path segment names a list (report 02 §6), or messages/{t}/me.</summary>
        public static bool LooksLikeList(HttpRequest request)
        {
            if (request.Method != "GET" && request.Method != "HEAD") return false;
            string[] segments = request.Segments;
            if (segments.Length == 0) return false;
            string last = segments[segments.Length - 1];
            if (segments.Length == 3 && string.Equals(segments[0], "messages", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(last, "users", StringComparison.OrdinalIgnoreCase) && request.Query.Contains("device_id")) return true;
            foreach (string s in ArraySegments)
                if (string.Equals(last, s, StringComparison.OrdinalIgnoreCase)) return true;
            if (segments.Length >= 2 && string.Equals(segments[segments.Length - 2], "find", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
