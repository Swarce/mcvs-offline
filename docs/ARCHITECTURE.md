# MCVS Offline — Architecture

Preservation project for **Modern Combat Versus** (Gameloft, Android, APK 1.17.32,
`com.gameloft.android.ANMP.GloftMVHM`). Gameloft's backend is gone; the content CDN still works.

Goals: fully offline play · full progression saved on the device · every item obtainable through
progression plus an optional unlock-all · bot matches · LAN/internet P2P.

All claims below are backed by `docs/research/` (reports `01`–`11`, plus the adversarial
`verify-C1`…`verify-C7` notes, which take precedence where they correct a report).

## 1. What the game already has (and what is dead)

| Layer | Status | Notes |
|---|---|---|
| Authoritative game logic: Transaction Server (TS: menus, meta, rewards, profile) and Game Server (GS: matches) | **Ships in the APK** | The same C# `GameServerComponent`, run as headless Worlds. |
| `ServerLocation.Embedded` dev mode (`Gameloft.Engine.LocalAnubis`) | **Ships, works in-process** | Lobby/Controller (TCP) + Finder (HTTP `localhost:51750`); `connect game` spawns TS/GS Worlds in the same process. |
| Netcode | **Ships** | TCP control frames + ENet UDP, 20 Hz, bots fill GS matches. |
| Game data | **CDN still alive** | Archived and verified with `tools/cdn-mirror` (report 10); the embedded host restores the installer's index from data already on the phone ([INSTALL-OFFLINE.md](INSTALL-OFFLINE.md)); decoder in `server/Core/Content` (GCBF/LZ4, crc32 names; report 11). |
| Gameloft "Federation" backend (eve, pandora, auth, Seshat storage, CRM, matchmaker `lobbyconfigs`, social/events, messaging, alerts, leaderboards, assets/coupons) | **Dead** (eve returns 404 for our client id) | **This is the only thing we reimplement.** |

The game saves **no** progression on the device. The TS/GS Worlds load and save the profile through
Federation storage, so a Federation emulator with correct storage semantics gives full persistence
with no game patch.

## 2. Components

```
server/        McvsOffline.Server — the Federation emulator (C#; one source tree, two builds)
  Core/        HTTP/1.1 server on TcpListener, router, MiniJson, accounts/tokens, storage engine, settings, logging
  Services/    one file/folder per Gameloft service (Eve, Pandora, Auth, Storage, Crm, Matchmaker, Social,
               Messaging, Alerts, Leaderboards, Assets, GameStats, Profanity, Misc catch-all), plus Unlocks and
               Admin (the in-game settings page: admin, admin-logs, admin-saves; design/settings-page.md)
  Content/     generated JSON (store catalogue, economy) produced by tools/content-gen; admin/index.html (the page)
hosts/
  standalone/  net8 console host: dev loop against a stock APK, LAN/community host, conformance target
  embedded/    Bootstrap compiled into McvsOffline.Server.dll for the game's Mono runtime
patcher/       McvsOffline.Patcher (net8 + Mono.Cecil): turns the player's own APK into the offline APK
tests/         unit tests, protocol tests, and a conformance harness that drives the REAL
               Gameloft.OnlinePlatform.dll (taken from the player's APK, never committed) against the server
tools/         content-gen (tables -> Content/), cdn-mirror (archive and verify the CDN data, offline install); planned: datapack (decode GCBF/LZ4 + names)
docs/          this file, protocol spec, setup guides, research/
```

### 2.1 Build constraints (important)

- **Shared code must compile for both targets**: `net8.0` (standalone, tests) and the game's
  **trimmed Xamarin.Android 10.3 / Mono BCL** (embedded). The embedded build references the
  shipped `mscorlib.dll`, `System.dll`, `System.Core.dll`, … from the player's APK (path given by
  `MCVS_REFS`; never committed) with `NoStdLib`.
- C# **7.3**. **No `async`/`await`** (does not compile against the shipped mscorlib; use threads,
  blocking I/O, `Task.FromResult`). Avoid APIs that the linker may have trimmed; prefer basic
  `System.Net.Sockets`, `System.IO`, `System.Threading`, `System.Text`, collections.
- **No Newtonsoft dependency** in shared code (the shipped copy is trimmed): use `Core/MiniJson`.
- **No HttpListener**: Mono's `HttpListener` rejects non-`localhost` Host headers for `localhost`
  prefixes (see verify-C6). Use our own minimal HTTP/1.1 server on `TcpListener`.

### 2.2 How the game reaches the server

Federation base URL: `http://127.0.0.1:51780/` (any non-80/443 port; `FormatUri` rewrites scheme
on default ports). One host at the root serves every service; `pandora/locate/{svc}` returns
`http://127.0.0.1:51780/<svc>/` (explicit `http://`, trailing slash: relative storage paths replace
the last segment of the located URL). The server must also answer `GET /` and tolerate `//config/...`.

