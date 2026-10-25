using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>The account a storage request is about, and who asked.</summary>
    public sealed class StorageTarget
    {
        public StorageTarget(string accountId, string credential, TokenInfo caller)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("account id required", "accountId");
            AccountId = accountId;
            Credential = string.IsNullOrEmpty(credential) ? McvsConstants.FedIdPrefix + accountId : credential;
            Caller = caller;
        }

        /// <summary>The account whose document is addressed (also the storage key).</summary>
        public string AccountId { get; private set; }

        /// <summary>
        /// The credential as addressed, with "me" resolved: "fed_id:&lt;id&gt;" or e.g. "anonymous:&lt;user&gt;".
        /// A forwarding store uses it to build the remote path.
        /// </summary>
        public string Credential { get; private set; }

        /// <summary>The token that made the request (null for in-process callers).</summary>
        public TokenInfo Caller { get; private set; }

        public string FedId { get { return McvsConstants.FedIdPrefix + AccountId; } }

        public override string ToString()
        {
            return AccountId;
        }
    }

    /// <summary>An HTTP-level storage answer: status and JSON body, relayed unchanged to the game.</summary>
    public sealed class StorageResult
    {
        public StorageResult(int status, string body)
        {
            Status = status;
            Body = body ?? "";
        }

        public int Status { get; private set; }

        public string Body { get; private set; }

        public bool IsOk { get { return Status == HttpStatus.Ok; } }

        /// <summary>200 with the given JSON text.</summary>
        public static StorageResult Ok(string json)
        {
            return new StorageResult(HttpStatus.Ok, json);
        }

        /// <summary>200 "{}": the answer to every successful write (verify-C3 §7.2, §7.8: exactly 200, valid JSON).</summary>
        public static StorageResult OkEmpty()
        {
            return new StorageResult(HttpStatus.Ok, "{}");
        }

        public static StorageResult Error(int status, string message)
        {
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["error"] = message ?? HttpStatus.ReasonPhrase(status);
            return new StorageResult(status, MiniJson.Serialize(body));
        }

        public HttpResponse ToResponse()
        {
            return HttpResponse.RawJson(Body, Status);
        }
    }

    /// <summary>
    /// Where profile operations for an account are carried out. <see cref="LocalProfileStore"/> keeps documents on
    /// this device. The LAN stage (roadmap M3, verify-C6 §3 topology B) can register an
    /// <see cref="IProfileStoreLocator"/> that returns a forwarding implementation for accounts whose home is another
    /// device, so the guest's own server stays the single source of truth for its profile (report 03 §14).
    /// <para>
    /// Every method returns the exact status and body to send to the game, so a forwarder can relay the remote
    /// answer unchanged. Implementations must keep the storage rules: 200 for every successful write, never
    /// 403/404 for existing data, no ETags, per-account ordering with read-your-writes.
    /// </para>
    /// </summary>
    public interface IProfileStore
    {
        /// <summary>Short name for logs ("local", "lan:192.168.1.20:51780", ...).</summary>
        string Name { get; }

        /// <summary>GET profiles/{cred}/myprofile[/{selector}][?include_fields=...].</summary>
        StorageResult Get(StorageTarget target, string selector, IList<string> includeFields);

        /// <summary>POST profiles/{cred}/myprofile[/{selector}] (set, merge, delete, ...).</summary>
        StorageResult Write(StorageTarget target, StorageWrite write);

        /// <summary>POST profiles/{cred}/myprofile/delete.</summary>
        StorageResult DeleteProfile(StorageTarget target);

        /// <summary>GET /profiles/{cred}/myprofile/backups?start_date=&amp;end_date= (JSON array).</summary>
        StorageResult ListBackups(StorageTarget target, DateTime? fromUtc, DateTime? toUtc);

        /// <summary>GET /profiles/{cred}/myprofile/backups/{id} (JSON object; "{}" for an unknown id).</summary>
        StorageResult GetBackup(StorageTarget target, string backupId);

        /// <summary>GET /profiles/me/myprofile/visibility (JSON object selector → visibility).</summary>
        StorageResult GetVisibility(StorageTarget target);

        /// <summary>POST /profiles/me/myprofile/visibility[/{selector}] visibility=...</summary>
        StorageResult SetVisibility(StorageTarget target, string selector, string visibility);
    }

    /// <summary>
    /// Chooses the store for an account. Register one with
    /// <c>context.Features.Set&lt;IProfileStoreLocator&gt;(...)</c>; it is consulted on every request, so it may be
    /// registered after the storage module. Return null to use the local store.
    /// </summary>
    public interface IProfileStoreLocator
    {
        IProfileStore Locate(StorageTarget target);
    }

    /// <summary>Raised when stored data exists but cannot be read; answered 503 (retried by the client), never "missing".</summary>
    public sealed class StorageUnavailableException : Exception
    {
        public StorageUnavailableException(string message, Exception inner = null) : base(message, inner) { }
    }
}
