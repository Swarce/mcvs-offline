using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// The 'inbox' patch on the real Steam Game.dll (1.17.30, MCVS_PC_REFS), executed on net8 next to the unpatched one: the same
    /// cases as <see cref="RealInboxPatchTests"/> (display cap with Gear kept, the UNLOCKALL message, the claim popup, the refresh
    /// latch) and I5, the null check PC's <c>KillSignatureRewardItem.IsRandom</c> lacks, plus ILVerify of the patch alone (the only
    /// other PC ILVerify is <see cref="PcInstallerTests.RealFolderEndToEnd"/>, of the whole default set). The fingerprints, the plan,
    /// the second application and JIT of the whole PC set are RealPcPatchTests' and RealPcJitTests'. Skipped without the files, a
    /// failure under MCVS_REQUIRE_PC=1.
    /// </summary>
    [Collection("GameHooks")]
    public class RealPcInboxPatchTests
    {
        private static readonly BuildProfile Pc = BuildProfile.PcSteam6372049;

        private readonly ITestOutputHelper output;

        public RealPcInboxPatchTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static readonly object PatchGate = new object();
        private static byte[] server;
        private static Dictionary<string, byte[]> patched;
        private static List<string> patchNotes;

        private static Dictionary<string, byte[]> Patched(string pc, out byte[] serverImage)
        {
            lock (PatchGate)
            {
                if (patched == null)
                {
                    server = PcHookServer.Compile();
                    var notes = new List<string>();
                    patched = RealPc.Patch(pc, server, notes, Pc, new InboxPatch());
                    patchNotes = notes;
                }
                serverImage = server;
                return patched;
            }
        }

        [Fact]
        public void TheShippedAndThePatchedIlAreTheVerifiedOnes()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            Dictionary<string, byte[]> images = Patched(pc, out _);
            foreach (string n in patchNotes) output.WriteLine(n);
            Assert.Equal(5, patchNotes.Count);
            Assert.Contains(patchNotes, n => n.Contains("get_IsRandom") && n.Contains("null Subtype2"));
            ReaderParameters reader = RealPc.Reader(pc, null);
            using (reader.AssemblyResolver)
            using (var original = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(Path.Combine(pc, "Game.dll"))), reader))
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(images["Game.dll"]), reader))
            {
                TestEnvironment.AssertPcCorlib(original);
                RealInboxPatchTests.AssertShippedFingerprints(original, true);
                RealInboxPatchTests.AssertPatchedShape(original, module, true);
            }
        }

        [Fact]
        public void TheDisplayListTheUnlockAllMessageAndThePopupBehaveAsOnAndroid()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            Dictionary<string, byte[]> images = Patched(pc, out byte[] serverImage);
            using (var original = new InboxScene(pc, new Dictionary<string, byte[]>(), serverImage))
            using (var fixedScene = new InboxScene(pc, images, serverImage))
            {
                RealInboxPatchTests.JitPatched(fixedScene.Game, output);
                RealInboxPatchTests.AssertDisplayDifferential(original, fixedScene, output);
                RealInboxPatchTests.AssertUnlockAll(original, fixedScene, output);
                RealInboxPatchTests.AssertPopupAndTotal(fixedScene);
            }
        }

        [Fact]
        public void RefreshUiRebuildsOncePer10SecondWindow()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            Dictionary<string, byte[]> images = Patched(pc, out byte[] serverImage);
            using (var original = new InboxScene(pc, new Dictionary<string, byte[]>(), serverImage))
            using (var fixedScene = new InboxScene(pc, images, serverImage))
            {
                RealInboxPatchTests.AssertRefreshLatch(original, fixedScene, output);
            }
        }

        /// <summary>I5: a kill-signature reward without id (a custom coupon "KillSignature_Avatar") no longer throws in IsRandom.</summary>
        [Fact]
        public void IsRandomOfARewardWithoutIdIsFalseInsteadOfThrowing()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            Dictionary<string, byte[]> images = Patched(pc, out byte[] serverImage);
            using (var original = new InboxScene(pc, new Dictionary<string, byte[]>(), serverImage))
            using (var fixedScene = new InboxScene(pc, images, serverImage))
            {
                foreach (InboxScene s in new[] { original, fixedScene })
                {
                    Type type = s.Game.GetType(InboxPatch.KillSignatureType, true);
                    PropertyInfo isRandom = type.GetProperty("IsRandom", InboxScene.Any);
                    bool IsRandom(string subtype2)
                    {
                        object item = RuntimeHelpers.GetUninitializedObject(type);
                        type.GetProperty("Subtype2", InboxScene.Any).SetValue(item, subtype2);
                        return (bool)isRandom.GetValue(item);
                    }
                    Assert.True(IsRandom("Random"));
                    Assert.True(IsRandom("Rarity03"));
                    Assert.False(IsRandom("12"));
                    if (s.IsPatched)
                    {
                        Assert.False(IsRandom(null));
                    }
                    else
                    {
                        var e = Assert.Throws<TargetInvocationException>(() => IsRandom(null));
                        Assert.IsType<NullReferenceException>(e.InnerException);
                    }
                }
            }
        }

        /// <summary>The 12 net452 assemblies PC ILVerify runs against (as <see cref="PcInstallerTests.RealFolderEndToEnd"/>'s).</summary>
        private static readonly string[] Net452 =
        {
            "mscorlib", "System", "System.Core", "System.Net.Http", "System.Xml", "System.Xml.Linq", "System.IO.Compression", "System.Windows.Forms",
            "System.Drawing", "System.Runtime.Serialization", "System.Data", "System.Numerics",
        };

        /// <summary>
        /// ILVerify of the Steam Game.dll patched with 'inbox' alone (I1-I5, the PC claim state machine included): the patched methods
        /// verify without a finding, and the whole assembly has exactly the shipped findings afterwards.
        /// </summary>
        [Fact]
        public void IlVerifyAcceptsThePatchedMethods()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            string tool = RealAdminPagePatchTests.IlVerifyTool();
            if (tool == null)
            {
                Assert.False(TestEnvironment.RequirePc, "MCVS_REQUIRE_PC=1 but ilverify was not found (MCVS_ILVERIFY or ~/.dotnet/tools/ilverify)");
                output.WriteLine("ilverify not found: skipped");
                return;
            }
            Dictionary<string, byte[]> images = Patched(pc, out byte[] serverImage);
            string dir = TestEnvironment.NewTempDir("pc-inbox-ilverify");
            try
            {
                string patchedFile = Path.Combine(dir, "Game.dll"), serverFile = Path.Combine(dir, "McvsOffline.Server.dll");
                File.WriteAllBytes(patchedFile, images["Game.dll"]);
                File.WriteAllBytes(serverFile, serverImage);
                string shipped = Path.Combine(pc, "Game.dll");
                var references = Net452.Select(n => Path.Combine(TestEnvironment.Net452Refs, n + ".dll"))
                    .Concat(PcGameCopy.ManagedFiles.Where(f => f != "Game.dll").Select(f => Path.Combine(pc, f)))
                    .Concat(new[] { serverFile }).ToList();
                var system = new[] { "--system-module", "mscorlib" };

                var arguments = system.Concat(new[]
                {
                    "-i", @"\]Game\.InboxComponent\.(GenerateRewardsObjects|GetObject|RefreshUI)$",
                    "-i", @"\]<ClaimRewardsForMessage>d__\d+\.MoveNext$",   // ILVerify names a nested type without its declaring one
                    "-i", @"\]Game\.KillSignatureRewardItem\.get_IsRandom$",
                    "-i", @"\]Game\.McvsInbox\.",
                    "--statistics",
                }).ToList();
                foreach (string file in new[] { shipped, patchedFile })
                {
                    var (exit, text) = RealAdminPagePatchTests.IlVerify(tool, file, references, arguments);
                    output.WriteLine((file == patchedFile ? "patched: " : "original: ") + text.Trim());
                    Assert.True(exit == 0, "ilverify exit code " + exit + ":\n" + text);
                    Assert.Contains("Methods verified: " + (file == patchedFile ? 5 + McvsInboxMethods : 5), text);
                }

                List<string> before = RealAdminPagePatchTests.Findings(RealAdminPagePatchTests.IlVerify(tool, shipped, references, system).Text);
                List<string> after = RealAdminPagePatchTests.Findings(RealAdminPagePatchTests.IlVerify(tool, patchedFile, references, system).Text);
                output.WriteLine("whole assembly: " + before.Count + " shipped findings, " + after.Count + " after patching");
                Assert.Equal(before, after);
                Assert.DoesNotContain(after, l => l.Contains("InboxComponent") || l.Contains("<ClaimRewardsForMessage>") || l.Contains("McvsInbox") ||
                                                  l.Contains("KillSignatureRewardItem"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        /// <summary>The methods of the added Game.McvsInbox (Rewards, Popup, Total, RefreshDue, Report, Forward).</summary>
        private const int McvsInboxMethods = 6;
    }
}
