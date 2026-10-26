using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Server.Services.Social.LiveOps;
using McvsOffline.Tests.Content;

namespace McvsOffline.Tests.Golden
{
    /// <summary>
    /// The golden scenarios of economy WP-0 (docs/design/economy-collector.md §6 "WP-0 Golden capture"): what the server
    /// serves before any collector wiring. <see cref="GoldenCaptureTests.Write"/> captured them on ac64bfd (the settings
    /// page merged, no economy code) into <see cref="FileName"/>; the collector packages run the same scenarios with the
    /// collector off or dormant and compare (WP-D <c>CollectorOffMatchesGolden</c>, <c>DormantMatchesGolden</c>, WP-E
    /// <c>LiveOpsCollectorOffMatchesGolden</c>), through <see cref="Run"/>'s <c>setup</c> hook and
    /// <see cref="Mismatches"/>.
    /// <para>
    /// A run is one composed server (<see cref="GoldenRun"/>: every module as the hosts run it, the admin module included;
    /// default settings, no data pack, not listening: port 51780 from the settings) on a fake clock at 12:00 UTC of each of
    /// the 16 days 2026-11-02 … 2026-11-17, which cross the Monday season boundary <see cref="Since"/> (the shipped
    /// collector <c>since</c>). Live ops on, or off (<c>liveops.enabled=false</c>). Each day, in this order: the full
    /// static document (<c>configs/users/me</c> without a token), the <c>game</c> selector, on the
    /// <see cref="PlayerDays"/> the per-player documents of three players (<see cref="DumpNames"/>: the dump is saved once
    /// on the first day, then read again before each fetch, as the TS does at login), the day's daily quest and the
    /// season's weekly event (<c>events/{id}</c>: the event text including <c>_version</c>; with live ops off the 404
    /// body). The CRM documents carry the <c>newsfeeds</c> inbox item.
    /// </para>
    /// <para>
    /// The golden file stores only <c>{sha256, length}</c> per output (<see cref="Serialize"/>); a package that changes
    /// some outputs on purpose compares the rest, or a transformed text, with <see cref="Mismatches"/>.
    /// </para>
    /// </summary>
    internal static class GoldenScenarios
    {
        /// <summary>The golden file, next to this source file (tests/McvsOffline.Tests/Golden).</summary>
        public const string FileName = "golden-ac64bfd.json";

        public const string Static = "static";
        public const string Game = "game";
        public const string Player = "player";
        public const string Daily = "daily";
        public const string Weekly = "weekly";

        /// <summary>The <c>DefaultProfile.tx</c> shape of a new profile: no signature lists, <c>"Mercs": []</c>, a squad of agent 1.</summary>
        public const string Fresh = "fresh";

        /// <summary>A profile after <c>settings.unlockAll</c> (<see cref="UnlockAllDump"/>), as the TS's Templates writer saves it.</summary>
        public const string UnlockAll = "unlockAll";

        /// <summary>CrmOwnedStoreItemsTests' unreadable dump: <c>KillSignatureAvatars</c> twice.</summary>
        public const string Malformed = "malformed";

        public const int DayCount = 16;

        /// <summary>The first day of a run (a Monday): 2026-11-02 00:00 UTC.</summary>
        public static readonly DateTime FirstDay = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>The Monday season boundary inside the run, the shipped collector <c>since</c>: 2026-11-09 00:00 UTC.</summary>
        public static readonly DateTime Since = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>The time of day of every capture.</summary>
        public static readonly TimeSpan TimeOfDay = TimeSpan.FromHours(12);

        /// <summary>The days with per-player documents: the first day, <see cref="Since"/> and the last day.</summary>
        public static readonly DateTime[] PlayerDays = { FirstDay, Since, FirstDay.AddDays(DayCount - 1) };

        public static readonly string[] DumpNames = { Fresh, UnlockAll, Malformed };

        /// <summary>Live ops on, then off.</summary>
        public static readonly bool[] LiveOpsModes = { true, false };

