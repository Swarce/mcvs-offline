# MCVS Offline

**Status: in development. Nothing here is playable yet.**

A preservation project for **Modern Combat Versus** (Gameloft, Android, APK 1.17.32): run the game fully offline,
with progression saved on the device, everything obtainable, bot matches, and later LAN / internet P2P.

The game already ships its own authoritative servers (the Transaction Server and Game Server run as in-process
Worlds in its "Embedded" mode). The only dead piece is Gameloft's **Federation** HTTP backend (eve, pandora,
auth, storage, CRM, matchmaker, social, messaging, …), and that is what this project reimplements. Nothing from
the game is distributed: the patcher works on the player's own APK and the server reads the player's own data
pack at runtime.

## Layout

```
server/            McvsOffline.Server: Core (HTTP server, router, MiniJson, accounts/tokens, storage helpers,
                   settings, logging, data-pack reader) + Services (one folder per Federation service) + Content
hosts/standalone/  net8 console host (dev loop against a stock APK, LAN host)
hosts/embedded/    Bootstrap compiled into the in-game McvsOffline.Server.dll
patcher/           turns the player's APK into the offline APK (Mono.Cecil)
tests/             xUnit tests
tools/             content-gen
docs/              architecture, module guide, protocol notes, research
```

## Build and run

Requires the .NET 8 SDK.

```sh
scripts/build.sh            # net8 solution + embedded DLL (needs MCVS_REFS, see below) + tests
scripts/run-standalone.sh   # Federation server on http://127.0.0.1:51780/ (Ctrl+C to stop)
```

`run-standalone.sh` accepts `--port`, `--bind`, `--data`, `--datapack <.../files/Data>`, `--lan` and
`--log-level`. Every request is logged to the console and to `data/logs/server.log`; endpoints the server does not
know yet are logged in full and collected in `data/logs/unknown-endpoints.jsonl`. Settings live in
`data/settings.json`.

The embedded build compiles against the trimmed Mono assemblies from **your own** APK:
`MCVS_REFS=/path/to/apk/assemblies scripts/build.sh`. Its output is a single
`artifacts/bin/McvsOffline.Server.Embedded/release/McvsOffline.Server.dll` that references only
`mscorlib`, `System` and `System.Core`.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): the design (normative)
- [docs/MODULES.md](docs/MODULES.md): module ownership, the module seam, core APIs and the rules for code that
  also runs inside the game
- [docs/protocol/](docs/protocol/): per-service protocol notes
- [docs/research/](docs/research/): reverse-engineering reports (`verify-C*` notes take precedence)
