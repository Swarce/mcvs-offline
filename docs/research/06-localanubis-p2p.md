# 06 – LocalAnubis (in-process lobby/matchmaker/controller) and the P2P components

Scope: everything under `decompiled/Gameloft.Engine/Gameloft.Engine.LocalAnubis/`, `LocalAnubisManager`,
`HTTPServer`, `P2P`, `P2PServerComponent`, `P2PClientComponent`, `P2PRoom`, `ServerManager.LocalServer`,
plus every place that enables them, and the call chains they depend on (client links, server worlds,
transport, Federation). The report ends with a P2P enablement recipe and a gap list.

All paths are relative to `/home/user/mcvs-work`. Abbreviations:

* `LA/` = `decompiled/Gameloft.Engine/Gameloft.Engine.LocalAnubis/`
* `EN/` = `decompiled/Gameloft.Engine/Gameloft.Engine/`
* `OP/` = `decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/`
* `GM/` = `decompiled/Game/Game/`

Tags: **[V]** = verified (I read the code, IL or disassembly). **[L]** = likely (strong inference, not every link
checked). **[S]** = speculative.

Companion reports: `reports/01-boot-redirect.md` (how to inject arguments), `reports/02-federation-protocol.md`
(the Federation stub every path below needs), `reports/03-progression-storage.md` (who loads and saves profiles).

---

## 0. TL;DR

1. **LocalAnubis is a real, working developer "embedded server" mode, not an offline mode.** With
   `ServerLocation = Embedded`, the client process starts a local Anubis **Lobby** (TCP JSON), **Controller** (TCP
   JSON) and **Finder** (HTTP matchmaker on port 51750). On "connect game", the Controller spawns a **headless server
   World inside the same process** (`ServerManager.AddClientAsync`) and turns the controller TCP socket into the game
   socket. So one phone can host its own Transaction Server ("ts") and gameplay server ("gs"). **[V]**
   (`EN/ClientComponent.cs:995-1000`, `EN/LocalAnubisManager.cs:113-132`, `LA/Controller.cs:71-98`,
   `EN/ServerManager.cs:146-184`)
2. **It still needs a Federation backend for everything.** LocalAnubis opens its own Federation session with a
   `game:` super-credential (eve → pandora → `auth/authorize`), verifies every lobby login and every Finder call with
   `auth/verify`, and fetches the room/game configuration with `matchmaker GET lobbyconfigs/{clientId}/{serverType}`
   (`GameConfig.FindGame`) on every create/join/quick-launch/automatch/connect. The in-process server worlds then
   open per-player `SessionForUser`s and load/save profiles through `storage`. **Against today's Gameloft servers
   none of this works** (eve no longer knows the client id, see report 01). **With the Federation stub from reports
   02/03, nothing in LocalAnubis needs reimplementing for single-device play.** It needs a few extra stub endpoints
   (`encrypt_token`, `verify`, `lobbyconfigs`) and correct config JSON. **[V]**
3. **How to turn it on (single device):** `-ServerLocation Embedded -AnubisCommand QuickLaunch`, plus a super
   credential (`ServerInfos.json` in the data, or `-SuperCredential/-SuperPassword` via `args.txt`, since the values
   contain `:`), plus the Federation redirect from reports 01/02. **Do not** pass `-LocalAnubisHost` or
   `-LocalAnubisFinderPort`: either one breaks the Finder path as shipped (sections 2.4 and 6). **[V]**
4. **As shipped it is strictly single-device (loopback).** **[V]** Four things stop remote devices:
   * the Finder `HttpListener` prefix is hard-coded to `http://localhost:51750/`. Mono binds that to 127.0.0.1
     and rejects any request whose `Host` header is not `localhost`;
   * Lobby and Controller bind 127.0.0.1 unless `-LocalAnubisHost` is set;
   * the advertised host (`PublicHost`) is `"localhost"`, or a DNS lookup of the device hostname that very likely
     gives 127.0.0.1 on Android **[L]**;
   * there is no argument that points a client at a remote Finder.

   Remote join therefore needs a handful of small IL patches (section 10.5).
5. **`P2P.cs` / `P2PServerComponent` / `P2PClientComponent` / `P2PRoom` are an unfinished editor prototype.** It
   is LAN-only UDP broadcast discovery on port 50034 (`"Discovering"` → JSON room). The advertised room has
   `controller_tcp_port = -1`. `ServerLocation.P2P` has no implementation, and the "connect" verbs only register a
   link without connecting it. There is **no NAT traversal, no relay, no host migration, no peer
   authentication**. The "P2P" design is plain client/server: **the host's in-process server World is
   authoritative**, and guests are ordinary clients (TCP control channel plus ENet/UDP channels). **[V]**
6. **Game transport after matchmaking** is a TCP connection (the controller socket, re-used, with framing `0xBB`,
   type, int32 length) plus ENet UDP for the reliable and unreliable channels. The host's ENet server binds
   **any-address on an ephemeral port** that it reports over TCP. LAN works. Internet needs a VPN overlay,
   port-forwarding plus a fixed-port patch, or a relay. **[V]** (`EN/Connection.cs:983-1045,1347-1378`, native
   disassembly of `EnetSocket_StartServer_private`)
7. **Best multi-human flows to reuse:** private match (create/find/join room by lobby), group-play team rooms with
   a join code (`_codeId`), and automatch with `midgame_join`. All of them run on the LocalAnubis Lobby/Finder
   unchanged once the host is reachable. **[V]**

---

## 1. Component map

| Piece | Class / file | Transport | Bind address | Port | Role |
|---|---|---|---|---|---|
| Manager | `EN/LocalAnubisManager.cs` | – | – | – | Opens super Federation session, starts Lobby (+Controller) and Finder; computes `Host`/`PublicHost` |
| Lobby | `LA/Lobby.cs` (: `BaseServer`) | TCP, JSON lines | `LocalAnubisManager.Host` (default `127.0.0.1`) | `-LocalAnubisLobbyPort` or **0 = ephemeral** | Room CRUD, reservations, launch, automatch/quick-launch logic |
| Controller | `LA/Controller.cs` (: `BaseServer`) | TCP, JSON lines, then game framing | same Host | `-LocalAnubisControllerPort` or **0 = ephemeral** | `connect game` → creates/joins in-process server World, hands over the socket |
| Finder | `LA/Finder.cs` (: `EN/HTTPServer.cs`), `LA/FinderHTTPMessageHandler.cs` | HTTP (Mono `HttpListener`) | **`localhost` prefix → 127.0.0.1, Host header must be `localhost`** | `-LocalAnubisFinderPort` or **51750** | HTTP matchmaker: find rooms, quick launch, automatch, locate lobby/controller |
| Game config | `LA/GameConfig.cs`, `LA/Automatcher.cs`, `LA/AutomatchSettings.cs` | via Federation | – | – | Pulls `lobbyconfigs` from the `matchmaker` service |
| Filters | `LA/BaseFilter.cs`, `LA/Filter.cs`, `LA/CustomFilter.cs` | – | – | – | Room-matching filters |
| Server host | `EN/ServerManager.cs` | – | – | (`DefaultLocalPort 51737` only for the native dedicated path) | One headless `World` per room id ("slot"), adds client connections |
| Fake Anubis socket | `EN/AnubisServerSocket.cs` | – | – | – | **Unused.** Accepts any login (`"FakeNonce"`) and answers `connect game` with slot `"0000"` (`:201-216,271-302`) |
| LAN discovery | `EN/P2P.cs` | UDP broadcast | any | **50034** (responder), ephemeral (requester) | `"Discovering"` probe / JSON room reply |
| P2P responder | `EN/P2PServerComponent.cs` | – | – | – | Enabled in server worlds when `Args.AllowP2P`; replies with a pseudo-room |
| P2P browser | `EN/P2PClientComponent.cs`, `EN/P2PRoom.cs` | – | – | – | Editor-browsable list of discovered rooms, `[Verb] Connect` |
| ENet | `EN/EnetSocket.cs` → `libnativeengine.so` | UDP | any (`in6addr_any`, 16 zero bytes) | ephemeral (`StartServer(20, 0, 0)`) | Game reliable and unreliable channels |

Constants: `LocalAnubisManager.DefaultLobbyPort = 0`, `DefaultLobbyBacklog = 20`, `DefaultFinderPort = 51750`
(`EN/LocalAnubisManager.cs:18-22`); `Lobby.DefaultControllerPort = 0`, backlog 20 (`LA/Lobby.cs:23-25`);
`P2P.port = 50034` (`EN/P2P.cs:64`); `ServerManager.DefaultLocalPort = 51737` (`EN/ServerManager.cs:46`);
`ClientSettings.LocalAnubisFinderPort = 51750` (a hard default, **not** taken from `Args`, `EN/ClientSettings.cs:355`).
**[V]**

