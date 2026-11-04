using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Patcher;
using McvsOffline.Patcher.Core;
using Xunit;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// Patch ids that earlier builds had (<see cref="PatchPlan.Removed"/>): 'bottile' went in build 21 with the hidden PRACTICE
    /// tile it served. A saved command line or the build-20 docs' default list may still name it: --patch drops it with a
    /// warning (Android and PC alike) instead of refusing the run, since leaving out a patch that no longer exists changes
    /// nothing; an id that never existed is still an error.
    /// </summary>
    public class RemovedPatchTests
    {
        /// <summary>The build-20 Android default set (PLAYER-SETUP.md printed it as the default --patch list).</summary>
        private const string Build20Defaults = "nativeonline,waits,geo,bottile,finder,store,signatures,inbox,debloat,adminpage,adminlink,graphics,targetsdk,game";

        private const string PcBuild20Warning = Pc.PcBuild20UpgradeTests.BotTileWarning;

        [Fact]
        public void TheWarningNamesTheIdAndTheBuild()
        {
            Assert.Equal(new[] { "bottile" }, PatchPlan.Removed.Keys);
            Assert.Equal(PcBuild20Warning, PatchPlan.RemovedWarning("bottile"));
            // PatchPlan.For itself still refuses a removed id: the command lines drop it first.
            Assert.Throws<ArgumentException>(() => PatchPlan.For(BuildProfile.PcSteam6372049, new[] { "bottile" }));
            Assert.Throws<ArgumentException>(() => PatchPlan.For(BuildProfile.Android1_17_32, new[] { "bottile" }));
        }

        [Fact]
        public void NoBuildHasTheBotTilePatchAnyMore()
        {
            Assert.DoesNotContain("bottile", PatcherOptions.DefaultPatches);
            Assert.DoesNotContain("bottile", PatcherOptions.OptionalPatches);
            Assert.DoesNotContain(ApkPatcher.AllPatches, p => p.Id == "bottile");
            foreach (BuildProfile build in new[] { BuildProfile.Android1_17_32, BuildProfile.PcSteam6372049 })
            {
                PatchIds ids = PatchPlan.Ids(build);
                Assert.DoesNotContain("bottile", ids.Mandatory.Concat(ids.Default).Concat(ids.Optional).Concat(ids.Excluded));
                Assert.DoesNotContain(PatchPlan.For(build, ids.Default.Concat(ids.Optional)), p => p.Id == "bottile");
            }
            Assert.Null(typeof(PatcherOptions).Assembly.GetType("McvsOffline.Patcher.Patches.BotTilePatch"));
        }

        [Fact]
        public void AStaleBotTileOnTheAndroidCommandLineIsIgnored()
        {
            SortedSet<string> defaults = PatcherOptions.ParsePatchList("default");
            Assert.Equal(defaults, PatcherOptions.ParsePatchList(Build20Defaults));
            Assert.Equal(new[] { "finder", "geo" }, PatcherOptions.ParsePatchList("geo,BOTTILE,finder"));
            Assert.Empty(PatcherOptions.ParsePatchList("bottile"));

            var warnings = new List<string>();
            Assert.Equal(defaults, PatcherOptions.ParsePatchList(Build20Defaults + ",bottile", warnings));
            Assert.Equal(new[] { PcBuild20Warning }, warnings);   // once, however often it is named

            PatcherOptions o = McvsOffline.Patcher.Program.Parse(new[] { "--apk", "a", "--server", "s", "--out", "o", "--patch", Build20Defaults });
            Assert.Equal(defaults, o.Patches);
            Assert.Equal(new[] { PcBuild20Warning }, o.Warnings);
            Assert.Empty(McvsOffline.Patcher.Program.Parse(new[] { "--apk", "a", "--server", "s", "--out", "o" }).Warnings);

            // An id that never existed still stops the run.
            var ex = Assert.Throws<ArgumentException>(() => PatcherOptions.ParsePatchList("geo,bottiles"));
            Assert.Contains("unknown patch 'bottiles'", ex.Message);
            Assert.DoesNotContain("bottile,", ex.Message);
        }
    }
}