        /// <summary>
        /// The <c>DefaultProfile.tx</c> layout (CRLF, ", " separators) with the members of <c>$default</c> and
        /// <c>/Profile</c> that the inventory reads (the excerpt PlayerInventoryTests also scans).
        /// </summary>
        private const string FreshDump
            = "{\r\n    \"@version\": \"0.1\", \r\n    \"$default\": {\r\n        \"Enabled\": true, \r\n"
              + "        \"/Profile\": {\r\n            \"Xp\": 0, \r\n            \"Mercs\": [], \r\n            \"Squads\": [\r\n"
              + "                {\r\n                    \"Id\": \"0\", \r\n                    \"Mercs\": [\r\n"
              + "                        \"1\"\r\n                    ], \r\n                    \"Name\": null, \r\n"
              + "                    \"Primary\": true\r\n                }\r\n            ], \r\n"
              + "            \"@type\": \"PlayerProfileComponent\"\r\n        }\r\n    }\r\n}";

        /// <summary>The dump of CrmOwnedStoreItemsTests.AnUnreadableDumpServesTheWholeStore.</summary>
        private const string MalformedDump = "{\"a\":{\"KillSignatureAvatars\":[\"1\"]},\"b\":{\"KillSignatureAvatars\":[\"2\"]}}";

        /// <summary>
        /// The <c>camos</c> group of server/Content/unlocks.json (UNLOCKALL) at ac64bfd: <c>WeaponCamo_&lt;agent&gt;_{…}</c>,
        /// index = agent.
        /// </summary>
        private static readonly string[] UnlockAllCamos =
        {
            null,
            "2,4-110,120-155,166-231", "2,4-110,120-155,166-231", "2,4-111,120-155,166-231", "2-3,5-9,11-110,120-155,166-231",
            "2-4,6-110,120-155,166-231", "2,4-110,120-155,166-232", "2,4-110,120-155,166-231", "3-110,120-155,166-231",
            "2-110,120-155,166-232", "2-110,120-155,166-228,230-232", "2-110,120-155,166-231", "2,4-110,120-155,166-231",
            "2,4-110,120-155,166-231", "2,4-110,120-155,166-231", "2-110,120-155,166-231", "3-110,120-155,166-231",
            "3-110,120-155,166-231", "2-9,11-110,120-155,166-231", "2,4-110,120-155,166-232", "2,4-110,120-155,166-231",
            "11-110,120-155,166-231",
        };

        /// <summary>The <c>accessories</c> group: <c>MercAccessory_&lt;agent&gt;_{…}</c>, the same ids for agents 1-21.</summary>
        private const string UnlockAllAccessories = "1-141,143-147,149-153,160-166,172-194,212-265,268-280";

        /// <summary>The <c>skins</c> group: <c>MercSkin_&lt;agent&gt;_&lt;id&gt;</c>, index = agent (agent 21 has none).</summary>
        private static readonly string[] UnlockAllSkins =
        {
            null,
            "2-3", "2,5", "2,4", "2-3,6", "2-4", "2,4", "2", "7", "2-4", "2-3,7", "2-4", "2,4", "2", "2", "6", "3", "6,11-12", "2", "2,4",
            "2,4", "",
        };

        private static string unlockAllDump;

        /// <summary>The 16 days of a run, 00:00 UTC.</summary>
        public static IEnumerable<DateTime> Days()
        {
            for (int i = 0; i < DayCount; i++) yield return FirstDay.AddDays(i);
        }

        public static bool IsPlayerDay(DateTime day)
        {
            return Array.IndexOf(PlayerDays, day) >= 0;
        }

        /// <summary>"liveops-on/2026-11-02/static", "liveops-off/2026-11-09/player-unlockAll", "liveops-on/2026-11-17/weekly".</summary>
        public static string NameOf(bool liveOps, DateTime day, string kind, string dump)
        {
            return (liveOps ? "liveops-on/" : "liveops-off/") + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "/"
                   + kind + (dump == null ? "" : "-" + dump);
        }

        /// <summary>Every output name of <see cref="RunAll"/>, in capture order, without running anything.</summary>
        public static List<string> Names()
        {
            var names = new List<string>();
            foreach (bool liveOps in LiveOpsModes)
            {
                foreach (DateTime day in Days())
                {
                    names.Add(NameOf(liveOps, day, Static, null));
                    names.Add(NameOf(liveOps, day, Game, null));
                    if (IsPlayerDay(day))
                        foreach (string dump in DumpNames) names.Add(NameOf(liveOps, day, Player, dump));
                    names.Add(NameOf(liveOps, day, Daily, null));
                    names.Add(NameOf(liveOps, day, Weekly, null));
                }
            }
            return names;
        }

