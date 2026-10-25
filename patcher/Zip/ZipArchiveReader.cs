using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace McvsOffline.Patcher.Zip
{
    /// <summary>
    /// Reads every entry of a zip/APK in raw form, driven by the central directory (the authoritative list; an APK's
    /// signing block sits between the last entry and the central directory and is skipped). Zip64 and multi-disk
    /// archives are refused: an APK of this game is neither.
    /// </summary>
    public static class ZipArchiveReader
    {
        private const uint LocalHeaderSignature = 0x04034b50;
        private const uint CentralHeaderSignature = 0x02014b50;
        private const uint EndOfCentralDirectorySignature = 0x06054b50;

        public sealed class Archive
        {
            public List<ZipEntry> Entries { get; } = new List<ZipEntry>();

            /// <summary>Offset of the central directory; bytes between the last entry and it (e.g. a signing block) are dropped.</summary>
            public long CentralDirectoryOffset { get; set; }

            /// <summary>True when an "APK Sig Block 42" (v2/v3 signature) precedes the central directory.</summary>
            public bool HasSigningBlock { get; set; }

            public ZipEntry Find(string name)
            {
                foreach (var e in Entries)
                {
                    if (string.Equals(e.Name, name, StringComparison.Ordinal)) return e;
                }
                return null;
            }
        }

        public static Archive Read(string path)
        {
            return Read(File.ReadAllBytes(path));
        }

        public static Archive Read(byte[] data)
        {
            int eocd = FindEndOfCentralDirectory(data);
            if (eocd < 0) throw new InvalidDataException("not a zip file (no end of central directory record)");
            ushort disk = U16(data, eocd + 4);
            ushort cdDisk = U16(data, eocd + 6);
            ushort countOnDisk = U16(data, eocd + 8);
            ushort count = U16(data, eocd + 10);
            uint cdSize = U32(data, eocd + 12);
            uint cdOffset = U32(data, eocd + 16);
            if (disk != 0 || cdDisk != 0 || countOnDisk != count) throw new InvalidDataException("multi-disk zip files are not supported");
            if (count == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF) throw new InvalidDataException("zip64 archives are not supported");
            if ((long)cdOffset + cdSize > eocd) throw new InvalidDataException("central directory out of range");

            var archive = new Archive { CentralDirectoryOffset = cdOffset };
            if (cdOffset >= 24)
            {
                archive.HasSigningBlock = Encoding.ASCII.GetString(data, (int)cdOffset - 16, 16) == "APK Sig Block 42";
            }

            int p = (int)cdOffset;
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                if (U32(data, p) != CentralHeaderSignature) throw new InvalidDataException("bad central directory record #" + i);
                var e = new ZipEntry
                {
                    VersionMadeBy = U16(data, p + 4),
                    VersionNeeded = U16(data, p + 6),
                    Flags = U16(data, p + 8),
                    Method = U16(data, p + 10),
                    DosTime = U16(data, p + 12),
                    DosDate = U16(data, p + 14),
                    Crc32 = U32(data, p + 16),
                };
                uint compressedSize = U32(data, p + 20);
                e.UncompressedSize = U32(data, p + 24);
                int nameLength = U16(data, p + 28);
                int extraLength = U16(data, p + 30);
                int commentLength = U16(data, p + 32);
                if (U16(data, p + 34) != 0) throw new InvalidDataException("multi-disk entry");
                e.InternalAttributes = U16(data, p + 36);
                e.ExternalAttributes = U32(data, p + 38);
                uint localOffset = U32(data, p + 42);
                if (compressedSize == 0xFFFFFFFF || e.UncompressedSize == 0xFFFFFFFF || localOffset == 0xFFFFFFFF)
                    throw new InvalidDataException("zip64 entries are not supported");
                e.NameBytes = Slice(data, p + 46, nameLength);
                e.Name = (e.Flags & ZipEntry.FlagUtf8) != 0 || IsAscii(e.NameBytes)
                    ? Encoding.UTF8.GetString(e.NameBytes)
                    : Encoding.Latin1.GetString(e.NameBytes);
                e.CentralExtra = Slice(data, p + 46 + nameLength, extraLength);
                e.Comment = Slice(data, p + 46 + nameLength + extraLength, commentLength);
                p += 46 + nameLength + extraLength + commentLength;

                int lh = (int)localOffset;
                if (U32(data, lh) != LocalHeaderSignature) throw new InvalidDataException("bad local header for " + e.Name);
                int localNameLength = U16(data, lh + 26);
                int localExtraLength = U16(data, lh + 28);
                e.LocalExtra = Slice(data, lh + 30 + localNameLength, localExtraLength);
                int dataStart = lh + 30 + localNameLength + localExtraLength;
                if ((long)dataStart + compressedSize > cdOffset) throw new InvalidDataException("entry data out of range: " + e.Name);
                e.RawData = Slice(data, dataStart, (int)compressedSize);
                e.SourceOffset = lh;
                if (e.Method != ZipEntry.MethodStored && e.Method != ZipEntry.MethodDeflated)
                    throw new InvalidDataException("entry " + e.Name + " uses unsupported compression method " + e.Method);
                if ((e.Flags & 0x0001) != 0) throw new InvalidDataException("encrypted entries are not supported: " + e.Name);
                if (!names.Add(e.Name)) throw new InvalidDataException("duplicate entry name: " + e.Name);
                archive.Entries.Add(e);
            }
            return archive;
        }

        private static int FindEndOfCentralDirectory(byte[] data)
        {
            int min = Math.Max(0, data.Length - 22 - 0xFFFF);
            for (int i = data.Length - 22; i >= min; i--)
            {
                if (U32(data, i) == EndOfCentralDirectorySignature && i + 22 + U16(data, i + 20) == data.Length) return i;
            }
            return -1;
        }

        private static bool IsAscii(byte[] bytes)
        {
            foreach (byte b in bytes)
            {
                if (b >= 0x80) return false;
            }
            return true;
        }

        private static byte[] Slice(byte[] data, int offset, int count)
        {
            if (offset < 0 || count < 0 || (long)offset + count > data.Length) throw new InvalidDataException("truncated zip file");
            var result = new byte[count];
            Buffer.BlockCopy(data, offset, result, 0, count);
            return result;
        }

        internal static ushort U16(byte[] d, int o)
        {
            return (ushort)(d[o] | d[o + 1] << 8);
        }

        internal static uint U32(byte[] d, int o)
        {
            return (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
        }
    }
}
