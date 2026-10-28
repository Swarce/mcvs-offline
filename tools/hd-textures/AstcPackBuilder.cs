using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Embedded.Install;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>What <c>astc-pack</c> and <c>astc-measure</c> were asked to do.</summary>
    internal sealed class AstcOptions
    {
        /// <summary>Gameloft's ASTC pack as a files/ folder.</summary>
        public string AstcFiles { get; set; }

        /// <summary>The PC build's Data folder.</summary>
        public string PcData { get; set; }

        /// <summary>Where the upload parts go.</summary>
        public string Output { get; set; }

        /// <summary>The resumable work folder (finished textures and their journal); default &lt;Output&gt;/work.</summary>
        public string Work { get; set; }

        public string[] Categories { get; set; } = HdIndex.AllCategories;

        /// <summary>Block size and quality per category.</summary>
        public Dictionary<string, AstcSetting> Settings { get; set; } = AstcPackBuilder.DefaultSettings();

        public int Threads { get; set; } = Environment.ProcessorCount;

        /// <summary>Each upload part stays below this many bytes.</summary>
        public long PartLimit { get; set; } = AstcPackBuilder.DefaultPartLimit;

        public HashSet<string> OnlyHashes { get; set; }
        public bool KeepWork { get; set; }

        /// <summary>Encode only (no parts): for a run split over several sessions.</summary>
        public bool EncodeOnly { get; set; }

        public bool DryRun { get; set; }
        public Astcenc Encoder { get; set; }
        public HdIndex Index { get; set; }

        /// <summary>astc-measure: samples per category and the block sizes to compare.</summary>
        public int Samples { get; set; } = 6;
        public List<AstcSetting> MeasureSettings { get; set; }

        public DateTime NowUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>What a pack build wrote.</summary>
    internal sealed class AstcPackSummary
    {
        public List<string> Parts { get; } = new List<string>();
        public int Files { get; set; }
        public int Replaced { get; set; }
        public long DataBytes { get; set; }
        public long OriginalDataBytes { get; set; }
        public long TextureGpuBytes { get; set; }
        public long OriginalTextureGpuBytes { get; set; }
        public List<string> Skipped { get; } = new List<string>();
        public string Report { get; set; }
    }

    /// <summary>
    /// The full ASTC HD data pack (<c>astc-pack</c>): every file of Gameloft's 1.17.32 ASTC pack, where the PC texture is
    /// larger replaced by the PC texture re-encoded to ASTC at the PC size (<see cref="AstcConverter"/>), written straight
    /// into upload parts (stored zips below 380 MiB each, entries <c>files/Data/...</c>; the last part also holds the
    /// installer index <c>files/McvsOffline/install/pack.info</c> describing every file as it now is, the list of replaced
    /// files and a README.txt). Encoding is resumable: every finished texture goes to the work folder with a journal line,
    /// so a run cut short continues where it stopped. Nothing of the source pack is changed or copied beside the parts.
    /// </summary>
    internal static class AstcPackBuilder
    {
        public const long DefaultPartLimit = 380L * 1024 * 1024;
        public const string ToolName = "McvsOffline.HdTextures astc-pack 1";
        public const string PartPrefix = "mcvs-astc-hd-";
        public const string ManifestPath = "McvsOffline/install/astc-hd.json";
        public const string IndexPath = "McvsOffline/install/pack.info";
        public const string IndexNotePath = "McvsOffline/install/pack.info.txt";

        /// <summary>
        /// The measured defaults (docs/design/hd-textures.md §6, astc-measure on 6 textures per category): 5x5 for the large
        /// categories (map surfaces, characters, the 4096 lightmaps, VFX, 3D menus: about 40-52 dB against the PC texture where
        /// Gameloft's 6x6 at the phone's size scores 24-35 dB), 4x4 for the small close-up ones (equipment, props, minimaps:
        /// over 60 dB, a few MB more), all -thorough.
        /// </summary>
        public static Dictionary<string, AstcSetting> DefaultSettings()
        {
            return new Dictionary<string, AstcSetting>(StringComparer.Ordinal)
            {
                [HdIndex.Surfaces] = new AstcSetting(5, 5, "thorough"),
                [HdIndex.Lightmaps] = new AstcSetting(5, 5, "thorough"),
                [HdIndex.Characters] = new AstcSetting(5, 5, "thorough"),
                [HdIndex.Equipment] = new AstcSetting(4, 4, "thorough"),
                [HdIndex.Props] = new AstcSetting(4, 4, "thorough"),
                [HdIndex.Vfx] = new AstcSetting(5, 5, "thorough"),
                [HdIndex.Menus3d] = new AstcSetting(5, 5, "thorough"),
                [HdIndex.Minimaps] = new AstcSetting(4, 4, "thorough"),
            };
        }

        /// <summary>"surfaces=5x5,lightmaps=6x6:medium" or "all=4x4" over the defaults.</summary>
        public static Dictionary<string, AstcSetting> ParseSettings(string text, string quality, Dictionary<string, AstcSetting> start)
        {
            var settings = new Dictionary<string, AstcSetting>(start, StringComparer.Ordinal);
            if (quality != null)
            {
                foreach (string c in settings.Keys.ToList()) settings[c] = new AstcSetting(settings[c].BlockX, settings[c].BlockY, quality);
            }
            if (string.IsNullOrWhiteSpace(text)) return settings;
            foreach (string part in text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) throw new ArgumentException("--blocks wants category=WxH[:quality] items, not '" + part + "'");
                string category = part.Substring(0, eq).Trim().ToLowerInvariant();
                string[] targets = category == "all" ? HdIndex.AllCategories : new[] { HdIndex.AllCategories.FirstOrDefault(c => c == category) };
                if (targets[0] == null) throw new ArgumentException("unknown category '" + category + "' in --blocks");
                foreach (string c in targets) settings[c] = AstcSetting.Parse(part.Substring(eq + 1), settings[c].Quality);
            }
            return settings;
        }

        /// <summary>GPU bytes of an ASTC texture of the given size and levels.</summary>
        public static long GpuBytes(int width, int height, int levels, int blockX, int blockY)
        {
            long sum = 0;
            for (int i = 0; i < levels; i++) sum += Astcenc.DataSize(Ktx.MipSize(width, i), Ktx.MipSize(height, i), blockX, blockY);
            return sum;
        }

        /// <summary>Texels astcenc encodes for an entry (one RGBA texture, every output level).</summary>
        public static long Texels(HdIndexEntry e)
        {
            long sum = 0;
            int levels = TextureConverter.LevelCount(e.AndroidMips, e.Doublings);
            for (int i = 0; i < levels; i++) sum += (long)Ktx.MipSize(e.PcWidth, i) * Ktx.MipSize(e.PcHeight, i);
            return sum;
        }

        public static long OutputGpuBytes(HdIndexEntry e, AstcSetting s) =>
            GpuBytes(e.PcWidth, e.PcHeight, TextureConverter.LevelCount(e.AndroidMips, e.Doublings), s.BlockX, s.BlockY);

        public static long AndroidGpuBytes(HdIndexEntry e) => GpuBytes(e.AndroidWidth, e.AndroidHeight, e.AndroidMips, 6, 6);

        // ------------------------------------------------------------------ measure

        /// <summary>
        /// <c>astc-measure</c>: encodes a few textures per category at each block size and prints PSNR against the PC texture
        /// next to Gameloft's 6x6 (enlarged to the PC size), the time per texel, and the whole category's GPU bytes and
        /// estimated encode time at each size. Nothing is written.
        /// </summary>
        public static void Measure(AstcOptions options, TextWriter log)
        {
            HdIndex index = options.Index ?? HdIndex.Load();
            AstcSource source = AstcSource.Open(options.AstcFiles);
            Astcenc encoder = options.Encoder;
            log.WriteLine("astcenc  " + encoder.Path + " (" + encoder.Version() + ")");
            var jobs = new List<Tuple<HdIndexEntry, AstcSetting>>();
            var samples = new Dictionary<string, List<HdIndexEntry>>();
            foreach (string category in options.Categories)
            {
                List<HdIndexEntry> all = index.Entries.Where(e => e.Category == category).OrderBy(e => e.Hash, StringComparer.Ordinal).ToList();
                if (options.OnlyHashes != null) all = all.Where(e => options.OnlyHashes.Contains(e.Hash)).ToList();
                var picked = new List<HdIndexEntry>();
                int n = Math.Min(options.Samples, all.Count);
                for (int i = 0; i < n; i++) picked.Add(all[(int)((i + 0.5) * all.Count / n)]);
                samples[category] = picked;
                foreach (HdIndexEntry e in picked)
                {
                    foreach (AstcSetting s in options.MeasureSettings) jobs.Add(Tuple.Create(e, s));
                }
            }
            // Biggest first, so the long ones do not end up alone at the end.
            jobs = jobs.OrderByDescending(j => Texels(j.Item1) * Cost(j.Item2)).ToList();
            var results = new ConcurrentDictionary<Tuple<HdIndexEntry, AstcSetting>, AstcResult>();
            var clock = Stopwatch.StartNew();
            int done = 0;
            Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Threads) }, job =>
            {
                try
                {
                    byte[] original = source.ReadChecked("Data/" + job.Item1.AndroidPath);
                    byte[] pc = File.ReadAllBytes(Path.Combine(options.PcData, job.Item1.PcPath));
                    AstcResult r = AstcConverter.Convert(original, pc, job.Item2, encoder);
                    r.File = null;
                    results[job] = r;
                    lock (log)
                    {
                        done++;
                        log.WriteLine("[" + done + "/" + jobs.Count + "] " + job.Item1.AndroidPath.PadRight(12) + " " + job.Item1.Category.PadRight(10) + " " +
                                      (r.Width + "x" + r.Height).PadLeft(9) + " " + job.Item2.ToString().PadRight(15) + " PSNR " + Db(r.Psnr) +
                                      (double.IsNaN(r.AlphaPsnr) ? "" : " alpha " + Db(r.AlphaPsnr)) + "  Gameloft " + Db(r.OriginalUpscaledPsnr) + "  " +
                                      r.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is DataPackException)
                {
                    lock (log) log.WriteLine("skipped  " + job.Item1.AndroidPath + " " + job.Item2 + ": " + ex.Message);
                }
            });
            log.WriteLine();
            log.WriteLine("measured in " + clock.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s on " + options.Threads + " threads " +
                          "(PSNR RGB in dB against the PC texture at the PC size; Gameloft = its ASTC 6x6 at the phone's size, enlarged bilinearly)");
            log.WriteLine("category    block            n  median  mean   min    Gameloft  alpha   Mtexel/s/thread  category GPU MB  (Gameloft MB)  est. hours on " + options.Threads + " threads");
            foreach (string category in options.Categories)
            {
                List<HdIndexEntry> all = index.Entries.Where(e => e.Category == category).ToList();
                long texels = all.Sum(Texels);
                foreach (AstcSetting s in options.MeasureSettings)
                {
                    List<AstcResult> rs = samples[category].Select(e => Tuple.Create(e, s)).Where(results.ContainsKey).Select(k => results[k]).ToList();
                    if (rs.Count == 0) continue;
                    double rate = rs.Sum(r => r.Texels) / 1e6 / Math.Max(0.001, rs.Sum(r => r.Elapsed.TotalSeconds));
                    double hours = texels / 1e6 / rate / Math.Max(1, options.Threads) / 3600.0;
                    List<double> alphas = rs.Where(r => !double.IsNaN(r.AlphaPsnr)).Select(r => r.AlphaPsnr).ToList();
                    log.WriteLine(category.PadRight(11) + " " + s.ToString().PadRight(15) + " " + rs.Count.ToString(CultureInfo.InvariantCulture).PadLeft(2) + "  " +
                                  F(Median(rs.Select(r => r.Psnr))) + "  " + F(rs.Average(r => Cap(r.Psnr))) + "  " + F(rs.Min(r => r.Psnr)) + "   " +
                                  F(Median(rs.Select(r => r.OriginalUpscaledPsnr))) + "    " + (alphas.Count == 0 ? "  -  " : F(Median(alphas))) + "   " +
                                  rate.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(8) + "         " +
                                  Mb(all.Sum(e => OutputGpuBytes(e, s))).PadLeft(9) + "       (" + Mb(all.Sum(AndroidGpuBytes)).PadLeft(7) + ")     " +
                                  hours.ToString("0.00", CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>Rough relative encode cost of a setting (to order work: small blocks and higher presets take longer).</summary>
        private static double Cost(AstcSetting s)
        {
            double q = s.Quality == "exhaustive" ? 8 : s.Quality == "verythorough" ? 4 : s.Quality == "thorough" ? 2 : 1;
            return q * 36.0 / (s.BlockX * s.BlockY);
        }

        // ------------------------------------------------------------------ build

        private sealed class Item
        {
            public List<HdIndexEntry> Entries = new List<HdIndexEntry>();
            public string Group;
            public long Texels;
        }

        /// <summary>One finished texture as the journal records it.</summary>
        internal sealed class Done
        {
            public string Hash;
            public string Block;
            public string Quality;
            public long Size;
            public uint Crc;
            public int Width, Height, Mips, OriginalWidth, OriginalHeight, OriginalMips;
            public long GpuBytes, OriginalGpuBytes, OriginalSize;
            public uint OriginalCrc;
            public double Psnr, AlphaPsnr, OriginalPsnr, OriginalUpscaledPsnr, Seconds;
            public bool Opaque;

            public string Line()
            {
                return string.Join("\t", new[]
                {
                    Hash, Block, Quality, S(Size), Hex(Crc), S(Width), S(Height), S(Mips), S(OriginalWidth), S(OriginalHeight), S(OriginalMips),
                    S(GpuBytes), S(OriginalGpuBytes), S(OriginalSize), Hex(OriginalCrc), D(Psnr), D(AlphaPsnr), D(OriginalPsnr), D(OriginalUpscaledPsnr),
                    D(Seconds), Opaque ? "1" : "0",
                });
            }

            public static Done Parse(string line)
            {
                string[] f = line.Split('\t');
                if (f.Length != 21) return null;
                try
                {
                    return new Done
                    {
                        Hash = f[0], Block = f[1], Quality = f[2], Size = long.Parse(f[3], CultureInfo.InvariantCulture), Crc = uint.Parse(f[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        Width = I(f[5]), Height = I(f[6]), Mips = I(f[7]), OriginalWidth = I(f[8]), OriginalHeight = I(f[9]), OriginalMips = I(f[10]),
                        GpuBytes = long.Parse(f[11], CultureInfo.InvariantCulture), OriginalGpuBytes = long.Parse(f[12], CultureInfo.InvariantCulture),
                        OriginalSize = long.Parse(f[13], CultureInfo.InvariantCulture), OriginalCrc = uint.Parse(f[14], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        Psnr = P(f[15]), AlphaPsnr = P(f[16]), OriginalPsnr = P(f[17]), OriginalUpscaledPsnr = P(f[18]), Seconds = P(f[19]), Opaque = f[20] == "1",
                    };
                }
                catch (FormatException)
                {
                    return null;
                }
            }

            private static string S(long v) => v.ToString(CultureInfo.InvariantCulture);
            private static string D(double v) => double.IsNaN(v) ? "-" : double.IsPositiveInfinity(v) ? "99" : v.ToString("0.###", CultureInfo.InvariantCulture);
            private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);
            private static double P(string s) => s == "-" ? double.NaN : double.Parse(s, CultureInfo.InvariantCulture);
        }

        public static AstcPackSummary Build(AstcOptions options, TextWriter log)
        {
            var clock = Stopwatch.StartNew();
            HdIndex index = options.Index ?? HdIndex.Load();
            List<HdIndexEntry> selected = index.Select(options.Categories, null);
            if (options.OnlyHashes != null) selected = selected.Where(e => options.OnlyHashes.Contains(e.Hash)).ToList();

            log.WriteLine("scope    " + string.Join(", ", options.Categories));
            long totalTexels = 0, gpuBefore = 0, gpuAfter = 0;
            foreach (string category in options.Categories)
            {
                List<HdIndexEntry> c = selected.Where(e => e.Category == category).ToList();
                if (c.Count == 0) continue;
                AstcSetting s = options.Settings[category];
                long texels = c.Sum(Texels);
                totalTexels += texels;
                gpuBefore += c.Sum(AndroidGpuBytes);
                gpuAfter += c.Sum(e => OutputGpuBytes(e, s));
                log.WriteLine("         " + category.PadRight(11) + c.Count.ToString(CultureInfo.InvariantCulture).PadLeft(5) + " files  " + s.ToString().PadRight(15) +
                              Mpx(texels).PadLeft(16) + "   GPU " + Mb(c.Sum(AndroidGpuBytes)) + " -> " + Mb(c.Sum(e => OutputGpuBytes(e, s))));
            }
            log.WriteLine("         total      " + selected.Count.ToString(CultureInfo.InvariantCulture).PadLeft(5) + " files" + new string(' ', 17) +
                          Mpx(totalTexels).PadLeft(16) + "   GPU " + Mb(gpuBefore) + " -> " + Mb(gpuAfter));
            var summary = new AstcPackSummary();
            if (options.DryRun) return summary;
            if (selected.Count == 0) throw new ArgumentException("nothing to convert");

            AstcSource source = AstcSource.Open(options.AstcFiles);
            log.WriteLine("ASTC     " + source.Root + " (" + source.Files.Count + " files, index " + source.IndexPath + ")");
            log.WriteLine("PC       " + options.PcData);
            Astcenc encoder = options.Encoder;
            string version = encoder.Version();
            log.WriteLine("astcenc  " + encoder.Path + " (" + version + ")");
            string output = Path.GetFullPath(options.Output);
            Directory.CreateDirectory(output);
            string work = Path.GetFullPath(options.Work ?? Path.Combine(output, "work"));
            string doneDir = Path.Combine(work, "done");
            Directory.CreateDirectory(doneDir);
            encoder.TempDir = Path.Combine(work, "tmp");
            if (Directory.Exists(encoder.TempDir)) Directory.Delete(encoder.TempDir, true);
            Directory.CreateDirectory(encoder.TempDir);
            CheckFreeSpace(output, log);

            // 1. Encode (resumable).
            Dictionary<string, Done> finished = Encode(options, selected, source, encoder, work, doneDir, log);
            if (options.EncodeOnly)
            {
                log.WriteLine("encode-only: " + finished.Count + " of " + selected.Count + " textures are in " + work + "; run again without --encode-only for the parts");
                return summary;
            }

            // 2. Which replacements go in: every file of a group, or none of it.
            var replaced = new Dictionary<string, Done>(StringComparer.Ordinal);
            var entryOf = new Dictionary<string, HdIndexEntry>(StringComparer.Ordinal);
            foreach (IGrouping<string, HdIndexEntry> g in selected.GroupBy(e => e.Group ?? "file:" + e.Hash))
            {
                List<HdIndexEntry> members = g.ToList();
                List<HdIndexEntry> missing = members.Where(e => !finished.ContainsKey(e.Hash)).ToList();
                if (missing.Count > 0)
                {
                    string what = g.Key.StartsWith("file:", StringComparison.Ordinal) ? members[0].AndroidPath : g.Key + " (" + members.Count + " files)";
                    summary.Skipped.Add(what + ": " + missing.Count + " not converted; Gameloft's file" + (members.Count > 1 ? "s stay" : " stays"));
                    continue;
                }
                foreach (HdIndexEntry e in members)
                {
                    replaced["Data/" + e.AndroidPath] = finished[e.Hash];
                    entryOf["Data/" + e.AndroidPath] = e;
                }
            }
            foreach (string s in summary.Skipped) log.WriteLine("skipped  " + s);

            // 3. The index describing every file as it now is (Gameloft's entries for the rest, byte for byte).
            var replacements = new Dictionary<string, List<PackEntry>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Done> r in replaced)
            {
                byte[] data = File.ReadAllBytes(Path.Combine(doneDir, r.Value.Hash + ".f"));
                if (data.Length != r.Value.Size || Crc32.Compute(data) != r.Value.Crc)
                    throw new InvalidDataException("the finished " + r.Key + " in " + doneDir + " does not match its journal line (delete it and run again)");
                List<PackEntry> old = source.Index.EntriesOf(r.Key);
                int[] pieces = PackIndex.PieceSizes(data.Length);
                var crcs = new List<uint>();
                long o = 0;
                foreach (int p in pieces)
                {
                    crcs.Add(Crc32.Compute(data, (int)o, p));
                    o += p;
                }
                replacements[r.Key] = PackIndex.EntriesFor(r.Key, data.Length, crcs, false, old);
            }
            PackIndex newIndex = source.Index.Replace(replacements);
            if (newIndex.LogicalFiles != source.Index.LogicalFiles)
                throw new InvalidDataException("the new index lists " + newIndex.LogicalFiles + " files, Gameloft's " + source.Index.LogicalFiles);

            // 4. The parts.
            var extras = new List<KeyValuePair<string, byte[]>>();
            string replacedTsv = ReplacedList.Write("ASTC", HdIndex.GameVersion, replaced.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new ReplacedList.Row
            {
                Path = r.Key, Size = r.Value.Size, Crc = r.Value.Crc, OriginalSize = r.Value.OriginalSize, OriginalCrc = r.Value.OriginalCrc,
            }));
            long dataBytes = newIndex.LogicalBytes;
            Dictionary<string, object> manifest = Manifest(options, version, replaced, entryOf, source, newIndex);
            string note = IndexNote(replaced.Count, newIndex, options);
            extras.Add(new KeyValuePair<string, byte[]>("files/" + IndexPath, newIndex.Raw));
            extras.Add(new KeyValuePair<string, byte[]>("files/" + IndexNotePath, Encoding.UTF8.GetBytes(note)));
            extras.Add(new KeyValuePair<string, byte[]>("files/" + ReplacedList.RelativePath, Encoding.UTF8.GetBytes(replacedTsv)));
            extras.Add(new KeyValuePair<string, byte[]>("files/" + ManifestPath, Encoding.UTF8.GetBytes(MiniJson.Serialize(manifest, true) + "\n")));

            var plan = new List<KeyValuePair<string, long>>();   // relative path, size
            foreach (KeyValuePair<string, long> f in source.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
                plan.Add(new KeyValuePair<string, long>(f.Key, replaced.TryGetValue(f.Key, out Done d) ? d.Size : f.Value));
            // README.txt is written last with the final numbers; reserve room for it.
            List<List<int>> parts = PlanParts(plan, extras.Sum(e => 76L + 2 * e.Key.Length + e.Value.Length) + 64 * 1024 + 76 + 20, options.PartLimit);
            foreach (string old in Directory.GetFiles(output, PartPrefix + "*.zip*")) File.Delete(old);
            string sumsPath = Path.Combine(output, "SHA256SUMS.txt");
            var sums = new StringBuilder();
            var ktxGpu = new long[2];   // new, original
            int textureFiles = 0, otherTextures = 0;
            for (int p = 0; p < parts.Count; p++)
            {
                string name = PartPrefix + (p + 1).ToString("D2", CultureInfo.InvariantCulture) + "-of-" + parts.Count.ToString("D2", CultureInfo.InvariantCulture) + ".zip";
                string target = Path.Combine(output, name);
                string partial = target + ".partial";
                using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var hashing = new HashingStream(file, hash);
                    var zip = new StoredZipWriter(hashing, false);
                    foreach (int i in parts[p])
                    {
                        string rel = plan[i].Key;
                        byte[] data;
                        Ktx header;
                        if (replaced.TryGetValue(rel, out Done d))
                        {
                            data = File.ReadAllBytes(Path.Combine(doneDir, d.Hash + ".f"));
                            AstcSource.CheckAgainst(newIndex.EntriesOf(rel), data, rel + " (replacement)");
                            byte[] original = File.ReadAllBytes(source.PathOf(rel));
                            AstcSource.CheckAgainst(source.Index.EntriesOf(rel), original, rel);
                            Ktx oh = AstcConverter.ReadHeader(original);
                            if (oh != null) ktxGpu[1] += oh.Levels.Sum(l => (long)l.Length);
                            header = AstcConverter.ReadHeader(data);
                            if (header != null) ktxGpu[0] += header.Levels.Sum(l => (long)l.Length);
                            summary.Replaced++;
                        }
                        else
                        {
                            data = source.ReadChecked(rel);
                            header = AstcConverter.ReadHeader(data);
                            if (header != null)
                            {
                                long g = header.Levels.Sum(l => (long)l.Length);
                                ktxGpu[0] += g;
                                ktxGpu[1] += g;
                            }
                        }
                        if (header != null) textureFiles++;
                        else if (LooksLikeTexture(rel, data)) otherTextures++;
                        if (data.LongLength != plan[i].Value) throw new InvalidDataException(rel + " changed size while the parts were written");
                        zip.Add("files/" + rel, data);
                        summary.Files++;
                    }
                    if (p == parts.Count - 1)
                    {
                        foreach (KeyValuePair<string, byte[]> e in extras) zip.Add(e.Key, e.Value);
                        summary.DataBytes = dataBytes;
                        summary.OriginalDataBytes = source.Index.LogicalBytes;
                        summary.TextureGpuBytes = ktxGpu[0];
                        summary.OriginalTextureGpuBytes = ktxGpu[1];
                        string readme = Readme(options, summary, parts.Count, replaced, newIndex, version, textureFiles);
                        zip.Add("README.txt", Encoding.UTF8.GetBytes(readme));
                    }
                    zip.Finish();
                    hashing.Flush();
                    if (zip.Length >= options.PartLimit) throw new IOException(name + " has " + zip.Length + " bytes, not below the part limit " + options.PartLimit);
                    file.Flush(true);
                    sums.Append(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()).Append("  ").Append(name).Append('\n');
                }
                File.Move(partial, target);
                summary.Parts.Add(target);
                log.WriteLine("wrote    " + name + " (" + parts[p].Count + " files, " + Mb(new FileInfo(target).Length) + ")");
            }
            File.WriteAllText(sumsPath, sums.ToString());
            summary.Report = Report(options, summary, replaced, entryOf, index, textureFiles, otherTextures);
            File.WriteAllText(Path.Combine(output, "astc-hd-report.txt"), summary.Report);
            log.WriteLine();
            log.Write(summary.Report);
            log.WriteLine("time     " + clock.Elapsed.TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture) + " min");
            if (!options.KeepWork)
            {
                Directory.Delete(work, true);
                log.WriteLine("removed  the work folder " + work);
            }
            log.WriteLine("The parts hold textures converted from your own game files: keep them for your own phone, do not share them.");
            return summary;
        }

        private static bool LooksLikeTexture(string rel, byte[] data) => data.Length > 4 && data[0] == 'P' && data[1] == 'V' && data[2] == 'R';

        /// <summary>Groups the files (in order) into parts whose zip stays below the limit; the last also takes <paramref name="lastExtra"/> bytes.</summary>
        internal static List<List<int>> PlanParts(List<KeyValuePair<string, long>> files, long lastExtra, long limit)
        {
            var parts = new List<List<int>>();
            var current = new List<int>();
            long size = 22;
            for (int i = 0; i < files.Count; i++)
            {
                long cost = 30 + 46 + 2L * ("files/" + files[i].Key).Length + files[i].Value;
                if (22 + cost >= limit) throw new IOException(files[i].Key + " alone does not fit in a part of " + limit + " bytes");
                if (current.Count > 0 && size + cost >= limit)
                {
                    parts.Add(current);
                    current = new List<int>();
                    size = 22;
                }
                current.Add(i);
                size += cost;
            }
            if (size + lastExtra >= limit)
            {
                parts.Add(current);
                current = new List<int>();
            }
            parts.Add(current);
            return parts;
        }

        private static Dictionary<string, Done> Encode(AstcOptions options, List<HdIndexEntry> selected, AstcSource source, Astcenc encoder,
                                                       string work, string doneDir, TextWriter log)
        {
            string journalPath = Path.Combine(work, "journal.tsv");
            var finished = new Dictionary<string, Done>(StringComparer.Ordinal);
            var wanted = selected.ToDictionary(e => e.Hash, StringComparer.Ordinal);
            if (File.Exists(journalPath))
            {
                foreach (string line in File.ReadAllLines(journalPath))
                {
                    Done d = Done.Parse(line);
                    if (d == null || !wanted.TryGetValue(d.Hash, out HdIndexEntry e)) continue;
                    AstcSetting s = options.Settings[e.Category];
                    string file = Path.Combine(doneDir, d.Hash + ".f");
                    // A texture finished with another block size or quality is done again; so is one whose file is gone.
                    if (d.Block != s.Block || d.Quality != s.Quality || !File.Exists(file) || new FileInfo(file).Length != d.Size) continue;
                    finished[d.Hash] = d;
                }
            }
            var items = new List<Item>();
            var groups = new Dictionary<string, Item>(StringComparer.Ordinal);
            foreach (HdIndexEntry e in selected)
            {
                if (finished.ContainsKey(e.Hash)) continue;
                string g = e.Group;
                Item item;
                if (g == null || !groups.TryGetValue(g, out item))
                {
                    item = new Item { Group = g };
                    items.Add(item);
                    if (g != null) groups[g] = item;
                }
                item.Entries.Add(e);
                item.Texels += Texels(e);
            }
            long left = items.Sum(i => i.Texels);
            log.WriteLine("encode   " + finished.Count + " textures already finished in " + work + "; " + items.Sum(i => i.Entries.Count) + " to do (" + Mpx(left) + ")");
            if (items.Count == 0) return finished;

            // The work is a queue of single textures (biggest first); a group is complete when all of its files are.
            var queue = items.SelectMany(i => i.Entries).OrderByDescending(Texels).ToList();
            var clock = Stopwatch.StartNew();
            long doneTexels = 0;
            int count = 0, failures = 0;
            object gate = new object();
            using (var journal = new StreamWriter(new FileStream(journalPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true })
            {
                Parallel.ForEach(Partitioner.Create(queue, EnumerablePartitionerOptions.NoBuffering),
                                 new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Threads) }, e =>
                {
                    AstcSetting s = options.Settings[e.Category];
                    string rel = "Data/" + e.AndroidPath;
                    try
                    {
                        byte[] original = source.ReadChecked(rel);
                        string pcFile = Path.Combine(options.PcData, e.PcPath);
                        if (!File.Exists(pcFile)) throw new FileNotFoundException("no " + e.PcPath + " in the PC Data folder");
                        AstcResult r = AstcConverter.Convert(original, File.ReadAllBytes(pcFile), s, encoder);
                        if (r.OriginalWidth != e.AndroidWidth || r.OriginalHeight != e.AndroidHeight || r.OriginalMipLevels != e.AndroidMips)
                            throw new InvalidDataException("the ASTC original is " + r.OriginalWidth + "x" + r.OriginalHeight + " with " + r.OriginalMipLevels + " levels, the table says " +
                                                           e.AndroidWidth + "x" + e.AndroidHeight + " with " + e.AndroidMips);
                        string target = Path.Combine(doneDir, e.Hash + ".f");
                        File.WriteAllBytes(target + ".tmp", r.File);
                        File.Move(target + ".tmp", target, true);
                        var d = new Done
                        {
                            Hash = e.Hash, Block = s.Block, Quality = s.Quality, Size = r.File.Length, Crc = Crc32.Compute(r.File), Width = r.Width, Height = r.Height,
                            Mips = r.MipLevels, OriginalWidth = r.OriginalWidth, OriginalHeight = r.OriginalHeight, OriginalMips = r.OriginalMipLevels,
                            GpuBytes = r.GpuBytes, OriginalGpuBytes = r.OriginalGpuBytes, OriginalSize = original.Length, OriginalCrc = Crc32.Compute(original),
                            Psnr = r.Psnr, AlphaPsnr = r.AlphaPsnr, OriginalPsnr = r.OriginalPsnr, OriginalUpscaledPsnr = r.OriginalUpscaledPsnr,
                            Seconds = r.Elapsed.TotalSeconds, Opaque = r.Opaque,
                        };
                        lock (gate)
                        {
                            journal.WriteLine(d.Line());
                            finished[e.Hash] = d;
                            count++;
                            doneTexels += r.Texels;
                            double rate = doneTexels / Math.Max(0.001, clock.Elapsed.TotalSeconds);
                            TimeSpan eta = TimeSpan.FromSeconds((left - doneTexels) / Math.Max(1, rate));
                            log.WriteLine("[" + count + "/" + queue.Count + "] " + e.AndroidPath.PadRight(12) + " " + e.Category.PadRight(10) + " " +
                                          (r.OriginalWidth + "x" + r.OriginalHeight).PadLeft(9) + " -> " + (r.Width + "x" + r.Height).PadRight(9) + " " + s.Block +
                                          "  PSNR " + Db(r.Psnr) + (double.IsNaN(r.AlphaPsnr) ? "" : " a " + Db(r.AlphaPsnr)) + " (Gameloft " + Db(r.OriginalUpscaledPsnr) + ")  " +
                                          r.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s  ETA " + (int)eta.TotalHours + "h" + eta.Minutes.ToString("D2", CultureInfo.InvariantCulture));
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is DataPackException || ex is UnauthorizedAccessException)
                    {
                        lock (gate)
                        {
                            failures++;
                            log.WriteLine("failed   " + rel + " (" + e.Category + "): " + ex.Message);
                        }
                    }
                });
            }
            log.WriteLine("encode   done in " + clock.Elapsed.TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture) + " min, " + failures + " failed");
            return finished;
        }

        private static void CheckFreeSpace(string folder, TextWriter log)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(folder));
                log.WriteLine("disk     " + Mb(drive.AvailableFreeSpace) + " free at " + folder + " (the parts take about 3.5 GB, the work folder about 1.5 GB while encoding)");
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)
            {
            }
        }

        private static Dictionary<string, object> Manifest(AstcOptions options, string version, Dictionary<string, Done> replaced,
                                                           Dictionary<string, HdIndexEntry> entryOf, AstcSource source, PackIndex newIndex)
        {
            var files = new List<object>();
            foreach (KeyValuePair<string, Done> r in replaced.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                Done d = r.Value;
                HdIndexEntry e = entryOf[r.Key];
                files.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["path"] = r.Key,
                    ["hash"] = e.Hash,
                    ["category"] = e.Category,
                    ["levels"] = e.Levels.Cast<object>().ToList(),
                    ["group"] = e.Group,
                    ["block"] = d.Block,
                    ["quality"] = d.Quality,
                    ["original"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["size"] = d.OriginalSize, ["crc32"] = Hex(d.OriginalCrc), ["width"] = (long)d.OriginalWidth, ["height"] = (long)d.OriginalHeight,
                        ["mipLevels"] = (long)d.OriginalMips, ["gpuBytes"] = d.OriginalGpuBytes,
                    },
                    ["replacement"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["size"] = d.Size, ["crc32"] = Hex(d.Crc), ["width"] = (long)d.Width, ["height"] = (long)d.Height, ["mipLevels"] = (long)d.Mips,
                        ["gpuBytes"] = d.GpuBytes,
                    },
                    ["opaque"] = d.Opaque,
                    ["psnr"] = Round(d.Psnr),
                    ["alphaPsnr"] = double.IsNaN(d.AlphaPsnr) ? null : (object)Round(d.AlphaPsnr),
                    ["originalPsnr"] = Round(d.OriginalPsnr),
                    ["originalUpscaledPsnr"] = Round(d.OriginalUpscaledPsnr),
                });
            }
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["format"] = 1L,
                ["kind"] = "astc-hd-data-pack",
                ["tool"] = ToolName,
                ["astcenc"] = version,
                ["created"] = options.NowUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["gameVersion"] = HdIndex.GameVersion,
                ["pack"] = "ASTC",
                ["settings"] = options.Settings.Where(s => options.Categories.Contains(s.Key)).ToDictionary(s => s.Key, s => (object)s.Value.ToString(), StringComparer.Ordinal),
                ["totals"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["files"] = (long)newIndex.LogicalFiles,
                    ["bytes"] = newIndex.LogicalBytes,
                    ["replaced"] = (long)replaced.Count,
                    ["gameloftBytes"] = source.Index.LogicalBytes,
                },
                ["files"] = files,
            };
        }

        private static string IndexNote(int replaced, PackIndex index, AstcOptions options)
        {
            return "Index of the MCVS Offline ASTC HD data pack: Gameloft's Modern Combat Versus 1.17.32 ASTC pack with " + replaced +
                   " textures replaced by the PC (Steam) textures re-encoded to ASTC (tools/hd-textures astc-pack; " + index.LogicalFiles + " files, " +
                   index.LogicalBytes.ToString(CultureInfo.InvariantCulture) + " bytes), made on " +
                   options.NowUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) + ".\n" +
                   "It lists every file as it is in this pack (sizes and CRC32s), so the game's installer accepts the data without internet.\n" +
                   "replaced.tsv lists the replaced files; see docs/INSTALL-OFFLINE.md (\"The ASTC HD data pack\").\n";
        }

        private static string Readme(AstcOptions options, AstcPackSummary summary, int partCount, Dictionary<string, Done> replaced, PackIndex index,
                                     string version, int textureFiles)
        {
            const string phone = "/sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files";
            var sb = new StringBuilder();
            sb.Append("MCVS Offline - ASTC HD data pack (Modern Combat Versus 1.17.32)\n");
            sb.Append("===============================================================\n\n");
            sb.Append("Gameloft's ASTC data pack with ").Append(replaced.Count).Append(" textures replaced by the PC (Steam) textures, re-encoded to ASTC at\n");
            sb.Append("the PC resolution (").Append(string.Join(", ", options.Categories.Where(options.Settings.ContainsKey).Select(c => c + " " + options.Settings[c]))).Append(";\n");
            sb.Append(version).Append("). Every other file is Gameloft's own, unchanged.\n\n");
            sb.Append("On the phone: ").Append(index.LogicalFiles).Append(" files in Data/, ").Append(Mb(index.LogicalBytes)).Append(" (Gameloft's ASTC pack: ")
              .Append(Mb(summary.OriginalDataBytes)).Append(").\n");
            sb.Append("Texture memory of the whole pack: ").Append(Mb(summary.TextureGpuBytes)).Append(" (Gameloft's: ").Append(Mb(summary.OriginalTextureGpuBytes))
              .Append("); a match loads only its map's and a few characters' textures.\n\n");
            sb.Append("The parts (").Append(partCount).Append(" zips, check them with SHA256SUMS.txt) all unpack into one folder `files/`:\n");
            sb.Append("  files/Data/...                         the data pack\n");
            sb.Append("  files/McvsOffline/install/pack.info    the installer's index of exactly these files (sizes, CRC32s, 2 MiB pieces)\n");
            sb.Append("  files/McvsOffline/install/replaced.tsv the replaced files and Gameloft's size and CRC32 of each\n");
            sb.Append("  files/McvsOffline/install/astc-hd.json per texture: block size, sizes, PSNR self-check\n\n");
            sb.Append("This needs an MCVS Offline APK that knows replaced.tsv (built after build 11): it gives this index to the game's\n");
            sb.Append("installer, which then accepts the files at the next start without internet and without SKIP_VALIDATION.\n");
            sb.Append("BUILD 11 AND OLDER keep the installer's old ETC index, and the installer would delete these files and try to download\n");
            sb.Append("them: with such a build clear the app's storage first (Android Settings > Apps > the game > Storage > Clear storage,\n");
            sb.Append("after step 1), then go on with step 2.\n\n");
            sb.Append("INSTALL (from a phone that has the ETC data and a manual ETC HD install)\n\n");
            sb.Append("1. Back up what you want to keep, at least your profile:\n");
            sb.Append("     adb pull ").Append(phone).Append("/McvsOffline mcvs-profile-backup\n");
            sb.Append("2. Check the parts and unpack every part into one folder on the computer (each part adds its share of files/):\n");
            sb.Append("     sha256sum -c SHA256SUMS.txt\n");
            sb.Append("     for z in ").Append(PartPrefix).Append("*.zip; do unzip -o \"$z\" -d astc-hd; done\n");
            sb.Append("3. Force-stop the game, then remove the test switch, the ETC data, its index files and the HD installer's state:\n");
            sb.Append("     adb shell am force-stop com.gameloft.android.ANMP.GloftMVHM\n");
            sb.Append("     adb shell rm -f ").Append(phone).Append("/qaTestingConfigs.txt\n");
            sb.Append("     adb shell rm -rf ").Append(phone).Append("/Data\n");
            sb.Append("     adb shell rm -f ").Append(phone).Append("/d_o_w_n_l_o_a_d_e_d.txt ").Append(phone).Append("/pack.info\n");
            sb.Append("     adb shell rm -f ").Append(phone).Append("/McvsOffline/install/pack.info ").Append(phone).Append("/McvsOffline/install/pack.info.txt \\\n");
            sb.Append("                     ").Append(phone).Append("/McvsOffline/install/verify-pending ").Append(phone).Append("/McvsOffline/install/damaged.txt\n");
            sb.Append("     adb shell rm -rf ").Append(phone).Append("/McvsOffline/hd ").Append(phone).Append("/McvsOffline/hd-install\n");
            sb.Append("   (To keep the ETC data for going back, move it instead: adb shell mv ").Append(phone).Append("/Data ").Append(phone).Append("/../Data-ETC\n");
            sb.Append("    needs about 2 GB more on the phone; or keep your computer's copy.)\n");
            sb.Append("4. Push the new files/ tree:\n");
            sb.Append("     adb push astc-hd/files/. ").Append(phone).Append("/\n");
            sb.Append("5. Start the game. The start-up log (Files > Downloads > MCVS-Offline > ...-bootstrap.log.txt, logcat tag McvsOffline)\n");
            sb.Append("   shows one line such as\n");
            sb.Append("     [install] installer data: index switched to the saved index (ASTC with ").Append(replaced.Count).Append(" replaced files, ")
              .Append(index.LogicalFiles).Append(" files, ").Append(index.Entries.Count).Append(" entries)\n");
            sb.Append("   or, after a reinstall, 'installer data: index restored from the saved index (ASTC with ...)'. A minute after the\n");
            sb.Append("   game has loaded, 'installer data: background check done, every file matches the CRC32 ...' confirms every file.\n");
            sb.Append("   The installer screen shows briefly and the game starts, with no network needed.\n\n");
            sb.Append("GOING BACK TO ETC: force-stop, delete Data/, d_o_w_n_l_o_a_d_e_d.txt and McvsOffline/install/{pack.info,pack.info.txt,\n");
            sb.Append("replaced.tsv,astc-hd.json,verify-pending}, push the ETC files/ tree (a mirror made with tools/cdn-mirror --tex ETC\n");
            sb.Append("--extract, or your backup of the ETC phone, with its McvsOffline/install/pack.info), start the game: the log says\n");
            sb.Append("'installer data: index switched to the saved index (ETC, ...)'. A manual ETC HD install goes on top as before.\n\n");
            sb.Append("Gameloft's original ASTC pack is unchanged on the computer that made this (tools/cdn-mirror mirror).\n");
            sb.Append("These files contain textures converted from your own game files: keep them for your own phone, do not share them.\n");
            sb.Append("ASTC encoding by ARM astc-encoder (Apache License 2.0), run by tools/hd-textures on your computer; it is not part of this pack.\n");
            return sb.ToString();
        }

        private static string Report(AstcOptions options, AstcPackSummary summary, Dictionary<string, Done> replaced, Dictionary<string, HdIndexEntry> entryOf,
                                     HdIndex index, int textureFiles, int otherTextures)
        {
            var sb = new StringBuilder();
            sb.Append("parts    ").Append(summary.Parts.Count).Append(" (each below ").Append(Mb(options.PartLimit)).Append("), SHA256SUMS.txt\n");
            sb.Append("files    ").Append(summary.Files).Append(" in Data/ (").Append(summary.Replaced).Append(" replaced), ").Append(Mb(summary.DataBytes))
              .Append(" on the phone (Gameloft's ASTC pack ").Append(Mb(summary.OriginalDataBytes)).Append(")\n");
            sb.Append("textures ").Append(textureFiles).Append(" ASTC KTX files: ").Append(Mb(summary.TextureGpuBytes)).Append(" of texture data on the GPU for the whole game (Gameloft's ")
              .Append(Mb(summary.OriginalTextureGpuBytes)).Append(")").Append(otherTextures > 0 ? "; " + otherTextures + " PVR images not counted" : "").Append('\n');
            sb.Append("category    files  block              GPU before -> after     disk before -> after    PSNR median (Gameloft)  min   alpha median\n");
            foreach (string category in options.Categories)
            {
                List<KeyValuePair<string, Done>> rs = replaced.Where(r => entryOf[r.Key].Category == category).ToList();
                if (rs.Count == 0) continue;
                List<double> alphas = rs.Select(r => r.Value.AlphaPsnr).Where(a => !double.IsNaN(a)).ToList();
                sb.Append(category.PadRight(11)).Append(rs.Count.ToString(CultureInfo.InvariantCulture).PadLeft(6)).Append("  ")
                  .Append(options.Settings[category].ToString().PadRight(15))
                  .Append(Mb(rs.Sum(r => r.Value.OriginalGpuBytes)).PadLeft(11)).Append(" -> ").Append(Mb(rs.Sum(r => r.Value.GpuBytes)).PadRight(10))
                  .Append(Mb(rs.Sum(r => r.Value.OriginalSize)).PadLeft(11)).Append(" -> ").Append(Mb(rs.Sum(r => r.Value.Size)).PadRight(10))
                  .Append(F(Median(rs.Select(r => r.Value.Psnr)))).Append(" (").Append(F(Median(rs.Select(r => r.Value.OriginalUpscaledPsnr)))).Append(")          ")
                  .Append(F(rs.Min(r => r.Value.Psnr))).Append("  ").Append(alphas.Count == 0 ? "-" : F(Median(alphas))).Append('\n');
            }
            sb.Append("PSNR: RGB of the top level against the PC texture; Gameloft = its ASTC 6x6 at the phone's size, enlarged bilinearly to the PC size.\n");
            sb.Append("per match (estimate): the map's own textures (surfaces, lightmaps, minimap) plus a share of the level-independent ones\n");
            long independentBefore = replaced.Where(r => entryOf[r.Key].Levels.Length == 0).Sum(r => r.Value.OriginalGpuBytes);
            long independentAfter = replaced.Where(r => entryOf[r.Key].Levels.Length == 0).Sum(r => r.Value.GpuBytes);
            foreach (HdLevel level in index.Levels)
            {
                List<Done> own = replaced.Where(r => entryOf[r.Key].Levels.Contains(level.Name)).Select(r => r.Value).ToList();
                if (own.Count == 0) continue;
                List<Done> lightmaps = replaced.Where(r => entryOf[r.Key].Category == HdIndex.Lightmaps && entryOf[r.Key].Levels.Contains(level.Name)).Select(r => r.Value).ToList();
                sb.Append("  ").Append(level.Name.PadRight(10)).Append(own.Count.ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append(" map textures ")
                  .Append(Mb(own.Sum(d => d.OriginalGpuBytes)).PadLeft(9)).Append(" -> ").Append(Mb(own.Sum(d => d.GpuBytes)).PadRight(9))
                  .Append(" (lightmaps, all variants: ").Append(Mb(lightmaps.Sum(d => d.OriginalGpuBytes))).Append(" -> ").Append(Mb(lightmaps.Sum(d => d.GpuBytes))).Append(")\n");
            }
            sb.Append("  level-independent (characters, equipment, props, vfx, 3D menus): ").Append(Mb(independentBefore)).Append(" -> ").Append(Mb(independentAfter))
              .Append(" for all of them; a match uses about a quarter of the characters (about 10 of 40 sets)\n");
            return sb.ToString();
        }

        /// <summary>A stream that hashes what passes through it.</summary>
        private sealed class HashingStream : Stream
        {
            private readonly Stream inner;
            private readonly IncrementalHash hash;

            public HashingStream(Stream inner, IncrementalHash hash)
            {
                this.inner = inner;
                this.hash = hash;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => throw new NotSupportedException();
            }

            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                hash.AppendData(buffer, offset, count);
                inner.Write(buffer, offset, count);
            }
        }

        internal static double Median(IEnumerable<double> values)
        {
            List<double> v = values.Where(x => !double.IsNaN(x)).Select(Cap).OrderBy(x => x).ToList();
            if (v.Count == 0) return double.NaN;
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }

        private static double Cap(double v) => double.IsPositiveInfinity(v) ? 99 : v;
        private static double Round(double v) => double.IsPositiveInfinity(v) ? 99.0 : Math.Round(v, 2);
        private static string F(double v) => double.IsNaN(v) ? "  -  " : Cap(v).ToString("0.0", CultureInfo.InvariantCulture).PadLeft(5);
        private static string Db(double v) => (double.IsPositiveInfinity(v) ? "inf" : v.ToString("0.0", CultureInfo.InvariantCulture)) + " dB";
        private static string Hex(uint v) => v.ToString("x8", CultureInfo.InvariantCulture);
        internal static string Mb(long bytes) => (bytes / 1048576.0).ToString("#,0.0", CultureInfo.InvariantCulture) + " MB";
        private static string Mpx(long texels) => (texels / 1e6).ToString("#,0.0", CultureInfo.InvariantCulture) + " Mtexel";
    }
}
