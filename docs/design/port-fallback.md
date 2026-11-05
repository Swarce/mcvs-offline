# Port fallback (build 26)

Owner: EMBED (`hosts/embedded/PortReservation.cs`, `PortProbe.cs`, `PortHooks.cs`, the `geo` and `finderport` patches), with
small changes in core (`HttpServer.Listen`, the adopting `HttpServer` constructor, `ServerOptions.Listener`/`PortNote`,
`ServerContext.PortNote`) and ADMIN (`StatusApi` `server.configuredPort`/`server.portNote`, the status row, `IsPageMarker`).

## 1. The problem

The owner's S22, build 24: another process held `127.0.0.1:51780`, most likely a frozen earlier copy of the game (an adb
reverse is the other candidate). The host retried the same port 15 times, 2 s apart, then gave up. args.txt had already been
written with 51780, so the game sent its Federation requests (the eve config first) to the holder, which accepted the
connection and never answered: the loading screen never ended, and the 30 s of retries delayed the boot as well. A phone
restart fixed it.

The game reads args.txt once per process, early: on Android at the MainLoop constructor after the Java GameInstaller (3.3 s
after our args.txt write at the fastest), on PC at `MainLoop_Start` right after `EngineBridge.Class1.Start` returns. A port
chosen later on the server thread can never reach the game, so the port is decided synchronously in `EmbeddedHost.Prepare`,
before args.txt is written.

## 2. What the host does

`PortReservation.Reserve`, on the thread that starts the game (Android UI thread, PC boot thread):

1. Bind and listen on the configured port (`settings.json` `port`, default 51780; LAN mode: every interface) with the
   build's address-reuse rule (`HttpServer.Listen`: SO_REUSEADDR alone through libc on Android, nothing on net8 and PC). In the
   normal case this is the only step, well under a millisecond, and nothing else changes.
