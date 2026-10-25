# Verify C6: LAN / P2P joining of an Embedded host

Claim under test (C6-lan-p2p):

> Remote devices cannot join an Embedded host as shipped: the LocalAnubis Finder HttpListener prefix is hard-coded
> to "localhost", PublicHost resolves to localhost/127.0.0.1, the Finder port is fixed at 51750, there is no argument
> to point a client at a remote Finder, and the Lobby/Controller bind 127.0.0.1 by default. Four small IL patches
> (Finder prefix '*', real PublicHost, remote-finder override reusing Args.RemoteIp, fixed ENet port) make the existing
> Lobby/Controller/in-process GS serve LAN guests unchanged; alternatively guests use -ServerLocation Remote -RemoteIp
> <host>. P2P.cs/P2PServerComponent are an unfinished prototype.

**Verdict: partially.** The headline is right: as shipped, a second device cannot join, and it takes IL (or data)
patches. Several of the supporting statements are wrong or overstated, though, and they change the design:

1. **The "PublicHost resolves to 127.0.0.1" premise is wrong** for a non-loopback `-LocalAnubisHost`. The shipped Mono
   runtime answers `Dns.GetHostEntry(Dns.GetHostName())` with the device's **non-loopback interface IPs**. So
   `PublicHost` is already a real LAN IPv4. It is just not reliably the Wi-Fi one.
2. **The Finder's "localhost" prefix probably does not bind 127.0.0.1 on Android either.** By the same Mono rule it
   binds the first non-loopback IPv4. Remote requests are still refused, because of the `Host`-header match.
3. **A fixed ENet port is not needed for LAN.** So "four patches" is really two required (P1, P3), one strongly
   recommended (P2), and P4 only for NAT or port forwarding.
4. **`-RemoteIp` does nothing as shipped.** Zero IL references. `-ServerLocation Remote` alone sends the guest to
   *its own* `localhost:51750`.
5. **"Unchanged" holds only if the guest uses the host's Federation stub.** The host's in-process worlds verify
   tokens, open the guest's session and load and save the guest's profile through the **host's** stub.
6. **New race, verified in code.** The Controller swaps the global `Args` during `connect game`, and `PublicHost` is
   recomputed from live `Args`. Rooms launched during that window advertise `"localhost"` to LAN guests.
7. **The PoC Federation handler (analyst 09) intercepts LAN Finder traffic.** Its `localhost`-prefix listener on
   18080 also rejects `Host: 127.0.0.1` requests.

Paths are relative to `/home/user/mcvs-work`. `EN/` = `decompiled/Gameloft.Engine/Gameloft.Engine/`, `LA/` =
`decompiled/Gameloft.Engine/Gameloft.Engine.LocalAnubis/`, `OP/` =
`decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/`, `GM/` = `decompiled/Game/Game/`. "IL" means my own
`ilspycmd -il` dump of `extracted/assemblies/Gameloft.Engine.dll`, kept at
`/tmp/claude-0/-home-user/98d9334c-9d5f-5995-bde1-c27c1652af38/scratchpad/c6verify/Gameloft.Engine.il`.
Template data comes from the data pack, which is complete per verify-C2. I decoded `Data/1/52218da.f`
(`SceneComponents.tx`, GCBF/LZ4) to `scratchpad/c6verify/out/SceneComponents.tx`.

---

## 1. Sub-claim verdicts

