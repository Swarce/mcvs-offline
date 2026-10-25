using System;
using System.Collections.Generic;
using McvsOffline.Server.Core.Http;

namespace McvsOffline.Server.Core.Routing
{
    /// <summary>A route handler. Return a response; throwing produces a logged 500.</summary>
    public delegate HttpResponse RouteHandler(HttpRequest request);

    /// <summary>Handles requests no route matched. <paramref name="reason"/> says why (for the log).</summary>
    public delegate HttpResponse FallbackHandler(HttpRequest request, string reason);

    /// <summary>A registered route.</summary>
    public sealed class Route
    {
        internal Route(string method, RouteTemplate template, string name, RouteHandler handler, int order, int index)
        {
            Method = method;
            Template = template;
            Name = name;
            Handler = handler;
            Order = order;
            Index = index;
        }

        /// <summary>Upper-case method, or "*" for any.</summary>
        public string Method { get; private set; }

        public RouteTemplate Template { get; private set; }

        /// <summary>Name shown in the request log, conventionally "module.action" (e.g. "auth.authorize").</summary>
        public string Name { get; private set; }

        public RouteHandler Handler { get; private set; }

        /// <summary>Lower orders are tried first; ties keep registration order.</summary>
        public int Order { get; private set; }

        internal int Index { get; private set; }

        public override string ToString()
        {
            return Method + " /" + Template.Text.TrimStart('/') + " [" + Name + "]";
        }
    }

    /// <summary>
    /// Maps (method, path) to handlers.
    /// <para>
    /// Matching is ordered: routes are tried by ascending <see cref="Route.Order"/>, then registration order, and the
    /// first match wins. Templates without a catch-all default to <see cref="DefaultOrder"/>; templates with a
    /// catch-all default to <see cref="CatchAllOrder"/>, so specific routes registered by any module win over broad
    /// ones regardless of which module registered first. HEAD requests use GET routes unless a HEAD route exists.
    /// </para>
    /// <para>
    /// Unmatched requests go to <see cref="Fallback"/>; the default fallback logs the full request as an unknown
    /// endpoint and answers 200 "{}" (the conservative answer, report 02 §6). Register routes during startup only.
    /// </para>
    /// </summary>
    public sealed class Router
    {
        public const int DefaultOrder = 0;
        public const int CatchAllOrder = 1000;

        /// <summary>Order for built-in core routes (GET /, /_mcvs/*): any module route wins over them.</summary>
        public const int CoreOrder = 10000;

        private readonly object gate = new object();
        private readonly List<Route> routes = new List<Route>();
        private readonly RequestLogger requestLogger;
        private Route[] sorted = new Route[0];

        public Router(RequestLogger requestLogger)
        {
            this.requestLogger = requestLogger;
            Fallback = DefaultFallback;
        }

        public FallbackHandler Fallback { get; set; }

        /// <summary>Routes in matching order.</summary>
        public Route[] Routes
        {
            get { lock (gate) return (Route[])sorted.Clone(); }
        }

        /// <summary>
        /// Registers a route. <paramref name="method"/> is GET/POST/DELETE/PUT/HEAD or "*" (any);
        /// <paramref name="template"/> uses {param} and a final {*rest}; <paramref name="order"/> overrides the default.
        /// </summary>
        public Route Map(string method, string template, string name, RouteHandler handler, int? order = null)
        {
            if (string.IsNullOrEmpty(method)) throw new ArgumentException("method required", "method");
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("route name required", "name");
            if (handler == null) throw new ArgumentNullException("handler");
            RouteTemplate parsed = RouteTemplate.Parse(template);
            lock (gate)
            {
                var route = new Route(method.ToUpperInvariant(), parsed, name, handler,
                                      order ?? (parsed.HasCatchAll ? CatchAllOrder : DefaultOrder), routes.Count);
                routes.Add(route);
                var copy = routes.ToArray();
                Array.Sort(copy, CompareRoutes);
                sorted = copy;
                return route;
            }
        }

        public Route Get(string template, string name, RouteHandler handler, int? order = null)
        {
            return Map("GET", template, name, handler, order);
        }

        public Route Post(string template, string name, RouteHandler handler, int? order = null)
        {
            return Map("POST", template, name, handler, order);
        }

        public Route Delete(string template, string name, RouteHandler handler, int? order = null)
        {
            return Map("DELETE", template, name, handler, order);
        }

        public Route Put(string template, string name, RouteHandler handler, int? order = null)
        {
            return Map("PUT", template, name, handler, order);
        }

