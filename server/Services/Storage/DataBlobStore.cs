using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>
    /// Seshat "data" objects (<c>GET|POST data/{cred}/{key}</c>, <c>GET data</c>, <c>POST data/me/{key}/delete</c>;
    /// OP/StorageApi.cs:165-232). Only BinaryProfileSection uses them and it has no subclass in the game (report 03
    /// §2.1), so this is a small faithful store: one JSON file per key in &lt;data&gt;/storage/data/&lt;account&gt;/.
    /// </summary>
    internal sealed class DataBlobStore
    {
        private const string Extension = ".json";

        private readonly string root;
        private readonly FairKeyedLock locks;
        private readonly IClock clock;

        public DataBlobStore(string root, FairKeyedLock locks, IClock clock)
        {
            this.root = Path.GetFullPath(root);
            this.locks = locks ?? new FairKeyedLock();
            this.clock = clock ?? SystemClock.Instance;
            Directory.CreateDirectory(this.root);
        }

        /// <summary>The stored entry {key, data, visibility, category, expiration, indexes, meta_data, created, modified}, or null.</summary>
        public Dictionary<string, object> Get(string accountId, string key)
        {
            using (locks.Acquire(LockKey(accountId)))
            {
                return ReadUnlocked(accountId, key);
            }
        }

        public void Put(string accountId, string key, string data, string visibility, string category, string expiration,
                        string indexes, string metaData)
        {
            using (locks.Acquire(LockKey(accountId)))
            {
                Dictionary<string, object> entry = ReadUnlocked(accountId, key);
                string now = TimeFormat.Iso8601(clock.UtcNow);
                string created = entry == null ? now : JsonValue.GetString(entry, "created", now);
                entry = ProfileJson.NewObject();
                entry["key"] = key;
                entry["data"] = data ?? "";
                entry["visibility"] = visibility;
                entry["category"] = category;
                entry["expiration"] = JsonValue.AsLong(expiration);
                entry["indexes"] = ParseJsonOrText(indexes);
                entry["meta_data"] = ParseJsonOrText(metaData);
                entry["created"] = created;
                entry["modified"] = now;
                string path = PathFor(accountId, key);
                AtomicFile.WriteAllText(path, MiniJson.Serialize(entry, true) + "\n");
            }
        }

        public bool Delete(string accountId, string key)
        {
            using (locks.Acquire(LockKey(accountId)))
            {
                string path = PathFor(accountId, key);
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
        }

        /// <summary>The account's entries (without their data), optionally filtered by category.</summary>
        public List<object> Search(string accountId, string category)
        {
            var result = new List<object>();
            using (locks.Acquire(LockKey(accountId)))
            {
                string dir = DirectoryFor(accountId);
                if (!Directory.Exists(dir)) return result;
                string[] files = Directory.GetFiles(dir, "*" + Extension);
                Array.Sort(files, StringComparer.Ordinal);
                foreach (string file in files)
                {
                    object parsed;
                    string text = AtomicFile.ReadAllTextOrNull(file);
                    var entry = text != null && MiniJson.TryParse(text, out parsed) ? parsed as Dictionary<string, object> : null;
                    if (entry == null) continue;
                    if (!string.IsNullOrEmpty(category) && JsonValue.GetString(entry, "category") != category) continue;
                    var summary = ProfileJson.CloneObject(entry);
                    JsonPath.RemoveKey(summary, "data");
                    summary["credential"] = McvsConstants.FedIdPrefix + accountId;
                    result.Add(summary);
                }
            }
            return result;
        }

        private Dictionary<string, object> ReadUnlocked(string accountId, string key)
        {
            string text = AtomicFile.ReadAllTextOrNull(PathFor(accountId, key));
            if (text == null) return null;
            object parsed;
            if (!MiniJson.TryParse(text, out parsed) || !(parsed is Dictionary<string, object>))
                throw new StorageUnavailableException("data object '" + key + "' of " + accountId + " is unreadable");
            return (Dictionary<string, object>)parsed;
        }

        private static object ParseJsonOrText(string text)
        {
            if (text == null) return null;
            object parsed;
            return MiniJson.TryParse(text, out parsed) ? parsed : text;
        }

        private string DirectoryFor(string accountId)
        {
            return Path.Combine(root, DocumentStore.EncodeKey(accountId));
        }

        private string PathFor(string accountId, string key)
        {
            return Path.Combine(DirectoryFor(accountId), DocumentStore.EncodeKey(key) + Extension);
        }

        private static string LockKey(string accountId)
        {
            return "data|" + accountId;
        }
    }
}
