using System;
using System.Collections.Generic;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>Trigger names (logs, status, rate-limit buckets).</summary>
    public static class AdminTriggers
    {
        public const string Account = "account";   // Settings > Account button (patch 'adminpage')
        public const string News = "news";         // inbox marker link (patch 'adminlink')
        public const string Redeem = "redeem";     // admin.redeemCode typed in Redeem code
        public const string Forum = "forum";       // patch 'forum'
        public const string Resume = "resume";     // reopened after "Save the game now"
        public const string Console = "console";   // standalone host
        public const string Browser = "browser";   // the page's "Open in the system browser" action (PC, docs/design/pc-port.md §6.5)
        public const string Test = "test";
    }

    /// <summary>
    /// What the page needs from the program hosting the server. Registered by the host with
    /// context.Features.Set&lt;IAdminHost&gt;(...) after McvsServer.Create and before Start; absent in the standalone host.
    /// Every member is thread-safe and never throws. Members return within a second, except ShareFile (10 s),
    /// WaitForGameLoop (its timeout) and Exit (returns once the exit has started; the process ends within ~2 s).
    /// </summary>
    public interface IAdminHost
    {
        AdminHostInfo Describe();
        /// <summary>Opens the in-game browser at url (posted to the game thread). False: cannot.</summary>
        bool OpenPage(string url, string trigger);
        /// <summary>Copies path into Download/MCVS-Offline/displayName and, unless subject is null, opens the share sheet.</summary>
        AdminShareResult ShareFile(string path, string mimeType, string displayName, string subject, string text);
        AdminShareResult ShareText(string subject, string text);
        bool CopyToClipboard(string label, string text);
        /// <summary>A short system toast (shown over the game after the browser closed). No-op where unsupported.</summary>
        void ShowToast(string text);
        /// <summary>Closes the in-game browser (InGameBrowser.QuitIGB on the UI thread). False: none open, or unsupported.</summary>
        bool CloseBrowser();
        /// <summary>True once the game thread has run a frame after this call; false on timeout or unsupported.</summary>
        bool WaitForGameLoop(int timeoutMs);
        /// <summary>Asks the game to save the player's profile (ForceSaveProfile, posted to the game thread). False: cannot.</summary>
        bool RequestGameSave();
        /// <summary>Ends the process through the game's own restart path (relaunch) or the host fallback (§8.8). False: nothing happened.</summary>
        bool Exit(string reason, bool relaunch);
        /// <summary>Called on every request of a valid session: the page is open, keep the watchdog paused.</summary>
        void PageAlive();
        /// <summary>WARN/ERROR lines of the host logger since boot, newest first, formatted like LogEntry.Format.</summary>
        List<string> RecentProblems(int max);
    }

    public sealed class AdminHostInfo
    {
        public string Platform { get; set; }          // "android", "windows", "directory"
        public int SdkInt { get; set; }               // Build.VERSION.SDK_INT; 0 off Android
        public string DeviceId { get; set; }
        public string GameCredential { get; set; }    // "anonymous:<name>" as the game uses it, read on the game thread; null unknown
        public string GameRoot { get; set; }
        public string LogsDir { get; set; }
        public string FallbackLogsDir { get; set; }   // internal-storage fallback of the log files, or null
        public DateTime? StartedUtc { get; set; }     // when this process started (its log files begin then: the report's previous start), or null unknown
        public string PublicLogsDir { get; set; }     // .../Download/MCVS-Offline, or null
        public string MirrorStatus { get; set; }      // one line
        public string WatchdogStatus { get; set; }    // one line
        public string BrowserStatus { get; set; }     // "initialized", "not initialized", "unknown"
        public bool BrowserOpen { get; set; }         // InGameBrowser.b
        public long FreeBytes { get; set; }           // free space of the game root's volume, -1 unknown
        public string RestartVia { get; set; }        // "game", "host", "none"
        public List<string> MissingJni { get; set; }  // JNI table entries that did not resolve (§8.6)
        public bool CanOpenPage { get; set; }
        public bool CanShareFile { get; set; }
        public bool CanSaveToDownloads { get; set; }  // SdkInt >= 29
        public bool CanShareText { get; set; }
        public bool CanCopy { get; set; }
        public bool CanToast { get; set; }
        public bool CanSave { get; set; }             // RequestGameSave can work
        public bool CanExit { get; set; }
        public bool RelaunchUnconfirmed { get; set; }     // Android 13+: the restart alarm may not bring the game back; not yet observed (target-sdk.md §4.6, STATUS device-test item 9). Windows: until the relaunch helper is confirmed
        public bool CanOpenInBrowser { get; set; }    // the page can be reopened in the system browser (Windows, actions/open-in-browser)
        public string Notifications { get; set; }     // the POST_NOTIFICATIONS state, one sentence; null off Android
        public string Graphics { get; set; }          // graphics.profile and the engine's GPU profile, one line; null unknown
        public string HdTextures { get; set; }        // the HD texture pack (hosts/embedded/Hd/HdInstaller), one line; null unknown
        public bool HdInstalled { get; set; }         // some Data/ files hold HD textures (the page offers "Remove HD textures")
    }

    public sealed class AdminShareResult
    {
        public bool Ok { get; set; }
        public string Method { get; set; }            // "sheet", "downloads", "text", "none"; Windows: "explorer", "file"
        public string PublicPath { get; set; }        // "Download/MCVS-Offline/<name>", Windows "McvsOffline\exports\<name>", or null
        public string Message { get; set; }           // one sentence for the player

        public static AdminShareResult None(string message)
        {
            return new AdminShareResult { Ok = false, Method = "none", Message = message };
        }
    }

    /// <summary>Provided by the admin module: context.Features.Get&lt;IAdminPortal&gt;(). Thread-safe, never throws.</summary>
    public interface IAdminPortal
    {
        /// <summary>admin.enabled and the module is registered.</summary>
        bool Enabled { get; }
        /// <summary>"http://127.0.0.1:&lt;port&gt;/_mcvs/ui/#t=&lt;one-time ticket&gt;[&amp;s=&lt;screen&gt;]", or null when disabled.</summary>
        string MintPageUrl(string trigger, string screen);
        /// <summary>The fixed inbox link "http://127.0.0.1:&lt;port&gt;/_mcvs/ui/#news". Carries no secret.</summary>
        string PageMarkerUrl { get; }
        /// <summary>Ordinal equality with PageMarkerUrl; false when disabled.</summary>
        bool IsPageMarker(string url);
        /// <summary>MintPageUrl + IAdminHost.OpenPage; false without a host or when disabled.</summary>
        bool OpenPage(string trigger, string screen);
    }
}
