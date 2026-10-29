using System;
using System.Collections.Generic;
using McvsOffline.Embedded.Windows;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// SteamOverlay's call order over a fake steam_api (docs/design/pc-port.md §6.5): every null or false stops with one logged
    /// reason, exceptions are caught, and the URL reaches only ActivateGameOverlayToWebPage.
    /// </summary>
    public class SteamOverlayTests
    {
        internal sealed class FakeSteam : ISteamApi
        {
            public readonly List<string> Calls = new List<string>();
            public IntPtr Client = new IntPtr(100);
            public IntPtr Utils = new IntPtr(200);
            public IntPtr Friends = new IntPtr(300);
            public bool Enabled = true;
            public bool Running = true;
            public Exception Throw;
            public string OpenedUrl;

            private void Call(string name)
            {
                Calls.Add(name);
                if (Throw != null) throw Throw;
            }

            public IntPtr SteamClient() { Call("SteamClient"); return Client; }
            public int GetHSteamPipe() { Call("GetHSteamPipe"); return 7; }
            public int GetHSteamUser() { Call("GetHSteamUser"); return 9; }
            public bool IsSteamRunning() { Call("IsSteamRunning"); return Running; }

            public IntPtr GetISteamUtils(IntPtr client, int pipe, string version)
            {
                Call("GetISteamUtils:" + client + ":" + pipe + ":" + version);
                return Utils;
            }

            public IntPtr GetISteamFriends(IntPtr client, int user, int pipe, string version)
            {
                Call("GetISteamFriends:" + client + ":" + user + ":" + pipe + ":" + version);
                return Friends;
            }

            public bool IsOverlayEnabled(IntPtr utils) { Call("IsOverlayEnabled:" + utils); return Enabled; }

            public void ActivateGameOverlayToWebPage(IntPtr friends, string url)
            {
                Call("ActivateGameOverlayToWebPage:" + friends);
                OpenedUrl = url;
            }
        }

        private const string Url = "http://127.0.0.1:51780/_mcvs/ui/#t=0123456789abcdef0123456789abcdef";

        [Fact]
        public void TheCallsComeInTheDocumentedOrder()
        {
            var steam = new FakeSteam();
            var warnings = new List<string>();
            var overlay = new SteamOverlay(steam, warnings.Add);
            Assert.True(overlay.TryOpen(Url));
            Assert.Equal(new[]
            {
                "SteamClient", "GetHSteamPipe", "GetISteamUtils:100:7:SteamUtils009", "IsOverlayEnabled:200", "GetHSteamUser",
                "GetISteamFriends:100:9:7:SteamFriends015", "ActivateGameOverlayToWebPage:300",
            }, steam.Calls);
            Assert.Equal(Url, steam.OpenedUrl);
            Assert.Empty(warnings);
            Assert.Null(overlay.LastReason);
        }

        [Fact]
        public void EveryNullOrFalseStopsWithOneReason()
        {
            Check(s => s.Client = IntPtr.Zero, "SteamClient() is null", "SteamClient");
            Check(s => s.Utils = IntPtr.Zero, "GetISteamUtils(SteamUtils009) is null", "GetISteamUtils:100:7:SteamUtils009");
            Check(s => s.Enabled = false, "the Steam overlay is not enabled for this game", "IsOverlayEnabled:200");
            Check(s => s.Friends = IntPtr.Zero, "GetISteamFriends(SteamFriends015) is null", "GetISteamFriends:100:9:7:SteamFriends015");
            Check(s => s.Throw = new DllNotFoundException("steam_api"), "steam_api call failed: DllNotFoundException", "SteamClient");
            Check(s => s.Throw = new EntryPointNotFoundException("SteamClient"), "EntryPointNotFoundException", "SteamClient");
        }

        private static void Check(Action<FakeSteam> setup, string reason, string lastCall)
        {
            var steam = new FakeSteam();
            setup(steam);
            var warnings = new List<string>();
            var overlay = new SteamOverlay(steam, warnings.Add);
            Assert.False(overlay.TryOpen(Url));
            Assert.False(overlay.TryOpen(Url));   // the same reason is logged once
            Assert.Single(warnings);
            Assert.Contains(reason, warnings[0]);
            Assert.StartsWith("admin: Steam overlay not used: ", warnings[0]);
            Assert.DoesNotContain("#t=", warnings[0]);
            Assert.Equal(lastCall, steam.Calls[steam.Calls.Count - 1]);
            Assert.Null(steam.OpenedUrl);
            Assert.Contains(reason, overlay.LastReason);
        }

        [Fact]
        public void AReasonIsLoggedAgainAfterItChanged()
        {
            var steam = new FakeSteam { Enabled = false };
            var warnings = new List<string>();
            var overlay = new SteamOverlay(steam, warnings.Add);
            Assert.False(overlay.TryOpen(Url));
            steam.Enabled = true;
            Assert.True(overlay.TryOpen(Url));
            steam.Enabled = false;
            Assert.False(overlay.TryOpen(Url));
            Assert.Equal(2, warnings.Count);
            Assert.False(overlay.TryOpen(null));
        }

        [Fact]
        public void TheStatusQuestionsNeverThrow()
        {
            var steam = new FakeSteam();
            var overlay = new SteamOverlay(steam, null);
            Assert.True(overlay.IsEnabled());
            Assert.True(overlay.IsSteamRunning());
            steam.Enabled = false;
            Assert.False(overlay.IsEnabled());
            steam.Client = IntPtr.Zero;
            Assert.Null(overlay.IsEnabled());
            steam.Throw = new DllNotFoundException();
            Assert.Null(overlay.IsEnabled());
            Assert.Null(overlay.IsSteamRunning());
        }

        [Fact]
        public void TheRealImportsFailCleanlyOffWindows()
        {
            // No steam_api here: the native route reports the missing library instead of throwing.
            var warnings = new List<string>();
            var overlay = new SteamOverlay(null, warnings.Add);
            Assert.False(overlay.TryOpen(Url));
            Assert.Single(warnings);
            Assert.Contains("steam_api call failed", warnings[0]);
            Assert.Null(overlay.IsSteamRunning());
        }
    }
}
