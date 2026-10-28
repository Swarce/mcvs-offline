# Development loop: the stock game against the standalone server

The fastest way to see what the real game asks for: the **unmodified** APK on a phone, the Federation server on a PC,
and one USB cable. Nothing is patched or re-signed, every request lands in the PC's log, and switching back to the
normal game is one `adb shell rm`.

How it works (verify-C1): the game's native engine reads `<GlfRootPath>/args.txt`, the folder that also holds the
downloaded `Data/`. Its arguments switch the game to `Embedded` mode (TS and GS Worlds in the game process) and point
the Federation client at `http://127.0.0.1:51780/`. `adb reverse` makes that phone-local port reach the PC.

## 1. What you need

- A PC with the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and `adb` (Android platform-tools).
- A phone with USB debugging and **Modern Combat Versus 1.17.32** installed from your own APK, launched once with a
  network connection so that its data (about 1.5 GB) is downloaded into
  `/sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/Data`.
- This repository, built once: `scripts/build.sh` (add `MCVS_REFS=/path/to/apk/assemblies` to also build the
  embedded DLL and run the real-client tests; the dev loop itself does not need it).

Test switches of `scripts/build.sh` (the full check sets all of them): `MCVS_REFS` (the game's assemblies; with them the
embedded DLL and the real-client tests are required), `MCVS_APK` (the original APK: patcher runs, the JNI table against
its dex), `MCVS_ANDROID_JAR` (an API 29 `android.jar` for the JNI table test; default
`platforms/android-29/android.jar` next to the build tools; required with `MCVS_REQUIRE_REAL_CLIENT=1`),
`MCVS_REQUIRE_NODE=1` (`node` must be on `PATH`: the settings page smoke against its fixtures and against a live server
then cannot skip) and the optional `MCVS_ACORN=<path to acorn.js>` (the page script must also parse as ECMAScript 5).

## 2. Start the server on the PC

```sh
scripts/run-standalone.sh --log-level debug
```

It listens on `127.0.0.1:51780` and keeps its state in `./data/` (`settings.json`, accounts, tokens, profiles,
mailboxes, leaderboards, logs). Useful options: `--port`, `--data <dir>`, `--datapack <dir>`, `--bind`, `--lan`.

The server builds the CRM document from the game's own `Config/crmpreconfiguration.json` when it can read the game's
data pack; without one the document is still complete (it is only missing the shipped iap catalogue and a few defaults).
To give it your phone's pack:

```sh
adb pull /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/Data ./device-data/Data
scripts/run-standalone.sh --datapack ./device-data/Data --log-level debug
```

Check it answers: `curl http://127.0.0.1:51780/` prints `{"eve":"on",...}`.

The console also prints `Settings page: http://127.0.0.1:51780/_mcvs/ui/#t=<ticket>`: open it in a browser on the PC
within two minutes (the ticket works once) for the settings page. On the PC it has no game host, so share, restart, save
now and the in-game clipboard are not offered; exports and the logs zip download as files instead.

**Host check.** Every route refuses a `Host` header that is not `127.0.0.1`, `localhost` or `[::1]` (404, and
`gate: refused Host '<host>' for <path> (DNS rebinding?)` in the log, once a minute), while the server is bound to
loopback and not in LAN mode. `curl` and the game through `adb reverse` send `127.0.0.1:51780` and pass. To reach the
PC server from another device, use `--lan` (or, for a quick test only, `"security": {"hostCheck": false}` in
`settings.json`). `/_mcvs/*` always answers loopback callers only, with a loopback `Host` and this port.

## 3. Forward the port over USB

```sh
adb reverse tcp:51780 tcp:51780
adb reverse --list          # should list tcp:51780 tcp:51780
```

`adb reverse` lasts until the USB connection or the adb server restarts; run it again after reconnecting.

## 4. Push args.txt

The exact file (seven lines, LF line endings, no BOM, no tabs; values are case-sensitive, so `Embedded`, not
`embedded`):

```
-ServerLocation Embedded
-AnubisCommand QuickLaunch
-EnvironmentSwitcherUrl http://127.0.0.1:51780/
-SuperCredential game:mcvsoffline
-SuperPassword mcvsoffline
-DeviceId mcvs4f3a9c2e7b1d4e8f9a0b1c2d3e4f5a6b
-DataCenter mcvs
```

