using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using McvsOffline.PcSpike;
using McvsOffline.Server.Core.Content;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// mcvs-hd-textures: turns the PC (Steam) build's textures into replacements for the phone's ETC data pack
    /// (docs/design/hd-textures.md). Command <c>build</c> writes the pack; <c>index</c> (maintainers) regenerates the table.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                return Run(args, Console.Out);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException || ex is IOException || ex is DataPackException ||
                                       ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        internal static int Run(string[] args, TextWriter output)
        {
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "help")
            {
                output.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }
            string[] known;
            switch (args[0])
            {
                case "build":
                    known = new[] { "--pc", "--android-data", "--scope", "--maps", "--out", "--min-psnr", "--threads", "--only", "--dry-run" };
                    break;
                case "index":
                    known = new[] { "--decoded", "--android-data", "--pc", "--out" };
                    break;
                case "astc-pack":
                    known = new[] { "--astc-files", "--pc", "--out", "--work", "--scope", "--blocks", "--quality", "--astcenc", "--threads", "--only",
                                    "--part-size", "--keep-work", "--encode-only", "--dry-run" };
                    break;
                case "astc-measure":
                    known = new[] { "--astc-files", "--pc", "--scope", "--blocks", "--quality", "--astcenc", "--threads", "--only", "--samples" };
                    break;
                default:
                    known = null;
                    break;
            }
            Dictionary<string, string> o = Options(args, known, args[0]);
            if (args[0] == "build") return Build(o, output);
            if (args[0] == "astc-pack" || args[0] == "astc-measure") return Astc(args[0], o, output);
            return Index(o, output);
        }

        private static int Astc(string command, Dictionary<string, string> o, TextWriter output)
        {
            var options = new AstcOptions
            {
                Categories = HdIndex.ParseScope(Get(o, "--scope") ?? "all"),
                Output = Get(o, "--out"),
                Work = Get(o, "--work"),
                DryRun = o.ContainsKey("--dry-run"),
                KeepWork = o.ContainsKey("--keep-work"),
                EncodeOnly = o.ContainsKey("--encode-only"),
            };
            string quality = Get(o, "--quality");
            if (quality != null) quality = quality.Trim().TrimStart('-').ToLowerInvariant();
            if (o.ContainsKey("--threads")) options.Threads = Math.Max(1, int.Parse(o["--threads"], CultureInfo.InvariantCulture));
            if (o.ContainsKey("--only"))
                options.OnlyHashes = new HashSet<string>(o["--only"].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(NormalizeHash));
            if (o.ContainsKey("--part-size")) options.PartLimit = long.Parse(o["--part-size"], CultureInfo.InvariantCulture) * 1024 * 1024;
            if (command == "astc-measure")
            {
                options.MeasureSettings = (Get(o, "--blocks") ?? "4x4,5x5,6x6").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                                                              .Select(b => AstcSetting.Parse(b, quality ?? "thorough")).ToList();
                if (o.ContainsKey("--samples")) options.Samples = Math.Max(1, int.Parse(o["--samples"], CultureInfo.InvariantCulture));
            }
            else
            {
                options.Settings = AstcPackBuilder.ParseSettings(Get(o, "--blocks"), quality, AstcPackBuilder.DefaultSettings());
                if (!options.DryRun && string.IsNullOrEmpty(options.Output)) throw new ArgumentException("give --out <folder for the upload parts>");
            }
            if (!options.DryRun)
            {
                options.AstcFiles = Get(o, "--astc-files") ?? Environment.GetEnvironmentVariable("MCVS_ASTC_MIRROR");
                options.PcData = ResolvePcData(Get(o, "--pc") ?? Environment.GetEnvironmentVariable("MCVS_PC_INSTALL"), output);
                options.Encoder = Astcenc.Locate(Get(o, "--astcenc"), out string how);
                output.WriteLine("astcenc  found by " + how);
            }
            if (command == "astc-measure") AstcPackBuilder.Measure(options, output);
            else AstcPackBuilder.Build(options, output);
            return 0;
        }

        private static int Build(Dictionary<string, string> o, TextWriter output)
        {
            HdIndex index = HdIndex.Load();
            var options = new BuildOptions
            {
                Categories = HdIndex.ParseScope(Get(o, "--scope")),
                Maps = index.ParseMaps(Get(o, "--maps")),
                Output = Get(o, "--out"),
                DryRun = o.ContainsKey("--dry-run"),
                Index = index,
            };
            if (o.ContainsKey("--min-psnr")) options.MinPsnr = double.Parse(o["--min-psnr"], CultureInfo.InvariantCulture);
            if (o.ContainsKey("--threads")) options.Threads = Math.Max(1, int.Parse(o["--threads"], CultureInfo.InvariantCulture));
            if (o.ContainsKey("--only"))
                options.OnlyHashes = new HashSet<string>(o["--only"].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(NormalizeHash));
            if (!options.DryRun)
            {
                options.AndroidData = ResolveAndroidData(Get(o, "--android-data"));
                options.PcData = ResolvePcData(Get(o, "--pc"), output);
                output.WriteLine("PC       " + options.PcData);
                output.WriteLine("Android  " + options.AndroidData);
            }
            PackBuilder.Build(options, output);
            return 0;
        }

        private static int Index(Dictionary<string, string> o, TextWriter output)
        {
            string decoded = Get(o, "--decoded") ?? Environment.GetEnvironmentVariable("MCVS_DATAPACK_DECODED");
            if (string.IsNullOrEmpty(decoded)) throw new ArgumentException("give --decoded <the decoded data pack> (or MCVS_DATAPACK_DECODED)");
            string android = ResolveAndroidData(Get(o, "--android-data") ?? Environment.GetEnvironmentVariable("MCVS_DATAPACK"));
            string pc = ResolvePcData(Get(o, "--pc") ?? Environment.GetEnvironmentVariable("MCVS_PC_INSTALL"), output);
            string target = Get(o, "--out") ?? "hd-index.tsv";
            HdIndex index = IndexBuilder.Build(decoded, android, pc, output);
            File.WriteAllText(target, index.Write(new[]
            {
                "McvsOffline HD texture table (tools/hd-textures): our own facts, no game data. Generated by",
                "'McvsOffline.HdTextures index' from the 1.17.32 ETC pack and the Steam build 6372049 (docs/design/hd-textures.md).",
                "level <name> <MapNN alias>; tex <crc32 of the logical path> <category> <levels> <android layout> <android bytes> <w> <h> <mips>",
                "<pc bytes> <w> <h> <mips>",
            }));
            output.WriteLine("wrote    " + Path.GetFullPath(target) + " (" + index.Entries.Count + " textures, " + index.Levels.Count + " levels)");
            return 0;
        }

        /// <summary>
        /// The PC build's Data folder: <paramref name="given"/> (the game folder, its Data folder or the exe), else the Steam
        /// install found like tools/pc-spike finds it.
        /// </summary>
        internal static string ResolvePcData(string given, TextWriter output)
        {
            string folder = given;
            if (string.IsNullOrEmpty(folder))
            {
                string problem;
                GameLocation found = SteamLocator.Locate(null, out problem);
                if (found == null) throw new ArgumentException("give --pc <the Steam game folder>: " + problem);
                output.WriteLine("found    the PC game: " + found.Directory + " (" + found.How + ")");
                folder = found.Directory;
            }
            folder = Path.GetFullPath(folder.Trim().Trim('"'));
            if (File.Exists(folder)) folder = Path.GetDirectoryName(folder);
            if (LooksLikePcData(Path.Combine(folder, "Data"))) return Path.Combine(folder, "Data");
            if (LooksLikePcData(folder)) return folder;
            throw new ArgumentException(folder + " is not the PC game folder or its Data folder (no numbered .f files)");
        }

        /// <summary>The phone's Data folder: the folder holding MCVS.game, or its parent (adb pull of files/).</summary>
        internal static string ResolveAndroidData(string given)
        {
            if (string.IsNullOrEmpty(given)) throw new ArgumentException("give --android-data <the Data folder pulled from the phone>");
            string folder = Path.GetFullPath(given.Trim().Trim('"'));
            if (File.Exists(Path.Combine(folder, "MCVS.game")) || Directory.Exists(Path.Combine(folder, "1"))) return folder;
            string nested = Path.Combine(folder, "Data");
            if (File.Exists(Path.Combine(nested, "MCVS.game")) || Directory.Exists(Path.Combine(nested, "1"))) return nested;
            throw new ArgumentException(folder + " is not the game's Data folder (no MCVS.game, no 0-f folders)");
        }

        private static bool LooksLikePcData(string folder)
        {
            if (!Directory.Exists(folder)) return false;
            foreach (string f in Directory.EnumerateFiles(folder, "*.f"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (name.Length > 0 && name.All(char.IsDigit)) return true;
            }
            return false;
        }

        private static string NormalizeHash(string h)
        {
            uint v = uint.Parse(h.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return v.ToString("x8", CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, string> Options(string[] args, string[] known, string command)
        {
            if (known == null) throw new ArgumentException("unknown command '" + command + "' (known: build, astc-pack, astc-measure, index)");
            var o = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (!known.Contains(a)) throw new ArgumentException("unknown option '" + a + "' for " + command);
                if (a == "--dry-run" || a == "--keep-work" || a == "--encode-only")
                {
                    o[a] = "1";
                    continue;
                }
                if (i + 1 >= args.Length) throw new ArgumentException(a + " needs a value");
                o[a] = args[++i];
            }
            return o;
        }

        private static string Get(Dictionary<string, string> o, string key)
        {
            return o.TryGetValue(key, out string v) ? v : null;
        }

        internal const string Usage =
            "mcvs-hd-textures build --android-data <phone Data folder> [--pc <Steam game folder>] [--scope default|all|<categories>]\n" +
            "                       [--maps all|<levels>] [--out <zip or folder>] [--min-psnr 24] [--threads N] [--dry-run]\n" +
            "\n" +
            "  Converts the textures of the PC (Steam) build that are larger than the phone's into ETC1 replacements for the\n" +
            "  phone's ETC data pack and writes them as one pack, mcvs-hd-<scope>.zip (docs/design/hd-textures.md).\n" +
            "  --android-data  the Data folder of your phone, pulled with:\n" +
            "                    adb pull /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/Data\n" +
            "  --pc            the game's Steam folder (found automatically on Windows when Steam installed it)\n" +
            "  --scope         default = surfaces,lightmaps,characters,equipment; all adds props,vfx,menus3d,minimaps;\n" +
            "                  or a comma list of those categories\n" +
            "  --maps          levels for surfaces and lightmaps, by name or MapNN: Port (Map01), Favelas (Map02),\n" +
            "                  Desert (Map03), Snow (Map04), Rooftop (Map05), DogDays (Map06), Stadium (Map08),\n" +
            "                  Stadium2 (Map09), Bootcamp; default all\n" +
            "  --min-psnr      flag textures whose self-check is below this many dB (default 24)\n" +
            "  --dry-run       only list what the scope holds\n" +
            "\n" +
            "mcvs-hd-textures astc-pack --astc-files <ASTC files folder> --out <folder> [--pc <Steam game folder>] [--astcenc <path>]\n" +
            "                       [--blocks surfaces=5x5,lightmaps=6x6:medium,...] [--quality thorough] [--work <folder>]\n" +
            "                       [--threads N] [--part-size 380] [--keep-work] [--encode-only] [--dry-run]\n" +
            "\n" +
            "  The whole ASTC data pack for an ASTC phone: Gameloft's ASTC pack (a tools/cdn-mirror --tex ASTC --extract folder)\n" +
            "  with every texture the PC has larger re-encoded to ASTC at the PC size by ARM's astcenc, written as upload parts\n" +
            "  (stored zips below 380 MiB, entries files/...) with the installer index for exactly these files. Resumable: run\n" +
            "  it again after an interruption. --astcenc or MCVS_ASTCENC: the astcenc binary (README.md, \"astcenc\").\n" +
            "\n" +
            "mcvs-hd-textures astc-measure --astc-files <ASTC files folder> [--pc ...] [--blocks 4x4,5x5,6x6] [--quality thorough] [--samples 6]\n" +
            "  Encodes a few textures per category at each block size and prints PSNR, memory and time.\n" +
            "\n" +
            "mcvs-hd-textures index --decoded <decoded pack> --android-data <ETC Data> --pc <PC game> [--out hd-index.tsv]\n" +
            "  Maintainers: regenerates the texture table built into the tool.\n" +
            "\n" +
            "The pack holds textures converted from your own game files: keep it for your own phone, do not share it.";
    }
}
