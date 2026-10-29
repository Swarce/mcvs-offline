using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using McvsOffline.Embedded.Windows;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// SystemBrowser (docs/design/pc-port.md §6.3): Open returns before a launcher that blocks finishes (the game thread never
    /// waits for ShellExecute), the launcher runs on a background thread named mcvs-browser that asked for STA, and a launcher
    /// that throws gives one WARN without the URL.
    /// </summary>
    public class SystemBrowserTests
    {
        private const string Url = "http://127.0.0.1:51780/_mcvs/ui/#t=0123456789abcdef0123456789abcdef";

        [Fact]
        public void OpenReturnsBeforeABlockingLauncherFinishes()
        {
            using (var release = new ManualResetEventSlim(false))
            using (var started = new ManualResetEventSlim(false))
            {
                string seenUrl = null;
                Thread seenThread = null;
                ApartmentState apartment = ApartmentState.Unknown;
                var browser = new SystemBrowser(url =>
                {
                    seenUrl = url;
                    seenThread = Thread.CurrentThread;
                    apartment = Thread.CurrentThread.GetApartmentState();
                    started.Set();
                    release.Wait(10000);
                }, null);
                var watch = Stopwatch.StartNew();
                Assert.True(browser.Open(Url));
                Assert.True(watch.ElapsedMilliseconds < 2000, "Open waited for the launcher");
                Assert.True(started.Wait(10000));
                Assert.Equal(Url, seenUrl);
                Assert.NotSame(Thread.CurrentThread, seenThread);
                Assert.True(seenThread.IsBackground);
                Assert.Equal(SystemBrowser.ThreadName, seenThread.Name);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Assert.True(browser.LastThreadWasSta);
                    Assert.Equal(ApartmentState.STA, apartment);
                }
                else
                {
                    Assert.False(browser.LastThreadWasSta);   // only Windows has STA threads; the request is still made
                }
                release.Set();
            }
        }

        [Fact]
        public void AThrowingLauncherGivesOneWarningWithoutTheUrl()
        {
            var warnings = new BlockingCollection<string>();
            var browser = new SystemBrowser(url => { throw new System.ComponentModel.Win32Exception(2, "cannot start " + url); }, warnings.Add);
            Assert.True(browser.Open(Url));   // "started": the failure comes later
            string warning;
            Assert.True(warnings.TryTake(out warning, 10000));
            Assert.StartsWith("admin: the system browser did not start: Win32Exception: ", warning);
            Assert.DoesNotContain("#t=", warning);
            Assert.DoesNotContain("0123456789abcdef", warning);
            Assert.False(warnings.TryTake(out warning, 200));
        }

        [Fact]
        public void NoUrlStartsNothing()
        {
            int calls = 0;
            var browser = new SystemBrowser(url => Interlocked.Increment(ref calls), null);
            Assert.False(browser.Open(null));
            Assert.False(browser.Open(""));
            Thread.Sleep(50);
            Assert.Equal(0, calls);
        }

        [Fact]
        public void WithoutUrlRemovesTheTicket()
        {
            Assert.Equal("x <page url> y", SystemBrowser.WithoutUrl("x " + Url + " y", Url));
            // A message that quotes the URL differently still loses the fragment (the ticket).
            string fragment = Url.Substring(Url.IndexOf('#'));
            Assert.Equal("open http://127.0.0.1:51780/_mcvs/ui/ failed", SystemBrowser.WithoutUrl("open " + Url + " failed", "http://other/" + fragment));
        }
    }
}