The original source layout (from `FireAndForgetTask` paths) is `Engine/Engine/Component/Network/LocalAnubis/*.cs`,
`Engine/Engine/Component/Network/PeerToPeer/P2P.cs` and `Engine/Engine/Managers/LocalAnubisManager.cs` /
`ServerManager.cs`.

---

## 2. Every way LocalAnubis or P2P gets enabled

### 2.1 `ServerLocation.Embedded` on a client link (the main switch) **[V]**

```csharp
// EN/ClientComponent.cs:987-1010
public async Task Prepare(ClientLink link, CancellationToken ct) {
    ResultWithException ex = await Authentication.Connect(ct);   // anonymous Federation session, always
    ...
    switch (link.Settings.ServerLocation) {
    case ServerLocation.Embedded:
        await LocalAnubisManager.Start(ct);                      // starts Lobby+Controller+Finder once
        break;
    case Auto: case None: case Online: case Remote: case SSH: case Tunneling: case P2P:
        break;                                                   // nothing special
    }
}
```

`ServerLocation` enum: `Auto, None, Online, Remote, Embedded, SSH, Tunneling, P2P` (`EN/ClientComponent.cs:31-41`).
`AnubisCommand` enum: `None, QuickLaunch, Automatch, CreateRoom, JoinRoom, Reconnect` (`:21-29`).

`ClientSettings.OverrideAnubisHost` is true for every location except `Online`, `None` and `Auto`
(`EN/ClientSettings.cs:341-351`). When it is true, every matchmaker call goes to
`http://{Settings.LocalAnubisFinderHost}:{Settings.LocalAnubisFinderPort}/...`, and lobby logins go through
`GET /locate?service=lobby` on that Finder (`EN/ClientLink.cs:377-381,400-446,448-458,460-492`). In other words
`Remote`, `SSH`, `Tunneling` and `P2P` are "use a LocalAnubis Finder at Settings.LocalAnubisFinderHost" without
starting one locally. Only `Embedded` starts one.

How a link gets `Embedded`:

* **Command-line argument `-ServerLocation Embedded`.** `LinkSettingsInitializerComponent.ResetLink` overwrites
  `ServerLocation` with `Args.ServerLocation` for every link whose current location is `Auto` and that has a
  `LinkSettings` entry in the template (`GM/LinkSettingsInitializerComponent.cs:28-52`). It is re-applied on every
  `ClientLink.SettingsRestored`.
* **Side effect you must handle:** `ClientComponent.InitLinkSettings` sets the *default* link's
  `AnubisCommand = Args.AnubisCommand` whenever `Args.ServerLocation != Auto` (`EN/ClientComponent.cs:243-250`).
  `Args.AnubisCommand` defaults to `None`, and `GetRoom` with `None` sets `link.Room = null`
  (`EN/ClientComponent.cs:1021-1023`). `ConnectToServer` then throws "Failed to obtain a valid room"
  (`:1103-1107`). ⇒ **always pass `-AnubisCommand QuickLaunch` together with `-ServerLocation`.** The TS link is
  a QuickLaunch link: `GameClientComponent.ShouldPlayTutorial` tests
  `CurrentAnubisCommand == QuickLaunch && CurrentServerLocation != Embedded` (`GM/GameClientComponent.cs:373`).
  This also shows that the tutorial is skipped in Embedded mode, so the mode was really used.
* **Template data**: `ClientSettings.ServerLocation`, `LocalAnubisFinderHost` and `LocalAnubisFinderPort` are
  serialized properties (no `[SerializeIgnore]`, `EN/ClientSettings.cs:26-27,353-355`), so the shipped link
  templates could set them. Unknown until the game data is available (open question).
* **Editor-only verb**: `P2PServerComponent.StartServer()` builds an `Embedded` link with `LoadFromCurrentLevel`
  and `AllowP2P`, then only calls `ConnectLink` (it never connects; see section 7) (`EN/P2PServerComponent.cs:59-75`).

### 2.2 Dedicated "server-only" app mode: `-Server` **[V]** (runtime on Android **[S]**)

```csharp
// EN/GameApplication.cs:1216-1234
if (Args.Server) ServerManager.StartServer();
...
if (!Args.Server) { Worlds.Add(new World(...)); World = Worlds[0]; ... }   // no client world in server mode
// EN/ServerManager.cs:48,221-243
public static bool LocalServer => string.IsNullOrEmpty(Args.UnixDomainSocketPath);
public static void StartServer() {
    if (!LocalServer) { private_StartServer(UnixDomainSocketPath, ..., args.Port ?? 51737, args.Tunneling) ... } // production dedicated server
    else LocalAnubisManager.Start();          // fire-and-forget; Lobby+Controller+Finder, no client world
    ...
}
```

So launching the APK with `-Server` (and probably `-Headless`) turns a device or emulator into a **pure
LocalAnubis host**, with server worlds spawned on demand. This is the natural basis for a "dedicated host"
(for example an Android emulator on a PC or VPS). Caveat: `LocalAnubisManager.Start()` is fire-and-forget (`async
void`). A Federation failure there is an unhandled exception and a likely crash (section 9).

### 2.3 `AllowP2P` **[V]**

* `Args.AllowP2P` (`EN/GameApplication.cs:116`) is read only in `ServerComponent.OnCreate`. It sets
  `World.Root.AllComponents.OfType<P2PServerComponent>().FirstOrDefault().Enabled = true`
  (`EN/ServerComponent.cs:210-213`), which throws NullReferenceException if the server world has no such component.
  Server worlds in Embedded mode are created **while `GameApplication.Args` is swapped for the game config's
  `cmd_args`** (section 4.3), so the switch that matters is `-AllowP2P` **inside the lobbyconfig `cmd_args`**, not the
  client's own argument.
* `ClientSettings.AllowP2P` (`EN/ClientSettings.cs:83-84`) is only copied around (`:359`) and set by the verb
  (`EN/P2PServerComponent.cs:69`). **Nothing reads it.** `ClientSettings.AutomatchEmbeddedDelayMS` (`:137`) is also
  never read.

### 2.4 LocalAnubis arguments (all are `GameApplication.Arguments` fields, `EN/GameApplication.cs:44-170`) **[V]**

| Arg | Type | Used at | Effect / caveat |
|---|---|---|---|
| `-ServerLocation <enum>` | enum (case-sensitive `Enum.Parse`) | `GM/LinkSettingsInitializerComponent.cs:33-35`, `EN/ClientComponent.cs:246` | `Embedded` = host locally. `Remote` = use a Finder elsewhere (needs patch P3 to choose where) |
| `-AnubisCommand <enum>` | enum | `EN/ClientComponent.cs:248` | **Must be `QuickLaunch`** when setting `-ServerLocation` |
| `-LocalAnubisHost <ip>` | string → `IPAddress.Parse` | `EN/LocalAnubisManager.cs:44-54` | Bind address of Lobby and Controller. **Any non-loopback value makes `PublicHost` an IP address, and the Finder then rejects the client's own requests (Host header ≠ `localhost`)** (section 6). As shipped, only loopback values are safe |
| `-LocalAnubisLobbyPort <int>` | int? | `EN/LocalAnubisManager.cs:121` | Fixed Lobby port (default ephemeral). Safe |
| `-LocalAnubisControllerPort <int>` | int? | `LA/Lobby.cs:43` | Fixed Controller port (default ephemeral). Safe |
| `-LocalAnubisFinderPort <int>` | int? | `EN/LocalAnubisManager.cs:123` | Changes the Finder's listen port **only**. Clients keep using `ClientSettings.LocalAnubisFinderPort = 51750`, so changing it breaks matchmaking unless templates or patches follow |
| `-SuperCredential`, `-SuperPassword` | string | `EN/LocalAnubisManager.cs:84-106`, `EN/AuthenticationComponent.cs:312-315` | Needed for the LocalAnubis session and the server worlds. The value contains `:`, so pass it via `args.txt` or `cmd_args`, or ship `ServerInfos.json` `{"SuperCredential":"game:…","SuperPassword":"…"}` (`GM/GameAuthenticationComponent.cs:18-33`) |
| `-DeviceId`, `-DataCenter` | string | `EN/LocalAnubisManager.cs:28,32` | LocalAnubis session device id (default `"default"`) and datacenter |
| `-EnvironmentSwitcherUrl` | string | `EN/LocalAnubisManager.cs:36` → `OnlineLibsSettingsComponent` | eve base URL. Cannot go through `phantomArgs` (report 01) |
| `-AllowP2P` | bool | `EN/ServerComponent.cs:210` | See 2.3 |
| `-Server`, `-Headless`, `-ServerType`, `-Level` | | `EN/GameApplication.cs:1216-1231`, `EN/ServerComponent.cs:209,760`, `GM/GameServerComponent.cs:1222` | Dedicated mode, server type, forced level. **Inside `cmd_args` these configure the in-process server worlds** |

