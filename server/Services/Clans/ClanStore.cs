using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Alerts;
using McvsOffline.Server.Services.Misc;
using McvsOffline.Server.Services.Social;

namespace McvsOffline.Server.Services.Clans
{
    /// <summary>
    /// Osiris groups (clans), one document per clan in &lt;data&gt;/clans/.
    /// <para>
    /// Offline a clan is a local, mostly single-member affair, but the game drives it like the real service: it
    /// stores the clan id in the player's profile itself (<c>EN/ClanComponent.cs SetClanInProfile</c>) after
    /// <c>POST groups</c> / joining succeeded, reloads it with <c>GET groups/{id}</c>, keeps scores in custom
    /// fields and posts notifications to the clan wall. Every failure on the game side is caught and shown as a
    /// failed clan operation, so the shapes below only have to parse (<c>OP/Clan.cs</c>, <c>OP/ClanMember.cs</c>).
    /// </para>
    /// Joining is immediate whatever the membership mode (there is nobody to approve a request).
    /// </summary>
    public sealed class ClanStore
    {
        public const string StoreName = "clans";

        /// <summary>Clan properties the create/update/to_clan requests may set, with their JSON type.</summary>
        private static readonly string[] StringProperties = { "name", "description", "category", "type", "visibility", "membership", "avatar", "recommendation_filter" };
        private static readonly string[] NumberProperties = { "member_limit", "score", "currency", "matchmaking_score", "recommendation_score", "days_offline_before_demotion" };

        private static readonly object ForGate = new object();

        private readonly ServerContext context;
        private readonly DocumentStore store;
        private readonly Log log;

        public ClanStore(ServerContext context)
        {
            this.context = context;
            store = context.OpenStore(StoreName);
            log = context.Logger.For("clans");
        }

        public static ClanStore For(ServerContext context)
        {
            lock (ForGate)
            {
                ClanStore clans = context.Features.Get<ClanStore>();
                if (clans == null)
                {
                    clans = new ClanStore(context);
                    context.Features.Set(clans);
                }
                return clans;
            }
        }

        /// <summary>Whether <paramref name="name"/> is one of the plain (non-custom) clan properties.</summary>
        public static bool IsProperty(string name)
        {
            return Array.IndexOf(StringProperties, name) >= 0 || Array.IndexOf(NumberProperties, name) >= 0 || name == "inventory";
        }

        /// <summary>Creates a clan owned by <paramref name="ownerAccountId"/>. Returns the stored document.</summary>
        public Dictionary<string, object> Create(string ownerAccountId, Dictionary<string, object> properties, Dictionary<string, object> custom)
        {
            string id = Fed.NewId();
            var clan = Fed.Obj();
            clan["id"] = id;
            clan["name"] = "";
            clan["description"] = "";
            clan["category"] = "Clan";
            clan["type"] = "clan";
            clan["visibility"] = "member";
            clan["membership"] = "owner_approved";
            clan["member_limit"] = 30L;
            clan["score"] = 0L;
            clan["currency"] = 0L;
            clan["inventory"] = Fed.Obj();
            clan["avatar"] = "";
            clan["matchmaking_score"] = 0L;
            clan["recommendation_score"] = 0L;
            clan["recommendation_filter"] = "";
            clan["days_offline_before_demotion"] = 30L;
            clan["edit_permissions"] = "";
            clan["participations"] = new List<object>();
            clan["created"] = Fed.Now(context);
            clan["custom"] = Fed.Obj();
            clan["owners"] = new List<object> { ownerAccountId };
            clan["members"] = new List<object> { NewMember(ownerAccountId) };
            ApplyProperties(clan, properties, custom);
            store.Write(id, clan);
            log.Info("clan " + id + " '" + JsonValue.GetString(clan, "name") + "' created by " + Fed.FedIdOf(ownerAccountId));
            return clan;
        }

        /// <summary>The stored clan, or null.</summary>
        public Dictionary<string, object> Get(string clanId)
        {
            return IsValidId(clanId) ? store.Read(clanId) : null;
        }

