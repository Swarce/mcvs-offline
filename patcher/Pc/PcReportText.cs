using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using McvsOffline.Patcher.Core;

namespace McvsOffline.Patcher.Pc
{
    /// <summary>
    /// The player-facing text of the PC installer's reports, shared by the patcher's <c>pc</c> verb and the kit, so both print
    /// the same lines (and WR2 can compare a Linux run with the player's).
    /// </summary>
    public static class PcReportText
    {
        public static string StateName(PcInstallState state)
        {
            switch (state)
            {
                case PcInstallState.NotInstalled: return "not installed";
                case PcInstallState.Current: return "installed, current";
                case PcInstallState.Stale: return "installed, STALE";
                case PcInstallState.Damaged: return "DAMAGED";
                case PcInstallState.NotActive: return "installed, NOT ACTIVE";
                case PcInstallState.Older: return "installed, OLDER BUILD";
                default: return "PARTIAL";
            }
        }

        public static List<string> Status(PcStatusReport r)
        {
            var lines = new List<string>();
            lines.Add("game folder: " + r.GameFolder);
            if (r.State == PcInstallState.NotActive && r.StateDetail != null) lines.Add("state:       " + r.StateDetail);
            else lines.Add("state:       " + StateName(r.State) + (r.StateDetail != null && r.State != PcInstallState.Current && r.State != PcInstallState.NotInstalled
                                                                    ? " - " + r.StateDetail : ""));
            foreach (string p in r.Problems) lines.Add("  " + p);
            lines.Add("build:       " + (r.UnknownFiles.Count == 0 && r.Identity.Count > 0 ? "Steam build 6372049 (game 1.17.30), every file known" : "NOT the verified build 6372049"));
            foreach (PcFileIdentity id in r.Identity.Where(i => !i.Known)) lines.Add("  " + id);
            lines.Add("game running: " + (r.GameRunning ? "yes" : "no"));
            if (r.Manifest != null)
            {
                lines.Add("installed by: " + r.Manifest.InstallerName + " " + r.Manifest.InstallerVersion + " (server build " + (r.Manifest.ServerBuild ?? "?") + ") at " +
                          r.Manifest.WrittenUtc + "; patches " + string.Join(",", r.Manifest.Patches));
            }
            if (r.ManifestError != null) lines.Add("install.json: " + r.ManifestError);
            lines.Add("config:      " + PcExeConfig.FileName + " " + r.Config);
            lines.Add("data folder: " + Bytes(r.DataFolderBytes) + " (McvsOffline\\ without bin\\)");
            if (r.SettingsPort.HasValue || r.InstalledPort.HasValue)
            {
                string ports = "port:        settings.json " + (r.SettingsPort.HasValue ? r.SettingsPort.Value.ToString(CultureInfo.InvariantCulture) : "(default)") +
                               ", install.json " + (r.InstalledPort.HasValue ? r.InstalledPort.Value.ToString(CultureInfo.InvariantCulture) : "-");
                if (r.SettingsPort.HasValue && r.InstalledPort.HasValue && r.SettingsPort != r.InstalledPort) ports += " (they differ: the server listens on settings.json's, the geo patch calls install.json's; run install again)";
                lines.Add(ports);
            }
            if (r.Activation != null) lines.Add("activation:  " + r.Activation);
            if (r.HookEvidence != null) lines.Add("hook:        " + r.HookEvidence);
            if (r.LastBootstrapLine != null) lines.Add("last boot:   " + r.LastBootstrapLine);
            foreach (string e in r.Errors) lines.Add("ERROR: " + e);
            foreach (string l in r.Leftovers) lines.Add("leftover: " + l);
            if (r.GameWritten.Count > 0) lines.Add("written by the game, not ours: " + string.Join(", ", r.GameWritten));
            if (r.CrashDump != null) lines.Add("the game's own crash dump: " + r.CrashDump + " (from its last crash; collect reports it, never includes it)");
            if (r.OtherFiles.Count > 0) lines.Add("other files, not ours: " + string.Join(", ", r.OtherFiles));
            return lines;
        }

