using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Patcher.Pc;
using McvsOffline.PcSpike;
using Xunit;

namespace McvsOffline.PcKit.Tests
{
    /// <summary>The kit's command line, its menu and the commands that need no real game files (docs/design/pc-port.md §9.2, §12.3).</summary>
    public class KitCommandTests
    {
        [Fact]
        public void ParsesCommandsAndOptions()
        {
            KitOptions o = KitOptions.Parse(new[] { "install", "--game", "G:\\Games\\MCVS", "--port", "51790", "--patch", "default,forum", "--dry-run", "--force" }, null);
            Assert.Equal("install", o.Command);
            Assert.Equal("G:\\Games\\MCVS", o.GameDir);
            Assert.Equal(51790, o.Port);
            Assert.Contains("forum", o.Patches);
            Assert.Contains("nativeonline", o.Patches);
            Assert.True(o.DryRun);
            Assert.True(o.Force);

            o = KitOptions.Parse(new[] { "UNINSTALL", "--remove-data", "--yes" }, "D:\\from-env");
            Assert.Equal("uninstall", o.Command);
            Assert.True(o.RemoveData && o.Yes);
            Assert.Equal("D:\\from-env", o.GameDir);   // MCVS_PC_GAME

            Assert.True(KitOptions.Parse(new[] { "status", "--listing" }, null).Listing);
            Assert.Null(KitOptions.Parse(new string[0], null).Command);   // double-click: the menu
            Assert.Equal("help", KitOptions.Parse(new[] { "--help" }, null).Command);
            Assert.Equal(new string[0], KitOptions.Parse(new[] { "install", "--patch", "none" }, null).Patches);
        }

        [Theory]
        [InlineData("frobnicate")]
        [InlineData("install", "--port", "0")]
        [InlineData("install", "--port")]
        [InlineData("install", "status")]
        [InlineData("install", "--bogus")]
        public void BadArgumentsAreUsageErrors(params string[] args)
        {
            Assert.Throws<ArgumentException>(() => KitOptions.Parse(args, null));
        }

        [Fact]
        public void TheMenuRunsTheChosenCommandsAndAsksForTheGameFolder()
        {
            using (var box = new KitSandbox())
            {
                // 3 = status, 4 = status with listing, 9 = game folder (a wrong one, then the right one), junk, 0 = quit.
                string input = "3\n4\n9\n" + Path.Combine(box.Root, "nope") + "\n\"" + box.Game + "\"\nxyz\n0\n";
                box.Env.Input = new ConsoleLines(new StringReader(input));
                var options = new KitOptions();
                var ran = new List<string>();
                int code = Program.Menu(box.Env, options, o =>
                {
                    ran.Add(o.Command + (o.Listing ? " --listing" : ""));
                    return 0;
                });
                Assert.Equal(0, code);
                Assert.Equal(new[] { "status", "status --listing" }, ran);
                Assert.Equal(box.Game, options.GameDir);
                Assert.Contains("no ModernCombatVersus.exe in", box.Out.ToString());
                Assert.Contains("0. quit", box.Out.ToString());
            }
        }

        /// <summary>A failed install from the double-click menu says INSTALL FAILED, not only its exit code; other commands keep the code.</summary>
        [Fact]
        public void TheMenuSaysInstallFailed()
        {
            using (var box = new KitSandbox())
            {
                box.Env.Input = new ConsoleLines(new StringReader("1\n3\n1\n0\n"));
                var codes = new Queue<int>(new[] { PcExitCodes.Refused, PcExitCodes.Io, PcExitCodes.Ok });
                Assert.Equal(0, Program.Menu(box.Env, new KitOptions { GameDir = box.Game }, o => codes.Dequeue()));
                string text = box.Out.ToString();
                Assert.Contains("INSTALL FAILED (exit code 3): see the error above, fix it and run install again", text);
                Assert.Contains("(finished with exit code 1)", text);   // status
                Assert.Contains("(done)", text);
                Assert.DoesNotContain("(finished with exit code 3)", text);
                Assert.Equal("(done)", Program.MenuResult("install", PcExitCodes.Ok));
            }
        }