| # | Sub-claim | Verdict | Key evidence |
|---|---|---|---|
| a | Finder prefix hard-coded `"localhost"` | **confirmed** | IL `Finder::.ctor IL_0009 ldstr "localhost"`; `LA/Finder.cs:11-15`; `EN/HTTPServer.cs:23-33` |
| a' | …so it binds 127.0.0.1 (report 06 §6.1) | **refuted / device-dependent** | Mono `GetEPListener` → `Dns.GetHostByName("localhost")`. On Android, `gethostname()` is `"localhost"`, which triggers Mono's `add_local_ips` path → first **non-loopback** IPv4 (§2.2) |
| a'' | Remote clients are rejected by the Finder | **confirmed** | The `Host` header must equal `localhost`. Otherwise Mono answers `400 Invalid host` (`System.Net.HttpConnection`) |
| b | PublicHost = localhost/127.0.0.1 | **partially** | `"localhost"` for the default loopback Host (`EN/LocalAnubisManager.cs:77-80`). For any non-loopback `-LocalAnubisHost` it is the first up non-loopback IPv4 from `SIOCGIFCONF`, **not** 127.0.0.1 (§2.2) |
| c | Finder port fixed at 51750 | **partially** | Host side honours `-LocalAnubisFinderPort` (`EN/LocalAnubisManager.cs:123`). Clients use the hard initializer `51750` (`EN/ClientSettings.cs:355`), and no template sets it |
| d | No argument points a client at a remote Finder | **confirmed (args)**, but a data route exists | `RemoteIp`/`GSRemoteIp`/`GSPort` have zero IL references. `LocalAnubisFinderHost` is a serialized, non-ignored property that the shipped templates leave unset (§2.4) |
| e | Lobby/Controller bind 127.0.0.1 by default | **confirmed** | `EN/LocalAnubisManager.cs:44-54` (`IPAddress.Loopback`), `LA/BaseServer.cs:197-202`, `LA/Lobby.cs:43`. Ports are ephemeral unless `-LocalAnubisLobbyPort`/`-LocalAnubisControllerPort` are given |
| f | Four IL patches make the Lobby/Controller/GS serve LAN guests unchanged | **partially** | P1 and P3 required, P2 recommended (and must cache, §2.6), P4 not needed on LAN (§2.5). "Unchanged" only with one shared Federation stub (§3) |
| g | Alternatively: `-ServerLocation Remote -RemoteIp <host>` | **refuted as shipped** | `Remote` = use the Finder at `Settings.LocalAnubisFinderHost` (the local PublicHost = `"localhost"`) without starting one. `RemoteIp` is inert. Works only together with P3 |
| h | P2P.cs/P2PServerComponent are an unfinished prototype | **confirmed**, with a correction | No join path. Advertised port −1. But both components **are present in the shipped `$default` scene template**, so report 06 B13 (NRE) does not happen (§4) |

---

## 2. Bind addresses, advertised addresses and ports (walked in code)

### 2.1 Lobby and Controller (TCP, JSON lines)

```csharp
// EN/LocalAnubisManager.cs:44-54
public static IPAddress Host { get {
    if (!string.IsNullOrEmpty(GameApplication.Instance.Args.LocalAnubisHost))
        return IPAddress.Parse(GameApplication.Instance.Args.LocalAnubisHost);
    return IPAddress.Loopback; } }
// EN/LocalAnubisManager.cs:121-124
Lobby = new Lobby(Session, Host, GameApplication.Instance.Args.LocalAnubisLobbyPort ?? 0, 20); Lobby.Start();
Finder = new Finder(Session, Lobby, GameApplication.Instance.Args.LocalAnubisFinderPort ?? 51750); Finder.Start();
// LA/Lobby.cs:43  (Controller uses the same Host)
Controller = new Controller(base.Session, this, base.Host, GameApplication.Instance.Args.LocalAnubisControllerPort ?? 0, 20);
// LA/BaseServer.cs:197-202
Listener = new TcpListener(host, port);
```

So the Lobby and Controller bind `127.0.0.1:<ephemeral>` by default, and `-LocalAnubisHost 0.0.0.0` makes them bind
`0.0.0.0` (IPv4 only). Advertised values: `controller_host = Controller.PublicHost`, `controller_tcp_port = Port`
(`LA/Lobby.cs:177-183`), `lobby_host = PublicHost` (`LA/Lobby.cs:205`), and Finder `/locate` returns
`"{PublicHost}:{PublicPort}"` (`LA/FinderHTTPMessageHandler.cs:197-211`). `PublicHost` is a property that calls
`GetPublicHost()` **on every access** (`EN/LocalAnubisManager.cs:56`, `LA/BaseServer.cs:181`).

### 2.2 PublicHost and the Finder bind: what Mono really does

```csharp
// EN/LocalAnubisManager.cs:75-82   (IL GetPublicHost @RVA 0xed2cc, IL_000c ldstr "localhost")
if (IPAddress.IsLoopback(Host)) return "localhost";
return Dns.GetHostEntry(Dns.GetHostName()).AddressList.FirstOrDefault(ip => ip.AddressFamily == InterNetwork).ToString();
```

Shipped `System.dll`: `Dns.GetHostEntry(name)` → `GetHostByName` → `GetHostByName_icall`. Shipped
`libmonosgen-2.0.so` (arm64), disassembled with `llvm-objdump`:

* `0x1c358c-0x1c35d4`: `gethostname(buf,256)`, then `strcmp(hostname, buf)`. On a match (or an empty name) the
  `add_local_ips` flag is set (`orr w25, wzr, #1`) and passed as arg 5 to the hostent builder `0x1c367c`
  (`mov w25, w4` at `0x1c36a8`).
* `0x1c37ac-0x1c3828`: if the flag is set, it calls `0x28834c` (`mono_get_local_interfaces`) for `AF_INET` (2) and
  `AF_INET6` (10). If either count is non-zero, it **returns those interface addresses instead of the resolver
  result**.
