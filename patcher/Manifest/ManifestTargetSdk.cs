using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace McvsOffline.Patcher.Manifest
{
    /// <summary>
    /// Patch "targetsdk", manifest part (docs/research/target-sdk.md WP-T2): raises <c>targetSdkVersion</c> from the shipped
    /// 29 so Play Protect stops warning "built for an older version of Android" on install (it warns when the target is more
    /// than 2 below the device's API level: below 34 on Android 16, below 35 on Android 17), with what the higher target
    /// needs in the manifest:
    /// <list type="bullet">
    /// <item>android:exported="true" on every activity, activity-alias, receiver and service that has an intent filter and no
    /// exported (Android 12+ refuses to install the APK otherwise; true is the pre-31 default, so nothing changes);</item>
    /// <item>android:allowNativeHeapPointerTagging="false" on &lt;application&gt; (from target 30 Scudo tags the top byte of
    /// malloc pointers on arm64; Havok, the engine and Mono 2021 never ran with it);</item>
    /// <item>&lt;uses-permission android.permission.POST_NOTIFICATIONS&gt; (from target 33 local reminders and the download
    /// notification need it declared; the host asks for it at run time);</item>
    /// <item>&lt;queries&gt; for VIEW + BROWSABLE http/https intents (from target 30 InGameBrowser's queryIntentActivities sees
    /// no browser without it and external links do nothing; the share sheet queries nothing and needs no entry);</item>
    /// <item>at 36 only: android:enableOnBackInvokedCallback="false" (predictive back would stop delivering KEYCODE_BACK to
    /// MainActivity and onBackPressed to InGameBrowser) and android:appCategory="game" (keeps the fixed orientation on
    /// large screens).</item>
    /// </list>
    /// compileSdkVersion and platformBuildVersion* stay as shipped (informational). The settings page's layouts get their
    /// edge-to-edge fix from <see cref="BrowserLayoutInsets"/>; the dex side (PendingIntent flags, receivers, Google Analytics)
    /// is a separate edit of classes.dex (Dex.DexTargetSdkEdits, same run), without which ApkPatcher refuses 31+. Target 29
    /// changes nothing at all. A target of 31 or more needs "debloat": the
    /// GCM and ad components it removes would otherwise need their own exported values and PendingIntent fixes.
    /// </summary>
    public static class ManifestTargetSdk
    {
        /// <summary>The --patch id.</summary>
        public const string PatchId = "targetsdk";

        /// <summary>The shipped targetSdkVersion: --target-sdk 29 keeps the manifest exactly as without the patch.</summary>
        public const int ShippedTarget = 29;

        /// <summary>
        /// The target without --target-sdk: 35 (no Play Protect warning up to Android 17; target-sdk.md §7). The manifest is
        /// only half of it: from <see cref="DexEditsRequiredFrom"/> on the game's own classes.dex would throw (a PendingIntent
        /// without a mutability flag, a receiver registered without an exported flag; "T2 alone is not shippable"), so
        /// "targetsdk" also edits classes.dex in the same run (Dex.DexTargetSdkEdits, WP-T3) and ApkPatcher refuses 31+
        /// without that edit.
        /// </summary>
        public const int DefaultTarget = 35;

        /// <summary>The values --target-sdk accepts.</summary>
        public static readonly IReadOnlyList<int> SupportedTargets = new[] { 29, 35, 36 };

        /// <summary>From this target on, PackageManager refuses an intent-filter component without android:exported.</summary>
        public const int ExportedRequiredFrom = 31;

        /// <summary>
        /// From this target on the game's classes.dex needs the targetsdk edits (Android 12 throws IllegalArgumentException
        /// for MainActivity's restart PendingIntent, flags 0x8000 only; Android 14 throws SecurityException for receivers
        /// registered without an exported flag at 34+).
        /// </summary>
        public const int DexEditsRequiredFrom = 31;

        /// <summary>The dex entry holding the PendingIntent and receiver sites (MainActivity, GMPHelper).</summary>
        public const string DexEntry = "classes.dex";

        /// <summary>SHA-256 of 1.17.32's AndroidManifest.xml as shipped (23,092 bytes, CRC32 717fdb3c).</summary>
        public const string ShippedManifestSha256 = "8c28916b39da66c1980f3335bb19c0a4eb7a72bb2c33810e29fc584dc02a3db2";

        // Framework attribute ids (frameworks/base/core/res/res/values/public-final.xml).
        public const uint NameId = 0x01010003;
        public const uint ExportedId = 0x01010010;
        public const uint SchemeId = 0x01010027;
        public const uint TargetSdkVersionId = 0x01010270;
        public const uint MinSdkVersionId = 0x0101020c;
        public const uint AppCategoryId = 0x01010545;
        public const uint AllowNativeHeapPointerTaggingId = 0x01010612;
        public const uint EnableOnBackInvokedCallbackId = 0x0101066c;

        public const string PostNotifications = "android.permission.POST_NOTIFICATIONS";

        /// <summary>Components the exported rule applies to (PackageParser reads exported on these).</summary>
        private static readonly string[] ComponentTags = { "activity", "activity-alias", "receiver", "service" };

        private const string ManifestPath = "manifest";
        private const string ApplicationPath = "manifest/application";

        /// <summary>The SHA-256 of <paramref name="data"/> as lower-case hex.</summary>
        public static string Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
            }
        }

        /// <summary>
        /// The patch on the APK's manifest: refuses (<see cref="InvalidDataException"/>, nothing written) unless
        /// <paramref name="shipped"/>, the APK's AndroidManifest.xml as read, is 1.17.32's byte for byte; then applies
        /// <see cref="Edit"/> to <paramref name="manifest"/>, the manifest after the permission rename and debloat.
        /// Returns <paramref name="manifest"/> itself for <see cref="ShippedTarget"/>.
        /// </summary>
        public static byte[] Apply(byte[] shipped, byte[] manifest, int target, ICollection<string> notes)
        {
            string actual = Sha256(shipped);
            if (actual != ShippedManifestSha256)
            {
                throw new InvalidDataException(ManifestPatch.EntryName + " is not the verified " + KnownBuild.Version + " manifest (SHA-256 " + actual +
                                               "), nothing was written: use --target-sdk " + ShippedTarget + " or leave '" + PatchId + "' out of --patch");
            }
            return Edit(manifest, target, notes);
        }

        /// <summary>
        /// The manifest edits for <paramref name="target"/> with one note each (none for <see cref="ShippedTarget"/>, which
        /// returns the input). Checks the shape it relies on first (one &lt;uses-sdk&gt; with integer minSdkVersion 21 and
        /// targetSdkVersion 29, one &lt;application&gt;, no POST_NOTIFICATIONS or &lt;queries&gt; yet, none of the
        /// application attributes it adds) and refuses (<see cref="InvalidDataException"/>) anything else; afterwards it
        /// re-reads the result and checks every edit and that nothing else moved.
        /// </summary>
        public static byte[] Edit(byte[] manifest, int target, ICollection<string> notes)
        {
            if (!SupportedTargets.Contains(target)) throw new ArgumentException("target SDK " + target + " is not one of " + string.Join(", ", SupportedTargets));
            if (target == ShippedTarget) return manifest;

            BinaryXml xml = BinaryXml.Parse(manifest);
            IReadOnlyList<BinaryXml.Element> elements = xml.Elements();
            var problems = new List<string>();
            BinaryXml.Element root = elements[0];
            if (root.Name != "manifest") throw Refused("the root element is <" + root.Name + ">, not <manifest>");
            BinaryXml.Element usesSdk = Single(elements, ManifestPath + "/uses-sdk", problems);
            BinaryXml.Element application = Single(elements, ApplicationPath, problems);
            if (usesSdk != null)
            {
                CheckInt(usesSdk, "minSdkVersion", MinSdkVersionId, KnownBuild.MinSdkVersion, problems);
                CheckInt(usesSdk, "targetSdkVersion", TargetSdkVersionId, ShippedTarget, problems);
            }
            if (application != null)
            {
                foreach (uint id in new[] { AllowNativeHeapPointerTaggingId, EnableOnBackInvokedCallbackId, AppCategoryId })
                    if (application.Attributes.Any(a => a.ResourceId == id)) problems.Add("<application> already sets attribute 0x" + id.ToString("x8"));
            }
            if (elements.Any(e => e.Path == ManifestPath + "/uses-permission" && e.AndroidName == PostNotifications)) problems.Add("POST_NOTIFICATIONS is already declared");
            if (elements.Any(e => e.Path == ManifestPath + "/queries")) problems.Add("<queries> already exists");
            if (problems.Count > 0) throw Refused(string.Join("; ", problems));

            // 1. The target.
            xml.SetInt(usesSdk, BinaryXml.AndroidNamespace, "targetSdkVersion", target);
            notes?.Add("manifest: <uses-sdk android:targetSdkVersion> " + ShippedTarget + " -> " + target +
                       " (Play Protect's \"built for an older version of Android\" warning starts more than 2 API levels below the device: none up to API " +
                       (target + 2) + ", Android " + (target - 18) + "; --target-sdk " + ShippedTarget + " keeps the shipped manifest)");

            // 2. exported on every component with an intent filter that lacks it.
            List<BinaryXml.Element> components = Unexported(elements);
            foreach (BinaryXml.Element c in components)
            {
                xml.InsertAttribute(c, BinaryXml.NewAttribute.Android("exported", ExportedId, BinaryXml.Value.Boolean(true)));
                notes?.Add("manifest: android:exported=\"true\" on " + c + " (it has an intent filter: from target " + ExportedRequiredFrom +
                           " Android refuses to install without exported; true is the old default, nothing changes)");
            }

            // 3. <application> attributes.
            var added = new List<(string Name, uint Id, BinaryXml.Value Value, string Why)>
            {
                ("allowNativeHeapPointerTagging", AllowNativeHeapPointerTaggingId, BinaryXml.Value.Boolean(false),
                 "from target 30 arm64 malloc pointers carry a tag in the top byte; Havok, the engine and Mono never ran with it"),
            };
            if (target >= 36)
            {
                added.Add(("enableOnBackInvokedCallback", EnableOnBackInvokedCallbackId, BinaryXml.Value.Boolean(false),
                           "at target 36 predictive back stops sending KEYCODE_BACK to the game and onBackPressed to the settings page"));
                added.Add(("appCategory", AppCategoryId, BinaryXml.Value.Integer(0),
                           "\"game\": at target 36 games keep their fixed landscape orientation on large screens"));
            }
            foreach (var a in added)
            {
                xml.InsertAttribute(application, BinaryXml.NewAttribute.Android(a.Name, a.Id, a.Value));
                notes?.Add("manifest: android:" + a.Name + "=" + (a.Id == AppCategoryId ? "\"game\"" : "\"" + a.Value + "\"") + " on <application> (" + a.Why + ")");
            }

            // 4. POST_NOTIFICATIONS, after the last <uses-permission> of <manifest>.
            BinaryXml.Element lastPermission = elements.LastOrDefault(e => e.Path == ManifestPath + "/uses-permission");
            BinaryXml.Element next = lastPermission == null ? null : elements.Where(e => e.Parent == root && e.Index > lastPermission.Index).FirstOrDefault();
            int line = (next ?? lastPermission ?? root).Line;
            xml.InsertElement(root, new BinaryXml.NewElement("uses-permission", line, BinaryXml.NewAttribute.Android("name", NameId, BinaryXml.Value.String(PostNotifications))), next);
            notes?.Add("manifest: + <uses-permission android:name=\"" + PostNotifications + "\"> (from target 33 local reminders and the data-download " +
                       "notification need it declared; ignored below Android 13)");

            // 5. <queries> for a browser, before <application>.
            xml.InsertElement(root, Queries(application.Line), application);
            notes?.Add("manifest: + <queries> for VIEW/BROWSABLE http and https intents (from target 30 InGameBrowser finds no browser without it and " +
                       "external links do nothing; the share sheet needs no entry)");

            byte[] result = xml.ToArray();
            Verify(manifest, result, target, components);
            return result;
        }

        /// <summary>The &lt;queries&gt; element: one VIEW + BROWSABLE intent per web scheme.</summary>
        public static BinaryXml.NewElement Queries(int line)
        {
            var queries = new BinaryXml.NewElement("queries", line);
            foreach (string scheme in new[] { "https", "http" })
            {
                queries.Add(new BinaryXml.NewElement("intent", line)
                    .Add(new BinaryXml.NewElement("action", line, BinaryXml.NewAttribute.Android("name", NameId, BinaryXml.Value.String("android.intent.action.VIEW"))))
                    .Add(new BinaryXml.NewElement("category", line, BinaryXml.NewAttribute.Android("name", NameId, BinaryXml.Value.String("android.intent.category.BROWSABLE"))))
                    .Add(new BinaryXml.NewElement("data", line, BinaryXml.NewAttribute.Android("scheme", SchemeId, BinaryXml.Value.String(scheme)))));
            }
            return queries;
        }

        /// <summary>Components of &lt;application&gt; with an &lt;intent-filter&gt; child and no android:exported, in document order.</summary>
        public static List<BinaryXml.Element> Unexported(IReadOnlyList<BinaryXml.Element> elements)
        {
            return elements.Where(e => e.Parent != null && e.Parent.Path == ApplicationPath && ComponentTags.Contains(e.Name) &&
                                       !e.Attributes.Any(a => a.ResourceId == ExportedId) &&
                                       elements.Any(f => ReferenceEquals(f.Parent, e) && f.Name == "intent-filter"))
                           .ToList();
        }

        /// <summary>Re-reads the result: the edits are there, and the elements of the input are all still there, in order.</summary>
        private static void Verify(byte[] input, byte[] result, int target, List<BinaryXml.Element> exported)
        {
            IReadOnlyList<BinaryXml.Element> before = BinaryXml.Parse(input).Elements();
            IReadOnlyList<BinaryXml.Element> after = BinaryXml.Parse(result).Elements();
            var problems = new List<string>();
            BinaryXml.Element usesSdk = after.Single(e => e.Path == ManifestPath + "/uses-sdk");
            if (usesSdk.Attributes.Single(a => a.ResourceId == TargetSdkVersionId).Data != target) problems.Add("targetSdkVersion");
            if (Unexported(after).Count > 0) problems.Add("a component with an intent filter has no exported");
            BinaryXml.Element application = after.Single(e => e.Path == ApplicationPath);
            if (application.Attributes.Count(a => a.ResourceId == AllowNativeHeapPointerTaggingId && a.Data == 0) != 1) problems.Add("allowNativeHeapPointerTagging");
            if (target >= 36 && application.Attributes.Count(a => (a.ResourceId == EnableOnBackInvokedCallbackId || a.ResourceId == AppCategoryId) && a.Data == 0) != 2)
                problems.Add("the target-36 application attributes");
            var ids = application.Attributes.Select(a => a.ResourceId).Where(id => id != 0).ToList();
            if (!ids.SequenceEqual(ids.OrderBy(id => id))) problems.Add("the <application> attributes are not in resource-id order");
            if (after.Count(e => e.Path == ManifestPath + "/uses-permission" && e.AndroidName == PostNotifications) != 1) problems.Add("POST_NOTIFICATIONS");
            List<BinaryXml.Element> queries = after.Where(e => e.Path.StartsWith(ManifestPath + "/queries", StringComparison.Ordinal)).ToList();
            if (queries.Count != 1 + 2 * 4) problems.Add("<queries>");
            var kept = after.Where(e => !queries.Contains(e) && !(e.Path == ManifestPath + "/uses-permission" && e.AndroidName == PostNotifications))
                            .Select(e => e.Path + " " + e.AndroidName + " " + e.Line).ToList();
            if (!kept.SequenceEqual(before.Select(e => e.Path + " " + e.AndroidName + " " + e.Line))) problems.Add("the input's elements changed");
            foreach (BinaryXml.Element e in exported)
            {
                BinaryXml.Element now = after.Single(x => x.Path == e.Path && x.AndroidName == e.AndroidName && x.Line == e.Line);
                if (now.Attributes.Count != e.Attributes.Count + 1 || now.Attributes.Single(a => a.ResourceId == ExportedId).Data != 0xFFFFFFFF)
                    problems.Add("exported on " + e);
            }
            if (problems.Count > 0) throw new InvalidOperationException(PatchId + ": the rewritten manifest does not re-read as expected: " + string.Join(", ", problems));
        }

        private static BinaryXml.Element Single(IReadOnlyList<BinaryXml.Element> elements, string path, List<string> problems)
        {
            List<BinaryXml.Element> found = elements.Where(e => e.Path == path).ToList();
            if (found.Count == 1) return found[0];
            problems.Add("<" + path.Substring(path.LastIndexOf('/') + 1) + "> occurs " + found.Count + " times");
            return null;
        }

        private static void CheckInt(BinaryXml.Element element, string name, uint id, int expected, List<string> problems)
        {
            List<BinaryXml.Attribute> found = element.Attributes.Where(a => a.ResourceId == id).ToList();
            bool ok = found.Count == 1 && found[0].Namespace == BinaryXml.AndroidNamespace && found[0].Name == name && found[0].RawValue < 0 &&
                      (found[0].DataType == 0x10 || found[0].DataType == 0x11) && found[0].Data == (uint)expected;
            if (!ok) problems.Add("<" + element.Name + " android:" + name + "> is not the integer " + expected);
        }

        private static InvalidDataException Refused(string why)
        {
            return new InvalidDataException(ManifestPatch.EntryName + " is not shaped like the verified " + KnownBuild.Version + " manifest, nothing was written: " + why +
                                            " (use --target-sdk " + ShippedTarget + ")");
        }
    }
}
