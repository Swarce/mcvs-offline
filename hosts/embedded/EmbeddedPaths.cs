// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.IO;

namespace McvsOffline.Embedded
{
    /// <summary>
    /// Where the embedded server keeps things, all relative to the game root (<c>GlfRootPath</c>): the folder that
    /// holds the downloaded <c>Data/</c> and the args.txt the native engine reads (verify-C1 §1.1). On current devices
    /// it is <c>/storage/emulated/0/Android/data/com.gameloft.android.ANMP.GloftMVHM/files</c>.
    /// <code>
    /// &lt;root&gt;/Data/                      the player's data pack (read-only for us)
    /// &lt;root&gt;/args.txt                   launch arguments (managed block + the player's own lines)
    /// &lt;root&gt;/McvsOffline/               server data directory: settings.json, accounts, profiles, ...
    /// &lt;root&gt;/McvsOffline/logs/          server.log, unknown-endpoints.jsonl, bootstrap.log
    /// &lt;root&gt;/McvsOffline/device-id.txt
    /// </code>
    /// The folder is visible over USB/adb, so players can back it up; Android deletes it when the app is uninstalled.
    /// </summary>
    public sealed class EmbeddedPaths
    {
        public const string DataFolderName = "McvsOffline";

        public EmbeddedPaths(string gameRoot)
        {
            if (string.IsNullOrEmpty(gameRoot)) throw new ArgumentException("game root required", "gameRoot");
            GameRoot = Path.GetFullPath(gameRoot);
        }

        public string GameRoot { get; private set; }

        public string DataPackDir { get { return Path.Combine(GameRoot, "Data"); } }

        public string ArgsFile { get { return Path.Combine(GameRoot, McvsOffline.Embedded.ArgsFile.FileName); } }

        /// <summary>The server's data directory (ServerOptions.DataDir).</summary>
        public string DataDir { get { return Path.Combine(GameRoot, DataFolderName); } }

        public string LogsDir { get { return Path.Combine(DataDir, "logs"); } }

        public string BootstrapLog { get { return Path.Combine(LogsDir, "bootstrap.log"); } }

        public string SettingsFile { get { return Path.Combine(DataDir, McvsOffline.Server.Core.Settings.FileName); } }
    }
}
