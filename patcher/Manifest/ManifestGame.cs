using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace McvsOffline.Patcher.Manifest
{
    /// <summary>
    /// Patch "game" (docs/design/pc-port.md §10): marks the app as a game in the manifest, so Android and OEM game tools
    /// can treat it as one:
    /// <list type="bullet">
    /// <item>android:appCategory="game" (0x01010545, enum game = 0) on &lt;application&gt;: ApplicationInfo.category ==
    /// CATEGORY_GAME is what GameManager (Game Mode, <c>cmd game</c>, the PowerHAL GAME hint) checks since Android 12. A
    /// store sets a category hint for its own installs; a sideloaded APK has none, so the manifest is the only on-device
    /// signal. The parse is not gated by targetSdk. At target 36 it also keeps the fixed orientation on large screens,
    /// which is why "targetsdk" 36 needs this patch;</item>
    /// <item>android:isGame="true" (0x010103f4) on &lt;application&gt;: the deprecated FLAG_IS_GAME. GameManager ignores it,
    /// but AOSP Settings still puts such an app's storage under "Games", and some launchers read it.</item>
    /// </list>
    /// Both are written as aapt2 writes them from source (TYPE_INT_BOOLEAN 0xFFFFFFFF and TYPE_INT_DEC 0, no raw value), in
    /// resource-id order. Samsung's own game list is keyed by package name; these flags are the only lever the APK has.
    /// Runs after "targetsdk" (whose input check wants a manifest without appCategory) and at any target, 29 included. No
    /// android.game_mode_config: a config with the defaults changes nothing, and its opt-ins would only stop the system's
    /// own interventions.
    /// </summary>
    public static class ManifestGame
    {
        /// <summary>The --patch id.</summary>
        public const string PatchId = "game";

        /// <summary>attr/isGame, boolean (API 21).</summary>
        public const uint IsGameId = 0x010103f4;

        /// <summary>attr/appCategory, enum (API 26).</summary>
        public const uint AppCategoryId = ManifestTargetSdk.AppCategoryId;

        /// <summary>appCategory's "game" (ApplicationInfo.CATEGORY_GAME).</summary>
        public const int CategoryGame = 0;

        /// <summary>The prefix of this patch's notes (distinct from the "manifest: android:" notes of "targetsdk").</summary>
        public const string NotePrefix = "manifest: game: ";

        private const string ApplicationPath = "manifest/application";

        /// <summary>
        /// The two attributes on the manifest's &lt;application&gt;, one note each. Refuses (<see cref="InvalidDataException"/>,
        /// nothing written) a manifest without exactly one &lt;application&gt; or whose &lt;application&gt; already sets either
        /// attribute, whatever its value; afterwards it re-reads the result and checks that nothing else moved.
        /// </summary>
        public static byte[] Edit(byte[] manifest, ICollection<string> notes)
        {
            BinaryXml xml = BinaryXml.Parse(manifest);
            List<BinaryXml.Element> applications = xml.Elements().Where(e => e.Path == ApplicationPath).ToList();
            if (applications.Count != 1) throw Refused("<application> occurs " + applications.Count + " times");
            BinaryXml.Element application = applications[0];
            var present = application.Attributes.Where(a => IsOneOfOurs(a)).Select(a => a.ToString()).ToList();
            if (present.Count > 0) throw Refused("<application> already sets " + string.Join(", ", present));

            xml.InsertAttribute(application, BinaryXml.NewAttribute.Android("isGame", IsGameId, BinaryXml.Value.Boolean(true)));
            xml.InsertAttribute(application, BinaryXml.NewAttribute.Android("appCategory", AppCategoryId, BinaryXml.Value.Integer(CategoryGame)));
            notes?.Add(NotePrefix + "android:isGame=\"true\" on <application> (the deprecated FLAG_IS_GAME: GameManager ignores it, but launchers " +
                       "and AOSP Settings' storage \"Games\" bucket still read it)");
            notes?.Add(NotePrefix + "android:appCategory=\"game\" on <application> (ApplicationInfo.category == CATEGORY_GAME: Android's GameManager " +
                       "and Game Mode treat the app as a game; a sideloaded APK has no store category hint)");

            byte[] result = xml.ToArray();
            Verify(manifest, result);
            return result;
        }

        /// <summary>isGame or appCategory, by resource id or by android: name.</summary>
        private static bool IsOneOfOurs(BinaryXml.Attribute a)
        {
            return a.ResourceId == IsGameId || a.ResourceId == AppCategoryId ||
                   (a.Namespace == BinaryXml.AndroidNamespace && (a.Name == "isGame" || a.Name == "appCategory"));
        }

        /// <summary>
        /// Re-reads the result: both attributes with their exact encoding, the android attributes in resource-id order, the
        /// element list (path, android:name, line) of the input, and &lt;application&gt; with every original attribute
        /// unchanged plus exactly the two new ones.
        /// </summary>
        private static void Verify(byte[] input, byte[] result)
        {
            IReadOnlyList<BinaryXml.Element> before = BinaryXml.Parse(input).Elements();
            IReadOnlyList<BinaryXml.Element> after = BinaryXml.Parse(result).Elements();
            var problems = new List<string>();
            BinaryXml.Element application = after.Single(e => e.Path == ApplicationPath);
            if (application.Attributes.Count(a => a.ResourceId == IsGameId && a.Namespace == BinaryXml.AndroidNamespace && a.Name == "isGame" &&
                                                  a.DataType == 0x12 && a.Data == 0xFFFFFFFF && a.RawValue < 0) != 1)
                problems.Add("isGame");
            if (application.Attributes.Count(a => a.ResourceId == AppCategoryId && a.Namespace == BinaryXml.AndroidNamespace && a.Name == "appCategory" &&
                                                  a.DataType == 0x10 && a.Data == (uint)CategoryGame && a.RawValue < 0) != 1)
                problems.Add("appCategory");
            var ids = application.Attributes.Select(a => a.ResourceId).Where(id => id != 0).ToList();
            if (!ids.SequenceEqual(ids.OrderBy(id => id))) problems.Add("the <application> attributes are not in resource-id order");
            if (!after.Select(Key).SequenceEqual(before.Select(Key))) problems.Add("the input's elements changed");
            List<string> original = before.Single(e => e.Path == ApplicationPath).Attributes.Select(a => a.ToString()).ToList();
            List<string> kept = application.Attributes.Where(a => !IsOneOfOurs(a)).Select(a => a.ToString()).ToList();
            if (application.Attributes.Count != original.Count + 2 || !kept.SequenceEqual(original)) problems.Add("the other <application> attributes");
            if (problems.Count > 0) throw new InvalidOperationException(PatchId + ": the rewritten manifest does not re-read as expected: " + string.Join(", ", problems));
        }

        private static string Key(BinaryXml.Element e)
        {
            return e.Path + " " + e.AndroidName + " " + e.Line;
        }

        private static InvalidDataException Refused(string why)
        {
            return new InvalidDataException(ManifestPatch.EntryName + " cannot get the game flags, nothing was written: " + why +
                                            " (leave '" + PatchId + "' out of --patch)");
        }
    }
}
