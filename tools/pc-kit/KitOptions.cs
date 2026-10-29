using System;
using System.Collections.Generic;
using McvsOffline.Patcher.Pc;

namespace McvsOffline.PcKit
{
    /// <summary>The kit's command line (docs/design/pc-port.md §9.2): <c>McvsOffline.PcKit &lt;command&gt; [options]</c>.</summary>
    public sealed class KitOptions
    {
        public static readonly IReadOnlyList<string> Commands = new[] { "install", "uninstall", "status", "verify", "probe", "watch", "collect", "help" };

        public KitOptions()
        {
            Port = 51780;
        }

        /// <summary>Null: none given (double-click: the menu).</summary>
        public string Command { get; set; }

        /// <summary>--game or MCVS_PC_GAME; null: the Steam lookup.</summary>
        public string GameDir { get; set; }

        /// <summary>--bcl; null: %WINDIR%\Microsoft.NET\Framework\v4.0.30319.</summary>
        public string Bcl { get; set; }

        /// <summary>--server: a PC McvsOffline.Server.dll to install instead of the embedded one (development).</summary>
        public string ServerDll { get; set; }

        public int Port { get; set; }

        /// <summary>--patch; null: the build's defaults.</summary>
        public List<string> Patches { get; set; }

        public bool DryRun { get; set; }

        public bool Force { get; set; }

        public bool RemoveData { get; set; }

        public bool Yes { get; set; }

        public bool Listing { get; set; }

        /// <summary>Throws <see cref="ArgumentException"/> on a bad argument. <paramref name="environmentGame"/>: MCVS_PC_GAME.</summary>
        public static KitOptions Parse(string[] args, string environmentGame)
        {
            var o = new KitOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-h":
                    case "--help":
                        o.Command = "help";
                        break;
                    case "--game": o.GameDir = Value(args, ref i); break;
                    case "--bcl": o.Bcl = Value(args, ref i); break;
                    case "--server": o.ServerDll = Value(args, ref i); break;
                    case "--port": o.Port = PcCli.ParsePort(Value(args, ref i)); break;
                    case "--patch": o.Patches = PcReportText.ParsePatchList(Value(args, ref i)); break;
                    case "--dry-run": o.DryRun = true; break;
                    case "--force": o.Force = true; break;
                    case "--remove-data": o.RemoveData = true; break;
                    case "--yes": o.Yes = true; break;
                    case "--listing": o.Listing = true; break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal) || o.Command != null) throw new ArgumentException("unknown argument '" + arg + "'");
                        string command = arg.ToLowerInvariant();
                        if (!((IList<string>)Commands).Contains(command)) throw new ArgumentException("unknown command '" + arg + "'");
                        o.Command = command;
                        break;
                }
            }
            if (string.IsNullOrEmpty(o.GameDir) && !string.IsNullOrEmpty(environmentGame)) o.GameDir = environmentGame;
            return o;
        }

        private static string Value(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException(args[i] + " needs a value");
            return args[++i];
        }
    }
}
