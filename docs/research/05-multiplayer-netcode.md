# 05 – Online multiplayer architecture, matchmaking and game-server netcode

Scope: how MCVS clients found and joined matches (matchmaker → lobby → controller → game server), the transports and
wire formats, the handshake and how a joining client is authenticated, the authoritative server (TS/GS) and its backend
needs, the replication/tick model, private matches and team rooms ("group play"), and what a replacement must provide
for offline, LAN and internet P2P play. LocalAnubis and P2P.cs internals belong to another analyst; this report only
shows where they plug in.

Paths are relative to `/home/user/mcvs-work`. Abbreviations:

* `EN/` = `decompiled/Gameloft.Engine/Gameloft.Engine/`
* `LA/` = `decompiled/Gameloft.Engine/Gameloft.Engine.LocalAnubis/`
* `OP/` = `decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/`
* `OPL/` = `decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform.Lobby/`
* `GM/` = `decompiled/Game/Game/`

Legend: **[V]** verified by reading the code or disassembly. **[L]** likely: a strong inference, not every link read.
**[S]** speculative.

---

## 0. TL;DR for the implementer

1. **Everything is server-authoritative, including the main menu.** Each client keeps up to three "links" (TCP
   connections): the **Transaction Server (TS, `ServerType "ts"`)** runs the menu and metagame, with the player's
   profile loaded server-side. The **QuickMatch** and **PrivateMatch** links go to a **Game Server (GS,
   `ServerType "gs"`)**. Both TS and GS are the same C# code (`GameServerComponent : ServerComponent`) running a
   headless `World`. The client is a "server slave": UI actions become RPCs to the TS. [V] (`GM/ServerComponentExtension.cs:7-15`,
   `GM/GameClientComponent.cs:2654-2683`, `EN/SynchronizationComponent.cs:458-468`, `EN/RemoteUtil.cs:7-43`)
2. **Join chain (online):** Matchmaker HTTP (`quick_launch` for TS; `automatch/{automatcher}` for GS, which the
   **TS calls for the client**) returns a Room JSON with `controller_host`/`controller_tcp_port`. The client opens a
   raw TCP connection to the **Anubis controller**, receives a `nonce`, sends `login` with
   `auth/encrypt_token(nonce)`, then sends `connect game {room_id}`. **That same TCP socket then becomes the game
   control channel** to the GS process, so the handshake runs over it. The client then opens UDP channels (ENet) to
   the GS. [V] (`EN/ClientLink.cs:257-283`, `EN/AnubisClientSocket.cs:34-84`, `EN/Connection.cs:949-1045`)
3. **Transports:** TCP carries two framings on one stream: Anubis newline-delimited JSON, and length-prefixed frames
   `0xBB, type(0=data,1=control,2=anubis), int32 LE length, body`. UDP is ENet (statically linked into
   `libnativeengine.so`, IPv6-capable address struct). It has 2 channels per connection: 0 = reliable,
   1 = unreliable. The server's ENet port is **ephemeral** (`StartServer(20 peers, port 0)`) and is announced in the
   `StartEnetSocket` control reply. The client connects ENet to the **IP of the TCP control socket's remote end**.
   [V] (`EN/MessageReader.cs:24-174`, `EN/Connection.cs:1347-1378, 983-1045`, `EN/EnetSocket.cs:275-330`)
4. **Auth model:** only the **Anubis lobby/controller** validates the client's token (encrypted access token plus
   nonce, checked with `auth/verify`). The game server **trusts** the `credential`/`fed_id` handed over with the
   socket. In the control `Handshake` it does not check any token. It opens a Federation `SessionForUser(credential)`
   with its own **super credential**, then loads that player's profile from storage. [V] (`EN/Connection.cs:1551-1608`,
   `EN/AuthenticationComponent.cs:576-600`, `LA/BaseServer.cs:406-429`)
5. **Dedicated servers were the same game build run headless** (`-Server -Headless -ServerType ts|gs -Slot ...
   -UnixDomainSocketPath ...`). The Anubis controller handed each accepted client socket to the game process: the
   native callback gives `(json{credential,fed_id,room_id,slot}, int fd)` and C# wraps it in `TcpNativeSocket(fd)`.
   One process hosts many rooms ("slots"), each in its own headless `World`. **On Android the native UDS server is a
   stub that returns -1.** The in-process path ("LocalServer", i.e. LocalAnubis) is fully present in C#.
   [V] (`EN/ServerManager.cs:46-243`; disasm `libnativeengine.so` 0x399ac8 → 0x3e2894 `mov w0,#-1; ret`)
6. **A single process can be server and client at once (a listen server).** With `ServerLocation.Embedded`, the
   client starts LocalAnubis (Lobby/Controller TCP + Finder HTTP on 51750) inside its own process. The controller's
   `connect game` creates headless server `World`s for the TS and/or GS in the same process. The client `World`
   connects to them over loopback TCP and ENet. This is the built-in "offline" mechanism. It **still needs a
   Federation backend** (auth/verify/encrypt_token/lobbyconfigs/storage/CRM; see report 02). [V] (`EN/ClientComponent.cs:987-1010`,
   `LA/Controller.cs:71-98`, `EN/ServerManager.cs:146-184`)
7. **Tick and replication:** fixed 20 Hz (`ConstantDeltaTime = 0.05 s`). The server sends a per-connection,
   delta-compressed snapshot (`Synchronize()`) each tick, on reliable and unreliable streams. RPCs are routed by
   `[Remote(caller, callee, reliable, guaranteed)]`. Movement and aiming are **client-authoritative**: the owner
   sends `CharacterRemoteState` every tick via an unreliable RPC. **Hit detection is client-side.** The client calls
   `ApplyFireDamage` on the server, and the server applies damage, health, match state, scoring and rewards.
   [V] (`EN/World.cs:1983`, `EN/SynchronizationComponent.cs:985-1011, 1941-1992`, `GM/CharacterSynchronizationComponent.cs:200-290`, `GM/WeaponComponent.cs:1920-1926`)
8. **Max players:** 8 per PvP room by default (`MaxPlayers = 8`, `MinPlayers = 2`, team = capacity/2). Private
   matches have capacity 8 plus spectators. Group-play team rooms hold 4. The ENet host accepts at most 20 peers. Bots
   fill empty slots on the server (`FillWithBots`/`SpawnBot`). [V] (`GM/GameMatchmakingComponent.cs:67-69, 751-805`,
   `GM/GameClientComponent.cs:148, 176`, `EN/Connection.cs:110, 1365`)
9. **Bot matches, practice and tutorial run client-side** (`ServerLocation.None` "offline links"). However, they are
   started from the TS link, and **bot-match results are reported to the TS** as a control message
   (`OfflineMatchAgainstBotsOver`). The TS applies the rewards. So offline play needs a TS, either remote or
   embedded. [V] (`GM/GameClientComponent.cs:3736-3756, 3973-4058`, `GM/OfflineBotsGameplayComponent.cs:375-570`)
10. **For P2P the hard part is not the netcode, it is profile ownership.** The GS loads every joining player's profile
    from *its* Federation storage, using that player's credential. At match end it writes XP, currency, MMR and
    rewards back to storage. The TS and GS hand the profile over through storage, using a lock field
    (`ProfileOwnerRoomId`) and `_mcvs.currentMatch`. A LAN or internet host therefore needs read/write access to each
    guest's profile. This means a federated storage stub (proxy to the guest's own stub) or a code patch. [V] (`GM/GameServerComponent.cs:694-768, 934-1116`,
    `GM/GameplayComponent.cs:765-929`)

---

## 1. Architecture

### 1.1 Text diagram (original online deployment)

```
                         HTTPS (Federation, see report 02)
   +------------------------------------------------------------------------------+
   |  eve -> pandora -> {auth, storage, matchmaker, lobby, alert, message, crm,     |
   |                     leaderboard, social, game_stats, config ...}              |
   +------------------------------------------------------------------------------+
        ^  ^  quick_launch / automatch / rooms? / lobbyconfigs (HTTP form/JSON)      ^
        |  |                                                                        | SessionForUser
        |  |   +--------------------- ANUBIS (Gameloft) ----------------------+     | storage / CRM /
        |  |   |  Matchmaker (HTTP)     Lobby (TCP, NL-JSON)   Controller (TCP)|     | alerts / lb ...
        |  |   |   rooms, automatchers   rooms, users,          "connect game" |     |
        |  |   |                         reservations           -> hands TCP fd |     |
        |  |   +----------------------------------------------------|---------+     |
        |  |                                                        | UDS: json + fd |
        |  |                     +----------------------------------v--------------+ |
        |  |                     |  Game-server process (same C# build, headless)  |-+
        |  |                     |  -Server -Headless -ServerType ts|gs -Slot ..    |
        |  |                     |  ServerManager: slot(room_id) -> headless World |
        |  |                     |    World[ts]: GameServerComponent(ServerType ts)|
        |  |                     |    World[gs]: GameServerComponent(ServerType gs)|
        |  |                     |  ENet host (UDP, ephemeral port, 20 peers)      |
        |  |                     +-------^-----------------------^-----------------+
        |  |                             | TCP control (0xBB frames)| UDP ENet ch0/ch1
   +----+--+-----------------------------+--------------------------+----------------+
   |  Client process (Android)                                                     |
   |   World (non-headless, Client.Enabled, "server slave")                         |
   |   GameClientComponent.Links: TransactionServer(ts) | QuickMatch(gs) |          |
   |                              PrivateMatch(gs) | ephemeral offline links        |
   |   Lobby (AnubisClientSocket) for private rooms / team rooms                     |
   +-------------------------------------------------------------------------------+
```

