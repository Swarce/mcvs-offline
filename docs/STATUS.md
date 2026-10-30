# Status

What is implemented, how it was checked, what the device tests showed, and the checklist for the next device sessions.
Last updated for build 19 (October 2026, on `wip/pc-port`; APK and PC kit): the game's Settings menu without the tiles
that cannot work offline (both platforms), and on the phone the Steam version's PERFORMANCE page (graphics, antialiasing,
screen scale, frame rate limit up to 120, low resolution maps) with GRAPHICS SETTINGS and FRAME RATE LIMIT out of
Gameplay Settings; not yet tested on a phone or on Windows; see "Build 19". Build 18: on the owner's PC build 17 still ran the
game's own `Game.dll` (`store NO, inbox NO`) although every other file came from `McvsOffline\bin`; the cause was most
likely MCVS Offline's own start code, which loaded `Game` by a partial name before the game did; build 18 no longer
loads any game assembly itself and logs who loaded one from the game folder; see "Build 18". Build 17: the big unlock
codes froze the inbox (one inbox message with thousands of rewards); the codes now go straight into the inventory
ledger, and the new `inbox` patch bounds the inbox for any message, also one already in a profile; see "Build 17". Build 16 (PC only, no APK): the
build-14/15 PC config most likely made Windows start the game without MCVS Offline (the owner's "game fails to
connect"); build 16 writes the WR2-proven config again and says when a start ran without us; see "Build 16 (PC only)". Build 15: rotating Special Events (the Battle menu's Arena box,
filled with bots, a new mode every day, or one pinned mode), an optional Practice tile (off by default) and the
corrected match-mode docs (the full-reward bot match is Battle › Classic multiplayer); server only, not yet tested on
a phone; see "Build 15". Build 14: the PC port's fixes after the second Windows round (WR2:
the game ran its own `Game.dll`; build 14 loads ours and says when it does not, and captures crashes and freezes),
ready for the third Windows round, plus small shared log and page changes on Android; see "Build 14". Build 13 (not
yet tested on the phone): the Android 'game' manifest flags, a Settings › Account menu with only Redeem code and MCVS
Offline, log cleanup at every start; see "Build 13". Before that, build 12 and device test 9 (the owner's Galaxy S22,
Android 16): the ASTC HD data pack and the graphics profiles work, and builds 10 to 12 (target Android 15) install and
play; see "Build 12 and device test 9". Before that: the build 11 device test (the HD installer could not write in `McvsOffline/hd/`; fixed in build
12). The build 11 plan put HD textures into `Data/` ("Build 11 (plan)"). On build 10 the HD texture probe showed that
route B fails and route A works. Build 10 targets Android 15 and adds graphics profiles and the PC test kit ("Build
10"). Device test 8 tested the MCVS Offline page ("Device test 8", "Build 9"). The Cosmetic crate starts on 2026-11-09
and is not yet tested on the phone. Not yet merged into `main-dev`: builds 13 to 19 (the PC port, the Android
'game' manifest flags and build 17's unlock-code and inbox changes are on `wip/pc-port`). Earlier: device tests 6 and 7 on the same S22 ("Build 8", "Build 7", "Device test 6").

**Summary:** the Federation server is complete for the offline single-player goals and passes its full test suite, including a
conformance harness that runs the game's own client libraries against it (the count of the last full run is under
"Checked" in the newest build's section). Device test 1 put the player build (patcher +
embedded host) on a phone for the first time and found two blockers (a stall with the internet on, a crash of the headless
TS World). Build 2 fixed both: in device test 2 the whole boot chain up to the TS handshake ran, and the TS World loaded its
level. Then the game hung on "connecting", with the game thread asleep in a futex. Build 3 gave the server Worlds
single-threaded physics, let only one World step at a time and added thread dumps to the watchdog: **in device test 3 the
game booted to the menu and played matches**; it found two crashes (unlock-all, the shop), fixed in build 4. **Device test
4: boot, bot matches, saves, the shop and the unlock codes work**; open were the installer stalling at 0 MB / 0 % on a
copied `Data/` folder and the avatar editor freezing after unlock-all. Build 5 adds the offline data install, an optional
64-bit-only APK (`--abi arm64-v8a`; 32-bit is kept by default again) and live ops (weekly division seasons, daily missions, weekly events).
**Device test 5: live ops and the copied-data install work**; leaving the firing range took ~11 s on "connecting to the
server". Build 6 makes the CRM document the game reloads there ~78% smaller for an unlock-all profile: **in device test 6
the return to the menu was fast**, and a thread dump finally caught the kill-signature screen freeze, which build 7 patches;
**device test 7 (builds 7 and 8): the signature screen is fast and the debloated build works.** Build 9 adds the
in-game MCVS Offline page: settings, saves (also from one phone to another) and logs, without a PC (**device test 8:
it works**), and the Cosmetic crate, a play path for the ~600 items that never had a source (from 2026-11-09). Build 10
targets Android 15 (no Play Protect "older version" warning; the game's Java code adapted), adds graphics profiles, an
HD texture probe and a Windows test kit for the Steam version. Build 11 installs HD textures converted from the Steam
version into the game's own data. Build 12 accepts a full ASTC data pack with 2,215 PC textures in it: **device test 9:
the HD textures and the graphics profiles work, at the same frame rate as before**, and builds 10 to 12 (target Android
15) installed and played fine.

## Build 19 (October 2026): a shorter Settings menu, and Steam's PERFORMANCE page on the phone

### What build 19 contains

`41c1407` on `wip/pc-port` (`McvsVersion.Build` is `19`; the code is the merges of `wip/b19-contracts`, `-registry`,
`-grid`, `-page` and `-backend`, the integration commit `6d02c28` and the review fixes `41c1407`, below; the first
build-19 APK `mcvs-offline-922ce02.apk` and kit `effcc07a…0aef` are replaced, and either installs over them). APK: `mcvs-offline-41c1407.apk` (SHA-256
`72a40d9c50933492f0b4ad1a574c3ac5024b84e4aefff05341205ad74d38bde6`; default patch set, unchanged since build 17; the same key as every build: certificate SHA-256
`fc0db84e…7d16`, targetSdk 35, both ABIs, `isGame` and `appCategory`, zipalign OK); it installs over build 18 as
usual. Compared entry by entry with build 18's `mcvs-offline-9e21ead.apk`: the same 952 entries in the same order; changed only the server DLL (2,562,560 → 2,650,112 bytes), the signature files and the 16 MVID bytes of `Game.dll`, `Gameloft.Engine.dll`, `Gameloft.Util.dll` and `NicePlaySDK.dll` ([design/pc-port.md](design/pc-port.md) §13.1). The PC kit
`McvsOffline.PcKit.exe` (win-x64, one file, SHA-256 `9f7302dde8bc829df1346989a81d1d947ad2e99b7a20d220dabb29dbcdf56089`) replaces build 18's; build 18's Windows checks (whether
`Game` now comes from `McvsOffline\bin`) still stand and come first. The design is
[design/settings-menu.md](design/settings-menu.md).

**The owner's request.** The game's Settings menu shows tiles that cannot work offline (SOCIAL MEDIA, FORUM, CUSTOMER
CARE, and MORE GAMES on the phone); remove them and centre what is left. On the phone, add the Steam version's
PERFORMANCE tile with its options, less those that mean nothing on a phone, and take GRAPHICS SETTINGS and FRAME RATE
LIMIT out of Gameplay Settings. Nothing in the game's files changes: MCVS Offline puts its own handlers on the game's
menu functions while the game runs (no new patch; the APK's game files are build 18's except for their MVIDs).

**What each Settings screen now looks like.**

* **PC (Steam), main-menu Settings:** two centred rows of three, GAMEPLAY SETTINGS, AUDIO, ACCOUNTS / HELP, LANGUAGES,
  PERFORMANCE, and ABOUT alone, centred, below them. SOCIAL MEDIA, FORUM and CUSTOMER CARE are hidden. Every page
  behind the tiles is Steam's own and unchanged (PERFORMANCE keeps its nine rows), and so is the in-match Settings
  panel. ABOUT no longer drifts sideways after visits to a submenu (Steam moved it relative to its last place).
* **Phone, main-menu Settings:** GAMEPLAY SETTINGS, AUDIO, CUSTOMIZE CONTROLS, ACCOUNTS in the first row (where the
  stock row 1 was), HELP, LANGUAGES, **PERFORMANCE** centred under it, ABOUT alone at the bottom. FORUM, SOCIAL MEDIA,
  CUSTOMER CARE and MORE GAMES are hidden; the PERFORMANCE tile is the game's own FORUM tile with the game's own
  PERFORMANCE title (translated) and icon. The in-match Settings panel (AUDIO, GAMEPLAY, CUSTOMIZE) is unchanged: it
  cannot get a PERFORMANCE tile without editing the game's files.
* **Phone, PERFORMANCE page** (the main menu only; no category headers, as on Steam):

  | Row | Choices | What it changes | When |
  |---|---|---|---|
  | GRAPHICS SETTINGS | AUTO, MID, HIGH, ULTRA | `graphics.profile` (the same as the settings page's Graphics profile; also resets the game's own relative step so AUTO is the phone's choice) | next start: back on the Settings grid a confirm popup (the game's own texts) offers to restart now |
  | ANTIALIASING (English) | OFF, 2x, 4x, 8x | `graphics.msaa` → GameOptions `renderTargetMSAA` | next start, same popup |
  | SCREEN SCALE (English) | slider 0-100 % | `graphics.renderScale` → `RenderTargetSizeScale` (Steam's formula: 0 is 40 %, above 97 is full) | at once while dragging; stored when the page closes |
  | FRAME RATE LIMIT (the game's own row, translated, with its battery 'i') | 30, 60, 90, 120, UNLIMITED | the game's `MaxFramerate`; 90 and 120 are new, and a stored choice is carried over to the longer list once (`graphics.fpsListVersion`) | at once |
  | LOW RESOLUTION MAPS (English) | OFF, ON | `graphics.lowResMaps` → the levels' low-end layers | next level load (next match) |

  Until a row is changed it shows what the game uses now (`auto`); SCREEN SCALE shows the scale matches render at (the
  menu itself renders at full scale). FULLSCREEN, SCREEN RESOLUTION and VSYNC are left out (nothing to switch on a phone:
  the game's VSync and Steam's frame limiter are empty functions in the Android engine); FIELD OF VIEW stays in Gameplay
  Settings, where the phone already has it.
* **Phone, Gameplay Settings** (main menu and in a match): GRAPHICS SETTINGS and FRAME RATE LIMIT (with its 'i') are
  gone, with no gap and no empty header; every other row is where it was. FRAME RATE LIMIT is therefore changed only on
  the main-menu PERFORMANCE page (the owner's choice); the stored limit keeps applying at every start.

**The settings to turn it off** (settings page, or `settings.json`; both apply the next time Settings opens):
`admin.settingsMenu.trim` (Settings › Gameplay › **Shorter Settings menu**, both platforms: off brings the stock grid
back) and `admin.settingsMenu.performance` (Settings › Gameplay › **PERFORMANCE in the Settings menu**, phone only: off
removes the tile, puts the two Gameplay rows back, and from the next start the game's own 30/60/UNLIMITED list with the
stored choice mapped back). The values themselves are on the phone's settings page under Graphics: `graphics.msaa`,
`graphics.renderScale`, `graphics.lowResMaps` (`auto` = the profile's own; a change there applies at the next start); a bad value can be fixed there. When the
grid cannot be laid out, it stays as the game draws it, the two Gameplay rows stay, and `server.log` says why in a
`settings menu: … not laid out (…)` line; a page that cannot be built shows the stock rows and logs `performance page
not built (…)`.

**How it works, in short.** The game's Flash menus call C# "native functions"; the engine lets anyone add a delegate
to one, and the last answer wins. A timer puts ours back every 2 s if the game re-registers its own. `EnterSection` on
the Settings grid posts the layout step (it reads every tile first and writes nothing unless all are there);
`GoToForum` opens the PERFORMANCE page while the tile is ours (otherwise it is the game's forum again);
`CanChangeGraphicSetting`/`CanChangeMaxFramerate` answer `false` outside the page, so the game never builds the two
Gameplay rows; on the page they let the game build its rows, and a posted step turns them into the five Performance
rows. That step's own writes fire the menu's change handlers; while it runs, every preference write is ignored (the
design review's E1), so opening the page never stores anything.

**Checked**: `McvsOffline.sln` (Release), the Android embedded DLL against the APK's assemblies and the PC DLL with
`Gameloft.Logs`; all four test projects with `MCVS_REFS`, `MCVS_PC_REFS`, `MCVS_APK`, `MCVS_DATAPACK`,
`MCVS_BUILD_TOOLS`, `MCVS_REQUIRE_REAL_CLIENT=1`, `MCVS_REQUIRE_PC=1`, `MCVS_REQUIRE_PC_E2E=1`, `MCVS_REQUIRE_EMBEDDED=1`,
`MCVS_REQUIRE_BUILD_TOOLS=1` and `MCVS_REQUIRE_NODE=1`, at `922ce02`: **McvsOffline.Tests 1576/1576, McvsOffline.Embed.Tests 1141/1141, McvsOffline.PcKit.Tests 63/63, McvsOffline.PcSpike.Tests 62/62, 0 skipped** (2,842; build 18's run was 2,623); after the review fixes, at `41c1407`: 1577/1577, 1142/1142, 63/63, 62/62 (2,844). New tests: the native registry on a
fake native table with the engine's dispatch rules (re-registration, one delegate per name, a throwing step, the forum
tap, no host), every game member used resolved by signature in the real Android and Steam assemblies, the grid plans
and their all-or-nothing writes on a fake Flash tree of both panels, the page transform on the real row order (the
exact five rows, the write order, nothing stored by the transform although every change handler runs, no donor
preference written), the writes (SCREEN SCALE live and stored once whatever order the saves run in), the restart prompt,
and the back end on the real `GameOptions_android.json` (MSAA, the 90/120 list, the index migration including a
VeryLowGPU phone, the scale and the low-end layers). The integration tests were checked to fail on the code they guard. The APK was built with `--build-tools` and verified
as above. The kit published, its `check-server` passed on the PC server DLL (every PC hook (17), `Gameloft.Logs`, only
allowed references), and its assembly carries that DLL byte for byte. The PC end to end on Linux (§12.6) with the kit's
Linux build on temp copies of the Steam files: `install`, `verify`, `status` (`installed, current`, `activation: NOT
VERIFIED` before a start), a second install with the same bytes, `uninstall` restoring the exact listing. Upgrade: over
a build-18 install (a `ce90194` patcher), `status` says `installed, OLDER BUILD - OLDER BUILD (18; this kit installs
build 19): run install …` and `verify` fails (exit 1); `install` over it writes exactly the bytes of §13.1, then
`installed, current`. The expected `verify` values are re-recorded in [design/pc-port.md](design/pc-port.md) §13.1 and
[PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) §7 (build 18's are in §13.2): every DLL changed with the server DLL, the config
is build 16's (`3ebe99b3…f94c`), `serverBuild` is `19`.

**Review fixes (`41c1407`).** (1) On the settings page, Screen scale and Low resolution maps were marked `live` and
`menu`, but nothing applies a save of the page's copy of `settings.json` to the running engine: the host reads its own
copy at start, and only the game's PERFORMANCE page applies its own changes live. Both are `restart` keys now, with help
saying so (a test pins every Graphics key to `restart`; the game's page still applies SCREEN SCALE at once and LOW
RESOLUTION MAPS from the next match). (2) The restart prompt now follows the values the game started with: a GRAPHICS
or ANTIALIASING change put back before leaving the page asks for no restart (it used to stay pending and restart for
nothing). Both new tests fail on the code before the fix. The APK, the kit and the §13.1 values were rebuilt and
re-recorded from the same runs as above (APK compared entry by entry with build 18's and with `922ce02`'s: only the
server DLL, the signature files and the four MVIDs differ; the PC end to end, fresh install, ILVerify, second install
and uninstall repeated; the upgrade run from build 18 was not repeated, no installer code changed). Integrator note:
the five build-19 package merges (`3fc37a4`, `54e247f`, `3c4175d`, `0836cae`, `3dc44ea`) lack the `Co-Authored-By` and
`Claude-Session` trailers; the branch is pushed, so history is not rewritten, and later `--no-ff` merges carry them.

**Not checked (untested)**: **nothing ran on a phone or on Windows.** In particular: the layout and centring on real
screens and whether the stock grid flashes for one frame before ours; whether the hidden tiles can still be reached (a
tap on their place; on PC the keyboard and a gamepad); whether gameswf accepts the page transform as the fakes do
(the first PERFORMANCE visit logs one `spike` line that says so); whether SCREEN SCALE and ANTIALIASING change the
picture on the S22's Mid profile, whether 8x works on its GPU, and whether 90/120 FPS go above 60 on its 120 Hz panel
(Samsung may hold a game at 60; then the list falls back to 30/60/UNLIMITED in a later build); the restart on Android
13+ (it may only close the game; the choice is kept either way).

### What to check on the PC and the phone (build 19)

**PC first** (build 18's steps 1-4 first if they have not been done: `Game` from `McvsOffline\bin`, `store yes, inbox
yes`):

1. Close the game, run this kit's `status` over the build-18 install: `installed, OLDER BUILD - OLDER BUILD (18; this
   kit installs build 19): run install …`. Run `install`, then `verify` (the six values of
   [PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) §7, exactly).
2. Start the game, open Settings from the main menu at 1920×1080: two rows of three tiles, centred (GAMEPLAY SETTINGS,
   AUDIO, ACCOUNTS / HELP, LANGUAGES, PERFORMANCE), ABOUT alone and centred below; no SOCIAL MEDIA, FORUM, CUSTOMER
   CARE. `server.log`: `settings menu: frame 1, 6 tiles + ABOUT laid out`. Once more in a windowed 16:10 size.
3. Every tile opens its own page (PERFORMANCE: Steam's nine rows); after each, Back: the grid is the same, ABOUT has not
   moved (five round trips).
4. The hidden tiles: click where SOCIAL MEDIA, FORUM and CUSTOMER CARE were: nothing. With the **keyboard** (arrows,
   Tab, Enter) and a **gamepad** (d-pad, A), walk the whole grid: focus never lands on an invisible tile and never
   opens one. Report any step where focus disappears.
5. In a match: Settings shows Steam's in-match panel, unchanged.
6. Settings page › Gameplay › Shorter Settings menu off, reopen Settings: the stock grid; back on.

**Phone (S22), install build 19 over build 18:**

1. Main menu › Settings: row 1 GAMEPLAY SETTINGS, AUDIO, CUSTOMIZE CONTROLS, ACCOUNTS; row 2 HELP, LANGUAGES,
   PERFORMANCE, centred under it; ABOUT alone and centred. No FORUM, SOCIAL MEDIA, CUSTOMER CARE, MORE GAMES; a slow-motion
   video of opening Settings and of returning from a submenu (does the stock grid flash for a frame?). `server.log`:
   `settings menu: frame 3, 7 tiles + ABOUT laid out`.
2. Tap where each hidden tile and button was: nothing happens. Every visible tile opens its own page, Back returns to
   the same grid.
3. PERFORMANCE opens a page titled PERFORMANCE with exactly GRAPHICS SETTINGS, ANTIALIASING, SCREEN SCALE, FRAME RATE
   LIMIT, LOW RESOLUTION MAPS, no headers. `server.log`: `performance page: 5 rows` and the one `spike` line (copy it).
   Opening it must not change any value: the settings page's Graphics values still say `auto`.
4. Each row:
   * GRAPHICS SETTINGS to HIGH, Back: the restart popup; **Yes** restarts (on Android 13+ it may only close: start it
     again), and the `[boot] graphics:` line says `HighGPU`. Back to AUTO the same way, once answering **No** (the
     next start applies it).
   * ANTIALIASING 4x (then 8x), restart: the `[boot] graphics:` line has `renderTargetMSAA 4` (8); edges in a match look
     smoother; note any glitch or crash at 8x.
   * SCREEN SCALE: drag to 0 and back up while watching the background: the picture gets blurrier and sharper at once.
     Leave it at 50, Back, start a match: still at 50 (blurrier than before); the settings page shows `50`.
   * FRAME RATE LIMIT: 90, then 120, then UNLIMITED, each in a match with an FPS counter (Game Booster or the developer
     options' frame rate overlay): the frame rate you see at each. If 90/120 never go above 60, say so (and whether
     Game Booster's own frame rate setting was on).
   * LOW RESOLUTION MAPS ON, start a match: plainer level geometry/textures; OFF again: back to normal.
5. Gameplay Settings from the main menu: no GRAPHICS SETTINGS row, no FRAME RATE LIMIT row and no 'i' button at the
   bottom, no empty header; FIELD OF VIEW is still there. The same in a match (Settings › GAMEPLAY), also in a match
   started before opening Settings from the main menu.
6. Settings page › Gameplay › **PERFORMANCE in the Settings menu** off, reopen Settings: FORUM gone, no PERFORMANCE,
   row 2 is HELP and LANGUAGES; Gameplay Settings has both rows again; after a restart FRAME RATE LIMIT lists 30, 60,
   UNLIMITED with your choice kept. Back on. Then **Shorter Settings menu** off: the stock grid with the PERFORMANCE
   tile in FORUM's place. Back on.
7. As always: boot to the menu, one Classic bot match, the shop, the MCVS Offline page.

## Build 18 (October 2026): MCVS Offline itself loaded the game's own Game.dll at start

### What build 18 contains

`9e21ead` on `wip/pc-port` (`McvsVersion.Build` is `18`; the code is the merge `ef0057c` of `wip/b18-gameload`). APK:
`mcvs-offline-9e21ead.apk` (SHA-256 `5981f1098151f61a8a3d9204514e2351a510fe9f70490521f0c6354c861023b8`; default
patch set, unchanged since build 17; the same key as every build: certificate SHA-256 `fc0db84e…7d16`, targetSdk 35,
both ABIs, `isGame` and `appCategory`, zipalign OK); it installs over build 17 as usual. Compared entry by entry with
build 17's `mcvs-offline-a8742ab.apk`: the same 952 entries in the same order; changed only the server DLL, the
signature files and the 16 MVID bytes of `Game.dll`, `Gameloft.Engine.dll`, `Gameloft.Util.dll` and `NicePlaySDK.dll`
([design/pc-port.md](design/pc-port.md) §13.1). The PC kit `McvsOffline.PcKit.exe` (win-x64, one file, SHA-256
`186c1bcb21b46aea633aea888fc6eae531bcdcad73d5027ab660269cd117b28e`) replaces build 17's.

**The owner's build-17 log** (Windows, 2026-10-30, with build 16's config in effect). Every redirected file but one
came from `McvsOffline\bin`, and the boot line said `gameload yes`, yet:

```
[boot] +0.109 s assemblies: EngineBridge …\McvsOffline\bin\EngineBridge.dll (McvsOffline\bin)       (and Gameloft.Engine, McvsOffline.Server)
[game] Game is not loaded yet: the client's patches are probed when it loads
[boot] +0.125 s assemblies: Gameloft.Util …\McvsOffline\bin\Gameloft.Util.dll (McvsOffline\bin)
[boot] +0.140 s install: current
[boot] +0.187 s assemblies: Game …\Modern Combat Versus\Game.dll (GAME FOLDER): the .exe.config redirect did not apply
[boot] +0.187 s patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes, store NO, inbox NO
[boot] +0.187 s install: installed, NOT ACTIVE (Game loaded from the game folder; the 'store' patch is missing; …)
[boot] +0.250 s offline server listening at http://127.0.0.1:51780/
```

The game then ran its own `Game.dll`: none of the fixes in that file (store, signatures, inbox, the settings-page
button, the bot match) were active.

**Cause (found by reading the code; the timing in both Windows logs fits; not yet confirmed on Windows).** `Game`
was loaded between `install: current` and `server listening`, which is the host's start thread
(`EmbeddedHost.RunServer`). On that thread `CreateAdminHost` called `GameReflection.Load()`, which looked up
`Type.GetType("Game.PlayerProfileComponent, Game", false)` and `Type.GetType("Game.UISceneExtentions, Game", false)`
for the settings page's "save now". `"Game"` there is a **partial** assembly name: .NET applies no application-config
policy to it, so the `<codeBase>` of `ModernCombatVersus.exe.config` was ignored, the loader probed the exe folder and
loaded the **stock** `Game.dll` next to the exe. When the engine later loaded `Game` by its full name (the `gameload`
patch), the `Game` already loaded was what it got. The same call ran in every WR2 start (build 13 had it), so it most
likely also explains **WR2's F1** (`Game` from the game folder in 15/15 starts), which was put down to the engine's
bare-name load. So `gameload` has never had a chance to run first: whether its full name gets the codeBase is still
open ([design/pc-port.md](design/pc-port.md) §8.9, Q1, Q15). The start thread runs beside the game thread, so the
timing alone cannot rule out an engine load that also missed the codeBase; the load stack below settles it.

**The fix.** MCVS Offline's own code never makes the runtime load a game assembly:

* `GameReflection.Load` binds lazily, only from assemblies that are **already loaded** (`LoadedTypes.Find`:
  `AppDomain.GetAssemblies()` by simple name, never a load), and binds what is still missing on the next use (the
  settings page's save now, the notification prompt, the exit post). On PC it is unbound at boot and bound once the
  engine has loaded `Game`. On Android `Game` and `Gameloft.Engine` are loaded from the APK before our start, so
  nothing changes there.
* The other lookups by name (watchdog, threading line, graphics, IAP status, `WindowsExit`'s `MenuManager`) take
  `LoadedTypes.EngineType`: the loaded `Gameloft.Engine` (on PC always loaded before our start); only on Android, when
  it is not loaded yet, the old partial-name lookup (the APK has one copy and no redirect).
* Tests with teeth: `GameLoadOrderTests` runs the shipped `GameReflection.cs` and `LoadedTypes.cs` in a load context
  that serves a look-alike `Game` by partial name and records every request (no request and no load of `Game` before
  the engine loads it, whatever member is used; bound once it is loaded; a control shows build 17's call is caught);
  a source scan fails on any `"…, Game"`-style literal of a redirected assembly, any bare-name `Assembly.Load` or
  `LoadWithPartialName`, and any `Type.GetType` in `hosts/` or `server/` outside `LoadedTypes` and `RuntimeInfo`;
  `PcEmbeddedAssemblyTests.OnlyRuntimeInfoLoadsByName` checks the built PC DLL's IL the same way.

**The load-stack diagnostic.** When a redirected assembly comes from the game folder, `bootstrap.log` now says who
loaded it, right under the `GAME FOLDER` line: `[boot] load stack: Game (GAME FOLDER) was loaded by (innermost first):`
and up to 25 indented `at …` frames (the managed stack inside the `AssemblyLoad` event, our handler's and the
loader's own frames cut), or `… already loaded when the host started watching, so its loader is unknown`. A second
signature has no stack: `assemblies: Game (no file) (elsewhere)` means the stock `Gameloft.Sequences` read `Game.dll`
from bytes because the engine's `gameload` bind itself failed ([design/pc-port.md](design/pc-port.md) §8.9). As the
spec says, the stack is not widened to that case (a redirected assembly with no `Location`); the docs describe its
signature instead, and widening it needs the owner's agreement.

**What the next Windows start must show** ([PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) §7 step 4):
`assemblies: Game …\McvsOffline\bin\Game.dll (McvsOffline\bin)` and `patches: … gameload yes, store yes, inbox yes`,
then `status`: `installed, current`, `activation: active at the last start`. If `Game` still comes from the game
folder, the `[boot] load stack:` lines name the loader (send them with `bootstrap.log`); `Game (no file) (elsewhere)`
means the `gameload` bind failed (send `collect`).

**Checked**: `McvsOffline.sln` (Release), the Android embedded DLL against the APK's assemblies and the PC DLL with
`Gameloft.Logs`; all four test projects with `MCVS_REFS`, `MCVS_PC_REFS`, `MCVS_APK`, `MCVS_DATAPACK`,
`MCVS_BUILD_TOOLS`, `MCVS_REQUIRE_REAL_CLIENT=1`, `MCVS_REQUIRE_PC=1`, `MCVS_REQUIRE_PC_E2E=1`, `MCVS_REQUIRE_EMBEDDED=1`,
`MCVS_REQUIRE_BUILD_TOOLS=1` and `MCVS_REQUIRE_NODE=1`, at `9e21ead`: **McvsOffline.Tests 1565/1565,
McvsOffline.Embed.Tests 933/933, McvsOffline.PcKit.Tests 63/63, McvsOffline.PcSpike.Tests 62/62, 0 skipped** (2,623;
build 17's run was 2,613). The APK was built with `--build-tools` and verified as above. The kit published, its
`check-server` passed on the embedded DLL (every PC hook (17), `Gameloft.Logs`, only allowed references), and its
assembly carries that DLL byte for byte. The PC end to end on Linux (§12.6) on a temp copy of the Steam files: `pc
install`, `verify`, `status` (`installed, current`, `activation: NOT VERIFIED` before a start), a second install with
the same bytes, `uninstall` restoring the exact listing, ILVerify as the stock files; the owner's build-17 start as the
last start gives `installed, NOT ACTIVE (Game loaded from the game folder; the 'store' patch is missing; the 'inbox'
patch is missing)`. Upgrade: a build-17 install (the `b20d12b` patcher), on which this kit's `status` says
`installed, OLDER BUILD - OLDER BUILD (17; this kit installs build 18): run install …` and its `verify` fails (exit
1); build 18's `install` over it writes exactly the bytes of §13.1, then `installed, current`. The expected `verify`
values are re-recorded in [design/pc-port.md](design/pc-port.md) §13.1 and [PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md)
§7 (build 17's are in §13.2): every DLL changed with the server DLL, the config is build 16's (`3ebe99b3…f94c`),
`serverBuild` is `18`.

**Not checked**: **nothing ran on Windows.** The cause is confirmed only by reading the code and by the timing in the
owner's build-17 log and WR2's log, until the next Windows start shows `Game` from `McvsOffline\bin` (`store yes,
inbox yes`) or the new load-stack lines. Nothing on a phone either.

### What to check on the PC and the phone (build 18)

1. **PC, first:** close the game, run this kit's `status` over the build-17 install: `installed, OLDER BUILD - OLDER
   BUILD (17; this kit installs build 18): run install …`. Run `install`, then `verify` (the six values of
   [PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) §7, exactly).
2. Start the game from Steam once. In `McvsOffline\logs\bootstrap.log`: `[boot] assemblies: Game
   …\McvsOffline\bin\Game.dll (McvsOffline\bin)` and `[boot] patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes,
   store yes, inbox yes`; no `NOT ACTIVE`. After closing the game, `status`: `installed, current`, `activation: active
   at the last start`.
3. If `Game` still says `(GAME FOLDER)`: copy the `[boot] load stack:` line and all its `at …` lines, send them with
   `bootstrap.log` and `collect`; stop there. If it says `Game (no file) (elsewhere)`: send `collect`; stop there.
4. With `Game` from `McvsOffline\bin`: the rest of WR3 as written ([PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) §7; build
   17's PC step 7 in "Build 17" below), in particular Settings › Account › **MCVS Offline** (the button lives in our
   `Game.dll`) and the settings page's Saves › **Save the game now** (it uses the lazily bound `GameReflection`).
5. **Phone** (install build 18 over build 17): the settings page still works: open it from Settings › Account › MCVS
   Offline and from the inbox message; Status loads; Saves › Save the game now finishes; Restart works. As always: boot
   to the menu, one Classic bot match, the shop.

## Build 17 (October 2026): big unlock codes froze the inbox; the codes now go through the ledger, and the inbox is bounded

### What build 17 contains

`a8742ab` on `wip/pc-port` (`McvsVersion.Build` is `17`; after the review fixes below, which replaced the first cut
`80378cf`/`96801b4`). APK: `mcvs-offline-a8742ab.apk` (SHA-256 `d4d332f1…16a4`;
default patch set, now with `inbox`; the same key as every build: certificate SHA-256 `fc0db84e…7d16`, targetSdk 35,
both ABIs, `isGame` and `appCategory`, zipalign OK); it installs over build 15 as usual (build 16 had no APK).
Compared entry by entry with build 15 (`mcvs-offline-1a70a5b.apk`): the same 952 entries in the same order; changed
only the server DLL, `Game.dll` (the `inbox` patch), the signature files and the 16 MVID bytes of
`Gameloft.Engine.dll`, `Gameloft.Util.dll` and `NicePlaySDK.dll` ([design/pc-port.md](design/pc-port.md) §13.1). The
PC kit `McvsOffline.PcKit.exe` (win-x64, one file, SHA-256 `804dd546825a27af92712062a3ec8603e114c9c4bc34ebb016b7fcee64fc3ec2`)
replaces build 16's for WR3. The first cut's APK (`mcvs-offline-80378cf.apk`, `b6bcb82b…2219`) and kit (`ba8a977f…50e5`)
are superseded: use these.

**The owner's report.** "The game simply freezes when I go to the inbox, but only after redeeming specific codes, not
all of them."

**Cause.** A redeem code became **one** inbox message holding every reward of the code. Opening the inbox makes the
game build every reward of every unclaimed message on the game thread (`InboxComponent.GetMessagesObject` →
`GetObject` → `GenerateRewardsObjects` → `RewardItem.GetObject`; for a camo or a jersey that builds a whole merc or
scans the accessory table), copies all of it into Flash several times and draws one card per reward of the selected
message. While the inbox stays open the C# part runs again on every constant frame of every 10th second
(`RefreshUI`, whose `lastRefreshUIDone` was written and never read). The code's size is the whole difference. The
right column is predicted from the code ([research/inbox-freeze.md](research/inbox-freeze.md) §2): only the owner's
report and WR2's freeze with stacked codes (below) were observed, nothing was measured per code:

| code | rewards | predicted on inbox open (build 16, from the code) |
|---|---|---|
| `UNLOCKALL` (and `UNLOCKALL2`, case variants) | 11,368 | freezes, practically for good (PC: may end in a crash) |
| `UNLOCKPAID`, `UNLOCKACCESSORIES`, `UNLOCKCAMOS` | 4,450-5,644 | freezes for minutes, again every 10 s |
| `UNLOCKPAIDACCESSORIES`, `UNLOCKPAIDCAMOS` | 2,704-2,960 | freezes, shorter |
| `UNLOCKSIGNATURES` | 1,549 | likely stalls for seconds, every 10 s |
| `UNLOCKGEARSLOTS`, `UNLOCKSKINS`, `UNLOCKWEAPONS`, `UNLOCKMERCS`, `UNLOCKUNRELEASED` | 6-150 | no freeze |

An unclaimed big message stays in the profile (saved with it) for 7 days, so after one big code **every** later
inbox visit froze, even after a small code: "the code I just redeemed" was not always the one to blame. WR2's 180 s
freeze at 19:01 (UNLOCKCAMOS and UNLOCKPAIDCAMOS on top of an unclaimed UNLOCKPAID, 12,798 rewards) is this.

**Direct grant (the owner's choice).** Every `UNLOCK*` code (the built-in ones, numbered repeats, any letter case, and
own codes whose name starts with `UNLOCK`) is now delivered through the inventory ledger, like `"unlockAll": true`
(`UnlockGranter.GrantKeys`, [protocol/unlocks.md](protocol/unlocks.md) "Unlock codes"): a backup of the profile
first (reason `unlockcode`), the agents alone in the first batch, then the rest in batches of `unlocks.batchSize`
(400) with a `set_profile` alert each, `unlocks.batchDelaySeconds` (10) apart. The redeem answer is an empty list.
What the player sees:

* the game says the code was redeemed, at once;
* **one empty "Coupon" message** in the inbox: nothing to claim, it goes after 7 days;
* the items arrive in batches with a rewards popup each: about 30 popups over a few minutes for `UNLOCKALL`; stay in
  the menus until they stop. A code not finished when the game closes carries on after the next start
  (`<data>/unlocks/codes.json`);
* a repeat (`UNLOCKALL2`, or `UNLOCKCAMOS` after `UNLOCKALL`) writes only keys the ledger lacks, so usually nothing; the
  duplicate compensation the old inbox claim paid for owned agents is gone. Items owned from play, crates, the store or
  an old inbox claim are not in the ledger and count as new once (the game pays its usual duplicate gear for an agent);
* in LAN mode (a guest's code would arrive on this device's menu server) and when the server cannot tell whose profile
  it is, the code is refused: the game says it does not exist, nothing changes, and `server.log` says why.

`server.log`: `unlock code <CODE>: granting <n> of <m> unlock keys to <account> ...` and `... done, <n> keys written in
<b> batch(es) (<k> already in the inventory ledger)`. A code that is **not** a ledger code still goes to the inbox
as one message and may hold at most 300 rewards: a bigger one is refused with a WARN line ("would freeze the game's
inbox"). The settings page (Settings › Unlocks & codes) says the codes arrive in batches, not through the inbox, and
its code list warns about an own inbox code above 300 rewards and about a ledger code with rewards the ledger cannot
give (quantity other than 1); the page's `GET /_mcvs/api/v1/codes` carries each code's delivery and reward count. Details:
[ECONOMY.md](ECONOMY.md), [protocol/content.md](protocol/content.md) "Coupons".

**The `inbox` patch (Game.dll, Android 1.17.32 and PC 1.17.30, default on both).** It bounds the inbox's cost
whatever a profile holds, so it also **rescues profiles that already hold a giant message** from build 16 or earlier
(and any other big message):

* the inbox list shows at most the **first 30 rewards** of a message, plus every Gear reward (the game's
  "gear inventory full" warning looks for them); the rewards past 30 are never built. `GetObject` adds
  `RewardsTotal` (the message's reward count);
* the claim popup lists the first 30, but **Claim still grants every reward** of the message and removes it
  (`ClaimRewardsForMessageRequest` is not touched);
* `RefreshUI` refreshes once per 10 s window instead of on every frame of every 10th second;
* each reward is built in a try/catch: one that throws is logged (`inbox reward <id> is left out of the inbox list`)
  and left out instead of ending the process;
* PC only: `KillSignatureRewardItem.IsRandom` gets the null check Android already has (an own coupon with a kill
  signature and no id).

On PC the boot line names it (`[boot] patches: … store yes, inbox yes`); on Android the server log's Debug line
`Game.dll: … 'inbox' patch present` and the settings page's Status show it (Android writes no `[boot] patches:` line).
The settings page's Status warns when the running Game.dll lacks it (a banner, "The game was patched without MCVS Offline's 'inbox' patch: opening the inbox freezes the
game while a big redeem-code message … is unclaimed", and `Game patches: missing: inbox`).
[PLAYER-SETUP.md](PLAYER-SETUP.md) §4, [design/pc-port.md](design/pc-port.md) §8.1.

**PC kit.** `inbox` is a witness of the kit now (`PcInstaller.Witnesses["inbox"]`, `Game.McvsInbox`; §3.9 is no
longer open): `verify` and `status` re-read it in `McvsOffline\bin\Game.dll` like `store`, and the contract test
holds the three copies equal. The expected WR3 values are re-recorded ([design/pc-port.md](design/pc-port.md) §13.1,
[PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) §7): `Game.dll` changed with the patch; EngineBridge, Gameloft.Engine,
Gameloft.Util with the server DLL; the config is build 16's (`3ebe99b3…f94c`); `serverBuild` is `17`.

**Review fixes (`2ce6ee4`, `a8742ab` and the docs after them).**

* The kit's `status` and `verify` call an install of an older build `installed, OLDER BUILD - OLDER BUILD (16; this
  kit installs build 17): run install to update it (your saves and settings stay)`, and `verify` fails on it (exit 1).
  Before, build 17's kit called a build-16 install `installed, current`: it records only build 16's patches, so the
  missing `inbox` had no witness to miss, and a build-16 server still sends the unlock codes to the inbox as one
  message (the freeze). The kit compares `install.json`'s `serverBuild` with `McvsVersion.Build` of the server DLL it
  carries (`PcInstaller.ServerBuildOf`, or `--server`'s); DAMAGED, STALE and NOT ACTIVE keep their state and get the
  same problem line ([design/pc-port.md](design/pc-port.md) §7.6). Tests: `PcOlderBuildTests`,
  `KitCommandTests.StatusAndVerifyCallAnInstallOfAnOlderBuildOlder`.
* The settings page's code list: a numbered repeat "gives only what an earlier code or unlockAll did not already give
  (items from play, crates, the store or an old coupon message count as new once)", instead of "only what is still
  missing" (the ledger does not know what an old inbox claim gave). This changed the server DLL, so the APK, the kit
  and every expected WR3 value were rebuilt and re-recorded.
* Docs: the checklist below gained step 3 (a code on items an old message gave) and PLAYER-SETUP-PC step 8a (WR2's
  old coupon messages on the PC); the per-code table says it is predicted; the boot line is PC only;
  [ARCHITECTURE.md](ARCHITECTURE.md) and [protocol/content.md](protocol/content.md)'s device checklist no longer
  describe the codes as one inbox message; the investigation is in the repo now,
  [research/inbox-freeze.md](research/inbox-freeze.md); PLAYER-SETUP-PC step 1 had kept build 16's kit hash.

**Checked**: `McvsOffline.sln` (Release), the Android embedded DLL against the APK's assemblies and the PC DLL with
`Gameloft.Logs`; all four test projects with `MCVS_REFS`, `MCVS_PC_REFS`, `MCVS_APK`, `MCVS_DATAPACK`,
`MCVS_BUILD_TOOLS`, `MCVS_REQUIRE_REAL_CLIENT=1`, `MCVS_REQUIRE_PC=1`, `MCVS_REQUIRE_PC_E2E=1`, `MCVS_REQUIRE_EMBEDDED=1`,
`MCVS_REQUIRE_BUILD_TOOLS=1` and `MCVS_REQUIRE_NODE=1`, at `a8742ab`: **McvsOffline.Tests 1564/1564,
McvsOffline.Embed.Tests 924/924, McvsOffline.PcKit.Tests 63/63, McvsOffline.PcSpike.Tests 62/62, 0 skipped** (2,613;
the first cut's run was 2,599, and its first Embed run found three tests the `inbox` branch had not updated for the
real APK, the note totals and P5's Android output, fixed in `80378cf`). `PcInstallerTests.RealFolderEndToEnd` ran
with `inbox` in the PC set: every recorded witness present, ILVerify findings equal to the stock files'. The
content-gen tests: 32/32. The PC end to end on Linux (§12.6) on a temp copy of the Steam files: `pc install`,
`verify`, `status` (`installed, current`, `activation: NOT VERIFIED` before a start), a second install with the same
bytes, `uninstall` restoring the exact listing; a build-16 install (the `c2b5076` patcher) upgraded by build 17's
`install` to exactly the bytes of §13.1 (before that, the build-17 kit's `status` says `installed, OLDER BUILD` and its
`verify` fails; the patcher CLI, which does not compare, says `installed, current`). The APK compared with the first
cut's: only the server DLL, the signature files and the 16 MVID bytes of `Game.dll`, `Gameloft.Engine.dll`,
`Gameloft.Util.dll` and `NicePlaySDK.dll` differ. Not checked: anything on a phone or on Windows; the timing of the
batches and popups in the real game.

### What to check on the phone and the PC (build 17)

Install build 17 over build 15 (no uninstall); the PC: `install` over build 16. Use a **copy** of a profile for the
code tests (settings page › Saves › Export first, or a fresh profile).

1. **UNLOCKALL on a profile copy.** Settings › Account › Redeem code `UNLOCKALL`: the game says the code was redeemed
   at once. Open the inbox: it opens **at once** and shows one empty "Coupon" message with nothing to claim. Stay in
   the menus: rewards popups arrive about every 10 s (about 30), then `server.log` says `unlock code UNLOCKALL: … done,
   … keys written in … batch(es)`. Afterwards the agents, camos, jerseys and signatures are owned (Agents screen,
   loadout, kill-signature screen). No `[watchdog] STALL` of more than a few seconds during the popups.
2. **A profile that already holds an old giant message** (one redeemed on build 16 or earlier and never claimed, for
   example the owner's stuck profile, or a save from before build 17 with an unclaimed `UNLOCKCAMOS`): open the inbox:
   it opens **within seconds**, the message shows 30 rewards; staying in the inbox does not freeze every 10 s. Press
   **Claim**: the popup lists 30, and afterwards **everything** of the message is owned (camos and jerseys of every
   agent); the message is gone. Repeat with UNLOCKALL's old message if one is at hand.
3. **A code on what step 2 claimed.** On the same profile copy, which now owns the old message's items from the
   inbox (not in the ledger): redeem `UNLOCKCAMOS2` (or `UNLOCKPAIDCAMOS` after an old `UNLOCKPAIDCAMOS`). The code
   writes those keys again and the game claims them again: the one path nobody has seen
   ([protocol/unlocks.md](protocol/unlocks.md), open items). Record the batches, the popups, any `[watchdog] STALL`,
   what the duplicate camos and jerseys turn into (nothing, a second copy, gear or currency) and the gear inventory
   (full?).
4. **Small codes.** `UNLOCKMERCS`, `UNLOCKSKINS` (fresh profile copy): redeemed, items arrive (ledger, a popup or
   two); a repeat (`UNLOCKMERCS2`) gives nothing new and no error. `MCVSOFFLINE` still opens the settings page.
5. **Settings page.** Settings › Unlocks & codes: the text says the codes arrive in batches, not in the inbox; an
   own inbox code with more than 300 rewards in `settings.json` `assets.coupons` shows its warning in the code list
   and is refused when redeemed (WARN "would freeze the game's inbox"). Status: no `inbox` warning with build 17; a
   Game.dll without the patch (an APK patched with `--patch` without `inbox`) shows the banner and `Game patches:
   missing: inbox`.
6. **LAN mode on**: redeem `UNLOCKSKINS`: the game says the code does not exist; `server.log` says why. Turn LAN off.
7. **PC (WR3)**: before `install`, the build-17 kit's `status` over the build-16 install says `installed, OLDER
   BUILD - OLDER BUILD (16; this kit installs build 17): run install …`; after it, PLAYER-SETUP-PC step 4's
   `[boot] patches:` line ends `store yes, inbox yes`; `verify` prints the values of
   [PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) §7; steps 8a (WR2's old coupon messages: 30 shown, Claim gives all) and 8b
   as written there (one empty Coupon message, the batches with a popup each, the `done` line).
8. As always: boot to the menu, one Classic bot match, the shop, the page (Status, Settings, Saves › Save now).

## Build 16 (PC only, October 2026): the config that kept MCVS Offline from starting, and start diagnostics

### What build 16 contains

`McvsVersion.Build` is `16` on `wip/pc-port`. **PC only**: no server code changed for Android, so there is no new APK
(build 15's `mcvs-offline-1a70a5b.apk` stays current on the phone). The kit `McvsOffline.PcKit.exe` (win-x64, one file,
SHA-256 `06235ac9f0cf6339bb201ffb32b9c9b14fb3c98453d0a2604a16470c0c31ae41`) replaces build 15's for WR3.

**The regression (builds 14 and 15).** Build 14 added one `<qualifyAssembly partialName="X" fullName="X, Version=…,
Culture=neutral, PublicKeyToken=null" />` per redirected assembly to `ModernCombatVersus.exe.config` (§7.4; config
SHA-256 `fda12aef…c4b6`, unchanged in build 15). The owner installed build 14 on the PC and the game "fails to connect":
our server never started. Copying the files of `McvsOffline\bin` next to `ModernCombatVersus.exe` made it work. While a
`<codeBase>` applies, .NET never uses a copy next to the exe, so the copy can only have mattered if .NET was **not using
our config at all**: the stock `EngineBridge.dll` loaded, and none of our code ran (no server, no `bootstrap.log`). The
kit could not see this: with no `bootstrap.log`, `status` still said `installed, current`.

**Cause (H-A, confirmed on Windows by the owner).** The .NET Framework assembly loader (fusion) parses the whole
`<assemblyBinding>` section at once. In the only published fusion source (SSCLI 2.0), a `<qualifyAssembly>` whose
`fullName` has `PublicKeyToken=null` (every game assembly: none is strong-named) fails the parse with `E_UNEXPECTED`, and
the loader then throws away everything it parsed, the five `<codeBase>` entries included, and runs with no app policy.
Build 13's config (no `<qualifyAssembly>`) was proven on WR2: 4 of 5 assemblies came from `McvsOffline\bin`. The CLR 4
loader in `clr.dll` is not public, but the owner's A/B test on Windows settles it: with build 14's installed config,
deleting only the five `<qualifyAssembly>` lines (nothing else changed, the stock DLLs back after Steam › Verify) made
the server start. Which file `Game` itself bound from in that run was not reported, so the gameload claim below stays
open.

**The fix.** The config is back to build 13's codeBase-only shape, byte for byte the one WR2 ran (SHA-256
`3ebe99b3c93a9540cd7f7b03da55fc048a544501c17e8621457884eae456f94c`). `Game` no longer needs `<qualifyAssembly>`: the
`gameload` IL patch (Gameloft.Engine.dll, `GameAssemblyLoadPatch.cs`) inserts `McvsGameLoad.Qualify(name)` before the
engine's `new AssemblyName(name)`, so the engine's `Assembly.Load` for `Game` gets
`Game, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null`, a full reference, to which the config's `<codeBase>`
should apply as it does for the static references. That too is expected, not proven (gameload has never run with a
config in effect; WR3 step 4 settles it). An install over build 14 or 15 rewrites their config (checked on Linux: the
`feb1684` patcher's install, then build 16's `install` over it, gives exactly build 16's bytes, with no `<qualifyAssembly>`
line).

**New diagnostics** ([design/pc-port.md](design/pc-port.md) §7.6, §8.2; [PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) §4-5):

* **hook.txt marker**: our `EngineBridge.dll` (`hook-pc`) appends one line to `%TEMP%\McvsOffline\hook.txt` per start
  (mscorlib only, before it calls into the server DLL) and the exception if starting the server fails. So "our
  EngineBridge never ran" and "it ran, but the server did not start" can be told apart without the server DLL.
* **`status`/`verify` activation**: `NOT VERIFIED: no start of MCVS Offline since the install` until the first start;
  `NOT ACTIVE: the game started without MCVS Offline; Windows did not use …\ModernCombatVersus.exe.config` when the game
  wrote its own files (`dr0.bin`, `OF_PP.bin`, `GameVersion`) after the install with no start of ours; `NOT ACTIVE: MCVS
  Offline's EngineBridge ran at … but the server did not start: …` from the marker; a `hook:` line. While the game is
  running none of these is a NOT ACTIVE verdict.
* **A leftover build-14/15 config** is `DAMAGED: … has <qualifyAssembly> entries …: an old build-14/15 config: run
  install again` (`verify` exits 1); `install` replaces it.
* **The manual copy is recognised**: a game DLL with the same SHA-256 as its `McvsOffline\bin` copy is named
  (`… is MCVS Offline's copy from McvsOffline\bin (copied over the game's own file)`), with the advice "Steam > Verify
  integrity of game files restores them, then run install"; `install` and `uninstall` refuse until then, instead of
  calling it a Steam update.
* **Install output**: the end of `install` names the exe and the config Windows must read and says to start the game
  once and run `status`; a failed config write says the game will start WITHOUT MCVS Offline; the double-click menu
  shows `INSTALL FAILED`.

**The owner's manual copy** must be undone before installing build 16: Steam › Properties › Installed Files › Verify
integrity of game files (puts back the four game DLLs), delete the `McvsOffline.Server.dll` next to the exe, then
`install` ([PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) §5).

**Checked**: `McvsOffline.sln` (Release), the Android embedded DLL against the APK's assemblies and the PC DLL with
`Gameloft.Logs`; all four test projects with `MCVS_REFS`, `MCVS_PC_REFS`, `MCVS_APK`, `MCVS_DATAPACK`,
`MCVS_BUILD_TOOLS`, `MCVS_REQUIRE_REAL_CLIENT=1`, `MCVS_REQUIRE_PC=1`, `MCVS_REQUIRE_PC_E2E=1`, `MCVS_REQUIRE_EMBEDDED=1`,
`MCVS_REQUIRE_BUILD_TOOLS=1` and `MCVS_REQUIRE_NODE=1`: **McvsOffline.Tests 1551/1551, McvsOffline.Embed.Tests 897/897,
McvsOffline.PcKit.Tests 62/62, McvsOffline.PcSpike.Tests 62/62, 0 skipped** (2,572). The PC end to end on Linux
(§12.6) on a temp copy of the Steam files: `pc install`, `verify`, `status` (`installed, current`, `activation: NOT
VERIFIED` before a start), a second install with the same bytes, `uninstall` restoring the exact listing, ILVerify as the
stock files; the build-15-then-16 upgrade above; WR2's first start as the last start gives `installed, NOT ACTIVE (Game
loaded from the game folder; …)`. The new expected `verify` values are in [design/pc-port.md](design/pc-port.md) §13.1
and [PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) §7. Not checked here: anything that runs only on Windows. On the
owner's PC the build-14 config without its `<qualifyAssembly>` lines (the build-16 shape) started the server (H-A
confirmed); whether `Game` follows the codeBase (gameload) is not yet reported.

### What WR3 must check first

* **Step 4** of [PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) §7: `McvsOffline\logs\bootstrap.log` exists after the first
  start, and its `[boot] assemblies:` lines name **all five** assemblies from `McvsOffline\bin`: EngineBridge,
  Gameloft.Engine, Gameloft.Util, McvsOffline.Server **and Game** (`Game …\McvsOffline\bin\Game.dll`); `[boot] patches:`
  every witness `yes` (`gameload yes`, `store yes`).
* `status` after closing the game: `installed, current` and `activation: active at the last start`. Any `NOT ACTIVE` or
  `NOT VERIFIED` after a start: stop, `collect`, send it (with `%TEMP%\McvsOffline\hook.txt`).
* H-A is already confirmed (the owner's A/B test above). If all five come from `McvsOffline\bin`, so is the gameload
  full-reference claim; if `Game` still comes from the game folder (`(GAME FOLDER)`, `store NO`), gameload does not
  qualify the load on Windows and needs another way (an `AssemblyResolve` handler or an in-place `Game.dll`).

## Build 15 (October 2026): rotating Special Events, an optional Practice tile, the match-mode doc corrections

### What build 15 contains

`1a70a5b` on `wip/pc-port` (`McvsVersion.Build` is `15`). APK: `mcvs-offline-1a70a5b.apk` (SHA-256 `424f7351…04d4`; default patch set, the same key as every
build: certificate SHA-256 `fc0db84e…7d16`, targetSdk 35, both ABIs, `isGame` and `appCategory`, zipalign OK); it
installs over build 14 as usual. Compared entry by entry with build 14 (`mcvs-offline-0b90763.apk`) it changes only
the server DLL, the signature files and the 16 MVID bytes of `Game.dll`, `Gameloft.Engine.dll`,
`Gameloft.Util.dll` and `NicePlaySDK.dll` ([design/pc-port.md](design/pc-port.md) §13.1). All of build 15 is in the server: no patch's code changed (only the `bottile` description text), and
the PC kit changes by its embedded server DLL (and that text).

**Special Events: the Battle menu's second box, generated offline** ([LIVEOPS.md](LIVEOPS.md) §8a). The Battle popup's
**Special Event** box is the game's Arena mode; it unlocks only while an `ArenaModeEvent` is in progress, and before
build 15 the server never served one, so the box was always locked. Live ops now generates them from the clock:

* **Rotation**: a new mode every window (`liveops.arenaHours`, default 24 h, on a grid from the daily reset hour, so
  by default a new mode every day with the daily missions). 22 rotating modes built from the data pack's Arena
  variants: Havoc, Override, Ambush, Grand Bounty, Bountiful, Blitz and Scorecharge in the four modes, Competitive
  (best of 3, no duplicate agents) in the four modes and with a random mode per map, and the four plain modes. Each
  mode once per cycle, never the same twice in a row; the rotation is a function of the window alone (nothing is
  stored). `tripleElixir` runs only when pinned.
* **Rewards**: a Special Event match is a public PvP match like Classic (trophies, division, Credits, VersusCoins, MVP,
  agent XP, daily and weekly objectives), and wins pay ×1.25 Credits and VersusCoins (×1.5 in the Competitive modes),
  within the daily caps ([ECONOMY.md](ECONOMY.md) §4). The bots fill it exactly as in Classic (`instantBots`); every
  event allows all 21 agents and plays on a random PvP map.
* **Pin**: `liveops.arenaRotation` set to a mode id (for example `competitiveWarzone`, or `tdm` with
  `liveops.arenaMap`) always runs that mode: a "pick your mode" bot match with full rewards. `off` shows no Special
  Event.
* **Settings** (page: Live ops): `Special Events` (`liveops.arenaEvents`, on), `Special Event mode`
  (`liveops.arenaRotation`, `rotate`), `Special Event length (hours)` (`liveops.arenaHours`, 24, 1–168), `Special
  Event map` (`liveops.arenaMap`, `Random`); applied at the next CRM fetch. Once the game has been handed a Special
  Event, switching them off keeps the current mode until the game restarts (the game never clears its Arena event, and
  a gap would leave the match without its parameters; the log says `Special Events are switched off, but the game
  already has one`).
* **Safety**: every Arena event passes `TleTemplateValidator` (the client's three traps: `img` mandatory, a non-empty
  agent list without "Any", no gap between windows); the CRM always lists the window in progress and the next one as a
  contiguous chain (three in a window's last 2 s), so the box switches to the next mode on its own timer. Hand-written
  Arena events in `content/social/events.json` get the same per-event checks and a log line where their chain ends.
  `GET /_mcvs/liveops` shows the current, next and upcoming modes.
* Checked through the real client's code (`LiveOpsArenaConformanceTests`: 14 days of events through `ArenaModeEvent`
  parsing, `GetArenaUIInfo` for the Battle popup's box, the TS's choice of the event for a room and the TS to GS
  hand-over), **not yet on a device**.

**The Practice tile (optional, off by default)**. The game has its own bot-match tile, "PRACTICE" (MCVS_Meta
`MainMenuElements` row 13), hidden in Gameloft's shipped data. The new setting **Practice tile in the main menu**
(`crm.practiceTile`, page: Settings › Gameplay, default off) adds two CRM cells that show it as the sixth and last
tile of the main menu; with the default `bottile` patch it starts the same bot-filled, full-reward match as Battle ›
Classic multiplayer, without the mode and group screens (without `bottile`: the game's free bot match, 5 Credits per
win). It applies at the next CRM fetch (after a match, or at the next start). The served documents go through the
real `StandardCrm.Data` in `PracticeTileConformanceTests`; where the tile sits and how it looks are **untested**. To
try it: turn it on on the page, play one match (or restart), look at the main menu; turn it off again if the menu
looks wrong.

**Doc corrections**. The docs said Battle had a visible "bot-match tile" and that the `bottile` patch drove it. In fact
the game shows no bot-match tile: the full-reward bot match is **Battle › Classic multiplayer** (7 bots at once with
`instantBots`), `bottile` only affects the hidden PRACTICE tile (so it changes nothing unless `crm.practiceTile` is
on), and "BOTMATCH" is only a `MenuMatchInfos` row with the confirm popup's text. Corrected in README, PLAYER-SETUP
(a new "Match modes" list), PLAYER-SETUP-PC (WR3 step 8 starts the bot match from Battle › Classic), ARCHITECTURE,
ECONOMY ("What each match mode pays": Classic, Special Event, tournaments, the Practice tile with its payout before and
after PvP activation), LIVEOPS (`ArenaModeEvent` needs no LocalAnubis change: it already copies the room kwargs),
PROTOCOL, the PC-port design and research notes, research 07 and verify-C4, and the `bottile` patch's description.
Tournaments are stated as they are: private matches, never filled with bots and without trophies or currency by the
game's design; they need other players (LAN is experimental). Not built, by decision: bots in tournaments.

**PC**: no PC-specific change. The kit `McvsOffline.PcKit.exe` (win-x64, one file, SHA-256 `34131809abd25515bd0ff791f3ebc82f337d906e7598ca0c3d4176b3ae1baabb`) was republished
because its embedded server DLL changed; the expected `verify` values of WR3 ([design/pc-port.md](design/pc-port.md)
§13.1, [PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) §7) were re-recorded: the server DLL and, with it, the four patched
DLLs' hashes and MVIDs changed; the config is build 14's, and `install.json` says `serverBuild` 15. WR3 runs with
this kit.

**Checked**: `McvsOffline.sln` (Release), the Android embedded DLL against the APK's assemblies and the PC DLL with
`Gameloft.Logs`; all four test projects with `MCVS_REFS`, `MCVS_PC_REFS`, `MCVS_APK`, `MCVS_DATAPACK`,
`MCVS_BUILD_TOOLS`, `MCVS_REQUIRE_REAL_CLIENT=1`, `MCVS_REQUIRE_PC=1`, `MCVS_REQUIRE_PC_E2E=1`, `MCVS_REQUIRE_EMBEDDED=1`,
`MCVS_REQUIRE_BUILD_TOOLS=1` and `MCVS_REQUIRE_NODE=1`: **McvsOffline.Tests 1551/1551, McvsOffline.Embed.Tests 878/878,
McvsOffline.PcKit.Tests 59/59, McvsOffline.PcSpike.Tests 62/62, 0 skipped** (2,550). The content-gen tests (`python3 -I tools/content-gen/test_content_gen.py`): 32/32.
The PC end to end on Linux (§12.6): `pc install` on a temp copy of the Steam files, `verify`, `status`, a second install with the same bytes, `uninstall` restoring every original SHA-256. Not checked: anything on a phone or on Windows.

### What to check on the phone (build 15)

Install build 15 over build 14 (no uninstall); the save and settings stay. Build 14's checks still apply.

1. **Special Event unlocked.** Battle › "Select game mode": the second box, **Special Event**, is unlocked and shows the
   mode's title and match type (e.g. "TEAM DEATHMATCH: HAVOC") and a countdown to its end. In `server.log` the TS's
   `GET events?event_ids=…` lists two `mcvs_arena_…` ids. The page's Live ops shows the four Special Event settings.
2. **A Special Event match.** Open the box: the lobby shows the rules text and the 21 agent icons; Start fills the
   room with 7 bots at once, as in Classic, and the match is the event's mode on a valid map. No GS exception about
   `_arenaParams` in the logs, no `[watchdog] STALL`, no `AndroidRuntime: FATAL EXCEPTION`.
3. **Its rewards.** The end screen pays trophies, Credits and VersusCoins; a win pays more than the same win in
   Classic (×1.25, ×1.5 in a Competitive mode, up to the daily cap); the daily and weekly objectives advance.
4. **The next window.** Set `Special Event length (hours)` to 1 on the page (or play across the daily reset): when the
   countdown ends the box switches to the next mode without a restart, and a match started after the change still
   plays. Put it back to 24.
5. **Pin.** Set `Special Event mode` to `competitiveWarzone`: after the next match (or a restart) the box shows it;
   the match has three rounds and no duplicate agents in a team. Put it back to `rotate`.
6. **Off.** Turn `Special Events` off while the game runs: the box stays usable (the log says `Special Events are
   switched off, but the game already has one`); after a restart it is locked. Turn it on again.
7. **Practice tile (optional).** Turn on **Practice tile in the main menu** (Settings › Gameplay), play one match or
   restart: does a PRACTICE tile appear in the main menu, where, and does it look right? Tap it, confirm: a bot match
   that pays like Classic. Turn the setting off: the tile is gone after the next match or start.
8. As always: boot to the menu, one Classic bot match, the shop, the page (Status, Settings, Saves › Save now).

## Build 14 (October 2026): the PC port's WR2 fixes, ready for the third Windows round

### What build 14 contains

`0b90763` on `wip/pc-port` (`McvsVersion.Build` is `14`). APK: `mcvs-offline-0b90763.apk` (default patch set, the same key as every
build: certificate SHA-256 `fc0db84e…7d16`, targetSdk 35, both ABIs, `isGame` and `appCategory`, zipalign OK); it
installs over build 13 as usual. Compared entry by entry with build 13 (`mcvs-offline-f158cb9.apk`) it changes only
the server DLL, the signature files and the 16 MVID bytes of `Game.dll`, `Gameloft.Engine.dll`, `Gameloft.Util.dll`
and `NicePlaySDK.dll` ([design/pc-port.md](design/pc-port.md) §13.1).

**PC (Steam): what WR2 showed.** The owner's second Windows round (2026-10-29, the build-13 preview kit, Windows build
26200, .NET 4.8.1) installed exactly the tested bytes, started the game 15 times to the main menu with our server,
played two matches with their rewards saved, and opened the settings page in the Steam overlay twice. But **the game
loaded its own `Game.dll` from the game folder in every start**: the engine loads `Game` by its bare name, and such a
load ignores the `<codeBase>` redirect in `ModernCombatVersus.exe.config`. The other four redirected assemblies were
ours. So every fix that lives in `Game.dll` was missing: the inbox crashed twice after unlock codes (a stock-game bug
our `store` patch fixes), the bot match ran the game's old offline mode instead of our in-process server, Settings ›
Account had no "MCVS Offline" button, and long game-thread freezes of 60-180 s followed the big unlock codes (most likely
the stock code without our `signatures` fix). A third crash was a native access violation in the engine during the
unlock-all grant at the in-game High graphics; its cause is unknown, because no crash dump was kept. The kit still said
"installed, current", and our crash reporter wrote nothing for the three crashes. Settled by WR2: http.sys works for a
normal user (no extra server mode needed), the quicklaunch continuations run on the game thread, the memory rule keeps
Physics, the game log bridge attaches, and there is no tracker traffic with the defaults
([design/pc-port.md](design/pc-port.md) §13.0 and §14 have the step-by-step results and the open questions).

**PC: the fixes in build 14** ([design/pc-port.md](design/pc-port.md)):

* **`Game.dll` is ours** (WR2 F1): the new mandatory PC patch `gameload` (Gameloft.Engine.dll, fingerprint-guarded,
  §8.9) makes the engine load `Game` by its full name, which does follow the redirect; the config also gets one
  `<qualifyAssembly>` per redirected assembly (§7.4; build 16 removes them again: most likely they made .NET ignore the
  whole config, so the game ran without MCVS Offline). `[boot] patches:` reports `gameload`.
* **"installed, NOT ACTIVE"** (F2): a start that loaded a redirected assembly from outside `McvsOffline\bin`, or ran
  without the witness of an applied patch, is an error: a `[boot] ERROR` line, `install: installed, NOT ACTIVE (…)` in
  the `[status]` line, a red banner on the settings page, and the kit's `status` and `verify` read the last start's
  boot lines (`activation:`), §7.6.
* **Crash capture on the game thread** (F3): the game's exe replaces the crash handler without chaining, so the host
  now installs a native top-level filter over it (compiled ahead while the process is healthy, no allocation in the
  report): `logs\crash-<time>-native.txt` with the code, the address and module, the thread and the game's last Fatal
  line, then the game's own filter runs as before. The game's Fatal lines before a rethrow into native code become one
  `exception-*.txt` headed `PROBABLY FATAL`. The pid is in the start and STALL lines (§6.4).
* **Freeze and crash dumps** (F4): the kit's `watch` follows `bootstrap.log` and takes one full dump of the game per
  STALL of 60 s or more (at most 3 per run, 6 in `%LOCALAPPDATA%\McvsOffline\dumps`, only with 5 GB free); `collect`
  lists these and Windows' LocalDumps with path and size and never zips them. The player guide has the one-time WER
  LocalDumps setup ([PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) §6).
* **game.log** (F5, F6): on PC the game enables every log level itself, so `game.log` was 4 MB of online chatter; it now
  keeps Fatal plus `gameLog.levels`. Lines carry UTC ISO times, and the game's own access tokens are redacted.
* **Graphics** (F7): the `[boot] graphics:` and `[status]` lines read the in-game Graphics options (`Performance`,
  render scale, FPS, Vsync, full screen) from the game's `GamePreferencesComponent`, not the Android key; the PC help
  texts of `graphics.profile` and `graphics.fullProfile` say what they do there; the player guide points to the in-game
  menu and says High costs memory.
* **The kit's capture** (F9): Windows events with UTC times and in the ANSI code page (no more `Informaþ§es`), each
  event parsed on its own; the debug-output capture in the ANSI code page; `MyLocalStorage` files listed; `probe` lists
  the GPU adapters, displays, physical memory and the firewall rules for the game; the kit's own SHA-256 in
  `summary.txt` and every log header; `GameVersion` and `core.dmp` counted as the game's own files (`core.dmp` with size
  and time, never zipped); process memory in every `[status]` line.
* **Smaller** (F8, F10, F11): the settings page's first 401 is no longer a boot milestone, an overlay tab from an
  earlier start says so, `/favicon.ico` answers 204, and `DataCenter` is a managed `args.txt` key.
* **Memory** (not changed in build 14): WR2 peaked at 84 % of the 2 GB address space with three Worlds during a stall.
  The recommendation, if WR3 confirms the pressure, is an opt-in LAA-flagged copy of the exe in `McvsOffline\bin`
  started by a Steam launch option ([design/pc-port.md](design/pc-port.md) §17).

The kit `McvsOffline.PcKit.exe` (win-x64, one file, SHA-256 `0aa37f249bb57661e49d50af50596e2765052a2f37390e23073156a9d7f6a7be`) and the
owner's guide for **WR3**: [PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) (design §13). The expected `verify` values were
re-recorded for build 14 (the config and all five DLLs changed). WR3 repeats the steps WR2 could not reach with
`Game.dll` active, and does the untested ones (the page's actions, a bot match from Battle › Classic multiplayer, minimise and fullscreen, Steam
achievements, Restart, uninstall).

**Android: the shared changes.** Android's behaviour changes only where the code is shared, all small:

* Access tokens (`mcvs` + 8 hex + `.` + 32 hex) are redacted anywhere in a log line, in every log (server.log,
  game.log, the share text). The 401 diagnosis line shows the token redacted too.
* `game.log` lines carry UTC ISO times (`2026-10-28T17:53:25.781Z`) instead of the local `HH:mm:ss.fff`; the page's log
  view reads both forms and shows local time. Android's `game.log` still keeps every entry the game logs.
* The settings page: a tab whose ticket was refused (a used ticket without its cookie) shows "This tab is from an earlier
  start of the game: close it and open the page from the game again"; the pages carry an empty icon, and
  `/favicon.ico` answers 204 (logged at DEBUG) instead of an unknown endpoint.
* The boot trace no longer records the page's routes (its first 401 by design) as the last milestone.
* `DataCenter` is a managed `args.txt` key: with the end marker deleted by hand, the line no longer survives as a player
  line.
* The admin status payload is unchanged on Android (the new `client` object appears only while the PC host reports NOT
  ACTIVE).

**Checked**: `McvsOffline.sln` (Release), the Android embedded DLL against the APK's assemblies and the PC DLL with
`Gameloft.Logs`; all four test projects with `MCVS_REFS`, `MCVS_PC_REFS`, `MCVS_APK`, `MCVS_DATAPACK`,
`MCVS_BUILD_TOOLS`, `MCVS_REQUIRE_REAL_CLIENT=1`, `MCVS_REQUIRE_PC=1`, `MCVS_REQUIRE_PC_E2E=1`, `MCVS_REQUIRE_EMBEDDED=1`,
`MCVS_REQUIRE_BUILD_TOOLS=1` and `MCVS_REQUIRE_NODE=1`: **McvsOffline.Tests 1521/1521, McvsOffline.Embed.Tests 878/878,
McvsOffline.PcKit.Tests 59/59, McvsOffline.PcSpike.Tests 62/62, 0 skipped** (2,520). The PC end to end on Linux (§12.6): `pc install` on a
temp copy of the Steam files, `verify`, `status`, a second install with the same bytes, `uninstall` restoring the exact
listing, ILVerify as the stock files (§13.1). Not checked: anything that runs only on Windows (the native crash filter
through a real crash, the kit's dumps, the Windows event capture end to end) or only on a phone.

### What to check

* **PC: WR3**, the checklist in [PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) §7. First of all step 4: `[boot] assemblies:`
  with `Game …\McvsOffline\bin\Game.dll`, `gameload yes`, `store yes`, and `status` saying `activation: active at the
  last start`.
* **Phone**: build 13's checks still apply (Game Mode flags, the Account menu, log cleanup; "What to check on the phone
  (build 13)" below). For build 14 also: in `McvsOffline/logs/game.log` the lines start with UTC ISO times; no
  `mcvs<8 hex>.<32 hex>` token appears unredacted in any log; the page's Logs view still shows `game.log` lines with
  their times.

## Build 13 (October 2026): Game Mode flags, a two-button Account menu, log cleanup; the PC port ready for WR2

### What build 13 contains

`f158cb9` on `wip/pc-port` (`McvsVersion.Build` is `13`). APK: `mcvs-offline-f158cb9.apk` (default patch set, the
same key as every build: certificate SHA-256 `fc0db84e…7d16`, targetSdk 35, both ABIs, zipalign OK); it installs over
build 12 as usual. Compared entry by entry with build 12 it changes only `AndroidManifest.xml` (the two `game`
attributes), the server DLL, the signature files and the 16 MVID bytes of `Game.dll`, `Gameloft.Engine.dll`,
`Gameloft.Util.dll` and `NicePlaySDK.dll` ([design/pc-port.md](design/pc-port.md) §13.1).

**Android**

* **The 'game' manifest flags** (new default patch `game`, [design/pc-port.md](design/pc-port.md) §10,
  [PLAYER-SETUP.md](PLAYER-SETUP.md) §4 and §6): `<application>` gets `android:isGame="true"` and
  `android:appCategory="game"`, so Android's Game Mode (Android 12+) and the phone maker's game tools can treat MCVS as
  a game. Samsung's Gaming Hub and Game Booster go mostly by their own package list, so the APK can only help; adding
  the game by hand (Gaming Hub › My games › Add game) always works. Side effects, all from Android: GameManager reports a
  mode, the chip may get a game power hint while the game is in front, and a phone may cap the screen at 60 Hz for it
  (the game runs at 30 fps). Nothing in the game reads the flags.
* **Settings › Account shows only Redeem code and MCVS Offline**, side by side in the middle of the screen (patch
  `adminpage`; `hosts/embedded/Admin/AccountMenuLayout.cs`). Facebook, Google Play Games and Restore (the in-app purchase
  restore) are hidden, since none of them can work offline. The setting `admin.accountMenu.hideDeadButtons` (Settings ›
  Unlocks & codes, on by default) brings them back at the next opening of the menu. Every opening writes one
  `[admin] account menu:` line in `server.log`: `2 buttons centred (vpX=…, vpW=…, x0=…, x1=…)`, or `not centred (<why>)`
  when the buttons stay where the game puts them ([PLAYER-SETUP.md](PLAYER-SETUP.md) §7).
* **Log cleanup at every start** (`hosts/embedded/Diagnostics/LogRetention.cs`, [design/settings-page.md](design/settings-page.md)
  §7.7, [PLAYER-SETUP.md](PLAYER-SETUP.md) §6 and §8): each start begins new `bootstrap.log`, `server.log`, `game.log` and
  `unknown-endpoints.jsonl` files (the previous start's are usually the `.1` files), records itself in
  `logs/starts.txt`, and the thread `mcvs-log-retention` deletes the log, crash, exception, thread-dump and exit files
  of older starts in `McvsOffline/logs/` (at most 5 s, the rest at the next start). The Downloads/MCVS-Offline copies
  follow the same starts. Setting `logging.keepStarts` (Settings › Diagnostics, 0–10, default 1: this start and the
  previous one; applies at the next start). Saves, settings and the logs zips are never deleted. Log lines: one
  `[logs]` line in `bootstrap.log`, e.g. `keeping this start and 1 previous (since 2026-10-28T19:02:11Z): deleted 23
  files, 41.6 MB (server.log.2-5, game.log.1, crash 1, exit 2; fallback 3)` (WARN when a file could not be deleted; on
  the first starts of build 13, before enough starts are recorded, `…: only 0 starts recorded so far, nothing deleted
  this time`), and `[mirror] keeping 2 starts:
  deleted N copies (… MB)` for Downloads. The share text's "previous start" section now reads the previous start's
  files.
* **CRM inbox**: our MCVS Offline item comes first among the newsfeed items of its asset key (items without an asset
  share the key ""), so an overlay item cannot give it its dates.

**PC (Steam)**

* The port is **ready for the second Windows round (WR2)**: the kit `McvsOffline.PcKit.exe` (win-x64, one file, SHA-256
  `c608fd1903b83763657646482a0c7aee6ec8282e10f63aa237dcb753b2e8715d`) and the owner's guide with the checklist and the
  expected `verify` values, [PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md) (design: [design/pc-port.md](design/pc-port.md)
  §13). The values were re-recorded for build 13 (the server DLL changed); `ModernCombatVersus.exe.config` is unchanged.
* New in build 13: `install --port n` also sets `"port"` in `McvsOffline\settings.json` (the port the server listens
  on), and without `--port` the installer keeps the port already there; the installer retries a `.mcvs-tmp` rename on a
  file a virus scanner briefly holds; the log cleanup works the same way in `McvsOffline\logs` and
  `%TEMP%\McvsOffline\logs`. The Account menu setting is not on the PC's settings page (the Steam build has no
  Facebook, Google or Restore button).

**Checked**: `McvsOffline.sln` (Release), the Android embedded DLL against the APK's assemblies and the PC DLL with
`Gameloft.Logs`; all four test projects with `MCVS_REFS`, `MCVS_PC_REFS`, `MCVS_APK`, `MCVS_DATAPACK`,
`MCVS_BUILD_TOOLS`, `MCVS_REQUIRE_REAL_CLIENT=1`, `MCVS_REQUIRE_PC=1`, `MCVS_REQUIRE_PC_E2E=1`, `MCVS_REQUIRE_EMBEDDED=1`,
`MCVS_REQUIRE_BUILD_TOOLS=1` and `MCVS_REQUIRE_NODE=1`: **McvsOffline.Tests 1501/1501, McvsOffline.Embed.Tests 827/827,
McvsOffline.PcKit.Tests 40/40, McvsOffline.PcSpike.Tests 62/62, 0 skipped** (2,430). Not checked: the JNI and
MediaStore side of the log cleanup (`ExitReasons`, the Downloads copies) and the menu layout run only on a phone.

### What to check on the phone (build 13)

1. **Before installing**, with a PC: `adb shell cmd game list-modes com.gameloft.android.ANMP.GloftMVHM` on build 12.
   Android 16 prints `Package com.gameloft.android.ANMP.GloftMVHM is not of game type, …` (Android 12–15: `current
   mode: unsupported, available game modes: []`).
2. Install build 13 over build 12 (no uninstall); the save and settings stay. Run the same command again: expected
   `current mode: standard, available game modes: [standard,custom]`; `adb shell dumpsys game` lists the game.
3. Settings › Storage (or Settings › Apps › MCVS › Storage): is the game counted under **Games** instead of **Apps**?
   (Only on Settings that follow Android's own; One UI may not show it.) Gaming Hub › My games: is MCVS listed without
   adding it? Does the Game Booster panel appear in a match?
4. Settings › Account: only **Redeem code** and **MCVS Offline**, side by side in the middle; both work. In `server.log`:
   `[admin] account menu: 2 buttons centred (…)` once per opening. Then turn off **Only Redeem code and MCVS Offline in
   Settings › Account** on the page, reopen Settings › Account: the game's own buttons are back (stacked on the left);
   turn it on again.
5. Logs: after three starts of build 13, `McvsOffline/logs/` (Files app or `adb shell ls`) holds only the last two starts' files;
   `bootstrap.log` has one `[logs] keeping this start and 1 previous …` line and no WARN after it; Downloads/MCVS-Offline
   holds the copies of two starts (`[mirror] keeping 2 starts: …`). Set `logging.keepStarts` to 0, restart twice: only
   the current start remains. Put it back to 1. The saves, `settings.json` and any saved `mcvs-logs-*.zip` are untouched.
6. The inbox: the MCVS Offline message is still there and its button opens the page (where it sits in the list was
   not checked in a running game).
7. As always: boot to the menu, one bot match, the shop, the page (Status, Settings, Saves › Save now) and the
   graphics profile; no `AndroidRuntime: FATAL EXCEPTION` and no `[watchdog] STALL` in the log.

## Build 12 and device test 9 (October 2026): ASTC HD textures and graphics profiles work

### What build 12 contains

`7f24f82` (`McvsVersion.Build` is `12`) adds to build 11:

* **The HD installer writes only in folders it owns** (`wip/hd-writable`; see "Build 11 device test" below).
* **The ASTC HD data pack** (`tools/hd-textures astc-pack`, [design/hd-textures.md](design/hd-textures.md) §6,
  [INSTALL-OFFLINE.md](INSTALL-OFFLINE.md), "The ASTC HD data pack"): Gameloft's whole 1.17.32 ASTC `files/` tree in
  which 2,215 textures are the PC textures re-encoded to ASTC at the PC resolution, in 8 parts below 380 MiB, with its
  own installer index (`McvsOffline/install/pack.info`) and `McvsOffline/install/replaced.tsv` (the replaced files with
  Gameloft's size and CRC32 of each).
* **No Clear storage with build 12**: `InstallerData` recognises the pack by `replaced.tsv` and, when the installer still
  holds the index of the data that was there before (the ETC pack), switches it to the pushed saved index (`installer
  data: index switched to the saved index (ASTC with 2215 replaced files, ...)`). The switch now runs before the
  damaged-list recheck, and a `damaged.txt` left by the old data is removed, since it says nothing about the new data.
  Build 11 and older keep the old ETC index, so with them the pack needs Clear storage and the profile pushed back.
* **ASTC pack fixes**: originals whose size is not a power of two ("stretched", five in the ASTC pack) are compared with
  the PC texture at a reduced size, so the 200x200 3D-menu emblem `Data/d/1a57284.f` is replaced too (2,215 files; the
  first parts of 2026-10-28 replaced 2,214). The `README.txt` of the last part has a step that puts the profile back
  after Clear storage on build 11 and older; the ETC `files/` for going back are kept on the computer, not moved aside
  on the phone, where Clear storage would delete them.

**Checked**: `AstcPackTests` (the stretched originals; `TheReadmeStepsInstallThePackAndKeepTheProfile` runs the
`README.txt` command lines on a stand-in for the phone, for an APK after build 11 and for build 11 with Clear
storage); `TheRealPackIsACompleteAstcInstallForTheHost` on the rebuilt parts (15,747 files, 2,215 replaced, a fresh install and a
switch from the ETC index, every CRC). No full `scripts/build.sh` count was recorded for build 12; the last one is under
"Build 11 device test".

### What the owner saw (device test 9)

Owner's Galaxy S22 (Android 16), October 2026. No logs were sent; the results are the owner's report.

* **Install**: builds 10, 11 and 12 (targetSdk 35) installed and played fine.
* **ASTC HD data pack**: works. The game "looks crazy good" and runs "at the same framerate as before, zero performance
  loss". Which parts were installed (the first parts of 2026-10-28 with 2,214 replaced files, or the rebuilt parts with
  2,215) was not reported.
* **Graphics profiles**: work.

### What stays open

* The Cosmetic crate (starts on 2026-11-09).
* The ETC HD packs (`McvsOffline/hd/mcvs-hd-*.zip`) on a phone: the S22 runs the ASTC pack now ("What to check on the
  phone (build 11)").
* From build 10: the Restart question (does the game come back by itself on Android 13+?), the page layout at target 35
  with both navigation modes, the notification prompt, reminders, the share sheet, the logcat greps and the
  data-download notification ("What to check on the phone (build 10)", items 3-5 and 8-11).
* The logcat checks of build-10 items 1 and 6, which need a log: no `AndroidRuntime: FATAL EXCEPTION` in the first hour
  after the install; the `[boot] graphics:` line for each profile, no `[watchdog] STALL` and a bot match with
  `graphics.fullProfile` at `ultra`.
* The PC port and the Android 'game' manifest flags (Game Mode and Samsung's Game Booster): in build 13 ("Build 13").

## Build 11 device test: the HD installer could not write in `McvsOffline/hd/` (fixed on `wip/hd-writable`)

Owner's Galaxy S22 (Android 16, build 11, targetSdk 35), first start with a pack in `McvsOffline/hd/`:

```
W McvsOffline: [hd] hd: cannot back up Data/9/705cf.f to .../files/McvsOffline/hd/backup/Data/9/705cf.f
  System.UnauthorizedAccessException: Access to the path '.../files/McvsOffline/hd/backup/Data/9' is denied. ---> System.IO.IOException: Permission denied
I McvsOffline: [boot] +0.204 s hd: none installed; 906 files at the next start; 1 failed (see the log)
```

**Cause**: the owner made `McvsOffline/hd/` with adb (Shizuku). On Android 11+ a folder under `Android/data/<package>/`
made by another tool belongs to that uid (`shell`, `media`): the app reads the pack in it but cannot create anything
inside. `McvsOffline/` and `Data/` were made by the app and are writable. Nothing in `Data/` had changed (the backup is
the first step), so the phone was left as it was.

**Fix** ([design/hd-install.md](design/hd-install.md), "Where the installer writes"; `hosts/embedded/Hd/HdPlaces.cs`):

* `McvsOffline/hd/` is a read-only drop folder: the installer never creates, writes, renames or deletes anything in it.
* `installed.json`, the backups and `imported.txt` go to `McvsOffline/hd-install/`, made by the app; when it does not take
  a probe file, `getFilesDir()/McvsOffline/hd-install/` (logged as a warning; Status adds "backups in the app's internal
  storage"). The folder with the current `installed.json` is used first; when both hold one, `imported.txt` tells the
  newer state from the stale copy `McvsOffline/hd-install` keeps while it cannot be written.
* Before anything is removed or installed, our folder, `Data/` and every `Data/<h>/` to be touched take a probe file; when
  one does not, nothing in `Data/` changes and Status and `[boot] hd:` give one reason, e.g. `none installed; cannot write
  McvsOffline/hd-install: permission denied (nor the app's internal storage: permission denied); nothing changed`. The
  installer's index, our saved copy and the journal are still made to match `Data/` (also after `InstallerData` replaced
  the index at that start).
* Build 11's `McvsOffline/hd/installed.json` (on phones where the app made `hd/` itself) is taken over once (checked,
  copied byte for byte, noted in `imported.txt`); its backups are used where they are for removal and are never
  deleted; once nothing is installed Status says they may be deleted. A state of another place beside ours is taken
  over when it is newer (it took ours over), else merged into ours (no record is dropped); the other `hd-install`
  folder's taken-over state and backups are deleted once nothing is installed.
* Every guarantee stays: originals checked, atomic writes, index/journal consistent with `Data/` after every start, a
  start cut short completed by the next, removal byte-identical (from our backups or the old ones).

**Checked**: `HdInstallerTests`: every start of every test now runs with `McvsOffline/hd/` read only through a fake
(`HdInstallerOptions.WriteDenied`, called before every create/write/rename/delete; the tests run as root, where chmod
denies nothing) and fails on any write attempt there (with the old folder layout two tests fail); new: the device case
(install, removal, drop folder byte-identical), the folder mode 0555 on disk without the fake, `hd-install` not
writable (internal storage used, kept while it holds the state, removal byte-identical, the next install back in
`hd-install`), nothing writable (the exact Status line; `Data/`, index and journal unchanged, no folder created),
`Data/8` not writable (nothing installed; with HD files installed, removal changes nothing and the files stay
consistent), build 11's state taken over with `hd-install` writable and not (no new writes, removal byte-identical from
the old backups, index and journal pristine, not taken again, reinstall backs up into our folder), an unreadable old
state not taken over and nothing changed until it can be read; review fixes: `hd-install` denied for one start and
writable again (the newer state in internal storage stays in use, removal puts all 5 originals back, no "may be deleted"
for our own folders, the stale copy and its backups cleaned up), two unrelated states merged (removal puts every
original back), `Data/8` denied right after `InstallerData` restored Gameloft's index from `files/pack.info` (the index
still matches the HD file; the installer accepts it). The `scripts/build.sh` steps with the same environment as
below: **McvsOffline.Tests 1345/1345, McvsOffline.Embed.Tests 506/506, McvsOffline.PcSpike.Tests 62/62, 0 skipped**.

**On the phone**: no new step: with the pack in `McvsOffline/hd/` (made with adb as before) the next start installs
(`[boot] hd: <name>, N files installed ...`); `McvsOffline/hd-install/` appears next to it.

## Build 11 (plan): HD textures from the Steam version

Branches: `feat/hd-install` (the phone side, this section) and `wf/hd-converter` (`tools/hd-textures`: the PC side that
converts the Steam textures into an HD pack). Their contract is the converter's
[design/hd-textures.md](design/hd-textures.md) (the pack, its manifest, the rules), identical on both branches; the
installer's side is [design/hd-install.md](design/hd-install.md). Owner's decisions after the build-10 probe: route A (replace the files in
`Data/`); ETC1 for the ETC pack the S22 runs (not ASTC); scope = map surfaces, lightmaps (all of a level at once),
third-person characters, grenades and equipment (research/pc-textures.md §7 tiers 1 and 2, about 1,750 files); the zip
mount of route B removed entirely.

### What is new (phone side)

* **The zip mount is gone**: `hosts/embedded/Hd/HdTextures.cs` and `EngineFiles.cs`, the `[boot] hd textures:` probe
  lines, `hd.mount`, GraphicsHooks' first-engine hook and their tests. Patch `graphics` is unchanged.
* **HD pack installer** (`hosts/embedded/Hd/HdInstaller.cs`, [design/hd-install.md](design/hd-install.md); the pack as
  the converter writes it, [design/hd-textures.md](design/hd-textures.md)): `McvsOffline/hd/mcvs-hd-*.zip` (stored
  entries `Data/<h>/<rest>.f` plus `mcvs-hd.json`, format 1, with each file's original and replacement size and CRC32) is installed at start-up, before the Java
  installer runs: originals checked and backed up to `McvsOffline/hd-install/backup/` (`McvsOffline/hd/` is only read;
  see the device test above), `installed.json` written before any file
  changes, each new file written beside the original and renamed over it, then the installer's `pack.info`, the saved
  copy and the journal of split pieces rewritten for those files, so the installer's size check passes at every start
  **without `SKIP_VALIDATION`**. At most 8 s per start, installing or removing (a level's lightmaps always together); the
  rest at the next start.
  An index reset (reinstall, `gl_ver`, CDN re-sync, no index at all) is repaired at the next start; with no usable index
  the originals go back until there is one (INSTALL-OFFLINE.md, "HD textures in `Data/`").
* **Settings and status**: `hd.enabled` (default true) now means "install the pack" and false removes it at the next
  start; Actions › **Remove HD textures** turns it off and offers the restart; Status › Game host › HD textures shows the
  pack, files and size; the start-up log has one `[boot] hd: ...` line.

### Checked (feat/hd-install)

`HdInstallerTests` (synthetic data pack and index in Gameloft's form, the host's order Prepare → InstallerData → Apply):
install with the installer's checks (sizes, journal, every CRC) passing; second start writes nothing; removal gives
byte-identical originals, index, saved copy and journal; a crash at five points of an install and one of a removal,
each completed by the next start; the 8 s budget over four starts, consistent after each, for installing, removing
(a level's lightmaps never mixed), a new pack (removals first) and putting originals back without an index; reinstall, `gl_ver` mismatch,
no index anywhere (rebuilt with the HD sizes, the pack recognised by the original sizes), Gameloft's index over an
installed pack, no usable index (originals back, pack back with the index); originals that do not match, a lightmap
group with one bad file, broken manifests (also the installer's first-draft schema), unsafe paths; packs in the
converter's exact shape (aligned entries, every manifest field of design/hd-textures.md §3). With the converter in the
tree (`MCVS_HD_CONVERTER`, defined when `tools/hd-textures/PackBuilder.cs` exists) a pack written by its `PackBuilder` is
installed and removed; checked on a scratch merge with `wf/hd-converter` (87/87 HD tests), and a real
`mcvs-hd-equipment.zip` (33 files) the converter built from the PC install and the ETC pack installed on the real ETC
`pack.info` (`Gameloft index in place (ETC, 15747 files, 16218 entries)`, every index CRC that of the HD bytes) and
removed to the original bytes and index. On the real ETC `pack.info` (`MCVS_DATAPACK`): written back
byte for byte (it has an empty `.\` section), two real files (the probe texture and a split file) swapped and restored to
Gameloft's exact bytes, a pack labelled ASTC refused. page-smoke: the Remove action. The `scripts/build.sh` steps with `MCVS_REFS`, `MCVS_REQUIRE_EMBEDDED=1`,
`MCVS_REQUIRE_REAL_CLIENT=1`, `MCVS_REQUIRE_NODE=1`, `MCVS_REQUIRE_PC=1` and `MCVS_DATAPACK` (ETC): **McvsOffline.Tests
1345/1345, McvsOffline.Embed.Tests 442/442, McvsOffline.PcSpike.Tests 62/62, 0 skipped**.

### What to check on the phone (build 11)

Needs an HD pack from `tools/hd-textures` (the converter branch) for the ETC pack. Keep
`adb logcat -s McvsOffline mono mono-stdout AndroidRuntime > b11.txt` running.

**Device test 9 (build 12):** the S22 moved to the ASTC HD data pack instead of an ETC HD pack, and the HD textures are
confirmed with it: item 3 as a whole (the owner: the game "looks crazy good") and the frame rate of item 5 (the same as
before; no heat or `meminfo` figures were sent). The ASTC install steps (INSTALL-OFFLINE.md) take the place of item 1.
Items 2, 4, 6 and 7 are for an ETC HD pack and were not run.

1. **Undo the build-10 probe first.** The 13 recoloured probe files are still in `Data/` and `qaTestingConfigs.txt` still
   says `SKIP_VALIDATION=1`. With `SKIP_VALIDATION` gone the installer would delete those 13 files (their size is not the
   index's) and try Gameloft's CDN. Game closed: push the 13 originals back (the same `Data/<h>/<rest>.f` paths from
   the PC copy of the ETC pack, which is byte-identical to the phone's), then
   `adb shell rm /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/qaTestingConfigs.txt`. Start once: the
   game must start without downloading anything (`[boot] installer data: Gameloft index in place ...`).
2. **Install**: game closed, `adb push mcvs-hd-<name>.zip .../files/McvsOffline/hd/`. Start: `[boot] hd: <name>, N files
   installed (...); N written now` and, for a large pack, "M files at the next start"; restart until that part is gone.
   Note how long each of these starts takes to reach the menu.
3. **In a match**: Port (the probe's map) and two others: surfaces, characters, grenades sharper; lightmaps of a level
   all HD or all not; no black or magenta surfaces, no missing textures. Screenshots next to build 10's.
4. **No validation trouble**: close and start again twice: `[boot] hd: ... installed (...)` without "written now", no
   download screen, no `[installer]` line about downloading, with Wi-Fi and mobile data off.
5. **Memory and heat** (research §6, WP4): a 10-minute bot match at `graphics.profile` auto and at high; frame rate,
   heat, `adb shell dumpsys meminfo com.gameloft.android.ANMP.GloftMVHM` during the match.
6. **Remove**: Actions › Remove HD textures, restart: `[boot] hd: off (settings hd.enabled false); N originals put back`,
   textures as before, no download. Turn HD textures on again, restart: installed again from the pack.
7. **A backup** of the whole `files/` folder now includes `McvsOffline/hd/` (the pack) and `McvsOffline/hd-install/` (the
   originals); restoring it on a
   reinstall gives the HD textures back without reinstalling them (`installer data: index restored from the saved index`).

## Build 10 (October 2026): target Android 15, graphics profiles, HD texture probe, PC test kit (target 35 and graphics profiles confirmed in device test 9)

`feat/build10`: five reviewed branches in one build (`McvsVersion.Build` is `10`). Player guide:
[PLAYER-SETUP.md](PLAYER-SETUP.md) sections 4 and 7.

### What is new

* **Target Android 15** (new default patch `targetsdk`, `--target-sdk 29|35|36`, default 35;
  [research/target-sdk.md](research/target-sdk.md), invariant I1' in [ARCHITECTURE.md](ARCHITECTURE.md) §2), so Play
  Protect stops warning "built for an older version of Android" on install. Manifest: `targetSdkVersion` 35, `exported`
  on the three intent-filter components left after debloat, `POST_NOTIFICATIONS`, `<queries>` for http/https browsers,
  `allowNativeHeapPointerTagging="false"` (through `BinaryXml` growth: attribute, string and element insertion). Both
  `activity_in_game_browser` layouts get `fitsSystemWindows` (the page stays clear of the navigation bar at 35).
  `classes.dex`: 14 same-size, fingerprinted edits in 13 methods (`patcher/Dex/`): `FLAG_IMMUTABLE` on the restart alarm
  (P1: the page's Restart and the game's graphics-setting restart), the local reminders (P2-P7) and the data-download
  notification (P8); empty filters for the push relay and Play billing receivers (P10-P11); Google Analytics' three entry
  points return at once (P12-P14), and GA's four components leave the manifest (build-10 review: a dispatch job or alarm
  build 9 left with the system survives the update and would start GA, whose init creates a `PendingIntent` with flags 0,
  an `IllegalArgumentException` that kills the process on Android 12+). Refused at 31+ without `debloat`, and never
  written without the dex edits.
  `--target-sdk 29` gives build 9's manifest, layouts and `classes.dex`.
* **Notification permission** (host): on Android 13+ the game asks once, at the first main menu, when the build targets
  33+ and declares the permission (details below).
* **Graphics profile** (new default patch `graphics`, settings group Graphics: `graphics.profile`
  auto/mid/high/ultra, `graphics.fullProfile`) and the `[boot] graphics:` log line (details below).
* **HD texture probe**: the host mounted `McvsOffline/hd/hd-textures.zip` through the engine's zip search path;
  `tools/hd-textures` built a probe zip with one coloured texture (result below; removed again for build 11).
* **Status and restart wording**: Status › Game host gains Graphics, HD textures and Notifications rows and "Restart via"
  says that on Android 13+ the game may only close (`host.relaunchUnconfirmed`).
* **Project icon and launch screen, mechanism only** (`--art auto|stock|<dir>`, `patcher/Art`,
  [art/android/README.md](../art/android/README.md), `tools/art/export-icons.py`): icon PNGs replaced in place, `v31` /
  `night-v31` splash styles in `resources.arsc`, an optional themed icon. `art/android/` has no art yet, so build 10
  keeps Gameloft's icon and launch screen byte for byte (the patcher notes it).
* **PC test kit (WP0)**: `tools/pc-spike`, one self-contained Windows exe that runs the Steam game against our server
  and packs the answers of [research/pc-port.md](research/pc-port.md) §5 into one zip; owner's guide
  [PC-SPIKE.md](PC-SPIKE.md). Built for this build: `McvsOffline.PcSpike.exe`, 35,939,879 bytes, win-x64.

The details of the graphics profile, the HD texture probe's result and the notification permission follow.

### Graphics profile (`graphics.profile`, new default patch `graphics`)

* **How.** The game resolves its GPU profile natively, once per process: `GameOptionPerformanceProfileComponent.OnCreate`
  hands the whole `GameOptions_android.json` to `GameOptionManager.InitializeGameOption`. Patch `graphics` (Gameloft.Engine.dll,
  IL fingerprint of `OnCreate` checked) passes that text through `McvsOffline.GraphicsHooks.GameOptionsJson` and reports
  `m_options` after `ApplyGameOptions` to `GraphicsHooks.GameOptionsApplied`; a missing or failing server DLL leaves the
  text as read. `hosts/embedded/Graphics/GameOptionsProfile.cs` rewrites it for `mid`/`high`/`ultra`: the GPU `selection`
  becomes the single rule `[{"<profile>": [[["CORES>1"]]]}]` (Gameloft's own catch-all condition), the chosen block's
  `priority` entry becomes `["", ""]` (no step up or down, as already for High_Ultra/VeryLowGPU), and every option value
  stays Gameloft's. `auto` (the default) passes the text through untouched. Settings page: group **Graphics**, apply
  "After a restart" (the native options are initialised once per process; nothing re-applies them live).
* **What each choice changes**, against the MidGPU profile the S22 gets today (the effective GPU options; checked on the
  real file by `GraphicsProfileTests`, which also prints this list):
  * `mid`: nothing on the S22 (MidGPU is its catch-all profile); on phones that Gameloft rated High or Low it moves them to MidGPU.
  * `high` (HighGPU): `addShaderDefine` MID_END → HIGH_END (normal mapping, full lightmap), `lowLightMap` → false (the
    `_N.tga` + `.dirirr.tga` lightmaps instead of `.lowend`), `enableLowResMaps` → false (`HighEndOnly` layers and the
    full `.StaticGeo` instead of the `_lowend` ones), `enableShadowMap`, `enableBloom`, `disableUseSampler2DShadow` false,
    `disableColorTablePostProcess` false, `renderTargetPixelFormat` R8G8B8A8_SRGB → B10F_G11F_R11F,
    `dynamicRenderTargetSizeScale` → true (scene scale from the screen's DPI instead of 0.6), `anisotropicFiltering` 1 → 2,
    `lowHudEffects` → false, `FontScale` 1.0 → 1.5, `GlyphTextureCacheSize` 1024 → 2048.
  * `ultra` (High_Ultra): the same with GOD_END shaders and `anisotropicFiltering` 3, plus `lowResAssets`, `lowCubeMap`,
    `lowEndSpecular`, `pruneSkeleton`, `exposureSkipFrame`, `AnimationCullerLowEndDevice` → false and
    `skipLodByPresetName` "LODPreset_Characters 1".
  * Never changed by any choice (asserted): `enableRenderThread` true and `renderThreadMode` 3 (identical in every block
    the S22 can get), `deferredPhysics`, `multithreadedPhysics`/`multithreadedAnimations` (not in the GPU blocks),
    `ThreadPoolSize`, `PostProcessMutex`. The physics fixes of device test 2 stay as they are under every profile:
    server Worlds get single-threaded Havok from `GameHooks.PhysicsMultithreading` (the game's `multithreadedPhysics` is
    not in any GPU block), and the step gate serialises every World's Havok step.
* **Process-wide statics (pc-textures.md critic note 1).** The applied options are statics shared by the client and the
  in-process TS/GS Worlds. HighGPU and High_Ultra set `excludedLoadingLayers` "-" (MidGPU "Physics"; it feeds
  `World.GlobalExcludeLayers`, so the server Worlds would load the levels' Physics layer into their single-threaded Havok)
  and `reducedAiUpdateFrequency` false (`BT_Root.AiModulo` 4 → 2: bots think twice as often on the game thread). **By default
  `high` and `ultra` keep both at the MidGPU values** (`excludedLoadingLayers` "Physics", `reducedAiUpdateFrequency` true);
  `graphics.fullProfile` true applies Gameloft's blocks unchanged, for an A/B run. What stays process-wide even then:
  `enableLowResMaps` false makes every World, server Worlds included, load the `HighEndOnly` layers instead of the
  `LowEndOnly` ones, and `skipLodByPresetName`/`pruneSkeleton`/`AnimationCullerLowEndDevice` (ultra) change animation and
  LOD work for every World. A bot match at `high` and at `ultra` is therefore part of the device test below.
* **Log.** `[boot] graphics: graphics.profile high: GameOptions GPU profile forced to HighGPU (...)` when the text is read,
  then `[boot] graphics: effective GPU profile HighGPU (device optimum MidGPU), settings graphics.profile high (forced HighGPU);
  device GPU '...', model '...'; in-game graphic setting GameOptions_SelectedProfile 0; N options: a=..., b=...` with every resolved
  option (`GameOptionManager.GetLoadedProfileName("GPU")`/`GetOptimalProfileName`, settles pc-textures.md U1). The status
  line and Status › Game host › Graphics repeat the short form.
* **The game's own graphics setting** (Settings › Graphic setting, `GameOptions_SelectedProfile`, read natively, presumably as a step
  along the priority chain; it restarts the game) should not move a forced profile, since its priority entry has no step (the native stepping is not decompiled: check 4 below); with `auto` it
  works as before. The `[boot] graphics:` line shows its stored value.

### HD texture probe (pc-textures.md WP1): result

Device session of 2026-10-28 on the owner's S22 (SM-S901E, Adreno 730, build 10; the phone runs the **ETC** pack,
byte-identical to our copy, with the game's Graphic setting on "quality"):

* **Route B failed**: the zip mounted through `SaveStream.PushZip`, and the engine's managed lookup returned the zip's copy
  of the probe texture, but nothing changed on screen: the renderer does not read through that search path. The mount,
  `hd.mount` and the probe lines were removed again (build 11).
* **Route A works**: 13 GCBF-wrapped ETC1 `.f` files (same 256x256, 9 mips, recoloured) written into
  `files/Data/<h>/<rest>.f` gave green floors in a Port match, with `files/qaTestingConfigs.txt` = `SKIP_VALIDATION=1`
  (without it the installer deletes every `Data/` file whose size differs from its index at each start). Build 11 installs
  HD packs this way and keeps the installer's index in line instead ("Build 11 (plan)").

### Notification permission (target-sdk.md WP-T5, host side)

* On Android 13+ the host asks for `POST_NOTIFICATIONS` **once**, at the first main menu: when the build targets API 33+
  and declares the permission (the manifest change belongs to the `targetsdk` patch; at target 29 Android prompts by itself
  when the game creates its first notification channel, and the host does not ask), the permission is not granted, and
  `notifications.requestedAt` is empty. A thread polls (on the game thread) whether the client World plays its main-menu
  level (`World.CurrentLevelPath == World.Client.MainMenuScene`); after three polls in a row with the settings page closed it
  writes `notifications.requestedAt` into settings.json first, then calls `Activity.requestPermissions` on the game's
  activity with a 120 s watchdog lease. `MainActivity` has no `onRequestPermissionsResult`, so the answer is read back with
  `checkSelfPermission`. `notifications.request` false turns it off. JNI table group `notify` (resolved on API 33+ only),
  checked against android.jar 29 for the members and an API 33+ jar for `POST_NOTIFICATIONS`, `PERMISSION_GRANTED`,
  `GET_PERMISSIONS` and `TIRAMISU`.
* Status › Game host › Notifications shows the state ("allowed", "not allowed (asked once on ...)", "not needed on Android
  12", "this build targets API 29, so Android asks by itself ...").
* **Restart wording on Android 13+** (target-sdk.md §4.6 and critic note 3): by the AOSP source AlarmManager starts no
  activity for an alarm since Android 13, whatever the target, so the game would close and not come back. No phone has
  shown that on its own yet, and it conflicts with device test 8, which counts checks 1–13, item 9 ("does the game come
  back by itself") included, as passed as a whole. So the page's Restart texts (Actions, the restart dialog, the restart
  banner, its toast) and Status ("Restart via") say the game **may** only close and then must be started again; none
  says it will not come back (`host.relaunchUnconfirmed`, Android 13+). The game's own graphics-setting dialog uses the
  same relaunch and cannot be reworded by the host. **Question for the owner:** on the S22 (Android 16, target 29), does
  Actions › Restart bring the game back by itself, or does it only close? The answer decides between a firm "closes;
  start it again" and the relaunch fix of target-sdk.md §4.6.

### Checked (build 10)

The `scripts/build.sh` steps with `MCVS_REFS`, `MCVS_REQUIRE_EMBEDDED=1`, `MCVS_REQUIRE_REAL_CLIENT=1`,
`MCVS_REQUIRE_NODE=1` and `MCVS_REQUIRE_PC=1`: **McvsOffline.Tests 1345/1345, McvsOffline.Embed.Tests 415/415, McvsOffline.PcSpike.Tests 62/62, 0 skipped** (1,822;
after the build-10 review fixes). From the branches:
`ManifestTargetSdkTests`, `BinaryXmlGrowthTests`, `DexEditorTests` and `RealDexTargetSdkTests` (the 14 sites on the real
`classes.dex`: fingerprints, only the edited code units and the header change, the edited methods read as the report
says, P8's registers and intent, no reachable `PendingIntent` without a mutability flag, GA cannot start, and no
component the patched manifest declares reaches an unflagged `PendingIntent` site, by a conservative reachability from
the Application class and every component, which with build 9's components finds GA's `zzaoa.zzyk` from the three GA
components; d8 and dexdump accept the result), `ArtPatchTests`/`RealArtTests`/`ArscTableTests`/`PngTests`, `GraphicsProfileTests`,
`RealGraphicsPatchTests`, `HdTexturesTests`, `NotificationPermissionTests`, the JNI table against android.jar 29 and 34,
and `McvsOffline.PcSpike.Tests` against the real Steam files. The default full run of the patcher on the real APK now
checks the `classes.dex` bytes and its 14 notes (107 notes in all, 4 of them the GA removals; 111 with `--patch all`).
`McvsOffline.PcSpike.Tests` add a copy the running game still holds (redirect-undo keeps the config, says to quit the
game and run it again; `status` and `collect` name the leftovers on the console).

The real 1.17.32 APK patched once with the defaults (57,785,115 bytes, then deleted): `aapt2 dump badging` says
`sdkVersion:'21'`, `targetSdkVersion:'35'` and lists `POST_NOTIFICATIONS`; `xmltree` shows `exported=true` on
XamarinMainActivity, IGPFreemiumActivity and LocalPushReceiver, no Google Analytics component (only the inert
`globalConfigResource` meta-data), `allowNativeHeapPointerTagging=false`, `<queries>` with
the https and http schemes, and `fitsSystemWindows=true` on the root of both browser layouts; `dexdump` parses
`classes.dex` ("Checksum verified") and shows all 14 sites as edited; `apksigner verify` v1, v2 and v3 true;
`zipalign -c -p 4` OK; `resources.arsc` STORED, 4-byte aligned, byte for byte the shipped one. With `--target-sdk 29`:
`targetSdkVersion:'29'`, `classes.dex`, both layouts and `resources.arsc` byte for byte the shipped ones, the manifest
build 9's (rename and debloat only). With `--art stock`: every entry identical to the default output.

### What to check on the phone (build 10)

**Device test 9 (builds 10 to 12):** items 1, 2 and 6 confirmed by the owner's report as a whole; their logcat checks
were not run. Item 7 was done on 2026-10-28. The rest stays open ("Build 12 and device test 9", "What stays open").

Install build 10 **over** build 9 (same key; never uninstall: that deletes the saves). Keep
`adb logcat -s McvsOffline mono mono-stdout AndroidRuntime ActivityManager > b10.txt` running where you can.

1. **Install**: no Play Protect "built for an older version of Android" warning, neither in the install dialog nor in a
   Play Protect scan afterwards. The saves, settings and the launcher icon (still Gameloft's) are as before. Keep the
   logcat running for the first hour after the install, game open or not: a Google Analytics job or alarm build 9 left
   with Android runs then, and build 10 must drop it without a crash (no `AndroidRuntime: FATAL EXCEPTION` naming
   `com.google.android.gms.analytics` or `PendingIntent`).
   **Device test 9:** builds 10, 11 and 12 installed and played fine (reported as a whole; no logcat was sent, so the
   first-hour check is open).
2. **Target 35 basics**: boot to the menu; menus, a bot match and a Battle; the game saves (close it, start it again, the
   progress is there); Back in the game (pause menu, the quit dialog) and on the MCVS Offline page (Back leaves the page
   or asks first, as in build 9).
   **Device test 9:** builds 10 to 12 played fine at target 35 (reported as a whole).
3. **Page layout**: open the page with three-button navigation and again with gesture navigation (Settings › Display ›
   Navigation bar), portrait and landscape: nothing hidden under the navigation or status bar, every button reachable.
   Screenshots of both modes.
4. **Restarts**: Actions › Restart on the page, and the game's own Settings › Graphic setting change: the game saves and
   closes without a crash; does it come back by itself (one process, one boot) or only close? Report which, for both.
5. **Notification prompt**: at the first main menu Android asks once for notifications (not over the page or the age
   gate). "Allow": Status › Game host › Notifications says "allowed", the next start does not ask. If build 9 already had
   notifications allowed, there is no dialog and Status says "allowed".
6. **Graphics profiles** (page: Graphics › Graphics profile, each change followed by a restart). First `auto`: note the
   `[boot] graphics: effective GPU profile ...` line (which profile the S22 really gets). Then `mid` (on the S22 the same
   as auto), `high` (sharper surfaces from normal maps, shadows, bloom, full lightmaps) and `ultra` (also the richest
   shaders, high-resolution assets and cube-map reflections): each time the `[boot] graphics:` line says "forced
   MidGPU/HighGPU/High_Ultra", a bot match plays through, then 10 minutes of play for frame rate and heat; no `[watchdog]
   STALL`, no `physics step gate: ... without the gate`. Then `graphics.fullProfile` on at `ultra`: one bot match. With a
   forced profile, the game's own Graphic setting should not change the `[boot] graphics:` profile. Back to `auto` at the end.
   **Device test 9: the profiles work** (reported as a whole); no `[boot] graphics:` lines, `STALL` check, per-profile
   frame rates or `fullProfile` run were sent.
7. **HD texture probe**: done on 2026-10-28; route B failed, route A works ("HD texture probe: result" above).
8. **Reminders**: with notifications allowed, leave the game so it schedules its local reminders and wait for one: it
   arrives. Tapping it only dismisses it on Android 12+ (expected; open the game from the launcher). After a reboot
   reminders still come. A reminder that build 9 scheduled may arrive once more after the update (expected).
9. **Share sheet**: Logs › Build › Share and Saves' "Share as a file" open Android's share sheet and another app receives
   the file; "Share a text report" and Save to Downloads still work.
10. **logcat**: during all of the above,
    `adb logcat | grep -E "FLAG_IMMUTABLE|RECEIVER_EXPORTED|VerifyError|Background activity launch blocked"` stays
    empty (no `IllegalArgumentException` from a PendingIntent, no `SecurityException` from a receiver, no class rejected
    by ART); a "Background activity launch blocked" line right after a Restart (item 4) is the expected Android 13+
    relaunch block, note when it appeared. Send `b10.txt`.
11. **Data-download notification** (P8, the largest dex edit: it rebuilds the notification's intent). Shown only while
    the installer downloads data, which the S22 no longer does, and a fresh download there would need its data cleared,
    which deletes the saves: **never clear the S22's data for it.** Only with a second Android 12+ phone: install build 10
    there, start it with the internet on, and once the installer downloads (if Gameloft's servers still answer), press
    Home: the progress notification appears; tap it: the installer comes back in the same task (Back from it does not
    show a second game screen); let it finish in the background: the "done" notification, tapped, opens the game.
    Without a second phone: skipped for build 10 (item 10's grep still catches a crash should the installer download).
12. **LAN mode**: out of scope for build 10 unless a second device runs build 10 (LAN is experimental, M3, and the
    target change touches no socket code; cleartext to 127.0.0.1 and the LAN host is unchanged from target 29). With
    one: host on the S22, join from the other, one bot match.
13. If anything in 1–12 regresses: patch again with `--target-sdk 29` (build 9's manifest, layouts and `classes.dex`;
    it installs over build 10 with the same key) and report the item.

## Device test 8 (October 2026): the MCVS Offline page works

The owner installed build 9 (`feat/economy` `6bcb3d8`, both features) and reports that everything works ("it works
wonderfully"). No logs and no per-check results were sent, so checks 1–13 below count as passed as a whole; the
Cosmetic crate (checks 14–28) was not exercised, since its first crates come on 2026-11-09.

## Build 9 (October 2026): the MCVS Offline page and the Cosmetic crate

The settings and diagnostics page of [design/settings-page.md](design/settings-page.md), in the game's own browser
(player guide: [PLAYER-SETUP.md](PLAYER-SETUP.md) section 7). `McvsVersion.Build` is `9`.

* **Screens**: Status (versions, the player and the game's last save, host state, recent problems, storage use);
  Settings (an allowlist of `settings.json` keys rendered from the server's schema, each marked Now / After the next
  match / After a restart, presets, a backup before risky changes, a repair panel for a broken file); Saves (backups in
  four pools, export as a `.txt` file or as `MCVS-SAVE:1` transfer text in parts of 60,000/3,900/1,900 characters,
  import by paste or from `McvsOffline/import/`, staged and applied at the next start before the port opens, then on
  probation with an automatic revert, Undo and "Play this account on this phone"); Logs (filtered tails, a deflated zip
  ≤ 8 MiB built first and then shared, a text report); Actions (Save the game now, Restart through the game's own
  restart path after a save, where the files are) and About. One self-contained ES5 page for 250–330 CSS px of height.
* **Opening it**: new default patches `adminpage` (Settings › Account › MCVS Offline) and `adminlink` (the button of
  the new "MCVS Offline" inbox message, a CRM `newsfeeds` item with a fixed link), the redeem code `MCVSOFFLINE`, and
  the optional `forum`.
* **Security**: one-time tickets (120 s) only in the URL fragment handed to the game's WebView, an `HttpOnly;
  SameSite=Strict` session cookie, a Host check on **every** route while bound to loopback (DNS rebinding;
  `security.hostCheck`), `X-MCVS: 1` on every `_mcvs` write (`curl -X POST -H 'X-MCVS: 1' ...`), a 1 KiB body limit
  at header time without a session, one log redaction for every logger. Nothing secret is ever written to a file. The
  redeem code and the page's "current player" accept only the game's own requests (no browser header, its exact
  User-Agent for a client id of its form; the player's login must be a POST with a device id), and the code is off in
  LAN mode.
* **Host** (`hosts/embedded/Admin`, `Android`): the share sheet through MediaStore Downloads on Android 10+ (`file://`
  before), clipboard, toast, the game's own save (`ForceSaveProfile`) and restart (`MainActivity.a(true)`, host
  fallback), the watchdog paused while the page is open, one JNI table checked against `android.jar` and the APK's dex.
  The public log mirror now writes temp files, renames them into place and asks for a media scan (the 0-byte uploads).
* **Checked**: `scripts/build.sh` with `MCVS_REFS`, `MCVS_REQUIRE_EMBEDDED=1`, `MCVS_REQUIRE_REAL_CLIENT=1` and
  `MCVS_REQUIRE_NODE=1`: **McvsOffline.Tests 1075/1075, McvsOffline.Embed.Tests 272/272, 0 skipped**. Among them: the page in node against its fixtures and against a live
  server (every screen, a settings change with the Back guard and its session history, an export pasted back, re-creation, view mode, ES5 parse
  with acorn); end-to-end tests over a socket (ticket → cookie → settings → CRM, the Host check, the early 413, an import
  from another phone applied at the next start and confirmed by the game's save); the three hook patches on the real
  1.17.32 assemblies (fingerprints, IL, executed against recording hooks with a differential, JIT, ILVerify) and
  against the real embedded DLL; the target SDK after patching (invariant I1' in ARCHITECTURE.md §2, which replaced
  "`targetSdkVersion` 29 after every default patch": 35 by default with the `classes.dex` edits since build 10,
  `--target-sdk 29` as the fallback).
* **The Cosmetic crate** ([ECONOMY.md](ECONOMY.md) §5, [design/economy-collector.md](design/economy-collector.md)):
  the unused `Crate_Event_Special`, named "CUSTOMIZATION CRATE", filled by CRM cells (39 rows: avatars, backgrounds,
  sounds on background carriers, the 16 skins, 12 hidden jerseys, Witch and Bli-P, camos, jerseys; 70 VersusCoins
  once everything is owned), weighted per player from the saved profile. From `collector.since` 2026-11-09: one crate
  with the day's first mission, 1/1/2 on each weekly objective's tiers (16 a week), 1–4 × N items for ranks 4–30 at the
  season end, a season emblem for ranks 1–3; one crate per milestone. Settings page: "Cosmetic crate" and "Items per
  Cosmetic crate" (default 5); presets leave them alone. A 5-cell guard from a day before `since`, latched for good in
  `<data>/collector-guard.json` once crates were handed out; an unreadable kill switch (or settings.json) counts as off,
  and the page shows the switch as the server reads it. Requires the
  `store` patch (the embedded host reports it). Status screen and `GET /_mcvs/crm` show the phase; `GET
  /_mcvs/collector?account=` what was read from the save.
* **Checked (both features, `6bcb3d8` plus the loose ends and the build-9 review)**: the full check as above: **McvsOffline.Tests 1345/1345,
  McvsOffline.Embed.Tests 283/283, 0 skipped**, content-gen `--check` and its 28 unit tests. For the crate: every
  output of 16 days of golden scenarios byte-identical to `ac64bfd` before the guard starts and equal after removing
  the 5 guard cells; the cells, the per-player document and the events through the real client (`StandardCrm.Data`,
  `ApplyCRMTableChanges`, `CrateRewards`, the patched `Game.dll`'s reveal paths, the claim of a milestone, the weighted
  draw, a day-first culture); the TLE screen cost on net8.

### What to check on the phone (build 9)

1–13: the page, done in device test 8 (details: design §12, C1–C20). 14–28: the Cosmetic crate (design §7), on or after
2026-11-09. Before them: from 2026-11-08 `server.log` says `CRM document built: … collector guard (before
collector.since …)`, from 2026-11-09 `collector active (510 cells, …)`; Status shows "Cosmetic crate: active".

1. The "MCVS Offline" inbox message appears; its button opens the page (C1, C2).
2. Settings › Account › MCVS Offline opens it; Status › player › source is `game` (C6).
3. Redeem code `MCVSOFFLINE`: the page opens over "code does not exist"; a second try within 30 s is ignored (C7).
4. The session survives the browser's Refresh and a dark-mode switch; then `admin.openVia` `jni` and `post` (C3, C18).
5. No watchdog STALL or thread dump while the page stays open 2 minutes; time back to a responsive menu (C5).
6. Settings: VersusCoins ×2 pays double after the next match; `devMenu` makes a `presettings` backup; a broken
   `settings.json` shows the repair panel (C9).
7. Copy: which clipboard path works; a 60,000-character part pastes intact (C10).
8. Saves: Save the game now; back up and delete; export to Gmail, Telegram, Discord, Drive; the transfer size of an
   unlock-all profile; import on a second phone (switch), probation clears after reaching the menu, Undo; `adb push`
   into `import/`; can Samsung My Files write `import/`? (C11)
9. Restart from Actions: does the game come back by itself, one process and one boot? Then `admin.restartVia=host` (C12).
10. Logs: the zip's size, arrives non-empty in the apps; Downloads › MCVS-Offline sizes right, `is_pending=0` (C13).
11. Chrome: `/_mcvs/status` loads, `/_mcvs/ui/` is view-only (C14). LAN mode: `/_mcvs/*` from another device is 404,
    the redeem code is ignored (C16).
12. Regression: boot, matches, shop, signatures, saves, codes as in build 8, and no `gate: refused Host` in `server.log` (C17).
13. Layout at 130 % and 200 % font size and with the keyboard open; hardware Back closes the browser when nothing is
    unsaved, also right after a dialog or a Save; Status › host › `missingJni` empty (C18, C19).
14. Fresh profile, right after login and after a match: the daily missions and the weekly event show "CUSTOMIZATION
    CRATE", its image, "CONTAINS 5 ITEM(S)" and a preview (a random emblem, a random background, an agent; no
    VersusCoins). The weekly final keeps its own event crate name. Log: `CRM: collector for <account>: base … left`.
15. Time opening the weekly event screen: collector off, fresh profile, ~1,400 avatars owned, complete profile (target
    ≤ +0.5 s); and the daily missions screen.
16. Claim the daily mission whose card shows the CUSTOMIZATION CRATE (the server's first pick: usually the row right
    under the Contracts bonus, until another mission completes): the lottery shows the crate's items, not the
    mission's VersusCoins/Credits (the balance still rises). Claim the daily bonus: the Contracts crate's own reveal, as before.
17. Sounds: `economy.collector.weights` all 0 but `sounds`, fresh profile: every item is a random background plus a
    sound; with 5 draws over 4 sounds at least one sound repeats and shows a gear piece instead (owned sound,
    `AlternateRewards`), as later crates do until the next profile read; once all 4 are owned and read, items pay 70
    VersusCoins. No exception in the game log.
18. Weekly tier 1 or 2: 5 items animate; tier 3: 10 items (11–12 with a sound: plain list); the final: the event crate
    as before.
19. `itemsPerCrate` 10: tier 3 (20 items) and a Master rank-4 season crate (40) show the plain list, every item listed;
    time them.
20. 50 avatar draws: concrete emblems, none Kayac (382–384, 443–531, 533–589, 598–1514), no starred season emblem.
21. A hidden jersey from a crate is in that agent's jersey list and can be equipped; Witch or Bli-P from a crate:
    unlocked with the unlock flow, then their skins appear in the preview.
22. Season end (`liveops.seasonStart` to the next hour): ranks 4–30 get "DIVISION REWARD" with a CUSTOMIZATION CRATE of
    band × 5 items; ranks 1–3 HardCurrency, gear and a ★★★/★★/★ emblem not owned before.
23. Unlock-all + `UNLOCKPAID`: every item pays 70 VersusCoins; no milestone shows "unavailable".
24. "Connecting to the server" after a match, collector on vs off, fresh and unlock-all (target ≤ +0.3 s); match start
    on vs off (≤ +0.1 s); one `CRM: collector for …` line per return.
25. Switch "Cosmetic crate" off after 2026-11-09 (the page asks first: the running events restart): the running events
    are replaced once and carry no crate, ranks 4–30 get their gear piece; log `collector guard
    (economy.collector.enabled is false)`. With `"collector": false` written by hand the page shows the switch off, and
    saving "Items per Cosmetic crate" keeps it off.
26. Stale crate: complete the crate's daily mission without claiming, switch the crate off, back to the menu; if the old
    mission is still there, claim it: 70 VersusCoins, no exception. `collector-guard.json` exists, `GET /_mcvs/crm`
    shows `cratesServed`; log `Cosmetic crates served for the first time`.
27. Phone language with a decimal comma (e.g. Deutsch): a crate still opens exactly 5 items.
28. `GET /_mcvs/collector?account=<id>` after a few crates: `left` falls, every part `known`, the agents match the
    game's list. On 5 logins claim a crate before the first match: no VersusCoins while items are left.

## Build 8 (October 2026): 'debloat', dead SDK pieces removed

New default patch id `debloat` (ARCHITECTURE.md §2 lists every item). Only what the APK research rated high-confidence and
free of any feature change; everything with a feature cost or a medium rating was left as it is (boot-completed reminders,
VIBRATE, BILLING, Google Analytics, FacebookActivity, the Play Games/Facebook app ids, the ad guard, dex code, Bind*.dll,
btls).

* **Manifest**: 113 → 46 elements (44 targets and their 23 intent-filter children). `BinaryXml` gained a checked element
  tree and `Remove`; the string pool, resource map and every kept chunk stay byte-identical. Refused (nothing written)
  unless every target matches exactly once in the verified 1.17.32 manifest.
* **Files**: the 9 Crashlytics/Fabric entries, again only if all are the verified ones.
* **Gameloft.Engine**: Crashlytics can never start; no push-token request (local reminders unchanged, the notification
  toggle still purges and re-schedules them); Facebook and Play Games are not initialised at menu start, so no
  GoogleApiClient connect, no possible Google sign-in prompt and no Facebook auto-login; a tap on their buttons in
  Settings > Account shows "Connecting…" and then the normal login-failed state.
* **Checked**: aapt2 `xmltree` (deletions only) and `badging` (package, launcher, exactly the kept permissions), apksigner
  v1/v2/v3 with `--min-sdk-version 21`, an independent AXML rebuild byte-identical to the patcher's, no reference to a
  removed component in the dex, the native libraries or the managed code, update install over build 7 (only removals,
  package-scoped permission). Gameloft.Engine: 9 patched + 3 added methods ILVerify clean and JIT, every other of 21,381
  method bodies unchanged, execution tests with the real assemblies and a native-call recorder. Two review findings (the
  "Connecting…" label after a login tap, the local-alarm purge in push registration) were fixed and re-verified.

### Also check in device test 7 (with build 8 installed)

1. Install over build 6 or 7: the update installs and the save is kept.
2. No Google sign-in prompt at boot; Settings > Account: Facebook and Google buttons show the failed-login state after a tap.
3. Local reminders still arrive (and after a reboot); toggling notifications off and on in settings works.
4. Shop, matches, saves and the signature screen as in build 7.

## Device test 6 (October 2026): fast return to the menu; the signature-screen freeze caught in a dump

Leaving the firing range is fast with build 6. The kill-signature (avatar) screen still froze after unlock-all; logcat
`25_10-20-06-49`: watchdog STALLs at 5 s and 15 s with the game thread (tid 27705) in state R, then the Mono managed dump
(the `logs.zip` upload was 0 bytes again; the logcat carries the dump):

```
string.op_Equality <- InternalStringComparer.IndexOf <- List<string>.Contains
  <- PlayerProfileComponent.GetSignatureAssetsList <- GetSignatureAvatarsList
  <- GameOfflineStore.GetKillSignItems <- GetSignatureList <- NativeGetSignatureList <- MenuComponent.OnInvokeDelegate (UI)
```

The build-5 analysis below looked only at `NativeGetSignatureLibrary` (one call) and wrongly put the cost in the Flash
VM. The screen also calls `GetSignatureList`, whose `GetKillSignItems` looks up each owned avatar's image with
`profile.GetSignatureAvatarsList().FirstOrDefault(...)`. Every one of those 1,512 calls rebuilds the 1,518-row list and
sets each row's `locked`/`isNew` with `List.Contains` on the owned and new lists (1,512 each): about 1,512 × 1,518 ×
3,024 ≈ 7 billion string compares. Measured on net8 with the real Game.dll: 26.8–33.8 s for one `GetKillSignItems("avatar")`
(the phone's Mono JIT is slower still). The same list builder is behind the 18 s and 27.6 s stalls of the unlock-all reward
popups in device test 5 (two builds per kill-signature reward, `_NotifyBundleReceived`).

## Build 7 (October 2026): the 'signatures' patch

New default patch `signatures` (Game.dll, `patcher/Patches/SignaturesPatch.cs`), both methods checked against their
1.17.32 IL fingerprint first:

* **G1** `PlayerProfileComponent.GetSignatureAssetsList`: an owned or new list of more than 16 ids is copied once per call
  into a `HashSet<string>` (the same default comparer as `List.Contains`) and read through `ICollection<string>.Contains`;
  shorter lists are used as they are. Every caller gains: the shop's availability check per kill-signature item,
  `GetSignatureObject` (matchmaking, HUD, leaderboards, the match-over sync of every player's card: bots own all avatars),
  level-up and reward popups. One call at unlock-all scale: 21–41 ms → 0.34–2 ms on net8.
* **G2** `GameOfflineStore.GetKillSignItems`: the avatar and background lists are built once per call and reused for every
  owned item (once per owned item before). The signature screen at unlock-all: 26.8–33.8 s → 49–104 ms on net8 (×380 or
  more); a half-way profile (759 owned, 759 for sale): ~14 s → 0.3 s.
* **Checked**: a differential test runs the original and the patched real Game.dll side by side on net8 (fake database
  and profile, real `OfflineStoreData`): ~60 cases of `GetSignatureAssetsList` and 56 of `GetKillSignItems` (null and empty
  lists, the 16/17 threshold, duplicates, unknown and null ids, case variants, missing tables, unlock-all) give identical
  rows, flags, dictionaries and exceptions (`RealSignaturesPatchTests`; `MCVS_SLOW_TESTS=1` also runs the original screen
  at full unlock-all scale). ILVerify against the game's BCL passes, both methods JIT, and a second patch run refuses.
  Three independent reviews (IL, behavioural equivalence, remaining cost) found no defect.
* **Left as it is** (minor, measured): for a player who owns about half the avatars, the shop part of the screen still
  builds the list once per avatar for sale (`GetKillSignItemObject`), ~0.3 s on net8, ~0.5–0.9 s on a phone; reward popups
  after unlock-all cost ~0.25 s per batch of 400 on net8 (keep `unlocks.batchSize` ≤ 400). A follow-up patch (one list per
  screen refresh for the shop part too) is noted in ROADMAP.md if device test 7 shows a hitch.

### What to check in device test 7

1. **Signature screen** after unlock-all: it opens within about a second; owned avatars, backgrounds and sounds show and
   can be equipped; with a profile that is not fully unlocked, the unowned ones can still be bought.
2. **Match results / return to the menu**: less stutter than before (G1 also covers every player card's signature).
3. A new unlock-all grant (fresh profile): the reward popups no longer stall for 18–27 s.
4. Any `[watchdog] STALL`: send the logcat (the managed dump is in it).

## Device test 5 (October 2026): live ops and the data restore work; firing-range exit lag

Live ops (seasons, daily missions, weekly events) and the install from a copied `Data/` folder worked. Leaving the
firing range sat on "please wait, connecting to the server" for ~11 s (19:43:18.6 → 19:43:29.5 in the logcat), all of it
on the game thread (tid 25142 runs the client, TS and GS Worlds); the server answered every request in 0-4 ms (92 ms for
the save). The log files arrived as 0-byte uploads again; the logcat was enough.

What the TS does on every return to the menu, after a match as well (`GameServerComponent.ResumeServer`, the client sends
it from `GameplayComponent._ExitGameplay`; the firing range only differs by `IsPractice`):

| Step | Request | Game-thread time |
|---|---|---|
| `ReloadProfileAsync`: load and apply the game-profile dump | #61 GET `_mcvs.gameProfile`, 557,204 B | 3.4 s |
| its forced CRM refresh (`RefreshCrmConfig(forceRefresh: true)`): parse twice, apply (`RefreshDatabases` reloads `MCVS_Meta`/`MCVS_System` from disk because the document carries `_OverrideTables`), sync the 400 KB text to the client, which parses it again, plus the native engine | #62 `configs/users/me`, 402,801 B; then #63 events | ~4.4 s |
| `SaveProfile` (`MakeSureProfileIsUnlocked` first) | #64 POST dump, #65 merge | 0.5 s |
| `DoSaveProfile`'s own CRM refresh: parsed twice, then found equal (`StandardCrm.Data.Equals`) | #67 `configs/users/me`, 402,801 B | 1.2 s |
| client: `StartSynchronization`, shop and UI refresh | | ~1 s |

99% of the CRM document (399 KB) is the generated `offline_store`, and 1,512 of its 1,858 entries are kill-signature
avatars (35 more are backgrounds). The `[watchdog] STALL` at +119 s was the game in the background (`PlayerBase::pause`
4 s earlier), not a hang.

## Build 6 (October 2026): a smaller CRM document

* **Owned kill signatures leave the player's `offline_store`** (`server/Services/Crm/CrmOwnedStoreItems.cs`). The
  signature screen lists owned avatars and backgrounds from the profile and only the others from the store
  (`GameOfflineStore.GetKillSignItems`), so their entries were never shown. For an unlock-all profile the document drops
  from 402,636 to 89,282 characters (1,858 → 311 entries); a new player still gets everything. Sounds stay listed: the
  screen takes their owned list from the backgrounds (a game bug), so the store is where they show up.
* **When the owned set changes**: only when the game-profile dump is read (the TS does so at login and in each
  `ReloadProfileAsync`; the storage module publishes `storage.gameprofile.read`). The fetch right after that read is the
  forced refresh, which applies the document anyway; the one after each save gets the same text, which the TS recognises
  without re-applying it. A newly bought avatar leaves the store at the next return to the menu (until then the screen
  already hides it as owned).
* **Fail-safe**: a dump in an unexpected shape (a list that appears twice, or holds anything but ids) logs
  `CRM: owned kill signatures of ... not read (...)` and serves the whole store, as before. `crm.omitOwnedSignatures:
  false` in settings.json turns the filter off.
* Not changed (game code): the 557 KB profile reload, the two CRM fetches per return and the database reload of the
  forced refresh. Those would need IL patches to the game's resume path; see [ROADMAP.md](ROADMAP.md).

### What to check in device test 6

1. **Firing range**: leave it and time "connecting to the server" (build 5: ~11 s). The server log line
   `CRM: offline_store for <account> leaves out N owned kill signature(s) (... avatars, ... backgrounds owned): 402... -> 89... chars`
   appears once per return to the menu, and `[http] #.. GET /configs/users/me ... -> 200 [crm.config]` shows ~89,000 B.
2. **After a match**: the results screen and the return to the menu stutter less.
3. **Signature screen** (avatars, backgrounds, sounds): owned ones are listed and equippable, the rest can still be
   bought; buy one and check it stays owned after the next match.
4. A `CRM: owned kill signatures of ... not read` warning means the dump has a shape the scanner does not expect: send
   the logcat.

## Build 5 (October 2026): offline data install, 64-bit only, live ops

`main-dev` after merging `feat/install` and `feat/liveops`; APK `mcvs-offline-<commit>.apk`, same signing key as builds
1-4, so it installs over build 4 as an update and keeps the data and the save.

* **Data already on the phone is accepted without Gameloft's CDN** ([INSTALL-OFFLINE.md](INSTALL-OFFLINE.md)). Why a
  copied `Data/` stalled: the Java installer starts offline only with its index `files/pack.info` in *internal* storage,
  `prefs/gl_ver` = 1173200 and every 2 MiB piece listed in its download journal. A copied `Data/` has none of that, so
  the installer went to the CDN for the index and every split file, and the CDN's rate limit (HTTP 429) most likely gave
  the 0 MB / 0 % screen (not proven: no log of that run exists). Now, in Bootstrap before `GameInstaller` runs: the
  installer's index is saved to `files/McvsOffline/install/pack.info` every start (a backup of `files/` carries it); a
  missing or stub index is restored from that copy or a mirror's, with `gl_ver`, the journal and the installer's
  preferences, then CRC-checked in the background once the installer has handed over to the game (resumed at later starts
  until done; files still damaged at the next start are deleted and downloaded again); a
  `Data/` that is exactly the complete 1.17.32 pack (15,747 files, size per texture format) gets a rebuilt, size-only
  index; `InsTime` is refreshed so the weekly CDN version check never runs; `[installer]` lines log the installer's
  states. No `classes.dex` change; the installer's own checks still run. Settings `install.*`.
* **ABIs**: the patcher keeps every ABI by default (32-bit `lib/armeabi-v7a` restored after build 5); `--abi arm64-v8a`
  drops the 67 `lib/armeabi-v7a` entries (APK 57 MB -> 44 MB).
* **Live ops** ([LIVEOPS.md](LIVEOPS.md)), computed from the clock and `settings.json` (`liveops.*`), nothing stored by
  the server: a division season every Monday 00:00 UTC (17 division events, a league group of 100 with rivals that grow
  during the week and freeze at the end; the TS itself promotes, demotes and puts the season reward in the inbox), a daily
  mission event (3 of 6 objectives, a loot crate for all three), a weekly event (theme objective + 3, cosmetic crate
  rotating weekly) and a weekly modifier (standard, Coin rush, Trophy fever, Credit rush, MVP bounty, Rival surge) applied
  as CRM table overrides. The CRM `expiry` makes the running game refetch at each day and season boundary. Event and
  expiry dates are sent as `yyyy-MM-dd HH:mm:ssZ`: with the ISO `T` a day-first phone locale swaps day and month.
  `GET http://127.0.0.1:51780/_mcvs/liveops` shows the schedule.
* **Integration fixes**: event dates from `social/events.json` are now sent in the same `T`-less form; ILVerify against
  the game's mscorlib found two `InitOnly` uses in the live-ops schedule (fixed, now clean); `HttpServer.Stop` answered a
  connection accepted at the same moment with "503 server busy" instead of closing it (the cause of an intermittent
  `HttpServerTests` failure).

**How it was checked.** `scripts/build.sh` with `MCVS_REQUIRE_EMBEDDED=1 MCVS_REQUIRE_REAL_CLIENT=1`: McvsOffline.Tests
508/508 and McvsOffline.Embed.Tests 143/143, among them 19 installer-data tests (real ETC pack, both live indexes), the
real-APK patcher tests for arm64-only, and the live-ops conformance tests (16 days of CRM documents and events through
the real client's parsers and every event type's `Initialize`, under a day-first culture). ILVerify of the embedded DLL
against the game's BCL: no findings. A clock simulation of the composed server over loopback HTTP with the real ETC data
pack (2026-10-19 to 2026-11-04 in 3-hour steps): 3 seasons, 16 days, 4,680 events parsed, the CRM expiry always ahead
of the clock and never past the next boundary, the next season listed on the last day; three players posting division-5
trophies finished at rank 28, 61 and 93 of 100 (promotion zone 1-30, demotion 91-100), i.e. promoted, kept and demoted, in
both seasons, with the same rank on every later read (9 days) and the rival scores frozen. APK: `zipalign -c -p 4` OK,
`apksigner` v1+v2+v3 with certificate SHA-256 `fc0db84e...7d16`, `lib/` only `arm64-v8a`; the patched game assemblies
differ from build 4's only in their 16-byte module id (no new IL patch in this build).

### Review fixes to build 5

APK `mcvs-offline-<commit>.apk` of the review-fix commit, same key; it replaces build 5's APK for device test 5.

| Finding | Fix | Tests |
|---|---|---|
| After a *partial* restore (saved index, some files missing), the background CRC check started after a fixed 90 s, while the installer was still downloading: every missing or half-written file went into `damaged.txt`, and the next start deleted the files the installer had finished in the meantime, without looking at them again (offline, the game could then not start) | The check waits until the installer has handed over to the game (`GameInstaller.sbStarted` or state 21, read over JNI), then one more minute; only if that cannot be read *and* every file passed the size test does it fall back to the fixed wait. Missing files and files of another size are never listed (the installer's own size test fetches them). The next start reads each listed file again against the installer's index (or the saved copy) and deletes only those that still fail; a file the installer finished or the player replaced is kept, one no index describes is left alone; at most 256 MiB are re-read at start-up, the rest waits for the next start | `InstallerDataTests`: `APartialRestoreWaitsForTheInstallerAndAFileItFinishedIsNotDeleted`, `TheDamagedListIsReadAgainAndOnlyFilesThatStillFailAreDeleted` |
| The CRC check ran only in the session that restored the index: if that session ended first (quit, crash, OOM), a zero-filled split piece of the right size, already in the journal from its size, was accepted for good | `Install()` writes `McvsOffline/install/verify-pending` (index hash and position); every start with it present schedules the check again ("CRC check pending" in the `[install]` line); the check saves its position every 64 MiB and resumes there for the same index (from the start for another); the marker goes once every entry has been read | `ACheckThatDidNotGetThroughIsScheduledAgainAndResumes` |
| With `liveops.seasonDays` 1 (documented as valid) no season object resolved: the key of a one-day window has no `x<days>` suffix, but season ids were parsed as multi-day only, so the CRM listed ids for which `/events` had no definition and the division boards fell back to the static rivals | `LiveOpsWindow.Parse` reads a bare `yyyyMMddHH` key of a season-type id as one day, and accepts only the exact form `Key` writes (`x1`, `x07` refused), so a rebuilt id equals the requested one | `LiveOpsTests.EverySeasonLengthGivesIdsThatParseBack` (1, 2, 3, 7, 28 days), `EveryListedEventIsServedParserSafeAndDeterministic` (7, 3, 1 days), the real-client conformance loop also with 1-day seasons (289 division events, 16 weekly events initialized) |

Checked: `scripts/build.sh` with `MCVS_REQUIRE_EMBEDDED=1 MCVS_REQUIRE_REAL_CLIENT=1`: McvsOffline.Tests 516/516,
McvsOffline.Embed.Tests 146/146; ILVerify of the embedded DLL against the game's BCL: no findings; APK `zipalign -c -p 4`,
`apksigner` v1+v2+v3 with certificate SHA-256 `fc0db84e...7d16`, `lib/` only `arm64-v8a`.

### What to check in device test 5

1. **Update install** over build 4: the save is kept; `[install] installer data: ...` in the bootstrap log says the
   installer's index was saved; the installer screen passes as before. Note the `[installer] ... finished after N s`
   time (about 16 s in device test 4).
2. **First season start.** The first CRM fetch lists the season events; the game starts its first season *without* a
   season-end evaluation, and the game itself resets the division trophies collected before (`Data` was never set
   because no events existed). From then on the Divisions screen shows a group of 100 and the season end on Monday
   00:00 UTC in local time. This week's modifier (season 2026-10-19) is Trophy fever; the season from 2026-10-26 has
   Credit rush. Check the dates read right on the phone's locale.
3. **Season end** (Monday 00:00 UTC, or set `liveops.seasonDays`/`seasonStart` to see it sooner): the season-end screen,
   promotion or demotion by one division, the "DIVISION REWARD" inbox message; restarting the game afterwards must not
   give it again.
4. **Daily missions and the weekly event** show their objectives, count matches and pay out; at the reset hour the day
   rolls over while the game is open (CRM refetch, no `[watchdog] STALL`).
5. **Copied `Data/` folder** (optional, the device-4 stall): with a backup of `files/` or only `Data/`
   ([INSTALL-OFFLINE.md](INSTALL-OFFLINE.md)): uninstall, install build 5, copy, start in airplane mode; the
   `[install]` line says `index restored ...` or `index rebuilt ...` and the game starts without the CDN. Measure the
   first start (the size check of 15,747 files runs on the UI thread in `OnCreate`; with a rebuilt index on every start).
   About a minute after `installer finished after N s`, `installer data: checking the CRC32 of every file` and later
   `background check done` (or the damaged files) appear; closed before that, the next start's `[install]` line ends in
   `CRC check pending` and the check resumes.
6. **Avatar editor** after unlock-all: a thread dump of the freeze, sent from Downloads > MCVS-Offline (see "Device test 4").

## Device test 4 (October 2026): shop and codes work; installer stall and avatar editor

Same phone, build 4. Boot, bot-filled matches, saving, the shop and the unlock codes work. Two problems remained: after
reinstalling and copying the `Data/` folder back, the installer screen stalled at 0 MB / 0 % (addressed in build 5,
above), and the kill-signature avatar editor froze after unlock-all (below). Logcat `25_10-12-30-37` is a normal boot of
this build: 56 requests, all fast; the main menu at about +60 s; the installer screen took about 16 s.

### Open: the kill-signature avatar editor freezes after unlock-all (solved in build 7, see "Device test 6")

Logcat `25_10-12-30-37`, second watchdog STALL (`1792945929.086`): opening the avatar (emblem) editor with every avatar
owned, the game thread (`Thread-22`, tid 31784) is in state R with `+2542 ticks` (25 s of CPU time since the watchdog's
previous snapshot), and no server request is in flight. The managed thread dump of that stall (`logs/threaddump-20261025-163218.txt`)
arrived empty (0 bytes), so the frames on the game thread are not known yet.

What the code does (Game.dll, Gameloft.Engine.dll, `Menus/UI.swf`):

- The editor is ActionScript (`MenuCustomization` in `Menus/UI.swf`), run by the engine's native Flash VM on the game
  thread. It calls the native function `GetSignatureLibrary` once, which is
  `PlayerProfileComponent.NativeGetSignatureLibrary`: three `GetAssetsObject` calls (avatars, backgrounds, sounds), each
  `GetSignatureAssetsList` over the **whole** table (`KillsignatureAvatars` has 1,518 rows) with
  `List<string>.Contains` on the owned and "new" lists, then `OrderBy(locked)`. With 1,518 owned avatars that is about
  2.3 million string comparisons, far below a second; nothing in it is quadratic in a way that reaches 25 s.
- The result reaches the VM as an array of 1,518 `ASManagedObject`s (`ASValue.SetArray`/`SetManagedObject`); the
  ActionScript reads their fields through `ASManagedObject.GetMember`, whose reflection lookups are cached
  (`MoreReflection` caches per type), so per access it is a dictionary lookup and a property get.
- **The editor always receives all 1,518 rows, owned or not.** Unlock-all changes only the flags: every row becomes
  unlocked, and every granted avatar is also "new" (`KillSignatureNewAvatars`). So the extra work is what the
  ActionScript does for unlocked and new entries (an emblem image and a "new" badge per tile instead of a lock), which
  happens in the native VM and its texture loading, not in managed code.

Not patched: no managed hot spot was found to cache, and the managed-side levers (hiding or capping entries, clearing
the "new" flags) change what the player sees and are not clearly safe without the stack. Next, on the phone: send
`logs/threaddump-<time>.txt` of such a stall (a dump whose game-thread frames sit in `ASManagedObject.GetMember` or
`GetSignatureAssetsList` would point back at managed code; frames in `(wrapper managed-to-native)` UI calls confirm the
VM), and say whether the editor opens after waiting one to two minutes and whether a second opening is fast.

The logs sent again with build 5's merge (`bootstrap.log`, `game.log`, `server.log`, `threaddump-20261025-163218.txt`)
were again **0 bytes**, while the logcat of the same run shows every log file being written without an error. Those are
the names under `Android/data/<package>/files/McvsOffline/logs/`, which other apps cannot read on Android 11 and later, so
a file picker hands over empty files. Send the copies in **Files > Downloads > MCVS-Offline** instead
(`<session>-bootstrap.log.txt` and so on); the watchdog writes the whole thread dump into `bootstrap.log` as well, so
that copy carries it.

## Device test 3 (October 2026): boots and plays; two crashes

Same phone, build 3 (default patch set, `--game-log all`), logcat from LogFox (three runs: 22031 unlock-all crash, 22595 the
slow next start, 24715 shop crash). **The game boots to the main menu and plays matches**: the device-test-2 physics fix
works. Every server request was fast (the slowest a few tens of ms).

### Bug 1: unlock-all lags, then crashes ("Could not find merc data for mercId 'ALL'")

* Seen: with `unlockAll: true` (and the `UNLOCKALL` coupon, which uses the same "mercs" group), the player level jumps to 46,
  level-up rewards tick up, the game lags, then dies with `System.InvalidOperationException: Could not find merc data for mercId
  'ALL'` in `LotteryComponent.DisplayLotteryRewards`, fatal (an unobserved fire-and-forget task, `GameApplication.UpdateWorlds`;
  logged four times: stdout, our crash hook, the game's handler, mono-rt).
  The next start was slow; once everything was claimed, unlock-all worked.
* Cause: the "mercs" unlock group was the single reward `UnlockMerc_ALL_MAX`. Claiming it works (`UnlockMercRewardItem.OnClaimed`
  -> `MercsComponent.CheatUnlockAllMercs(profile, "MAX")`), but it is an `IPrintableRewardItem`, so the reward display shows it,
  and the display looks the merc up by its id (`GetBuiltMerc("ALL")` -> null -> throw). The reward-string grammar is
  `Type_Subtype_Subtype2...` split on `_` (GM/RewardItem.cs Parse); `"ALL"` is only understood by `OnClaimed`, by nothing that
  displays a reward.
* Fix: the "mercs" group (tools/content-gen, `merc_unlock_items`) is now `UnlockMerc_{2-21}` (every displayed merc but the
  starting merc 1, whose reward would only turn into its alternate reward, 40 random gear) followed by `ModTier_{1-21}_4`
  (`ModTierRewardItem.OnClaimed`: every mod of that merc with `Tiers <= 4`, perks included at tier 4; mod names in the catalogue
  go up to Tier4). `UnlockMerc_<id>` takes the plain merc path of every display (`GetBuiltMerc(<id>)` exists for 1-21);
  `ModTier` is not printable, so the lottery skips it, and the inventory diff / inbox claim it after its merc
  (`ModTierRewardItem.IsAvailable` needs an unlocked merc; the diff keeps the ledger's key order, only `MercSkin_*` moves last).
  41 rewards instead of 1; UNLOCKALL then had 11374 (11368 once the 6 unreleased avatars left it, below). The conformance test parses the UNLOCKMERCS coupon with the real
  `RewardHelper.CreateRewardItem` and checks every id is a numeric merc.
* An inventory ledger granted by build 3 still holds `UnlockMerc_ALL_MAX` (already claimed). `UnlockGranter` counts the
  whole "mercs" group as present then (`LegacyMercsKey`), so the per-merc keys are not written over it: each would pay
  the game's duplicate compensation (40 random gear) for a merc the player already has.
* Also fixed by build 4's 'store' patch (bug 2): every `KillSignature_Sound_*` of the signatures group made
  `RewardItem.GetRewardInfo` (which calls `IsAvailable`) throw in the reward popups and the lottery.
* **The level jump is a known one-time behaviour, not changed.** Levels come from the mods: `PlayerProfileComponent.UnlockModForMerc`
  calls `AddXp(modSpecs.PlayerXP)` for each newly unlocked mod (21 mercs x 23 mods), the same with `UnlockMerc_ALL_MAX` or
  `ModTier`. Every XP change recomputes the level; each level crossed runs `OnPlayerLevelUp` (its kill-signature/credits rewards
  are claimed at once, no popup each) and a fire-and-forget `SaveProfile` of the whole (now large) profile: run 22031 made 50
  `POST /profiles/me/myprofile`, each followed by the same `DELETE /messages/secured/me` of one long-deleted message and
  about every second one by a reload of the 400 KB CRM document (`GET /configs/users/me`), all parsed on the game thread.
  That is the lag; it ends once the grant is done. The slow next start (run 22595) was the grant continuing: the crash had
  stopped it after 801 of 11334 keys, so the next start diffed the remaining 10533. A mitigation would mean patching XP or
  save batching in the game; not small and not clearly safe, so not done.

### Bug 2: opening the shop crashes at once

* Seen (run 24715): `NullReferenceException` in `Game.KillSignatureRewardItem.GetAvailableId(PlayerProfileComponent)` [IL 0x84]
  <- `IsAvailable` <- `GameUnifiedStoreComponent.<>c__DisplayClass25_0.<SelectItems>b__8` <- `SelectItems` <- `GetStoreData`
  <- `TryDispatchStore` <- `NativeSetStoreCallback`.
* Cause: `GetAvailableId` builds its candidate list only for "Avatar" and "Background"; for "Sound" the list stays null and
  `list.Count` throws, so `IsAvailable` throws for every `KillSignature_Sound_*` our `#VersusCoins` hub sells (the official
  store never listed sounds). The SelectItems filter (`x.Content.FirstOrDefault()?.RewardPrototype.IsAvailable(profile)`) is
  evaluated for every item when the shop opens.
* Fix: new default patch **'store'** (patcher/Patches/StorePatch.cs, Game.dll, both methods fingerprint-checked):
  S1 the "other subtype" branch of `GetAvailableId` returns `Subtype2` for a sound the player does not own yet
  (`KillSignatureSounds.Contains`, the same test `OnPrepareToClaim` makes), null for a random sound or any other subtype, and a
  null `Subtype2` returns null; avatars and backgrounds run the shipped code. S2 the SelectItems filter lambda runs in
  `try/catch (Exception)`: an item whose check throws is left out of the shop and reported once per id as
  `[McvsOffline] store item <id> threw <exception>` (stdout/logcat, bootstrap.log via `GameHooks.StoreItemException`; the
  crash report counts them). Both executed against the real Game.dll on net8 (RealStorePatchTests).
* The other hub rewards (MercWeapon, GearSlot, ModTier, crates, VIP, packs, `WeaponCamo_<merc>_Random`,
  `MercAccessory_<merc>_Random`) were read in the decompiled `IsAvailable`/`GetRewardInfo`: no null path for the ids we generate
  (GearSlots always has 6 entries, slots are 1-6; crates check their contents, where a random sound now is simply unavailable).
  Not verified on a device: the serialisation of each hub entry (`SerializeObject` -> `GetObject`) is not covered by the safety
  net; a throw there would still stop the shop.

### Also in build 4: opt-in codes for paid and unreleased content

`UNLOCKPAID` (VIP-pool-only camos and jerseys, 5,644 rewards with the mercs first; split as `UNLOCKPAIDCAMOS` /
`UNLOCKPAIDACCESSORIES`) and `UNLOCKUNRELEASED` (6 never-released kill-signature avatars, 1513-1518). The unreleased set
is no longer sold or part of UNLOCKALL/`unlockAll` (UNLOCKALL: 11,368 rewards). How the sets are decided, the caveats
("no shipped source" alone would wrongly catch Witch and Bli-P, which the live server sold) and every exclusion:
docs/ECONOMY.md "Paid and unreleased content". Not tried on a device yet.

## Device test 2 (October 2026): "connecting" forever

Same phone (Samsung Galaxy S22, Android 16), build 2 (`d9a433f`: hook, headless, headless-engine, nativeonline, geo, bottile,
finder; `--game-log all`), **internet on**, installed over build 1 (same key). One run, logcat from LogFox.

### What worked

Everything up to the TS handshake, as predicted. Hook at +0.008 s, `args.txt is up to date` (with `-DataCenter mcvs`), server
on 127.0.0.1:51780, CRM document built; the native online SDK was skipped twice (client World and TS World). Requests #1-#25
in the predicted order with no `datacenters` request: client session, LocalAnubis super session, QuickLaunch,
`lobbyconfigs ts`, Controller login, `connect game` (TS World created), the TS World's own Federation, CRM
`configs/users/me/game` 596 ms after the TS session, TS lobby login. **The TS World loaded `Levels/Menus.level` with no
`Fatal` and no `[McvsOffline] headless world:` line**: the device-test-1 headless fixes work. The native IAP library reports
`initialized: yes` without GaiaInit (this answers the open question of device test 1).

### The hang

* `#26 POST /authorize` on behalf of the player (the TS handshake: `Connection.ProcessHandshake` ->
  `OpenSessionForUser`), answered 200 in 0.0 ms. The game logged `[w2] {FedRequestHandler.cs}.SendAsync:48 | Request
  .../authorize took 5 ms` on the game thread at 953.755 and then **nothing at all, on any thread**, for 60 s (logs/game.log
  has exactly the logcat entries). For every earlier request a second line, `{RequestHandler.cs}.SendAsync:73 ... returned
  completed with code : OK`, followed; for #26 it never came.
* Watchdog: `STALL: no game-thread work item ran for 5940 ms; frame clock (totalTime) unchanged for 5004 ms`, then at 15 s
  `game thread 26748: 'Thread-22' state S wchan futex_wait_queue_me syscall ?`: the game thread was **asleep in a futex**
  (a lock or condition wait), not spinning, not in the network. Java stack: `"Thread-22" RUNNABLE` with no Java frames.
* The heartbeat says where: the last beat ran at 952.916, the frame clock last moved by 953.85, and the beat posted at
  about 953.86 (to `GameApplication.Context`, which raises `WorkItemAdded`) never ran. So the game thread blocked **in the
  frame that ran the TS handshake continuation, after the TS World's work items** (`World.UpdateUnhandled`: `ExecuteItems`,
  then `_Update`), in a wait that does not pump contexts. The #26 continuation had gone asynchronous and was queued on the
  TS World's context, which only that thread drains: hence the missing second line.

### Cause (leading hypothesis, not yet confirmed on a device)

The facts below are verified in the decompiled engine, the IL and libnativeengine.so
(`/home/user/mcvs-work/reports/device2-deadlock.md`, tags [V]); that this overlap is what put the game thread to sleep is an
inference (that report's [I], likelihood high): no device run has shown the game thread inside a Havok wait yet.

* `GameOptions_android.json` (profile `Specific`) turns on **multithreaded Havok physics** for the whole process
  (`PhysicsComponent.PhysicsMultithreaded = true`, the only override that turns it off is never selected), and GPU defaults
  turn on **deferred physics** (`DefaultDeferred = true`). The options are applied in `LoadGameDefinition`, before the client
  World exists. Every World's `PhysicsComponent` constructor passes `PhysicsMultithreaded` to the native constructor.
  `Deferred = !World.Headless && DefaultDeferred`: the client steps on a `LoadingThreadPool` thread
  (`PhysicsComponent.UpdateSimulation` -> `World.RunTask`), a headless World steps on the calling thread (the game thread).
* Native side: the `hkCpuThreadPool` and the `hkJobQueue` are **process globals**, created once (NE 0x37fab4, `[0x16ff440]`,
  `[0x16ff448]`); every multithreaded hkpWorld registers with that one queue and `PhysicsComponent_UpdateStep_private`
  calls `hkpWorld::stepMultithreaded(world, queue, pool, dt)`. The pool's `processJobQueue` and `waitForCompletion` take no
  lock and share one "processing" flag, one worker callback slot and one completion semaphore: **one stepping thread at a
  time**. The waits are `hkSemaphore::acquire` = `pthread_cond_wait`, i.e. `futex_wait_queue_me`.
* A World steps physics only while it plays a level. The TS World started playing `Menus.level` just before the handshake
  (about 53.7). From then on, every frame the client's step ran on a pool thread **while** the TS World's step ran on the
  game thread inside `w2._Update`, through the same pool and queue, with nothing serializing them (`WaitDeferred` only orders a
  World's step against its own previous one). Within a few frames a thread waits for a completion that never comes.
* Gameloft never ran this mix: their dedicated servers ran one World per Linux process and skip GameOptions entirely
  (`GameOptionPerformanceProfileComponent.OnCreate` returns on Linux), i.e. single-threaded, non-deferred Havok; the Android
  client has one World.

Which Havok wait the game thread sits in, and whether it sits in one at all, cannot be settled statically (the build 3
thread dump will show it); that the two steps overlap with no synchronisation is verified. **Ruled out** (same report and
`/home/user/mcvs-work/reports/device2-ourcode.md`): the context-pump mechanism (no game-thread wait in the frame loop pumps
contexts; `Join`, `WaitLoading` and `Send` have no callers; `DelayedPost` has none either), the HTTP continuation chain (no
blocking call), the loggers (`DeviceLogger_Log` is a bare `ret`; our bridge only enqueues), MenuComponent/SWF in the TS
World (both `[KeepInHeadlessMode(false)]`), the network (futex, not poll/recv), and **our own code**: everything of ours on
the game thread is bounded and was idle, our IAP P/Invoke first ran 33 s after the hang and takes no lock, and after the
hang our threads still JIT-compiled, allocated and made JNI calls (no runtime-global lock held). The headless patches are
not wrong; they let the TS reach code that had never run on Android.

**Not ruled out: the render path.** In `RenderThreadMode.Full` there is no managed cycle (the render thread waits for
`UpdateSignal`, which the game thread sets before its own wait) and the native render-queue lock is a spinlock, but it is
not verified that this phone runs Full. The GPU defaults in `GameOptions_android.json` are `enableRenderThread false`,
`renderThreadMode 0` (None); only the named GPU profiles (VeryLowGPU to High_Ultra) set `true`/`3` (Full). The S22
(SM-S901E, Adreno 730 per the device-test-2 logcat) is named by no rule (they list Adreno 3xx-6xx, Mali, PowerVR, Tegra and
a few models); it gets MidGPU only through the last rule, `MidGPU: CORES>1`, a catch-all whose evaluation is native and was
not checked. In None mode `Render()` runs on the game thread (`GameApplication.cs:883-895`), a path that was not analysed.
The build-3 `[boot] physics:` line prints `RenderThreadEnabled`/`RenderThreadMode` and settles it.

### Fix (build 3)

* **`physics`, a new mandatory patch** (Gameloft.Engine.dll), each site checked against an IL fingerprint of the whole
  verified method first:
  * P2: the `PhysicsComponent` constructor passes `McvsPhysics.UseMultithreading(this)` to `ConstructNative` instead of
    `PhysicsMultithreaded`. Headless (TS/GS) Worlds get a **single-threaded hkpWorld** (simulation type 2, `stepDeltaTime`,
    never registered with the global queue: `tbz w19` at NE 0x37fa8c), as on Gameloft's servers; the client keeps
    multithreaded, deferred physics. The final answer comes from `GameHooks.PhysicsMultithreading`, which also logs it
    (`[boot] physics: ...`); without McvsOffline.Server.dll the IL alone picks "multithreaded only for client Worlds".
  * P1: `PhysicsComponent.UpdateSimulationSteps` (the only caller of `UpdateStep_private`) runs inside
    `McvsPhysics.EnterStep()`/`ExitStep()`, a **process-wide gate**: no two Worlds ever step Havok at the same time. The
    wait is bounded (2 s, then the step goes ahead without the gate and a warning is logged), so the gate can never hang the
    game itself; `WaitDeferred` is always called outside it, and nothing that runs inside a step waits for the game thread
    (contact callbacks post to the World's context).
  * Settings for A/B runs (defaults = the fix): `embedded.physics.serverMultithreaded` (false), `clientMultithreaded`
    (true), `stepGate` (true), `stepGateTimeoutMs` (2000).
* **`waits`, a new default patch, hardening with no observed trigger** (Gameloft.Engine.dll): both
  `TaskExtension.WaitAndWork` overloads wait in 100 ms slices (`WaitAny(handles)` -> `WaitAny(handles, 100)`). `Post`,
  `Send` and `EnqueueDelayedItems` raise `WorkItemAdded` and native tasks call `NativeTaskAdded`, so ordinary work always
  wakes these waits. The slices bound the two state changes in the shipped code that raise no event: a context re-enabled
  by `World.OnLoadedScene` (`Context.Enabled = true`; only `World_Disposing` waits with contexts), and the native wake-up
  callback, which the `NativeTaskAdded` accessors install and remove with an unlocked check-then-act. They do **not** cover
  a context registered to the thread after the wait began (`Currents` is a snapshot, re-drained every slice), and
  `DelayedPost` has no caller in the game. Every call site pumps exactly what it pumped before (the non-pumping
  `task.Wait()`/`signal.Wait()` paths are unchanged), so no work runs where or in an order it did not run before. Not
  related to this hang: no pumping wait was active.
* Our code: the IAP status read now waits for the first heartbeat and stops after the first "yes"; the watchdog reports a
  heartbeat that ever runs on another thread than the game thread. No interference was found.

### Diagnostics added for device test 3

| What | Where to find it |
|---|---|
| **Thread dump** at the 15 s and 180 s STALL (`diagnostics.threadDump`, default true), on its own thread `mcvs-threaddump`, never waited for: (1) native view, no suspension: every thread's state, wchan and `/proc/self/task/<tid>/syscall` (futex address, mapping, words, timeout, pc), read with `PR_SET_DUMPABLE` raised and restored (the reason for "syscall ?"), plus a stack scan (64-bit installs; the S22 runs arm64) of the game thread and the threads that share its name and the Finalizer, through `/proc/self/mem` (a bad address is an error, never a crash), raw `lib+0x...` first, then named (`dladdr`, `mono_pmip` for managed code); (2) Mono's own managed dump of every thread (`mono_threads_request_thread_dump`, printed by the finalizer thread to fd 1, which points at a file for at most 10 s) | logcat `McvsOffline` lines `[threaddump] ...`, the header `Full thread dump:` on tag `mono` (priority E), `logs/bootstrap.log` (copied to Download/MCVS-Offline), `logs/threaddump-<time>.txt` |
| The STALL snapshot's game-thread line now reads the syscall too (`syscall 98 0x... (futex: waiting for a lock or condition)`) | logcat `[watchdog] STALL` |
| Physics decisions and the engine's threading choices, once per World | `[boot] physics: client World #1: Havok multithreaded ...; engine threading: PhysicsComponent.DefaultDeferred ..., GameApplication.RenderThreadEnabled ..., RenderThreadMode ...`, `[boot] physics: server (headless) World #2: Havok single-threaded ...` |
| Step-gate counters (entries, waits, longest wait, timeouts) | the `[status]` line, `physics: ...` |

### Expected log sequence in device test 3 (up to the main menu)

`adb logcat -s McvsOffline mono mono-stdout` or LogFox filtered on the game. Lines 1-8 are as in device test 2 (request
numbers included), plus the physics milestones.

1. `[embedded] MCVS Offline 0.1.0-dev embedded host starting`, `[crash] hooks: ...`, `[boot] +0.0 s MCVS Offline host
   started by the game`, `[logfile] ... exists, N bytes before this run` (bootstrap.log, game.log, server.log), `[gamelog]
   Gameloft.Logs levels: ...`, `args.txt is up to date`, `[boot] offline server listening at http://127.0.0.1:51780/`,
   `[diag] [mirror] ...`, `[crm] CRM document built: 1864 offline_store items ...`, `[diag] [exit] how the last 5 game
   processes ended`.
2. **New, before #1:** `[boot] physics: client World #1: Havok multithreaded (GameOptions multithreadedPhysics true,
   settings embedded.physics.clientMultithreaded true; step gate on); engine threading: PhysicsComponent.DefaultDeferred True
   (client physics on a loading-pool thread), GameApplication.RenderThreadEnabled True, RenderThreadMode Full` (the values are
   whatever the phone's GameOptions profile says: True/Full if the S22 gets MidGPU through the `CORES>1` rule, False/None
   if it keeps the GPU defaults; nothing has shown which yet).
3. `#1 .../datacenters/mcvs/urls` -> `[boot] client Federation: eve config (#1)`, `[watchdog] game thread is tid N ''`, `#2
   locate/auth`, `[boot] native online SDK init requested (... call 1): skipped`, `#3 POST /authorize` -> `[boot] client
   anonymous session`, `#4 users/me`.
4. `#5 .../mcvs/urls` -> `[boot] second Federation: eve config (LocalAnubis)`, `#6`, `#7` -> `[boot] LocalAnubisManager.Start:
   super session`, `#8`.
5. `#9 verify`, `#10 locate/matchmaker`, `#11` -> `[boot] QuickLaunch: Finder session`, `#12` -> `[boot] QuickLaunch:
   lobbyconfigs ts`, `#13 encrypt_token`, `#14` -> `[boot] Controller login of the client`, `#15` -> `[boot] Controller
   'connect game': creating the TS World`.
6. **New:** `[boot] physics: server (headless) World #2: Havok single-threaded (GameOptions multithreadedPhysics true,
   settings embedded.physics.serverMultithreaded false; step gate on)`.
7. `#16` -> `[boot] third Federation: eve config (TS World)`, `#17`, `native online SDK init requested (... call 2): skipped`,
   `#18` -> `[boot] TS World: game-server session`, `#19`-`#22`, `#23 configs/users/me/game` -> `[boot] first CRM config ...
   N ms after the TS session`, `#24 encrypt_token`, `#25 verify` -> `[boot] TS lobby login: the TS World now loads
   Levels/Menus.level`. About 1.5 s of silence while the TS loads its level (normal).
8. `#26 POST /authorize` on behalf of the player -> `[boot] TS handshake: session on behalf of the player (#26)`, and on
   mono-stdout `[w2] ... FedRequestHandler.cs}.SendAsync:48 | Request .../authorize took N ms`. **This is where device test
   2 stopped.**
9. **The test:** `[w2] ... {RequestHandler.cs}.SendAsync:73 ... /authorize returned completed with code : OK`, then the rest
   of the TS handshake, roughly in this order: `#27 users/me`, `alerts/me` -> `[boot] alert stream open (the TS handshake
   reached the player's session)`, the first `profiles/...` read -> `[boot] TS handshake: first profile read (Seshat)` (a new
   player: the root is `{}` and the sections are created), `users?device_id=` -> `[boot] TS handshake: accounts for this
   device`, the section creations and the game-profile save -> `[boot] first profile write (Seshat)`, `location/?ip=` ->
   `[boot] TS handshake: geolocation lookup answered locally`, `games/mygame/alias` -> `[boot] TS handshake:
   games/mygame/alias`, `configs/users/me`. No `[watchdog] STALL` line, no `physics step gate: ... stepping without the gate`
   warning.
10. The client loads Menus.level (no requests), shows the language prompt and the age gate, then the main menu; `[boot] quiet:
    no request for 10 s ...` once the menu is idle is normal. Within a minute: `[status] boot: ...; watchdog: ... N beats; ...
    physics: 2 Havok worlds built (1 server Worlds made single-threaded), step gate on: N entries, M waited (max X ms), 0
    timeouts; native IAP library initialized: yes`.

**If it still hangs** at step 9: `[watchdog] STALL` at 5 s (now with the futex syscall), at 15 s the Java stacks and then the
`[threaddump]` sections: `native view: dumpable 0 (raised to 1 for the reads), /proc/self/mem open, ...` with one line per
thread, `native stack of <game tid> (raw)` / `(named)`, the same for the `Thread-22` siblings and the Finalizer, then `Mono
managed thread dump via libmonosgen-2.0.so: complete after N ms` and one `[threaddump] "<thread name>"` block per thread. The
game thread is the `"<unnamed thread>"` block whose frames contain `Gameloft.Engine.GameApplication:UpdateWorlds`. Its top
frames name the wait (a `(wrapper managed-to-native) ..._private` engine call, `Monitor.Enter`, `ManualResetEventSlim.Wait`,
...); see `/home/user/mcvs-work/reports/device2-threaddump.md` §5 for how to read it. Then one A/B run with
`"embedded": {"physics": {"serverMultithreaded": true, "stepGate": false}}` in settings.json should reproduce the
device-test-2 hang (and confirms the cause); a hang **with** the defaults is something else, which the dump names. If the
`[boot] physics:` line says `RenderThreadMode None`, `Render()` runs on the game thread and the render path is the first
suspect.

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

### Expected log sequence in device test 2

Superseded by the device-test-3 sequence above (steps 1-8 are what device test 2 showed).

## 1. Implemented

| Area | What | Code | Protocol note |
|---|---|---|---|
| Core | HTTP/1.1 server on raw sockets (keep-alive, chunked, `Expect: 100-continue`, streams), router with re-dispatch, MiniJson, accounts and self-describing tokens, atomic writes with file and directory fsync, settings with merge-overrides, request/unknown-endpoint logging, GCBF/LZ4 data-pack reader | `server/Core` | [MODULES.md](MODULES.md) |
| AUTH | eve, pandora (locate, service-prefix aliases), authorize (anonymous, game server, on-behalf, transfer code), users, compliance, aliases, credential links, `encrypt_token`/`verify`, `IAuthenticator` | `server/Services/{Eve,Pandora,Auth}` | [auth.md](protocol/auth.md) |
| STORAGE | Seshat profiles with verbatim values, nested `include_fields`, deep merge, no ETag, 401-never-403, arrival-order lock, cache, backups, corrupt-file recovery (503, never "missing"), batch/backups/visibility/data endpoints, `IProfileAccess`, LAN seam | `server/Services/Storage` | [storage.md](protocol/storage.md) |
| CONTENT | CRM over the player's own preconfiguration (validated against every client cast), matchmaker `lobbyconfigs`, unlock coupons, local Iris assets, generated VersusCoins economy (1,864 entries), the Cosmetic crate (build 9: CRM cells per player, guard and latch) | `server/Services/{Crm,Matchmaker,Assets}`, `server/Content`, `tools/content-gen` | [content.md](protocol/content.md), [ECONOMY.md](ECONOMY.md) |
| MISC | Hermes mailboxes, Kairos alert stream, Olympus leaderboards with league groups, Osiris events/profiles, clans, wall, group chat, profanity, game_stats, BanTool, geolocation, tracking/marketing catch-alls | `server/Services/{Social,Messaging,Alerts,...,Misc}` | [misc.md](protocol/misc.md) |
| LIVE OPS | Weekly division seasons (17 division events, 100-entry season boards with rivals that grow and freeze), daily missions, weekly events, weekly modifier as CRM table overrides, CRM `expiry` for the client's refresh, culture-safe dates | `server/Services/Social/LiveOps`, `server/Services/Leaderboards/SeasonRivals.cs`, `tools/content-gen/liveops_tuning.json` | [LIVEOPS.md](LIVEOPS.md), [misc.md](protocol/misc.md) |
| Unlocks | `settings.unlockAll` through the inventory ledger (starts only once the game profile and the inventory section are stored, waits for the player's alert stream, backup, ordered batches with the merc unlock re-checked before each, `set_profile` alerts) | `server/Services/Unlocks` | [unlocks.md](protocol/unlocks.md) |
| EMBED | In-game bootstrap (game root via the game's own Java helpers, `args.txt` managed block with `-DataCenter`, device id, logcat, server thread); diagnostics (crash reporter, self-reporting log files, boot trace, game-thread watchdog with managed and native thread dump, game-log bridge, phone-readable copy, exit reasons); patcher (hook, headless, headless-engine, physics, nativeonline, waits, geo, bottile, finder, lan; `res/raw/args.txt` for `--game-log`; vendor-wide manifest permission renamed into the package namespace; AOT strip; ABI selection (every ABI by default, `--abi arm64-v8a` for 64-bit only); signatures (build 7); debloat (build 8: 44 dead manifest elements, Crashlytics/Fabric files, SDK start-up calls); raw zip rewrite; v1/v2/v3 signing); installer-data restore (saved CDN index, journal and preferences, size-only index for a complete `Data/`, background CRC pass, installer monitor) | `hosts/embedded`, `patcher` | [PLAYER-SETUP.md](PLAYER-SETUP.md), [INSTALL-OFFLINE.md](INSTALL-OFFLINE.md) |
| ADMIN | The in-game settings page (build 9): `LocalAccessGate` on every route, tickets and sessions, header-time body limits, log redaction; settings allowlist and schema, status, save-then-exit; backups in four pools, save bundle and transfer text, staged operations with probation; logs tail, zip and share; the page; the Android host side (share sheet, clipboard, restart, JNI table, mirror fix) and the `adminpage`/`adminlink`/`forum` patches | `server/Core/Http`, `server/Services/Admin`, `server/Content/admin`, `hosts/embedded/Admin`, `patcher/Patches` | [design/settings-page.md](design/settings-page.md), [PLAYER-SETUP.md](PLAYER-SETUP.md) |
| Hosts | Standalone net8 console host (prints the settings page's link at start); embedded DLL compiled against the game's trimmed Mono BCL (ILVerify-clean) | `hosts/standalone`, `server/McvsOffline.Server.Embedded.csproj` | [DEV-LOOP.md](DEV-LOOP.md) |

### How it was checked

`MCVS_REFS=<apk assemblies> MCVS_REQUIRE_EMBEDDED=1 MCVS_REQUIRE_REAL_CLIENT=1 scripts/build.sh`: net8 solution, embedded
build against the game's own BCL, then all tests. Result after the device-test-2 fixes and their review: **McvsOffline.Tests
485/485, McvsOffline.Embed.Tests 119/119, 0 skipped**:

| Tests | Count |
|---|---|
| Core (HTTP, router, JSON, accounts, settings, storage helpers, data pack, embedded-DLL references, libc SO_REUSEADDR, log files, boot trace) | 145 |
| AUTH | 64 |
| STORAGE | 83 |
| CONTENT (including the generator's own Python tests and the real data pack) | 73 |
| MISC | 110 |
| Unlocks | 8 |
| End-to-end conformance (real client) | 2 |
| Embedded host (including the crash reporter and its separate fatal/non-fatal budgets, the watchdog, the thread dump, the physics hooks and step gate, a Cecil resolution of every reference of the built DLL against the game's assemblies, and every P/Invoke typed for both shipped ABIs: no 32-bit `off_t`/`ssize_t` mismatch on armeabi-v7a) | 65 |
| Patcher (including the binary manifest rewrite, `res/raw/args.txt`, the device-1 and device-2 patches applied to the real Game.dll and Gameloft.Engine.dll, then executed and JIT-compiled on net8, and the IL fingerprints of the verified methods) | 54 |

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
- **Live ops** use our own tuning: the original season length and rival strength are not in the client
  ([LIVEOPS.md](LIVEOPS.md)); permanent achievements are switched off in 1.17.32 itself; shard "mega events" are not
  generated (Arena-mode events are, as the rotating Special Events of build 15, [LIVEOPS.md](LIVEOPS.md) §8a). Changing
  the live-ops content while an event runs resets that event's progress.
- **Special Events** (build 15) are checked through the real client's code (event parsing, the Battle popup's box, the
  TS's choice for a room, the TS to GS hand-over) but not yet on a device: a Special Event match with bots, its rewards
  and the box's timers are on the device checklist ([LIVEOPS.md](LIVEOPS.md) §8a).
- The CRM `iap` catalogue is empty on purpose (it would start real Google Play purchases nothing can fulfil).
- A brand-new account's root GET is `{}` without the server-maintained fields (`credential`, `datacenter`,
  `created`) until its first save; every game reader defaults them (checked in the conformance harness).
- Scopes are recorded but not enforced; `auth.requireSuperPassword` is off by default (any `game:` credential may act
  on behalf of players).
- Native SDK calls (global device id, ads, ecommerce) have unknown formats: they reach the unknown-endpoint log.
- The DLC download path (inactive, `UseDlc=false`) cannot work with our `http://` locate answers.
- `tools/cdn-mirror` (mirror, verify, offline install) is in the repository; a separate data-pack decoder tool
  (`tools/datapack`, research report 11) is not, the server's own data-pack reader is in `server/Core/Content`.
- A rebuilt installer index (a `Data/` folder without Gameloft's index) checks sizes only; damaged content of the right
  size is accepted (run `tools/cdn-mirror/verify.py` on such a backup, or add a mirror's `pack.info`).
- Internet P2P (M4) is not started; of the preservation extras (M5) the CDN mirror and offline install exist.

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
   request and failed 1.7 s later (the headless crash, fixed). Device test 2: the level load works, no headless-world line;
   the game thread then hung in the TS handshake frame (the physics race, fixed in build 3). Check the device-test-3
   sequence above from step 8 on: `RequestHandler.SendAsync:73` after #26, the rest of the handshake, the menu, no
   `[watchdog] STALL`. With the internet on, steps 3-7 must follow without a `[boot] quiet` line (true in device test 2).
4. **The 1 s CRM window.** The TS's first `GET /configs/users/me/game` must be answered within 1 s of the TS starting,
   or `IsDevelopment=true` leaks into that session (verify-C5). The document is pre-built at startup; compare the
   request's timestamp with the server's "CRM document built" line on a slow phone. Device test 1: 229 ms after the TS
   session (the boot trace now prints this and warns above 1000 ms).
5. **Mono transport.** `Expect: 100-continue` on the large R4 save; `fed_id%3A` vs `fed_id:` in paths (both accepted;
   the log shows which); the chunked `text/event-stream` read line by line (`GET /alerts/me` stays open); no HTTPS
   anywhere (all our URLs are `http://` with an explicit port) (verify-C3 §8, MISC notes).
6. **Native IAP and online SDK.** The native online SDK (GaiaInit) is off by default now (`nativeonline`; turn it back on
   with `embedded.nativeOnlineSdk: true` to compare). Device test 2: `native IAP library initialized: yes` without
   GaiaInit. Still to see: the shop and the VersusCoins tab once the menu is reached. With "no", `IapStoreComponent` never registers and the unified shop (VersusCoins items included) cannot load: open the
   shop and the VersusCoins tab. If it hangs, the fallbacks are the report 04 §14.5 patch (raise LibInitialized and
   StoreRefreshed with an empty store) or `crm.iap=preconfig`. Also look for `service-prefixed path`, `locate of unknown
   service` and unknown endpoints such as `/users/me/approvals/` (verify-C2 §4.4-4.5, AUTH and CONTENT notes).
7. **First-boot gates.** Language prompt and age gate (`crm.skipAgeGate`); after the forced uninstall of the original
   game, the Java installer's data validation: build 5 restores or rebuilds its index from data on the phone
   ([INSTALL-OFFLINE.md](INSTALL-OFFLINE.md)); `qaTestingConfigs.txt SKIP_VALIDATION=1` stays the untested last resort
   (report 10 §4.3) (verify-C2 §4.6-4.7, EMBED notes).
8. **No 4xx on the first-install load path**, in particular none on `profiles/...` (a 403/404 there resets the profile).

### P1: progression is kept

9. **Save and reload.** Play, wait for the 5-minute autosave or a match end (R4 then R5 `200` in the log), kill the
   app, start again: same profile. `data/storage/profiles/<account>.json` grows; backups appear every 30 minutes.
10. **Match end on the GS** saves the result; the bot-match resume race (verify-C3 §4): a match result followed
    quickly by a resume GET must not restore the pre-match state.
11. **Bot matches.** Battle › Classic multiplayer goes to the GS and pays full rewards (trophies, MVP, merc XP) once
    PvP is activated; it starts at once, filled with 7 bots (`instantBots`) (verify-C4). There is no visible
    bot-match tile: `bottile` only affects the hidden PRACTICE tile (MainMenuElements row 13, `Displayed=false`), which
    the opt-in `crm.practiceTile` shows (placement and art untested). Tournaments (custom matches) are private matches:
    the GS never fills them with bots and they give no trophies, currency, MVP, agent XP or stats, by design; they
    need other human players (LAN is experimental and does not work end to end yet, PLAYER-SETUP §9), so use Classic
    for bots. Special Event is the game's Arena mode: live ops generates a rotating one (build 15, LIVEOPS §8a),
    filled with bots like Classic.
12. **The GS's Lobby round-trips** (`encrypt_token`/`verify` for its own super session, `GetRoomInfo`) never fail: a
    failure there plays the match and grants nothing (verify-C4 §4.1). Fed ids must match on both paths (§4.3).
13. **Matchmaking after a network change** (Wi-Fi or airplane mode toggled while the game runs), fixed by the default
    `finder` patch; drop it with `--patch nativeonline,geo,bottile` if it misbehaves (verify-C6 §2.2, EMBED notes).
14. **Divisions and leaderboards.** `SelfData` gives a rank in a 100-entry league group; the season end promotes or
    demotes once and the reward reaches the inbox; the rivals' pace is plausible against real trophy gains (build 5:
    "What to check in device test 5", [LIVEOPS.md](LIVEOPS.md)).

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
