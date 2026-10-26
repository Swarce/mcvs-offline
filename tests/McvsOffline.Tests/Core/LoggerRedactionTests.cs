using System;
using System.IO;
using System.Linq;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services;
using Xunit;

namespace McvsOffline.Tests.Core
{
    /// <summary>
    /// Logger.Redactor is applied before any sink sees an entry (design §5.4); the request logger keeps /_mcvs bodies,
    /// parameters and cookies out of the logs.
    /// </summary>
    public class LoggerRedactionTests
    {
        private const string Ticket = "0123456789abcdef0123456789abcdef";

        [Fact]
        public void EverySinkGetsRedactedMessagesAndExceptions()
        {
            using var dir = new TempDir();
            var logger = new Logger { Redactor = LogRedaction.Redact };
            var memory = new MemoryLogSink();
            var file = new FileLogSink(dir.Combine("x.log"), LogLevel.Trace, options: new SafeLogFileOptions { Synchronous = true });
            logger.AddSink(memory);
            logger.AddSink(file);
            Log log = logger.For("test");

            log.Info("opening http://127.0.0.1:51780/_mcvs/ui/#t=" + Ticket);
            log.Error("failed", new InvalidOperationException("bad ticket {\"ticket\":\"" + Ticket + "\"} password=hunter22"));
            logger.Flush(2000);
            logger.Dispose();

            var entries = memory.Snapshot();
            Assert.Equal("opening http://127.0.0.1:51780/_mcvs/ui/#t=<redacted>", entries[0].Message);
            string formatted = entries[1].Format();
            Assert.Contains("\"ticket\":\"<redacted>\"", formatted);
            Assert.Contains("password=hunt…<redacted>", formatted);
            Assert.DoesNotContain(Ticket, formatted);
            Assert.DoesNotContain("hunter22", entries[1].ExceptionText());

            string text = File.ReadAllText(dir.Combine("x.log"));
            Assert.Contains("#t=<redacted>", text);
            Assert.DoesNotContain(Ticket, text);
            Assert.DoesNotContain("hunter22", text);
        }

        [Fact]
        public void AThrowingRedactorLeavesTheTextAlone()
        {
            var logger = new Logger { Redactor = s => throw new InvalidOperationException() };
            var memory = new MemoryLogSink();
            logger.AddSink(memory);
            logger.For("x").Warn("plain", new Exception("inner"));
            Assert.Equal("plain", memory.Snapshot()[0].Message);
            Assert.Contains("inner", memory.Snapshot()[0].Format());
        }

        [Fact]
        public void TheServerRedactsItsLogsAndKeepsProblems()
        {
            using var dir = new TempDir();
            var logs = new MemoryLogSink(LogLevel.Trace);
            var options = new ServerOptions { DataDir = dir.Combine("data"), Port = 51780, ConsoleLog = false, FileLog = true };
            options.ExtraSinks.Add(logs);
            using (var server = McvsServer.Create(options))
            {
                Assert.Equal("#t=<redacted>", server.Context.Logger.Redactor("#t=" + Ticket));
                Assert.NotNull(server.Context.Problems);

                // The game server's authorize carries the super password in its form.
                var response = server.Handle(HttpRequest.Create("POST", "/authorize",
                    "credential_type=game&username=mcvsoffline&password=" + Settings.DefaultSuperPassword + "&client_id=c&scope=auth"));
                Assert.Equal(200, response.StatusCode);
                Assert.True(logs.Contains("password=mcvs…<redacted>"));
                Assert.DoesNotContain(logs.Snapshot(), e => e.Format().Contains("password=" + Settings.DefaultSuperPassword));
                Assert.DoesNotContain(logs.Snapshot(), e => e.Format().Contains("password = " + Settings.DefaultSuperPassword));

                server.Context.Logger.For("other").Warn("something odd");
                Assert.Contains(server.Context.Problems.Snapshot(), e => e.Message == "something odd");
                Assert.DoesNotContain(server.Context.Problems.Snapshot(), e => e.Level < LogLevel.Warn);
            }
            string serverLog = File.ReadAllText(dir.Combine("data", "logs", "server.log"));
            Assert.Contains("password=mcvs…<redacted>", serverLog);
            Assert.DoesNotContain("password=" + Settings.DefaultSuperPassword, serverLog);
        }

