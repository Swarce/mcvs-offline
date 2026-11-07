using System.Collections.Generic;
using McvsOffline.Embedded;
using McvsOffline.Embedded.Android;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// Android's capability rules (docs/design/pc-port.md §3.3): the matrix AdminHostTests checked through Describe before the
    /// capability seam (the sdk 21/28/29/33 rules and the missing JNI members), moved unchanged onto AndroidCapabilities.From.
    /// AdminHostTests keeps checking how Describe combines a platform's capabilities with the game state.
    /// </summary>
    public class AndroidCapabilitiesTests
    {
        private static AdminPlatformCapabilities From(int sdk, params string[] missing)
        {
            return AndroidCapabilities.From(sdk, new List<string>(missing));
        }

        [Fact]
        public void EverythingOnAndroid14WithTheWholeJniTable()
        {
            AdminPlatformCapabilities caps = From(34);
            Assert.True(caps.OpenPage && caps.ShareFile && caps.SaveToDownloads && caps.ShareText && caps.Copy && caps.Toast);
            Assert.True(caps.ExitViaGame && caps.ExitViaHost);
            Assert.True(caps.RelaunchUnconfirmed);   // Android 13+: the restart alarm may bring nothing back (target-sdk.md §4.6, not observed yet)
            Assert.False(caps.OpenInBrowser);        // Windows only
        }

        [Fact]
        public void AMissingClipboardMemberTurnsOffCopyOnly()
        {
            AdminPlatformCapabilities caps = From(34, AndroidJni.ClipboardSetPrimary.Key);
            Assert.False(caps.Copy);
            Assert.True(caps.ShareFile);
            Assert.True(caps.ShareText);
        }

        [Fact]
        public void AMissingDownloadsUriTurnsOffFileSharesAndDownloads()
        {
            AdminPlatformCapabilities caps = From(34, AndroidJni.DownloadsUri.Key);
            Assert.False(caps.ShareFile);
            Assert.False(caps.SaveToDownloads);
        }

        [Fact]
        public void Android9SharesFilesWithoutDownloadsAndNeedsTheStrictModeRelaxation()
        {
            AdminPlatformCapabilities caps = From(28);   // Android 9: file:// shares, no Downloads
            Assert.False(caps.RelaunchUnconfirmed);
            Assert.True(caps.ShareFile);
            Assert.False(caps.SaveToDownloads);
            Assert.False(From(28, AndroidJni.StrictSetVmPolicy.Key).ShareFile);
            Assert.True(From(23, AndroidJni.StrictSetVmPolicy.Key).ShareFile);   // no StrictMode relaxation needed before Android 7
        }

        [Fact]
        public void TheTwoExitRoutesFollowTheirOwnMembers()
        {
            AdminPlatformCapabilities caps = From(34, AndroidJni.MainActivityRestart.Key);
            Assert.False(caps.ExitViaGame);
            Assert.True(caps.ExitViaHost);    // the host's restart remains
            caps = From(34, AndroidJni.MainActivityRestart.Key, AndroidJni.KillProcess.Key);
            Assert.False(caps.ExitViaGame || caps.ExitViaHost);
        }

        [Fact]
        public void NullMissingListMeansNothingMissing()
        {
            AdminPlatformCapabilities caps = AndroidCapabilities.From(34, null);
            Assert.True(caps.OpenPage && caps.Copy && caps.ExitViaHost);
        }
    }
}
