# 02 – Gameloft OnlinePlatform ("Federation") transport, auth and social/misc protocol

Scope: `Gameloft.OnlinePlatform.dll` client stack (Federation / Session / requests), the auth flow, and the
social & misc services (chat, clans, messaging/Hermes, alerts/Kairos, leaderboards/Olympus, wall, profanity,
ban tool, social/Osiris, social profile, game stats, transfer code, alias…). Storage/Profile, CRM, GamePortal,
Assets, IAP/Store and Matchmaker/Lobby are only listed (section 9) — other analysts own them.

Path abbreviations used below (all relative to `/home/user/mcvs-work`):

* `OP/` = `decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/`
* `EN/` = `decompiled/Gameloft.Engine/Gameloft.Engine/`
* `LA/` = `decompiled/Gameloft.Engine/Gameloft.Engine.LocalAnubis/`
* `GM/` = `decompiled/Game/Game/`

Legend: **[V]** verified by reading code, **[L]** likely (strong inference), **[S]** speculative.

---

## 0. TL;DR for the implementer

1. **Every online feature, and also the embedded/local server path, needs a Federation stack.** MCVS has no
   built-in "fake Federation". `ClientComponent.Connect(link)` always calls `Authentication.Connect()` (an anonymous
   Federation session) before anything else, for every `ServerLocation` including `Embedded`
   (`EN/ClientComponent.cs:758`, `:989-1006`). The in-process LocalAnubis lobby (`EN/LocalAnubisManager.cs:117`)
   opens its own Federation session with a *game* super-credential and calls `auth/verify` +
   `matchmaker/lobbyconfigs` for every player. The game server side (`Connection.ProcessHandshake`) opens a
   `SessionForUser` per player and does storage/social/messaging/leaderboard calls on their behalf
   (`EN/Connection.cs:1564`). **[V]** So a Federation replacement *is* needed (unless you rewrite large parts of the
   C# code). The good news: the protocol is plain HTTP + form-urlencoded + JSON, with **no request signing, no
   response signing and TLS validation disabled** for the C# stack.
2. Discovery chain: `eve` (environment switcher) → `config/{clientId}/datacenters[/ {dc}/urls]` → JSON map of
   service base URLs, of which `pandora` is mandatory → `pandora/locate/{svc}?client_id=` returns a **plain-text**
   base URL for each *fed* service (`auth`, `storage`, `social`, `message`, `alert`, `leaderboard`, `groupchat`,
   `lobby`, `matchmaker`, `config`, `asset`, `game_stats`). One HTTP server on one port can serve everything; paths
   do not collide.
3. Auth = OAuth-like `POST auth/authorize` (form body `username, credential_type, password, device_id,
   client_id, scope, device_*`), response `{"access_token":..., "scope":"<space separated>"}`. Then
   `GET auth/users/me` → `{"account":"<fedid>","credentials":[...]}`. Tokens are cached per scope-set forever;
   the client only re-authorizes after a `401`. No refresh token use, no expiry handling in C#. **[V]**
4. `access_token` placement: query string for GET, **form body** for POST, query **and** body for DELETE. **[V]**
5. Blocking statuses: any `403` from `eve` → `UpgradeRequiredException`; eve config `"status"` other than
   `none`/absent → update/maintenance popups (`required`, `maintenance_block`, `upgrade_required`…); `403` from
   `authorize` → ban flow; `401` from `authorize` → session fails; malformed JSON → `OnlineRequestFailedException`;
   missing `access_token`/`scope` in authorize → `NullReferenceException` (not caught as online error). **[V]**
6. Client ID (verified in Java): `2620:66102:1.17.32:android:googleplay`
   (`decompiled-java/sources/com/gameloft/android/iab/utils/HTTP.java:95`,
   `.../installer/GlotImp/TrackingManager.java:31`). C# reads it from `GameDefinitionComponent` (OBB template data,
   `EN/GameApplication.cs:338`) – **[L]** identical.
7. Redirecting the game to a replacement server: the eve URL comes from `Args.EnvironmentSwitcherUrl ??
   "https://eve.gameloft.com/"` (`EN/OnlineLibsSettingsComponent.cs:68`). It **cannot** be passed through the
   `phantomArgs` intent extra because that string is split on `':'` (`decompiled/XaLaunchANMP/XaLaunchANMP/XamarinMainActivity.cs:66-71`),
   which destroys any `http://host:port/` value. Options: patch that string literal in `Gameloft.Engine.dll`
   (and drop/regenerate the matching `libaot-Gameloft.Engine.dll.so`), or DNS-redirect `eve.gameloft.com`.
   Colon-free args (`-DeviceId`, `-DataCenter`, `-DefaultProtocol`, `-SuperPassword`, `-LocalAnubis*Port`) *can*
   be passed via `phantomArgs`. **[V]**
8. For P2P/LAN: make tokens **self-describing** (e.g. `v1.<base64url(json{credential,account,client_id,scope})>`)
   and fed IDs **deterministic** (e.g. `account = hex(sha1(credential))[:24]`) so that independent per-device
   replacement servers can `verify` each other's tokens and agree on identities without shared state. **[design
   recommendation]**

---

## 1. Client architecture

### 1.1 Objects

| Class | Role | Evidence |
|---|---|---|
| `Federation` | One per client id. Holds `ClientId`, `DatacenterName`, `EnvironmentSwitcher` URL, `DefaultProtocol`, service-URL cache, the `HttpClient`, and is the only `IResponseInspector`. | `OP/Federation.cs:19-127` |
| `Session` | Credential + password + deviceId; token cache (`AccessTokens`, keyed by scope set); `FedId`; ban list; per-session service instances. | `OP/Session.cs:18-152` |
| `SessionForUser` | Session opened by a *super* credential (game server) **for** another user (`for_username`). Read-only variant shares the game-server tokens. | `OP/SessionForUser.cs:11-103` |
| `FedRequestMessage : HttpRequestMessage` | Logical request: `Service` (an `OnlineService` key) + relative `Path` (or absolute `Uri`), `Scopes`, `Parameters`, `Method`, `Timeout` (default 100 000 ms). Resolves its URL lazily. | `OP/FedRequestMessage.cs:14-149` |
| `FedRequestHandler : DelegatingHandler` | Calls `ResolveUri` (service location + token injection + body building) right before send; applies per-request timeout. | `OP/FedRequestHandler.cs:21-53` |
| `RequestHandler` | Retry loop (max 5 attempts) driven by `IResponseInspector.Inspect` results; exponential back-off `min(50*(2^n-1)/2, 2000)` ms. | `OP/RequestHandler.cs:16-131` |
| `Service` subclasses | `ChatService`, `ClanService`, `MessagingService`, `AlertService`, `LeaderboardService`, `WallService`, `ProfanityService`, `BanToolService`, `SocialService`, `SocialProfileService`, `StorageService`, `CrmService`, `AssetsService`, `MatchmakerService`, `GamePortalService`. Obtained by `session.GetService<T>()`. | `OP/Service.cs`, `OP/Session.cs:373-382` |

### 1.2 Service keys (`OnlineService`) **[V]** (`OP/OnlineService.cs:7-53`)

| Key string | Property | "fed service"? | How the base URL is obtained |
|---|---|---|---|
| `eve` | EnvironmentSwitcher | (special) | `Federation.EnvironmentSwitcher` string, returned **as-is** (no `FormatUri`) |
| `pandora` | Discovery | no | eve config key `pandora` |
| `game_portal` | GamePortal | no | eve config key |
| `ads_agency` | AdsAgency | no | eve config key |
| `marketing_site` | Marketing | no | eve config key |
| `ecomm_api_root` | Ecommerce | no | eve config key |
| `crm_iap` | CrmIap | no | eve config key |
| `etsv2` | Tracking | no | eve config key |
| `auth` `storage` `leaderboard` `alert` `config` `groupchat` `lobby` `matchmaker` `message` `social` `asset` `game_stats` | … | **yes** | `GET {pandora}/locate/{key}?client_id=…` (plain-text body) |

Any `OnlineService.FromName(x)` is treated as a fed service (pandora-located).

### 1.3 URL resolution rules **[V]**

* `LocateServiceTask` (`OP/Federation.cs:400-430`): eve → raw string; non-fed → `Configuration.Services[key]`
  (missing key ⇒ `OnlineRequestFailedException("Invalid online configuration…")`); fed → pandora locate.
  Results are cached per key (`ServicesCache`, `SharedTask` – faulted tasks are retried, success is cached for
  the life of the `Federation`). A 408/500/502/503/504 drops the cached URL once per request
  (`OP/Federation.cs:519-532`).