        /// <summary>
        /// Applies <paramref name="mutate"/> to an existing clan and saves it. Returns the clan after the change, or
        /// null when it does not exist (nothing is written then).
        /// </summary>
        public Dictionary<string, object> Change(string clanId, Action<Dictionary<string, object>> mutate)
        {
            if (!IsValidId(clanId) || !store.Exists(clanId)) return null;
            Dictionary<string, object> result = null;
            using (store.Lock(clanId))
            {
                Dictionary<string, object> clan = store.Read(clanId);
                if (clan == null) return null;
                mutate(clan);
                store.Write(clanId, clan);
                result = clan;
            }
            return result;
        }

        public Dictionary<string, object> Update(string clanId, Dictionary<string, object> properties, Dictionary<string, object> custom,
                                                 IEnumerable<string> promoteOwners, IEnumerable<string> demoteOwners)
        {
            return Change(clanId, clan =>
            {
                ApplyProperties(clan, properties, custom);
                List<object> owners = Owners(clan);
                if (promoteOwners != null)
                    foreach (string account in promoteOwners)
                        if (IsMember(clan, account) && !owners.Contains(account)) owners.Add(account);
                if (demoteOwners != null)
                    foreach (string account in demoteOwners)
                        if (owners.Count > 1) owners.Remove(account);
            });
        }

        public bool Delete(string clanId)
        {
            bool deleted = IsValidId(clanId) && store.Delete(clanId);
            if (deleted) log.Info("clan " + clanId + " deleted");
            return deleted;
        }

        /// <summary>Adds a member (no-op when already one). Null when the clan does not exist.</summary>
        public Dictionary<string, object> AddMember(string clanId, string accountId)
        {
            return Change(clanId, clan =>
            {
                if (IsMember(clan, accountId)) return;
                Members(clan).Add(NewMember(accountId));
                log.Info(Fed.FedIdOf(accountId) + " joined clan " + clanId);
            });
        }

        /// <summary>
        /// Removes a member; the next member becomes owner when the last owner leaves, and a clan without members is
        /// deleted. Returns the clan as it is after the change (also when deleted), or null if it did not exist.
        /// </summary>
        public Dictionary<string, object> RemoveMember(string clanId, string accountId)
        {
            bool empty = false;
            Dictionary<string, object> clan = Change(clanId, c =>
            {
                List<object> members = Members(c);
                members.RemoveAll(m => JsonValue.GetString(m as Dictionary<string, object>, "account") == accountId);
                List<object> owners = Owners(c);
                owners.Remove(accountId);
                if (owners.Count == 0 && members.Count > 0)
                    owners.Add(JsonValue.GetString(members[0] as Dictionary<string, object>, "account"));
                empty = members.Count == 0;
            });
            if (clan != null)
            {
                log.Info(Fed.FedIdOf(accountId) + " left clan " + clanId);
                if (empty) Delete(clanId);
            }
            return clan;
        }

        /// <summary>Merges custom fields into a member's record. False when the clan or the member does not exist.</summary>
        public bool SetMemberFields(string clanId, string accountId, Dictionary<string, object> fields)
        {
            bool found = false;
            Change(clanId, clan =>
            {
                Dictionary<string, object> member = FindMember(clan, accountId);
                if (member == null) return;
                found = true;
                Dictionary<string, object> current = JsonValue.GetObject(member, "fields");
                if (current == null)
                {
                    current = Fed.Obj();
                    member["fields"] = current;
                }
                foreach (var kv in fields) current[kv.Key] = JsonValue.DeepClone(kv.Value);
            });
            return found;
        }

        /// <summary>
        /// A clan field (<c>GET groups/{id}/fields/{field}</c>): a clan property or a custom field; null when unset
        /// (the client then uses its default, report 02 §4.3 K7).
        /// </summary>
        public object GetField(Dictionary<string, object> clan, string field)
        {
            if (clan == null || string.IsNullOrEmpty(field)) return null;
            object value;
            if (IsProperty(field) && clan.TryGetValue(field, out value)) return JsonValue.DeepClone(value);
            Dictionary<string, object> custom = JsonValue.GetObject(clan, "custom");
            if (custom != null && custom.TryGetValue(field, out value)) return JsonValue.DeepClone(value);
            if (custom != null && !field.StartsWith("_", StringComparison.Ordinal) && custom.TryGetValue("_" + field, out value)) return JsonValue.DeepClone(value);
            return null;
        }

