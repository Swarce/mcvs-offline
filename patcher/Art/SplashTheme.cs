using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Patcher.Resources;

namespace McvsOffline.Patcher.Art
{
    /// <summary>
    /// The launch screen part of the art patch (app-icon-and-launch-screen.md §3, §5.2, WP-2). On Android 12+ the system
    /// draws a splash from the launcher activity's theme, at any targetSdk (<c>SplashscreenContentDrawer.getWindowAttrs</c>
    /// reads the attributes without a target check). XamarinMainActivity's theme is style/Theme.acp.notitlebar.fullscreen
    /// (0x7f0b017e; <c>Resource.cs</c> compiles the id into managed code, so no id may move), shipped in the configurations
    /// <c>()</c>, <c>v11</c> and <c>v21</c> with no items. The patch adds a <c>v31</c> configuration of that one entry
    /// (and <c>night-v31</c> when night mode has its own colours): a new style type chunk right after the style type's
    /// last chunk, holding only entry 0x17e with the parent and items of its highest shipped configuration plus
    /// windowSplashScreenBackground and the optional extras. That is the table aapt2 builds for a values-v31/themes.xml.
    /// <para>
    /// Android never merges a style's items across configurations: the best-matching configuration wins whole, and
    /// inheritance runs only through <c>parent</c>. A later <c>v35</c> (or other) configuration of this entry would hide
    /// these items on newer devices (critic note 1), so the patch refuses an entry that already has a configuration at
    /// v31 or above, and the targetSdk work fixes the settings page with <c>fitsSystemWindows</c> in its layouts instead
    /// of a v35 theme.
    /// </para>
    /// </summary>
    public static class SplashTheme
    {
        public const uint PackageId = 0x7f;
        public const byte StyleTypeId = 0x0b;
        public const int ThemeEntry = 0x17e;
        public const uint ThemeId = 0x7f0b017e;
        public const string ThemeName = "Theme.acp.notitlebar.fullscreen";

        /// <summary>The shipped configurations of the theme: qualifier and parent (aapt2 dump resources of 1.17.32).</summary>
        public static readonly IReadOnlyList<(string Config, uint Parent)> ShippedConfigs = new[]
        {
            ("()", 0x01030007u),    // @android:style/Theme.NoTitleBar.Fullscreen
            ("v11", 0x0103006du),   // Theme.Holo.NoActionBar.Fullscreen
            ("v21", 0x01030242u),   // Theme.Material.Light.NoActionBar.Fullscreen
        };

        /// <summary>The API level of the new configuration: the windowSplashScreen* attributes exist from Android 12.</summary>
        public const ushort SplashSdk = 31;

        // Attribute ids (AOSP core/res/res/values/public-final.xml; android.jar 34 agrees).
        public const uint AttrWindowBackground = 0x01010054;
        public const uint AttrWindowSplashScreenBackground = 0x0101062c;
        public const uint AttrWindowSplashScreenIconBackgroundColor = 0x01010630;
        public const uint AttrWindowSplashScreenBehavior = 0x01010668;   // API 33; ignored by 31/32

        public const byte TypeIntDec = 0x10;
        public const byte TypeIntColorArgb8 = 0x1c;

        /// <summary>windowSplashScreenBehavior enum value icon_preferred.</summary>
        public const uint IconPreferred = 1;

