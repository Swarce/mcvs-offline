using System.Collections.Generic;
using System.IO;
using System.Text;
using McvsOffline.Embedded;
using McvsOffline.Server.Core;
using Xunit;

namespace McvsOffline.PcSpike.Tests
{
    /// <summary>
    /// 'start' / 'stop' on args.txt (WP0 step 1): the managed block is the Android one, the original file is backed up once
    /// and restored byte for byte, a missing file is deleted again, and an unwritable install folder is reported.
    /// </summary>
    public class ArgsTxtStepTests
    {
        private const string DeviceId = "mcvs0123456789abcdef0123456789abcdef";

        private static IList<ArgEntry> Managed() => ManagedArgs.Build(new Settings(), DeviceId, 51780, null);

        [Fact]
        public void WritesTheWp0Step1BlockWhenThereWasNoArgsTxtAndStopDeletesIt()
        {
            string game = Support.NewTempDir("game");
            string data = Support.NewTempDir("data");
            try
            {
                var log = new SpikeLog(data, echo: false);
                ArgsInstallResult result = ArgsTxtStep.Install(game, data, Managed(), log);
                Assert.True(result.Written);
                Assert.Null(result.WriteError);
                Assert.Empty(result.Warnings);
                string expected =
                    ArgsFile.BeginMarker + " " + ArgsFile.BlockHint + "\n" +
                    "-ServerLocation Embedded\n" +
                    "-AnubisCommand QuickLaunch\n" +
                    "-EnvironmentSwitcherUrl http://127.0.0.1:51780/\n" +
                    "-SuperCredential game:mcvsoffline\n" +
                    "-SuperPassword mcvsoffline\n" +
                    "-DeviceId " + DeviceId + "\n" +
                    "-DataCenter mcvs\n" +
                    ArgsFile.EndMarker + "\n";
                byte[] written = File.ReadAllBytes(Path.Combine(game, "args.txt"));
                Assert.Equal(expected, Encoding.ASCII.GetString(written));
                Assert.Equal(expected, result.Content);
                Assert.NotEqual(0xEF, written[0]);   // no BOM: it would swallow the first argument
                Assert.DoesNotContain((byte)'\t', written);
                Assert.Contains("RESULT install folder writable: yes", File.ReadAllText(log.FilePath));
                // 'status' and 'collect' warn on the console while our block is there (review finding, build 10).
                Assert.Equal(new[] { "args.txt still has the MCVS Offline block (the game would look for our server): run 'stop' to restore the original" },
                             GameFolderState.Leftovers(game));

                Assert.Equal("deleted our args.txt (there was none before)", ArgsTxtStep.Restore(game, data, log));
                Assert.False(File.Exists(Path.Combine(game, "args.txt")));
                Assert.Empty(GameFolderState.Leftovers(game));
                Assert.Equal("nothing to restore (no backup record)", ArgsTxtStep.Restore(game, data, log));
            }
            finally
            {
                Support.DeleteDir(game);
                Support.DeleteDir(data);
            }
        }

        [Fact]
        public void AnExistingArgsTxtIsBackedUpOnceKeptAroundOurBlockAndRestoredExactly()
        {
            string game = Support.NewTempDir("game");
            string data = Support.NewTempDir("data");
            try
            {
                var log = new SpikeLog(data, echo: false);
                byte[] original = Encoding.ASCII.GetBytes("-Fullscreen 0\r\n-Width 1280\r\n");
                File.WriteAllBytes(Path.Combine(game, "args.txt"), original);

                ArgsInstallResult first = ArgsTxtStep.Install(game, data, Managed(), log);
                Assert.True(first.Written);
                string text = File.ReadAllText(Path.Combine(game, "args.txt"));
                Assert.StartsWith("-Fullscreen 0\r\n-Width 1280\r\n" + ArgsFile.BeginMarker, text);
                Assert.Contains("-ServerLocation Embedded\r\n", text);   // the player's CRLF style is kept
                Assert.Equal(original, File.ReadAllBytes(Path.Combine(data, "backup", "args.txt")));

                // A second start (e.g. after a crash) keeps the first backup, not our file.
                ArgsTxtStep.Install(game, data, Managed(), log);
                Assert.Equal(original, File.ReadAllBytes(Path.Combine(data, "backup", "args.txt")));

                Assert.Single(GameFolderState.Leftovers(game));
                Assert.Equal("restored the original args.txt", ArgsTxtStep.Restore(game, data, log));
                Assert.Equal(original, File.ReadAllBytes(Path.Combine(game, "args.txt")));
                Assert.Empty(GameFolderState.Leftovers(game));   // the player's own args.txt is not a leftover
                Assert.False(File.Exists(Path.Combine(data, "backup", "args.txt")));
            }
            finally
            {
                Support.DeleteDir(game);
                Support.DeleteDir(data);
            }
        }

        [Fact]
        public void APlayerValueOutsideTheBlockWinsAndIsReported()
        {
            string game = Support.NewTempDir("game");
            string data = Support.NewTempDir("data");
            try
            {
                File.WriteAllText(Path.Combine(game, "args.txt"), "-DataCenter elsewhere\n");
                ArgsInstallResult result = ArgsTxtStep.Install(game, data, Managed(), new SpikeLog(data, echo: false));
                Assert.Contains(result.Warnings, w => w.Contains("-DataCenter is already set"));
                Assert.DoesNotContain("-DataCenter mcvs", result.Content);
            }
            finally
            {
                Support.DeleteDir(game);
                Support.DeleteDir(data);
            }
        }

        [Fact]
        public void AnUnwritableArgsTxtIsReportedNotThrown()
        {
            string game = Support.NewTempDir("game");
            string data = Support.NewTempDir("data");
            try
            {
                // A directory named args.txt cannot be written as a file, whoever runs the test (root ignores permissions).
                Directory.CreateDirectory(Path.Combine(game, "args.txt"));
                var log = new SpikeLog(data, echo: false);
                ArgsInstallResult result = ArgsTxtStep.Install(game, data, Managed(), log);
                Assert.False(result.Written);
                Assert.NotNull(result.WriteError);
                Assert.Contains("RESULT install folder writable: NO", File.ReadAllText(log.FilePath));
            }
            finally
            {
                Support.DeleteDir(game);
                Support.DeleteDir(data);
            }
        }

        [Fact]
        public void PrefixedArgsFilesAreListed()
        {
            string game = Support.NewTempDir("game");
            try
            {
                File.WriteAllText(Path.Combine(game, "args.txt"), "");
                File.WriteAllText(Path.Combine(game, "steam_args.txt"), "");
                Assert.Equal(new[] { "steam_args.txt" }, ArgsTxtStep.PrefixedArgsFiles(game));
                Assert.Empty(ArgsTxtStep.PrefixedArgsFiles(Path.Combine(game, "missing")));
            }
            finally
            {
                Support.DeleteDir(game);
            }
        }
    }
}