        /// <summary>Any method.</summary>
        public Route Any(string template, string name, RouteHandler handler, int? order = null)
        {
            return Map("*", template, name, handler, order);
        }

        /// <summary>
        /// Finds the route for a request. On failure <paramref name="reason"/> explains it (e.g. which methods the
        /// path does accept).
        /// </summary>
        public bool TryMatch(HttpRequest request, out Route route, out RouteValues values, out string reason)
        {
            return TryMatch(request, null, out route, out values, out reason);
        }

        /// <summary>
        /// Like <see cref="TryMatch(HttpRequest, out Route, out RouteValues, out string)"/>, ignoring every route for which
        /// <paramref name="skip"/> returns true (e.g. a forwarding route looking for what would answer without it).
        /// </summary>
        public bool TryMatch(HttpRequest request, Func<Route, bool> skip, out Route route, out RouteValues values, out string reason)
        {
            route = null;
            values = null;
            reason = null;
            Route[] current;
            lock (gate) current = sorted;

            List<string> otherMethods = null;
            Route headCandidate = null;
            RouteValues headValues = null;
            foreach (Route r in current)
            {
                if (skip != null && skip(r)) continue;
                RouteValues v;
                if (!r.Template.TryMatch(request.Segments, request.RawSegments, out v)) continue;
                if (r.Method == "*" || r.Method == request.Method)
                {
                    route = r;
                    values = v;
                    return true;
                }
                if (request.IsHead && r.Method == "GET" && headCandidate == null)
                {
                    headCandidate = r;
                    headValues = v;
                    continue;
                }
                if (otherMethods == null) otherMethods = new List<string>();
                if (!otherMethods.Contains(r.Method)) otherMethods.Add(r.Method);
            }
            if (headCandidate != null)
            {
                route = headCandidate;
                values = headValues;
                return true;
            }
            reason = otherMethods == null
                ? "no route matches the path"
                : "path exists for " + string.Join(", ", otherMethods.ToArray()) + " but not for " + request.Method;
            return false;
        }

        /// <summary>Routes the request and runs the handler; never throws.</summary>
        public HttpResponse Dispatch(HttpRequest request)
        {
            return Dispatch(request, null);
        }

        /// <summary>
        /// Routes the request ignoring the routes <paramref name="skip"/> selects, and runs the handler (or the
        /// fallback); never throws. Used to re-dispatch from inside a route, e.g. with <see cref="HttpRequest.WithTarget"/>.
        /// </summary>
        public HttpResponse Dispatch(HttpRequest request, Func<Route, bool> skip)
        {
            Route route;
            RouteValues values;
            string reason;
            if (TryMatch(request, skip, out route, out values, out reason)) return Run(route, values, request);

            request.SetRoute("unknown", RouteValues.Empty);
            try
            {
                return (Fallback ?? DefaultFallback)(request, reason) ?? HttpResponse.EmptyObject();
            }
            catch (Exception ex)
            {
                if (requestLogger != null) requestLogger.LogHandlerError(request, ex);
                return HttpResponse.Error(HttpStatus.InternalServerError, "internal error in fallback");
            }
        }

        /// <summary>
        /// Runs one specific route for a request whose path that route's template matches (route values are bound
        /// from the request); a handler exception becomes a logged 500, as in <see cref="Dispatch(HttpRequest)"/>.
        /// </summary>
        public HttpResponse Invoke(Route route, HttpRequest request)
        {
            if (route == null) throw new ArgumentNullException("route");
            if (request == null) throw new ArgumentNullException("request");
            RouteValues values;
            if (!route.Template.TryMatch(request.Segments, request.RawSegments, out values))
                throw new ArgumentException("route " + route + " does not match " + request.Path, "request");
            return Run(route, values, request);
        }

        private HttpResponse Run(Route route, RouteValues values, HttpRequest request)
        {
            request.SetRoute(route.Name, values);
            try
            {
                return route.Handler(request) ?? HttpResponse.EmptyObject();
            }
            catch (Exception ex)
            {
                if (requestLogger != null) requestLogger.LogHandlerError(request, ex);
                return HttpResponse.Error(HttpStatus.InternalServerError, "internal error in " + route.Name);
            }
        }

        /// <summary>Logs the request as an unknown endpoint and answers 200 "{}".</summary>
        public HttpResponse DefaultFallback(HttpRequest request, string reason)
        {
            if (requestLogger != null) requestLogger.LogUnknown(request, reason);
            return HttpResponse.EmptyObject();
        }

        private static int CompareRoutes(Route a, Route b)
        {
            int c = a.Order.CompareTo(b.Order);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        }
    }
}
