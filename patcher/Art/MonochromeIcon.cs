using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Patcher.Resources;

namespace McvsOffline.Patcher.Art
{
    /// <summary>
    /// The themed (monochrome) launcher icon, Android 13+ (app-icon-and-launch-screen.md WP-4), used only when the art has a
    /// monochrome layer. The adaptive icon gets a third element, <c>&lt;monochrome android:drawable="@mipmap/ic_launcher_foreground1"/&gt;</c>.
    /// That resource (0x7f030004) is a spare slot of the shipped table: one default-config PNG that nothing references
    /// (no Java reference, only a constant in Resource.cs). It gets the six density configurations the other layers have:
    /// each density's mipmap type chunk gains entry 4 (a file path string appended to the global pool), and the typeSpec
    /// marks the entry as density-dependent. No resource id moves, no entry count changes.
    /// </summary>
    public static class MonochromeIcon
    {
        public const byte MipmapTypeId = 0x03;
        public const int SpareEntry = 4;
        public const uint SpareId = 0x7f030004;
        public const string SpareName = "ic_launcher_foreground1";

        /// <summary>The spare slot's only shipped file (72x72 RGB, a copy of Gameloft's art), replaced by the mdpi monochrome export.</summary>
        public const string DefaultFile = "res/mipmap/ic_launcher_foreground1.png";

        public const int DefaultFileBytes = 12154;
        public const uint DefaultFileCrc = 0x09fbe797;

        /// <summary>The adaptive icon XML (binary XML, DEFLATED, 552 bytes in 1.17.32).</summary>
        public const string AdaptiveIconEntry = "res/mipmap-anydpi-v26/ic_launcher.xml";

        public const int AdaptiveIconBytes = 552;
        public const uint AdaptiveIconCrc = 0xb49f237e;

        public const uint BackgroundId = 0x7f030002, ForegroundId = 0x7f030003;

        public const byte TypeString = 0x03;

        public static string DensityFile(IconDensity density)
        {
            return "res/mipmap-" + density.Name + "-v4/" + SpareName + ".png";
        }

        /// <summary>The density files are added to the zip right after this entry (the same density's foreground).</summary>
        public static string AnchorEntry(IconDensity density)
        {
            return "res/mipmap-" + density.Name + "-v4/ic_launcher_foreground.png";
        }

        /// <summary>The shipped adaptive icon (background, foreground) or the themed one (plus monochrome).</summary>
        public static byte[] AdaptiveIcon(bool monochrome)
        {
            var layers = new List<(string, uint)> { ("background", BackgroundId), ("foreground", ForegroundId) };
            if (monochrome) layers.Add(("monochrome", SpareId));
            return AdaptiveIconXml.Write(layers);
        }

        /// <summary>
        /// Gives entry 0x7f030004 its six density configurations in <paramref name="table"/>. Returns the report line; throws
        /// <see cref="InvalidDataException"/> when the mipmap type does not have the verified shape.
        /// </summary>
        public static string Apply(ArscTable table)
        {
            ArscPackage package = table.Package(SplashTheme.PackageId);
            if (package.TypeName(MipmapTypeId) != "mipmap") throw new InvalidDataException("resources.arsc: type 0x03 is not mipmap");
            ArscTypeSpec spec = package.TypeSpec(MipmapTypeId);
            if (spec == null || spec.EntryCount <= SpareEntry) throw new InvalidDataException("resources.arsc: the mipmap type has no entry " + SpareEntry);
            List<int> indices = package.TypeIndices(MipmapTypeId);

            ArscEntry spare = null;
            foreach (int i in indices)
            {
                var type = new ArscType(package.Children[i].Bytes);
                ArscEntry entry = type.Entry(SpareEntry);
                if (entry == null) continue;
                bool isDefault = ArscConfig.Describe(type.Config) == "()";
                if (!isDefault || spare != null)
                    throw new InvalidDataException("resources.arsc: mipmap/" + SpareName + " already has the configuration " + ArscConfig.Describe(type.Config) + " (patch the original APK)");
                spare = entry;
            }
            if (spare == null) throw new InvalidDataException("resources.arsc: mipmap/" + SpareName + " has no default configuration");
            string name = package.KeyStrings.Get((int)spare.Key);
            if (name != SpareName || spare.IsComplex || spare.Value.DataType != TypeString || table.Strings.Get((int)spare.Value.Data) != DefaultFile)
                throw new InvalidDataException("resources.arsc: entry 0x" + SpareId.ToString("x8") + " is not " + SpareName + " -> " + DefaultFile);

            foreach (IconDensity density in IconDensity.All)
            {
                int index = indices.Where(i => IsDensityConfig(new ArscType(package.Children[i].Bytes).Config, density)).DefaultIfEmpty(-1).Single();
                if (index < 0) throw new InvalidDataException("resources.arsc: no mipmap " + density + "-v4 configuration");
                var type = new ArscType(package.Children[index].Bytes);
                SortedDictionary<int, byte[]> entries = type.Entries();
                // Rebuilding the unchanged chunk must give its exact bytes, or the layout is not aapt's and is left alone.
                if (!ArscType.Build(type.Id, type.Config, type.EntryCount, entries).AsSpan().SequenceEqual(type.Bytes))
                    throw new InvalidDataException("resources.arsc: the mipmap " + density + "-v4 chunk is not laid out entry by entry; not editing it");
                string path = DensityFile(density);
                if (table.Strings.IndexOf(path) >= 0) throw new InvalidDataException("resources.arsc already names " + path + " (patch the original APK)");
                int pathIndex = table.Strings.Append(path);
                byte[] entry = ArscEntry.Simple(spare.Key, TypeString, (uint)pathIndex);
                ArscTable.Put16(entry, 2, spare.Flags);
                entries[SpareEntry] = entry;
                package.Children[index] = new ArscChunk(ArscType.Build(type.Id, type.Config, type.EntryCount, entries));
            }
            package.Children[package.TypeSpecIndex(MipmapTypeId)] = new ArscChunk(spec.With(SpareEntry, ArscTypeSpec.ConfigDensity, 0));
            return "resources.arsc: mipmap 0x" + SpareId.ToString("x8") + " " + SpareName + " +ldpi..xxxhdpi-v4 (" + IconDensity.All.Count +
                   " file paths appended to the string pool; the themed icon's layer)";
        }

