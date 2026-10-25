using System;
using System.Collections.Generic;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Core
{
    /// <summary>
    /// Built-in routes registered before the modules, at <see cref="Router.CoreOrder"/> so a module can override any
    /// of them (e.g. Eve may take over GET /).
    /// </summary>
    public static class CoreRoutes
    {
        public static void Register(Router router, ServerContext context)
        {
            // Connectivity.IsInternetAvailable() GETs the eve base URL and treats anything but 2xx as "offline"
            // (report 02 §2.5). The real eve answered {"eve":"on","version":"3.9.79"} (report 01 §0).
            router.Get("", "core.root", request =>
            {
                var body = new Dictionary<string, object>(StringComparer.Ordinal);
                body["eve"] = "on";
                body["version"] = "mcvs-offline/" + McvsVersion.Current;
                return HttpResponse.Json(body);
            }, Router.CoreOrder);

            // Developer diagnostics (not used by the game).
            router.Get("_mcvs/status", "core.status", request =>
            {
                var body = new Dictionary<string, object>(StringComparer.Ordinal);
                body["server"] = "mcvs-offline";
                body["version"] = McvsVersion.Current;
                body["instance"] = context.InstanceId;
                body["started"] = TimeFormat.Iso8601(context.StartedUtc);
                body["baseUrl"] = context.BaseUrlFor(request);
                body["dataDir"] = context.DataDir;
                body["dataPack"] = context.Content.DataPack == null ? null : context.Content.DataPack.DataDirectory;
                body["accounts"] = (long)context.Accounts.Count;
                body["tokens"] = (long)context.Tokens.Count;
                body["settings"] = context.Settings.Snapshot();
                return HttpResponse.Json(body);
            }, Router.CoreOrder);

            router.Get("_mcvs/routes", "core.routes", request =>
            {
                var list = new List<object>();
                foreach (Route r in router.Routes)
                {
                    var o = new Dictionary<string, object>(StringComparer.Ordinal);
                    o["method"] = r.Method;
                    o["template"] = "/" + r.Template.Text.Trim('/');
                    o["name"] = r.Name;
                    o["order"] = (long)r.Order;
                    list.Add(o);
                }
                return HttpResponse.Json(list);
            }, Router.CoreOrder);
        }
    }
}