        /// <summary>Sets a field, or adds <paramref name="delta"/> to it (inc/dec) when delta is not null.</summary>
        public Dictionary<string, object> SetField(string clanId, string field, object value, long? delta)
        {
            return Change(clanId, clan =>
            {
                Dictionary<string, object> target = IsProperty(field) ? clan : Custom(clan);
                if (delta.HasValue)
                {
                    object current;
                    long before = target.TryGetValue(field, out current) ? (JsonValue.AsLong(current) ?? 0) : 0;
                    target[field] = before + delta.Value;
                }
                else
                {
                    target[field] = JsonValue.DeepClone(value);
                }
            });
        }

        /// <summary>All clans (optionally of one category, matching all keywords in name/description).</summary>
        public List<Dictionary<string, object>> Search(string category, string keywords)
        {
            var result = new List<Dictionary<string, object>>();
            string[] words = string.IsNullOrEmpty(keywords) ? new string[0] : keywords.ToLowerInvariant().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string key in store.Keys())
            {
                Dictionary<string, object> clan;
                try
                {
                    clan = store.Read(key);
                }
                catch (DocumentStoreException ex)
                {
                    log.Error("clan " + key + " is unreadable; skipped", ex);
                    continue;
                }
                if (clan == null) continue;
                if (!string.IsNullOrEmpty(category) && !string.Equals(JsonValue.GetString(clan, "category"), category, StringComparison.OrdinalIgnoreCase)) continue;
                string text = ((JsonValue.GetString(clan, "name") ?? "") + " " + (JsonValue.GetString(clan, "description") ?? "")).ToLowerInvariant();
                bool all = true;
                foreach (string w in words) if (text.IndexOf(w, StringComparison.Ordinal) < 0) { all = false; break; }
                if (all) result.Add(clan);
            }
            return result;
        }

        /// <summary>Ids of the clans <paramref name="accountId"/> belongs to (the social profile's "groups").</summary>
        public List<string> GroupsOf(string accountId)
        {
            var groups = new List<string>();
            if (string.IsNullOrEmpty(accountId)) return groups;
            foreach (Dictionary<string, object> clan in Search(null, null))
                if (IsMember(clan, accountId)) groups.Add(JsonValue.GetString(clan, "id"));
            return groups;
        }

        public static bool IsMember(Dictionary<string, object> clan, string accountId)
        {
            return FindMember(clan, accountId) != null;
        }

        public static bool IsOwner(Dictionary<string, object> clan, string accountId)
        {
            return Owners(clan).Contains(accountId);
        }

        public static Dictionary<string, object> FindMember(Dictionary<string, object> clan, string accountId)
        {
            foreach (object m in Members(clan))
            {
                var member = m as Dictionary<string, object>;
                if (member != null && JsonValue.GetString(member, "account") == accountId) return member;
            }
            return null;
        }

        public static List<object> Members(Dictionary<string, object> clan)
        {
            List<object> members = JsonValue.GetArray(clan, "members");
            if (members == null && clan != null)
            {
                members = new List<object>();
                clan["members"] = members;
            }
            return members ?? new List<object>();
        }

        public static List<object> Owners(Dictionary<string, object> clan)
        {
            List<object> owners = JsonValue.GetArray(clan, "owners");
            if (owners == null && clan != null)
            {
                owners = new List<object>();
                clan["owners"] = owners;
            }
            return owners ?? new List<object>();
        }

