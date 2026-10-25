using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace McvsOffline.Patcher.Manifest
{
    /// <summary>
    /// A compiled (binary XML, "AXML") AndroidManifest.xml, as far as the patcher needs it: the string pool, which can be
    /// rewritten, and a read-only walk of the start elements and their attributes.
    /// <para>
    /// Layout (frameworks/base/libs/androidfw ResourceTypes.h): an RES_XML_TYPE chunk holding an RES_STRING_POOL_TYPE
    /// chunk, then the resource map and the namespace/element nodes. Element names, attribute names and string values
    /// all refer to the pool by index, so replacing the text of one pool string changes it everywhere it is used, while
    /// every byte after the pool is copied unchanged. Unchanged strings keep their exact encoded bytes, so
    /// <c>Parse(x).ToArray()</c> is <c>x</c> again.
    /// </para>
    /// </summary>
    public sealed class BinaryXml
    {
        private const ushort XmlType = 0x0003;
        private const ushort StringPoolType = 0x0001;
        private const ushort ResourceMapType = 0x0180;
        private const ushort StartElementType = 0x0102;
        private const uint SortedFlag = 0x1;
        private const uint Utf8Flag = 0x100;
        private const uint NoIndex = 0xFFFFFFFF;
        private const byte TypeString = 0x03;
        private const int PoolHeaderMinSize = 28;

        private readonly byte[] poolHeader;
        private readonly uint[] originalOffsets;
        private readonly byte[][] encoded;      // each string's bytes with its length prefix and terminator
        private readonly bool[] replaced;
        private readonly string[] strings;
        private readonly byte[] styleOffsets;
        private readonly byte[] styleData;
        private readonly byte[] tail;           // everything after the string pool chunk
        private readonly int xmlHeaderSize;
        private readonly byte[] xmlHeader;
        private readonly bool utf8;
        private readonly int resourceMapCount;

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

        /// <summary>Index of the first pool string equal to <paramref name="value"/>, or -1.</summary>
        public int IndexOf(string value)
        {
            return Array.IndexOf(strings, value);
        }

        /// <summary>Replaces the text of pool string <paramref name="index"/> (every use of it changes).</summary>
        public void Replace(int index, string value)
        {
            if (index < 0 || index >= strings.Length) throw new ArgumentOutOfRangeException(nameof(index));
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (index < resourceMapCount) throw new InvalidOperationException("string " + index + " ('" + strings[index] + "') is a framework attribute name");
            if (value.Length > 0x7FFF) throw new ArgumentException("string too long for the pool", nameof(value));
            strings[index] = value;
            replaced[index] = true;
        }

        /// <summary>A string attribute of a start element.</summary>
        public sealed class Attribute
        {
            public string Element { get; internal set; }

            /// <summary>Namespace URI ("" for none).</summary>
            public string Namespace { get; internal set; }

            public string Name { get; internal set; }

            /// <summary>Pool index of the raw value, or -1.</summary>
            public int RawValue { get; internal set; }

            /// <summary>Pool index of the typed string value (Res_value TYPE_STRING), or -1.</summary>
            public int TypedString { get; internal set; }

            /// <summary>The value's text, from the raw value or else the typed string.</summary>
            public string Value { get; internal set; }

            public override string ToString()
            {
                return "<" + Element + " " + (Namespace.Length > 0 ? "{" + Namespace + "}" : "") + Name + "=\"" + Value + "\">";
            }
        }

        /// <summary>Every attribute of every start element whose value is a pool string, in document order.</summary>
        public List<Attribute> StringAttributes()
        {
            var result = new List<Attribute>();
            int at = 0;
            while (at + 8 <= tail.Length)
            {
                ushort type = U16(tail, at);
                int headerSize = U16(tail, at + 2);
                long size = U32(tail, at + 4);
                if (size < 8 || at + size > tail.Length) throw Bad("XML node chunk at " + at + " out of range");
                if (type == StartElementType)
                {
                    int ext = at + headerSize;
                    if (headerSize < 8 || ext + 20 > at + size) throw Bad("start element chunk at " + at + " too small");
                    string element = Str(U32(tail, ext + 4));
                    int attributeStart = U16(tail, ext + 8);
                    int attributeSize = U16(tail, ext + 10);
                    int attributeCount = U16(tail, ext + 12);
                    for (int i = 0; i < attributeCount; i++)
                    {
                        int a = ext + attributeStart + i * attributeSize;
                        if (a + 20 > at + size) throw Bad("attribute " + i + " of <" + element + "> out of range");
                        uint raw = U32(tail, a + 8);
                        byte dataType = tail[a + 15];
                        uint typed = U32(tail, a + 16);
                        int rawIndex = raw == NoIndex ? -1 : (int)raw;
                        int typedIndex = dataType == TypeString ? (int)typed : -1;
                        if (rawIndex < 0 && typedIndex < 0) continue;
                        result.Add(new Attribute
                        {
                            Element = element,
                            Namespace = Str(U32(tail, a)) ?? "",
                            Name = Str(U32(tail, a + 4)),
                            RawValue = rawIndex,
                            TypedString = typedIndex,
                            Value = Str((uint)(rawIndex >= 0 ? rawIndex : typedIndex)),
                        });
                    }
                }
                at += (int)size;
            }
            return result;
        }

        /// <summary>The document with the current strings.</summary>
        public byte[] ToArray()
        {
            var data = new MemoryStream();
            var offsets = new uint[strings.Length];
            var placed = new Dictionary<uint, uint>(); // original offset -> new offset, for strings shared by several indices
            for (int i = 0; i < strings.Length; i++)
            {
                uint shared;
                if (!replaced[i] && placed.TryGetValue(originalOffsets[i], out shared))
                {
                    offsets[i] = shared;
                    continue;
                }
                offsets[i] = (uint)data.Length;
                byte[] bytes = replaced[i] ? (utf8 ? EncodeUtf8(strings[i]) : EncodeUtf16(strings[i])) : encoded[i];
                data.Write(bytes, 0, bytes.Length);
                if (!replaced[i]) placed[originalOffsets[i]] = offsets[i];
            }
            while (data.Length % 4 != 0) data.WriteByte(0);

            int stringCount = strings.Length;
            int styleCount = styleOffsets.Length / 4;
            long stringsStart = poolHeader.Length + 4L * (stringCount + styleCount);
            long stylesStart = styleCount > 0 ? stringsStart + data.Length : 0;
            long poolSize = stringsStart + data.Length + styleData.Length;

            var header = (byte[])poolHeader.Clone();
            Put32(header, 4, poolSize);
            Put32(header, 20, stringCount > 0 ? stringsStart : 0);
            Put32(header, 24, stylesStart);
            bool anyReplaced = Array.IndexOf(replaced, true) >= 0;
            if (anyReplaced) Put32(header, 16, U32(header, 16) & ~SortedFlag); // a renamed string may break the sort order

            var output = new MemoryStream();
            var xml = (byte[])xmlHeader.Clone();
            Put32(xml, 4, xmlHeaderSize + poolSize + tail.Length);
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
            output.Write(tail, 0, tail.Length);
            return output.ToArray();
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
