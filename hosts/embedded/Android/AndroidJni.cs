// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// The table and the capability rules are platform-independent (compiled into the net8 tests: JniTableTests checks the table
// against android.jar and the APK's dex); resolving and calling are Android only (MCVS_EMBEDDED).
using System;
using System.Collections.Generic;
#if MCVS_EMBEDDED
using System.Threading;
using Android.Runtime;
#endif

namespace McvsOffline.Embedded.Android
{
    /// <summary>One Java member the host calls: class (JNI name), member name, JNI descriptor, static or instance, field or method.</summary>
    internal sealed class JniMember
    {
        public JniMember(string className, string name, string descriptor, bool isStatic, bool isField, int minSdk, string use)
        {
            Class = className;
            Name = name;
            Descriptor = descriptor;
            IsStatic = isStatic;
            IsField = isField;
            MinSdk = minSdk;
            Use = use;
        }

        public string Class { get; private set; }

        public string Name { get; private set; }

        public string Descriptor { get; private set; }

        public bool IsStatic { get; private set; }

        public bool IsField { get; private set; }

        /// <summary>Not resolved (and not missing) on an older device.</summary>
        public int MinSdk { get; private set; }

        /// <summary>The capability group it belongs to (<see cref="AndroidJni"/> constants).</summary>
        public string Use { get; private set; }

        /// <summary>"android/content/Intent.addFlags(I)Landroid/content/Intent;" (fields: "Class.name:descriptor"), as Describe().MissingJni shows it.</summary>
        public string Key
        {
            get { return Class + "." + Name + (IsField ? ":" : "") + Descriptor; }
        }

#if MCVS_EMBEDDED
        /// <summary>jmethodID / jfieldID once resolved (valid while the class's global reference is held).</summary>
        internal IntPtr Id;

        /// <summary>The class's global reference once resolved.</summary>
        internal IntPtr ClassRef;
#endif

        public override string ToString()
        {
            return Key;
        }
    }

    /// <summary>
    /// The one JNI table (design §8.6): every Java class, method and field the host's settings-page code uses is one entry here,
    /// and every call looks its member up through it (cached <c>jmethodID</c>/<c>jfieldID</c>); nothing is written inline
    /// elsewhere. <see cref="Resolve"/> runs once from <c>EmbeddedHost.Prepare</c> (entries above the device's SDK are skipped)
    /// and reports misses (<c>Describe().MissingJni</c>); a capability whose members are missing is off
    /// (<see cref="Available"/>). The trimmed Mono.Android has no Intent/ContentResolver bindings, but
    /// <c>Android.Runtime.JNIEnv</c> is complete and <c>JNIEnv.FindClass</c> uses the app class loader, so it works from any
    /// thread. Framework entries are checked against android.jar (API 29; the notification constants against API 33+) and the game's against the APK's dex by
    /// JniTableTests. None needs a manifest change.
    /// </summary>
    internal static partial class AndroidJni
    {
        // Capability groups.
        public const string Common = "common";
        public const string Browser = "browser";
        public const string Post = "post";
        public const string Downloads = "downloads";
        public const string Send = "send";
        public const string FileUri = "fileuri";
        public const string StrictModeUse = "strictmode";
        public const string Clipboard = "clipboard";
        public const string Toast = "toast";
        public const string ShareTextUse = "sharetext";
        public const string ExitGame = "exit-game";
        public const string ExitHost = "exit-host";
        public const string Scan = "scan";
        public const string Storage = "statfs";
        public const string Notify = "notify";

        /// <summary>Manifest.permission.POST_NOTIFICATIONS (API 33; checked against an android.jar of API 33+ by JniTableTests).</summary>
        public const string PostNotifications = "android.permission.POST_NOTIFICATIONS";

        /// <summary>PackageManager.PERMISSION_GRANTED.</summary>
        public const int PermissionGranted = 0;

