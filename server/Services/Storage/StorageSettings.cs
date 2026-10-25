using McvsOffline.Server.Core;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>
    /// The module's keys in settings.json (all optional; read on every use so edits apply without a restart):
    /// <code>
    /// "storage": {
    ///   "backups": { "keep": 20, "intervalMinutes": 30 },
    ///   "autoRestoreCorrupt": true,
    ///   "absentField": "empty",
    ///   "cache": true
    /// }
    /// </code>
    /// </summary>
    internal sealed class StorageSettings
    {
        private readonly Settings settings;

        public StorageSettings(Settings settings)
        {
            this.settings = settings ?? new Settings();
        }

        /// <summary>Backups kept per account (0 disables backups).</summary>
        public int BackupsKeep
        {
            get { return (int)Num.Clamp(settings.GetLong("storage.backups.keep", 20), 0, 1000); }
        }

        /// <summary>Minimum minutes between two automatic backups of an account (0: before every write).</summary>
        public long BackupIntervalMinutes
        {
            get { return Num.Clamp(settings.GetLong("storage.backups.intervalMinutes", 30), 0, 7L * 24 * 60); }
        }

        /// <summary>Replace an unreadable profile file by its newest readable backup (the broken file is kept aside).</summary>
        public bool AutoRestoreCorrupt
        {
            get { return settings.GetBool("storage.autoRestoreCorrupt", true); }
        }

        /// <summary>
        /// Answer for a selector GET of an absent field: "empty" (200 {}, default) or "404". The game-critical
        /// selectors (_mcvs.gameProfile, _mcvs.currentMatch, the object sections) always get 200 {} (verify-C3 §7.2).
        /// </summary>
        public bool AbsentFieldIsNotFound
        {
            get { return settings.GetString("storage.absentField", "empty") == "404"; }
        }

        /// <summary>Keep parsed profiles in memory (validated against the file on every request).</summary>
        public bool CacheEnabled
        {
            get { return settings.GetBool("storage.cache", true); }
        }

        public string DataCenter
        {
            get { return settings.DataCenter; }
        }
    }
}
