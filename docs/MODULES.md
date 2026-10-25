# Module guide

How the server is cut into modules, who owns what, and the core APIs every module builds on.
Read [ARCHITECTURE.md](ARCHITECTURE.md) first; the research notes in [research/](research/) are the evidence
(the `verify-C*` notes override the numbered reports where they disagree).

## 1. Ownership

Every group owns its folders outright. Nobody edits another group's files; if you need a change in a file you
do not own (including `server/Core`), ask the owner.

| Group | Owns |
|---|---|
| **AUTH** | `server/Services/Eve/`, `Pandora/`, `Auth/`, `docs/protocol/auth.md` |
| **STORAGE** | `server/Services/Storage/`, `docs/protocol/storage.md` |
| **CONTENT** | `server/Services/Crm/`, `Matchmaker/`, `Assets/`, `server/Content/`, `tools/content-gen/`, `docs/protocol/content.md` |
| **MISC** | `server/Services/Social/`, `Messaging/`, `Alerts/`, `Leaderboards/`, `GameStats/`, `Profanity/`, `Chat/`, `Clans/`, `Wall/`, `BanTool/`, `Geo/`, `Misc/`, `docs/protocol/misc.md` |
| **EMBED** | `hosts/embedded/`, `patcher/` |
| core (lead) | `server/Core/`, `server/Services/ServiceRegistry.cs`, `server/Services/Unlocks/` (cross-module glue for `unlockAll`), `server/build/`, `server/*.csproj`, `hosts/standalone/`, `tests/` core tests, `tests/McvsOffline.Tests/Conformance/`, `scripts/`, top-level docs |

Tests: add your own test files under `tests/McvsOffline.Tests/<Module>/` (e.g. `tests/McvsOffline.Tests/Auth/`);
you own that folder.

## 2. The module seam

Each module is one static class with one entry point, already wired into `ServiceRegistry`:

```csharp
namespace McvsOffline.Server.Services.Auth
{
    public static class AuthService
    {
        public static void Register(Router router, ServerContext context)
        {
            var log = context.Logger.For("auth");
            router.Post("authorize", "auth.authorize", request => Authorize(context, request));
            router.Get("users/me", "auth.users.me", request => Me(context, request));
        }
    }
}
```

Put any further classes of your module in your folder and namespace. `Register` runs once at startup; a module
whose `Register` throws is logged and skipped, the rest of the server still starts.

### Routes

- `router.Get/Post/Delete/Put/Any(template, name, handler[, order])`. Templates are relative to the host root
  (every service is served at the root, ARCHITECTURE §2.2); leading/trailing/double slashes do not matter.
- `{name}` captures one segment, `{*name}` (last) captures the rest (possibly empty). Literals match
  case-insensitively. Captured values are percent-decoded (`fed_id%3Aabc` → `fed_id:abc`);
  `request.RouteValues.GetRaw(name)` gives the raw form.
- Route names show up in every log line: use `module.action` (`storage.profile.get`).
- Ordering: ascending `order`, then registration order. Default order is `Router.DefaultOrder` (0) for exact
  templates and `Router.CatchAllOrder` (1000) for templates with `{*rest}`, so specific routes win over broad
  ones whatever module registered first. Core routes (`GET /`, `/_mcvs/*`) use `Router.CoreOrder` (10000):
  any module route overrides them (Eve may take over `GET /`).
- `HEAD` uses the `GET` route. Unmatched requests reach the router fallback, which logs the complete request
  as an unknown endpoint and answers `200 {}` (MISC wraps it to answer `[]` for list-shaped paths).
- Re-dispatching from inside a route: `request.WithTarget(target)` copies a request under another path/query;
  `router.TryMatch(request, skip, ...)` and `router.Dispatch(request, skip)` ignore the routes `skip` selects;
  `router.Invoke(route, request)` runs one chosen route (values bound, exceptions → logged 500). Pandora's
  service-prefix aliases (`/auth//authorize` → `/authorize`) are built on these.
