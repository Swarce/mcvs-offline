# Player setup: building your offline Modern Combat Versus

**Status: in development.** The patcher and the embedded server are built and checked offline (signature,
alignment, patched code). The first device test (October 2026) got as far as the in-process game server loading its
level; the problems it found are fixed in the current build, which also writes detailed logs you can read on the phone
(section 6). Expect rough edges, and keep your backups.

This guide turns **your own** copy of Modern Combat Versus for Android (APK **1.17.32**, package
`com.gameloft.android.ANMP.GloftMVHM`) into an offline build. The game keeps its own match servers; MCVS Offline adds
the missing Gameloft backend ("Federation") inside the game process. Nothing from the game is downloaded from this
project or redistributed: the patcher works on your APK, and the server reads your data pack on your phone.

You need:

- an Android phone: tested on 64-bit ARM (arm64-v8a); the build also keeps the 32-bit libraries (untested);
- a PC (Linux, macOS or Windows) with the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0);
- `adb` (Android platform-tools) and USB debugging enabled on the phone;
- optional: Android **build-tools** (`apksigner`, `zipalign`) and Java. Without them the patcher signs with its own
  built-in signer (v1+v2), which is enough for this game.

## 1. Get your APK

With the original game installed on your phone:

```sh
adb shell pm path com.gameloft.android.ANMP.GloftMVHM     # prints package:/data/app/.../base.apk
adb pull /data/app/<...>/base.apk mcvs.apk
```

If `pm path` prints more than one line (split APKs), this setup does not support your install yet: the patcher needs
the single APK that contains both `assemblies/` and `lib/`. The patcher checks the APK and refuses anything that is not
the 1.17.32 build it was verified against.

Keep `mcvs.apk` safe and unmodified. It is your only source for future patches.

## 2. Back up the game data before you uninstall anything

The game downloads about 1.5 GB of data on first launch into
`/sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/Data`. Android deletes that whole folder when the
game is uninstalled, and the patched APK can only be installed after uninstalling the original (it is signed with a
different key). Back it up first:

```sh
adb pull /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files mcvs-files-backup
```

On Android 11 and later, file managers on the phone cannot open `Android/data`; use `adb` (or a PC file transfer that
shows the folder).

