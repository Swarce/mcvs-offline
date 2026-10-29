// Module: Misc - owner group: MISC
// The native connectivity tracker's endpoint (online_connectivity/connectionstatus).
// Protocol: docs/protocol/misc.md "Connectivity tracker"; docs/design/pc-port.md §4.1.

using System;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Misc
{
    /// <summary>
    /// The native engine's <c>ConnectivityTrackingManager</c> (in <c>NativeEngine.dll</c> on PC and every Android
    /// <c>libnativeengine.so</c>) locates <c>online_connectivity</c> through pandora and posts one form per tracked event:
    /// <c>type=&lt;auth|sp|gs|…&gt;&amp;status=&lt;0|1&gt;&amp;timestamp=…&amp;connectionstatus=&lt;details&gt;&amp;domain=…&amp;client_id=…[&amp;port=…]&amp;device_id=…</c>
    /// (the body may start with '&amp;'; <c>port</c> is left out when 0; on PC <c>device_id</c> is the machine's MAC
    /// address). The body of the answer is never read: 200 ends the event, a 5xx or a 3 s timeout makes the tracker retry,
    /// anything else drops it. So this route answers <c>200 {}</c> whatever happens, and logs one INFO line without the
    /// device id. The requests stop when the native online SDK is not initialised (patch <c>nativeonline</c>).
    /// </summary>
    public static class ConnectivityTracker
    {
        public const string RouteTemplate = "online_connectivity/connectionstatus";
        public const string RouteName = "misc.connectivity";

        /// <summary>Characters of <c>connectionstatus</c> kept in the log line (the tracker sends at most 100).</summary>
        public const int DetailsLimit = 100;

        public static void Register(Router router, Log log)
        {
            // Exact template, order 0: wins over the pandora service-prefix alias "online_connectivity/{first}/{*rest}".
            router.Any(RouteTemplate, RouteName, request => Handle(log, request));
        }

        /// <summary>Logs the event; always <c>200 {}</c> (a 500 from the router would make the tracker retry).</summary>
        public static HttpResponse Handle(Log log, HttpRequest request)
        {
            try
            {
                log.Info(Describe(request));
            }
            catch (Exception ex)
            {
                try
                {
                    log.Warn("connectivity: unreadable tracker post (" + ex.GetType().Name + ")");
                }
                catch (Exception)
                {
                    // Nothing left to do; the answer below is all the tracker needs.
                }
            }
            return HttpResponse.EmptyObject();
        }

        /// <summary><c>connectivity: type=&lt;t&gt; status=&lt;s&gt; details="&lt;≤100 chars&gt;"</c>; never the device id.</summary>
        public static string Describe(HttpRequest request)
        {
            string type = request.Param("type");
            string status = request.Param("status");
            string details = request.Param("connectionstatus") ?? "";
            if (details.Length > DetailsLimit) details = details.Substring(0, DetailsLimit);
            var sb = new StringBuilder(64 + details.Length);
            sb.Append("connectivity: type=").Append(Clean(type, 32)).Append(" status=").Append(Clean(status, 8))
              .Append(" details=\"").Append(details.Replace("\"", "'").Replace('\r', ' ').Replace('\n', ' ')).Append('"');
            return sb.ToString();
        }

        /// <summary>A short single-line token for the log: "-" when missing, control characters and spaces replaced.</summary>
        private static string Clean(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            if (value.Length > limit) value = value.Substring(0, limit);
            var sb = new StringBuilder(value.Length);
            foreach (char c in value) sb.Append(c <= ' ' || c == '"' ? '_' : c);
            return sb.ToString();
        }
    }
}
