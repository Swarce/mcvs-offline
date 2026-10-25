using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Server.Core.Accounts
{
    /// <summary>
    /// A Federation account. Instances returned by <see cref="AccountStore"/> are snapshots: change an account
    /// through <see cref="AccountStore.Update"/>, which persists the change.
    /// </summary>
    public sealed class Account
    {
        public Account()
        {
            Credentials = new List<string>();
            DeviceIds = new List<string>();
            Data = new Dictionary<string, object>(StringComparer.Ordinal);
        }

        /// <summary>The account id ("account" in users/me). Lower-case hex, never contains ':' (report 02 §3.2).</summary>
        public string Id { get; set; }

        /// <summary>"fed_id:" + Id.</summary>
        public string FedId { get { return McvsConstants.FedIdPrefix + Id; } }

        /// <summary>Linked credentials, "type:username", first one = the one that created the account.</summary>
        public List<string> Credentials { get; set; }

        public List<string> DeviceIds { get; set; }

        /// <summary>ISO 8601 creation time.</summary>
        public string Created { get; set; }

        /// <summary>Free-form per-account data for modules (e.g. Data["alias"]). Keep values MiniJson-serializable.</summary>
        public Dictionary<string, object> Data { get; set; }

        public Account Clone()
        {
            return FromJson(ToJson());
        }

        public Dictionary<string, object> ToJson()
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["id"] = Id;
            o["credentials"] = new List<object>(Credentials.ConvertAll(c => (object)c));
            o["devices"] = new List<object>(DeviceIds.ConvertAll(d => (object)d));
            o["created"] = Created;
            o["data"] = JsonValue.DeepClone(Data);
            return o;
        }

        public static Account FromJson(Dictionary<string, object> o)
        {
            var a = new Account();
            a.Id = JsonValue.GetString(o, "id");
            a.Created = JsonValue.GetString(o, "created");
            var creds = JsonValue.GetArray(o, "credentials");
            if (creds != null)
                foreach (var c in creds) { string s = JsonValue.AsString(c); if (!string.IsNullOrEmpty(s)) a.Credentials.Add(s); }
            var devices = JsonValue.GetArray(o, "devices");
            if (devices != null)
                foreach (var d in devices) { string s = JsonValue.AsString(d); if (!string.IsNullOrEmpty(s)) a.DeviceIds.Add(s); }
            var data = JsonValue.GetObject(o, "data");
            if (data != null) a.Data = JsonValue.DeepCloneObject(data);
            return a;
        }
    }

    /// <summary>
    /// Credential → account mapping, persisted in accounts.json (ARCHITECTURE §3 "Accounts &amp; auth").
    /// <para>
    /// Account ids are <b>derived deterministically</b> from the creating credential (SHA-1, 24 hex chars), so:
    /// the client's own session and the game server's for_username session for the same anonymous credential
    /// always agree (verify-C2 §1.5); independent servers (LAN hosts, a reinstalled embedded server, a lost
    /// accounts.json) compute the same fed id for the same player (verify-C6 §5 topology B).
    /// </para>
    /// Thread-safe; every mutation is written atomically before the call returns.
    /// </summary>
    public sealed class AccountStore
    {
        public const string FileName = "accounts.json";

        private readonly object gate = new object();
        private readonly string path;
        private readonly IClock clock;
        private readonly Log log;
        private readonly Dictionary<string, Account> byId = new Dictionary<string, Account>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> byCredential = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> order = new List<string>();

        public AccountStore(string path, IClock clock, Log log)
        {
            this.path = Path.GetFullPath(path);
            this.clock = clock ?? SystemClock.Instance;
            this.log = log ?? Log.Null;
            Load();
        }

        public int Count
        {
            get { lock (gate) return byId.Count; }
        }

        /// <summary>The id an account created by <paramref name="credential"/> gets: hex(sha1(...))[0..24].</summary>
        public static string DeriveAccountId(string credential)
        {
            return Ids.Sha1Hex("mcvs-offline/account/v1|" + Normalize(credential)).Substring(0, 24);
        }

        /// <summary>Canonical "type:username" (type lower-cased). Throws ArgumentException for invalid credentials.</summary>
        public static string Normalize(string credential)
        {
            Credential c;
            if (!Credential.TryParse(credential, out c) || !c.IsValid || c.IsMe)
                throw new ArgumentException("invalid credential '" + credential + "'", "credential");
            return c.ToString();
        }

        /// <summary>
        /// The account for <paramref name="credential"/>, created on first use (auth/authorize auto-creates accounts for
        /// unknown credentials). A "fed_id:&lt;id&gt;" credential addresses (or creates) the account with that id.
        /// <paramref name="deviceId"/>, when given, is remembered on the account.
        /// </summary>
        public Account GetOrCreate(string credential, string deviceId = null)
        {
            string normalized = Normalize(credential);
            lock (gate)
            {
                Account account = FindUnlocked(normalized);
                bool changed = false;
                if (account == null)
                {
                    account = new Account();
                    account.Id = NewIdFor(normalized);
                    account.Created = TimeFormat.Iso8601(clock.UtcNow);
                    if (!normalized.StartsWith(McvsConstants.FedIdPrefix, StringComparison.Ordinal)) account.Credentials.Add(normalized);
                    byId[account.Id] = account;
                    order.Add(account.Id);
                    foreach (var c in account.Credentials) byCredential[c] = account.Id;
                    changed = true;
                    log.Info("created account " + account.Id + " for " + normalized);
                }
                if (!string.IsNullOrEmpty(deviceId) && !account.DeviceIds.Contains(deviceId))
                {
                    account.DeviceIds.Add(deviceId);
                    changed = true;
                }
                if (changed) SaveUnlocked();
                return account.Clone();
            }
        }

        /// <summary>The account owning <paramref name="credential"/>, or null. Accepts "fed_id:&lt;id&gt;" too.</summary>
        public Account Find(string credential)
        {
            string normalized;
            try
            {
                normalized = Normalize(credential);
            }
            catch (ArgumentException)
            {
                return null;
            }
            lock (gate)
            {
                var account = FindUnlocked(normalized);
                return account == null ? null : account.Clone();
            }
        }

        public Account FindById(string accountId)
        {
            if (string.IsNullOrEmpty(accountId)) return null;
            lock (gate)
            {
                Account account;
                return byId.TryGetValue(accountId, out account) ? account.Clone() : null;
            }
        }

        /// <summary>Accounts that have used <paramref name="deviceId"/>.</summary>
        public List<Account> FindByDevice(string deviceId)
        {
            var result = new List<Account>();
            if (string.IsNullOrEmpty(deviceId)) return result;
            lock (gate)
            {
                foreach (string id in order)
                {
                    if (byId[id].DeviceIds.Contains(deviceId)) result.Add(byId[id].Clone());
                }
            }
            return result;
        }

        public List<Account> All()
        {
            var result = new List<Account>();
            lock (gate)
            {
                foreach (string id in order) result.Add(byId[id].Clone());
            }
            return result;
        }

        /// <summary>Applies <paramref name="mutate"/> to the stored account and persists it. Id and credentials are re-indexed.</summary>
        public Account Update(string accountId, Action<Account> mutate)
        {
            if (mutate == null) throw new ArgumentNullException("mutate");
            lock (gate)
            {
                Account account;
                if (accountId == null || !byId.TryGetValue(accountId, out account))
                    throw new KeyNotFoundException("no account '" + accountId + "'");
                var working = account.Clone();
                mutate(working);
                working.Id = account.Id; // the id is immutable
                foreach (var c in account.Credentials) byCredential.Remove(c);
                byId[account.Id] = working;
                foreach (var c in working.Credentials) byCredential[c] = working.Id;
                SaveUnlocked();
                return working.Clone();
            }
        }

        /// <summary>
        /// Attaches <paramref name="credential"/> to the account, moving it away from any other account that owns it
        /// (users/me/credentials with if_exists=relink). Returns false if the account does not exist.
        /// </summary>
        public bool LinkCredential(string accountId, string credential)
        {
            string normalized = Normalize(credential);
            lock (gate)
            {
                Account target;
                if (accountId == null || !byId.TryGetValue(accountId, out target)) return false;
                string ownerId;
                if (byCredential.TryGetValue(normalized, out ownerId))
                {
                    if (ownerId == accountId) return true;
                    Account owner;
                    if (byId.TryGetValue(ownerId, out owner)) owner.Credentials.Remove(normalized);
                }
                target.Credentials.Add(normalized);
                byCredential[normalized] = accountId;
                SaveUnlocked();
                return true;
            }
        }

        private Account FindUnlocked(string normalized)
        {
            Account account;
            if (normalized.StartsWith(McvsConstants.FedIdPrefix, StringComparison.Ordinal))
            {
                string id = normalized.Substring(McvsConstants.FedIdPrefix.Length);
                return byId.TryGetValue(id, out account) ? account : null;
            }
            string ownerId;
            if (byCredential.TryGetValue(normalized, out ownerId) && byId.TryGetValue(ownerId, out account)) return account;
            return null;
        }

        private string NewIdFor(string normalized)
        {
            if (normalized.StartsWith(McvsConstants.FedIdPrefix, StringComparison.Ordinal))
                return normalized.Substring(McvsConstants.FedIdPrefix.Length);
            string id = DeriveAccountId(normalized);
            for (int n = 1; byId.ContainsKey(id); n++)
            {
                // Only reachable if a different credential already owns the derived id (hash collision, or an id
                // created directly from a fed_id): fall back to a still-deterministic variant.
                id = DeriveAccountId(normalized + "#" + n);
            }
            return id;
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
                var list = JsonValue.GetArray(doc, "accounts");
                if (list == null) return;
                foreach (var item in list)
                {
                    var obj = item as Dictionary<string, object>;
                    if (obj == null) continue;
                    var account = Account.FromJson(obj);
                    if (string.IsNullOrEmpty(account.Id) || byId.ContainsKey(account.Id)) continue;
                    byId[account.Id] = account;
                    order.Add(account.Id);
                    foreach (var c in account.Credentials) byCredential[c] = account.Id;
                }
            }
            catch (Exception ex)
            {
                // Ids are derived from credentials, so starting empty still maps every player to the same account;
                // only extra linked credentials and per-account data are lost. Keep the broken file for inspection.
                string aside = path + ".corrupt-" + TimeFormat.ToUnixSeconds(clock.UtcNow);
                log.Error(path + " is unreadable; moved to " + aside + " and starting with an empty account list", ex);
                try { FileSystem.Rename(path, aside); } catch (Exception) { }
                byId.Clear();
                byCredential.Clear();
                order.Clear();
            }
        }

        private void SaveUnlocked()
        {
            var list = new List<object>(order.Count);
            foreach (string id in order) list.Add(byId[id].ToJson());
            var doc = new Dictionary<string, object>(StringComparer.Ordinal);
            doc["version"] = 1L;
            doc["accounts"] = list;
            AtomicFile.WriteAllText(path, MiniJson.Serialize(doc, true) + "\n");
        }
    }
}
