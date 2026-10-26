// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using McvsOffline.Embedded.Admin;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline
{
    /// <summary>
    /// Entry points the patched game code calls for the settings page (design §8.1, §9): patch <c>adminpage</c> (Game.dll:
    /// <c>PlayerProfileComponent.GetAccountMenuButtonList</c> and <c>ProcessAccountMenuButtonAction</c>), <c>adminlink</c>
    /// (Gameloft.Engine.dll: <c>BrowserComponent.OpenUrl</c>) and <c>forum</c> (Game.dll: <c>UIComponent.GoToForum</c>). Their
    /// names and signatures are the patch contract (the patcher imports them by name and parameter types). They run on the game
    /// thread inside Flash native calls with no exception handler around them: they catch everything, never block, and leave
    /// the game's behaviour unchanged when the page is off or the offline server is not running.
    /// </summary>
    public static class AdminHooks
    {
        public const string ButtonAction = "mcvs_offline";
        public const string ButtonLabel = "MCVS Offline";
        public const string ButtonIcon = "Menus/assets/png/icons/Settings.png";

        private static volatile AdminHost host;

        /// <summary>Set by the embedded host once the offline server listens (null before, and when it could not start).</summary>
        internal static AdminHost Host
        {
            get { return host; }
            set { host = value; }
        }

        /// <summary>Game thread (Flash native call). Returns a List&lt;object&gt; (ASValue.SetObject turns an IList into an
        /// AS array; any other IEnumerable would become an opaque managed object). Never throws.</summary>
        public static IEnumerable<object> AccountMenuButtons(IEnumerable<object> buttons)
        {
            try
            {
                AdminHost h = host;
                if (h == null || !h.PortalEnabled) return buttons;
                // An IDictionary becomes a real AS object (the game's own entries are anonymous types with the same names).
                var entry = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "customAction", ButtonAction },
                    { "isClickable", true },
                    { "label", ButtonLabel },
                    { "icon", ButtonIcon },
                };
                var list = buttons as List<object>;
                if (list == null)
                {
                    list = new List<object>();
                    if (buttons != null)
                    {
                        foreach (object button in buttons) list.Add(button);
                    }
                }
                list.Add(entry);   // last: after "Recover progress"
                return list;
            }
            catch (Exception)
            {
                return buttons;
            }
        }

        /// <summary>Game thread. world: the caller's Gameloft.Engine.World (as object), may be null. Never throws.</summary>
        public static void OpenPage(string trigger, object world)
        {
            try
            {
                AdminHost h = host;
                if (h != null) h.Opener.OpenFromGame(trigger, world);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Game thread, first thing in BrowserComponent.OpenUrl (patch 'adminlink'). browserComponent: the
        /// Gameloft.Engine.BrowserComponent (as object). True: url was the page marker and was handled (the original
        /// body is skipped). False for every other url, when the portal is disabled or absent, and on any error.</summary>
        public static bool OpenIfPageMarker(string url, object browserComponent)
        {
            try
            {
                if (url == null) return false;
                AdminHost h = host;
                if (h == null) return false;
                IAdminPortal portal = h.Portal;
                // Runs for every URL the game opens: an ordinal compare first (IsPageMarker reads admin.enabled only on a match).
                if (portal == null || !portal.IsPageMarker(url)) return false;
                object world = GameReflection.WorldOf(browserComponent);
                if (world == null) return false;   // not a component of a World: left to the original body
                // A server World: the original body returns there too (BrowserComponent.OpenUrl: IsServer != false).
                if (GameReflection.IsServer(world) != false) return true;
                h.Opener.OpenFromGame(AdminTriggers.News, world);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
