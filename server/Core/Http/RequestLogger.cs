using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>
    /// Request logging. Device testing has no debugger, so the logs are the protocol oracle:
    /// <list type="bullet">
    /// <item>every request: one INFO line (id, method, target, status, route, size, time) and one INFO line with
    /// its parameters (query, form or body, each value cut to <see cref="SummaryValueLimit"/> characters); the full
    /// headers and body, up to logging.bodyLimit, at DEBUG. Game-profile saves carry a form value of hundreds of KB,
    /// so the INFO line must not repeat it (every 5-minute autosave would otherwise log it twice);</item>
    /// <item>every unknown endpoint: a WARN entry with the complete request (headers, query, form, body) and one
    /// JSON line in logs/unknown-endpoints.jsonl, so missing endpoints can be collected after a play session;</item>
    /// <item>every handler exception: an ERROR with the full request.</item>
    /// </list>
    /// </summary>
    public sealed class RequestLogger
    {
        /// <summary>Upper bound for bodies in unknown-endpoint entries (they are rare and important).</summary>
        public const int UnknownBodyLimit = 1024 * 1024;

        /// <summary>Characters per parameter value (or raw body) on the INFO parameter line.</summary>
        public const int SummaryValueLimit = 512;

        private readonly Log log;
        private readonly RotatingFile unknownFile;
        private readonly Func<int> bodyLimit;

        /// <param name="logger">Root logger.</param>
        /// <param name="unknownLogPath">JSON-lines file for unknown endpoints (null: log only).</param>
        /// <param name="bodyLimit">Max characters per value/body in the normal request log.</param>
        public RequestLogger(Logger logger, string unknownLogPath, Func<int> bodyLimit)
        {
            log = logger != null ? logger.For("http") : Log.Null;
            unknownFile = unknownLogPath == null ? null : new RotatingFile(unknownLogPath, 10L * 1024 * 1024, 3);
            this.bodyLimit = bodyLimit ?? (() => 64 * 1024);
        }

        public void LogRequest(HttpRequest request, HttpResponse response, double elapsedMs)
        {
            if (!log.IsEnabled(LogLevel.Info)) return;
            int status = response == null ? 0 : response.StatusCode;
            string size = response == null ? "-" : response.StreamBody != null ? "stream" : response.Body.Length.ToString(CultureInfo.InvariantCulture) + " B";
            var sb = new StringBuilder(128);
            sb.Append('#').Append(request.Id.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(request.Method).Append(' ').Append(request.RawTarget.Length > 300 ? request.RawTarget.Substring(0, 300) + "..." : request.RawTarget)
              .Append(" -> ").Append(status.ToString(CultureInfo.InvariantCulture))
              .Append(" [").Append(request.RouteName ?? "-").Append("] ")
              .Append(size).Append(' ')
              .Append(elapsedMs.ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms");
            LogLevel level = status >= 500 ? LogLevel.Warn : LogLevel.Info;
            log.Write(level, sb.ToString(), null);

            string parameters = DescribeParameters(request, Num.Min(bodyLimit(), SummaryValueLimit));
            if (parameters.Length > 0) log.Write(level, "#" + request.Id.ToString(CultureInfo.InvariantCulture) + "   " + parameters, null);

            if (log.IsEnabled(LogLevel.Debug))
                log.Debug("#" + request.Id.ToString(CultureInfo.InvariantCulture) + " full request:\n" + Describe(request, bodyLimit()));
        }

        public void LogUnknown(HttpRequest request, string reason)
        {
            log.Warn("UNKNOWN ENDPOINT #" + request.Id.ToString(CultureInfo.InvariantCulture) + " (" + (reason ?? "no route") + ")\n"
                     + Describe(request, UnknownBodyLimit));
            if (unknownFile == null) return;
            var entry = new Dictionary<string, object>(StringComparer.Ordinal);
            entry["time"] = TimeFormat.Iso8601Millis(DateTime.UtcNow);
            entry["id"] = request.Id;
            entry["reason"] = reason;
            entry["method"] = request.Method;
            entry["target"] = request.RawTarget;
            entry["path"] = request.Path;
            entry["query"] = request.Query.ToJsonObject();
            entry["form"] = request.Form.ToJsonObject();
            entry["contentType"] = request.ContentType;
            entry["body"] = request.IsFormBody ? null : Truncate(request.BodyText, UnknownBodyLimit);
            entry["remote"] = request.RemoteEndPoint == null ? null : request.RemoteEndPoint.ToString();
            var headers = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var h in request.Headers) headers[h.Key] = h.Value;
            entry["headers"] = headers;
            unknownFile.AppendLine(MiniJson.Serialize(entry));
        }

        public void LogHandlerError(HttpRequest request, Exception ex)
        {
            log.Error("handler '" + (request.RouteName ?? "?") + "' threw for #" + request.Id.ToString(CultureInfo.InvariantCulture) + "\n"
                      + Describe(request, bodyLimit()), ex);
        }

        /// <summary>Multi-line description of a request: line, remote, headers, query, form or body.</summary>
        public static string Describe(HttpRequest request, int maxValueChars)
        {
            var sb = new StringBuilder(256);
            sb.Append("  ").Append(request.Method).Append(' ').Append(request.RawTarget).Append(' ').Append(request.ProtocolVersion).Append('\n');
            sb.Append("  path: ").Append(request.Path).Append('\n');
            if (request.RemoteEndPoint != null) sb.Append("  remote: ").Append(request.RemoteEndPoint).Append('\n');
            foreach (var h in request.Headers) sb.Append("  ").Append(h.Key).Append(": ").Append(h.Value).Append('\n');
            AppendParams(sb, "query", request.Query, maxValueChars);
            if (request.IsFormBody)
            {
                AppendParams(sb, "form", request.Form, maxValueChars);
            }
            else if (request.Body.Length > 0)
            {
                sb.Append("  body (").Append(request.Body.Length.ToString(CultureInfo.InvariantCulture)).Append(" B): ")
                  .Append(Truncate(request.BodyText, maxValueChars)).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>One line: "query: a=1 b=2 | form: c=3" or "body(n B): ..."; every value cut to <paramref name="maxValueChars"/>.</summary>
        public static string DescribeParameters(HttpRequest request, int maxValueChars)
        {
            var sb = new StringBuilder();
            if (request.Query.Count > 0)
            {
                sb.Append("query:");
                foreach (var kv in request.Query) sb.Append(' ').Append(kv.Key).Append('=').Append(Truncate(kv.Value, maxValueChars));
            }
            if (request.IsFormBody && request.Form.Count > 0)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append("form:");
                foreach (var kv in request.Form) sb.Append(' ').Append(kv.Key).Append('=').Append(Truncate(kv.Value, maxValueChars));
            }
            else if (!request.IsFormBody && request.Body.Length > 0)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append("body(").Append(request.Body.Length.ToString(CultureInfo.InvariantCulture)).Append(" B): ")
                  .Append(Truncate(request.BodyText, maxValueChars));
            }
            return sb.ToString();
        }

        private static void AppendParams(StringBuilder sb, string label, ParamCollection p, int maxValueChars)
        {
            if (p.Count == 0) return;
            sb.Append("  ").Append(label).Append(":\n");
            foreach (var kv in p) sb.Append("    ").Append(kv.Key).Append(" = ").Append(Truncate(kv.Value, maxValueChars)).Append('\n');
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return null;
            if (s.Length <= max) return s;
            return s.Substring(0, max) + "...(" + s.Length.ToString(CultureInfo.InvariantCulture) + " chars)";
        }
    }
}
