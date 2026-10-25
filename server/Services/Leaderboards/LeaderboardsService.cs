// Module: Leaderboards - owner group: MISC
// Leaderboards (Olympus), incl. the division/season ranking.
// Protocol: report 02 §4.9, report 04 §9.2; docs/protocol/misc.md
//   POST   leaderboards/{sort}/{name}/me                score, replace_score_if?, display_name, expiration_*?, custom fields
//   POST   leaderboards/{sort}/{name}/{entry}           credential, score, ...   (arbitrary entry)
//   POST   leaderboards/{sort}/{name}/me/delete
//   GET    leaderboards/{sort}/{name}?offset&limit[&for_entry_id]   (limit=0: the caller's own entry only)
//   GET    leaderboards/{sort}/{name}/me/location?offset&limit[&around_me=True]
//   GET    leaderboards/{sort}/{name}/me/friends?offset&limit
//   GET    leaderboards/{sort}/{name}/{credential|me}?offset&limit     (around that entry)
//   DELETE leaderboards/{sort}/{name}/{credential}
//   GET|POST leaderboards/{sort}/{name}/settings      (league container settings)
// Conventions and core APIs: docs/MODULES.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Misc;

namespace McvsOffline.Server.Services.Leaderboards
{
    /// <summary>
    /// Leaderboards (Olympus). Every read answers a LeaderboardData object (<c>OP/Leaderboard.cs</c>):
    /// {"data": [entries], "total_entries": n, "my_entry": {...} or {}}. An empty my_entry (no score yet) makes
    /// the client post the player's score and ask again (<c>GM/LeaderboardsComponent.cs PostUserIfNotFound</c>).
    /// </summary>
    public static class LeaderboardsService
    {
        private const int DefaultLimit = 100;
        private const int MaxLimit = 1000;

        private static readonly HashSet<string> ControlParameters = new HashSet<string>(StringComparer.Ordinal)
        {
            "access_token", "score", "replace_score_if", "display_name", "expiration_date", "expiration_duration", "credential",
        };

        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("leaderboards");
            var store = new LeaderboardStore(context);

            // Literal segments ("me", "settings") are registered before the {entry}/{credential} routes of the same shape.
            router.Post("leaderboards/{sort}/{name}/me/delete", "leaderboards.me.delete", r => DeleteMine(context, store, log, r));
            router.Post("leaderboards/{sort}/{name}/settings", "leaderboards.settings.set", r => SetSettings(store, r));
            router.Post("leaderboards/{sort}/{name}/me", "leaderboards.me.post", r => PostScore(context, store, log, r, null));
            router.Post("leaderboards/{sort}/{name}/{entry}", "leaderboards.entry.post", r => PostScore(context, store, log, r, r.RouteValue("entry")));

            router.Get("leaderboards/{sort}/{name}", "leaderboards.top", r => Top(context, store, log, r));
            router.Get("leaderboards/{sort}/{name}/settings", "leaderboards.settings.get", r => GetSettings(store, r));
            router.Get("leaderboards/{sort}/{name}/me/location", "leaderboards.location", r => Location(context, store, log, r));
            router.Get("leaderboards/{sort}/{name}/me/friends", "leaderboards.friends", r => Friends(context, store, log, r));
            router.Get("leaderboards/{sort}/{name}/{credential}", "leaderboards.around", r => Around(context, store, log, r));
            router.Delete("leaderboards/{sort}/{name}/{credential}", "leaderboards.entry.delete", r => DeleteEntry(context, store, log, r));
        }

        private static HttpResponse PostScore(ServerContext context, LeaderboardStore store, Log log, HttpRequest request, string entry)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;

            long score;
            if (!long.TryParse((request.Param("score") ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out score))
                return Fed.BadRequest("score must be an integer");

            var post = new ScorePost();
            post.Score = score;
            post.ReplaceIf = request.Param("replace_score_if");
            post.DisplayName = request.Param("display_name");
            post.Fields = Fed.CustomFields(request, ControlParameters, false);
            post.ExpiresUtc = Expiry(context, request);
            if (entry == null)
            {
                post.Credential = caller.FedId;
            }
            else
            {
                // An arbitrary entry: a player's credential (normalized to its fed id) or any name the caller chose.
                string named = request.Param("credential") ?? entry;
                string accountId = named.IndexOf(':') >= 0 ? Fed.ResolveAccountId(context, caller, named) : null;
                post.Credential = accountId != null ? Fed.FedIdOf(accountId) : named;
            }
            store.Post(request.RouteValue("sort"), request.RouteValue("name"), post);
            return HttpResponse.EmptyObject();
        }

