using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Pandora
{
    /// <summary>
    /// Routes "/&lt;service&gt;/&lt;path&gt;" to "/&lt;path&gt;" for the service names we hand out.
    /// <para>
    /// The C# client resolves paths against our "&lt;base&gt;&lt;service&gt;/" URLs with RFC 3986 rules and so always
    /// reaches the root routes (see <see cref="ServiceUrls"/>). Callers that concatenate strings do not:
    /// <c>DownloadableContent.RefreshServerAddressAsync</c> requests <c>pandora + "/locate/asset"</c>
    /// (EN/DownloadableContent.cs:401-411) and the native Gaia layer carries "/authorize", "/users/me", "/locate",
    /// "/configs/users/me/iap" path literals (libnativeengine.so). With our URLs those become e.g.
    /// "/auth//authorize" or "/pandora//locate/asset", which this class maps to the root route.
    /// </para>
    /// <para>
    /// Each alias is a route "&lt;service&gt;/{first}/{*rest}" at <c>Router.CatchAllOrder - 1</c>: every exact route
    /// (any module, order 0) still wins, and the alias runs before broad catch-alls. The prefix (or a chain of
    /// prefixes) is stripped only when the remaining path hits a specific route, i.e. one whose template starts with
    /// a literal. Otherwise the original request goes to the first non-alias route that matches it, as if no alias
    /// existed: a broad catch-all still sees the real path, and an unmatched request is logged as unknown with the
    /// path the client actually sent.
    /// </para>
    /// </summary>
    public static class ServicePrefixAliases
    {
        public const string RouteNamePrefix = "pandora.prefix.";

        public static void Register(Router router, Log log, IEnumerable<string> services)
        {
            var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string service in services)
            {
                if (!prefixes.Add(service)) continue;
                router.Any(service + "/{first}/{*rest}", RouteNamePrefix + service,
                           request => Handle(router, log, prefixes, request), Router.CatchAllOrder - 1);
            }
        }

        private static HttpResponse Handle(Router router, Log log, HashSet<string> prefixes, HttpRequest request)
        {
            string id = request.Id.ToString(CultureInfo.InvariantCulture);
            HttpRequest current = request;
            while (current.Segments.Length >= 2 && prefixes.Contains(current.Segments[0]))
            {
                current = current.WithTarget(WithoutFirstSegment(current));
                Route target = FirstNonAliasMatch(router, current);
                if (target != null && IsSpecific(target))
                {
                    log.Info("#" + id + " service-prefixed path " + request.Path + " -> " + current.Path + " [" + target.Name + "]");
                    return router.Invoke(target, current);
                }
            }

            Route next = FirstNonAliasMatch(router, request);
            if (next == null)
            {
                FallbackHandler fallback = router.Fallback ?? router.DefaultFallback;
                return fallback(request, "no route matches the path, with or without its service prefix");
            }
            log.Debug("#" + id + " service-prefixed path " + request.Path + " has no specific route without the prefix; passing it unchanged to [" + next.Name + "]");
            // The original request, re-routed: its log line then names the route that really answered it.
            return router.Invoke(next, request);
        }

        /// <summary>The route the router would pick if alias routes did not exist (HEAD falls back to GET), or null.</summary>
        private static Route FirstNonAliasMatch(Router router, HttpRequest request)
        {
            Route route;
            RouteValues ignored;
            string reason;
            return router.TryMatch(request, IsAlias, out route, out ignored, out reason) ? route : null;
        }

        private static bool IsAlias(Route route)
        {
            return route.Name.StartsWith(RouteNamePrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// True for module routes whose template starts with a literal segment (not a broad catch-all, not "/"), except
        /// "_mcvs": "/auth/_mcvs/..." must never reach a settings-page or diagnostics route, which the gate guards by the
        /// request's own path (design §5.2).
        /// </summary>
        private static bool IsSpecific(Route route)
        {
            if (route.Order >= Router.CoreOrder) return false;
            foreach (string piece in route.Template.Text.Split('/'))
            {
                if (piece.Length == 0) continue;
                return piece[0] != '{' && !string.Equals(piece, "_mcvs", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        /// <summary>The request-target without the first path segment (query kept).</summary>
        private static string WithoutFirstSegment(HttpRequest request)
        {
            var sb = new StringBuilder();
            for (int i = 1; i < request.RawSegments.Length; i++) sb.Append('/').Append(request.RawSegments[i]);
            if (sb.Length == 0) sb.Append('/');
            if (request.RawQuery != null) sb.Append('?').Append(request.RawQuery);
            return sb.ToString();
        }
    }
}
