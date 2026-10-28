using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace McvsOffline.Embed.Tests.Support
{
    /// <summary>
    /// Writes a small compiled (binary XML) AndroidManifest.xml the way aapt does: string pool (framework attribute names
    /// first, tied to their ids by the resource map), the android namespace, then the elements (each with a line number of
    /// its own, in document order), with optional text (CDATA) and namespace scopes declared on inner elements. Test
    /// fixture only.
    /// </summary>
    internal static class AxmlWriter
    {
        public const string AndroidNs = "http://schemas.android.com/apk/res/android";

        private static readonly Dictionary<string, uint> FrameworkIds = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["name"] = 0x01010003, ["permission"] = 0x01010006, ["protectionLevel"] = 0x01010009, ["enabled"] = 0x0101000e, ["exported"] = 0x01010010,
            ["authorities"] = 0x01010018, ["value"] = 0x01010024, ["minSdkVersion"] = 0x0101020c, ["targetSdkVersion"] = 0x01010270,
            ["maxSdkVersion"] = 0x01010271, ["scheme"] = 0x01010027, ["fitsSystemWindows"] = 0x010100dd,
            ["allowNativeHeapPointerTagging"] = 0x01010612, ["layout_width"] = 0x010100f4, ["layout_height"] = 0x010100f5, ["id"] = 0x010100d0,
            ["appCategory"] = 0x01010545, ["enableOnBackInvokedCallback"] = 0x0101066c,
        };

        public sealed class Element
        {
            public Element(string name, params (string Ns, string Name, object Value)[] attributes)
            {
                Name = name;
                Attributes = attributes.ToList();
            }

            public string Name { get; }

            public List<(string Ns, string Name, object Value)> Attributes { get; }

            public List<Element> Children { get; } = new List<Element>();

            /// <summary>Line number of its nodes; null: the next one in document order (each element gets its own).</summary>
            public int? Line { get; set; }

            /// <summary>Text written as a CDATA node right after the start element, or null.</summary>
            public string Text { get; set; }

            /// <summary>Namespace scopes (prefix, uri) declared on this element: START_NAMESPACE before it, END_NAMESPACE after.</summary>
            public List<(string Prefix, string Uri)> Namespaces { get; } = new List<(string Prefix, string Uri)>();

            public Element Add(Element child)
            {
                Children.Add(child);
                return this;
            }

            /// <summary>This element and every descendant, in document order.</summary>
            public IEnumerable<Element> Descendants()
            {
                yield return this;
                foreach (Element c in Children)
                    foreach (Element d in c.Descendants()) yield return d;
            }
        }

        /// <summary>A manifest with the 1.17.32 permission shapes: package-scoped C2D_MESSAGE, vendor-wide glshare.</summary>
        public static Element GameLikeManifest(string package = "com.gameloft.android.ANMP.GloftMVHM", string vendor = "glshare.permission.ACCESS_SHARED_DATA")
        {
            var app = new Element("application", (AndroidNs, "name", "com.gameloft.android.FrameworkApplication"));
            app.Add(new Element("provider", (AndroidNs, "name", package + ".KeyProvider"), (AndroidNs, "permission", vendor),
                                (AndroidNs, "exported", true), (AndroidNs, "authorities", package + ".KeyProvider")));
            app.Add(new Element("meta-data", (AndroidNs, "name", "io.fabric.ApiKey"), (AndroidNs, "value", "0123")));
            return new Element("manifest", (null, "package", package))
                .Add(new Element("permission", (AndroidNs, "name", package + ".permission.C2D_MESSAGE"), (AndroidNs, "protectionLevel", 2)))
                .Add(new Element("uses-permission", (AndroidNs, "name", package + ".permission.C2D_MESSAGE")))
                .Add(new Element("permission", (AndroidNs, "name", vendor), (AndroidNs, "protectionLevel", 2)))
                .Add(new Element("uses-permission", (AndroidNs, "name", vendor)))
                .Add(new Element("uses-permission", (AndroidNs, "name", "android.permission.INTERNET")))
                .Add(app);
        }

        public static byte[] Write(Element root, bool utf8 = false)
        {
            // Pool order: framework attribute names (resource map), then everything else in first-use order.
            var all = new List<Element>();
            Collect(root, all);
            var mapped = all.SelectMany(e => e.Attributes).Where(a => a.Ns == AndroidNs).Select(a => a.Name).Distinct().ToList();
            foreach (string name in mapped)
                if (!FrameworkIds.ContainsKey(name)) throw new ArgumentException("no framework id known for android:" + name);
            var pool = new List<string>(mapped);
            void Use(string s)
            {
                if (s != null && !pool.Contains(s)) pool.Add(s);
            }
            Use("android");
            Use(AndroidNs);
            foreach (Element e in all)
            {
                foreach (var ns in e.Namespaces)
                {
                    Use(ns.Prefix);
                    Use(ns.Uri);
                }
                Use(e.Name);
                foreach (var a in e.Attributes)
                {
                    Use(a.Ns);
                    Use(a.Name);
                    if (a.Value is string s) Use(s);
                }
                Use(e.Text);
            }

            var body = new MemoryStream();
            WriteStringPool(body, pool, utf8);
            Chunk(body, 0x0180, 8, w => { foreach (string name in mapped) w.Write(FrameworkIds[name]); });
            Chunk(body, 0x0100, 16, w => { w.Write(1); w.Write(-1); w.Write(pool.IndexOf("android")); w.Write(pool.IndexOf(AndroidNs)); });
            int line = 1;
            WriteElement(body, root, pool, ref line);
            Chunk(body, 0x0101, 16, w => { w.Write(1); w.Write(-1); w.Write(pool.IndexOf("android")); w.Write(pool.IndexOf(AndroidNs)); });

            var file = new MemoryStream();
            var writer = new BinaryWriter(file);
            writer.Write((ushort)0x0003);
            writer.Write((ushort)8);
            writer.Write((uint)(8 + body.Length));
            body.WriteTo(file);
            return file.ToArray();
        }

        private static void Collect(Element e, List<Element> all)
        {
            all.Add(e);
            foreach (Element c in e.Children) Collect(c, all);
        }

        private static void WriteElement(Stream output, Element e, List<string> pool, ref int nextLine)
        {
            int line = e.Line ?? nextLine++;
            foreach (var ns in e.Namespaces)
                Chunk(output, 0x0100, 16, w => { w.Write(line); w.Write(-1); w.Write(pool.IndexOf(ns.Prefix)); w.Write(pool.IndexOf(ns.Uri)); });
            Chunk(output, 0x0102, 16, w =>
            {
                w.Write(line);   // line
                w.Write(-1);     // comment
                w.Write(-1);     // ns
                w.Write(pool.IndexOf(e.Name));
                w.Write((ushort)20);
                w.Write((ushort)20);
                w.Write((ushort)e.Attributes.Count);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write((ushort)0);
                foreach (var a in e.Attributes)
                {
                    w.Write(a.Ns == null ? -1 : pool.IndexOf(a.Ns));
                    w.Write(pool.IndexOf(a.Name));
                    byte type;
                    int data;
                    switch (a.Value)
                    {
                        case string s:
                            type = 0x03;
                            data = pool.IndexOf(s);
                            break;
                        case bool b:
                            type = 0x12;
                            data = b ? -1 : 0;
                            break;
                        case int i:
                            type = 0x11;
                            data = i;
                            break;
                        default:
                            throw new ArgumentException("unsupported attribute value " + a.Value);
                    }
                    w.Write(type == 0x03 ? data : -1); // raw value
                    w.Write((ushort)8);
                    w.Write((byte)0);
                    w.Write(type);
                    w.Write(data);
                }
            });
            if (e.Text != null)
            {
                // ResXMLTree_cdataExt: the text, and an empty typed value (as aapt2 writes it).
                Chunk(output, 0x0104, 16, w => { w.Write(line); w.Write(-1); w.Write(pool.IndexOf(e.Text)); w.Write((ushort)8); w.Write((byte)0); w.Write((byte)0); w.Write(0); });
            }
            foreach (Element c in e.Children) WriteElement(output, c, pool, ref nextLine);
            Chunk(output, 0x0103, 16, w => { w.Write(line); w.Write(-1); w.Write(-1); w.Write(pool.IndexOf(e.Name)); });
            for (int i = e.Namespaces.Count - 1; i >= 0; i--)
            {
                var ns = e.Namespaces[i];
                Chunk(output, 0x0101, 16, w => { w.Write(line); w.Write(-1); w.Write(pool.IndexOf(ns.Prefix)); w.Write(pool.IndexOf(ns.Uri)); });
            }
        }

        private static void WriteStringPool(Stream output, List<string> strings, bool utf8)
        {
            var data = new MemoryStream();
            var offsets = new List<int>();
            foreach (string s in strings)
            {
                offsets.Add((int)data.Length);
                if (utf8)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(s);
                    data.WriteByte((byte)s.Length);
                    data.WriteByte((byte)bytes.Length);
                    data.Write(bytes, 0, bytes.Length);
                    data.WriteByte(0);
                }
                else
                {
                    data.WriteByte((byte)s.Length);
                    data.WriteByte((byte)(s.Length >> 8));
                    byte[] chars = Encoding.Unicode.GetBytes(s);
                    data.Write(chars, 0, chars.Length);
                    data.WriteByte(0);
                    data.WriteByte(0);
                }
            }
            while (data.Length % 4 != 0) data.WriteByte(0);
            Chunk(output, 0x0001, 28, w =>
            {
                w.Write(strings.Count);
                w.Write(0);                          // styles
                w.Write(utf8 ? 0x100 : 0);           // flags
                w.Write(28 + 4 * strings.Count);     // stringsStart
                w.Write(0);                          // stylesStart
                foreach (int o in offsets) w.Write(o);
                data.WriteTo(w.BaseStream);
            });
        }

        /// <summary>
        /// A chunk: type, header size, total size, then the body. For XML nodes the 16-byte header also holds the line
        /// number and comment, which their body writes first.
        /// </summary>
        private static void Chunk(Stream output, ushort type, ushort headerSize, Action<BinaryWriter> body)
        {
            var content = new MemoryStream();
            body(new BinaryWriter(content));
            var w = new BinaryWriter(output);
            w.Write(type);
            w.Write(headerSize);
            w.Write((uint)(8 + content.Length));
            content.WriteTo(output);
        }
    }
}
