// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL, into the net8 tests and into the patcher tests' server fixture.
// Self-contained on purpose (System only): the patch fixtures compile this file alone.
using System;

namespace McvsOffline
{
    /// <summary>
    /// The ports this start of the game uses, for the patched game code (docs/design/port-fallback.md). The embedded host
    /// decides them once, synchronously, before the game reads args.txt (<see cref="Configure"/>); the patched getters ask here
    /// instead of using the port baked in at patch time:
    /// <list type="bullet">
    /// <item>'geo' (Gameloft.Util): <c>Geolocalization.get_GeolocalizationUrl</c> returns
    /// <c>GeolocalizationUrl("http://127.0.0.1:&lt;--port&gt;/location/?ip=")</c>;</item>
    /// <item>'finderport' (Gameloft.Engine, mandatory on Android): <c>ClientSettings</c> initialises
    /// <c>LocalAnubisFinderPort</c> with <c>LocalAnubisFinderPort(51750)</c>.</item>
    /// </list>
    /// Both return their argument unchanged until the host has decided (and whenever anything goes wrong): the patched game
    /// behaves exactly as with the baked values. They are part of the patch contract (looked up by name and parameter types),
    /// run on game threads, never throw and never block.
    /// </summary>
    public static class PortHooks
    {
        /// <summary>The LocalAnubis Finder's port when nothing overrides it (LocalAnubisManager.DefaultFinderPort).</summary>
        public const int DefaultFinderPort = 51750;

        private const string LoopbackPrefix = "http://127.0.0.1:";
        private const string LocationPath = "/location/";

        private static volatile string serverBase;
        private static volatile int finderPort;

        /// <summary>
        /// Set by the embedded host before args.txt is written: <paramref name="serverPort"/> is the port of the offline server the
        /// game reaches on 127.0.0.1 this start (the bound one, or the configured one when another MCVS Encore server answers
        /// there), <paramref name="localAnubisFinderPort"/> the port the game's LocalAnubis Finder listens on. 0 (or out of range):
        /// unknown, the baked value stays.
        /// </summary>
        public static void Configure(int serverPort, int localAnubisFinderPort)
        {
            serverBase = IsPort(serverPort) ? LoopbackPrefix + serverPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" : null;
            finderPort = IsPort(localAnubisFinderPort) ? localAnubisFinderPort : 0;
        }

        /// <summary>The server base URL the hooks use ("http://127.0.0.1:51780/"), or null before <see cref="Configure"/>.</summary>
        public static string ServerBase { get { return serverBase; } }

        /// <summary>The Finder port the hooks use, or 0 before <see cref="Configure"/>.</summary>
        public static int FinderPort { get { return finderPort; } }

        /// <summary>
        /// The 'geo' patch: <paramref name="baked"/> ("http://127.0.0.1:&lt;port&gt;/location/?ip=") with this start's server port.
        /// Anything else (another host, no "/location/", nothing decided) comes back unchanged.
        /// </summary>
        public static string GeolocalizationUrl(string baked)
        {
            try
            {
                string current = serverBase;
                if (current == null || baked == null || !baked.StartsWith(LoopbackPrefix, StringComparison.Ordinal)) return baked;
                int path = baked.IndexOf(LocationPath, LoopbackPrefix.Length, StringComparison.Ordinal);
                if (path < 0) return baked;
                for (int i = LoopbackPrefix.Length; i < path; i++)
                {
                    if (baked[i] < '0' || baked[i] > '9') return baked;
                }
                return current + baked.Substring(path + 1);
            }
            catch (Exception)
            {
                return baked;
            }
        }

        /// <summary>The 'finderport' patch: the port the game's Finder listens on this start, else <paramref name="baked"/> (51750).</summary>
        public static int LocalAnubisFinderPort(int baked)
        {
            int current = finderPort;
            return current > 0 ? current : baked;
        }

        private static bool IsPort(int port)
        {
            return port > 0 && port <= 65535;
        }
    }
}