### 1.2 Embedded / listen-server deployment (built in, used for offline and dev)

```
  Client process
  +------------------------------------------------------------------------------+
  | LocalAnubisManager (C#):  Lobby TCP  (Host:LocalAnubisLobbyPort|ephemeral)   |
  |                           Controller TCP (ephemeral)                          |
  |                           Finder HTTP :51750  (/rooms/... quick_launch,       |
  |                                                automatch, /locate?service=)   |
  |       Controller "connect game" -> ServerManager.AddClientAsync(socket)       |
  |                   -> new World(headless, server) per room (TS world, GS world) |
  | Client World ---loopback TCP + ENet---> headless TS/GS Worlds (same process)   |
  +------------------------------------------------------------------------------+
         | still HTTPS to a Federation for: super session, auth/verify,
         | encrypt_token, lobbyconfigs, storage, CRM, alerts ... (report 02)
```

### 1.3 Roles and link model [V]

| Concept | Values | Evidence |
|---|---|---|
| `ClientComponent.ServerLocation` | `Auto, None (offline), Online, Remote, Embedded, SSH, Tunneling, P2P` | `EN/ClientComponent.cs:31-41` |
| `ClientComponent.AnubisCommand` | `None, QuickLaunch, Automatch, CreateRoom, JoinRoom, Reconnect` | `EN/ClientComponent.cs:21-29` |
| `ClientLink.Offline` | `ServerLocation == None` | `EN/ClientLink.cs:124` |
| `ClientSettings.OverrideAnubisHost` | true for any location except `Online/None/Auto`. Matchmaker calls then go to `http://{LocalAnubisFinderHost}:{LocalAnubisFinderPort}` (defaults: `LocalAnubisManager.PublicHost`, `51750`) instead of pandora | `EN/ClientSettings.cs:341-355` |
| Server types | `"ts"` (transaction/menu) and `"gs"` (gameplay) | `GM/ServerComponentExtension.cs:7-15` |
| Client links (template-driven) | `TransactionServerId`, `QuickMatchServerId`, `PrivateMatchServerId`, plus ephemeral links (`TrainingMatchWithBots`, `PracticeLevel_<merc>`, `OfflineLevel_Connected`, `OfflineLevel_Pending`, `P2P:<machine>`) | `GM/GameClientComponent.cs:141-143, 170-237, 2654-2683, 3736-3756, 4041-4058`; `EN/P2PRoom.cs:30-38` |
| Per-link ServerLocation | From template (`LinkSettingsInitializerComponent.LinkSettings.DefaultServerLocation`). `-ServerLocation <enum>` on the command line overrides **every** link that is `Auto` | `GM/LinkSettingsInitializerComponent.cs:28-52` |
| One link is "current" | Connecting a new link **pauses** the current one (`PauseCurrentLinkUponConnection`). Leaving gameplay resumes the TS | `EN/ClientComponent.cs:1095-1168`, `GM/GameplayComponent.cs:1326-1386` |

The link ids, `ServerType`, `AnubisCommand`, `Matcher`, `MatchmakingFilters` and `EnableReliable/UnreliableChannel`
are template data (`SceneComponents.tx` and level templates in the downloaded data pack). They are **not** in the APK.
`GameSynchronizationComponent` confirms that a link with `Settings.ServerType == "ts"` and an `InitialLevel` exists
(`GM/GameSynchronizationComponent.cs`, `OnLevelPathChanged`). [V]

---

## 2. Service tier APIs

### 2.1 Matchmaker (HTTP, `OnlineService.Matchmaker`; or `http://host:port` when `OverrideAnubisHost`) [V]

`OP/MatchmakerApi.cs:10-73`, `OP/MatchmakerService.cs:13-58`. Token placement and encoding are as in report 02
(GET: query, POST: form body).

| # | Method + path | Scope | Parameters | Response |
|---|---|---|---|---|
| M1 | `POST rooms/{clientId}/quick_launch` | `lobby` | `server_type` | Room JSON (object) |
| M2 | `POST rooms/{clientId}/automatch/{automatcher}` | `lobby` | `server_type`, `user` (RoomUser compact JSON), `filter` (a **url-encoded string** `k=v&k2=v2`), `default_room_kwargs` (JSON, filter values copied in), `timeout`, `score`, `score_min_range`, `score_max_range`, `score_automatch_enabled`, `fill`, `midgame_join`, `joins`, `instant`, `country`, `team_room_id`, `max_tags` (absent when null) | Room JSON |
| M3 | `GET rooms/{clientId}` | default | `server_type`, `limit`, plus each filter as its own query param (`id`, `_closed`, `privateMatch`, `reservation`, `_codeId`, `_privacy`, `randomPseudoFilter`, ...) | JSON **array** of Room |
| M4 | `GET lobbyconfigs/{clientId}/{serverType}` | `lobby_game_admin` | – | `{"game":{server_type,room_info_on_start,midgame_join,room_capacity,max_tags,room_timeout,room_launch_timeout,reconnect_grace_period,socket_timeout,cmd_args},"automatchers":{<k>:{"name":...,...}}}` (`LA/GameConfig.cs:11-51`) |
| M5 | `GET http://{finder}:51750/locate?service=lobby` | – | – | plain text `host:port` (`EN/ClientLink.cs:448-458`) |

**Filter value encoding** (`OP/RoomFilter.cs:67-113`, `OP/DefaultCustomPropertiesValueConverter.cs`). Equality sends
the value as is. `!value` means not-equal. `a,b,c` means OR (`RoomFilter.CreateInstance(list)`). `min-max` is a range.
`bool.ToString()` gives **`True`/`False`** (capitalised). Example `FindRooms` for the private-room browser:
`privateMatch=True&_started=False&reservation=_privateMatchPlayer&_privacy=open,clanOnly_<clanId>&randomPseudoFilter=!<ticks>`
(`GM/GameClientComponent.cs:1940-1982`, `EN/ClientLink.cs:416`).

Engine defaults for automatch (`EN/MatchmakingComponent.cs:64-272`): automatcher `"dev"` (the template probably
overrides it), `joins 3`, `fill 1`, `max_tags 1`, `score_max_range 100`, `score_automatch_enabled false`,
`midgame_join false`, `instant false`. The game adds the following (`GM/GameMatchmakingComponent.cs:1042-1103`):

* Filters: `_noob_<id>` = 1|0, `_team` = 1|0 (when `SplitGroupPlay`), `_closed=0`, `_matchOver=0`,
  `capacity=<MaxPlayers>`, `_arena=True|False` (from the client).
* `country` = profile region.
* `score` / `score_min_range` / `score_max_range` from the dynamic score range.
* RoomUser extras: `_level`, `_avatar`, `_trophies`, `_gentleTrophies`, `_deviation`, `_clan`, `_emblem`,
  `_dynamicScoreRangeEnabled`, `_pvpShiftingWindowEnabled`, `_score`, `_scoreDelta`, `_staticScoreRange`,
  `_winLossStreak`, `_dynamicScoreRange`, `_scoreRangePlayerDensity`, `_expectedLobbyTimeInSeconds`.
* For a team: `_teamRoomId`, `_teamMemberCount`, `team_room_id`.

`DoAutomatch` retries with `id=!badRoom1,badRoom2` until `AutomatchPostCondition` accepts the team split
(`EN/MatchmakingComponent.cs:361-381`, `GM/GameMatchmakingComponent.cs:950-997`).

### 2.2 Room / RoomUser / RoomInfo JSON [V]

`OP/Room.cs:33-339` (typed accessors), `OP/RoomUser.cs:26-105`, `OP/RoomInfo.cs` (id, lobbyHost, lobbyPort for UI lists).

```jsonc
// Room (fields read by the client; anything else is a free "attribute")
{ "id": "<room id>" /* or "room_id" */, "name": "...", "server_type": "gs", "client_id": "2620:66102:...",
  "capacity": 8, "members": [ RoomUser, ... ], "owner": RoomUser, "reservations": ["bot", "_privateMatchPlayer"],
  "tags": [], "full": false, "empty": false, "available_slots": 3, "member_count": 5,
  "game_started": true, "launch_date": "...", "created": "<ISO8601>", "timeout": 600, "launch_timeout": 0,
  "pid": 1234, "preferred_region": "...", "matchmake_score": 1200,
  "lobby_host": "x.x.x.x", "lobby_tcp_port": 1234,
  "controller_host": "x.x.x.x", "controller_tcp_port": 5678,
  // game attributes seen in code:
  "privateMatch": true, "_closed": 0, "_matchOver": 0, "_started": false, "_codeId": "7KQ2M...",
  "_privateMatchMapId": "...", "_privateMatchMapPath": "Levels/...", "_privateMatchType": "ControlPoint",
  "_rewardId": "...", "_tournamentRules": "...", "_privacy": "open|clanOnly_<id>|hidden", "_spectatorsAllowed": "...",
  "_maxNumberSpectators": 0, "_matchRoundsId": "...", "_maxNumberRounds": 1, "_numberRoundsToWin": 1,
  "_duplicatesAllowed": true, "_minMercsUnlocked": 0, "_agentSwapDisabled": false, "_clanId": "...",
  "_arena": false, "_arenaParams": "<json string>", "_isArena": false, "_groupPlay": true,
  "_scoreAverage": 0.0, "_scoreStandardDeviation": 0.0, "_lobbyTimeAverageInMilliseonds": 0,
  "_bots": 0, "_spawnedAsBots": 0, "_humans": 0, "loadFromCurrentLevel": false, "levelPath": "..." }

// RoomUser
{ "credential": "anonymous:xxxx", "fed_id": "<account>", "name": "...", "client_id": "...",
  "matchmaking_score": 0, "_level": 12, "_avatar": {...}, "_trophies": 1234, "_gentleTrophies": 0,
  "_league": {...}, "_clan": "...", "_emblem": "...", "_entry": 0, "privateMatchTeam": "<team>|Unassigned",
  "_isSpectator": true, "_teamRoomId": "...", "_teamMemberCount": 2, "_score": ..., ... }
```

