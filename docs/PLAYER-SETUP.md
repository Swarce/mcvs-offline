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
| `--patch nativeonline,waits,geo,bottile,finder,store,signatures,inbox,debloat,adminpage,adminlink,graphics,targetsdk,game` | The optional patches (this is the default set; `debloat` removes dead ad/analytics/social/push pieces, see ARCHITECTURE.md §2; `adminpage` and `adminlink` open the MCVS Offline page, section 7; `graphics` is needed for the graphics profile, section 7; `targetsdk` raises the app's target Android version, see `--target-sdk`; `game` marks the app as a game for Android's Game Mode, section 6). `none` keeps only the mandatory patches (the server hook, the headless-server fixes and the server physics), `all` adds `lan` and `forum`, `default,forum` adds only `forum`. |
| `--target-sdk 29` | The Android version the app declares it was built for (`targetsdk` patch). The default, 35, avoids Play Protect's "built for an older version of Android" warning up to Android 17; 36 also covers Android 18. For both the patcher also fixes the game's own Java code (`classes.dex`) where a newer target would make Android refuse it. `--target-sdk 29` keeps the game's own value and files, exactly as builds 9 and earlier: the fallback if something misbehaves on 35. By Android's rules a 29 build installs over a 35 build signed with the same key, but this has not been tried on a phone yet. 35 and 36 need `debloat` (so `--patch` without `debloat` also needs `--target-sdk 29`), and 36 also needs `game`. |
| `--game-log all` | The game's own log at every level in logcat (`mono-stdout`), for bug reports; this is the default while the offline build is being tested. `--game-log fatal` keeps the game's original setting (fatal errors only). |
| `--keystore <file.p12>` | Your signing key. Without it a key is created once in `~/.config/McvsOffline/` (Windows: `%APPDATA%\McvsOffline\`) and reused. |
| `--build-tools <dir>` | Folder with `apksigner` and `zipalign`; otherwise they are looked up on `PATH` and in `$ANDROID_HOME/build-tools`. |
| `--signer builtin` | Do not use apksigner, even if it is installed. |
| `--abi arm64-v8a` | Native libraries to keep. The default keeps **every ABI** the APK ships (64-bit `arm64-v8a` and 32-bit `armeabi-v7a`). `--abi arm64-v8a` drops the 32-bit copies for a 64-bit-only APK (about 14 MB smaller; a 64-bit phone never uses them). |
| `--art stock` | Keep Gameloft's launcher icon and launch screen even when this patcher has the project's own art built in. The default, `--art auto`, uses that art when there is any (today there is none, so the icon does not change); `--art <folder>` uses a folder laid out like `art/android/` (for the artist's test builds). |
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
- **bottile**: the game's "Practice" bot-match tile is hidden in the shipped data, so this patch normally does
  nothing. If the tile is shown (setting `crm.practiceTile`, section 6 "Match modes"), the patch makes it start the
  same bot-filled match as Battle (full rewards and progression, after the tutorial) instead of a free bot match that
  pays 5 Credits.
- **finder**: the game's matchmaking service listens on all network interfaces instead of the one that was active at
  start-up, so switching Wi-Fi or airplane mode on and off does not break matchmaking until a restart.
- **store**: opening the shop crashed the game because of the kill-signature sounds it sells (the game's availability
  check had no code for sounds). With the patch sounds work, and any shop item whose check still fails is left out of the
  shop (and logged as `[McvsOffline] store item <id> threw ...`) instead of closing the game.
- **signatures**: the kill-signature screen looks your owned items up in a set instead of a list, so it does not freeze
  when you own many of them (for example after `UNLOCKALL`).
- **inbox**: the inbox froze after the big unlock codes (`UNLOCKALL`, `UNLOCKPAID`, `UNLOCKCAMOS`, `UNLOCKACCESSORIES`
  and their parts): up to build 16 each code was one message with thousands of rewards, and the game built every one of
  them on opening the inbox and again every 10 seconds while it was open. Since build 17 the codes come through the
  inventory ledger instead (section 6, Unlock codes); the patch covers the messages that are already in a profile and
  any other big message. With the patch a message shows its first 30 rewards (plus any gear), the inbox refreshes once
  per 10 seconds, and **Claim** still gives you every reward of the message (the rewards popup lists the first 30). A
  profile that already holds such a message works again.
- **debloat**: removes the parts of Gameloft's SDKs that are dead offline (ad screens, push, install-referrer and boot
  receivers, Crashlytics and Facebook start-up, location and legacy permissions) from the manifest and drops the
  Crashlytics/Fabric files; see ARCHITECTURE.md §2.
- **adminpage**: Settings › Account gets an **MCVS Offline** button that opens the MCVS Offline page (section 7), and
  shows only the buttons that work offline (section 7, "Settings › Account").
- **adminlink**: the button of the "MCVS Offline" inbox message opens the page with full access. Without this patch
  the message opens a view-only page.
- **graphics**: the game reads its GPU profiles (`GameOptions_android.json`) through MCVS Offline, so the page's
  graphics profile (`graphics.profile`) can pick one. The start-up log gets a `[boot] graphics:` line with what the game
  really got. With `graphics.profile` `auto` nothing changes. Without this patch the setting does nothing, and Status ›
  Game host › Graphics says the game has not gone through patch 'graphics' (section 7). (The HD textures need no patch:
  section 7.)
- **targetsdk**: the app declares Android 15 (`--target-sdk`, default 35) instead of Android 10 as its target, so Play
  Protect no longer warns that it was "built for an older version of Android". A newer target changes some of Android's
  rules for the app, so the patch also adapts what they touch: the manifest (exported components, the notification
  permission, finding your browser for the page's links, native memory tagging off), the MCVS Offline page's layout (kept
  clear of the navigation bar), and the game's own Java code (alarms and notifications get the flag Android 12+
  requires, two receivers that are dead offline no longer listen, Google Analytics never starts). On Android 13 and
  later the game then asks once for the notification permission (section 7). Install it over your previous build as
  usual (same key, no uninstall). Since Android 12 tapping a reminder only dismisses it (open the game from the
  launcher), and a reminder the previous build scheduled can arrive once more after the update. `--target-sdk 29` turns
  all of this off.
- **game**: the manifest says the app is a game (`android:appCategory="game"` and the older `android:isGame="true"`),
  at every `--target-sdk`. Android's Game Mode then treats it as a game (Android 12 and later), and so can the phone
  maker's game tools (Samsung's Gaming Hub and Game Booster also go by their own list; section 6 shows how to check and
  how to add the game by hand). Side effects, all from Android itself: while the game is in front, Android may tell
  the chip it runs a game (more performance, device dependent), and a phone may cap the screen at 60 Hz for it, which
  the game's 30 fps does not notice. Nothing in the game reads these flags. Without it the manifest is exactly as in
  build 12 and earlier; `--target-sdk 36` needs it.
- **forum** (not in the default set): the Forum button in Settings opens the page too.
- **lan** (experimental): groundwork for LAN play (see the end of this guide). It changes nothing for single-device
  play.
- **Icon and launch screen** (`--art`, not a `--patch` id): once the project's own art exists in `art/android/` and the
  patcher is rebuilt, the offline build gets the project's launcher icon (and, if drawn, a themed icon for Android 13+)
  and a launch-screen colour on Android 12+. Until then nothing changes. After installing a build with a new icon, the
  home screen may keep showing the old one for a while (the launcher caches icons): reboot the phone, or clear the
  home app's cache (Settings › Apps › your launcher › Storage).

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

Settings › Account › Redeem code. Each code works once per profile; add a number to redeem again (`UNLOCKALL2`).

The items do **not** come through the inbox any more (one inbox message with thousands of rewards froze the game every
time the inbox opened). The game says the code was redeemed and the server puts the items straight into your profile,
the same way as `"unlockAll": true`: the agents first, then the rest in batches of 400 about 10 seconds apart, each with
a rewards popup. `UNLOCKALL` takes a few minutes (about 30 popups). Stay in the menus and let it finish; if you close
the game half-way, the rest arrives after the next start. An empty "Coupon" message is left in the inbox: it has nothing
to claim and disappears after 7 days. An old, full coupon message from build 16 or earlier that is still unclaimed no
longer freezes the inbox (the `inbox` patch, section 4): it shows its first 30 rewards, and **Claim** gives you all of
them.

| Code | What you get |
|---|---|
| `UNLOCKALL` | everything that was released: every agent and all their mods, weapons, gear slots, kill signatures, camos, jerseys, skins (about 11,400 items). Unlocking every mod raises your level to about 46 at once; the game then saves once per level and lags for a while. Let it finish. |
| `UNLOCKMERCS`, `UNLOCKWEAPONS`, `UNLOCKGEARSLOTS`, `UNLOCKSIGNATURES`, `UNLOCKCAMOS`, `UNLOCKACCESSORIES`, `UNLOCKSKINS` | the same in smaller parts (all but signatures unlock the agents first) |
| `UNLOCKPAID` | only what the original game sold for real money and nothing else gave out: the VIP-only camos and jerseys (about 5,600 items, agents first). `UNLOCKPAIDCAMOS` / `UNLOCKPAIDACCESSORIES` split it. |
| `UNLOCKUNRELEASED` | content that is in the game's data but was never released: 6 kill-signature emblems. Strictly optional; never part of UNLOCKALL, `unlockAll` or the shop. |

A code skips only what an earlier code or `"unlockAll": true` already gave you. Agents, camos and jerseys you got by
playing, from crates or the store, or by claiming an old coupon message (build 16 and earlier) count as new the first
time: the code gives them again and the game pays its usual duplicate gear for an agent you already had. Only a repeat
of a code (`UNLOCKALL2`, `UNLOCKMERCS2`, or a split code after `UNLOCKALL`) gives nothing new; in earlier builds such a
repeat paid the duplicate compensation for each agent again, and that repeat compensation is gone. Codes need the game's own request from this device: in LAN
mode, and when the server cannot tell which profile is yours (several profiles on one install and the game not logged
in yet), the game says the code does not exist and nothing changes; the server log says why. `settings.json`
`"unlockAll": true` grants the same as `UNLOCKALL` without a code. Details and the exact item lists:
[ECONOMY.md](ECONOMY.md).

### Match modes

- **Bot matches with full rewards: Battle › Classic multiplayer.** The match starts at once and 7 bots fill the other
  slots (`instantBots`); you get trophies, division progress, Credits, VersusCoins, the MVP bonus and agent XP, as in
  an online match. During the tutorial Battle starts the game's scripted onboarding matches instead.
- **Special Event** (the second box after Battle) is the game's Arena mode: a time-limited event with a twist (Havoc,
  Blitz, Competitive best-of-3, Grand Bounty and others) that sets the mode, the map, the allowed agents and reward
  multipliers. Live ops generates them: a new mode every day at the daily reset (settings page, Live ops:
  `Special Events`, `Special Event mode` to pin one mode, `Special Event length (hours)`, `Special Event map`). The
  match fills with bots like Classic and pays the full rewards, wins with a Credits and VersusCoins bonus; the box
  shows the countdown to the next mode ([LIVEOPS.md](LIVEOPS.md) §8a). With Special Events off the box stays locked.
- **Tournaments** are the game's private matches. The game never fills a private match with bots (no setting changes
  that), and a tournament gives no trophies, division progress, Credits, VersusCoins, MVP bonus, agent XP or stats, by
  the game's design: only the prize crate when you win (its gem cost is paid when you press Start, refunded on a
  draw), the crate-track win count, tournament achievements and probably daily and weekly mission progress. Play them
  with other players over LAN (experimental, not working end to end yet: section 9); for bot matches use Classic.
- **The Practice tile** (optional, off by default). The game has its own bot-match tile, "PRACTICE", but Gameloft's
  data hides it. Setting **Practice tile in the main menu** (`crm.practiceTile`, page: Settings › Gameplay) shows it as
  the sixth tile of the main menu: tap it, confirm, and with the default `bottile` patch you get the same bot-filled,
  full-reward match as Classic, without the mode and group screens (without `bottile`: the game's free bot match, 5
  Credits per win). It applies at the next CRM fetch: after your next match, or at the next start. **Untested on a
  phone or PC:** where the tile sits in this menu layout and how it looks are unknown, and it may show a "VICTORY
  LIMIT REACHED" lock badge. Turn it off again if the menu looks wrong.

### Logs, if something goes wrong

**On the phone, without a PC** (Android 11 and later): open the **Files** app (or Samsung **My Files**) > **Downloads** >
**MCVS-Offline**. Every game start writes its own copies there, named after the start time:
`<date>-<time>-bootstrap.log.txt`, `-server.log.txt`, `-game.log.txt`, plus `crash-*.txt` when the game crashed. The
copies are refreshed every 30 seconds while the game runs, and this start and the previous one are kept (setting
`logging.keepStarts`, see §8); older copies are deleted at the next start. Send the files of the newest start with your
bug report; if the game broke and you restarted it, the previous start's files (and usually the `.1` copies) hold the
broken start. (If the folder is missing, the start-up log says why: look for `[mirror]`.)

**The logs are cleaned at every start.** `McvsOffline/logs/` keeps the same starts as the copies in Downloads: each
start begins new `bootstrap.log`, `server.log`, `game.log` and `unknown-endpoints.jsonl` files (the previous start's are
usually the `.1` files; a log that grew past 5 MB during a start has more numbered files for it), and the log files, crash, exception, thread-dump and exit files of older starts are deleted (one
`[logs]` line in `bootstrap.log` says what went). So send the logs of a problem before you start the game twice more,
or raise `logging.keepStarts` first. Saves, settings and the logs zips you saved or shared are never deleted.

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

### Game Mode and Samsung Gaming Hub (patch `game`)

With the `game` patch (in the default set) the app tells Android it is a game, so Android's Game Mode and the phone
maker's game tools can treat it as one. Samsung decides mostly by the package name, from its own list, so check on the
phone:

1. **Gaming Hub** (Game Launcher) › **My games**: if MCVS is listed, Game Booster works for it. If it is missing, add
   it once with **My games › Add game › MCVS**; this works whatever the APK says, and stays after updates. Then start
   the game and look for the Game Booster icon in the navigation bar or its swipe panel.
2. **With a PC** (adb), Android's own view:

   ```sh
   adb shell cmd game list-modes com.gameloft.android.ANMP.GloftMVHM
   adb shell dumpsys game
   ```

   With the patch the first command prints `current mode: standard, available game modes: [standard,custom]`. Without
   it, Android 16 prints `Package com.gameloft.android.ANMP.GloftMVHM is not of game type, to use the game mode
   commands, it must specify game category in the manifest as android:appCategory="game"` (Android 12 to 15 print
   `current mode: unsupported, available game modes: []` instead). `dumpsys game` lists every installed game; with the
   patch MCVS is among them.
3. On phones whose Settings follow Android's own (not every One UI version), Settings › Storage counts the app under
   **Games** instead of **Apps**.

Before installing, you can check the APK on the PC with the Android build-tools: `aapt2 dump badging mcvs-offline.apk |
grep application-isGame` prints `application-isGame`, and `aapt2 dump xmltree --file AndroidManifest.xml
mcvs-offline.apk | grep -E 'isGame|appCategory'` shows `isGame(0x010103f4)=true` and `appCategory(0x01010545)=0`.

## 7. The MCVS Offline page

The offline build has its own page for settings, saves and logs. It runs inside the game, in the game's own browser,
and nothing on it leaves the phone unless you share it.

**Opening it** (the game's own browser opens over the game; its Close button or Back takes you back):

- Settings › Account › **MCVS Offline** (patch `adminpage`).
- The **MCVS Offline** message in the inbox: tap its button (patch `adminlink`; without the patch the page opens view-only).
- Settings › Account › Redeem code: type `MCVSOFFLINE`. The page opens and the game then says the code does not exist;
  that is expected, nothing is claimed. `admin.redeemCode` in `settings.json` changes the code (empty turns it off).
  The code does nothing while `lanMode` is on.
- The Forum button, if you patched with `--patch default,forum`.

**Settings › Account** shows only **Redeem code** and **MCVS Offline**, side by side in the middle of the screen.
Facebook, Google Play Games and Restore are hidden: their login can never succeed offline, and Restore is the restore of
in-app purchases, which do not exist offline (your progress moves through Saves › Export and Import instead). The page's
setting **Only Redeem code and MCVS Offline in Settings › Account** (`admin.accountMenu.hideDeadButtons`, Settings ›
Unlocks & codes, on by default) brings the game's own buttons back when turned off; it applies the next time you open
Settings › Account. Without Redeem code (setting `crm.redeemCoupons` off) MCVS Offline is alone in the middle; with the
page turned off (`admin.enabled`) Redeem code is. If the menu cannot be rearranged, the buttons stay where the game puts
them (stacked on the left) and `server.log` says why in an `[admin] account menu:` line. On a PC the game's GameCenter
button stays (the Steam build has no Facebook, Google or Restore button, so nothing changes there), and the setting is
not on the PC's settings page.

**Chrome is not supported.** Open the page only from the game. In Chrome (or any other browser)
`http://127.0.0.1:51780/_mcvs/ui/` shows a view-only page at most: Android freezes the game about a minute after it leaves the screen, and the page's
one-time links go only to the game's own browser.

**Screens:** Status (version, the player, the last save by the game, recent problems), Settings, Saves, Logs, Actions
and About. Each setting says when it applies: **Now**, **After the next match** (after you close the page) or **After a
restart**. Saving writes `settings.json`; risky settings (cheat menu, unlock everything, season length) make a backup of
your save first.

**Save the game now, and restart.** The game saves on its own only every few minutes and after certain events, never
when it is closed. **Saves › Save the game now** closes the page, lets the game save and opens the page again (with
"The game did not save" if it did not). **Actions › Restart the game** (and the Restart buttons of a staged import or
a restart setting) first lets the game save, then restarts it; when the game does not save it does not restart, unless
you tick "Restart even if the game does not save". If the game does not come back by itself after about 20 seconds,
start it again from the home screen. **On Android 13 and later the game may not come back by itself** (Android no
longer lets an alarm start an app's screen, whatever the app; this has not been checked on a phone on its own yet): then
"Restart" only closes it after the save and you start it again; the page says so there. The same may hold for the game's
own graphics-setting dialog, whose text the page cannot change.

**Backups** (Saves): automatic ones (`storage.backups.keep`, default 20), one before every import, restore and risky
setting, and your own (**Back up now**, at most 10, never deleted automatically). Restore one, or delete your own.
A restore, like an import, is applied at the next start, before the game loads your save.

**Moving to another phone, or reinstalling, without a PC:**

1. Saves › Export: **Share as a file** (a `.txt` to e-mail, Drive, Telegram's Saved Messages...) or **Copy as text**,
   part by part: whole parts of 60,000 characters, or 3,900 (Telegram) or 1,900 (Discord). The page shows the number of
   parts first; above 4 parts a file is easier.
2. On the other phone, in the offline build: Saves › Import, paste the parts (everything from `MCVS-SAVE` to `~`, in any
   order, one or several at a time), check the summary, tick the confirmation and **Stage**, then restart.
3. At the next start the save is put in place and that phone's login now opens the imported account. The account the
   phone had before stays on the phone: **Play this account on this phone** (Saves › Other accounts) or **Undo** take
   you back. If the imported save never reaches the menu, it is undone by itself after three starts.

With a PC, `adb push` the exported file into `McvsOffline/import/` and pick it in Saves › Import. Which way works where:

| | Android 5–9 | Android 10 | Android 11 and later |
|---|---|---|---|
| Export: Share as a file | yes (some apps need the storage permission) | yes | yes |
| Export: Save to Downloads | no | yes | yes |
| Export / import: copy and paste the text | yes | yes | yes |
| Import: a file in `McvsOffline/import/` put there on the phone | any file manager | most file managers | not with most file managers: use a PC (USB file transfer or `adb push`) |
| Import: a file in `Download/` | no | no | no |

**Logs** (Logs): read the server, start-up and game logs with filters, then **Share**: **Build** makes a zip (scope
"Recent" stays under 8 MB, which fits Discord and Gmail; "Everything" can be larger) and shows its size; **Share** opens
Android's share sheet, **Save to Downloads** puts it in `Download/MCVS-Offline/`. **Share a text report** sends a short
plain-text summary instead. After starting a share, come back to the game within a minute.

Settings for the page (in `settings.json` only): `admin.enabled` (`false` turns the page, the button and the inbox
message off), `crm.newsfeedLink` (the inbox message, also on the page), `admin.openVia` (`auto`, `game`, `jni`, `post`:
how the browser is opened; for testing), `admin.restartVia` (`game`, `host`, `none`), `security.hostCheck` (keep `true`:
it stops web pages from reaching the server through a hostile DNS name).

### Graphics profile, HD textures and the notification permission

**Graphics profile** (Settings › Graphics › Graphics profile, `graphics.profile`, applies after a restart; needs the
default patch `graphics`). The game picks one of Gameloft's own GPU profiles from a list of phone models; phones it does
not know, like the Galaxy S22, get the middle one. `auto` keeps that choice; `mid`, `high` and `ultra` force Gameloft's
`MidGPU`, `HighGPU` and `High_Ultra` profiles. High adds normal maps, the full lightmaps with their direction maps, the
levels' high-detail layers, shadow maps, bloom, 2x anisotropic filtering and a render scale that follows the screen size;
Ultra also the richest shader set (`GOD_END`), high-resolution assets and cube maps and 3x anisotropic filtering. Expect
more heat and less battery. Two options of those profiles also change the match servers that run inside the game (the
levels' Physics layer, how often bots think); they stay at the Mid values unless you also switch on "High profiles change
world loading too" (`graphics.fullProfile`). The start-up log says what the game really got: a `[boot] graphics:` line
with the profile and every option. If a profile misbehaves, set it back to `auto` (or edit `settings.json`) and restart.

**HD textures** (Settings › Graphics › HD textures, `hd.enabled`, default on; applies at the next start). The Steam
version of the game has textures at twice the resolution of the phone's for map surfaces, lightmaps, the characters
and grenades. [tools/hd-textures](../tools/hd-textures/README.md) converts them, on your own computer from your own Steam
copy, into an **HD pack** for your phone's data: a file named `mcvs-hd-<scope>.zip` (`mcvs-hd-default.zip` for the
whole set). HD packs are for the **ETC** data pack: the converter writes ETC1 textures and refuses an ASTC `Data/`
folder. For a phone with ASTC (the Galaxy S22) the same tool writes a whole **ASTC HD data pack** instead
(`astc-pack`: Gameloft's ASTC pack with every sharper PC texture in it, all categories), which replaces `Data/` as a
whole; installing it is in INSTALL-OFFLINE.md, "The ASTC HD data pack". The two variants are explained there too;
the start-up log line `[boot] installer data: ...` usually names the one your phone has (`ETC` or `ASTC`). To install
it, with the game closed:

```sh
adb shell mkdir -p /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline/hd
adb push mcvs-hd-default.zip /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline/hd/
```

`McvsOffline/hd/` is only a drop folder: you may make it with adb (as above), Shizuku or any file manager. On Android 11
and later a folder made that way belongs to that tool, and the game can read the pack in it but not write there, so
MCVS Offline never writes in it: its own files (the list of installed files and the backups of the originals) go to
`McvsOffline/hd-install/`, which the game makes itself (or, if even that cannot be written, to the game's internal
storage; the log and Status say so). Do not make `McvsOffline/hd-install/` yourself.

At the next start MCVS Offline checks every texture the pack replaces (it must be exactly the original the pack was made
from), copies the original to `McvsOffline/hd-install/backup/`, writes the HD file in its place and updates the installer's list
of the data (`pack.info`, the INSTALL-OFFLINE.md page explains it), so the game's installer accepts the new files at
every start, without internet and without `qaTestingConfigs.txt`. Copying takes a few seconds; a start does at most
about 8 seconds of it, so with a large pack the first two or three starts take longer and the rest follows at the next
start. The start-up log says what happened (`[boot] hd: ...`), and so does Status › Game host › HD textures (pack,
files, size, "N files at the next start" while it is not finished). If a folder cannot be written, nothing in `Data/`
changes and that line says why, for example `cannot write McvsOffline/hd-install: permission denied`. The pack, the HD files and the backups of the
originals take extra space: roughly 1.2 GB for the full set of research/pc-textures.md (the pack about 0.6 GB, the HD
files 0.45 GB more than the originals, the backups 0.15 GB). Check the free space first.

To remove them: Actions › **Remove HD textures** (or turn Settings › Graphics › HD textures off) and restart: the
originals come back from the backup and the installer's list is set back, byte for byte. This also takes at most about
8 seconds per start, so with a large pack it takes the first two or three starts (Status says "N originals at the next
start" until it is done); the game works normally meanwhile. The pack file stays in
`McvsOffline/hd/`, so turning HD textures on again reinstalls it; delete it with
`adb shell rm /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline/hd/mcvs-hd-*.zip` when you no
longer want it. Keep the pack, `McvsOffline/hd/` and `McvsOffline/hd-install/` in your backups (a backup of the whole
`files/` folder has them); do not delete `McvsOffline/hd-install/backup/` by hand while HD textures are installed (it
holds the only copy of the originals on the phone). Build 11 kept that list and the backups in `McvsOffline/hd/`; a
newer build takes the list over and still uses those backups, and once nothing is installed Status says
`McvsOffline/hd/backup ... may be deleted`: then you may delete `McvsOffline/hd/backup/` and `McvsOffline/hd/installed.json`. Only one pack is used: with several `mcvs-hd-*.zip`, the last by name. A pack made for the other texture
variant, or from other data, is refused and nothing changes. Never share the pack: it holds converted copies of the
game's files.

**Notifications** (Android 13 and later). A build that targets Android 13 or later must ask for the notification
permission, or the game's reminders are silently dropped. The game asks **once**, at the main menu, with Android's own
dialog. If you said no, allow it later in Android's settings (Apps › Modern Combat Versus › Notifications); it is not
asked again. Status › Game host › Notifications shows the state. `notifications.request` `false` in `settings.json`
turns the question off; `notifications.requestedAt` records when it was asked.

## 8. Saves, settings and backups

Most of this can be done on the phone with the MCVS Offline page (section 7). Everything MCVS Offline stores is in
one folder:

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
| `logging.keepStarts` | `1` | Starts whose logs are kept besides the current one (0–10). Older logs are deleted at the next start. |
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

## 9. LAN play (experimental, not working end-to-end yet)

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
  overloads, finder, lan), `Gameloft.Util.dll` (geo), `Game.dll` (bottile; store: `KillSignatureRewardItem.GetAvailableId` handles sounds, the `GameUnifiedStoreComponent.SelectItems` availability filter skips an item that throws; signatures: set lookups in `GetSignatureAssetsList`, one list per `GetKillSignItems` call; inbox: `InboxComponent.GenerateRewardsObjects` builds at most 30 rewards of a message plus its Gear rewards, each in try/catch, `GetObject` adds `RewardsTotal`, the claim popup of `ClaimRewardsForMessage` lists the first 30, `RefreshUI` refreshes once per 10 s window, and on PC `KillSignatureRewardItem.IsRandom` checks for a null id; adminpage: `PlayerProfileComponent.GetAccountMenuButtonList` passes its list through `McvsOffline.AdminHooks.AccountMenuButtons`, and `ProcessAccountMenuButtonAction("mcvs_offline")` calls `AdminHooks.OpenPage`; forum: `UIComponent.GoToForum` calls `AdminHooks.OpenPage`), `Gameloft.Engine.dll` (adminlink: `BrowserComponent.OpenUrl` first asks `AdminHooks.OpenIfPageMarker`, which handles only the inbox message's fixed link; graphics: `GameOptionPerformanceProfileComponent.OnCreate` passes the GameOptions_android.json text through `McvsOffline.GraphicsHooks.GameOptionsJson` before the engine reads it, and reports the resolved options to `GraphicsHooks.GameOptionsApplied`). Each site is checked against the exact code
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
  refuses if the text is used for anything else. With `targetsdk` at `--target-sdk 35` (the default) or 36 the
  manifest also gets the target version,
  `exported` on the three components that receive intents (the launcher activity, the cross-promotion screen and the
  local-reminder receiver), the notification permission, a declaration that lets the in-game browser find your browser,
  and heap-pointer tagging off; the two `res/layout-*/activity_in_game_browser.xml` files (the browser that shows the
  MCVS Offline page) keep the page clear of the navigation bar. The patcher checks the shipped manifest and layouts
  byte for byte first. With `game` (at any target) `<application>` gets `android:isGame="true"` and
  `android:appCategory="game"`, encoded as Android's own build tools write them; the patcher refuses a manifest that
  already sets either of them.
- `classes.dex` (the game's Java code), also with `targetsdk` at 35 or 36: 14 places in 13 methods are changed in
  place, each method checked against its verified fingerprint first (docs/research/target-sdk.md, P1-P14): the restart
  alarm, the local reminders and the download notification create their `PendingIntent`s with `FLAG_IMMUTABLE`
  (Android 12+ throws without a mutability flag), the push relay and Play billing receivers are registered with an empty
  filter (at target 34+ Android 14 refuses a registration without an export flag; both receive nothing offline anyway), and the
  game's three Google Analytics entry points return at once. Nothing else in the file moves; the dex checksum and
  signature are recomputed.
- With project art (`--art`, see `art/android/README.md`): the 20 launcher icon PNGs (`res/mipmap*/ic_launcher*.png`)
  are replaced in place under the same names, each checked against the shipped file's CRC first; a splash colour adds
  `v31` (and `night-v31`) configurations of the launcher activity's theme to `resources.arsc` (no resource id moves;
  the file stays uncompressed and aligned); a themed icon adds a `<monochrome>` layer to
  `res/mipmap-anydpi-v26/ic_launcher.xml` and six `ic_launcher_foreground1.png` density files. Without art none of
  these entries changes.
- Every other entry is copied byte for byte with its original compression; the old signature files are replaced by
  yours (v1+v2, plus v3 with apksigner).
