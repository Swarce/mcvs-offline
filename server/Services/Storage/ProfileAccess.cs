using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>
    /// In-process access to this device's profiles for other modules: <c>context.Features.Get&lt;IProfileAccess&gt;()</c>.
    /// Every call is applied under the account's lock with the same durability and backups as a game request, and
    /// publishes <see cref="LocalProfileStore.ChangedTopic"/>. Paths are dotted selectors ("" = the whole document).
    /// </summary>
    public interface IProfileAccess
    {
        /// <summary>True when the account has a stored profile.</summary>
        bool HasProfile(string accountId);

        /// <summary>
        /// A deep copy of the value at <paramref name="path"/> as plain MiniJson values ("" = the document as the
        /// game reads it, with the read-only fields), or null when absent.
        /// </summary>
        object Read(string accountId, string path);

        /// <summary>The body a selector GET would answer (exact stored text for verbatim values), or null when absent.</summary>
        string ReadText(string accountId, string path);

        /// <summary>Sets a MiniJson value at a (non-root) path, like a selector POST without operation.</summary>
        void Set(string accountId, string path, object value);

        /// <summary>Deep-merges an object at a path ("" = root), like operation=merge.</summary>
        void Merge(string accountId, string path, Dictionary<string, object> patch);

        /// <summary>
        /// Same, and when <paramref name="backupReason"/> is set the stored profile is backed up first (a one-way
        /// change such as the unlock-all ledger, verify-C5 "Recommended design"). The reason becomes part of the
        /// backup id: lower-case letters and digits only.
        /// </summary>
        void Merge(string accountId, string path, Dictionary<string, object> patch, string backupReason);

        /// <summary>Removes a (non-root) path. Returns false when it was absent.</summary>
        bool Remove(string accountId, string path);

        /// <summary>
        /// Adds <paramref name="delta"/> to the IAP ledger entry <c>inventory[rewardKey]</c> and returns the new count.
        /// This is the generic grant channel: the TS diffs the ledger against what it already granted and gives the
        /// difference as an IAP_BUNDLE reward on the next login (or a set_profile alert); keys use the reward grammar,
        /// e.g. "UnlockMerc_ALL_MAX", "VersusCoins" (report 03 §13.3; EN/IapStoreComponent.cs:1243-1305).
        /// </summary>
        long AddToInventory(string accountId, string rewardKey, long delta);

        /// <summary>Same for any integer ledger section ("inventory" or the ads ledger "_inventory").</summary>
        long AddToLedger(string accountId, string section, string key, long delta);
    }

    /// <summary><see cref="IProfileAccess"/> over the <see cref="LocalProfileStore"/>.</summary>
    internal sealed class ProfileAccess : IProfileAccess
    {
        /// <summary>Published after a ledger change: {account, fedId, section, key, count, delta}.</summary>
        public const string LedgerTopic = "storage.inventory.changed";

        private readonly LocalProfileStore store;
        private readonly EventHub events;

        public ProfileAccess(LocalProfileStore store, EventHub events)
        {
            if (store == null) throw new ArgumentNullException("store");
            this.store = store;
            this.events = events;
        }

        public bool HasProfile(string accountId)
        {
            return store.ReadDocument(accountId).IsStored;
        }

        public object Read(string accountId, string path)
        {
            object node;
            return TryReadNode(accountId, path, out node) ? ProfileJson.ToPlain(node) : null;
        }

        public string ReadText(string accountId, string path)
        {
            object node;
            return TryReadNode(accountId, path, out node) ? ProfileJson.ResponseText(node) : null;
        }

        public void Set(string accountId, string path, object value)
        {
            string[] segments = ParsePath(path, false);
            object copy = ProfileJson.ToPlain(value);
            store.Commit(accountId, "api.set", path, "api", doc =>
            {
                var outcome = new WriteOutcome();
                ProfileJson.Set(doc.Root, segments, copy);
                outcome.Changed = true;
                return outcome;
            });
        }

        public void Merge(string accountId, string path, Dictionary<string, object> patch)
        {
            Merge(accountId, path, patch, null);
        }

        public void Merge(string accountId, string path, Dictionary<string, object> patch, string backupReason)
        {
            if (patch == null) throw new ArgumentNullException("patch");
            var write = new StorageWrite();
            write.Selector = path ?? "";
            write.Operation = SeshatOperations.Merge;
            write.ObjectText = MiniJson.Serialize(ProfileJson.ToPlain(patch));
            WriteOutcome outcome = store.Commit(accountId, "api.merge", path, "api", doc =>
            {
                WriteOutcome applied = SeshatOperations.Apply(doc, write);
                if (applied != null && backupReason != null && applied.BackupReason == null) applied.BackupReason = backupReason;
                return applied;
            });
            if (outcome.Error != null) throw new ArgumentException(outcome.Error, "path");
        }

        public bool Remove(string accountId, string path)
        {
            string[] segments = ParsePath(path, false);
            WriteOutcome outcome = store.Commit(accountId, "api.remove", path, "api", doc =>
            {
                var o = new WriteOutcome();
                o.Changed = ProfileJson.Remove(doc.Root, segments);
                return o;
            });
            return outcome.Changed;
        }

        public long AddToInventory(string accountId, string rewardKey, long delta)
        {
            return AddToLedger(accountId, "inventory", rewardKey, delta);
        }

        public long AddToLedger(string accountId, string section, string key, long delta)
        {
            if (string.IsNullOrEmpty(section) || section.IndexOf('.') >= 0) throw new ArgumentException("section must be a top-level key", "section");
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key required", "key");
            long count = 0;
            store.Commit(accountId, "api.ledger", section + "[" + key + "]", "api", doc =>
            {
                var outcome = new WriteOutcome();
                Dictionary<string, object> ledger = ProfileJson.MaterializeChild(doc.Root, section);
                if (ledger == null)
                {
                    ledger = ProfileJson.NewObject();
                    doc.Root[section] = ledger;
                }
                object current;
                ledger.TryGetValue(key, out current);
                long before = JsonValue.AsLong(ProfileJson.Unwrap(current)) ?? 0;
                // The TS reads each entry as (int)(long)JValue.Value: it must stay an integer within int range.
                count = Num.Clamp(before + delta, 0, Num.IntMax);
                ledger[key] = count; // keys may contain '.', so the ledger is addressed by key, not by path
                outcome.Changed = true;
                return outcome;
            });
            if (events != null)
            {
                var payload = ProfileJson.NewObject();
                payload["account"] = accountId;
                payload["fedId"] = McvsConstants.FedIdPrefix + accountId;
                payload["section"] = section;
                payload["key"] = key;
                payload["count"] = count;
                payload["delta"] = delta;
                events.Publish(LedgerTopic, payload);
            }
            return count;
        }

        private bool TryReadNode(string accountId, string path, out object node)
        {
            string[] segments = ParsePath(path, true);
            ProfileDocument doc = store.ReadDocument(accountId);
            if (!doc.IsStored)
            {
                node = null;
                return false;
            }
            return ProfileJson.TryGet(store.BuildView(doc), segments, out node);
        }

        private static string[] ParsePath(string path, bool allowRoot)
        {
            string[] segments;
            if (!ProfileJson.TryParsePath(path, out segments)) throw new ArgumentException("malformed path '" + path + "'", "path");
            if (!allowRoot && segments.Length == 0) throw new ArgumentException("a path is required", "path");
            return segments;
        }
    }
}
