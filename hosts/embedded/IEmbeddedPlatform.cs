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