- Diagnostics: `_mcvs/<module>` is reserved for module diagnostics (developer tools, never the game); answer them
  for loopback callers only when they expose player data. In use: `_mcvs/status`, `_mcvs/routes` (core),
  `_mcvs/storage`, `_mcvs/storage/profiles/{account}` (STORAGE), `GET _mcvs/crm`, `POST _mcvs/crm/reload`
  (CONTENT), `GET _mcvs/unlocks`, `POST _mcvs/unlocks/grant` (core/unlocks).

### Requests and responses

- `request.Param(name)`: form body first, then query (GET has params in the query, POST in the form body,
  DELETE in both; report 02 §1.4). Also `request.Query`, `request.Form`, `request.BodyText`, `request.Json`
  (when the content type is JSON), `request.Headers`, `request.Host`, `request.Segments`.
- Booleans arrive in C# casing (`True`/`False`): use `request.Form.GetBool(...)` / `JsonValue.AsBool`.
- `HttpResponse.Json(value)`, `RawJson(text)` (verbatim, e.g. stored profile text), `EmptyObject()` (`{}`),
  `EmptyArray()` (`[]`), `Text(text)` (plain text: pandora locate, encrypt_token), `Error(status, message)`,
  `Stream(writer, contentType)` (long-lived streams: alerts, chat listen; see `HttpResponseStream`).
- The server adds `Date`, `Content-Length`/chunked framing and `Connection`; it never adds an `ETag`.
- Status codes matter (report 02 §1.6, verify-C3 §7): 5xx/408 are retried 5×, 401 re-authorizes once,
  **403 is never correct** (eve: "upgrade required"; storage: profile wipe), other 4xx abort the request. JSON
  responses must not be empty: the game parses an empty body as null and usually throws.

### Logging

`context.Logger.For("module")` gives a category logger (`Trace/Debug/Info/Warn/Error`). Every request is
already logged by the core (summary + parameters at INFO, full request at DEBUG; unknown endpoints and handler
exceptions in full) to the console and `<data>/logs/server.log`; unknown endpoints also go to
`<data>/logs/unknown-endpoints.jsonl`. Log module decisions that would help a device tester, not the request.

## 3. Core APIs (`server/Core`)

