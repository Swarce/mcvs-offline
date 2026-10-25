# Verification of claim C2-embedded-boot

**Claim (as given):** with `-ServerLocation Embedded -AnubisCommand QuickLaunch` and a game super-credential (from
`ServerInfos.json` or args), the client starts LocalAnubis (Lobby/Controller TCP and a Finder over HTTP on
`localhost:51750`) and runs the TS and GS as headless Worlds in the same process. Given a Federation stub for eve
datacenters/urls, pandora locate, `auth/authorize`, `auth/users/me`, `encrypt_token`, `verify`,
`games/mygame/alias`, CRM `configs/users/me` (with `game`, `offline_store.prices[]` and `iap`), matchmaker
`lobbyconfigs/{cid}/{serverType}` with `cmd_args`, storage profiles and social events, the client reaches the main
menu with no other servers.

**Verdict: PARTIALLY confirmed.** The mechanism is real and I followed it end to end in code. The claim as written
is not enough to reach the main menu, for these reasons:

1. **The server Worlds are built with a different `GameApplication.Args`.** The Controller swaps it for the parsed
   `cmd_args` during the whole TS World construction and level load. So `cmd_args` (or another channel) must carry
   `-Level Levels/Menus.level`, `-ServerType ts`, the super credential and the eve URL. Without `-Level`, the TS
   loads a random PvP map instead of the menu. Without an eve URL, the TS World's Federation points at the real
   `https://eve.gameloft.com/`.
2. **`lobbyconfigs` must return `game.server_type = "ts"`.** If it is missing, the room type falls back to `"default"`.
3. **Two endpoints in the list are not strictly required.** CRM `configs/users/me` can fail, because the TS falls
   back to the bundled `Config/crmpreconfiguration.json`. Social `events` is not needed, because no time-limited
   events (TLEs) means no request.
4. **One endpoint is missing from the list.** On first run the TS calls `GET auth users?device_id=…` (it must return
   `[]`).
5. **"No other servers" is not strictly true.** The TS handshake calls `http://ip.ecomapis.gameloft.org/location/?ip=`
   directly with `HttpClient`, outside the Federation. It is fail-soft, but it can stall for up to 100 s (the default
   `HttpClient` timeout). The ts link's Handshake timeout is 60 s.
6. **The GS World is not created on the boot path.** Only the TS World is.

Path conventions: `EN/` = `decompiled/Gameloft.Engine/Gameloft.Engine/`, `LA/` =
`decompiled/Gameloft.Engine/Gameloft.Engine.LocalAnubis/`, `OP/` =
`decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/`, `GM/` = `decompiled/Game/Game/`, `UT/` =
`decompiled/Gameloft.Util/Gameloft.Util/`. All paths are relative to `/home/user/mcvs-work`.

**Data pack.** `datapack/1.17.32/ETC/files/` holds 15,747 files, which matches `manifest.json` `file_count`, so it
looks complete. I decoded the GCBF/LZ4 templates with a small script (`scratchpad/c2verify/gcbf.py`), which I wrote
to `/tmp/claude-0/-home-user/98d9334c-9d5f-5995-bde1-c27c1652af38/scratchpad/c2verify/`.

**New finding: the obfuscated-file-system name mapping.** The file for a path is
`Data/<first hex digit of %x(crc32(path))>/<remaining hex digits>.f`, where `path` is case-sensitive and relative
to `Data`. I verified it on these files:

| Path | Obfuscated file |
|---|---|
| `SceneComponents.tx` | `Data/1/52218da.f` |
| `Levels/Menus.level` | `Data/9/634dd17.f` |
| `Levels/Connection.level` | `Data/8/3f9efb0.f` |
| `Design/MCVS_Gameplay.xlsx` | `Data/c/e32b22.f` |
| `Config/crmpreconfiguration.json` | `Data/b/de76cd6.f` |
| `LevelComponents.tx`, `DefaultCrm.tx`, … | (also checked) |

---

## 1. Boot path walked in code

### 1.1 How links become Embedded/QuickLaunch (confirmed, with template data)

The shipped template data matches what the code expects:

