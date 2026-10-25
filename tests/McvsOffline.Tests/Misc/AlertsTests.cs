using System;
using System.Collections.Generic;
using System.Diagnostics;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Alerts;
using Xunit;

namespace McvsOffline.Tests.Misc
{
    /// <summary>The Kairos stream (report 02 §4.8) and the hub behind it.</summary>
    public class AlertsTests
    {
        private static string ListenTarget(TestPlayer p, string types = "message,connection_request,set_profile", string targets = null)
        {
            return "/alerts/me?push_method=streaming&alert_types=" + Uri.EscapeDataString(types) + "&content_type=event-stream"
                   + (targets == null ? "" : "&targets=" + Uri.EscapeDataString(targets))
                   + "&access_token=" + Uri.EscapeDataString(p.OnBehalfToken);
        }

        [Fact]
        public void StreamStaysOpenAndCarriesAlertsAsDataLines()
        {
            using var s = new MiscServer();
            s.Start();
            TestPlayer p = s.NewPlayer("alice");
            using (var stream = new StreamingResponse(s.Server.Port, ListenTarget(p)))
            {
                Assert.Equal(200, stream.Status);
                Assert.StartsWith("text/event-stream", stream.Headers["Content-Type"]);
                Assert.Equal("chunked", stream.Headers["Transfer-Encoding"]);
                Assert.StartsWith(":", stream.ReadLine()); // the opening comment, flushed at once

                WaitForListener(s, p.FedId);
                s.Post("/messages/secured/" + p.FedId, s.GameServer.Token, "body", "x", "alert_kairos", "True");
                string line = stream.ReadDataLine();
                Assert.NotNull(line);
                Assert.StartsWith("data: ", line);
                var alert = MiniJson.ParseObject(line.Substring("data:".Length));
                Assert.Equal("message", alert["type"]);
                Assert.Equal("secured", alert["transport"]);
                Assert.False(stream.Ended);
            }
        }

