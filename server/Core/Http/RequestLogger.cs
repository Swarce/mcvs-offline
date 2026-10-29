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
    /// Requests under <c>/_mcvs/</c> (the settings page and the diagnostics routes) carry tickets and settings, so their
    /// summary line is DEBUG (INFO from status 400 on) and shows the path and the body size only, never parameters or a
    /// body; the DEBUG full request, handler errors and unknown-endpoint entries leave out their query, form and body.
    /// The values of Cookie and Set-Cookie headers are never logged (design §5.4); every line and every jsonl entry goes
    /// through <see cref="LogRedaction.Redact"/> here (also when the logger has no redactor: the PC tracker's MAC address,
    /// SteamID64 user names, docs/design/pc-port.md §4.2), and again through the logger's redactor (idempotent).
    /// </summary>
    public sealed class RequestLogger : IDisposable
    {
        /// <summary>Upper bound for bodies in unknown-endpoint entries (they are rare and important).</summary>
        public const int UnknownBodyLimit = 1024 * 1024;

        /// <summary>Characters per parameter value (or raw body) on the INFO parameter line.</summary>
        public const int SummaryValueLimit = 512;

        private readonly Log log;
        private readonly SafeLogFile unknownFile;
        private readonly Func<int> bodyLimit;

        /// <param name="logger">Root logger.</param>
        /// <param name="unknownLogPath">JSON-lines file for unknown endpoints (null: log only).</param>
        /// <param name="bodyLimit">Max characters per value/body in the normal request log.</param>
        /// <param name="newFileAtStart">Start a new unknown-endpoints file for this process (the embedded host's per-start logs).</param>
        public RequestLogger(Logger logger, string unknownLogPath, Func<int> bodyLimit, bool newFileAtStart = false)
        {
            log = logger != null ? logger.For("http") : Log.Null;
            // Synchronous: unknown endpoints are rare, and the file is read while the server runs (the list of what is missing).
            unknownFile = unknownLogPath == null ? null
                : new SafeLogFile(unknownLogPath, new SafeLogFileOptions
                {
                    MaxBytes = 10L * 1024 * 1024, MaxFiles = 3, Synchronous = true, NewFileAtStart = newFileAtStart,
                });
            this.bodyLimit = bodyLimit ?? (() => 64 * 1024);
        }

        /// <summary>logs/unknown-endpoints.jsonl, or null (the embedded host's log retention deletes its older files through it).</summary>
        internal SafeLogFile UnknownFile { get { return unknownFile; } }

        public string DescribeUnknownFile()
        {
            return unknownFile == null ? null : unknownFile.Describe();
        }

        public void Dispose()
        {
            if (unknownFile != null) unknownFile.Dispose();
        }

        public void LogRequest(HttpRequest request, HttpResponse response, double elapsedMs)
        {
            int status = response == null ? 0 : response.StatusCode;
            bool admin = LocalAccessGate.IsAdminPath(request);
            LogLevel level = status >= 500 ? LogLevel.Warn : (!admin || status >= 400) ? LogLevel.Info : LogLevel.Debug;
            if (!log.IsEnabled(level)) return;
            string size = response == null ? "-" : response.StreamBody != null ? "stream" : response.Body.Length.ToString(CultureInfo.InvariantCulture) + " B";
            string target = admin ? request.RawPath : request.RawTarget;
            var sb = new StringBuilder(128);
            sb.Append('#').Append(request.Id.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(request.Method).Append(' ').Append(target.Length > 300 ? target.Substring(0, 300) + "..." : target)
              .Append(" -> ").Append(status.ToString(CultureInfo.InvariantCulture))
              .Append(" [").Append(request.RouteName ?? "-").Append("] ")
              .Append(size).Append(' ')
              .Append(elapsedMs.ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms");
            if (admin && request.Body.Length > 0)
                sb.Append(" (request body ").Append(request.Body.Length.ToString(CultureInfo.InvariantCulture)).Append(" B)");
            log.Write(level, LogRedaction.Redact(sb.ToString()), null);

            if (!admin)
            {
                string parameters = DescribeParameters(request, Num.Min(bodyLimit(), SummaryValueLimit));
                if (parameters.Length > 0)
                    log.Write(level, LogRedaction.Redact("#" + request.Id.ToString(CultureInfo.InvariantCulture) + "   " + parameters), null);
            }

            if (log.IsEnabled(LogLevel.Debug))
                log.Debug(LogRedaction.Redact("#" + request.Id.ToString(CultureInfo.InvariantCulture) + " full request:\n" + Describe(request, bodyLimit())));
        }

        public void LogUnknown(HttpRequest request, string reason)
        {
            log.Warn(LogRedaction.Redact("UNKNOWN ENDPOINT #" + request.Id.ToString(CultureInfo.InvariantCulture) + " (" + (reason ?? "no route") + ")\n"
                                         + Describe(request, UnknownBodyLimit)));
            if (unknownFile == null) return;
            bool admin = LocalAccessGate.IsAdminPath(request);
            var entry = new Dictionary<string, object>(StringComparer.Ordinal);
            entry["time"] = TimeFormat.Iso8601Millis(DateTime.UtcNow);
            entry["id"] = request.Id;
            entry["reason"] = reason;
            entry["method"] = request.Method;
            entry["target"] = admin ? request.RawPath : request.RawTarget;
            entry["path"] = request.Path;
            entry["query"] = admin ? null : request.Query.ToJsonObject();
            entry["form"] = admin ? null : request.Form.ToJsonObject();
            entry["contentType"] = request.ContentType;
            entry["body"] = admin || request.IsFormBody ? null : Truncate(request.BodyText, UnknownBodyLimit);
            if (admin) entry["bodyBytes"] = (long)request.Body.Length;
            entry["remote"] = request.RemoteEndPoint == null ? null : request.RemoteEndPoint.ToString();
            var headers = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var h in request.Headers) headers[h.Key] = HeaderValueForLog(h.Key, h.Value);
            entry["headers"] = headers;
            // The file is written directly, not through the logger: redact it here.
            unknownFile.AppendLine(LogRedaction.Redact(MiniJson.Serialize(entry)));
        }

        public void LogHandlerError(HttpRequest request, Exception ex)
        {
            log.Error(LogRedaction.Redact("handler '" + (request.RouteName ?? "?") + "' threw for #" + request.Id.ToString(CultureInfo.InvariantCulture) + "\n"
                                          + Describe(request, bodyLimit())), ex);
        }

        /// <summary>
        /// Multi-line description of a request: line, remote, headers, query, form or body. Cookie values are never
        /// shown; under <c>/_mcvs/</c> the query, form and body are left out (only the body size is given).
        /// </summary>
        public static string Describe(HttpRequest request, int maxValueChars)
        {
            bool admin = LocalAccessGate.IsAdminPath(request);
            var sb = new StringBuilder(256);
            sb.Append("  ").Append(request.Method).Append(' ').Append(admin ? request.RawPath : request.RawTarget).Append(' ')
              .Append(request.ProtocolVersion).Append('\n');
            sb.Append("  path: ").Append(request.Path).Append('\n');
            if (request.RemoteEndPoint != null) sb.Append("  remote: ").Append(request.RemoteEndPoint).Append('\n');
            foreach (var h in request.Headers) sb.Append("  ").Append(h.Key).Append(": ").Append(HeaderValueForLog(h.Key, h.Value)).Append('\n');
            if (admin)
            {
                if (request.Body.Length > 0)
                    sb.Append("  body: ").Append(request.Body.Length.ToString(CultureInfo.InvariantCulture)).Append(" B (not logged)\n");
                return sb.ToString().TrimEnd('\n');
            }
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

        /// <summary>The value of a header as logged: Cookie and Set-Cookie values are replaced by &lt;redacted&gt;.</summary>
        public static string HeaderValueForLog(string name, string value)
        {
            if (string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                return LogRedaction.Marker;
            return value;
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
