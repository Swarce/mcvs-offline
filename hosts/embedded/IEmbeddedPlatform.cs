// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Embedded
{
    /// <summary>The few things <see cref="EmbeddedHost"/> needs from the device.</summary>
    public interface IEmbeddedPlatform
    {
        /// <summary>Short name for the log ("android", "directory").</summary>
        string Name { get; }

        /// <summary>
        /// The game root: the directory the native engine resolves as GlfRootPath, where Data/ and args.txt live.
        /// Throws when it cannot be determined.
        /// </summary>
        string ResolveGameRoot(Log log);

        /// <summary>A sink for the platform's system log (logcat on Android), or null.</summary>
        LogSink CreateSystemLogSink(LogLevel minLevel);
    }

    /// <summary>
    /// Optional platform diagnostics (Android: the AndroidEnvironment crash hook, internal storage for log fallback, the
    /// game's own log levels, Java stacks, the phone-readable copy of the logs and the previous exit reasons). Every member
    /// must catch everything.
    /// </summary>
    public interface IPlatformDiagnostics
    {
        /// <summary>Crash hooks beyond AppDomain (called once, right after <see cref="Diagnostics.CrashReporter.Install"/>).</summary>
        void InstallCrashHooks();

        /// <summary>A directory on storage that is always writable, for log files whose own directory fails; or null.</summary>
        string FallbackLogDirectory();

        /// <summary>Applies settings.json "gameLog" to the game's logger; copies its entries to <paramref name="gameLog"/>. A description, or null.</summary>
        string ApplyGameLog(Settings settings, SafeLogFile gameLog);

        /// <summary>Java stacks of the UI thread and the threads named like <paramref name="gameThreadComm"/>, or null.</summary>
        string JavaStacks(string gameThreadComm);

        /// <summary>Background extras once the host runs (the copy of the logs on the phone, the previous exit reasons).</summary>
        void StartBackground(EmbeddedHost host);

        /// <summary>Extra state for the status line (Android: whether the native IAP library reports initialized), or null.</summary>
        string StatusExtras();
    }

    /// <summary>
    /// Optional: what <see cref="Install.InstallerData"/> needs to prepare the Java installer's state before it runs (Android:
    /// getFilesDir() and the installer's SharedPreferences through the game's own SUtils). Every member must catch everything.
    /// </summary>
    public interface IInstallerPlatform
    {
        /// <summary>Context.getFilesDir() (GameInstaller.SaveFolder), or null.</summary>
        string InternalFilesDir();

        /// <summary>The installer's preferences (MainActivityPrefs), or null.</summary>
        Install.IInstallerPrefs InstallerPrefs();

        /// <summary>Starts logging the installer's progress (state machine, errors) while it runs; diagnostics only.</summary>
        void MonitorInstaller(Log log, BootTrace boot);

        /// <summary>
        /// Whether the Java installer has handed over to the game (GameInstaller.sbStarted, or its state 21): 1 yes, 0 not yet,
        /// -1 cannot tell (its class or fields cannot be read). Reads fields only; never throws.
        /// </summary>
        int InstallerHandedOver();
    }

    /// <summary>
    /// Optional: what the settings page needs from the device (design §8.2; Android: <c>AndroidPlatform</c> over the JNI table of
    /// <c>AndroidJni</c>). <see cref="Admin.AdminHost"/> decides when and in which order; the platform only performs. Every member
    /// is thread-safe, catches everything and returns a failure result instead of throwing.
    /// </summary>
    public interface IAdminPlatform
    {
        /// <summary>Build.VERSION.SDK_INT.</summary>
        int SdkInt { get; }

        /// <summary>InGameBrowser.SetParameters("EN") + OpenURL(url, true). False: not called or failed.</summary>
        bool OpenInGameBrowserJni(string url);

        /// <summary>InGameBrowser.SetParameters("EN") + OpenURLWithPostData(url, postData).</summary>
        bool OpenInGameBrowserWithPostData(string url, string postData);

        /// <summary>InGameBrowser.b (public static boolean: a browser is shown).</summary>
        bool IsInGameBrowserOpen();

        /// <summary>InGameBrowser.QuitIGB() when a browser is shown. False: none was open, or the call failed.</summary>
        bool QuitInGameBrowser();

        /// <summary>Copies path into Download/MCVS-Offline/displayName and, unless subject is null, starts the share chooser.</summary>
        AdminShareResult ShareFile(string path, string mimeType, string displayName, string subject, string text);

        /// <summary>
        /// The game's own SUtils.shareInfo(subject, text, title) on the UI thread, within a second: Ok "text" once it ran, Ok false
        /// with Method "text" when it started but had not ended (the sheet may still open), "none" when it never runs.
        /// </summary>
        AdminShareResult ShareText(string subject, string text);

        /// <summary>
        /// ClipboardManager.setPrimaryClip(ClipData.newPlainText(label, text)) on the UI thread, within a second. True once it ran;
        /// false otherwise (a copy the UI thread had not started is withdrawn).
        /// </summary>
        bool CopyToClipboard(string label, string text);

        /// <summary>Toast.makeText(context, text, LENGTH_LONG).show() on the UI thread.</summary>
        void ShowToast(string text);

        /// <summary>
        /// MainActivity.d.a(relaunch) on the UI thread (§8.8). True: it ran or is running. False: it never runs (no MainActivity,
        /// it threw, or the UI thread had not started it within 2 s and it was withdrawn), so the host's restart may follow at once.
        /// </summary>
        bool ExitViaGame(bool relaunch);

        /// <summary>Relaunch alarm (when relaunch), finishAndRemoveTask, killProcess 200 ms later, on the UI thread (§8.8).</summary>
        bool ExitViaHost(bool relaunch);

        /// <summary>Environment.getExternalStoragePublicDirectory("Download") + "/MCVS-Offline", or null.</summary>
        string PublicDownloadsDir();

        /// <summary>StatFs.getAvailableBytes of path's volume, -1 on failure.</summary>
        long FreeBytes(string path);

        /// <summary>AndroidJni table entries that did not resolve ("class.name descriptor").</summary>
        List<string> MissingJniMembers();
    }

    /// <summary>A fixed directory as game root (tests, and running the embedded host on a PC).</summary>
    public sealed class DirectoryPlatform : IEmbeddedPlatform
    {
        private readonly string root;
        private readonly bool console;

        public DirectoryPlatform(string root, bool console)
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentException("root required", "root");
            this.root = root;
            this.console = console;
        }

        public string Name { get { return "directory"; } }

        public string ResolveGameRoot(Log log)
        {
            Directory.CreateDirectory(root);
            return Path.GetFullPath(root);
        }

        public LogSink CreateSystemLogSink(LogLevel minLevel)
        {
            return console ? new ConsoleLogSink(minLevel) : null;
        }
    }
}
