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
    /// Invariant I1 of the settings page (docs/design/settings-page.md §0): the patched APK keeps <c>targetSdkVersion 29</c> and
    /// <c>minSdkVersion 21</c> of the shipped manifest. The design relies on target SDK 29: the game's own restart (a
    /// PendingIntent without FLAG_IMMUTABLE), the share chooser without package-visibility filtering, the Android 10
    /// background-start rules for the relaunch alarm, no forced edge-to-edge in the browser activity, and the StrictMode
    /// file:// share on Android 7-9. Checked on every manifest edit the patcher makes (the permission rename, debloat's
    /// removals) and on a full run of the default patch set over the player's real APK.
    /// </summary>
    public class ManifestTargetSdkTests
    {
        public const uint TargetSdk = 29, MinSdk = 21;

        private readonly ITestOutputHelper output;

        public ManifestTargetSdkTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>The <c>uses-sdk</c> element's android:minSdkVersion and android:targetSdkVersion (integer values).</summary>
        internal static (uint Min, uint Target) UsesSdk(byte[] manifest)
        {
            BinaryXml.Element usesSdk = Assert.Single(BinaryXml.Parse(manifest).Elements(), e => e.Path == "manifest/uses-sdk");
            uint Value(string name)
            {
                BinaryXml.Attribute a = usesSdk.Find(BinaryXml.AndroidNamespace, name);
                Assert.True(a != null, "<uses-sdk> has no android:" + name);
                Assert.True(a.DataType == 0x10 || a.DataType == 0x11, a.ToString());   // TYPE_INT_DEC / TYPE_INT_HEX
                return a.Data;
            }
            return (Value("minSdkVersion"), Value("targetSdkVersion"));
        }

        /// <summary>The attributes of <c>uses-sdk</c> exactly as stored.</summary>
        private static List<string> UsesSdkAttributes(byte[] manifest)
        {
            return BinaryXml.Parse(manifest).Elements().Single(e => e.Path == "manifest/uses-sdk").Attributes.Select(a => a.ToString()).ToList();
        }

        [Fact]
        public void EveryManifestEditOfThePatcherKeepsTheSdkLevels()
        {
            // A manifest shaped like 1.17.32 for the debloat (every target, the vendor permission, uses-sdk 21/29).
            byte[] manifest = AxmlWriter.Write(ManifestDebloatTests.DebloatLikeManifest(), false);
            Assert.Equal((MinSdk, TargetSdk), UsesSdk(manifest));
            byte[] renamed = ManifestPatch.Apply(manifest, null);
            Assert.NotNull(renamed);
            byte[] debloated = ManifestDebloat.Apply(renamed, null);
            Assert.Equal((MinSdk, TargetSdk), UsesSdk(renamed));
            Assert.Equal((MinSdk, TargetSdk), UsesSdk(debloated));
            Assert.Equal((MinSdk, TargetSdk), UsesSdk(ManifestDebloat.Apply(manifest, null)));
        }

        [Fact]
        public void TheRealManifestKeepsItsSdkLevelsThroughEveryManifestEdit()
        {
            string apk = TestEnvironment.GameApk;
            if (apk == null)
            {
                if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                    throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but the original APK (MCVS_APK) is missing");
                output.WriteLine("original APK (MCVS_APK) not available: skipped");
                return;
            }
            byte[] manifest = ZipArchiveReader.Read(apk).Find(ManifestPatch.EntryName).GetData();
            Assert.Equal((MinSdk, TargetSdk), UsesSdk(manifest));   // AndroidManifest.xml:13 of 1.17.32
            List<string> shipped = UsesSdkAttributes(manifest);
            output.WriteLine(string.Join("\n", shipped));
            byte[] renamed = ManifestPatch.Apply(manifest, null);
            Assert.Equal(shipped, UsesSdkAttributes(renamed));
            Assert.Equal(shipped, UsesSdkAttributes(ManifestDebloat.Apply(renamed, null)));
        }

        [Fact]
        public void TheDefaultPatchRunKeepsTargetSdk29()
        {
            string apk = TestEnvironment.GameApk;
            string server = RealAdminPagePatchTests.EmbeddedServer(output);
            if (apk == null && Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but the original APK (MCVS_APK) is missing");
            if (server == null && Environment.GetEnvironmentVariable("MCVS_REQUIRE_EMBEDDED") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_EMBEDDED=1 but the embedded server DLL is missing");
            if (apk == null || server == null)
            {
                output.WriteLine("original APK (MCVS_APK) or embedded server DLL not available: skipped");
                return;
            }

            // The patcher's default run (every default patch, debloat's manifest edit included), signed with a throwaway key.
            string dir = TestEnvironment.NewTempDir("target-sdk");
            string outApk = Path.Combine(dir, "offline.apk");
            var options = new PatcherOptions
            {
                InputApk = apk,
                ServerDll = server,
                OutputApk = outApk,
                Keystore = Path.Combine(dir, "key.p12"),   // never the player's default key
                BuildToolsDir = TestEnvironment.BuildToolsDir,
            };
            Assert.Equal(new SortedSet<string>(PatcherOptions.DefaultPatches, StringComparer.Ordinal), options.Patches);
            var log = new List<string>();
            PatchReport report = ApkPatcher.Run(options, log.Add);
            foreach (string id in PatcherOptions.DefaultPatches)
                Assert.Contains(log, l => l.StartsWith("patch " + id + ": ", StringComparison.Ordinal));
            Assert.Contains(ManifestPatch.EntryName, report.Modified);
            Assert.Contains(report.Notes, n => n.StartsWith("manifest: removed <", StringComparison.Ordinal));

            byte[] manifest = ZipArchiveReader.Read(outApk).Find(ManifestPatch.EntryName).GetData();
            Assert.Equal((MinSdk, TargetSdk), UsesSdk(manifest));
            Assert.Equal(UsesSdkAttributes(ZipArchiveReader.Read(apk).Find(ManifestPatch.EntryName).GetData()), UsesSdkAttributes(manifest));

            if (TestEnvironment.Aapt2 == null)
            {
                output.WriteLine("aapt2 not found (set MCVS_BUILD_TOOLS): badging skipped");
                return;
            }
            string badging = Aapt2Dump.Badging(outApk);
            List<string> lines = badging.Replace("\r", "").Split('\n').ToList();
            Assert.Contains("sdkVersion:'" + MinSdk + "'", lines);
            Assert.Contains("targetSdkVersion:'" + TargetSdk + "'", lines);
        }
    }
}
