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
    /// <c>token</c> or <c>superPassword</c> (any letter case) → its first 4 characters + <c>…&lt;redacted&gt;</c>;</item>
    /// <item>a MAC-shaped value (six pairs of hex digits separated by <c>:</c>, <c>-</c> or <c>%3A</c>) of a form/query
    /// parameter or JSON string member named <c>device_id</c> or <c>global_device_id</c> → <c>&lt;redacted-mac&gt;</c>
    /// (the PC client's connectivity tracker sends the machine's MAC address there; our own <c>mcvs…</c> ids and every
    /// other value stay, docs/design/pc-port.md §4.2);</item>
    /// <item>a SteamID64 (exactly 17 digits starting with <c>7656119</c>, no letter or digit around it) → its first and
    /// last 4 digits, <c>7656…8514</c> (the PC client's anonymous user name);</item>
    /// <item>an access token of this server anywhere in a line (<c>mcvs</c> + 8 hex + <c>.</c> + 32 hex, the shape
    /// <c>TokenService</c> issues; the game's <c>Session.GetAccessToken</c> prints it in game.log as
    /// <c>Get access token for scopes [...] succeeded: mcvs59915ef0.e681…</c>) → <c>mcvs…&lt;redacted&gt;</c>, as the
    /// <c>access_token=</c> rule writes it.</item>
    /// </list>
    /// The JSON rules also match one level deeper, where a JSON text sits inside a JSON string and its quotes arrive as
    /// <c>\"</c> (unknown-endpoints.jsonl stores a request body that way).
    /// Redacting twice gives the same text as redacting once (log files are redacted when written and again when read).
    /// Fast path: when none of the trigger substrings occurs (ordinal search) the input instance is returned unchanged;
    /// otherwise one linear scan, no regular expressions (the game's trimmed System.dll). Never throws.
    /// </summary>
    public static class LogRedaction
    {
        public const string Marker = "<redacted>";

        /// <summary>Appended to the kept first characters of a password or token.</summary>
        public const string PartialMarker = "…" + Marker;

        /// <summary>Replaces a MAC address in a <c>device_id</c>/<c>global_device_id</c> value.</summary>
        public const string MacMarker = "<redacted-mac>";

        private const int KeptCharacters = 4;

        /// <summary>The first digits of every individual SteamID64 (universe 1, account type 1, instance 1).</summary>
        private const string SteamIdPrefix = "7656119";

        private const int SteamIdDigits = 17;

        /// <summary>Access tokens: "mcvs" + 8 hex (the issuer, ServerContext's instance id) + '.' + 32 hex.</summary>
        private const string TokenPrefix = "mcvs";

        private const int TokenIssuerHex = 8;

        private const int TokenSecretHex = 32;

        /// <summary>Parameter and JSON member names whose values are secrets (compared ignoring ASCII case).</summary>
        private static readonly string[] SecretNames = { "password", "access_token", "refresh_token", "token", "superPassword" };

        /// <summary>Parameter and JSON member names whose MAC-shaped values are redacted (longest first).</summary>
        private static readonly string[] DeviceIdNames = { "global_device_id", "device_id" };

        /// <summary>Substrings without which nothing can match (ordinal, ignoring case).</summary>
        private static readonly string[] Triggers = { "t=", "%23", "mcvs_s", "ticket", "password", "token", "device_id", SteamIdPrefix };

        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text) || (!MayContainSecret(text) && !ContainsAccessToken(text))) return text;
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

        /// <summary>
        /// An access token anywhere (ordinal: "mcvs" is in every path of McvsOffline, so it cannot be a trigger of the
        /// case-insensitive fast path; the token's prefix is always lower case).
        /// </summary>
        private static bool ContainsAccessToken(string text)
        {
            int at = text.IndexOf(TokenPrefix, StringComparison.Ordinal);
            while (at >= 0)
            {
                if (AccessTokenLength(text, at) > 0) return true;
                at = text.IndexOf(TokenPrefix, at + 1, StringComparison.Ordinal);
            }
            return false;
        }

        /// <summary>
        /// The length of an access token at <paramref name="i"/>: "mcvs", exactly 8 hex digits (the issuing server's id),
        /// '.', at least 32 hex digits (the whole hex run is taken), else 0.
        /// </summary>
        private static int AccessTokenLength(string text, int i)
        {
            if (!At(text, i, TokenPrefix)) return 0;
            int p = i + TokenPrefix.Length;
            if (HexRun(text, p) != TokenIssuerHex) return 0;
            p += TokenIssuerHex;
            if (p >= text.Length || text[p] != '.') return 0;
            int secret = HexRun(text, p + 1);
            return secret >= TokenSecretHex ? p + 1 + secret - i : 0;
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
            if (c == '"' || c == '\\')
                return QuoteAt(text, i) > 0 && (TryJsonMember(text, i, out replacement, out end) || TryJsonDeviceId(text, i, out replacement, out end));
            if (c == '7') return TrySteamId(text, i, out replacement, out end);
            if (c == 'm')
            {
                // An access token anywhere, also inside a word ("token:mcvs…", "Bearer_mcvs…").
                int token = AccessTokenLength(text, i);
                if (token > 0)
                {
                    replacement = TokenPrefix + PartialMarker;
                    end = i + token;
                    return true;
                }
            }
            if (!IsBoundary(text, i)) return false;
            if ((c == 'd' || c == 'D' || c == 'g' || c == 'G') && TryDeviceIdParameter(text, i, out replacement, out end)) return true;
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

        /// <summary>
        /// <c>"name" : "value"</c> for "ticket" (whole value) or a secret name (first characters kept); also with every
        /// quote written <c>\"</c>.
        /// </summary>
        private static bool TryJsonMember(string text, int i, out string replacement, out int end)
        {
            replacement = null;
            end = i;
            int quote = QuoteAt(text, i);
            int n = i + quote;
            bool ticket = AtIgnoreCase(text, n, "ticket") && QuoteAt(text, n + 6) == quote;
            string name = ticket ? "ticket" : SecretNameAt(text, n);
            if (name == null) return false;
            int start = JsonValueStart(text, n + name.Length, quote);
            if (start < 0) return false;
            int close = JsonStringEnd(text, start, quote == 2);
            string value = text.Substring(start, close - start);
            if (value.Length == 0 || value == Marker || value.EndsWith(PartialMarker, StringComparison.Ordinal)) return false;
            replacement = text.Substring(i, start - i) + (ticket ? Marker : Partial(value));
            end = close;
            return true;
        }

        /// <summary>The length of a JSON quote at <paramref name="i"/>: 1 for <c>"</c>, 2 for <c>\"</c>, else 0.</summary>
        private static int QuoteAt(string text, int i)
        {
            if (i < 0 || i >= text.Length) return 0;
            if (text[i] == '"') return 1;
            return text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '"' ? 2 : 0;
        }

        /// <summary>
        /// After a member name that ends at <paramref name="p"/>: its closing quote, ':' and the opening quote of a string
        /// value, all quotes of length <paramref name="quote"/>; the index of the value's first character, else -1.
        /// </summary>
        private static int JsonValueStart(string text, int p, int quote)
        {
            if (QuoteAt(text, p) != quote) return -1;
            int q = SkipSpaces(text, p + quote);
            if (q >= text.Length || text[q] != ':') return -1;
            int open = SkipSpaces(text, q + 1);
            return QuoteAt(text, open) == quote ? open + quote : -1;
        }

        /// <summary>
        /// The index of the quote that ends a JSON string value starting at <paramref name="start"/>, or the text's length
        /// when it is unterminated. <paramref name="escaped"/>: the value sits one level deeper (closed by <c>\"</c>), so
        /// each <c>\x</c> of the outer text is one character of the inner string, and an inner backslash escapes the inner
        /// character after it.
        /// </summary>
        private static int JsonStringEnd(string text, int start, bool escaped)
        {
            int p = start;
            while (p < text.Length)
            {
                char c = text[p];
                if (!escaped)
                {
                    if (c == '"') return p;
                    p += c == '\\' ? 2 : 1;
                    continue;
                }
                if (c != '\\')
                {
                    p++;
                    continue;
                }
                if (p + 1 < text.Length && text[p + 1] == '"') return p;
                bool innerBackslash = p + 1 < text.Length && text[p + 1] == '\\';
                p += 2;
                if (innerBackslash && p < text.Length) p += text[p] == '\\' ? 2 : 1; // the inner character it escapes
            }
            return text.Length;
        }

        /// <summary><c>device_id=&lt;MAC&gt;</c> or <c>device_id = &lt;MAC&gt;</c> (also <c>global_device_id</c>).</summary>
        private static bool TryDeviceIdParameter(string text, int i, out string replacement, out int end)
        {
            replacement = null;
            end = i;
            string name = NameAt(text, i, DeviceIdNames);
            if (name == null) return false;
            int p = i + name.Length;
            if (p < text.Length && IsWordChar(text[p])) return false;
            int q = SkipSpaces(text, p);
            if (q >= text.Length || text[q] != '=') return false;
            int valueStart = SkipSpaces(text, q + 1);
            int mac = MacLength(text, valueStart);
            if (mac == 0) return false;
            replacement = text.Substring(i, valueStart - i) + MacMarker;
            end = valueStart + mac;
            return true;
        }

        /// <summary><c>"device_id" : "&lt;MAC&gt;"</c> (also <c>global_device_id</c>, also with <c>\"</c> quotes), the whole string value.</summary>
        private static bool TryJsonDeviceId(string text, int i, out string replacement, out int end)
        {
            replacement = null;
            end = i;
            int quote = QuoteAt(text, i);
            string name = NameAt(text, i + quote, DeviceIdNames);
            if (name == null) return false;
            int start = JsonValueStart(text, i + quote + name.Length, quote);
            if (start < 0) return false;
            int mac = MacLength(text, start);
            if (mac == 0 || QuoteAt(text, start + mac) != quote) return false;
            replacement = text.Substring(i, start - i) + MacMarker;
            end = start + mac;
            return true;
        }

        /// <summary>
        /// The length of a MAC address at <paramref name="start"/> (six pairs of hex digits and one kind of separator:
        /// ':', '-' or "%3A" in any case) that is a whole value (no name character or separator follows), else 0.
        /// </summary>
        private static int MacLength(string text, int start)
        {
            int p = start;
            string separator = null;
            for (int pair = 0; pair < 6; pair++)
            {
                if (pair > 0)
                {
                    string here;
                    if (p < text.Length && (text[p] == ':' || text[p] == '-')) here = text[p] == ':' ? ":" : "-";
                    else if (AtIgnoreCase(text, p, "%3a")) here = "%3a";
                    else return 0;
                    if (separator == null) separator = here;
                    else if (separator != here) return 0;
                    p += here.Length;
                }
                if (p + 2 > text.Length || !IsHex(text[p]) || !IsHex(text[p + 1])) return 0;
                p += 2;
            }
            if (p < text.Length && (IsWordChar(text[p]) || text[p] == ':' || text[p] == '-' || text[p] == '%')) return 0;
            return p - start;
        }

        /// <summary>
        /// A SteamID64 at <paramref name="i"/>: exactly 17 digits starting with 7656119, neither preceded nor followed by
        /// a letter or digit (a percent escape such as <c>%3A</c> right before it counts as punctuation, as in a raw
        /// query <c>anonymous%3A7656…</c>) → its first and last 4 digits around "…".
        /// </summary>
        private static bool TrySteamId(string text, int i, out string replacement, out int end)
        {
            replacement = null;
            end = i;
            if (!At(text, i, SteamIdPrefix)) return false;
            if (i > 0 && IsLetterOrDigit(text[i - 1]) && !(i >= 3 && text[i - 3] == '%' && IsHex(text[i - 2]) && IsHex(text[i - 1])))
                return false;
            int run = i;
            while (run < text.Length && text[run] >= '0' && text[run] <= '9') run++;
            if (run - i != SteamIdDigits) return false;
            if (run < text.Length && IsLetterOrDigit(text[run])) return false;
            replacement = text.Substring(i, KeptCharacters) + "…" + text.Substring(run - KeptCharacters, KeptCharacters);
            end = run;
            return true;
        }

        private static bool IsLetterOrDigit(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
        }

        private static string NameAt(string text, int i, string[] names)
        {
            foreach (string name in names)
            {
                if (AtIgnoreCase(text, i, name)) return name;
            }
            return null;
        }

        private static string Partial(string value)
        {
            string kept = value.Length <= KeptCharacters ? value : value.Substring(0, KeptCharacters);
            // Never keep half of an escape: "ab\…" would turn the marker into an escape sequence of the JSON around it.
            int backslashes = 0;
            while (backslashes < kept.Length && kept[kept.Length - 1 - backslashes] == '\\') backslashes++;
            if (backslashes % 2 == 1) kept = kept.Substring(0, kept.Length - 1);
            return kept + PartialMarker;
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
