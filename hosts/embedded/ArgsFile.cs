// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Text;

namespace McvsOffline.Embedded
{
    /// <summary>One "-Key value" argument written into the managed block of args.txt.</summary>
    public sealed class ArgEntry
    {
        public ArgEntry(string key, string value)
        {
            Key = key;
            Value = value;
        }

        /// <summary>Argument name without the leading '-', e.g. "ServerLocation".</summary>
        public string Key { get; private set; }

        public string Value { get; private set; }

        public override string ToString()
        {
            return "-" + Key + " " + Value;
        }
    }

    /// <summary>What <see cref="ArgsFile.Merge"/> produced.</summary>
    public sealed class ArgsMergeResult
    {
        public ArgsMergeResult()
        {
            Warnings = new List<string>();
            OverriddenKeys = new List<string>();
            Written = new List<ArgEntry>();
        }

        /// <summary>The new file content (ASCII block, user bytes preserved).</summary>
        public byte[] Content { get; internal set; }

        /// <summary>False when <see cref="Content"/> equals the existing file byte for byte (nothing to write).</summary>
        public bool Changed { get; internal set; }

        /// <summary>Managed keys left out because the player set them outside the block (their line wins).</summary>
        public List<string> OverriddenKeys { get; private set; }

        /// <summary>The managed entries actually written into the block.</summary>
        public List<ArgEntry> Written { get; private set; }

        /// <summary>Problems found in the existing file (BOM removed, tabs, a broken block...), for the log.</summary>
        public List<string> Warnings { get; private set; }
    }

    /// <summary>
    /// Reads and rewrites <c>&lt;GlfRootPath&gt;/args.txt</c>, the one file the native engine parses into the game's
    /// launch arguments (verify-C1 §1). The file format is dictated by the native tokenizer:
    /// <list type="bullet">
    /// <item>Tokens are separated by space, CR and LF only. A tab is part of a token, so we never write one.</item>
    /// <item>There is no comment syntax and no quoting. A token starting with '-' names an argument; every other token
    /// is appended (with a space) to the value of the pending argument.</item>
    /// <item>A UTF-8 BOM becomes part of the first token, which silently drops the first argument, so we write
    /// none and strip an existing one.</item>
    /// <item>The first occurrence of a key wins inside the file (emplace-if-absent, case-sensitive), but the C# side
    /// matches field names case-insensitively, and enum values (<c>Embedded</c>, <c>QuickLaunch</c>) are
    /// case-sensitive.</item>
    /// </list>
    /// Our arguments live in a block delimited by two marker arguments the game ignores (they match no
    /// <c>GameApplication.Arguments</c> field). Everything outside the block is the player's and is preserved
    /// verbatim. A managed key the player also sets outside the block (compared case-insensitively, as the C# parser
    /// does) is left out of the block, so the player's value is the only one and wins whatever its position.
    /// The merge is idempotent: merging its own output again yields the same bytes.
    /// </summary>
    public static class ArgsFile
    {
        public const string FileName = "args.txt";

        /// <summary>Marker argument opening the managed block (an unknown key: native keeps it, C# skips it).</summary>
        public const string BeginMarker = "-McvsOfflineBegin";

        /// <summary>Marker argument closing the managed block.</summary>
        public const string EndMarker = "-McvsOfflineEnd";

        /// <summary>
        /// Written as the value of the begin marker, i.e. as words of an ignored argument: the file has no comments.
        /// None of these words may start with '-' (that would start a new argument).
        /// </summary>
        public const string BlockHint =
            "lines up to McvsOfflineEnd are rewritten by MCVS Offline at every start; put your own arguments outside this block (they replace the managed ones)";

        /// <summary>Every key the managed block may contain (used to recover a block whose end marker was deleted).</summary>
        public static readonly string[] KnownManagedKeys =
        {
            "ServerLocation", "AnubisCommand", "EnvironmentSwitcherUrl", "SuperCredential", "SuperPassword", "DeviceId",
            "DataCenter", "LocalAnubisHost",
        };