        public static List<string> Install(PcInstallReport r, bool dryRun)
        {
            var lines = new List<string>();
            foreach (string w in r.Warnings) lines.Add("warning: " + w);
            if (r.ExitCode != PcExitCodes.Ok)
            {
                lines.Add("error: " + r.Error);
                return lines;
            }
            foreach (string n in r.Notes) lines.Add("patched  " + n);
            lines.Add("patches  " + string.Join(",", r.Applied));
            foreach (string path in r.Written)
                lines.Add((dryRun ? "would write " : "wrote    ") + path + "  sha256 " + r.WrittenSha256[path]);
            foreach (string removed in r.Removed) lines.Add("removed  " + removed);
            if (r.SettingsPort.HasValue)
            {
                lines.Add("port     " + r.SettingsPort.Value.ToString(CultureInfo.InvariantCulture) + (dryRun ? " would be" : " is") + " the server port in " +
                          GameFolder.DataDirName + "\\" + PcSettingsPort.FileName + " (the game reads it there; your other settings are unchanged)");
            }
            if (dryRun)
            {
                lines.Add("dry run: every patch site verified, nothing written");
                return lines;
            }
            if (r.GameFolder != null)
            {
                lines.Add("exe      " + System.IO.Path.Combine(r.GameFolder, GameFolder.ExeName));
                lines.Add("config   " + System.IO.Path.Combine(r.GameFolder, PcExeConfig.FileName) + " (Windows reads it when that exe starts)");
            }
            lines.Add("next     " + PcInstaller.StartThenStatus);
            return lines;
        }

        public static List<string> Uninstall(PcInstallReport r)
        {
            var lines = new List<string>();
            if (r.ExitCode != PcExitCodes.Ok && r.LeftBehind.Count == 0)
            {
                lines.Add("error: " + r.Error);
                return lines;
            }
            foreach (string removed in r.Removed) lines.Add("removed  " + removed);
            if (r.Removed.Count == 0) lines.Add("nothing of MCVS Offline was found to remove");
            foreach (string n in r.Notes) lines.Add("note     " + n);
            foreach (string left in r.LeftBehind) lines.Add("LEFT     " + left);
            if (r.ExitCode != PcExitCodes.Ok) lines.Add("error: " + r.Error);
            return lines;
        }

        public static List<string> Verify(PcInstallReport r)
        {
            var lines = new List<string>();
            if (r.Status != null) lines.AddRange(Status(r.Status));
            foreach (string path in r.Written) lines.Add("written  " + path + "  sha256 " + r.WrittenSha256[path]);
            foreach (string w in r.Warnings) lines.Add("warning: " + w);
            lines.Add(r.ExitCode == PcExitCodes.Ok ? "verify: every written file, witness and the config read back as installed" : "error: " + r.Error);
            return lines;
        }

        public static string Bytes(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary>
        /// --patch for the PC build: "default" (the build's default set), "none" (mandatory only), "all" (default + optional), or
        /// a comma-separated list that may contain "default" (e.g. "default,forum"). Unknown ids are left for PatchPlan.For to refuse.
        /// </summary>
        public static List<string> ParsePatchList(string value)
        {
            PatchIds ids = PatchPlan.Ids(BuildProfile.PcSteam6372049);
            var list = new List<string>();
            foreach (string raw in (value ?? "").Split(','))
            {
                string id = raw.Trim().ToLowerInvariant();
                if (id.Length == 0) continue;
                if (id == "none") continue;
                if (id == "default") list.AddRange(ids.Default);
                else if (id == "all") list.AddRange(ids.Default.Concat(ids.Optional));
                else list.Add(id);
            }
            return list.Distinct(StringComparer.Ordinal).ToList();
        }
    }
}
