// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded
{
    /// <summary>
    /// The arguments the offline game needs at launch (ARCHITECTURE §2.2), built from settings.json:
    /// <code>
    /// -ServerLocation Embedded                          in-process LocalAnubis + TS/GS worlds (verify-C2)
    /// -AnubisCommand QuickLaunch                        mandatory for Embedded (verify-C6 §3.1)
    /// -EnvironmentSwitcherUrl http://127.0.0.1:51780/   our Federation; explicit port + trailing slash (verify-C1 §2)
    /// -SuperCredential game:mcvsoffline                 LocalAnubis super session; exactly one ':' (verify-C2 §2)
    /// -SuperPassword mcvsoffline
    /// -DeviceId mcvs...                                 skips the native GDID wait (10 s, verify-C2 §1.2)
    /// -LocalAnubisHost 0.0.0.0                          only with settings.lanMode (verify-C6 §5)
    /// </code>
    /// Enum values are case-sensitive (Enum.Parse without ignoreCase).
    /// </summary>
    public static class ManagedArgs
    {
        public static List<ArgEntry> Build(Settings settings, string deviceId, int port, Log log)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            log = log ?? Log.Null;

            string credential = settings.SuperCredential;
            if (!IsValidSuperCredential(credential))
            {
                log.Error("settings.superCredential '" + credential + "' is not 'type:name' without spaces or '-'; using " +
                          Settings.DefaultSuperCredential + " (a malformed value breaks the game's boot, verify-C2 §2)");
                credential = Settings.DefaultSuperCredential;
            }
            string password = settings.SuperPassword;
            if (!Settings.IsCmdArgSafe(password) || ArgsFile.ValidateValue(password) != null)
            {
                log.Error("settings.superPassword is empty or contains spaces or '-'; using the default");
                password = Settings.DefaultSuperPassword;
            }

            var args = new List<ArgEntry>
            {
                new ArgEntry("ServerLocation", "Embedded"),
                new ArgEntry("AnubisCommand", "QuickLaunch"),
                new ArgEntry("EnvironmentSwitcherUrl", EnvironmentSwitcherUrl(port)),
                new ArgEntry("SuperCredential", credential),
                new ArgEntry("SuperPassword", password),
                new ArgEntry("DeviceId", deviceId),
            };
            if (settings.LanMode)
            {
                // Lobby/Controller listen on every interface so LAN guests can reach them (verify-C6 §2.1, §5 cfg).
                args.Add(new ArgEntry("LocalAnubisHost", "0.0.0.0"));
            }
            return args;
        }

        /// <summary>The game always reaches its own embedded server over loopback, LAN host or not.</summary>
        public static string EnvironmentSwitcherUrl(int port)
        {
            return "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
        }

        /// <summary>"type:name": exactly one ':' with both sides non-empty, and safe for args.txt and cmd_args.</summary>
        public static bool IsValidSuperCredential(string credential)
        {
            if (!Settings.IsCmdArgSafe(credential) || ArgsFile.ValidateValue(credential) != null) return false;
            int colon = credential.IndexOf(':');
            return colon > 0 && colon < credential.Length - 1 && credential.IndexOf(':', colon + 1) < 0;
        }
    }
}