        [Fact]
        public void IdleStreamsSendKeepAlivesAndEndAfterTheHoldTimeNeverImmediately()
        {
            using var s = new MiscServer();
            s.Set("alerts.keepAliveSeconds", 1L);
            s.Set("alerts.streamSeconds", 2L);
            s.Start();
            TestPlayer p = s.NewPlayer("bob");
            var watch = Stopwatch.StartNew();
            using (var stream = new StreamingResponse(s.Server.Port, ListenTarget(p)))
            {
                var lines = new List<string>();
                string line;
                while ((line = stream.ReadLine(5000)) != null) lines.Add(line);
                Assert.True(stream.Ended);
                Assert.Contains(": keep-alive", lines);
                Assert.DoesNotContain(lines, l => l.StartsWith("data:", StringComparison.Ordinal));
            }
            // A stream that ended at once would make the client re-request in a tight loop.
            Assert.True(watch.ElapsedMilliseconds >= 1800, "the stream ended after " + watch.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void UnknownTokenIs401NotAStream()
        {
            using var s = new MiscServer();
            var r = s.Get("/alerts/me?push_method=streaming&alert_types=message", "mcvs00000000.0123456789abcdef0123456789abcdef");
            Assert.Equal(401, r.StatusCode);
            Assert.Null(r.StreamBody);
        }

        [Fact]
        public void ListenersOnlyGetTheTypesTheyAskedFor()
        {
            var hub = new AlertHub(null, null);
            using (var connectionOnly = hub.Subscribe(new[] { "fed_id:a" }, new[] { "connection" }, null))
            using (var all = hub.Subscribe(new[] { "fed_id:a" }, new string[0], null))
            {
                Assert.Equal(1, hub.Push("fed_id:a", Alert("message")));
                Assert.Empty(connectionOnly.Take(50, null));
                Assert.Single(all.Take(50, null));
            }
        }

        [Fact]
        public void ConnectionAlertsAreNeverSentBecauseTheyKickThePlayer()
        {
            var hub = new AlertHub(null, null);
            using (var sub = hub.Subscribe(new[] { "fed_id:a" }, new[] { "connection" }, null))
            {
                Assert.Equal(0, hub.Push("fed_id:a", Alert("connection")));
                Assert.Empty(sub.Take(50, null));
            }
        }

        [Fact]
        public void AlertsForTargetsReachListenersThatNamedThem()
        {
            var hub = new AlertHub(null, null);
            using (var clanMember = hub.Subscribe(new[] { "fed_id:a", "group:clan1" }, new string[0], null))
            using (var other = hub.Subscribe(new[] { "fed_id:b" }, new string[0], null))
            {
                hub.Push("group:clan1", Alert("wall_post"));
                Assert.Single(clanMember.Take(50, null));
                Assert.Empty(other.Take(50, null));
                // A bare account id is the account.
                hub.Push("a", Alert("set_profile"));
                Assert.Single(clanMember.Take(50, null));
            }
        }

        [Fact]
        public void AlertsPublishedWhileTheListenerReconnectsAreReplayed()
        {
            var clock = new ManualClock(new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc));
            var hub = new AlertHub(clock, null);
            hub.Subscribe(new[] { "fed_id:a" }, new string[0], "tok|-|-").Dispose(); // stream 1 ends
            hub.Push("fed_id:a", Alert("message"));                                  // in the gap
            using (var second = hub.Subscribe(new[] { "fed_id:a" }, new string[0], "tok|-|-"))
            {
                Assert.Single(second.Take(50, null));
            }
            // A listener with another key (or one coming back too late) gets nothing old.
            using (var stranger = hub.Subscribe(new[] { "fed_id:a" }, new string[0], "other|-|-"))
            {
                Assert.Empty(stranger.Take(50, null));
            }
            hub.Subscribe(new[] { "fed_id:a" }, new string[0], "tok|-|-").Dispose();
            hub.Push("fed_id:a", Alert("message"));
            clock.Advance(TimeSpan.FromMinutes(10));
            using (var late = hub.Subscribe(new[] { "fed_id:a" }, new string[0], "tok|-|-"))
            {
                Assert.Empty(late.Take(50, null));
            }
        }

        [Fact]
        public void QueuedButUndeliveredAlertsAreReplayedToo()
        {
            var hub = new AlertHub(null, null);
            var first = hub.Subscribe(new[] { "fed_id:a" }, new string[0], "k");
            hub.Push("fed_id:a", Alert("message"));
            first.Dispose(); // ended before taking it
            using (var second = hub.Subscribe(new[] { "fed_id:a" }, new string[0], "k"))
            {
                Assert.Single(second.Take(50, null));
            }
        }

        [Fact]
        public void OtherModulesPushThroughTheEventHubAndPostAlertWorks()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("carl");
            AlertHub hub = AlertHub.For(s.Context);
            using (var sub = hub.Subscribe(new[] { p.FedId }, new string[0], null))
            {
                var payload = new Dictionary<string, object> { ["to"] = p.AccountId, ["alert"] = Alert("set_profile") };
                Assert.Equal(1, s.Context.Events.Publish(AlertHub.PushTopic, payload));
                Assert.Equal("set_profile", MiniJson.ParseObject(Assert.Single(sub.Take(100, null)))["type"]);

                var r = s.Post("/alerts/" + Uri.EscapeDataString(p.FedId), s.GameServer.Token, "alert", "{\"type\":\"set_profile\"}");
                Assert.Equal(200, r.StatusCode);
                Assert.Equal(1L, MiscServer.Obj(r)["delivered"]);
                Assert.Single(sub.Take(100, null));
                Assert.Equal(400, s.Post("/alerts/me", p.Token, "alert", "nope").StatusCode);
            }
        }

        [Fact]
        public void TakeReturnsEarlyWhenStopping()
        {
            var hub = new AlertHub(null, null);
            using (var sub = hub.Subscribe(new[] { "fed_id:a" }, new string[0], null))
            {
                var watch = Stopwatch.StartNew();
                Assert.Empty(sub.Take(10000, () => true));
                Assert.True(watch.ElapsedMilliseconds < 2000);
            }
        }

        private static Dictionary<string, object> Alert(string type)
        {
            return new Dictionary<string, object> { ["type"] = type };
        }

        internal static void WaitForListener(MiscServer s, string recipient)
        {
            var watch = Stopwatch.StartNew();
            while (AlertHub.For(s.Context).ListenerCount(recipient) == 0 && watch.ElapsedMilliseconds < 5000)
                System.Threading.Thread.Sleep(10);
        }
    }
}
