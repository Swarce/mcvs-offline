using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm.Collector;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>One key of the settings catalogue (design §3).</summary>
    public sealed class SettingDefinition
    {
        public string Key { get; internal set; }

        /// <summary>Group id: gameplay, graphics, economy, unlocks, liveops, diagnostics.</summary>
        public string Group { get; internal set; }

        public string Label { get; internal set; }

        public string Help { get; internal set; }

        /// <summary>bool, int, number, enum, string, tokens.</summary>
        public string Type { get; internal set; }

        public double? Min { get; internal set; }

        public double? Max { get; internal set; }

        public double? Step { get; internal set; }

        /// <summary>enum values, or the tokens a tokens value may hold.</summary>
        public List<string> Options { get; internal set; }

        /// <summary>For the page (a JavaScript regular expression); the server checks with <see cref="Check"/>.</summary>
        public string Pattern { get; internal set; }

        public int? MaxLength { get; internal set; }

        public object Default { get; internal set; }

        /// <summary>live (read on every use), menu (next CRM fetch: after the next match) or restart.</summary>
        public string Apply { get; internal set; }

        public bool Editable { get; internal set; }

        /// <summary>Shown before saving a change (with <see cref="ConfirmWhen"/>), or null.</summary>
        public string Confirm { get; internal set; }

        /// <summary>any, enable (false → true), decrease; or null.</summary>
        public string ConfirmWhen { get; internal set; }

        /// <summary>any or enable: a "presettings" backup of the current player's save is taken before saving; or null.</summary>
        public string BackupBefore { get; internal set; }

        /// <summary>
        /// For a key whose module does not simply fall back to its default (the Cosmetic crate's, read by
        /// <c>CollectorOptions</c>): the value the module uses, read from the whole settings, and a note when it differs
        /// from what is stored (null: <see cref="SettingsSchema.Coerce"/> of the stored value).
        /// </summary>
        internal SettingReader Reader { get; set; }

        /// <summary>The server-side check of a string value (patterns without a regex engine) and its message.</summary>
        internal Func<string, bool> Check { get; set; }

        internal string CheckMessage { get; set; }

        /// <summary>A copy with another help text (a platform's wording of the same key).</summary>
        internal SettingDefinition WithHelp(string help)
        {
            var copy = (SettingDefinition)MemberwiseClone();
            copy.Help = help ?? "";
            return copy;
        }
    }

    /// <summary>
    /// One group of the page as a platform shows it (<see cref="SettingsSchema.GroupsFor"/>): the editable keys, or for
    /// the "advanced" group the read-only keys with their labels and, where there is one, a description.
    /// </summary>
    public sealed class SettingGroupView
    {
        public SettingGroup Group { get; internal set; }

        /// <summary>The group's editable keys in page order (empty for "advanced").</summary>
        public List<SettingDefinition> Keys { get; internal set; }

        /// <summary>The read-only keys (key, label) of "advanced", in page order; empty for the other groups.</summary>
        public List<KeyValuePair<string, string>> Advanced { get; internal set; }

        /// <summary>Descriptions of some read-only keys (key → text); keys without one are absent.</summary>
        public Dictionary<string, string> AdvancedHelp { get; internal set; }
    }

    /// <summary>The value a module uses for a key, and a note for the page when it is not what settings.json holds.</summary>
    internal delegate object SettingReader(SettingDefinition def, Settings settings, out string invalidNote);

    public sealed class SettingGroup
    {
        public SettingGroup(string id, string label)
        {
            Id = id;
            Label = label;
        }

        public string Id { get; private set; }

        public string Label { get; private set; }
    }

    public sealed class SettingPreset
    {
        public string Id { get; internal set; }

        public string Label { get; internal set; }

        public Dictionary<string, object> Values { get; internal set; }
    }

    /// <summary>
    /// The settings catalogue the page renders and the server enforces (design §3): the allowlist of editable keys with
    /// type, page range, default, when the change applies and the confirmations; the read-only "Advanced" keys; the
    /// server-side denylist; the Economy presets. The page has no key list of its own.
    /// </summary>
    public static class SettingsSchema
    {
        public const string TypeBool = "bool";
        public const string TypeInt = "int";
        public const string TypeNumber = "number";
        public const string TypeEnum = "enum";
        public const string TypeString = "string";
        public const string TypeTokens = "tokens";

        public const string Live = "live";
        public const string Menu = "menu";
        public const string Restart = "restart";

        /// <summary>The Cosmetic crate's kill switch (O7).</summary>
        public const string CollectorEnabledKey = CollectorOptions.SettingsPath + ".enabled";

        /// <summary>Read by the embedded host's AdminHooks.AccountMenuButtons each time Settings › Account opens.</summary>
        public const string AccountMenuHideDeadButtonsKey = "admin.accountMenu.hideDeadButtons";

        private static readonly SettingGroup[] GroupList =
        {
            new SettingGroup("gameplay", "Gameplay"),
            new SettingGroup("graphics", "Graphics"),
            new SettingGroup("economy", "Economy"),
            new SettingGroup("unlocks", "Unlocks & codes"),
            new SettingGroup("liveops", "Live ops"),
            new SettingGroup("diagnostics", "Diagnostics"),
            new SettingGroup("advanced", "Advanced"),
        };

        private static readonly List<SettingDefinition> KeyList = BuildKeys();

        /// <summary>
        /// Shown read-only (design §3.2), in this order, as key, label, key, label... (a flat list: the game's trimmed
        /// mscorlib cannot resolve the accessors of a two-dimensional array).
        /// </summary>
        private static readonly string[] AdvancedList =
        {
            "port", "Port", "bind", "Listen address", "publicUrl", "Public URL", "lanMode", "LAN mode",
            "superCredential", "Game server credential", "superPassword", "Super password", "dataCenter", "Data center",
            "logging.consoleLevel", "Console log detail", "logging.bodyLimit", "Logged body limit",
            "logging.fileMaxBytes", "server.log size", "logging.fileCount", "server.log files",
            "embedded.manageArgs", "Manage args.txt", "embedded.lanAdvertiseHost", "LAN address to advertise",
            "embedded.nativeOnlineSdk", "Native online SDK",
            "embedded.physics.serverMultithreaded", "Server physics threads",
            "embedded.physics.clientMultithreaded", "Client physics threads",
            "embedded.physics.stepGate", "Physics step gate", "embedded.physics.stepGateTimeoutMs", "Physics step gate timeout (ms)",
            "install.manageInstaller", "Manage the installer", "install.suppressUpdateCheck", "Skip the update check",
            "install.rebuildIndex", "Rebuild the data index", "install.verifyRestoredData", "Verify restored data",
            "install.monitorInstaller", "Log the installer",
            "crm.usePreconfiguration", "Use the game's CRM preconfiguration", "crm.offlineStore", "Offline store",
            "crm.iap", "In-app purchases", "crm.omitOwnedSignatures", "Hide owned signatures in the shop",
            "crm.overlay", "CRM overlay", "crm.pointcutActions", "Extra pointcut actions", "assets.coupons", "Extra codes",
            "matchmaker.tsRoomCapacity", "Menu room capacity", "matchmaker.gsRoomCapacity", "Match room capacity",
            "storage.cache", "Profile cache", "storage.absentField", "Absent profile fields",
            "auth.requireSuperPassword", "Require the super password", "admin.enabled", "This page",
            "admin.openVia", "How the page opens", "admin.restartVia", "How the game restarts",
            "security.hostCheck", "Host check",
            "notifications.request", "Ask for the notification permission (Android 13+)",
            "notifications.requestedAt", "Notification permission asked",
        };

        /// <summary>Never editable, whatever the schema says (design §3.2); prefixes end with '.'.</summary>
        private static readonly string[] Denylist =
        {
            "port", "bind", "publicUrl", "lanMode", "superCredential", "superPassword", "dataCenter", "pandora.",
            "embedded.manageArgs", "embedded.lanAdvertiseHost", "crm.overlay", "crm.pointcutActions", "crm.developerIpRange",
            "assets.coupons", "admin.enabled", "admin.openVia", "admin.restartVia", "security.", "install.",
        };

        /// <summary><c>AdminHostInfo.Platform</c> of the PC (Steam) host.</summary>
        public const string PlatformWindows = "windows";

        /// <summary>
        /// Keys the PC host neither writes nor uses (docs/design/pc-port.md §3.6): the Android installer and HD textures,
        /// notifications, the public log copy and the Mono thread dump. Prefixes end with '.'.
        /// </summary>
        private static readonly string[] WindowsHidden =
        {
            "hd.", "install.", "notifications.", "diagnostics.publicLogCopy", "diagnostics.threadDump",
        };

        /// <summary>Read-only keys only the PC host has, as key, label, description, inserted after <see cref="WindowsAdvancedAfter"/>.</summary>
        private static readonly string[] WindowsAdvanced =
        {
            "embedded.physics.keepPhysicsLayer", "Keep the levels' Physics layer",
            "On: the 'graphics' patch removes Physics from every excludedLoadingLayers list of GameOptions_windows.json, so "
            + "the match servers inside the game always load the levels' collision layer (the PC's low-memory fallback would "
            + "drop it). Read at the next start.",
        };

        private const string WindowsAdvancedAfter = "embedded.physics.stepGateTimeoutMs";

        private const string WindowsGraphicsProfileHelp =
            "Which of Gameloft's own GPU profiles the game uses. Auto: the game's choice for this PC (MidGPU on graphics cards "
            + "it does not know). Mid: MidGPU. High: HighGPU (normal maps, full lightmaps, shadows, bloom). Ultra: HighGPU on PC, "
            + "which has no High_Ultra. Needs a build with the 'graphics' patch.";

        public static List<SettingGroup> Groups
        {
            get { return new List<SettingGroup>(GroupList); }
        }

        /// <summary>
        /// The page's groups as <paramref name="platform"/> (<c>AdminHostInfo.Platform</c>) shows them (§4.6 of
        /// docs/design/pc-port.md). On <see cref="PlatformWindows"/>: no <c>hd.*</c>, <c>install.*</c>,
        /// <c>notifications.*</c>, <c>diagnostics.publicLogCopy</c> or <c>diagnostics.threadDump</c> entry (labels and
        /// settings), the PC wording of <c>graphics.profile</c>, and <c>embedded.physics.keepPhysicsLayer</c> among the
        /// read-only keys. Any other platform (android, directory, none) gets exactly <see cref="Groups"/>,
        /// <see cref="Keys"/> and <see cref="AdvancedKeys"/>.
        /// </summary>
        public static List<SettingGroupView> GroupsFor(string platform)
        {
            bool windows = string.Equals(platform, PlatformWindows, StringComparison.Ordinal);
            var views = new List<SettingGroupView>();
            foreach (SettingGroup group in GroupList)
            {
                var view = new SettingGroupView
                {
                    Group = group,
                    Keys = new List<SettingDefinition>(),
                    Advanced = new List<KeyValuePair<string, string>>(),
                    AdvancedHelp = new Dictionary<string, string>(StringComparer.Ordinal),
                };
                if (group.Id == "advanced")
                {
                    foreach (KeyValuePair<string, string> kv in AdvancedKeys)
                    {
                        if (windows && HiddenOnWindows(kv.Key)) continue;
                        view.Advanced.Add(kv);
                        if (!windows || kv.Key != WindowsAdvancedAfter) continue;
                        for (int i = 0; i + 2 < WindowsAdvanced.Length; i += 3)
                        {
                            view.Advanced.Add(new KeyValuePair<string, string>(WindowsAdvanced[i], WindowsAdvanced[i + 1]));
                            view.AdvancedHelp[WindowsAdvanced[i]] = WindowsAdvanced[i + 2];
                        }
                    }
                }
                else
                {
                    foreach (SettingDefinition def in KeyList)
                    {
                        if (def.Group != group.Id) continue;
                        if (!windows)
                        {
                            view.Keys.Add(def);
                            continue;
                        }
                        if (HiddenOnWindows(def.Key)) continue;
                        view.Keys.Add(def.Key == "graphics.profile" ? def.WithHelp(WindowsGraphicsProfileHelp) : def);
                    }
                }
                views.Add(view);
            }
            return views;
        }

        private static bool HiddenOnWindows(string key)
        {
            foreach (string hidden in WindowsHidden)
            {
                if (hidden.EndsWith(".", StringComparison.Ordinal) ? key.StartsWith(hidden, StringComparison.Ordinal) : key == hidden)
                    return true;
            }
            return false;
        }

        /// <summary>Every editable key of §3.1, in page order.</summary>
        public static List<SettingDefinition> Keys
        {
            get { return new List<SettingDefinition>(KeyList); }
        }

        /// <summary>The Advanced keys (key, label), in page order.</summary>
        public static List<KeyValuePair<string, string>> AdvancedKeys
        {
            get
            {
                var list = new List<KeyValuePair<string, string>>();
                for (int i = 0; i + 1 < AdvancedList.Length; i += 2)
                    list.Add(new KeyValuePair<string, string>(AdvancedList[i], AdvancedList[i + 1]));
                return list;
            }
        }

        public static List<SettingPreset> Presets
        {
            get
            {
                return new List<SettingPreset>
                {
                    Preset("standard", "Standard", 1.0, 1.0, 1.0, 1.0, 1.0),
                    Preset("relaxed", "Relaxed", 2.0, 1.0, 1.0, 0.5, 1.0),
                    Preset("fast", "Fast", 5.0, 1.0, 1.0, 1.0, 1.0),
                };
            }
        }

        /// <summary>The editable definition of <paramref name="key"/>, or null (not in the allowlist, or denied).</summary>
        public static SettingDefinition Find(string key)
        {
            if (string.IsNullOrEmpty(key) || IsDenied(key)) return null;
            foreach (SettingDefinition def in KeyList)
            {
                if (string.Equals(def.Key, key, StringComparison.Ordinal)) return def.Editable ? def : null;
            }
            return null;
        }

        /// <summary>True for the keys of the server-side denylist (and everything under its prefixes).</summary>
        public static bool IsDenied(string key)
        {
            if (key == null) return true;
            foreach (string denied in Denylist)
            {
                if (denied.EndsWith(".", StringComparison.Ordinal))
                {
                    if (key.StartsWith(denied, StringComparison.Ordinal) || key == denied.Substring(0, denied.Length - 1)) return true;
                }
                else if (key == denied)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Validates a value sent by the page against the key's type and page range. On success
        /// <paramref name="normalized"/> is the value to store (int → long, number → double, enum → its option's spelling,
        /// tokens → one space-separated string); otherwise <paramref name="problem"/> is a sentence for the player.
        /// </summary>
        public static bool Validate(SettingDefinition def, object value, out object normalized, out string problem)
        {
            normalized = null;
            problem = null;
            switch (def.Type)
            {
                case TypeBool:
                    if (value is bool)
                    {
                        normalized = value;
                        return true;
                    }
                    problem = "Must be on or off.";
                    return false;

                case TypeInt:
                {
                    long? l = value is long ? (long)value : value is double && Num.IsIntegralLong((double)value) ? (long)(double)value : (long?)null;
                    if (l == null)
                    {
                        problem = "Must be a whole number.";
                        return false;
                    }
                    if ((def.Min.HasValue && l.Value < def.Min.Value) || (def.Max.HasValue && l.Value > def.Max.Value))
                    {
                        problem = "Must be between " + Format(def.Min) + " and " + Format(def.Max) + ".";
                        return false;
                    }
                    normalized = l.Value;
                    return true;
                }

                case TypeNumber:
                {
                    double? d = value is double ? (double)value : value is long ? (double)(long)value : (double?)null;
                    if (d == null || double.IsNaN(d.Value) || double.IsInfinity(d.Value))
                    {
                        problem = "Must be a number.";
                        return false;
                    }
                    if ((def.Min.HasValue && d.Value < def.Min.Value - 1e-9) || (def.Max.HasValue && d.Value > def.Max.Value + 1e-9))
                    {
                        problem = "Must be between " + Format(def.Min) + " and " + Format(def.Max) + ".";
                        return false;
                    }
                    normalized = d.Value;
                    return true;
                }

                case TypeEnum:
                {
                    string s = value as string;
                    string option = s == null ? null : OptionOf(def, s);
                    if (option == null)
                    {
                        problem = "Must be one of: " + string.Join(", ", def.Options.ToArray()) + ".";
                        return false;
                    }
                    normalized = option;
                    return true;
                }

                case TypeString:
                {
                    string s = value as string;
                    if (s == null)
                    {
                        problem = "Must be text.";
                        return false;
                    }
                    if ((def.MaxLength.HasValue && s.Length > def.MaxLength.Value) || (def.Check != null && !def.Check(s)))
                    {
                        problem = def.CheckMessage ?? "Not a valid value.";
                        return false;
                    }
                    normalized = s;
                    return true;
                }

                case TypeTokens:
                {
                    string s = value as string;
                    var tokens = new List<string>();
                    if (s == null)
                    {
                        problem = "Must be text.";
                        return false;
                    }
                    foreach (string piece in s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string option = OptionOf(def, piece);
                        if (option == null || tokens.Contains(option))
                        {
                            problem = "Choose among: " + string.Join(", ", def.Options.ToArray()) + " (each once).";
                            return false;
                        }
                        tokens.Add(option);
                    }
                    normalized = string.Join(" ", tokens.ToArray());
                    return true;
                }

                default:
                    problem = "This setting cannot be changed here.";
                    return false;
            }
        }

        /// <summary>The page's note for a stored value of the wrong type (<see cref="Coerce"/>).</summary>
        public const string InvalidNote = "The value in settings.json is not valid; the default is used.";

        /// <summary>
        /// The value a key has now, as the server uses it (the page's <c>value</c>): <see cref="Coerce"/> of the stored
        /// (effective) value, or the key's <see cref="SettingDefinition.Reader"/>. <paramref name="invalidNote"/> is null
        /// when settings.json holds exactly that value (or nothing), else what the page says about it (<c>invalidStored</c>).
        /// </summary>
        public static object Read(SettingDefinition def, Settings settings, out string invalidNote)
        {
            if (def.Reader != null) return def.Reader(def, settings, out invalidNote);
            bool invalid;
            object value = Coerce(def, settings.Get(def.Key), out invalid);
            invalidNote = invalid ? InvalidNote : null;
            return value;
        }

        /// <summary>
        /// The stored (effective) value for display, coerced to the key's type: null gives the default; a value of the
        /// wrong type gives the default with <paramref name="invalid"/> set.
        /// </summary>
        public static object Coerce(SettingDefinition def, object stored, out bool invalid)
        {
            invalid = false;
            if (stored == null) return JsonValue.DeepClone(def.Default);
            switch (def.Type)
            {
                case TypeBool:
                    if (stored is bool) return stored;
                    break;
                case TypeInt:
                    if (stored is long) return stored;
                    if (stored is double && Num.IsIntegralLong((double)stored)) return (long)(double)stored;
                    break;
                case TypeNumber:
                    if (stored is double) return stored;
                    if (stored is long) return (double)(long)stored;
                    break;
                case TypeEnum:
                {
                    string option = stored is string ? OptionOf(def, (string)stored) : null;
                    if (option != null) return option;
                    break;
                }
                case TypeString:
                case TypeTokens:
                    if (stored is string) return stored;
                    break;
            }
            invalid = true;
            return JsonValue.DeepClone(def.Default);
        }

        /// <summary>
        /// The Cosmetic crate's keys as <see cref="CollectorOptions.FromSettings"/> reads them (the only parser of
        /// <c>economy.collector.*</c>): the shorthand <c>"collector": false</c>, the spellings "off"/"yes"/1, an
        /// unreadable kill switch or an unreadable settings.json (off, O7), item counts as strings or out of range (the
        /// collector's value). Flagged whenever that is not what settings.json holds as the key's type.
        /// </summary>
        private static object ReadCollectorKey(SettingDefinition def, Settings settings, out string invalidNote)
        {
            invalidNote = null;
            bool isSwitch = def.Key == CollectorEnabledKey;
            CollectorOptions used = CollectorOptions.FromSettings(settings, null);
            object value = isSwitch ? (object)used.Enabled : (long)used.ItemsPerCrate;
            if (settings.IsFallback)
            {
                if (isSwitch) invalidNote = "settings.json cannot be read, so the Cosmetic crate is off until it is repaired.";
                return value;
            }
            object collector = settings.Get(CollectorOptions.SettingsPath);
            var block = collector as Dictionary<string, object>;
            object stored = null;
            if (block != null) block.TryGetValue(def.Key.Substring(CollectorOptions.SettingsPath.Length + 1), out stored);
            else if (isSwitch) stored = collector; // "collector": false, the shorthand of the kill switch
            if (stored == null) return value;
            bool invalid;
            object coerced = Coerce(def, stored, out invalid);
            if (!invalid && JsonValue.DeepEquals(coerced, value)) return value;
            string held = "settings.json holds " + MiniJson.Serialize(stored) + (block == null ? " as economy.collector" : "");
            if (isSwitch && !CollectorOptions.Switch(stored).HasValue)
                invalidNote = held + ", which the server cannot read as on or off: the Cosmetic crate is off.";
            else if (isSwitch)
                invalidNote = held + ", which the server reads as " + (used.Enabled ? "on" : "off") + ".";
            else
                invalidNote = held + "; the server uses " + used.ItemsPerCrate.ToString(CultureInfo.InvariantCulture) + ".";
            return value;
        }

        /// <summary>The schema fields of a key as the page reads them (design §4.4), without value.</summary>
        public static Dictionary<string, object> Describe(SettingDefinition def)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["key"] = def.Key;
            o["label"] = def.Label;
            o["help"] = def.Help;
            o["type"] = def.Type;
            if (def.Type == TypeInt || def.Type == TypeNumber)
            {
                o["min"] = NumberValue(def.Type, def.Min);
                o["max"] = NumberValue(def.Type, def.Max);
                o["step"] = NumberValue(def.Type, def.Step ?? 1);
            }
            if (def.Options != null) o["options"] = new List<object>(def.Options.ToArray());
            if (def.Pattern != null) o["pattern"] = def.Pattern;
            if (def.MaxLength.HasValue) o["maxLength"] = (long)def.MaxLength.Value;
            o["default"] = JsonValue.DeepClone(def.Default);
            o["apply"] = def.Apply;
            o["editable"] = def.Editable;
            o["confirm"] = def.Confirm;
            o["confirmWhen"] = def.ConfirmWhen;
            o["backupBefore"] = def.BackupBefore;
            return o;
        }

        private static object NumberValue(string type, double? value)
        {
            if (value == null) return null;
            if (type == TypeInt) return (long)value.Value;
            return value.Value;
        }

        private static string OptionOf(SettingDefinition def, string value)
        {
            if (def.Options == null) return null;
            foreach (string option in def.Options)
            {
                if (string.Equals(option, value, StringComparison.OrdinalIgnoreCase)) return option;
            }
            return null;
        }

        private static string Format(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "?";
        }

        private static SettingPreset Preset(string id, string label, double versusCoins, double credits, double xp, double prices, double dailyCap)
        {
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            values["economy.versusCoinsMultiplier"] = versusCoins;
            values["economy.creditsMultiplier"] = credits;
            values["economy.xpMultiplier"] = xp;
            values["economy.priceMultiplier"] = prices;
            values["crm.dailyCapMultiplier"] = dailyCap;
            return new SettingPreset { Id = id, Label = label, Values = values };
        }

        // ------------------------------------------------------------------ the catalogue (design §3.1)

        private static List<SettingDefinition> BuildKeys()
        {
            const string backupConfirm = "Changing this starts a new season now; the running season's ranks and progress reset. "
                                         + "A backup of your save is made now (season ranks are not part of it).";
            var keys = new List<SettingDefinition>
            {
                // Gameplay
                Bool("gameplay", "instantBots", "Instant bot matches", true, Menu,
                     "Matches start at once and bots fill the empty slots. Off: the game waits for other players first."),
                Confirmed(Bool("gameplay", "devMenu", "Developer cheat menu", false, Restart,
                               "Gameloft's developer menu (IsDevelopment in the menu server handshake)."),
                          "Cheats can damage your profile. A backup of your save is made now.", "enable", "enable"),
                Bool("gameplay", "leaderboards.virtualPlayers", "Leaderboard opponents", true, Live,
                     "Division leaderboards are filled with generated players."),
                Text("gameplay", "geo.country", "Country", "US", Restart, "Two-letter country code reported to the game.",
                     "^[A-Z]{2}$", 2, IsCountry, "Two capital letters, for example US."),

                // Graphics (patch 'graphics' and the embedded host read them at the next start; docs/research/pc-textures.md)
                Choice("graphics", "graphics.profile", "Graphics profile", "auto", Restart,
                       "Which of Gameloft's own GPU profiles the game uses. Auto: the game's choice for this phone (MidGPU on "
                       + "phones it does not know). Mid: MidGPU. High: HighGPU (normal maps, full lightmaps, shadows, bloom). "
                       + "Ultra: High_Ultra (everything; the heaviest). Needs a build with the 'graphics' patch.",
                       "auto", "mid", "high", "ultra"),
                Confirmed(Bool("graphics", "graphics.fullProfile", "High profiles change world loading too", false, Restart,
                               "Off: High and Ultra keep two options at the Mid values because the match servers in the game share "
                               + "them (the levels' Physics layer stays unloaded, bots think at the usual rate). On: Gameloft's "
                               + "profile as it is."),
                          "This also changes the match servers inside the game: they load the levels' Physics layer and run bot AI "
                          + "twice as often. If matches stall or stutter, turn it off again.", "enable", null),
                Bool("graphics", "hd.enabled", "HD textures", true, Restart,
                     "On: an HD texture pack you put in McvsOffline/hd/ (mcvs-hd-*.zip, made on your PC from your own Steam "
                     + "copy) is installed into the game's data at the next start. Off: an installed pack is removed at the "
                     + "next start and the original textures come back. Without a pack nothing changes."),

                // Economy
                Number("economy", "economy.versusCoinsMultiplier", "VersusCoins per match", 0.1, 20, 0.1, Menu,
                       "Multiplies VersusCoins for wins, defeats, MVP and the daily cap."),
                Number("economy", "economy.creditsMultiplier", "Credits per match", 0.1, 20, 0.1, Menu,
                       "Same for Credits (faster loot crates)."),
                Number("economy", "economy.xpMultiplier", "XP speed", 0.1, 20, 0.1, Menu,
                       "Divides the XP needed per player level and agent mastery level."),
                Number("economy", "economy.priceMultiplier", "Shop prices", 0.05, 10, 0.05, Menu,
                       "Scales every VersusCoins price in the offline shop (at least 1)."),
                Number("economy", "crm.dailyCapMultiplier", "Daily cap", 0.1, 20, 0.1, Menu,
                       "Extra factor on the two daily caps only."),
                // Read by CollectorOptions at the next CRM fetch (docs/design/economy-collector.md §4.4), and shown as it
                // reads them: a stored kill switch it cannot read switches the crate off (O7), and the page says so.
                // Switching replaces the running daily missions and weekly event once (their progress is lost, §3.2).
                ReadingCollector(Confirmed(Bool("economy", CollectorEnabledKey, "Cosmetic crate", true, Menu,
                                                "Random cosmetic crates in daily missions, weekly events and season rewards. "
                                                + "Off restores the original rewards. Switching restarts the running daily "
                                                + "missions and weekly event."),
                                           "The running daily missions and weekly event are replaced after the next match: "
                                           + "their progress is lost (rewards already claimed stay).", "any", null)),
                ReadingCollector(Integer("economy", CollectorOptions.SettingsPath + ".itemsPerCrate", "Items per Cosmetic crate",
                                         CollectorOptions.DefaultItemsPerCrate, 1, 10, 1, Menu, "")),

                // Unlocks & codes
                Confirmed(Bool("unlocks", "unlockAll", "Unlock everything", false, Live,
                               "Grants what UNLOCKALL grants, through the inventory, at the game's next save (~11,400 rewards; "
                               + "the level jumps to ~46 and the game lags while it levels up)."),
                          "This cannot be undone: turning it off later removes nothing. A backup of your save is made now.", "enable", "enable"),
                Bool("unlocks", "assets.couponsEnabled", "Unlock codes", true, Live,
                     "Settings > Account > Redeem code accepts the codes listed below."),
                Bool("unlocks", "crm.redeemCoupons", "Redeem-code button", true, Menu,
                     "Shows the Redeem code button in Settings > Account."),
                Bool("unlocks", AccountMenuHideDeadButtonsKey, "Only Redeem code and MCVS Offline in Settings > Account", true, Live,
                     "Hides the Facebook, Google Play Games and Restore buttons, which cannot work offline, and centres the "
                     + "buttons that are left. Applies the next time Settings > Account opens."),
                Integer("unlocks", "unlocks.batchSize", "Rewards per batch", 400, 50, 400, 50, Live,
                        "Unlock-all rewards per batch; keep at most 400 (reward popups)."),
                Text("unlocks", "admin.redeemCode", "Code that opens this page", "MCVSOFFLINE", Live,
                     "Typing it in Redeem code opens this page (the game then says the code does not exist). Empty: off.",
                     "^([A-Z0-9]{4,20})?$", 20, IsRedeemCode, "4 to 20 capital letters or digits, or empty."),

                // Live ops
                Bool("liveops", "liveops.enabled", "Live events", true, Menu, "Division seasons, daily missions and weekly events."),
                Bool("liveops", "liveops.divisionSeasons", "Division seasons", true, Menu, ""),
                Bool("liveops", "liveops.dailyQuests", "Daily missions", true, Menu, ""),
                Bool("liveops", "liveops.weeklyEvents", "Weekly events", true, Menu, ""),
                Confirmed(Integer("liveops", "liveops.seasonDays", "Season length (days)", 7, 1, 28, 1, Menu, ""),
                          backupConfirm, "any", "any"),
                Confirmed(Text("liveops", "liveops.seasonStart", "Season anchor (UTC)", "2024-01-01T00:00:00Z", Menu,
                               "Any season start; seasons repeat every season length from it.",
                               @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", 20, IsIsoUtc, "A UTC date and time like 2024-01-01T00:00:00Z."),
                          backupConfirm, "any", "any"),
                Integer("liveops", "liveops.dailyResetHour", "Daily reset hour (UTC)", 0, 0, 23, 1, Menu, ""),
                Choice("liveops", "liveops.modifiers", "Weekly modifier", "rotate", Menu, "Rotate changes it every season.",
                       "rotate", "off", "standard", "coinRush", "trophyFever", "creditRush", "mvpBounty", "rivalSurge"),
                Number("liveops", "liveops.difficulty", "Event difficulty", 0.1, 10, 0.1, Menu, ""),
                Number("liveops", "liveops.seasonRewardMultiplier", "Season rewards", 0.1, 20, 0.1, Menu, ""),
                Bool("liveops", "crm.newsfeedLink", "Inbox message for this page", true, Menu,
                     "The \"MCVS Offline\" inbox message. Off removes it at the next CRM fetch."),

                // Diagnostics
                Confirmed(Integer("diagnostics", "storage.backups.keep", "Automatic backups kept", 20, 0, 200, 1, Live,
                                  "Per account. Counts only the automatic backups taken every N minutes; backups before imports, "
                                  + "restores, risky settings and events, and your own backups are kept separately. "
                                  + "0: no new automatic backups (existing ones stay)."),
                          "Older automatic backups beyond the new number are deleted at the next automatic backup.", "decrease", null),
                Integer("diagnostics", "storage.backups.intervalMinutes", "Minutes between automatic backups", 30, 5, 1440, 5, Live, ""),
                Bool("diagnostics", "storage.autoRestoreCorrupt", "Repair unreadable saves", true, Live,
                     "An unreadable save file is replaced by its newest readable backup (the broken file is kept)."),
                Choice("diagnostics", "logging.fileLevel", "server.log detail", "debug", Restart, "",
                       "trace", "debug", "info", "warn", "error"),
                TokenList("diagnostics", "gameLog.levels", "Game log levels", "error warning", Restart,
                          "A build patched with --game-log all logs every level anyway.", "error", "warning", "info", "debug"),
                Text("diagnostics", "gameLog.mute", "Muted game log categories", "profile:debug", Restart,
                     "Space-separated category:level pairs (levels all, debug, info, warning, error).",
                     "^([A-Za-z]+:(all|debug|info|warning|error))?( [A-Za-z]+:(all|debug|info|warning|error))*$", 512,
                     IsMuteList, "Space-separated category:level pairs, for example profile:debug."),
                Bool("diagnostics", "gameLog.file", "Copy the game's log to logs/game.log", true, Restart, ""),
                Bool("diagnostics", "diagnostics.watchdog", "Freeze watchdog", true, Restart, ""),
                Bool("diagnostics", "diagnostics.threadDump", "Thread dumps on freezes", true, Restart, ""),
                Bool("diagnostics", "diagnostics.publicLogCopy", "Log copies in Downloads/MCVS-Offline", true, Restart,
                     "Android 11 and later."),
                Bool("diagnostics", "tracking.disable", "Block the game's analytics uploads", false, Live, ""),
            };
            return keys;
        }

        private static SettingDefinition Base(string group, string key, string label, string type, object defaultValue, string apply, string help)
        {
            return new SettingDefinition
            {
                Group = group, Key = key, Label = label, Type = type, Default = defaultValue, Apply = apply, Help = help ?? "",
                Editable = true,
            };
        }

        private static SettingDefinition Bool(string group, string key, string label, bool defaultValue, string apply, string help)
        {
            return Base(group, key, label, TypeBool, defaultValue, apply, help);
        }

        private static SettingDefinition ReadingCollector(SettingDefinition def)
        {
            def.Reader = ReadCollectorKey;
            return def;
        }

        private static SettingDefinition Integer(string group, string key, string label, long defaultValue, long min, long max, long step,
                                                 string apply, string help)
        {
            SettingDefinition def = Base(group, key, label, TypeInt, defaultValue, apply, help);
            def.Min = min;
            def.Max = max;
            def.Step = step;
            return def;
        }

        private static SettingDefinition Number(string group, string key, string label, double min, double max, double step,
                                                string apply, string help)
        {
            SettingDefinition def = Base(group, key, label, TypeNumber, 1.0, apply, help);
            def.Min = min;
            def.Max = max;
            def.Step = step;
            return def;
        }

        private static SettingDefinition Choice(string group, string key, string label, string defaultValue, string apply, string help,
                                                params string[] options)
        {
            SettingDefinition def = Base(group, key, label, TypeEnum, defaultValue, apply, help);
            def.Options = new List<string>(options);
            return def;
        }

        private static SettingDefinition TokenList(string group, string key, string label, string defaultValue, string apply, string help,
                                                   params string[] options)
        {
            SettingDefinition def = Base(group, key, label, TypeTokens, defaultValue, apply, help);
            def.Options = new List<string>(options);
            return def;
        }

        private static SettingDefinition Text(string group, string key, string label, string defaultValue, string apply, string help,
                                              string pattern, int maxLength, Func<string, bool> check, string checkMessage)
        {
            SettingDefinition def = Base(group, key, label, TypeString, defaultValue, apply, help);
            def.Pattern = pattern;
            def.MaxLength = maxLength;
            def.Check = check;
            def.CheckMessage = checkMessage;
            return def;
        }

        private static SettingDefinition Confirmed(SettingDefinition def, string confirm, string confirmWhen, string backupBefore)
        {
            def.Confirm = confirm;
            def.ConfirmWhen = confirmWhen;
            def.BackupBefore = backupBefore;
            return def;
        }

        // ------------------------------------------------------------------ string checks (the patterns, by hand)

        /// <summary>^[A-Z]{2}$</summary>
        internal static bool IsCountry(string s)
        {
            return s.Length == 2 && IsUpper(s[0]) && IsUpper(s[1]);
        }

        /// <summary>^([A-Z0-9]{4,20})?$</summary>
        internal static bool IsRedeemCode(string s)
        {
            if (s.Length == 0) return true;
            if (s.Length < 4 || s.Length > 20) return false;
            foreach (char c in s)
            {
                if (!IsUpper(c) && !IsDigit(c)) return false;
            }
            return true;
        }

        /// <summary>^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$ and a real date and time.</summary>
        internal static bool IsIsoUtc(string s)
        {
            const string shape = "dddd-dd-ddTdd:dd:ddZ";
            if (s.Length != shape.Length) return false;
            for (int i = 0; i < shape.Length; i++)
            {
                if (shape[i] == 'd' ? !IsDigit(s[i]) : s[i] != shape[i]) return false;
            }
            DateTime parsed;
            return DateTime.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                                          DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed);
        }

        /// <summary>^([A-Za-z]+:(all|debug|info|warning|error))?( [A-Za-z]+:(all|debug|info|warning|error))*$</summary>
        internal static bool IsMuteList(string s)
        {
            if (s.Length == 0) return true;
            string[] parts = s.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0 && i == 0) continue; // the first pair is optional: " a:all" matches
                if (!IsMutePair(parts[i])) return false;
            }
            return true;
        }

        private static bool IsMutePair(string pair)
        {
            int colon = pair.IndexOf(':');
            if (colon <= 0) return false;
            for (int i = 0; i < colon; i++)
            {
                char c = pair[i];
                if (!IsUpper(c) && !(c >= 'a' && c <= 'z')) return false;
            }
            string level = pair.Substring(colon + 1);
            return level == "all" || level == "debug" || level == "info" || level == "warning" || level == "error";
        }

        private static bool IsUpper(char c)
        {
            return c >= 'A' && c <= 'Z';
        }

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }
    }
}
