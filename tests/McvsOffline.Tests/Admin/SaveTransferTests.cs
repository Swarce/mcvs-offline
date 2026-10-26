using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Services.Admin.Saves;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>The transfer text (design §6.2): format, part sizes, both methods, damaged and foreign parts, limits.</summary>
    public class SaveTransferTests
    {
        /// <summary>A bundle-like text that deflates the way real saves do (~1:4) and is large enough for several parts.</summary>
        internal static byte[] SampleBundle(int kilobytes = 120)
        {
            var rng = new Random(17);
            var sb = new StringBuilder("{\"format\":\"mcvs-offline/save-bundle\",\"items\":[");
            while (sb.Length < kilobytes * 1024)
            {
                sb.Append("{\"id\":\"item_").Append(rng.Next(0, 5000)).Append("\",\"count\":").Append(rng.Next(0, 99))
                  .Append(",\"hex\":\"").Append(rng.Next().ToString("x8")).Append("\"},");
            }
            sb.Append("{}]}");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        [Theory]
        [InlineData(true, 60000)]
        [InlineData(true, 3900)]
        [InlineData(true, 1900)]
        [InlineData(false, 60000)]
        [InlineData(false, 3900)]
        [InlineData(false, 1900)]
        public void RoundTripForBothMethodsAndEveryPartSize(bool deflate, int partChars)
        {
            byte[] bundle = SampleBundle();
            TransferExport export = SaveTransfer.Encode(bundle, partChars, "1a2b3c4d", deflate);
            Assert.Equal(deflate ? 'd' : 's', export.Method);
            foreach (string part in export.Parts)
            {
                Assert.StartsWith("MCVS-SAVE:1:1a2b3c4d:", part);
                Assert.EndsWith("\n~", part);
                int data = part.Length - part.IndexOf('\n') - 3;
                Assert.True(data <= partChars, "part data " + data + " > " + partChars);
                Assert.True(part.Length < partChars + 80);
            }
            int expectedParts = (int)Math.Ceiling(Convert.ToBase64String(deflate ? SaveTransfer.Deflate(bundle) : bundle).TrimEnd('=').Length / (double)partChars);
            Assert.Equal(expectedParts, export.Parts.Count);

            var rejected = new List<TransferRejection>();
            var parts = SaveTransfer.Extract(string.Join("\n", export.Parts), rejected);
            Assert.Empty(rejected);
            Assert.Equal(bundle, SaveTransfer.Decode(parts));

            // The .txt file (wrapped at 76, instruction lines first) reads back the same.
            string file = SaveTransfer.FileText(export, "MCVS Offline save of 123456789 (level 41, revision 900).\nTo import it...");
            var dataLines = file.Split('\n').Where(l => l.Length > 0 && l.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/')).ToList();
            Assert.NotEmpty(dataLines);
            Assert.All(dataLines, line => Assert.True(line.Length <= 76, line));
            Assert.StartsWith("MCVS Offline save of 123456789", file);
            var fromFile = SaveTransfer.Extract(file, rejected);
            Assert.Empty(rejected);
            Assert.Equal(bundle, SaveTransfer.Decode(fromFile));
        }

        [Fact]
        public void TheHeaderDescribesTheBinaryStreamAndThePart()
        {
            byte[] bundle = SampleBundle(10);
            TransferExport export = SaveTransfer.Encode(bundle, 1900, "0badf00d", true);
            byte[] stream = SaveTransfer.Deflate(bundle);
            string first = export.Parts[0];
            string[] header = first.Substring(0, first.IndexOf('\n')).Split(':');
            Assert.Equal(new[] { "MCVS-SAVE", "1", "0badf00d", "1/" + export.Parts.Count, "d", stream.Length.ToString(),
                                 Crc32.Compute(stream).ToString("x8"), header[7], "" }, header);
            string data = first.Substring(first.IndexOf('\n') + 1, first.Length - first.IndexOf('\n') - 3);
            Assert.Equal(Crc32.Compute(Encoding.ASCII.GetBytes(data)).ToString("x8"), header[7]);
            Assert.DoesNotContain("=", string.Concat(export.Parts));   // no padding
            Assert.DoesNotContain("_", string.Concat(export.Parts));   // standard base64, not base64url
        }

        [Fact]
        public void PartsInAnyOrderWithChatNoiseWrappedLinesAndCrlf()
        {
            byte[] bundle = SampleBundle();
            TransferExport export = SaveTransfer.Encode(bundle, 3900, "abcdef01", true);
            Assert.True(export.Parts.Count >= 3);
            var noisy = new StringBuilder();
            foreach (string part in export.Parts.AsEnumerable().Reverse())
            {
                noisy.Append("[10:42] Player4711: here you go\r\n");
                string header = part.Substring(0, part.IndexOf('\n') + 1);
                string data = part.Substring(header.Length, part.Length - header.Length - 2);
                noisy.Append(header.Replace("\n", "\r\n"));
                for (int i = 0; i < data.Length; i += 50) noisy.Append(data, i, Math.Min(50, data.Length - i)).Append(i % 100 == 0 ? "\r\n" : "  \t\n");
                noisy.Append("~\r\n(edited)\r\n\r\n");
            }
            var rejected = new List<TransferRejection>();
            var parts = SaveTransfer.Extract(noisy.ToString(), rejected);
            Assert.Empty(rejected);
            Assert.Equal(export.Parts.Count, parts.Count);
            Assert.Equal(bundle, SaveTransfer.Decode(parts.OrderBy(p => p.Number).ToList()));
        }

        [Fact]
        public void ADamagedPartIsRejectedByNumber()
        {
            TransferExport export = SaveTransfer.Encode(SampleBundle(), 1900, "11112222", true);
            string second = export.Parts[1];
            int at = second.IndexOf('\n') + 10;
            string flipped = second.Substring(0, at) + (second[at] == 'A' ? 'B' : 'A') + second.Substring(at + 1);
            string truncated = export.Parts[2].Substring(0, export.Parts[2].Length - 40);   // copied only in part, no '~'
            string foreign = export.Parts[3].Substring(0, export.Parts[3].IndexOf('\n') + 5) + "é" + export.Parts[3].Substring(export.Parts[3].IndexOf('\n') + 5);

            var rejected = new List<TransferRejection>();
            var parts = SaveTransfer.Extract(export.Parts[0] + "\n" + flipped + "\n" + truncated + "\n" + foreign + "\n" + export.Parts[4], rejected);
            Assert.Equal(new[] { 1, 5 }, parts.Select(p => p.Number));
            Assert.Equal(new[] { 2, 3, 4 }, rejected.Select(r => r.Part));
            Assert.All(rejected, r => Assert.Equal(SaveTransfer.Damaged, r.Reason));
        }

        [Fact]
        public void APartCutOffByTheNextPartIsRejectedAndTheNextStillRead()
        {
            TransferExport export = SaveTransfer.Encode(SampleBundle(), 1900, "22223333", true);
            string cut = export.Parts[0].Substring(0, export.Parts[0].Length - 100);
            var rejected = new List<TransferRejection>();
            var parts = SaveTransfer.Extract(cut + export.Parts[1], rejected);
            Assert.Equal(new[] { 2 }, parts.Select(p => p.Number));
            Assert.Equal(1, Assert.Single(rejected).Part);
        }

        [Theory]
        [InlineData("MCVS-SAVE:2:1a2b3c4d:1/1:d:10:00000000:00000000:\nAAAA\n~")]
        [InlineData("MCVS-SAVE:1:1A2B3C4D:1/1:d:10:00000000:00000000:\nAAAA\n~")]
        [InlineData("MCVS-SAVE:1:1a2b3c4d:2/1:d:10:00000000:00000000:\nAAAA\n~")]
        [InlineData("MCVS-SAVE:1:1a2b3c4d:0/1:d:10:00000000:00000000:\nAAAA\n~")]
        [InlineData("MCVS-SAVE:1:1a2b3c4d:1/1000:d:10:00000000:00000000:\nAAAA\n~")]
        [InlineData("MCVS-SAVE:1:1a2b3c4d:1/1:x:10:00000000:00000000:\nAAAA\n~")]
        [InlineData("MCVS-SAVE:1:1a2b3c4d:1/1:d:99999999:00000000:00000000:\nAAAA\n~")]
        [InlineData("MCVS-SAVE:1:1a2b3c4d:1/1:d:10:00000000:\nAAAA\n~")]
        [InlineData("MCVS-SAVE:")]
        public void MalformedHeadersAreRejected(string text)
        {
            var rejected = new List<TransferRejection>();
            Assert.Empty(SaveTransfer.Extract(text, rejected));
            Assert.NotEmpty(rejected);
        }

        [Fact]
        public void AnInflateBombIsTooLarge()
        {
            byte[] zeros = new byte[SaveTransfer.MaxInflatedBytes + 1024];
            TransferExport export = SaveTransfer.Encode(zeros, 60000, "b0b0b0b0", true);
            Assert.Equal('d', export.Method);
            Assert.True(export.Bytes < 100 * 1024);
            var parts = SaveTransfer.Extract(string.Concat(export.Parts), null);
            var ex = Assert.Throws<TransferTooLargeException>(() => SaveTransfer.Decode(parts));
            Assert.Equal(SaveTransfer.MaxInflatedBytes, ex.Limit);
        }

        [Fact]
        public void AStreamThatDoesNotMatchItsChecksumIsRefused()
        {
            TransferExport export = SaveTransfer.Encode(SampleBundle(5), 60000, "c0c0c0c0", true);
            var parts = SaveTransfer.Extract(export.Parts[0], null);
            parts[0].Crc = "00000000";
            Assert.Throws<TransferException>(() => SaveTransfer.Decode(parts));
            parts = SaveTransfer.Extract(export.Parts[0], null);
            parts[0].Bytes++;
            Assert.Throws<TransferException>(() => SaveTransfer.Decode(parts));
        }

        /// <summary>A raw deflate stream made by Python's zlib (level 6, wbits=-15): the format other tools produce.</summary>
        [Fact]
        public void InflatesPythonZlibRawDeflate()
        {
            const string fixture = "83UOC1bwT0vLycxLVSgpSswrTkstUkjLrCgpLUq1UijJSFUoLM1MzlZIKsovz1NIy69QyCrNLShWyC8DqgNJ5yRWVSqk5KcrGBgaGZuYmplbWI5qG9U2qm1UG6o2AA==";
            byte[] inflated = SaveTransfer.Inflate(Convert.FromBase64String(fixture), 4096);
            string expected = "MCVS Offline transfer fixture: " + string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog 0123456789 ", 20));
            Assert.Equal(expected, Encoding.UTF8.GetString(inflated));
            Assert.Equal("bbaa9967", Crc32.Compute(inflated).ToString("x8"));
        }

        /// <summary>Our deflate output is plain RFC 1951: Python's zlib inflates it with wbits=-15 (when python3 is on PATH).</summary>
        [Fact]
        public void PythonZlibInflatesOurDeflate()
        {
            byte[] data = SampleBundle(20);
            byte[] deflated = SaveTransfer.Deflate(data);
            string python = FindOnPath("python3");
            if (python == null) return;
            using var dir = new TempDir();
            string input = dir.Combine("in.bin");
            File.WriteAllBytes(input, deflated);
            var psi = new ProcessStartInfo(python)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.ArgumentList.Add("-I");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import sys,zlib;d=open(sys.argv[1],'rb').read();print('%08x' % zlib.crc32(zlib.decompress(d,-15)))");
            psi.ArgumentList.Add(input);
            using var process = Process.Start(psi);
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(30000);
            Assert.Equal(Crc32.Compute(data).ToString("x8"), output);
        }

        [Fact]
        public void TheDeflateProbeReportsAFailureInsteadOfThrowing()
        {
            string problem;
            Assert.True(SaveTransfer.Probe(SaveTransfer.Deflate, d => SaveTransfer.Inflate(d, 1024), out problem));
            Assert.Null(problem);
            Assert.False(SaveTransfer.Probe(d => throw new DllNotFoundException("libz"), d => d, out problem));
            Assert.Contains("DllNotFoundException", problem);
            Assert.False(SaveTransfer.Probe(d => d, d => new byte[1], out problem));
        }

        [Fact]
        public void WithoutDeflateTheExportIsStored()
        {
            byte[] bundle = SampleBundle(30);
            TransferExport export = SaveTransfer.Encode(bundle, 60000, "d0d0d0d0", false);
            Assert.Equal('s', export.Method);
            Assert.Contains(":s:", export.Parts[0].Substring(0, 60));
            Assert.Equal(bundle, SaveTransfer.Decode(SaveTransfer.Extract(string.Concat(export.Parts), null)));
            // Incompressible data is stored even when deflate is available.
            byte[] random = RandomNumberGenerator.GetBytes(5000);
            Assert.Equal('s', SaveTransfer.Encode(random, 60000, "e0e0e0e0", true).Method);
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
