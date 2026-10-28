using System;
using System.IO;
using System.Reflection;
using System.Text;
using McvsOffline.Server.Core.Content;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.PcSpike.Tests
{
    /// <summary>The data view for the server, the telemetry CSV, the crash-log decoder, the PE header check and the options.</summary>
    public class FilesAndFormatsTests
    {
        private readonly ITestOutputHelper output;

        public FilesAndFormatsTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Theory]
        [InlineData("Config/glue.json", "187993782.f")]
        [InlineData("Config/crmpreconfiguration.json", "3186060502.f")]
        [InlineData("Config/Settings.tx", "3390017736.f")]
        [InlineData("Design/MCVS_Meta.xlsx", "3659042665.f")]
        public void SteamFileNamesAreTheDecimalCrc32(string logical, string expected)
        {
            Assert.Equal(expected, PcDataView.PcFileName(logical));
        }

        [Fact]
        public void TheDataViewOfTheRealInstallFeedsTheServersDataPack()
        {
            string pc = Support.PcInstall(output);
            if (pc == null) return;
            string data = Support.NewTempDir("data");
            try
            {
                string view = PcDataView.Build(pc, data, new SpikeLog(data, echo: false));
                Assert.NotNull(view);
                var pack = new DataPack(view);
                Assert.True(pack.IsValid);
                string crm;
                Assert.True(pack.TryReadText("Config/crmpreconfiguration.json", out crm));
                Assert.StartsWith("{", crm.TrimStart());
                byte[] meta;
                Assert.True(pack.TryRead("Design/MCVS_Meta.xlsx", out meta));
                // The pack's "xlsx" is the exported JSON tables (as on Android), which LiveOps' DivisionTables reads.
                Assert.StartsWith("{\"ShopCategoryPrio\"", Encoding.UTF8.GetString(meta, 0, 40));
                Assert.Contains("DivisionsList", Encoding.UTF8.GetString(meta));
                long copied = 0;
                foreach (string f in Directory.GetFiles(view, "*", SearchOption.AllDirectories)) copied += new FileInfo(f).Length;
                output.WriteLine("view: " + copied + " bytes");
                Assert.True(copied < 8 * 1024 * 1024);
            }
            finally
            {
                Support.DeleteDir(data);
            }
        }

        [Fact]
        public void WithoutMcvsGameThereIsNoView()
        {
            string game = Support.NewTempDir("game");
            string data = Support.NewTempDir("data");
            try
            {
                Assert.Null(PcDataView.Build(game, data, new SpikeLog(data, echo: false)));
            }
            finally
            {
                Support.DeleteDir(game);
                Support.DeleteDir(data);
            }
        }

        [Fact]
        public void TelemetryRowsAreMegabytesWithQuotedNotes()
        {
            var sample = new TelemetrySample
            {
                Time = new DateTime(2026, 10, 28, 20, 15, 30),
                ElapsedSeconds = 12.4,
                ProcessId = 4242,
                PrivateBytes = 1536L * 1024 * 1024,
                VirtualBytes = 1900L * 1024 * 1024 + 512 * 1024,
                PeakVirtualBytes = -1,
                WorkingSet = 0,
                PeakWorkingSet = 10 * 1024 * 1024,
                PagedBytes = 1024 * 1024,
                Threads = 80,
                Handles = 900,
                Note = "match start, \"bots\"",
            };
            Assert.Equal("2026-10-28 20:15:30,12,4242,1536.0,1900.5,,0.0,10.0,1.0,80,900,\"match start, \"\"bots\"\"\"", TelemetryCsv.Row(sample));
            Assert.Equal(12, TelemetryCsv.Header.Split(',').Length);
            Assert.Equal("menu", TelemetryCsv.Quote("menu"));
            Assert.Equal("", TelemetryCsv.Quote(null));

            string csv = Path.GetTempFileName();
            try
            {
                File.WriteAllLines(csv, new[] { TelemetryCsv.Header, TelemetryCsv.Row(sample), "2026-10-28 20:15:35,17,4242,1600.0,1700.0,,,,,1,1," });
                double privateMb, virtualMb;
                int rows;
                Assert.True(TelemetryCsv.Peaks(csv, out privateMb, out virtualMb, out rows));
                Assert.Equal(1600.0, privateMb);
                Assert.Equal(1900.5, virtualMb);
                Assert.Equal(2, rows);
            }
            finally
            {
                File.Delete(csv);
            }
        }

        [Fact]
        public void NetstatLinesOfTheGameAreTheListenersAndUdpEndpoints()
        {
            const string netstat =
                "\r\nActive Connections\r\n\r\n  Proto  Local Address          Foreign Address        State           PID\r\n" +
                "  TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1000\r\n" +
                "  TCP    127.0.0.1:51750        0.0.0.0:0              LISTENING       4\r\n" +
                "  TCP    127.0.0.1:51750        127.0.0.1:55001        ESTABLISHED     4\r\n" +
                "  TCP    127.0.0.1:51750        0.0.0.0:0              ABHÖREN         4242\r\n" +
                "  TCP    127.0.0.1:55000        127.0.0.1:51780        ESTABLISHED     4242\r\n" +
                "  TCP    [::]:7777              [::]:0                 LISTENING       4242\r\n" +
                "  UDP    0.0.0.0:27015          *:*                                    4242\r\n" +
                "  UDP    0.0.0.0:5353           *:*                                    77\r\n";
            Assert.Equal(new[]
            {
                "TCP 127.0.0.1:51750 0.0.0.0:0 LISTENING 4",   // the http.sys Finder, owned by System
                "TCP 127.0.0.1:51750 0.0.0.0:0 ABHÖREN 4242",
                "TCP [::]:7777 [::]:0 LISTENING 4242",
                "UDP 0.0.0.0:27015 *:* 4242",
            }, TelemetrySampler.SocketLinesOf(netstat, new[] { 4242 }));
        }

        [Fact]
        public void CrashLogLinesRoundTripAndOtherLinesStay()
        {
            string line = CrashLogDecoder.EncryptLine("Exception type: ");
            Assert.NotEqual("Exception type: ", line);
            Assert.Equal("Exception type: ", CrashLogDecoder.DecryptLine(line));
            Assert.Equal("plain text", CrashLogDecoder.DecryptLine("plain text"));
            Assert.Equal("QUJD", CrashLogDecoder.DecryptLine("QUJD"));   // base64, but not a cipher block
            Assert.Equal(24, CrashLogDecoder.Key().Length);
            Assert.Equal(16, CrashLogDecoder.Iv().Length);
        }

        [Fact]
        public void TheGamesOwnEncryptionIsDecrypted()
        {
            string pc = Support.PcInstall(output);
            if (pc == null) return;
            using (var sandbox = new Support.Sandbox().With("Gameloft.Logs", File.ReadAllBytes(Path.Combine(pc, "Gameloft.Logs.dll"))))
            {
                Type saveLogs = sandbox.Get("Gameloft.Logs").GetType("Gameloft.SaveLogs", true);
                MethodInfo encrypt = saveLogs.GetMethod("EncryptAES", BindingFlags.NonPublic | BindingFlags.Static);
                const string text = "NullReferenceException at Gameloft.Engine.World.GetUI() éè";
                string cipher = (string)encrypt.Invoke(null, new object[] { text, true });
                Assert.Equal(text, CrashLogDecoder.DecryptLine(cipher));
            }
        }

        [Fact]
        public void TheRealExeIsTheKnown32BitBuildWithoutLargeAddressAware()
        {
            string pc = Support.PcInstall(output);
            if (pc == null) return;
            string exe = Path.Combine(pc, "ModernCombatVersus.exe");
            int machine, characteristics;
            PeHeader.Read(exe, out machine, out characteristics);
            Assert.Equal(0x14c, machine);
            Assert.Equal(KnownPcBuild.ExeCharacteristics, characteristics);
            Assert.Contains("large-address-aware: no", PeHeader.Describe(exe));
            Assert.StartsWith("known", KnownPcBuild.Describe(exe, KnownPcBuild.ExeSha256));
        }

        [Fact]
        public void PeHeaderRejectsOtherFiles()
        {
            string file = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(file, Encoding.ASCII.GetBytes(new string('x', 200)));
                Assert.StartsWith("unreadable", PeHeader.Describe(file));
                Assert.StartsWith("UNKNOWN", KnownPcBuild.Describe(file, KnownPcBuild.ExeSha256));
                Assert.Equal("missing", KnownPcBuild.Describe(file + ".none", KnownPcBuild.ExeSha256));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void OptionsParse()
        {
            SpikeOptions o = SpikeOptions.Parse(new[] { "Start", "--game", "D:\\Games\\MCVS", "--port", "51781", "--interval", "2", "--no-debug-log" });
            Assert.Equal("start", o.Command);
            Assert.Equal("D:\\Games\\MCVS", o.GameDir);
            Assert.Equal(51781, o.Port);
            Assert.Equal(2, o.TelemetrySeconds);
            Assert.True(o.NoDebugCapture);
            Assert.False(o.NoTelemetry);
            Assert.EndsWith(Path.Combine("McvsOffline", "spike"), o.DataDir);
            Assert.Null(SpikeOptions.Parse(new string[0]).Command);
            Assert.Equal("help", SpikeOptions.Parse(new[] { "--help" }).Command);
            Assert.Throws<ArgumentException>(() => SpikeOptions.Parse(new[] { "start", "stop" }));
            Assert.Throws<ArgumentException>(() => SpikeOptions.Parse(new[] { "--port", "0" }));
            Assert.Throws<ArgumentException>(() => SpikeOptions.Parse(new[] { "--bogus" }));
        }
    }
}