        /// <summary>The end of a successful install: the exe and the config by full path, and "start the game once, then run status".</summary>
        [Fact]
        public void InstallEndsWithTheExeTheConfigAndWhatToCheck()
        {
            using (var box = new KitSandbox())
            {
                box.Env.Install = o => new PcInstallReport { GameFolder = o.GameFolder };
                Assert.Equal(PcExitCodes.Ok, Program.Run(box.Env, box.Options("install")));
                string text = box.Out.ToString();
                Assert.Contains("exe      " + Path.Combine(box.Game, GameFolder.ExeName), text);
                Assert.Contains("config   " + Path.Combine(box.Game, PcExeConfig.FileName) + " (Windows reads it when that exe starts)", text);
                Assert.Contains("next     start the game once, then run status: it must say activation: active", text);
            }
        }

        /// <summary>
        /// The kit's status and verify read the kit's own %TEMP% (TempRoot): hook-pc's marker there, after the install, without a log
        /// of our host, is "NOT ACTIVE: … the server did not start".
        /// </summary>
        [Fact]
        public void StatusAndVerifyReadTheMarkerInTheKitsTemp()
        {
            using (var box = new KitSandbox())
            {
                WriteInstall(box.Game, "2026-11-02T18:00:00Z");
                Assert.Equal(PcExitCodes.Ok, Program.Run(box.Env, box.Options("status")));
                Assert.Contains("activation:  " + PcInstaller.NotVerified, box.Out.ToString());

                Directory.CreateDirectory(Path.Combine(box.Temp, "McvsOffline"));
                File.WriteAllText(Path.Combine(box.Temp, "McvsOffline", "hook.txt"),
                                  "2026-11-02T18:10:00.000Z hook-pc start pid 7 exe x dll y\r\n" +
                                  "2026-11-02T18:10:00.010Z hook-pc McvsOfflineInit failed (pid 7): System.IO.FileNotFoundException: McvsOffline.Server\r\n");
                box.Out.GetStringBuilder().Clear();
                Assert.Equal(PcExitCodes.Ok, Program.Run(box.Env, box.Options("status")));
                string text = box.Out.ToString();
                Assert.Contains("state:       installed, NOT ACTIVE (the server did not start)", text);
                Assert.Contains("activation:  NOT ACTIVE: MCVS Offline's EngineBridge ran at 2026-11-02T18:10:00Z but the server did not start: " +
                                "McvsOfflineInit failed (pid 7): System.IO.FileNotFoundException: McvsOffline.Server", text);
                Assert.Equal(PcExitCodes.Io, Program.Run(box.Env, box.Options("verify")));
            }
        }

        /// <summary>A made-up install of the sandbox's game files: bin copies, install.json written at <paramref name="writtenUtc"/>, our config.</summary>
        private static void WriteInstall(string game, string writtenUtc)
        {
            Directory.CreateDirectory(GameFolder.BinDir(game));
            var manifest = new InstallManifest { InstallerName = "tests", InstallerVersion = "0", WrittenUtc = writtenUtc, Port = 51780 };
            var codeBases = new List<PcCodeBase>();
            foreach (string file in GameFolder.PatchedFiles.Concat(new[] { GameFolder.ServerFile }))
            {
                string path = GameFolder.Resolve(game, GameFolder.BinRelative(file));
                File.WriteAllText(path, "ours " + file);
                manifest.Written.Add(new InstallWritten { Path = GameFolder.BinRelative(file), Sha256 = GameFolder.TrySha256(path) });
                if (file != GameFolder.ServerFile) manifest.Originals.Add(new InstallOriginal { File = file, Sha256 = GameFolder.TrySha256(Path.Combine(game, file)) });
                codeBases.Add(new PcCodeBase(Path.GetFileNameWithoutExtension(file), new Version(1, 0, 0, 0), GameFolder.BinRelative(file)));
            }
            byte[] config = PcExeConfig.Encode(PcExeConfig.Build(codeBases));
            File.WriteAllBytes(Path.Combine(game, PcExeConfig.FileName), config);
            manifest.Written.Add(new InstallWritten { Path = PcExeConfig.FileName, Sha256 = GameFolder.Sha256(config) });
            File.WriteAllBytes(Path.Combine(GameFolder.DataDir(game), InstallManifest.FileName), manifest.ToBytes());
        }