`Room.IsValid` means `id` (or `room_id`) is non-empty. `ConnectToServer` throws if the room is invalid
(`EN/ClientComponent.cs:1103-1107`). `Room.IsClosed()`/`IsGameOver()` test `_closed==1` / `_matchOver==1`
(`GM/RoomExtension.cs:10-40`).

### 2.3 Lobby / Controller ("Anubis") TCP protocol [V]

Plain TCP with no TLS (`EN/TcpSocket.cs:185-194`). Messages are newline-terminated compact JSON
(`EN/MessageReader.cs:171-174`). Each request waits for the **next** message received. There is no request id, so a
connection carries one request at a time. Timeout is 60 s. `success:false` raises `AnubisRequestFailedException`
(`EN/AnubisClientSocket.cs:17, 51-66, 86-134`).

| Step / action (client → server) | Fields | Expected reply |
|---|---|---|
| (on connect, server → client) | – | `{"action":"create connection","success":true,"nonce":"<n>"}` |
| `login` | all RoomUser fields + `access_token` = **encrypted** token from `GET auth/encrypt_token?nonce=<n>` using scope `lobby` (client) or `lobby_admin` (game server session) | `{"action":"login","success":true}` |
| `create room` | `server_type`, `timeout` (seconds, double), `reservation_codes[]`, plus room attributes at top level | `{"success":true,"room_id":...}` (then the client sends `get room info`) |
| `create team room` | same | same |
| `join room` | `room_id`, `reservation_code?` | success |
| `leave room` | `room_id`, `credential?` (a fed id, used for kick) | success |
| `delete room` | `room_id` | success |
| `update room` | `room_id` + attributes | success |
| `update user` | `room_id`, `credential` + attributes, or a full RoomUser | success |
| `reserve` / `remove reservation` | `room_id`, `reservation_codes[]` | success |
| `launch game` | `room_id` | success |
| `get room info` | `room_id` | `{"success":true,"room":Room}`. If `room.game_started`, the client also sends `get game controller` → `{"controller_host","controller_tcp_port"}`, merged into the room (`EN/Lobby.cs:206-218`) |
| `connect game` (to the **controller**) | `room_id` | `{"action":"connect game","success":true,"slot":"<slot>"}`. After this reply the socket belongs to the game process |

Builders: `OPL/LobbyApi.cs:10-255`. Client wrapper: `EN/Lobby.cs:57-218`. The Lobby address comes from pandora
`locate lobby` (`EN/Lobby.cs:66-72`) or from the Room's `lobby_host/lobby_tcp_port` (join, reconnect, and server-side
`LobbyOperation`, `EN/ServerComponent.cs:734-755`).

`EN/AnubisServerSocket.cs:57-158` is a tiny fake controller: it sends `nonce:"FakeNonce"`, accepts any `login`, and
answers `connect game` with `slot:"0000"`. **Nothing in this build constructs it.** The only reference is its own file
(grep over all decompiled code and assemblies). It is dead code but could be reused for a direct-connect server. [V]

### 2.4 Datacenters [V]

`Federation.ListDatacenters` → `GET {eve}/config/{clientId}/datacenters?country=&override_ip_geolocalisation=`
(`OP/Federation.cs:349-368`). Each entry is `{status, preferred, name, country_code}` (`OP/Datacenter.cs:6-22`). The
TS uses it during the handshake for `ValidateDatacenterForCountries`, and may push a `ChangeDatacenter` request to
the client (`GM/GameServerComponent.cs:858-932, 1005-1021`). This is irrelevant offline. Make the stub return one
`preferred` datacenter equal to the configured one.

---

## 3. Transport and framing

### 3.1 TCP stream (`TcpSocketBase` / `TcpSocket` / `TcpNativeSocket` / `MessageReader`) [V]

* Sockets are non-blocking with 128 KiB send/receive buffers (`EN/TcpSocket.cs:46-56`). `TcpNativeSocket` wraps an
  existing native fd (`EN/TcpNativeSocket.cs:100-104`); it is used for sockets handed over from the dedicated
  controller (`EN/ServerManager.cs:90-93`).
* `MessageReader.Messages` (`EN/MessageReader.cs:55-149`):
  * If the first byte is `0xBB` (187): header `[0xBB][type:u8][len:int32 LE]`, then `len` bytes.
    `type`: `0 Default` (game data → `DataReceived`), `1 Control` (JSON control message), `2 Anubis`.
  * Otherwise: read until `\n` and deliver the bytes as an **Anubis** message (JSON line).
* `MaxMessageSize = 1 MiB` is declared but not enforced (`EN/MessageReader.cs:53`).
* Send paths: `Send()` → Default frame; `SendControlMessage()` → Control frame; `SendAnubisMessage()` → raw JSON + `\n`
  (`EN/TcpSocketBase.cs:107-164`).
* `Update()` flushes queued sends, dispatches complete messages, and raises `DisconnectionDetected` when
  `PollWrite` fails (`EN/TcpSocketBase.cs:71-105`). TCP keepalive helper: `EN/SocketExtension.cs:26-40`.

### 3.2 UDP (`EnetSocket` / `UdpSocket`) [V]

* Native ENet is statically linked. The exported thin API is `EnetSocket_*` (`EN/EnetSocket.cs:505-773`; dynsym of
  `extracted/lib/arm64-v8a/libnativeengine.so`). `EnetSocket_StartServer_private` builds an address with a 16-byte
  host (IPv6-style ENet fork) and port at +0x10, then calls `enet_host_create(peers, channelLimit, 0, 0)` (disasm
  0x392be0). [V]
* Server: `StartServer(20, port 0, channelLimit 0)` gives an ephemeral port (`EN/Connection.cs:1363-1375`).
  `Connection.MaxEnetConnections = 20` (`EN/Connection.cs:110`).
* Client: `StartClient(serverCount = Links.Count, channelCount = 2 * Links.Count)` (`EN/ClientComponent.cs:213-221`).
  `ConnectAsync(ip, port, channelCount = 2)` (`EN/EnetSocket.cs:295-330`). Peer timeouts are set from
  `AsyncTimeout(-1)` on connect (`EN/EnetSocket.cs:342-357`). The GS configures throttle
  `(5000 ms, accel 2, decel 2)` for each new peer (`GM/GameServerComponent.cs:104-108, 206-209`).
* Logical channel: a `UdpSocket` is bound to `(peerId, channelId)`. `PacketType.Reliable` is for channel 0;
  unreliable is used for everything else (`EN/UdpSocket.cs:85-96`).
* **Identification:** after `OpenChannel`, the client sends on that ENet channel, reliably, the 20 bytes
  `ac ef fe ca` (uint 0xCAFEEFAC, LE) followed by `Guid(socketId).ToByteArray()`. The server-side `UdpSocket`
  created for `OpenChannel` has `EnetPeerId = -1`. It adopts the first peer that sends its exact identification and
  only then counts as connected (`EN/UdpSocket.cs:20, 85-96, 153-156, 192-206`).
* Channel fallback: when the unreliable channel is missing, `GetChannel(Unreliable)` falls back to the reliable
  channel (the control TCP socket if no ENet reliable channel exists) for `ChannelFallbackTimeout` (client 20 s,
  server 4 s) (`EN/Connection.cs:259-269, 371-398`). So **all game traffic can run over TCP alone** when no UDP
  channel is opened.
* `EnableReliableChannel` / `EnableUnreliableChannel` are link settings. Only P2P code sets them in C#, so production
  values come from templates. [L] They were on in production: the HUD and tracking show
  `ActionPhaseRTT`/`AvgActionPhaseRTT`, which only exist for an ENet unreliable channel
  (`GM/FirstPersonHudInterfaceComponent.cs:1255`, `GM/GameTrackingComponent.cs:17174`, `EN/Connection.cs:225-247`).

### 3.3 Control-message JSON (TCP frame type 1) [V]

The envelope is added by `Connection.SendControlMessage` / `SendControlRequest` / `SendControlReply`
(`EN/Connection.cs:1625-1793`):

```jsonc
// Request
{"action":"<RequestName or custom>","token":"<Guid>","messageType":"Request",
 "timestamp":<DateTime.UtcNow.Ticks>,"pid":<process id>, ...payload, "IsKayac":false /* client post-processor */}
// Reply (same action and token; must contain "result")
{"action":"<same>","token":"<same>","result":true|false,"messageType":"Reply","timestamp":<server ticks>,"pid":..., ...}
// Message (one-way)
{"action":"ServerError"|"OfflineMatchAgainstBotsOver"|...,"messageType":"Message","timestamp":...,"pid":...}
```

