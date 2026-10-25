// Module: Social - owner group: MISC
// Social (Osiris): events (TLE/seasons), connections, requests, social profile.
// Protocol: report 02 §4.4-4.5, report 04 §9.1, verify-C2 §1.8; docs/protocol/misc.md
//   GET  events?event_ids=a,b&language=en  (array!)        GET events/{id}
//   POST|DELETE events/{id}/participants/{credential}[/award]
//   accounts/me/connections/{type}[/count|/import|/{credential}/delete], accounts/me/requests[/sent[/{id}/cancel]|/{id}/{op}]
//   GET accounts/{credential}; POST accounts/me, accounts/me/status_line
// Conventions and core APIs: docs/MODULES.md.

using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Clans;
using McvsOffline.Server.Services.Misc;
using McvsOffline.Server.Services.Social.LiveOps;

namespace McvsOffline.Server.Services.Social
{
    /// <summary>
    /// Social (Osiris). Events come from <see cref="SocialEvents"/> (the TS asks only for ids CRM lists, so with no
    /// TLEs in CRM nothing is requested, verify-C2 §1.8); profiles from <see cref="SocialProfiles"/>. Friends and
    /// friend requests do not exist offline: the lists are empty arrays (the client calls <c>.Where</c>/iterates on
    /// them, an object would break it) and the commands succeed without effect.
    /// </summary>
    public static class SocialService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("social");
            // Live ops (weekly division seasons, daily quests, weekly achievement events) are the default event
            // provider; LiveOpsService.For registers it unless a provider is already set (docs/LIVEOPS.md).
            LiveOpsService liveOps = LiveOpsService.For(context);
            liveOps.StartWarmup();
            var events = new SocialEvents(context);
            SocialProfiles profiles = SocialProfiles.For(context);

            router.Get("_mcvs/liveops", "social.liveops.debug", r => HttpResponse.Json(liveOps.Describe(context.Clock.UtcNow)));

            // Events (TLEs).
            router.Get("events", "social.events", r => Events(events, r));
            router.Get("events/{id}", "social.event", r => Event(events, r));
            router.Post("events/{id}/participants/{credential}/award", "social.event.award", r => Logged(log, r, "event award"));
            router.Post("events/{id}/participants/{credential}", "social.event.participate", r => Logged(log, r, "event participation"));
            router.Delete("events/{id}/participants/{credential}", "social.event.leave", r => Logged(log, r, "event unparticipation"));

            // Connections (friends) and requests: empty offline.
            router.Get("accounts/me/connections/{type}/count", "social.connections.count", r => Count(0));
            router.Post("accounts/me/connections/{type}/import", "social.connections.import", r => Logged(log, r, "friend import"));
            router.Post("accounts/me/connections/{type}/{credential}/delete", "social.connections.delete", r => Logged(log, r, "connection delete"));
            router.Get("accounts/me/connections/{type}", "social.connections.list", r => HttpResponse.EmptyArray());
            router.Post("accounts/me/connections/{type}", "social.connections.add", r => Logged(log, r, "connection request"));
            router.Get("accounts/me/requests/sent", "social.requests.sent", r => HttpResponse.EmptyArray());
            router.Post("accounts/me/requests/sent/{id}/cancel", "social.requests.cancel", r => Logged(log, r, "request cancel"));
            router.Get("accounts/me/requests", "social.requests.list", r => HttpResponse.EmptyArray());
            router.Post("accounts/me/requests/{id}/{operation}", "social.requests.answer", r => Logged(log, r, "request " + r.RouteValue("operation")));

            // Social profile.
            router.Post("accounts/me/status_line", "social.profile.status", r => StatusLine(context, profiles, log, r));
            router.Post("accounts/me", "social.profile.update", r => UpdateProfile(context, profiles, log, r));
            router.Get("accounts/{credential}", "social.profile.get", r => GetProfile(context, profiles, log, r));
        }

        private static HttpResponse Events(SocialEvents events, HttpRequest request)
        {
            List<string> ids = Fed.ListParam(request, "event_ids", ',');
            return HttpResponse.Json(events.Find(ids, request.Param("language") ?? "en"));
        }

        private static HttpResponse Event(SocialEvents events, HttpRequest request)
        {
            string id = request.RouteValue("id");
            Dictionary<string, object> e = events.FindOne(id, request.Param("language") ?? "en");
            return e == null ? Fed.NotFound("no event '" + id + "'") : HttpResponse.Json(e);
        }

        private static HttpResponse GetProfile(ServerContext context, SocialProfiles profiles, Log log, HttpRequest request)
        {
            Caller caller = Fed.TryGetCaller(context, request);
            string credential = request.RouteValue("credential");
            if (caller == null && string.Equals(credential, "me", StringComparison.OrdinalIgnoreCase))
            {
                Caller ignored;
                HttpResponse failure;
                Fed.RequireCaller(context, request, log, out ignored, out failure);
                return failure;
            }
            string accountId = Fed.ResolveAccountId(context, caller, credential);
            // GetSocialProfile treats any failure as "no profile" (OP/SocialProfileService.cs).
            if (accountId == null) return Fed.NotFound("no social profile for '" + credential + "'");

            Dictionary<string, object> stored = profiles.Get(accountId);
            var body = Fed.Obj();
            body["credential"] = Fed.FedIdOf(accountId);
            body["name"] = JsonValue.GetString(stored, "name") ?? "";
            var groups = new List<object>();
            foreach (string id in ClanStore.For(context).GroupsOf(accountId)) groups.Add(id);
            body["groups"] = groups;
            body["language"] = JsonValue.GetString(stored, "language") ?? "";
            body["country"] = JsonValue.GetString(stored, "country") ?? "";
            body["status_line"] = JsonValue.GetString(stored, "status_line") ?? "";
            return HttpResponse.Json(body);
        }

        private static HttpResponse UpdateProfile(ServerContext context, SocialProfiles profiles, Log log, HttpRequest request)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;
            profiles.Update(caller.AccountId, request.Param("name"), request.Param("language"), request.Param("country"));
            log.Info("social profile of " + caller.FedId + " updated (name=" + (request.Param("name") ?? "-") + ")");
            return HttpResponse.EmptyObject();
        }

        private static HttpResponse StatusLine(ServerContext context, SocialProfiles profiles, Log log, HttpRequest request)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;
            string line = request.Param("status_line") ?? "";
            if (line.Length > 250) line = line.Substring(0, 250);
            profiles.SetStatusLine(caller.AccountId, line);
            return HttpResponse.EmptyObject();
        }

        private static HttpResponse Count(long count)
        {
            var body = Fed.Obj();
            body["count"] = count;
            return HttpResponse.Json(body);
        }

        /// <summary>A command that has no effect offline: logged so a tester sees it, answered 200 {}.</summary>
        private static HttpResponse Logged(Log log, HttpRequest request, string what)
        {
            log.Info(what + " accepted without effect (no other players offline): " + request.Method + " " + request.Path);
            return HttpResponse.EmptyObject();
        }
    }
}