        /// <summary>
        /// Merges <paramref name="managed"/> into <paramref name="existing"/> (the current file bytes, or null when the
        /// file does not exist). Never throws for any input bytes; throws <see cref="ArgumentException"/> for an invalid
        /// managed entry (a programming error, see <see cref="ValidateEntry"/>).
        /// </summary>
        public static ArgsMergeResult Merge(byte[] existing, IList<ArgEntry> managed)
        {
            if (managed == null) throw new ArgumentNullException("managed");
            foreach (var entry in managed)
            {
                string error = ValidateEntry(entry);
                if (error != null) throw new ArgumentException(error, "managed");
            }

            var result = new ArgsMergeResult();
            string text = existing == null ? "" : Latin1Decode(existing);
            if (text.StartsWith("ï»¿", StringComparison.Ordinal))
            {
                text = text.Substring(3);
                result.Warnings.Add("removed a UTF-8 byte order mark (the game would have ignored the first argument)");
            }

            List<string> lines = SplitLines(text);
            var userLines = new List<string>();
            int blockPosition = -1;   // index into userLines where the (first) managed block was
            int blocks = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                string first = FirstToken(lines[i]);
                if (!string.Equals(first, BeginMarker, StringComparison.Ordinal))
                {
                    if (string.Equals(first, EndMarker, StringComparison.Ordinal))
                    {
                        result.Warnings.Add("dropped a stray " + EndMarker + " line");
                        continue;
                    }
                    userLines.Add(lines[i]);
                    continue;
                }

                blocks++;
                if (blockPosition < 0) blockPosition = userLines.Count;
                int end = -1;
                for (int j = i + 1; j < lines.Count; j++)
                {
                    string token = FirstToken(lines[j]);
                    if (string.Equals(token, EndMarker, StringComparison.Ordinal))
                    {
                        end = j;
                        break;
                    }
                    if (string.Equals(token, BeginMarker, StringComparison.Ordinal)) break;
                }
                if (end >= 0)
                {
                    i = end;
                    continue;
                }

                // Begin without end (hand-edited or truncated file): take back only the consecutive lines that are
                // ours, never a line that could be the player's.
                result.Warnings.Add(BeginMarker + " without " + EndMarker + "; recovered the managed lines that follow it");
                int k = i + 1;
                while (k < lines.Count && IsKnownManagedLine(lines[k])) k++;
                i = k - 1;
            }
            if (blocks > 1) result.Warnings.Add("merged " + blocks + " managed blocks into one");

            // Keys the player sets outside the block, compared like ApplicationArguments.Parse does (IgnoreCase).
            var userKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < userLines.Count; i++)
            {
                if (userLines[i].IndexOf('\t') >= 0)
                {
                    result.Warnings.Add("line " + (i + 1) + " of the player's lines contains a tab; the game does not treat tabs as separators");
                }
                foreach (string token in Tokenize(userLines[i]))
                {
                    if (token.Length > 1 && token[0] == '-') userKeys.Add(token.Substring(1));
                }
            }

            string newline = DetectNewline(userLines);
            var block = new StringBuilder();
            block.Append(BeginMarker).Append(' ').Append(BlockHint).Append(newline);
            foreach (var entry in managed)
            {
                if (userKeys.Contains(entry.Key))
                {
                    result.OverriddenKeys.Add(entry.Key);
                    continue;
                }
                result.Written.Add(entry);
                block.Append('-').Append(entry.Key).Append(' ').Append(entry.Value).Append(newline);
            }
            block.Append(EndMarker).Append(newline);

            var output = new StringBuilder(text.Length + block.Length + 4);
            if (blockPosition < 0) blockPosition = userLines.Count;
            for (int i = 0; i < userLines.Count; i++)
            {
                if (i == blockPosition) output.Append(block);
                output.Append(userLines[i]);
            }
            if (blockPosition == userLines.Count)
            {
                // Appending: the player's last line must end before our first token.
                if (output.Length > 0 && output[output.Length - 1] != '\n') output.Append(newline);
                output.Append(block);
            }