2. `AddressAlreadyInUse`: ask the holder (`PortProbe`): a raw socket connect of at most 250 ms, then
   `GET /_mcvs/status HTTP/1.0` with `Host: 127.0.0.1:<port>`, at most 500 ms and 16 KiB of answer. The holder is
   - **Mcvs**: a working MCVS Offline server answered (`"server": "mcvs-offline"`). The game uses it: args.txt keeps the
     configured port and this game's own server stays off (INFO line). This is the dev loop (a standalone server on the PC
     through `adb reverse`), and for a second copy of the game it avoids two servers on one data folder (the log line says
     so when the answering server's data directory is ours);
   - **Silent** (the connection is accepted, or left waiting, and nothing comes: a frozen process), **Closed** (accepted and
     closed or reset: an adb reverse with nothing behind it), **Refused** (bound but not listening, or just gone), **Foreign**
     (an HTTP or other answer that is not ours): the configured port is tried twice more, 150 ms apart (a dying process lets
     it go), then the fallback.
3. Fallback: the next 9 ports in order (51781..51789: deterministic, so a LAN guest's line is predictable), then any free
   port (port 0). `AccessDenied` (Windows' Hyper-V/WSL excluded port ranges, usually 100 ports wide) skips the probe and the
   neighbours and goes straight to port 0.
4. One WARN line names the busy port, what the probe saw and the port used, for example:
   `port 51780 is held by another program that does not answer as MCVS Offline: it accepted the connection but sent nothing
   in 500 ms; this start uses port 51781: args.txt, the eve URLs, the settings page and the geo lookup follow it;
   settings.json keeps 51780 and the next start tries it first (a phone restart frees it)`, plus a `[boot]` mark.

The worst case adds about 1.05 s to the start, only when the port is busy (build 25: 30 s, and no working game).

The listening socket goes to the server (`ServerOptions.Listener`): `McvsServer.Create` sets `Context.Port` to it before
any module registers (so the admin module's "settings page at" line names the real port), `Start` adopts it
(`new HttpServer(Socket, ...)`), and connections the game makes in between wait in the backlog instead of being refused.
There is no bind left on the server thread and no retry loop on this path. If `Create` fails, the socket is closed.

The fallback is per start: it is never written to settings.json (nor to the PC kit's install.json), and `settings.port`
keeps the configured value (the settings page shows it as `server.configuredPort`, the bound one as `server.port`, and
`server.portNote` says why they differ; the status page's Address row shows the note).

`embedded.portFallback` (default `true`, file-only, read-only on the settings page) set to `false` pins the port: a busy
port is then handled as in build 25 (args.txt keeps it, the server thread retries `StartAttempts` x `RetryDelayMs`, and the
error carries the platform hint). Only the deferral to an answering MCVS Offline server stays.

## 3. Everything that has to follow the port

| Site | How it follows |
|---|---|
| args.txt `-EnvironmentSwitcherUrl` | written after the reservation, with the bound port |
| eve URLs, pandora locate, lobbyconfig cmd_args, access gate, `/_mcvs/status` | built from `Context.Port` per request (already) |
| settings page URL (tickets, post mode), PageOpener, Steam overlay | `MintPageUrl` uses `Context.Port` at every open (already) |
| inbox marker `http://127.0.0.1:<port>/_mcvs/ui/#news` | built from `Context.Port` per CRM request; `IsPageMarker` now accepts any loopback port, so a message built before a port change still opens the page |
| `admin: settings page at ...` log line | `Context.Port` is the bound port from `Create` on |
| status, logs zip | `server.port`, `server.configuredPort`, `server.portNote` |
| `geo` patch (Gameloft.Util, TS handshake) | `ldstr "http://127.0.0.1:<--port>/location/?ip="` is now followed by `call McvsOffline.PortHooks::GeolocalizationUrl(string)`; the hook returns this start's port (the baked string until the host decided) |
| LocalAnubis Finder (51750) | see §4 |
| LAN guests | they name the host's port by hand: after a fallback the host logs the `-EnvironmentSwitcherUrl` line guests must use for that start (LAN is experimental) |
| the player's `-EnvironmentSwitcherUrl` outside the managed block, or `embedded.manageArgs` false | the game cannot follow: an ERROR names the URL the game will use instead of a misleading "switched" line |
| `publicUrl` | it wins over the port for the URLs the server hands out: a WARN after a fallback |
| res/raw/args.txt, notifications, the game's own caches | carry no port (verified) |

### 3.1 IL and the patch fingerprints

With the port free, args.txt is byte for byte build 25's, but the IL is not: `geo` gains the hook call (its note says
so), and Gameloft.Util.dll references McvsOffline.Server.dll from this build on (it is in the APK and in `McvsOffline\bin`
on PC, where the exe.config codeBase already points). `geo` is located by its `ldstr` as before (no fingerprint); its
fixture test checks the baked value before the host decides and the followed one after, and the real-client test bakes a
dead port and reaches the server through the hook. `--port` (APK patcher, PC kit) still bakes the fallback value, so a kit
`status` that finds settings.json and install.json on different ports is harmless now.

## 4. The LocalAnubis Finder

The game's LocalAnubis Finder listens on a second fixed port, `Args.LocalAnubisFinderPort ?? 51750` (Mono HttpListener
without SO_REUSEADDR; on every interface with the `finder` patch; http.sys on PC). A frozen earlier copy holds it as well,
and then `LocalAnubisManager.Start` throws and the main menu does not load even with the server moved. So the host also
test-binds `0.0.0.0:51750` the same way (plain bind, closed at once) and, when it is busy, picks 51751..51759 or any free
port and writes `-LocalAnubisFinderPort <n>` into the managed block (only then: with the port free the block is unchanged).
The client side, `ClientSettings.LocalAnubisFinderPort`, is a hard-coded 51750 on Android (`ClientSettings::.ctor IL_00f7
ldc.i4 51750`) that QuickLaunch, FindRooms and locate use: the new mandatory `finderport` patch appends
`call int32 McvsOffline.PortHooks::LocalAnubisFinderPort(int32)`, located by its exact pattern (the `lan` patch edits the
neighbouring host site of the same constructor and runs after it). On PC the Steam build's ClientSettings has no such
property and PcHooks already use the Finder's `PublicPort`, so the argument alone is enough there (not yet checked on a
real Windows machine whether a raw bind sees http.sys's port as busy).

A player's own `-LocalAnubisFinderPort` outside the block is used as it is (the hook returns it), with an ERROR when it is
busy.

## 5. Known limits

- Two servers on one data folder: when a frozen copy still holds the port, the new copy runs its own server on the same
  `McvsOffline/` folder. If the old process thaws instead of dying, its game can still save through its own server. There
  is no data-folder lock yet (a future pid/flock lock would close this).
- 51780 and 51750 lie in the ephemeral ranges of Linux/Android (32768-60999) and Windows (49152-65535): an outgoing
  connection can briefly own either port. The fallback covers that too; it is a reason not to read "busy" as "another copy".
- The probe runs on the thread that starts the game; its budget is hard (connect 250 ms, answer 500 ms, two re-binds of
  150 ms).
- On PC a lingering copy also holds the VDB ports (25001+); only the Finder and the server follow.
- Socket.ExclusiveAddressUse on Windows is still not set (it may refuse rebinds during TIME_WAIT); to check on a real PC.
