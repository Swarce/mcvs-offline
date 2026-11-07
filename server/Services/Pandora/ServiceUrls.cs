using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;

namespace McvsOffline.Server.Services.Pandora
{
    /// <summary>
    /// The service base URLs we hand to clients, in eve's config/.../urls answer and in pandora's locate answer.
    /// <para>
    /// Every service gets <c>&lt;base&gt;&lt;service&gt;/</c>, e.g. <c>http://127.0.0.1:51780/storage/</c>
    /// (ARCHITECTURE §2.2). Why that works although every service is served at the host root: the C# client passes
    /// each URL through <c>Federation.FormatUri</c>, which trims the trailing '/', and then resolves request paths
    /// with <c>new Uri(new Uri(base), path)</c> (RFC 3986), so the last segment (the service name) is replaced by
    /// the relative path and absolute paths ("/users/...") start at the root (report 02 §1.3,
    /// OP/Federation.cs FormatUri, OP/FedRequestMessage.cs ResolveUri). The explicit "http://" and the explicit,
    /// non-80/443 port matter: FormatUri adds "https://" to scheme-less values and, for callers that force a protocol
    /// (LocalAnubis forces HTTPS), rewrites the scheme and drops the port when the port is 80, 443 or absent.
    /// </para>
    /// Code that joins by string concatenation instead (the DLC path: <c>pandora + "/locate/asset"</c>; native Gaia
    /// strings "/authorize", "/locate") produces "/&lt;service&gt;//&lt;path&gt;"; <see cref="PandoraService"/>
    /// registers service-prefix aliases that route those to the root routes.
    /// </summary>
    public static class ServiceUrls
    {
        /// <summary>"fed services": located through pandora (OP/OnlineService.cs FedServices).</summary>
        public static readonly string[] FedServices =
        {
            "auth", "storage", "leaderboard", "alert", "config", "groupchat", "lobby", "matchmaker", "message", "social",
            "asset", "game_stats",
        };

        /// <summary>Non-fed services read from eve's config/.../urls object (OP/OnlineService.cs, report 02 §1.2).</summary>
        public static readonly string[] EveServices =
        {
            "pandora", "game_portal", "ads_agency", "marketing_site", "ecomm_api_root", "crm_iap", "etsv2",
        };

        /// <summary>
        /// Service names only the native Gaia layer knows (libnativeengine.so strings next to "pandora"/"eveConfig");
        /// it may locate them through pandora. Answered like the others. <c>online_connectivity</c> is the native
        /// connectivity tracker's (Misc <c>ConnectivityTracker</c>; located by the PC client in every session).
        /// </summary>
        public static readonly string[] NativeServices =
        {
            "feeds", "lottery", "voice", "schedule", "globalidservice", "transaction", "online_connectivity",
        };

        /// <summary>Every service name we know, in a stable order (fed, eve, native).</summary>
        public static string[] AllKnown
        {
            get
            {
                var all = new List<string>();
                all.AddRange(FedServices);
                all.AddRange(EveServices);
                all.AddRange(NativeServices);
                return all.ToArray();
            }
        }

        public static bool IsKnown(string service)
        {
            return Array.IndexOf(FedServices, service) >= 0 || Array.IndexOf(EveServices, service) >= 0
                   || Array.IndexOf(NativeServices, service) >= 0;
        }

        /// <summary>
        /// The URL for <paramref name="service"/> as seen by the client that sent <paramref name="request"/>:
        /// settings <c>pandora.urls.&lt;service&gt;</c> when set (to point one service elsewhere), else
        /// <c>context.BaseUrlFor(request) + service + "/"</c>. Names that are not plain identifiers get the base URL.
        /// </summary>
        public static string For(ServerContext context, HttpRequest request, string service)
        {
            string baseUrl = context.BaseUrlFor(request);
            if (!IsSafeName(service)) return baseUrl;
            string configured = context.Settings.GetString("pandora.urls." + service, null);
            if (!string.IsNullOrEmpty(configured) && configured.Trim().Length > 0) return configured.Trim();
            return baseUrl + service + "/";
        }

        /// <summary>A service name we are willing to put into a URL: 1-64 of [A-Za-z0-9_.-].</summary>
        public static bool IsSafeName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64) return false;
            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
                if (!ok) return false;
            }
            return name != "." && name != "..";
        }

        /// <summary>
        /// Why <paramref name="url"/> would be rewritten by FormatUri, or null if it is safe: it must start with
        /// http:// or https:// and carry an explicit port other than 80/443 (report 02 §1.3, verify-C2 §1.2).
        /// </summary>
        public static string CheckClientUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "empty URL";
            int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd <= 0) return "no http:// scheme (FormatUri would prefix https://)";
            string scheme = url.Substring(0, schemeEnd).ToLowerInvariant();
            if (scheme != "http" && scheme != "https") return "scheme '" + scheme + "' is not http/https";
            int authorityStart = schemeEnd + 3;
            int pathStart = url.IndexOf('/', authorityStart);
            string authority = pathStart < 0 ? url.Substring(authorityStart) : url.Substring(authorityStart, pathStart - authorityStart);
            int bracket = authority.LastIndexOf(']');
            int colon = authority.LastIndexOf(':');
            if (colon < 0 || colon < bracket) return "no explicit port (callers forcing HTTPS rewrite it to https://host)";
            string port = authority.Substring(colon + 1);
            if (port == "80" || port == "443") return "port " + port + " (callers forcing HTTPS rewrite it to https://host)";
            if (port.Length == 0) return "empty port";
            return null;
        }
    }
}
