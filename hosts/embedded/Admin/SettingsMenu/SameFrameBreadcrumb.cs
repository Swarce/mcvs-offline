// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Embedded.Admin.SettingsMenu
{
    /// <summary>
    /// The automatic fallback of our Flash work inside the game's natives (build 22; docs/design/settings-menu.md §3.2, §5 risk 2):
    /// the Android dress before <c>setup()</c> (<see cref="SettingsMenuLayout.PreDress"/>, inside EnterSection's native) and the
    /// same-frame step (<see cref="SettingsMenuLayout.SameFrame"/>, inside GetPlatformId's or GetLanguage's) re-enter ActionScript
    /// from a native of libnativeengine/gameswf. A native fault there cannot be caught, so a process that dies in it would die again
    /// at every visit to Settings. A small marker file (<see cref="ISettingsMenuHost.SameFrameMarkerPaths"/>: the host's log folder,
    /// next to <c>restart-requested.txt</c>, else the fallback log folder) records that such work is under way:
    /// <list type="bullet">
    /// <item><see cref="Enter"/>: inside the native, right before the process's first Flash access there (Android: PreDress's first
    /// read; Steam: the first look). Once per process: every later focus-in goes through the same natives, and a file write on the
    /// game thread per return is not worth it. Temp file and rename (the marker exists whole or not at all), no flush to disk (a
    /// crash of the process leaves the page cache to the kernel; only a power cut could lose it).</item>
    /// <item><see cref="LeaveNextFrame"/>: the run's first frame (one field read when nothing is outstanding) posts
    /// <see cref="Leave"/> to the frame after it. The process has then lived through the native call, the rest of the focus-in
    /// frame and its render also with the render thread on: GameApplication.Render starts the frame's World.RenderAsync and returns
    /// (GameApplication.cs:923), and only the next frame's Render waits for it (:900), so the run's first frame can begin while
    /// the focus-in frame is still being drawn; the frame after it cannot. The marker exists for about two frames.</item>
    /// <item><see cref="Check"/>: once per process, at start (<see cref="UiNatives.Start"/>, off the game thread) or at the first
    /// focus-in when the start did not run it: a marker found there was left by a process that ended inside that frame. The work is
    /// then switched off for this install the way the owner would do it, <c>admin.settingsMenu.sameFrame</c> = false in
    /// settings.json (the settings page shows the switch off; turning it back on retries), every marker is deleted, and one WARN
    /// line names the marker and the switch (the status page's "Recent problems"). When the setting cannot be saved the marker
    /// stays and the work stays off in this process, so every start skips it until the marker is gone; the line then says to
    /// delete it to retry (the key still reads on, so switching it cannot).</item>
    /// </list>
    /// False positives need the process to end inside those two frames for another reason: a crash of the game itself in the rest
    /// of the focus-in frame or the next one, or the game paused in the background right after it (a Home within about 33 ms of
    /// the tap or Back) and then swiped away before it drew two more frames. A normal exit, backgrounding at any other time and the watchdog (which only
    /// reports, never kills) find no marker. Every member never throws; nothing here reaches ActionScript.
    /// </summary>
    internal static class SameFrameBreadcrumb
    {
        /// <summary>
        /// Once per process: a marker left by the previous process means it ended inside our Flash work. Then the work is switched
        /// off (see the class), and the one line is logged and kept in <see cref="SettingsMenuState.SameFrameSkipped"/>. No I/O
        /// after the first call.
        /// </summary>
        public static void Check(ISettingsMenuHost host, SettingsMenuState state)
        {
            try
            {
                if (host == null || state == null || !state.BeginBreadcrumbCheck()) return;
                var markers = new List<string>();
                foreach (string path in Paths(host))
                {
                    if (File.Exists(path)) markers.Add(path);
                }
                if (markers.Count == 0) return;
                string found = markers[0];
                string line = "settings menu: same frame skipped: the last start ended during it (marker " + found + "); ";
                if (host.SwitchSameFrameOff())
                {
                    foreach (string path in Paths(host)) Delete(path);
                    line += SettingsSchema.SettingsMenuSameFrameKey + " is now off; switch it back on to retry";
                }
                else
                {
                    state.SameFrameBlocked = true;
                    // The key still reads on, so switching it on cannot retry: the marker is what keeps the step off.
                    line += SettingsSchema.SettingsMenuSameFrameKey + " could not be saved as off, so it stays off while the marker is there; delete " +
                            string.Join(" and ", markers.ToArray()) + " to retry";
                }
                state.SameFrameSkipped = line;
                Log log = host.Log;
                if (log != null) log.Warn(line);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Inside the native, right before the process's first Flash access there: writes the marker (once per process), in the first
        /// folder that takes it. A marker that cannot be written leaves the work as it is (the next focus-in line says why, once).
        /// Nothing without a folder (<see cref="ISettingsMenuHost.SameFrameMarkerPaths"/> empty).
        /// </summary>
        public static void Enter(ISettingsMenuHost host, SettingsMenuState state, int run, string native)
        {
            try
            {
                if (host == null || state == null || !state.BeginBreadcrumb()) return;
                byte[] text = Encoding.UTF8.GetBytes(TimeFormat.Iso8601Millis(DateTime.UtcNow) + " focus-in " +
                                                     run.ToString(CultureInfo.InvariantCulture) + " " + native + "\n");
                // A host that knows no log folder (tests) has no breadcrumb, and nothing to say about it.
                string[] paths = Paths(host);
                if (paths.Length == 0) return;
                string problem = null;
                foreach (string path in paths)
                {
                    string temp = path + AtomicFile.TempSuffix;
                    try
                    {
                        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) fs.Write(text, 0, text.Length);
                        FileSystem.Rename(temp, path);
                        state.BreadcrumbPath = path;
                        return;
                    }
                    catch (Exception ex)
                    {
                        problem = ex.GetType().Name;
                        Delete(temp);
                    }
                }
                state.BreadcrumbProblem = "breadcrumb not written (" + problem + ")";
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// The run's first frame: <see cref="Leave"/> one frame later (<see cref="ISettingsMenuHost.PostNextFrame"/>), after the
        /// focus-in frame's render has ended on either render thread mode; at once when nothing can be posted (no run frame follows
        /// to delete it later). One field read when there is no marker. Never throws.
        /// </summary>
        public static void LeaveNextFrame(ISettingsMenuHost host, SettingsMenuState state)
        {
            try
            {
                if (host == null || state == null || state.BreadcrumbPath == null) return;
                if (!host.PostNextFrame(() => Leave(state))) Leave(state);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// A later frame: the process lived through the work, so the marker it wrote is deleted. One field read when there is none.
        /// A delete that fails is tried again on the next run and said on the next focus-in line.
        /// </summary>
        public static void Leave(SettingsMenuState state)
        {
            try
            {
                if (state == null || state.BreadcrumbPath == null) return;
                string path = state.TakeBreadcrumbPath();
                if (path == null) return;
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    state.BreadcrumbPath = path;
                    state.BreadcrumbProblem = "breadcrumb not deleted (" + ex.GetType().Name + ")";
                }
            }
            catch (Exception)
            {
            }
        }

        private static string[] Paths(ISettingsMenuHost host)
        {
            string[] paths = host.SameFrameMarkerPaths;
            return paths ?? new string[0];
        }

        private static void Delete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
            }
        }
    }
}
