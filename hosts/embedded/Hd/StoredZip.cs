// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace McvsOffline.Embedded.Hd
{
    /// <summary>The HD pack is not usable (zip structure, ZIP64, an entry outside the file, a CRC mismatch, its manifest).</summary>
    internal sealed class HdZipException : Exception
    {
        public HdZipException(string message) : base(message) { }
    }

    /// <summary>One central-directory entry of a <see cref="StoredZip"/>.</summary>
    internal sealed class StoredZipEntry
    {
        public string Name { get; internal set; }

        /// <summary>0 stored, 8 deflated, ...</summary>
        public int Method { get; internal set; }

        public long CompressedSize { get; internal set; }

        public long Size { get; internal set; }

        public uint Crc32 { get; internal set; }

        public long LocalHeaderOffset { get; internal set; }
    }

    /// <summary>
    /// A minimal zip reader for the HD texture pack (McvsOffline/hd/mcvs-hd-*.zip, docs/design/hd-textures.md §2): the
    /// end-of-central-directory record, the central directory and, for stored entries, their bytes. It checks that the structure
    /// is complete, there is no ZIP64 and every entry lies inside the file. No decompression: the pack's entries are stored
    /// (the .f files are LZ4 blocks already). Throws <see cref="HdZipException"/> for anything else.
    /// </summary>
    internal sealed class StoredZip
    {
        private const uint EndSignature = 0x06054b50;
        private const uint CentralSignature = 0x02014b50;
        private const uint LocalSignature = 0x04034b50;
        private const int MaxEntries = 200000;

        private readonly string path;
        private readonly long length;
        private readonly Dictionary<string, StoredZipEntry> byName = new Dictionary<string, StoredZipEntry>(StringComparer.Ordinal);

        private StoredZip(string path, long length, List<StoredZipEntry> entries)
        {
            this.path = path;
            this.length = length;
            Entries = entries;
            foreach (StoredZipEntry e in entries) byName[e.Name] = e;
        }

        public string Path { get { return path; } }

        public long Length { get { return length; } }

        public List<StoredZipEntry> Entries { get; private set; }

        /// <summary>Entries whose method is not 0 (stored).</summary>
        public int CompressedEntries
        {
            get
            {
                int n = 0;
                foreach (StoredZipEntry e in Entries)
                {
                    if (e.Method != 0) n++;
                }
                return n;
            }
        }

        public StoredZipEntry Find(string name)
        {
            StoredZipEntry e;
            return name != null && byName.TryGetValue(name, out e) ? e : null;
        }

        /// <summary>Reads and checks the directory of the zip at <paramref name="zipPath"/>.</summary>
        public static StoredZip Open(string zipPath)
        {
            using (var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long size = file.Length;
                if (size < 22) throw new HdZipException("too short for a zip (" + size + " bytes)");
                if (size > 0xFFFFFFFFL) throw new HdZipException("larger than 4 GiB (ZIP64 is not supported)");
                int tail = (int)Math.Min(size, 22 + 65535);
                byte[] end = ReadAt(file, size - tail, tail);
                int eocd = -1;
                for (int i = tail - 22; i >= 0; i--)
                {
                    if (U32(end, i) == EndSignature && i + 22 + U16(end, i + 20) == tail)
                    {
                        eocd = i;
                        break;
                    }
                }
                if (eocd < 0) throw new HdZipException("no end-of-central-directory record");
                int disk = U16(end, eocd + 4);
                int cdDisk = U16(end, eocd + 6);
                int count = U16(end, eocd + 10);
                uint cdSize = U32(end, eocd + 12);
                uint cdOffset = U32(end, eocd + 16);
                if (disk != 0 || cdDisk != 0 || U16(end, eocd + 8) != count) throw new HdZipException("multi-disk zips are not supported");
                if (count == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF) throw new HdZipException("ZIP64 is not supported");
                long cdStart = cdOffset;
                if (cdStart + cdSize > size - tail + eocd) throw new HdZipException("the central directory lies outside the file");
                if (count > MaxEntries) throw new HdZipException("too many entries (" + count + ")");
                byte[] cd = ReadAt(file, cdStart, (int)cdSize);
                var entries = new List<StoredZipEntry>(count);
                int p = 0;
                for (int i = 0; i < count; i++)
                {
                    if (p + 46 > cd.Length || U32(cd, p) != CentralSignature) throw new HdZipException("central directory entry " + i + " is damaged");
                    int flags = U16(cd, p + 8);
                    int method = U16(cd, p + 10);
                    uint crc = U32(cd, p + 16);
                    uint csize = U32(cd, p + 20);
                    uint usize = U32(cd, p + 24);
                    int nameLength = U16(cd, p + 28);
                    int extraLength = U16(cd, p + 30);
                    int commentLength = U16(cd, p + 32);
                    uint local = U32(cd, p + 42);
                    if (p + 46 + nameLength + extraLength + commentLength > cd.Length) throw new HdZipException("central directory entry " + i + " is cut off");
                    if (csize == 0xFFFFFFFF || usize == 0xFFFFFFFF || local == 0xFFFFFFFF) throw new HdZipException("ZIP64 entries are not supported");
                    if ((flags & 1) != 0) throw new HdZipException("encrypted entries are not supported");
                    string name = Encoding.UTF8.GetString(cd, p + 46, nameLength);
                    if ((long)local + 30 + nameLength + csize > cdStart) throw new HdZipException("entry '" + name + "' lies outside the file");
                    if (method == 0 && csize != usize) throw new HdZipException("stored entry '" + name + "' has different sizes");
                    entries.Add(new StoredZipEntry
                    {
                        Name = name, Method = method, CompressedSize = csize, Size = usize, Crc32 = crc, LocalHeaderOffset = local,
                    });
                    p += 46 + nameLength + extraLength + commentLength;
                }
                return new StoredZip(zipPath, size, entries);
            }
        }

        /// <summary>The bytes of a stored entry (checked against its CRC-32), or null when absent or compressed.</summary>
        public byte[] ReadStored(StoredZipEntry entry)
        {
            if (entry == null || entry.Method != 0 || entry.Size > 0x7FFFFFFF) return null;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] header = ReadAt(file, entry.LocalHeaderOffset, 30);
                if (U32(header, 0) != LocalSignature) throw new HdZipException("local header of '" + entry.Name + "' is damaged");
                long data = entry.LocalHeaderOffset + 30 + U16(header, 26) + U16(header, 28);
                if (data + entry.Size > length) throw new HdZipException("'" + entry.Name + "' lies outside the file");
                byte[] bytes = ReadAt(file, data, (int)entry.Size);
                if (McvsOffline.Server.Core.Content.Crc32.Compute(bytes) != entry.Crc32) throw new HdZipException("'" + entry.Name + "' fails its CRC-32");
                return bytes;
            }
        }

        /// <summary>
        /// Streams the bytes of a stored entry to <paramref name="sink"/> in pieces of at most 256 KiB and checks them against the
        /// entry's CRC-32 at the end (<see cref="HdZipException"/> when they differ: the caller drops what it wrote).
        /// </summary>
        public void CopyStored(StoredZipEntry entry, Action<byte[], int, int> sink)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (entry.Method != 0) throw new HdZipException("'" + entry.Name + "' is compressed (the pack's entries must be stored)");
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] header = ReadAt(file, entry.LocalHeaderOffset, 30);
                if (U32(header, 0) != LocalSignature) throw new HdZipException("local header of '" + entry.Name + "' is damaged");
                long data = entry.LocalHeaderOffset + 30 + U16(header, 26) + U16(header, 28);
                if (data + entry.Size > length) throw new HdZipException("'" + entry.Name + "' lies outside the file");
                file.Position = data;
                var buffer = new byte[256 * 1024];
                long left = entry.Size;
                uint crc = 0;
                while (left > 0)
                {
                    int n = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (n <= 0) throw new HdZipException("unexpected end of file in '" + entry.Name + "'");
                    crc = McvsOffline.Embedded.Install.Crc32.Update(crc, buffer, 0, n);
                    sink(buffer, 0, n);
                    left -= n;
                }
                if (crc != entry.Crc32) throw new HdZipException("'" + entry.Name + "' fails its CRC-32");
            }
        }

        private static byte[] ReadAt(FileStream file, long offset, int count)
        {
            var buffer = new byte[count];
            file.Position = offset;
            int done = 0;
            while (done < count)
            {
                int n = file.Read(buffer, done, count - done);
                if (n <= 0) throw new HdZipException("unexpected end of file");
                done += n;
            }
            return buffer;
        }

        private static int U16(byte[] b, int o)
        {
            return b[o] | (b[o + 1] << 8);
        }

        private static uint U32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }
    }
}
