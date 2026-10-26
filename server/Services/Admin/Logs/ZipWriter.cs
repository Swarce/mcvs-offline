using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using McvsOffline.Server.Core.Content;

namespace McvsOffline.Server.Services.Admin.Logs
{
    /// <summary>An entry compressed in memory, ready to be written (its size is known before it is added).</summary>
    internal sealed class ZipEntryData
    {
        public string Name { get; set; }

        public byte[] NameBytes { get; set; }

        /// <summary>8 = deflate, 0 = stored.</summary>
        public ushort Method { get; set; }

        public uint Crc { get; set; }

        public long UncompressedSize { get; set; }

        public byte[] Data { get; set; }

        /// <summary>Bytes this entry adds to the archive: local header, data and its central directory record.</summary>
        public long ArchiveBytes
        {
            get { return ZipWriter.LocalHeaderSize + NameBytes.Length + Data.Length + ZipWriter.CentralHeaderSize + NameBytes.Length; }
        }
    }

    /// <summary>
    /// A minimal zip writer (design §7.2): each entry is snapshotted into a byte array by the caller, its CRC-32 computed
    /// over the uncompressed bytes, compressed with DeflateStream into memory (method 8; stored, method 0, when deflate is
    /// unavailable or does not make it smaller), then written with its sizes in the local header. Central directory at the
    /// end; no data descriptors, no zip64, no extra fields (archives stay far below 4 GiB). File.Copy and ZipArchive are not
    /// used (trimmed or absent in the game's BCL).
    /// </summary>
    internal sealed class ZipWriter : IDisposable
    {
        public const int LocalHeaderSize = 30;
        public const int CentralHeaderSize = 46;
        public const int EndRecordSize = 22;

        private readonly Stream output;
        private readonly bool deflate;
        private readonly List<KeyValuePair<ZipEntryData, long>> written = new List<KeyValuePair<ZipEntryData, long>>();
        private readonly ushort dosTime;
        private readonly ushort dosDate;
        private long position;
        private bool finished;

        /// <summary><paramref name="timeLocal"/> is the modification time every entry gets (the zip's local DOS time).</summary>
        public ZipWriter(Stream output, bool deflate, DateTime timeLocal)
        {
            this.output = output;
            this.deflate = deflate;
            int year = timeLocal.Year < 1980 ? 1980 : timeLocal.Year > 2107 ? 2107 : timeLocal.Year;
            dosTime = (ushort)((timeLocal.Hour << 11) | (timeLocal.Minute << 5) | (timeLocal.Second / 2));
            dosDate = (ushort)(((year - 1980) << 9) | (timeLocal.Month << 5) | timeLocal.Day);
        }

        /// <summary>Bytes written so far.</summary>
        public long Position
        {
            get { return position; }
        }

        public int Count
        {
            get { return written.Count; }
        }

        /// <summary>What the archive would measure if it were finished now.</summary>
        public long FinishedSize
        {
            get
            {
                long central = 0;
                foreach (var kv in written) central += CentralHeaderSize + kv.Key.NameBytes.Length;
                return position + central + EndRecordSize;
            }
        }

        /// <summary>Compresses an entry (not written yet).</summary>
        public ZipEntryData Prepare(string name, byte[] data)
        {
            var entry = new ZipEntryData();
            entry.Name = name;
            entry.NameBytes = Encoding.UTF8.GetBytes(name);
            entry.Crc = Crc32.Compute(data);
            entry.UncompressedSize = data.Length;
            entry.Method = 0;
            entry.Data = data;
            if (deflate && data.Length > 0)
            {
                var buffer = new MemoryStream();
                using (var stream = new DeflateStream(buffer, CompressionMode.Compress, true)) stream.Write(data, 0, data.Length);
                if (buffer.Length < data.Length)
                {
                    entry.Method = 8;
                    entry.Data = buffer.ToArray();
                }
            }
            return entry;
        }

        public void Add(ZipEntryData entry)
        {
            if (finished) throw new InvalidOperationException("the archive is finished");
            long offset = position;
            var header = new List<byte>(LocalHeaderSize + entry.NameBytes.Length);
            UInt32(header, 0x04034b50);
            UInt16(header, (ushort)(entry.Method == 8 ? 20 : 10));   // version needed to extract
            UInt16(header, 0);                                       // flags: no data descriptor
            UInt16(header, entry.Method);
            UInt16(header, dosTime);
            UInt16(header, dosDate);
            UInt32(header, entry.Crc);
            UInt32(header, (uint)entry.Data.Length);
            UInt32(header, (uint)entry.UncompressedSize);
            UInt16(header, (ushort)entry.NameBytes.Length);
            UInt16(header, 0);                                       // extra field length
            header.AddRange(entry.NameBytes);
            Write(header.ToArray());
            Write(entry.Data);
            written.Add(new KeyValuePair<ZipEntryData, long>(entry, offset));
        }

        /// <summary>Writes the central directory and the end record.</summary>
        public void Finish()
        {
            if (finished) return;
            finished = true;
            long start = position;
            foreach (var kv in written)
            {
                ZipEntryData entry = kv.Key;
                var record = new List<byte>(CentralHeaderSize + entry.NameBytes.Length);
                UInt32(record, 0x02014b50);
                UInt16(record, 20);                                      // version made by: 2.0, MS-DOS attributes
                UInt16(record, (ushort)(entry.Method == 8 ? 20 : 10));
                UInt16(record, 0);
                UInt16(record, entry.Method);
                UInt16(record, dosTime);
                UInt16(record, dosDate);
                UInt32(record, entry.Crc);
                UInt32(record, (uint)entry.Data.Length);
                UInt32(record, (uint)entry.UncompressedSize);
                UInt16(record, (ushort)entry.NameBytes.Length);
                UInt16(record, 0);                                       // extra
                UInt16(record, 0);                                       // comment
                UInt16(record, 0);                                       // disk number
                UInt16(record, 0);                                       // internal attributes
                UInt32(record, 0);                                       // external attributes
                UInt32(record, (uint)kv.Value);                          // local header offset
                record.AddRange(entry.NameBytes);
                Write(record.ToArray());
            }
            long size = position - start;
            var end = new List<byte>(EndRecordSize);
            UInt32(end, 0x06054b50);
            UInt16(end, 0);
            UInt16(end, 0);
            UInt16(end, (ushort)written.Count);
            UInt16(end, (ushort)written.Count);
            UInt32(end, (uint)size);
            UInt32(end, (uint)start);
            UInt16(end, 0);
            Write(end.ToArray());
            output.Flush();
        }

        public void Dispose()
        {
            Finish();
        }

        private void Write(byte[] bytes)
        {
            output.Write(bytes, 0, bytes.Length);
            position += bytes.Length;
        }

        private static void UInt16(List<byte> buffer, ushort value)
        {
            buffer.Add((byte)value);
            buffer.Add((byte)(value >> 8));
        }

        private static void UInt32(List<byte> buffer, uint value)
        {
            buffer.Add((byte)value);
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 24));
        }
    }
}
