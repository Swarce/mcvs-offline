using System;
using System.Collections.Generic;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>
    /// The game's profile lock left by an earlier start (build 32). <c>PlayerProfileComponent.ProfileOwnerRoomId</c> is saved in
    /// the game-profile dump (<c>$default./Profile.ProfileOwnerRoomId</c>). The TS sets it to its room's id and saves
    /// (<c>GameServerComponent.TakeProfileLock</c>, <c>ResumeServer</c>) and sets it back to null and saves when the player
    /// leaves (<c>SaveAndReleaseProfile</c>: <c>CloseConnection</c>, <c>CloseConnectionAsync</c>, <c>DoPauseServer</c>). A start
    /// that ended any other way (the app killed, the restart from the settings page) leaves the id in the dump, and the next
    /// start's TS (<c>MakeSureProfileIsUnlocked</c>) posts <c>multipleConnectionDetected</c>, kicks the user from that room and
    /// reloads the profile every second for 5 s (<c>MultipleConnectionGracePeriodTime</c>) before it takes the lock anyway:
    /// the build-31 log's two 3.7 s reloads at boot.
    /// <para>
    /// The room ids are <c>LocalAnubis-Room-</c> + a new GUID (<c>Gameloft.Engine.LocalAnubis.Lobby.GenerateRoomId</c>), made
    /// by the game in its own process, and the game writes the dump only through this server's storage endpoints. So when the
    /// game client of this server's process is the only one using it (the embedded host's server started with the game:
    /// <see cref="Core.ClientCapabilities.InProcessClient"/>; not a server that took the port over from another copy of the game,
    /// not in LAN mode), the owner the dump held before the game's first storage request of this process (its first read of the
    /// dump, or its first write to the account, whichever comes first) was written by an earlier process, and no room of this
    /// process can have that id. While the stored owner is still exactly that id, a read of <c>_mcvs.gameProfile</c> is answered with the
    /// value replaced by <c>null</c>, which is what the game itself stores on <c>SaveAndReleaseProfile</c> (the Templates writer
    /// writes a null string as <c>null</c>): the stored file is not changed. Any other owner (one the game wrote in this
    /// process) and anything unexpected (no dump, a dump that is not the expected shape, a key twice, an owner that is not a
    /// string) are answered unchanged, the game's own behaviour.
    /// </para>
    /// </summary>
    public sealed class StaleProfileOwner
    {
        public const string DefaultKey = "$default";
        public const string ProfileKey = "/Profile";
        public const string OwnerKey = "ProfileOwnerRoomId";
        public const string TypeKey = "@type";
        public const string ProfileType = "PlayerProfileComponent";

        private readonly object gate = new object();
        // account -> the owner its dump held before the game's first request of this process (null: none or not readable).
        private readonly Dictionary<string, string> inherited = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>True once the account's owner at this process's first request is known.</summary>
        public bool IsCaptured(string accountId)
        {
            lock (gate) return inherited.ContainsKey(accountId);
        }

        /// <summary>The owner captured for the account (null: none, unreadable, or not captured yet).</summary>
        public string Inherited(string accountId)
        {
            lock (gate)
            {
                string owner;
                return inherited.TryGetValue(accountId, out owner) ? owner : null;
            }
        }

        /// <summary>
        /// Before a write by the game to the account: the first one captures the owner of the stored dump as it is before the
        /// write (<paramref name="readDump"/>: the stored <c>_mcvs.gameProfile</c> text, or null). Later calls do nothing.
        /// </summary>
        public void BeforeWrite(string accountId, Func<string> readDump)
        {
            if (string.IsNullOrEmpty(accountId) || readDump == null) return;
            lock (gate)
            {
                if (inherited.ContainsKey(accountId)) return;
                string dump = null;
                try
                {
                    dump = readDump();
                }
                catch (Exception)
                {
                    // Unreadable: nothing is inherited, nothing will be cleared (the game's behaviour).
                }
                int start, length;
                string owner;
                inherited[accountId] = TryFindOwner(dump, out start, out length, out owner) ? owner : null;
            }
        }

        /// <summary>
        /// A read of the account's <c>_mcvs.gameProfile</c> answered <paramref name="dump"/>. The first request of the process
        /// captures its owner. Returns the text to answer: <paramref name="dump"/> itself, or, when <paramref name="clear"/> and the
        /// stored owner is the captured one, the same text with that owner's string replaced by <c>null</c>
        /// (<paramref name="cleared"/> is then that owner, and <paramref name="firstTime"/> says whether it is the first time for
        /// this account and owner).
        /// </summary>
        public string OnRead(string accountId, string dump, bool clear, out string cleared, out bool firstTime)
        {
            cleared = null;
            firstTime = false;
            if (string.IsNullOrEmpty(accountId)) return dump;
            string old;
            lock (gate)
            {
                // Already captured and nothing to look for (nothing inherited, the setting off, or a dump that cannot hold the
                // inherited owner): the answer below would be the dump itself, so it is given without scanning the dump.
                if (inherited.TryGetValue(accountId, out old) && (old == null || !clear || !MayHold(dump, old))) return dump;
            }
            int start, length;
            string owner;
            bool found = TryFindOwner(dump, out start, out length, out owner);
            lock (gate)
            {
                if (!inherited.TryGetValue(accountId, out old))
                {
                    old = found ? owner : null;
                    inherited[accountId] = old;
                }
                if (!clear || !found || owner == null || old == null || !string.Equals(owner, old, StringComparison.Ordinal)) return dump;
                firstTime = reported.Add(accountId + "\n" + owner);
            }
            cleared = owner;
            return Release(dump, start, length);
        }

        /// <summary>
        /// False only when no JSON string of <paramref name="dump"/> can decode to <paramref name="owner"/>: the dump has no backslash
        /// (so no string in it has an escape, and each one's text is its value) and does not contain the owner's text. Then
        /// <see cref="TryFindOwner"/> cannot find that owner, and <see cref="OnRead"/> answers the dump as it is.
        /// </summary>
        internal static bool MayHold(string dump, string owner)
        {
            if (string.IsNullOrEmpty(dump)) return false;
            return dump.IndexOf('\\') >= 0 || dump.IndexOf(owner, StringComparison.Ordinal) >= 0;
        }

        /// <summary>The dump with the value span [start, start + length) replaced by the JSON literal null.</summary>
        public static string Release(string dump, int start, int length)
        {
            return dump.Substring(0, start) + "null" + dump.Substring(start + length);
        }

        /// <summary>
        /// Finds <c>$default./Profile.ProfileOwnerRoomId</c> in a game-profile dump: each key exactly once in its object (and no
        /// other key there that the Templates reader could take for it: the same name in another case, or the name followed by
        /// '.' or '@', with a leading '.' or in single quotes), <c>/Profile</c> an object whose <c>@type</c>, when present, is a string ending in
        /// <c>PlayerProfileComponent</c>, and the owner a JSON string (<paramref name="owner"/> decoded) or <c>null</c>
        /// (<paramref name="owner"/> null). False for anything else (then nothing is changed).
        /// </summary>
        public static bool TryFindOwner(string dump, out int start, out int length, out string owner)
        {
            start = 0;
            length = 0;
            owner = null;
            if (string.IsNullOrEmpty(dump)) return false;
            var members = new List<JsonSpanScanner.Member>();
            int pos = JsonSpanScanner.SkipLeading(dump, 0);
            JsonSpanScanner.Member member;
            if (!JsonSpanScanner.TryReadMembers(dump, pos, members) || !FindOnly(members, DefaultKey, out member)) return false;
            if (!JsonSpanScanner.TryReadMembers(dump, member.ValueStart, members) || !FindOnly(members, ProfileKey, out member)) return false;
            int profile = member.ValueStart;
            if (!JsonSpanScanner.TryReadMembers(dump, profile, members)) return false;
            bool typeSeen = false;
            foreach (JsonSpanScanner.Member m in members)
            {
                if (!string.Equals(m.Name, TypeKey, StringComparison.Ordinal)) continue;
                string type;
                if (typeSeen || !JsonSpanScanner.TryReadString(dump, m.ValueStart, m.ValueLength, out type) || type == null ||
                    !type.EndsWith(ProfileType, StringComparison.Ordinal))
                    return false;
                typeSeen = true;
            }
            if (!FindOnly(members, OwnerKey, out member)) return false;
            if (member.ValueLength == 4 && string.CompareOrdinal(dump, member.ValueStart, "null", 0, 4) == 0)
            {
                start = member.ValueStart;
                length = 4;
                return true;
            }
            string value;
            if (!JsonSpanScanner.TryReadString(dump, member.ValueStart, member.ValueLength, out value)) return false;
            start = member.ValueStart;
            length = member.ValueLength;
            owner = value;
            return true;
        }

        /// <summary>'a'… → a… (the first single-quoted part without its quotes); anything else unchanged.</summary>
        private static string Unquote(string name)
        {
            if (name.Length < 2 || name[0] != '\'') return name;
            int close = name.IndexOf('\'', 1);
            return close < 0 ? name : name.Substring(1, close - 1) + name.Substring(close + 1);
        }

        /// <summary>An object's <paramref name="members"/> have exactly one <paramref name="key"/> and nothing that resembles it.</summary>
        private static bool FindOnly(List<JsonSpanScanner.Member> members, string key, out JsonSpanScanner.Member found)
        {
            found = new JsonSpanScanner.Member();
            int count = 0;
            foreach (JsonSpanScanner.Member m in members)
            {
                string name = m.Name ?? "";
                if (string.Equals(name, key, StringComparison.Ordinal))
                {
                    found = m;
                    count++;
                    continue;
                }
                // The reader's other spellings of a name: a leading '.', single quotes ('a'), a path or an attribute after it.
                string bare = Unquote(name.TrimStart('.'));
                if (string.Equals(bare, key, StringComparison.OrdinalIgnoreCase)) return false;
                if (bare.Length > key.Length && bare.StartsWith(key, StringComparison.OrdinalIgnoreCase) &&
                    (bare[key.Length] == '.' || bare[key.Length] == '@'))
                    return false;
            }
            return count == 1;
        }
    }
}
