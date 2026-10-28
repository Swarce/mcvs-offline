using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Manifest;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// BinaryXml's additions on synthetic AXML (UTF-16 and UTF-8 pools): an integer value set in place, attributes inserted
    /// (with a name already in the resource map, and with a framework name the pool lacks, which moves every later string
    /// reference), strings appended, element subtrees inserted, alone and together with removals. Every result is compared
    /// with the same tree written directly by <see cref="AxmlWriter"/> (element by element, and as aapt2 prints it when
    /// aapt2 is available), and nothing asked means the input's bytes.
    /// </summary>
    public class BinaryXmlGrowthTests
    {
        private const string Ns = AxmlWriter.AndroidNs;
        private const uint Exported = 0x01010010, Scheme = 0x01010027, TargetSdk = 0x01010270, FitsSystemWindows = 0x010100dd;
        private const uint HeapTagging = 0x01010612;

        private readonly ITestOutputHelper output;

        public BinaryXmlGrowthTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// manifest
        ///   uses-sdk minSdkVersion=21 targetSdkVersion=29
        ///   uses-permission INTERNET
        ///   application                                (declares xmlns:tools on its first child)
        ///     activity app.Main                        (an intent filter, no exported)
        ///       intent-filter / action MAIN, category LAUNCHER
        ///     receiver app.Push exported=false         (text; the pool string "https" is used here only)
        ///       meta-data inner value=https
        ///     service app.Last
        /// </summary>
        private static AxmlWriter.Element Tree(Action<Dictionary<string, AxmlWriter.Element>> change = null)
        {
            var e = new Dictionary<string, AxmlWriter.Element>
            {
                ["uses-sdk"] = new AxmlWriter.Element("uses-sdk", (Ns, "minSdkVersion", 21), (Ns, "targetSdkVersion", 29)),
                ["internet"] = new AxmlWriter.Element("uses-permission", (Ns, "name", "android.permission.INTERNET")),
                ["main"] = new AxmlWriter.Element("activity", (Ns, "name", "app.Main")),
                ["filter"] = new AxmlWriter.Element("intent-filter"),
                ["push"] = new AxmlWriter.Element("receiver", (Ns, "name", "app.Push"), (Ns, "exported", false)) { Text = "some text" },
                ["inner"] = new AxmlWriter.Element("meta-data", (Ns, "name", "inner"), (Ns, "value", "https")),
                ["last"] = new AxmlWriter.Element("service", (Ns, "name", "app.Last")),
                ["application"] = new AxmlWriter.Element("application", (Ns, "name", "app.App")),
            };
            e["main"].Namespaces.Add(("tools", "http://schemas.android.com/tools"));
            e["filter"].Add(new AxmlWriter.Element("action", (Ns, "name", "android.intent.action.MAIN")))
                       .Add(new AxmlWriter.Element("category", (Ns, "name", "android.intent.category.LAUNCHER")));
            e["main"].Add(e["filter"]);
            e["push"].Add(e["inner"]);
            e["application"].Add(e["main"]).Add(e["push"]).Add(e["last"]);
            e["manifest"] = new AxmlWriter.Element("manifest", (null, "package", "app")).Add(e["uses-sdk"]).Add(e["internet"]).Add(e["application"]);
            int line = 1;
            foreach (AxmlWriter.Element x in e["manifest"].Descendants()) x.Line = line++;   // fixed, so a removal moves no line number
            change?.Invoke(e);
            return e["manifest"];
        }

        private static List<string> Keys(byte[] doc)
        {
            return BinaryXml.Parse(doc).Elements().Select(ManifestDebloat.Key).ToList();
        }

        private static BinaryXml.Element Find(BinaryXml xml, string tag, string androidName = null)
        {
            return xml.Elements().Single(x => x.Name == tag && (androidName == null || x.AndroidName == androidName));
        }

        /// <summary>The result reads as <paramref name="expected"/> written directly: element by element, and through aapt2.</summary>
        private void AssertSameTree(AxmlWriter.Element expected, byte[] actual, bool utf8)
        {
            byte[] direct = AxmlWriter.Write(expected, utf8);
            Assert.Equal(Keys(direct), Keys(actual));
            if (TestEnvironment.Aapt2 == null)
            {
                output.WriteLine("aapt2 not found (set MCVS_BUILD_TOOLS): xmltree comparison skipped");
                return;
            }
            string dump = Aapt2Dump.XmlTree(actual);
            Assert.Equal(Aapt2Dump.XmlTree(direct), dump);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NothingAskedOrNothingNewIsByteIdentical(bool utf8)
        {
            byte[] doc = AxmlWriter.Write(Tree(), utf8);
            BinaryXml xml = BinaryXml.Parse(doc);
            xml.AppendString("app.Last");                       // already in the pool, outside the mapped region
            xml.InsertAttributeName("exported", Exported);      // already mapped
            Assert.NotEmpty(xml.Elements());
            Assert.False(xml.Grows);
            Assert.Equal(doc, xml.ToArray());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SetIntChangesOnlyTheValuesData(bool utf8)
        {
            byte[] doc = AxmlWriter.Write(Tree(), utf8);
            BinaryXml xml = BinaryXml.Parse(doc);
            xml.SetInt(Find(xml, "uses-sdk"), Ns, "targetSdkVersion", 35);
            byte[] result = xml.ToArray();

            Assert.Equal(doc.Length, result.Length);
            List<int> differ = Enumerable.Range(0, doc.Length).Where(i => doc[i] != result[i]).ToList();
            Assert.Equal(new[] { 0 }, differ.Select(i => i - differ[0]));   // one byte: 29 -> 35
            BinaryXml.Attribute target = BinaryXml.Parse(result).Elements().Single(e => e.Name == "uses-sdk").Find(Ns, "targetSdkVersion");
            Assert.Equal(35u, target.Data);
            Assert.Equal(0x11, target.DataType);   // the type stays (the writer's TYPE_INT_HEX)
            AssertSameTree(Tree(e => e["uses-sdk"].Attributes[1] = (Ns, "targetSdkVersion", 35)), result, utf8);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnAttributeWithAnAlreadyMappedNameIsTwentyBytesInItsSortedPlace(bool utf8)
        {
            byte[] doc = AxmlWriter.Write(Tree(), utf8);
            BinaryXml xml = BinaryXml.Parse(doc);
            BinaryXml.Element main = Find(xml, "activity", "app.Main");
            xml.InsertAttribute(main, BinaryXml.NewAttribute.Android("exported", Exported, BinaryXml.Value.Boolean(true)));
            byte[] result = xml.ToArray();

            // The pool and the resource map are untouched; only the element's chunk grew by one attribute.
            Assert.Equal(doc.Length + 20, result.Length);
            int pool = BinaryXmlTests.PoolEnd(doc);
            Assert.Equal(doc.Skip(8).Take(pool - 8), result.Skip(8).Take(pool - 8));
            Assert.Equal(BinaryXml.Parse(doc).Strings, BinaryXml.Parse(result).Strings);
            BinaryXml.Element back = Find(BinaryXml.Parse(result), "activity", "app.Main");
            Assert.Equal(new[] { 0x01010003u, Exported }, back.Attributes.Select(a => a.ResourceId));   // name (0x03) < exported (0x10)
            Assert.Equal(0xFFFFFFFFu, back.Find(Ns, "exported").Data);
            AssertSameTree(Tree(e => e["main"].Attributes.Add((Ns, "exported", true))), result, utf8);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AppendedStringsMoveNoIndex(bool utf8)
        {
            byte[] doc = AxmlWriter.Write(Tree(), utf8);
            BinaryXml xml = BinaryXml.Parse(doc);
            xml.AppendString("queries");
            xml.AppendString("android.permission.POST_NOTIFICATIONS");
            xml.AppendString("queries");   // once
            byte[] result = xml.ToArray();

            BinaryXml back = BinaryXml.Parse(result);
            Assert.Equal(xml.Strings.Concat(new[] { "queries", "android.permission.POST_NOTIFICATIONS" }), back.Strings);
            // Every byte after the pool is the input's.
            Assert.Equal(doc.Skip(BinaryXmlTests.PoolEnd(doc)), result.Skip(BinaryXmlTests.PoolEnd(result)));
            AssertSameTree(Tree(), result, utf8);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnInsertedFrameworkNameMovesEveryLaterStringReference(bool utf8)
        {
            byte[] doc = AxmlWriter.Write(Tree(), utf8);
            BinaryXml original = BinaryXml.Parse(doc);
            int mapped = original.ResourceMapCount;
            // Comments of a namespace node, an element and a CDATA node point at strings past the mapped region.
            byte[] commented = (byte[])doc.Clone();
            var chunks = BinaryXmlTests.NodeChunks(commented);
            int https = original.IndexOf("https"), text = original.IndexOf("some text"), last = original.IndexOf("app.Last");
            Assert.True(https > mapped && text > mapped && last > mapped);
            BitConverter.GetBytes(https).CopyTo(commented, chunks.First(c => c.Type == 0x0100).Offset + 12);
            BitConverter.GetBytes(text).CopyTo(commented, chunks.First(c => c.Type == 0x0102).Offset + 12);
            BitConverter.GetBytes(last).CopyTo(commented, chunks.First(c => c.Type == 0x0104).Offset + 12);

            BinaryXml xml = BinaryXml.Parse(commented);
            xml.InsertAttributeName("allowNativeHeapPointerTagging", HeapTagging);
            byte[] result = xml.ToArray();

            BinaryXml back = BinaryXml.Parse(result);
            Assert.Equal(mapped + 1, back.ResourceMapCount);
            Assert.Equal("allowNativeHeapPointerTagging", back.Strings[mapped]);
            Assert.Equal(HeapTagging, back.ResourceId(mapped));
            Assert.Equal(original.Strings.Take(mapped).Concat(new[] { "allowNativeHeapPointerTagging" }).Concat(original.Strings.Skip(mapped)), back.Strings);
            for (int i = 0; i < mapped; i++) Assert.Equal(original.ResourceId(i), back.ResourceId(i));
            // The tree reads the same (names, namespaces, values, text), and so do the comments.
            Assert.Equal(Keys(doc), Keys(result));
            Assert.Equal(original.StringAttributes().Select(a => a.ToString()), back.StringAttributes().Select(a => a.ToString()));
            var after = BinaryXmlTests.NodeChunks(result);
            Assert.Equal("https", back.Strings[BitConverter.ToInt32(result, after.First(c => c.Type == 0x0100).Offset + 12)]);
            Assert.Equal("some text", back.Strings[BitConverter.ToInt32(result, after.First(c => c.Type == 0x0102).Offset + 12)]);
            int cdata = after.First(c => c.Type == 0x0104).Offset;
            Assert.Equal("app.Last", back.Strings[BitConverter.ToInt32(result, cdata + 12)]);
            Assert.Equal("some text", back.Strings[BitConverter.ToInt32(result, cdata + 16)]);
            Assert.Equal(original.IndexOf("tools") + 1, back.IndexOf("tools"));
            AssertSameTree(Tree(), result, utf8);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AnAttributeWithANewFrameworkName(bool utf8)
        {
            byte[] doc = AxmlWriter.Write(Tree(), utf8);
            BinaryXml xml = BinaryXml.Parse(doc);
            xml.InsertAttribute(Find(xml, "application"), BinaryXml.NewAttribute.Android("allowNativeHeapPointerTagging", HeapTagging, BinaryXml.Value.Boolean(false)));
            xml.InsertAttribute(Find(xml, "activity", "app.Main"), BinaryXml.NewAttribute.Android("fitsSystemWindows", FitsSystemWindows, BinaryXml.Value.Boolean(true)));
            xml.InsertAttribute(Find(xml, "activity", "app.Main"), BinaryXml.NewAttribute.Android("exported", Exported, BinaryXml.Value.Boolean(true)));
            xml.SetInt(Find(xml, "uses-sdk"), Ns, "targetSdkVersion", 35);
            byte[] result = xml.ToArray();

            BinaryXml back = BinaryXml.Parse(result);
            Assert.Equal(xml.ResourceMapCount + 2, back.ResourceMapCount);
            Assert.Equal(new[] { 0x01010003u, Exported, FitsSystemWindows }, Find(back, "activity", "app.Main").Attributes.Select(a => a.ResourceId));
            Assert.Equal(new[] { 0x01010003u, HeapTagging }, Find(back, "application").Attributes.Select(a => a.ResourceId));
            AssertSameTree(Tree(e =>
            {
                e["uses-sdk"].Attributes[1] = (Ns, "targetSdkVersion", 35);
                e["application"].Attributes.Add((Ns, "allowNativeHeapPointerTagging", false));
                e["main"].Attributes.Add((Ns, "exported", true));
                e["main"].Attributes.Add((Ns, "fitsSystemWindows", true));
            }), result, utf8);
        }

        private static BinaryXml.NewElement Queries(int line)
        {
            BinaryXml.NewElement Intent(string scheme) => new BinaryXml.NewElement("intent", line)
                .Add(new BinaryXml.NewElement("action", line, BinaryXml.NewAttribute.Android("name", 0x01010003, BinaryXml.Value.String("android.intent.action.VIEW"))))
                .Add(new BinaryXml.NewElement("data", line, BinaryXml.NewAttribute.Android("scheme", Scheme, BinaryXml.Value.String(scheme))));
            return new BinaryXml.NewElement("queries", line).Add(Intent("https")).Add(Intent("http"));
        }

        private static AxmlWriter.Element ExpectedQueries(int line)
        {
            AxmlWriter.Element Intent(string scheme) => new AxmlWriter.Element("intent") { Line = line }
                .Add(new AxmlWriter.Element("action", (Ns, "name", "android.intent.action.VIEW")) { Line = line })
                .Add(new AxmlWriter.Element("data", (Ns, "scheme", scheme)) { Line = line });
            return new AxmlWriter.Element("queries") { Line = line }.Add(Intent("https")).Add(Intent("http"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ElementSubtreesGoBeforeAChildOrAtTheEnd(bool utf8)
        {
            byte[] doc = AxmlWriter.Write(Tree(), utf8);
            BinaryXml xml = BinaryXml.Parse(doc);
            BinaryXml.Element manifest = Find(xml, "manifest"), application = Find(xml, "application");
            xml.InsertElement(manifest, new BinaryXml.NewElement("uses-permission", 90,
                                                                 BinaryXml.NewAttribute.Android("name", 0x01010003, BinaryXml.Value.String("android.permission.POST_NOTIFICATIONS"))),
                              application);
            xml.InsertElement(manifest, Queries(91), application);
            // Before an element that declares a namespace: ahead of its scope. At the end of <application>.
            xml.InsertElement(application, new BinaryXml.NewElement("meta-data", 92, BinaryXml.NewAttribute.Android("name", 0x01010003, BinaryXml.Value.String("first"))),
                              Find(xml, "activity", "app.Main"));
            xml.InsertElement(application, new BinaryXml.NewElement("meta-data", 93, BinaryXml.NewAttribute.Android("name", 0x01010003, BinaryXml.Value.String("app.Last"))));
            byte[] result = xml.ToArray();

            // "scheme" is a new framework name; the permission, the element names, the VIEW action, "http" and "first" are
            // appended ("https" and "app.Last" are reused).
            BinaryXml back = BinaryXml.Parse(result);
            Assert.Equal(xml.ResourceMapCount + 1, back.ResourceMapCount);
            Assert.Equal(new[] { "android.permission.POST_NOTIFICATIONS", "queries", "intent", "android.intent.action.VIEW", "data", "http", "first" }.OrderBy(s => s),
                         back.Strings.Skip(xml.Strings.Count + 1).OrderBy(s => s));
            AssertSameTree(Tree(e =>
            {
                var manifestChildren = e["manifest"].Children;
                manifestChildren.Insert(2, new AxmlWriter.Element("uses-permission", (Ns, "name", "android.permission.POST_NOTIFICATIONS")) { Line = 90 });
                manifestChildren.Insert(3, ExpectedQueries(91));
                e["application"].Children.Insert(0, new AxmlWriter.Element("meta-data", (Ns, "name", "first")) { Line = 92 });
                e["application"].Children.Add(new AxmlWriter.Element("meta-data", (Ns, "name", "app.Last")) { Line = 93 });
            }), result, utf8);
        }

        [Fact]
        public void AdditionsAndRemovalsCombine()
        {
            byte[] doc = AxmlWriter.Write(Tree());
            BinaryXml xml = BinaryXml.Parse(doc);
            BinaryXml.Element push = Find(xml, "receiver", "app.Push"), application = Find(xml, "application");
            // Inserted into an element that is then removed: gone with it. Inserted before a removed element: stays.
            xml.InsertElement(push, new BinaryXml.NewElement("meta-data", 80, BinaryXml.NewAttribute.Android("name", 0x01010003, BinaryXml.Value.String("lost"))));
            xml.InsertAttribute(push, BinaryXml.NewAttribute.Android("fitsSystemWindows", FitsSystemWindows, BinaryXml.Value.Boolean(true)));
            xml.InsertElement(application, new BinaryXml.NewElement("meta-data", 81, BinaryXml.NewAttribute.Android("name", 0x01010003, BinaryXml.Value.String("kept"))), push);
            xml.InsertAttribute(Find(xml, "activity", "app.Main"), BinaryXml.NewAttribute.Android("exported", Exported, BinaryXml.Value.Boolean(true)));
            xml.Remove(push);
            xml.Remove(Find(xml, "uses-permission"));
            Assert.Throws<InvalidOperationException>(() => xml.InsertAttribute(push, BinaryXml.NewAttribute.Android("enabled", 0x0101000e, BinaryXml.Value.Boolean(true))));
            byte[] result = xml.ToArray();

            AssertSameTree(Tree(e =>
            {
                e["application"].Children.Remove(e["push"]);
                e["application"].Children.Insert(1, new AxmlWriter.Element("meta-data", (Ns, "name", "kept")) { Line = 81 });
                e["manifest"].Children.Remove(e["internet"]);
                e["main"].Attributes.Add((Ns, "exported", true));
            }), result, false);
        }

        [Fact]
        public void TheIdClassAndStyleIndicesFollowTheirAttributes()
        {
            // A layout-like root whose header says its third attribute is "style" (no namespace, as aapt indexes it).
            var root = new AxmlWriter.Element("RelativeLayout", (Ns, "id", "@id"), (Ns, "layout_width", -1), (null, "style", "@style/x"));
            byte[] doc = AxmlWriter.Write(root, true);
            int start = BinaryXmlTests.NodeChunks(doc).First(c => c.Type == 0x0102).Offset;
            BitConverter.GetBytes((ushort)3).CopyTo(doc, start + 16 + 18);   // styleIndex (1-based)

            BinaryXml xml = BinaryXml.Parse(doc);
            xml.InsertAttribute(xml.Elements()[0], BinaryXml.NewAttribute.Android("fitsSystemWindows", FitsSystemWindows, BinaryXml.Value.Boolean(true)));
            byte[] result = xml.ToArray();
            int moved = BinaryXmlTests.NodeChunks(result).First(c => c.Type == 0x0102).Offset;
            Assert.Equal(4, BitConverter.ToUInt16(result, moved + 16 + 18));   // id (0xd0), fitsSystemWindows (0xdd), layout_width (0xf4), style
            Assert.Equal(0, BitConverter.ToUInt16(result, moved + 16 + 14));
            Assert.Equal(new[] { "id", "fitsSystemWindows", "layout_width", "style" }, BinaryXml.Parse(result).Elements()[0].Attributes.Select(a => a.Name));
        }

        [Fact]
        public void BadAdditionsAreRefused()
        {
            byte[] doc = AxmlWriter.Write(Tree());
            BinaryXml xml = BinaryXml.Parse(doc);
            BinaryXml.Element push = Find(xml, "receiver", "app.Push"), main = Find(xml, "activity", "app.Main");
            var exported = BinaryXml.NewAttribute.Android("exported", Exported, BinaryXml.Value.Boolean(true));
            Assert.Throws<InvalidOperationException>(() => xml.InsertAttribute(push, exported));                      // already has one
            xml.InsertAttribute(main, exported);
            Assert.Throws<InvalidOperationException>(() => xml.InsertAttribute(main, exported));                      // twice
            Assert.Throws<InvalidOperationException>(() => xml.InsertAttribute(main, new BinaryXml.NewAttribute(Ns, "enabled", 0, BinaryXml.Value.Boolean(true))));
            Assert.Throws<InvalidOperationException>(() => xml.InsertAttribute(main, BinaryXml.NewAttribute.Android("name2", 0x01010003, BinaryXml.Value.Boolean(true))));
            Assert.Throws<InvalidOperationException>(() => xml.InsertAttribute(push, new BinaryXml.NewAttribute("http://schemas.android.com/tools", "node", 0, BinaryXml.Value.String("x"))));
            xml.InsertAttribute(Find(xml, "intent-filter"), new BinaryXml.NewAttribute("http://schemas.android.com/tools", "node", 0, BinaryXml.Value.String("x")));   // in scope there
            Assert.Throws<InvalidOperationException>(() => xml.InsertAttribute(main, new BinaryXml.NewAttribute("", "style", 0, BinaryXml.Value.String("x"))));
            Assert.Throws<InvalidOperationException>(() => xml.SetInt(push, Ns, "exported", 1));                    // a boolean
            Assert.Throws<InvalidOperationException>(() => xml.SetInt(Find(xml, "manifest"), "", "package", 1));     // a string
            Assert.Throws<InvalidOperationException>(() => xml.SetInt(main, Ns, "missing", 1));
            Assert.Throws<ArgumentException>(() => xml.InsertElement(Find(xml, "manifest"), new BinaryXml.NewElement("x", 1), main));   // not its child
            Assert.Throws<ArgumentException>(() => xml.InsertElement(BinaryXml.Parse(doc).Elements()[0], new BinaryXml.NewElement("x", 1)));
            Assert.Throws<InvalidOperationException>(() => xml.InsertElement(main, new BinaryXml.NewElement("x", 1,
                BinaryXml.NewAttribute.Android("name", 0x01010003, BinaryXml.Value.String("a")), BinaryXml.NewAttribute.Android("name", 0x01010003, BinaryXml.Value.String("b")))));
            Assert.Throws<InvalidOperationException>(() => xml.InsertAttributeName("other", Exported));               // 0x01010010 is "exported"
            Assert.Throws<InvalidOperationException>(() => xml.Replace(xml.IndexOf("app.Last"), "app.Other"));        // renames come first
            Assert.True(xml.Grows);

            // Strings are reserved when an addition is recorded (a refused one reserves none), so ToArray adds none of its own.
            byte[] result = xml.ToArray();
            Assert.Equal(new[] { "node", "x" }, BinaryXml.Parse(result).Strings.Skip(xml.Strings.Count));
            Assert.Equal(result, xml.ToArray());
        }
    }
}