Client boot arguments (`<GlfRootPath>/args.txt` = the folder that contains `Data/`; see verify-C1 for
the exact format — space/CR/LF delimited, no tabs, no BOM, case-sensitive enum values):

```
-ServerLocation Embedded
-AnubisCommand QuickLaunch
-EnvironmentSwitcherUrl http://127.0.0.1:51780/
-SuperCredential game:mcvsoffline
-SuperPassword mcvsoffline
-DeviceId <stable per-device id>
-DataCenter mcvs
```

`-DataCenter` makes every Federation (client, LocalAnubis, and through `cmd_args` the TS/GS Worlds) skip eve's datacenter
list (device test 1, STATUS.md).

The in-process TS/GS Worlds are built while `GameApplication.Args` = the **`cmd_args` from our
`matchmaker lobbyconfigs` response**, so those must repeat the eve URL and super credential:

- `lobbyconfigs/{cid}/ts` → `game.server_type = "ts"`, `cmd_args = "-Level Levels/Menus.level -ServerType ts -EnvironmentSwitcherUrl http://127.0.0.1:51780/ -SuperCredential game:mcvsoffline -SuperPassword mcvsoffline"`
- `lobbyconfigs/{cid}/gs` → `game.server_type = "gs"`, `cmd_args` **without** `-Level`.
- Values inside `cmd_args` must contain **no `-` and no spaces** (the Controller splits on `-`).

### 2.3 Two ways to run

| Mode | APK | Server | Use |
|---|---|---|---|
| **Dev loop** | stock, unmodified | standalone host on a PC; `adb reverse tcp:51780 tcp:51780`; `args.txt` pushed with adb | protocol work, logging every request |
| **Player build** | patched by `patcher/` | embedded in the game process | the actual offline game |

Embedded wiring (verify-C7): add `McvsOffline.Server.dll` to `assemblies/`; IL-patch
`NicePlaySDKDLL.OnInitialize` (reflectively invoked by `XamarinMainActivity.OnCreate` *before*
`MainLoop.Start`, Java `GameInstaller` and native init) to call `McvsOffline.Bootstrap.Init()`.
`Init()` starts the server thread and writes/merges the managed block of `args.txt` (native reads it
later, at `JNIBridge.NativeInit`). Delete `libaot-NicePlaySDK.dll.so` for each ABI kept (default: every ABI; `--abi arm64-v8a` drops the 32-bit `lib/armeabi-v7a`) (Mono "normal"
AOT mode falls back to JIT). Re-sign (zipalign + apksigner v1+v2). The native signature check has no
local effect; neutralising it is optional.

Mandatory IL patches for the in-process servers (device test 1, STATUS.md): `headless` (Game.dll) and `headless-engine`
(Gameloft.Engine.dll) keep client-only components out of the headless TS/GS Worlds (`InputSettingsComponent`,
`SoundGroupSnapshotComponent` get `[KeepInHeadlessMode(false)]`; null-safe HudGamepad and VfxAnimator calls; audio and
input singletons left alone) and turn `Component.Events.GetSafeAction` into a try/catch that applies to headless Worlds
only, reporting instead of ending the player's process. Mandatory `physics` (Gameloft.Engine.dll; addresses the leading,
not yet confirmed hypothesis for the device-test-2 hang): every multithreaded Havok world in the process steps through one
process-wide hkCpuThreadPool/hkJobQueue that serves one stepping thread at a time, and GameOptions_android.json makes every
World multithreaded (client physics deferred to a loading-pool thread). Server Worlds therefore get single-threaded Havok,
as on Gameloft's servers (`PhysicsComponent` constructor -> `McvsPhysics.UseMultithreading`), and every World's step runs
inside a process-wide, time-bounded gate (`UpdateSimulationSteps`); settings `embedded.physics.*` revert each part for A/B
runs.

Recommended extra IL patches (all optional, patcher flags):
- `waits` (default, hardening with no observed trigger): the pumping `TaskExtension.WaitAndWork` helpers wait in 100 ms
  slices (`WaitAny(handles, 100)`). This bounds the two wake-up gaps in the shipped code: a context re-enabled by
  `World.OnLoadedScene` raises no `WorkItemAdded`, and the `NativeTaskAdded` accessors install and remove the native
  callback with an unlocked check-then-act. Not covered: contexts registered after the wait began, because `Currents`
  is a snapshot. `DelayedPost` has no caller.
- `nativeonline` (default): `OnlinePlatformComponent.Init_private` (the native "GaiaInit") runs only when
  `settings.json` `embedded.nativeOnlineSdk` is true; with the internet on, the native online SDK froze the boot.
- `geo`: point the hard-coded `http://ip.ecomapis.gameloft.org/location/` (TS handshake, 100 s timeout vs
  60 s handshake) at our server.
