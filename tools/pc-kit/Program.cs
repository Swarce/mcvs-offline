using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Patcher.Pc;
using McvsOffline.PcSpike;

namespace McvsOffline.PcKit
{
    /// <summary>
    /// The player's kit for the Steam build (docs/design/pc-port.md §9):
    /// <code>
    /// McvsOffline.PcKit install    patched copies in McvsOffline\bin + install.json + ModernCombatVersus.exe.config (option A)
    /// McvsOffline.PcKit uninstall  removes them (the data stays; --remove-data --yes deletes McvsOffline\ too)
    /// McvsOffline.PcKit status     what is installed (--listing: the game folder's listing and its diff)
    /// McvsOffline.PcKit verify     status + every written file read back, with its SHA-256
    /// McvsOffline.PcKit probe      http.sys, ports, folder, .NET, Steam: before the game runs, as the normal user
    /// McvsOffline.PcKit watch      memory, debug output and endpoints of the running game
    /// McvsOffline.PcKit collect    one zip on the Desktop to send back
    /// </code>
    /// Without a command (double-click) it shows a numbered menu. Exit codes: 0 ok, 1 I/O, 2 usage, 3 refused, 4 the game runs.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            KitEnvironment env = KitEnvironment.Real();
            KitOptions options;
            try
            {
                options = KitOptions.Parse(args, Environment.GetEnvironmentVariable("MCVS_PC_GAME"));
            }
            catch (ArgumentException ex)
            {
                env.Error.WriteLine("error: " + ex.Message);
                env.Error.WriteLine(Usage);
                return PcExitCodes.Usage;
            }
            if (options.Command == null) return Menu(env, options, o => Run(env, o));
            return Run(env, options);
        }

        /// <summary>Runs one command.</summary>
        public static int Run(KitEnvironment env, KitOptions options)
        {
            if (options.Command == "help")
            {
                env.Out.WriteLine(Usage);
                return PcExitCodes.Ok;
            }
            KitServer.CleanStale(env.TempRoot, env.Now().ToUniversalTime(), TimeSpan.FromDays(1));
            string problem;
            GameLocation game = TryLocate(env, options, out problem);
            var log = new KitLog(KitFolder(env, game == null ? null : game.Directory), env.Out, env.Error, env.Now);
            log.Detail("== " + options.Command + " (kit " + KitServer.Version() + ")");
            if (game != null) log.Info("game folder: " + game.Directory + " (" + game.How + ")");
            switch (options.Command)
            {
                case "install": return Install(env, options, game, problem, log);
                case "uninstall": return Uninstall(env, options, game, problem, log);
                case "status": return Status(env, options, game, problem, log);
                case "verify": return Verify(env, options, game, problem, log);
                case "probe":
                    if (game == null) log.Warn(problem + " (the folder checks are skipped)");
                    foreach (string line in Probe.Run(env.Probe, game == null ? null : game.Directory, options.Bcl ?? env.DefaultBcl())) log.Info(line);
                    return PcExitCodes.Ok;
                case "watch": return Watch.Run(env, KitFolder(env, game == null ? null : game.Directory), log);
                case "collect": return CollectCommand(env, options, game, log);
                default:
                    log.Error("unknown command '" + options.Command + "'");
                    return PcExitCodes.Usage;
            }
        }

        // ------------------------------------------------------------------------------------------------ commands

        private static int Install(KitEnvironment env, KitOptions options, GameLocation game, string problem, KitLog log)
        {
            if (game == null) return NoGame(problem, log);
            byte[] server;
            try
            {
                server = options.ServerDll != null ? File.ReadAllBytes(options.ServerDll) : env.ServerDll();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Error("cannot read " + options.ServerDll + ": " + ex.Message);
                return PcExitCodes.Io;
            }
            if (server == null)
            {
                log.Error("this kit was built without the PC McvsOffline.Server.dll; pass one with --server <file>");
                return PcExitCodes.Io;
            }
            string temp = null;
            PcInstallReport report;
            try
            {
                temp = KitServer.CreateTempFolder(env.TempRoot);
                File.WriteAllBytes(Path.Combine(temp, GameFolder.ServerFile), server);
                report = env.Install(new PcInstallOptions
                {
                    GameFolder = game.Directory,
                    ServerDll = server,
                    ServerFolder = temp,
                    BclDirectory = options.Bcl ?? env.DefaultBcl(),
                    Port = options.Port,
                    SetSettingsPort = options.PortGiven,
                    OptionalPatches = options.Patches,
                    Force = options.Force,
                    DryRun = options.DryRun,
                    GameRunning = env.GameRunning,
                    Files = env.Files,
                    Log = log.Detail,
                    InstallerName = "McvsOffline.PcKit",
                    InstallerVersion = KitServer.Version(),
                });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Error("cannot prepare the install: " + ex.Message);
                return PcExitCodes.Io;
            }
            finally
            {
                KitServer.DeleteTempFolder(temp, log);
            }
            Print(log, PcReportText.Install(report, options.DryRun), report.ExitCode == PcExitCodes.Ok);
            if (report.ExitCode != PcExitCodes.Ok || options.DryRun) return report.ExitCode;
            log.ContinueIn(Path.Combine(GameFolder.DataDir(game.Directory), GameFolder.KitDirName));
            log.Info("");
            log.Info("Installed. Start the game from Steam; your saves will be in " + GameFolder.DataDir(game.Directory) + ".");
            log.Info("Uninstalling the game in Steam deletes them: use the settings page's Saves > Export first.");
            return PcExitCodes.Ok;
        }

        private static int Uninstall(KitEnvironment env, KitOptions options, GameLocation game, string problem, KitLog log)
        {
            if (game == null) return NoGame(problem, log);
            if (options.RemoveData && !options.Yes)
            {
                env.Out.WriteLine("This also deletes " + GameFolder.DataDir(game.Directory) + ": your saves, settings and logs.");
                env.Out.Write("Type DELETE to confirm: ");
                string answer = env.Input.ReadLine();
                if (answer == null || answer.Trim() != "DELETE")
                {
                    log.Error("not confirmed: nothing was removed");
                    return PcExitCodes.Usage;
                }
            }
            if (options.RemoveData || PcInstaller.HoldsNothingOfThePlayers(game.Directory))
            {
                // McvsOffline\ goes (--remove-data, or an install that was never started): a file open for writing cannot be
                // deleted on Windows, and the log must not recreate the folder.
                log.ContinueIn(Path.Combine(env.TempRoot, GameFolder.DataDirName, GameFolder.KitDirName));
            }
            PcInstallReport report = env.Uninstall(new PcUninstallOptions
            {
                GameFolder = game.Directory, RemoveData = options.RemoveData, GameRunning = env.GameRunning, Files = env.Files, Log = log.Detail,
            });
            Print(log, PcReportText.Uninstall(report), report.ExitCode == PcExitCodes.Ok);
            return report.ExitCode;
        }

        private static int Status(KitEnvironment env, KitOptions options, GameLocation game, string problem, KitLog log)
        {
            if (game == null) return NoGame(problem, log);
            try
            {
                PcStatusReport status = PcInstaller.Status(game.Directory, env.GameRunning, SpikeDataFolder(env));
                Print(log, PcReportText.Status(status), true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return CannotRead(ex, log);
            }
            if (!options.Listing) return PcExitCodes.Ok;
            try
            {
                foreach (string line in KitListing.Write(game.Directory, KitFolder(env, game.Directory), ListingFolders(env, game.Directory), env.Now())) log.Info(line);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Error("the listing could not be written: " + ex.Message);
                return PcExitCodes.Io;
            }
            return PcExitCodes.Ok;
        }

        private static int Verify(KitEnvironment env, KitOptions options, GameLocation game, string problem, KitLog log)
        {
            if (game == null) return NoGame(problem, log);
            PcInstallReport report;
            try
            {
                report = PcInstaller.Verify(game.Directory, options.Bcl ?? env.DefaultBcl(), env.GameRunning);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return CannotRead(ex, log);
            }
            Print(log, PcReportText.Verify(report), report.ExitCode == PcExitCodes.Ok);
            return report.ExitCode;
        }

        private static int CollectCommand(KitEnvironment env, KitOptions options, GameLocation game, KitLog log)
        {
            List<string> status = null;
            if (game != null)
            {
                try
                {
                    status = PcReportText.Status(PcInstaller.Status(game.Directory, env.GameRunning, SpikeDataFolder(env)));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // The zip is still worth having: the status part says why it is missing.
                    status = new List<string> { "error: the game folder could not be read: " + ex.Message };
                    log.Warn("the status could not be read: " + ex.Message);
                }
            }
            List<string> probe = Probe.Run(env.Probe, game == null ? null : game.Directory, options.Bcl ?? env.DefaultBcl());
            try
            {
                string zip = Collect.Run(env, game == null ? null : game.Directory, status, probe, log);
                log.Info("Send this file: " + zip);
                return PcExitCodes.Ok;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Error("collect failed: " + ex.Message);
                return PcExitCodes.Io;
            }
        }

        // ------------------------------------------------------------------------------------------------ helpers

        /// <summary>The kit's folder: McvsOffline\kit when McvsOffline\ exists in the game folder, else %TEMP%\McvsOffline\kit.</summary>
        public static string KitFolder(KitEnvironment env, string game)
        {
            if (!string.IsNullOrEmpty(game) && Directory.Exists(GameFolder.DataDir(game))) return Path.Combine(GameFolder.DataDir(game), GameFolder.KitDirName);
            return Path.Combine(env.TempRoot, GameFolder.DataDirName, GameFolder.KitDirName);
        }

        private static IEnumerable<string> ListingFolders(KitEnvironment env, string game)
        {
            return new[] { Path.Combine(GameFolder.DataDir(game), GameFolder.KitDirName), Path.Combine(env.TempRoot, GameFolder.DataDirName, GameFolder.KitDirName) };
        }

        private static string SpikeDataFolder(KitEnvironment env)
        {
            return string.IsNullOrEmpty(env.LocalAppData) ? null : Path.Combine(env.LocalAppData, "McvsOffline", "spike");
        }

        private static GameLocation TryLocate(KitEnvironment env, KitOptions options, out string problem)
        {
            problem = null;
            try
            {
                return env.LocateGame(options.GameDir);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException || ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)
            {
                problem = ex.Message;
                return null;
            }
        }

        private static int CannotRead(Exception ex, KitLog log)
        {
            log.Error("the game folder could not be read: " + ex.Message + " (an antivirus scan holding a file? try again in a minute)");
            return PcExitCodes.Io;
        }

        private static int NoGame(string problem, KitLog log)
        {
            log.Error((problem ?? "the game was not found") + ". Pass the folder that holds " + GameFolder.ExeName + " with --game \"<folder>\"" +
                      " (in Steam: right-click Modern Combat Versus > Manage > Browse local files).");
            return PcExitCodes.Usage;
        }

        private static void Print(KitLog log, IEnumerable<string> lines, bool ok)
        {
            foreach (string line in lines)
            {
                if (line.StartsWith("error: ", StringComparison.Ordinal)) log.Error(line.Substring(7));
                else if (line.StartsWith("warning: ", StringComparison.Ordinal)) log.Warn(line.Substring(9));
                else log.Info(line);
            }
        }

        // ------------------------------------------------------------------------------------------------ the menu

        /// <summary>The menu's commands, in order; "game-folder" asks for the folder.</summary>
        public static readonly IReadOnlyList<string> MenuCommands = new[]
        {
            "install", "uninstall", "status", "status-listing", "verify", "probe", "watch", "collect", "game-folder",
        };

        private static readonly string[] MenuLabels =
        {
            "install         - install MCVS Offline into the game folder",
            "uninstall       - remove it again (your saves stay)",
            "status          - show what is installed",
            "status listing  - the same, plus a listing of the game folder",
            "verify          - read every installed file back",
            "probe           - check this PC before the first start (run the kit normally, not as administrator)",
            "watch           - record the running game's memory, debug output and network endpoints",
            "collect         - make the zip to send back (on your desktop)",
            "game folder     - give the folder that holds " + GameFolder.ExeName + " (when it was not found)",
        };

        /// <summary>The double-click menu: a numbered list until 0 or the end of the input; <paramref name="run"/> runs a choice.</summary>
        public static int Menu(KitEnvironment env, KitOptions options, Func<KitOptions, int> run)
        {
            env.Out.WriteLine("MCVS Offline - PC kit " + KitServer.Version());
            while (true)
            {
                env.Out.WriteLine();
                for (int i = 0; i < MenuLabels.Length; i++) env.Out.WriteLine("  " + (i + 1) + ". " + MenuLabels[i]);
                env.Out.WriteLine("  0. quit");
                env.Out.Write("Choose: ");
                string choice = env.Input.ReadLine();
                if (choice == null) return PcExitCodes.Ok;
                int n;
                if (!int.TryParse(choice.Trim(), out n) || n < 0 || n > MenuCommands.Count) continue;
                if (n == 0) return PcExitCodes.Ok;
                string command = MenuCommands[n - 1];
                if (command == "game-folder")
                {
                    AskGameFolder(env, options);
                    continue;
                }
                options.Command = command == "status-listing" ? "status" : command;
                options.Listing = command == "status-listing";
                int code = run(options);
                env.Out.WriteLine(code == PcExitCodes.Ok ? "(done)" : "(finished with exit code " + code + ")");
                if (command == "watch") env.Input.DiscardPending();
            }
        }

        /// <summary>Asks for the game folder until one with the exe is given (an empty line keeps the current one).</summary>
        public static bool AskGameFolder(KitEnvironment env, KitOptions options)
        {
            while (true)
            {
                env.Out.WriteLine("Paste the folder that holds " + GameFolder.ExeName + " (or drag the exe into this window), then Enter. Enter alone skips this.");
                env.Out.Write("Game folder: ");
                string line = env.Input.ReadLine();
                if (line == null || line.Trim().Trim('"').Length == 0) return false;
                string notFound;
                GameLocation game = SteamLocator.Locate(line, out notFound);
                if (game != null)
                {
                    options.GameDir = game.Directory;
                    env.Out.WriteLine("game folder: " + game.Directory);
                    return true;
                }
                env.Out.WriteLine(notFound + ".");
            }
        }

        public const string Usage =
            "MCVS Offline - PC kit for the Steam build of Modern Combat Versus (6372049); no command: a menu\n\n" +
            "Usage: McvsOffline.PcKit <command> [options]\n\n" +
            "Commands:\n" +
            "  install [--patch default|<ids>] [--port n] [--dry-run] [--force]\n" +
            "                  patched copies of 4 game DLLs + the offline server in McvsOffline\\bin, install.json and\n" +
            "                  ModernCombatVersus.exe.config; no Steam file is changed\n" +
            "  uninstall [--remove-data --yes]\n" +
            "                  removes them; your data in McvsOffline\\ stays unless --remove-data\n" +
            "  status [--listing]   what is installed (--listing: a listing of the game folder, diffed against the first one)\n" +
            "  verify          reads every installed file back and prints its SHA-256\n" +
            "  probe           checks http.sys, ports, the folder, .NET and Steam: run it before the game, not as administrator\n" +
            "  watch           records the running game (memory, debug output, endpoints) until it exits or Ctrl+C\n" +
            "  collect         one zip on the desktop with the logs, for a bug report\n\n" +
            "Options:\n" +
            "  --game <folder>   the folder with ModernCombatVersus.exe (default: found through Steam); env MCVS_PC_GAME\n" +
            "  --bcl <folder>    the .NET Framework 4 folder (default %WINDIR%\\Microsoft.NET\\Framework\\v4.0.30319)\n" +
            "  --server <dll>    install this McvsOffline.Server.dll instead of the kit's own (development)\n" +
            "  --port <n>        install: the server port, set in McvsOffline\\settings.json (only that key); without it the\n" +
            "                    port already in settings.json, else 51780\n\n" +
            "Exit codes: 0 ok, 1 I/O, 2 usage, 3 refused (unknown build, site mismatch; nothing written), 4 the game is running.";
    }
}
