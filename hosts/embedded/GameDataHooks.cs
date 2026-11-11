// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL, into the net8 tests and into the patcher tests' server fixture.
// Self-contained on purpose (System only): the patch fixtures compile this file alone.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;

namespace McvsOffline
{
    /// <summary>
    /// Entry points of the build-30 hitch patches (the owner's S22 log of one bot match: the game thread stopped for seconds at
    /// the match start and on the results screen). Both keep the game's behaviour identical and only skip work whose result is
    /// already known; both are called by the patched IL through a guarded forwarder, so a failure here means "do it the game's
    /// way". They run on the game thread (and on whatever thread the game calls them from): cheap, never throw.
    /// <list type="bullet">
    /// <item>Patch 'crmtables' (Gameloft.Engine <c>Database.Changer</c> setter): <see cref="SameCrmChanges"/>. The game makes a new
    /// <c>CRMDatabaseChanger</c> object for every CRM refresh (<c>CRMComponent.GetChanger</c>), so the setter's
    /// <c>changer == value</c> test never holds and, whenever the old or the new changer has cells, the database is read and parsed
    /// again from disk (<c>Load(File)</c>: MCVS_Meta.xlsx is 1.6 MB of JSON, 34 MB of garbage per load) and the cells applied to
    /// the fresh tables. Our CRM document always has MCVS_Meta cells (the collector layer, the live-ops modifier), so the GS
    /// creation and the client's handshake reply reloaded it at every match start, and the TS on every return to the menu. When
    /// the incoming changer holds exactly the same cells in the same order, the tables already are the shipped rows plus those
    /// cells, so the patched setter stores the new object and raises <c>TablesLoaded</c> (what Load's <c>OnTableLoaded</c>
    /// ends with, for the typed tables and the other listeners) without the reload.</item>
    /// <item>Patch 'crmtables', build 31 (Gameloft.Engine <c>Database.Load(string)</c>): <see cref="RestoreDatabaseFile"/>,
    /// <see cref="DatabaseFileParsed"/> and <see cref="ForgetDatabaseFiles"/>. The owner's build-30 log: during agent selection
    /// the GS adds a bot about every 5 s, each bot gets new per-user databases (<c>PerUserDatabase.GetDatabase</c>: a new
    /// <c>Database</c>, its file parsed), and in our single process all three Worlds' <c>DatabaseLoaded</c> handlers run on it,
    /// so MCVS_Meta was parsed three times per bot (the new database, then the client's per-player cells, then the GS cells
    /// again), about 265 ms each on the game thread. The second time a new database parses a file, its freshly parsed rows
    /// (before any CRM cell) are copied and kept; every later <c>Load</c> of that file, by any database, gets a copy of those
    /// rows instead of reading and parsing the file, then the game's own <c>OnTableLoaded</c> applies the cells and raises
    /// <c>TablesLoaded</c> as after a parse.</item>
    /// <item>Patch 'jsonnorm' (Gameloft.Util <c>Json.NormalizeJsonString</c>): <see cref="CachedNormalizedJson"/> and
    /// <see cref="RememberNormalizedJson"/>. <c>ProfileSection.IsModified</c> normalizes both the saved and the current profile
    /// text (a full parse, a recursive sort, a re-serialization), and every profile save evaluates it twice: four
    /// normalizations of the ~611 KB unlock-all gameProfile per save, on the game thread. The function is pure, so the last
    /// few large results are kept and an identical input (ordinal) gets the same string back.</item>
    /// <item>Patch 'templates', build 32 (Gameloft.Templates <c>JsonTemplateReader</c>, <c>TObject.FromObject</c>): timing only,
    /// <see cref="TemplateClock"/>, <see cref="TemplateRead"/>, <see cref="TemplateFromObject"/> and <see cref="TemplateToJson"/>. The reader change itself is
    /// pure IL (a per-object dictionary instead of two linear scans per property); these say on the device how long each large
    /// template read (the profile of every bot at agent select, every profile load) and each large FromObject (the profile save's
    /// writer) took.</item>
    /// </list>
    /// </summary>
    public static class GameDataHooks
    {
        /// <summary>Inputs shorter than this are normalized as before, without the cache (they are cheap).</summary>
        public const int NormalizeCacheMinChars = 16 * 1024;

        /// <summary>Large results kept (the saved and the current text of the gameProfile and of the root profile).</summary>
        public const int NormalizeCacheSlots = 4;

        /// <summary>A normalization of at least this many characters gets a log line.</summary>
        public const int NormalizeLogMinChars = 64 * 1024;

        /// <summary>At most this many crm-tables lines per process (then only the counters of the status line).</summary>
        public const int CrmLineLimit = 40;

