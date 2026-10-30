// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (reflection on Gameloft.Engine and Game): compiled into the embedded DLL and the net8 tests.
using System;

namespace McvsOffline.Embedded.Admin.SettingsMenu
{
    /// <summary>
    /// The runtime native registry of the Settings menu (docs/design/settings-menu.md §3.1, skeptic E4, E5): our own delegates on
    /// the game's Flash natives, no IL. Every 2 s a <c>System.Threading.Timer</c> posts <see cref="Ensure"/> to the game thread, which
    /// registers each delegate on the client World's UIComponent with <c>OverrideNativeFunction(name, ours, true, true)</c>: it
    /// removes only our earlier delegate (same Target; every name has its own target object) and appends again, so ours stays last
    /// and comes back after the game re-registers or removes the name (UIComponent.cs:402, :416, :1449, :1578;
    /// GameClientComponent.cs:3815-3816, :3878; GamePreferencesComponent.cs:146-151, :167-173). Natives:
    /// <list type="table">
    /// <item><term>EnterSection</term><description>append, returns nothing; both platforms. On <c>settings_main_screen</c>: clear
    /// <see cref="SettingsMenuState.TileActive"/> and the Performance mode, post the grid step, show the restart prompt when one is
    /// pending.</description></item>
    /// <item><term>GoToForum</term><description><b>replace</b> (true, false), Android: while the tile is active, the page opens
    /// (<see cref="PerformancePage.Open"/>); otherwise our delegate calls <c>UIComponent.GoToForum()</c> itself, so the stock body and
    /// patch 'forum' run as before (E4).</description></item>
    /// <item><term>CanChangeGraphicSetting, CanChangeMaxFramerate</term><description>append, boxed false or null; Android
    /// (<see cref="PerformancePage"/>, R4 and the page).</description></item>
    /// <item><term>SetUserPreference, SaveUserPreferences</term><description>append, nothing returned; Android (the page's
    /// writes).</description></item>
    /// </list>
    /// Rules (pc-port P4): every delegate catches everything and never blocks, reads its settings on every call, and does nothing
    /// while the host function returns null (AdminHooks.Host before the server is up). On PC only EnterSection is registered.
    /// WP-A of the design; the bodies are filled in there.
    /// </summary>
    internal sealed class UiNatives : IDisposable
    {
        /// <summary>How often the registrations are ensured (and how late a game re-registration is repaired at worst).</summary>
        public const int EnsureIntervalMs = 2000;

        /// <summary>The natives Android registers, in registration order; PC registers only <see cref="SettingsMenuNames.EnterSection"/>.</summary>
        public static readonly string[] AndroidNames =
        {
            SettingsMenuNames.EnterSection, SettingsMenuNames.GoToForum, SettingsMenuNames.CanChangeGraphicSetting,
            SettingsMenuNames.CanChangeMaxFramerate, SettingsMenuNames.SetUserPreference, SettingsMenuNames.SaveUserPreferences,
        };

        private static volatile UiNatives current;

        private readonly Func<ISettingsMenuHost> host;
        private readonly SettingsMenuState state;

        /// <summary>The registry of this process (after <see cref="Start"/>), or null.</summary>
        public static UiNatives Current { get { return current; } }

        /// <summary>
        /// EmbeddedHost, right after <c>AdminHooks.Host = admin</c>: a registry over <c>() =&gt; AdminHooks.Host</c> with its timer
        /// running. Never throws; null when it cannot start (it then logs one line, and the game's menus stay stock).
        /// </summary>
        public static UiNatives Start(AdminHost admin)
        {
            try
            {
                if (admin == null) return null;
                var natives = new UiNatives(() => AdminHooks.Host, new SettingsMenuState());
                natives.StartTimer();
                current = natives;
                return natives;
            }
            catch (Exception ex)
            {
                try
                {
                    admin.Log.Info("settings menu: natives not started (" + ex.GetType().Name + ")");
                }
                catch (Exception)
                {
                }
                return null;
            }
        }

        /// <summary>A registry reading the host through <paramref name="host"/> on every call (tests: a fake host and their own state).</summary>
        public UiNatives(Func<ISettingsMenuHost> host, SettingsMenuState state)
        {
            if (host == null) throw new ArgumentNullException("host");
            if (state == null) throw new ArgumentNullException("state");
            this.host = host;
            this.state = state;
        }

        public SettingsMenuState State { get { return state; } }

        /// <summary>The host now, or null (read on every call).</summary>
        internal ISettingsMenuHost Host
        {
            get
            {
                try
                {
                    return host();
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>Starts the timer that posts <see cref="Ensure"/> every <see cref="EnsureIntervalMs"/>; the first post at once.</summary>
        public void StartTimer()
        {
            throw new NotImplementedException("WP-A: settings-menu.md §3.1");
        }

        /// <summary>
        /// Game thread: one re-ensure of every registration on <see cref="ISettingsMenuHost.ClientUi"/> (T-A1, T-A2). Counts a
        /// registration that had to be put back (<see cref="SettingsMenuState.CountRegistration"/>). Never throws.
        /// </summary>
        public void Ensure()
        {
            throw new NotImplementedException("WP-A: settings-menu.md §3.1");
        }

        // ------------------------------------------------------------------ the delegates' bodies (each registered on its own target)

        /// <summary>EnterSection(section), appended: the grid on <c>settings_main_screen</c>. Never throws.</summary>
        public void EnterSection(string section)
        {
            throw new NotImplementedException("WP-A: settings-menu.md §3.1");
        }

        /// <summary>GoToForum(), replacing the game's: the page while the tile is active, else <c>UIComponent.GoToForum()</c> (E4). Never throws.</summary>
        public void GoToForum()
        {
            throw new NotImplementedException("WP-A: settings-menu.md §3.1, skeptic E4");
        }

        /// <summary>CanChangeGraphicSetting(), appended: <see cref="PerformancePage.CanChangeGraphicSetting"/>. Never throws.</summary>
        public object CanChangeGraphicSetting()
        {
            throw new NotImplementedException("WP-A: settings-menu.md §3.1");
        }

        /// <summary>CanChangeMaxFramerate(), appended: <see cref="PerformancePage.CanChangeMaxFramerate"/>. Never throws.</summary>
        public object CanChangeMaxFramerate()
        {
            throw new NotImplementedException("WP-A: settings-menu.md §3.1");
        }

        /// <summary>SetUserPreference(key, value), appended: <see cref="PerformancePage.OnSetUserPreference"/>. Never throws.</summary>
        public void SetUserPreference(string key, object value)
        {
            throw new NotImplementedException("WP-A: settings-menu.md §3.1");
        }

        /// <summary>SaveUserPreferences(), appended: <see cref="PerformancePage.OnSaveUserPreferences"/>. Never throws.</summary>
        public void SaveUserPreferences()
        {
            throw new NotImplementedException("WP-A: settings-menu.md §3.1");
        }

        /// <summary>Stops the timer; the delegates stay registered until the game drops them (they do nothing without a host).</summary>
        public void Dispose()
        {
            if (ReferenceEquals(current, this)) current = null;
        }
    }
}
