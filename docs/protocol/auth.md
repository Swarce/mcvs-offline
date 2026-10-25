# Protocol notes: AUTH

Owner group: **AUTH** (Eve, Pandora, Auth). Code: `server/Services/Eve/`, `server/Services/Pandora/`,
`server/Services/Auth/`. Tests: `tests/McvsOffline.Tests/Auth/`.

Scope: eve (environment switcher), pandora (service locator), auth (Janus).
Evidence: report 02 §1-§3; verify-C1 §2; verify-C2 §1.2-1.9, §3; verify-C6 §3, §5; ARCHITECTURE §2.2, §3
"Accounts & auth". Client code is cited as `OP/` = `decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/`,
`EN/` = `decompiled/Gameloft.Engine/Gameloft.Engine/`, `LA/` = `.../Gameloft.Engine.LocalAnubis/`,
`GM/` = `decompiled/Game/Game/`.

All examples below were captured from the standalone host (`scripts/run-standalone.sh --port 51791`); `Date` and
`Server` headers are left out.

## 1. Conventions

* **One host, every service at the root.** eve is the URL from `-EnvironmentSwitcherUrl` (args.txt or lobbyconfigs
  `cmd_args`), used verbatim. Every other service URL we hand out is `<base><service>/`, e.g.
  `http://127.0.0.1:51780/storage/`, where `<base>` is `context.BaseUrlFor(request)` (explicit port, trailing slash;
  the request's Host in LAN mode).
* **How the client joins them** (`OP/Federation.cs` `FormatUri`, `OP/FedRequestMessage.cs` `ResolveUri`):
  `FormatUri` prefixes `https://` when the value does not start with `http`, rewrites the scheme and drops the port
  when a caller forces a protocol *and* the port is 80/443/absent (LocalAnubis forces HTTPS), and trims the trailing
  `/`. The request URL is then `new Uri(new Uri(base), path)`: the last segment (`storage`) is replaced by the
  relative path, absolute paths start at the root. So `http://127.0.0.1:51780/storage/` + `profiles/me/myprofile` →
  `http://127.0.0.1:51780/profiles/me/myprofile`. URLs without an explicit non-80/443 port are logged as unsafe.
* **Token placement** (`OP/FedRequestMessage.cs` `PrepareToSend`): when a request has scopes and its URL has no query,
  `access_token` is appended to the parameters (replacing one already there). GET puts parameters in the query, POST
  in an `application/x-www-form-urlencoded; charset=utf-8` body, DELETE puts only `access_token` in the query and all
  parameters in the body. Encoding is `Uri.EscapeDataString` for keys and values (`:` → `%3A`, space → `%20`),
  booleans are `True`/`False`. Requests without scopes (authorize, locate, verify, eve) carry no token of their own.
* **Status codes** (`OP/Federation.cs` `Inspect`): 2xx and 302 succeed. 401 on a request that carried
  `access_token` makes the client drop **all** its tokens, re-authorize once and retry; 401 on `authorize` aborts the
  session. 403 from eve is "upgrade required", 403 from authorize is "banned": **we never answer 403**. Other 4xx
  abort the request (callers branch on 404 for "no such account"). 5xx/408 are retried.
* Every response the client parses as JSON has a JSON body (an empty body is a null token and an NRE). locate and
  encrypt_token are `text/plain; charset=utf-8`, no quotes, no trailing newline. No `ETag`, no `WWW-Authenticate`.
* Every request is logged by the core (summary and parameters at INFO, full request at DEBUG, unknown endpoints in
  full plus `logs/unknown-endpoints.jsonl`). The modules add one INFO line per decision (account created, authorize
  outcome, alias assigned, credential relinked, transfer code) and a WARN for every 401 with its reason.

## 2. Endpoints

| Method | Path template | Route name | Response | Evidence | Status |
|---|---|---|---|---|---|
| GET | `/` | `core.root` (core) | 200 `{"eve":"on","version":"mcvs-offline/<v>"}` | report 02 §2.5 (`EN/Connectivity.cs`) | done (core) |
| GET | `config/{clientId}/datacenters` | `eve.datacenters` | 200 `[{"name":<dataCenter>,"status":"active","preferred":true,"country_code":"US"}]` | `OP/Federation.cs` LoadDataCenter, `OP/Datacenters.cs` | done |
| GET | `config/{clientId}/datacenters/{dc}/urls` | `eve.urls` | 200 object of 7 URL strings, no `status` | `OP/Configuration.cs`, verify-C2 §1.2, §1.9 | done |
| GET | `locate/{service}` | `pandora.locate` | 200 text `<base><service>/` | `OP/Federation.cs` LocateServiceTask | done |
| GET | `locate?service=` | `pandora.locate.query` | same | native `/locate` + `service=` strings | done |
| * | `<service>/{first}/{*rest}` | `pandora.prefix.<service>` | the root route's answer | §4 below | done |
| POST, GET | `authorize` | `auth.authorize` | 200 `{access_token, token_type, expires_in, scope}` | `OP/Session.cs` Authorize, `OP/SessionForUser.cs` | done |
| GET | `users/{credential}` | `auth.users.get` | 200 `{account, credentials}`; `me` needs a token (401); unknown → 404 | `OP/Session.cs` GetAccountInfoTask, `EN/SocialComponent.cs:857` | done |
| GET | `users?device_id=` | `auth.users.by_device` | 200 `[]` | `GM/GameProfileSection.cs:155`, verify-C2 §1.6 | done |
| POST, GET | `users/{credential}/compliance` | `auth.users.compliance` | 200 `{"default":{date, compliant:true, reasons:{}}}` | `GM/AgeGateChecker.cs` | done |
| GET | `users/{credential}/games/{clientId}` | `auth.users.games` | 200 if the account exists, else 404 | `OP/Session.cs` CheckIfUserExists | done |
| POST | `users/me/credentials` | `auth.users.link` | 200 `{account, credentials}`; 409 when `if_exists=error` and owned elsewhere | `OP/Session.cs` LinkCredential, `EN/AuthenticationComponent.cs` | done |
| POST | `users/me/transfer_code` | `auth.users.transfer_code` | 200 `{"value","expiration"}` | `OP/TransferCode.cs` | done |
| POST | `devices/mydevice` | `auth.devices.mydevice` | 200 `{}` | `OP/Session.cs` SetDeviceInfo (unused) | done |
| GET, POST | `encrypt_token` | `auth.encrypt_token` | 200 text: the token | `EN/AnubisClientSocket.cs` Login | done |
| GET, POST | `verify` | `auth.verify` | 200 `{credential, account, client_id, scope}`; 400 without `access_token`; 401 unknown | `LA/BaseServer.cs`, `LA/FinderHTTPMessageHandler.cs`, `OP/RoomUser.cs` | done |
| POST, GET | `games/{game}/alias` | `auth.alias` | 200 `{"alias":"<9 digits>"}` | `GM/PlayerProfileComponent.cs` InitPlayerName | done |
| GET | `games/{game}/alias/{alias}` | `auth.alias.lookup` | 200 `{"credential":"fed_id:<id>"}`, else 404 | `OP/Session.cs` LookupAlias | done |

Not implemented (logged as unknown endpoints when called): `GET services.json?internal=true` (only with
`-UseInternalFedAddress`), `users/me/approvals/...` (native string only), anything else the native Gaia layer may send.

## 3. Wire format

### 3.1 eve: datacenters

`Federation.ListDatacenters` (no token; `country`/`override_ip_geolocalisation` are never set by the game):

```
GET /config/2620:66102:1.17.32:android:googleplay/datacenters HTTP/1.1
Cache-Control: NO-CACHE
User-Agent: (2620:66102:1.17.32:android:googleplay; )

HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Content-Length: 72

[{"name":"mcvs","status":"active","preferred":true,"country_code":"US"}]
```

The client takes the first `preferred` entry; an empty name fails with "Incorrect datacenter". The name is
`settings.dataCenter` (default `mcvs`, validated cmd-arg safe because lobbyconfigs repeats it as `-DataCenter`); the
client persists it (`SaveStream "datacenter"`).

### 3.2 eve: urls

```
GET /config/2620:66102:1.17.32:android:googleplay/datacenters/mcvs/urls HTTP/1.1

HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"pandora":"http://127.0.0.1:51791/pandora/","game_portal":"http://127.0.0.1:51791/game_portal/",
 "ads_agency":"http://127.0.0.1:51791/ads_agency/","marketing_site":"http://127.0.0.1:51791/marketing_site/",
 "ecomm_api_root":"http://127.0.0.1:51791/ecomm_api_root/","crm_iap":"http://127.0.0.1:51791/crm_iap/",
 "etsv2":"http://127.0.0.1:51791/etsv2/"}
```

* No `status` (and no `code`): `Configuration` parses it into `ConfigStatus` and throws `FormatException` for
  unknown values; anything other than none shows update/maintenance popups or blocks login (verify-C2 §1.2).
* `pandora` is needed for every fed service; `ecomm_api_root` and `crm_iap` are located by
  `IapStoreComponent.InitNative`, which throws without them (verify-C2 §1.9); `etsv2`, `marketing_site`,
  `game_portal`, `ads_agency` avoid "Invalid online configuration" in tracking, browser, game portal and ads code.
  The whole object is also handed to native (`OnlinePlatformComponent_Init_private`).
* Answered for every client id and datacenter name (logged when they are not ours): an install that played online
  may still have Gameloft's datacenter saved, and the DLC code asks for its own.
* What the C# client then calls with them (endpoints owned by MISC): tracking posts to path `""`, i.e. `POST /etsv2`
  (`EN/EtsRequestMessage.cs`); marketing hits resolve `redir/` against the marketing URL, i.e. `/redir/?...`
  (`EN/HitsFedRequestMessage.cs`); game portal paths are relative too. `ecomm_api_root`, `crm_iap` and `ads_agency`
  are passed to native code, whose way of joining them is unknown (§4 covers string joins).

### 3.3 pandora: locate

```
GET /locate/storage?client_id=2620%3A66102%3A1.17.32%3Aandroid%3Agoogleplay HTTP/1.1

HTTP/1.1 200 OK
Content-Type: text/plain; charset=utf-8
Content-Length: 31

http://127.0.0.1:51791/storage/
```

Services the C# client locates (`OP/OnlineService.cs` FedServices): `auth storage leaderboard alert config groupchat
lobby matchmaker message social asset game_stats`. The native Gaia layer knows `feeds lottery voice schedule
globalidservice transaction` too. Any name is answered (unknown names are logged at WARN); a name that is not
`[A-Za-z0-9_.-]{1,64}` gets the bare base URL. `GET /locate?service=<name>` gives the same answer.
`settings.pandora.urls.<service>` overrides one URL (e.g. to point storage at another machine).

`lobby` is only located when `ClientSettings.OverrideAnubisHost` is false (online mode): `Lobby.Login` then opens a TCP
Anubis connection to the URL's host and port. In Embedded mode the LocalAnubis lobby is used instead.

### 3.4 authorize: player (password grant)

`Session.GetAuthorizeRequest` field order; `scope` = `auth ∪ InitialScopesClient`; `device_*` from
`OpenAnonymousSession` (resolution may be empty):

```
POST /authorize HTTP/1.1
Content-Type: application/x-www-form-urlencoded; charset=utf-8
Expect: 100-continue

username=0123abcd&credential_type=anonymous&password=s3cret&device_id=c0ffee
&client_id=2620%3A66102%3A1.17.32%3Aandroid%3Agoogleplay&scope=auth%20lobby%20config
&device_firmware=10&device_country=US&device_carrier=...&device_model=...&device_resolution=...&device_language=en

HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"access_token":"mcvsf53873d5.1a893868ac7a7cad9d7b9f206436e65d","token_type":"bearer","expires_in":31536000,"scope":"auth lobby config"}
```

* `access_token` and `scope` are both dereferenced (a missing one is an NRE outside the online-error handling).
  `scope` echoes the request in its order (the client asserts set equality). `token_type`/`expires_in` are ignored by
  C#; `expires_in` is one year so a native "now + expires_in" stays far from 32-bit overflow.
* Unknown anonymous credentials are created on the spot (there is no account-creation request); any password is
  accepted (the anonymous username and password come from native and may change with app data).
* The client caches one token per scope set and authorizes again with exactly the new set
  (`Session.GetAccessToken`), so the server sees many scope strings (`auth lobby config`, `lobby`, `lobby_admin`,
  `lobby_game_admin`, `config storage_ro`, `auth_admin_ro`, the 21 server scopes...). The same subject, actor,
  client id and scope set always get the same token back.
* Malformed requests (no `username`/`credential_type`, type `none`/`me`, a `fed_id` that is not
  `[A-Za-z0-9_-]{1,64}`, an unsupported `grant_type`) get **400**.

### 3.5 authorize: game server and on-behalf grants

The TS/GS own session (`OpenGameServerSession`, no `device_*`):

```
username=mcvsoffline&credential_type=game&password=mcvsoffline&device_id=<id>&client_id=...&scope=auth%20storage_restricted%20chat%20...
```

The TS handshake's per-player session (`SessionForUser.GetAuthorizeRequest`: the base fields with
username/credential_type/password replaced in place, then `for_*` appended; `device_id` is `Unknown` when the
server has none):

```
POST /authorize
username=mcvsoffline&credential_type=game&password=mcvsoffline&device_id=c0ffee&client_id=...
&scope=auth%20storage%20lobby&for_username=0123abcd&for_credential_type=anonymous