Unused `Arguments` string fields that are free to repurpose in IL patches (0 references in C#): `RemoteIp`,
`GSRemoteIp`, `BotHostName`, `ClientMode`, `GSClientMode`, and int `GSPort`. They are parsed automatically by
`ApplicationArguments.Parse` (`EN/ApplicationArguments.cs:25-89`, `-Name value` tokens, `\n`-separated, field names
case-insensitive). An IPv4 address has no `:`, so `-RemoteIp:192.168.1.23` works through `phantomArgs`. **[V]**

### 2.5 Things that do NOT enable it **[V]**

* No CRM/remote-config key mentions LocalAnubis, Embedded or P2P (grep across all assemblies).
* No in-game debug menu sets `ServerLocation`. The only readers are `LinkSettingsInitializerComponent`,
  `ProfileModifierComponent` (disables itself when Embedded, `GM/ProfileModifierComponent.cs:26`) and
  `ShouldPlayTutorial`.
* No build flag: the `[Component("Network/P2P/...", false)]` second argument is `Hidden = false`
  (`EN/ComponentAttribute.cs:13`).
* Java side: no reference to `phantomArgs`, LocalAnubis or ServerLocation (grep `decompiled-java/sources`).
* `ServerLocation.SSH` (defaults `alpha01.mdc.gameloft.org`, `opt/data/gameserver/games`,
  `EN/ClientSettings.cs:86-111`), `Tunneling`, `Args.RemoteIp/GSRemoteIp/ClientMode/GSPort` are dead dev remnants
  with no implementation.

---

## 3. End-to-end sequence: single device, `-ServerLocation Embedded -AnubisCommand QuickLaunch` (TS link) **[V]**

```
CLIENT WORLD                                LOCALANUBIS (same process)                FEDERATION (stub)
ClientComponent.Connect(link)               
 Prepare: Authentication.Connect ─────────────────────────────────────────────────────► eve/pandora/auth (anon session)
 Prepare: LocalAnubisManager.Start ──► new Federation(clientId, eveUrl, DataCenter, 60s, 3, HTTPS)
                                         .OpenSession(SuperCredential, SuperPassword, DeviceId,
                                                      scopes ["auth","auth_admin_ro"]) ──────► authorize, users/me
                                       Lobby  TcpListener(127.0.0.1, 0).Start(20)
                                       Controller TcpListener(127.0.0.1, 0).Start(20)
                                       Finder HttpListener "http://localhost:51750/"
 GetRoom: QuickLaunch ──HTTP POST http://localhost:51750/rooms/{clientId}/quick_launch
          body: server_type=ts&access_token=<anon token, scope lobby>
                                       ParseUser → Session.Verify(access_token) ──────────────► GET auth/verify?access_token=…
                                       GameConfig.FindGame("ts") ─────────────────────────────► GET matchmaker lobbyconfigs/{cid}/ts
                                       Lobby.QuickLaunch → MakeRoom + LaunchRoom
          ◄── {"action":"launch game","room_id":"LocalAnubis-Room-<guid>",
               "controller_host":"localhost","controller_tcp_port":<ephemeral>,"success":true}
 ConnectToServer (ClientLink.ConnectToServer)
  TCP connect localhost:<ctrl port>  ◄── {"action":"create connection","nonce":"LocalAnubisNonce","success":true}\n
  encrypt_token(nonce, scope lobby) ──────────────────────────────────────────────────────► GET auth/encrypt_token
  ──► {"action":"login","access_token":"<enc>", <RoomUser JSON>}\n
                                       Session.Verify(enc, "LocalAnubisNonce", "system:Anubis") ► GET auth/verify?…&nonce&credential
  ◄── {"action":"login","success":true}\n
  ──► {"action":"connect game","room_id":"LocalAnubis-Room-…"}\n
                                       Controller.DoConnectGame (serialized by a semaphore):
                                        GameConfig.FindGame(room.server_type) ──────────────────► lobbyconfigs again
                                        swap GameApplication.Args := parse(cmd_args)
                                        ServerManager.OnRoomInfo(room) ; AddClientAsync(...)
                                          new World(headless, server) ; Worlds.Add
                                          RefreshRoom → OpenGameServerSession ───────────────────► authorize (game cred), users/me
                                             → Lobby TCP login as game server (scope lobby_admin) + "get room info"
                                          InitWorld → GetStartLevelArguments → LoadLevel
                                          world.Server.AddClient(fedId, credential, GameSocket)
                                        restore Args
  ◄── {"action":"connect game","slot":"<room_id>","success":true}\n   (then the socket switches to game framing)
 Authentication.OpenAnonymousSession, Handshaking:
  control "Handshake" ──────────────►  server World Connection.ProcessHandshake
                                        OpenSessionForUser(credential, …) ──────────────────────► authorize for_username, storage, config…
                                        GameServerComponent.Handshake → LoadPlayerProfile ──────► storage (report 03)
  OpenChannel(Reliable/Unreliable): StartEnetSocket → server ENet StartServer(20, port 0) → reply {"port":N}
  ENet connect <TCP peer IP>:N ; OpenChannel → {"channelId","socketId"}
```

Evidence: `EN/ClientComponent.cs:754-783,987-1062,1095-1139`; `EN/ClientLink.cs:257-283,398-412`;
`OP/MatchmakerApi.cs:10-17,66-73`; `LA/FinderHTTPMessageHandler.cs:143-166`; `LA/Lobby.cs:177-183,195-212,238-258`;
`LA/BaseServer.cs:358-363,406-429`; `EN/AnubisClientSocket.cs:34-84`; `LA/Controller.cs:71-98`;
`EN/ServerManager.cs:100-202`; `EN/ServerComponent.cs:584-610,656-694,734-755`; `EN/Connection.cs:983-1045,1347-1378,1551-1600`.

The guest's gameplay ("gs") match goes through the same path: automatch requests reach the TS world, which calls
the Finder (`/automatch`), and then the client connects to the gs controller (section 4.4).

---

## 4. Protocol reference

### 4.1 Framing on the Lobby/Controller TCP sockets **[V]** (`EN/MessageReader.cs:24-45,55-174`, `LA/BaseServer.cs:455-458`)

* **Anubis messages**: compact JSON + `\n`, UTF-8. The reader treats any frame whose first byte is not `0xBB` as an
  Anubis line and splits on `0x0A`.
* **Game messages** (after `connect game`): 6-byte header `0xBB, type, int32 little-endian length`, then the body.
  `type`: `0 = Default` (game data), `1 = Control` (JSON control requests and replies), `2 = Anubis`. Maximum 1 MiB.
* The server sends a greeting immediately on accept, before reading anything:
  `{"action":"create connection","nonce":"LocalAnubisNonce","success":true}` (`LA/BaseServer.cs:173,358-363`).
  The nonce is a constant: there is no replay protection.
* Every request needs `"action"`. Unknown actions get `{"action":"<x>","success":false}`
  (`LA/BaseServer.cs:373-387`). `AnubisClientSocket.SendAnubisRequest` throws `AnubisRequestFailedException` when
  `success` is not true (`EN/AnubisClientSocket.cs:51-66`). Client receive timeout: **60 s**
  (`EN/AnubisClientSocket.cs:17`).

### 4.2 `login` (Lobby and Controller) **[V]** (`LA/BaseServer.cs:406-429`, `OP/../Gameloft.OnlinePlatform.Lobby/LobbyApi.cs:10-20`)

Request = `RoomUser` JSON plus `action` and `access_token`:
```json
{"credential":"anonymous:abc","fed_id":"9f2c…","client_id":"2620:66102:1.17.32:android:googleplay",
 "name":"agent-123", "...custom fields...": "...",
 "action":"login","access_token":"<output of auth/encrypt_token(nonce, scope lobby|lobby_admin)>"}
```
Server side:
1. `Session.Verify(access_token, "LocalAnubisNonce", "system:Anubis")` → `GET {auth}/verify?access_token=…&nonce=LocalAnubisNonce&credential=system%3AAnubis`
   (`OP/Session.cs:357-364`). The response must contain `credential`, `account` and `client_id`. A failure throws,
   which ends the connection.
2. Builds `RoomUser{credential, fed_id=account, client_id}`, **then copies every request key except
   `action, access_token, username, override_ip_geolocation, nonce_type` over it** (`username` → `name`). So the
   client-supplied `credential/fed_id/client_id` overwrite the verified values: identity is effectively asserted by
   the client.
3. Response: `{"action":"login","success":true}`.

Scope used by the client for `encrypt_token`: `Session.IsGameServer ? "lobby_admin" : "lobby"` (`EN/AnubisClientSocket.cs:80`).

### 4.3 Controller actions **[V]** (`LA/Controller.cs:27-106`)

| action | request | response | side effects |
|---|---|---|---|
| `login` | 4.2 | `success` | sets context RoomUser |
| `connect game` | `{"room_id":"…"}` | `{"action":"connect game","slot":"<room_id>","success":true}` | Runs `DoConnectGame` under a one-at-a-time semaphore (`NonConcurrentTask`, `UT/NonConcurrentTask.cs`). Looks the room up in **this process's** `Lobby.Rooms` (an unknown id gives NullReferenceException and the socket closes). Calls `GameConfig.FindGame(room.ServerType)`. Converts `game.cmd_args` with `GetConfigArgs`, **replaces `GameApplication.Instance.Args` by reflection** for the duration, calls `ServerManager.OnRoomInfo(room)` and `ServerManager.AddClientAsync({credential, fed_id, room_id, slot=room_id}, GameSocket)`, restores Args, and after the reply is sent calls `SwitchToGameSocket()` (the same TCP socket becomes the game connection) |

`GetConfigArgs` (`LA/Controller.cs:100-106`):
```csharp
string[] source = gameConfig.CmdArgs.Split(new[]{" -","-"}, RemoveEmptyEntries); // splits on EVERY '-'
source = source.Select(s => "-" + new Regex(" ").Replace(s, "\n", 1)).ToArray();  // first space -> newline
return string.Join("\n", source);
```
⇒ `cmd_args` **must be non-null** (NullReferenceException otherwise), and values **must not contain `-` or
spaces**. Example: `"-ServerType ts -DataCenter local"` becomes `-ServerType\nts\n-DataCenter\nlocal`.

`ServerManager._AddClientAsync` (`EN/ServerManager.cs:146-184`): if a world exists for this slot (room id), it
reuses it. Otherwise it creates `new World(headless:true, createSceneComponents:true, server:true)`, adds it to
`GameApplication.Worlds`, then runs `AddRoomInfo` (cached room → `RefreshRoom` → `Lobby` TCP `get room info` as
the game server) and `world.Server.InitWorld(room)` (level load). Then
`world.Client.Enabled=false; world.Server.Enabled=true; world.Server.AddClient(fedId, credential, socket)`.
**All exceptions are swallowed** (`:178-183`): a failed world creation still answers `connect game` with
success, and the client then times out in its handshake (default control timeout 20 s, `EN/ClientSettings.cs:81`).

Disconnects: if a room user's controller socket drops while `game_started`, `ScheduleDeleteRoomUser` waits
`reconnect_grace_period` seconds (default 10), then calls `Room.Leave` and deletes the room if it is empty
(`LA/Controller.cs:44-63`, `LA/BaseServer.cs:399-404`, `LA/Lobby.cs:150-161`). An empty server world destroys
itself 10 s after its last connection closes (`EN/ServerComponent.cs:88,405-448`).

### 4.4 Lobby actions **[V]** (`LA/Lobby.cs:54-76` and handlers)

All requests other than `login` need the context to be logged in (`RoomUser` set). Responses always echo
`action` and carry `success`, plus `reason` on "not found" errors.

| action | request fields | response fields | notes (file:line) |
|---|---|---|---|
| `create room` | `server_type` (req), `capacity`, `timeout`, `reservation_codes[]`, any extra keys | `room_id` | `GameConfig.FindGame(server_type)`; capacity is capped at `room_capacity`; extra keys (including `timeout`) are copied into the room (`:214-236,413-420`). Id `LocalAnubis-Room-<guid>` |
| `create team room` | same | `room_id` | id `LocalAnubis-TeamRoom-<guid>` (`:422-428`) |
| `join room` | `room_id`, (`reservation_code` ignored) | – | `AddMember` with **no capacity, duplicate or reservation check**; FindGame again (`:430-444`) |
| `get room info` | `room_id` | `room` (full room JSON) | (`:518-527`) |
| `update room` | `room_id`, any keys | – | every key except `action, room_id` is set on the room (`:476-492`) |
| `update user` | `room_id`, optional `credential`, keys | – | updates a member by credential/fed_id, else the context user (`:494-516`) |
| `launch game` | `room_id` | `room_id, controller_host, controller_tcp_port` | `LaunchRoom`: sets `controller_host = Controller.PublicHost`, `controller_tcp_port`, `game_started=true`, `"launch_date "` (sic, trailing space) (`:177-183,238-250`) |
| `get game controller` | `room_id` | `room_id, controller_host, controller_tcp_port` | (`:400-411`) |
| `leave room` | `room_id`, optional `credential` (= kick) | – | (`:446-463`) |
| `delete room` | `room_id` | – | closes every lobby socket in that room (`:131-148,465-474`) |
| `reserve` / `remove reservation` | `room_id`, `reservation_codes[]` | – | (`:529-551`) |

Housekeeping (`LA/Lobby.cs:98-129`, every 1 s): rooms that are not started get deleted after `timeout` seconds of
inactivity (`room_timeout`, default 600) or after `launch_timeout`. A lobby socket that disconnects before game
start removes its user after the grace period (`:78-96`).

Client wrappers: `EN/Lobby.cs` (`Login(host,port)`, `CreateRoom` → `get room info`, `GetRoomInfo` also calls
`get game controller` when `game_started`, `EN/Lobby.cs:57-72,206-218`), JSON builders in `LobbyApi.cs:10-255`.

### 4.5 Finder HTTP API **[V]** (`LA/FinderHTTPMessageHandler.cs:28-211`, `OP/MatchmakerApi.cs:10-64`)

Base: `http://localhost:51750/` (the `Host` header must be `localhost[:51750]`, see section 6). Parameters come from
the query string for GET and from an `application/x-www-form-urlencoded` body for POST (`:114-128`).

| Method + path | Params | Response | Client caller |
|---|---|---|---|
| `GET /locate?service=lobby\|controller` | `service` | `text/plain` `"<PublicHost>:<port>"` (e.g. `localhost:40123`) | `ClientLink.LocateLocalLobby` (`EN/ClientLink.cs:448-458`) |
| `GET /rooms` (no client id, no filters) | – | JSON array of **all** rooms (debug dump) | – |
| `GET /rooms/{client_id}` | `server_type`, `limit`, filters = **every other query key** (except `server_type, limit, access_token`) | JSON array of room objects matching `client_id`, `server_type` and all filters | `MatchmakerService.FindRooms` (no scope, no token) (`OP/MatchmakerApi.cs:55-64`) |
| `POST /rooms/{client_id}/quick_launch` | `server_type`, `access_token` (scope `lobby`), optional `user` (RoomUser JSON) | `{"action":"launch game","room_id","controller_host","controller_tcp_port","success":true}` (a new, already launched room each call) | `ClientLink.QuickLaunch` (`EN/ClientLink.cs:398-412`) |
| `POST /rooms/{client_id}/automatch/{automatcher}` | `server_type`, `access_token`, `user`, `filter` (url-encoded `k=v&…`), `score`, `score_min_range`, `score_max_range`, `score_automatch_enabled`, `fill`, `midgame_join`, `instant`, `region_isolation`, `country_geolocation`, `group_automatch_by_team_capacity`, `team_room_id`, `default_room_kwargs` (JSON), `timeout`, `joins`, `country`, `max_tags` | room JSON (an existing match, or a new room, launched immediately unless `automatch_wait_launch=false`) | `MatchmakingComponent.DoAutomatch` running **in the TS server world** for the player (`EN/MatchmakingComponent.cs:361-381`) |

`ParseUser` (`:143-156`) calls `Session.Verify(access_token)` → `GET {auth}/verify?access_token=…`. It needs
`credential`, `account` and `client_id` in the response (`RoomUser.FromAccountInfo`, `OP/RoomUser.cs:151-154`), then
merges the optional `user` JSON over it.

Errors: `HTTPException` → its status (404 page text). `ArgumentException` (a missing mandatory parameter, and also
`ArgumentNullException`) → 401 with text `"(401 Unauthorized : <param> argument must be supplied"`.
`IOnlineRequestException` → 401 with its message. **Any other exception is rethrown to `HTTPServer.HandleMessage`,
which writes the stack trace, flushes, and *then* sets `StatusCode = 500`** (`EN/HTTPServer.cs:81-100`). Mono
throws `InvalidOperationException("Cannot be changed after headers are sent.")` from that setter (verified in
`extracted/assemblies/System.dll` `HttpListenerResponse.StatusCode`). That exception escapes a fire-and-forget
`async void` and is fatal (section 9). **[V code / L runtime]**

`GET /` should print `"Welcome to <b>local</b> Anubis matchmaker!"`, but it is unreachable: `"/".Split('/')` has
length 2, so the request gets the 404 page (`:34-88`).

Lobby automatch algorithm (`LA/Lobby.cs:260-364`), condensed:
* **Settings** (`LA/AutomatchSettings.cs:62-102`): read from `GameConfig` and the named `Automatcher`, with query
  values taking precedence. `GetAutomacher(name)` uses `.First(...)`, so an **unknown name throws** (→ fatal path
  above). The name `"default"` skips the lookup. Filters always get `full=False`, and `game_started=False` unless
  `midgame_join`.
* **Team members** (a `team_room_id` whose owner is not this user): loop every 1 s until a room with
  `reservations ∋ team_room_id` and `tags ∋ team_room_id` exists, then join it and consume one reservation.
* **Candidates** = rooms with the same `client_id` and `server_type` matching all filters, then (if score
  automatch) `|matchmake_score − score| < score_max_range`, ordered by distance. Join the first, or create a new
  room: `automatcher`, `automatched=true`, `matchmake_score/time`, then `default_room_kwargs` **or** (if absent)
  every filter key/value copied as a room attribute. A team leader reserves `teamSlots−1` and tags the room. If
  `AutomatchWaitLaunch` (default true), the room is launched immediately.

  ⇒ A second human joining a launched room requires `midgame_join: true` (in the automatcher config or the
  request), otherwise each player gets a separate room.
* The game's `GameMatchmakingComponent.MakeAutomatchSettings` adds filters `_noob_<id>=0|1`, `_team`, `_closed=0`,
  `_matchOver=0`, `capacity=<MaxPlayers>`, plus score and country (`GM/GameMatchmakingComponent.cs:1042-1100`).
  **Players in different noob buckets or MMR ranges never share an automatch room.** For friends, use the team-room
  code flow or private match instead.

### 4.6 Filter semantics **[V]** (`LA/BaseFilter.cs:21-67`, `LA/CustomFilter.cs:22-43`, `OP/RoomFilter.cs:107-114`)

Built-in keys: `id` (==), `exclude_id` (≠ id), `name`, `game`, `full` (bool), `automatched` (bool),
`game_started` (bool), `reservation` (∈ `reservations`), `tag` (∈ `tags`), `max_tags` (count(tags) < n),
`available_slots` (≥ n). Any other key is a `CustomFilter` on the room attribute of the same name, compared as a
string (`"True"`, `"0"`, `"TDM"`…). A missing attribute matches only when the value starts with `!`. A value
containing `,` is an OR list.

Bugs: the `!` prefix is never stripped (so `!x` almost always matches), and range filters `a-b` call
`double.Parse(Groups[0])` = `"a-b"`, which throws FormatException (→ fatal path). The game's
`randomPseudoFilter=!<ticks>` cache-buster therefore always matches, which is fine (`EN/ClientLink.cs:416`).

### 4.7 Room JSON produced by LocalAnubis **[V]** (`OP/Room.cs:363-399`, `LA/Lobby.cs:177-212`)

```json
{
  "members": [ {"credential":"anonymous:abc","fed_id":"9f2c…","client_id":"2620:…","name":"…", "...":"..."} ],
  "game_started": true,
  "id": "LocalAnubis-Room-6f1c…",
  "server_type": "gs",
  "capacity": 6,
  "owner": { "...RoomUser..." },
  "client_id": "2620:66102:1.17.32:android:googleplay",
  "lobby_host": "localhost", "lobby_tcp_port": 40123,
  "member_count": 1, "available_slots": 5, "full": false, "empty": false,
  "created ": "2026-10-24T12:00:00Z",          // note trailing space in key (bug)
  "_local": true, "timeout": 600, "launch_timeout": 0,
  "controller_host": "localhost", "controller_tcp_port": 40125,
  "launch_date ": "2026-10-24T12:00:01Z",      // trailing space (bug)
  "reservations": ["…"], "tags": ["…"],
  "automatcher": "dev", "automatched": true, "matchmake_score": 600, "matchmake_time": 1790000000,
  "_noob_1": "0", "_closed": "0", "_matchOver": "0", "capacity": "6"   // copied filters (strings)
}
```
`Room.Id` falls back to `room_id` (`OP/Room.cs:86-106`), which is why the quick-launch reply works as a room.

### 4.8 `lobbyconfigs` – what `GameConfig.FindGame` expects **[V]** (`LA/GameConfig.cs:17-57`, `LA/Automatcher.cs`, `OP/MatchmakerApi.cs:66-73`)

Request: `GET {matchmaker}/lobbyconfigs/{clientId}/{serverType}?access_token=<LocalAnubis super session token, scope lobby_game_admin>`.
Response (JSON object). The values below are the code defaults, except where marked:
```json
{
  "game": {
    "server_type": "gs",
    "room_capacity": 6,                 // default 5 – set to the game's MaxPlayers (gs) or 1 (ts)
    "max_tags": 2,
    "room_timeout": 600,
    "room_launch_timeout": 0,           // KEEP 0: >0 triggers DateTime.Parse(null) crash (see §9)
    "reconnect_grace_period": 10,
    "socket_timeout": 10,               // TCP keep-alive seconds
    "midgame_join": true,               // recommended so several humans share an automatch room
    "room_info_on_start": true,         // read nowhere
    "cmd_args": "-ServerType gs"        // REQUIRED (non-null). No '-' or spaces inside values.
  },
  "automatchers": {
    "a1": {"name": "dev", "midgame_join": true, "instant": false, "automatch_wait_launch": true,
           "score_automatch_enabled": false, "fill": 6, "region_isolation": false,
           "country_geolocation": false, "group_automatch_by_team_capacity": false}
  }
}
```
* `cmd_args` arrive over HTTP/JSON, so they **can** contain `:`. Use them to give the in-process server worlds
  everything they would otherwise read from the swapped Args: `-ServerType ts|gs`, `-EnvironmentSwitcherUrl
  http://127.0.0.1:18080/` (no `-` in the URL!), `-DataCenter local`, `-SuperCredential game:mcvs`,
  `-SuperPassword x`, `-DeviceId mcvsGameServer`, optionally `-AllowP2P`, and **for the TS** probably `-Level <TS
  level path>`. `GameServerComponent.GetStartLevelArguments` otherwise falls through to a PvP map selection
  (`GM/GameServerComponent.cs:1191-1260`). **[L]** The exact production `cmd_args` and the TS level path are
  unknown (they are in template data / server config).
* The automatcher **name** must match `MatchmakingComponent.Automatcher` from the templates (code default `"dev"`,
  `EN/MatchmakingComponent.cs:244`). Unknown until the data is available. Returning several common names is a
  cheap hedge.

---

## 5. What still calls Gameloft (i.e. what the Federation stub must answer) in LocalAnubis paths **[V]**

| Call | Who | Endpoint (via eve → pandora) | Expected answer |
|---|---|---|---|
| `Federation.OpenSession(SuperCredential, …, ["auth","auth_admin_ro"])` | `LocalAnubisManager.Start` (`EN/LocalAnubisManager.cs:117`) | eve config, pandora locate, `POST auth/authorize` (`credential_type=game`), `GET auth/users/me` | report 02 §3 |
| `Session.Verify(token, nonce, "system:Anubis")` | every Lobby/Controller `login` (`LA/BaseServer.cs:410`) | `GET auth/verify?access_token&nonce&credential` | `{"credential","account","client_id","scope"}` |
| `Session.Verify(token)` | Finder quick_launch/automatch (`LA/FinderHTTPMessageHandler.cs:146`) | `GET auth/verify?access_token` | same |
| `GameConfig.FindGame` | create room, create team room, join room, quick launch, automatch, connect game (`LA/Lobby.cs:217,439`, `LA/FinderHTTPMessageHandler.cs:162`, `LA/AutomatchSettings.cs:65`, `LA/Controller.cs:74`) | `GET matchmaker/lobbyconfigs/{cid}/{st}` (scope `lobby_game_admin`) | §4.8 |
| `Session.EncryptedAccessToken(nonce, lobby\|lobby_admin)` | every client/server lobby login (`EN/AnubisClientSocket.cs:80`) | `GET auth/encrypt_token?nonce&access_token` | text/plain token |
| `Session.GetAccessToken([...])` | each scoped Finder request (`OP/FedRequestMessage.cs:164-171`) | `POST auth/authorize` (cached per scope set) | report 02 |
| `OpenGameServerSession` / `OpenSessionForUser` / profile / CRM | in-process server worlds (`EN/ServerComponent.cs:620-624,663-667`, `EN/Connection.cs:1564`, `EN/ServerComponent.cs:361-377`) | auth, storage, config, alert, leaderboard, game_stats … | reports 02/03 |

So **no Gameloft host is contacted directly** by LocalAnubis: everything resolves through `EnvironmentSwitcherUrl`.
Point that at the stub and add `verify`, `encrypt_token` and `lobbyconfigs`. Today, against real eve, the first
step already fails (eve answers 404 "client_id not configured", report 01). `Prepare` then throws and the client
shows a connection error.

---

## 6. Can a remote device on the LAN discover and join a host? (as shipped: **no**)

1. **Finder is loopback-only.** `Finder..ctor` passes `prefix = "localhost"` (IL `Finder::.ctor IL_0009 ldstr
   "localhost"`; source `LA/Finder.cs:10-14`, `EN/HTTPServer.cs:23-33`). In Mono's `EndPointManager.GetEPListener`,
   a non-IP host is resolved with `Dns.GetHostByName`, so it binds **127.0.0.1:51750**. `EndPointListener.SearchListener`
   only dispatches when the request URI host (the `Host` header) equals the prefix host `"localhost"`; there are
   no `*`/`+` prefixes to fall back on (decompiled `extracted/assemblies/System.dll`). **[V]**
2. **Lobby/Controller bind `LocalAnubisManager.Host`**: `Args.LocalAnubisHost` or `IPAddress.Loopback`
   (`EN/LocalAnubisManager.cs:44-54`, `LA/BaseServer.cs:197-202`). **[V]**
3. **Advertised address (`PublicHost`)** (`EN/LocalAnubisManager.cs:75-82`):
   `IsLoopback(Host) ? "localhost" : Dns.GetHostEntry(Dns.GetHostName()).AddressList.First(IPv4)`. On Android,
   `gethostname()` is normally `"localhost"`, so the non-loopback branch very likely returns **127.0.0.1**.
   **[L]** (verify on a device)
   * Consequence A: with `-LocalAnubisHost 0.0.0.0`, rooms advertise `127.0.0.1`. Local play still connects; remote
     clients connect to themselves.
   * Consequence B: with `-LocalAnubisHost <LAN IP>`, rooms advertise `127.0.0.1` but nothing listens on loopback,
     so even the host's own connection breaks. **[L]**
   * Consequence C: in both cases `ClientSettings.LocalAnubisFinderHost` (= `PublicHost`, `EN/ClientSettings.cs:353`)
     becomes an IP address, so the client's own Finder requests carry `Host: 127.0.0.1:51750` and are rejected by
     the `localhost` prefix (point 1). **[V code / L runtime]** ⇒ `-LocalAnubisHost` is unusable without patches.
4. **There is no argument to target another device's Finder.** `LocalAnubisFinderHost` defaults to the local
   `PublicHost`, and `LocalAnubisFinderPort` is a hard-coded 51750 (`EN/ClientSettings.cs:353-355`). **[V]**
5. **No discovery** is wired up. The Finder has no broadcast, and the only broadcast code (`P2P.cs`) is the dead
   prototype in section 7. **[V]**
6. **Positive points:** the TCP JSON lobby protocol, room ids and controller hand-off are host-agnostic.
   `TcpSocket.ConnectAsync` resolves any host name or IP (`EN/TcpSocket.cs:185-191`). The ENet server binds
   any-address (16 zero bytes loaded from `.rodata 0x12cfd50` into `ENetAddress.host`, port = argument; disassembly
   of `EnetSocket_StartServer_private @0x392be0` in `extracted/lib/arm64-v8a/libnativeengine.so`). The ENet client
   connects to the IP of the existing TCP peer (`RemoteEndPoint.GetIp()`, `EN/Connection.cs:1003`). Once
   points 1-4 are patched, **a remote device can use the host's Lobby/Controller and join the host's in-process
   server worlds with no other code changes**. **[V]**

---

## 7. `P2P.cs`, `P2PServerComponent`, `P2PClientComponent`, `P2PRoom`: what they do **[V]**

* **`P2P` (static)** (`EN/P2P.cs`):
  * `OnDiscovering` subscribers cause a `UdpClient(50034)` with broadcast enabled to be opened. Every datagram
    equal to `"Discovering"` raises `OnDiscovering(socket, remoteEP)` (`:119-169`).
  * `OnRoomDiscovered` subscribers cause a `UdpClient(0)` (ephemeral port) to be opened. Any datagram that is not
    `"Discovering"` is parsed as Room JSON and raises `OnRoomDiscovered(new Room(json))` (`:171-216`).
  * `Discover()` sends `"Discovering"` to `255.255.255.255:50034` from that ephemeral socket (`:69-73`).
  * All exceptions are swallowed.
* **`P2PServerComponent`** (server world; enabled only through `Args.AllowP2P`, see 2.3) (`EN/P2PServerComponent.cs`):
  * It answers each probe with
    `new Room(controllerHost: LocalAdress, controllerPort: -1, id: "P2P:" + MachineName, name: MachineName)`. The
    members are the credentials of the current `Server.Connections`, plus `room["Level"] = World.CurrentLevelPath`
    (`:30-45`).
  * `LocalAdress` comes from a route trick: connect a UDP socket to `10.0.2.4:65530` (an Android-emulator subnet)
    and read the local endpoint (`:23-28`). On a real device this picks the interface of the default route.
  * `Environment.MachineName` is `"localhost"` on Android **[L]**, so every host gets the same name.
  * `[Verb] StartServer()` builds a client link `P2P:<machine>` with `Embedded`, `LoadFromCurrentLevel` and
    `AllowP2P`, then calls only `ConnectLink` (`:59-75`).
* **`P2PClientComponent`** (`EN/P2PClientComponent.cs`): re-sends `Discover()` every 1000 ms (`:42-58`). It keeps
  `List<P2PRoom>` keyed by `ControllerHost`, with optional 30 s expiry (`AutoRefresh`), and offers `[Verb] Refresh`.
  The list is `[Browsable]`, i.e. editor property-grid UI. There is no game UI.
* **`P2PRoom.Connect()`** (`EN/P2PRoom.cs:30-38`): creates a link with `ServerLocation.P2P` and
  `Room = discoveredRoom`, then calls `ConnectLink` only.

Why it cannot work as shipped:
1. `ConnectLink` only registers the link (`EN/ClientComponent.cs:455-467`). Nothing calls `Connect(link)`.
2. If it were connected, `GetRoom` with the default `AnubisCommand.None` would null the room (`:1021-1023`).
3. Even with a room, `controller_tcp_port = -1`, and `ServerLocation.P2P` has no case in `Prepare` and no handling
   anywhere else.
4. Whether the `P2PServerComponent`/`P2PClientComponent` are present in `SceneComponents.tx` is unknown (data).

**Authority:** whoever runs the server World. There is no NAT punch-through, no relay, no lobby federation, no host
migration, and no authentication beyond the Federation `verify` used by the Lobby/Controller. Android also lacks
`CHANGE_WIFI_MULTICAST_STATE`, so the app cannot take a `MulticastLock`. Some devices filter broadcast reception
without one **[S]** (manifest permission list in `decompiled-java/resources/AndroidManifest.xml`).

Reusable parts: the UDP-broadcast idea and port 50034, and the route trick for the LAN address.

---

## 8. Game transport after `connect game` **[V]**

* **TCP control and data**: the controller socket itself (`LA/BaseServer.cs:102-110`, `EN/ClientLink.cs:257-283`),
  framed as in 4.1. Ping/keepalive: client `PingRecurringDelay` 10 s and `PingMaxRetries` 10
  (`EN/ClientSettings.cs:143-150`); server keepalive 4 s (`EN/ServerComponent.cs:131`).
* **ENet UDP**: when the client opens the Reliable/Unreliable channel, it sends a `StartEnetSocket` control
  request. The server answers `{"port": EnetSocket.Port}` after `StartServer(20, 0, 0)`, i.e. an **ephemeral port
  per server World** (IL: two call sites, each `ldc.i4.s 20; ldc.i4.0; ldc.i4.0` before
  `EnetSocket::StartServer`). The client connects ENet to `<TCP peer IP>:<port>`, then `OpenChannel` →
  `{"channelId","socketId"}` and an identification message (`EN/Connection.cs:983-1045,1347-1400`). Until the
  unreliable channel exists, traffic falls back to the reliable channel for `ChannelFallbackTimeout` 20 s
  (`EN/Connection.cs:371-395`).
* **Handshake** opens a per-player Federation session on the host (`EN/Connection.cs:1551-1600`) and returns
  `ServerClientId`/`ServerDlcSlot`. A version mismatch raises `ClientGameVersionIsGreaterThanServerGameVersionException`,
  so all peers must run the same build.

Ports a host must expose for one match:
* TCP: Finder 51750, Lobby (ephemeral, or `-LocalAnubisLobbyPort`), Controller (ephemeral, or
  `-LocalAnubisControllerPort`), and the Federation stub port if guests use the host's stub.
* UDP: one ephemeral ENet port per server World involved (TS worlds for Topology A guests, plus the gs world).

---

## 9. Bugs, stubs and fragility in the LocalAnubis code (to fix or avoid)

| # | Issue | Evidence | Impact / workaround |
|---|---|---|---|
| B1 | `HTTPServer.HandleMessage` sets `StatusCode=500` after writing and flushing the body | `EN/HTTPServer.cs:90-99` + Mono setter | Any unexpected Finder exception becomes an `InvalidOperationException` from an `async void` (`UT/MoreAsync.cs:68-77`), which `GameApplication.UpdateWorlds` rethrows (`EN/GameApplication.cs:1420-1428`): **likely app crash** [L]. Avoid unknown automatcher names and range filters; patch to set the status first |
| B2 | Room keys `"created "` and `"launch_date "` have a trailing space | `LA/Lobby.cs:182,207` | `Room.Created` is null; `room_launch_timeout>0` → `DateTime.Parse(null)` in `Update` → escapes the async-void update loop → crash [L]. **Keep `room_launch_timeout = 0`**. The private-match list sorts by `Created`, which is harmless (`Convert.ToDateTime(null)` = MinValue) |
| B3 | `CustomFilter`: `!` never stripped; the range regex uses `Groups[0]` | `LA/CustomFilter.cs:22-43` | NotEqual filters almost always match; ranges throw → B1 |
| B4 | `join room` has no capacity/duplicate/reservation handling | `LA/Lobby.cs:430-444` | Rejoins (AnubisCommand.Reconnect → JoinRoom) duplicate members; reservations never consumed |
| B5 | `GameConfig.GetAutomacher` uses `.First` | `LA/GameConfig.cs:39-46` | An unknown automatcher name → B1 |
| B6 | Global `GameApplication.Args` swap during `connect game` | `LA/Controller.cs:78-91` | The client World sees server args for the duration of a level load. Calls are serialized, but anything in the client world reading Args (e.g. `ServerLocation`, `Level`, `SuperCredential`) can misbehave [S] |
| B7 | `_AddClientAsync` swallows all exceptions | `EN/ServerManager.cs:178-183` | Silent failures → 20 s handshake timeout on the client. Add logging |
| B8 | `LocalAnubisManager.Start` is not re-entrant | `EN/LocalAnubisManager.cs:113-132` | A concurrent second Start re-binds 51750, fails, and `Stop()` tears down the first instance |
| B9 | `ServerManager.StartServer` uses fire-and-forget `LocalAnubisManager.Start()` | `EN/ServerManager.cs:234`, `EN/LocalAnubisManager.cs:108-111` | In `-Server` mode a Federation error crashes [L] |
| B10 | `-LocalAnubisFinderPort` not mirrored in `ClientSettings` | `EN/ClientSettings.cs:355` | Keep 51750 |
| B11 | Finder `GET /` unreachable | `LA/FinderHTTPMessageHandler.cs:34-88` | Cosmetic |
| B12 | BaseServer `connectionContexts` is a plain `List` used from accept, process and update tasks | `LA/BaseServer.cs:175,249-257,278-327` | Rare races [S] |
| B13 | `ServerComponent.OnCreate` with `AllowP2P` assumes a `P2PServerComponent` exists | `EN/ServerComponent.cs:210-213` | NullReferenceException → world creation fails silently (B7) |

---

## 10. P2P enablement recipe

### 10.1 Stage 0 – single device, local hosting (no IL patch in LocalAnubis)

Prerequisites (other reports):
* The Federation stub (report 02 §7.1) and storage (report 03 §11), reachable at `EnvironmentSwitcherUrl`
  (patched literal or `args.txt`, report 01).
* **Additional stub endpoints for LocalAnubis:**
  * `POST auth/authorize` for `credential_type=game` with scopes `auth auth_admin_ro` (LocalAnubis) and the server
    scope list (report 02 §3.4). Issue tokens for scopes `lobby`, `lobby_admin` and `lobby_game_admin` too.
  * `GET auth/encrypt_token?nonce=LocalAnubisNonce&access_token=T` → `text/plain` `T` (or a wrapped form).
  * `GET auth/verify?access_token=T[&nonce=…&credential=system:Anubis]` →
    `{"credential":"anonymous:…","account":"<fedid>","client_id":"2620:66102:1.17.32:android:googleplay","scope":"lobby"}`.
    It must succeed for client tokens, game-server tokens (`lobby_admin`) and SessionForUser tokens.
  * `GET matchmaker/lobbyconfigs/{cid}/ts` and `/gs` (plus any other server type a link uses) → §4.8 JSON with
    `cmd_args` set.
* Super credential: ship `ServerInfos.json` `{"SuperCredential":"game:mcvs","SuperPassword":"x"}` in a path
  `GlfStream` can read **[L]**, or put `-SuperCredential game:mcvs -SuperPassword x` in `args.txt`. Repeat them in
  `cmd_args`.

Arguments (phantomArgs or args.txt):
```
-ServerLocation Embedded -AnubisCommand QuickLaunch -DeviceId mcvsdev -DataCenter local
```
Expected result: the client QuickLaunches the TS on `localhost:51750`, an in-process TS world loads the menu
level, and the client connects and handshakes. Matches (automatch, private, team rooms) run in in-process gs
worlds. Bot fill comes from `GameMatchmakingComponent.EnableBotInLobby` (another report).

Debugging on a device: `adb forward tcp:51750 tcp:51750`, then `curl -H 'Host: localhost:51750'
'http://127.0.0.1:51750/rooms'` dumps all rooms. The Host header is required because of the `localhost` prefix.
To talk to the Lobby, `adb forward` its port (fix it with `-LocalAnubisLobbyPort`) and send JSON lines with `nc`.
You need a valid `encrypt_token` value for `login`.

### 10.2 Stage 1 – LAN, Topology A "host is the server for everyone" (fewest moving parts)

Every guest's TS **and** gs run on the host. Guests use the host's Federation stub, so there is one identity
authority, tokens are naturally consistent, and all profiles live in the host's storage (sync is a separate
feature).