        /// <summary>The game-profile dump a player of the run saves (<see cref="DumpNames"/>).</summary>
        public static string DumpText(string dump)
        {
            switch (dump)
            {
                case Fresh: return FreshDump;
                case UnlockAll: return UnlockAllDump();
                case Malformed: return MalformedDump;
                default: throw new ArgumentException("unknown dump '" + dump + "'", "dump");
            }
        }

        /// <summary>
        /// A profile after <c>settings.unlockAll</c>, in the compact shape of the TS's Templates writer
        /// (PlayerInventoryTests.WriterMercsFixture): <c>KillSignatureAvatars</c> 1-1512, <c>KillSignatureBackgrounds</c>
        /// 1-35, <c>KillSignatureSounds</c> 1-5 (1: the starting items; the rest the <c>signatures</c> group), and
        /// <c>Mercs</c> agents 1-21 unlocked with the starting skin and camo 1 plus the <c>skins</c>, <c>camos</c> and
        /// <c>accessories</c> groups of unlocks.json at ac64bfd (the VIP-only pools are not in UNLOCKALL). The other
        /// <c>Merc.ShortDef</c> properties keep their defaults; no <c>Squads</c> (the TS's dump has none). Fixed text:
        /// it is part of the golden input and must not follow later content changes.
        /// </summary>
        public static string UnlockAllDump()
        {
            if (unlockAllDump != null) return unlockAllDump;
            var sb = new StringBuilder("{\"@version\":\"0.1\",\"$default\":{\"/Profile\":{\"@type\":\"PlayerProfileComponent\",");
            sb.Append("\"KillSignatureAvatars\":").Append(IdList("1-1512"))
              .Append(",\"KillSignatureBackgrounds\":").Append(IdList("1-35"))
              .Append(",\"KillSignatureSounds\":").Append(IdList("1-5"))
              .Append(",\"Mercs\":[");
            for (int agent = 1; agent <= 21; agent++)
            {
                if (agent > 1) sb.Append(',');
                string id = agent.ToString(CultureInfo.InvariantCulture);
                sb.Append("{\"NewAccessories\":[],\"Skins\":").Append(IdList(Join("1", UnlockAllSkins[agent])))
                  .Append(",\"Id\":\"").Append(id).Append("\",\"Accessories\":").Append(IdList(UnlockAllAccessories))
                  .Append(",\"NewMods\":[],\"WeaponInTrial\":[],\"Fatigue\":0,\"Camos\":").Append(IdList(Join("1", UnlockAllCamos[agent])))
                  .Append(",\"Locked\":false,\"Mods\":[],\"IsNew\":true,\"EquippedSkin\":\"1\",\"MasteryToClaim\":[],\"Xp\":0,")
                  .Append("\"EquippedWeapon\":\"\",\"Weapons\":[],\"GearSlots\":[");
                for (int slot = 0; slot < 6; slot++) sb.Append(slot > 0 ? "," : "").Append("{\"Locked\":true,\"GearId\":-1}");
                sb.Append("],\"Level\":1,\"EquippedAccessories\":[],\"AutoclaimMasteryLevel\":0,\"NewWeapons\":[],")
                  .Append("\"WeaponsFragments\":{},\"UpgradeSeen\":true,\"NewCamos\":[],\"NewSkins\":[],\"EquippedCamo\":\"1\"}");
            }
            sb.Append("]}}}");
            unlockAllDump = sb.ToString();
            return unlockAllDump;
        }

        /// <summary>Both live-ops modes (<see cref="LiveOpsModes"/>), in <see cref="Names"/> order.</summary>
        /// <param name="setup">Called on each run's server before its first request (settings, content overrides).</param>
        public static List<GoldenOutput> RunAll(Action<GoldenRun> setup = null)
        {
            var outputs = new List<GoldenOutput>();
            foreach (bool liveOps in LiveOpsModes) outputs.AddRange(Run(liveOps, setup));
            return outputs;
        }

