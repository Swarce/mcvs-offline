# MCVS Offline

A preservation project for **Modern Combat Versus** (Gameloft, Android, APK 1.17.32,
`com.gameloft.android.ANMP.GloftMVHM`): the game fully offline, with full progression saved on the device,
everything obtainable, bot matches, and later LAN and internet P2P.

**Status: feature-complete on the server side, first device test done.** Every piece builds and passes its tests,
including a conformance harness that drives the game's own client libraries against the server. The first device test
(October 2026) booted the patched game as far as the in-process game server loading its level; the two blockers it found
are fixed and the build now carries detailed diagnostics. [docs/STATUS.md](docs/STATUS.md) has the findings and what the
next device session must check.

## How it works

The game already ships its own authoritative servers: the Transaction Server (menus, meta, rewards, profile) and the
Game Server (matches) run as in-process Worlds in the game's "Embedded" mode, and bots fill the matches. The only dead
piece is Gameloft's **Federation** HTTP backend (eve, pandora, auth, storage, CRM, matchmaker, social, messaging,
alerts, leaderboards, coupons, ...). This project reimplements exactly that backend, as a small HTTP/1.1 server written
in C# 7.3 that runs either on a PC (for development) or inside the game process (for players).

The game saves no progression on the device itself: its TS and GS Worlds load and save the profile through
Federation storage. A Federation emulator with the right storage semantics therefore gives full persistence with no
change to the game's logic. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (normative) and the research notes in
[docs/research/](docs/research/).

## Status per goal

| Goal | Status | Where |
|---|---|---|
| Fully offline boot to the menu | Works on a Samsung S22 (Android 16) since device test 3; conformance-tested with the real client libraries. Data already on the phone (backup, `Data/` copy or mirror) installs without Gameloft's CDN (build 5, device test pending) | AUTH, STORAGE, CONTENT, MISC; [STATUS](docs/STATUS.md), [INSTALL-OFFLINE](docs/INSTALL-OFFLINE.md) |
| Full progression saved on the device | Storage complete: byte-exact profile dumps, no ETags, 401 never 403, atomic writes plus directory fsync, backups, corrupt-file recovery | [protocol/storage.md](docs/protocol/storage.md) |
| Everything obtainable | VersusCoins economy (1,864 store entries), unlock coupons (`UNLOCKALL` and split codes), optional `unlockAll` through the inventory ledger, optional developer menu | [ECONOMY.md](docs/ECONOMY.md), [protocol/content.md](docs/protocol/content.md), [protocol/unlocks.md](docs/protocol/unlocks.md) |
| Bot matches with full rewards | Instant bot-filled PvP through CRM (`MinPlayers 1`), `bottile` patch routes the BOTMATCH tile to the full-progression GS flow | [verify-C4](docs/research/verify-C4-bot-match-progression.md), patcher |
| Player build (offline APK) | Patcher complete (hook, nativeonline, waits, geo, bottile, finder, store, signatures, debloat, adminpage, adminlink, graphics; signing v1/v2/v3; every ABI by default, `--abi arm64-v8a` for 64-bit only); checked offline with apksigner, zipalign and ILVerify | [PLAYER-SETUP.md](docs/PLAYER-SETUP.md) |
| Settings, saves and logs on the phone | The in-game MCVS Offline page (build 9, device test pending): settings, backups, save export/import between phones, logs shared through Android's share sheet | [PLAYER-SETUP.md](docs/PLAYER-SETUP.md) §7, [design](docs/design/settings-page.md) |
| LAN P2P | Experimental: `lan` patch and LAN mode exist; guests must use the host's server for everything (topology A). Forwarding to each device's own server is not implemented | [roadmap](docs/ROADMAP.md) M3, [verify-C6](docs/research/verify-C6-lan-p2p.md) |
| Internet P2P | Not started (planned: overlay VPN guide first) | [roadmap](docs/ROADMAP.md) M4 |
| Live ops (division seasons, daily missions, weekly events) | Generated from the clock, offline; conformance-tested with the real client (build 5, device test pending) | [LIVEOPS.md](docs/LIVEOPS.md) |
| Preservation extras (missing maps, local data archive) | Partly: CDN mirror and backup verifier (`tools/cdn-mirror`); missing maps not started | [roadmap](docs/ROADMAP.md) M5 |

## Quickstart

Requirements: the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). The embedded build and the
conformance tests also need the `assemblies/` folder of **your own** 1.17.32 APK (`unzip mcvs.apk 'assemblies/*'`).

```sh
MCVS_REFS=/path/to/apk/assemblies scripts/build.sh   # net8 solution + embedded DLL + all tests
scripts/run-standalone.sh                             # Federation server on http://127.0.0.1:51780/
```