Host:
* IL patches **P1** (Finder prefix `*`) and **P2** (real `PublicHost`), see 10.5.
* Args: `-ServerLocation Embedded -AnubisCommand QuickLaunch -LocalAnubisHost 0.0.0.0 -LocalAnubisLobbyPort 51760 -LocalAnubisControllerPort 51761 [-GSRemoteIp <advertised IPv4>]`
* The Federation stub listens on `0.0.0.0:<port>` and advertises (pandora `locate`) URLs with the host's LAN IP.
  Guests must be able to reach every service URL.

Guest:
* IL patch **P3** (`LocalAnubisFinderHost = Args.RemoteIp ?? PublicHost`), plus `EnvironmentSwitcherUrl` pointing
  at `http://<hostIP>:<stubport>/` (args.txt, or derived from `RemoteIp` by the same patch).
* Args: `-ServerLocation Remote -AnubisCommand QuickLaunch -RemoteIp <hostIP>`

Flow: the guest's TS QuickLaunch goes to the host Finder, which spawns a TS world for that guest on the host (a new
world per QuickLaunch, so the host runs N+1 TS worlds). Quick match: the guest's TS (on the host) automatches at
`host/port` = the guest's `LocalAnubisFinderHost` = the host IP, reaching the same Finder and the same gs room
(`midgame_join` true). ENet works on the LAN as-is. Discovery: a pre-launch picker (native companion `.so` or Java
launcher) that broadcasts/listens on UDP (e.g. reuse port 50034 with a new payload such as
`{"mcvs":1,"finder":"<ip>:51750","stub":"<ip>:<port>","name":…}`) and starts the game activity with
`phantomArgs = "-ServerLocation:Remote:-AnubisCommand:QuickLaunch:-RemoteIp:<ip>"`. Manual IP entry also works.

