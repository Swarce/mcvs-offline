using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace McvsOffline.Patcher.Pc
{
    /// <summary>
    /// <c>install --port n</c>: the game's server listens on <c>McvsOffline\settings.json</c> "port" (EmbeddedHost reads it at boot;
    /// the settings page never changes it), so the installer sets that key there, and only that key. The file is edited as text:
    /// the value of every top-level "port" is replaced, or a "port" member is inserted right after the opening brace; every other
    /// byte (other keys, their order, formatting, a BOM) stays as it is. A file that is not a JSON object is refused, never
    /// replaced (the game would fall back to its defaults for it too).
    /// </summary>
    public static class PcSettingsPort
    {
        public const string FileName = "settings.json";

        /// <summary><c>McvsOffline/settings.json</c>: the path the report and the errors name.</summary>
        public const string RelativePath = GameFolder.DataDirName + "/" + FileName;

        private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

        /// <summary>
        /// The bytes of settings.json with "port" = <paramref name="port"/>: <paramref name="existing"/> edited as described above,
        /// or a new <c>{ "port": n }</c> when it is null (no file). Throws <see cref="InvalidDataException"/> with a sentence for
        /// the player when <paramref name="existing"/> is not one valid JSON object.
        /// </summary>
        public static byte[] Merge(byte[] existing, int port)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            string number = port.ToString(CultureInfo.InvariantCulture);
            if (existing == null) return Encoding.UTF8.GetBytes("{\n  \"port\": " + number + "\n}\n");

            int start = StartsWith(existing, Bom) ? Bom.Length : 0;
            var json = new ReadOnlySpan<byte>(existing, start, existing.Length - start);
            int open;
            bool empty;
            List<KeyValuePair<int, int>> values;
            try
            {
                values = PortValues(json, out open, out empty);
            }
            catch (JsonException ex)
            {
                throw Malformed("it is not valid JSON (" + ex.Message + ")");
            }

            var output = new List<byte>(existing.Length + 32);
            byte[] digits = Encoding.ASCII.GetBytes(number);
            if (values.Count > 0)
            {
                int from = 0;
                foreach (KeyValuePair<int, int> v in values)
                {
                    AddRange(output, existing, from, start + v.Key - from);
                    output.AddRange(digits);
                    from = start + v.Value;
                }
                AddRange(output, existing, from, existing.Length - from);
            }
            else
            {
                int after = start + open + 1;
                AddRange(output, existing, 0, after);
                output.AddRange(Encoding.UTF8.GetBytes(empty ? "\n  \"port\": " + number + "\n" : "\n  \"port\": " + number + ","));
                AddRange(output, existing, after, existing.Length - after);
            }
            return output.ToArray();
        }

        /// <summary>
        /// The [start, end) offsets of every top-level "port" value in <paramref name="json"/>, in order; <paramref name="open"/>:
        /// the offset of the root object's '{'; <paramref name="empty"/>: the object has no member. Throws JsonException for
        /// anything but exactly one JSON object (strict: no comments, no trailing commas, nothing after it).
        /// </summary>
        private static List<KeyValuePair<int, int>> PortValues(ReadOnlySpan<byte> json, out int open, out bool empty)
        {
            var values = new List<KeyValuePair<int, int>>();
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            if (!reader.Read()) throw Malformed("it is empty");
            if (reader.TokenType != JsonTokenType.StartObject) throw Malformed("it is not a JSON object");
            open = (int)reader.TokenStartIndex;
            empty = true;
            while (true)
            {
                if (!reader.Read()) throw Malformed("it ends inside the object");
                if (reader.TokenType == JsonTokenType.EndObject) break;
                empty = false;
                bool isPort = reader.ValueTextEquals("port");
                reader.Read();
                int valueStart = (int)reader.TokenStartIndex;
                if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray) reader.Skip();
                if (isPort) values.Add(new KeyValuePair<int, int>(valueStart, (int)reader.BytesConsumed));
            }
            if (reader.Read()) throw Malformed("there is more after the object");
            return values;
        }

        private static InvalidDataException Malformed(string why)
        {
            return new InvalidDataException(GameFolder.DataDirName + "\\" + FileName + " cannot take the port: " + why + ". The game cannot " +
                                            "read it either; fix it (or move it away to start from the defaults) and run install --port again");
        }

        private static bool StartsWith(byte[] bytes, byte[] prefix)
        {
            if (bytes.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
            {
                if (bytes[i] != prefix[i]) return false;
            }
            return true;
        }

        private static void AddRange(List<byte> output, byte[] bytes, int offset, int count)
        {
            for (int i = 0; i < count; i++) output.Add(bytes[offset + i]);
        }
    }
}
