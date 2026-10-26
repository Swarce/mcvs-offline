using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>An admin API route handler (runs after the session, CSRF, size and kill-switch checks).</summary>
    public delegate HttpResponse AdminHandler(AdminCall call);

    /// <summary>One admin API request as a handler sees it.</summary>
    public sealed class AdminCall
    {
        public AdminCall(HttpRequest request, Dictionary<string, object> body)
        {
            Request = request;
            Body = body;
        }

        public HttpRequest Request { get; private set; }

        /// <summary>The JSON object of a write; null for GET/HEAD.</summary>
        public Dictionary<string, object> Body { get; private set; }

        /// <summary>The caller's session (null only on the routes that do not need one: ping, session open).</summary>
        public AdminSession Session { get; internal set; }

        /// <summary>Percent-decoded route value, or null.</summary>
        public string Route(string name)
        {
            return Request.RouteValue(name);
        }

        /// <summary>Decoded query parameter, or null.</summary>
        public string Query(string name)
        {
            return Request.Query.Get(name);
        }
    }

    /// <summary>Who the page is about (§6.6).</summary>
    public sealed class CurrentPlayer
    {
        public string AccountId { get; set; }

        /// <summary>Full "anonymous:&lt;name&gt;", or null (known only for the sources "game" and "session").</summary>
        public string Credential { get; set; }

        /// <summary>"game", "session", "device", "single", or null.</summary>
        public string Source { get; set; }

        public CurrentPlayer Clone()
        {
            return new CurrentPlayer { AccountId = AccountId, Credential = Credential, Source = Source };
        }
    }

    /// <summary>
    /// The admin API framework, for the admin modules: <c>context.Features.Get&lt;AdminApi&gt;()</c> after the "admin"
    /// module registered (design §8.1).
    /// <para>
    /// <see cref="Get"/>/<see cref="Post"/>/<see cref="Delete"/> prefix the template with <see cref="Prefix"/>, register
    /// the route's body limit for the header-time check (§5.2 rule 5) and, before the handler, check in this order:
    /// admin.enabled (503 admin_disabled), the session cookie (401 no_session / session_expired), and for writes the CSRF
    /// header (403, also checked by the gate), the body limit (413 too_large), <c>Content-Type: application/json</c>
    /// (415), a JSON object body (400 bad_request), the shared write limit of 60 per minute (429) and a running restart
    /// (409 restarting). A handler exception becomes 500 internal, logged with the route name and never with the body.
    /// Every answer carries <c>Cache-Control: no-store</c> and <c>X-Content-Type-Options: nosniff</c>.
    /// </para>
    /// </summary>
    public sealed class AdminApi
    {
        public const string Prefix = "_mcvs/api/v1/";

        /// <summary>Body limit of GET and DELETE routes (and of POST routes that pass a smaller one).</summary>
        public const int DefaultBodyLimit = 1024;

        public const string WriteBucket = "write";
        public const int WritesPerMinute = 60;

        private readonly Router router;
        private readonly AdminSecurity security;
        private readonly CurrentPlayerResolver resolver;
        private readonly object gate = new object();
        private readonly List<KeyValuePair<string, Func<object>>> statusSections = new List<KeyValuePair<string, Func<object>>>();

        internal AdminApi(Router router, ServerContext context, AdminSecurity security, GameSaveTracker saves, CurrentPlayerResolver resolver)
        {
            this.router = router;
            Context = context;
            this.security = security;
            this.resolver = resolver;
            Saves = saves;
            Log = context.Logger.For("admin");
        }

        public ServerContext Context { get; private set; }

        /// <summary>Category "admin".</summary>
        public Log Log { get; private set; }

        /// <summary>context.Features.Get&lt;IAdminHost&gt;() at call time; may be null (standalone host).</summary>
        public IAdminHost Host
        {
            get { return Context.Features.Get<IAdminHost>(); }
        }

        public GameSaveTracker Saves { get; private set; }

        /// <summary>Set by admin-saves at registration (LocalProfileStore.CreateBackup); null until then.</summary>
        public Func<string, string, BackupInfo> CreateBackup { get; set; }   // (accountId, reason) → backup or null

        /// <summary>settings.json <c>admin.enabled</c> (default true), read on every call: the file-only kill switch.</summary>
        public bool Enabled
        {
            get { return Context.Settings.GetBool("admin.enabled", true); }
        }

        /// <summary>An ExitSequence of kind "restart" runs: every admin write answers 409 restarting.</summary>
        public bool IsRestarting
        {
            get { return Exit != null && Exit.State == ExitSequence.Restarting; }
        }

        /// <summary>"idle", "saving" (save now) or "restarting". While not idle, take no account lock (§4.5).</summary>
        public string ExitState
        {
            get { return Exit == null ? ExitSequence.Idle : Exit.State; }
        }

        internal ExitSequence Exit { get; set; }

        internal AdminSecurity Security
        {
            get { return security; }
        }

        public void Get(string template, string name, AdminHandler handler)
        {
            Map("GET", template, name, handler, DefaultBodyLimit, SessionMode.Required);
        }

        public void Post(string template, string name, AdminHandler handler, int maxBodyBytes)
        {
            Map("POST", template, name, handler, maxBodyBytes, SessionMode.Required);
        }

        public void Delete(string template, string name, AdminHandler handler)
        {
            Map("DELETE", template, name, handler, DefaultBodyLimit, SessionMode.Required);
        }

        /// <summary>
        /// The current player (§6.6). While a save-now or restart sequence runs, the player it was started for (no
        /// profile is read then).
        /// </summary>
        public CurrentPlayer CurrentPlayer()
        {
            ExitSequence exit = Exit;
            if (exit != null && exit.State != ExitSequence.Idle && exit.Player != null) return exit.Player.Clone();
            return resolver.Resolve(Host);
        }

        /// <summary>The shared rate limiter: true (and counted) while the bucket had fewer than perMinute events in the last minute.</summary>
        public bool Allow(string bucket, int perMinute)
        {
            return security.Limiter.Allow(bucket, perMinute);
        }

        /// <summary>429 rate_limited with details.retryAfterSeconds for a bucket that <see cref="Allow"/> refused.</summary>
        public HttpResponse RateLimited(string bucket, int perMinute)
        {
            int retry = security.Limiter.RetryAfterSeconds(bucket, perMinute);
            return AdminJson.Error(AdminJson.TooManyRequests, "rate_limited", "Too many requests. Try again in a moment.",
                                   AdminJson.Details("retryAfterSeconds", (long)retry));
        }

        /// <summary>
        /// Adds (or replaces) a section of <c>GET status</c>, e.g. admin-saves' <c>"staged"</c>. The provider runs on every
        /// status request and must not take an account lock while <see cref="ExitState"/> is not idle; a throwing provider
        /// gives null.
        /// </summary>
        public void SetStatusSection(string name, Func<object> provider)
        {
            if (string.IsNullOrEmpty(name) || provider == null) throw new ArgumentException("name and provider required");
            lock (gate)
            {
                statusSections.RemoveAll(kv => kv.Key == name);
                statusSections.Add(new KeyValuePair<string, Func<object>>(name, provider));
            }
        }

        internal List<KeyValuePair<string, Func<object>>> StatusSections
        {
            get { lock (gate) return new List<KeyValuePair<string, Func<object>>>(statusSections); }
        }

        /// <summary>"anonymous:a1b2…f9e8": a credential with only the first and last 4 characters of its user name.</summary>
        public static string MaskCredential(string credential)
        {
            if (string.IsNullOrEmpty(credential)) return credential;
            int colon = credential.IndexOf(':');
            string type = colon < 0 ? "" : credential.Substring(0, colon + 1);
            string name = colon < 0 ? credential : credential.Substring(colon + 1);
            if (name.Length <= 8) return type + (name.Length <= 4 ? name : name.Substring(0, 4)) + "…";
            return type + name.Substring(0, 4) + "…" + name.Substring(name.Length - 4);
        }

        // ------------------------------------------------------------------ the route wrapper

        internal enum SessionMode
        {
            /// <summary>401 without a valid session.</summary>
            Required,

            /// <summary>The handler runs with or without a session (ping).</summary>
            Optional,

            /// <summary>No session involved and no write limit: the ticket exchange (POST session).</summary>
            Ticket,
        }

        internal void Map(string method, string template, string name, AdminHandler handler, int maxBodyBytes, SessionMode mode)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            string full = Prefix + template.TrimStart('/');
            security.RegisterLimit(method, full, maxBodyBytes);
            router.Map(method, full, name, request => Run(request, name, handler, maxBodyBytes, mode));
        }

        private HttpResponse Run(HttpRequest request, string name, AdminHandler handler, int maxBodyBytes, SessionMode mode)
        {
            HttpResponse response;
            try
            {
                response = Check(request, maxBodyBytes, mode, handler);
            }
            catch (Exception ex)
            {
                Log.Error("admin: route " + name + " failed", ex);
                response = AdminJson.Error(HttpStatus.InternalServerError, "internal", "Something went wrong. The details are in server.log.");
            }
            return AdminJson.WithApiHeaders(response ?? AdminJson.Ok(new Dictionary<string, object>(StringComparer.Ordinal)));
        }

        private HttpResponse Check(HttpRequest request, int maxBodyBytes, SessionMode mode, AdminHandler handler)
        {
            if (!Enabled) return Disabled();

            AdminSession session = mode == SessionMode.Ticket ? null : security.SessionOf(request, true);
            if (mode == SessionMode.Required && session == null) return NoSession(request);
            if (session != null) PageAlive();

            Dictionary<string, object> body = null;
            if (LocalAccessGate.IsUnsafeMethod(request.Method))
            {
                HttpResponse refused = CheckWrite(request, maxBodyBytes, out body);
                if (refused != null) return refused;
                if (mode != SessionMode.Ticket && !Allow(WriteBucket, WritesPerMinute)) return RateLimited(WriteBucket, WritesPerMinute);
                if (IsRestarting) return Restarting();
            }
            var call = new AdminCall(request, body);
            call.Session = session;
            return handler(call);
        }

        /// <summary>The write checks after the session: CSRF header, size, JSON content type, a JSON object body.</summary>
        internal static HttpResponse CheckWrite(HttpRequest request, long maxBodyBytes, out Dictionary<string, object> body)
        {
            body = null;
            // The gate checks this for every /_mcvs write; repeated here in case a route is ever reached around it.
            string csrf = request.Headers.Get(LocalAccessGate.HeaderName);
            if (csrf == null || csrf.Trim() != "1")
                return AdminJson.Error(HttpStatus.Forbidden, "csrf", "This request was refused. Use the MCVS Offline page inside the game.");
            if (request.Body.Length > maxBodyBytes) return AdminJson.TooLarge(maxBodyBytes);
            if (!LocalAccessGate.IsMediaType(request.ContentType, "application/json"))
                return AdminJson.Error(AdminJson.UnsupportedMediaType, "unsupported_media_type", "Send JSON (Content-Type: application/json).");
            object parsed;
            if (request.Body.Length == 0 || !MiniJson.TryParse(request.BodyText, out parsed) || !(parsed is Dictionary<string, object>))
                return AdminJson.Error(HttpStatus.BadRequest, "bad_request", "The request body must be a JSON object.");
            body = (Dictionary<string, object>)parsed;
            return null;
        }

        internal static HttpResponse Disabled()
        {
            return AdminJson.Error(HttpStatus.ServiceUnavailable, "admin_disabled",
                                   "The MCVS Offline page is turned off (admin.enabled is false in settings.json).");
        }

        internal static HttpResponse NoSession(HttpRequest request)
        {
            bool expired = AdminSecurity.HasSessionCookie(request);
            return AdminJson.Error(HttpStatus.Unauthorized, expired ? "session_expired" : "no_session",
                                   "Open this page from the game: Settings > Account > MCVS Offline, or the MCVS Offline message in the inbox.");
        }

        internal static HttpResponse Restarting()
        {
            return AdminJson.Error(HttpStatus.Conflict, "restarting", "The game is restarting.");
        }

        /// <summary>Tells the host the page is open (watchdog lease, §8.5). Never throws.</summary>
        internal void PageAlive()
        {
            IAdminHost host = Host;
            if (host == null) return;
            try
            {
                host.PageAlive();
            }
            catch (Exception ex)
            {
                Log.Debug("admin: PageAlive failed: " + ex.Message);
            }
        }

        /// <summary>The host's description, or null (no host, or it threw).</summary>
        internal AdminHostInfo DescribeHost()
        {
            IAdminHost host = Host;
            if (host == null) return null;
            try
            {
                return host.Describe();
            }
            catch (Exception ex)
            {
                Log.Warn("admin: the host's Describe failed", ex);
                return null;
            }
        }
    }
}
