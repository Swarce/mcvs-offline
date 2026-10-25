// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
// Why and how: docs/INSTALL-OFFLINE.md ("How the game's installer decides"), report 10 §1.2, §4.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Embedded.Install
{
    /// <summary>The installer's SharedPreferences (<c>MainActivityPrefs</c>). Implementations never throw.</summary>
    public interface IInstallerPrefs
    {
        /// <summary>The value, or null when it is absent or cannot be read.</summary>
        string GetString(string key);

        int GetInt(string key, int defaultValue);

        bool SetString(string key, string value);

        bool SetInt(string key, int value);
    }

    /// <summary>settings.json "install.*".</summary>
    public sealed class InstallerDataOptions
    {
        public InstallerDataOptions()
        {
            Manage = true;
            SuppressUpdateCheck = true;
            RebuildIndex = true;
            VerifyRestoredData = true;
        }

        /// <summary>install.manageInstaller: prepare the installer's state at all.</summary>
        public bool Manage { get; set; }

        /// <summary>install.suppressUpdateCheck: keep the installer's weekly CDN version check from running (InsTime).</summary>
        public bool SuppressUpdateCheck { get; set; }

        /// <summary>install.rebuildIndex: with no Gameloft index at hand, rebuild one from a complete Data folder.</summary>
        public bool RebuildIndex { get; set; }

        /// <summary>install.verifyRestoredData: after giving the installer a Gameloft index back, check every file's CRC32 in the background.</summary>
        public bool VerifyRestoredData { get; set; }

        /// <summary>Tests: the complete packs a Data folder is compared with before an index is rebuilt (default <see cref="InstallerData.KnownPacks"/>).</summary>
        internal InstallerData.KnownPack[] KnownPacks { get; set; }
    }

    /// <summary>Where the installer keeps its state (GameInstaller, SUtils, installer/Utils; report 10 §4.1).</summary>
    public sealed class InstallerDataLocations
    {
        public InstallerDataLocations(string internalDir, string gameRoot)
        {
            if (string.IsNullOrEmpty(internalDir)) throw new ArgumentException("internal files directory required", "internalDir");
            if (string.IsNullOrEmpty(gameRoot)) throw new ArgumentException("game root required", "gameRoot");
            InternalDir = internalDir;
            GameRoot = gameRoot;
        }

        /// <summary>Context.getFilesDir() (GameInstaller.SaveFolder).</summary>
        public string InternalDir { get; private set; }

        /// <summary>The SDFolder preference (GameInstaller.sd_folder; DATA_PATH is it plus "/").</summary>
        public string GameRoot { get; private set; }

        /// <summary>The installer's copy of the index (DownloadComponent: SaveFolder + "/pack.info").</summary>
        public string PackInfo { get { return Path.Combine(InternalDir, "pack.info"); } }

        /// <summary>APK version marker; any other value makes GameInstaller.onCreate delete pack.info and CRC-check everything.</summary>
        public string GlVer { get { return Path.Combine(Path.Combine(InternalDir, "prefs"), "gl_ver"); } }

        /// <summary>Pieces of split files the installer counts as downloaded (installer/Utils, one folder+name per line).</summary>
        public string Journal { get { return Path.Combine(GameRoot, "d_o_w_n_l_o_a_d_e_d.txt"); } }

        /// <summary>Time of the last weekly version check ("&lt;unix seconds&gt;/").</summary>
        public string InsTime { get { return Path.Combine(GameRoot, "InsTime"); } }

        public string DataDir { get { return Path.Combine(GameRoot, "Data"); } }

        /// <summary>Our copy of the installer's index, next to the player's data (so a backup of files/ includes it).</summary>
        public string SavedIndex { get { return Path.Combine(Path.Combine(Path.Combine(GameRoot, EmbeddedPaths.DataFolderName), "install"), "pack.info"); } }

        public string SavedIndexNote { get { return SavedIndex + ".txt"; } }

        /// <summary>Files the background CRC check found damaged; deleted (and so downloaded again) at the next start.</summary>
        public string DamagedList { get { return Path.Combine(Path.GetDirectoryName(SavedIndex), "damaged.txt"); } }

        /// <summary>An index the player copied next to Data/ (for example from a mirror made with tools/cdn-mirror).</summary>
        public string PlayerIndex { get { return Path.Combine(GameRoot, "pack.info"); } }
    }

    /// <summary>How the files on the device compare with an index, by the installer's own rules (existence and size; no CRC).</summary>
    public sealed class DataCheck
    {
        internal DataCheck()
        {
            Examples = new List<string>();
            GoodPieces = new List<string>();
        }

        public int Entries { get; internal set; }

        public int Good { get; internal set; }

        public int Missing { get; internal set; }

        /// <summary>Entries whose file exists with another size (the installer deletes such a whole file and downloads it again).</summary>
        public int WrongSize { get; internal set; }

        /// <summary>Up to five problems, for the log.</summary>
        public List<string> Examples { get; private set; }

        /// <summary>Journal lines of the pieces of split files that pass the installer's size test.</summary>
        public List<string> GoodPieces { get; private set; }

        public bool Complete { get { return Missing == 0 && WrongSize == 0; } }

        public string Describe()
        {
            string text = Good + "/" + Entries + " entries good, " + Missing + " missing, " + WrongSize + " with another size";
            if (Examples.Count > 0) text += " (" + string.Join(", ", Examples.ToArray()) + ")";
            return text;
        }
    }

    /// <summary>What <see cref="InstallerData.Prepare"/> did.</summary>
    public sealed class InstallerDataResult
    {
        internal InstallerDataResult(string summary, PackIndex verify)
        {
            Summary = summary;
            IndexToVerify = verify;
        }

        /// <summary>One line for the boot trace.</summary>
        public string Summary { get; private set; }

        /// <summary>A Gameloft index just given back to the installer, whose CRC32s <see cref="InstallerData.VerifyContent"/> should check; or null.</summary>
        public PackIndex IndexToVerify { get; private set; }
    }

    /// <summary>
    /// Runs in Bootstrap.Init, before the Java GameInstaller starts, and prepares its state so data that is already on the
    /// device is accepted without the CDN (docs/INSTALL-OFFLINE.md). On a fresh install the installer has no index
    /// (<c>getFilesDir()/pack.info</c>), so whatever is in <c>files/Data</c> it fetches Gameloft's index from the CDN, and
    /// without the journal it downloads every split file again. Here:
    /// <list type="bullet">
    /// <item>every boot, a CDN index the installer has saved is copied to <c>McvsOffline/install/pack.info</c> (so a backup of
    /// <c>files/</c> carries it), and the weekly version check is kept from running (InsTime);</item>
    /// <item>with no installer index, the saved copy (or a <c>files/pack.info</c> the player put there) is checked against the
    /// files and given back to the installer with <c>gl_ver</c>, the journal of split pieces and its preferences;</item>
    /// <item>with no index at all, a Data folder that holds exactly the complete 1.17.32 pack gets a rebuilt index (same
    /// names and layout, CRC 0) that lets the installer accept it; it is re-checked every boot.</item>
    /// </list>
    /// Missing or damaged data is never hidden: the installer then goes its normal (online) way. Never throws.
    /// </summary>
    public static class InstallerData
    {
        /// <summary>The APK's version code as the installer writes it to prefs/gl_ver (GameInstaller.j()).</summary>
        public const string ApkVersionCode = "1173200";

        /// <summary>x-gl-version of the 1.17.32 data ("1.17.32" without dots, HttpClient.a(String, int)).</summary>
        public const int CdnVersion = 11732;

        public const string DataTypeKey = "DATA_TYPE";
        public const string CurrentVersionKey = "CurrentVersion";   // "CurrentVersion" + DownloadComponent name ("")

        /// <summary>The two 1.17.32 data packs (report 10 §3.3): texture variant, files on the device, their total size.</summary>
        public static readonly KnownPack[] KnownPacks =
        {
            new KnownPack("ASTC", 15747, 2042882427L),
            new KnownPack("ETC", 15747, 2036819797L),
        };

        public sealed class KnownPack
        {
            public KnownPack(string variant, int files, long bytes)
            {
                Variant = variant;
                Files = files;
                Bytes = bytes;
            }

            public string Variant { get; private set; }

            public int Files { get; private set; }

            public long Bytes { get; private set; }
        }

        /// <summary>"ASTC", "ETC", or null when the counts are not those of a complete 1.17.32 pack.</summary>
        public static string VariantOf(int files, long bytes)
        {
            return VariantOf(files, bytes, KnownPacks);
        }

        private static string VariantOf(int files, long bytes, KnownPack[] known)
        {
            foreach (KnownPack p in known ?? KnownPacks)
            {
                if (p.Files == files && p.Bytes == bytes) return p.Variant;
            }
            return null;
        }

        /// <summary>Prepares the installer's state. Never throws.</summary>
        public static InstallerDataResult Prepare(InstallerDataLocations loc, IInstallerPrefs prefs, InstallerDataOptions options, Log log, long nowUnixSeconds)
        {
            options = options ?? new InstallerDataOptions();
            var restored = new Restored();
            try
            {
                string summary = PrepareCore(loc, prefs, options, log, nowUnixSeconds, restored);
                return new InstallerDataResult(summary, options.VerifyRestoredData ? restored.Index : null);
            }
            catch (Exception ex)
            {
                log.Warn("installer data: preparation failed; the game's installer runs as usual", ex);
                return new InstallerDataResult("installer data: failed (" + ex.GetType().Name + ")", null);
            }
        }

        /// <summary>The Gameloft index given back to the installer during this run, if any.</summary>
        private sealed class Restored
        {
            public PackIndex Index;
        }

        private static string PrepareCore(InstallerDataLocations loc, IInstallerPrefs prefs, InstallerDataOptions options, Log log, long now, Restored restored)
        {
            if (!options.Manage)
            {
                log.Info("installer data: install.manageInstaller is false, the game's installer state is left alone");
                return "installer data: not managed";
            }
            RemoveDamaged(loc, log);
            string glVer = ReadText(loc.GlVer);
            byte[] internalRaw = ReadBytes(loc.PackInfo);
            string parseError = null;
            PackIndex internalIndex = internalRaw == null ? null : PackIndex.TryParse(internalRaw, out parseError);
            bool hasData = Directory.Exists(loc.DataDir);

            if (internalIndex != null && glVer == ApkVersionCode)
            {
                if (!internalIndex.IsRebuilt)
                {
                    // Its journal is the installer's own business: a half-finished download leaves pre-allocated split files whose
                    // size looks right, so pieces are never added to it from sizes here.
                    SaveCopy(loc, internalIndex, log);
                    RefreshInsTime(loc, options, log, now);
                    return "installer data: Gameloft index in place (" + Describe(internalIndex) + ")";
                }
                DataCheck check = Check(internalIndex, loc.GameRoot);
                if (check.Complete)
                {
                    MergeJournal(loc, check.GoodPieces, log);
                    RefreshInsTime(loc, options, log, now);
                    return "installer data: rebuilt index still matches the data (" + Describe(internalIndex) + ")";
                }
                log.Warn("installer data: the data no longer matches the index rebuilt from it: " + check.Describe());
                string again = Restore(loc, prefs, options, log, now, restored);
                if (again != null) return again;
                TryDelete(loc.PackInfo, log);
                log.Warn("installer data: removed the rebuilt index; the game's installer checks the data against Gameloft's index " +
                         "and downloads what is missing (needs internet; see docs/INSTALL-OFFLINE.md)");
                return "installer data: rebuilt index removed (data incomplete)";
            }

            if (internalRaw != null && internalIndex == null)
            {
                log.Warn("installer data: the installer's own index (" + internalRaw.Length + " bytes) is unusable: " + parseError +
                         " (for example the stub it writes when a CDN request fails)");
            }
            else if (internalIndex != null)
            {
                log.Info("installer data: the installer's index is there but gl_ver is " + (glVer == null ? "missing" : "'" + glVer + "'") +
                         ": the installer would delete it");
            }
            if (!hasData)
            {
                log.Info("installer data: no Data folder in " + loc.GameRoot + ": the game's installer downloads the data pack " +
                         "(needs internet once; or copy a backup or a mirror first, see docs/INSTALL-OFFLINE.md)");
                return "installer data: no data yet";
            }
            string result = Restore(loc, prefs, options, log, now, restored);
            if (result != null) return result;
            return "installer data: left to the game's installer (no usable index)";
        }

        /// <summary>Gives the installer an index that matches the data; null when there is none.</summary>
        private static string Restore(InstallerDataLocations loc, IInstallerPrefs prefs, InstallerDataOptions options, Log log, long now, Restored restored)
        {
            PackIndex partial = null;
            DataCheck partialCheck = null;
            string partialSource = null;
            foreach (string[] candidate in new[] { new[] { loc.SavedIndex, "saved index" }, new[] { loc.PlayerIndex, "files/pack.info" } })
            {
                byte[] raw = ReadBytes(candidate[0]);
                if (raw == null) continue;
                string error;
                PackIndex index = PackIndex.TryParse(raw, out error);
                if (index == null)
                {
                    log.Warn("installer data: " + candidate[1] + " " + candidate[0] + " is not a usable index: " + error);
                    continue;
                }
                if (index.IsRebuilt)
                {
                    log.Info("installer data: " + candidate[1] + " " + candidate[0] + " is a rebuilt index, not Gameloft's: ignored");
                    continue;
                }
                DataCheck check = Check(index, loc.GameRoot);
                if (check.Complete)
                {
                    Install(loc, prefs, options, log, now, index, check, candidate[1], restored);
                    log.Info("installer data: gave the installer its index back from the " + candidate[1] + " (" + Describe(index) +
                             "); every file is in place, the game starts without internet");
                    return "installer data: index restored from the " + candidate[1] + " (" + Describe(index) + ")";
                }
                // A few files of another size are an interrupted download (the installer deletes and fetches them again); many are
                // another texture variant or version, and handing that index over would make the installer delete them all.
                int tolerated = Math.Min(Math.Max(5, check.Entries / 100), check.Good / 2);
                if (check.Good > 0 && check.WrongSize <= tolerated)
                {
                    if (partial == null)
                    {
                        partial = index;
                        partialCheck = check;
                        partialSource = candidate[1];
                    }
                    log.Info("installer data: the " + candidate[1] + " matches the data except " + check.Describe());
                }
                else
                {
                    log.Warn("installer data: the " + candidate[1] + " does not match the data (" + check.Describe() +
                             "; another game version or texture variant?): not used");
                }
            }
            if (partial != null)
            {
                Install(loc, prefs, options, log, now, partial, partialCheck, partialSource, restored);
                log.Warn("installer data: gave the installer its index back from the " + partialSource + "; " +
                         (partialCheck.Missing + partialCheck.WrongSize) + " entries are missing or incomplete and the installer will download them " +
                         "(needs internet and Gameloft's CDN)");
                return "installer data: index restored from the " + partialSource + ", " + (partialCheck.Missing + partialCheck.WrongSize) + " entries to download";
            }
            if (!options.RebuildIndex)
            {
                log.Info("installer data: no saved index and install.rebuildIndex is false: the game's installer checks the data online");
                return null;
            }
            long bytes;
            List<PackIndex.FileInfoEntry> files = ScanData(loc.GameRoot, out bytes);
            string variant = VariantOf(files.Count, bytes, options.KnownPacks);
            if (variant == null)
            {
                var sb = new StringBuilder();
                foreach (KnownPack p in options.KnownPacks ?? KnownPacks)
                {
                    if (sb.Length > 0) sb.Append(" or ");
                    sb.Append(p.Files).Append(" files of ").Append(p.Bytes.ToString(CultureInfo.InvariantCulture)).Append(" bytes (").Append(p.Variant).Append(')');
                }
                log.Warn("installer data: no saved index, and " + loc.DataDir + " holds " + files.Count + " files of " +
                         bytes.ToString(CultureInfo.InvariantCulture) + " bytes, not the complete 1.17.32 data pack (" + sb +
                         "): the game's installer checks it against Gameloft's index and downloads what is missing (needs internet). " +
                         "Copy McvsOffline/install/pack.info from your backup, or files/pack.info from a mirror (docs/INSTALL-OFFLINE.md)");
                return null;
            }
            PackIndex rebuilt = PackIndex.Rebuild(files);
            DataCheck rebuiltCheck = Check(rebuilt, loc.GameRoot);
            if (!rebuiltCheck.Complete)
            {
                log.Warn("installer data: the rebuilt index does not match the data it was built from: " + rebuiltCheck.Describe());
                return null;
            }
            Install(loc, prefs, options, log, now, rebuilt, rebuiltCheck, "files on the device", restored);
            log.Info("installer data: no Gameloft index on the device; the Data folder holds the complete 1.17.32 " + variant +
                     " pack, so the installer got an index rebuilt from it (" + Describe(rebuilt) + "; sizes only, re-checked every start)");
            return "installer data: index rebuilt from the complete " + variant + " data (" + Describe(rebuilt) + ")";
        }

        private static void Install(InstallerDataLocations loc, IInstallerPrefs prefs, InstallerDataOptions options, Log log, long now,
                                     PackIndex index, DataCheck check, string source, Restored restored)
        {
            // pack.info before gl_ver: interrupted in between, the installer deletes the lone index and goes online as before.
            AtomicFile.WriteAllBytes(loc.PackInfo, index.Raw);
            AtomicFile.WriteAllBytes(loc.GlVer, Encoding.ASCII.GetBytes(ApkVersionCode));
            MergeJournal(loc, check.GoodPieces, log);
            string variant = VariantOf(index.LogicalFiles, index.LogicalBytes, options.KnownPacks);
            if (prefs != null)
            {
                // The texture variant the data is, so a later CDN request asks for the same archive (the installer otherwise probes the GPU).
                if (variant != null && prefs.GetString(DataTypeKey) != variant && !prefs.SetString(DataTypeKey, variant))
                    log.Warn("installer data: could not set the DATA_TYPE preference");
                // The CDN version the data is, so the update check does not take 1.17.32 for an update.
                if (prefs.GetInt(CurrentVersionKey, 0) != CdnVersion && !prefs.SetInt(CurrentVersionKey, CdnVersion))
                    log.Warn("installer data: could not set the CurrentVersion preference");
            }
            RefreshInsTime(loc, options, log, now);
            if (!index.IsRebuilt)
            {
                SaveCopy(loc, index, log);
                restored.Index = index;   // sizes only so far: the CRC32s are checked in the background (VerifyContent)
            }
            log.Info("installer data: wrote " + loc.PackInfo + " (" + index.Raw.Length + " bytes, from the " + source + "), " + loc.GlVer + " = " +
                     ApkVersionCode + ", " + check.GoodPieces.Count + " split pieces in the journal" + (variant == null ? "" : ", DATA_TYPE " + variant));
        }

        /// <summary>Compares the files under <paramref name="gameRoot"/> with the index, exactly like DownloadComponent.a(h, ...) without CRC.</summary>
        public static DataCheck Check(PackIndex index, string gameRoot)
        {
            var check = new DataCheck();
            string lastPath = null;
            long lastLength = -1;
            foreach (PackEntry e in index.Entries)
            {
                check.Entries++;
                string path = Path.Combine(gameRoot, e.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (path != lastPath)
                {
                    lastPath = path;
                    lastLength = Length(path);
                }
                bool good;
                if (lastLength < 0) good = false;
                else if (e.Piece == 0) good = lastLength == e.Size;
                else good = GoodSize(lastLength, e.Piece, e.Size);
                if (good)
                {
                    check.Good++;
                    if (e.Piece > 0) check.GoodPieces.Add(e.JournalKey);
                    continue;
                }
                if (lastLength < 0) check.Missing++;
                else check.WrongSize++;
                if (check.Examples.Count < 5)
                    check.Examples.Add(e.RelativePath + (e.Piece > 0 ? " piece " + e.Piece : "") + (lastLength < 0 ? " missing" : " has " + lastLength + " bytes"));
            }
            return check;
        }

        /// <summary>
        /// Background check of a Gameloft index just given back to the installer: the CRC32 of every file, and of every 2 MiB
        /// window of split files (what the installer's own CRC pass checks after an APK change, CRC.java and SplitCRC). Files
        /// that fail are listed in McvsOffline/install/damaged.txt; the next start deletes them before the installer runs, so it
        /// downloads them again (needs internet), as its own CRC pass would. Low priority; returns the number of damaged files,
        /// or -1 when it stopped early.
        /// </summary>
        public static int VerifyContent(InstallerDataLocations loc, PackIndex index, Log log, Func<bool> stop, int pauseMsPerMiB)
        {
            var damaged = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var buffer = new byte[256 * 1024];
            long checkedBytes = 0;
            int started = Environment.TickCount;
            try
            {
                string openPath = null;
                FileStream stream = null;
                try
                {
                    foreach (PackEntry e in index.Entries)
                    {
                        if (stop != null && stop()) return -1;
                        if (seen.Contains(e.RelativePath)) continue;   // already damaged
                        string path = Path.Combine(loc.GameRoot, e.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        if (path != openPath)
                        {
                            if (stream != null) stream.Dispose();
                            stream = null;
                            openPath = path;
                            try
                            {
                                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            }
                            catch (Exception)
                            {
                                stream = null;
                            }
                        }
                        bool good = false;
                        if (stream != null)
                        {
                            long start = e.Piece == 0 ? 0 : (long)(e.Piece - 1) * PackIndex.SplitSize;
                            long length = e.Size;
                            if (start + length <= stream.Length && (e.Piece > 0 || stream.Length == length))
                            {
                                stream.Position = start;
                                uint crc = 0;
                                long left = length;
                                while (left > 0)
                                {
                                    int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                                    if (n <= 0) break;
                                    crc = Crc32.Update(crc, buffer, 0, n);
                                    left -= n;
                                    checkedBytes += n;
                                    if (pauseMsPerMiB > 0 && (checkedBytes & 0xFFFFF) < n) System.Threading.Thread.Sleep(pauseMsPerMiB);
                                }
                                good = left == 0 && crc == (uint)e.Crc;
                            }
                        }
                        if (!good)
                        {
                            seen.Add(e.RelativePath);
                            damaged.Add(e.RelativePath);
                        }
                    }
                }
                finally
                {
                    if (stream != null) stream.Dispose();
                }
            }
            catch (Exception ex)
            {
                log.Warn("installer data: the background data check stopped", ex);
                return -1;
            }
            int seconds = Math.Max(0, (Environment.TickCount - started) / 1000);
            if (damaged.Count == 0)
            {
                log.Info("installer data: background check done, every file matches the CRC32 in Gameloft's index (" +
                         (checkedBytes >> 20) + " MiB in " + seconds + " s)");
                return 0;
            }
            try
            {
                AtomicFile.WriteAllText(loc.DamagedList, string.Join("\n", damaged.ToArray()) + "\n");
            }
            catch (Exception ex)
            {
                log.Warn("installer data: cannot write " + loc.DamagedList, ex);
            }
            log.Warn("installer data: background check: " + damaged.Count + " damaged or incomplete files (" +
                     string.Join(", ", damaged.GetRange(0, Math.Min(5, damaged.Count)).ToArray()) + (damaged.Count > 5 ? ", ..." : "") +
                     "). The next start deletes them so the game's installer downloads them again (needs internet); to avoid that, " +
                     "copy them from another backup or a mirror and delete " + loc.DamagedList);
            return damaged.Count;
        }

        /// <summary>Deletes the files the last background check found damaged (and their journal lines), before the installer runs.</summary>
        private static void RemoveDamaged(InstallerDataLocations loc, Log log)
        {
            string list = ReadText(loc.DamagedList);
            if (list == null) return;
            var damaged = new HashSet<string>(StringComparer.Ordinal);
            int deleted = 0;
            foreach (string line in list.Split('\n'))
            {
                string rel = line.Trim();
                if (rel.Length == 0) continue;
                if (!rel.StartsWith("Data/", StringComparison.Ordinal) || rel.Contains("..") || rel.Contains("\\")) continue;
                damaged.Add(rel);
                string path = Path.Combine(loc.GameRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        deleted++;
                    }
                }
                catch (Exception ex)
                {
                    log.Warn("installer data: cannot delete damaged " + path, ex);
                }
            }
            HashSet<string> journal = ReadJournal(loc.Journal);
            var kept = new StringBuilder();
            int dropped = 0;
            foreach (string key in journal)
            {
                // A journal line is folder + piece name: ".\\Data\1" + "x.f.split_0001" -> Data/1/x.f
                int split = key.LastIndexOf(".split_", StringComparison.Ordinal);
                string withoutPiece = split < 0 ? key : key.Substring(0, split);
                bool match = false;
                foreach (string rel in damaged)
                {
                    int slash = rel.LastIndexOf('/');
                    if (PackIndex.FolderFor(rel.Substring(0, slash)) + rel.Substring(slash + 1) == withoutPiece)
                    {
                        match = true;
                        break;
                    }
                }
                if (match) dropped++;
                else kept.Append(key).Append('\n');
            }
            try
            {
                if (dropped > 0) AtomicFile.WriteAllBytes(loc.Journal, Encoding.UTF8.GetBytes(kept.ToString()));
                File.Delete(loc.DamagedList);
            }
            catch (Exception ex)
            {
                log.Warn("installer data: cannot update " + loc.Journal, ex);
            }
            log.Warn("installer data: deleted " + deleted + " files the last check found damaged (" + dropped +
                     " journal lines); the game's installer downloads them again (needs internet)");
        }

        /// <summary>DownloadComponent.goodSize: the file covers the piece (and for its last piece, ends right after it).</summary>
        public static bool GoodSize(long length, int piece, int pieceSize)
        {
            long pieces = (length + PackIndex.SplitSize - 1) / PackIndex.SplitSize;
            if (pieces == piece) return length % PackIndex.SplitSize == pieceSize;
            return length >= (long)piece * PackIndex.SplitSize;
        }

        /// <summary>Every file under Data/ (names starting with '.' skipped: the installer writes .nomedia files there).</summary>
        public static List<PackIndex.FileInfoEntry> ScanData(string gameRoot, out long bytes)
        {
            var files = new List<PackIndex.FileInfoEntry>();
            bytes = 0;
            var pending = new Stack<string>();
            pending.Push("Data");
            while (pending.Count > 0)
            {
                string relative = pending.Pop();
                string dir = Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(file);
                    if (name.Length == 0 || name[0] == '.') continue;
                    long length = Length(file);
                    if (length < 0) continue;
                    files.Add(new PackIndex.FileInfoEntry(relative + "/" + name, length));
                    bytes += length;
                }
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (name.Length == 0 || name[0] == '.') continue;
                    pending.Push(relative + "/" + name);
                }
            }
            return files;
        }

        private static HashSet<string> ReadJournal(string path)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            string text = ReadText(path);
            if (text == null) return set;
            foreach (string line in text.Split('\n'))
            {
                string l = line.TrimEnd('\r');
                if (l.Length > 0) set.Add(l);
            }
            return set;
        }

        private static void MergeJournal(InstallerDataLocations loc, List<string> keys, Log log)
        {
            if (keys == null || keys.Count == 0) return;
            string existing = ReadText(loc.Journal);
            HashSet<string> present = ReadJournal(loc.Journal);
            var sb = new StringBuilder(existing ?? "");
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
            int added = 0;
            foreach (string key in keys)
            {
                if (present.Add(key))
                {
                    sb.Append(key).Append('\n');
                    added++;
                }
            }
            if (added == 0) return;
            try
            {
                AtomicFile.WriteAllBytes(loc.Journal, Encoding.UTF8.GetBytes(sb.ToString()));
            }
            catch (Exception ex)
            {
                log.Warn("installer data: cannot write " + loc.Journal + " (the installer downloads the split files again)", ex);
            }
        }

        /// <summary>
        /// Keeps the installer's weekly check (GET d.cdn.php&amp;head=1, then a re-sync when the CDN reports a newer version) from
        /// running: it reads DATA_PATH/InsTime and checks when that is a week old (GameInstaller.r(), state 23).
        /// </summary>
        private static void RefreshInsTime(InstallerDataLocations loc, InstallerDataOptions options, Log log, long now)
        {
            if (!options.SuppressUpdateCheck || !Directory.Exists(loc.GameRoot)) return;
            try
            {
                AtomicFile.WriteAllBytes(loc.InsTime, Encoding.ASCII.GetBytes(now.ToString(CultureInfo.InvariantCulture) + "/"));
            }
            catch (Exception ex)
            {
                log.Warn("installer data: cannot write " + loc.InsTime, ex);
            }
        }

        /// <summary>Copies a Gameloft index to McvsOffline/install/ when it differs from the copy there.</summary>
        private static void SaveCopy(InstallerDataLocations loc, PackIndex index, Log log)
        {
            if (index.IsRebuilt) return;
            try
            {
                byte[] saved = ReadBytes(loc.SavedIndex);
                if (saved != null && Same(saved, index.Raw)) return;
                AtomicFile.WriteAllBytes(loc.SavedIndex, index.Raw);
                string variant = VariantOf(index.LogicalFiles, index.LogicalBytes);
                AtomicFile.WriteAllText(loc.SavedIndexNote,
                    "Copy of the game installer's index of the data pack (Modern Combat Versus 1.17.32" + (variant == null ? "" : ", " + variant + " textures") +
                    ", " + index.LogicalFiles + " files, " + index.LogicalBytes.ToString(CultureInfo.InvariantCulture) + " bytes), saved by MCVS Offline on " +
                    DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) + ".\n" +
                    "Keep it in your backup of the files folder. After a reinstall MCVS Offline gives it back to the installer, which then accepts\n" +
                    "the restored Data folder without internet. See docs/INSTALL-OFFLINE.md.\n");
                log.Info("installer data: saved a copy of the installer's index to " + loc.SavedIndex + " (keep it in your backups)");
            }
            catch (Exception ex)
            {
                log.Warn("installer data: cannot save a copy of the installer's index to " + loc.SavedIndex, ex);
            }
        }

        private static string Describe(PackIndex index)
        {
            string variant = VariantOf(index.LogicalFiles, index.LogicalBytes);
            return (variant == null ? "" : variant + ", ") + index.LogicalFiles + " files, " + index.Entries.Count + " entries";
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static long Length(string path)
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

        /// <summary>Like SUtils.ReadFile: the bytes as a string (UTF-8), or null.</summary>
        private static string ReadText(string path)
        {
            byte[] bytes = ReadBytes(path);
            return bytes == null ? null : Encoding.UTF8.GetString(bytes);
        }

        private static void TryDelete(string path, Log log)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                log.Warn("installer data: cannot delete " + path, ex);
            }
        }
    }

    /// <summary>CRC-32 (IEEE, as java.util.zip.CRC32).</summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = MakeTable();

        private static uint[] MakeTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        /// <summary>Continues <paramref name="crc"/> (0 to start) over the bytes.</summary>
        public static uint Update(uint crc, byte[] data, int offset, int count)
        {
            uint c = ~crc;
            for (int i = offset; i < offset + count; i++) c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return ~c;
        }
    }
}
