// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Embedded.Admin
{
    /// <summary>
    /// Opens the settings page in the game's own in-game browser (design §8.3, D1): the game stays the foreground app, so the
    /// cached-apps freezer never stops it, and the page's one-time ticket reaches only the game's WebView.
    /// <list type="bullet">
    /// <item><see cref="OpenFromServer"/> (<c>IAdminHost.OpenPage</c>: the redeem code, the reopen after "Save the game now";
    /// a server thread): posts to the game thread and opens there with the client World. True once posted.</item>
    /// <item><see cref="OpenFromGame"/> (the Settings › Account button, the inbox marker, the Forum button; already on the game
    /// thread): reads the game's credential, mints the URL through <see cref="IAdminPortal"/> and opens at once.</item>
    /// <item>By <c>admin.openVia</c> (server settings, read per open): <c>auto</c> = <c>World.Browser.OpenUrl(url, true)</c> when
    /// the native browser is initialised, else JNI <c>InGameBrowser.SetParameters("EN")</c> + <c>OpenURL(url, true)</c>;
    /// <c>game</c> and <c>jni</c> only that path; <c>post</c> = JNI <c>OpenURLWithPostData("http://127.0.0.1:&lt;port&gt;/_mcvs/ui",
    /// "t=&lt;ticket&gt;[&amp;s=&lt;screen&gt;][&amp;r=nosave]")</c> (the WebView's form POST; the server answers 303 with the cookie).</item>
    /// </list>
    /// Every open starts the watchdog lease and logs <c>admin: page opened (trigger account, via game|jni|post)</c>; the URL
    /// (it carries the ticket) is never logged. Nothing here throws, and the game thread never waits.
    /// <para>
    /// A platform that opens the page itself (<see cref="IPagePlatform"/>, Windows: the Steam overlay or the system browser,
    /// docs/design/pc-port.md §6.5) gets every <c>admin.openVia</c> but <c>game</c>, and the log says <c>via overlay|system</c>. The
    /// overlay gives no success signal, so the page's own requests (<see cref="PageAlive"/>) are the proof: when the previous
    /// open went to the overlay and no PageAlive came within <see cref="NoPageMs"/>, the next <c>auto</c> open takes the system
    /// browser and the line adds <c>, overlay showed no page last time</c>. The trigger <c>browser</c> (the page's "Open in the
    /// system browser") always takes it.
    /// </para>
    /// </summary>
    internal sealed class PageOpener
    {
        /// <summary>How long after an overlay open the page has to show it loaded (a PageAlive) before the next open avoids it.</summary>
        public const int NoPageMs = 20000;

        public const string BrowserTrigger = "browser";

        private readonly AdminHost host;
        private readonly object gate = new object();
        private bool overlayPending;    // the last open went to the overlay and no PageAlive came since (under gate)
        private int overlayOpenedAt;    // Clock() of that open (under gate)

        public PageOpener(AdminHost host)
        {
            this.host = host;
            Clock = () => Environment.TickCount;
        }

        /// <summary>Environment.TickCount (tests move it).</summary>
        public Func<int> Clock { get; set; }

        /// <summary>The page loaded with its session (any of its requests): the overlay did show it.</summary>
        public void PageAlive()
        {
            lock (gate) overlayPending = false;
        }

        /// <summary>True when the previous open went to the overlay and no PageAlive came within <see cref="NoPageMs"/> of it.</summary>
        private bool OverlayShowedNoPage()
        {
            lock (gate) return overlayPending && Clock() - overlayOpenedAt >= NoPageMs;
        }

        private void Opened(string route)
        {
            lock (gate)
            {
                overlayPending = route == "overlay";
                if (overlayPending) overlayOpenedAt = Clock();
            }
        }

        /// <summary>Server thread: open <paramref name="url"/> (a minted page URL) on the game thread. True once posted or opened.</summary>
        public bool OpenFromServer(string url, string trigger)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return false;
                string name = AdminHost.SafeReason(trigger);
                bool posted = host.Game.Post(() =>
                {
                    object world = host.Game.ClientWorld();
                    host.ObserveWorld(world);
                    OpenWithWorld(world, url, name);
                }, host.Log);
                if (posted) return true;
                // No game thread to post to (GameApplication unknown): only the JNI paths can work, from here.
                if (host.OpenVia == "game") return false;
                return OpenWithWorld(null, url, name);
            }
            catch (Exception ex)
            {
                host.Log.Warn("admin: opening the page failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Game thread (a hook): mint a page URL and open it with <paramref name="world"/> (the caller's World); a null World is
        /// opened through the game thread's queue with the client World. False: disabled, or nothing could open it.
        /// </summary>
        public bool OpenFromGame(string trigger, object world)
        {
            try
            {
                string name = AdminHost.SafeReason(trigger);
                IAdminPortal portal = host.Portal;
                if (portal == null || !portal.Enabled)
                {
                    host.Log.Info("admin: the page is turned off (admin.enabled) or not available; not opened (trigger " + name + ")");
                    return false;
                }
                string url = portal.MintPageUrl(name, null);
                if (url == null) return false;
                if (world == null) return OpenFromServer(url, name);
                host.ObserveWorld(world);
                return OpenWithWorld(world, url, name);
            }
            catch (Exception ex)
            {
                host.Log.Warn("admin: opening the page failed", ex);
                return false;
            }
        }

        /// <summary>Game thread (or any thread with a null World: JNI only). Opens by admin.openVia; false when nothing worked.</summary>
        public bool OpenWithWorld(object world, string url, string trigger)
        {
            string via = host.OpenVia;
            // Lease first: the browser activity pauses the game loop as soon as it starts.
            host.Lease(AdminHost.PageLeaseMs, AdminHost.PageLeaseReason);
            string used = null;
            string note = "";
            var page = host.Platform as IPagePlatform;
            try
            {
                if (page != null && (via != "game" || trigger == BrowserTrigger))
                {
                    string mode = via;
                    if (trigger == BrowserTrigger)
                    {
                        mode = "system";
                    }
                    else if (via == "auto" && OverlayShowedNoPage())
                    {
                        mode = "system";
                        note = ", overlay showed no page last time";
                    }
                    used = page.Open(url, mode);
                    if (used != null) Opened(used);
                }
                else switch (via)
                {
                    case "game":
                        if (OpenManaged(world, url)) used = "game";
                        break;
                    case "jni":
                        if (OpenJni(url)) used = "jni";
                        break;
                    case "post":
                        if (OpenPost(url)) used = "post";
                        break;
                    default:
                        if (world != null && GameReflection.IsInGameBrowserInitialized(GameReflection.Browser(world)) == true && OpenManaged(world, url))
                            used = "game";
                        else if (OpenJni(url))
                            used = "jni";
                        break;
                }
            }
            catch (Exception ex)
            {
                host.Log.Warn("admin: opening the in-game browser failed (openVia " + via + ")", ex);
            }
            if (used == null)
            {
                host.Lease(0, null);
                host.Log.Warn("admin: the page could not be opened (trigger " + trigger + ", openVia " + via + ")");
                return false;
            }
            if (used == "game") Opened(used);
            host.Log.Info("admin: page opened (trigger " + trigger + ", via " + used + note + ")");
            return true;
        }

        private bool OpenManaged(object world, string url)
        {
            if (world == null) return false;
            try
            {
                return GameReflection.OpenUrl(GameReflection.Browser(world), url);
            }
            catch (Exception ex)
            {
                host.Log.Warn("admin: BrowserComponent.OpenUrl failed", ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex);
                return false;
            }
        }

        private bool OpenJni(string url)
        {
            IAdminPlatform platform = host.Platform;
            return platform != null && platform.OpenInGameBrowserJni(url);
        }

        /// <summary>post mode: the page URL without its fragment (and trailing '/'), and "t=&lt;ticket&gt;" from the fragment.</summary>
        private bool OpenPost(string url)
        {
            IAdminPlatform platform = host.Platform;
            string target, form;
            if (platform == null || !SplitTicket(url, out target, out form)) return false;
            return platform.OpenInGameBrowserWithPostData(target, form);
        }

        /// <summary>
        /// "http://127.0.0.1:51780/_mcvs/ui/#t=&lt;32 hex&gt;&amp;s=saves&amp;r=nosave" → "http://127.0.0.1:51780/_mcvs/ui" and
        /// "t=&lt;32 hex&gt;&amp;s=saves&amp;r=nosave": the ticket and the fragment's screen (s=) and result (r=) fields as form
        /// data (the server checks them as the page checks the fragment and hands them back in its redirect, so a reopen after
        /// "Save the game now" lands on its screen with its toast in post mode too).
        /// </summary>
        internal static bool SplitTicket(string url, out string target, out string form)
        {
            target = null;
            form = null;
            if (url == null) return false;
            int hash = url.IndexOf("#t=", StringComparison.Ordinal);
            if (hash <= 0) return false;
            string[] fields = url.Substring(hash + 1).Split('&');
            string ticket = fields[0].Substring(2);
            if (ticket.Length == 0) return false;
            var data = new System.Text.StringBuilder("t=").Append(ticket);
            for (int i = 1; i < fields.Length; i++)
            {
                if (fields[i].StartsWith("s=", StringComparison.Ordinal) || fields[i].StartsWith("r=", StringComparison.Ordinal))
                    data.Append('&').Append(fields[i]);
            }
            target = url.Substring(0, hash).TrimEnd('/');
            form = data.ToString();
            return true;
        }
    }
}