* Replies are matched by `token`. `result:false` raises `ControlRequestFailedException`. Timeout: client
  `DefaultControlRequestTimeout = 20000 ms` (with per-request overrides `CustomControlRequestTimeout`), server 4000 ms
  (`EN/Connection.cs:327-344`, `EN/ClientSettings.cs:81, 141`).
* Clock sync: the client stores `LastServerTimeDelta = (reply.timestamp - localTicks) + RTT/2`, averaged over
  `RTTSampleCount = 3` (`EN/Connection.cs:1766-1779`).
* A request is executed on the server under `RequestContext.World` by default. `ping` runs under `Application`. Each
  delegate registered for an action must return true, otherwise `result:false` is sent (`EN/Connection.cs:104-108, 298-320, 847-861, 1265-1345`).

`Connection.RequestName` (`EN/Connection.cs:29-45`): `Handshake, StartEnetSocket, OpenChannel, CloseChannel,
PauseServer, ResumeServer, CloseConnection, Ping, ServerError, SetAgeAndGender, RegisterPNTransport,
UnregisterPNTransport, Automatch, CustomRequest`. Game-specific action strings: `GetLobbyInfo`, `ChangeDatacenter`,
`OfflineMatchAgainstBotsOver` (`GameClientComponent.CustomRequestName = 14`, `GM/GameClientComponent.cs:25-28`).

| Action | Direction | Request payload | Reply payload | Evidence |
|---|---|---|---|---|
| `Handshake` | C→S | `credential`, `deviceId`, `clientId`, `language`, `localTime` (local `DateTime.Now.Ticks`) | `ServerClientId`, `ServerDlcSlot`, `EnableCrashlytics`, `9SplayCredentials[]`, `FinishedTutorial`, `IsDevelopper`, `EnableThreadedNetwork`, `ReconnectToRoom{Room}?`, `RoomUser:"<json string>"?`, `MustRetryHandshake?`, `_OverrideTables?`, `AgeLimit/AgeGatePopup/NoAgeRequired/Age?` | `EN/Connection.cs:949-974, 1551-1608`; `EN/ServerComponent.cs:361-377`; `GM/GameServerComponent.cs:934-1116`; `GM/GameplayCRMComponent.cs:119-132`; `GM/AgeGateChecker.cs:99-153`; client `GM/GameClientComponent.cs:3618-3659` |
| `StartEnetSocket` | C→S | `{}` | `{"port":<udp>}` | `EN/Connection.cs:995-1009, 1347-1378` |
| `OpenChannel` | C→S | `{"type":"0"|"1"}` (**string**) | `{"channelId":<uint>,"socketId":"<guid>"}` | `EN/Connection.cs:1019-1045, 1380-1460` |
| `CloseChannel` | C→S | `{"channelId":n}` | – | `EN/Connection.cs:1070-1086, 1469-1499` |
| `Ping` | C→S and S→C | `{"retryCount","maxRetries","datetime":"yyyy-MM-ddThh:mm:ss.fff"}` | – | `EN/Connection.cs:1109-1128` |
| `PauseServer` / `ResumeServer` | C→S (TS) | resume: `{"IsPractice":true}?` | – | `GM/GameServerComponent.cs:735-768`, `GM/GameplayComponent.cs:1359-1367` |
| `CloseConnection` | C→S | `{"exception":{stackTrace,message,title,exceptionType}}?` | – | `EN/Connection.cs:648-728` |
| `Automatch` | C→S (TS) | `{"linkId","teamRoomId"?, "filters":{k:"v"}, "host"?, "port"?, "loadFromCurrentLevel"?, "default_room_kwargs"?}` | `{"room":Room}` | `EN/ClientLink.cs:366-391`, `EN/ServerComponent.cs:521-538` |
| `GetLobbyInfo` | C→S (GS) | `{}` | `{"lobbyInfo":{players[{id,name,level,killSignature,score,clan,clanIcon,league,squad}],playersCount,isReady,needsMatchmaking,expectedLobbyTime,gameMode,matchType,objective{name,description},map{name,image},minPlayers,maxPlayers,isInIntermission}}` | `GM/GameMatchmakingComponent.cs:670-793` |
| `ChangeDatacenter` | S→C (TS) | `{"Ask":true,"NewDatacenter","Title","Message","MessageArgs"[]}`, then `{"NewDatacenter"}` | `{"AcceptNewDatacenter":bool}` | `GM/GameServerComponent.cs:880-914` |
| `ServerError` | S→C message | `stackTrace,message,title,showErrorToClient,fatalError,exceptionType,BannedFroms[]?,UpgradeRequired?,MaintenanceBlock?,allowclose?` | – | `EN/Connection.cs:1221-1263`, `GM/GameClientComponent.cs:3567-3608` |
| `OfflineMatchAgainstBotsOver` | C→S (TS) message | `matchResult` (OfflinePlayerMatchResult), `mercsUsed[]`, `isMatchFinished`, `agentScores{}`, `isMVP` | – | `GM/OfflineBotsGameplayComponent.cs:401-473, 529-570` |

### 3.4 Game data packets (TCP type 0 frames or ENet payloads) [V]

`NetworkPacket` is a `BitStream` whose first **3 bits** are `NetworkPacketType`
(`ceil(log2(6))`: `Ping, Synchronization, RPC, Frame, SimpleServerData, Custom`) (`EN/NetworkPacket.cs:7-48`,
`EN/NetworkPacketType.cs:3-11`). Dispatch is in `EN/SynchronizationComponent.cs:577-635`:

* `Synchronization`: a bool "reliable" flag, then per-object deltas (`SynchronizeFromStream`).
* `RPC`: one or more serialized `RPC` records. Each holds `MethodId = HashSum(name)`, an `UnresolvedComponent`
  (network id of the GameObject + component type + instance index), and its arguments (`EN/RPC.cs:74-140`).
* `Frame`: a `CommanderComponent.Frame` (frame id + queued command RPCs). The server rejects frames whose implied
  time is more than 30 s off (`EN/SynchronizationComponent.cs:1800-1811, 1925-1934`).
* `SimpleServerData` / `Custom`: hooks that are empty in this build.

The bit-level layout of `BitStream`/`RPC` is shared code (`EN/BitStream.cs`, 3881 lines). It was not reversed here,
and it does not need to be, because every endpoint runs the same assemblies. The type registry and method-id hashes
must match, so **all peers must run the same build** (1.17.32). The profile game-version check also enforces this
(`GM/ProfileManagerComponent.cs:265-270`).

---

## 4. Sequences

### 4.1 Boot → TS (online) [V]

1. `ClientComponent.Connect(TS link)` → `Prepare`: `Authentication.Connect()` (anonymous Federation session, scopes
   `auth lobby config`). For `Embedded` it also runs `LocalAnubisManager.Start()` (`EN/ClientComponent.cs:754-783, 987-1010`,
   `EN/AuthenticationComponent.cs:270`).
2. `GetRoom`: the TS link uses `AnubisCommand.QuickLaunch` [L, template] → **M1** `quick_launch server_type=ts`
   (`EN/ClientLink.cs:398-412`).
3. `ConnectToServer` (`EN/ClientComponent.cs:1095-1139`):
   1. `AnubisClientSocket.Login(room.controller_host, controller_tcp_port, RoomUser)`: receive
      `create connection`+nonce → `GET auth/encrypt_token` → send `login` (`EN/AnubisClientSocket.cs:34-84`).
   2. `connect game {room_id}` → success (`EN/ClientLink.cs:262-267`).
   3. `CreateConnection()`: the same socket becomes `Connection.ControlChannel` (id 0xFFFFFFFF). Pings start (client
      every `PingRecurringDelay = 10 s`, `PingMaxRetries = 10`) (`EN/ClientLink.cs:275-283`, `EN/ClientSettings.cs:144-153`).
   4. `Authentication.OpenAnonymousSession` again (cached); optional IAP receipt check.
   5. `Handshake` control request. On the TS the server side does: `OpenSessionForUser(credential)` → optional
      `ValidateInstall` → `StartAlerts` (`alert` listener for `Connection` alerts = multiple login) → handshake
      delegates (profile load, CRM, age gate, geolocation, region filter, leaderboard registration) → reply.
   6. If enabled: `OpenChannel(Reliable)` (`StartEnetSocket` → ENet connect → `OpenChannel{"type":"0"}` →
      identification packet), then `OpenChannel(Unreliable)` (`EN/ClientLink.cs:285-314`).
4. `OnLinkConnected` → `GameClientComponent_Connected`. If the handshake returned `ReconnectToRoom`, the client
   reconnects to that GS room (§4.5). Otherwise it either starts the tutorial (offline link) or calls
   `World.Synchronization.StartSynchronization()` (RPC to the server). The TS then replicates the menu world,
   including the `LevelPath` sync property (`GM/GameClientComponent.cs:3666-3693`,
   `EN/SynchronizationComponent.cs:297-308, 1357-1379`).

### 4.2 Quick match (PvP) [V]

1. The UI calls native `StartAutoMatch` → `StartAutoMatchAsync` (`GM/GameClientComponent.cs:3953-3995`). It only runs
   when the current link is the TS and not paused.
2. RPC `OfflineBotsGameplayComponent.RequestOfflineBotsMatchRemote` → the TS decides whether this player gets a bot
   match (tutorial/onboarding) (`GM/OfflineBotsGameplayComponent.cs:208-232`). If yes, see §4.6.
3. `ConnectGameplayServer(QuickMatchServerLink)` sets the filter `_arena`, then `Connect(link)`
   (`GM/GameClientComponent.cs:2776-2844`).
