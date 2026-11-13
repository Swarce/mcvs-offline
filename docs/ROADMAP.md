# Roadmap

Done items are struck through. Device-test history and open risks are in [STATUS.md](STATUS.md).

## Done

- ~~**M0** Federation emulator: standalone server passes the conformance harness against the real `Gameloft.OnlinePlatform.dll`; dev-loop guide (stock APK + `args.txt` + `adb reverse`).~~ ✅
- ~~**M1** Patcher + embedded build: offline APK boots to the menu, saves progression and plays bot-filled matches on a real device (Samsung S22, Android 16).~~ ✅
  - ~~Headless menu-server crash (`InputSettingsComponent`), native online SDK stall, Havok physics deadlock between the client and in-process server Worlds.~~ ✅
  - ~~Diagnostics: game logs in logcat, crash reporter, boot trace, game-thread watchdog with thread dumps, phone-readable log copies.~~ ✅
- ~~**Shop**: VersusCoins catalogue for every item category; sound-signature crash fixed; store items that throw are skipped and logged.~~ ✅
- ~~**Unlock codes**: `UNLOCKALL` and split codes (display crash fixed), `UNLOCKPAID`, opt-in `UNLOCKUNRELEASED`; dev cheat menu toggle.~~ ✅
- ~~**Preservation tools**: CDN mirror + backup verifier (`tools/cdn-mirror`), data-pack decoder (`tools/datapack`, still in the work area, not in this repository).~~ ✅
- ~~**Live-ops** ([LIVEOPS.md](LIVEOPS.md)): weekly division seasons, weekly modifier rotation, daily missions and weekly events, generated from the clock, working offline (confirmed in device test 5).~~ ✅
- ~~**Install from copied data** ([INSTALL-OFFLINE.md](INSTALL-OFFLINE.md)): a copied `Data/` folder or a `files/` backup is accepted without Gameloft's CDN (confirmed in device test 5).~~ ✅
- ~~**ABIs**: 32-bit kept by default; `--abi arm64-v8a` gives a 64-bit-only APK.~~ ✅
- ~~**Firing-range exit lag**: the CRM document leaves owned kill signatures out (build 6; fast in device test 6).~~ ✅
- ~~**Avatar editor freeze** after unlock-all: caught in a thread dump in device test 6 (managed code, not the Flash VM); the `signatures` patch builds the list once per screen and looks ids up in sets (build 7, ×380 on net8).~~ ✅ (confirmed in device test 7)
- ~~**Unlock-all reward-popup stalls** (18–27 s per batch in device test 5): the same list builder; below a second per batch with build 7.~~ ✅ (the level jump to ~46 itself is the game's own behaviour: every new mod gives XP)
- ~~**Server write speed / load times**: saves and the return to the menu are fast since build 6 (the CRM refetch after every save is ~78% smaller for unlock-all profiles; confirmed in device tests 6 and 7).~~ ✅
- ~~**Patcher tooling**: `--game-log fatal` for quiet player builds, `--abi`, `--dry-run`, fingerprint-verified IL patches.~~ ✅
- ~~**Settings and diagnostics page** (build 9, [design/settings-page.md](design/settings-page.md)): game options without editing settings.json, save backup/restore/export/import (also from one phone to another), logs viewable and shareable from the phone, opened from the inbox, Settings › Account or the code `MCVSOFFLINE`.~~ ✅ (confirmed in device test 8)
- ~~**Debloat** (build 8): 44 dead manifest elements (ad screens, GCM push, Crashlytics/Facebook start-up providers, dead receivers, location permission), the Crashlytics/Fabric files, and no Crashlytics, push-token, Play Games or Facebook start at boot.~~ ✅ (confirmed in device test 7)
- ~~**Target Android 15 and graphics profiles** (build 10): `targetsdk` (default 35: no Play Protect "older version" warning; `--target-sdk 29` as the fallback) and the graphics setting (`graphics`, profiles auto/mid/high/ultra).~~ ✅ (confirmed in device test 9; STATUS.md, "Build 12 and device test 9")
- ~~**HD textures, ASTC** (builds 11 and 12, route A, [design/hd-textures.md](design/hd-textures.md)): the PC textures converted by `tools/hd-textures` and installed into the game's own `Data/`; the ASTC HD data pack.~~ ✅ (confirmed in device test 9, at the same frame rate; the ETC packs are not yet tested on a phone)

- ~~**Steam PC version** (builds 13-27, [design/pc-port.md](design/pc-port.md), [PLAYER-SETUP-PC.md](PLAYER-SETUP-PC.md)): the PC kit installs the same server and patches into the Steam files and restores them on uninstall; bots fixed at any frame rate (`botmove`, build 27); a frame rate setting up to unlimited (`fpslimit`).~~ ✅ (confirmed on the owner's PC)
- ~~**Settings menu** (builds 19-24, [design/settings-menu.md](design/settings-menu.md)): the shorter menu with the PERFORMANCE page, no flash of the stock menu, ABOUT centred.~~ ✅ (confirmed on the phone)
- ~~**Project art** (build 25): the owner's launcher icon, the launch screen colour, the data installer background from a local folder (`--art-extra`).~~ ✅ (confirmed on the phone)
- ~~**Port fallback** (build 26, [design/port-fallback.md](design/port-fallback.md)): a busy port no longer hangs the loading screen.~~ ✅ (approved by the owner)
- ~~**Names** (builds 28-29): rivals with their own names and emblems; ~2,000 hand-written names for rivals and bots.~~ ✅ (approved by the owner)

- ~~**1.0 release** (build 29): players are using it.~~ ✅
- **Hitches** (build 30): the match start no longer reloads `MCVS_Meta.xlsx`, a profile save normalizes once instead of four times, in-match bot names off by default; a hitch probe names the rest (needs a device log).
- **Agent-select stutter** (build 31): a design file read before is restored from memory instead of parsed again (each bot the match adds parsed `MCVS_Meta.xlsx` three times); the log says when the watchdog is off (needs a device log).
- **Agent-select freeze** (build 32): the template reader finds a JSON object's properties with a dictionary (each bot read the human's 611 KB unlock-all profile with a scan that is quadratic in the 11,368 Inventory keys: about 4 s per bot, 27.7 s for 7 on the owner's phone); profile loads and reloads use the same reader; managed thread dumps at the first hitches name the rest (needs a device log).

## Next

Build 29 is the **1.0 release** (November 2026): players already use it. The open device checks of builds 22-29 passed and
are approved by the owner (names, port fallback, Special Event, HD packs, Game Mode flags, the PC kit, the Cosmetic crate).
Build 30 is the first update after it (fewer hitches, in-match bot names off). Build 31 removes the three `MCVS_Meta.xlsx` parses per bot at agent selection, the main cost of the stutter every 5 s there (to be confirmed on a device). Build 32 removes the quadratic name scan of the template reader behind the 27.7 s agent-select freeze of the build-31 log (to be confirmed on a device).

| Priority | Item |
|---|---|
| High | **M3 LAN multiplayer**: a read-only design pass first (the LocalAnubis Finder/Lobby patches of research 06 and verify-C6, the guest's profile reached through the host), then the implementation. It is the base for internet play. |
| High | **M4 Internet multiplayer with Epic Online Services P2P** (the owner's choice): the owner registers a product in the Epic Developer Portal; a spike signs in on phone and PC and exchanges a packet; then a tunnel of the game's TCP control and ENet UDP traffic over EOS P2P and join codes through EOS Lobbies. Risks: Epic's terms for a fan project, client keys inside every build; phone-PC cross-play needs a protocol check (1.17.32 vs 1.17.30). |
| Next | **Polish**: quieter logging defaults (server.log detail `info`, one-line unknown-endpoint summaries, fewer Settings-menu timing lines), STATUS.md split into a short CHANGELOG.md plus `docs/history/`, `tools/pc-spike` archived, licence notices and SPDX headers, GitHub Releases with the patcher and the PC kit; robustness (a data-folder lock, Windows `ExclusiveAddressUse` on a real PC, the PC kit's probe following the bound port). |
| Check | **The Cosmetic crate** device test once crates start (2026-11-09). |
| Later | **Live content**: a seasonal event calendar, new event rules and modifiers, weekly challenges, rotating store bundles and crates, event packs defined in data files. |
| Later | **Player features**: moving a save between phone and PC in one step, a match history and stats page on the settings page, an opt-in update notice from GitHub Releases. |
| Low | **Signature screen, half-unlocked profiles** (only if someone reports a hitch). |

The APK signing key (`mcvs-offline-signing.p12`) is with the owner: every update must be signed with the same key, or
players have to uninstall (and lose their data) to update.

## Release checklist

- [ ] **`collector.since`**: before publishing a player build, set `collector.since` in `tools/content-gen/liveops_tuning.json` to the first Monday at least 7 days after the publication date, regenerate the content (`python3 -I tools/content-gen/content_gen.py`, which warns otherwise) and update `CollectorGuard.ShippedSince` to match (`GeneratedContentTests` checks they agree; `CollectorPlanTests` and `GoldenScenarios.Since` pin the date too). Never bump `revision` for it (LIVEOPS.md §7a).

## Later

- **M5 Preservation extras** (time-sensitive: `tools/cdn-mirror` and the offline install from copied data are done): mirror all 121 CDN builds while the CDN lives; missing maps from older builds (Barrage, Bridge, Downtown, Gym); archive the remaining online assets (news/event images); fully CDN-independent installs.
- **More devices**: testing on more phones and GPUs; ready-to-use patcher builds for Windows, macOS and Linux.
