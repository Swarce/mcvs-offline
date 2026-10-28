using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Notifications;
using McvsOffline.Server.Core;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The one-time POST_NOTIFICATIONS request (docs/research/target-sdk.md §4.4, WP-T5): when it asks (Android 13+, a build that
    /// targets 33+ and declares the permission, not granted, never asked), that it waits for the main menu, records the request
    /// in settings.json before showing the dialog, and the settings page's status row.
    /// </summary>
    public class NotificationPermissionTests
    {
        private sealed class FakePlatform : INotificationPlatform
        {
            public int SdkInt { get; set; } = 36;
            public int Target = 35;
            public bool? IsDeclared = true;
            public bool? IsGranted = false;
            public int Requests;
            public int TargetReads;
            public Action OnRequest = () => { };

            public int TargetSdk()
            {
                TargetReads++;
                return Target;
            }

            public bool? Declared() => IsDeclared;

            public bool? Granted() => IsGranted;

            public bool Request()
            {
                Requests++;
                OnRequest();
                return true;
            }
        }

        [Theory]
        [InlineData(32, 35, true, false, true, "", "NotNeeded")]
        [InlineData(29, 29, false, null, true, "", "NotNeeded")]
        [InlineData(0, 35, true, false, true, "", "Unknown")]
        [InlineData(33, 35, true, true, true, "", "Granted")]
        [InlineData(36, 35, true, true, true, "2026-10-01T00:00:00Z", "Granted")]
        [InlineData(36, 35, true, false, false, "", "Disabled")]
        [InlineData(36, 29, false, false, true, "", "TargetBelow33")]
        [InlineData(36, 35, false, false, true, "", "NotDeclared")]
        [InlineData(36, 35, true, false, true, "2026-10-01T00:00:00Z", "AlreadyAsked")]
        [InlineData(36, -1, true, false, true, "", "Unknown")]
        [InlineData(36, 35, null, false, true, "", "Unknown")]
        [InlineData(36, 35, true, null, true, "", "Unknown")]
        [InlineData(33, 33, true, false, true, "", "Ask")]
        [InlineData(36, 35, true, false, true, null, "Ask")]
        public void TheDecisionFollowsTheRules(int sdk, int target, bool? declared, bool? granted, bool enabled, string askedAt, string expectedName)
        {
            var expected = (NotificationDecision)Enum.Parse(typeof(NotificationDecision), expectedName);
            Assert.Equal(expected, NotificationPermission.Decide(sdk, target, declared, granted, enabled, askedAt));
            Assert.False(string.IsNullOrEmpty(NotificationPermission.Describe(expected, sdk, target, askedAt)));
        }

        [Fact]
        public void TheConstantsAreAndroids()
        {
            Assert.Equal("android.permission.POST_NOTIFICATIONS", NotificationPermission.Permission);
            Assert.Equal(McvsOffline.Embedded.Android.AndroidJni.PostNotifications, NotificationPermission.Permission);
            Assert.Equal(McvsOffline.Embedded.Android.AndroidJni.Tiramisu, NotificationPermission.MinSdk);
            Assert.Equal("allowed", NotificationPermission.Describe(NotificationDecision.Granted, 36, 35, null));
            Assert.Contains("asked once on 2026-10-01T00:00:00Z", NotificationPermission.Describe(NotificationDecision.AlreadyAsked, 36, 35, "2026-10-01T00:00:00Z"));
            Assert.Contains("targets API 29", NotificationPermission.Describe(NotificationDecision.TargetBelow33, 36, 29, null));
            Assert.Contains("Android 12", NotificationPermission.Describe(NotificationDecision.NotNeeded, 31, 29, null));
        }

        private static Settings NewSettings(out string path)
        {
            string dir = TestEnvironment.NewTempDir("notify");
            path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{\"port\": 51780}");
            return Settings.Load(path, Log.Null);
        }

        private static NotificationPrompt Prompt(FakePlatform platform, Settings settings, Queue<bool?> menu, List<string> marks, List<string> events)
        {
            int ticks = 0;
            return new NotificationPrompt(platform, () => settings, Log.Null)
            {
                AtMainMenu = () => menu.Count > 0 ? menu.Dequeue() : true,
                Sleep = ms => ticks += ms,
                Ticks = () => ticks,
                Now = () => new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc),
                Mark = marks.Add,
                Lease = (ms, reason) => events.Add("lease " + ms + " " + reason),
            };
        }

        [Fact]
        public void ItWaitsForThreeMenuPollsRecordsTheRequestThenAsksOnce()
        {
            var platform = new FakePlatform();
            Settings settings = NewSettings(out string path);
            var marks = new List<string>();
            var events = new List<string>();
            platform.OnRequest = () => events.Add("request, settings file says " +
                                                   Settings.Load(path, Log.Null).GetString(NotificationPermission.RequestedAtKey, "?"));
            // Loading screen, menu, language prompt elsewhere, then the menu for good.
            var menu = new Queue<bool?>(new bool?[] { null, false, true, false, true, true });
            NotificationPrompt prompt = Prompt(platform, settings, menu, marks, events);
            Assert.Contains("the game asks once at the main menu", prompt.Describe());
            prompt.Run();

            Assert.Equal(1, platform.Requests);
            Assert.Empty(menu);   // it needed the last three polls in a row
            Assert.Equal(new[] { "lease 120000 notification permission dialog", "request, settings file says 2026-10-28T12:00:00Z" }, events);
            Assert.Contains(marks, m => m.StartsWith("notifications: asked once for the notification permission at the main menu (Android 36)"));
            Assert.Equal(1, platform.TargetReads);   // cached

            // The next start: asked before, never again.
            var again = Prompt(platform, Settings.Load(path, Log.Null), new Queue<bool?>(), marks, events);
            again.Run();
            Assert.Equal(1, platform.Requests);
            Assert.Contains("AlreadyAsked", again.Outcome);
            Assert.Contains("asked once on 2026-10-28T12:00:00Z", again.Describe());
            platform.IsGranted = true;
            Assert.Equal("allowed", again.Describe());
        }

        [Fact]
        public void NothingIsAskedWhenItIsNotNeededTheBrowserIsOpenOrSettingsCannotRecordIt()
        {
            var marks = new List<string>();
            var events = new List<string>();
            foreach (FakePlatform p in new[]
                     {
                         new FakePlatform { SdkInt = 31 }, new FakePlatform { IsGranted = true }, new FakePlatform { Target = 29 },
                         new FakePlatform { IsDeclared = false }, new FakePlatform { IsGranted = null },
                     })
            {
                Settings s = NewSettings(out string path);
                NotificationPrompt prompt = Prompt(p, s, new Queue<bool?>(), marks, events);
                prompt.Run();
                Assert.Equal(0, p.Requests);
                Assert.StartsWith("notifications: no request (", prompt.Outcome);
                Assert.Equal("", Settings.Load(path, Log.Null).GetString(NotificationPermission.RequestedAtKey, ""));
            }

            // Disabled in settings.
            var platform = new FakePlatform();
            Settings off = NewSettings(out string offPath);
            off.SetAndSave(new Dictionary<string, object> { [NotificationPermission.RequestKey] = false });
            Prompt(platform, off, new Queue<bool?>(), marks, events).Run();
            Assert.Equal(0, platform.Requests);

            // The settings page stays in front: no dialog over it (gives up after an hour of polls).
            Settings s2 = NewSettings(out string p2);
            NotificationPrompt browser = Prompt(platform, s2, new Queue<bool?>(), marks, events);
            browser.BrowserOpen = () => true;
            browser.Run();
            Assert.Equal(0, platform.Requests);
            Assert.Contains("no main menu within 60 minutes", browser.Outcome);

            // An unreadable settings.json (fallback): it would ask on every start, so it does not ask at all.
            string dir = TestEnvironment.NewTempDir("notify-broken");
            string broken = Path.Combine(dir, "settings.json");
            File.WriteAllText(broken, "{ broken");
            Settings fallback = Settings.Load(broken, Log.Null);
            Assert.True(fallback.IsFallback);
            NotificationPrompt cannot = Prompt(platform, fallback, new Queue<bool?>(), marks, events);
            cannot.Run();
            Assert.Equal(0, platform.Requests);
            Assert.Contains("settings.json cannot record the request", cannot.Outcome);
            Assert.Equal("{ broken", File.ReadAllText(broken));

            // No platform at all (a PC): unknown, nothing happens.
            var none = new NotificationPrompt(null, () => null, null);
            Assert.Equal(NotificationDecision.Unknown, none.Decide());
            none.Run();
            Assert.Contains("Unknown", none.Outcome);
        }
    }
}