* `Data/1/52218da.f` (`SceneComponents.tx`), lines 176–218 and 296–301: link `ts` has
  `"AnubisCommand":"QuickLaunch"`, `"ServerLocation":"Auto"`, `"ServerType":"ts"`,
  `"InitialLevel":"Levels/Menus.level"`, `"StartSynchronizationUponConnection":false`, `EnableReliableChannel` and
  `EnableUnreliableChannel` both false (so no ENet is needed for the TS), and a Handshake timeout of 60000.
  `"DefaultServerId":"ts"`, `"TransactionServerId":"ts"`, `"AutoConnect":true`.
* `Data/8/3f9efb0.f` (`Connection.level`), lines 42–61, and `Data/9/634dd17.f` (`Menus.level`), lines 535–557:
  `LinkSettingsInitializerComponent` with `"DefaultServerLocation":"Online","EditorServerLocation":"Embedded"` for
  `ts`, `quickMatch` and `privateMatch`. This is strong evidence that the developers ran Embedded in the editor.

```csharp
// GM/LinkSettingsInitializerComponent.cs:33-35  (applied to every Auto link that has a LinkSettings entry)
if (GameApplication.Instance.Args.ServerLocation != ClientComponent.ServerLocation.Auto)
    link.Settings.ServerLocation = GameApplication.Instance.Args.ServerLocation;
// EN/ClientComponent.cs:243-250
if (args.ServerLocation != ServerLocation.Auto)
    CurrentClientLink.Settings.AnubisCommand = args.AnubisCommand;     // default None
// EN/ClientComponent.cs:1021-1023, 1103-1107: None -> link.Room = null -> "Failed to obtain a valid room"
```

So `-AnubisCommand QuickLaunch` is mandatory whenever `-ServerLocation` is passed (**confirmed**). Both values are
colon-free, so `phantomArgs` can carry them.

Auto-connect: `EN/ClientComponent.cs:274-283` (`OnUpdate`) calls `DoAutoConnect` → `Connect(CurrentClientLink)`.
Exceptions that `ShouldCatchException` does not list (for example `ArgumentException` or `HttpListenerException`)
are caught by `DoAutoConnect` and turned into `OnConnectionError` (`:287-298`), which leads to the error popup or a
reconnect. They do not crash the app.

### 1.2 `Prepare` → anonymous session → LocalAnubis (confirmed)

```csharp
// EN/ClientComponent.cs:987-1010
ResultWithException ex = await Authentication.Connect(ct);   // = OpenAnonymousSession, always first
...
case ServerLocation.Embedded: await LocalAnubisManager.Start(ct);
```

`OpenAnonymousSession` (`EN/AuthenticationComponent.cs:486-566`):

1. `FederationFor(clientId)` → `Federation.GetConfig` (eve).
2. It waits for `DeviceId`: either `Args.DeviceId`, or the native GDID with a **10 s** limit
   (`:279-283,497-504`). A timeout raises `OnlineRequestTimeout("DeviceId timeout")`, which leads to the
   `OpenSessionFailed` popup. Pass `-DeviceId x` (colon-free) to avoid this.
3. `fed.OpenSession(AnonymousCredential, Password, …)`. The anonymous user name and password come from **native**
   (`AuthenticationComponent_GetAnonymousUsername/Password`, `:288-290,767-770`), so the stub must accept any anonymous
   credential.
4. `GetEveStatus` (`:520`). An unknown `"status"` string throws `FormatException` (`OP/Configuration.cs:72-73`),
   which is not an online exception. Omit `status`.

`LocalAnubisManager.Start` (`EN/LocalAnubisManager.cs:113-132`):

```csharp
Session = await new Federation(ClientId, EnvironmentSwitcherUrl, Datacenter, DefaultFedRequestTimeoutMS, 3, DefaultProtocol)
              .OpenSession(SuperCredential, SuperPassword, DeviceId, null, InitialScopes /*auth auth_admin_ro*/, ct);
Lobby = new Lobby(Session, Host /*127.0.0.1*/, Args.LocalAnubisLobbyPort ?? 0, 20); Lobby.Start();   // also starts Controller
Finder = new Finder(Session, Lobby, Args.LocalAnubisFinderPort ?? 51750); Finder.Start();       // HttpListener "http://localhost:51750/"
```

* `DefaultProtocol = Federation.Protocol.HTTPS` (`:91`). `FormatUri` rewrites any located URL whose port is
  80, 443 or absent into `https://host` with no port (`OP/Federation.cs:456-466`). **The stub must hand out URLs
  with explicit non-standard ports, or serve real TLS on 443.**
