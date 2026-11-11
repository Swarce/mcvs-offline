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
    /// <item>Patch 'jsonnorm' (Gameloft.Util <c>Json.NormalizeJsonString</c>): <see cref="CachedNormalizedJson"/> and
    /// <see cref="RememberNormalizedJson"/>. <c>ProfileSection.IsModified</c> normalizes both the saved and the current profile
    /// text (a full parse, a recursive sort, a re-serialization), and every profile save evaluates it twice: four
    /// normalizations of the ~611 KB unlock-all gameProfile per save, on the game thread. The function is pure, so the last
    /// few large results are kept and an identical input (ordinal) gets the same string back.</item>
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

        private static volatile Action<string> info;
        private static readonly object gate = new object();

        // crmtables
        private static int crmSkipped, crmChanged, crmLines;
        private static long crmCompareTicks;

        // jsonnorm (under gate)
        private static readonly string[] normInput = new string[NormalizeCacheSlots];
        private static readonly string[] normOutput = new string[NormalizeCacheSlots];
        private static readonly long[] normUsed = new long[NormalizeCacheSlots];
        private static long normClock;
        private static int normHits, normMisses, normHitsSinceLine;
        private static long normMissTicks;
        [ThreadStatic] private static long normStarted;

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

        // ------------------------------------------------------------------------------------------------ status

        public static int CrmReloadsSkipped { get { return crmSkipped; } }

        public static int CrmReloadsKept { get { return crmChanged; } }

        public static int NormalizeHits { get { lock (gate) return normHits; } }

        public static int NormalizeMisses { get { lock (gate) return normMisses; } }

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
            return "data hooks: crm tables " + crmSkipped.ToString(CultureInfo.InvariantCulture) + " reloads skipped (same cells), " +
                   crmChanged.ToString(CultureInfo.InvariantCulture) + " reloaded (other cells), compare total " +
                   Ms(Interlocked.Read(ref crmCompareTicks)) + "; json normalize " + misses.ToString(CultureInfo.InvariantCulture) +
                   " large ones computed (" + Ms(missTicks) + "), " + hits.ToString(CultureInfo.InvariantCulture) + " from the cache";
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
