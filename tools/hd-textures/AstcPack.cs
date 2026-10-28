using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Embedded.Install;
using McvsOffline.Server.Core.Content;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// Gameloft's ASTC data pack as a <c>files/</c> folder (tools/cdn-mirror <c>--extract</c>, or a backup of an ASTC phone):
    /// <c>Data/</c> and the installer index <c>McvsOffline/install/pack.info</c> (or <c>pack.info</c> next to <c>Data/</c>).
    /// </summary>
    internal sealed class AstcSource
    {
        public string Root { get; private set; }
        public string IndexPath { get; private set; }
        public PackIndex Index { get; private set; }

        /// <summary>The files the index lists (relative path, size), in index order, a split file once.</summary>
        public List<KeyValuePair<string, long>> Files { get; } = new List<KeyValuePair<string, long>>();

        public static AstcSource Open(string given)
        {
            if (string.IsNullOrWhiteSpace(given)) throw new ArgumentException("give --astc-files <the files folder of Gameloft's ASTC pack> (tools/cdn-mirror --extract)");
            string folder = Path.GetFullPath(given.Trim().Trim('"'));
            if (Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) == "Data" && !Directory.Exists(Path.Combine(folder, "Data")))
                folder = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar));
            if (!Directory.Exists(Path.Combine(folder, "Data"))) throw new ArgumentException(folder + " holds no Data folder (give the files folder of the ASTC pack)");
            string index = null;
            foreach (string candidate in new[]
                     {
                         Path.Combine(folder, "McvsOffline", "install", "pack.info"), Path.Combine(folder, "pack.info"),
                         Path.Combine(Path.GetDirectoryName(folder) ?? folder, "pack.info"),
                     })
            {
                if (File.Exists(candidate))
                {
                    index = candidate;
                    break;
                }
            }
            if (index == null) throw new ArgumentException("no pack.info for " + folder + " (McvsOffline/install/pack.info or pack.info next to Data/)");
            PackIndex parsed;
            try
            {
                parsed = PackIndex.Parse(File.ReadAllBytes(index));
            }
            catch (Exception ex) when (ex is FormatException || ex is EndOfStreamException)
            {
                throw new InvalidDataException(index + " is not an installer index: " + ex.Message);
            }
            if (parsed.IsRebuilt) throw new InvalidDataException(index + " is an index rebuilt from files (no CRCs), not Gameloft's: use the mirror's pack.info");
            var source = new AstcSource { Root = folder, IndexPath = index, Index = parsed };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (PackEntry e in parsed.Entries)
            {
                if (e.Piece > 1 || !seen.Add(e.RelativePath)) continue;
                source.Files.Add(new KeyValuePair<string, long>(e.RelativePath, e.Piece == 0 ? e.Size : e.TotalSize));
            }
            return source;
        }

        public string PathOf(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>The bytes of a file, checked against every CRC32 and size the index has for it.</summary>
        public byte[] ReadChecked(string relativePath)
        {
            byte[] data = File.ReadAllBytes(PathOf(relativePath));
            CheckAgainst(Index.EntriesOf(relativePath), data, relativePath);
            return data;
        }

        /// <summary>Throws unless <paramref name="data"/> is what <paramref name="entries"/> (one file's index entries) describe.</summary>
        public static void CheckAgainst(List<PackEntry> entries, byte[] data, string what)
        {
            if (entries.Count == 0) throw new InvalidDataException(what + " is not in the index");
            long offset = 0;
            foreach (PackEntry e in entries)
            {
                if (offset + e.Size > data.Length) throw new InvalidDataException(what + " is shorter than the index says");
                uint crc = Crc32.Compute(data, (int)offset, e.Size);
                if (e.Crc != 0 && crc != (uint)e.Crc)
                    throw new InvalidDataException(what + (e.Piece > 0 ? " piece " + e.Piece : "") + " has CRC32 " + crc.ToString("x8") + ", the index says " + ((uint)e.Crc).ToString("x8"));
                offset += e.Size;
            }
            if (offset != data.Length) throw new InvalidDataException(what + " has " + data.Length + " bytes, the index says " + offset);
        }
    }

    /// <summary>
    /// <c>McvsOffline/install/replaced.tsv</c>: the files of a data pack that are not Gameloft's, with Gameloft's size and CRC32
    /// of each. The host reads it to recognise the pack (InstallerData: the original sizes say which 1.17.32 pack it is).
    /// </summary>
    internal static class ReplacedList
    {
        public const string RelativePath = "McvsOffline/install/replaced.tsv";

        public sealed class Row
        {
            public string Path;
            public long Size;
            public uint Crc;
            public long OriginalSize;
            public uint OriginalCrc;
        }

        public static string Write(string variant, string gameVersion, IEnumerable<Row> rows)
        {
            var sb = new StringBuilder();
            sb.Append("# MCVS Offline: the files of this data pack that are not Gameloft's (PC textures re-encoded by tools/hd-textures astc-pack).\n");
            sb.Append("# <path> <size> <crc32> <Gameloft's size> <Gameloft's crc32>; the offline build recognises the pack by it (docs/INSTALL-OFFLINE.md).\n");
            sb.Append("mcvs-replaced\t1\t").Append(variant).Append('\t').Append(gameVersion).Append('\n');
            foreach (Row r in rows)
            {
                sb.Append(r.Path).Append('\t').Append(r.Size.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(r.Crc.ToString("x8", CultureInfo.InvariantCulture))
                  .Append('\t').Append(r.OriginalSize.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(r.OriginalCrc.ToString("x8", CultureInfo.InvariantCulture)).Append('\n');
            }
            return sb.ToString();
        }
    }
}
