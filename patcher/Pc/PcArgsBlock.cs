using System;
using System.Collections.Generic;
using System.Text;

namespace McvsOffline.Patcher.Pc
{
    /// <summary>
    /// The managed block the PC bootstrap writes into <c>&lt;install&gt;\args.txt</c> (hosts/embedded/ArgsFile.cs), as the
    /// installer's uninstall sees it (docs/design/pc-port.md §3.4, §7.6): it only finds and strips the block. The patcher does
    /// not link ArgsFile.cs (the test assembly compiles hosts/embedded too, and a second public ArgsFile would collide), so the
    /// markers are repeated here and a contract test asserts they equal ArgsFile's.
    /// </summary>
    public static class PcArgsBlock
    {
        /// <summary>= ArgsFile.BeginMarker.</summary>
        public const string BeginMarker = "-McvsOfflineBegin";

        /// <summary>= ArgsFile.EndMarker.</summary>
        public const string EndMarker = "-McvsOfflineEnd";

        /// <summary>= ArgsFile.KnownManagedKeys: what a block whose end marker was deleted may contain.</summary>
        public static readonly string[] KnownManagedKeys =
        {
            "ServerLocation", "AnubisCommand", "EnvironmentSwitcherUrl", "SuperCredential", "SuperPassword", "DeviceId",
            "DataCenter", "LocalAnubisHost",
        };

        /// <summary>True when a line of <paramref name="existing"/> starts with <see cref="BeginMarker"/>.</summary>
        public static bool HasBlock(byte[] existing)
        {
            if (existing == null) return false;
            foreach (string line in SplitLines(Latin1(existing)))
            {
                if (string.Equals(FirstToken(line), BeginMarker, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// <paramref name="existing"/> without the managed block(s) and stray end markers, every other byte kept (the reverse of
        /// ArgsFile.Merge for a file that ended with a line break). <paramref name="onlyBlockLeft"/>: nothing but whitespace is
        /// left, so uninstall deletes the file (the depot has no args.txt).
        /// </summary>
        public static byte[] Remove(byte[] existing, out bool onlyBlockLeft)
        {
            if (existing == null)
            {
                onlyBlockLeft = true;
                return new byte[0];
            }
            List<string> lines = SplitLines(Latin1(existing));
            var kept = new StringBuilder(existing.Length);
            for (int i = 0; i < lines.Count; i++)
            {
                string first = FirstToken(lines[i]);
                if (string.Equals(first, EndMarker, StringComparison.Ordinal)) continue;
                if (!string.Equals(first, BeginMarker, StringComparison.Ordinal))
                {
                    kept.Append(lines[i]);
                    continue;
                }
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
                // Begin without end, as ArgsFile.Merge recovers it: only the consecutive lines that are ours.
                int k = i + 1;
                while (k < lines.Count && IsKnownManagedLine(lines[k])) k++;
                i = k - 1;
            }
            string text = kept.ToString();
            onlyBlockLeft = text.Trim(' ', '\t', '\r', '\n').Length == 0;
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++) bytes[i] = (byte)text[i];
            return bytes;
        }

        private static bool IsKnownManagedLine(string line)
        {
            string first = FirstToken(line);
            if (first == null || first.Length < 2 || first[0] != '-') return false;
            return Array.IndexOf(KnownManagedKeys, first.Substring(1)) >= 0;
        }

        /// <summary>The native tokenizer's first token: separators are space, CR and LF only.</summary>
        private static string FirstToken(string line)
        {
            int start = -1;
            for (int i = 0; i <= line.Length; i++)
            {
                bool delimiter = i == line.Length || line[i] == ' ' || line[i] == '\r' || line[i] == '\n';
                if (delimiter)
                {
                    if (start >= 0) return line.Substring(start, i - start);
                }
                else if (start < 0)
                {
                    start = i;
                }
            }
            return null;
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

        private static string Latin1(byte[] bytes)
        {
            var chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }
    }
}