        private static bool IsDensityConfig(byte[] config, IconDensity density)
        {
            return ArscConfig.OnlyDensitySdkUiMode(config) && ArscConfig.Density(config) == density.Dpi && ArscConfig.SdkVersion(config) == 4 && ArscConfig.UiMode(config) == 0;
        }
    }

    /// <summary>
    /// Writes the binary XML of an adaptive icon the way the 1.17.32 build tools did (UTF-16 pool "drawable, android, &lt;ns&gt;,
    /// "", adaptive-icon, &lt;layers&gt;", one resource-map entry for android:drawable, every node on line 1), so that writing
    /// the shipped two layers reproduces the shipped file byte for byte, which the patch checks before writing three.
    /// </summary>
    public static class AdaptiveIconXml
    {
        public const string AndroidNamespace = "http://schemas.android.com/apk/res/android";
        public const uint DrawableAttribute = 0x01010199;

        private const uint None = 0xFFFFFFFF;

        public static byte[] Write(IList<(string Element, uint Drawable)> layers)
        {
            var strings = new List<string> { "drawable", "android", AndroidNamespace, "", "adaptive-icon" };
            strings.AddRange(layers.Select(l => l.Element));
            const uint drawable = 0, prefix = 1, uri = 2, root = 4;

            var body = new MemoryStream();
            var w = new BinaryWriter(body);
            byte[] pool = Pool(strings);
            w.Write(pool);
            w.Write((ushort)0x0180); w.Write((ushort)8); w.Write(12u); w.Write(DrawableAttribute);           // resource map
            Node(w, 0x0100, 24); w.Write(prefix); w.Write(uri);                                                 // <xmlns:android>
            StartElement(w, root, null);
            for (int i = 0; i < layers.Count; i++)
            {
                uint name = (uint)(5 + i);
                StartElement(w, name, (uri, drawable, layers[i].Drawable));
                Node(w, 0x0103, 24); w.Write(None); w.Write(name);
            }
            Node(w, 0x0103, 24); w.Write(None); w.Write(root);
            Node(w, 0x0101, 24); w.Write(prefix); w.Write(uri);

            var output = new MemoryStream();
            var o = new BinaryWriter(output);
            o.Write((ushort)0x0003); o.Write((ushort)8); o.Write((uint)(8 + body.Length));
            o.Write(body.ToArray());
            return output.ToArray();
        }

        private static void Node(BinaryWriter w, ushort type, uint size)
        {
            w.Write(type); w.Write((ushort)16); w.Write(size);
            w.Write(1u);     // line number
            w.Write(None);   // comment
        }

        private static void StartElement(BinaryWriter w, uint name, (uint Ns, uint Name, uint Reference)? attribute)
        {
            Node(w, 0x0102, attribute.HasValue ? 56u : 36u);
            w.Write(None); w.Write(name);
            w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)(attribute.HasValue ? 1 : 0));
            w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);   // id, class, style attribute indices
            if (!attribute.HasValue) return;
            w.Write(attribute.Value.Ns); w.Write(attribute.Value.Name); w.Write(None);
            w.Write((ushort)8); w.Write((byte)0); w.Write((byte)0x01); w.Write(attribute.Value.Reference);   // TYPE_REFERENCE
        }

        private static byte[] Pool(List<string> strings)
        {
            var data = new MemoryStream();
            var offsets = new List<uint>();
            foreach (string s in strings)
            {
                offsets.Add((uint)data.Length);
                data.WriteByte((byte)s.Length);
                data.WriteByte((byte)(s.Length >> 8));
                byte[] utf16 = Encoding.Unicode.GetBytes(s);
                data.Write(utf16, 0, utf16.Length);
                data.WriteByte(0);
                data.WriteByte(0);
            }
            while (data.Length % 4 != 0) data.WriteByte(0);
            int start = 28 + 4 * strings.Count;
            var pool = new MemoryStream();
            var w = new BinaryWriter(pool);
            w.Write((ushort)0x0001); w.Write((ushort)28); w.Write((uint)(start + data.Length));
            w.Write((uint)strings.Count); w.Write(0u); w.Write(0u); w.Write((uint)start); w.Write(0u);
            foreach (uint offset in offsets) w.Write(offset);
            w.Write(data.ToArray());
            return pool.ToArray();
        }
    }
}
