// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests (the device side is INotificationPlatform).
using System;

namespace McvsOffline.Embedded.Notifications
{
    /// <summary>What the notification-permission request needs from the device (Android: AndroidNotifications over AndroidJni).</summary>
    internal interface INotificationPlatform
    {
        /// <summary>Build.VERSION.SDK_INT (0 unknown).</summary>
        int SdkInt { get; }

        /// <summary>The app's ApplicationInfo.targetSdkVersion, or -1 when it cannot be read.</summary>
        int TargetSdk();

        /// <summary>POST_NOTIFICATIONS is among the manifest's uses-permission entries (PackageInfo.requestedPermissions); null unknown.</summary>
        bool? Declared();

        /// <summary>Context.checkSelfPermission(POST_NOTIFICATIONS) == PERMISSION_GRANTED; null unknown.</summary>
        bool? Granted();

        /// <summary>Activity.requestPermissions([POST_NOTIFICATIONS], code) on the UI thread. True once it ran or is running.</summary>
        bool Request();
    }

    /// <summary>What to do about the notification permission now.</summary>
    internal enum NotificationDecision
    {
        /// <summary>Android 12 or older: there is no runtime permission (notifications work unless turned off in settings).</summary>
        NotNeeded,

        /// <summary>Already granted.</summary>
        Granted,

        /// <summary>settings.json notifications.request is false.</summary>
        Disabled,

        /// <summary>The build targets API 32 or lower: Android shows its own prompt when the game creates its first channel.</summary>
        TargetBelow33,

        /// <summary>The manifest does not declare POST_NOTIFICATIONS: a request would be denied at once, without a dialog.</summary>
        NotDeclared,

        /// <summary>Asked before (settings.json notifications.requestedAt): never again; the player changes it in Android's settings.</summary>
        AlreadyAsked,

        /// <summary>The state cannot be read (JNI members missing): do nothing.</summary>
        Unknown,

        /// <summary>Show the system dialog now (once).</summary>
        Ask,
    }

    /// <summary>
    /// The one-time POST_NOTIFICATIONS request (docs/research/target-sdk.md §4.4, WP-T5). From Android 13 (API 33) an app that
    /// targets 33 or later must request the permission itself, or the game's local reminders and its data-download notification
    /// are silently dropped. The host asks once, at the first main menu (<see cref="NotificationPrompt"/>), when the device is on
    /// 33+, the build targets 33+ and declares the permission (the manifest change is the 'targetsdk' patch's), the permission is
    /// not granted, and it was never asked before. The game's MainActivity has no onRequestPermissionsResult, so the answer is
    /// not reported to the game: it is read back with checkSelfPermission (the status row of the settings page).
    /// </summary>
    internal static class NotificationPermission
    {
        public const string Permission = "android.permission.POST_NOTIFICATIONS";

        /// <summary>Build.VERSION_CODES.TIRAMISU (Android 13): the first API level with the runtime permission.</summary>
        public const int MinSdk = 33;

        /// <summary>The request code passed to requestPermissions (nothing reads it).</summary>
        public const int RequestCode = 0x4d43;

        public const string RequestKey = "notifications.request";
        public const string RequestedAtKey = "notifications.requestedAt";

        public static NotificationDecision Decide(int sdkInt, int targetSdk, bool? declared, bool? granted, bool requestEnabled, string requestedAt)
        {
            if (sdkInt > 0 && sdkInt < MinSdk) return NotificationDecision.NotNeeded;
            if (sdkInt <= 0) return NotificationDecision.Unknown;
            if (granted == true) return NotificationDecision.Granted;
            if (!requestEnabled) return NotificationDecision.Disabled;
            if (targetSdk >= 0 && targetSdk < MinSdk) return NotificationDecision.TargetBelow33;
            if (declared == false) return NotificationDecision.NotDeclared;
            if (!string.IsNullOrEmpty(requestedAt)) return NotificationDecision.AlreadyAsked;
            if (granted == null || targetSdk < 0 || declared == null) return NotificationDecision.Unknown;
            return NotificationDecision.Ask;
        }

        /// <summary>The settings page's status row (one sentence).</summary>
        public static string Describe(NotificationDecision decision, int sdkInt, int targetSdk, string requestedAt)
        {
            switch (decision)
            {
                case NotificationDecision.NotNeeded:
                    return "not needed on Android " + AndroidVersion(sdkInt) + " (allowed unless turned off in Android's app settings)";
                case NotificationDecision.Granted:
                    return "allowed";
                case NotificationDecision.Disabled:
                    return "not allowed; asking is off (settings notifications.request)";
                case NotificationDecision.TargetBelow33:
                    return "not allowed yet; this build targets API " + targetSdk +
                           ", so Android asks by itself when the game first creates a notification channel";
                case NotificationDecision.NotDeclared:
                    return "not allowed; this build does not declare the permission (a build with the 'targetsdk' patch does)";
                case NotificationDecision.AlreadyAsked:
                    return "not allowed (asked once" + (string.IsNullOrEmpty(requestedAt) ? "" : " on " + requestedAt) +
                           "); reminders stay off until you allow notifications in Android's app settings";
                case NotificationDecision.Ask:
                    return "not allowed yet; the game asks once at the main menu";
                default:
                    return "unknown (the permission state cannot be read)";
            }
        }

        private static string AndroidVersion(int sdkInt)
        {
            switch (sdkInt)
            {
                case 29: return "10";
                case 30: return "11";
                case 31: return "12";
                case 32: return "12L";
                default: return sdkInt < 29 ? "9 or older" : "API " + sdkInt;
            }
        }
    }
}
