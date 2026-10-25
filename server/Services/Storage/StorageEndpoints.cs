using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>
    /// The storage HTTP endpoints (OP/StorageApi.cs; verify-C3 §2). Each handler authenticates the token (401, never
    /// 403, verify-C3 §3.1), resolves the credential in the path to an account, picks the account's store and relays
    /// its answer.
    /// <para>
    /// Credentials in paths: "me" is the token's subject (the for_username player for the game server's
    /// SessionForUser tokens); "fed_id:&lt;id&gt;" and its encoded form "fed_id%3A&lt;id&gt;" (both reach us, verify-C3
    /// §2.1; the router decodes path segments, and a doubly encoded "%253A" is decoded once more here) address the
    /// account directly; any other "type:user" is looked up in the account store. Any valid token may address any
    /// account: game-server tokens must read and write other players' <c>_mcvs.currentMatch</c> (storage_admin,
    /// verify-C3 §7.7), and refusing anything would have to be a 403, which the game treats as "profile missing".
    /// </para>
    /// </summary>
    internal sealed class StorageEndpoints
    {
        private static readonly string[] KnownOperations =
        {
            SeshatOperations.Set, SeshatOperations.Merge, SeshatOperations.Delete, SeshatOperations.Add,
            SeshatOperations.Increment, SeshatOperations.Append, SeshatOperations.BatchSet, SeshatOperations.BatchDelete,
        };

        private readonly ServerContext context;
        private readonly LocalProfileStore store;
        private readonly DataBlobStore blobs;
        private readonly Log log;

        public StorageEndpoints(ServerContext context, LocalProfileStore store, DataBlobStore blobs, Log log)
        {
            this.context = context;
            this.store = store;
            this.blobs = blobs;
            this.log = log ?? Log.Null;
        }

        public void Register(Router router)
        {
            // Batch reads (GameMatchmakingComponent MMR of party members, GameClanComponent languages) and two
            // searches the game never calls.
            router.Get("profiles", "storage.profiles.batch", BatchProfiles);
            router.Get("profiles/location/{credential}", "storage.profiles.nearme", r => Authenticated(r, caller => HttpResponse.EmptyArray()));
            router.Get("profiles/matchers/{name}/matches", "storage.profiles.matches", r => Authenticated(r, caller => HttpResponse.EmptyArray()));

            // The whole document.
            router.Get("profiles/{credential}/myprofile", "storage.profile.get", GetProfile);
            router.Post("profiles/{credential}/myprofile", "storage.profile.write", WriteProfile);
            router.Put("profiles/{credential}/myprofile", "storage.profile.write", WriteProfile);
            router.Delete("profiles/{credential}/myprofile", "storage.profile.delete", DeleteProfile);
            router.Post("profiles/{credential}/myprofile/delete", "storage.profile.delete", DeleteProfile);

            // Backups (only for Version 56 profiles, GM/ProfileManagerComponent.cs:130-178) and visibility objects.
            router.Get("profiles/{credential}/{name}/backups", "storage.backups.list", ListBackups);
            router.Get("profiles/{credential}/{name}/backups/{id}", "storage.backups.get", GetBackup);
            router.Get("profiles/{credential}/myprofile/visibility", "storage.visibility.get", GetVisibility);
            router.Post("profiles/{credential}/myprofile/visibility", "storage.visibility.set", SetVisibility);
            // Before the generic selector catch-all below (same template shape).
            router.Post("profiles/{credential}/myprofile/visibility/{*selector}", "storage.visibility.set", SetVisibility,
                        Router.CatchAllOrder - 100);

            // Dotted selectors: _mcvs.gameProfile, _mcvs.currentMatch, inventory, language, ...
            router.Get("profiles/{credential}/myprofile/{*selector}", "storage.field.get", GetProfile);
            router.Post("profiles/{credential}/myprofile/{*selector}", "storage.field.write", WriteProfile);
            router.Put("profiles/{credential}/myprofile/{*selector}", "storage.field.write", WriteProfile);
            router.Delete("profiles/{credential}/myprofile/{*selector}", "storage.field.delete", DeleteField);

            // Seshat data objects (BinaryProfileSection; unused by the game).
            router.Get("data", "storage.data.search", SearchData);
            router.Get("data/{credential}/{*key}", "storage.data.get", GetData);
            router.Post("data/{credential}/{*key}", "storage.data.write", WriteData);

            // Diagnostics for whoever runs the device (not used by the game).
            router.Get("_mcvs/storage", "storage.diag.list", DiagnosticsList);
            router.Get("_mcvs/storage/profiles/{account}", "storage.diag.profile", DiagnosticsProfile);
        }

        // ------------------------------------------------------------------ profile endpoints

        private HttpResponse GetProfile(HttpRequest request)
        {
            return Authenticated(request, caller =>
            {
                StorageTarget target;
                HttpResponse error;
                if (!ResolveTarget(request, caller, false, out target, out error)) return error;
                string selector = Selector(request);
                List<string> fields = IncludeFields(request);
                IProfileStore s = Locate(target);
                StorageResult result = s.Get(target, selector, fields);
                log.Debug("read " + Describe(target, selector) + (fields.Count > 0 ? " include_fields=" + fields.Count.ToString(CultureInfo.InvariantCulture) : "")
                          + " via " + s.Name + " -> " + Status(result) + ", " + result.Body.Length.ToString(CultureInfo.InvariantCulture) + " chars");
                return result.ToResponse();
            });
        }

        private HttpResponse WriteProfile(HttpRequest request)
        {
            return Authenticated(request, caller =>
            {
                StorageTarget target;
                HttpResponse error;
                if (!ResolveTarget(request, caller, true, out target, out error)) return error;
                var write = new StorageWrite();
                write.Selector = Selector(request);
                write.Operation = Param(request, "operation");
                write.ObjectText = Param(request, "object");
                write.Visibility = Param(request, "visibility");
                write.IfMatch = request.Headers.Get("If-Match");
                return ApplyWrite(request, target, write);
            });
        }

        private HttpResponse DeleteField(HttpRequest request)
        {
            return Authenticated(request, caller =>
            {
                StorageTarget target;
                HttpResponse error;
                if (!ResolveTarget(request, caller, true, out target, out error)) return error;
                var write = new StorageWrite();
                write.Selector = Selector(request);
                write.Operation = SeshatOperations.Delete;
                return ApplyWrite(request, target, write);
            });
        }

        private HttpResponse ApplyWrite(HttpRequest request, StorageTarget target, StorageWrite write)
        {
            string op = write.EffectiveOperation;
            if (Array.IndexOf(KnownOperations, op) < 0)
            {
                // Never seen from the game; log everything so a device test shows what is missing, and do not fail.
                log.Warn("unknown storage operation '" + op + "' for " + Describe(target, write.Selector) + "; nothing changed\n"
                         + RequestLogger.Describe(request, context.Settings.LogBodyLimit));
                return HttpResponse.EmptyObject();
            }
            if (!string.IsNullOrEmpty(write.IfMatch))
                log.Debug("If-Match '" + write.IfMatch + "' ignored for " + Describe(target, write.Selector) + " (no ETags, verify-C3 §3.5)");

            IProfileStore s = Locate(target);
            StorageResult result = s.Write(target, write);
            string size = write.ObjectText == null ? "no object" : write.ObjectText.Length.ToString(CultureInfo.InvariantCulture) + " chars";
            string line = op + " " + Describe(target, write.Selector) + " (" + size + ") via " + s.Name + " -> " + Status(result);
            if (result.IsOk) log.Debug(line);
            else log.Warn(line + ": " + result.Body);
            return result.ToResponse();
        }

        private HttpResponse DeleteProfile(HttpRequest request)
        {
            return Authenticated(request, caller =>
            {
                StorageTarget target;
                HttpResponse error;
                if (!ResolveTarget(request, caller, true, out target, out error)) return error;
                IProfileStore s = Locate(target);
                StorageResult result = s.DeleteProfile(target);
                log.Info("profile delete requested for " + target.AccountId + " by " + DescribeCaller(caller) + " via " + s.Name + " -> " + Status(result));
                return result.ToResponse();
            });
        }

        private HttpResponse BatchProfiles(HttpRequest request)
        {
            return Authenticated(request, caller =>
            {
                List<string> credentials = JsonPath.SplitFieldList(Param(request, "credentials"));
                List<string> fields = IncludeFields(request);
                // One entry per requested credential, in order (callers index the array, e.g.
                // x["_mcvs"]["snapshot"]["MatchMakingRating"] in GM/GameMatchmakingComponent.cs:1029).
                var sb = new StringBuilder("[");
                for (int i = 0; i < credentials.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    StorageTarget target;
                    string body = "{}";
                    if (TryResolveCredential(credentials[i], caller, false, out target))
                    {
                        StorageResult result = Locate(target).Get(target, "", fields);
                        if (result.IsOk && result.Body.Length > 0) body = result.Body;
                    }
                    else
                    {
                        log.Warn("batch profiles: invalid credential '" + credentials[i] + "' answered {}");
                    }
                    sb.Append(body);
                }
                sb.Append(']');
                return HttpResponse.RawJson(sb.ToString());
            });
        }

        private HttpResponse ListBackups(HttpRequest request)
        {
            return Authenticated(request, caller =>
            {
                StorageTarget target;
                HttpResponse error;
                if (!ResolveTarget(request, caller, false, out target, out error)) return error;
                string name = request.RouteValue("name");
                if (!string.Equals(name, "myprofile", StringComparison.OrdinalIgnoreCase))
                    log.Warn("backups requested for profile name '" + name + "'; answering with the myprofile backups");
                DateTime? from = ParseDate(Param(request, "start_date"));
                DateTime? to = ParseDate(Param(request, "end_date"));
                return Locate(target).ListBackups(target, from, to).ToResponse();
            });
        }

        private HttpResponse GetBackup(HttpRequest request)
        {
            return Authenticated(request, caller =>
            {
                StorageTarget target;
                HttpResponse error;
                if (!ResolveTarget(request, caller, false, out target, out error)) return error;
                return Locate(target).GetBackup(target, request.RouteValue("id")).ToResponse();
            });
        }

        private HttpResponse GetVisibility(HttpRequest request)
        {
            return Authenticated(request, caller =>
            {
                StorageTarget target;
                HttpResponse error;
                if (!ResolveTarget(request, caller, false, out target, out error)) return error;
                return Locate(target).GetVisibility(target).ToResponse();
            });
        }

        private HttpResponse SetVisibility(HttpRequest request)
        {
            return Authenticated(request, caller =>
            {
                StorageTarget target;
                HttpResponse error;
                if (!ResolveTarget(request, caller, true, out target, out error)) return error;
                return Locate(target).SetVisibility(target, Selector(request), Param(request, "visibility")).ToResponse();
            });
        }

        // ------------------------------------------------------------------ data objects

        private HttpResponse SearchData(HttpRequest request)
        {
            return Authenticated(request, caller => HttpResponse.Json(blobs.Search(caller.AccountId, Param(request, "category"))));
        }

        private HttpResponse GetData(HttpRequest request)
        {
            return Authenticated(request, caller =>
            {
                StorageTarget target;
                HttpResponse error;
                if (!ResolveTarget(request, caller, false, out target, out error)) return error;
                string key = request.RouteValue("key");
                Dictionary<string, object> entry = string.IsNullOrEmpty(key) ? null : blobs.Get(target.AccountId, key);
                if (entry == null) return HttpResponse.Error(HttpStatus.NotFound, "no data object '" + key + "'");
                return HttpResponse.Text(JsonValue.GetString(entry, "data", ""), HttpStatus.Ok, "application/octet-stream");
            });
        }

        private HttpResponse WriteData(HttpRequest request)
        {
            return Authenticated(request, caller =>
            {
                StorageTarget target;
                HttpResponse error;
                if (!ResolveTarget(request, caller, true, out target, out error)) return error;
                string key = request.RouteValue("key") ?? "";
                const string deleteSuffix = "/delete";
                if (key.EndsWith(deleteSuffix, StringComparison.Ordinal))
                {
                    blobs.Delete(target.AccountId, key.Substring(0, key.Length - deleteSuffix.Length));
                    return HttpResponse.EmptyObject();
                }
                if (key.Length == 0) return HttpResponse.Error(HttpStatus.BadRequest, "data key required");
                blobs.Put(target.AccountId, key, Param(request, "data"), Param(request, "visibility"), Param(request, "category"),
                          Param(request, "expiration"), Param(request, "indexes"), Param(request, "meta_data"));
                return HttpResponse.EmptyObject();
            });
        }

        // ------------------------------------------------------------------ diagnostics

        private HttpResponse DiagnosticsList(HttpRequest request)
        {
            if (!IsFromThisDevice(request)) return NotFromThisDevice();
            return Guarded(request, () =>
            {
                var list = new List<object>();
                foreach (string id in store.AccountIds())
                {
                    var item = ProfileJson.NewObject();
                    item["account"] = id;
                    Account account = context.Accounts.FindById(id);
                    item["credentials"] = account == null ? new List<object>() : new List<object>(account.Credentials.ConvertAll(c => (object)c));
                    try
                    {
                        ProfileDocument doc = store.ReadDocument(id);
                        item["revision"] = doc.Revision;
                        item["created"] = doc.Created;
                        item["modified"] = doc.Modified;
                        item["hasGameProfile"] = SeshatOperations.HasGameProfile(doc);
                    }
                    catch (StorageUnavailableException ex)
                    {
                        item["error"] = ex.Message;
                    }
                    item["size"] = FileSize(store.PathFor(id));
                    item["backups"] = (long)store.Backups.List(id).Count;
                    list.Add(item);
                }
                var body = ProfileJson.NewObject();
                body["directory"] = store.ProfilesDirectory;
                body["profiles"] = list;
                return HttpResponse.Json(body);
            });
        }

        private HttpResponse DiagnosticsProfile(HttpRequest request)
        {
            if (!IsFromThisDevice(request)) return NotFromThisDevice();
            return Guarded(request, () =>
            {
                string id = request.RouteValue("account") ?? "";
                if (id.StartsWith(McvsConstants.FedIdPrefix, StringComparison.Ordinal)) id = id.Substring(McvsConstants.FedIdPrefix.Length);
                if (request.Query.GetBool("raw", false))
                {
                    string text = AtomicFile.ReadAllTextOrNull(store.PathFor(id));
                    return text == null ? HttpResponse.Error(HttpStatus.NotFound, "no stored profile for '" + id + "'") : HttpResponse.RawJson(text);
                }
                ProfileDocument doc = store.ReadDocument(id);
                if (!doc.IsStored) return HttpResponse.Error(HttpStatus.NotFound, "no stored profile for '" + id + "'");
                return HttpResponse.RawJson(ProfileJson.Serialize(store.BuildView(doc), true));
            });
        }

        private static bool IsFromThisDevice(HttpRequest request)
        {
            return request.RemoteEndPoint == null || IPAddress.IsLoopback(request.RemoteEndPoint.Address);
        }

        private static HttpResponse NotFromThisDevice()
        {
            return HttpResponse.Error(HttpStatus.NotFound, "storage diagnostics are only served to this device");
        }

        private static long FileSize(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (IOException)
            {
                return 0;
            }
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>Runs <paramref name="handler"/> for a valid token; 401 otherwise (report 02 §1.6: one re-authorize, then fail).</summary>
        private HttpResponse Authenticated(HttpRequest request, Func<TokenInfo, HttpResponse> handler)
        {
            string token = Param(request, "access_token");
            if (string.IsNullOrEmpty(token))
            {
                string authorization = request.Headers.Get("Authorization");
                if (authorization != null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    token = authorization.Substring(7).Trim();
            }
            if (string.IsNullOrEmpty(token)) return Unauthorized(request, "no access_token");
            TokenInfo caller = context.Tokens.Resolve(token);
            if (caller == null || string.IsNullOrEmpty(caller.AccountId))
            {
                string issuer = TokenService.GetIssuer(token);
                return Unauthorized(request, "unknown access token (issuer " + (issuer ?? "?")
                                             + (context.Tokens.IsLocal(token) ? ", this server" : ", not this server") + ")");
            }
            return Guarded(request, () => handler(caller));
        }

        private HttpResponse Guarded(HttpRequest request, Func<HttpResponse> handler)
        {
            try
            {
                return handler();
            }
            catch (StorageUnavailableException ex)
            {
                // 503 is retried by the client (report 02 §1.6); answering "missing" instead would reset the profile.
                log.Error("storage unavailable for " + request.Method + " " + request.Path + ": " + ex.Message, ex.InnerException);
                return HttpResponse.Error(HttpStatus.ServiceUnavailable, "profile storage unavailable");
            }
        }

        private HttpResponse Unauthorized(HttpRequest request, string reason)
        {
            log.Warn("401 for " + request.Method + " " + request.Path + ": " + reason);
            return HttpResponse.Error(HttpStatus.Unauthorized, reason);
        }

        private bool ResolveTarget(HttpRequest request, TokenInfo caller, bool forWrite, out StorageTarget target, out HttpResponse error)
        {
            error = null;
            string raw = request.RouteValue("credential");
            if (TryResolveCredential(raw, caller, forWrite, out target)) return true;
            log.Warn("invalid credential '" + request.RouteValues.GetRaw("credential") + "' in " + request.Method + " " + request.RawPath);
            error = HttpResponse.Error(HttpStatus.BadRequest, "invalid credential '" + raw + "'");
            return false;
        }

        private bool TryResolveCredential(string text, TokenInfo caller, bool forWrite, out StorageTarget target)
        {
            target = null;
            if (string.IsNullOrEmpty(text)) return false;
            string decoded = text.Trim();
            if (decoded.IndexOf('%') >= 0) decoded = UrlEncoding.Decode(decoded, false); // "fed_id%253A..." (double-encoded)

            Credential credential;
            if (!Credential.TryParse(decoded, out credential)) return false;
            if (credential.IsMe)
            {
                target = new StorageTarget(caller.AccountId, caller.FedId, caller);
                return true;
            }
            if (!credential.IsValid) return false;

            string normalized = credential.ToString();
            string accountId;
            if (credential.Type == Credential.FedId)
            {
                accountId = credential.Username;
            }
            else
            {
                Account account = context.Accounts.Find(normalized);
                if (account != null) accountId = account.Id;
                else if (forWrite) accountId = context.Accounts.GetOrCreate(normalized).Id;
                else accountId = AccountStore.DeriveAccountId(normalized); // never created: reads as an empty profile
            }
            target = new StorageTarget(accountId, normalized, caller);
            if (accountId != caller.AccountId)
                log.Trace(DescribeCaller(caller) + " addresses " + normalized + " (account " + accountId + ")");
            return true;
        }

        private IProfileStore Locate(StorageTarget target)
        {
            IProfileStoreLocator locator;
            if (context.Features.TryGet(out locator))
            {
                try
                {
                    IProfileStore located = locator.Locate(target);
                    if (located != null) return located;
                }
                catch (Exception ex)
                {
                    log.Error("profile store locator failed for " + target.AccountId + "; using the local store", ex);
                }
            }
            return store;
        }

        /// <summary>
        /// The selector from the path: the rest of the path after "myprofile/", split on '.' only (verify-C3 §7.6).
        /// A '/' stays part of a key, as in the dump's "/Profile" ("_mcvs.gameProfile.$default./Profile.Version").
        /// </summary>
        private static string Selector(HttpRequest request)
        {
            return request.RouteValue("selector") ?? "";
        }

        /// <summary>include_fields from the query and/or form, comma-separated, possibly repeated.</summary>
        private static List<string> IncludeFields(HttpRequest request)
        {
            var result = new List<string>();
            var values = new List<string>(request.Query.GetAll("include_fields"));
            values.AddRange(request.Form.GetAll("include_fields"));
            foreach (string value in values)
            {
                foreach (string field in JsonPath.SplitFieldList(value))
                {
                    if (!result.Contains(field)) result.Add(field);
                }
            }
            return result;
        }

        /// <summary>A parameter from the form, the query, or (if the body is JSON) the body object.</summary>
        private static string Param(HttpRequest request, string name)
        {
            string value = request.Param(name);
            if (value != null) return value;
            var json = request.Json as Dictionary<string, object>;
            object v;
            if (json == null || !json.TryGetValue(name, out v) || v == null) return null;
            return v as string ?? MiniJson.Serialize(v);
        }

        private static DateTime? ParseDate(string text)
        {
            DateTime value;
            if (!string.IsNullOrEmpty(text) && TimeFormat.TryParse(text, out value)) return value;
            return null;
        }

        private static string Status(StorageResult result)
        {
            return result.Status.ToString(CultureInfo.InvariantCulture);
        }

        private static string Describe(StorageTarget target, string selector)
        {
            return target.AccountId + (string.IsNullOrEmpty(selector) ? " (root)" : " '" + selector + "'");
        }

        private static string DescribeCaller(TokenInfo caller)
        {
            return caller.Credential + (caller.IsOnBehalf ? " (on behalf, by " + caller.Actor + ")" : "");
        }
    }
}
