# PC spike (WP0): test the Steam game on your Windows PC

Before we port MCVS Offline to the Steam version of Modern Combat Versus, we need answers that only a real Windows PC
with Steam can give ([research/pc-port.md](research/pc-port.md) §5 WP0 and §8). One small program,
`McvsOffline.PcSpike.exe`, runs every test and packs the results into one zip that you send back. It takes about an hour.

What it does to your PC:

* It runs our server on your own PC only (`127.0.0.1:51780`). Its files live in `%LOCALAPPDATA%\McvsOffline\spike`.
* While a test runs, it adds files to the game folder: `args.txt` (Part B) and `ModernCombatVersus.exe.config` plus a
  `McvsOffline\spike\` folder (Part A). `stop` and `redirect-undo` remove them again.
* It changes no Steam file. If a file it writes already exists, it first saves a copy to
  `%LOCALAPPDATA%\McvsOffline\spike\backup\`, and the undo command puts it back.
* It contains no game file. For Part A it makes a changed copy of the game's own `EngineBridge.dll` on your PC.
* It sends nothing anywhere. You send the zip yourself.

## 1. What you need

* Windows 10 or 11, 64-bit.
* Steam, running and signed in. Online or offline mode both work. The game will not start without Steam.
* Modern Combat Versus installed from Steam. The kit expects Steam build 6372049 (game version 1.17.30). The summary
  says if yours is different.
* Close DebugView or any debugger if you use one. The kit records the game's debug output, and only one program at a
  time can do that.

## 2. Get the program

1. Download `McvsOffline.PcSpike.exe` (about 34 MB) into any folder, for example `Downloads`. You do not need to install
   .NET or anything else.
2. **SmartScreen warning.** The exe is not code-signed, so Windows may show "Windows protected your PC". Click
   **More info**, then **Run anyway**. Your antivirus may also scan it first.
3. Open a terminal in that folder: in Explorer, right-click an empty spot in the folder and choose **Open in Terminal**
   (Windows 11). On Windows 10, hold Shift while you right-click and choose **Open PowerShell window here**.
4. Check that it finds the game:

   ```powershell
   .\McvsOffline.PcSpike.exe status
   ```

   It prints the game folder. If it says the game was not found, add `--game "<folder that holds ModernCombatVersus.exe>"`
   to **every** command below, for example `--game "D:\SteamLibrary\steamapps\common\Modern Combat Versus"`. In Steam,
   right-click Modern Combat Versus, then **Manage** > **Browse local files** opens that folder. Instead of `--game` you
   can also set it once for the terminal window: `$env:MCVS_PC_GAME = "<folder>"`.

If you double-click the exe instead, it shows a numbered menu with the same commands. If it does not find the game, it
asks for the folder first: paste it, or drag `ModernCombatVersus.exe` into the window, and press Enter. Menu entry 8
asks again. After `start` ends with Ctrl+C, the menu comes back: choose `stop`, then `collect`.

## 3. Part A: the redirect test (about 5 minutes)

1. Run:

   ```powershell
   .\McvsOffline.PcSpike.exe redirect-test
   ```

2. Start Modern Combat Versus from Steam with **Play**. Wait for the title screen or the main menu, or until it fails
   (an error box, a crash or a black screen for more than 2 minutes).
3. Quit the game. If it hangs, end it in Task Manager.
4. Run:

   ```powershell
   .\McvsOffline.PcSpike.exe redirect-undo
   ```

   It prints `MARKER WRITTEN` or `NO MARKER`, then removes the test files. Both answers are useful.

If the game does not start at all after `redirect-test`, run `redirect-undo` at once. The game then starts as before.
That result is useful too.

## 4. Part B: the main session (30 to 45 minutes)

1. Run:

   ```powershell
   .\McvsOffline.PcSpike.exe start
   ```

   Leave this window open. The server's log scrolls in it.
2. Start the game from Steam with **Play**. Then go as far as you can:
   * **Main menu.** When you reach it, type `menu` in the kit's window and press Enter. Each note you type gets a
     timestamp in the memory record.
   * **One news or event link.** Open one item in the main menu that would show a web page, such as news, an event
     banner or an inbox message with a link. Note what happens: a page inside the game, your web browser outside the
     game, nothing, or a freeze.
   * **One bot match.** Start a match, for example a bot or training match if the menu offers one, otherwise a normal
     match. The server fills it with bots. Type `match start` when it starts and `match end` when it ends. Play it to
     the end if you can.
   * **Quit the game** from its menu.
3. **Windows Firewall.** If a firewall window appears at any point, note when (menu, match start, …) and which
   program it names, then choose **Cancel**. The test works without that permission.
4. **If the game crashes or hangs**, note on which screen. End it in Task Manager if needed, then go on to step 5.
5. Back in the kit's window, press **Ctrl+C**. (If you close the window instead, the kit still stops the server and
   finishes its logs, but you then need a new terminal for the next step.) Then run:

   ```powershell
   .\McvsOffline.PcSpike.exe stop
   ```

   This puts `args.txt` back the way it was. The game starts normally again.

## 5. Part C: collect and send

```powershell
.\McvsOffline.PcSpike.exe collect
```

Run it after you quit the game and stopped `start` with Ctrl+C. If either still runs, it warns you; the zip is still
made, but the memory record may be cut short.

It asks six short questions: menu reached, bot match, the news link, firewall, graphics setting, anything else. Press
Enter to skip one. It then writes `McvsOffline-pc-spike-<date>-<time>.zip` to your desktop. Send us that file.

The zip contains:

* `summary.txt`
* the kit's own log
* the server log
* the redirect result
* the memory CSV and the sockets the game opened
* the game's debug output
* the game's crash logs from `%LOCALAPPDATA%\Gameloft`, plus readable copies
* the game's options file `app_preferences.json`, if the kit finds it (it holds the graphics settings)
* Windows event-log entries about crashes of `ModernCombatVersus.exe`
* `args.txt`, if one is present, and a list of the file names in the game folder

The zip contains no game files and no passwords. Some paths in it include your Windows user name.

## 6. Clean up

* `stop` and `redirect-undo` already removed everything from the game folder. `status` shows what is still installed.
* Delete `%LOCALAPPDATA%\McvsOffline\spike` and the exe when you no longer need them.
* You do not need Steam's "Verify integrity of game files", because no Steam file was changed. Running it does no harm.

## 7. What each result tells us

| Result (in summary.txt) | What it means | What we do next |
|---|---|---|
| **Step 1: routes in the server log.** For example `eve.*`, `auth.*`, `storage.*`, `crm.*`, `matchmaker.*` (lobbyconfigs) | How far the stock PC client gets against our server. Its Federation code is the same as on Android (§3.1). | If it reaches `matchmaker` and the in-process TS World starts, the WP1–WP2 plan stands. |
| No requests at all | The game did not read our `args.txt`. Possible causes: a `*_args.txt` file (listed in the summary), the working directory, or no write access. | Fall back to passing the arguments through `MainLoop.Start(mode, args)` (§3.2, critic note 3). |
| **Install folder writable: NO** | Steam's folder does not let you write files into it (critic note 3). | Also the `MainLoop.Start` fallback, plus the data root in `%LOCALAPPDATA%`. |
| **Game debug output:** the first exception lines | The first failure of the stock client. The expected one (**[I]**) is a headless problem in the TS World, for example `InputSettingsComponent.OnStart` → `World.GetUI()` (R1, R4). | WP4 starts from this failure. A failure in native code with no managed exception points at R1 instead. |
| **Step 2: MARKER WRITTEN.** `location=` ends in `McvsOffline\spike\EngineBridge.dll`. | The CLR reads `ModernCombatVersus.exe.config` and honours `<codeBase>` for this mixed-mode exe (unknown 1). `cwd=` answers unknown 4: the working directory when the game starts. | Option A: the player install writes no Steam file (WP3). |
| **NO MARKER**, but the game started | The CLR ignores the config, or loaded the original DLL (R3). | Option B: patch the files in place, with backups. |
| The game failed to start during Part A | The redirect broke loading. The Windows event-log entry gives the reason. | Look at the event-log entry. Option B is likely. |
| **Step 3: peak virtual MB** out of 2048 | Address space used by the 32-bit exe, which is not large-address-aware. This covers the client plus the TS and GS Worlds, if they ran (R2). | Above about 1600 MB: decide on memory measures before WP2. Your graphics setting and `app_preferences.json` go into the same assessment (critic note 4). |
| **Sockets:** non-loopback UDP or TCP listeners, and a `:51750` listener | Whether a GS or ENet socket binds an address outside your PC, which brings a firewall prompt (R7, unknown 8). The `:51750` line shows whether the Finder registered in http.sys without administrator rights (unknown 7). | If a socket binds outside your PC, add a bind patch or document the firewall prompt. |
| **The news link** (your answer) | What `BrowserComponent.OpenUrl` does on PC (unknown 5). | Decides how the settings page opens on PC: the game's browser, the Steam overlay or the system browser (§3.4). |
| **Firewall** (your answer) | R7 | |
| **Crash logs and event log** | Crash details that the debug output may have missed. | |

## 8. For maintainers

* Source: `tools/pc-spike/`, a net8 console. It reuses:
  * the server library that `hosts/standalone` runs
  * the embedded host's `ArgsFile`, `ManagedArgs` and `DeviceIdentity`. `args.txt` gets the same managed block as on
    Android.
  * the patcher's `IlPatch`. The marker edit `EngineBridgeMarkerPatch` checks the SHA-256 IL fingerprint of
    `EngineBridge.Class1.Start` and refuses any other body.
* Tests: `tests/McvsOffline.PcSpike.Tests`. They run on Linux and use the real Steam files from
  `/home/user/mcvs-work/pc/install` (or `MCVS_PC_INSTALL`) when present: the marker patch against the real
  `EngineBridge.dll` (fingerprint, ILVerify, and an execution on net8 with a stand-in `Gameloft.Engine`), the data
  view, the crash-log decryption against the game's own `Gameloft.Logs.dll`, and the PE header. Without the install
  those tests skip, unless `MCVS_REQUIRE_PC=1`. The Steam files are only read.
* Build the exe for the owner on any OS. Built on Linux with .NET SDK 8.0.426, the output is one self-contained,
  compressed file of 35,937,976 bytes (34.3 MiB; build 10). It bundles the .NET 8 runtime, the server, the patcher and Mono.Cecil,
  and no game file:

  ```sh
  dotnet publish tools/pc-spike/McvsOffline.PcSpike.csproj -c Release -r win-x64 -p:McvsPcSpikePublish=true -o artifacts/pc-spike
  ```

* What the kit only reports, without doing it: Windows-only code paths that the Linux tests cannot run. These are the
  registry lookup, the `OutputDebugString` listener (`DebugOutputCapture`, the DBWIN protocol), `netstat` and
  `wevtutil`. Each one only reports when it fails.
* Not used: the game's `-LogElkEnabled -LogElkToFile` switches. They rewrite `logorama.log` in the game folder for every
  entry (`File.Create`), so the file keeps only the last line.
