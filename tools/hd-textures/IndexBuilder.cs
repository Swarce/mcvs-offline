using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McvsOffline.Server.Core.Content;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// The maintainer command <c>index</c>: regenerates <c>hd-index.tsv</c> from the decoded data pack of report 11
    /// (<c>names.json</c> and the decoded tree, for logical paths and level references), the ETC <c>Data/</c> folder and the
    /// PC install. For every named texture of a category (<see cref="HdIndex.Categorize"/>) whose PC copy is a DXT1/DXT5 DDS
    /// a power of two larger than the Android ETC1 copy, it writes the hash, category, levels, layout and both sizes;
    /// paths and contents stay out of the table.
    /// <para>
    /// Levels: from each level's own files (<c>Levels/&lt;L&gt;/</c>, <c>Baked/Levels/&lt;L&gt;/</c>) the references are
    /// followed through templates, models and layers (printable strings that name a pack file), never into another level,
    /// characters, weapons or menus. A level's alias is the <c>MapNN</c> folder its kits and maps use most.
    /// </para>
    /// </summary>
    internal static class IndexBuilder
    {
        private static readonly Regex Printable = new Regex("[\\x20-\\x7e]{5,}", RegexOptions.Compiled);

        private static readonly Regex PathPattern = new Regex(
            "((?:3d|Templates|Shaders|Baked|Levels|Design|Config)/[^\\x00\"',;=<>|]+?\\.(?:tga|png|dds|exr|bdae|tx|json|layer|StaticGeo))",
            RegexOptions.Compiled);

        private static readonly Regex MapFolder = new Regex("/(Map\\d\\d)/", RegexOptions.Compiled);

        private sealed class PackFile
        {
            public string Rel;
            public string Hash;
            public string Path;
            public string Out;
            public string Kind;
        }

        public static HdIndex Build(string decodedDir, string androidData, string pcData, TextWriter log)
        {
            var files = LoadNames(decodedDir);
            var byPath = new Dictionary<string, PackFile>(StringComparer.Ordinal);
            foreach (PackFile f in files)
            {
                if (f.Path != null) byPath[f.Path] = f;
            }
            log.WriteLine("names    " + files.Count + " files, " + byPath.Count + " named");

            // Levels and the surfaces each one references.
            var levelNames = byPath.Keys.Where(p => p.StartsWith("Levels/", StringComparison.Ordinal) && p.Split('/').Length >= 3)
                                   .Select(p => p.Split('/')[1]).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            var refsCache = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var levelsOf = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var index = new HdIndex();
            foreach (string level in levelNames)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var queue = new Stack<string>();
                foreach (string p in byPath.Keys.Where(p => Own(p, level)))
                {
                    seen.Add(p);
                    if (IsTexture(byPath[p])) Mark(levelsOf, p, level);
                    else queue.Push(p);
                }
                var aliasVotes = new Dictionary<string, int>(StringComparer.Ordinal);
                while (queue.Count > 0)
                {
                    string p = queue.Pop();
                    foreach (string r in References(decodedDir, byPath[p], byPath, refsCache))
                    {
                        if (!seen.Add(r)) continue;
                        if ((r.StartsWith("Levels/", StringComparison.Ordinal) || r.StartsWith("Baked/Levels/", StringComparison.Ordinal)) && !Own(r, level)) continue;
                        if (r.StartsWith("3d/Characters", StringComparison.Ordinal) || r.StartsWith("3d/Weapons", StringComparison.Ordinal) ||
                            r.StartsWith("Menus/", StringComparison.Ordinal)) continue;
                        if (r.StartsWith("3d/Environment/05_Kits/", StringComparison.Ordinal) || r.StartsWith("3d/Environment/03_Maps/", StringComparison.Ordinal))
                        {
                            Match m = MapFolder.Match(r);
                            if (m.Success) aliasVotes[m.Groups[1].Value] = (aliasVotes.TryGetValue(m.Groups[1].Value, out int v) ? v : 0) + 1;
                        }
                        if (IsTexture(byPath[r])) Mark(levelsOf, r, level);
                        else queue.Push(r);
                    }
                }
                string alias = aliasVotes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key).FirstOrDefault();
                index.Levels.Add(new HdLevel { Name = level, Alias = alias });
                log.WriteLine("level    " + level.PadRight(10) + " " + (alias ?? "-").PadRight(6) + " " + seen.Count + " files reached, " +
                              levelsOf.Count(kv => kv.Value.Contains(level) && kv.Key.StartsWith("3d/Environment/", StringComparison.Ordinal)) + " surfaces");
            }

            // Every named texture of a category whose PC copy is larger.
            var candidates = byPath.Values.Where(IsTexture).OrderBy(f => f.Hash, StringComparer.Ordinal).ToList();
            var lightmapProblems = new Dictionary<string, string>(StringComparer.Ordinal);
            var entries = new List<HdIndexEntry>();
            foreach (PackFile f in candidates)
            {
                string level;
                string category = HdIndex.Categorize(f.Path, out level);
                if (category == null) continue;
                HdIndexEntry e;
                string problem = Describe(f, androidData, pcData, out e);
                if (problem != null)
                {
                    if (category == HdIndex.Lightmaps) lightmapProblems[level] = f.Hash + ": " + problem;
                    continue;
                }
                e.Category = category;
                if (level != null) e.Levels = new[] { level };
                else if (category == HdIndex.Surfaces && levelsOf.TryGetValue(f.Path, out SortedSet<string> ls)) e.Levels = ls.ToArray();
                entries.Add(e);
            }
            foreach (KeyValuePair<string, string> p in lightmapProblems)
            {
                int dropped = entries.RemoveAll(x => x.Category == HdIndex.Lightmaps && x.Levels[0] == p.Key);
                log.WriteLine("lightmaps of " + p.Key + " left out (" + dropped + " files; all or none): " + p.Value);
            }
            index.Entries.AddRange(entries.OrderBy(x => Array.IndexOf(HdIndex.AllCategories, x.Category))
                                          .ThenBy(x => x.Levels.Length == 1 && x.Category == HdIndex.Lightmaps ? x.Levels[0] : "", StringComparer.Ordinal)
                                          .ThenBy(x => x.Hash, StringComparer.Ordinal));
            foreach (string c in HdIndex.AllCategories)
                log.WriteLine("category " + c.PadRight(11) + index.Entries.Count(x => x.Category == c).ToString(CultureInfo.InvariantCulture).PadLeft(5));
            return index;
        }

        /// <summary>The table row for a texture, or why it is not convertible.</summary>
        private static string Describe(PackFile f, string androidData, string pcData, out HdIndexEntry entry)
        {
            entry = null;
            string androidFile = Path.Combine(androidData, f.Rel.Replace('/', Path.DirectorySeparatorChar));
            uint crc = uint.Parse(f.Hash, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string pcFile = Path.Combine(pcData, crc.ToString(CultureInfo.InvariantCulture) + ".f");
            if (!File.Exists(androidFile)) return "no Android file";
            if (!File.Exists(pcFile)) return "no PC file";
            AndroidTexture android;
            byte[] androidBytes = File.ReadAllBytes(androidFile);
            try
            {
                android = AndroidTexture.Parse(androidBytes);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is DataPackException)
            {
                return "Android: " + ex.Message;
            }
            byte[] pcBytes = File.ReadAllBytes(pcFile);
            Dds dds;
            try
            {
                dds = Dds.Parse(Gcbf.IsGcbf(pcBytes) ? Gcbf.Decode(pcBytes, false) : pcBytes);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is DataPackException)
            {
                return "PC: " + ex.Message;
            }
            Ktx a = android.Rgb;
            int k = 0;
            while ((a.Width << k) < dds.Width) k++;
            if (k == 0) return "same size on PC";
            if ((a.Width << k) != dds.Width || (a.Height << k) != dds.Height) return "PC size is not a power-of-two multiple";
            entry = new HdIndexEntry
            {
                Hash = f.Hash, Layout = android.Layout,
                AndroidSize = androidBytes.Length, AndroidWidth = a.Width, AndroidHeight = a.Height, AndroidMips = a.MipLevels,
                PcSize = pcBytes.Length, PcWidth = dds.Width, PcHeight = dds.Height, PcMips = dds.Levels.Count,
            };
            return null;
        }

        private static bool Own(string path, string level)
        {
            return path.StartsWith("Levels/" + level + "/", StringComparison.Ordinal) || path.StartsWith("Baked/Levels/" + level + "/", StringComparison.Ordinal);
        }

        private static bool IsTexture(PackFile f)
        {
            return f.Kind == "tex-split-zip" || f.Kind == "ktx" || f.Kind == "tga";
        }

        private static void Mark(Dictionary<string, SortedSet<string>> levelsOf, string path, string level)
        {
            if (!levelsOf.TryGetValue(path, out SortedSet<string> set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                levelsOf[path] = set;
            }
            set.Add(level);
        }

        /// <summary>The pack files a decoded file names (printable strings that are logical paths of the pack).</summary>
        private static HashSet<string> References(string decodedDir, PackFile f, Dictionary<string, PackFile> byPath, Dictionary<string, HashSet<string>> cache)
        {
            if (cache.TryGetValue(f.Path, out HashSet<string> found)) return found;
            found = new HashSet<string>(StringComparer.Ordinal);
            cache[f.Path] = found;
            if (f.Out == null) return found;
            string file = Path.Combine(decodedDir, f.Out.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) return found;
            byte[] data = File.ReadAllBytes(file);
            var blobs = new List<byte[]> { data };
            if (data.Length > 4 && data[0] == 'P' && data[1] == 'K')
            {
                try
                {
                    using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
                    {
                        blobs.Clear();
                        foreach (ZipArchiveEntry e in zip.Entries)
                        {
                            using (Stream s = e.Open())
                            using (var ms = new MemoryStream())
                            {
                                s.CopyTo(ms);
                                blobs.Add(ms.ToArray());
                            }
                        }
                    }
                }
                catch (InvalidDataException)
                {
                }
            }
            foreach (byte[] blob in blobs)
            {
                string text = Encoding.Latin1.GetString(blob);
                foreach (Match run in Printable.Matches(text))
                {
                    foreach (Match m in PathPattern.Matches(run.Value))
                    {
                        string candidate = m.Groups[1].Value;
                        if (byPath.ContainsKey(candidate)) found.Add(candidate);
                    }
                }
            }
            return found;
        }

        private static List<PackFile> LoadNames(string decodedDir)
        {
            string file = Path.Combine(decodedDir, "names.json");
            if (!File.Exists(file)) throw new ArgumentException("no names.json in " + decodedDir + " (the decoded data pack of report 11)");
            var list = new List<PackFile>();
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(file)))
            {
                foreach (JsonProperty p in doc.RootElement.GetProperty("files").EnumerateObject())
                {
                    JsonElement v = p.Value;
                    list.Add(new PackFile
                    {
                        Rel = p.Name,
                        Hash = v.GetProperty("hash").GetString(),
                        Path = v.TryGetProperty("path", out JsonElement path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null,
                        Out = v.TryGetProperty("out", out JsonElement o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null,
                        Kind = v.TryGetProperty("kind", out JsonElement k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null,
                    });
                }
            }
            return list;
        }
    }
}
