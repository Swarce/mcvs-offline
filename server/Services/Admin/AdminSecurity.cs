using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>A settings-page session (design §5.3): the <c>mcvs_s</c> cookie and what it was opened from.</summary>
    public sealed class AdminSession
    {
        /// <summary>The cookie value (64 hex). Never logged.</summary>
        internal string Cookie { get; set; }

        public string Trigger { get; internal set; }

        public DateTime CreatedUtc { get; internal set; }

        public DateTime LastSeenUtc { get; internal set; }

        public string UserAgent { get; internal set; }
    }

    /// <summary>
    /// One-time tickets and sessions of the settings page (design §5.3), all in memory: a restart invalidates every
    /// ticket and session (I2: no secret is ever stored).
    /// <list type="bullet">
    /// <item>Ticket: 32 hex (<c>Ids.RandomHex(16)</c>), single use, 120 s, at most 16 outstanding (the oldest is dropped).
    /// Minted only for the game's own WebView (<see cref="IAdminPortal.MintPageUrl"/>).</item>
    /// <item>Session: cookie <c>mcvs_s</c> = 64 hex, idle 2 h, absolute 12 h, at most 4 (the oldest is dropped).</item>
    /// </list>
    /// Tickets and cookies are compared in constant time over the full string against every live entry. Also holds the
    /// shared rate limiter and the body limit of every admin route (for the header-time check, design §5.2 rule 5).
    /// Thread-safe.
    /// </summary>
    internal sealed class AdminSecurity
    {
        public const string CookieName = LocalAccessGate.SessionCookieName;
        public const string CookiePath = "/_mcvs/";
        public const int TicketLifetimeSeconds = 120;
        public const int MaxTickets = 16;
        public const int SessionIdleSeconds = 2 * 3600;
        public const int SessionAbsoluteSeconds = 12 * 3600;
        public const int MaxSessions = 4;

        /// <summary>Failed ticket exchanges allowed per minute (successful ones are never limited).</summary>
        public const int TicketFailuresPerMinute = 5;

        public const string TicketFailureBucket = "ticket-failure";

        private readonly object gate = new object();
        private readonly IClock clock;
        private readonly List<Ticket> tickets = new List<Ticket>();
        private readonly List<AdminSession> sessions = new List<AdminSession>();
        private readonly List<RouteLimitEntry> routeLimits = new List<RouteLimitEntry>();

        public AdminSecurity(IClock clock)
        {
            this.clock = clock ?? SystemClock.Instance;
            Limiter = new AdminRateLimiter(this.clock);
        }

        public AdminRateLimiter Limiter { get; private set; }

        public int SessionCount
        {
            get { lock (gate) return sessions.Count; }
        }

        // ------------------------------------------------------------------ tickets

        /// <summary>A new one-time ticket for <paramref name="trigger"/>.</summary>
        public string MintTicket(string trigger)
        {
            string value = Ids.RandomHex(16);
            lock (gate)
            {
                PruneTickets(clock.UtcNow);
                while (tickets.Count >= MaxTickets) tickets.RemoveAt(0);
                tickets.Add(new Ticket { Value = value, Trigger = trigger, CreatedUtc = clock.UtcNow });
            }
            return value;
        }

        /// <summary>
        /// Uses up a valid ticket. False for an unknown, used or expired one (the caller cannot tell which). The
        /// comparison runs over every outstanding ticket in constant time.
        /// </summary>
        public bool TryConsumeTicket(string value, out string trigger)
        {
            trigger = null;
            if (!IsTicketFormat(value)) return false;
            lock (gate)
            {
                PruneTickets(clock.UtcNow);
                Ticket match = null;
                foreach (Ticket t in tickets)
                {
                    if (ConstantTimeEquals(t.Value, value)) match = t;
                }
                if (match == null) return false;
                tickets.Remove(match);
                trigger = match.Trigger;
                return true;
            }
        }

        public int OutstandingTickets
        {
            get
            {
                lock (gate)
                {
                    PruneTickets(clock.UtcNow);
                    return tickets.Count;
                }
            }
        }

        /// <summary>Exactly 32 lower-case hex characters.</summary>
        public static bool IsTicketFormat(string value)
        {
            return IsLowerHex(value, 32);
        }

        private void PruneTickets(DateTime now)
        {
            tickets.RemoveAll(t => (now - t.CreatedUtc).TotalSeconds >= TicketLifetimeSeconds || now < t.CreatedUtc.AddSeconds(-5));
        }

        // ------------------------------------------------------------------ sessions

        /// <summary>A new session (the oldest is dropped beyond <see cref="MaxSessions"/>).</summary>
        public AdminSession CreateSession(string trigger, string userAgent)
        {
            DateTime now = clock.UtcNow;
            var session = new AdminSession
            {
                Cookie = Ids.RandomHex(32),
                Trigger = trigger,
                CreatedUtc = now,
                LastSeenUtc = now,
                UserAgent = userAgent,
            };
            lock (gate)
            {
                PruneSessions(now);
                while (sessions.Count >= MaxSessions) sessions.RemoveAt(0);
                sessions.Add(session);
            }
            return session;
        }

        /// <summary>The live session named by <paramref name="cookie"/>, or null. <paramref name="touch"/> renews its idle time.</summary>
        public AdminSession FindSession(string cookie, bool touch)
        {
            if (!IsLowerHex(cookie, 64)) return null;
            lock (gate)
            {
                DateTime now = clock.UtcNow;
                PruneSessions(now);
                AdminSession match = null;
                foreach (AdminSession s in sessions)
                {
                    if (ConstantTimeEquals(s.Cookie, cookie)) match = s;
                }
                if (match != null && touch) match.LastSeenUtc = now;
                return match;
            }
        }

        /// <summary>The live session of a request (any of its <c>mcvs_s</c> cookies), or null.</summary>
        public AdminSession SessionOf(HttpRequest request, bool touch)
        {
            foreach (string cookie in LocalAccessGate.CookieValues(request.Headers, CookieName))
            {
                AdminSession session = FindSession(cookie, touch);
                if (session != null) return session;
            }
            return null;
        }

        /// <summary>True when the request carries a <c>mcvs_s</c> cookie at all (valid or not).</summary>
        public static bool HasSessionCookie(HttpRequest request)
        {
            foreach (string cookie in LocalAccessGate.CookieValues(request.Headers, CookieName))
            {
                if (cookie.Length > 0) return true;
            }
            return false;
        }

        /// <summary><see cref="ServerContext.IsValidAdminSession"/>: a live session, without renewing it.</summary>
        public bool IsValidCookie(string cookie)
        {
            return FindSession(cookie, false) != null;
        }

        public bool CloseSession(AdminSession session)
        {
            if (session == null) return false;
            lock (gate) return sessions.Remove(session);
        }

        private void PruneSessions(DateTime now)
        {
            sessions.RemoveAll(s => (now - s.LastSeenUtc).TotalSeconds >= SessionIdleSeconds
                                    || (now - s.CreatedUtc).TotalSeconds >= SessionAbsoluteSeconds);
        }

        /// <summary>The Set-Cookie value of a new session: a session cookie (no Max-Age) for /_mcvs/ only.</summary>
        public static string SetCookie(AdminSession session)
        {
            return CookieName + "=" + session.Cookie + "; Path=" + CookiePath + "; HttpOnly; SameSite=Strict";
        }

        /// <summary>The Set-Cookie value that removes the cookie.</summary>
        public static string ExpiredCookie()
        {
            return CookieName + "=; Path=" + CookiePath + "; Max-Age=0";
        }

        // ------------------------------------------------------------------ route body limits

        /// <summary>Records the body limit of an admin route ("GET", "_mcvs/api/v1/settings").</summary>
        public void RegisterLimit(string method, string template, long limit)
        {
            var entry = new RouteLimitEntry { Method = method.ToUpperInvariant(), Template = RouteTemplate.Parse(template), Limit = limit };
            lock (gate) routeLimits.Add(entry);
        }

        /// <summary>
        /// <see cref="ServerContext.AdminRouteBodyLimit"/>: the limit of the first registered route matching
        /// <paramref name="method"/> and the decoded <paramref name="path"/>, or 0 when none does.
        /// </summary>
        public long RouteLimit(string method, string path)
        {
            var segments = new List<string>();
            foreach (string piece in (path ?? "").Split('/'))
            {
                if (piece.Length > 0) segments.Add(piece);
            }
            string[] s = segments.ToArray();
            string m = (method ?? "").ToUpperInvariant();
            if (m == "HEAD") m = "GET";
            lock (gate)
            {
                foreach (RouteLimitEntry entry in routeLimits)
                {
                    RouteValues ignored;
                    if ((entry.Method == m || entry.Method == "*") && entry.Template.TryMatch(s, s, out ignored)) return entry.Limit;
                }
            }
            return 0;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Equality that looks at every character of both strings whatever they hold.</summary>
        public static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            int length = Math.Max(a.Length, b.Length);
            for (int i = 0; i < length; i++)
            {
                int x = i < a.Length ? a[i] : 0;
                int y = i < b.Length ? b[i] : 0;
                diff |= x ^ y;
            }
            return diff == 0;
        }

        private static bool IsLowerHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (char c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private sealed class Ticket
        {
            public string Value;
            public string Trigger;
            public DateTime CreatedUtc;
        }

        private sealed class RouteLimitEntry
        {
            public string Method;
            public RouteTemplate Template;
            public long Limit;
        }
    }

    /// <summary>
    /// Sliding-minute rate limits per bucket (design §5.3): admin writes 60/min, share/export/zip 6/min, paste 30/min,
    /// failed ticket exchanges 5/min. Thread-safe.
    /// </summary>
    internal sealed class AdminRateLimiter
    {
        private readonly object gate = new object();
        private readonly IClock clock;
        private readonly Dictionary<string, Queue<DateTime>> buckets = new Dictionary<string, Queue<DateTime>>(StringComparer.Ordinal);

        public AdminRateLimiter(IClock clock)
        {
            this.clock = clock ?? SystemClock.Instance;
        }

        /// <summary>True (and counted) while the bucket had fewer than <paramref name="perMinute"/> events in the last 60 s.</summary>
        public bool Allow(string bucket, int perMinute)
        {
            lock (gate)
            {
                DateTime now = clock.UtcNow;
                Queue<DateTime> events = Events(bucket, now);
                if (events.Count >= perMinute) return false;
                events.Enqueue(now);
                return true;
            }
        }

        /// <summary>Seconds until <see cref="Allow"/> can succeed again (at least 1).</summary>
        public int RetryAfterSeconds(string bucket, int perMinute)
        {
            lock (gate)
            {
                DateTime now = clock.UtcNow;
                Queue<DateTime> events = Events(bucket, now);
                if (events.Count < perMinute) return 1;
                double wait = 60 - (now - events.Peek()).TotalSeconds;
                return Math.Max(1, (int)Math.Ceiling(wait));
            }
        }

        private Queue<DateTime> Events(string bucket, DateTime now)
        {
            Queue<DateTime> events;
            if (!buckets.TryGetValue(bucket, out events))
            {
                events = new Queue<DateTime>();
                buckets[bucket] = events;
            }
            while (events.Count > 0 && ((now - events.Peek()).TotalSeconds >= 60 || events.Peek() > now)) events.Dequeue();
            return events;
        }
    }
}
