using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using McvsOffline.Patcher.Zip;

namespace McvsOffline.Patcher.Dex
{
    /// <summary>
    /// The dex part of patch "targetsdk" (docs/research/target-sdk.md §4.2, §4.10, WP-T3): the in-place edits of the game's
    /// Java code that a targetSdkVersion of 31 or more needs, so the raised target does not turn into crashes. Applied only
    /// when the target is at least <see cref="MinTargetSdk"/>; below that nothing is read or changed.
    /// <list type="bullet">
    /// <item>P1-P8 (31+: a PendingIntent without FLAG_IMMUTABLE or FLAG_MUTABLE throws IllegalArgumentException): every
    /// creation site the game reaches gets FLAG_IMMUTABLE (0x04000000), OR-ed into the flags it already passes. P1 is
    /// <c>MainActivity.c()</c>, the restart alarm of the graphics-setting restart and of the settings page's ViaGame restart
    /// (it passed 0x8000, an Intent flag and no PendingIntent flag, by mistake: now 0x04000000 alone). P2-P6 are the
    /// local-reminder alarms of <c>LocalPushManager</c> and P7 the reminder notification's three PendingIntents in
    /// <c>SimplifiedAndroidUtils.generateNotification</c> (one register): FLAG_UPDATE_CURRENT 0x08000000 becomes 0x0c000000.
    /// P8 is the data-download notification of <c>GameInstaller.a(ILjava/lang/String;II)V</c>, whose flags register v2 is also
    /// the request code (0): the 10 bytes of <c>const-string v3, "android.intent.category.LAUNCHER"</c> + the
    /// <c>addCategory</c> call whose result is unused become <c>const/high16 v6, #0x04000000</c> + 3 nops, and the
    /// getActivity call takes v6 as its flags. The intent keeps action MAIN and its explicit component, so dropping the
    /// category changes nothing; v3 is rewritten before any read and v6 holds nothing that is read later, handler included
    /// (<see cref="DexEditor"/> checks both). Immutable fits all of them: no sender fills anything in (AlarmManager and the
    /// notification send them as they are). Side effect: FLAG_IMMUTABLE is part of a
    /// PendingIntent's identity, so reminders an earlier build scheduled are not matched by the new CancelAll and can fire
    /// once more after the update.</item>
    /// <item>P10-P11 (34+: registerReceiver without RECEIVER_EXPORTED/NOT_EXPORTED throws SecurityException unless every action
    /// of the filter is a protected broadcast, and an empty filter has none): the two non-system filters are built empty with
    /// <c>new IntentFilter()</c> instead of <c>new IntentFilter(action)</c>, the constructor id the dex already has.
    /// <c>SimplifiedAndroidUtils.Init</c>'s "..._pushbroadcast" relay (the last statement, inside the plugin loop's try/catch)
    /// and <c>GMPHelper.&lt;init&gt;</c>'s PURCHASES_UPDATED (Play billing, which cannot work offline) then simply receive
    /// nothing, as they already do offline.</item>
    /// <item>P12-P14 (Google Analytics off): <c>GoogleAnalyticsTracker.Init</c>, <c>activityStart</c> and <c>activityStop</c>, the
    /// game's only entry points into GA, return at once (their first instruction becomes return-void, the rest of the
    /// instruction padded with nops); <c>trackEvent</c> already returns while no tracker exists. GA could never send anything
    /// offline; this also keeps its dispatch alarm (a PendingIntent without a mutability flag, report P9), its
    /// RADIO_POWERED receiver (34+) and its JobScheduler use from ever running.</item>
    /// </list>
    /// Each method is located by class, name and descriptor in whichever classes*.dex defines it, pinned by its code
    /// fingerprint, and edited only with whole instructions of the same size; see <see cref="DexEditor"/> for the checks.
    /// Not edited, and unreachable offline: the PendingIntent sites of GCM, Play services resolution, the support library and
    /// GA's own (the scan in RealDexTargetSdkTests), which is why a target of 31 or more needs the "debloat" patch too.
    /// </summary>
    public static class DexTargetSdkEdits
    {
        /// <summary>The first target SDK that needs the edits (PendingIntent mutability, Android 12).</summary>
        public const int MinTargetSdk = 31;

        private const string PendingIntent = "Landroid/app/PendingIntent;";
        private const string GetActivity = PendingIntent + "->getActivity(Landroid/content/Context;ILandroid/content/Intent;I)Landroid/app/PendingIntent;";
        private const string LocalPushManager = "Lcom/gameloft/pushnotification/LocalPushManager;";
        private const string SimplifiedAndroidUtils = "Lcom/gameloft/pushnotification/SimplifiedAndroidUtils;";
        private const string GoogleAnalyticsTracker = "Lcom/gameloft/android/utils/GoogleAnalyticsTracker;";
        private const string GaGetInstance = "Lcom/google/android/gms/analytics/GoogleAnalytics;->getInstance(Landroid/content/Context;)Lcom/google/android/gms/analytics/GoogleAnalytics;";
        private const string FilterWithAction = "Landroid/content/IntentFilter;-><init>(Ljava/lang/String;)V";
        private const string EmptyFilter = "Landroid/content/IntentFilter;-><init>()V";