### 10.3 Stage 2 – LAN, Topology B "own TS and profile, shared match server" (matches the "progression saved locally" goal)

* Each device runs `Embedded` with its own stub. Guests keep LocalAnubis on loopback.
* Only the **match** links (quick/private/team) point at the host. Patch **P3b**: in
  `ClientLink.Automatch`/`QuickLaunch`/`FindRooms`/`FindTeamRooms`/`LocateLocalLobby`
  (`EN/ClientLink.cs:377-381,400-446,448-458`), substitute `Args.RemoteIp` when
  `Settings.ServerType != "ts"`. Automatch is executed by the guest's local TS world, but it uses the `host`/`port`
  the client sends (`EN/MatchmakingComponent.cs:402-412`), so patching the client side is enough.
* Federation interop is mandatory. The host's stub must `verify` guest-issued tokens, `authorize for_username` for
  guest credentials with the **same account id** the guest's stub uses, and serve and accept the guest's profile.
  Use self-describing tokens and deterministic accounts (report 02 §0.8), plus storage proxying to the guest's
  home stub (report 03 §14). Without that, the host's gs loads an empty or new profile for the guest, and the
  match-end save lands on the host.

### 10.4 Stage 3 – Internet

Ordered by effort:
1. **Overlay VPN (ZeroTier, Tailscale, WireGuard)**: identical to the LAN setup. Patch P2 must advertise the
   overlay IP (use the explicit `-GSRemoteIp` override). **Recommended**, with zero protocol work.
