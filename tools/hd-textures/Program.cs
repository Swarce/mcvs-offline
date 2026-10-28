using System;
using System.IO;
using McvsOffline.Server.Core.Content;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// mcvs-hd-textures: the desktop tool of the HD texture work (docs/research/pc-textures.md). Command "probe" builds the WP1
    /// device-probe zip (see <see cref="ProbeBuilder"/>); the PC-texture transcoder (WP2) comes later.
    /// </summary>
    public static class Program
    {
        public const string PackageName = "com.gameloft.android.ANMP.GloftMVHM";
        public const string PhoneZip = "/sdcard/Android/data/" + PackageName + "/files/McvsOffline/hd/hd-textures.zip";

        public static int Main(string[] args)
        {
            try
            {
                return Run(args, Console.Out);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException || ex is IOException || ex is DataPackException)
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
            if (args[0] != "probe") throw new ArgumentException("unknown command '" + args[0] + "' (known: probe)");
            string data = null, file = null, texture = ProbeBuilder.DefaultTexture, output1 = "hd-textures.zip";
            bool raw = false;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                Func<string> value = () =>
                {
                    if (i + 1 >= args.Length) throw new ArgumentException(a + " needs a value");
                    return args[++i];
                };
                switch (a)
                {
                    case "--data": data = value(); break;
                    case "--file": file = value(); break;
                    case "--texture": texture = value(); break;
                    case "--out": output1 = value(); break;
                    case "--raw": raw = true; break;
                    default: throw new ArgumentException("unknown option '" + a + "'");
                }
            }
            if ((data == null) == (file == null)) throw new ArgumentException("give either --data <Data folder> or --file <the texture's .f file>");
            string source = file;
            if (data != null)
            {
                var pack = new DataPack(data);
                source = pack.Locate(texture);
                if (source == null)
                    throw new ArgumentException(texture + " (" + DataPack.HashedPath(texture) + ") is not in " + pack.DataDirectory);
            }
            if (!File.Exists(source)) throw new ArgumentException(source + " does not exist");

            ProbeResult result = ProbeBuilder.Build(File.ReadAllBytes(source), texture, raw, DateTime.UtcNow);
            File.WriteAllBytes(output1, result.Zip);
            output.WriteLine("read     " + source + " (" + result.LogicalPath + ")");
            output.WriteLine("texture  " + result.Original.FormatName + " " + result.Original.Width + "x" + result.Original.Height + ", " +
                             result.Original.MipLevels + " mip levels (" + result.Pack + " pack)");
            output.WriteLine("wrote    " + Path.GetFullPath(output1) + " (" + result.Zip.Length + " bytes, entries " + (result.Gcbf ? "GCBF-wrapped" : "bare KTX") + "):");
            foreach (ProbeVariant v in result.Variants)
                output.WriteLine("           " + v.Colour.Name.PadRight(8) + " " + v.Naming.PadRight(19) + " " + v.Entry);
            output.WriteLine();
            output.WriteLine("On the phone (game closed):");
            output.WriteLine("  adb shell mkdir -p " + Path.GetDirectoryName(PhoneZip).Replace('\\', '/'));
            output.WriteLine("  adb push " + output1 + " " + PhoneZip);
            output.WriteLine("Then start the game, play a match on a map with concrete (the texture shows in one of the colours above if");
            output.WriteLine("the engine uses the zip) and read the '[boot] hd textures:' lines in logcat or McvsOffline/logs/bootstrap.log.");
            output.WriteLine("Remove it again: adb shell rm " + PhoneZip);
            return 0;
        }

        internal const string Usage =
            "mcvs-hd-textures probe (--data <Data folder> | --file <texture .f>) [--texture <logical path>] [--raw] [--out <zip>]\n" +
            "\n" +
            "  Builds the HD texture probe zip (docs/research/pc-textures.md WP1) from your own data pack: one texture,\n" +
            "  recoloured, under its logical name (magenta), its hashed Data/ name (green) and that name under Data/ (blue);\n" +
            "  --raw stores bare KTX instead of GCBF (yellow, cyan, orange). Default texture:\n" +
            "  " + ProbeBuilder.DefaultTexture + " (Data/8/dbd2f1.f).\n" +
            "  Get the file from the phone with: adb pull /sdcard/Android/data/" + PackageName + "/files/Data/8/dbd2f1.f\n" +
            "  The zip contains a recoloured copy of a game file: keep it for your own phone, do not share it.";
    }
}