* `Datacenter = Args.DataCenter` (`:32`). If it is absent, eve `config/{cid}/datacenters` is called.
* If the Finder port is busy, `HttpListenerException` is raised; `Stop()` runs, and the exception goes to
  `OnConnectionError`.

### 1.3 QuickLaunch → Finder (confirmed)

* `ClientLink.QuickLaunch` (`EN/ClientLink.cs:398-412`) sends `POST http://localhost:51750/rooms/{cid}/quick_launch`
  with `server_type=ts` and the `lobby` token (`OP/MatchmakerApi.cs:10-17`). `OverrideAnubisHost` is true for
  Embedded (`EN/ClientSettings.cs:341-351`).
* The Finder's `ParseUser` (`LA/FinderHTTPMessageHandler.cs:143-156`) calls `Session.Verify(token)`, which is
  `GET auth/verify?access_token=` on the LocalAnubis session. It then builds
  `RoomUser.FromAccountInfo(jObject)`, which dereferences **`credential`, `account` and `client_id`**
  (`OP/RoomUser.cs:153`). If any is null, the result is an NRE and an HTTP 500 or garbage body.
* Next comes `GameConfig.FindGame(Session, "ts")`, which is `GET matchmaker lobbyconfigs/{cid}/ts` with scope
  **`lobby_game_admin`** (`OP/MatchmakerApi.cs:66-73`). That triggers a new `authorize` with that scope.
* `Lobby.QuickLaunch` → `MakeRoom(..., gameConfig)` builds `new Room(id, gameConfig.ServerType, …)`
  (`LA/Lobby.cs:205`), where `ServerType = game.server_type ?? "default"` (`LA/GameConfig.cs:19`).
  **⇒ `server_type: "ts"` is required.**

### 1.4 Controller login and `connect game` → in-process TS World (confirmed, with an important condition)

The client:

* `AnubisClientSocket.Login` (`EN/AnubisClientSocket.cs:34-84`) calls `GET auth/encrypt_token?access_token&nonce`
  with scope `lobby` (the game-server session uses `lobby_admin`), and sends `login`.
* The Controller then calls `Verify(token, "LocalAnubisNonce", "system:Anubis")` (`LA/BaseServer.cs:406-429`).
* Client receive timeout for each Anubis reply: **60 s** (`EN/AnubisClientSocket.cs:17`).

The Controller (`LA/Controller.cs:71-98`):

```csharp
GameConfig gameconfig = await GameConfig.FindGame(Session, room.ServerType, ct);        // lobbyconfigs again
string configArgs = GetConfigArgs(connectionContext.GameConfig);                        // NRE if cmd_args null
GameApplication.Arguments value = GameApplication.Arguments.FromString(configArgs);     // :80
argsField.SetValue(GameApplication.Instance, value);                                    // :81  <-- SWAP
ServerManager.OnRoomInfo(room.ToJObject());
await ServerManager.AddClientAsync(new ClientConnectionData(...), connectionContext.GameSocket, ct); // world ctor + level load
ct.ThrowIfCancellationRequested();                                                       // :90
argsField.SetValue(GameApplication.Instance, originalArgs);                             // :91  restore (no finally!)
```

* `GetConfigArgs` (`:100-106`) splits on **every** `-`, and turns only the first space of each token into `\n`.
  Values must therefore contain **no `-` and no spaces**. That excludes host names like `mcvs-stub` and passwords
  with `-`.
* `ServerManager._AddClientAsync` (`EN/ServerManager.cs:146-184`) runs `new World(headless:true, …, server:true)`
  synchronously (`:161`; the World constructor calls `InitSceneComponents`, `EN/World.cs:2011-2014`). It then runs
  `AddRoomInfo`, `RefreshRoom` and `InitWorld`, the last of which loads the level. **All of this runs while `Args`
  is the parsed `cmd_args`.** All exceptions are swallowed (`:178-183`, `:141-143`).

Consequences inside the TS World, all read from the swapped `Args`:

* **Super credential.** The `AuthenticationComponent` constructor reads `args.SuperCredential` and
  `args.SuperPassword`, and only if both are non-null (`EN/AuthenticationComponent.cs:312-316`). The fallback is
  `GameAuthenticationComponent.OnCreate` reading `ServerInfos.json` (`GM/GameAuthenticationComponent.cs:18-33`).
  The client's own `-SuperCredential` does not reach the TS World.
