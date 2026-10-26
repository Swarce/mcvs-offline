using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
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
    }

    /// <summary>
    /// The admin API framework, for the admin modules: <c>context.Features.Get&lt;AdminApi&gt;()</c> after the "admin"
    /// module registered. <see cref="Get"/>/<see cref="Post"/>/<see cref="Delete"/> prefix the template with
    /// <see cref="Prefix"/>, register the route's body limit for the header-time check and run the session, CSRF, size,
    /// admin.enabled and restarting checks before the handler (design §8.1).
    /// </summary>
    public sealed class AdminApi
    {
        public const string Prefix = "_mcvs/api/v1/";

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

        /// <summary>An ExitSequence of kind "restart" runs: every admin write answers 409 restarting.</summary>
        public bool IsRestarting
        {
            get { throw new NotImplementedException(); }
        }

        public void Get(string template, string name, AdminHandler handler)
        {
            throw new NotImplementedException();
        }

        public void Post(string template, string name, AdminHandler handler, int maxBodyBytes)
        {
            throw new NotImplementedException();
        }

        public void Delete(string template, string name, AdminHandler handler)
        {
            throw new NotImplementedException();
        }

        public CurrentPlayer CurrentPlayer()
        {
            throw new NotImplementedException();
        }

        /// <summary>The shared rate limiter: true (and counted) while the bucket had fewer than perMinute events in the last minute.</summary>
        public bool Allow(string bucket, int perMinute)
        {
            throw new NotImplementedException();
        }
    }
}
