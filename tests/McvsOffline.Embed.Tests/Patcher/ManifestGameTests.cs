using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher;
using McvsOffline.Patcher.Manifest;
using McvsOffline.Patcher.Zip;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// Patch "game" (docs/design/pc-port.md §10, §12.5): android:isGame="true" and android:appCategory="game" on
    /// &lt;application&gt;, encoded as aapt2 writes them, in resource-id order, at any target and after "targetsdk" (which
    /// no longer writes appCategory at 36, so there is never a second one); every other element and attribute stays. Also
    /// pins invariant P5's manifest half: rename, debloat and targetsdk 35 without "game" give the manifest of every
    /// build before it. Checked on synthetic manifests, on the player's real manifest and with aapt2 (MCVS_BUILD_TOOLS;
    /// MCVS_REQUIRE_BUILD_TOOLS=1 fails instead of skipping when aapt2 is missing).
    /// </summary>
    public class ManifestGameTests
    {
        private const string Ns = AxmlWriter.AndroidNs;

        /// <summary>
        /// SHA-256 of rename -> debloat -> targetsdk 35 on 1.17.32's real manifest: the bytes the main-dev 7f24f82 patcher
        /// writes (patcher/Manifest unchanged since; the same 17,184 bytes as build 12, 56956ef). "game" must not change them.
        /// </summary>
        public const string Target35WithoutGameSha256 = "8f8474628a8ac81bff723ee7f5997e3ceb64abf228294207c96c920a4a42a55b";

        /// <summary>
        /// SHA-256 of "game" on that manifest (17,284 bytes): byte for byte the research prototype's output that aapt2
        /// xmltree and badging were checked on (samsung-game report §3.2).
        /// </summary>
        public const string Target35WithGameSha256 = "eae945af360667a2d6b1399bc8b1469b38fbc78eb49d62712196bffe21890917";

        private readonly ITestOutputHelper output;

        public ManifestGameTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>Both flags on &lt;application&gt;, once each, with aapt2's encoding (no raw value), in resource-id order.</summary>
        internal static void AssertGameFlags(byte[] manifest)
        {
            BinaryXml.Element application = Assert.Single(BinaryXml.Parse(manifest).Elements(), e => e.Path == "manifest/application");
            BinaryXml.Attribute isGame = Assert.Single(application.Attributes, a => a.ResourceId == ManifestGame.IsGameId);
            Assert.Equal((BinaryXml.AndroidNamespace, "isGame", (byte)0x12, 0xFFFFFFFFu, -1), (isGame.Namespace, isGame.Name, isGame.DataType, isGame.Data, isGame.RawValue));
            BinaryXml.Attribute category = Assert.Single(application.Attributes, a => a.ResourceId == ManifestGame.AppCategoryId);
            Assert.Equal((BinaryXml.AndroidNamespace, "appCategory", (byte)0x10, 0u, -1), (category.Namespace, category.Name, category.DataType, category.Data, category.RawValue));
            var ids = application.Attributes.Select(a => a.ResourceId).Where(id => id != 0).ToList();
            Assert.Equal(ids.OrderBy(id => id), ids);
        }

        /// <summary>aapt2, or null when it is missing (a failure under MCVS_REQUIRE_BUILD_TOOLS=1).</summary>
        private string Aapt2()
        {
            if (TestEnvironment.Aapt2 != null) return TestEnvironment.Aapt2;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_BUILD_TOOLS") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_BUILD_TOOLS=1 but aapt2 is missing (set MCVS_BUILD_TOOLS)");
            output.WriteLine("aapt2 not found (set MCVS_BUILD_TOOLS): skipped");
            return null;
        }

        /// <summary>The debloat fixture with &lt;application&gt; attributes around the two new ids, like the real one.</summary>
        private static byte[] Debloated(bool utf8 = false, Action<AxmlWriter.Element, AxmlWriter.Element> tweak = null)
        {
            AxmlWriter.Element manifest = ManifestDebloatTests.DebloatLikeManifest((m, a) =>
            {
                a.Attributes.Add((Ns, "largeHeap", true));                         // 0x0101035a
                a.Attributes.Add((Ns, "restrictedAccountType", "com.google"));     // 0x010103d5, then isGame 0x010103f4
                a.Attributes.Add((Ns, "networkSecurityConfig", "@xml/nsc"));       // 0x01010527, then appCategory 0x01010545
                tweak?.Invoke(m, a);
            });
            return ManifestDebloat.Apply(ManifestPatch.Apply(AxmlWriter.Write(manifest, utf8), null), null);
        }

        /// <summary>
        /// Asserts the result of "game" on <paramref name="input"/>: the flags, the two notes, the elements of the input in
        /// order, and &lt;application&gt; with its original attributes unchanged, the two new ones merged in by id.
        /// </summary>
        private static void AssertOnlyTheFlagsAdded(byte[] input, byte[] result, List<string> notes)
        {
            AssertGameFlags(result);
            Assert.Equal(2, notes.Count);
            Assert.All(notes, n => Assert.StartsWith(ManifestGame.NotePrefix, n));
            Assert.StartsWith("manifest: game: android:isGame=\"true\" on <application> (", notes[0]);
            Assert.StartsWith("manifest: game: android:appCategory=\"game\" on <application> (", notes[1]);
            Assert.DoesNotContain(notes, n => n.StartsWith("manifest: android:", StringComparison.Ordinal));   // ManifestTargetSdkTests counts those

            IReadOnlyList<BinaryXml.Element> before = BinaryXml.Parse(input).Elements(), after = BinaryXml.Parse(result).Elements();
            Assert.Equal(before.Select(e => e.Path + " " + e.AndroidName + " " + e.Line), after.Select(e => e.Path + " " + e.AndroidName + " " + e.Line));
            for (int i = 0; i < before.Count; i++)
            {
                if (before[i].Path == "manifest/application") continue;
                Assert.Equal(before[i].Attributes.Select(a => a.ToString()), after[i].Attributes.Select(a => a.ToString()));
            }
            BinaryXml.Element was = before.Single(e => e.Path == "manifest/application"), now = after.Single(e => e.Path == "manifest/application");
            Assert.Equal(was.Attributes.Select(a => a.ResourceId).Concat(new[] { ManifestGame.IsGameId, ManifestGame.AppCategoryId }).OrderBy(id => id),
                         now.Attributes.Select(a => a.ResourceId));
            Assert.Equal(was.Attributes.Select(a => a.ToString()),
                         now.Attributes.Where(a => a.ResourceId != ManifestGame.IsGameId && a.ResourceId != ManifestGame.AppCategoryId).Select(a => a.ToString()));
        }

        // ------------------------------------------------------------------ synthetic manifests

        [Theory]
        [InlineData(29, false)]
        [InlineData(29, true)]
        [InlineData(35, false)]
        [InlineData(35, true)]
        [InlineData(36, false)]
        public void TheFlagsComeAfterTargetSdkAtEveryTarget(int target, bool utf8)
        {
            byte[] debloated = Debloated(utf8);
            byte[] targeted = ManifestTargetSdk.Edit(debloated, target, null);   // 29 returns the input
            Assert.Equal(target == 29, ReferenceEquals(debloated, targeted));
            var notes = new List<string>();
            byte[] result = ManifestGame.Edit(targeted, notes);
            foreach (string n in notes) output.WriteLine(n);

            AssertOnlyTheFlagsAdded(targeted, result, notes);
            if (target != 29) ManifestTargetSdkTests.AssertInvariant(result, target, new[] { "com.gameloft.android.XamarinMainActivity", "com.gameloft.pushnotification.LocalPushReceiver" }, game: true);
            else Assert.Equal((ManifestTargetSdkTests.MinSdk, 29u), ManifestTargetSdkTests.UsesSdk(result));
            // The new attributes sit where aapt2 would put them: isGame after restrictedAccountType, appCategory after
            // networkSecurityConfig (and before allowNativeHeapPointerTagging when targetsdk added it).
            List<uint> ids = BinaryXml.Parse(result).Elements().Single(e => e.Path == "manifest/application").Attributes.Select(a => a.ResourceId).ToList();
            Assert.Equal(ids.IndexOf(0x010103d5) + 1, ids.IndexOf(ManifestGame.IsGameId));
            Assert.Equal(ids.IndexOf(0x01010527) + 1, ids.IndexOf(ManifestGame.AppCategoryId));
            if (target != 29) Assert.Equal(ids.IndexOf(ManifestGame.AppCategoryId) + 1, ids.IndexOf(ManifestTargetSdk.AllowNativeHeapPointerTaggingId));

            if (Aapt2() == null) return;
            List<string> treeBefore = Aapt2Dump.XmlTree(targeted).Split('\n').ToList(), treeAfter = Aapt2Dump.XmlTree(result).Split('\n').ToList();
            output.WriteLine(string.Join("\n", treeAfter));
            // aapt2 sees exactly two new lines.
            string isGame = Assert.Single(treeAfter, l => l.Contains("isGame"));
            string category = Assert.Single(treeAfter, l => l.Contains("appCategory"));
            Assert.EndsWith("A: http://schemas.android.com/apk/res/android:isGame(0x010103f4)=true", isGame);
            Assert.EndsWith("A: http://schemas.android.com/apk/res/android:appCategory(0x01010545)=0", category);
            Assert.Equal(treeBefore, treeAfter.Where(l => l != isGame && l != category));
            Assert.Contains("application-isGame", Aapt2Dump.Badging(Aapt2Dump.MinimalApk(result)).Split('\n'));
        }

        [Fact]
        public void TargetSdk36NoLongerWritesAppCategoryAndGameAddsItOnce()
        {
            byte[] targeted = ManifestTargetSdk.Edit(Debloated(), 36, null);
            BinaryXml.Element application = BinaryXml.Parse(targeted).Elements().Single(e => e.Path == "manifest/application");
            Assert.DoesNotContain(application.Attributes, a => a.ResourceId == ManifestGame.AppCategoryId);
            Assert.Single(application.Attributes, a => a.ResourceId == ManifestTargetSdk.EnableOnBackInvokedCallbackId);
            byte[] result = ManifestGame.Edit(targeted, null);
            Assert.Single(BinaryXml.Parse(result).Elements().Single(e => e.Path == "manifest/application").Attributes, a => a.ResourceId == ManifestGame.AppCategoryId);
            // targetsdk wants a stock input: after "game" it refuses (the order in ApkPatcher is targetsdk, then game).
            Assert.Contains("already sets attribute 0x01010545", Assert.Throws<InvalidDataException>(() => ManifestTargetSdk.Edit(ManifestGame.Edit(Debloated(), null), 35, null)).Message);
        }

        [Fact]
        public void AManifestThatIsNotStockForTheFlagsIsRefused()
        {
            void Refused(byte[] manifest, string why)
            {
                var notes = new List<string>();
                var ex = Assert.Throws<InvalidDataException>(() => ManifestGame.Edit(manifest, notes));
                output.WriteLine(ex.Message);
                Assert.Contains(why, ex.Message);
                Assert.StartsWith("AndroidManifest.xml cannot get the game flags, nothing was written: ", ex.Message);
                Assert.Empty(notes);
            }
            // Either attribute already there, whatever its value.
            Refused(Debloated(tweak: (m, a) => a.Attributes.Add((Ns, "isGame", true))), "<application> already sets <application {" + Ns + "}isGame=");
            Refused(Debloated(tweak: (m, a) => a.Attributes.Add((Ns, "isGame", false))), "isGame=");
            Refused(Debloated(tweak: (m, a) => a.Attributes.Add((Ns, "appCategory", 0))), "appCategory=");
            Refused(Debloated(tweak: (m, a) => a.Attributes.Add((Ns, "appCategory", 7))), "appCategory=");
            // Not exactly one <application> (written directly: debloat itself wants the one with its components).
            Refused(AxmlWriter.Write(ManifestDebloatTests.DebloatLikeManifest((m, a) => m.Children.Remove(a))), "<application> occurs 0 times");
            Refused(AxmlWriter.Write(ManifestDebloatTests.DebloatLikeManifest((m, a) => m.Add(new AxmlWriter.Element("application")))), "<application> occurs 2 times");
            // A second run.
            Refused(ManifestGame.Edit(Debloated(), null), "already sets");
        }

        [Fact]
        public void TheOptionsCarryGameByDefaultAndNotOnPc()
        {
            Assert.Contains(ManifestGame.PatchId, PatcherOptions.DefaultPatches);
            Assert.Contains(ManifestGame.PatchId, PatcherOptions.OptionalPatches);
            Assert.Contains(ManifestGame.PatchId, new PatcherOptions().Patches);
            Assert.DoesNotContain(ApkPatcher.AllPatches, p => p.Id == ManifestGame.PatchId);   // no IL
            Assert.Equal(ManifestTargetSdk.AppCategoryId, ManifestGame.AppCategoryId);
            Assert.Equal(0x010103f4u, ManifestGame.IsGameId);
        }

        // ------------------------------------------------------------------ the player's manifest

        private bool RealManifest(out byte[] shipped, out byte[] arsc)
        {
            shipped = arsc = null;
            if (TestEnvironment.GameApk == null)
            {
                if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                    throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but the original APK (MCVS_APK) is missing");
                output.WriteLine("original APK (MCVS_APK) not available: skipped");
                return false;
            }
            ZipArchiveReader.Archive apk = ZipArchiveReader.Read(TestEnvironment.GameApk);
            shipped = apk.Find(ManifestPatch.EntryName).GetData();
            arsc = apk.Find("resources.arsc").GetData();
            Assert.Equal(ManifestTargetSdk.ShippedManifestSha256, ManifestTargetSdk.Sha256(shipped));
            return true;
        }

        [Theory]
        [InlineData(29)]
        [InlineData(35)]
        [InlineData(36)]
        public void TheRealManifestGetsExactlyTheTwoFlags(int target)
        {
            if (!RealManifest(out byte[] shipped, out byte[] arsc)) return;
            byte[] debloated = ManifestDebloat.Apply(ManifestPatch.Apply(shipped, null), null);
            byte[] targeted = target == 29 ? debloated : ManifestTargetSdk.Apply(shipped, debloated, target, null);
            var notes = new List<string>();
            byte[] result = ManifestGame.Edit(targeted, notes);
            foreach (string n in notes) output.WriteLine(n);

            AssertOnlyTheFlagsAdded(targeted, result, notes);
            if (target != 29) ManifestTargetSdkTests.AssertInvariant(result, target, ManifestTargetSdkTests.ExportedAfterDebloat, game: true);
            List<string> RootAttributes(byte[] m) => BinaryXml.Parse(m).Elements()[0].Attributes.Select(a => a.ToString()).ToList();
            Assert.Equal(RootAttributes(shipped), RootAttributes(result));
            // Two attributes (20 bytes each), two resource-map entries and string offsets (4 + 4 each), the two names.
            Assert.Equal(BinaryXml.Parse(targeted).ResourceMapCount + 2, BinaryXml.Parse(result).ResourceMapCount);
            output.WriteLine(targeted.Length + " -> " + result.Length + " bytes");

            if (Aapt2() == null) return;
            string before = Aapt2Dump.XmlTree(targeted), after = Aapt2Dump.XmlTree(result);
            List<string> added = after.Split('\n').Except(before.Split('\n')).ToList();
            Assert.Equal(new[]
            {
                "        A: http://schemas.android.com/apk/res/android:isGame(0x010103f4)=true",
                "        A: http://schemas.android.com/apk/res/android:appCategory(0x01010545)=0",
            }, added);
            Assert.Equal(before.Split('\n').Length + 2, after.Split('\n').Length);
            List<string> badging = Aapt2Dump.Badging(Aapt2Dump.MinimalApk(result, arsc)).Split('\n').ToList();
            Assert.Contains("application-isGame", badging);
            Assert.Contains("targetSdkVersion:'" + target + "'", badging);
            Assert.Contains("launchable-activity: name='com.gameloft.android.XamarinMainActivity'  label='MCVS' icon=''", badging);
        }

        [Fact]
        public void WithoutGameTheManifestIsTheOneOfEveryEarlierBuild()
        {
            // Invariant P5, manifest half: --patch without "game" gives today's manifest bytes.
            if (!RealManifest(out byte[] shipped, out _)) return;
            byte[] manifest = ManifestTargetSdk.Apply(shipped, ManifestDebloat.Apply(ManifestPatch.Apply(shipped, null), null), 35, null);
            Assert.Equal(Target35WithoutGameSha256, ManifestTargetSdk.Sha256(manifest));
            Assert.Equal(17184, manifest.Length);
            Assert.DoesNotContain(BinaryXml.Parse(manifest).Elements().Single(e => e.Path == "manifest/application").Attributes,
                                  a => a.ResourceId == ManifestGame.IsGameId || a.ResourceId == ManifestGame.AppCategoryId);
            byte[] game = ManifestGame.Edit(manifest, null);
            Assert.Equal(Target35WithGameSha256, ManifestTargetSdk.Sha256(game));
            Assert.Equal(17284, game.Length);
        }
    }
}