        /// <summary>PackageManager.GET_PERMISSIONS (fills PackageInfo.requestedPermissions).</summary>
        public const int GetPermissions = 4096;

        /// <summary>Build.VERSION_CODES.TIRAMISU (Android 13): the notification permission's first API level.</summary>
        public const int Tiramisu = 33;

        private const string Context = "android/content/Context";
        private const string ContentResolver = "android/content/ContentResolver";
        private const string ContentValues = "android/content/ContentValues";
        private const string Intent = "android/content/Intent";
        private const string ClipData = "android/content/ClipData";
        private const string Uri = "android/net/Uri";
        private const string Cursor = "android/database/Cursor";
        private const string Pfd = "android/os/ParcelFileDescriptor";
        private const string Activity = "android/app/Activity";
        private const string SUtils = "com/gameloft/android/GLUtils/SUtils";
        private const string InGameBrowser = "com/gameloft/ingamebrowser/InGameBrowser";
        private const string MainActivity = "com/gameloft/android/MainActivity";

        // Common.
        public static readonly JniMember GetSystemService = M(Context, "getSystemService", "(Ljava/lang/String;)Ljava/lang/Object;", Common);
        public static readonly JniMember StartActivity = M(Activity, "startActivity", "(Landroid/content/Intent;)V", Common);
        public static readonly JniMember GetActivity = S(SUtils, "getActivity", "()Landroid/app/Activity;", Common);
        public static readonly JniMember IntentAddFlags = M(Intent, "addFlags", "(I)Landroid/content/Intent;", Common);

        // The in-game browser (InGameBrowser.java:113-138, 373-406, 505-520).
        public static readonly JniMember IgbSetParameters = S(InGameBrowser, "SetParameters", "(Ljava/lang/String;)V", Browser);
        public static readonly JniMember IgbOpenUrl = S(InGameBrowser, "OpenURL", "(Ljava/lang/String;Z)V", Browser);
        public static readonly JniMember IgbQuit = S(InGameBrowser, "QuitIGB", "()V", Browser);
        public static readonly JniMember IgbShown = F(InGameBrowser, "b", "Z", Browser);
        public static readonly JniMember IgbOpenWithPost = S(InGameBrowser, "OpenURLWithPostData", "(Ljava/lang/String;Ljava/lang/String;)V", Post);

        // MediaStore Downloads (Android 10+).
        public static readonly JniMember DownloadsUri = new JniMember("android/provider/MediaStore$Downloads", "EXTERNAL_CONTENT_URI", "Landroid/net/Uri;", true, true, 29, Downloads);
        public static readonly JniMember GetContentResolver = M(Context, "getContentResolver", "()Landroid/content/ContentResolver;", Downloads);
        public static readonly JniMember ValuesNew = M(ContentValues, "<init>", "()V", Downloads);
        public static readonly JniMember ValuesPutString = M(ContentValues, "put", "(Ljava/lang/String;Ljava/lang/String;)V", Downloads);
        public static readonly JniMember ValuesPutInteger = M(ContentValues, "put", "(Ljava/lang/String;Ljava/lang/Integer;)V", Downloads);
        public static readonly JniMember IntegerValueOf = S("java/lang/Integer", "valueOf", "(I)Ljava/lang/Integer;", Downloads);
        public static readonly JniMember ResolverInsert = M(ContentResolver, "insert", "(Landroid/net/Uri;Landroid/content/ContentValues;)Landroid/net/Uri;", Downloads);
        public static readonly JniMember ResolverOpenFd = M(ContentResolver, "openFileDescriptor", "(Landroid/net/Uri;Ljava/lang/String;)Landroid/os/ParcelFileDescriptor;", Downloads);
        public static readonly JniMember ResolverUpdate = M(ContentResolver, "update", "(Landroid/net/Uri;Landroid/content/ContentValues;Ljava/lang/String;[Ljava/lang/String;)I", Downloads);
        public static readonly JniMember ResolverDelete = M(ContentResolver, "delete", "(Landroid/net/Uri;Ljava/lang/String;[Ljava/lang/String;)I", Downloads);
        public static readonly JniMember ResolverQuery = M(ContentResolver, "query", "(Landroid/net/Uri;[Ljava/lang/String;Ljava/lang/String;[Ljava/lang/String;Ljava/lang/String;)Landroid/database/Cursor;", Downloads);
        public static readonly JniMember CursorMoveToFirst = M(Cursor, "moveToFirst", "()Z", Downloads);
        public static readonly JniMember CursorGetString = M(Cursor, "getString", "(I)Ljava/lang/String;", Downloads);
        public static readonly JniMember CursorClose = M(Cursor, "close", "()V", Downloads);
        public static readonly JniMember PfdGetFd = M(Pfd, "getFd", "()I", Downloads);
        public static readonly JniMember PfdClose = M(Pfd, "close", "()V", Downloads);
        public static readonly JniMember UriParse = S(Uri, "parse", "(Ljava/lang/String;)Landroid/net/Uri;", Downloads);
        public static readonly JniMember UriToString = M(Uri, "toString", "()Ljava/lang/String;", Downloads);

