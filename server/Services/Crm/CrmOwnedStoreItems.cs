using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm.Collector;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>
    /// The per-player CRM documents (<c>configs/users/me</c> with a player's token): the static document with, per player,
    /// <list type="bullet">
    /// <item>the kill-signature avatars and backgrounds the player owns left out of the <c>offline_store</c>
    /// (<c>crm.omitOwnedSignatures</c>, on by default);</item>
    /// <item>while the collector is active (the document carries its layer, economy-collector.md §2.9): the collector's
    /// per-player cells (crate weights, the Unlockable window, the season emblems; <c>CollectorPlan.PlayerCells</c>) and
    /// the guard as the mirror (D10).</item>
    /// </list>
    /// <para>
    /// The store filter: the signature screen lists owned signatures from the profile and only the others from the store
    /// (GM/GameOfflineStore.cs GetKillSignItems: <c>where !profileKSs.Contains(...)</c>), so those entries are never
    /// shown; after unlock-all they are ~1,550 of the ~1,860 entries, ~75% of the document. The size matters on every
    /// return to the menu (after a match or the firing range, ResumeServer): the TS fetches the document twice
    /// (ReloadProfileAsync's forced refresh and DoSaveProfile's), parses each copy twice (CrmSection.Load, GetReader), and
    /// the client and the native engine parse what the TS synchronizes, all on the game thread (device test 6: ~11 s on
    /// the "connecting to the server" screen). Sounds stay listed: the screen takes their owned list from the
    /// backgrounds (a game bug), so the store is the only place they show up.
    /// </para>
    /// <para>
    /// The inventory (<see cref="PlayerInventory.Scan"/>; the agent list only while the collector is active) is taken on
    /// the first request after the game-profile dump was read (<see cref="StorageService.GameProfileReadTopic"/>: the TS
    /// reads it at login and in every ReloadProfileAsync), never on a save: the fetch right after that read is the forced
    /// refresh, which applies the document anyway, and the fetch after each save gets the same text (cached per document
    /// and read), which the TS recognises (StandardCrm.Data.Equals) without applying it again. An account whose dump was
    /// not read since the server started gets the static document: so does a server World's <c>mcvsGameServer</c>
    /// session (the <c>game:</c> super credential has no game profile, §2.9), so the GS and the TS's server Worlds install
    /// the static mirror.
    /// </para>
    /// Anything unexpected in the dump falls back part by part: unreadable avatars or backgrounds leave the store
    /// complete, as before; an unknown part takes the static assumptions in the collector's cells. Never throws.
    /// </summary>
    public sealed class CrmOwnedStoreItems
    {
        private static readonly string[] Parts =
        {
            PlayerInventory.AvatarsPart, PlayerInventory.BackgroundsPart, PlayerInventory.SoundsPart, PlayerInventory.MercsPart,
        };

        private readonly ServerContext context;
        private readonly Log log;
        private readonly object gate = new object();
        private readonly Dictionary<string, long> reads = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, PlayerState> players = new Dictionary<string, PlayerState>(StringComparer.Ordinal);

        public CrmOwnedStoreItems(ServerContext context, Log log)
        {
            this.context = context;
            this.log = log ?? Log.Null;
        }

        /// <summary>EventHub handler for <see cref="StorageService.GameProfileReadTopic"/>: the next document takes a new inventory.</summary>
        public void OnGameProfileRead(object payload)
        {
            string account = JsonValue.GetString(payload as Dictionary<string, object>, "account");
            if (string.IsNullOrEmpty(account)) return;
            lock (gate)
            {
                long n;
                reads.TryGetValue(account, out n);
                reads[account] = n + 1;
            }
        }

        /// <summary>How often the dump of <paramref name="accountId"/> was read since the server started.</summary>
        public long ReadCount(string accountId)
        {
            lock (gate)
            {
                long n;
                return accountId != null && reads.TryGetValue(accountId, out n) ? n : 0;
            }
        }

        /// <summary>The full document's JSON for <paramref name="accountId"/> with <c>crm.omitOwnedSignatures</c> on.</summary>
        public string JsonFor(CrmDocument doc, string accountId)
        {
            return JsonFor(doc, accountId, true);
        }

        /// <summary>
        /// The full document's JSON for <paramref name="accountId"/>: <see cref="CrmDocument.Json"/>, filtered by the owned
        /// signatures when <paramref name="omitOwned"/>, with the collector's per-player cells when the document carries
        /// the collector layer. The static text when neither applies, for an account whose dump was not read since the
        /// server started, and on any failure.
        /// </summary>
        public string JsonFor(CrmDocument doc, string accountId, bool omitOwned)
        {
            if (doc == null) return null;
            bool active = doc.CollectorPhase == CollectorPhase.Active;
            if (string.IsNullOrEmpty(accountId) || (!omitOwned && !active)) return doc.Json;
            try
            {
                PlayerState player = Current(accountId, active, omitOwned);
                if (player == null || player.Inventory == null) return doc.Json;
                lock (player)
                {
                    if (player.Json == null || !ReferenceEquals(player.Document, doc) || player.Omit != omitOwned)
                    {
                        player.Json = Make(doc, accountId, player, omitOwned, active);
                        player.Document = doc;
                        player.Omit = omitOwned;
                    }
                    return player.Json;
                }
            }
            catch (Exception ex)
            {
                log.Error("CRM: the document of " + accountId + " could not be made for the player; serving the static one", ex);
                return doc.Json;
            }
        }

        /// <summary>
        /// <c>GET /_mcvs/collector?account=&lt;id&gt;</c> (economy-collector.md §5.7, device check 14): what the dump of the
        /// account reads as, per part with the problem texts, its agents, and while the collector is active what is left
        /// and the weights its document gets. Never throws.
        /// </summary>
        public Dictionary<string, object> Describe(CrmDocument doc, string accountId)
        {
            var body = NewObject();
            body["account"] = accountId;
            body["phase"] = doc == null ? "unknown" : doc.CollectorPhase.ToString();
            if (doc != null && doc.Collector != null && doc.Collector.Status != null) body["reason"] = doc.Collector.Status.Reason;
            body["reads"] = ReadCount(accountId);
            try
            {
                PlayerState cached;
                lock (gate) players.TryGetValue(accountId ?? "", out cached);
                bool active = doc != null && doc.CollectorPhase == CollectorPhase.Active;
                PlayerState state = cached != null && cached.Inventory != null ? cached : Load(accountId, active, false);
                body["source"] = ReferenceEquals(state, cached) ? "the inventory the documents were made from (read " + cached.Read + ")" : "read now";
                if (state.Inventory == null)
                {
                    body["dump"] = "missing";
                    return body;
                }
                body["dumpChars"] = (long)state.DumpChars;
                PlayerInventory inventory = state.Inventory;
                var parts = NewObject();
                foreach (string part in Parts)
                {
                    var o = NewObject();
                    o["known"] = Known(inventory, part);
                    string problem;
                    if (inventory.Problems.TryGetValue(part, out problem)) o["problem"] = problem;
                    if (part == PlayerInventory.AvatarsPart) o["owned"] = (long)inventory.Avatars.Count;
                    else if (part == PlayerInventory.BackgroundsPart) o["owned"] = (long)inventory.Backgrounds.Count;
                    else if (part == PlayerInventory.SoundsPart) o["owned"] = (long)inventory.Sounds.Count;
                    else o["agents"] = (long)inventory.Mercs.Count;
                    parts[part] = o;
                }
                body["parts"] = parts;
                var agents = new List<object>();
                var ids = new List<int>(inventory.Mercs.Keys);
                ids.Sort((a, b) => a.CompareTo(b));
                foreach (int id in ids)
                {
                    MercState merc = inventory.Mercs[id];
                    var o = NewObject();
                    o["id"] = (long)id;
                    o["unlocked"] = merc.Unlocked;
                    o["skins"] = SortedIds(merc.Skins);
                    o["camos"] = (long)merc.Camos.Count;
                    o["jerseys"] = (long)merc.Accessories.Count;
                    agents.Add(o);
                }
                body["agents"] = agents;
                if (active && doc.Collector.Content != null)
                {
                    CrmCollector c = doc.Collector;
                    CollectorUnits units;
                    CollectorWeights weights;
                    IList<FieldPatch> patches = CollectorPlan.PlayerCells(c.Content, c.Options ?? new CollectorOptions(), inventory, c.LiveOps,
                                                                          out units, out weights);
                    body["left"] = units.Summary(c.Content);
                    var byCategory = NewObject();
                    foreach (string category in CollectorContent.BaseCategories) byCategory[category] = units.Category(c.Content, category);
                    foreach (string category in CollectorContent.PaidCategories) byCategory[category] = units.Category(c.Content, category);
                    body["leftByCategory"] = byCategory;
                    body["unlockableCells"] = (long)UnlockableCells(patches);
                    body["weights"] = WeightsObject(weights);
                }
            }
            catch (Exception ex)
            {
                body["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            return body;
        }

        // ------------------------------------------------------------------------------------------ internals

        private PlayerState Current(string accountId, bool active, bool omitOwned)
        {
            long read;
            lock (gate)
            {
                if (!reads.TryGetValue(accountId, out read) || read == 0) return null;
                PlayerState cached;
                // A read that skipped the agent list (the collector was not active) is read again once it is.
                if (players.TryGetValue(accountId, out cached) && cached.Read == read && (!active || cached.MercsParsed)) return cached;
            }
            PlayerState state = Load(accountId, active, omitOwned);
            state.Read = read;
            lock (gate) players[accountId] = state;
            return state;
        }

        /// <summary>The account's dump as an inventory (none when there is no dump); the agent list only when
        /// <paramref name="parseMercs"/>.</summary>
        private PlayerState Load(string accountId, bool parseMercs, bool omitOwned)
        {
            var state = new PlayerState { MercsParsed = parseMercs };
            IProfileAccess profiles;
            if (string.IsNullOrEmpty(accountId) || !context.Features.TryGet(out profiles)) return state;
            string dump = profiles.ReadText(accountId, McvsConstants.GameProfileSelector + ".gameProfile");
            if (string.IsNullOrEmpty(dump)) return state;
            state.DumpChars = dump.Length;
            PlayerInventory inventory = PlayerInventory.Scan(dump, parseMercs);
            state.Inventory = inventory;
            state.Names = new HashSet<string>(inventory.OwnedStoreNames(), StringComparer.Ordinal);
            if (omitOwned && (!inventory.AvatarsKnown || !inventory.BackgroundsKnown))
                log.Warn("CRM: owned kill signatures of " + accountId + " not read (" + StoreProblem(inventory) + "); serving the whole store");
            return state;
        }

        private string Make(CrmDocument doc, string accountId, PlayerState player, bool omitOwned, bool active)
        {
            ICollection<string> names = omitOwned ? player.Names : null;
            IList<FieldPatch> patches = null;
            if (active)
            {
                CrmCollector c = doc.Collector;
                try
                {
                    CollectorUnits units;
                    CollectorWeights weights;
                    patches = CollectorPlan.PlayerCells(c.Content, c.Options ?? new CollectorOptions(), player.Inventory, c.LiveOps, out units, out weights);
                    log.Info("CRM: collector for " + accountId + ": " + units.Summary(c.Content) + ", "
                             + UnlockableCells(patches).ToString(CultureInfo.InvariantCulture) + " Unlockable cells, " + DescribeParts(player.Inventory));
                }
                catch (Exception ex)
                {
                    log.Error("CRM: the collector cells of " + accountId + " could not be computed; serving the static layer", ex);
                    patches = null;
                }
            }
            int omitted;
            string problem;
            string json = doc.JsonFor(names, patches, out omitted, out problem);
            if (problem != null) log.Warn("CRM: the collector cells of " + accountId + " are not used (" + problem + "); serving the static layer");
            if (names != null && names.Count > 0)
                log.Info("CRM: offline_store for " + accountId + " leaves out " + omitted + " owned kill signature(s) ("
                         + player.Inventory.Avatars.Count + " avatars, " + player.Inventory.Backgrounds.Count + " backgrounds owned): "
                         + doc.Json.Length + " -> " + json.Length + " chars");
            return json;
        }

        /// <summary>"parts known avatars|backgrounds|sounds|mercs" plus the problems of the unknown parts.</summary>
        private static string DescribeParts(PlayerInventory inventory)
        {
            var known = new List<string>();
            var problems = new List<string>();
            foreach (string part in Parts)
            {
                if (Known(inventory, part)) known.Add(part);
                else
                {
                    string problem;
                    inventory.Problems.TryGetValue(part, out problem);
                    problems.Add(part + ": " + (problem ?? "unknown"));
                }
            }
            return "parts known " + (known.Count == 0 ? "none" : string.Join("|", known.ToArray()))
                   + (problems.Count == 0 ? "" : " (" + string.Join("; ", problems.ToArray()) + ")");
        }

        private static bool Known(PlayerInventory inventory, string part)
        {
            switch (part)
            {
                case PlayerInventory.AvatarsPart: return inventory.AvatarsKnown;
                case PlayerInventory.BackgroundsPart: return inventory.BackgroundsKnown;
                case PlayerInventory.SoundsPart: return inventory.SoundsKnown;
                default: return inventory.MercsKnown;
            }
        }

        /// <summary>The store filter's refusal: the avatars' problem, else the backgrounds'.</summary>
        private static string StoreProblem(PlayerInventory inventory)
        {
            string problem;
            if (!inventory.AvatarsKnown && inventory.Problems.TryGetValue(PlayerInventory.AvatarsPart, out problem)) return problem;
            if (!inventory.BackgroundsKnown && inventory.Problems.TryGetValue(PlayerInventory.BackgroundsPart, out problem)) return problem;
            return "unknown problem";
        }

        private static int UnlockableCells(IList<FieldPatch> patches)
        {
            int n = 0;
            if (patches == null) return 0;
            foreach (FieldPatch patch in patches)
                if (patch.Field == "Unlockable") n += patch.Cells.Count;
            return n;
        }

        internal static Dictionary<string, object> WeightsObject(CollectorWeights weights)
        {
            var o = NewObject();
            if (weights == null) return o;
            var ids = new List<int>(weights.Rows.Keys);
            ids.Sort((a, b) => a.CompareTo(b));
            foreach (int id in ids) o[id.ToString(CultureInfo.InvariantCulture)] = weights.Rows[id];
            return o;
        }

        private static List<object> SortedIds(IEnumerable<int> ids)
        {
            var list = new List<int>(ids);
            list.Sort((a, b) => a.CompareTo(b));
            var result = new List<object>(list.Count);
            foreach (int id in list) result.Add((long)id);
            return result;
        }

        private static Dictionary<string, object> NewObject()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        /// <summary>One account's inventory (as of read <see cref="Read"/>) and the document last made with it.</summary>
        private sealed class PlayerState
        {
            public long Read;
            public bool MercsParsed;
            public int DumpChars;

            /// <summary>Null when the account has no dump.</summary>
            public PlayerInventory Inventory;

            /// <summary>The owned avatars and backgrounds as store names (empty unless both lists were read).</summary>
            public HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal);

            public CrmDocument Document;
            public bool Omit;
            public string Json;
        }
    }
}
