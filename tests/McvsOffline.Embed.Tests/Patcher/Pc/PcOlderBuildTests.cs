using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Patcher.Pc;
using McvsOffline.Server.Core;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// docs/design/pc-port.md §7.6: an install of an older build is OLDER BUILD, not current. A build-16 install records the
    /// patches build 16 had (no 'inbox'), so no witness of it can be missing; only the recorded <c>serverBuild</c> against the
    /// kit's own server says the install lacks build 17's inbox patch and still sends the unlock codes to the inbox (the freeze).
    /// On <see cref="FakePcInstall"/> (server build 16; hook-pc, gameload, store).
    /// </summary>
    public class PcOlderBuildTests
    {
        private readonly ITestOutputHelper output;

        public PcOlderBuildTests(ITestOutputHelper output)
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
        public void ABuild16InstallWithoutInboxIsOlderUnderABuild17Kit()
        {
            using (var fake = new FakePcInstall())
            {
                PcStatusReport status = PcInstaller.Status(fake.Game, () => false, null, fake.Temp, "17");
                List<string> text = Print(status);
                Assert.DoesNotContain("inbox", status.Manifest.Patches);
                Assert.Equal(PcInstallState.Older, status.State);
                Assert.Equal("OLDER BUILD (16; this kit installs build 17): " + PcInstaller.OlderAdvice, status.StateDetail);
                Assert.Contains("state:       installed, OLDER BUILD - OLDER BUILD (16; this kit installs build 17): run install to update it " +
                                "(your saves and settings stay)", text);
                Assert.Contains("installed by MCVS Offline build 16, this kit is build 17: install.json lists build 16's patches, so the newer ones are " +
                                "not checked", status.Problems);
                Assert.DoesNotContain(text, l => l.Contains("installed, current"));

                PcInstallReport verify = PcInstaller.Verify(fake.Game, null, () => false, null, fake.Temp, "17");
                Assert.Equal(PcExitCodes.Io, verify.ExitCode);
                Assert.Equal(status.StateDetail, verify.Error);
            }
        }

        [Fact]
        public void TheSameBuildANewerOneOrNoComparisonIsCurrent()
        {
            using (var fake = new FakePcInstall())
            {
                foreach (string kit in new[] { "16", "15", null, "?", "" })
                {
                    PcStatusReport status = PcInstaller.Status(fake.Game, () => false, null, fake.Temp, kit);
                    Assert.Equal(PcInstallState.Current, status.State);
                    Assert.Equal("installed, current", status.StateDetail);
                    Assert.DoesNotContain(status.Problems, p => p.Contains("this kit is build"));
                }
                // Verify gets past the state to the re-read (which fails on the made-up install's text "assemblies").
                foreach (PcInstallReport verify in new[]
                         {
                             PcInstaller.Verify(fake.Game, null, () => false, null, fake.Temp, "16"),
                             PcInstaller.Verify(fake.Game, null, () => false, null, fake.Temp),
                         })
                {
                    Assert.Equal(PcInstallState.Current, verify.Status.State);
                    Assert.StartsWith("the install does not read back as written: ", verify.Error);
                }
            }
        }

        /// <summary>Damaged and NOT ACTIVE keep their state and advice; the older build is one more problem line.</summary>
        [Fact]
        public void AWorseStateWinsAndStillNamesTheOlderBuild()
        {
            using (var fake = new FakePcInstall())
            {
                fake.Write("McvsOffline/bin/Game.dll", "changed by hand");
                PcStatusReport damaged = PcInstaller.Status(fake.Game, () => false, null, fake.Temp, "17");
                Print(damaged);
                Assert.Equal(PcInstallState.Damaged, damaged.State);
                Assert.StartsWith("DAMAGED: McvsOffline/bin/Game.dll changed", damaged.StateDetail);
                Assert.Contains(damaged.Problems, p => p.StartsWith("installed by MCVS Offline build 16, this kit is build 17", StringComparison.Ordinal));
            }
            using (var fake = new FakePcInstall())
            {
                fake.GameWrote("dr0.bin", FakePcInstall.Installed.AddMinutes(30));
                PcStatusReport inactive = PcInstaller.Status(fake.Game, () => false, null, fake.Temp, "17");
                Print(inactive);
                Assert.Equal(PcInstallState.NotActive, inactive.State);
                Assert.Equal("installed, NOT ACTIVE (the game started without MCVS Offline)", inactive.StateDetail);
                Assert.Contains(inactive.Problems, p => p.StartsWith("installed by MCVS Offline build 16, this kit is build 17", StringComparison.Ordinal));
            }
        }

        [Theory]
        [InlineData("16", "17", true)]
        [InlineData("9", "17", true)]
        [InlineData("17", "17", false)]
        [InlineData("18", "17", false)]
        [InlineData(null, "17", false)]
        [InlineData("16", null, false)]
        [InlineData("16-dev", "17", false)]
        [InlineData(" 16", "17", false)]
        [InlineData("-1", "17", false)]
        public void IsOlderBuildComparesBuildNumbers(string installed, string installs, bool older)
        {
            Assert.Equal(older, PcInstaller.IsOlderBuild(installed, installs));
        }

        /// <summary>The kit compares with the build of the server DLL it carries: the real server assembly's McvsVersion.Build.</summary>
        [Fact]
        public void ServerBuildOfReadsTheServersBuildConstant()
        {
            Assert.Equal(McvsVersion.Build, PcInstaller.ServerBuildOf(File.ReadAllBytes(typeof(McvsVersion).Assembly.Location)));
            Assert.Equal("23", PcInstaller.ServerBuildOf(StandIn("23")));
            Assert.Null(PcInstaller.ServerBuildOf(StandIn(null)));
            Assert.Null(PcInstaller.ServerBuildOf(new byte[] { 1, 2, 3 }));
            Assert.Null(PcInstaller.ServerBuildOf(null));
        }

        /// <summary>An assembly with McvsOffline.Server.Core.McvsVersion and, unless null, its Build constant.</summary>
        internal static byte[] StandIn(string build)
        {
            using (AssemblyDefinition assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("McvsOffline.Server", new Version(1, 0, 0, 0)),
                                                                                  "McvsOffline.Server.dll", ModuleKind.Dll))
            {
                ModuleDefinition module = assembly.MainModule;
                var type = new TypeDefinition("McvsOffline.Server.Core", "McvsVersion", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
                                              module.TypeSystem.Object);
                module.Types.Add(type);
                if (build != null)
                {
                    type.Fields.Add(new FieldDefinition("Build", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
                                                        module.TypeSystem.String) { Constant = build });
                }
                var stream = new MemoryStream();
                assembly.Write(stream);
                return stream.ToArray();
            }
        }
    }
}
