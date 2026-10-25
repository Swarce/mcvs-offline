using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Misc;

namespace McvsOffline.Server.Services.Social
{
    /// <summary>
    /// Osiris social profiles (name, language, country, status line), one document per account in
    /// &lt;data&gt;/social/. The game sets the name when the player picks one (<c>POST accounts/me</c>, errors swallowed,
    /// <c>OP/SocialProfileService.cs</c>) and reads it back as the chat nickname and in clan member lists.
    /// </summary>
    public sealed class SocialProfiles
    {
        public const string StoreName = "social";

        private static readonly object ForGate = new object();

        private readonly ServerContext context;
        private readonly DocumentStore store;

        public SocialProfiles(ServerContext context)
        {
            this.context = context;
            store = context.OpenStore(StoreName);
        }

        public static SocialProfiles For(ServerContext context)
        {
            lock (ForGate)
            {
                SocialProfiles profiles = context.Features.Get<SocialProfiles>();
                if (profiles == null)
                {
                    profiles = new SocialProfiles(context);
                    context.Features.Set(profiles);
                }
                return profiles;
            }
        }

        /// <summary>The stored profile, or null.</summary>
        public Dictionary<string, object> Get(string accountId)
        {
            return string.IsNullOrEmpty(accountId) ? null : store.Read(accountId);
        }

        /// <summary>The player's social name ("" when never set).</summary>
        public string NameOf(string accountId)
        {
            return JsonValue.GetString(Get(accountId), "name") ?? "";
        }

        /// <summary>Sets the non-null values among name/language/country.</summary>
        public void Update(string accountId, string name, string language, string country)
        {
            store.Update(accountId, doc =>
            {
                if (name != null) doc["name"] = name;
                if (language != null) doc["language"] = language;
                if (country != null) doc["country"] = country;
                doc["updated"] = Fed.Now(context);
            });
        }

        public void SetStatusLine(string accountId, string statusLine)
        {
            store.Update(accountId, doc =>
            {
                doc["status_line"] = statusLine ?? "";
                doc["status_changed"] = Fed.Now(context);
                doc["updated"] = Fed.Now(context);
            });
        }

        /// <summary>Seconds since the status line last changed (0 when it never did).</summary>
        public long SecondsSinceStatusChange(Dictionary<string, object> profile)
        {
            DateTime at;
            string changed = JsonValue.GetString(profile, "status_changed");
            if (string.IsNullOrEmpty(changed) || !TimeFormat.TryParse(changed, out at)) return 0;
            return Num.Max(0, (long)(context.Clock.UtcNow - at).TotalSeconds);
        }
    }
}