* `0x28834c`: `socket()`, `ioctl(SIOCGIFCONF=0x8912)` (`0x2883c0`), `ioctl(SIOCGIFFLAGS=0x8913)` per entry
  (`0x288428`), `tst w8,#0x8` (IFF_LOOPBACK) and the IFF_UP bit. Loopback entries are dropped when any non-loopback
  interface is up.

Consequences (mechanism verified in code; the hostname value is an AOSP convention, `init.rc` `hostname localhost`,
so test it on a device):

* `GetPublicHost()` passes **exactly** `Dns.GetHostName()`, so `add_local_ips` always applies whatever the hostname
  is. With `-LocalAnubisHost 0.0.0.0`, PublicHost = **the first up non-loopback IPv4 in kernel `SIOCGIFCONF`
  order**. On a Wi-Fi-only phone that is the Wi-Fi address. With mobile data, a hotspot (`swlan0`/`ap0`) or a VPN
  (`tun0`) up, it can be any of them. Report 06's "[L] very likely 127.0.0.1" is wrong. P2 is about choosing the
  *right* interface, not about escaping loopback.
* Finder: `HTTPServer` builds `http://localhost:51750/` (`EN/HTTPServer.cs:31`). Mono
  `EndPointManager.GetEPListener` only special-cases `"*"`. Any other host name goes through
  `Dns.GetHostByName(host)` and `AddressList[0]`, with a fallback to `IPAddress.Any` if that throws (decompiled
  `extracted/assemblies/System.dll`). If `gethostname()=="localhost"`, the Finder therefore binds the **first
  non-loopback IPv4**, e.g. `192.168.1.23:51750`. It binds 127.0.0.1 only when no other interface is up
  (airplane mode).
  * The host's own client still works: the managed `HttpClientHandler` resolves `localhost` through the same
    icall, and the `Host: localhost:51750` header matches.
  * A **remote** client reaches the socket but gets `400 Invalid host`. `EndPointListener.SearchListener` requires
    `prefix.Host == uri.Host` for named prefixes, and only `*` (the `unhandled` list) or `+` (the `all` list) skip
    that check. `HttpConnection` calls `SendError("Invalid host", 400)` when `BindContext` fails.
  * **New single-device fragility:** the bind address is fixed when the Finder starts. If Wi-Fi drops or the DHCP
    address changes, `localhost` resolves to a different address. The Finder (still `Started`) is then unreachable,
    and every later QuickLaunch/automatch fails until the app restarts. P1 (`*` → `0.0.0.0`) also fixes this.
    **[mechanism V, runtime needs a device test]**
* `TcpSocket.ConnectAsync` special-cases the literal `"localhost"` to `IPAddress.Loopback`
  (`EN/TcpSocket.cs:185-187`). That is why `controller_host:"localhost"` reaches the loopback-bound Controller on
  the host itself, and why a remote guest given `"localhost"` connects to itself.

### 2.3 Finder (HTTP), what clients use

```csharp
// EN/ClientSettings.cs:341-355
public bool OverrideAnubisHost => ServerLocation != Online && != None && != Auto;   // true for Embedded, Remote, SSH, Tunneling, P2P
public string LocalAnubisFinderHost { get; set; } = LocalAnubisManager.PublicHost;  // IL ClientSettings::.ctor IL_00ec/IL_00f1
public int    LocalAnubisFinderPort { get; set; } = 51750;                          // IL_00f7 ldc.i4 51750
```

Every matchmaker call takes this host and port when `OverrideAnubisHost` is set: QuickLaunch, FindRooms,
FindTeamRooms, `/locate` (`EN/ClientLink.cs:398-458`), and the automatch `host`/`port` the client forwards to its TS
world (`EN/ClientLink.cs:377-382`, used server-side in `EN/MatchmakingComponent.cs:342-344,400-411`). The URL is
`new UriBuilder("http", host, port, path)` (`OP/MatchmakerApi.cs:13,22,58`).

### 2.4 "No argument to point at a remote Finder": confirmed, plus a data route

* `GameApplication.Arguments.RemoteIp`, `GSRemoteIp` and `GSPort` (`EN/GameApplication.cs:106,108,130`) are only
  declared. `grep '::RemoteIp\|::GSRemoteIp\|::GSPort'` over my IL dumps of Gameloft.Engine, Game,
  Gameloft.OnlinePlatform, Gameloft.Util and XaLaunchANMP finds **0 hits**. `ApplicationArguments.Parse` fills them
  by reflection (`EN/ApplicationArguments.cs:24-60`), and nothing reads them.
* `-LocalAnubisHost <hostIP>` on a guest does not help. `GetPublicHost()` would return the **guest's own** first
  interface IP (§2.2).
