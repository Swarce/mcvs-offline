using System;
using System.Globalization;
using System.IO;

namespace McvsOffline.PcSpike
{
    /// <summary>The command line: <c>McvsOffline.PcSpike &lt;command&gt; [--game &lt;folder&gt;] [--data &lt;folder&gt;] [...]</c>.</summary>
    public sealed class SpikeOptions
    {
        public const int DefaultPort = 51780;

        public string Command { get; set; }

        /// <summary>--game or MCVS_PC_GAME; null: Steam search.</summary>
        public string GameDir { get; set; }

        /// <summary>--data; default %LOCALAPPDATA%\McvsOffline\spike.</summary>
        public string DataDir { get; set; }

        public int Port { get; set; } = DefaultPort;

        public int TelemetrySeconds { get; set; } = 5;

        public bool NoTelemetry { get; set; }

        public bool NoDebugCapture { get; set; }

        public bool NoQuestions { get; set; }

        public static string DefaultDataDir()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
            return Path.Combine(local, "McvsOffline", "spike");
        }

        /// <summary>Parses the arguments; throws <see cref="ArgumentException"/> on a bad one. Command null: none given.</summary>
        public static SpikeOptions Parse(string[] args)
        {
            var o = new SpikeOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--game":
                        o.GameDir = Value(args, ref i);
                        break;
                    case "--data":
                        o.DataDir = Path.GetFullPath(Value(args, ref i));
                        break;
                    case "--port":
                        int port;
                        if (!int.TryParse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                            throw new ArgumentException("--port needs a number between 1 and 65535");
                        o.Port = port;
                        break;
                    case "--interval":
                        int seconds;
                        if (!int.TryParse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds < 1 || seconds > 600)
                            throw new ArgumentException("--interval needs a number of seconds between 1 and 600");
                        o.TelemetrySeconds = seconds;
                        break;
                    case "--no-telemetry":
                        o.NoTelemetry = true;
                        break;
                    case "--no-debug-log":
                        o.NoDebugCapture = true;
                        break;
                    case "--no-questions":
                        o.NoQuestions = true;
                        break;
                    case "-h":
                    case "--help":
                    case "help":
                        o.Command = "help";
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal) || o.Command != null)
                            throw new ArgumentException("unknown argument '" + arg + "'");
                        o.Command = arg.ToLowerInvariant();
                        break;
                }
            }
            if (string.IsNullOrEmpty(o.GameDir)) o.GameDir = Environment.GetEnvironmentVariable("MCVS_PC_GAME");
            if (string.IsNullOrEmpty(o.DataDir)) o.DataDir = DefaultDataDir();
            return o;
        }

        private static string Value(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException(args[i] + " needs a value");
            i++;
            return args[i];
        }
    }
}
