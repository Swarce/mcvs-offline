using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Patcher;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using Xunit;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>The per-build patch ids and order (docs/design/pc-port.md §8.1, §8.8, §12.4).</summary>
    public class PatchPlanTests
    {
        private static readonly BuildProfile Pc = BuildProfile.PcSteam6372049;

        [Fact]
        public void ThePcIdsAreTheDesignsTable()
        {
            PatchIds ids = PatchPlan.Ids(Pc);
            Assert.Equal(new[] { "graphics", "headless", "headless-diag", "headless-engine", "hook-pc", "physics", "quicklaunch" }, Sorted(ids.Mandatory));
            Assert.Equal(new[] { "adminlink", "adminpage", "bottile", "geo", "nativeonline", "signatures", "store", "waits" }, Sorted(ids.Default));
            Assert.Equal(new[] { "forum" }, Sorted(ids.Optional));
            Assert.Equal(new[] { "debloat", "finder", "game", "hook", "lan", "targetsdk" }, Sorted(ids.Excluded));
        }

        [Fact]
        public void ThePcPlanKeepsTheFixedOrder()
        {
            Assert.Equal(new[] { "hook-pc", "headless", "headless-engine", "physics", "quicklaunch", "headless-diag", "graphics" },
                         PatchPlan.For(Pc, new string[0]).Select(p => p.Id));
            Assert.Equal(new[] { "hook-pc", "headless", "forum", "headless-engine", "physics", "quicklaunch", "headless-diag", "graphics" },
                         PatchPlan.For(Pc, new[] { "forum" }).Select(p => p.Id));
            IReadOnlyList<IlPatch> full = PatchPlan.For(Pc, PatchPlan.Ids(Pc).Default.Concat(PatchPlan.Ids(Pc).Optional));
            Assert.Equal(new[]
            {
                "hook-pc", "headless", "bottile", "store", "signatures", "adminpage", "forum",
                "headless-engine", "physics", "quicklaunch", "headless-diag", "nativeonline", "waits", "adminlink", "graphics", "geo",
            }, full.Select(p => p.Id));
            Assert.Equal(new[] { "EngineBridge.dll", "Game.dll", "Gameloft.Engine.dll", "Gameloft.Util.dll" },
                         full.Select(p => p.AssemblyFile).Distinct());
            Assert.Equal(PatchPlan.For(Pc, null).Select(p => p.Id), PatchPlan.For(Pc, new string[0]).Select(p => p.Id));
        }

        [Theory]
        [InlineData("hook")]
        [InlineData("finder")]
        [InlineData("lan")]
        [InlineData("debloat")]
        [InlineData("targetsdk")]
        [InlineData("game")]
        [InlineData("no-such-patch")]
        public void AnIdThePcBuildExcludesIsRefused(string id)
        {
            Assert.Throws<ArgumentException>(() => PatchPlan.For(Pc, new[] { id }));
        }

        [Theory]
        [InlineData("hook-pc")]
        [InlineData("quicklaunch")]
        [InlineData("headless-diag")]
        public void APcOnlyIdIsRefusedOnAndroid(string id)
        {
            Assert.Throws<ArgumentException>(() => PatchPlan.For(BuildProfile.Android1_17_32, new[] { id }));
        }

        [Fact]
        public void TheAndroidPlanIsTheApkPatchersSelection()
        {
            var cases = new List<ICollection<string>>
            {
                new PatcherOptions().Patches,
                new string[0],
                PatcherOptions.OptionalPatches,
                new[] { "lan" },
                new[] { "forum", "geo" },
            };
            foreach (ICollection<string> options in cases)
            {
                Assert.Equal(ApkPatcher.SelectPatches(options).Select(p => p.Id), PatchPlan.For(BuildProfile.Android1_17_32, options).Select(p => p.Id));
            }
            PatchIds ids = PatchPlan.Ids(BuildProfile.Android1_17_32);
            Assert.Equal(PatcherOptions.MandatoryPatches, ids.Mandatory);
            Assert.Equal(new[] { "forum", "lan" }, Sorted(ids.Optional));
        }

        [Fact]
        public void EachPatchRefusesABuildItHasNoTableFor()
        {
            // §8.8: before the module is even looked at (the PC-only patches on Android, the Android-only ones on PC).
            foreach (IlPatch patch in new IlPatch[] { new EngineBridgeHookPatch(), new QuickLaunchPatch(), new HeadlessDiagnosticsPatch() })
            {
                PatchException refused = Assert.Throws<PatchException>(() => patch.Apply(null, new PatchContext(null, 51780, BuildProfile.Android1_17_32)));
                Assert.Equal(patch.Id + " is not supported on build android-1.17.32", refused.Message);
            }
            foreach (IlPatch patch in new IlPatch[] { new BootstrapHookPatch(), new FinderPatch(), new LanPatch(), new DebloatEnginePatch() })
            {
                PatchException refused = Assert.Throws<PatchException>(() => patch.Apply(null, new PatchContext(null, 51780, Pc)));
                Assert.Equal(patch.Id + " is not supported on build pc-steam-6372049", refused.Message);
            }
            Assert.Equal(BootstrapHookPatch.HelperName, EngineBridgeHookPatch.HelperName);
        }

        private static string[] Sorted(IEnumerable<string> ids)
        {
            return ids.OrderBy(i => i, StringComparer.Ordinal).ToArray();
        }
    }
}