* Data route (not an argument): `LocalAnubisFinderHost` and `LocalAnubisFinderPort` have no `[SerializeIgnore]`.
  The shipped link templates (`SceneComponents.tx`, links `ts` / `quickMatch` / `privateMatch`, lines 176-294 of the
  decoded file) set other `ClientSettings` strings, e.g. `"Host":"mda-anubis-gsa001.gameloft.com"`, which overrides
  the code default `alpha01.mdc.gameloft.org` (`EN/ClientSettings.cs:88`). They do **not** set the Finder keys.
  Adding `"LocalAnubisFinderHost":"<hostIP>"` to the three links is therefore a plausible IL-free alternative to P3
  (the IP is baked into the data). **[L]** It needs re-packing GCBF/LZ4 and an assurance that installed `.f` files
  are not re-verified.

### 2.5 GS ENet socket and the UDP target

* Server: `ProcessStartEnetSocket` (`EN/Connection.cs:1347-1378`) calls `EnetSocket.Port != 0 ||
  EnetSocket.StartServer(20u, 0, 0u)` and replies `{"port": EnetSocket.Port}`. These are the only two
  `StartServer` call sites (IL `<ProcessStartEnetSocket>d__209` IL_00fc and IL_012a: `ldc.i4.s 20; ldc.i4.0;
  ldc.i4.0`). There is one EnetSocket per server World (`ServerComponent` is the `IEnetSocketOwner`,
  `EN/ServerComponent.cs:173-176`).
* Native `EnetSocket_StartServer_private @0x392be0` (`extracted/lib/arm64-v8a/libnativeengine.so`): ENetAddress
  host = 16 bytes from `.rodata 0x12cfd50`, which I read as all zeros, i.e. `in6addr_any`; port = `listenPort` (0)
  → **ephemeral**. The host-creation path `@0x76b590`: `socket(AF_INET6=10, SOCK_DGRAM=2, 0)`,
  `setsockopt(IPPROTO_IPV6=41, IPV6_V6ONLY=26, 0)` (**dual-stack**), `bind`, `SO_BROADCAST=1`, 256 KiB buffers.
  IPv4 LAN peers are therefore accepted.
* Client target: `EnetSocket.ConnectAsync(RemoteEndPoint.GetIp(), reply["port"], …)` (`EN/Connection.cs:1003`).
  `RemoteEndPoint` is the control TCP socket's peer (`EN/Connection.cs:209`, `UT/EndPointExtension.cs:7-10`). The
  guest's UDP therefore goes to **whatever IP it reached the Controller on**, plus the port reported over TCP.
* ⇒ On a LAN the ephemeral port needs no patch. **P4 is only for port forwarding or relays**, as report 06 §10.4
  itself says. The claim's "four patches … serve LAN guests" overstates it. The TS link has
  `EnableReliableChannel/EnableUnreliableChannel=false` (template), so TS sessions use TCP only. Only gs worlds open
  ENet.

### 2.6 New, code-verified race: global `Args` swap vs. live `PublicHost`

```csharp
// LA/Controller.cs:78-91
GameApplication.Arguments originalArgs = GameApplication.Instance.Args;
argsField.SetValue(GameApplication.Instance, GameApplication.Arguments.FromString(configArgs));   // cmd_args only
ServerManager.OnRoomInfo(room.ToJObject());
await ServerManager.AddClientAsync(...);     // world creation + level load: seconds, other async work interleaves
argsField.SetValue(GameApplication.Instance, originalArgs);
```

`LocalAnubisManager.Host` re-reads `GameApplication.Instance.Args.LocalAnubisHost` on every call
(`EN/LocalAnubisManager.cs:48`). During the swap, `Args` = the lobbyconfig `cmd_args`, so `LocalAnubisHost` is null,
Host is Loopback and PublicHost is `"localhost"`. The Finder and Lobby handlers are async continuations that can run
inside that window. Any guest QuickLaunch, automatch or launch then gets `controller_host:"localhost"` /
`lobby_host:"localhost"`, and the guest connects to itself. This is invisible on one device and real on a LAN, where
guests' TS worlds are created on the host often.

**Fix:** P2 must compute PublicHost **once at `LocalAnubisManager.Start`** and cache it. Alternatively (or as well),
the host stub's lobbyconfig `cmd_args` repeat `-LocalAnubisHost 0.0.0.0 -GSRemoteIp <ip>`; there is no `-` inside
the values, as `GetConfigArgs` requires (`LA/Controller.cs:100-106`).

---

## 3. What `-ServerLocation Remote` does, and whose Federation and storage are used

### 3.1 Remote on the guest

