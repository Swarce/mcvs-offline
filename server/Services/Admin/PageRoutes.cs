using System;
using System.Collections.Generic;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>
    /// The page itself (<c>GET /_mcvs/ui</c>, <c>/_mcvs/ui/</c>, <c>/_mcvs/ui/index.html</c>), its post-mode entry
    /// (<c>POST /_mcvs/ui</c>, design §5.3), the session exchange (<c>POST/DELETE session</c>), <c>GET ping</c> and the
    /// <c>_mcvs/api/{*rest}</c> catch-all (404 without unknown-endpoint logging).
    /// <para>
    /// The page is compiled into the server from server/Content/admin/index.html (<see cref="BuiltInContent"/>; never the
    /// &lt;data&gt;/content override folder). A build without it answers 500 with a short placeholder.
    /// </para>
    /// </summary>
    internal sealed class PageRoutes
    {
        public const string PageContent = "admin/index.html";

        /// <summary>The page's own policy: inline script and style only, XHR to this origin only, no frames, no forms.</summary>
        public const string ContentSecurityPolicy = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; "
                                                    + "img-src data:; connect-src 'self'; base-uri 'none'; form-action 'none'; "
                                                    + "frame-ancestors 'none'";

        private const string Placeholder =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>MCVS Offline</title></head><body>"
            + "<p>MCVS Offline: this build has no settings page (server/Content/admin/index.html was missing when it was built).</p>"
            + "</body></html>";

        private readonly AdminApi api;
        private readonly AdminSecurity security;

        public PageRoutes(AdminApi api, AdminSecurity security)
        {
            this.api = api;
            this.security = security;
        }

        public void Register(Router router)
        {
            router.Get("_mcvs/ui", "admin.ui", Page);
            router.Get("_mcvs/ui/index.html", "admin.ui", Page);
            router.Post("_mcvs/ui", "admin.ui.post", PostMode);

            api.Map("POST", "session", "admin.session.open", OpenSession, 1024, AdminApi.SessionMode.Ticket);
            api.Delete("session", "admin.session.close", CloseSession);
            api.Map("GET", "ping", "admin.ping", Ping, 1024, AdminApi.SessionMode.Optional);
            // The body limit of the post-mode form, for the header-time check (it has no session yet).
            security.RegisterLimit("POST", "_mcvs/ui", 1024);

            router.Any("_mcvs/api/{*rest}", "admin.api.unknown", request => AdminJson.Error(HttpStatus.NotFound, "not_found", "Not found."));
        }

        // ------------------------------------------------------------------ the page

        private HttpResponse Page(HttpRequest request)
        {
            if (!api.Enabled)
                return PageHeaders(HttpResponse.Text("MCVS Offline: this page is turned off (admin.enabled is false in settings.json).",
                                                     HttpStatus.ServiceUnavailable));
            byte[] page;
            if (!BuiltInContent.All.TryGetValue(PageContent, out page))
            {
                api.Log.Error("admin: " + PageContent + " is missing from this build");
                return PageHeaders(HttpResponse.Bytes(Encoding.UTF8.GetBytes(Placeholder), "text/html; charset=utf-8", HttpStatus.InternalServerError));
            }
            return PageHeaders(HttpResponse.Bytes(page, "text/html; charset=utf-8"));
        }

        /// <summary>The headers of every page response (§5.3).</summary>
        internal static HttpResponse PageHeaders(HttpResponse response)
        {
            response.Headers.Set("Content-Security-Policy", ContentSecurityPolicy);
            response.Headers.Set("X-Frame-Options", "DENY");
            response.Headers.Set("Referrer-Policy", "no-referrer");
            response.Headers.Set("Cache-Control", "no-store");
            response.Headers.Set("X-Content-Type-Options", "nosniff");
            return response;
        }

        /// <summary>
        /// <c>admin.openVia=post</c>: the game's WebView posts <c>t=&lt;ticket&gt;</c>, optionally with the page URL's
        /// <c>s=&lt;screen&gt;</c> and <c>r=nosave</c>; the answer is always a 303 to <c>GET /_mcvs/ui/</c> (a Refresh then
        /// reloads with the cookie instead of re-posting a used ticket), with a new session cookie for a valid ticket and
        /// without one otherwise. The screen (default <c>status</c>) and a valid <c>r</c> come back as the redirect's
        /// fragment (<c>#s=saves&amp;r=nosave</c>), so the page opens on that screen with its toast as it does from the
        /// fragment of a ticket URL, and without a session shows #banner-nosession rather than view mode.
        /// </summary>
        private HttpResponse PostMode(HttpRequest request)
        {
            if (!api.Enabled) return AdminJson.WithApiHeaders(AdminApi.Disabled());
            if (!string.Equals(api.Context.Settings.GetString("admin.openVia", "auto"), "post", StringComparison.OrdinalIgnoreCase))
                return AdminJson.Error(HttpStatus.NotFound, "not_found", "Not found.");
            if (request.Body.Length > 1024) return AdminJson.TooLarge(1024);

            ParamCollection form = request.IsFormBody ? request.Form : null;
            string ticket = form == null ? null : form.Get("t");
            string location = PageLocation(form);
            string trigger;
            if (ticket != null && security.TryConsumeTicket(ticket, out trigger))
            {
                AdminSession session = Open(trigger, request);
                return Redirect(location, AdminSecurity.SetCookie(session));
            }
            if (security.SessionOf(request, true) != null) return Redirect(location, null);
            if (!security.Limiter.Allow(AdminSecurity.TicketFailureBucket, AdminSecurity.TicketFailuresPerMinute))
                return TicketsRateLimited();
            api.Log.Info("admin: page opened with an invalid or used ticket (post mode)");
            return Redirect(location, null);
        }

        /// <summary>
        /// "/_mcvs/ui/#s=&lt;screen&gt;", plus "&amp;r=nosave", from the form's fields that pass the page's own fragment
        /// checks (§2.5: one of the six screen names; exactly "nosave"); anything else is left out, and the screen is
        /// "status" (the page's default) when the form names none. The fragment is never empty: the page treats a bare
        /// URL as a browser's (view mode, §2.4), but a post always comes from the game, so a failed exchange must show
        /// #banner-nosession.
        /// </summary>
        internal static string PageLocation(ParamCollection form)
        {
            string screen = form == null ? null : form.Get("s");
            string result = form == null ? null : form.Get("r");
            string fragment = "s=" + (AdminPortal.IsScreen(screen) ? screen : "status");
            if (string.Equals(result, "nosave", StringComparison.Ordinal)) fragment += "&r=nosave";
            return "/_mcvs/ui/#" + fragment;
        }

        private static HttpResponse Redirect(string location, string setCookie)
        {
            HttpResponse response = HttpResponse.Empty(AdminJson.SeeOther);
            response.Headers.Set("Location", location);
            if (setCookie != null) response.Headers.Set("Set-Cookie", setCookie);
            return AdminJson.WithApiHeaders(response);
        }

        // ------------------------------------------------------------------ session, ping

        private HttpResponse OpenSession(AdminCall call)
        {
            string ticket = JsonValue.GetString(call.Body, "ticket");
            string trigger;
            if (ticket != null && security.TryConsumeTicket(ticket, out trigger))
            {
                AdminSession session = Open(trigger, call.Request);
                var info = new Dictionary<string, object>(StringComparer.Ordinal);
                info["created"] = TimeFormat.Iso8601Millis(session.CreatedUtc);
                info["trigger"] = session.Trigger;
                info["expiresIdle"] = (long)AdminSecurity.SessionIdleSeconds;
                var body = new Dictionary<string, object>(StringComparer.Ordinal);
                body["ok"] = true;
                body["session"] = info;
                HttpResponse response = AdminJson.Ok(body);
                response.Headers.Set("Set-Cookie", AdminSecurity.SetCookie(session));
                return response;
            }
            // Only failures count, and the ticket is checked first: a valid ticket always works.
            if (!security.Limiter.Allow(AdminSecurity.TicketFailureBucket, AdminSecurity.TicketFailuresPerMinute))
                return TicketsRateLimited();
            return AdminJson.Error(HttpStatus.Unauthorized, "ticket_invalid",
                                   "This page link is no longer valid. Open MCVS Offline from the game again.");
        }

        private HttpResponse CloseSession(AdminCall call)
        {
            security.CloseSession(call.Session);
            api.Log.Info("admin: session closed (trigger " + call.Session.Trigger + ")");
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["ok"] = true;
            HttpResponse response = AdminJson.Ok(body);
            response.Headers.Set("Set-Cookie", AdminSecurity.ExpiredCookie());
            return response;
        }

        private HttpResponse Ping(AdminCall call)
        {
            ServerContext context = api.Context;
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["ok"] = true;
            body["session"] = call.Session != null;
            body["now"] = TimeFormat.Iso8601Millis(context.Clock.UtcNow);
            body["uptimeSeconds"] = (long)Math.Max(0, (context.Clock.UtcNow - context.StartedUtc).TotalSeconds);
            return AdminJson.Ok(body);
        }

        private AdminSession Open(string trigger, HttpRequest request)
        {
            string userAgent = request.Headers.Get("User-Agent") ?? "-";
            if (userAgent.Length > 200) userAgent = userAgent.Substring(0, 200) + "...";
            AdminSession session = security.CreateSession(trigger, userAgent);
            api.Log.Info("admin: session opened (trigger " + trigger + ", " + userAgent + ")");
            api.PageAlive();
            return session;
        }

        private HttpResponse TicketsRateLimited()
        {
            if (security.Limiter.Allow("ticket-failure-logged", 1))
                api.Log.Warn("admin: too many invalid page tickets; refusing failed exchanges for a minute");
            int retry = security.Limiter.RetryAfterSeconds(AdminSecurity.TicketFailureBucket, AdminSecurity.TicketFailuresPerMinute);
            return AdminJson.Error(AdminJson.TooManyRequests, "rate_limited", "Too many attempts. Try again in a minute.",
                                   AdminJson.Details("retryAfterSeconds", (long)retry));
        }
    }
}
