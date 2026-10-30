using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Patches;
using McvsOffline.Patcher.Pc;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// A made-up install in a temp folder, without the Steam files: the four game DLLs and the exe are a few bytes each, our bin
    /// copies other bytes, install.json records both (written 2026-11-02T18:00:00Z) and the config is ours, so <c>status</c> says
    /// "installed, current" before any start. <see cref="Temp"/> stands in for the player's %TEMP%. Deleted on Dispose.
    /// </summary>
    internal sealed class FakePcInstall : IDisposable
    {
        public static readonly DateTime Installed = new DateTime(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc);

        public FakePcInstall()
        {
            Root = TestEnvironment.NewTempDir("pc-fake-install");
            Game = Path.Combine(Root, "Modern Combat Versus");
            Temp = Path.Combine(Root, "Temp");
            Directory.CreateDirectory(Path.Combine(Game, "Data"));
            Directory.CreateDirectory(GameFolder.BinDir(Game));
            Directory.CreateDirectory(Temp);
            File.WriteAllText(Path.Combine(Game, "Data", "MCVS.game"), "game");
            var manifest = new InstallManifest
            {
                InstallerName = "tests", InstallerVersion = "0.0.0-test", ServerBuild = "16", WrittenUtc = "2026-11-02T18:00:00Z", BuildId = "6372049",
                ExeSha256 = Write(GameFolder.ExeName, "stock exe"), Port = 51780,
            };
            var codeBases = new List<PcCodeBase>();
            foreach (string file in GameFolder.PatchedFiles.Concat(new[] { GameFolder.ServerFile }))
            {
                if (file != GameFolder.ServerFile)
                    manifest.Originals.Add(new InstallOriginal { File = file, Sha256 = Write(file, Stock(file)), Mvid = Guid.Empty });
                string bin = GameFolder.BinRelative(file);
                manifest.Written.Add(new InstallWritten { Path = bin, Sha256 = Write(bin, Ours(file)), Version = new Version(1, 0, 0, 0) });
                codeBases.Add(new PcCodeBase(Path.GetFileNameWithoutExtension(file), new Version(1, 0, 0, 0), bin));
            }
            manifest.Written.Add(new InstallWritten
            {
                Path = PcExeConfig.FileName, Sha256 = GameFolder.Sha256(PcExeConfig.Encode(PcExeConfig.Build(codeBases))),
            });
            File.WriteAllBytes(Path.Combine(Game, PcExeConfig.FileName), PcExeConfig.Encode(PcExeConfig.Build(codeBases)));
            manifest.Patches.AddRange(new[] { "hook-pc", "gameload", "store" });
            File.WriteAllBytes(Path.Combine(GameFolder.DataDir(Game), InstallManifest.FileName), manifest.ToBytes());
        }

        public string Root { get; }

        public string Game { get; }

        /// <summary>The fake %TEMP% (the host's fallback logs, hook-pc's marker).</summary>
        public string Temp { get; }

        public string ConfigPath
        {
            get { return Path.Combine(Game, PcExeConfig.FileName); }
        }

        public string GameLog
        {
            get { return Path.Combine(GameFolder.DataDir(Game), "logs", GameFolder.BootstrapLogName); }
        }

        public string TempLog
        {
            get { return Path.Combine(Temp, GameFolder.DataDirName, "logs", GameFolder.BootstrapLogName); }
        }

        public string Marker
        {
            get { return GameFolder.HookMarker(Temp); }
        }

        public static string Stock(string file)
        {
            return "the game's own " + file;
        }

        public static string Ours(string file)
        {
            return "MCVS Offline's copy of " + file;
        }

        /// <summary>Writes a file relative to the game folder ('/'), returns its SHA-256.</summary>
        public string Write(string relative, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            File.WriteAllBytes(GameFolder.Resolve(Game, relative), bytes);
            return GameFolder.Sha256(bytes);
        }

        /// <summary>A file the stock game writes in its folder (dr0.bin, OF_PP.bin, …), last written at <paramref name="utc"/>.</summary>
        public void GameWrote(string name, DateTime utc)
        {
            string path = Path.Combine(Game, name);
            File.WriteAllText(path, "written by the game");
            File.SetLastWriteTimeUtc(path, utc);
        }

        public void Log(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        public PcStatusReport Status()
        {
            return PcInstaller.Status(Game, () => false, null, Temp);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// docs/design/pc-port.md §7.6: what <c>status</c> says when no start of our host is recorded since the install (build 14's
    /// "game fails to connect": the kit said "installed, current" while the game ran the stock files). NOT VERIFIED without
    /// evidence; NOT ACTIVE when a file the stock game writes is newer than the install and than our host's last line, or when
    /// hook-pc's marker says our EngineBridge ran but no log followed. Both bootstrap.log files count (the game folder's and the
    /// %TEMP% fallback). On <see cref="FakePcInstall"/>: runs without the Steam files.
    /// </summary>
    public class PcActivationEvidenceTests
    {
        private static readonly DateTime After = FakePcInstall.Installed.AddMinutes(30);
        private static readonly DateTime Before = FakePcInstall.Installed.AddHours(-2);

        private const string AllYes = "patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes, store yes";

        private readonly ITestOutputHelper output;

        public PcActivationEvidenceTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private List<string> Print(PcStatusReport status)
        {
            List<string> text = PcReportText.Status(status);
            foreach (string line in text) output.WriteLine(line);
            return text;
        }

        [Fact]
        public void NoStartSinceTheInstallIsNotVerified()
        {
            using (var fake = new FakePcInstall())
            {
                PcStatusReport status = fake.Status();
                List<string> text = Print(status);
                Assert.Equal(PcInstallState.Current, status.State);
                Assert.Equal(PcInstaller.NotVerified, status.Activation);
                Assert.Contains("activation:  NOT VERIFIED: no start of MCVS Offline since the install (start the game once, then run status)", text);
                Assert.Empty(status.Errors);
                Assert.Null(status.HookEvidence);

                // A start and the game's own files from before the install change nothing: they belong to another install.
                fake.Log(fake.GameLog, PcBootActivationTests.Start("2026-11-02T16:00:00", true, AllYes));
                fake.GameWrote("dr0.bin", Before);
                fake.GameWrote("core.dmp", FakePcInstall.Installed);   // the same second as the install: not after it
                status = fake.Status();
                Assert.Equal(PcInstallState.Current, status.State);
                Assert.Equal(PcInstaller.NotVerified, status.Activation);
                Assert.Equal(fake.GameLog, status.BootstrapLog);
            }
        }

        [Fact]
        public void AGameFileNewerThanTheInstallWithoutAStartOfOursIsNotActive()
        {
            using (var fake = new FakePcInstall())
            {
                fake.Log(fake.GameLog, PcBootActivationTests.Start("2026-11-02T16:00:00", true, AllYes));   // the last start of ours: before
                fake.GameWrote("dr0.bin", After);
                fake.GameWrote("OF_PP.bin", After.AddMinutes(-5));
                PcStatusReport status = fake.Status();
                List<string> text = Print(status);
                Assert.Equal(PcInstallState.NotActive, status.State);
                Assert.Equal("installed, NOT ACTIVE (the game started without MCVS Offline)", status.StateDetail);
                Assert.Equal("NOT ACTIVE: the game started without MCVS Offline; Windows did not use " + fake.ConfigPath, status.Activation);
                Assert.Contains("activation:  NOT ACTIVE: the game started without MCVS Offline; Windows did not use " + fake.ConfigPath, text);
                Assert.Contains("dr0.bin written by the game at 2026-11-02T18:30:00Z, after the install (2026-11-02T18:00:00Z); no start of MCVS Offline since",
                                status.Problems);
                Assert.Contains(status.Errors, e => e.Contains("Windows did not use " + fake.ConfigPath));
                Assert.Equal("no hook.txt in %TEMP%\\McvsOffline: MCVS Offline's EngineBridge has never run as this user", status.HookEvidence);
                Assert.Contains("hook:        " + status.HookEvidence, text);
                Assert.DoesNotContain(text, l => l.Contains("installed, current"));

                // With a marker from before the install: it says when our EngineBridge last ran.
                fake.Log(fake.Marker, "2026-11-01T10:00:00.000Z hook-pc start pid 4242 exe C:\\Games\\ModernCombatVersus.exe dll C:\\Games\\McvsOffline\\bin\\EngineBridge.dll\r\n");
                status = fake.Status();
                Assert.Equal(PcInstallState.NotActive, status.State);
                Assert.Equal("hook.txt: the last start of MCVS Offline's EngineBridge was at 2026-11-01T10:00:00Z, before the install: it has not run since",
                             status.HookEvidence);
                Assert.Equal(fake.Marker, status.HookMarkerPath);

                // verify fails on it (it starts from status).
                PcInstallReport verify = PcInstaller.Verify(fake.Game, null, () => false, null, fake.Temp);
                Assert.Equal(PcExitCodes.Io, verify.ExitCode);
                Assert.Contains("NOT ACTIVE (the game started without MCVS Offline)", verify.Error);
            }
        }

        [Fact]
        public void AStartOfOursAfterTheInstallIsJudgedByItsLinesEvenWhenTheGameWroteLater()
        {
            using (var fake = new FakePcInstall())
            {
                fake.Log(fake.GameLog, PcBootActivationTests.Start("2026-11-02T18:05:00", true, AllYes));
                fake.GameWrote("dr0.bin", After);   // the game writes its files while our host runs: no evidence against it
                PcStatusReport status = fake.Status();
                Print(status);
                Assert.Equal(PcInstallState.Current, status.State);
                Assert.StartsWith("active at the last start (2026-11-02T18:05:00.425Z)", status.Activation);
                Assert.Null(status.HookEvidence);

                // A line of our host after the install without the assemblies/patches lines: unknown, not NOT VERIFIED.
                fake.Log(fake.GameLog, "2026-11-02T18:07:00.100Z INFO  [embedded] MCVS Offline 0.1.0-dev embedded host starting (platform windows)\r\n");
                status = fake.Status();
                Assert.Equal(PcInstallState.Current, status.State);
                Assert.StartsWith("unknown: MCVS Offline started at 2026-11-02T18:07:00Z but logged no [boot] assemblies:/patches: lines", status.Activation);
            }
        }

        [Fact]
        public void TheTempFallbackLogCountsAndTheNewerLogIsJudged()
        {
            using (var fake = new FakePcInstall())
            {
                // Only the %TEMP% log, after the install, from bin: active.
                fake.Log(fake.TempLog, PcBootActivationTests.Start("2026-11-02T18:20:00", true, AllYes));
                fake.GameWrote("dr0.bin", After);
                PcStatusReport status = fake.Status();
                Print(status);
                Assert.Equal(PcInstallState.Current, status.State);
                Assert.Equal(fake.TempLog, status.BootstrapLog);
                Assert.StartsWith("active at the last start (2026-11-02T18:20:00.425Z)", status.Activation);

                // An older game-folder log does not hide it; a newer one wins.
                fake.Log(fake.GameLog, PcBootActivationTests.Start("2026-11-02T16:00:00", false, AllYes));
                Assert.Equal(fake.TempLog, fake.Status().BootstrapLog);
                fake.Log(fake.GameLog, PcBootActivationTests.Start("2026-11-02T18:40:00", false, AllYes));
                status = fake.Status();
                Assert.Equal(fake.GameLog, status.BootstrapLog);
                Assert.Equal(PcInstallState.NotActive, status.State);
                Assert.Equal("installed, NOT ACTIVE (Game loaded from the game folder)", status.StateDetail);

                // Without a %TEMP% (the three-argument Status): only the game folder's log.
                File.Delete(fake.GameLog);
                PcStatusReport noTemp = PcInstaller.Status(fake.Game, () => false, null);
                Assert.Null(noTemp.BootstrapLog);
                Assert.Equal(PcInstallState.NotActive, noTemp.State);   // dr0.bin after the install, no log of ours read
            }
        }

        [Fact]
        public void TheMarkerSaysOurEngineBridgeRanButTheServerDidNotStart()
        {
            using (var fake = new FakePcInstall())
            {
                fake.GameWrote("OF_PP.bin", After);
                fake.Log(fake.Marker,
                         "2026-11-02T18:29:00.000Z hook-pc start pid 4242 exe C:\\Games\\ModernCombatVersus.exe dll C:\\Games\\McvsOffline\\bin\\EngineBridge.dll\r\n" +
                         "2026-11-02T18:29:00.050Z hook-pc McvsOfflineInit failed (pid 4242): System.IO.FileNotFoundException: Could not load file or assembly " +
                         "'McvsOffline.Server, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null'\r\n" +
                         "   at EngineBridge.Class1.McvsOfflineInit()\r\n");
                PcStatusReport status = fake.Status();
                List<string> text = Print(status);
                Assert.Equal(PcInstallState.NotActive, status.State);
                Assert.Equal("installed, NOT ACTIVE (the server did not start)", status.StateDetail);
                Assert.Equal("NOT ACTIVE: MCVS Offline's EngineBridge ran at 2026-11-02T18:29:00Z but the server did not start: McvsOfflineInit failed (pid 4242): " +
                             "System.IO.FileNotFoundException: Could not load file or assembly 'McvsOffline.Server, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null'",
                             status.Activation);
                Assert.StartsWith("hook.txt: our EngineBridge ran at 2026-11-02T18:29:00Z (start pid 4242 exe ", status.HookEvidence);
                Assert.Contains(status.Errors, e => e.StartsWith("MCVS Offline's EngineBridge ran (" + fake.Marker + ") but its server did not start", StringComparison.Ordinal));
                Assert.DoesNotContain(text, l => l.Contains("Windows did not use"));

                // Without the failure line (a crash before the catch): still NOT ACTIVE, "no exception recorded".
                fake.Log(fake.Marker, "2026-11-02T18:29:00.000Z hook-pc start pid 4242 exe x dll y\r\n");
                Assert.EndsWith("but the server did not start: no exception recorded", fake.Status().Activation);
            }
        }

        [Fact]
        public void TheMarkerIsReadEntryByEntry()
        {
            List<PcHookEntry> entries = PcHookMarker.Read(
                "2026-11-02T18:29:00.000Z hook-pc start pid 1 exe a dll b\r\n" +
                "2026-11-02T18:29:00.050Z hook-pc McvsOfflineInit failed (pid 1): System.Exception: boom\r\n" +
                "   at Somewhere()\r\n" +
                "garbage\n" +
                "2026-11-02T19:00:00.000Z hook-pc start pid 2 exe a dll b\n" +
                "2026-11-02T19:00:01.000Z something else\n");
            Assert.Equal(3, entries.Count);
            Assert.False(entries[0].Failed);
            Assert.Equal("start pid 1 exe a dll b", entries[0].Text);
            Assert.True(entries[1].Failed);
            Assert.Equal("McvsOfflineInit failed (pid 1): System.Exception: boom", entries[1].Text);
            Assert.Equal(new DateTime(2026, 11, 2, 18, 29, 0, 50, DateTimeKind.Utc), entries[1].Utc);
            Assert.Equal(DateTimeKind.Utc, entries[0].Utc.Kind);
            Assert.Equal("start pid 2 exe a dll b", PcHookMarker.LastStart(entries, FakePcInstall.Installed).Text);
            Assert.Null(PcHookMarker.LastStart(entries, new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)));
            Assert.Null(PcHookMarker.LastFailure(entries, new DateTime(2026, 11, 2, 18, 30, 0, DateTimeKind.Utc)));
            Assert.Empty(PcHookMarker.Read(null));
            Assert.Empty(PcHookMarker.Read(""));
            // The marker's own constants: what the patch writes is what this reads.
            Assert.Equal(24, EngineBridgeHookPatch.MarkerTimeFormat.Replace("'", "").Length);
            Assert.StartsWith(" hook-pc ", EngineBridgeHookPatch.MarkerStart);
            Assert.StartsWith(" hook-pc ", EngineBridgeHookPatch.MarkerFailed);
        }

        [Fact]
        public void TheLastLineOfALogIsItsNewestRecord()
        {
            Assert.Equal(new DateTime(2026, 11, 2, 18, 5, 0, 971, DateTimeKind.Utc), PcBootActivation.LastLineUtc(PcBootActivationTests.Start("2026-11-02T18:05:00", true, AllYes)));
            Assert.Equal(new DateTime(2026, 11, 2, 18, 5, 0, DateTimeKind.Utc), PcBootActivation.LastLineUtc("2026-11-02T18:05:00.000Z a\n   at b\n\n"));
            Assert.Null(PcBootActivation.LastLineUtc("no time here\n"));
            Assert.Null(PcBootActivation.LastLineUtc(null));
            Assert.Equal(DateTimeKind.Utc, PcBootActivation.ParseUtc("2026-11-02T18:00:00Z").Value.Kind);
            Assert.Equal(FakePcInstall.Installed, PcBootActivation.ParseUtc("2026-11-02T18:00:00Z"));
            Assert.Null(PcBootActivation.ParseUtc("not a time"));
        }

        /// <summary>
        /// The build-14 owner's work-around: McvsOffline\bin copied next to the exe. Status calls it a hand copy (not "Steam updated
        /// the game") and says how to undo it; a Steam update of the same file keeps the STALE advice; the server's copy is a leftover.
        /// </summary>
        [Fact]
        public void AHandCopyOfBinIsNamedAndUndoneThroughSteam()
        {
            using (var fake = new FakePcInstall())
            {
                foreach (string file in GameFolder.PatchedFiles.Concat(new[] { GameFolder.ServerFile })) fake.Write(file, FakePcInstall.Ours(file));
                PcStatusReport status = fake.Status();
                List<string> text = Print(status);
                Assert.Equal(PcInstallState.Stale, status.State);
                Assert.Equal("STALE (EngineBridge.dll, Game.dll, Gameloft.Engine.dll, Gameloft.Util.dll changed): " + PcInstaller.ManualCopyAdvice, status.StateDetail);
                Assert.EndsWith("you copied MCVS Offline's DLLs over the game's own files; Steam > Verify integrity of game files restores them, then run install",
                                status.StateDetail);
                Assert.Contains("Game.dll is MCVS Offline's copy from McvsOffline\\bin (copied over the game's own file)", status.Problems);
                Assert.DoesNotContain(text, l => l.Contains(PcInstaller.StaleAdvice));
                Assert.Contains(status.Leftovers, l => l.StartsWith("McvsOffline.Server.dll in the game folder is a hand copy of ours", StringComparison.Ordinal));

                // Steam's own update of the files: the STALE advice, no hand copy.
                foreach (string file in GameFolder.PatchedFiles) fake.Write(file, "a newer Steam build of " + file);
                File.Delete(Path.Combine(fake.Game, GameFolder.ServerFile));
                status = fake.Status();
                Assert.Equal(PcInstallState.Stale, status.State);
                Assert.EndsWith(PcInstaller.StaleAdvice, status.StateDetail);
                Assert.Contains("Game.dll differs from the one the install was made from", status.Problems);
                Assert.DoesNotContain(status.Leftovers, l => l.Contains("hand copy"));
            }
        }

        /// <summary>Install refuses a game folder holding our copies before anything is written, even with --force.</summary>
        [Fact]
        public void InstallRefusesAHandCopyEvenWithForce()
        {
            using (var fake = new FakePcInstall())
            {
                fake.Write("EngineBridge.dll", FakePcInstall.Ours("EngineBridge.dll"));
                fake.Write("Gameloft.Util.dll", FakePcInstall.Ours("Gameloft.Util.dll"));
                foreach (bool force in new[] { false, true })
                {
                    PcInstallReport report = PcInstaller.Install(new PcInstallOptions
                    {
                        GameFolder = fake.Game, ServerDll = new byte[] { 1 }, BclDirectory = fake.Temp, GameRunning = () => false, Force = force,
                    });
                    output.WriteLine(report.Error);
                    Assert.Equal(PcExitCodes.Refused, report.ExitCode);
                    Assert.Equal("EngineBridge.dll, Gameloft.Util.dll are MCVS Offline's own copies from McvsOffline\\bin, not the game's: " +
                                 PcInstaller.ManualCopyAdvice + ". Nothing was written", report.Error);
                    Assert.Empty(report.Written);
                }
                // Without install.json (an interrupted install): the files in McvsOffline\bin tell it.
                File.Delete(Path.Combine(GameFolder.DataDir(fake.Game), InstallManifest.FileName));
                PcInstallReport noManifest = PcInstaller.Install(new PcInstallOptions { GameFolder = fake.Game, ServerDll = new byte[] { 1 }, GameRunning = () => false });
                Assert.Equal(PcExitCodes.Refused, noManifest.ExitCode);
                Assert.Contains(PcInstaller.ManualCopyAdvice, noManifest.Error);
                // Another unknown file is the usual refusal.
                fake.Write("EngineBridge.dll", FakePcInstall.Stock("EngineBridge.dll"));
                fake.Write("Gameloft.Util.dll", "something else");
                PcInstallReport other = PcInstaller.Install(new PcInstallOptions { GameFolder = fake.Game, ServerDll = new byte[] { 1 }, GameRunning = () => false });
                Assert.Equal(PcExitCodes.Refused, other.ExitCode);
                Assert.StartsWith("this is not the verified Steam build 6372049", other.Error);
            }
        }

        /// <summary>The path list status reads is the kit's watch list (StallDumps reuses it).</summary>
        [Fact]
        public void TheBootstrapLogsAreTheGameFoldersAndTheTempFallback()
        {
            Assert.Equal(new[] { Path.Combine("G", "McvsOffline", "logs", "bootstrap.log"), Path.Combine("T", "McvsOffline", "logs", "bootstrap.log") },
                         GameFolder.BootstrapLogs("G", "T"));
            Assert.Equal(new[] { Path.Combine("G", "McvsOffline", "logs", "bootstrap.log") }, GameFolder.BootstrapLogs("G", null));
            Assert.Equal(Path.Combine("T", "McvsOffline", "hook.txt"), GameFolder.HookMarker("T"));
            Assert.Null(GameFolder.HookMarker(null));
        }
    }
}