```csharp
// EN/ClientComponent.cs:987-1009  Prepare
ResultWithException ex = await Authentication.Connect(ct);     // anonymous Federation session: ALWAYS
switch (link.Settings.ServerLocation) { case Embedded: await LocalAnubisManager.Start(ct); break;
                                         case Remote: … break; }   // Remote: nothing started locally
```

* `Remote` = `OverrideAnubisHost` without a local Finder. The guest talks to
  `Settings.LocalAnubisFinderHost:51750` = its own PublicHost = `"localhost"` → connection refused.
  `-RemoteIp` changes nothing (§2.4).
* `-AnubisCommand QuickLaunch` is still mandatory (`EN/ClientComponent.cs:243-250` with `1021-1023`).
* **A Federation is still required on the guest:**
  * `Prepare` (anonymous session: eve config, pandora, `authorize`, `users/me`);
  * `ConnectToServer` → `Authentication.OpenAnonymousSession` (`EN/ClientComponent.cs:1095-1139`);
  * `Session.EncryptedAccessToken(nonce, "lobby")` for every Lobby/Controller login (`EN/AnubisClientSocket.cs:78-83`);
  * the scoped `access_token` added to every Finder request (`OP/FedRequestMessage.cs` `PrepareToSend`, scope
    `"lobby"` from `OP/MatchmakerApi.cs:13,22`);
  * all client-world services.

  It does **not** have to be the guest's *own* stub. Pointing `EnvironmentSwitcherUrl` at the host's stub is the
  simplest correct choice (§5).
* Side effects of Remote compared with Embedded:
  * `ShouldPlayTutorial` gates on `CurrentServerLocation != Embedded` (`GM/GameClientComponent.cs:363-380`), so
    Remote guests get the normal tutorial flow when the profile's `FinishedTutorial` is false;
  * `ProfileModifierComponent` only disables itself for Embedded (`GM/ProfileModifierComponent.cs:26-34`), but it
    is not in the shipped data (grep of the decoded pack: no hit).

### 3.2 Who verifies what on the host

| Hop | Code | Verified by | Identity actually used |
|---|---|---|---|
| Finder QuickLaunch/automatch | `LA/FinderHTTPMessageHandler.cs:143-156` → `Session.Verify(access_token)` | **host** LocalAnubis super session → host stub `auth/verify` | QuickLaunch: the verify response only (no `user` param, `OP/MatchmakerApi.cs:10-17`). Automatch: the verify response, then `user` JSON merged over it (`OP/MatchmakerApi.cs:25`, `OP/RoomUser.cs:143-149`) |
| Lobby/Controller `login` | `LA/BaseServer.cs:406-429` → `Session.Verify(token, "LocalAnubisNonce", "system:Anubis")` | host stub | **Client-asserted**: every request key except `action/access_token/username/override_ip_geolocation/nonce_type` overwrites the verified `credential/fed_id/client_id`. The client sends `RoomUser(session)` = its own credential, fed_id and client_id (`OP/RoomUser.cs:143-149`, `OP/Gameloft.OnlinePlatform.Lobby/LobbyApi.cs:10-20`) |
| `connect game` | `LA/Controller.cs:83-89` → `ServerManager.AddClientAsync({credential, fed_id})` → `ServerComponent.AddClient(fedId, credential)` (`EN/ServerComponent.cs:388-398`) | – | `Connection.FedId/Credential` = the client-asserted values from the login (never reconciled later) |
| Handshake | `EN/Connection.cs:1551-1565` → `Authentication.OpenSessionForUser(Credential, …)` → `World.OnlinePlatform.FederationFor` → `OpenSessionForUser(SuperCredential, SuperPassword, guestCredential)` (`EN/AuthenticationComponent.cs:576-583`) | **host stub** (`for_username` style, super credential) | The host stub decides the account for the guest's credential |
| Profile load/save | `GM/GameServerComponent.cs:934-943` `LoadPlayerProfile(connection.Session)` → `StorageService` of the host-issued session; registered under **`session.FedId`** (`GM/ProfileManagerComponent.cs:956-998`). Saves and lookups use **`connection.FedId`** (`GM/GameServerComponent.cs:694-701,770-777`; `CurrentMatchAccessor.LoadForUser(..., connection.FedId)` `:963`) | **host stub storage** | If the host stub's account id for the guest credential ≠ the guest-asserted `fed_id`, `GetProfileComponent(connection.FedId)` returns null. The **match-end save is silently skipped** |

Answers to the questions:

* **Whose storage the host's GS (and TS) uses for guest profiles:** the **host stub's**, always. All in-process
  server worlds share the host process's Federation (`OnlineLibsSettingsComponent.EnvironmentSwitcherUrl` = the live
  `Args.EnvironmentSwitcherUrl`, `EN/OnlineLibsSettingsComponent.cs:68`).