        [Fact]
        public void TheMenuEndsWithTheInput()
        {
            using (var box = new KitSandbox())
            {
                box.Env.Input = new ConsoleLines(new StringReader("1\n"));
                int runs = 0;
                Assert.Equal(0, Program.Menu(box.Env, new KitOptions(), o => ++runs * 0));
                Assert.Equal(1, runs);
            }
        }

        [Fact]
        public void WithoutAGameFolderTheCommandsSayHowToGiveOne()
        {
            using (var box = new KitSandbox())
            {
                KitOptions o = KitOptions.Parse(new[] { "status" }, null);
                Assert.Equal(PcExitCodes.Usage, Program.Run(box.Env, o));
                Assert.Contains("--game", box.Err.ToString());
            }
        }

        [Fact]
        public void InstallRemovesItsTempFolderOnSuccessAndOnFailure()
        {
            using (var box = new KitSandbox())
            {
                string seenFolder = null;
                box.Env.Install = o =>
                {
                    seenFolder = o.ServerFolder;
                    Assert.True(File.Exists(Path.Combine(o.ServerFolder, "McvsOffline.Server.dll")));
                    Assert.StartsWith(Path.Combine(box.Temp, KitServer.TempPrefix), o.ServerFolder);
                    Assert.Equal("McvsOffline.PcKit", o.InstallerName);
                    Assert.Equal(box.Game, o.GameFolder);
                    return new PcInstallReport();
                };
                Assert.Equal(PcExitCodes.Ok, Program.Run(box.Env, box.Options("install")));
                Assert.False(Directory.Exists(seenFolder));
                Assert.Contains("Start the game from Steam", box.Out.ToString());
                Assert.Contains("Saves > Export", box.Out.ToString());

                box.Env.Install = o =>
                {
                    seenFolder = o.ServerFolder;
                    return new PcInstallReport { ExitCode = PcExitCodes.Refused, Error = "a patch site does not match" };
                };
                Assert.Equal(PcExitCodes.Refused, Program.Run(box.Env, box.Options("install")));
                Assert.False(Directory.Exists(seenFolder));
                Assert.Contains("a patch site does not match", box.Err.ToString());

                box.Env.Install = o =>
                {
                    seenFolder = o.ServerFolder;
                    throw new IOException("disk full");
                };
                Assert.Equal(PcExitCodes.Io, Program.Run(box.Env, box.Options("install")));
                Assert.False(Directory.Exists(seenFolder));
                Assert.Empty(Directory.GetDirectories(box.Temp, KitServer.TempPrefix + "*"));
            }
        }

        /// <summary>
        /// install --port n asks the installer to set settings.json "port" (the key the game's server listens on) and prints where;
        /// without --port settings.json is left alone.
        /// </summary>
        [Fact]
        public void InstallWithAPortSetsItInSettingsJson()
        {
            using (var box = new KitSandbox())
            {
                Assert.True(KitOptions.Parse(new[] { "install", "--port", "51790" }, null).PortGiven);
                Assert.False(KitOptions.Parse(new[] { "install" }, null).PortGiven);

                PcInstallOptions seen = null;
                box.Env.Install = o =>
                {
                    seen = o;
                    return new PcInstallReport { SettingsPort = o.SetSettingsPort ? o.Port : (int?)null };
                };
                Assert.Equal(PcExitCodes.Ok, Program.Run(box.Env, box.Options("install", "--port", "51790")));
                Assert.True(seen.SetSettingsPort);
                Assert.Equal(51790, seen.Port);
                Assert.Contains("port     51790 is the server port in McvsOffline\\settings.json", box.Out.ToString());

                Assert.Equal(PcExitCodes.Ok, Program.Run(box.Env, box.Options("install")));
                Assert.False(seen.SetSettingsPort);
                Assert.Equal(51780, seen.Port);
            }
        }