| API | Use |
|---|---|
| `ServerContext` | `Settings`, `Logger`, `Clock`, `Content`, `Accounts`, `Tokens`, `Events`, `Features`, `DataDir`, `InstanceId`, `Port`, `BaseUrl`, `BaseUrlFor(request)`, `OpenStore(name)`, `StopSignal` |
| `context.BaseUrlFor(request)` | The URL to hand to a client (eve urls, pandora, chat `cmd_url`, lobbyconfigs): explicit port, trailing slash; Host-header based in LAN mode |
| `Settings` | Typed shared settings (`Port`, `Bind`, `LanMode`, `UnlockAll`, `DevMenu`, `InstantBots`, economy multipliers, `SuperCredential`, `SuperPassword`, `DataCenter`) plus `GetBool/GetLong/GetDouble/GetString/GetObject(path, default)` for module-specific keys. Put your keys under your module name (`"storage": {...}`); defaults stay in your code. `Override(path, value)` (command line, tests) is never saved; an object override deep-merges into the file's object, so deeper keys it does not name still come from the file |
| `AccountStore` (`context.Accounts`) | `GetOrCreate(credential, deviceId)`, `Find(credential or fed_id:x)`, `FindById`, `FindByDevice`, `LinkCredential`, `Update(id, mutate)`; `Account.Data` holds per-account module data. Account ids are derived from the credential (stable across devices and reinstalls) |
| `TokenService` (`context.Tokens`) | `Issue(account, credential, clientId, scopes, deviceId, actor)` → `<issuer>.<random>` token (same grant → same token), `Resolve(token)`, `GetIssuer`, `IsLocal`, `ParseScopes` |
| `Credential` | Parse/format `type:username` and `me` |
| `DocumentStore` (`context.OpenStore("name")`) | One JSON document per key in `<data>/<name>/`; atomic writes, per-key lock, `Read/Write/ReadText/WriteText/Update/Delete/Keys/Lock`. Unreadable files throw `DocumentStoreException` (never "missing") |
| `AtomicFile`, `KeyedLock`, `FileSystem` | Lower-level durable writes (temp + fsync + rename) and per-key locking |
| `MiniJson` | `Parse`, `ParseObject`, `ParseArray`, `TryParse`, `Serialize(value[, indented])`, `Quote`. Objects are `Dictionary<string, object>` (insertion order kept), arrays `List<object>`, integers `long`, other numbers `double`, huge integers `JsonNumber` |
| `JsonValue` | Lenient conversions (`AsString/AsLong/AsDouble/AsBool/AsObject/AsArray`, `GetString(obj, key, default)`…), `DeepClone`, `DeepEquals` |
| `JsonPath` | Dotted `Get/TryGet/Set/Remove`, `DeepMerge`, `Project` (include_fields, nested result), `SplitFieldList`, `RemoveKey` (order-preserving) |
| `IContentProvider` (`context.Content`) | `TryReadGameFile/TryReadGameText(logicalPath)` from the player's data pack (e.g. `Config/crmpreconfiguration.json`); `TryReadContent/TryReadContentText(name)` for our generated content (`server/Content`, overridable from `<data>/content/`) |
| `EventHub` (`context.Events`) | `Publish(topic, payload)` / `Subscribe(topic, handler)` between modules that must not reference each other |
| `FeatureRegistry` (`context.Features`) | `Set<T>(impl)` / `Get<T>()` for services one module provides to others (define the interface in the provider's folder); the registered features are listed below |
| `IClock` (`context.Clock`), `TimeFormat` | Current time (inject `ManualClock` in tests); `Iso8601`, `Rfc1123`, `ToUnixSeconds` |
| `McvsConstants` | `ClientId`, `DefaultPort`, `GameProfileSelector` (`_mcvs`), `FedIdPrefix` |
| `Num` | `IntMax`, `LongMax`, `PositiveInfinity`, `Max/Min/Clamp(long)`: the trimmed BCL lacks these |

### Features (`context.Features`)

| Feature | Provider | Use |
|---|---|---|
| `Auth.IAuthenticator` (`server/Services/Auth/Authenticator.cs`) | AUTH | The standard token check for new code. `Authenticate(request)` finds the token (form, query, JSON body `access_token`, `Authorization: Bearer`) and returns an `AuthIdentity` (`AccountId`, `FedId`, `Credential`, `Actor`, `IsOnBehalf`, `IsGameServer`) or null for a missing, unknown, foreign-issuer or orphaned token; answer that with `Unauthorized(request, reason)` (401, never 403, with a diagnostic log line). `ResolveAccount(identity, routeCredential)` resolves `me`, `fed_id:x` (also `%3A`) and `type:user`; `identity.IsGameServer` allows cross-account access (the GS writing `_mcvs.currentMatch` with its own token). STORAGE and MISC predate it and resolve tokens with the same rules through `context.Tokens` |
| `Storage.IProfileAccess` | STORAGE | In-process profile read/set/merge/remove under the account lock (durable, backed up, publishes `storage.profile.changed`); `AddToInventory`/`AddToLedger` for the IAP ledger grant channel; `Merge(..., backupReason)` backs up first |
| `Storage.IProfileStoreLocator` | (LAN stage, M3) | Optional: return a forwarding `IProfileStore` for accounts whose home is another device; asked on every storage request |
| `Storage.LocalProfileStore` | STORAGE | The local store itself (diagnostics, tests) |
| `Crm.CrmDocumentCache` | CONTENT | The current CRM document (built at startup on a warm-up thread; `POST _mcvs/crm/reload` rebuilds it) |
| `Social.ISocialEventProvider` (`server/Services/Social/SocialEvents.cs`) | any (optional) | Time-limited events ahead of `content/social/events.json` (rolling division seasons need code); events the TS cannot parse are dropped |
| `Alerts.AlertHub`, `Messaging.Mailboxes`, `Chat.ChatHub`, `Clans.ClanStore`, `Social.SocialProfiles` | MISC | Shared in-process hubs/stores of the MISC modules; other modules should prefer the EventHub topics below |
| `Unlocks.UnlockGranter` | core | `settings.unlockAll` through the inventory ledger (diagnostics, tests) |

### Event topics

Document every topic you publish here (topic, payload shape, publisher). Payloads are MiniJson values. Handlers run
synchronously on the publisher's thread (usually a request thread): do little, queue real work.

| Topic | Payload | Publisher → typical subscriber |
|---|---|---|
| `storage.profile.changed` | `{account, fedId, revision, operation, selector, deleted, source}`; `source` is `"http"` for game requests, `"api"` for `IProfileAccess` | STORAGE, after every saved change → unlocks (grants after the first game-profile save) |
| `storage.inventory.changed` | `{account, fedId, section, key, count, delta}` | STORAGE (`AddToLedger`) → whoever must tell a running TS to re-check the ledger |
| `alerts.push` | `{"to": "fed_id:<id>" \| "<account id>" \| "group:<id>", "alert": {"type": ..., ...}}` (`alert` may be a JSON string); `connection` alerts are dropped (they kick the player) | anyone → MISC Kairos streams. `{"type":"set_profile"}` makes the TS re-run `IapStoreComponent.CheckInventory` (verify-C5) |
| `messaging.post` | `{"to", "transport"? (default `secured`), "body", "type"?, "alert"? (default true), "label"?, "replaceMatchingLabel"?, "from"?, "fields"?}` | anyone → MISC Hermes mailboxes (inbox gifts, notices) |

## 4. Rules for shared code (it also runs inside the game)

All of `server/Core` and `server/Services` is compiled twice: for net8 and against the **trimmed Mono BCL from
the player's APK** (`McvsOffline.Server.Embedded.csproj`). `scripts/build.sh` builds both; a change that only
builds for net8 breaks the game build.

- C# 7.3: no `async`/`await`, no switch expressions, no `using var`, no `??=`, no ranges, no records,
  no nullable reference types, no tuples (`ValueTuple` is not guaranteed). Use threads and blocking I/O.
- No Newtonsoft; avoid LINQ (System.Core is trimmed to what the game uses); no `HttpListener`; no outgoing
  `HttpClient` calls from modules (everything is local).
- The trimmed BCL lacks many members that compile fine on net8. Known missing (non-exhaustive):
  `int.MaxValue`, `long.MinValue/MaxValue`, `double.PositiveInfinity` (use `Num`), `Math.Max/Min(long,long)`
  (silently binds to the float overload; use `Num`), `File.Move/Replace/Copy/WriteAllBytes/AppendAllText`,
  `File.ReadAllText(path, encoding)`, `FileStream.Flush(bool)` (use `AtomicFile`/`FileSystem`),
  `Directory.EnumerateFiles` (use `GetFiles`), `Thread.Join(int)`, `WaitOne(TimeSpan)`, `Timeout.Infinite`,
  `Assembly.GetManifestResourceStream` (content is compiled in, see `server/Content/README.md`),
  `TcpListener.AcceptTcpClient`, `Console.Out`, `Environment.Exit`, `AssemblyVersionAttribute`.
- Always format and parse numbers and dates with `CultureInfo.InvariantCulture`; compare strings with
  `StringComparison.Ordinal`; lower-case with `ToLowerInvariant`. Device locales differ.
- Android-only code goes behind `#if MCVS_EMBEDDED`.
- Never let an exception escape a thread you start: in the embedded build it kills the game.
- No game data in the repository: read it at runtime through `context.Content.TryReadGameFile`.

## 5. Docs

Record protocol decisions and endpoint status in your group's `docs/protocol/<group>.md` (what you answer,
why, which research note backs it, what still needs a device test).
