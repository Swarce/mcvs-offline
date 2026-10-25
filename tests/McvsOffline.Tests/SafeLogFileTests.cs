using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using Xunit;

namespace McvsOffline.Tests
{
    /// <summary>
    /// The log file that replaced RotatingFile (device test 1: bootstrap.log and server.log arrived empty). SelfLog is
    /// process-wide, so the tests that capture it share one collection and filter by their own paths.
    /// </summary>
    [Collection("SelfLog")]
    public class SafeLogFileTests : IDisposable
    {
        private readonly TempDir dir = new TempDir();
        private readonly List<string> selfLog = new List<string>();

        public SafeLogFileTests()
        {
            SelfLog.SetWriter((priority, text) => { lock (selfLog) selfLog.Add(priority + " " + text); });
        }

        public void Dispose()
        {
            SelfLog.SetWriter(null);
            dir.Dispose();
        }

        private List<string> SelfLines(string contains)
        {
            lock (selfLog) return selfLog.Where(l => l.Contains(contains)).ToList();
        }

        [Fact]
        public void ConcurrentLinesAreAllWrittenAndTheFileIsClosedBetweenBatches()
        {
            string path = dir.Combine("logs", "server.log");
            using (var file = new SafeLogFile(path, new SafeLogFileOptions { ReportInitialSize = true }))
            {
                Parallel.For(0, 4, t =>
                {
                    for (int i = 0; i < 500; i++) file.AppendLine("thread " + t + " line " + i);
                });
                Assert.True(file.Flush(10000));
                // Closed after the batch: an exclusive open succeeds while the writer is idle (RotatingFile kept it open forever,
                // so MTP never saw IN_CLOSE_WRITE and kept reporting the size the file had at creation).
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                string[] lines = File.ReadAllLines(path);
                Assert.Equal(2000, lines.Length);
                Assert.Equal(500, lines.Count(l => l.StartsWith("thread 3 ")));
                Assert.Equal(new FileInfo(path).Length, file.BytesWritten);
                Assert.Equal(0, file.Failures);
            }
            Assert.Single(SelfLines(path + " does not exist yet"));

            using (var again = new SafeLogFile(path, new SafeLogFileOptions { ReportInitialSize = true }))
            {
                again.AppendLine("second run");
            }
            Assert.Equal("second run", File.ReadAllLines(path).Last());   // Dispose writes what is queued
            Assert.Single(SelfLines(path + " exists, " + (new FileInfo(path).Length - "second run\n".Length) + " bytes before this run"));
        }

        [Fact]
        public void RotatesBySize()
        {
            string path = dir.Combine("rot", "a.log");
            using (var file = new SafeLogFile(path, new SafeLogFileOptions { MaxBytes = 4096, MaxFiles = 2 }))
            {
                for (int i = 0; i < 40; i++)
                {
                    file.AppendLine(new string('x', 400) + " " + i);
                    Assert.True(file.Flush(5000));
                }
            }
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(path + ".1"));
            Assert.True(File.Exists(path + ".2"));
            Assert.False(File.Exists(path + ".3"));
            Assert.True(new FileInfo(path).Length <= 4096);
            Assert.EndsWith(" 39", File.ReadAllLines(path).Last());
        }

        [Fact]
        public void AFailingFileReportsItsStageAndMovesToTheFallback()
        {
            string path = dir.Combine("broken", "server.log");
            Directory.CreateDirectory(path);   // a directory where the file should be: open fails
            string fallback = dir.Combine("internal");
            using (var file = new SafeLogFile(path, new SafeLogFileOptions { FallbackDirectory = fallback, RetryDelaysMs = new[] { 20 } }))
            {
                file.AppendLine("first");
                file.AppendLine("second");
                Assert.True(file.Flush(10000));
                Assert.True(file.UsingFallback);
                Assert.Equal(Path.Combine(fallback, "server.log"), file.Path);
                Assert.Contains("open", file.LastError);
                Assert.Equal(3, file.Failures);
            }
            Assert.Equal(new[] { "first", "second" }, File.ReadAllLines(Path.Combine(fallback, "server.log")));
            List<string> reports = SelfLines("cannot write " + path);
            Assert.Equal(3, reports.Count);
            Assert.All(reports, r => Assert.Contains("at stage 'open'", r));
            Assert.Single(SelfLines("keeps failing; continuing in " + Path.Combine(fallback, "server.log")));
            Assert.Single(SelfLines("writable again"));
        }

