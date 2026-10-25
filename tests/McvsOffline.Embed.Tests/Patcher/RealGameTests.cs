using System;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher;
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

        [Fact]
        public void EveryPatchSiteIsFoundInTheRealApk()
        {
            string apk = TestEnvironment.GameApk;
            string server = TestEnvironment.EmbeddedServerDll;
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
                DryRun = true,
            };
            PatchReport report = ApkPatcher.Run(options, s => output.WriteLine(s));
            foreach (string note in report.Notes) output.WriteLine(note);

            Assert.Equal(new[] { "AndroidManifest.xml", "assemblies/Game.dll", "assemblies/Gameloft.Engine.dll", "assemblies/Gameloft.Util.dll", "assemblies/NicePlaySDK.dll",
                                 "res/raw/args.txt" },
                         report.Modified.OrderBy(m => m, StringComparer.Ordinal));
            Assert.Equal(new[] { "assemblies/McvsOffline.Server.dll" }, report.Added);
            // 22 IL patch notes (hook 1, headless 5, headless-engine 3, physics 2, nativeonline 1, waits 2, geo 1, bottile 1,
            // finder 1, lan 3, store 2), the manifest permission rename, the res/raw/args.txt rewrite and the ABI selection.
            Assert.Equal(25, report.Notes.Count);
            Assert.Contains("native libraries: kept arm64-v8a; dropped armeabi-v7a (the offline build is 64-bit ARM only)", report.Notes);
            Assert.Contains(report.Notes, n => n.Contains("KillSignatureRewardItem::GetAvailableId") && n.Contains("\"Sound\""));
            Assert.Contains(report.Notes, n => n.Contains("<SelectItems>b__8") && n.Contains("McvsStoreSafety.Report"));
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
            // arm64-v8a loses the AOT images of exactly the four modified assemblies; v1 signature files go.
            var aot = report.Removed.Where(r => r.Contains("(AOT image")).Select(r => r.Split(' ')[0]).OrderBy(r => r).ToList();
            Assert.Equal(4, aot.Count);
            foreach (string dll in new[] { "Game", "Gameloft.Engine", "Gameloft.Util", "NicePlaySDK" })
                Assert.Contains("lib/arm64-v8a/libaot-" + dll + ".dll.so", aot);
            // 64-bit only: every lib/armeabi-v7a entry goes (67 in 1.17.32), nothing of arm64-v8a but those four.
            var input = ZipArchiveReader.Read(apk);
            var dropped = report.Removed.Where(r => r.Contains("(native library of an ABI not kept")).Select(r => r.Split(' ')[0]).ToList();
            Assert.Equal(input.Entries.Count(e => e.Name.StartsWith("lib/armeabi-v7a/", StringComparison.Ordinal)), dropped.Count);
            Assert.Equal(67, dropped.Count);
            Assert.All(dropped, d => Assert.StartsWith("lib/armeabi-v7a/", d));
            Assert.DoesNotContain(report.Removed, r => r.StartsWith("lib/arm64-v8a/", StringComparison.Ordinal) && !r.Contains("(AOT image"));
            Assert.Contains(report.Warnings, w => w.StartsWith("AOT image of XaLaunchANMP.dll"));
            int entries = ZipArchiveReader.Read(apk).Entries.Count;
            Assert.Equal(entries - report.Removed.Count - report.Modified.Count, report.UnchangedEntries);
        }

        [Fact]
        public void DefaultPatchSetLeavesLocalAnubisManagerAlone()
        {
            string apk = TestEnvironment.GameApk;
            string server = TestEnvironment.EmbeddedServerDll;
            if (apk == null || server == null)
            {
                output.WriteLine("original APK (MCVS_APK) or embedded server DLL not available: skipped");
                return;
            }
            PatchReport report = ApkPatcher.Run(new PatcherOptions { InputApk = apk, ServerDll = server, DryRun = true }, null);
            // 19 IL patch notes (hook, headless 5 + 3, physics 2, nativeonline, waits 2, geo, bottile, finder, store 2), the
            // manifest rename, res/raw/args.txt, the ABI selection.
            Assert.Equal(22, report.Notes.Count);
            Assert.DoesNotContain(report.Notes, n => n.Contains("LocalAnubisManager"));

            // --game-log fatal leaves res/raw/args.txt byte for byte (VerifyUnchanged checks the bytes).
            PatchReport fatal = ApkPatcher.Run(new PatcherOptions { InputApk = apk, ServerDll = server, DryRun = true, GameLog = GameLogMode.Fatal }, null);
            Assert.DoesNotContain("res/raw/args.txt", fatal.Modified);
            Assert.Equal(21, fatal.Notes.Count);

            // --abi all keeps both ABIs (the old behaviour): both lose the AOT images of the modified assemblies.
            PatchReport both = ApkPatcher.Run(new PatcherOptions { InputApk = apk, ServerDll = server, DryRun = true, Abis = null }, null);
            Assert.DoesNotContain(both.Removed, r => r.Contains("(native library of an ABI not kept"));
            Assert.Equal(8, both.Removed.Count(r => r.Contains("(AOT image")));
            Assert.Contains("native libraries: every ABI kept (arm64-v8a, armeabi-v7a)", both.Notes);
        }
    }
}
