using System;
using System.Collections;
using System.Collections.Generic;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>Ordered HTTP header list with case-insensitive lookup. Repeated headers are kept as separate entries.</summary>
    public sealed class HttpHeaders : IEnumerable<KeyValuePair<string, string>>
    {
        private readonly List<KeyValuePair<string, string>> entries = new List<KeyValuePair<string, string>>();

        public int Count { get { return entries.Count; } }

        /// <summary>First value of the header, or null. Setting replaces every value of that header.</summary>
        public string this[string name]
        {
            get { return Get(name); }
            set { Set(name, value); }
        }

        public string Get(string name)
        {
            foreach (var kv in entries)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return null;
        }

        public List<string> GetAll(string name)
        {
            var values = new List<string>();
            foreach (var kv in entries)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) values.Add(kv.Value);
            }
            return values;
        }

        public bool Contains(string name)
        {
            return Get(name) != null;
        }

        public void Add(string name, string value)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("header name required", "name");
            entries.Add(new KeyValuePair<string, string>(name, value ?? ""));
        }

        /// <summary>Replaces all values of <paramref name="name"/>; a null value removes the header.</summary>
        public void Set(string name, string value)
        {
            Remove(name);
            if (value != null) Add(name, value);
        }

        public bool Remove(string name)
        {
            return entries.RemoveAll(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        /// <summary>True when a comma-separated header (e.g. Connection) contains <paramref name="token"/>.</summary>
        public bool HasToken(string name, string token)
        {
            foreach (string value in GetAll(name))
            {
                foreach (string part in value.Split(','))
                {
                    if (string.Equals(part.Trim(), token, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() { return entries.GetEnumerator(); }

        IEnumerator IEnumerable.GetEnumerator() { return entries.GetEnumerator(); }
    }
}
