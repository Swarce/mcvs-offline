// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.IO;
using McvsOffline.Server.Core;

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
