using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using McvsOffline.Patcher.Pc;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// The build-20 'bottile' patch as build 20 applied it (removed in build 21 with the PRACTICE tile): in the lambda of
    /// <c>Game.GameClientComponent.NativeStartMatchWithBots</c>, <c>IL_0007 ldc.i4.2</c> (OfflineMatchWithBots) became
    /// <c>ldc.i4.1</c>. Only these tests use it, to write a build-20 install that an upgrade then replaces.
    /// </summary>
    public sealed class Build20BotTilePatch : IlPatch
    {
        public override string Id => "bottile";

        public override string AssemblyFile => "Game.dll";

        public override string Description => "test stand-in: build 20's 'bottile' (one ldc.i4.2 -> ldc.i4.1)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            Instruction site = PcBuild20UpgradeTests.ConnectionTypeSite(module);
            if (site.OpCode != OpCodes.Ldc_I4_2) throw new PatchException("Game.dll: the bot-match lambda is not the stock one (" + site.OpCode + ")");
            site.OpCode = OpCodes.Ldc_I4_1;
            context.Notes.Add("bot-match lambda: IL_0007 ldc.i4.2 -> ldc.i4.1 (test stand-in of build 20's bottile)");
        }
    }

    /// <summary>
    /// Build 21 removed the 'bottile' patch (docs/design/pc-port.md §8.1). An install of build 20 (install.json lists 'bottile'
    /// for McvsOffline\bin\Game.dll, which has it) is OLDER BUILD under a build-21 kit, never DAMAGED, and no check names the
    /// removed patch (it has no witness); install over it writes the same Game.dll as a fresh build-21 install (the patches
    /// start from the game folder's own Game.dll, never from the old copy), install.json no longer lists 'bottile', verify
    /// passes and, after a start, status says active. A stale <c>--patch</c> list that still names 'bottile' (the build-20
    /// default set, as PLAYER-SETUP-PC.md printed it) installs with a warning. On a temp copy of the real Steam install with
    /// the real PC patches and server (as <see cref="PcInstallerTests.RealFolderEndToEnd"/>).
    /// </summary>
    public class PcBuild20UpgradeTests
    {
        /// <summary>The build-20 PC default set, in the order install.json printed it.</summary>
        public const string Build20Defaults = "nativeonline,waits,adminlink,geo,bottile,store,signatures,inbox,adminpage";

        /// <summary>What a build-21 install says about a stale 'bottile' in --patch (PatchPlan.RemovedWarning).</summary>
        public const string BotTileWarning = "patch 'bottile' was removed in build 21 (the hidden PRACTICE tile it served is gone): ignored";

        private const string GameCopy = "McvsOffline/bin/Game.dll";

        private readonly ITestOutputHelper output;

        public PcBuild20UpgradeTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>The <c>ldc.i4</c> that passes the MatchConnectionType in the lambda NativeStartMatchWithBots runs.</summary>
        internal static Instruction ConnectionTypeSite(ModuleDefinition module)
        {
            TypeDefinition type = module.GetType("Game.GameClientComponent");
            MethodDefinition native = type.Methods.Single(m => m.Name == "NativeStartMatchWithBots" && !m.HasParameters);
            MethodDefinition lambda = native.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldftn).Select(i => ((MethodReference)i.Operand).Resolve())
                .Single(m => m.DeclaringType == type && m.Name.StartsWith("<NativeStartMatchWithBots>", StringComparison.Ordinal));
            Instruction site = lambda.Body.Instructions[3];
            Assert.Equal(7, site.Offset);
            return site;
        }

        private static OpCode ConnectionTypeOpCode(PcGameCopy copy, string relative)
        {
            ReaderParameters reader = TestEnvironment.PcReader(copy.Game, copy.ServerFolder);
            using (reader.AssemblyResolver)
            using (ModuleDefinition game = ModuleDefinition.ReadModule(copy.Path_(relative), reader))
                return ConnectionTypeSite(game).OpCode;
        }

        private List<string> Print(IEnumerable<string> lines)
        {
            List<string> list = lines.ToList();
            foreach (string line in list) output.WriteLine(line);
            return list;
        }

        [Fact]
        public void AnUpgradeOverABuild20InstallWritesBuild21sGameDll()
        {
            string serverDll = TestEnvironment.PcServerDll;
            if (serverDll == null)
            {
                Assert.False(TestEnvironment.RequirePc, "MCVS_REQUIRE_PC=1 but the PC server DLL of this configuration was not built");
                output.WriteLine("the PC server DLL was not built: skipped");
                return;
            }
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                byte[] server = File.ReadAllBytes(serverDll);
                string kit = PcInstaller.ServerBuildOf(server);
                string older = (int.Parse(kit, CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture);
                Func<PcInstallOptions> real = () =>
                {
                    PcInstallOptions o = copy.Options();
                    o.Patches = null;   // PatchPlan.For(PcSteam6372049, OptionalPatches or the defaults)
                    o.ServerDll = server;
                    o.ServerFolder = Path.GetDirectoryName(serverDll);
                    return o;
                };
                Assert.DoesNotContain("bottile", PatchPlan.Ids(BuildProfile.PcSteam6372049).Default);

                // What a fresh build-21 install writes (a dry run on the untouched copy).
                PcInstallOptions dry = real();
                dry.DryRun = true;
                PcInstallReport fresh = PcInstaller.Install(dry);
                Assert.True(fresh.ExitCode == 0, fresh.Error);
                Assert.DoesNotContain("bottile", fresh.Applied);
                string build21Game = fresh.WrittenSha256[GameCopy];

                // A build-20 install: its plan had 'bottile' right before 'store' (Game.dll), and install.json says build 20.
                PcInstallOptions o20 = real();
                List<IlPatch> plan20 = PatchPlan.For(BuildProfile.PcSteam6372049, PatchPlan.Ids(BuildProfile.PcSteam6372049).Default).ToList();
                plan20.Insert(plan20.FindIndex(p => p.Id == "store"), new Build20BotTilePatch());
                o20.Patches = plan20;
                PcInstallReport r20 = PcInstaller.Install(o20);
                Assert.True(r20.ExitCode == 0, r20.Error);
                string manifestPath = copy.Path_("McvsOffline/install.json");
                InstallManifest m20 = InstallManifest.Parse(File.ReadAllText(manifestPath));
                m20.ServerBuild = older;
                File.WriteAllBytes(manifestPath, m20.ToBytes());
                List<string> game20 = m20.FindWritten(GameCopy).Patches;
                Assert.Equal("store", game20[game20.IndexOf("bottile") + 1]);
                Assert.Contains("bottile", m20.Patches);
                Assert.Equal(OpCodes.Ldc_I4_1, ConnectionTypeOpCode(copy, GameCopy));
                Assert.NotEqual(build21Game, GameFolder.Sha256(File.ReadAllBytes(copy.Path_(GameCopy))));

                // Under the build-21 kit: OLDER BUILD, not DAMAGED, and nothing about the removed patch.
                PcStatusReport before = PcInstaller.Status(copy.Game, () => false, copy.SpikeData, null, kit);
                List<string> beforeText = Print(PcReportText.Status(before));
                Assert.Equal(PcInstallState.Older, before.State);
                Assert.Equal("OLDER BUILD (" + older + "; this kit installs build " + kit + "): " + PcInstaller.OlderAdvice, before.StateDetail);
                Assert.Empty(before.Errors);
                Assert.DoesNotContain(before.Problems, p => p.Contains("bottile") || p.Contains("witness"));
                Assert.DoesNotContain(beforeText, l => l.Contains("DAMAGED"));
                Assert.Empty(PcInstaller.CheckWritten(copy.Game, InstallManifest.Parse(File.ReadAllText(manifestPath)), TestEnvironment.Net452Refs));

                // The upgrade, with the build-20 default list on the command line (a saved kit shortcut, the old docs).
                PcInstallOptions o21 = real();
                o21.OptionalPatches = PcReportText.ParsePatchList(Build20Defaults);
                PcInstallReport r21 = PcInstaller.Install(o21);
                List<string> installText = Print(PcReportText.Install(r21, false));
                Assert.True(r21.ExitCode == 0, r21.Error);
                Assert.Equal(new[] { BotTileWarning }, r21.Warnings);
                Assert.Contains("warning: " + BotTileWarning, installText);
                Assert.DoesNotContain("bottile", r21.Applied);
                Assert.Equal(fresh.Applied, r21.Applied);

                // The Game.dll a fresh build-21 install writes, from the game folder's own Game.dll; install.json without 'bottile'.
                Assert.Equal(build21Game, GameFolder.Sha256(File.ReadAllBytes(copy.Path_(GameCopy))));
                Assert.Equal(fresh.WrittenSha256, r21.WrittenSha256);
                Assert.Equal(OpCodes.Ldc_I4_2, ConnectionTypeOpCode(copy, GameCopy));
                InstallManifest m21 = InstallManifest.Parse(File.ReadAllText(manifestPath));
                Assert.Equal(kit, m21.ServerBuild);
                Assert.DoesNotContain("bottile", m21.Patches);
                Assert.Equal(game20.Where(id => id != "bottile"), m21.FindWritten(GameCopy).Patches);
                Assert.Equal(m20.Patches.Where(id => id != "bottile"), m21.Patches);

                PcInstallReport verify = PcInstaller.Verify(copy.Game, TestEnvironment.Net452Refs, () => false, copy.SpikeData, null, kit);
                Print(PcReportText.Verify(verify));
                Assert.True(verify.ExitCode == 0, verify.Error);
                Assert.Equal(PcInstallState.Current, verify.Status.State);

                // The first start of build 21 (its host's witness line has never named 'bottile'): active, current.
                Directory.CreateDirectory(copy.Path_("McvsOffline/logs"));
                File.WriteAllText(copy.Path_("McvsOffline/logs/bootstrap.log"),
                                  PcBootActivationTests.Start("2026-11-02T18:30:00", true,
                                                              "patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes, store yes, inbox yes"));
                PcStatusReport after = PcInstaller.Status(copy.Game, () => false, copy.SpikeData, null, kit);
                List<string> afterText = Print(PcReportText.Status(after));
                Assert.Equal(PcInstallState.Current, after.State);
                Assert.StartsWith("active at ", after.Activation);
                Assert.Empty(after.Errors);
                Assert.Empty(after.Problems);
                Assert.DoesNotContain(afterText, l => l.Contains("bottile") || l.Contains("DAMAGED") || l.Contains("NOT VERIFIED"));
            }
        }
    }
}
