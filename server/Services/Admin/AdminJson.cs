using System;
using System.Collections.Generic;
using McvsOffline.Server.Core.Http;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>
    /// Responses of the admin API (design §4.1): JSON, <c>Cache-Control: no-store</c>, <c>X-Content-Type-Options:
    /// nosniff</c>, never a CORS header. Errors are <c>{"error": code, "message": sentence, "details": {...}?}</c>.
    /// </summary>
    public static class AdminJson
    {
        // The statuses only the admin API uses (aliases of the core table's constants).
        public const int SeeOther = HttpStatus.SeeOther;
        public const int UnsupportedMediaType = HttpStatus.UnsupportedMediaType;
        public const int TooManyRequests = HttpStatus.TooManyRequests;

        /// <summary>200 with <paramref name="body"/> (a MiniJson value).</summary>
        public static HttpResponse Ok(object body)
        {
            return Json(body, HttpStatus.Ok);
        }

        public static HttpResponse Json(object body, int status)
        {
            return WithApiHeaders(HttpResponse.Json(body, status));
        }

        public static HttpResponse Error(int status, string code, string message, Dictionary<string, object> details = null)
        {
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["error"] = code;
            body["message"] = message;
            if (details != null) body["details"] = details;
            return Json(body, status);
        }

        /// <summary>413 too_large with details.limit.</summary>
        public static HttpResponse TooLarge(long limit)
        {
            return Error(HttpStatus.PayloadTooLarge, "too_large", "The request is too large.", Details("limit", limit));
        }

        /// <summary>One detail: <c>{"name": value}</c>.</summary>
        public static Dictionary<string, object> Details(string name, object value)
        {
            var details = new Dictionary<string, object>(StringComparer.Ordinal);
            details[name] = value;
            return details;
        }

        /// <summary>Adds the headers every admin API response carries (idempotent).</summary>
        public static HttpResponse WithApiHeaders(HttpResponse response)
        {
            if (response == null) return null;
            response.Headers.Set("Cache-Control", "no-store");
            response.Headers.Set("X-Content-Type-Options", "nosniff");
            if (response.ReasonPhrase == null) response.ReasonPhrase = HttpStatus.ReasonPhrase(response.StatusCode);
            return response;
        }
    }
}
