#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL.
using System;
using System.Diagnostics;
using System.Threading;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>
    /// Opens a URL in the player's default browser (docs/design/pc-port.md §6.3, §6.5): <c>Process.Start</c> with ShellExecute on
    /// a new background STA thread (<c>mcvs-browser</c>), and returns at once. On .NET Framework a ShellExecute from a non-STA
    /// thread runs on an STA helper thread that the caller joins, so a direct call would hold the game thread (or the server
    /// thread) for as long as the browser takes to launch. "Started" is all <see cref="Open"/> can say; a later failure is one
    /// WARN line without the URL (it carries the page's one-time ticket).
    /// </summary>
    public sealed class SystemBrowser
    {
        public const string ThreadName = "mcvs-browser";

        private readonly Action<string> launcher;
        private readonly Action<string> warn;

        /// <param name="launcher">Starts the browser (tests inject one); null: ShellExecute of the URL.</param>
        /// <param name="warn">Where a failure goes; may be null.</param>
        public SystemBrowser(Action<string> launcher, Action<string> warn)
        {
            this.launcher = launcher ?? ShellExecute;
            this.warn = warn;
        }

        /// <summary>Whether the last launcher thread got the STA apartment it asked for (false off Windows).</summary>
        public bool LastThreadWasSta { get; private set; }

        /// <summary>Starts the launch on its own thread; true once that thread started. Never throws.</summary>
        public bool Open(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            try
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        launcher(url);
                    }
                    catch (Exception ex)
                    {
                        Warn("admin: the system browser did not start: " + ex.GetType().Name + ": " + WithoutUrl(ex.Message, url));
                    }
                });
                thread.IsBackground = true;
                thread.Name = ThreadName;
                // The .NET Framework's ShellExecute wants an STA thread; off Windows the request is refused (false).
                LastThreadWasSta = thread.TrySetApartmentState(ApartmentState.STA);
                thread.Start();
                return true;
            }
            catch (Exception ex)
            {
                Warn("admin: the system browser did not start: " + ex.GetType().Name + ": " + WithoutUrl(ex.Message, url));
                return false;
            }
        }

        private static void ShellExecute(string url)
        {
            using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }))
            {
            }
        }

        /// <summary>The message with the URL (and its part before the fragment) replaced: the ticket never reaches a log.</summary>
        internal static string WithoutUrl(string message, string url)
        {
            if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(url)) return message ?? "";
            string text = message.Replace(url, "<page url>");
            int hash = url.IndexOf('#');
            if (hash > 0) text = text.Replace(url.Substring(hash), "");
            return text;
        }

        private void Warn(string text)
        {
            try
            {
                if (warn != null) warn(text);
            }
            catch (Exception)
            {
            }
        }
    }
}
#endif
