using System;
using System.Collections.Generic;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>
    /// Whether a request looks like one of the game's own Federation client (Gameloft.OnlinePlatform), for the few
    /// places that must not be driven by a web page (design docs/design/settings-page.md §5.1, §6.6, §9.5).
    /// <para>
    /// The game's requests carry only Host, Cache-Control, the form or query, and the User-Agent
    /// <c>[&lt;session agent&gt; ](&lt;client id&gt;; &lt;datacenter&gt;)</c> (OP/FedRequestMessage.cs:137-142;
    /// <c>Session.UserAgent</c> is never set, so on the wire it is just the comment). A browser adds <c>Origin</c>,
    /// <c>Referer</c> or <c>Sec-Fetch-*</c> (current Chromium, Firefox and Safari send <c>Sec-Fetch-Site</c> on every
    /// request, no-cors image loads included), and its User-Agent is its own: a page cannot set it on a no-cors request,
    /// and a CORS request with it needs a preflight no route here answers. The User-Agent check covers old browsers
    /// without Fetch Metadata and pages with <c>referrerpolicy="no-referrer"</c>; it compares against a client id of the
    /// game's form only, so a client id the caller picks to match its own User-Agent ("Linux" against
    /// "(Linux; Android 7.0; ...)") is refused.
    /// </para>
    /// A local app can still send all of this: nothing over loopback TCP tells apps apart (§5.1, §5.5).
    /// </summary>
    public static class GameRequest
    {
        /// <summary>The name of the first browser-only header of the request (Origin, Referer, Sec-Fetch-*), or null.</summary>
        public static string BrowserHeader(HttpRequest request)
        {
            if (request == null || request.Headers == null) return null;
            foreach (KeyValuePair<string, string> header in request.Headers)
            {
                string name = header.Key ?? "";
                if (string.Equals(name, "Origin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Referer", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase))
                    return name;
            }
            return null;
        }

        /// <summary>
        /// True for a client id of the game's form <c>&lt;digits&gt;:&lt;digits&gt;:&lt;version&gt;:&lt;platform&gt;:&lt;store&gt;</c>
        /// (<c>2620:66102:1.17.32:android:googleplay</c>): a version of digits and dots, platform and store of lower-case
        /// letters, digits and '_'.
        /// </summary>
        public static bool IsClientId(string clientId)
        {
            if (string.IsNullOrEmpty(clientId) || clientId.Length > 128) return false;
            string[] parts = clientId.Split(':');
            if (parts.Length != 5) return false;
            return IsDigits(parts[0]) && IsDigits(parts[1]) && IsVersion(parts[2]) && IsWord(parts[3]) && IsWord(parts[4]);
        }

        /// <summary>
        /// True when the User-Agent is exactly the game's for <paramref name="clientId"/>: <c>(&lt;client id&gt;;
        /// &lt;datacenter&gt;)</c> (the datacenter may be empty), optionally after one product token (the session agent), and no browser's
        /// (<c>Mozilla/</c>). The client id must have the game's form (<see cref="IsClientId"/>).
        /// </summary>
        public static bool HasUserAgent(HttpRequest request, string clientId)
        {
            if (!IsClientId(clientId) || request == null || request.Headers == null) return false;
            string userAgent = (request.Headers.Get("User-Agent") ?? "").Trim();
            if (userAgent.Length == 0 || userAgent.Length > 512 || userAgent.IndexOf("Mozilla/", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (userAgent[userAgent.Length - 1] != ')') return false;
            int open = userAgent.IndexOf('(');
            if (open < 0 || userAgent.IndexOf('(', open + 1) >= 0) return false;
            string agent = userAgent.Substring(0, open).Trim();
            if (agent.Length > 0 && !IsToken(agent)) return false;
            string comment = userAgent.Substring(open + 1, userAgent.Length - open - 2);
            string head = clientId + "; ";
            if (!comment.StartsWith(head, StringComparison.Ordinal)) return false;
            // Empty before the client knows its datacenter (Federation.ListDatacenters sends "(<client id>; )").
            string datacenter = comment.Substring(head.Length);
            return IsToken(datacenter) && datacenter.IndexOf(';') < 0;
        }

        /// <summary>Null when the request looks like the game's own for <paramref name="clientId"/>; else why not.</summary>
        public static string NotTheGame(HttpRequest request, string clientId)
        {
            if (BrowserHeader(request) != null) return "a web browser's request";
            if (!IsClientId(clientId)) return "not the game's client id";
            if (!HasUserAgent(request, clientId)) return "not the game's User-Agent";
            return null;
        }

        private static bool IsDigits(string s)
        {
            if (s.Length == 0 || s.Length > 12) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        private static bool IsVersion(string s)
        {
            if (s.Length == 0 || s[0] == '.' || s[s.Length - 1] == '.') return false;
            foreach (char c in s) if ((c < '0' || c > '9') && c != '.') return false;
            return true;
        }

        private static bool IsWord(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s) if ((c < 'a' || c > 'z') && (c < '0' || c > '9') && c != '_') return false;
            return true;
        }

        /// <summary>Printable ASCII without spaces, parentheses or quotes (one product token or a datacenter name).</summary>
        private static bool IsToken(string s)
        {
            foreach (char c in s) if (c <= ' ' || c >= 127 || c == '(' || c == ')' || c == '"') return false;
            return true;
        }
    }
}
