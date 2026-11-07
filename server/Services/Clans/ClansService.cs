// Module: Clans - owner group: MISC
// Clans (Osiris groups), stored locally: a player can create, run and leave a (solo) clan offline.
// Protocol: report 02 §4.3; docs/protocol/misc.md
//   POST groups; POST groups/{id} (update); POST groups/{id}/delete; POST groups/{id}/to_clan; GET groups/{id}
//   GET|POST groups/config; GET|POST groups/{id}/fields/{field} (404 = default value; object=, operation=inc|dec)
//   GET groups?category&limit&offset[&random]; GET groups/categories/{cat}/find/{matchmaking_score|recommendation_score|keyword}
//   POST groups/{id}/members (credential=me joins); POST groups/{id}/members/{cred}/delete; GET groups/{id}/members[/{cred}]
//   POST groups/{id}/members/{cred}  (_field=_json_:<json> custom member fields)
// Conventions and core APIs: docs/MODULES.md.

using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Misc;

namespace McvsOffline.Server.Services.Clans
{
    /// <summary>Clans (Osiris groups), see <see cref="ClanStore"/>.</summary>
    public static class ClansService
    {
        private static readonly HashSet<string> ControlParameters = new HashSet<string>(StringComparer.Ordinal)
        {
            "access_token", "promote_owners", "demote_owners", "credential", "alert_kairos", "object", "operation",
        };

        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("clans");
            ClanStore clans = ClanStore.For(context);

            router.Get("groups/config", "clans.config.get", r => HttpResponse.EmptyObject());
            router.Post("groups/config", "clans.config.set", r => HttpResponse.EmptyObject());
            router.Get("groups/categories/{category}/find/{kind}", "clans.find", r => Search(clans, r, r.RouteValue("category"), r.Param("keywords")));
            router.Get("groups", "clans.search", r => Search(clans, r, r.Param("category"), null));
            router.Post("groups", "clans.create", r => Create(context, clans, log, r));

            router.Get("groups/{id}", "clans.get", r => Show(clans, r));
            router.Post("groups/{id}", "clans.update", r => Update(context, clans, log, r));
            router.Post("groups/{id}/delete", "clans.delete", r => Delete(context, clans, log, r));
            router.Post("groups/{id}/to_clan", "clans.to_clan", r => ToClan(clans, r));
            router.Get("groups/{id}/fields/{field}", "clans.field.get", r => GetField(clans, r));
            router.Post("groups/{id}/fields/{field}", "clans.field.set", r => SetField(clans, log, r));

            router.Get("groups/{id}/members", "clans.members", r => Members(clans, r));
            router.Post("groups/{id}/members", "clans.members.add", r => AddMember(context, clans, log, r));
            router.Get("groups/{id}/members/{credential}", "clans.member", r => Member(context, clans, r));
            router.Post("groups/{id}/members/{credential}/delete", "clans.members.remove", r => RemoveMember(context, clans, log, r));
            router.Post("groups/{id}/members/{credential}", "clans.member.fields", r => MemberFields(context, clans, log, r));
        }

