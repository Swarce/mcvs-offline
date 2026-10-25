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

            Assert.Equal(new[] { "AndroidManifest.xml", "assemblies/Game.dll", "assemblies/Gameloft.Engine.dll", "assemblies/Gameloft.Util.dll", "assemblies/NicePlaySDK.dll" },
                         report.Modified.OrderBy(m => m, StringComparer.Ordinal));
            Assert.Equal(new[] { "assemblies/McvsOffline.Server.dll" }, report.Added);
            Assert.Equal(8, report.Notes.Count); // 7 IL patch notes + the manifest permission rename
            Assert.Contains(report.Notes, n => n.StartsWith("manifest: vendor-wide signature permission glshare.permission.ACCESS_SHARED_DATA renamed"));
            Assert.Contains(report.Notes, n => n.Contains("<NativeStartMatchWithBots>b__354_0") && n.Contains("IL_0007"));
            Assert.Contains(report.Notes, n => n.Contains("http://127.0.0.1:51780/location/?ip="));
            // Both ABIs lose the AOT images of exactly the four modified assemblies; v1 signature files go.
            var aot = report.Removed.Where(r => r.Contains("libaot-")).Select(r => r.Split(' ')[0]).OrderBy(r => r).ToList();
            Assert.Equal(8, aot.Count);
            foreach (string abi in new[] { "arm64-v8a", "armeabi-v7a" })
            {
                foreach (string dll in new[] { "Game", "Gameloft.Engine", "Gameloft.Util", "NicePlaySDK" })
                    Assert.Contains("lib/" + abi + "/libaot-" + dll + ".dll.so", aot);
            }
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
            Assert.Equal(5, report.Notes.Count); // 4 IL patch notes + the manifest permission rename
            Assert.DoesNotContain(report.Notes, n => n.Contains("LocalAnubisManager"));
        }
    }
}