- `bottile`: route the BOTMATCH tile to the full-progression GS flow (verify-C4: `NativeStartMatchWithBots`
  lambda `IL_0007 ldc.i4.2 -> ldc.i4.1`).
- `store` (default, device test 3): `KillSignatureRewardItem.GetAvailableId` no longer dereferences its null list for
  sounds (unowned sound -> its id, random/other -> null), and the `GameUnifiedStoreComponent.SelectItems` availability
  filter runs in try/catch (an item that throws is skipped and reported through `GameHooks.StoreItemException`).
- `signatures` (default, build 7): `PlayerProfileComponent.GetSignatureAssetsList` looks owned/new ids up in a
  `HashSet<string>` (lists of more than 16 ids) instead of `List.Contains` per table row, and
  `GameOfflineStore.GetKillSignItems` builds the avatar/background list once per call instead of once per owned item
  (the kill-signature screen froze for minutes after unlock-all).
- `debloat` (default, build 8): third-party SDK pieces that are dead offline, without touching any game feature.
  Manifest (`patcher/Manifest/ManifestDebloat.cs`, binary-XML element removal in `BinaryXml`): 44 exact elements go
  (the 20 ad activities, dead Facebook/Google Sign-In/AppInvite/AppLinks/GMP activities, GCM receiver/service/permissions,
  install-referrer and boot/network/ApplicationSetUp receivers, the Crashlytics and Facebook init providers,
  `io.fabric.ApiKey`, the location and maxSdk-18 storage permissions, `<library moat-mobile-app-kit>`); every target must
  match exactly once or nothing is written. Files: `lib/*/libcrashlytics.so`, `fabric/*.properties`,
  `assets/crashlytics-build.properties`. Gameloft.Engine (`DebloatEnginePatch`): `Crashlytics.Start` returns at once;
  no push-token request (`ShouldRegister` false; `RegisterForRemoteNotifications` keeps only its local-alarm purge);
  Facebook and Play Games are not initialised at menu start (`SocialLib_private.Init` -> NotSent like an unsupported
  Sns; a login tap fails one frame later through `GameApplication.Context`, like a failed login). Kept on purpose: the
  IAP code, KeyProvider, local push and `RECEIVE_BOOT_COMPLETED`, FacebookActivity and the app-id meta-data, Google
  Analytics, VIBRATE/BILLING, all dex code and Bind*.dll.
- `adminpage` (default, build 9): Settings › Account gets an "MCVS Offline" button. Game.dll
  `PlayerProfileComponent.GetAccountMenuButtonList` returns its list through `McvsOffline.AdminHooks.AccountMenuButtons`
  (one more entry, `customAction "mcvs_offline"`), and `ProcessAccountMenuButtonAction` first sends that action to
  `AdminHooks.OpenPage("account", World)`, which opens the settings page in the game's own browser with a one-time
  ticket ([design/settings-page.md](design/settings-page.md) §9.2).
- `adminlink` (default, build 9): Gameloft.Engine.dll `BrowserComponent.OpenUrl` first asks
  `AdminHooks.OpenIfPageMarker(url, this)`: the CRM inbox item's fixed link `http://127.0.0.1:<port>/_mcvs/ui/#news`
  (no secret) opens the page with a fresh ticket; every other URL runs the original body (§9.3).
- `forum` (optional): Game.dll `UIComponent.GoToForum` opens the page (`AdminHooks.OpenPage("forum", World)`, §9.4).
- `lan`: P1 Finder prefix `"*"`, P2 cache a real `PublicHost` at `LocalAnubisManager.Start`, P3
  `ClientSettings.LocalAnubisFinderHost = Args.RemoteIp ?? PublicHost` (verify-C6).

Project art (`--art auto|stock|<dir>`, default `auto`; not a `--patch` id; research/app-icon-and-launch-screen.md):
`art/android/**` is embedded in the patcher. With art, `patcher/Art` replaces the 20 launcher icon PNGs in place (same
names, CRC fingerprints, masters resampled to every density), inserts `v31`/`night-v31` configurations of the launcher
theme 0x7f0b017e (windowSplashScreenBackground and extras) into `resources.arsc` through one table reader/writer
(`patcher/Resources/ArscTable.cs`), and for a themed icon gives the spare `mipmap/ic_launcher_foreground1` six density
files and the adaptive icon a `<monochrome>` layer. No resource id moves. With no art the APK is byte-identical. A later
configuration of that theme (for example `v35`) would hide the splash items, because Android picks one configuration
per style; edge-to-edge fixes therefore go into the browser layouts, not into a theme configuration.

