// Module: Wall - owner group: MISC
// Walls of groups/accounts/events/game_objects. Clans use theirs as a notification feed: join/leave/kick notices
// are game posts whose text is {"Type":<ClanNotifictionType>,"Receiver":{member},"TriggeredBy":{member}}
// (OP/Clan.cs SendNotification / GetLastAlerts reads the last 3).
// Protocol: report 02 §4.6; docs/protocol/misc.md
//   POST /{groups|accounts|events|game_objects}/{id}/wall   text, language, activity_type, alert_kairos
//   GET  /{groups|accounts|events|game_objects}/{id}/wall?sort_type&language&limit    (array, newest first)
// Conventions and core APIs: docs/MODULES.md.

using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Alerts;
using McvsOffline.Server.Services.Misc;
using McvsOffline.Server.Services.Social;

namespace McvsOffline.Server.Services.Wall
{
    /// <summary>
    /// Walls, one document per wall in &lt;data&gt;/walls/ (the newest posts are kept). A post with alert_kairos
    /// sends a {"type":"wall_post"} alert to the wall's owner: "group:&lt;id&gt;" for a clan (clan members listen to that
    /// target, <c>EN/ClanComponent.cs</c>), the account for an account wall.
    /// </summary>
    public static class WallService
    {
        public const string StoreName = "walls";

        private static readonly string[] WallTypes = { "groups", "accounts", "events", "game_objects" };

        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("wall");
            DocumentStore store = context.OpenStore(StoreName);
            foreach (string type in WallTypes)
            {
                string wallType = type;
                router.Post(wallType + "/{id}/wall", "wall." + wallType + ".post", r => Post(context, store, log, r, wallType));
                router.Get(wallType + "/{id}/wall", "wall." + wallType + ".view", r => View(context, store, r, wallType));
            }
        }

        private static HttpResponse Post(ServerContext context, DocumentStore store, Log log, HttpRequest request, string wallType)
        {
            Caller caller = Fed.TryGetCaller(context, request);
            string id = request.RouteValue("id");
            string ownerId = WallOwnerAccount(context, caller, wallType, id);

            var post = Fed.Obj();
            post["id"] = Fed.NewId();
            post["type"] = string.IsNullOrEmpty(request.Param("activity_type")) ? "user_post" : request.Param("activity_type");
            post["text"] = request.Param("text") ?? "";
            post["actor"] = caller == null ? null : ChatUser(context, caller.AccountId);
            post["creation"] = TimeFormat.Iso8601Millis(context.Clock.UtcNow);
            post["target"] = Target(context, wallType, id, ownerId);
            post["game"] = McvsConstants.ClientId;
            post["language"] = request.Param("language") ?? "";
            post["votes"] = 0L;

            int capacity = (int)Fed.LongSetting(context, "wall.maxPostsPerWall", 100, 3, 10000);
            store.Update(Key(wallType, ownerId ?? id), doc =>
            {
                List<object> posts = Posts(doc);
                posts.Add(post);
                while (posts.Count > capacity) posts.RemoveAt(0);
            });
            log.Info("wall post " + post["id"] + " on " + wallType + "/" + id + " by " + (caller == null ? "?" : caller.FedId));

            if (Fed.BoolParam(request, "alert_kairos", true))
            {
                var alert = Fed.Obj();
                alert["type"] = "wall_post";
                string recipient = wallType == "groups" ? "group:" + id : ownerId != null ? Fed.FedIdOf(ownerId) : null;
                if (recipient != null)
                {
                    alert["target"] = recipient;
                    AlertHub.For(context).Push(recipient, alert);
                }
            }
            return HttpResponse.EmptyObject();
        }

        private static HttpResponse View(ServerContext context, DocumentStore store, HttpRequest request, string wallType)
        {
            Caller caller = Fed.TryGetCaller(context, request);
            string id = request.RouteValue("id");
            string ownerId = WallOwnerAccount(context, caller, wallType, id);
            int limit = Fed.IntParam(request, "limit", 50, 1, 1000);
            bool popularity = string.Equals(request.Param("sort_type"), "popularity", StringComparison.OrdinalIgnoreCase);

            var posts = new List<object>(Posts(store.Read(Key(wallType, ownerId ?? id))));
            posts.Reverse(); // newest first; a stable sort by votes keeps that order between equals
            if (popularity)
            {
                var indexed = new List<KeyValuePair<int, object>>();
                for (int i = 0; i < posts.Count; i++) indexed.Add(new KeyValuePair<int, object>(i, posts[i]));
                indexed.Sort((a, b) =>
                {
                    int c = JsonValue.GetLong(b.Value as Dictionary<string, object>, "votes").CompareTo(JsonValue.GetLong(a.Value as Dictionary<string, object>, "votes"));
                    return c != 0 ? c : a.Key.CompareTo(b.Key);
                });
                posts.Clear();
                foreach (var kv in indexed) posts.Add(kv.Value);
            }
            if (posts.Count > limit) posts.RemoveRange(limit, posts.Count - limit);
            return HttpResponse.Json(posts);
        }

        /// <summary>For account walls, the account the id names ("me" included); null for other walls.</summary>
        private static string WallOwnerAccount(ServerContext context, Caller caller, string wallType, string id)
        {
            return wallType == "accounts" ? Fed.ResolveAccountId(context, caller, id) : null;
        }

        /// <summary>A ChatUser for an account: {"credential","nickname","avatar"} (<c>OP/ChatUser.cs</c>).</summary>
        internal static Dictionary<string, object> ChatUser(ServerContext context, string accountId)
        {
            var user = Fed.Obj();
            user["credential"] = Fed.FedIdOf(accountId);
            user["nickname"] = SocialProfiles.For(context).NameOf(accountId);
            user["avatar"] = "";
            return user;
        }

        /// <summary>The wall's owner as a ChatUser, when it has a credential form (a clan: "group:&lt;id&gt;").</summary>
        private static Dictionary<string, object> Target(ServerContext context, string wallType, string id, string ownerId)
        {
            if (wallType == "accounts" && ownerId != null) return ChatUser(context, ownerId);
            if (wallType != "groups") return null;
            var target = Fed.Obj();
            target["credential"] = "group:" + id;
            target["nickname"] = "";
            return target;
        }

        private static List<object> Posts(Dictionary<string, object> doc)
        {
            if (doc == null) return new List<object>();
            List<object> posts = JsonValue.GetArray(doc, "posts");
            if (posts == null)
            {
                posts = new List<object>();
                doc["posts"] = posts;
            }
            return posts;
        }

        private static string Key(string wallType, string id)
        {
            return wallType + "/" + id;
        }
    }
}