* **Eve URL.** `OnlinePlatformComponent.InitFederation` runs synchronously in `OnCreate` and fixes
  `EnvironmentSwitcher = OnlineLibsSettingsComponent.EnvironmentSwitcherUrl`, which is
  `Args.EnvironmentSwitcherUrl ?? "https://eve.gameloft.com/"` (`EN/OnlinePlatformComponent.cs:131-150`,
  `EN/OnlineLibsSettingsComponent.cs:68`). **Unless `cmd_args` contains `-EnvironmentSwitcherUrl`, the TS World talks
  to the real eve.** The alternatives are a DNS redirect of `eve.gameloft.com`, or analyst 09's in-process
  `HttpClientHandler`, which intercepts every host except `localhost`.
* **Datacenter.** It comes from `Args.DataCenter`, else the saved file `datacenter`, else an eve `datacenters` call
  (`EN/OnlinePlatformComponent.cs:40-66`).
* **Server type.** `ServerType = Args.ServerType` in `ServerComponent.OnCreate` (`EN/ServerComponent.cs:209`). It is
  overwritten by `room.ServerType` only if `RefreshRoom` returns the room (`:592`). If `RefreshRoom` fails (it is
  swallowed, `:656-694`), `ServerType` stays as `cmd_args` set it. **Pass `-ServerType ts`.**
* **Start level.** It comes from `Args.Level` (`GM/GameServerComponent.cs:1222-1225`). Otherwise the server picks
  `GetLevelToLoad(GameMode.PVP)` (`:1255`), which is a random map from `MapsComponent`
  (`GM/GameMatchmakingComponent.cs:893-915`). **Pass `-Level Levels/Menus.level`, or the "TS" loads a PvP map and
  the client syncs to it, not the menu.**

Two hazards in this design:

* The `Args` restore at `:91` is not in a `finally`. An exception, or a cancellation at `:90`, after `:81` leaves
  the **client** running with the `cmd_args` `Args` for the rest of the process. One example is an empty `fed_id`,
  which makes `new Credential(fed_id, "")` throw (`OP/ClientConnectionData.cs:26`). A cancellation can happen when
  the client gives up after 60 s while the TS level is still loading. **[L]**
* While the TS Menus level loads, the client World also sees the swapped `Args`. The client is mostly idle then,
  waiting for the `connect game` reply. **[S]**

### 1.5 TS handshake (server side), Federation calls

`Connection.ProcessHandshake` (`EN/Connection.cs:1551-1607`):

1. `OpenSessionForUser(credential = request.credential (anonymous), deviceId, clientId, InitialScopesServer)`
   → `authorize` with `for_username`/`for_credential_type` (`OP/SessionForUser.cs:91-102`), then `users/me`.
   The stub must map that anonymous credential to the **same account** the client's own `users/me` returned.
2. `StartAlerts()` (`:1573`) starts a background `alerts/me` streaming loop with retries
   (`OP/AlertListener.cs:54-57,73+`). It is not blocking.
3. The registered Handshake delegates:
   * `ServerComponent.Handshake`: reads CRM `game.CrashlyticsParams.Enable`. It is wrapped in try/catch
     (`EN/ServerComponent.cs:361-378`), so it is optional.
   * `GameServerComponent.Handshake` (`GM/GameServerComponent.cs:934-1116`):
     * `LoadPlayerProfile`, which uses storage (§1.6).
     * `CurrentMatchAccessor.LoadForUser`, with `OnlineRequestFailedException` caught.
     * `TakeProfileLock` → `SaveProfile` (`:972`), which needs a storage write.
     * `IsInGameloftEdgeList` uses the local list only. `fromLocalFile = true`, and `127.0.0.0/8` is always
       included (`UT/Geolocalization.cs:87,174`), so a loopback client is treated as a developer.
     * **`GetRegion(ip)` does a direct GET to `http://ip.ecomapis.gameloft.org/location/?ip=127.0.0.1`
       (`UT/Geolocalization.cs:93,99-105`) with no cancellation and the default 100 s `HttpClient` timeout.** It is
       inside try/catch (`GM/GameServerComponent.cs:994-1023`), so a fast DNS or connect failure is harmless. A
       blackholed route can push the handshake past the 60 s Handshake timeout.
     * `RegisterUser` → leaderboard `PostScore`, fire-and-forget and swallowed (`:780-829`).
   * `AgeGateChecker.Handshake` (`GM/AgeGateChecker.cs:66-140`; the component is present in `Menus.level`):
     * `GetStandardProfile` (storage).
     * `AddCompliances` is caught and defaults to 16.
     * The CRM user component's `RefreshCrmConfig`.
     * It sets `AgeGatePopup = !Birthdate` (`:128`). That means **an age-gate dialog appears on first boot** unless
       CRM `game.AgeGateParams.Ignore` is true. The bundled preconfiguration has `Ignore:false`.
   * `GameplayCRMComponent` registers its Handshake delegate only for a GS (`GM/GameplayCRMComponent.cs:30-60`).
