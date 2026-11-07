using System;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin.Logs;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>Log tail parsing (design §7.1): both formats, continuation lines, the cut window, filters, redaction.</summary>
    public class LogTailTests
    {
        private const string ServerLog =
            "2026-10-26T10:15:00.123Z INFO  [crm] CRM document built\n" +
            "2026-10-26T10:15:01.000Z WARN  [storage] slow write\n" +
            "2026-10-26T10:15:02.500Z ERROR [admin] admin: route admin.saves.list failed\n" +
            "System.InvalidOperationException: boom\n" +
            "   at McvsOffline.Server.Services.Admin.Saves.SavesApi.List()\n" +
            "2026-10-26T10:15:03.000Z DEBUG [http] GET /config/ 200\n" +
            "2026-10-26T10:15:04.000Z TRACE [http] body\n";

        private const string GameLog =
            "10:15:00.123 Info [Profile] loaded\n" +
            "10:15:01.000 Warning [Network] retry 1\n" +
            "10:15:02.000 Error [Crm] parse failed\n" +
            "    stack line\n" +
            "10:15:03.000 Fatal [Engine] crash\n" +
            "10:15:04.000 Verbose [Ui] frame\n";

        [Fact]
        public void ServerFormatWithAMultiLineException()
        {
            var entries = LogTail.Parse(ServerLog);
            Assert.Equal(new[] { "INFO", "WARN", "ERROR", "DEBUG", "TRACE" }, entries.Select(e => e.Level));
            Assert.Equal("2026-10-26T10:15:02.500Z", entries[2].Time);
            Assert.Equal("admin", entries[2].Category);
            Assert.Equal("admin: route admin.saves.list failed\nSystem.InvalidOperationException: boom\n   at McvsOffline.Server.Services.Admin.Saves.SavesApi.List()",
                         entries[2].Message);
            Assert.Equal("CRM document built", entries[0].Message);
        }

        [Fact]
        public void GameLogLevelsAreMapped()
        {
            var entries = LogTail.Parse(GameLog.Replace("\n", "\r\n"));
            Assert.Equal(new[] { "INFO", "WARN", "ERROR", "ERROR", "DEBUG" }, entries.Select(e => e.Level));
            Assert.Equal("10:15:02.000", entries[2].Time);
            Assert.Equal("Crm", entries[2].Category);
            Assert.Equal("parse failed\n    stack line", entries[2].Message);
        }

        [Fact]
        public void GameLogInUtcAsBuild14WritesIt()
        {
            // GameLogFormat.Line: the server.log time stamp, then the game's level and category names (not server.log's
            // padded upper-case levels, so the server-line parser must not take them).
            var entries = LogTail.Parse("2026-10-28T17:53:25.781Z Debug [Online] {Session.cs}.GetAccessToken:384 | ok\n" +
                                        "2026-10-28T17:53:26.000Z Error [Crm] parse failed\n    stack line\n" +
                                        "2026-10-28T17:53:27.000Z Fatal [Engine] crash\n" +
                                        "2026-10-28T17:53:28.000Z Warning [Network] retry 1\n" +
                                        "2026-10-28T17:53:29.000Z Info [Profile] loaded\n");
            Assert.Equal(new[] { "DEBUG", "ERROR", "ERROR", "WARN", "INFO" }, entries.Select(e => e.Level));
            Assert.Equal("2026-10-28T17:53:26.000Z", entries[1].Time);
            Assert.Equal("Crm", entries[1].Category);
            Assert.Equal("parse failed\n    stack line", entries[1].Message);
            Assert.Equal("Online", entries[0].Category);
            Assert.Equal("{Session.cs}.GetAccessToken:384 | ok", entries[0].Message);
        }

        [Fact]
        public void LinesBeforeTheFirstEntryAreOneUnknownEntry()
        {
            var entries = LogTail.Parse("tail of something\nmore\n" + ServerLog);
            Assert.Equal("?", entries[0].Level);
            Assert.Equal("tail of something\nmore", entries[0].Message);
            Assert.Equal(6, entries.Count);
            Assert.Equal("?", LogTail.Parse("2026-10-26T10:15:00.123Z       [x] no level\n")[0].Level);
        }

        [Fact]
        public void TheWindowIsTheLastMegabyteStartingAtAWholeLine()
        {
            using var dir = new TempDir();
            string path = dir.Combine("server.log");
            var sb = new StringBuilder();
            int n = 0;
            while (sb.Length < 3 * 1024 * 1024)
                sb.Append("2026-10-26T10:15:00.123Z INFO  [x] line ").Append(n++).Append(" é漢 padding padding padding\n");
            File.WriteAllText(path, sb.ToString());

            LogTailResult tail = LogTail.Read(path, 2000, LogLevel.Trace, null);
            Assert.True(tail.TruncatedHead);
            Assert.Equal(LogTail.WindowBytes, tail.Window);
            Assert.Equal(new FileInfo(path).Length, tail.Size);
            Assert.Equal(2000, tail.Entries.Count);
            Assert.All(tail.Entries, e => Assert.Equal("INFO", e.Level));   // no partial first line became a "?" entry
            Assert.EndsWith("line " + (n - 1) + " é漢 padding padding padding", tail.Entries.Last().Message);
        }

        [Fact]
        public void LevelAndSubstringFiltersKeepTheNewest()
        {
            using var dir = new TempDir();
            string path = dir.Combine("server.log");
            File.WriteAllText(path, "preamble\n" + ServerLog + ServerLog);
            // Level "?" (text before the first entry) is always shown.
            Assert.Equal(new[] { "?", "WARN", "ERROR", "WARN", "ERROR" }, LogTail.Read(path, 200, LogLevel.Warn, null).Entries.Select(e => e.Level));
            Assert.Equal(new[] { "?", "ERROR", "ERROR" }, LogTail.Read(path, 200, LogLevel.Error, null).Entries.Select(e => e.Level));
            Assert.Equal(2, LogTail.Read(path, 200, LogLevel.Trace, "BOOM").Entries.Count);
            Assert.Equal(2, LogTail.Read(path, 200, LogLevel.Trace, "storage").Entries.Count);   // the category too
            var last = LogTail.Read(path, 3, LogLevel.Trace, null).Entries;
            Assert.Equal(new[] { "ERROR", "DEBUG", "TRACE" }, last.Select(e => e.Level));
        }

        [Fact]
        public void MessagesAreRedactedBeforeTheyAreSearched()
        {
            using var dir = new TempDir();
            string path = dir.Combine("server.log");
            const string ticket = "0123456789abcdef0123456789abcdef";
            File.WriteAllText(path, "2026-10-26T10:15:00.123Z INFO  [old] opened http://127.0.0.1:51780/_mcvs/ui/#t=" + ticket + "\n" +
                                    "2026-10-26T10:15:01.123Z INFO  [old] Cookie mcvs_s=" + ticket + ticket + "\n");
            var entries = LogTail.Read(path, 200, LogLevel.Trace, null).Entries;
            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.DoesNotContain(ticket, e.Message));
            Assert.Empty(LogTail.Read(path, 200, LogLevel.Trace, ticket.Substring(0, 12)).Entries);
            long size;
            bool truncated;
            Assert.DoesNotContain(ticket, LogTail.Raw(path, out size, out truncated));
        }

        [Fact]
        public void LongMessagesAreCut()
        {
            using var dir = new TempDir();
            string path = dir.Combine("server.log");
            File.WriteAllText(path, "2026-10-26T10:15:00.123Z INFO  [x] " + new string('a', 40000) + "\n");
            var entry = LogTail.Read(path, 1, LogLevel.Trace, null).Entries.Single();
            Assert.Equal(LogTail.MaxMessageChars + 1, entry.Message.Length);
        }
    }
}