4. `GetRoom`, with the QuickMatch link set to `AnubisCommand.Automatch` [L, template] → `ClientComponent.Automatch`
   → `link.Automatch(TS connection, request)` → control request **`Automatch`** to the **TS**
   (`EN/ClientComponent.cs:785-793`, `EN/ClientLink.cs:366-391`, `EN/MatchmakingComponent.cs:293-304`).
5. On the TS: `ServerComponent.Automatch` → `GameMatchmakingComponent.DoAutomatch(connection.Session /* the player's
   SessionForUser */, args)`. This finds the team room, builds the settings from the player's profile, calls **M2**
   (with `host/port` when embedded), records a matchmaking-distribution stat, and replies `{"room":...}`
   (`EN/ServerComponent.cs:521-538`, `GM/GameMatchmakingComponent.cs:917-932, 1042-1103`).
6. Client `ConnectToServer(GS room)`. `PauseCurrentLink` sends `PauseServer` to the TS. The TS then
   `SaveAndReleaseProfile`s: `ProfileOwnerRoomId = null`, save, `DisableSave` (`GM/GameServerComponent.cs:694-704, 759-768`).
   After that come controller `login`, `connect game`, `Handshake`, and the ENet channels, exactly as in §4.1.
7. On the GS:
   1. `ServerManager.AddClientAsync` creates or joins the headless World for `slot`.
   2. The first client triggers `InitWorld`: `GetStartLevelArguments` picks the map (private map, arena map, or
      weighted random `MapsComponent.GetAvailableMaps(PVP)`), then `LoadLevel` and the match type
      (`EN/ServerManager.cs:146-184`, `GM/GameServerComponent.cs:1191-1333`).
   3. `AddClient` → `Connection`. On `Opened`: `MultiplayerComponent.AddPlayerConnection` (refused when there are
      ≥ `MaxPlayers` humans), and the room's `matchmake_score` is updated (`GM/MultiplayerComponent.cs:1294-1400`,
      `GM/GameServerComponent.cs:496-518`).
8. Lobby phase. The client polls `GetLobbyInfo` every `GetLobbyInfoRecurringDelay = 1 s`.
   * States: `WaitingForPlayers` → `TeamReview` (5 s) → `WaitingForSynchronization` → `CharacterSelection` →
     `BattleStart` (`GM/GameMatchmakingComponent.cs:23-32, 374-632`).
   * The GS adds bots over time: `num = floor(elapsed/MaxLobbyTime * MinPlayers)`, capped at `MaxPlayers`; the full
     room after `MaxLobbyTime` for team splits (`GM/GameMatchmakingComponent.cs:327-361`).
   * When `HasEnoughPlayerToStartMatch` (≥ `MinPlayers = 2` active players including bots), the GS schedules
     `CloseRoom` after `MaxOpenTimeInSeconds`. `CloseRoom` does the following (`GM/GameServerComponent.cs:294-430`):
     1. Sets `_closed=1` and stores score stats in the room.
     2. Reserves `"bot"` in every free slot.
     3. Trims or fills bots to `MaxPlayers`.
     4. Balances the teams.
     5. Saves `CurrentMatchAccessor` (pending loss) per member.
9. Match. Snapshots run at 20 Hz; see §6.
10. Match over, on the GS (`GM/GameplayComponent.cs:765-929`):
    1. Glicko MMR and trophies (`ApplyMatchResult`), `ProcessRewards`, achievements, stats.
    2. `BroadcastMatchResult` RPC.
    3. `SaveProfile` per player, leaderboard `PostScoreAsync`, clan `UpdatePlayerScoreServer`.
    4. `_matchOver=1` in the room (`GM/GameServerComponent.cs:520-534`).
11. The client shows the results, then `CloseGameplayConnection` → `CloseConnection` control request → the GS
    `SaveAndReleaseProfile`s again. The client returns to the TS link and sends `ResumeServer`. The TS reloads the
    profile from storage, takes the lock (`ProfileOwnerRoomId = TS room`) and saves. The client then calls
    `StartSynchronization` (`GM/GameplayComponent.cs:1326-1386`, `GM/GameServerComponent.cs:735-757`).

### 4.3 Private match ("tournaments") [V] (`GM/GameClientComponent.cs:1277-2440`)

* Create, on the owner side:
  1. Checks: profanity check of the name (`ProfanityService`), and the ticket price through `LiveTournamentComponent`.
  2. Room info: `privateMatch=true`, `_started=false`, `capacity = 8 + spectators`, `_codeId`, map/type/rules/rounds
     attributes.
  3. Reservations: `_privateMatchPlayer` × (8-1) and `_privateMatchSpectator` × N.
  4. Lobby `create room` with `timeout 30 min` → `update room {_codeId}`. `_codeId` is a base-32 encoding of
     `roomId.GetHashCode()` plus a platform suffix letter derived from `ServerClientId`.
* Browse: the client polls `FindRooms` (M3) with the filters from §2.1 every `FindRoomRecurringDelay`. Joining by code
  uses `FindRooms` with `_codeId`.
* Join: Lobby login on `room.lobby_host/port` → `join room {room_id, reservation_code}`. While in the lobby, the
  client polls `get room info` every 500 ms. Switching teams uses `update user`.
* Start (owner only): `update room {_started:true}`, the ticket is bought, the map/type are resolved,
  `BalancePrivateMatchRoom` runs, `RegisterRoomInProfile` (an RPC to the TS that saves `CurrentMatchAccessor`), and
  the owner connects the PrivateMatch link with `AnubisCommand.CreateRoom` (`GetRoom` → `launch game` if not started).
  The other members see `game_started` and connect with `AnubisCommand.JoinRoom` (`JoinPrivateMatch`).
* On the GS, `IsPrivateMatch` (room attr `privateMatch`) switches behaviour: `GameMode.Tournament`, no automatch, no
  MMR, `_privateMatchMapPath` / `_privateMatchType` / `_duplicatesAllowed` / `_agentSwapDisabled` are applied, and an
  empty room forces match-over (`GM/GameServerComponent.cs:110-117, 706-733, 1196-1303`).

### 4.4 Group play / team rooms (party → matchmaking) [V] (`GM/GameClientComponent.cs:423-1222`)

* The owner sends `create team room` to the QuickMatch link's `TeamLobby` with `_groupPlay=true`, `_closed=0`,
  `_started=false`, `_isArena`, `capacity=4`, `timeout 30 min`, `name="<fedId> team room"`, then `_codeId`.
* Invites are secured Hermes messages of type `groupPlayInvitation` carrying `teamRoomId`, `isArena` and `clanId`,
  sent via an RPC to the TS. They expire after 12 h.
* Members join with `join room`, then poll `get room info` (500 ms).
* Start: the owner sets the team room `_closed=1`, then runs automatch. The request includes `teamRoomId`. The TS
  looks the team room up (M3 `id=`), adds `_teamRoomId`/`_teamMemberCount`/`team_room_id`/`max_tags`, and computes the
  group MMR from `storage GetBatchProfiles("_mcvs.snapshot.MatchMakingRating")`. After a successful match it sets
  `_started:true`. Members who see the owner's battle start join the same flow (`JoinGroupPlay`).
* GS-side split: `SplitGroupPlay` groups members by `_teamRoomId` (`GM/GameMatchmakingComponent.cs:342-355`).

### 4.5 Reconnect [V]

The TS handshake reads `_mcvs.currentMatch` from storage (`CurrentMatchAccessor`, scope `storage_admin`,
`GM/CurrentMatchAccessor.cs:20-140`). If `CurrentServerType=="gs"`, the room still exists and it is not game-over
(at most `MaxGSReconnectAttempts = 1`), the reply carries `ReconnectToRoom` and `RoomUser`. The client then connects
the QuickMatch or PrivateMatch link with `AnubisCommand.Reconnect` (`join room`) (`GM/GameServerComponent.cs:1068-1101`,
`GM/GameClientComponent.cs:3712-3721`).

### 4.6 Offline links (bot match, practice, tutorial) [V]

`LoadOfflineLevel(linkName, level)` creates an ephemeral link (`AnubisCommand.None`, `ServerLocation.None`) →
`ConnectLink` (re-applies the SceneComponents template, enables the Client) → `Connect`
(`EN/ClientComponent.cs:455-467, 1095-1101`, `GM/GameClientComponent.cs:3736-3756`). The `Connect` steps are:

1. `Prepare`, which **still opens a Federation session**.
2. `GetRoom` = null.
3. `ConnectToServer`, which on an offline link only pauses the TS and switches the current link.

Then `World.LoadLevel` runs locally. `RemoteUtil.ShouldInvoke` returns false when the current command is `None`, so
every `[Remote(callee:Server)]` method **executes locally** (`EN/RemoteUtil.cs:33-40`). `MultiplayerComponent.IsOffline`
is true (`GM/MultiplayerComponent.cs:298-316`) and bots are spawned locally (`NbOfflineBotsToSpawn = 7`,
`GM/GameClientComponent.cs:4041-4058`). At the end, `OfflineBotsGameplayComponent.OnEndOfMatch` sends the
`OfflineMatchAgainstBotsOver` message on the TS link. If the TS is not connected it waits for it. The TS grants the
rewards (`GM/OfflineBotsGameplayComponent.cs:375-570`).

### 4.7 Embedded (LocalAnubis) plug-in points [V]

