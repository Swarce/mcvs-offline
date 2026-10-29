using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using McvsOffline.Patcher.Pc;
using Xunit;

namespace McvsOffline.PcKit.Tests
{
    /// <summary>'probe' formatting with injected actions, and 'collect' contents and exclusions on a fake tree (§9.2, §12.3).</summary>
    public class ProbeAndCollectTests
    {
        private const string UrlAcl = "\r\nURL Reservations: \r\n----------------- \r\n\r\n    Reserved URL            : http://+:80/Temporary_Listen_Addresses/ \r\n" +
                                      "        User: \\Everyone\r\n\r\n    Reserved URL            : http://localhost:51750/ \r\n        User: PC\\owner\r\n";

        private const string Excluded = "\r\nProtocol tcp Port Exclusion Ranges\r\n\r\nStart Port    End Port\r\n----------    --------\r\n" +
                                        "     50000       50059     *\r\n     51701       51800\r\n\r\n* - Administered port exclusions.\r\n";

        [Fact]
        public void AGoodPcReadsGood()
        {
            using (var box = new KitSandbox())
            {
                Directory.CreateDirectory(Path.Combine(box.Root, "bcl"));
                foreach (string f in new[] { "mscorlib.dll", "System.dll", "System.Core.dll" }) File.WriteAllText(Path.Combine(box.Root, "bcl", f), "x");
                string text = string.Join("\n", Probe.Run(box.Probe, box.Game, Path.Combine(box.Root, "bcl")));
                Assert.Contains("http.sys http://localhost:51750/: ok", text);
                Assert.Contains("elevated: no", text);
                Assert.Contains("netsh not available", text);
                Assert.Contains("bind 127.0.0.1:51780: ok", text);
                Assert.Contains("install folder writable (McvsOffline\\): yes", text);
                Assert.Equal(Path.Combine(box.Game, "McvsOffline"), box.Probe.WritableAsked.Single());
                Assert.Contains("mscorlib.dll, System.dll, System.Core.dll present", text);
                Assert.Contains(".NET Framework 4 Release: 528040 (4.8)", text);
                Assert.Contains("Steam running: yes", text);
                Assert.Contains("tools that hook into games: none seen", text);
            }
        }

        [Fact]
        public void ABadPcSaysWhatIsWrong()
        {
            using (var box = new KitSandbox())
            {
                box.Probe.HttpAnswer = new ProbeOutcome(false, "Access is denied.", 5);
                box.Probe.Elevated = true;
                box.Probe.Tools["netsh http show urlacl"] = UrlAcl;
                box.Probe.Tools["netsh int ipv4 show excludedportrange protocol=tcp"] = Excluded;
                box.Probe.BindAnswer = new ProbeOutcome(false, "AddressAlreadyInUse");
                box.Probe.WritableAnswer = new ProbeOutcome(false, "UnauthorizedAccessException: denied");
                box.Probe.Release = 378389;
                box.Probe.Processes = new System.Collections.Generic.List<string> { "obs64", "Discord" };
                string text = string.Join("\n", Probe.Run(box.Probe, box.Game, Path.Combine(box.Root, "bcl")));
                Assert.Contains("ACCESS DENIED (HttpListenerException 5", text);
                Assert.Contains("YES - this probe is not representative", text);
                Assert.Contains("Reserved URL            : http://localhost:51750/", text);
                Assert.DoesNotContain("Temporary_Listen_Addresses", text);
                Assert.Contains("excluded port range covering 51750: YES 51701-51800", text);
                Assert.Contains("excluded port range covering 51780: YES 51701-51800", text);
                Assert.Contains("bind 127.0.0.1:51780: FAILED", text);
                Assert.Contains("install folder writable (McvsOffline\\): NO", text);
                Assert.Contains("MISSING mscorlib.dll, System.dll, System.Core.dll", text);
                Assert.Contains("older than 4.5.2", text);
                Assert.Contains("Steam running: no", text);
                Assert.Contains("obs64, Discord", text);
            }
        }

        [Fact]
        public void HttpSysPortInUseAndExcludedRangesParse()
        {
            Assert.Contains("port in use (is the game running?)", Probe.HttpSysText(new ProbeOutcome(false, "in use", 32)));
            Assert.Contains("port in use", Probe.HttpSysText(new ProbeOutcome(false, "exists", 183)));
            var ranges = Probe.ParseExcludedRanges(Excluded);
            Assert.Equal(2, ranges.Count);
            Assert.Equal(50000, ranges[0].Key);
            Assert.Equal(51800, ranges[1].Value);
            Assert.Equal("4.5.2", Probe.ReleaseName(379893));
            Assert.Equal("4.8.1", Probe.ReleaseName(533325));
        }

