using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace McvsOffline.Patcher.Manifest
{
    /// <summary>
    /// Patch "debloat", manifest part: removes the declarations of third-party SDK components that are dead offline, so the
    /// system no longer constructs them at process start, wakes the game for their broadcasts or exposes them to other apps.
    /// <para>
    /// Every removed element was traced in the 1.17.32 code (debloat research, decompiled-java resources/AndroidManifest.xml):
    /// nothing starts the ad screens offline (no ad is ever loaded; the two AdColony ones and com.facebook.LoginActivity /
    /// iab.GMPActivity even name classes that are not in the dex), the GCM and INSTALL_REFERRER broadcasts never come, the
    /// Crashlytics and Facebook init providers only run at process start (Fabric never initialises: no google_app_id), and
    /// the permissions are unused (location), pre-Android-4.1 GCM leftovers or capped at maxSdkVersion 18 below minSdk 21.
    /// Only declarations go: every class stays in the dex (AppLinksActivity and IReferrerReceiver are read by other code),
    /// and what still works offline stays declared (FacebookActivity, GoogleApiActivity, the local push receiver and
    /// services, KeyProvider, the Mono providers, the Google Analytics components, the Play Games and Facebook ids).
    /// </para>
    /// <para>
    /// Like the IL locators, it changes nothing unless each element is found exactly once where 1.17.32 has it (same parent,
    /// same tag, same android:name): a missing or repeated one means this is not the verified manifest.
    /// </para>
    /// </summary>
    public static class ManifestDebloat
    {
        /// <summary>The --patch id (also the id of the debloat IL patches).</summary>
        public const string PatchId = "debloat";

        public const string ManifestPath = "manifest";
        public const string ApplicationPath = "manifest/application";

        /// <summary>One element to remove: where it is, what it is, and why it can go.</summary>
        public sealed class Target
        {
            public Target(string parent, string tag, string name, string reason)
            {
                Parent = parent;
                Tag = tag;
                Name = name;
                Reason = reason;
            }

            /// <summary>Path of the parent element: <see cref="ManifestPath"/> or <see cref="ApplicationPath"/>.</summary>
            public string Parent { get; }

            public string Tag { get; }

            /// <summary>The literal android:name.</summary>
            public string Name { get; }

            public string Reason { get; }

            /// <summary>Depth of the element (the root &lt;manifest&gt; is 0).</summary>
            public int Depth { get { return Parent.Split('/').Length; } }

            public bool Matches(BinaryXml.Element e)
            {
                return e.Name == Tag && e.Namespace.Length == 0 && e.AndroidName == Name && e.Parent != null && e.Parent.Path == Parent;
            }

            public override string ToString()
            {
                return "<" + Tag + " android:name=\"" + Name + "\"> in <" + Parent.Substring(Parent.LastIndexOf('/') + 1) + ">";
            }
        }

        private const string Package = KnownBuild.PackageName;
        private const string AdScreen = "ad screen: no ad is ever loaded or shown offline";
        private const string MissingAdScreen = "ad screen whose class is not even in the dex: no AdColony SDK ships";
        private const string NoClass = "leftover declaration: the class does not exist in the dex";
        private const string Gcm = "GCM remote push: Gameloft's push backend is gone and nothing sends one; local reminders are separate";
        private const string Referrer = "exported INSTALL_REFERRER receiver: Play stopped sending it in 2020 and a sideloaded APK never gets it";
        private static readonly string C2dm = "pre-Android-4.1 GCM permission, minSdk is " + KnownBuild.MinSdkVersion + "; only the removed GCM receiver used it";
        private static readonly string LegacyStorage = "maxSdkVersion 18: inert on minSdk " + KnownBuild.MinSdkVersion;

        /// <summary>The 44 elements removed, in manifest order within each parent.</summary>
        public static readonly IReadOnlyList<Target> Targets = new List<Target>
        {
            // <manifest> children
            new Target(ManifestPath, "permission", Package + ".permission.C2D_MESSAGE", C2dm),
            new Target(ManifestPath, "uses-permission", Package + ".permission.C2D_MESSAGE", C2dm),
            new Target(ManifestPath, "library", "moat-mobile-app-kit", "stray tag of the Moat ad-measurement SDK: PackageManager ignores <library> outside the system image"),
            new Target(ManifestPath, "uses-permission", "android.permission.ACCESS_COARSE_LOCATION",
                       "optional location for tracking/ads, unused by the game: LocationPlugin reports it disabled, no prompt"),
            new Target(ManifestPath, "uses-permission", "android.permission.READ_EXTERNAL_STORAGE", LegacyStorage),
            new Target(ManifestPath, "uses-permission", "android.permission.WRITE_EXTERNAL_STORAGE", LegacyStorage),
            new Target(ManifestPath, "uses-permission", "com.google.android.c2dm.permission.RECEIVE",
                       "lets Play services register a GCM push token for Gameloft's sender id; without it the token request fails and is caught"),

            // <application> children
            new Target(ApplicationPath, "receiver", "com.adjust.binding.AdjustReferrerReceiver", Referrer + "; Adjust is disabled anyway, enableAdjust=false"),
            new Target(ApplicationPath, "provider", "com.crashlytics.android.CrashlyticsInitProvider",
                       "constructed at every process start, never initialises Fabric: there is no google_app_id"),
            new Target(ApplicationPath, "activity", "com.facebook.CustomTabMainActivity",
                       "Facebook custom-tab login: needs server app settings that never come and a redirect activity this manifest lacks"),
            new Target(ApplicationPath, "provider", "com.facebook.internal.FacebookInitProvider",
                       "started the Facebook SDK and its network fetches at every process start; SocialPlugin still initialises it before any Facebook call"),
            new Target(ApplicationPath, "activity", "com.google.android.gms.appinvite.PreviewActivity",
                       "exported AppInvite preview screen, unreachable from the game's code"),
            new Target(ApplicationPath, "activity", "com.facebook.LoginActivity", NoClass + ", an old Facebook SDK's"),
            new Target(ApplicationPath, "activity", "com.gameloft.android.AppLinksActivity",
                       "autoVerify https link to the dead modern-combat-versus.extads.gameloft.com, checked at install; the class stays"),
            new Target(ApplicationPath, "activity", "com.gameloft.android.iab.GMPActivity", NoClass + "; in-app purchases use GMPHelper/GMPUtils"),
            new Target(ApplicationPath, "activity", "com.google.android.gms.ads.AdActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.facebook.ads.AudienceNetworkActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.facebook.ads.InterstitialAdActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.facebook.ads.RewardedVideoAdActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.adcolony.sdk.AdColonyInterstitialActivity", MissingAdScreen),
            new Target(ApplicationPath, "activity", "com.adcolony.sdk.AdColonyAdViewActivity", MissingAdScreen),
            new Target(ApplicationPath, "activity", "com.vungle.publisher.VideoFullScreenAdActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.vungle.publisher.MraidFullScreenAdActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.vungle.publisher.FlexViewAdActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.mopub.mobileads.MoPubActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.mopub.mobileads.MraidActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.mopub.common.MoPubBrowser", AdScreen),
            new Target(ApplicationPath, "activity", "com.mopub.mobileads.MraidVideoPlayerActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.ironsource.sdk.controller.ControllerActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.ironsource.sdk.controller.InterstitialActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.ironsource.sdk.controller.OpenUrlActivity", AdScreen),
            new Target(ApplicationPath, "service", Package + ".GCMIntentService", Gcm),
            new Target(ApplicationPath, "receiver", "com.gameloft.pushnotification.GCMBroadcastReceiver", Gcm),
            new Target(ApplicationPath, "receiver", "com.gameloft.android.GLUtils.NetworkStateReceiver",
                       "exported CONNECTIVITY_CHANGE receiver: never delivered on Android 7+, and on 5/6 its native setConnectionType has no " +
                       "implementation, an UnsatisfiedLinkError; the dynamic UtilsNetworkStateReceiver keeps reporting the network type"),
            new Target(ApplicationPath, "receiver", "com.gameloft.android.BootCompletedReceiver",
                       "started the game process at every phone boot to cache OEM preload strings; LocalPushReceiver still re-arms reminders"),
            new Target(ApplicationPath, "receiver", Package + ".installer.IReferrerReceiver", Referrer + "; the class stays, getReferrer reads its preferences"),
            new Target(ApplicationPath, "receiver", Package + ".ApplicationSetUp", "exported receiver that only stores a GA_DEBUG string nothing reads"),
            new Target(ApplicationPath, "meta-data", "io.fabric.ApiKey", "read only by Fabric, which never starts"),
            new Target(ApplicationPath, "activity", "com.google.android.gms.auth.api.signin.internal.SignInHubActivity",
                       "Google Sign-In screen the game never starts: Play Games uses the GoogleApiClient resolution, GoogleApiActivity stays"),
            new Target(ApplicationPath, "service", "com.google.android.gms.auth.api.signin.RevocationBoundService",
                       "exported service Play services binds only when a Google Sign-In grant is revoked"),
            new Target(ApplicationPath, "activity", "com.unity3d.ads.adunit.AdUnitActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.unity3d.ads.adunit.AdUnitTransparentActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.unity3d.ads.adunit.AdUnitTransparentSoftwareActivity", AdScreen),
            new Target(ApplicationPath, "activity", "com.unity3d.ads.adunit.AdUnitSoftwareActivity", AdScreen),
        }.AsReadOnly();

        /// <summary>
        /// Returns the manifest without the <see cref="Targets"/> and adds one note per removed element. Throws
        /// <see cref="InvalidDataException"/>, changing nothing, unless the package is 1.17.32's, every target is found exactly
        /// once, the two legacy storage permissions are capped at maxSdkVersion 18 and minSdkVersion is the shipped 21 (which
        /// is what makes them inert); afterwards it re-reads the result and checks that exactly the targets (and the intent
        /// filters inside them) are gone. Independent of the permission rename (<see cref="ManifestPatch"/>): either order
        /// gives the same bytes.
        /// </summary>
        public static byte[] Apply(byte[] manifest, ICollection<string> notes)
        {
            BinaryXml xml = BinaryXml.Parse(manifest);
            IReadOnlyList<BinaryXml.Element> elements = xml.Elements();
            if (elements[0].Name != "manifest") throw Refused("the root element is <" + elements[0].Name + ">, not <manifest>");

            var problems = new List<string>();
            string package = elements[0].Find("", "package")?.Value;
            if (package != KnownBuild.PackageName) problems.Add("<manifest package> is " + (package ?? "missing") + ", not " + KnownBuild.PackageName);
            var found = new List<BinaryXml.Element>();
            foreach (Target target in Targets)
            {
                List<BinaryXml.Element> matches = elements.Where(target.Matches).ToList();
                if (matches.Count == 1) found.Add(matches[0]);
                else problems.Add(target + (matches.Count == 0 ? " is missing" : " occurs " + matches.Count + " times"));
            }
            foreach (BinaryXml.Element e in found.Where(e => e.Name == "uses-permission" && e.AndroidName.EndsWith("_EXTERNAL_STORAGE", StringComparison.Ordinal)))
            {
                BinaryXml.Attribute max = e.Find(BinaryXml.AndroidNamespace, "maxSdkVersion");
                if (max == null || !IsInt(max) || max.Data != 18) problems.Add(e + " is not capped at android:maxSdkVersion=\"18\"");
            }
            List<BinaryXml.Element> sdk = elements.Where(e => e.Path == "manifest/uses-sdk").ToList();
            BinaryXml.Attribute min = sdk.Count == 1 ? sdk[0].Find(BinaryXml.AndroidNamespace, "minSdkVersion") : null;
            if (min == null || !IsInt(min) || min.Data != KnownBuild.MinSdkVersion)
                problems.Add("<uses-sdk android:minSdkVersion> is not " + KnownBuild.MinSdkVersion);
            if (problems.Count > 0) throw Refused(string.Join("; ", problems));

            var before = elements.ToList();
            var removed = new List<BinaryXml.Element>();
            for (int i = 0; i < Targets.Count; i++)
            {
                BinaryXml.Element e = found[i];
                List<BinaryXml.Element> inside = before.Where(x => IsInside(x, e)).ToList();
                xml.Remove(e);
                removed.Add(e);
                removed.AddRange(inside);
                notes?.Add("manifest: removed " + Targets[i] +
                           (inside.Count == 0 ? "" : " with the " + inside.Count + " elements inside it (" + string.Join(", ", inside.Select(x => x.Name).Distinct()) + ")") +
                           " (" + Targets[i].Reason + ")");
            }
            byte[] result = xml.ToArray();

            // Self-check on the bytes: parsed again, the tree is the original one minus exactly the removed elements.
            var expected = before.Except(removed).Select(Key).ToList();
            var actual = BinaryXml.Parse(result).Elements().Select(Key).ToList();
            if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("debloat: the rewritten manifest does not re-read as expected");
            return result;
        }

        /// <summary>Position-independent identity of an element: depth, path, android:name and every attribute.</summary>
        internal static string Key(BinaryXml.Element e)
        {
            return e.Depth + " " + e.Path + " " + e.AndroidName + " " + string.Join(" ", e.Attributes);
        }

        private static bool IsInt(BinaryXml.Attribute a)
        {
            return a.DataType == 0x10 || a.DataType == 0x11;   // TYPE_INT_DEC, TYPE_INT_HEX
        }

        private static bool IsInside(BinaryXml.Element candidate, BinaryXml.Element ancestor)
        {
            for (BinaryXml.Element p = candidate.Parent; p != null; p = p.Parent)
            {
                if (ReferenceEquals(p, ancestor)) return true;
            }
            return false;
        }

        private static InvalidDataException Refused(string why)
        {
            return new InvalidDataException(ManifestPatch.EntryName + " is not the verified " + KnownBuild.Version + " manifest, nothing was removed: " + why +
                                            " (leave '" + PatchId + "' out of --patch to keep the manifest's components)");
        }
    }
}
