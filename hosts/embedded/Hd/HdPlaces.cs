// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
// Design: docs/design/hd-install.md ("Where the installer writes").
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using McvsOffline.Embedded.Install;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Embedded.Hd
{
    /// <summary>
    /// Where the HD installer reads and writes (docs/design/hd-install.md). <c>McvsOffline/hd/</c> is the player's drop folder:
    /// on Android 11+ a folder made there with a file manager or adb belongs to another uid, so the app can read the pack in it
    /// but create nothing; the installer never writes inside it. Its own files (installed.json, the backups of the originals,
    /// the import marker) go to a folder the app creates itself: <c>McvsOffline/hd-install/</c> next to the drop folder, or,
    /// when that cannot be written, the same folder in the app's internal storage (getFilesDir()). Folders of earlier starts
    /// (build 11 kept everything in the drop folder) are only read: their installed.json is taken over once, their backups are
    /// used where they are.
    /// </summary>
    internal sealed class HdPlaces
    {
        public const string WorkFolderName = "hd-install";
        public const string ProbeName = ".mcvs-hd-probe";

        /// <summary>One line per installed.json taken over from another folder (CRC-32, size, folder), so it is taken over once.</summary>
        public const string ImportedFile = "imported.txt";

        private readonly InstallerDataLocations loc;

        private HdPlaces(InstallerDataLocations loc)
        {
            this.loc = loc;
            Drop = HdInstaller.HdDir(loc);
            Primary = Path.Combine(Path.Combine(loc.GameRoot, EmbeddedPaths.DataFolderName), WorkFolderName);
            Fallback = Path.Combine(Path.Combine(loc.InternalDir, EmbeddedPaths.DataFolderName), WorkFolderName);
            Others = new List<string>();
        }

        /// <summary>&lt;game root&gt;/McvsOffline/hd: the pack; read only.</summary>
        public string Drop { get; private set; }

        /// <summary>&lt;game root&gt;/McvsOffline/hd-install.</summary>
        public string Primary { get; private set; }

        /// <summary>&lt;getFilesDir()&gt;/McvsOffline/hd-install.</summary>
        public string Fallback { get; private set; }

        /// <summary>The folder the installer writes its files to; null when none could be written (<see cref="Problem"/>).</summary>
        public string Work { get; private set; }

        /// <summary>The work folder is the one in internal storage.</summary>
        public bool IsFallback { get { return Work != null && Work == Fallback; } }

        /// <summary>Why <see cref="Primary"/> is not used ("permission denied"), or null.</summary>
        public string PrimaryProblem { get; private set; }

        /// <summary>One line when no folder could be written: "cannot write McvsOffline/hd-install: permission denied ...".</summary>
        public string Problem { get; private set; }

        /// <summary>
        /// Set by <see cref="Migrate"/> when our folder has no state and one of an earlier start could not be taken over: Data/
        /// may hold HD files whose originals only that state knows, so nothing is changed ("... cannot be read").
        /// </summary>
        public string Blocked { get; private set; }

        /// <summary>Folders an earlier start may have written to, other than <see cref="Work"/> (only read: installed.json, backups).</summary>
        public List<string> Others { get; private set; }

        public string StatePath { get { return Work == null ? null : Path.Combine(Work, HdInstaller.StateFile); } }

        public string BackupDir { get { return Work == null ? null : Path.Combine(Work, HdInstaller.BackupFolder); } }

        public string BackupPath(string relativePath)
        {
            return Backup(Work, relativePath);
        }

        internal static string Backup(string folder, string relativePath)
        {
            return Path.Combine(Path.Combine(folder, HdInstaller.BackupFolder), relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>The folders, without touching the disk; <see cref="Choose"/> picks the work folder.</summary>
        public static HdPlaces For(InstallerDataLocations loc)
        {
            return new HdPlaces(loc);
        }

        /// <summary>Some folder holds an installed.json (cheap: no folder is created).</summary>
        public bool AnyState()
        {
            return File.Exists(Path.Combine(Primary, HdInstaller.StateFile)) || File.Exists(Path.Combine(Fallback, HdInstaller.StateFile)) ||
                   File.Exists(Path.Combine(Drop, HdInstaller.StateFile));
        }

        /// <summary>
        /// Picks the work folder: the one of McvsOffline/hd-install and its internal-storage twin that holds an installed.json,
        /// else McvsOffline/hd-install first; each is created when missing and must take a probe file. Never throws.
        /// </summary>
        public void Choose()
        {
            bool fallbackFirst = File.Exists(Path.Combine(Fallback, HdInstaller.StateFile)) && !File.Exists(Path.Combine(Primary, HdInstaller.StateFile));
            string first = fallbackFirst ? Fallback : Primary, second = fallbackFirst ? Primary : Fallback;
            string firstProblem = ProbeFolder(first);
            string secondProblem = null;
            if (firstProblem == null)
            {
                Work = first;
            }
            else
            {
                secondProblem = ProbeFolder(second);
                if (secondProblem == null) Work = second;
            }
            string primaryProblem = first == Primary ? firstProblem : secondProblem;
            string fallbackProblem = first == Fallback ? firstProblem : secondProblem;
            if (Work != Primary) PrimaryProblem = primaryProblem;
            if (Work == null)
            {
                Problem = "cannot write " + Display(Primary) + ": " + primaryProblem + " (nor the app's internal storage: " + fallbackProblem + ")";
            }
            Others.Clear();
            foreach (string dir in new[] { Primary, Fallback, Drop })
            {
                if (dir != Work && !Others.Contains(dir)) Others.Add(dir);
            }
        }

        /// <summary>A folder relative to the game root ("McvsOffline/hd-install"), else the full path.</summary>
        public string Display(string path)
        {
            string root = loc.GameRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(root, StringComparison.Ordinal)) return path.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/');
            return path;
        }

        /// <summary>One line for the log: which folder is used and why.</summary>
        public string Describe()
        {
            if (Work == null) return Problem;
            if (IsFallback)
                return "installer files in the app's internal storage " + Work + " (" +
                       (PrimaryProblem != null ? Display(Primary) + ": " + PrimaryProblem : "it holds the state of an earlier start") + ")";
            return "installer files in " + Display(Work);
        }

        /// <summary>Null when <paramref name="dir"/> exists (created if missing) and a probe file can be written and deleted in it; else why not.</summary>
        public static string ProbeFolder(string dir)
        {
            try
            {
                HdInstaller.Guard(dir);
                Directory.CreateDirectory(dir);
                ProbeIn(dir);
                return null;
            }
            catch (Exception ex)
            {
                return Reason(ex);
            }
        }

        /// <summary>Writes, flushes and deletes a probe file in an existing folder; throws what the file system throws.</summary>
        public static void ProbeIn(string dir)
        {
            string probe = Path.Combine(dir, ProbeName);
            HdInstaller.Guard(probe);
            try
            {
                using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.WriteByte(1);
                    FileSystem.FlushToDisk(fs);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(probe)) File.Delete(probe);
                }
                catch (Exception)
                {
                }
            }
            if (File.Exists(probe)) throw new IOException("cannot delete " + probe);
        }

        /// <summary>"permission denied", or the file system's message.</summary>
        public static string Reason(Exception ex)
        {
            if (ex is UnauthorizedAccessException) return "permission denied";
            string m = ex.Message ?? ex.GetType().Name;
            if (m.IndexOf("ermission denied", StringComparison.Ordinal) >= 0 || m.IndexOf("ccess to the path", StringComparison.Ordinal) >= 0)
                return "permission denied";
            return m.TrimEnd('.');
        }

        /// <summary>
        /// The backup of <paramref name="relativePath"/> with <paramref name="size"/> bytes: in the work folder, else in a folder
        /// of an earlier start. Null when there is none of that size.
        /// </summary>
        public string FindBackup(string relativePath, long size)
        {
            if (Work != null)
            {
                string own = BackupPath(relativePath);
                if (HdInstaller.Length(own) == size) return own;
            }
            foreach (string dir in Others)
            {
                string other = Backup(dir, relativePath);
                if (HdInstaller.Length(other) == size) return other;
            }
            return null;
        }

        /// <summary>Every backup of <paramref name="relativePath"/> with <paramref name="size"/> bytes, ours first.</summary>
        public List<string> BackupCandidates(string relativePath, long size)
        {
            var found = new List<string>();
            if (Work != null && HdInstaller.Length(BackupPath(relativePath)) == size) found.Add(BackupPath(relativePath));
            foreach (string dir in Others)
            {
                string other = Backup(dir, relativePath);
                if (HdInstaller.Length(other) == size) found.Add(other);
            }
            return found;
        }

        /// <summary>A backup with the original's size and CRC-32, wherever it is; null when there is none.</summary>
        public string FindCheckedBackup(string relativePath, long size, uint crc)
        {
            var places = new List<string>();
            if (Work != null) places.Add(BackupPath(relativePath));
            foreach (string dir in Others) places.Add(Backup(dir, relativePath));
            foreach (string path in places)
            {
                if (HdInstaller.Length(path) == size && HdInstaller.FileCrc(path) == crc) return path;
            }
            return null;
        }

        /// <summary>Folders of earlier starts other than the work folder that still hold backups (for the "may be deleted" note).</summary>
        public List<string> OldBackupFolders()
        {
            var found = new List<string>();
            foreach (string dir in Others)
            {
                string backups = Path.Combine(dir, HdInstaller.BackupFolder);
                try
                {
                    if (Directory.Exists(backups) && Directory.GetFiles(backups, "*", SearchOption.AllDirectories).Length > 0) found.Add(backups);
                }
                catch (Exception)
                {
                }
            }
            return found;
        }

        /// <summary>
        /// When the work folder has no installed.json and another folder has one this folder has not taken over yet: it is
        /// checked (<see cref="HdState.Load"/>) and copied byte for byte into the work folder, then noted in imported.txt; its
        /// backups stay where they are. An installed.json that cannot be read is left for a later start (logged). Never throws.
        /// </summary>
        public void Migrate(Log log)
        {
            Blocked = null;
            if (Work == null) return;
            try
            {
                HashSet<string> imported = ImportedKeys();
                bool haveState = File.Exists(StatePath);
                foreach (string dir in Others)
                {
                    string source = Path.Combine(dir, HdInstaller.StateFile);
                    byte[] raw;
                    try
                    {
                        if (!File.Exists(source)) continue;
                        raw = File.ReadAllBytes(source);
                    }
                    catch (Exception ex)
                    {
                        log.Warn("hd: " + Display(source) + " of an earlier start cannot be read (" + Reason(ex) + "); it is not taken over");
                        if (!haveState && Blocked == null) Blocked = Display(source) + " of an earlier build cannot be read";
                        continue;
                    }
                    string key = Key(raw);
                    if (imported.Contains(key)) continue;
                    if (haveState)
                    {
                        log.Warn("hd: " + Display(source) + " of an earlier start is ignored: " + Display(StatePath) + " is in use");
                        Note(key, dir);
                        continue;
                    }
                    HdState state;
                    try
                    {
                        state = HdState.Load(source);
                    }
                    catch (FormatException ex)
                    {
                        log.Warn("hd: " + ex.Message + ": it is not taken over (nothing is changed for its files)");
                        if (Blocked == null) Blocked = Display(source) + " of an earlier build cannot be read";
                        continue;
                    }
                    if (state.Records.Count > 0)
                    {
                        HdInstaller.Guard(StatePath);
                        AtomicFile.WriteAllBytes(StatePath, raw);
                        haveState = true;
                        log.Info("hd: took over " + Display(source) + " (" + state.Records.Count + " files) into " + Display(Work) +
                                 "; its backups are used where they are");
                    }
                    Note(key, dir);
                }
            }
            catch (Exception ex)
            {
                log.Warn("hd: taking over the state of an earlier start failed", ex);
                if (!File.Exists(StatePath) && Blocked == null) Blocked = "taking over the state of an earlier build failed (" + Reason(ex) + ")";
            }
        }

        private HashSet<string> ImportedKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string dir in new[] { Primary, Fallback })
            {
                try
                {
                    string text = AtomicFile.ReadAllTextOrNull(Path.Combine(dir, ImportedFile));
                    if (text == null) continue;
                    foreach (string line in text.Split('\n'))
                    {
                        string l = line.TrimEnd('\r');
                        int space = l.IndexOf(' ');
                        int second = space < 0 ? -1 : l.IndexOf(' ', space + 1);
                        if (second > 0) keys.Add(l.Substring(0, second));
                    }
                }
                catch (Exception)
                {
                }
            }
            return keys;
        }

        private void Note(string key, string folder)
        {
            string path = Path.Combine(Work, ImportedFile);
            string text = AtomicFile.ReadAllTextOrNull(path) ?? "";
            if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal)) text += "\n";
            HdInstaller.Guard(path);
            AtomicFile.WriteAllText(path, text + key + " " + folder + "\n");
        }

        private static string Key(byte[] raw)
        {
            return HdPack.Hex(Install.Crc32.Update(0, raw, 0, raw.Length)) + " " + raw.Length.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The installed.json of the work folder, else (when no folder can be written) the first readable one of an earlier start.</summary>
        public string ReadableStatePath()
        {
            if (Work != null) return StatePath;
            foreach (string dir in Others)
            {
                string path = Path.Combine(dir, HdInstaller.StateFile);
                if (File.Exists(path)) return path;
            }
            return Path.Combine(Primary, HdInstaller.StateFile);
        }
    }
}