2. **Port forwarding on the host**: forward TCP 51750, 51760, 51761 and the stub port, plus UDP ENet. That needs
   patch **P4** (deterministic ENet port per server world, e.g. `Args.GSPort + worldIndex`) and P2 advertising the
   WAN IP. The host's own client then connects to its WAN IP and needs NAT hairpinning, or a split-horizon P2
   (return `localhost` for the local client).
3. **Relay** (frp/ngrok-style): the host's native companion keeps outbound TCP and UDP tunnels to a VPS that
   exposes the same port numbers. P2 advertises the relay host name (`TcpSocket` resolves names), and ENet goes
   to the relay IP plus the reported port, so the relay must expose the identical UDP port numbers (P4).
4. **NAT hole punching**: not present anywhere. It would need a rendezvous service and changes to the native
   ENet connect path, and the TCP control channel cannot be punched reliably. Not recommended.
5. A central cloud lobby/matchmaker federating LocalAnubis hosts is possible: the cloud creates rooms on a host's
   Lobby over TCP and returns that host's controller. It is more work and only worth it for public matchmaking.

### 10.5 IL patch list (Gameloft.Engine.dll; then drop/regenerate `lib/*/libaot-Gameloft.Engine.dll.so` and re-sign, report 02 §7)

| Id | Where | Change | Needed for |
|---|---|---|---|
| P1 | `Gameloft.Engine.LocalAnubis.Finder::.ctor` `IL_0009 ldstr "localhost"` | → `"*"` (Mono: `IPAddress.Any`, prefix goes to the `unhandled` list, matches any Host header) | any remote Finder access |
| P2 | `Gameloft.Engine.LocalAnubisManager::GetPublicHost` | Keep `"localhost"` for loopback. Otherwise return `Args.GSRemoteIp` if set, else the LAN IP from a UDP-connect route trick (as in `P2PServerComponent.GetLocalAdress`, but towards e.g. `8.8.8.8` or the gateway) | remote hosting; makes `-LocalAnubisHost 0.0.0.0` usable |
| P3 | `Gameloft.Engine.ClientSettings::.ctor` initializer of `LocalAnubisFinderHost` | `Args.RemoteIp ?? LocalAnubisManager.PublicHost` (templates may still override, see open question) | Topology A guests |
| P3b | `ClientLink.Automatch/QuickLaunch/FindRooms/FindTeamRooms/LocateLocalLobby` | Use `Args.RemoteIp` for non-`ts` links | Topology B |
| P4 | `Connection` `<ProcessStartEnetSocket>` state machine, the two `StartServer(20, 0, 0)` call sites | listenPort from an argument plus a per-world offset | port forwarding / relay |
| P5 | Robustness | B1: set `StatusCode` before writing, wrap in try/catch. B2: drop the trailing spaces. B3: strip `!`, use `Groups[1]`/`Groups[3]`. B4: dedupe by `fed_id` and consume `reservation_code`. B5: `FirstOrDefault`. B7: log | stability |
| P6 (optional) | `P2PServerComponent.P2P_OnDiscovering` | Advertise `controller_tcp_port` = Finder 51750 and a stable name, and drop the NullReferenceException in `ServerComponent.OnCreate` | in-game LAN discovery, if a UI is added |

