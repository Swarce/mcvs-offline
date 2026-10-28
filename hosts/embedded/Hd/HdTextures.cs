// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Embedded.Hd
{
    /// <summary>What <see cref="HdTextures"/> needs from the engine's file system (the game: <see cref="EngineFiles"/>; tests: fakes).</summary>
    internal interface IEngineFiles
    {
        /// <summary>The engine is loaded and these calls can work.</summary>
        bool Available { get; }

        /// <summary>GlfStream.SavePath (native GetSavePath), or null.</summary>
        string SavePath { get; }

        /// <summary>GlfStream.CachePath (native GetCachePath), or null.</summary>
        string CachePath { get; }

        /// <summary>GameApplication.Instance.DataPath (RootPath/Data), or null.</summary>
        string DataPath { get; }

        /// <summary>
        /// GlfStream.PushZip_protected(relativePath, location) through SaveStream/CacheStream/AssetStream.PushZip: native
        /// AddZipSearchPath; true when it returned a handle. <paramref name="location"/>: "Save", "Cache" or "Asset".
        /// </summary>
        bool PushZip(string location, string relativePath);

        /// <summary>GlfStream_private.PushZipSearchPathFront(relativePath) (what PackManager uses for .pak packs). False when the call failed.</summary>
        bool PushZipFront(string relativePath);

        /// <summary>AssetStream.ReadAllBytes(logicalPath): what the engine's own file lookup returns for that name, or null.</summary>
        byte[] ReadAsset(string logicalPath);
    }

    /// <summary>One coloured copy of the probe texture in the zip (written by tools/hd-textures, listed in mcvs-hd.json).</summary>
    internal sealed class HdProbeVariant
    {
        public string Entry;
        public string Naming;
        public string Colour;
        public string EntryCrc32;
        public string PayloadCrc32;
    }

    /// <summary>
    /// The optional HD texture override zip (docs/research/pc-textures.md WP1 and WP3 route B): <c>&lt;McvsOffline&gt;/hd/hd-textures.zip</c>
    /// is mounted through the engine's own zip search path at boot, before the first level loads (the first call of patch 'graphics',
    /// <see cref="McvsOffline.GraphicsHooks"/>), so a texture inside it can replace the copy in Data/ without the Java installer ever
    /// seeing it. settings.json: <c>hd.enabled</c> (default true: used whenever the file exists) and <c>hd.mount</c>:
    /// <list type="bullet">
    /// <item><c>auto</c> (default): <c>SaveStream.PushZip</c> (LocationType.Save), then <c>CacheStream.PushZip</c>, each with the zip's
    /// path relative to that root (the native GetSavePath / GetCachePath, logged), "../" segments included when the game root is
    /// elsewhere;</item>
    /// <item><c>save</c>, <c>cache</c>: only that location; <c>asset</c>: <c>AssetStream.PushZip</c> relative to Data/;
    /// <c>front</c>: <c>GlfStream_private.PushZipSearchPathFront</c> relative to Data/ (the call PackManager uses for its
    /// packs, the only one whose name says "searched first").</item>
    /// </list>
    /// Whether a mounted zip is searched before Data/ and whether its entries need logical or hashed names is not known (U2);
    /// the probe zip of tools/hd-textures answers both: it carries one recoloured texture under each naming, lists them in
    /// <c>mcvs-hd.json</c>, and the host reads that texture back through the engine's lookup (<c>AssetStream.ReadAllBytes</c>) and
    /// logs which copy came back. A missing zip, a broken one, or a mount the engine refuses changes nothing: the game uses
    /// Data/ as before. Never throws.
    /// </summary>
    internal static class HdTextures
    {
        public const string FolderName = "hd";
        public const string ZipName = "hd-textures.zip";
        public const string ManifestEntry = "mcvs-hd.json";

        /// <summary>The <c>hd.mount</c> values.</summary>
        public static readonly string[] MountModes = { "auto", "save", "cache", "asset", "front" };

        /// <summary>
        /// The status until the first engine hook of patch 'graphics' mounts the zip: an APK built without that patch keeps it,
        /// so it names the patch instead of looking like a check still to come.
        /// </summary>
        public const string NotCheckedStatus =
            "hd textures: not checked yet (the zip is mounted when the game first reads its graphics options, through patch 'graphics')";

        private static volatile string status = NotCheckedStatus;

        /// <summary>One line for the status page and the status line.</summary>
        public static string Status { get { return status; } }

        /// <summary>&lt;McvsOffline&gt;/hd/hd-textures.zip.</summary>
        public static string ZipPath(string dataDir)
        {
            return System.IO.Path.Combine(System.IO.Path.Combine(dataDir, FolderName), ZipName);
        }

        /// <summary>
        /// Mounts <paramref name="zipPath"/> per settings and reports each step through <paramref name="mark"/> ("hd textures: ..."
        /// boot milestones). Returns the final status line.
        /// </summary>
        public static string Mount(string zipPath, bool enabled, string mountMode, IEngineFiles engine, Action<string> mark)
        {
            string result;
            try
            {
                result = MountCore(zipPath, enabled, mountMode, engine, mark ?? (s => { }));
            }
            catch (Exception ex)
            {
                result = "hd textures: not used (" + ex.GetType().Name + ": " + ex.Message + ")";
                Report(mark, result);
            }
            status = result;
            return result;
        }

        private static string MountCore(string zipPath, bool enabled, string mountMode, IEngineFiles engine, Action<string> mark)
        {
            bool exists = !string.IsNullOrEmpty(zipPath) && File.Exists(zipPath);
            if (!enabled)
            {
                string off = "hd textures: off (settings hd.enabled false)" + (exists ? "; " + ZipName + " is left unused" : "");
                Report(mark, off);
                return off;
            }
            if (!exists)
            {
                // The normal case: nothing installed. One quiet line, no warning.
                string none = "hd textures: none (no McvsOffline/" + FolderName + "/" + ZipName + ")";
                Report(mark, none);
                return none;
            }
            StoredZip zip;
            try
            {
                zip = StoredZip.Open(zipPath);
            }
            catch (Exception ex)
            {
                string broken = "hd textures: McvsOffline/" + FolderName + "/" + ZipName + " is not a usable zip (" + ex.Message + "); ignored, the game uses Data/";
                Report(mark, broken);
                return broken;
            }
            Dictionary<string, object> manifest = ReadManifest(zip);
            string kind = manifest == null ? null : JsonValue.AsString(manifest.ContainsKey("kind") ? manifest["kind"] : null);
            var described = new StringBuilder("hd textures: ").Append(ZipName).Append(' ')
                .Append(zip.Entries.Count.ToString(CultureInfo.InvariantCulture)).Append(" entries, ")
                .Append((zip.Length / 1024).ToString(CultureInfo.InvariantCulture)).Append(" KiB")
                .Append(kind == null ? "" : ", kind " + kind);
            if (zip.CompressedEntries > 0)
                described.Append(", ").Append(zip.CompressedEntries.ToString(CultureInfo.InvariantCulture)).Append(" of them compressed (written for stored entries)");
            if (engine == null || !engine.Available)
            {
                string unavailable = described + "; the engine's file system is not reachable (no Gameloft.Engine): not mounted";
                Report(mark, unavailable);
                return unavailable;
            }

            string mode = NormalizeMode(mountMode);
            string save = engine.SavePath;
            string cache = engine.CachePath;
            string data = engine.DataPath;
            described.Append("; engine roots: Save '").Append(save ?? "?").Append("', Cache '").Append(cache ?? "?")
                     .Append("', Data '").Append(data ?? "?").Append("'; hd.mount ").Append(mode);
            Report(mark, described.ToString());

            string mounted = null;
            var attempts = new List<string>();
            foreach (string[] attempt in Attempts(mode, zipPath, save, cache, data))
            {
                string location = attempt[0];
                string relative = attempt[1];
                bool ok = location == "Front" ? engine.PushZipFront(relative) : engine.PushZip(location, relative);
                attempts.Add(location + " '" + relative + "' " + (ok ? "ok" : "refused"));
                if (ok)
                {
                    mounted = location + " '" + relative + "'";
                    break;
                }
            }
            if (mounted == null)
            {
                string refused = "hd textures: the engine did not mount the zip (" + (attempts.Count == 0 ? "no usable root" : string.Join("; ", attempts.ToArray())) +
                                 "); the game uses Data/";
                Report(mark, refused);
                return refused;
            }
            Report(mark, "hd textures: mounted through " + mounted + (attempts.Count > 1 ? " (tried: " + string.Join("; ", attempts.ToArray()) + ")" : ""));
            string probe = kind == "probe" ? CheckProbe(zip, manifest, engine, mark) : null;
            return "hd textures: " + zip.Entries.Count.ToString(CultureInfo.InvariantCulture) + " entries mounted through " + mounted +
                   (probe == null ? "" : "; probe: " + probe);
        }

        /// <summary>The (location, relative path) pairs to try for <paramref name="mode"/>, in order.</summary>
        internal static List<string[]> Attempts(string mode, string zipPath, string save, string cache, string data)
        {
            var list = new List<string[]>();
            switch (NormalizeMode(mode))
            {
                case "save":
                    Add(list, "Save", save, zipPath);
                    break;
                case "cache":
                    Add(list, "Cache", cache, zipPath);
                    break;
                case "asset":
                    Add(list, "Asset", data, zipPath);
                    break;
                case "front":
                    Add(list, "Front", data, zipPath);
                    break;
                default:
                    Add(list, "Save", save, zipPath);
                    Add(list, "Cache", cache, zipPath);
                    break;
            }
            return list;
        }

        private static void Add(List<string[]> list, string location, string root, string target)
        {
            string relative = Relative(root, target);
            if (relative != null) list.Add(new[] { location, relative });
        }

        /// <summary>
        /// <paramref name="target"/> relative to <paramref name="root"/> with '/' separators and "../" where the target is not below
        /// the root; null when either is unknown or they share no root at all.
        /// </summary>
        internal static string Relative(string root, string target)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(target)) return null;
            string[] from = Split(root);
            string[] to = Split(target);
            if (from.Length == 0 || to.Length == 0) return null;
            int common = 0;
            while (common < from.Length && common < to.Length && string.Equals(from[common], to[common], StringComparison.Ordinal)) common++;
            if (common == 0 && (root.StartsWith("/", StringComparison.Ordinal) != target.StartsWith("/", StringComparison.Ordinal))) return null;
            var parts = new List<string>();
            for (int i = common; i < from.Length; i++) parts.Add("..");
            for (int i = common; i < to.Length; i++) parts.Add(to[i]);
            return parts.Count == 0 ? null : string.Join("/", parts.ToArray());
        }

        private static string[] Split(string path)
        {
            var parts = new List<string>();
            foreach (string p in path.Replace('\\', '/').Split('/'))
            {
                if (p.Length == 0 || p == ".") continue;
                if (p == ".." && parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                else parts.Add(p);
            }
            return parts.ToArray();
        }

        internal static string NormalizeMode(string mode)
        {
            string m = (mode ?? "").Trim().ToLowerInvariant();
            foreach (string known in MountModes)
            {
                if (m == known) return known;
            }
            return "auto";
        }

        private static Dictionary<string, object> ReadManifest(StoredZip zip)
        {
            try
            {
                byte[] bytes = zip.ReadStored(zip.Find(ManifestEntry));
                if (bytes == null) return null;
                object parsed;
                return MiniJson.TryParse(Encoding.UTF8.GetString(bytes), out parsed) ? parsed as Dictionary<string, object> : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads the probe texture back through the engine's own lookup and names the copy that came back: one of the zip's
        /// variants (its naming and colour), or the game's Data/ copy.
        /// </summary>
        internal static string CheckProbe(StoredZip zip, Dictionary<string, object> manifest, IEngineFiles engine, Action<string> mark)
        {
            var probe = manifest.ContainsKey("probe") ? manifest["probe"] as Dictionary<string, object> : null;
            string logical = probe == null || !probe.ContainsKey("logicalPath") ? null : JsonValue.AsString(probe["logicalPath"]);
            if (string.IsNullOrEmpty(logical))
            {
                Report(mark, "hd textures: probe manifest without a logicalPath; nothing to check");
                return "no logicalPath";
            }
            var variants = new List<HdProbeVariant>();
            var list = probe.ContainsKey("variants") ? probe["variants"] as List<object> : null;
            if (list != null)
            {
                foreach (object o in list)
                {
                    var v = o as Dictionary<string, object>;
                    if (v == null) continue;
                    variants.Add(new HdProbeVariant
                    {
                        Entry = Text(v, "entry"), Naming = Text(v, "naming"), Colour = Text(v, "colour"),
                        EntryCrc32 = Text(v, "entryCrc32"), PayloadCrc32 = Text(v, "payloadCrc32"),
                    });
                }
            }
            var present = new List<string>();
            foreach (HdProbeVariant v in variants)
            {
                StoredZipEntry e = zip.Find(v.Entry);
                present.Add("'" + v.Entry + "' (" + v.Naming + ", " + v.Colour + (e == null ? ", MISSING from the zip" : "") + ")");
            }
            byte[] read = null;
            string error = null;
            try
            {
                read = engine.ReadAsset(logical);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
            }
            string verdict;
            if (read == null || read.Length == 0)
            {
                verdict = "the engine read nothing for " + logical + (error == null ? "" : " (" + error + ")");
            }
            else
            {
                string crc = Crc(read);
                verdict = null;
                foreach (HdProbeVariant v in variants)
                {
                    if (string.Equals(crc, v.PayloadCrc32, StringComparison.OrdinalIgnoreCase))
                        verdict = "the engine returned the zip entry '" + v.Entry + "' (" + v.Naming + " naming, " + v.Colour + "), GCBF-decoded";
                    else if (string.Equals(crc, v.EntryCrc32, StringComparison.OrdinalIgnoreCase))
                        verdict = "the engine returned the zip entry '" + v.Entry + "' (" + v.Naming + " naming, " + v.Colour + "), bytes as stored";
                    if (verdict != null) break;
                }
                if (verdict == null)
                {
                    verdict = "the engine returned another copy (" + read.Length.ToString(CultureInfo.InvariantCulture) + " bytes, CRC-32 " + crc +
                              ", starts " + Hex(read, 12) + "): Data/, so the zip is not searched before Data/ for this name";
                }
            }
            Report(mark, "hd textures: probe " + logical + ": " + verdict + "; the zip holds " + string.Join(", ", present.ToArray()) +
                         ". The texture on screen tells what the renderer used (docs/STATUS.md, HD texture probe).");
            return verdict;
        }

        private static string Text(Dictionary<string, object> o, string key)
        {
            return o.ContainsKey(key) ? JsonValue.AsString(o[key]) : null;
        }

        internal static string Crc(byte[] data)
        {
            return McvsOffline.Server.Core.Content.Crc32.Compute(data).ToString("x8", CultureInfo.InvariantCulture);
        }

        private static string Hex(byte[] data, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < data.Length && i < count; i++) sb.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static void Report(Action<string> mark, string text)
        {
            try
            {
                if (mark != null) mark(text);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Tests: forget the last status.</summary>
        internal static void ResetForTests()
        {
            status = NotCheckedStatus;
        }
    }
}
