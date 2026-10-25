# Status

What is implemented, how it was checked, what the device tests showed, and the checklist for the next device sessions.
Last updated after device test 1 (October 2026: Samsung Galaxy S22, Android 16; see "Device test 1" below).

**Summary:** the Federation server is complete for the offline single-player goals and passes 589 tests, including a
conformance harness that runs the game's own client libraries against it. The player build (patcher + embedded host)
ran on a device for the first time in device test 1: the embedded server, the hook and the args.txt block work on the
phone, and the boot got as far as the in-process TS loading its level. Two blockers were found and are fixed in this
build (a stall with the internet on, and a crash of the headless TS World); the next build also carries the diagnostics
that make the next device run conclusive. Device test 2 is the next step (expected log sequence below).

## Device test 1 (October 2026)

Samsung Galaxy S22 (SM-S901E, Snapdragon 8 Gen 1), Android 16 (SDK 36), patched APK at `eb50af6` (default patches), the
original game uninstalled first, the data pack downloaded again by the game's own installer. Two runs, logcat captured
with LogFox, plus one tombstone.

### What worked

The hook ran at +0.4 s, `args.txt is up to date`, the server bound 127.0.0.1:51780, the CRM document was built from the
player's data pack, and every request that reached the server was answered 200 in under 8 ms. The game's own Fatal log
lines reach logcat as `mono-stdout` (Gameloft.Logs' ConsoleLogger); the shipped `res/raw/args.txt` (`-LogCategoryFilter
Fatal`) mutes everything below Fatal.

### RUN 1, internet on: "loading" forever

Requests #1-#5 only: eve urls (client Federation, datacenter known from the saved `datacenter` file), locate auth,
authorize anonymous, users/me, then `GET config/<cid>/datacenters` on a new connection (the LocalAnubis Federation, no
`-DataCenter` in the args), answered 200 at 793.607 s, and then nothing for 20 s (no Fatal, no ANR). In RUN 2 the next
request (#6, eve urls of the same Federation) came 73 ms later.

* **Cause, verified part.** Every hop between two Federation requests is a `GameSynchronizationContext` continuation
  (no `ConfigureAwait(false)` on the request path), drained by the game thread once per frame in
  `GameApplication.UpdateWorlds`. So after #5 the game thread stopped draining its queue, and only with the network up.
  `OnlinePlatformComponent.Init_private` (posted to the game thread right after #1, for every World that knows its
  datacenter) is the managed entry of the native online SDK: `OnlinePlatformComponent_Init_private` posts the native
  `GaiaInit` event, whose handler starts the native "Gaia Thread" (Gaia::Update over 18 service slots, each under a
  pthread mutex, glwebtools/libcurl work) against hard-coded Gameloft hosts (`eve.gameloft.com:20001`, gameoptions,
  sink, ingameads, igp). It returned (the #2-#5 continuations ran after it), so it does not itself block.
* **Cause, inference (medium-high).** A game-thread call into the native SDK waits for a lock or a network timeout that
  the Gaia/curl threads hold while they talk to the real, half-dead hosts; in airplane mode DNS fails at once and
  nothing waits. The exact waiting call is not known (the release build has no logging of its own there); the next
  build's watchdog names it if the stall survives (see below).
* **Fix.** Patch `nativeonline` (default): `Init_private` calls the native SDK only when
  `McvsOffline.GameHooks.AllowNativeOnlineInit` says so, which answers `settings.json` `embedded.nativeOnlineSdk`
  (default **false**) and logs each request as a boot milestone. If our DLL cannot load, the native call is skipped.
  The managed args now carry `-DataCenter mcvs`, so no Federation asks eve for the datacenter list (#5 disappears, and
  a first run no longer goes through `SetDatacenter` -> a second `InitFederation`); `lobbyconfigs` `cmd_args` already
  carry it. `Federation.WaitForConfig` (no timeout when `GetConfig` fails) only feeds `Init_private`, so with the patch
  it can no longer hold anything up, and eve always answers 200.
* **What the native SDK did and what offline play loses.** Everything the game does with Federation is managed C#
  (report 09 §1). The singletons the per-frame native calls read are created by `MainLoop.Start`, not by GaiaInit (checked
  in libnativeengine.so: `IapStoreComponent_IsInitialized_private` reads `[0x16ff078]+0x40`, set at `MainLoop_Start`
  0x3d518c), so skipping GaiaInit dereferences nothing null. Lost, all dead offline anyway: native Gaia sessions
  (Janus, Seshat, Osiris, Hermes clients), ETS tracking (sink), ads (glads, ingameads), IGP cross-promotion and news, the
  Iris GameOptions download (the bundled `GameOptions_*.json` stay in use), native CRM point-cut popups. Not affected:
  crash reporting (Crashlytics, a separate path). **Open:** whether the native IAP library initialises offline, with or
  without GaiaInit; `IapStoreComponent` waits for it (no timeout) before it registers, and the unified shop, including the
  VersusCoins items, waits for that registration. The status line now reports it (`native IAP library initialized:
  yes/no`, also a boot milestone). The real-money shop cannot work offline in any case (empty CRM `iap`).

### RUN 2, airplane mode: crash when the TS World loads its level

Requests #5-#26 went through LocalAnubis, the QuickLaunch Finder, the Controller and the TS World's own Federation, CRM
`configs/users/me/game` and the TS lobby login; 1.7 s later the TS World's level load failed:
`Fatal: {World.Loading.cs}.ExecuteLoadLevel:399` with `NullReferenceException at Game.InputSettingsComponent.OnStart
[0x00011]`, rethrown by `MoreAsync.FireAndForgetTask` on the game thread, `UpdateWorlds:1048`,
`CurrentDomain_UnhandledException:852`, then Mono's `exit()` (the tombstone is the Adreno driver crashing in `exit()`:
noise, not a separate fault).

* **Cause (verified in code and IL).** The in-process TS/GS are headless Worlds built from the same SceneComponents.tx as
  the client. A component is created in a headless World unless it is `[KeepInHeadlessMode(false)]`; `InputSettingsComponent`
  has no attribute, and its `OnStart` dereferences `World.LocalStorage`, which is null in every headless World
  (`LocalStorageComponent` is `[KeepInHeadlessMode(false)]`). In this release build `Component.Events.GetSafeAction` is
  `ldarg.0; ret` (the server build's try/catch wrapper was stripped), so one exception in any lifecycle event of any
  server-World component ends the player's process. `ErrorHandler(Args.Headless)`, `-Headless` and `-Server` play no
  part (template diagnostics only; read once in the GameApplication constructor).
* **Fix: mandatory patches `headless` (Game.dll) and `headless-engine` (Gameloft.Engine.dll)**, every site verified against
  the 1.17.32 IL first:
  * `InputSettingsComponent` gets `[KeepInHeadlessMode(false)]` (it would also reset the player's process-wide input
    sensitivity and invert settings from a server World);
  * `GameplayComponent`: the four `OfType<HudGamepadComponent>().FirstOrDefault().BindingTo*Preset()` calls (OnStart on
    every GS map load, OnStop at teardown, the two pause handlers) are null-safe (HudGamepad is dropped headless);
  * `FlameZoneComponent.BroadcastEnd` and `BacktrackTimeDistortionExplosion.FadeOutEffect`: `GetComponent<VfxAnimatorComponent>().TriggerEnd()`
    null-safe (BroadcastEnd would throw, and send its RPC, every frame);
  * `SoundManagerGameComponent` and `SoundGroupSnapshotComponent`, `GameInputManagerComponent`: server Worlds no longer touch
    the player's VoxManager (OnStop stopped every sound group but six) or poll the gamepad a second time per frame;
  * the safety net: `GetSafeAction` returns `McvsHeadlessSafeAction.Invoke`, which calls the event directly for client
    Worlds (unchanged, fail-fast) and, for headless Worlds only, catches the exception and reports
    `[McvsOffline] headless world: <Type>.<Event> threw, the rest of it is skipped: <exception>` (logcat mono-stdout, first
    200) and to `GameHooks.HeadlessComponentException` (bootstrap.log, boot trace). Each such line names a component that
    needs a precise fix.

  The headless audit (all 70 TS scene components, Menus.level, every PvP map minus ClientSide, all 877 templates) found
  no other definite crasher in the TS; the GS ones are the GameplayComponent and Vfx sites above. Not changed:
  `ChangeGraphicQuality` on headless level loads and `FireAndForgetTask` for headless loads (low impact; reconsider if
  device logs show them).

### The 0-byte bootstrap.log and server.log

Both files existed with 0 bytes although logcat had every line. The old `RotatingFile` set a `failed` flag on its first
exception and never said anything again, and it kept the file open for the whole process: Android's MTP server refreshes
a file's size for a connected PC only on `IN_CLOSE_WRITE` (AOSP `MtpStorageManager`: `sendObjectInfoChanged` is sent on
CLOSE_WRITE only), so a PC that saw the file created kept showing, and copying, 0 bytes. Static analysis cannot tell
which of the two happened; the replacement settles both: `SafeLogFile` queues lines (no I/O on the caller's thread) and
writes each batch with open, seek, write, flush and **close**; every failure names its stage and goes straight to logcat;
after 3 failures it continues in internal storage; at start it logs `exists, N bytes before this run`.

### Diagnostics added for device test 2

| What | Where to find it |
|---|---|
| The game's own log at every level (`--game-log all`, default for now: `res/raw/args.txt` without `-LogCategoryFilter`) | logcat `mono-stdout`; copy in `logs/game.log` (settings `gameLog.mute`, default `profile:debug`) |
| Unhandled-exception reporter, installed first in `Bootstrap.Init` (AppDomain, AndroidEnvironment, unobserved tasks). Separate budgets: up to 3 fatal reports (the first always) and up to 5 non-fatal ones, so unobserved task faults can never use up the crash report | logcat `F McvsOffline: FATAL unhandled exception ...`; `logs/crash-*.txt` (fsync'ed); unobserved task faults: `E McvsOffline: NON-FATAL ...`, `logs/exception-*.txt` |
| Boot trace: one `[boot] +12.345 s` line per milestone of the boot chain (recognised from the requests), the native-init and headless-World hook calls, failed requests, a `quiet: no request for N s` line during boot | logcat `McvsOffline`, `logs/bootstrap.log` |
| Game-thread watchdog: one heartbeat a second through the game's work queue; on a stall of 5/15/60/180 s, the game thread's kernel state, wchan and syscall, the busiest threads, the Java stacks of the game and UI threads (at 15 s), the server's last request | logcat `W McvsOffline: [watchdog] STALL ...`, `logs/bootstrap.log` |
| Status line every 60 s for 10 minutes: boot trace, watchdog, every log file's size, bytes written, drops and last error, native IAP state | logcat `[status]`, `logs/bootstrap.log` |
| How the previous processes ended (`ApplicationExitInfo`, Android 11+; ANR traces and tombstones saved) | logcat `[exit]` 3 s after start, `logs/exit-*` |
| A phone-readable copy of bootstrap.log, server.log, game.log and the crash files (Android 11+) | Files app > Downloads > `MCVS-Offline` |

All of it uses only members of the game's trimmed assemblies (a Cecil test resolves the 170 type references, 751 member
references and 8,256 body operands of the built DLL against `MCVS_REFS`, none missing, no linker stubs), never throws
into the game, and does no I/O on the game or UI thread beyond what the host did before.

### Expected log sequence in device test 2 (up to the main menu)

`adb logcat -s McvsOffline mono-stdout` or LogFox filtered on the game. Request numbers are for the internet-on and the
airplane run alike; the `[boot]` lines follow the request they name.

1. `[crash] hooks: AppDomain ok, unobserved-tasks ok, AndroidEnvironment ok`, `embedded host starting`, `[boot] +0.4 s MCVS
   Offline host started by the game`, `[logfile] .../logs/bootstrap.log exists, N bytes before this run` (and for
   game.log, server.log), `[gamelog] Gameloft.Logs levels: Fatal Error Warning, muted profile:debug, copy to .../game.log`.
2. First start of this build: `args.txt updated (...): ... -DataCenter mcvs` and `[boot] args.txt updated`; later starts:
   `args.txt is up to date`.
3. `[boot] offline server listening at http://127.0.0.1:51780/`, `[crm] CRM document built`, `[diag] [mirror] copies of
   the logs go to /storage/emulated/0/Download/MCVS-Offline`, `[diag] [exit] how the last N game processes ended`.
4. `#1 GET /config/<cid>/datacenters/mcvs/urls` -> `[boot] client Federation: eve config (#1)`, then
   `[boot] native online SDK init requested (... call 1): skipped, settings embedded.nativeOnlineSdk is false`,
   `[watchdog] game thread is tid N 'Thread-N'`, and the game's own `mono-stdout` Info/Debug lines (`LoadConfig for ...
   on mcvs`, `Send GET request ...`).
5. `#2 locate/auth`, `#3 POST /authorize` (anonymous) -> `[boot] client anonymous session`, `#4 users/me`.
6. **No `datacenters` request any more.** `#5 .../mcvs/urls` -> `[boot] second Federation: eve config (LocalAnubis)`.
   **This is where RUN 1 stopped (internet on):** right after the LocalAnubis Federation's first request (then `#5
   datacenters`, now `#5 .../mcvs/urls`). If `#6 locate/auth` never comes, the stall reproduced at the same step; if `#5`
   itself never comes, it moved one step earlier. Then `#6 locate/auth`, `#7 authorize game:mcvsoffline [auth
   auth_admin_ro]` -> `[boot] LocalAnubisManager.Start: super session`, `#8 users/me`.
7. `#9 verify`, `#10 locate/matchmaker`, `#11 authorize [lobby_game_admin]` -> `[boot] QuickLaunch: Finder session`,
   `#12 lobbyconfigs/<cid>/ts` -> `[boot] QuickLaunch: lobbyconfigs ts`, `#13 encrypt_token nonce=LocalAnubisNonce`,
   `#14 verify credential=system:Anubis` -> `[boot] Controller login of the client`, `#15 lobbyconfigs ts` ->
   `[boot] Controller 'connect game': creating the TS World`.
8. TS World: `#16 urls` -> `[boot] third Federation: eve config (TS World)`, locate/auth, two `authorize` with
   `device_id=default` -> `[boot] TS World: game-server session`, users/me x2, locate/config, `configs/users/me/game` ->
   `[boot] first CRM config: /configs/users/me/game N ms after the TS session` (a WARN if over 1000 ms), a second
   `native online SDK init requested (... call 2): skipped`, `encrypt_token`, `verify` -> `[boot] TS lobby login: the TS
   World now loads Levels/Menus.level`. **This is where RUN 2 crashed.**
9. About 2 s later, **no** `Fatal: {World.Loading.cs}.ExecuteLoadLevel:399` and ideally no `[McvsOffline] headless world:`
   line. Then the TS handshake: `authorize` on behalf of the player -> `[boot] TS handshake: session on behalf of the
   player`, `alerts/me` -> `[boot] alert stream open`, the first `profiles/...` read -> `[boot] TS handshake: first profile
   read`, `users?device_id=` -> `[boot] TS handshake: accounts for this device`, section creations and the game-profile
   save -> `[boot] first profile write`, `location/?ip=` -> `[boot] TS handshake: geolocation lookup answered locally`,
   `games/mygame/alias` -> `[boot] TS handshake: games/mygame/alias`, `configs/users/me`.
10. The client loads Menus.level (no requests), shows the language prompt and the age gate (unless `crm.skipAgeGate`), then
    the main menu; `[boot] quiet: no request for 10 s ...` once the menu is idle is normal. Within a minute:
    `[status] boot: ...; watchdog: ...; log bootstrap.log: ...; native IAP library initialized: yes|no` and the matching
    `[boot] native IAP library initialized` milestone.

If it stops anywhere: the last `[boot]` line names the step, `[boot] quiet: no request for 10 s since #N` follows, and if
the game thread is stuck, `[watchdog] STALL` lines (5, 15, 60 s) give its kernel state (futex = a lock, ppoll/connect/
recvfrom = network on the game thread, R = busy) and, at 15 s, its Java stack. A crash leaves `crash-*.txt` in
`logs/` and in Download/MCVS-Offline, and `[exit] ... CRASH_NATIVE` at the next start.

## 1. Implemented

| Area | What | Code | Protocol note |
|---|---|---|---|
| Core | HTTP/1.1 server on raw sockets (keep-alive, chunked, `Expect: 100-continue`, streams), router with re-dispatch, MiniJson, accounts and self-describing tokens, atomic writes with file and directory fsync, settings with merge-overrides, request/unknown-endpoint logging, GCBF/LZ4 data-pack reader | `server/Core` | [MODULES.md](MODULES.md) |
| AUTH | eve, pandora (locate, service-prefix aliases), authorize (anonymous, game server, on-behalf, transfer code), users, compliance, aliases, credential links, `encrypt_token`/`verify`, `IAuthenticator` | `server/Services/{Eve,Pandora,Auth}` | [auth.md](protocol/auth.md) |
| STORAGE | Seshat profiles with verbatim values, nested `include_fields`, deep merge, no ETag, 401-never-403, arrival-order lock, cache, backups, corrupt-file recovery (503, never "missing"), batch/backups/visibility/data endpoints, `IProfileAccess`, LAN seam | `server/Services/Storage` | [storage.md](protocol/storage.md) |
| CONTENT | CRM over the player's own preconfiguration (validated against every client cast), matchmaker `lobbyconfigs`, unlock coupons, local Iris assets, generated VersusCoins economy (1,864 entries) | `server/Services/{Crm,Matchmaker,Assets}`, `server/Content`, `tools/content-gen` | [content.md](protocol/content.md), [ECONOMY.md](ECONOMY.md) |
| MISC | Hermes mailboxes, Kairos alert stream, Olympus leaderboards with league groups, Osiris events/profiles, clans, wall, group chat, profanity, game_stats, BanTool, geolocation, tracking/marketing catch-alls | `server/Services/{Social,Messaging,Alerts,...,Misc}` | [misc.md](protocol/misc.md) |
| Unlocks | `settings.unlockAll` through the inventory ledger (starts only once the game profile and the inventory section are stored, waits for the player's alert stream, backup, ordered batches with the merc unlock re-checked before each, `set_profile` alerts) | `server/Services/Unlocks` | [unlocks.md](protocol/unlocks.md) |
| EMBED | In-game bootstrap (game root via the game's own Java helpers, `args.txt` managed block with `-DataCenter`, device id, logcat, server thread); diagnostics (crash reporter, self-reporting log files, boot trace, game-thread watchdog, game-log bridge, phone-readable copy, exit reasons); patcher (hook, headless, headless-engine, nativeonline, geo, bottile, finder, lan; `res/raw/args.txt` for `--game-log`; vendor-wide manifest permission renamed into the package namespace; AOT strip; raw zip rewrite; v1/v2/v3 signing) | `hosts/embedded`, `patcher` | [PLAYER-SETUP.md](PLAYER-SETUP.md) |
| Hosts | Standalone net8 console host; embedded DLL compiled against the game's trimmed Mono BCL (ILVerify-clean) | `hosts/standalone`, `server/McvsOffline.Server.Embedded.csproj` | [DEV-LOOP.md](DEV-LOOP.md) |

### How it was checked

`MCVS_REFS=<apk assemblies> MCVS_REQUIRE_EMBEDDED=1 MCVS_REQUIRE_REAL_CLIENT=1 scripts/build.sh`: net8 solution, embedded
build against the game's own BCL, then all tests. Result after the device-test-1 fixes: **McvsOffline.Tests 485/485,
McvsOffline.Embed.Tests 105/105, 0 skipped**:

| Tests | Count |
|---|---|
| Core (HTTP, router, JSON, accounts, settings, storage helpers, data pack, embedded-DLL references, libc SO_REUSEADDR, log files, boot trace) | 145 |
| AUTH | 64 |
| STORAGE | 83 |
| CONTENT (including the generator's own Python tests and the real data pack) | 73 |
| MISC | 110 |
| Unlocks | 8 |
| End-to-end conformance (real client) | 2 |
| Embedded host (including the crash reporter and its separate fatal/non-fatal budgets, the watchdog and a Cecil resolution of every reference of the built DLL against the game's assemblies) | 56 |
| Patcher (including the binary manifest rewrite, `res/raw/args.txt`, and the device-1 patches applied to the real Game.dll and Gameloft.Engine.dll, then executed and JIT-compiled on net8) | 49 |

**Real-client conformance** (the player's Gameloft.OnlinePlatform, Gameloft.Engine, Game, Gameloft.Templates and
Gameloft.Util loaded from `MCVS_REFS` into net8; mandatory in `build.sh` when the folder has them). Flows that ran
through the game's own code against the complete server, over HTTP:

- **Boot** (`Conformance/EndToEndConformanceTests`): eve `GetConfig` (status None, 7 urls); `LocateService` for 18
  services; anonymous `OpenSession`; `GetAccountInfo`; game-server `OpenSession` and on-behalf `OpenSessionForUser`;
  `GetAccountsForDeviceId` (`[]`); `CreateAlias`/`LookupAlias`; `AddCompliances`; CRM `GetCrmConfig("game")` on the GS
  session (`IsDevelopment=false`, `Matchmaking 1/0`, `RedeemCoupons=1`); `Crm.GetStandardConfiguration` +
  `StandardCrm.Data` (every client parser: 1,864 `OfflineStoreData` items, `IapStoreData`, point-cuts, fatigue
  groups, events, ads, game parameters) and the `iap` selector; R1 `GameProfileSection.Load` (new profile, null
  template); R2/R3 `ProfileSection.LoadSections` of the five TS sections; R4 `GameProfileSection.Save` with a dump
  written by the game's own Templates writer from the game's `DefaultProfile.tx`, then again at 1.4 MB; R5
  `SaveSections`; R11 language; R8/R9 currentMatch with the GS's own token; `EncryptedAccessToken` → `Verify` →
  `RoomUser.FromAccountInfo` on an HTTPS-forced LocalAnubis Federation; `lobbyconfigs` ts/gs through LocalAnubis
  `GameConfig.FindGame`, `Controller.GetConfigArgs` and `GameApplication.Arguments.FromString`; coupon
  `AssetsService.RedeemCoupon` → `Json.LoadArray` → `RewardHelper.CreateRewardItem`; **server restart** with a new
  client: same account and alias, R1 byte-exact at 1.4 MB, read back identically by the game's `TemplateReader`,
  sections persisted; `GetReadOnlySessionForUser` reading the player's profile from another game-server session.
  80 requests, all 200 on known routes.
- **unlockAll**: the grant starts after the real `LoadSections` (R3 creations) and `GameProfileSection.Save` (R4),
  once the real `AlertListener` is open (the granter's own readiness signal, checked); 11,334 ledger keys read by the
  TS's `IapStoreComponent.InventorySection`, all JValue integers in order, 4 `set_profile` alerts received by the real
  `AlertListener`, every key accepted by `RewardItem.Parse`.
- **AUTH** (`Auth/RealClientConformanceTests`): anonymous, game-server, on-behalf and LocalAnubis sessions; first-run
  calls; transfer codes; recovery through the client's own 401 handling after the server lost its tokens and accounts.
- **MISC** (`Misc/ClientConformanceTests`, now against the real AUTH): Hermes gifts and deletion, the Kairos stream,
  `LeaderboardData`, `SocialEvent`, social profiles, clans and the clan wall, the chat room stream, profanity,
  game_stats; the time-limited-event validator agrees with the game's `ParseTemplate` on 36 templates.
- **Embed**: our `args.txt` block through the native flattening and the game's real `GameApplication.Arguments`
  parser (`Embedded` / `QuickLaunch` / URL / super credential / device id / LAN host); the `geo` patch applied to the
  real Gameloft.Util and its `GetRegion` answered by our server; patches checked on the real APK (dry run); the real
  manifest round-trips byte for byte through the patcher's binary-XML code, and after the permission rename
  `aapt2 dump xmltree` is the original dump with exactly the three `glshare` uses renamed.

Also checked by hand: the standalone host with curl (boot sequence, byte-exact storage, CRM, `lobbyconfigs`,
lobby hand-off, coupons, geo, alerts with a pushed gift, unknown-endpoint logging, `unlockAll` after a restart, and a
new player's first load replayed request by request: R1, R2, the four R3 creations, R4, the alert stream, then a late
R3 `inventory` reset mid-grant); the patcher on the real APK: `apksigner verify`, `zipalign -c`, `aapt2 dump xmltree`
(every declared permission package-scoped) and `aapt2 dump badging` of the output; ILVerify on the embedded DLL
against the game's BCL (no findings).

### Review fixes (October 2026)

| Finding | Fix | Tests |
|---|---|---|
| unlockAll started at the first write under `_mcvs` (a new player's first R3), so the R3 `inventory` Set could erase `UnlockMerc_ALL_MAX` after it was written and the merc-dependent keys became alternate rewards | Trigger on the game-profile section Sets only, and grant only when the stored profile holds the game profile and the inventory section; not-ready accounts are retried at the next save; the ledger is re-read before each batch and the merc unlock rewritten first if it is gone (and heads every merge); the first batch waits for the player's alert stream; the backup now holds the game profile | `Unlocks/UnlockGranterTests` (first-load sequence, mid-grant reset, stream wait, selectors), conformance |
| The re-signed APK redeclared the vendor-wide signature permission `glshare.permission.ACCESS_SHARED_DATA`: `INSTALL_FAILED_DUPLICATE_PERMISSION` next to any official Gameloft game | `patcher/Manifest`: the pool string is renamed to `<package>.mcvs.glshare.permission.ACCESS_SHARED_DATA` (permission, uses-permission and the KeyProvider's `android:permission` together); report 08 §L corrected | `Patcher/ManifestPatchTests`, pipeline and real-APK tests |
| A `settings.json` with a syntax error was replaced by the defaults when the embedded host added its keys | `Settings.IsFallback`; `Save()` refuses for it; the host skips its save; an unreadable file is never written back | `SettingsTests`, `EmbeddedHostTests` |
| Mono's `SetSocketOption(ReuseAddress)` also sets SO_REUSEPORT, so a same-UID second listener could share 51780 | The embedded listener sets SO_REUSEADDR alone through libc (`SocketReuse`) | `SocketReuseTests`, `EmbeddedAssemblyTests` (libc imports) |

What the harness cannot show: Mono's HTTP stack (the client ran on net8's `SocketsHttpHandler`), anything native
(Gaia, JNI, the engine's GameObjects), the Worlds themselves (no TS/GS World ran), timing on a phone. Device test 1 covered
the first part of the boot on a real phone (see above).

## 2. Known gaps (by design or not yet built)

- **LAN (M3)** is experimental: guests must use the host's server for everything (topology A). Tokens issued by a
  guest's own server are unknown to the host (401, the log names the issuer); storage, mailboxes and alerts are not
  forwarded between devices (`IProfileStoreLocator` is the seam).
- **Division seasons** advance only when time-limited events are served: no `content/social/events.json` and no
  `ISocialEventProvider` ship yet, so divisions work but seasons do not roll (MISC note).
- The CRM `iap` catalogue is empty on purpose (it would start real Google Play purchases nothing can fulfil).
- A brand-new account's root GET is `{}` without the server-maintained fields (`credential`, `datacenter`,
  `created`) until its first save; every game reader defaults them (checked in the conformance harness).
- Scopes are recorded but not enforced; `auth.requireSuperPassword` is off by default (any `game:` credential may act
  on behalf of players).
- Native SDK calls (global device id, ads, ecommerce) have unknown formats: they reach the unknown-endpoint log.
- The DLC download path (inactive, `UseDlc=false`) cannot work with our `http://` locate answers.
- The CDN mirror and data-pack tools described in the research (`tools/cdn-mirror`, `tools/datapack`) are not in this
  repository yet; the server's own data-pack reader is in `server/Core/Content`.
- Internet P2P (M4) and the preservation extras (M5) are not started.

## 3. Device-test checklist (priority order)

Run the dev loop first ([DEV-LOOP.md](DEV-LOOP.md)): it needs no patch, and the server log on the PC shows every
request. Then the player build ([PLAYER-SETUP.md](PLAYER-SETUP.md)). Sources: the `remaining_risks` of
[verify-C1](research/verify-C1-args-redirect.md) to [verify-C7](research/verify-C7-integrity-patching.md) and the
modules' open issues.

### P0: does it boot to the menu

1. **args.txt is read** (dev loop). `adb push` to `Android/data/<pkg>/files/` works and the game reads a shell-owned
   file on Android 11-15; the native VFS opens the absolute path; the first `GET /config/.../datacenters` reaches the
   PC (verify-C1 §5). If nothing arrives: BOM, tabs, `Embedded` spelling, `SDFolder` on another volume.
2. **The embedded server starts** (player build). Confirmed on the S22 in device test 1 (hook at +0.4 s, server on
   127.0.0.1:51780, CRM built, args.txt block in place). Still to see: `[logfile] ... exists, N bytes` with N > 0 and the
   copies in Download/MCVS-Offline; a quick kill-and-restart binds 51780 again (no "SO_REUSEADDR not set" warning).
3. **The TS World loads within the timeouts.** Device test 1 (RUN 2): the TS reached its level load 3 s after the first
   request and failed 1.7 s later (the headless crash, fixed). Check the expected sequence above from step 8 on: no
   `ExecuteLoadLevel:399`, no `[McvsOffline] headless world:` line, the TS handshake requests, the menu. With the internet
   on (RUN 1 stalled after the LocalAnubis datacenter request), steps 6-7 must follow without a `[boot] quiet` line.
4. **The 1 s CRM window.** The TS's first `GET /configs/users/me/game` must be answered within 1 s of the TS starting,
   or `IsDevelopment=true` leaks into that session (verify-C5). The document is pre-built at startup; compare the
   request's timestamp with the server's "CRM document built" line on a slow phone. Device test 1: 229 ms after the TS
   session (the boot trace now prints this and warns above 1000 ms).
5. **Mono transport.** `Expect: 100-continue` on the large R4 save; `fed_id%3A` vs `fed_id:` in paths (both accepted;
   the log shows which); the chunked `text/event-stream` read line by line (`GET /alerts/me` stays open); no HTTPS
   anywhere (all our URLs are `http://` with an explicit port) (verify-C3 §8, MISC notes).
6. **Native IAP and online SDK.** The native online SDK (GaiaInit) is off by default now (`nativeonline`; turn it back on
   with `embedded.nativeOnlineSdk: true` to compare). Watch `native IAP library initialized: yes|no` in the status line:
   with "no", `IapStoreComponent` never registers and the unified shop (VersusCoins items included) cannot load: open the
   shop and the VersusCoins tab. If it hangs, the fallbacks are the report 04 §14.5 patch (raise LibInitialized and
   StoreRefreshed with an empty store) or `crm.iap=preconfig`. Also look for `service-prefixed path`, `locate of unknown
   service` and unknown endpoints such as `/users/me/approvals/` (verify-C2 §4.4-4.5, AUTH and CONTENT notes).
7. **First-boot gates.** Language prompt and age gate (`crm.skipAgeGate`); after the forced uninstall of the original
   game, the Java installer's online data validation (`pack.info` is lost; offline escape hatch
   `qaTestingConfigs.txt SKIP_VALIDATION=1`, report 10 §4.3, untested) (verify-C2 §4.6-4.7, EMBED notes).
8. **No 4xx on the first-install load path**, in particular none on `profiles/...` (a 403/404 there resets the profile).

### P1: progression is kept

9. **Save and reload.** Play, wait for the 5-minute autosave or a match end (R4 then R5 `200` in the log), kill the
   app, start again: same profile. `data/storage/profiles/<account>.json` grows; backups appear every 30 minutes.
10. **Match end on the GS** saves the result; the bot-match resume race (verify-C3 §4): a match result followed
    quickly by a resume GET must not restore the pre-match state.
11. **Bot matches.** With the `bottile` patch the BOTMATCH tile goes through the GS flow and pays full rewards
    (trophies, MVP, merc XP) once PvP is activated; quick PvP starts at once and is filled with 7 bots (`instantBots`)
    (verify-C4).
12. **The GS's Lobby round-trips** (`encrypt_token`/`verify` for its own super session, `GetRoomInfo`) never fail: a
    failure there plays the match and grants nothing (verify-C4 §4.1). Fed ids must match on both paths (§4.3).
13. **Matchmaking after a network change** (Wi-Fi or airplane mode toggled while the game runs), fixed by the default
    `finder` patch; drop it with `--patch nativeonline,geo,bottile` if it misbehaves (verify-C6 §2.2, EMBED notes).
14. **Divisions and leaderboards.** `SelfData` gives a rank in a 100-entry league group; promotion and demotion work;
    `leaderboards.virtualTopScore` is plausible against real trophy gains (MISC notes).

### P2: everything obtainable

15. **unlockAll.** On a brand-new player the log shows `waiting: no game profile saved yet` for the R3s, then the
    grant after R4 once the alert stream is open (no "no alert stream" warning); the TS receives `set_profile` on the
    player's alert stream and claims each batch, the mercs first; reward popups with 400 keys are acceptable; check
    whether the GS also diffs and double-grants ([unlocks.md](protocol/unlocks.md)).
16. **Coupons.** "Redeem code" shows under Settings → Account (`RedeemCoupons=1`); `UNLOCKALL` gives one inbox
    message of about 11.3k rewards (UI and save size untested; the split codes `UNLOCKMERCS`, `UNLOCKCAMOS`, ... are
    the fallback) (verify-C5 (b), CONTENT notes).
17. **The VersusCoins hub tab** renders the reward types we sell there (weapons, gear slots, mod tier 4, crates, VIP,
    packs) and pack names localise (verify-C5, CONTENT notes).
18. **Economy multipliers** sent as `*x` cells come back as integers in the game's typed tables;
    `DailyShop_Cosmetics.Type` really is the price currency (verify-C5).
19. **Developer menu** (`devMenu: true`): the 6-tap gesture opens `menu_cheats`; the TS sees `127.0.0.1` as IPv4 so
    the handshake reports a developer (verify-C5).
20. **Inbox.** A gift posted to the secured mailbox shows up and can be claimed and deleted; after a forced kill the
    `multipleConnectionDetected` notice does not kick the player (MISC notes).

### P3: performance and LAN

21. Startup and first-use time with JIT instead of the deleted AOT images (`--patch none` keeps the most AOT code);
    the cold 1.4 MB profile read (about 200 ms on a desktop) on the phone (EMBED notes, verify-C7 §8).
22. LAN (experimental): `gethostname()`/interface order and the `PublicHost` the `lan` patch picks; UDP 50034
    broadcasts without a MulticastLock; inbound TCP/UDP to a foreground host under Doze; CPU/RAM of one TS World per
    guest; template-injected Finder host (verify-C6 §7).
