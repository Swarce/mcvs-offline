using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using McvsOffline.Patcher.Pc;
using McvsOffline.PcSpike;
using Xunit;

namespace McvsOffline.PcKit.Tests
{
    /// <summary>
    /// The kit's capture fixes after WR2 (F9): event times in UTC and ANSI decoding, the kit's own SHA-256 in every record,
    /// the MyLocalStorage search, GPU/display/memory/firewall facts in the probe, and the game's own GameVersion and core.dmp
    /// (reported with size and time, never hashed or zipped).
    /// </summary>
    public class CaptureFixTests
    {
        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "McvsOffline.sln"))) dir = Path.GetDirectoryName(dir);
            return dir;
        }

        private static string KitSha()
        {
            return GameFolder.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(KitSandbox.KitFileText)));
        }

        // ------------------------------------------------------------------ ANSI decoding (DBWIN, wevtutil)

        [Fact]
        public void AnsiCodePage1252DecodesWhatTheGameAndWevtutilWrite()
        {
            // "Informações" and "Referência" as cp1252 bytes: WR2 showed them as "Informaþ§es" (read as cp850) and "Refer�ncia" (UTF-8).
            byte[] info = { 0x49, 0x6E, 0x66, 0x6F, 0x72, 0x6D, 0x61, 0xE7, 0xF5, 0x65, 0x73 };
            byte[] reference = { 0x52, 0x65, 0x66, 0x65, 0x72, 0xEA, 0x6E, 0x63, 0x69, 0x61 };
            Encoding ansi = AnsiCodePage.ForCodePage(1252);
            Assert.Equal(1252, ansi.CodePage);
            Assert.Equal("Informações", ansi.GetString(info));
            Assert.Equal("Referência", ansi.GetString(reference));
            Assert.Contains('\uFFFD', Encoding.UTF8.GetString(reference));            // what the capture did before
            Assert.Equal("Referência", AnsiCodePage.ForCodePage(0).GetString(reference)); // Latin-1 off Windows: no U+FFFD
            Assert.NotNull(AnsiCodePage.Current);
            if (!OperatingSystem.IsWindows()) Assert.Equal(Encoding.Latin1.CodePage, AnsiCodePage.Current.CodePage);
        }

        [Fact]
        public void TheDebugCaptureDecodesWithTheAnsiCodePage()
        {
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "pc-spike", "DebugOutputCapture.cs"));
            Assert.Contains("AnsiCodePage.Current.GetString(bytes, 0, length)", source);
            Assert.DoesNotContain("Encoding.UTF8.GetString", source);
        }

        // ------------------------------------------------------------------ Windows events as XML, times in UTC

        private const string EventsXml =
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Application Error'/>" +
            "<EventID Qualifiers='0'>1000</EventID><Level>2</Level><TimeCreated SystemTime='2026-10-29T18:22:25.8186123Z'/></System>" +
            "<EventData><Data Name='AppName'>ModernCombatVersus.exe</Data><Data Name='ExceptionCode'>e0434352</Data><Data>Informações extras</Data></EventData></Event>\r\n" +
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Application Error'/>" +
            "<EventID>1000</EventID><Level>2</Level><TimeCreated SystemTime='2026-10-29T17:00:00.0000000Z'/></System>" +
            "<EventData><Data Name='AppName'>notepad.exe</Data></EventData></Event>\r\n" +
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Windows Error Reporting'/>" +
            "<EventID>1001</EventID><Level>4</Level><TimeCreated SystemTime='2026-10-29T17:57:52.1Z'/></System>" +
            "<EventData><Data>RADAR_PRE_LEAK_WOW64</Data><Data>ModernCombatVersus.exe</Data></EventData></Event>";

        [Fact]
        public void EventsAreTheXmlOnesNamingTheGameWithUtcTimes()
        {
            List<XElement> events = Collect.MatchingEvents(EventsXml, "ModernCombatVersus");
            Assert.Equal(2, events.Count);
            string text = Collect.FormatEvents(events);
            Assert.Contains("Event[0]\r\n  Time (UTC): 2026-10-29 18:22:25.818Z\r\n  Source: Application Error\r\n  Event ID: 1000\r\n  Level: 2\r\n", text);
            Assert.Contains("  AppName: ModernCombatVersus.exe\r\n  ExceptionCode: e0434352\r\n  Informações extras\r\n", text);
            Assert.Contains("Event[1]\r\n  Time (UTC): 2026-10-29 17:57:52.100Z\r\n  Source: Windows Error Reporting", text);
            Assert.Contains("  RADAR_PRE_LEAK_WOW64\r\n", text);
            Assert.DoesNotContain("notepad", text);
            Assert.Empty(Collect.MatchingEvents("", "x"));
            Assert.Empty(Collect.MatchingEvents("<Event><broken", "x"));
        }

        [Fact]
        public void CollectAsksWevtutilForXmlInTheAnsiCodePage()
        {
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "pc-kit", "Collect.cs"));
            Assert.Contains("/f:xml", source);
            Assert.DoesNotContain("/f:text", source);
            Assert.Contains("StandardOutputEncoding = AnsiCodePage.Current", source);
        }

        // ------------------------------------------------------------------ probe: GPU, displays, memory, firewall

        private const string Firewall =
            "\r\nRule Name:                            Modern Combat Versus\r\n----------------------------------------------------------------------\r\n" +
            "Enabled:                              Yes\r\nDirection:                            In\r\nProfiles:                             Private\r\n" +
            "Protocol:                             UDP\r\nProgram:                              C:\\Steam\\steamapps\\common\\Modern Combat Versus\\ModernCombatVersus.exe\r\n" +
            "Action:                               Allow\r\n\r\n" +
            "Rule Name:                            Other\r\n----------------------------------------------------------------------\r\n" +
            "Enabled:                              Yes\r\nProgram:                              C:\\other.exe\r\nAction:                               Block\r\n\r\n" +
            "Nome da regra:                        Modern Combat Versus\r\n----------------------------------------------------------------------\r\n" +
            "Habilitada:                           Sim\r\nPrograma:                             C:\\Jogos\\ModernCombatVersus.exe\r\nAção:                                 Bloquear\r\n\r\nOk.\r\n";

        [Fact]
        public void TheProbeRecordsGpusDisplaysMemoryAndTheGamesFirewallRules()
        {
            using (var box = new KitSandbox())
            {
                box.Probe.Gpus = new List<GpuAdapter>
                {
                    new GpuAdapter { Name = "Intel(R) HD Graphics", DedicatedBytes = 128L << 20, DriverVersion = "20.19.15.5107", DriverDate = "9-26-2019" },
                    new GpuAdapter { Name = "NVIDIA GeForce GTX 1650", DedicatedBytes = 4096L << 20, DriverVersion = "31.0.15.3623" },
                };
                box.Probe.Screens = new List<DisplayMode> { new DisplayMode { Device = @"\\.\DISPLAY1", Adapter = "Intel(R) HD Graphics", Primary = true, Width = 1920, Height = 1080, RefreshHz = 60 } };
                box.Probe.Memory = 16L << 30;
                box.Probe.Tools["netsh " + Probe.FirewallArguments] = Firewall;
                string text = string.Join("\n", Probe.Run(box.Probe, box.Game, null));
                Assert.Contains("GPU adapter: Intel(R) HD Graphics, dedicated memory 128 MB, driver 20.19.15.5107 (9-26-2019)", text);
                Assert.Contains("GPU adapter: NVIDIA GeForce GTX 1650, dedicated memory 4096 MB, driver 31.0.15.3623\n", text);
                Assert.Contains(@"display: \\.\DISPLAY1 (primary) 1920x1080 at 60 Hz on Intel(R) HD Graphics", text);
                Assert.Contains("physical memory: 16384 MB", text);
                Assert.Contains("firewall rules naming ModernCombatVersus.exe: 2", text);
                Assert.Contains("  Rule Name: Modern Combat Versus | Enabled: Yes | Direction: In | Profiles: Private | Protocol: UDP | Program: C:\\Steam", text);
                Assert.Contains("  Nome da regra: Modern Combat Versus | Habilitada: Sim | Programa: C:\\Jogos\\ModernCombatVersus.exe | Ação: Bloquear", text);
                Assert.DoesNotContain("other.exe", text);
            }
        }

        [Fact]
        public void TheProbeSaysWhatItCouldNotRead()
        {
            using (var box = new KitSandbox())
            {
                string text = string.Join("\n", Probe.Run(box.Probe, box.Game, null));
                Assert.Contains("GPU adapters: unknown", text);
                Assert.Contains("displays: unknown", text);
                Assert.Contains("physical memory: unknown", text);
                Assert.Contains("firewall rules naming ModernCombatVersus.exe: netsh not available", text);
                box.Probe.Gpus = new List<GpuAdapter> { new GpuAdapter() };
                box.Probe.Screens = new List<DisplayMode>();
                box.Probe.Tools["netsh " + Probe.FirewallArguments] = "No rules match the specified criteria.\r\n";
                text = string.Join("\n", Probe.Run(box.Probe, box.Game, null));
                Assert.Contains("GPU adapter: (no name), dedicated memory unknown, driver ?", text);
                Assert.Contains("displays: none attached", text);
                Assert.Contains("firewall rules naming ModernCombatVersus.exe: none", text);
                Assert.Equal("x at the hardware default rate", Probe.DescribeDisplay(new DisplayMode { Device = "x", RefreshHz = 1 }).Replace(" 0x0", ""));
            }
        }

        [Fact]
        public void RegistryMemorySizesOfEveryShapeRead()
        {
            Assert.Equal(4096L << 20, RealProbeActions.MemorySize(4096L << 20));
            Assert.Equal(3221225472L, RealProbeActions.MemorySize(unchecked((int)3221225472u)));   // a REG_DWORD above 2 GB
            Assert.Equal(128L << 20, RealProbeActions.MemorySize(BitConverter.GetBytes((uint)(128 << 20))));
            Assert.Equal(8L << 30, RealProbeActions.MemorySize(BitConverter.GetBytes(8L << 30)));
            Assert.Null(RealProbeActions.MemorySize("no"));
            Assert.Null(RealProbeActions.MemorySize(null));
        }

        // ------------------------------------------------------------------ the kit's own SHA-256

        [Fact]
        public void EveryLogHeaderAndTheSummaryNameTheKitsSha256()
        {
            using (var box = new KitSandbox())
            {
                Assert.Equal("kit " + KitServer.Version() + ", sha256 " + KitSha(), KitServer.Identity(box.Env));
                Assert.Equal(0, Program.Run(box.Env, box.Options("probe")));
                Assert.Equal(0, Program.Run(box.Env, box.Options("collect")));
                string log = File.ReadAllText(Directory.GetFiles(Path.Combine(box.Temp, "McvsOffline", "kit"), KitLog.FileName).Single());
                Assert.Contains("== probe (kit " + KitServer.Version() + ", sha256 " + KitSha() + ")", log);
                Assert.Contains("== collect (kit " + KitServer.Version() + ", sha256 " + KitSha() + ")", log);
                using (ZipArchive zip = ZipFile.OpenRead(Directory.GetFiles(box.Env.Desktop, "mcvs-offline-pc-*.zip").Single()))
                {
                    string summary = Read(zip, "summary.txt");
                    Assert.StartsWith("MCVS Offline PC collect, 2026-11-02 18:00:00, kit " + KitServer.Version() + ", sha256 " + KitSha() + "\r\n", summary);
                }
                box.Env.KitPath = () => Path.Combine(box.Root, "missing.exe");
                Assert.EndsWith(", sha256 unknown", KitServer.Identity(box.Env));
                box.Env.KitPath = null;
                Assert.EndsWith(", sha256 unknown", KitServer.Identity(box.Env));
                Assert.NotNull(KitServer.OwnPath());
            }
        }

        // ------------------------------------------------------------------ MyLocalStorage, core.dmp, GameVersion

        [Fact]
        public void CollectListsMyLocalStorageAndReportsTheCrashDumpWithoutZippingIt()
        {
            using (var box = new KitSandbox())
            {
                string save = Path.Combine(box.Env.AppData, "ModernCombatVersus", "gameloft");
                Directory.CreateDirectory(save);
                File.WriteAllText(Path.Combine(save, "MyLocalStorage"), "{\"Performance\":2,\"SECRET\":1}");
                File.WriteAllText(Path.Combine(save, "MyLocalStorage.bak"), "old");
                File.WriteAllText(Path.Combine(save, "unrelated.json"), "{}");
                File.WriteAllText(Path.Combine(box.Game, GameFolder.CrashDumpName), "");
                File.SetLastWriteTimeUtc(Path.Combine(box.Game, GameFolder.CrashDumpName), new DateTime(2026, 10, 29, 18, 22, 25, DateTimeKind.Utc));
                File.WriteAllText(Path.Combine(box.Game, "GameVersion"), "6372049");

                List<string> found = Collect.FindLocalStorage(box.Game, box.Env);
                Assert.Equal(new[] { "MyLocalStorage", "MyLocalStorage.bak" }, found.Select(Path.GetFileName));

                Assert.Equal(0, Program.Run(box.Env, box.Options("collect")));
                using (ZipArchive zip = ZipFile.OpenRead(Directory.GetFiles(box.Env.Desktop, "mcvs-offline-pc-*.zip").Single()))
                {
                    Assert.DoesNotContain(zip.Entries, e => e.Name.Equals(GameFolder.CrashDumpName, StringComparison.OrdinalIgnoreCase));
                    Assert.Null(zip.GetEntry("app_preferences-locations.txt"));
                    string locations = Read(zip, "local-storage-locations.txt");
                    Assert.Contains(Path.Combine(save, "MyLocalStorage") + "\t", locations);
                    Assert.DoesNotContain("unrelated", locations);
                    Assert.Contains("MyLocalStorage", Read(zip, "gameloft-files-listing.txt"));
                    string all = string.Concat(zip.Entries.Select(e =>
                    {
                        using (var r = new StreamReader(e.Open())) return r.ReadToEnd();
                    }));
                    Assert.DoesNotContain("SECRET", all);   // listed, never read
                    string summary = Read(zip, "summary.txt");
                    Assert.Contains("the game's own crash dump: core.dmp, 0 bytes, written 2026-10-29 18:22:25 UTC (not included)", summary);
                    Assert.Contains("MyLocalStorage files (the in-game options; listed, not read): 2", summary);
                    string listing = Read(zip, "game-folder-listing.txt");
                    Assert.Contains("core.dmp\tfile\t0\t2026-10-29 18:22:25\tnot-hashed", listing);
                    string status = Read(zip, "status.txt");
                    Assert.Contains("written by the game, not ours: core.dmp, GameVersion", status);
                    Assert.Contains("the game's own crash dump: core.dmp, 0 bytes, written 2026-10-29 18:22:25 UTC", status);
                    Assert.DoesNotContain("other files, not ours", status);
                }
            }
        }

        [Fact]
        public void TheListingDiffCallsGameVersionAndTheCrashDumpTheGamesOwn()
        {
            Assert.Equal(GameFolderOwner.Game, GameFolder.Classify("GameVersion"));
            Assert.Equal(GameFolderOwner.Game, GameFolder.Classify("CORE.DMP"));
            var t0 = new DateTime(2026, 10, 29, 18, 22, 25, DateTimeKind.Utc);
            var before = new List<GameFolderEntry> { new GameFolderEntry { Name = "core.dmp", Size = 0, LastWriteUtc = t0 } };
            var same = GameFolder.ParseListing(GameFolder.FormatListing("g", before));
            Assert.Null(same.Single().Sha256);
            Assert.Empty(GameFolder.DiffListings(before, same));
            var after = new List<GameFolderEntry>
            {
                new GameFolderEntry { Name = "core.dmp", Size = 0, LastWriteUtc = t0.AddHours(1) },   // a new crash, the same 0 bytes
                new GameFolderEntry { Name = "GameVersion", Size = 7, Sha256 = "ab" },
            };
            List<string> diff = GameFolder.DiffListings(before, after);
            Assert.Contains("changed\tcore.dmp\tthe game's own crash dump (0 bytes, written 2026-10-29 19:22:25 UTC; not ours, never collected)", diff);
            Assert.Contains("added\tGameVersion\tthe game's own (written by the game, not ours)", diff);
        }

        private static string Read(ZipArchive zip, string name)
        {
            using (var r = new StreamReader(zip.GetEntry(name).Open())) return r.ReadToEnd();
        }
    }
}
