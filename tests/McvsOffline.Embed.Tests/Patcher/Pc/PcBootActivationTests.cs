using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Pc;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// WR2 F2: the kit's <c>status</c> judges the last start from <c>McvsOffline\logs\bootstrap.log</c> (its "[boot] assemblies:"
    /// and "patches:" lines), so an install whose files are current but whose Game.dll came from the game folder is "installed,
    /// NOT ACTIVE (...)", an error, not "installed, current".
    /// </summary>
    public class PcBootActivationTests
    {
        private const string Steam = "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Modern Combat Versus";

        private readonly ITestOutputHelper output;

        public PcBootActivationTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>A start as WR2's bootstrap.log wrote it (lines 1-20 of the first start), with Game from <paramref name="gameWhere"/>.</summary>
        internal static string Start(string time, bool gameFromBin, string patches)
        {
            string game = gameFromBin
                ? "INFO  [boot] +0.672 s assemblies: Game " + Steam + "\\McvsOffline\\bin\\Game.dll (McvsOffline\\bin)"
                : "WARN  [boot] +0.672 s assemblies: Game " + Steam + "\\Game.dll (GAME FOLDER): the .exe.config redirect did not apply";
            return string.Join("\r\n", new[]
            {
                time + ".425Z INFO  [embedded] MCVS Offline 0.1.0-dev embedded host starting (platform windows)",
                time + ".434Z INFO  [crash] hooks: AppDomain ok, unobserved-tasks ok (event)",
                time + ".438Z INFO  [boot] +0.078 s MCVS Offline host started by the game (EngineBridge.Class1.Start)",
                time + ".764Z INFO  [boot] +0.406 s assemblies: EngineBridge " + Steam + "\\McvsOffline\\bin\\EngineBridge.dll (McvsOffline\\bin)",
                time + ".765Z INFO  [boot] +0.406 s assemblies: Gameloft.Engine " + Steam + "\\McvsOffline\\bin\\Gameloft.Engine.dll (McvsOffline\\bin)",
                time + ".765Z INFO  [boot] +0.406 s assemblies: McvsOffline.Server " + Steam + "\\McvsOffline\\bin\\McvsOffline.Server.dll (McvsOffline\\bin)",
                time + ".766Z INFO  [game] Game is not loaded yet: the client's patches are probed when it loads",
                time + ".847Z INFO  [boot] +0.484 s assemblies: Gameloft.Util " + Steam + "\\McvsOffline\\bin\\Gameloft.Util.dll (McvsOffline\\bin)",
                time + ".905Z INFO  [boot] +0.641 s install: current",
                time + ".929Z " + game,
                time + ".934Z " + (patches.Contains(" NO") ? "WARN " : "INFO ") + " [boot] +0.672 s " + patches,
                time + ".971Z INFO  [boot] +0.813 s offline server listening at http://127.0.0.1:51780/",
            }) + "\r\n";
        }

        private const string Wr2Patches = "patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, store NO";
        private const string AllYes = "patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes, store yes";

        [Fact]
        public void TheWr2StartIsNotActive()
        {
            PcBootActivation boot = PcBootActivation.Read(Start("2026-10-29T17:53:17", false, Wr2Patches));
            Assert.NotNull(boot);
            Assert.True(boot.NotActive);
            Assert.True(boot.GameSeen);
            Assert.Equal("2026-10-29T17:53:17.425Z", boot.Started);
            Assert.Equal(new[] { "Game loaded from the game folder", "the 'store' patch is missing" }, boot.Reasons);
            Assert.Equal("Game loaded from the game folder; the 'store' patch is missing", boot.Reason);
            Assert.Equal(5, boot.Assemblies.Count);
            Assert.Equal(Wr2Patches, boot.Patches);
            Assert.Equal("NOT ACTIVE at the last start (2026-10-29T17:53:17.425Z): Game loaded from the game folder; the 'store' patch is missing", boot.Describe());
        }

        [Fact]
        public void OnlyTheLastStartCounts()
        {
            string log = Start("2026-10-29T17:53:17", false, Wr2Patches) + Start("2026-10-29T19:00:00", true, AllYes);
            PcBootActivation boot = PcBootActivation.Read(log);
            Assert.False(boot.NotActive);
            Assert.Null(boot.Reason);
            Assert.Equal("2026-10-29T19:00:00.425Z", boot.Started);
            Assert.Equal("active at the last start (2026-10-29T19:00:00.425Z): every assembly from McvsOffline\\bin, every applied patch's witness yes", boot.Describe());

            boot = PcBootActivation.Read(Start("2026-10-29T19:00:00", true, AllYes) + Start("2026-10-29T20:00:00", false, Wr2Patches));
            Assert.True(boot.NotActive);
        }

        [Fact]
        public void AStartThatHasNotLoadedGameIsUnknown()
        {
            string log = Start("2026-10-29T17:53:17", true, AllYes);
            string cut = string.Join("\r\n", log.Split(new[] { "\r\n" }, StringSplitOptions.None).Take(9));   // stopped before Game loaded
            PcBootActivation boot = PcBootActivation.Read(cut);
            Assert.False(boot.NotActive);
            Assert.False(boot.GameSeen);
            Assert.StartsWith("unknown: the last start (2026-10-29T17:53:17.425Z) did not load Game", boot.Describe());
            Assert.Null(PcBootActivation.Read("2026-10-29T17:53:17.425Z INFO  [embedded] MCVS Offline embedded host starting\n"));
            Assert.Null(PcBootActivation.Read(""));
            Assert.Null(PcBootActivation.Read(null));
        }

        [Fact]
        public void ElsewhereAndAMissingWitnessAreReasonsToo()
        {
            const string log = "[boot] assemblies: Game (no file) (elsewhere): the .exe.config redirect did not apply\n" +
                               "[boot] patches: hook-pc unknown (not loaded), quicklaunch yes, gameload NO, store yes\n";
            PcBootActivation boot = PcBootActivation.Read(log);
            Assert.Null(boot.Started);
            Assert.Equal(new[] { "Game loaded from outside McvsOffline\\bin (elsewhere)", "the 'gameload' patch is missing" }, boot.Reasons);
            Assert.False(boot.StartedBefore("2026-11-02T18:00:00Z"));   // no time: judged
        }

        /// <summary>
        /// Only the witnesses of applied patches count (CheckWritten's rule): an install without the default 'store' (--patch none)
        /// runs a Game.dll from bin without McvsStoreSafety. An older host writes "store NO", a current one "store off (not applied)".
        /// </summary>
        [Fact]
        public void AMissingWitnessOfAPatchNotAppliedIsNoReason()
        {
            const string storeNo = "patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes, store NO";
            const string storeOff = "patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes, store off (not applied)";
            var mandatoryOnly = new List<string> { "hook-pc", "headless", "headless-engine", "physics", "quicklaunch", "headless-diag", "graphics", "gameload" };

            PcBootActivation boot = PcBootActivation.Read(Start("2026-10-29T19:00:00", true, storeNo), mandatoryOnly);
            Assert.False(boot.NotActive);
            Assert.StartsWith("active at the last start", boot.Describe());
            Assert.False(PcBootActivation.Read(Start("2026-10-29T19:00:00", true, storeOff), mandatoryOnly).NotActive);
            Assert.False(PcBootActivation.Read(Start("2026-10-29T19:00:00", true, storeOff)).NotActive);

            // Applied but missing, or the applied list unknown: still a reason.
            var withStore = new List<string>(mandatoryOnly) { "store" };
            Assert.Equal(new[] { "the 'store' patch is missing" }, PcBootActivation.Read(Start("2026-10-29T19:00:00", true, storeNo), withStore).Reasons);
            Assert.True(PcBootActivation.Read(Start("2026-10-29T19:00:00", true, storeNo)).NotActive);
            // A mandatory witness missing stays a reason, and Game from the game folder too.
            boot = PcBootActivation.Read(Start("2026-10-29T19:00:00", false, storeNo.Replace("gameload yes", "gameload NO")), mandatoryOnly);
            Assert.Equal(new[] { "Game loaded from the game folder", "the 'gameload' patch is missing" }, boot.Reasons);
        }

        /// <summary>The kit on a real install without 'store': a start from bin with "store NO" is current and active, and verify passes.</summary>
        [Fact]
        public void AnInstallWithoutStoreIsActive()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                PcInstallOptions options = copy.Options();
                options.Patches = PcGameCopy.StandInPlan().Where(p => p.Id != "store").ToList();
                Assert.Equal(0, PcInstaller.Install(options).ExitCode);
                Assert.DoesNotContain("store", copy.Status().Manifest.Patches);

                Directory.CreateDirectory(copy.Path_("McvsOffline/logs"));
                File.WriteAllText(copy.Path_("McvsOffline/logs/bootstrap.log"),
                                  Start("2026-11-02T18:05:00", true, "patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes, store NO"));
                PcStatusReport status = copy.Status();
                foreach (string line in PcReportText.Status(status)) output.WriteLine(line);
                Assert.Equal(PcInstallState.Current, status.State);
                Assert.Empty(status.Errors);
                Assert.StartsWith("active at the last start (2026-11-02T18:05:00.425Z)", status.Activation);
                PcInstallReport verify = PcInstaller.Verify(copy.Game, TestEnvironment.Net452Refs, () => false);
                Assert.Equal(0, verify.ExitCode);

                // The same start from the game folder is still NOT ACTIVE (the redirect, not the witness).
                File.WriteAllText(copy.Path_("McvsOffline/logs/bootstrap.log"),
                                  Start("2026-11-02T18:06:00", false, "patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes, store NO"));
                status = copy.Status();
                Assert.Equal(PcInstallState.NotActive, status.State);
                Assert.Equal("installed, NOT ACTIVE (Game loaded from the game folder)", status.StateDetail);
            }
        }

        [Fact]
        public void AStartBeforeTheInstallIsNotJudged()
        {
            PcBootActivation boot = PcBootActivation.Read(Start("2026-10-29T17:53:17", false, Wr2Patches));
            Assert.True(boot.StartedBefore("2026-11-02T18:00:00Z"));
            Assert.False(boot.StartedBefore("2026-10-29T17:51:08Z"));
            Assert.False(boot.StartedBefore(null));
        }

        /// <summary>
        /// The kit's status on a real install (the stand-in plan): no bootstrap.log → current; WR2's start after the install →
        /// "installed, NOT ACTIVE (Game loaded from the game folder; ...)" with an ERROR line; a start from bin → current and "active";
        /// a start older than the install → current, "not started since the install". Damaged files still win over NOT ACTIVE.
        /// </summary>
        [Fact]
        public void TheKitStatusSaysNotActiveForTheWr2Start()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                Assert.Equal(0, copy.Install().ExitCode);   // install.json writtenUtc 2026-11-02T18:00:00Z
                PcStatusReport fresh = copy.Status();
                Assert.Equal(PcInstallState.Current, fresh.State);
                Assert.Null(fresh.Activation);

                Directory.CreateDirectory(copy.Path_("McvsOffline/logs"));
                string log = copy.Path_("McvsOffline/logs/bootstrap.log");
                File.WriteAllText(log, Start("2026-11-02T18:05:00", false, Wr2Patches));
                PcStatusReport inactive = copy.Status();
                List<string> text = PcReportText.Status(inactive);
                foreach (string line in text) output.WriteLine(line);
                Assert.Equal(PcInstallState.NotActive, inactive.State);
                Assert.Equal("installed, NOT ACTIVE (Game loaded from the game folder; the 'store' patch is missing)", inactive.StateDetail);
                Assert.Contains("state:       installed, NOT ACTIVE (Game loaded from the game folder; the 'store' patch is missing)", text);
                Assert.DoesNotContain(text, l => l.Contains("installed, current"));
                Assert.Contains(text, l => l.StartsWith("ERROR: the last start did not run MCVS Offline's files (Game loaded from the game folder", StringComparison.Ordinal));
                Assert.Contains(text, l => l.StartsWith("activation:  NOT ACTIVE at the last start (2026-11-02T18:05:00.425Z)", StringComparison.Ordinal));
                Assert.Contains(text, l => l.Contains("last start: assemblies: Game " + Steam + "\\Game.dll (GAME FOLDER)"));
                Assert.Equal("installed, NOT ACTIVE", PcReportText.StateName(PcInstallState.NotActive));

                File.AppendAllText(log, Start("2026-11-02T18:10:00", true, AllYes));
                PcStatusReport active = copy.Status();
                Assert.Equal(PcInstallState.Current, active.State);
                Assert.Empty(active.Errors);
                Assert.StartsWith("active at the last start (2026-11-02T18:10:00.425Z)", active.Activation);
                Assert.Contains("state:       installed, current", PcReportText.Status(active));

                File.WriteAllText(log, Start("2026-10-29T17:53:17", false, Wr2Patches));
                PcStatusReport older = copy.Status();
                Assert.Equal(PcInstallState.Current, older.State);
                Assert.Empty(older.Errors);
                Assert.StartsWith("not started since the install", older.Activation);

                // DAMAGED stays DAMAGED (the file problem is the first thing to fix); the start's reason is still an error.
                File.WriteAllText(log, Start("2026-11-02T18:05:00", false, Wr2Patches));
                byte[] util = File.ReadAllBytes(copy.Path_("McvsOffline/bin/Gameloft.Util.dll"));
                util[util.Length - 1] ^= 0xFF;
                File.WriteAllBytes(copy.Path_("McvsOffline/bin/Gameloft.Util.dll"), util);
                PcStatusReport damaged = copy.Status();
                Assert.Equal(PcInstallState.Damaged, damaged.State);
                Assert.Contains(damaged.Errors, e => e.Contains("Game loaded from the game folder"));
            }
        }
    }
}
