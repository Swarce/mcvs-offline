using System;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>
    /// A stored value kept exactly as the game sent it. Every value written with a selector set
    /// (<c>POST profiles/me/myprofile/&lt;selector&gt; object=...</c>) is stored like this, most importantly the
    /// Templates dump in <c>_mcvs.gameProfile</c>: verify-C3 §7.6 asks for it byte-for-byte (its inner keys contain
    /// '.', '/', '@', '$' and collapsed dotted names; re-serializing would only invite number/format drift).
    /// <para>
    /// Two kinds:
    /// <list type="bullet">
    /// <item><b>JSON</b> (the normal case): <see cref="Text"/> is the JSON text with surrounding whitespace/BOM removed.
    /// It is embedded unchanged when it appears inside a larger response or the stored file.</item>
    /// <item><b>Opaque text</b>: an <c>object=</c> value that is not valid JSON. A selector GET returns it unchanged;
    /// inside a larger JSON document it appears as a JSON string.</item>
    /// </list>
    /// Immutable: the tree helpers never modify <see cref="Value"/>; writing inside a verbatim value first replaces it
    /// by a mutable deep copy ("materializes" it).
    /// </para>
    /// </summary>
    public sealed class VerbatimJson
    {
        private readonly object gate = new object();
        private object value;
        private bool parsed;

        private VerbatimJson(string text, bool isJson, object value, bool parsed)
        {
            Text = text;
            IsJson = isJson;
            this.value = value;
            this.parsed = parsed;
        }

        /// <summary>The stored text (trimmed JSON, or the opaque text as received).</summary>
        public string Text { get; private set; }

        /// <summary>False for opaque (non-JSON) text.</summary>
        public bool IsJson { get; private set; }

        /// <summary>
        /// The parsed value (MiniJson model) of a JSON text, or the text itself for opaque text. Shared and cached:
        /// treat it as read-only.
        /// </summary>
        public object Value
        {
            get
            {
                if (!IsJson) return Text;
                lock (gate)
                {
                    if (!parsed)
                    {
                        value = MiniJson.Parse(Text);
                        parsed = true;
                    }
                    return value;
                }
            }
        }

        /// <summary>True when the value is a JSON object (paths can go inside it).</summary>
        public bool IsObject
        {
            get { return IsJson && Value is System.Collections.Generic.Dictionary<string, object>; }
        }

        /// <summary>
        /// Wraps an <c>object=</c> form value: valid JSON becomes a JSON verbatim value (trimmed), anything else
        /// opaque text. Returns null for null.
        /// </summary>
        public static VerbatimJson FromRequestText(string text)
        {
            if (text == null) return null;
            string trimmed = TrimJsonWhitespace(text);
            object parsedValue;
            if (trimmed.Length > 0 && MiniJson.TryParse(trimmed, out parsedValue))
                return new VerbatimJson(trimmed, true, parsedValue, true);
            return new VerbatimJson(text, false, null, true);
        }

        /// <summary>A JSON verbatim value from text already known to be valid JSON (stored files).</summary>
        public static VerbatimJson FromValidJson(string json)
        {
            if (json == null) throw new ArgumentNullException("json");
            return new VerbatimJson(TrimJsonWhitespace(json), true, null, false);
        }

        /// <summary>
        /// Like <see cref="FromValidJson(string)"/> with the value already parsed from that text (it becomes the
        /// cached <see cref="Value"/> and must not be modified afterwards).
        /// </summary>
        public static VerbatimJson FromValidJson(string json, object parsedValue)
        {
            if (json == null) throw new ArgumentNullException("json");
            return new VerbatimJson(TrimJsonWhitespace(json), true, parsedValue, true);
        }

        /// <summary>An opaque-text verbatim value.</summary>
        public static VerbatimJson FromOpaqueText(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            return new VerbatimJson(text, false, null, true);
        }

        /// <summary>Removes a leading BOM and JSON whitespace (space, tab, CR, LF) on both ends.</summary>
        public static string TrimJsonWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            int start = 0;
            int end = text.Length;
            if (text[0] == '﻿') start = 1;
            while (start < end && IsJsonWhitespace(text[start])) start++;
            while (end > start && IsJsonWhitespace(text[end - 1])) end--;
            return start == 0 && end == text.Length ? text : text.Substring(start, end - start);
        }

        private static bool IsJsonWhitespace(char c)
        {
            return c == ' ' || c == '\t' || c == '\r' || c == '\n';
        }

        public override string ToString()
        {
            return Text;
        }
    }
}
