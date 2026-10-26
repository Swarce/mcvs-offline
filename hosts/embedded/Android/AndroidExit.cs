// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Android only: compiled into the embedded DLL (MCVS_EMBEDDED), never into the net8 build.
#if MCVS_EMBEDDED
using System;
using System.Threading;
using Android.Runtime;

namespace McvsOffline.Embedded.Android
{
    /// <summary>
    /// The two ways the settings page ends the game for a restart (design §8.8). AdminHost decides which and in what order
    /// (closing a browser still shown first, the 3 s fallback); these only perform, over the JNI table.
    /// <list type="bullet">
    /// <item><see cref="ViaGame"/>: the game's own restart, <c>MainActivity.d.a(true)</c> on the UI thread
    /// (MainActivity.java:315-318: sets the restart flag and posts <c>finish()</c>; the resumed activity's <c>onPause</c> sees
    /// <c>isFinishing()</c> and calls <c>c()</c>, :147-157 and :107-115: <c>AlarmManager.set(RTC, now + 900, PendingIntent of
    /// its own intent)</c>, then <c>Process.killProcess(myPid())</c>).</item>
    /// <item><see cref="ViaHost"/>: on the UI thread, a relaunch alarm 1 s ahead (the launch intent with NEW_TASK | CLEAR_TASK,
    /// <c>FLAG_CANCEL_CURRENT</c>, <c>FLAG_IMMUTABLE</c> on 23+), then <c>finishAndRemoveTask()</c> of the game's activity (no
    /// activity survives for Android to re-create), then <c>Process.killProcess(myPid())</c> 200 ms later.</item>
    /// </list>
    /// </summary>
    internal static class AndroidExit
    {
        private const int FlagActivityNewTask = 0x10000000;
        private const int FlagActivityClearTask = 0x8000;
        private const int FlagCancelCurrent = 0x10000000;
        private const int FlagImmutable = 0x04000000;
        private const int RequestCode = 0x4D435653;   // "MCVS"
        private const int Rtc = 1;
        private const int UiWaitMs = 2000;

        /// <summary>MainActivity.d.a(relaunch) on the UI thread. False: no MainActivity (or the members are missing), or it failed.</summary>
        public static bool ViaGame(bool relaunch, Action<string, Exception> warn)
        {
            if (!AndroidJni.Has(AndroidJni.MainActivityInstance) || !AndroidJni.Has(AndroidJni.MainActivityRestart)) return false;
            IntPtr current = AndroidJni.GetStaticObject(AndroidJni.MainActivityInstance);
            if (current == IntPtr.Zero) return false;
            AndroidJni.Delete(current);
            int result = AndroidJni.RunOnUiThread(() =>
            {
                IntPtr activity = AndroidJni.GetStaticObject(AndroidJni.MainActivityInstance);   // local references are per thread
                try
                {
                    if (activity == IntPtr.Zero) throw new InvalidOperationException("MainActivity.d is null");
                    AndroidJni.CallVoid(activity, AndroidJni.MainActivityRestart, new JValue(relaunch));
                }
                finally
                {
                    AndroidJni.Delete(activity);
                }
            }, UiWaitMs, ex => warn("admin: the game's restart (MainActivity.a) failed", ex));
            return result == AndroidJni.UiDone;
        }

        /// <summary>
        /// The relaunch alarm (when <paramref name="relaunch"/>), finishAndRemoveTask on the UI thread, and the kill 200 ms after the
        /// UI thread ran it (at most 5 s later). True once posted.
        /// </summary>
        public static bool ViaHost(bool relaunch, Action<string, Exception> warn)
        {
            var finished = new ManualResetEvent(false);
            int result = AndroidJni.RunOnUiThread(() =>
            {
                try
                {
                    if (relaunch) ScheduleRelaunch(warn);
                    IntPtr activity = IntPtr.Zero;
                    try
                    {
                        activity = AndroidJni.CallStaticObject(AndroidJni.GetActivity);
                        if (activity != IntPtr.Zero) AndroidJni.CallVoid(activity, AndroidJni.FinishAndRemoveTask);
                    }
                    catch (Exception ex)
                    {
                        warn("admin: finishAndRemoveTask failed", ex);
                    }
                    finally
                    {
                        AndroidJni.Delete(activity);
                    }
                }
                finally
                {
                    finished.Set();
                }
            }, 0, ex => warn("admin: the host's restart failed", ex));
            if (result == AndroidJni.UiNotPosted) return false;
            var killer = new Thread(() =>
            {
                try
                {
                    finished.WaitOne(5000, false);
                    Thread.Sleep(200);
                    int pid = AndroidJni.CallStaticInt(AndroidJni.MyPid);
                    AndroidJni.CallStaticVoid(AndroidJni.KillProcess, new JValue(pid));
                }
                catch (Exception ex)
                {
                    warn("admin: killProcess failed", ex);
                }
            });
            killer.IsBackground = true;
            killer.Name = "mcvs-exit";
            killer.Start();
            return true;
        }

        /// <summary>AlarmManager.set(RTC, now + 1000, PendingIntent.getActivity(context, "MCVS", launch intent, CANCEL_CURRENT | IMMUTABLE)).</summary>
        private static void ScheduleRelaunch(Action<string, Exception> warn)
        {
            IntPtr pm = IntPtr.Zero, package = IntPtr.Zero, launch = IntPtr.Zero, same = IntPtr.Zero, pending = IntPtr.Zero;
            IntPtr alarmName = IntPtr.Zero, alarm = IntPtr.Zero;
            try
            {
                IntPtr context = AndroidJni.AppContext();
                pm = AndroidJni.CallObject(context, AndroidJni.GetPackageManager);
                package = AndroidJni.CallObject(context, AndroidJni.GetPackageName);
                launch = AndroidJni.CallObject(pm, AndroidJni.LaunchIntent, new JValue(package));
                if (launch == IntPtr.Zero) throw new InvalidOperationException("no launch intent for the game's package");
                same = AndroidJni.CallObject(launch, AndroidJni.IntentAddFlags, new JValue(FlagActivityNewTask | FlagActivityClearTask));
                int flags = FlagCancelCurrent | (AndroidJni.SdkInt >= 23 ? FlagImmutable : 0);
                pending = AndroidJni.CallStaticObject(AndroidJni.PendingGetActivity, new JValue(context), new JValue(RequestCode), new JValue(launch), new JValue(flags));
                alarmName = AndroidJni.Str("alarm");
                alarm = AndroidJni.CallObject(context, AndroidJni.GetSystemService, new JValue(alarmName));
                if (pending == IntPtr.Zero || alarm == IntPtr.Zero) throw new InvalidOperationException("no PendingIntent or AlarmManager");
                long now = AndroidJni.CallStaticLong(AndroidJni.CurrentTimeMillis);
                AndroidJni.CallVoid(alarm, AndroidJni.AlarmSet, new JValue(Rtc), new JValue(now + 1000), new JValue(pending));
            }
            catch (Exception ex)
            {
                warn("admin: the relaunch alarm could not be set (start the game again yourself)", ex);
            }
            finally
            {
                AndroidJni.Delete(alarm);
                AndroidJni.Delete(alarmName);
                AndroidJni.Delete(pending);
                AndroidJni.Delete(same);
                AndroidJni.Delete(launch);
                AndroidJni.Delete(package);
                AndroidJni.Delete(pm);
            }
        }
    }
}
#endif
