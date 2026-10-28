using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using McvsOffline.Server.Core.Content;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// Writes a plain zip with stored (uncompressed) entries, the shape the engine's zip search path reads best: no data
    /// descriptors, no ZIP64, entry data 4-byte aligned (an extra field pads the local header, as zipalign does), a fixed
    /// timestamp so the same input gives the same file.
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

        private readonly List<Entry> entries = new List<Entry>();
        private readonly MemoryStream output = new MemoryStream();

        public void Add(string name, byte[] data)
        {
            if (string.IsNullOrEmpty(name) || name.StartsWith("/", StringComparison.Ordinal) || name.Contains("\\"))
                throw new ArgumentException("bad zip entry name '" + name + "'");
            byte[] n = Encoding.UTF8.GetBytes(name);
            var e = new Entry { Name = n, Crc = Crc32.Compute(data), Size = checked((uint)data.Length), Offset = checked((uint)output.Position) };
            int headerEnd = (int)output.Position + 30 + n.Length;
            // zipalign's extra field (id 0xD935, u16 alignment, zero padding) so the data starts on a 4-byte boundary.
            int pad = 6 + (4 - (headerEnd + 6) % 4) % 4;
            Ktx.WriteU32(output, 0x04034b50);
            WriteU16(output, 10);        // version needed
            WriteU16(output, (ushort)(IsAscii(n) ? 0 : 0x0800));
            WriteU16(output, 0);         // stored
            WriteU16(output, DosTime);
            WriteU16(output, DosDate);
            Ktx.WriteU32(output, e.Crc);
            Ktx.WriteU32(output, e.Size);
            Ktx.WriteU32(output, e.Size);
            WriteU16(output, (ushort)n.Length);
            WriteU16(output, (ushort)pad);
            output.Write(n, 0, n.Length);
            WriteU16(output, 0xD935);
            WriteU16(output, (ushort)(pad - 4));
            WriteU16(output, 4);
            for (int i = 0; i < pad - 6; i++) output.WriteByte(0);
            output.Write(data, 0, data.Length);
            entries.Add(e);
        }

        public byte[] ToArray()
        {
            using (var ms = new MemoryStream())
            {
                output.Position = 0;
                output.CopyTo(ms);
                uint cdStart = (uint)ms.Position;
                foreach (Entry e in entries)
                {
                    Ktx.WriteU32(ms, 0x02014b50);
                    WriteU16(ms, 20);            // made by
                    WriteU16(ms, 10);            // needed
                    WriteU16(ms, (ushort)(IsAscii(e.Name) ? 0 : 0x0800));
                    WriteU16(ms, 0);
                    WriteU16(ms, DosTime);
                    WriteU16(ms, DosDate);
                    Ktx.WriteU32(ms, e.Crc);
                    Ktx.WriteU32(ms, e.Size);
                    Ktx.WriteU32(ms, e.Size);
                    WriteU16(ms, (ushort)e.Name.Length);
                    WriteU16(ms, 0);
                    WriteU16(ms, 0);
                    WriteU16(ms, 0);
                    WriteU16(ms, 0);
                    Ktx.WriteU32(ms, 0);
                    Ktx.WriteU32(ms, e.Offset);
                    ms.Write(e.Name, 0, e.Name.Length);
                }
                uint cdSize = (uint)ms.Position - cdStart;
                Ktx.WriteU32(ms, 0x06054b50);
                WriteU16(ms, 0);
                WriteU16(ms, 0);
                WriteU16(ms, (ushort)entries.Count);
                WriteU16(ms, (ushort)entries.Count);
                Ktx.WriteU32(ms, cdSize);
                Ktx.WriteU32(ms, cdStart);
                WriteU16(ms, 0);
                return ms.ToArray();
            }
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
