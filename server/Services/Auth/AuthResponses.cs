using System;
using System.Collections.Generic;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;

namespace McvsOffline.Server.Services.Auth
{
    /// <summary>Response bodies shared by the auth endpoints.</summary>
    internal static class AuthResponses
    {
        /// <summary>
        /// An error with a JSON body {"error": code, "description": text}. Status codes follow report 02 §1.6: 401 for
        /// token/credential problems (the client re-authorizes once), 400/404/409 for the rest (the client aborts the
        /// request). 403 is never used: it means "banned" on authorize and "upgrade required" on eve.
        /// </summary>
        public static HttpResponse Error(int status, string code, string description)
        {
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["error"] = code;
            if (!string.IsNullOrEmpty(description)) body["description"] = description;
            return HttpResponse.Json(body, status);
        }

        public static HttpResponse BadRequest(string description)
        {
            return Error(HttpStatus.BadRequest, "invalid_request", description);
        }

        public static HttpResponse NotFound(string description)
        {
            return Error(HttpStatus.NotFound, "not_found", description);
        }

        /// <summary>
        /// Account info as users/me and users/{credential} return it (OP/Session.cs GetAccountInfoTask): "account" is
        /// the bare id (the client prefixes "fed_id:"; it must not contain ':'), "credentials" are "type:username"
        /// strings that each must parse as a client Credential (exactly one ':').
        /// </summary>
        public static Dictionary<string, object> AccountInfo(Account account)
        {
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["account"] = account.Id;
            var credentials = new List<object>();
            foreach (string c in account.Credentials) credentials.Add(c);
            body["credentials"] = credentials;
            return body;
        }
    }
}
