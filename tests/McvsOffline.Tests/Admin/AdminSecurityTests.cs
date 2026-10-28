using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>Tickets, sessions, rate limits and the admin API's checks (design §5.3, §4.1).</summary>
    public class AdminSecurityTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);

        // ------------------------------------------------------------------ unit

        [Fact]
        public void TicketsAreOneTimeAndShortLived()
        {
            var clock = new ManualClock(Start);
            var security = new AdminSecurity(clock);
            string ticket = security.MintTicket("account");
            Assert.Matches("^[0-9a-f]{32}$", ticket);

            Assert.True(security.TryConsumeTicket(ticket, out string trigger));
            Assert.Equal("account", trigger);
            Assert.False(security.TryConsumeTicket(ticket, out _));

            string late = security.MintTicket("news");
            clock.Advance(TimeSpan.FromSeconds(119));
            string stillValid = security.MintTicket("news");
            Assert.True(security.TryConsumeTicket(late, out _));
            clock.Advance(TimeSpan.FromSeconds(120));
            Assert.False(security.TryConsumeTicket(stillValid, out _));

            Assert.False(security.TryConsumeTicket(null, out _));
            Assert.False(security.TryConsumeTicket(ticket.ToUpperInvariant(), out _));
            Assert.False(security.TryConsumeTicket("abc", out _));
        }

        [Fact]
        public void AtMostSixteenTicketsAreOutstanding()
        {
            var security = new AdminSecurity(new ManualClock(Start));
            var tickets = Enumerable.Range(0, 17).Select(i => security.MintTicket("t" + i)).ToList();
            Assert.Equal(16, security.OutstandingTickets);
            Assert.False(security.TryConsumeTicket(tickets[0], out _));
            Assert.True(security.TryConsumeTicket(tickets[1], out _));
            Assert.True(security.TryConsumeTicket(tickets[16], out _));
        }

        [Fact]
        public void ConstantTimeComparisonIsExact()
        {
            Assert.True(AdminSecurity.ConstantTimeEquals("abc", "abc"));
            Assert.False(AdminSecurity.ConstantTimeEquals("abc", "abd"));
            Assert.False(AdminSecurity.ConstantTimeEquals("abc", "abcd"));
            Assert.False(AdminSecurity.ConstantTimeEquals("abc", "ab"));
            Assert.False(AdminSecurity.ConstantTimeEquals("abc", null));
            Assert.False(AdminSecurity.ConstantTimeEquals("a\0", "a"));
        }

        [Fact]
        public void SessionsExpireWhenIdleAndAfterTwelveHours()
        {
            var clock = new ManualClock(Start);
            var security = new AdminSecurity(clock);
            AdminSession session = security.CreateSession("account", "ua");
            Assert.Matches("^[0-9a-f]{64}$", session.Cookie);
            Assert.Same(session, security.FindSession(session.Cookie, true));

            // Touched every hour: alive until the absolute limit.
            for (int hour = 1; hour < 12; hour++)
            {
                clock.Advance(TimeSpan.FromHours(1));
                Assert.NotNull(security.FindSession(session.Cookie, true));
            }
            clock.Advance(TimeSpan.FromHours(1));
            Assert.Null(security.FindSession(session.Cookie, true));

            // Idle for two hours: gone; IsValidCookie does not renew.
            AdminSession idle = security.CreateSession("news", "ua");
            clock.Advance(TimeSpan.FromMinutes(119));
            Assert.True(security.IsValidCookie(idle.Cookie));
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.False(security.IsValidCookie(idle.Cookie));
        }

        [Fact]
        public void AtMostFourSessions()
        {
            var security = new AdminSecurity(new ManualClock(Start));
            var sessions = Enumerable.Range(0, 5).Select(i => security.CreateSession("t", "ua")).ToList();
            Assert.Equal(4, security.SessionCount);
            Assert.False(security.IsValidCookie(sessions[0].Cookie));
            Assert.True(security.IsValidCookie(sessions[4].Cookie));
            Assert.True(security.CloseSession(sessions[4]));
            Assert.False(security.IsValidCookie(sessions[4].Cookie));
        }

        [Fact]
        public void TheRateLimiterSlidesOverAMinute()
        {
            var clock = new ManualClock(Start);
            var limiter = new AdminRateLimiter(clock);
            for (int i = 0; i < 5; i++)
            {
                Assert.True(limiter.Allow("b", 5));
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            Assert.False(limiter.Allow("b", 5));
            Assert.Equal(55, limiter.RetryAfterSeconds("b", 5));
            Assert.True(limiter.Allow("other", 5));
            clock.Advance(TimeSpan.FromSeconds(55));
            Assert.True(limiter.Allow("b", 5));
            Assert.False(limiter.Allow("b", 5));
        }

        [Fact]
        public void RouteLimitsMatchTemplates()
        {
            var security = new AdminSecurity(null);
            security.RegisterLimit("POST", "_mcvs/api/v1/settings", 65536);
            security.RegisterLimit("POST", "_mcvs/api/v1/saves/{account}/backups", 1024);
            security.RegisterLimit("GET", "_mcvs/api/v1/status", 1024);
            Assert.Equal(65536, security.RouteLimit("POST", "/_mcvs/api/v1/settings"));
            Assert.Equal(1024, security.RouteLimit("post", "/_mcvs/api/v1/saves/0123/backups"));
            Assert.Equal(1024, security.RouteLimit("HEAD", "/_mcvs/api/v1/status"));
            Assert.Equal(0, security.RouteLimit("DELETE", "/_mcvs/api/v1/settings"));
            Assert.Equal(0, security.RouteLimit("POST", "/_mcvs/api/v1/nothing"));
        }

        // ------------------------------------------------------------------ HTTP through McvsServer.Handle

        [Fact]
        public void ATicketBecomesASessionCookie()
        {
            using var dir = new TempDir();
            var logs = new MemoryLogSink();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"), logs);
            var client = new AdminTestClient(server);

            string ticket = client.MintTicket(AdminTriggers.Account);
            var response = client.Post("session", new Dictionary<string, object> { ["ticket"] = ticket });
            Assert.Equal(200, response.StatusCode);
            string setCookie = response.Headers.Get("Set-Cookie");
            Assert.Matches("^mcvs_s=[0-9a-f]{64}; Path=/_mcvs/; HttpOnly; SameSite=Strict$", setCookie);
            Assert.Equal("no-store", response.Headers.Get("Cache-Control"));
            Assert.Equal("nosniff", response.Headers.Get("X-Content-Type-Options"));
            Assert.Null(response.Headers.Get("Access-Control-Allow-Origin"));
            var body = AdminTestClient.Json(response);
            Assert.Equal(true, body["ok"]);
            Assert.Equal("account", JsonPath.Get(body, "session.trigger"));
            Assert.Equal(7200L, JsonPath.Get(body, "session.expiresIdle"));
            Assert.Equal(200, client.Get("status").StatusCode);

            // Never logged: the ticket and the cookie.
            Assert.True(logs.Contains("admin: session opened (trigger account, Mozilla/5.0"));
            string cookie = client.SessionCookie;
            Assert.DoesNotContain(logs.Snapshot(), e => e.Format().Contains(ticket) || e.Format().Contains(cookie));

            // The header-time limit knows the session and the route.
            Assert.True(server.Context.IsValidAdminSession(cookie));
            Assert.Equal(64 * 1024, server.Context.AdminRouteBodyLimit("POST", "/_mcvs/api/v1/settings"));

            var close = client.Delete("session");
            Assert.Equal(200, close.StatusCode);
            Assert.Equal("mcvs_s=; Path=/_mcvs/; Max-Age=0", close.Headers.Get("Set-Cookie"));
            Assert.Null(client.SessionCookie);
            Assert.False(server.Context.IsValidAdminSession(cookie));
        }

        [Fact]
        public void UsedExpiredAndUnknownTicketsLookTheSame()
        {
            using var dir = new TempDir();
            var clock = new ManualClock(Start);
            using var server = AdminTestClient.CreateServer(dir.Combine("data"), clock: clock);
            var client = new AdminTestClient(server);

            string used = client.MintTicket();
            Assert.Equal(200, client.Post("session", new Dictionary<string, object> { ["ticket"] = used }).StatusCode);
            client.SessionCookie = null;
            string expired = client.MintTicket();
            clock.Advance(TimeSpan.FromSeconds(121));

            var bodies = new List<string>();
            foreach (string ticket in new[] { used, expired, "0123456789abcdef0123456789abcdef", "nothex" })
            {
                var response = client.Post("session", new Dictionary<string, object> { ["ticket"] = ticket });
                Assert.Equal(401, response.StatusCode);
                Assert.Null(response.Headers.Get("Set-Cookie"));
                bodies.Add(response.BodyText);
            }
            Assert.Single(bodies.Distinct());
            Assert.Equal("ticket_invalid", MiniJson.ParseObject(bodies[0])["error"]);
        }

        [Fact]
        public void FailedExchangesAreRateLimitedButAValidTicketAlwaysWorks()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var client = new AdminTestClient(server);
            var bogus = new Dictionary<string, object> { ["ticket"] = "0123456789abcdef0123456789abcdef" };
            for (int i = 0; i < 5; i++) Assert.Equal(401, client.Post("session", bogus).StatusCode);
            var limited = client.Post("session", bogus);
            Assert.Equal(429, limited.StatusCode);
            Assert.Equal("rate_limited", AdminTestClient.At(limited, "error"));
            Assert.True((long)AdminTestClient.At(limited, "details.retryAfterSeconds") >= 1);

            Assert.Equal(200, client.Post("session", new Dictionary<string, object> { ["ticket"] = client.MintTicket() }).StatusCode);
        }

        [Fact]
        public void EveryApiRouteNeedsASession()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var client = new AdminTestClient(server);

            foreach (string path in new[] { "status", "settings", "settings/file", "codes" })
            {
                var response = client.Get(path);
                Assert.Equal(401, response.StatusCode);
                Assert.Equal("no_session", AdminTestClient.At(response, "error"));
            }
            foreach (string path in new[] { "settings", "settings/reset", "settings/repair", "clipboard", "actions/save-now",
                                            "actions/restart", "actions/crm-reload", "actions/unlock-now" })
                Assert.Equal(401, client.Post(path, "{}").StatusCode);
            Assert.Equal(401, client.Delete("session").StatusCode);

            client.SessionCookie = new string('a', 64);
            Assert.Equal("session_expired", AdminTestClient.At(client.Get("status"), "error"));

            // Unknown API paths are a quiet 404, not an unknown endpoint.
            var unknown = client.Get("nothing/here");
            Assert.Equal(404, unknown.StatusCode);
            Assert.Equal("not_found", AdminTestClient.At(unknown, "error"));
        }

        [Fact]
        public void WritesNeedJsonTheHeaderAndAModestBody()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var client = new AdminTestClient(server);
            client.OpenSession();

            Assert.Equal(415, client.Send("POST", "settings", "changes=1", "application/x-www-form-urlencoded").StatusCode);
            Assert.Equal(415, client.Send("POST", "settings", "{}", "text/plain").StatusCode);
            Assert.Equal(400, client.Post("settings", "[1,2]").StatusCode);
            Assert.Equal(400, client.Post("settings", "{broken").StatusCode);
            Assert.Equal("bad_request", AdminTestClient.At(client.Post("settings", "\"x\""), "error"));

            var big = client.Post("actions/crm-reload", "{\"pad\":\"" + new string('x', 2000) + "\"}");
            Assert.Equal(413, big.StatusCode);
            Assert.Equal(1024L, AdminTestClient.At(big, "details.limit"));

            client.SendCsrfHeader = false;
            var csrf = client.Post("actions/crm-reload", "{}");
            Assert.Equal(403, csrf.StatusCode);
            Assert.Equal("csrf", AdminTestClient.At(csrf, "error"));
            client.SendCsrfHeader = true;
            client.Origin = "http://evil.example";
            Assert.Equal(403, client.Post("actions/crm-reload", "{}").StatusCode);
            client.Origin = "http://127.0.0.1:51780";
            Assert.Equal(200, client.Post("actions/crm-reload", "{}").StatusCode);
        }

        [Fact]
        public void AdminWritesAreLimitedToSixtyAMinute()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"), clock: new ManualClock(Start));
            var client = new AdminTestClient(server);
            client.OpenSession();
            for (int i = 0; i < 60; i++) Assert.Equal(200, client.Post("actions/crm-reload", "{}").StatusCode);
            var limited = client.Post("actions/crm-reload", "{}");
            Assert.Equal(429, limited.StatusCode);
            Assert.Equal(200, client.Get("status").StatusCode); // reads are not limited
        }

        [Fact]
        public void PingTellsTheHostOnlyWithASession()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var host = new FakeAdminHost();
            server.Context.Features.Set<IAdminHost>(host);
            var client = new AdminTestClient(server);

            var anonymous = client.Get("ping");
            Assert.Equal(200, anonymous.StatusCode);
            Assert.Equal(false, AdminTestClient.At(anonymous, "session"));
            Assert.Equal(0, host.PageAliveCount);

            client.OpenSession();
            int before = host.PageAliveCount;
            var ping = client.Get("ping");
            Assert.Equal(true, AdminTestClient.At(ping, "session"));
            Assert.True(host.PageAliveCount > before);
            Assert.NotNull(AdminTestClient.At(ping, "now"));
        }

        [Fact]
        public void TheKillSwitchTurnsEverythingOff()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var client = new AdminTestClient(server);
            client.OpenSession();
            IAdminPortal portal = client.Portal;
            Assert.True(portal.Enabled);

            server.Context.Settings.Override("admin.enabled", false);
            Assert.False(portal.Enabled);
            Assert.Null(portal.MintPageUrl(AdminTriggers.Account, null));
            Assert.False(portal.IsPageMarker(portal.PageMarkerUrl));
            Assert.False(portal.OpenPage(AdminTriggers.Redeem, null));
            foreach (var response in new[] { client.Get("status"), client.Get("ping"), client.Post("settings", "{}") })
            {
                Assert.Equal(503, response.StatusCode);
                Assert.Equal("admin_disabled", AdminTestClient.At(response, "error"));
            }
            Assert.Equal(503, client.Get("/_mcvs/ui/").StatusCode);
        }

        [Fact]
        public void ThePortalMintsTicketsAndKnowsItsMarker()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            IAdminPortal portal = new AdminTestClient(server).Portal;

            Assert.Equal("http://127.0.0.1:51780/_mcvs/ui/#news", portal.PageMarkerUrl);
            Assert.True(portal.IsPageMarker("http://127.0.0.1:51780/_mcvs/ui/#news"));
            Assert.False(portal.IsPageMarker("http://127.0.0.1:51780/_mcvs/ui/#news "));
            Assert.False(portal.IsPageMarker("http://127.0.0.1:51780/_mcvs/ui/#NEWS"));
            Assert.False(portal.IsPageMarker("http://localhost:51780/_mcvs/ui/#news"));
            Assert.False(portal.IsPageMarker(null));

            Assert.Matches(@"^http://127\.0\.0\.1:51780/_mcvs/ui/#t=[0-9a-f]{32}$", portal.MintPageUrl(AdminTriggers.Account, null));
            Assert.Matches(@"^http://127\.0\.0\.1:51780/_mcvs/ui/#t=[0-9a-f]{32}&s=saves$", portal.MintPageUrl(AdminTriggers.Resume, "saves"));
            Assert.Matches(@"#t=[0-9a-f]{32}$", portal.MintPageUrl(AdminTriggers.Account, "../etc"));

            // No host: nothing can be opened. With one: a fresh ticket each time.
            Assert.False(portal.OpenPage(AdminTriggers.Redeem, null));
            var host = new FakeAdminHost();
            server.Context.Features.Set<IAdminHost>(host);
            Assert.True(portal.OpenPage(AdminTriggers.Redeem, "status"));
            Assert.True(portal.OpenPage(AdminTriggers.Redeem, "status"));
            Assert.Equal(new[] { "OpenPage:redeem", "OpenPage:redeem" }, host.Calls);
            Assert.NotEqual(host.OpenedUrls[0], host.OpenedUrls[1]);
            Assert.EndsWith("&s=status", host.OpenedUrls[0]);
        }

        [Fact]
        public void ThePageIsServedWithItsSecurityHeaders()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var client = new AdminTestClient(server);
            bool built = BuiltInContent.All.ContainsKey("admin/index.html");
            foreach (string path in new[] { "/_mcvs/ui", "/_mcvs/ui/", "/_mcvs/ui/index.html" })
            {
                var page = client.Get(path);
                Assert.Equal(built ? 200 : 500, page.StatusCode);
                Assert.Equal("text/html; charset=utf-8", page.ContentType);
                Assert.Equal(PageRoutes.ContentSecurityPolicy, page.Headers.Get("Content-Security-Policy"));
                Assert.StartsWith("default-src 'none'; script-src 'unsafe-inline'", page.Headers.Get("Content-Security-Policy"));
                Assert.Equal("DENY", page.Headers.Get("X-Frame-Options"));
                Assert.Equal("no-referrer", page.Headers.Get("Referrer-Policy"));
                Assert.Equal("no-store", page.Headers.Get("Cache-Control"));
                Assert.Contains("<title>MCVS Offline</title>", page.BodyText);
            }
            // The page itself needs no session; a foreign Host still gets nothing.
            client.Host = "evil.example:51780";
            Assert.Equal(404, client.Get("/_mcvs/ui/").StatusCode);
        }

        [Fact]
        public void RegisteringTwiceIsHarmless()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"),
                register: (r, c) => AdminService.Register(r, c));
            var client = new AdminTestClient(server);
            client.OpenSession();
            Assert.Equal(200, client.Get("status").StatusCode);
        }
    }
}
