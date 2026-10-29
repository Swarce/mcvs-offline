using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>The two build profiles (docs/design/pc-port.md §3.4, §7.2): their data, and the PC values against the real install.</summary>
    public class BuildProfileTests
    {
        private readonly ITestOutputHelper output;

        public BuildProfileTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void TheAndroidProfileIsKnownBuild()
        {
            BuildProfile android = BuildProfile.Android1_17_32;
            Assert.Equal("android-1.17.32", android.Id);
            Assert.False(android.IsPc);
            Assert.Equal(KnownBuild.Version, android.GameVersion);
            Assert.Equal("2620:66102:1.17.32:android:googleplay", android.ClientId);
            Assert.Equal(KnownBuild.MonoProfileVersion, android.CorlibVersion);
            Assert.Equal("7cec85d7bea7798e", android.CorlibPublicKeyToken);
            Assert.Same(KnownBuild.PatchedOriginals, android.PatchedOriginals);
            Assert.Empty(android.UnchangedWitnesses);
            Assert.Empty(android.OriginalSha256);
            Assert.Same(KnownBuild.ServerAllowedReferences, android.ServerAllowedReferences);
            Assert.Null(android.ServerAllowedModuleRefs);
            Assert.Equal("GameOptions_android.json", android.GameOptionsFile);
        }

        [Fact]
        public void ThePcProfileHasTheSteamBuildsData()
        {
            BuildProfile pc = BuildProfile.PcSteam6372049;
            Assert.Equal("pc-steam-6372049", pc.Id);
            Assert.True(pc.IsPc);
            Assert.Equal("1.17.30", pc.GameVersion);
            Assert.Equal("2620:70425:1.17.30:steam:steam", pc.ClientId);
            Assert.Equal(new Version(4, 0, 0, 0), pc.CorlibVersion);
            Assert.Equal("b77a5c561934e089", pc.CorlibPublicKeyToken);
            Assert.Equal(new[] { "EngineBridge.dll", "Game.dll", "Gameloft.Engine.dll", "Gameloft.Util.dll" }, pc.PatchedOriginals.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(new[] { "GameManager.dll", "Gameloft.OnlinePlatform.dll" }, pc.UnchangedWitnesses.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(8, pc.OriginalSha256.Count);
            Assert.Equal(new[] { "Gameloft.Logs", "System", "System.Core", "mscorlib" }, pc.ServerAllowedReferences.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(new[] { "kernel32", "steam_api", "user32" }, pc.ServerAllowedModuleRefs.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal("GameOptions_windows.json", pc.GameOptionsFile);
            Assert.Equal(0x0102, pc.ExeCharacteristics);
        }

        [Fact]
        public void APatchContextDefaultsToAndroidAndTakesABuild()
        {
            Assert.Same(BuildProfile.Android1_17_32, new PatchContext(null, 51780).Build);
            Assert.Same(BuildProfile.PcSteam6372049, new PatchContext(null, 51780, BuildProfile.PcSteam6372049).Build);
            Assert.Throws<ArgumentNullException>(() => new PatchContext(null, 51780, null));
        }

        [Fact]
        public void ThePcHashesAndMvidsAreTheRealFiles()
        {
            string folder = TestEnvironment.PcRefs;
            if (folder == null)
            {
                Assert.False(TestEnvironment.RequirePc, "MCVS_REQUIRE_PC=1 but MCVS_PC_REFS has no Steam install");
                output.WriteLine("the Steam install is not available (MCVS_PC_REFS): skipped");
                return;
            }
            BuildProfile pc = BuildProfile.PcSteam6372049;
            foreach (var pair in pc.OriginalSha256)
            {
                using (var sha = SHA256.Create())
                {
                    string actual = string.Concat(sha.ComputeHash(File.ReadAllBytes(Path.Combine(folder, pair.Key))).Select(b => b.ToString("x2")));
                    Assert.True(actual == pair.Value, pair.Key + ": " + actual);
                }
            }
            ReaderParameters reader = TestEnvironment.PcReader(folder, null);
            using (reader.AssemblyResolver)
            {
                foreach (var pair in pc.PatchedOriginals.Concat(pc.UnchangedWitnesses))
                {
                    using (ModuleDefinition module = ModuleDefinition.ReadModule(Path.Combine(folder, pair.Key), reader))
                    {
                        Assert.True(module.Mvid == pair.Value, pair.Key + ": " + module.Mvid);
                        TestEnvironment.AssertPcCorlib(module);
                    }
                }
            }
            using (var stream = File.OpenRead(Path.Combine(folder, "ModernCombatVersus.exe")))
            using (var pe = new System.Reflection.PortableExecutable.PEReader(stream))
            {
                Assert.Equal(pc.ExeCharacteristics, (int)pe.PEHeaders.CoffHeader.Characteristics);
            }
        }
    }
}
