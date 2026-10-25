using System;
using System.Text;

namespace McvsOffline.Patcher.Zip
{
    /// <summary>
    /// One entry of an APK, kept in its raw (still compressed) form so unchanged entries are copied byte for byte:
    /// same compression method, same compressed bytes, same CRC (verify-C7 §5: assemblies/ is STORED and mmap'd,
    /// lib/ is DEFLATED and extracted at install time).
    /// </summary>
    public sealed class ZipEntry
    {
        public const ushort MethodStored = 0;
        public const ushort MethodDeflated = 8;

        /// <summary>General purpose flag bit 3: sizes and CRC follow the data in a descriptor.</summary>
        public const ushort FlagDataDescriptor = 0x0008;

        /// <summary>General purpose flag bit 11: the name is UTF-8.</summary>
        public const ushort FlagUtf8 = 0x0800;

        public string Name { get; set; }

        /// <summary>The raw name bytes as stored (kept so non-UTF-8 names round-trip).</summary>
        public byte[] NameBytes { get; set; }

        public ushort VersionMadeBy { get; set; }

        public ushort VersionNeeded { get; set; }

        public ushort Flags { get; set; }

        public ushort Method { get; set; }

        public ushort DosTime { get; set; }

        public ushort DosDate { get; set; }

        public uint Crc32 { get; set; }

        public long UncompressedSize { get; set; }

        /// <summary>The entry's data exactly as stored in the archive (compressed when <see cref="Method"/> is 8).</summary>
        public byte[] RawData { get; set; }

        /// <summary>Extra field of the local header (may hold old alignment padding).</summary>
        public byte[] LocalExtra { get; set; }

        /// <summary>Extra field of the central directory record.</summary>
        public byte[] CentralExtra { get; set; }

        public byte[] Comment { get; set; }

        public ushort InternalAttributes { get; set; }

        public uint ExternalAttributes { get; set; }

        /// <summary>Offset of the local header in the archive it was read from (diagnostics).</summary>
        public long SourceOffset { get; set; }

        public bool IsDirectory { get { return Name.EndsWith("/", StringComparison.Ordinal); } }

        /// <summary>A new STORED entry (DLLs go back into assemblies/ uncompressed, verify-C7 §5).</summary>
        public static ZipEntry CreateStored(string name, byte[] data, ZipEntry template)
        {
            var entry = new ZipEntry
            {
                Name = name,
                NameBytes = Encoding.UTF8.GetBytes(name),
                VersionMadeBy = template != null ? template.VersionMadeBy : (ushort)0,
                VersionNeeded = 10,
                Flags = 0,
                Method = MethodStored,
                DosTime = template != null ? template.DosTime : DefaultDosTime,
                DosDate = template != null ? template.DosDate : DefaultDosDate,
                Crc32 = Zip.Crc32.Compute(data),
                UncompressedSize = data.Length,
                RawData = data,
                LocalExtra = new byte[0],
                CentralExtra = new byte[0],
                Comment = new byte[0],
                InternalAttributes = 0,
                ExternalAttributes = template != null ? template.ExternalAttributes : 0,
            };
            return entry;
        }

        /// <summary>A new DEFLATED entry.</summary>
        public static ZipEntry CreateDeflated(string name, byte[] data, ZipEntry template)
        {
            var entry = CreateStored(name, data, template);
            entry.VersionNeeded = 20;
            entry.Method = MethodDeflated;
            entry.RawData = Deflate.Compress(data);
            return entry;
        }

        /// <summary>The uncompressed content.</summary>
        public byte[] GetData()
        {
            if (Method == MethodStored) return RawData;
            if (Method == MethodDeflated) return Deflate.Decompress(RawData, UncompressedSize);
            throw new NotSupportedException("entry " + Name + " uses unsupported compression method " + Method);
        }

        // 2008-01-01 00:00:00, the timestamp Android build tools use for generated entries.
        public const ushort DefaultDosTime = 0;
        public const ushort DefaultDosDate = (2008 - 1980) << 9 | 1 << 5 | 1;

        public override string ToString()
        {
            return Name + " (method " + Method + ", " + RawData.Length + " bytes stored)";
        }
    }
}