4. The TS also starts `CRMServerSettingsComponent.RetrieveServerConfig` (`EN/CRMServerSettingsComponent.cs:64-75,100-150`),
   which opens the game-server session and reads `configs/users/me/game`. On the TS it is awaited only through
   `WaitForCRM` with a **1 s** limit (`GM/GameServerComponent.cs:1175-1189`), so it is **optional for the TS**. It is
   required for GS and automatch (`GM/GameplayCRMComponent.cs:68-79`, `GM/GameMatchmakingComponent.cs:852`).

### 1.6 Profile creation on first run (uncaught calls)

The calls below are not wrapped in try/catch, so each one must succeed.

* `ProfileManagerComponent.LoadPlayerProfile` (`GM/ProfileManagerComponent.cs:956-995`) reads storage
  `profiles/{cred}/myprofile[...]` sections (`OP/StorageApi.cs:100-141`). A 404 leads to `CreateProfile`.
* `GameProfileSection.CreateProfile` (`GM/GameProfileSection.cs:147-181`). If `users/me` lists exactly one
  credential and it is anonymous:
  * `RetrieveCrmServerConfig` (`:60-75`) opens another super session (device id `"mcvsGameServer"`) and reads CRM
    `JsonConfig["game"]["_ServerConfig"]`. If CRM answers 200 without `game`, that is an NRE (uncaught). A 404 is
    caught and is fine.
  * **`GET auth users?device_id=…`** (`OP/Session.cs:825-837`, scope `auth_admin_ro`) must return a **JSON array**.
    A non-array becomes null, and `.Where` on null throws.
  * The section is then saved with `SetProfileField`.
* `LoadProfile` → `InitPlayerName` when `PlayerName` is empty (`GM/ProfileManagerComponent.cs:886-889`) →
  `CreateAlias` = **`POST auth games/mygame/alias` → `{"alias":…}`**, uncaught
  (`GM/PlayerProfileComponent.cs:6375-6385`). `UpdateSocialProfile` is caught (`OP/SocialProfileService.cs:30-42`).

### 1.7 CRM is not strictly required (new finding)

```csharp
// EN/CRMComponent.cs:439-467
try { StandardCrm standardCrm = await Session.GetService<CrmService>().GetCRM().GetStandardConfiguration(reload: true, ct); ... }
catch (Exception ex) {
    if (!(ex is IOnlineRequestException) && !(ex is OperationCanceledException) && !(ex is CrmSection.InvalidDataException)) throw;
    JObject preconfig = LoadPreconfiguration();            // AssetStream "Config/crmpreconfiguration.json"
    ... CrmConfig = new StandardCrm.Data(preconfig.ToString());
}
```

The bundled file `Data/b/de76cd6.f` (2.18 MB of plain JSON) has these keys: `pointcut_actions`, `game`
(`AgeGateParams{Ignore:false}`, `CrashlyticsParams{Enable:true}`, `_OverrideTables`, …), `offline_store`
(95 `prices`), and `iap` (18 languages). It has **no `time_limited_events`**.

So:

* If the stub answers CRM with a 404 or 5xx, the per-user CRM still initialises from the pack.
* If the stub answers with a 200, the response must be a complete, valid CRM document, because no fallback happens.

### 1.8 Sync to the menu, and the gate on the main-menu UI

