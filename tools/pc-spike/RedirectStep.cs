using System;
using System.Globalization;
using System.IO;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;

namespace McvsOffline.PcSpike
{
    /// <summary>
    /// WP0 step 2 (pc-port.md §5, unknown 1): does the CLR honour <c>ModernCombatVersus.exe.config</c> and a
    /// <c>&lt;codeBase&gt;</c> for this mixed-mode exe? 'redirect-test' writes a marker-writing copy of the player's
    /// EngineBridge.dll to <c>&lt;game&gt;\McvsOffline\spike\EngineBridge.dll</c> and a config with one codeBase pointing at
    /// it. Starting the game then either writes the marker (the redirect works: option A is open) or not (option B,
    /// in-place with backups). No Steam file is modified: the original EngineBridge.dll is only read, the config and the
    /// subfolder are new, and an existing config (there is none in the depot) is backed up before it is replaced.
    /// 'redirect-undo' removes both and restores that backup.
    /// </summary>
    public static class RedirectStep
    {
        public const string AssemblyName = "EngineBridge";
        public const string AssemblyFileName = "EngineBridge.dll";

        /// <summary>The copy's folder relative to the exe folder (also the codeBase href's folder).</summary>
        public const string CopyFolder = "McvsOffline/spike";

        public const string MarkerFileName = "redirect-marker.txt";
        public const string InstalledFileName = "redirect-installed.txt";

        public static string MarkerPath(string dataDir) => Path.Combine(dataDir, MarkerFileName);

        public static string CopyPath(string gameDir) => Path.Combine(gameDir, "McvsOffline", "spike", AssemblyFileName);

        public static string ConfigPath(string gameDir) => Path.Combine(gameDir, ExeConfig.FileName);

        /// <summary>What to do when the copy cannot be deleted: Windows keeps a DLL the game process has loaded.</summary>
        public const string QuitAndUndoAgain = "quit the game (end ModernCombatVersus.exe in Task Manager if it hangs), then run 'redirect-undo' again";

