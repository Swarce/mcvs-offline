using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Manifest;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// BinaryXml's element tree and element removal on synthetic AXML: a removal cuts exactly the element's chunk range
    /// (children, CDATA and inner namespace scopes included) and every other byte stays, the string pool and resource map
    /// included; malformed node sequences are refused; aapt2, when available, reads every result as the original tree minus
    /// that subtree.
    /// </summary>
    public class BinaryXmlTests
    {
        private const string Ns = AxmlWriter.AndroidNs;
        private const string Tools = "http://schemas.android.com/tools";
        private const string Other = "http://example.com/other";

        private readonly ITestOutputHelper output;

        public BinaryXmlTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// manifest
        ///   uses-permission INTERNET                    (first child of the root)
        ///   application
        ///     activity app.First                        (first child, with an intent filter)
        ///       intent-filter / action MAIN, category LAUNCHER
        ///     service app.Middle                        (text)
        ///     receiver app.Scoped                       (declares xmlns:tools, used by its child)
        ///       meta-data inner tools:node
        ///     provider app.Holder                       (its child declares xmlns:o)
        ///       meta-data app.Inner android:value="app.First" (a pool string another element uses too)
        ///     activity app.Last                         (last child)
        ///   uses-permission VIBRATE maxSdkVersion=18     (last child of the root)
        /// </summary>
        internal static AxmlWriter.Element Tree()
        {
            var first = new AxmlWriter.Element("activity", (Ns, "name", "app.First"), (Ns, "exported", true))
                .Add(new AxmlWriter.Element("intent-filter")
                     .Add(new AxmlWriter.Element("action", (Ns, "name", "android.intent.action.MAIN")))
                     .Add(new AxmlWriter.Element("category", (Ns, "name", "android.intent.category.LAUNCHER"))));
            var middle = new AxmlWriter.Element("service", (Ns, "name", "app.Middle")) { Text = "some text" };
            var scoped = new AxmlWriter.Element("receiver", (Ns, "name", "app.Scoped"))
                .Add(new AxmlWriter.Element("meta-data", (Ns, "name", "inner"), (Tools, "node", "remove")));
            scoped.Namespaces.Add(("tools", Tools));
            var inner = new AxmlWriter.Element("meta-data", (Ns, "name", "app.Inner"), (Ns, "value", "app.First"), (Other, "flag", "on"));
            inner.Namespaces.Add(("o", Other));
            var holder = new AxmlWriter.Element("provider", (Ns, "name", "app.Holder"), (Ns, "authorities", "app.holder")).Add(inner);
            var app = new AxmlWriter.Element("application", (Ns, "name", "app.App"))
                .Add(first).Add(middle).Add(scoped).Add(holder).Add(new AxmlWriter.Element("activity", (Ns, "name", "app.Last")));
            return new AxmlWriter.Element("manifest", (null, "package", "app"))
                .Add(new AxmlWriter.Element("uses-permission", (Ns, "name", "android.permission.INTERNET")))
                .Add(app)
                .Add(new AxmlWriter.Element("uses-permission", (Ns, "name", "android.permission.VIBRATE"), (Ns, "maxSdkVersion", 18)));
        }

        /// <summary>Type and offset of every chunk after the string pool (resource map, then the XML nodes).</summary>
        internal static List<(ushort Type, int Offset, int Size)> NodeChunks(byte[] doc)
        {
            var result = new List<(ushort, int, int)>();
            int at = PoolEnd(doc);
            while (at < doc.Length)
            {
                int size = BitConverter.ToInt32(doc, at + 4);
                result.Add((BitConverter.ToUInt16(doc, at), at, size));
                at += size;
            }
            return result;
        }

        /// <summary>File offset of the first chunk after the string pool.</summary>
        internal static int PoolEnd(byte[] doc)
        {
            int headerSize = BitConverter.ToUInt16(doc, 2);
            return headerSize + BitConverter.ToInt32(doc, headerSize + 4);
        }

        internal static List<string> Keys(BinaryXml xml)
        {
            return xml.Elements().Select(ManifestDebloat.Key).ToList();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ParsingAndWritingWithoutRemovalsIsByteIdentical(bool utf8)
        {
            foreach (AxmlWriter.Element tree in new[] { Tree(), AxmlWriter.GameLikeManifest(), ManifestDebloatTests.DebloatLikeManifest() })
            {
                byte[] doc = AxmlWriter.Write(tree, utf8);
                Assert.Equal(doc, BinaryXml.Parse(doc).ToArray());
                BinaryXml xml = BinaryXml.Parse(doc);
                Assert.NotEmpty(xml.Elements());   // building the element index changes nothing either
                Assert.Equal(doc, xml.ToArray());
            }
        }

        [Fact]
        public void ElementsDescribeTheTree()
        {
            BinaryXml xml = BinaryXml.Parse(AxmlWriter.Write(Tree()));
            IReadOnlyList<BinaryXml.Element> e = xml.Elements();
            Assert.Equal(new[] { "manifest", "uses-permission", "application", "activity", "intent-filter", "action", "category", "service", "receiver",
                                 "meta-data", "provider", "meta-data", "activity", "uses-permission" }, e.Select(x => x.Name));
            Assert.Equal(new[] { 0, 1, 1, 2, 3, 4, 4, 2, 2, 3, 2, 3, 2, 1 }, e.Select(x => x.Depth));
            Assert.Equal(Enumerable.Range(0, e.Count), e.Select(x => x.Index));
            Assert.Equal(Enumerable.Range(1, e.Count), e.Select(x => x.Line));
            Assert.Null(e[0].Parent);
            Assert.Same(e[4], e[5].Parent);
            Assert.Equal("manifest/application/activity/intent-filter/action", e[5].Path);
            Assert.Equal(new[] { null, "android.permission.INTERNET", "app.App", "app.First", null, "android.intent.action.MAIN", "android.intent.category.LAUNCHER",
                                 "app.Middle", "app.Scoped", "inner", "app.Holder", "app.Inner", "app.Last", "android.permission.VIBRATE" },
                         e.Select(x => x.AndroidName));
            Assert.All(e, x => Assert.Equal("", x.Namespace));
            BinaryXml.Attribute max = e[13].Find(Ns, "maxSdkVersion");
            Assert.Equal(18u, max.Data);
            Assert.Equal(0x01010271u, max.ResourceId);
            Assert.Null(max.Value);
            Assert.Equal("on", e[11].Find(Other, "flag").Value);
            Assert.Equal(0u, e[11].Find(Other, "flag").ResourceId);
            Assert.Equal("app", e[0].Find("", "package").Value);
        }

        public static IEnumerable<object[]> RemovalCases()
        {
            yield return new object[] { "android.permission.INTERNET", "first child of the root" };
            yield return new object[] { "android.permission.VIBRATE", "last child of the root" };
            yield return new object[] { "app.First", "first child, with nested children" };
            yield return new object[] { "app.Middle", "with text" };
            yield return new object[] { "app.Scoped", "declares a namespace: the scope stays" };
            yield return new object[] { "app.Holder", "a child declares a namespace: the scope goes" };
            yield return new object[] { "app.Last", "last child" };
            yield return new object[] { "android.intent.action.MAIN", "a nested leaf" };
            yield return new object[] { "app.App", "the element that holds most of the document" };
        }

        [Theory]
        [MemberData(nameof(RemovalCases))]
        public void RemovalCutsExactlyTheElementsChunkRange(string androidName, string what)
        {
            foreach (bool utf8 in new[] { false, true })
            {
                byte[] doc = AxmlWriter.Write(Tree(), utf8);
                BinaryXml xml = BinaryXml.Parse(doc);
                List<BinaryXml.Element> before = xml.Elements().ToList();
                BinaryXml.Element target = before.Single(x => x.AndroidName == androidName);
                List<BinaryXml.Element> subtree = before.Where(x => IsIn(x, target)).ToList();
                List<string> attributesBefore = xml.StringAttributes().Select(a => a.ToString()).ToList();

                xml.Remove(target);
                byte[] result = xml.ToArray();

                // The bytes: the original with [start, end) of the node sequence cut and the RES_XML_TYPE size updated.
                int cutAt = PoolEnd(doc) + target.Start, cutLength = target.End - target.Start;
                byte[] expected = doc.Take(cutAt).Concat(doc.Skip(cutAt + cutLength)).ToArray();
                BitConverter.GetBytes(expected.Length).CopyTo(expected, 4);
                Assert.Equal(expected, result);
                Assert.True(target.Removed && subtree.All(x => x.Removed), what);

                // Re-read: same pool and resource map, the tree without the subtree, the attributes without its attributes.
                BinaryXml back = BinaryXml.Parse(result);
                Assert.Equal(BinaryXml.Parse(doc).Strings, back.Strings);
                Assert.Equal(BinaryXml.Parse(doc).ResourceMapCount, back.ResourceMapCount);
                Assert.Equal(before.Except(subtree).Select(ManifestDebloat.Key), Keys(back));
                Assert.Equal(Keys(back), Keys(xml));
                List<string> attributesAfter = back.StringAttributes().Select(a => a.ToString()).ToList();
                Assert.Equal(attributesAfter, xml.StringAttributes().Select(a => a.ToString()));
                int removedAttributes = subtree.Sum(x => x.Attributes.Count(a => a.RawValue >= 0 || a.TypedString >= 0));
                Assert.Equal(attributesBefore.Count - removedAttributes, attributesAfter.Count);

                // Text and namespace scopes inside the element go with it; a scope declared on the element itself stays.
                int Count(byte[] d, ushort type) => NodeChunks(d).Count(c => c.Type == type);
                int scopesInside = androidName == "app.Holder" ? 1 : androidName == "app.App" ? 2 : 0;   // xmlns:o; xmlns:tools and xmlns:o
                Assert.Equal(Count(doc, 0x0100) - scopesInside, Count(result, 0x0100));
                Assert.Equal(Count(doc, 0x0101) - scopesInside, Count(result, 0x0101));
                Assert.Equal(Count(doc, 0x0104) - (androidName == "app.Middle" || androidName == "app.App" ? 1 : 0), Count(result, 0x0104));
            }
        }

        private static bool IsIn(BinaryXml.Element candidate, BinaryXml.Element ancestor)
        {
            for (BinaryXml.Element e = candidate; e != null; e = e.Parent)
            {
                if (ReferenceEquals(e, ancestor)) return true;
            }
            return false;
        }

        [Theory]
        [MemberData(nameof(RemovalCases))]
        public void Aapt2ReadsEveryRemovalAsTheTreeWithoutThatSubtree(string androidName, string what)
        {
            if (TestEnvironment.Aapt2 == null)
            {
                output.WriteLine("aapt2 not found (set MCVS_BUILD_TOOLS): skipped");
                return;
            }
            byte[] doc = AxmlWriter.Write(Tree());
            BinaryXml xml = BinaryXml.Parse(doc);
            BinaryXml.Element target = xml.Elements().Single(x => x.AndroidName == androidName);
            xml.Remove(target);
            string original = Aapt2Dump.XmlTree(doc), result = Aapt2Dump.XmlTree(xml.ToArray());
            output.WriteLine(what + ":\n" + result);
            string expected = Aapt2Dump.CutSubtree(original, target.Name, target.Line);
            if (androidName != "app.Scoped")
            {
                Assert.Equal(expected, result);
                return;
            }
            // The receiver's own xmlns:tools scope stays, now empty: aapt2 shows it on the next element (line 11, the
            // provider); without the namespace lines the trees are the same.
            Assert.Contains("N: tools=" + Tools + " (line=9)\n" + new string(' ', 12) + "E: provider (line=11)", result);
            Assert.Equal(Aapt2Dump.WithoutNamespaces(expected), Aapt2Dump.WithoutNamespaces(result));
        }

        [Fact]
        public void SiblingsAndNestedRemovalsCombine()
        {
            byte[] doc = AxmlWriter.Write(Tree());

            // The first and the last child of <application>, and a leaf inside a third element.
            BinaryXml xml = BinaryXml.Parse(doc);
            foreach (string name in new[] { "app.Last", "android.intent.category.LAUNCHER", "app.Middle" })
                xml.Remove(xml.Elements().Single(x => x.AndroidName == name));
            Assert.Equal(new[] { "manifest", "uses-permission", "application", "activity", "intent-filter", "action", "receiver", "meta-data", "provider",
                                 "meta-data", "uses-permission" }, BinaryXml.Parse(xml.ToArray()).Elements().Select(x => x.Name));

            // A child, then its parent: the same bytes as removing the parent alone.
            BinaryXml childFirst = BinaryXml.Parse(doc);
            childFirst.Remove(childFirst.Elements().Single(x => x.Name == "intent-filter"));
            childFirst.Remove(childFirst.Elements().Single(x => x.AndroidName == "app.First"));
            BinaryXml parentOnly = BinaryXml.Parse(doc);
            parentOnly.Remove(parentOnly.Elements().Single(x => x.AndroidName == "app.First"));
            Assert.Equal(parentOnly.ToArray(), childFirst.ToArray());

            // Removals and a pool rename are independent.
            BinaryXml renamed = BinaryXml.Parse(doc);
            renamed.Replace(renamed.IndexOf("app.First"), "app.Renamed");
            renamed.Remove(renamed.Elements().Single(x => x.AndroidName == "app.Holder"));
            BinaryXml back = BinaryXml.Parse(renamed.ToArray());
            Assert.Equal("app.Renamed", back.Elements().Single(x => x.Name == "activity" && x.Depth == 2 && x.Index == 3).AndroidName);
            Assert.DoesNotContain(back.Elements(), x => x.AndroidName == "app.Holder" || x.AndroidName == "app.Inner");
            // Once the tree is read its names are fixed: a later rename is refused instead of leaving them stale.
            Assert.Throws<InvalidOperationException>(() => renamed.Replace(renamed.IndexOf("app.Last"), "app.Other"));
        }

        [Fact]
        public void RemoveRefusesTheRootAForeignAndAnAlreadyRemovedElement()
        {
            byte[] doc = AxmlWriter.Write(Tree());
            BinaryXml xml = BinaryXml.Parse(doc);
            IReadOnlyList<BinaryXml.Element> e = xml.Elements();
            Assert.Throws<InvalidOperationException>(() => xml.Remove(e[0]));
            Assert.Throws<ArgumentException>(() => xml.Remove(BinaryXml.Parse(doc).Elements()[3]));
            Assert.Throws<ArgumentNullException>(() => xml.Remove(null));
            BinaryXml.Element first = e.Single(x => x.AndroidName == "app.First");
            BinaryXml.Element action = e.Single(x => x.AndroidName == "android.intent.action.MAIN");
            xml.Remove(first);
            Assert.Throws<InvalidOperationException>(() => xml.Remove(first));
            Assert.Throws<InvalidOperationException>(() => xml.Remove(action));   // gone with its ancestor
            Assert.DoesNotContain(action, xml.Elements());
        }

        public static IEnumerable<object[]> Malformations()
        {
            yield return new object[] { "end-name", "does not close the innermost open element" };
            yield return new object[] { "unclosed", "document ends inside" };
            yield return new object[] { "unknown-type", "unknown XML node chunk type 0x0105" };
            yield return new object[] { "end-namespace", "END_NAMESPACE" };
            yield return new object[] { "second-root", "expected one root element, found 2" };
            yield return new object[] { "trailing", "truncated XML node chunk" };
            yield return new object[] { "short-header", "8-byte header" };
            yield return new object[] { "late-map", "resource map chunk" };
        }

        [Theory]
        [MemberData(nameof(Malformations))]
        public void MalformedNodeSequencesAreRefused(string kind, string message)
        {
            byte[] doc = AxmlWriter.Write(Tree());
            List<(ushort Type, int Offset, int Size)> chunks = NodeChunks(doc);
            BinaryXml pool = BinaryXml.Parse(doc);
            byte[] bad;
            switch (kind)
            {
                case "end-name":   // the first END_ELEMENT (</uses-permission>) names <category>
                    bad = (byte[])doc.Clone();
                    BitConverter.GetBytes(pool.IndexOf("category")).CopyTo(bad, chunks.First(c => c.Type == 0x0103).Offset + 20);
                    break;
                case "unclosed":   // without </manifest> and the end of its namespace scope
                    bad = doc.Take(chunks[chunks.Count - 2].Offset).ToArray();
                    break;
                case "unknown-type":
                    bad = (byte[])doc.Clone();
                    BitConverter.GetBytes((ushort)0x0105).CopyTo(bad, chunks.First(c => c.Type == 0x0104).Offset);
                    break;
                case "end-namespace":   // the inner xmlns:tools scope closes with another prefix
                    bad = (byte[])doc.Clone();
                    BitConverter.GetBytes(pool.IndexOf("o")).CopyTo(bad, chunks.First(c => c.Type == 0x0101).Offset + 16);
                    break;
                case "second-root":     // a copy of <uses-permission INTERNET/> after </manifest>
                {
                    var start = chunks.First(c => c.Type == 0x0102 && c.Offset > chunks.First(x => x.Type == 0x0102).Offset);
                    var end = chunks.First(c => c.Type == 0x0103);
                    Assert.True(end.Offset > start.Offset);
                    byte[] copy = doc.Skip(start.Offset).Take(end.Offset + end.Size - start.Offset).ToArray();
                    int closeRoot = chunks[chunks.Count - 1].Offset;   // before the final END_NAMESPACE
                    bad = doc.Take(closeRoot).Concat(copy).Concat(doc.Skip(closeRoot)).ToArray();
                    break;
                }
                case "trailing":
                    bad = doc.Concat(new byte[4]).ToArray();
                    break;
                case "short-header":
                    bad = (byte[])doc.Clone();
                    BitConverter.GetBytes((ushort)8).CopyTo(bad, chunks.First(c => c.Type == 0x0104).Offset + 2);
                    break;
                case "late-map":        // the resource map moved after the first namespace node
                {
                    var map = chunks[0];
                    var ns = chunks[1];
                    Assert.Equal(0x0180, map.Type);
                    bad = doc.Take(map.Offset).Concat(doc.Skip(ns.Offset).Take(ns.Size)).Concat(doc.Skip(map.Offset).Take(map.Size))
                             .Concat(doc.Skip(ns.Offset + ns.Size)).ToArray();
                    break;
                }
                default:
                    throw new ArgumentException(kind);
            }
            BitConverter.GetBytes(bad.Length).CopyTo(bad, 4);
            BinaryXml xml = BinaryXml.Parse(bad);   // the pool is fine; the tree is what is refused
            var ex = Assert.Throws<InvalidDataException>(() => xml.Elements());
            output.WriteLine(ex.Message);
            Assert.Contains(message, ex.Message);
            Assert.Throws<InvalidDataException>(() => xml.Remove(BinaryXml.Parse(doc).Elements()[1]));
        }

        [Fact]
        public void AndroidNameIsReadTheWayThePlatformReadsIt()
        {
            // An int android:name is no literal name; an element without one has none.
            var root = new AxmlWriter.Element("manifest", (null, "package", "app"))
                .Add(new AxmlWriter.Element("uses-feature", (Ns, "name", 3)))
                .Add(new AxmlWriter.Element("supports-screens", (Ns, "exported", true)));
            IReadOnlyList<BinaryXml.Element> e = BinaryXml.Parse(AxmlWriter.Write(root)).Elements();
            Assert.Null(e[1].AndroidName);
            Assert.Null(e[2].AndroidName);

            // PackageManager reads android:name by resource id: another namespace's "name" shares the mapped pool string, so
            // the platform would read it as android:name too. Refused rather than guessed.
            var aliased = new AxmlWriter.Element("manifest", (null, "package", "app"))
                .Add(new AxmlWriter.Element("uses-permission", (Ns, "name", "android.permission.INTERNET")))
                .Add(new AxmlWriter.Element("activity", (Ns, "exported", true), (Other, "name", "app.Alias")));
            Assert.Contains("is not a plain android:name", Assert.Throws<InvalidDataException>(() => BinaryXml.Parse(AxmlWriter.Write(aliased)).Elements()).Message);

            // Two android:name attributes, or raw and typed values that disagree, are refused.
            var twice = new AxmlWriter.Element("manifest", (null, "package", "app"))
                .Add(new AxmlWriter.Element("activity", (Ns, "name", "app.A"), (Ns, "name", "app.B")));
            Assert.Contains("2 android:name attributes", Assert.Throws<InvalidDataException>(() => BinaryXml.Parse(AxmlWriter.Write(twice)).Elements()).Message);

            byte[] doc = AxmlWriter.Write(Tree());
            BinaryXml xml = BinaryXml.Parse(doc);
            BinaryXml.Element first = xml.Elements().Single(x => x.AndroidName == "app.First");
            int ext = PoolEnd(doc) + first.Start + 16;
            int attribute = ext + BitConverter.ToUInt16(doc, ext + 8);   // android:name is the first attribute
            Assert.Equal(xml.IndexOf("name"), BitConverter.ToInt32(doc, attribute + 4));
            byte[] mismatch = (byte[])doc.Clone();
            BitConverter.GetBytes(xml.IndexOf("app.Last")).CopyTo(mismatch, attribute + 8);   // raw value
            Assert.Contains("different raw and typed values", Assert.Throws<InvalidDataException>(() => BinaryXml.Parse(mismatch).Elements()).Message);
        }
    }
}