        [Fact]
        public void McvsRequestsAreLoggedWithoutParametersBodiesOrCookies()
        {
            using var dir = new TempDir();
            var logs = new MemoryLogSink(LogLevel.Trace);
            var options = new ServerOptions
            {
                DataDir = dir.Combine("data"), Port = 51780, ConsoleLog = false, FileLog = false,
                RegisterServices = (router, context) =>
                {
                    ServiceRegistry.RegisterAll(router, context);
                    router.Post("_mcvs/api/v1/boom", "test.boom", r => throw new InvalidOperationException("boom"));
                    router.Get("_mcvs/api/v1/fine", "test.fine", r => HttpResponse.EmptyObject());
                },
            };
            options.ExtraSinks.Add(logs);
            using var server = McvsServer.Create(options);

            const string body = "{\"ticket\":\"" + Ticket + "\",\"secretNote\":\"do-not-log-me\"}";
            var boom = HttpRequest.Create("POST", "/_mcvs/api/v1/boom?q=hidden-query", body, "application/json");
            boom.Headers.Add("X-MCVS", "1");
            boom.Headers.Add("Cookie", "mcvs_s=feedfacefeedfacefeedfacefeedface; other=visible-cookie");
            Assert.Equal(500, server.Handle(boom).StatusCode);

            var fine = HttpRequest.Create("GET", "/_mcvs/api/v1/fine?q=hidden-query");
            server.Handle(fine);

            string all = string.Join("\n", logs.Snapshot().Select(e => e.Format()));
            Assert.Contains("handler 'test.boom' threw", all);
            Assert.Contains("boom", all);
            Assert.DoesNotContain("do-not-log-me", all);
            Assert.DoesNotContain("hidden-query", all);
            Assert.DoesNotContain("visible-cookie", all);
            Assert.DoesNotContain("feedface", all);
            Assert.Contains("Cookie: <redacted>", all);
            Assert.Contains("(request body " + body.Length + " B)", all);

            // Summary lines under /_mcvs are DEBUG below status 400.
            var http = logs.Snapshot().Where(e => e.Category == "http").ToList();
            var summary = http.Single(e => e.Message.Contains("GET /_mcvs/api/v1/fine -> 200"));
            Assert.Equal(LogLevel.Debug, summary.Level);
            var failed = http.Single(e => e.Message.Contains("POST /_mcvs/api/v1/boom -> 500"));
            Assert.Equal(LogLevel.Warn, failed.Level);

            // Federation requests keep their parameter line (redacted by the logger).
            server.Handle(HttpRequest.Create("GET", "/locate/auth?client_id=visible-client"));
            Assert.True(logs.Contains("query: client_id=visible-client"));
        }

        [Fact]
        public void UnknownMcvsEndpointsAreRecordedWithoutBodies()
        {
            using var dir = new TempDir();
            var options = new ServerOptions { DataDir = dir.Combine("data"), Port = 51780, ConsoleLog = false, FileLog = false };
            using (var server = McvsServer.Create(options))
            {
                var request = HttpRequest.Create("POST", "/_mcvs/nothing-here?x=hidden-query", "t=" + Ticket + "&note=hidden-form");
                request.Headers.Add("X-MCVS", "1");
                server.Handle(request);
                var federation = HttpRequest.Create("POST", "/nothing/here", "password=hunter22&x=1");
                server.Handle(federation);
            }
            string unknown = File.ReadAllText(dir.Combine("data", "logs", "unknown-endpoints.jsonl"));
            Assert.Contains("/_mcvs/nothing-here", unknown);
            Assert.DoesNotContain("hidden-query", unknown);
            Assert.DoesNotContain("hidden-form", unknown);
            Assert.DoesNotContain(Ticket, unknown);
            Assert.Contains("/nothing/here", unknown);
            Assert.DoesNotContain("hunter22", unknown);
        }
    }
}
