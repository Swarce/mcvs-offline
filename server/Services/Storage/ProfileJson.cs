using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>
    /// Dotted-path operations over a profile tree: the MiniJson value model (Dictionary / List / scalars) in which
    /// some values are <see cref="VerbatimJson"/> nodes. Same semantics as <see cref="JsonPath"/> (verify-C3 §7:
    /// split request paths on '.', create intermediates, deep merge objects and replace everything else, nested
    /// projections), plus:
    /// <list type="bullet">
    /// <item>reads go through verbatim JSON objects transparently;</item>
    /// <item>writes inside a verbatim object first replace it by a mutable copy ("materialize");</item>
    /// <item><see cref="Write"/> embeds verbatim text unchanged, so stored values round-trip byte-for-byte.</item>
    /// </list>
    /// Output is deterministic: keys in insertion order, scalars formatted by <see cref="MiniJson"/>.
    /// </summary>
    public static class ProfileJson
    {
        private static readonly string[] NoSegments = new string[0];

        // ------------------------------------------------------------------ paths

        /// <summary>
        /// Splits a dotted request path ("_mcvs.gameProfile"). Null or "" is the root (no segments). Returns false for
        /// an empty segment ("a..b", ".a", "a.").
        /// </summary>
        public static bool TryParsePath(string path, out string[] segments)
        {
            segments = NoSegments;
            if (string.IsNullOrEmpty(path)) return true;
            string[] parts = path.Split('.');
            foreach (string p in parts)
            {
                if (p.Length == 0) return false;
            }
            segments = parts;
            return true;
        }

        /// <summary>"a.b.c" for display.</summary>
        public static string FormatPath(string[] segments)
        {
            return segments == null || segments.Length == 0 ? "" : string.Join(".", segments);
        }

        // ------------------------------------------------------------------ reads

        /// <summary>The value behind a node: a verbatim node's parsed value, otherwise the node itself.</summary>
        public static object Unwrap(object node)
        {
            var v = node as VerbatimJson;
            return v != null ? v.Value : node;
        }

        /// <summary>True for an object, plain or verbatim (decided without parsing a verbatim value).</summary>
        public static bool IsObjectNode(object node)
        {
            var verbatim = node as VerbatimJson;
            return verbatim != null ? verbatim.IsObject : node is Dictionary<string, object>;
        }

        /// <summary>
        /// Looks up a path and returns the stored <b>node</b> (possibly a <see cref="VerbatimJson"/>, so callers can
        /// answer with the exact stored text). Non-negative integer segments also index arrays.
        /// </summary>
        public static bool TryGet(object root, string[] segments, out object node)
        {
            node = root;
            if (segments == null) return true;
            foreach (string segment in segments)
            {
                object current = Unwrap(node);
                var obj = current as Dictionary<string, object>;
                if (obj != null)
                {
                    if (!obj.TryGetValue(segment, out node))
                    {
                        node = null;
                        return false;
                    }
                    continue;
                }
                var arr = current as List<object>;
                int index;
                if (arr != null && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index)
                    && index < arr.Count)
                {
                    node = arr[index];
                    continue;
                }
                node = null;
                return false;
            }
            return true;
        }

        public static bool Exists(object root, string[] segments)
        {
            object ignored;
            return TryGet(root, segments, out ignored);
        }

        // ------------------------------------------------------------------ writes

        /// <summary>
        /// Puts <paramref name="value"/> at the path, creating intermediate objects (non-object intermediates are
        /// replaced). Replacing an existing key keeps its position. The root itself cannot be set.
        /// </summary>
        public static void Set(Dictionary<string, object> root, string[] segments, object value)
        {
            if (root == null) throw new ArgumentNullException("root");
            if (segments == null || segments.Length == 0) throw new ArgumentException("cannot set the root of a document", "segments");
            Dictionary<string, object> current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                Dictionary<string, object> child = MaterializeChild(current, segments[i]);
                if (child == null)
                {
                    child = NewObject();
                    current[segments[i]] = child;
                }
                current = child;
            }
            current[segments[segments.Length - 1]] = value;
        }

        /// <summary>Removes the value at a path, keeping the order of the remaining keys. False if it was absent.</summary>
        public static bool Remove(Dictionary<string, object> root, string[] segments)
        {
            if (root == null) throw new ArgumentNullException("root");
            if (segments == null || segments.Length == 0) throw new ArgumentException("cannot remove the root of a document", "segments");
            if (!Exists(root, segments)) return false;
            Dictionary<string, object> current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                current = MaterializeChild(current, segments[i]);
                if (current == null) return false; // the path went through an array: not removable
            }
            return JsonPath.RemoveKey(current, segments[segments.Length - 1]);
        }

        /// <summary>
        /// Recursive merge (verify-C3 §7.4): when both sides hold an object the merge recurses, otherwise the patch
        /// value (array, scalar, null or verbatim node) replaces the target value. A verbatim object on the target side
        /// is materialized first. Patch values are copied, so the target never aliases the patch.
        /// </summary>
        public static void DeepMerge(Dictionary<string, object> target, Dictionary<string, object> patch)
        {
            if (target == null) throw new ArgumentNullException("target");
            if (patch == null) return;
            foreach (var kv in patch)
            {
                var patchObj = kv.Value as Dictionary<string, object>;
                if (patchObj != null)
                {
                    object existing;
                    if (target.TryGetValue(kv.Key, out existing) && IsObjectNode(existing))
                    {
                        DeepMerge(MaterializeChild(target, kv.Key), patchObj);
                        continue;
                    }
                }
                target[kv.Key] = Clone(kv.Value);
            }
        }

        /// <summary>
        /// Returns <paramref name="parent"/>[<paramref name="key"/>] as a mutable object: a plain object as is, a
        /// verbatim JSON object replaced in the parent by a deep copy of its value. Null when absent or not an object.
        /// </summary>
        public static Dictionary<string, object> MaterializeChild(Dictionary<string, object> parent, string key)
        {
            object node;
            if (!parent.TryGetValue(key, out node)) return null;
            var obj = node as Dictionary<string, object>;
            if (obj != null) return obj;
            var verbatim = node as VerbatimJson;
            if (verbatim != null && verbatim.IsObject)
            {
                var copy = (Dictionary<string, object>)verbatim.ParseCopy();
                parent[key] = copy;
                return copy;
            }
            return null;
        }

        // ------------------------------------------------------------------ projection

        /// <summary>
        /// include_fields: a new object with every requested path that exists, re-nested
        /// (["_mcvs.matchers","level"] → {"_mcvs":{"matchers":...},"level":...}). Never flat "a.b" keys: the game
        /// reads the result with SelectToken (verify-C3 §3.2). Missing or malformed paths are left out. Verbatim
        /// nodes are shared (immutable), other values copied.
        /// </summary>
        public static Dictionary<string, object> Project(object source, IEnumerable<string> paths)
        {
            var result = NewObject();
            if (source == null || paths == null) return result;
            foreach (string raw in paths)
            {
                if (raw == null) continue;
                string path = raw.Trim();
                string[] segments;
                if (path.Length == 0 || !TryParsePath(path, out segments)) continue;
                object node;
                if (!TryGet(source, segments, out node)) continue;
                if (segments.Length > 1 && Exists(result, segments)) continue; // already covered by a parent path
                Set(result, segments, Clone(node));
            }
            return result;
        }

        // ------------------------------------------------------------------ copies and comparison

        /// <summary>Deep copy of the containers; verbatim nodes and scalars are immutable and shared.</summary>
        public static object Clone(object value)
        {
            var obj = value as Dictionary<string, object>;
            if (obj != null) return CloneObject(obj);
            var arr = value as List<object>;
            if (arr != null)
            {
                var copy = new List<object>(arr.Count);
                foreach (var item in arr) copy.Add(Clone(item));
                return copy;
            }
            return value;
        }

        public static Dictionary<string, object> CloneObject(Dictionary<string, object> obj)
        {
            if (obj == null) return null;
            var copy = new Dictionary<string, object>(obj.Count, StringComparer.Ordinal);
            foreach (var kv in obj) copy.Add(kv.Key, Clone(kv.Value));
            return copy;
        }

        /// <summary>A plain MiniJson value (no verbatim nodes), deep-copied: safe to hand to other modules.</summary>
        public static object ToPlain(object value)
        {
            var verbatim = value as VerbatimJson;
            if (verbatim != null) return verbatim.ParseCopy();
            var obj = value as Dictionary<string, object>;
            if (obj != null)
            {
                var copy = new Dictionary<string, object>(obj.Count, StringComparer.Ordinal);
                foreach (var kv in obj) copy.Add(kv.Key, ToPlain(kv.Value));
                return copy;
            }
            var arr = value as List<object>;
            if (arr != null)
            {
                var copy = new List<object>(arr.Count);
                foreach (var item in arr) copy.Add(ToPlain(item));
                return copy;
            }
            return value;
        }

        /// <summary>Structural equality, looking through verbatim nodes.</summary>
        public static bool DeepEquals(object a, object b)
        {
            a = Unwrap(a);
            b = Unwrap(b);
            var oa = a as Dictionary<string, object>;
            var ob = b as Dictionary<string, object>;
            if (oa != null || ob != null)
            {
                if (oa == null || ob == null || oa.Count != ob.Count) return false;
                foreach (var kv in oa)
                {
                    object other;
                    if (!ob.TryGetValue(kv.Key, out other) || !DeepEquals(kv.Value, other)) return false;
                }
                return true;
            }
            var la = a as List<object>;
            var lb = b as List<object>;
            if (la != null || lb != null)
            {
                if (la == null || lb == null || la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++)
                {
                    if (!DeepEquals(la[i], lb[i])) return false;
                }
                return true;
            }
            return JsonValue.DeepEquals(a, b);
        }

        /// <summary>True for an absent value, JSON null, or an empty object/array (verbatim values are not parsed).</summary>
        public static bool IsEmpty(object node)
        {
            var verbatim = node as VerbatimJson;
            if (verbatim != null) return verbatim.IsEmptyJson;
            object v = node;
            if (v == null) return true;
            var obj = v as Dictionary<string, object>;
            if (obj != null) return obj.Count == 0;
            var arr = v as List<object>;
            return arr != null && arr.Count == 0;
        }

        /// <summary>
        /// Every verbatim node reachable through object keys, with its path (keys may themselves contain dots, so
        /// paths are kept as segment arrays). Verbatim nodes never nest: writing inside one materializes it.
        /// </summary>
        public static List<KeyValuePair<string[], VerbatimJson>> CollectVerbatim(Dictionary<string, object> root)
        {
            var result = new List<KeyValuePair<string[], VerbatimJson>>();
            if (root != null) Collect(root, new List<string>(), result);
            return result;
        }

        private static void Collect(Dictionary<string, object> obj, List<string> prefix, List<KeyValuePair<string[], VerbatimJson>> result)
        {
            foreach (var kv in obj)
            {
                var verbatim = kv.Value as VerbatimJson;
                if (verbatim != null)
                {
                    var path = new List<string>(prefix);
                    path.Add(kv.Key);
                    result.Add(new KeyValuePair<string[], VerbatimJson>(path.ToArray(), verbatim));
                    continue;
                }
                var child = kv.Value as Dictionary<string, object>;
                if (child != null)
                {
                    prefix.Add(kv.Key);
                    Collect(child, prefix, result);
                    prefix.RemoveAt(prefix.Count - 1);
                }
            }
        }

        public static Dictionary<string, object> NewObject()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        // ------------------------------------------------------------------ serialization

        /// <summary>Serializes a node (compact unless <paramref name="indented"/>), embedding verbatim text unchanged.</summary>
        public static string Serialize(object value, bool indented = false)
        {
            var sb = new StringBuilder(256);
            Write(sb, value, indented, 0);
            return sb.ToString();
        }

        /// <summary>
        /// The body a selector GET answers for a node: the exact stored text for a verbatim node (also opaque text),
        /// otherwise the compact serialization.
        /// </summary>
        public static string ResponseText(object node)
        {
            var verbatim = node as VerbatimJson;
            return verbatim != null ? verbatim.Text : Serialize(node);
        }

        /// <summary>Appends a node. Indentation (two spaces) matches <see cref="MiniJson.Serialize(object, bool)"/>.</summary>
        public static void Write(StringBuilder sb, object value, bool indented, int depth)
        {
            if (depth > MiniJson.MaxDepth) throw new InvalidOperationException("JSON value nested deeper than " + MiniJson.MaxDepth);
            var verbatim = value as VerbatimJson;
            if (verbatim != null)
            {
                if (verbatim.IsJson) sb.Append(verbatim.Text);
                else sb.Append(MiniJson.Quote(verbatim.Text));
                return;
            }
            var obj = value as Dictionary<string, object>;
            if (obj != null)
            {
                if (obj.Count == 0)
                {
                    sb.Append("{}");
                    return;
                }
                sb.Append('{');
                bool first = true;
                foreach (var kv in obj)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indented, depth + 1);
                    sb.Append(MiniJson.Quote(kv.Key ?? ""));
                    sb.Append(indented ? ": " : ":");
                    Write(sb, kv.Value, indented, depth + 1);
                }
                NewLine(sb, indented, depth);
                sb.Append('}');
                return;
            }
            var arr = value as List<object>;
            if (arr != null)
            {
                if (arr.Count == 0)
                {
                    sb.Append("[]");
                    return;
                }
                sb.Append('[');
                for (int i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    NewLine(sb, indented, depth + 1);
                    Write(sb, arr[i], indented, depth + 1);
                }
                NewLine(sb, indented, depth);
                sb.Append(']');
                return;
            }
            MiniJson.Serialize(value, sb, false);
        }

        private static void NewLine(StringBuilder sb, bool indented, int depth)
        {
            if (!indented) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }
    }
}
