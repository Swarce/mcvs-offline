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

        /// <summary>
        /// §7.4 verbatim, for build 6372049's versions: codeBase entries only, the shape WR2 ran with build 13 (4 of 5 assemblies
        /// from McvsOffline\bin), byte for byte (<see cref="ExpectedSha256"/> is WR2's recorded config hash).
        /// </summary>
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
            "    </assemblyBinding>\r\n" +
            "  </runtime>\r\n" +
            "</configuration>\r\n";

        /// <summary>SHA-256 of <see cref="Expected"/>: the config WR2 installed (build 13, docs/design/pc-port.md §13.2 "3ebe99b3…f94c").</summary>
        public const string ExpectedSha256 = "3ebe99b3c93a9540cd7f7b03da55fc048a544501c17e8621457884eae456f94c";

        /// <summary>The five qualifyAssembly lines builds 14 and 15 wrote after the dependentAssembly entries.</summary>
        public const string Build14QualifyLines =
            "      <qualifyAssembly partialName=\"EngineBridge\" fullName=\"EngineBridge, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null\" />\r\n" +
            "      <qualifyAssembly partialName=\"Game\" fullName=\"Game, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null\" />\r\n" +
            "      <qualifyAssembly partialName=\"Gameloft.Engine\" fullName=\"Gameloft.Engine, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null\" />\r\n" +
            "      <qualifyAssembly partialName=\"Gameloft.Util\" fullName=\"Gameloft.Util, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null\" />\r\n" +
            "      <qualifyAssembly partialName=\"McvsOffline.Server\" fullName=\"McvsOffline.Server, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null\" />\r\n";

        /// <summary>The build-14/15 config text: <paramref name="config"/> with <see cref="Build14QualifyLines"/>.</summary>
        public static string Build14Config(string config = Expected)
        {
            return config.Replace("    </assemblyBinding>\r\n", Build14QualifyLines + "    </assemblyBinding>\r\n");
        }

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
            Assert.DoesNotContain("qualifyAssembly", text);
            Assert.Equal(ExpectedSha256, GameFolder.Sha256(bytes));
            Assert.False(PcExeConfig.HasQualifyAssembly(text));
        }

        /// <summary>The build-14/15 text of these tests is what those builds wrote: its SHA-256 is the one build 14's §13.1 recorded.</summary>
        [Fact]
        public void TheBuild14ConfigOfTheseTestsIsTheOneThoseBuildsWrote()
        {
            Assert.Equal("fda12aef617b4cfcd8de0ed0b8ebc3764270a21bbabeaef08113eb09d700c4b6", GameFolder.Sha256(PcExeConfig.Encode(Build14Config())));
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
        public void ParseQualifiedAndHasQualifyAssemblyFindABuild14Leftover()
        {
            string old = Build14Config();
            List<PcQualifiedName> qualified = PcExeConfig.ParseQualified(old);
            Assert.Equal(new[] { "EngineBridge", "Game", "Gameloft.Engine", "Gameloft.Util", "McvsOffline.Server" }, qualified.Select(q => q.PartialName));
            Assert.Equal(BuildCodeBases().Select(c => c.FullName), qualified.Select(q => q.FullName));
            Assert.True(PcExeConfig.HasQualifyAssembly(old));
            Assert.True(PcExeConfig.IsOurs(old));   // ours: install replaces it
            // Parse still reads the five codeBases: the qualifyAssembly siblings are not dependentAssembly elements.
            Assert.Equal(5, PcExeConfig.Parse(old).Count);
            Assert.Empty(PcExeConfig.ParseQualified(Expected));
            Assert.Empty(PcExeConfig.ParseQualified("<configuration><runtime /></configuration>"));
            const string noFullName = "<configuration><runtime><assemblyBinding xmlns=\"urn:schemas-microsoft-com:asm.v1\">" +
                                      "<qualifyAssembly partialName=\"Game\" /></assemblyBinding></runtime></configuration>";
            Assert.Throws<FormatException>(() => PcExeConfig.ParseQualified(noFullName));
            Assert.True(PcExeConfig.HasQualifyAssembly(noFullName));
            Assert.Throws<FormatException>(() => PcExeConfig.ParseQualified("<configuration>"));
            // Outside the binding namespace, and in a file that does not parse: still found.
            Assert.True(PcExeConfig.HasQualifyAssembly("<configuration><qualifyAssembly partialName=\"a\" fullName=\"a, Version=1.0.0.0\" /></configuration>"));
            Assert.True(PcExeConfig.HasQualifyAssembly("<configuration><qualifyAssembly partialName=\"a\""));
            Assert.False(PcExeConfig.HasQualifyAssembly("<configuration>"));
            Assert.False(PcExeConfig.HasQualifyAssembly(null));
        }

        /// <summary>
        /// Install's read-back (and verify) reads the config back to the five codeBases, each pointing at the copy of its own
        /// assembly, and reports a qualifyAssembly entry as an old build-14/15 config. Real install of the stand-in plan; skipped
        /// without MCVS_PC_REFS.
        /// </summary>
        [Fact]
        public void TheReadBackReportsAQualifyAssemblyLineAndACodeBaseToTheWrongFile()
        {
            using (PcGameCopy copy = PcGameCopy.Create(output))
            {
                if (copy == null) return;
                PcInstallReport installed = copy.Install();
                Assert.True(installed.ExitCode == 0, installed.Error);
                string config = copy.Path_(PcExeConfig.FileName);
                string text = File.ReadAllText(config);
                Assert.Equal(Expected, text);
                Assert.Equal(ExpectedSha256, installed.WrittenSha256[PcExeConfig.FileName]);
                InstallManifest manifest = copy.Status().Manifest;
                Assert.Empty(PcInstaller.CheckWritten(copy.Game, manifest, TestEnvironment.Net452Refs));

                File.WriteAllText(config, Build14Config(text));
                List<string> leftover = PcInstaller.CheckWritten(copy.Game, manifest, TestEnvironment.Net452Refs);
                Assert.Contains(PcExeConfig.FileName + " " + PcExeConfig.QualifyLeftoverProblem + ": run install again", leftover);
                Assert.Contains(leftover, p => p.EndsWith("an old build-14/15 config: run install again", StringComparison.Ordinal));

                File.WriteAllText(config, text.Replace("href=\"McvsOffline/bin/Game.dll\"", "href=\"McvsOffline/bin/Gameloft.Util.dll\""));
                List<string> wrong = PcInstaller.CheckWritten(copy.Game, manifest, TestEnvironment.Net452Refs);
                Assert.Contains(PcExeConfig.FileName + ": the codeBase of Game points to McvsOffline/bin/Gameloft.Util.dll, which is the assembly Gameloft.Util", wrong);
            }
        }

        /// <summary>
        /// The full name install checks every redirected copy against is the identity the CLR binds it by (read three ways from
        /// the real Steam file), and the gameload patch's table says the same for the names it qualifies (WR2 F1). Skipped without
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
                Assert.False(PcExeConfig.HasQualifyAssembly(PcExeConfig.Build(new[] { codeBase })));
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
