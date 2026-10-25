// Module: Alerts - owner group: MISC
// Alerts (Kairos): the long-lived alert stream the TS keeps open per player.
// Protocol: report 02 §4.8; docs/protocol/misc.md
//   GET  alerts/me?push_method=streaming&alert_types=a,b&content_type=event-stream[&targets=x y]   SSE-like stream
//   POST alerts/{receiver}   alert=<json>   (scope alert_send; not used by the game)
// Conventions and core APIs: docs/MODULES.md.

using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Misc;

namespace McvsOffline.Server.Services.Alerts
{
    /// <summary>
    /// Alerts (Kairos). The stream is held open and fed by <see cref="AlertHub"/>:
    /// <list type="bullet">
    /// <item>"data: {json}" lines carry alerts; ": keep-alive" comment lines are written while idle (the client
    /// ignores every line that does not start with "data:", <c>OP/AlertListener.cs Read</c>) and let a dead client
    /// be noticed;</item>
    /// <item>after <c>alerts.streamSeconds</c> the stream ends normally. The client re-requests at once
    /// (<c>OP/AlertListener.cs Listen</c>), so a stream must never end quickly: a 200 that ends immediately makes
    /// it busy-loop (report 02 §4.8). The hold time bounds that to one request per hold.</item>
    /// </list>
    /// </summary>
    public static class AlertsService
    {
        public const string StreamContentType = "text/event-stream; charset=utf-8";

        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("alerts");
            AlertHub hub = AlertHub.For(context);

            router.Get("alerts/me", "alerts.listen", request => Listen(context, hub, log, request));
            router.Post("alerts/{receiver}", "alerts.send", request => Send(context, hub, log, request));
        }

        private static HttpResponse Listen(ServerContext context, AlertHub hub, Log log, HttpRequest request)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;

            List<string> types = Fed.ListParam(request, "alert_types", ',');
            List<string> targets = Fed.ListParam(request, "targets", ' ', ',');
            var recipients = new List<string> { caller.FedId };
            recipients.AddRange(targets);

            int streamSeconds = (int)Fed.LongSetting(context, "alerts.streamSeconds", 600, 1, 24 * 3600);
            int keepAliveSeconds = (int)Fed.LongSetting(context, "alerts.keepAliveSeconds", 20, 1, 300);

            // The same listener comes back with the same token and parameters: that identifies it for replay.
            string resumeKey = caller.Token.Token + "|" + Fed.Describe(types) + "|" + Fed.Describe(targets);
            string description = caller + " types=" + Fed.Describe(types) + " targets=" + Fed.Describe(targets);
            log.Debug("alert stream opened for " + description);

            return HttpResponse.Stream(stream =>
            {
                int alerts = 0;
                using (AlertHub.Subscription subscription = hub.Subscribe(recipients, types, resumeKey))
                {
                    stream.Write(": mcvs-offline kairos\n\n");
                    int end = Environment.TickCount + streamSeconds * 1000;
                    while (!stream.IsStopping)
                    {
                        int left = end - Environment.TickCount;
                        if (left <= 0) break;
                        List<string> batch = subscription.Take(Num.Min(left, keepAliveSeconds * 1000), () => stream.IsStopping);
                        if (batch.Count == 0)
                        {
                            stream.Write(": keep-alive\n\n");
                            continue;
                        }
                        foreach (string json in batch)
                        {
                            stream.Write("data: " + json + "\n\n");
                            alerts++;
                        }
                    }
                }
                log.Debug("alert stream for " + description + " ended after delivering " + alerts + " alert(s)");
            }, StreamContentType);
        }

        private static HttpResponse Send(ServerContext context, AlertHub hub, Log log, HttpRequest request)
        {
            string receiver = request.RouteValue("receiver");
            Caller caller = Fed.TryGetCaller(context, request);
            string recipient = receiver;
            if (receiver.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
            {
                recipient = receiver; // a listener target, not an account
            }
            else if (receiver.IndexOf(':') >= 0 || string.Equals(receiver, "me", StringComparison.OrdinalIgnoreCase))
            {
                string accountId = Fed.ResolveAccountId(context, caller, receiver);
                if (accountId == null) return Fed.NotFound("unknown receiver '" + receiver + "'");
                recipient = Fed.FedIdOf(accountId);
            }

            object alert = null;
            string text = request.Param("alert");
            if (!string.IsNullOrEmpty(text)) MiniJson.TryParse(text, out alert);
            var obj = alert as Dictionary<string, object>;
            if (obj == null || string.IsNullOrEmpty(JsonValue.GetString(obj, "type")))
                return Fed.BadRequest("alert must be a JSON object with a type");

            int delivered = hub.Push(recipient, obj);
            var body = Fed.Obj();
            body["delivered"] = (long)delivered;
            return HttpResponse.Json(body);
        }
    }
}
