using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>
    /// Our generated CRM content (server/Content, written by tools/content-gen; a file of the same name in
    /// &lt;data&gt;/content/ overrides the built-in one): the VersusCoins catalogue, the static overlay and the economy
    /// multiplier targets. A missing or broken file degrades to an empty section and is logged.
    /// </summary>
    public sealed class CrmContent
    {
        public const string OfflineStoreFile = "offline_store.json";
        public const string OverlayFile = "crm_overlay.json";
        public const string EconomyFile = "economy.json";

        public CrmContent()
        {
            OfflineStore = NewObject();
            OfflineStore["prices"] = new List<object>();
            Overlay = NewObject();
            Economy = NewObject();
        }

        /// <summary><c>offline_store</c> as generated: <c>{"prices":[...]}</c>.</summary>
        public Dictionary<string, object> OfflineStore { get; set; }

        /// <summary>Deep-merged over the preconfiguration (see crm_overlay.json).</summary>
        public Dictionary<string, object> Overlay { get; set; }

        /// <summary><c>{"multipliers":{"credits":[target...],"versusCoins":[...],"xp":[...]}}</c>.</summary>
        public Dictionary<string, object> Economy { get; set; }

        public static CrmContent Load(IContentProvider content, Log log)
        {
            log = log ?? Log.Null;
            var c = new CrmContent();
            var store = ReadObject(content, OfflineStoreFile, log);
            if (store != null && store.ContainsKey("prices") && store["prices"] is List<object>) c.OfflineStore = store;
            else if (store != null) log.Error(OfflineStoreFile + " has no \"prices\" array; the generated catalogue is empty");
            c.Overlay = ReadObject(content, OverlayFile, log) ?? NewObject();
            c.Economy = ReadObject(content, EconomyFile, log) ?? NewObject();
            return c;
        }

        private static Dictionary<string, object> ReadObject(IContentProvider content, string name, Log log)
        {
            string text;
            if (content == null || !content.TryReadContentText(name, out text))
            {
                log.Warn("content file " + name + " is missing (run tools/content-gen)");
                return null;
            }
            object parsed;
            if (!MiniJson.TryParse(text, out parsed) || !(parsed is Dictionary<string, object>))
            {
                log.Error("content file " + name + " is not a JSON object; ignoring it");
                return null;
            }
            return (Dictionary<string, object>)parsed;
        }

        private static Dictionary<string, object> NewObject()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }
    }
}
