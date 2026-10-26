using System;
using System.Text;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>
    /// The one redaction function for everything that ends up in a log, a crash file or a share (design §5.4, I2): the
    /// settings page's one-time tickets and session cookies, and password/token parameters.
    /// <list type="bullet">
    /// <item><c>#t=&lt;8+ hex&gt;</c> and its percent-encoded form <c>%23t%3D&lt;hex&gt;</c> → <c>#t=&lt;redacted&gt;</c>;</item>
    /// <item><c>mcvs_s=&lt;hex&gt;</c> → <c>mcvs_s=&lt;redacted&gt;</c>;</item>
    /// <item>the string value of a JSON member <c>"ticket"</c> (any spacing) → <c>&lt;redacted&gt;</c>;</item>
    /// <item>a form/query parameter <c>t=&lt;32 hex&gt;</c> → <c>t=&lt;redacted&gt;</c>;</item>
    /// <item>the value of every form/query parameter (<c>name=value</c>, also <c>name = value</c> as the request log
    /// prints forms) and JSON string member named <c>password</c>, <c>access_token</c>, <c>refresh_token</c>,
    /// <c>token</c> or <c>superPassword</c> (any letter case) → its first 4 characters + <c>…&lt;redacted&gt;</c>.</item>
    /// </list>
    /// Redacting twice gives the same text as redacting once (log files are redacted when written and again when read).
    /// Fast path: when none of the trigger substrings occurs (ordinal search) the input instance is returned unchanged;
    /// otherwise one linear scan, no regular expressions (the game's trimmed System.dll). Never throws.
    /// </summary>
    public static class LogRedaction
    {
        public const string Marker = "<redacted>";

        /// <summary>Appended to the kept first characters of a password or token.</summary>
        public const string PartialMarker = "…" + Marker;

        private const int KeptCharacters = 4;

        /// <summary>Parameter and JSON member names whose values are secrets (compared ignoring ASCII case).</summary>
        private static readonly string[] SecretNames = { "password", "access_token", "refresh_token", "token", "superPassword" };

        /// <summary>Substrings without which nothing can match (ordinal, ignoring case).</summary>
        private static readonly string[] Triggers = { "t=", "%23", "mcvs_s", "ticket", "password", "token" };

        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text) || !MayContainSecret(text)) return text;
            try
            {
                return Scan(text);
            }
            catch (Exception)
            {
                // A bug here must never cost a log line; the scanner only reads inside the string, so this is a safety net.
                return text;
            }
        }

        private static bool MayContainSecret(string text)
        {
            foreach (string trigger in Triggers)
            {
                if (text.IndexOf(trigger, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static string Scan(string text)
        {
            StringBuilder sb = null;
            int copied = 0; // text[copied..i) is not yet in sb
            int i = 0;
            while (i < text.Length)
            {
                string replacement;
                int end;
                if (TryMatch(text, i, out replacement, out end))
                {
                    if (sb == null) sb = new StringBuilder(text.Length + 16);
                    sb.Append(text, copied, i - copied).Append(replacement);
                    i = end;
                    copied = end;
                }
                else
                {
                    i++;
                }
            }
            if (sb == null) return text;
            sb.Append(text, copied, text.Length - copied);
            return sb.ToString();
        }

        /// <summary>Tries every rule at <paramref name="i"/>; on a match, the text that replaces text[i..end).</summary>
        private static bool TryMatch(string text, int i, out string replacement, out int end)
        {
            replacement = null;
            end = i;
            char c = text[i];
            if (c == '#')
            {
                // #t=<8+ hex>
                if (At(text, i, "#t=") && HexRun(text, i + 3) >= 8)
                {
                    replacement = "#t=" + Marker;
                    end = i + 3 + HexRun(text, i + 3);
                    return true;
                }
                return false;
            }
            if (c == '%')
            {
                // %23t%3D<8+ hex>
                if (AtIgnoreCase(text, i, "%23t%3d") && HexRun(text, i + 7) >= 8)
                {
                    replacement = "#t=" + Marker;
                    end = i + 7 + HexRun(text, i + 7);
                    return true;
                }
                return false;
            }
            if (c == '"') return TryJsonMember(text, i, out replacement, out end);
            if (!IsBoundary(text, i)) return false;
            if (c == 'm' || c == 'M')
            {
                // mcvs_s=<hex>
                if (AtIgnoreCase(text, i, "mcvs_s=") && HexRun(text, i + 7) > 0)
                {
                    replacement = "mcvs_s=" + Marker;
                    end = i + 7 + HexRun(text, i + 7);
                    return true;
                }
                return false;
            }
            if ((c == 't' || c == 'T') && i + 1 < text.Length && text[i + 1] == '=')
            {
                // t=<exactly 32 hex>
                int run = HexRun(text, i + 2);
                if (run == 32)
                {
                    replacement = "t=" + Marker;
                    end = i + 2 + run;
                    return true;
                }
                return false;
            }
            return TryParameter(text, i, out replacement, out end);
        }

        /// <summary><c>name=value</c> or <c>name = value</c> for a secret name; the value ends at '&amp;', whitespace, a quote, ',' or ';'.</summary>
        private static bool TryParameter(string text, int i, out string replacement, out int end)
        {
            replacement = null;
            end = i;
            string name = SecretNameAt(text, i);
            if (name == null) return false;
            int p = i + name.Length;
            if (p < text.Length && IsWordChar(text[p])) return false; // "tokens=", "password2="
            int q = SkipSpaces(text, p);
            if (q >= text.Length || text[q] != '=') return false;
            int valueStart = SkipSpaces(text, q + 1);
            int valueEnd = valueStart;
            while (valueEnd < text.Length && !EndsParameterValue(text[valueEnd])) valueEnd++;
            if (valueEnd == valueStart) return false;
            string value = text.Substring(valueStart, valueEnd - valueStart);
            if (value.EndsWith(PartialMarker, StringComparison.Ordinal)) return false; // already redacted
            replacement = text.Substring(i, valueStart - i) + Partial(value);
            end = valueEnd;
            return true;
        }

        /// <summary><c>"name" : "value"</c> for "ticket" (whole value) or a secret name (first characters kept).</summary>
        private static bool TryJsonMember(string text, int i, out string replacement, out int end)
        {
            replacement = null;
            end = i;
            bool ticket = AtIgnoreCase(text, i + 1, "ticket\"");
            string name = ticket ? "ticket" : SecretNameAt(text, i + 1);
            if (name == null) return false;
            int p = i + 1 + name.Length;
            if (p >= text.Length || text[p] != '"') return false;
            int q = SkipSpaces(text, p + 1);
            if (q >= text.Length || text[q] != ':') return false;
            int open = SkipSpaces(text, q + 1);
            if (open >= text.Length || text[open] != '"') return false;
            int close = open + 1;
            while (close < text.Length && text[close] != '"')
            {
                if (text[close] == '\\') close++;
                close++;
            }
            if (close >= text.Length) close = text.Length; // unterminated: redact to the end
            string value = text.Substring(open + 1, Math.Min(close, text.Length) - open - 1);
            if (value.Length == 0 || value == Marker || value.EndsWith(PartialMarker, StringComparison.Ordinal)) return false;
            replacement = text.Substring(i, open + 1 - i) + (ticket ? Marker : Partial(value));
            end = open + 1 + value.Length;
            return true;
        }

        private static string Partial(string value)
        {
            return (value.Length <= KeptCharacters ? value : value.Substring(0, KeptCharacters)) + PartialMarker;
        }

        private static string SecretNameAt(string text, int i)
        {
            foreach (string name in SecretNames)
            {
                if (AtIgnoreCase(text, i, name)) return name;
            }
            return null;
        }

        private static bool EndsParameterValue(char c)
        {
            // Not '<': "mcvs…<redacted>" is one value, so redacting twice (log file, then the tail view) changes nothing.
            return c == '&' || c == '"' || c == '\'' || c == ',' || c == ';' || char.IsWhiteSpace(c);
        }

        /// <summary>True when the character before <paramref name="i"/> cannot be part of a name ("xtoken=" is not "token=").</summary>
        private static bool IsBoundary(string text, int i)
        {
            if (i == 0) return true;
            char prev = text[i - 1];
            return !IsWordChar(prev) && prev != '%' && prev != '.' && prev != '-';
        }

        private static bool IsWordChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
        }

        private static int SkipSpaces(string text, int i)
        {
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            return i;
        }

        private static int HexRun(string text, int start)
        {
            int i = start;
            while (i < text.Length && IsHex(text[i])) i++;
            return i - start;
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private static bool At(string text, int i, string literal)
        {
            return i + literal.Length <= text.Length && string.CompareOrdinal(text, i, literal, 0, literal.Length) == 0;
        }

        private static bool AtIgnoreCase(string text, int i, string literal)
        {
            if (i < 0 || i + literal.Length > text.Length) return false;
            for (int k = 0; k < literal.Length; k++)
            {
                if (ToLowerAscii(text[i + k]) != ToLowerAscii(literal[k])) return false;
            }
            return true;
        }

        private static char ToLowerAscii(char c)
        {
            return c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
        }
    }
}
