using System.IO;
using System.IO.Compression;
using System.Linq;
using McvsOffline.Embedded;
using McvsOffline.Server.Core;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.PcSpike.Tests
{
    /// <summary>'collect': the request summary from server.log and a zip that holds logs and our own files, never game files.</summary>
    public class CollectorTests
    {
        private readonly ITestOutputHelper output;

        public CollectorTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void RequestsAreGroupedByRouteInOrderOfFirstUse()
        {
            string[] lines =
            {
                "2026-10-28 20:00:00.000 INFO  [http] #1 GET /config/2620:70425:1.17.30:steam:steam -> 200 [eve.config] 812 B 1.0 ms",
                "2026-10-28 20:00:00.100 INFO  [http] #1   client_id=2620:70425:1.17.30:steam:steam",
                "2026-10-28 20:00:01.000 INFO  [http] #2 POST /authorize -> 200 [auth.authorize] 300 B 2.0 ms",
                "2026-10-28 20:00:02.000 WARN  [http] #3 GET /config/x -> 500 [eve.config] 10 B 0.5 ms",
                "unrelated line -> 200 [nope]",
            };
            var routes = Collector.SummariseRequests(lines);
            Assert.Equal(new[] { "eve.config", "auth.authorize" }, routes.Select(r => r.Route));
            Assert.Equal(2, routes[0].Count);
            Assert.Equal(new[] { 200, 500 }, routes[0].Statuses);
            Assert.Equal("GET /config/2620:70425:1.17.30:steam:steam", routes[0].FirstTarget);
        }

        [Fact]
        public void PreferencesAreLookedForInTheGameFolderAndInGameloftNamedFolders()
        {
            string root = Support.NewTempDir("prefs");
            try
            {
                string game = Path.Combine(root, "game");
                string appData = Path.Combine(root, "appdata");
                foreach (string dir in new[] { game, Path.Combine(appData, "Gameloft", "MCVS", "prefs"), Path.Combine(appData, "Other"), Path.Combine(appData, "McvsOffline") })
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "app_preferences.json"), "{}");
                }
                var found = Collector.FindPreferences(game, appData, Path.Combine(root, "missing"), null);
                Assert.Equal(new[] { Path.Combine(game, "app_preferences.json"), Path.Combine(appData, "Gameloft", "MCVS", "prefs", "app_preferences.json") }, found);
            }
            finally
            {
                Support.DeleteDir(root);
            }
        }

        [Fact]
        public void TheZipHasTheSummaryLogsAndResultsButNoGameFile()
        {
            string game = Support.NewTempDir("game");
            string data = Support.NewTempDir("data");
            string outDir = Support.NewTempDir("out");
            try
            {
                // A stand-in game folder with a stand-in DLL that must never reach the zip.
                File.WriteAllText(Path.Combine(game, SteamLocator.ExeName), "stand-in exe");
                File.WriteAllText(Path.Combine(game, "Game.dll"), "stand-in dll");
                Directory.CreateDirectory(Path.Combine(game, "Data"));
                File.WriteAllText(Path.Combine(game, "Data", "187993782.f"), "stand-in data");
                var log = new SpikeLog(data, echo: false);
                ArgsTxtStep.Install(game, data, ManagedArgs.Build(new Settings(), "mcvsdevice", 51780, null), log);
                Directory.CreateDirectory(Path.Combine(data, "logs"));
                File.WriteAllText(Path.Combine(data, "logs", "server.log"),
                                  "2026-10-28 20:00:00.000 INFO  [http] #1 GET /config/x -> 200 [eve.config] 812 B 1.0 ms\n");
                Directory.CreateDirectory(Path.Combine(data, "telemetry"));
                File.WriteAllLines(Path.Combine(data, "telemetry", "telemetry-1.csv"), new[] { TelemetryCsv.Header, "2026-10-28 20:00:05,5,1,900.0,1500.0,,,,,1,1," });
                File.WriteAllText(Path.Combine(data, RedirectStep.MarkerFileName), EngineBridgeMarkerPatch.MarkerHeader + "\r\nlocation=x\r\n");
                File.WriteAllText(Path.Combine(data, "settings.json"), "{ \"secret\": true }");

                var options = SpikeOptions.Parse(new[] { "collect", "--data", data });
                string zipPath = Collector.Run(options, new GameLocation(game, "test"), log, false, outDir);
                Assert.StartsWith(outDir, zipPath);
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
                    output.WriteLine(string.Join("\n", names));
                    Assert.Contains("summary.txt", names);
                    Assert.Contains("pc-spike.log", names);
                    Assert.Contains("server-logs/server.log", names);
                    Assert.Contains("telemetry/telemetry-1.csv", names);
                    Assert.Contains(RedirectStep.MarkerFileName, names);
                    Assert.Contains("game-folder/args.txt", names);
                    Assert.Contains("game-folder/listing.txt", names);
                    Assert.DoesNotContain(names, n => n.EndsWith(".dll") || n.EndsWith(".exe") || n.EndsWith(".f") || n.EndsWith("settings.json"));

                    string summary = new StreamReader(zip.GetEntry("summary.txt").Open()).ReadToEnd();
                    output.WriteLine(summary);
                    Assert.Contains("1 requests, 1 routes", summary);
                    Assert.Contains("eve.config", summary);
                    Assert.Contains("MARKER WRITTEN", summary);
                    Assert.Contains("peak private 900 MB, peak virtual 1500 MB", summary);
                    Assert.Contains("args.txt: present, with the MCVS Offline block", summary);
                    Assert.Contains("RESULT install folder writable: yes", summary);
                }
            }
            finally
            {
                Support.DeleteDir(game);
                Support.DeleteDir(data);
                Support.DeleteDir(outDir);
            }
        }
    }
}
