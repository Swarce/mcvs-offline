// Module: Profanity - owner group: MISC
// Profanity filter (groupchat service): on the critical path for setting the player name.
// Protocol: report 02 §4.2; docs/protocol/misc.md
//   POST chat/words/{lang}/filter, chat/words/filter   message=...  -> {"matches":false,"result":<message>}
//   GET  chat/words;  POST chat/words/{lang}/{word} (bad=True/False);  POST chat/words/{lang}/{word}/delete
// Conventions and core APIs: docs/MODULES.md.

using System;
using System.Collections.Generic;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Misc;

namespace McvsOffline.Server.Services.Profanity
{
    /// <summary>
    /// The profanity filter. The TS filters the player name, clan names and private room names through it and
    /// aborts the operation when the request fails (<c>GM/PlayerProfileComponent.cs:3673-3690</c>,
    /// <c>EN/ClanComponent.cs:256-270</c>), so it always answers 200 with {"matches", "result"}
    /// (<c>OP/FilteredMessage.cs</c>).
    /// <para>
    /// By default nothing is censored. Words added through the admin endpoints, or listed in settings
    /// <c>profanity.blacklist</c>, are masked with '*' (whole words, case-insensitive); <c>whitelisted</c> words win.
    /// </para>
    /// </summary>
    public static class ProfanityService
    {
        public const string StoreName = "profanity";
        private const string WordsKey = "words";
        private const string AllLanguages = "all";

        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("profanity");
            DocumentStore store = context.OpenStore(StoreName);

            // "filter" is a literal: registered before the {word} route of the same shape.
            router.Post("chat/words/filter", "profanity.filter", r => Filter(context, store, log, r, null));
            router.Post("chat/words/{lang}/filter", "profanity.filter.lang", r => Filter(context, store, log, r, r.RouteValue("lang")));
            router.Get("chat/words", "profanity.words", r => HttpResponse.Json(Words(context, store)));
            router.Post("chat/words/{lang}/{word}/delete", "profanity.word.delete", r => SetWord(store, log, r, null));
            router.Post("chat/words/{lang}/{word}", "profanity.word.set", r => SetWord(store, log, r, Fed.BoolParam(r, "bad", true)));
        }

        private static HttpResponse Filter(ServerContext context, DocumentStore store, Log log, HttpRequest request, string language)
        {
            string message = request.Param("message") ?? "";
            Dictionary<string, object> words = Words(context, store);
            HashSet<string> black = WordSet(words, language, "blacklisted");
            HashSet<string> white = WordSet(words, language, "whitelisted");
            bool matches;
            string result = Censor(message, black, white, out matches);
            if (matches) log.Info("filtered '" + message + "' -> '" + result + "'");

            var body = Fed.Obj();
            body["matches"] = matches;
            body["result"] = result;
            return HttpResponse.Json(body);
        }

        private static HttpResponse SetWord(DocumentStore store, Log log, HttpRequest request, bool? bad)
        {
            string language = (request.RouteValue("lang") ?? AllLanguages).ToLowerInvariant();
            string word = (request.RouteValue("word") ?? "").Trim().ToLowerInvariant();
            if (word.Length == 0) return Fed.BadRequest("word required");
            store.Update(WordsKey, doc =>
            {
                Dictionary<string, object> lang = JsonValue.GetObject(doc, language);
                if (lang == null)
                {
                    lang = Fed.Obj();
                    doc[language] = lang;
                }
                List<object> blacklisted = List(lang, "blacklisted");
                List<object> whitelisted = List(lang, "whitelisted");
                blacklisted.Remove(word);
                whitelisted.Remove(word);
                if (bad == true) blacklisted.Add(word);
                else if (bad == false) whitelisted.Add(word);
            });
            log.Info("profanity word '" + word + "' (" + language + ") " + (bad == null ? "removed" : bad.Value ? "blacklisted" : "whitelisted"));
            return HttpResponse.EmptyObject();
        }

        /// <summary>{"&lt;lang&gt;": {"blacklisted": [...], "whitelisted": [...]}, "all": {...}} (<c>OP/Profanity.cs</c>).</summary>
        private static Dictionary<string, object> Words(ServerContext context, DocumentStore store)
        {
            Dictionary<string, object> doc = store.Read(WordsKey) ?? Fed.Obj();
            Dictionary<string, object> all = JsonValue.GetObject(doc, AllLanguages);
            if (all == null)
            {
                all = Fed.Obj();
                doc[AllLanguages] = all;
            }
            List<object> blacklisted = List(all, "blacklisted");
            List(all, "whitelisted");
            List<object> fromSettings = context.Settings.Get("profanity.blacklist") as List<object>;
            if (fromSettings != null)
            {
                foreach (object w in fromSettings)
                {
                    string word = (JsonValue.AsString(w) ?? "").Trim().ToLowerInvariant();
                    if (word.Length > 0 && !blacklisted.Contains(word)) blacklisted.Add(word);
                }
            }
            foreach (var kv in doc)
            {
                var lang = kv.Value as Dictionary<string, object>;
                if (lang == null) continue;
                List(lang, "blacklisted");
                List(lang, "whitelisted");
            }
            return doc;
        }

        private static HashSet<string> WordSet(Dictionary<string, object> words, string language, string list)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string lang in new[] { AllLanguages, string.IsNullOrEmpty(language) ? null : language.ToLowerInvariant() })
            {
                if (lang == null) continue;
                List<object> items = JsonValue.GetArray(JsonValue.GetObject(words, lang), list);
                if (items == null) continue;
                foreach (object item in items)
                {
                    string w = JsonValue.AsString(item);
                    if (!string.IsNullOrEmpty(w)) set.Add(w.ToLowerInvariant());
                }
            }
            return set;
        }

        /// <summary>Masks blacklisted whole words (letters/digits runs) with '*'.</summary>
        public static string Censor(string message, ICollection<string> black, ICollection<string> white, out bool matches)
        {
            matches = false;
            if (string.IsNullOrEmpty(message) || black == null || black.Count == 0) return message ?? "";
            var sb = new StringBuilder(message.Length);
            int i = 0;
            while (i < message.Length)
            {
                if (!char.IsLetterOrDigit(message[i]))
                {
                    sb.Append(message[i++]);
                    continue;
                }
                int start = i;
                while (i < message.Length && char.IsLetterOrDigit(message[i])) i++;
                string word = message.Substring(start, i - start);
                string lower = word.ToLowerInvariant();
                if (black.Contains(lower) && (white == null || !white.Contains(lower)))
                {
                    matches = true;
                    sb.Append('*', word.Length);
                }
                else
                {
                    sb.Append(word);
                }
            }
            return sb.ToString();
        }

        private static List<object> List(Dictionary<string, object> lang, string name)
        {
            List<object> list = JsonValue.GetArray(lang, name);
            if (list == null)
            {
                list = new List<object>();
                lang[name] = list;
            }
            return list;
        }
    }
}
