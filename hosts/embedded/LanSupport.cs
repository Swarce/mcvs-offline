// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
// Called from Gameloft.Engine.dll by the patcher's 'lan' IL patch (verify-C6 §5 P2, P3). The signatures are part of
// the patcher contract (patcher/Patches/LanPatch.cs): change both together.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using McvsOffline.Server.Core;

namespace McvsOffline
{
    /// <summary>
    /// Helpers for the LocalAnubis LAN patches. Gameloft.Engine calls them with plain strings, so this assembly never
    /// references the game.
    /// <list type="bullet">
    /// <item><b>P2</b> (host): <c>LocalAnubisManager.Start(ct)</c> computes the advertised host once
    /// (<see cref="ResetPublicHost"/> then <see cref="SetPublicHost"/>), and <c>GetPublicHost()</c> returns that cached
    /// value (<see cref="GetCachedPublicHost"/>). Stock code recomputes it on every access from the live
    /// <c>GameApplication.Args</c>, which the Controller swaps for the lobbyconfig cmd_args while it creates a world, so
    /// rooms launched meanwhile advertised "localhost" to LAN guests (verify-C6 §2.6).</item>
    /// <item><b>P3</b> (guest): <c>ClientSettings.LocalAnubisFinderHost</c> = <see cref="ChooseFinderHost"/>(Args.RemoteIp,
    /// PublicHost), so <c>-RemoteIp &lt;host&gt;</c> finally points a guest at the host's Finder (it was inert,
    /// verify-C6 §2.4).</item>
    /// </list>
    /// Every method is total: it never throws into the game.
    /// </summary>
    public static class LanSupport
    {
        public const string Loopback = "localhost";

        private static volatile string cachedPublicHost;

        /// <summary>From settings.json embedded.lanAdvertiseHost (set by the embedded host); wins over detection.</summary>
        public static string ConfiguredHost { get; set; }

        /// <summary>The value cached at LocalAnubis start, or null (the game then computes it as stock code does).</summary>
        public static string GetCachedPublicHost()
        {
            return cachedPublicHost;
        }

        /// <summary>Forgets the cached value so the next GetPublicHost() runs the game's own logic.</summary>
        public static void ResetPublicHost()
        {
            cachedPublicHost = null;
        }

        /// <summary>
        /// Caches the host to advertise. <paramref name="detected"/> is the game's own answer, computed with the
        /// original arguments: "localhost" for the default loopback setup (kept, single-device play is unchanged),
        /// otherwise the first non-loopback IPv4 Mono found, which is not reliably the Wi-Fi one (verify-C6 §2.2).
        /// Order: settings embedded.lanAdvertiseHost, <c>-GSRemoteIp</c>, a private LAN address, the game's answer.
        /// </summary>
        public static void SetPublicHost(string gsRemoteIp, string detected)
        {
            try
            {
                cachedPublicHost = SelectPublicHost(ConfiguredHost, gsRemoteIp, detected, LocalIPv4Addresses());
            }
            catch (Exception)
            {
                cachedPublicHost = detected;
            }
        }

        /// <summary>P3: an explicit <c>-RemoteIp</c> wins; otherwise the stock default (the local PublicHost).</summary>
        public static string ChooseFinderHost(string remoteIp, string publicHost)
        {
            string remote = remoteIp == null ? null : remoteIp.Trim();
            return string.IsNullOrEmpty(remote) ? publicHost : remote;
        }

        /// <summary>The selection rule behind <see cref="SetPublicHost"/>, separated for tests.</summary>
        public static string SelectPublicHost(string configured, string gsRemoteIp, string detected, IList<IPAddress> local)
        {
            if (string.Equals(detected, Loopback, StringComparison.OrdinalIgnoreCase)) return detected;
            if (!string.IsNullOrEmpty(configured) && configured.Trim().Length > 0) return configured.Trim();
            if (!string.IsNullOrEmpty(gsRemoteIp) && gsRemoteIp.Trim().Length > 0) return gsRemoteIp.Trim();

            IPAddress best = null;
            int bestRank = Num.IntMax;
            if (local != null)
            {
                foreach (IPAddress address in local)
                {
                    int rank = PrivateRank(address);
                    if (rank < bestRank)
                    {
                        best = address;
                        bestRank = rank;
                    }
                }
            }
            if (best != null) return best.ToString();
            return detected;
        }

        /// <summary>
        /// 0 = 192.168/16 (home Wi-Fi and Android hotspots), 1 = 172.16/12, 2 = 10/8 (also used by mobile carriers),
        /// Num.IntMax = not a private IPv4.
        /// </summary>
        internal static int PrivateRank(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return Num.IntMax;
            byte[] b = address.GetAddressBytes();
            if (b[0] == 192 && b[1] == 168) return 0;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return 1;
            if (b[0] == 10) return 2;
            return Num.IntMax;
        }

        /// <summary>The device's IPv4 addresses as Mono reports them (interface addresses on Android, verify-C6 §2.2).</summary>
        private static IList<IPAddress> LocalIPv4Addresses()
        {
            var result = new List<IPAddress>();
            try
            {
                foreach (IPAddress address in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address)) result.Add(address);
                }
            }
            catch (Exception)
            {
            }
            return result;
        }
    }
}
