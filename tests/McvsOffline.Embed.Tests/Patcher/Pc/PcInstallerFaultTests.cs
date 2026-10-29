using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Patcher.Pc;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// System.IO with injected failures (docs/design/pc-port.md §12.3): the Nth write or rename throws (a full disk, a locked file,
    /// a killed process), and optionally Windows' rule that a file open for writing cannot be deleted.
    /// </summary>
    public sealed class FaultyFileOps : IPcFileOps
    {
        private readonly PcFileOps real = new PcFileOps();

        /// <summary>Throw on the write of this path (before anything is written).</summary>
        public string FailWriteOf { get; set; }

        /// <summary>Throw on the rename onto this path (the .mcvs-tmp file stays, as after a crash).</summary>
        public string FailRenameTo { get; set; }

        /// <summary>A file "open for writing": it, and a folder holding it, cannot be deleted.</summary>
        public string OpenFile { get; set; }

        public List<string> Calls { get; } = new List<string>();

        public void WriteAllBytes(string path, byte[] bytes)
        {
            Calls.Add("write " + path);
            if (Same(path, FailWriteOf)) throw new IOException("There is not enough space on the disk. (injected)");
            real.WriteAllBytes(path, bytes);
        }

        public void Rename(string source, string destination)
        {
            Calls.Add("rename " + destination);
            if (Same(destination, FailRenameTo)) throw new UnauthorizedAccessException("Access to the path is denied. (injected)");
            real.Rename(source, destination);
        }

        public void DeleteFile(string path)
        {
            Calls.Add("delete " + path);
            if (Same(path, OpenFile)) throw new IOException("The process cannot access the file because it is being used by another process. (injected)");
            real.DeleteFile(path);
        }

        public void DeleteDirectory(string path)
        {
            Calls.Add("rmdir " + path);
            if (OpenFile != null && Path.GetFullPath(OpenFile).StartsWith(Path.GetFullPath(path) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new IOException("The process cannot access the file '" + OpenFile + "' because it is being used by another process. (injected)");
            real.DeleteDirectory(path);
        }

        private static bool Same(string a, string b)
        {
            return b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A failure after each step of §7.4 leaves a state status names (PARTIAL, or not installed with a *.mcvs-tmp leftover), never a
    /// config that points at a missing file, and a following uninstall restores the original tree exactly; uninstall from PARTIAL,
    /// DAMAGED and STALE; a data folder with an open file is reported, not retried.
    /// </summary>
    public class PcInstallerFaultTests
    {
        /// <summary>The §7.4 write order.</summary>
        private static readonly string[] Steps =
        {
            "McvsOffline/bin/EngineBridge.dll", "McvsOffline/bin/Game.dll", "McvsOffline/bin/Gameloft.Engine.dll", "McvsOffline/bin/Gameloft.Util.dll",
            "McvsOffline/bin/McvsOffline.Server.dll", "McvsOffline/install.json", "ModernCombatVersus.exe.config",
        };

        private readonly ITestOutputHelper output;

        public PcInstallerFaultTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void AFailureAfterAnyStepLeavesANamedStateAndUninstallRestoresTheTree()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                SortedDictionary<string, string> original = copy.Tree();
                for (int i = 0; i < Steps.Length; i++)
                {
                    foreach (bool atRename in new[] { false, true })
                    {
                        string target = copy.Path_(Steps[i]);
                        var files = new FaultyFileOps();
                        if (atRename) files.FailRenameTo = target;
                        else files.FailWriteOf = target + GameFolder.TempSuffix;
                        PcInstallReport report = copy.Install(files);
                        string what = Steps[i] + (atRename ? " (rename)" : " (write)");
                        Assert.True(report.ExitCode == PcExitCodes.Io, what + ": " + report.ExitCode + " " + report.Error);
                        Assert.Contains(Steps[i], report.Error);

                        PcStatusReport status = copy.Status();
                        output.WriteLine(what + " -> " + PcReportText.StateName(status.State) + "; leftovers: " + string.Join(" | ", status.Leftovers));
                        Assert.Equal(i == 0 ? PcInstallState.NotInstalled : PcInstallState.Partial, status.State);
                        bool tempLeft = status.Leftovers.Any(l => l.StartsWith(Steps[i] + GameFolder.TempSuffix, StringComparison.Ordinal));
                        Assert.Equal(atRename, tempLeft);
                        AssertNoConfigPointsAtAMissingFile(copy);

                        PcInstallReport undo = copy.Uninstall();
                        Assert.True(undo.ExitCode == 0, what + ": uninstall " + undo.Error);
                        Assert.Equal(original, copy.Tree());
                        Assert.False(Directory.Exists(copy.Path_("McvsOffline")));
                    }
                }
            }
        }

        [Fact]
        public void AFailedReinstallNeverLeavesAConfigPointingAtAMissingFile()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                Assert.Equal(0, copy.Install().ExitCode);
                // The reinstall breaks after the first bin file: the old config still points at files that exist.
                var files = new FaultyFileOps { FailRenameTo = copy.Path_("McvsOffline/bin/Game.dll") };
                Assert.Equal(PcExitCodes.Io, copy.Install(files).ExitCode);
                AssertNoConfigPointsAtAMissingFile(copy);
                // Same bytes as the first install, so nothing is even damaged; a clean reinstall then removes nothing but the temp file.
                Assert.Equal(PcInstallState.Current, copy.Status().State);
                Assert.Equal(0, copy.Install().ExitCode);
                Assert.Empty(copy.Status().Leftovers);
            }
        }

        [Fact]
        public void UninstallWorksFromDamagedAndStale()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                SortedDictionary<string, string> original = copy.Tree();

                Assert.Equal(0, copy.Install().ExitCode);
                File.Delete(copy.Path_("McvsOffline/bin/Gameloft.Engine.dll"));
                Assert.Equal(PcInstallState.Damaged, copy.Status().State);
                Assert.Equal(0, copy.Uninstall().ExitCode);
                Assert.Equal(original, copy.Tree());

                Assert.Equal(0, copy.Install().ExitCode);
                byte[] changed = PcInstallerTests.Flip(File.ReadAllBytes(copy.Path_("Gameloft.Util.dll")));
                File.WriteAllBytes(copy.Path_("Gameloft.Util.dll"), changed);   // "Steam updated the game"
                Assert.Equal(PcInstallState.Stale, copy.Status().State);
                Assert.Equal(0, copy.Uninstall().ExitCode);
                var expected = new SortedDictionary<string, string>(original, StringComparer.Ordinal);
                expected["Gameloft.Util.dll"] = McvsOffline.Patcher.Pc.GameFolder.Sha256(changed);
                Assert.Equal(expected, copy.Tree());
            }
        }

        [Fact]
        public void ALockedFileIsReportedAndTheRestIsRemoved()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                Assert.Equal(0, copy.Install().ExitCode);
                var files = new FaultyFileOps { OpenFile = copy.Path_("McvsOffline/bin/Game.dll") };
                PcInstallReport report = copy.Uninstall(files: files);
                Assert.Equal(PcExitCodes.Io, report.ExitCode);
                Assert.Contains(report.LeftBehind, l => l.Contains("Game.dll") && l.Contains("quit the game"));
                Assert.False(File.Exists(copy.Path_(PcExeConfig.FileName)));   // the config went first
                Assert.False(File.Exists(copy.Path_("McvsOffline/bin/Gameloft.Engine.dll")));
                Assert.Equal(1, files.Calls.Count(c => c == "delete " + copy.Path_("McvsOffline/bin/Game.dll")));   // never retried in a loop
                Assert.Equal(PcInstallState.Partial, copy.Status().State);
                Assert.Equal(0, copy.Uninstall().ExitCode);   // once the game is closed
            }
        }

        [Fact]
        public void RemoveDataWithAnOpenFileReportsItOnce()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                Assert.Equal(0, copy.Install().ExitCode);
                Directory.CreateDirectory(copy.Path_("McvsOffline/kit"));
                File.WriteAllText(copy.Path_("McvsOffline/kit/pc-kit.log"), "open");
                var files = new FaultyFileOps { OpenFile = copy.Path_("McvsOffline/kit/pc-kit.log") };
                PcInstallReport report = copy.Uninstall(removeData: true, files: files);
                Assert.Equal(PcExitCodes.Io, report.ExitCode);
                Assert.Contains(report.LeftBehind, l => l.Contains("McvsOffline"));
                Assert.Equal(1, files.Calls.Count(c => c == "rmdir " + copy.Path_("McvsOffline")));   // reported, never retried
                // Everything of the install itself is gone; the kit (which moves its log to %TEMP% first) then succeeds.
                Assert.False(File.Exists(copy.Path_(PcExeConfig.FileName)));
                Assert.False(Directory.Exists(copy.Path_("McvsOffline/bin")));
                Assert.Equal(0, copy.Uninstall(removeData: true).ExitCode);
                Assert.False(Directory.Exists(copy.Path_("McvsOffline")));
            }
        }

        /// <summary>
        /// A config that cannot be deleted (read-only attribute, an antivirus lock) stops the uninstall: bin\ and install.json stay, so
        /// the redirect never points at deleted files and the game still starts; the next uninstall finishes.
        /// </summary>
        [Fact]
        public void AConfigThatCannotBeDeletedKeepsWhatItPointsAt()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                SortedDictionary<string, string> original = copy.Tree();
                Assert.Equal(0, copy.Install().ExitCode);
                SortedDictionary<string, string> installed = copy.Tree();
                var files = new FaultyFileOps { OpenFile = copy.Path_(PcExeConfig.FileName) };
                PcInstallReport report = copy.Uninstall(files: files);
                Assert.Equal(PcExitCodes.Io, report.ExitCode);
                Assert.Contains(PcExeConfig.FileName, report.Error);
                Assert.Contains(report.LeftBehind, l => l.StartsWith(PcExeConfig.FileName, StringComparison.Ordinal));
                Assert.Equal(installed, copy.Tree());   // every bin file and install.json still there
                AssertNoConfigPointsAtAMissingFile(copy);
                Assert.Equal(PcInstallState.Current, copy.Status().State);
                Assert.Equal(0, copy.Uninstall().ExitCode);
                Assert.Equal(original, copy.Tree());
            }
        }

        /// <summary>args.txt with the player's lines is rewritten through args.txt.mcvs-tmp: a failed write leaves it as it was.</summary>
        [Fact]
        public void TheArgsFileIsRewrittenThroughATempFile()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                Assert.Equal(0, copy.Install().ExitCode);
                string args = copy.Path_(GameFolder.ArgsFileName);
                File.WriteAllBytes(args, McvsOffline.Embedded.ArgsFile.Merge(System.Text.Encoding.UTF8.GetBytes("-Foo bar\n"),
                    new List<McvsOffline.Embedded.ArgEntry> { new McvsOffline.Embedded.ArgEntry("ServerLocation", "Embedded") }).Content);
                byte[] withBlock = File.ReadAllBytes(args);

                var files = new FaultyFileOps { FailWriteOf = args + GameFolder.TempSuffix };
                PcInstallReport report = copy.Uninstall(files: files);
                Assert.Equal(PcExitCodes.Io, report.ExitCode);
                Assert.Contains(report.LeftBehind, l => l.StartsWith("args.txt still has the MCVS Offline block", StringComparison.Ordinal));
                Assert.Equal(withBlock, File.ReadAllBytes(args));
                Assert.DoesNotContain("write " + args, files.Calls);

                Assert.Equal(0, copy.Uninstall().ExitCode);
                Assert.Equal("-Foo bar\n", File.ReadAllText(args));
                Assert.False(File.Exists(args + GameFolder.TempSuffix));
            }
        }

        private static void AssertNoConfigPointsAtAMissingFile(PcGameCopy copy)
        {
            string config = copy.Path_(PcExeConfig.FileName);
            if (!File.Exists(config)) return;
            foreach (PcCodeBase c in PcExeConfig.Parse(File.ReadAllText(config)))
                Assert.True(File.Exists(copy.Path_(c.Href)), "the config points at the missing " + c.Href);
        }
    }
}
