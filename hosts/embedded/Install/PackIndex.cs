// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
// Format: report 10 §3.2; parser semantics copied from the installer's own PackFileReader
// (com.gameloft.android.installer.utils.i.a(DataInputStream), raw dex instructions, see docs/INSTALL-OFFLINE.md).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace McvsOffline.Embedded.Install
{
    /// <summary>One entry of the installer's <c>pack.info</c> (a whole file, or one 2 MiB piece of a split file).</summary>
    public sealed class PackEntry
    {
        /// <summary>Folder exactly as stored, e.g. <c>.\\Data\1</c> (dot, two backslashes, Data, backslash, bucket).</summary>
        public string Folder { get; internal set; }

        /// <summary>Name exactly as stored, e.g. <c>0018344.f</c> or <c>0084423.f.split_0001</c>.</summary>
        public string Name { get; internal set; }

        /// <summary>Offset of the entry's ZIP blob in the archive's data region (0 in a rebuilt index).</summary>
        public long Offset { get; internal set; }

        /// <summary>Uncompressed size of the file, or of this piece.</summary>
        public int Size { get; internal set; }

        /// <summary>CRC32 of the uncompressed file or piece (0 in a rebuilt index: the installer then skips its CRC check).</summary>
        public long Crc { get; internal set; }

        /// <summary>Size of the whole file for a split file's pieces (stored after <c>.split_0001</c>), else <see cref="Size"/>.</summary>
        public long TotalSize { get; internal set; }

        /// <summary>Size of the entry's ZIP blob in the archive (0 in a rebuilt index).</summary>
        public int ZipSize { get; internal set; }

        public string ZipName { get; internal set; }

        /// <summary>Piece number of a split file (1-based), 0 for a whole file.</summary>
        public int Piece { get; internal set; }

        /// <summary>The file name on the device (<see cref="Name"/> without the <c>.split_NNNN</c> suffix).</summary>
        public string FileName { get; internal set; }

        /// <summary>The folder on the device, relative to the game root, with '/' (the installer's mapping), e.g. <c>Data/1</c>.</summary>
        public string RelativeDirectory { get; internal set; }

        /// <summary>Path of the file relative to the game root, e.g. <c>Data/1/0018344.f</c>.</summary>
        public string RelativePath
        {
            get { return RelativeDirectory.Length == 0 ? FileName : RelativeDirectory + "/" + FileName; }
        }

        /// <summary>The line the installer keeps for a downloaded piece in <c>d_o_w_n_l_o_a_d_e_d.txt</c> (folder + name).</summary>
        public string JournalKey
        {
            get { return Folder + Name; }
        }
    }

    /// <summary>
    /// The installer's index of the data pack (<c>getFilesDir()/pack.info</c>, the header of the CDN's <c>.amz</c> archive):
    /// Java DataOutputStream, big-endian, modified UTF-8 strings. Sections repeat until the end of the file:
    /// <code>
    /// UTF s; if s starts with "version: ": version = int(s[9..]), int split_kb, UTF folder; else folder = s, version = -1
    /// int count; count x { long offset; int size; long crc; UTF name; [long total if version &gt;= 101 and name ends ".split_0001"];
    ///                      int zsize; UTF zname }
    /// </code>
    /// <see cref="Parse"/> accepts exactly what the installer's own reader accepts and, on top of that, refuses anything that
    /// is not a plain data-pack index (paths outside <c>Data/</c>, native libraries, pieces out of order): such an index is
    /// never written back or trusted.
    /// </summary>
    public sealed class PackIndex
    {
        /// <summary>Piece size of split files (the installer hard-codes 2097152 in Section, CRC and goodSize).</summary>
        public const int SplitSize = 2097152;

        /// <summary>The section version every 1.17.32 index uses (and our rebuilt one).</summary>
        public const int FormatVersion = 101;

        private const string VersionPrefix = "version: ";
        private const string SplitMarker = ".split_";

        private PackIndex(byte[] raw, List<PackEntry> entries)
        {
            Raw = raw;
            Entries = entries.AsReadOnly();
            long bytes = 0;
            int files = 0;
            bool rebuilt = entries.Count > 0;
            foreach (PackEntry e in entries)
            {
                if (e.Piece <= 1)
                {
                    files++;
                    bytes += e.Piece == 0 ? e.Size : e.TotalSize;
                }
                if (e.ZipSize != 0) rebuilt = false;
            }
            LogicalFiles = files;
            LogicalBytes = bytes;
            IsRebuilt = rebuilt;
        }

        /// <summary>The bytes as read or written (what goes to <c>getFilesDir()/pack.info</c>).</summary>
        public byte[] Raw { get; private set; }

        public IList<PackEntry> Entries { get; private set; }

        /// <summary>Files on the device (a split file counts once).</summary>
        public int LogicalFiles { get; private set; }

        /// <summary>Total size of the files on the device.</summary>
        public long LogicalBytes { get; private set; }

        /// <summary>
        /// True for an index rebuilt from the files on the device (<see cref="Rebuild"/>): every ZIP size is 0, which never
        /// happens in a CDN index (each entry is a ZIP file of at least 22 bytes).
        /// </summary>
        public bool IsRebuilt { get; private set; }

        /// <summary>Parses an index; throws <see cref="FormatException"/> when the installer could not use it or it is not a data-pack index.</summary>
        public static PackIndex Parse(byte[] raw)
        {
            if (raw == null) throw new ArgumentNullException("raw");
            if (raw.Length == 0) throw new FormatException("empty index");
            var reader = new BigEndianReader(raw);
            var entries = new List<PackEntry>();
            long total = 0;   // the installer keeps the last split total across entries and sections
            string currentSplit = null;
            int nextPiece = 0;
            do
            {
                string s = reader.ReadUtf();
                int version;
                string folder;
                if (s.StartsWith(VersionPrefix, StringComparison.Ordinal))
                {
                    if (!int.TryParse(s.Substring(VersionPrefix.Length), System.Globalization.NumberStyles.Integer,
                                      System.Globalization.CultureInfo.InvariantCulture, out version))
                        throw new FormatException("bad section header '" + s + "'");
                    reader.ReadInt32();   // split size in KiB (2048); the installer ignores it
                    folder = reader.ReadUtf();
                }
                else
                {
                    folder = s;
                    version = -1;
                }
                string directory = MapFolder(folder);
                int count = reader.ReadInt32();
                if (count < 0 || count > 1000000) throw new FormatException("bad entry count " + count + " in section '" + folder + "'");
                for (int i = 0; i < count; i++)
                {
                    var e = new PackEntry { Folder = folder, RelativeDirectory = directory };
                    e.Offset = reader.ReadInt64();
                    e.Size = reader.ReadInt32();
                    e.Crc = reader.ReadInt64();
                    e.Name = reader.ReadUtf();
                    if (version >= FormatVersion)
                    {
                        if (e.Name.EndsWith(SplitMarker + "0001", StringComparison.Ordinal)) total = reader.ReadInt64();
                        e.TotalSize = e.Name.Contains(SplitMarker) ? total : e.Size;
                    }
                    else
                    {
                        e.TotalSize = e.Name.Contains(SplitMarker) ? total : e.Size;
                    }
                    e.ZipSize = reader.ReadInt32();
                    e.ZipName = reader.ReadUtf();
                    CheckEntry(e, ref currentSplit, ref nextPiece);
                    entries.Add(e);
                }
            }
            while (!reader.AtEnd);
            if (currentSplit != null) CheckSplitComplete(entries[entries.Count - 1]);
            if (entries.Count == 0) throw new FormatException("index lists no files");
            return new PackIndex(raw, entries);
        }

        /// <summary>Parses, or returns null with the reason.</summary>
        public static PackIndex TryParse(byte[] raw, out string error)
        {
            try
            {
                error = null;
                return Parse(raw);
            }
            catch (FormatException ex)
            {
                error = ex.Message;
                return null;
            }
            catch (EndOfStreamException)
            {
                error = "truncated";
                return null;
            }
        }

        /// <summary>A file for <see cref="Rebuild"/>: its path relative to the game root ('/' separators) and its size.</summary>
        public struct FileInfoEntry
        {
            public FileInfoEntry(string relativePath, long size)
            {
                RelativePath = relativePath;
                Size = size;
            }

            public string RelativePath;
            public long Size;
        }

        /// <summary>
        /// An index that lists exactly the given files, laid out like Gameloft's (same folders, same names, files larger than
        /// 2 MiB split into 2 MiB pieces with the total after piece 1), with offsets, CRCs and ZIP sizes 0. The installer then
        /// checks existence and size only (it skips the CRC check of entries whose CRC is 0), and its folder+name keys match a
        /// CDN index of the same data, so a later switch to the CDN's index sees the same files.
        /// </summary>
        public static PackIndex Rebuild(IList<FileInfoEntry> files)
        {
            var byDir = new Dictionary<string, List<FileInfoEntry>>(StringComparer.Ordinal);
            var dirs = new List<string>();
            foreach (FileInfoEntry f in files)
            {
                string path = f.RelativePath ?? "";
                int slash = path.LastIndexOf('/');
                if (slash <= 0) throw new ArgumentException("not under Data/: " + path);
                string dir = path.Substring(0, slash);
                if (dir != "Data" && !dir.StartsWith("Data/", StringComparison.Ordinal)) throw new ArgumentException("not under Data/: " + path);
                if (f.Size < 0 || f.Size > 0x7FFFFFFFL) throw new ArgumentException("size out of range: " + path);
                List<FileInfoEntry> list;
                if (!byDir.TryGetValue(dir, out list))
                {
                    byDir[dir] = list = new List<FileInfoEntry>();
                    dirs.Add(dir);
                }
                list.Add(f);
            }
            dirs.Sort(StringComparer.Ordinal);
            var w = new BigEndianWriter();
            foreach (string dir in dirs)
            {
                List<FileInfoEntry> list = byDir[dir];
                list.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
                var names = new List<string>();
                var sizes = new List<int>();
                var totals = new List<long>();
                foreach (FileInfoEntry f in list)
                {
                    string name = f.RelativePath.Substring(dir.Length + 1);
                    CheckName(name);
                    // Split exactly like Gameloft's packer (every 1.17.32 file above 2 MiB is split, smaller ones never are). A size
                    // that is an exact multiple of 2 MiB stays whole: its last piece would fail the installer's goodSize test.
                    if (f.Size > SplitSize && f.Size % SplitSize != 0)
                    {
                        int pieces = (int)((f.Size + SplitSize - 1) / SplitSize);
                        for (int p = 1; p <= pieces; p++)
                        {
                            names.Add(name + SplitMarker + p.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
                            sizes.Add(p < pieces ? SplitSize : (int)(f.Size - (long)SplitSize * (pieces - 1)));
                            totals.Add(f.Size);
                        }
                    }
                    else
                    {
                        names.Add(name);
                        sizes.Add((int)f.Size);
                        totals.Add(f.Size);
                    }
                }
                w.WriteUtf(VersionPrefix + FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteInt32(SplitSize / 1024);
                w.WriteUtf(FolderFor(dir));
                w.WriteInt32(names.Count);
                for (int i = 0; i < names.Count; i++)
                {
                    w.WriteInt64(0);
                    w.WriteInt32(sizes[i]);
                    w.WriteInt64(0);
                    w.WriteUtf(names[i]);
                    if (names[i].EndsWith(SplitMarker + "0001", StringComparison.Ordinal)) w.WriteInt64(totals[i]);
                    w.WriteInt32(0);
                    w.WriteUtf(names[i] + ".zip");
                }
            }
            return Parse(w.ToArray());
        }

        /// <summary>The installer's folder mapping: remove <c>.\\</c>, then <c>.\</c>, then turn '\' into '/'.</summary>
        public static string MapFolder(string folder)
        {
            string mapped = (folder ?? "").Replace(".\\\\", "").Replace(".\\", "").Replace("\\", "/");
            if (mapped.Length == 0) return mapped;
            if (mapped != "Data" && !mapped.StartsWith("Data/", StringComparison.Ordinal))
                throw new FormatException("folder '" + folder + "' is outside Data/");
            foreach (string part in mapped.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == "..") throw new FormatException("unsafe folder '" + folder + "'");
            }
            return mapped;
        }

        /// <summary>The stored folder for a directory relative to the game root: <c>Data/1</c> -&gt; <c>.\\Data\1</c> (as in Gameloft's index).</summary>
        public static string FolderFor(string relativeDirectory)
        {
            return ".\\\\" + relativeDirectory.Replace('/', '\\');
        }

        private static void CheckName(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "." || name == ".." || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 ||
                name.IndexOf('?') >= 0 || name.IndexOf(':') >= 0 || name.IndexOf('\0') >= 0)
                throw new FormatException("unsafe file name '" + name + "'");
        }

        private static void CheckEntry(PackEntry e, ref string currentSplit, ref int nextPiece)
        {
            CheckName(e.Name);
            if (e.RelativeDirectory.Length == 0) throw new FormatException("file '" + e.Name + "' is outside Data/");
            if (e.Size < 0 || e.ZipSize < 0) throw new FormatException("negative size for '" + e.Name + "'");
            int split = e.Name.LastIndexOf(SplitMarker, StringComparison.Ordinal);
            if (split < 0)
            {
                if (currentSplit != null) throw new FormatException("split file '" + currentSplit + "' ends before its last piece");
                e.Piece = 0;
                e.FileName = e.Name;
            }
            else
            {
                string suffix = e.Name.Substring(split + SplitMarker.Length);
                int piece;
                if (suffix.Length != 4 || !int.TryParse(suffix, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out piece) || piece < 1)
                    throw new FormatException("bad piece name '" + e.Name + "'");
                e.Piece = piece;
                e.FileName = e.Name.Substring(0, split);
                CheckName(e.FileName);
                string key = e.Folder + "\n" + e.FileName;
                if (piece == 1)
                {
                    if (currentSplit != null) throw new FormatException("split file '" + currentSplit + "' ends before its last piece");
                    if (e.TotalSize <= SplitSize) throw new FormatException("bad total size for '" + e.Name + "'");
                    currentSplit = key;
                    nextPiece = 2;
                }
                else
                {
                    if (currentSplit != key || piece != nextPiece) throw new FormatException("piece '" + e.Name + "' out of order");
                    nextPiece++;
                }
                int pieces = (int)((e.TotalSize + SplitSize - 1) / SplitSize);
                if (piece > pieces) throw new FormatException("piece '" + e.Name + "' beyond the file's size");
                if (piece < pieces && e.Size != SplitSize) throw new FormatException("piece '" + e.Name + "' is not " + SplitSize + " bytes");
                if (piece == pieces)
                {
                    if (e.Size != e.TotalSize - (long)SplitSize * (pieces - 1)) throw new FormatException("last piece '" + e.Name + "' has the wrong size");
                    currentSplit = null;
                }
            }
            if (e.FileName.EndsWith(".so", StringComparison.Ordinal)) throw new FormatException("native library '" + e.Name + "' (not a data-pack index)");
        }

        private static void CheckSplitComplete(PackEntry last)
        {
            throw new FormatException("split file '" + last.FileName + "' ends before its last piece");
        }

        /// <summary>Java DataInputStream reads over a byte array; EndOfStreamException past the end.</summary>
        private sealed class BigEndianReader
        {
            private readonly byte[] data;
            private int pos;

            public BigEndianReader(byte[] data)
            {
                this.data = data;
            }

            public bool AtEnd
            {
                get { return pos >= data.Length; }
            }

            private void Need(int n)
            {
                if (n < 0 || data.Length - pos < n) throw new EndOfStreamException();
            }

            public int ReadInt32()
            {
                Need(4);
                int v = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
                pos += 4;
                return v;
            }

            public long ReadInt64()
            {
                long hi = (uint)ReadInt32();
                long lo = (uint)ReadInt32();
                return (hi << 32) | lo;
            }

            /// <summary>DataInputStream.readUTF: u16 length, modified UTF-8 (1-3 byte forms; NUL as C0 80).</summary>
            public string ReadUtf()
            {
                Need(2);
                int length = (data[pos] << 8) | data[pos + 1];
                pos += 2;
                Need(length);
                int end = pos + length;
                var sb = new StringBuilder(length);
                while (pos < end)
                {
                    int a = data[pos++];
                    if (a < 0x80)
                    {
                        if (a == 0) throw new FormatException("malformed modified UTF-8");
                        sb.Append((char)a);
                    }
                    else if ((a & 0xE0) == 0xC0)
                    {
                        if (pos >= end) throw new FormatException("malformed modified UTF-8");
                        int b = data[pos++];
                        if ((b & 0xC0) != 0x80) throw new FormatException("malformed modified UTF-8");
                        sb.Append((char)(((a & 0x1F) << 6) | (b & 0x3F)));
                    }
                    else if ((a & 0xF0) == 0xE0)
                    {
                        if (end - pos < 2) throw new FormatException("malformed modified UTF-8");
                        int b = data[pos++];
                        int c = data[pos++];
                        if ((b & 0xC0) != 0x80 || (c & 0xC0) != 0x80) throw new FormatException("malformed modified UTF-8");
                        sb.Append((char)(((a & 0x0F) << 12) | ((b & 0x3F) << 6) | (c & 0x3F)));
                    }
                    else
                    {
                        throw new FormatException("malformed modified UTF-8");
                    }
                }
                return sb.ToString();
            }
        }

        /// <summary>Java DataOutputStream writes (ASCII strings only: every name in the data pack is ASCII).</summary>
        private sealed class BigEndianWriter
        {
            private readonly MemoryStream stream = new MemoryStream();

            public void WriteInt32(int v)
            {
                stream.WriteByte((byte)(v >> 24));
                stream.WriteByte((byte)(v >> 16));
                stream.WriteByte((byte)(v >> 8));
                stream.WriteByte((byte)v);
            }

            public void WriteInt64(long v)
            {
                WriteInt32((int)(v >> 32));
                WriteInt32((int)v);
            }

            public void WriteUtf(string s)
            {
                if (s.Length > 65535) throw new ArgumentException("string too long");
                foreach (char ch in s)
                {
                    if (ch == 0 || ch > 0x7E) throw new ArgumentException("non-ASCII name '" + s + "'");
                }
                stream.WriteByte((byte)(s.Length >> 8));
                stream.WriteByte((byte)s.Length);
                byte[] bytes = Encoding.ASCII.GetBytes(s);
                stream.Write(bytes, 0, bytes.Length);
            }

            public byte[] ToArray()
            {
                return stream.ToArray();
            }
        }
    }
}
