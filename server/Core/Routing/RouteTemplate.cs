using System;
using System.Collections.Generic;

namespace McvsOffline.Server.Core.Routing
{
    /// <summary>Values captured by a route template: decoded, plus the raw (still percent-encoded) form.</summary>
    public sealed class RouteValues
    {
        public static readonly RouteValues Empty = new RouteValues();

        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> raw = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Decoded value or null.</summary>
        public string Get(string name)
        {
            string v;
            return values.TryGetValue(name, out v) ? v : null;
        }

        public string this[string name] { get { return Get(name); } }

        /// <summary>The value exactly as it appeared in the path (e.g. "fed_id%3Aabc").</summary>
        public string GetRaw(string name)
        {
            string v;
            return raw.TryGetValue(name, out v) ? v : null;
        }

        public bool Contains(string name) { return values.ContainsKey(name); }

        public int Count { get { return values.Count; } }

        public IEnumerable<string> Names { get { return values.Keys; } }

        internal void Set(string name, string value, string rawValue)
        {
            values[name] = value;
            raw[name] = rawValue;
        }
    }

    /// <summary>
    /// A path template such as "profiles/{credential}/myprofile/{*selector}".
    /// <list type="bullet">
    /// <item>Literal segments match case-insensitively.</item>
    /// <item><c>{name}</c> matches exactly one (non-empty) segment.</item>
    /// <item><c>{*name}</c>, only as the last segment, matches zero or more segments (joined with '/').</item>
    /// </list>
    /// Leading, trailing and doubled slashes are irrelevant on both sides: "//config/x/" matches "config/x".
    /// </summary>
    public sealed class RouteTemplate
    {
        private readonly Segment[] segments;

        private RouteTemplate(string text, Segment[] segments, bool hasCatchAll)
        {
            Text = text;
            this.segments = segments;
            HasCatchAll = hasCatchAll;
        }

        public string Text { get; private set; }

        public bool HasCatchAll { get; private set; }

        /// <summary>Number of segments a match needs at least (all of them without a catch-all).</summary>
        public int FixedSegmentCount { get { return HasCatchAll ? segments.Length - 1 : segments.Length; } }

        public static RouteTemplate Parse(string template)
        {
            if (template == null) throw new ArgumentNullException("template");
            var list = new List<Segment>();
            bool catchAll = false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string piece in template.Split('/'))
            {
                if (piece.Length == 0) continue;
                if (catchAll) throw new ArgumentException("a catch-all {*name} must be the last segment: " + template, "template");
                if (piece[0] == '{')
                {
                    if (piece[piece.Length - 1] != '}' || piece.Length < 3)
                        throw new ArgumentException("malformed parameter '" + piece + "' in " + template, "template");
                    string name = piece.Substring(1, piece.Length - 2);
                    bool isCatchAll = name[0] == '*';
                    if (isCatchAll) name = name.Substring(1);
                    if (name.Length == 0 || name.IndexOfAny(new[] { '{', '}', '*' }) >= 0)
                        throw new ArgumentException("malformed parameter '" + piece + "' in " + template, "template");
                    if (!names.Add(name)) throw new ArgumentException("duplicate parameter '" + name + "' in " + template, "template");
                    list.Add(new Segment(isCatchAll ? SegmentKind.CatchAll : SegmentKind.Parameter, name));
                    catchAll = isCatchAll;
                }
                else
                {
                    if (piece.IndexOf('{') >= 0 || piece.IndexOf('}') >= 0)
                        throw new ArgumentException("parameters must span a whole segment: '" + piece + "' in " + template, "template");
                    list.Add(new Segment(SegmentKind.Literal, piece));
                }
            }
            return new RouteTemplate(template, list.ToArray(), catchAll);
        }

        /// <summary>Matches decoded segments (and their raw forms); on success fills a new RouteValues.</summary>
        public bool TryMatch(string[] decoded, string[] raw, out RouteValues values)
        {
            values = null;
            int fixedCount = FixedSegmentCount;
            if (decoded.Length < fixedCount) return false;
            if (!HasCatchAll && decoded.Length != fixedCount) return false;

            RouteValues result = null;
            for (int i = 0; i < fixedCount; i++)
            {
                Segment s = segments[i];
                if (s.Kind == SegmentKind.Literal)
                {
                    if (!string.Equals(s.Text, decoded[i], StringComparison.OrdinalIgnoreCase)) return false;
                }
                else
                {
                    if (result == null) result = new RouteValues();
                    result.Set(s.Text, decoded[i], raw[i]);
                }
            }
            if (HasCatchAll)
            {
                if (result == null) result = new RouteValues();
                int restCount = decoded.Length - fixedCount;
                var restDecoded = new string[restCount];
                var restRaw = new string[restCount];
                Array.Copy(decoded, fixedCount, restDecoded, 0, restCount);
                Array.Copy(raw, fixedCount, restRaw, 0, restCount);
                result.Set(segments[segments.Length - 1].Text, string.Join("/", restDecoded), string.Join("/", restRaw));
            }
            values = result ?? new RouteValues();
            return true;
        }

        public override string ToString() { return Text; }

        private enum SegmentKind
        {
            Literal,
            Parameter,
            CatchAll,
        }

        private struct Segment
        {
            public readonly SegmentKind Kind;
            public readonly string Text;

            public Segment(SegmentKind kind, string text)
            {
                Kind = kind;
                Text = text;
            }
        }
    }
}
