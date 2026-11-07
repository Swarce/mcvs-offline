using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Patcher.Pc;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// <c>install --port n</c> (docs/design/pc-port.md §7.7): the game's server listens on settings.json "port", so the installer
    /// sets that key there and touches nothing else; a settings.json that is not a JSON object refuses the install.
    /// </summary>
    public class PcSettingsPortTests
    {
        private readonly ITestOutputHelper output;

        public PcSettingsPortTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static string Merge(string existing, int port)
        {
            return Encoding.UTF8.GetString(PcSettingsPort.Merge(existing == null ? null : Encoding.UTF8.GetBytes(existing), port));
        }

        [Fact]
        public void NoFileGetsOneWithThePortOnly()
        {
            Assert.Equal("{\n  \"port\": 51790\n}\n", Merge(null, 51790));
        }

        [Fact]
        public void AnExistingPortValueIsReplacedAndEveryOtherByteKept()
        {
            // As Settings.Save writes it (MiniJson indented), with a nested "port" that is not the server's.
            string before = "{\n  \"lanMode\": false,\n  \"port\": 51780,\n  \"admin\": {\n    \"port\": 7,\n    \"enabled\": true\n  },\n  \"x\": 1.50\n}\n";
            Assert.Equal(before.Replace("\"port\": 51780", "\"port\": 51790"), Merge(before, 51790));
            // Any value type, every top-level duplicate (the parser keeps the last one).
            Assert.Equal("{\"port\":51790,\"a\":[1,{\"port\":2}],\"port\":51790}", Merge("{\"port\":\"51780\",\"a\":[1,{\"port\":2}],\"port\":{\"n\":[1]}}", 51790));
        }

        [Fact]
        public void AMissingPortIsInsertedFirstAndTheRestKept()
        {
            string before = "{\n  \"lanMode\": false,\n  \"logging\": {\n    \"level\": \"info\"\n  },\n  \"unicode\": \"é\\u00e9\"\n}\n";
            string after = Merge(before, 51790);
            Assert.Equal("{\n  \"port\": 51790," + before.Substring(1), after);
            Assert.Equal("{\n  \"port\": 51790\n}", Merge("{}", 51790));
            Assert.Equal("  {\n  \"port\": 51790\n }  ", Merge("  { }  ", 51790));

            // What the game reads back: the port, and every other member as it was, in its order.
            var read = (Dictionary<string, object>)MiniJson.Parse(after);
            var original = (Dictionary<string, object>)MiniJson.Parse(before);
            Assert.Equal(51790L, read["port"]);
            Assert.Equal(new[] { "port" }.Concat(original.Keys), read.Keys);
            Assert.Equal(MiniJson.Serialize(original), MiniJson.Serialize(read.Where(p => p.Key != "port").ToDictionary(p => p.Key, p => p.Value)));
        }

        [Fact]
        public void AByteOrderMarkIsKept()
        {
            byte[] bom = { 0xEF, 0xBB, 0xBF };
            byte[] merged = PcSettingsPort.Merge(bom.Concat(Encoding.UTF8.GetBytes("{\"port\":1}")).ToArray(), 51790);
            Assert.Equal(bom.Concat(Encoding.UTF8.GetBytes("{\"port\":51790}")), merged);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("{")]
        [InlineData("[]")]
        [InlineData("51780")]
        [InlineData("{\"port\":51780,}")]
        [InlineData("// mine\n{\"port\":51780}")]
        [InlineData("{\"port\":51780} {}")]
        [InlineData("{\"port\":51780")]
        public void ANonObjectIsRefused(string text)
        {
            var ex = Assert.Throws<InvalidDataException>(() => PcSettingsPort.Merge(Encoding.UTF8.GetBytes(text), 51790));
            Assert.StartsWith("McvsOffline\\settings.json cannot take the port: ", ex.Message);
            Assert.Contains("run install --port again", ex.Message);
        }

        // ------------------------------------------------------------------------------------------------ the installer

        /// <summary>
        /// install --port (SetSettingsPort) merges "port" into an existing settings.json, reports it, reads it back, and status sees
        /// it; the patched geo URL and install.json carry the same port; without --port settings.json is left alone.
        /// </summary>
        [Fact]
        public void InstallWithAPortSetsItInSettingsJsonOnly()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                string settings = copy.Path_(PcSettingsPort.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(settings));
                string mine = "{\n  \"lanMode\": false,\n  \"graphics\": {\n    \"profile\": \"high\"\n  },\n  \"port\": 51780\n}\n";
                File.WriteAllText(settings, mine);

                PcInstallOptions options = copy.Options();
                options.Port = 51790;
                options.SetSettingsPort = true;
                PcInstallReport report = PcInstaller.Install(options);
                Assert.True(report.ExitCode == 0, report.Error);
                List<string> lines = PcReportText.Install(report, false);
                foreach (string line in lines) output.WriteLine(line);

                Assert.Equal(mine.Replace("51780", "51790"), File.ReadAllText(settings));
                Assert.Equal(51790, report.SettingsPort);
                Assert.Equal(PcSettingsPort.RelativePath, report.Written.Last());   // after the config, outside the redirect chain
                Assert.Equal(GameFolder.Sha256(File.ReadAllBytes(settings)), report.WrittenSha256[PcSettingsPort.RelativePath]);
                Assert.Contains(lines, l => l.StartsWith("port     51790 is the server port in McvsOffline\\settings.json", StringComparison.Ordinal));
                Assert.Contains(report.Notes, n => n.Contains("http://127.0.0.1:51790/location/"));
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(settings), "*" + GameFolder.TempSuffix));

                PcStatusReport status = copy.Status();
                Assert.Equal(PcInstallState.Current, status.State);
                Assert.Equal(51790, status.SettingsPort);
                Assert.Equal(51790, status.InstalledPort);

                // What the host reads at boot.
                Assert.Equal(51790, Settings.Load(settings, null).Port);

                // A reinstall without --port (status's "run install again", a Steam update) leaves settings.json byte for byte
                // and keeps its port in the geo patch and install.json: the server still listens there.
                byte[] kept = File.ReadAllBytes(settings);
                PcInstallReport again = copy.Install();
                Assert.True(again.ExitCode == 0, again.Error);
                Assert.Null(again.SettingsPort);
                Assert.DoesNotContain(PcSettingsPort.RelativePath, again.Written);
                Assert.Equal(kept, File.ReadAllBytes(settings));
                Assert.Contains(again.Notes, n => n.Contains("http://127.0.0.1:51790/location/"));
                Assert.Contains(again.Notes, n => n.StartsWith("port 51790 from McvsOffline/settings.json (no --port given)", StringComparison.Ordinal));
                status = copy.Status();
                Assert.Equal(PcInstallState.Current, status.State);
                Assert.Equal(51790, status.InstalledPort);
                Assert.DoesNotContain(PcReportText.Status(status), l => l.Contains("they differ"));

                // A port the player wrote into settings.json after the install: status says the two differ, and a plain
                // reinstall takes it.
                File.WriteAllText(settings, mine.Replace("51780", "51795"));
                kept = File.ReadAllBytes(settings);
                Assert.Contains(PcReportText.Status(copy.Status()), l => l.Contains("settings.json 51795, install.json 51790 (they differ"));
                again = copy.Install();
                Assert.True(again.ExitCode == 0, again.Error);
                Assert.Contains(again.Notes, n => n.Contains("http://127.0.0.1:51795/location/"));
                Assert.Equal(kept, File.ReadAllBytes(settings));
                Assert.Equal(51795, copy.Status().InstalledPort);

                // Uninstall keeps it (the player's data).
                Assert.Equal(0, copy.Uninstall().ExitCode);
                Assert.Equal(kept, File.ReadAllBytes(settings));
            }
        }

        [Fact]
        public void InstallWithAPortCreatesAMissingSettingsJsonAndADryRunWritesNone()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                string settings = copy.Path_(PcSettingsPort.RelativePath);
                PcInstallOptions options = copy.Options();
                options.Port = 51790;
                options.SetSettingsPort = true;
                options.DryRun = true;
                PcInstallReport dry = PcInstaller.Install(options);
                Assert.True(dry.ExitCode == 0, dry.Error);
                Assert.False(File.Exists(settings));
                Assert.Contains(PcReportText.Install(dry, true), l => l.StartsWith("port     51790 would be the server port", StringComparison.Ordinal));

                options.DryRun = false;
                PcInstallReport report = PcInstaller.Install(options);
                Assert.True(report.ExitCode == 0, report.Error);
                Assert.Equal("{\n  \"port\": 51790\n}\n", File.ReadAllText(settings));
                Assert.Equal(dry.WrittenSha256[PcSettingsPort.RelativePath], report.WrittenSha256[PcSettingsPort.RelativePath]);
                Settings loaded = Settings.Load(settings, null);   // the host's first boot adds the defaults around it
                Assert.False(loaded.IsFallback);
                Assert.Equal(51790, loaded.Port);
                Assert.Equal(51790L, ((Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(settings)))["port"]);
            }
        }

        [Fact]
        public void AMalformedSettingsJsonRefusesTheInstallBeforeAnyWrite()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                string settings = copy.Path_(PcSettingsPort.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(settings));
                File.WriteAllText(settings, "{ \"lanMode\": false, ");
                SortedDictionary<string, string> before = copy.Tree();
                PcInstallOptions options = copy.Options();
                options.Port = 51790;
                options.SetSettingsPort = true;
                PcInstallReport report = PcInstaller.Install(options);
                Assert.Equal(PcExitCodes.Refused, report.ExitCode);
                Assert.Contains("McvsOffline\\settings.json cannot take the port", report.Error);
                Assert.EndsWith("nothing was written", report.Error);
                Assert.Equal(before, copy.Tree());

                // Without --port the same file does not matter to the installer: no port in it, the default one.
                PcInstallReport plain = copy.Install();
                Assert.Equal(0, plain.ExitCode);
                Assert.Contains(plain.Notes, n => n.Contains("http://127.0.0.1:51780/location/"));
                Assert.Equal("{ \"lanMode\": false, ", File.ReadAllText(settings));
            }
        }

        [Fact]
        public void ThePatcherCliPassesAGivenPortOnly()
        {
            PcCliOptions given = PcCli.Parse(new[] { "install", "--game", "x", "--port", "51790" });
            Assert.True(given.PortGiven);
            Assert.Equal(51790, given.Port);
            PcCliOptions none = PcCli.Parse(new[] { "install", "--game", "x" });
            Assert.False(none.PortGiven);
            Assert.Equal(51780, none.Port);
            Assert.Contains("McvsOffline\\settings.json", PcCli.Usage);
        }
    }
}
