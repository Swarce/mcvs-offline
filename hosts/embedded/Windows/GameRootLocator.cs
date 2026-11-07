#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL.
using System;
using System.Collections.Generic;
using System.IO;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>
    /// The game root on PC (docs/design/pc-port.md §6.3, D1): the folder of ModernCombatVersus.exe, where the native engine reads
    /// args.txt and the data pack (<c>Data\</c>). Candidates in order: <c>MCVS_GAME_ROOT</c> (tests, a game started outside its
    /// folder), <c>AppDomain.CurrentDomain.BaseDirectory</c> (the exe folder: the CLR is hosted by the exe), the directory of
    /// the process's main module. The first that holds <c>Data\MCVS.game</c> wins. Pure: every input is passed in.
    /// </summary>
    public static class GameRootLocator
    {
        public const string Variable = "MCVS_GAME_ROOT";

        /// <summary>The file that marks a game root (the engine's game definition in the data pack).</summary>
        public static readonly string Marker = Path.Combine("Data", "MCVS.game");

        /// <summary>
        /// The first candidate holding <see cref="Marker"/>, as a full path. Throws <see cref="InvalidOperationException"/> naming
        /// every candidate when none does.
        /// </summary>
        public static string Locate(string variable, string baseDirectory, Func<string> mainModuleDirectory, Func<string, bool> fileExists)
        {
            var tried = new List<string>();
            foreach (string candidate in Candidates(variable, baseDirectory, mainModuleDirectory))
            {
                string full;
                try
                {
                    full = Path.GetFullPath(candidate);
                }
                catch (Exception)
                {
                    continue;
                }
                if (fileExists(Path.Combine(full, Marker))) return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                tried.Add(full);
            }
            throw new InvalidOperationException("the game folder is unknown: no " + Marker + " in " +
                                                (tried.Count == 0 ? "any candidate" : string.Join(", ", tried.ToArray())));
        }

        /// <summary><see cref="Locate"/> for this process.</summary>
        public static string LocateForProcess()
        {
            return Locate(Environment.GetEnvironmentVariable(Variable), AppDomain.CurrentDomain.BaseDirectory, MainModuleDirectory, File.Exists);
        }

        private static IEnumerable<string> Candidates(string variable, string baseDirectory, Func<string> mainModuleDirectory)
        {
            if (!string.IsNullOrEmpty(variable)) yield return variable;
            if (!string.IsNullOrEmpty(baseDirectory)) yield return baseDirectory;
            string module = null;
            try
            {
                module = mainModuleDirectory == null ? null : mainModuleDirectory();
            }
            catch (Exception)
            {
            }
            if (!string.IsNullOrEmpty(module)) yield return module;
        }

        private static string MainModuleDirectory()
        {
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                return Path.GetDirectoryName(process.MainModule.FileName);
            }
        }
    }
}
#endif
