// Module: Chat - owner group: MISC
// Group chat (clan chat): subscribe, listen stream, commands.
// Protocol: report 02 §4.1; docs/protocol/misc.md
//   POST chat/channels/{channel}/subscribe, chat/rooms/{room}/subscribe -> {cmd_url (ends with '/'), listen_url, status}
//   GET  {listen_url} = chat/{kind}/{name}/listen    stream, one JSON object per line
//   POST {cmd_url} = chat/{kind}/{name}/             msg, user, message_type, _custom
//   POST {cmd_url}members/{cred} (private), {cmd_url}invite, {cmd_url}members/{cred}/{kick|mute|unkick|unmute}
//   GET  {cmd_url}members; GET chat/rooms, chat/channels
// Conventions and core APIs: docs/MODULES.md.

using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Misc;
using McvsOffline.Server.Services.Social;

namespace McvsOffline.Server.Services.Chat
{
    /// <summary>
    /// Group chat backed by <see cref="ChatHub"/>. The listen stream starts with a room_info line and the recent
    /// history, then carries messages as they are posted; blank keep-alive lines in between are ignored by the
    /// client (<c>OP/ChatRoom.cs Read</c>). The end of a stream counts as a lost connection on the client, which
    /// then waits up to 120 s before subscribing again (<c>EN/ClientChatComponent.cs</c>), so streams are held open
    /// (by default until the client goes away) and end with a {"type":"disconnect","reason":"Idle channel"} line.
    /// </summary>
    public static class ChatService
    {
        public const string StreamContentType = "application/json; charset=utf-8";

        private static readonly string[] Kinds = { "channels", "rooms" };

        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("chat");
            ChatHub hub = ChatHub.For(context);

            foreach (string kind in Kinds)
            {
                string k = kind;
                router.Get("chat/" + k, "chat." + k + ".list", r => HttpResponse.EmptyArray());
                router.Post("chat/" + k + "/{name}/subscribe", "chat." + k + ".subscribe", r => Subscribe(context, hub, log, r, k));
                router.Get("chat/" + k + "/{name}/listen", "chat." + k + ".listen", r => Listen(context, hub, log, r, k));
                router.Post("chat/" + k + "/{name}", "chat." + k + ".send", r => Send(context, hub, log, r, k, null));
                router.Post("chat/" + k + "/{name}/invite", "chat." + k + ".invite", r => Invite(log, r));
                router.Get("chat/" + k + "/{name}/members", "chat." + k + ".members", r => Members(hub, r, k));
                router.Post("chat/" + k + "/{name}/members/{credential}", "chat." + k + ".private", r => Send(context, hub, log, r, k, r.RouteValue("credential")));
                router.Post("chat/" + k + "/{name}/members/{credential}/{action}", "chat." + k + ".moderate", r => Moderate(context, hub, log, r, k));
            }
        }

