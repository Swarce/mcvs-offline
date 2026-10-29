using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Pc;
using Xunit;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary><c>ModernCombatVersus.exe.config</c> (docs/design/pc-port.md §7.4): the exact text, href validation, ownership.</summary>
    public class PcExeConfigTests
    {
        /// <summary>§7.4 verbatim, for build 6372049's versions.</summary>
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
    }
}