* Start: `Prepare` (link `Embedded`) or `ServerManager.StartServer()` when `-Server` and `UnixDomainSocketPath` is
  empty → `LocalAnubisManager.Start()`. Start opens a Federation session with `SuperCredential/SuperPassword`
  (scopes `auth auth_admin_ro`, HTTPS, `ServerInfos.json` fallback). It starts the Lobby on `LocalAnubisHost`
  (default loopback) at `LocalAnubisLobbyPort` (default ephemeral) with backlog 20, and the Finder on 51750
  (`EN/LocalAnubisManager.cs:44-132`, `EN/ServerManager.cs:221-243`).
* The client's matchmaker calls go to `http://{LocalAnubisFinderHost}:{51750}/rooms/...`. Lobby login verifies the
  token with `Session.Verify(access_token, "LocalAnubisNonce", "system:Anubis")` (`LA/BaseServer.cs:358-363, 406-429`).
* Controller `connect game` (`LA/Controller.cs:71-98`):
  1. Fetches `lobbyconfigs` (M4) for the room's server type.
  2. **Temporarily replaces `GameApplication.Args` with `Arguments.FromString(cmd_args)`**.
  3. Calls `ServerManager.OnRoomInfo(room)` and `ServerManager.AddClientAsync({credential, fed_id, room_id, slot=room_id}, GameSocket)`.
  4. Restores the args.
  5. After replying, hands the accepted socket to the game (`SwitchToGameSocket`, `LA/BaseServer.cs:102-110`).
* Advertised addresses: `controller_host/lobby_host = LocalAnubisManager.PublicHost`. That is `"localhost"` if the
  host is loopback, otherwise `Dns.GetHostEntry(Dns.GetHostName())` first IPv4 (`EN/LocalAnubisManager.cs:75-82`,
  `LA/Lobby.cs:179-180, 205`).

---

## 5. Authentication and how the server validates a joining client

[V] unless marked.

1. The client gets a Federation token (anonymous or linked credential; report 02).
2. Before each Anubis login, the client calls `GET auth/encrypt_token?nonce=<controller nonce>&access_token=<token
   with lobby scope>`. The plain-text result goes into `login.access_token` (`EN/AnubisClientSocket.cs:78-84`). A
   game server's own lobby sessions use scope `lobby_admin` (`Session.IsGameServer`).
3. The **controller (Anubis) validates** the token and nonce. LocalAnubis does it with `auth/verify`
   (`LA/BaseServer.cs:406-429`). It builds the RoomUser from the verify response (`credential`, `account`→`fed_id`,
   `client_id`) plus the login fields.
4. The controller hands the socket to the game process with `{credential, fed_id, room_id, slot}`. On the GS,
   `ServerComponent.AddClient(fedId, credential, socket)` replaces any existing connection with the same fed id
   (`EN/ServerComponent.cs:388-403`).
5. Control `Handshake`: the server takes `request.credential` only if the connection had none (it was set from the
   hand-off). It opens `SessionForUser(SuperCredential, SuperPassword, credential, deviceId)` with
   `InitialScopesServer` = `storage_restricted chat message storage_ro leaderboard_override social_trophies storage
   social_group leaderboard_ro social_event clusters config message_secured leaderboard_restricted message_broadcast
   alert auth lobby leaderboard social social_clan` (`EN/Connection.cs:1551-1608`, `EN/AuthenticationComponent.cs:272-277, 576-600`).
   **No client token is checked here.** A ban (`IsFullyBanned`) or `UserBannedException` produces `ServerError`.
6. Optional `ValidateInstall` (only with `-PerformIapCheck`) reads and deletes the storage field
   `install_verified_status` (`EN/Connection.cs:1519-1549`).
7. Multiple-login protection:
   * An alert listener for type `Connection` on each server connection closes it with `MultipleConnectionException`.
   * The TS also posts a `multipleConnectionDetected` secured message, kicks the user from the old room, and waits
     up to 5 s for `ProfileOwnerRoomId` to clear (`EN/Connection.cs:576-596, 1610-1616`, `GM/GameServerComponent.cs:1134-1173`).

**Consequence for P2P/LAN:** a replacement controller can trust its LAN peers, or it can verify a self-describing
token issued by the guest's own stub (report 02 §0.8). The GS code path needs only a credential and fed id, plus a
Federation that will open a `SessionForUser` for them.

---

## 6. Replication / tick model

* **Fixed step 20 Hz.** `World.ConstantDeltaTime = 0.05 s`, at most 4 constant frames per update (`EN/World.cs:1983-1985`).
  The headless frame rate comes from the template `GameSettingsComponent.HeadlessFramerateLimit`
  (`EN/GameApplication.cs:1301-1320`). The client is capped at 30 fps by `MCVS.game` [S]: an LZ4 fragment of
  `captures/cdn/10_astc_entry_MCVS.game.zip.bin` shows `"FramerateLimit": 30.0`.
* **Snapshots.** On the authority (`IsSynchronizationOwner == IsServer`), every `OnConstantUpdate` runs
  `Synchronize()` (`EN/SynchronizationComponent.cs:985-1011, 1941-1992`). For each opened connection, and for each
  synchronized GameObject, it serializes the `[Sync]`/`[SyncReliable]` members:
  * Delta caches are per connection (`SynchronizationSettings`, `EN/SynchronizationSettings.cs:6-78`).
  * Dirty detection is `StreamCompare` / `NotifySet` / `Manual` / `PropertyChanged` (`EN/SyncAttribute.cs:25`).
  * Output is one reliable and one unreliable `Synchronization` packet per connection.
  * The world's object creation/destruction table (`bObjectsCreationSettings`), inactive set, and `LevelPath` are
    themselves reliable sync properties (`EN/SynchronizationComponent.cs:297-334`).
  * `SynchronizationMode.Transactional` (menu/TS) syncs only on request; `Continuous` (gameplay) syncs every tick
    (`EN/SynchronizationComponent.cs:100-104, 1943`).
* **RPCs.** `[Remote(caller, callee, reliable, guaranteed)]` (`EN/RemoteAttribute.cs:5-33`). `this.RemoteCall(...)`
  decides whether to execute locally or send (`EN/RemoteUtil.cs:7-43`, `EN/RemoteCallContext.cs:86-112`):
  * A client in server-slave mode sends server-callee RPCs to the server.
  * The server sends client-callee RPCs to clients.
  * Offline, everything runs locally.
  * Queued RPCs are flushed in `OnPostUpdate`, after a reliable sync (`EN/SynchronizationComponent.cs:1013-1076`).
  * Request/response RPCs (`RemoteRequest`) carry a uint id. `Guaranteed` ones are de-duplicated by
    `RequestResponseHandler` (`EN/SynchronizationComponent.cs:1821-1923`).
* **Who is authoritative.** `Game.Authority.IsMaster` is true for the server and for offline (`GM/Authority.cs:7-15`).
  * Movement and look: the owning client sends `CharacterRemoteState` (position or predicted position, yaw, pitch,
    sprint, fire, reload/melee/charge/teleport/stumble/respawn/grenade counters (mod 4), equipment, walk, zoom) on
    **every constant update** via the unreliable RPC `Synchronize` (callee Server). The server stores it as
    `RemoteState`, and the auto-pilot drives the server-side character (`GM/CharacterSynchronizationComponent.cs:200-290`).
    This is **client-authoritative movement**.
  * Damage: the shooter's client calls `ApplyFireDamage(start, dir, hitPoint, health, bodyPart, shotId)`, a reliable
    RPC with callee Server (`GM/WeaponComponent.cs:1920-1926`). This means **client-side hit detection with server
    application**. The server owns health (`ServerState.CharacterHealthRatio`), slowdowns and stuns, and replicates
    them (`GM/CharacterSynchronizationComponent.cs:97-114, 235-259`).
  * Match state, scoring, bots, rewards and profile changes are server-only.
* **Command frames.** `CommanderComponent` frames carry command RPCs. `CommandFlow` is `ClientControlled` unless
  `-Offline`, and the authority validates frame time (`EN/SynchronizationComponent.cs:956-963, 1477-1483, 1800-1934`).
  Exactly which systems use the Commander was not traced. [S] It is probably the deterministic metagame transactions.
* **Keep-alives and timeouts.**
  * Client `Ping` every 10 s, max 10 retries. A ping is skipped after a "slow frame" (> 4 s).
  * Server pings every 4 s (`KeepAliveRecurrentDelayMilliseconds`). With `KeepAliveIgnoreTimeout = true` the server
    never drops on ping failure (`EN/Connection.cs:473-535`, `EN/ServerComponent.cs:119-141`).
  * During the battle the server closes a connection after `ActionPhaseTimeoutMS` without data
    (`GM/GameplayComponent.cs:504-525`).
  * `DisconnectionDetectedTimeout = 50 s`.
  * An empty GS world is destroyed 10 s after the last connection, after `delete room` (`EN/ServerComponent.cs:405-448`).
* **Max players:** see §0 item 8.

---

## 7. Matchmaking logic (game side) [V]