            result.Content = Latin1Encode(output.ToString());
            result.Changed = existing == null || !BytesEqual(existing, result.Content);
            return result;
        }

        /// <summary>
        /// The value of <paramref name="key"/> inside the managed block of <paramref name="existing"/>, or null.
        /// Used to keep the device id stable if its persisted copy is lost.
        /// </summary>
        public static string ReadManagedValue(byte[] existing, string key)
        {
            if (existing == null || string.IsNullOrEmpty(key)) return null;
            string text = Latin1Decode(existing);
            bool inside = false;
            foreach (string line in SplitLines(text))
            {
                string first = FirstToken(line);
                if (string.Equals(first, BeginMarker, StringComparison.Ordinal))
                {
                    inside = true;
                    continue;
                }
                if (string.Equals(first, EndMarker, StringComparison.Ordinal))
                {
                    inside = false;
                    continue;
                }
                if (!inside) continue;
                List<string> tokens = Tokenize(line);
                if (tokens.Count >= 2 && string.Equals(tokens[0], "-" + key, StringComparison.Ordinal)) return tokens[1];
            }
            return null;
        }

        /// <summary>Returns null when the entry can be written as one "-Key value" line, else the reason.</summary>
        public static string ValidateEntry(ArgEntry entry)
        {
            if (entry == null) return "null entry";
            if (string.IsNullOrEmpty(entry.Key)) return "empty key";
            foreach (char c in entry.Key)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
                if (!ok) return "key '" + entry.Key + "' must be letters, digits or '_'";
            }
            string valueError = ValidateValue(entry.Value);
            return valueError == null ? null : "value of -" + entry.Key + ": " + valueError;
        }

        /// <summary>
        /// Returns null when <paramref name="value"/> is a single token the game reads back unchanged: printable ASCII,
        /// no space/tab/CR/LF, and not starting with '-' (it would become a new argument).
        /// </summary>
        public static string ValidateValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return "empty";
            if (value[0] == '-') return "must not start with '-'";
            foreach (char c in value)
            {
                if (c <= ' ' || c > '~') return "must be printable ASCII without spaces";
            }
            return null;
        }

        /// <summary>The native file tokenizer: split on space, CR and LF only (verify-C1 §1.2).</summary>
        public static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(text)) return tokens;
            int start = -1;
            for (int i = 0; i <= text.Length; i++)
            {
                bool delimiter = i == text.Length || text[i] == ' ' || text[i] == '\r' || text[i] == '\n';
                if (delimiter)
                {
                    if (start >= 0) tokens.Add(text.Substring(start, i - start));
                    start = -1;
                }
                else if (start < 0)
                {
                    start = i;
                }
            }
            return tokens;
        }

        private static bool IsKnownManagedLine(string line)
        {
            string first = FirstToken(line);
            if (first == null || first.Length < 2 || first[0] != '-') return false;
            string key = first.Substring(1);
            foreach (string known in KnownManagedKeys)
            {
                if (string.Equals(known, key, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static string FirstToken(string line)
        {
            List<string> tokens = Tokenize(line);
            return tokens.Count == 0 ? null : tokens[0];
        }

        /// <summary>Splits after each LF; every element keeps its own terminator (the last one may have none).</summary>
        private static List<string> SplitLines(string text)
        {
            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                lines.Add(text.Substring(start, i + 1 - start));
                start = i + 1;
            }
            if (start < text.Length) lines.Add(text.Substring(start));
            return lines;
        }

        /// <summary>CRLF when the player's lines consistently use it, LF otherwise. CR is a separator either way.</summary>
        private static string DetectNewline(List<string> lines)
        {
            int crlf = 0, lf = 0;
            foreach (string line in lines)
            {
                if (!line.EndsWith("\n", StringComparison.Ordinal)) continue;
                if (line.EndsWith("\r\n", StringComparison.Ordinal)) crlf++;
                else lf++;
            }
            return crlf > 0 && lf == 0 ? "\r\n" : "\n";
        }

        // Byte <-> char one to one, so the player's bytes survive whatever their encoding (the trimmed BCL has no
        // Latin-1 Encoding to rely on).
        private static string Latin1Decode(byte[] bytes)
        {
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }

        private static byte[] Latin1Encode(string text)
        {
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++) bytes[i] = (byte)text[i];
            return bytes;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }
    }
}
