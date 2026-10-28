using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>What <c>build</c> was asked to do.</summary>
    internal sealed class BuildOptions
    {
        /// <summary>The PC build's Data folder (decimal .f names).</summary>
        public string PcData { get; set; }

        /// <summary>The phone's ETC Data folder (hashed .f names).</summary>
        public string AndroidData { get; set; }

        public string[] Categories { get; set; } = HdIndex.DefaultCategories;

        /// <summary>Level names; null = every level.</summary>
        public string[] Maps { get; set; }

        /// <summary>The pack file, or a folder for the default name.</summary>
        public string Output { get; set; }

        public double MinPsnr { get; set; } = 24.0;
        public int Threads { get; set; } = Environment.ProcessorCount;

        /// <summary>Only these hashes (tests, a quick look); null = all of the scope.</summary>
        public HashSet<string> OnlyHashes { get; set; }

        public bool DryRun { get; set; }

        /// <summary>The texture table; null = the one built into the tool.</summary>
        public HdIndex Index { get; set; }

        public DateTime NowUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>The outcome of a build.</summary>
    internal sealed class BuildSummary
    {
        public string PackPath { get; set; }
        public int Files { get; set; }
        public long PackBytes { get; set; }
        public long OriginalBytes { get; set; }
        public long ReplacementBytes { get; set; }
        public long Texels { get; set; }
        public TimeSpan Elapsed { get; set; }
        public List<string> Skipped { get; } = new List<string>();
        public List<string> LowPsnr { get; } = new List<string>();
        public Dictionary<string, object> Manifest { get; set; }
    }

    /// <summary>
    /// The <c>build</c> command: converts the chosen textures (docs/design/hd-textures.md §5) and writes the pack
    /// <c>mcvs-hd-&lt;scope&gt;.zip</c> with its manifest (§2, §3). Work runs in windows of a few items at a time, each
    /// conversion also parallel inside, and the results are written in table order, so the same inputs give the same pack.
    /// A level's lightmaps are one work item: all are written, or none.
    /// </summary>
    internal static class PackBuilder
    {
        public const string ManifestEntry = "mcvs-hd.json";
        public const string ToolName = "McvsOffline.HdTextures 1";

        private sealed class Item
        {
            public List<HdIndexEntry> Entries = new List<HdIndexEntry>();
            public string Group;
            public List<Converted> Results;
            public string Failure;
        }

        private sealed class Converted
        {
            public HdIndexEntry Entry;
            public byte[] Original;
            public ConversionResult Result;
        }

        public static string DefaultFileName(string[] categories, string[] maps)
        {
            string name = "mcvs-hd-" + HdIndex.ScopeName(categories);
            if (maps != null) name += "-" + string.Join("+", maps);
            return name + ".zip";
        }

        public static BuildSummary Build(BuildOptions options, TextWriter log)
        {
            var clock = Stopwatch.StartNew();
            HdIndex index = options.Index ?? HdIndex.Load();
            List<HdIndexEntry> selected = index.Select(options.Categories, options.Maps);
            if (options.OnlyHashes != null) selected = selected.Where(e => options.OnlyHashes.Contains(e.Hash)).ToList();
            var summary = new BuildSummary();
            string scope = HdIndex.ScopeName(options.Categories);
            log.WriteLine("scope    " + scope + " (" + string.Join(", ", options.Categories) + "), maps: " +
                          (options.Maps == null ? "all" : string.Join(", ", options.Maps)));
            foreach (string category in options.Categories)
            {
                List<HdIndexEntry> c = selected.Where(e => e.Category == category).ToList();
                log.WriteLine("         " + category.PadRight(11) + c.Count.ToString(CultureInfo.InvariantCulture).PadLeft(5) + " files, " +
                              Mpx(c.Sum(e => e.OutputTexels)) + " to encode");
            }
            long totalTexels = selected.Sum(e => e.OutputTexels);
            log.WriteLine("         total      " + selected.Count.ToString(CultureInfo.InvariantCulture).PadLeft(5) + " files, " + Mpx(totalTexels));
            if (options.DryRun)
            {
                summary.Files = selected.Count;
                summary.Texels = totalTexels;
                return summary;
            }
            if (selected.Count == 0) throw new ArgumentException("nothing to convert for this scope and these maps");
            CheckPack(options, selected[0]);

            // Work items: one per texture, one per level for lightmaps.
            var items = new List<Item>();
            var groups = new Dictionary<string, Item>();
            foreach (HdIndexEntry e in selected)
            {
                string g = e.Group;
                if (g == null)
                {
                    items.Add(new Item { Entries = { e } });
                    continue;
                }
                Item item;
                if (!groups.TryGetValue(g, out item))
                {
                    item = new Item { Group = g };
                    groups[g] = item;
                    items.Add(item);
                }
                item.Entries.Add(e);
            }
            Dictionary<string, string> names = LightmapNames(index);

            string packPath = ResolveOutput(options.Output, options.Categories, options.Maps);
            string partial = packPath + ".partial";
            var manifestFiles = new List<object>();
            int done = 0;
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var zip = new StoredZipWriter(stream, true);
                int window = Math.Max(2, options.Threads);
                for (int start = 0; start < items.Count; start += window)
                {
                    List<Item> batch = items.Skip(start).Take(window).ToList();
                    Parallel.ForEach(batch, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Threads) },
                                     item => ConvertItem(item, options));
                    foreach (Item item in batch)
                    {
                        if (item.Failure != null)
                        {
                            done += item.Entries.Count;
                            string what = item.Group != null ? item.Group + " (" + item.Entries.Count + " files)" : item.Entries[0].AndroidPath;
                            summary.Skipped.Add(what + ": " + item.Failure);
                            log.WriteLine("skipped  " + what + ": " + item.Failure);
                            continue;
                        }
                        foreach (Converted c in item.Results)
                        {
                            done++;
                            HdIndexEntry e = c.Entry;
                            ConversionResult r = c.Result;
                            uint newCrc = Crc32.Compute(r.File);
                            string entryName = "Data/" + e.AndroidPath;
                            zip.Add(entryName, r.File, newCrc);
                            summary.Files++;
                            summary.OriginalBytes += c.Original.Length;
                            summary.ReplacementBytes += r.File.Length;
                            summary.Texels += r.Texels;
                            bool low = r.Psnr < options.MinPsnr || (!double.IsNaN(r.AlphaPsnr) && r.AlphaPsnr < options.MinPsnr);
                            string psnr = Db(r.Psnr) + (double.IsNaN(r.AlphaPsnr) ? "" : ", alpha " + Db(r.AlphaPsnr)) + " (phone " + Db(r.OriginalPsnr) + ")";
                            if (low) summary.LowPsnr.Add(e.AndroidPath + " (" + e.Category + "): " + psnr);
                            string name;
                            names.TryGetValue(e.Hash, out name);
                            log.WriteLine("[" + done.ToString(CultureInfo.InvariantCulture).PadLeft(Width(selected.Count)) + "/" + selected.Count + "] " +
                                          e.AndroidPath.PadRight(12) + " " + e.Category.PadRight(10) + " " + r.Layout.PadRight(5) + " " +
                                          (r.OriginalWidth + "x" + r.OriginalHeight).PadLeft(9) + " -> " + (r.Width + "x" + r.Height).PadRight(9) + " " +
                                          Kb(c.Original.Length).PadLeft(9) + " -> " + Kb(r.File.Length).PadLeft(9) + "  PSNR " + psnr +
                                          (low ? "  LOW" : "") + "  " + r.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                            manifestFiles.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["entry"] = entryName,
                                ["hash"] = e.Hash,
                                ["logicalPath"] = name,
                                ["category"] = e.Category,
                                ["levels"] = e.Levels.Cast<object>().ToList(),
                                ["group"] = e.Group,
                                ["layout"] = r.Layout,
                                ["original"] = new Dictionary<string, object>(StringComparer.Ordinal)
                                {
                                    ["size"] = (long)c.Original.Length,
                                    ["crc32"] = Hex(Crc32.Compute(c.Original)),
                                    ["width"] = (long)r.OriginalWidth,
                                    ["height"] = (long)r.OriginalHeight,
                                    ["mipLevels"] = (long)r.OriginalMipLevels,
                                },
                                ["replacement"] = new Dictionary<string, object>(StringComparer.Ordinal)
                                {
                                    ["size"] = (long)r.File.Length,
                                    ["crc32"] = Hex(newCrc),
                                    ["width"] = (long)r.Width,
                                    ["height"] = (long)r.Height,
                                    ["mipLevels"] = (long)r.MipLevels,
                                },
                                ["psnr"] = Round(r.Psnr),
                                ["originalPsnr"] = Round(r.OriginalPsnr),
                                ["alphaPsnr"] = double.IsNaN(r.AlphaPsnr) ? null : (object)Round(r.AlphaPsnr),
                            });
                        }
                        item.Results = null;
                    }
                }
                if (summary.Files == 0) throw new InvalidDataException("no texture could be converted (" + summary.Skipped.Count + " skipped)");
                var manifest = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["format"] = 1L,
                    ["kind"] = "hd-textures",
                    ["tool"] = ToolName,
                    ["created"] = options.NowUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    ["gameVersion"] = HdIndex.GameVersion,
                    ["pack"] = HdIndex.Pack,
                    ["scope"] = scope,
                    ["categories"] = options.Categories.Cast<object>().ToList(),
                    ["maps"] = (options.Maps ?? index.Levels.Select(l => l.Name).ToArray()).Cast<object>().ToList(),
                    ["files"] = manifestFiles,
                    ["totals"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["files"] = (long)summary.Files,
                        ["originalBytes"] = summary.OriginalBytes,
                        ["replacementBytes"] = summary.ReplacementBytes,
                    },
                };
                summary.Manifest = manifest;
                zip.Add(ManifestEntry, Encoding.UTF8.GetBytes(MiniJson.Serialize(manifest, true) + "\n"));
                zip.Finish();
                summary.PackBytes = zip.Length;
            }
            if (File.Exists(packPath)) File.Delete(packPath);
            File.Move(partial, packPath);
            summary.PackPath = packPath;
            summary.Elapsed = clock.Elapsed;

            log.WriteLine();
            log.WriteLine("wrote    " + packPath + ": " + summary.Files + " files, " + Mb(summary.PackBytes) + " (the originals: " + Mb(summary.OriginalBytes) + ")");
            log.WriteLine("time     " + summary.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s, " + Mpx(summary.Texels) + " encoded, " +
                          (summary.Texels / 1e6 / Math.Max(0.001, summary.Elapsed.TotalSeconds)).ToString("0.0", CultureInfo.InvariantCulture) + " Mtexel/s on " +
                          options.Threads + " threads");
            if (summary.LowPsnr.Count > 0)
            {
                log.WriteLine("check    " + summary.LowPsnr.Count + " textures below " + Db(options.MinPsnr) + " (compare them in game):");
                foreach (string s in summary.LowPsnr) log.WriteLine("           " + s);
            }
            if (summary.Skipped.Count > 0) log.WriteLine("skipped  " + summary.Skipped.Count + " (listed above); the game keeps their original textures");
            log.WriteLine("The pack holds textures converted from your own game files: keep it for your own phone, do not share it.");
            return summary;
        }

        private static void ConvertItem(Item item, BuildOptions options)
        {
            var results = new List<Converted>();
            foreach (HdIndexEntry e in item.Entries)
            {
                string androidFile = Path.Combine(options.AndroidData, e.AndroidPath.Replace('/', Path.DirectorySeparatorChar));
                string pcFile = Path.Combine(options.PcData, e.PcPath);
                try
                {
                    if (!File.Exists(androidFile)) throw new FileNotFoundException("no " + e.AndroidPath + " in the Android Data folder");
                    if (!File.Exists(pcFile)) throw new FileNotFoundException("no " + e.PcPath + " in the PC Data folder");
                    byte[] original = File.ReadAllBytes(androidFile);
                    ConversionResult r = TextureConverter.Convert(original, File.ReadAllBytes(pcFile), options.Threads);
                    if (r.Layout != e.Layout) throw new InvalidDataException("the Android file is a " + r.Layout + " texture, the table says " + e.Layout);
                    results.Add(new Converted { Entry = e, Original = original, Result = r });
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is DataPackException || ex is UnauthorizedAccessException)
                {
                    item.Failure = (item.Group != null ? e.AndroidPath + ": " : "") + ex.Message;
                    return;
                }
            }
            item.Results = results;
        }

        /// <summary>Refuses an ASTC (or otherwise non-ETC) Data folder before any work.</summary>
        private static void CheckPack(BuildOptions options, HdIndexEntry probe)
        {
            string file = Path.Combine(options.AndroidData, probe.AndroidPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) return;
            byte[] data = File.ReadAllBytes(file);
            byte[] raw = Gcbf.IsGcbf(data) ? Gcbf.Decode(data, true) : data;
            if (Ktx.LooksLikeKtx(raw))
            {
                Ktx ktx = Ktx.Parse(raw);
                if (ktx.IsAstc)
                    throw new InvalidDataException(options.AndroidData + " is the ASTC pack (" + probe.AndroidPath + " is " + ktx.FormatName +
                                                   "); the converter writes ETC1 for the ETC pack only");
            }
        }

        private static Dictionary<string, string> LightmapNames(HdIndex index)
        {
            var hashes = new HashSet<string>(index.Entries.Where(e => e.Category == HdIndex.Lightmaps).Select(e => e.Hash));
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (HdLevel level in index.Levels)
            {
                foreach (string candidate in HdIndex.LightmapCandidates(level.Name))
                {
                    string hash = Hex(Crc32.Compute(Encoding.UTF8.GetBytes(candidate)));
                    if (hashes.Contains(hash)) names[hash] = candidate;
                }
            }
            return names;
        }

        private static string ResolveOutput(string output, string[] categories, string[] maps)
        {
            string name = DefaultFileName(categories, maps);
            if (string.IsNullOrEmpty(output)) return Path.GetFullPath(name);
            if (Directory.Exists(output) || output.EndsWith("/", StringComparison.Ordinal) || output.EndsWith("\\", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(output);
                return Path.GetFullPath(Path.Combine(output, name));
            }
            string dir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            return Path.GetFullPath(output);
        }

        internal static string Hex(uint v) => v.ToString("x8", CultureInfo.InvariantCulture);

        private static double Round(double v) => double.IsPositiveInfinity(v) ? 99.0 : Math.Round(v, 2);

        private static string Db(double v) => (double.IsPositiveInfinity(v) ? "inf" : v.ToString("0.0", CultureInfo.InvariantCulture)) + " dB";

        private static string Kb(long bytes) => (bytes / 1024.0).ToString("#,0", CultureInfo.InvariantCulture) + " KB";

        internal static string Mb(long bytes) => (bytes / 1048576.0).ToString("#,0.0", CultureInfo.InvariantCulture) + " MB";

        internal static string Mpx(long texels) => (texels / 1e6).ToString("#,0.0", CultureInfo.InvariantCulture) + " Mtexel";

        private static int Width(int n) => n.ToString(CultureInfo.InvariantCulture).Length;
    }
}
