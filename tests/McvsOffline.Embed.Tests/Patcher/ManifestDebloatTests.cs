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
    /// Patch "debloat", manifest part: exactly the 44 verified elements go (with the intent filters inside them), only
    /// from the verified manifest; on the player's real 1.17.32 manifest the result is checked element by element and,
    /// when Android build-tools are available, read back by the real aapt2 (xmltree and badging).
    /// </summary>
    public class ManifestDebloatTests
    {
        private const string Ns = AxmlWriter.AndroidNs;
        private const string Package = KnownBuild.PackageName;
        private const string Vendor = "glshare.permission.ACCESS_SHARED_DATA";
        private static readonly string Scoped = ManifestPatch.ScopedName(Package, Vendor);

        private readonly ITestOutputHelper output;

        public ManifestDebloatTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static AxmlWriter.Element Named(string tag, string name, params (string Ns, string Name, object Value)[] more)
        {
            return new AxmlWriter.Element(tag, new[] { (Ns, "name", (object)name) }.Concat(more).ToArray());
        }

        private static AxmlWriter.Element Filter(string action)
        {
            return new AxmlWriter.Element("intent-filter").Add(Named("action", action));
        }

        /// <summary>
        /// A manifest shaped like 1.17.32 for the debloat: every target where 1.17.32 has it (receivers with an intent
        /// filter, BootCompletedReceiver's with the same BOOT_COMPLETED action as the kept LocalPushReceiver's), plus some
        /// of the components that must stay. <paramref name="tweak"/> edits (manifest, application) before writing.
        /// </summary>
        internal static AxmlWriter.Element DebloatLikeManifest(Action<AxmlWriter.Element, AxmlWriter.Element> tweak = null)
        {
            var app = Named("application", "com.gameloft.android.FrameworkApplication")
                .Add(Named("activity", "com.gameloft.android.XamarinMainActivity")
                     .Add(Filter("android.intent.action.MAIN").Add(Named("category", "android.intent.category.LAUNCHER"))))
                .Add(Named("provider", "mono.MonoRuntimeProvider", (Ns, "exported", false)))
                .Add(Named("activity", "com.facebook.FacebookActivity"))
                .Add(Named("receiver", "com.gameloft.pushnotification.LocalPushReceiver").Add(Filter("android.intent.action.BOOT_COMPLETED")))
                .Add(Named("provider", Package + ".KeyProvider", (Ns, "permission", Vendor), (Ns, "exported", true), (Ns, "authorities", Package + ".KeyProvider")))
                .Add(Named("meta-data", "com.google.android.gms.version", (Ns, "value", "12451000")));
            var manifest = new AxmlWriter.Element("manifest", (null, "package", Package))
                .Add(new AxmlWriter.Element("uses-sdk", (Ns, "minSdkVersion", 21), (Ns, "targetSdkVersion", 29)))
                .Add(Named("permission", Vendor, (Ns, "protectionLevel", 2)))
                .Add(Named("uses-permission", Vendor));
            foreach (ManifestDebloat.Target t in ManifestDebloat.Targets)
            {
                AxmlWriter.Element e = Named(t.Tag, t.Name);
                if (t.Tag == "receiver") e.Add(Filter(t.Name.EndsWith(".BootCompletedReceiver", StringComparison.Ordinal) ? "android.intent.action.BOOT_COMPLETED" : t.Name + ".ACTION"));
                if (t.Tag == "permission") e.Attributes.Add((Ns, "protectionLevel", 2));
                if (t.Name.EndsWith("_EXTERNAL_STORAGE", StringComparison.Ordinal)) e.Attributes.Add((Ns, "maxSdkVersion", 18));
                (t.Parent == ManifestDebloat.ApplicationPath ? app : manifest).Add(e);
            }
            manifest.Add(Named("uses-permission", "android.permission.INTERNET")).Add(Named("uses-permission", "android.permission.RECEIVE_BOOT_COMPLETED")).Add(app);
            tweak?.Invoke(manifest, app);
            return manifest;
        }

        /// <summary>The elements a debloat of <paramref name="before"/> removes: each target and everything inside it.</summary>
        private static List<BinaryXml.Element> Removed(IReadOnlyList<BinaryXml.Element> before, out List<BinaryXml.Element> targets)
        {
            targets = ManifestDebloat.Targets.Select(t => before.Single(t.Matches)).ToList();
            var tops = targets;
            return before.Where(e => tops.Any(top => IsIn(e, top))).ToList();
        }

        private static bool IsIn(BinaryXml.Element candidate, BinaryXml.Element ancestor)
        {
            for (BinaryXml.Element e = candidate; e != null; e = e.Parent)
            {
                if (ReferenceEquals(e, ancestor)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ the list

        [Fact]
        public void TheTargetsAreTheVerifiedList()
        {
            IReadOnlyList<ManifestDebloat.Target> targets = ManifestDebloat.Targets;
            Assert.Equal(44, targets.Count);
            Assert.Equal(targets.Count, targets.Select(t => t.Parent + " " + t.Tag + " " + t.Name).Distinct().Count());
            var byKind = targets.GroupBy(t => t.Parent + " " + t.Tag).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(new Dictionary<string, int>
            {
                ["manifest permission"] = 1, ["manifest uses-permission"] = 5, ["manifest library"] = 1,
                ["manifest/application activity"] = 26, ["manifest/application service"] = 2, ["manifest/application receiver"] = 6,
                ["manifest/application provider"] = 2, ["manifest/application meta-data"] = 1,
            }, byKind);
            Assert.Equal(20, targets.Count(t => t.Tag == "activity" && t.Reason.StartsWith("ad screen", StringComparison.Ordinal)));
            Assert.All(targets, t => Assert.Equal(t.Parent == ManifestDebloat.ManifestPath ? 1 : 2, t.Depth));
            Assert.All(targets, t => Assert.False(string.IsNullOrWhiteSpace(t.Reason)));

            // What still works offline, or must stay for the code that remains, is never a target.
            foreach (var kept in KeptChildren(Vendor))
                Assert.DoesNotContain(targets, t => t.Parent + "/" + t.Tag == kept.Path && t.Name == kept.Name);
        }

        // ------------------------------------------------------------------ synthetic manifests

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RemovesExactlyTheTargetsAndWhatIsInsideThem(bool utf8)
        {
            byte[] manifest = AxmlWriter.Write(DebloatLikeManifest(), utf8);
            var notes = new List<string>();
            byte[] result = ManifestDebloat.Apply(manifest, notes);

            Assert.Equal(ManifestDebloat.Targets.Count, notes.Count);
            for (int i = 0; i < notes.Count; i++)
            {
                Assert.StartsWith("manifest: removed " + ManifestDebloat.Targets[i], notes[i]);
                Assert.EndsWith("(" + ManifestDebloat.Targets[i].Reason + ")", notes[i]);
            }
            Assert.All(notes.Where(n => n.Contains("<receiver ")), n => Assert.Contains(" with the 2 elements inside it (intent-filter, action) ", n));

            IReadOnlyList<BinaryXml.Element> before = BinaryXml.Parse(manifest).Elements();
            List<BinaryXml.Element> removed = Removed(before, out _);
            Assert.Equal(44 + 2 * 6, removed.Count);
            BinaryXml after = BinaryXml.Parse(result);
            Assert.Equal(before.Except(removed).Select(ManifestDebloat.Key), BinaryXmlTests.Keys(after));
            Assert.Contains(after.Elements(), e => e.Path == "manifest/application/receiver/intent-filter/action" && e.AndroidName == "android.intent.action.BOOT_COMPLETED");
            Assert.Equal(BinaryXml.Parse(manifest).Strings, after.Strings);

            // The permission rename still finds the glshare permission and its three uses afterwards.
            var renameNotes = new List<string>();
            Assert.NotNull(ManifestPatch.Apply(result, renameNotes));
            Assert.Contains("<permission android:name>, <uses-permission android:name>, <provider android:permission>", Assert.Single(renameNotes));
        }

        [Fact]
        public void CommutesWithThePermissionRename()
        {
            byte[] manifest = AxmlWriter.Write(DebloatLikeManifest());
            byte[] renamedFirst = ManifestDebloat.Apply(ManifestPatch.Apply(manifest, null), null);
            Assert.Equal(renamedFirst, ManifestPatch.Apply(ManifestDebloat.Apply(manifest, null), null));
            Assert.Contains(BinaryXml.Parse(renamedFirst).Elements(), e => e.Name == "uses-permission" && e.AndroidName == Scoped);
        }

        [Theory]
        [InlineData("missing", "<activity android:name=\"com.gameloft.android.iab.GMPActivity\"> in <application> is missing")]
        [InlineData("duplicate", "<activity android:name=\"com.google.android.gms.ads.AdActivity\"> in <application> occurs 2 times")]
        [InlineData("misplaced", "<meta-data android:name=\"io.fabric.ApiKey\"> in <application> is missing")]
        [InlineData("moved-up", "<receiver android:name=\"com.gameloft.android.BootCompletedReceiver\"> in <application> is missing")]
        [InlineData("renamed", "<activity android:name=\"com.mopub.common.MoPubBrowser\"> in <application> is missing")]
        [InlineData("uncapped", "android.permission.READ_EXTERNAL_STORAGE\"> in <manifest> (line")]
        [InlineData("cap-missing", "android.permission.WRITE_EXTERNAL_STORAGE\"> in <manifest> (line")]
        [InlineData("min-sdk", "<uses-sdk android:minSdkVersion> is not 21")]
        [InlineData("package", "<manifest package> is com.example.other")]
        public void RefusesAManifestThatIsNotTheVerifiedOne(string kind, string message)
        {
            AxmlWriter.Element Find(AxmlWriter.Element parent, string name) => parent.Children.Single(c => c.Attributes.Any(a => a.Name == "name" && name.Equals(a.Value)));
            AxmlWriter.Element tree = DebloatLikeManifest((manifest, app) =>
            {
                switch (kind)
                {
                    case "missing":
                        app.Children.Remove(Find(app, "com.gameloft.android.iab.GMPActivity"));
                        break;
                    case "duplicate":
                        app.Add(Named("activity", "com.google.android.gms.ads.AdActivity"));
                        break;
                    case "misplaced":   // inside an activity instead of directly in <application>
                    {
                        AxmlWriter.Element meta = Find(app, "io.fabric.ApiKey");
                        app.Children.Remove(meta);
                        Find(app, "com.gameloft.android.XamarinMainActivity").Add(meta);
                        break;
                    }
                    case "moved-up":    // a child of <manifest> instead of <application>
                    {
                        AxmlWriter.Element receiver = Find(app, "com.gameloft.android.BootCompletedReceiver");
                        app.Children.Remove(receiver);
                        manifest.Add(receiver);
                        break;
                    }
                    case "renamed":
                    {
                        AxmlWriter.Element browser = Find(app, "com.mopub.common.MoPubBrowser");
                        browser.Attributes[0] = (Ns, "name", "com.mopub.common.MoPubBrowser2");
                        break;
                    }
                    case "uncapped":
                        Find(manifest, "android.permission.READ_EXTERNAL_STORAGE").Attributes[1] = (Ns, "maxSdkVersion", 28);
                        break;
                    case "cap-missing":
                        Find(manifest, "android.permission.WRITE_EXTERNAL_STORAGE").Attributes.RemoveAt(1);
                        break;
                    case "min-sdk":
                        manifest.Children[0].Attributes[0] = (Ns, "minSdkVersion", 16);
                        break;
                    case "package":
                        manifest.Attributes[0] = (null, "package", "com.example.other");
                        break;
                    default:
                        throw new ArgumentException(kind);
                }
            });
            var notes = new List<string>();
            var ex = Assert.Throws<InvalidDataException>(() => ManifestDebloat.Apply(AxmlWriter.Write(tree), notes));
            output.WriteLine(ex.Message);
            Assert.Contains("is not the verified 1.17.32 manifest, nothing was removed", ex.Message);
            Assert.Contains(message, ex.Message);
            Assert.Contains("leave 'debloat' out of --patch", ex.Message);
            Assert.Empty(notes);
        }

        [Fact]
        public void RefusesAnAlreadyDebloatedOrUnrelatedManifest()
        {
            byte[] once = ManifestDebloat.Apply(AxmlWriter.Write(DebloatLikeManifest()), null);
            var again = Assert.Throws<InvalidDataException>(() => ManifestDebloat.Apply(once, null));
            Assert.Contains("<library android:name=\"moat-mobile-app-kit\"> in <manifest> is missing", again.Message);
            Assert.Throws<InvalidDataException>(() => ManifestDebloat.Apply(AxmlWriter.Write(AxmlWriter.GameLikeManifest()), null));
        }

        [Fact]
        public void Aapt2ReadsTheDebloatedSyntheticManifestAsTheOriginalWithoutTheTargets()
        {
            if (TestEnvironment.Aapt2 == null)
            {
                output.WriteLine("aapt2 not found (set MCVS_BUILD_TOOLS): skipped");
                return;
            }
            byte[] manifest = AxmlWriter.Write(DebloatLikeManifest());
            List<BinaryXml.Element> targets;
            Removed(BinaryXml.Parse(manifest).Elements(), out targets);
            string expected = Aapt2Dump.XmlTree(manifest);
            foreach (BinaryXml.Element t in targets) expected = Aapt2Dump.CutSubtree(expected, t.Name, t.Line);
            Assert.Equal(expected, Aapt2Dump.XmlTree(ManifestDebloat.Apply(manifest, null)));
        }

        // ------------------------------------------------------------------ the real 1.17.32 manifest

        /// <summary>
        /// The &lt;manifest&gt; and &lt;application&gt; children 1.17.32 keeps, in document order (the permission rename turns
        /// <paramref name="vendor"/> into its package-scoped name): everything the offline game uses or that must stay for the
        /// code that remains (FacebookActivity, the Play Games and Facebook ids, gms.version, GoogleApiActivity, the local push
        /// receiver and services, KeyProvider and its permission, the Mono providers, Google Analytics, BILLING, VIBRATE, boot).
        /// </summary>
        internal static (string Path, string Name)[] KeptChildren(string vendor)
        {
            const string M = "manifest/", A = "manifest/application/";
            return new[]
            {
                (M + "uses-sdk", (string)null), (M + "uses-feature", null), (M + "permission", vendor), (M + "uses-permission", vendor),
                (M + "application", "com.gameloft.android.FrameworkApplication"),
                (A + "activity", "com.gameloft.android.XamarinMainActivity"), (A + "provider", "mono.android.MultiDexLoader"),
                (A + "provider", "mono.MonoRuntimeProvider"), (A + "activity", "com.facebook.FacebookActivity"),
                (A + "receiver", "com.google.android.gms.analytics.AnalyticsReceiver"), (A + "service", "com.google.android.gms.analytics.AnalyticsService"),
                (A + "service", "com.google.android.gms.analytics.AnalyticsJobService"),
                (A + "meta-data", "com.samsung.android.infinitedisplay.supported"), (A + "meta-data", "android.max_aspect"),
                (A + "meta-data", "com.google.android.gms.games.APP_ID"), (A + "meta-data", "com.google.android.gms.version"),
                (A + "meta-data", "com.google.android.gms.analytics.globalConfigResource"),
                (A + "service", "com.google.android.gms.analytics.CampaignTrackingService"), (A + "meta-data", "com.facebook.sdk.ApplicationId"),
                (A + "activity", "com.gameloft.android.installer.GameInstaller"), (A + "activity", "com.gameloft.igp.IGPFreemiumActivity"),
                (A + "activity", "com.gameloft.ingamebrowser.InGameBrowser"), (A + "activity", "com.gameloft.android.PackageUtils.PermissionActivity"),
                (A + "receiver", "com.gameloft.pushnotification.LocalPushReceiver"), (A + "service", "com.gameloft.pushnotification.LaunchPushService"),
                (A + "service", "com.gameloft.pushnotification.DeletePushService"), (A + "provider", Package + ".KeyProvider"),
                (A + "activity", "com.google.android.gms.common.api.GoogleApiActivity"),
                (M + "uses-permission", "android.permission.INTERNET"), (M + "uses-permission", "android.permission.ACCESS_NETWORK_STATE"),
                (M + "uses-permission", "android.permission.WAKE_LOCK"), (M + "uses-permission", "com.android.vending.BILLING"),
                (M + "uses-permission", "android.permission.ACCESS_WIFI_STATE"), (M + "uses-permission", "android.permission.VIBRATE"),
                (M + "uses-permission", "android.permission.RECEIVE_BOOT_COMPLETED"), (M + "uses-feature", "android.hardware.touchscreen"),
                (M + "uses-feature", "android.hardware.touchscreen.multitouch"), (M + "supports-screens", null),
            };
        }

        /// <summary>The uses-permission names aapt2 badging lists for the patched 1.17.32 manifest, in order.</summary>
        internal static readonly string[] KeptPermissions =
        {
            Scoped, "android.permission.INTERNET", "android.permission.ACCESS_NETWORK_STATE", "android.permission.WAKE_LOCK", "com.android.vending.BILLING",
            "android.permission.ACCESS_WIFI_STATE", "android.permission.VIBRATE", "android.permission.RECEIVE_BOOT_COMPLETED",
        };

        /// <summary>The player's AndroidManifest.xml and resources.arsc, or null (skipped) unless MCVS_REQUIRE_REAL_CLIENT=1.</summary>
        private byte[] RealManifest(out byte[] resources)
        {
            resources = null;
            string apk = TestEnvironment.GameApk;
            if (apk == null)
            {
                if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                    throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but the original APK (MCVS_APK) is missing");
                output.WriteLine("original APK (MCVS_APK) not available: skipped");
                return null;
            }
            ZipArchiveReader.Archive archive = ZipArchiveReader.Read(apk);
            resources = archive.Find("resources.arsc").GetData();
            return archive.Find(ManifestPatch.EntryName).GetData();
        }

        [Fact]
        public void TheRealManifestLosesExactlyTheTargetsAndKeepsEverythingElse()
        {
            byte[] manifest = RealManifest(out _);
            if (manifest == null) return;
            BinaryXml original = BinaryXml.Parse(manifest);
            IReadOnlyList<BinaryXml.Element> before = original.Elements();
            Assert.Equal(113, before.Count);
            Assert.Equal(manifest, original.ToArray());

            var notes = new List<string>();
            byte[] result = ManifestDebloat.Apply(manifest, notes);
            foreach (string n in notes) output.WriteLine(n);
            Assert.Equal(ManifestDebloat.Targets.Count, notes.Count);

            // Each target exactly once; what goes with them is only their intent filters (23 elements).
            List<BinaryXml.Element> removed = Removed(before, out List<BinaryXml.Element> targets);
            List<BinaryXml.Element> inside = removed.Except(targets).ToList();
            Assert.Equal(23, inside.Count);
            Assert.All(inside, e => Assert.Contains(e.Name, new[] { "intent-filter", "action", "category", "data" }));

            // (depth, tag, android:name) of the result = the original's minus exactly those; and in document order, attributes included.
            BinaryXml after = BinaryXml.Parse(result);
            string Triple(BinaryXml.Element e) => e.Depth + " " + e.Name + " " + e.AndroidName;
            Assert.Equal(before.Except(removed).Select(Triple).OrderBy(x => x, StringComparer.Ordinal),
                         after.Elements().Select(Triple).OrderBy(x => x, StringComparer.Ordinal));
            Assert.Equal(before.Except(removed).Select(ManifestDebloat.Key), BinaryXmlTests.Keys(after));
            Assert.Equal(113 - 67, after.Elements().Count);

            // The children of <manifest> and <application> left are exactly the kept list.
            Assert.Equal(KeptChildren(Vendor), after.Elements().Where(e => e.Depth == 1 || e.Depth == 2).Select(e => (e.Path, e.AndroidName)));

            // Nothing else changed: same pool and resource map, and the bytes are the original minus the removed chunk ranges.
            Assert.Equal(original.Strings, after.Strings);
            Assert.Equal(original.ResourceMapCount, after.ResourceMapCount);
            Assert.Equal(manifest.Length - targets.Sum(t => t.End - t.Start), result.Length);
        }

        [Fact]
        public void Aapt2ReadsTheDebloatedRealManifest()
        {
            byte[] manifest = RealManifest(out byte[] resources);
            if (manifest == null) return;
            if (TestEnvironment.Aapt2 == null)
            {
                output.WriteLine("aapt2 not found (set MCVS_BUILD_TOOLS): skipped");
                return;
            }
            // xmltree: the original dump with each removed element's subtree cut, nothing else.
            byte[] result = ManifestDebloat.Apply(manifest, null);
            Removed(BinaryXml.Parse(manifest).Elements(), out List<BinaryXml.Element> targets);
            string expected = Aapt2Dump.XmlTree(manifest);
            foreach (BinaryXml.Element t in targets) expected = Aapt2Dump.CutSubtree(expected, t.Name, t.Line);
            Assert.Equal(expected, Aapt2Dump.XmlTree(result));

            // badging of the manifest as the patcher writes it (rename + debloat) next to the real resources.arsc.
            string originalBadging = Aapt2Dump.Badging(Aapt2Dump.MinimalApk(manifest, resources));
            string badging = Aapt2Dump.Badging(Aapt2Dump.MinimalApk(ManifestDebloat.Apply(ManifestPatch.Apply(manifest, null), null), resources));
            output.WriteLine(string.Join("\n", badging.Split('\n').Where(l => !l.StartsWith("application-label-", StringComparison.Ordinal))));
            Assert.Contains("package: name='" + Package + "' versionCode='1173200' versionName='1.17.32'", badging);
            Assert.Contains("launchable-activity: name='com.gameloft.android.XamarinMainActivity'", badging);
            Assert.Equal(KeptPermissions, Aapt2Dump.UsesPermissions(badging));
            // Apart from the permissions (and the location feature ACCESS_COARSE_LOCATION implied), aapt2 sees the same app.
            IEnumerable<string> Rest(string b) => b.Split('\n').Where(l => !l.StartsWith("uses-permission:", StringComparison.Ordinal) && !l.Contains("android.hardware.location"));
            Assert.Equal(Rest(originalBadging), Rest(badging));
            Assert.Contains("android.hardware.location", originalBadging);
            Assert.DoesNotContain("android.hardware.location", badging);
        }
    }

    /// <summary>
    /// Full patcher runs on the player's real APK (MCVS_APK) with the embedded server (scripts/build.sh): the default run
    /// (debloat included) gives an APK apksigner accepts, without the Crashlytics/Fabric files, whose manifest is the
    /// permission rename plus the debloat; without debloat the manifest is the rename alone, byte for byte, and the files stay.
    /// </summary>
    public class RealDebloatPipelineTests
    {
        private readonly ITestOutputHelper output;

        public RealDebloatPipelineTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private bool Inputs(out string apk, out string server)
        {
            apk = TestEnvironment.GameApk;
            server = TestEnvironment.EmbeddedServerDll;
            if (apk == null && Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but the original APK (MCVS_APK) is missing");
            if (server == null && Environment.GetEnvironmentVariable("MCVS_REQUIRE_EMBEDDED") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_EMBEDDED=1 but the embedded server DLL is missing");
            if (apk != null && server != null) return true;
            output.WriteLine("original APK (MCVS_APK) or embedded server DLL not available: skipped");
            return false;
        }

        private PatchReport Run(string apk, string server, SortedSet<string> patches, out string outApk)
        {
            string dir = TestEnvironment.NewTempDir("debloat-run");
            outApk = Path.Combine(dir, "offline.apk");
            var options = new PatcherOptions
            {
                InputApk = apk,
                ServerDll = server,
                OutputApk = outApk,
                Keystore = Path.Combine(dir, "key.p12"),   // never the player's default key
                BuildToolsDir = TestEnvironment.BuildToolsDir,
            };
            if (patches != null) options.Patches = patches;
            return ApkPatcher.Run(options, s => output.WriteLine(s));
        }

        [Fact]
        public void TheDefaultRunIsSignedAndLeavesOutTheDeadComponentsAndFiles()
        {
            if (!Inputs(out string apk, out string server)) return;
            PatchReport report = Run(apk, server, null, out string outApk);
            output.WriteLine(report.Signer + "\n" + report.Verification);

            ZipArchiveReader.Archive input = ZipArchiveReader.Read(apk);
            ZipArchiveReader.Archive result = ZipArchiveReader.Read(outApk);
            if (report.Signer.StartsWith("apksigner", StringComparison.Ordinal))
            {
                Assert.Contains("apksigner verify: OK", report.Verification);
                string apksigner = McvsOffline.Patcher.Tools.BuildTools.Locate(TestEnvironment.BuildToolsDir).ApkSigner;
                string verify = McvsOffline.Patcher.Signing.ExternalApkSigner.Verify(apksigner, outApk);
                Assert.Contains("Verified using v2 scheme (APK Signature Scheme v2): true", verify);
            }
            else
            {
                output.WriteLine("apksigner not found (set MCVS_BUILD_TOOLS): signature not verified");
            }

            // The Crashlytics/Fabric files are gone (reported one by one), every other lib/ entry but the AOT images stays.
            Assert.DoesNotContain(result.Entries, e => ApkPatcher.IsDebloatEntry(e.Name));
            Assert.DoesNotContain(result.Entries, e => e.Name.StartsWith("fabric/", StringComparison.Ordinal) || e.Name.EndsWith("/libcrashlytics.so", StringComparison.Ordinal));
            foreach (string name in ApkPatcher.DebloatDroppedEntries.Keys)
                Assert.Contains(report.Removed, r => r.StartsWith(name + " (", StringComparison.Ordinal) && r.EndsWith("; debloat)", StringComparison.Ordinal));
            var libsIn = input.Entries.Select(e => e.Name).Where(n => n.StartsWith("lib/", StringComparison.Ordinal) && !n.EndsWith("/libcrashlytics.so", StringComparison.Ordinal) &&
                                                                     !report.Removed.Any(r => r.StartsWith(n + " (AOT image", StringComparison.Ordinal)));
            Assert.Equal(libsIn, result.Entries.Select(e => e.Name).Where(n => n.StartsWith("lib/", StringComparison.Ordinal)));
            Assert.Contains(report.Notes, n => n.StartsWith("debloat: dropped 9 Crashlytics/Fabric files", StringComparison.Ordinal));

            // The manifest is the permission rename plus the debloat, one note per removed element.
            byte[] original = input.Find(ManifestPatch.EntryName).GetData();
            Assert.Equal(ManifestDebloat.Apply(ManifestPatch.Apply(original, null), null), result.Find(ManifestPatch.EntryName).GetData());
            Assert.Equal(ManifestDebloat.Targets.Count, report.Notes.Count(n => n.StartsWith("manifest: removed <", StringComparison.Ordinal)));

            if (TestEnvironment.Aapt2 == null)
            {
                output.WriteLine("aapt2 not found (set MCVS_BUILD_TOOLS): badging skipped");
                return;
            }
            string badging = Aapt2Dump.Badging(outApk);
            Assert.Contains("package: name='" + KnownBuild.PackageName + "' versionCode='1173200' versionName='1.17.32'", badging);
            Assert.Contains("launchable-activity: name='com.gameloft.android.XamarinMainActivity'", badging);
            Assert.Equal(ManifestDebloatTests.KeptPermissions, Aapt2Dump.UsesPermissions(badging));
            Assert.Contains("native-code: 'arm64-v8a' 'armeabi-v7a'", badging);
        }

        [Fact]
        public void WithoutDebloatTheManifestIsTheRenameAloneAndTheFilesStay()
        {
            if (!Inputs(out string apk, out string server)) return;
            var patches = new SortedSet<string>(PatcherOptions.DefaultPatches.Where(p => p != ManifestDebloat.PatchId), StringComparer.Ordinal);
            PatchReport report = Run(apk, server, patches, out string outApk);

            ZipArchiveReader.Archive input = ZipArchiveReader.Read(apk);
            ZipArchiveReader.Archive result = ZipArchiveReader.Read(outApk);
            byte[] original = input.Find(ManifestPatch.EntryName).GetData();
            Assert.Equal(ManifestPatch.Apply(original, null), result.Find(ManifestPatch.EntryName).GetData());
            foreach (string name in ApkPatcher.DebloatDroppedEntries.Keys)
                Assert.Equal(input.Find(name).RawData, result.Find(name).RawData);
            Assert.DoesNotContain(report.Notes, n => n.StartsWith("manifest: removed", StringComparison.Ordinal) || n.StartsWith("debloat:", StringComparison.Ordinal));
            Assert.DoesNotContain(report.Removed, r => r.EndsWith("; debloat)", StringComparison.Ordinal));
        }
    }
}
