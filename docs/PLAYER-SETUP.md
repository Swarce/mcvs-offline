# Player setup: building your offline Modern Combat Versus

**Status: in development.** The patcher and the embedded server are built and checked offline (signature,
alignment, patched code), but nobody has run the result on a device yet. Expect rough edges, and keep your backups.

This guide turns **your own** copy of Modern Combat Versus for Android (APK **1.17.32**, package
`com.gameloft.android.ANMP.GloftMVHM`) into an offline build. The game keeps its own match servers; MCVS Offline adds
the missing Gameloft backend ("Federation") inside the game process. Nothing from the game is downloaded from this
project or redistributed: the patcher works on your APK, and the server reads your data pack on your phone.

You need:

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
again. They will not stay up forever, so keep your backup: a CDN mirror tool that archives the data and checks a
backup against Gameloft's own CRC index (report 10) is planned but not part of this repository yet.

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
| `--patch geo,bottile,finder` | The optional patches (this is the default set). `none` keeps only the server hook, `all` adds `lan`. |
| `--keystore <file.p12>` | Your signing key. Without it a key is created once in `~/.config/McvsOffline/` (Windows: `%APPDATA%\McvsOffline\`) and reused. |
| `--build-tools <dir>` | Folder with `apksigner` and `zipalign`; otherwise they are looked up on `PATH` and in `$ANDROID_HOME/build-tools`. |
| `--signer builtin` | Do not use apksigner, even if it is installed. |
| `--dry-run` | Check that every patch site matches, write nothing. |

What the optional patches do:

- **geo**: the game looks up your country at a hard-coded Gameloft address while it connects to its own server. Offline
  that lookup can hang longer than the connection timeout; the patch sends it to the embedded server.
- **bottile**: the BOTMATCH tile in the menu normally starts a "free" bot match that pays almost nothing. With the
  patch it starts the same bot-filled match as "Battle", with full rewards and progression (after the tutorial).
- **finder**: the game's matchmaking service listens on all network interfaces instead of the one that was active at
  start-up, so switching Wi-Fi or airplane mode on and off does not break matchmaking until a restart.
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

After a reinstall the game's installer checks the data against Gameloft's index, which needs internet. If the servers
are gone, the installer's own test switch `SKIP_VALIDATION=1` in
`/sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/qaTestingConfigs.txt` skips that check (report 10
§4.3; not yet tried on a device).

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
- The game's own installer screen still appears and checks or downloads the data as usual.
- The first launch and the first match are slower than with the original APK: the patched libraries run without
  their precompiled (AOT) code and are compiled on the phone as they are used.
- Google Play Games and Facebook sign-in no longer work (they are tied to Gameloft's signature, and their servers are
  gone anyway). The game's own signature self-check is left as it is: its result is only recorded, nothing on the
  phone acts on it.

Logs, if something goes wrong:

```sh
adb logcat -s McvsOffline                     # live, while the game runs
adb pull /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline/logs
```

`bootstrap.log` covers the start-up (game folder, args.txt, port), `server.log` every request, and
`unknown-endpoints.jsonl` the requests the server does not handle yet. Please attach them to bug reports.

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
- Optional: `Gameloft.Util.dll` (geo), `Game.dll` (bottile), `Gameloft.Engine.dll` (finder, lan). Each site is checked
  against the exact code of 1.17.32 first; if anything differs, the patcher stops without writing.
- The precompiled AOT images (`lib/<abi>/libaot-<name>.dll.so`, both ABIs) of every modified assembly are removed, so
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
