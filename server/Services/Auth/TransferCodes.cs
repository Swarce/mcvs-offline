using System;
using System.Collections.Generic;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Server.Services.Auth
{
    /// <summary>
    /// Account transfer codes (report 02 §3.6): <c>POST users/me/transfer_code</c> creates one for the caller's
    /// account; <c>authorize?grant_type=transfer_code</c> redeems it for a token of that account, which the client
    /// then uses to relink its anonymous credential (<c>POST users/me/credentials if_exists=relink_all</c>,
    /// EN/AuthenticationComponent.cs DoLinkAnonymousSession). Codes are single-use and expire (settings
    /// <c>auth.transferCodeHours</c>, default 168). Kept in the module store <c>&lt;data&gt;/auth/transfer-codes.json</c>.
    /// </summary>
    public sealed class TransferCodes
    {
        private const string Key = "transfer-codes";
        // No 0/O/1/I/L: the code is typed by hand on the other device.
        private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        private const int Length = 10;

        private readonly DocumentStore store;
        private readonly IClock clock;
        private readonly Func<long> lifetimeHours;

        public TransferCodes(DocumentStore store, IClock clock, Func<long> lifetimeHours)
        {
            if (store == null) throw new ArgumentNullException("store");
            this.store = store;
            this.clock = clock ?? SystemClock.Instance;
            this.lifetimeHours = lifetimeHours ?? (() => 168);
        }

        /// <summary>A new code for <paramref name="accountId"/>; <paramref name="expires"/> receives its expiry (UTC).</summary>
        public string Create(string accountId, out DateTime expires)
        {
            DateTime now = clock.UtcNow;
            DateTime expiry = now.AddHours(Num.Clamp(lifetimeHours(), 1, 24L * 365));
            string code = store.Update(Key, doc =>
            {
                RemoveExpired(doc, now);
                string candidate;
                do candidate = NewCode(); while (doc.ContainsKey(candidate));
                var entry = new Dictionary<string, object>(StringComparer.Ordinal);
                entry["account"] = accountId;
                entry["created"] = TimeFormat.Iso8601(now);
                entry["expires"] = TimeFormat.Iso8601(expiry);
                doc[candidate] = entry;
                return candidate;
            });
            expires = expiry;
            return code;
        }

        /// <summary>Consumes <paramref name="code"/> (case and spaces ignored); the account id, or null if unknown or expired.</summary>
        public string Redeem(string code)
        {
            string normalized = Normalize(code);
            if (normalized.Length == 0) return null;
            DateTime now = clock.UtcNow;
            return store.Update(Key, doc =>
            {
                RemoveExpired(doc, now);
                var entry = JsonValue.Get(doc, normalized) as Dictionary<string, object>;
                if (entry == null) return null;
                doc.Remove(normalized);
                return JsonValue.GetString(entry, "account");
            });
        }

        private static string Normalize(string code)
        {
            if (code == null) return "";
            var sb = new StringBuilder(code.Length);
            foreach (char c in code)
            {
                if (c == ' ' || c == '-') continue;
                sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        private static void RemoveExpired(Dictionary<string, object> doc, DateTime now)
        {
            var expired = new List<string>();
            foreach (var kv in doc)
            {
                DateTime expires;
                var entry = kv.Value as Dictionary<string, object>;
                if (entry == null || !TimeFormat.TryParse(JsonValue.GetString(entry, "expires"), out expires) || expires <= now)
                    expired.Add(kv.Key);
            }
            foreach (string key in expired) doc.Remove(key);
        }

        private static string NewCode()
        {
            string hex = Ids.RandomHex(Length);
            var sb = new StringBuilder(Length);
            for (int i = 0; i < Length; i++)
            {
                int b = HexValue(hex[i * 2]) * 16 + HexValue(hex[i * 2 + 1]);
                sb.Append(Alphabet[b % Alphabet.Length]);
            }
            return sb.ToString();
        }

        private static int HexValue(char c)
        {
            return c <= '9' ? c - '0' : c - 'a' + 10;
        }
    }
}
