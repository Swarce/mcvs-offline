// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Android only: compiled into the embedded DLL (MCVS_EMBEDDED), never into the net8 build.
#if MCVS_EMBEDDED
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Android.Runtime;
using McvsOffline.Embedded.Diagnostics;
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
    internal sealed class AndroidPlatform : IEmbeddedPlatform, IPlatformDiagnostics, IInstallerPlatform
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

        // ------------------------------------------------------------------ IInstallerPlatform

        public string InternalFilesDir()
        {
            return AndroidDiagnostics.FilesDir();
        }

        public McvsOffline.Embedded.Install.IInstallerPrefs InstallerPrefs()
        {
            return new AndroidInstallerPrefs();
        }

        public void MonitorInstaller(Log log, BootTrace boot)
        {
            try
            {
                InstallerMonitor.Start(log, boot);
            }
            catch (Exception ex)
            {
                log.Info("[installer] monitor not started: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ IPlatformDiagnostics

        public void InstallCrashHooks()
        {
            // Installed by Bootstrap.InstallDiagnostics (before the host exists); nothing more here.
        }

        public string FallbackLogDirectory()
        {
            return AndroidDiagnostics.FilesDir();
        }

        public string ApplyGameLog(Settings settings, SafeLogFile gameLog)
        {
            return GameLogBridge.Apply(settings.GetString("gameLog.levels", "error warning"), settings.GetString("gameLog.mute", "profile:debug"), gameLog,
                                       (int)settings.GetLong("gameLog.maxMessageChars", 8192));
        }

        public string JavaStacks(string gameThreadComm)
        {
            try
            {
                return AndroidDiagnostics.JavaStacks(gameThreadComm);
            }
            catch (Exception ex)
            {
                return "    (Java stacks unavailable: " + ex.GetType().Name + ": " + ex.Message + ")\n";
            }
        }

        private int iapState = -1;

        /// <summary>
        /// Whether the native IAP library reports initialized: IapStoreComponent waits for it (WaitLibInitialization, no
        /// timeout) before it registers, and the unified shop (VersusCoins items included) waits for that registration.
        /// Read through the same native export the game polls every frame (a byte of the IAP object MainLoop.Start creates),
        /// only once GameApplication exists and the game loop has run (the watchdog saw a heartbeat), and no more once it said
        /// yes (the export is lock-free, but it reads a lazily created native singleton and an unsynchronised shared_ptr from
        /// our thread: the fewer such reads the better); logged as a milestone when it changes.
        /// </summary>
        public string StatusExtras()
        {
            try
            {
                if (iapState == 1) return "native IAP library initialized: yes";
                Type app = Type.GetType("Gameloft.Engine.GameApplication, Gameloft.Engine", false);
                object instance = app == null ? null : app.GetProperty("Instance").GetValue(null, null);
                if (instance == null) return null;
                EmbeddedHost current = EmbeddedHost.Current;
                if (current != null && current.Watchdog != null && current.Watchdog.Beats == 0) return "native IAP state: not read yet (game loop not seen)";
                bool initialized = NativeMethods.IapStoreComponent_IsInitialized_private();
                int state = initialized ? 1 : 0;
                if (state != iapState)
                {
                    iapState = state;
                    EmbeddedHost host = EmbeddedHost.Current;
                    if (host != null && host.Boot != null)
                        host.Boot.Mark("native IAP library initialized: " + (initialized ? "yes" : "no") + " (the shop waits for yes)");
                }
                return "native IAP library initialized: " + (initialized ? "yes" : "no");
            }
            catch (Exception ex)
            {
                return "native IAP state unavailable: " + ex.GetType().Name;
            }
        }

        private static class NativeMethods
        {
            [DllImport("nativeengine", CallingConvention = CallingConvention.Cdecl)]
            [return: MarshalAs(UnmanagedType.U1)]
            public static extern bool IapStoreComponent_IsInitialized_private();
        }

        public void StartBackground(EmbeddedHost host)
        {
            Log log = host.Logger.For("diag");
            if (host.Settings.GetBool("diagnostics.publicLogCopy", true))
            {
                try
                {
                    var mirror = new PublicMirror(host.LogFiles, s => log.Info(s));
                    mirror.Start();
                    CrashReporter.Mirror = mirror.CopyCrashFile;
                }
                catch (Exception ex)
                {
                    log.Warn("[mirror] not started", ex);
                }
            }
            var exits = new Thread(() =>
            {
                try
                {
                    Thread.Sleep(3000);   // out of the start-up path
                    string text = ExitReasons.Describe(5, host.Paths.LogsDir);
                    if (!string.IsNullOrEmpty(text)) log.Info(text);
                }
                catch (Exception ex)
                {
                    log.Info("[exit] previous exit reasons unavailable: " + ex.GetType().Name + ": " + ex.Message);
                }
            });
            exits.IsBackground = true;
            exits.Name = "mcvs-exit-reasons";
            exits.Start();
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