### Mode 1: development loop (stock APK, server on a PC)

Install the unmodified game, push an `args.txt` that points it at the PC, forward the port over USB and watch every
request in the server log. Step by step, with the exact file contents: [docs/DEV-LOOP.md](docs/DEV-LOOP.md).

```sh
adb reverse tcp:51780 tcp:51780
adb push args.txt /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/args.txt
scripts/run-standalone.sh --log-level debug
```

### Mode 2: player build (server inside the game)

The patcher turns your own APK into the offline APK (the server DLL is added, the game is hooked to start it, the APK
is re-signed). Full guide, including the data backup you must make before uninstalling the original:
[docs/PLAYER-SETUP.md](docs/PLAYER-SETUP.md).

```sh
dotnet artifacts/bin/McvsOffline.Patcher/release/McvsOffline.Patcher.dll \
  --apk mcvs.apk --server artifacts/bin/McvsOffline.Server.Embedded/release/McvsOffline.Server.dll --out mcvs-offline.apk
```

## Settings

`settings.json` lives next to the server's data (standalone: `./data/`; in the game:
`/sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline/`). The most useful keys:

| Key | Default | Meaning |
|---|---|---|
| `port` | `51780` | Federation port (the player build bakes it into the `geo` patch; re-patch after changing it) |
| `unlockAll` | `false` | Grant every merc, weapon, gear slot, signature, camo, accessory and skin through the inventory ledger |
| `devMenu` | `false` | Leave the game's developer cheat menu enabled (6-tap gesture, verify-C5) |
| `instantBots` | `true` | Start PvP immediately, filled with bots |
| `economy.*Multiplier` | `1.0` | Credits, VersusCoins, XP and store-price multipliers |
| `assets.couponsEnabled` | `true` | Accept the unlock coupon codes (Settings → Account → Redeem code) |
| `lanMode`, `bind` | `false`, `127.0.0.1` | LAN host mode (experimental) |

Each module's own keys are documented in its protocol note ([docs/PROTOCOL.md](docs/PROTOCOL.md)).

## Project layout

```
server/            McvsOffline.Server: one source tree compiled twice (net8 and the game's trimmed Mono BCL)
  Core/            HTTP/1.1 server on raw sockets, router, MiniJson, accounts/tokens, durable storage helpers,
                   settings, logging, data-pack reader (GCBF/LZ4)
  Services/        one folder per Federation service: Eve, Pandora, Auth, Storage, Crm, Matchmaker, Assets,
                   Social, Messaging, Alerts, Leaderboards, GameStats, Profanity, Chat, Clans, Wall, BanTool, Geo,
                   Misc (catch-all), Unlocks (unlockAll glue); ServiceRegistry wires them
  Content/         generated store catalogue, economy, unlock groups, CRM overlay (ids and prices only)
hosts/standalone/  net8 console host: dev loop, LAN host, conformance target
hosts/embedded/    Bootstrap compiled into the in-game McvsOffline.Server.dll (args.txt, device id, logcat)
patcher/           McvsOffline.Patcher (net8 + Mono.Cecil): your APK -> the offline APK
tests/             McvsOffline.Tests (server modules, core, real-client conformance) and
                   McvsOffline.Embed.Tests (embedded host, patcher, real parser and geo checks)
tools/content-gen/ generator for server/Content from the decoded data pack (Python, stdlib only)
scripts/           build.sh, run-standalone.sh
docs/              architecture, module guide, protocol notes, status, guides, research
```

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): the design (normative)
- [docs/STATUS.md](docs/STATUS.md): what is implemented, the device-test findings, the expected log sequence, the device-test checklist
- [docs/DEV-LOOP.md](docs/DEV-LOOP.md): stock APK against the standalone server
- [docs/PLAYER-SETUP.md](docs/PLAYER-SETUP.md): building and installing the offline APK
- [docs/PROTOCOL.md](docs/PROTOCOL.md): index of the per-service protocol notes
- [docs/ECONOMY.md](docs/ECONOMY.md): the offline economy and how to tune it
- [docs/MODULES.md](docs/MODULES.md): module ownership, the module seam, core APIs, rules for code that runs in the game
- [docs/research/](docs/research/): reverse-engineering reports; the `verify-C*` notes take precedence

## Legal note

This repository contains no Gameloft files: no APK, no assemblies, no decompiled code, no game data and nothing derived
from them except generated item ids and prices. The build compiles against the libraries in the player's own APK, the
patcher modifies the player's own APK on the player's machine, and the server reads the player's own data pack at
runtime. The `.gitignore` refuses binaries, data-pack files, decompiled sources and signing keys. Modern Combat Versus
and all related names are trademarks of Gameloft; this is an unofficial, non-commercial preservation project with no
affiliation to Gameloft.
