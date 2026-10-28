using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using McvsOffline.Patcher.Art;
using McvsOffline.Patcher.Resources;
using McvsOffline.Patcher.Tools;
using Xunit;

namespace McvsOffline.Embed.Tests.Support
{
    /// <summary>Art held in memory (synthetic PNGs drawn by the tests; never Gameloft's icons, risk R6).</summary>
    internal sealed class MemoryArtSource : ArtSource
    {
        public Dictionary<string, byte[]> Content { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        public override string Description { get { return "test art"; } }

        public override IReadOnlyList<string> Files { get { return Content.Keys.Where(k => !IsIgnored(k)).OrderBy(k => k, StringComparer.Ordinal).ToList(); } }

        public override byte[] Read(string path)
        {
            return Content[path];
        }
    }

    internal static class ArtFixtures
    {
        /// <summary>
        /// The patcher's "--art auto" sees no built-in art in this test assembly (ArtPatch.BuiltInForTests): the tests that
        /// run the whole patcher keep their expectations when the owner adds art to art/android/. Tests of the built-in path
        /// pass their own source, and EmbeddedArtSourceMatchesTheRepositoryFolder checks the real embedding.
        /// </summary>
        [ModuleInitializer]
        internal static void NoBuiltInArt()
        {
            ArtPatch.BuiltInForTests = new MemoryArtSource();
        }