        /// <summary>One run over the 16 days (see the class remarks).</summary>
        /// <param name="setup">
        /// Called on the run's server after <c>liveops.enabled</c> is set and before the first request, e.g. to set
        /// <c>economy.collector.*</c> (<see cref="GoldenRun.Set"/>), write a content override
        /// (<see cref="GoldenRun.OverrideContent"/>) or change a feature of <see cref="GoldenRun.Context"/> (the client
        /// capabilities' store patch).
        /// </param>
        public static List<GoldenOutput> Run(bool liveOps, Action<GoldenRun> setup = null)
        {
            var outputs = new List<GoldenOutput>();
            using (var run = new GoldenRun(FirstDay + TimeOfDay))
            {
                if (!liveOps) run.Set("liveops.enabled", false);
                if (setup != null) setup(run);
                var players = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (DateTime day in Days())
                {
                    run.Clock.UtcNow = day + TimeOfDay;
                    outputs.Add(Capture(run, liveOps, day, Static, null, null, "/configs/users/me", null));
                    outputs.Add(Capture(run, liveOps, day, Game, null, null, "/configs/users/me/game", null));
                    if (IsPlayerDay(day))
                    {
                        foreach (string dump in DumpNames)
                        {
                            string token;
                            if (!players.TryGetValue(dump, out token))
                            {
                                token = run.NewPlayer("golden-" + dump);
                                run.SaveGameProfile(token, DumpText(dump));
                                players[dump] = token;
                            }
                            run.ReadGameProfile(token);
                            outputs.Add(Capture(run, liveOps, day, Player, dump, null, "/configs/users/me", token));
                        }
                    }
                    LiveOpsSettings s = LiveOpsService.For(run.Context).Settings;
                    string daily = LiveOpsSchedule.DailyEventId(LiveOpsSchedule.DayAt(s, run.Clock.UtcNow));
                    string weekly = LiveOpsSchedule.WeeklyEventId(LiveOpsSchedule.SeasonAt(s, run.Clock.UtcNow));
                    outputs.Add(Capture(run, liveOps, day, Daily, null, daily, "/events/" + daily + "?language=en", null));
                    outputs.Add(Capture(run, liveOps, day, Weekly, null, weekly, "/events/" + weekly + "?language=en", null));
                }
            }
            return outputs;
        }

        /// <summary>Lower-case hex SHA-256 of the UTF-8 bytes of <paramref name="text"/>.</summary>
        public static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>UTF-8 byte count of <paramref name="text"/>, the <c>length</c> of a golden entry.</summary>
        public static long Length(string text)
        {
            return Encoding.UTF8.GetByteCount(text ?? "");
        }

        /// <summary>The golden file in the source tree.</summary>
        public static string FilePath
        {
            get { return Path.Combine(ContentTestSupport.RepoRoot, "tests", "McvsOffline.Tests", "Golden", FileName); }
        }

        /// <summary>The golden entries by output name.</summary>
        public static Dictionary<string, GoldenEntry> Load()
        {
            var entries = new Dictionary<string, GoldenEntry>(StringComparer.Ordinal);
            foreach (var kv in MiniJson.ParseObject(File.ReadAllText(FilePath, Encoding.UTF8)))
            {
                var entry = kv.Value as Dictionary<string, object>;
                long? length = JsonValue.AsLong(JsonValue.Get(entry, "length"));
                entries[kv.Key] = new GoldenEntry { Sha256 = JsonValue.GetString(entry, "sha256"), Length = length ?? -1 };
            }
            return entries;
        }

        /// <summary>
        /// The outputs whose text (or <paramref name="text"/> of the output, e.g. without the guard cells) does not hash
        /// to its golden entry, one line each ("name: reason"); empty when all match.
        /// </summary>
        public static List<string> Mismatches(IEnumerable<GoldenOutput> outputs, Func<GoldenOutput, string> text = null)
        {
            Dictionary<string, GoldenEntry> golden = Load();
            var problems = new List<string>();
            foreach (GoldenOutput o in outputs)
            {
                GoldenEntry entry;
                if (!golden.TryGetValue(o.Name, out entry))
                {
                    problems.Add(o.Name + ": no golden entry");
                    continue;
                }
                string t = text == null ? o.Text : text(o);
                string hash = Sha256(t);
                long length = Length(t);
                if (hash != entry.Sha256)
                    problems.Add(o.Name + ": sha256 " + hash + " != " + entry.Sha256 + ", length " + length
                                 + (length == entry.Length ? " (same)" : " != " + entry.Length));
            }
            return problems;
        }

