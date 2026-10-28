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
        /// Picks the work folder: the one of McvsOffline/hd-install and its internal-storage twin that holds the current
        /// installed.json, else McvsOffline/hd-install first; each is created when missing and must take a probe file. When both
        /// hold one, the twin is current only when it took over McvsOffline/hd-install's (its imported.txt lists that file as it
        /// is now): McvsOffline/hd-install keeps the copy it had when it could not be written. Never throws.
        /// </summary>
        public void Choose()
        {
            bool fallbackState = File.Exists(Path.Combine(Fallback, HdInstaller.StateFile));
            bool primaryState = File.Exists(Path.Combine(Primary, HdInstaller.StateFile));
            bool fallbackFirst = fallbackState && (!primaryState || TookOver(Fallback, Primary));
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
            // The twin went first because it took over McvsOffline/hd-install's state: that folder (it exists) is probed too, so
            // Status still says why it was left.
            if (Work == Fallback && first == Fallback && primaryState) primaryProblem = ProbeFolder(Primary);
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

        /// <summary>
        /// For the "may be deleted" note once nothing is installed: build 11's backup folder in the drop folder when it still
        /// holds files and its installed.json is gone or was taken over (a state not taken over may know originals no other
        /// folder has). Our own folders are never named: <see cref="CleanTwin"/> empties the other one itself.
        /// </summary>
        public List<string> OldBackupFolders()
        {
            var found = new List<string>();
            if (Drop == Work) return found;
            string backups = Path.Combine(Drop, HdInstaller.BackupFolder);
            try
            {
                if (!Directory.Exists(backups) || Directory.GetFiles(backups, "*", SearchOption.AllDirectories).Length == 0) return found;
                byte[] raw = ReadStateOrNull(Drop);
                if (raw == null || ImportedKeys().Contains(Key(raw))) found.Add(backups);
            }
            catch (Exception)
            {
            }
            return found;
        }

        /// <summary>The other of McvsOffline/hd-install and its internal-storage twin (null when no folder is used).</summary>
        public string Twin { get { return Work == null ? null : Work == Primary ? Fallback : Primary; } }

        /// <summary>
        /// Once nothing is installed: the other hd-install folder's installed.json (when it was taken over) and its backups are
        /// no longer needed by any record (every record of it went into ours and has been given back), so they are deleted.
        /// Both folders are the app's own (never the drop folder); a folder that cannot be written is left as it is. Never throws.
        /// </summary>
        public void CleanTwin(Log log)
        {
            string twin = Twin;
            if (twin == null || !Directory.Exists(twin)) return;
            try
            {
                string state = Path.Combine(twin, HdInstaller.StateFile);
                byte[] raw = ReadStateOrNull(twin);
                if (raw != null)
                {
                    if (!ImportedKeys().Contains(Key(raw))) return;
                    HdInstaller.Guard(state);
                    File.Delete(state);
                }
                string backups = Path.Combine(twin, HdInstaller.BackupFolder);
                if (!Directory.Exists(backups)) return;
                int deleted = 0;
                foreach (string file in Directory.GetFiles(backups, "*", SearchOption.AllDirectories))
                {
                    HdInstaller.Guard(file);
                    File.Delete(file);
                    deleted++;
                }
                HdInstaller.DeleteEmptyDirs(backups);
                if (deleted > 0 || raw != null)
                    log.Info("hd: " + Display(twin) + ": " + (raw != null ? HdInstaller.StateFile + " taken over earlier and " : "") + deleted +
                             " backups no longer needed deleted");
            }
            catch (Exception ex)
            {
                log.Info("hd: " + Display(twin) + " of an earlier start is left as it is (" + Reason(ex) + ")");
            }
        }

        /// <summary>
        /// Takes over the installed.json of another folder that imported.txt does not list yet (checked with
        /// <see cref="HdState.Load"/>), then notes it in imported.txt (CRC-32, size, folder) so it is taken over once:
        /// <list type="bullet">
        /// <item>our folder has none: it is copied byte for byte (atomically);</item>
        /// <item>it took over ours as ours is now (its imported.txt lists our installed.json: that folder was in use while ours
        /// could not be written): it is the newer one and replaces ours byte for byte;</item>
        /// <item>else its records for files ours does not list are added to ours (ours wins for a file both list), so no
        /// record, and no original only it knows, is dropped.</item>
        /// </list>
        /// Its backups stay where they are. An installed.json that cannot be read is left for a later start (logged; with no state
        /// of ours, <see cref="Blocked"/>). Never throws.
        /// </summary>
        public void Migrate(Log log)
        {
            Blocked = null;
            if (Work == null) return;
            try
            {
                HashSet<string> imported = ImportedKeys();
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
                        if (!File.Exists(StatePath) && Blocked == null) Blocked = Display(source) + " of an earlier build cannot be read";
                        continue;
                    }
                    string key = Key(raw);
                    if (imported.Contains(key)) continue;
                    byte[] own = ReadStateOrNull(Work);
                    HdState state;
                    try
                    {
                        state = HdState.Load(source);
                    }
                    catch (FormatException ex)
                    {
                        log.Warn("hd: " + ex.Message + ": it is not taken over (nothing is changed for its files)");
                        if (own == null && Blocked == null) Blocked = Display(source) + " of an earlier build cannot be read";
                        continue;
                    }
                    if (own == null || ImportedKeysOf(dir).Contains(Key(own)))
                    {
                        if (state.Records.Count > 0)
                        {
                            HdInstaller.Guard(StatePath);
                            AtomicFile.WriteAllBytes(StatePath, raw);
                            log.Info("hd: took over " + Display(source) + " (" + state.Records.Count + " files) into " + Display(Work) +
                                     (own != null ? ", in place of the older " + HdInstaller.StateFile + " there" : "") + "; its backups are used where they are");
                        }
                    }
                    else
                    {
                        HdState ours;
                        try
                        {
                            ours = HdState.Load(StatePath);
                        }
                        catch (FormatException)
                        {
                            // Ours cannot be read: the run reports it and changes nothing; this one is tried again later.
                            log.Warn("hd: " + Display(source) + " of an earlier start is not taken over now: " + Display(StatePath) + " cannot be read");
                            continue;
                        }
                        int added = 0;
                        foreach (HdRecord r in state.Records)
                        {
                            if (ours.Find(r.Path) != null) continue;
                            ours.Put(r.Clone());
                            added++;
                        }
                        if (added > 0)
                        {
                            if (ours.PackFile == null && ours.PackName == null)
                            {
                                ours.PackFile = state.PackFile;
                                ours.PackId = state.PackId;
                                ours.PackName = state.PackName;
                            }
                            HdInstaller.Guard(StatePath);
                            ours.Save(StatePath);
                        }
                        log.Info("hd: " + Display(source) + " of an earlier start: " + added + " of its " + state.Records.Count + " files added to " +
                                 Display(StatePath) + " (the others are listed there already); its backups are used where they are");
                    }
                    Note(key, dir);
                    imported.Add(key);
                }
            }
            catch (Exception ex)
            {
                log.Warn("hd: taking over the state of an earlier start failed", ex);
                if (!File.Exists(StatePath) && Blocked == null) Blocked = "taking over the state of an earlier build failed (" + Reason(ex) + ")";
            }
        }

        /// <summary><paramref name="dir"/>'s imported.txt lists <paramref name="other"/>'s installed.json as it is now (it took that one over).</summary>
        private bool TookOver(string dir, string other)
        {
            try
            {
                byte[] raw = ReadStateOrNull(other);
                return raw != null && ImportedKeysOf(dir).Contains(Key(raw));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static byte[] ReadStateOrNull(string dir)
        {
            string path = Path.Combine(dir, HdInstaller.StateFile);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        private HashSet<string> ImportedKeys()
        {
            var keys = ImportedKeysOf(Primary);
            keys.UnionWith(ImportedKeysOf(Fallback));
            return keys;
        }

        /// <summary>The "&lt;crc32&gt; &lt;size&gt;" keys in <paramref name="folder"/>'s imported.txt (empty when it has none).</summary>
        private static HashSet<string> ImportedKeysOf(string folder)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                string text = AtomicFile.ReadAllTextOrNull(Path.Combine(folder, ImportedFile));
                if (text == null) return keys;
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