* **Guest tokens issued by the guest's own stub:**
  * They fail at the Finder and the Lobby/Controller **if the host stub validates tokens against its own table**.
    Finder: `IOnlineRequestException` → 401 (`LA/FinderHTTPMessageHandler.cs:100-107`). Login: an exception closes
    the socket.
  * They pass if the host's `verify` is permissive or decodes self-describing tokens. Permissive is enough for the
    login hops, because identity is client-asserted there. It is **not** enough for host-side QuickLaunch (the TS
    owner identity comes from `verify`) or for `OpenSessionForUser`, which must return a `fed_id` identical to the
    one the guest's own stub issued.

---

## 4. P2P.cs, P2PServerComponent, P2PClientComponent, P2PRoom

Confirmed behaviour:
* **Responder:** `UdpClient(50034)` with broadcast enabled (`EN/P2P.cs:119-141`). It answers the exact UTF-8 string
  `"Discovering"` by sending unicast to the requester's endpoint
  `new Room(controllerHost: LocalAdress, controllerPort: -1, id: "P2P:"+MachineName, name: MachineName)`, plus
  `members` (current `Server.Connections`) and `Level` (`EN/P2PServerComponent.cs:30-45`). `LocalAdress` comes from
  a UDP `connect("10.0.2.4", 65530)` route trick (`:23-28`).
* **Browser:** `P2PClientComponent` re-sends `"Discovering"` to `255.255.255.255:50034` every 1000 ms from a
  `UdpClient(0)` (`EN/P2P.cs:69-73,171-193`; `EN/P2PClientComponent.cs:42-58`). It keeps rooms keyed by
  `ControllerHost`, drops the UDP source endpoint, and has editor-only UI (`[Browsable]`).
* **Join:** `P2PRoom.Connect()` builds a link with `ServerLocation.P2P` and `Room` = the discovered room, then calls
  only `ConnectLink` (`EN/P2PRoom.cs:30-38`). `Prepare` has no P2P case (`EN/ClientComponent.cs:1007`), and the
  advertised port is −1. **It cannot join anything.**

Corrections and additions:
* `SceneComponents.tx` `$default` contains `/P2P/Client` (`P2PClientComponent`, `AutoRefresh:true`,
  `Enabled:false`) and `/P2P/Server` (`P2PServerComponent`, `Enabled:false`) (decoded lines 17-26). So
  `ServerComponent.OnCreate`'s `…OfType<P2PServerComponent>().FirstOrDefault().Enabled = true`
  (`EN/ServerComponent.cs:210-213`) finds the component. Report 06's **B13 NRE does not occur with the shipped
  data**.
* `-AllowP2P` is a bare bool flag (`EN/ApplicationArguments.cs:43-47`). Server worlds read it from the swapped
  `cmd_args`. So putting `-AllowP2P` in the **ts** lobbyconfig `cmd_args` turns the host's TS world(s) into
  responders with **no code change**. Each TS world on the host answers. The client dedups by ControllerHost.
* `ClientSettings.AllowP2P` (true on `quickMatch` and `privateMatch` in the template) is read nowhere
  (`grep AllowP2P`: only `EN/ClientSettings.cs:84,359`, `EN/P2PServerComponent.cs:69`).
* Nothing in code ever enables `P2PClientComponent`. `Environment.MachineName` is the hostname (`"localhost"` on
  Android), so every host advertises the same name.

**Is it reusable for LAN discovery?**
* **Wire protocol and responder: yes, as is.** Broadcast `"Discovering"` to UDP 50034 and read the JSON reply's
  `controller_host`. Because the Finder port is the constant 51750, `controller_host` plus 51750 is all a guest
  needs.
* **Client side: no.** It needs either a patch (enable the component, plus a join hook that sets
  `LocalAnubisFinderHost` and connects with `Remote`) or, more simply, a pre-launch picker (Java/native/bootstrap)
  that speaks the same protocol and passes `-RemoteIp`.
* **Robustness:**
  * prefer the reply's UDP **source address** over `controller_host`. The 10.0.2.4 route trick picks the
    default-route interface, which can be cellular on an offline Wi-Fi LAN that Android marks "no internet". With no
    default route, the trick throws, the exception is swallowed (`EN/P2P.cs:143`), and no reply is sent;
  * the app has no `CHANGE_WIFI_MULTICAST_STATE` (manifest permissions lines 322-337), so it cannot take a
    MulticastLock. Some Wi-Fi drivers filter broadcasts. **[device test]**

---

## 5. Minimal correct LAN flow (recommended: Topology A, "host is the whole backend")