        /// <summary>The parse of a file by a new database that makes its rows kept (the first one of a file is not kept: most
        /// files are loaded once per process, and a copy of them would only cost memory).</summary>
        public const int KeepFileAtParse = 2;

        /// <summary>At most this many files are kept.</summary>
        public const int KeptFilesLimit = 32;

        /// <summary>At most this many database-file lines (parsed, kept, restored) per process.</summary>
        public const int FileLineLimit = 80;

        /// <summary>A template read or a FromObject that took at least this long gets a "templates:" line.</summary>
        public const double TemplateLogMinMs = 100;

        /// <summary>A template read with an object of at least this many names gets a "templates:" line, however fast.</summary>
        public const int TemplateLogMinNames = 1000;

        /// <summary>At most this many "templates:" lines per process (then only the counters of the status line).</summary>
        public const int TemplateLineLimit = 60;

        private static volatile Action<string> info;
        private static readonly object gate = new object();

        // crmtables
        private static int crmSkipped, crmChanged, crmLines;
        private static long crmCompareTicks;

        // crmtables, Database.Load (the maps under fileGate)
        private static readonly object fileGate = new object();
        private static readonly Dictionary<string, KeptFile> keptFiles = new Dictionary<string, KeptFile>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> freshParses = new Dictionary<string, int>(StringComparer.Ordinal);
        private static int filesParsed, filesRestored, fileLines, filesForgotten;
        private static long fileParseTicks, fileRestoreTicks;
        [ThreadStatic] private static long loadStarted;
        [ThreadStatic] private static bool loadFresh;

        // jsonnorm (under gate)
        private static readonly string[] normInput = new string[NormalizeCacheSlots];
        private static readonly string[] normOutput = new string[NormalizeCacheSlots];
        private static readonly long[] normUsed = new long[NormalizeCacheSlots];
        private static long normClock;
        private static int normHits, normMisses, normHitsSinceLine;
        private static long normMissTicks;
        [ThreadStatic] private static long normStarted;

        // templates (Interlocked)
        private static int templateReads, templateLargeReads, templateIndexed, templateLargest, templateLines;
        private static long templateReadTicks, templateLargeReadTicks, templateMaxReadTicks;
        private static int fromObjects, fromObjectsLarge;
        private static long fromObjectLargeTicks, fromObjectMaxTicks;
        private static int jsonTexts, jsonTextsLarge;
        private static long jsonLargeTicks, jsonMaxTicks;
        // The reads that ended on this thread, newest last (start, inclusive ticks): a read that ends takes out those that started
        // after it (they ran inside it: a non-local @base, BindBase -> TemplateManager.GetTemplate -> a nested ReadDefaultTemplate).
        private const int NestedSlots = 32;
        [ThreadStatic] private static long[] endedStarts;
        [ThreadStatic] private static long[] endedTicks;
        [ThreadStatic] private static int endedCount;

        /// <summary>The embedded host's log (bootstrap.log, "[game]"); null: no lines, counters only.</summary>
        public static void Configure(Action<string> infoLine)
        {
            info = infoLine;
        }

        // ------------------------------------------------------------------------------------------------ crmtables

