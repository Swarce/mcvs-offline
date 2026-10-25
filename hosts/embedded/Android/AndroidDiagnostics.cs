// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Android only: compiled into the embedded DLL (MCVS_EMBEDDED), never into the net8 build.
#if MCVS_EMBEDDED
using System;
using System.Globalization;
using System.Text;
using Android.Runtime;
using McvsOffline.Embedded.Diagnostics;

namespace McvsOffline.Embedded.Android
{
    /// <summary>
    /// The Android pieces of the diagnostics, all through raw JNI (the linker trimmed Mono.Android to what the game uses;
    /// JNIEnv is complete): the AndroidEnvironment crash hook, the internal-storage fallback for log files, the SDK level,
    /// the public Download folder, and Java stacks for the watchdog. Every method catches everything.
    /// </summary>
    internal static class AndroidDiagnostics
    {
        /// <summary>Managed exceptions escaping a Java-to-managed callback. Never sets Handled: the game's behaviour is unchanged.</summary>
        public static void InstallCrashHook()
        {
            try
            {
                AndroidEnvironment.UnhandledExceptionRaiser += OnAndroidUnhandled;
                CrashReporter.AddHook("AndroidEnvironment ok");
            }
            catch (Exception)
            {
                CrashReporter.AddHook("AndroidEnvironment no");
            }
        }

        private static void OnAndroidUnhandled(object sender, RaiseThrowableEventArgs e)
        {
            CrashReporter.Report("AndroidEnvironment.UnhandledExceptionRaiser", e == null ? null : e.Exception, true);
        }

        /// <summary>Build.VERSION.SDK_INT, or 0.</summary>
        public static int SdkInt()
        {
            IntPtr cls = IntPtr.Zero;
            try
            {
                cls = JNIEnv.FindClass("android/os/Build$VERSION");
                IntPtr field = JNIEnv.GetStaticFieldID(cls, "SDK_INT", "I");
                return JNIEnv.GetStaticIntField(cls, field);
            }
            catch (Exception)
            {
                return 0;
            }
            finally
            {
                if (cls != IntPtr.Zero) JNIEnv.DeleteGlobalRef(cls);
            }
        }

