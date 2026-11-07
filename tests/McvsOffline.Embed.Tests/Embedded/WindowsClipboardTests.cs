using System;
using System.Collections.Generic;
using McvsOffline.Embedded.Admin;
using McvsOffline.Embedded.Windows;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// WindowsClipboard over fake Win32 calls (docs/design/pc-port.md §6.3): the owner is the game's main window, else a
    /// message-only window that is destroyed; never OpenClipboard(NULL); a failed SetClipboardData frees the memory, a
    /// successful one does not; OpenClipboard is retried while another program holds it; the text is cut to the maximum.
    /// </summary>
    public class WindowsClipboardTests
    {
        internal sealed class FakeClipboard : IClipboardNative
        {
            public readonly List<string> Calls = new List<string>();
            public IntPtr Main = new IntPtr(11);
            public IntPtr MessageWindow = new IntPtr(22);
            public int OpenFailures;
            public bool SetFails;
            public bool AllocFails;
            public string Copied;

            public IntPtr MainWindow() { Calls.Add("MainWindow"); return Main; }
            public IntPtr CreateMessageWindow() { Calls.Add("CreateMessageWindow"); return MessageWindow; }
            public bool DestroyWindow(IntPtr window) { Calls.Add("DestroyWindow:" + window); return true; }

            public bool OpenClipboard(IntPtr owner)
            {
                Calls.Add("OpenClipboard:" + owner);
                if (OpenFailures > 0)
                {
                    OpenFailures--;
                    return false;
                }
                return true;
            }

            public bool EmptyClipboard() { Calls.Add("EmptyClipboard"); return true; }

            public IntPtr SetClipboardData(uint format, IntPtr memory)
            {
                Calls.Add("SetClipboardData:" + format + ":" + memory);
                return SetFails ? IntPtr.Zero : memory;
            }

            public bool CloseClipboard() { Calls.Add("CloseClipboard"); return true; }
            public IntPtr GlobalAlloc(int bytes) { Calls.Add("GlobalAlloc:" + bytes); return AllocFails ? IntPtr.Zero : new IntPtr(33); }
            public IntPtr GlobalLock(IntPtr memory) { Calls.Add("GlobalLock:" + memory); return new IntPtr(44); }
            public bool GlobalUnlock(IntPtr memory) { Calls.Add("GlobalUnlock:" + memory); return false; }
            public IntPtr GlobalFree(IntPtr memory) { Calls.Add("GlobalFree:" + memory); return IntPtr.Zero; }
            public void CopyText(string text, IntPtr destination) { Calls.Add("CopyText:" + destination); Copied = text; }
            public int LastError() { return 5; }
            public void Sleep(int ms) { Calls.Add("Sleep:" + ms); }
        }

        [Fact]
        public void TheGamesWindowOwnsTheClipboardAndTheSystemOwnsTheMemory()
        {
            var fake = new FakeClipboard();
            var warnings = new List<string>();
            Assert.True(new WindowsClipboard(fake, warnings.Add).SetText("héllo"));
            Assert.Equal(new[]
            {
                "MainWindow", "OpenClipboard:11", "EmptyClipboard", "GlobalAlloc:12", "GlobalLock:33", "CopyText:44", "GlobalUnlock:33",
                "SetClipboardData:13:33", "CloseClipboard",
            }, fake.Calls);
            Assert.Equal("héllo", fake.Copied);
            Assert.Empty(warnings);
        }

        [Fact]
        public void WithoutAMainWindowAMessageOnlyWindowOwnsItAndIsDestroyed()
        {
            var fake = new FakeClipboard { Main = IntPtr.Zero };
            Assert.True(new WindowsClipboard(fake, null).SetText("x"));
            Assert.Equal("CreateMessageWindow", fake.Calls[1]);
            Assert.Equal("OpenClipboard:22", fake.Calls[2]);
            Assert.Equal("DestroyWindow:22", fake.Calls[fake.Calls.Count - 1]);
            Assert.DoesNotContain("OpenClipboard:0", fake.Calls);
        }

        [Fact]
        public void NoWindowAtAllMeansNoOpenClipboardNull()
        {
            var fake = new FakeClipboard { Main = IntPtr.Zero, MessageWindow = IntPtr.Zero };
            var warnings = new List<string>();
            Assert.False(new WindowsClipboard(fake, warnings.Add).SetText("x"));
            Assert.Equal(new[] { "MainWindow", "CreateMessageWindow" }, fake.Calls);
            Assert.Single(warnings);
            Assert.Contains("no window to own the clipboard", warnings[0]);
        }

        [Fact]
        public void AFailedSetClipboardDataFreesTheMemory()
        {
            var fake = new FakeClipboard { SetFails = true };
            var warnings = new List<string>();
            Assert.False(new WindowsClipboard(fake, warnings.Add).SetText("x"));
            int set = fake.Calls.IndexOf("SetClipboardData:13:33");
            Assert.True(set > 0);
            Assert.Equal("GlobalFree:33", fake.Calls[set + 1]);
            Assert.Equal("CloseClipboard", fake.Calls[fake.Calls.Count - 1]);
            Assert.Contains("SetClipboardData error 5", warnings[0]);

            var ok = new FakeClipboard();
            new WindowsClipboard(ok, null).SetText("x");
            Assert.DoesNotContain(ok.Calls, c => c.StartsWith("GlobalFree", StringComparison.Ordinal));
        }

        [Fact]
        public void AHeldClipboardIsRetriedFiveTimes()
        {
            var fake = new FakeClipboard { OpenFailures = 4 };
            Assert.True(new WindowsClipboard(fake, null).SetText("x"));
            Assert.Equal(5, fake.Calls.FindAll(c => c == "OpenClipboard:11").Count);
            Assert.Equal(4, fake.Calls.FindAll(c => c == "Sleep:50").Count);

            var held = new FakeClipboard { OpenFailures = 5 };
            var warnings = new List<string>();
            Assert.False(new WindowsClipboard(held, warnings.Add).SetText("x"));
            Assert.Equal(5, held.Calls.FindAll(c => c == "OpenClipboard:11").Count);
            Assert.DoesNotContain("EmptyClipboard", held.Calls);
            Assert.DoesNotContain("CloseClipboard", held.Calls);   // never opened
            Assert.Contains("another program holds the clipboard", warnings[0]);
        }

        [Fact]
        public void TheTextIsCutToTheMaximum()
        {
            var fake = new FakeClipboard();
            Assert.True(new WindowsClipboard(fake, null).SetText(new string('a', AdminHost.MaxClipboardChars + 10)));
            Assert.Equal(AdminHost.MaxClipboardChars, fake.Copied.Length);
            Assert.Contains("GlobalAlloc:" + ((AdminHost.MaxClipboardChars + 1) * 2), fake.Calls);
            Assert.False(new WindowsClipboard(fake, null).SetText(null));
        }

        [Fact]
        public void AFailedAllocationClosesTheClipboard()
        {
            var fake = new FakeClipboard { AllocFails = true };
            Assert.False(new WindowsClipboard(fake, null).SetText("x"));
            Assert.Equal("CloseClipboard", fake.Calls[fake.Calls.Count - 1]);
            Assert.DoesNotContain(fake.Calls, c => c.StartsWith("GlobalFree", StringComparison.Ordinal));
        }
    }
}