- `-DataCenter`: the datacenter name eve answers (`dataCenter` in the server's `settings.json`); with it no Federation asks
  for the datacenter list.
- `-DeviceId`: any stable value without spaces (keep it the same across sessions; it is sent with every login).
- `-SuperCredential` / `-SuperPassword` must match `superCredential` / `superPassword` in the server's
  `settings.json` (these are the defaults). Neither may contain `-` or spaces: the same values are repeated in the
  matchmaker's `cmd_args`, which the game splits on `-` (verify-C2 §1.4).
- If you changed the server's port, change it in `-EnvironmentSwitcherUrl` and in `adb reverse` too. Keep an explicit
  port other than 80/443 (the client rewrites URLs on default ports, report 02 §1.3).

Create and push it (`printf` writes LF endings and no BOM on every platform with a POSIX shell):

```sh
printf '%s\n' \
  '-ServerLocation Embedded' \
  '-AnubisCommand QuickLaunch' \
  '-EnvironmentSwitcherUrl http://127.0.0.1:51780/' \
  '-SuperCredential game:mcvsoffline' \
  '-SuperPassword mcvsoffline' \
  '-DeviceId mcvs4f3a9c2e7b1d4e8f9a0b1c2d3e4f5a6b' \
  '-DataCenter mcvs' > args.txt
adb push args.txt /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/args.txt
adb shell ls -l /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/   # args.txt next to Data/
```

On Windows without a POSIX shell, save the seven lines with an editor that writes UTF-8 **without BOM** and LF endings
(a BOM glues itself to `-ServerLocation` and that line is lost, verify-C1 §1.2).

The file must sit next to `Data/`. On old phones with an extra `/storage/sdcard*` volume the game may keep its data
there instead; put `args.txt` wherever `Data/` is (verify-C1 §1.1).

## 5. Launch the game

Recommended: **airplane mode on, Wi-Fi off.** `adb` over USB keeps working, and the stock game's own calls to
Gameloft hosts then fail at once instead of hanging. The one that matters is the IP-geolocation lookup the TS makes
during the handshake (`http://ip.ecomapis.gameloft.org/location/`): it has a 100 s timeout against a 60 s handshake.
The player build redirects it to our server (the patcher's `geo` patch); with the stock APK a fast DNS failure is
harmless (the game falls back to region `US`).

```sh
adb shell monkey -p com.gameloft.android.ANMP.GloftMVHM -c android.intent.category.LAUNCHER 1
```

The first requests appear in the server console within seconds: `GET /config/<client id>/datacenters`,
`.../urls`, `GET /locate/auth`, `POST /authorize`, `GET /users/me`, then the TS's profile load
(`GET /profiles/me/myprofile/_mcvs.gameProfile`, `GET /profiles/me/myprofile?include_fields=...`), CRM
(`GET /configs/users/me/game`) and the matchmaker (`GET /lobbyconfigs/<client id>/ts`).

## 6. Read the logs

| Where | What |
|---|---|
| Server console | One INFO line per request (`#id METHOD target -> status [route] size time`) and one with its parameters (values cut to 512 characters) |
| `data/logs/server.log` | The same plus DEBUG: every request in full (headers, query, form or body up to `logging.bodyLimit`, 64 KB), module decisions. Rotates at 5 MB, 5 files |
| `data/logs/unknown-endpoints.jsonl` | One JSON object per request no route handled: method, target, query, form, body, headers. This is the list of what is still missing |
| `GET /_mcvs/status`, `/_mcvs/routes` | Server state and every registered route |
| `GET /_mcvs/storage`, `/_mcvs/storage/profiles/<account>` | Stored profiles (loopback only) |
| `GET /_mcvs/crm`, `POST /_mcvs/crm/reload` | The CRM document's sources and size; rebuild after editing `data/content/` (`curl -X POST -H 'X-MCVS: 1' http://127.0.0.1:51780/_mcvs/crm/reload`: every `_mcvs` write needs that header) |
| `GET /_mcvs/unlocks`, `POST /_mcvs/unlocks/grant` | `unlockAll` progress per account; grant now (`-H 'X-MCVS: 1'`) |
| `/_mcvs/ui/` | The settings page (open the console's link; `GET /_mcvs/api/v1/ping` answers without a session) |
| `adb logcat` | The game's own log. Filter with `adb logcat | grep -i -E "mono|gameloft|Online|Profile"` |

A quick summary of the unknown endpoints after a session:

```sh
python3 -c "import json,collections; c=collections.Counter((e['method'],e['path']) for e in map(json.loads, open('data/logs/unknown-endpoints.jsonl'))); [print(n, m, p) for (m,p),n in c.most_common()]"
```

Log lines worth searching for: `UNKNOWN ENDPOINT`, `401 for`, `service-prefixed path`, `locate of unknown service`,
`threw for` (a handler exception, answered 500), `storage unavailable`.

## 7. Settings while testing

Edit `data/settings.json` and restart the server (Ctrl+C, run again). Handy ones: `"devMenu": true` (the game's
developer cheat menu, opened with taps top-left, top-right, bottom-left, bottom-right, top-left, bottom-right within
1.5 s each, verify-C5), `"unlockAll": true`, `"instantBots": false`, the `economy` multipliers. Module settings are
listed in [PROTOCOL.md](PROTOCOL.md). The settings page (section 2) edits the same file without a restart for the
settings that apply at once; `security.hostCheck` (default `true`) can be changed only in the file.

Your progress lives in `data/storage/profiles/` (one readable JSON file per account) with automatic backups in
`data/storage/backups/`. Copy the whole `data/` folder to keep a session.

## 8. Back to the normal game

```sh
adb shell rm /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/args.txt
adb reverse --remove tcp:51780
```

**Delete the dev-loop `args.txt` before installing the player build.** The embedded host writes its own block into
`args.txt` but leaves any key you set outside its block alone (your value wins), so a leftover dev-loop file would keep
its `-DeviceId` and, if you changed it, its port.

## Troubleshooting

| Symptom | Check |
|---|---|
| No request reaches the server | `adb reverse --list`; `args.txt` next to `Data/`; no BOM/tabs; `Embedded` spelled exactly |
| The server refuses to start: port in use | Another host is running on 51780 (two hosts can no longer share a port); stop it or use `--port` and change `args.txt` |
| Requests arrive, the game shows "Incorrect datacenter" or an update popup | `GET /config/.../urls` must answer without a `status` key (eve, verify-C2 §1.2); look for errors in `server.log` |
| The game waits forever at "connecting" | `configs/users/me/game` must answer 200 (the GS waits otherwise); check `GET /_mcvs/crm` |
| The profile resets | Look for a 403/404 on a `profiles/...` request or `storage unavailable` in the log; restore from `data/storage/backups/` |
| Every request gets 404, `gate: refused Host` in the log | The client sends a `Host` that is not loopback (another device, a host name): use `adb reverse` and `127.0.0.1`, or `--lan` |
| A `curl -X POST` to `/_mcvs/...` answers 403 | Add `-H 'X-MCVS: 1'` |
