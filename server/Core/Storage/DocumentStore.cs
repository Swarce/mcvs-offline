using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Core.Storage
{
    /// <summary>Raised when a stored document exists but cannot be read. Never treat this as "missing".</summary>
    public sealed class DocumentStoreException : IOException
    {
        public DocumentStoreException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// A directory of JSON documents, one file per key (<c>&lt;dir&gt;/&lt;encoded key&gt;.json</c>), with atomic writes
    /// and per-key locking. Each module gets its own store via <see cref="ServerContext.OpenStore"/>; nothing is
    /// cached in memory, so every read sees the latest completed write.
    /// <para>
    /// A file that exists but does not parse raises <see cref="DocumentStoreException"/> instead of looking absent:
    /// reporting stored data as missing makes the game reset the profile (verify-C3 §3.1).
    /// </para>
    /// </summary>
    public sealed class DocumentStore
    {
        public const string Extension = ".json";

        private readonly KeyedLock locks = new KeyedLock();

        public DocumentStore(string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("directory required", "directory");
            Directory = System.IO.Path.GetFullPath(directory);
            System.IO.Directory.CreateDirectory(Directory);
            AtomicFile.DeleteLeftovers(Directory);
        }

        public string Directory { get; private set; }

        /// <summary>
        /// Maps a key to a file name: [A-Za-z0-9.-] are kept, every other byte (UTF-8) becomes "_xx" (lower-case
        /// hex; '_' itself is "_5f"). The mapping is reversible and safe on every file system.
        /// </summary>
        public static string EncodeKey(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key required", "key");
            var sb = new StringBuilder(key.Length + 8);
            foreach (byte b in Encoding.UTF8.GetBytes(key))
            {
                char c = (char)b;
                bool safe = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'
                            || (c == '.' && sb.Length > 0);
                if (safe) sb.Append(c);
                else sb.Append('_').Append(((int)b).ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static string DecodeKey(string encoded)
        {
            var bytes = new List<byte>(encoded.Length);
            for (int i = 0; i < encoded.Length; i++)
            {
                char c = encoded[i];
                int value;
                if (c == '_' && i + 2 < encoded.Length
                    && int.TryParse(encoded.Substring(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value))
                {
                    bytes.Add((byte)value);
                    i += 2;
                }
                else
                {
                    bytes.Add((byte)c);
                }
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public string PathFor(string key)
        {
            return System.IO.Path.Combine(Directory, EncodeKey(key) + Extension);
        }

        /// <summary>Holds the key's lock until disposed (for multi-step operations; Read/Write/Update lock themselves).</summary>
        public IDisposable Lock(string key)
        {
            return locks.Acquire(key);
        }

        public bool Exists(string key)
        {
            return File.Exists(PathFor(key));
        }

        /// <summary>The stored text, or null if the key has no document.</summary>
        public string ReadText(string key)
        {
            using (locks.Acquire(key))
            {
                return AtomicFile.ReadAllTextOrNull(PathFor(key));
            }
        }

        /// <summary>The stored JSON object, or null if absent. Throws <see cref="DocumentStoreException"/> if unreadable.</summary>
        public Dictionary<string, object> Read(string key)
        {
            using (locks.Acquire(key))
            {
                return ReadUnlocked(key);
            }
        }

        public void WriteText(string key, string text)
        {
            using (locks.Acquire(key))
            {
                AtomicFile.WriteAllText(PathFor(key), text);
            }
        }

        /// <summary>Stores a MiniJson value (normally a Dictionary) as compact JSON.</summary>
        public void Write(string key, object document)
        {
            string text = MiniJson.Serialize(document);
            using (locks.Acquire(key))
            {
                AtomicFile.WriteAllText(PathFor(key), text);
            }
        }

        /// <summary>
        /// Read-modify-write under the key's lock. <paramref name="mutate"/> receives the stored object (or a new
        /// empty one) and may change it in place; the result is written back before the lock is released, unless
        /// <paramref name="mutate"/> throws (then nothing is written).
        /// </summary>
        public TResult Update<TResult>(string key, Func<Dictionary<string, object>, TResult> mutate)
        {
            if (mutate == null) throw new ArgumentNullException("mutate");
            using (locks.Acquire(key))
            {
                Dictionary<string, object> doc = ReadUnlocked(key) ?? new Dictionary<string, object>(StringComparer.Ordinal);
                TResult result = mutate(doc);
                AtomicFile.WriteAllText(PathFor(key), MiniJson.Serialize(doc));
                return result;
            }
        }

        public void Update(string key, Action<Dictionary<string, object>> mutate)
        {
            if (mutate == null) throw new ArgumentNullException("mutate");
            Update<bool>(key, doc =>
            {
                mutate(doc);
                return true;
            });
        }

        public bool Delete(string key)
        {
            using (locks.Acquire(key))
            {
                string path = PathFor(key);
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
        }

        /// <summary>All stored keys (decoded), in file-name order.</summary>
        public List<string> Keys()
        {
            var keys = new List<string>();
            foreach (string file in System.IO.Directory.GetFiles(Directory, "*" + Extension))
            {
                string name = System.IO.Path.GetFileName(file);
                if (name.EndsWith(AtomicFile.TempSuffix, StringComparison.Ordinal)) continue;
                keys.Add(DecodeKey(name.Substring(0, name.Length - Extension.Length)));
            }
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private Dictionary<string, object> ReadUnlocked(string key)
        {
            string path = PathFor(key);
            string text = AtomicFile.ReadAllTextOrNull(path);
            if (text == null) return null;
            try
            {
                var doc = MiniJson.Parse(text) as Dictionary<string, object>;
                if (doc == null) throw new JsonParseException("document is not a JSON object", 0);
                return doc;
            }
            catch (JsonParseException ex)
            {
                throw new DocumentStoreException("stored document '" + path + "' is not valid JSON", ex);
            }
        }
    }
}