        /// <summary>
        /// Patches the player's EngineBridge.dll in memory with <see cref="EngineBridgeMarkerPatch"/> and returns the new image
        /// and the assembly version (for the codeBase element). Throws <see cref="PatchException"/> on an unknown build.
        /// </summary>
        public static byte[] BuildMarkerCopy(string originalEngineBridge, string markerPath, out Version version, out string note)
        {
            byte[] original = File.ReadAllBytes(originalEngineBridge);
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(originalEngineBridge)));
            using (resolver)
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(original), new ReaderParameters { AssemblyResolver = resolver }))
            {
                var context = new PatchContext(null, 0);
                new EngineBridgeMarkerPatch(markerPath).Apply(module, context);
                note = string.Join("; ", context.Notes);
                version = module.Assembly.Name.Version;
                using (var output = new MemoryStream())
                {
                    module.Write(output);
                    return output.ToArray();
                }
            }
        }

        public static bool Install(string gameDir, string dataDir, SpikeLog log)
        {
            string original = Path.Combine(gameDir, AssemblyFileName);
            if (!File.Exists(original))
            {
                log.Error("redirect-test: " + original + " is missing");
                return false;
            }
            log.Info("redirect-test: EngineBridge.dll is " + KnownPcBuild.Describe(original, KnownPcBuild.EngineBridgeSha256));
            string marker = MarkerPath(dataDir);
            Directory.CreateDirectory(dataDir);
            if (File.Exists(marker))
            {
                File.Delete(marker);
                log.Info("redirect-test: removed the marker of an earlier run");
            }

            byte[] image;
            Version version;
            string note;
            try
            {
                image = BuildMarkerCopy(original, marker, out version, out note);
            }
            catch (PatchException ex)
            {
                log.Error("redirect-test refused: " + ex.Message);
                log.Result("redirect test", "not run: EngineBridge.dll is not the verified Steam build");
                return false;
            }
            log.Info("redirect-test: " + note);

            string copy = CopyPath(gameDir);
            string config = ConfigPath(gameDir);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy));
                File.WriteAllBytes(copy, image);
                if (File.Exists(config) && !ExeConfig.IsOurs(File.ReadAllText(config)))
                {
                    string backupDir = Path.Combine(dataDir, ArgsTxtStep.BackupFolder);
                    string backup = Path.Combine(backupDir, ExeConfig.FileName);
                    Directory.CreateDirectory(backupDir);
                    if (!File.Exists(backup)) File.Copy(config, backup);
                    log.Warn("redirect-test: " + config + " already existed (not ours); backed up to " + backup);
                }
                File.WriteAllText(config, ExeConfig.Build(AssemblyName, version, CopyFolder + "/" + AssemblyFileName));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Error("redirect-test: cannot write into the game folder: " + ex.Message);
                log.Result("install folder writable", "NO: " + ex.Message);
                return false;
            }
            File.WriteAllText(Path.Combine(dataDir, InstalledFileName), DateTime.Now.ToString("o", CultureInfo.InvariantCulture) + "\n");
            log.Info("redirect-test: wrote " + copy + " and " + config);
            return true;
        }

        /// <summary>
        /// Removes our copy and config, restores a config we replaced. Returns false when something was left behind. The copy
        /// goes first: while the game still runs, Windows refuses to delete the DLL it loaded, and then the config stays
        /// too, so 'status' still shows the folder as modified (and a later 'redirect-undo' finishes the job).
        /// </summary>
        public static bool Undo(string gameDir, string dataDir, SpikeLog log)
        {
            return Undo(gameDir, dataDir, log, File.Delete);
        }

        /// <summary><see cref="Undo(string, string, SpikeLog)"/> with another file deletion (tests: a DLL Windows keeps loaded).</summary>
        internal static bool Undo(string gameDir, string dataDir, SpikeLog log, Action<string> deleteFile)
        {
            bool clean = true;
            string config = ConfigPath(gameDir);
            string backup = Path.Combine(dataDir, ArgsTxtStep.BackupFolder, ExeConfig.FileName);
            try
            {
                string copy = CopyPath(gameDir);
                if (File.Exists(copy))
                {
                    deleteFile(copy);
                    log.Info("redirect-undo: deleted " + copy);
                }
                DeleteIfEmpty(Path.GetDirectoryName(copy));
                DeleteIfEmpty(Path.Combine(gameDir, "McvsOffline"));
                if (File.Exists(config))
                {
                    if (ExeConfig.IsOurs(File.ReadAllText(config)))
                    {
                        deleteFile(config);
                        log.Info("redirect-undo: deleted " + config);
                    }
                    else
                    {
                        log.Warn("redirect-undo: " + config + " is not ours; left as it is");
                        clean = false;
                    }
                }
                if (File.Exists(backup) && !File.Exists(config))
                {
                    File.Move(backup, config);
                    log.Info("redirect-undo: restored the earlier " + ExeConfig.FileName);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Error("redirect-undo: " + ex.Message + " (" + QuitAndUndoAgain + ")");
                clean = false;
            }
            return clean;
        }

        /// <summary>One line for the console and summary.txt: did the game load our copy?</summary>
        public static string DescribeMarker(string dataDir)
        {
            string installed = Path.Combine(dataDir, InstalledFileName);
            string marker = MarkerPath(dataDir);
            if (!File.Exists(installed) && !File.Exists(marker)) return "not run";
            if (!File.Exists(marker))
                return "NO MARKER: the game did not load our EngineBridge copy (or was not started after redirect-test at " + ReadTrimmed(installed) + ")";
            string text = ReadTrimmed(marker);
            string when = File.GetLastWriteTime(marker).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (!text.StartsWith(EngineBridgeMarkerPatch.MarkerHeader, StringComparison.Ordinal)) return "marker present but unreadable: " + text;
            return "MARKER WRITTEN at " + when + ": the CLR honoured ModernCombatVersus.exe.config <codeBase> (" +
                   text.Substring(EngineBridgeMarkerPatch.MarkerHeader.Length).Trim().Replace("\r\n", "; ") + ")";
        }

        private static string ReadTrimmed(string file)
        {
            try
            {
                return File.ReadAllText(file).Trim();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "(unreadable: " + ex.Message + ")";
            }
        }

        private static void DeleteIfEmpty(string dir)
        {
            if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
        }
    }
}
