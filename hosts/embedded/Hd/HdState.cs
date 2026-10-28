// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Embedded.Hd
{
    /// <summary>Where a piece of an original file came from in Gameloft's archive (kept so removal restores the index exactly).</summary>
    internal sealed class HdZipRef
    {
        public long Offset;
        public int ZipSize;
    }

    /// <summary>
    /// One Data/ file the HD installer has taken over (installed.json): the original (size, CRC-32, the CRC of each 2 MiB piece,
    /// its archive references in Gameloft's index) and the HD file written in its place. A record exists from just before the
    /// file is replaced until the original is back in Data/ and in the installer's index; what Data/ holds is always read from
    /// the file itself (sizes, CRCs), never assumed from the record.
    /// </summary>
    internal sealed class HdRecord
    {
        public string Path;
        public string Group;
        public long OriginalSize;
        public uint OriginalCrc;

        /// <summary>CRC-32 of each piece of the original as the installer lists it (one for a whole file); null until known.</summary>
        public uint[] OriginalPieceCrcs;

        /// <summary>Offset and ZIP size of each piece in Gameloft's index; null when it was a rebuilt index (no CDN references).</summary>
        public List<HdZipRef> OriginalZip;

        public long HdSize;
        public uint HdCrc;

        /// <summary>CRC-32 of each piece of the HD file; null until known.</summary>
        public uint[] HdPieceCrcs;

        public HdRecord Clone()
        {
            var r = (HdRecord)MemberwiseClone();
            if (OriginalPieceCrcs != null) r.OriginalPieceCrcs = (uint[])OriginalPieceCrcs.Clone();
            if (HdPieceCrcs != null) r.HdPieceCrcs = (uint[])HdPieceCrcs.Clone();
            if (OriginalZip != null) r.OriginalZip = new List<HdZipRef>(OriginalZip);
            return r;
        }
    }

    /// <summary>
    /// <c>McvsOffline/hd/installed.json</c>: which Data/ files the HD installer has taken over, and from which pack. Written
    /// atomically (<see cref="AtomicFile"/>) before a file in Data/ is replaced and after the installer's index describes it.
    /// </summary>
    internal sealed class HdState
    {
        public const string Format = "mcvs-hd-installed";
        public const int FormatVersion = 1;

        private readonly List<HdRecord> records = new List<HdRecord>();
        private readonly Dictionary<string, HdRecord> byPath = new Dictionary<string, HdRecord>(StringComparer.Ordinal);

        public string PackFile;
        public string PackId;
        public string PackName;

        public IList<HdRecord> Records { get { return records.AsReadOnly(); } }

        public HdRecord Find(string path)
        {
            HdRecord r;
            return path != null && byPath.TryGetValue(path, out r) ? r : null;
        }

        public void Put(HdRecord record)
        {
            HdRecord old;
            if (byPath.TryGetValue(record.Path, out old)) records[records.IndexOf(old)] = record;
            else records.Add(record);
            byPath[record.Path] = record;
        }

        public void Remove(string path)
        {
            HdRecord old;
            if (!byPath.TryGetValue(path, out old)) return;
            records.Remove(old);
            byPath.Remove(path);
        }

        /// <summary>The state in <paramref name="path"/>: empty when the file does not exist; throws <see cref="FormatException"/> when it cannot be used.</summary>
        public static HdState Load(string path)
        {
            var state = new HdState();
            string text;
            try
            {
                text = AtomicFile.ReadAllTextOrNull(path);
            }
            catch (Exception ex)
            {
                throw new FormatException("cannot read " + path + " (" + ex.Message + ")");
            }
            if (text == null) return state;
            object parsed;
            if (!MiniJson.TryParse(text, out parsed) || !(parsed is Dictionary<string, object>)) throw new FormatException(path + " is not a JSON object");
            var o = (Dictionary<string, object>)parsed;
            if (JsonValue.GetString(o, "format") != Format || JsonValue.GetLong(o, "version", -1) != FormatVersion)
                throw new FormatException(path + " has another format or version");
            state.PackFile = JsonValue.GetString(o, "pack");
            state.PackId = JsonValue.GetString(o, "packId");
            state.PackName = JsonValue.GetString(o, "name");
            List<object> files = JsonValue.GetArray(o, "files") ?? new List<object>();
            foreach (object item in files)
            {
                var f = item as Dictionary<string, object>;
                if (f == null) throw new FormatException(path + ": a files entry is not an object");
                var r = new HdRecord
                {
                    Path = JsonValue.GetString(f, "path"),
                    Group = JsonValue.GetString(f, "group"),
                    OriginalSize = JsonValue.GetLong(f, "originalSize", -1),
                    HdSize = JsonValue.GetLong(f, "hdSize", -1),
                    OriginalPieceCrcs = Crcs(JsonValue.GetArray(f, "originalPieces")),
                    HdPieceCrcs = Crcs(JsonValue.GetArray(f, "hdPieces")),
                };
                if (!HdPack.IsDataPath(r.Path) || r.OriginalSize <= 0 || r.HdSize <= 0 ||
                    !HdPack.TryCrc(JsonValue.Get(f, "originalCrc"), out r.OriginalCrc) || !HdPack.TryCrc(JsonValue.Get(f, "hdCrc"), out r.HdCrc))
                    throw new FormatException(path + ": a files entry is incomplete");
                List<object> zip = JsonValue.GetArray(f, "originalZip");
                if (zip != null)
                {
                    r.OriginalZip = new List<HdZipRef>();
                    foreach (object z in zip)
                    {
                        var zo = z as Dictionary<string, object>;
                        if (zo == null) throw new FormatException(path + ": bad originalZip");
                        r.OriginalZip.Add(new HdZipRef { Offset = JsonValue.GetLong(zo, "offset"), ZipSize = (int)JsonValue.GetLong(zo, "zipSize") });
                    }
                }
                if (state.Find(r.Path) != null) throw new FormatException(path + ": " + r.Path + " is listed twice");
                state.Put(r);
            }
            return state;
        }

        /// <summary>Writes the state atomically; with no records the file is deleted.</summary>
        public void Save(string path)
        {
            if (records.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            var files = new List<object>();
            foreach (HdRecord r in records)
            {
                var f = new Dictionary<string, object>(StringComparer.Ordinal);
                f["path"] = r.Path;
                if (r.Group != null && r.Group != r.Path) f["group"] = r.Group;
                f["originalSize"] = r.OriginalSize;
                f["originalCrc"] = HdPack.Hex(r.OriginalCrc);
                if (r.OriginalPieceCrcs != null) f["originalPieces"] = CrcList(r.OriginalPieceCrcs);
                if (r.OriginalZip != null)
                {
                    var zip = new List<object>();
                    foreach (HdZipRef z in r.OriginalZip)
                    {
                        var zo = new Dictionary<string, object>(StringComparer.Ordinal);
                        zo["offset"] = z.Offset;
                        zo["zipSize"] = (long)z.ZipSize;
                        zip.Add(zo);
                    }
                    f["originalZip"] = zip;
                }
                f["hdSize"] = r.HdSize;
                f["hdCrc"] = HdPack.Hex(r.HdCrc);
                if (r.HdPieceCrcs != null) f["hdPieces"] = CrcList(r.HdPieceCrcs);
                files.Add(f);
            }
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["format"] = Format;
            o["version"] = (long)FormatVersion;
            o["pack"] = PackFile;
            o["packId"] = PackId;
            o["name"] = PackName;
            o["updated"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            o["files"] = files;
            AtomicFile.WriteAllText(path, MiniJson.Serialize(o, true) + "\n");
        }

        private static uint[] Crcs(List<object> list)
        {
            if (list == null) return null;
            var crcs = new uint[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                if (!HdPack.TryCrc(list[i], out crcs[i])) return null;
            }
            return crcs;
        }

        private static List<object> CrcList(uint[] crcs)
        {
            var list = new List<object>(crcs.Length);
            foreach (uint c in crcs) list.Add(HdPack.Hex(c));
            return list;
        }
    }
}