{"access_token":"mcvsf53873d5.1b13879a0890bd45fe1809855fbf5c34","token_type":"bearer","expires_in":31536000,"scope":"auth storage lobby"}
```

* The token's subject is the player: `users/me` with it returns the player's account, the same one the client's own
  session got (verify-C2 §1.5). `for_credential_type=fed_id` addresses the account id directly
  (`GM/GameProfileSection.cs` opens such sessions). The player's account is created if needed (a LAN guest whose
  client logged in elsewhere gets the same derived id). Only the subject gets an account.
* Only `game:` credentials may act on behalf (others: **401**); on-behalf grants for a `game:` subject: 400.
* Any `game:` credential logs in as a game server (the TS/GS read it from `cmd_args`, args.txt or `ServerInfos.json`).
  One that differs from `settings.superCredential`/`superPassword` is logged at WARN and refused with **401** only
  when `settings.auth.requireSuperPassword` is true (for LAN hosts that do not want guests to obtain on-behalf
  tokens). Default false: a refused game server can load no profile at all.

### 3.6 authorize: transfer code

```
GET /authorize?grant_type=transfer_code&scope=auth&client_id=2620%3A66102%3A1.17.32%3Aandroid%3Agoogleplay&transfer_code=QTXYQCVH32

{"access_token":"...","token_type":"bearer","expires_in":31536000,"scope":"auth"}
```

Unknown, used or expired code: **404** `{"error":"invalid_grant",...}`. Codes are case-insensitive, single-use, and
the token's subject is `fed_id:<account>`.

### 3.7 users

```
GET /users/me?access_token=mcvsf53873d5.1a893868ac7a7cad9d7b9f206436e65d