The three hook patches are fingerprint-verified like the others and call only `McvsOffline.AdminHooks` in the embedded
server DLL (`hosts/embedded/Admin/AdminHooks.cs`; `AdminHooksContractTests` keeps both sides in step). **Invariant I1:
the patched APK keeps `targetSdkVersion` 29** (`ManifestTargetSdkTests`, after every default patch): the settings
page's restart (the game's own `PendingIntent` without `FLAG_IMMUTABLE`), the share chooser (no package-visibility
filtering), the relaunch alarm (Android 10 background-start rules), the browser layout (no forced edge-to-edge) and the
`file://` share on Android 7–9 all rely on it.

## 3. Server behaviour (normative summary — details in docs/PROTOCOL.md and research/)

**Accounts & auth.** `auth/authorize` auto-creates accounts for unknown anonymous credentials and
echoes any requested scopes; tokens never expire; 401 (never 403) on auth failure. Stable non-empty
`account` per credential; `fed_id = "fed_id:" + account`. Implement `users/me`,
`users?device_id=` (→ `[]`), `games/mygame/alias`, `encrypt_token` (plain text) and `verify`
(`{credential, account, client_id}`, `account` must equal the profile fed id). Super-credential
sessions (`game:mcvsoffline`, incl. `for_username`/`for_credential_type`) act on behalf of users.
Tokens are opaque and **self-describing** (`<issuer>.<random>`) so LAN hosts can later forward
verification to the issuing device.

**Storage (Seshat) — get this exactly right (verify-C3).** One JSON document per account
(`myprofile`), deep-merge, dotted selectors / `include_fields`, atomic file writes, per-account
serialisation with read-your-writes. Saves return exactly **200**. Missing root → `200 {}`; missing
`_mcvs.gameProfile` → `200 {}`; missing `_mcvs.currentMatch` → `200 {}`. **No ETag** (a per-document
ETag makes every later save fail with 412). **Never 403/404 for existing data** (403 wipes the profile).
Also the IAP `inventory` section `{rewardId: int}` (a generic grant channel).

**CRM.** `configs/users/me` and `configs/users/me/game` must return **200** with a complete document
(the GS waits forever otherwise; a partial 200 is worse than an error for the TS). Base it on the
game's own `Config/crmpreconfiguration.json` (read from the player's data pack at runtime, never
redistributed) overlaid with our generated content and settings:
`game._ServerConfig.default.Matchmaking = {MinPlayers: 1, MaxOpenTimeInSeconds: 0}` (instant bot-filled
PvP), `game._ServerConfig.default.IsDevelopment` from settings (the dev cheat menu is **on** by default
over loopback — send `false` unless the player enabled it), `_OverrideTables` tweaks (e.g.
`MCVS_System.GameFeatures` `RedeemCoupons = 1`), and a generated `offline_store`.

**Economy / unlocks.** Everything purchasable for **VersusCoins** (the repeatable currency; HC is
finite) via `offline_store` and the generic `#VersusCoins` hub category (which can sell any reward
string, incl. weapons, camos, sounds, crates). Unlock-all, all opt-in through settings: dev cheat
menu, a coupon code (`assets coupons/{cid}/{code}` → `[{type:"UnlockMerc_2",quantity:1}, ...]`),
and the inventory ledger.

**Everything else** (social events, Hermes messaging/inbox, Kairos alerts, Olympus leaderboards,
game_stats, profanity, chat, clans, wall, bantool, Iris assets, geolocation): well-formed minimal
responses that never block; leaderboards return a sane `my_entry`/`total_entries` for divisions.
**Unknown endpoints are logged with full request details** and answered conservatively, so device
testing reveals what is missing.

**Settings** (`settings.json`, next to the data): port, `unlockAll`, `devMenu`, economy multipliers,
instant-bots, LAN mode, live ops (`liveops.*`, [LIVEOPS.md](LIVEOPS.md)), installer-data restore (`install.*`, embedded only,
[INSTALL-OFFLINE.md](INSTALL-OFFLINE.md)). Data dir: embedded `<GlfRootPath>/McvsOffline/` (user-visible, easy to back
up; removed on uninstall), standalone `./data/`. The in-game **settings page** (`/_mcvs/ui/`, build 9,
[design/settings-page.md](design/settings-page.md)) edits an allowlist of these keys in the server's copy, and manages
backups, save export/import (staged and applied at the next start, before the port opens) and logs.

**Local access.** Every request passes `LocalAccessGate` first: while bound to loopback (and not in LAN mode) a foreign
`Host` header gets 404 on every route (DNS rebinding; `security.hostCheck`), `/_mcvs/*` needs a loopback caller and
`X-MCVS: 1` on writes, and the page's API needs a session from a one-time ticket that only the game's own browser
receives.

## 4. Roadmap

The roadmap, with finished items struck through, is in [ROADMAP.md](ROADMAP.md). Device-test history and open
risks are tracked in [STATUS.md](STATUS.md) and `docs/research/verify-*.md` ("remaining_risks").