Patch tooling: a small Mono.Cecil program (dotnet is available) is more reliable than recompiling the ilspy
projects (FastAccess/Aspects generated code).

---

## 11. Gap list for real multi-device play

**Discovery**
* No working LAN discovery. `P2P` yields un-joinable rooms (port -1), its responder needs `-AllowP2P` in `cmd_args`
  plus a template component, and there is no game UI to browse or join. → Do it in a pre-launch picker or native
  companion, or add a UI hook.
* Android broadcast reception without `MulticastLock` may be filtered [S] (no permission to acquire one).

**Reachability**
* Finder is loopback/`localhost`-Host only (P1). `PublicHost` is wrong on Android (P2). No remote-finder argument
  (P3/P3b). The Finder port is hard-coded on clients. ENet ports are ephemeral, one per world (P4).

**NAT traversal and relay**
* None. Use an overlay VPN, port forwarding, or a relay (10.4).

**Authentication between peers**
* Every hop requires the host's Federation to `verify` tokens and to `authorize for_username` guests. Per-device
  stubs need cross-verifiable tokens and deterministic accounts.
* Lobby login trusts client-supplied identity fields after `verify`, with a constant nonce. That is fine for
  friends and not secure.

**Profiles and progression**
* The host gs loads and saves each participant's profile through the host's storage (report 03 §14). This needs a
  storage proxy or hand-off, or acceptance that guest progression lives on the host (Topology A).
