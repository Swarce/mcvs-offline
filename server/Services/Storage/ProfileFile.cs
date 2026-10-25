using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>One account's Seshat "myprofile" document plus the metadata our server keeps about it.</summary>
    public sealed class ProfileDocument
    {
        public ProfileDocument(string accountId)
        {
            AccountId = accountId;
            Root = ProfileJson.NewObject();
            Visibility = ProfileJson.NewObject();
        }

        public string AccountId { get; set; }

        /// <summary>Incremented on every saved change. Internal bookkeeping only: never sent as an ETag (verify-C3 §3.5).</summary>
        public long Revision { get; set; }

        /// <summary>ISO 8601 time of the first saved change (served as the read-only "created" field).</summary>
        public string Created { get; set; }

        /// <summary>ISO 8601 time of the last saved change (served as "modified" and "last_session").</summary>
        public string Modified { get; set; }

        /// <summary>Last visibility given per selector ("*" for the root form <c>{"*":"restricted"}</c>). Stored, never enforced.</summary>
        public Dictionary<string, object> Visibility { get; set; }

        /// <summary>The profile tree (MiniJson values with <see cref="VerbatimJson"/> nodes).</summary>
        public Dictionary<string, object> Root { get; set; }

        /// <summary>True when the document came from a stored file (false for an account that never saved anything).</summary>
        public bool IsStored { get; set; }
    }

    /// <summary>Raised when a stored profile file cannot be read. Never treated as "missing" (verify-C3 §3.1).</summary>
    public sealed class ProfileFileException : FormatException
    {
        public ProfileFileException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// The on-disk format of a profile: one UTF-8 JSON file per account, readable and editable by hand.
    /// <code>
    /// {
    ///   "format": "mcvs-offline/seshat-profile", "version": 1,
    ///   "account": "&lt;id&gt;", "revision": 12, "created": "...", "modified": "...",
    ///   "visibility": { "_mcvs.gameProfile": "restricted", ... },
    ///   "verbatim": [ { "path": ["_mcvs", "gameProfile"], "kind": "json" }, ... ],
    ///   "profile": { ...the Seshat document; verbatim values written inline exactly as received... }
    /// }
    /// </code>
    /// "verbatim" lists the values stored byte-for-byte ("json": inline JSON whose original text is recovered with
    /// <see cref="JsonSpanScanner"/>; "text": non-JSON text stored as a JSON string). "profile" is last so the
    /// metadata is easy to see. A file without our "format" marker is imported as a plain profile document.
    /// </summary>
    public static class ProfileFile
    {
        public const string Format = "mcvs-offline/seshat-profile";
        public const long CurrentVersion = 1;

        public static string Serialize(ProfileDocument doc)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            var verbatimList = new List<object>();
            foreach (var entry in ProfileJson.CollectVerbatim(doc.Root))
            {
                var item = ProfileJson.NewObject();
                var path = new List<object>(entry.Key.Length);
                foreach (string segment in entry.Key) path.Add(segment);
                item["path"] = path;
                item["kind"] = entry.Value.IsJson ? "json" : "text";
                verbatimList.Add(item);
            }

            var envelope = ProfileJson.NewObject();
            envelope["format"] = Format;
            envelope["version"] = CurrentVersion;
            envelope["account"] = doc.AccountId;
            envelope["revision"] = doc.Revision;
            envelope["created"] = doc.Created;
            envelope["modified"] = doc.Modified;
            envelope["visibility"] = doc.Visibility ?? ProfileJson.NewObject();
            envelope["verbatim"] = verbatimList;
            envelope["profile"] = doc.Root;
            var sb = new StringBuilder(4096);
            ProfileJson.Write(sb, envelope, true, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// Parses a stored file. <paramref name="warnings"/> receives non-fatal problems (e.g. a verbatim value whose
        /// original text could not be recovered and is served re-serialized). Throws <see cref="ProfileFileException"/>
        /// when the file is not a JSON object.
        /// </summary>
        public static ProfileDocument Parse(string text, string accountId, List<string> warnings)
        {
            if (text == null) throw new ArgumentNullException("text");
            object parsed;
            try
            {
                parsed = MiniJson.Parse(text);
            }
            catch (JsonParseException ex)
            {
                throw new ProfileFileException("profile file is not valid JSON: " + ex.Message, ex);
            }
            var top = parsed as Dictionary<string, object>;
            if (top == null) throw new ProfileFileException("profile file is not a JSON object");

            var doc = new ProfileDocument(accountId);
            doc.IsStored = true;
            if (!string.Equals(JsonValue.GetString(top, "format"), Format, StringComparison.Ordinal))
            {
                // A plain Seshat document (e.g. an exported profile dropped in by hand): take it as the profile.
                doc.Root = top;
                if (warnings != null) warnings.Add("file has no '" + Format + "' marker; imported as a plain profile document");
                return doc;
            }

            long version = JsonValue.GetLong(top, "version", CurrentVersion);
            if (version > CurrentVersion && warnings != null)
                warnings.Add("file format version " + version.ToString(CultureInfo.InvariantCulture) + " is newer than " + CurrentVersion.ToString(CultureInfo.InvariantCulture) + "; reading it anyway");

            string storedAccount = JsonValue.GetString(top, "account");
            if (!string.IsNullOrEmpty(storedAccount) && accountId != null && storedAccount != accountId && warnings != null)
                warnings.Add("file belongs to account '" + storedAccount + "', read as '" + accountId + "'");

            doc.Revision = JsonValue.GetLong(top, "revision", 0);
            doc.Created = JsonValue.GetString(top, "created");
            doc.Modified = JsonValue.GetString(top, "modified");
            doc.Visibility = JsonValue.GetObject(top, "visibility") ?? ProfileJson.NewObject();
            object profileNode;
            if (top.TryGetValue("profile", out profileNode) && profileNode != null)
            {
                doc.Root = profileNode as Dictionary<string, object>;
                if (doc.Root == null) throw new ProfileFileException("\"profile\" is not a JSON object");
            }

            var verbatimList = JsonValue.GetArray(top, "verbatim");
            if (verbatimList != null)
            {
                foreach (var item in verbatimList) RestoreVerbatim(text, doc.Root, item as Dictionary<string, object>, warnings);
            }
            return doc;
        }

        private static void RestoreVerbatim(string text, Dictionary<string, object> root, Dictionary<string, object> item, List<string> warnings)
        {
            if (item == null) return;
            var pathList = JsonValue.GetArray(item, "path");
            if (pathList == null || pathList.Count == 0) return;
            var segments = new string[pathList.Count];
            for (int i = 0; i < segments.Length; i++)
            {
                segments[i] = pathList[i] as string;
                if (segments[i] == null) return;
            }
            string display = ProfileJson.FormatPath(segments);
            object node;
            if (!ProfileJson.TryGet(root, segments, out node))
            {
                if (warnings != null) warnings.Add("verbatim value '" + display + "' is listed but missing from the profile");
                return;
            }

            if (string.Equals(JsonValue.GetString(item, "kind"), "text", StringComparison.Ordinal))
            {
                string opaque = node as string;
                if (opaque != null) SetNode(root, segments, VerbatimJson.FromOpaqueText(opaque));
                else if (warnings != null) warnings.Add("opaque text '" + display + "' is not a string any more; kept as parsed JSON");
                return;
            }

            var filePath = new List<string>(segments.Length + 1);
            filePath.Add("profile");
            filePath.AddRange(segments);
            int start, length;
            if (JsonSpanScanner.TryFindValue(text, filePath, out start, out length))
            {
                string span = text.Substring(start, length);
                object spanValue;
                // The span must parse to exactly the value MiniJson read for that path; anything else would mean the
                // scanner and the parser disagree, and the parsed value wins.
                if (MiniJson.TryParse(span, out spanValue) && ProfileJson.DeepEquals(spanValue, node))
                {
                    SetNode(root, segments, VerbatimJson.FromValidJson(span, node));
                    return;
                }
            }
            if (warnings != null) warnings.Add("could not recover the exact text of '" + display + "'; it will be served re-serialized");
        }

        private static void SetNode(Dictionary<string, object> root, string[] segments, object value)
        {
            // Only object paths are recorded, so every intermediate is a plain object here.
            Dictionary<string, object> current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                object next;
                if (!current.TryGetValue(segments[i], out next)) return;
                current = next as Dictionary<string, object>;
                if (current == null) return;
            }
            current[segments[segments.Length - 1]] = value;
        }
    }
}
