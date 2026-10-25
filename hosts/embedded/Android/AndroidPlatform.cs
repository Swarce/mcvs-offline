// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Android only: compiled into the embedded DLL (MCVS_EMBEDDED), never into the net8 build.
#if MCVS_EMBEDDED
using System;
using System.IO;
using Android.Runtime;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded.Android
{
    /// <summary>
    /// Resolves the game root exactly like the native engine does. Native init calls the Java static
    /// <c>com.gameloft.android.PackageUtils.AndroidUtils.RetrieveDataPath()</c> and appends "/args.txt"
    /// (verify-C1 §1, §1.1):
    /// <code>
    /// String pref = SUtils.getPreferenceString(ctx, "SDFolder", GameInstaller.mPreferencesName /* "MainActivityPrefs" */);
    /// return pref != "" ? pref : ctx.getExternalFilesDir(null).getAbsolutePath();
    /// </code>
    /// We call the game's own Java helpers through raw JNI, because the linker trimmed Mono.Android down to what the
    /// game uses: there is no Context.GetSharedPreferences/GetExternalFilesDir binding left, but Android.Runtime.JNIEnv
    /// is complete. FrameworkApplication.onCreate has already stored the application context SUtils needs (an
    /// Application's onCreate runs before any Activity's).
    /// <para>
    /// First run: GameInstaller.onCreate sets SDFolder only later (to its own choice among the external files dir
    /// and old-style /storage/sdcard* mounts, GameInstaller.C()). We pin SDFolder to the value we use, so the Java
    /// installer, the native engine and the server agree on one folder from the very first launch. On current devices
    /// it is the same folder the installer would pick.
    /// </para>
    /// </summary>
    internal sealed class AndroidPlatform : IEmbeddedPlatform
    {
        public const string PackageName = "com.gameloft.android.ANMP.GloftMVHM";
        private const string PreferencesName = "MainActivityPrefs";   // GameInstaller.mPreferencesName
        private const string SdFolderKey = "SDFolder";
        private const string SUtilsClass = "com/gameloft/android/GLUtils/SUtils";
        private const string AndroidUtilsClass = "com/gameloft/android/PackageUtils/AndroidUtils";

        public string Name { get { return "android"; } }

        public string ResolveGameRoot(Log log)
        {
            string pref = null;
            try
            {
                pref = CallStaticString(SUtilsClass, "getPreferenceString",
                                        "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;",
                                        SdFolderKey, "", PreferencesName);
            }
            catch (Exception ex)
            {
                log.Warn("cannot read the SDFolder preference through SUtils", ex);
            }
            if (!string.IsNullOrEmpty(pref))
            {
                log.Info("game root from the SDFolder preference: " + pref);
                return pref;
            }

            string root = null;
            try
            {
                root = CallStaticString(AndroidUtilsClass, "RetrieveDataPath", "()Ljava/lang/String;");
            }
            catch (Exception ex)
            {
                log.Warn("AndroidUtils.RetrieveDataPath() failed", ex);
            }
            if (string.IsNullOrEmpty(root))
            {
                root = FallbackExternalFilesDir();
                log.Warn("using the default external files directory " + root);
            }

            try
            {
                CallStaticVoid(SUtilsClass, "setPreference", "(Ljava/lang/String;Ljava/lang/Object;Ljava/lang/String;)V",
                               SdFolderKey, root, PreferencesName);
                log.Info("first run: pinned the SDFolder preference to " + root);
            }
            catch (Exception ex)
            {
                log.Warn("cannot pin the SDFolder preference", ex);
            }
            return root;
        }

        public LogSink CreateSystemLogSink(LogLevel minLevel)
        {
            return new LogcatSink(minLevel);
        }

        /// <summary>/sdcard/Android/data/&lt;package&gt;/files (EXTERNAL_STORAGE is set for every Android process).</summary>
        private static string FallbackExternalFilesDir()
        {
            string storage = Environment.GetEnvironmentVariable("EXTERNAL_STORAGE");
            if (string.IsNullOrEmpty(storage)) storage = "/sdcard";
            string package = PackageName;
            try
            {
                string fromContext = global::Android.App.Application.Context.PackageName;
                if (!string.IsNullOrEmpty(fromContext)) package = fromContext;
            }
            catch (Exception)
            {
            }
            return Path.Combine(Path.Combine(Path.Combine(storage, "Android"), "data"), Path.Combine(package, "files"));
        }

        private static string CallStaticString(string className, string method, string signature, params string[] args)
        {
            IntPtr cls = JNIEnv.FindClass(className);   // global reference
            var locals = new IntPtr[args.Length];
            try
            {
                IntPtr id = JNIEnv.GetStaticMethodID(cls, method, signature);
                var values = new JValue[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    locals[i] = JNIEnv.NewString(args[i]);
                    values[i] = new JValue(locals[i]);
                }
                IntPtr result = JNIEnv.CallStaticObjectMethod(cls, id, values);
                return JNIEnv.GetString(result, JniHandleOwnership.TransferLocalRef);
            }
            finally
            {
                foreach (IntPtr local in locals)
                {
                    if (local != IntPtr.Zero) JNIEnv.DeleteLocalRef(local);
                }
                JNIEnv.DeleteGlobalRef(cls);
            }
        }

        private static void CallStaticVoid(string className, string method, string signature, params string[] args)
        {
            IntPtr cls = JNIEnv.FindClass(className);
            var locals = new IntPtr[args.Length];
            try
            {
                IntPtr id = JNIEnv.GetStaticMethodID(cls, method, signature);
                var values = new JValue[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    locals[i] = JNIEnv.NewString(args[i]);
                    values[i] = new JValue(locals[i]);
                }
                JNIEnv.CallStaticVoidMethod(cls, id, values);
            }
            finally
            {
                foreach (IntPtr local in locals)
                {
                    if (local != IntPtr.Zero) JNIEnv.DeleteLocalRef(local);
                }
                JNIEnv.DeleteGlobalRef(cls);
            }
        }
    }
}
#endif
