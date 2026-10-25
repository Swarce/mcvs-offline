using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>
    /// Leaves the kill-signature avatars and backgrounds a player owns out of the <c>offline_store</c> of their full CRM
    /// document. The signature screen lists owned ones from the profile and only the others from the store
    /// (GM/GameOfflineStore.cs GetKillSignItems: <c>where !profileKSs.Contains(...)</c>), so those entries are never
    /// shown; after unlock-all they are ~1,550 of the ~1,860 entries, ~75% of the document.
    /// <para>
    /// The size matters on every return to the menu (after a match or the firing range, ResumeServer): the TS fetches
    /// the document twice (ReloadProfileAsync's forced refresh and DoSaveProfile's), parses each copy twice
    /// (CrmSection.Load, GetReader), and the client and the native engine parse what the TS synchronizes, all on the
    /// game thread (device test 6: ~11 s on the "connecting to the server" screen).
    /// </para>
    /// <para>
    /// The owned set is taken when the game-profile dump is read (the TS does so at login and in every
    /// ReloadProfileAsync), never on a save: the fetch right after that read is the forced refresh, which applies the
    /// document anyway, and the fetch after each save gets the same text, which the TS recognises
    /// (StandardCrm.Data.Equals) without applying it again. Sounds stay listed: the screen takes their owned list from
    /// the backgrounds (a game bug), so the store is the only place they show up.
    /// </para>
    /// Anything unexpected in the dump leaves the store complete, as before.
    /// </summary>
    public sealed class CrmOwnedStoreItems
    {
        /// <summary>Profile list property → the store item prefix of its ids (GameOfflineStore.cs GetSignatureList).</summary>
        private static readonly string[][] SignatureLists =
        {
            new[] { "KillSignatureAvatars", "KillSignature_Avatar_" },
            new[] { "KillSignatureBackgrounds", "KillSignature_Background_" },
        };

        private readonly ServerContext context;
        private readonly Log log;
        private readonly object gate = new object();
        private readonly Dictionary<string, long> reads = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, OwnedSet> sets = new Dictionary<string, OwnedSet>(StringComparer.Ordinal);

        public CrmOwnedStoreItems(ServerContext context, Log log)
        {
            this.context = context;
            this.log = log ?? Log.Null;
        }

        /// <summary>EventHub handler for <see cref="StorageService.GameProfileReadTopic"/>: the next document takes a new set.</summary>
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

        /// <summary>
        /// The full document's JSON for <paramref name="accountId"/>: <see cref="CrmDocument.Json"/> without the owned
        /// signatures. Never throws (the whole document is the fallback).
        /// </summary>
        public string JsonFor(CrmDocument doc, string accountId)
        {
            if (doc == null) return null;
            if (string.IsNullOrEmpty(accountId)) return doc.Json;
            try
            {
                OwnedSet set = Current(accountId);
                if (set.Names.Count == 0) return doc.Json;
                lock (set)
                {
                    if (!ReferenceEquals(set.Document, doc))
                    {
                        int omitted;
                        set.Json = doc.JsonWithout(set.Names, out omitted);
                        set.Document = doc;
                        log.Info("CRM: offline_store for " + accountId + " leaves out " + omitted + " owned kill signature(s) ("
                                 + set.Avatars + " avatars, " + set.Backgrounds + " backgrounds owned): "
                                 + doc.Json.Length + " -> " + set.Json.Length + " chars");
                    }
                    return set.Json;
                }
            }
            catch (Exception ex)
            {
                log.Error("CRM: owned kill signatures unavailable for " + accountId + "; serving the whole store", ex);
                return doc.Json;
            }
        }

        private OwnedSet Current(string accountId)
        {
            long read;
            lock (gate)
            {
                reads.TryGetValue(accountId, out read);
                OwnedSet cached;
                if (sets.TryGetValue(accountId, out cached) && cached.Read == read) return cached;
            }
            OwnedSet set = Load(accountId);
            set.Read = read;
            lock (gate) sets[accountId] = set;
            return set;
        }

        private OwnedSet Load(string accountId)
        {
            IProfileAccess profiles;
            if (!context.Features.TryGet(out profiles)) return new OwnedSet();
            string dump = profiles.ReadText(accountId, McvsConstants.GameProfileSelector + ".gameProfile");
            if (string.IsNullOrEmpty(dump)) return new OwnedSet();
            string problem;
            var set = Scan(dump, out problem);
            if (set == null)
            {
                log.Warn("CRM: owned kill signatures of " + accountId + " not read (" + problem + "); serving the whole store");
                return new OwnedSet();
            }
            return set;
        }

        /// <summary>
        /// The store names of the ids in the dump's KillSignatureAvatars/KillSignatureBackgrounds arrays, or null (and
        /// why) when the dump is not shaped as expected: a list that appears twice, or holds anything but ids.
        /// A list that is absent is empty (the Templates writer leaves out default values).
        /// </summary>
        internal static OwnedSet Scan(string dump, out string problem)
        {
            var set = new OwnedSet();
            foreach (string[] list in SignatureLists)
            {
                string key = "\"" + list[0] + "\"";
                int found = 0;
                // A match is a key: inside a string its quotes would be escaped.
                for (int at = dump.IndexOf(key, StringComparison.Ordinal); at >= 0; at = dump.IndexOf(key, at + key.Length, StringComparison.Ordinal))
                {
                    int i = SkipSpace(dump, at + key.Length);
                    if (i >= dump.Length || dump[i] != ':') continue;
                    if (++found > 1)
                    {
                        problem = list[0] + " appears more than once";
                        return null;
                    }
                    int count;
                    if (!ReadIds(dump, SkipSpace(dump, i + 1), list[1], set.Names, out count))
                    {
                        problem = list[0] + " is not an array of ids";
                        return null;
                    }
                    if (list[0] == "KillSignatureAvatars") set.Avatars = count;
                    else set.Backgrounds = count;
                }
            }
            problem = null;
            return set;
        }

        private static bool ReadIds(string text, int i, string prefix, HashSet<string> names, out int count)
        {
            count = 0;
            if (i >= text.Length || text[i] != '[') return false;
            i = SkipSpace(text, i + 1);
            if (i < text.Length && text[i] == ']') return true;
            while (i < text.Length)
            {
                int start;
                string id;
                if (text[i] == '"')
                {
                    start = i + 1;
                    int end = text.IndexOf('"', start);
                    if (end < 0) return false;
                    id = text.Substring(start, end - start);
                    if (id.IndexOf('\\') >= 0) return false;   // ids are digits; anything escaped is not one
                    i = end + 1;
                }
                else if (text[i] == '-' || (text[i] >= '0' && text[i] <= '9'))
                {
                    start = i;
                    while (i < text.Length && (text[i] == '-' || (text[i] >= '0' && text[i] <= '9'))) i++;
                    id = text.Substring(start, i - start);
                }
                else
                {
                    return false;   // an object (a Templates value with attributes) or anything else
                }
                if (id.Length == 0) return false;
                names.Add(prefix + id);
                count++;
                i = SkipSpace(text, i);
                if (i >= text.Length) return false;
                if (text[i] == ']') return true;
                if (text[i] != ',') return false;
                i = SkipSpace(text, i + 1);
            }
            return false;
        }

        private static int SkipSpace(string text, int i)
        {
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n')) i++;
            return i;
        }

        /// <summary>One account's owned signatures as store names, and the document last filtered with them.</summary>
        internal sealed class OwnedSet
        {
            public readonly HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal);
            public int Avatars;
            public int Backgrounds;
            public long Read;
            public CrmDocument Document;
            public string Json;
        }
    }
}