        /// <summary>The golden file's text: one entry per line, in <see cref="Names"/> order.</summary>
        public static string Serialize(IList<GoldenOutput> outputs)
        {
            var sb = new StringBuilder("{\n");
            for (int i = 0; i < outputs.Count; i++)
            {
                GoldenOutput o = outputs[i];
                sb.Append("  ").Append(MiniJson.Serialize(o.Name)).Append(": {\"sha256\": \"").Append(Sha256(o.Text)).Append("\", \"length\": ")
                  .Append(Length(o.Text).ToString(CultureInfo.InvariantCulture)).Append('}').Append(i + 1 < outputs.Count ? ",\n" : "\n");
            }
            return sb.Append("}\n").ToString();
        }

        private static GoldenOutput Capture(GoldenRun run, bool liveOps, DateTime day, string kind, string dump, string eventId, string target,
                                            string token)
        {
            HttpResponse response = run.Get(target, token);
            return new GoldenOutput
            {
                Name = NameOf(liveOps, day, kind, dump),
                LiveOps = liveOps,
                Day = day,
                Kind = kind,
                Dump = dump,
                EventId = eventId,
                Status = response.StatusCode,
                Text = response.BodyText,
            };
        }

        private static string Join(string first, string rest)
        {
            return string.IsNullOrEmpty(rest) ? first : first + "," + rest;
        }

