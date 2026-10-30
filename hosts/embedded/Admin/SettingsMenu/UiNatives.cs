// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (reflection on Gameloft.Engine and Game): compiled into the embedded DLL and the net8 tests.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

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
    /// <para>A re-ensure reads the UI's own table first (MenuComponent's private <c>nativeEventOrFunctions</c>, read only): a name
    /// whose list still ends with ours (GoToForum: holds only ours) is left alone, any other is registered again and counted
    /// (<see cref="SettingsMenuState.Registrations"/>, one log line per step that put any back). When the table cannot be read, every
    /// tick registers again, which the flags make harmless. A new client UI (another World) gets every name and one log line.</para>
    /// <para>The delegates hand their work to <see cref="SettingsMenuLayout"/> and <see cref="PerformancePage"/> through
    /// <see cref="Steps"/> (the tests swap single steps); a step that throws is logged once per native and the native answers null.
    /// While <see cref="SettingsMenuState.Transforming"/> is set, SetUserPreference and SaveUserPreferences reach no step at all (E1).</para>
    /// </summary>
    internal sealed class UiNatives : IDisposable
    {
        /// <summary>How often the registrations are ensured (and how late a game re-registration is repaired at worst).</summary>
        public const int EnsureIntervalMs = 2000;

        /// <summary>A posted re-ensure that has not run after this long no longer holds the next tick back (a lost post).</summary>
        public const int LostPostMs = 30000;

        /// <summary>The natives Android registers, in registration order; PC registers only <see cref="SettingsMenuNames.EnterSection"/>.</summary>
        public static readonly string[] AndroidNames =
        {
            SettingsMenuNames.EnterSection, SettingsMenuNames.GoToForum, SettingsMenuNames.CanChangeGraphicSetting,
            SettingsMenuNames.CanChangeMaxFramerate, SettingsMenuNames.SetUserPreference, SettingsMenuNames.SaveUserPreferences,
        };

        /// <summary>The natives PC registers: Steam keeps its own PERFORMANCE tile, page and Gameplay rows.</summary>
        public static readonly string[] PcNames = { SettingsMenuNames.EnterSection };

        /// <summary>MenuComponent's per-instance table (MenuComponent.cs:70, PC :65): name → NativeEventOrFunction with <c>Delegates</c>.</summary>
        internal const string TableField = "nativeEventOrFunctions";

        private static volatile UiNatives current;

        private readonly Func<ISettingsMenuHost> host;
        private readonly SettingsMenuState state;
        private readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        private readonly object gate = new object();
        private readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);
        private Timer timer;
        private volatile bool disposed;
        private int ensurePending;
        private int ensurePostedAt;
        private object registeredUi;
        private string lastEnsureProblem;

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
                // AdminHooks.Host while it is this host: a registry left from an earlier host (tests start several) does nothing.
                var natives = new UiNatives(() => ReferenceEquals(AdminHooks.Host, admin) ? admin : null, new SettingsMenuState());
                natives.StartTimer();
                UiNatives previous = current;
                current = natives;
                if (previous != null) previous.Dispose();
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
            Steps = new MenuSteps();
            foreach (string name in AndroidNames) slots[name] = new Slot(this, name);
        }

        public SettingsMenuState State { get { return state; } }

        /// <summary>Where the delegates' work goes (the other parts of the Settings menu code; the tests replace single steps).</summary>
        internal MenuSteps Steps { get; set; }

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

        /// <summary>The delegate registered for <paramref name="name"/> (its Target is that name's own object), or null for another name.</summary>
        internal Delegate DelegateOf(string name)
        {
            Slot slot;
            return name != null && slots.TryGetValue(name, out slot) ? slot.Delegate : null;
        }

        /// <summary>The UI the delegates were last registered on (game thread), or null.</summary>
        internal object RegisteredUi { get { return registeredUi; } }

        /// <summary>Starts the timer that posts <see cref="Ensure"/> every <see cref="EnsureIntervalMs"/>; the first post at once.</summary>
        public void StartTimer()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("UiNatives");
                if (timer != null) return;
                timer = new Timer(unused => Tick(), null, 0, EnsureIntervalMs);
            }
        }

        /// <summary>
        /// One timer tick (any thread): posts one <see cref="Ensure"/> to the game thread unless the previous one has not run yet
        /// (a stalled game thread gets one, not a pile; after <see cref="LostPostMs"/> it counts as lost). False when nothing was
        /// posted. Never throws.
        /// </summary>
        internal bool Tick()
        {
            try
            {
                if (disposed) return false;
                ISettingsMenuHost h = Host;
                if (h == null) return false;
                if (Interlocked.CompareExchange(ref ensurePending, 1, 0) != 0 &&
                    unchecked(Environment.TickCount - Interlocked.CompareExchange(ref ensurePostedAt, 0, 0)) < LostPostMs)
                    return false;
                Interlocked.Exchange(ref ensurePostedAt, Environment.TickCount);
                bool posted = false;
                try
                {
                    posted = h.Post(RunEnsure);
                }
                finally
                {
                    if (!posted) Interlocked.Exchange(ref ensurePending, 0);
                }
                return posted;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void RunEnsure()
        {
            Interlocked.Exchange(ref ensurePending, 0);
            Ensure();
        }

        /// <summary>
        /// Game thread: one re-ensure of every registration on <see cref="ISettingsMenuHost.ClientUi"/> (T-A1, T-A2). Counts a
        /// registration that had to be put back (<see cref="SettingsMenuState.CountRegistration"/>). Never throws.
        /// </summary>
        public void Ensure()
        {
            ISettingsMenuHost h = Host;
            if (h == null) return;
            try
            {
                object ui = h.ClientUi();
                if (ui == null) return;
                string[] names = h.IsWindows ? PcNames : AndroidNames;
                bool fresh = !ReferenceEquals(ui, registeredUi);
                var putBack = new List<string>();
                foreach (string name in names)
                {
                    Slot slot = slots[name];
                    bool replace = name == SettingsMenuNames.GoToForum;
                    IList<Delegate> list = Delegates(ui, name);
                    if (!fresh && list != null && InPlace(list, slot, replace)) continue;
                    // (true, true): only our own earlier delegate goes and ours is appended last; (true, false): the list is ours alone (E4).
                    FlashInterop.OverrideNative(ui, name, slot.Delegate, true, !replace);
                    slot.Ui = ui;
                    if (!fresh && list != null) putBack.Add(name);
                }
                lastEnsureProblem = null;
                if (fresh)
                {
                    registeredUi = ui;
                    h.Log.Info("settings menu: natives registered (" + string.Join(", ", names) + ") on " + Describe(ui));
                }
                else if (putBack.Count > 0)
                {
                    int count = state.CountRegistration();
                    h.Log.Info("settings menu: natives put back (" + string.Join(", ", putBack.ToArray()) + "; " + count + " so far)");
                }
            }
            catch (Exception ex)
            {
                try
                {
                    // One line per new reason, not one every 2 s.
                    string problem = Reason(ex);
                    if (problem != lastEnsureProblem)
                    {
                        lastEnsureProblem = problem;
                        h.Log.Info("settings menu: natives not registered (" + problem + ")");
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        // ------------------------------------------------------------------ the delegates' bodies (each registered on its own target)

        /// <summary>EnterSection(section), appended: the grid on <c>settings_main_screen</c>. Never throws.</summary>
        public void EnterSection(string section)
        {
            ISettingsMenuHost h = Host;
            if (h == null || !string.Equals(section, SettingsMenuNames.MainSettingsSection, StringComparison.Ordinal)) return;
            try
            {
                MenuSteps steps = Steps;
                // Every return to the grid: the tile reads FORUM until the grid step has re-dressed it, and the page has ended.
                state.TileActive = false;
                if (!Run(h, "EnterSection (page)", () => steps.LeaveMode(state))) state.PerformanceMode = false;
                Run(h, "EnterSection (grid)", () => steps.Schedule(h, state));
                Run(h, "EnterSection (restart prompt)", () => steps.ShowRestartPromptIfPending(h, state));
            }
            catch (Exception)
            {
            }
        }

        /// <summary>GoToForum(), replacing the game's: the page while the tile is active, else <c>UIComponent.GoToForum()</c> (E4). Never throws.</summary>
        public void GoToForum()
        {
            Forum(slots[SettingsMenuNames.GoToForum].Ui);
        }

        /// <summary>CanChangeGraphicSetting(), appended: <see cref="PerformancePage.CanChangeGraphicSetting"/>. Never throws.</summary>
        public object CanChangeGraphicSetting()
        {
            ISettingsMenuHost h = Host;
            if (h == null) return null;
            object answer = null;
            MenuSteps steps = Steps;
            if (!Run(h, SettingsMenuNames.CanChangeGraphicSetting, () => answer = steps.CanChangeGraphicSetting(h, state))) return null;
            return answer;
        }

        /// <summary>CanChangeMaxFramerate(), appended: <see cref="PerformancePage.CanChangeMaxFramerate"/>. Never throws.</summary>
        public object CanChangeMaxFramerate()
        {
            ISettingsMenuHost h = Host;
            if (h == null) return null;
            object answer = null;
            MenuSteps steps = Steps;
            if (!Run(h, SettingsMenuNames.CanChangeMaxFramerate, () => answer = steps.CanChangeMaxFramerate(h, state))) return null;
            return answer;
        }

        /// <summary>SetUserPreference(key, value), appended: <see cref="PerformancePage.OnSetUserPreference"/>. Never throws.</summary>
        public void SetUserPreference(string key, object value)
        {
            ISettingsMenuHost h = Host;
            // E1: the page's own row writes fire the menu's handlers inside our call; those store nothing.
            if (h == null || state.Transforming) return;
            MenuSteps steps = Steps;
            Run(h, SettingsMenuNames.SetUserPreference, () => steps.OnSetUserPreference(h, state, key, value));
        }

        /// <summary>SaveUserPreferences(), appended: <see cref="PerformancePage.OnSaveUserPreferences"/>. Never throws.</summary>
        public void SaveUserPreferences()
        {
            ISettingsMenuHost h = Host;
            if (h == null || state.Transforming) return;
            MenuSteps steps = Steps;
            Run(h, SettingsMenuNames.SaveUserPreferences, () => steps.OnSaveUserPreferences(h, state));
        }

        /// <summary>Stops the timer; the delegates stay registered until the game drops them (they do nothing without a host).</summary>
        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                if (timer != null) timer.Dispose();
                timer = null;
            }
            if (ReferenceEquals(current, this)) current = null;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>A tap on the tile of <paramref name="ui"/> (where the delegate is registered): the page, or the game's own GoToForum.</summary>
        private void Forum(object ui)
        {
            ISettingsMenuHost h = Host;
            try
            {
                if (h != null && !h.IsWindows && h.SettingsMenuPerformance && state.TileActive)
                {
                    MenuSteps steps = Steps;
                    Run(h, SettingsMenuNames.GoToForum, () => steps.Open(h, state));
                    return;
                }
                // FORUM as before: the game's body (Browser.ShowForum, or patch 'forum''s page), also without a host.
                object target = ui ?? (h == null ? null : h.ClientUi());
                if (target != null) FlashInterop.GoToForum(target);
            }
            catch (Exception ex)
            {
                Warn(h, SettingsMenuNames.GoToForum, ex);
            }
        }

        /// <summary>Runs one step; false (and one log line per native and process) when it threw.</summary>
        private bool Run(ISettingsMenuHost h, string native, Action step)
        {
            try
            {
                step();
                return true;
            }
            catch (Exception ex)
            {
                Warn(h, native, ex);
                return false;
            }
        }

        private void Warn(ISettingsMenuHost h, string native, Exception ex)
        {
            try
            {
                lock (gate)
                {
                    if (!warned.Add(native)) return;
                }
                if (h != null) h.Log.Warn("settings menu: " + native + " failed (" + Reason(ex) + "); later failures are not logged");
            }
            catch (Exception)
            {
            }
        }

        private static string Reason(Exception ex)
        {
            Exception cause = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            return cause.GetType().Name + ": " + cause.Message;
        }

        /// <summary>Ours is the last delegate and there only once (append), or the only one (replace).</summary>
        private static bool InPlace(IList<Delegate> list, Slot slot, bool replace)
        {
            if (list.Count == 0 || !ReferenceEquals(list[list.Count - 1].Target, slot)) return false;
            if (replace) return list.Count == 1;
            int ours = 0;
            foreach (Delegate d in list)
            {
                if (ReferenceEquals(d.Target, slot)) ours++;
            }
            return ours == 1;
        }

        /// <summary>
        /// A copy of the delegates the UI has on <paramref name="name"/> (empty when the name has no entry), or null when its table
        /// cannot be read: the field <see cref="TableField"/>, else the first instance field of the same shape (a string-keyed
        /// Dictionary whose values have a public <c>List&lt;Delegate&gt; Delegates</c> field). Read only, game thread.
        /// </summary>
        internal static IList<Delegate> Delegates(object ui, string name)
        {
            try
            {
                IDictionary table = Table(ui);
                if (table == null) return null;
                if (!table.Contains(name)) return new List<Delegate>();
                object entry = table[name];
                FieldInfo field = entry == null ? null : entry.GetType().GetField("Delegates", BindingFlags.Public | BindingFlags.Instance);
                var delegates = field == null ? null : field.GetValue(entry) as List<Delegate>;
                return delegates == null ? null : new List<Delegate>(delegates);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static IDictionary Table(object ui)
        {
            FieldInfo shaped = null;
            for (Type type = ui.GetType(); type != null; type = type.BaseType)
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (!IsTableShape(field.FieldType)) continue;
                    if (field.Name == TableField) return field.GetValue(ui) as IDictionary;
                    if (shaped == null) shaped = field;
                }
            }
            return shaped == null ? null : shaped.GetValue(ui) as IDictionary;
        }

        /// <summary>
        /// An IDictionary whose indexer takes a string and gives an X with a public instance field <c>List&lt;Delegate&gt; Delegates</c>
        /// (Dictionary&lt;string, NativeEventOrFunction&gt;). Read from the indexer: the game's trimmed mscorlib has
        /// Type.GetGenericTypeDefinition and GetGenericArguments as throwing stubs (EmbeddedDllResolutionTests).
        /// </summary>
        internal static bool IsTableShape(Type type)
        {
            if (type == null || !typeof(IDictionary).IsAssignableFrom(type)) return false;
            PropertyInfo item = type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            if (item == null) return false;
            ParameterInfo[] index = item.GetIndexParameters();
            if (index.Length != 1 || index[0].ParameterType != typeof(string)) return false;
            FieldInfo delegates = item.PropertyType.GetField("Delegates", BindingFlags.Public | BindingFlags.Instance);
            return delegates != null && delegates.FieldType == typeof(List<Delegate>);
        }

        /// <summary>"UIComponent (World.IsServer false)" for the registration line (E5: the client World's UI, never a server's).</summary>
        private static string Describe(object ui)
        {
            string text = ui.GetType().Name;
            try
            {
                bool? server = GameReflection.IsServer(GameReflection.WorldOf(ui));
                text += server.HasValue ? " (World.IsServer " + (server.Value ? "true" : "false") + ")" : " (World unknown)";
            }
            catch (Exception)
            {
            }
            return text;
        }

        /// <summary>
        /// One native's delegate target: an object of its own, so <c>OverrideNativeFunction(name, ours, true, true)</c> finds exactly
        /// our earlier delegate by Target. Each method has the native's own signature (the game's dispatch converts the AS arguments
        /// to the parameter types); the two Func&lt;object&gt; return null to leave the game's answer standing.
        /// </summary>
        private sealed class Slot
        {
            private readonly UiNatives owner;

            public Slot(UiNatives owner, string name)
            {
                this.owner = owner;
                switch (name)
                {
                    case SettingsMenuNames.EnterSection: Delegate = new Action<string>(EnterSection); break;
                    case SettingsMenuNames.GoToForum: Delegate = new Action(GoToForum); break;
                    case SettingsMenuNames.CanChangeGraphicSetting: Delegate = new Func<object>(CanChangeGraphicSetting); break;
                    case SettingsMenuNames.CanChangeMaxFramerate: Delegate = new Func<object>(CanChangeMaxFramerate); break;
                    case SettingsMenuNames.SetUserPreference: Delegate = new Action<string, object>(SetUserPreference); break;
                    case SettingsMenuNames.SaveUserPreferences: Delegate = new Action(SaveUserPreferences); break;
                    default: throw new ArgumentException("not a Settings menu native: " + name, "name");
                }
            }

            public Delegate Delegate { get; private set; }

            /// <summary>The UI this name was last registered on (game thread).</summary>
            public object Ui { get; set; }

            public void EnterSection(string section)
            {
                owner.EnterSection(section);
            }

            public void GoToForum()
            {
                owner.Forum(Ui);
            }

            public object CanChangeGraphicSetting()
            {
                return owner.CanChangeGraphicSetting();
            }

            public object CanChangeMaxFramerate()
            {
                return owner.CanChangeMaxFramerate();
            }

            public void SetUserPreference(string key, object value)
            {
                owner.SetUserPreference(key, value);
            }

            public void SaveUserPreferences()
            {
                owner.SaveUserPreferences();
            }
        }
    }

    /// <summary>
    /// The work the natives hand on: by default the other parts of the Settings menu code (the grid, <see cref="SettingsMenuLayout"/>;
    /// the page, R4 and the prompt, <see cref="PerformancePage"/>). One instance per registry; the tests replace single members.
    /// </summary>
    internal sealed class MenuSteps
    {
        public MenuSteps()
        {
            Schedule = SettingsMenuLayout.Schedule;
            LeaveMode = PerformancePage.LeaveMode;
            ShowRestartPromptIfPending = PerformancePage.ShowRestartPromptIfPending;
            Open = PerformancePage.Open;
            CanChangeGraphicSetting = PerformancePage.CanChangeGraphicSetting;
            CanChangeMaxFramerate = PerformancePage.CanChangeMaxFramerate;
            OnSetUserPreference = PerformancePage.OnSetUserPreference;
            OnSaveUserPreferences = PerformancePage.OnSaveUserPreferences;
        }

        /// <summary>EnterSection("settings_main_screen"): post the grid step.</summary>
        public Action<ISettingsMenuHost, SettingsMenuState> Schedule { get; set; }

        /// <summary>EnterSection("settings_main_screen"): the page has ended.</summary>
        public Action<SettingsMenuState> LeaveMode { get; set; }

        /// <summary>EnterSection("settings_main_screen"): the restart prompt, once, when one is pending.</summary>
        public Action<ISettingsMenuHost, SettingsMenuState> ShowRestartPromptIfPending { get; set; }

        /// <summary>GoToForum with the tile active: the page.</summary>
        public Action<ISettingsMenuHost, SettingsMenuState> Open { get; set; }

        public Func<ISettingsMenuHost, SettingsMenuState, object> CanChangeGraphicSetting { get; set; }

        public Func<ISettingsMenuHost, SettingsMenuState, object> CanChangeMaxFramerate { get; set; }

        public Action<ISettingsMenuHost, SettingsMenuState, string, object> OnSetUserPreference { get; set; }

        public Action<ISettingsMenuHost, SettingsMenuState> OnSaveUserPreferences { get; set; }
    }
}
