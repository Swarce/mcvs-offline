// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Android only: compiled into the embedded DLL (MCVS_ANDROID), never into the net8 build.
#if MCVS_ANDROID
using System;
using Android.Runtime;
using McvsOffline.Embedded.Notifications;

namespace McvsOffline.Embedded.Android
{
    /// <summary>
    /// <see cref="INotificationPlatform"/> over the JNI table (group <see cref="AndroidJni.Notify"/>, resolved on Android 13+ only):
    /// <c>ApplicationInfo.targetSdkVersion</c>, <c>PackageInfo.requestedPermissions</c> (GET_PERMISSIONS), <c>checkSelfPermission</c>
    /// on the application context, and <c>requestPermissions</c> on the game's activity (<c>SUtils.getActivity()</c>) on the UI
    /// thread. Every member catches everything: a miss answers "unknown" and nothing is asked.
    /// </summary>
    internal static class AndroidNotifications
    {
        public static int TargetSdk()
        {
            if (!AndroidJni.Has(AndroidJni.GetApplicationInfo) || !AndroidJni.Has(AndroidJni.AppInfoTargetSdk)) return -1;
            IntPtr info = IntPtr.Zero;
            try
            {
                info = AndroidJni.CallObject(AndroidJni.AppContext(), AndroidJni.GetApplicationInfo);
                return info == IntPtr.Zero ? -1 : AndroidJni.GetInt(info, AndroidJni.AppInfoTargetSdk);
            }
            catch (Exception)
            {
                return -1;
            }
            finally
            {
                AndroidJni.Delete(info);
            }
        }

        public static bool? Declared()
        {
            if (!AndroidJni.Has(AndroidJni.GetPackageManager) || !AndroidJni.Has(AndroidJni.GetPackageName) ||
                !AndroidJni.Has(AndroidJni.GetPackageInfo) || !AndroidJni.Has(AndroidJni.PackageInfoPermissions)) return null;
            IntPtr pm = IntPtr.Zero, name = IntPtr.Zero, info = IntPtr.Zero, permissions = IntPtr.Zero;
            try
            {
                IntPtr context = AndroidJni.AppContext();
                pm = AndroidJni.CallObject(context, AndroidJni.GetPackageManager);
                name = AndroidJni.CallObject(context, AndroidJni.GetPackageName);
                if (pm == IntPtr.Zero || name == IntPtr.Zero) return null;
                info = AndroidJni.CallObject(pm, AndroidJni.GetPackageInfo, new JValue(name), new JValue(AndroidJni.GetPermissions));
                if (info == IntPtr.Zero) return null;
                permissions = AndroidJni.GetObject(info, AndroidJni.PackageInfoPermissions);
                if (permissions == IntPtr.Zero) return false;
                int count = JNIEnv.GetArrayLength(permissions);
                for (int i = 0; i < count; i++)
                {
                    IntPtr element = JNIEnv.GetObjectArrayElement(permissions, i);
                    string permission = AndroidJni.TakeString(element);
                    if (string.Equals(permission, AndroidJni.PostNotifications, StringComparison.Ordinal)) return true;
                }
                return false;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                AndroidJni.Delete(permissions);
                AndroidJni.Delete(info);
                AndroidJni.Delete(name);
                AndroidJni.Delete(pm);
            }
        }

        public static bool? Granted()
        {
            if (!AndroidJni.Has(AndroidJni.CheckSelfPermission)) return null;
            IntPtr permission = IntPtr.Zero;
            try
            {
                permission = AndroidJni.Str(AndroidJni.PostNotifications);
                return AndroidJni.CallInt(AndroidJni.AppContext(), AndroidJni.CheckSelfPermission, new JValue(permission)) == AndroidJni.PermissionGranted;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                AndroidJni.Delete(permission);
            }
        }

        /// <summary>requestPermissions on the UI thread; true when it ran (or started and had not ended within 2 s).</summary>
        public static bool Request(Action<string, Exception> warn)
        {
            if (!AndroidJni.Has(AndroidJni.RequestPermissions) || !AndroidJni.Has(AndroidJni.GetActivity)) return false;
            int state = AndroidJni.RunOnUiThread(() =>
            {
                IntPtr activity = IntPtr.Zero, array = IntPtr.Zero;
                try
                {
                    activity = AndroidJni.CallStaticObject(AndroidJni.GetActivity);
                    if (activity == IntPtr.Zero) throw new InvalidOperationException("SUtils.getActivity() returned null");
                    array = JNIEnv.NewArray(new[] { AndroidJni.PostNotifications });
                    AndroidJni.CallVoid(activity, AndroidJni.RequestPermissions, new JValue(array), new JValue(NotificationPermission.RequestCode));
                }
                finally
                {
                    AndroidJni.Delete(array);
                    AndroidJni.Delete(activity);
                }
            }, 2000, ex => warn("notifications: requestPermissions failed", ex));
            return state == AndroidJni.UiDone || state == AndroidJni.UiPending;
        }
    }
}
#endif