Gameloft's download servers still worked when this was written (October 2026), so the game can also download the data
again. They will not stay up forever, so keep your backup, and consider archiving the data with
`tools/cdn-mirror` (it also checks a backup against Gameloft's own CRC index). Restoring a backup or a mirror needs no
internet with the offline build: [INSTALL-OFFLINE.md](INSTALL-OFFLINE.md).

## 3. Build the server for your game

The in-game server is compiled against the libraries inside **your** APK (they are never stored in this repository):

```sh
unzip -o mcvs.apk 'assemblies/*' -d mcvs-apk          # or extract assemblies/ with any zip tool (7-Zip on Windows)
MCVS_REFS="$PWD/mcvs-apk/assemblies" scripts/build.sh
```

This builds everything, runs the tests and produces
`artifacts/bin/McvsOffline.Server.Embedded/release/McvsOffline.Server.dll`.

## 4. Patch

```sh
dotnet artifacts/bin/McvsOffline.Patcher/release/McvsOffline.Patcher.dll \
    --apk mcvs.apk \
    --server artifacts/bin/McvsOffline.Server.Embedded/release/McvsOffline.Server.dll \
    --out mcvs-offline.apk
```

The patcher prints every change it makes and finishes with a signature check. Useful options
(`--help` lists them all):

| Option | Meaning |
|---|---|
| `--patch nativeonline,waits,geo,bottile,finder,store,signatures` | The optional patches (this is the default set). `none` keeps only the mandatory patches (the server hook, the headless-server fixes and the server physics), `all` adds `lan`. |
| `--game-log all` | The game's own log at every level in logcat (`mono-stdout`), for bug reports; this is the default while the offline build is being tested. `--game-log fatal` keeps the game's original setting (fatal errors only). |
| `--keystore <file.p12>` | Your signing key. Without it a key is created once in `~/.config/McvsOffline/` (Windows: `%APPDATA%\McvsOffline\`) and reused. |
| `--build-tools <dir>` | Folder with `apksigner` and `zipalign`; otherwise they are looked up on `PATH` and in `$ANDROID_HOME/build-tools`. |
| `--signer builtin` | Do not use apksigner, even if it is installed. |
| `--abi arm64-v8a` | Native libraries to keep. The default keeps **every ABI** the APK ships (64-bit `arm64-v8a` and 32-bit `armeabi-v7a`). `--abi arm64-v8a` drops the 32-bit copies for a 64-bit-only APK (about 14 MB smaller; a 64-bit phone never uses them). |
| `--dry-run` | Check that every patch site matches, write nothing. |

What the patches do:

- **headless** (always applied): the game's own match and menu servers run inside your game as "headless" worlds. A few
  of the game's components were never meant to run there (the controls settings, the gamepad bindings, two weapon
  effects, the sound manager); in the original build any error in such a server world closes the whole game. The patch
  keeps those components out of the server worlds and turns any other error there into a log line
  (`[McvsOffline] headless world: ...`) instead of a crash.
- **physics** (always applied): the game runs its physics engine (Havok) on several threads, and every physics world in
  the process shares one set of worker threads that can serve only one world at a time. Gameloft's servers ran each
  server world alone, with single-threaded physics; inside your game the menu server's physics and the game's own use
  those threads at the same moment. That is the most likely reason the game froze on "connecting" in device test 2, but
  a device test has not confirmed it yet. The patch gives the server worlds single-threaded physics, as on Gameloft's
  servers, and lets only one world step its physics at a time. The `embedded.physics` settings can undo each part for
  testing.
- **nativeonline**: the game's native online library (Gaia: Gameloft's native service clients, tracking, ads,
  cross-promotion) is not started. With the internet on, it tried to reach Gameloft's servers and froze the loading screen. Offline play
  does not use it; set `"nativeOnlineSdk": true` under `embedded` in `settings.json` to start it anyway.
- **waits**: a safety measure; no test has needed it so far. The engine's "wait and keep working" helper checks for new
  work every 100 ms instead of only when it is woken. In the game's code, two rare situations do not wake it. This patch
  limits the delay to 100 ms in both. It changes nothing else.
- **geo**: the game looks up your country at a hard-coded Gameloft address while it connects to its own server. Offline
  that lookup can hang longer than the connection timeout; the patch sends it to the embedded server.
- **bottile**: the BOTMATCH tile in the menu normally starts a "free" bot match that pays almost nothing. With the
  patch it starts the same bot-filled match as "Battle", with full rewards and progression (after the tutorial).
- **finder**: the game's matchmaking service listens on all network interfaces instead of the one that was active at
  start-up, so switching Wi-Fi or airplane mode on and off does not break matchmaking until a restart.
- **store**: opening the shop crashed the game because of the kill-signature sounds it sells (the game's availability
  check had no code for sounds). With the patch sounds work, and any shop item whose check still fails is left out of the
  shop (and logged as `[McvsOffline] store item <id> threw ...`) instead of closing the game.
- **lan** (experimental): groundwork for LAN play (see the end of this guide). It changes nothing for single-device
  play.

**Back up your signing key** (the patcher prints its path). Android only installs an update over an existing install
when it is signed with the same key, and the alternative, uninstalling, deletes your saves.

## 5. Install

```sh
adb uninstall com.gameloft.android.ANMP.GloftMVHM        # removes the original game AND its data folder
adb install mcvs-offline.apk
```

Then either let the game download its data again (needs internet once), or restore your backup **before** the first
launch:

```sh
adb shell mkdir -p /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files
adb push mcvs-files-backup/. /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/
```

If the game later cannot read the restored files (Android versions differ in how they treat files pushed by `adb`),
launch the game once so Android creates the folder itself, force-stop it, and push again.

A restored backup needs no internet: before the game's installer runs, the offline build gives it the index of the
data (the copy it keeps in `files/McvsOffline/install/pack.info`, or one rebuilt from a complete `Data/` folder) and the
installer's other markers, so the installer accepts the files instead of going to Gameloft's CDN. A fresh install from a
mirror made with `tools/cdn-mirror` works the same way. Details, the checks it makes and what to do when the installer
screen stalls: [INSTALL-OFFLINE.md](INSTALL-OFFLINE.md).

Later updates of the offline build install over the existing one, keeping everything, as long as you use the same key:
`adb install -r mcvs-offline.apk`.

**Other Gameloft games on the same phone.** The original game declares a permission that every Gameloft game shares
(`glshare.permission.ACCESS_SHARED_DATA`). Android refuses to install an app that redeclares a permission owned by an
app signed with a different key (`INSTALL_FAILED_DUPLICATE_PERMISSION ... already owned by com.gameloft...`), so the
patcher renames it to `com.gameloft.android.ANMP.GloftMVHM.mcvs.glshare.permission.ACCESS_SHARED_DATA`: the offline
build installs next to Asphalt, Modern Combat 5, N.O.V.A. and the others, and they can still be installed afterwards.
If you see that error anyway, the APK was patched by an older version of the patcher: patch your original APK again.

## 6. First launch

- The offline server starts before anything else in the game. On the first launch it creates
  `.../files/McvsOffline/` and writes `.../files/args.txt`, which makes the game use its built-in ("Embedded") servers
  and the local backend at `http://127.0.0.1:51780/`.
- The game's own installer screen still appears. With the data already on the phone it only checks the files and
  starts the game, with or without internet; with no data it downloads it as usual
  ([INSTALL-OFFLINE.md](INSTALL-OFFLINE.md)).
- The first launch and the first match are slower than with the original APK: the patched libraries run without
  their precompiled (AOT) code and are compiled on the phone as they are used.
- Google Play Games and Facebook sign-in no longer work (they are tied to Gameloft's signature, and their servers are
  gone anyway). The game's own signature self-check is left as it is: its result is only recorded, nothing on the
  phone acts on it.

### Unlock codes (optional)

Settings › Account › Redeem code. Each code works once per profile; add a number to redeem again (`UNLOCKALL2`). The
rewards arrive as one inbox message and are claimed in order.

| Code | What you get |
|---|---|
| `UNLOCKALL` | everything that was released: every agent and all their mods, weapons, gear slots, kill signatures, camos, jerseys, skins (about 11,400 rewards). Unlocking every mod raises your level to about 46 at once; the game then saves once per level and lags for a while. Let it finish. |
| `UNLOCKMERCS`, `UNLOCKWEAPONS`, `UNLOCKGEARSLOTS`, `UNLOCKSIGNATURES`, `UNLOCKCAMOS`, `UNLOCKACCESSORIES`, `UNLOCKSKINS` | the same in smaller parts (all but signatures unlock the agents first) |
| `UNLOCKPAID` | only what the original game sold for real money and nothing else gave out: the VIP-only camos and jerseys (about 5,600 rewards, agents first). `UNLOCKPAIDCAMOS` / `UNLOCKPAIDACCESSORIES` split it. |
| `UNLOCKUNRELEASED` | content that is in the game's data but was never released: 6 kill-signature emblems. Strictly optional; never part of UNLOCKALL, `unlockAll` or the shop. |

Redeeming an agent code again when you already own the agents gives the game's usual duplicate compensation (random
gear) for each one. `settings.json` `"unlockAll": true` grants the same as `UNLOCKALL` without a code. Details and the
exact item lists: [ECONOMY.md](ECONOMY.md).

### Logs, if something goes wrong

**On the phone, without a PC** (Android 11 and later): open the **Files** app (or Samsung **My Files**) > **Downloads** >
**MCVS-Offline**. Every game start writes its own copies there, named after the start time:
`<date>-<time>-bootstrap.log.txt`, `-server.log.txt`, `-game.log.txt`, plus `crash-*.txt` when the game crashed. The
copies are refreshed every 30 seconds while the game runs, and the last five starts are kept. Send the files of the
newest start with your bug report. (If the folder is missing, the start-up log says why: look for `[mirror]`.)

**The full system log with LogFox** (no PC; LogFox needs the `READ_LOGS` permission, granted once with adb or Shizuku,
see its own instructions):

1. Developer options > **Logger buffer sizes** > **16M** (otherwise the start of a session is gone after a few minutes).
2. In LogFox, start recording, filter on the game (the app or its UID), then launch the game.
3. If the game hangs on a loading screen, **wait at least 60 seconds** before closing it: the watchdog writes what the
   game is waiting for after 5, 15 and 60 seconds.
4. Export the log and send it with the files from Downloads > MCVS-Offline.

Useful tags: `McvsOffline` (our lines: `[boot]` milestones, `[http]` requests, `[watchdog]`, `[status]`, `[crash]`) and
`mono-stdout` (the game's own log; with `--game-log all` at every level).

**With a PC:**

```sh
adb logcat -s McvsOffline mono-stdout        # live, while the game runs
adb pull /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline/logs
adb shell ls -l /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline/logs
```

In `McvsOffline/logs/`: `bootstrap.log` covers the start-up, the boot milestones, the watchdog and the status lines;
`server.log` every request; `game.log` a copy of the game's own log; `unknown-endpoints.jsonl` the requests the server
does not handle yet; `crash-*.txt` one file per crash (always written for the crash itself); `exception-*.txt` the
first 5 background errors that did not stop the game (unobserved task faults, mostly harmless); `exit-*` the system's
record of a previous crash or freeze. If a log
file cannot be written, logcat says so (`[logfile] cannot write ... at stage ...`) and the file continues in the game's
internal storage. Copying the folder over USB with a file manager on the PC can show stale sizes while the game runs;
the copies in Downloads do not have that problem.

## 7. Saves, settings and backups

Everything MCVS Offline stores is in one folder:

```
/sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline/
    settings.json        your settings (see below)
    accounts.json, ...   your account and profile: progression, inventory, currencies
    device-id.txt        the device id passed to the game
    logs/
```

Back it up regularly, and always before uninstalling:

```sh
adb pull /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline mcvs-save-backup
```

To restore, close the game (Settings → Apps → Force stop) and push it back:

```sh
adb push mcvs-save-backup/. /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline/
```

`settings.json` is created with defaults on the first launch. Edit it (pull, edit, push) and force-stop the game for
changes to apply. If the file has a syntax error (a missing comma, say), the game runs with the defaults and leaves your
file exactly as it is: `logs/bootstrap.log` says so; fix the file and restart. Shared keys are documented in `docs/ARCHITECTURE.md` §3 ("Settings"); the ones that belong to the
in-game host:

| Key | Default | Meaning |
|---|---|---|
| `port` | `51780` | Port of the local backend. If you change it, re-patch with the same `--port` (the `geo` patch contains it). |
| `lanMode` | `false` | Experimental LAN host mode (see below). |
| `embedded.manageArgs` | `true` | `false` stops MCVS Offline from touching `args.txt`. |
| `embedded.lanAdvertiseHost` | `""` | LAN host mode: the IP address to announce to other devices (empty = detect). |
| `embedded.nativeOnlineSdk` | `false` | `true` starts the game's native online library again (see the `nativeonline` patch). |
| `gameLog.levels` | `"error warning"` | Levels of the game's own log (`error warning info debug`). A build patched with `--game-log all` logs every level anyway. |
| `gameLog.mute` | `"profile:debug"` | `category:level` pairs to silence, e.g. `online:debug`; `category:all` silences a category. |
| `gameLog.file` | `true` | Copy the game's log to `logs/game.log`. |
| `embedded.physics.serverMultithreaded` | `false` | `true` gives the in-process server worlds the game's multithreaded physics again (the suspected cause of the device-test-2 freeze; for testing only). |
| `embedded.physics.clientMultithreaded` | `true` | `false` makes the game's own physics single-threaded too. |
| `embedded.physics.stepGate` | `true` | `false` lets several worlds step their physics at the same time again (for testing only). |
| `embedded.physics.stepGateTimeoutMs` | `2000` | How long one world's physics waits for another's before it goes ahead anyway (logged). |
| `diagnostics.watchdog` | `true` | Report what the game waits for when it stops responding. |
| `diagnostics.threadDump` | `true` | When the game has not responded for 15 s (and again at 180 s), write where every thread is (managed and native stacks) to the log and `logs/threaddump-*.txt`. |
| `diagnostics.publicLogCopy` | `true` | Copies of the logs in Downloads > MCVS-Offline (Android 11+). |

### args.txt

`.../files/args.txt` holds the game's launch arguments. MCVS Offline rewrites only the lines between
`-McvsOfflineBegin` and `-McvsOfflineEnd` (the file has no comment syntax, so the markers are arguments the game
ignores). Add your own arguments **outside** that block; an argument you set there replaces the managed one. Rules
the game imposes (verify-C1): separate words with spaces or new lines only (no tabs), save without a byte order mark,
and keep the exact spelling of values such as `Embedded`.

If you used the development loop before ([DEV-LOOP.md](DEV-LOOP.md)), delete its `args.txt` before the first start of
the offline build: its lines sit outside the managed block, so they would keep overriding the managed values.

## 8. LAN play (experimental, not working end-to-end yet)

Patching with `--patch all` (or `default,lan`) adds the pieces a LAN host and its guests need on the game side
(verify-C6 P1-P3). The backend part (guests using the host's backend, roadmap M3) is still being built, so this is
for testing only:

- host: set `"lanMode": true` in `settings.json`; the backend then listens on all interfaces and the game's own
  servers accept other devices;
- guest: add these lines outside the managed block of the guest's `args.txt`, with `<host-ip>` the host's Wi-Fi
  address:

  ```
  -ServerLocation Remote
  -RemoteIp <host-ip>
  -EnvironmentSwitcherUrl http://<host-ip>:51780/
  ```

## What the patcher changes, exactly

For reviewers (details and evidence: `docs/research/verify-C7-integrity-patching.md`, `verify-C4`, `verify-C6`):

- `assemblies/NicePlaySDK.dll`: `NicePlaySDKDLL.OnInitialize`, which the game calls very early, first runs
  `try { McvsOffline.Bootstrap.Init(); } catch { }`, then its original body.
- `assemblies/McvsOffline.Server.dll` is added (uncompressed and aligned like every other assembly).
- Always: `Game.dll` and `Gameloft.Engine.dll` (headless, headless-engine: client-only components kept out of the
  in-process server worlds, null-safe gamepad and weapon-effect calls, and a try/catch that applies to server worlds
  only; physics: `PhysicsComponent`'s constructor asks `McvsPhysics.UseMultithreading(this)` instead of reading the
  game option, and `UpdateSimulationSteps` runs inside a process-wide, time-bounded gate). Optional: `Gameloft.Engine.dll`
  (nativeonline, waits: `WaitHandle.WaitAny(handles)` -> `WaitAny(handles, 100)` in both `TaskExtension.WaitAndWork`
  overloads, finder, lan), `Gameloft.Util.dll` (geo), `Game.dll` (bottile; store: `KillSignatureRewardItem.GetAvailableId` handles sounds, the `GameUnifiedStoreComponent.SelectItems` availability filter skips an item that throws; signatures: set lookups in `GetSignatureAssetsList`, one list per `GetKillSignItems` call). Each site is checked against the exact code
  of 1.17.32 first (the device-test-2 patches against an IL fingerprint of the whole method); if anything differs, the
  patcher stops without writing.
- `res/raw/args.txt` (with `--game-log all`, the default): `-LogCategoryFilter Fatal` is replaced by a line the game
  ignores, so the game logs every level. The patcher refuses if the file is not the shipped one.
- The precompiled AOT images (`lib/<abi>/libaot-<name>.dll.so`, every ABI kept) of every modified assembly are removed, so
  the runtime compiles the patched code instead. Other AOT images that depend on a modified assembly are rejected by
  the runtime and fall back the same way.
- The six assemblies whose identity is baked into the game's native code (`XaLaunchANMP`, `Mono.Android`,
  `AdjustSdk.Xamarin.Android`, `BindAF`, `BindUnityAds`, `BindGPSrv-auth-base`) are never modified.
- `AndroidManifest.xml`: the vendor-wide signature permission `glshare.permission.ACCESS_SHARED_DATA` (declared,
  used, and required by the game's `KeyProvider`) is renamed into the game's own namespace, so the re-signed APK does
  not collide with other Gameloft games (section 5). Only that one string of the compiled manifest changes; the patcher
  refuses if the text is used for anything else.
- Every other entry is copied byte for byte with its original compression; the old signature files are replaced by
  yours (v1+v2, plus v3 with apksigner).