        /// <summary>
        /// Inserts the v31 (and night-v31) configurations of the theme into <paramref name="table"/>. Returns the report line.
        /// Throws <see cref="InvalidDataException"/> when the table does not have the shape the edit was verified on.
        /// </summary>
        public static string Apply(ArscTable table, ArtSettings.SplashSettings splash)
        {
            ArscPackage package = table.Package(PackageId);
            if (package.TypeName(StyleTypeId) != "style")
                throw new InvalidDataException("resources.arsc: type 0x" + StyleTypeId.ToString("x2") + " is " + (package.TypeName(StyleTypeId) ?? "missing") + ", not style");
            ArscTypeSpec spec = package.TypeSpec(StyleTypeId) ?? throw new InvalidDataException("resources.arsc: no style typeSpec");
            if (spec.EntryCount <= ThemeEntry) throw new InvalidDataException("resources.arsc: the style type has only " + spec.EntryCount + " entries");

            List<int> indices = package.TypeIndices(StyleTypeId);
            var shipped = new List<(byte[] Config, ArscEntry Entry)>();
            var configs = new List<byte[]>();
            foreach (int i in indices)
            {
                var type = new ArscType(package.Children[i].Bytes);
                configs.Add(type.Config);
                ArscEntry entry = type.Entry(ThemeEntry);
                if (entry == null) continue;
                string name = package.KeyStrings.Get((int)entry.Key);
                if (name != ThemeName) throw new InvalidDataException("resources.arsc: style 0x" + ThemeId.ToString("x8") + " is " + name + ", not " + ThemeName);
                if (!entry.IsComplex) throw new InvalidDataException("resources.arsc: " + ThemeName + " is not a style (map) entry");
                shipped.Add((type.Config, entry));
            }
            if (shipped.Count == 0) throw new InvalidDataException("resources.arsc: " + ThemeName + " has no configuration");
            foreach (var (config, _) in shipped)
            {
                if (!ArscConfig.OnlyDensitySdkUiMode(config) || ArscConfig.Density(config) != 0 || ArscConfig.UiMode(config) != 0 || ArscConfig.SdkVersion(config) >= SplashSdk)
                {
                    throw new InvalidDataException("resources.arsc: " + ThemeName + " already has the configuration " + ArscConfig.Describe(config) +
                                                   " (only plain API-level configurations below v" + SplashSdk + " are expected: patch the original APK)");
                }
            }

            // The highest shipped configuration is what an API 31+ device uses today: keep its parent and items.
            (byte[] baseConfig, ArscEntry baseEntry) = shipped.OrderBy(s => ArscConfig.SdkVersion(s.Config)).Last();
            var light = Items(baseEntry, splash.Background, splash.IconBackground, splash);
            var newChunks = new List<(string Name, byte[] Config, List<(uint, byte, uint)> Items)>
            {
                ("v" + SplashSdk, ArscConfig.Create(baseConfig.Length, SplashSdk), light),
            };
            if (splash.HasNight)
            {
                var night = Items(baseEntry, splash.BackgroundNight ?? splash.Background, splash.IconBackgroundNight ?? splash.IconBackground, splash);
                newChunks.Add(("night-v" + SplashSdk, ArscConfig.Create(baseConfig.Length, SplashSdk, uiMode: ArscConfig.UiModeNightYes), night));
            }
            foreach (var chunk in newChunks)
            {
                if (configs.Any(c => c.AsSpan().SequenceEqual(chunk.Config)))
                    throw new InvalidDataException("resources.arsc: the style type already has a " + chunk.Name + " configuration (patch the original APK)");
            }

            int insertAt = indices.Last() + 1;
            foreach (var chunk in newChunks)
            {
                var entries = new SortedDictionary<int, byte[]> { [ThemeEntry] = ArscEntry.Map(baseEntry.Flags, baseEntry.Key, baseEntry.Parent, chunk.Items) };
                package.Children.Insert(insertAt++, new ArscChunk(ArscType.Build(StyleTypeId, chunk.Config, spec.EntryCount, entries)));
            }
            // The entry now also varies by night mode: aapt2 records that in the typeSpec's configuration-change flags.
            uint changes = ArscTypeSpec.ConfigVersion | (splash.HasNight ? ArscTypeSpec.ConfigUiMode : 0);
            int specIndex = package.TypeSpecIndex(StyleTypeId);
            package.Children[specIndex] = new ArscChunk(spec.With(ThemeEntry, changes, newChunks.Count));

            return "resources.arsc: style 0x" + ThemeId.ToString("x8") + " " + ThemeName + " (XamarinMainActivity's theme) +" +
                   string.Join(" +", newChunks.Select(c => c.Name + " (" + Describe(c.Items) + ")")) +
                   ", parent 0x" + baseEntry.Parent.ToString("x8") + " as in " + ArscConfig.Describe(baseConfig) + "; the Android 12+ launch screen";
        }

        /// <summary>The base configuration's items with ours set, sorted by attribute id as aapt2 writes them.</summary>
        private static List<(uint, byte, uint)> Items(ArscEntry baseEntry, uint background, uint? iconBackground, ArtSettings.SplashSettings splash)
        {
            var items = new SortedDictionary<uint, (byte, uint)>();
            foreach (var (name, type, data) in baseEntry.Items) items[name] = (type, data);
            items[AttrWindowSplashScreenBackground] = (TypeIntColorArgb8, 0xFF000000 | background);
            if (iconBackground.HasValue) items[AttrWindowSplashScreenIconBackgroundColor] = (TypeIntColorArgb8, 0xFF000000 | iconBackground.Value);
            if (splash.IconPreferred) items[AttrWindowSplashScreenBehavior] = (TypeIntDec, IconPreferred);
            if (splash.WindowBackground) items[AttrWindowBackground] = (TypeIntColorArgb8, 0xFF000000 | background);
            return items.Select(kv => (kv.Key, kv.Value.Item1, kv.Value.Item2)).ToList();
        }

        private static string Describe(List<(uint Name, byte Type, uint Data)> items)
        {
            return string.Join(", ", items.Select(i =>
            {
                switch (i.Name)
                {
                    case AttrWindowBackground: return "windowBackground #" + (i.Data & 0xFFFFFF).ToString("X6");
                    case AttrWindowSplashScreenBackground: return "windowSplashScreenBackground #" + (i.Data & 0xFFFFFF).ToString("X6");
                    case AttrWindowSplashScreenIconBackgroundColor: return "windowSplashScreenIconBackgroundColor #" + (i.Data & 0xFFFFFF).ToString("X6");
                    case AttrWindowSplashScreenBehavior: return "windowSplashScreenBehavior icon_preferred";
                    default: return "0x" + i.Name.ToString("x8") + " kept";
                }
            }));
        }
    }
}
