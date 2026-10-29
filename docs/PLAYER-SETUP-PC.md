# Player setup: MCVS Offline on the Steam (PC) version

**Status: draft for the second Windows round (WR2), build 13.** The PC port is built and checked on Linux against the
real Steam files (patches, installer, uninstall, ILVerify), but it has never run on Windows yet. This round is that first
run. Follow the steps in order, write down what the checklist in section 7 asks for, and send back the files of
section 8. If anything looks wrong, stop and send what you have; nothing here can damage the Steam install (see section 3).

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
   `c608fd1903b83763657646482a0c7aee6ec8282e10f63aa237dcb753b2e8715d` (PowerShell:
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
   this round.
2. `.\McvsOffline.PcKit.exe status --listing` (records the game folder **before** install).
3. `.\McvsOffline.PcKit.exe probe` (from the normal terminal; it checks the local ports, the folder and .NET).
4. `.\McvsOffline.PcKit.exe install`, then `.\McvsOffline.PcKit.exe verify`. `status` should now say
   `installed, current`.
   * "`<name>_args.txt` is in the game folder": the game would read that file instead of `args.txt` and go online.
     Move it out of the game folder and run `install` again.
   * "a ModernCombatVersus.exe.config that is not ours exists": move it away and run `install` again.
5. In a second terminal: `.\McvsOffline.PcKit.exe watch` (records memory, debug output and network endpoints until the
   game exits or you press Ctrl+C).
6. Start the game **from Steam**, as usual. Do not start it under a debugger: the game then shows a dead window.

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

## 7. WR2 checklist (what to record)

Write the answers into a text file (or the reply). "Expected" is what the Linux run predicts; anything else is a
finding, not a failure on your side.

| # | do | record | expected |
|---|---|---|---|
| 1 | `status --listing` (before install) | any leftovers it lists (an old config, `McvsOffline\spike\`, an `args.txt` block) | the listing file is written to `McvsOffline\kit\` (or `%TEMP%\McvsOffline\kit\`) |
| 2 | `probe` from a normal terminal | the http.sys line for `http://localhost:51750/`, the urlacl lines, port 51780, excluded port ranges, folder writable, the .NET Release value; the `GPU adapter:`, `display:`, `physical memory:` and `firewall rules naming ModernCombatVersus.exe:` lines | http.sys starts and stops without "access denied"; every GPU of the PC is listed with its memory |
| 3 | `install`, then `verify`, then `status` | the notes; the `written … sha256` lines of `verify` | `installed, current`; the six values in the table below, **exactly** |
| 4 | `watch` in a second window, then start the game from Steam | in `McvsOffline\logs\bootstrap.log`: the `[boot] runtime:`, `[boot] assemblies:` and `[boot] patches:` lines | `process x86, LAA no`, `Release` 528040 or higher (4.5.2 at least: 379893); every assembly from `McvsOffline\bin`; every patch `yes` |
| 5 | reach the main menu | `[pc] quicklaunch:` lines; any `headless world:`, `server world:` or `component … not created` line; the `[boot] graphics:` line | LocalAnubis started and a room; the thread id equals the game thread; no headless/server-world lines; `MEM rule on, removed 1`; `excludedLoadingLayers` without `Physics` |
| 6 | open the settings page: Settings › Account › MCVS Offline; then the inbox message; then the page's "Open in the system browser"; then `admin.openVia` = `system`, then `game` (restart the game each time); back to `auto`: once fullscreen, once windowed, once with Steam › Settings › In Game › "Enable the Steam Overlay while in-game" off | for each: where the page appeared and whether Status loads; the `admin: page opened … via …` line in `server.log` | the overlay shows the page in both window modes; with the overlay off: `via system` and the browser opens; `game` works after login; if `via overlay` ever shows nothing, the next click logs `via system … overlay showed no page last time` |
| 7 | on the page: Saves › Save the game now; Saves › Export (Explorer opens `McvsOffline\exports`); Logs › share; copy a text. Then with `admin.openVia` = `system` and the game **minimised** behind the browser: Save the game now | whether each works; whether "Save now" finishes while the game is minimised | all work |
| 8 | play one bot match to the end | bots and physics (nobody falls through the floor), rewards saved; `[pc] quicklaunch: automatch through the local Finder` | the match runs and ends normally |
| 9 | during the match: minimise and restore; open the in-game menu; open the Steam overlay (Shift+Tab) | pauses, cursor in the menu and on the end screen | one pause each time, cursor visible |
| 10 | in the game's options: toggle fullscreen twice | crash? setting kept? | no crash, kept |
| 11 | firewall prompts (when, for what); `watch`'s endpoint lists | prompts and listeners | listeners: `0.0.0.0:25001` and up, the match's UDP socket, the Finder on 51750 |
| 12 | `watch` memory in the menu and in the match; the `memory:` part of the `[status]` lines | private bytes, virtual size, % of 2048 MB | below 100 % |
| 13 | defaults: open the in-game store, buy one item with soft currency, open every news/promo screen the menu offers; look at `server.log`. Then once with `embedded.nativeOnlineSdk: true` in `settings.json` | hangs, error popups, crashes; `GlWebTools` or `connectionstatus` requests in `server.log`; with the SDK on: tracker posts and their log lines | no hang or crash; no `GlWebTools`/`connectionstatus` with the default; with the SDK on, each tracker post gets one INFO line and no MAC address appears in any log |
| 14 | Steam achievements: note one stat on the overlay's achievements page, open one crate (or note the first login of the day), compare | how far the stat moved | exactly one step |
| 15 | settings page › Restart; then once more with `admin.restartVia: host` | does the game close and come back through Steam within about 10 s? `logs\game.log` present? which exit path the log names | it comes back each time; `game.log` exists; `ExitAppAndroid` (or the host's kill with `host`) |
| 16 | `collect`, then `uninstall`, then `status --listing` | the zip; `listing-diff.txt` in the kit folder | differences only: `McvsOffline\` (your data) and the game's own files (`dr0.bin`..`dr3.bin`, `OF_PP.bin`, `GameVersion`, `core.dmp` with its size and time, or other entries with a game-run time); no `args.txt`, no config, no `*.mcvs-tmp` |
| 17 | optional, on a copy of the game folder or after exporting the save: `uninstall --remove-data --yes` | what is left | no `McvsOffline\`; the kit log continued in `%TEMP%\McvsOffline\kit\`; no `%TEMP%\mcvs-pckit-*` folder |
| 18 | optional: Steam › Verify integrity of game files while installed, then `status` | the state | still `installed, current` |
| 19 | question: had this Steam account played MCVS on PC before this project? | yes / no | — |

Expected `verify` values for step 3 (the Linux run of the same kit on build 6372049; `install.json` itself is not
listed because it holds the install time):

| file | sha256 |
|---|---|
| `McvsOffline/bin/EngineBridge.dll` | `1d669508abc805528e88454338cd1f3ba0dced4f87d0d6e24aaa8aec28652e04` |
| `McvsOffline/bin/Game.dll` | `f511c72b100d7cd1ea7b98339a4330ea7ae4491c811145ad61206b5ee31ae278` |
| `McvsOffline/bin/Gameloft.Engine.dll` | `5c21dc66bbbe3d6414ae6910cbf2723ec1345443e09208599a4f63d02e11abf9` |
| `McvsOffline/bin/Gameloft.Util.dll` | `3d4926d36b27c88b456c6613d1a914c2c9ac7f6bd44dd9a0a72caea34bf8abbd` |
| `McvsOffline/bin/McvsOffline.Server.dll` | `6303a789f9724c7fb3bcf16f44a4071fb2fb7e52f3f7c9901e41b1f66b243dd5` |
| `ModernCombatVersus.exe.config` | `3ebe99b3c93a9540cd7f7b03da55fc048a544501c17e8621457884eae456f94c` |

Any difference in this table stops the round until it is explained: the bytes you run must be the bytes we tested.

## 8. What to send back

* the `collect` zip from step 16 (on your Desktop);
* the `watch` folder (`McvsOffline\kit\watch-<date>\`; with `--remove-data` it is gone, so copy it first);
* the two listings and `listing-diff.txt` from `McvsOffline\kit\` (or `%TEMP%\McvsOffline\kit\`);
* your checklist notes (section 7).