        /// <summary>A transparent square with an opaque disc of <paramref name="radiusShare"/> of the size in the middle.</summary>
        public static Png Disc(int size, uint rgb, double radiusShare, byte alphaOutside = 0)
        {
            var pixels = new byte[size * size * 4];
            double c = size / 2.0, r = size * radiusShare;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size + x) * 4;
                    double dx = x + 0.5 - c, dy = y + 0.5 - c;
                    bool inside = dx * dx + dy * dy <= r * r;
                    pixels[i] = (byte)(rgb >> 16);
                    pixels[i + 1] = (byte)(rgb >> 8);
                    pixels[i + 2] = (byte)rgb;
                    pixels[i + 3] = inside ? (byte)0xFF : alphaOutside;
                }
            }
            return new Png(size, size, pixels);
        }

        /// <summary>A diagonal gradient (opaque), so resampled exports differ per density.</summary>
        public static Png Gradient(int size)
        {
            var pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = (y * size + x) * 4;
                    pixels[i] = (byte)(255 * x / size);
                    pixels[i + 1] = (byte)(255 * y / size);
                    pixels[i + 2] = 0x40;
                    pixels[i + 3] = 0xFF;
                }
            }
            return new Png(size, size, pixels);
        }

        /// <summary>A complete icon set from masters: 432 px foreground (a disc inside the safe zone), 864 px background, 192 px legacy.</summary>
        public static MemoryArtSource IconMasters(bool monochrome = false, string artJson = null)
        {
            var art = new MemoryArtSource();
            art.Content["icon/foreground.png"] = Disc(432, 0xF0C040, 0.25).Encode();
            art.Content["icon/background.png"] = Gradient(864).Encode();
            art.Content["icon/legacy.png"] = Disc(192, 0x2060A0, 0.45).Encode();
            if (monochrome) art.Content["icon/monochrome.png"] = Disc(432, 0xFFFFFF, 0.25).Encode();
            if (artJson != null) art.Content["art.json"] = Encoding.UTF8.GetBytes(artJson);
            art.Content["README.md"] = Encoding.UTF8.GetBytes("ignored");
            return art;
        }

        public static string WriteDirectory(MemoryArtSource art, string dir)
        {
            foreach (var file in art.Content)
            {
                string path = Path.Combine(dir, file.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, file.Value);
            }
            return dir;
        }
    }

    /// <summary>
    /// A small resources.arsc with the shape of the 1.17.32 table where the art patch edits it: package 0x7f, type 3 "mipmap"
    /// (the six icon slots in the default, ldpi..xxxhdpi-v4 and anydpi-v26 configurations), type 0x0b "style" with
    /// Theme.acp.notitlebar.fullscreen at 0x17e in (), v11 and v21 plus another theme in () and night-v8, and a type 0x0c
    /// typeSpec after it (the insertion must stay before it).
    /// </summary>
    internal static class SyntheticArsc
    {
        public static readonly string[] TypeNames = { "attr", "drawable", "mipmap", "layout", "anim", "raw", "string", "color", "bool", "integer", "style", "dimen" };

        public static readonly string[] Keys =
        {
            "ic_ad_choices", "ic_launcher", "ic_launcher_background", "ic_launcher_foreground", "ic_launcher_foreground1", "iconiab",
            "Theme.acp.notitlebar.fullscreen", "Theme.Other", "spacing",
        };

        public const int OtherTheme = 0x17f;

        public static byte[] Build(bool utf8Global = true)
        {
            var strings = new List<string>();
            int Str(string s)
            {
                int i = strings.IndexOf(s);
                if (i >= 0) return i;
                strings.Add(s);
                return strings.Count - 1;
            }
            uint Key(string k)
            {
                return (uint)Array.IndexOf(Keys, k);
            }
            const byte TypeString = 0x03;

            var children = new List<byte[]>();
            children.Add(Pool(TypeNames, true));
            children.Add(Pool(Keys, true));

            // mipmap (3): 6 entries
            children.Add(TypeSpec(3, new uint[] { 0x100, 0x500, 0x500, 0x100, 0, 0 }));
            var mipDefault = new SortedDictionary<int, byte[]>
            {
                [1] = ArscEntry.Simple(Key("ic_launcher"), TypeString, (uint)Str("res/mipmap/ic_launcher.png")),
                [2] = ArscEntry.Simple(Key("ic_launcher_background"), TypeString, (uint)Str("res/mipmap/ic_launcher_background.png")),
                [4] = ArscEntry.Simple(Key("ic_launcher_foreground1"), TypeString, (uint)Str("res/mipmap/ic_launcher_foreground1.png")),
            };
            children.Add(ArscType.Build(3, ArscConfig.Create(64, 0), 6, mipDefault));
            foreach (IconDensity d in IconDensity.All)
            {
                string folder = "res/mipmap-" + d.Name + "-v4/";
                var entries = new SortedDictionary<int, byte[]>();
                if (d != IconDensity.Ldpi) entries[0] = ArscEntry.Simple(Key("ic_ad_choices"), TypeString, (uint)Str(folder + "ic_ad_choices.png"));
                entries[1] = ArscEntry.Simple(Key("ic_launcher"), TypeString, (uint)Str(folder + "ic_launcher.png"));
                entries[2] = ArscEntry.Simple(Key("ic_launcher_background"), TypeString, (uint)Str(folder + "ic_launcher_background.png"));
                entries[3] = ArscEntry.Simple(Key("ic_launcher_foreground"), TypeString, (uint)Str(folder + "ic_launcher_foreground.png"));
                if (d == IconDensity.Hdpi) entries[5] = ArscEntry.Simple(Key("iconiab"), TypeString, (uint)Str(folder + "iconiab.png"));
                children.Add(ArscType.Build(3, ArscConfig.Create(64, 4, d.Dpi), 6, entries));
            }
            children.Add(ArscType.Build(3, ArscConfig.Create(64, 26, 0xFFFE), 6, new SortedDictionary<int, byte[]>
            {
                [1] = ArscEntry.Simple(Key("ic_launcher"), TypeString, (uint)Str("res/mipmap-anydpi-v26/ic_launcher.xml")),
            }));

            // style (0x0b): 0x180 entries
            var styleFlags = new uint[0x180];
            styleFlags[SplashTheme.ThemeEntry] = 0x400;
            styleFlags[OtherTheme] = 0x1000;
            children.Add(TypeSpec(0x0b, styleFlags));
            var noItems = new List<(uint, byte, uint)>();
            uint theme = Key(SplashTheme.ThemeName), other = Key("Theme.Other");
            children.Add(ArscType.Build(0x0b, ArscConfig.Create(64, 0), 0x180, new SortedDictionary<int, byte[]>
            {
                [SplashTheme.ThemeEntry] = ArscEntry.Map(1, theme, 0x01030007, noItems),
                [OtherTheme] = ArscEntry.Map(1, other, 0x0103001c, new List<(uint, byte, uint)> { (0x01010048, 0x01, 0x7f0b0001) }),
            }));
            children.Add(ArscType.Build(0x0b, ArscConfig.Create(64, 8, uiMode: 0x20), 0x180, new SortedDictionary<int, byte[]>
            {
                [OtherTheme] = ArscEntry.Map(1, other, 0x0103001c, noItems),
            }));
            children.Add(ArscType.Build(0x0b, ArscConfig.Create(64, 11), 0x180, new SortedDictionary<int, byte[]> { [SplashTheme.ThemeEntry] = ArscEntry.Map(1, theme, 0x0103006d, noItems) }));
            children.Add(ArscType.Build(0x0b, ArscConfig.Create(64, 21), 0x180, new SortedDictionary<int, byte[]> { [SplashTheme.ThemeEntry] = ArscEntry.Map(1, theme, 0x01030242, noItems) }));

            // dimen (0x0c): one entry
            children.Add(TypeSpec(0x0c, new uint[] { 0 }));
            children.Add(ArscType.Build(0x0c, ArscConfig.Create(64, 0), 1, new SortedDictionary<int, byte[]> { [0] = ArscEntry.Simple(Key("spacing"), 0x05, 0x00000101) }));

            byte[] package = Package(0x7f, "com.gameloft.android.ANMP.GloftMVHM", children);
            byte[] global = Pool(strings, utf8Global);
            var table = new MemoryStream();
            var w = new BinaryWriter(table);
            w.Write((ushort)ArscTable.TableType); w.Write((ushort)12); w.Write((uint)(12 + global.Length + package.Length)); w.Write(1u);
            w.Write(global);
            w.Write(package);
            return table.ToArray();
        }

        public static byte[] TypeSpec(byte id, uint[] flags)
        {
            var bytes = new byte[16 + 4 * flags.Length];
            ArscTable.Put16(bytes, 0, ArscTable.TypeSpecType);
            ArscTable.Put16(bytes, 2, 16);
            ArscTable.Put32(bytes, 4, (uint)bytes.Length);
            bytes[8] = id;
            ArscTable.Put32(bytes, 12, (uint)flags.Length);
            for (int i = 0; i < flags.Length; i++) ArscTable.Put32(bytes, 16 + 4 * i, flags[i]);
            return bytes;
        }

        public static byte[] Pool(IList<string> strings, bool utf8)
        {
            var data = new MemoryStream();
            var offsets = new List<uint>();
            foreach (string s in strings)
            {
                offsets.Add((uint)data.Length);
                if (utf8)
                {
                    byte[] b = Encoding.UTF8.GetBytes(s);
                    data.WriteByte((byte)s.Length);
                    data.WriteByte((byte)b.Length);
                    data.Write(b, 0, b.Length);
                    data.WriteByte(0);
                }
                else
                {
                    data.WriteByte((byte)s.Length);
                    data.WriteByte(0);
                    byte[] b = Encoding.Unicode.GetBytes(s);
                    data.Write(b, 0, b.Length);
                    data.WriteByte(0);
                    data.WriteByte(0);
                }
            }
            while (data.Length % 4 != 0) data.WriteByte(0);
            int start = 28 + 4 * strings.Count;
            var pool = new MemoryStream();
            var w = new BinaryWriter(pool);
            w.Write((ushort)ArscTable.StringPoolType); w.Write((ushort)28); w.Write((uint)(start + data.Length));
            w.Write((uint)strings.Count); w.Write(0u); w.Write(utf8 ? StringPool.Utf8Flag : 0u); w.Write((uint)start); w.Write(0u);
            foreach (uint o in offsets) w.Write(o);
            w.Write(data.ToArray());
            return pool.ToArray();
        }

        private static byte[] Package(uint id, string name, List<byte[]> children)
        {
            var header = new byte[288];
            ArscTable.Put16(header, 0, ArscTable.PackageType);
            ArscTable.Put16(header, 2, 288);
            ArscTable.Put32(header, 8, id);
            byte[] n = Encoding.Unicode.GetBytes(name);
            Array.Copy(n, 0, header, 12, n.Length);
            ArscTable.Put32(header, 268, 288);                                     // typeStrings
            ArscTable.Put32(header, 272, (uint)TypeNames.Length);                  // lastPublicType
            ArscTable.Put32(header, 276, (uint)(288 + children[0].Length));        // keyStrings
            ArscTable.Put32(header, 280, (uint)Keys.Length);                       // lastPublicKey
            var output = new MemoryStream();
            output.Write(header, 0, header.Length);
            foreach (byte[] c in children) output.Write(c, 0, c.Length);
            byte[] result = output.ToArray();
            ArscTable.Put32(result, 4, (uint)result.Length);
            return result;
        }
    }

    /// <summary>aapt2's resource table dump, and what an edit added to it.</summary>
    internal static class ArtAapt2
    {
        /// <summary><c>aapt2 dump resources</c>, without "warn:" lines.</summary>
        public static string Resources(string apk)
        {
            int code = BuildTools.Run(TestEnvironment.Aapt2, new[] { "dump", "resources", apk }, null, out string text);
            Assert.True(code == 0, "aapt2 dump resources failed (exit " + code + "): " + text);
            return string.Join("\n", text.Replace("\r", "").Split('\n').Where(l => !l.StartsWith("warn: ", StringComparison.Ordinal)));
        }

        /// <summary>
        /// The lines of <paramref name="after"/> that are not in <paramref name="before"/> (trimmed), for an edit that only
        /// inserts: every line of <paramref name="before"/> must still be there, in order.
        /// </summary>
        public static List<string> AddedLines(string before, string after)
        {
            string[] a = before.Split('\n'), b = after.Split('\n');
            var added = new List<string>();
            int i = 0;
            foreach (string line in b)
            {
                if (i < a.Length && line == a[i]) i++;
                else added.Add(line.Trim());
            }
            Assert.True(i == a.Length, "a line of the original dump is gone or changed: " + (i < a.Length ? a[i] : ""));
            return added;
        }
    }
}
