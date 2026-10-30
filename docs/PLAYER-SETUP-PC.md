# Player setup: MCVS Offline on the Steam (PC) version

**Status: draft for the third Windows round (WR3), build 17** (build 14's PC fixes; build 15 added only server features: the Special Events and an optional Practice tile; build 16 removes the config lines that most likely made Windows start the game without MCVS Offline in builds 14 and 15, and says when the game starts without it; build 17 delivers the unlock codes without the inbox and adds the `inbox` patch, which keeps a big redeem-code message from freezing the inbox). The second round (WR2, build 13) showed that the
install and the game start work, but the game kept loading its own `Game.dll` instead of our patched copy, so every
fix in that file was missing: the two inbox crashes, the missing "MCVS Offline" button in Settings › Account, the bot
match running the old way and probably the long freezes. Build 14 makes the game load our copy and says
`installed, NOT ACTIVE` when it does not. It also records crashes and freezes much better. WR3 repeats most of WR2 with
the fix in place, and tries what WR2 could not reach. Follow the steps in order, write down what the checklist in
section 7 asks for, and send back the files of section 8. If anything looks wrong, stop and send what you have; nothing
here can damage the Steam install (see section 3).

This guide is for the Steam version of Modern Combat Versus (Steam build 6372049, game version 1.17.30). The Android
version has its own guide: [PLAYER-SETUP.md](PLAYER-SETUP.md).

## 1. What you need

* Windows 10 or 11, 64-bit.
* Steam, running and signed in. For play without internet, use Steam's offline mode (Steam › Go Offline); the game
  does not start without Steam.
* Modern Combat Versus installed from Steam and started once (Steam's first start installs .NET Framework 4.5.2 or
  later, which the game needs; Windows 10/11 already have 4.8).
* The kit: one file, `McvsOffline.PcKit.exe` (about 35 MB). No .NET install, nothing else to download.
* Close the game before you install or uninstall.

## 2. Get the kit

1. Download `McvsOffline.PcKit.exe` into any folder, for example `Downloads`. Its SHA-256 is
   `06235ac9f0cf6339bb201ffb32b9c9b14fb3c98453d0a2604a16470c0c31ae41` (PowerShell:
   `Get-FileHash .\McvsOffline.PcKit.exe`).
2. **SmartScreen warning.** The exe is not code-signed, so Windows may show "Windows protected your PC". Click
   **More info**, then **Run anyway**. Your antivirus may scan it first.
3. Open a terminal in that folder: in Explorer, right-click an empty spot and choose **Open in Terminal** (Windows 11).
   On Windows 10, hold Shift while you right-click and choose **Open PowerShell window here**. Use a normal terminal,
   **not** "Run as administrator".
4. Check that it finds the game:

   ```powershell
   .\McvsOffline.PcKit.exe status
   ```

   It prints the game folder and `state: not installed`. If it cannot find the game, add
   `--game "<folder that holds ModernCombatVersus.exe>"` to **every** command below (Steam: right-click the game,
   **Manage** › **Browse local files** opens that folder), or set it once for the terminal window:
   `$env:MCVS_PC_GAME = "<folder>"`.

If you double-click the exe instead, it shows a numbered menu with the same commands (install, uninstall, status,
status listing, verify, probe, watch, collect, and "game folder" to give the folder by hand).

## 3. What the kit changes

Nothing that Steam owns is changed: every game file stays byte for byte as Steam installed it. The kit adds:

```
<game folder>\
  ModernCombatVersus.exe.config        tells .NET to load the patched copies below (removed by uninstall)
  args.txt                             written by MCVS Offline at every start (uninstall strips its block)
  McvsOffline\
    bin\                               patched copies of 4 game DLLs + the offline server (made on your PC from your files)
    install.json                       what was installed
    settings.json, accounts\, profiles\, logs\, exports\, ...   your offline saves, settings and logs
    kit\                               the kit's own log, listings and 'watch' recordings
```

The patched copies are made from **your** game files on **your** PC; the kit contains no game file.

**Important: uninstalling the game in Steam deletes `McvsOffline\` with your saves.** Export your save first (settings
page › Saves › Export).

The game itself also writes some files that are not ours: `dr0.bin`..`dr3.bin`, `OF_PP.bin` and `GameVersion` in the
game folder, `core.dmp` there when it crashes (its own crash dump; only the last crash's is kept), Gameloft device-id
files in Documents (`GDID.bin`, `DC.bin`, `HDIDFV.bin`), and its options (`MyLocalStorage`) and other data under
`%APPDATA%` and `%LOCALAPPDATA%\Gameloft`. That is stock behaviour; neither our uninstall nor Steam's removes them.

## 4. Install and play

1. Close the game. Quit capture and overlay tools (OBS, Discord overlay, Medal, GeForce Experience and the like) for
   this round. Once, before the round: turn on Windows' crash dumps for the game (section 6, "Crashes: Windows' own
   crash dumps", one administrator prompt).
2. `.\McvsOffline.PcKit.exe status --listing` (records the game folder **before** install).
3. `.\McvsOffline.PcKit.exe probe` (from the normal terminal; it checks the local ports, the folder and .NET).
4. `.\McvsOffline.PcKit.exe install`, then `.\McvsOffline.PcKit.exe verify` (do not skip `verify`: its `written`
   lines are compared with section 7). `status` should now say `installed, current` and, until the game has been
   started once, `activation: NOT VERIFIED: no start of MCVS Offline since the install (start the game once, then run
   status)`. The end of `install` names the exe and the config Windows has to read (`exe …\ModernCombatVersus.exe`,
   `config …\ModernCombatVersus.exe.config`): they must be in the folder Steam starts the game from (Steam › Manage ›
   Browse local files). If build 13 is still installed from WR2, run `.\McvsOffline.PcKit.exe uninstall` first (your
   saves and settings stay), then `install`. Over build 14 or 15, just run `install`: it rewrites their config (section 5).
   * `INSTALL FAILED` (in the double-click menu) or an `error:` line: read the error. "The game will start WITHOUT MCVS
     Offline until install succeeds" means the config was not written: fix the cause (game closed, folder writable) and
     run `install` again.
   * "`<name>_args.txt` is in the game folder": the game would read that file instead of `args.txt` and go online.
     Move it out of the game folder and run `install` again.
   * "a ModernCombatVersus.exe.config that is not ours exists": move it away and run `install` again.
5. In a second terminal: `.\McvsOffline.PcKit.exe watch` (records memory, debug output and network endpoints until the
   game exits or you press Ctrl+C; when the game freezes for a minute it also saves a dump of the game, see section 6).
6. Start the game **from Steam**, as usual. Do not start it under a debugger: the game then shows a dead window.
7. After the first start reaches the main menu, close the game and run `.\McvsOffline.PcKit.exe status`. It must say
   `installed, current` and `activation: active at the last start`. If it says `installed, NOT ACTIVE`, stop here, run
   `collect` and send the zip: the rest of the round would test the old files again. What the other answers mean:
   * `activation: NOT VERIFIED: no start of MCVS Offline since the install`: the game has not been started since the
     install (or it stopped before MCVS Offline wrote anything). Start it once from Steam, then run `status` again.
   * `activation: NOT ACTIVE: the game started without MCVS Offline; Windows did not use …\ModernCombatVersus.exe.config`:
     the game ran (it wrote its own `dr0.bin`/`OF_PP.bin`/`GameVersion`) but our files did not. The `hook:` line below it
     says whether our `EngineBridge.dll` ever ran (it leaves `%TEMP%\McvsOffline\hook.txt`). Run `collect` and send it.
   * `activation: NOT ACTIVE: MCVS Offline's EngineBridge ran at … but the server did not start: …`: our files were
     used, the offline server did not start; the text after it is the error. Run `collect` and send it. (While the game
     is still running this reads `NOT VERIFIED: MCVS Offline's EngineBridge ran at …; the game is still running`: close
     the game and run `status` again.)
   * `activation: NOT ACTIVE: the game started without MCVS Offline: …\ModernCombatVersus.exe.config is missing (install
     did not finish)`: the install stopped before it wrote the config (`status` says `PARTIAL`). Run `install` again.

**The settings page.** Settings › Account › **MCVS Offline** (or the "MCVS Offline" message in the inbox) opens the
offline settings page: status, settings, saves (save now, export, import, backups), logs and restart. By default it
opens in the **Steam overlay**; when the overlay is turned off it opens in your **web browser** instead. If a click
showed nothing in the overlay, the next click opens the browser. The page also has **Open in the system browser**.
The setting `admin.openVia` in `McvsOffline\settings.json` picks the route: `auto` (default), `overlay`, `system`
(always the browser) or `game` (the game's own browser, after login).

**Steam achievements.** Real Steam achievements and stats unlock from offline progress, including whatever the
settings page's unlock tools grant. On a Steam account that never played MCVS on PC, the game's first start resets this
game's Steam stats (stock behaviour).

**Graphics.** Use the game's own menu: Settings › Graphics (quality Low, Mid or High, render scale, 30/60 FPS, Vsync,
MSAA, full screen). The graphics quality applies at the next start and overrides the settings page's
`graphics.profile` (keep that at `auto`). High uses more memory, and the game is a 32-bit program with 2 GB at most: if
it crashes soon after you chose High, go back to Mid. The `[boot] graphics:` lines and the `graphics:` part of the
`[status]` lines in `McvsOffline\logs\bootstrap.log` say which profile ran and what the game's menu was set to.

**Firewall.** Windows may ask about network access for the game: the game's own physics debug listeners
(`0.0.0.0:25001` and up) and the match's UDP socket. Allow or decline, both work for single-player.

**LAN play is not supported on PC.**

## 5. Uninstall

* `.\McvsOffline.PcKit.exe uninstall` removes the config, `McvsOffline\bin\`, `install.json` and the `args.txt` block.
  **Your data stays** in `McvsOffline\` (settings, saves, logs, exports).
* `.\McvsOffline.PcKit.exe uninstall --remove-data --yes` also deletes `McvsOffline\` (export your save first). The
  kit's log then continues in `%TEMP%\McvsOffline\kit\`.
* After a Steam update or "Verify integrity of game files", run `status`: it says when you need to uninstall and wait
  for a kit that knows the new build.
* **If you copied the files of `McvsOffline\bin` next to `ModernCombatVersus.exe`** (the work-around for build 14,
  whose config Windows most likely ignored): those copies replaced Steam's own files, and the kit cannot restore them.
  `status` names each copy (`Game.dll is MCVS Offline's copy from McvsOffline\bin (copied over the game's own file)`)
  and ends with `you copied MCVS Offline's DLLs over the game's own files; Steam > Verify integrity of game files
  restores them, then run install`. Its state is `STALE (…)`, or `DAMAGED (…)` while the build-14/15 config is still in
  place (the copies are listed first, then the old config). `install` and `uninstall` refuse until then (uninstall
  would delete the only proof of the copy and leave the game running it). To undo it: Steam › right-click Modern Combat
  Versus › Properties › Installed Files › **Verify integrity of game files** (it puts back `EngineBridge.dll`,
  `Game.dll`, `Gameloft.Engine.dll` and `Gameloft.Util.dll` and leaves `McvsOffline\` and the config alone), delete the
  `McvsOffline.Server.dll` you copied next to the exe (`status` lists it as a leftover; Steam keeps it; `uninstall`
  removes it too), then run `install` and start the game once.
* A config from build 14 or 15 (it has `<qualifyAssembly>` lines) makes `status` say `DAMAGED: ModernCombatVersus.exe.config
  has <qualifyAssembly> entries, which most likely make .NET ignore the whole file (…): an old build-14/15 config: run
  install again`. `install` replaces it; if that install fails before it writes the new config, it says the game will
  start WITHOUT MCVS Offline until install succeeds (the old config does not redirect it).
* `status` also reads the last start in `McvsOffline\logs\bootstrap.log`. `installed, NOT ACTIVE (Game loaded from the
  game folder)` (or `… the 'store' patch is missing`) means the files are installed but the game did not run them, so
  the offline fixes were missing in that start: close the game, run `install` again, start once, and if `status` still
  says NOT ACTIVE send `collect`. The settings page shows the same as a red banner, and the `[status]` line in
  `bootstrap.log` ends with `install: installed, NOT ACTIVE (…)`.

## 6. When something goes wrong

* `.\McvsOffline.PcKit.exe collect` makes one zip on your Desktop (`mcvs-offline-pc-<date>-<time>.zip`) with the logs,
  the kit's recordings, the settings, `install.json`, the config, a listing of the game folder, the game's crash
  logs and its Windows error events (times in UTC). It never takes your saves, accounts, the contents of Gameloft's id
  files or of `MyLocalStorage` (only their names, sizes and times), or the game's crash dump `core.dmp` (it can be
  2 GB: `summary.txt` gives its size and time). `summary.txt` and every command in the kit's log name the kit's
  version and its own SHA-256.
* Logs: `McvsOffline\logs\` (`bootstrap.log`, `server.log`, `game.log`); when the game folder is not writable,
  `%TEMP%\McvsOffline\logs\`. Debug output also goes to `OutputDebugString` (DebugView, or the kit's `watch`).
  Both folders are cleaned at every start: each start begins new log files (the previous start's are usually the `.1` files)
  and the logs of older starts are deleted (setting `logging.keepStarts`, default 1: this start and the previous one).
  So run `collect` before you start the game twice more.
* If the game misbehaves, close capture and overlay tools and try again.

### Crash and freeze dumps (WR3)

A dump is a snapshot of the game's memory. It shows us where the game was stuck or where it crashed. Dumps are large
(about 1.5 to 2 GB each), so `collect` never puts them in its zip. Its `summary.txt` and `dumps.txt` list each dump with
its path and size. Send the dumps separately, for example through a file-sharing link. Delete them after we have them.

* **Freezes.** While `watch` runs, it reads `McvsOffline\logs\bootstrap.log`. When the game has not run a frame for 60
  seconds (a `[watchdog] STALL` line), it saves one dump of the game to `%LOCALAPPDATA%\McvsOffline\dumps\`. The game
  pauses for a few seconds while the dump is written. `watch` saves one dump per freeze and at most 3 per run. It writes
  no dump when 6 dumps are already in that folder, or when the drive has less than 5 GB free. Keep `watch` running for
  the whole round.
* **Crashes: Windows' own crash dumps (once, needs an administrator).** Windows only keeps a full dump of a crashing
  game if you turn this on. Open **Command Prompt as administrator** (Start, type `cmd`, then "Run as administrator").
  Run these two commands exactly as written:

  ```
  reg add "HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\ModernCombatVersus.exe" /v DumpType /t REG_DWORD /d 2 /f
  reg add "HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\ModernCombatVersus.exe" /v DumpCount /t REG_DWORD /d 3 /f
  ```

  `DumpType 2` is a full dump. `DumpCount 3` keeps the last three. With no `DumpFolder` value, Windows writes them to
  `%LOCALAPPDATA%\CrashDumps\ModernCombatVersus.exe.<number>.dmp` of the user who played. `collect` lists them.
  To check the setting, run
  `reg query "HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\ModernCombatVersus.exe"`. To turn
  it off after the round, run
  `reg delete "HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\ModernCombatVersus.exe" /f`.
  This setting affects only this game.
* **Crashes: Windows' crash reports.** For every crash, Windows also keeps a small text report in a folder named
  `C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_ModernCombatVers_<…>`. Zip all of these folders and
  send the zip. In PowerShell (as administrator if it says access is denied):

  ```
  Compress-Archive -Path "C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_ModernCombatVers_*" -DestinationPath "$env:USERPROFILE\Desktop\mcvs-wer-reports.zip"
  ```

* After a crash, `McvsOffline\logs\` should hold a `crash-<time>-native.txt`. After a crash that the game logged
  first, it should also hold an `exception-<time>.txt` that starts with `PROBABLY FATAL`. `collect` includes both.
  The game's own `core.dmp` in the game folder is not needed. It holds only the last crash and is often empty.

## 7. WR3 checklist (what to record)

Write the answers into a text file (or the reply). "Expected" is what the Linux run predicts; anything else is a
finding, not a failure on your side. Step 4 decides the round: everything after it only counts when `Game` comes from
`McvsOffline\bin` and every patch says `yes`.

For every crash or freeze, also write down:

* the local time, and what you were doing just before (which screen, which button);
* for a freeze: whether the picture was frozen or still moving, how long you waited, and how you ended it (waited,
  Alt+F4, Task Manager);
* for a crash: whether Windows showed a message, and what it said.

Do the unlock codes and the unlock-all grant **only at step 8b**, after the bot match: in WR2 they caused the long
freezes, which would hide everything else.

| # | do | record | expected |
|---|---|---|---|
| 1 | `status --listing` (before install) | any leftovers it lists (an old config, `McvsOffline\spike\`, an `args.txt` block) | the listing file is written to `McvsOffline\kit\` (or `%TEMP%\McvsOffline\kit\`) |
| 2 | `probe` from a normal terminal | the http.sys line for `http://localhost:51750/`, the urlacl lines, port 51780, excluded port ranges, folder writable, the .NET Release value; the `GPU adapter:`, `display:`, `physical memory:` and `firewall rules naming ModernCombatVersus.exe:` lines | http.sys starts and stops without "access denied"; every GPU of the PC is listed with its memory |
| 3 | `install`, then `verify`, then `status` | the notes; the `written … sha256` lines of `verify` | `installed, current`; the six values in the table below, **exactly** |
| 4 | `watch` in a second window, then start the game from Steam; afterwards `status` | in `McvsOffline\logs\bootstrap.log`: the `[boot] runtime:`, `[boot] assemblies:` and `[boot] patches:` lines; the `state:` and `activation:` lines of `status` | `process x86, LAA no`, `Release` 528040 or higher (4.5.2 at least: 379893); every assembly from `McvsOffline\bin`, **`Game` included** (`assemblies: Game …\McvsOffline\bin\Game.dll`); every patch `yes` (`gameload yes`, `store yes`, `inbox yes`); no `NOT ACTIVE` anywhere; `status`: `installed, current`, `activation: active at the last start` |
| 5 | reach the main menu | `[pc] quicklaunch:` lines; any `headless world:`, `server world:` or `component … not created` line; the `[boot] graphics:` line | LocalAnubis started and a room; the thread id equals the game thread; no headless/server-world lines; `MEM rule on, removed 1`; `excludedLoadingLayers` without `Physics` |
| 6 | open the settings page: Settings › Account › MCVS Offline (WR2 could not show it); then the inbox message; then the page's "Open in the system browser"; then `admin.openVia` = `system`, then `game` (restart the game each time); back to `auto`: once fullscreen, once windowed, once with Steam › Settings › In Game › "Enable the Steam Overlay while in-game" off | for each: where the page appeared and whether Status loads; the `admin: page opened … via …` line in `server.log` | the overlay shows the page in both window modes; with the overlay off: `via system` and the browser opens; `game` works after login; if `via overlay` ever shows nothing, the next click logs `via system … overlay showed no page last time` |
| 7 | on the page: Saves › Save the game now; Saves › Export (Explorer opens `McvsOffline\exports`); Logs › share; copy a text. Then with `admin.openVia` = `system` and the game **minimised** behind the browser: Save the game now | whether each works; whether "Save now" finishes while the game is minimised | all work |
| 8 | play one bot match to the end, started from **Battle › Classic multiplayer** (the game has no visible bot-match tile), with `watch` running, before any unlock code | bots and physics (nobody falls through the floor), rewards saved; `[pc] quicklaunch: automatch through the local Finder` | the match runs and ends normally; `automatch through the local Finder` appears (in WR2 it never did) |
| 8b | now redeem one big unlock code (`UNLOCKALL`) and time how long the game takes to answer; open the inbox; stay in the menus until the items stop arriving (about 30 rewards popups over a few minutes); then turn on the unlock-all grant once | the time; whether the game says the code was redeemed; what the inbox shows; any freeze or `STALL` during the popups (and whether `watch` printed that it saved a dump); the `unlock code UNLOCKALL: … done, … keys written in … batch(es)` line in `server.log` | the game answers at once and says the code was redeemed; the inbox shows one empty Coupon message and opens at once (WR2 crashed there twice with the old full message); the batches arrive with a popup each; the `done` line appears; a freeze of a minute or more leaves a dump in `%LOCALAPPDATA%\McvsOffline\dumps\` |
| 9 | during the match: minimise and restore; open the in-game menu; open the Steam overlay (Shift+Tab) | pauses, cursor in the menu and on the end screen | one pause each time, cursor visible |
| 10 | in the game's options: toggle fullscreen twice | crash? setting kept? | no crash, kept |
| 11 | firewall prompts (when, for what); `watch`'s endpoint lists | prompts and listeners | listeners: `0.0.0.0:25001` and up, the match's UDP socket, the Finder on 51750 |
| 12 | `watch` memory in the menu and through a whole match, once with the game's Settings › Graphics quality at Low and once at High (it applies at the next start); the `memory:` part of the `[status]` lines. Then set `graphics.profile` to `high` on the settings page and restart, once without and once after choosing a quality in the game's menu | private bytes, virtual size, % of 2048 MB, the highest value in each match; the `[boot] graphics:` lines | below 100 %; say which quality you played at when the numbers were highest |
| 13 | defaults: open the in-game store, buy one item with soft currency, open every news/promo screen the menu offers; look at `server.log`. Then once with `embedded.nativeOnlineSdk: true` in `settings.json` | hangs, error popups, crashes; `GlWebTools` or `connectionstatus` requests in `server.log`; with the SDK on: tracker posts and their log lines | no hang or crash; no `GlWebTools`/`connectionstatus` with the default; with the SDK on, each tracker post gets one INFO line and no MAC address appears in any log |
| 14 | Steam achievements: note one stat on the overlay's achievements page, open one crate (or note the first login of the day), compare | how far the stat moved | exactly one step |
| 15 | settings page › Restart; then once more with `admin.restartVia: host` | does the game close and come back through Steam within about 10 s? `logs\game.log` present? which exit path the log names | it comes back each time; `game.log` exists; `ExitAppAndroid` (or the host's kill with `host`) |
| 16 | `collect`, then `uninstall`, then `status --listing` | the zip; `listing-diff.txt` in the kit folder | differences only: `McvsOffline\` (your data) and the game's own files (`dr0.bin`..`dr3.bin`, `OF_PP.bin`, `GameVersion`, `core.dmp` with its size and time, or other entries with a game-run time); no `args.txt`, no config, no `*.mcvs-tmp` |
| 17 | optional, on a copy of the game folder or after exporting the save: `uninstall --remove-data --yes` | what is left | no `McvsOffline\`; the kit log continued in `%TEMP%\McvsOffline\kit\`; no `%TEMP%\mcvs-pckit-*` folder |
| 18 | optional: Steam › Verify integrity of game files while installed, then `status` | the state | still `installed, current` |
| 19 | question: had this Steam account played MCVS on PC before this project? | yes / no | — |

Expected `verify` values for step 3 (build 17: the Linux run of the same kit on Steam build 6372049; `install.json`
itself is not listed because it holds the install time):

| file | sha256 |
|---|---|
| `McvsOffline/bin/EngineBridge.dll` | `5c2dd7e2ef9e1bf937f574150189d5dcbc8921fd52ffd34c62feb9bec35eaf13` |
| `McvsOffline/bin/Game.dll` | `39b80ecb84b08c62714bb7b80f8f7da626023e307d6e370662ccc9e589a21618` |
| `McvsOffline/bin/Gameloft.Engine.dll` | `c4d37967d4c18efb8dd54ac199d264121e7d28dd66979517a4f1dac32e68e5d3` |
| `McvsOffline/bin/Gameloft.Util.dll` | `40c1792b3c4739821b6022ef81d6b514e6a37cf49a4103c184a2fdf9d1e399ba` |
| `McvsOffline/bin/McvsOffline.Server.dll` | `3fe6146ab50ec93d74ba0c0e1d682d92c9ef88df088ed64c98996bb29d9a6478` |
| `ModernCombatVersus.exe.config` | `3ebe99b3c93a9540cd7f7b03da55fc048a544501c17e8621457884eae456f94c` |

Any difference in this table stops the round until it is explained: the bytes you run must be the bytes we tested.
(The config is the one WR2 ran and build 16 wrote, with no `<qualifyAssembly>` lines: builds 14 and 15 wrote
`fda12aef…c4b6`, which has five of them. `Game.dll` changed with the new `inbox` patch, the other DLLs with the server.
An install over build 16 writes exactly these values too (checked on Linux). Over a build-16 install, `status` says
`installed, current` with `installed by: … (server build 16)` until you run `install`: run it.)

## 8. What to send back

* the `collect` zip from step 16 (on your Desktop);
* the `watch` folder (`McvsOffline\kit\watch-<date>\`; with `--remove-data` it is gone, so copy it first);
* the dumps that `collect` lists in `dumps.txt` (`%LOCALAPPDATA%\McvsOffline\dumps\`, `%LOCALAPPDATA%\CrashDumps\`)
  and the zip of the `AppCrash_ModernCombatVers_*` folders (section 6), each sent separately;
* the two listings and `listing-diff.txt` from `McvsOffline\kit\` (or `%TEMP%\McvsOffline\kit\`);
* your checklist notes (section 7), with the times and descriptions of every crash and freeze;
* answers to the open WR2 questions, if you remember: did you set the game's Graphics quality to High during the first
  WR2 session and back to Low later; which graphics card(s) Task Manager › Performance shows and which one the monitor
  is plugged into; did you edit `settings.json` by hand to turn on `unlockAll`, or reset your save (your level went
  back to 1 around 14:15)?
