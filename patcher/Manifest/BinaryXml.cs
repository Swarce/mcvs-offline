using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace McvsOffline.Patcher.Manifest
{
    /// <summary>
    /// A compiled (binary XML, "AXML") document, AndroidManifest.xml or a res/ XML file, as far as the patcher needs it: the
    /// string pool, which can be rewritten and grown, a walk of the elements and their attributes, the removal of whole
    /// elements, and additions (an integer value changed, attributes and element subtrees inserted).
    /// <para>
    /// Layout (frameworks/base/libs/androidfw ResourceTypes.h): an RES_XML_TYPE chunk holding an RES_STRING_POOL_TYPE
    /// chunk, then the resource map and the namespace/element nodes. Element names, attribute names and string values
    /// all refer to the pool by index, so replacing the text of one pool string changes it everywhere it is used, while
    /// every byte after the pool is copied unchanged. Unchanged strings keep their exact encoded bytes, so
    /// <c>Parse(x).ToArray()</c> is <c>x</c> again.
    /// </para>
    /// <para>
    /// The nodes are a flat sequence of chunks (START_NAMESPACE, START_ELEMENT, CDATA, END_ELEMENT, END_NAMESPACE): an
    /// element's chunk does not contain its children, they follow it up to the matching END_ELEMENT. Removing an element
    /// therefore cuts that one contiguous byte range out of the node sequence and changes nothing else: no chunk refers to
    /// another by offset, so only the RES_XML_TYPE size changes. The pool and the resource map stay as they are (strings
    /// that only the removed nodes used remain in the pool, unused, which the platform's parser does not mind).
    /// </para>
    /// <para>
    /// Additions are recorded and applied by <see cref="ToArray"/>, like removals: <see cref="Elements"/> and the attributes
    /// describe the document as read, <c>Parse(ToArray())</c> shows the additions, and with nothing asked the output is the
    /// input byte for byte. Strings without a framework id (element names, values, other attribute names) are appended at
    /// the end of the pool, which moves no index. A framework attribute name has to sit in the resource-mapped region (pool
    /// index i &lt; map count means framework id map[i]), so a missing one is inserted right after the last mapped string,
    /// the map grows by its id, and every reference to a later string moves up: node comments, namespace prefixes and URIs,
    /// element and attribute namespaces and names, raw values, TYPE_STRING values and CDATA text. An inserted attribute goes
    /// where the platform looks for it (attributes with a framework id ascend by id: AttributeResolution's RetrieveAttributes
    /// walks them in step with the sorted requested ids), and the element's id/class/style attribute indices follow their
    /// attributes. An inserted element is a START_ELEMENT/END_ELEMENT pair (with its children) placed either before a given
    /// child, ahead of the namespace scopes declared around that child, or as the parent's last child.
    /// </para>
    /// </summary>
    public sealed class BinaryXml
    {
        private const ushort XmlType = 0x0003;
        private const ushort StringPoolType = 0x0001;
        private const ushort ResourceMapType = 0x0180;
        private const ushort StartNamespaceType = 0x0100;
        private const ushort EndNamespaceType = 0x0101;
        private const ushort StartElementType = 0x0102;
        private const ushort EndElementType = 0x0103;
        private const ushort CDataType = 0x0104;
        private const int NodeHeaderSize = 16;          // ResXMLTree_node: chunk header, lineNumber, comment
        private const int AttributeExtSize = 20;        // ResXMLTree_attrExt
        private const int AttributeSize = 20;           // ResXMLTree_attribute
        private const uint SortedFlag = 0x1;
        private const uint Utf8Flag = 0x100;
        private const uint NoIndex = 0xFFFFFFFF;
        private const byte TypeString = 0x03;
        private const byte TypeIntDec = 0x10;
        private const byte TypeIntHex = 0x11;
        private const byte TypeIntBoolean = 0x12;
        private const int PoolHeaderMinSize = 28;

        /// <summary>android:name (frameworks/base/core/res/res/values/public.xml): what PackageManager reads, by id.</summary>
        public const uint AndroidNameResourceId = 0x01010003;

        public const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

        private readonly byte[] poolHeader;
        private readonly uint[] originalOffsets;
        private readonly byte[][] encoded;      // each string's bytes with its length prefix and terminator
        private readonly bool[] replaced;
        private readonly string[] strings;
        private readonly byte[] styleOffsets;
        private readonly byte[] styleData;
        private readonly byte[] tail;           // everything after the string pool chunk, as read (removals are applied on output)
        private readonly int xmlHeaderSize;
        private readonly byte[] xmlHeader;
        private readonly bool utf8;
        private readonly int resourceMapCount;
        private List<Element> elements;         // built on first use (Elements, Remove), in document order

        // Additions, applied on output (see the class remarks).
        private readonly List<(string Name, uint Id)> insertedNames = new List<(string, uint)>();   // after the mapped strings, in order
        private readonly List<string> appendedStrings = new List<string>();
        private readonly Dictionary<int, ElementEdit> edits = new Dictionary<int, ElementEdit>();   // by START_ELEMENT offset
        private readonly List<(int Offset, NewElement Element, Element Scope)> insertions = new List<(int, NewElement, Element)>();

        private BinaryXml(byte[] xmlHeader, byte[] poolHeader, uint[] originalOffsets, byte[][] encoded, string[] strings,
                          byte[] styleOffsets, byte[] styleData, byte[] tail, bool utf8, int resourceMapCount)
        {
            this.xmlHeader = xmlHeader;
            xmlHeaderSize = xmlHeader.Length;
            this.poolHeader = poolHeader;
            this.originalOffsets = originalOffsets;
            this.encoded = encoded;
            this.strings = strings;
            replaced = new bool[strings.Length];
            this.styleOffsets = styleOffsets;
            this.styleData = styleData;
            this.tail = tail;
            this.utf8 = utf8;
            this.resourceMapCount = resourceMapCount;
        }

        /// <summary>The pool's strings, by index.</summary>
        public IReadOnlyList<string> Strings { get { return strings; } }

        /// <summary>
        /// The number of leading pool strings that the resource map ties to framework attribute ids: those are attribute
        /// names and must never be renamed.
        /// </summary>
        public int ResourceMapCount { get { return resourceMapCount; } }

        /// <summary>Thrown for anything that is not a well-formed binary XML document.</summary>
        public static BinaryXml Parse(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length < 8 || U16(data, 0) != XmlType) throw Bad("not a binary XML document (no RES_XML_TYPE header)");
            int headerSize = U16(data, 2);
            long size = U32(data, 4);
            if (headerSize < 8 || size != data.Length) throw Bad("RES_XML_TYPE size " + size + " does not match the file (" + data.Length + " bytes)");

            int pool = headerSize;
            if (pool + PoolHeaderMinSize > data.Length || U16(data, pool) != StringPoolType) throw Bad("the first chunk is not the string pool");
            int poolHeaderSize = U16(data, pool + 2);
            long poolSize = U32(data, pool + 4);
            int stringCount = Checked(U32(data, pool + 8), "string count");
            int styleCount = Checked(U32(data, pool + 12), "style count");
            uint flags = U32(data, pool + 16);
            long stringsStart = U32(data, pool + 20);
            long stylesStart = U32(data, pool + 24);
            if (poolHeaderSize < PoolHeaderMinSize || pool + poolSize > data.Length) throw Bad("string pool header or size out of range");
            long offsetsEnd = poolHeaderSize + 4L * (stringCount + styleCount);
            if (offsetsEnd > poolSize || (stringCount > 0 && (stringsStart < offsetsEnd || stringsStart > poolSize)))
                throw Bad("string pool offsets out of range");
            long stringsEnd = styleCount > 0 ? stylesStart : poolSize;
            if (styleCount > 0 && (stylesStart < stringsStart || stylesStart > poolSize)) throw Bad("style data out of range");
            bool isUtf8 = (flags & Utf8Flag) != 0;

            var offsets = new uint[stringCount];
            var encoded = new byte[stringCount][];
            var strings = new string[stringCount];
            for (int i = 0; i < stringCount; i++)
            {
                offsets[i] = U32(data, pool + poolHeaderSize + 4 * i);
                long at = pool + stringsStart + offsets[i];
                if (stringsStart + offsets[i] >= stringsEnd) throw Bad("string " + i + " starts outside the string data");
                int length = isUtf8 ? DecodeUtf8(data, (int)at, pool + stringsEnd, out strings[i]) : DecodeUtf16(data, (int)at, pool + stringsEnd, out strings[i]);
                encoded[i] = Slice(data, (int)at, length);
            }

            byte[] styleOffsets = Slice(data, (int)(pool + poolHeaderSize + 4L * stringCount), 4 * styleCount);
            byte[] styleData = styleCount > 0 ? Slice(data, (int)(pool + stylesStart), (int)(poolSize - stylesStart)) : new byte[0];
            byte[] tail = Slice(data, (int)(pool + poolSize), (int)(data.Length - pool - poolSize));

            int mapCount = 0;
            if (tail.Length >= 8 && U16(tail, 0) == ResourceMapType)
            {
                long mapSize = U32(tail, 4);
                int mapHeader = U16(tail, 2);
                if (mapSize > tail.Length || mapHeader > mapSize) throw Bad("resource map out of range");
                mapCount = (int)((mapSize - mapHeader) / 4);
            }
            return new BinaryXml(Slice(data, 0, headerSize), Slice(data, pool, poolHeaderSize), offsets, encoded, strings,
                                 styleOffsets, styleData, tail, isUtf8, mapCount);
        }

        /// <summary>The framework attribute id the resource map gives pool string <paramref name="index"/>, or 0.</summary>
        public uint ResourceId(int index)
        {
            if (index < 0 || index >= resourceMapCount) return 0;
            return U32(tail, U16(tail, 2) + 4 * index);
        }

        /// <summary>Index of the first pool string equal to <paramref name="value"/>, or -1.</summary>
        public int IndexOf(string value)
        {
            return Array.IndexOf(strings, value);
        }

        /// <summary>
        /// Replaces the text of pool string <paramref name="index"/> (every use of it changes). Renames come first: once
        /// <see cref="Elements"/> or <see cref="Remove"/> has read the tree, its names are fixed and a rename is refused.
        /// </summary>
        public void Replace(int index, string value)
        {
            if (index < 0 || index >= strings.Length) throw new ArgumentOutOfRangeException(nameof(index));
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (elements != null || Grows) throw new InvalidOperationException("rename pool strings before reading, removing or adding anything");
            if (index < resourceMapCount) throw new InvalidOperationException("string " + index + " ('" + strings[index] + "') is a framework attribute name");
            if (value.Length > 0x7FFF) throw new ArgumentException("string too long for the pool", nameof(value));
            strings[index] = value;
            replaced[index] = true;
        }

        /// <summary>An attribute of a start element.</summary>
        public sealed class Attribute
        {
            public string Element { get; internal set; }

            /// <summary>Namespace URI ("" for none).</summary>
            public string Namespace { get; internal set; }

            public string Name { get; internal set; }

            /// <summary>Pool index of the attribute name.</summary>
            public int NameIndex { get; internal set; }

            /// <summary>Framework attribute id of the name (resource map), or 0.</summary>
            public uint ResourceId { get; internal set; }

            /// <summary>Pool index of the raw value, or -1.</summary>
            public int RawValue { get; internal set; }

            /// <summary>Pool index of the typed string value (Res_value TYPE_STRING), or -1.</summary>
            public int TypedString { get; internal set; }

            /// <summary>Res_value dataType (0x03 string, 0x01 reference, 0x10 decimal int, 0x11 hex int, 0x12 boolean...).</summary>
            public byte DataType { get; internal set; }

            /// <summary>Res_value data (pool index for a string, the number for an int).</summary>
            public uint Data { get; internal set; }

            /// <summary>The value's text, from the raw value or else the typed string; null for a value that is no pool string.</summary>
            public string Value { get; internal set; }

            public override string ToString()
            {
                return "<" + Element + " " + (Namespace.Length > 0 ? "{" + Namespace + "}" : "") + Name + "=\"" +
                       (Value ?? "0x" + DataType.ToString("x2") + ":" + Data.ToString("x8")) + "\">";
            }
        }

        /// <summary>
        /// An element: its START_ELEMENT chunk, everything up to the matching END_ELEMENT chunk, and its place in the tree.
        /// </summary>
        public sealed class Element
        {
            /// <summary>Position in document order (0 = the root).</summary>
            public int Index { get; internal set; }

            public string Name { get; internal set; }

            /// <summary>Namespace URI ("" for none, as for every manifest element).</summary>
            public string Namespace { get; internal set; }

            /// <summary>0 for the root, 1 for its children...</summary>
            public int Depth { get; internal set; }

            /// <summary>The enclosing element, or null for the root.</summary>
            public Element Parent { get; internal set; }

            /// <summary>Source line number recorded by aapt.</summary>
            public int Line { get; internal set; }

            public IReadOnlyList<Attribute> Attributes { get; internal set; }

            /// <summary>
            /// The literal value of the element's android:name (resource id 0x01010003 in the android namespace), or null when
            /// it has none or its value is not a pool string.
            /// </summary>
            public string AndroidName { get; internal set; }

            /// <summary>Element names from the root down to this one, e.g. "manifest/application/activity".</summary>
            public string Path { get { return Parent == null ? Name : Parent.Path + "/" + Name; } }

            /// <summary>True once this element or an ancestor was removed.</summary>
            public bool Removed { get; internal set; }

            /// <summary>Offset of the START_ELEMENT chunk in the node sequence.</summary>
            internal int Start { get; set; }

            /// <summary>Offset just after the matching END_ELEMENT chunk.</summary>
            internal int End { get; set; }

            /// <summary>Offset of the matching END_ELEMENT chunk.</summary>
            internal int EndStart { get; set; }

            /// <summary>
            /// Offset of the START_NAMESPACE chunks that directly precede the START_ELEMENT chunk (the scopes declared on this
            /// element), or <see cref="Start"/> when there are none.
            /// </summary>
            internal int LeadStart { get; set; }

            /// <summary>Pool indices of the namespace URIs in scope at the element, innermost last.</summary>
            internal List<uint> Scope { get; set; }

            /// <summary>The attribute <paramref name="ns"/>:<paramref name="name"/>, or null.</summary>
            public Attribute Find(string ns, string name)
            {
                return Attributes.FirstOrDefault(a => a.Namespace == ns && a.Name == name);
            }

            public override string ToString()
            {
                return "<" + Name + (AndroidName == null ? "" : " android:name=\"" + AndroidName + "\"") + ">" +
                       (Parent == null ? "" : " in <" + Parent.Name + ">") + " (line " + Line + ")";
            }
        }

        /// <summary>
        /// The document's elements in document order, without removed ones. The first call checks the node structure
        /// and refuses (<see cref="InvalidDataException"/>) anything but a well-formed tree: every chunk after the resource
        /// map is a namespace, element or CDATA node of plausible size; every END_ELEMENT closes the innermost open element
        /// (same namespace and name) and every END_NAMESPACE the innermost open namespace scope (scopes and elements nest,
        /// they never overlap); there is exactly one root element and nothing follows it.
        /// </summary>
        public IReadOnlyList<Element> Elements()
        {
            return Tree().Where(e => !e.Removed).ToList();
        }

        /// <summary>
        /// Removes <paramref name="element"/>: its START_ELEMENT chunk through its matching END_ELEMENT chunk, with every
        /// nested child, CDATA node and namespace scope inside. Every other byte stays as it is, including the string pool,
        /// the resource map and a namespace scope declared around the element itself (an empty scope is valid). Throws for
        /// the root, for an element of another document and for one already removed (itself or with an ancestor).
        /// </summary>
        public void Remove(Element element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            List<Element> index = Tree();
            if (element.Index < 0 || element.Index >= index.Count || !ReferenceEquals(index[element.Index], element))
                throw new ArgumentException(element + " is not an element of this document", nameof(element));
            if (element.Removed) throw new InvalidOperationException(element + " was already removed");
            if (element.Parent == null) throw new InvalidOperationException("refusing to remove the root element " + element);
            // The descendants follow the element in document order and start before its END_ELEMENT.
            for (int i = element.Index; i < index.Count && index[i].Start < element.End; i++) index[i].Removed = true;
        }

        /// <summary>
        /// Every attribute of every start element whose value is a pool string, in document order (removed elements excluded,
        /// additions not included).
        /// </summary>
        public List<Attribute> StringAttributes()
        {
            var result = new List<Attribute>();
            byte[] nodes = Nodes(false);
            int at = 0;
            while (at + 8 <= nodes.Length)
            {
                ushort type = U16(nodes, at);
                int headerSize = U16(nodes, at + 2);
                long size = U32(nodes, at + 4);
                if (size < 8 || at + size > nodes.Length) throw Bad("XML node chunk at " + at + " out of range");
                if (type == StartElementType)
                {
                    foreach (Attribute a in ReadAttributes(nodes, at, headerSize, size))
                    {
                        if (a.RawValue >= 0 || a.TypedString >= 0) result.Add(a);
                    }
                }
                at += (int)size;
            }
            return result;
        }

        /// <summary>The attributes of the START_ELEMENT chunk at <paramref name="at"/>.</summary>
        private List<Attribute> ReadAttributes(byte[] nodes, int at, int headerSize, long size)
        {
            int ext = at + headerSize;
            if (headerSize < 8 || ext + AttributeExtSize > at + size) throw Bad("start element chunk at " + at + " too small");
            string element = Str(U32(nodes, ext + 4));
            int attributeStart = U16(nodes, ext + 8);
            int attributeSize = U16(nodes, ext + 10);
            int attributeCount = U16(nodes, ext + 12);
            var result = new List<Attribute>(attributeCount);
            for (int i = 0; i < attributeCount; i++)
            {
                int a = ext + attributeStart + i * attributeSize;
                if (a + AttributeSize > at + size) throw Bad("attribute " + i + " of <" + element + "> out of range");
                uint nameIndex = U32(nodes, a + 4);
                uint raw = U32(nodes, a + 8);
                byte dataType = nodes[a + 15];
                uint typed = U32(nodes, a + 16);
                int rawIndex = raw == NoIndex ? -1 : (int)raw;
                int typedIndex = dataType == TypeString ? (int)typed : -1;
                result.Add(new Attribute
                {
                    Element = element,
                    Namespace = Str(U32(nodes, a)) ?? "",
                    Name = Str(nameIndex),
                    NameIndex = nameIndex == NoIndex ? -1 : (int)nameIndex,
                    ResourceId = nameIndex == NoIndex ? 0 : ResourceId((int)nameIndex),
                    RawValue = rawIndex,
                    TypedString = typedIndex,
                    DataType = dataType,
                    Data = typed,
                    Value = rawIndex >= 0 || typedIndex >= 0 ? Str((uint)(rawIndex >= 0 ? rawIndex : typedIndex)) : null,
                });
            }
            return result;
        }

        /// <summary>Builds (once) and returns every element, removed ones included, checking the node structure.</summary>
        private List<Element> Tree()
        {
            if (elements != null) return elements;
            var result = new List<Element>();
            // Open scopes, innermost last: an element (ns, name) or a namespace (prefix, uri).
            var open = new List<(bool IsElement, uint A, uint B, Element Element)>();
            Element current = null;
            int roots = 0;
            int at = 0;
            int lead = -1;   // offset of the START_NAMESPACE run just before the next START_ELEMENT
            while (at < tail.Length)
            {
                if (at + 8 > tail.Length) throw Bad("truncated XML node chunk at " + at);
                ushort type = U16(tail, at);
                int headerSize = U16(tail, at + 2);
                long size = U32(tail, at + 4);
                if (headerSize < 8 || size < headerSize || at + size > tail.Length) throw Bad("XML node chunk at " + at + " out of range");
                if (type == ResourceMapType)
                {
                    if (at != 0) throw Bad("resource map chunk at " + at + " is not before the first XML node");
                    at += (int)size;
                    continue;
                }
                if (type < StartNamespaceType || type > CDataType) throw Bad("unknown XML node chunk type 0x" + type.ToString("x4") + " at " + at);
                if (headerSize < NodeHeaderSize) throw Bad("XML node chunk at " + at + " has a " + headerSize + "-byte header");
                if (roots == 1 && open.Count == 0) throw Bad("XML node chunk at " + at + " after the root element");
                int ext = at + headerSize;
                if (type != StartNamespaceType && type != StartElementType) lead = -1;
                switch (type)
                {
                    case StartNamespaceType:
                        Need(ext + 8, at, size, "namespace");
                        open.Add((false, U32(tail, ext), U32(tail, ext + 4), null));
                        if (lead < 0) lead = at;
                        break;
                    case EndNamespaceType:
                    {
                        Need(ext + 8, at, size, "namespace");
                        var top = open.Count > 0 ? open[open.Count - 1] : default;
                        if (open.Count == 0 || top.IsElement || top.A != U32(tail, ext) || top.B != U32(tail, ext + 4))
                            throw Bad("END_NAMESPACE at " + at + " does not close the innermost open scope");
                        open.RemoveAt(open.Count - 1);
                        break;
                    }
                    case StartElementType:
                    {
                        Need(ext + AttributeExtSize, at, size, "start element");
                        uint ns = U32(tail, ext), name = U32(tail, ext + 4);
                        string tag = Str(name);
                        if (tag == null) throw Bad("start element at " + at + " has no name");
                        if (ns != NoIndex && Str(ns) == null) throw Bad("start element <" + tag + "> at " + at + " has an invalid namespace");
                        if (current == null) roots++;
                        var element = new Element
                        {
                            Index = result.Count,
                            Name = tag,
                            Namespace = Str(ns) ?? "",
                            Depth = current == null ? 0 : current.Depth + 1,
                            Parent = current,
                            Line = (int)U32(tail, at + 8),
                            Attributes = ReadAttributes(tail, at, headerSize, size),
                            Start = at,
                            LeadStart = lead >= 0 ? lead : at,
                            Scope = open.Where(o => !o.IsElement).Select(o => o.B).ToList(),
                        };
                        lead = -1;
                        element.AndroidName = AndroidNameOf(element);
                        result.Add(element);
                        open.Add((true, ns, name, element));
                        current = element;
                        break;
                    }
                    case EndElementType:
                    {
                        Need(ext + 8, at, size, "end element");
                        var top = open.Count > 0 ? open[open.Count - 1] : default;
                        if (open.Count == 0 || !top.IsElement || top.A != U32(tail, ext) || top.B != U32(tail, ext + 4))
                            throw Bad("END_ELEMENT at " + at + " does not close the innermost open element" + (current == null ? "" : " " + current));
                        open.RemoveAt(open.Count - 1);
                        top.Element.EndStart = at;
                        top.Element.End = at + (int)size;
                        current = top.Element.Parent;
                        break;
                    }
                    default: // CDATA
                        Need(ext + 12, at, size, "CDATA");
                        if (current == null) throw Bad("CDATA at " + at + " outside the root element");
                        break;
                }
                at += (int)size;
            }
            if (open.Count > 0) throw Bad("document ends inside " + (current != null ? current.ToString() : "a namespace scope"));
            if (roots != 1) throw Bad("expected one root element, found " + roots);
            elements = result;
            return elements;
        }

        private static void Need(long end, int at, long size, string what)
        {
            if (end > at + size) throw Bad(what + " chunk at " + at + " too small");
        }

        /// <summary>
        /// The literal android:name of an element. PackageManager reads it by resource id, so an attribute counts when its
        /// name maps to 0x01010003; that attribute must also be "name" in the android namespace and be the only one, and
        /// its raw and typed strings must agree (anything else is refused rather than guessed).
        /// </summary>
        private static string AndroidNameOf(Element element)
        {
            var names = element.Attributes.Where(a => a.ResourceId == AndroidNameResourceId || (a.Namespace == AndroidNamespace && a.Name == "name")).ToList();
            if (names.Count == 0) return null;
            if (names.Count > 1) throw Bad(element + " has " + names.Count + " android:name attributes");
            Attribute name = names[0];
            if (name.ResourceId != AndroidNameResourceId || name.Namespace != AndroidNamespace || name.Name != "name")
                throw Bad(element + ": attribute " + name.Namespace + ":" + name.Name + " (resource id 0x" + name.ResourceId.ToString("x8") + ") is not a plain android:name");
            if (name.TypedString < 0) return null;
            if (name.RawValue >= 0 && name.RawValue != name.TypedString) throw Bad(element + ": android:name has different raw and typed values");
            return name.Value;
        }

        /// <summary>
        /// The node sequence (resource map and XML nodes) with the removed elements' byte ranges cut out and, when
        /// <paramref name="additions"/>, the additions applied: string references moved past inserted framework names, the
        /// map grown, values set, attributes and elements inserted. Without additions or removals it is the input's.
        /// </summary>
        private byte[] Nodes(bool additions)
        {
            bool cut = elements != null && elements.Any(e => e.Removed);
            additions &= Grows;
            if (!cut && !additions) return tail;
            if (additions) Tree();   // the chunks are rewritten by type: the node structure must be sound
            // Only the outermost removed elements; an insertion inside one goes with it.
            Dictionary<int, int> cuts = (elements ?? new List<Element>()).Where(e => e.Removed && (e.Parent == null || !e.Parent.Removed))
                                                                         .ToDictionary(e => e.Start, e => e.End);
            var output = new MemoryStream(tail.Length + 1024);
            int at = 0;
            while (at < tail.Length)
            {
                if (additions)
                {
                    foreach (var insertion in insertions.Where(i => i.Offset == at)) WriteElement(output, insertion.Element, insertion.Scope);
                }
                if (cuts.TryGetValue(at, out int end))
                {
                    at = end;
                    continue;
                }
                int size = (int)U32(tail, at + 4);
                if (!additions)
                {
                    output.Write(tail, at, size);
                    at += size;
                    continue;
                }
                byte[] chunk = Slice(tail, at, size);
                if (U16(chunk, 0) == ResourceMapType)
                {
                    chunk = GrowResourceMap(chunk);
                }
                else
                {
                    if (insertedNames.Count > 0) RemapNode(chunk);
                    if (edits.TryGetValue(at, out ElementEdit edit)) chunk = EditStartElement(chunk, edit);
                }
                output.Write(chunk, 0, chunk.Length);
                at += size;
            }
            return output.ToArray();
        }

        /// <summary>The document with the current strings, without the removed elements and with the additions.</summary>
        public byte[] ToArray()
        {
            int poolGrowth = insertedNames.Count + appendedStrings.Count;
            byte[] nodes = Nodes(true);   // first: it must not need any string that was not added before
            if (insertedNames.Count + appendedStrings.Count != poolGrowth) throw new InvalidOperationException("a string was added while writing");

            var data = new MemoryStream();
            var offsets = new List<uint>(strings.Length + poolGrowth);
            var placed = new Dictionary<uint, uint>(); // original offset -> new offset, for strings shared by several indices
            void Add(string value)
            {
                offsets.Add((uint)data.Length);
                byte[] bytes = utf8 ? EncodeUtf8(value) : EncodeUtf16(value);
                data.Write(bytes, 0, bytes.Length);
            }
            for (int i = 0; i < strings.Length; i++)
            {
                if (i == resourceMapCount) foreach (var name in insertedNames) Add(name.Name);
                uint shared;
                if (!replaced[i] && placed.TryGetValue(originalOffsets[i], out shared))
                {
                    offsets.Add(shared);
                    continue;
                }
                uint offset = (uint)data.Length;
                offsets.Add(offset);
                byte[] bytes = replaced[i] ? (utf8 ? EncodeUtf8(strings[i]) : EncodeUtf16(strings[i])) : encoded[i];
                data.Write(bytes, 0, bytes.Length);
                if (!replaced[i]) placed[originalOffsets[i]] = offset;
            }
            if (resourceMapCount == strings.Length) foreach (var name in insertedNames) Add(name.Name);
            foreach (string value in appendedStrings) Add(value);
            while (data.Length % 4 != 0) data.WriteByte(0);

            int stringCount = offsets.Count;
            int styleCount = styleOffsets.Length / 4;
            long stringsStart = poolHeader.Length + 4L * (stringCount + styleCount);
            long stylesStart = styleCount > 0 ? stringsStart + data.Length : 0;
            long poolSize = stringsStart + data.Length + styleData.Length;

            var header = (byte[])poolHeader.Clone();
            Put32(header, 4, poolSize);
            Put32(header, 8, stringCount);
            Put32(header, 20, stringCount > 0 ? stringsStart : 0);
            Put32(header, 24, stylesStart);
            bool anyReplaced = Array.IndexOf(replaced, true) >= 0;
            // A renamed or added string may break the sort order.
            if (anyReplaced || poolGrowth > 0) Put32(header, 16, U32(header, 16) & ~SortedFlag);

            var output = new MemoryStream();
            var xml = (byte[])xmlHeader.Clone();
            Put32(xml, 4, xmlHeaderSize + poolSize + nodes.Length);
            output.Write(xml, 0, xml.Length);
            output.Write(header, 0, header.Length);
            var offsetBytes = new byte[4];
            foreach (uint o in offsets)
            {
                Put32(offsetBytes, 0, o);
                output.Write(offsetBytes, 0, 4);
            }
            output.Write(styleOffsets, 0, styleOffsets.Length);
            data.WriteTo(output);
            output.Write(styleData, 0, styleData.Length);
            output.Write(nodes, 0, nodes.Length);
            return output.ToArray();
        }

        // ------------------------------------------------------------------ additions

        /// <summary>A typed attribute value (Res_value) for <see cref="InsertAttribute"/> and <see cref="InsertElement"/>.</summary>
        public sealed class Value
        {
            private Value(byte dataType, uint data, string text)
            {
                DataType = dataType;
                Data = data;
                Text = text;
            }

            public byte DataType { get; }

            /// <summary>The Res_value data; for a string, its pool index, set on output.</summary>
            public uint Data { get; }

            /// <summary>The string of a TYPE_STRING value (also written as the raw value, as aapt does), else null.</summary>
            public string Text { get; }

            /// <summary>TYPE_INT_BOOLEAN, as aapt writes it: 0xFFFFFFFF for true, 0 for false.</summary>
            public static Value Boolean(bool value)
            {
                return new Value(TypeIntBoolean, value ? 0xFFFFFFFFu : 0u, null);
            }

            /// <summary>TYPE_INT_DEC (also an enum or flag value).</summary>
            public static Value Integer(int value)
            {
                return new Value(TypeIntDec, unchecked((uint)value), null);
            }

            public static Value String(string value)
            {
                return new Value(TypeString, 0, value ?? throw new ArgumentNullException(nameof(value)));
            }

            public override string ToString()
            {
                if (Text != null) return "\"" + Text + "\"";
                if (DataType == TypeIntBoolean) return Data != 0 ? "true" : "false";
                return DataType == TypeIntDec ? ((int)Data).ToString(System.Globalization.CultureInfo.InvariantCulture) : "0x" + DataType.ToString("x2") + ":" + Data.ToString("x8");
            }
        }

        /// <summary>An attribute to insert.</summary>
        public sealed class NewAttribute
        {
            /// <param name="ns">Namespace URI, "" for none; it must be in scope where the attribute goes.</param>
            /// <param name="resourceId">Framework (or app) attribute id the name is mapped to, 0 for none; required in the android namespace.</param>
            public NewAttribute(string ns, string name, uint resourceId, Value value)
            {
                Namespace = ns ?? throw new ArgumentNullException(nameof(ns));
                Name = string.IsNullOrEmpty(name) ? throw new ArgumentException("attribute name missing", nameof(name)) : name;
                ResourceId = resourceId;
                Value = value ?? throw new ArgumentNullException(nameof(value));
            }

            public string Namespace { get; }

            public string Name { get; }

            public uint ResourceId { get; }

            public Value Value { get; }

            /// <summary>An attribute in the android namespace, read by the platform through <paramref name="resourceId"/>.</summary>
            public static NewAttribute Android(string name, uint resourceId, Value value)
            {
                return new NewAttribute(AndroidNamespace, name, resourceId, value);
            }

            public override string ToString()
            {
                return (Namespace == AndroidNamespace ? "android:" : Namespace.Length > 0 ? "{" + Namespace + "}" : "") + Name + "=" + Value;
            }
        }

        /// <summary>An element (with its children) to insert.</summary>
        public sealed class NewElement
        {
            /// <param name="line">Source line number recorded in its nodes (aapt's; the platform only shows it in parse errors).</param>
            public NewElement(string name, int line, params NewAttribute[] attributes)
            {
                Name = string.IsNullOrEmpty(name) ? throw new ArgumentException("element name missing", nameof(name)) : name;
                Line = line;
                Attributes = (attributes ?? new NewAttribute[0]).ToList();
            }

            public string Name { get; }

            public int Line { get; }

            public IReadOnlyList<NewAttribute> Attributes { get; }

            public List<NewElement> Children { get; } = new List<NewElement>();

            public NewElement Add(NewElement child)
            {
                Children.Add(child ?? throw new ArgumentNullException(nameof(child)));
                return this;
            }

            public override string ToString()
            {
                return "<" + Name + (Attributes.Count == 0 ? "" : " " + string.Join(" ", Attributes)) + (Children.Count == 0 ? "/>" : ">" + string.Concat(Children) + "</" + Name + ">");
            }
        }

        /// <summary>The value changes and inserted attributes of one start element.</summary>
        private sealed class ElementEdit
        {
            public ElementEdit(Element element)
            {
                Element = element;
            }

            public Element Element { get; }

            /// <summary>Attribute index (as read) -> new Res_value data.</summary>
            public Dictionary<int, uint> Data { get; } = new Dictionary<int, uint>();

            public List<NewAttribute> Added { get; } = new List<NewAttribute>();
        }

        /// <summary>A pool string as additions refer to it until the output's indices are known.</summary>
        private struct PoolRef
        {
            public PoolRef(int kind, int index)
            {
                Kind = kind;
                Index = index;
            }

            /// <summary>0: an original string, 1: an inserted framework name, 2: an appended string.</summary>
            public int Kind { get; }

            public int Index { get; }
        }

        /// <summary>True once anything was added (strings, values, attributes or elements).</summary>
        public bool Grows
        {
            get { return insertedNames.Count > 0 || appendedStrings.Count > 0 || edits.Count > 0 || insertions.Count > 0; }
        }

        /// <summary>
        /// Makes sure the pool holds <paramref name="value"/> outside the resource-mapped region, where element names, values
        /// and attribute names without a framework id belong: an existing string is reused, otherwise it is appended at the
        /// end of the pool on output (no index moves).
        /// </summary>
        public void AppendString(string value)
        {
            TextRef(value);
        }

        /// <summary>
        /// Makes sure the pool holds the attribute name <paramref name="name"/> mapped to <paramref name="resourceId"/>: an
        /// existing mapped string is reused, otherwise the name is inserted right after the last mapped string, and on output
        /// the resource map grows by the id and every reference to a later string moves up by one. Refused for a pool with
        /// styled strings (their spans and their pairing with the first strings would move) or without a resource map chunk.
        /// </summary>
        public void InsertAttributeName(string name, uint resourceId)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (resourceId == 0) throw new ArgumentException("a mapped attribute name needs a resource id", nameof(resourceId));
            NameRef(name, resourceId);
        }

        /// <summary>
        /// Sets the value of an existing integer attribute (TYPE_INT_DEC or TYPE_INT_HEX; the type stays). Refused for any
        /// other type and for an attribute that also keeps its text as a raw value, which would then disagree.
        /// </summary>
        public void SetInt(Element element, string ns, string name, int value)
        {
            CheckEditable(element);
            List<Attribute> found = element.Attributes.Where(a => a.Namespace == ns && a.Name == name).ToList();
            if (found.Count != 1) throw new InvalidOperationException(element + " has " + found.Count + " attributes " + ns + ":" + name);
            Attribute attribute = found[0];
            if (attribute.DataType != TypeIntDec && attribute.DataType != TypeIntHex) throw new InvalidOperationException(attribute + " is not an integer");
            if (attribute.RawValue >= 0) throw new InvalidOperationException(attribute + " also has a raw string value");
            Edit(element).Data[element.Attributes.ToList().IndexOf(attribute)] = unchecked((uint)value);
        }

        /// <summary>
        /// Inserts <paramref name="attribute"/> into <paramref name="element"/> at the place the platform expects (see the
        /// class remarks). Refused when the element already has that attribute (same name, or same resource id), when the
        /// namespace is not in scope there, and for the no-namespace id/class/style attributes (indexed in the element header).
        /// </summary>
        public void InsertAttribute(Element element, NewAttribute attribute)
        {
            CheckEditable(element);
            if (attribute == null) throw new ArgumentNullException(nameof(attribute));
            ElementEdit edit = edits.TryGetValue(element.Start, out ElementEdit existing) ? existing : null;
            bool Same(uint id, string ns, string name) => (id != 0 && id == attribute.ResourceId) || (ns == attribute.Namespace && name == attribute.Name);
            if (element.Attributes.Any(a => Same(a.ResourceId, a.Namespace, a.Name)) || (edit != null && edit.Added.Any(a => Same(a.ResourceId, a.Namespace, a.Name))))
                throw new InvalidOperationException(element + " already has an attribute " + attribute.Name + (attribute.ResourceId == 0 ? "" : " (0x" + attribute.ResourceId.ToString("x8") + ")"));
            Check(attribute, element);
            Reserve(attribute);
            Edit(element).Added.Add(attribute);
        }

        /// <summary>
        /// Inserts <paramref name="element"/> (with its children) into <paramref name="parent"/>: before the child
        /// <paramref name="before"/> (ahead of the namespace scopes declared around it), or as the last child when it is null.
        /// Its attributes may only use namespaces in scope at <paramref name="parent"/>.
        /// </summary>
        public void InsertElement(Element parent, NewElement element, Element before = null)
        {
            CheckEditable(parent);
            if (element == null) throw new ArgumentNullException(nameof(element));
            if (before != null)
            {
                CheckEditable(before);
                if (!ReferenceEquals(before.Parent, parent)) throw new ArgumentException(before + " is not a child of " + parent, nameof(before));
            }
            CheckTree(element, parent);
            ReserveTree(element);
            insertions.Add((before != null ? before.LeadStart : parent.EndStart, element, parent));
        }

        private void CheckTree(NewElement element, Element scope)
        {
            CheckLength(element.Name);
            for (int i = 0; i < element.Attributes.Count; i++)
            {
                NewAttribute a = element.Attributes[i];
                for (int j = 0; j < i; j++)
                {
                    NewAttribute b = element.Attributes[j];
                    if ((a.ResourceId != 0 && a.ResourceId == b.ResourceId) || (a.Namespace == b.Namespace && a.Name == b.Name))
                        throw new InvalidOperationException("<" + element.Name + "> lists attribute " + a.Name + " twice");
                }
                Check(a, scope);
            }
            foreach (NewElement child in element.Children) CheckTree(child, scope);
        }

        private void ReserveTree(NewElement element)
        {
            TextRef(element.Name);
            foreach (NewAttribute a in element.Attributes) Reserve(a);
            foreach (NewElement child in element.Children) ReserveTree(child);
        }

        /// <summary>Refuses an attribute that cannot be written at <paramref name="scope"/>; changes nothing.</summary>
        private void Check(NewAttribute attribute, Element scope)
        {
            if (attribute.Namespace.Length == 0 && (attribute.Name == "id" || attribute.Name == "class" || attribute.Name == "style"))
                throw new InvalidOperationException("inserting the no-namespace attribute '" + attribute.Name + "' is not supported");
            if (attribute.Namespace == AndroidNamespace && attribute.ResourceId == 0)
                throw new InvalidOperationException("android:" + attribute.Name + " needs its framework resource id: the platform reads it by id");
            if (attribute.Namespace.Length > 0) NamespaceIndex(scope, attribute.Namespace);
            NameRef(attribute.Name, attribute.ResourceId, false);
            CheckLength(attribute.Name);
            if (attribute.Value.Text != null) CheckLength(attribute.Value.Text);
        }

        /// <summary>Adds the pool strings a checked attribute needs, so ToArray never has to.</summary>
        private void Reserve(NewAttribute attribute)
        {
            NameRef(attribute.Name, attribute.ResourceId);
            if (attribute.Value.Text != null) TextRef(attribute.Value.Text);
        }

        /// <summary>Pool index of the in-scope namespace URI <paramref name="ns"/> at <paramref name="scope"/> (innermost first).</summary>
        private uint NamespaceIndex(Element scope, string ns)
        {
            for (int i = scope.Scope.Count - 1; i >= 0; i--)
            {
                if (Str(scope.Scope[i]) == ns) return scope.Scope[i];
            }
            throw new InvalidOperationException("namespace " + ns + " is not declared in scope at " + scope);
        }

        private void CheckEditable(Element element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));
            List<Element> index = Tree();
            if (element.Index < 0 || element.Index >= index.Count || !ReferenceEquals(index[element.Index], element))
                throw new ArgumentException(element + " is not an element of this document", nameof(element));
            if (element.Removed) throw new InvalidOperationException(element + " was removed");
        }

        private ElementEdit Edit(Element element)
        {
            if (!edits.TryGetValue(element.Start, out ElementEdit edit)) edits[element.Start] = edit = new ElementEdit(element);
            return edit;
        }

        /// <summary>A string outside the mapped region: an original one with that text, else an appended one.</summary>
        private PoolRef TextRef(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            for (int i = resourceMapCount; i < strings.Length; i++)
            {
                if (strings[i] == value) return new PoolRef(0, i);
            }
            int at = appendedStrings.IndexOf(value);
            if (at >= 0) return new PoolRef(2, at);
            CheckLength(value);
            appendedStrings.Add(value);
            return new PoolRef(2, appendedStrings.Count - 1);
        }

        /// <summary>
        /// An attribute name: mapped to <paramref name="resourceId"/> when it is not 0, else an ordinary string. With
        /// <paramref name="add"/> false it only refuses what could not be added and returns a dummy for a string not there yet.
        /// </summary>
        private PoolRef NameRef(string name, uint resourceId, bool add = true)
        {
            if (resourceId == 0) return add ? TextRef(name) : default(PoolRef);
            for (int i = 0; i < resourceMapCount && i < strings.Length; i++)
            {
                if (ResourceId(i) != resourceId) continue;
                if (strings[i] != name) throw new InvalidOperationException("resource id 0x" + resourceId.ToString("x8") + " is mapped to '" + strings[i] + "', not '" + name + "'");
                return new PoolRef(0, i);
            }
            int at = insertedNames.FindIndex(n => n.Id == resourceId);
            if (at >= 0)
            {
                if (insertedNames[at].Name != name) throw new InvalidOperationException("resource id 0x" + resourceId.ToString("x8") + " was already added as '" + insertedNames[at].Name + "'");
                return new PoolRef(1, at);
            }
            if (styleOffsets.Length > 0) throw new InvalidOperationException("the pool has styled strings: a mapped attribute name cannot be inserted");
            if (tail.Length < 8 || U16(tail, 0) != ResourceMapType || resourceMapCount > strings.Length)
                throw new InvalidOperationException("the document has no usable resource map: a mapped attribute name cannot be inserted");
            CheckLength(name);
            if (!add) return default(PoolRef);
            insertedNames.Add((name, resourceId));
            return new PoolRef(1, insertedNames.Count - 1);
        }

        private void CheckLength(string value)
        {
            if (value.Length > 0x7FFF || (utf8 && Encoding.UTF8.GetByteCount(value) > 0x7FFF)) throw new ArgumentException("string too long for the pool", nameof(value));
        }

        /// <summary>The output index of a pool string.</summary>
        private uint FinalIndex(PoolRef r)
        {
            switch (r.Kind)
            {
                case 0: return Remap((uint)r.Index);
                case 1: return (uint)(resourceMapCount + r.Index);
                default: return (uint)(strings.Length + insertedNames.Count + r.Index);
            }
        }

        /// <summary>The output index of original pool string <paramref name="index"/>: past the mapped region it moves up by the inserted names.</summary>
        private uint Remap(uint index)
        {
            if (index == NoIndex) return index;
            if (index >= strings.Length) throw Bad("string reference " + index + " out of range (" + strings.Length + " strings)");
            return index < resourceMapCount ? index : index + (uint)insertedNames.Count;
        }

        private void RemapAt(byte[] chunk, int at)
        {
            Put32(chunk, at, Remap(U32(chunk, at)));
        }

        /// <summary>Moves every string reference of an original XML node chunk to its output index.</summary>
        private void RemapNode(byte[] chunk)
        {
            ushort type = U16(chunk, 0);
            int ext = U16(chunk, 2);
            RemapAt(chunk, 12);   // the node's comment
            switch (type)
            {
                case StartNamespaceType:
                case EndNamespaceType:
                case EndElementType:
                    RemapAt(chunk, ext);       // prefix / namespace
                    RemapAt(chunk, ext + 4);   // uri / name
                    break;
                case StartElementType:
                {
                    RemapAt(chunk, ext);
                    RemapAt(chunk, ext + 4);
                    int attributeStart = U16(chunk, ext + 8), attributeSize = U16(chunk, ext + 10), count = U16(chunk, ext + 12);
                    for (int i = 0; i < count; i++)
                    {
                        int a = ext + attributeStart + i * attributeSize;
                        RemapAt(chunk, a);       // namespace
                        RemapAt(chunk, a + 4);   // name
                        RemapAt(chunk, a + 8);   // raw value
                        if (chunk[a + 15] == TypeString) RemapAt(chunk, a + 16);
                    }
                    break;
                }
                case CDataType:
                    RemapAt(chunk, ext);
                    if (chunk[ext + 7] == TypeString) RemapAt(chunk, ext + 8);
                    break;
            }
        }

        /// <summary>The resource map chunk with the inserted names' ids after the original ones.</summary>
        private byte[] GrowResourceMap(byte[] chunk)
        {
            if (insertedNames.Count == 0) return chunk;
            int end = U16(chunk, 2) + 4 * resourceMapCount;
            var result = new byte[chunk.Length + 4 * insertedNames.Count];
            Buffer.BlockCopy(chunk, 0, result, 0, end);
            for (int i = 0; i < insertedNames.Count; i++) Put32(result, end + 4 * i, insertedNames[i].Id);
            Buffer.BlockCopy(chunk, end, result, end + 4 * insertedNames.Count, chunk.Length - end);
            Put32(result, 4, result.Length);
            return result;
        }

        /// <summary>
        /// A start element chunk (its references already at their output indices) with its value changes and inserted
        /// attributes; the id/class/style indices of the header follow their attributes.
        /// </summary>
        private byte[] EditStartElement(byte[] chunk, ElementEdit edit)
        {
            int ext = U16(chunk, 2);
            int attributeStart = U16(chunk, ext + 8), attributeSize = U16(chunk, ext + 10), count = U16(chunk, ext + 12);
            int first = ext + attributeStart;
            foreach (var set in edit.Data) Put32(chunk, first + set.Key * attributeSize + 16, set.Value);
            if (edit.Added.Count == 0) return chunk;
            if (attributeSize != AttributeSize) throw Bad(edit.Element + " has " + attributeSize + "-byte attributes; cannot insert one");

            var merged = edit.Element.Attributes.Select((a, i) => (Id: a.ResourceId, Original: i, Bytes: Slice(chunk, first + i * attributeSize, attributeSize))).ToList();
            foreach (NewAttribute added in edit.Added)
            {
                var bytes = new byte[AttributeSize];
                WriteAttribute(bytes, 0, added, edit.Element);
                int position = added.ResourceId == 0 ? -1 : merged.FindIndex(m => m.Id == 0 || m.Id > added.ResourceId);
                merged.Insert(position < 0 ? merged.Count : position, (added.ResourceId, -1, bytes));
            }
            int end = first + count * attributeSize;
            var output = new MemoryStream(chunk.Length + AttributeSize * edit.Added.Count);
            output.Write(chunk, 0, first);
            foreach (var m in merged) output.Write(m.Bytes, 0, m.Bytes.Length);
            output.Write(chunk, end, chunk.Length - end);
            byte[] result = output.ToArray();
            Put32(result, 4, result.Length);
            Put16(result, ext + 12, merged.Count);
            for (int field = ext + 14; field <= ext + 18; field += 2)   // idIndex, classIndex, styleIndex (1-based, 0 = none)
            {
                int index = U16(result, field);
                if (index != 0) Put16(result, field, merged.FindIndex(m => m.Original == index - 1) + 1);
            }
            return result;
        }

        /// <summary>One ResXMLTree_attribute of an added attribute, at its output indices.</summary>
        private void WriteAttribute(byte[] target, int at, NewAttribute attribute, Element scope)
        {
            Put32(target, at, attribute.Namespace.Length == 0 ? NoIndex : Remap(NamespaceIndex(scope, attribute.Namespace)));
            Put32(target, at + 4, FinalIndex(NameRef(attribute.Name, attribute.ResourceId)));
            uint text = attribute.Value.Text == null ? NoIndex : FinalIndex(TextRef(attribute.Value.Text));
            Put32(target, at + 8, text);                                   // raw value
            Put16(target, at + 12, 8);                                     // Res_value size
            target[at + 14] = 0;
            target[at + 15] = attribute.Value.DataType;
            Put32(target, at + 16, attribute.Value.Text == null ? attribute.Value.Data : text);
        }

        /// <summary>
        /// An added element and its children as node chunks (16-byte node headers, no comment, 20-byte attributes sorted
        /// like aapt's: framework ids ascending, then the others in the given order).
        /// </summary>
        private void WriteElement(Stream output, NewElement element, Element scope)
        {
            List<NewAttribute> attributes = element.Attributes.Where(a => a.ResourceId != 0).OrderBy(a => a.ResourceId)
                                                   .Concat(element.Attributes.Where(a => a.ResourceId == 0)).ToList();
            var start = new byte[NodeHeaderSize + AttributeExtSize + AttributeSize * attributes.Count];
            NodeHeader(start, StartElementType, element.Line);
            int ext = NodeHeaderSize;
            Put32(start, ext, NoIndex);                                    // no namespace, like every manifest/layout element
            Put32(start, ext + 4, FinalIndex(TextRef(element.Name)));
            Put16(start, ext + 8, AttributeExtSize);                       // attributeStart
            Put16(start, ext + 10, AttributeSize);
            Put16(start, ext + 12, attributes.Count);                      // id/class/style indices stay 0
            for (int i = 0; i < attributes.Count; i++) WriteAttribute(start, ext + AttributeExtSize + i * AttributeSize, attributes[i], scope);
            output.Write(start, 0, start.Length);

            foreach (NewElement child in element.Children) WriteElement(output, child, scope);

            var end = new byte[NodeHeaderSize + 8];
            NodeHeader(end, EndElementType, element.Line);
            Put32(end, NodeHeaderSize, NoIndex);
            Put32(end, NodeHeaderSize + 4, FinalIndex(TextRef(element.Name)));
            output.Write(end, 0, end.Length);
        }

        private static void NodeHeader(byte[] chunk, ushort type, int line)
        {
            Put16(chunk, 0, type);
            Put16(chunk, 2, NodeHeaderSize);
            Put32(chunk, 4, chunk.Length);
            Put32(chunk, 8, line);
            Put32(chunk, 12, NoIndex);                                     // comment
        }

        // ------------------------------------------------------------------ encoding

        private string Str(uint index)
        {
            return index == NoIndex || index >= strings.Length ? null : strings[index];
        }

        private static int DecodeUtf16(byte[] data, int at, long end, out string value)
        {
            int p = at;
            int length = Need16(data, ref p, end);
            if ((length & 0x8000) != 0) length = ((length & 0x7FFF) << 16) | Need16(data, ref p, end);
            if (p + 2L * length + 2 > end) throw Bad("UTF-16 string at " + at + " runs past the string data");
            value = Encoding.Unicode.GetString(data, p, 2 * length);
            return p + 2 * length + 2 - at; // + the 0x0000 terminator
        }

        private static int DecodeUtf8(byte[] data, int at, long end, out string value)
        {
            int p = at;
            Utf8Length(data, ref p, end);              // length in UTF-16 units (not needed to decode)
            int bytes = Utf8Length(data, ref p, end);  // length in bytes
            if (p + (long)bytes + 1 > end) throw Bad("UTF-8 string at " + at + " runs past the string data");
            value = Encoding.UTF8.GetString(data, p, bytes);
            return p + bytes + 1 - at; // + the 0x00 terminator
        }

        private static int Utf8Length(byte[] data, ref int p, long end)
        {
            if (p >= end) throw Bad("truncated UTF-8 string length");
            int length = data[p++];
            if ((length & 0x80) != 0)
            {
                if (p >= end) throw Bad("truncated UTF-8 string length");
                length = ((length & 0x7F) << 8) | data[p++];
            }
            return length;
        }

        private static int Need16(byte[] data, ref int p, long end)
        {
            if (p + 2 > end) throw Bad("truncated UTF-16 string length");
            int v = U16(data, p);
            p += 2;
            return v;
        }

        private static byte[] EncodeUtf16(string value)
        {
            var output = new MemoryStream();
            int length = value.Length;
            if (length > 0x7FFF)
            {
                Write16(output, 0x8000 | (length >> 16));
                Write16(output, length & 0xFFFF);
            }
            else
            {
                Write16(output, length);
            }
            byte[] chars = Encoding.Unicode.GetBytes(value);
            output.Write(chars, 0, chars.Length);
            Write16(output, 0);
            return output.ToArray();
        }

        private static byte[] EncodeUtf8(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > 0x7FFF) throw new ArgumentException("string too long for a UTF-8 pool");
            var output = new MemoryStream();
            WriteUtf8Length(output, value.Length);
            WriteUtf8Length(output, bytes.Length);
            output.Write(bytes, 0, bytes.Length);
            output.WriteByte(0);
            return output.ToArray();
        }

        private static void WriteUtf8Length(Stream output, int length)
        {
            if (length > 0x7F) output.WriteByte((byte)(0x80 | (length >> 8)));
            output.WriteByte((byte)(length & 0xFF));
        }

        private static void Write16(Stream output, int value)
        {
            output.WriteByte((byte)value);
            output.WriteByte((byte)(value >> 8));
        }

        private static ushort U16(byte[] data, int at)
        {
            return (ushort)(data[at] | data[at + 1] << 8);
        }

        private static uint U32(byte[] data, int at)
        {
            return (uint)(data[at] | data[at + 1] << 8 | data[at + 2] << 16 | data[at + 3] << 24);
        }

        private static void Put16(byte[] data, int at, int value)
        {
            data[at] = (byte)value;
            data[at + 1] = (byte)(value >> 8);
        }

        private static void Put32(byte[] data, int at, long value)
        {
            data[at] = (byte)value;
            data[at + 1] = (byte)(value >> 8);
            data[at + 2] = (byte)(value >> 16);
            data[at + 3] = (byte)(value >> 24);
        }

        private static byte[] Slice(byte[] data, int at, int length)
        {
            if (at < 0 || length < 0 || at + (long)length > data.Length) throw Bad("chunk out of range");
            var copy = new byte[length];
            Buffer.BlockCopy(data, at, copy, 0, length);
            return copy;
        }

        private static int Checked(uint value, string what)
        {
            if (value > 1000000) throw Bad(what + " " + value + " is implausible");
            return (int)value;
        }

        private static InvalidDataException Bad(string message)
        {
            return new InvalidDataException("AndroidManifest.xml: " + message);
        }
    }
}