* `FormatUri(uri, protocol)` (`OP/Federation.cs:456-466`): prefixes `https://` if the string does not start with
  `http`; if a protocol is forced (non-`Unspecified`) **and** the port is 80/443/absent, the scheme is replaced and
  the port removed; trailing `/` trimmed. ⇒ **Always hand out URLs with an explicit non-standard port
  (e.g. `http://127.0.0.1:18080`)** so that callers forcing `HTTPS` (LocalAnubisManager uses
  `Protocol.HTTPS`, `EN/LocalAnubisManager.cs:91`; BrowserComponent marketing lookup uses HTTPS,
  `EN/BrowserComponent.cs:166`) do not rewrite them.
* Final URL = `new Uri(new Uri(baseUrl), Path)` (`OP/FedRequestMessage.cs:214-223`) – RFC 3986 relative
  resolution: if the base has a path without trailing slash its last segment is replaced; paths that start with
  `/` (several social/storage/alert paths) are host-absolute. ⇒ **Serve every service at the host root.**
* `ChatApi.ReportUser` uses an absolute path `https://cmt.gameloft.com:443/api/index.php`, BanTool uses
  `https://bantoolv3.gameloft.org` – absolute URIs override the service base.

### 1.4 Request construction **[V]** (`OP/FedRequestMessage.cs:126-207`, `OP/RequestParameters.cs`, `OP/RequestValue.cs`)

Headers on every FedRequest:

```
Cache-Control: NO-CACHE
User-Agent: <Session.UserAgent> (<clientId>; <datacenter>)      # Session.UserAgent is never set → " (2620:66102:1.17.32:android:googleplay; <dc>)"
```
(no Authorization header except BanTool's Basic auth; ETS adds `X-ETS-sha1`.)

Token injection (`PrepareToSend`, `OP/FedRequestMessage.cs:164-172`): if the request has a `Session`, a non-empty
scope list **and the resolved URL has no query string**, `access_token = await Session.GetAccessToken(Scopes)` is
put into `Parameters` (overwriting any existing one).

Serialization by method:

| Method | URL | Body |
|---|---|---|
| GET (default) | `?` + `Parameters.ToString()` (only if params non-empty) | none |
| POST | no query | `Parameters.Content` = `StringContent(ToString(), UTF-8, FormatType)` → `Content-Type: application/x-www-form-urlencoded; charset=utf-8` (or `application/json`) |
| DELETE | `?access_token=…` (only that) | **also** the full `Parameters` form body (incl. `access_token`) |

Encodings (`RequestParametersSerializationFormat`): `FormUrlEncoded` (default) = `Uri.EscapeDataString(key)=Uri.EscapeDataString(value)` joined by `&` (spaces → `%20`, `:` → `%3A`); `FormUrlEncodedCustom` = same but value printed via `RequestValue.ToString()` (= prefix + escaped string); `Json` = `JsonConvert.SerializeObject(dict of raw values)`. Values: `bool` → `"True"`/`"False"` (C# casing!), ints invariant, `IEnumerable`/`Dictionary` via `TryAdd` → JSON text, `Credential` → `"type:username"`. `AddCustomFields(…, enforceUnderscorePrefix:true)` prefixes keys with `_`; with `JsonInterpretedConverter` values become `_json_:<json>` (`OP/JsonInterpretedConverter.cs`).

### 1.5 Transport, TLS, timeouts **[V]**

* `HttpClient(new FedRequestHandler(new HttpClientHandler()))`, `Timeout = Infinite` (`OP/Federation.cs:468-473`).
  Xamarin env `XA_HTTP_CLIENT_HANDLER_TYPE=System.Net.Http.HttpClientHandler`, `XA_TLS_PROVIDER=btls`
  (`extracted/lib/arm64-v8a/libxamarin-app.so` strings) → managed Mono handler.
* `ServicePointManager.ServerCertificateValidationCallback = AcceptAllCertificatesPlease` (always `true`) is set in
  `Federation`'s static ctor (`OP/Federation.cs:104-108,129-132`) – **any certificate is accepted** process-wide
  for the managed stack. Self-signed HTTPS is fine; plain HTTP is also fine.
* Per-request timeout: `FedRequestMessage.Timeout` default 100 s (`OP/FedRequestMessage.cs:60`); streaming listens
  set `-1` (infinite). `GetConfig` uses `DefaultTimeoutMS` (Federation ctor default 300 000; the game passes
  `OnlinePlatformComponent.DefaultFedRequestTimeoutMS = 60000`, `EN/OnlinePlatformComponent.cs:102,128`).
  `Session.TransferCodeTimeoutMS`/`SetDeviceInfoTimeoutMS` = 30 s. DeviceId wait = 10 s
  (`EN/AuthenticationComponent.cs:283`).
* **[L]** Mono's `HttpWebRequest` sends `Expect: 100-continue` on POST by default – a hand-written server must
  answer `100 Continue` or simply read the body.
* Streaming responses (alerts, chat listen) are read with `HttpCompletionOption.ResponseHeadersRead` line by line.
  Chunked or connection-close framing both work. **[L]**

### 1.6 Response inspection / retry semantics **[V]** (`OP/Federation.cs:495-557`, `OP/RequestHandler.cs:38-131`, `OP/HttpResponseMessageExtension.cs:33-70`)

| Response | Inspector result | Outcome |
|---|---|---|
| 200, 201, 202, 302 | Ok | success (`EnsureSuccessStatusCode` treats 2xx and 302 as success) |
| network exception (`WebException`, `HttpRequestException`, `TimeoutException`) / null response | Retry | up to 5 attempts, then exception |
| 408, 500, 502, 503, 504 (and any 5xx) | Retry (first time also drops the cached service URL) | after 5 attempts `OnlineRequestFailedException` |
| 401 on path `authorize` | Abort | `OnlineRequestFailedException(401)` → session open fails |
| 401 with `access_token` param | `Session.ResetToken()` (clears **all** tokens) + Retry once (re-authorize) | |
| 401 without `access_token` param | throws `OnlineRequestFailedException("Unauthorize. Did you forget…")` | |
| 403 from **eve** | Abort → `EnsureSuccessStatusCode` throws **`UpgradeRequiredException`** | "upgrade required" popup, online disabled |
| 403 from `authorize` | Abort → `Session.ParseForbidden` → **`UserBannedException`** if body parses | ban popup |
| any other 4xx (404, 400, 409, 412…) | Abort | `OnlineRequestFailedException` with `.Response.StatusCode` – several callers branch on 404/412/400 |

JSON parsing (`Session._JsonRequest`, `OP/Session.cs:230-270`): body → `Json.Load` (Newtonsoft `JToken.Load`);
empty body ⇒ `Token == null` (many callers then NRE ⇒ always return at least `{}` or `[]`); invalid JSON ⇒
`OnlineRequestFailedException("Fail to parse response string. Invalid json.")`. The `Date` and `ETag` response
headers are surfaced (`JsonResponse.Date/Etag`) – `Date` is used by chat to classify history, ETag by
storage/CRM.

### 1.7 Exceptions (all in `OP/`) **[V]**

| Exception | Implements `IOnlineRequestException` | Thrown when | Game reaction |
|---|---|---|---|
| `OnlineRequestFailedException` | yes, `Reason="Failed with code N"` | any non-success / parse / locate failure | generic error popup + retry (`GM/GameClientComponent.cs:3938-3951`) |
| `OnlineRequestTimeout : TimeoutException` | yes | request or DeviceId timeout | same |
| `OnlineRequestCanceledException : OperationCanceledException` | yes | cancellation | silent |
| `UpgradeRequiredException` | yes | eve 403, or eve status `required` after login | `ClientLink.UpgradeRequired` event, `IsUpgradeRequired=true` (`EN/AuthenticationComponent.cs:551-555`) |
| `MaintenanceBlockException` | yes | eve status `maintenance_block` | `ClientLink.MaintenanceBlock` |
| `UserBannedException : OnlineRequestFailedException` | yes | 403 on authorize with parsable ban body; or `Session.IsFullyBanned` | `ClientLink.UserBanned` |
| `CorruptedGiftException` | no | gift message without body and without `gift_type` | logged per message |

Note: `AuthenticationComponent.OpenAnonymousSession` rethrows anything that is **not** `IOnlineRequestException`
(`EN/AuthenticationComponent.cs:536-541`) – e.g. a `NullReferenceException` from a malformed authorize
response escapes into a fire-and-forget task.

---

## 2. Service discovery (eve + pandora) – the first requests on boot

### 2.1 Sequence **[V]**

```
OnlinePlatformComponent.OnCreate → InitFederation()                     EN/OnlinePlatformComponent.cs:131-150
   Federation(clientId, EnvironmentSwitcherUrl, Datacenter, 60000, 3, DefaultProtocol)
   Datacenter = Args.DataCenter ?? SaveStream("datacenter") ?? ""      EN/OnlinePlatformComponent.cs:41-66
AuthenticationComponent.OpenAnonymousSession
   → OnlinePlatform.FederationFor(clientId) → Federation.GetConfig()   EN/OnlinePlatformComponent.cs:109-124
       LoadDataCenter(): if DatacenterName empty →
          GET {eve}config/{clientId}/datacenters                        OP/Federation.cs:349-370
          pick first entry with preferred=true (empty name ⇒ "Incorrect datacenter")   OP/Federation.cs:384-388
          DatacenterChanged → OnlinePlatformComponent.SetDatacenter() persists "datacenter" file, re-creates Federation
       GET {eve}config/{clientId}/datacenters/{dc}/urls                 OP/Federation.cs:321-333
       → Configuration (status + service map); ConfigurationReceived event
       → native OnlinePlatformComponent_Init_private(dc, "", eveConfigJson)  EN/OnlinePlatformComponent.cs:152-173
   first fed request, e.g. auth/authorize:
       GET {pandora}/locate/auth?client_id=2620%3A66102%3A1.17.32%3Aandroid%3Agoogleplay   OP/Federation.cs:444-454
```

### 2.2 `GET {eve}config/{clientId}/datacenters`
* Query: `country`, `override_ip_geolocalisation` – only if non-null (never set by the game) **[V]**.
* No token. Response: **JSON array** (`Json.LoadArray`) of objects; fields read: `name` (string, required for the
  preferred one), `preferred` (bool), `status` (string), `country_code` (string) (`OP/Datacenter.cs:16-22`).
* 403 here ⇒ `UpgradeRequiredException` (service is `eve`). 404 ⇒ logged "Configuration not found".

```json
[{"name":"local","status":"active","preferred":true,"country_code":"US"}]
```

### 2.3 `GET {eve}config/{clientId}/datacenters/{dc}/urls`
* Response: JSON **object**. `status` (or, if absent, `code`) is removed and parsed into `ConfigStatus`
  (`OP/Configuration.cs:30-74`): `none`, `maintenance`, `maintenance_block`, `maintenance_notice`, `optional`,
  `optional_with_wifi`, `required`, `critical`|`upgrade_required`, `upgrade_required_with_wifi`; **any other
  string ⇒ `FormatException` (not an online exception)**. Every other property becomes
  `Services[key] = value.ToString()`.
* Blocking: `AuthenticationComponent` throws on `required` / `maintenance_block` right after login
  (`EN/AuthenticationComponent.cs:520-531`); `UIComponent.ShowEarlyPopup` shows maintenance / optional /
  mandatory update popups for `maintenance*`, `optional*`, `required`, `upgrade_required*`
  (`GM/UIComponent.cs:426-447`). ⇒ return `"status":"none"`.
* The whole object (minus status) is also handed to the native engine (`eveConfig`), which contains its own Gaia
  SDK (IAP "GLEcommCRMService"/"FederationCRMService" use `pandora`, `ecomm_api_root`, `crm_iap`) **[V strings, S usage]**.

Minimal valid response:
```json
{"status":"none",
 "pandora":"http://127.0.0.1:18080",
 "etsv2":"http://127.0.0.1:18080/ets",
 "marketing_site":"http://127.0.0.1:18080",
 "game_portal":"http://127.0.0.1:18080",
 "ads_agency":"http://127.0.0.1:18080",
 "ecomm_api_root":"http://127.0.0.1:18080",
 "crm_iap":"http://127.0.0.1:18080"}
```
(only `pandora` is strictly required for the C# auth/social path; the others avoid
"Invalid online configuration" errors in tracking, browser, ads and IAP code.)

### 2.4 `GET {pandora}/locate/{service}?client_id={clientId}`
* No token, `Cache-Control`/User-Agent as usual. Response body: **plain text** (read with
  `ReadAsStringAsync`, not JSON) = host or URL, e.g. `http://127.0.0.1:18080` (no quotes, no trailing
  newline). `FormatUri` adds `https://` if no scheme. **[V]** (`OP/Federation.cs:419-422`)
* Services the client will locate: `auth`, `storage`, `social`, `message`, `alert`, `leaderboard`, `groupchat`,
  `lobby`, `matchmaker`, `config`, `asset`, `game_stats`.
* **[S]** The native Gaia code contains strings `/locate`, `service=`, `/locate/asset`; it may call
  `/locate?service=X` or `/locate/X`. Implement both.
* `GET {pandora-authority}/services.json?internal=true` is only used with `-UseInternalFedAddress`
  (`GM/GameOnlinePlatformComponent.cs:27-55`) – not needed.

### 2.5 Connectivity probe
`Connectivity.IsInternetAvailable()` does a plain `HttpClient.GetStringAsync(EnvironmentSwitcherUrl)` and
treats any exception (incl. non-2xx) as "no internet" (`EN/Connectivity.cs:28-39`). Used for error-popup wording
and by `DlcComponent` (`EN/DlcComponent.cs:293`). ⇒ `GET {eve}/` should return `200`.

---

## 3. Authentication – end to end

### 3.1 Credentials **[V]**
* `Credential` = `"<type>:<username>"`; parser splits on `:` and requires exactly 2 parts (3 for `gamecenter`),
  otherwise `ArgumentException` (`OP/Credential.cs:54-88`). Special value `me`. `Credential.IsValid` = type ≠
  `none` and non-empty username.
* Types (`OP/CredentialType.cs:85-126`): `none, anonymous, fed_id, game, dev, me, facebook, gamecenter,
  googleplaygames, gllive, gamecircle, ghost, xboxlive, msnlive, weibo, group, 9splay (npplay), system`.
  SNS = facebook, gamecenter, googleplaygames, msnlive.
* Client anonymous credential: `Args.Credential ?? "anonymous:" + AnonymousUsername` and
  `Password = Args.Password ?? AnonymousPassword`, both produced by **native** code
  (`AuthenticationComponent_GetAnonymousUsername/Password`, `EN/AuthenticationComponent.cs:213-217,288-296`).
  The algorithm is native (Gaia "GameloftID"/GLUID code in `libnativeengine.so`) – **not needed** for a
  replacement: accept any anonymous username/password and auto-create the account on first `authorize`.
  There is **no separate "create account" request** in C#. **[V]**
* Device id: `Args.DeviceId ?? DeviceInfos.GDID` (Gameloft *global* device id from native
  `AuthenticationComponent_GetGDID_private`, polled every frame, `EN/AuthenticationComponent.cs:372-383`).
  `OpenAnonymousSession` waits **10 s** for it and fails with `OnlineRequestTimeout("DeviceId timeout")`
  (`EN/AuthenticationComponent.cs:497-505`). **[S]** Native GDID comes from Gaia's
  `globalidservice` (`/assign_global_id/`, `/get_global_id/`, `/get_device_id/`, `/update_device_id/` strings) –
  it may need network on first launch. Mitigation: pass `-DeviceId:<something>` in `phantomArgs` (colon-free
  value), or answer those native requests.
* Game-server super credential: `Args.SuperCredential/SuperPassword` or asset `ServerInfos.json`
  `{"SuperCredential":"game:xxx","SuperPassword":"…"}` (`GM/GameAuthenticationComponent.cs:18-33`,
  `EN/LocalAnubisManager.cs:84-106`). The file is **not in the APK** (`unzip -l`), and the value contains `:` so it
  cannot be given via `phantomArgs`. Without it `Session.GetSession` throws `ArgumentException("credential
  invalid")` *before any HTTP request* (`OP/Session.cs:666-669`). ⇒ ship a `ServerInfos.json` into the game data
  (GlfStream/OBB/DLC) or patch the default. **[V]**

### 3.2 Client (anonymous) session – exact sequence **[V]**

`AuthenticationComponent.OpenAnonymousSession(InitialScopesClient = ["auth","lobby","config"])`
(`EN/AuthenticationComponent.cs:270,477-569`):

1. Abort early if `IsMandatoryUpdate`/`IsMaintenanceBlock`.
2. `FederationFor(clientId)` → eve/datacenter/config (section 2).
3. Await DeviceId (≤10 s).
4. Build device args: `firmware, country, carrier, model, resolution, language` (country validated against an
   ISO list, default `US`; resolution `"WxH"` or null).
5. `Federation.OpenSession(anonCred, password, deviceId, deviceArgs, initialScopes)` → `Session.GetSession` →
   `Session.Open(authScopes=["auth"], initialScopes)` (`OP/Session.cs:163-182,649-676`):
   * scope set = `auth ∪ initialScopes` = `{auth, lobby, config}`
   * `GetAccessToken(scopes)` → `Authorize()` → **`POST {auth}/authorize`**
   * `IsOpen = token non-empty`
   * because credential type ≠ `fed_id`: `FedId = GetFedId()` → **`GET {auth}/users/me`** (scope `auth`, served by
     the cached token since `{auth} ⊆ {auth,lobby,config}`).
6. `Session.UserBanned += …`; `GetEveStatus()` (cached config) → throw on `required` / `maintenance_block`.
7. `OnOpenSessionSucceded(session)` → listeners: `GetClientSessionAsync()` waiters (UI, notifications,
   crashlytics), `TrackingHitsComponent`, `BrowserComponent`, `LocalizationComponent`.
   On failure `OpenSessionFailed` → `GameClientComponent` shows an error popup whose OK button calls
   `World.Client.Connect()` again (`GM/GameClientComponent.cs:3938-3951`).

"Session ready" = `Session.IsOpen && FedId resolved`; game code awaits it via
`AuthenticationComponent.GetClientSessionAsync(ct)` (`EN/AuthenticationComponent.cs:385-418`) or the
`OpenSessionSucceded` event, or implicitly through `ClientComponent.Prepare()` (`EN/ClientComponent.cs:987-1006`)
and `ConnectToServer()` (`EN/ClientComponent.cs:1117`).

#### `POST {auth}/authorize` (password grant) **[V]** (`OP/Session.cs:683-701`)
Form body (order as built):
```
username=<anon username>&credential_type=anonymous&password=<anon pwd>&device_id=<gdid>
&client_id=2620%3A66102%3A1.17.32%3Aandroid%3Agoogleplay&scope=auth%20lobby%20config
&device_firmware=<..>&device_country=US&device_carrier=<..>&device_model=<..>&device_resolution=1080x2340&device_language=en
```
No `access_token`. Response read (`OP/Session.cs:415-424`): `access_token` (**required**, string),
`scope` (**required**, space-separated; a mismatch with the requested set only logs an assert). Everything else
(`expires_in`, `token_type`, `refresh_token`…) is ignored by C#.
```json
{"access_token":"v1.eyJjcmVkZW50aWFsIjoiYW5vbnltb3VzOmFiYyIsImFjY291bnQiOiI5ZjI..." ,
 "token_type":"bearer","expires_in":2592000,"scope":"auth lobby config"}
```
* `403` with body `{"description":"{\"reason\":\"{\\\"asset_name\\\":…,\\\"text_id\\\":…,\\\"error\\\":…}\"}"}`
  (nested JSON-in-string, HTML-decoded first) ⇒ `UserBannedException`; then `GET {asset}/assets/{clientId}/{asset_name}`
  is fetched for the localized text (`OP/BannedFrom.cs:76-100`, `OP/Session.cs:437-464`). Never return 403.
* `401` ⇒ session open fails (Abort).
* Silo: if the token contains `~`, the substring between the last `,` before `~` and the first `|` after it
  becomes `Session.Silo` (`OP/Session.cs:88-120`). Optional – avoid `~` or ignore.

#### `GET {auth}/users/me?access_token=…` (scope `auth`) **[V]** (`OP/Session.cs:521-552,749-754`)
Response fields: `account` (**required**, becomes `fed_id:<account>`; must not contain `:`),
`credentials` (array of credential strings; each must parse as `type:user` or an `ArgumentException` escapes).
```json
{"account":"9f2c0d1e7a6b4c3d2e1f0a9b","credentials":["anonymous:abc123"]}
```
`GET {auth}/users/{credential}` with scope `auth_admin_ro` (used for SNS conflict checks, `EN/SocialComponent.cs:857`)
has the same shape; **404 = "no such account"** is handled.

### 3.3 Token cache, lifetime, refresh **[V]** (`OP/Session.cs:315-346`)
* `AccessTokens` = set of `(scopeSet, Task<string>)`. A request with scopes S reuses the first token whose scope
  set ⊇ S, else performs a **new `authorize` with exactly S** (not the union). So the server will see authorize
  calls with assorted scope strings (e.g. `social_group social_admin`, `chat chat_admin`, `message
  message_secured`, `leaderboard social`, `config storage_ro`, `lobby_game_admin`). Echo the requested scopes back
  in `scope`.
* No expiry tracking, no refresh grant in C#. Only a `401` on a token-bearing request triggers
  `ResetToken()` (drop all) + one retry with fresh authorize (`OP/Federation.cs:533-548`). ⇒ issue long-lived
  tokens; never 401 a valid one.
* `Session.Close()` clears tokens and the account-info cache.

### 3.4 Game-server / per-user sessions **[V]**
* Game server own session: `OpenGameServerSession(InitialScopesServer)` with `SuperCredential`
  (`EN/AuthenticationComponent.cs:439-470`). `InitialScopesServer` =
  `storage_restricted chat message storage_ro leaderboard_override social_trophies storage social_group
  leaderboard_ro social_event clusters config message_secured leaderboard_restricted message_broadcast alert auth
  lobby leaderboard social social_clan` (`EN/AuthenticationComponent.cs:272-277`). Authorize body is the same as 3.2
  with `credential_type=game` and **no `device_*`**; then `GET users/me` (credential type `game` ≠ fed_id).
* Per-player session on the server: `Connection.ProcessHandshake` receives `{credential, deviceId, clientId}` from
  the client (client sends `Session.Credential`, i.e. `anonymous:<user>` — `EN/Connection.cs:952-954`) and calls
  `AuthenticationComponent.OpenSessionForUser(credential, deviceId, clientId, InitialScopesServer)`
  (`EN/Connection.cs:1564`) → `SessionForUser.GetSessionForUser` → `Open(["auth"], initialScopes minus "auth")`.
  Authorize body (`OP/SessionForUser.cs:90-102`):
  ```
  username=<super user>&credential_type=game&password=<super pwd>&device_id=<client device or "Unknown">
  &client_id=<cid>&scope=auth%20storage_restricted%20chat…&for_username=<client user>&for_credential_type=anonymous
  ```
  → token *for the client user*; then `GET users/me` with that token → client's `account`.
  The server then: starts an alert listener for the user (type `connection`, `EN/Connection.cs:576-584`), loads the
  player profile through storage (`GM/AgeGateChecker.cs:66-75`), etc.
* Read-only variant `SessionForUser.GetReadOnlySessionForUser(gameServerSession, fedId)` reuses the server's tokens
  and never authorizes (`OP/SessionForUser.cs:24-35,76-88`) – requests are made with the game server's token for
  paths like `profiles/fed_id:…/myprofile`.
* `GameProfileSection.RetrieveCrmServerConfig` opens a *second* super session with deviceId `mcvsGameServer`
  (`GM/GameProfileSection.cs:60-73`).
* LocalAnubis (embedded lobby) session: `new Federation(clientId, eveUrl, Args.DataCenter, 60000, 3, HTTPS)
  .OpenSession(SuperCredential, SuperPassword, Args.DeviceId ?? "default", null, ["auth","auth_admin_ro"])`
  (`EN/LocalAnubisManager.cs:113-132`).

### 3.5 Lobby (Anubis) token hand-off **[V]**
* Client → lobby login: `GET {auth}/encrypt_token?nonce=<nonce from lobby>&access_token=<token(scope lobby or
  lobby_admin)>` → **plain-text** body = encrypted token (`OP/Session.cs:348-355`, `EN/AnubisClientSocket.cs:78-84`).
  The client sends it as `{"action":"login","access_token":"<that text>", …RoomUser}`
  (`decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform.Lobby/LobbyApi.cs:10-20`).
* LocalAnubis server verifies it: `GET {auth}/verify?access_token=<encrypted>&nonce=LocalAnubisNonce&credential=system%3AAnubis`
  (`LA/BaseServer.cs:173,406-428`); the Finder HTTP endpoint verifies raw client tokens:
  `GET {auth}/verify?access_token=<client token>` (`LA/FinderHTTPMessageHandler.cs:144-157`).
  These requests carry **no own token** (no scopes) – the `access_token` parameter is the token being verified.
  Response fields read: `credential`, `account`, `client_id` (all used with `.ToObject<string>()`/copied;
  `account` must be non-empty because it ends up in `new Credential(fed_id, …)` in
  `ClientConnectionData`, `OP/ClientConnectionData.cs:26`).
  ```json
  {"credential":"anonymous:abc123","account":"9f2c0d1e7a6b4c3d2e1f0a9b","client_id":"2620:66102:1.17.32:android:googleplay","scope":"lobby"}
  ```
  Replacement: `encrypt_token` may simply return the token itself; `verify` decodes it.

### 3.6 Other auth endpoints **[V]** (all `auth` service)

| Purpose | Request | Response read | Used by |
|---|---|---|---|
| Transfer code create | `POST users/me/transfer_code` (scope auth) | `{"value":"…","expiration":"ISO"}` (`OP/TransferCode.cs:17-21`) | settings UI via `AuthenticationComponent.GetTransferCode` |
| Transfer code redeem | **GET** `authorize?grant_type=transfer_code&scope=auth&client_id=…&transfer_code=…` (no token; `OP/Session.cs:703-711`) | `{"access_token":"…"}` | `LinkAnonymousSession` |
| Link anonymous into redeemed account | `POST users/me/credentials` body `access_token=<transfer token>&if_exists=relink_all&resolve_conflicts=False&credential=anonymous:…&password=…` (`EN/AuthenticationComponent.cs:610-620`) | status only | then full `Client.Reconnect` |
| Link credential | `POST users/me/credentials` scope `auth` (or `auth_game_admin` if no secret) body `if_exists=error|relink|relink_all&credential=…[&password=…]` (`OP/Session.cs:756-768`) | status only | SNS link, 9splay, account merge (`GM/GameProfileSection.cs:167`, `EN/SocialComponent.cs:742-754`) |
| Create alias (friend code) | `POST games/mygame/alias` scope auth | `{"alias":"123456789"}` | `PlayerProfileComponent.QueryAlias` (`GM/PlayerProfileComponent.cs:6379`), UI `QuerySelfAlias` |
| Lookup alias | `GET games/mygame/alias/{alias}` scope auth | `{"credential":"fed_id:…"}` | friend search |
| Compliance (age gate) | `POST users/{fed_id cred}/compliance` scope auth body `country=US&age=15` | object `{ "<rule>": {"date":"ISO","compliant":true,"reasons":{}} }` – all `compliant` must be true else age limit 16 (`GM/AgeGateChecker.cs:44-61`) | server handshake |
| Get compliance | `GET users/{cred}/compliance` | same | unused |
| Accounts for device | `GET users?device_id=<id>[&all_games=True]` scope `auth_admin_ro` | **JSON array** `[{"account":"…","credentials":[…]}]` (never null – `.Where` on it) | `GameProfileSection.CreateProfile` account auto-merge (`GM/GameProfileSection.cs:143-176`). Return `[]`. |
| Set device info | `POST devices/mydevice` scope auth, arbitrary fields | JSON | unused by game |
| User exists | `GET /users/{cred}/games/{clientId}` scope `auth_admin_ro`; `GET users/{cred}?client_id=` (no token) | status 200 | unused |

---

## 4. Social / misc services – every request

Conventions: "scope" = scope string passed to `FedRequestMessage` (token obtained via authorize with that scope
set). All responses JSON unless stated. `{cred}` = `Credential.ToUrlEncodedString()` (`fed_id%3Axxx`).
Response examples are *minimal valid* objects synthesized from the parsing code.

### 4.1 Group chat (`groupchat`) – `OP/ChatApi.cs`, `OP/ChatService.cs`, `OP/ChatRoom.cs`

Used only for **clan chat** (`GM/ClanChatComponent.cs` → `EN/ClientChatComponent.cs`), on the game server
(user session) and client. Failures are retried with back-off `min(120, max(1, 3·n²))` s
(`EN/ClientChatComponent.cs:309-316`). Non-blocking.

| # | Method / path | Scope | Params | Response |
|---|---|---|---|---|
| C1 | `POST chat/channels/{channel}/subscribe` | chat | `language`, `reconnect_key?` | `{"cmd_url":"http://h:p/chat/channels/x/","listen_url":"http://h:p/chat/channels/x/listen","status":""}` – `https_listen_url` preferred over `listen_url`; `status` `kick`/`mute` → IsKicked/IsMuted (`OP/ChatRoom.cs:110-134`) |
| C2 | `POST chat/rooms/{room}/subscribe` | chat | same | same |
| C3 | `GET {listen_url}` | chat (token appended only if `listen_url` has **no** query) | — | **stream**, one JSON object per line (see below) |
| C4 | `POST {cmd_url}` | `chat` (type message) / `chat chat_admin` (type game) | `msg` (≤256 chars), `user` (ChatUser JSON `{"nickname","avatar"?, "_…"}`), `message_type` (`message`/`game`), `_original_message`, `_sub_type` (`Message`/`Notification`) + other `_` fields | status only |
| C5 | `POST {cmd_url}members/{cred}` (private msg) | chat | `msg`, `user`, `_…` | status |
| C6 | `POST {cmd_url}invite` | chat | `recipient` | status |
| C7 | `GET {cmd_url}members?offset&limit` | chat_admin | | any JSON |
| C8 | `POST {cmd_url}members/{cred}/kick` / `mute` | chat_admin | `reason?`, `limit?` (seconds) | any JSON |
| C9 | `POST {cmd_url}members/{cred}/unkick` / `unmute` | chat_admin | | any JSON |
| C10 | `GET chat/rooms`, `GET chat/channels` | chat_admin | | (then `NotImplementedException`) – unused |
| C11 | `POST https://cmt.gameloft.com:443/api/index.php` (ReportUser) | chat | `client_id, language, complainer_credential, complainer_nickname, reported_user_credential, reported_user_nickname, channel_type (int), channel_name, report_type (int 0-4), report_description, chat_log (JSON array {channel,message,nickname,credential,timestamp,messageId})` | status |

Because `cmd_url` is combined with relative paths (`members/…`, `invite`), **`cmd_url` must end with `/`**.

Listen stream line types (`OP/ChatRoom.cs:316-383`):
```
{"type":"message","id":"m1","msg":"hello","sender":{"credential":"fed_id:9f2c…","nickname":"Bob","avatar":""},"sent":"2026-10-24T12:00:00+00:00","moderator":false,"_original_message":"hello","_sub_type":"Message"}
{"type":"game", ...same fields...}
{"type":"room_info","cmd_url":"…/","listen_url":"…","num_members":3,"motd":"","send_quota":10,"quota_period":60,"reconnect_key":"rk1"}
{"type":"disconnect","reason":"Idle channel"}        # "Idle channel"/"Idle room" ⇒ idle; anything else ⇒ replaced connection
```
End-of-stream ⇒ reconnect after back-off. Keep the stream open (send blank lines as keep-alive – blank lines are
ignored).

### 4.2 Profanity (`groupchat`) – `OP/ProfanityApi.cs`

| # | Method / path | Scope | Params | Response |
|---|---|---|---|---|
| P1 | `POST chat/words/{lang}/filter` (or `chat/words/filter` if language empty) | chat | `message` | `{"matches":false,"result":"<censored message>"}` (`OP/FilteredMessage.cs:24-34`) |
| P2 | `GET chat/words` | chat_admin | | `{"en":{"blacklisted":[],"whitelisted":[]},"all":{"blacklisted":[]}}` |
| P3 | `POST chat/words/{lang}/{word}` | chat_admin | `bad=True/False` | JSON |
| P4 | `POST chat/words/{lang}/{word}/delete` | chat_admin | | JSON |

**P1 is on the critical path for setting the player name** (`GM/PlayerProfileComponent.cs:3673-3690`,
server side) and for clan names and private tournament room names (`EN/ClanComponent.cs:256-270`,
`GM/GameClientComponent.cs:1543`). An exception there aborts the name change. Implement it (return
`matches:false` and echo the message).

### 4.3 Clans (`social`, Osiris groups) – `OP/ClanApi.cs`, `OP/ClanService.cs`, `OP/Clan.cs`

All run on the game server inside the player's `SessionForUser` (`EN/ClanComponent.cs`, `GM/GameClanComponent.cs`).

| # | Method / path | Scope | Params | Response |
|---|---|---|---|---|
| K1 | `POST groups` | social_group | `name, category (default "Clan"), description?, member_limit?, membership (open/owner_approved; default owner_approved), type (clan/group; default clan), visibility (open/owner/member/restricted; default member), avatar?, score?, matchmaking_score?, recommendation_score?, recommendation_filter?, days_offline_before_demotion?, _<custom>=_json_:<json>` | Clan object |
| K2 | `POST groups/{id}` (update) | social_group social_admin | same fields optional + `promote_owners` / `demote_owners` (JSON array of credential strings) | Clan object |
| K3 | `POST groups/{id}/delete` | social (or `social social_admin social_clan social_group`) | | JSON |
| K4 | `POST groups/{id}/to_clan` | social_group | `category, currency?, score?, inventory? (JSON), matchmaking_score?, …` | JSON |
| K5 | `GET groups/{id}` | social | | Clan object (then members fetched if `members.length != member_count`) |
| K6 | `GET /groups/config` / `POST /groups/config` | social_admin | `configuration` (JSON) | JSON object |
| K7 | `GET groups/{id}/fields/{field}` | social | | the field value (any JSON); **404 ⇒ default value** |
| K8 | `POST groups/{id}/fields/{field}` | social | `object=<json>` (+ `If-Match` ETag optional); increments: `object=<int>&operation=inc|dec` | Clan object |
| K9 | `GET groups?category&limit&offset[&random=True]` | social | | **array** of Clan |
| K10 | `GET groups/categories/{cat}/find/matchmaking_score?limit&score` | social | | array of Clan |
| K11 | `GET groups/categories/{cat}/find/recommendation_score?limit&score&filter` | social | | array of Clan |
| K12 | `GET groups/categories/{cat}/find/keyword?keywords&limit&offset` | social | | array of Clan |
| K13 | `POST groups/{id}/members` (join = credential `me`, invite = other) | social | `credential`, `alert_kairos=True/False` | join: status only; invite: `{"group":{Clan}}` |
| K14 | `POST groups/{id}/members/{cred}/delete` (leave = `me`, kick = other) | social | | Clan object |
| K15 | `GET groups/{id}/members?limit&offset&owners=True/False` | social | | **array** of ClanMember |
| K16 | `GET groups/{id}/members/{cred}` | social | | ClanMember |
| K17 | `POST groups/{id}/members/{cred}` | social | `_<field>=_json_:<json>` | status |

Clan JSON (`OP/Clan.cs:37-113`; `_`-prefixed keys go to CustomProperties; `visibility/type/membership` are
string-convertible classes):
```json
{"id":"g1","name":"Alpha","description":"","category":"Clan","type":"clan","visibility":"member",
 "membership":"owner_approved","member_count":1,"member_limit":30,"score":0,"currency":0,"inventory":{},
 "avatar":"","matchmaking_score":0,"recommendation_score":0,"recommendation_filter":"",
 "days_offline_before_demotion":30,"edit_permissions":"","participations":[],
 "owner":{"credential":"fed_id:9f2c…","fed_id":"9f2c…","name":"Bob","online":true},
 "owners":[{"credential":"fed_id:9f2c…","fed_id":"9f2c…","name":"Bob"}],
 "members":[{"credential":"fed_id:9f2c…","fed_id":"9f2c…","name":"Bob","online":true,
             "status_line":"","game":"","avatar":"","seconds_since_last_status_change":0,"_score":0,"_rank":"leader"}]}
```
ClanMember (`OP/ClanMember.cs:8-60`): `credential, fed_id (prefixed with "fed_id:" if missing), name,
seconds_since_last_status_change, status_line, game, avatar, online, _score, _rank, _*`.

Clan notifications are posted to the clan wall (W1) as `game_post` with a JSON text
`{"Type":<ClanNotifictionType int>,"Receiver":{ClanMember},"TriggeredBy":{ClanMember}}` and read back with
W2 limit 3 (`OP/Clan.cs:133-185`).

### 4.4 Social / Osiris connections, requests, events (`social`) – `OP/SocialApi.cs`, `OP/SocialService.cs`

| # | Method / path | Scope | Params | Response |
|---|---|---|---|---|
| S1 | `GET events/{id}?language` | social | | SocialEvent object |
| S2 | `GET events?event_ids=a,b&language=en` | social | | **array** of SocialEvent (an object would NRE) |
| S3 | `POST events/{id}/participants/{cred|me}/award` | social | | any JSON |
| S4 | `POST events/{id}/participants/{cred|me}` | social | | any JSON |
| S5 | `DELETE events/{id}/participants/{cred|me}` | social | (token in query+body) | any JSON |
| S6 | `POST accounts/me/connections/{type}` | social (+ ` social_connection_override` if no approval) | `target_credential, required_approval=True/False, alert_kairos=True/False, requester_credential?` | SocialRequestObject or `{}` |
| S7 | `GET accounts/me/connections/{type}?game=mygame[&seconds_since_last_login&limit&online=True]` | social | | **array** of SocialConnection |
| S8 | `GET accounts/me/connections/{type}/count?game=mygame&online=False…` | social | | count (unused by game) |
| S9 | `POST accounts/me/connections/{type}/import` | social | `from_credential, secret` | status (FB import) |
| S10 | `GET accounts/me/requests?request_type&limit&status&offset` | social | | **array** of SocialRequestObject (null ok) |
| S11 | `GET accounts/me/requests/sent?request_type&limit&offset` | social | | array |
| S12 | `POST /accounts/me/requests/sent/{id}/cancel` | social | | status |
| S13 | `POST /accounts/me/connections/{type}/{cred}/delete` | social | | status |
| S14 | `POST /accounts/me/requests/{id}/{accept|reject|ignore}` | social | | SocialRequestObject (+ `group` for clan membership approvals) |

SocialEvent (`OP/SocialEvent.cs:70-139`): `id, name, start_date, end_date (ISO-8601), _version (int-string),
tournament {leaderboard:{name,order}, type:"award_by_rank"|"award_by_percentile", awards:[{gifts:[{name,value}],
rank_interval:[a,b], minimum_score?, percentile?}]}`; every other key goes into `Fields`.
**`TimeLimitedEventsComponent` requires `Fields["_template"]` (a JSON *string* with `event_tuning`/`event_type`)
and reads `_priority`** (`EN/TimeLimitedEventsComponent.cs:185-206`) – event ids come from CRM
(`crm.TimeLimitedEvents.Participation/Advertisement`). Divisions/seasons are TLEs (`GM/DivisionsComponent.cs`).
```json
[{"id":"tle_1","name":"Season 1","start_date":"2026-10-01T00:00:00Z","end_date":"2027-10-01T00:00:00Z",
  "_version":"1","_priority":1,"_template":"{\"event_tuning\":{\"event_type\":\"Game.DivisionSeasonEvent\"}}"}]
```
(the template contents/event types are the CRM analyst's domain.)

SocialConnection (`OP/SocialConnection.cs:17-44`): `credential, name, avatar, online, seconds_since_last_status_change,
status_line, game, credentials[], fed_id`.
SocialRequestObject (`OP/SocialRequestObject.cs:8-33`): `id, creation, type (connection_approval |
membership_approval | group_invitation), requester{SocialConnection}, game, connection_type ("friend"),
connector, group{Clan}, recipient`.

### 4.5 Social profile (`social`) – `OP/SocialProfileApi.cs`

| # | Method / path | Scope | Params | Response |
|---|---|---|---|---|
| SP1 | `GET accounts/{cred}` | social | | `{"credential":"fed_id:…","name":"Bob","groups":["g1"]}` (`OP/SocialProfile.cs`) – used for chat nickname and "leave all groups" |
| SP2 | `POST accounts/me` | social | `name?, language?, country?` | status (errors swallowed) |
| SP3 | `POST accounts/me/status_line` | social | `status_line` (≤250) | status (used by clans: `GM/GameClanComponent.cs:1416`) |

### 4.6 Wall (`social`) – `OP/WallApi.cs`, `OP/Wall.cs`

| # | Method / path | Scope | Params | Response |
|---|---|---|---|---|
| W1 | `POST /{groups|accounts|events|game_objects}/{id}/wall` | social | `text, language, activity_type (user_post/game_post), alert_kairos=True/False` | status |
| W2 | `GET /{type}/{id}/wall?sort_type=chronological|popularity&language?&limit=50` | social | | **array** of `{"id","type":"game_post","text","actor":{ChatUser},"creation":"ISO","target":{ChatUser},"game","language","votes":0}` |

### 4.7 Messaging / Hermes (`message`) – `OP/MessagingApi.cs`, `OP/Messaging.cs`

Transports (`EN/MessagingTransportType.cs`): `secured, inbox, apns, mpns, gcm, wns`. Server-side
`MessagingTransport` reads the user's `secured`/`inbox` mailbox on start and on every Kairos `message` alert
(`EN/MessagingTransport.cs:38-106`). **Live-tournament rewards and tokens are delivered through `secured`
messages** posted by the game server (`GM/RewardComponent.cs:88-160`) and the server posts a
`multipleConnectionDetected` message (`GM/GameServerComponent.cs:1149`) ⇒ to keep those features, implement a
real per-account mailbox.

| # | Method / path | Scope | Params | Response |
|---|---|---|---|---|
| M1 | `GET messages/{transport}/me[?delete=delete]` | message | | **array** of messages |
| M2 | `POST messages/{transport}/{cred}` | `message` (+` message_secured` if secured, default true) | `body, delay (seconds int), attachment?, message_label?, rate_limit_count?, rollover=True/False, replace_matching_label=True?, type=gift?, alert_kairos=True?` + raw custom fields (e.g. `created, expirationDate, title, from, gift_type, roomId, quantityPaid, type`) | status (`IsSuccessStatusCode`) |
| M3 | `POST messages/{transport}/multicast` | same | `body, credentials=<JSON array of strings>`, same optional fields (`GM/GameClanComponent.cs:1486-1503` adds `localizations` (JSON), `default_localization`) | JSON (ignored) |
| M4 | `POST transports/{transport}/endpoints/{endpoint}` | message | | status (push registration) |
| M5 | `POST transports/{transport}/endpoints/delete` | message | | status |
| M6 | `DELETE messages/{transport}/me` | message message_secured | **query** `access_token`, **body** `msgids=id1,id2&access_token=…` | JSON or empty |

Message object (`OP/Messaging.cs:69-81,148-165`): `id` (string, required for dedupe), `body`, `from`,
`attachment`, `type` (`gift` ⇒ GiftData), **`created` (required, `DateTime.Parse`able)**, `gift_type`, plus any
stored custom fields (kept in `AdditionalData`). Gift body = JSON string
`{"gifts":[{"name":"<item>","value":"<qty>"}],"message":"…"}`; if body empty, `gift_type` becomes a single
gift of qty 1; neither ⇒ `CorruptedGiftException`.
```json
[{"id":"msg1","from":"fed_id:server","type":"gift","created":"2026-10-24T12:00:00Z","gift_type":"LiveTournament",
  "body":"{\"gifts\":[{\"name\":\"Credits\",\"value\":\"500\"}],\"message\":\"Well done\"}","title":"Reward","roomId":"r1"}]
```

### 4.8 Alerts / Kairos (`alert`) – `OP/AlertsApi.cs`, `OP/AlertListener.cs`

| # | Method / path | Scope | Params | Response |
|---|---|---|---|---|
| A1 | `GET alerts/me?push_method=streaming&alert_types=<comma list>&content_type=event-stream[&targets=<space list>]` | alert | (FormUrlEncodedCustom) | **SSE-like stream**: lines `data: {json}`; non-`data:` lines ignored |
| A2 | `POST /alerts/{receiver}` | alert_send | `alert=<json>` | unused |

Alert JSON: `{"type":"message","transport":"secured"}`, `{"type":"connection_request","connection_type":"friend"}`,
`{"type":"invitation","room":"…","credential":"…"}`, others just `{"type":…}` (`OP/AlertListener.cs:143-151`,
`OP/MessageAlert.cs`, …). Types: `message, connection, connection_request, connection_request_accepted,
invitation, wall_post, join_room, left_room, online_status, group_request, set_profile`.
Behaviour: listener reconnects forever; on exception back-off doubles 1→300 s (`OP/AlertListener.cs:73-97`), but
**a 200 response that ends immediately is re-requested with no delay (busy loop)** – keep the stream open or
return 404/503. **Never send `{"type":"connection"}`**: the server-side connection listener treats it as
"multiple connection detected" and kicks the player (`EN/Connection.cs:1610-1616`).
Use: send `{"type":"message","transport":"secured"}` when you store a new mailbox message.

### 4.9 Leaderboards / Olympus (`leaderboard`) – `OP/LeaderboardApi.cs`, `OP/Leaderboard.cs`

`{sort}` = `desc` (default) or `asc`.

| # | Method / path | Scope | Params | Response |
|---|---|---|---|---|
| L1 | `POST leaderboards/{sort}/{name}/me` | leaderboard | `score, replace_score_if? (higher/lower), display_name (default = credential username), expiration_date? ("u" fmt) / expiration_duration?` + custom `_level,_avatar,_leagueId,_fed_id,_alias,_killSignatureEmblem,_killSignatureBg,_clan,_clanId,_clanIcon` (`GM/LeaderboardsComponent.cs:138-160`) | status |
| L2 | `GET leaderboards/{sort}/{name}?offset&limit[&for_entry_id]` | leaderboard | (`limit=0` = "self" query) | LeaderboardData |
| L3 | `GET leaderboards/{sort}/{name}/me/location?offset&limit[&around_me=True][&distance]` | leaderboard | | LeaderboardData |
| L4 | `GET leaderboards/{sort}/{name}/me/friends?offset&limit&connection_type=friend` | leaderboard social | | LeaderboardData |
| L5 | `GET leaderboards/{sort}/{name}/{cred|me}?offset&limit` | leaderboard | | LeaderboardData |
| L6 | `POST leaderboards/{sort}/{name}/settings` | leaderboard_admin | `type=league, group_size, expiration_duration` | 400 tolerated |
| L7 | `GET leaderboards/{sort}/{name}/settings` | leaderboard_admin | | `{"container_type":"league","group_size":100}` |
| L8 | `POST leaderboards/{sort}/{name}/me/delete` | leaderboard | | status |
| L9 | `POST leaderboards/{sort}/{name}/{entry}` | leaderboard | `credential, score, replace_score_if?, display_name, expiration_*?, custom` | status |
| L10 | `GET leaderboards/{sort}/{name}/{cred}[?limit]` | leaderboard | | LeaderboardData |
| L11 | `DELETE leaderboards/{sort}/{name}/{cred}` | leaderboard | | status |

LeaderboardData (`OP/Leaderboard.cs:36-87`) – must be an **object**:
```json
{"data":[{"display_name":"Bob","credential":"fed_id:9f2c…","index":0,"rank":1,"score":1200,"_fed_id":"fed_id:9f2c…","_level":"12"}],
 "total_entries":1,
 "my_entry":{"display_name":"Bob","credential":"fed_id:9f2c…","rank":1,"score":1200}}
```
**Progression impact:** `DivisionsComponent.OnNewSeasonStartedServer` uses `my_entry.rank` and `total_entries`
from L2 (limit 0) to decide promotion/demotion at season end (`GM/DivisionsComponent.cs:370-395`). An offline
server can rank the player among bots (or always report rank 1) to emulate season progression. Leaderboard posts
are skipped if the session is banned from `leaderboard`.

### 4.10 Game stats (`game_stats`) – `OP/GameStatsApi.cs`
Only used by matchmaking heuristics; every call is wrapped in `try/catch` (`GM/GameMatchmakingComponent.cs:1267-1290,1325-1345`).

| # | Method / path | Scope | Params | Response |
|---|---|---|---|---|
| G1 | `GET gamestats/users/queries/default?category&group_keys` | game_stats | | any |
| G2 | `GET gamestats/users/queries/game_stats_by_time?category&group_keys&start&end` (epoch s) | game_stats | | **array** `[{"key1":"3","key2":"<slot>","sum_events":12.0}]` |
| G3 | `POST gamestats/users/me/events/default` | game_stats | `category, keys=a,b, aggregates=x,y,z` | status |

### 4.11 BanTool (absolute `https://bantoolv3.gameloft.org`) – `OP/BanToolApi.cs`
Server-admin tool with HTTP Basic auth (`credential:password` base64). **Not referenced by Game/Engine** (no
`GetService<BanToolService>`). `POST api/campaign` (JSON body `comment, credentials, actionParams, actionId,
reasonId, processAllFoundAccounts`), `GET api/campaign?page&id&user&action&startDate&endDate&pageLimit&campaignAccountsType`,
`GET api/action`, `GET api/asset`, `GET api/reason`. Ignore.

### 4.12 Misc types in the brief
* `MoreUri.EscapeLongDataString` – `Uri.EscapeDataString` in 4096-char chunks (`OP/MoreUri.cs:8-29`).
* `LinkAction` – `error | relink | relink_all` (`if_exists` values).
* `NewsFeedData` – parses an array of `{asset, body, redirect, title, youtube, news_id, timestamp (epoch), end_date}`
  (`OP/NewsFeedData.cs`); no C# producer request found (fed from CRM/assets elsewhere) **[V no caller]**.
* `ClientConnectionData` – `{credential, slot, room_id, fed_id}`; note the decompiled code reads `Slot` from
  `room_id` (bug) and throws if `fed_id` is empty (`OP/ClientConnectionData.cs:21-27`).
* `Scope` constants – full list in `OP/Scope.cs` (note typo `acount_merge`).

---

## 5. Hashing / signing / encryption
* **None** on Federation requests or responses in C#. No HMAC, no body signature, no response signature check.
  TLS validation disabled. **[V]**
* `encrypt_token` is server-side "encryption" of the token for Anubis; opaque to the client. **[V]**
* ETS tracking adds `X-ETS-sha1: sha1(json body)` (`EN/EtsRequestMessage.cs:110-117`) – server may ignore.
* **Native** IAP (`gl_ecomm_crm`) verifies an ecomm response header hash with a public key
  ("Failed to parse ecomm response headers. Hash incorrect…") – IAP analyst's problem. **[V strings]**

---

## 6. Which endpoints actually matter (offline priority)

| Priority | Endpoints | Why |
|---|---|---|
| **P0 – boot/login** | eve `/`, eve datacenters, eve urls, pandora locate, `auth/authorize` (password, game, for_username grants), `auth/users/me` | otherwise no session ⇒ no `Connect()`, no embedded server, error popups loop |
| **P0 – server handshake** | `auth/users?device_id=` (→`[]`), `auth/users/{cred}/compliance`, `auth/verify`, `auth/encrypt_token`, `alert/alerts/me` (idle stream) + storage/CRM/matchmaker (other reports) | per-player server session, LocalAnubis login, profile load |
| **P1 – progression/UX** | `groupchat/chat/words/filter` (player name), `auth/games/mygame/alias`, `message/*` mailbox (tournament rewards, gifts), `leaderboard/*` (divisions, leaderboards UI), `social/events` (TLE/seasons), `social/accounts/{cred}` | name entry, rewards, seasons |
| **P2 – social** | clans, clan chat, wall, friends/requests, status line | feature-complete social; can be stubbed with empty arrays |
| **P3 – ignore/stub 200** | game_stats, ETS tracking, marketing hits, BanTool, cmt report, transfer codes, SNS link, devices/mydevice | telemetry / admin |

Safe generic stub: unknown GET → `200 {}` except endpoints documented as arrays (`users?device_id`, `events`,
`groups*` searches, `members`, `connections`, `requests`, `wall`, `messages/*/me`, `game_stats_by_time`) → `200 []`.

---

## 7. Practical: pointing the game at a replacement

* **Eve URL**: literal `"https://eve.gameloft.com/"` in `OnlineLibsSettingsComponent.EnvironmentSwitcherUrl`
  (`EN/OnlineLibsSettingsComponent.cs:68`); every Federation creation goes through it
  (`EN/OnlinePlatformComponent.cs:119,144`, `EN/LocalAnubisManager.cs:36`, `GM/GameServerComponent.cs:922`,
  `GM/GameClientComponent.cs:3295`, `EN/Connectivity.cs:20`). Patch to e.g. `http://127.0.0.1:18080/` (keep the
  trailing `/`). **[L]** After IL patching, delete `lib/*/libaot-Gameloft.Engine.dll.so` (Xamarin falls back to JIT
  when the AOT image's MVID does not match) and re-sign.
* `phantomArgs` (`Intent` extra, `':'`-separated, each element becomes one `\n`-separated arg; parser
  `EN/ApplicationArguments.cs:25-89`): usable for `-DeviceId:<id>`, `-DataCenter:local`, `-DefaultProtocol:HTTP`,
  `-LocalAnubisFinderPort:51750`, `-ServerLocation:<enum>`; **not** for URLs or credentials (contain `:`).
  **[V parse / L native join]**
* Native engine has its own Gaia/Janus/Hestia/Seshat/Olympus/Osiris/Hermes/Kairos client (strings around
  `0x12e9d00-0x12ecf00` of `libnativeengine.so`, incl. `https://eve.gameloft.com` and
  `http://eve.gameloft.com:20001/config/`). It receives the C# eve config via `OnlinePlatformComponent_Init_private`,
  so native calls (if any) will hit the same `pandora`. **[S]** what the native side actually calls at runtime
  (IAP/CRM config, GameOptions asset `/locate/asset` + `/assets/…/GameOptions`, GDID) – capture with a logging stub.

### 7.1 Reference routing sketch (single host)
```
GET  /                                   -> 200 "ok"
GET  /config/{cid}/datacenters           -> [{"name":"local","status":"active","preferred":true,"country_code":"US"}]
GET  /config/{cid}/datacenters/{dc}/urls -> {"status":"none","pandora":BASE, ...}
GET  /locate/{svc}  | /locate?service=   -> text/plain BASE
POST /authorize                          -> grant: password|game(+for_username) ; GET /authorize?grant_type=transfer_code
GET  /users/me | /users/{cred}           -> {"account":acct(cred),"credentials":[cred,...]}   (404 if unknown non-me)
GET  /users?device_id=                   -> []
POST /users/{cred}/compliance            -> {"default":{"date":NOW,"compliant":true,"reasons":{}}}
GET  /encrypt_token                      -> text/plain <access_token>
GET  /verify                             -> {"credential","account","client_id","scope"}
POST /games/mygame/alias                 -> {"alias":digits(acct)} ; GET /games/mygame/alias/{a} -> {"credential":"fed_id:.."}
POST /users/me/credentials | /users/me/transfer_code | /devices/mydevice -> 200 {}
POST /chat/words[/{lang}]/filter         -> {"matches":false,"result":message}
GET  /alerts/me                          -> 200 text/event-stream, keep open (": ping\n" every 30s)
messages/*, leaderboards/*, groups/*, accounts/*, events*, /{type}/{id}/wall, chat/* -> see section 4
```
Token = `v1.` + base64url(json{credential, account, client_id, scope}); account = deterministic hash of the
end-user credential (for `for_username` grants use the *for* credential). Accept any password.

---

## 8. Verified facts vs inferences (summary)

Verified (code read): all request paths/methods/params/scopes in sections 2-4; response field usage; retry and
status semantics; TLS acceptance; token caching; `phantomArgs` colon split; ServerInfos.json requirement;
LocalAnubis use of verify/encrypt_token; client ID string in Java; Xamarin handler/TLS env.

Likely: implicit string→class conversion for `visibility/type/membership/activity_type` JSON values (Newtonsoft
cast operators); `Expect: 100-continue`; native joins `phantomArgs` with `\n`; AOT fallback to JIT after patching;
C# clientId equals the Java one.

Speculative: native Gaia GDID/locate request formats and whether native calls Federation at runtime.

---

## 9. Endpoints owned by other analysts (listed only)

* **Storage/Seshat** (`storage`): `GET|POST profiles/{cred}/myprofile[/{selector}]` (`include_fields`, `If-Match`),
  `POST profiles/{cred}/myprofile/delete`, `GET /profiles`, `GET|POST data/{cred}/{selector}`, `GET data`,
  `POST data/me/{selector}/delete`, `GET /profiles/matchers/{name}/matches`, `POST|GET /profiles/me/myprofile/visibility[/{sel}]`,
  `GET profiles/location/{cred}`, `GET /profiles/{cred}/{name}/backups[/{id}]` (`OP/StorageApi.cs`). 412 on save is
  handled (reload), 404 means "new account".
* **CRM/Hestia** (`config`): `GET configs/users/me[/{selector}]` scope `config storage_ro`, `If-Match` (`OP/CrmApi.cs`).
* **Assets/Iris** (`asset`, **no token**): `GET assets/{cid}`, `assets/{cid}/{name}` (Range header), `/metadata[/{attr}]`,
  `/url`, `coupons/{cid}/{code}` (`OP/AssetsApi.cs`).
* **Matchmaker** (`matchmaker`): `POST rooms/{cid}/quick_launch`, `POST rooms/{cid}/automatch/{automatcher}`,
  `GET rooms/{cid}?server_type&limit&<filters>` (no scope), `GET lobbyconfigs/{cid}/{serverType}` (lobby_game_admin)
  (`OP/MatchmakerApi.cs`); LocalAnubis Finder `GET http://{host}:51750/locate?service=lobby` (`EN/ClientLink.cs:448-458`).
* **Lobby/Anubis** (`lobby`): TCP JSON protocol (`.../Gameloft.OnlinePlatform.Lobby/LobbyApi.cs`).
* **GamePortal** (`game_portal`): `ios/1916/public/gamePortal/gamePortal.wsgi?request&clientId&datacenter&accessToken…`,
  `POST {basePath}` `op_code, client_id, credential` (`OP/GamePortalApi.cs`) – no game caller found.
* **IAP/Ecomm** (`ecomm_api_root`, `crm_iap`): `EN/IapStoreComponent.cs:874-876` + native.
* **Tracking** (`etsv2`): `POST {etsv2}` JSON + `X-ETS-sha1` (`EN/EtsRequestMessage.cs`). **Marketing hits**:
  `{marketing_site}/redir/?…` (`EN/HitsFedRequestMessage.cs`). **Ads** (`ads_agency`, `EN/AdsComponent.cs:580`).
* Misc hosts: `http://ip.ecomapis.gameloft.org/location/?ip=`, `https://it.gameloft.org/dump/dump.edgelist`
  (`decompiled/Gameloft.Util/Gameloft.Util/Geolocalization.cs:93-95`), `https://201205igp.gameloft.com`
  (`EN/BrowserComponent.cs:78`), `https://surveyplatform.gameloft.com` (cheat menu).

---

## 10. Open questions
1. Exact native GDID acquisition path and whether it blocks without network (use `-DeviceId` to be safe).
2. Whether the native Gaia stack issues its own eve/pandora/janus calls at boot (IAP init, GameOptions asset).
3. Real eve `urls` key set for this client id (only C#-referenced keys are known) – a capture or archived response
   would confirm (`captures/` is empty).
4. Where `ServerInfos.json` should live so `GlfStream.Exists` finds it (OBB vs DLC vs data dir) – needed for any
   embedded/LocalAnubis server.
5. P2P model: in MCVS the *host's* game server reads/writes each player's profile via Federation storage
   (`SessionForUser`). With per-device replacement servers, a guest's profile would be loaded from the host's store –
   needs a sync/proxy design (storage/multiplayer analysts).
6. `OnlinePlatformComponent.defaultProtocol` template value (OBB) – affects `FormatUri` when ports are 80/443.