        /// <summary>
        /// True when <paramref name="incoming"/> holds exactly the cells of <paramref name="current"/>, both non-null, distinct
        /// objects with at least one table changer, and <paramref name="database"/> has a file: the patched setter then keeps the
        /// tables (they are the file's rows plus these cells) instead of reloading them. False in every other case, including
        /// any failure, and the game's own setter runs.
        /// </summary>
        public static bool SameCrmChanges(object database, object current, object incoming)
        {
            try
            {
                if (current == null || incoming == null || ReferenceEquals(current, incoming)) return false;
                long started = Stopwatch.GetTimestamp();
                IList a = Get(current, "TableChangers") as IList;
                IList b = Get(incoming, "TableChangers") as IList;
                if (a == null || b == null || a.Count == 0 || b.Count == 0) return false;   // the game's setter: no reload or the in-place apply
                string file = Get(database, "File") as string;
                if (string.IsNullOrEmpty(file)) return false;
                int cells;
                string difference = Compare(a, b, out cells);
                long ticks = Stopwatch.GetTimestamp() - started;
                Interlocked.Add(ref crmCompareTicks, ticks);
                if (difference == null) Interlocked.Increment(ref crmSkipped);
                else Interlocked.Increment(ref crmChanged);
                if (Interlocked.Increment(ref crmLines) <= CrmLineLimit)
                {
                    Info("crm tables: " + file + (difference == null
                             ? " got the same CRM cells again (" + a.Count.ToString(CultureInfo.InvariantCulture) + " tables, " +
                               cells.ToString(CultureInfo.InvariantCulture) + " cells): kept, no reload from disk"
                             : " got other CRM cells (" + difference + "): the game reloads it") +
                         " (compared in " + Ms(ticks) + ")");
                }
                return difference == null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Null when the two lists of <c>CRMTableChanger</c> are equal in order (TableName; FieldChangers in order with
        /// FieldToChange, IndexField; TableCells in order with CellRefValue, Operator, CellNewValue), else what differs first.
        /// </summary>
        internal static string Compare(IList a, IList b, out int cells)
        {
            cells = 0;
            if (a.Count != b.Count) return "tables " + a.Count + " -> " + b.Count;
            for (int t = 0; t < a.Count; t++)
            {
                object ta = a[t], tb = b[t];
                if (ta == null || tb == null) return "table " + t + " is null";
                string name = Get(ta, "TableName") as string;
                if (!string.Equals(name, Get(tb, "TableName") as string, StringComparison.Ordinal)) return "table " + t + " name";
                IList fa = Get(ta, "FieldChangers") as IList, fb = Get(tb, "FieldChangers") as IList;
                if (fa == null || fb == null || fa.Count != fb.Count) return name + " fields";
                for (int f = 0; f < fa.Count; f++)
                {
                    object xa = fa[f], xb = fb[f];
                    if (xa == null || xb == null) return name + " field " + f + " is null";
                    if (!string.Equals(Get(xa, "FieldToChange") as string, Get(xb, "FieldToChange") as string, StringComparison.Ordinal) ||
                        !string.Equals(Get(xa, "IndexField") as string, Get(xb, "IndexField") as string, StringComparison.Ordinal))
                        return name + " field " + f;
                    IList ca = Get(xa, "TableCells") as IList, cb = Get(xb, "TableCells") as IList;
                    if (ca == null || cb == null || ca.Count != cb.Count) return name + " field " + f + " cells";
                    for (int c = 0; c < ca.Count; c++)
                    {
                        object pa = ca[c], pb = cb[c];
                        if (pa == null || pb == null) return name + " cell is null";
                        if (!string.Equals(Get(pa, "CellRefValue") as string, Get(pb, "CellRefValue") as string, StringComparison.Ordinal) ||
                            !Equals(Get(pa, "Operator"), Get(pb, "Operator")) || !Equals(Get(pa, "CellNewValue"), Get(pb, "CellNewValue")))
                            return name + " " + (Get(pa, "CellRefValue") as string ?? "?");
                    }
                    cells += ca.Count;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------------------------------------ crmtables: Database.Load

        /// <summary>The rows of one file as its parse left them (before any CRM cell): never handed out, only copied.</summary>
        private sealed class KeptFile
        {
            public ConstructorInfo TableConstructor;
            public string[] Names;
            public string[][] Fields;
            public object[][][] Rows;
            public int RowCount, CellCount;
        }

        /// <summary>
        /// Called first by the patched <c>Database.Load(path)</c>. True when <paramref name="path"/>'s rows are kept: then every
        /// table of the file in <paramref name="database"/> (an existing <c>Table</c> object, or a new one as <c>Load</c> makes it)
        /// gets a copy of the kept <c>Fields</c> and <c>Rows</c> (new arrays, the same interned strings and boxed values a parse
        /// gives), and the patched method goes on with the game's <c>OnTableLoaded(null)</c>, as after its parse. False (also on
        /// any failure, after which the game's parse sets every table of the file again): the game reads and parses the file.
        /// </summary>
        public static bool RestoreDatabaseFile(object database, string path)
        {
            loadStarted = 0;
            try
            {
                if (database == null || string.IsNullOrEmpty(path)) return false;
                IDictionary tables = Get(database, "Tables") as IDictionary;
                if (tables == null) return false;
                long started = Stopwatch.GetTimestamp();
                KeptFile kept;
                lock (fileGate) keptFiles.TryGetValue(path, out kept);
                if (kept == null)
                {
                    loadFresh = tables.Count == 0;
                    loadStarted = started;
                    return false;
                }
                // Every array first, then the assignments (they cannot fail half-way for a reason of ours).
                int count = kept.Names.Length;
                var fields = new string[count][];
                var rows = new object[count][][];
                for (int t = 0; t < count; t++)
                {
                    fields[t] = (string[])kept.Fields[t].Clone();
                    object[][] source = kept.Rows[t];
                    var copy = new object[source.Length][];
                    for (int r = 0; r < source.Length; r++) copy[r] = (object[])source[r].Clone();
                    rows[t] = copy;
                }
                for (int t = 0; t < count; t++)
                {
                    string name = kept.Names[t];
                    object table = tables.Contains(name) ? tables[name] : null;
                    if (table == null)
                    {
                        // Load: value = (Tables[item.Key] = new Table()); value.Database = this; value.Name = item.Key;
                        table = kept.TableConstructor.Invoke(null);
                        tables[name] = table;
                        Set(table, "Database", database);
                        Set(table, "Name", name);
                    }
                    SetField(table, "Fields", fields[t]);
                    SetField(table, "Rows", rows[t]);
                }
                long ticks = Stopwatch.GetTimestamp() - started;
                Interlocked.Increment(ref filesRestored);
                Interlocked.Add(ref fileRestoreTicks, ticks);
                if (Interlocked.Increment(ref fileLines) <= FileLineLimit)
                {
                    Info("crm tables: " + path + " restored from memory (" + count.ToString(CultureInfo.InvariantCulture) + " tables, " +
                         kept.RowCount.ToString(CultureInfo.InvariantCulture) + " rows) in " + Ms(ticks) + ", no read from disk");
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Called by the patched <c>Database.Load(path)</c> after the game's parse, before its <c>OnTableLoaded(null)</c> (which
        /// applies the CRM cells). Counts the parse; at the <see cref="KeepFileAtParse"/>-th parse of a file by a new database
        /// (an empty <c>Tables</c> before it, so every table came from this file) keeps a copy of its rows, unless a cell is not
        /// an immutable value (a JSON object or array: nothing is kept for that file).
        /// </summary>
        public static void DatabaseFileParsed(object database, string path)
        {
            long started = loadStarted;
            bool fresh = loadFresh;
            loadStarted = 0;
            try
            {
                if (started == 0 || database == null || string.IsNullOrEmpty(path)) return;
                long ticks = Stopwatch.GetTimestamp() - started;
                Interlocked.Increment(ref filesParsed);
                Interlocked.Add(ref fileParseTicks, ticks);
                string kept = null;
                if (fresh)
                {
                    int parses;
                    bool keep;
                    lock (fileGate)
                    {
                        freshParses.TryGetValue(path, out parses);
                        freshParses[path] = ++parses;
                        keep = parses >= KeepFileAtParse && !keptFiles.ContainsKey(path) && keptFiles.Count < KeptFilesLimit;
                    }
                    if (keep)
                    {
                        KeptFile file = Snapshot(Get(database, "Tables") as IDictionary);
                        if (file == null)
                        {
                            kept = "not kept in memory (a cell is not a plain value)";
                        }
                        else
                        {
                            lock (fileGate)
                            {
                                if (!keptFiles.ContainsKey(path) && keptFiles.Count < KeptFilesLimit) keptFiles[path] = file;
                            }
                            kept = "its rows are kept in memory for the next loads (" + file.Names.Length.ToString(CultureInfo.InvariantCulture) +
                                   " tables, " + file.RowCount.ToString(CultureInfo.InvariantCulture) + " rows, " +
                                   file.CellCount.ToString(CultureInfo.InvariantCulture) + " cells)";
                        }
                    }
                }
                if (Interlocked.Increment(ref fileLines) <= FileLineLimit)
                {
                    Info("crm tables: " + path + " read from disk and parsed in " + Ms(ticks) +
                         (fresh ? " (a new database)" : " (a reload)") + (kept == null ? "" : "; " + kept));
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>The game's <c>Database.Game_DataChanged</c> (a data file changed, development builds): nothing kept is used again.</summary>
        public static void ForgetDatabaseFiles()
        {
            try
            {
                lock (fileGate)
                {
                    if (keptFiles.Count > 0) filesForgotten += keptFiles.Count;
                    keptFiles.Clear();
                    freshParses.Clear();
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>A copy of every table's Fields and Rows in the dictionary's order, or null when a cell is not a plain value.</summary>
        private static KeptFile Snapshot(IDictionary tables)
        {
            if (tables == null || tables.Count == 0) return null;
            var names = new List<string>();
            var fields = new List<string[]>();
            var rows = new List<object[][]>();
            Type tableType = null;
            int rowCount = 0, cellCount = 0;
            foreach (DictionaryEntry entry in tables)
            {
                string name = entry.Key as string;
                object table = entry.Value;
                if (name == null || table == null) return null;
                if (tableType == null) tableType = table.GetType();
                else if (table.GetType() != tableType) return null;
                string[] f = GetField(table, "Fields") as string[];
                object[][] r = GetField(table, "Rows") as object[][];
                if (f == null || r == null) return null;
                var copy = new object[r.Length][];
                for (int i = 0; i < r.Length; i++)
                {
                    object[] row = r[i];
                    if (row == null) return null;
                    foreach (object cell in row)
                    {
                        if (!IsPlainValue(cell)) return null;
                    }
                    copy[i] = (object[])row.Clone();
                    cellCount += row.Length;
                }
                rowCount += r.Length;
                names.Add(name);
                fields.Add((string[])f.Clone());
                rows.Add(copy);
            }
            ConstructorInfo constructor = tableType == null ? null : tableType.GetConstructor(Type.EmptyTypes);
            if (constructor == null) return null;
            return new KeptFile
            {
                TableConstructor = constructor, Names = names.ToArray(), Fields = fields.ToArray(), Rows = rows.ToArray(),
                RowCount = rowCount, CellCount = cellCount,
            };
        }

        /// <summary>Null, a string or a boxed primitive, decimal, DateTime, DateTimeOffset, TimeSpan or Guid: values nothing can change.</summary>
        internal static bool IsPlainValue(object cell)
        {
            if (cell == null || cell is string) return true;
            Type type = cell.GetType();
            return type.IsPrimitive || type.IsEnum || cell is decimal || cell is DateTime || cell is DateTimeOffset || cell is TimeSpan || cell is Guid;
        }

        // ------------------------------------------------------------------------------------------------ jsonnorm

        /// <summary>
        /// The normalized text of an input that was normalized before and is still kept (same characters), else null: the patched
        /// <c>NormalizeJsonString</c> then runs the game's code and hands the result to <see cref="RememberNormalizedJson"/>.
        /// </summary>
        public static string CachedNormalizedJson(string json)
        {
            try
            {
                if (json == null || json.Length < NormalizeCacheMinChars) return null;
                lock (gate)
                {
                    for (int i = 0; i < NormalizeCacheSlots; i++)
                    {
                        string kept = normInput[i];
                        if (kept == null || kept.Length != json.Length) continue;
                        if (!ReferenceEquals(kept, json) && !string.Equals(kept, json, StringComparison.Ordinal)) continue;
                        normUsed[i] = ++normClock;
                        normHits++;
                        normHitsSinceLine++;
                        return normOutput[i];
                    }
                }
                normStarted = Stopwatch.GetTimestamp();
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Keeps the game's result for <paramref name="json"/> (large inputs only) and returns it unchanged.</summary>
        public static string RememberNormalizedJson(string normalized, string json)
        {
            try
            {
                if (json == null || json.Length < NormalizeCacheMinChars || normalized == null) return normalized;
                long started = normStarted;
                normStarted = 0;
                long ticks = started == 0 ? 0 : Stopwatch.GetTimestamp() - started;
                int hits;
                lock (gate)
                {
                    int slot = 0;
                    for (int i = 0; i < NormalizeCacheSlots; i++)
                    {
                        if (normInput[i] == null) { slot = i; break; }
                        if (normUsed[i] < normUsed[slot]) slot = i;
                    }
                    normInput[slot] = json;
                    normOutput[slot] = normalized;
                    normUsed[slot] = ++normClock;
                    normMisses++;
                    normMissTicks += ticks;
                    hits = normHitsSinceLine;
                    normHitsSinceLine = 0;
                }
                if (json.Length >= NormalizeLogMinChars)
                {
                    Info("json normalize: " + json.Length.ToString(CultureInfo.InvariantCulture) + " chars in " + Ms(ticks) + " (" +
                         hits.ToString(CultureInfo.InvariantCulture) + " large ones from the cache since the last line)");
                }
                return normalized;
            }
            catch (Exception)
            {
                return normalized;
            }
        }

        // ------------------------------------------------------------------------------------------------ templates

        /// <summary>The patched reader's clock (<see cref="Stopwatch.GetTimestamp"/>).</summary>
        public static long TemplateClock()
        {
            return Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// After a <c>JsonTemplateReader</c> read its default template (<c>ReadDefaultTemplate</c>'s first call): from
        /// <paramref name="started"/> to now, the JSON parse up to <paramref name="rooted"/> (0: unknown), <paramref name="indexed"/>
        /// objects of 8 names or more found their children by the dictionary, the largest with <paramref name="largest"/> names.
        /// A slow or large one gets a "templates:" line; every read counts in the status line. Build 32 review: a template read
        /// inside this one on the same thread (a base template that was not cached yet) has its own call and line, so its time is
        /// taken out of this read's total, nodes part and status counters, and the line says how much was taken out.
        /// </summary>
        public static void TemplateRead(string source, long started, long rooted, int indexed, int largest)
        {
            try
            {
                if (started == 0) return;
                long now = Stopwatch.GetTimestamp();
                int nestedReads;
                long nested = TakeNested(started, now - started, out nestedReads);
                long ticks = NotNegative(now - started - nested);
                Interlocked.Increment(ref templateReads);
                Interlocked.Add(ref templateReadTicks, ticks);
                if (indexed > 0) Interlocked.Add(ref templateIndexed, indexed);
                Max(ref templateLargest, largest);
                MaxLong(ref templateMaxReadTicks, ticks);
                double ms = ticks * 1000.0 / Stopwatch.Frequency;
                if (ms < TemplateLogMinMs && largest < TemplateLogMinNames) return;
                Interlocked.Increment(ref templateLargeReads);
                Interlocked.Add(ref templateLargeReadTicks, ticks);
                if (Interlocked.Increment(ref templateLines) > TemplateLineLimit) return;
                string json = rooted >= started && rooted <= now
                    ? "JSON " + Ms(rooted - started) + ", nodes " + Ms(NotNegative(now - rooted - nested))
                    : "";
                if (nestedReads > 0)
                {
                    json += (json.Length > 0 ? "; " : "") + "not counting " + Ms(nested) + " in " + nestedReads.ToString(CultureInfo.InvariantCulture) +
                            (nestedReads == 1 ? " template" : " templates") + " read inside it, counted on their own";
                }
                if (json.Length > 0) json = " (" + json + ")";
                Info("templates: " + (source ?? "?") + " read in " + Ms(ticks) + json + "; " + indexed.ToString(CultureInfo.InvariantCulture) +
                     " objects of 8+ names indexed, the largest " + largest.ToString(CultureInfo.InvariantCulture) + " names (patch 'templates')");
            }
            catch (Exception)
            {
            }
        }

        // Not Math.Max: against the game's Android mscorlib (MCVS_REFS) the embedded build found no Math.Max(long, long) overload.
        private static long NotNegative(long value)
        {
            return value < 0 ? 0 : value;
        }

        /// <summary>
        /// The inclusive ticks of the reads that ended on this thread since <paramref name="started"/> (each started at or after
        /// it, so it ran inside this read; their own nested reads were taken out by them), and records this read. Reads on one
        /// thread either nest or follow each other, so those are the newest records.
        /// </summary>
        private static long TakeNested(long started, long inclusive, out int count)
        {
            long[] starts = endedStarts, ticks = endedTicks;
            if (starts == null)
            {
                endedStarts = starts = new long[NestedSlots];
                endedTicks = ticks = new long[NestedSlots];
                endedCount = 0;
            }
            int n = endedCount;
            long nested = 0;
            count = 0;
            while (n > 0 && starts[n - 1] >= started)
            {
                n--;
                nested += ticks[n];
                count++;
            }
            if (n == NestedSlots)
            {
                Array.Copy(starts, 1, starts, 0, n - 1);
                Array.Copy(ticks, 1, ticks, 0, n - 1);
                n--;
            }
            starts[n] = started;
            ticks[n] = inclusive;
            endedCount = n + 1;
            return nested;
        }

        /// <summary>After <c>TObject.FromObject(instance)</c> (the profile save's writer among others): a slow one gets a line.</summary>
        public static void TemplateFromObject(object instance, long started)
        {
            try
            {
                if (started == 0) return;
                long ticks = Stopwatch.GetTimestamp() - started;
                Interlocked.Increment(ref fromObjects);
                MaxLong(ref fromObjectMaxTicks, ticks);
                if (ticks * 1000.0 / Stopwatch.Frequency < TemplateLogMinMs) return;
                Interlocked.Increment(ref fromObjectsLarge);
                Interlocked.Add(ref fromObjectLargeTicks, ticks);
                if (Interlocked.Increment(ref templateLines) > TemplateLineLimit) return;
                Info("templates: TObject.FromObject(" + (instance == null ? "null" : instance.GetType().Name) + ") took " + Ms(ticks) +
                     " (the template writer: a profile save builds its text from it)");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// After <c>TObject.ToString("j")</c> (or "J") wrote <paramref name="text"/>: the profile save's text (GameProfileSection.FromGameObject
        /// is FromObject, then this). A slow one gets a line; every one counts in the status line.
        /// </summary>
        public static void TemplateToJson(string text, long started)
        {
            try
            {
                if (started == 0) return;
                long ticks = Stopwatch.GetTimestamp() - started;
                Interlocked.Increment(ref jsonTexts);
                MaxLong(ref jsonMaxTicks, ticks);
                if (ticks * 1000.0 / Stopwatch.Frequency < TemplateLogMinMs) return;
                Interlocked.Increment(ref jsonTextsLarge);
                Interlocked.Add(ref jsonLargeTicks, ticks);
                if (Interlocked.Increment(ref templateLines) > TemplateLineLimit) return;
                Info("templates: TObject.ToString(\"j\") wrote " + (text == null ? 0 : text.Length).ToString(CultureInfo.InvariantCulture) +
                     " chars in " + Ms(ticks) + " (the template writer's text: a profile save stores it)");
            }
            catch (Exception)
            {
            }
        }

        private static void Max(ref int target, int value)
        {
            int seen;
            while (value > (seen = Interlocked.CompareExchange(ref target, 0, 0)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }

        private static void MaxLong(ref long target, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }

        // ------------------------------------------------------------------------------------------------ status

        public static int CrmReloadsSkipped { get { return crmSkipped; } }

        public static int CrmReloadsKept { get { return crmChanged; } }

        public static int FilesParsed { get { return filesParsed; } }

        public static int FilesRestored { get { return filesRestored; } }

        public static int FilesKept { get { lock (fileGate) return keptFiles.Count; } }

        public static int NormalizeHits { get { lock (gate) return normHits; } }

        public static int NormalizeMisses { get { lock (gate) return normMisses; } }

        public static int TemplateReads { get { return Interlocked.CompareExchange(ref templateReads, 0, 0); } }

        public static int TemplateObjectsIndexed { get { return Interlocked.CompareExchange(ref templateIndexed, 0, 0); } }

        public static int TemplateLargestObject { get { return Interlocked.CompareExchange(ref templateLargest, 0, 0); } }

        /// <summary>One status-line part: "data hooks: crm tables ...; json normalize ...".</summary>
        public static string Describe()
        {
            int hits, misses;
            long missTicks;
            lock (gate)
            {
                hits = normHits;
                misses = normMisses;
                missTicks = normMissTicks;
            }
            int kept, forgotten;
            lock (fileGate)
            {
                kept = keptFiles.Count;
                forgotten = filesForgotten;
            }
            return "data hooks: crm tables " + crmSkipped.ToString(CultureInfo.InvariantCulture) + " reloads skipped (same cells), " +
                   crmChanged.ToString(CultureInfo.InvariantCulture) + " reloaded (other cells), compare total " +
                   Ms(Interlocked.Read(ref crmCompareTicks)) + "; database files " + filesParsed.ToString(CultureInfo.InvariantCulture) +
                   " parsed (" + Ms(Interlocked.Read(ref fileParseTicks)) + "), " + filesRestored.ToString(CultureInfo.InvariantCulture) +
                   " restored from memory (" + Ms(Interlocked.Read(ref fileRestoreTicks)) + "), " + kept.ToString(CultureInfo.InvariantCulture) +
                   " files kept" + (forgotten > 0 ? " (" + forgotten.ToString(CultureInfo.InvariantCulture) + " forgotten: data changed)" : "") +
                   "; json normalize " + misses.ToString(CultureInfo.InvariantCulture) +
                   " large ones computed (" + Ms(missTicks) + "), " + hits.ToString(CultureInfo.InvariantCulture) + " from the cache" +
                   DescribeTemplates();
        }

        /// <summary>"; templates: none read (no 'templates' patch)" or "; templates: 412 read (...)".</summary>
        private static string DescribeTemplates()
        {
            int reads = Interlocked.CompareExchange(ref templateReads, 0, 0);
            int converted = Interlocked.CompareExchange(ref fromObjects, 0, 0);
            if (reads == 0 && converted == 0 && Interlocked.CompareExchange(ref jsonTexts, 0, 0) == 0) return "; templates: none read yet (or no 'templates' patch)";
            return "; templates: " + reads.ToString(CultureInfo.InvariantCulture) + " read (" + Ms(Interlocked.Read(ref templateReadTicks)) +
                   ", longest " + Ms(Interlocked.Read(ref templateMaxReadTicks)) + "; " + Interlocked.CompareExchange(ref templateLargeReads, 0, 0).ToString(CultureInfo.InvariantCulture) +
                   " slow or large ones, " + Ms(Interlocked.Read(ref templateLargeReadTicks)) + "), " +
                   Interlocked.CompareExchange(ref templateIndexed, 0, 0).ToString(CultureInfo.InvariantCulture) + " objects indexed (largest " +
                   Interlocked.CompareExchange(ref templateLargest, 0, 0).ToString(CultureInfo.InvariantCulture) + " names); FromObject " +
                   converted.ToString(CultureInfo.InvariantCulture) + " (" + Interlocked.CompareExchange(ref fromObjectsLarge, 0, 0).ToString(CultureInfo.InvariantCulture) +
                   " of 100 ms or more, " + Ms(Interlocked.Read(ref fromObjectLargeTicks)) + ", longest " + Ms(Interlocked.Read(ref fromObjectMaxTicks)) + "); ToString(j) " +
                   Interlocked.CompareExchange(ref jsonTexts, 0, 0).ToString(CultureInfo.InvariantCulture) + " (" +
                   Interlocked.CompareExchange(ref jsonTextsLarge, 0, 0).ToString(CultureInfo.InvariantCulture) + " of 100 ms or more, " +
                   Ms(Interlocked.Read(ref jsonLargeTicks)) + ", longest " + Ms(Interlocked.Read(ref jsonMaxTicks)) + ")";
        }

        /// <summary>Tests: forget the counters and the cache.</summary>
        internal static void Reset()
        {
            lock (gate)
            {
                for (int i = 0; i < NormalizeCacheSlots; i++)
                {
                    normInput[i] = null;
                    normOutput[i] = null;
                    normUsed[i] = 0;
                }
                normClock = 0;
                normHits = normMisses = normHitsSinceLine = 0;
                normMissTicks = 0;
            }
            crmSkipped = crmChanged = crmLines = 0;
            crmCompareTicks = 0;
            lock (fileGate)
            {
                keptFiles.Clear();
                freshParses.Clear();
                filesParsed = filesRestored = fileLines = filesForgotten = 0;
                fileParseTicks = fileRestoreTicks = 0;
            }
            loadStarted = 0;
            loadFresh = false;
            templateReads = templateLargeReads = templateIndexed = templateLargest = templateLines = 0;
            templateReadTicks = templateLargeReadTicks = templateMaxReadTicks = 0;
            endedCount = 0;
            fromObjects = fromObjectsLarge = 0;
            fromObjectLargeTicks = fromObjectMaxTicks = 0;
            jsonTexts = jsonTextsLarge = 0;
            jsonLargeTicks = jsonMaxTicks = 0;
        }

        // ------------------------------------------------------------------------------------------------ helpers

        private static readonly Dictionary<Type, Dictionary<string, PropertyInfo>> properties = new Dictionary<Type, Dictionary<string, PropertyInfo>>();

        /// <summary>A public instance property of the object's type, the PropertyInfo cached per type and name.</summary>
        private static object Get(object target, string property)
        {
            if (target == null) return null;
            Type type = target.GetType();
            PropertyInfo p;
            lock (properties)
            {
                Dictionary<string, PropertyInfo> ofType;
                if (!properties.TryGetValue(type, out ofType))
                {
                    ofType = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
                    properties[type] = ofType;
                }
                if (!ofType.TryGetValue(property, out p))
                {
                    p = type.GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
                    ofType[property] = p;
                }
            }
            if (p == null) throw new InvalidOperationException(type.FullName + " has no public property " + property);
            return p.GetValue(target, null);
        }

        private static readonly Dictionary<Type, Dictionary<string, FieldInfo>> fieldInfos = new Dictionary<Type, Dictionary<string, FieldInfo>>();

        private static FieldInfo FieldOf(object target, string field)
        {
            Type type = target.GetType();
            FieldInfo f;
            lock (fieldInfos)
            {
                Dictionary<string, FieldInfo> ofType;
                if (!fieldInfos.TryGetValue(type, out ofType))
                {
                    ofType = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
                    fieldInfos[type] = ofType;
                }
                if (!ofType.TryGetValue(field, out f))
                {
                    f = type.GetField(field, BindingFlags.Instance | BindingFlags.Public);
                    ofType[field] = f;
                }
            }
            if (f == null) throw new InvalidOperationException(type.FullName + " has no public field " + field);
            return f;
        }

        /// <summary>A public instance field (Table.Fields, Table.Rows), the FieldInfo cached per type and name.</summary>
        private static object GetField(object target, string field)
        {
            return target == null ? null : FieldOf(target, field).GetValue(target);
        }

        private static void SetField(object target, string field, object value)
        {
            FieldOf(target, field).SetValue(target, value);
        }

        /// <summary>Sets a public instance property (its setter may be non-public: Table.Database and Table.Name are public).</summary>
        private static void Set(object target, string property, object value)
        {
            PropertyInfo p = target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
            if (p == null || !p.CanWrite) throw new InvalidOperationException(target.GetType().FullName + " has no settable property " + property);
            p.SetValue(target, value, null);
        }

        private static string Ms(long ticks)
        {
            return (ticks * 1000.0 / Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture) + " ms";
        }

        private static void Info(string line)
        {
            try
            {
                Action<string> sink = info;
                if (sink != null) sink(line);
            }
            catch (Exception)
            {
            }
        }
    }
}
