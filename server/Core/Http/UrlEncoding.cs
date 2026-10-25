using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>
    /// Percent-encoding helpers (own implementation: behaviour must be identical in both builds).
    /// The client encodes with Uri.EscapeDataString (spaces as %20, ':' as %3A; report 02 §1.4), but '+' is
    /// still decoded as a space in queries and forms, as every form decoder does (a literal '+' arrives as %2B).
    /// </summary>
    public static class UrlEncoding
    {
        /// <summary>
        /// Decodes %XX sequences (as UTF-8). Invalid or truncated escapes are kept literally, never rejected.
        /// <paramref name="plusIsSpace"/> is true for query/form values and false for path segments.
        /// </summary>
        public static string Decode(string s, bool plusIsSpace)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (s.IndexOf('%') < 0 && (!plusIsSpace || s.IndexOf('+') < 0)) return s;

            var sb = new StringBuilder(s.Length);
            byte[] pending = null;
            int pendingCount = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '%' && i + 2 < s.Length && IsHex(s[i + 1]) && IsHex(s[i + 2]))
                {
                    if (pending == null) pending = new byte[s.Length / 3 + 1];
                    pending[pendingCount++] = (byte)((HexValue(s[i + 1]) << 4) | HexValue(s[i + 2]));
                    i += 2;
                    continue;
                }
                if (pendingCount > 0)
                {
                    sb.Append(Encoding.UTF8.GetString(pending, 0, pendingCount));
                    pendingCount = 0;
                }
                sb.Append(plusIsSpace && c == '+' ? ' ' : c);
            }
            if (pendingCount > 0) sb.Append(Encoding.UTF8.GetString(pending, 0, pendingCount));
            return sb.ToString();
        }

        /// <summary>Like Uri.EscapeDataString: everything except RFC 3986 unreserved characters is %XX-encoded (UTF-8).</summary>
        public static string Encode(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var sb = new StringBuilder(s.Length + 8);
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            foreach (byte b in bytes)
            {
                char c = (char)b;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                    || c == '-' || c == '_' || c == '.' || c == '~')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('%').Append(((int)b).ToString("X2", CultureInfo.InvariantCulture));
                }
            }
            return sb.ToString();
        }

        /// <summary>Encodes pairs as application/x-www-form-urlencoded (the way the game's client does).</summary>
        public static string EncodeForm(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            var sb = new StringBuilder();
            foreach (var kv in pairs)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Encode(kv.Key)).Append('=').Append(Encode(kv.Value));
            }
            return sb.ToString();
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            return c - 'A' + 10;
        }
    }
}
