using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace McvsOffline.Server.Core.Json
{
    /// <summary>Thrown for malformed JSON text. <see cref="Position"/> is the character offset of the problem.</summary>
    public sealed class JsonParseException : FormatException
    {
        public JsonParseException(string message, int position)
            : base(message + " (at offset " + position.ToString(CultureInfo.InvariantCulture) + ")")
        {
            Position = position;
        }

        public int Position { get; private set; }
    }

    /// <summary>
    /// Minimal JSON reader/writer for shared code. Newtonsoft.Json cannot be used: the copy in the game is trimmed
    /// and the embedded build only references mscorlib/System/System.Core (ARCHITECTURE §2.1).
    /// <para>
    /// Value model produced by <see cref="Parse"/>:
    /// object → <c>Dictionary&lt;string, object&gt;</c> (ordinal keys, insertion order preserved: the game processes
    /// some objects in JSON order, verify-C5 (c)), array → <c>List&lt;object&gt;</c>, string → <c>string</c>,
    /// integer → <c>long</c>, other numbers → <c>double</c>, <c>true/false</c> → <c>bool</c>, <c>null</c> → null.
    /// Integers outside the long range (and doubles that would overflow) become <see cref="JsonNumber"/> so their
    /// digits are never lost.
    /// </para>
    /// <para>
    /// <see cref="Serialize(object)"/> is deterministic: the same value always produces the same text. Keys are
    /// written in dictionary order (not sorted), numbers with the invariant culture, doubles in shortest
    /// round-trip form and always with a fraction or exponent (so 1.0 reads back as a double), and non-ASCII
    /// characters as raw UTF-8. Removing a key from a Dictionary can reorder later inserts; use
    /// <see cref="JsonPath.RemoveKey"/> when order matters.
    /// </para>
    /// </summary>
    public static class MiniJson
    {
        /// <summary>Maximum nesting depth accepted by the parser and the serializer.</summary>
        public const int MaxDepth = 512;

        // ------------------------------------------------------------------ parsing

        /// <summary>Parses JSON text. Throws <see cref="JsonParseException"/> on malformed input.</summary>
        public static object Parse(string json)
        {
            if (json == null) throw new ArgumentNullException("json");
            return new Parser(json).ParseDocument();
        }

        /// <summary>Parses JSON text; returns false (and null) instead of throwing on malformed input.</summary>
        public static bool TryParse(string json, out object value)
        {
            value = null;
            if (json == null) return false;
            try
            {
                value = new Parser(json).ParseDocument();
                return true;
            }
            catch (JsonParseException)
            {
                return false;
            }
        }

        /// <summary>Parses JSON text that must be an object.</summary>
        public static Dictionary<string, object> ParseObject(string json)
        {
            var obj = Parse(json) as Dictionary<string, object>;
            if (obj == null) throw new JsonParseException("expected a JSON object", 0);
            return obj;
        }

        /// <summary>Parses JSON text that must be an array.</summary>
        public static List<object> ParseArray(string json)
        {
            var arr = Parse(json) as List<object>;
            if (arr == null) throw new JsonParseException("expected a JSON array", 0);
            return arr;
        }

        // ------------------------------------------------------------------ serialization

        /// <summary>Serializes a value compactly (no whitespace).</summary>
        public static string Serialize(object value)
        {
            return Serialize(value, false);
        }

        /// <summary>Serializes a value; <paramref name="indented"/> uses two-space indentation.</summary>
        public static string Serialize(object value, bool indented)
        {
            var sb = new StringBuilder(256);
            new Writer(sb, indented).Write(value, 0);
            return sb.ToString();
        }

        /// <summary>Appends the serialized value to <paramref name="sb"/>.</summary>
        public static void Serialize(object value, StringBuilder sb, bool indented)
        {
            if (sb == null) throw new ArgumentNullException("sb");
            new Writer(sb, indented).Write(value, 0);
        }

        /// <summary>Returns <paramref name="s"/> as a quoted, escaped JSON string literal.</summary>
        public static string Quote(string s)
        {
            var sb = new StringBuilder((s == null ? 0 : s.Length) + 2);
            WriteString(sb, s);
            return sb.ToString();
        }

        /// <summary>Formats a double the way <see cref="Serialize(object)"/> does.</summary>
        public static string FormatDouble(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null"; // not representable in JSON
            string s = d.ToString("R", CultureInfo.InvariantCulture);
            double back;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out back) || back != d)
                s = d.ToString("G17", CultureInfo.InvariantCulture); // old runtimes: "R" is not always round-trip
            if (s.IndexOf('.') < 0 && s.IndexOf('E') < 0 && s.IndexOf('e') < 0) s += ".0";
            return s;
        }

        internal static void WriteString(StringBuilder sb, string s)
        {
            if (s == null)
            {
                sb.Append("null");
                return;
            }
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == '\u2028' || c == '\u2029')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Writer
        {
            private readonly StringBuilder sb;
            private readonly bool indented;

            public Writer(StringBuilder sb, bool indented)
            {
                this.sb = sb;
                this.indented = indented;
            }

            public void Write(object value, int depth)
            {
                if (depth > MaxDepth) throw new InvalidOperationException("JSON value nested deeper than " + MaxDepth);

                if (value == null) { sb.Append("null"); return; }

                string s = value as string;
                if (s != null) { WriteString(sb, s); return; }

                if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
                if (value is long) { sb.Append(((long)value).ToString(CultureInfo.InvariantCulture)); return; }
                if (value is int) { sb.Append(((int)value).ToString(CultureInfo.InvariantCulture)); return; }
                if (value is double) { sb.Append(FormatDouble((double)value)); return; }
                if (value is float) { sb.Append(FormatDouble((float)value)); return; }
                if (value is decimal) { sb.Append(((decimal)value).ToString(CultureInfo.InvariantCulture)); return; }
                if (value is short || value is byte || value is sbyte || value is ushort || value is uint)
                {
                    sb.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                    return;
                }
                if (value is ulong) { sb.Append(((ulong)value).ToString(CultureInfo.InvariantCulture)); return; }
                if (value is char) { WriteString(sb, new string((char)value, 1)); return; }

                var number = value as JsonNumber;
                if (number != null) { sb.Append(number.Literal); return; }

                var genericDict = value as IDictionary<string, object>;
                if (genericDict != null) { WriteObject(genericDict, depth); return; }

                var dict = value as IDictionary;
                if (dict != null) { WriteLegacyObject(dict, depth); return; }

                var enumerable = value as IEnumerable;
                if (enumerable != null) { WriteArray(enumerable, depth); return; }

                throw new ArgumentException("MiniJson cannot serialize values of type " + value.GetType().FullName);
            }

            private void WriteObject(IDictionary<string, object> dict, int depth)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(depth + 1);
                    WriteString(sb, kv.Key ?? "");
                    sb.Append(indented ? ": " : ":");
                    Write(kv.Value, depth + 1);
                }
                NewLine(depth);
                sb.Append('}');
            }

            private void WriteLegacyObject(IDictionary dict, int depth)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(depth + 1);
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "");
                    sb.Append(indented ? ": " : ":");
                    Write(entry.Value, depth + 1);
                }
                NewLine(depth);
                sb.Append('}');
            }

            private void WriteArray(IEnumerable items, int depth)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in items)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(depth + 1);
                    Write(item, depth + 1);
                }
                if (!first) NewLine(depth);
                sb.Append(']');
            }

            private void NewLine(int depth)
            {
                if (!indented) return;
                sb.Append('\n');
                sb.Append(' ', depth * 2);
            }
        }

        private sealed class Parser
        {
            private readonly string s;
            private int pos;

            public Parser(string text)
            {
                s = text;
                pos = 0;
            }

            public object ParseDocument()
            {
                if (s.Length > 0 && s[0] == '\uFEFF') pos = 1; // tolerate a UTF-8 BOM decoded as text
                SkipWhitespace();
                if (pos >= s.Length) throw Error("empty JSON document");
                object value = ParseValue(0);
                SkipWhitespace();
                if (pos < s.Length) throw Error("unexpected trailing characters");
                return value;
            }

            private object ParseValue(int depth)
            {
                if (depth > MaxDepth) throw Error("JSON nested deeper than " + MaxDepth);
                SkipWhitespace();
                if (pos >= s.Length) throw Error("unexpected end of JSON");
                char c = s[pos];
                switch (c)
                {
                    case '{': return ParseObject(depth);
                    case '[': return ParseArray(depth);
                    case '"': return ParseString();
                    case 't': ExpectWord("true"); return true;
                    case 'f': ExpectWord("false"); return false;
                    case 'n': ExpectWord("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                        throw Error("unexpected character '" + c + "'");
                }
            }

            private Dictionary<string, object> ParseObject(int depth)
            {
                pos++; // '{'
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                SkipWhitespace();
                if (Peek() == '}') { pos++; return result; }
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"') throw Error("expected a property name");
                    string key = ParseString();
                    SkipWhitespace();
                    if (Peek() != ':') throw Error("expected ':' after property name");
                    pos++;
                    object value = ParseValue(depth + 1);
                    result[key] = value; // duplicate keys: last one wins (like Newtonsoft's JObject.Parse)
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',') { pos++; continue; }
                    if (c == '}') { pos++; return result; }
                    throw Error("expected ',' or '}' in object");
                }
            }

            private List<object> ParseArray(int depth)
            {
                pos++; // '['
                var result = new List<object>();
                SkipWhitespace();
                if (Peek() == ']') { pos++; return result; }
                while (true)
                {
                    result.Add(ParseValue(depth + 1));
                    SkipWhitespace();
                    char c = Peek();
                    if (c == ',') { pos++; continue; }
                    if (c == ']') { pos++; return result; }
                    throw Error("expected ',' or ']' in array");
                }
            }

            private string ParseString()
            {
                int start = pos;
                pos++; // opening quote
                StringBuilder sb = null;
                int runStart = pos;
                while (true)
                {
                    if (pos >= s.Length) { pos = start; throw Error("unterminated string"); }
                    char c = s[pos];
                    if (c == '"')
                    {
                        string tail = s.Substring(runStart, pos - runStart);
                        pos++;
                        if (sb == null) return tail;
                        sb.Append(tail);
                        return sb.ToString();
                    }
                    if (c != '\\')
                    {
                        // Raw control characters are invalid JSON but harmless; accept them (lenient, like Newtonsoft).
                        pos++;
                        continue;
                    }
                    if (sb == null) sb = new StringBuilder();
                    sb.Append(s, runStart, pos - runStart);
                    pos++; // backslash
                    if (pos >= s.Length) throw Error("unterminated escape sequence");
                    char e = s[pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (pos + 4 > s.Length) throw Error("truncated \\u escape");
                            int code = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                int h = HexValue(s[pos + i]);
                                if (h < 0) throw Error("invalid \\u escape");
                                code = (code << 4) | h;
                            }
                            pos += 4;
                            sb.Append((char)code); // surrogate pairs arrive as two escapes and recombine naturally
                            break;
                        default:
                            throw Error("invalid escape '\\" + e + "'");
                    }
                    runStart = pos;
                }
            }

            private object ParseNumber()
            {
                int start = pos;
                bool isInteger = true;
                if (Peek() == '-') pos++;
                if (pos >= s.Length) throw Error("invalid number");
                if (s[pos] == '0')
                {
                    pos++;
                }
                else if (s[pos] >= '1' && s[pos] <= '9')
                {
                    while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
                }
                else
                {
                    throw Error("invalid number");
                }
                if (pos < s.Length && s[pos] == '.')
                {
                    isInteger = false;
                    pos++;
                    if (pos >= s.Length || s[pos] < '0' || s[pos] > '9') throw Error("invalid number fraction");
                    while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
                }
                if (pos < s.Length && (s[pos] == 'e' || s[pos] == 'E'))
                {
                    isInteger = false;
                    pos++;
                    if (pos < s.Length && (s[pos] == '+' || s[pos] == '-')) pos++;
                    if (pos >= s.Length || s[pos] < '0' || s[pos] > '9') throw Error("invalid number exponent");
                    while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
                }
                string literal = s.Substring(start, pos - start);
                if (isInteger)
                {
                    long l;
                    if (long.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
                    return new JsonNumber(literal); // beyond long: keep every digit
                }
                double d;
                if (double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out d)
                    && !double.IsInfinity(d))
                {
                    return d;
                }
                return new JsonNumber(literal);
            }

            private void ExpectWord(string word)
            {
                if (string.CompareOrdinal(s, pos, word, 0, word.Length) != 0) throw Error("invalid literal");
                pos += word.Length;
            }

            private char Peek()
            {
                return pos < s.Length ? s[pos] : '\0';
            }

            private void SkipWhitespace()
            {
                while (pos < s.Length)
                {
                    char c = s[pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') pos++;
                    else break;
                }
            }

            private static int HexValue(char c)
            {
                if (c >= '0' && c <= '9') return c - '0';
                if (c >= 'a' && c <= 'f') return c - 'a' + 10;
                if (c >= 'A' && c <= 'F') return c - 'A' + 10;
                return -1;
            }

            private JsonParseException Error(string message)
            {
                return new JsonParseException(message, pos);
            }
        }
    }
}
