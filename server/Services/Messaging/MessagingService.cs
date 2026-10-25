// Module: Messaging - owner group: MISC
// Messaging (Hermes): per-account mailboxes (tournament rewards, CRM gifts, multiple-connection notices).
// Protocol: report 02 §4.7; verify-C5 (b); docs/protocol/misc.md
//   GET    messages/{transport}/me[?delete=delete]     array; every message has id and created
//   POST   messages/{transport}/{credential}           body, delay, type=gift?, alert_kairos, message_label, custom fields
//   POST   messages/{transport}/multicast              body, credentials=<json array>, ...
//   DELETE messages/{transport}/me                     msgids=a,b in the body (all when absent)
//   POST   transports/{transport}/endpoints/{endpoint}, transports/{transport}/endpoints/delete   (push registration)
// Conventions and core APIs: docs/MODULES.md.

using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Misc;

namespace McvsOffline.Server.Services.Messaging
{
    /// <summary>Messaging (Hermes): per-account mailboxes, see <see cref="Mailboxes"/>.</summary>
    public static class MessagingService
    {
        /// <summary>Request parameters that steer delivery and are not stored on the message.</summary>
        private static readonly HashSet<string> ControlParameters = new HashSet<string>(StringComparer.Ordinal)
        {
            "access_token", "body", "delay", "attachment", "message_label", "rate_limit_count", "rollover",
            "replace_matching_label", "type", "alert_kairos", "credentials",
        };

        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("messaging");
            Mailboxes mailboxes = Mailboxes.For(context);

            router.Get("messages/{transport}/{credential}", "messaging.retrieve", request => Retrieve(context, mailboxes, log, request));
            // "multicast" is a literal; registered before the {credential} route of the same shape.
            router.Post("messages/{transport}/multicast", "messaging.multicast", request => Multicast(context, mailboxes, log, request));
            router.Post("messages/{transport}/{credential}", "messaging.post", request => Post(context, mailboxes, log, request));
            router.Delete("messages/{transport}/{credential}", "messaging.delete", request => Delete(context, mailboxes, log, request));
            // Push-notification endpoint registration: there is no push service offline.
            router.Post("transports/{transport}/endpoints/delete", "messaging.endpoint.delete", request => HttpResponse.EmptyObject());
            // The endpoint is the device's raw push token, not escaped by the client (OP/MessagingApi.cs): take the rest of the path.
            router.Post("transports/{transport}/endpoints/{*endpoint}", "messaging.endpoint.register", request => HttpResponse.EmptyObject());
        }

        private static HttpResponse Retrieve(ServerContext context, Mailboxes mailboxes, Log log, HttpRequest request)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;
            string accountId = Fed.ResolveAccountId(context, caller, request.RouteValue("credential"));
            if (accountId == null) return Fed.NotFound("unknown credential '" + request.RouteValue("credential") + "'");
            bool delete = string.Equals(request.Param("delete"), "delete", StringComparison.OrdinalIgnoreCase) || Fed.BoolParam(request, "delete", false);
            List<object> messages = mailboxes.Retrieve(accountId, request.RouteValue("transport"), delete);
            return HttpResponse.Json(messages);
        }

        private static HttpResponse Post(ServerContext context, Mailboxes mailboxes, Log log, HttpRequest request)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;
            string credential = request.RouteValue("credential");
            string accountId = Fed.ResolveAccountId(context, caller, credential);
            if (accountId == null) return Fed.NotFound("unknown recipient '" + credential + "'");

            OutgoingMessage message = ReadMessage(request, caller);
            string problem = Mailboxes.Validate(message);
            if (problem != null)
            {
                log.Warn("message to " + credential + " rejected: " + problem);
                return Fed.BadRequest(problem);
            }
            Dictionary<string, object> stored = mailboxes.Post(accountId, request.RouteValue("transport"), message);
            var body = Fed.Obj();
            if (stored != null) body["id"] = stored["id"];
            return HttpResponse.Json(body);
        }

        private static HttpResponse Multicast(ServerContext context, Mailboxes mailboxes, Log log, HttpRequest request)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;

            var recipients = new List<string>();
            object parsed;
            string credentials = request.Param("credentials");
            if (!string.IsNullOrEmpty(credentials) && MiniJson.TryParse(credentials, out parsed) && parsed is List<object>)
            {
                foreach (object item in (List<object>)parsed) recipients.Add(JsonValue.AsString(item));
            }
            else
            {
                recipients.AddRange(Fed.ListParam(request, "credentials", ','));
            }

            OutgoingMessage message = ReadMessage(request, caller);
            string problem = Mailboxes.Validate(message);
            if (problem != null) return Fed.BadRequest(problem);

            var ids = new List<object>();
            foreach (string recipient in recipients)
            {
                string accountId = Fed.ResolveAccountId(context, caller, recipient);
                if (accountId == null)
                {
                    log.Warn("multicast: skipping unknown recipient '" + recipient + "'");
                    continue;
                }
                Dictionary<string, object> stored = mailboxes.Post(accountId, request.RouteValue("transport"), message);
                if (stored != null) ids.Add(stored["id"]);
            }
            var body = Fed.Obj();
            body["count"] = (long)ids.Count;
            body["ids"] = ids;
            return HttpResponse.Json(body);
        }

        private static HttpResponse Delete(ServerContext context, Mailboxes mailboxes, Log log, HttpRequest request)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;
            string accountId = Fed.ResolveAccountId(context, caller, request.RouteValue("credential"));
            if (accountId == null) return Fed.NotFound("unknown credential '" + request.RouteValue("credential") + "'");
            // DELETE carries access_token in the query and every parameter in the body (report 02 §1.4).
            List<string> ids = request.HasParam("msgids") ? Fed.ListParam(request, "msgids", ',') : null;
            int removed = mailboxes.Delete(accountId, request.RouteValue("transport"), ids);
            var body = Fed.Obj();
            body["deleted"] = (long)removed;
            return HttpResponse.Json(body);
        }

        private static OutgoingMessage ReadMessage(HttpRequest request, Caller caller)
        {
            var message = new OutgoingMessage();
            message.Body = request.Param("body") ?? "";
            message.Type = request.Param("type");
            message.Attachment = request.Param("attachment");
            message.Label = request.Param("message_label");
            message.ReplaceMatchingLabel = Fed.BoolParam(request, "replace_matching_label", false);
            message.DelaySeconds = Fed.IntParam(request, "delay", 0, 0, 365 * 24 * 3600);
            message.Alert = Fed.BoolParam(request, "alert_kairos", false);
            message.SenderFedId = caller == null ? "" : caller.FedId;
            // Custom fields are plain strings (DefaultCustomPropertiesValueConverter): store them as sent.
            foreach (var kv in request.Form.Count > 0 ? request.Form : request.Query)
            {
                if (ControlParameters.Contains(kv.Key) || message.Fields.ContainsKey(kv.Key)) continue;
                message.Fields[kv.Key] = kv.Value;
            }
            return message;
        }
    }
}
