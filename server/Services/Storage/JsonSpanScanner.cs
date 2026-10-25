using System;
using System.Collections.Generic;
using System.Text;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>
    /// Finds the exact character span of a value inside a JSON text, following a path of object keys, without
    /// building values. The profile file keeps verbatim values (the game-profile dump) inline as real JSON so the
    /// file stays readable and editable; on load this scanner recovers their original text. Callers validate the
    /// whole text with MiniJson first and compare the span's parsed value with the parsed tree (see
    /// <see cref="ProfileFile"/>), so a scanner miss can only cost verbatim-ness, never data.
    /// <para>
    /// Duplicate keys resolve to the last occurrence, like <c>MiniJson.Parse</c>.
    /// </para>
    /// </summary>
    public static class JsonSpanScanner
    {
        /// <summary>True and the span of the value at <paramref name="path"/> (object keys from the root).</summary>
        public static bool TryFindValue(string text, IList<string> path, out int start, out int length)
        {
            start = 0;
            length = 0;
            if (text == null || path == null) return false;
            int pos = 0;
            if (text.Length > 0 && text[0] == '﻿') pos = 1;
            pos = SkipWhitespace(text, pos);
            foreach (string key in path)
            {
                pos = FindMember(text, pos, key);
                if (pos < 0) return false;
            }
            int end = SkipValue(text, pos);
            if (end < 0) return false;
            start = pos;
            length = end - pos;
            return true;
        }

        /// <summary>
        /// <paramref name="pos"/> is at an object; returns the start of the value of the last member named
        /// <paramref name="key"/>, or -1.
        /// </summary>
        private static int FindMember(string s, int pos, string key)
        {
            if (pos >= s.Length || s[pos] != '{') return -1;
            pos = SkipWhitespace(s, pos + 1);
            if (pos < s.Length && s[pos] == '}') return -1;
            int found = -1;
            while (true)
            {
                if (pos >= s.Length || s[pos] != '"') return -1;
                string name;
                pos = ReadString(s, pos, out name);
                if (pos < 0) return -1;
                pos = SkipWhitespace(s, pos);
                if (pos >= s.Length || s[pos] != ':') return -1;
                pos = SkipWhitespace(s, pos + 1);
                if (string.Equals(name, key, StringComparison.Ordinal)) found = pos;
                pos = SkipValue(s, pos);
                if (pos < 0) return -1;
                pos = SkipWhitespace(s, pos);
                if (pos >= s.Length) return -1;
                if (s[pos] == ',')
                {
                    pos = SkipWhitespace(s, pos + 1);
                    continue;
                }
                if (s[pos] == '}') return found;
                return -1;
            }
        }

        /// <summary>Returns the index just after the value starting at <paramref name="pos"/>, or -1.</summary>
        private static int SkipValue(string s, int pos)
        {
            if (pos >= s.Length) return -1;
            char c = s[pos];
            if (c == '"')
            {
                string ignored;
                return ReadString(s, pos, out ignored, false);
            }
            if (c == '{' || c == '[')
            {
                int depth = 0;
                while (pos < s.Length)
                {
                    char d = s[pos];
                    if (d == '"')
                    {
                        string ignored;
                        pos = ReadString(s, pos, out ignored, false);
                        if (pos < 0) return -1;
                        continue;
                    }
                    if (d == '{' || d == '[') depth++;
                    else if (d == '}' || d == ']')
                    {
                        depth--;
                        if (depth == 0) return pos + 1;
                    }
                    pos++;
                }
                return -1;
            }
            // number, true, false, null
            int start = pos;
            while (pos < s.Length)
            {
                char d = s[pos];
                if (d == ',' || d == '}' || d == ']' || d == ' ' || d == '\t' || d == '\r' || d == '\n') break;
                pos++;
            }
            return pos > start ? pos : -1;
        }

        private static int ReadString(string s, int pos, out string value)
        {
            return ReadString(s, pos, out value, true);
        }

        /// <summary>
        /// Reads the string literal starting at <paramref name="pos"/> (a quote); returns the index after the closing
        /// quote, or -1. Decodes escapes only when <paramref name="decode"/> (keys); values are just skipped.
        /// </summary>
        private static int ReadString(string s, int pos, out string value, bool decode)
        {
            value = null;
            pos++; // opening quote
            StringBuilder sb = decode ? new StringBuilder() : null;
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == '"')
                {
                    if (sb != null) value = sb.ToString();
                    return pos + 1;
                }
                if (c != '\\')
                {
                    if (sb != null) sb.Append(c);
                    pos++;
                    continue;
                }
                if (pos + 1 >= s.Length) return -1;
                char e = s[pos + 1];
                if (e == 'u')
                {
                    if (pos + 6 > s.Length) return -1;
                    if (sb != null)
                    {
                        int code = 0;
                        for (int i = 0; i < 4; i++)
                        {
                            int h = HexValue(s[pos + 2 + i]);
                            if (h < 0) return -1;
                            code = (code << 4) | h;
                        }
                        sb.Append((char)code);
                    }
                    pos += 6;
                    continue;
                }
                if (sb != null)
                {
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
                        default: return -1;
                    }
                }
                pos += 2;
            }
            return -1;
        }

        private static int SkipWhitespace(string s, int pos)
        {
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') pos++;
                else break;
            }
            return pos;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
