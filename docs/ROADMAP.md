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

## Next

| Priority | Item |
|---|---|
| High | **Economy polish**: a play path for the ~600 released items that never had an unlock source. **Implemented in build 9** as the Cosmetic crate ("CUSTOMIZATION CRATE", [ECONOMY.md](ECONOMY.md) §5, [design/economy-collector.md](design/economy-collector.md)): daily missions, weekly events and season rewards from 2026-11-09; awaiting its device test (STATUS.md, build 9, checks 14–28; crates start on 2026-11-09). Then tuned from play (`itemsPerCrate`, weights). |
| High | **Release polish, implemented in build 10, awaiting its device test** (STATUS.md, "Build 10"): target Android 15 (`targetsdk`, default 35: no Play Protect "older version" warning; manifest, page layouts and the 14 `classes.dex` edits; `--target-sdk 29` as the fallback) with the one-time notification permission request; the project icon and launch-screen mechanism (`--art`; the art itself is still to be drawn, so the stock icon stays); the graphics setting (`graphics`, profiles auto/mid/high/ultra); the HD texture probe (`tools/hd-textures`). |
| Next | **Steam PC version**: the WP0 test kit is implemented in build 10 (`tools/pc-spike`, one Windows exe, [PC-SPIKE.md](PC-SPIKE.md)) and awaits a run on the owner's PC. PC support itself comes next, after WP0's answers ([research/pc-port.md](research/pc-port.md)): the same server and patches on the Steam build, which ships the same .NET assemblies and data. |
| Low | **Signature screen, half-unlocked profiles** (only if someone reports a hitch; ~0.5–0.9 s estimated, not seen in device test 7): build the avatar list once per screen refresh in `GameOfflineStore.GetKillSignItemObject` too. |

## Release checklist

- [ ] **`collector.since`**: before publishing a player build, set `collector.since` in `tools/content-gen/liveops_tuning.json` to the first Monday at least 7 days after the publication date, regenerate the content (`python3 -I tools/content-gen/content_gen.py`, which warns otherwise) and update `CollectorGuard.ShippedSince` to match (`GeneratedContentTests` checks they agree; `CollectorPlanTests` and `GoldenScenarios.Since` pin the date too). Never bump `revision` for it (LIVEOPS.md §7a).

## Later

- **Release polish** (the build-10 items are under "Next"): the project's own icon and launch-screen art (the mechanism is in); HD textures beyond the probe (research/pc-textures.md); quieter logging by default for player builds (the `--game-log fatal` option exists; the default is still `all` for testing); ready-to-use patcher builds for Windows/macOS/Linux; player guide; testing on more devices.
- **M3 LAN multiplayer** (experimental groundwork in: the optional `lan` patches and the server's LAN mode): host's Game Server reaches each guest's profile on the guest's own embedded server; LAN discovery.
- **M4 Internet multiplayer**: overlay VPN guide first (Tailscale/ZeroTier), then an optional relay; host-leaves handling.
- **M5 Preservation extras** (`tools/cdn-mirror` and the offline install from copied data are done): mirror all 121 CDN builds while the CDN lives; missing maps from older builds (Barrage, Bridge, Downtown, Gym); archive the remaining online assets (news/event images); fully CDN-independent installs.
