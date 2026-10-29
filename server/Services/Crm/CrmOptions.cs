using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>How <c>offline_store</c> is assembled (settings <c>crm.offlineStore</c>).</summary>
    public enum OfflineStoreMode
    {
        /// <summary>Only our generated catalogue (server/Content/offline_store.json). Default.</summary>
        Generated,

        /// <summary>Our catalogue, plus the shipped preconfiguration items whose names we do not generate.</summary>
        Merge,

        /// <summary>The shipped preconfiguration catalogue unchanged (our catalogue when there is none).</summary>
        Preconfig,
    }

    /// <summary>What CRM sends in <c>iap</c> (settings <c>crm.iap</c>).</summary>
    public enum IapMode
    {
        /// <summary><c>{"prices":[]}</c>: no real-money items (they cannot be fulfilled offline). Default.</summary>
        Empty,

        /// <summary>The preconfiguration catalogue for <see cref="CrmOptions.IapLanguage"/>, as the TS fallback does.</summary>
        Preconfig,
    }

    /// <summary>
    /// Immutable snapshot of every setting that shapes the CRM document. Settings live in settings.json: the shared
    /// typed ones (devMenu, instantBots, economy.*) plus our own keys under <c>"crm"</c>, all optional:
    /// <code>
    /// "crm": {
    ///   "skipAgeGate": true, "redeemCoupons": true, "developerIpRange": "",
    ///   "offlineStore": "generated|merge|preconfig", "iap": "empty|preconfig", "iapLanguage": "en",
    ///   "keepAdPointcuts": false, "pointcutActions": [], "eventIds": [], "matchmaking": {},
    ///   "dailyCapMultiplier": 1.0, "usePreconfiguration": true, "dataPackDir": "", "overlay": {},
    ///   "newsfeedLink": true, "omitOwnedSignatures": true, "practiceTile": false
    /// }
    /// </code>
    /// <see cref="Fingerprint"/> changes whenever any of them does, so the cached document is rebuilt.
    /// </summary>
    public sealed class CrmOptions
    {
        public bool DevMenu { get; set; }

        /// <summary>Extra developer ranges ("0.0.0.0/0 ::/0" makes every peer a developer; verify-C5 (a)).</summary>
        public string DeveloperIpRange { get; set; }

        public bool InstantBots { get; set; }

        /// <summary>Extra <c>_ServerConfig.default.Matchmaking</c> keys (e.g. TeamReviewDurationInMilliseconds).</summary>
        public Dictionary<string, object> MatchmakingOverrides { get; set; }

        public bool SkipAgeGate { get; set; }

        public bool RedeemCoupons { get; set; }

        public OfflineStoreMode OfflineStore { get; set; }

        public IapMode Iap { get; set; }

        public string IapLanguage { get; set; }

        public bool KeepAdPointcuts { get; set; }

        /// <summary>Extra CRM pointcut actions appended to the document (validated before use).</summary>
        public List<object> PointcutActions { get; set; }

        /// <summary>Osiris event ids announced in <c>groupware.events.participation</c> (none by default).</summary>
        public List<string> EventIds { get; set; }

        public double PriceMultiplier { get; set; }

        public double CreditsMultiplier { get; set; }

        public double VersusCoinsMultiplier { get; set; }

        public double XpMultiplier { get; set; }

        /// <summary>Extra factor on the daily currency caps (on top of the currency multiplier).</summary>
        public double DailyCapMultiplier { get; set; }

        public bool UsePreconfiguration { get; set; }

        /// <summary>Explicit Data folder for the preconfiguration when the host configured no data pack.</summary>
        public string DataPackDir { get; set; }

        /// <summary>Deep-merged into the document last: an escape hatch for testers.</summary>
        public Dictionary<string, object> Overlay { get; set; }

        /// <summary>
        /// The "MCVS Offline" inbox item that opens the settings page (<c>crm.newsfeedLink</c>; <see cref="CrmNewsfeed"/>,
        /// design docs/design/settings-page.md §9.1). Off: <c>"newsfeeds": []</c>, which removes the message.
        /// </summary>
        public bool NewsfeedLink { get; set; }

        /// <summary>
        /// Leave the kill signatures a player owns out of the <c>offline_store</c> of their document
        /// (<c>crm.omitOwnedSignatures</c>, <see cref="CrmOwnedStoreItems"/>). Independent of the collector, which gets
        /// per-player documents of its own (economy-collector.md §5.6).
        /// </summary>
        public bool OmitOwnedSignatures { get; set; }

        /// <summary>
        /// Show the game's hidden PRACTICE bot-match tile in the main menu (<c>crm.practiceTile</c>, off by default):
        /// two SET cells on MCVS_Meta <c>MainMenuElements</c> row 13 (<see cref="CrmDocumentBuilder.ApplyPracticeTile"/>).
        /// Its placement and art in the v9 menu are untested.
        /// </summary>
        public bool PracticeTile { get; set; }

        public CrmOptions()
        {
            DeveloperIpRange = "";
            InstantBots = true;
            SkipAgeGate = true;
            RedeemCoupons = true;
            OfflineStore = OfflineStoreMode.Generated;
            Iap = IapMode.Empty;
            IapLanguage = "en";
            PointcutActions = new List<object>();
            EventIds = new List<string>();
            PriceMultiplier = 1.0;
            CreditsMultiplier = 1.0;
            VersusCoinsMultiplier = 1.0;
            XpMultiplier = 1.0;
            DailyCapMultiplier = 1.0;
            UsePreconfiguration = true;
            DataPackDir = "";
            NewsfeedLink = true;
            OmitOwnedSignatures = true;
        }

        public static CrmOptions FromSettings(Settings settings, Log log)
        {
            log = log ?? Log.Null;
            var o = new CrmOptions();
            o.DevMenu = settings.DevMenu;
            o.InstantBots = settings.InstantBots;
            o.DeveloperIpRange = settings.GetString("crm.developerIpRange", "").Trim();
            o.MatchmakingOverrides = settings.GetObject("crm.matchmaking");
            o.SkipAgeGate = settings.GetBool("crm.skipAgeGate", true);
            o.RedeemCoupons = settings.GetBool("crm.redeemCoupons", true);
            o.OfflineStore = ParseStoreMode(settings.GetString("crm.offlineStore", "generated"), log);
            o.Iap = ParseIapMode(settings.GetString("crm.iap", "empty"), log);
            o.IapLanguage = settings.GetString("crm.iapLanguage", "en");
            o.KeepAdPointcuts = settings.GetBool("crm.keepAdPointcuts", false);
            o.PointcutActions = JsonValue.AsArray(settings.Get("crm.pointcutActions")) ?? new List<object>();
            o.EventIds = new List<string>();
            foreach (object id in JsonValue.AsArray(settings.Get("crm.eventIds")) ?? new List<object>())
            {
                string s = JsonValue.AsString(id);
                if (!string.IsNullOrEmpty(s)) o.EventIds.Add(s);
            }
            o.PriceMultiplier = Multiplier(settings.PriceMultiplier, "economy.priceMultiplier", log);
            o.CreditsMultiplier = Multiplier(settings.CreditsMultiplier, "economy.creditsMultiplier", log);
            o.VersusCoinsMultiplier = Multiplier(settings.VersusCoinsMultiplier, "economy.versusCoinsMultiplier", log);
            o.XpMultiplier = Multiplier(settings.XpMultiplier, "economy.xpMultiplier", log);
            o.DailyCapMultiplier = Multiplier(settings.GetDouble("crm.dailyCapMultiplier", 1.0), "crm.dailyCapMultiplier", log);
            o.UsePreconfiguration = settings.GetBool("crm.usePreconfiguration", true);
            o.DataPackDir = settings.GetString("crm.dataPackDir", "");
            o.Overlay = settings.GetObject("crm.overlay");
            o.NewsfeedLink = settings.GetBool("crm.newsfeedLink", true);
            o.OmitOwnedSignatures = settings.GetBool("crm.omitOwnedSignatures", true);
            o.PracticeTile = settings.GetBool("crm.practiceTile", false);
            return o;
        }

        /// <summary>A string that changes whenever the generated document would.</summary>
        public string Fingerprint
        {
            get
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                d["dev"] = DevMenu;
                d["ip"] = DeveloperIpRange;
                d["bots"] = InstantBots;
                d["mm"] = MatchmakingOverrides;
                d["age"] = SkipAgeGate;
                d["coupons"] = RedeemCoupons;
                d["store"] = OfflineStore.ToString();
                d["iap"] = Iap.ToString();
                d["iapLang"] = IapLanguage;
                d["ads"] = KeepAdPointcuts;
                d["pc"] = PointcutActions;
                var events = new List<object>();
                foreach (string e in EventIds) events.Add(e);
                d["events"] = events;
                d["price"] = PriceMultiplier;
                d["credits"] = CreditsMultiplier;
                d["vc"] = VersusCoinsMultiplier;
                d["xp"] = XpMultiplier;
                d["cap"] = DailyCapMultiplier;
                d["pre"] = UsePreconfiguration;
                d["packDir"] = DataPackDir;
                d["overlay"] = Overlay;
                d["news"] = NewsfeedLink;
                d["omitOwned"] = OmitOwnedSignatures;
                d["practice"] = PracticeTile;
                return MiniJson.Serialize(d);
            }
        }

        private static double Multiplier(double value, string name, Log log)
        {
            if (double.IsNaN(value) || value <= 0 || value > 1000)
            {
                log.Warn("settings: " + name + " = " + value.ToString(CultureInfo.InvariantCulture) + " is not a positive multiplier (max 1000); using 1");
                return 1.0;
            }
            return value;
        }

        private static OfflineStoreMode ParseStoreMode(string value, Log log)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "":
                case "generated": return OfflineStoreMode.Generated;
                case "merge": return OfflineStoreMode.Merge;
                case "preconfig":
                case "preconfiguration": return OfflineStoreMode.Preconfig;
                default:
                    log.Warn("settings: crm.offlineStore '" + value + "' is not generated|merge|preconfig; using generated");
                    return OfflineStoreMode.Generated;
            }
        }

        private static IapMode ParseIapMode(string value, Log log)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "":
                case "empty": return IapMode.Empty;
                case "preconfig":
                case "preconfiguration": return IapMode.Preconfig;
                default:
                    log.Warn("settings: crm.iap '" + value + "' is not empty|preconfig; using empty");
                    return IapMode.Empty;
            }
        }
    }
}
