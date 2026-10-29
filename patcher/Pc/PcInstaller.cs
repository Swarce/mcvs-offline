using System;
using System.Collections.Generic;

namespace McvsOffline.Patcher.Pc
{
    /// <summary>Exit codes of the PC installer, the patcher's <c>pc</c> verb and the kit (docs/design/pc-port.md §7.6).</summary>
    public static class PcExitCodes
    {
        public const int Ok = 0;
        public const int Io = 1;
        public const int Usage = 2;
        /// <summary>Unknown build, site mismatch, cannot resolve, a foreign config or a *_args.txt: nothing written.</summary>
        public const int Refused = 3;
        public const int GameRunning = 4;
    }

    /// <summary>What <see cref="PcInstaller.Install"/> works with (§7.6).</summary>
    public sealed class PcInstallOptions
    {
        public PcInstallOptions()
        {
            Port = 51780;
            Files = new PcFileOps();
        }

        /// <summary>The Steam install folder (ModernCombatVersus.exe, the four DLLs, Data\MCVS.game).</summary>
        public string GameFolder { get; set; }

        /// <summary>The PC McvsOffline.Server.dll (the kit's embedded copy, or --server).</summary>
        public byte[] ServerDll { get; set; }

        /// <summary>The .NET Framework 4 BCL directory for <see cref="PcAssemblyResolver"/> (mandatory).</summary>
        public string BclDirectory { get; set; }

        /// <summary>The server port written to install.json (the bootstrap reads settings.json "port").</summary>
        public int Port { get; set; }

        /// <summary>Optional patch ids on top of the mandatory ones (null: the build's defaults).</summary>
        public ICollection<string> OptionalPatches { get; set; }

        /// <summary>Install on a build-identity mismatch with a warning (the site fingerprints still protect).</summary>
        public bool Force { get; set; }

        /// <summary>Verify every site and report, write nothing.</summary>
        public bool DryRun { get; set; }

        /// <summary>Whether ModernCombatVersus is running (null: Process.GetProcessesByName).</summary>
        public Func<bool> GameRunning { get; set; }

        public IPcFileOps Files { get; set; }

        public Action<string> Log { get; set; }
    }

    /// <summary>What <see cref="PcInstaller.Uninstall"/> works with (§7.6).</summary>
    public sealed class PcUninstallOptions
    {
        public PcUninstallOptions()
        {
            Files = new PcFileOps();
        }

        public string GameFolder { get; set; }

        /// <summary>--remove-data: also delete McvsOffline\ (saves, logs) after the confirmation.</summary>
        public bool RemoveData { get; set; }

        public Func<bool> GameRunning { get; set; }

        public IPcFileOps Files { get; set; }

        public Action<string> Log { get; set; }
    }

    /// <summary>The install state <see cref="PcInstaller.Status"/> reports (§7.6).</summary>
    public enum PcInstallState
    {
        NotInstalled,
        Current,
        Stale,
        Damaged,
        Partial,
    }

    /// <summary>The result of an install, uninstall or verify run.</summary>
    public sealed class PcInstallReport
    {
        public PcInstallReport()
        {
            Written = new List<string>();
            WrittenSha256 = new Dictionary<string, string>(StringComparer.Ordinal);
            Removed = new List<string>();
            LeftBehind = new List<string>();
            Notes = new List<string>();
            Warnings = new List<string>();
        }

        /// <summary>One of <see cref="PcExitCodes"/>.</summary>
        public int ExitCode { get; set; }

        /// <summary>One sentence for the player when <see cref="ExitCode"/> is not Ok.</summary>
        public string Error { get; set; }

        /// <summary>Paths relative to the game folder with '/', in the order written (§7.4).</summary>
        public List<string> Written { get; private set; }

        /// <summary>Path → lower-case SHA-256 of what was written (or would be, in a dry run).</summary>
        public Dictionary<string, string> WrittenSha256 { get; private set; }

        public List<string> Removed { get; private set; }

        /// <summary>Things uninstall could not remove ("quit the game and run uninstall again").</summary>
        public List<string> LeftBehind { get; private set; }

        /// <summary>PatchContext.Notes and other information for the player.</summary>
        public List<string> Notes { get; private set; }

        public List<string> Warnings { get; private set; }
    }

    /// <summary>What <see cref="PcInstaller.Status"/> found in the game folder (§7.6).</summary>
    public sealed class PcStatusReport
    {
        public PcStatusReport()
        {
            UnknownFiles = new List<string>();
            Leftovers = new List<string>();
            Errors = new List<string>();
            GameWritten = new List<string>();
            OtherFiles = new List<string>();
        }

        public string GameFolder { get; set; }

        public PcInstallState State { get; set; }

        /// <summary>Files whose SHA-256 or MVID is not build 6372049's.</summary>
        public List<string> UnknownFiles { get; private set; }

        /// <summary>A foreign or spike config, an args.txt block while not installed, *.mcvs-tmp files, the spike's data folder.</summary>
        public List<string> Leftovers { get; private set; }

        /// <summary>E.g. a *_args.txt in the game folder (the engine reads it instead of args.txt).</summary>
        public List<string> Errors { get; private set; }

        /// <summary>Informational: written by the game, not ours (dr0.bin..dr3.bin, OF_PP.bin).</summary>
        public List<string> GameWritten { get; private set; }

        /// <summary>Informational: other top-level entries that are neither the depot's nor ours.</summary>
        public List<string> OtherFiles { get; private set; }

        public bool GameRunning { get; set; }

        public long DataFolderBytes { get; set; }

        public int? SettingsPort { get; set; }

        public int? InstalledPort { get; set; }

        public string LastBootstrapLine { get; set; }
    }

    /// <summary>
    /// Install / Uninstall / Status / Verify of the offline mode in a Steam install (docs/design/pc-port.md §7.4-§7.6), used by
    /// the patcher's <c>pc</c> verb and the kit. Day-0 skeleton: the types are the contract; WP3 implements the bodies.
    /// </summary>
    public static class PcInstaller
    {
        public static PcInstallReport Install(PcInstallOptions options)
        {
            throw new NotImplementedException("PcInstaller.Install (WP3)");
        }

        public static PcInstallReport Uninstall(PcUninstallOptions options)
        {
            throw new NotImplementedException("PcInstaller.Uninstall (WP3)");
        }

        public static PcStatusReport Status(string gameFolder, Func<bool> gameRunning)
        {
            throw new NotImplementedException("PcInstaller.Status (WP3)");
        }

        /// <summary>Status plus the re-read of every written file; the report carries the written SHA-256 values (WR2 step 3).</summary>
        public static PcInstallReport Verify(string gameFolder, string bclDirectory, Func<bool> gameRunning)
        {
            throw new NotImplementedException("PcInstaller.Verify (WP3)");
        }
    }
}
