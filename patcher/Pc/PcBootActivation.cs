using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace McvsOffline.Patcher.Pc
{
    /// <summary>
    /// What the last start of the game said about its assemblies (WR2 F2), read from <c>McvsOffline\logs\bootstrap.log</c>: the
    /// host writes one <c>[boot] assemblies: &lt;name&gt; &lt;path&gt; (&lt;where&gt;)</c> line per redirected assembly as it loads and
    /// one <c>[boot] patches: hook-pc yes, …</c> line with the patch witnesses (hosts/embedded/GamePatchProbe.cs). The install is
    /// active only when every one came from <c>McvsOffline\bin</c> and no witness says NO; file hashes alone cannot tell (WR2: "installed,
    /// current" for 15 starts that ran the stock Game.dll).
    /// </summary>
    public sealed class PcBootActivation
    {
        public const string BinFolder = "McvsOffline\\bin";
        public const string GameFolder = "GAME FOLDER";
        public const string StartMarker = " embedded host starting";

        private static readonly Regex AssembliesLine = new Regex(@"\[boot\] (?:\+[0-9.]+ s )?assemblies: (?<name>\S+) (?<path>.*?) \((?<where>[^()]*)\)(?:: .*)?$",
                                                                 RegexOptions.CultureInvariant);

        private static readonly Regex PatchesLine = new Regex(@"\[boot\] (?:\+[0-9.]+ s )?patches: (?<list>.*)$", RegexOptions.CultureInvariant);

        public PcBootActivation()
        {
            Assemblies = new List<string>();
            Reasons = new List<string>();
        }

        /// <summary>The timestamp of the start's first line ("2026-10-29T17:53:17.425Z"), or null when the log has none.</summary>
        public string Started { get; private set; }

        /// <summary>The start's "assemblies:" lines as written (from "assemblies:" on), in order.</summary>
        public List<string> Assemblies { get; private set; }

        /// <summary>The start's "patches:" line (from "patches:" on), or null when it was not written.</summary>
        public string Patches { get; private set; }

        /// <summary>Why the install was not active: "Game loaded from the game folder", "the 'store' patch is missing"; empty when it was.</summary>
        public List<string> Reasons { get; private set; }

        /// <summary>The start loaded Game (so the redirect and the witnesses could be judged).</summary>
        public bool GameSeen { get; private set; }

        /// <summary>Not active: some assembly came from elsewhere or a witness said NO.</summary>
        public bool NotActive { get { return Reasons.Count > 0; } }

        /// <summary>"Game loaded from the game folder; the 'store' patch is missing", or null when active or unknown.</summary>
        public string Reason { get { return Reasons.Count == 0 ? null : string.Join("; ", Reasons); } }

        /// <summary>
        /// The last start in <paramref name="log"/> (from its last "embedded host starting" line, else the whole text), or null when
        /// the log has no "assemblies:" or "patches:" line at all. With <paramref name="applied"/> (install.json <c>options.patches</c>),
        /// a witness that says NO counts only when its patch was applied: <c>--patch none</c> leaves out the default 'store', and
        /// an older host still writes "store NO" for it (a current one writes "off (not applied)"); null: every NO counts.
        /// </summary>
        public static PcBootActivation Read(string log, ICollection<string> applied = null)
        {
            if (string.IsNullOrEmpty(log)) return null;
            string[] lines = log.Replace("\r", "").Split('\n');
            int start = 0;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].Contains("[embedded] ") && lines[i].Contains(StartMarker))
                {
                    start = i;
                    break;
                }
            }
            var result = new PcBootActivation();
            bool any = false;
            for (int i = start; i < lines.Length; i++)
            {
                string line = lines[i];
                if (result.Started == null && line.Length > 0) result.Started = Timestamp(line);
                Match m = AssembliesLine.Match(line);
                if (m.Success)
                {
                    any = true;
                    string name = m.Groups["name"].Value, where = m.Groups["where"].Value;
                    result.Assemblies.Add(line.Substring(line.IndexOf("assemblies: ", StringComparison.Ordinal)));
                    if (name == "Game") result.GameSeen = true;
                    if (where == BinFolder) continue;
                    result.Reasons.Add(name + (where == GameFolder ? " loaded from the game folder" : " loaded from outside " + BinFolder + " (" + where + ")"));
                    continue;
                }
                m = PatchesLine.Match(line);
                if (!m.Success) continue;
                any = true;
                result.Patches = line.Substring(line.IndexOf("patches: ", StringComparison.Ordinal));
                foreach (string item in m.Groups["list"].Value.Split(','))
                {
                    string[] parts = item.Trim().Split(new[] { ' ' }, 2);
                    if (parts.Length == 2 && parts[1] == "NO" && (applied == null || applied.Contains(parts[0])))
                        result.Reasons.Add("the '" + parts[0] + "' patch is missing");
                }
            }
            return any ? result : null;
        }

        /// <summary>The leading ISO-8601 timestamp of a log line, or null.</summary>
        private static string Timestamp(string line)
        {
            int space = line.IndexOf(' ');
            string first = space > 0 ? line.Substring(0, space) : line;
            DateTime parsed;
            return first.Length >= 19 && DateTime.TryParse(first, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed)
                ? first : null;
        }

        /// <summary>
        /// True when the start began before <paramref name="installedUtc"/> (install.json writtenUtc): that start ran an older
        /// install (or none), so it says nothing about this one. False when either time is unknown.
        /// </summary>
        public bool StartedBefore(string installedUtc)
        {
            DateTime started, installed;
            if (Started == null || string.IsNullOrEmpty(installedUtc)) return false;
            const DateTimeStyles styles = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;
            if (!DateTime.TryParse(Started, CultureInfo.InvariantCulture, styles, out started)) return false;
            if (!DateTime.TryParse(installedUtc, CultureInfo.InvariantCulture, styles, out installed)) return false;
            return started < installed;
        }

        /// <summary>One line for <c>status</c>: "active at the last start (…)", "NOT ACTIVE at the last start (…): …", or why it is unknown.</summary>
        public string Describe()
        {
            string when = Started == null ? "the last start" : "the last start (" + Started + ")";
            if (NotActive) return "NOT ACTIVE at " + when + ": " + Reason;
            if (!GameSeen || Patches == null) return "unknown: " + when + " did not load Game (still starting, or it stopped early)";
            return "active at " + when + ": every assembly from " + BinFolder + ", every applied patch's witness yes";
        }

        /// <summary>Tests and the "status" listing: the assemblies and patches lines of the start.</summary>
        public IEnumerable<string> Lines()
        {
            return Assemblies.Concat(Patches == null ? Enumerable.Empty<string>() : new[] { Patches });
        }
    }
}