        // ACTION_SEND and the chooser.
        public static readonly JniMember IntentNew = M(Intent, "<init>", "(Ljava/lang/String;)V", Send);
        public static readonly JniMember IntentSetType = M(Intent, "setType", "(Ljava/lang/String;)Landroid/content/Intent;", Send);
        public static readonly JniMember IntentPutParcelable = M(Intent, "putExtra", "(Ljava/lang/String;Landroid/os/Parcelable;)Landroid/content/Intent;", Send);
        public static readonly JniMember IntentPutString = M(Intent, "putExtra", "(Ljava/lang/String;Ljava/lang/String;)Landroid/content/Intent;", Send);
        public static readonly JniMember IntentSetClipData = M(Intent, "setClipData", "(Landroid/content/ClipData;)V", Send);
        public static readonly JniMember IntentCreateChooser = S(Intent, "createChooser", "(Landroid/content/Intent;Ljava/lang/CharSequence;)Landroid/content/Intent;", Send);
        public static readonly JniMember ClipNewRawUri = S(ClipData, "newRawUri", "(Ljava/lang/CharSequence;Landroid/net/Uri;)Landroid/content/ClipData;", Send);

        // file:// before Android 10.
        public static readonly JniMember UriFromFile = S(Uri, "fromFile", "(Ljava/io/File;)Landroid/net/Uri;", FileUri);
        public static readonly JniMember FileNew = M("java/io/File", "<init>", "(Ljava/lang/String;)V", FileUri);
        public static readonly JniMember StrictSetVmPolicy = S("android/os/StrictMode", "setVmPolicy", "(Landroid/os/StrictMode$VmPolicy;)V", StrictModeUse);
        public static readonly JniMember VmPolicyBuilderNew = M("android/os/StrictMode$VmPolicy$Builder", "<init>", "()V", StrictModeUse);
        public static readonly JniMember VmPolicyBuild = M("android/os/StrictMode$VmPolicy$Builder", "build", "()Landroid/os/StrictMode$VmPolicy;", StrictModeUse);

        // Clipboard, toast, the game's own text share.
        public static readonly JniMember ClipNewPlainText = S(ClipData, "newPlainText", "(Ljava/lang/CharSequence;Ljava/lang/CharSequence;)Landroid/content/ClipData;", Clipboard);
        public static readonly JniMember ClipboardSetPrimary = M("android/content/ClipboardManager", "setPrimaryClip", "(Landroid/content/ClipData;)V", Clipboard);
        public static readonly JniMember ToastMakeText = S("android/widget/Toast", "makeText", "(Landroid/content/Context;Ljava/lang/CharSequence;I)Landroid/widget/Toast;", Toast);
        public static readonly JniMember ToastShow = M("android/widget/Toast", "show", "()V", Toast);
        public static readonly JniMember ShareInfo = S(SUtils, "shareInfo", "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)V", ShareTextUse);

