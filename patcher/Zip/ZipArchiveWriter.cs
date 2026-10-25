using System;
using System.Collections.Generic;
using System.IO;

namespace McvsOffline.Patcher.Zip
{
    /// <summary>
    /// Writes entries as an APK-ready zip: raw data copied unchanged, sizes in the local headers (no data
    /// descriptors), and every STORED entry's data aligned like <c>zipalign -p 4</c> does: 4 bytes, 4096 for
    /// <c>.so</c> files. Padding goes into the local extra field as the Android alignment record (0xD935, the same
    /// record apksigner writes), replacing any old padding.
    /// </summary>
    public static class ZipArchiveWriter
    {
        public const ushort AlignmentExtraId = 0xD935;
        public const int DefaultAlignment = 4;
        public const int SharedLibraryAlignment = 4096;

        public static byte[] Write(IList<ZipEntry> entries)
        {
            using (var output = new MemoryStream())
            {
                Write(entries, output);
                return output.ToArray();
            }
        }

        public static void Write(IList<ZipEntry> entries, Stream output)
        {
            var offsets = new long[entries.Count];
            var w = new BinaryWriter(output);
            long position = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                ZipEntry e = entries[i];
                offsets[i] = position;
                byte[] extra = StripPadding(e.LocalExtra);
                int alignment = AlignmentFor(e);
                if (alignment > 1)
                {
                    long dataOffset = position + 30 + e.NameBytes.Length + extra.Length;
                    if (dataOffset % alignment != 0) extra = AppendAlignment(extra, dataOffset, alignment);
                }
                ushort flags = (ushort)(e.Flags & ~ZipEntry.FlagDataDescriptor);
                w.Write(0x04034b50u);
                w.Write(e.VersionNeeded);
                w.Write(flags);
                w.Write(e.Method);
                w.Write(e.DosTime);
                w.Write(e.DosDate);
                w.Write(e.Crc32);
                w.Write(checked((uint)e.RawData.Length));
                w.Write(checked((uint)e.UncompressedSize));
                w.Write(checked((ushort)e.NameBytes.Length));
                w.Write(checked((ushort)extra.Length));
                w.Write(e.NameBytes);
                w.Write(extra);
                w.Write(e.RawData);
                position += 30 + e.NameBytes.Length + extra.Length + e.RawData.Length;
            }

            long cdStart = position;
            for (int i = 0; i < entries.Count; i++)
            {
                ZipEntry e = entries[i];
                ushort flags = (ushort)(e.Flags & ~ZipEntry.FlagDataDescriptor);
                w.Write(0x02014b50u);
                w.Write(e.VersionMadeBy);
                w.Write(e.VersionNeeded);
                w.Write(flags);
                w.Write(e.Method);
                w.Write(e.DosTime);
                w.Write(e.DosDate);
                w.Write(e.Crc32);
                w.Write(checked((uint)e.RawData.Length));
                w.Write(checked((uint)e.UncompressedSize));
                w.Write(checked((ushort)e.NameBytes.Length));
                w.Write(checked((ushort)e.CentralExtra.Length));
                w.Write(checked((ushort)e.Comment.Length));
                w.Write((ushort)0);
                w.Write(e.InternalAttributes);
                w.Write(e.ExternalAttributes);
                w.Write(checked((uint)offsets[i]));
                w.Write(e.NameBytes);
                w.Write(e.CentralExtra);
                w.Write(e.Comment);
                position += 46 + e.NameBytes.Length + e.CentralExtra.Length + e.Comment.Length;
            }
            long cdSize = position - cdStart;

            w.Write(0x06054b50u);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write(checked((ushort)entries.Count));
            w.Write(checked((ushort)entries.Count));
            w.Write(checked((uint)cdSize));
            w.Write(checked((uint)cdStart));
            w.Write((ushort)0);
            w.Flush();
        }

        /// <summary>4 for STORED entries, 4096 for STORED native libraries, none for compressed ones.</summary>
        public static int AlignmentFor(ZipEntry e)
        {
            if (e.Method != ZipEntry.MethodStored) return 1;
            return e.Name.EndsWith(".so", StringComparison.Ordinal) ? SharedLibraryAlignment : DefaultAlignment;
        }

        /// <summary>
        /// Drops old alignment padding: Android alignment records and the raw zero bytes classic zipalign appends
        /// (which parse as records with id 0). Other extra records are kept. Unparseable data is dropped as padding.
        /// </summary>
        public static byte[] StripPadding(byte[] extra)
        {
            if (extra == null || extra.Length == 0) return new byte[0];
            var kept = new MemoryStream();
            int p = 0;
            while (p + 4 <= extra.Length)
            {
                ushort id = ZipArchiveReader.U16(extra, p);
                ushort size = ZipArchiveReader.U16(extra, p + 2);
                if (p + 4 + size > extra.Length) return kept.ToArray();
                if (id != 0 && id != AlignmentExtraId) kept.Write(extra, p, 4 + size);
                p += 4 + size;
            }
            return kept.ToArray();
        }

        private static byte[] AppendAlignment(byte[] extra, long dataOffsetWithoutRecord, int alignment)
        {
            // Record: id (2) + size (2) + alignment (2) + zero padding.
            long afterHeader = dataOffsetWithoutRecord + 6;
            int padding = (int)((alignment - afterHeader % alignment) % alignment);
            var result = new byte[extra.Length + 6 + padding];
            Buffer.BlockCopy(extra, 0, result, 0, extra.Length);
            int p = extra.Length;
            result[p] = (byte)(AlignmentExtraId & 0xFF);
            result[p + 1] = (byte)(AlignmentExtraId >> 8);
            result[p + 2] = (byte)((2 + padding) & 0xFF);
            result[p + 3] = (byte)((2 + padding) >> 8);
            result[p + 4] = (byte)(alignment & 0xFF);
            result[p + 5] = (byte)(alignment >> 8);
            return result;
        }
    }
}