        private static HttpResponse Create(ServerContext context, ClanStore clans, Log log, HttpRequest request)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;
            if (string.IsNullOrEmpty(request.Param("name"))) return Fed.BadRequest("a clan needs a name");
            Dictionary<string, object> clan = clans.Create(caller.AccountId, Properties(request), Custom(request));
            return HttpResponse.Json(clans.Render(clan, request));
        }

        private static HttpResponse Show(ClanStore clans, HttpRequest request)
        {
            Dictionary<string, object> clan = clans.Get(request.RouteValue("id"));
            return clan == null ? NoClan(request) : HttpResponse.Json(clans.Render(clan, request));
        }

        private static HttpResponse Update(ServerContext context, ClanStore clans, Log log, HttpRequest request)
        {
            Caller caller = Fed.TryGetCaller(context, request);
            Dictionary<string, object> clan = clans.Update(request.RouteValue("id"), Properties(request), Custom(request),
                                                           Accounts(context, caller, request.Param("promote_owners")),
                                                           Accounts(context, caller, request.Param("demote_owners")));
            if (clan == null) return NoClan(request);
            log.Info("clan " + request.RouteValue("id") + " updated");
            return HttpResponse.Json(clans.Render(clan, request));
        }

        private static HttpResponse Delete(ServerContext context, ClanStore clans, Log log, HttpRequest request)
        {
            Dictionary<string, object> clan = clans.Get(request.RouteValue("id"));
            if (clan == null) return NoClan(request);
            clans.Delete(request.RouteValue("id"));
            return HttpResponse.EmptyObject();
        }

        private static HttpResponse ToClan(ClanStore clans, HttpRequest request)
        {
            Dictionary<string, object> properties = Properties(request);
            properties["type"] = "clan";
            Dictionary<string, object> clan = clans.Update(request.RouteValue("id"), properties, null, null, null);
            return clan == null ? NoClan(request) : HttpResponse.Json(clans.Render(clan, request));
        }

        private static HttpResponse GetField(ClanStore clans, HttpRequest request)
        {
            Dictionary<string, object> clan = clans.Get(request.RouteValue("id"));
            if (clan == null) return NoClan(request);
            object value = clans.GetField(clan, request.RouteValue("field"));
            // 404 = "never set": the client falls back to its default value (report 02 §4.3 K7).
            return value == null ? Fed.NotFound("field '" + request.RouteValue("field") + "' is not set") : HttpResponse.Json(value);
        }

        private static HttpResponse SetField(ClanStore clans, Log log, HttpRequest request)
        {
            string field = request.RouteValue("field");
            string raw = request.Param("object");
            string operation = (request.Param("operation") ?? "").ToLowerInvariant();
            long? delta = null;
            object value = null;
            if (operation == "inc" || operation == "dec")
            {
                long amount = JsonValue.AsLong(raw) ?? 0;
                delta = operation == "inc" ? amount : -amount;
            }
            else
            {
                value = Fed.ParseFieldValue(raw);
                object parsed;
                if (value is string && MiniJson.TryParse((string)value, out parsed)) value = parsed;
            }
            Dictionary<string, object> clan = clans.SetField(request.RouteValue("id"), field, value, delta);
            if (clan == null) return NoClan(request);
            log.Debug("clan " + request.RouteValue("id") + " field " + field + (delta.HasValue ? " += " + ClanStore.Format(delta.Value) : " = " + MiniJson.Serialize(value)));
            return HttpResponse.Json(clans.Render(clan, request));
        }

        private static HttpResponse Search(ClanStore clans, HttpRequest request, string category, string keywords)
        {
            int offset = Fed.IntParam(request, "offset", 0, 0, Num.IntMax);
            int limit = Fed.IntParam(request, "limit", 100, 1, 1000);
            var result = new List<object>();
            List<Dictionary<string, object>> found = clans.Search(category, keywords);
            for (int i = offset; i < found.Count && result.Count < limit; i++) result.Add(clans.Render(found[i], request));
            return HttpResponse.Json(result);
        }

        private static HttpResponse Members(ClanStore clans, HttpRequest request)
        {
            Dictionary<string, object> clan = clans.Get(request.RouteValue("id"));
            if (clan == null) return NoClan(request);
            bool ownersOnly = Fed.BoolParam(request, "owners", false);
            int offset = Fed.IntParam(request, "offset", 0, 0, Num.IntMax);
            int limit = Fed.IntParam(request, "limit", 100, 1, 1000);
            var result = new List<object>();
            int index = 0;
            foreach (object m in ClanStore.Members(clan))
            {
                var member = m as Dictionary<string, object>;
                if (member == null) continue;
                if (ownersOnly && !ClanStore.IsOwner(clan, JsonValue.GetString(member, "account"))) continue;
                if (index++ < offset) continue;
                if (result.Count >= limit) break;
                result.Add(clans.RenderMember(clan, member, request));
            }
            return HttpResponse.Json(result);
        }

        private static HttpResponse Member(ServerContext context, ClanStore clans, HttpRequest request)
        {
            Dictionary<string, object> clan = clans.Get(request.RouteValue("id"));
            if (clan == null) return NoClan(request);
            string accountId = Fed.ResolveAccountId(context, Fed.TryGetCaller(context, request), request.RouteValue("credential"));
            Dictionary<string, object> member = accountId == null ? null : ClanStore.FindMember(clan, accountId);
            return member == null ? Fed.NotFound("not a member") : HttpResponse.Json(clans.RenderMember(clan, member, request));
        }

        private static HttpResponse AddMember(ServerContext context, ClanStore clans, Log log, HttpRequest request)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;
            string credential = request.Param("credential") ?? "me";
            string accountId = Fed.ResolveAccountId(context, caller, credential);
            if (accountId == null) return Fed.NotFound("unknown credential '" + credential + "'");
            Dictionary<string, object> clan = clans.Get(request.RouteValue("id"));
            if (clan == null) return NoClan(request);

            if (accountId != caller.AccountId)
            {
                // An invitation: there is nobody to receive it offline. The client reads {"group": clan}.
                log.Info("clan invitation of " + credential + " to " + request.RouteValue("id") + " accepted without effect");
                var invite = Fed.Obj();
                invite["group"] = clans.Render(clan, request);
                return HttpResponse.Json(invite);
            }
            long limit = JsonValue.GetLong(clan, "member_limit", 0);
            if (!ClanStore.IsMember(clan, accountId) && limit > 0 && ClanStore.Members(clan).Count >= limit)
                return HttpResponse.Error(HttpStatus.Conflict, "the clan is full");
            // Joining is immediate (200): with nobody to approve, a 202 "waiting for approval" would never resolve.
            clan = clans.AddMember(request.RouteValue("id"), accountId);
            return clan == null ? NoClan(request) : HttpResponse.Json(clans.Render(clan, request));
        }

        private static HttpResponse RemoveMember(ServerContext context, ClanStore clans, Log log, HttpRequest request)
        {
            Caller caller = Fed.TryGetCaller(context, request);
            string accountId = Fed.ResolveAccountId(context, caller, request.RouteValue("credential"));
            if (accountId == null) return Fed.NotFound("unknown credential '" + request.RouteValue("credential") + "'");
            Dictionary<string, object> clan = clans.RemoveMember(request.RouteValue("id"), accountId);
            return clan == null ? NoClan(request) : HttpResponse.Json(clans.Render(clan, request));
        }

        private static HttpResponse MemberFields(ServerContext context, ClanStore clans, Log log, HttpRequest request)
        {
            Caller caller = Fed.TryGetCaller(context, request);
            string accountId = Fed.ResolveAccountId(context, caller, request.RouteValue("credential"));
            if (accountId == null) return Fed.NotFound("unknown credential");
            Dictionary<string, object> fields = Fed.CustomFields(request, ControlParameters, true);
            if (!clans.SetMemberFields(request.RouteValue("id"), accountId, fields)) return Fed.NotFound("not a member");
            return HttpResponse.EmptyObject();
        }

        /// <summary>The plain clan properties of a create/update/to_clan request.</summary>
        private static Dictionary<string, object> Properties(HttpRequest request)
        {
            var properties = Fed.Obj();
            ParamCollection source = request.Form.Count > 0 ? request.Form : request.Query;
            foreach (var kv in source)
            {
                if (!ClanStore.IsProperty(kv.Key) || properties.ContainsKey(kv.Key)) continue;
                object value = Fed.ParseFieldValue(kv.Value);
                if (kv.Key == "inventory" && value is string)
                {
                    object parsed;
                    if (MiniJson.TryParse((string)value, out parsed)) value = parsed;
                }
                properties[kv.Key] = value;
            }
            return properties;
        }

        /// <summary>The "_"-prefixed custom fields ("_name=_json_:&lt;json&gt;", JsonInterpretedConverter).</summary>
        private static Dictionary<string, object> Custom(HttpRequest request)
        {
            return Fed.CustomFields(request, ControlParameters, true);
        }

        /// <summary>Account ids from a JSON array of credentials (promote_owners / demote_owners).</summary>
        private static List<string> Accounts(ServerContext context, Caller caller, string json)
        {
            var result = new List<string>();
            object parsed;
            if (string.IsNullOrEmpty(json) || !MiniJson.TryParse(json, out parsed) || !(parsed is List<object>)) return result;
            foreach (object item in (List<object>)parsed)
            {
                string accountId = Fed.ResolveAccountId(context, caller, JsonValue.AsString(item));
                if (accountId != null) result.Add(accountId);
            }
            return result;
        }

        private static HttpResponse NoClan(HttpRequest request)
        {
            return Fed.NotFound("no clan '" + request.RouteValue("id") + "'");
        }
    }
}
