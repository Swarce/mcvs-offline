using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using McvsOffline.Server.Core.Content;

namespace McvsOffline.PcSpike
{
    /// <summary>
    /// The server reads two game files (the CRM default <c>Config/crmpreconfiguration.json</c>, byte-identical on PC, and
    /// the live-ops division tables <c>Design/MCVS_Meta.xlsx</c>). The Steam <c>Data\</c> folder names files
    /// <c>&lt;decimal crc32(logical path)&gt;.f</c> in one flat folder (pc-port.md §4.1), which <see cref="DataPack"/> does not
    /// read yet (WP1). Until then the kit copies those files, still encoded, from the player's own install to
    /// <c>&lt;data&gt;\pc-data\&lt;logical path&gt;</c> plus <c>MCVS.game</c>: a decoded-tree layout <see cref="DataPack"/> reads
    /// (GCBF is decoded on read). The copy stays on the player's PC.
    /// </summary>
    public static class PcDataView
    {
        public const string FolderName = "pc-data";

        /// <summary>The logical paths the server reads (CrmPreconfiguration.LogicalPath, DivisionTables.LogicalPath).</summary>
        public static readonly string[] ServerFiles = { "Config/crmpreconfiguration.json", "Design/MCVS_Meta.xlsx" };

        /// <summary>The Steam file name of a logical path: "Config/glue.json" -> "187993782.f".</summary>
        public static string PcFileName(string logicalPath)
        {
            uint crc = Crc32.Compute(Encoding.UTF8.GetBytes(logicalPath.Replace('\\', '/').TrimStart('/')));
            return crc.ToString(CultureInfo.InvariantCulture) + ".f";
        }

        /// <summary>Builds the view; returns its folder, or null (the server then runs without a data pack, as it warns).</summary>
        public static string Build(string gameDir, string dataDir, SpikeLog log)
        {
            string data = Path.Combine(gameDir, "Data");
            string game = Path.Combine(data, "MCVS.game");
            if (!File.Exists(game))
            {
                log.Warn("no " + game + ": the server runs without the game's CRM defaults");
                return null;
            }
            string view = Path.Combine(dataDir, FolderName);
            try
            {
                Directory.CreateDirectory(view);
                File.Copy(game, Path.Combine(view, "MCVS.game"), true);
                var missing = new List<string>();
                foreach (string logical in ServerFiles)
                {
                    string source = Path.Combine(data, PcFileName(logical));
                    if (!File.Exists(source))
                    {
                        missing.Add(logical);
                        continue;
                    }
                    string target = Path.Combine(view, logical.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(source, target, true);
                }
                if (missing.Count > 0) log.Warn("not in the Steam Data folder: " + string.Join(", ", missing));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Warn("cannot copy the server's game files to " + view + ": " + ex.Message);
                return null;
            }
            return view;
        }
    }
}
