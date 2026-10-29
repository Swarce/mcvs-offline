#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL.
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>The steam_api.dll calls <see cref="SteamOverlay"/> makes, behind an interface so their order is unit-tested.</summary>
    public interface ISteamApi
    {
        IntPtr SteamClient();

        int GetHSteamPipe();

        int GetHSteamUser();

        bool IsSteamRunning();

        IntPtr GetISteamUtils(IntPtr client, int pipe, string version);

        IntPtr GetISteamFriends(IntPtr client, int user, int pipe, string version);

        bool IsOverlayEnabled(IntPtr utils);

        void ActivateGameOverlayToWebPage(IntPtr friends, string url);
    }

    /// <summary>
    /// The flat steam_api.dll exports of the game's Steamworks SDK (1.42 generation: <c>SteamFriends015</c>, <c>SteamUtils009</c>;
    /// docs/design/pc-port.md §6.5, [WH] §1.2). <c>DllImport("steam_api")</c> binds the module the exe already loaded. Every
    /// import is cdecl with its exact export name; the two C++ <c>bool</c> returns are one byte in <c>al</c> (the rest of
    /// <c>eax</c> is garbage), hence <c>MarshalAs(U1)</c>; strings are ANSI with best-fit mapping off (our URLs are ASCII).
    /// Checked by PcPInvokeTests against the export table of the real steam_api.dll.
    /// </summary>
    internal sealed class SteamApiNative : ISteamApi
    {
        public IntPtr SteamClient() { return NativeMethods.SteamClient(); }

        public int GetHSteamPipe() { return NativeMethods.SteamAPI_GetHSteamPipe(); }

        public int GetHSteamUser() { return NativeMethods.SteamAPI_GetHSteamUser(); }

        public bool IsSteamRunning() { return NativeMethods.SteamAPI_IsSteamRunning(); }

        public IntPtr GetISteamUtils(IntPtr client, int pipe, string version)
        {
            return NativeMethods.SteamAPI_ISteamClient_GetISteamUtils(client, pipe, version);
        }

        public IntPtr GetISteamFriends(IntPtr client, int user, int pipe, string version)
        {
            return NativeMethods.SteamAPI_ISteamClient_GetISteamFriends(client, user, pipe, version);
        }

        public bool IsOverlayEnabled(IntPtr utils) { return NativeMethods.SteamAPI_ISteamUtils_IsOverlayEnabled(utils); }

        public void ActivateGameOverlayToWebPage(IntPtr friends, string url)
        {
            NativeMethods.SteamAPI_ISteamFriends_ActivateGameOverlayToWebPage(friends, url);
        }

        private static class NativeMethods
        {
            private const string Dll = "steam_api";

            [DllImport(Dll, EntryPoint = "SteamClient", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern IntPtr SteamClient();

            [DllImport(Dll, EntryPoint = "SteamAPI_GetHSteamPipe", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int SteamAPI_GetHSteamPipe();

            [DllImport(Dll, EntryPoint = "SteamAPI_GetHSteamUser", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            public static extern int SteamAPI_GetHSteamUser();

            [DllImport(Dll, EntryPoint = "SteamAPI_IsSteamRunning", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            [return: MarshalAs(UnmanagedType.U1)]
            public static extern bool SteamAPI_IsSteamRunning();

            [DllImport(Dll, EntryPoint = "SteamAPI_ISteamClient_GetISteamUtils", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true,
                       CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
            public static extern IntPtr SteamAPI_ISteamClient_GetISteamUtils(IntPtr client, int pipe, [MarshalAs(UnmanagedType.LPStr)] string version);

            [DllImport(Dll, EntryPoint = "SteamAPI_ISteamClient_GetISteamFriends", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true,
                       CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
            public static extern IntPtr SteamAPI_ISteamClient_GetISteamFriends(IntPtr client, int user, int pipe, [MarshalAs(UnmanagedType.LPStr)] string version);

            [DllImport(Dll, EntryPoint = "SteamAPI_ISteamUtils_IsOverlayEnabled", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
            [return: MarshalAs(UnmanagedType.U1)]
            public static extern bool SteamAPI_ISteamUtils_IsOverlayEnabled(IntPtr utils);

            [DllImport(Dll, EntryPoint = "SteamAPI_ISteamFriends_ActivateGameOverlayToWebPage", CallingConvention = CallingConvention.Cdecl,
                       ExactSpelling = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
            public static extern void SteamAPI_ISteamFriends_ActivateGameOverlayToWebPage(IntPtr friends, [MarshalAs(UnmanagedType.LPStr)] string url);
        }
    }

    /// <summary>
    /// Opens a page in the Steam overlay (docs/design/pc-port.md §6.5, D2): <c>SteamClient()</c> → <c>GetISteamUtils(client,
    /// pipe, "SteamUtils009")</c> → <c>IsOverlayEnabled</c> → <c>GetISteamFriends(client, user, pipe, "SteamFriends015")</c> →
    /// <c>ActivateGameOverlayToWebPage(friends, url)</c>. Every step is checked; a null or false stops with its reason, which is
    /// logged once per distinct reason. The call has no success signal: "true" means the overlay was asked. Never calls
    /// <c>GameManager.InitOverlayChanged</c> or <c>FullScreen</c> (one native slot each; it would replace the game's handler).
    /// Thread-safe (Steamworks' client API may be called from any thread) and never throws.
    /// </summary>
    public sealed class SteamOverlay
    {
        public const string UtilsVersion = "SteamUtils009";
        public const string FriendsVersion = "SteamFriends015";

        private readonly ISteamApi api;
        private readonly Action<string> warn;
        private string lastReason;

        /// <param name="api">steam_api.dll (<see cref="SteamApiNative"/>) or a fake.</param>
        /// <param name="warn">Where a reason for "no overlay" goes (once per distinct reason); may be null.</param>
        public SteamOverlay(ISteamApi api, Action<string> warn)
        {
            this.api = api ?? new SteamApiNative();
            this.warn = warn;
        }

        /// <summary>Why the last <see cref="TryOpen"/> or <see cref="IsEnabled"/> said no, or null after a yes.</summary>
        public string LastReason { get { return lastReason; } }

        /// <summary>True once the overlay was asked to show <paramref name="url"/>; false (and one logged reason) otherwise.</summary>
        public bool TryOpen(string url)
        {
            if (string.IsNullOrEmpty(url)) return Fail("no URL");
            try
            {
                IntPtr client = api.SteamClient();
                if (client == IntPtr.Zero) return Fail("SteamClient() is null (Steam API not initialised)");
                int pipe = api.GetHSteamPipe();
                IntPtr utils = api.GetISteamUtils(client, pipe, UtilsVersion);
                if (utils == IntPtr.Zero) return Fail("GetISteamUtils(" + UtilsVersion + ") is null");
                if (!api.IsOverlayEnabled(utils)) return Fail("the Steam overlay is not enabled for this game");
                IntPtr friends = api.GetISteamFriends(client, api.GetHSteamUser(), pipe, FriendsVersion);
                if (friends == IntPtr.Zero) return Fail("GetISteamFriends(" + FriendsVersion + ") is null");
                api.ActivateGameOverlayToWebPage(friends, url);
                lastReason = null;
                return true;
            }
            catch (Exception ex)
            {
                // DllNotFoundException / EntryPointNotFoundException (another steam_api.dll), a marshalling refusal of the URL.
                return Fail("steam_api call failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>The status line's "overlay enabled": true, false, or null when Steam cannot be asked. Never throws.</summary>
        public bool? IsEnabled()
        {
            try
            {
                IntPtr client = api.SteamClient();
                if (client == IntPtr.Zero) return null;
                IntPtr utils = api.GetISteamUtils(client, api.GetHSteamPipe(), UtilsVersion);
                if (utils == IntPtr.Zero) return null;
                return api.IsOverlayEnabled(utils);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>SteamAPI_IsSteamRunning, or null when it cannot be asked. Never throws.</summary>
        public bool? IsSteamRunning()
        {
            try
            {
                return api.IsSteamRunning();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private bool Fail(string reason)
        {
            string previous = Interlocked.Exchange(ref lastReason, reason);
            if (previous != reason && warn != null)
            {
                try
                {
                    warn("admin: Steam overlay not used: " + reason);
                }
                catch (Exception)
                {
                }
            }
            return false;
        }
    }
}
#endif
