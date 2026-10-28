// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
// Design: docs/design/hd-textures.md; why route A: docs/research/pc-textures.md §5.3 and docs/STATUS.md (build 10 device test).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using McvsOffline.Embedded.Install;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Embedded.Hd
{
    /// <summary>settings.json <c>hd.enabled</c> and <c>install.manageInstaller</c>, and the start-up time budget.</summary>
    public sealed class HdInstallerOptions
    {
        public HdInstallerOptions()
        {
            Enabled = true;
            ManageInstaller = true;
            BudgetMs = DefaultBudgetMs;
        }

        /// <summary>The default for <see cref="BudgetMs"/>.</summary>
        public const int DefaultBudgetMs = 8000;

        /// <summary>hd.enabled: install the pack; false removes an installed one.</summary>
        public bool Enabled { get; set; }

        /// <summary>install.manageInstaller: false leaves the installer's state, and so the HD files, alone.</summary>
        public bool ManageInstaller { get; set; }

        /// <summary>
        /// Installing and removing run on the game's main thread before the installer starts: no new group is installed or given
        /// back after this many milliseconds (at least one always is); the rest follows at the next start.
        /// </summary>
        public int BudgetMs { get; set; }

        /// <summary>
        /// Tests: called at "recorded" (a batch is in installed.json, no file changed yet), "written:&lt;path&gt;" (one HD file in
        /// place), "restored:&lt;path&gt;" and "commit" (before the indexes are written); an exception thrown here ends the run
        /// like a crash at that point (nothing after it happens).
        /// </summary>
        internal Action<string> Checkpoint { get; set; }

        internal void Reached(string point)
        {
            Action<string> hook = Checkpoint;
            if (hook != null) hook(point);
        }
    }

    /// <summary>What <see cref="HdInstaller.Prepare"/> found, for <see cref="InstallerDataOptions.HdOriginalSizes"/>.</summary>
    public sealed class HdPreparation
    {
        internal HdPreparation(IDictionary<string, long> originals)
        {
            OriginalSizes = originals;
        }

        /// <summary>Data/ files that hold an HD file now: relative path -&gt; size of the original. Null when none.</summary>
        public IDictionary<string, long> OriginalSizes { get; private set; }
    }

    /// <summary>What <see cref="HdInstaller.Apply"/> did.</summary>
    public sealed class HdResult
    {
        internal HdResult(string summary, bool indexChanged, bool installed)
        {
            Summary = summary;
            IndexChanged = indexChanged;
            Installed = installed;
        }

        /// <summary>One line for the boot trace and Status, starting "hd textures: ".</summary>
        public string Summary { get; private set; }

        /// <summary>The installer's index was rewritten (a Gameloft index to CRC-check must be read again).</summary>
        public bool IndexChanged { get; private set; }

        /// <summary>Some Data/ files hold HD textures now.</summary>
        public bool Installed { get; private set; }
    }

    /// <summary>
    /// Installs an HD texture pack into the game's own data (docs/research/pc-textures.md route A, docs/design/hd-textures.md):
    /// each <c>Data/&lt;h&gt;/&lt;rest&gt;.f</c> of the pack replaces the original file in place, and the Java installer's index
    /// (<c>getFilesDir()/pack.info</c>, our saved copy <c>McvsOffline/install/pack.info</c>) and its journal of split pieces
    /// (<c>d_o_w_n_l_o_a_d_e_d.txt</c>) are rewritten to list the new sizes and CRCs, so the installer's size check at every start
    /// accepts the files without <c>SKIP_VALIDATION</c>. Both steps run in Bootstrap, before the installer:
    /// <list type="number">
    /// <item><see cref="Prepare"/>, before <see cref="InstallerData.Prepare"/>: makes both index copies describe what Data/ holds
    /// for every file the HD installer has taken over (a crash between a file and its index entry, or an index the game or a
    /// reinstall reset), and tells InstallerData the original sizes, so it still recognises the 1.17.32 pack it rebuilds or
    /// restores an index for;</item>
    /// <item><see cref="Apply"/>, after it: removes (hd.enabled false, files the pack no longer has) and installs (the pack, group
    /// by group within <see cref="HdInstallerOptions.BudgetMs"/>), then writes both index copies, the journal and the state.</item>
    /// </list>
    /// Every original is checked against the pack's size and CRC-32 and copied to <c>McvsOffline/hd/backup/</c> before it is
    /// replaced; every file is written to a temporary file next to it, checked and renamed over it, so Data/ never holds half a
    /// file. <c>McvsOffline/hd/installed.json</c> names the files taken over before any of them changes; what each file holds is
    /// always read from the file itself, so a run cut short anywhere is completed or undone by the next one. When the installer
    /// has no usable index after InstallerData (it then goes online and would replace every file of another size), the originals
    /// are put back and the pack is installed again once there is one. Never throws.
    /// </summary>
    public static class HdInstaller
    {
        public const string FolderName = "hd";
        public const string StateFile = "installed.json";
        public const string BackupFolder = "backup";
        public const string TempSuffix = ".mcvs-hd-tmp";

        /// <summary>A batch of groups is recorded in installed.json, then written, after this many files or bytes.</summary>
        internal const int BatchFiles = 64;
        internal const long BatchBytes = 64L << 20;

        private static volatile string status = "hd textures: not checked (the host did not run the installer step)";
        private static volatile bool installed;

        /// <summary>One line for Status › Game host and the status line.</summary>
        public static string Status { get { return status; } }

        /// <summary>Some Data/ files held HD textures at the last start (the settings page offers to remove them).</summary>
        public static bool Installed { get { return installed; } }

        /// <summary>&lt;game root&gt;/McvsOffline/hd: where the player puts the pack, and our state and backups.</summary>
        public static string HdDir(InstallerDataLocations loc)
        {
            return Path.Combine(Path.Combine(loc.GameRoot, EmbeddedPaths.DataFolderName), FolderName);
        }

        internal static string StatePath(InstallerDataLocations loc)
        {
            return Path.Combine(HdDir(loc), StateFile);
        }

        internal static string BackupPath(InstallerDataLocations loc, string relativePath)
        {
            return Path.Combine(Path.Combine(HdDir(loc), BackupFolder), relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        internal static string DataPath(InstallerDataLocations loc, string relativePath)
        {
            return Path.Combine(loc.GameRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>Tests: back to the state before the first run.</summary>
        internal static void ResetForTests()
        {
            status = "hd textures: not checked (the host did not run the installer step)";
            installed = false;
        }

        // ------------------------------------------------------------------ step 1: before InstallerData

        /// <summary>Step 1, before <see cref="InstallerData.Prepare"/>. Never throws.</summary>
        public static HdPreparation Prepare(InstallerDataLocations loc, HdInstallerOptions options, Log log)
        {
            options = options ?? new HdInstallerOptions();
            try
            {
                if (!options.ManageInstaller) return new HdPreparation(null);
                HdState state = HdState.Load(StatePath(loc));
                DeleteTemps(loc, state, log);
                if (state.Records.Count == 0) return new HdPreparation(null);
                var contents = new Dictionary<string, Content>(StringComparer.Ordinal);
                var originals = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (HdRecord r in state.Records)
                {
                    Content c = Classify(DataPath(loc, r.Path), r);
                    contents[r.Path] = c;
                    if (c == Content.Hd) originals[r.Path] = r.OriginalSize;
                }
                var session = new IndexSession(loc, log);
                int fixedInstaller = session.Installer == null ? 0 : session.Reconcile(session.Installer, state.Records, contents);
                int fixedSaved = session.Saved == null ? 0 : session.Reconcile(session.Saved, state.Records, contents);
                session.EnsureJournal(state.Records, contents);
                session.Commit();
                if (fixedInstaller > 0)
                    log.Warn("hd: the installer's index listed " + fixedInstaller + " of the HD pack's files with other sizes (a reset index, or a start " +
                             "cut short): their entries were made to match the files again");
                if (fixedSaved > 0) log.Info("hd: the saved copy of the index was made to match " + fixedSaved + " HD files");
                if (session.Installer == null)
                    log.Info("hd: the installer has no usable index right now (" + session.InstallerProblem + "); the original sizes of " + originals.Count +
                             " HD files are given to the installer-data step");
                return new HdPreparation(originals.Count == 0 ? null : originals);
            }
            catch (Exception ex)
            {
                log.Warn("hd: checking the installed HD files failed", ex);
                return new HdPreparation(null);
            }
        }

        // ------------------------------------------------------------------ step 2: after InstallerData

        /// <summary>Step 2, after <see cref="InstallerData.Prepare"/>. Never throws.</summary>
        public static HdResult Apply(InstallerDataLocations loc, HdInstallerOptions options, Log log)
        {
            options = options ?? new HdInstallerOptions();
            HdResult result;
            try
            {
                result = new Run(loc, options, log).Execute();
            }
            catch (Exception ex)
            {
                log.Warn("hd: the HD texture step failed; Data/ is left as it is", ex);
                result = new HdResult("hd textures: failed (" + ex.GetType().Name + ": " + ex.Message + ")", false, installed);
            }
            status = result.Summary;
            installed = result.Installed;
            return result;
        }

        /// <summary>What a Data/ file holds, compared with a record.</summary>
        internal enum Content
        {
            Missing,
            Original,
            Hd,
            Other,
        }

        /// <summary>By size; by CRC-32 only when the original and the HD file have the same size.</summary>
        internal static Content Classify(string path, HdRecord r)
        {
            long length = Length(path);
            if (length < 0) return Content.Missing;
            bool orig = length == r.OriginalSize, hd = length == r.HdSize;
            if (orig && !hd) return Content.Original;
            if (hd && !orig) return Content.Hd;
            if (!orig) return Content.Other;
            uint crc = FileCrc(path);
            if (crc == r.HdCrc) return Content.Hd;
            if (crc == r.OriginalCrc) return Content.Original;
            return Content.Other;
        }

        // ------------------------------------------------------------------ the run of step 2

        private sealed class Plan
        {
            public HdPackFile File;
            public HdRecord Record;

            /// <summary>The original is already in the backup folder (checked), so Data/ need not hold it.</summary>
            public bool BackedUp;
        }

        private sealed class Run
        {
            private readonly InstallerDataLocations loc;
            private readonly HdInstallerOptions options;
            private readonly Log log;
            private readonly int started = Environment.TickCount;
            private HdState state;
            private IndexSession session;
            private readonly Dictionary<string, Content> contents = new Dictionary<string, Content>(StringComparer.Ordinal);
            private readonly List<string> notes = new List<string>();
            private int restored, written, skipped, deferred, deferredOriginals, failed;
            private int matchedGroups, skippedGroups;
            private bool stopInstalling, abandoned, skipNoted;

            /// <summary>When this many groups in a row do not match, before any did, the pack is not for this phone's data.</summary>
            internal const int GiveUpAfterGroups = 20;

            public Run(InstallerDataLocations loc, HdInstallerOptions options, Log log)
            {
                this.loc = loc;
                this.options = options;
                this.log = log;
            }

            public HdResult Execute()
            {
                string hdDir = HdDir(loc);
                if (!options.ManageInstaller)
                {
                    log.Info("hd: install.manageInstaller is false: HD textures are neither installed nor removed");
                    return new HdResult("hd textures: not managed (install.manageInstaller false)", false, false);
                }
                try
                {
                    state = HdState.Load(StatePath(loc));
                }
                catch (FormatException ex)
                {
                    log.Warn("hd: " + ex.Message + ": HD textures are left exactly as they are (the originals are in " +
                             Path.Combine(hdDir, BackupFolder) + ")");
                    return new HdResult("hd textures: " + StateFile + " cannot be read; nothing changed", false, false);
                }
                foreach (HdRecord r in state.Records) contents[r.Path] = Classify(DataPath(loc, r.Path), r);

                HdPack pack = null;
                string packProblem = null;
                List<string> candidates = HdPack.Candidates(hdDir);
                if (options.Enabled && candidates.Count > 0)
                {
                    string chosen = candidates[candidates.Count - 1];
                    if (candidates.Count > 1)
                        notes.Add((candidates.Count - 1) + " other mcvs-hd-*.zip ignored (only the last by name, " + Path.GetFileName(chosen) + ", is used)");
                    try
                    {
                        pack = HdPack.Open(chosen);
                    }
                    catch (Exception ex)
                    {
                        packProblem = Path.GetFileName(chosen) + " cannot be used: " + ex.Message;
                        log.Warn("hd: " + packProblem);
                    }
                }

                session = new IndexSession(loc, log);
                if (session.Installer == null && state.Records.Count == 0 && pack == null) return Finish(null, null, packProblem, candidates.Count);
                if (session.Installer == null)
                {
                    PutOriginalsBack();
                    string back = deferredOriginals == 0
                        ? "the originals are back"
                        : Count(restored, "original is", "originals are") + " back, " + Count(deferredOriginals, "more follows", "more follow") + " at the next start";
                    return Finish("hd textures: not installed now: the game's installer has no usable index (" + session.InstallerProblem +
                                  "), so it checks the data online and would replace every HD file; " + back + " and the pack is installed " +
                                  "again once the installer has an index", null, null, candidates.Count);
                }

                if (pack != null && pack.Variant != null)
                {
                    string variant = DataVariant();
                    if (variant != null && variant != pack.Variant)
                    {
                        packProblem = pack.FileName + " was made for the " + pack.Variant + " data pack, this phone has the " + variant + " pack";
                        log.Warn("hd: " + packProblem + ": not installed");
                        pack = null;
                    }
                }

                var wanted = new Dictionary<string, HdPackFile>(StringComparer.Ordinal);
                if (pack != null) foreach (HdPackFile f in pack.Files) wanted[f.Path] = f;

                // 1. Files to give back: everything when hd.enabled is false; files the pack no longer has; with no usable pack, files
                //    whose HD copy is gone (an HD file can only be written again from the pack). Group by group within the budget.
                var giveBack = new List<HdRecord>();
                foreach (HdRecord r in state.Records)
                {
                    bool remove;
                    if (!options.Enabled) remove = true;
                    else if (pack != null) remove = !wanted.ContainsKey(r.Path);
                    else remove = contents[r.Path] != Content.Hd;
                    if (remove) giveBack.Add(r);
                }
                foreach (List<HdRecord> group in Groups(giveBack))
                {
                    if (WithinBudget(group)) foreach (HdRecord r in group) RestoreOriginal(r);
                }

                // 2. The pack, group by group (after every removal is done: a start that ran out of time removing installs nothing).
                if (pack != null)
                {
                    if (deferredOriginals > 0) stopInstalling = true;
                    Install(pack);
                }

                return Finish(null, pack, packProblem, candidates.Count);
            }

            private string DataVariant()
            {
                var originals = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (HdRecord r in state.Records)
                {
                    if (contents[r.Path] == Content.Hd) originals[r.Path] = r.OriginalSize;
                }
                PackIndex index = session.Installer.Index;
                return InstallerData.VariantOf(index.LogicalFiles, InstallerData.PackBytes(index, originals));
            }

            /// <summary>
            /// The installer has no index: Data/ gets every original back (records stay, so the pack returns later), group by group
            /// within the budget; the rest at the next start (Prepare makes the index match whatever Data/ holds by then).
            /// </summary>
            private void PutOriginalsBack()
            {
                foreach (List<HdRecord> group in Groups(state.Records))
                {
                    if (!WithinBudget(group)) continue;
                    foreach (HdRecord r in group)
                    {
                        if (contents[r.Path] == Content.Original) continue;
                        if (CopyBackup(r)) restored++;
                    }
                }
                if (restored > 0) log.Warn("hd: the installer has no usable index (" + session.InstallerProblem + "): " + restored + " original files put back in Data/");
                if (deferredOriginals > 0) log.Warn("hd: " + deferredOriginals + " more originals are put back at the next start (start-up time budget)");
            }

            /// <summary>Records by group, in the order of their first file (a level's lightmaps are given back together).</summary>
            private static List<List<HdRecord>> Groups(IEnumerable<HdRecord> records)
            {
                var groups = new List<List<HdRecord>>();
                var byGroup = new Dictionary<string, List<HdRecord>>(StringComparer.Ordinal);
                foreach (HdRecord r in records)
                {
                    string key = r.Group ?? r.Path;
                    List<HdRecord> g;
                    if (!byGroup.TryGetValue(key, out g))
                    {
                        byGroup[key] = g = new List<HdRecord>();
                        groups.Add(g);
                    }
                    g.Add(r);
                }
                return groups;
            }

            /// <summary>
            /// False when the budget is spent and an original was already copied back in this start: the group's records stay as
            /// they are (their HD files and index entries agree) and are counted for "at the next start".
            /// </summary>
            private bool WithinBudget(List<HdRecord> group)
            {
                if (deferredOriginals == 0 && (restored == 0 || Environment.TickCount - started <= options.BudgetMs)) return true;
                foreach (HdRecord r in group)
                {
                    if (contents[r.Path] != Content.Original) deferredOriginals++;
                }
                return false;
            }

            /// <summary>Gives a record's original back (Data/ and both indexes); the record goes at commit time.</summary>
            private void RestoreOriginal(HdRecord r)
            {
                if (contents[r.Path] != Content.Original)
                {
                    if (!CopyBackup(r))
                    {
                        failed++;
                        return;
                    }
                    restored++;
                    options.Reached("restored:" + r.Path);
                }
                dropped.Add(r.Path);
            }

            private readonly List<string> dropped = new List<string>();

            /// <summary>Copies the backup of <paramref name="r"/> over its Data/ file (checked against the original's CRC-32).</summary>
            private bool CopyBackup(HdRecord r)
            {
                string backup = BackupPath(loc, r.Path);
                if (Length(backup) != r.OriginalSize)
                {
                    log.Warn("hd: the backup of " + r.Path + " is missing or has another size (" + backup + "): the HD file stays");
                    return false;
                }
                try
                {
                    uint[] pieces;
                    CopyChecked(backup, DataPath(loc, r.Path), r.OriginalSize, r.OriginalCrc, out pieces);
                    r.OriginalPieceCrcs = pieces;
                    contents[r.Path] = Content.Original;
                    return true;
                }
                catch (Exception ex)
                {
                    log.Warn("hd: cannot put the original of " + r.Path + " back", ex);
                    return false;
                }
            }

            private void Install(HdPack pack)
            {
                var groups = new List<List<HdPackFile>>();
                var byGroup = new Dictionary<string, List<HdPackFile>>(StringComparer.Ordinal);
                foreach (HdPackFile f in pack.Files)
                {
                    List<HdPackFile> g;
                    if (!byGroup.TryGetValue(f.Group, out g))
                    {
                        byGroup[f.Group] = g = new List<HdPackFile>();
                        groups.Add(g);
                    }
                    g.Add(f);
                }
                state.PackFile = pack.FileName;
                state.PackId = pack.Id;
                state.PackName = pack.Name;
                var batch = new List<List<Plan>>();
                int batchFiles = 0;
                long batchBytes = 0;
                foreach (List<HdPackFile> group in groups)
                {
                    if (abandoned)
                    {
                        skipped += Pending(group);
                        continue;
                    }
                    if (stopInstalling)
                    {
                        deferred += Pending(group);
                        continue;
                    }
                    if ((written > 0 || batchFiles > 0 || restored > 0) && Environment.TickCount - started > options.BudgetMs)
                    {
                        WriteBatch(batch);
                        batch.Clear();
                        batchFiles = 0;
                        batchBytes = 0;
                        stopInstalling = true;
                        deferred += Pending(group);
                        continue;
                    }
                    List<Plan> plans = PlanGroup(group);
                    if (plans == null)
                    {
                        if (stopInstalling) continue;
                        if (++skippedGroups >= GiveUpAfterGroups && matchedGroups == 0)
                        {
                            abandoned = true;
                            notes.Add("the first " + skippedGroups + " files differ from the originals the pack was made from, so it does not fit " +
                                      "this phone's data (another texture variant or game version?)");
                            log.Warn("hd: " + pack.FileName + ": the first " + skippedGroups + " files differ from the originals it was made from; " +
                                     "the rest is not tried");
                        }
                        continue;
                    }
                    matchedGroups++;
                    if (plans.Count == 0) continue;
                    batch.Add(plans);
                    foreach (Plan p in plans)
                    {
                        batchFiles++;
                        batchBytes += p.File.HdSize;
                    }
                    if (batchFiles >= BatchFiles || batchBytes >= BatchBytes)
                    {
                        WriteBatch(batch);
                        batch.Clear();
                        batchFiles = 0;
                        batchBytes = 0;
                    }
                }
                WriteBatch(batch);
            }

            /// <summary>Files of the group that are not installed yet (for the "left for the next start" count).</summary>
            private int Pending(List<HdPackFile> group)
            {
                int n = 0;
                foreach (HdPackFile f in group)
                {
                    HdRecord r = state.Find(f.Path);
                    if (r == null || !SameHd(r, f) || contents[f.Path] != Content.Hd) n++;
                }
                return n;
            }

            private static bool SameHd(HdRecord r, HdPackFile f)
            {
                return r.HdSize == f.HdSize && r.HdCrc == f.HdCrc && r.OriginalSize == f.OriginalSize && r.OriginalCrc == f.OriginalCrc;
            }

            /// <summary>
            /// What to do for each file of a group: nothing when it is installed, otherwise its original must be checked and backed
            /// up first. One file that cannot be done skips the whole group (all lightmaps of a level, or none). Null: skipped.
            /// </summary>
            private List<Plan> PlanGroup(List<HdPackFile> group)
            {
                var plans = new List<Plan>();
                foreach (HdPackFile f in group)
                {
                    HdRecord r = state.Find(f.Path);
                    string dataPath = DataPath(loc, f.Path);
                    if (r != null && SameHd(r, f))
                    {
                        Content c;
                        if (!contents.TryGetValue(f.Path, out c)) contents[f.Path] = c = Classify(dataPath, r);
                        if (c == Content.Hd) continue;
                    }
                    if (r != null && (r.OriginalSize != f.OriginalSize || r.OriginalCrc != f.OriginalCrc))
                    {
                        Skip(group, f.Path + ": the pack was made from another original than the one backed up");
                        return null;
                    }
                    var plan = new Plan { File = f, Record = r };
                    string backup = BackupPath(loc, f.Path);
                    if (Length(backup) == f.OriginalSize && FileCrc(backup) == f.OriginalCrc)
                    {
                        plan.BackedUp = true;
                    }
                    else
                    {
                        long length = Length(dataPath);
                        if (length != f.OriginalSize)
                        {
                            Skip(group, f.Path + (length < 0 ? " is missing" : " has " + length + " bytes, not the " + f.OriginalSize + " of the original the pack was made from"));
                            return null;
                        }
                        try
                        {
                            uint[] pieces;
                            CopyChecked(dataPath, backup, f.OriginalSize, f.OriginalCrc, out pieces);
                            plan.BackedUp = true;
                        }
                        catch (HdZipException ex)
                        {
                            Skip(group, f.Path + " " + ex.Message);
                            return null;
                        }
                        catch (Exception ex)
                        {
                            log.Warn("hd: cannot back up " + f.Path + " to " + backup, ex);
                            failed += group.Count;
                            stopInstalling = true;
                            return null;
                        }
                    }
                    plans.Add(plan);
                }
                return plans;
            }

            private void Skip(List<HdPackFile> group, string reason)
            {
                skipped += group.Count;
                string text = group.Count > 1 ? reason + " (its group '" + group[0].Group + "' of " + group.Count + " files is skipped)" : reason;
                if (skipped <= 20) log.Warn("hd: not installed: " + text);
                if (!skipNoted)
                {
                    skipNoted = true;
                    notes.Add("first: " + text);
                }
            }

            /// <summary>Records the batch in installed.json (before any file of it changes), then writes the HD files group by group.</summary>
            private void WriteBatch(List<List<Plan>> batch)
            {
                if (batch.Count == 0) return;
                foreach (List<Plan> plans in batch)
                {
                    foreach (Plan p in plans)
                    {
                        HdRecord r = p.Record == null ? new HdRecord() : p.Record.Clone();
                        r.Path = p.File.Path;
                        r.Group = p.File.Group;
                        if (p.Record == null || r.HdSize != p.File.HdSize || r.HdCrc != p.File.HdCrc) r.HdPieceCrcs = null;
                        r.OriginalSize = p.File.OriginalSize;
                        r.OriginalCrc = p.File.OriginalCrc;
                        r.HdSize = p.File.HdSize;
                        r.HdCrc = p.File.HdCrc;
                        if (r.OriginalZip == null) r.OriginalZip = session.ZipRefs(r.Path, r.OriginalSize);
                        if (r.OriginalPieceCrcs == null) r.OriginalPieceCrcs = PieceCrcsOf(BackupPath(loc, r.Path), r.OriginalSize);
                        state.Put(r);
                        p.Record = r;
                        // Against the record as it is now (a file of an older pack version is "other" until it is written).
                        contents[r.Path] = Classify(DataPath(loc, r.Path), r);
                    }
                }
                state.Save(StatePath(loc));
                options.Reached("recorded");
                foreach (List<Plan> plans in batch)
                {
                    if (stopInstalling)
                    {
                        deferred += plans.Count;
                        continue;
                    }
                    var done = new List<Plan>();
                    foreach (Plan p in plans)
                    {
                        try
                        {
                            uint[] pieces = null;
                            string target = DataPath(loc, p.File.Path);
                            WriteFromPack(p.File, target, out pieces);
                            p.Record.HdPieceCrcs = pieces;
                            contents[p.File.Path] = Content.Hd;
                            done.Add(p);
                            written++;
                        }
                        catch (Exception ex)
                        {
                            log.Warn("hd: cannot write " + p.File.Path + " (the rest of the pack waits for the next start)", ex);
                            // All of a group or nothing: the files of this group written so far get their originals back.
                            foreach (Plan d in done)
                            {
                                if (CopyBackup(d.Record)) written--;
                            }
                            failed += plans.Count;
                            stopInstalling = true;
                            break;
                        }
                        options.Reached("written:" + p.File.Path);
                    }
                }
            }

            private HdResult Finish(string fixedSummary, HdPack pack, string packProblem, int candidates)
            {
                // Both index copies and the journal describe what Data/ holds now, then the state follows.
                options.Reached("commit");
                int changedInstaller = session.Installer == null ? 0 : session.Reconcile(session.Installer, state.Records, contents);
                if (session.Saved != null) session.Reconcile(session.Saved, state.Records, contents);
                if (session.Installer != null) session.EnsureJournal(state.Records, contents);
                session.Commit();
                foreach (string path in dropped)
                {
                    state.Remove(path);
                    string backup = BackupPath(loc, path);
                    try
                    {
                        if (File.Exists(backup)) File.Delete(backup);
                    }
                    catch (Exception ex)
                    {
                        log.Warn("hd: cannot delete the backup " + backup, ex);
                    }
                }
                if (state.Records.Count == 0)
                {
                    state.PackFile = null;
                    state.PackId = null;
                    state.PackName = null;
                }
                state.Save(StatePath(loc));
                if (state.Records.Count == 0) DeleteEmptyDirs(Path.Combine(HdDir(loc), BackupFolder));

                int hdFiles = 0;
                long hdBytes = 0;
                foreach (HdRecord r in state.Records)
                {
                    if (contents[r.Path] != Content.Hd) continue;
                    hdFiles++;
                    hdBytes += r.HdSize;
                }
                string summary;
                if (fixedSummary != null)
                {
                    summary = fixedSummary;
                }
                else
                {
                    var sb = new StringBuilder("hd textures: ");
                    if (hdFiles > 0)
                    {
                        sb.Append(state.PackName ?? state.PackFile ?? "HD pack").Append(", ").Append(Count(hdFiles, "file", "files"))
                          .Append(" installed (").Append(Mib(hdBytes)).Append(')');
                    }
                    else if (!options.Enabled)
                    {
                        sb.Append("off (settings hd.enabled false)");
                    }
                    else if (packProblem != null)
                    {
                        sb.Append("none installed; ").Append(packProblem);
                    }
                    else if (pack == null && candidates == 0)
                    {
                        sb.Append("none (no McvsOffline/").Append(FolderName).Append('/').Append(HdPack.FilePrefix).Append("*.zip)");
                    }
                    else
                    {
                        sb.Append("none installed");
                    }
                    if (hdFiles > 0 && packProblem != null) sb.Append("; ").Append(packProblem);
                    if (hdFiles > 0 && pack == null && packProblem == null && options.Enabled)
                        sb.Append("; the pack file is no longer in McvsOffline/").Append(FolderName).Append(" (keep it there: a lost HD file can only be written again from it)");
                    if (written > 0) sb.Append("; ").Append(written).Append(" written now");
                    if (restored > 0) sb.Append("; ").Append(Count(restored, "original", "originals")).Append(" put back");
                    if (deferredOriginals > 0) sb.Append("; ").Append(Count(deferredOriginals, "original", "originals")).Append(" at the next start");
                    if (deferred > 0) sb.Append("; ").Append(Count(deferred, "file", "files")).Append(" at the next start");
                    if (skipped > 0) sb.Append("; ").Append(skipped).Append(" skipped");
                    if (failed > 0) sb.Append("; ").Append(failed).Append(" failed (see the log)");
                    if (changedInstaller > 0 && written == 0 && restored == 0) sb.Append("; index entries of ").Append(Count(changedInstaller, "file", "files")).Append(" corrected");
                    foreach (string note in notes) sb.Append("; ").Append(note);
                    summary = sb.ToString();
                }
                int seconds = Math.Max(0, (Environment.TickCount - started) / 1000);
                if (written > 0 || restored > 0) log.Info("hd: " + written + " HD files written, " + restored + " originals put back in " + seconds + " s");
                return new HdResult(summary, session.InstallerChanged, hdFiles > 0);
            }

            /// <summary>Copies one HD file from the pack next to its target, checks it, and renames it over the target.</summary>
            private void WriteFromPack(HdPackFile f, string target, out uint[] pieces)
            {
                string temp = TempFor(target);
                var crc = new PieceCrc(f.HdSize);
                try
                {
                    using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        f.Zip.CopyStored(f.Entry, (b, o, n) =>
                        {
                            output.Write(b, o, n);
                            crc.Add(b, o, n);
                        });
                        FileSystem.FlushToDisk(output);
                    }
                    if (crc.Length != f.HdSize || crc.Whole != f.HdCrc) throw new HdZipException(f.Path + " in the pack is not the file the manifest describes");
                    FileSystem.Rename(temp, target);
                }
                catch
                {
                    TryDelete(temp);
                    throw;
                }
                pieces = crc.Pieces;
            }
        }

        // ------------------------------------------------------------------ the installer's index, its saved copy, the journal

        /// <summary>One copy of the index being brought in line with Data/.</summary>
        internal sealed class IndexCopy
        {
            public string Path;
            public PackIndex Index;
            public bool Changed;
        }

        /// <summary>The installer's index (when gl_ver lets the installer keep it), our saved copy, the journal; written by <see cref="Commit"/>.</summary>
        internal sealed class IndexSession
        {
            private readonly InstallerDataLocations loc;
            private readonly Log log;
            private readonly Dictionary<string, List<PackEntry>> journalChanges = new Dictionary<string, List<PackEntry>>(StringComparer.Ordinal);
            private readonly HashSet<string> journalWanted = new HashSet<string>(StringComparer.Ordinal);

            public IndexSession(InstallerDataLocations loc, Log log)
            {
                this.loc = loc;
                this.log = log;
                string problem;
                Installer = Load(loc.PackInfo, true, out problem);
                InstallerProblem = problem;
                string ignored;
                Saved = Load(loc.SavedIndex, false, out ignored);
                if (Saved != null && Saved.Index.IsRebuilt) Saved = null;
            }

            /// <summary>The installer's own index, or null when it has none it would keep (missing, unreadable, gl_ver not 1173200).</summary>
            public IndexCopy Installer { get; private set; }

            public string InstallerProblem { get; private set; }

            /// <summary>McvsOffline/install/pack.info when it is a Gameloft index, else null.</summary>
            public IndexCopy Saved { get; private set; }

            public bool InstallerChanged { get; private set; }

            private IndexCopy Load(string path, bool installer, out string problem)
            {
                problem = null;
                byte[] raw = ReadBytes(path);
                if (raw == null)
                {
                    problem = "no " + (installer ? "pack.info" : "saved index");
                    return null;
                }
                if (installer)
                {
                    byte[] ver = ReadBytes(loc.GlVer);
                    string glVer = ver == null ? null : Encoding.UTF8.GetString(ver);
                    if (glVer != InstallerData.ApkVersionCode)
                    {
                        problem = "gl_ver " + (glVer == null ? "missing" : "'" + glVer + "'") + ": the installer deletes its index";
                        return null;
                    }
                }
                string error;
                PackIndex index = PackIndex.TryParse(raw, out error);
                if (index == null)
                {
                    problem = "pack.info unusable: " + error;
                    return null;
                }
                return new IndexCopy { Path = path, Index = index };
            }

            /// <summary>Offset and ZIP size of each piece of <paramref name="path"/> in the installer's Gameloft index when it lists the original's layout.</summary>
            public List<HdZipRef> ZipRefs(string path, long originalSize)
            {
                IndexCopy source = Installer != null && !Installer.Index.IsRebuilt ? Installer : Saved;
                if (source == null || source.Index.IsRebuilt) return null;
                List<PackEntry> current = source.Index.EntriesOf(path);
                if (!SameSizes(current, PackIndex.PieceSizes(originalSize))) return null;
                var refs = new List<HdZipRef>();
                foreach (PackEntry e in current) refs.Add(new HdZipRef { Offset = e.Offset, ZipSize = e.ZipSize });
                return refs;
            }

            /// <summary>
            /// Rewrites the entries of every record's file whose listing differs from what Data/ holds (an HD file, or the
            /// original); a missing file or another file is left to the installer as it is listed. Returns the files changed.
            /// </summary>
            public int Reconcile(IndexCopy copy, IList<HdRecord> records, Dictionary<string, Content> contents)
            {
                var replacements = new Dictionary<string, List<PackEntry>>(StringComparer.Ordinal);
                bool rebuilt = copy.Index.IsRebuilt;
                foreach (HdRecord r in records)
                {
                    Content c;
                    if (!contents.TryGetValue(r.Path, out c) || (c != Content.Hd && c != Content.Original)) continue;
                    List<PackEntry> current = copy.Index.EntriesOf(r.Path);
                    if (current.Count == 0) continue;
                    long size = c == Content.Hd ? r.HdSize : r.OriginalSize;
                    int[] sizes = PackIndex.PieceSizes(size);
                    uint[] crcs = null;
                    if (!rebuilt)
                    {
                        crcs = c == Content.Hd ? r.HdPieceCrcs : r.OriginalPieceCrcs;
                        if (crcs == null || crcs.Length != sizes.Length)
                        {
                            crcs = PieceCrcsOf(DataPath(loc, r.Path), size);
                            if (c == Content.Hd) r.HdPieceCrcs = crcs;
                            else r.OriginalPieceCrcs = crcs;
                        }
                        if (crcs == null) continue;
                    }
                    if (Matches(current, sizes, crcs)) continue;
                    List<PackEntry> like = current;
                    if (c == Content.Original && r.OriginalZip != null && r.OriginalZip.Count == sizes.Length)
                    {
                        like = new List<PackEntry>();
                        foreach (HdZipRef z in r.OriginalZip) like.Add(new PackEntry { Offset = z.Offset, ZipSize = z.ZipSize });
                    }
                    List<PackEntry> with = PackIndex.EntriesFor(r.Path, size, crcs, rebuilt, like);
                    foreach (PackEntry e in with) e.Folder = current[0].Folder;   // the folder exactly as this index spells it
                    replacements[r.Path] = with;
                }
                if (replacements.Count == 0) return 0;
                copy.Index = copy.Index.Replace(replacements);
                copy.Changed = true;
                if (copy == Installer)
                {
                    foreach (KeyValuePair<string, List<PackEntry>> kv in replacements) journalChanges[kv.Key] = kv.Value;
                }
                return replacements.Count;
            }

            /// <summary>Journal lines for every piece of the split files that hold HD textures (the installer downloads a listed piece without one).</summary>
            public void EnsureJournal(IList<HdRecord> records, Dictionary<string, Content> contents)
            {
                if (Installer == null) return;
                foreach (HdRecord r in records)
                {
                    Content c;
                    if (!contents.TryGetValue(r.Path, out c) || c != Content.Hd) continue;
                    foreach (PackEntry e in Installer.Index.EntriesOf(r.Path))
                    {
                        if (e.Piece > 0) journalWanted.Add(e.JournalKey);
                    }
                }
            }

            /// <summary>Writes what changed: the installer's index, the journal, the saved copy (in that order; each atomically).</summary>
            public void Commit()
            {
                if (Installer != null && Installer.Changed)
                {
                    AtomicFile.WriteAllBytes(Installer.Path, Installer.Index.Raw);
                    InstallerChanged = true;
                    Installer.Changed = false;
                }
                UpdateJournal();
                if (Saved != null && Saved.Changed)
                {
                    AtomicFile.WriteAllBytes(Saved.Path, Saved.Index.Raw);
                    Saved.Changed = false;
                }
            }

            private void UpdateJournal()
            {
                if (journalChanges.Count == 0 && journalWanted.Count == 0) return;
                byte[] raw = ReadBytes(loc.Journal);
                string text = raw == null ? "" : Encoding.UTF8.GetString(raw);
                // A changed file's old piece lines go (folder + name, or folder + name + ".split_NNNN"); its new pieces come in.
                var drop = new HashSet<string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, List<PackEntry>> kv in journalChanges)
                {
                    PackEntry first = kv.Value[0];
                    drop.Add(first.Folder + first.FileName);
                    foreach (PackEntry e in kv.Value)
                    {
                        if (e.Piece > 0) journalWanted.Add(e.JournalKey);
                    }
                }
                var lines = new List<string>();
                var present = new HashSet<string>(StringComparer.Ordinal);
                bool changed = false;
                foreach (string line in text.Split('\n'))
                {
                    string l = line.TrimEnd('\r');
                    if (l.Length == 0) continue;
                    int split = l.LastIndexOf(".split_", StringComparison.Ordinal);
                    string fileKey = split < 0 ? l : l.Substring(0, split);
                    if (drop.Contains(fileKey) && !journalWanted.Contains(l))
                    {
                        changed = true;
                        continue;
                    }
                    if (present.Add(l)) lines.Add(l);
                }
                foreach (string key in journalWanted)
                {
                    if (present.Add(key))
                    {
                        lines.Add(key);
                        changed = true;
                    }
                }
                journalChanges.Clear();
                journalWanted.Clear();
                if (!changed) return;
                var sb = new StringBuilder();
                foreach (string l in lines) sb.Append(l).Append('\n');
                try
                {
                    AtomicFile.WriteAllBytes(loc.Journal, Encoding.UTF8.GetBytes(sb.ToString()));
                }
                catch (Exception ex)
                {
                    log.Warn("hd: cannot write " + loc.Journal, ex);
                }
            }

            private static bool Matches(List<PackEntry> current, int[] sizes, uint[] crcs)
            {
                if (!SameSizes(current, sizes)) return false;
                if (crcs == null) return true;
                for (int i = 0; i < sizes.Length; i++)
                {
                    if ((uint)current[i].Crc != crcs[i]) return false;
                }
                return true;
            }

            private static bool SameSizes(List<PackEntry> current, int[] sizes)
            {
                if (current.Count != sizes.Length) return false;
                for (int i = 0; i < sizes.Length; i++)
                {
                    if (current[i].Size != sizes[i] || current[i].Piece != (sizes.Length > 1 ? i + 1 : 0)) return false;
                }
                return true;
            }
        }

        // ------------------------------------------------------------------ files

        /// <summary>CRC-32 of a file's 2 MiB pieces as the installer lists them (one CRC for a whole file).</summary>
        internal sealed class PieceCrc
        {
            private readonly int[] sizes;
            private readonly uint[] crcs;
            private int piece;
            private int inPiece;
            private uint whole;

            public PieceCrc(long size)
            {
                sizes = PackIndex.PieceSizes(size);
                crcs = new uint[sizes.Length];
            }

            public long Length { get; private set; }

            public uint Whole { get { return whole; } }

            public uint[] Pieces { get { return (uint[])crcs.Clone(); } }

            public void Add(byte[] data, int offset, int count)
            {
                whole = Install.Crc32.Update(whole, data, offset, count);
                Length += count;
                while (count > 0 && piece < sizes.Length)
                {
                    int n = Math.Min(count, sizes[piece] - inPiece);
                    crcs[piece] = Install.Crc32.Update(crcs[piece], data, offset, n);
                    inPiece += n;
                    offset += n;
                    count -= n;
                    if (inPiece == sizes[piece] && piece < sizes.Length - 1)
                    {
                        piece++;
                        inPiece = 0;
                    }
                }
            }
        }

        /// <summary>
        /// Copies <paramref name="source"/> to a temporary file next to <paramref name="target"/>, checks size and CRC-32, flushes it
        /// and renames it over the target. <see cref="HdZipException"/> when the source is not the expected file.
        /// </summary>
        internal static void CopyChecked(string source, string target, long size, uint crc, out uint[] pieces)
        {
            string dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = TempFor(target);
            var check = new PieceCrc(size);
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[256 * 1024];
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, n);
                        check.Add(buffer, 0, n);
                    }
                    FileSystem.FlushToDisk(output);
                }
                if (check.Length != size || check.Whole != crc)
                    throw new HdZipException("is not the original the pack was made from (" + check.Length + " bytes, CRC-32 " + HdPack.Hex(check.Whole) +
                                             "; expected " + size + " bytes, " + HdPack.Hex(crc) + ")");
                FileSystem.Rename(temp, target);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
            pieces = check.Pieces;
        }

        /// <summary>The CRC-32 of each piece of a file of <paramref name="size"/> bytes, or null when it does not have that size or cannot be read.</summary>
        internal static uint[] PieceCrcsOf(string path, long size)
        {
            try
            {
                if (Length(path) != size) return null;
                var check = new PieceCrc(size);
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buffer = new byte[256 * 1024];
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0) check.Add(buffer, 0, n);
                }
                return check.Length == size ? check.Pieces : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static uint FileCrc(string path)
        {
            try
            {
                uint crc = 0;
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buffer = new byte[256 * 1024];
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0) crc = Install.Crc32.Update(crc, buffer, 0, n);
                }
                return crc;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>A dot-name next to the file (the installer-data scan and the rebuilt index skip names that start with '.').</summary>
        internal static string TempFor(string target)
        {
            return Path.Combine(Path.GetDirectoryName(target), "." + Path.GetFileName(target) + TempSuffix);
        }

        /// <summary>Temporary files a start cut short left behind: next to every recorded Data/ file, and anywhere in the backup folder.</summary>
        private static void DeleteTemps(InstallerDataLocations loc, HdState state, Log log)
        {
            int deleted = 0;
            foreach (HdRecord r in state.Records)
            {
                string temp = TempFor(DataPath(loc, r.Path));
                if (File.Exists(temp) && TryDelete(temp)) deleted++;
            }
            string backups = Path.Combine(HdDir(loc), BackupFolder);
            try
            {
                var pending = new Stack<string>();
                if (Directory.Exists(backups)) pending.Push(backups);
                while (pending.Count > 0)
                {
                    string dir = pending.Pop();
                    foreach (string file in Directory.GetFiles(dir, "*" + TempSuffix))
                    {
                        if (TryDelete(file)) deleted++;
                    }
                    foreach (string sub in Directory.GetDirectories(dir)) pending.Push(sub);
                }
            }
            catch (Exception)
            {
            }
            if (deleted > 0) log.Info("hd: deleted " + deleted + " temporary files of a start that was cut short");
        }

        private static void DeleteEmptyDirs(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (string dir in Directory.GetDirectories(root)) DeleteEmptyDirs(dir);
                if (Directory.GetFiles(root).Length == 0 && Directory.GetDirectories(root).Length == 0) Directory.Delete(root, false);
            }
            catch (Exception)
            {
            }
        }

        private static string Count(int n, string one, string many)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
        }

        private static string Mib(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
        }

        internal static long Length(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static byte[] ReadBytes(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