        private const string Reminder = "local-reminder alarm: FLAG_UPDATE_CURRENT -> FLAG_UPDATE_CURRENT | FLAG_IMMUTABLE";
        private const string Ga = "Google Analytics entry point returns at once (GA off)";

        private static DexSiteEdit Site(int pc, string before, string after)
        {
            return new DexSiteEdit(pc, new[] { before }, new[] { after });
        }

        private static DexSiteEdit UpdateCurrent(int pc, int register)
        {
            return Site(pc, "const/high16 v" + register + ", #0x08000000", "const/high16 v" + register + ", #0x0c000000");
        }

        /// <summary>The edits, in the report's order (P9 is covered by P12-P14).</summary>
        public static readonly IReadOnlyList<DexMethodEdit> Edits = new List<DexMethodEdit>
        {
            new DexMethodEdit("P1", "Lcom/gameloft/android/MainActivity;", "c", "()V",
                "a6fc625f29942766f150b551881d5e59a4a92818ba96f04e57d1e9cf42923299",
                "restart alarm: the Intent flag 0x8000 passed by mistake -> FLAG_IMMUTABLE",
                Site(0x0016, "const v3, #0x00008000", "const v3, #0x04000000")),
            new DexMethodEdit("P2", LocalPushManager, "SetAlarm", "(Landroid/os/Bundle;I)Ljava/lang/String;",
                "67eb1fca774242fefd4498cbade525cce6c56c04c11e1fd9483ca91de4aacbcf", Reminder, UpdateCurrent(0x0057, 5)),
            new DexMethodEdit("P3", LocalPushManager, "SetRestoredAlarm", "(Landroid/content/Context;Ljava/lang/String;ILandroid/os/Bundle;I)V",
                "546b558d139ab83329593a4fc3d8f1accff5dd186dbfa3fe887fa18a96ba567c", Reminder, UpdateCurrent(0x0030, 0)),
            new DexMethodEdit("P4", LocalPushManager, "TriggerAlarm", "(Landroid/content/Context;Ljava/lang/String;)V",
                "50093708cdb845d4cafc86ef4a53d63ec8392b99fdafaac5b5a701c1f80f9ae5", Reminder, UpdateCurrent(0x008f, 0)),
            new DexMethodEdit("P5", LocalPushManager, "CancelAlarm", "(Landroid/content/Context;Ljava/lang/String;)V",
                "c6b413af2d69acc3ded228d2a69d582d4e429c47c2c3bcc4d67b49c50357f5e0", Reminder, UpdateCurrent(0x0010, 2)),
            new DexMethodEdit("P6", LocalPushManager, "CancelAll", "()V",
                "c3baa0abd12f1128663c64fa6ab56bca3bfe5f2c9da3d0b5129dfd3e4574eb16", Reminder, UpdateCurrent(0x002f, 7)),
            new DexMethodEdit("P7", SimplifiedAndroidUtils, "generateNotification",
                "(Landroid/content/Context;Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;Landroid/content/Intent;Landroid/os/Bundle;)V",
                "ce654203624f66f22e6706baa7ab1e8db2890f5ef17dd802988c471c8ef80320",
                "reminder notification, one flags register for its three PendingIntents: FLAG_UPDATE_CURRENT -> FLAG_UPDATE_CURRENT | FLAG_IMMUTABLE",
                UpdateCurrent(0x0101, 5)),
            new DexMethodEdit("P8", "Lcom/gameloft/android/installer/GameInstaller;", "a", "(ILjava/lang/String;II)V",
                "ee2fe57b35f84a912cf8b6bf8bb607121632498aa2fa586055f5a945f1ea540f",
                "data-download notification: flags 0 (shared with the request code) -> FLAG_IMMUTABLE in v6; the unused LAUNCHER " +
                "category of an explicit intent makes room for it",
                new DexSiteEdit(0x002c,
                    new[] { "const-string v3, \"android.intent.category.LAUNCHER\"", "invoke-virtual {v0, v3}, Landroid/content/Intent;->addCategory(Ljava/lang/String;)Landroid/content/Intent;" },
                    new[] { "const/high16 v6, #0x04000000", "nop", "nop", "nop" }),
                Site(0x003d, "invoke-static {v7, v2, v0, v2}, " + GetActivity, "invoke-static {v7, v2, v0, v6}, " + GetActivity)),
            new DexMethodEdit("P10", SimplifiedAndroidUtils, "Init", "(Landroid/app/Activity;)V",
                "8c800598ce973b79cf6d2d97208fd5a8ec3f743e3d24577640d4cafc57eb984c",
                "push relay receiver gets an empty IntentFilter (no unprotected custom action for registerReceiver on 34+)",
                Site(0x003d, "invoke-direct {v2, v0}, " + FilterWithAction, "invoke-direct {v2}, " + EmptyFilter)),
            new DexMethodEdit("P11", "Lcom/gameloft/android/iab/GMPHelper;", "<init>", "(Lcom/gameloft/android/iab/ServerInfo;)V",
                "0feda9e641208d2b6865b65fda435d132be21267b77d62f1e9440d8314eb2dc9",
                "Play billing PURCHASES_UPDATED receiver gets an empty IntentFilter (registerReceiver on 34+; billing cannot work offline)",
                Site(0x0059, "invoke-direct {v0, v1}, " + FilterWithAction, "invoke-direct {v0}, " + EmptyFilter)),
            new DexMethodEdit("P12", GoogleAnalyticsTracker, "Init", "(Landroid/content/Context;)V",
                "d7631e08d81b36c75c75c9ed298a3360a50d65d3624d3170dadf4a588949aa05", Ga,
                new DexSiteEdit(0x0000, new[] { "sput-object v0, " + GoogleAnalyticsTracker + "->d:Landroid/content/Context;" }, new[] { "return-void", "nop" })),
            new DexMethodEdit("P13", GoogleAnalyticsTracker, "activityStart", "(Landroid/app/Activity;)V",
                "6b5dfae85b38ec19984039650bdb746a2781fdcbccfc215877f7ceeb70b8e684", Ga,
                new DexSiteEdit(0x0000, new[] { "invoke-static {v1}, " + GaGetInstance }, new[] { "return-void", "nop", "nop" })),
            new DexMethodEdit("P14", GoogleAnalyticsTracker, "activityStop", "(Landroid/app/Activity;)V",
                "9065c95e4762f7aaaf689c252b2bb90be6ba91ed5fcebfec7a25f5c89b95a238", Ga,
                new DexSiteEdit(0x0000, new[] { "invoke-static {v1}, " + GaGetInstance }, new[] { "return-void", "nop", "nop" })),
        }.AsReadOnly();