        // The game's own restart (MainActivity.java:107-115, 147-157, 315-318).
        public static readonly JniMember MainActivityInstance = F(MainActivity, "d", "Lcom/gameloft/android/MainActivity;", ExitGame);
        public static readonly JniMember MainActivityRestart = M(MainActivity, "a", "(Z)V", ExitGame);

        // The host's restart.
        public static readonly JniMember GetPackageManager = M(Context, "getPackageManager", "()Landroid/content/pm/PackageManager;", ExitHost);
        public static readonly JniMember GetPackageName = M(Context, "getPackageName", "()Ljava/lang/String;", ExitHost);
        public static readonly JniMember LaunchIntent = M("android/content/pm/PackageManager", "getLaunchIntentForPackage", "(Ljava/lang/String;)Landroid/content/Intent;", ExitHost);
        public static readonly JniMember PendingGetActivity = S("android/app/PendingIntent", "getActivity", "(Landroid/content/Context;ILandroid/content/Intent;I)Landroid/app/PendingIntent;", ExitHost);
        public static readonly JniMember AlarmSet = M("android/app/AlarmManager", "set", "(IJLandroid/app/PendingIntent;)V", ExitHost);
        public static readonly JniMember CurrentTimeMillis = S("java/lang/System", "currentTimeMillis", "()J", ExitHost);
        public static readonly JniMember MyPid = S("android/os/Process", "myPid", "()I", ExitHost);
        public static readonly JniMember KillProcess = S("android/os/Process", "killProcess", "(I)V", ExitHost);
        public static readonly JniMember FinishAndRemoveTask = M(Activity, "finishAndRemoveTask", "()V", ExitHost);

        // The log mirror's media scan, free space.
        public static readonly JniMember ScanFile = S("android/media/MediaScannerConnection", "scanFile",
            "(Landroid/content/Context;[Ljava/lang/String;[Ljava/lang/String;Landroid/media/MediaScannerConnection$OnScanCompletedListener;)V", Scan);
        public static readonly JniMember StatFsNew = M("android/os/StatFs", "<init>", "(Ljava/lang/String;)V", Storage);
        public static readonly JniMember StatFsAvailable = M("android/os/StatFs", "getAvailableBytes", "()J", Storage);

        // The one-time notification permission request (target-sdk.md WP-T5): only resolved on Android 13+, where it exists.
        // With GetPackageManager and GetPackageName of the host's restart group.
        public static readonly JniMember CheckSelfPermission = N(Context, "checkSelfPermission", "(Ljava/lang/String;)I", false);
        public static readonly JniMember RequestPermissions = N(Activity, "requestPermissions", "([Ljava/lang/String;I)V", false);
        public static readonly JniMember GetApplicationInfo = N(Context, "getApplicationInfo", "()Landroid/content/pm/ApplicationInfo;", false);
        public static readonly JniMember AppInfoTargetSdk = N("android/content/pm/ApplicationInfo", "targetSdkVersion", "I", true);
        public static readonly JniMember GetPackageInfo = N("android/content/pm/PackageManager", "getPackageInfo",
            "(Ljava/lang/String;I)Landroid/content/pm/PackageInfo;", false);
        public static readonly JniMember PackageInfoPermissions = N("android/content/pm/PackageInfo", "requestedPermissions", "[Ljava/lang/String;", true);