* The client handshake reply is processed by `ProcessHandshakeReply` (`GM/GameClientComponent.cs:3618-3664`).
  `ShouldPlayTutorial` is forced to false under Embedded (`:363-381`), so the tutorial is skipped. Then
  `_GameClientComponent_Connected` → `StartSynchronization()` (`:3666-3693`). The TS pushes `LevelPath`, and the
  client runs `LoadLevelFromFile` (`EN/SynchronizationComponent.cs:885-891`). **Confirmed.**
* Menu UI readiness. `MainMenuComponent.InitCrmAsync` waits for the synced `TimeLimitedEventsComponent` and its
  `OnEventsReceived` (`GM/MainMenuComponent.cs:169-191`). That handler (`:244-318`) ends in `OnMainMenuReady` →
  `SHOW_MENUS` (`:328-348`).
* On the TS, `TimeLimitedEventsComponent.RetrieveEventsAsync` (`EN/TimeLimitedEventsComponent.cs:160-231`) sets an
  **empty list without any request** when CRM has no TLE ids (`:177-181`). **⇒ Social `events` is not needed to
  reach the menu.** It is needed only for TLEs (crates, bundles and so on).
* Its `InitAsync` waits for the server CRM component. With the fallback in §1.7, that component is always created.

### 1.9 Other potential blockers I checked

| Item | Finding | Blocks the menu? |
|---|---|---|
| IAP init | `IapStoreComponent.InitAsync` → `InitNative` locates the **eve-config keys `ecomm_api_root` and `crm_iap`** (they throw if missing), then waits for the native `LibInitialized`, then `InitCrm` waits for the native **`StoreRefreshed`** callback (`EN/IapStoreComponent.cs:829-927,1538-1568`). `MainMenuComponent.WaitForIAP` waits for it, but in a fire-and-forget task that does not gate `SHOW_MENUS` (`GM/MainMenuComponent.cs:128,193-210`). | **No** for reaching the menu. The shop and unified store may hang. Native behaviour is untested (device test). |
| Native online SDK | `InitializeNative` → `Init_private(dc,"",eveConfig)` is posted to the main context for **every World with a non-empty Datacenter, including the TS World** (`EN/OnlinePlatformComponent.cs:146-172`). | Unknown. It is native. A second init with the TS World's config is a risk. |
| Tracking / ETS / ads | Federation `etsv2` and native TrackingManager calls, none awaited on the path | No |
| Time sync | No dedicated endpoint. Only the `Date` header (`OP/Session.cs:242`). | No |
| Terms / consent | Only a user-triggered privacy-policy link. Before the menu, the user sees the **language prompt** (`GM/GameClientComponent.cs:3107-3111`) and the **age gate** (`GM/UIComponent.cs:422-462`). | No. Both need user input, neither hangs. |
| Alerts long poll | Background retry loop | No. The stub should still answer something, to avoid log spam. |
| DLC | `HandleDlc` returns when `Args.UseDlc` is false. `UseDlc` comes from Java `getUseDlc()`, which is true only if `res/raw/args.txt` contains `-UseDlc`. The shipped file is only `-LogCategoryFilter Fatal` (`decompiled-java/.../AndroidUtilsPlugin.java:120-143`; native trampoline `GameApplication_GetUseDlc` at `0x39d5e8` JNI-calls `getUseDlc`). | No |
| `PerformIapCheck` / `ValidateInstall` | Default false (`EN/ClientComponent.cs:1119`, `EN/Connection.cs:1567-1570`) | No |
| Eve `GET /` | Never requested on the path. Only `Connectivity.IsInternetAvailable` uses it, in error popups (`EN/Connectivity.cs:17-37`). | No |
| Native signature flag | It only matters if the APK is modified (report 08 §2). Where it is consumed is still unknown. | Not with a stock APK |

---

## 2. Where the super credential comes from

* **LocalAnubis (client process).** It is a static constructor (`EN/LocalAnubisManager.cs:84-106`). It first runs
  when `ClientSettings` is constructed, because of the field initializer
  `LocalAnubisFinderHost = LocalAnubisManager.PublicHost` (`EN/ClientSettings.cs:353`), that is, during client World
  template creation. It reads `Args.SuperCredential` and `Args.SuperPassword`. If the credential is invalid or the
  password is null, it falls back to `ServerInfos.json` keys `SuperCredential` and `SuperPassword`.
  * The string-to-`Credential` conversion **throws `ArgumentException`** for a value without exactly one `:` (or
    two for `gamecenter`) (`OP/Credential.cs:66-71`). Inside a static constructor that becomes
    `TypeInitializationException`. **A malformed `-SuperCredential` therefore breaks boot.**
  * A missing password raises `ArgumentNullException("Password")` (`OP/Session.cs:654-657`), which surfaces as a
    connection error.
