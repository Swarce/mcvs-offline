using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace McvsOffline.Patcher.Resources
{
    /// <summary>
    /// A compiled resource table (resources.arsc), as far as the art patch needs it: the chunks in their order, the global
    /// string pool (which can grow at its end), each package's type and key pools, and its typeSpec/type chunks, which can be
    /// replaced or have new chunks inserted after them (app-icon-and-launch-screen.md §5.2, risk R7: one shared reader/writer
    /// for every arsc edit, applied in one pass).
    /// <para>
    /// Layout (frameworks/base/libs/androidfw ResourceTypes.h): RES_TABLE_TYPE { global RES_STRING_POOL_TYPE, then one
    /// RES_TABLE_PACKAGE_TYPE per package { header with the offsets of its type and key string pools, the two pools, then
    /// per type a RES_TABLE_TYPE_SPEC_TYPE followed by its RES_TABLE_TYPE_TYPE chunks, one per configuration } }. Resource
    /// ids are positions (package id, type id, entry index), so as long as no typeSpec changes its entry count and no type
    /// is added, inserting a configuration chunk or appending strings moves no id. Every chunk the caller did not touch is
    /// written back with its exact bytes, so <c>Parse(x).ToArray()</c> is <c>x</c> again; only the sizes of the enclosing
    /// chunks (and the package header's pool offsets, which stay valid because nothing is inserted before the pools) are
    /// recomputed.
    /// </para>
    /// </summary>
    public sealed class ArscTable
    {
        public const ushort StringPoolType = 0x0001;
        public const ushort TableType = 0x0002;
        public const ushort PackageType = 0x0200;
        public const ushort TypeType = 0x0201;
        public const ushort TypeSpecType = 0x0202;

        /// <summary>An entry offset of a dense type chunk that means "no value in this configuration".</summary>
        public const uint NoEntry = 0xFFFFFFFF;

        private readonly byte[] header;

        /// <summary>The table's chunks in order: the global <see cref="StringPool"/>, the packages and anything else, raw.</summary>
        private readonly List<object> chunks;

        private ArscTable(byte[] header, List<object> chunks)
        {
            this.header = header;
            this.chunks = chunks;
        }

        public StringPool Strings { get { return chunks.OfType<StringPool>().First(); } }

        public IEnumerable<ArscPackage> Packages { get { return chunks.OfType<ArscPackage>(); } }

        public ArscPackage Package(uint id)
        {
            return Packages.FirstOrDefault(p => p.Id == id) ?? throw new InvalidDataException("resources.arsc has no package 0x" + id.ToString("x2"));
        }

        public static ArscTable Parse(byte[] data)
        {
            if (data == null || data.Length < 12) throw new InvalidDataException("resources.arsc is too short");
            ushort type = U16(data, 0), headerSize = U16(data, 2);
            uint size = U32(data, 4);
            if (type != TableType || headerSize < 12 || size != data.Length)
                throw new InvalidDataException("not a resource table (type 0x" + type.ToString("x4") + ", size " + size + " of " + data.Length + " bytes)");
            uint packageCount = U32(data, 8);
            var chunks = new List<object>();
            foreach (var (offset, chunkType, chunkSize) in Children(data, headerSize, data.Length, "resources.arsc"))
            {
                byte[] bytes = Slice(data, offset, chunkSize);
                if (chunkType == StringPoolType && !chunks.OfType<StringPool>().Any()) chunks.Add(StringPool.Parse(bytes));
                else if (chunkType == PackageType) chunks.Add(ArscPackage.Parse(bytes));
                else chunks.Add(new ArscChunk(bytes));
            }
            if (!chunks.OfType<StringPool>().Any()) throw new InvalidDataException("resources.arsc has no global string pool");
            if (chunks.OfType<ArscPackage>().Count() != packageCount)
                throw new InvalidDataException("resources.arsc declares " + packageCount + " packages, has " + chunks.OfType<ArscPackage>().Count());
            return new ArscTable(Slice(data, 0, headerSize), chunks);
        }

        public byte[] ToArray()
        {
            var output = new MemoryStream();
            output.Write(header, 0, header.Length);
            foreach (object chunk in chunks)
            {
                byte[] bytes = chunk is StringPool pool ? pool.ToArray() : chunk is ArscPackage package ? package.ToArray() : ((ArscChunk)chunk).Bytes;
                output.Write(bytes, 0, bytes.Length);
            }
            byte[] result = output.ToArray();
            Put32(result, 4, (uint)result.Length);
            return result;
        }

        /// <summary>The child chunks of [<paramref name="start"/>, <paramref name="end"/>): offset, type and size of each.</summary>
        internal static IEnumerable<(int Offset, ushort Type, int Size)> Children(byte[] data, int start, int end, string where)
        {
            int p = start;
            while (p < end)
            {
                if (end - p < 8) throw new InvalidDataException(where + ": truncated chunk header at 0x" + p.ToString("x"));
                ushort type = U16(data, p), headerSize = U16(data, p + 2);
                uint size = U32(data, p + 4);
                if (headerSize < 8 || size < headerSize || size > end - p || size % 4 != 0)
                    throw new InvalidDataException(where + ": bad chunk at 0x" + p.ToString("x") + " (type 0x" + type.ToString("x4") + ", header " + headerSize + ", size " + size + ")");
                yield return (p, type, (int)size);
                p += (int)size;
            }
        }

        internal static ushort U16(byte[] b, int at)
        {
            return (ushort)(b[at] | b[at + 1] << 8);
        }

        internal static uint U32(byte[] b, int at)
        {
            return (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24);
        }

        internal static void Put16(byte[] b, int at, ushort value)
        {
            b[at] = (byte)value;
            b[at + 1] = (byte)(value >> 8);
        }

        internal static void Put32(byte[] b, int at, uint value)
        {
            b[at] = (byte)value;
            b[at + 1] = (byte)(value >> 8);
            b[at + 2] = (byte)(value >> 16);
            b[at + 3] = (byte)(value >> 24);
        }

        internal static byte[] Slice(byte[] b, int offset, int count)
        {
            var result = new byte[count];
            Array.Copy(b, offset, result, 0, count);
            return result;
        }
    }

    /// <summary>A chunk kept as raw bytes (header included).</summary>
    public sealed class ArscChunk
    {
        public ArscChunk(byte[] bytes)
        {
            Bytes = bytes;
        }

        public byte[] Bytes { get; }

        public ushort Type { get { return ArscTable.U16(Bytes, 0); } }

        public ushort HeaderSize { get { return ArscTable.U16(Bytes, 2); } }
    }

    /// <summary>
    /// One RES_TABLE_PACKAGE_TYPE chunk: its header (kept as read, sizes and pool offsets recomputed on output) and its
    /// children. The type and key string pools are parsed for names; every other child stays raw and is reached through
    /// <see cref="TypeSpec"/> / <see cref="Types"/>.
    /// </summary>
    public sealed class ArscPackage
    {
        private readonly byte[] header;
        private readonly int typeStringsIndex, keyStringsIndex;

        private ArscPackage(byte[] header, List<ArscChunk> children, int typeStringsIndex, int keyStringsIndex)
        {
            this.header = header;
            Children = children;
            this.typeStringsIndex = typeStringsIndex;
            this.keyStringsIndex = keyStringsIndex;
            TypeStrings = StringPool.Parse(children[typeStringsIndex].Bytes);
            KeyStrings = StringPool.Parse(children[keyStringsIndex].Bytes);
        }

        public uint Id { get { return ArscTable.U32(header, 8); } }

        public string Name { get { return Encoding.Unicode.GetString(header, 12, 256).TrimEnd('\0'); } }

        /// <summary>The children in order; edits replace an element or insert new ones (never before the two pools).</summary>
        public List<ArscChunk> Children { get; }

        public StringPool TypeStrings { get; }

        public StringPool KeyStrings { get; }

        public static ArscPackage Parse(byte[] bytes)
        {
            ushort headerSize = ArscTable.U16(bytes, 2);
            if (headerSize < 284) throw new InvalidDataException("package header of " + headerSize + " bytes");
            uint typeStrings = ArscTable.U32(bytes, 268), keyStrings = ArscTable.U32(bytes, 276);
            var children = new List<ArscChunk>();
            int typeIndex = -1, keyIndex = -1;
            foreach (var (offset, _, size) in ArscTable.Children(bytes, headerSize, bytes.Length, "package"))
            {
                if (offset == typeStrings) typeIndex = children.Count;
                if (offset == keyStrings) keyIndex = children.Count;
                children.Add(new ArscChunk(ArscTable.Slice(bytes, offset, size)));
            }
            if (typeIndex < 0 || keyIndex < 0) throw new InvalidDataException("package: the type/key string pool offsets do not point at a chunk");
            return new ArscPackage(ArscTable.Slice(bytes, 0, headerSize), children, typeIndex, keyIndex);
        }

        public byte[] ToArray()
        {
            var output = new MemoryStream();
            byte[] head = (byte[])header.Clone();
            output.Write(head, 0, head.Length);
            for (int i = 0; i < Children.Count; i++)
            {
                if (i == typeStringsIndex) ArscTable.Put32(head, 268, (uint)output.Length);
                if (i == keyStringsIndex) ArscTable.Put32(head, 276, (uint)output.Length);
                output.Write(Children[i].Bytes, 0, Children[i].Bytes.Length);
            }
            byte[] result = output.ToArray();
            Array.Copy(head, result, head.Length);
            ArscTable.Put32(result, 4, (uint)result.Length);
            return result;
        }

        /// <summary>The name of type id <paramref name="typeId"/> ("style", "mipmap"...), or null.</summary>
        public string TypeName(int typeId)
        {
            return typeId >= 1 && typeId <= TypeStrings.Count ? TypeStrings.Get(typeId - 1) : null;
        }

        /// <summary>Index in <see cref="Children"/> of the type's typeSpec chunk, or -1.</summary>
        public int TypeSpecIndex(int typeId)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                if (Children[i].Type == ArscTable.TypeSpecType && Children[i].Bytes[8] == typeId) return i;
            }
            return -1;
        }

        public ArscTypeSpec TypeSpec(int typeId)
        {
            int i = TypeSpecIndex(typeId);
            return i < 0 ? null : new ArscTypeSpec(Children[i].Bytes);
        }

        /// <summary>
        /// Indices in <see cref="Children"/> of the type's configuration chunks: the RES_TABLE_TYPE_TYPE chunks that follow its
        /// typeSpec (another type's typeSpec ends the run). A chunk of this type elsewhere in the package is refused, because
        /// older loaders attach a type chunk to the most recent typeSpec.
        /// </summary>
        public List<int> TypeIndices(int typeId)
        {
            int spec = TypeSpecIndex(typeId);
            var result = new List<int>();
            if (spec < 0) return result;
            int i = spec + 1;
            for (; i < Children.Count && Children[i].Type == ArscTable.TypeType && Children[i].Bytes[8] == typeId; i++) result.Add(i);
            for (; i < Children.Count; i++)
            {
                if (Children[i].Type == ArscTable.TypeType && Children[i].Bytes[8] == typeId)
                    throw new InvalidDataException("package: a type 0x" + typeId.ToString("x2") + " chunk does not follow its typeSpec");
            }
            return result;
        }

        public List<ArscType> Types(int typeId)
        {
            return TypeIndices(typeId).Select(i => new ArscType(Children[i].Bytes)).ToList();
        }
    }

    /// <summary>A RES_TABLE_TYPE_SPEC_TYPE chunk: the type id and the configuration-change flags of every entry.</summary>
    public sealed class ArscTypeSpec
    {
        public const uint ConfigDensity = 0x0100;
        public const uint ConfigVersion = 0x0400;
        public const uint ConfigUiMode = 0x1000;

        public ArscTypeSpec(byte[] bytes)
        {
            Bytes = bytes;
            if (ArscTable.U16(bytes, 0) != ArscTable.TypeSpecType) throw new InvalidDataException("not a typeSpec chunk");
            if (HeaderSize + 4L * EntryCount > bytes.Length) throw new InvalidDataException("typeSpec of type 0x" + Id.ToString("x2") + " is truncated");
        }

        public byte[] Bytes { get; }

        public int HeaderSize { get { return ArscTable.U16(Bytes, 2); } }

        public byte Id { get { return Bytes[8]; } }

        /// <summary>res1: the number of type chunks that follow (written by newer aapt2; 0 = not counted).</summary>
        public ushort TypesCount { get { return ArscTable.U16(Bytes, 10); } }

        public int EntryCount { get { return (int)ArscTable.U32(Bytes, 12); } }

        public uint Flags(int entry)
        {
            return ArscTable.U32(Bytes, HeaderSize + 4 * entry);
        }

        /// <summary>A copy with the entry's flags ORed with <paramref name="add"/> and the type count raised by <paramref name="addedTypes"/> when counted.</summary>
        public byte[] With(int entry, uint add, int addedTypes)
        {
            byte[] copy = (byte[])Bytes.Clone();
            ArscTable.Put32(copy, HeaderSize + 4 * entry, Flags(entry) | add);
            if (TypesCount != 0) ArscTable.Put16(copy, 10, checked((ushort)(TypesCount + addedTypes)));
            return copy;
        }
    }

    /// <summary>
    /// A RES_TABLE_TYPE_TYPE chunk (one configuration of one type): its configuration and its entries. Only dense chunks
    /// (32-bit offsets, no FLAG_SPARSE / FLAG_OFFSET16) are read, which is what aapt produced for 1.17.32.
    /// </summary>
    public sealed class ArscType
    {
        public const byte FlagSparse = 0x01;
        public const byte FlagOffset16 = 0x02;

        public ArscType(byte[] bytes)
        {
            Bytes = bytes;
            if (ArscTable.U16(bytes, 0) != ArscTable.TypeType) throw new InvalidDataException("not a type chunk");
            if (HeaderSize < 20 + 28 || ConfigSize < 28 || 20 + ConfigSize > HeaderSize)
                throw new InvalidDataException("type 0x" + Id.ToString("x2") + " chunk: header " + HeaderSize + ", config " + ConfigSize);
            if ((Flags & (FlagSparse | FlagOffset16)) != 0) throw new InvalidDataException("type 0x" + Id.ToString("x2") + " chunk is sparse/16-bit (flags 0x" + Flags.ToString("x2") + ")");
            if (EntriesStart != HeaderSize + 4L * EntryCount || EntriesStart > bytes.Length)
                throw new InvalidDataException("type 0x" + Id.ToString("x2") + " chunk: entriesStart " + EntriesStart + " for " + EntryCount + " entries");
        }

        public byte[] Bytes { get; }

        public int HeaderSize { get { return ArscTable.U16(Bytes, 2); } }

        public byte Id { get { return Bytes[8]; } }

        public byte Flags { get { return Bytes[9]; } }

        public int EntryCount { get { return (int)ArscTable.U32(Bytes, 12); } }

        public int EntriesStart { get { return (int)ArscTable.U32(Bytes, 16); } }

        public int ConfigSize { get { return (int)ArscTable.U32(Bytes, 20); } }

        /// <summary>The ResTable_config bytes (its own size field first).</summary>
        public byte[] Config { get { return ArscTable.Slice(Bytes, 20, ConfigSize); } }

        public bool Has(int entry)
        {
            return entry < EntryCount && ArscTable.U32(Bytes, HeaderSize + 4 * entry) != ArscTable.NoEntry;
        }

        /// <summary>The entry's bytes (ResTable_entry plus its Res_value, or ResTable_map_entry plus its maps), or null.</summary>
        public byte[] EntryBytes(int entry)
        {
            if (!Has(entry)) return null;
            int at = EntriesStart + (int)ArscTable.U32(Bytes, HeaderSize + 4 * entry);
            if (at + 8 > Bytes.Length) throw new InvalidDataException("type 0x" + Id.ToString("x2") + " entry " + entry + " lies outside its chunk");
            int size = ArscEntry.Length(Bytes, at);
            if (at + size > Bytes.Length) throw new InvalidDataException("type 0x" + Id.ToString("x2") + " entry " + entry + " runs past its chunk");
            return ArscTable.Slice(Bytes, at, size);
        }

        public ArscEntry Entry(int entry)
        {
            byte[] bytes = EntryBytes(entry);
            return bytes == null ? null : new ArscEntry(bytes);
        }

        /// <summary>All present entries, by index.</summary>
        public SortedDictionary<int, byte[]> Entries()
        {
            var result = new SortedDictionary<int, byte[]>();
            for (int i = 0; i < EntryCount; i++)
            {
                if (Has(i)) result[i] = EntryBytes(i);
            }
            return result;
        }

        /// <summary>
        /// A dense type chunk the way aapt writes one: header (with <paramref name="config"/>), one offset per entry
        /// (<see cref="ArscTable.NoEntry"/> for absent ones), then the entries in index order.
        /// </summary>
        public static byte[] Build(byte id, byte[] config, int entryCount, SortedDictionary<int, byte[]> entries)
        {
            int headerSize = 20 + config.Length;
            int entriesStart = headerSize + 4 * entryCount;
            int dataSize = entries.Values.Sum(e => e.Length);
            var bytes = new byte[entriesStart + dataSize];
            ArscTable.Put16(bytes, 0, ArscTable.TypeType);
            ArscTable.Put16(bytes, 2, (ushort)headerSize);
            ArscTable.Put32(bytes, 4, (uint)bytes.Length);
            bytes[8] = id;
            ArscTable.Put32(bytes, 12, (uint)entryCount);
            ArscTable.Put32(bytes, 16, (uint)entriesStart);
            Array.Copy(config, 0, bytes, 20, config.Length);
            int data = 0;
            for (int i = 0; i < entryCount; i++)
            {
                if (entries.TryGetValue(i, out byte[] entry))
                {
                    if (entry.Length % 4 != 0) throw new ArgumentException("entry " + i + " is not 4-byte aligned");
                    ArscTable.Put32(bytes, headerSize + 4 * i, (uint)data);
                    Array.Copy(entry, 0, bytes, entriesStart + data, entry.Length);
                    data += entry.Length;
                }
                else
                {
                    ArscTable.Put32(bytes, headerSize + 4 * i, ArscTable.NoEntry);
                }
            }
            if (entries.Keys.Any(k => k < 0 || k >= entryCount)) throw new ArgumentException("entry index out of range");
            return bytes;
        }
    }

    /// <summary>One ResTable_entry: a simple value, or a map (bag: a style's parent and attribute/value items).</summary>
    public sealed class ArscEntry
    {
        public const ushort FlagComplex = 0x0001;

        public ArscEntry(byte[] bytes)
        {
            Bytes = bytes;
        }

        public byte[] Bytes { get; }

        public ushort Size { get { return ArscTable.U16(Bytes, 0); } }

        public ushort Flags { get { return ArscTable.U16(Bytes, 2); } }

        /// <summary>Index of the entry's name in the package's key string pool.</summary>
        public uint Key { get { return ArscTable.U32(Bytes, 4); } }

        public bool IsComplex { get { return (Flags & FlagComplex) != 0; } }

        /// <summary>Map entries: the parent style (ResTable_ref), 0 for none.</summary>
        public uint Parent { get { return IsComplex ? ArscTable.U32(Bytes, 8) : 0; } }

        /// <summary>Map entries: the items (attribute id, Res_value data type, data).</summary>
        public List<(uint Name, byte DataType, uint Data)> Items
        {
            get
            {
                var items = new List<(uint, byte, uint)>();
                if (!IsComplex) return items;
                int count = (int)ArscTable.U32(Bytes, 12);
                for (int i = 0, at = Size; i < count; i++, at += 12) items.Add((ArscTable.U32(Bytes, at), Bytes[at + 7], ArscTable.U32(Bytes, at + 8)));
                return items;
            }
        }

        /// <summary>Simple entries: the Res_value data type and data.</summary>
        public (byte DataType, uint Data) Value
        {
            get
            {
                if (IsComplex) throw new InvalidOperationException("a map entry has no single value");
                return (Bytes[Size + 3], ArscTable.U32(Bytes, Size + 4));
            }
        }

        /// <summary>The byte length of the entry at <paramref name="at"/>: its header plus the value or the maps.</summary>
        internal static int Length(byte[] b, int at)
        {
            ushort size = ArscTable.U16(b, at), flags = ArscTable.U16(b, at + 2);
            if ((flags & FlagComplex) != 0)
            {
                if (size < 16) throw new InvalidDataException("map entry header of " + size + " bytes");
                return size + 12 * (int)ArscTable.U32(b, at + 12);
            }
            if (size < 8) throw new InvalidDataException("entry header of " + size + " bytes");
            return size + ArscTable.U16(b, at + size);
        }

        /// <summary>A simple entry (ResTable_entry of 8 bytes, then one Res_value).</summary>
        public static byte[] Simple(uint key, byte dataType, uint data)
        {
            var bytes = new byte[16];
            ArscTable.Put16(bytes, 0, 8);
            ArscTable.Put32(bytes, 4, key);
            ArscTable.Put16(bytes, 8, 8);
            bytes[11] = dataType;
            ArscTable.Put32(bytes, 12, data);
            return bytes;
        }

        /// <summary>A map entry (16-byte ResTable_map_entry, then the items in the order given).</summary>
        public static byte[] Map(ushort flags, uint key, uint parent, IList<(uint Name, byte DataType, uint Data)> items)
        {
            var bytes = new byte[16 + 12 * items.Count];
            ArscTable.Put16(bytes, 0, 16);
            ArscTable.Put16(bytes, 2, (ushort)(flags | FlagComplex));
            ArscTable.Put32(bytes, 4, key);
            ArscTable.Put32(bytes, 8, parent);
            ArscTable.Put32(bytes, 12, (uint)items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                int at = 16 + 12 * i;
                ArscTable.Put32(bytes, at, items[i].Name);
                ArscTable.Put16(bytes, at + 4, 8);
                bytes[at + 7] = items[i].DataType;
                ArscTable.Put32(bytes, at + 8, items[i].Data);
            }
            return bytes;
        }
    }

    /// <summary>ResTable_config, as far as the art patch needs it (field offsets from ResourceTypes.h).</summary>
    public static class ArscConfig
    {
        public const int DensityOffset = 14;
        public const int SdkVersionOffset = 24;
        public const int UiModeOffset = 29;
        public const byte UiModeNightYes = 0x20;

        public static ushort Density(byte[] config)
        {
            return ArscTable.U16(config, DensityOffset);
        }

        public static ushort SdkVersion(byte[] config)
        {
            return ArscTable.U16(config, SdkVersionOffset);
        }

        public static byte UiMode(byte[] config)
        {
            return config[UiModeOffset];
        }

        /// <summary>A configuration of <paramref name="size"/> bytes with only the given fields set.</summary>
        public static byte[] Create(int size, ushort sdkVersion, ushort density = 0, byte uiMode = 0)
        {
            var config = new byte[size];
            ArscTable.Put32(config, 0, (uint)size);
            ArscTable.Put16(config, DensityOffset, density);
            ArscTable.Put16(config, SdkVersionOffset, sdkVersion);
            config[UiModeOffset] = uiMode;
            return config;
        }

        /// <summary>True when the configuration sets nothing but the fields <see cref="Create"/> sets.</summary>
        public static bool OnlyDensitySdkUiMode(byte[] config)
        {
            byte[] same = Create(config.Length, SdkVersion(config), Density(config), UiMode(config));
            return same.AsSpan().SequenceEqual(config);
        }

        /// <summary>aapt2's qualifier text for the configurations the art patch deals with ("night-v31", "xxxhdpi-v4", "()" ...).</summary>
        public static string Describe(byte[] config)
        {
            if (!OnlyDensitySdkUiMode(config)) return "(other)";
            var parts = new List<string>();
            if (UiMode(config) == UiModeNightYes) parts.Add("night");
            else if (UiMode(config) != 0) parts.Add("uiMode=0x" + UiMode(config).ToString("x2"));
            switch (Density(config))
            {
                case 0: break;
                case 120: parts.Add("ldpi"); break;
                case 160: parts.Add("mdpi"); break;
                case 240: parts.Add("hdpi"); break;
                case 320: parts.Add("xhdpi"); break;
                case 480: parts.Add("xxhdpi"); break;
                case 640: parts.Add("xxxhdpi"); break;
                case 0xFFFE: parts.Add("anydpi"); break;
                case 0xFFFF: parts.Add("nodpi"); break;
                default: parts.Add(Density(config) + "dpi"); break;
            }
            if (SdkVersion(config) != 0) parts.Add("v" + SdkVersion(config));
            return parts.Count == 0 ? "()" : string.Join("-", parts);
        }
    }

    /// <summary>
    /// A RES_STRING_POOL_TYPE chunk: read any string; append new ones at the end (indices of existing strings do not move).
    /// Pools with style spans are read-only here (appending would have to move the style data). Unchanged pools are
    /// written back byte for byte.
    /// </summary>
    public sealed class StringPool
    {
        public const uint Utf8Flag = 0x100;
        public const uint SortedFlag = 0x1;

        private readonly byte[] original;
        private readonly List<string> appended = new List<string>();
        private string[] cache;

        private StringPool(byte[] original)
        {
            this.original = original;
        }

        public static StringPool Parse(byte[] bytes)
        {
            if (ArscTable.U16(bytes, 0) != ArscTable.StringPoolType) throw new InvalidDataException("not a string pool chunk");
            var pool = new StringPool(bytes);
            if (pool.HeaderSize < 28) throw new InvalidDataException("string pool header of " + pool.HeaderSize + " bytes");
            if (pool.HeaderSize + 4L * (pool.OriginalCount + pool.StyleCount) > bytes.Length ||
                pool.StringsStart > bytes.Length || pool.StyleCount > 0 && pool.StylesStart > bytes.Length)
                throw new InvalidDataException("string pool offsets point outside the pool");
            return pool;
        }

        private int HeaderSize { get { return ArscTable.U16(original, 2); } }

        private int OriginalCount { get { return (int)ArscTable.U32(original, 8); } }

        public int StyleCount { get { return (int)ArscTable.U32(original, 12); } }

        public uint Flags { get { return ArscTable.U32(original, 16); } }

        private int StringsStart { get { return (int)ArscTable.U32(original, 20); } }

        private int StylesStart { get { return (int)ArscTable.U32(original, 24); } }

        public bool IsUtf8 { get { return (Flags & Utf8Flag) != 0; } }

        public int Count { get { return OriginalCount + appended.Count; } }

        public string Get(int index)
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (index >= OriginalCount) return appended[index - OriginalCount];
            if (cache == null) cache = new string[OriginalCount];
            return cache[index] ?? (cache[index] = Read(index));
        }

        /// <summary>The first index holding <paramref name="value"/>, or -1.</summary>
        public int IndexOf(string value)
        {
            for (int i = 0; i < Count; i++)
            {
                if (Get(i) == value) return i;
            }
            return -1;
        }

        /// <summary>Adds a string at the end and returns its index.</summary>
        public int Append(string value)
        {
            if (StyleCount > 0) throw new InvalidOperationException("cannot append to a string pool with styles");
            if ((Flags & SortedFlag) != 0) throw new InvalidOperationException("cannot append to a sorted string pool");
            appended.Add(value);
            return Count - 1;
        }

        public byte[] ToArray()
        {
            if (appended.Count == 0) return original;
            int count = OriginalCount, headerSize = HeaderSize;
            var offsets = new List<uint>();
            for (int i = 0; i < count; i++) offsets.Add(ArscTable.U32(original, headerSize + 4 * i));
            int dataStart = StringsStart, dataEnd = original.Length;
            var data = new MemoryStream();
            data.Write(original, dataStart, dataEnd - dataStart);   // the existing string data, with its padding
            foreach (string s in appended)
            {
                offsets.Add((uint)data.Length);
                byte[] encoded = Encode(s);
                data.Write(encoded, 0, encoded.Length);
            }
            while (data.Length % 4 != 0) data.WriteByte(0);

            int newStringsStart = headerSize + 4 * offsets.Count;
            var result = new byte[newStringsStart + data.Length];
            Array.Copy(original, result, headerSize);
            ArscTable.Put32(result, 4, (uint)result.Length);
            ArscTable.Put32(result, 8, (uint)offsets.Count);
            ArscTable.Put32(result, 20, (uint)newStringsStart);
            for (int i = 0; i < offsets.Count; i++) ArscTable.Put32(result, headerSize + 4 * i, offsets[i]);
            data.Position = 0;
            data.Read(result, newStringsStart, (int)data.Length);
            return result;
        }

        private string Read(int index)
        {
            int at = StringsStart + (int)ArscTable.U32(original, HeaderSize + 4 * index);
            if (IsUtf8)
            {
                ReadLength8(ref at);              // length in UTF-16 units
                int bytes = ReadLength8(ref at);  // length in bytes
                return Encoding.UTF8.GetString(original, at, bytes);
            }
            int units = ArscTable.U16(original, at);
            at += 2;
            if ((units & 0x8000) != 0)
            {
                units = (units & 0x7FFF) << 16 | ArscTable.U16(original, at);
                at += 2;
            }
            return Encoding.Unicode.GetString(original, at, units * 2);
        }

        private int ReadLength8(ref int at)
        {
            int length = original[at++];
            if ((length & 0x80) != 0) length = (length & 0x7F) << 8 | original[at++];
            return length;
        }

        private byte[] Encode(string s)
        {
            var output = new MemoryStream();
            if (IsUtf8)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(s);
                WriteLength8(output, s.Length);
                WriteLength8(output, utf8.Length);
                output.Write(utf8, 0, utf8.Length);
                output.WriteByte(0);
            }
            else
            {
                if (s.Length > 0x7FFF) throw new ArgumentException("string too long");
                output.WriteByte((byte)s.Length);
                output.WriteByte((byte)(s.Length >> 8));
                byte[] utf16 = Encoding.Unicode.GetBytes(s);
                output.Write(utf16, 0, utf16.Length);
                output.WriteByte(0);
                output.WriteByte(0);
            }
            return output.ToArray();
        }

        private static void WriteLength8(Stream output, int length)
        {
            if (length > 0x7FFF) throw new ArgumentException("string too long");
            if (length > 0x7F) output.WriteByte((byte)(0x80 | length >> 8));
            output.WriteByte((byte)length);
        }
    }
}