        [Fact]
        public void AKitWithoutItsServerDllSaysSo()
        {
            using (var box = new KitSandbox())
            {
                box.Env.ServerDll = () => null;
                Assert.Equal(PcExitCodes.Io, Program.Run(box.Env, box.Options("install")));
                Assert.Contains("built without the PC McvsOffline.Server.dll", box.Err.ToString());
            }
        }

        [Fact]
        public void StaleTempFoldersOfKilledRunsAreRemovedAtStart()
        {
            using (var box = new KitSandbox())
            {
                string old = Path.Combine(box.Temp, KitServer.TempPrefix + "old");
                string fresh = Path.Combine(box.Temp, KitServer.TempPrefix + "fresh");
                string other = Path.Combine(box.Temp, "something-else");
                foreach (string d in new[] { old, fresh, other }) Directory.CreateDirectory(d);
                Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
                Assert.Equal(1, KitServer.CleanStale(box.Temp, DateTime.UtcNow, TimeSpan.FromDays(1)));
                Assert.False(Directory.Exists(old));
                Assert.True(Directory.Exists(fresh) && Directory.Exists(other));
            }
        }

        [Fact]
        public void TheLogStartsInTempAndMovesIntoTheGameFolderOnceItExists()
        {
            using (var box = new KitSandbox())
            {
                Assert.Equal(Path.Combine(box.Temp, "McvsOffline", "kit"), Program.KitFolder(box.Env, box.Game));
                Directory.CreateDirectory(GameFolder.DataDir(box.Game));
                Assert.Equal(Path.Combine(box.Game, "McvsOffline", "kit"), Program.KitFolder(box.Env, box.Game));

                var log = new KitLog(Path.Combine(box.Temp, "a"), box.Out, box.Err);
                log.Info("one");
                log.ContinueIn(Path.Combine(box.Temp, "b"));
                log.Warn("two");
                Assert.Contains("log continues in", File.ReadAllText(Path.Combine(box.Temp, "a", KitLog.FileName)));
                Assert.Contains("WARN two", File.ReadAllText(Path.Combine(box.Temp, "b", KitLog.FileName)));
                Assert.DoesNotContain("two", File.ReadAllText(Path.Combine(box.Temp, "a", KitLog.FileName)));
                // The log never recreates a deleted folder.
                Directory.Delete(Path.Combine(box.Temp, "b"), true);
                log.Info("three");
                Assert.False(Directory.Exists(Path.Combine(box.Temp, "b")));
            }
        }

        [Fact]
        public void RemoveDataNeedsDeleteOrYes()
        {
            using (var box = new KitSandbox("no\n"))
            {
                string data = GameFolder.DataDir(box.Game);
                Directory.CreateDirectory(Path.Combine(data, "profiles"));
                File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
                Assert.Equal(PcExitCodes.Usage, Program.Run(box.Env, box.Options("uninstall", "--remove-data")));
                Assert.True(File.Exists(Path.Combine(data, "settings.json")));
                Assert.Contains("not confirmed", box.Err.ToString());

                box.Env.Input = new ConsoleLines(new StringReader("DELETE\n"));
                Assert.Equal(PcExitCodes.Ok, Program.Run(box.Env, box.Options("uninstall", "--remove-data")));
                Assert.False(Directory.Exists(data));
            }
        }