        /// <summary>Context.getFilesDir() (internal storage, always writable), or null.</summary>
        public static string FilesDir()
        {
            try
            {
                IntPtr context = global::Android.App.Application.Context.Handle;
                IntPtr contextClass = JNIEnv.GetObjectClass(context);
                try
                {
                    IntPtr getFilesDir = JNIEnv.GetMethodID(contextClass, "getFilesDir", "()Ljava/io/File;");
                    IntPtr file = JNIEnv.CallObjectMethod(context, getFilesDir);
                    return AbsolutePath(file);
                }
                finally
                {
                    JNIEnv.DeleteLocalRef(contextClass);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Environment.getExternalStoragePublicDirectory("Download"), else $EXTERNAL_STORAGE/Download.</summary>
        public static string DownloadsDir()
        {
            try
            {
                IntPtr env = JNIEnv.FindClass("android/os/Environment");
                IntPtr name = JNIEnv.NewString("Download");
                try
                {
                    IntPtr get = JNIEnv.GetStaticMethodID(env, "getExternalStoragePublicDirectory", "(Ljava/lang/String;)Ljava/io/File;");
                    IntPtr file = JNIEnv.CallStaticObjectMethod(env, get, new[] { new JValue(name) });
                    string path = AbsolutePath(file);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                finally
                {
                    JNIEnv.DeleteLocalRef(name);
                    JNIEnv.DeleteGlobalRef(env);
                }
            }
            catch (Exception)
            {
            }
            string storage = System.Environment.GetEnvironmentVariable("EXTERNAL_STORAGE");
            return System.IO.Path.Combine(string.IsNullOrEmpty(storage) ? "/storage/emulated/0" : storage, "Download");
        }

        /// <summary>File.getAbsolutePath() of a local reference (which is released).</summary>
        private static string AbsolutePath(IntPtr file)
        {
            if (file == IntPtr.Zero) return null;
            IntPtr fileClass = JNIEnv.GetObjectClass(file);
            try
            {
                IntPtr abs = JNIEnv.GetMethodID(fileClass, "getAbsolutePath", "()Ljava/lang/String;");
                return JNIEnv.GetString(JNIEnv.CallObjectMethod(file, abs), JniHandleOwnership.TransferLocalRef);
            }
            finally
            {
                JNIEnv.DeleteLocalRef(fileClass);
                JNIEnv.DeleteLocalRef(file);
            }
        }

        /// <summary>
        /// Java stacks (Thread.getAllStackTraces) of the "main" (UI) thread and of every thread whose name starts with
        /// <paramref name="gameThreadComm"/> (the kernel's comm is the Java name cut to 15 characters). Called from the
        /// watchdog thread only, during a stall.
        /// </summary>
        public static string JavaStacks(string gameThreadComm)
        {
            var sb = new StringBuilder();
            IntPtr threadClass = JNIEnv.FindClass("java/lang/Thread");
            IntPtr mapClass = JNIEnv.FindClass("java/util/Map");
            IntPtr setClass = JNIEnv.FindClass("java/util/Set");
            IntPtr entryClass = JNIEnv.FindClass("java/util/Map$Entry");
            IntPtr objectClass = JNIEnv.FindClass("java/lang/Object");
            try
            {
                IntPtr getAll = JNIEnv.GetStaticMethodID(threadClass, "getAllStackTraces", "()Ljava/util/Map;");
                IntPtr getName = JNIEnv.GetMethodID(threadClass, "getName", "()Ljava/lang/String;");
                IntPtr getState = JNIEnv.GetMethodID(threadClass, "getState", "()Ljava/lang/Thread$State;");
                IntPtr entrySet = JNIEnv.GetMethodID(mapClass, "entrySet", "()Ljava/util/Set;");
                IntPtr toArray = JNIEnv.GetMethodID(setClass, "toArray", "()[Ljava/lang/Object;");
                IntPtr getKey = JNIEnv.GetMethodID(entryClass, "getKey", "()Ljava/lang/Object;");
                IntPtr getValue = JNIEnv.GetMethodID(entryClass, "getValue", "()Ljava/lang/Object;");
                IntPtr toStringId = JNIEnv.GetMethodID(objectClass, "toString", "()Ljava/lang/String;");

                IntPtr map = JNIEnv.CallStaticObjectMethod(threadClass, getAll);
                IntPtr set = JNIEnv.CallObjectMethod(map, entrySet);
                IntPtr entries = JNIEnv.CallObjectMethod(set, toArray);
                JNIEnv.DeleteLocalRef(set);
                JNIEnv.DeleteLocalRef(map);
                int count = JNIEnv.GetArrayLength(entries);
                int shown = 0;
                for (int i = 0; i < count && shown < 6; i++)
                {
                    IntPtr entry = JNIEnv.GetObjectArrayElement(entries, i);
                    IntPtr thread = JNIEnv.CallObjectMethod(entry, getKey);
                    string name = JNIEnv.GetString(JNIEnv.CallObjectMethod(thread, getName), JniHandleOwnership.TransferLocalRef) ?? "";
                    // comm is the name cut to 15 characters: equal, or a prefix when it was cut.
                    bool wanted = name == "main" ||
                                  (!string.IsNullOrEmpty(gameThreadComm) &&
                                   (name == gameThreadComm || (gameThreadComm.Length >= 15 && name.StartsWith(gameThreadComm, StringComparison.Ordinal))));
                    if (wanted)
                    {
                        shown++;
                        IntPtr state = JNIEnv.CallObjectMethod(thread, getState);
                        string stateName = JNIEnv.GetString(JNIEnv.CallObjectMethod(state, toStringId), JniHandleOwnership.TransferLocalRef);
                        JNIEnv.DeleteLocalRef(state);
                        sb.Append("    \"").Append(name).Append("\" ").Append(stateName).Append('\n');
                        IntPtr frames = JNIEnv.CallObjectMethod(entry, getValue);
                        int depth = JNIEnv.GetArrayLength(frames);
                        for (int k = 0; k < depth && k < 25; k++)
                        {
                            IntPtr frame = JNIEnv.GetObjectArrayElement(frames, k);
                            string text = JNIEnv.GetString(JNIEnv.CallObjectMethod(frame, toStringId), JniHandleOwnership.TransferLocalRef);
                            JNIEnv.DeleteLocalRef(frame);
                            sb.Append("      at ").Append(text).Append('\n');
                        }
                        if (depth > 25) sb.Append("      ... ").Append((depth - 25).ToString(CultureInfo.InvariantCulture)).Append(" more\n");
                        JNIEnv.DeleteLocalRef(frames);
                    }
                    JNIEnv.DeleteLocalRef(thread);
                    JNIEnv.DeleteLocalRef(entry);
                }
                JNIEnv.DeleteLocalRef(entries);
                if (shown == 0) sb.Append("    (no Java thread named 'main' or '").Append(gameThreadComm ?? "").Append("...')\n");
            }
            finally
            {
                JNIEnv.DeleteGlobalRef(objectClass);
                JNIEnv.DeleteGlobalRef(entryClass);
                JNIEnv.DeleteGlobalRef(setClass);
                JNIEnv.DeleteGlobalRef(mapClass);
                JNIEnv.DeleteGlobalRef(threadClass);
            }
            return sb.ToString();
        }
    }
}
#endif
