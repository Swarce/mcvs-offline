// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded.Notifications
{
    /// <summary>
    /// Asks for POST_NOTIFICATIONS once, at the first main menu (<see cref="NotificationPermission"/>): a background thread
    /// polls every few seconds whether the client World plays its main-menu level (read on the game thread), waits until it
    /// has been there for three polls in a row with the settings page closed (the language prompt and the age gate of a first
    /// start come and go on that level too), then records <c>notifications.requestedAt</c> in settings.json FIRST (a crash in
    /// the dialog must not make it ask on every start) and shows the system dialog, with a watchdog lease while it is up
    /// (the game's activity pauses behind it). Gives up after an hour without a menu. Never throws.
    /// </summary>
    internal sealed class NotificationPrompt
    {
        public const int PollMs = 3000;
        public const int StablePolls = 3;
        public const int GiveUpMs = 60 * 60 * 1000;
        public const int DialogLeaseMs = 120000;
        public const string LeaseReason = "notification permission dialog";

        private readonly INotificationPlatform platform;
        private readonly Func<Settings> settings;
        private readonly Log log;
        private readonly object gate = new object();
        private int targetSdk = TargetNotRead;
        private const int TargetNotRead = -2;
        private bool? declared;
        private bool declaredRead;
        private volatile string outcome;

        public NotificationPrompt(INotificationPlatform platform, Func<Settings> settings, Log log)
        {
            this.platform = platform;
            this.settings = settings;
            this.log = log ?? Log.Null;
            AtMainMenu = () => null;
            BrowserOpen = () => false;
            Lease = (ms, reason) => { };
            Mark = s => this.log.Info(s);
            Sleep = ms => Thread.Sleep(ms);
            Now = () => DateTime.UtcNow;
            Ticks = () => Environment.TickCount;
        }

        /// <summary>True when the client World plays its main menu, false elsewhere, null unknown (read on the game thread).</summary>
        public Func<bool?> AtMainMenu { get; set; }

        /// <summary>The settings page (in-game browser) is in front.</summary>
        public Func<bool> BrowserOpen { get; set; }

        /// <summary>The watchdog lease (AdminHost.Lease).</summary>
        public Action<int, string> Lease { get; set; }

        /// <summary>A boot-trace milestone ("notifications: ...").</summary>
        public Action<string> Mark { get; set; }

        public Action<int> Sleep { get; set; }

        public Func<DateTime> Now { get; set; }

        public Func<int> Ticks { get; set; }

        /// <summary>What happened (null while it waits or before it ran).</summary>
        public string Outcome { get { return outcome; } }

        /// <summary>The decision with the current state (cached target and manifest, live grant).</summary>
        public NotificationDecision Decide()
        {
            if (platform == null) return NotificationDecision.Unknown;
            int sdk = platform.SdkInt;
            if (sdk > 0 && sdk < NotificationPermission.MinSdk) return NotificationDecision.NotNeeded;
            int target;
            bool? isDeclared;
            lock (gate)
            {
                if (targetSdk == TargetNotRead) targetSdk = SafeTarget();
                if (!declaredRead)
                {
                    declared = SafeDeclared();
                    declaredRead = true;
                }
                target = targetSdk;
                isDeclared = declared;
            }
            Settings s = SafeSettings();
            bool request = s == null || s.GetBool(NotificationPermission.RequestKey, true);
            string requestedAt = s == null ? null : s.GetString(NotificationPermission.RequestedAtKey, "");
            return NotificationPermission.Decide(sdk, target, isDeclared, SafeGranted(), request, requestedAt);
        }

        /// <summary>The settings page's row: the state now.</summary>
        public string Describe()
        {
            try
            {
                NotificationDecision d = Decide();
                Settings s = SafeSettings();
                string at = s == null ? null : s.GetString(NotificationPermission.RequestedAtKey, "");
                int sdk = platform == null ? 0 : platform.SdkInt;
                int target;
                lock (gate) target = targetSdk;
                return NotificationPermission.Describe(d, sdk, target, at);
            }
            catch (Exception)
            {
                return NotificationPermission.Describe(NotificationDecision.Unknown, 0, -1, null);
            }
        }

        /// <summary>Starts the background thread (only when there is something to ask); never throws.</summary>
        public void Start()
        {
            try
            {
                var thread = new Thread(Run);
                thread.IsBackground = true;
                thread.Name = "mcvs-notifications";
                thread.Start();
            }
            catch (Exception ex)
            {
                log.Warn("notifications: the permission prompt could not start", ex);
            }
        }

        /// <summary>The whole job on the calling thread (the thread body; tests call it directly).</summary>
        public void Run()
        {
            try
            {
                NotificationDecision first = Decide();
                if (first != NotificationDecision.Ask)
                {
                    Finish("notifications: no request (" + first + ": " + Describe() + ")", false);
                    return;
                }
                int started = Ticks();
                int stable = 0;
                while (Ticks() - started < GiveUpMs)
                {
                    Sleep(PollMs);
                    bool? menu = null;
                    try
                    {
                        menu = AtMainMenu();
                    }
                    catch (Exception)
                    {
                    }
                    if (menu != true || SafeBrowserOpen())
                    {
                        stable = 0;
                        continue;
                    }
                    if (++stable < StablePolls) continue;
                    NotificationDecision now = Decide();
                    if (now != NotificationDecision.Ask)
                    {
                        Finish("notifications: no request at the main menu (" + now + ": " + Describe() + ")", false);
                        return;
                    }
                    Ask();
                    return;
                }
                Finish("notifications: no main menu within " + (GiveUpMs / 60000).ToString(CultureInfo.InvariantCulture) + " minutes; not asked", false);
            }
            catch (Exception ex)
            {
                Finish("notifications: the permission prompt failed (" + ex.GetType().Name + ": " + ex.Message + ")", true);
            }
        }

        private void Ask()
        {
            Settings s = SafeSettings();
            string stamp = Now().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            try
            {
                if (s == null) throw new InvalidOperationException("no settings");
                s.SetAndSave(new Dictionary<string, object>(StringComparer.Ordinal) { { NotificationPermission.RequestedAtKey, stamp } });
            }
            catch (Exception ex)
            {
                Finish("notifications: settings.json cannot record the request (" + ex.Message + "); not asking, it would ask on every start", true);
                return;
            }
            Lease(DialogLeaseMs, LeaseReason);
            bool ok = false;
            try
            {
                ok = platform.Request();
            }
            catch (Exception)
            {
            }
            Finish(ok ? "notifications: asked once for the notification permission at the main menu (Android " +
                        platform.SdkInt.ToString(CultureInfo.InvariantCulture) + "); the answer shows on the settings page"
                      : "notifications: the permission request could not be shown (recorded as asked; allow notifications in Android's app settings)",
                   !ok);
        }

        private void Finish(string text, bool warn)
        {
            outcome = text;
            try
            {
                if (warn) log.Warn(text);
                else Mark(text);
            }
            catch (Exception)
            {
            }
        }

        private Settings SafeSettings()
        {
            try
            {
                return settings == null ? null : settings();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private bool SafeBrowserOpen()
        {
            try
            {
                return BrowserOpen();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private int SafeTarget()
        {
            try
            {
                return platform.TargetSdk();
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private bool? SafeDeclared()
        {
            try
            {
                return platform.Declared();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private bool? SafeGranted()
        {
            try
            {
                return platform.Granted();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
