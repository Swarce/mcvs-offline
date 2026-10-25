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
                current = WithoutFirstSegment(current);
                Route target = FirstNonAliasMatch(router, current);
                if (target != null && IsSpecific(target))
                {
                    log.Info("#" + id + " service-prefixed path " + request.Path + " -> " + current.Path + " [" + target.Name + "]");
                    return Run(log, id, target, current);
                }
            }

            Route next = FirstNonAliasMatch(router, request);
            if (next == null)
            {
                FallbackHandler fallback = router.Fallback ?? router.DefaultFallback;
                return fallback(request, "no route matches the path, with or without its service prefix");
            }
            log.Debug("#" + id + " service-prefixed path " + request.Path + " has no specific route without the prefix; passing it unchanged to [" + next.Name + "]");
            return Run(log, id, next, CopyWithTarget(request, request.RawTarget));
        }

        /// <summary>
        /// Runs <paramref name="route"/> for <paramref name="request"/>. Route values can only be bound by a Router, so
        /// the route goes through a one-route router (which also turns exceptions into a 500, like the main one).
        /// </summary>
        private static HttpResponse Run(Log log, string id, Route route, HttpRequest request)
        {
            var single = new Router(null);
            single.Map(route.Method, route.Template.Text, route.Name, req =>
            {
                try
                {
                    return route.Handler(req);
                }
                catch (Exception ex)
                {
                    log.Error("#" + id + " handler " + route.Name + " failed for " + req.Method + " " + req.RawTarget, ex);
                    throw;
                }
            });
            return single.Dispatch(request);
        }

        /// <summary>The route the router would pick if alias routes did not exist (HEAD falls back to GET), or null.</summary>
        private static Route FirstNonAliasMatch(Router router, HttpRequest request)
        {
            Route headFallback = null;
            foreach (Route r in router.Routes)
            {
                if (r.Name.StartsWith(RouteNamePrefix, StringComparison.Ordinal)) continue;
                RouteValues ignored;
                if (!r.Template.TryMatch(request.Segments, request.RawSegments, out ignored)) continue;
                if (r.Method == "*" || r.Method == request.Method) return r;
                if (request.IsHead && r.Method == "GET" && headFallback == null) headFallback = r;
            }
            return headFallback;
        }

        /// <summary>True for module routes whose template starts with a literal segment (not a broad catch-all, not "/").</summary>
        private static bool IsSpecific(Route route)
        {
            if (route.Order >= Router.CoreOrder) return false;
            foreach (string piece in route.Template.Text.Split('/'))
            {
                if (piece.Length == 0) continue;
                return piece[0] != '{';
            }
            return false;
        }

        private static HttpRequest WithoutFirstSegment(HttpRequest request)
        {
            var sb = new StringBuilder();
            for (int i = 1; i < request.RawSegments.Length; i++) sb.Append('/').Append(request.RawSegments[i]);
            if (sb.Length == 0) sb.Append('/');
            if (request.RawQuery != null) sb.Append('?').Append(request.RawQuery);
            return CopyWithTarget(request, sb.ToString());
        }

        private static HttpRequest CopyWithTarget(HttpRequest request, string target)
        {
            var copy = new HttpRequest(request.Method, target, request.Headers, request.Body);
            copy.Id = request.Id;
            copy.ProtocolVersion = request.ProtocolVersion;
            copy.RemoteEndPoint = request.RemoteEndPoint;
            copy.LocalEndPoint = request.LocalEndPoint;
            copy.ReceivedUtc = request.ReceivedUtc;
            copy.KeepAlive = request.KeepAlive;
            return copy;
        }
    }
}
