using System;
using System.Globalization;
using System.IO;
using System.Runtime.Loader;
using System.Threading;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Standalone
{
    /// <summary>
    /// Runs the Federation emulator as a console program.
    /// <code>
    /// McvsOffline.Standalone [--port 51780] [--bind 127.0.0.1] [--data ./data] [--datapack &lt;.../files/Data&gt;]
    ///                        [--lan] [--log-level info]
    /// </code>
    /// Command-line values override settings.json for this run only. Ctrl+C (or SIGTERM) stops cleanly.
    /// Dev loop with a stock APK: <c>adb reverse tcp:51780 tcp:51780</c> and push args.txt (ARCHITECTURE §2.3).
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            ServerOptions options;
            try
            {
                options = ParseArguments(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                PrintUsage();
                return 2;
            }
            if (options == null)
            {
                PrintUsage();
                return 0;
            }

            if (string.IsNullOrEmpty(options.DataPackDir))
                options.DataPackDir = Environment.GetEnvironmentVariable("MCVS_DATAPACK");

            McvsServer server;
            try
            {
                server = McvsServer.Create(options);
                server.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: could not start the server: " + ex.Message);
                return 1;
            }

            var stopRequested = new ManualResetEventSlim(false);
            var stopped = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true; // let Main shut down cleanly instead of killing the process
                stopRequested.Set();
            };
            AssemblyLoadContext.Default.Unloading += context =>
            {
                // SIGTERM: stop and wait (bounded) so the last writes and log lines land.
                stopRequested.Set();
                stopped.Wait(TimeSpan.FromSeconds(10));
            };

            Console.WriteLine("MCVS Offline server running on " + server.Context.BaseUrl + " - press Ctrl+C to stop.");
            string page = SettingsPageUrl(server);
            if (page != null) Console.WriteLine("Settings page: " + page);
            stopRequested.Wait();
            Console.WriteLine("Stopping...");
            server.Dispose();
            stopped.Set();
            return 0;
        }

        /// <summary>
        /// The settings page with a one-time ticket (trigger "console", design §8.10), for a browser on this PC; null when the
        /// page is turned off (admin.enabled) or the admin module is missing. Written to the console only, never to a log:
        /// the ticket opens a session (once, within 120 s).
        /// </summary>
        internal static string SettingsPageUrl(McvsServer server)
        {
            IAdminPortal portal = server.Context.Features.Get<IAdminPortal>();
            return portal == null ? null : portal.MintPageUrl(AdminTriggers.Console, null);
        }

        /// <summary>Returns null when help was requested.</summary>
        internal static ServerOptions ParseArguments(string[] args)
        {
            var options = new ServerOptions();
            options.DataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-h":
                    case "--help":
                        return null;
                    case "--port":
                        int port;
                        if (!int.TryParse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port > 65535)
                            throw new ArgumentException("--port needs a number between 0 and 65535");
                        options.Port = port;
                        break;
                    case "--bind":
                        options.Bind = Value(args, ref i);
                        McvsServer.ParseBind(options.Bind); // validate early
                        break;
                    case "--data":
                        options.DataDir = Path.GetFullPath(Value(args, ref i));
                        break;
                    case "--datapack":
                        options.DataPackDir = Path.GetFullPath(Value(args, ref i));
                        break;
                    case "--lan":
                        options.LanMode = true;
                        break;
                    case "--log-level":
                        string level = Value(args, ref i);
                        LogLevel parsed = LogEntry.ParseLevel(level, (LogLevel)(-1));
                        if ((int)parsed < 0) throw new ArgumentException("unknown log level '" + level + "'");
                        options.ConsoleLevel = parsed;
                        break;
                    default:
                        throw new ArgumentException("unknown argument '" + arg + "'");
                }
            }
            return options;
        }

        private static string Value(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException(args[i] + " needs a value");
            i++;
            return args[i];
        }

        private static void PrintUsage()
        {
            Console.WriteLine(
                "MCVS Offline - Federation server (standalone host)\n\n" +
                "Usage: McvsOffline.Standalone [options]\n\n" +
                "  --port <n>         TCP port (default 51780, from settings.json)\n" +
                "  --bind <address>   127.0.0.1 (default), 0.0.0.0 or * for LAN, :: for dual-stack\n" +
                "  --data <dir>       data directory: settings.json, accounts, profiles, logs (default ./data)\n" +
                "  --datapack <dir>   the game's on-device Data folder (.../files/Data); env MCVS_DATAPACK\n" +
                "  --lan              LAN mode: hand out URLs built from the request's Host header\n" +
                "  --log-level <lvl>  console level: trace, debug, info (default), warn, error\n");
        }
    }
}
