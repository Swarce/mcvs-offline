using System;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>
    /// When the game last saved each account in this process: subscribes to storage.profile.changed and keeps the saves
    /// with source "http" only (the game's own requests; admin and in-process writes are ignored).
    /// </summary>
    public sealed class GameSaveTracker
    {
        /// <summary>This process only; null when the game has not saved the account since the server started.</summary>
        public DateTime? LastGameSave(string accountId)
        {
            throw new NotImplementedException();
        }

        /// <summary>True when a save of accountId began after sinceUtc and none followed for quietMs; false on timeout.</summary>
        public bool WaitForSave(string accountId, DateTime sinceUtc, int timeoutMs, int quietMs)
        {
            throw new NotImplementedException();
        }
    }
}
