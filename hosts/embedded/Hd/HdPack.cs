// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
// Format: docs/design/hd-textures.md §2-§4 (the converter's contract); what the installer reads of it: docs/design/hd-install.md.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Embedded.Hd
{
    /// <summary>One file of an HD pack: the Data/ file it replaces, the original it must replace, and the new bytes (a zip entry).</summary>
    internal sealed class HdPackFile
    {
        /// <summary><c>Data/&lt;h&gt;/&lt;rest&gt;.f</c>, relative to the game root (also the zip entry's name).</summary>
        public string Path;

        public long OriginalSize;
        public uint OriginalCrc;
        public long HdSize;
        public uint HdCrc;

        /// <summary>
        /// Files of one group (<c>lightmaps:&lt;Level&gt;</c>: all lightmaps of a level) are installed together or not at all. Never
        /// null: the manifest's <c>null</c> is the file alone (its path).
        /// </summary>
        public string Group;

        public StoredZipEntry Entry;

        /// <summary>The pack's zip (for <see cref="StoredZip.CopyStored"/>).</summary>
        public StoredZip Zip;
    }

    /// <summary>
    /// An HD texture pack as <c>tools/hd-textures build</c> writes it, <c>McvsOffline/hd/mcvs-hd-*.zip</c> (docs/design/hd-textures.md
    /// §2-§3): a zip with stored entries <c>Data/&lt;h&gt;/&lt;rest&gt;.f</c> (the new files, named as in the game's Data folder) and a
    /// manifest <c>mcvs-hd.json</c> (<c>format</c> 1, <c>kind</c> <c>hd-textures</c>) that lists, for every file, its <c>entry</c>, the
    /// size and CRC-32 of the <c>original</c> it replaces and of the <c>replacement</c>, and its <c>group</c>. <see cref="Open"/> checks
    /// the whole directory and manifest (not the entries' bytes: those are checked while they are copied).
    /// </summary>
    internal sealed class HdPack
    {
        public const string ManifestEntry = "mcvs-hd.json";
        public const string Kind = "hd-textures";
        public const int Format = 1;
        public const string FilePrefix = "mcvs-hd-";
        public const string FileSuffix = ".zip";

        private HdPack()
        {
            Files = new List<HdPackFile>();
        }

        public string ZipPath { get; private set; }

        /// <summary>The zip's file name.</summary>
        public string FileName { get; private set; }

        /// <summary>The CRC-32 of the manifest, as 8 hex digits (it changes with every build of a pack: <c>created</c>).</summary>
        public string Id { get; private set; }

        /// <summary>For the status line: the file name.</summary>
        public string Name { get; private set; }

        /// <summary>The Android data pack the pack was made for ("ETC", "ASTC"), or null when the manifest does not say.</summary>
        public string Variant { get; private set; }

        public List<HdPackFile> Files { get; private set; }

        public long HdBytes
        {
            get
            {
                long n = 0;
                foreach (HdPackFile f in Files) n += f.HdSize;
                return n;
            }
        }

        internal StoredZip Zip { get; private set; }

        /// <summary>The packs in <paramref name="hdDir"/> (<c>mcvs-hd-*.zip</c>), sorted by name (ordinal).</summary>
        public static List<string> Candidates(string hdDir)
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(hdDir)) return list;
                foreach (string file in Directory.GetFiles(hdDir))
                {
                    string name = System.IO.Path.GetFileName(file);
                    if (name.StartsWith(FilePrefix, StringComparison.Ordinal) && name.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase)) list.Add(file);
                }
            }
            catch (Exception)
            {
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>Reads and checks the pack; throws <see cref="HdZipException"/> with the reason.</summary>
        public static HdPack Open(string zipPath)
        {
            StoredZip zip = StoredZip.Open(zipPath);
            StoredZipEntry manifestEntry = zip.Find(ManifestEntry);
            if (manifestEntry == null) throw new HdZipException("no " + ManifestEntry + " in the zip");
            if (manifestEntry.Method != 0) throw new HdZipException(ManifestEntry + " is compressed (the pack's entries must be stored)");
            if (manifestEntry.Size > 16 * 1024 * 1024) throw new HdZipException(ManifestEntry + " is too large");
            byte[] raw = zip.ReadStored(manifestEntry);
            object parsed;
            if (!MiniJson.TryParse(Encoding.UTF8.GetString(raw), out parsed) || !(parsed is Dictionary<string, object>))
                throw new HdZipException(ManifestEntry + " is not a JSON object");
            var manifest = (Dictionary<string, object>)parsed;
            if (JsonValue.GetString(manifest, "kind") != Kind) throw new HdZipException(ManifestEntry + ": kind is not '" + Kind + "' (not a pack of tools/hd-textures)");
            long? format = JsonValue.AsLong(JsonValue.Get(manifest, "format"));
            if (format == null) throw new HdZipException(ManifestEntry + ": format is not a number");
            if (format.Value != Format) throw new HdZipException(ManifestEntry + ": format " + format.Value + " (this build reads format " + Format + ")");
            var pack = new HdPack
            {
                ZipPath = zipPath,
                FileName = System.IO.Path.GetFileName(zipPath),
                Zip = zip,
                Id = McvsOffline.Embedded.Install.Crc32.Update(0, raw, 0, raw.Length).ToString("x8", CultureInfo.InvariantCulture),
                Name = System.IO.Path.GetFileName(zipPath),
                Variant = JsonValue.GetString(manifest, "pack"),
            };
            if (pack.Variant != null) pack.Variant = pack.Variant.Trim().ToUpperInvariant();
            List<object> files = JsonValue.GetArray(manifest, "files");
            if (files == null || files.Count == 0) throw new HdZipException(ManifestEntry + " lists no files");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < files.Count; i++)
            {
                var o = files[i] as Dictionary<string, object>;
                if (o == null) throw new HdZipException(ManifestEntry + ": files[" + i + "] is not an object");
                string path = JsonValue.GetString(o, "entry");
                if (!IsDataPath(path)) throw new HdZipException(ManifestEntry + ": files[" + i + "] has no usable entry ('" + path + "'; expected Data/<h>/<name>.f)");
                if (!seen.Add(path)) throw new HdZipException(ManifestEntry + ": " + path + " is listed twice");
                var f = new HdPackFile { Path = path, Group = JsonValue.GetString(o, "group") ?? path };
                ReadSizeCrc(JsonValue.GetObject(o, "original"), path, "original", out f.OriginalSize, out f.OriginalCrc);
                ReadSizeCrc(JsonValue.GetObject(o, "replacement"), path, "replacement", out f.HdSize, out f.HdCrc);
                if (f.OriginalSize == f.HdSize && f.OriginalCrc == f.HdCrc) throw new HdZipException(ManifestEntry + ": " + path + " is the same as the original");
                f.Entry = zip.Find(path);
                f.Zip = zip;
                if (f.Entry == null) throw new HdZipException(path + " is listed in " + ManifestEntry + " but not in the zip");
                if (f.Entry.Method != 0) throw new HdZipException(path + " is compressed (the pack's entries must be stored)");
                if (f.Entry.Size != f.HdSize || f.Entry.Crc32 != f.HdCrc)
                    throw new HdZipException(path + " in the zip (" + f.Entry.Size + " bytes, CRC-32 " + Hex(f.Entry.Crc32) + ") is not the file " + ManifestEntry +
                                             " describes (" + f.HdSize + " bytes, CRC-32 " + Hex(f.HdCrc) + ")");
                pack.Files.Add(f);
            }
            return pack;
        }

        /// <summary><c>Data/&lt;dir&gt;/&lt;name&gt;.f</c> with one plain directory level (the game's hashed layout).</summary>
        internal static bool IsDataPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0 || path.IndexOf('\0') >= 0) return false;
            string[] parts = path.Split('/');
            if (parts.Length != 3 || parts[0] != "Data" || !parts[2].EndsWith(".f", StringComparison.Ordinal)) return false;
            foreach (string part in parts)
            {
                if (part.Length == 0 || part[0] == '.' || part.Length > 64) return false;
                foreach (char c in part)
                {
                    if (c <= ' ' || c > '~') return false;
                }
            }
            return true;
        }

        private static void ReadSizeCrc(Dictionary<string, object> o, string path, string what, out long size, out uint crc)
        {
            if (o == null) throw new HdZipException(ManifestEntry + ": " + path + " has no '" + what + "' object");
            long? s = JsonValue.AsLong(JsonValue.Get(o, "size"));
            if (s == null || s.Value <= 0 || s.Value > 0x7FFFFFFFL) throw new HdZipException(ManifestEntry + ": " + path + ": bad " + what + ".size");
            size = s.Value;
            if (!TryCrc(JsonValue.Get(o, "crc32"), out crc)) throw new HdZipException(ManifestEntry + ": " + path + ": bad " + what + ".crc32 (8 hex digits)");
        }

        /// <summary>A CRC-32 as 8 hex digits (the contract) or as a number.</summary>
        internal static bool TryCrc(object value, out uint crc)
        {
            crc = 0;
            var text = value as string;
            if (text != null)
            {
                text = text.Trim();
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);
                return text.Length > 0 && text.Length <= 8 && uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out crc);
            }
            long? n = JsonValue.AsLong(value);
            if (n == null || n.Value < 0 || n.Value > 0xFFFFFFFFL) return false;
            crc = (uint)n.Value;
            return true;
        }

        internal static string Hex(uint crc)
        {
            return crc.ToString("x8", CultureInfo.InvariantCulture);
        }
    }
}
