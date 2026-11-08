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
   - **Mcvs**: a working MCVS Encore server answered (`"server": "mcvs-offline"`). The game uses it: args.txt keeps the
     configured port and this game's own server stays off (INFO line). This is the dev loop (a standalone server on the PC
     through `adb reverse`), and for a second copy of the game it avoids two servers on one data folder. When the
     answering server's data directory is ours (another copy of the game), the line is a WARN ("quit it before playing"),
     and a background thread tries to bind the configured port every 2 s: the bind only succeeds once that server has let
     the port go (never two listeners), and then this game's own server starts on it. Port and args.txt are unchanged, so
     the running game's next requests reach it (state the other server kept only in memory, such as a token, may still
     need a game restart). A standalone server with another data directory is not watched: taking over would switch the
     game's data mid-session;
   - **Silent** (the connection is accepted, or left waiting, and nothing comes: a frozen process), **Closed** (accepted and
     closed or reset: an adb reverse with nothing behind it), **Refused** (bound but not listening, or just gone), **Foreign**
     (an HTTP or other answer that is not ours): the configured port is tried twice more, 150 ms apart (a dying process lets
     it go), then the fallback.
   - **A second copy started at the same moment.** A copy reserves the port in `Prepare` but answers only once
     `McvsServer.Create` and `Start` have run on its server thread, so a second copy on the same data folder started a
     fraction of a second later sees **Silent**. Each start that reserves a socket writes `logs/server-start.txt`
     (`port=`, `state=starting`, `utcTicks=`; `state=running` once its server listens, `stopped` when it failed). When
     the holder is Silent and the marker says `starting` on the configured port less than 10 s ago, the second copy probes
     again every 100 ms for up to 2 s and uses that server when it answers (Deferred, and the watch above). A frozen copy
     wrote `running` long ago (or `starting` more than 10 s ago), so the owner's case costs nothing extra; a start that
     froze within 10 s of its own start costs at most about 2.8 s more before the fallback.
3. Fallback: the next 9 ports in order (51781..51789: deterministic, so a LAN guest's line is predictable), then any free
   port (port 0). `AccessDenied` (Windows' Hyper-V/WSL excluded port ranges, usually 100 ports wide) skips the probe and the
   neighbours and goes straight to port 0.
4. One WARN line names the busy port, what the probe saw and the port used, for example:
   `port 51780 is held by another program that does not answer as MCVS Encore: it accepted the connection but sent nothing
   in 500 ms; this start uses port 51781: args.txt, the eve URLs, the settings page and the geo lookup follow it;
   settings.json keeps 51780 and the next start tries it first (a phone restart frees it)`, plus a `[boot]` mark. The
   Android build's `SO_REUSEADDR not set (...)` warning, when the libc call fails, comes from this bind now and is logged
   once to `bootstrap.log` (build 25 logged it on the server's own bind): it explains a busy port right after a restart.

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
error carries the platform hint). Only the deferral to an answering MCVS Encore server stays.

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
| the player's `-EnvironmentSwitcherUrl` outside the managed block, or `embedded.manageArgs` false | the game cannot follow. An ERROR when the line names the busy port on loopback (`127.0.0.1`, `localhost`, `[::1]`) or there is no line: the game will not reach this start's server (the ERROR mentions the one exception, a standalone behind `adb reverse` that starts later, DEV-LOOP §3). Any other URL (a LAN guest's host, a PC server) is a server chosen on purpose: one WARN that the game uses it, not this device's server |
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

LAN (experimental). The same client property names the Finder a guest contacts: with the `lan` patch,
`LocalAnubisFinderHost` is the host from `-RemoteIp`, so QuickLaunch, FindRooms and locate go to
`<host>:LocalAnubisFinderPort`. So:

- a guest (args.txt sets `-RemoteIp`) whose own 51750 was busy moves its own Finder (`-LocalAnubisFinderPort` in the
  block), but the hook keeps returning the unmoved port, the one the host listens on (INFO line). A guest's own
  `-LocalAnubisFinderPort` still wins: that is how a guest names a host's moved Finder (it moves the guest's own Finder
  too). With `-RemoteIp` but without the `lan` patch the client asks its local Finder on the unmoved port, which then fails
  if that one was moved (both are unsupported combinations);
- a host in LAN mode whose Finder moved logs a WARN with the `-LocalAnubisFinderPort <n>` guests need for that start, next
  to the `-EnvironmentSwitcherUrl` WARN of a server-port fallback.

## 5. Known limits

- Two servers on one data folder: when a frozen copy still holds the port, the new copy runs its own server on the same
  `McvsOffline/` folder. If the old process thaws instead of dying, its game can still save through its own server. There
  is no data-folder lock yet (a future pid/flock lock would close this). Two copies started at the same moment are covered
  by the start marker only while the first one's server answers within about 3.5 s of the second's first probe; a slower first
  start (or a first copy that fell back to another port itself) still leads to a second server.
- 51780 and 51750 lie in the ephemeral ranges of Linux/Android (32768-60999) and Windows (49152-65535): an outgoing
  connection can briefly own either port. The fallback covers that too; it is a reason not to read "busy" as "another copy".
- The probe runs on the thread that starts the game; its budget is hard (connect 250 ms, answer 500 ms, two re-binds of
  150 ms).
- On PC a lingering copy also holds the VDB ports (25001+); only the Finder and the server follow.
- Socket.ExclusiveAddressUse on Windows is still not set (it may refuse rebinds during TIME_WAIT); to check on a real PC.