{"account":"0ca880ce086fd076127c7265","credentials":["anonymous:0123abcd"]}
```

* `account` is the bare id (the client prefixes `fed_id:`; it never contains `:`); each credential is
  `type:username` and must parse as a client `Credential` (exactly one `:`).
* `users/me` without a valid token, or for a token whose account is gone: **401** (the client re-authorizes, which
  re-creates the account under the same derived id).
* `users/{credential}` for anyone else (`users/facebook%3A123`, `users/fed_id%3A<id>`; `%3A` or `:`): the account or
  **404** `{"error":"not_found",...}`. A token is optional (`auth_admin_ro`, or none from `Federation.CheckIfUserExists`).
* `GET /users?device_id=c0ffee&access_token=...` → `[]` always, with or without `all_games=True` and with or without a
  valid token. On first run the TS merges into any account listed there and restarts the handshake
  (`GameProfileSection.CreateProfile`); every player keeps exactly one account here, so there is nothing to merge.
* `POST /users/fed_id%3A<id>/compliance` with `country=US&age=15&access_token=...` →
  `{"default":{"date":"2026-10-25T03:48:56Z","compliant":true,"reasons":{}}}`. All compliant gives the game's lowest
  age limit (13); GET returns the same.
* `GET /users/{credential}/games/{clientId}`: 200 with the account info plus `client_id`, or 404.

### 3.8 Linking credentials

`POST users/me/credentials` with the caller's token (scope `auth`, or `auth_game_admin` without a password) and
`if_exists=<error|relink|relink_all>&credential=<type:user>[&password=...]`. The transfer-code flow sends
`access_token=<transfer token>&if_exists=relink_all&resolve_conflicts=False&credential=anonymous:...&password=...`
with no scope of its own (`EN/AuthenticationComponent.cs` GetLinkAnonymousRequest).

| Credential state | error | relink | relink_all |
|---|---|---|---|
| unknown | linked | linked | linked |
| already ours | 200 | 200 | 200 |
| owned by account X | **409** `credential_in_use` | moved to us | every credential of X moved to us |

Answer: 200 `{account, credentials}` of the caller. `game:`, `fed_id:` and `me` cannot be linked (400). Profiles stay
with their account ids: after a relink the next login of that credential lands on the new account.

`POST users/me/transfer_code` → `{"value":"QTXYQCVH32","expiration":"2026-11-01T03:48:56Z"}`: 10 characters without
0/O/1/I/L, valid `settings.auth.transferCodeHours` (default 168), stored in `<data>/auth/transfer-codes.json`.
No game C# code calls the transfer-code endpoints; they exist for completeness.

### 3.9 Lobby token hand-off: encrypt_token and verify

Client (scope `lobby`; the game server uses `lobby_admin`), nonce first, token appended:

```
GET /encrypt_token?nonce=LocalAnubisNonce&access_token=mcvsf53873d5.1a893868ac7a7cad9d7b9f206436e65d