* **Server Worlds (TS and GS).** The `AuthenticationComponent` constructor uses the swapped `Args`, that is, the
  **lobbyconfig `cmd_args`**, and only when both values are present. The fallback is `ServerInfos.json`
  (`GM/GameAuthenticationComponent.cs:18-33`).
* **`ServerInfos.json` file format and location.** The format is `{"SuperCredential":"game:<name>","SuperPassword":"<pw>"}`.
  It is read with `GlfStream.Exists` and `AssetStream.ReadAllText`, both native and backed by the obfuscated file
  system (`UseObfuscatedFileSystem = true`, `EN/GameApplication.cs:1207-1208`).
  * By the crc32 rule above, the file would be **`<DataPath>/Data/5/f4c1f0f.f`**.
  * It is **absent** from the 1.17.32 ETC pack. No decoded template or JSON contains the text `SuperCredential`.
  * I have not verified whether native code accepts a raw (non-GCBF) file at that path. `EngineConfig.json` is
    shipped raw, under a plain name, which suggests it might. I also have not verified whether the Java installer
    deletes unknown files. Both need a device test.
* **The args channel itself is disputed.**
  * `phantomArgs` splits on `:` (`decompiled/XaLaunchANMP/XaLaunchANMP/XamarinMainActivity.cs:66-70`), so it cannot
    carry `game:x` or the eve URL.
  * For `<DataPath>/args.txt`, report 01 says it reaches C#. Report 09 says native code reads it but the C#
    `argument` string gets only `-UseDlc` and `-LogCategoryFilter`. I saw the reader at NE `0x3d59fc` look up only
    named options (`UnixDomainSocketPath`, `phantom_log`, `syslog`, …). I did **not** prove either way whether the
    rest is forwarded to `MainLoop.OnStarted`. **Inconclusive.**
  * Without a modified APK (`ArgInject`, analyst 09), the safe routes are: a DNS redirect of `eve.gameloft.com` to an
    HTTPS stub, with TLS validation off in C# (`OP/Federation.cs:107`); `phantomArgs` for the colon-free values; and
    `ServerInfos.json` dropped at `Data/5/f4c1f0f.f`.

---

## 3. Corrected minimal endpoint list for boot to `SHOW_MENUS` (TS only)

"Req" means the menu cannot be reached without it. Any answer to a request below must be HTTP 200 unless the row
says otherwise. Every URL the stub hands out must use an explicit non-standard port.