The aim is one token authority and one storage. That is the only setup in which the existing
Lobby/Controller/in-process worlds really run *unchanged*.

```
GUEST (Remote, no LocalAnubis, no stub of its own in use)            HOST (Embedded)
EnvironmentSwitcherUrl=http://H:S/  ── eve/pandora/auth/storage ──►  Federation stub on 0.0.0.0:S (advertises URLs
                                                                     built from the request Host header → H for guests,
                                                                     127.0.0.1 for itself)
TS link: POST http://H:51750/rooms/{cid}/quick_launch ───────────►  Finder (prefix "*", P1) → verify (same stub) →
                                                                     new TS room; controller_host = cached PublicHost H (P2)
TCP H:<ctrl> login(encrypt_token from host stub) + connect game ──►  Controller (bind 0.0.0.0) → new TS World for the guest
                                                                     → OpenSessionForUser(guest anon cred) on host stub
                                                                     → guest profile = host storage[account(cred)]
quick match: TS world (on host) automatches at host=H (from guest's      Finder/Lobby shared with host's own TS → same gs
  quickMatch link LocalAnubisFinderHost, P3) ─────────────────────►  room (midgame_join true)
gs: TCP H:<ctrl> + ENet H:<ephemeral, reported over TCP> ─────────►  gs World (ENet [::]:0, dual-stack)
```

* **Who runs the TS for a guest:** the host. One TS World per guest, made by the guest's QuickLaunch
  (`LA/Lobby.cs:252-258`: a new room each call).
* **Where guest profiles live:** the host stub's storage, keyed by the guest's device-stable anonymous credential.
  That credential comes from native (report 02 / verify-C2 §1.2). "Progression saved locally" for guests then needs
  an explicit export/import or sync between stubs. That is a separate feature.
* **How tokens verify across devices:** they do not have to. Every token is issued and verified by the host stub.

**Patch and config list (exact places):**

| Id | Device | Where | Change | Status |
|---|---|---|---|---|
| P1 | host | `Gameloft.Engine.LocalAnubis.Finder::.ctor` IL_0009 `ldstr "localhost"` | → `ldstr "*"` (Mono `IPAddress.Any`, `unhandled` list, any Host header) | **required** (also fixes the §2.2 Wi-Fi-change fragility on a single device) |
| P2 | host | `Gameloft.Engine.LocalAnubisManager::GetPublicHost` (RVA 0xed2cc) and its `get_PublicHost` callers | Keep `"localhost"` for loopback. Otherwise return `Args.GSRemoteIp` if set, else the IPv4 of the default-route or Wi-Fi interface (UDP-connect trick in try/catch), else the existing DNS path. **Compute once in `Start` and cache it** (§2.6) | **strongly recommended**. Without it, multi-interface hosts advertise a random interface, and the Args-swap race advertises `"localhost"` |
| P3 | guest (same binary is fine) | `Gameloft.Engine.ClientSettings::.ctor` IL_00ec `call LocalAnubisManager::get_PublicHost()` | → call a helper `string.IsNullOrEmpty(Args.RemoteIp) ? LocalAnubisManager.PublicHost : Args.RemoteIp`. Unset `RemoteIp` keeps the shipped behaviour | **required** (or the template alternative in §2.4) |
| cfg | host | args | `-ServerLocation Embedded -AnubisCommand QuickLaunch -LocalAnubisHost 0.0.0.0 [-LocalAnubisLobbyPort 51760 -LocalAnubisControllerPort 51761] [-GSRemoteIp <H>]`. Also repeat `-LocalAnubisHost 0.0.0.0 -GSRemoteIp <H>` in every lobbyconfig `cmd_args` if P2 does not cache | required |
| cfg | guest | args | `-ServerLocation Remote -AnubisCommand QuickLaunch -RemoteIp <H> -EnvironmentSwitcherUrl http://<H>:<S>/ -DeviceId <x>` | required |
| stub | host | Federation stub | Network listener with a `*` prefix (not `localhost`). Service URLs built from the request's Host. Must answer `verify`, `encrypt_token`, `lobbyconfigs` and `for_username` sessions for any guest credential | required |
| stub | guest | PoC `InProcessFedHandler` (`scratchpad/poc/Bootstrap.cs:47-48`) | Currently intercepts everything except `localhost` and `127.0.0.1:!18080`, so **LAN Finder calls (`http://H:51750/...`) and the host stub URL would be answered by the local fake**. It must pass through non-loopback hosts or the Finder port | required if the PoC is used |
| P4 | host | `Connection.<ProcessStartEnetSocket>d__209` IL_00fe and IL_012c `ldc.i4.0` (listenPort) | Fixed port per world | **not needed for LAN**. Only for port forwarding or a relay |
| P5 | host | `HTTPServer.HandleMessage` (`EN/HTTPServer.cs:90-99`) | Set the status before writing the body (report 06 B1) | robustness |
| opt | host | lobbyconfig `ts` `cmd_args` | `-AllowP2P` turns on the shipped UDP-50034 responder (§4) | optional discovery |