* Offline bot results are client-reported to the TS (report 03 §1.3). Unaffected.

**Host leaving / lifecycle**
* No host migration. When the host app dies, the gs and (Topology A) all guest TS worlds die, the Lobby rooms
  vanish, and guests hit reconnect loops (`GameClientComponent` auto-reconnect) to a dead host.
* The gs saves profiles only at match end, so a mid-match host exit loses guests' match rewards.
* Backgrounding the host app very likely stops the native main loop, freezing all hosted worlds [S].
* Empty server worlds self-destruct after 10 s. Rooms that are not started expire after `room_timeout`.

**Capacity and performance**
* One process runs the client World plus N server Worlds (Topology A: one TS world per guest). CPU and RAM on
  phones are untested [S].
* `connect game` is serialized; the first connect to a new room includes a level load, and later connects queue
  behind it against the client's 60 s Anubis timeout.

**Matchmaking semantics**
* Noob-bucket and MMR filters can keep friends apart. Prefer team-room codes or private matches, or set
  `score_automatch_enabled: false` in the config. Clients may still send their own score parameters, which win.
* `midgame_join` must be true for several humans in one automatch room.
* The automatcher name and TS level path come from template data (unknown).
* Private match UI is tied to clans and tournament tickets (`liveTournament.CanBuyTournamentTicket`, clan id,
  profanity check: `GM/GameClientComponent.cs:1520-1600`), so it needs the social/clan/profanity stub endpoints.

**Version**
* All devices must run the identical build (handshake `ServerClientId` / version checks).

---

## 12. Verified vs inferred (summary)

* **[V]**:
  * All LocalAnubis actions, fields, ports and defaults.
  * The enable paths and the `-AnubisCommand` requirement.
  * Federation dependencies (verify, encrypt_token, lobbyconfigs, sessions).
  * The Controller → `ServerManager` in-process world creation and the Args swap.
  * Framing and the ENet channel setup, including the any-address bind and ephemeral port (disassembly).
  * Finder binding and Host-header matching in Mono's `HttpListener` (decompiled System.dll).
  * The P2P prototype's behaviour and why it cannot connect.
  * The bugs B1-B13 as code facts.
* **[L]**:
  * `Dns.GetHostName()` → `localhost` → 127.0.0.1 on Android, and hence `PublicHost` breakage.
  * The crash consequence of async-void exceptions.
  * The need for `-Level` in the TS `cmd_args`.
  * `ServerInfos.json` being readable from the installed data directory.
  * AOT fallback after IL patching (report 02).
* **[S]**:
  * Broadcast filtering on Android.
  * Background suspension of hosted worlds.
  * Phone performance with several server worlds.
  * Behaviour of the B6 args swap on client-world code.

---

## 13. Open questions

1. Template data (needs the game data): link `ServerLocation`/`AnubisCommand`/`ServerType`/`InitialLevel`
   defaults; serialized `LocalAnubisFinderHost`/`Port`; `MatchmakingComponent.Automatcher`/`Host`/`Port`; whether
   `P2PServerComponent`/`P2PClientComponent` exist in `SceneComponents.tx`.
2. The production lobbyconfigs `cmd_args` for `ts`/`gs` and the TS level path.
3. `PublicHost` and `MachineName` values on real Android devices (quick test: Embedded with
   `-LocalAnubisHost 0.0.0.0` and `GET /rooms`).
4. Does the native main loop keep ticking `UpdateWorlds` while the activity is paused?
5. Was Embedded mode ever exercised on Android (memory, loading two worlds), or only in the PC editor?
6. Exact `GlfStream` search path for `ServerInfos.json` (obfuscated FS, Extra vs Data).