        [Fact]
        public void TheProbeCommandRunsWithoutAGameFolder()
        {
            using (var box = new KitSandbox())
            {
                Assert.Equal(0, Program.Run(box.Env, KitOptions.Parse(new[] { "probe" }, null)));
                Assert.Contains("install folder writable: unknown", box.Out.ToString());
            }
        }

        [Fact]
        public void CollectTakesOurLogsAndListsTheGamesIdFilesWithoutTheirContentsOrTheSaves()
        {
            using (var box = new KitSandbox())
            {
                string data = GameFolder.DataDir(box.Game);
                Directory.CreateDirectory(Path.Combine(data, "logs"));
                Directory.CreateDirectory(Path.Combine(data, "kit"));
                Directory.CreateDirectory(Path.Combine(data, "profiles"));
                Directory.CreateDirectory(Path.Combine(data, "accounts"));
                File.WriteAllText(Path.Combine(data, "logs", "server.log"), "server log line");
                File.WriteAllText(Path.Combine(data, "logs", "bootstrap.log"), "[boot] runtime: CLR 4");
                File.WriteAllText(Path.Combine(data, "kit", "pc-kit.log"), "kit log");
                File.WriteAllText(Path.Combine(data, "settings.json"), "{\"port\":51780}");
                File.WriteAllText(Path.Combine(data, "install.json"), "{\"format\":1}");
                File.WriteAllText(Path.Combine(data, "tokens.json"), "SECRET-TOKEN");
                File.WriteAllText(Path.Combine(data, "profiles", "p.json"), "SECRET-SAVE");
                File.WriteAllText(Path.Combine(data, "accounts", "a.json"), "SECRET-ACCOUNT");
                File.WriteAllText(Path.Combine(box.Game, PcExeConfig.FileName), "<configuration/>");
                File.WriteAllText(Path.Combine(box.Game, "args.txt"), "-McvsOfflineBegin\n-McvsOfflineEnd\n");
                File.WriteAllText(Path.Combine(box.Env.Documents, "GDID.bin"), "SECRET-GDID");
                Directory.CreateDirectory(Path.Combine(box.Env.AppData, "Gameloft", "MCVS"));
                File.WriteAllText(Path.Combine(box.Env.AppData, "Gameloft", "MCVS", "app_preferences.json"), "SECRET-PREFS");
                Directory.CreateDirectory(Path.Combine(box.Env.LocalAppData, "Gameloft"));
                File.WriteAllText(Path.Combine(box.Env.LocalAppData, "Gameloft", "game.log"), "native log");
                Directory.CreateDirectory(Path.Combine(box.Temp, "McvsOffline", "logs"));
                File.WriteAllText(Path.Combine(box.Temp, "McvsOffline", "logs", "bootstrap.log"), "fallback log");

                Assert.Equal(0, Program.Run(box.Env, box.Options("collect")));
                string zipPath = Directory.GetFiles(box.Env.Desktop, "mcvs-offline-pc-*.zip").Single();
                Assert.Equal("mcvs-offline-pc-20261102-180000.zip", Path.GetFileName(zipPath));
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    var names = zip.Entries.Select(e => e.FullName).ToList();
                    foreach (string expected in new[]
                             {
                                 "logs/server.log", "logs/bootstrap.log", "kit/pc-kit.log", "settings.json", "install.json", PcExeConfig.FileName, "args.txt",
                                 "game-folder-listing.txt", "status.txt", "probe.txt", "gameloft-files-listing.txt", "temp-logs/bootstrap.log",
                                 "gameloft-logs/game.log", "summary.txt",
                             })
                        Assert.True(names.Contains(expected), expected + " missing; zip has " + string.Join(", ", names));
                    Assert.DoesNotContain(names, n => n.Contains("profiles") || n.Contains("accounts") || n.Contains("tokens"));
                    string all = string.Concat(zip.Entries.Select(e =>
                    {
                        using (var r = new StreamReader(e.Open())) return r.ReadToEnd();
                    }));
                    Assert.DoesNotContain("SECRET", all);
                    string listing = Read(zip, "gameloft-files-listing.txt");
                    Assert.Contains("GDID.bin", listing);
                    Assert.Contains("DC.bin\tabsent", listing);
                    Assert.Contains("app_preferences.json", listing);
                    Assert.Contains("state:       PARTIAL", Read(zip, "status.txt"));   // an install.json without the bin files
                }
            }
        }

        private static string Read(ZipArchive zip, string name)
        {
            using (var r = new StreamReader(zip.GetEntry(name).Open())) return r.ReadToEnd();
        }
    }
}