        /// <summary>Every entry, in declaration order.</summary>
        public static readonly JniMember[] Table =
        {
            GetSystemService, StartActivity, GetActivity, IntentAddFlags,
            IgbSetParameters, IgbOpenUrl, IgbQuit, IgbShown, IgbOpenWithPost,
            DownloadsUri, GetContentResolver, ValuesNew, ValuesPutString, ValuesPutInteger, IntegerValueOf, ResolverInsert, ResolverOpenFd,
            ResolverUpdate, ResolverDelete, ResolverQuery, CursorMoveToFirst, CursorGetString, CursorClose, PfdGetFd, PfdClose, UriParse, UriToString,
            IntentNew, IntentSetType, IntentPutParcelable, IntentPutString, IntentSetClipData, IntentCreateChooser, ClipNewRawUri,
            UriFromFile, FileNew, StrictSetVmPolicy, VmPolicyBuilderNew, VmPolicyBuild,
            ClipNewPlainText, ClipboardSetPrimary, ToastMakeText, ToastShow, ShareInfo,
            MainActivityInstance, MainActivityRestart,
            GetPackageManager, GetPackageName, LaunchIntent, PendingGetActivity, AlarmSet, CurrentTimeMillis, MyPid, KillProcess, FinishAndRemoveTask,
            ScanFile, StatFsNew, StatFsAvailable,
            CheckSelfPermission, RequestPermissions, GetApplicationInfo, AppInfoTargetSdk, GetPackageInfo, PackageInfoPermissions,
        };

        /// <summary>
        /// True when every entry of each group in <paramref name="uses"/> that applies to <paramref name="sdkInt"/> resolved
        /// (its key is not in <paramref name="missing"/>), and at least one entry of each group applies.
        /// </summary>
        public static bool Available(int sdkInt, ICollection<string> missing, params string[] uses)
        {
            foreach (string use in uses)
            {
                bool any = false;
                foreach (JniMember m in Table)
                {
                    if (m.Use != use || m.MinSdk > sdkInt) continue;
                    any = true;
                    if (missing != null && missing.Contains(m.Key)) return false;
                }
                if (!any) return false;
            }
            return true;
        }

        /// <summary>The groups a file share needs on this Android version (§8.6): MediaStore on 10+, file:// before.</summary>
        public static string[] ShareFileUses(int sdkInt)
        {
            if (sdkInt >= 29) return new[] { Common, Downloads, Send };
            if (sdkInt >= 24) return new[] { Common, Send, FileUri, StrictModeUse };
            return new[] { Common, Send, FileUri };
        }

        private static JniMember M(string cls, string name, string descriptor, string use)
        {
            return new JniMember(cls, name, descriptor, false, false, 0, use);
        }

        private static JniMember S(string cls, string name, string descriptor, string use)
        {
            return new JniMember(cls, name, descriptor, true, false, 0, use);
        }

        private static JniMember F(string cls, string name, string descriptor, string use)
        {
            return new JniMember(cls, name, descriptor, true, true, 0, use);
        }

        /// <summary>An instance method or instance field of the notification group, resolved on Android 13+ only.</summary>
        private static JniMember N(string cls, string name, string descriptor, bool isField)
        {
            return new JniMember(cls, name, descriptor, false, isField, Tiramisu, Notify);
        }

#if MCVS_EMBEDDED
        private static readonly object gate = new object();
        private static readonly Dictionary<string, IntPtr> classes = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        private static volatile List<string> missing;   // set last by Resolve: ids are complete once it is non-null
        private static int sdk;
        private static global::Android.OS.Handler uiHandler;

        /// <summary>Build.VERSION.SDK_INT as read by <see cref="Resolve"/>.</summary>
        public static int SdkInt
        {
            get { return sdk; }
        }

