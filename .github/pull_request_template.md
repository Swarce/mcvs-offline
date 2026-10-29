## Summary

<!-- What changes and why, in a few sentences. Link the issue if there is one. -->

## Testing

Suites run with `scripts/build.sh` (tick what you ran):

- [ ] Without `MCVS_REFS` (net8 projects and their tests)
- [ ] With `MCVS_REFS` (embedded server DLL, the embedded-DLL check and the real-client conformance tests)
- [ ] With `MCVS_APK` (the patcher's real-APK tests)
- [ ] With `MCVS_DATAPACK` (the real-data-pack tests)
- [ ] With `MCVS_REQUIRE_NODE=1` (the settings page smoke)
- [ ] `python3 -I tools/content-gen/test_content_gen.py` (content generator)
- [ ] Other: <!-- e.g. MCVS_PC_INSTALL, MCVS_ASTCENC -->

Device test:

- [ ] Tested on a phone: <!-- model, Android version, player build, what you checked -->
- [ ] Not tested on a device

## Impact

- Android (the patched APK, the embedded host): <!-- none, or what changes for players -->
- PC (standalone host, Steam port): <!-- none, or what changes -->
- Saves, `settings.json` or the data folder: <!-- none, or what changes and whether old files still load -->

## Checklist

- [ ] No Gameloft files: no APK, assemblies, decompiled code, game data, textures, HD packs or keys
- [ ] Shared code (`server/Core`, `server/Services`) follows the C# 7.3 rules of docs/MODULES.md §4 and ARCHITECTURE.md §2.1
- [ ] New or changed patch sites are checked against the verified 1.17.32 code (IL fingerprint) and have tests
- [ ] Docs updated (player guide, protocol notes, STATUS.md) where behaviour changed
