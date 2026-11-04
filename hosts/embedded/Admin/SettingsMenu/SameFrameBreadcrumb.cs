// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.IO;
using System.Globalization;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Embedded.Admin.SettingsMenu
{
    /// <summary>
    /// The same-frame step's automatic fallback (build 22; docs/design/settings-menu.md §3.2, §5 risk 2). The step
    /// (<see cref="SettingsMenuLayout.SameFrame"/>) re-enters ActionScript from inside a Flash native of libnativeengine/gameswf; a
    /// native fault there cannot be caught, so a process that dies in it would die again at every return to Settings. A small
    /// marker file in the host's log folder (<see cref="ISettingsMenuHost.SameFrameMarkerPath"/>, next to
    /// <c>restart-requested.txt</c>) records that a step is under way:
    /// <list type="bullet">
    /// <item><see cref="Enter"/>: inside the native, right before the process's first read of the Flash tree (its first look; on
    /// Android the check of ABOUT before it). Once per process: the first step goes through the same natives as every later one,
    /// and a file write on the game thread per return is not worth it. Temp file and rename (the marker exists whole or not at
    /// all), no flush to disk (a crash of the process leaves the page cache to the kernel; only a power cut could lose it).</item>
    /// <item><see cref="Leave"/>: the next frame (every step of a run calls it; it is one field read when nothing is
    /// outstanding): the process lived through the native call, the rest of the focus-in frame and its render, so the marker is
    /// deleted. The marker therefore exists only for the native call(s) and the rest of that one frame.</item>
    /// <item><see cref="Check"/>: once per process, at start (<see cref="UiNatives.Start"/>, off the game thread) or at the first
    /// arm when the start did not run it: a marker found there was left by a process that ended during that window. The step is
    /// then switched off for this install the way the owner would do it, <c>admin.settingsMenu.sameFrame</c> = false in
    /// settings.json (the settings page shows the switch off and turning it back on retries), the marker is deleted, and one
    /// WARN line is logged (the status page's "Recent problems"). When the setting cannot be saved the marker stays and the step
    /// stays off in this process, so every start skips it until the marker is gone.</item>
    /// </list>
    /// False positives are a death of the process inside that window for another reason: a crash of the game itself in the rest
    /// of the focus-in frame, or the game paused in the background in that very frame (a Back and a Home within one frame) and
    /// then swiped away before it drew another frame. The watchdog never kills the process (it only reports). A normal kill at
    /// any other time finds no marker. Every member never throws; nothing here reaches ActionScript.
    /// </summary>
    internal static class SameFrameBreadcrumb
    {
        /// <summary>
        /// Once per process: a marker left by the previous process means it ended during a same-frame step. Then the step is
        /// switched off (see the class), and the one line is logged and kept in <see cref="SettingsMenuState.SameFrameSkipped"/>.
        /// Nothing (no I/O after the first call) otherwise.
        /// </summary>
        public static void Check(ISettingsMenuHost host, SettingsMenuState state)
        {
            try
            {
                if (host == null || state == null || !state.BeginBreadcrumbCheck()) return;
                string path = host.SameFrameMarkerPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                string line = "settings menu: same-frame step skipped: the last start ended during it (marker " + path + "); ";
                if (host.SwitchSameFrameOff())
                {
                    Delete(path);
                    line += "switch " + SettingsSchema.SettingsMenuSameFrameKey + " back on to retry";
                }
                else
                {
                    state.SameFrameBlocked = true;
                    line += SettingsSchema.SettingsMenuSameFrameKey + " could not be saved as off, so the step stays off while the marker is there";
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
        /// Inside the watched native, right before the first read of the process's first look: writes the marker (once per
        /// process). A marker that cannot be written leaves the step as it is (it says why on the next focus-in line).
        /// </summary>
        public static void Enter(ISettingsMenuHost host, SettingsMenuState state, int run, string native)
        {
            string temp = null;
            try
            {
                if (host == null || state == null || !state.BeginBreadcrumb()) return;
                string path = host.SameFrameMarkerPath;
                if (string.IsNullOrEmpty(path))
                {
                    state.BreadcrumbProblem = "marker not written (no log folder)";
                    return;
                }
                byte[] text = Encoding.UTF8.GetBytes(TimeFormat.Iso8601Millis(DateTime.UtcNow) + " focus-in " +
                                                     run.ToString(CultureInfo.InvariantCulture) + " " + native + "\n");
                temp = path + AtomicFile.TempSuffix;
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) fs.Write(text, 0, text.Length);
                FileSystem.Rename(temp, path);
                temp = null;
                state.BreadcrumbPath = path;
            }
            catch (Exception ex)
            {
                try
                {
                    state.BreadcrumbProblem = "marker not written (" + ex.GetType().Name + ")";
                }
                catch (Exception)
                {
                }
                Delete(temp);
            }
        }

        /// <summary>
        /// A later frame: the process lived through the step, so the marker it wrote is deleted. One field read when there is none.
        /// A delete that fails is tried again on the next step and said on the next focus-in line.
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
                    state.BreadcrumbProblem = "marker not deleted (" + ex.GetType().Name + ")";
                }
            }
            catch (Exception)
            {
            }
        }

        private static void Delete(string path)
        {
            try
            {
                if (path != null && File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
            }
        }
    }
}