        /// <summary>
        /// Windows semantics: a file open for writing cannot be deleted. The kit's log is "open" wherever it currently points;
        /// with --remove-data it moves to %TEMP%\McvsOffline\kit first, so McvsOffline\ goes completely and the log continues.
        /// </summary>
        [Fact]
        public void RemoveDataWithTheKitLogInTheDataFolderLeavesNothingAndTheLogContinuesInTemp()
        {
            using (var box = new KitSandbox())
            {
                string data = GameFolder.DataDir(box.Game);
                Directory.CreateDirectory(Path.Combine(data, "kit"));
                File.WriteAllText(Path.Combine(data, "kit", KitLog.FileName), "earlier runs\n");
                File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
                string dataLog = Path.Combine(data, "kit", KitLog.FileName);
                string tempLog = Path.Combine(box.Temp, "McvsOffline", "kit", KitLog.FileName);
                // The kit's log is open where it currently writes: in the data folder until it has moved to %TEMP%.
                var files = new OpenFileOps(() => File.Exists(tempLog) ? tempLog : dataLog);
                Assert.Throws<IOException>(() => files.DeleteDirectory(data));   // the fake refuses what Windows refuses
                box.Env.Files = files;
                KitOptions options = box.Options("uninstall", "--remove-data", "--yes");
                Assert.Equal(PcExitCodes.Ok, Program.Run(box.Env, options));
                Assert.False(Directory.Exists(data));
                string text = File.ReadAllText(tempLog);
                Assert.Contains("log continued from " + dataLog, text);
                Assert.Contains("removed  McvsOffline/", text);
            }
        }

        /// <summary>
        /// An install that was never started leaves nothing of the player's in McvsOffline\, only the kit's log: the kit moves its log
        /// to %TEMP% first (the log is "open" wherever it writes), and uninstall removes the folder.
        /// </summary>
        [Fact]
        public void UninstallAfterAnInstallThatWasNeverStartedLeavesNoDataFolder()
        {
            using (var box = new KitSandbox())
            {
                string data = GameFolder.DataDir(box.Game);
                box.Env.Install = o =>
                {
                    Directory.CreateDirectory(GameFolder.BinDir(o.GameFolder));
                    foreach (string f in GameFolder.PatchedFiles) File.WriteAllText(Path.Combine(GameFolder.BinDir(o.GameFolder), f), "patched " + f);
                    File.WriteAllText(Path.Combine(data, InstallManifest.FileName), "{}");
                    return new PcInstallReport();
                };
                Assert.Equal(PcExitCodes.Ok, Program.Run(box.Env, box.Options("install")));
                string dataLog = Path.Combine(data, "kit", KitLog.FileName);
                string tempLog = Path.Combine(box.Temp, "McvsOffline", "kit", KitLog.FileName);
                Assert.True(File.Exists(dataLog));
                File.Delete(tempLog);   // the install's start, before McvsOffline\ existed
                box.Env.Files = new OpenFileOps(() => File.Exists(tempLog) ? tempLog : dataLog);

                Assert.Equal(PcExitCodes.Ok, Program.Run(box.Env, box.Options("uninstall")));
                Assert.False(Directory.Exists(data));
                Assert.DoesNotContain("your data stays", box.Out.ToString());
                Assert.Contains("log continued from " + dataLog, File.ReadAllText(tempLog));
            }
        }

        /// <summary>With the player's own files in McvsOffline\ the folder and the kit's log there stay.</summary>
        [Fact]
        public void UninstallKeepsADataFolderWithThePlayersFiles()
        {
            using (var box = new KitSandbox())
            {
                string data = GameFolder.DataDir(box.Game);
                Directory.CreateDirectory(Path.Combine(data, "kit"));
                File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
                Assert.Equal(PcExitCodes.Ok, Program.Run(box.Env, box.Options("uninstall")));
                Assert.True(File.Exists(Path.Combine(data, "settings.json")));
                Assert.Contains("your data stays", box.Out.ToString());
                Assert.Contains("== uninstall", File.ReadAllText(Path.Combine(data, "kit", KitLog.FileName)));
            }
        }

