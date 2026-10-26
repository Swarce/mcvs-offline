using System;
using System.Collections.Generic;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Tests.Admin
{
    /// <summary>
    /// An <see cref="IAdminHost"/> that records every call in order (<see cref="Calls"/>) and answers with configurable
    /// results. Shared by the admin test packages (owned by WP2; other packages use it without editing it).
    /// <para>
    /// <see cref="IAdminHost.Describe"/>, <see cref="IAdminHost.PageAlive"/> and <see cref="IAdminHost.RecentProblems"/>
    /// are called on almost every request, so the first two are counted (<see cref="DescribeCount"/>,
    /// <see cref="PageAliveCount"/>) and none of them is put into <see cref="Calls"/>;
    /// the list then shows the order of the actions (toast, close, wait, save, open, exit...). Tests may add their own
    /// entries with <see cref="Record"/> (e.g. a recording lock or flush) to check the order across both.
    /// </para>
    /// Entries: <c>ShowToast:&lt;text&gt;</c>, <c>CloseBrowser</c>, <c>WaitForGameLoop:&lt;ms&gt;</c>, <c>RequestGameSave</c>,
    /// <c>OpenPage:&lt;trigger&gt;</c> (URL in <see cref="OpenedUrls"/>), <c>Exit:&lt;reason&gt;:&lt;relaunch&gt;</c>,
    /// <c>ShareFile:&lt;mime&gt;:&lt;displayName&gt;</c>, <c>ShareText:&lt;subject&gt;</c>, <c>CopyToClipboard:&lt;label&gt;</c>.
    /// </summary>
    public sealed class FakeAdminHost : IAdminHost
    {
        private readonly object gate = new object();
        private readonly List<string> calls = new List<string>();
        private readonly List<string> openedUrls = new List<string>();
        private readonly List<string> toasts = new List<string>();
        private readonly List<string> clipboard = new List<string>();
        private readonly List<string[]> sharedFiles = new List<string[]>();
        private int pageAlive;
        private int describe;

        public FakeAdminHost()
        {
            Info = new AdminHostInfo
            {
                Platform = "fake",
                SdkInt = 34,
                DeviceId = "fake-device",
                GameCredential = null,
                GameRoot = null,
                LogsDir = null,
                FallbackLogsDir = null,
                PublicLogsDir = null,
                MirrorStatus = "off",
                WatchdogStatus = "off",
                BrowserStatus = "initialized",
                BrowserOpen = false,
                FreeBytes = -1,
                RestartVia = "game",
                MissingJni = new List<string>(),
                CanOpenPage = true,
                CanShareFile = true,
                CanSaveToDownloads = true,
                CanShareText = true,
                CanCopy = true,
                CanToast = true,
                CanSave = true,
                CanExit = true,
            };
            OpenPageResult = true;
            CloseBrowserResult = true;
            WaitForGameLoopResult = true;
            RequestGameSaveResult = true;
            ExitResult = true;
            CopyResult = true;
            ShareFileResult = new AdminShareResult { Ok = true, Method = "sheet", PublicPath = null, Message = "Shared." };
            ShareTextResult = new AdminShareResult { Ok = true, Method = "text", Message = "Shared." };
            Problems = new List<string>();
        }

        /// <summary>What <see cref="Describe"/> returns (the same instance: tests may change it at any time).</summary>
        public AdminHostInfo Info { get; set; }

        public bool OpenPageResult { get; set; }

        public bool CloseBrowserResult { get; set; }

        public bool WaitForGameLoopResult { get; set; }

        public bool RequestGameSaveResult { get; set; }

        public bool ExitResult { get; set; }

        public bool CopyResult { get; set; }

        public AdminShareResult ShareFileResult { get; set; }

        public AdminShareResult ShareTextResult { get; set; }

        /// <summary>Host log lines returned (newest first) by <see cref="RecentProblems"/>.</summary>
        public List<string> Problems { get; set; }

        /// <summary>Runs inside <see cref="RequestGameSave"/> (e.g. a test publishes the game's save from here).</summary>
        public Action OnRequestGameSave { get; set; }

        /// <summary>Runs inside <see cref="OpenPage"/> with (url, trigger).</summary>
        public Action<string, string> OnOpenPage { get; set; }

        /// <summary>Runs inside <see cref="Exit"/> with (reason, relaunch).</summary>
        public Action<string, bool> OnExit { get; set; }

        public List<string> Calls
        {
            get { lock (gate) return new List<string>(calls); }
        }

        public List<string> OpenedUrls
        {
            get { lock (gate) return new List<string>(openedUrls); }
        }

        public List<string> Toasts
        {
            get { lock (gate) return new List<string>(toasts); }
        }

        /// <summary>Texts passed to <see cref="CopyToClipboard"/>.</summary>
        public List<string> Clipboard
        {
            get { lock (gate) return new List<string>(clipboard); }
        }

        /// <summary>Arguments of every <see cref="ShareFile"/> call: path, mimeType, displayName, subject, text.</summary>
        public List<string[]> SharedFiles
        {
            get { lock (gate) return new List<string[]>(sharedFiles); }
        }

        public int PageAliveCount
        {
            get { lock (gate) return pageAlive; }
        }

        public int DescribeCount
        {
            get { lock (gate) return describe; }
        }

        /// <summary>Appends an entry to <see cref="Calls"/> (also used by tests for their own recording fakes).</summary>
        public void Record(string call)
        {
            lock (gate) calls.Add(call);
        }

        public void ClearCalls()
        {
            lock (gate)
            {
                calls.Clear();
                openedUrls.Clear();
                toasts.Clear();
            }
        }

        // ------------------------------------------------------------------ IAdminHost

        public AdminHostInfo Describe()
        {
            lock (gate) describe++;
            return Info;
        }

        public bool OpenPage(string url, string trigger)
        {
            lock (gate)
            {
                calls.Add("OpenPage:" + trigger);
                openedUrls.Add(url);
            }
            var hook = OnOpenPage;
            if (hook != null) hook(url, trigger);
            return OpenPageResult;
        }

        public AdminShareResult ShareFile(string path, string mimeType, string displayName, string subject, string text)
        {
            lock (gate)
            {
                calls.Add("ShareFile:" + mimeType + ":" + displayName);
                sharedFiles.Add(new[] { path, mimeType, displayName, subject, text });
            }
            return ShareFileResult;
        }

        public AdminShareResult ShareText(string subject, string text)
        {
            Record("ShareText:" + subject);
            return ShareTextResult;
        }

        public bool CopyToClipboard(string label, string text)
        {
            lock (gate)
            {
                calls.Add("CopyToClipboard:" + label);
                clipboard.Add(text);
            }
            return CopyResult;
        }

        public void ShowToast(string text)
        {
            lock (gate)
            {
                calls.Add("ShowToast:" + text);
                toasts.Add(text);
            }
        }

        public bool CloseBrowser()
        {
            Record("CloseBrowser");
            return CloseBrowserResult;
        }

        public bool WaitForGameLoop(int timeoutMs)
        {
            Record("WaitForGameLoop:" + timeoutMs);
            return WaitForGameLoopResult;
        }

        public bool RequestGameSave()
        {
            Record("RequestGameSave");
            var hook = OnRequestGameSave;
            if (hook != null) hook();
            return RequestGameSaveResult;
        }

        public bool Exit(string reason, bool relaunch)
        {
            Record("Exit:" + reason + ":" + relaunch);
            var hook = OnExit;
            if (hook != null) hook(reason, relaunch);
            return ExitResult;
        }

        public void PageAlive()
        {
            lock (gate) pageAlive++;
        }

        public List<string> RecentProblems(int max)
        {
            var list = new List<string>();
            foreach (string line in Problems ?? new List<string>())
            {
                if (list.Count >= max) break;
                list.Add(line);
            }
            return list;
        }
    }
}