HTTP/1.1 200 OK
Content-Type: text/plain; charset=utf-8
Content-Length: 45

mcvsf53873d5.1a893868ac7a7cad9d7b9f206436e65d
```

The Controller (`LA/BaseServer.cs` Login) and the Finder (`LA/FinderHTTPMessageHandler.cs` ParseUser) verify through
the LocalAnubis session; the `access_token` parameter is the token being checked:

```
GET /verify?access_token=mcvsf53873d5.1a893868ac7a7cad9d7b9f206436e65d&nonce=LocalAnubisNonce&credential=system%3AAnubis

{"credential":"anonymous:0123abcd","account":"0ca880ce086fd076127c7265","client_id":"2620:66102:1.17.32:android:googleplay","scope":"auth lobby config"}
```

* `credential`, `account` and `client_id` are always strings: `RoomUser.FromAccountInfo` dereferences all three and
  builds `fed_id:<account>`; the Controller copies them into the room user that becomes the TS connection's
  credential and fed id. `account` is the id storage keys the profile by.
* Our "encryption" is the identity: the token is opaque to the client and already self-describing. The nonce is not
  checked (the client sends the lobby's nonce, the Controller verifies with the constant `LocalAnubisNonce`).
* No `access_token`: 400. Unknown token: 401 (the LocalAnubis session then drops its own tokens and retries once,
  harmlessly).

### 3.10 Aliases

```
POST /games/mygame/alias            access_token=mcvsf53873d5.1a893868ac7a7cad9d7b9f206436e65d
{"alias":"394220313"}

