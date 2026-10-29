# Contributing

Thanks for helping. This page says what helps most, how to build and test, and the rules every change follows.

## What helps most

**Reports from real devices.** The offline build is tested on one phone (a Samsung Galaxy S22). Every other phone,
Android version and GPU is unknown ground. A report that says "it works on my phone" is useful too.

- Open a [bug report](https://github.com/Swarce/mcvs-offline/issues/new/choose). The form asks for the player build,
  the phone, the Android version, where the game data came from, the HD pack and the logs.
- Features marked "waiting for a phone test" in the [README](README.md#status) need exactly that.
- The logs: MCVS Offline page › Logs › **Build**, then **Share**; or Files › Downloads › MCVS-Offline; or
  `adb logcat -s McvsOffline mono-stdout`. Details: [PLAYER-SETUP.md](docs/PLAYER-SETUP.md) section 6.

Never attach your APK, game data, an HD pack or a signing key. See [Legal rules](#legal-rules).

## Building

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and a bash shell (Linux, macOS, or
Windows with WSL). The embedded build and the real-client tests also need the `assemblies/` folder of **your own**
1.17.32 APK. It is never committed.

```sh
unzip -o mcvs.apk 'assemblies/*' -d mcvs-apk
MCVS_REFS="$PWD/mcvs-apk/assemblies" scripts/build.sh   # solution, embedded server DLL, all tests
scripts/run-standalone.sh --log-level debug             # Federation server on http://127.0.0.1:51780/
```

`scripts/build.sh` builds `McvsOffline.sln`, then the embedded server DLL against the game's own class library
(`MCVS_REFS`), then runs `dotnet test`. Without `MCVS_REFS` it builds and tests the net8 projects and skips the
embedded build. A change to shared code that only builds for net8 breaks the game build, so run it with `MCVS_REFS`
before you open a pull request.

CI builds the solution and runs the three test projects on every push and pull request. It has no game files, so the
tests that need them skip there, and it does not build the embedded server DLL. Run `scripts/build.sh` with
`MCVS_REFS` yourself.

To try a change against the unmodified game on a phone, use the development loop:
[docs/DEV-LOOP.md](docs/DEV-LOOP.md).

### Test switches

Each switch turns on tests that need files from your own copy of the game. Without it, those tests skip.

| Switch | What it enables |
|---|---|
| `MCVS_REFS` | the `assemblies/` folder of your APK: the embedded build, the embedded-DLL check and the real-client conformance tests (then required) |
| `MCVS_APK` | your original APK: the patcher's real-APK tests and the JNI table against its dex |
| `MCVS_DATAPACK` | your phone's `Data/` folder: the real-data-pack tests |
| `MCVS_ANDROID_JAR` | an API 29 `android.jar` for the JNI table test |
| `MCVS_BUILD_TOOLS` | an Android build-tools folder (`aapt2`, `apksigner`, `dexdump`, `d8`; without it `PATH` and `ANDROID_HOME` are searched): the manifest, dex and signing checks |
| `MCVS_STRICT=1` | fail instead of skipping the embedded build when `MCVS_REFS` is missing |
| `MCVS_REQUIRE_REAL_CLIENT=0` | let the real-client tests skip even though `MCVS_REFS` has the game's assemblies |
| `MCVS_REQUIRE_NODE=1` | `node` must be on `PATH`: the settings page smoke cannot skip |
| `MCVS_ACORN` | path to `acorn.js`: the page script must also parse as ECMAScript 5 |
| `CONFIG` | `Debug` or `Release` (default `Release`) |

The HD texture tests have their own switches (`MCVS_PC_INSTALL`, `MCVS_ASTCENC`, `MCVS_ASTC_HD_OUT`; see
[tools/hd-textures](tools/hd-textures/README.md)). The content generator has Python tests:
`python3 -I tools/content-gen/test_content_gen.py` (also run by `dotnet test`).

## Code rules

The full rules are in [docs/MODULES.md](docs/MODULES.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) §2.1. The
short version:

- **Shared code runs inside the game.** Everything in `server/Core` and `server/Services` is compiled twice: for net8
  and against the game's trimmed Mono class library.
- **C# 7.3.** No `async`/`await`, switch expressions, `using var`, `??=`, ranges, records, nullable reference types or
  tuples. Use threads and blocking I/O.
- **No Newtonsoft** (use `Core/MiniJson`), no `HttpListener`, avoid LINQ, no outgoing `HttpClient` calls from modules.
- The trimmed class library lacks many members that compile on net8. MODULES.md §4 lists the known ones and what to use
  instead.
- Format and parse numbers and dates with `CultureInfo.InvariantCulture`; compare strings with
  `StringComparison.Ordinal`.
- Android-only code goes behind `#if MCVS_EMBEDDED`.
- Never let an exception escape a thread you start: in the game it kills the process.
- Keep a change inside the module it is about. MODULES.md §1 lists who owns which folders; a change to `server/Core`
  or to a shared contract needs a reason in the pull request.
- Put tests next to the module's other tests: `tests/McvsOffline.Tests/<Module>/` for the server;
  `tests/McvsOffline.Embed.Tests` for the embedded host (`Embedded/`), the patcher (`Patcher/`) and the HD texture
  tools (`HdTextures/`); `tests/McvsOffline.PcSpike.Tests` for the PC test kit.

### Patches to the game

The patcher changes only code it has verified. Every patch site is checked against the exact code of 1.17.32 before it
is changed; if anything differs, the patcher stops without writing.

- An IL patch (`patcher/Patches`) checks the whole method body with `RequireFingerprint` against the SHA-256 that
  `IlPatch.Fingerprint` gives for the verified 1.17.32 method. Manifest, resource and `classes.dex` edits are checked
  against the shipped files the same way.
- Every new or changed patch has tests: the synthetic ones in `tests/McvsOffline.Embed.Tests/Patcher`, and a `Real*`
  test that runs against your own assemblies or APK (`MCVS_REFS`, `MCVS_APK`).
- Document the patch in [PLAYER-SETUP.md](docs/PLAYER-SETUP.md) ("What the patcher changes, exactly").

## Commits and pull requests

- One topic per commit. The subject names the area and says what changed, with no full stop:
  `HD installer: never write in McvsOffline/hd/`, `Docs: one HD pack contract`. The body says why.
- Open pull requests against `main`. Fill in the template: what changed, which suites you ran, whether you tested on a
  phone, and what changes for Android and PC players.
- Update the docs in the same pull request when behaviour changes: the player guide, the protocol note of the module,
  and [docs/STATUS.md](docs/STATUS.md) for device test results.
- Docs are plain, precise English in short sentences. Say what is checked and how; say what is not checked yet.

## License

The project is licensed under the [Mozilla Public License 2.0](LICENSE). Contributions are accepted under the same
license (inbound = outbound): by opening a pull request you agree that your contribution is licensed under MPL-2.0.
Contribute only work you have the right to license this way.

## Legal rules

The repository must never contain Gameloft files.

- **Never commit** an APK, the game's assemblies, native libraries, decompiled sources, game data (`*.f`, `*.game`,
  `*.ktx` and the rest of `Data/`), textures, HD packs or signing keys. The `.gitignore` refuses most of them; do not
  force-add anything it refuses.
- The only exception is generated content in `server/Content`: item ids and prices, produced by `tools/content-gen`.
- Code builds against the player's own files at build time or reads them at run time. It never ships them.
- Do not quote decompiled game code at length in code, docs or issues. Name the type and method instead.
- Never attach game files, an APK, an HD pack or a signing key to an issue or pull request.

Modern Combat Versus and all related names are trademarks of Gameloft. This is an unofficial, non-commercial
preservation project with no affiliation to Gameloft.
