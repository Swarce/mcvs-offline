using System;
using System.Collections.Generic;
using System.Globalization;

namespace McvsOffline.Server.Core.Json
{
    /// <summary>
    /// Dotted-path helpers over MiniJson values, with the semantics Seshat storage needs (verify-C3 §7):
    /// <list type="bullet">
    /// <item>paths are split on '.' only ("_mcvs.gameProfile"); stored keys may themselves contain dots, they are
    /// just not addressable by path;</item>
    /// <item><see cref="Set"/> creates (or replaces non-object) intermediates;</item>
    /// <item><see cref="DeepMerge"/> merges objects recursively and replaces arrays/scalars; JSON null is stored;</item>
    /// <item><see cref="Project"/> (include_fields) returns the selected paths as <b>nested</b> objects, never as
    /// flat "a.b" keys, because the game reads them with SelectToken.</item>
    /// </list>
    /// A path segment that is a non-negative integer also indexes into arrays for reads.
    /// </summary>
    public static class JsonPath
    {
        /// <summary>Splits a dotted path. Null or "" means the root and yields no segments.</summary>
        public static string[] Split(string path)
        {
            if (string.IsNullOrEmpty(path)) return new string[0];
            string[] parts = path.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) throw new ArgumentException("empty segment in JSON path '" + path + "'", "path");
            }
            return parts;
        }

        /// <summary>Looks up a dotted path. Returns false when any segment is missing.</summary>
        public static bool TryGet(object root, string path, out object value)
        {
            value = root;
            foreach (string segment in Split(path))
            {
                var obj = value as Dictionary<string, object>;
                if (obj != null)
                {
                    if (!obj.TryGetValue(segment, out value)) { value = null; return false; }
                    continue;
                }
                var arr = value as List<object>;
                int index;
                if (arr != null && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index)
                    && index < arr.Count)
                {
                    value = arr[index];
                    continue;
                }
                value = null;
                return false;
            }
            return true;
        }

        /// <summary>Value at a dotted path, or null when missing.</summary>
        public static object Get(object root, string path)
        {
            object value;
            return TryGet(root, path, out value) ? value : null;
        }

        /// <summary>
        /// Sets the value at a dotted path, creating intermediate objects and replacing any non-object
        /// intermediate. The root path ("" / null) is not settable; replace the document instead.
        /// </summary>
        public static void Set(Dictionary<string, object> root, string path, object value)
        {
            if (root == null) throw new ArgumentNullException("root");
            string[] segments = Split(path);
            if (segments.Length == 0) throw new ArgumentException("cannot set the root of a document", "path");
            Dictionary<string, object> current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                object next;
                var child = current.TryGetValue(segments[i], out next) ? next as Dictionary<string, object> : null;
                if (child == null)
                {
                    child = new Dictionary<string, object>(StringComparer.Ordinal);
                    current[segments[i]] = child;
                }
                current = child;
            }
            current[segments[segments.Length - 1]] = value;
        }

        /// <summary>Removes the value at a dotted path (key order of the parent is preserved). Returns false if absent.</summary>
        public static bool Remove(Dictionary<string, object> root, string path)
        {
            if (root == null) throw new ArgumentNullException("root");
            string[] segments = Split(path);
            if (segments.Length == 0) throw new ArgumentException("cannot remove the root of a document", "path");
            Dictionary<string, object> current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                object next;
                if (!current.TryGetValue(segments[i], out next)) return false;
                current = next as Dictionary<string, object>;
                if (current == null) return false;
            }
            return RemoveKey(current, segments[segments.Length - 1]);
        }

        /// <summary>
        /// Removes one key and keeps the enumeration order of the remaining keys. (A plain Dictionary.Remove
        /// leaves a hole that the next insert fills, which would move that new key to the middle.)
        /// </summary>
        public static bool RemoveKey(Dictionary<string, object> obj, string key)
        {
            if (obj == null || key == null || !obj.ContainsKey(key)) return false;
            var remaining = new List<KeyValuePair<string, object>>(obj.Count - 1);
            foreach (var kv in obj)
            {
                if (!string.Equals(kv.Key, key, StringComparison.Ordinal)) remaining.Add(kv);
            }
            obj.Clear();
            foreach (var kv in remaining) obj.Add(kv.Key, kv.Value);
            return true;
        }

        /// <summary>
        /// Deep-merges <paramref name="patch"/> into <paramref name="target"/>: when both sides hold an object the
        /// merge recurses, otherwise the patch value (array, scalar or null) replaces the target value. Patch values
        /// are deep-copied so the target never aliases the patch.
        /// </summary>
        public static void DeepMerge(Dictionary<string, object> target, Dictionary<string, object> patch)
        {
            if (target == null) throw new ArgumentNullException("target");
            if (patch == null) return;
            foreach (var kv in patch)
            {
                var patchObj = kv.Value as Dictionary<string, object>;
                object existing;
                var targetObj = target.TryGetValue(kv.Key, out existing) ? existing as Dictionary<string, object> : null;
                if (patchObj != null && targetObj != null)
                {
                    DeepMerge(targetObj, patchObj);
                }
                else
                {
                    target[kv.Key] = JsonValue.DeepClone(kv.Value);
                }
            }
        }

        /// <summary>
        /// Projection for <c>include_fields</c>: a new object holding a deep copy of every requested dotted path that
        /// exists in <paramref name="source"/>, re-nested (["_mcvs.matchers","level"] →
        /// {"_mcvs":{"matchers":...},"level":...}). Missing paths are simply absent from the result.
        /// </summary>
        public static Dictionary<string, object> Project(Dictionary<string, object> source, IEnumerable<string> paths)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (source == null || paths == null) return result;
            foreach (string raw in paths)
            {
                if (raw == null) continue;
                string path = raw.Trim();
                if (path.Length == 0) continue;
                string[] segments;
                try
                {
                    segments = Split(path);
                }
                catch (ArgumentException)
                {
                    continue; // "a..b": not addressable, ignore like a missing field
                }
                object value;
                if (!TryGet(source, path, out value)) continue;
                object existing;
                if (segments.Length > 1 && TryGet(result, path, out existing)) continue; // already covered by a parent
                Set(result, path, JsonValue.DeepClone(value));
            }
            return result;
        }

        /// <summary>Splits a comma-separated include_fields parameter.</summary>
        public static List<string> SplitFieldList(string commaSeparated)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(commaSeparated)) return list;
            foreach (string part in commaSeparated.Split(','))
            {
                string p = part.Trim();
                if (p.Length > 0) list.Add(p);
            }
            return list;
        }
    }
}
