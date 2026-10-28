using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace McvsOffline.Patcher.Manifest
{
    /// <summary>
    /// Patch "targetsdk", layout part (docs/research/target-sdk.md WP-T4): android:fitsSystemWindows="true" on the root
    /// RelativeLayout (<c>@id/ingamebrowser_mview</c>) of both <c>activity_in_game_browser</c> layouts. From target 35
    /// Android draws every window edge to edge; InGameBrowser (the game's browser, which shows the settings page) hides the
    /// status bar but is not immersive, so the navigation bar would cover the page's edge. With the flag the root view takes
    /// the system-bar insets as padding, at 35 and at 36 (where the edge-to-edge opt-out is gone). The fix is in the layouts
    /// rather than a v35 theme opt-out: a v35 style entry would hide the launch-screen items of the style's v31 entry
    /// (styles are not merged across configurations; app-icon-and-launch-screen.md, critic note 1). At target 29 the layouts
    /// stay as shipped.
    /// </summary>
    public static class BrowserLayoutInsets
    {
        /// <summary>The two layouts (InGameBrowser.onCreate: setContentView(R.layout.activity_in_game_browser)) and the SHA-256 they ship with.</summary>
        public static readonly IReadOnlyDictionary<string, string> Layouts = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["res/layout-land/activity_in_game_browser.xml"] = "e9e23bca9157669df3ffa7b081d384688917dc125ed1bbf084731e13c98af510",
            ["res/layout-port/activity_in_game_browser.xml"] = "1bcdc26fe8853b8da30f461fed33da6433a12f49a0854d4ed8f39317dfb33616",
        };

        /// <summary>R.id.ingamebrowser_mview, the root view InGameBrowser looks up.</summary>
        public const uint RootViewId = 0x7f0e0075;

        public const uint IdId = 0x010100d0;
        public const uint FitsSystemWindowsId = 0x010100dd;

        /// <summary>
        /// The patch on one layout entry: refuses (<see cref="InvalidDataException"/>, nothing written) unless the bytes are
        /// the shipped ones (<see cref="Layouts"/>), then applies <see cref="Edit"/>.
        /// </summary>
        public static byte[] Apply(string entry, byte[] layout, ICollection<string> notes)
        {
            if (!Layouts.TryGetValue(entry, out string expected)) throw new ArgumentException(entry + " is not one of the browser layouts", nameof(entry));
            string actual = ManifestTargetSdk.Sha256(layout);
            if (actual != expected)
            {
                throw new InvalidDataException(entry + " is not the verified " + KnownBuild.Version + " layout (SHA-256 " + actual + "), nothing was written: use --target-sdk " +
                                               ManifestTargetSdk.ShippedTarget + " or leave '" + ManifestTargetSdk.PatchId + "' out of --patch");
            }
            return Edit(entry, layout, notes);
        }

        /// <summary>
        /// Inserts android:fitsSystemWindows="true" into the root element, which must be a RelativeLayout with
        /// android:id=@0x7f0e0075 and no fitsSystemWindows yet (<see cref="InvalidDataException"/> otherwise); re-reads the
        /// result and checks that only that attribute was added.
        /// </summary>
        public static byte[] Edit(string entry, byte[] layout, ICollection<string> notes)
        {
            BinaryXml xml = BinaryXml.Parse(layout);
            IReadOnlyList<BinaryXml.Element> elements = xml.Elements();
            BinaryXml.Element root = elements[0];
            List<BinaryXml.Attribute> id = root.Attributes.Where(a => a.ResourceId == IdId).ToList();
            if (root.Name != "RelativeLayout" || id.Count != 1 || id[0].DataType != 0x01 || id[0].Data != RootViewId)   // TYPE_REFERENCE
                throw new InvalidDataException(entry + ": the root element is not RelativeLayout android:id=@0x" + RootViewId.ToString("x8") + ", nothing was written");
            if (root.Attributes.Any(a => a.ResourceId == FitsSystemWindowsId)) throw new InvalidDataException(entry + ": the root element already sets fitsSystemWindows");

            xml.InsertAttribute(root, BinaryXml.NewAttribute.Android("fitsSystemWindows", FitsSystemWindowsId, BinaryXml.Value.Boolean(true)));
            byte[] result = xml.ToArray();

            IReadOnlyList<BinaryXml.Element> after = BinaryXml.Parse(result).Elements();
            List<string> keysBefore = elements.Select(ManifestDebloat.Key).ToList(), keysAfter = after.Select(ManifestDebloat.Key).ToList();
            BinaryXml.Attribute fits = after[0].Attributes.SingleOrDefault(a => a.ResourceId == FitsSystemWindowsId);
            if (fits == null || fits.Data != 0xFFFFFFFF || after[0].Attributes.Count != root.Attributes.Count + 1 ||
                !keysBefore.Skip(1).SequenceEqual(keysAfter.Skip(1)) ||
                !after[0].Attributes.Where(a => a.ResourceId != FitsSystemWindowsId).Select(a => a.ToString()).SequenceEqual(root.Attributes.Select(a => a.ToString())))
                throw new InvalidOperationException(ManifestTargetSdk.PatchId + ": " + entry + " does not re-read as expected");
            notes?.Add(entry + ": android:fitsSystemWindows=\"true\" on the root RelativeLayout (from target 35 the settings page's browser is drawn edge " +
                       "to edge: the root view now pads itself out from under the navigation bar)");
            return result;
        }
    }
}
