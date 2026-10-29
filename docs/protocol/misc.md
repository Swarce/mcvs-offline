# Protocol notes: MISC

Owner group: **MISC** (Social, Messaging, Alerts, Leaderboards, GameStats, Profanity, Chat, Clans, Wall, BanTool, Geo, Misc).
Scope: every Federation service not owned by AUTH, STORAGE or CONTENT, the hard-coded geolocation host, and the
catch-all module.
Evidence: report 02 §1.4-1.6, §4, §6, §9; report 04 §9-10; verify-C2 §1.5, §1.8, §3; verify-C5 (b); live ops: [LIVEOPS.md](../LIVEOPS.md). Paths in the
code comments (`OP/`, `EN/`, `GM/`, `UT/`) are relative to the decompiled assemblies, as in report 02.

All services are served at the host root (ARCHITECTURE §2.2). Tokens are resolved with the core `TokenService`;
the account a request acts for is the token's subject, so the TS's on-behalf (for_username) session acts for the
player. Code: `server/Services/<Module>/`; shared helpers in `server/Services/Misc/Fed.cs`. Tests:
`tests/McvsOffline.Tests/Misc/`.

## Status codes

| Situation | Answer | Why |
|---|---|---|
| Missing or unknown `access_token` where the caller matters ("me" paths, posts, streams) | **401** `{"error":...}` | With an `access_token` parameter the client drops its tokens, re-authorizes and retries once (report 02 §1.6). Unknown tokens only happen after `tokens.json` was lost. |
| An object that does not exist (clan, event, field never set, unresolvable credential) | **404** | Every caller treats it as "absent" (clan field default, `GetSocialProfile` → null, ...). |
| A request the client could never use (a gift without a body/gift_type, a score that is not an integer, a clan without a name) | **400** | Caught by the callers; storing it would leave an unconsumable message forever. |
| **403** | never | 403 means "upgrade required" / ban / profile wipe to the client. |
| Everything else | **200** with a JSON body (`{}` or `[]` at least) | An empty body parses as null and most callers throw. |

## Endpoints

### Social / Osiris (`server/Services/Social/`)

| Method | Path template | Route name | Response | Evidence |
|---|---|---|---|---|
| GET | `events?event_ids=a,b&language=` | `social.events` | **array** of events, request order, unknown ids left out (`[]` when none) | report 02 S2, verify-C2 §1.8 |
| GET | `events/{id}` | `social.event` | event object, 404 if unknown | S1 |
| POST | `events/{id}/participants/{credential}` | `social.event.participate` | `{}` | S4 (no caller in the game) |
| POST | `events/{id}/participants/{credential}/award` | `social.event.award` | `{}` | S3 (no caller) |
| DELETE | `events/{id}/participants/{credential}` | `social.event.leave` | `{}` | S5 (no caller) |
| GET | `_mcvs/liveops` | `social.liveops.debug` | the live-ops schedule now (season, day, modifier, CRM expiry, listed ids, objectives); no player data | ours, [LIVEOPS.md](../LIVEOPS.md) |
| GET | `accounts/me/connections/{type}` | `social.connections.list` | `[]` | S7 |
| POST | `accounts/me/connections/{type}` | `social.connections.add` | `{}` (parsed as an empty SocialRequestObject) | S6 |
| GET | `accounts/me/connections/{type}/count` | `social.connections.count` | `{"count":0}` | S8 |
| POST | `accounts/me/connections/{type}/import` | `social.connections.import` | `{}` | S9 |
| POST | `accounts/me/connections/{type}/{credential}/delete` | `social.connections.delete` | `{}` | S13 |
| GET | `accounts/me/requests` | `social.requests.list` | `[]` | S10 |
| GET | `accounts/me/requests/sent` | `social.requests.sent` | `[]` | S11 |
| POST | `accounts/me/requests/sent/{id}/cancel` | `social.requests.cancel` | `{}` | S12 |
| POST | `accounts/me/requests/{id}/{accept\|reject\|ignore}` | `social.requests.answer` | `{}` | S14 |
| GET | `accounts/{credential}` | `social.profile.get` | `{"credential":"fed_id:…","name","groups":[clan ids],"language","country","status_line"}`; 404 if the credential names no account | SP1 |
| POST | `accounts/me` | `social.profile.update` | `{}` (stores name/language/country) | SP2 |
| POST | `accounts/me/status_line` | `social.profile.status` | `{}` (≤ 250 chars) | SP3 |

### Messaging / Hermes (`server/Services/Messaging/`)

