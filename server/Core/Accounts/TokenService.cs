using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Server.Core.Accounts
{
    /// <summary>What an access token stands for.</summary>
    public sealed class TokenInfo
    {
        public TokenInfo()
        {
            Scopes = new List<string>();
        }

        public string Token { get; set; }

        /// <summary>Account of the token's subject (for an on-behalf token: the player, not the game server).</summary>
        public string AccountId { get; set; }

        /// <summary>The subject's credential, "type:username".</summary>
        public string Credential { get; set; }

        /// <summary>Super credential that obtained the token on behalf of the subject (for_username grant), else null.</summary>
        public string Actor { get; set; }

        public string ClientId { get; set; }

        /// <summary>Scopes as requested (the client caches tokens per scope set, report 02 §3.3).</summary>
        public List<string> Scopes { get; set; }

        public string DeviceId { get; set; }

        /// <summary>ISO 8601 issue time.</summary>
        public string Issued { get; set; }

        public string FedId { get { return McvsConstants.FedIdPrefix + AccountId; } }

        public bool IsOnBehalf { get { return !string.IsNullOrEmpty(Actor); } }

        /// <summary>Space-separated scopes, as echoed in the authorize response.</summary>
        public string ScopeString { get { return string.Join(" ", Scopes.ToArray()); } }

        public bool HasScope(string scope)
        {
            return Scopes.Contains(scope);
        }

        public TokenInfo Clone()
        {
            return FromJson(ToJson());
        }

        public Dictionary<string, object> ToJson()
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["token"] = Token;
            o["account"] = AccountId;
            o["credential"] = Credential;
            o["actor"] = Actor;
            o["clientId"] = ClientId;
            o["scopes"] = ScopeString;
            o["deviceId"] = DeviceId;
            o["issued"] = Issued;
            return o;
        }

        public static TokenInfo FromJson(Dictionary<string, object> o)
        {
            var t = new TokenInfo();
            t.Token = JsonValue.GetString(o, "token");
            t.AccountId = JsonValue.GetString(o, "account");
            t.Credential = JsonValue.GetString(o, "credential");
            t.Actor = JsonValue.GetString(o, "actor");
            t.ClientId = JsonValue.GetString(o, "clientId");
            t.Scopes = TokenService.ParseScopes(JsonValue.GetString(o, "scopes"));
            t.DeviceId = JsonValue.GetString(o, "deviceId");
            t.Issued = JsonValue.GetString(o, "issued");
            return t;
        }
    }

    /// <summary>
    /// Issues and resolves access tokens (ARCHITECTURE §3). Tokens are opaque to the game and never expire (the C#
    /// client has no expiry handling and only re-authorizes after a 401, report 02 §3.3).
    /// <para>
    /// Format: "&lt;issuer&gt;.&lt;32 hex&gt;", where the issuer is this server instance's id. Tokens are therefore
    /// self-describing: a LAN host can tell which device's server issued a token and forward verification there
    /// (roadmap M3). They contain no '~', ',' or '|' (the client derives a "silo" from tokens containing '~',
    /// report 02 §3.2).
    /// </para>
    /// Re-authorizing with the same subject, actor, client and scope set returns the existing token, which keeps
    /// tokens.json small across restarts. Persisted on every new token; oldest tokens are evicted past the capacity.
    /// </summary>
    public sealed class TokenService
    {
        public const string FileName = "tokens.json";

        private readonly object gate = new object();
        private readonly string path;
        private readonly IClock clock;
        private readonly Log log;
        private readonly int capacity;
        private readonly Dictionary<string, TokenInfo> byToken = new Dictionary<string, TokenInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> byGrant = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> order = new List<string>();
        private TokenInfo lastDirectAnonymous;

        public TokenService(string path, string issuer, IClock clock, Log log, int capacity = 4096)
        {
            if (string.IsNullOrEmpty(issuer) || issuer.IndexOf('.') >= 0) throw new ArgumentException("issuer must be non-empty without '.'", "issuer");
            this.path = Path.GetFullPath(path);
            Issuer = issuer;
            this.clock = clock ?? SystemClock.Instance;
            this.log = log ?? Log.Null;
            this.capacity = Num.Max(16, capacity);
            Load();
        }

        /// <summary>This server's issuer id (the token prefix).</summary>
        public string Issuer { get; private set; }

        public int Count
        {
            get { lock (gate) return byToken.Count; }
        }

        /// <summary>
        /// The last grant issued (or re-issued) in this process directly to an "anonymous:" credential, i.e. without an
        /// actor: the player's own login, never an on-behalf token of the game server or a "game:" grant. A copy with its
        /// ClientId and DeviceId; null until the first such grant. The settings page uses it to find the current player
        /// (design §6.6, source "session").
        /// </summary>
        public TokenInfo LastDirectAnonymous
        {
            get
            {
                lock (gate) return lastDirectAnonymous == null ? null : lastDirectAnonymous.Clone();
            }
        }

        /// <summary>The issuer part of a token, or null if it is not in "issuer.random" form.</summary>
        public static string GetIssuer(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            int dot = token.IndexOf('.');
            return dot > 0 ? token.Substring(0, dot) : null;
        }

        /// <summary>True if this server issued <paramref name="token"/> (by prefix; it may still be unknown).</summary>
        public bool IsLocal(string token)
        {
            return string.Equals(GetIssuer(token), Issuer, StringComparison.Ordinal);
        }

        /// <summary>Splits a scope parameter ("auth lobby config"; also tolerates ',' and '+').</summary>
        public static List<string> ParseScopes(string scope)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(scope)) return list;
            foreach (string part in scope.Split(new[] { ' ', ',', '+', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!list.Contains(part)) list.Add(part);
            }
            return list;
        }

        /// <summary>
        /// Issues (or re-issues) a token. <paramref name="credential"/> is the subject; <paramref name="actor"/> is the
        /// super credential for on-behalf tokens. Scope order is kept as requested.
        /// </summary>
        public TokenInfo Issue(string accountId, string credential, string clientId, IEnumerable<string> scopes,
                               string deviceId = null, string actor = null)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("account id required", "accountId");
            var scopeList = new List<string>();
            if (scopes != null)
                foreach (string s in scopes) if (!string.IsNullOrEmpty(s) && !scopeList.Contains(s)) scopeList.Add(s);

            string grant = GrantKey(accountId, credential, actor, clientId, scopeList);

            lock (gate)
            {
                string existing;
                TokenInfo info;
                if (byGrant.TryGetValue(grant, out existing) && byToken.TryGetValue(existing, out info))
                {
                    if (!string.IsNullOrEmpty(deviceId) && info.DeviceId != deviceId)
                    {
                        info.DeviceId = deviceId;
                        SaveUnlocked();
                    }
                    RememberDirectAnonymous(info);
                    // Echo the scopes in the order of this request.
                    var echo = info.Clone();
                    echo.Scopes = scopeList;
                    return echo;
                }

                info = new TokenInfo();
                info.Token = Issuer + "." + Ids.RandomHex(16);
                info.AccountId = accountId;
                info.Credential = credential;
                info.Actor = string.IsNullOrEmpty(actor) ? null : actor;
                info.ClientId = clientId;
                info.Scopes = scopeList;
                info.DeviceId = deviceId;
                info.Issued = TimeFormat.Iso8601(clock.UtcNow);
                Add(info, grant);
                while (order.Count > capacity) Evict(order[0]);
                SaveUnlocked();
                RememberDirectAnonymous(info);
                return info.Clone();
            }
        }

        /// <summary>
        /// Removes every persisted grant whose subject is <paramref name="credential"/> (ordinal), on every account and
        /// with or without an actor, and saves tokens.json. Returns how many were removed. Used after the credential is
        /// relinked to another account: its old tokens would still name the old account (design §6.4 step 7).
        /// </summary>
        public int RevokeCredential(string credential)
        {
            if (string.IsNullOrEmpty(credential)) return 0;
            lock (gate)
            {
                var revoke = new List<string>();
                foreach (string token in order)
                {
                    if (string.Equals(byToken[token].Credential, credential, StringComparison.Ordinal)) revoke.Add(token);
                }
                foreach (string token in revoke) Evict(token);
                if (revoke.Count > 0)
                {
                    SaveUnlocked();
                    log.Info("revoked " + revoke.Count + " token(s) of " + Credential.Mask(credential));
                }
                return revoke.Count;
            }
        }

        /// <summary>Caller holds <see cref="gate"/>.</summary>
        private void RememberDirectAnonymous(TokenInfo info)
        {
            if (!string.IsNullOrEmpty(info.Actor) || info.Credential == null) return;
            if (!info.Credential.StartsWith("anonymous:", StringComparison.Ordinal)) return;
            lastDirectAnonymous = info.Clone();
        }

        /// <summary>The token's record, or null if this server does not know it.</summary>
        public TokenInfo Resolve(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            lock (gate)
            {
                TokenInfo info;
                return byToken.TryGetValue(token, out info) ? info.Clone() : null;
            }
        }

        private void Add(TokenInfo info, string grant)
        {
            byToken[info.Token] = info;
            byGrant[grant] = info.Token;
            order.Add(info.Token);
        }

        private void Evict(string token)
        {
            order.Remove(token);
            TokenInfo info;
            if (byToken.TryGetValue(token, out info))
            {
                byToken.Remove(token);
                byGrant.Remove(GrantKey(info));
            }
        }

        private static string GrantKey(TokenInfo info)
        {
            return GrantKey(info.AccountId, info.Credential, info.Actor, info.ClientId, info.Scopes);
        }

        /// <summary>Identity of a grant: same subject, actor, client and scope set (order-insensitive) → same token.</summary>
        private static string GrantKey(string accountId, string credential, string actor, string clientId, List<string> scopes)
        {
            var sorted = new List<string>(scopes);
            sorted.Sort(StringComparer.Ordinal);
            return string.Join("|", new[] { accountId ?? "", credential ?? "", actor ?? "", clientId ?? "", string.Join(" ", sorted.ToArray()) });
        }

        private void Load()
        {
            string text;
            try
            {
                text = AtomicFile.ReadAllTextOrNull(path);
            }
            catch (Exception ex)
            {
                log.Error("cannot read " + path, ex);
                return;
            }
            if (text == null) return;
            try
            {
                var doc = MiniJson.ParseObject(text);
                var list = JsonValue.GetArray(doc, "tokens");
                if (list == null) return;
                foreach (var item in list)
                {
                    var obj = item as Dictionary<string, object>;
                    if (obj == null) continue;
                    var info = TokenInfo.FromJson(obj);
                    if (string.IsNullOrEmpty(info.Token) || string.IsNullOrEmpty(info.AccountId) || byToken.ContainsKey(info.Token)) continue;
                    Add(info, GrantKey(info));
                }
            }
            catch (Exception ex)
            {
                // Losing tokens is harmless: the client gets a 401, drops its tokens and re-authorizes once.
                log.Error(path + " is unreadable; starting with no tokens", ex);
                byToken.Clear();
                byGrant.Clear();
                order.Clear();
            }
        }

        private void SaveUnlocked()
        {
            var list = new List<object>(order.Count);
            foreach (string token in order) list.Add(byToken[token].ToJson());
            var doc = new Dictionary<string, object>(StringComparer.Ordinal);
            doc["version"] = 1L;
            doc["issuer"] = Issuer;
            doc["tokens"] = list;
            try
            {
                AtomicFile.WriteAllText(path, MiniJson.Serialize(doc) + "\n");
            }
            catch (Exception ex)
            {
                // Tokens stay valid in memory; only a restart would forget them (→ one 401 and re-authorize).
                log.Error("cannot write " + path, ex);
            }
        }
    }
}
