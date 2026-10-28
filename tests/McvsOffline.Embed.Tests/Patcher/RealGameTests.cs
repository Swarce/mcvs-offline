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
    /// The patch locators against the player's real 1.17.32 files. Skipped when they are not available (they are never
    /// in the repository): set MCVS_APK to the original APK; the embedded server DLL comes from scripts/build.sh.
    /// </summary>
    public class RealGameTests
    {
        private readonly ITestOutputHelper output;

        public RealGameTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>Report notes per IL patch id on the real 1.17.32 assemblies. A new IL patch adds its id here.</summary>
        internal static readonly IReadOnlyDictionary<string, int> IlPatchNotes = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["hook"] = 1, ["headless"] = 5, ["headless-engine"] = 3, ["physics"] = 2, ["nativeonline"] = 1, ["waits"] = 2, ["geo"] = 1,
            ["bottile"] = 1, ["finder"] = 1, ["lan"] = 3, ["store"] = 2, ["signatures"] = 2, ["debloat"] = 7, ["adminpage"] = 2,
            ["adminlink"] = 1, ["forum"] = 1, ["graphics"] = 1,
        };

        /// <summary>
        /// The notes of a run on the real APK: those of its IL patches, the manifest permission rename, the res/raw/args.txt
        /// rewrite (--game-log all), the ABI selection, for "debloat" one per removed manifest element plus one for the
        /// dropped Crashlytics/Fabric files, and for "targetsdk" at <paramref name="target"/> 35 the target, the three exported
        /// components, heap tagging, POST_NOTIFICATIONS, &lt;queries&gt;, the two browser layouts, the four Google Analytics
        /// components it removes and the 14 classes.dex sites (<see cref="DexNotes"/>; none of them at 29).
        /// </summary>
        internal static int ExpectedNotes(IEnumerable<string> optionalPatches, bool gameLogAll = true, int target = ManifestTargetSdk.DefaultTarget)
        {
            var ids = new HashSet<string>(PatcherOptions.MandatoryPatches.Concat(optionalPatches), StringComparer.Ordinal);
            int notes = ids.Sum(id => IlPatchNotes.TryGetValue(id, out int n) ? n : 0);
            notes += 1 + (gameLogAll ? 1 : 0) + 1;
            if (ids.Contains(ManifestDebloat.PatchId)) notes += ManifestDebloat.Targets.Count + 1;
            if (ids.Contains(ManifestTargetSdk.PatchId) && target != ManifestTargetSdk.ShippedTarget) notes += 1 + 3 + 1 + 1 + 1 + BrowserLayoutInsets.Layouts.Count;
            if (ids.Contains(ManifestTargetSdk.PatchId) && target >= DexTargetSdkEdits.MinTargetSdk) notes += ManifestTargetSdk.GoogleAnalyticsComponents.Count + DexNotes;
            return notes;
        }

        /// <summary>One note per edited classes.dex site (P1-P8, P10-P14; P8 is two): 14.</summary>
        internal static int DexNotes => DexTargetSdkEdits.Edits.Sum(e => e.Sites.Count);

        [Fact]
        public void EveryPatchSiteIsFoundInTheRealApk()
        {
            string apk = TestEnvironment.GameApk;
            string server = RealAdminPagePatchTests.EmbeddedServer(output);
            if (apk == null || server == null)
            {
                output.WriteLine("original APK (MCVS_APK) or embedded server DLL not available: skipped");
                return;
            }
            var options = new PatcherOptions
            {
                InputApk = apk,
                ServerDll = server,
                Patches = PatcherOptions.ParsePatchList("all"),
                Abis = new SortedSet<string>(new[] { PatcherOptions.Arm64 }, StringComparer.Ordinal),   // --abi arm64-v8a
                TargetSdk = 35,
                DryRun = true,
            };
            PatchReport report = ApkPatcher.Run(options, s => output.WriteLine(s));
            foreach (string note in report.Notes) output.WriteLine(note);

            Assert.Equal(new[] { "AndroidManifest.xml", "assemblies/Game.dll", "assemblies/Gameloft.Engine.dll", "assemblies/Gameloft.Util.dll", "assemblies/NicePlaySDK.dll",
                                 "classes.dex", "res/layout-land/activity_in_game_browser.xml", "res/layout-port/activity_in_game_browser.xml", "res/raw/args.txt" },
                         report.Modified.OrderBy(m => m, StringComparer.Ordinal));
            Assert.Equal(new[] { "assemblies/McvsOffline.Server.dll" }, report.Added);
            // 36 IL patch notes, the manifest permission rename, the res/raw/args.txt rewrite, the ABI selection, debloat's
            // 44 manifest removals and file drop, and targetsdk's 7 manifest, 2 layout, 4 Google Analytics removal and 14
            // classes.dex edits: 111 (see IlPatchNotes / ExpectedNotes).
            Assert.Equal(14, DexNotes);
            Assert.Equal(111, ExpectedNotes(options.Patches, target: 35));
            Assert.Equal(ExpectedNotes(options.Patches, target: 35), report.Notes.Count);
            Assert.Equal(ManifestDebloat.Targets.Count, report.Notes.Count(n => n.StartsWith("manifest: removed <", StringComparison.Ordinal)));
            Assert.Contains(report.Notes, n => n.StartsWith("debloat: dropped 8 Crashlytics/Fabric files", StringComparison.Ordinal));   // armeabi-v7a's went with its ABI
            Assert.Contains("native libraries: kept arm64-v8a; dropped armeabi-v7a (the offline build is 64-bit ARM only)", report.Notes);
            Assert.Contains(report.Notes, n => n.Contains("KillSignatureRewardItem::GetAvailableId") && n.Contains("\"Sound\""));
            Assert.Contains(report.Notes, n => n.Contains("<SelectItems>b__8") && n.Contains("McvsStoreSafety.Report"));
            Assert.Contains(report.Notes, n => n.Contains("PlayerProfileComponent::GetSignatureAssetsList") && n.Contains("HashSet<string>"));
            Assert.Contains(report.Notes, n => n.Contains("GameOfflineStore::GetKillSignItems") && n.Contains("run once per call"));
            Assert.Contains(report.Notes, n => n.StartsWith("Game.InputSettingsComponent: + [KeepInHeadlessMode(false)]"));
            Assert.Contains(report.Notes, n => n.Contains("OnStart IL_0441 -> McvsBindingToActionPreset") && n.Contains("OnStop IL_01a2 -> McvsBindingToDefaultPreset"));
            Assert.Contains(report.Notes, n => n.Contains("FlameZoneComponent::BroadcastEnd(): IL_0035"));
            Assert.Contains(report.Notes, n => n.Contains("BacktrackTimeDistortionExplosion::FadeOutEffect(): IL_000d"));
            Assert.Contains(report.Notes, n => n.Contains("GetSafeAction") && n.Contains("McvsHeadlessSafeAction"));
            Assert.Contains(report.Notes, n => n.Contains("OnlinePlatformComponent::Init_private") && n.Contains("AllowNativeOnlineInit"));
            Assert.Contains(report.Notes, n => n.Contains("PhysicsComponent::.ctor()") && n.Contains("McvsPhysics.UseMultithreading(this)"));
            Assert.Contains(report.Notes, n => n.Contains("PhysicsComponent::UpdateSimulationSteps") && n.Contains("McvsPhysics.EnterStep()"));
            Assert.Equal(2, report.Notes.Count(n => n.Contains("TaskExtension::WaitAndWork") && n.Contains("WaitAny(handles, 100)")));
            Assert.Contains(report.Notes, n => n.StartsWith("res/raw/args.txt: \"-LogCategoryFilter Fatal\" -> \"-McvsOfflineGameLogAll\""));
            Assert.Contains(report.Notes, n => n.StartsWith("manifest: vendor-wide signature permission glshare.permission.ACCESS_SHARED_DATA renamed"));
            Assert.Contains(report.Notes, n => n.Contains("<NativeStartMatchWithBots>b__354_0") && n.Contains("IL_0007"));
            Assert.Contains(report.Notes, n => n.Contains("http://127.0.0.1:51780/location/?ip="));
            Assert.Contains(report.Notes, n => n.Contains("PlayerProfileComponent::GetAccountMenuButtonList") && n.Contains("AdminHooks.AccountMenuButtons(list)"));
            Assert.Contains(report.Notes, n => n.Contains("PlayerProfileComponent::ProcessAccountMenuButtonAction") && n.Contains("OpenPage(\"account\", World)"));
            Assert.Contains(report.Notes, n => n.Contains("BrowserComponent::OpenUrl") && n.Contains("OpenIfPageMarker(url, this)"));
            Assert.Contains(report.Notes, n => n.Contains("UIComponent::GoToForum") && n.Contains("OpenPage(\"forum\", World)"));
            Assert.Equal(DexNotes, report.Notes.Count(n => n.StartsWith("classes.dex: ", StringComparison.Ordinal)));
            Assert.Equal(ManifestTargetSdk.GoogleAnalyticsComponents.Count, report.Notes.Count(n => n.StartsWith("manifest: - <", StringComparison.Ordinal) && n.Contains(".analytics.")));
            Assert.Equal(BrowserLayoutInsets.Layouts.Count, report.Notes.Count(n => n.Contains("activity_in_game_browser.xml: ")));
            // arm64-v8a loses the AOT images of exactly the four modified assemblies; v1 signature files go.
            var aot = report.Removed.Where(r => r.Contains("(AOT image")).Select(r => r.Split(' ')[0]).OrderBy(r => r).ToList();
            Assert.Equal(4, aot.Count);
            foreach (string dll in new[] { "Game", "Gameloft.Engine", "Gameloft.Util", "NicePlaySDK" })
                Assert.Contains("lib/arm64-v8a/libaot-" + dll + ".dll.so", aot);
            // --abi arm64-v8a: every lib/armeabi-v7a entry goes (67 in 1.17.32), nothing of arm64-v8a but those four.
            var input = ZipArchiveReader.Read(apk);
            var dropped = report.Removed.Where(r => r.Contains("(native library of an ABI not kept")).Select(r => r.Split(' ')[0]).ToList();
            Assert.Equal(input.Entries.Count(e => e.Name.StartsWith("lib/armeabi-v7a/", StringComparison.Ordinal)), dropped.Count);
            Assert.Equal(67, dropped.Count);
            Assert.All(dropped, d => Assert.StartsWith("lib/armeabi-v7a/", d));
            Assert.DoesNotContain(report.Removed, r => r.StartsWith("lib/arm64-v8a/", StringComparison.Ordinal) && !r.Contains("(AOT image") &&
                                                       r != "lib/arm64-v8a/libcrashlytics.so (Crashlytics NDK library, never loaded offline; debloat)");
            Assert.Contains("lib/arm64-v8a/libcrashlytics.so (Crashlytics NDK library, never loaded offline; debloat)", report.Removed);
            Assert.Equal(7, report.Removed.Count(r => r.EndsWith(".properties (Fabric kit version, read only by Fabric.with, which never runs; debloat)", StringComparison.Ordinal) ||
                                                    r.StartsWith("assets/crashlytics-build.properties (", StringComparison.Ordinal)));
            Assert.Contains(report.Warnings, w => w.StartsWith("AOT image of XaLaunchANMP.dll"));
            int entries = ZipArchiveReader.Read(apk).Entries.Count;
            Assert.Equal(entries - report.Removed.Count - report.Modified.Count, report.UnchangedEntries);
        }

        [Fact]
        public void DefaultPatchSetLeavesLocalAnubisManagerAlone()
        {
            string apk = TestEnvironment.GameApk;
            string server = RealAdminPagePatchTests.EmbeddedServer(output);
            if (apk == null || server == null)
            {
                output.WriteLine("original APK (MCVS_APK) or embedded server DLL not available: skipped");
                return;
            }
            var log = new List<string>();
            PatchReport report = ApkPatcher.Run(new PatcherOptions { InputApk = apk, ServerDll = server, DryRun = true }, log.Add);
            // 32 IL patch notes (hook, headless 5 + 3, physics 2, nativeonline, waits 2, geo, bottile, finder, store 2,
            // signatures 2, debloat 7, adminpage 2, adminlink, graphics), the manifest rename, res/raw/args.txt, the ABI selection,
            // debloat's 44 + 1, and targetsdk at its default 35: 7 manifest, 2 layout and 14 classes.dex edits: 103.
            Assert.Equal(103, ExpectedNotes(PatcherOptions.DefaultPatches));
            Assert.Equal(ExpectedNotes(PatcherOptions.DefaultPatches), report.Notes.Count);
            Assert.Contains("patch targetsdk: targetSdkVersion 29 -> 35, with what that target needs in the manifest, the settings page's layouts and classes.dex", log);
            Assert.Contains(ManifestTargetSdk.DexEntry, report.Modified);
            Assert.DoesNotContain(report.Notes, n => n.Contains("LocalAnubisManager"));

            // The settings-page triggers are verified and listed by the default dry run; the Forum button is not touched.
            Assert.Contains(log, l => l.StartsWith("patch adminpage: Settings > Account: an 'MCVS Offline' button", StringComparison.Ordinal));
            Assert.Contains(log, l => l.StartsWith("patch adminlink: the 'MCVS Offline' inbox message", StringComparison.Ordinal));
            Assert.DoesNotContain(log, l => l.StartsWith("patch forum:", StringComparison.Ordinal));
            Assert.Equal(2, report.Notes.Count(n => n.Contains("PlayerProfileComponent::") && n.Contains("AdminHooks.")));
            Assert.Single(report.Notes, n => n.Contains("BrowserComponent::OpenUrl") && n.Contains("OpenIfPageMarker"));
            Assert.DoesNotContain(report.Notes, n => n.Contains("GoToForum"));

            // --game-log fatal leaves res/raw/args.txt byte for byte (VerifyUnchanged checks the bytes).
            PatchReport fatal = ApkPatcher.Run(new PatcherOptions { InputApk = apk, ServerDll = server, DryRun = true, GameLog = GameLogMode.Fatal }, null);
            Assert.DoesNotContain("res/raw/args.txt", fatal.Modified);
            Assert.Equal(ExpectedNotes(PatcherOptions.DefaultPatches, gameLogAll: false), fatal.Notes.Count);
            Assert.Equal(report.Notes.Count - 1, fatal.Notes.Count);

            // The default keeps both ABIs: both lose the AOT images of the modified assemblies.
            PatchReport both = ApkPatcher.Run(new PatcherOptions { InputApk = apk, ServerDll = server, DryRun = true }, null);
            Assert.DoesNotContain(both.Removed, r => r.Contains("(native library of an ABI not kept"));
            Assert.Equal(8, both.Removed.Count(r => r.Contains("(AOT image")));
            Assert.Contains("native libraries: every ABI kept (arm64-v8a, armeabi-v7a)", both.Notes);
        }
    }
}