        /// <summary>The clan as Osiris returned it (<c>OP/Clan.cs</c>): properties, owner(s), members, custom fields.</summary>
        public Dictionary<string, object> Render(Dictionary<string, object> clan)
        {
            var o = Fed.Obj();
            o["id"] = JsonValue.GetString(clan, "id");
            foreach (string p in StringProperties) o[p] = JsonValue.GetString(clan, p) ?? "";
            foreach (string p in NumberProperties) o[p] = JsonValue.GetLong(clan, p);
            o["inventory"] = JsonValue.DeepClone(JsonValue.GetObject(clan, "inventory") ?? Fed.Obj());
            o["edit_permissions"] = JsonValue.GetString(clan, "edit_permissions") ?? "";
            o["participations"] = JsonValue.DeepClone(JsonValue.GetArray(clan, "participations") ?? new List<object>());

            var members = new List<object>();
            var owners = new List<object>();
            List<object> ownerIds = Owners(clan);
            foreach (object m in Members(clan))
            {
                var member = m as Dictionary<string, object>;
                if (member == null) continue;
                Dictionary<string, object> rendered = RenderMember(clan, member);
                members.Add(rendered);
                if (ownerIds.Contains(JsonValue.GetString(member, "account"))) owners.Add(JsonValue.DeepClone(rendered));
            }
            o["member_count"] = (long)members.Count;
            o["owner"] = owners.Count > 0 ? JsonValue.DeepClone(owners[0]) : null;
            o["owners"] = owners;
            o["members"] = members;
            Dictionary<string, object> custom = JsonValue.GetObject(clan, "custom");
            if (custom != null)
                foreach (var kv in custom)
                    if (!o.ContainsKey(kv.Key)) o[kv.Key] = JsonValue.DeepClone(kv.Value);
            return o;
        }

        /// <summary>A member as Osiris returned it (<c>OP/ClanMember.cs</c>).</summary>
        public Dictionary<string, object> RenderMember(Dictionary<string, object> clan, Dictionary<string, object> member)
        {
            string account = JsonValue.GetString(member, "account");
            Dictionary<string, object> profile = SocialProfiles.For(context).Get(account);
            var o = Fed.Obj();
            o["credential"] = Fed.FedIdOf(account);
            o["fed_id"] = Fed.FedIdOf(account);
            o["name"] = JsonValue.GetString(profile, "name") ?? "";
            o["online"] = AlertHub.For(context).ListenerCount(Fed.FedIdOf(account)) > 0;
            o["status_line"] = JsonValue.GetString(profile, "status_line") ?? "";
            o["game"] = McvsConstants.ClientId;
            o["avatar"] = "";
            o["seconds_since_last_status_change"] = SocialProfiles.For(context).SecondsSinceStatusChange(profile);
            Dictionary<string, object> fields = JsonValue.GetObject(member, "fields");
            if (fields != null)
                foreach (var kv in fields)
                    if (!o.ContainsKey(kv.Key)) o[kv.Key] = JsonValue.DeepClone(kv.Value);
            return o;
        }

        private Dictionary<string, object> NewMember(string accountId)
        {
            var member = Fed.Obj();
            member["account"] = accountId;
            member["joined"] = Fed.Now(context);
            member["fields"] = Fed.Obj();
            return member;
        }

        private static void ApplyProperties(Dictionary<string, object> clan, Dictionary<string, object> properties, Dictionary<string, object> custom)
        {
            if (properties != null)
            {
                foreach (var kv in properties)
                {
                    if (Array.IndexOf(StringProperties, kv.Key) >= 0) clan[kv.Key] = JsonValue.AsString(kv.Value) ?? "";
                    else if (Array.IndexOf(NumberProperties, kv.Key) >= 0)
                    {
                        long? n = JsonValue.AsLong(kv.Value);
                        if (n.HasValue) clan[kv.Key] = n.Value;
                    }
                    else if (kv.Key == "inventory" && kv.Value is Dictionary<string, object>) clan["inventory"] = JsonValue.DeepClone(kv.Value);
                }
            }
            if (custom != null)
            {
                Dictionary<string, object> target = Custom(clan);
                foreach (var kv in custom) target[kv.Key] = JsonValue.DeepClone(kv.Value);
            }
        }

        private static Dictionary<string, object> Custom(Dictionary<string, object> clan)
        {
            Dictionary<string, object> custom = JsonValue.GetObject(clan, "custom");
            if (custom == null)
            {
                custom = Fed.Obj();
                clan["custom"] = custom;
            }
            return custom;
        }

        private static bool IsValidId(string clanId)
        {
            if (string.IsNullOrEmpty(clanId) || clanId.Length > 64) return false;
            foreach (char c in clanId)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>Formats a number for logs.</summary>
        internal static string Format(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
