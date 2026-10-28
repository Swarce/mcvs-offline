using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using McvsOffline.Embedded;
using McvsOffline.Server;
using McvsOffline.Server.Core;

namespace McvsOffline.PcSpike
{
    /// <summary>
    /// The WP0 kit for the Steam build (docs/PC-SPIKE.md, docs/research/pc-port.md §5 WP0):
    /// <code>
    /// McvsOffline.PcSpike start           server on 127.0.0.1:51780 + args.txt next to the exe (+ telemetry, game debug log)
    /// McvsOffline.PcSpike stop            restores args.txt
    /// McvsOffline.PcSpike redirect-test   ModernCombatVersus.exe.config + marker-writing EngineBridge copy
    /// McvsOffline.PcSpike redirect-undo   removes both
    /// McvsOffline.PcSpike telemetry       memory samples of the running game into a CSV
    /// McvsOffline.PcSpike collect         one zip on the desktop to send back
    /// </code>
    /// Without a command (double-click) it shows a menu.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            SpikeOptions options;
            try
            {
                options = SpikeOptions.Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                PrintUsage();
                return 2;
            }
            if (options.Command == null) return Menu(options);
            return Run(options, true);
        }

        private static int Run(SpikeOptions options, bool fromCommandLine)
        {
            switch (options.Command)
            {
                case "help":
                    PrintUsage();
                    return 0;
                case "start":
                    return Start(options);
                case "stop":
                    return Stop(options);
                case "redirect-test":
                    return RedirectTest(options);
                case "redirect-undo":
                    return RedirectUndo(options);
                case "telemetry":
                    return Telemetry(options);
                case "collect":
                    return Collect(options);
                case "status":
                    return Status(options);
                default:
                    Console.Error.WriteLine("error: unknown command '" + options.Command + "'");
                    if (fromCommandLine) PrintUsage();
                    return 2;
            }
        }

        // ------------------------------------------------------------------------------------------------ commands

        private static int Start(SpikeOptions options)
        {
            var log = new SpikeLog(options.DataDir);
            log.Info("== start (data folder " + options.DataDir + ")");
            GameLocation game = FindGame(options, log);
            if (game == null) return 1;
            List<string> prefixed = ArgsTxtStep.PrefixedArgsFiles(game.Directory);
            if (prefixed.Count > 0)
                log.Warn("the game folder has " + string.Join(", ", prefixed) + ": the game may read that instead of args.txt (please report this)");

            string view = PcDataView.Build(game.Directory, options.DataDir, log);
            McvsServer server;
            try
            {
                server = McvsServer.Create(new ServerOptions
                {
                    DataDir = options.DataDir,
                    DataPackDir = view,
                    Port = options.Port,
                    Bind = "127.0.0.1",
                    ConsoleLog = true,
                    FileLog = true,
                });
                server.Start();
            }
            catch (Exception ex)
            {
                log.Error("could not start the server on 127.0.0.1:" + options.Port + ": " + ex.Message +
                          " (is another copy of this tool or the MCVS Offline server already running?)");
                return 1;
            }
            log.Result("server", "running at " + server.Context.BaseUrl);

            ArgsInstallResult argsResult;
            try
            {
                byte[] existing = File.Exists(ArgsTxtStep.ArgsPath(game.Directory)) ? File.ReadAllBytes(ArgsTxtStep.ArgsPath(game.Directory)) : null;
                Log serverLog = server.Context.Logger.For("pc-spike");
                string deviceId = DeviceIdentity.LoadOrCreate(options.DataDir, ArgsFile.ReadManagedValue(existing, "DeviceId"), serverLog);
                IList<ArgEntry> managed = ManagedArgs.Build(server.Context.Settings, deviceId, server.Port, serverLog);
                argsResult = ArgsTxtStep.Install(game.Directory, options.DataDir, managed, log);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Error("args.txt: " + ex.Message);
                server.Dispose();
                return 1;
            }
            foreach (string warning in argsResult.Warnings) log.Warn("args.txt: " + warning);
            if (!argsResult.Written)
            {
                log.Error("args.txt could not be written into the game folder: " + argsResult.WriteError);
                log.Error("That is a result too: please run 'collect' and send the zip.");
                server.Dispose();
                return 1;
            }
            log.Info("args.txt now reads:\n" + argsResult.Content.TrimEnd());

            TelemetrySampler telemetry = null;
            if (!options.NoTelemetry)
            {
                telemetry = new TelemetrySampler(options.DataDir, TimeSpan.FromSeconds(options.TelemetrySeconds), log);
                telemetry.StartBackground();
            }
            IDisposable capture = null;
            if (!options.NoDebugCapture && OperatingSystem.IsWindows()) capture = DebugOutputCapture.TryStart(options.DataDir, log);

            Console.WriteLine();
            Console.WriteLine("Ready. Leave this window open, then:");
            Console.WriteLine("  1. Start Modern Combat Versus from Steam (Play).");
            Console.WriteLine("  2. Go as far as you can: main menu, one bot match, open a news/event link, then quit the game.");
            Console.WriteLine("  3. Type a short note + Enter here at each step (e.g. 'menu', 'match start', 'match end'); it goes into the telemetry.");
            Console.WriteLine("  4. Press Ctrl+C here when done, then run 'stop' and 'collect'.");
            Console.WriteLine();

            var stopRequested = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                stopRequested.Set();
            };
            // Closing the window (CTRL_CLOSE_EVENT) arrives as SIGTERM on Windows: stop cleanly so the logs are complete.
            using (PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                stopRequested.Set();
            }))
            {
                var notes = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while (!stopRequested.IsSet && (line = Console.ReadLine()) != null)
                        {
                            if (line.Trim().Length == 0) continue;
                            log.Info("note: " + line.Trim());
                            if (telemetry != null) telemetry.AddNote(line);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }) { IsBackground = true, Name = "notes" };
                notes.Start();
                stopRequested.Wait();
            }

            Console.WriteLine("Stopping...");
            if (capture != null) capture.Dispose();
            if (telemetry != null)
            {
                telemetry.Dispose();
                log.Info("telemetry: " + telemetry.Samples + " samples in " + telemetry.CsvPath);
            }
            server.Dispose();
            log.Info("server stopped. args.txt still points at it: run 'stop' to restore it, then 'collect'.");
            return 0;
        }

        private static int Stop(SpikeOptions options)
        {
            var log = new SpikeLog(options.DataDir);
            log.Info("== stop");
            GameLocation game = FindGame(options, log);
            if (game == null) return 1;
            try
            {
                string done = ArgsTxtStep.Restore(game.Directory, options.DataDir, log);
                Console.WriteLine("args.txt: " + done);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Error("stop: " + ex.Message);
                return 1;
            }
            if (File.Exists(RedirectStep.ConfigPath(game.Directory)))
                Console.WriteLine("Note: the redirect test is still installed; run 'redirect-undo' to remove it.");
            return 0;
        }

        private static int RedirectTest(SpikeOptions options)
        {
            var log = new SpikeLog(options.DataDir);
            log.Info("== redirect-test");
            GameLocation game = FindGame(options, log);
            if (game == null) return 1;
            if (!RedirectStep.Install(game.Directory, options.DataDir, log)) return 1;
            Console.WriteLine();
            Console.WriteLine("Installed. Now:");
            Console.WriteLine("  1. Start Modern Combat Versus from Steam and wait for the title screen or main menu (or until it fails).");
            Console.WriteLine("  2. Quit the game.");
            Console.WriteLine("  3. Run 'redirect-undo' (it prints the result and removes the test).");
            return 0;
        }

        private static int RedirectUndo(SpikeOptions options)
        {
            var log = new SpikeLog(options.DataDir);
            log.Info("== redirect-undo");
            GameLocation game = FindGame(options, log);
            if (game == null) return 1;
            string marker = RedirectStep.DescribeMarker(options.DataDir);
            log.Result("redirect test", marker);
            return RedirectStep.Undo(game.Directory, options.DataDir, log) ? 0 : 1;
        }

        private static int Telemetry(SpikeOptions options)
        {
            var log = new SpikeLog(options.DataDir);
            log.Info("== telemetry");
            using (var sampler = new TelemetrySampler(options.DataDir, TimeSpan.FromSeconds(options.TelemetrySeconds), log))
            {
                var stop = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    stop.Set();
                };
                sampler.StartBackground();
                Console.WriteLine("Type a note + Enter to mark a moment (e.g. 'match start'); Ctrl+C to stop. It also stops when the game exits.");
                var notes = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while (!stop.IsSet && (line = Console.ReadLine()) != null)
                        {
                            if (line.Trim().Length == 0) continue;
                            log.Info("note: " + line.Trim());
                            sampler.AddNote(line);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }) { IsBackground = true, Name = "notes" };
                notes.Start();
                while (!stop.Wait(1000))
                {
                    if (sampler.GameExited) break;
                }
                log.Info("telemetry: " + sampler.Samples + " samples in " + sampler.CsvPath);
            }
            return 0;
        }

        private static int Collect(SpikeOptions options)
        {
            var log = new SpikeLog(options.DataDir);
            log.Info("== collect");
            GameLocation game = FindGame(options, log);
            bool ask = !options.NoQuestions && !Console.IsInputRedirected;
            string zip = Collector.Run(options, game, log, ask);
            Console.WriteLine();
            Console.WriteLine("Done: " + zip);
            Console.WriteLine("Please send this file back. It holds logs and our own files only, no game files.");
            return 0;
        }

        private static int Status(SpikeOptions options)
        {
            var log = new SpikeLog(options.DataDir, echo: true);
            GameLocation game = FindGame(options, log);
            Console.WriteLine("data folder: " + options.DataDir);
            if (game != null)
            {
                Console.WriteLine("args.txt:    " + (File.Exists(ArgsTxtStep.ArgsPath(game.Directory)) ? "present" : "absent") +
                                  (File.Exists(Path.Combine(options.DataDir, ArgsTxtStep.BackupFolder, ArgsTxtStep.StateFileName)) ? " (ours; 'stop' restores the original)" : ""));
                Console.WriteLine("exe.config:  " + (File.Exists(RedirectStep.ConfigPath(game.Directory)) ? "present" : "absent"));
                Console.WriteLine("exe header:  " + PeHeader.Describe(game.Exe));
            }
            Console.WriteLine("redirect:    " + RedirectStep.DescribeMarker(options.DataDir));
            return 0;
        }

        // ------------------------------------------------------------------------------------------------ helpers

        private static GameLocation FindGame(SpikeOptions options, SpikeLog log)
        {
            string problem;
            GameLocation game = SteamLocator.Locate(options.GameDir, out problem);
            if (game == null)
            {
                log.Error(problem + ". Pass the folder that holds " + SteamLocator.ExeName + " with --game \"<folder>\".");
                return null;
            }
            log.Info("game folder: " + game.Directory + " (" + game.How + ")");
            return game;
        }

        private static int Menu(SpikeOptions options)
        {
            string[] commands = { "start", "stop", "redirect-test", "redirect-undo", "telemetry", "collect", "status" };
            string[] labels =
            {
                "start          - run the test server and point the game at it (args.txt)",
                "stop           - restore args.txt",
                "redirect-test  - install the .exe.config redirect test",
                "redirect-undo  - show its result and remove it",
                "telemetry      - record the game's memory use only",
                "collect        - make the zip to send back (on your desktop)",
                "status         - show what is installed",
            };
            Console.WriteLine("MCVS Offline - PC spike kit (see PC-SPIKE.md)");
            while (true)
            {
                Console.WriteLine();
                for (int i = 0; i < labels.Length; i++) Console.WriteLine("  " + (i + 1) + ". " + labels[i]);
                Console.WriteLine("  0. quit");
                Console.Write("Choose: ");
                string choice = Console.ReadLine();
                if (choice == null) return 0;
                int n;
                if (!int.TryParse(choice.Trim(), out n) || n < 0 || n > commands.Length) continue;
                if (n == 0) return 0;
                options.Command = commands[n - 1];
                Run(options, false);
                if (options.Command == "start") return 0;   // Ctrl+C ends the whole program
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine(
                "MCVS Offline - PC spike kit (WP0); step by step: PC-SPIKE.md\n\n" +
                "Usage: McvsOffline.PcSpike <command> [options]   (no command: a menu)\n\n" +
                "Commands:\n" +
                "  start           run our server on 127.0.0.1:51780 (data in %LOCALAPPDATA%\\McvsOffline\\spike), write args.txt\n" +
                "                  next to ModernCombatVersus.exe (the old one is backed up), record memory and the game's debug log\n" +
                "  stop            restore args.txt\n" +
                "  redirect-test   write ModernCombatVersus.exe.config + a marker-writing EngineBridge.dll copy in McvsOffline\\spike\n" +
                "  redirect-undo   print the result and remove both\n" +
                "  telemetry       sample the game's memory into a CSV (start does this too)\n" +
                "  collect         zip logs, results and summary.txt onto the desktop\n" +
                "  status          show what is installed\n\n" +
                "Options:\n" +
                "  --game <folder>   the folder with ModernCombatVersus.exe (default: found through Steam); env MCVS_PC_GAME\n" +
                "  --data <folder>   our data folder (default %LOCALAPPDATA%\\McvsOffline\\spike)\n" +
                "  --port <n>        server port (default 51780)\n" +
                "  --interval <s>    telemetry interval in seconds (default 5)\n" +
                "  --no-telemetry, --no-debug-log, --no-questions\n");
        }
    }
}
