using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin.Logs;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>The logs routes through McvsServer.Handle (design §4.7, §7).</summary>
    public class LogsApiTests
    {
        private const string Ticket = "0123456789abcdef0123456789abcdef";

        private static Dictionary<string, object> J(HttpResponse r) => AdminTestClient.Json(r);

        private static void Status(int expected, HttpResponse r) => Assert.True(r.StatusCode == expected, r.StatusCode + " " + r.BodyText);

        /// <summary>A phone whose host has a logs directory and an internal-storage fallback with a few files.</summary>
        private static SavesServer Phone(out string logs, out string fallback)
        {
            var s = new SavesServer();
            logs = s.Dir.Combine("phone-logs");
            fallback = s.Dir.Combine("phone-fallback");
            Directory.CreateDirectory(logs);
            Directory.CreateDirectory(fallback);
            File.WriteAllText(Path.Combine(logs, "server.log"),
                "2026-10-26T10:15:00.123Z INFO  [crm] built\n" +
                "2026-10-26T10:15:01.000Z WARN  [admin] opened /_mcvs/ui/#t=" + Ticket + "\n" +
                "2026-10-26T10:15:02.000Z ERROR [storage] disk\n");
            File.WriteAllText(Path.Combine(logs, "bootstrap.log"), "2026-10-26T10:14:00.000Z INFO  [boot] started\n");
            File.WriteAllText(Path.Combine(logs, "server.log.1"), "2026-10-25T10:14:00.000Z INFO  [old] older\n");
            File.WriteAllText(Path.Combine(logs, "crash-20261026-101000-000-1.txt"), "crash line 1\ncrash line 2\n");
            File.WriteAllText(Path.Combine(logs, "threaddump-20261026-101100.txt"), "thread 1\n");
            File.WriteAllText(Path.Combine(logs, "notes.weird name"), "x");                    // not a log name: not listed
            File.WriteAllText(Path.Combine(fallback, "game.log"), "10:15:00.123 Warning [Net] retry\n");
            File.SetLastWriteTimeUtc(Path.Combine(logs, "server.log.1"), SavesServer.T0.AddDays(-1));
            s.Host.Info.LogsDir = logs;
            s.Host.Info.FallbackLogsDir = fallback;
            s.Host.Info.MirrorStatus = "active, 3 files";
            s.Session();
            return s;
        }

        [Fact]
        public void BothDirectoriesAreListedInTheirOrder()
        {
            using var s = Phone(out string logs, out string fallback);
            HttpResponse r = s.Client.Get("logs");
            Status(200, r);
            var dirs = ((List<object>)J(r)["directories"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(new object[] { Path.GetFullPath(logs), Path.GetFullPath(fallback) }, dirs.Select(d => d["path"]));
            Assert.Equal(new object[] { "primary", "fallback" }, dirs.Select(d => d["kind"]));
            var files = ((List<object>)J(r)["files"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(new[] { "0:server.log", "0:bootstrap.log", "1:game.log", "0:crash-20261026-101000-000-1.txt",
                                 "0:threaddump-20261026-101100.txt", "0:server.log.1" },
                         files.Select(f => f["dir"] + ":" + f["name"]));
            Assert.Equal(new object[] { "server", "bootstrap", "game", "crash", "threaddump", "rotated" }, files.Select(f => f["kind"]));
            Assert.Equal("active, 3 files", J(r)["mirror"]);
        }

        [Fact]
        public void WithoutAHostTheServersOwnLogIsListed()
        {
            using var s = new SavesServer(withHost: false, configure: o => o.FileLog = true);
            s.Session();
            s.Context.Logger.Flush(5000);
            HttpResponse r = s.Client.Get("logs");
            Status(200, r);
            Assert.Equal(Path.Combine(s.DataDir, "logs"), ((Dictionary<string, object>)((List<object>)J(r)["directories"])[0])["path"]);
            var names = ((List<object>)J(r)["files"]).Cast<Dictionary<string, object>>().Select(f => (string)f["name"]).ToList();
            Assert.Contains("server.log", names);
            Assert.Single((List<object>)J(r)["directories"]);
            HttpResponse tail = s.Client.Get("logs/server.log");
            Status(200, tail);
            Assert.NotEmpty((List<object>)J(tail)["entries"]);
        }

        [Fact]
        public void TheTailIsParsedFilteredAndRedacted()
        {
            using var s = Phone(out _, out _);
            HttpResponse r = s.Client.Get("logs/server.log?lines=200&min=warn");
            Status(200, r);
            var entries = ((List<object>)J(r)["entries"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(new object[] { "WARN", "ERROR" }, entries.Select(e => e["l"]));
            Assert.Equal("2026-10-26T10:15:01.000Z", entries[0]["t"]);
            Assert.Equal("admin", entries[0]["c"]);
            Assert.DoesNotContain(Ticket, r.BodyText);
            Assert.Equal(false, J(r)["truncatedHead"]);

            HttpResponse search = s.Client.Get("logs/server.log?q=DISK");
            Assert.Single((List<object>)J(search)["entries"]);
            HttpResponse game = s.Client.Get("logs/game.log?dir=1");
            Status(200, game);
            Assert.Equal("WARN", ((Dictionary<string, object>)((List<object>)J(game)["entries"])[0])["l"]);

            HttpResponse raw = s.Client.Get("logs/crash-20261026-101000-000-1.txt?raw=1");
            Status(200, raw);
            Assert.StartsWith("text/plain", raw.ContentType);
            Assert.Equal("crash line 1\ncrash line 2\n", raw.BodyText);
            HttpResponse rawServer = s.Client.Get("logs/server.log?raw=1");
            Assert.DoesNotContain(Ticket, rawServer.BodyText);
        }

        [Theory]
        [InlineData("logs/..")]
        [InlineData("logs/..%2Fsettings.json")]
        [InlineData("logs/%2E%2E")]
        [InlineData("logs/server.log?dir=2")]
        [InlineData("logs/server.log?dir=-1")]
        [InlineData("logs/game.log")]                // it is in the fallback directory, not the primary
        [InlineData("logs/notes.weird%20name")]
        [InlineData("logs/nothere.log")]
        public void UnlistedNamesAreNotFound(string path)
        {
            using var s = Phone(out _, out _);
            HttpResponse r = s.Client.Get(path);
            Assert.True(r.StatusCode == 404, path + " answered " + r.StatusCode + " " + r.BodyText);
        }

        [Fact]
        public void ALinkInTheLogsDirectoryIsNotFollowed()
        {
            using var s = Phone(out string logs, out _);
            File.CreateSymbolicLink(Path.Combine(logs, "accounts.log"), Path.Combine(s.DataDir, "accounts.json"));
            Assert.DoesNotContain("accounts.log", s.Client.Get("logs").BodyText);
            Assert.Equal(404, s.Client.Get("logs/accounts.log").StatusCode);
        }

        [Theory]
        [InlineData("logs/server.log?lines=0")]
        [InlineData("logs/server.log?lines=2001")]
        [InlineData("logs/server.log?lines=x")]
        [InlineData("logs/server.log?min=loud")]
        public void BadQueriesAreInvalid(string path)
        {
            using var s = Phone(out _, out _);
            HttpResponse r = s.Client.Get(path);
            Assert.Equal(400, r.StatusCode);
            Assert.Equal("invalid", J(r)["error"]);
            HttpResponse longQ = s.Client.Get("logs/server.log?q=" + new string('a', 101));
            Assert.Equal(400, longQ.StatusCode);
        }

        [Fact]
        public void ZipThenShare()
        {
            using var s = Phone(out _, out _);
            s.Context.Settings.Set("superPassword", "hunter22secret");
            HttpResponse zip = s.Client.Post("logs/zip", new Dictionary<string, object> { ["scope"] = "recent", ["note"] = "it froze #t=" + Ticket });
            Status(200, zip);
            string file = (string)J(zip)["file"];
            Assert.Matches("^mcvs-logs-\\d{8}-\\d{6}\\.zip$", file);
            Assert.Equal(true, AdminTestClient.At(zip, "fits.discord"));
            Assert.Equal(0L, J(zip)["skipped"]);
            string path = Path.Combine(s.DataDir, "admin", "share", file);
            Assert.Equal(new FileInfo(path).Length, J(zip)["size"]);

            using (var archive = ZipFile.OpenRead(path))
            {
                var names = archive.Entries.Select(e => e.FullName).ToList();
                Assert.Contains("server.log", names);
                Assert.Contains("fallback/game.log", names);
                Assert.Contains("crash-20261026-101000-000-1.txt", names);
                string all = string.Concat(archive.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
                Assert.DoesNotContain(Ticket, all);
                Assert.DoesNotContain("hunter22secret", all);
                string status = new StreamReader(archive.GetEntry("status.json").Open()).ReadToEnd();
                // The GET status body with ?storage=1, asked with the caller's own session.
                Assert.NotNull(JsonPath.Get(MiniJson.ParseObject(status), "server.instance"));
                Assert.NotNull(JsonPath.Get(MiniJson.ParseObject(status), "storage.total"));
                Assert.False(MiniJson.ParseObject(new StreamReader(archive.GetEntry("settings.json").Open()).ReadToEnd()).ContainsKey("superPassword"));
            }

            HttpResponse share = s.Client.Post("logs/share", new Dictionary<string, object> { ["file"] = file, ["target"] = "share" });
            Status(200, share);
            Assert.Equal(true, AdminTestClient.At(share, "share.ok"));
            string[] call = Assert.Single(s.Host.SharedFiles);
            Assert.Equal(new[] { path, "application/zip", file, "MCVS Offline logs" }, call.Take(4));
            Assert.DoesNotContain(Ticket, call[4]);
            Assert.Contains("it froze", call[4]);

            Assert.Equal(404, s.Client.Post("logs/share", new Dictionary<string, object> { ["file"] = "../settings.json" }).StatusCode);
            Assert.Equal(404, s.Client.Post("logs/share", new Dictionary<string, object> { ["file"] = "mcvs-logs-20200101-000000.zip" }).StatusCode);
            s.Host.Info.CanSaveToDownloads = false;
            Assert.Equal(501, s.Client.Post("logs/share", new Dictionary<string, object> { ["file"] = file, ["target"] = "downloads" }).StatusCode);
        }

        [Fact]
        public void OnlyOneZipAtATimeAndSixAMinute()
        {
            using var s = Phone(out _, out _);
            LogsApi logs = s.Context.Features.Get<LogsApi>();
            FieldInfo building = typeof(LogsApi).GetField("building", BindingFlags.NonPublic | BindingFlags.Instance);
            building.SetValue(logs, 1);
            HttpResponse busy = s.Client.Post("logs/zip", "{}");
            Assert.Equal(409, busy.StatusCode);
            Assert.Equal("busy", J(busy)["error"]);
            building.SetValue(logs, 0);

            for (int i = 0; i < LogsApi.ZipsPerMinute; i++)
            {
                Status(200, s.Client.Post("logs/zip", "{}"));
                s.Clock.Advance(TimeSpan.FromSeconds(1));
            }
            Assert.Equal(429, s.Client.Post("logs/zip", "{}").StatusCode);
            // The newest 3 zips are kept.
            Assert.Equal(LogsApi.ZipsKept, Directory.GetFiles(Path.Combine(s.DataDir, "admin", "share"), "mcvs-logs-*.zip").Length);
            Assert.Equal(400, s.Client.Post("logs/zip", new Dictionary<string, object> { ["scope"] = "everything" }).StatusCode);
        }

        [Fact]
        public void TheBundleIsAZipAttachment()
        {
            using var s = Phone(out _, out _);
            HttpResponse r = s.Client.Get("logs/bundle?scope=all");
            Status(200, r);
            Assert.Equal("application/zip", r.ContentType);
            Assert.Matches("^attachment; filename=\"mcvs-logs-\\d{8}-\\d{6}\\.zip\"$", r.Headers.Get("Content-Disposition"));
            using var archive = new ZipArchive(new MemoryStream(r.Body), ZipArchiveMode.Read);
            Assert.Contains("server.log.1", archive.Entries.Select(e => e.FullName));   // "all" includes the rotations
            Assert.Equal(400, s.Client.Get("logs/bundle?scope=x").StatusCode);
        }

        [Fact]
        public void TheTextReportGoesToTheHostOrBackToThePage()
        {
            using var s = Phone(out _, out _);
            HttpResponse shared = s.Client.Post("logs/share-text", new Dictionary<string, object> { ["note"] = "lag" });
            Status(200, shared);
            Assert.Equal(new[] { "ShareText:MCVS Offline report" }, s.Host.Calls.Where(c => c.StartsWith("ShareText")));

            s.Host.Info.CanShareText = false;
            HttpResponse text = s.Client.Post("logs/share-text", new Dictionary<string, object> { ["note"] = "lag" });
            Assert.Equal(501, text.StatusCode);
            string report = (string)J(text)["text"];
            Assert.StartsWith("MCVS Offline report", report);
            Assert.Contains("Note: lag", report);
            Assert.Contains("== Status ==", report);
            Assert.Contains("ERROR [storage] disk", report);
            Assert.Contains("crash line 1", report);
            Assert.DoesNotContain("[crm] built", report);   // INFO is not a problem
            Assert.DoesNotContain(Ticket, report);
            Assert.True(report.Length <= LogsApi.ReportMaxChars);
        }

        /// <summary>
        /// The embedded host starts new log files at every start: "the game broke, I restarted it and opened the page" finds the
        /// broken start in the numbered files, so the report has their problems too, after the current files. Without the host's
        /// start time it takes the .1 files and says "previous file".
        /// </summary>
        [Fact]
        public void TheTextReportHasThePreviousStartsProblems()
        {
            using var s = Phone(out string logs, out _);
            File.WriteAllText(Path.Combine(logs, "bootstrap.log.1"),
                "2026-10-25T09:00:00.000Z INFO  [boot] started\n2026-10-25T09:05:00.000Z WARN  [watchdog] game thread stalled\n");
            s.Host.Info.CanShareText = false;
            s.Host.Info.StartedUtc = null;
            string report = (string)J(s.Client.Post("logs/share-text", new Dictionary<string, object>()))["text"];
            int current = report.IndexOf("== server.log (last warnings and errors) ==", StringComparison.Ordinal);
            int previous = report.IndexOf("== bootstrap.log.1 (previous file, usually the previous start, last warnings and errors) ==", StringComparison.Ordinal);
            Assert.True(current >= 0 && previous > current, report);
            Assert.Contains("WARN [watchdog] game thread stalled", report.Substring(previous));
            Assert.Contains("== server.log.1 (previous file, usually the previous start, last warnings and errors) ==", report);   // listed, nothing above INFO
            Assert.DoesNotContain("[boot] started", report.Substring(previous));
        }

        /// <summary>
        /// A log that also rotated by size during this start: .1 is the earlier part of this start, so the report takes the newest
        /// numbered file written before the host's start time, and labels only that one "previous start".
        /// </summary>
        [Fact]
        public void TheTextReportFindsThePreviousStartPastASizeRotation()
        {
            using var s = Phone(out string logs, out _);
            DateTime started = DateTime.UtcNow.AddHours(-1);
            string part = Path.Combine(logs, "game.log.1");
            File.WriteAllText(part, "2026-10-29T09:00:00.000Z WARN  [game] earlier part of this start\n");
            File.SetLastWriteTimeUtc(part, started.AddMinutes(20));
            string broken = Path.Combine(logs, "game.log.2");
            File.WriteAllText(broken, "2026-10-28T09:00:00.000Z ERROR [game] the start that broke\n");
            File.SetLastWriteTimeUtc(broken, started.AddMinutes(-5));
            string older = Path.Combine(logs, "game.log.3");
            File.WriteAllText(older, "2026-10-27T09:00:00.000Z ERROR [game] an older start\n");
            File.SetLastWriteTimeUtc(older, started.AddDays(-1));
            s.Host.Info.CanShareText = false;
            s.Host.Info.StartedUtc = started;
            string report = (string)J(s.Client.Post("logs/share-text", new Dictionary<string, object>()))["text"];
            Assert.Contains("== game.log.2 (previous start, last warnings and errors) ==\n", report);
            Assert.Contains("ERROR [game] the start that broke", report);
            Assert.DoesNotContain("game.log.1 (", report);
            Assert.DoesNotContain("game.log.3", report);
            Assert.DoesNotContain("an older start", report);
            Assert.DoesNotContain("previous file", report);
        }
    }
}
