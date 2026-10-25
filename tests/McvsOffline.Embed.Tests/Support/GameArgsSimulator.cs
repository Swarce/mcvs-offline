using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace McvsOffline.Embed.Tests.Support
{
    /// <summary>
    /// What the game ends up with for a given args.txt, simulating the two parsers described in verify-C1 §1.2:
    /// <list type="number">
    /// <item>native (libnativeengine 0x31e178 / 0x31db24 / 0x31e86c): tokens split on space, CR, LF only; '-token' starts a
    /// key; other tokens join the pending value with a space; emplace-if-absent into a case-sensitive map (file before
    /// argv); output = positionals, then "-Key\n[value\n]" per key in byte order;</item>
    /// <item>managed (ApplicationArguments.Parse): split on '\n'; '-Name' matched case-insensitively against the
    /// Arguments fields; bool fields take no value; enum values parsed case-sensitively; unknown keys skipped.</item>
    /// </list>
    /// </summary>
    public static class GameArgsSimulator
    {
        private static readonly Dictionary<string, string[]> Enums = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ServerLocation"] = new[] { "Auto", "None", "Online", "Remote", "Embedded", "SSH", "Tunneling", "P2P" },
            ["AnubisCommand"] = new[] { "None", "QuickLaunch", "Automatch", "CreateRoom", "JoinRoom", "Reconnect" },
        };

        private static readonly HashSet<string> Strings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Level", "Credential", "Password", "DeviceId", "SuperCredential", "SuperPassword", "ClientId", "DataCenter",
            "LogCategoryFilter", "RemoteIp", "GSRemoteIp", "EnvironmentSwitcherUrl", "LocalAnubisHost", "ServerType",
        };

        private static readonly HashSet<string> Bools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Server", "Headless", "Offline", "UseDlc", "AllowP2P", "Tunneling",
        };

        /// <summary>The native output string handed to MainLoop.OnStarted.</summary>
        public static string Native(byte[] argsFile, params string[] argv)
        {
            var positional = new List<string>();
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            string name = null;
            string value = "";
            void Flush()
            {
                if (name != null && !map.ContainsKey(name)) map[name] = value;
                name = null;
                value = "";
            }
            void Token(string token)
            {
                if (token.StartsWith("-", StringComparison.Ordinal))
                {
                    Flush();
                    name = token.Substring(1);
                }
                else if (name == null) positional.Add(token);
                else value = value.Length == 0 ? token : value + " " + token;
            }

            // Bytes as chars one to one (a BOM stays glued to the first token, as in native).
            string text = new string(argsFile.Select(b => (char)b).ToArray());
            foreach (string t in text.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) Token(t);
            Flush();
            foreach (string a in argv) Token(a);
            Flush();

            var sb = new StringBuilder();
            foreach (string p in positional) sb.Append(p).Append('\n');
            foreach (var kv in map)
            {
                sb.Append('-').Append(kv.Key).Append('\n');
                if (kv.Value.Length > 0) sb.Append(kv.Value).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>The managed GameApplication.Arguments values (field name → value; bools as "True").</summary>
        public static Dictionary<string, string> Managed(string nativeOutput)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] array = nativeOutput.Trim('\n').Split('\n');
            for (int i = 0; i < array.Length; i++)
            {
                string text = array[i];
                if (!text.StartsWith("-", StringComparison.Ordinal)) continue;
                text = text.Substring(1);
                if (Bools.Contains(text))
                {
                    result[text] = "True";
                    continue;
                }
                bool isEnum = Enums.TryGetValue(text, out string[] values);
                if (!isEnum && !Strings.Contains(text)) continue;
                i++;
                if (i == array.Length) throw new ArgumentException("Need to set a value for " + text);
                if (isEnum && !values.Contains(array[i], StringComparer.Ordinal))
                    throw new ArgumentException("Cannot parse enum value " + array[i] + " for argument " + text);
                result[Enums.Keys.Concat(Strings).First(k => string.Equals(k, text, StringComparison.OrdinalIgnoreCase))] = array[i];
            }
            return result;
        }

        public static Dictionary<string, string> Parse(byte[] argsFile)
        {
            return Managed(Native(argsFile, "<Exe>/../.."));
        }
    }
}
