using System;
using System.Globalization;
using McvsOffline.Server.Core;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>
    /// <see cref="IAdminPortal"/>: how hosts and other modules open the page (design §8.1). Page URLs always use
    /// 127.0.0.1 (the game's network_security_config allows cleartext only there, D3) and the port the server listens on.
    /// The one-time ticket travels only in the URL fragment, which never reaches the server, a log or a Referer. Thread-safe;
    /// never throws.
    /// </summary>
    internal sealed class AdminPortal : IAdminPortal
    {
        /// <summary>The screens a page URL may select (<c>&amp;s=</c>).</summary>
        public static readonly string[] Screens = { "status", "settings", "saves", "logs", "actions", "about" };

        private readonly AdminApi api;
        private readonly AdminSecurity security;
        private readonly object gate = new object();
        private int markerPort = -1;
        private string marker;

        public AdminPortal(AdminApi api, AdminSecurity security)
        {
            this.api = api;
            this.security = security;
        }

        public bool Enabled
        {
            get
            {
                try
                {
                    return api.Enabled;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public string PageMarkerUrl
        {
            get
            {
                int port = api.Context.Port;
                lock (gate)
                {
                    if (port != markerPort)
                    {
                        marker = PageBase(port) + "#news";
                        markerPort = port;
                    }
                    return marker;
                }
            }
        }

        public bool IsPageMarker(string url)
        {
            // Runs for every URL the game opens: compare first, read the setting only for the marker itself.
            if (url == null) return false;
            string m = PageMarkerUrl;
            return url.Length == m.Length && string.Equals(url, m, StringComparison.Ordinal) && Enabled;
        }

        public string MintPageUrl(string trigger, string screen)
        {
            return MintPageUrl(trigger, screen, false);
        }

        /// <summary>As <see cref="MintPageUrl(string, string)"/>; <paramref name="noSave"/> adds "&amp;r=nosave" (§4.5).</summary>
        public string MintPageUrl(string trigger, string screen, bool noSave)
        {
            try
            {
                if (!Enabled) return null;
                string name = TriggerName(trigger);
                string url = PageBase(api.Context.Port) + "#t=" + security.MintTicket(name);
                if (IsScreen(screen)) url += "&s=" + screen;
                if (noSave) url += "&r=nosave";
                api.Log.Debug("admin: page ticket minted (trigger " + name + ")");
                return url;
            }
            catch (Exception ex)
            {
                api.Log.Error("admin: could not mint a page ticket", ex);
                return null;
            }
        }

        public bool OpenPage(string trigger, string screen)
        {
            return OpenPage(trigger, screen, false);
        }

        /// <summary>Mints a page URL and asks the host to open it; false without a host, when disabled, or on failure.</summary>
        public bool OpenPage(string trigger, string screen, bool noSave)
        {
            try
            {
                IAdminHost host = api.Host;
                if (host == null || !Enabled) return false;
                string url = MintPageUrl(trigger, screen, noSave);
                if (url == null) return false;
                bool opened = host.OpenPage(url, TriggerName(trigger));
                api.Log.Info("admin: page " + (opened ? "opening" : "could not be opened") + " (trigger " + TriggerName(trigger) + ")");
                return opened;
            }
            catch (Exception ex)
            {
                api.Log.Error("admin: opening the page failed", ex);
                return false;
            }
        }

        public static bool IsScreen(string screen)
        {
            return screen != null && Array.IndexOf(Screens, screen) >= 0;
        }

        private static string PageBase(int port)
        {
            return "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/_mcvs/ui/";
        }

        /// <summary>A trigger name safe for logs and buckets: [a-z0-9-], at most 20 characters, "unknown" when empty.</summary>
        internal static string TriggerName(string trigger)
        {
            if (string.IsNullOrEmpty(trigger)) return "unknown";
            var chars = new char[Math.Min(trigger.Length, 20)];
            int n = 0;
            foreach (char c in trigger)
            {
                if (n == chars.Length) break;
                char lower = char.ToLowerInvariant(c);
                if ((lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9') || lower == '-') chars[n++] = lower;
            }
            return n == 0 ? "unknown" : new string(chars, 0, n);
        }
    }
}