GET /games/mygame/alias/394220313?access_token=...
{"credential":"fed_id:0ca880ce086fd076127c7265"}
```

The game names a new player `agent-<alias>` (`InitPlayerName`, uncaught: it must succeed). The alias is 9 digits
(no leading zero) derived from the account id (SHA-1, `AliasRegistry.Derive`), stored in `Account.Data["alias"]`,
identical on every call, across restarts and on every server that derives the same account id; a collision moves on
to the next derivation. `GET games/mygame/alias` returns it too. Unknown alias: 404.

## 4. Service-prefixed paths

The C# client always reaches root paths (§1), but code that joins strings does not:
`DownloadableContent.RefreshServerAddressAsync` requests `pandora + "/locate/asset"` (`EN/DownloadableContent.cs:401-411`),
and `libnativeengine.so` carries `/authorize`, `/users/me`, `/locate`, `/configs/users/me/iap` path literals. With our
URLs that gives `/pandora//locate/asset` or `/auth//authorize`.

Pandora registers `<service>/{first}/{*rest}` for every known service name at `Router.CatchAllOrder - 1`:

* exact routes of any module still win (e.g. eve's `config/{clientId}/datacenters` over the `config` prefix);
* the prefix (or a chain of prefixes) is stripped when the rest hits a *specific* route (a template that starts with
  a literal, catch-all suffixes included, e.g. storage's `profiles/{credential}/myprofile/{*selector}`);
* otherwise the original request goes to the first non-alias route that matches it, so a broad catch-all (MISC's)
  still sees `/etsv2/events`, and unmatched requests are logged as unknown with the path the client sent.

Example: `GET /pandora//locate/asset` → `http://127.0.0.1:51791/asset/`, logged as
`service-prefixed path /pandora/locate/asset -> /locate/asset [pandora.locate]`.

## 5. Accounts and tokens (core, used as follows)

* Account id = `hex(sha1("mcvs-offline/account/v1|" + credential))[0..24]` (core `AccountStore`), persisted in
  `accounts.json` with linked credentials, device ids and per-module data (`alias`). The client's own session and the
  game server's on-behalf session always agree, and so do independent servers (LAN, reinstall).
* Tokens = `<instanceId>.<32 hex>` (core `TokenService`), persisted in `tokens.json`, never expire, no `~` (the client
  derives a "silo" from tokens containing `~`). The issuer prefix lets a LAN host tell which device issued a token;
  forwarding verification there is roadmap M3. Today a token we did not issue (or lost) is unknown → 401, and the
  client recovers by re-authorizing once.
* Scopes are recorded and echoed but not enforced: the client always presents a token whose scope set covers the
  request, and a wrong refusal would only cost a 401 round trip per request.

## 6. For other modules: `IAuthenticator`

`AuthService.Register` puts an `IAuthenticator` into `context.Features` (interface in `server/Services/Auth/Authenticator.cs`):

```csharp
var auth = context.Features.Get<IAuthenticator>();
AuthIdentity who = auth.Authenticate(request);             // form, query, JSON body, or "Authorization: Bearer"
if (who == null) return auth.Unauthorized(request, "...");  // 401 + WARN log, never 403
Account target = auth.ResolveAccount(who, request.RouteValue("credential")); // "me", "fed_id:x" (%3A or :), "type:user"
// who.AccountId / who.FedId: the subject (the player for on-behalf tokens)
// who.IsGameServer: a game server holds the token (own session or on-behalf): may address other accounts
// who.IsOnBehalf, who.Actor, who.Credential, who.Token.Scopes, who.Token.ClientId
```

`Authenticate` returns null for a missing or unknown token and for a token whose account no longer exists.

## 7. Settings

| Key | Default | Effect |
|---|---|---|
| `dataCenter` (core) | `mcvs` | datacenter name in eve answers |
| `superCredential` / `superPassword` (core) | `game:mcvsoffline` / `mcvsoffline` | the expected game-server login |
| `publicUrl`, `lanMode` (core) | | base of every URL handed out |
| `auth.requireSuperPassword` | `false` | refuse (401) game logins that do not match the configured super credential |
| `auth.transferCodeHours` | `168` | transfer code lifetime (1 h to 1 year) |
| `pandora.urls.<service>` | unset | override one service URL (eve urls and locate) |

## 8. Decisions

1. **No `status` in eve urls** (verify-C2 §1.2 overrides report 02's `"none"`): absent is parsed as None and cannot be
   misread.
2. **`<base><service>/` service URLs** as ARCHITECTURE §2.2 prescribes; equivalent to the bare root for the C# client
   (FormatUri trims, RFC 3986 joins), more informative in logs, and safe for string joins thanks to §4.
3. **GET / stays with `core.root`**: it already answers what `Connectivity.IsInternetAvailable` needs, and core tests
   pin that route name.
4. **Permissive game-server logins** by default (§3.5): a refused super credential silently breaks every profile load;
   strictness is opt-in.
5. **`users?device_id` is always `[]`**: listing accounts would trigger the TS account merge and a handshake retry
   (verify-C2 §1.6); with derived ids there is never a second account to merge.
6. **encrypt_token returns the token itself**; verify resolves it. Nonces are not checked.
7. **Account missing for a known token → 401**, not a synthesized answer: the client's own retry re-creates the
   account consistently.
8. **Aliases derived from the account id**, so a LAN host and a reinstall show the same `agent-<alias>` name.
9. **Unknown fed ids are created on on-behalf grants** instead of refused: never block a game server; ids are
   restricted to `[A-Za-z0-9_-]{1,64}` because they become storage keys.

## 9. Tests

* `FedClientReplay.cs` rebuilds requests as `Gameloft.OnlinePlatform` does (parameter order and encoding, token
  placement, FormatUri and RFC 3986 joining, per-scope token cache, the 401 retry) and drives the composed server
  in-process: `EvePandoraTests`, `AuthorizeTests`, `AuthEndpointsTests`. `AuthWireTests` replays the boot over a
  socket (Expect: 100-continue, keep-alive).
* `RealClientConformanceTests` runs the **real** `Gameloft.OnlinePlatform.dll` from `MCVS_REFS` (default
  `/home/user/mcvs-work/extracted/assemblies`; skipped when absent) against the server over HTTP: anonymous,
  game-server, on-behalf and LocalAnubis (HTTPS-forced) sessions, encrypt_token → verify →
  `RoomUser.FromAccountInfo`, first-run calls (`users?device_id`, alias, lookup, compliances, 404 lookup,
  CheckIfUserExists), transfer codes, and 401 recovery after the server lost its tokens and accounts.

## 10. Open questions / device-test checklist

- [ ] Mono's `Uri` keeps or unescapes `%3A` in `users/fed_id%3A...` paths: both are accepted; check the log.
- [ ] Native Gaia: does it call eve/pandora/janus itself (IAP/CRM init, GameOptions asset, GDID)? Look for
      `service-prefixed path` lines, `locate of unknown service`, and unknown endpoints under `/users/me/approvals`.
- [ ] Native consumers of `expires_in` / `refresh_token` (none known).
- [ ] The DLC path (`UseDlc` is false in the shipped build) would build `https://http://...` from our locate answer
      (`"https://" + text + "/assets/"`, `EN/DownloadableContent.cs:411`); irrelevant unless DLC is enabled.
- [ ] LAN (M3): guest tokens issued by the guest's own server are unknown to the host (401). Either guests use the
      host's server for everything (verify-C6 §5 topology A, works today) or verify/storage get forwarded by issuer.
- [ ] Confirm on device that no request logs `401` during a normal boot (only after data loss).