* `GameMatchmakingComponent` (`GM/GameMatchmakingComponent.cs`):
  * Defaults: `MinPlayers 2`, `MaxPlayers 8`, `EnableBotInLobby true`, `DefaultLobbyTime 1 min`, `MMRCap 2500`,
    `TerminatorThreshold 2400` / `TerminatorMMR 20000` (an all-bot team facing high-MMR humans gets "terminator"
    bots), dynamic score range 100..1000 step 100, `ScoreBucketSize 100`, `TeamReviewDuration 5 s`.
  * Every value can be overridden by CRM `GameParameters.Matchmaking.*` and `MatchmakingRating.{MinDeviation,MaxDeviation,MaxDiffRating}`
    (`:845-891`).
  * Expected lobby time uses `game_stats` category `MatchmakingDistributionDynamicRange` (keys: score bucket and
    `Slot-AnubisRegion` pool) (`:1268-1345`).
  * The per-player max lobby time comes from the DB table `MatchmakingBotAddingTime` (`TrophyRange`,
    `MaxLobbyTimeInSeconds`), or from the `NoobSettings` for a player's first N matches (`:634-656, 1105-1121`).
* `Matchmaking/Glicko.cs`: Glicko-1 with `DefaultDeviation 350`, `MinDeviation 100`, `MaxDeviation 150`,
  `MaxDiffRating 200`, `Glicko(50, 1825)`. `UpdateMethod` values are `PlayerVsAverageTeam`, `PlayerVsAllIndividuals`
  and `SameDeltaForEveryTeammate`. It runs on the GS at match end (`GM/GameplayComponent.cs:779-789`).
* Map selection is a weighted random over `MapsComponent.GetAvailableMaps(gameMode)` by `SelectionProbability`, or
  `ForceLevelIndex` (`GM/GameMatchmakingComponent.cs:893-915`). The match type comes from database probability per
  level (`GameplayComponent.SelectMatchTypeFromDatabaseProbability`).
* `MatchType`: `None, TeamDeathMatch, Survivor, ControlPoint, ShootingRange, Uphold, Tutorial, Dominion`.
  `GameMode`: `None, PVP, Tournament, Onboarding, BotMatch, MENU, Arena` (`GM/MatchType.cs`, `GM/GameMode.cs`).

---

## 8. What the TS/GS need from the backend

| Need | Call | Used by | Evidence | Offline/P2P handling |
|---|---|---|---|---|
| Game-server identity | `auth/authorize` with `SuperCredential/SuperPassword` (args or `ServerInfos.json`) | GS/TS `OpenGameServerSession`, LocalAnubis | `EN/AuthenticationComponent.cs:439-470`, `EN/LocalAnubisManager.cs:84-117` | Stub accepts any super credential |
| Per-player session | `auth/authorize` with `for_username` (SessionForUser) | every handshake | `EN/AuthenticationComponent.cs:576-600` | Stub returns a session for any credential |
| Token check | `auth/encrypt_token`, `auth/verify` | Anubis login | `EN/AnubisClientSocket.cs:78-84`, `LA/BaseServer.cs:406-429` | Self-describing tokens (report 02) |
| Room lookup | M3 `rooms?id=` | `ServerManager.AddRoomInfo`, reconnect, team rooms | `EN/ServerComponent.cs:612-654` | Finder / own matchmaker |
| Room admin | Lobby `login` (lobby_admin) + `get room info`, `update room`, `reserve "bot"`, `remove reservation`, `leave room` (kick), `delete room` | GS lifecycle | `EN/ServerComponent.cs:656-755`, `GM/GameServerComponent.cs:312-430` | LocalAnubis Lobby |
| Game config | M4 `lobbyconfigs/{cid}/{ts|gs}` (`cmd_args`!) | LocalAnubis controller | `LA/Controller.cs:71-106`, `LA/GameConfig.cs` | Stub must return `cmd_args` that at least set `-ServerType ts|gs` (and the super credential) [L] |
| Profile | storage get/set (`ProfileManagerComponent.LoadPlayerProfile`, `SaveProfile`), `_mcvs.currentMatch` (storage_admin), batch `_mcvs.snapshot.MatchMakingRating` | TS & GS | `GM/GameServerComponent.cs:934-1116`, `GM/CurrentMatchAccessor.cs:110-140`, `GM/GameMatchmakingComponent.cs:1026-1030` | See report 03. **The key P2P problem** (§10) |
| Alerts | `alert` listener type `Connection` per server connection | multi-login | `EN/Connection.cs:576-596` | Idle long-poll |
| Messaging | secured messages (multipleConnectionDetected, groupPlayInvitation), Hermes tournament tokens | TS/GS | `GM/GameServerComponent.cs:1134-1173`, `GM/GameClientComponent.cs:977-1022`, `GM/GameplayComponent.cs:806-827` | Accept/no-op, or local inbox |
| CRM | `GameParameters` (server settings, Matchmaking, `_OverrideTables`), `game.CrashlyticsParams.Enable` | GS handshake **requires** `CRMServerSettingsComponent` (otherwise the handshake returns false) | `GM/GameplayCRMComponent.cs:119-132`, `EN/ServerComponent.cs:361-377`, `GM/GameServerComponent.cs:275-292` | Stub returns tuned params |
| Leaderboards | `global` PostScore (RegisterUser on TS), division trophies | TS/GS | `GM/GameServerComponent.cs:780-829`, `GM/GameplayComponent.cs:911-914` | No-op / local |
| Clans | `UpdatePlayerScoreServer` | GS match end | `GM/GameplayComponent.cs:917-925` | No-op |
| Game stats | `GetGameStatsByTime`, `AddUserEvent` (matchmaking distribution / quality) | TS/GS | `GM/GameMatchmakingComponent.cs:1268-1345` | Empty array is fine (all calls wrapped in try/catch) |
| Geolocation | `http://ip.ecomapis.gameloft.org/location/?ip=` (`country_info.iso2`), edge list `https://it.gameloft.org/dump/dump.edgelist` (2 s timeout) | TS handshake (region, **IsDeveloper/IsQA** if the IP is in the edge list or `AdditionalDeveloperIpRange`) | `decompiled/Gameloft.Util/Gameloft.Util/Geolocalization.cs:93-148`, `GM/GameServerComponent.cs:976-1026` | Wrapped in try/catch, falls back to "US". Redirect or ignore |
| Datacenters | `config/{cid}/datacenters` | TS (country validation) | `GM/GameServerComponent.cs:1005-1021` | One preferred DC |
| Tracking/anti-cheat | GLOT tracking; `AntiCheatManager.StopSession` is a no-op | – | `EN/AntiCheatManagerComponent.cs:35-37` | Ignore |

---

## 9. Was the authoritative server the same C# code? Can one process be server and client?

**Same code, run headless:** [V] for the code paths, [L] for production deployment.

* `Arguments` has `Server`, `Headless`, `Slot`, `Revision`, `ServerType`, `UnixDomainSocketPath`, `Port` (default
  51737), `Tunneling`, `SuperCredential/SuperPassword`, `Watchdog`, `Automatcher`, `GSPort`, `RemoteIp`,
  `GSRemoteIp`, `ClientMode`, `GSClientMode` (`EN/GameApplication.cs:44-169`).
* `-Server` starts `ServerManager` and creates **no client World** (`EN/GameApplication.cs:1216-1234`).
* The non-local path `ServerManager_private_StartServer(path, onAnubisMessage(json, fd), onClosed, port, tunneling)`
  receives either room info or `{credential,fed_id,room_id,slot}` plus a socket fd. Each new slot gets
  `new World(headless:true, createSceneComponents:true, server:true)`, `InitWorld(room)`, `AddClient`
  (`EN/ServerManager.cs:63-184`).
* Server-oriented loggers (Syslog / ELK with `Slot/Revision/ServerType`, `EN/SyslogLogger.cs:55`) and dev SSH
  deployment defaults (`Host = "alpha01.mdc.gameloft.org"`, `ServerPath = "opt/data/gameserver/games"`,
  `EN/ClientSettings.cs:86-92`) point to a Linux headless build of the same assemblies, launched by Anubis with
  `cmd_args` from `lobbyconfigs`.
* In this Android `libnativeengine.so`, `ServerManager_private_StartServer` tail-calls a function that only returns
  -1 (0x399ac8 → 0x3e2894). So the UDS hand-off path is compiled out on Android and `StartServer()` calls
  `StopServer()` (`EN/ServerManager.cs:226-230`).

**Listen server:** [V] yes. See §1.2 and §4.7. Server Worlds and the client World coexist in `GameApplication.Worlds`.
They talk through real loopback sockets, and remote peers can reach the same listeners if they are bound to a
reachable interface. [L] An Android instance launched with `-Server -Headless` (no `UnixDomainSocketPath`) would
run LocalAnubis as a **headless dedicated server**. This makes a community server possible in an Android container
(for example Redroid) with no Linux server build. Not tested; whether headless rendering init works on Android is
unknown.

---

## 10. What a replacement must provide

### 10.1 Common base (needed for any mode, including pure offline)

1. **A Federation replacement** (report 02): eve/pandora, `auth/authorize` (any credential, including the super
   credential and `for_username`), `auth/users/me`, `auth/encrypt_token`, `auth/verify`, alert long-poll, storage
   (report 03), CRM `GameParameters` (report 04), matchmaker `lobbyconfigs` with `cmd_args`.
   * In embedded mode, the matchmaker/finder and lobby/controller are the **built-in LocalAnubis**. Nothing extra is
     needed for them.
2. **Launch args:** `-ServerLocation Embedded` (colon-free, fine for `phantomArgs`) plus a redirect of
   `EnvironmentSwitcherUrl` via `args.txt` or patching (report 01). Optional: `-LocalAnubisHost`,
   `-LocalAnubisFinderPort`, `-LocalAnubisLobbyPort`, `-LocalAnubisControllerPort`.
