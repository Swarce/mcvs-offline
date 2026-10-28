using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Services.Admin.Logs;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>The zip writer and the logs zip (design §7.2).</summary>
    public class ZipWriterTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 26, 10, 15, 0, DateTimeKind.Utc);

        private static byte[] Text(int kilobytes, string line = "2026-10-26T10:15:00.123Z INFO  [x] the same line again and again\n")
        {
            var sb = new StringBuilder();
            while (sb.Length < kilobytes * 1024) sb.Append(line);
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>Exactly <paramref name="bytes"/> of log-like text that deflates poorly (base64 of random bytes, in lines).</summary>
        private static byte[] Noise(int bytes)
        {
            var sb = new StringBuilder(bytes + 100);
            while (sb.Length < bytes) sb.Append(Convert.ToBase64String(RandomNumberGenerator.GetBytes(57))).Append('\n');
            return Encoding.ASCII.GetBytes(sb.ToString(0, bytes));
        }

        private static byte[] Write(bool deflate, params (string Name, byte[] Data)[] entries)
        {
            var output = new MemoryStream();
            using (var zip = new ZipWriter(output, deflate, Now.ToLocalTime()))
            {
                foreach (var (name, data) in entries) zip.Add(zip.Prepare(name, data));
            }
            return output.ToArray();
        }

        [Fact]
        public void TheArchiveOpensWithZipArchiveAndTheCrcsAreRight()
        {
            byte[] text = Text(300);
            byte[] random = RandomNumberGenerator.GetBytes(50000);
            byte[] zip = Write(true, ("server.log", text), ("fallback/exit-1-tombstone.pb", random), ("empty.txt", new byte[0]));

            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            Assert.Equal(new[] { "server.log", "fallback/exit-1-tombstone.pb", "empty.txt" }, archive.Entries.Select(e => e.FullName));
            foreach (var (entry, data) in archive.Entries.Zip(new[] { text, random, new byte[0] }))
            {
                using var s = entry.Open();
                var copy = new MemoryStream();
                s.CopyTo(copy);
                Assert.Equal(data, copy.ToArray());
                Assert.Equal(Crc32.Compute(data), entry.Crc32);
            }
            Assert.True(archive.Entries[0].CompressedLength < text.Length / 10);   // deflated
            Assert.Equal(random.Length, archive.Entries[1].CompressedLength);       // stored: deflate did not help
        }

        [Fact]
        public void LocalHeadersHaveNoDataDescriptorAndTheRightMethods()
        {
            byte[] zip = Write(true, ("a.log", Text(64)), ("b.bin", RandomNumberGenerator.GetBytes(1000)));
            Assert.Equal(0x04034b50u, BitConverter.ToUInt32(zip, 0));
            Assert.Equal(0, BitConverter.ToUInt16(zip, 6) & 0x0008);    // general purpose flag bit 3: no data descriptor
            Assert.Equal(8, BitConverter.ToUInt16(zip, 8));              // deflate
            int second = 30 + 5 + BitConverter.ToInt32(zip, 18);
            Assert.Equal(0x04034b50u, BitConverter.ToUInt32(zip, second));
            Assert.Equal(0, BitConverter.ToUInt16(zip, second + 8));     // stored

            byte[] stored = Write(false, ("a.log", Text(64)));
            Assert.Equal(0, BitConverter.ToUInt16(stored, 8));
            using var archive = new ZipArchive(new MemoryStream(stored), ZipArchiveMode.Read);
            Assert.InRange(archive.Entries[0].Length, 64 * 1024, 64 * 1024 + 100);
        }

        [Fact]
        public void UnzipAcceptsTheArchive()
        {
            string unzip = FindOnPath("unzip");
            if (unzip == null) return;
            using var dir = new TempDir();
            string path = dir.Combine("t.zip");
            File.WriteAllBytes(path, Write(true, ("server.log", Text(200)), ("fallback/game.log", Text(10, "10:15:00.000 Info [x] y\n")),
                                           ("r.pb", RandomNumberGenerator.GetBytes(3000))));
            var psi = new ProcessStartInfo(unzip) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            Assert.True(p.ExitCode == 0, output);
            Assert.Contains("No errors detected", output);
        }

        private static LogFile File_(TempDir dir, int d, string name, byte[] data, DateTime modified)
        {
            string folder = dir.Combine("logs" + d);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name);
            File.WriteAllBytes(path, data);
            File.SetLastWriteTimeUtc(path, modified);
            return new LogFile { Dir = d, Name = name, Kind = LogsApi.KindOf(name), Size = data.Length, ModifiedUtc = modified, Path = path };
        }

        [Fact]
        public void RecentStaysUnderEightMegabytesAndListsWhatWasLeftOut()
        {
            using var dir = new TempDir();
            var files = new List<LogFile>
            {
                File_(dir, 0, "server.log", Noise(6 * 1024 * 1024), Now),
                File_(dir, 0, "bootstrap.log", Noise(4 * 1024 * 1024), Now),
                File_(dir, 0, "game.log", Noise(3 * 1024 * 1024), Now),
                File_(dir, 0, "server.log.1", Text(100), Now.AddHours(-1)),
                File_(dir, 0, "crash-20261026-100000-000-1.txt", Text(10), Now.AddHours(-1)),
                File_(dir, 0, "crash-20261020-100000-000-1.txt", Text(10), Now.AddDays(-6)),   // older than 72 h
                File_(dir, 1, "exception-20261026-090000-000-1.txt", Text(10), Now.AddHours(-2)),
                File_(dir, 0, "unknown-endpoints.jsonl", Text(2000, "{\"path\":\"/x\"}\n"), Now),
                File_(dir, 0, "other.txt", Text(10), Now),
            };
            string zipPath = dir.Combine("out.zip");
            LogsZipResult result = LogsZip.Build(zipPath, files, false, "{\"status\":1}", "{\"port\":51780}", "it crashed", true, Now);
            Assert.True(result.Size <= LogsZip.RecentCap, result.Size.ToString());
            Assert.Equal(new FileInfo(zipPath).Length, result.Size);
            Assert.Contains("game.log", result.Skipped);

            using var archive = ZipFile.OpenRead(zipPath);
            var names = archive.Entries.Select(e => e.FullName).ToList();
            Assert.Equal(new[] { "status.json", "settings.json", "note.txt" }, names.Take(3));
            Assert.Equal("manifest.txt", names.Last());
            Assert.Contains("server.log", names);
            Assert.Contains("server.log.1", names);
            Assert.Contains("crash-20261026-100000-000-1.txt", names);
            Assert.Contains("fallback/exception-20261026-090000-000-1.txt", names);
            Assert.Contains("unknown-endpoints.jsonl", names);
            Assert.DoesNotContain("crash-20261020-100000-000-1.txt", names);
            Assert.DoesNotContain("other.txt", names);
            Assert.InRange(archive.GetEntry("server.log").Length, LogsZip.CurrentTail - 1024, LogsZip.CurrentTail);   // the 4 MiB tail
            string manifest = new StreamReader(archive.GetEntry("manifest.txt").Open()).ReadToEnd();
            Assert.Contains("game.log\t" + (3 * 1024 * 1024) + "\t0\tskipped", manifest);
            Assert.Contains("server.log\t" + (6 * 1024 * 1024) + "\t", manifest);
            Assert.Contains("\ttruncated", manifest);
            Assert.Equal("it crashed\n", new StreamReader(archive.GetEntry("note.txt").Open()).ReadToEnd());
        }

        [Fact]
        public void EverythingTakesEveryFileAndTextIsRedacted()
        {
            using var dir = new TempDir();
            const string ticket = "0123456789abcdef0123456789abcdef";
            string line = "2026-10-26T10:15:00.123Z INFO  [old] page http://127.0.0.1:51780/_mcvs/ui/#t=" + ticket
                          + " superPassword=hunter22 access_token=abcdefghijkl\n";
            byte[] tombstone = RandomNumberGenerator.GetBytes(4000);
            var files = new List<LogFile>
            {
                File_(dir, 0, "server.log", Encoding.UTF8.GetBytes(line), Now),
                File_(dir, 0, "other.txt", Text(10), Now.AddDays(-30)),
                File_(dir, 0, "exit-123-tombstone.pb", tombstone, Now),
            };
            string zipPath = dir.Combine("all.zip");
            LogsZipResult result = LogsZip.Build(zipPath, files, true, "{\"note\":\"#t=" + ticket + "\"}", "{}", null, true, Now);
            Assert.Empty(result.Skipped);
            using var archive = ZipFile.OpenRead(zipPath);
            Assert.Contains("other.txt", archive.Entries.Select(e => e.FullName));
            Assert.Null(archive.GetEntry("note.txt"));
            string server = new StreamReader(archive.GetEntry("server.log").Open()).ReadToEnd();
            Assert.DoesNotContain(ticket, server);
            Assert.DoesNotContain("hunter22", server);
            Assert.DoesNotContain("abcdefghijkl", server);
            Assert.DoesNotContain(ticket, new StreamReader(archive.GetEntry("status.json").Open()).ReadToEnd());
            // Binary entries (*.pb) are copied as they are.
            var copy = new MemoryStream();
            using (var s = archive.GetEntry("exit-123-tombstone.pb").Open()) s.CopyTo(copy);
            Assert.Equal(tombstone, copy.ToArray());
        }

        private static string FindOnPath(string name)
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (dir.Length == 0) continue;
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
