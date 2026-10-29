// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Android only: compiled into the embedded DLL (MCVS_ANDROID), never into the net8 build.
#if MCVS_ANDROID
using System;
using System.Globalization;
using System.IO;
using System.Text;
using Android.Runtime;
using McvsOffline.Embedded.Diagnostics;

namespace McvsOffline.Embedded.Android
{
    /// <summary>
    /// Why the previous game processes ended (Android 11+, ActivityManager.getHistoricalProcessExitReasons): a crash
    /// (a managed crash ends as CRASH_NATIVE here, because Mono's exit() then crashes the Adreno driver: correlate with the
    /// crash file's time and ignore that tombstone), an ANR, the player swiping the game away during a stall
    /// (USER_REQUESTED / SIGNALED), low memory. ANR traces and native tombstones are saved next to the logs. Background
    /// thread only (JNI into ActivityManager).
    /// </summary>
    internal static class ExitReasons
    {
        private static readonly string[] Names =
        {
            "UNKNOWN", "EXIT_SELF", "SIGNALED", "LOW_MEMORY", "CRASH", "CRASH_NATIVE", "ANR", "INITIALIZATION_FAILURE",
            "PERMISSION_CHANGE", "EXCESSIVE_RESOURCE_USAGE", "USER_REQUESTED", "USER_STOPPED", "DEPENDENCY_DIED", "OTHER",
            "FREEZER", "PACKAGE_STATE_CHANGE", "PACKAGE_UPDATED",
        };

        /// <summary>
        /// One line per previous process (newest first), or null below API 30. Traces are saved only for exits at or after
        /// <paramref name="keepSinceUtc"/> (LogRetention.TraceCutoffUtc, null: all): an older one was deleted with its start and
        /// must not be written again (SaveTrace only checks that the file exists). With logging.keepStarts 0 that is the previous
        /// start, so the trace of the process that just ended is still saved (and deleted at the next start).
        /// </summary>
        public static string Describe(int max, string traceDirectory, DateTime? keepSinceUtc)
        {
            if (AndroidDiagnostics.SdkInt() < 30) return null;
            IntPtr context = global::Android.App.Application.Context.Handle;
            IntPtr contextClass = JNIEnv.GetObjectClass(context);
            IntPtr service = JNIEnv.NewString("activity");
            try
            {
                IntPtr getSystemService = JNIEnv.GetMethodID(contextClass, "getSystemService", "(Ljava/lang/String;)Ljava/lang/Object;");
                IntPtr am = JNIEnv.CallObjectMethod(context, getSystemService, new[] { new JValue(service) });
                IntPtr amClass = JNIEnv.GetObjectClass(am);
                IntPtr history = JNIEnv.GetMethodID(amClass, "getHistoricalProcessExitReasons", "(Ljava/lang/String;II)Ljava/util/List;");
                IntPtr list = JNIEnv.CallObjectMethod(am, history, new[] { new JValue(IntPtr.Zero), new JValue(0), new JValue(max) });
                IntPtr listClass = JNIEnv.GetObjectClass(list);
                int count = JNIEnv.CallIntMethod(list, JNIEnv.GetMethodID(listClass, "size", "()I"));
                IntPtr get = JNIEnv.GetMethodID(listClass, "get", "(I)Ljava/lang/Object;");
                var sb = new StringBuilder();
                sb.Append("[exit] how the last ").Append(count.ToString(CultureInfo.InvariantCulture)).Append(" game processes ended (newest first):");
                for (int i = 0; i < count; i++)
                {
                    IntPtr info = JNIEnv.CallObjectMethod(list, get, new[] { new JValue(i) });
                    IntPtr infoClass = JNIEnv.GetObjectClass(info);
                    int reason = JNIEnv.CallIntMethod(info, JNIEnv.GetMethodID(infoClass, "getReason", "()I"));
                    int status = JNIEnv.CallIntMethod(info, JNIEnv.GetMethodID(infoClass, "getStatus", "()I"));
                    int pid = JNIEnv.CallIntMethod(info, JNIEnv.GetMethodID(infoClass, "getPid", "()I"));
                    long when = JNIEnv.CallLongMethod(info, JNIEnv.GetMethodID(infoClass, "getTimestamp", "()J"));
                    string description = JNIEnv.GetString(JNIEnv.CallObjectMethod(info, JNIEnv.GetMethodID(infoClass, "getDescription", "()Ljava/lang/String;")),
                                                          JniHandleOwnership.TransferLocalRef);
                    DateTime exitUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(when);
                    sb.Append("\n  pid ").Append(pid.ToString(CultureInfo.InvariantCulture)).Append(' ')
                      .Append(exitUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                      .Append(' ').Append(reason >= 0 && reason < Names.Length ? Names[reason] : reason.ToString(CultureInfo.InvariantCulture))
                      .Append(" status ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(" \"").Append(description).Append('"');
                    if (traceDirectory != null && (reason == 5 || reason == 6) && !LogRetention.KeepsExit(exitUtc, keepSinceUtc))
                    {
                        sb.Append(" (trace not kept: older than the kept starts)");
                    }
                    else if (traceDirectory != null && (reason == 5 || reason == 6))
                    {
                        string saved = SaveTrace(info, infoClass, Path.Combine(traceDirectory, "exit-" + pid.ToString(CultureInfo.InvariantCulture) +
                                                                               (reason == 6 ? "-anr.txt" : "-tombstone.pb")));
                        if (saved != null) sb.Append(" (trace: ").Append(saved).Append(')');
                    }
                    JNIEnv.DeleteLocalRef(infoClass);
                    JNIEnv.DeleteLocalRef(info);
                }
                JNIEnv.DeleteLocalRef(listClass);
                JNIEnv.DeleteLocalRef(list);
                JNIEnv.DeleteLocalRef(amClass);
                JNIEnv.DeleteLocalRef(am);
                return sb.ToString();
            }
            finally
            {
                JNIEnv.DeleteLocalRef(service);
                JNIEnv.DeleteLocalRef(contextClass);
            }
        }

        /// <summary>ApplicationExitInfo.getTraceInputStream(): the ANR text or the native tombstone (protobuf), at most 8 MB.</summary>
        private static string SaveTrace(IntPtr info, IntPtr infoClass, string path)
        {
            try
            {
                if (File.Exists(path)) return path;
                IntPtr stream = JNIEnv.CallObjectMethod(info, JNIEnv.GetMethodID(infoClass, "getTraceInputStream", "()Ljava/io/InputStream;"));
                if (stream == IntPtr.Zero) return null;
                IntPtr streamClass = JNIEnv.GetObjectClass(stream);
                IntPtr read = JNIEnv.GetMethodID(streamClass, "read", "([B)I");
                var buffer = new byte[64 * 1024];
                IntPtr javaBuffer = JNIEnv.NewArray(buffer);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        long total = 0;
                        while (total < 8 * 1024 * 1024)
                        {
                            int n = JNIEnv.CallIntMethod(stream, read, new[] { new JValue(javaBuffer) });
                            if (n <= 0) break;
                            JNIEnv.CopyArray(javaBuffer, buffer);
                            fs.Write(buffer, 0, n);
                            total += n;
                        }
                    }
                }
                finally
                {
                    JNIEnv.CallVoidMethod(stream, JNIEnv.GetMethodID(streamClass, "close", "()V"));
                    JNIEnv.DeleteLocalRef(javaBuffer);
                    JNIEnv.DeleteLocalRef(streamClass);
                    JNIEnv.DeleteLocalRef(stream);
                }
                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
#endif