| # | Request | Strictly required response | Why |
|---|---|---|---|
| 1 | `GET {eve}/config/{cid}/datacenters` | `[{"name":"<dc>","preferred":true}]` | Req for any Federation with an empty datacenter: the client without `-DataCenter`, LocalAnubis without `-DataCenter`, the TS World without `-DataCenter` in `cmd_args` and no saved file (`OP/Federation.cs:372-397`, `OP/Datacenters.cs:90`). |
| 2 | `GET {eve}/config/{cid}/datacenters/{dc}/urls` | JSON object of strings with **`pandora`**. Add `ecomm_api_root` and `crm_iap` (the client IAP component throws without them). No `status`, or `"none"`. | `OP/Federation.cs:314-347`, `OP/Configuration.cs:29-47` |
| 3 | `GET {pandora}/locate/{svc}?client_id=` | Plain-text base URL, served at the host root | auth, matchmaker, storage, config: Req. alert, message, leaderboard, social: answer them, but failures are tolerated. |
| 4 | `POST {auth}/authorize` | `{"access_token":"…","scope":"<requested scopes, space-separated>"}` | Req for `credential_type=anonymous`, for `game`, and for `game` with `for_username`/`for_credential_type`. Scopes that appear: `auth lobby config`, `auth auth_admin_ro`, `lobby_game_admin`, `lobby_admin`, the template `InitialScopesServer` list, `config storage_ro` and others. A missing key is an NRE (`OP/Session.cs:420-423`). |
| 5 | `GET {auth}/users/me` | `{"account":"<non-empty id>","credentials":["anonymous:…"]}` | Req. A stable mapping from credential to account is needed so the client and the for-user TS session agree. An empty account leads to a `ClientConnectionData` throw. |
| 6 | `GET {auth}/encrypt_token?access_token&nonce` | Any non-empty body | Req, for scopes `lobby` (client) and `lobby_admin` (TS lobby login) |
| 7 | `GET {auth}/verify?access_token[&nonce&credential]` | `{"credential":"…","account":"…","client_id":"…"}` | Req: Finder `ParseUser` NRE (`OP/RoomUser.cs:153`) and Controller login |
| 8 | `GET {matchmaker}/lobbyconfigs/{cid}/ts` | `{"game":{"server_type":"ts","cmd_args":"-Level Levels/Menus.level -ServerType ts -EnvironmentSwitcherUrl http://127.0.0.1:<port>/ -DataCenter <dc> -SuperCredential game:<n> -SuperPassword <pw>"}}`. No `-` or spaces inside values. | Req; called twice (Finder, Controller). `-EnvironmentSwitcherUrl` can be dropped only if eve is DNS-redirected or the Federation handler is interposed. The super-credential pair can be dropped only if `ServerInfos.json` works. |
| 9 | Storage `profiles/{cred}/myprofile[/sel]` GET and set, plus the section saves | Report 03 schema (not re-verified here). On first run, a 404 on the game section triggers creation. | Req (uncaught on the handshake path) |
| 10 | `POST {auth}/games/mygame/alias` | `{"alias":"<string>"}` | Req on first run (`InitPlayerName`) |
| 11 | **`GET {auth}/users?device_id=…`** | `[]` | **Req on first run when `users/me` lists exactly one anonymous credential** (`GM/GameProfileSection.cs:155`). **Missing from the claim.** |
| 12 | `GET {config}/configs/users/me` | **Optional.** A 404 makes the TS fall back to the bundled preconfiguration. If answered 200, the document must be complete (`game` object at least, valid `offline_store` and `iap`). | `EN/CRMComponent.cs:439-467` |
| 13 | `GET {config}/configs/users/me/game` | Optional for the TS (1 s wait). Req for GS and automatch. | §1.5 |
| 14 | `GET {social}/events?event_ids=` | **Not called** when CRM has no TLE ids | §1.8 |
| — | Tolerated or non-blocking | `users/{fed}/compliance`, `alerts/me`, leaderboard `PostScore`, social profile update, messaging `PostMessage` (only on the profile-lock path), ETS | All caught or fire-and-forget |
| — | **Outside the Federation stub** | `http://ip.ecomapis.gameloft.org/location/?ip=<peer>` | Fail-soft. Make it fail fast (DNS to a closed local port), or answer `{"country_info":{"iso2":"US"}}`. |

---

## 4. Blockers a stub alone cannot solve, or that need a device test

1. **Delivering `:`-containing values** (eve URL, super credential) to the client. It needs `args.txt` (disputed,
   §2), an IL `ArgInject` patch (which means re-signing and then the native signature flag, report 08), or a DNS
   redirect plus `ServerInfos.json` at `Data/5/f4c1f0f.f` (native file-system acceptance unverified).
2. **Args swap fragility** (§1.4): the `Args` restore has no `finally`, and the client World sees the server `Args`
   during the TS load. This is not fixable from the stub.
3. **Timing.** The TS World is created and the full `Menus.level` loaded headless inside the 60 s Anubis receive
   window (`EN/AnubisClientSocket.cs:17`), and the TS handshake runs inside the 60 s Handshake window. Two Worlds
   share memory and the native engine on a phone. Embedded mode was clearly used in the editor (templates), but I
   found nothing showing it was ever run on Android. `-DisableTimeout` (`EN/GameApplication.cs:1201-1203`,
   `UT/AsyncTimeout.cs`) removes these limits, but it also removes protection against hangs.
4. **Native IAP** `LibInitialized`/`StoreRefreshed` with an empty or fake catalogue, and no Play billing: needed for
   the shop, not for the menu.
5. **Native online SDK re-initialisation** by the TS World (§1.9).
6. **Java `GameInstaller` first-run data validation and CDN** (report 01): a precondition outside this C# path. I did
   not re-verify it.
7. **User-interaction gates**: the language prompt and the age gate on first boot. They need taps, not servers.
