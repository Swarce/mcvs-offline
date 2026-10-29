using System.IO;
using System.Linq;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Tests.Misc;
using Xunit;

namespace McvsOffline.Tests.Core
{
    /// <summary>
    /// The four request-log outputs (INFO summary, DEBUG dump, WARN unknown-endpoint dump, unknown-endpoints.jsonl) carry
    /// no MAC address and no full SteamID64, also through a logger without a redactor (docs/design/pc-port.md §4.2).
    /// </summary>
    public class RequestLoggerTests
    {
        private const string Mac = "04:BF:1B:6C:B9:62";
        private const string SteamId = "76561198366698514";

        private static HttpRequest TrackerLikeRequest(string path)
        {
            HttpRequest request = HttpRequest.Create("POST", path + "?username=" + SteamId,
                "&type=auth&status=1&client_id=2620%3A70425%3A1.17.30%3Asteam%3Asteam&device_id=04%3ABF%3A1B%3A6C%3AB9%3A62"
                + "&global_device_id=04-BF-1B-6C-B9-62", "application/x-www-form-urlencoded");
            request.Headers.Add("User-Agent", "connectivity_tracker/ GlWebTools/2.0 / ()");
            return request;
        }

        private static void AssertClean(string text)
        {
            Assert.DoesNotContain(Mac, text);
            Assert.DoesNotContain("04-BF-1B-6C-B9-62", text);
            Assert.DoesNotContain("B9%3A62", text);
            Assert.DoesNotContain(SteamId, text);
        }

        [Fact]
        public void EveryOutputIsRedactedWithoutTheLoggersRedactor()
        {
            using var dir = new TempDir();
            var logger = new Logger(); // no Redactor: RequestLogger redacts by itself
            var logs = new MemoryLogSink(LogLevel.Trace);
            logger.AddSink(logs);
            string jsonl = dir.Combine("logs", "unknown-endpoints.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(jsonl));
            using (var requestLogger = new RequestLogger(logger, jsonl, () => 64 * 1024))
            {
                HttpRequest request = TrackerLikeRequest("/some/unknown/endpoint");
                requestLogger.LogRequest(request, HttpResponse.EmptyObject(), 0.5);
                requestLogger.LogUnknown(request, "no route");
                requestLogger.LogHandlerError(request, new System.InvalidOperationException("boom"));
            }

            var entries = logs.Snapshot();
            Assert.Contains(entries, e => e.Level == LogLevel.Info && e.Message.Contains("form:"));      // the INFO summary
            Assert.Contains(entries, e => e.Level == LogLevel.Debug && e.Message.Contains("full request")); // the DEBUG dump
            Assert.Contains(entries, e => e.Level == LogLevel.Warn && e.Message.Contains("UNKNOWN ENDPOINT"));
            Assert.Contains(entries, e => e.Level == LogLevel.Error && e.Message.Contains("threw"));
            foreach (LogEntry e in entries) AssertClean(e.Format());
            Assert.Contains(entries, e => e.Message.Contains("device_id=<redacted-mac>"));
            Assert.Contains(entries, e => e.Message.Contains("device_id = <redacted-mac>"));
            Assert.Contains(entries, e => e.Message.Contains("username=7656…8514"));

            string file = File.ReadAllText(jsonl);
            Assert.Contains("\"device_id\":\"<redacted-mac>\"", file);
            Assert.Contains("\"global_device_id\":\"<redacted-mac>\"", file);
            AssertClean(file);
        }

        [Fact]
        public void AnUnknownEndpointThroughTheServerLeavesNoMac()
        {
            using var server = new MiscServer();
            HttpResponse response = server.Server.Handle(TrackerLikeRequest("/tracker/unknown"));
            Assert.Equal(200, response.StatusCode);
            Assert.True(server.Logs.Contains("UNKNOWN ENDPOINT"));
            foreach (LogEntry e in server.Logs.Snapshot()) AssertClean(e.Format());
            string jsonl = server.Dir.Combine("data", "logs", "unknown-endpoints.jsonl");
            Assert.True(File.Exists(jsonl));
            string file = File.ReadAllText(jsonl);
            Assert.Contains("/tracker/unknown", file);
            AssertClean(file);
        }

        [Fact]
        public void AJsonBodyOfAnUnknownEndpointKeepsNoMacInTheJsonlFile()
        {
            // unknown-endpoints.jsonl stores a non-form body as a JSON string, so the body's quotes arrive escaped (\").
            using var dir = new TempDir();
            string jsonl = dir.Combine("logs", "unknown-endpoints.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(jsonl));
            using (var requestLogger = new RequestLogger(new Logger(), jsonl, () => 64 * 1024))
            {
                HttpRequest request = HttpRequest.Create("POST", "/tracker/json",
                    "{\"device_id\":\"" + Mac + "\",\"global_device_id\" : \"04-BF-1B-6C-B9-62\",\"password\":\"hunter22\",\"user\":\"" + SteamId + "\"}",
                    "application/json");
                requestLogger.LogUnknown(request, "no route");
            }

            string file = File.ReadAllText(jsonl);
            Assert.Contains("\\\"device_id\\\":\\\"<redacted-mac>\\\"", file);
            Assert.Contains("\\\"password\\\":\\\"hunt…<redacted>\\\"", file);
            AssertClean(file);
            Assert.DoesNotContain("hunter22", file);
        }

        [Fact]
        public void TheConnectivityRouteLogsNoMacEither()
        {
            using var server = new MiscServer();
            server.Server.Handle(TrackerLikeRequest("/online_connectivity/connectionstatus"));
            Assert.True(server.Logs.Contains("connectivity: type=auth status=1"));
            foreach (LogEntry e in server.Logs.Snapshot()) AssertClean(e.Format());
        }
    }
}
