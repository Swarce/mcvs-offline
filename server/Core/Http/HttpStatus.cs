using System;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>
    /// Status codes and reason phrases. How the game reacts to each status matters (report 02 §1.6):
    /// 2xx/302 are success; 408 and 5xx are retried up to 5 times; 401 drops all tokens and re-authorizes once;
    /// 403 from eve means "upgrade required" and from storage reads means "profile missing" (wipe, verify-C3 §3.1);
    /// any other 4xx aborts the request.
    /// </summary>
    public static class HttpStatus
    {
        public const int Continue = 100;
        public const int Ok = 200;
        public const int Created = 201;
        public const int Accepted = 202;
        public const int NoContent = 204;
        public const int Found = 302;
        public const int NotModified = 304;
        public const int BadRequest = 400;
        public const int Unauthorized = 401;
        public const int Forbidden = 403;
        public const int NotFound = 404;
        public const int MethodNotAllowed = 405;
        public const int RequestTimeout = 408;
        public const int Conflict = 409;
        public const int LengthRequired = 411;
        public const int PreconditionFailed = 412;
        public const int PayloadTooLarge = 413;
        public const int UriTooLong = 414;
        public const int ExpectationFailed = 417;
        public const int RequestHeaderFieldsTooLarge = 431;
        public const int InternalServerError = 500;
        public const int NotImplemented = 501;
        public const int ServiceUnavailable = 503;
        public const int HttpVersionNotSupported = 505;

        public static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 100: return "Continue";
                case 200: return "OK";
                case 201: return "Created";
                case 202: return "Accepted";
                case 204: return "No Content";
                case 206: return "Partial Content";
                case 301: return "Moved Permanently";
                case 302: return "Found";
                case 304: return "Not Modified";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 408: return "Request Timeout";
                case 409: return "Conflict";
                case 411: return "Length Required";
                case 412: return "Precondition Failed";
                case 413: return "Payload Too Large";
                case 414: return "URI Too Long";
                case 416: return "Range Not Satisfiable";
                case 417: return "Expectation Failed";
                case 431: return "Request Header Fields Too Large";
                case 500: return "Internal Server Error";
                case 501: return "Not Implemented";
                case 502: return "Bad Gateway";
                case 503: return "Service Unavailable";
                case 504: return "Gateway Timeout";
                case 505: return "HTTP Version Not Supported";
                default: return status >= 200 && status < 300 ? "OK" : "Status";
            }
        }

        /// <summary>Statuses whose responses never carry a body.</summary>
        public static bool AllowsBody(int status)
        {
            return status >= 200 && status != 204 && status != 304;
        }
    }

    /// <summary>A malformed or unacceptable request; the server answers <see cref="Status"/> and closes the connection.</summary>
    public sealed class HttpProtocolException : Exception
    {
        public HttpProtocolException(int status, string message) : base(message)
        {
            Status = status;
        }

        public int Status { get; private set; }
    }
}