        /// <summary>A JSON array of the ids of a "2,4-110,120-155" spec as strings: <c>["2","4","5",…]</c>.</summary>
        private static string IdList(string spec)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (string part in spec.Split(','))
            {
                int dash = part.IndexOf('-');
                int from = int.Parse(dash < 0 ? part : part.Substring(0, dash), NumberStyles.None, CultureInfo.InvariantCulture);
                int to = dash < 0 ? from : int.Parse(part.Substring(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture);
                for (int id = from; id <= to; id++)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(id.ToString(CultureInfo.InvariantCulture)).Append('"');
                }
            }
            return sb.Append(']').ToString();
        }
    }

    /// <summary>One captured output of a golden run.</summary>
    internal sealed class GoldenOutput
    {
        /// <summary><see cref="GoldenScenarios.NameOf"/>: the key of the golden entry.</summary>
        public string Name;

        public bool LiveOps;

        /// <summary>The day, 00:00 UTC (captured at <see cref="GoldenScenarios.TimeOfDay"/>).</summary>
        public DateTime Day;

        /// <summary><see cref="GoldenScenarios.Static"/>, <c>Game</c>, <c>Player</c>, <c>Daily</c> or <c>Weekly</c>.</summary>
        public string Kind;

        /// <summary>For a per-player document: <see cref="GoldenScenarios.DumpNames"/>; else null.</summary>
        public string Dump;

        /// <summary>For an event: its id; else null.</summary>
        public string EventId;

        /// <summary>The HTTP status (not hashed).</summary>
        public int Status;

        /// <summary>The response body: what the golden entry hashes.</summary>
        public string Text;

        public override string ToString()
        {
            return Name + " (" + Status + ", " + (Text == null ? 0 : Text.Length) + " chars)";
        }
    }

    /// <summary>One entry of the golden file: <c>{"sha256": "…", "length": UTF-8 bytes}</c>.</summary>
    internal sealed class GoldenEntry
    {
        public string Sha256;
        public long Length;
    }

    /// <summary>
    /// The server of one golden run: every module (<see cref="ServiceRegistry.RegisterAll"/>, as the hosts compose it)
    /// in a temporary data directory, default settings, a <see cref="ManualClock"/>, not listening (requests go through
    /// <see cref="McvsServer.Handle"/>), logs captured in <see cref="Logs"/>. The admin module (the settings page, whose
    /// <c>IAdminPortal</c> gives the CRM its <c>newsfeeds</c> inbox item) is registered after the registry, where the
    /// registry itself registers it once the settings page lands; that second registration is ignored, so the run
    /// composes the same server before and after.
    /// </summary>
    internal sealed class GoldenRun : IDisposable
    {
        /// <summary>The scopes of a TS on-behalf token (CrmOwnedStoreItemsTests).</summary>
        private static readonly string[] PlayerScopes = { "auth", "storage", "storage_ro", "storage_restricted", "storage_admin", "config" };

        private const string GameProfile = "/profiles/me/myprofile/_mcvs.gameProfile";

        /// <summary>The temporary directory this run owns (null when it runs on another run's data directory).</summary>
        private readonly TempDir dir;

        public GoldenRun(DateTime startUtc)
            : this(startUtc, null)
        {
        }

        /// <summary>
        /// A run on <paramref name="dataDir"/> (null: a new temporary one, deleted with the run): a restart of the server
        /// of another run, which must have been disposed with <see cref="KeepDataDir"/> set.
        /// </summary>
        public GoldenRun(DateTime startUtc, string dataDir)
        {
            if (dataDir == null) dir = new TempDir();
            Clock = new ManualClock(startUtc);
            Logs = new MemoryLogSink();
            DataDir = dataDir ?? dir.Combine("data");
            var options = new ServerOptions
            {
                DataDir = DataDir,
                ConsoleLog = false,
                FileLog = false,
                Clock = Clock,
                RegisterServices = (router, context) =>
                {
                    ServiceRegistry.RegisterAll(router, context);
                    AdminService.Register(router, context);
                },
            };
            options.ExtraSinks.Add(Logs);
            Server = McvsServer.Create(options);
        }

        public McvsServer Server { get; private set; }

        public ServerContext Context { get { return Server.Context; } }

        public ManualClock Clock { get; private set; }

        public MemoryLogSink Logs { get; private set; }

        public string DataDir { get; private set; }

        /// <summary>Dispose leaves <see cref="DataDir"/> for a restart (<see cref="GoldenRun(DateTime, string)"/>); the
        /// restarted run's owner deletes it (<see cref="DeleteDataDir"/>).</summary>
        public bool KeepDataDir { get; set; }

        public void Set(string setting, object value)
        {
            Context.Settings.Override(setting, value);
        }

        /// <summary>Writes <c>&lt;data&gt;/content/&lt;name&gt;</c>, which wins over the built-in content file.</summary>
        public void OverrideContent(string name, string text)
        {
            string path = Path.Combine(Path.Combine(DataDir, "content"), name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>A GET as the client sends it, the token (if any) in the query.</summary>
        public HttpResponse Get(string target, string token)
        {
            if (token != null) target += (target.Contains("?") ? "&" : "?") + "access_token=" + Uri.EscapeDataString(token);
            return Server.Handle(HttpRequest.Create("GET", target));
        }

        /// <summary>A new anonymous account and the TS's on-behalf token for it (actor: the game server's super credential).</summary>
        public string NewPlayer(string user)
        {
            var account = Context.Accounts.GetOrCreate("anonymous:" + user, "device-" + user);
            return Context.Tokens.Issue(account.Id, "anonymous:" + user, McvsConstants.ClientId, PlayerScopes, "device-" + user,
                                        "game:mcvsoffline").Token;
        }

        /// <summary>The TS's save of the game-profile dump (R4).</summary>
        public void SaveGameProfile(string token, string dump)
        {
            string form = UrlEncoding.EncodeForm(new[]
            {
                new KeyValuePair<string, string>("visibility", "restricted"),
                new KeyValuePair<string, string>("object", dump),
                new KeyValuePair<string, string>("access_token", token),
            });
            HttpResponse response = Server.Handle(HttpRequest.Create("POST", GameProfile, form));
            if (response.StatusCode != 200)
                throw new InvalidOperationException("game profile save: HTTP " + response.StatusCode + " " + response.BodyText);
        }

        /// <summary>The TS's read of the game-profile dump (login, ReloadProfileAsync): <c>storage.gameprofile.read</c>.</summary>
        public void ReadGameProfile(string token)
        {
            HttpResponse response = Get(GameProfile, token);
            if (response.StatusCode != 200)
                throw new InvalidOperationException("game profile read: HTTP " + response.StatusCode + " " + response.BodyText);
        }

        public void Dispose()
        {
            Server.Dispose();
            if (dir != null && !KeepDataDir) dir.Dispose();
        }

        /// <summary>Deletes the temporary directory of a run disposed with <see cref="KeepDataDir"/>.</summary>
        public void DeleteDataDir()
        {
            if (dir != null) dir.Dispose();
        }
    }
}
