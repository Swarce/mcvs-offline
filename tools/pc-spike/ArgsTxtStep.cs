using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Embedded;

namespace McvsOffline.PcSpike
{
    /// <summary>What <see cref="ArgsTxtStep.Install"/> did.</summary>
    public sealed class ArgsInstallResult
    {
        public bool Written { get; internal set; }

        /// <summary>Why args.txt could not be written (the install folder is not writable, pc-port.md critic note 3), or null.</summary>
        public string WriteError { get; internal set; }

        /// <summary>The file content as written (or as it would have been).</summary>
        public string Content { get; internal set; }

        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// WP0 step 1 (pc-port.md §5): <c>args.txt</c> next to ModernCombatVersus.exe with the same managed block the embedded
    /// host writes on Android (<see cref="ManagedArgs"/>, <see cref="ArgsFile"/>: <c>-ServerLocation Embedded</c>,
    /// <c>-AnubisCommand QuickLaunch</c>, <c>-EnvironmentSwitcherUrl http://127.0.0.1:51780/</c>, the super credential,
    /// <c>-DeviceId</c>, <c>-DataCenter mcvs</c>). The native reader is the same on PC (§1.3: <c>0x1010b900</c>).
    /// Before the first write the existing file is copied to <c>&lt;data&gt;\backup\args.txt</c> (or its absence is recorded);
    /// a second 'start' keeps that first backup. <see cref="Restore"/> puts the original back or deletes ours.
    /// </summary>
    public static class ArgsTxtStep
    {
        public const string BackupFolder = "backup";
        public const string StateFileName = "args.txt.state";
        public const string StateExisted = "existed";
        public const string StateAbsent = "absent";

        public static string ArgsPath(string gameDir) => Path.Combine(gameDir, ArgsFile.FileName);

        /// <summary>
        /// Files named <c>*_args.txt</c> in the game folder: the native reader prefers <c>&lt;prefix&gt;_args.txt</c> when it
        /// exists (§1.3, string at <c>0x10cdff24</c>), which would hide our args.txt.
        /// </summary>
        public static List<string> PrefixedArgsFiles(string gameDir)
        {
            if (!Directory.Exists(gameDir)) return new List<string>();
            return Directory.GetFiles(gameDir, "*_args.txt").Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static ArgsInstallResult Install(string gameDir, string dataDir, IList<ArgEntry> managed, SpikeLog log)
        {
            var result = new ArgsInstallResult();
            string args = ArgsPath(gameDir);
            string backupDir = Path.Combine(dataDir, BackupFolder);
            string state = Path.Combine(backupDir, StateFileName);
            byte[] existing = File.Exists(args) ? File.ReadAllBytes(args) : null;

            if (File.Exists(state))
            {
                log.Info("args.txt: keeping the backup made by an earlier 'start' (" + File.ReadAllText(state).Trim() + "); 'stop' restores it");
            }
            else
            {
                Directory.CreateDirectory(backupDir);
                if (existing != null)
                {
                    File.WriteAllBytes(Path.Combine(backupDir, ArgsFile.FileName), existing);
                    File.WriteAllText(state, StateExisted + "\n");
                    log.Info("args.txt: backed up the existing file (" + existing.Length + " bytes) to " + Path.Combine(backupDir, ArgsFile.FileName));
                }
                else
                {
                    File.WriteAllText(state, StateAbsent + "\n");
                    log.Info("args.txt: there was none; 'stop' will delete ours");
                }
            }

            ArgsMergeResult merge = ArgsFile.Merge(existing, managed);
            result.Content = Encoding.ASCII.GetString(merge.Content);
            result.Warnings.AddRange(merge.Warnings);
            foreach (string key in merge.OverriddenKeys)
                result.Warnings.Add("-" + key + " is already set outside the managed block; the existing value wins");
            if (!merge.Changed)
            {
                result.Written = true;
                log.Info("args.txt: already up to date at " + args);
                return result;
            }
            try
            {
                File.WriteAllBytes(args, merge.Content);
                result.Written = true;
                log.Result("install folder writable", "yes (wrote " + args + ")");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                result.WriteError = ex.GetType().Name + ": " + ex.Message;
                log.Result("install folder writable", "NO: " + result.WriteError);
            }
            return result;
        }

        /// <summary>Puts the original args.txt back (or deletes ours when there was none). Returns what it did.</summary>
        public static string Restore(string gameDir, string dataDir, SpikeLog log)
        {
            string args = ArgsPath(gameDir);
            string backupDir = Path.Combine(dataDir, BackupFolder);
            string state = Path.Combine(backupDir, StateFileName);
            string backup = Path.Combine(backupDir, ArgsFile.FileName);
            if (!File.Exists(state))
            {
                bool ours = File.Exists(args) && ArgsFile.ReadManagedValue(File.ReadAllBytes(args), "ServerLocation") != null;
                string message = ours
                    ? "args.txt has an MCVS Offline block but no backup record was found; left as it is (delete " + args + " by hand if you want)"
                    : "nothing to restore (no backup record)";
                log.Info("stop: " + message);
                return message;
            }
            string recorded = File.ReadAllText(state).Trim();
            string done;
            if (recorded == StateExisted)
            {
                if (!File.Exists(backup)) throw new IOException("the backup " + backup + " is missing; args.txt left as it is");
                File.WriteAllBytes(args, File.ReadAllBytes(backup));
                File.Delete(backup);
                done = "restored the original args.txt";
            }
            else
            {
                if (File.Exists(args)) File.Delete(args);
                done = "deleted our args.txt (there was none before)";
            }
            File.Delete(state);
            log.Info("stop: " + done + " (" + args + ")");
            return done;
        }
    }
}
