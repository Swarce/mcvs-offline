# Federation protocol: index

What the server answers, per service, and why. Each module group documents its endpoints, exact responses,
settings and open device questions in its own note under [protocol/](protocol/); the evidence is in
[research/](research/) (the `verify-C*` notes override the numbered reports where they disagree).

## Protocol notes

| Note | Group | Services | Highlights |
|---|---|---|---|
| [protocol/auth.md](protocol/auth.md) | AUTH | eve, pandora, auth | eve datacenters/urls without `status`; pandora `locate` as plain text with explicit `http://` and port; service-prefixed paths (`/auth//authorize`); anonymous, game-server and on-behalf grants; `encrypt_token`/`verify`; aliases, compliance, credential links, transfer codes; 401 never 403 |
| [protocol/storage.md](protocol/storage.md) | STORAGE | storage (Seshat) | the TS's R1-R12 requests; 200 `{}` for missing sections; byte-exact values; no ETag; nested `include_fields`; deep merge; `fed_id` encodings; durability, backups and recovery |
| [protocol/content.md](protocol/content.md) | CONTENT | crm, matchmaker, assets/coupons | CRM document layering over the game's own preconfiguration; `lobbyconfigs` `cmd_args`; unlock coupons; local Iris assets; the content generator |
| [protocol/misc.md](protocol/misc.md) | MISC | social, messaging, alerts, leaderboards, game_stats, profanity, chat, clans, wall, bantool, geo, catch-all | Hermes mailboxes; the Kairos stream; league-group leaderboards; time-limited event validation; tracking and marketing catch-alls; list-shaped unknown endpoints answered `[]` |
| [protocol/unlocks.md](protocol/unlocks.md) | core | `settings.unlockAll` | the inventory-ledger unlock: order, batches, `set_profile` alerts, backup |
| [ECONOMY.md](ECONOMY.md) | CONTENT | offline store | prices, earn rates, tuning |
| [design/settings-page.md](design/settings-page.md) | admin | admin, admin-logs, admin-saves | the in-game settings page: `_mcvs/ui` and `_mcvs/api/v1/*`, one-time tickets and session cookies, the access gate on every route, saves transfer, staged operations |

## Shared conventions

- Transport, parameter placement (GET query, POST form, DELETE both), `Uri.EscapeDataString` encoding and status-code
  semantics: [research/02-federation-protocol.md](research/02-federation-protocol.md) §1.
- Every service is served at the host root; the URLs we hand out are `http://127.0.0.1:<port>/<service>/` and the
  client's RFC 3986 joining lands relative paths on the root routes (ARCHITECTURE §2.2, protocol/auth.md).
- Status codes: 5xx and 408 are retried 5 times, 401 makes the client re-authorize once, **403 is never correct**
  (upgrade-required on eve, a profile wipe on storage), other 4xx abort the request. No response is ever empty JSON.
- No `ETag` is ever sent (verify-C3 §3.5). Every request is logged; unknown endpoints are logged in full and collected
  in `logs/unknown-endpoints.jsonl`.
- **The Host check applies to every route** (`LocalAccessGate`, before the router; [design/settings-page.md](design/settings-page.md)
  §5.2). While the listener is bound to loopback, `lanMode` is off and `security.hostCheck` is on, a Federation request
  whose `Host` is not `127.0.0.1`, `localhost` or `[::1]` (any port) or the `publicUrl` host gets 404 and one WARN per
  minute (`gate: refused Host ...`): a web page that resolved its own name to 127.0.0.1 (DNS rebinding) reaches nothing.
  A missing `Host` is allowed; the game always sends `127.0.0.1:<port>`. `/_mcvs/*` always needs a loopback caller and
  the Host `127.0.0.1`/`localhost`/`[::1]` with this server's port (also in LAN mode); `OPTIONS` there is 405, and every
  write needs `X-MCVS: 1`, an allowed `Origin` if one is sent and no cross-site `Sec-Fetch-Site`. Without a session
  cookie a `/_mcvs/` request body is limited to 1 KiB at header time (413 before the body is read).
- Module seams, features and event topics between modules: [MODULES.md](MODULES.md).

## Endpoint map

All routes the server registers (227; `GET /_mcvs/routes` prints the live list). `{x}` is one path segment, `{*x}` the
rest of the path.

