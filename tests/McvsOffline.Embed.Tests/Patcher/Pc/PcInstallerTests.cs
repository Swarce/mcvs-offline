using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
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
    /// A temp copy of the parts of the read-only Steam install the installer reads (the exe, every managed DLL, Data\MCVS.game;
    /// not the 3.7 GB data pack): the installer writes into it, never into MCVS_PC_REFS. Deleted on Dispose.
    /// </summary>
    public sealed class PcGameCopy : IDisposable
    {
        /// <summary>The managed assemblies of the depot (the native ones are not needed to patch).</summary>
        public static readonly string[] ManagedFiles =
        {
            "EngineBridge.dll", "Game.dll", "GameManager.dll", "Gameloft.Aspects.dll", "Gameloft.Collections.dll", "Gameloft.Engine.Math.dll",
            "Gameloft.Engine.dll", "Gameloft.Logs.dll", "Gameloft.OnlinePlatform.dll", "Gameloft.Sequences.dll", "Gameloft.Templates.dll",
            "Gameloft.Util.dll", "Newtonsoft.Json.dll",
        };

        private PcGameCopy(string refs)
        {
            Root = TestEnvironment.NewTempDir("pc-install");
            Game = Path.Combine(Root, "Modern Combat Versus");
            Directory.CreateDirectory(Path.Combine(Game, "Data"));
            foreach (string f in ManagedFiles.Concat(new[] { GameFolder.ExeName, "steam_appid.txt" }))
                File.Copy(Path.Combine(refs, f), Path.Combine(Game, f));
            File.Copy(Path.Combine(refs, "Data", "MCVS.game"), Path.Combine(Game, "Data", "MCVS.game"));
            ServerFolder = Path.Combine(Root, "server");
            Directory.CreateDirectory(ServerFolder);
            File.WriteAllBytes(Path.Combine(ServerFolder, GameFolder.ServerFile), PcHookServer.Compile());
            SpikeData = Path.Combine(Root, "LocalAppData", "McvsOffline", "spike");
        }

        public string Root { get; }

        public string Game { get; }

        public string ServerFolder { get; }

        /// <summary>Where Status looks for the spike's data folder in these tests (never the real %LOCALAPPDATA%).</summary>
        public string SpikeData { get; }

        /// <summary>A copy, or null (skipped) without MCVS_PC_REFS / the net452 references; a failure under MCVS_REQUIRE_PC=1.</summary>
        public static PcGameCopy Create(ITestOutputHelper output)
        {
            string refs = TestEnvironment.PcRefs;
            if (refs == null || TestEnvironment.Net452Refs == null)
            {
                Assert.False(TestEnvironment.RequirePc, "MCVS_REQUIRE_PC=1 but MCVS_PC_REFS or MCVS_NET452_REFS is missing");
                output.WriteLine("the Steam install or the net452 references are not available: skipped");
                return null;
            }
            return new PcGameCopy(refs);
        }

        /// <summary>Every file below the game folder: relative path with '/' → SHA-256.</summary>
        public SortedDictionary<string, string> Tree()
        {
            var tree = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string f in Directory.GetFiles(Game, "*", SearchOption.AllDirectories))
                tree[Path.GetRelativePath(Game, f).Replace('\\', '/')] = GameFolder.Sha256(File.ReadAllBytes(f));
            return tree;
        }

        public string Path_(string relative)
        {
            return GameFolder.Resolve(Game, relative);
        }

        /// <summary>The patches that exist on PC before WP4 lands, plus a stand-in for hook-pc (so every file has a patch and a witness).</summary>
        public static List<IlPatch> StandInPlan()
        {
            return new List<IlPatch>
            {
                new StandInHookPcPatch(), new BotTilePatch(), new StorePatch(), new SignaturesPatch(), new HeadlessEnginePatch(), new PhysicsPatch(),
                new GameAssemblyLoadPatch(), new GeoPatch(),
            };
        }

        public PcInstallOptions Options(IPcFileOps files = null)
        {
            return new PcInstallOptions
            {
                GameFolder = Game,
                ServerDll = PcHookServer.Compile(),
                ServerFolder = ServerFolder,
                BclDirectory = TestEnvironment.Net452Refs,
                Patches = StandInPlan(),
                GameRunning = () => false,
                Files = files ?? new PcFileOps(),
                InstallerName = "tests",
                InstallerVersion = "0.0.0-test",
                UtcNow = () => new DateTime(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc),
            };
        }

        public PcInstallReport Install(IPcFileOps files = null)
        {
            return PcInstaller.Install(Options(files));
        }

        public PcInstallReport Uninstall(bool removeData = false, IPcFileOps files = null)
        {
            return PcInstaller.Uninstall(new PcUninstallOptions { GameFolder = Game, RemoveData = removeData, GameRunning = () => false, Files = files ?? new PcFileOps() });
        }

        public PcStatusReport Status()
        {
            return PcInstaller.Status(Game, () => false, SpikeData);
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

    /// <summary>Stands in for WP4's hook-pc in installer tests: adds the §3.9 witness (EngineBridge.Class1::McvsOfflineInit) and nothing else.</summary>
    public sealed class StandInHookPcPatch : IlPatch
    {
        public override string Id => "hook-pc";

        public override string AssemblyFile => "EngineBridge.dll";

        public override string Description => "test stand-in: adds EngineBridge.Class1::McvsOfflineInit (an empty method)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            TypeDefinition type = RequireType(module, "EngineBridge.Class1");
            if (type.Methods.Any(m => m.Name == "McvsOfflineInit")) throw new PatchException("EngineBridge.dll is already patched");
            var method = new MethodDefinition("McvsOfflineInit", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
            method.Body.GetILProcessor().Emit(OpCodes.Ret);
            type.Methods.Add(method);
            context.Notes.Add("EngineBridge.Class1::McvsOfflineInit added (test stand-in)");
        }
    }

    /// <summary>
    /// <see cref="PcInstaller"/> on a temp copy of the real Steam install with the stand-in plan (docs/design/pc-port.md §7.4-§7.6,
    /// §12.3): the written tree, install.json, reinstall, the status states, the refusals, uninstall.
    /// </summary>
    public class PcInstallerTests
    {
        private static readonly string[] OurFiles =
        {
            "McvsOffline/bin/EngineBridge.dll", "McvsOffline/bin/Game.dll", "McvsOffline/bin/Gameloft.Engine.dll", "McvsOffline/bin/Gameloft.Util.dll",
            "McvsOffline/bin/McvsOffline.Server.dll", "McvsOffline/install.json", "ModernCombatVersus.exe.config",
        };

        private readonly ITestOutputHelper output;

        public PcInstallerTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void InstallWritesExactlyTheDesignedTree()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                SortedDictionary<string, string> before = copy.Tree();
                PcInstallReport report = copy.Install();
                Assert.True(report.ExitCode == 0, report.Error);
                List<string> printed = PcReportText.Install(report, false);
                foreach (string line in printed) output.WriteLine(line);
                // The last lines: the exe and the config Windows must read, by full path, and how to check that it did (§7.6, F).
                Assert.Equal(new[]
                {
                    "exe      " + Path.Combine(copy.Game, "ModernCombatVersus.exe"),
                    "config   " + Path.Combine(copy.Game, "ModernCombatVersus.exe.config") + " (Windows reads it when that exe starts)",
                    "next     start the game once, then run status: it must say activation: active",
                }, printed.Skip(printed.Count - 3));
                Assert.Equal(PcInstaller.NotVerified, copy.Status().Activation);
                PcInstallOptions dry = copy.Options();
                dry.DryRun = true;
                Assert.DoesNotContain(PcReportText.Install(PcInstaller.Install(dry), true), l => l.StartsWith("next ", StringComparison.Ordinal));
                SortedDictionary<string, string> after = copy.Tree();

                // P1: every original unchanged, and exactly our seven files added.
                foreach (var pair in before) Assert.Equal(pair.Value, after[pair.Key]);
                Assert.Equal(OurFiles.OrderBy(f => f, StringComparer.Ordinal), after.Keys.Except(before.Keys).OrderBy(f => f, StringComparer.Ordinal));
                Assert.Equal(OurFiles, report.Written.ToArray());   // §7.4 order: bin, install.json, config
                foreach (string f in OurFiles) Assert.Equal(after[f], report.WrittenSha256[f]);

                Assert.Equal(PcExeConfigTests.Expected, File.ReadAllText(copy.Path_(PcExeConfig.FileName)));
                InstallManifest m = InstallManifest.Parse(File.ReadAllText(copy.Path_("McvsOffline/install.json")));
                Assert.Equal("pc-steam-6372049", m.BuildId);
                Assert.Equal(BuildProfile.PcSteam6372049.OriginalSha256["ModernCombatVersus.exe"], m.ExeSha256);
                Assert.Equal(GameFolder.PatchedFiles, m.Originals.Select(o => o.File));
                foreach (InstallOriginal o in m.Originals)
                {
                    Assert.Equal(BuildProfile.PcSteam6372049.OriginalSha256[o.File], o.Sha256);
                    Assert.Equal(BuildProfile.PcSteam6372049.PatchedOriginals[o.File], o.Mvid);
                }
                Assert.Equal(OurFiles.Where(f => f != "McvsOffline/install.json"), m.Written.Select(w => w.Path));
                foreach (InstallWritten w in m.Written) Assert.Equal(after[w.Path], w.Sha256);
                Assert.Equal(new[] { "hook-pc" }, m.FindWritten("McvsOffline/bin/EngineBridge.dll").Patches);
                Assert.Equal(new[] { "bottile", "store", "signatures" }, m.FindWritten("McvsOffline/bin/Game.dll").Patches);
                Assert.Equal(new[] { "geo" }, m.FindWritten("McvsOffline/bin/Gameloft.Util.dll").Patches);
                InstallWritten server = m.FindWritten("McvsOffline/bin/McvsOffline.Server.dll");
                Assert.Null(server.Patches);
                Assert.False(server.GameLog);   // the stand-in has no Gameloft.Logs reference
                Assert.Equal(new Version(1, 0, 0, 0), m.FindWritten("McvsOffline/bin/EngineBridge.dll").Version);
                Assert.Equal(51780, m.Port);
                Assert.Equal(TestEnvironment.Net452Refs, m.Bcl);
                Assert.Equal("2026-11-02T18:00:00Z", m.WrittenUtc);

                // Each patched copy has its DerivedMvid, one corlib, and its witnesses.
                ReaderParameters reader = TestEnvironment.PcReader(copy.Game, copy.ServerFolder);
                using (reader.AssemblyResolver)
                {
                    foreach (string file in GameFolder.PatchedFiles)
                    {
                        using (ModuleDefinition patched = ModuleDefinition.ReadModule(copy.Path_("McvsOffline/bin/" + file), reader))
                        {
                            TestEnvironment.AssertPcCorlib(patched);
                            InstallWritten w = m.FindWritten("McvsOffline/bin/" + file);
                            Assert.Equal(w.Mvid, patched.Mvid);
                            Assert.NotEqual(BuildProfile.PcSteam6372049.PatchedOriginals[file], patched.Mvid);
                            Assert.Equal(1, patched.AssemblyReferences.Count(r => r.Name == "mscorlib"));
                        }
                    }
                    using (ModuleDefinition game = ModuleDefinition.ReadModule(copy.Path_("McvsOffline/bin/Game.dll"), reader))
                        Assert.True(PcInstaller.Witnesses["store"].IsIn(game));
                    using (ModuleDefinition bridge = ModuleDefinition.ReadModule(copy.Path_("McvsOffline/bin/EngineBridge.dll"), reader))
                        Assert.True(PcInstaller.Witnesses["hook-pc"].IsIn(bridge));
                }

                PcStatusReport status = copy.Status();
                foreach (string line in PcReportText.Status(status)) output.WriteLine(line);
                Assert.Equal(PcInstallState.Current, status.State);
                Assert.Empty(status.UnknownFiles);
                Assert.Empty(status.Leftovers);
                Assert.Empty(status.Errors);
                Assert.Equal(51780, status.InstalledPort);

                PcInstallReport verify = PcInstaller.Verify(copy.Game, TestEnvironment.Net452Refs, () => false);
                Assert.True(verify.ExitCode == 0, verify.Error);
                foreach (string f in OurFiles.Where(f => f != "McvsOffline/install.json")) Assert.Equal(after[f], verify.WrittenSha256[f]);
            }
        }

        [Fact]
        public void ADryRunWritesNothingAndReportsTheSameBytesAReinstallIsIdentical()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                SortedDictionary<string, string> before = copy.Tree();
                PcInstallOptions dry = copy.Options();
                dry.DryRun = true;
                PcInstallReport planned = PcInstaller.Install(dry);
                Assert.True(planned.ExitCode == 0, planned.Error);
                Assert.Equal(before, copy.Tree());

                PcInstallReport first = copy.Install();
                Assert.True(first.ExitCode == 0, first.Error);
                SortedDictionary<string, string> once = copy.Tree();
                foreach (string f in OurFiles) Assert.Equal(planned.WrittenSha256[f], once[f]);

                // A reinstall (the patches always start from the game-folder originals) writes the same bytes again, with a
                // settings.json the game wrote meanwhile (its port is the one installed; another one would go into the geo patch
                // and install.json, PcSettingsPortTests).
                File.WriteAllText(copy.Path_("McvsOffline/settings.json"), "{\"port\": 51780, \"lanMode\": false}");
                PcInstallReport second = copy.Install();
                Assert.True(second.ExitCode == 0, second.Error);
                SortedDictionary<string, string> twice = copy.Tree();
                Assert.Equal(once, new SortedDictionary<string, string>(twice.Where(p => p.Key != "McvsOffline/settings.json").ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal));
                PcStatusReport status = copy.Status();
                Assert.Equal(PcInstallState.Current, status.State);
                Assert.Equal(51780, status.SettingsPort);
            }
        }

        [Fact]
        public void TheStatusStatesFollowWhatIsOnDisk()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                Assert.Equal(PcInstallState.NotInstalled, copy.Status().State);
                Assert.Equal(0, copy.Install().ExitCode);
                Assert.Equal(PcInstallState.Current, copy.Status().State);

                // DAMAGED: one of our copies changed.
                byte[] ours = File.ReadAllBytes(copy.Path_("McvsOffline/bin/Gameloft.Util.dll"));
                File.WriteAllBytes(copy.Path_("McvsOffline/bin/Gameloft.Util.dll"), Flip(ours));
                PcStatusReport damaged = copy.Status();
                Assert.Equal(PcInstallState.Damaged, damaged.State);
                Assert.Contains("McvsOffline/bin/Gameloft.Util.dll changed", damaged.Problems);
                Assert.NotEqual(0, PcInstaller.Verify(copy.Game, TestEnvironment.Net452Refs, () => false).ExitCode);
                File.WriteAllBytes(copy.Path_("McvsOffline/bin/Gameloft.Util.dll"), ours);

                // STALE: Steam changed an original (one byte of Game.dll).
                byte[] game = File.ReadAllBytes(copy.Path_("Game.dll"));
                File.WriteAllBytes(copy.Path_("Game.dll"), Flip(game));
                PcStatusReport stale = copy.Status();
                Assert.Equal(PcInstallState.Stale, stale.State);
                Assert.Contains(PcInstaller.StaleAdvice, stale.StateDetail);
                Assert.Contains(stale.UnknownFiles, u => u.StartsWith("Game.dll", StringComparison.Ordinal));
                PcInstallReport refused = copy.Install();
                Assert.Equal(PcExitCodes.Refused, refused.ExitCode);
                Assert.Contains("not the verified Steam build 6372049", refused.Error);
                File.WriteAllBytes(copy.Path_("Game.dll"), game);

                // PARTIAL: bin without config.
                File.Delete(copy.Path_(PcExeConfig.FileName));
                PcStatusReport partial = copy.Status();
                Assert.Equal(PcInstallState.Partial, partial.State);
                Assert.Contains(PcExeConfig.FileName + " missing", partial.Problems);

                // A leftover temp file is listed.
                File.WriteAllText(copy.Path_("McvsOffline/bin/Game.dll" + GameFolder.TempSuffix), "x");
                Assert.Contains(copy.Status().Leftovers, l => l.StartsWith("McvsOffline/bin/Game.dll.mcvs-tmp", StringComparison.Ordinal));
            }
        }

        [Fact]
        public void ForceInstallsAnUnknownBuildWithAWarning()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                // GameManager.dll is not patched but identifies the build.
                File.WriteAllBytes(copy.Path_("GameManager.dll"), Flip(File.ReadAllBytes(copy.Path_("GameManager.dll"))));
                SortedDictionary<string, string> before = copy.Tree();
                PcInstallReport refused = copy.Install();
                Assert.Equal(PcExitCodes.Refused, refused.ExitCode);
                Assert.Contains("GameManager.dll: UNKNOWN", refused.Error);
                Assert.Equal(before, copy.Tree());

                PcInstallOptions force = copy.Options();
                force.Force = true;
                PcInstallReport forced = PcInstaller.Install(force);
                Assert.True(forced.ExitCode == 0, forced.Error);
                Assert.Contains(forced.Warnings, w => w.Contains("--force"));
            }
        }

        [Fact]
        public void AForeignConfigIsRefusedBeforeAnyWrite()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                File.WriteAllText(copy.Path_(PcExeConfig.FileName), "<configuration><startup><supportedRuntime version=\"v4.0\"/></startup></configuration>");
                SortedDictionary<string, string> before = copy.Tree();
                PcInstallReport report = copy.Install();
                Assert.Equal(PcExitCodes.Refused, report.ExitCode);
                Assert.Contains("is not ours exists; move it away", report.Error);
                Assert.Equal(before, copy.Tree());
                Assert.Contains(copy.Status().Leftovers, l => l.Contains("is not ours"));
                // Uninstall leaves it too.
                Assert.Equal(0, copy.Uninstall().ExitCode);
                Assert.True(File.Exists(copy.Path_(PcExeConfig.FileName)));
            }
        }

        [Fact]
        public void APrefixedArgsFileIsRefusedBeforeAnyWriteEvenWithForce()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                File.WriteAllText(copy.Path_("ModernCombatVersus_args.txt"), "-Foo bar\n");
                SortedDictionary<string, string> before = copy.Tree();
                foreach (bool force in new[] { false, true })
                {
                    PcInstallOptions o = copy.Options();
                    o.Force = force;
                    PcInstallReport report = PcInstaller.Install(o);
                    Assert.Equal(PcExitCodes.Refused, report.ExitCode);
                    Assert.Contains("ModernCombatVersus_args.txt is in the game folder; the game reads it instead of args.txt", report.Error);
                    Assert.Equal(before, copy.Tree());
                }
                PcStatusReport status = copy.Status();
                Assert.Contains(status.Errors, e => e.StartsWith("ModernCombatVersus_args.txt", StringComparison.Ordinal));
                Assert.DoesNotContain("ModernCombatVersus_args.txt", status.OtherFiles);
            }
        }

        [Fact]
        public void TheSpikesConfigAndCopyAreReplaced()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                File.WriteAllText(copy.Path_(PcExeConfig.FileName), "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + PcExeConfig.SpikeOwnerComment + "\r\n<configuration />\r\n");
                Directory.CreateDirectory(copy.Path_("McvsOffline/spike"));
                File.Copy(copy.Path_("EngineBridge.dll"), copy.Path_("McvsOffline/spike/EngineBridge.dll"));
                Directory.CreateDirectory(copy.SpikeData);
                PcStatusReport before = copy.Status();
                Assert.Equal(PcInstallState.NotInstalled, before.State);
                Assert.Contains(before.Leftovers, l => l.Contains("WP0 spike's redirect test"));
                Assert.Contains(before.Leftovers, l => l.Contains("spike's data folder"));

                PcInstallReport report = copy.Install();
                Assert.True(report.ExitCode == 0, report.Error);
                Assert.Contains(report.Notes, n => n.Contains("spike"));
                Assert.True(PcExeConfig.IsOurs(File.ReadAllText(copy.Path_(PcExeConfig.FileName))));
                Assert.False(Directory.Exists(copy.Path_("McvsOffline/spike")));
                Assert.Equal(PcInstallState.Current, copy.Status().State);
            }
        }

        /// <summary>
        /// A build-14/15 install: its config has the five qualifyAssembly lines and install.json records that config's hash, so
        /// every hash matches. Status and verify still report it (most likely .NET ignores the whole file), and install rewrites it (it is ours).
        /// </summary>
        [Fact]
        public void ABuild14ConfigIsReportedAndInstallRewritesIt()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                Assert.Equal(0, copy.Install().ExitCode);
                string configPath = copy.Path_(PcExeConfig.FileName);
                byte[] old = PcExeConfig.Encode(PcExeConfigTests.Build14Config(File.ReadAllText(configPath)));
                File.WriteAllBytes(configPath, old);
                string manifestPath = copy.Path_("McvsOffline/install.json");
                InstallManifest manifest = InstallManifest.Parse(File.ReadAllText(manifestPath));
                manifest.FindWritten(PcExeConfig.FileName).Sha256 = GameFolder.Sha256(old);
                File.WriteAllBytes(manifestPath, manifest.ToBytes());

                PcStatusReport status = copy.Status();
                Assert.Equal("ours", status.Config);
                Assert.Equal(PcInstallState.Damaged, status.State);
                Assert.Equal(new[] { PcExeConfig.FileName + " " + PcExeConfig.QualifyLeftoverProblem }, status.Problems);
                Assert.EndsWith("an old build-14/15 config: run install again", status.StateDetail);
                PcInstallReport verify = PcInstaller.Verify(copy.Game, TestEnvironment.Net452Refs, () => false);
                Assert.Equal(PcExitCodes.Io, verify.ExitCode);
                Assert.Contains("an old build-14/15 config: run install again", verify.Error);

                PcInstallReport again = copy.Install();
                Assert.True(again.ExitCode == 0, again.Error);
                Assert.Equal(PcExeConfigTests.Expected, File.ReadAllText(configPath));
                Assert.Equal(PcInstallState.Current, copy.Status().State);
                Assert.Equal(0, PcInstaller.Verify(copy.Game, TestEnvironment.Net452Refs, () => false).ExitCode);
            }
        }

        /// <summary>
        /// The build-14 owner's work-around on a real install: every file of McvsOffline\bin copied next to the exe. Status names it
        /// (not "Steam updated the game"), install refuses it before any write, and putting the game's files back is "current" again.
        /// </summary>
        [Fact]
        public void AHandCopyOfBinOverTheGameFilesIsNamedAndRefused()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                Assert.Equal(0, copy.Install().ExitCode);
                var originals = new Dictionary<string, byte[]>();
                foreach (string file in GameFolder.PatchedFiles.Concat(new[] { GameFolder.ServerFile }))
                {
                    if (File.Exists(copy.Path_(file))) originals[file] = File.ReadAllBytes(copy.Path_(file));
                    File.Copy(copy.Path_(GameFolder.BinRelative(file)), copy.Path_(file), true);
                }
                PcStatusReport status = copy.Status();
                Assert.Equal(PcInstallState.Stale, status.State);
                Assert.EndsWith(PcInstaller.ManualCopyAdvice, status.StateDetail);
                Assert.Contains("EngineBridge.dll is MCVS Offline's copy from McvsOffline\\bin (copied over the game's own file)", status.Problems);
                Assert.Contains(status.Leftovers, l => l.StartsWith("McvsOffline.Server.dll in the game folder is a hand copy of ours", StringComparison.Ordinal));

                SortedDictionary<string, string> before = copy.Tree();
                PcInstallReport refused = copy.Install();
                Assert.Equal(PcExitCodes.Refused, refused.ExitCode);
                Assert.Equal("EngineBridge.dll, Game.dll, Gameloft.Engine.dll, Gameloft.Util.dll are MCVS Offline's own copies from McvsOffline\\bin, not the game's: " +
                             PcInstaller.ManualCopyAdvice + ". Nothing was written", refused.Error);
                Assert.Equal(before, copy.Tree());

                // Steam's Verify puts the game's files back (and leaves the server copy, which the player deletes).
                foreach (KeyValuePair<string, byte[]> o in originals) File.WriteAllBytes(copy.Path_(o.Key), o.Value);
                File.Delete(copy.Path_(GameFolder.ServerFile));
                Assert.Equal(PcInstallState.Current, copy.Status().State);
            }
        }

        [Fact]
        public void TheRunningGameStopsInstallAndUninstall()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                SortedDictionary<string, string> before = copy.Tree();
                PcInstallOptions o = copy.Options();
                o.GameRunning = () => true;
                Assert.Equal(PcExitCodes.GameRunning, PcInstaller.Install(o).ExitCode);
                Assert.Equal(before, copy.Tree());
                Assert.Equal(0, copy.Install().ExitCode);
                SortedDictionary<string, string> installed = copy.Tree();
                PcInstallReport report = PcInstaller.Uninstall(new PcUninstallOptions { GameFolder = copy.Game, GameRunning = () => true });
                Assert.Equal(PcExitCodes.GameRunning, report.ExitCode);
                Assert.Equal(installed, copy.Tree());
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("-Foo bar\n")]
        public void UninstallGivesBackTheOriginalTreePlusTheData(string playersArgs)
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                if (playersArgs.Length > 0) File.WriteAllText(copy.Path_("args.txt"), playersArgs);
                SortedDictionary<string, string> before = copy.Tree();
                Assert.Equal(0, copy.Install().ExitCode);

                // What the first start adds: the bootstrap's args.txt block, the data, and the game's own files.
                byte[] existing = File.Exists(copy.Path_("args.txt")) ? File.ReadAllBytes(copy.Path_("args.txt")) : null;
                File.WriteAllBytes(copy.Path_("args.txt"), ArgsFile.Merge(existing, new List<ArgEntry>
                {
                    new ArgEntry("ServerLocation", "Embedded"), new ArgEntry("DeviceId", "mcvs0123456789abcdef0123456789abcdef"),
                }).Content);
                Directory.CreateDirectory(copy.Path_("McvsOffline/logs"));
                File.WriteAllText(copy.Path_("McvsOffline/settings.json"), "{}");
                File.WriteAllText(copy.Path_("McvsOffline/logs/bootstrap.log"), "[boot] runtime: CLR 4.0.30319\n[boot] patches: hook-pc yes\n");
                File.WriteAllText(copy.Path_("dr0.bin"), "game");
                File.WriteAllText(copy.Path_("OF_PP.bin"), "game");
                File.WriteAllText(copy.Path_(PcExeConfig.FileName + GameFolder.TempSuffix), "left by a killed install");

                PcStatusReport status = copy.Status();
                Assert.Equal(PcInstallState.Current, status.State);
                Assert.Equal(new[] { "OF_PP.bin", "dr0.bin" }, status.GameWritten.OrderBy(n => n, StringComparer.Ordinal));
                Assert.DoesNotContain("dr0.bin", status.OtherFiles);
                Assert.Equal("[boot] patches: hook-pc yes", status.LastBootstrapLine);
                Assert.True(status.DataFolderBytes > 0);

                PcInstallReport report = copy.Uninstall();
                foreach (string line in PcReportText.Uninstall(report)) output.WriteLine(line);
                Assert.True(report.ExitCode == 0, report.Error);
                SortedDictionary<string, string> after = copy.Tree();
                var expected = new SortedDictionary<string, string>(before, StringComparer.Ordinal);
                expected["McvsOffline/settings.json"] = after["McvsOffline/settings.json"];
                expected["McvsOffline/logs/bootstrap.log"] = after["McvsOffline/logs/bootstrap.log"];
                expected["dr0.bin"] = after["dr0.bin"];
                expected["OF_PP.bin"] = after["OF_PP.bin"];
                Assert.Equal(expected, after);
                if (playersArgs.Length > 0) Assert.Equal(playersArgs, File.ReadAllText(copy.Path_("args.txt")));
                else Assert.False(File.Exists(copy.Path_("args.txt")));
                Assert.False(Directory.Exists(copy.Path_("McvsOffline/bin")));
                Assert.Equal(PcInstallState.NotInstalled, copy.Status().State);
                Assert.Empty(copy.Status().Leftovers);

                // --remove-data: no McvsOffline\ at all.
                PcInstallReport removed = copy.Uninstall(removeData: true);
                Assert.True(removed.ExitCode == 0, removed.Error);
                Assert.False(Directory.Exists(copy.Path_("McvsOffline")));
                Assert.True(File.Exists(copy.Path_("dr0.bin")));
            }
        }

        [Fact]
        public void AnInstallThatWasNeverStartedLeavesNoFolder()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                SortedDictionary<string, string> before = copy.Tree();
                Assert.Equal(0, copy.Install().ExitCode);
                Assert.Equal(0, copy.Uninstall().ExitCode);
                Assert.Equal(before, copy.Tree());
                Assert.False(Directory.Exists(copy.Path_("McvsOffline")));
            }
        }

        [Fact]
        public void ThePatcherCliRunsTheSameInstaller()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                var o = new StringWriter();
                var e = new StringWriter();
                // Without a PC patch set (WP4) the default plan cannot finish; status and a usage error still go through the verb.
                Assert.Equal(PcExitCodes.Ok, McvsOffline.Patcher.Program.Main(new[] { "pc", "--help" }));
                Assert.Equal(PcExitCodes.Usage, PcCli.Run(new[] { "install", "--game", copy.Game }, o, e));
                Assert.Contains("--server", e.ToString());
                Assert.Equal(PcExitCodes.Ok, PcCli.Run(new[] { "status", "--game", copy.Game }, o, e));
                Assert.Contains("state:       not installed", o.ToString());
                Assert.Equal(PcExitCodes.Usage, PcCli.Run(new[] { "uninstall", "--game", copy.Game, "--remove-data" }, o, e));
                Assert.Equal(PcExitCodes.Refused, PcCli.Run(new[] { "verify", "--game", copy.Game, "--bcl", TestEnvironment.Net452Refs }, o, e));
                PcCliOptions parsed = PcCli.Parse(new[] { "install", "--game", "x", "--patch", "none", "--port", "51790", "--dry-run" });
                Assert.Empty(parsed.Patches);
                Assert.Equal(51790, parsed.Port);
                Assert.True(parsed.DryRun);
                Assert.Equal(PatchPlan.Ids(BuildProfile.PcSteam6372049).Default.Concat(new[] { "forum" }), PcReportText.ParsePatchList("default,forum"));
            }
        }

        /// <summary>
        /// A game file that cannot be read (held by an antivirus scan; on Linux a FileShare.None handle refuses the installer's
        /// shared read the same way) is exit code 1 with a message, for every pc command that reads the folder, never a crash.
        /// </summary>
        [Fact]
        public void TheCliReportsAnUnreadableGameFileAsIo()
        {
            string root = TestEnvironment.NewTempDir("pc-cli-io");
            try
            {
                string game = Path.Combine(root, "Modern Combat Versus");
                Directory.CreateDirectory(Path.Combine(game, "Data"));
                File.WriteAllText(Path.Combine(game, GameFolder.ExeName), "exe");
                foreach (string f in GameFolder.PatchedFiles) File.WriteAllText(Path.Combine(game, f), "dll " + f);
                File.WriteAllText(GameFolder.Resolve(game, GameFolder.GameMarker), "game");
                string server = Path.Combine(root, GameFolder.ServerFile);
                File.WriteAllBytes(server, PcHookServer.Compile());
                using (new FileStream(Path.Combine(game, GameFolder.ExeName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    foreach (string[] args in new[]
                    {
                        new[] { "status", "--game", game },
                        new[] { "verify", "--game", game, "--bcl", root },
                        new[] { "install", "--game", game, "--server", server, "--bcl", root },
                    })
                    {
                        var o = new StringWriter();
                        var e = new StringWriter();
                        Assert.Equal(PcExitCodes.Io, PcCli.Run(args, o, e));
                        Assert.StartsWith("error: ", e.ToString());
                    }
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void OnlyASkeletonRefusalCountsAsTheUnfinishedPatchSet()
        {
            Assert.Equal("quicklaunch", SkeletonRefusal(new PcInstallReport
            {
                ExitCode = PcExitCodes.Refused, Error = "a patch site does not match the verified game code, nothing was written: quicklaunch: not implemented",
            }));
            Assert.Null(SkeletonRefusal(new PcInstallReport { ExitCode = PcExitCodes.Refused, Error = "geo: the site is not implemented here" }));
            Assert.Null(SkeletonRefusal(new PcInstallReport { ExitCode = PcExitCodes.Io, Error = "hook-pc: not implemented" }));
            Assert.Null(SkeletonRefusal(new PcInstallReport()));
            // Every skeleton id is in the PC plan's defaults (a renamed id would silently stop matching).
            PatchIds ids = PatchPlan.Ids(BuildProfile.PcSteam6372049);
            foreach (string id in SkeletonPatchIds) Assert.True(ids.Mandatory.Contains(id) || ids.Default.Contains(id), id);
        }

        /// <summary>
        /// Every real PC patch (PatchPlan defaults) with the real PC server DLL, end to end (WP5a on, §11.4, §12.6): the outputs pass the
        /// reference-set check (inside the installer) and ILVerify (findings equal to the originals'), and the written SHA-256 values are
        /// printed for the WR2 record. Until MCVS_REQUIRE_PC_E2E=1 (WP5a) it skips while one of the Day-0 skeleton patches
        /// (<see cref="SkeletonPatchIds"/>) refuses; under it any refusal fails.
        /// </summary>
        [Fact]
        public void RealFolderEndToEnd()
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
                PcInstallOptions o = copy.Options();
                o.Patches = null;   // PatchPlan.For(PcSteam6372049, defaults)
                o.ServerDll = File.ReadAllBytes(serverDll);
                o.ServerFolder = Path.GetDirectoryName(serverDll);
                PcInstallReport report = PcInstaller.Install(o);
                string skeleton = SkeletonRefusal(report);
                if (skeleton != null && !TestEnvironment.RequirePcEndToEnd)
                {
                    output.WriteLine("the Day-0 skeleton of " + skeleton + " still refuses (WP4): skipped until MCVS_REQUIRE_PC_E2E=1 (WP5a); " + report.Error);
                    return;
                }
                Assert.True(report.ExitCode == 0, report.Error);
                foreach (string line in PcReportText.Install(report, false)) output.WriteLine(line);
                Assert.Equal(PcInstallState.Current, copy.Status().State);
                PcInstallReport verify = PcInstaller.Verify(copy.Game, TestEnvironment.Net452Refs, () => false);
                Assert.True(verify.ExitCode == 0, verify.Error);
                output.WriteLine("WR2 expected values (written SHA-256):");
                foreach (string path in verify.Written) output.WriteLine("  " + path + "  " + verify.WrittenSha256[path]);
                IlVerifyOutputs(copy, serverDll);
            }
        }

        /// <summary>The PC patches D0-d added as skeletons that throw "&lt;id&gt;: not implemented" (§11.4); WP4 implements them.</summary>
        public static readonly string[] SkeletonPatchIds = { "hook-pc", "headless-diag", "quicklaunch" };

        /// <summary>The id of the skeleton patch whose refusal <paramref name="report"/> is, or null for any other outcome.</summary>
        public static string SkeletonRefusal(PcInstallReport report)
        {
            if (report.ExitCode != PcExitCodes.Refused || report.Error == null) return null;
            return SkeletonPatchIds.FirstOrDefault(id => report.Error.Contains(id + ": not implemented"));
        }

        /// <summary>ILVerify each patched copy against the 12 net452 assemblies, the other PC DLLs and the server (§12): findings as the original's.</summary>
        private void IlVerifyOutputs(PcGameCopy copy, string serverDll)
        {
            string tool = RealAdminPagePatchTests.IlVerifyTool();
            if (tool == null)
            {
                Assert.False(TestEnvironment.RequirePc, "MCVS_REQUIRE_PC=1 but ilverify was not found (MCVS_ILVERIFY or ~/.dotnet/tools/ilverify)");
                output.WriteLine("ilverify not found: ILVerify part skipped");
                return;
            }
            string bcl = TestEnvironment.Net452Refs;
            string[] net452 =
            {
                "mscorlib", "System", "System.Core", "System.Net.Http", "System.Xml", "System.Xml.Linq", "System.IO.Compression", "System.Windows.Forms",
                "System.Drawing", "System.Runtime.Serialization", "System.Data", "System.Numerics",
            };
            foreach (string file in GameFolder.PatchedFiles)
            {
                string patched = copy.Path_("McvsOffline/bin/" + file);
                string original = copy.Path_(file);
                var references = net452.Select(n => Path.Combine(bcl, n + ".dll"))
                    .Concat(PcGameCopy.ManagedFiles.Where(f => f != file).Select(copy.Path_))
                    .Concat(new[] { serverDll }).ToList();
                var arguments = new[] { "--system-module", "mscorlib" };
                List<string> before = RealAdminPagePatchTests.Findings(RealAdminPagePatchTests.IlVerify(tool, original, references, arguments).Text);
                List<string> after = RealAdminPagePatchTests.Findings(RealAdminPagePatchTests.IlVerify(tool, patched, references, arguments).Text);
                output.WriteLine(file + ": " + before.Count + " findings before, " + after.Count + " after");
                Assert.Equal(before, after);
            }
        }

        public static byte[] Flip(byte[] bytes)
        {
            var copy = (byte[])bytes.Clone();
            copy[copy.Length - 1] ^= 0xFF;
            return copy;
        }
    }
}
