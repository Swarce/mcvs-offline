using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Auth;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Admin.Saves
{
    /// <summary>
    /// One account's save as a self-contained JSON document (design §6.1): the profile file text exactly as on disk (so
    /// the verbatim <c>_mcvs.gameProfile</c> bytes survive), its UTF-8 length and CRC-32, and the account's two Hermes
    /// mailboxes the TS reads (unclaimed coupon unlocks and season rewards). Never exported: credentials, tokens.json,
    /// instance.json, device-id.txt, args.txt, leaderboards, social/.
    /// <code>
    /// {"format": "mcvs-offline/save-bundle", "version": 1, "created": "...",
    ///  "server": {"version", "build", "instance"}, "account": {"id", "alias", "created"},
    ///  "profile": "&lt;file text&gt;", "profileLength": 612000, "profileCrc32": "1a2b3c4d",
    ///  "messages": {"inbox": "&lt;text&gt;", "secured": "&lt;text&gt;"}, "settings": null}
    /// </code>
    /// <see cref="Parse"/> validates strictly: every check of §6.1 that fails is an error (the import cannot be staged).
    /// A raw profile file (<c>format: mcvs-offline/seshat-profile</c>) is accepted as a bundle without messages.
    /// </summary>
    public sealed class SaveBundle
    {
        public const string Format = "mcvs-offline/save-bundle";
        public const long Version = 1;
        public const int MaxProfileBytes = 12 * 1024 * 1024;
        public const int MaxMessageBytes = 4 * 1024 * 1024;

        /// <summary>The Hermes transports a bundle carries (the two the TS reads at login).</summary>
        public static readonly string[] Transports = { "inbox", "secured" };

        public const string ErrorNotASave = "This is not an MCVS Offline save (or it comes from a newer version).";
        public const string ErrorAccount = "The save's account id is not valid.";
        public const string ErrorIncomplete = "The save is incomplete (copied only in part?).";
        public const string ErrorProfile = "The save's profile is not an MCVS Offline profile.";
        public const string ErrorNoProgress = "The save holds no game progress.";
        public const string ErrorInbox = "The save's inbox is damaged.";

        public SaveBundle()
        {
            Messages = new Dictionary<string, string>(StringComparer.Ordinal);
            Errors = new List<string>();
            Warnings = new List<string>();
        }

        public string AccountId { get; set; }

        public string Alias { get; set; }

        /// <summary>ISO time the account was created on the exporting phone.</summary>
        public string AccountCreated { get; set; }

        /// <summary>ISO time of the export.</summary>
        public string Created { get; set; }

        public string ServerVersion { get; set; }

        public string ServerBuild { get; set; }

        public string ServerInstance { get; set; }

        /// <summary>The profile file text exactly as it was on disk.</summary>
        public string ProfileText { get; set; }

        /// <summary>transport → the mailbox document's text (only the transports that had one).</summary>
        public Dictionary<string, string> Messages { get; private set; }

        /// <summary>Settings.Snapshot() without superPassword (informational; never applied by an import), or null.</summary>
        public Dictionary<string, object> Settings { get; set; }

        /// <summary>The text was a raw profile file, not a bundle.</summary>
        public bool IsRawProfile { get; private set; }

        // ------------------------------------------------------------------ after Parse

        /// <summary>The parsed profile (verbatim nodes kept), or null when it could not be read.</summary>
        public ProfileDocument Document { get; private set; }

        public List<string> Errors { get; private set; }

        public List<string> Warnings { get; private set; }

        public bool IsValid
        {
            get { return Errors.Count == 0; }
        }

        public bool HasGameProfile
        {
            get { return Document != null && SeshatOperations.HasGameProfile(Document); }
        }

        /// <summary>The profile root's <c>level</c> (0 when absent).</summary>
        public long Level
        {
            get { return LevelOf(Document); }
        }

        public long Revision
        {
            get { return Document == null ? 0 : Document.Revision; }
        }

        public string Modified
        {
            get { return Document == null ? null : Document.Modified; }
        }

        /// <summary>UTF-8 byte count of <see cref="ProfileText"/>.</summary>
        public long ProfileLength
        {
            get { return ProfileText == null ? 0 : AdminFiles.Utf8Bytes(ProfileText).Length; }
        }

        // ------------------------------------------------------------------ export

        /// <summary>
        /// The bundle of a stored account, read under the account's lock (null when nothing is stored). Contains the
        /// progress as of the game's last save.
        /// </summary>
        public static SaveBundle Build(ServerContext context, LocalProfileStore store, string accountId, bool includeSettings)
        {
            string text;
            using (store.Locks.Acquire(accountId)) text = AtomicFile.ReadAllTextOrNull(store.PathFor(accountId));
            if (text == null) return null;

            var bundle = new SaveBundle();
            bundle.AccountId = accountId;
            bundle.ProfileText = text;
            bundle.Created = TimeFormat.Iso8601Millis(context.Clock.UtcNow);
            bundle.ServerVersion = McvsVersion.Current;
            bundle.ServerBuild = McvsVersion.Build;
            bundle.ServerInstance = context.InstanceId;
            Account account = context.Accounts.FindById(accountId);
            if (account != null)
            {
                bundle.Alias = JsonValue.AsString(JsonValue.Get(account.Data, AliasRegistry.DataKey));
                bundle.AccountCreated = account.Created;
            }
            DocumentStore messages = context.OpenStore(MessagesStore);
            foreach (string transport in Transports)
            {
                string message = messages.ReadText(MessageKey(accountId, transport));
                if (message != null) bundle.Messages[transport] = message;
            }
            if (includeSettings)
            {
                Dictionary<string, object> snapshot = context.Settings.Snapshot();
                JsonPath.RemoveKey(snapshot, "superPassword");
                bundle.Settings = snapshot;
            }
            try
            {
                bundle.Document = ProfileFile.Parse(text, accountId, null);
            }
            catch (ProfileFileException)
            {
                bundle.Document = null;
            }
            return bundle;
        }

        /// <summary>The bundle as compact JSON (field order as in design §6.1).</summary>
        public string ToJson()
        {
            byte[] profileBytes = AdminFiles.Utf8Bytes(ProfileText);
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["format"] = Format;
            o["version"] = Version;
            o["created"] = Created;
            var server = new Dictionary<string, object>(StringComparer.Ordinal);
            server["version"] = ServerVersion;
            server["build"] = ServerBuild;
            server["instance"] = ServerInstance;
            o["server"] = server;
            var account = new Dictionary<string, object>(StringComparer.Ordinal);
            account["id"] = AccountId;
            account["alias"] = Alias;
            account["created"] = AccountCreated;
            o["account"] = account;
            o["profile"] = ProfileText;
            o["profileLength"] = (long)profileBytes.Length;
            o["profileCrc32"] = Crc32Hex(profileBytes);
            var messages = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string transport in Transports)
            {
                string text;
                if (Messages.TryGetValue(transport, out text)) messages[transport] = text;
            }
            o["messages"] = messages;
            o["settings"] = Settings;
            return MiniJson.Serialize(o);
        }

        /// <summary>What the page shows of a save (design §4.6 "bundle").</summary>
        public Dictionary<string, object> Summary()
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["account"] = AccountId;
            o["alias"] = Alias;
            o["level"] = Level;
            o["revision"] = Revision;
            o["modified"] = Modified;
            o["hasGameProfile"] = HasGameProfile;
            o["size"] = ProfileLength;
            o["messages"] = (long)Messages.Count;
            o["created"] = Created;
            o["server"] = ServerVersion;
            return o;
        }

        // ------------------------------------------------------------------ import

        /// <summary>
        /// Parses and validates a bundle or a raw profile file. Never throws: problems are in <see cref="Errors"/> (any
        /// error: the save cannot be staged) and <see cref="Warnings"/>. <paramref name="nowUtc"/> only serves the
        /// "created in the future" warning.
        /// </summary>
        public static SaveBundle Parse(string text, DateTime nowUtc)
        {
            var bundle = new SaveBundle();
            try
            {
                bundle.ParseInto(text, nowUtc);
            }
            catch (Exception)
            {
                // A defensive net: the checks below are written not to throw, but nothing in an import may crash a request.
                if (bundle.Errors.Count == 0) bundle.Errors.Add(ErrorNotASave);
            }
            return bundle;
        }

        private void ParseInto(string text, DateTime nowUtc)
        {
            if (string.Equals(ScanTopLevel(text, "format") as string, ProfileFile.Format, StringComparison.Ordinal))
            {
                // A raw profile file (from the import folder or pasted whole): a bundle with that text and no messages.
                // (Its JSON is validated by the profile checks, which parse it once.)
                IsRawProfile = true;
                ProfileText = text;
                AccountId = ScanTopLevel(text, "account") as string;
                if (!IsAccountId(AccountId))
                {
                    Errors.Add(ErrorAccount);
                    return;
                }
                // The bundle's profile limit applies too (a raw file from the import folder or a transfer text may be larger).
                if (text.Length > MaxProfileBytes || AdminFiles.Utf8Bytes(text).Length > MaxProfileBytes)
                {
                    Errors.Add(ErrorIncomplete);
                    return;
                }
                CheckProfile(text, AccountId, true);
                return;
            }

            object parsed;
            Dictionary<string, object> top = null;
            if (text != null && MiniJson.TryParse(text, out parsed)) top = parsed as Dictionary<string, object>;
            if (top == null)
            {
                Errors.Add(ErrorNotASave);
                return;
            }

            string format = JsonValue.GetString(top, "format");
            object version;
            if (!string.Equals(format, Format, StringComparison.Ordinal) || !top.TryGetValue("version", out version)
                || !(version is long) || (long)version != Version)
            {
                Errors.Add(ErrorNotASave);
                return;
            }

            Created = JsonValue.GetString(top, "created");
            Dictionary<string, object> server = JsonValue.GetObject(top, "server");
            if (server != null)
            {
                ServerVersion = JsonValue.GetString(server, "version");
                ServerBuild = JsonValue.GetString(server, "build");
                ServerInstance = JsonValue.GetString(server, "instance");
            }
            Dictionary<string, object> account = JsonValue.GetObject(top, "account");
            if (account != null)
            {
                AccountId = JsonValue.Get(account, "id") as string;
                Alias = JsonValue.Get(account, "alias") as string;
                AccountCreated = JsonValue.Get(account, "created") as string;
            }
            if (!IsAccountId(AccountId))
            {
                Errors.Add(ErrorAccount);
                return;
            }

            ProfileText = JsonValue.Get(top, "profile") as string;
            byte[] profileBytes = ProfileText == null ? null : AdminFiles.Utf8Bytes(ProfileText);
            object length = JsonValue.Get(top, "profileLength");
            string crc = JsonValue.Get(top, "profileCrc32") as string;
            if (profileBytes == null || profileBytes.Length == 0 || profileBytes.Length > MaxProfileBytes
                || !(length is long) || (long)length != profileBytes.Length
                || !string.Equals(crc, Crc32Hex(profileBytes), StringComparison.Ordinal))
            {
                Errors.Add(ErrorIncomplete);
                return;
            }
            CheckProfile(ProfileText, AccountId, true);
            CheckMessages(JsonValue.Get(top, "messages"));
            Settings = JsonValue.GetObject(top, "settings");

            DateTime created;
            if (Created != null && TimeFormat.TryParse(Created, out created) && created > nowUtc.AddMinutes(10))
                Warnings.Add("The save was made in the future (" + Created + "): check the clock of the phone it comes from.");
        }

        /// <summary>The profile checks of §6.1 (shared by imports and by restores of a backup file).</summary>
        private void CheckProfile(string text, string accountId, bool requireGameProfile)
        {
            ProfileDocument doc;
            List<string> problems = ProfileProblems(text, accountId, requireGameProfile, Warnings, out doc);
            Errors.AddRange(problems);
            Document = doc;
        }

        /// <summary>
        /// The errors of §6.1's profile checks for a profile file text that should belong to <paramref name="accountId"/>:
        /// the <c>mcvs-offline/seshat-profile</c> envelope (a plain document is an error here), version ≤ 1, the envelope's
        /// account, and (when required) a non-empty game profile. Parse warnings go to <paramref name="warnings"/>;
        /// <paramref name="doc"/> is the parsed document (null when the text is not a profile file). The text is parsed once.
        /// </summary>
        public static List<string> ProfileProblems(string text, string accountId, bool requireGameProfile, List<string> warnings,
                                                   out ProfileDocument doc)
        {
            doc = null;
            var errors = new List<string>();
            // The envelope members are scanned (duplicate keys resolve to the last one, as in MiniJson); ProfileFile.Parse
            // then validates the whole text.
            object version = ScanTopLevel(text, "version");
            if (!string.Equals(ScanTopLevel(text, "format") as string, ProfileFile.Format, StringComparison.Ordinal)
                || (version != null && (!(version is long) || (long)version > ProfileFile.CurrentVersion || (long)version < 0))
                || !string.Equals(ScanTopLevel(text, "account") as string, accountId, StringComparison.Ordinal))
            {
                errors.Add(ErrorProfile);
                return errors;
            }
            var parseWarnings = new List<string>();
            try
            {
                doc = ProfileFile.Parse(text, accountId, parseWarnings);
            }
            catch (ProfileFileException)
            {
                errors.Add(ErrorProfile);
                return errors;
            }
            if (warnings != null) warnings.AddRange(parseWarnings);
            if (requireGameProfile && !SeshatOperations.HasGameProfile(doc)) errors.Add(ErrorNoProgress);
            return errors;
        }

        /// <summary>A small top-level member of a JSON object text, found without parsing the text (null when absent or large).</summary>
        private static object ScanTopLevel(string text, string key)
        {
            int start, length;
            if (text == null || !JsonSpanScanner.TryFindValue(text, new[] { key }, out start, out length) || length > 256) return null;
            object value;
            return MiniJson.TryParse(text.Substring(start, length), out value) ? value : null;
        }

        private void CheckMessages(object node)
        {
            if (node == null) return;
            var messages = node as Dictionary<string, object>;
            if (messages == null)
            {
                Errors.Add(ErrorInbox);
                return;
            }
            foreach (var kv in messages)
            {
                string text = kv.Value as string;
                object parsed;
                if (Array.IndexOf(Transports, kv.Key) < 0 || text == null || AdminFiles.Utf8Bytes(text).Length > MaxMessageBytes
                    || !MiniJson.TryParse(text, out parsed) || !(parsed is Dictionary<string, object>))
                {
                    Errors.Add(ErrorInbox);
                    Messages.Clear();
                    return;
                }
                Messages[kv.Key] = text;
            }
        }

        // ------------------------------------------------------------------ helpers

        public const string MessagesStore = "messages";

        /// <summary>The Hermes mailbox key of an account and transport (Mailboxes: "&lt;account&gt;.&lt;transport&gt;").</summary>
        public static string MessageKey(string accountId, string transport)
        {
            return accountId + "." + transport;
        }

        /// <summary>^[0-9a-f]{24}$: the form of AccountStore.DeriveAccountId (lower case only: Android's shared storage is case-insensitive).</summary>
        public static bool IsAccountId(string id)
        {
            return AdminFiles.IsLowerHex(id, 24);
        }

        public static string Crc32Hex(byte[] data)
        {
            return Crc32.Compute(data).ToString("x8", CultureInfo.InvariantCulture);
        }

        /// <summary>The root <c>level</c> of a profile document, 0 when absent.</summary>
        public static long LevelOf(ProfileDocument doc)
        {
            object node;
            if (doc == null || doc.Root == null || !doc.Root.TryGetValue("level", out node)) return 0;
            return JsonValue.AsLong(ProfileJson.Unwrap(node)) ?? 0;
        }
    }
}
