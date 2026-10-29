using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace McvsOffline.Patcher.Pc
{
    /// <summary>The parsed <c>McvsOffline.Patcher pc ...</c> command line (docs/design/pc-port.md §7.7).</summary>
    public sealed class PcCliOptions
    {
        public PcCliOptions()
        {
            Port = 51780;
        }

        /// <summary>install, uninstall, status or verify.</summary>
        public string Command { get; set; }

        public string GameFolder { get; set; }

        public string ServerDll { get; set; }

        public string BclDirectory { get; set; }

        public int Port { get; set; }

        /// <summary>--port was given: install also sets "port" in McvsOffline\settings.json.</summary>
        public bool PortGiven { get; set; }

        /// <summary>Null: the build's defaults.</summary>
        public List<string> Patches { get; set; }

        public bool Force { get; set; }

        public bool DryRun { get; set; }

        public bool RemoveData { get; set; }

        public bool Yes { get; set; }
    }

    /// <summary>
    /// <c>McvsOffline.Patcher pc install|uninstall|status|verify --game &lt;folder&gt; [--server &lt;pc dll&gt;] [--bcl &lt;dir&gt;]
    /// [--port n] [--patch default|&lt;ids&gt;] [--force] [--dry-run] [--remove-data --yes]</c>: the PC installer on any OS
    /// (§7.7), mainly for the Linux runs whose written SHA-256 values WR2 compares with the player's. No Steam lookup (the kit
    /// does that). The APK command line is unchanged.
    /// </summary>
    public static class PcCli
    {
        public const string Verb = "pc";

        public static int Run(string[] args, TextWriter output, TextWriter error)
        {
            PcCliOptions o;
            try
            {
                o = Parse(args);
            }
            catch (ArgumentException ex)
            {
                error.WriteLine("error: " + ex.Message);
                error.WriteLine("run 'McvsOffline.Patcher pc --help' for usage");
                return PcExitCodes.Usage;
            }
            if (o == null)
            {
                output.WriteLine(Usage);
                return PcExitCodes.Ok;
            }
            try
            {
                return RunCommand(o, output, error);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A game file that cannot be read (an antivirus scan holding it, no read permission): I/O, not a crash.
                error.WriteLine("error: " + ex.Message);
                return PcExitCodes.Io;
            }
        }

        private static int RunCommand(PcCliOptions o, TextWriter output, TextWriter error)
        {
            switch (o.Command)
            {
                case "install":
                    {
                        if (o.ServerDll == null) return UsageError(error, "--server <McvsOffline.Server.dll of the PC build> is required for install");
                        byte[] server;
                        try
                        {
                            server = File.ReadAllBytes(o.ServerDll);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            error.WriteLine("error: " + ex.Message);
                            return PcExitCodes.Io;
                        }
                        PcInstallReport report = PcInstaller.Install(new PcInstallOptions
                        {
                            GameFolder = o.GameFolder,
                            ServerDll = server,
                            ServerFolder = Path.GetDirectoryName(Path.GetFullPath(o.ServerDll)),
                            BclDirectory = o.BclDirectory ?? DefaultBcl(),
                            Port = o.Port,
                            SetSettingsPort = o.PortGiven,
                            OptionalPatches = o.Patches,
                            Force = o.Force,
                            DryRun = o.DryRun,
                            Log = line => output.WriteLine("  " + line),
                        });
                        Print(report.ExitCode == PcExitCodes.Ok ? output : error, PcReportText.Install(report, o.DryRun));
                        return report.ExitCode;
                    }
                case "uninstall":
                    {
                        if (o.RemoveData && !o.Yes) return UsageError(error, "--remove-data deletes your saves and settings: add --yes to confirm");
                        PcInstallReport report = PcInstaller.Uninstall(new PcUninstallOptions
                        {
                            GameFolder = o.GameFolder, RemoveData = o.RemoveData, Log = line => output.WriteLine("  " + line),
                        });
                        Print(report.ExitCode == PcExitCodes.Ok ? output : error, PcReportText.Uninstall(report));
                        return report.ExitCode;
                    }
                case "status":
                    Print(output, PcReportText.Status(PcInstaller.Status(o.GameFolder, null)));
                    return PcExitCodes.Ok;
                case "verify":
                    {
                        PcInstallReport report = PcInstaller.Verify(o.GameFolder, o.BclDirectory ?? DefaultBcl(), null);
                        Print(report.ExitCode == PcExitCodes.Ok ? output : error, PcReportText.Verify(report));
                        return report.ExitCode;
                    }
                default:
                    return UsageError(error, "unknown pc command '" + o.Command + "' (install, uninstall, status, verify)");
            }
        }

        /// <summary>The arguments after "pc"; null for --help. Throws <see cref="ArgumentException"/> on a bad one.</summary>
        public static PcCliOptions Parse(string[] args)
        {
            var o = new PcCliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-h":
                    case "--help":
                    case "help":
                        return null;
                    case "--game": o.GameFolder = Value(args, ref i); break;
                    case "--server": o.ServerDll = Value(args, ref i); break;
                    case "--bcl": o.BclDirectory = Value(args, ref i); break;
                    case "--port": o.Port = ParsePort(Value(args, ref i)); o.PortGiven = true; break;
                    case "--patch": o.Patches = PcReportText.ParsePatchList(Value(args, ref i)); break;
                    case "--force": o.Force = true; break;
                    case "--dry-run": o.DryRun = true; break;
                    case "--remove-data": o.RemoveData = true; break;
                    case "--yes": o.Yes = true; break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal) || o.Command != null) throw new ArgumentException("unknown argument '" + arg + "'");
                        o.Command = arg.ToLowerInvariant();
                        break;
                }
            }
            if (o.Command == null) throw new ArgumentException("a command is required: install, uninstall, status or verify");
            if (string.IsNullOrEmpty(o.GameFolder)) throw new ArgumentException("--game <folder with ModernCombatVersus.exe> is required");
            return o;
        }

        public static int ParsePort(string value)
        {
            int port;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                throw new ArgumentException("--port needs a number between 1 and 65535");
            return port;
        }

        /// <summary>
        /// The BCL directory when --bcl is not given: on Windows the 32-bit .NET Framework 4 folder the game runs on; elsewhere
        /// MCVS_NET452_REFS, else the NuGet package microsoft.netframework.referenceassemblies.net452 in the user's cache (§7.3).
        /// </summary>
        public static string DefaultBcl()
        {
            if (OperatingSystem.IsWindows())
            {
                string windir = Environment.GetEnvironmentVariable("WINDIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return Path.Combine(windir, "Microsoft.NET", "Framework", "v4.0.30319");
            }
            string refs = Environment.GetEnvironmentVariable("MCVS_NET452_REFS");
            if (!string.IsNullOrEmpty(refs)) return refs;
            string home = Environment.GetEnvironmentVariable("HOME") ?? "";
            return Path.Combine(home, ".nuget", "packages", "microsoft.netframework.referenceassemblies.net452", "1.0.3", "build", ".NETFramework", "v4.5.2");
        }

        public const string Usage =
            "MCVS Offline patcher, PC (Steam) mode: installs the offline mode into a Steam install of Modern Combat Versus\n" +
            "(build 6372049): patched copies in McvsOffline\\bin, install.json, ModernCombatVersus.exe.config. No Steam file changes.\n\n" +
            "Usage: McvsOffline.Patcher pc install|uninstall|status|verify --game <folder> [options]\n\n" +
            "  --server <dll>        the PC McvsOffline.Server.dll (artifacts/bin/McvsOffline.Server.Pc/release/), install only\n" +
            "  --bcl <dir>           the .NET Framework 4 assemblies the game's are read with (default: Windows' Framework\\v4.0.30319;\n" +
            "                        elsewhere MCVS_NET452_REFS, else the NuGet net452 reference assemblies)\n" +
            "  --port <n>            the server port: set in McvsOffline\\settings.json (only that key), baked into the geo\n" +
            "                        patch and recorded in install.json (without it settings.json is left alone and its port,\n" +
            "                        else 51780, is used)\n" +
            "  --patch <list>        default (the default set), none (mandatory only), all, or ids, e.g. default,forum\n" +
            "  --dry-run             verify every patch site and print the SHA-256 of every file, write nothing\n" +
            "  --force               install on a build that is not the verified 6372049 (sites are still verified)\n" +
            "  --remove-data --yes   uninstall also deletes McvsOffline\\ (saves, settings, logs)\n\n" +
            "Exit codes: 0 ok, 1 I/O, 2 usage, 3 refused (unknown build, site mismatch, cannot resolve; nothing written), 4 the game runs.\n" +
            "Players use McvsOffline.PcKit.exe, which finds the game through Steam (docs/design/pc-port.md §9).";

        private static int UsageError(TextWriter error, string message)
        {
            error.WriteLine("error: " + message);
            return PcExitCodes.Usage;
        }

        private static void Print(TextWriter writer, IEnumerable<string> lines)
        {
            foreach (string line in lines) writer.WriteLine(line);
        }

        private static string Value(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException(args[i] + " needs a value");
            return args[++i];
        }
    }
}