        private static HttpResponse Top(ServerContext context, LeaderboardStore store, Log log, HttpRequest request)
        {
            Caller caller = Fed.TryGetCaller(context, request);
            List<RankedEntry> ranking = store.Ranking(request.RouteValue("sort"), request.RouteValue("name"));
            string forEntry = request.Param("for_entry_id");
            RankedEntry mine = Find(ranking, forEntry != null ? Normalize(context, caller, forEntry) : (caller == null ? null : caller.FedId));
            int offset = Fed.IntParam(request, "offset", 0, 0, Num.IntMax);
            int limit = Fed.IntParam(request, "limit", DefaultLimit, 0, MaxLimit);
            List<RankedEntry> slice = limit == 0
                ? (mine == null ? new List<RankedEntry>() : new List<RankedEntry> { mine }) // the "self" query (SelfData/SelfEntry)
                : Slice(ranking, offset, limit);
            return Data(slice, mine, ranking.Count);
        }

        private static HttpResponse Location(ServerContext context, LeaderboardStore store, Log log, HttpRequest request)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;
            // Offline every player is "local": the country board is the global board.
            List<RankedEntry> ranking = store.Ranking(request.RouteValue("sort"), request.RouteValue("name"));
            RankedEntry mine = Find(ranking, caller.FedId);
            int offset = Fed.IntParam(request, "offset", 0, 0, Num.IntMax);
            int limit = Fed.IntParam(request, "limit", DefaultLimit, 1, MaxLimit);
            bool aroundMe = Fed.BoolParam(request, "around_me", false);
            return Data(aroundMe ? Around(ranking, mine, offset, limit) : Slice(ranking, offset, limit), mine, ranking.Count);
        }

        private static HttpResponse Friends(ServerContext context, LeaderboardStore store, Log log, HttpRequest request)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;
            // No friends offline: the board of the player and their friends is the player alone.
            List<RankedEntry> ranking = store.Ranking(request.RouteValue("sort"), request.RouteValue("name"));
            RankedEntry mine = Find(ranking, caller.FedId);
            var friends = new List<RankedEntry>();
            if (mine != null)
            {
                friends.Add(new RankedEntry
                {
                    Credential = mine.Credential, DisplayName = mine.DisplayName, Score = mine.Score, Rank = 1, Fields = mine.Fields,
                });
            }
            return Data(friends, friends.Count > 0 ? friends[0] : null, friends.Count);
        }

        private static HttpResponse Around(ServerContext context, LeaderboardStore store, Log log, HttpRequest request)
        {
            Caller caller = Fed.TryGetCaller(context, request);
            string credential = request.RouteValue("credential");
            if (caller == null && string.Equals(credential, "me", StringComparison.OrdinalIgnoreCase))
            {
                HttpResponse failure;
                Fed.RequireCaller(context, request, log, out caller, out failure);
                return failure;
            }
            List<RankedEntry> ranking = store.Ranking(request.RouteValue("sort"), request.RouteValue("name"));
            RankedEntry target = Find(ranking, Normalize(context, caller, credential));
            int offset = Fed.IntParam(request, "offset", 0, 0, Num.IntMax);
            int limit = Fed.IntParam(request, "limit", DefaultLimit, 1, MaxLimit);
            return Data(Around(ranking, target, offset, limit), target, ranking.Count);
        }

        private static HttpResponse DeleteMine(ServerContext context, LeaderboardStore store, Log log, HttpRequest request)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;
            bool removed = store.Delete(request.RouteValue("sort"), request.RouteValue("name"), caller.FedId);
            log.Info("entry of " + caller.FedId + " on " + request.RouteValue("name") + (removed ? " deleted" : " did not exist"));
            return HttpResponse.EmptyObject();
        }

        private static HttpResponse DeleteEntry(ServerContext context, LeaderboardStore store, Log log, HttpRequest request)
        {
            Caller caller = Fed.TryGetCaller(context, request);
            string credential = Normalize(context, caller, request.RouteValue("credential"));
            bool removed = credential != null && store.Delete(request.RouteValue("sort"), request.RouteValue("name"), credential);
            log.Info("entry " + credential + " on " + request.RouteValue("name") + (removed ? " deleted" : " did not exist"));
            return HttpResponse.EmptyObject();
        }

        private static HttpResponse SetSettings(LeaderboardStore store, HttpRequest request)
        {
            var settings = Fed.Obj();
            settings["container_type"] = request.Param("type") ?? "league";
            settings["group_size"] = (long)Fed.IntParam(request, "group_size", 100, 1, 1000);
            string expiration = request.Param("expiration_duration");
            if (!string.IsNullOrEmpty(expiration)) settings["expiration_duration"] = (long)Fed.IntParam(request, "expiration_duration", 0, 0, Num.IntMax);
            store.SetSettings(request.RouteValue("sort"), request.RouteValue("name"), settings);
            return HttpResponse.EmptyObject();
        }

        private static HttpResponse GetSettings(LeaderboardStore store, HttpRequest request)
        {
            Dictionary<string, object> settings = store.GetSettings(request.RouteValue("sort"), request.RouteValue("name"));
            if (settings == null)
            {
                settings = Fed.Obj();
                settings["container_type"] = "league";
                settings["group_size"] = 100L;
            }
            return HttpResponse.Json(settings);
        }

        /// <summary>{"data": [...], "total_entries": n, "my_entry": {...}} (my_entry {} when there is none).</summary>
        private static HttpResponse Data(List<RankedEntry> entries, RankedEntry mine, int total)
        {
            var data = new List<object>(entries.Count);
            foreach (RankedEntry e in entries) data.Add(e.ToJson());
            var body = Fed.Obj();
            body["data"] = data;
            body["total_entries"] = (long)total;
            body["my_entry"] = mine == null ? Fed.Obj() : mine.ToJson();
            return HttpResponse.Json(body);
        }

        private static List<RankedEntry> Slice(List<RankedEntry> ranking, int offset, int limit)
        {
            var slice = new List<RankedEntry>();
            for (int i = offset; i < ranking.Count && slice.Count < limit; i++) slice.Add(ranking[i]);
            return slice;
        }

        /// <summary>A window of <paramref name="limit"/> entries with <paramref name="target"/> in the middle (the top when absent).</summary>
        private static List<RankedEntry> Around(List<RankedEntry> ranking, RankedEntry target, int offset, int limit)
        {
            if (target == null) return Slice(ranking, offset, limit);
            int index = target.Rank - 1;
            int start = index - (limit - 1) / 2 + offset;
            start = (int)Num.Clamp(start, 0, Num.Max(0, ranking.Count - limit));
            return Slice(ranking, start, limit);
        }

        private static RankedEntry Find(List<RankedEntry> ranking, string credential)
        {
            if (string.IsNullOrEmpty(credential)) return null;
            foreach (RankedEntry e in ranking)
                if (string.Equals(e.Credential, credential, StringComparison.Ordinal)) return e;
            return null;
        }

        /// <summary>A credential as entries are keyed: players by fed id, other names unchanged.</summary>
        private static string Normalize(ServerContext context, Caller caller, string credential)
        {
            if (string.IsNullOrEmpty(credential)) return null;
            if (string.Equals(credential, "me", StringComparison.OrdinalIgnoreCase)) return caller == null ? null : caller.FedId;
            if (credential.IndexOf(':') < 0) return credential;
            string accountId = Fed.ResolveAccountId(context, caller, credential);
            return accountId != null ? Fed.FedIdOf(accountId) : credential;
        }

        private static DateTime? Expiry(ServerContext context, HttpRequest request)
        {
            DateTime at;
            string date = request.Param("expiration_date");
            if (!string.IsNullOrEmpty(date) && TimeFormat.TryParse(date, out at)) return at;
            int seconds = Fed.IntParam(request, "expiration_duration", 0, 0, Num.IntMax);
            if (seconds > 0) return context.Clock.UtcNow.AddSeconds(seconds);
            return null;
        }
    }
}