        [Fact]
        public void SynchronousModeWritesOnTheCallersThread()
        {
            string path = dir.Combine("unknown-endpoints.jsonl");
            using (var file = new SafeLogFile(path, new SafeLogFileOptions { Synchronous = true }))
            {
                file.AppendLine("{\"a\":1}");
                Assert.Equal("{\"a\":1}\n", File.ReadAllText(path));   // no flush needed: the server reads it while running
            }
        }

        [Fact]
        public void FlushNowDrainsTheQueueOnTheCallingThread()
        {
            string path = dir.Combine("crash", "bootstrap.log");
            using (var file = new SafeLogFile(path, new SafeLogFileOptions()))
            {
                for (int i = 0; i < 100; i++) file.AppendLine("line " + i);
                Assert.True(file.FlushNow(1000));
                Assert.Equal(100, File.ReadAllLines(path).Length);
                Assert.True(file.Flush(1000));
            }
        }

        [Fact]
        public void TheServerLogIsWrittenAndFlushedThroughTheLogger()
        {
            var logger = new Logger();
            var sink = new FileLogSink(dir.Combine("l", "x.log"), LogLevel.Info);
            logger.AddSink(sink);
            logger.For("t").Info("hello");
            Assert.True(logger.Flush(5000));
            Assert.Contains("INFO  [t] hello", File.ReadAllText(sink.Path));
            logger.Dispose();
        }
    }

    [Collection("SelfLog")]
    public class BootTraceTests
    {
        [Fact]
        public void RecognisesTheBootChainFromRequests()
        {
            var memory = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(memory);
            var trace = new BootTrace(logger.For("boot"));
            int id = 0;
            void Request(string method, string target, string route, int status = 200, string form = null)
            {
                var r = form == null ? HttpRequest.Create(method, target) : HttpRequest.Create(method, target, form);
                r.Id = ++id;
                r.SetRoute(route, McvsOffline.Server.Core.Routing.RouteValues.Empty);
                trace.RequestStarted(r);
                trace.RequestFinished(r, HttpResponse.RawJson("{}", status));
            }
            Request("GET", "/config/c/datacenters/mcvs/urls", "eve.urls");
            Request("POST", "/authorize", "auth.authorize", form: "credential_type=anonymous&scope=auth%20lobby%20config");
            Request("GET", "/config/c/datacenters/mcvs/urls", "eve.urls");
            Request("POST", "/authorize", "auth.authorize", form: "credential_type=game&scope=auth%20auth_admin_ro");
            Request("GET", "/lobbyconfigs/c/ts", "matchmaker.lobbyconfigs");
            Request("GET", "/verify?credential=system%3AAnubis", "auth.verify");
            Request("GET", "/lobbyconfigs/c/ts", "matchmaker.lobbyconfigs");
            Request("POST", "/authorize", "auth.authorize", form: "credential_type=game&device_id=default&scope=auth%20storage%20config");
            Request("GET", "/configs/users/me/game", "crm.config.selector");
            Request("GET", "/verify?credential=system%3AAnubis", "auth.verify");
            Request("GET", "/profiles/me/myprofile", "storage.profile.get", 404);
            Request("GET", "/profiles/me/myprofile", "storage.profile.get");
            Request("GET", "/lobbyconfigs/c/gs", "matchmaker.lobbyconfigs");

            string text = string.Join("\n", trace.Milestones());
            foreach (string expected in new[]
            {
                "client Federation: eve config (#1)", "client anonymous session (Authentication.Connect) (#2)",
                "second Federation: eve config (LocalAnubis) (#3)", "LocalAnubisManager.Start: super session (Embedded mode) (#4)",
                "QuickLaunch: lobbyconfigs ts (#5)", "Controller login of the client (verify system:Anubis) (#6)",
                "Controller 'connect game': creating the TS World (Args = cmd_args) (#7)", "TS World: game-server session (the TS World exists) (#8)",
                "first CRM config: /configs/users/me/game", "TS lobby login: the TS World now loads Levels/Menus.level (#10)",
                "request failed: GET /profiles/me/myprofile -> 404 (#11)", "TS handshake: first profile read (Seshat) (#12)", "GS match: lobbyconfigs gs #1 (#13)",
            })
            {
                Assert.Contains(expected, text);
            }
            Assert.Contains("13 requests, last #13 GET /lobbyconfigs/c/gs", trace.Describe());
            Assert.Contains("0 in flight", trace.Describe());
            Assert.True(memory.Contains("[boot]") || memory.Snapshot().All(e => e.Category == "boot"));
        }
    }
}
