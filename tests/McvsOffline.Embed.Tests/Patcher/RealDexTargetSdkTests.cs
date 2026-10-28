using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Dex;
using McvsOffline.Patcher.Tools;
using McvsOffline.Patcher.Zip;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// The dex edits of patch "targetsdk" (P1-P8, P10-P14 of docs/research/target-sdk.md) on the player's REAL classes.dex
    /// (MCVS_APK): the shipped methods have the verified fingerprints, every edit lands exactly where the report's prototype
    /// put it and nowhere else, no PendingIntent creation site without a mutability flag is left in code the game reaches,
    /// Google Analytics can no longer start, and dexdump and d8 accept the result when Android build-tools are found.
    /// Skipped without the APK unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    public class RealDexTargetSdkTests
    {
        private readonly ITestOutputHelper output;

        public RealDexTargetSdkTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static readonly object Gate = new object();
        private static List<ZipEntry> entries;
        private static IReadOnlyDictionary<string, byte[]> patched;
        private static List<string> notes;

        /// <summary>The APK's entries (read once for the class), or null when the APK is not available.</summary>
        private List<ZipEntry> Entries()
        {
            string apk = TestEnvironment.GameApk;
            if (apk == null)
            {
                if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                    throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but the original APK (MCVS_APK) is missing");
                output.WriteLine("original APK (MCVS_APK) not available: skipped");
                return null;
            }
            lock (Gate)
            {
                return entries ?? (entries = ZipArchiveReader.Read(apk).Entries.ToList());
            }
        }

        /// <summary>The APK's entries and the edits' result at target 35 (computed once for the class), or false without the APK.</summary>
        private bool Load(out List<ZipEntry> apkEntries, out IReadOnlyDictionary<string, byte[]> result, out List<string> resultNotes)
        {
            apkEntries = Entries();
            result = null;
            resultNotes = null;
            if (apkEntries == null) return false;
            lock (Gate)
            {
                if (patched == null)
                {
                    var n = new List<string>();
                    patched = DexTargetSdkEdits.Apply(apkEntries, 35, n);
                    notes = n;
                }
                result = patched;
                resultNotes = notes;
                return true;
            }
        }

        private static DexFile Original(List<ZipEntry> apkEntries, string name)
        {
            return DexFile.Parse(name, apkEntries.Single(e => e.Name == name).GetData());
        }

        // ------------------------------------------------------------------ the shipped code

        [Fact]
        public void TheShippedMethodsHaveTheVerifiedFingerprints()
        {
            List<ZipEntry> apkEntries = Entries();
            if (apkEntries == null) return;
            Assert.Equal(new[] { "classes.dex", "classes2.dex" }, apkEntries.Where(e => DexTargetSdkEdits.IsDexEntry(e.Name)).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));
            DexFile dex = Original(apkEntries, "classes.dex");
            DexFile dex2 = Original(apkEntries, "classes2.dex");
            var mismatches = new List<string>();
            foreach (DexMethodEdit edit in DexTargetSdkEdits.Edits)
            {
                Assert.Null(DexEditor.Locate(dex2, edit));   // every patched class lives in classes.dex
                DexFile.Method method = DexEditor.Locate(dex, edit);
                Assert.NotNull(method);
                string actual = DexEditor.Fingerprint(dex, method);
                output.WriteLine(edit.Id + " " + method.DisplayName + " " + actual);
                if (actual != edit.Fingerprint) mismatches.Add(edit.Id + ": " + actual);
            }
            Assert.Empty(mismatches);
        }

        /// <summary>The report's file offsets (dexdump, classes.dex of 1.17.32) of the code units each site changes.</summary>
        [Fact]
        public void TheSitesAreWhereTheReportFoundThem()
        {
            List<ZipEntry> apkEntries = Entries();
            if (apkEntries == null) return;
            DexFile dex = Original(apkEntries, "classes.dex");
            var expected = new Dictionary<string, int[]>
            {
                ["P1"] = new[] { 0x2db230 }, ["P2"] = new[] { 0x308196 }, ["P3"] = new[] { 0x30861c }, ["P4"] = new[] { 0x30878e },
                ["P5"] = new[] { 0x308254 }, ["P6"] = new[] { 0x308302 }, ["P7"] = new[] { 0x30a1ae }, ["P8"] = new[] { 0x2f2a70, 0x2f2a92 },
                ["P10"] = new[] { 0x309da6 }, ["P11"] = new[] { 0x2e60ee }, ["P12"] = new[] { 0x2fe114 }, ["P13"] = new[] { 0x2fe130 },
                ["P14"] = new[] { 0x2fe160 },
            };
            Assert.Equal(expected.Keys.OrderBy(k => k), DexTargetSdkEdits.Edits.Select(e => e.Id).OrderBy(k => k));
            foreach (DexMethodEdit edit in DexTargetSdkEdits.Edits)
            {
                DexFile.CodeItem code = dex.GetCode(DexEditor.Locate(dex, edit));
                Assert.Equal(expected[edit.Id], edit.Sites.Select(s => code.FileOffsetOf(s.Pc)).ToArray());
            }
        }

        // ------------------------------------------------------------------ the edits

        [Fact]
        public void OnlyTheEditedCodeUnitsAndTheHeaderChange()
        {
            if (!Load(out List<ZipEntry> apkEntries, out IReadOnlyDictionary<string, byte[]> result, out List<string> resultNotes)) return;
            Assert.Equal(new[] { "classes.dex" }, result.Keys);
            foreach (string n in resultNotes) output.WriteLine(n);
            Assert.Equal(DexTargetSdkEdits.Edits.Sum(e => e.Sites.Count), resultNotes.Count);
            Assert.All(resultNotes, n => Assert.StartsWith("classes.dex: ", n));

            // The research prototype's byte table (tsdk-scripts/dexpatch.py, checked there with dexdump -c and a full d8 re-dex):
            // applied to the original by hand it must give exactly the patcher's output.
            byte[] original = apkEntries.Single(e => e.Name == "classes.dex").GetData();
            var table = new (int Offset, string Expected, string New)[]
            {
                (0x2db230, "140300800000", "140300000004"),
                (0x308254, "15020008", "1502000c"), (0x308302, "15070008", "1507000c"), (0x308196, "15050008", "1505000c"),
                (0x30861c, "15000008", "1500000c"), (0x30878e, "15000008", "1500000c"), (0x30a1ae, "15050008", "1505000c"),
                (0x2f2a70, "1a03a8836e2074023000", "15060004000000000000"), (0x2f2a92, "7140a3012720", "7140a3012760"),
                (0x309da6, "7020b8020200", "7010b7020200"), (0x2e60ee, "7020b8021000", "7010b7020000"),
                (0x2fe114, "6900e444", "0e000000"), (0x2fe130, "7110a2790100", "0e0000000000"), (0x2fe160, "7110a2790100", "0e0000000000"),
            };
            byte[] byHand = (byte[])original.Clone();
            foreach (var t in table)
            {
                byte[] e = Convert.FromHexString(t.Expected), n = Convert.FromHexString(t.New);
                Assert.Equal(e, original.AsSpan(t.Offset, e.Length).ToArray());
                n.CopyTo(byHand, t.Offset);
            }
            DexFile.UpdateChecksumAndSignature(byHand);
            Assert.Equal(byHand, result["classes.dex"]);

            // The shipped header's checksum is right and its SHA-1 signature is not (nothing on Android reads the signature);
            // the edited file gets a correct one.
            Assert.False(DexFile.Parse("classes.dex", original).SignatureMatches);
            Assert.True(DexFile.Parse("classes.dex", result["classes.dex"]).SignatureMatches);

            // Every other byte is the original one.
            var changed = Enumerable.Range(0, original.Length).Where(k => original[k] != result["classes.dex"][k]).ToList();
            var allowed = new HashSet<int>(Enumerable.Range(DexFile.ChecksumOffset, 24));
            foreach (var t in table) allowed.UnionWith(Enumerable.Range(t.Offset, t.New.Length / 2));
            Assert.DoesNotContain(changed, k => !allowed.Contains(k));
            Assert.Equal(original.Length, result["classes.dex"].Length);
            output.WriteLine(changed.Count + " bytes differ (header checksum/signature included)");
        }

        [Fact]
        public void TheEditedMethodsReadAsTheReportDescribes()
        {
            if (!Load(out List<ZipEntry> apkEntries, out IReadOnlyDictionary<string, byte[]> result, out _)) return;
            DexFile dex = DexFile.Parse("classes.dex", result["classes.dex"]);
            DexFile.Method Find(string cls, string name, string descriptor) => dex.FindMethod(cls, name, descriptor);
            string At(DexFile.Method m, int pc) => DexInstruction.Decode(dex.GetCode(m).Insns, pc).Render(dex);

            DexFile.Method restart = Find("Lcom/gameloft/android/MainActivity;", "c", "()V");
            Assert.Equal("const v3, #0x04000000", At(restart, 0x16));
            Assert.Equal("invoke-static {v8, v1, v2, v3}, Landroid/app/PendingIntent;->getActivity(Landroid/content/Context;ILandroid/content/Intent;I)Landroid/app/PendingIntent;", At(restart, 0x19));

            DexFile.Method installer = Find("Lcom/gameloft/android/installer/GameInstaller;", "a", "(ILjava/lang/String;II)V");
            Assert.Equal("const/high16 v6, #0x04000000", At(installer, 0x2c));
            Assert.Equal(new[] { "nop", "nop", "nop" }, new[] { 0x2e, 0x2f, 0x30 }.Select(pc => At(installer, pc)));
            Assert.Equal("invoke-direct {v0, v3}, Landroid/content/Intent;-><init>(Ljava/lang/String;)V", At(installer, 0x29));   // action MAIN stays
            Assert.Equal("invoke-virtual {v0, v3}, Landroid/content/Intent;->setComponent(Landroid/content/ComponentName;)Landroid/content/Intent;", At(installer, 0x3a));
            Assert.Equal("invoke-static {v7, v2, v0, v6}, Landroid/app/PendingIntent;->getActivity(Landroid/content/Context;ILandroid/content/Intent;I)Landroid/app/PendingIntent;", At(installer, 0x3d));

            Assert.Equal("invoke-direct {v2}, Landroid/content/IntentFilter;-><init>()V",
                         At(Find("Lcom/gameloft/pushnotification/SimplifiedAndroidUtils;", "Init", "(Landroid/app/Activity;)V"), 0x3d));
            Assert.Equal("invoke-direct {v0}, Landroid/content/IntentFilter;-><init>()V",
                         At(Find("Lcom/gameloft/android/iab/GMPHelper;", "<init>", "(Lcom/gameloft/android/iab/ServerInfo;)V"), 0x59));
            foreach (string ga in new[] { "Init(Landroid/content/Context;)V", "activityStart(Landroid/app/Activity;)V", "activityStop(Landroid/app/Activity;)V" })
            {
                DexFile.Method m = Find("Lcom/gameloft/android/utils/GoogleAnalyticsTracker;", ga.Substring(0, ga.IndexOf('(')), ga.Substring(ga.IndexOf('(')));
                Assert.Equal("return-void", At(m, 0));
            }
        }

        /// <summary>
        /// Critic note 4: P8 drops the write of v3 (const-string LAUNCHER) and starts writing v6. Both are dead there: v3 is
        /// rewritten (new-instance ComponentName at 0x31) before any read, and v6 is written (move-result at 0x6d, const/4 at
        /// 0x73) before any read on every path; the site lies in the try 0x04-0x5f, whose Exception handler at 0x103 reads
        /// neither. DexEditor refuses the edit otherwise; this states the facts on the real method.
        /// </summary>
        [Fact]
        public void P8OnlyUsesDeadRegisters()
        {
            List<ZipEntry> apkEntries = Entries();
            if (apkEntries == null) return;
            DexFile dex = Original(apkEntries, "classes.dex");
            DexFile.Method installer = dex.FindMethod("Lcom/gameloft/android/installer/GameInstaller;", "a", "(ILjava/lang/String;II)V");
            DexFile.CodeItem code = dex.GetCode(installer);
            var flow = new DexFlow(code, DexInstruction.DecodeAll(code.Insns));
            Assert.Equal(new[] { (0x04, 0x5b), (0x64, 0x9f) }, code.Tries.Select(t => (t.StartAddr, t.InsnCount)));
            Assert.Equal(new[] { 0x103 }, flow.HandlersAt(0x2c));
            Assert.False(flow.IsLiveBefore(3, 0x31, out DexInstruction reader3), reader3?.ToString());
            Assert.False(flow.IsLiveBefore(6, 0x2c, out DexInstruction reader6), reader6?.ToString());
            Assert.False(flow.IsLiveBefore(3, 0x103, out reader3), reader3?.ToString());
            Assert.False(flow.IsLiveBefore(6, 0x103, out reader6), reader6?.ToString());
            Assert.Equal(12, code.RegistersSize);
            Assert.Equal(5, code.InsSize);   // v7..v11 are this and the parameters: v6 is a local
            Assert.True(flow.IsLiveBefore(2, 0x3d, out _));   // the request code 0 stays in v2
        }

        // ------------------------------------------------------------------ what the game can still reach

        /// <summary>
        /// The PendingIntent creation sites that keep no mutability flag after the edits, each unreachable offline. Every other
        /// creation site in classes.dex and classes2.dex passes a constant with FLAG_IMMUTABLE or FLAG_MUTABLE.
        /// </summary>
        private static readonly Dictionary<string, string> UnreachableSites = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Landroid/support/v4/app/TaskStackBuilder$TaskStackBuilderImplBase;->getPendingIntent(Landroid/content/Context;[Landroid/content/Intent;IILandroid/os/Bundle;)Landroid/app/PendingIntent; @000f"] = SupportLibrary,
            ["Landroid/support/v4/app/TaskStackBuilderHoneycomb;->getActivitiesPendingIntent(Landroid/content/Context;I[Landroid/content/Intent;I)Landroid/app/PendingIntent; @0000"] = SupportLibrary,
            ["Landroid/support/v4/app/TaskStackBuilderJellybean;->getActivitiesPendingIntent(Landroid/content/Context;I[Landroid/content/Intent;ILandroid/os/Bundle;)Landroid/app/PendingIntent; @0000"] = SupportLibrary,
            ["Landroid/support/v4/media/TransportMediatorJellybeanMR2;->windowAttached()V @0010"] = SupportLibrary,
            ["Landroid/support/v4/media/session/MediaSessionCompat$MediaSessionImplBase;-><init>(Landroid/content/Context;Ljava/lang/String;Landroid/content/ComponentName;Landroid/app/PendingIntent;)V @006e"] = SupportLibrary,
            ["Landroid/support/v7/widget/SearchView;->createVoiceAppSearchIntent(Landroid/content/Intent;Landroid/app/SearchableInfo;)Landroid/content/Intent; @0015"] = SupportLibrary,
            ["Lcom/google/android/gcm/GCMRegistrar;->a(Landroid/content/Context;)V @0029"] = Gcm,
            ["Lcom/google/android/gcm/GCMRegistrar;->a(Landroid/content/Context;[Ljava/lang/String;)V @0035"] = Gcm,
            ["Lcom/google/android/gcm/a;->onHandleIntent(Landroid/content/Intent;)V @00bf"] = Gcm,
            ["Lcom/google/android/gms/gcm/GcmNetworkManager;-><init>(Landroid/content/Context;)V @001a"] = Gcm,
            ["Lcom/google/android/gms/gcm/GoogleCloudMessaging;->zzf(Landroid/content/Intent;)V @0012"] = Gcm,
            ["Lcom/google/android/gms/gcm/zza;->zzt(Landroid/os/Bundle;)Landroid/app/PendingIntent; @0077"] = Gcm,
            ["Lcom/google/android/gms/iid/zze;->zzg(Landroid/content/Intent;)V @0012"] = Gcm,
            ["Lcom/google/android/gms/common/api/GoogleApiActivity;->zza(Landroid/content/Context;Landroid/app/PendingIntent;I)Landroid/app/PendingIntent; @0008"] = PlayServices,
            ["Lcom/google/android/gms/common/api/internal/zzbp;->zza(Lcom/google/android/gms/common/api/internal/zzh;I)Landroid/app/PendingIntent; @001b"] = PlayServices,
            ["Lcom/google/android/gms/common/api/internal/zzy;->zzagm()Landroid/app/PendingIntent; @0016"] = PlayServices,
            ["Lcom/google/android/gms/common/zze;->zza(Landroid/content/Context;IILjava/lang/String;)Landroid/app/PendingIntent; @000a"] = PlayServices,
            ["Lcom/google/android/gms/internal/zzash;->getHintPickerIntent(Lcom/google/android/gms/common/api/GoogleApiClient;Lcom/google/android/gms/auth/api/credentials/HintRequest;)Landroid/app/PendingIntent; @005f"] = PlayServices,
            ["Lcom/google/android/gms/internal/zzaoa;->zzyk()Landroid/app/PendingIntent; @001a"] = GoogleAnalytics,
        };

        private const string SupportLibrary = "support-library helper the game never calls";
        private const string Gcm = "GCM remote push: no token request offline (debloat), its receiver and service are out of the manifest";
        private const string PlayServices = "Play services sign-in/resolution UI: Play Games and Facebook are not initialised offline (debloat)";
        private const string GoogleAnalytics = "GA's dispatch alarm (report P9): GA never starts once P12-P14 return at once";

        /// <summary>The sites the edits fix: P1-P8 (P7 is one register for three calls).</summary>
        private static readonly string[] FixedSites =
        {
            "Lcom/gameloft/android/MainActivity;->c()V @0019",
            "Lcom/gameloft/pushnotification/LocalPushManager;->SetAlarm(Landroid/os/Bundle;I)Ljava/lang/String; @0059",
            "Lcom/gameloft/pushnotification/LocalPushManager;->SetRestoredAlarm(Landroid/content/Context;Ljava/lang/String;ILandroid/os/Bundle;I)V @0032",
            "Lcom/gameloft/pushnotification/LocalPushManager;->TriggerAlarm(Landroid/content/Context;Ljava/lang/String;)V @0091",
            "Lcom/gameloft/pushnotification/LocalPushManager;->CancelAlarm(Landroid/content/Context;Ljava/lang/String;)V @0012",
            "Lcom/gameloft/pushnotification/LocalPushManager;->CancelAll()V @0031",
            "Lcom/gameloft/pushnotification/SimplifiedAndroidUtils;->generateNotification(Landroid/content/Context;Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;Landroid/content/Intent;Landroid/os/Bundle;)V @0105",
            "Lcom/gameloft/pushnotification/SimplifiedAndroidUtils;->generateNotification(Landroid/content/Context;Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;Landroid/content/Intent;Landroid/os/Bundle;)V @0121",
            "Lcom/gameloft/pushnotification/SimplifiedAndroidUtils;->generateNotification(Landroid/content/Context;Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;Landroid/content/Intent;Landroid/os/Bundle;)V @0129",
            "Lcom/gameloft/android/installer/GameInstaller;->a(ILjava/lang/String;II)V @003d",
        };

        [Fact]
        public void NoReachablePendingIntentIsCreatedWithoutAMutabilityFlag()
        {
            if (!Load(out List<ZipEntry> apkEntries, out IReadOnlyDictionary<string, byte[]> result, out _)) return;
            var before = new List<DexScan.PendingIntentSite>();
            var after = new List<DexScan.PendingIntentSite>();
            foreach (ZipEntry e in apkEntries.Where(e => DexTargetSdkEdits.IsDexEntry(e.Name)))
            {
                before.AddRange(DexScan.PendingIntentSites(DexFile.Parse(e.Name, e.GetData())));
                after.AddRange(DexScan.PendingIntentSites(DexFile.Parse(e.Name, result.TryGetValue(e.Name, out byte[] p) ? p : e.GetData())));
            }
            foreach (var s in after) output.WriteLine((s.HasMutabilityFlag ? "ok   " : "none ") + s);
            Assert.Equal(29, after.Count);   // the report's count: 29 creation sites, all in classes.dex
            Assert.All(after, s => Assert.Equal("classes.dex", s.Dex));
            Assert.Equal(before.Select(s => s.Key), after.Select(s => s.Key));

            // Before: exactly the fixed sites and the unreachable ones lack a flag.
            var unflaggedBefore = new SortedSet<string>(before.Where(s => !s.HasMutabilityFlag).Select(s => s.Key), StringComparer.Ordinal);
            Assert.Equal(new SortedSet<string>(UnreachableSites.Keys.Concat(FixedSites), StringComparer.Ordinal), unflaggedBefore);

            // After: only the unreachable ones.
            var unflaggedAfter = new SortedSet<string>(after.Where(s => !s.HasMutabilityFlag).Select(s => s.Key), StringComparer.Ordinal);
            Assert.Equal(new SortedSet<string>(UnreachableSites.Keys, StringComparer.Ordinal), unflaggedAfter);
            foreach (string key in FixedSites)
            {
                DexScan.PendingIntentSite site = after.Single(s => s.Key == key);
                Assert.True(site.Flags.All(f => (f & DexScan.FlagImmutable) != 0), site.ToString());
                // The edit only adds FLAG_IMMUTABLE: FLAG_UPDATE_CURRENT stays where it was.
                DexScan.PendingIntentSite old = before.Single(s => s.Key == key);
                Assert.Equal(old.Flags.Select(f => f == 0x8000 ? DexScan.FlagImmutable : f | DexScan.FlagImmutable), site.Flags);
            }
        }

        /// <summary>
        /// GA (the source of the unflagged zzaoa.zzyk alarm, the RADIO_POWERED receiver and the JobScheduler use) is entered from
        /// game code only through GoogleAnalyticsTracker and the INSTALL_REFERRER receiver that "debloat" takes out of the
        /// manifest; the tracker object is created only by getTracker, called only by Init, which now returns at once, and
        /// trackEvent/sendTimingTracking do nothing without it.
        /// </summary>
        [Fact]
        public void GoogleAnalyticsCanNoLongerStart()
        {
            if (!Load(out List<ZipEntry> apkEntries, out IReadOnlyDictionary<string, byte[]> result, out _)) return;
            const string Tracker = "Lcom/gameloft/android/utils/GoogleAnalyticsTracker;";
            var entriesFromGame = new SortedSet<string>(StringComparer.Ordinal);
            var getTrackerCallers = new SortedSet<string>(StringComparer.Ordinal);
            foreach (ZipEntry e in apkEntries.Where(e => DexTargetSdkEdits.IsDexEntry(e.Name)))
            {
                DexFile dex = DexFile.Parse(e.Name, result.TryGetValue(e.Name, out byte[] p) ? p : e.GetData());
                entriesFromGame.UnionWith(DexScan.Callers(dex, r => r.StartsWith("Lcom/google/android/gms/analytics/", StringComparison.Ordinal))
                                                  .Where(c => !c.StartsWith("Lcom/google/android/gms/", StringComparison.Ordinal))
                                                  .Select(c => c.Substring(0, c.IndexOf("->", StringComparison.Ordinal) + 2) + c.Substring(c.IndexOf("->", StringComparison.Ordinal) + 2).Split('(')[0]));
                getTrackerCallers.UnionWith(DexScan.Callers(dex, r => r.StartsWith(Tracker + "->getTracker(", StringComparison.Ordinal)));
            }
            output.WriteLine(string.Join("\n", entriesFromGame));
            Assert.All(entriesFromGame, c => Assert.True(c.StartsWith(Tracker, StringComparison.Ordinal) ||
                                                         c == "Lcom/gameloft/android/ANMP/GloftMVHM/installer/IReferrerReceiver;->onReceive", c));
            Assert.Equal(new[] { Tracker + "->Init(Landroid/content/Context;)V" }, getTrackerCallers);
        }

        // ------------------------------------------------------------------ refusals on the real file

        [Fact]
        public void AChangedMethodIsRefusedAndNothingIsReturned()
        {
            List<ZipEntry> apkEntries = Entries();
            if (apkEntries == null) return;
            ZipEntry classes = apkEntries.Single(e => e.Name == "classes.dex");
            byte[] tampered = (byte[])classes.GetData().Clone();
            tampered[0x2db248 + 2] = 0x85;   // MainActivity.c: const-wide/16 v6, #900 -> #901 (the restart delay)
            DexFile.UpdateChecksumAndSignature(tampered);
            var replaced = apkEntries.Select(e => e == classes ? ZipEntry.CreateDeflated(e.Name, tampered, e) : e).ToList();
            var n = new List<string>();
            var ex = Assert.Throws<InvalidDataException>(() => DexTargetSdkEdits.Apply(replaced, 35, n));
            output.WriteLine(ex.Message);
            Assert.Contains("com.gameloft.android.MainActivity.c()V (P1)", ex.Message);
            Assert.Contains("fingerprint", ex.Message);
            Assert.Empty(n);

            // A dex whose checksum no longer matches its content is refused before any lookup.
            byte[] corrupt = (byte[])classes.GetData().Clone();
            corrupt[0x2db248 + 2] = 0x85;
            replaced = apkEntries.Select(e => e == classes ? ZipEntry.CreateDeflated(e.Name, corrupt, e) : e).ToList();
            ex = Assert.Throws<InvalidDataException>(() => DexTargetSdkEdits.Apply(replaced, 35, n));
            Assert.Contains("checksum", ex.Message);
        }

        [Fact]
        public void BelowTarget31NothingIsReadOrChanged()
        {
            if (!Load(out List<ZipEntry> apkEntries, out _, out _)) return;
            var n = new List<string>();
            Assert.Empty(DexTargetSdkEdits.Apply(apkEntries, 29, n));
            Assert.Empty(DexTargetSdkEdits.Apply(apkEntries, 30, n));
            Assert.Empty(n);
            Assert.NotEmpty(DexTargetSdkEdits.Apply(apkEntries, DexTargetSdkEdits.MinTargetSdk, n));
        }

        /// <summary>
        /// The integration the 'targetsdk' patch performs, done here by hand: every entry but classes.dex is copied as it is,
        /// classes.dex is replaced with the same compression method, and the written archive reads back that way (and passes
        /// zipalign's check when build-tools are found).
        /// </summary>
        [Fact]
        public void AnApkWithTheEditedDexKeepsEveryOtherEntry()
        {
            if (!Load(out List<ZipEntry> apkEntries, out IReadOnlyDictionary<string, byte[]> result, out _)) return;
            List<ZipEntry> composed = apkEntries.Where(e => !e.Name.StartsWith("META-INF/", StringComparison.Ordinal))
                                                .Select(e => result.TryGetValue(e.Name, out byte[] dex) ? DexTargetSdkEdits.ReplacementEntry(e, dex) : e).ToList();
            string dir = TestEnvironment.NewTempDir("dex-apk");
            try
            {
                string file = Path.Combine(dir, "unsigned.apk");
                using (var stream = File.Create(file)) ZipArchiveWriter.Write(composed, stream);
                ZipArchiveReader.Archive written = ZipArchiveReader.Read(file);
                foreach (ZipEntry e in composed)
                {
                    ZipEntry o = written.Find(e.Name);
                    Assert.NotNull(o);
                    ZipEntry original = apkEntries.Single(x => x.Name == e.Name);
                    Assert.Equal(original.Method, o.Method);
                    if (e.Name == "classes.dex") Assert.Equal(result["classes.dex"], o.GetData());
                    else Assert.True(o.Crc32 == original.Crc32 && o.RawData.AsSpan().SequenceEqual(original.RawData), e.Name + " changed");
                }
                string zipalign = TestEnvironment.BuildToolsDir == null ? null : Path.Combine(TestEnvironment.BuildToolsDir, "zipalign");
                if (zipalign != null && File.Exists(zipalign))
                    Assert.Equal(0, BuildTools.Run(zipalign, new[] { "-c", "-p", "4", file }, null, out _));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        // ------------------------------------------------------------------ Android's own tools

        [Fact]
        public void DexdumpAndD8AcceptThePatchedDex()
        {
            if (!Load(out _, out IReadOnlyDictionary<string, byte[]> result, out _)) return;
            string tools = TestEnvironment.BuildToolsDir;
            string dexdump = tools == null ? null : Path.Combine(tools, "dexdump");
            string d8 = tools == null ? null : Path.Combine(tools, "d8");
            if (dexdump == null || !File.Exists(dexdump) || !File.Exists(d8))
            {
                output.WriteLine("dexdump/d8 not found (MCVS_BUILD_TOOLS): skipped");
                return;
            }
            string dir = TestEnvironment.NewTempDir("dex-targetsdk");
            try
            {
                string file = Path.Combine(dir, "classes.dex");
                File.WriteAllBytes(file, result["classes.dex"]);

                // dexdump verifies the structure and the checksum; the disassembly of every edited site shows the new code.
                Assert.Equal(0, BuildTools.Run(dexdump, new[] { "-c", file }, null, out string check));
                Assert.Contains("Checksum verified", check);
                string listing = Path.Combine(dir, "classes.dis");
                Assert.Equal(0, BuildTools.Run(dexdump, new[] { "-d", "-o", listing, file }, null, out string dumpErrors));
                Assert.DoesNotContain("Failure", dumpErrors);
                var sites = new Dictionary<int, string>
                {
                    [0x2db230] = "|0016: const v3, ", [0x2f2a70] = "|002c: const/high16 v6, ", [0x2f2a74] = "|002e: nop",
                    [0x2f2a92] = "|003d: invoke-static {v7, v2, v0, v6}, Landroid/app/PendingIntent;.getActivity:",
                    [0x309da6] = "|003d: invoke-direct {v2}, Landroid/content/IntentFilter;.<init>:()V",
                    [0x2e60ee] = "|0059: invoke-direct {v0}, Landroid/content/IntentFilter;.<init>:()V",
                    [0x2fe114] = "|0000: return-void", [0x2fe130] = "|0000: return-void", [0x2fe160] = "|0000: return-void",
                    [0x308196] = "|0057: const/high16 v5, ", [0x30a1ae] = "|0101: const/high16 v5, ",
                };
                var found = new Dictionary<int, string>();
                foreach (string line in File.ReadLines(listing))
                {
                    int colon = line.IndexOf(':');
                    if (colon != 6 || !int.TryParse(line.Substring(0, 6), System.Globalization.NumberStyles.HexNumber, null, out int at)) continue;
                    if (sites.ContainsKey(at)) found[at] = line;
                }
                foreach (var site in sites)
                {
                    Assert.True(found.ContainsKey(site.Key), "no dexdump line at " + site.Key.ToString("x"));
                    output.WriteLine(found[site.Key]);
                    Assert.Contains(site.Value, found[site.Key]);
                }
                Assert.Contains("// #04000000", found[0x2db230]);
                Assert.Contains("#int 201326592", found[0x308196]);   // 0x0c000000
                File.Delete(listing);

                // d8 re-dexes the whole file (it parses every method and builds its own IR from it).
                string outDir = Path.Combine(dir, "d8");
                Directory.CreateDirectory(outDir);
                int code = BuildTools.Run(d8, new[] { "--release", "--min-api", "21", "--output", outDir, file }, null, out string d8Output);
                output.WriteLine(d8Output);
                Assert.Equal(0, code);
                Assert.True(File.Exists(Path.Combine(outDir, "classes.dex")));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
