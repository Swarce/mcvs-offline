using System;
using System.Collections.Generic;
using System.Threading;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Misc;

namespace McvsOffline.Server.Services.Chat
{
    /// <summary>
    /// In-memory group chat (clan chat): rooms and channels with their members, a short history and the open listen
    /// streams. Nothing here needs to survive a restart (the real service did not keep history either; the client
    /// merges what it already had, <c>OP/ChatRoom.cs MergeRoomsFromDisconnection</c>).
    /// </summary>
    public sealed class ChatHub
    {
        private static readonly object ForGate = new object();

        private readonly object gate = new object();
        private readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>(StringComparer.Ordinal);

        public ChatHub(int historySize)
        {
            HistorySize = Num.Max(0, historySize);
        }

        public int HistorySize { get; private set; }

        public static ChatHub For(ServerContext context)
        {
            lock (ForGate)
            {
                ChatHub hub = context.Features.Get<ChatHub>();
                if (hub == null)
                {
                    hub = new ChatHub((int)Fed.LongSetting(context, "chat.historySize", 50, 0, 1000));
                    context.Features.Set(hub);
                }
                return hub;
            }
        }

        /// <summary>"channels/&lt;name&gt;" or "rooms/&lt;name&gt;".</summary>
        public static string RoomKey(string kind, string name)
        {
            return kind + "/" + name;
        }

        /// <summary>Registers <paramref name="accountId"/> as a member and returns its state ("" / "kick" / "mute").</summary>
        public string Join(string roomKey, string accountId, string nickname)
        {
            lock (gate)
            {
                Room room = GetOrCreate(roomKey);
                Member member;
                if (!room.Members.TryGetValue(accountId, out member))
                {
                    member = new Member();
                    room.Members[accountId] = member;
                }
                if (!string.IsNullOrEmpty(nickname)) member.Nickname = nickname;
                return member.Status(DateTime.UtcNow);
            }
        }

        public int MemberCount(string roomKey)
        {
            lock (gate) return GetOrCreate(roomKey).Members.Count;
        }

        /// <summary>Members as {"credential","nickname","status"} objects.</summary>
        public List<object> Members(string roomKey, int offset, int limit)
        {
            var list = new List<object>();
            lock (gate)
            {
                int index = 0;
                foreach (var kv in GetOrCreate(roomKey).Members)
                {
                    if (index++ < offset) continue;
                    if (list.Count >= limit) break;
                    var o = Fed.Obj();
                    o["credential"] = Fed.FedIdOf(kv.Key);
                    o["nickname"] = kv.Value.Nickname ?? "";
                    o["status"] = kv.Value.Status(DateTime.UtcNow);
                    list.Add(o);
                }
            }
            return list;
        }

        /// <summary>Kick/mute (with an optional duration) or lift them. False when the member is unknown.</summary>
        public bool Moderate(string roomKey, string accountId, string action, int seconds)
        {
            lock (gate)
            {
                Member member;
                if (!GetOrCreate(roomKey).Members.TryGetValue(accountId, out member)) return false;
                DateTime until = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
                switch (action)
                {
                    case "kick": member.KickedUntil = until; break;
                    case "unkick": member.KickedUntil = DateTime.MinValue; break;
                    case "mute": member.MutedUntil = until; break;
                    case "unmute": member.MutedUntil = DateTime.MinValue; break;
                    default: return false;
                }
                // A kicked member's streams end with a disconnect line.
                if (action == "kick")
                    foreach (Listener l in GetOrCreate(roomKey).Listeners)
                        if (l.AccountId == accountId) l.Kicked = true;
                Monitor.PulseAll(gate);
                return true;
            }
        }

        public string StatusOf(string roomKey, string accountId)
        {
            lock (gate)
            {
                Member member;
                return GetOrCreate(roomKey).Members.TryGetValue(accountId, out member) ? member.Status(DateTime.UtcNow) : "";
            }
        }

        /// <summary>
        /// Delivers a message line to the room's listeners (all of them, or only <paramref name="onlyAccounts"/> for a
        /// private message) and keeps public messages in the history. Returns how many streams got it.
        /// </summary>
        public int Publish(string roomKey, Dictionary<string, object> message, ICollection<string> onlyAccounts)
        {
            string line = MiniJson.Serialize(message);
            lock (gate)
            {
                Room room = GetOrCreate(roomKey);
                if (onlyAccounts == null && HistorySize > 0)
                {
                    room.History.Add(line);
                    if (room.History.Count > HistorySize) room.History.RemoveRange(0, room.History.Count - HistorySize);
                }
                int delivered = 0;
                foreach (Listener l in room.Listeners)
                {
                    if (onlyAccounts != null && !onlyAccounts.Contains(l.AccountId)) continue;
                    l.Queue.Add(line);
                    delivered++;
                }
                if (delivered > 0) Monitor.PulseAll(gate);
                return delivered;
            }
        }

        /// <summary>Opens a listen stream; the history is queued first. Dispose it when the stream ends.</summary>
        public Listener Listen(string roomKey, string accountId)
        {
            lock (gate)
            {
                Room room = GetOrCreate(roomKey);
                var listener = new Listener(this, room, accountId);
                listener.Queue.AddRange(room.History);
                room.Listeners.Add(listener);
                return listener;
            }
        }

        private List<string> Take(Listener listener, int timeoutMs, Func<bool> stopRequested)
        {
            int deadline = Environment.TickCount + Num.Max(0, timeoutMs);
            lock (gate)
            {
                while (listener.Queue.Count == 0 && !listener.Kicked)
                {
                    int left = deadline - Environment.TickCount;
                    if (left <= 0 || (stopRequested != null && stopRequested())) break;
                    Monitor.Wait(gate, Num.Min(left, 500));
                }
                var lines = new List<string>(listener.Queue);
                listener.Queue.Clear();
                return lines;
            }
        }

        private void Remove(Listener listener)
        {
            lock (gate) listener.Room.Listeners.Remove(listener);
        }

        private Room GetOrCreate(string roomKey)
        {
            Room room;
            if (!rooms.TryGetValue(roomKey, out room))
            {
                room = new Room();
                rooms[roomKey] = room;
            }
            return room;
        }

        /// <summary>One open listen stream.</summary>
        public sealed class Listener : IDisposable
        {
            private readonly ChatHub hub;

            internal Listener(ChatHub hub, Room room, string accountId)
            {
                this.hub = hub;
                Room = room;
                AccountId = accountId;
                Queue = new List<string>();
            }

            internal Room Room { get; private set; }

            public string AccountId { get; private set; }

            internal List<string> Queue { get; private set; }

            /// <summary>Set when the member was kicked: the stream should end with a disconnect line.</summary>
            public bool Kicked { get; internal set; }

            /// <summary>Waits up to <paramref name="timeoutMs"/> for lines to send (empty on timeout).</summary>
            public List<string> Take(int timeoutMs, Func<bool> stopRequested)
            {
                return hub.Take(this, timeoutMs, stopRequested);
            }

            public void Dispose()
            {
                hub.Remove(this);
            }
        }

        internal sealed class Room
        {
            public readonly Dictionary<string, Member> Members = new Dictionary<string, Member>(StringComparer.Ordinal);
            public readonly List<string> History = new List<string>();
            public readonly List<Listener> Listeners = new List<Listener>();
        }

        internal sealed class Member
        {
            public string Nickname;
            public DateTime KickedUntil = DateTime.MinValue;
            public DateTime MutedUntil = DateTime.MinValue;

            public string Status(DateTime now)
            {
                if (KickedUntil > now) return "kick";
                if (MutedUntil > now) return "mute";
                return "";
            }
        }
    }
}
