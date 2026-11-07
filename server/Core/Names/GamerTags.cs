using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Core.Names
{
    /// <summary>
    /// Gamer tags for the players the server makes up: the leaderboard rivals (Services/Leaderboards/RivalIdentity) and the
    /// in-match bots (Services/Crm/CrmBotNames). The curated names, parts, styles, limits, blocklist and the rivals' emblem
    /// pools come from server/Content/names.json (tools/content-gen <c>build_names</c> from <c>names_tuning.json</c> and
    /// <c>names_curated.txt</c>; a file of the same name in &lt;data&gt;/content/ overrides it, a broken one is refused and
    /// the built-in file used).
    /// <para>
    /// Build 29: a name comes first from the curated list (<see cref="Curated"/>, about 2,000 names written by us: puns,
    /// memes, other games' characters, brands, streamer handles, clan tags; the owner found the generated names generic).
    /// Each id draws its own order through the list from a hash of (scope, id) and takes the first name the board or window
    /// does not hold yet (<see cref="UniqueName"/>); only when every curated name is taken (a board of more than about
    /// 2,000 players) does it fall back to the build-28 generator below.
    /// </para>
    /// <para>
    /// A generated name is a pure function of (scope, id, attempt): FNV-1a 64 over UTF-8 seeds xorshift64*, which draws the
    /// style (by weight), each part, each number and the leet letters. Integer arithmetic only, invariant culture: the same
    /// id gives the same name on every runtime, on Android and on PC. A draw the limits or the blocklist refuse, or one
    /// already taken, is replaced by the next attempt.
    /// </para>
    /// <para>
    /// What the client takes and shows (docs/LIVEOPS.md 5.1, <see cref="ShapeProblem"/>): 2..16 characters (the rename
    /// popup takes 16 and has no character restriction, PlayerProfileComponent needs 2), ASCII letters and digits, the
    /// space, the signs <c> !%'()*+,-.?@[]^_|~</c> and the Latin-1 letters (the Latin font TCFNoli-Black has them all;
    /// FormatName picks a font per character). Never '&lt;', '&gt;' or '&amp;' (FormatName and gameswf read them as markup),
    /// ':' (a bot's credential is "dev:&lt;name&gt;" and Credential throws on a second ':'), '{' or '}' (the localization
    /// formatter reads them as placeholders), '+', '-' or '*' first (a CRM cell operator) or text a number parser reads (the
    /// client's CRM cells turn it into a number, and its bot list reads names as strings). Generated names keep the build-28
    /// shape on top: 3..16 characters of <c>[A-Za-z0-9_.-]</c>, a letter or digit at both ends, never two separators in a
    /// row, at least two letters.
    /// </para>
    /// </summary>
    public sealed class GamerTags
    {
        public const string FileName = "names.json";

        /// <summary>The <c>format</c> this server reads (content-gen <c>FORMAT</c>).</summary>
        public const int Format = 1;

        /// <summary>The client's longest player name (popups/PopupChangeName.as maxChars).</summary>
        public const int ClientMaxLength = 16;

        /// <summary>The client's shortest player name (PlayerProfileComponent MinimalPlayerNameLength).</summary>
        public const int ClientMinLength = 2;

        /// <summary>Attempts <see cref="UniqueName"/> makes before it falls back to the default style.</summary>
        public const int MaxAttempts = 64;

        /// <summary>Hashed draws from the curated list before <see cref="UniqueName"/> walks it in order from the last one.</summary>
        public const int CuratedDraws = 16;

        /// <summary>The signs a name may hold besides ASCII letters, digits and Latin-1 letters (<see cref="ShapeProblem"/>).</summary>
        public const string NameSigns = " !%'()*+,-.?@[]^_|~";

        /// <summary>Latin-1 letters U+00C0..U+00DF as a player reads them; U+00E0..U+00FF are the same in lower case.</summary>
        private static readonly string[] Latin1Fold = "a|a|a|a|a|a|ae|c|e|e|e|e|i|i|i|i|d|n|o|o|o|o|o||o|u|u|u|u|y|th|ss".Split('|');

        private const string Separators = "_.-";

        private static readonly string[] ListNames = { "adj", "noun", "handle", "short", "foreign", "prefix", "suffix", "mascot", "country" };

        /// <summary>Prefix of the skeleton keys <see cref="Reserve"/> adds to a taken set (no name holds this character).</summary>
        private const char SkeletonMark = '\u0001';

        private static readonly object Gate = new object();
        private static string cachedHash;
        private static GamerTags cached;
        private static string refusedHash;
        private static GamerTags builtIn;

        private readonly Dictionary<string, string[]> parts = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private readonly List<Style> styles = new List<Style>();
        private readonly Dictionary<char, char> leet = new Dictionary<char, char>();
        private readonly List<string> substrings = new List<string>();
        private readonly HashSet<string> substringSet = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> words = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<int> wordLengths = new List<int>();
        private readonly List<string> digits = new List<string>();
        private readonly HashSet<string> digitRuns = new HashSet<string>(StringComparer.Ordinal);
        private string[] curated = new string[0];
        private string[] curatedKeys = new string[0];
        private readonly HashSet<string> curatedSet = new HashSet<string>(StringComparer.Ordinal);
        private int totalWeight;
        private int yearFirst;
        private int yearLast;

        private GamerTags()
        {
        }

        /// <summary>names.json <c>version</c>: part of every hash, so bumping it renames everyone on purpose.</summary>
        public int Version { get; private set; }

        public int MinLength { get; private set; }

        public int MaxLength { get; private set; }

        /// <summary>Per cent of rivals that keep the starting emblem and background (1, 1).</summary>
        public int DefaultShare { get; private set; }

        /// <summary>The emblem ids (KillsignatureAvatars) a rival may show, ascending.</summary>
        public int[] Avatars { get; private set; }

        /// <summary>The background ids (KillsignatureBackgrounds) a rival may show, ascending.</summary>
        public int[] Backgrounds { get; private set; }

        /// <summary>FNV-1a 64 of the source text (hex): changes whenever the content does.</summary>
        public string Hash { get; private set; }

        /// <summary>Where the content came from ("names.json", "built-in names.json", "fallback").</summary>
        public string Source { get; private set; }

        /// <summary>The styles' ids, in file order.</summary>
        public List<string> StyleIds
        {
            get
            {
                var ids = new List<string>();
                foreach (Style s in styles) ids.Add(s.Id);
                return ids;
            }
        }

        /// <summary>The curated names in file order (names.json <c>curated</c>; empty when the file has none).</summary>
        public string[] Curated
        {
            get { return (string[])curated.Clone(); }
        }

        /// <summary>Whether <paramref name="name"/> is one of the curated names (exactly as written).</summary>
        public bool IsCurated(string name)
        {
            return name != null && curatedSet.Contains(name);
        }

        /// <summary>A part list ("adj", "noun", ...), or an empty array.</summary>
        public string[] Part(string list)
        {
            string[] values;
            return parts.TryGetValue(list ?? "", out values) ? (string[])values.Clone() : new string[0];
        }

        // ------------------------------------------------------------------------------------------------ loading

        /// <summary>
        /// names.json as the content provider has it (the &lt;data&gt;/content override first), parsed once per distinct
        /// text. A file that does not parse or breaks a rule is logged once and replaced by the built-in file. Never null.
        /// </summary>
        public static GamerTags For(IContentProvider content, Log log)
        {
            string text;
            if (content == null || !content.TryReadContentText(FileName, out text)) return BuiltIn;
            string hash = Fnv64(text).ToString("x16", CultureInfo.InvariantCulture);
            GamerTags shipped = BuiltIn;
            if (shipped.Hash == hash) return shipped;
            lock (Gate)
            {
                if (cached != null && cachedHash == hash) return cached;
                if (refusedHash == hash) return BuiltIn;
            }
            string problem;
            GamerTags parsed = Parse(text, out problem);
            lock (Gate)
            {
                if (parsed == null)
                {
                    if (refusedHash != hash && log != null)
                        log.Error(FileName + " refused (" + problem + "); the rivals and bots use the built-in " + FileName);
                    refusedHash = hash;
                    return BuiltIn;
                }
                parsed.Source = FileName;
                cached = parsed;
                cachedHash = hash;
                return parsed;
            }
        }

        /// <summary>The names.json compiled into the server, or a small fallback when that is missing or broken.</summary>
        public static GamerTags BuiltIn
        {
            get
            {
                lock (Gate)
                {
                    if (builtIn != null) return builtIn;
                }
                GamerTags result = null;
                byte[] bytes;
                if (BuiltInContent.All.TryGetValue(FileName, out bytes))
                {
                    string problem;
                    result = Parse(new UTF8Encoding(false).GetString(bytes), out problem);
                    if (result != null) result.Source = "built-in " + FileName;
                }
                if (result == null)
                {
                    string ignored;
                    result = Parse(FallbackJson, out ignored);
                    result.Source = "fallback";
                }
                lock (Gate)
                {
                    if (builtIn == null) builtIn = result;
                    return builtIn;
                }
            }
        }

        /// <summary>A minimal file for a server built without names.json: one style, a few words, the starting emblem.</summary>
        private const string FallbackJson =
            "{\"format\":1,\"version\":1,\"limits\":{\"min\":3,\"max\":16},\"years\":[1990,2012],\"defaultShare\":100,"
            + "\"parts\":{\"adj\":[\"Silent\",\"Swift\",\"Rapid\",\"Steel\"],\"noun\":[\"Fox\",\"Hawk\",\"Raven\",\"Lynx\"],\"handle\":[\"Kryptik\"],"
            + "\"short\":[\"Vex\"],\"foreign\":[\"Kaze\"],\"prefix\":[\"Sir\"],\"suffix\":[\"GG\"],\"country\":[\"FR\"]},"
            + "\"styles\":[{\"id\":\"adjNoun_d2\",\"pattern\":\"{adj}{noun}{d2}\",\"case\":\"asis\",\"leet\":false,\"weight\":1}],"
            + "\"leet\":{\"a\":\"4\",\"e\":\"3\",\"i\":\"1\",\"o\":\"0\",\"s\":\"5\",\"t\":\"7\"},"
            + "\"blocklist\":{\"substrings\":[\"fuck\"],\"words\":[\"ass\"],\"digits\":[\"88\"],\"digitRuns\":[\"14\"]},"
            + "\"emblems\":{\"avatars\":\"1\",\"backgrounds\":\"1\"}}";

        /// <summary>The parsed file, or null with the reason.</summary>
        public static GamerTags Parse(string text, out string problem)
        {
            problem = null;
            object parsed;
            if (string.IsNullOrEmpty(text) || !MiniJson.TryParse(text, out parsed) || !(parsed is Dictionary<string, object>))
            {
                problem = "not a JSON object";
                return null;
            }
            try
            {
                var tags = new GamerTags();
                tags.Load((Dictionary<string, object>)parsed);
                tags.Hash = Fnv64(text).ToString("x16", CultureInfo.InvariantCulture);
                return tags;
            }
            catch (FormatException ex)
            {
                problem = ex.Message;
                return null;
            }
        }

        private void Load(Dictionary<string, object> doc)
        {
            if (JsonValue.AsLong(JsonValue.Get(doc, "format")) != Format) throw new FormatException("format must be " + Format);
            Version = (int)Require(JsonValue.AsLong(JsonValue.Get(doc, "version")), 1, 1000000, "version");
            var limits = JsonValue.GetObject(doc, "limits");
            MinLength = (int)Require(JsonValue.AsLong(JsonValue.Get(limits, "min")), ClientMinLength, ClientMaxLength, "limits.min");
            MaxLength = (int)Require(JsonValue.AsLong(JsonValue.Get(limits, "max")), MinLength, ClientMaxLength, "limits.max");
            var years = JsonValue.GetArray(doc, "years");
            if (years == null || years.Count != 2) throw new FormatException("years must be [first, last]");
            yearFirst = (int)Require(JsonValue.AsLong(years[0]), 1950, 2030, "years[0]");
            yearLast = (int)Require(JsonValue.AsLong(years[1]), yearFirst, 2030, "years[1]");
            DefaultShare = (int)Require(JsonValue.AsLong(JsonValue.Get(doc, "defaultShare")), 0, 100, "defaultShare");

            var leetMap = JsonValue.GetObject(doc, "leet") ?? new Dictionary<string, object>();
            foreach (var kv in leetMap)
            {
                string to = JsonValue.AsString(kv.Value);
                if (kv.Key.Length != 1 || kv.Key[0] < 'a' || kv.Key[0] > 'z' || to == null || to.Length != 1 || to[0] < '0' || to[0] > '9')
                    throw new FormatException("leet maps one lower-case letter to one digit");
                leet[kv.Key[0]] = to[0];
            }

            var block = JsonValue.GetObject(doc, "blocklist");
            ReadStrings(JsonValue.GetArray(block, "substrings"), "blocklist.substrings", 3, substrings);
            foreach (string s in substrings) substringSet.Add(s);
            var wordList = new List<string>();
            ReadStrings(JsonValue.GetArray(block, "words"), "blocklist.words", 2, wordList);
            foreach (string w in wordList)
            {
                words.Add(w);
                if (!wordLengths.Contains(w.Length)) wordLengths.Add(w.Length);
            }
            wordLengths.Sort();
            ReadStrings(JsonValue.GetArray(block, "digits"), "blocklist.digits", 1, digits);
            var runs = new List<string>();
            ReadStrings(JsonValue.GetArray(block, "digitRuns"), "blocklist.digitRuns", 1, runs);
            foreach (string r in runs) digitRuns.Add(r);

            var partsObj = JsonValue.GetObject(doc, "parts");
            foreach (string list in ListNames)
            {
                var values = JsonValue.GetArray(partsObj, list);
                if (values == null || values.Count == 0) throw new FormatException("parts." + list + " must be a non-empty list");
                var words2 = new string[values.Count];
                for (int i = 0; i < values.Count; i++)
                {
                    string w = JsonValue.AsString(values[i]);
                    if (!IsPart(w)) throw new FormatException("parts." + list + ": '" + w + "' must be 1-12 ASCII letters or digits, starting with a letter");
                    string reason = BlockedReason(new List<string> { w });
                    if (reason != null) throw new FormatException("parts." + list + ": '" + w + "' is refused by the blocklist (" + reason + ")");
                    words2[i] = w;
                }
                parts[list] = words2;
            }

            ReadCurated(JsonValue.Get(doc, "curated"));

            var styleList = JsonValue.GetArray(doc, "styles");
            if (styleList == null || styleList.Count == 0) throw new FormatException("styles must be a non-empty list");
            foreach (object s in styleList)
            {
                Style style = ParseStyle(JsonValue.AsObject(s));
                styles.Add(style);
                totalWeight += style.Weight;
            }

            var emblems = JsonValue.GetObject(doc, "emblems");
            Avatars = Ranges(JsonValue.GetString(emblems, "avatars"), 1, 100000, "emblems.avatars");
            Backgrounds = Ranges(JsonValue.GetString(emblems, "backgrounds"), 1, 1000, "emblems.backgrounds");
        }

        /// <summary>
        /// The curated names (optional: a file without them names everyone with the generator). Each must pass
        /// <see cref="ShapeProblem"/> and <see cref="CuratedBlockedReason"/>, and no two may read alike (by name ignoring
        /// case or by <see cref="Skeleton"/>: the second could never stand beside the first).
        /// </summary>
        private void ReadCurated(object value)
        {
            if (value == null) return;
            var list = value as List<object>;
            if (list == null) throw new FormatException("curated must be a list of names");
            var names = new string[list.Count];
            var keys = new string[list.Count];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i++)
            {
                string name = JsonValue.AsString(list[i]);
                string problem = ShapeProblem(name);
                if (problem != null) throw new FormatException("curated: '" + name + "' " + problem);
                problem = CuratedBlockedReason(name);
                if (problem != null) throw new FormatException("curated: '" + name + "' is refused by the blocklist (" + problem + ")");
                string skeleton = Skeleton(name);
                keys[i] = skeleton.Length == 0 ? null : SkeletonMark + skeleton;
                if (!seen.Add(name) || (keys[i] != null && !seen.Add(keys[i])))
                    throw new FormatException("curated: '" + name + "' reads like an earlier name");
                names[i] = name;
                curatedSet.Add(name);
            }
            curated = names;
            curatedKeys = keys;
        }

        private Style ParseStyle(Dictionary<string, object> s)
        {
            string id = JsonValue.GetString(s, "id");
            string pattern = JsonValue.GetString(s, "pattern");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(pattern)) throw new FormatException("a style needs an id and a pattern");
            var style = new Style { Id = id };
            style.Case = JsonValue.GetString(s, "case", "asis");
            if (style.Case != "asis" && style.Case != "lower" && style.Case != "upper" && style.Case != "camel")
                throw new FormatException("style " + id + ": unknown case " + style.Case);
            style.Leet = JsonValue.GetBool(s, "leet", false);
            style.Weight = (int)Require(JsonValue.AsLong(JsonValue.Get(s, "weight") ?? 1L), 1, 100, "style " + id + " weight");
            int pos = 0;
            int letters = 0;
            bool hasList = false;
            while (pos < pattern.Length)
            {
                int open = pattern.IndexOf('{', pos);
                if (open < 0) open = pattern.Length;
                if (open > pos)
                {
                    string literal = pattern.Substring(pos, open - pos);
                    foreach (char ch in literal)
                    {
                        if (!IsNameChar(ch) || ch == '}') throw new FormatException("style " + id + ": literal '" + literal + "' outside A-Z a-z 0-9 _ . -");
                        if (IsLetter(ch)) letters++;
                    }
                    style.Pieces.Add(new Piece { Kind = PieceKind.Text, Value = literal });
                }
                if (open >= pattern.Length) break;
                int close = pattern.IndexOf('}', open);
                if (close < 0) throw new FormatException("style " + id + ": unclosed token");
                string token = pattern.Substring(open + 1, close - open - 1);
                if (Array.IndexOf(ListNames, token) >= 0)
                {
                    style.Pieces.Add(new Piece { Kind = PieceKind.List, Value = token });
                    hasList = true;
                }
                else if (token == "d2" || token == "d3" || token == "d4" || token == "year" || token == "alias")
                {
                    style.Pieces.Add(new Piece { Kind = PieceKind.Number, Value = token });
                }
                else
                {
                    throw new FormatException("style " + id + ": unknown token {" + token + "}");
                }
                pos = close + 1;
            }
            if (!hasList && letters < 2) throw new FormatException("style " + id + " needs a word token or a literal with letters");
            return style;
        }

        // ------------------------------------------------------------------------------------------------ names

        /// <summary>
        /// The name drawn for (<paramref name="scope"/>, <paramref name="id"/>, <paramref name="attempt"/>), or null when
        /// the limits or the blocklist refuse that draw, or when it draws one word twice (PhantomPhantom, NovaNova).
        /// </summary>
        public string Name(string scope, string id, int attempt)
        {
            ulong state = Fnv64(scope + "|v" + Version.ToString(CultureInfo.InvariantCulture) + "|" + id + "|" + attempt.ToString(CultureInfo.InvariantCulture));
            int pick = Below(ref state, totalWeight);
            Style style = styles[styles.Count - 1];
            foreach (Style s in styles)
            {
                if (pick < s.Weight)
                {
                    style = s;
                    break;
                }
                pick -= s.Weight;
            }

            var written = new List<string>(style.Pieces.Count);
            var kinds = new List<PieceKind>(style.Pieces.Count);
            foreach (Piece piece in style.Pieces)
            {
                kinds.Add(piece.Kind);
                if (piece.Kind == PieceKind.Text)
                {
                    written.Add(piece.Value);
                }
                else if (piece.Kind == PieceKind.List)
                {
                    string[] list = parts[piece.Value];
                    string word = list[Below(ref state, list.Length)];
                    for (int i = 0; i < written.Count; i++)
                        if (kinds[i] == PieceKind.List && string.Equals(written[i], word, StringComparison.OrdinalIgnoreCase)) return null;
                    written.Add(word);
                }
                else
                {
                    written.Add(Number(piece.Value, ref state));
                }
            }
            ApplyCase(written, style.Case);
            if (style.Leet) ApplyLeet(written, style, ref state);

            string name = string.Concat(written.ToArray());
            if (name.Length < MinLength || name.Length > MaxLength || GeneratedShapeProblem(name) != null) return null;
            return BlockedReason(written) == null ? name : null;
        }

        /// <summary>
        /// The first name for <paramref name="id"/> that <paramref name="taken"/> does not hold, by name or by
        /// <see cref="Skeleton"/> (so a board never shows Frost27 beside frost-27, or Pika Pew beside PikaPew), added to it
        /// with <see cref="Reserve"/>. Make the set with <see cref="StringComparer.OrdinalIgnoreCase"/> and fill it with
        /// <see cref="Reserve"/>. A curated name first (<see cref="CuratedName"/>); once every curated name is taken, the
        /// generator: after <see cref="MaxAttempts"/> refused or taken draws, the default style "agent-&lt;9 digits&gt;"
        /// from the same hash.
        /// </summary>
        public string UniqueName(string scope, string id, HashSet<string> taken)
        {
            string pick = CuratedName(scope, id, taken);
            if (pick != null) return pick;
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                string name = Name(scope, id, attempt);
                if (name != null && Reserve(taken, name)) return name;
            }
            for (int k = 0; ; k++)
            {
                ulong state = Fnv64(scope + "|v" + Version.ToString(CultureInfo.InvariantCulture) + "|" + id + "|default|" + k.ToString(CultureInfo.InvariantCulture));
                string name = "agent-" + (100000000 + Below(ref state, 900000000)).ToString(CultureInfo.InvariantCulture);
                if (BlockedReason(new List<string> { "agent-", name.Substring(6) }) != null) continue;
                if (Reserve(taken, name)) return name;
            }
        }

        /// <summary>
        /// The curated name of <paramref name="id"/> that <paramref name="taken"/> does not hold (and now does), or null when
        /// the list is empty or every name of it is taken. <see cref="CuratedDraws"/> draws from a hash of (scope, version,
        /// id), then the names after the last draw in file order, round the list: the same id and the same taken set give
        /// the same name, and the list is used up before the generator names anyone.
        /// </summary>
        public string CuratedName(string scope, string id, HashSet<string> taken)
        {
            int n = curated.Length;
            if (n == 0) return null;
            ulong state = Fnv64(scope + "|v" + Version.ToString(CultureInfo.InvariantCulture) + "|" + id + "|curated");
            int index = 0;
            for (int draw = 0; draw < CuratedDraws; draw++)
            {
                index = Below(ref state, n);
                if (TakeCurated(taken, index)) return curated[index];
            }
            for (int step = 1; step < n; step++)
            {
                int at = (index + step) % n;
                if (TakeCurated(taken, at)) return curated[at];
            }
            return null;
        }

        /// <summary><see cref="Reserve"/> for curated name <paramref name="index"/>, with its skeleton key made once at load.</summary>
        private bool TakeCurated(HashSet<string> taken, int index)
        {
            if (taken == null) return true;
            string name = curated[index];
            string key = curatedKeys[index];
            if (taken.Contains(name) || (key != null && taken.Contains(key))) return false;
            taken.Add(name);
            if (key != null) taken.Add(key);
            return true;
        }

        /// <summary>
        /// Adds <paramref name="name"/> and its <see cref="Skeleton"/> to <paramref name="taken"/>; false (and nothing added)
        /// when the set already holds either. A null set takes every name.
        /// </summary>
        public static bool Reserve(HashSet<string> taken, string name)
        {
            if (taken == null) return true;
            if (string.IsNullOrEmpty(name) || taken.Contains(name)) return false;
            string skeleton = Skeleton(name);
            string key = skeleton.Length == 0 ? null : SkeletonMark + skeleton;
            if (key != null && taken.Contains(key)) return false;
            taken.Add(name);
            if (key != null) taken.Add(key);
            return true;
        }

        /// <summary>
        /// The name as a player tells two names apart: lower case, accents dropped (Crème Brûlée is "cremebrulee"), only
        /// its letters and digits (spaces, separators and signs left out: Pika Pew and PikaPew, Mr. Saturn and MrSaturn), a
        /// digit inside a word or a single digit at an end read as the letter it stands for (7ac7ical, Brav0), and the
        /// number at either end left out (Frost27 and frost-27 are both "frost", blindside_563 and blindside_386 both
        /// "blindside"). The default form "agent-&lt;9 digits&gt;" keeps its digits: uncustomised players differ only there.
        /// </summary>
        public static string Skeleton(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string text = Plain(name);
            if (name.StartsWith("agent-", StringComparison.OrdinalIgnoreCase)) return text;
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] < '0' || text[i] > '9')
                {
                    sb.Append(text[i++]);
                    continue;
                }
                int start = i;
                while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
                bool inside = start > 0 && i < text.Length;
                bool single = i - start == 1 && (start > 0 || i < text.Length);
                if (inside || single) sb.Append(Unleet(text.Substring(start, i - start), 'i'));
            }
            return sb.ToString();
        }

        /// <summary>
        /// The emblem and background of <paramref name="id"/>: the starting pair (1, 1) for <see cref="DefaultShare"/>
        /// per cent, else one of <see cref="Avatars"/> and one of <see cref="Backgrounds"/>.
        /// </summary>
        public void Emblem(string scope, string id, out int avatar, out int background)
        {
            ulong state = Fnv64(scope + "|" + id);
            if (Below(ref state, 100) < DefaultShare)
            {
                avatar = Array.IndexOf(Avatars, 1) >= 0 ? 1 : Avatars[0];
                background = Array.IndexOf(Backgrounds, 1) >= 0 ? 1 : Backgrounds[0];
                return;
            }
            avatar = Avatars[Below(ref state, Avatars.Length)];
            background = Backgrounds[Below(ref state, Backgrounds.Length)];
        }

        /// <summary>
        /// Null when the client takes <paramref name="name"/> as a player name and every screen shows it, else why not:
        /// 2..16 characters, no space at an end or two in a row, only <see cref="IsShownChar"/> characters (never '&lt;',
        /// '&gt;', '&amp;', ':', '{', '}', '/', '\\', '"'), not '+', '-' or '*' first (a CRM cell operator:
        /// Gameloft.Engine CRMTableCell), at least one letter, not text the CRM cell parser reads as a number
        /// (double.TryParse, NumberStyles.Float, invariant: "1e5", "NaN", "Infinity").
        /// </summary>
        public static string ShapeProblem(string name)
        {
            if (name == null || name.Length < ClientMinLength || name.Length > ClientMaxLength) return "length outside " + ClientMinLength + ".." + ClientMaxLength;
            if (name[0] == ' ' || name[name.Length - 1] == ' ' || name.IndexOf("  ", StringComparison.Ordinal) >= 0) return "a space at an end or two in a row";
            int letters = 0;
            foreach (char ch in name)
            {
                if (!IsShownChar(ch)) return "the character '" + ch + "' (the client cannot show or take it)";
                if (IsAnyLetter(ch)) letters++;
            }
            if (name[0] == '+' || name[0] == '-' || name[0] == '*') return "starts with a CRM cell operator (+ - *)";
            if (letters == 0) return "no letter";
            double number;
            string lower = name.ToLowerInvariant();
            if (double.TryParse(name, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || lower == "nan" || lower == "inf" || lower == "infinity")
                return "reads as a number";
            return null;
        }

        /// <summary>
        /// Null when a generated <paramref name="name"/> keeps the build-28 shape, else why not: <see cref="ShapeProblem"/>
        /// and 2..16 characters of [A-Za-z0-9_.-], a letter or digit at both ends, no two separators in a row, at least
        /// two letters, not text a number parser reads (after one leading '+', '-' or '*').
        /// </summary>
        public static string GeneratedShapeProblem(string name)
        {
            string shown = ShapeProblem(name);
            if (shown != null) return shown;
            int letters = 0;
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (!IsNameChar(ch)) return "character outside A-Z a-z 0-9 _ . -";
                if (IsLetter(ch)) letters++;
                if (IsSeparator(ch) && (i == 0 || i == name.Length - 1 || IsSeparator(name[i - 1]))) return "separator at an end or after another";
            }
            if (letters < 2) return "fewer than two letters";
            string rest = name[0] == '+' || name[0] == '-' || name[0] == '*' ? name.Substring(1) : name;
            bool numeric = true;
            foreach (char ch in rest)
                if ("0123456789.,eE+-".IndexOf(ch) < 0) numeric = false;
            string lower = rest.ToLowerInvariant();
            if (numeric || lower == "nan" || lower == "inf" || lower == "infinity") return "reads as a number";
            return null;
        }

        /// <summary>
        /// Null when the curated <paramref name="name"/> passes the blocklist, else why not. Its <see cref="Words"/> are read
        /// lower case, accents dropped, only letters and digits, digits as letters: a <c>substrings</c> entry inside one
        /// word, a <c>words</c> entry as one word, either entry spelled exactly by a run of whole words (White Power,
        /// KillYourSelf), and the digit rules. Never a match that starts or ends inside a word across a word break: Spooky
        /// Scary holds no 'kys', Obi Wan Kenoodle no 'wank', WaitItsAllOhio no 'tits'.
        /// </summary>
        public string CuratedBlockedReason(string name)
        {
            if (string.IsNullOrEmpty(name)) return "empty";
            List<string> parts = Words(name);
            var plains = new List<string>(parts.Count);
            foreach (string word in parts) plains.Add(Plain(word));
            foreach (string plain in plains)
            {
                foreach (string form in Readings(plain))
                {
                    foreach (string s in substrings)
                        if (form.IndexOf(s, StringComparison.Ordinal) >= 0) return "contains '" + s + "'";
                    if (words.Contains(form)) return "has the word '" + form + "'";
                }
            }
            for (int i = 0; i < plains.Count; i++)
            {
                string joined = plains[i];
                for (int j = i + 1; j < plains.Count; j++)
                {
                    joined += plains[j];
                    if (joined.Length > ClientMaxLength) break;
                    foreach (string form in Readings(joined))
                        if (substringSet.Contains(form) || words.Contains(form)) return "spells '" + form + "' across its words";
                }
            }
            return DigitReason(name);
        }

        /// <summary>
        /// The words of a name: split at everything but letters and digits, where a lower-case letter or a digit meets an
        /// upper-case one (PepeHands: Pepe, Hands) and where an upper-case run meets a capitalised word (SSSniper: SS, Sniper).
        /// </summary>
        public static List<string> Words(string name)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(name)) return result;
            var current = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (!IsAnyLetter(ch) && (ch < '0' || ch > '9'))
                {
                    if (current.Length > 0) result.Add(current.ToString());
                    current.Length = 0;
                    continue;
                }
                if (current.Length > 0 && char.IsUpper(ch))
                {
                    char prev = name[i - 1];
                    bool nextLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                    if (char.IsLower(prev) || (prev >= '0' && prev <= '9') || (char.IsUpper(prev) && nextLower))
                    {
                        result.Add(current.ToString());
                        current.Length = 0;
                    }
                }
                current.Append(ch);
            }
            if (current.Length > 0) result.Add(current.ToString());
            return result;
        }

        /// <summary>Null when the name made of <paramref name="pieces"/> (in order) passes the blocklist, else why not.</summary>
        public string BlockedReason(IList<string> pieces)
        {
            string name = string.Concat(new List<string>(pieces).ToArray());
            foreach (string form in Forms(name))
            {
                foreach (string s in substrings)
                    if (form.IndexOf(s, StringComparison.Ordinal) >= 0) return "contains '" + s + "'";
                if (words.Contains(form)) return "is '" + form + "'";
            }
            var joined = new List<string>();
            foreach (string piece in pieces)
            {
                List<string> forms = Forms(piece);
                foreach (string form in forms)
                    if (words.Contains(form)) return "has the part '" + form + "'";
                if (forms[0].Length > 0) joined.Add(piece);
            }
            for (int i = 0; i + 1 < joined.Count; i++)
            {
                foreach (string a in Forms(joined[i]))
                    foreach (string b in Forms(joined[i + 1]))
                    {
                        string w = Spanning(a, b);
                        if (w != null) return "spells '" + w + "' across a join";
                    }
            }
            return DigitReason(name);
        }

        /// <summary>The digit rules: a run of digits that is a <c>digitRuns</c> entry or holds a <c>digits</c> entry.</summary>
        private string DigitReason(string name)
        {
            int start = -1;
            for (int i = 0; i <= name.Length; i++)
            {
                bool digit = i < name.Length && name[i] >= '0' && name[i] <= '9';
                if (digit && start < 0) start = i;
                if (!digit && start >= 0)
                {
                    string run = name.Substring(start, i - start);
                    if (digitRuns.Contains(run)) return "has the number " + run;
                    foreach (string d in digits)
                        if (run.IndexOf(d, StringComparison.Ordinal) >= 0) return "has the digits " + d;
                    start = -1;
                }
            }
            return null;
        }

        private string Spanning(string a, string b)
        {
            string s = a + b;
            int join = a.Length;
            foreach (int n in wordLengths)
            {
                for (int start = Math.Max(0, join - n + 1); start < join && start + n <= s.Length; start++)
                {
                    string w = s.Substring(start, n);
                    if (words.Contains(w)) return w;
                }
            }
            return null;
        }

        /// <summary>
        /// <paramref name="text"/> lower case, Latin-1 letters without their accents (é e, Æ ae, ß ss), only its letters and
        /// digits (letters of other scripts, which only a real player's name holds, kept in lower case).
        /// </summary>
        private static string Plain(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                if (ch >= 'A' && ch <= 'Z') sb.Append((char)(ch + 32));
                else if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
                else if (IsLatin1Letter(ch)) sb.Append(ch == '\u00FF' ? "y" : Latin1Fold[(ch - 0xC0) % 32]);
                else if (ch > '\u00FF' && char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>A plain text as written and with its digits read as letters ('1' as i and as l).</summary>
        private static List<string> Readings(string plain)
        {
            string asI = Unleet(plain, 'i');
            string asL = Unleet(plain, 'l');
            var forms = new List<string> { plain };
            if (asI != plain) forms.Add(asI);
            if (asL != asI) forms.Add(asL);
            return forms;
        }

        /// <summary>The blocklist readings: lower case without separators, as written, with digits read as letters ('1' as i and as l).</summary>
        private static List<string> Forms(string text)
        {
            var raw = new StringBuilder(text.Length);
            foreach (char ch in text)
                if (!IsSeparator(ch)) raw.Append(ch >= 'A' && ch <= 'Z' ? (char)(ch + 32) : ch);
            string plain = raw.ToString();
            string asI = Unleet(plain, 'i');
            string asL = Unleet(plain, 'l');
            var forms = new List<string> { plain };
            if (asI != plain) forms.Add(asI);
            if (asL != asI) forms.Add(asL);
            return forms;
        }

        private static string Unleet(string text, char one)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                switch (ch)
                {
                    case '0': sb.Append('o'); break;
                    case '1': sb.Append(one); break;
                    case '3': sb.Append('e'); break;
                    case '4': sb.Append('a'); break;
                    case '5': sb.Append('s'); break;
                    case '7': sb.Append('t'); break;
                    case '8': sb.Append('b'); break;
                    case '$': sb.Append('s'); break;
                    case '@': sb.Append('a'); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }

        private string Number(string token, ref ulong state)
        {
            switch (token)
            {
                case "d2": return Below(ref state, 100).ToString("D2", CultureInfo.InvariantCulture);
                case "d3": return (100 + Below(ref state, 900)).ToString(CultureInfo.InvariantCulture);
                case "d4": return (1000 + Below(ref state, 9000)).ToString(CultureInfo.InvariantCulture);
                case "year": return (yearFirst + Below(ref state, yearLast - yearFirst + 1)).ToString(CultureInfo.InvariantCulture);
                default: return (100000000 + Below(ref state, 900000000)).ToString(CultureInfo.InvariantCulture);
            }
        }

        private static void ApplyCase(List<string> written, string mode)
        {
            for (int i = 0; i < written.Count; i++)
            {
                if (mode == "lower") written[i] = written[i].ToLowerInvariant();
                else if (mode == "upper") written[i] = written[i].ToUpperInvariant();
            }
            if (mode == "camel" && written.Count > 0 && written[0].Length > 0)
                written[0] = char.ToLowerInvariant(written[0][0]) + written[0].Substring(1);
        }

        /// <summary>1-3 letters of the word parts become the digits of the leet map, at drawn positions.</summary>
        /// <summary>Turns 1-3 letters of the word parts into digits; never a country code's (L30pard_R0 would read as noise).</summary>
        private void ApplyLeet(List<string> written, Style style, ref ulong state)
        {
            var positions = new List<int[]>();
            for (int i = 0; i < written.Count; i++)
            {
                if (style.Pieces[i].Kind != PieceKind.List || style.Pieces[i].Value == "country") continue;
                for (int j = 0; j < written[i].Length; j++)
                {
                    char lower = char.ToLowerInvariant(written[i][j]);
                    if (lower < 128 && leet.ContainsKey(lower)) positions.Add(new[] { i, j });
                }
            }
            int count = 1 + Below(ref state, 3);
            for (int k = 0; k < count && positions.Count > 0; k++)
            {
                int index = Below(ref state, positions.Count);
                int[] at = positions[index];
                positions.RemoveAt(index);
                char[] chars = written[at[0]].ToCharArray();
                chars[at[1]] = leet[char.ToLowerInvariant(chars[at[1]])];
                written[at[0]] = new string(chars);
            }
        }

        // ------------------------------------------------------------------------------------------------ helpers

        /// <summary>FNV-1a 64 over UTF-8 (never 0): the same value on every runtime.</summary>
        public static ulong Fnv64(string text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(text ?? ""))
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
            return hash == 0 ? 0x9E3779B97F4A7C15UL : hash;
        }

        /// <summary>xorshift64*; 0 &lt;= result &lt; n.</summary>
        private static int Below(ref ulong state, int n)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            ulong value = state * 2685821657736338717UL;
            return n <= 1 ? 0 : (int)(value % (ulong)n);
        }

        private static bool IsLetter(char ch)
        {
            return (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
        }

        private static bool IsSeparator(char ch)
        {
            return Separators.IndexOf(ch) >= 0;
        }

        private static bool IsNameChar(char ch)
        {
            return IsLetter(ch) || (ch >= '0' && ch <= '9') || IsSeparator(ch);
        }

        /// <summary>A Latin-1 letter: U+00C0..U+00FF but '×' and '÷' (the Latin font draws them all).</summary>
        private static bool IsLatin1Letter(char ch)
        {
            return ch >= '\u00C0' && ch <= '\u00FF' && ch != '\u00D7' && ch != '\u00F7';
        }

        private static bool IsAnyLetter(char ch)
        {
            return IsLetter(ch) || IsLatin1Letter(ch);
        }

        /// <summary>Whether a player name may hold <paramref name="ch"/>: an ASCII letter or digit, a Latin-1 letter or one of <see cref="NameSigns"/>.</summary>
        public static bool IsShownChar(char ch)
        {
            return IsAnyLetter(ch) || (ch >= '0' && ch <= '9') || NameSigns.IndexOf(ch) >= 0;
        }

        private static bool IsPart(string w)
        {
            if (string.IsNullOrEmpty(w) || w.Length > 12 || !IsLetter(w[0])) return false;
            foreach (char ch in w)
                if (!IsLetter(ch) && (ch < '0' || ch > '9')) return false;
            return true;
        }

        private static long Require(long? value, long min, long max, string what)
        {
            if (!value.HasValue || value.Value < min || value.Value > max)
                throw new FormatException(what + " must be an integer " + min.ToString(CultureInfo.InvariantCulture) + ".." + max.ToString(CultureInfo.InvariantCulture));
            return value.Value;
        }

        private static void ReadStrings(List<object> values, string what, int minLength, List<string> into)
        {
            if (values == null || values.Count == 0) throw new FormatException(what + " must be a non-empty list");
            foreach (object v in values)
            {
                string s = JsonValue.AsString(v);
                if (s == null || s.Length < minLength) throw new FormatException(what + ": '" + s + "' is too short");
                foreach (char ch in s)
                    if ((ch < 'a' || ch > 'z') && (ch < '0' || ch > '9')) throw new FormatException(what + ": '" + s + "' must be lower-case letters and digits");
                into.Add(s);
            }
        }

        /// <summary>"1-6,9-224" → the ids, ascending, distinct, each within [min, max].</summary>
        private static int[] Ranges(string text, int min, int max, string what)
        {
            if (string.IsNullOrEmpty(text)) throw new FormatException(what + " is missing");
            var ids = new SortedSet<int>();
            foreach (string part in text.Split(','))
            {
                string[] ends = part.Split('-');
                int a, b;
                if (ends.Length > 2 || !int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out a)
                    || !int.TryParse(ends[ends.Length - 1], NumberStyles.None, CultureInfo.InvariantCulture, out b) || a > b || a < min || b > max)
                    throw new FormatException(what + ": '" + part + "' is not an id or a range within " + min + ".." + max);
                for (int i = a; i <= b; i++) ids.Add(i);
            }
            return new List<int>(ids).ToArray();
        }

        private enum PieceKind
        {
            Text,
            List,
            Number,
        }

        private sealed class Piece
        {
            public PieceKind Kind;
            public string Value;
        }

        private sealed class Style
        {
            public string Id;
            public string Case;
            public bool Leet;
            public int Weight;
            public List<Piece> Pieces = new List<Piece>();
        }
    }
}
