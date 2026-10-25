using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace McvsOffline.Server.Core.Content
{
    /// <summary>Raised when a data-pack file is present but cannot be decoded.</summary>
    public sealed class DataPackException : Exception
    {
        public DataPackException(string message) : base(message) { }

        public DataPackException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Read-only access to the game's downloaded data pack, i.e. the player's own
    /// <c>&lt;GlfRootPath&gt;/Data</c> directory (report 11). Files are addressed by their logical path
    /// ("Config/crmpreconfiguration.json"); the on-disk name is <c>"%x" % crc32(path)</c> with the first hex digit
    /// as directory ("b/de76cd6.f"). GCBF containers are decoded transparently; raw files are returned as is.
    /// A directory holding the decoded tree (plain logical paths) works too.
    /// Nothing read from here may be redistributed; it is only used at runtime on the player's device.
    /// </summary>
    public sealed class DataPack
    {
        public DataPack(string dataDirectory)
        {
            if (string.IsNullOrEmpty(dataDirectory)) throw new ArgumentException("data directory required", "dataDirectory");
            string full = Path.GetFullPath(dataDirectory);
            // Accept the folder that contains Data/ as well (e.g. ".../files").
            string nested = Path.Combine(full, "Data");
            if (!File.Exists(Path.Combine(full, "MCVS.game")) && File.Exists(Path.Combine(nested, "MCVS.game"))) full = nested;
            DataDirectory = full;
        }

        public string DataDirectory { get; private set; }

        /// <summary>True when the directory exists and looks like a data pack (MCVS.game present).</summary>
        public bool IsValid
        {
            get { return Directory.Exists(DataDirectory) && File.Exists(Path.Combine(DataDirectory, "MCVS.game")); }
        }

        /// <summary>The hashed relative path of a logical path, e.g. "Config/Settings.tx" → "c/a0f90c8.f".</summary>
        public static string HashedPath(string logicalPath)
        {
            uint crc = Crc32.Compute(Encoding.UTF8.GetBytes(logicalPath));
            string hex = crc.ToString("x", CultureInfo.InvariantCulture);
            return hex.Substring(0, 1) + "/" + hex.Substring(1) + ".f";
        }

        /// <summary>Full path of the file backing <paramref name="logicalPath"/>, or null if it is not in the pack.</summary>
        public string Locate(string logicalPath)
        {
            if (string.IsNullOrEmpty(logicalPath)) return null;
            string logical = logicalPath.Replace('\\', '/').TrimStart('/');
            string hashed = Path.Combine(DataDirectory, HashedPath(logical).Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(hashed)) return hashed;
            // Unhashed files (MCVS.game, Config/EngineConfig.json) and decoded trees.
            string plain = Path.Combine(DataDirectory, logical.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(plain) ? plain : null;
        }

        public bool Contains(string logicalPath)
        {
            return Locate(logicalPath) != null;
        }

        /// <summary>Reads and decodes a file. Returns false if absent; throws <see cref="DataPackException"/> if corrupt.</summary>
        public bool TryRead(string logicalPath, out byte[] data)
        {
            data = null;
            string file = Locate(logicalPath);
            if (file == null) return false;
            byte[] raw = File.ReadAllBytes(file);
            try
            {
                data = Gcbf.IsGcbf(raw) ? Gcbf.Decode(raw, true) : raw;
            }
            catch (Exception ex)
            {
                throw new DataPackException("cannot decode '" + logicalPath + "' (" + file + ")", ex);
            }
            return true;
        }

        /// <summary>Reads a text file (UTF-8, BOM stripped).</summary>
        public bool TryReadText(string logicalPath, out string text)
        {
            byte[] data;
            text = null;
            if (!TryRead(logicalPath, out data)) return false;
            text = Encoding.UTF8.GetString(data);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            return true;
        }
    }

    /// <summary>
    /// GCBF block container decoder (report 11 §3; reference implementation tools/datapack/gcbf.py).
    /// Layout (little-endian u32): "GCBF", N, N × {raw_offset, file_offset} seek table (entry N-1 is the end
    /// sentinel), then N-1 blocks, each a 36-byte "padA" header (codec FourCC, payload size, decoded size,
    /// crc32(payload, 1), crc32(decoded, 1), buffer size, 2 reserved) followed by the payload.
    /// </summary>
    public static class Gcbf
    {
        private const uint BlockMagic = 0x41646170;  // "padA" on disk
        private const uint CodecLz4 = 0x4C7A3420;    // 'Lz4 '
        private const uint CodecRaw = 0x52617720;    // 'Raw '
        private const uint CodecZlib = 0x5A6C6962;   // 'Zlib' (not used by the 1.17.32 pack)
        private const uint CodecYapy = 0x59617079;   // 'Yapy' (not used by the 1.17.32 pack)
        private const int BlockHeaderSize = 36;
        private const long MaxDecodedSize = 512L * 1024 * 1024;

        public static bool IsGcbf(byte[] data)
        {
            return data != null && data.Length >= 8 && data[0] == 'G' && data[1] == 'C' && data[2] == 'B' && data[3] == 'F';
        }

        /// <summary>Decodes a whole GCBF file. <paramref name="verify"/> also checks both CRCs of every block.</summary>
        public static byte[] Decode(byte[] file, bool verify)
        {
            if (!IsGcbf(file)) throw new DataPackException("not a GCBF file");
            uint n = U32(file, 4);
            if (n < 1 || 8L + 8L * n > file.Length) throw new DataPackException("bad seek-table count " + n);
            var rawOffsets = new long[n];
            var fileOffsets = new long[n];
            for (int i = 0; i < n; i++)
            {
                rawOffsets[i] = U32(file, 8 + 8 * i);
                fileOffsets[i] = U32(file, 12 + 8 * i);
            }
            if (rawOffsets[0] != 0 || fileOffsets[0] != 8L + 8L * n) throw new DataPackException("seek table does not start at the header end");
            for (int i = 1; i < n; i++)
            {
                if (rawOffsets[i] < rawOffsets[i - 1] || fileOffsets[i] <= fileOffsets[i - 1])
                    throw new DataPackException("seek table not monotonic at entry " + i);
            }
            if (fileOffsets[n - 1] != file.Length) throw new DataPackException("sentinel offset does not match the file size");
            long total = rawOffsets[n - 1];
            if (total > MaxDecodedSize) throw new DataPackException("decoded size too large");

            var output = new byte[total];
            for (int i = 0; i < n - 1; i++)
            {
                int fo = (int)fileOffsets[i];
                int span = (int)(fileOffsets[i + 1] - fileOffsets[i]);
                int expected = (int)(rawOffsets[i + 1] - rawOffsets[i]);
                if (span < BlockHeaderSize) throw new DataPackException("block " + i + " shorter than its header");
                uint magic = U32(file, fo);
                uint codec = U32(file, fo + 4);
                uint payloadSize = U32(file, fo + 8);
                uint decodedSize = U32(file, fo + 12);
                uint crcPayload = U32(file, fo + 16);
                uint crcDecoded = U32(file, fo + 20);
                if (magic != BlockMagic) throw new DataPackException("block " + i + ": bad magic");
                if (payloadSize != span - BlockHeaderSize) throw new DataPackException("block " + i + ": payload size does not fill the block");
                if (decodedSize != expected) throw new DataPackException("block " + i + ": decoded size does not match the seek table");
                int payloadOffset = fo + BlockHeaderSize;
                if (verify && Crc32.Compute(file, payloadOffset, (int)payloadSize, 1) != crcPayload)
                    throw new DataPackException("block " + i + ": payload CRC mismatch");

                int outOffset = (int)rawOffsets[i];
                if (codec == CodecLz4)
                {
                    Lz4Block.Decompress(file, payloadOffset, (int)payloadSize, output, outOffset, expected);
                }
                else if (codec == CodecRaw)
                {
                    if (payloadSize != expected) throw new DataPackException("block " + i + ": raw block size mismatch");
                    Buffer.BlockCopy(file, payloadOffset, output, outOffset, expected);
                }
                else if (codec == CodecZlib || codec == CodecYapy)
                {
                    throw new DataPackException("block " + i + ": codec " + (codec == CodecZlib ? "Zlib" : "Yapy") + " is not supported");
                }
                else
                {
                    throw new DataPackException("block " + i + ": unknown codec 0x" + codec.ToString("x8", CultureInfo.InvariantCulture));
                }

                if (verify && Crc32.Compute(output, outOffset, expected, 1) != crcDecoded)
                    throw new DataPackException("block " + i + ": decoded CRC mismatch");
            }
            return output;
        }

        private static uint U32(byte[] b, int offset)
        {
            if (offset < 0 || offset + 4 > b.Length) throw new DataPackException("read past the end of the file");
            return (uint)(b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24));
        }
    }
}