| Module | Routes |
|---|---|
| core | `GET /` (eve on), `GET /_mcvs/status`, `GET /_mcvs/routes` |
| eve | `GET config/{clientId}/datacenters`, `GET config/{clientId}/datacenters/{dc}/urls` |
| pandora | `GET locate/{service}`, `GET locate?service=`, `* <service>/{first}/{*rest}` for 27 known service names (prefix aliases) |
| auth | `authorize` (GET/POST), `encrypt_token`, `verify`, `users`, `users/{credential}`, `users/{credential}/compliance`, `users/{credential}/games/{clientId}`, `users/me/credentials`, `users/me/transfer_code`, `devices/mydevice`, `games/{game}/alias[/{alias}]` |
| storage | `profiles/{credential}/myprofile[/{*selector}]` (GET/POST/DELETE), `.../myprofile/visibility`, `profiles/{credential}/{name}/backups[/{id}]`, `GET profiles` (batch), `profiles/location/{credential}`, `profiles/matchers/{name}/matches`, `data[/{credential}/{*key}]`, `_mcvs/storage[/profiles/{account}]` |
| crm | `GET configs/users/me[/{*selector}]`, `GET configs/users/{user}[/{*selector}]`, `GET _mcvs/crm`, `POST _mcvs/crm/reload` |
| matchmaker | `GET lobbyconfigs/{clientId}/{serverType}`, `GET rooms/{clientId}`, `POST rooms/{clientId}/quick_launch`, `POST rooms/{clientId}/automatch/{automatcher}` |
| assets | `GET coupons/{clientId}/{code}`, `GET assets/{clientId}[/{*name}]` |
| social | `accounts/me` (profile, status line, connections, requests), `accounts/{credential}`, `events`, `events/{id}`, `events/{id}/participants/{credential}` |
| messaging | `messages/{transport}/{credential}` (GET/POST/DELETE), `messages/{transport}/multicast`, `transports/{transport}/endpoints/...` |
| alerts | `GET alerts/me` (event stream), `POST alerts/{receiver}` |
| leaderboards | `leaderboards/{sort}/{name}` (top, `me`, `me/friends`, `me/location`, `{credential}`, `settings`, delete) |
| gamestats | `GET gamestats/users/queries/{query}`, `POST gamestats/users/{credential}/events/{kind}` |
| profanity | `chat/words` (filter, admin words) |
| chat | `chat/rooms/{name}/...`, `chat/channels/{name}/...` (subscribe, listen stream, members, invite, kick/mute) |
| clans | `groups`, `groups/{id}` (members, fields, delete, to_clan), `groups/config`, `groups/categories/{category}/find/{kind}` |
| wall | `{accounts,events,game_objects,groups}/{id}/wall` |
| bantool | `api/action`, `api/asset`, `api/campaign`, `api/reason` |
| geo | `GET location`, `GET location/{ip}` (and `location/?ip=`), `GET dump/dump.edgelist` |
| unlocks | `GET _mcvs/unlocks`, `POST _mcvs/unlocks/grant` |
| admin | the settings page: `GET _mcvs/ui[/\|/index.html]`, `POST _mcvs/ui` (post mode); under `_mcvs/api/v1/`: `session` (POST/DELETE), `ping`, `status`, `settings` (GET/POST), `settings/reset`, `settings/file`, `settings/repair`, `codes`, `clipboard`, `actions/{save-now,restart,crm-reload,unlock-now}`; `* _mcvs/api/{*rest}` (404) |
| admin-logs | `_mcvs/api/v1/logs`, `logs/{name}`, `logs/zip`, `logs/share`, `logs/share-text`, `logs/bundle` |
| admin-saves | `_mcvs/api/v1/saves`, `saves/import/{files,paste,validate,stage}`, `saves/staged` (GET/DELETE), `saves/staged/undo`, `saves/{account}/backups[/{id}]`, `saves/{account}/{restore,relink,export}` |
| misc | `POST /`, `ets`, `etsv2`, `tracking` (+ subpaths), `* redir/{*rest}`, `* ios/{*rest}`, `POST api/index.php`; the array-aware unknown-endpoint fallback |

## Settings by module

Shared keys (`port`, `bind`, `publicUrl`, `lanMode`, `unlockAll`, `devMenu`, `instantBots`, `economy.*`,
`superCredential`, `superPassword`, `dataCenter`, `logging.*`) are in `server/Core/Settings.cs` and the README. Module
keys, with defaults and meaning in each protocol note:

| Module | Keys |
|---|---|
| pandora / auth | `pandora.urls.<service>`, `auth.requireSuperPassword`, `auth.transferCodeHours` |
| storage | `storage.backups.keep`, `storage.backups.intervalMinutes`, `storage.autoRestoreCorrupt`, `storage.absentField`, `storage.cache` |
| crm | `crm.usePreconfiguration`, `crm.dataPackDir`, `crm.overlay`, `crm.offlineStore`, `crm.iap`, `crm.iapLanguage`, `crm.matchmaking`, `crm.redeemCoupons`, `crm.skipAgeGate`, `crm.developerIpRange`, `crm.keepAdPointcuts`, `crm.dailyCapMultiplier`, `crm.documentWaitMs`, `crm.selectorWaitMs`, `crm.omitOwnedSignatures` |
| matchmaker | `matchmaker.passDataCenter`, `matchmaker.tsArgs`, `matchmaker.gsArgs`, `matchmaker.tsRoomCapacity`, `matchmaker.gsRoomCapacity` |
| assets | `assets.couponsEnabled`, `assets.coupons` |
| misc | `alerts.streamSeconds`, `alerts.keepAliveSeconds`, `alerts.replaySeconds`, `chat.streamSeconds`, `chat.keepAliveSeconds`, `chat.historySize`, `messaging.maxMessagesPerMailbox`, `leaderboards.groupSize`, `leaderboards.virtualPlayers`, `leaderboards.virtualTopScore`, `wall.maxPostsPerWall`, `geo.country`, `tracking.disable` |
| unlocks | `unlocks.batchSize`, `unlocks.batchDelaySeconds`, `unlocks.startDelaySeconds`, `unlocks.streamWaitSeconds` |
| admin (settings page) | `admin.enabled`, `admin.redeemCode`, `admin.openVia`, `admin.restartVia`, `crm.newsfeedLink`, `security.hostCheck` ([design/settings-page.md](design/settings-page.md) §3) |
| embedded host | `embedded.manageArgs`, `embedded.lanAdvertiseHost` ([PLAYER-SETUP.md](PLAYER-SETUP.md)) |
