using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Names;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>
    /// Our names for the in-match bots (setting <c>crm.botNames</c>, build 28; docs/protocol/content.md "Bot names").
    /// <para>
    /// The client names every bot from column 0 of the AI table <c>BotNames</c> (293 rows in 1.17.32), read from the
    /// GLOBAL database each time a match level loads (GM/MultiplayerComponent.cs LoadBotNames, a Level component) and drawn
    /// without replacement (GetNextAvailableBotName). A CRM changer can only edit existing rows, found by value (EN/Database.cs
    /// ApplyCRMTableChanges, GetRowByFieldValue FirstOrDefault), so a match can never have more than those rows; the pool
    /// grows across matches instead. The renames are SET cells <c>MCVS_AI.BotNames["Name@Name"][stock] = ours</c> in
    /// <c>_ServerConfig.default._OverrideTables</c> of the <c>configs/users/me/game</c> answer only
    /// (<see cref="CrmDocumentCache.GameSelectorJson"/>): every GS World fetches that selector when it is created
    /// (EN/CRMServerSettingsComponent.cs OnCreate → RetrieveServerConfig) and installs the block on the global database
    /// before its match level loads (GM/GameplayCRMComponent.cs InitGameplayCrmSettingsAsync → RefreshDatabases), which is
    /// what LoadBotNames reads. The player's own document (<c>configs/users/me</c>, the TS's per-player databases) carries
    /// no bot names and does not change with the window, so the TS never reloads its databases for them.
    /// </para>
    /// <para>
    /// The stock names are the keys, so they are read from the player's own data pack (Design/MCVS_AI.xlsx) at run time and
    /// never shipped. Every <see cref="WindowMinutes"/> minutes (about one match) a new window keeps
    /// <see cref="KeepStockPercent"/> per cent of the stock names (so the game's own names still come up) and renames the
    /// rest with names from <see cref="GamerTags"/> (scope <see cref="Scope"/>, id "&lt;window&gt;|&lt;slot&gt;"), the same
    /// on both platforms: since build 29 the curated names (each window draws its own couple of hundred of them), so a day
    /// of play meets the 291 stock names and nearly every curated one. None of ours equals a stock name ignoring case or by
    /// skeleton (a renamed row would be renamed again by a later cell of the same changer), none repeats (the client adds
    /// "(n)" to duplicate names), none reads as a number or starts with '+', '-' or '*' (CRMTableCell turns such text into
    /// a double, which the bot list reads as null, or into an ADD/MUL cell), none holds ':' (the bot's credential is
    /// "dev:&lt;name&gt;", MultiplayerComponent.CredentialFromBotName, and Credential throws on a second ':'):
    /// <see cref="GamerTags.ShapeProblem"/>. A stock name that appears twice is always renamed (its second row keeps
    /// the stock name: one cell renames the first only), so no window has a name twice. Without a data pack nothing is sent
    /// and the bots keep their stock names.
    /// </para>
    /// </summary>
    public sealed class CrmBotNames
    {
        public const string Database = "MCVS_AI";
        public const string Table = "BotNames";
        public const string Field = "Name";
        public const string LogicalPath = "Design/MCVS_AI.xlsx";

        /// <summary>Hash scope of bot names (versioned: a new scope renames every bot).</summary>
        public const string Scope = "mcvs-offline/bot-name/v1";

        /// <summary>Length of a rename window: about one match (a GS World fetches the window's set when it is created).</summary>
        public const int WindowMinutes = 10;

        /// <summary>Per cent of the stock names (drawn per window) that keep their name, so the game's own names still come up.</summary>
        public const int KeepStockPercent = 25;

        /// <summary>A missing data pack is looked for again after this long (a fresh install unpacks it after the server starts).</summary>
        private const int RetryMs = 60000;

        private static readonly object ForGate = new object();

        private readonly ServerContext context;
        private readonly Log log;
        private readonly object gate = new object();
        private List<string> stock;
        private HashSet<string> stockRepeated;
        private string stockHash;
        private bool attempted;
        private int lastAttemptTick;

        public CrmBotNames(ServerContext context, Log log)
        {
            this.context = context;
            this.log = log ?? Log.Null;
        }

        /// <summary>The server's shared instance.</summary>
        public static CrmBotNames For(ServerContext context)
        {
            lock (ForGate)
            {
                CrmBotNames names = context.Features.Get<CrmBotNames>();
                if (names != null) return names;
                names = new CrmBotNames(context, context.Logger.For("crm"));
                context.Features.Set(names);
                return names;
            }
        }

        /// <summary>
        /// The stock names of the player's data pack (distinct, in table order), or null when it cannot be read. Read once;
        /// a failed read is retried after a minute.
        /// </summary>
        public List<string> StockNames(CrmOptions options, out string hash)
        {
            HashSet<string> repeated;
            return StockNames(options, out hash, out repeated);
        }

        /// <summary><see cref="StockNames(CrmOptions, out string)"/>, also returning the names the table has more than once.</summary>
        public List<string> StockNames(CrmOptions options, out string hash, out HashSet<string> repeated)
        {
            lock (gate)
            {
                if (stock == null && (!attempted || unchecked(Environment.TickCount - lastAttemptTick) >= RetryMs))
                {
                    attempted = true;
                    lastAttemptTick = Environment.TickCount;
                    string text = ReadTable(options);
                    HashSet<string> twice = null;
                    List<string> names = text == null ? null : ParseStockNames(text, out twice);
                    if (names != null && names.Count > 0)
                    {
                        stock = names;
                        stockRepeated = twice;
                        stockHash = GamerTags.Fnv64(string.Join("\n", names.ToArray())).ToString("x16", CultureInfo.InvariantCulture);
                        log.Info("bot names: " + names.Count + " stock names in " + LogicalPath);
                    }
                    else
                    {
                        log.Warn("bot names: " + LogicalPath + (text == null ? " is not in the data pack" : " has no BotNames names")
                                 + "; the bots keep the game's names");
                    }
                }
                hash = stockHash;
                repeated = stockRepeated;
                return stock;
            }
        }

        private string ReadTable(CrmOptions options)
        {
            string text;
            if (context.Content.DataPack != null && context.Content.TryReadGameText(LogicalPath, out text)) return text;
            var candidates = new List<string>();
            if (options != null && !string.IsNullOrEmpty(options.DataPackDir)) candidates.Add(options.DataPackDir);
            if (!string.IsNullOrEmpty(context.DataDir)) candidates.Add(System.IO.Path.Combine(System.IO.Path.Combine(context.DataDir, ".."), "Data"));
            foreach (string dir in candidates)
            {
                try
                {
                    var pack = new McvsOffline.Server.Core.Content.DataPack(dir);
                    if (pack.IsValid && pack.TryReadText(LogicalPath, out text)) return text;
                }
                catch (Exception ex)
                {
                    log.Warn("bot names: cannot read the data pack at " + dir + ": " + ex.Message);
                }
            }
            return null;
        }

        /// <summary>The <c>Name</c> column of the <c>BotNames</c> table of an AI database text: strings only, distinct (ordinal), in table order.</summary>
        public static List<string> ParseStockNames(string text)
        {
            HashSet<string> repeated;
            return ParseStockNames(text, out repeated);
        }

        /// <summary><see cref="ParseStockNames(string)"/>, also returning the names that are in the column more than once (ordinal).</summary>
        public static List<string> ParseStockNames(string text, out HashSet<string> repeated)
        {
            repeated = new HashSet<string>(StringComparer.Ordinal);
            object parsed;
            if (!MiniJson.TryParse(text, out parsed)) return null;
            var table = JsonValue.GetObject(JsonValue.AsObject(parsed), Table);
            var fields = JsonValue.GetArray(table, "Fields");
            var rows = JsonValue.GetArray(table, "Rows");
            if (fields == null || rows == null) return null;
            int column = fields.FindIndex(f => JsonValue.AsString(f) == Field);
            if (column < 0) return null;
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object row in rows)
            {
                var cells = JsonValue.AsArray(row);
                string name = cells != null && column < cells.Count ? cells[column] as string : null;
                if (string.IsNullOrEmpty(name)) continue;
                if (seen.Add(name)) names.Add(name);
                else repeated.Add(name);
            }
            return names;
        }

        /// <summary>
        /// The renames for the window of <paramref name="utcNow"/>: the stock names (ordinal order) this window does not keep,
        /// each with our name for its slot. Pure: the same stock names, content and window give the same list.
        /// </summary>
        public static List<KeyValuePair<string, string>> Renames(IList<string> stockNames, GamerTags tags, DateTime utcNow)
        {
            return Renames(stockNames, null, tags, utcNow);
        }

        /// <summary>
        /// <see cref="Renames(IList{string}, GamerTags, DateTime)"/>; a name in <paramref name="repeated"/> (in the table
        /// more than once) is always renamed, so its two rows never show the same name.
        /// </summary>
        public static List<KeyValuePair<string, string>> Renames(IList<string> stockNames, ICollection<string> repeated, GamerTags tags, DateTime utcNow)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (stockNames == null || stockNames.Count == 0) return result;
            tags = tags ?? GamerTags.BuiltIn;
            var sorted = new List<string>(new HashSet<string>(stockNames, StringComparer.Ordinal));
            sorted.Sort(StringComparer.Ordinal);
            // Ours never equal a stock name or each other, ignoring case and by skeleton (GamerTags.Reserve).
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in sorted) GamerTags.Reserve(taken, name);
            string window = Window(utcNow).ToString(CultureInfo.InvariantCulture);
            for (int slot = 0; slot < sorted.Count; slot++)
            {
                if ((repeated == null || !repeated.Contains(sorted[slot])) && Keeps(window, sorted[slot])) continue;
                string ours = tags.UniqueName(Scope, window + "|" + slot.ToString(CultureInfo.InvariantCulture), taken);
                result.Add(new KeyValuePair<string, string>(sorted[slot], ours));
            }
            return result;
        }

        /// <summary>Whether the window keeps <paramref name="stockName"/> (<see cref="KeepStockPercent"/> per cent, drawn per window).</summary>
        private static bool Keeps(string window, string stockName)
        {
            ulong h = GamerTags.Fnv64(Scope + "|keep|" + window + "|" + stockName);
            h ^= h >> 29;
            return (int)((h >> 16) % 100UL) < KeepStockPercent;
        }

        /// <summary>The rename window of <paramref name="utcNow"/>: <see cref="WindowMinutes"/>-minute steps since 0001-01-01 UTC.</summary>
        public static long Window(DateTime utcNow)
        {
            return utcNow.Ticks / (WindowMinutes * 600000000L); // TimeSpan.TicksPerMinute (not in the game's trimmed mscorlib)
        }

        /// <summary>
        /// Writes the rename cells into an <c>_OverrideTables</c> tree; a cell the tree already has for the same stock name
        /// (an overlay's) is kept. Returns how many cells were written.
        /// </summary>
        public static int Apply(Dictionary<string, object> tables, IList<KeyValuePair<string, string>> renames)
        {
            if (tables == null || renames == null) return 0;
            var existing = JsonValue.GetObject(JsonValue.GetObject(JsonValue.GetObject(tables, Database), Table), Field + "@" + Field);
            int written = 0;
            foreach (var kv in renames)
            {
                if (existing != null && existing.ContainsKey(kv.Key)) continue;
                if (string.IsNullOrEmpty(kv.Key) || GamerTags.ShapeProblem(kv.Value) != null) continue;
                CrmDocumentBuilder.SetCell(tables, Database, Table, Field, Field, kv.Key, kv.Value);
                written++;
            }
            return written;
        }
    }
}
