using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher;
using McvsOffline.Patcher.Dex;
using McvsOffline.Patcher.Manifest;
using McvsOffline.Patcher.Zip;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// Invariant I1' (docs/research/target-sdk.md §6, manifest and layout part): with "targetsdk" at --target-sdk 35 or 36
    /// the patched APK targets that and keeps minSdkVersion 21; every activity/receiver/service with an intent filter has
    /// android:exported; POST_NOTIFICATIONS is declared; &lt;queries&gt; makes a browser visible; native heap pointer tagging
    /// is off; both browser layouts fit the system windows; resources.arsc stays STORED and 4-byte aligned. --target-sdk 29
    /// (or leaving the patch out) gives the shipped manifest, layouts and classes.dex, exactly as before the patch existed.
    /// 35 is the default, and a target of 31+ is only written with the classes.dex edits (WP-T3, Dex.DexTargetSdkEdits) of
    /// the same run (the manifest alone would ship a game that throws on Android 12+). Checked on
    /// synthetic manifests, on the player's real manifest and layouts, and on full runs of the patcher over the real APK
    /// (aapt2 badging/xmltree and apksigner when build-tools are available).
    /// </summary>
    public class ManifestTargetSdkTests
    {
        public const uint MinSdk = 21;
        private const string Ns = AxmlWriter.AndroidNs;

        /// <summary>The components 1.17.32 declares with an intent filter and no exported, after debloat (target-sdk.md §3).</summary>
        public static readonly string[] ExportedAfterDebloat =
        {
            "com.gameloft.android.XamarinMainActivity", "com.gameloft.igp.IGPFreemiumActivity", "com.gameloft.pushnotification.LocalPushReceiver",
        };

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

        /// <summary>
        /// Asserts I1' on a manifest: the SDK levels, exported on every intent-filter component (the
        /// <paramref name="exported"/> ones newly true), POST_NOTIFICATIONS once, the browser &lt;queries&gt;, heap tagging
        /// off, and for 36 the back-key and app-category attributes.
        /// </summary>
        internal static void AssertInvariant(byte[] manifest, int target, IEnumerable<string> exported)
        {
            Assert.Equal((MinSdk, (uint)target), UsesSdk(manifest));
            IReadOnlyList<BinaryXml.Element> elements = BinaryXml.Parse(manifest).Elements();
            Assert.Empty(ManifestTargetSdk.Unexported(elements));
            foreach (string name in exported)
            {
                BinaryXml.Element component = Assert.Single(elements, e => e.Depth == 2 && e.AndroidName == name);
                Assert.Equal(0xFFFFFFFFu, Assert.Single(component.Attributes, a => a.ResourceId == ManifestTargetSdk.ExportedId).Data);
            }
            Assert.Single(elements, e => e.Path == "manifest/uses-permission" && e.AndroidName == ManifestTargetSdk.PostNotifications);

            BinaryXml.Element queries = Assert.Single(elements, e => e.Path == "manifest/queries");
            List<BinaryXml.Element> intents = elements.Where(e => ReferenceEquals(e.Parent, queries)).ToList();
            Assert.Equal(new[] { "intent", "intent" }, intents.Select(e => e.Name));
            var schemes = new List<string>();
            foreach (BinaryXml.Element intent in intents)
            {
                List<BinaryXml.Element> parts = elements.Where(e => ReferenceEquals(e.Parent, intent)).ToList();
                Assert.Equal(new[] { "action", "category", "data" }, parts.Select(e => e.Name));
                Assert.Equal("android.intent.action.VIEW", parts[0].AndroidName);
                Assert.Equal("android.intent.category.BROWSABLE", parts[1].AndroidName);
                BinaryXml.Attribute scheme = Assert.Single(parts[2].Attributes);
                Assert.Equal(ManifestTargetSdk.SchemeId, scheme.ResourceId);
                schemes.Add(scheme.Value);
            }
            Assert.Equal(new[] { "https", "http" }, schemes);

            BinaryXml.Element application = Assert.Single(elements, e => e.Path == "manifest/application");
            Assert.Equal(0u, Assert.Single(application.Attributes, a => a.ResourceId == ManifestTargetSdk.AllowNativeHeapPointerTaggingId).Data);
            Assert.Equal(target >= 36, application.Attributes.Any(a => a.ResourceId == ManifestTargetSdk.EnableOnBackInvokedCallbackId && a.DataType == 0x12 && a.Data == 0));
            Assert.Equal(target >= 36, application.Attributes.Any(a => a.ResourceId == ManifestTargetSdk.AppCategoryId && a.DataType == 0x10 && a.Data == 0));
            foreach (BinaryXml.Element e in elements)
            {
                var ids = e.Attributes.Select(a => a.ResourceId).Where(id => id != 0).ToList();
                Assert.Equal(ids.OrderBy(id => id), ids);   // the platform's attribute lookup relies on it
            }
        }

        // ------------------------------------------------------------------ synthetic manifests

        [Theory]
        [InlineData(35, false)]
        [InlineData(35, true)]
        [InlineData(36, false)]
        public void TheEditsGiveTheInvariantOnADebloatedGameLikeManifest(int target, bool utf8)
        {
            byte[] manifest = ManifestDebloat.Apply(ManifestPatch.Apply(AxmlWriter.Write(ManifestDebloatTests.DebloatLikeManifest(), utf8), null), null);
            Assert.Equal((MinSdk, 29u), UsesSdk(manifest));
            var notes = new List<string>();
            byte[] result = ManifestTargetSdk.Edit(manifest, target, notes);

            AssertInvariant(result, target, new[] { "com.gameloft.android.XamarinMainActivity", "com.gameloft.pushnotification.LocalPushReceiver" });
            foreach (string n in notes) output.WriteLine(n);
            Assert.Equal(1 + 2 + 1 + 1 + 1 + (target >= 36 ? 2 : 0), notes.Count);
            Assert.StartsWith("manifest: <uses-sdk android:targetSdkVersion> 29 -> " + target + " (", notes[0]);
            Assert.All(notes, n => Assert.StartsWith("manifest: ", n));

            // Nothing else changed: the elements of the input, in order, with their attributes (plus exported and the
            // application attributes), and the two new top-level elements.
            List<string> before = BinaryXml.Parse(manifest).Elements().Select(e => e.Path + " " + e.AndroidName).ToList();
            List<string> after = BinaryXml.Parse(result).Elements().Where(e => !e.Path.StartsWith("manifest/queries", StringComparison.Ordinal) &&
                                                                              e.AndroidName != ManifestTargetSdk.PostNotifications)
                                          .Select(e => e.Path + " " + e.AndroidName).ToList();
            Assert.Equal(before, after);

            if (TestEnvironment.Aapt2 == null) return;
            string dump = Aapt2Dump.XmlTree(result);
            output.WriteLine(dump);
            Assert.Contains("android:targetSdkVersion(0x01010270)=0x" + target.ToString("x8"), dump);   // the writer's TYPE_INT_HEX stays
            Assert.Contains("android:allowNativeHeapPointerTagging(0x01010612)=false", dump);
            Assert.Contains("android:scheme(0x01010027)=\"http\"", dump);
        }

        [Fact]
        public void TargetTwentyNineIsTheInputAndOtherTargetsAreRefused()
        {
            byte[] manifest = ManifestDebloat.Apply(AxmlWriter.Write(ManifestDebloatTests.DebloatLikeManifest()), null);
            var notes = new List<string>();
            Assert.Same(manifest, ManifestTargetSdk.Edit(manifest, 29, notes));
            Assert.Empty(notes);
            Assert.Throws<ArgumentException>(() => ManifestTargetSdk.Edit(manifest, 34, notes));
            Assert.Throws<ArgumentException>(() => PatcherOptions.ParseTargetSdk("33"));
            Assert.Equal(new[] { 29, 35, 36 }, new[] { "29", "35", "36" }.Select(PatcherOptions.ParseTargetSdk));
        }

        [Fact]
        public void AManifestOfAnotherShapeIsRefused()
        {
            byte[] Debloated(Action<AxmlWriter.Element, AxmlWriter.Element> tweak) => AxmlWriter.Write(ManifestDebloatTests.DebloatLikeManifest(tweak));
            void Refused(byte[] manifest, string why)
            {
                var ex = Assert.Throws<InvalidDataException>(() => ManifestTargetSdk.Edit(manifest, 35, null));
                output.WriteLine(ex.Message);
                Assert.Contains(why, ex.Message);
            }
            Refused(Debloated((m, a) => m.Children[0].Attributes[1] = (Ns, "targetSdkVersion", 30)), "targetSdkVersion> is not the integer 29");
            Refused(Debloated((m, a) => m.Children.RemoveAt(0)), "<uses-sdk> occurs 0 times");
            Refused(Debloated((m, a) => m.Add(new AxmlWriter.Element("uses-permission", (Ns, "name", ManifestTargetSdk.PostNotifications)))), "POST_NOTIFICATIONS is already declared");
            Refused(Debloated((m, a) => m.Add(new AxmlWriter.Element("queries"))), "<queries> already exists");
            Refused(Debloated((m, a) => a.Attributes.Add((Ns, "allowNativeHeapPointerTagging", true))), "already sets attribute 0x01010612");

            // The patch itself first wants the shipped manifest, byte for byte.
            byte[] synthetic = AxmlWriter.Write(ManifestDebloatTests.DebloatLikeManifest());
            var fingerprint = Assert.Throws<InvalidDataException>(() => ManifestTargetSdk.Apply(synthetic, ManifestDebloat.Apply(synthetic, null), 35, null));
            Assert.Contains("is not the verified 1.17.32 manifest (SHA-256 ", fingerprint.Message);
        }

        [Fact]
        public void TheOptionsRefuseATargetWithoutItsPatchesBeforeAnyWork()
        {
            PatcherOptions Options(string patches, int? target) => new PatcherOptions { Patches = PatcherOptions.ParsePatchList(patches), TargetSdk = target };
            // 35 by default (ManifestTargetSdk.DefaultTarget), with the classes.dex edits.
            Assert.Equal(35, ManifestTargetSdk.DefaultTarget);
            Assert.Equal(35, Options("default", null).EffectiveTargetSdk);
            Assert.Equal(35, Options("default", 35).EffectiveTargetSdk);
            Assert.Equal(36, Options("default", 36).EffectiveTargetSdk);
            Assert.Equal(29, Options("default", 29).EffectiveTargetSdk);
            Assert.Equal(29, Options("none", null).EffectiveTargetSdk);
            Assert.Equal(29, Options("debloat", null).EffectiveTargetSdk);
            ApkPatcher.CheckTargetSdk(Options("default", null));
            ApkPatcher.CheckTargetSdk(Options("none", null));
            ApkPatcher.CheckTargetSdk(Options("none", 29));
            ApkPatcher.CheckTargetSdk(Options("targetsdk", 29));   // 29 without debloat: nothing changes, nothing to refuse
            Assert.Contains("needs the 'targetsdk' patch", Assert.Throws<ArgumentException>(() => ApkPatcher.CheckTargetSdk(Options("debloat", 35))).Message);
            Assert.Contains("needs the 'debloat' patch", Assert.Throws<ArgumentException>(() => ApkPatcher.CheckTargetSdk(Options("targetsdk", 35))).Message);
            var withoutDebloat = new SortedSet<string>(PatcherOptions.DefaultPatches.Where(p => p != ManifestDebloat.PatchId), StringComparer.Ordinal);
            // Leaving debloat out of the default set needs --target-sdk 29 (the default 35 needs it).
            Assert.Contains("needs the 'debloat' patch", Assert.Throws<ArgumentException>(() => ApkPatcher.CheckTargetSdk(new PatcherOptions { Patches = withoutDebloat })).Message);
            ApkPatcher.CheckTargetSdk(new PatcherOptions { Patches = withoutDebloat, TargetSdk = 29 });
            Assert.Throws<ArgumentException>(() => ApkPatcher.CheckTargetSdk(new PatcherOptions { Patches = withoutDebloat, TargetSdk = 36 }));

            // The command line: --target-sdk is parsed and checked by ApkPatcher.Run.
            string[] common = { "--apk", "a", "--server", "s", "--out", "o" };
            Assert.Null(McvsOffline.Patcher.Program.Parse(common).TargetSdk);
            Assert.Equal(36, McvsOffline.Patcher.Program.Parse(common.Concat(new[] { "--target-sdk", "36" }).ToArray()).TargetSdk);
            Assert.Throws<ArgumentException>(() => McvsOffline.Patcher.Program.Parse(common.Concat(new[] { "--target-sdk", "30" }).ToArray()));
            Assert.Throws<ArgumentException>(() => ApkPatcher.Run(new PatcherOptions { InputApk = "a", ServerDll = "s", OutputApk = "o", Patches = PatcherOptions.ParsePatchList("targetsdk"),
                                                                                       TargetSdk = 35 }, null));
        }

        [Fact]
        public void ATarget31OrMoreNeedsTheDexEditsOfTheSameRun()
        {
            // The manifest alone at 31+ would ship MainActivity's restart PendingIntent (flags 0x8000, no mutability flag:
            // IllegalArgumentException on Android 12+) and GMPHelper's billing receiver (no exported flag: SecurityException on
            // Android 14+ at 34+). Only a run that also replaced classes.dex may write such a target.
            PatcherOptions Options(int? target) => new PatcherOptions { Patches = PatcherOptions.ParsePatchList("default"), TargetSdk = target };
            string[] layouts = BrowserLayoutInsets.Layouts.Keys.ToArray();
            ApkPatcher.CheckDexEdits(Options(29), new string[0]);
            ApkPatcher.CheckDexEdits(new PatcherOptions { Patches = PatcherOptions.ParsePatchList("debloat"), TargetSdk = 35 }, new string[0]);   // targetsdk off: 29
            foreach (int? target in new int?[] { null, 35, 36 })
            {
                var refused = Assert.Throws<InvalidOperationException>(() => ApkPatcher.CheckDexEdits(Options(target), layouts));
                Assert.Equal("target SDK " + (target ?? 35) + " needs the 'targetsdk' edits of classes.dex (PendingIntent flags, receiver export flags; " +
                             "docs/research/target-sdk.md WP-T3), which were not made: nothing was written, use --target-sdk 29", refused.Message);
                Assert.Throws<InvalidOperationException>(() => ApkPatcher.CheckDexEdits(Options(target), layouts.Concat(new[] { "classes2.dex" })));   // P1 is in classes.dex
                ApkPatcher.CheckDexEdits(Options(target), layouts.Concat(new[] { ManifestTargetSdk.DexEntry }));
            }
        }

        // ------------------------------------------------------------------ the player's files

        private static bool RealApk(ITestOutputHelper output, out ZipArchiveReader.Archive apk)
        {
            apk = null;
            if (TestEnvironment.GameApk == null)
            {
                if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                    throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but the original APK (MCVS_APK) is missing");
                output.WriteLine("original APK (MCVS_APK) not available: skipped");
                return false;
            }
            apk = ZipArchiveReader.Read(TestEnvironment.GameApk);
            return true;
        }

        [Theory]
        [InlineData(35)]
        [InlineData(36)]
        public void TheRealManifestAfterRenameAndDebloat(int target)
        {
            if (!RealApk(output, out ZipArchiveReader.Archive apk)) return;
            byte[] shipped = apk.Find(ManifestPatch.EntryName).GetData();
            Assert.Equal(ManifestTargetSdk.ShippedManifestSha256, ManifestTargetSdk.Sha256(shipped));
            Assert.Equal((MinSdk, 29u), UsesSdk(shipped));   // AndroidManifest.xml:13 of 1.17.32
            byte[] debloated = ManifestDebloat.Apply(ManifestPatch.Apply(shipped, null), null);
            Assert.Equal(ExportedAfterDebloat, ManifestTargetSdk.Unexported(BinaryXml.Parse(debloated).Elements()).Select(e => e.AndroidName));

            var notes = new List<string>();
            byte[] result = ManifestTargetSdk.Apply(shipped, debloated, target, notes);
            foreach (string n in notes) output.WriteLine(n);
            AssertInvariant(result, target, ExportedAfterDebloat);
            Assert.Equal(1 + ExportedAfterDebloat.Length + 1 + 1 + 1 + (target >= 36 ? 2 : 0), notes.Count);
            foreach (string name in ExportedAfterDebloat) Assert.Contains(notes, n => n.StartsWith("manifest: android:exported=\"true\" on <", StringComparison.Ordinal) && n.Contains(name));

            // compileSdkVersion and platformBuildVersion* stay as shipped (informational only).
            List<string> RootAttributes(byte[] m) => BinaryXml.Parse(m).Elements()[0].Attributes.Select(a => a.ToString()).ToList();
            Assert.Equal(RootAttributes(shipped), RootAttributes(result));

            if (TestEnvironment.Aapt2 == null) return;
            string badging = Aapt2Dump.Badging(Aapt2Dump.MinimalApk(result, apk.Find("resources.arsc").GetData()));
            Assert.Contains("targetSdkVersion:'" + target + "'", badging);
            Assert.Contains("sdkVersion:'" + MinSdk + "'", badging);
            Assert.Contains("compileSdkVersion='29'", badging);
            Assert.Contains("launchable-activity: name='com.gameloft.android.XamarinMainActivity'", badging);
            Assert.Contains("uses-permission: name='" + ManifestTargetSdk.PostNotifications + "'", badging);
        }

        [Fact]
        public void WithoutDebloatTheRuleFindsTheSixComponentsOfTheShippedManifest()
        {
            // target-sdk.md §3 and its critic note: six intent-filter components lack exported in the shipped manifest; the
            // rule covers them all (the patcher still refuses 31+ without debloat, see the options test).
            if (!RealApk(output, out ZipArchiveReader.Archive apk)) return;
            byte[] renamed = ManifestPatch.Apply(apk.Find(ManifestPatch.EntryName).GetData(), null);
            Assert.Equal(new[]
            {
                "com.gameloft.android.XamarinMainActivity", "com.gameloft.android.AppLinksActivity", "com.gameloft.igp.IGPFreemiumActivity",
                "com.gameloft.pushnotification.GCMBroadcastReceiver", "com.gameloft.pushnotification.LocalPushReceiver", "com.gameloft.android.BootCompletedReceiver",
            }, ManifestTargetSdk.Unexported(BinaryXml.Parse(renamed).Elements()).Select(e => e.AndroidName));
            AssertInvariant(ManifestTargetSdk.Edit(renamed, 35, null), 35, new string[0]);
        }

        [Fact]
        public void TheRealBrowserLayoutsFitTheSystemWindows()
        {
            if (!RealApk(output, out ZipArchiveReader.Archive apk)) return;
            Assert.Equal(2, apk.Entries.Count(e => e.Name.EndsWith("/activity_in_game_browser.xml", StringComparison.Ordinal)));
            foreach (var layout in BrowserLayoutInsets.Layouts)
            {
                byte[] shipped = apk.Find(layout.Key).GetData();
                Assert.Equal(layout.Value, ManifestTargetSdk.Sha256(shipped));
                var notes = new List<string>();
                byte[] result = BrowserLayoutInsets.Apply(layout.Key, shipped, notes);
                Assert.StartsWith(layout.Key + ": android:fitsSystemWindows=\"true\" on the root RelativeLayout", Assert.Single(notes));

                // The root: id, fitsSystemWindows, layout_width, layout_height; the name went into the mapped region.
                BinaryXml back = BinaryXml.Parse(result);
                Assert.Equal(new[] { 0x010100d0u, BrowserLayoutInsets.FitsSystemWindowsId, 0x010100f4u, 0x010100f5u }, back.Elements()[0].Attributes.Select(a => a.ResourceId));
                Assert.Equal(BinaryXml.Parse(shipped).ResourceMapCount + 1, back.ResourceMapCount);
                // An attribute (20 bytes), a map entry and a string offset (4 + 4), the UTF-8 string (20, the pool padded to 4).
                Assert.InRange(result.Length - shipped.Length, 20 + 4 + 4 + 20 - 3, 20 + 4 + 4 + 20 + 3);
                Assert.Throws<InvalidDataException>(() => BrowserLayoutInsets.Apply(layout.Key, result, null));   // fingerprint
                Assert.Throws<InvalidDataException>(() => BrowserLayoutInsets.Edit(layout.Key, result, null));    // already set

                if (TestEnvironment.Aapt2 == null) continue;
                string before = Aapt2Dump.Run("dump", "xmltree", "--file", layout.Key, TestEnvironment.GameApk);
                string after = Aapt2Dump.XmlTree(result, layout.Key);
                string idLine = "    A: http://schemas.android.com/apk/res/android:id(0x010100d0)=@0x7f0e0075\n";
                Assert.Contains(idLine, before);
                Assert.Equal(before.Replace(idLine, idLine + "    A: http://schemas.android.com/apk/res/android:fitsSystemWindows(0x010100dd)=true\n"), after);
            }
        }

        // ------------------------------------------------------------------ full patcher runs

        private bool Inputs(out string apk, out string server)
        {
            apk = TestEnvironment.GameApk;
            server = RealAdminPagePatchTests.EmbeddedServer(output);
            if (apk == null && Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but the original APK (MCVS_APK) is missing");
            if (server == null && Environment.GetEnvironmentVariable("MCVS_REQUIRE_EMBEDDED") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_EMBEDDED=1 but the embedded server DLL is missing");
            if (apk != null && server != null) return true;
            output.WriteLine("original APK (MCVS_APK) or embedded server DLL not available: skipped");
            return false;
        }

        private PatchReport Run(string apk, string server, int? target, out string outApk, out List<string> log)
        {
            string dir = TestEnvironment.NewTempDir("target-sdk");
            outApk = Path.Combine(dir, "offline.apk");
            var options = new PatcherOptions
            {
                InputApk = apk,
                ServerDll = server,
                OutputApk = outApk,
                TargetSdk = target,
                Keystore = Path.Combine(dir, "key.p12"),   // never the player's default key
                BuildToolsDir = TestEnvironment.BuildToolsDir,
            };
            Assert.Equal(new SortedSet<string>(PatcherOptions.DefaultPatches, StringComparer.Ordinal), options.Patches);
            var lines = new List<string>();
            PatchReport report = ApkPatcher.Run(options, lines.Add);
            log = lines;
            return report;
        }

        /// <summary>resources.arsc STORED and 4-byte aligned (from target 30 the install fails otherwise), byte for byte the shipped one.</summary>
        private static void AssertResourcesStoredAndAligned(string apk, string original)
        {
            byte[] zip = File.ReadAllBytes(apk);
            ZipEntry arsc = ZipArchiveReader.Read(zip).Find("resources.arsc");
            Assert.Equal(ZipEntry.MethodStored, arsc.Method);
            Assert.Equal(0, ZipTests.DataOffset(zip, arsc) % 4);
            Assert.Equal(ZipArchiveReader.Read(original).Find("resources.arsc").RawData, arsc.RawData);
        }

        [Fact]
        public void TheDefaultTarget35WritesTheManifestLayoutsAndClassesDex()
        {
            // I1' on a full run with the defaults (no --target-sdk): manifest, layouts and the classes.dex edits.
            if (!Inputs(out string apk, out string server)) return;
            PatchReport report = Run(apk, server, null, out string outApk, out List<string> log);
            foreach (string id in PatcherOptions.DefaultPatches)
                Assert.Contains(log, l => l.StartsWith("patch " + id + ": ", StringComparison.Ordinal));
            Assert.Contains("patch targetsdk: targetSdkVersion 29 -> 35, with what that target needs in the manifest, the settings page's layouts and classes.dex", log);
            Assert.Contains(ManifestPatch.EntryName, report.Modified);
            foreach (string layout in BrowserLayoutInsets.Layouts.Keys) Assert.Contains(layout, report.Modified);
            Assert.Contains(ManifestTargetSdk.DexEntry, report.Modified);
            Assert.Equal(RealGameTests.DexNotes, report.Notes.Count(n => n.StartsWith("classes.dex: ", StringComparison.Ordinal)));
            Assert.Equal(7 + 2, report.Notes.Count(n => n.StartsWith("manifest: <uses-sdk", StringComparison.Ordinal) || n.StartsWith("manifest: android:", StringComparison.Ordinal) ||
                                                        n.StartsWith("manifest: + <", StringComparison.Ordinal) || n.Contains("activity_in_game_browser.xml: ")));

            ZipArchiveReader.Archive input = ZipArchiveReader.Read(apk), result = ZipArchiveReader.Read(outApk);
            byte[] shipped = input.Find(ManifestPatch.EntryName).GetData();
            byte[] manifest = result.Find(ManifestPatch.EntryName).GetData();
            Assert.Equal(ManifestTargetSdk.Edit(ManifestDebloat.Apply(ManifestPatch.Apply(shipped, null), null), 35, null), manifest);
            AssertInvariant(manifest, 35, ExportedAfterDebloat);
            foreach (var layout in BrowserLayoutInsets.Layouts)
            {
                Assert.Equal(input.Find(layout.Key).Method, result.Find(layout.Key).Method);
                Assert.Equal(BrowserLayoutInsets.Edit(layout.Key, input.Find(layout.Key).GetData(), null), result.Find(layout.Key).GetData());
            }
            // classes.dex: exactly DexTargetSdkEdits' result, same compression method; every other dex as shipped.
            IReadOnlyDictionary<string, byte[]> dex = DexTargetSdkEdits.Apply(input.Entries, 35, null);
            Assert.Equal(new[] { ManifestTargetSdk.DexEntry }, dex.Keys);
            Assert.Equal(input.Find(ManifestTargetSdk.DexEntry).Method, result.Find(ManifestTargetSdk.DexEntry).Method);
            Assert.Equal(dex[ManifestTargetSdk.DexEntry], result.Find(ManifestTargetSdk.DexEntry).GetData());
            foreach (ZipEntry other in input.Entries.Where(e => DexTargetSdkEdits.IsDexEntry(e.Name) && e.Name != ManifestTargetSdk.DexEntry))
                Assert.Equal(other.RawData, result.Find(other.Name).RawData);
            AssertResourcesStoredAndAligned(outApk, apk);
            Assert.Contains("zipalign -c -p 4: OK", report.Verification);

            if (TestEnvironment.Aapt2 == null)
            {
                output.WriteLine("aapt2 not found (set MCVS_BUILD_TOOLS): badging skipped");
                return;
            }
            Assert.Contains("apksigner verify: OK", report.Verification);
            List<string> lines = Aapt2Dump.Badging(outApk).Split('\n').ToList();
            Assert.Contains("sdkVersion:'" + MinSdk + "'", lines);
            Assert.Contains("targetSdkVersion:'35'", lines);
            Assert.Contains("launchable-activity: name='com.gameloft.android.XamarinMainActivity'  label='MCVS' icon=''", lines);
            Assert.Equal(ManifestDebloatTests.KeptPermissions.Concat(new[] { ManifestTargetSdk.PostNotifications }), Aapt2Dump.UsesPermissions(string.Join("\n", lines)));
            string tree = Aapt2Dump.XmlTree(outApk);
            Assert.Equal(3 + 1, tree.Split('\n').Count(l => l.Contains("android:exported(0x01010010)=true")));   // the three, and KeyProvider's own
            string land = Aapt2Dump.Run("dump", "xmltree", "--file", "res/layout-land/activity_in_game_browser.xml", outApk);
            Assert.Contains("android:fitsSystemWindows(0x010100dd)=true", land);
        }

        [Fact]
        public void TargetSdk29IsTheOutputWithoutThePatch()
        {
            if (!Inputs(out string apk, out string server)) return;
            PatchReport report = Run(apk, server, 29, out string outApk, out List<string> log);
            Assert.Contains("patch targetsdk: --target-sdk 29: the shipped targetSdkVersion, manifest and layouts stay as they are", log);
            Assert.DoesNotContain(report.Notes, n => n.Contains("targetSdkVersion") || n.Contains("activity_in_game_browser") || n.StartsWith("classes.dex: ", StringComparison.Ordinal));

            ZipArchiveReader.Archive input = ZipArchiveReader.Read(apk), result = ZipArchiveReader.Read(outApk);
            byte[] shipped = input.Find(ManifestPatch.EntryName).GetData();
            Assert.Equal(ManifestDebloat.Apply(ManifestPatch.Apply(shipped, null), null), result.Find(ManifestPatch.EntryName).GetData());
            Assert.Equal((MinSdk, 29u), UsesSdk(result.Find(ManifestPatch.EntryName).GetData()));
            foreach (string layout in BrowserLayoutInsets.Layouts.Keys)
            {
                Assert.DoesNotContain(layout, report.Modified);
                Assert.Equal(input.Find(layout).RawData, result.Find(layout).RawData);
            }
            Assert.DoesNotContain(ManifestTargetSdk.DexEntry, report.Modified);
            Assert.Equal(input.Find(ManifestTargetSdk.DexEntry).RawData, result.Find(ManifestTargetSdk.DexEntry).RawData);
            AssertResourcesStoredAndAligned(outApk, apk);
            if (TestEnvironment.Aapt2 == null) return;
            Assert.Contains("targetSdkVersion:'29'", Aapt2Dump.Badging(outApk).Split('\n'));
        }
    }
}