        /// <summary>A game file the kit cannot read (an antivirus scan holding it) is exit code 1 with a message, never a crash.</summary>
        [Fact]
        public void AnUnreadableGameFileIsIoForStatusVerifyAndCollect()
        {
            using (var box = new KitSandbox())
            using (new FileStream(Path.Combine(box.Game, GameFolder.ExeName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal(PcExitCodes.Io, Program.Run(box.Env, box.Options("status")));
                Assert.Contains("could not be read", box.Err.ToString());
                Assert.Equal(PcExitCodes.Io, Program.Run(box.Env, box.Options("verify")));
                Assert.Equal(PcExitCodes.Ok, Program.Run(box.Env, box.Options("collect")));
                Assert.Contains("Send this file", box.Out.ToString());
            }
        }

        [Fact]
        public void StatusPrintsTheStateAndTheListingIsDiffedAgainstTheFirstOne()
        {
            using (var box = new KitSandbox())
            {
                Assert.Equal(PcExitCodes.Ok, Program.Run(box.Env, box.Options("status", "--listing")));
                Assert.Contains("state:       not installed", box.Out.ToString());
                Assert.Contains("NOT the verified build", box.Out.ToString());
                string tempKit = Path.Combine(box.Temp, "McvsOffline", "kit");
                Assert.Single(Directory.GetFiles(tempKit, "listing-*.txt"));
                Assert.Contains("no earlier listing", box.Out.ToString());

                // The game writes its own files (GameVersion too, WR2), we write ours (McvsOffline\, args.txt), something else adds dxgi.dll.
                File.WriteAllText(Path.Combine(box.Game, "dr0.bin"), "x");
                File.WriteAllText(Path.Combine(box.Game, "GameVersion"), "x");
                File.WriteAllText(Path.Combine(box.Game, "dxgi.dll"), "x");
                Directory.CreateDirectory(Path.Combine(box.Game, "McvsOffline", "logs"));
                File.WriteAllText(Path.Combine(box.Game, "McvsOffline", "logs", "server.log"), "x");
                box.Env.Now = () => new DateTime(2026, 11, 2, 19, 0, 0);
                Assert.Equal(PcExitCodes.Ok, Program.Run(box.Env, box.Options("status", "--listing")));
                string kit = Path.Combine(box.Game, "McvsOffline", "kit");
                string diff = File.ReadAllText(Path.Combine(kit, KitListing.DiffName));
                Assert.Contains("added\tdr0.bin\tthe game's own", diff);
                Assert.Contains("added\tGameVersion\tthe game's own", diff);
                Assert.Contains("added\tdxgi.dll\tother (not ours)", diff);
                Assert.Contains("added\tMcvsOffline\\\tours", diff);
                Assert.Contains("written by the game, not ours: dr0.bin, GameVersion", box.Out.ToString());
                Assert.Contains("other files, not ours: dxgi.dll", box.Out.ToString());
            }
        }

        [Fact]
        public void HelpPrintsTheUsage()
        {
            using (var box = new KitSandbox())
            {
                Assert.Equal(0, Program.Run(box.Env, KitOptions.Parse(new[] { "help" }, null)));
                Assert.Contains("uninstall [--remove-data --yes]", box.Out.ToString());
            }
        }
    }

    /// <summary>System.IO with Windows' rule for open files: deleting the open file, or a folder holding it, fails.</summary>
    public sealed class OpenFileOps : IPcFileOps
    {
        private readonly PcFileOps real = new PcFileOps();

        public OpenFileOps(Func<string> openPath)
        {
            OpenPath = openPath;
        }

        public Func<string> OpenPath { get; set; }

        public void WriteAllBytes(string path, byte[] bytes) { real.WriteAllBytes(path, bytes); }

        public void Rename(string source, string destination) { real.Rename(source, destination); }

        public void DeleteFile(string path)
        {
            if (IsOpen(path)) throw new IOException("The process cannot access the file '" + path + "' because it is being used by another process.");
            real.DeleteFile(path);
        }

        public void DeleteDirectory(string path)
        {
            string open = OpenPath();
            if (open != null && File.Exists(open) && Path.GetFullPath(open).StartsWith(Path.GetFullPath(path) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new IOException("The process cannot access the file '" + open + "' because it is being used by another process.");
            real.DeleteDirectory(path);
        }

        private bool IsOpen(string path)
        {
            string open = OpenPath();
            return open != null && string.Equals(Path.GetFullPath(open), Path.GetFullPath(path), StringComparison.Ordinal);
        }
    }
}
