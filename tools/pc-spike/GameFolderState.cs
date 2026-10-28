using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Embedded;

namespace McvsOffline.PcSpike
{
    /// <summary>
    /// What the kit may still have left in the Steam game folder, each with the command that removes it: 'status' and
    /// 'collect' print these on the console, so the owner does not take a modified folder for a clean one (PC-SPIKE.md §6).
    /// </summary>
    public static class GameFolderState
    {
        /// <summary>One line per leftover of ours in <paramref name="gameDir"/>; empty when the folder is as Steam installed it.</summary>
        public static List<string> Leftovers(string gameDir)
        {
            var left = new List<string>();
            string args = ArgsTxtStep.ArgsPath(gameDir);
            try
            {
                if (File.Exists(args) && ArgsFile.ReadManagedValue(File.ReadAllBytes(args), "ServerLocation") != null)
                    left.Add("args.txt still has the MCVS Offline block (the game would look for our server): run 'stop' to restore the original");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                left.Add("args.txt could not be read (" + ex.Message + "): run 'status' again once the game is closed");
            }
            string config = RedirectStep.ConfigPath(gameDir);
            try
            {
                if (File.Exists(config) && ExeConfig.IsOurs(File.ReadAllText(config)))
                    left.Add(ExeConfig.FileName + " is ours: run 'redirect-undo'");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                left.Add(ExeConfig.FileName + " could not be read (" + ex.Message + "): run 'status' again once the game is closed");
            }
            if (File.Exists(RedirectStep.CopyPath(gameDir)))
                left.Add(RedirectStep.CopyFolder.Replace('/', '\\') + "\\" + RedirectStep.AssemblyFileName + " is still there: " + RedirectStep.QuitAndUndoAgain);
            return left;
        }
    }
}
