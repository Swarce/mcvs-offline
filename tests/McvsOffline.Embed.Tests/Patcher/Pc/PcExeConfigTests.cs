using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Pc;
using Xunit;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary><c>ModernCombatVersus.exe.config</c> (docs/design/pc-port.md §7.4): the exact text, href validation, ownership.</summary>
    public class PcExeConfigTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper output;

        public PcExeConfigTests(Xunit.Abstractions.ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>§7.4 verbatim, for build 6372049's versions (the qualifyAssembly entries: WR2 F1).</summary>
        public const string Expected =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<!-- written by McvsOffline (PC install); 'uninstall' removes it; see McvsOffline/install.json -->\r\n" +
            "<configuration>\r\n" +
            "  <runtime>\r\n" +
            "    <assemblyBinding xmlns=\"urn:schemas-microsoft-com:asm.v1\">\r\n" +
            "      <dependentAssembly>\r\n" +
            "        <assemblyIdentity name=\"EngineBridge\" culture=\"neutral\" />\r\n" +
            "        <codeBase version=\"1.0.0.0\" href=\"McvsOffline/bin/EngineBridge.dll\" />\r\n" +
            "      </dependentAssembly>\r\n" +
            "      <dependentAssembly>\r\n" +
            "        <assemblyIdentity name=\"Game\" culture=\"neutral\" />\r\n" +
            "        <codeBase version=\"0.0.0.0\" href=\"McvsOffline/bin/Game.dll\" />\r\n" +
            "      </dependentAssembly>\r\n" +
            "      <dependentAssembly>\r\n" +
            "        <assemblyIdentity name=\"Gameloft.Engine\" culture=\"neutral\" />\r\n" +
            "        <codeBase version=\"0.0.0.0\" href=\"McvsOffline/bin/Gameloft.Engine.dll\" />\r\n" +
            "      </dependentAssembly>\r\n" +
            "      <dependentAssembly>\r\n" +
            "        <assemblyIdentity name=\"Gameloft.Util\" culture=\"neutral\" />\r\n" +
            "        <codeBase version=\"1.0.0.0\" href=\"McvsOffline/bin/Gameloft.Util.dll\" />\r\n" +
            "      </dependentAssembly>\r\n" +
            "      <dependentAssembly>\r\n" +
            "        <assemblyIdentity name=\"McvsOffline.Server\" culture=\"neutral\" />\r\n" +
            "        <codeBase version=\"0.0.0.0\" href=\"McvsOffline/bin/McvsOffline.Server.dll\" />\r\n" +
            "      </dependentAssembly>\r\n" +
            "      <qualifyAssembly partialName=\"EngineBridge\" fullName=\"EngineBridge, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null\" />\r\n" +
            "      <qualifyAssembly partialName=\"Game\" fullName=\"Game, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null\" />\r\n" +
            "      <qualifyAssembly partialName=\"Gameloft.Engine\" fullName=\"Gameloft.Engine, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null\" />\r\n" +
            "      <qualifyAssembly partialName=\"Gameloft.Util\" fullName=\"Gameloft.Util, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null\" />\r\n" +
            "      <qualifyAssembly partialName=\"McvsOffline.Server\" fullName=\"McvsOffline.Server, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null\" />\r\n" +
            "    </assemblyBinding>\r\n" +
            "  </runtime>\r\n" +
            "</configuration>\r\n";

        public static List<PcCodeBase> BuildCodeBases()
        {
            return new List<PcCodeBase>
            {
                new PcCodeBase("EngineBridge", new Version(1, 0, 0, 0), "McvsOffline/bin/EngineBridge.dll"),
                new PcCodeBase("Game", new Version(0, 0, 0, 0), "McvsOffline/bin/Game.dll"),
                new PcCodeBase("Gameloft.Engine", new Version(0, 0, 0, 0), "McvsOffline/bin/Gameloft.Engine.dll"),
                new PcCodeBase("Gameloft.Util", new Version(1, 0, 0, 0), "McvsOffline/bin/Gameloft.Util.dll"),
                new PcCodeBase("McvsOffline.Server", new Version(0, 0, 0, 0), "McvsOffline/bin/McvsOffline.Server.dll"),
            };
        }

        [Fact]
        public void TheConfigIsTheDesignsTextExactly()
        {
            string text = PcExeConfig.Build(BuildCodeBases());
            Assert.Equal(Expected, text);
            byte[] bytes = PcExeConfig.Encode(text);
            Assert.NotEqual(0xEF, bytes[0]);   // no BOM
            Assert.Equal(Encoding.UTF8.GetBytes(Expected), bytes);
            Assert.DoesNotContain("<startup", text);
            Assert.DoesNotContain("<probing", text);
            Assert.DoesNotContain("loadFromRemoteSources", text);
        }

        [Theory]
        [InlineData("")]
        [InlineData("C:/Games/McvsOffline/bin/Game.dll")]
        [InlineData("/McvsOffline/bin/Game.dll")]
        [InlineData("McvsOffline\\bin\\Game.dll")]
        [InlineData("../McvsOffline/bin/Game.dll")]
        [InlineData("McvsOffline/../../Game.dll")]
        [InlineData("file:///C:/Game.dll")]
        public void HrefsOutsideTheExeFolderAreRefused(string href)
        {
            Assert.Throws<ArgumentException>(() => PcExeConfig.Build(new[] { new PcCodeBase("Game", new Version(0, 0, 0, 0), href) }));
        }

        [Fact]
        public void OursAndTheSpikesAreRecognised()
        {
            Assert.True(PcExeConfig.IsOurs(Expected));
            Assert.False(PcExeConfig.IsSpikeConfig(Expected));
            // The spike's own writer, in its own words (tools/pc-spike/ExeConfig.cs).
            string spikeSource = FixtureCompiler.RepoSource("tools", "pc-spike", "ExeConfig.cs");
            Assert.Contains("OwnerComment = \"" + PcExeConfig.SpikeOwnerComment + "\"", spikeSource);
            string spike = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + PcExeConfig.SpikeOwnerComment + "\r\n<configuration />\r\n";
            Assert.True(PcExeConfig.IsSpikeConfig(spike));
            Assert.False(PcExeConfig.IsOurs(spike));
            Assert.False(PcExeConfig.IsOurs("<configuration><startup /></configuration>"));
            Assert.False(PcExeConfig.IsOurs(null));
        }

        [Fact]
        public void ParseReadsTheCodeBasesBack()
        {
            List<PcCodeBase> parsed = PcExeConfig.Parse(Expected);
            Assert.Equal(5, parsed.Count);
            Assert.Equal("Gameloft.Util", parsed[3].AssemblyName);
            Assert.Equal(new Version(1, 0, 0, 0), parsed[3].Version);
            Assert.Equal("McvsOffline/bin/McvsOffline.Server.dll", parsed[4].Href);
            Assert.Empty(PcExeConfig.Parse("<configuration><runtime /></configuration>"));
            Assert.Throws<FormatException>(() => PcExeConfig.Parse("<configuration>"));
            Assert.Throws<FormatException>(() => PcExeConfig.Parse("<!DOCTYPE x [<!ENTITY a \"b\">]><configuration>&a;</configuration>"));
        }

        [Fact]
        public void ParseQualifiedReadsTheQualifyAssemblyEntriesBack()
        {
            List<PcQualifiedName> qualified = PcExeConfig.ParseQualified(Expected);
            Assert.Equal(new[] { "EngineBridge", "Game", "Gameloft.Engine", "Gameloft.Util", "McvsOffline.Server" }, qualified.Select(q => q.PartialName));
            Assert.Equal(BuildCodeBases().Select(c => c.FullName), qualified.Select(q => q.FullName));
            Assert.Equal("Game, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null", qualified[1].FullName);
            // Parse still reads the five codeBases: the qualifyAssembly siblings are not dependentAssembly elements.
            Assert.Equal(5, PcExeConfig.Parse(Expected).Count);
            Assert.Empty(PcExeConfig.ParseQualified("<configuration><runtime /></configuration>"));
            const string noFullName = "<configuration><runtime><assemblyBinding xmlns=\"urn:schemas-microsoft-com:asm.v1\">" +
                                      "<qualifyAssembly partialName=\"Game\" /></assemblyBinding></runtime></configuration>";
            Assert.Throws<FormatException>(() => PcExeConfig.ParseQualified(noFullName));
            Assert.Throws<FormatException>(() => PcExeConfig.ParseQualified("<configuration>"));
        }

        /// <summary>
        /// Install's read-back (and verify) checks each qualifyAssembly against the copy its codeBase points to: a wrong version,
        /// a missing entry and an entry without a codeBase are problems. Real install of the stand-in plan; skipped without
        /// MCVS_PC_REFS.
        /// </summary>
        [Fact]
        public void TheReadBackChecksEachQualifiedNameAgainstItsCopy()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                PcInstallReport installed = copy.Install();
                Assert.True(installed.ExitCode == 0, installed.Error);
                string config = copy.Path_(PcExeConfig.FileName);
                string text = File.ReadAllText(config);
                Assert.Equal(Expected, text);
                InstallManifest manifest = copy.Status().Manifest;
                Assert.Empty(PcInstaller.CheckWritten(copy.Game, manifest, TestEnvironment.Net452Refs));

                const string game = "<qualifyAssembly partialName=\"Game\" fullName=\"Game, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null\" />";
                File.WriteAllText(config, text.Replace(game, game.Replace("Version=0.0.0.0", "Version=1.0.0.0")));
                List<string> wrong = PcInstaller.CheckWritten(copy.Game, manifest, TestEnvironment.Net452Refs);
                Assert.Contains(PcExeConfig.FileName + ": expected one qualifyAssembly Game -> \"Game, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null\" " +
                                "(the file's identity), found \"Game, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null\"", wrong);

                File.WriteAllText(config, text.Replace(game + "\r\n", "").Replace("</assemblyBinding>",
                                                       "  <qualifyAssembly partialName=\"Other\" fullName=\"Other, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null\" />\r\n    </assemblyBinding>"));
                List<string> missing = PcInstaller.CheckWritten(copy.Game, manifest, TestEnvironment.Net452Refs);
                Assert.Contains(missing, p => p.Contains("expected one qualifyAssembly Game -> ") && p.EndsWith("found none", StringComparison.Ordinal));
                Assert.Contains(PcExeConfig.FileName + ": qualifyAssembly Other has no codeBase", missing);
            }
        }

        /// <summary>
        /// The qualifyAssembly fullName of every redirected game file is the identity the CLR binds it by (read three ways from the
        /// real Steam file), and the gameload patch's table says the same for the names it qualifies (WR2 F1). Skipped without
        /// MCVS_PC_REFS, a failure under MCVS_REQUIRE_PC=1.
        /// </summary>
        [Fact]
        public void TheQualifiedNamesAreTheShippedAssembliesIdentities()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            foreach (string file in RealPc.PatchedFiles)
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(pc, file));
                var codeBase = new PcCodeBase(GameFolder.ReadAssemblyName(bytes), GameFolder.ReadAssemblyVersion(bytes), "McvsOffline/bin/" + file);
                Assert.Equal(GameFolder.ReadAssemblyFullName(bytes), codeBase.FullName);
                Assert.Equal(System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(pc, file)).FullName, codeBase.FullName);
                Assert.Equal(Path.GetFileNameWithoutExtension(file), codeBase.AssemblyName);
                string text = PcExeConfig.Build(new[] { codeBase });
                Assert.Equal(codeBase.FullName, PcExeConfig.ParseQualified(text).Single().FullName);
            }
            foreach (KeyValuePair<string, string> name in McvsOffline.Patcher.Patches.GameAssemblyLoadPatch.QualifiedNames)
            {
                string path = Path.Combine(pc, name.Key + ".dll");
                Assert.Equal(System.Reflection.AssemblyName.GetAssemblyName(path).FullName, name.Value);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.Equal(new PcCodeBase(name.Key, GameFolder.ReadAssemblyVersion(bytes), "McvsOffline/bin/" + name.Key + ".dll").FullName, name.Value);
            }
        }
    }
}