        private static HttpResponse Subscribe(ServerContext context, ChatHub hub, Log log, HttpRequest request, string kind)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;
            string name = request.RouteValue("name");
            string roomKey = ChatHub.RoomKey(kind, name);
            string status = hub.Join(roomKey, caller.AccountId, SocialProfiles.For(context).NameOf(caller.AccountId));
            log.Info(caller.FedId + " subscribed to chat " + roomKey + (status.Length > 0 ? " (" + status + ")" : ""));
            Dictionary<string, object> body = RoomInfo(context, hub, request, kind, name);
            JsonPath.RemoveKey(body, "type");
            body["status"] = status;
            return HttpResponse.Json(body);
        }

        private static HttpResponse Listen(ServerContext context, ChatHub hub, Log log, HttpRequest request, string kind)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;
            string name = request.RouteValue("name");
            string roomKey = ChatHub.RoomKey(kind, name);
            hub.Join(roomKey, caller.AccountId, null);
            string roomInfo = MiniJson.Serialize(RoomInfo(context, hub, request, kind, name));
            int keepAliveSeconds = (int)Fed.LongSetting(context, "chat.keepAliveSeconds", 20, 1, 300);
            long streamSeconds = Fed.LongSetting(context, "chat.streamSeconds", 0, 0, 7L * 24 * 3600); // 0 = no limit

            return HttpResponse.Stream(stream =>
            {
                using (ChatHub.Listener listener = hub.Listen(roomKey, caller.AccountId))
                {
                    stream.Write(roomInfo + "\n");
                    int end = Environment.TickCount + (int)Num.Min(streamSeconds, Num.IntMax / 1000) * 1000;
                    while (!stream.IsStopping)
                    {
                        int wait = keepAliveSeconds * 1000;
                        if (streamSeconds > 0)
                        {
                            int left = end - Environment.TickCount;
                            if (left <= 0) break;
                            wait = Num.Min(wait, left);
                        }
                        List<string> lines = listener.Take(wait, () => stream.IsStopping);
                        if (listener.Kicked)
                        {
                            stream.Write("{\"type\":\"disconnect\",\"reason\":\"Kicked\"}\n");
                            return;
                        }
                        if (lines.Count == 0)
                        {
                            stream.Write("\n");
                            continue;
                        }
                        foreach (string line in lines) stream.Write(line + "\n");
                    }
                    // "Idle channel"/"Idle room": the client treats the end as an idle room, not as a replaced connection.
                    stream.Write("{\"type\":\"disconnect\",\"reason\":\"" + (kind == "rooms" ? "Idle room" : "Idle channel") + "\"}\n");
                }
            }, StreamContentType);
        }

        private static HttpResponse Send(ServerContext context, ChatHub hub, Log log, HttpRequest request, string kind, string recipient)
        {
            Caller caller;
            HttpResponse failure;
            if (!Fed.RequireCaller(context, request, log, out caller, out failure)) return failure;
            string roomKey = ChatHub.RoomKey(kind, request.RouteValue("name"));
            string status = hub.StatusOf(roomKey, caller.AccountId);
            if (status == "kick" || status == "mute")
            {
                log.Info("chat message from " + caller.FedId + " in " + roomKey + " dropped (" + status + ")");
                return HttpResponse.EmptyObject();
            }

            string text = request.Param("msg") ?? "";
            if (text.Length > 256) text = text.Substring(0, 256);
            var message = Fed.Obj();
            message["type"] = string.Equals(request.Param("message_type"), "game", StringComparison.Ordinal) ? "game" : "message";
            message["id"] = Fed.NewId();
            message["msg"] = text;
            message["sender"] = Sender(context, caller, request.Param("user"));
            message["sent"] = TimeFormat.Iso8601Millis(context.Clock.UtcNow);
            message["moderator"] = false;
            foreach (var kv in Fed.CustomFields(request, null, true))
                if (!message.ContainsKey(kv.Key)) message[kv.Key] = kv.Value;

            List<string> only = null;
            if (recipient != null)
            {
                string accountId = Fed.ResolveAccountId(context, caller, recipient);
                if (accountId == null) return Fed.NotFound("unknown recipient '" + recipient + "'");
                only = new List<string> { accountId, caller.AccountId };
            }
            int delivered = hub.Publish(roomKey, message, only);
            log.Debug("chat message " + message["id"] + " in " + roomKey + " to " + delivered + " stream(s)");
            return HttpResponse.EmptyObject();
        }

        private static HttpResponse Moderate(ServerContext context, ChatHub hub, Log log, HttpRequest request, string kind)
        {
            string action = (request.RouteValue("action") ?? "").ToLowerInvariant();
            string roomKey = ChatHub.RoomKey(kind, request.RouteValue("name"));
            string accountId = Fed.ResolveAccountId(context, Fed.TryGetCaller(context, request), request.RouteValue("credential"));
            if (accountId == null) return Fed.NotFound("unknown member");
            int seconds = Fed.IntParam(request, "limit", 0, 0, Num.IntMax);
            if (!hub.Moderate(roomKey, accountId, action, seconds)) return Fed.NotFound("unknown member or action");
            log.Info("chat " + roomKey + ": " + action + " " + Fed.FedIdOf(accountId) + (seconds > 0 ? " for " + seconds + " s" : ""));
            return HttpResponse.EmptyObject();
        }

        private static HttpResponse Invite(Log log, HttpRequest request)
        {
            log.Info("chat invitation of " + (request.Param("recipient") ?? "?") + " accepted without effect (no other players offline)");
            return HttpResponse.EmptyObject();
        }

        private static HttpResponse Members(ChatHub hub, HttpRequest request, string kind)
        {
            int offset = Fed.IntParam(request, "offset", 0, 0, Num.IntMax);
            int limit = Fed.IntParam(request, "limit", 100, 1, 1000);
            return HttpResponse.Json(hub.Members(ChatHub.RoomKey(kind, request.RouteValue("name")), offset, limit));
        }

        /// <summary>
        /// The room_info object (also the subscribe answer). cmd_url must end with '/': the client appends
        /// "members/..." and "invite" to it (report 02 §4.1). The URLs follow the request's host (LAN mode).
        /// </summary>
        private static Dictionary<string, object> RoomInfo(ServerContext context, ChatHub hub, HttpRequest request, string kind, string name)
        {
            string cmdUrl = context.BaseUrlFor(request) + "chat/" + kind + "/" + UrlEncoding.Encode(name) + "/";
            var info = Fed.Obj();
            info["type"] = "room_info";
            info["cmd_url"] = cmdUrl;
            info["listen_url"] = cmdUrl + "listen";
            info["num_members"] = (long)hub.MemberCount(ChatHub.RoomKey(kind, name));
            info["motd"] = "";
            info["send_quota"] = 10L;
            info["quota_period"] = 60L;
            info["reconnect_key"] = Fed.NewId();
            return info;
        }

        /// <summary>The sender as a ChatUser: the client's "user" JSON (nickname, avatar, _fields) with our credential.</summary>
        private static Dictionary<string, object> Sender(ServerContext context, Caller caller, string userJson)
        {
            var sender = Fed.Obj();
            object parsed;
            if (!string.IsNullOrEmpty(userJson) && MiniJson.TryParse(userJson, out parsed) && parsed is Dictionary<string, object>)
                foreach (var kv in (Dictionary<string, object>)parsed) sender[kv.Key] = kv.Value;
            sender["credential"] = caller.FedId;
            if (string.IsNullOrEmpty(JsonValue.GetString(sender, "nickname")))
                sender["nickname"] = SocialProfiles.For(context).NameOf(caller.AccountId);
            return sender;
        }
    }
}