**Topology B** ("guest keeps its own TS and storage, gs on the host") is closer to production, but it needs much
more than the four patches:
* P3b: only the non-`ts` links (`quickMatch` / `privateMatch`) go to the host. Patch `ClientLink.QuickLaunch`,
  `FindRooms`, `FindTeamRooms`, `LocateLocalLobby` and the automatch `host` (`EN/ClientLink.cs:377-458`).
* A host stub that accepts tokens issued by the guest's stub.
* A **deterministic `fed_id = f(credential)` shared by all stubs** (§3.2: profile load is keyed by `session.FedId`,
  save by `connection.FedId`).
* Storage for guest accounts **proxied to the guest's stub**, which must then also be LAN-reachable and registered
  with the host stub, because the host stub only ever sees requests from the host process. Without the proxy, the
  host gs loads a new or empty profile and saves the match result on the host.

---

## 6. New findings (outside the claim's wording)

1. **Mono `add_local_ips`** (libmonosgen `0x1c358c`, `0x1c37ac`, `0x28834c`): `GetHostEntry(GetHostName())`, and
   `GetHostByName("localhost")` when the hostname is `localhost`, return the non-loopback interface IPs. This
   corrects report 06 §6.1 and §6.3 and changes what P2 must do.
2. **The Finder binds the Wi-Fi IP at start** (when the hostname is `localhost`). A network change breaks
   single-device Embedded play until the app restarts. P1 fixes it.
3. **Args-swap race** (§2.6): `PublicHost` is recomputed from the swapped `Args`, so rooms advertise `"localhost"`
   while another server World is being created. Cache it in P2, or repeat the host args in `cmd_args`.
4. **Profile keying split** (§3.2): load uses `session.FedId` (host-issued), save uses `connection.FedId`
   (client-asserted). A mismatch silently drops gs saves. Cross-stub topologies need identical account ids.
5. **The PoC's own `localhost` listener on 18080** (`scratchpad/poc/Bootstrap.cs:20`) returns `400 Invalid host`
   for `http://127.0.0.1:18080/` (the injected `EnvironmentSwitcherUrl`, `ArgInject.cs:19`) to any non-Federation
   HTTP caller, e.g. `Connectivity.IsInternetAvailable` (`EN/Connectivity.cs:27-39`: "no internet" in error popups
   and DLC error classification, `EN/DlcComponent.cs:287-299`). On Android it may also bind the Wi-Fi IP. Use `*`.
6. **B13 refuted:** `P2PServerComponent` and `P2PClientComponent` ship in `$default`. `-AllowP2P` in `cmd_args`
   enables the responder without a crash.
7. **Cleartext is fine:** `XA_HTTP_CLIENT_HANDLER_TYPE=System.Net.Http.HttpClientHandler` (`libxamarin-app.so`
   strings), and `Federation` explicitly uses `new HttpClientHandler()` (`OP/Federation.cs:470`), which is managed
   (Sockets/MonoWebRequest, `System.Net.Http.dll` `CreateDefaultHandler`). So `network_security_config.xml`
   (cleartext only for 127.0.0.1 and gameloft.com) does not block `http://<LAN IP>`.
8. **ENet is dual-stack**, `IPV6_V6ONLY=0` (`libnativeengine.so @0x76b5ac`). IPv4 LAN and loopback peers both work.
9. **Remote guests take the online tutorial path** (`GM/GameClientComponent.cs:373`). Embedded skips it.

---

## 7. What only a device test can settle

* `gethostname()` on target devices (expected `localhost`), the `SIOCGIFCONF` order with Wi-Fi + mobile data,
  hotspot or VPN, and so which IP the shipped `PublicHost` / Finder bind pick. Whether SELinux still allows
  `SIOCGIFCONF` for `untrusted_app` on new Android versions; if not, `add_local_ips` falls back to the resolver.
* Sending and receiving 255.255.255.255:50034 broadcasts without a MulticastLock. The 10.0.2.4 route trick on an
  offline LAN or a phone hotspot.
* Inbound TCP/UDP to the host app while it is foreground (netd UID firewall chains under Doze/standby), and
  whether a backgrounded host keeps its worlds running.
* CPU/RAM of one TS World per guest plus the gs World on a phone host; `connect game` serialization against the
  60 s Anubis timeout.
* Whether a template-injected `LocalAnubisFinderHost` is applied and whether modified `.f` files are accepted.
