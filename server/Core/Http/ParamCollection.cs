using System;
using System.Collections;
using System.Collections.Generic;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>
    /// Ordered multi-map of decoded query-string or form parameters. Names are case-sensitive (ordinal), as the
    /// Federation's are. The indexer returns the first value or null.
    /// </summary>
    public sealed class ParamCollection : IEnumerable<KeyValuePair<string, string>>
    {
        public static readonly ParamCollection Empty = new ParamCollection(true);

        private readonly List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();
        private readonly bool readOnly;

        public ParamCollection() { }

        private ParamCollection(bool readOnly) { this.readOnly = readOnly; }

        /// <summary>
        /// Parses "a=1&amp;b=x%20y" (query string without '?', or a form body). A name without '=' gets "".
        /// Empty pieces ("a=1&amp;&amp;b=2") are skipped.
        /// </summary>
        public static ParamCollection Parse(string encoded)
        {
            var result = new ParamCollection();
            if (string.IsNullOrEmpty(encoded)) return result;
            foreach (string piece in encoded.Split('&'))
            {
                if (piece.Length == 0) continue;
                int eq = piece.IndexOf('=');
                string name = eq < 0 ? piece : piece.Substring(0, eq);
                string value = eq < 0 ? "" : piece.Substring(eq + 1);
                result.Add(UrlEncoding.Decode(name, true), UrlEncoding.Decode(value, true));
            }
            return result;
        }

        public int Count { get { return pairs.Count; } }

        public string this[string name]
        {
            get { return Get(name); }
        }

        public string Get(string name)
        {
            foreach (var kv in pairs)
            {
                if (string.Equals(kv.Key, name, StringComparison.Ordinal)) return kv.Value;
            }
            return null;
        }

        public List<string> GetAll(string name)
        {
            var values = new List<string>();
            foreach (var kv in pairs)
            {
                if (string.Equals(kv.Key, name, StringComparison.Ordinal)) values.Add(kv.Value);
            }
            return values;
        }

        public bool Contains(string name)
        {
            foreach (var kv in pairs)
            {
                if (string.Equals(kv.Key, name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Distinct names in first-seen order.</summary>
        public List<string> Names
        {
            get
            {
                var names = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kv in pairs)
                {
                    if (seen.Add(kv.Key)) names.Add(kv.Key);
                }
                return names;
            }
        }

        public void Add(string name, string value)
        {
            if (readOnly) throw new InvalidOperationException("ParamCollection.Empty is read-only");
            pairs.Add(new KeyValuePair<string, string>(name ?? "", value ?? ""));
        }

        /// <summary>Lenient boolean (accepts the client's "True"/"False").</summary>
        public bool GetBool(string name, bool defaultValue)
        {
            return JsonValue.AsBool(Get(name)) ?? defaultValue;
        }

        public long GetLong(string name, long defaultValue)
        {
            return JsonValue.AsLong(Get(name)) ?? defaultValue;
        }

        /// <summary>First value of each name as a JSON object (for logs and debugging).</summary>
        public Dictionary<string, object> ToJsonObject()
        {
            var obj = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in pairs)
            {
                if (!obj.ContainsKey(kv.Key)) obj[kv.Key] = kv.Value;
            }
            return obj;
        }

        /// <summary>Re-encodes the collection as application/x-www-form-urlencoded.</summary>
        public override string ToString()
        {
            return UrlEncoding.EncodeForm(pairs);
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() { return pairs.GetEnumerator(); }

        IEnumerator IEnumerable.GetEnumerator() { return pairs.GetEnumerator(); }
    }
}
