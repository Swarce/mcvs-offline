using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using McvsOffline.Server.Core.Content;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// Writes a plain zip with stored (uncompressed) entries to a stream: no data descriptors, no ZIP64, a fixed timestamp
    /// (1980-01-01 00:00) so the same input gives the same bytes. Two shapes:
    /// <list type="bullet">
    /// <item><b>aligned</b> (the HD pack, docs/design/hd-textures.md §2): entry data starts on a 4-byte boundary (a zipalign
    /// extra field, id 0xD935, pads the local header);</item>
    /// <item><b>unaligned</b> (the ETC pack's split-alpha textures): no extra fields, version 2.0, exactly as Gameloft's
    /// files are laid out.</item>
    /// </list>
    /// </summary>
    internal sealed class StoredZipWriter
    {
        private sealed class Entry
        {
            public byte[] Name;
            public uint Crc;
            public uint Size;
            public uint Offset;
        }

        private const ushort DosTime = 0;
        private const ushort DosDate = (0 << 9) | (1 << 5) | 1;   // 1980-01-01
        private const long Limit = 0xFFFFFFFFL;

        private readonly List<Entry> entries = new List<Entry>();
        private readonly Stream output;
        private readonly bool align;
        private readonly ushort version;
        private long position;
        private bool finished;

        public StoredZipWriter(Stream output, bool align, ushort version = 20)
        {
            this.output = output;
            this.align = align;
            this.version = version;
        }

        public int Count => entries.Count;

        /// <summary>Bytes written so far.</summary>
        public long Length => position;

        /// <summary>A whole zip in memory.</summary>
        public static byte[] Build(bool align, IEnumerable<KeyValuePair<string, byte[]>> files)
        {
            using (var ms = new MemoryStream())
            {
                var zip = new StoredZipWriter(ms, align);
                foreach (KeyValuePair<string, byte[]> f in files) zip.Add(f.Key, f.Value);
                zip.Finish();
                return ms.ToArray();
            }
        }

        public void Add(string name, byte[] data)
        {
            Add(name, data, Crc32.Compute(data));
        }

        /// <summary>Adds an entry whose CRC-32 the caller already computed.</summary>
        public void Add(string name, byte[] data, uint crc)
        {
            if (finished) throw new InvalidOperationException("the zip is finished");
            if (string.IsNullOrEmpty(name) || name.StartsWith("/", StringComparison.Ordinal) || name.Contains("\\"))
                throw new ArgumentException("bad zip entry name '" + name + "'");
            if (entries.Count >= 65535) throw new IOException("more than 65,535 entries need ZIP64, which the pack does not use");
            byte[] n = Encoding.UTF8.GetBytes(name);
            int pad = 0;
            if (align)
            {
                long headerEnd = position + 30 + n.Length;
                // zipalign's extra field (id 0xD935, u16 alignment, zero padding) so the data starts on a 4-byte boundary.
                pad = 6 + (int)((4 - (headerEnd + 6) % 4) % 4);
            }
            if (position + 30 + n.Length + pad + data.Length > Limit)
                throw new IOException("the zip would pass 4 GiB, which needs ZIP64; choose a smaller scope or fewer maps");
            var e = new Entry { Name = n, Crc = crc, Size = (uint)data.Length, Offset = (uint)position };
            var h = new MemoryStream(30 + n.Length + pad);
            Ktx.WriteU32(h, 0x04034b50);
            WriteU16(h, version);
            WriteU16(h, (ushort)(IsAscii(n) ? 0 : 0x0800));
            WriteU16(h, 0);              // stored
            WriteU16(h, DosTime);
            WriteU16(h, DosDate);
            Ktx.WriteU32(h, e.Crc);
            Ktx.WriteU32(h, e.Size);
            Ktx.WriteU32(h, e.Size);
            WriteU16(h, (ushort)n.Length);
            WriteU16(h, (ushort)pad);
            h.Write(n, 0, n.Length);
            if (align)
            {
                WriteU16(h, 0xD935);
                WriteU16(h, (ushort)(pad - 4));
                WriteU16(h, 4);
                for (int i = 0; i < pad - 6; i++) h.WriteByte(0);
            }
            Write(h.ToArray());
            Write(data);
            entries.Add(e);
        }

        /// <summary>Writes the central directory and the end record.</summary>
        public void Finish()
        {
            if (finished) return;
            finished = true;
            long cdStart = position;
            var cd = new MemoryStream();
            foreach (Entry e in entries)
            {
                Ktx.WriteU32(cd, 0x02014b50);
                WriteU16(cd, version);       // made by
                WriteU16(cd, version);       // needed
                WriteU16(cd, (ushort)(IsAscii(e.Name) ? 0 : 0x0800));
                WriteU16(cd, 0);
                WriteU16(cd, DosTime);
                WriteU16(cd, DosDate);
                Ktx.WriteU32(cd, e.Crc);
                Ktx.WriteU32(cd, e.Size);
                Ktx.WriteU32(cd, e.Size);
                WriteU16(cd, (ushort)e.Name.Length);
                WriteU16(cd, 0);
                WriteU16(cd, 0);
                WriteU16(cd, 0);
                WriteU16(cd, 0);
                Ktx.WriteU32(cd, 0);
                Ktx.WriteU32(cd, e.Offset);
                cd.Write(e.Name, 0, e.Name.Length);
            }
            long cdSize = cd.Length;
            if (cdStart + cdSize + 22 > Limit) throw new IOException("the zip would pass 4 GiB, which needs ZIP64");
            Ktx.WriteU32(cd, 0x06054b50);
            WriteU16(cd, 0);
            WriteU16(cd, 0);
            WriteU16(cd, (ushort)entries.Count);
            WriteU16(cd, (ushort)entries.Count);
            Ktx.WriteU32(cd, (uint)cdSize);
            Ktx.WriteU32(cd, (uint)cdStart);
            WriteU16(cd, 0);
            Write(cd.ToArray());
            output.Flush();
        }

        private void Write(byte[] bytes)
        {
            output.Write(bytes, 0, bytes.Length);
            position += bytes.Length;
        }

        private static bool IsAscii(byte[] name)
        {
            foreach (byte b in name)
            {
                if (b >= 0x80) return false;
            }
            return true;
        }

        private static void WriteU16(Stream s, ushort v)
        {
            s.WriteByte((byte)v);
            s.WriteByte((byte)(v >> 8));
        }
    }
}
