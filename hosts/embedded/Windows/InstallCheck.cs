#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>
    /// The optional boot check of docs/design/pc-port.md §6.2: are the patched copies in McvsOffline\bin still made from the
    /// game files Steam has now? It compares the SHA-256 of <c>&lt;GameRoot&gt;\Game.dll</c>, <c>Gameloft.Engine.dll</c>,
    /// <c>Gameloft.Util.dll</c> and <c>EngineBridge.dll</c> with <c>McvsOffline\install.json</c> <c>originals.&lt;file&gt;.sha256</c>
    /// (only the keys fixed in §3.10; a <c>format</c> other than 1 is not checked). Reads files only; never throws.
    /// </summary>
    public static class InstallCheck
    {
        public const string ManifestFile = "install.json";

        /// <summary>The game-folder originals the installer patched (§3.10).</summary>
        public static readonly string[] Originals = { "EngineBridge.dll", "Game.dll", "Gameloft.Engine.dll", "Gameloft.Util.dll" };

        /// <summary>The result line without "[boot] ": "install: current", "install: stale: ...", or an "install: ..." note.</summary>
        public sealed class Result
        {
            public bool Stale { get; set; }
            public string Line { get; set; }
        }

        /// <summary>Compares the game folder with <c>&lt;dataDir&gt;\install.json</c>.</summary>
        public static Result Run(string gameRoot, string dataDir)
        {
            try
            {
                string manifest = Path.Combine(dataDir, ManifestFile);
                if (!File.Exists(manifest)) return Note("install: no McvsOffline\\" + ManifestFile + " (not installed by the kit); no check");
                object parsed;
                if (!MiniJson.TryParse(File.ReadAllText(manifest, Encoding.UTF8), out parsed) || !(parsed is Dictionary<string, object>))
                    return Note("install: McvsOffline\\" + ManifestFile + " does not parse; no check");
                var root = (Dictionary<string, object>)parsed;
                object format;
                if (!root.TryGetValue("format", out format) || !(format is long) || (long)format != 1)
                    return Note("install: unknown install.json format; no check");
                object originalsValue;
                var originals = root.TryGetValue("originals", out originalsValue) ? originalsValue as Dictionary<string, object> : null;
                if (originals == null) return Note("install: install.json has no originals; no check");
                var changed = new List<string>();
                foreach (string file in Originals)
                {
                    object entryValue;
                    var entry = originals.TryGetValue(file, out entryValue) ? entryValue as Dictionary<string, object> : null;
                    object hashValue = null;
                    string expected = entry != null && entry.TryGetValue("sha256", out hashValue) ? hashValue as string : null;
                    if (string.IsNullOrEmpty(expected)) continue;
                    string path = Path.Combine(gameRoot, file);
                    string actual = File.Exists(path) ? Sha256(path) : null;
                    if (actual == null) changed.Add(file + " missing");
                    else if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) changed.Add(file);
                }
                if (changed.Count == 0) return new Result { Line = "install: current" };
                return new Result
                {
                    Stale = true,
                    Line = "install: stale: Steam updated the game (" + string.Join(", ", changed.ToArray()) + " changed since the install); run the kit's uninstall, then install",
                };
            }
            catch (Exception ex)
            {
                return Note("install: the check failed (" + ex.GetType().Name + ")");
            }
        }

        /// <summary>
        /// The "install:" part of the status line (WR2 F2): the file check's line, unless the running process shows the install is
        /// not active (<paramref name="notActive"/>, the patch watcher's reason): then "install: installed, NOT ACTIVE (...)",
        /// followed by the file check's line when that found something too. Null when neither is known yet.
        /// </summary>
        public static string Describe(string fileCheckLine, string notActive)
        {
            if (string.IsNullOrEmpty(notActive)) return fileCheckLine;
            string line = "install: " + PatchWatcher.NotActiveText(notActive);
            if (!string.IsNullOrEmpty(fileCheckLine) && fileCheckLine != "install: current") line += "; " + fileCheckLine;
            return line;
        }

        internal static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(64);
                foreach (byte b in hash) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static Result Note(string line)
        {
            return new Result { Line = line };
        }
    }
}
#endif
