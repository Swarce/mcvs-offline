// Module: Pandora - owner group: AUTH (Eve, Pandora, Auth)
// Service locator (pandora): plain-text base URL per Federation service.
// Protocol: report 02 §1.3, §2.4; ARCHITECTURE §2.2; docs/protocol/auth.md
//   GET  locate/{service}?client_id=   -> text/plain "http://<host>:<port>/<service>/" (no quotes, no newline)
//   GET  locate?service=<service>      (native Gaia variant, report 02 §2.4 [S]; libnativeengine.so "/locate", "service=")
//   ANY  <service>/<path...>           service-prefixed paths from string-concatenating callers -> the root route
// Conventions and core APIs: docs/MODULES.md. Protocol notes for this group: docs/protocol/auth.md.

using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Pandora
{
    /// <summary>Service locator (pandora): plain-text base URL per Federation service.</summary>
    public static class PandoraService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("pandora");
            var warnedUrls = new HashSet<string>(StringComparer.Ordinal);

            router.Get("locate/{service}", "pandora.locate", request =>
                Locate(context, log, warnedUrls, request, request.RouteValue("service")));

            router.Get("locate", "pandora.locate.query", request =>
                Locate(context, log, warnedUrls, request, request.Param("service")));

            ServicePrefixAliases.Register(router, log, ServiceUrls.AllKnown);
        }

        private static HttpResponse Locate(ServerContext context, Log log, HashSet<string> warnedUrls, HttpRequest request, string service)
        {
            if (string.IsNullOrEmpty(service))
            {
                // Nothing to locate: hand out the host root, which serves every service anyway.
                log.Warn("locate without a service name (" + request.RawTarget + "); answering the base URL");
                return HttpResponse.Text(context.BaseUrlFor(request));
            }

            string url = ServiceUrls.For(context, request, service);
            if (!ServiceUrls.IsKnown(service))
            {
                // Still answered (every service lives at the root), but a device tester should see the name.
                log.Warn("locate of unknown service '" + service + "' -> " + url);
            }

            string problem = ServiceUrls.CheckClientUrl(url);
            if (problem != null)
            {
                bool first;
                lock (warnedUrls) first = warnedUrls.Add(url);
                if (first) log.Warn("service URL " + url + " for '" + service + "' is unsafe for the game client: " + problem);
            }

            // Plain text, read with ReadAsStringAsync (OP/Federation.cs LocateServiceTask): no JSON quotes, no newline.
            return HttpResponse.Text(url);
        }
    }
}
