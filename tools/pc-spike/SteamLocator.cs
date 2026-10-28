using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace McvsOffline.PcSpike
{
    /// <summary>Where the game was found and how.</summary>
    public sealed class GameLocation
    {
        public GameLocation(string directory, string how)
        {
            Directory = directory;
            How = how;
        }

        /// <summary>The folder holding ModernCombatVersus.exe.</summary>
        public string Directory { get; }

        /// <summary>A line for the log and summary.txt ("--game", "appmanifest_723780.acf in D:\SteamLibrary", ...).</summary>
        public string How { get; }

        public string Exe => Path.Combine(Directory, SteamLocator.ExeName);
    }

    /// <summary>
    /// Finds the Steam install of Modern Combat Versus (app 723780; pc-port.md §3.5): the Steam folder from the registry
    /// (<c>HKCU\Software\Valve\Steam\SteamPath</c>, then the machine-wide <c>InstallPath</c>), every library listed in
    /// <c>steamapps\libraryfolders.vdf</c> (both the current and the pre-2021 layout), and in the library whose
    /// <c>steamapps\appmanifest_723780.acf</c> exists, <c>steamapps\common\&lt;installdir&gt;</c>. The search over folders
    /// is pure (<see cref="FindInSteamRoots"/>) and tested on any OS; only <see cref="SteamRootsFromRegistry"/> is Windows-only.
    /// </summary>
    public static class SteamLocator
    {
        public const string AppId = "723780";
        public const string ExeName = "ModernCombatVersus.exe";
        public const string ManifestName = "appmanifest_" + AppId + ".acf";

        public static bool LooksLikeGameFolder(string directory)
        {
            return !string.IsNullOrEmpty(directory) && File.Exists(Path.Combine(directory, ExeName));
        }

        /// <summary>
        /// The game folder: <paramref name="explicitFolder"/> (--game or MCVS_PC_GAME) when given, else the Steam search.
        /// Null with <paramref name="problem"/> set when nothing fits.
        /// </summary>
        public static GameLocation Locate(string explicitFolder, out string problem)
        {
            problem = null;
            if (!string.IsNullOrEmpty(explicitFolder))
            {
                string full = Path.GetFullPath(explicitFolder.Trim().Trim('"'));
                if (File.Exists(full) && string.Equals(Path.GetFileName(full), ExeName, StringComparison.OrdinalIgnoreCase))
                    full = Path.GetDirectoryName(full);
                if (LooksLikeGameFolder(full)) return new GameLocation(full, "given on the command line");
                problem = "no " + ExeName + " in " + full;
                return null;
            }
            var roots = new List<string>(SteamRootsFromRegistry());
            roots.AddRange(DefaultSteamRoots());
            GameLocation found = FindInSteamRoots(roots, out problem);
            return found;
        }

        /// <summary>Searches each Steam folder and its libraries; returns the first folder that has the exe.</summary>
        public static GameLocation FindInSteamRoots(IEnumerable<string> steamRoots, out string problem)
        {
            var checkedLibraries = new List<string>();
            foreach (string root in Distinct(steamRoots))
            {
                foreach (string library in Distinct(new[] { root }.Concat(LibrariesOf(root))))
                {
                    checkedLibraries.Add(library);
                    GameLocation hit = FindInLibrary(library);
                    if (hit != null)
                    {
                        problem = null;
                        return hit;
                    }
                }
            }
            problem = checkedLibraries.Count == 0
                ? "Steam was not found (no registry entry, no default folder)"
                : "Modern Combat Versus (app " + AppId + ") was not found in the Steam libraries " + string.Join(", ", checkedLibraries);
            return null;
        }

        /// <summary>The game in one library: through its appmanifest, else any steamapps\common\*\ModernCombatVersus.exe.</summary>
        public static GameLocation FindInLibrary(string library)
        {
            string steamapps = Path.Combine(library, "steamapps");
            string manifest = Path.Combine(steamapps, ManifestName);
            if (File.Exists(manifest))
            {
                string installDir = null;
                try
                {
                    VdfNode state = Vdf.Parse(File.ReadAllText(manifest))["AppState"];
                    installDir = state == null ? null : state.Get("installdir");
                }
                catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    installDir = null;
                }
                if (!string.IsNullOrEmpty(installDir))
                {
                    string dir = Path.Combine(steamapps, "common", installDir);
                    if (LooksLikeGameFolder(dir)) return new GameLocation(dir, ManifestName + " in " + library);
                }
            }
            string common = Path.Combine(steamapps, "common");
            if (!System.IO.Directory.Exists(common)) return null;
            try
            {
                foreach (string dir in System.IO.Directory.GetDirectories(common).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    if (LooksLikeGameFolder(dir)) return new GameLocation(dir, "found in " + common);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
            return null;
        }

        /// <summary>The library folders listed in a Steam folder's libraryfolders.vdf files (missing or broken files: none).</summary>
        public static List<string> LibrariesOf(string steamRoot)
        {
            var libraries = new List<string>();
            foreach (string file in new[] { Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"), Path.Combine(steamRoot, "config", "libraryfolders.vdf") })
            {
                if (!File.Exists(file)) continue;
                try
                {
                    libraries.AddRange(ParseLibraryFolders(File.ReadAllText(file)));
                }
                catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
            return libraries;
        }

        /// <summary>
        /// The paths in a libraryfolders.vdf. Current layout: <c>"libraryfolders" { "0" { "path" "C:\\Steam" ... } }</c>;
        /// old layout: <c>"LibraryFolders" { "TimeNextStatsReport" "..." "1" "D:\\SteamLibrary" }</c>.
        /// </summary>
        public static List<string> ParseLibraryFolders(string text)
        {
            var paths = new List<string>();
            VdfNode root = Vdf.Parse(text);
            VdfNode folders = root["libraryfolders"];
            if (folders == null || !folders.IsBlock) return paths;
            foreach (VdfNode entry in folders.Children)
            {
                int index;
                if (!int.TryParse(entry.Key, out index)) continue;
                string path = entry.IsBlock ? entry.Get("path") : entry.Value;
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }
            return paths;
        }

        /// <summary>Steam folders named in the registry (Windows only; empty elsewhere).</summary>
        public static IEnumerable<string> SteamRootsFromRegistry()
        {
            var roots = new List<string>();
            if (!OperatingSystem.IsWindows()) return roots;
            try
            {
                AddRegistryValue(roots, Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
                AddRegistryValue(roots, Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
                AddRegistryValue(roots, Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            }
            catch (Exception)
            {
                // A locked-down registry only means falling back to the default folders.
            }
            return roots;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void AddRegistryValue(List<string> roots, Microsoft.Win32.RegistryKey hive, string path, string name)
        {
            using (Microsoft.Win32.RegistryKey key = hive.OpenSubKey(path))
            {
                string value = key == null ? null : key.GetValue(name) as string;
                if (!string.IsNullOrEmpty(value)) roots.Add(value.Replace('/', '\\'));
            }
        }

        private static IEnumerable<string> DefaultSteamRoots()
        {
            if (!OperatingSystem.IsWindows()) yield break;
            string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(x86)) yield return Path.Combine(x86, "Steam");
            string x64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(x64)) yield return Path.Combine(x64, "Steam");
        }

        private static IEnumerable<string> Distinct(IEnumerable<string> folders)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in folders)
            {
                if (string.IsNullOrEmpty(folder)) continue;
                string full;
                try
                {
                    full = Path.GetFullPath(folder).TrimEnd('\\', '/');
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
                {
                    continue;
                }
                if (seen.Add(full)) yield return full;
            }
        }
    }
}