| Method | Path template | Route name | Response | Evidence |
|---|---|---|---|---|
| GET | `messages/{transport}/{credential}` (`me`) `[?delete=delete]` | `messaging.retrieve` | **array** of messages, oldest first; with `delete=delete` they are removed | M1 |
| POST | `messages/{transport}/{credential}` | `messaging.post` | `{"id":…}` | M2 |
| POST | `messages/{transport}/multicast` | `messaging.multicast` | `{"count":n,"ids":[…]}` (`credentials` = JSON array) | M3 |
| DELETE | `messages/{transport}/{credential}` | `messaging.delete` | `{"deleted":n}`; `msgids=a,b` in the body, all messages when absent | M6 |
| POST | `transports/{transport}/endpoints/{*endpoint}` | `messaging.endpoint.register` | `{}` (the raw push token, unescaped) | M4 |
| POST | `transports/{transport}/endpoints/delete` | `messaging.endpoint.delete` | `{}` | M5 |

A stored message is exactly what the client reads (`OP/Messaging.cs`): `id` (new 24-hex id), `from` (the custom
`from` field, else the sender's fed id), `body`, `created` (the custom `created` if it parses, else now: the client
`DateTime.Parse`s it and drops the message without it), `type` (`gift` or a custom type), `attachment`, and every
custom field as sent (`title`, `gift_type`, `expirationDate`, `roomId`, `quantityPaid`, `localizations`, …).
Delivery controls (`delay`, `alert_kairos`, `message_label`, `replace_matching_label`, `rollover`,
`rate_limit_count`, `credentials`) are not stored on the message.

### Alerts / Kairos (`server/Services/Alerts/`)

| Method | Path template | Route name | Response | Evidence |
|---|---|---|---|---|
| GET | `alerts/me?push_method=streaming&alert_types=…&content_type=event-stream[&targets=…]` | `alerts.listen` | `text/event-stream`, chunked: `: comment` lines, `data: {json}` per alert | A1 |
| POST | `alerts/{receiver}` | `alerts.send` | `{"delivered":n}` (`alert=<json>`) | A2 (unused by the game) |

### Leaderboards / Olympus (`server/Services/Leaderboards/`)

| Method | Path template | Route name | Response | Evidence |
|---|---|---|---|---|
| POST | `leaderboards/{sort}/{name}/me` | `leaderboards.me.post` | `{}` | L1 |
| POST | `leaderboards/{sort}/{name}/{entry}` | `leaderboards.entry.post` | `{}` | L9 |
| POST | `leaderboards/{sort}/{name}/me/delete` | `leaderboards.me.delete` | `{}` | L8 |
| GET | `leaderboards/{sort}/{name}?offset&limit[&for_entry_id]` | `leaderboards.top` | LeaderboardData; `limit=0` = the caller's own entry only (SelfData) | L2 |
| GET | `leaderboards/{sort}/{name}/me/location?…[&around_me=True]` | `leaderboards.location` | LeaderboardData (offline the local board is the global board) | L3 |
| GET | `leaderboards/{sort}/{name}/me/friends?…` | `leaderboards.friends` | LeaderboardData with only the caller (no friends offline) | L4 |
| GET | `leaderboards/{sort}/{name}/{credential\|me}?offset&limit` | `leaderboards.around` | LeaderboardData, a window of `limit` entries around that entry | L5, L10 |
| DELETE | `leaderboards/{sort}/{name}/{credential}` | `leaderboards.entry.delete` | `{}` | L11 |
| POST | `leaderboards/{sort}/{name}/settings` | `leaderboards.settings.set` | `{}` (`type`, `group_size`, `expiration_duration`) | L6 |
| GET | `leaderboards/{sort}/{name}/settings` | `leaderboards.settings.get` | `{"container_type","group_size"}` | L7 |