        private static readonly Regex DexEntry = new Regex(@"^classes\d*\.dex$", RegexOptions.CultureInvariant);

        /// <summary>The APK's dex entries: classes.dex, classes2.dex, ... at the root.</summary>
        public static bool IsDexEntry(string name)
        {
            return DexEntry.IsMatch(name);
        }

        /// <summary>
        /// For a target SDK of at least <see cref="MinTargetSdk"/>: verifies every edit against the APK's classes*.dex and
        /// returns the new content of each dex entry that changes (by entry name; for 1.17.32 only classes.dex), adding one
        /// note per edited site. Below <see cref="MinTargetSdk"/> it returns an empty map and reads nothing. Throws
        /// <see cref="InvalidDataException"/>, before anything is written, when a dex does not parse or its checksum is
        /// wrong, when a class is defined in no dex or in several, or when any method or site is not the verified code. The
        /// caller replaces the entries (same compression method) and writes nothing else for them.
        /// </summary>
        public static IReadOnlyDictionary<string, byte[]> Apply(IEnumerable<ZipEntry> entries, int targetSdk, ICollection<string> notes)
        {
            return Apply(entries, targetSdk, notes, Edits);
        }

        /// <summary>The output entry for an edited dex: same name, same compression method as the original (DEFLATED in 1.17.32).</summary>
        public static ZipEntry ReplacementEntry(ZipEntry original, byte[] data)
        {
            return original.Method == ZipEntry.MethodStored ? ZipEntry.CreateStored(original.Name, data, original) : ZipEntry.CreateDeflated(original.Name, data, original);
        }

        /// <summary><see cref="Apply(IEnumerable{ZipEntry}, int, ICollection{string})"/> with other edits (tests).</summary>
        internal static IReadOnlyDictionary<string, byte[]> Apply(IEnumerable<ZipEntry> entries, int targetSdk, ICollection<string> notes,
                                                                  IReadOnlyList<DexMethodEdit> edits)
        {
            var result = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            if (targetSdk < MinTargetSdk) return result;
            List<DexFile> dexFiles = entries.Where(e => IsDexEntry(e.Name)).OrderBy(e => e.Name, StringComparer.Ordinal)
                                            .Select(e => DexFile.Parse(e.Name, e.GetData())).ToList();
            if (dexFiles.Count == 0) throw new InvalidDataException("the APK has no classes.dex");

            var byDex = new Dictionary<DexFile, List<DexMethodEdit>>();
            foreach (DexMethodEdit edit in edits)
            {
                List<DexFile> owners = dexFiles.Where(d => d.FindClass(edit.ClassDescriptor) != null).ToList();
                if (owners.Count != 1)
                {
                    throw new InvalidDataException(DexFile.JavaName(edit.ClassDescriptor) + " (" + edit.Id + ") is defined in " +
                                                   (owners.Count == 0 ? "no dex file" : string.Join(" and ", owners.Select(d => d.Name))) +
                                                   " of the APK, expected exactly one: not the verified " + KnownBuild.Version + " code, nothing was changed");
                }
                if (!byDex.TryGetValue(owners[0], out List<DexMethodEdit> list)) byDex[owners[0]] = list = new List<DexMethodEdit>();
                list.Add(edit);
            }
            var pending = new List<string>();
            foreach (DexFile dex in dexFiles.Where(byDex.ContainsKey))
                result[dex.Name] = DexEditor.Apply(dex, byDex[dex], pending);
            foreach (string n in pending) notes?.Add(n);
            return result;
        }
    }
}
