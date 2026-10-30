using System;
using System.Collections.Generic;
using System.IO;
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
    /// latch) and I5, the null check PC's <c>KillSignatureRewardItem.IsRandom</c> lacks. The fingerprints, the plan, the second
    /// application, JIT and ILVerify of the whole PC set are RealPcPatchTests' and RealPcJitTests'. Skipped without the files, a
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
    }
}