LeaderboardData = `{"data":[entry…],"total_entries":n,"my_entry":entry or {}}`; entry =
`{"display_name","credential","index","rank","score", …custom fields}` (`_level`, `_avatar`, `_leagueId`,
`_fed_id`, `_alias`, `_killSignature*`, `_clan*`, or the "global" board's plain keys).

### Clans / Osiris groups (`server/Services/Clans/`)

| Method | Path template | Route name | Response | Evidence |
|---|---|---|---|---|
| POST | `groups` | `clans.create` | Clan (400 without a name) | K1 |
| GET | `groups/{id}` | `clans.get` | Clan, 404 if unknown | K5 |
| POST | `groups/{id}` | `clans.update` | Clan (`promote_owners`/`demote_owners` JSON arrays) | K2 |
| POST | `groups/{id}/delete` | `clans.delete` | `{}` | K3 |
| POST | `groups/{id}/to_clan` | `clans.to_clan` | Clan | K4 |
| GET / POST | `groups/config` | `clans.config.get` / `.set` | `{}` | K6 |
| GET | `groups/{id}/fields/{field}` | `clans.field.get` | the JSON value, **404 when never set** (client default) | K7 |
| POST | `groups/{id}/fields/{field}` | `clans.field.set` | Clan (`object=…`, `operation=inc\|dec`) | K8 |
| GET | `groups?category&limit&offset[&random]` | `clans.search` | **array** of Clan | K9 |
| GET | `groups/categories/{cat}/find/{matchmaking_score\|recommendation_score\|keyword}` | `clans.find` | **array** of Clan (`keywords` must all appear in name/description) | K10-K12 |
| POST | `groups/{id}/members` | `clans.members.add` | `credential=me`: joins at once, 200 + Clan; another credential: `{"group":Clan}` (invitation, no effect) | K13 |
| POST | `groups/{id}/members/{credential}/delete` | `clans.members.remove` | Clan as it is after the removal | K14 |
| GET | `groups/{id}/members?limit&offset&owners=True\|False` | `clans.members` | **array** of ClanMember | K15 |
| GET | `groups/{id}/members/{credential}` | `clans.member` | ClanMember, 404 if not a member | K16 |
| POST | `groups/{id}/members/{credential}` | `clans.member.fields` | `{}` (`_field=_json_:<json>`) | K17 |

Clan = `{"id","name","description","category","type","visibility","membership","member_count","member_limit",
"score","currency","inventory","avatar","matchmaking_score","recommendation_score","recommendation_filter",
"days_offline_before_demotion","edit_permissions","participations","owner","owners","members", …"_custom"}`;
ClanMember = `{"credential":"fed_id:…","fed_id":"fed_id:…","name","online","status_line","game","avatar",
"seconds_since_last_status_change", …"_score","_rank"}` (`OP/Clan.cs`, `OP/ClanMember.cs`).

### Wall (`server/Services/Wall/`)

| Method | Path template | Route name | Response | Evidence |
|---|---|---|---|---|
| POST | `{groups\|accounts\|events\|game_objects}/{id}/wall` | `wall.<type>.post` | `{}` | W1 |
| GET | `{groups\|accounts\|events\|game_objects}/{id}/wall?sort_type&language&limit` | `wall.<type>.view` | **array**, newest first (`popularity`: by votes) | W2 |

Post = `{"id","type":"game_post\|user_post","text","actor":{"credential","nickname","avatar"},"creation",
"target":{…}\|null,"game","language","votes":0}` (`OP/Wall.cs`).

### Group chat (`server/Services/Chat/`)

| Method | Path template | Route name | Response | Evidence |
|---|---|---|---|---|
| POST | `chat/{channels\|rooms}/{name}/subscribe` | `chat.<kind>.subscribe` | `{"cmd_url":"<base>chat/<kind>/<name>/","listen_url":"<cmd_url>listen","status":""\|"kick"\|"mute",…}` | C1, C2 |
| GET | `chat/{kind}/{name}/listen` | `chat.<kind>.listen` | stream, one JSON object per line: `room_info`, recent history, messages, blank keep-alives | C3 |
| POST | `chat/{kind}/{name}` (= cmd_url) | `chat.<kind>.send` | `{}` (`msg`, `user`, `message_type`, `_custom`) | C4 |
| POST | `chat/{kind}/{name}/members/{credential}` | `chat.<kind>.private` | `{}` (to that member and the sender) | C5 |
| POST | `chat/{kind}/{name}/invite` | `chat.<kind>.invite` | `{}` | C6 |
| GET | `chat/{kind}/{name}/members` | `chat.<kind>.members` | array `{"credential","nickname","status"}` | C7 |
| POST | `chat/{kind}/{name}/members/{credential}/{kick\|mute\|unkick\|unmute}` | `chat.<kind>.moderate` | `{}` (`limit` = seconds) | C8, C9 |
| GET | `chat/rooms`, `chat/channels` | `chat.<kind>.list` | `[]` | C10 |

Message line = `{"type":"message\|game","id","msg","sender":{ChatUser},"sent","moderator":false,…"_custom"}`.

### Profanity (`server/Services/Profanity/`)

| Method | Path template | Route name | Response | Evidence |
|---|---|---|---|---|
| POST | `chat/words/filter`, `chat/words/{lang}/filter` | `profanity.filter[.lang]` | `{"matches":false,"result":<message>}` | P1 (player names, clan names, room names) |
| GET | `chat/words` | `profanity.words` | `{"all":{"blacklisted":[],"whitelisted":[]},…}` | P2 |
| POST | `chat/words/{lang}/{word}` | `profanity.word.set` | `{}` (`bad=True\|False`) | P3 |
| POST | `chat/words/{lang}/{word}/delete` | `profanity.word.delete` | `{}` | P4 |

### Game stats, BanTool, geolocation, catch-all

| Method | Path template | Route name | Response | Evidence |
|---|---|---|---|---|
| GET | `gamestats/users/queries/{default\|game_stats_by_time}` | `gamestats.query` | `[]` | G1, G2 |
| POST | `gamestats/users/{credential}/events/{kind}` | `gamestats.event` | `{}` (logged at DEBUG) | G3 |
| POST | `api/campaign` | `bantool.campaign.create` | `{"id","accounts":[]}` | report 02 §4.11 (no caller) |
| GET | `api/campaign` | `bantool.campaign.list` | `{"campaigns":[],"page","total":0}` | " |
| GET | `api/action`, `api/asset`, `api/reason` | `bantool.*` | `[]` | " |
| GET | `location?ip=` (also `location/?ip=`, `location/{ip}`) | `geo.location[.path]` | `{"ip","country_info":{"iso2":"US","iso3","name"}}` | `UT/Geolocalization.cs:93-105`, verify-C2 §1.5 |
| GET | `dump/dump.edgelist` | `geo.edgelist` | empty text | `UT/Geolocalization.cs` (read locally by the game) |
| POST | `` (root), `ets`, `etsv2`, `tracking` (+ `/{*rest}`) | `misc.tracking[.root]` | `{}` 200 (or 993 with `tracking.disable`) | `EN/EtsRequestMessage.cs`, `EN/TrackingComponent.cs` |
| any | `redir/{*rest}` | `misc.marketing` | 200 HTML "Not available offline" | `EN/TrackingHitsComponent.cs`, `EN/BrowserComponent.cs` |
| GET/HEAD | `favicon.ico` | `misc.favicon` | 204, request log at DEBUG | the Steam client and the overlay browser on the offline page (WR2 F11) |
| any | `ios/{*rest}` | `misc.gameportal` | `{}` | report 04 §12 (no caller) |
| POST | `api/index.php` | `misc.chat.report` | `{}` | `OP/ChatApi.cs:80` (cmt.gameloft.com) |
| any | `online_connectivity/connectionstatus` | `misc.connectivity` | **always** `{}` 200; one INFO line `connectivity: type=<t> status=<s> details="<≤100 chars>"` | native `ConnectivityTrackingManager` (`NativeEngine.dll`, `libnativeengine.so`); see "Connectivity tracker" below |
| * | anything else | `unknown` | core fallback (logged in full + `logs/unknown-endpoints.jsonl`), answered `{}`, or `[]` for list-shaped GETs | report 02 §6 |

All catch-all routes use `Router.CatchAllOrder`, so any module's specific route wins.

### Connectivity tracker (`server/Services/Misc/ConnectivityTracker.cs`)

The native `ConnectivityTrackingManager` (in `NativeEngine.dll` on PC and in every Android `libnativeengine.so`)
locates `online_connectivity` through pandora (`ServiceUrls.NativeServices`, so the locate answers
`<base>/online_connectivity/` without the "unknown service" warning) and posts one form per tracked event to
`/online_connectivity/connectionstatus`, User-Agent `connectivity_tracker/ GlWebTools/2.0 / ()`:

```
type=<auth|sp|gs|chat|shop|matchmaking|lobby|data|mpmatch>&status=<0|1>&timestamp=<YYYY-MM-DD HH:MM:SS, UTC>
&connectionstatus=<details, at most 100 chars>&domain=<datacenter>&client_id=<client id>[&port=<n, only when > 0>]
&device_id=<on PC: the machine's MAC address>
```

* The body may start with `&`; `port` is left out when 0; only `type=auth` ↔ the login tracker is proven (the PC spike
  saw `type=auth status=1 connectionstatus=` after each failed reconnect), so any `type` is accepted.
* **Why always 200.** The tracker never reads the answer: 200 ends the event, a 5xx or a 3 s timeout makes it retry,
  any other status drops it. The handler catches everything and answers `200 {}` even when it fails itself (a 500
  from the router would only produce retries). It logs one INFO line
  `connectivity: type=<t> status=<s> details="<≤100 chars>"` and never the `device_id`.
* **Privacy.** On PC `device_id` is a MAC address. `LogRedaction` replaces MAC-shaped `device_id`/`global_device_id`
  values with `<redacted-mac>` in every request log line and `unknown-endpoints.jsonl` entry, and SteamID64s (the PC
  anonymous user name) keep only their first and last 4 digits (docs/design/pc-port.md §4.2).
* **When it stops.** The tracker is part of the native online SDK; with the `nativeonline` patch and
  `embedded.nativeOnlineSdk: false` (the defaults) the SDK is not initialised and these posts should not appear at
  all (to be confirmed on PC in WR2, design Q8). The route stays either way: it is shared code and costs nothing.

## Decisions

1. **Coupon rewards do not go through Hermes.** `PlayerProfileComponent.PushCouponDataToInbox` (`GM/PlayerProfileComponent.cs:3524-3545`)
   builds the inbox message on the TS and adds it to the profile's `InboxMessages`, which the TS saves through storage
   (STORAGE owns that). Hermes carries what the *game server* sends to a player: live-tournament rewards and tokens
   (`GM/RewardComponent.cs:88-160`), CRM `give_bundle_secured`/`give_object_secured` gifts
   (`EN/MessagingSessionComponent.cs`), and `multipleConnectionDetected` notices (`GM/GameServerComponent.cs:1136-1150`).
   The TS reads `secured` and `inbox` when the player's session starts and on every `{"type":"message"}` alert,
   converts rewards into profile inbox messages and deletes what it processed (`EN/MessagingTransport.cs`,
   `GM/InboxComponent.cs:239-292`). So the server keeps a real, persistent per-account mailbox
   (`<data>/messages/<account>.<transport>.json`).
2. **`replace_matching_label`** replaces the mailbox's older messages with the same `message_label`: the TS posts a
   `multipleConnectionDetected` notice with that flag on every login whose profile is still locked (after a crash).
   The notice names the *old* room id; the new TS ignores it (`GameServerComponent.InboxComponent_MultipleConnectionDetected`
   compares room ids, and LocalAnubis room ids are fresh GUIDs, `LA/Lobby.cs:185-188`).
3. **Gifts the client cannot parse are refused (400)**: a `type=gift` message needs a JSON-object body
   (`{"gifts":[{"name","value"}],"message"}`) or a `gift_type`, otherwise the client throws `CorruptedGiftException`
   and would skip it forever. Push transports (`apns`, `gcm`, `mpns`, `wns`, and `any`, used by the clan join-request
   notifications of `GM/GameClanComponent.cs PostMultiplePNMessages`) are accepted and dropped.
4. **Kairos streams are held open, never ended quickly.** The client re-requests immediately when a 200 stream ends
   (`OP/AlertListener.cs Listen`: a fast-ending stream is a busy loop). A stream stays open for `alerts.streamSeconds`
   (600 s), writes `: keep-alive` comments every `alerts.keepAliveSeconds` (20 s; also detects dead clients) and then
   ends normally, so a listener costs one request per 10 minutes. Alerts are pushed as they happen (a message post
   with `alert_kairos=True` reaches the TS at once). Alerts published in the short gap while a listener reconnects
   are kept for `alerts.replaySeconds` (120 s) and replayed to the listener that returns with the same token and
   parameters.
5. **`alert_types` is honoured and `connection` alerts are never sent.** The TS's per-connection listener asks only
   for `connection` and kicks the player on one (`EN/Connection.cs:576-584,1610-1616`); it therefore idles forever.
   `targets` work as in Kairos: clan members listen to `group:<clanId>` (`EN/ClanComponent.cs ResetAlertTargets`),
   and clan wall posts alert that target.
6. **Leaderboards are completed to a league group of 100 with deterministic rivals** (`VirtualPlayers.cs`). At
   season end the TS promotes/demotes from `my_entry.rank` and `total_entries` (`GM/DivisionsComponent.cs:364-425`,
   `TieredDivision.GetStatusForRank`): promoted if rank ≤ ⌊PromotionZone% × total⌋, demoted if
   rank ≥ total − ⌊DemotionZone% × total⌋ + 1. Alone on a board (1 of 1) nobody is ever promoted (⌊40% × 1⌋ = 0).
   Olympus put players in league groups of 100 (`CreateLeague(groupSize: 100)`), so descending boards get
   `groupSize − real entries` rivals. Rival k of n scores `topScore × ((n − k)/(n + 1))² × (0.9…1.1)`
   (`leaderboards.virtualTopScore`, default 400): ~36 % of topScore reaches the top 40 %, ~64 % the top 20 %,
   ~81 % the top 10 %; only a near-zero score is demoted. The game posts its season division trophies with
   `replace_score_if` absent (always replace). Rivals are derived from the board name (same board, same rivals),
   rank behind real players on ties, carry `_mcvs_bot: true`, and appear in Top/AroundMe lists so the UI shows a
   populated board. Not completed: ascending boards, boards in `leaderboards.virtualExclude` (default `["global"]`,
   the TS's user registry board that `RegisterUser` posts epoch seconds to), or everything with
   `leaderboards.virtualPlayers=false`. A board's `POST …/settings group_size` overrides the group size.
   Division boards are named by the season event's `tournament.leaderboard.name`; because the TS sees a loopback
   player as a developer/QA (127.0.0.0/8 is always in the edge list, verify-C2 §1.5), `DivisionData.LeaderboardId`
   turns "Leaderboard" into "Leaderboard_QA" in that name for both posting and reading. Any name works here.
   **Season boards** (`mcvs_division_<season>_d<nn>_Leaderboard[_QA]`, the names live ops gives its season events)
   get `SeasonRivals` instead: calibrated so that promotion needs a set number of matches a day for that division
   at the player's own `_level`, growing over the season and frozen after its end, which is what the TS reads at
   the next season start (docs/LIVEOPS.md §5).
7. **An empty `my_entry` is `{}`**: `Leaderboard.LeaderboardData` reads `Value<JObject>("my_entry") ?? new JObject()`,
   and an entry without a valid credential makes `LeaderboardsComponent` post the player's score and ask again
   (`PostUserIfNotFound`). `limit=0` (SelfData/SelfEntry) returns the caller's entry in `data` too.
8. **Social events are served from a provider, then from content; the shapes are made parser-safe.** The ids come
   from CRM; without TLE ids in CRM the TS never asks (verify-C2 §1.8). Definitions come from an
   `ISocialEventProvider` registered in `context.Features` — by default **`LiveOpsService`**
   (`server/Services/Social/LiveOps/`, docs/LIVEOPS.md): weekly division seasons, daily missions and weekly
   achievement events generated from the clock, whose ids, expiry and weekly modifier it also hands to the CRM
   builder — or from the content file `social/events.json` (an array, or
   `{"events":[…]}`, or `{"<id>":{…}}`; built in from `server/Content/`, overridable in `<data>/content/`). An event
   the TS cannot parse would abort `TimeLimitedEventsComponent.RetrieveEventsAsync` with a non-online exception and
   the menu would wait forever for its events, so every event is normalized: `_template` must be a JSON *string*
   (objects are serialized; events without one are dropped), `_version` must be an integer (else removed),
   top-level nulls become `""` (image fields go through `DownloadableAsset(string).Trim()`), and the template must
   survive the TS's SEM parser (`TleTemplateValidator`: unknown or non-string `type`, a non-object `sub`, scalar
   time/date values, non-integer `duration`/consumable `value`, non-boolean `consumable`, … and a
   `DivisionSeasonEvent` `_participation` without an integer division), otherwise the event is dropped with a
   warning. The validator is checked against the game's own `ParseTemplate` (`ClientConformanceTests`). Missing or
   non-ISO dates are logged (the TS reads them as `DateTime.MinValue`). **Event dates should not use the ISO `T`**:
   SocialEvent reads them through `JValue.ToString()`, i.e. in the phone's culture once Newtonsoft has turned a
   `yyyy-MM-ddT…` string into a DateTime, and a day-first locale swaps day and month (`2026-10-05T00:00:00Z` → 10 May);
   live ops send `"2026-10-05 00:00:00Z"`, which stays a string (`LiveOpsConformanceTests`). Any parseable
   `start_date`/`end_date` (from a provider or `social/events.json`) is rewritten to that form when served, so a
   content file written with ISO dates is safe too.
9. **Clans are a real local store** rather than "always not in a clan": the game writes the clan id into the profile
   itself after `POST groups` / a successful join (`EN/ClanComponent.cs SetClanInProfile`), so a player can create
   and run a solo clan offline. Joining is immediate (200) whatever the membership mode, since nobody can approve a
   request. Invitations answer `{"group":Clan}` without effect. The last member leaving deletes the clan; when the
   last owner leaves, the next member becomes owner. Clan fields (`groups/{id}/fields/{f}`) address plain clan
   properties (`score`, `currency`, …) or custom fields; `_json_:` values are decoded.
10. **Chat streams are held open by default** (`chat.streamSeconds = 0`): the client counts every end of stream as a lost
    connection and backs off up to 120 s before subscribing again (`EN/ClientChatComponent.cs:300-316`). Blank lines
    keep the connection alive; a stream that does end sends `{"type":"disconnect","reason":"Idle channel|Idle room"}`
    first. Chat state is in memory only.
11. **Profanity is a pass-through** unless words are configured (`profanity.blacklist` or the admin endpoints): the
    player name check is on the critical path and an error aborts the name change.
12. **Geolocation answers at once** with `geo.country` (default `US`): the TS handshake waits for it inside its 60 s
    window with HttpClient's 100 s default timeout (verify-C2 §1.5). Reached only when the patcher's `geo` patch
    points `http://ip.ecomapis.gameloft.org/location/` here; `US` is not in any datacenter-validation list.
13. **game_stats answers "no data"** (`[]`): it only feeds lobby-time estimates, and offline lobbies are filled with
    bots at once (CRM `MinPlayers 1`, verify-C4).
14. **The client's HTTP stack has no per-host connection limit.** `Federation` uses `new HttpClientHandler()`; with
    `XA_HTTP_CLIENT_HANDLER_TYPE=System.Net.Http.HttpClientHandler` the shipped `System.Net.Http.dll` picks
    `SocketsHttpHandler` (only `MonoWebRequestHandler` values select the HttpWebRequest path), whose
    `MaxConnectionsPerServer` is unlimited. Several long-lived alert/chat streams to the one Federation host can
    therefore not starve the other requests. (Mono's ServicePoint limit of 10 would only apply to HttpWebRequest.)
15. **Tracking is acknowledged with 200**, the only status the TS treats as delivered. `tracking.disable` answers ETS's
    own `993` "blocked, disable tracking" code instead, after which the TS stops sending its queued tracking
    (`EN/TrackingComponent.cs:352-374`); off by default.
16. **Unknown endpoints stay visible.** The catch-all only adds known-harmless routes; the core fallback still logs
    every unknown request in full. For list-shaped GETs (last segment `events`, `groups`, `members`, `connections`,
    `requests`, `sent`, `wall`, `rooms`, `channels`, `friends`, `game_stats_by_time`, …, `…/find/…`,
    `messages/{t}/{x}`, `users?device_id=`) the answer becomes `[]` instead of `{}` (report 02 §6).

## Settings (`settings.json`, all optional)

| Key | Default | Meaning |
|---|---|---|
| `alerts.streamSeconds` | 600 | How long an alert stream stays open before ending normally (1…86400). |
| `alerts.keepAliveSeconds` | 20 | Idle keep-alive comment interval. |
| `alerts.replaySeconds` | 120 | How long alerts are kept for a reconnecting listener. |
| `messaging.maxMessagesPerMailbox` | 500 | Oldest messages are dropped beyond it. |
| `leaderboards.virtualPlayers` | true | Complete descending boards with rivals. |
| `leaderboards.groupSize` | 100 | Board size with rivals (a board's own `group_size` setting wins). |
| `leaderboards.virtualTopScore` | 400 | Score of the best rival (before ±10 % jitter). |
| `leaderboards.virtualExclude` | `["global"]` | Board names (or `prefix*`) never completed. |
| `chat.keepAliveSeconds` | 20 | Blank keep-alive line interval on chat streams. |
| `chat.streamSeconds` | 0 | 0 = hold until the client goes away. |
| `chat.historySize` | 50 | Recent messages sent to a new listener. |
| `wall.maxPostsPerWall` | 100 | Newest posts kept per wall. |
| `profanity.blacklist` | `[]` | Words masked by the filter (all languages). |
| `geo.country` | `"US"` | ISO 3166-1 alpha-2 country the geolocation reports. |
| `liveops.*` | see [LIVEOPS.md](../LIVEOPS.md) §3 | `enabled`, `seasonStart`, `seasonDays`, `dailyResetHour`, `divisionSeasons`, `dailyQuests`, `weeklyEvents`, `modifiers`, `difficulty`, `seasonRewardMultiplier`. |
| `tracking.disable` | false | Answer tracking with 993 so the TS stops sending it. |

## Cross-module seams

| Seam | Shape | Use |
|---|---|---|
| EventHub topic `alerts.push` (subscribed by Alerts) | `{"to": "fed_id:<id>" \| "<account id>" \| "group:<id>", "alert": {"type": …}}` | e.g. STORAGE/CONTENT sending `{"type":"set_profile"}` so the TS re-runs the IAP inventory diff (`EN/IapStoreComponent.cs:957-970`, verify-C5 (c)). `connection` alerts are dropped. |
| EventHub topic `messaging.post` (subscribed by Messaging) | `{"to", "transport": "secured", "body", "type"?, "alert": true, "label"?, "replaceMatchingLabel"?, "from"?, "fields": {…}?}` | Deliver a Hermes message (e.g. a gift) without referencing Messaging's files. |
| `context.Features` `ISocialEventProvider` (defined in `server/Services/Social/SocialEvents.cs`) | `FindEvent(id, language, utcNow)` → Osiris event object or null | Rolling TLEs; `LiveOpsService` registers itself unless another provider is set. |
| `context.Features` `LiveOps.LiveOpsService` (`LiveOpsService.For(context)`) | `Snapshot(utc)` (CRM ids, expiry, MUL cells, cache key), `TryGetSeasonBoard(name)` (rival tuning), `Describe(utc)` | Read by Crm (`CrmDocumentCache`) and Leaderboards (`LeaderboardStore`). |
| `context.Features` `AlertHub`, `Mailboxes`, `ClanStore`, `SocialProfiles`, `ChatHub` | the shared instances (`X.For(context)`) | In-process use by any module. |

## Verification

- `tests/McvsOffline.Tests/Misc/*Tests.cs`: behaviour of every endpoint through the composed server (in-process and,
  for the Kairos and chat streams, over real HTTP with chunked line-by-line reads), the hub semantics (type filters,
  targets, replay, the `connection` ban), ranking maths, normalization rules, persistence across a restart, and a
  guard that no MISC route takes another module's paths.
- `ClientConformanceTests`: loads the player's own `Gameloft.OnlinePlatform.dll` and its `Newtonsoft.Json` from
  `MCVS_REFS` at run time and drives the **real client classes** against the server: `Messaging.PostMessage` /
  `RetrieveMessages` / `DeleteMessages` (`GiftData` parsing), `AlertListener` receiving a `MessageAlert`,
  `Leaderboard.PostScore` / `SelfData` / `Top` / `AroundMe` / `Friends`, `SocialService.GetEvents` (`SocialEvent`),
  `SocialProfileService`, `ProfanityService.FilterMessage`, `ClanService.CreateClan` / `GetClan` /
  `IncrementClanField`, `Clan.SendNotification` / `GetLastAlerts` (wall), `ChatRoom.SendMessage` →
  `ChatMessageReceived`, and `game_stats_by_time`. (It runs the client on .NET 8, not Mono: the parsing code is the
  same, the HTTP stack is the same SocketsHttpHandler design.) It also runs every template of
  `TleTemplateValidatorTests` through the game's own `TimeLimitedEventsComponent.ParseTemplate` and requires the
  validator to drop exactly the ones that throw.
- `LiveOpsTests`: the schedule grid, ids, the CRM layer (ids, expiry ahead of the clock, modifier cells combined with
  the economy multipliers, the off switches), deterministic events, the season boards (exact promotion/demotion lines,
  growth and freeze, level scaling) and the division tables cut out of the design file (also the real one).
  `LiveOpsConformanceTests`: 16 days of generated CRM documents and events through the real client, down to each
  event type's `Initialize` (see LIVEOPS.md §9).

## Open questions / device-test checklist

- [ ] Alert stream: the TS's `AlertsComponent` receives `{"type":"message","transport":"secured"}` lines over the
      chunked `text/event-stream` with SocketsHttpHandler + `ResponseHeadersRead` (expected: yes, line by line).
- [ ] Logs show one `alerts/me` request per player per `alerts.streamSeconds` and per listener, never a tight loop.
- [ ] Live tournaments / CRM pointcut gifts: a gift posted to `secured` appears in the in-game inbox and is deleted
      from the mailbox (`DELETE messages/secured/me msgids=…`) after the TS processed it.
- [ ] After a forced kill, the next login's `multipleConnectionDetected` notice does not kick the player.
- [ ] Division season end (live ops, LIVEOPS.md §11): `SelfData` on the old `mcvs_division_…_Leaderboard_QA` board gives
      rank/total from a 100-entry board and promotion/demotion and the inbox reward follow; the division
      leaderboard UI shows the rivals and the player's row.
- [ ] Setting the player name passes through `chat/words/{lang}/filter`; `POST accounts/me` stores the name.
- [ ] Creating, renaming and leaving a solo clan; the clan wall shows the join notice; clan chat connects and
      messages round-trip.
- [ ] Geolocation (with the `geo` patch): the handshake does not stall; `GET location/?ip=127.0.0.1` is answered.
- [ ] Collect `logs/unknown-endpoints.jsonl` after a session: native SDK calls (GLUID/global id, Glads ads, ecomm)
      may show up there; none is expected to block.