        /// <summary>
        /// Resolves the whole table once (class global references, member ids); later calls return the same list. Entries above
        /// the device's SDK are skipped. Never throws.
        /// </summary>
        public static List<string> Resolve()
        {
            lock (gate)
            {
                if (missing != null) return new List<string>(missing);
                var misses = new List<string>();
                try
                {
                    sdk = AndroidDiagnostics.SdkInt();
                }
                catch (Exception)
                {
                    sdk = 0;
                }
                foreach (JniMember m in Table)
                {
                    if (m.MinSdk > sdk) continue;
                    try
                    {
                        IntPtr cls = ClassRef(m.Class);
                        if (m.IsField)
                            m.Id = m.IsStatic ? JNIEnv.GetStaticFieldID(cls, m.Name, m.Descriptor) : JNIEnv.GetFieldID(cls, m.Name, m.Descriptor);
                        else
                            m.Id = m.IsStatic ? JNIEnv.GetStaticMethodID(cls, m.Name, m.Descriptor) : JNIEnv.GetMethodID(cls, m.Name, m.Descriptor);
                        if (m.Id == IntPtr.Zero) throw new InvalidOperationException("no id");
                        m.ClassRef = cls;
                    }
                    catch (Exception)
                    {
                        m.Id = IntPtr.Zero;
                        misses.Add(m.Key);
                    }
                }
                missing = misses;
                return new List<string>(missing);
            }
        }

        /// <summary>The class's global reference (cached for the process: ids stay valid while it is held).</summary>
        private static IntPtr ClassRef(string name)
        {
            IntPtr cls;
            if (classes.TryGetValue(name, out cls)) return cls;
            cls = JNIEnv.FindClass(name);   // a global reference
            if (cls == IntPtr.Zero) throw new InvalidOperationException("class " + name + " not found");
            classes[name] = cls;
            return cls;
        }

        /// <summary>The member's id; throws when it did not resolve (callers catch everything).</summary>
        private static IntPtr Id(JniMember m)
        {
            if (missing == null) Resolve();
            if (m.Id == IntPtr.Zero) throw new InvalidOperationException("JNI member missing: " + m.Key);
            return m.Id;
        }

        public static bool Has(JniMember m)
        {
            if (missing == null) Resolve();
            return m.Id != IntPtr.Zero;
        }

        // ---------------------------------------------------------------- calls (local references are the caller's to delete)

        public static void CallStaticVoid(JniMember m, params JValue[] args)
        {
            IntPtr id = Id(m);
            JNIEnv.CallStaticVoidMethod(m.ClassRef, id, args);
        }

        public static IntPtr CallStaticObject(JniMember m, params JValue[] args)
        {
            IntPtr id = Id(m);
            return JNIEnv.CallStaticObjectMethod(m.ClassRef, id, args);
        }

        public static int CallStaticInt(JniMember m, params JValue[] args)
        {
            IntPtr id = Id(m);
            return JNIEnv.CallStaticIntMethod(m.ClassRef, id, args);
        }

        public static long CallStaticLong(JniMember m, params JValue[] args)
        {
            IntPtr id = Id(m);
            return JNIEnv.CallStaticLongMethod(m.ClassRef, id, args);
        }

        public static void CallVoid(IntPtr target, JniMember m, params JValue[] args)
        {
            IntPtr id = Id(m);
            JNIEnv.CallVoidMethod(target, id, args);
        }

        public static IntPtr CallObject(IntPtr target, JniMember m, params JValue[] args)
        {
            IntPtr id = Id(m);
            return JNIEnv.CallObjectMethod(target, id, args);
        }

        public static int CallInt(IntPtr target, JniMember m, params JValue[] args)
        {
            IntPtr id = Id(m);
            return JNIEnv.CallIntMethod(target, id, args);
        }

        public static long CallLong(IntPtr target, JniMember m, params JValue[] args)
        {
            IntPtr id = Id(m);
            return JNIEnv.CallLongMethod(target, id, args);
        }

        public static bool CallBool(IntPtr target, JniMember m, params JValue[] args)
        {
            IntPtr id = Id(m);
            return JNIEnv.CallBooleanMethod(target, id, args);
        }

        public static IntPtr New(JniMember m, params JValue[] args)
        {
            IntPtr id = Id(m);
            return JNIEnv.NewObject(m.ClassRef, id, args);
        }

