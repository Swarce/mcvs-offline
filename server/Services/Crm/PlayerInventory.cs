using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>
    /// What a player owns, read from their game-profile dump: the text the TS saves (R4), i.e.
    /// <c>TObject.FromObject(player).ToString("j")</c> (GM/GameProfileSection.cs:105-109). It feeds the store filter
    /// (<c>crm.omitOwnedSignatures</c>) and the collector's per-player cells (economy-collector.md §2.9.1).
    /// <para>
    /// Each part has its own known flag and, when unknown, a text in <see cref="Problems"/> keyed by the part name; an
    /// unknown part leaves the others usable:
    /// <list type="bullet">
    /// <item>avatars, backgrounds: the kill-signature lists with the exact rules the store filter has always used (see
    /// <see cref="OwnedStoreNames"/>); sounds: the same rules, independent of the store.</item>
    /// <item>mercs: the agents of <c>$default./Profile.Mercs</c>, read only when asked (the collector is active).</item>
    /// </list>
    /// </para>
    /// Ids are kept as the game compares them, as strings (<c>List&lt;string&gt;.Contains</c>): a set holds an id only in
    /// its canonical decimal spelling. Scanning never throws.
    /// </summary>
    public sealed class PlayerInventory
    {
        public const string AvatarsPart = "avatars";
        public const string BackgroundsPart = "backgrounds";
        public const string SoundsPart = "sounds";
        public const string MercsPart = "mercs";

        // The profile's signature lists and the store item prefix of their ids (GameOfflineStore.cs GetSignatureList).
        private const string AvatarsList = "KillSignatureAvatars";
        private const string BackgroundsList = "KillSignatureBackgrounds";
        private const string SoundsList = "KillSignatureSounds";
        private const string AvatarPrefix = "KillSignature_Avatar_";
        private const string BackgroundPrefix = "KillSignature_Background_";

        /// <summary>The agent the starting inventory unlocks (MCVS_Meta <c>StartingInventory</c>: <c>UnlockMerc 1</c>).</summary>
        private const int StartingAgent = 1;

        private static readonly string[] MercsPath = { "$default", "/Profile", "Mercs" };
        private static readonly string[] SquadsPath = { "$default", "/Profile", "Squads" };

        public bool AvatarsKnown;
        public bool BackgroundsKnown;
        public bool SoundsKnown;
        public bool MercsKnown;

        public HashSet<int> Avatars = new HashSet<int>();
        public HashSet<int> Backgrounds = new HashSet<int>();
        public HashSet<int> Sounds = new HashSet<int>();

        /// <summary>
        /// The agents of the dump by id, plus agent 1 when the dump does not list it. An agent that is not here is locked
        /// (MercsComponent.ProfileLoaded adds a locked ShortDef for it at load, GM/MercsComponent.cs:2231-2255).
        /// </summary>
        public Dictionary<int, MercState> Mercs = new Dictionary<int, MercState>();

        /// <summary>Why a part is unknown, keyed by <see cref="AvatarsPart"/>, <see cref="BackgroundsPart"/>, ….</summary>
        public IDictionary<string, string> Problems = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The owned avatars and backgrounds as store names, spelled as the dump spells their ids (Scan only).</summary>
        private HashSet<string> storeNames;

        /// <summary>
        /// Reads <paramref name="dump"/>. The agent list is parsed only when <paramref name="parseMercs"/> is true;
        /// otherwise its span is not even looked up and <see cref="MercsKnown"/> stays false.
        /// </summary>
        public static PlayerInventory Scan(string dump, bool parseMercs)
        {
            if (dump == null) dump = "";
            var inventory = new PlayerInventory { storeNames = new HashSet<string>(StringComparer.Ordinal) };
            inventory.AvatarsKnown = inventory.ScanList(dump, AvatarsList, AvatarsPart, AvatarPrefix, inventory.Avatars);
            inventory.BackgroundsKnown = inventory.ScanList(dump, BackgroundsList, BackgroundsPart, BackgroundPrefix, inventory.Backgrounds);
            inventory.SoundsKnown = inventory.ScanList(dump, SoundsList, SoundsPart, null, inventory.Sounds);
            if (!parseMercs)
            {
                inventory.Problems[MercsPart] = "not read (the collector is not active)";
                return inventory;
            }
            string problem = ReadMercs(dump, inventory.Mercs);
            if (problem == null)
            {
                inventory.MercsKnown = true;
            }
            else
            {
                inventory.Mercs.Clear();
                inventory.Problems[MercsPart] = problem;
            }
            return inventory;
        }

        /// <summary>
        /// The store names of the owned avatars and backgrounds (<c>KillSignature_Avatar_&lt;id&gt;</c>,
        /// <c>KillSignature_Background_&lt;id&gt;</c>), or none unless both lists are known: anything unexpected in either
        /// list leaves the whole store, as the store filter always did. Sounds and agents play no part.
        /// </summary>
        public IEnumerable<string> OwnedStoreNames()
        {
            if (!AvatarsKnown || !BackgroundsKnown) return new string[0];
            if (storeNames != null) return storeNames;
            // An inventory built by hand rather than scanned: the canonical names of its ids.
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (int id in Avatars) names.Add(AvatarPrefix + id.ToString(CultureInfo.InvariantCulture));
            foreach (int id in Backgrounds) names.Add(BackgroundPrefix + id.ToString(CultureInfo.InvariantCulture));
            return names;
        }

        /// <summary>
        /// One signature list (PlayerProfileComponent <c>List&lt;string&gt;</c>, GM/PlayerProfileComponent.cs:2024-2032),
        /// found by its key anywhere in the dump: refused (false, and why) when the key appears twice or its value is not
        /// an array of ids. A list that is absent is empty.
        /// </summary>
        private bool ScanList(string dump, string property, string part, string storePrefix, HashSet<int> ids)
        {
            string key = "\"" + property + "\"";
            var read = new List<string>();
            int found = 0;
            // A match is a key: inside a string its quotes would be escaped.
            for (int at = dump.IndexOf(key, StringComparison.Ordinal); at >= 0; at = dump.IndexOf(key, at + key.Length, StringComparison.Ordinal))
            {
                int i = SkipSpace(dump, at + key.Length);
                if (i >= dump.Length || dump[i] != ':') continue;
                if (++found > 1)
                {
                    Problems[part] = property + " appears more than once";
                    return false;
                }
                if (!ReadIds(dump, SkipSpace(dump, i + 1), read))
                {
                    Problems[part] = property + " is not an array of ids";
                    return false;
                }
            }
            foreach (string id in read)
            {
                int n;
                if (TryCanonical(id, out n)) ids.Add(n);
                if (storePrefix != null) storeNames.Add(storePrefix + id);
            }
            return true;
        }

        private static bool ReadIds(string text, int i, List<string> ids)
        {
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
                ids.Add(id);
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

        /// <summary>
        /// The agents of <c>$default./Profile.Mercs</c> (<c>List&lt;Merc.ShortDef&gt;</c>, GM/PlayerProfileComponent.cs:1257)
        /// into <paramref name="mercs"/>, or why the list is not usable. The Templates writer serializes every public
        /// read/write property of a ShortDef (Gameloft.Templates DefaultConverter.KeepProperty; the shape is pinned by
        /// <c>PlayerInventoryTests.WriterMercsFixture</c>); a property that is absent takes the ShortDef default
        /// (GM/Merc.cs:14-84: <c>Locked</c> true, <c>Skins</c> ["1"], <c>Camos</c> ["1"], <c>Accessories</c> []).
        /// Only <c>$default./Profile.Mercs</c> is read: <c>Squads[].Mercs</c> lists squad members, not the agents.
        /// </summary>
        private static string ReadMercs(string dump, Dictionary<int, MercState> mercs)
        {
            int start, length;
            if (!JsonSpanScanner.TryFindValue(dump, MercsPath, out start, out length)) return "no agent list at $default./Profile.Mercs";
            object value;
            if (!MiniJson.TryParse(dump.Substring(start, length), out value)) return "Mercs is not valid JSON";
            var list = value as List<object>;
            if (list == null) return "Mercs is not an array";
            foreach (object element in list)
            {
                var merc = element as Dictionary<string, object>;
                if (merc == null) return "Mercs holds something other than agent objects";
                int id;
                if (!TryId(JsonValue.Get(merc, "Id"), out id)) return "an agent's Id is missing or not an id";
                if (mercs.ContainsKey(id)) return "agent " + Text(id) + " appears more than once";
                var state = new MercState();
                object locked;
                if (merc.TryGetValue("Locked", out locked))
                {
                    bool? isLocked = ReadBool(locked);
                    if (isLocked == null) return "agent " + Text(id) + ": Locked is not a bool";
                    state.Unlocked = !isLocked.Value;
                }
                if (!ReadIdList(merc, "Skins", true, state.Skins)) return "agent " + Text(id) + ": Skins is not a list of ids";
                if (!ReadIdList(merc, "Camos", true, state.Camos)) return "agent " + Text(id) + ": Camos is not a list of ids";
                if (!ReadIdList(merc, "Accessories", false, state.Accessories)) return "agent " + Text(id) + ": Accessories is not a list of ids";
                mercs[id] = state;
            }
            string problem = CheckSquads(dump, mercs);
            if (problem != null) return problem;
            if (!mercs.ContainsKey(StartingAgent))
            {
                // Not saved yet since the TS loaded the starting inventory (DefaultProfile.tx has "Mercs": []).
                var starting = new MercState { Unlocked = true };
                starting.Skins.Add(1);
                starting.Camos.Add(1);
                mercs[StartingAgent] = starting;
            }
            return null;
        }

        /// <summary>
        /// Plausibility guard: a squad member that the agent list holds must read unlocked; if it does not, our reading of
        /// the list is wrong. <c>Squads</c> is in DefaultProfile.tx (<c>[{"Mercs":["1"]}]</c>) but is no property of
        /// PlayerProfileComponent in 1.17.32 (the engine's ErrorHandler skips it on load), so a dump the TS wrote has none
        /// and the guard only speaks for template-shaped dumps. An unreadable <c>Squads</c> says nothing about the agents.
        /// </summary>
        private static string CheckSquads(string dump, Dictionary<int, MercState> mercs)
        {
            // The common case, a dump the TS wrote, is decided without walking the profile again.
            if (dump.IndexOf("\"Squads\"", StringComparison.Ordinal) < 0) return null;
            int start, length;
            if (!JsonSpanScanner.TryFindValue(dump, SquadsPath, out start, out length)) return null;
            object value;
            if (!MiniJson.TryParse(dump.Substring(start, length), out value)) return null;
            var squads = value as List<object>;
            if (squads == null) return null;
            foreach (object squad in squads)
            {
                List<object> members = JsonValue.GetArray(squad as Dictionary<string, object>, "Mercs");
                if (members == null) continue;
                foreach (object member in members)
                {
                    int id;
                    MercState state;
                    if (TryId(member, out id) && mercs.TryGetValue(id, out state) && !state.Unlocked)
                        return "squad agent " + Text(id) + " reads locked: unexpected Mercs shape";
                }
            }
            return null;
        }

        /// <summary>
        /// The ids of an agent's list property into <paramref name="ids"/>; an absent property takes the ShortDef default
        /// (["1"] when <paramref name="defaultOne"/>, else []).
        /// </summary>
        private static bool ReadIdList(Dictionary<string, object> merc, string key, bool defaultOne, HashSet<int> ids)
        {
            object value;
            if (!merc.TryGetValue(key, out value))
            {
                if (defaultOne) ids.Add(1);
                return true;
            }
            var list = value as List<object>;
            if (list == null) return false;
            foreach (object element in list)
            {
                int id;
                if (!TryId(element, out id)) return false;
                ids.Add(id);
            }
            return true;
        }

        /// <summary>An id as the writer emits it (a string) or as a JSON integer.</summary>
        private static bool TryId(object value, out int id)
        {
            id = 0;
            string s = value as string;
            if (s != null) return TryCanonical(s, out id);
            if (!(value is long)) return false;
            long l = (long)value;
            if (l < Num.IntMin || l > Num.IntMax) return false;
            id = (int)l;
            return true;
        }

        /// <summary>True when <paramref name="text"/> is the canonical decimal spelling of an int ("7", not "07" or "+7").</summary>
        private static bool TryCanonical(string text, out int id)
        {
            return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id)
                   && string.Equals(id.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        }

        private static bool? ReadBool(object value)
        {
            if (value is bool) return (bool)value;
            string s = value as string;
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return null;
        }

        private static string Text(int id)
        {
            return id.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>One agent of the dump (Merc.ShortDef, GM/Merc.cs:10-96): unlocked or not, and the ids it owns.</summary>
    public sealed class MercState
    {
        public bool Unlocked;
        public HashSet<int> Skins = new HashSet<int>();
        public HashSet<int> Camos = new HashSet<int>();
        public HashSet<int> Accessories = new HashSet<int>();
    }
}