3. **The `lobbyconfigs` reply** for `ts` and `gs` must give `cmd_args`. These become the global `Args` while the
   server World is created, so they must carry at least `-ServerType ts` / `-ServerType gs`. [L] Include
   `-SuperCredential`/`-SuperPassword` and anything else read from `Args` during World construction
   (`AuthenticationComponent` ctor reads `Args.SuperCredential`, `EN/AuthenticationComponent.cs:308-317`). Owner:
   LocalAnubis analyst.
4. CRM `Matchmaking` tuning for small player counts: `MinPlayers`, `MaxOpenTimeInSeconds`, `EnableBotInLobby`, and
   lobby times, so that a 1–2 human room fills with bots quickly.

### 10.2 LAN P2P (one device hosts)

Recommended "host-embedded" topology. Netcode changes: none. Plumbing changes: small.

```
  Guest device                                     Host device
  +--------------------------+                     +-------------------------------------------+
  | Client World             |  TCP (Finder 51750) | LocalAnubis Finder/Lobby/Controller        |
  |  TS link: Embedded(local)|-------------------->|   bound to LAN IP                          |
  |  QuickMatch/PrivateMatch |  TCP controller     | GS World (headless) <- guests + host client|
  |   link: Remote(host IP)  |-------------------->| ENet host (UDP ephemeral or fixed)         |
  |  own TS World + stub     |  UDP ENet           | Federation stub (host)                     |
  +--------------------------+-------------------->|   storage for guest -> proxy to guest stub |
                 ^                                 +-------------------------------------------+
                 |  HTTP (LAN): guest stub serves its own profile to the host stub
```

What must exist or change:

1. **Host listeners on the LAN interface.** `-LocalAnubisHost <host LAN IP>`. Patch `LocalAnubisManager.GetPublicHost()`
   to return `Host.ToString()`. It currently uses `Dns.GetHostEntry(Dns.GetHostName())`, which on Android
   [S] most likely resolves to loopback, so peers would receive an unusable `controller_host`
   (`EN/LocalAnubisManager.cs:75-82`).
2. **Per-link redirection on guests.** Keep the TS link `Embedded` (local progression), and point the gameplay links
   at the host. Set `ServerLocation = Remote` (so `OverrideAnubisHost`, but no local LocalAnubis start for that link)
   and `LocalAnubisFinderHost = <host IP>`, `LocalAnubisFinderPort = 51750`.
   * `-ServerLocation` applies to every `Auto` link, so this needs a small C# patch: for example in
     `LinkSettingsInitializerComponent.ResetLink`, or by setting `QuickMatchServerLink.Settings.*` and
     `PrivateMatchServerLink.Settings.*` at runtime.
   * Automatch then runs on the **guest's own TS**, which calls the host Finder `automatch` because the client puts
     `host/port` into the request (`EN/ClientLink.cs:377-382`, `EN/MatchmakingComponent.cs:402-412`). This works.
3. **Profile access across devices.** This is mandatory, because the host GS loads and saves the guest's profile
   through *the host's* Federation (`SessionForUser(guestCredential)` → storage). Options:
   * (a) **Federated stubs.** Tokens and credentials encode the owner device (report 02 §0.8). The host stub proxies
     storage get/set (and `currentMatch`, batch snapshot MMR) for foreign accounts to the owner's stub over LAN HTTP.
     The guest's TS sees the results through `ReloadProfileAsync` on `ResumeServer`.
   * (b) **Pre-upload.** The guest pushes a profile snapshot to the host stub on join, and pulls it back after the
     match (needs a side channel or a C# hook).
   * (c) **Patch the GS.** Never save remote profiles; send the match results to each guest's own TS, the same way as
     `OfflineMatchAgainstBotsOver`. Clean ownership, but more code.

   Also keep the lock protocol consistent. `ProfileOwnerRoomId` has to be cleared and set across devices, otherwise
   `MakeSureProfileIsUnlocked` waits 5 s and kicks (`GM/GameServerComponent.cs:1134-1173`).
4. **Token validation** on the host's LocalAnubis: `auth/verify` on the host stub must accept the guest's encrypted
   token. Use self-describing tokens, or trust the LAN.
5. **Discovery.** Options: P2P.cs UDP broadcast (`"Discovering"` → Room JSON; another analyst covers it), manual IP
   entry, or the built-in private-match room browser (M3 against the host Finder) plus `_codeId` join codes. Note
   that the shipping P2P path is a dev tool: `P2PRoom.Connect` leaves `AnubisCommand = None`, so `GetRoom` returns
   null and `ConnectToServer` throws (`EN/P2PRoom.cs:30-38`, `EN/ClientComponent.cs:1019-1023, 1103-1107`). [V]
6. **Ports.**
   * TCP 51750 (Finder HTTP).
   * The Lobby and Controller TCP ports. They are ephemeral by default; fix them with
     `-LocalAnubisLobbyPort/-LocalAnubisControllerPort`.
   * UDP: the ENet port is **always ephemeral** (`StartServer(20, 0, 0)`). Patch it to a fixed port if a firewall or
     relay is involved (`EN/Connection.cs:1365-1370`).

### 10.3 Internet P2P

Everything in 10.2, plus reachability:

* **Easiest:** an overlay network (Tailscale, ZeroTier, WireGuard). With it, internet play looks like LAN. No code.
* **Relay / rendezvous service.** The guest's ENet target IP is the **remote IP of the TCP control socket**
  (`EN/Connection.cs:1003`). Therefore a relay that terminates TCP must also relay UDP on the same public IP. It must
  either learn the host's ENet port by parsing the `StartEnetSocket` reply (TCP frame `0xBB 01 len` JSON
  `{"port":...}`), or rely on a patched fixed ENet port. The relay must also rewrite or own the `controller_host/port`
  and `lobby_host/port` that the Finder and Lobby advertise.
* **Port forwarding / UPnP** on the host with fixed ports.
* **Community dedicated server (alternative to P2P).** Run the APK headless (`-Server -Headless`, LocalAnubis path)
  in an Android container [L], with a central Federation stub that also owns every profile. This removes the
  profile-ownership problem and restores authoritative internet matchmaking (M1/M2/M3 semantics) close to the
  original. The TS can stay local (offline progression), or move to the server.

### 10.4 Bot matches and "unlock all"

* Solo bot matches need only the embedded TS (§4.6). Rewards are applied on the TS from `OfflineMatchAgainstBotsOver`.
* Mixed human+bot matches happen naturally on any GS through `FillWithBots` and `ReserveBot`.
* `SpawnBot` creates bots with `unlockAllMercs: true, unlockAllSkins: true` (`GM/GameMatchmakingComponent.cs:751-758`).
* Bot difficulty for solo matches comes from the AI DB tables `BotDifficulty` and `SoloBots`, indexed by
  `BotMatchesDoneInTheDay` (`GM/GameClientComponent.cs:3997-4026`).

---

## 11. Verified vs inferred (summary)

**Verified by reading the code or disassembly:**

* Link and ServerLocation model.
* Matchmaker paths and parameters.
* The Anubis JSON protocol from the client and LocalAnubis sides.
* TCP framing; ENet API, identification packet and channel handshake; the control-message envelope and all listed
  actions.
* The handshake and the server-side trust model.
* ServerManager slot/World creation, and the Android stub of the native UDS server.
* The 20 Hz tick and snapshot loop; the RPC permission routing; client-authoritative movement and hit reporting.
* GS room lifecycle (`_closed`, bot reservations, `_matchOver`) and match-end reward/save path.
* Private match and group play flows; offline-link behaviour and the bot-result reporting to the TS.
* Default player counts.

**Likely:**

* Production link templates use QuickLaunch for the TS and Automatch for QuickMatch, with ENet channels enabled.
* Dedicated servers were a Linux headless build fed sockets by Anubis over a UDS.
* `cmd_args` must carry `-ServerType`.
* Android `-Server -Headless` can act as a dedicated server.

**Speculative:** Android hostname resolution for `PublicHost`; Commander usage; the exact client frame-rate value.

---

## 12. Open questions

1. Link templates (`SceneComponents.tx` / level templates in the data pack): exact link ids, `ServerType`,
   `AnubisCommand`, `Matcher`/automatcher name, `MatchmakingFilters`, `EnableReliableChannel/UnreliableChannel`,
   `LocalAnubisFinderHost` overrides, and `GameMatchmakingComponent.MaxOpenTimeInSeconds/MaxPlayers` template values.
   The data pack has to be downloaded (report 10).
2. What `lobbyconfigs` `cmd_args` the official servers used for `ts`/`gs`, and which `Args` fields the headless
   server World reads during the args swap (LocalAnubis analyst).
3. `BitStream`/`RPC` bit layout. Only needed when a non-game process must read gameplay traffic (relay inspection,
   anti-cheat). Not needed for P2P.
4. Whether ENet in `libnativeengine.so` is stock ENet 1.3.x or a fork, given the IPv6-style address. This matters
   only if a third-party relay or server speaks ENet.
5. Whether a headless `-Server` start works on Android without a GL surface (community-server option).
6. The `IsDeveloper` path. The TS marks a player as developer when their IP is in the edge list or in CRM
   `AdditionalDeveloperIpRange` (`GM/GameServerComponent.cs:977-992`). It is worth checking what developer status
   unlocks (cheats, `CheatsComponent`) as a possible "unlock all" lever (economy analyst).
7. Profile write conflicts. The GS saves the full profile at match end while the TS is paused. A P2P design must
   check that no TS-side changes are lost when the guest's TS reloads (report 03 storage semantics).
