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
- ~~**Debloat** (build 8): 44 dead manifest elements (ad screens, GCM push, Crashlytics/Facebook start-up providers, dead receivers, location permission), the Crashlytics/Fabric files, and no Crashlytics, push-token, Play Games or Facebook start at boot.~~ ✅ (confirmed in device test 7)

## Next

| Priority | Item |
|---|---|
| Low | **Signature screen, half-unlocked profiles** (only if someone reports a hitch; ~0.5–0.9 s estimated, not seen in device test 7): build the avatar list once per screen refresh in `GameOfflineStore.GetKillSignItemObject` too. |

## Later

- **Release polish**: quieter logging by default for player builds (the `--game-log fatal` option exists; the default is still `all` for testing); a settings page served by the embedded server (`http://127.0.0.1:51780/`) for toggles, save backup/restore and logs; ready-to-use patcher builds for Windows/macOS/Linux; player guide; testing on more devices.
- **Economy**: progression paths (reward tracks, crates) for the ~1,600 items that never had an unlock source; tuning from play.
- **M3 LAN multiplayer** (experimental groundwork in: the optional `lan` patches and the server's LAN mode): host's Game Server reaches each guest's profile on the guest's own embedded server; LAN discovery.
- **M4 Internet multiplayer**: overlay VPN guide first (Tailscale/ZeroTier), then an optional relay; host-leaves handling.
- **M5 Preservation extras** (`tools/cdn-mirror` and the offline install from copied data are done): mirror all 121 CDN builds while the CDN lives; missing maps from older builds (Barrage, Bridge, Downtown, Gym); archive the remaining online assets (news/event images); fully CDN-independent installs.
- **Steam PC version**: try the same server and patches on the Steam build, which ships the same .NET assemblies and data.
