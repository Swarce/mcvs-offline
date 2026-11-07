using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core.Names;

namespace McvsOffline.Server.Services.Leaderboards
{
    /// <summary>
    /// The name, emblem and emblem background a generated rival shows (<see cref="VirtualPlayers"/>,
    /// <see cref="SeasonRivals"/>; docs/LIVEOPS.md "Rival names and emblems"). All three are a pure function of the rival's
    /// id (its board and slot), through <see cref="GamerTags"/>: the same rival keeps them across sessions and on both
    /// platforms. Names are unique on a board, also as a player reads them (<see cref="GamerTags.Skeleton"/>): <see cref="Taken"/>
    /// starts with the real players' names, and rivals are named in slot order, each taking the first free draw for its id.
    /// <para>
    /// The client builds a row's badge from <c>_killSignatureEmblem</c>, <c>_killSignatureBg</c> and <c>_level</c>, and falls
    /// back to the viewer's own emblem and background when the fields are missing (GM/LeaderboardsComponent.cs
    /// GetEntriesObject; PlayerProfileComponent.GetSignatureObject looks the ids up as strings), so both are sent as
    /// strings, as the game posts its own (GetCustomPostData). <c>_avatar</c>, <c>_leagueId</c> and the clan fields stay out:
    /// the client then shows the default profile picture, no league and no clan.
    /// </para>
    /// </summary>
    public static class RivalIdentity
    {
        /// <summary>Hash scope of rival names (versioned: a new scope renames every rival).</summary>
        public const string NameScope = "mcvs-offline/rival-name/v1";

        /// <summary>Hash scope of rival emblems and backgrounds.</summary>
        public const string EmblemScope = "mcvs-offline/rival-emblem/v1";

        /// <summary>
        /// The names already on a board (the real players'), compared as a player reads them: ignoring case, and by
        /// <see cref="GamerTags.Skeleton"/> (no rival is "Frost27" beside a player "frost-27").
        /// </summary>
        public static HashSet<string> Taken(IEnumerable<string> realNames)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (realNames != null)
                foreach (string name in realNames)
                    GamerTags.Reserve(taken, name);   // a second real player with the same skeleton is blocked by the first's
            return taken;
        }

        /// <summary>The rival's name: the first draw for <paramref name="id"/> that is not taken yet (added to <paramref name="taken"/>).</summary>
        public static string Name(GamerTags tags, string id, HashSet<string> taken)
        {
            return (tags ?? GamerTags.BuiltIn).UniqueName(NameScope, id, taken);
        }

        /// <summary>Sets <c>_killSignatureEmblem</c> and <c>_killSignatureBg</c> (ids as strings) on a rival entry.</summary>
        public static void SetEmblem(Dictionary<string, object> entry, GamerTags tags, string id)
        {
            int avatar, background;
            (tags ?? GamerTags.BuiltIn).Emblem(EmblemScope, id, out avatar, out background);
            entry["_killSignatureEmblem"] = avatar.ToString(CultureInfo.InvariantCulture);
            entry["_killSignatureBg"] = background.ToString(CultureInfo.InvariantCulture);
        }
    }
}
