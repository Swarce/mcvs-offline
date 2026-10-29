// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Android only: compiled into the embedded DLL (MCVS_ANDROID), never into the net8 build.
#if MCVS_ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Android.Runtime;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;

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
    /// <para>
    /// The settings page's device side (<see cref="IAdminPlatform"/>, design §8.2, §8.6): the in-game browser, the share sheet,
    /// clipboard and toasts, the restart, free space, all through the one JNI table of <see cref="AndroidJni"/>
    /// (<see cref="AndroidShare"/>, <see cref="AndroidExit"/>).
    /// </para>
    /// </summary>
    internal sealed class AndroidPlatform : IEmbeddedPlatform, IPlatformDiagnostics, IInstallerPlatform, IAdminPlatform,
                                            McvsOffline.Embedded.Notifications.INotificationPlatform
    {
        public const string PackageName = "com.gameloft.android.ANMP.GloftMVHM";
        private const string PreferencesName = "MainActivityPrefs";   // GameInstaller.mPreferencesName
        private const string SdFolderKey = "SDFolder";
        private const string SUtilsClass = "com/gameloft/android/GLUtils/SUtils";
        private const string AndroidUtilsClass = "com/gameloft/android/PackageUtils/AndroidUtils";

        public string Name { get { return "android"; } }

        public string EntryPoint { get { return "NicePlaySDKDLL.OnInitialize"; } }

        public string ArgsReader { get { return "JNIBridge.NativeInit"; } }

        public string PortInUseHint { get { return "If a standalone server is connected with 'adb reverse', the game will use that one."; } }

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

        public int InstallerHandedOver()
        {
            return InstallerMonitor.HandedOver();
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
            adminLog = host.Logger.For("android");
            if (host.Paths != null) ledger = new MediaStoreLedger(host.Paths.DataDir);
            // The kept starts (settings logging.keepStarts): the Downloads copies and the exit traces follow the logs/ rule.
            LogRetention retention = host.Retention;
            DateTime? cutoff = retention == null ? null : retention.CutoffUtc;
            if (host.Settings.GetBool("diagnostics.publicLogCopy", true))
            {
                try
                {
                    var mirror = new PublicMirror(host.LogFiles, host.Paths.LogsDir, s => log.Info(s))
                    {
                        SdkInt = AndroidDiagnostics.SdkInt,
                        DownloadsDir = AndroidDiagnostics.DownloadsDir,
                        Scan = ScanFiles,
                        DiagnosticsBeforeUtc = cutoff,
                        // Session names are local times: after the clock went back their order says nothing; keep them all.
                        KeepAllSessions = retention != null && retention.ClockWentBackNow,
                    };
                    if (retention != null) mirror.KeepSessions = retention.KeepStarts + 1;
                    host.MirrorStatus = mirror.Describe;
                    mirror.Start();
                    CrashReporter.Mirror = mirror.CopyCrashFile;
                }
                catch (Exception ex)
                {
                    log.Warn("[mirror] not started", ex);
                }
            }
            else
            {
                host.MirrorStatus = () => "off (diagnostics.publicLogCopy is false)";
            }
            var exits = new Thread(() =>
            {
                try
                {
                    Thread.Sleep(3000);   // out of the start-up path
                    string text = ExitReasons.Describe(5, host.Paths.LogsDir, retention == null ? null : retention.TraceCutoffUtc);
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

        // ------------------------------------------------------------------ IAdminPlatform

        private volatile Log adminLog;
        private volatile MediaStoreLedger ledger;

        private void Warn(string message, Exception ex)
        {
            try
            {
                Log log = adminLog;
                if (log != null) log.Warn(message, ex);
                else SelfLog.Write(SelfLog.Warn, message + ": " + (ex == null ? "" : ex.GetType().Name + ": " + ex.Message));
            }
            catch (Exception)
            {
            }
        }

        public int SdkInt
        {
            get
            {
                AndroidJni.Resolve();
                return AndroidJni.SdkInt;
            }
        }

        public List<string> MissingJniMembers()
        {
            return AndroidJni.Resolve();
        }

        public AdminPlatformCapabilities Capabilities()
        {
            return AndroidCapabilities.From(SdkInt, MissingJniMembers());
        }

        public bool OpenInGameBrowserJni(string url)
        {
            return OpenInGameBrowser(url, null);
        }

        public bool OpenInGameBrowserWithPostData(string url, string postData)
        {
            return postData != null && OpenInGameBrowser(url, postData);
        }

        /// <summary>
        /// InGameBrowser.SetParameters("EN") (without it onCreate closes the activity at once, "NotItitialized") then OpenURL(url,
        /// true) or OpenURLWithPostData(url, postData) (InGameBrowser.java:113-138, 373-406). Any thread: they only start the
        /// activity. The URL carries the page ticket: never logged.
        /// </summary>
        private bool OpenInGameBrowser(string url, string postData)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (!AndroidJni.Has(AndroidJni.IgbSetParameters) || !AndroidJni.Has(postData == null ? AndroidJni.IgbOpenUrl : AndroidJni.IgbOpenWithPost)) return false;
            IntPtr language = IntPtr.Zero, target = IntPtr.Zero, form = IntPtr.Zero;
            try
            {
                language = AndroidJni.Str("EN");
                target = AndroidJni.Str(url);
                AndroidJni.CallStaticVoid(AndroidJni.IgbSetParameters, new JValue(language));
                if (postData == null)
                {
                    AndroidJni.CallStaticVoid(AndroidJni.IgbOpenUrl, new JValue(target), new JValue(true));
                }
                else
                {
                    form = AndroidJni.Str(postData);
                    AndroidJni.CallStaticVoid(AndroidJni.IgbOpenWithPost, new JValue(target), new JValue(form));
                }
                return true;
            }
            catch (Exception ex)
            {
                Warn("admin: InGameBrowser could not be opened over JNI", ex);
                return false;
            }
            finally
            {
                AndroidJni.Delete(form);
                AndroidJni.Delete(target);
                AndroidJni.Delete(language);
            }
        }

        public bool IsInGameBrowserOpen()
        {
            try
            {
                return AndroidJni.Has(AndroidJni.IgbShown) && AndroidJni.GetStaticBool(AndroidJni.IgbShown);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool QuitInGameBrowser()
        {
            try
            {
                if (!IsInGameBrowserOpen() || !AndroidJni.Has(AndroidJni.IgbQuit)) return false;
                // QuitIGB posts to the browser's own view when called off the UI thread (InGameBrowser.java:118-128).
                AndroidJni.CallStaticVoid(AndroidJni.IgbQuit);
                return true;
            }
            catch (Exception ex)
            {
                Warn("admin: InGameBrowser.QuitIGB failed", ex);
                return false;
            }
        }

        public AdminShareResult ShareFile(string path, string mimeType, string displayName, string subject, string text)
        {
            try
            {
                MediaStoreLedger l = ledger;
                if (l == null && EmbeddedHost.Current != null && EmbeddedHost.Current.Paths != null)
                    l = ledger = new MediaStoreLedger(EmbeddedHost.Current.Paths.DataDir);
                return AndroidShare.ShareFile(path, mimeType, displayName, subject, text, l, Warn);
            }
            catch (Exception ex)
            {
                Warn("admin: sharing a file failed", ex);
                return AdminShareResult.None("The file could not be shared.");
            }
        }

        public AdminShareResult ShareText(string subject, string text)
        {
            try
            {
                return AndroidShare.ShareText(subject, text, Warn);
            }
            catch (Exception ex)
            {
                Warn("admin: sharing a text failed", ex);
                return AdminShareResult.None("The text could not be shared.");
            }
        }

        public bool CopyToClipboard(string label, string text)
        {
            try
            {
                return AndroidShare.CopyToClipboard(label, text, Warn);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void ShowToast(string text)
        {
            try
            {
                AndroidShare.ShowToast(text, Warn);
            }
            catch (Exception)
            {
            }
        }

        public bool ExitViaGame(bool relaunch)
        {
            try
            {
                return AndroidExit.ViaGame(relaunch, Warn);
            }
            catch (Exception ex)
            {
                Warn("admin: the game's restart failed", ex);
                return false;
            }
        }

        public bool ExitViaHost(bool relaunch)
        {
            try
            {
                return AndroidExit.ViaHost(relaunch, Warn);
            }
            catch (Exception ex)
            {
                Warn("admin: the host's restart failed", ex);
                return false;
            }
        }

        public string PublicDownloadsDir()
        {
            try
            {
                string downloads = AndroidDiagnostics.DownloadsDir();
                return string.IsNullOrEmpty(downloads) ? null : Path.Combine(downloads, PublicMirror.FolderName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ INotificationPlatform (target-sdk.md WP-T5)

        public int TargetSdk()
        {
            return AndroidNotifications.TargetSdk();
        }

        public bool? Declared()
        {
            return AndroidNotifications.Declared();
        }

        public bool? Granted()
        {
            return AndroidNotifications.Granted();
        }

        public bool Request()
        {
            return AndroidNotifications.Request(Warn);
        }

        /// <summary>new StatFs(path).getAvailableBytes(), -1 on failure.</summary>
        public long FreeBytes(string path)
        {
            if (string.IsNullOrEmpty(path) || !AndroidJni.Has(AndroidJni.StatFsNew) || !AndroidJni.Has(AndroidJni.StatFsAvailable)) return -1;
            IntPtr jpath = IntPtr.Zero, statFs = IntPtr.Zero;
            try
            {
                jpath = AndroidJni.Str(path);
                statFs = AndroidJni.New(AndroidJni.StatFsNew, new JValue(jpath));
                return AndroidJni.CallLong(statFs, AndroidJni.StatFsAvailable);
            }
            catch (Exception)
            {
                return -1;
            }
            finally
            {
                AndroidJni.Delete(statFs);
                AndroidJni.Delete(jpath);
            }
        }

        /// <summary>
        /// The log mirror's scan (§7.5): MediaScannerConnection.scanFile(context, paths, text/plain..., null). No permission needed; it
        /// clears IS_PENDING and records the real _size of files written through the FUSE path.
        /// </summary>
        private void ScanFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0 || !AndroidJni.Has(AndroidJni.ScanFile)) return;
            var mimes = new string[paths.Length];
            for (int i = 0; i < mimes.Length; i++) mimes[i] = "text/plain";
            IntPtr jpaths = IntPtr.Zero, jmimes = IntPtr.Zero;
            try
            {
                jpaths = JNIEnv.NewArray(paths);
                jmimes = JNIEnv.NewArray(mimes);
                AndroidJni.CallStaticVoid(AndroidJni.ScanFile, new JValue(AndroidJni.AppContext()), new JValue(jpaths), new JValue(jmimes), new JValue(IntPtr.Zero));
            }
            finally
            {
                AndroidJni.Delete(jmimes);
                AndroidJni.Delete(jpaths);
            }
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