        public static IntPtr GetStaticObject(JniMember m)
        {
            IntPtr id = Id(m);
            return JNIEnv.GetStaticObjectField(m.ClassRef, id);
        }

        public static bool GetStaticBool(JniMember m)
        {
            IntPtr id = Id(m);
            return JNIEnv.GetStaticBooleanField(m.ClassRef, id);
        }

        /// <summary>An instance int field.</summary>
        public static int GetInt(IntPtr target, JniMember m)
        {
            IntPtr id = Id(m);
            return JNIEnv.GetIntField(target, id);
        }

        /// <summary>An instance object field (a new local reference, the caller's to delete).</summary>
        public static IntPtr GetObject(IntPtr target, JniMember m)
        {
            IntPtr id = Id(m);
            return JNIEnv.GetObjectField(target, id);
        }

        /// <summary>A new local reference to a Java string (IntPtr.Zero for null).</summary>
        public static IntPtr Str(string text)
        {
            return text == null ? IntPtr.Zero : JNIEnv.NewString(text);
        }

        /// <summary>A Java string result as a managed string (the local reference is released).</summary>
        public static string TakeString(IntPtr local)
        {
            return local == IntPtr.Zero ? null : JNIEnv.GetString(local, JniHandleOwnership.TransferLocalRef);
        }

        public static void Delete(IntPtr local)
        {
            if (local == IntPtr.Zero) return;
            try
            {
                JNIEnv.DeleteLocalRef(local);
            }
            catch (Exception)
            {
            }
        }

        public static void DeleteGlobal(IntPtr global)
        {
            if (global == IntPtr.Zero) return;
            try
            {
                JNIEnv.DeleteGlobalRef(global);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>The application Context (a global reference owned by Mono.Android: never delete it).</summary>
        public static IntPtr AppContext()
        {
            return global::Android.App.Application.Context.Handle;
        }

        /// <summary>The action was not posted (it will never run).</summary>
        public const int UiNotPosted = UiCall.NotPosted;

        /// <summary>Posted without a wait (it runs when the UI thread gets to it), or it started and had not ended within the wait.</summary>
        public const int UiPending = UiCall.Pending;

        /// <summary>It ran without an exception.</summary>
        public const int UiDone = UiCall.Done;

        /// <summary>It ran and threw (reported through the failure callback).</summary>
        public const int UiFailed = UiCall.Failed;

        /// <summary>The UI thread had not started it when the wait ended: withdrawn, it will never run.</summary>
        public const int UiWithdrawn = UiCall.Withdrawn;

        /// <summary>
        /// Runs <paramref name="action"/> on the UI thread (the main looper's Handler). With <paramref name="waitMs"/> &gt; 0 waits up
        /// to that long for it and withdraws it when the UI thread has not started it by then (<see cref="UiCall"/>): nothing runs
        /// late behind the caller's back. Returns <see cref="UiNotPosted"/>, <see cref="UiPending"/>, <see cref="UiDone"/>,
        /// <see cref="UiFailed"/> or <see cref="UiWithdrawn"/>. JNI local references belong to one thread: the action makes its own
        /// (or uses global references made for it, which it then deletes; the caller deletes them when it never runs).
        /// </summary>
        public static int RunOnUiThread(Action action, int waitMs, Action<Exception> failed)
        {
            try
            {
                global::Android.OS.Handler handler;
                lock (gate)
                {
                    if (uiHandler == null) uiHandler = new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper);
                    handler = uiHandler;
                }
                var call = new UiCall(action, failed);
                if (!handler.Post(call.Run)) return UiNotPosted;
                return call.Wait(waitMs);
            }
            catch (Exception ex)
            {
                try
                {
                    if (failed != null) failed(ex);
                }
                catch (Exception)
                {
                }
                return UiNotPosted;
            }
        }
#endif
    }
}
