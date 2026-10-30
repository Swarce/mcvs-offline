# Design: the PC (Steam) port and the Android `game` manifest flags

Status: **implemented up to WP5a plus the WR2 fixes (build 14, October 2026; build 15 changes only the server DLL; build 16 drops the `<qualifyAssembly>` entries again and adds the start diagnostics; build 17 adds the `inbox` patch and its witness; build 18: our host no longer loads `Game` by a partial name at boot, and a `GAME FOLDER` load logs its loader's stack, §8.9); WR2 done (§13.0, §14), WR3 and WP5b pending** (designed October 2026; revised after one review round, Appendix A; the expected WR3 values in §13.1). It is the contract for the work packages in §11: everything another
package relies on (defines, hook signatures, IL sites and fingerprints, file layouts, settings keys, file formats, log
lines) is fixed here, mostly in §3 ("Day-0 contracts"). Change it only through the integrator (WP5, §11.1).

Scope: the offline mode for the Steam build of Modern Combat Versus (app 723780, depot build 6372049, game 1.17.30,
`ModernCombatVersus.exe`, 32-bit, .NET Framework 4 CLR), plus one small Android item of the same release, the manifest
patch `game` (WP-G, §10). The research baseline is [research/pc-port.md](../research/pc-port.md); it is partly outdated,
and §17 lists what this design corrects.

Evidence. The understand-phase reports and their skeptic checks live in the session scratchpad
(`/tmp/claude-0/-home-user-mcvs-offline/98d9334c-9d5f-5995-bde1-c27c1652af38/scratchpad/pcport2/`, not committed). They
are cited by short name; a `.verify` file overrides its report where they disagree, and the critic's synthesis records
every override:

| short | file | topic |
|---|---|---|
| `[QL]`, `[QL-V]` | `quicklaunch.md`, `quicklaunch.verify.md` | why the stock PC client never reached the menu; the LocalAnubis plumbing |
| `[CN]`, `[CN-V]` | `connectivity.md`, `.verify.md` | the native connectivity tracker, the marketing hit, the changed OnlinePlatform methods, the harness |
| `[ES]`, `[ES-V]` | `embedded-split.md`, `.verify.md` | the `#if` audit, the net452 build (trial `t7`), the platform interfaces |
| `[PP]`, `[PP-V]` | `patcher-pc.md`, `.verify.md` | the patcher pipeline, per-patch PC status and fingerprints, option A layout |
| `[HL]`, `[HL-V]` | `headless-pc.md`, `.verify.md` | the PC headless sites, physics and GameOptions on PC |
| `[WH]`, `[WH-V]` | `windows-host.md`, `.verify.md` | steam_api, the overlay, exit/restart, the bootstrap on Windows |
| `[SG]`, `[SG-V]` | `samsung-game.md`, `.verify.md` | the Android `isGame`/`appCategory` flags |
| `[CR]` | `critic.md` | synthesis: §1 unknowns, §2 contradictions C1-C19, §3.1 facts F1-F10, §3.2 questions Q1-Q11, §3.3 WR2 |

Other sources: the WP0 spike results (`/home/user/mcvs-work/pc/spike-results/`, `summary.txt`, `server-logs/server.log`),
the Steam install (`/home/user/mcvs-work/pc/install`, read only), the PC decompile (scratchpad `pc-decompiled/`, `PC-EN/` =
Gameloft.Engine, `PC-GM/` = Game below). Markers as in the reports: **[V]** verified, **[I]** inferred.

Fingerprints are `IlPatch.Fingerprint` (`patcher/Patches/IlPatch.cs:114-132`): SHA-256 over the locals, every instruction
with its offset and operand full name, and every exception handler.

---------------------------------------------------------------------------------------------------

## 1. Decisions and invariants

### 1.1 Owner decisions (final)

| # | decision | consequence here |
|---|---|---|
| D1 | **Data root is the game folder**: `<install>\McvsOffline\` holds `settings.json`, accounts, profiles, logs, `device-id.txt` (Android parity). Patched assemblies in `<install>\McvsOffline\bin\`, `install.json` beside them. Fallback log folder when the install folder is not writable: `%TEMP%\McvsOffline\logs` | `EmbeddedPaths` is unchanged (`hosts/embedded/EmbeddedPaths.cs:33-44`: everything under `<GameRoot>\McvsOffline`). Uninstalling the game in Steam deletes the folder and with it the saves: the docs say so and point at the settings page's save export (§16) |
| D2 | **The settings page opens in the Steam overlay by default** (`SteamAPI_ISteamFriends_ActivateGameOverlayToWebPage`, 2-parameter cdecl), with an automatic fallback to the system browser (`Process.Start`, ShellExecute). `admin.openVia` accepts `auto`, `overlay`, `system` and `game` on PC | §6.5 (including the fallback when the overlay shows no page, and the page's "Open in the system browser" action) |
| D3 | **Steam achievements and stats: allow** (stock behaviour), but the server-World double counting is fixed: H9 plus `SteamAchievementsComponent.OnStop` calling `base.OnStop()` in headless (server) Worlds (M3; the client World keeps the stock call) | §8.4 sites H9, M3; no `AllowSteamStats` guard |
| D4 | Only if WR2 shows that http.sys refuses `http://localhost:51750/` to a normal user: option (b), no admin step, our own server replaces the Finder routes | designed as the optional WP6 (§15), not implemented unless WR2 Q2 fails |
| D5 | A second Windows round (WR2) with the new kit | §13; WP5 is split around it: WP5a publishes the kit, WP5b records WR2 and finishes the docs (§11.1) |
| A | **Option A**: `ModernCombatVersus.exe.config` `<codeBase>` redirects to patched copies; no Steam file is changed | §7.4 |
| X | Excluded on PC: LAN (`lanMode`), patches `finder`, `lan`, `debloat`, `targetsdk`, `game`, `hook`; the Android installer-data restore (`hosts/embedded/Install/`) and HD textures (`Hd/`) | §5.2, §8.1 |
| G | Android side task: a new default manifest patch `game` adding `android:isGame=true` (0x010103f4) and `android:appCategory=game` (0x01010545, value 0) to `<application>`, per [SG] option A with the [SG-V] corrections | §10 |

### 1.2 Choices this design makes (the critic's open choices)

| [CR] | choice | why |
|---|---|---|
| C16 | The EngineBridge hook calls `McvsOffline.Bootstrap.Init()` (void), and `MainLoop.Start(0, null, false)` stays as it is. No `InitAndArgs` | one contract with Android; the install folder is writable (`dr0..3.bin`, [ES-V] claim 9); argv is a later additive patch only if WR2 shows a read-only install |
| C17 | Defines `MCVS_EMBEDDED` (in-process host), `MCVS_ANDROID`, `MCVS_WINDOWS` (§3.1) | the only scheme audited site by site ([ES] §2.1) |
| C18 | `ExitViaGame` posts `Gameloft.Engine.MenuManager.ExitAppAndroid()` (the game's own quit, `WCPToolkit::ExitApp` then `TerminateProcess`, [WH-V] C8); relaunch through a PowerShell helper (`Wait-Process`, then `steam://rungameid/723780`) | the single-instance mutex makes an early second start exit silently ([WH] §5.1) |
| C19 | Quicklaunch P3 stays an IL prologue (not a data override of `/Matchmaking`) | PC data packs cannot be overridden; a hook at TS-World creation would be needed anyway ([CR] F5) |
| C15 | Shared files go to `<install>\McvsOffline\exports\` and Explorer is opened on them (`explorer.exe /select,`) | no Downloads known-folder P/Invoke |
| F6 | The quicklaunch hooks live in `McvsOffline.Server.dll`, not in a separate hooks DLL; BCL-typed signatures | one `PatchContext`, no reference cycle, no Steam file at build time |
| F7 | `graphics` is **mandatory** on PC and carries the MEM rule (`Physics` never excluded) | the MEM `LowEnd` fallback would strip `Physics` from the in-process GS World |

### 1.3 Invariants of the PC port (every package keeps them; the tests in §12 guard them)

* **P1 — no Steam file changes.** The installer writes only `<install>\ModernCombatVersus.exe.config` (through
  `ModernCombatVersus.exe.config.mcvs-tmp`, §7.4), `<install>\McvsOffline\**`; the bootstrap writes `<install>\args.txt`
  (managed block) and `<install>\McvsOffline\**`. Nothing else is created, renamed or deleted in the game folder. The
  stock game writes files there itself (`dr0.bin`..`dr3.bin` from `NativeEngine.dll`, [ES-V] claim 9, [CR] critic note 3;
  `OF_PP.bin` also carries a game-run time in the WP0 listing [I]); they are the game's, not ours, and nothing of ours
  creates or removes them.
* **P2 — patches refuse anything but the verified build.** Every PC site is checked against the fingerprint or shape of
  build 6372049 before anything changes; an unknown build is refused before anything is written.
* **P3 — one corlib.** The PC server DLL references `mscorlib`, `System`, `System.Core` 4.0.0.0
  (`b77a5c561934e089`) and optionally `Gameloft.Logs` 1.0.0.0, nothing else. Every patched PC assembly references exactly
  its original references plus `McvsOffline.Server, Version=0.0.0.0` (`Gameloft.Util.dll` adds nothing). A second
  `mscorlib 2.0.5.0/7cec85d7bea7798e` must be impossible ([PP] §0.3, [PP-V] §3: ILVerify does not catch it).
* **P4 — the game never sees an exception from us.** Hooks are public static, BCL-typed, catch everything and log; every
  patched call site is guarded by a try/catch around a `[NoInlining]` forwarder, so a missing or broken server DLL
  leaves the stock behaviour.
* **P5 — Android behaves as today.** For the same options, the Android APK built from the same commit differs from a
  `main-dev` 7f24f82 build only in these entries: `McvsOffline.Server.dll` (the shared §4 changes, the define renames,
  `GameLogBridge` in its new namespace), the patched assemblies whose only change is their derived MVID (it hashes the
  server's MVID, `ApkPatcher.cs:208, 220-226`), `AndroidManifest.xml` (WP-G `game`, on by default; `--patch` without
  `game` gives today's manifest bytes) and the signature files under `META-INF/`. Every patched method's IL, every
  AssemblyRef/TypeRef/MemberRef table and every other entry is unchanged (§12.4 `AndroidOutputUnchangedTests`, §12.6).
  Build 17 adds one deliberate Android change, the default `inbox` patch (Game.dll): P5 holds for the default options
  without it, and with it only `Game.dll` differs (its values pinned in the same test).
* **P6 — no stored secret** (settings-page I2) also on PC: the ticket travels in the URL fragment to the overlay or the
  system browser only.

---------------------------------------------------------------------------------------------------

## 2. How the PC process runs with the port

```
ModernCombatVersus.exe (native, 32-bit, mutex GL_MCVS_UNIQUE_INSTANCE, SteamAPI_Init)
 └─ first WCP render callback (0x402940, [WH-V] C10; not under a debugger: PEB.BeingDebugged gate)
     └─ Wrapper.Start → EngineBridge.Class1.Start()          ← McvsOffline\bin\EngineBridge.dll via .exe.config
          ├─ try { McvsOffline.Bootstrap.Init() } catch {}    ← patch hook-pc (§8.2)
          │    cwd = exe folder; EmbeddedHost.Start(new WindowsPlatform()):
          │    settings.json, args.txt managed block, server thread (127.0.0.1:51780), watchdog
          └─ MainLoop.Start(NativeHostsDotNet, null, false)
               native MainLoop_Start reads args.txt, then OnStarted → GameApplication
                 Assembly.Load(new AssemblyName(McvsGameLoad.Qualify("Game")))   ← patch gameload (§8.9, WR2 F1)
                   = a full name → McvsOffline\bin\Game.dll via the .exe.config codeBase (a bare name got the stock copy)
                 client World; "ts" link auto-connects: Prepare (anonymous session = SteamID64)
                 GetRoom: QuickLaunch → ClientComponent.QuickLaunch   ← patch quicklaunch P2 (§8.3)
                    PcHooks.QuickLaunch: LocalAnubisManager.Start (Lobby, Controller, Finder http.sys :51750)
                    MatchmakerService.QuickLaunch("ts", ct, "localhost", 51750) → Finder → Lobby.QuickLaunch
                 Controller "connect game" → new World(headless: true) = TS (menu)    ← headless PC sites (§8.4)
                 Battle (Classic) → StartGroupPlay/automatch → AddAutomatchHostSettings  ← P3 → Finder → GS World (match)
                 BaseServer.Port restored                            ← P1 (rooms no longer advertise port 0)
```

What runs where: our Federation server (`server/**`, same code as Android) in the game process; the TS and GS Worlds as
headless Worlds in the same process (identical machinery on PC, [HL] §4, [HL-V] §9); the LocalAnubis Lobby and
Controller on loopback TCP ports and the Finder on http.sys `http://localhost:51750/` (Gameloft code, never started by the
stock PC client: [QL] §2 D1-D4).

Per layer, what changes:

| layer | Android today | PC |
|---|---|---|
| server (`server/Core`, `server/Services`) | shared | shared + §4 changes (both platforms) |
| server build | `McvsOffline.Server.Embedded.csproj`, Mono BCL from the APK | new `McvsOffline.Server.Pc.csproj`, net452 reference assemblies (§5) |
| host (`hosts/embedded`) | `AndroidPlatform` (JNI) | `WindowsPlatform` (`hosts/embedded/Windows/`, §6) |
| entry | `NicePlaySDKDLL.OnInitialize` (patch `hook`) | `EngineBridge.Class1.Start` (patch `hook-pc`) |
| patched assemblies | inside the re-signed APK | copies in `McvsOffline\bin\` + `.exe.config` (§7) |
| patch set | 17 patches + manifest/dex edits | §8.1 table |
| installer | `McvsOffline.Patcher --apk` | `McvsOffline.PcKit.exe install` (§9) and `McvsOffline.Patcher pc install` (§7.6) |

---------------------------------------------------------------------------------------------------

## 3. Day-0 contracts

Everything in this section is committed on day 0 (§11.2) and is the only thing parallel packages may assume about each
other's work.

### 3.1 Defines and guards

| define | meaning | set by |
|---|---|---|
| `MCVS_EMBEDDED` | the server runs inside the game process (any OS): `Bootstrap`, `EmbeddedHost` wiring; test-only code excluded | `McvsOffline.Server.Embedded.csproj` (Android), `McvsOffline.Server.Pc.csproj` |
| `MCVS_ANDROID` | Xamarin/Mono/bionic/JNI: libc P/Invokes, `Mono.Android`, logcat, `AndroidPlatform` | Android csproj only |
| `MCVS_WINDOWS` | .NET Framework on Windows: `WindowsPlatform`, Win32 P/Invokes, the Windows `FileSystem` branch | PC csproj only |
| `MCVS_GAMELOG` | the game-log bridge over `Gameloft.Logs` is compiled in | Android csproj always; PC csproj when `$(MCVS_PC_REFS)Gameloft.Logs.dll` exists |

Neither OS define is set by `server/McvsOffline.Server.csproj` (net8) or the test projects; they keep compiling
`hosts/embedded/**` as today ([ES] §2.1 rule). Guard patterns, one per purpose:

| guard | used for |
|---|---|
| `#if MCVS_ANDROID` | Android-only code; replaces every `#if MCVS_EMBEDDED` that means "libc/Mono/JNI" ([ES] §2.2: `FileSystem.cs:30-38,44-57,102-105` (libc `rename`, the `fsync` of `FlushToDisk`), `HttpServer.cs:85-92`, `LocalAssets.cs:66-72`, `Bootstrap.cs:60-78`, and every guarded file in `hosts/embedded/Android/`; `MediaStoreLedger.cs`, `PublicMirror.cs`, `UiCall.cs` have no guard and keep none, [ES-V] claim 3) |
| `#if MCVS_WINDOWS \|\| !MCVS_EMBEDDED` | first line of **every** file in `hosts/embedded/Windows/`, without exception: compiled into the PC DLL and the net8 tests, never into the Android DLL (P/Invoke declarations compile everywhere; they only fail when called) |
| `#if MCVS_WINDOWS` | only (a) `hosts/embedded/Diagnostics/ThreadDumpStub.cs` (the no-op `ThreadDump`; the net8 tests compile the real `Diagnostics/ThreadDump.cs`, so the stub must stay out of them), and (b) the Windows branch of a three-way `#if` in shared code. Rule for (b): the branch is **one line** that delegates to code under `MCVS_WINDOWS \|\| !MCVS_EMBEDDED` (which the net8 tests compile and run), e.g. `Bootstrap` → `WindowsBoot.Init()`, `FileSystem.Rename` → `WindowsFiles.Rename`, `NativeThreads.CurrentTid` → `Win32.GetCurrentThreadId()` |
| `#if !MCVS_WINDOWS` | shared code that must not reach the PC DLL because it uses `Install/` or `Hd/` (excluded from the PC csproj, §5.1). The complete list: `EmbeddedHost.cs:12` (`using McvsOffline.Embedded.Hd;`, error CS0234 once the namespace has no types), `:179` (`HdInstaller.Status` in `DescribeState`), `:308` (the `PrepareInstallerData(platform)` call), `:429-535` (`PrepareInstallerData`, `StartDataVerify`), `:724-731` (`hd.*` defaults) and the `install.*` defaults test on `IInstallerPlatform` in `AddEmbeddedDefaults`; `Admin/AdminHost.cs:224-225` (`HdTextures`, `HdInstalled`; null on PC); `IEmbeddedPlatform.cs:53-73` (`IInstallerPlatform`, whose `InstallerPrefs()` returns `Install.IInstallerPrefs`). Checked by the D0-b build of the PC csproj (§11.2) |
| `#if MCVS_ANDROID \|\| !MCVS_EMBEDDED` | libc code the Android DLL and the net8 build both need, never the PC DLL (no `libc` ModuleRef, P3, §12.1): `SocketReuse.NativeMethods` **and** the bodies of `TrySetReuseAddressOnly`/`GetSocketOption` (`#else` `return "not supported on this build";` / `return -1;`); `FileSystem.NativeMethods.open/fsync/close` and the body of `FlushDirectory` (`#else` a no-op, keeping the directory fsync for the standalone Linux server); `NativeThreads.NativeMethods.gettid` (`#else` absent, `CurrentTid` delegates as above) |
| `#if MCVS_GAMELOG` | `hosts/embedded/Diagnostics/GameLogBridge.cs` (moved from `Android/`, its `#define ENABLE_LOGGER` kept on line 1) |

### 3.2 Hooks in `McvsOffline.Server.dll`

Rule (unchanged, `IlPatch.ImportServerMethod`, `IlPatch.cs:79-94`): public static, parameter and return types from
`mscorlib`/`System`/`System.Core` only, never throw, return quickly on the calling thread.

Used unchanged on PC (they exist with these signatures, [PP] §1.3):

```csharp
McvsOffline.Bootstrap.Init()                                                         // hook-pc
McvsOffline.GameHooks.HeadlessComponentException(string, string, Exception)          // headless-engine
McvsOffline.GameHooks.PhysicsMultithreading(bool, bool) : bool                       // physics
McvsOffline.GameHooks.EnterPhysicsStep() : bool; ExitPhysicsStep()                   // physics
McvsOffline.GameHooks.AllowNativeOnlineInit(string) : bool                           // nativeonline
McvsOffline.GameHooks.StoreItemException(object, Exception)                          // store
McvsOffline.AdminHooks.AccountMenuButtons(IEnumerable<object>) : IEnumerable<object> // adminpage
McvsOffline.AdminHooks.OpenPage(string, object)                                      // adminpage, forum
McvsOffline.AdminHooks.OpenIfPageMarker(string, object) : bool                       // adminlink
McvsOffline.GraphicsHooks.GameOptionsJson(string) : string                           // graphics
McvsOffline.GraphicsHooks.GameOptionsApplied(object)                                 // graphics (PC: gets the component, §8.6)
```

New (`hosts/embedded/Windows/PcHooks.cs`, `namespace McvsOffline`, guard `#if MCVS_WINDOWS || !MCVS_EMBEDDED`):

```csharp
namespace McvsOffline
{
    /// <summary>Entry points of the PC-only patches. Part of the patch contract (PcHookContractTests).</summary>
    public static class PcHooks
    {
        /// <summary>Set by EmbeddedHost on PC before the game starts. Thread-safe; never throws.</summary>
        public static void Configure(McvsOffline.Server.Core.Log hostLog, McvsOffline.Server.Core.BootTrace boot);

        /// <summary>Patch quicklaunch P2: replaces ClientComponent.QuickLaunch(ClientLink, CancellationToken). Game thread.
        /// component: the Gameloft.Engine.ClientComponent; link: the Gameloft.Engine.ClientLink. Never returns null and
        /// never throws: every failure is a faulted Task carrying the original exception (§8.3).</summary>
        public static Task QuickLaunch(object component, object link, CancellationToken ct);

        /// <summary>Patch quicklaunch P3: prologue of MatchmakingComponent.AddAutomatchHostSettings. TS World thread.
        /// settings: the MatchmakingComponent.AutomatchSettings. Never throws.</summary>
        public static void DefaultAutomatchHost(object settings);

        /// <summary>Patch headless-diag M1: the catch (Exception) of ServerManager._AddClientAsync. Never throws.</summary>
        public static void ServerWorldFailed(Exception exception);

        /// <summary>Patch headless-diag M4: the catch (Exception) of ComponentAssemblies.CreateInstance. Never throws.</summary>
        public static void ComponentCreateFailed(Exception exception, Type type);
    }
}
```

Signature strings for `ImportServerMethod` (the patch side):

| hook | type, method | return | parameters |
|---|---|---|---|
| P2 | `McvsOffline.PcHooks`, `QuickLaunch` | `System.Threading.Tasks.Task` | `System.Object`, `System.Object`, `System.Threading.CancellationToken` |
| P3 | `McvsOffline.PcHooks`, `DefaultAutomatchHost` | `System.Void` | `System.Object` |
| M1 | `McvsOffline.PcHooks`, `ServerWorldFailed` | `System.Void` | `System.Exception` |
| M4 | `McvsOffline.PcHooks`, `ComponentCreateFailed` | `System.Void` | `System.Exception`, `System.Type` |

### 3.3 Host interfaces (`hosts/embedded/IEmbeddedPlatform.cs`)

```csharp
public interface IEmbeddedPlatform
{
    string Name { get; }                               // "android", "windows", "directory"
    string ResolveGameRoot(Log log);
    LogSink CreateSystemLogSink(LogLevel minLevel);
    // new:
    string EntryPoint { get; }                         // "NicePlaySDKDLL.OnInitialize" | "EngineBridge.Class1.Start" | "test host"
    string ArgsReader { get; }                         // "JNIBridge.NativeInit" | "MainLoop_Start" | "nobody"
    string PortInUseHint { get; }                      // one sentence for the bind-retry error (Android: adb reverse; Windows: §6.2)
}

public interface IAdminPlatform
{
    // ... the existing members, unchanged ...
    /// <summary>What this platform can do for the page; AdminHost combines it with the game-side state.</summary>
    AdminPlatformCapabilities Capabilities();          // new
}

public sealed class AdminPlatformCapabilities          // new, same file
{
    public bool OpenPage { get; set; }                 // the platform can open the page without the game's browser
    public bool ShareFile { get; set; }
    public bool SaveToDownloads { get; set; }          // Android: Download/MCVS-Offline; Windows: McvsOffline\exports
    public bool ShareText { get; set; }
    public bool Copy { get; set; }
    public bool Toast { get; set; }
    public bool ExitViaGame { get; set; }
    public bool ExitViaHost { get; set; }
    public bool RelaunchUnconfirmed { get; set; }      // the page must not promise that the game comes back
    public bool OpenInBrowser { get; set; }            // the page can be reopened in the system browser (Windows)
}

/// <summary>Android's capability rules (today's AdminHost.cs:251-272, moved unchanged). Platform-neutral and
/// unguarded (hosts/embedded/Android/AndroidJni.cs, beside the unguarded JNI table), so the net8 tests run it.</summary>
public static class AndroidCapabilities
{
    public static AdminPlatformCapabilities From(int sdk, List<string> missingJni);
}

/// <summary>Optional (Windows): opening the page without the in-game browser. PageOpener uses it when implemented.</summary>
public interface IPagePlatform                         // new, same file
{
    string[] OpenModes { get; }                        // admin.openVia values besides "auto" and "game": Windows { "overlay", "system" }
    /// <summary>Opens url by mode ("auto", or one of OpenModes); the route used ("overlay", "system") or null. Never throws.</summary>
    string Open(string url, string mode);
}
```

`AdminHost.Describe` computes `CanOpenPage = game.IsBound || caps.OpenPage`, `CanShareFile = caps.ShareFile`, …,
`CanExit = via == "game" ? caps.ExitViaGame || caps.ExitViaHost : via == "host" && caps.ExitViaHost`,
`RelaunchUnconfirmed = caps.RelaunchUnconfirmed`, `CanOpenInBrowser = caps.OpenInBrowser`, and `Platform =
platform.Name`. `AndroidPlatform.Capabilities()` is one line, `AndroidCapabilities.From(SdkInt, MissingJniMembers())`,
which computes exactly what `AdminHost.cs:251-272` computes today from `AndroidJni.Available` (moved, not changed;
`AdminHostTests.cs:60-116` move to `AndroidCapabilitiesTests` with the same assertions, §12.2); after the move
`AdminHost.cs` no longer uses `McvsOffline.Embedded.Android` ([ES] §2.5 capability seam, [ES-V] claim 8).

`server/Services/Admin/AdminContracts.cs` (D0-a): `AdminHostInfo.Platform` gains the value `"windows"`,
`AdminShareResult.Method` the values `"explorer"` and `"file"` (doc comments), `AdminHostInfo.CanOpenInBrowser` (new
bool), and the trigger constant `"browser"` (a page open the player asked to take the system browser, §6.5).

### 3.4 Patcher API (`patcher/Core/`)

```csharp
namespace McvsOffline.Patcher.Core
{
    public sealed class BuildProfile
    {
        public static readonly BuildProfile Android1_17_32;     // from KnownBuild (unchanged constants)
        public static readonly BuildProfile PcSteam6372049;     // §7.2
        public string Id { get; }                               // "android-1.17.32", "pc-steam-6372049"
        public bool IsPc { get; }
        public string GameVersion { get; }                      // "1.17.32" / "1.17.30"
        public string ClientId { get; }
        public Version CorlibVersion { get; }                   // 2.0.5.0 / 4.0.0.0
        public string CorlibPublicKeyToken { get; }             // "7cec85d7bea7798e" / "b77a5c561934e089"
        public IReadOnlyDictionary<string, Guid> PatchedOriginals { get; }   // file -> MVID
        public IReadOnlyDictionary<string, Guid> UnchangedWitnesses { get; } // PC: GameManager, Gameloft.OnlinePlatform
        public IReadOnlyDictionary<string, string> OriginalSha256 { get; }   // PC: exe + 7 DLLs; Android: empty (MVIDs only)
        public IReadOnlyCollection<string> ServerAllowedReferences { get; }
        public IReadOnlyCollection<string> ServerAllowedModuleRefs { get; } // PC: kernel32, user32, steam_api; Android: null (unchecked)
        public string GameOptionsFile { get; }                  // "GameOptions_android.json" / "GameOptions_windows.json"
    }
}

namespace McvsOffline.Patcher.Patches
{
    public sealed class PatchContext
    {
        public PatchContext(ModuleDefinition server, int port);                       // = Android1_17_32 (existing callers unchanged)
        public PatchContext(ModuleDefinition server, int port, BuildProfile build);   // new
        public BuildProfile Build { get; }                                            // new
        // Server, Port, Notes unchanged
    }

    public abstract class IlPatch
    {
        // new helpers:
        /// <summary>The value for ctx.Build; PatchException "&lt;id&gt; is not supported on build &lt;id&gt;" when it is null.</summary>
        protected string ForBuild(PatchContext ctx, string android, string pc);
        /// <summary>Like CorlibMethod, for a member of the module's existing System reference; Resolve() proves it exists.</summary>
        protected static MethodReference SystemMethod(ModuleDefinition module, string typeFullName, string name,
                                                      bool instance, TypeReference returnType, params TypeReference[] parameters);
    }
}
```

`patcher/Core/PatchPlan.cs` (WP4): `static IReadOnlyList<IlPatch> For(BuildProfile build, IEnumerable<string> optionalIds)`
(mandatory + chosen optional ids, in the build's fixed order, refusing an id the build excludes with
`ArgumentException`), and `static PatchIds Ids(BuildProfile build)` (`Mandatory`, `Default`, `Optional`, `Excluded`).
`patcher/Core/AssemblyPatcher.cs` (WP3): `public static byte[] Patch(byte[] original, IList<IlPatch> group,
PatchContext ctx, ReaderParameters reader, Guid serverMvid, Action<string> log)` = today's `ApkPatcher.PatchAssembly`
(`ApkPatcher.cs:196-218`, including `DerivedMvid`, the re-read check and the `log` parameter), made public.
`ApkPatcher.PatchAssembly` stays, with its signature, as an `internal` one-line forwarder to it, so `PatchTests.cs:32`
and `RealGamePatchTests.cs:136` need no change.

Test support (WP3, D0-c, in `tests/McvsOffline.Embed.Tests/Support/`; WP4 and WP2 consume it):
* `TestEnvironment.PcReader(string gameFolder, string serverFolder)`: `ReaderParameters` with a `PcAssemblyResolver` over
  `Net452Refs`, the game folder and the server folder (§7.3); `TestEnvironment.AssertPcCorlib(ModuleDefinition)` resolves
  `mscorlib` through that reader and asserts the file lies in `Net452Refs` (C12). Every PC real-file test reads through
  these two (§12.4).
* `PcHookServer.cs`: `PcHookServer.Source` (C# 7.3) and `PcHookServer.Compile()`, the net452 stand-in
  `McvsOffline.Server` (compiled against `Net452Refs`, version 0.0.0.0) exposing every hook of §3.2 (`Bootstrap.Init`, the
  existing `GameHooks`/`AdminHooks`/`GraphicsHooks` members, the four `PcHooks`), each recording its calls in a public
  static list. One owner (WP3); a §3.2 change goes through WP5 and updates it.

`patcher/Pc/PcArgsBlock.cs` (WP3): `BeginMarker = "-McvsOfflineBegin"`, `EndMarker = "-McvsOfflineEnd"` (equal to
`ArgsFile.BeginMarker/EndMarker`, `hosts/embedded/ArgsFile.cs:79-82`, asserted by a contract test), `bool HasBlock(byte[])`,
`byte[] Remove(byte[] existing, out bool onlyBlockLeft)`. The patcher does not link `ArgsFile.cs` (the test assembly
compiles `hosts/embedded/**` too, and a second public `McvsOffline.Embedded.ArgsFile` would collide).

### 3.5 Paths on the player's PC (D1)

```
<install>\                                   the game folder (Steam: ...\steamapps\common\Modern Combat Versus)
  ModernCombatVersus.exe.config              ours (installer; §7.4), removed by uninstall
  args.txt                                   managed block written/merged by Bootstrap at every start; uninstall strips it
  Data\                                      the game's data pack, decimal names (read only for us)
  McvsOffline\                               DataDir (EmbeddedPaths.DataDir, unchanged)
    bin\EngineBridge.dll, Game.dll, Gameloft.Engine.dll, Gameloft.Util.dll, McvsOffline.Server.dll   installer-owned
    install.json                             installer-owned (§3.10, §7.5)
    settings.json, accounts\, profiles\, admin\, tokens.json, device-id.txt, ...   server and host (as on Android)
    logs\bootstrap.log, server.log, game.log, unknown-endpoints.jsonl, crash-*.txt (crash-*-native.txt), exception-*.txt  host and server
    exports\                                 shared files (saves, logs zip, text) + Explorer (C15)
    kit\                                     the kit's own log and 'watch' output (§9)
%TEMP%\McvsOffline\logs\                     fallback log folder (IPlatformDiagnostics.FallbackLogDirectory = %TEMP%)
%TEMP%\McvsOffline\hook.txt                  hook-pc's marker: one line per start of our EngineBridge, plus McvsOfflineInit's exception (§8.2)
```

The server never writes `McvsOffline\bin\`, `install.json` or `kit\`; the installer never writes anything else in
`McvsOffline\` (uninstall keeps the data, §7.6). Temporary files of the installer end in `.mcvs-tmp` and lie next to
their target (`McvsOffline\bin\*.mcvs-tmp`, `McvsOffline\install.json.mcvs-tmp`,
`<install>\ModernCombatVersus.exe.config.mcvs-tmp`); the kit's own temporary folder is `%TEMP%\mcvs-pckit-<guid>\`.

Not ours, never touched: the game's own files in the install folder (`dr0.bin`..`dr3.bin`, `OF_PP.bin`, P1; `GameVersion`
and the exe's crash dump `core.dmp`, WR2 F9: `GameFolder.GameWrittenNames`) and the native engine's per-user files outside
it: `GDID.bin`, `DC.bin`, `HDIDFV.bin` in Documents (`SHGetFolderPathW(CSIDL_PERSONAL)`), its save root under `%APPDATA%`
(built from `getenv("APPDATA")`, the exe name and `gameloft`) with `MyLocalStorage`, the PC options file (the in-game
graphics quality `Performance`; the PC has no `app_preferences.json`), and its crash logs under `%LOCALAPPDATA%\Gameloft`
([WH-V] C11, [CR] §3.2, WR2 F7/F9). Neither our uninstall nor Steam's removes them (§16). `core.dmp` is written by the
exe's own unhandled-exception filter (full memory, `CREATE_ALWAYS`: only the last crash's survives; 1.5-2 GB when it
works): the listing records its size and time without hashing it, and `collect` never zips it.

### 3.6 Settings keys (settings.json)

| key | default | PC meaning | notes |
|---|---|---|---|
| `admin.openVia` | `auto` | `auto` = overlay when `IsOverlayEnabled`, else system browser, and the system browser also when the previous overlay open showed no page (§6.5); `overlay` = overlay only; `system` = system browser only; `game` = the game's own `BrowserComponent.OpenUrl` (the overlay, after login) | file-only (settings-page denylist, `SettingsSchema.cs:162`). `jni`/`post` on PC and `overlay`/`system` on Android are treated as `auto` with one WARN |
| `admin.restartVia` | `game` | `game` = `MenuManager.ExitAppAndroid` + relaunch helper; `host` = helper + `Process.Kill`; `none` | as on Android |
| `embedded.physics.keepPhysicsLayer` | `true` | the `graphics` hook removes `Physics` from every `excludedLoadingLayers` of GameOptions_windows.json | new; read by the host at boot (restart); ignored on Android; label "Keep the levels' Physics layer" in `SettingsSchema.AdvancedList` |
| `graphics.profile` | `auto` | `mid` → MidGPU, `high` → HighGPU; `ultra` → HighGPU with a note (no High_Ultra on PC) | existing key |
| `graphics.fullProfile` | `false` | as on Android (forced profile keeps its own world options) | existing |
| `embedded.nativeOnlineSdk` | `false` | as on Android (patch `nativeonline`) | existing |
| `lanMode` | `false` | **no effect on PC**: the host overrides it to false for the host and the server copy (one WARN when the file says true) | LAN is excluded (X) |
| `install.*`, `hd.*`, `notifications.*`, `diagnostics.publicLogCopy`, `diagnostics.threadDump` | — | not written by the PC host (`AddEmbeddedDefaults` gates them, §6.2), and not shown on the settings page when `AdminHostInfo.Platform == "windows"` (`SettingsSchema` platform filter, §4.6) | |
| `admin.accountMenu.hideDeadButtons` | `true` | **no effect on PC**: the Steam build's Account list (`GetAccountMenuButtonList` on Win32) is Redeem code and GameCenter (`snsId 1`), with no Facebook, Google or Restore entry to drop, and a list with an `snsId` entry is never centred | not shown on the settings page when `AdminHostInfo.Platform == "windows"` (`SettingsSchema.WindowsHidden`, §4.6) |
| `pc.finder` | — | reserved for WP6 (D4), not read before WP6 | |

### 3.7 Environment variables and build outputs

| name | meaning |
|---|---|
| `MCVS_PC_REFS` | the Steam install folder (default `/home/user/mcvs-work/pc/install`, exported by `scripts/build.sh` when it exists): `Gameloft.Logs.dll` for the PC build, the real PC assemblies and `Data\` for every PC test |
| `MCVS_REQUIRE_PC=1` | PC real-file tests fail instead of skipping when `MCVS_PC_REFS` lacks a file (set by `build.sh` when `$MCVS_PC_REFS/Gameloft.Engine.dll` exists) |
| `MCVS_REQUIRE_PC_E2E=1` | `PcInstallerTests.RealFolderEndToEnd` must install with every real patch (any refusal fails); without it the test skips while a Day-0 skeleton (`hook-pc`, `headless-diag`, `quicklaunch`) still refuses. Set by the WP5a run (§12.6), not by `build.sh` |
| `MCVS_NET452_REFS` | the .NET Framework 4.5.2 reference assemblies (default `~/.nuget/packages/microsoft.netframework.referenceassemblies.net452/1.0.3/build/.NETFramework/v4.5.2/`): the BCL directory of every Linux PC patch run and ILVerify |
| `MCVS_ILVERIFY` | ilverify path (default `~/.dotnet/tools/ilverify`) |
| `artifacts/bin/McvsOffline.Server.Pc/<config>/McvsOffline.Server.dll` | the PC server DLL |
| `artifacts/pc-kit/McvsOffline.PcKit.exe` | the kit (`dotnet publish tools/pc-kit/... -r win-x64 -p:McvsPcKitPublish=true -o artifacts/pc-kit`) |

### 3.8 Log lines other packages and WR2 rely on

| line (prefix) | written by | when |
|---|---|---|
| `[boot] runtime: CLR <v>, mscorlib <file version>, Release <n> (<name>), <FrameworkDescription>; process x86, LAA no; OS <build>` | WP2 host | once at start (PC) |
| `[boot] assemblies: EngineBridge <path> (McvsOffline\bin\|GAME FOLDER), Gameloft.Engine …, Game …, Gameloft.Util …, McvsOffline.Server …` | WP2 host | as each loads (`AppDomain.AssemblyLoad`); `GAME FOLDER` is a WARN (the redirect did not apply, Q1) |
| `[boot] load stack: <name> (GAME FOLDER) was loaded by (innermost first):` + up to 25 indented `at …` frames, or `[boot] load stack: <name> (GAME FOLDER): already loaded when the host started watching, so its loader is unknown` (build 18) | WP2 host (`PatchWatcher.ReportLoader`) | WARN right after a `GAME FOLDER` assemblies line: `Environment.StackTrace` inside the `AssemblyLoad` event, from the loader's caller on (our handler, `AppDomain.OnAssemblyLoadEvent` and the `System.Reflection`/`System.AppDomain`/`System.RuntimeType*` loader frames cut, `PatchWatcher.LoadStack`); not an `assemblies:` line, so the kit does not count it |
| `[boot] patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes, store yes, inbox yes` (`inbox` since build 17) | WP2 host (`GamePatchProbe`, witnesses of §3.9) | once `Game` and `Gameloft.Engine` are loaded; a missing witness is `NO`, or `off (not applied)` when `install.json` `options.patches` does not list that patch (`--patch none` leaves out the default `store`) |
| `[boot] <name>_args.txt present: the engine ignores args.txt` | WP2 host | WARN at start when a `*_args.txt` is in the game folder (also in the status) |
| `[boot] install: current` / WARN `[boot] install: stale: …` | WP2 host | optional boot check (§6.2) |
| `[pc] quicklaunch: …` | `PcHooks` | §8.3 |
| `[McvsOffline] headless world: <Type>.<Event> threw …` | existing H3 hook | unchanged |
| `[McvsOffline] server world: …`, `[McvsOffline] component <type> was not created: …` | `PcHooks` M1/M4 | §8.5 |
| `[boot] graphics: …` (PC adds `MEM profile`, `MEM rule on, removed <n>` or `MEM rule off`, `excludedLoadingLayers`, `, overridden by the in-game graphics quality` when the loaded GPU profile is not the optimum and none was forced) | `GraphicsHooks` | §6.7, §8.6 |
| `[boot] graphics: in-game graphics quality Performance <n> (<Low\|Mid\|High>)` (or `Performance not set (the game's own choice)`; start values, then `, changed in-game to <n> (applies at the next start)` after a menu change)`, RenderScale <f>, FPS <f>, Vsync <b>, FullScreen <b>` | `GraphicsHooks` (PC) | once, when the client World's `GamePreferencesComponent` has loaded; the `[status]` line's `graphics:` part repeats it |
| `[status] …; memory: private <n> MB, virtual <n> MB (<p> % of 2048 MB), GC heap <n> MB` | `WindowsPlatform.StatusExtras` (PC) | every status line (WR2 F9) |
| `admin: page opened (trigger …, via overlay\|system\|game\|jni\|post)`; PC adds `, overlay showed no page last time` | `PageOpener` | §6.5 |
| `connectivity: type=<t> status=<0\|1> details="<text>"` | WP1 route | each tracker post, INFO, never the `device_id` |
| `[embedded] MCVS Offline <v> embedded host starting (platform windows, pid <n>)` | host (`HostProcess`, PC only) | once per start; bootstrap.log holds every start (WR2 F3) |
| `[watchdog] STALL: no game-thread work item ran for <n> ms; frame clock …; suspendUpdateInBackground=…; pid <n>` | watchdog (pid on PC only) | at 5, 15, 60, 180 s of a stall; the kit's `watch` dumps the pid it names at 60 s (§9.2, WR2 F4) |
| `[crash] hooks: AppDomain ok, unobserved-tasks ok (event), native filter ok (previous filter 0x…)` | host | once at start (PC: the native filter of §6.4) |

### 3.9 Patch witnesses

A witness is a member a patch adds to the patched assembly; its presence means "this patch is applied" (a second
application is refused on it, `GamePatchProbe` reports it, `PcInstaller` step 6 re-reads it). WP4 creates them, WP2 and
WP3 only look them up by these names:

| patch id | assembly | witness (type, member) | already exists on Android |
|---|---|---|---|
| `hook-pc` | `EngineBridge.dll` | type `EngineBridge.Class1`, method `McvsOfflineInit` (private static, `BootstrapHookPatch.HelperName`) | the same name in `hook` |
| `quicklaunch` | `Gameloft.Engine.dll` | type `Gameloft.Engine.McvsQuickLaunch` (internal, abstract sealed) | no |
| `headless-diag` | `Gameloft.Engine.dll` | type `Gameloft.Engine.McvsWorldDiagnostics` (internal, abstract sealed) | no |
| `graphics` | `Gameloft.Engine.dll` | type `Gameloft.Engine.McvsGraphics` (`GraphicsPatch.HelperType`) | yes |
| `gameload` | `Gameloft.Engine.dll` | type `Gameloft.Engine.McvsGameLoad` (internal, abstract sealed; `GameAssemblyLoadPatch.HelperType`, §8.9) | no |
| `store` | `Game.dll` | type `Game.McvsStoreSafety` (`StorePatch.SafetyType`, `GamePatchProbe.StoreSafetyType`) | yes |
| `inbox` | `Game.dll` | type `Game.McvsInbox` (`InboxPatch.HelperType`, `GamePatchProbe.InboxType`; build 17) | yes |

The constants live in the patch classes (WP4) and are duplicated as string literals in `GamePatchProbe` (WP2) and
`PcInstaller` (WP3); `PcHookContractTests` (§12.4) asserts the three copies are equal. `inbox` is in
`PcInstaller.Witnesses` since build 17 (`["inbox"] = new PcWitness("Game.dll", "Game.McvsInbox", null)`, with its row in
`PcHookContractTests.TheWitnessNamesAreTheDesignsAndTheirCopiesAgree`), so the kit's `verify`/`status` re-read it like
`store`; `PcInstallerTests.RealFolderEndToEnd` checks every recorded witness in the installed copies (the default PC set,
`inbox` included) and ILVerifies them.

### 3.10 `install.json` (format 1)

WP3 writes it (§7.5 has the full example); WP2's boot check (§6.2) and the kit's `status` read it. Fixed on day 0:
UTF-8 without BOM, MiniJson-compatible, keys in the order of §7.5. Readers other than `PcInstaller` may rely only on:

| key | type | meaning |
|---|---|---|
| `format` | number, `1` | a reader refuses any other value ("unknown install.json format", no stale check) |
| `kind` | string `"mcvs-pc-install"` | |
| `originals.<file>.sha256` | lower-case hex | `<file>` ∈ `EngineBridge.dll`, `Game.dll`, `Gameloft.Engine.dll`, `Gameloft.Util.dll`: the game-folder original at install time |
| `written.<path>.sha256` | lower-case hex | `<path>` relative to the game folder with `/` |
| `options.port` | number | the port the installer was given |

Every other key is installer-private and may change without notice.

---------------------------------------------------------------------------------------------------

## 4. Server changes (WP1; both platforms unless marked)

### 4.1 The connectivity tracker route

The native `ConnectivityTrackingManager` (in `NativeEngine.dll` **and** every Android `libnativeengine.so`, [QL-V] §12,
C2) locates `online_connectivity` through pandora and posts a form; the body is never read, 200 ends the event, 5xx and
a 3 s timeout make it retry ([CN] §1.2, [CN-V] §3).

* `server/Services/Pandora/ServiceUrls.cs`: add `"online_connectivity"` to `NativeServices` (`:43-46`), so
  `GET /pandora//locate?service=online_connectivity` answers without the "unknown service" warning.
* New `server/Services/Misc/ConnectivityTracker.cs`, registered from `MiscService.Register` (one line):
  `router.Any("online_connectivity/connectionstatus", "misc.connectivity", …)` (an exact route, order 0, so it wins
  over the `ServicePrefixAliases` catch-all). The handler catches everything and **always** answers `200 {}` (a 500 from
  the router would make the client retry). It reads `type`, `status`, `connectionstatus` with `request.Param` (form
  first), accepts any `type` (the native names are `auth`, `sp`, `gs`, `chat`, `shop`, `matchmaking`, `lobby`, `data`,
  `mpmatch`, only `auth` ↔ 0 proven, [CN-V] §2), a missing `port`, a body starting with `&` ([CN-V] §2, §11), and logs
  one INFO line `connectivity: type=<t> status=<s> details="<≤100 chars>"`, never `device_id`.
* `docs/protocol/misc.md`: the route, why 200, and that it disappears with `nativeonline` (Q8).

### 4.2 Redaction: MAC addresses and SteamID64

`server/Core/Http/LogRedaction.cs` (one scanner, no regex, idempotent, fast path; `:7-35`) gains three rules:

* **MAC.** The value of a form/query parameter or JSON string member named `device_id` or `global_device_id` that is
  MAC-shaped (six pairs of hex digits separated by `:` or `-`, also with `%3A`/`%3a`) becomes `<redacted-mac>`. Any
  other value (our own `mcvs` + 32 hex ids, `Unknown`) is left alone ([CN] §1.4, [CN-V] §5, §11.2). New trigger
  substring `device_id`.
* **SteamID64.** A run of exactly 17 ASCII digits starting with `7656119`, not preceded or followed by a letter or
  digit, keeps its first 4 and last 4 digits: `76561198366698514` → `7656…8514` (the form of `Credential.Mask`). It
  appears as the anonymous user name (`username=`, `anonymous:<id>`, [QL] §4.1). New trigger substring `7656119`.
* **Access tokens** (WR2 F6). `mcvs` + exactly 8 hex digits (the issuer, `ServerContext`'s instance id) + `.` + 32 or
  more hex digits (the whole hex run is taken), anywhere in a line, also inside a word → `mcvs…<redacted>` (the form
  the `access_token=` rule writes). The game's `Session.GetAccessToken` prints the token at Debug
  (`… succeeded: mcvs59915ef0.e681…`), hundreds of lines per start, and game.log goes into collect and share logs.
  `mcvs` is no trigger substring (every McvsOffline path contains it): the fast path runs a second test besides the
  case-insensitive trigger list, an ordinal search for `mcvs` followed by the token's shape, so lines that only carry
  paths stay on the fast path. Shared: `Redact` runs on every log line, so server.log and bootstrap.log on Android
  change too (the 401 diagnosis line reads `token mcvs…<redacted> is not known here`).

`server/Core/Http/RequestLogger.cs` applies `LogRedaction.Redact` to the INFO summary, the DEBUG dump, the WARN
"UNKNOWN ENDPOINT" dump **and** each `unknown-endpoints.jsonl` line (all four carried the MAC in the spike, [CN-V] §5).

### 4.3 Newsfeed `end_date` (F10)

PC's `NewsFeedData.Newsfeeds` dictionary makes the inbox message take the item's dates when they differ
(`PC-GM/InboxComponent.cs:465-491`); our item's timestamp is Monday 00:00 UTC of the current week, so on PC the
MCVS Offline inbox item expired at every week rollover ([CN-V] §8). `server/Services/Crm/CrmNewsfeed.Item` adds
`"end_date": TimeFormat.Iso8601(WeekStart(now) + 37 days)` after `"timestamp"` (at least 30 days ahead; on Android the
expiry becomes this date instead of now + 30 days, the same behaviour). Optional in the same package:
`CrmDocumentValidator` warns when two newsfeed items share an `asset` and the first has no `end_date`. A missing
asset is the key "" (`SafeGetString`'s default), ours; `CrmDocumentBuilder` puts our item before the first overlay
item of its key, so an overlay item without asset and `end_date` cannot give our message its dates.

### 4.4 Client id per platform

* `server/Core/McvsConstants.cs`: `ClientIdSteam = "2620:70425:1.17.30:steam:steam"` ([QL] §1), `KnownPlatforms =
  { "android", "steam" }`, and `static bool IsGameClientId(string clientId)` = `GameRequest.IsClientId(clientId)` and
  the fourth segment in `KnownPlatforms`. `ClientId` stays the Android default.
* `server/Services/Admin/CurrentPlayerResolver.cs:59-63`: the "session" rule requires `McvsConstants.IsGameClientId
  (last.ClientId)` instead of `IndexOf(":android:")` ([ES] §7.2).
* `server/Services/Clans/ClanStore.cs:359`, `server/Services/Wall/WallService.cs:60`: the `"game"` value is the
  caller's client id (token or path), falling back to `McvsConstants.ClientId`.
* `server/Services/Eve/EveService.cs:73-74`: no "answering anyway" note for either known id.
* Invariant kept: eve's `urls` object never carries `status`/`code` (PC's `Configuration.ParseStatus` no longer knows
  `critical`, [CN] §4); `tests/McvsOffline.Tests/Auth/FedClientReplay.cs:155` drops `"critical"`.
* Invariant kept: `GET /` answers at once. The PC error popup awaits `Connectivity.IsInternetAvailable` whose 2 s cap is
  not applied ([QL-V] §5).

### 4.5 `DataPack` decimal layout (C11)

`server/Core/Content/DataPack.cs`: the PC pack is flat, `Data/<decimal crc32>.f`, with `MCVS.game` and
`Config/EngineConfig.json` under their own names ([ES] §7.1, [ES-V] claim 11: `Config/glue.json` → `187993782.f`,
`Config/crmpreconfiguration.json` → `3186060502.f`, 15,922 flat entries). Add `public static string DecimalPath(string
logical)` (`crc.ToString(CultureInfo.InvariantCulture) + ".f"`) and make `Locate` try hex-directory, then decimal-flat,
then plain. This also makes the two `../Data` fallbacks (`CrmPreconfiguration.cs:181-185`,
`LiveOpsService.cs:111`, [ES-V] claim 9) dead on PC; they stay for the standalone host.

### 4.6 Small Windows-safety items in shared code

* `server/Core/Storage/FileSystem.cs`: `Rename` three ways: `#if MCVS_ANDROID` libc `rename`; `#elif MCVS_WINDOWS` the
  one line `WindowsFiles.Rename(source, destination);`; `#else` `File.Move(src, dst, true)` (net8). New
  `server/Core/Storage/WindowsFiles.cs` (guard `MCVS_WINDOWS || !MCVS_EMBEDDED`, so the net8 tests compile it):
  `MoveFileExW(src, dst, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)` (kernel32, `CharSet.Unicode`,
  `ExactSpelling = true`, `SetLastError = true`) behind an injectable `Func<string, string, int>` (0 or the Win32 error),
  retried through `FileSystem.Retry(Func<int> attempt, int attempts, int delayMs)` up to 5 times 20 ms apart on
  `ERROR_SHARING_VIOLATION` (32) and `ERROR_ACCESS_DENIED` (5) (antivirus, an open reader), then an `IOException` naming
  the Win32 error. `FlushToDisk`: libc `fsync` only under `MCVS_ANDROID`, else `stream.Flush(true)`. `NativeMethods`:
  `rename` under `MCVS_ANDROID`; `open`/`fsync`/`close` under `MCVS_ANDROID || !MCVS_EMBEDDED`, and so is the body of
  `FlushDirectory` (`#else` a no-op): the Android DLL and the standalone Linux server keep the directory fsync, the PC DLL
  has no `libc` ModuleRef. Without this split every write fails on Windows ([ES] §0.3, [ES-V] claim 4).
* `server/Core/Http/HttpServer.cs:85-92` → `MCVS_ANDROID` (no SO_REUSEADDR on Windows, no spurious warning, [ES-V]
  "missed" 7). `SocketReuse.cs`: `NativeMethods` **and** the bodies of `TrySetReuseAddressOnly` and `GetSocketOption`
  under `#if MCVS_ANDROID || !MCVS_EMBEDDED`, with `#else` `return "not supported on this build";` and `return -1;`
  (the public API stays, so nothing else needs a guard; the only caller is the `MCVS_ANDROID` block of `HttpServer`).
* `server/Services/Assets/LocalAssets.cs:66-72` → `MCVS_ANDROID` (`SHA256.Create()` on PC; `SHA256Managed` throws
  under the FIPS policy on this native-hosted exe, [ES-V] claim 10).
* `server/Services/Admin/Logs/LogTail.cs`: open readers with `FileShare.ReadWrite | FileShare.Delete`, so a log
  rotation on Windows is not refused while the page reads the tail ([ES] §4.3).
* `server/Services/Admin/Saves/AdminFiles.cs:85`: also refuse the Windows reserved names (`CON`, `PRN`, `AUX`, `NUL`,
  `COM1`-`COM9`, `LPT1`-`LPT9`, with or without an extension) and names ending in `.` or space, on every platform.
* Case-insensitive file names ([ES] §4.3, [ES-V] claim 10 "plausible"): NTFS folds case, so two names that differ only in
  case are one file on PC. WP1 audits every place that turns user text into a file or folder name
  (`Admin/Saves/AdminFiles.cs`, `Admin/Saves/ImportUploads.cs`, the export names of `SaveTransfer`, alias-keyed account
  files if any) and, on every platform, checks collisions with `StringComparison.OrdinalIgnoreCase` (a new name equal to
  an existing one ignoring case is treated as the existing one, or gets the ` (2)` suffix where the site already
  uniquifies). WP2 does the same in `WindowsShare` (§6.3). The audit result (sites, and "none" where a site is keyed by
  our own ids) goes into the WP1 commit message.
* `server/Services/Admin/SettingsSchema.cs`: a platform filter `SettingsSchema.GroupsFor(string platform)` used where
  the page's schema is served (the platform is `AdminHostInfo.Platform`): on `"windows"` the `hd.*`, `install.*` and
  `notifications.*` entries are left out (labels and settings); the `graphics.profile` text gets a PC wording ("the
  game's choice for this PC … Ultra: HighGPU on PC, which has no High_Ultra"); `embedded.physics.keepPhysicsLayer` gets
  its label and a description (§3.6). Android's schema output is unchanged (asserted by the existing schema tests).
* `server/Content/admin/index.html`: a `windows` sentence wherever the page says Android (`:1083` platform line,
  `:2459-2470` import folder and adb block are already gated or server-provided; [ES] §2.5); "Copies for sharing" reads
  `McvsOffline\exports` on Windows; an "Open in the system browser" button shown when `CanOpenInBrowser`.
* `server/Services/Admin/ActionsApi.cs`: `POST actions/open-in-browser` (session required like every action): mints a
  fresh page URL with `IAdminPortal.MintPageUrl("browser", screen)` and calls `IAdminHost.OpenPage(url, "browser")`;
  answers `{ "ok": <bool> }` (§6.5).

Not a work item: the "culture-sensitive sorts" of [ES] §4.3 compare `int` keys (C9).

### 4.7 The real-client harness on the PC DLLs (C7)

A live run gave 28/38: every failure was `System.Security.Permissions` missing (the PC `Newtonsoft.Json` 7.0.1 is the
net45 build), and the PC folder can never be `MCVS_REFS` (no `mscorlib.dll`, `build.sh:38-44`) ([CN-V] §10).

* `tests/McvsOffline.Tests/McvsOffline.Tests.csproj`: `PackageReference System.Security.Permissions` 8.0.0 (same public
  key token; the default context resolves it, the custom contexts defer `System.*` to it).
* The four loaders become per folder: `Conformance/GameClient.cs` (`GameClient.For(folder)`, a
  `ConcurrentDictionary<string, GameClient>`; `Shared` = the `MCVS_REFS` one), `Auth/RealClientConformanceTests.cs`
  (`LoadLibrary(folder)`), `Misc/ClientConformanceTests.cs` (`GameClient.TryLoad(folder)`), `Conformance/RealTables.cs`
  (stays Android: it patches `Game.dll` with the Android shapes).
* `tests/McvsOffline.Tests/TestFolders.cs` (new): `RealClientFolders()` yields `("android", MCVS_REFS)` and, when
  `MCVS_PC_REFS` has `Gameloft.OnlinePlatform.dll`, `("pc", MCVS_PC_REFS)`. The 38 tests of the live run become
  `[Theory]` over it. A PC-only `RealClientNewsfeedTests` case asserts `NewsFeedData.Newsfeeds` has one entry for our
  item and that its `EndDate` is our `end_date`.
  *As built (WP1):* the collector tests that go through `RealTables` stay Android (the bullet above); their client half
  runs on both folders as `CollectorConformanceTests.EveryServedTreeParsesIntoItsChangers` (`StandardCrm.Data` and
  `CRMComponent.GetChanger` of each client). Applying our cells to the PC data pack's `MCVS_Meta` tables (a per-folder
  `RealTables` with the PC `store` patch of WP4 and the PC data pack) is a WP4/WP5a follow-up.
* `McvsOffline.Embed.Tests` tests fed by `MCVS_REFS` (`GamePatchProbeTests`, `RealArgumentsParserTests`,
  `RealStorePatchTests`, `AdminHooksContractTests`) stay Android; their PC twins are WP3/WP4 tests.

---------------------------------------------------------------------------------------------------

## 5. The PC server build (WP1, with the WP2 split)

### 5.1 `server/McvsOffline.Server.Pc.csproj`

Based on [ES-V] trial `t7` (built with `MCVS_EMBEDDED`, ILVerify clean, AssemblyRefs exactly `mscorlib`, `System`,
`System.Core` 4.0.0.0; C8), not on trial 5 (no `MCVS_EMBEDDED`, [ES-V] claim 2):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!--
    McvsOffline.Server.dll for the Steam (PC) game process: .NET Framework 4 CLR, 32-bit, loaded from
    <install>\McvsOffline\bin by the patched Game.dll / Gameloft.Engine.dll / EngineBridge.dll (docs/design/pc-port.md).
    Same sources as the Android embedded build minus Android/, Install/, Hd/ and the Mono thread dump, plus Windows/.
    Compiled against the .NET Framework 4.5.2 reference assemblies (NuGet); no game file is needed, except
    Gameloft.Logs.dll from MCVS_PC_REFS for the game-log bridge.
      dotnet build server/McvsOffline.Server.Pc.csproj -c Release [-p:MCVS_PC_REFS=/path/to/steam/install]
  -->
  <Import Project="build/McvsOffline.Server.Shared.props" />
  <PropertyGroup>
    <TargetFramework>net452</TargetFramework>
    <OutputType>Library</OutputType>
    <PlatformTarget>AnyCPU</PlatformTarget>
    <DefineConstants>$(DefineConstants);MCVS_EMBEDDED;MCVS_WINDOWS</DefineConstants>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>                       <!-- version 0.0.0.0, as Android -->
    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
    <GenerateDependencyFile>false</GenerateDependencyFile>
    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>
    <DebugType>embedded</DebugType>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
    <MCVS_PC_REFS Condition="'$(MCVS_PC_REFS)' == ''">/home/user/mcvs-work/pc/install</MCVS_PC_REFS>
    <McvsPcRefsDir>$([MSBuild]::EnsureTrailingSlash('$(MCVS_PC_REFS)'))</McvsPcRefsDir>
    <McvsPcGameLog Condition="Exists('$(McvsPcRefsDir)Gameloft.Logs.dll')">true</McvsPcGameLog>
    <DefineConstants Condition="'$(McvsPcGameLog)' == 'true'">$(DefineConstants);MCVS_GAMELOG</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../hosts/embedded/**/*.cs"
             Exclude="../hosts/embedded/Android/**/*.cs;../hosts/embedded/Install/**/*.cs;../hosts/embedded/Hd/**/*.cs;../hosts/embedded/Diagnostics/ThreadDump.cs" />
  </ItemGroup>
  <ItemGroup Condition="'$(McvsPcGameLog)' == 'true'">
    <Reference Include="Gameloft.Logs" HintPath="$(McvsPcRefsDir)Gameloft.Logs.dll" Private="false" />
  </ItemGroup>
</Project>
```

* It joins `McvsOffline.sln` (unlike the Android build it needs no player file), so every solution build compiles the
  split ([ES] §6.1); it is added in D0-b, the first commit in which it builds (§11.2). `Directory.Build.props`
  (`UseArtifactsOutput`, `Deterministic`, `Features strict`) applies.
* Trial `t7` excluded only `Android/` and compiled `Install/` and `Hd/`; this layout is new. It compiles only with the
  `#if !MCVS_WINDOWS` sites of §3.1 guarded, which D0-b proves by building it (and `PcEmbeddedAssemblyTests` keeps
  proving: no `Install`/`Hd` type, no `libc` ModuleRef).
* `server/McvsOffline.Server.Embedded.csproj:28`: `MCVS_EMBEDDED;MCVS_ANDROID;MCVS_GAMELOG`.
* `scripts/build.sh`: export `MCVS_PC_REFS` (when the folder exists) and `MCVS_REQUIRE_PC=1` (when it has
  `Gameloft.Engine.dll`); the solution build then builds the PC DLL with the bridge.
* The DLL the kit embeds must be built with `MCVS_GAMELOG` (the kit's publish target refuses one without it: §9.1).

### 5.2 What the split changes (owners in brackets)

| file | change |
|---|---|
| `server/Core/Storage/FileSystem.cs`, `server/Core/Http/HttpServer.cs`, `SocketReuse.cs`, `server/Services/Assets/LocalAssets.cs` | §4.6 (WP1) |
| `hosts/embedded/Bootstrap.cs` | `#if MCVS_ANDROID` (Logcat, `AndroidDiagnostics`, `AndroidPlatform`) / `#elif MCVS_WINDOWS` (§6.1) / `#else` `DirectoryPlatform` (WP2) |
| `hosts/embedded/Android/*.cs` | every `#if MCVS_EMBEDDED` → `#if MCVS_ANDROID` (17 + 3 errors on net452 otherwise, [ES-V] claim 1) (WP2) |
| `hosts/embedded/Android/GameLogBridge.cs` | moved to `hosts/embedded/Diagnostics/GameLogBridge.cs`, guard `MCVS_GAMELOG`, namespace `McvsOffline.Embedded.Diagnostics` (WP2) |
| `hosts/embedded/Admin/AdminHost.cs`, `IEmbeddedPlatform.cs`, `Android/AndroidPlatform.cs`, `Android/AndroidJni.cs` | capability seam (§3.3; `AndroidCapabilities` in the unguarded part of `AndroidJni.cs`); `AdminHost.cs:224-225` under `#if !MCVS_WINDOWS`; `IInstallerPlatform` (`IEmbeddedPlatform.cs:53-73`) under `#if !MCVS_WINDOWS` (WP2) |
| `hosts/embedded/EmbeddedHost.cs` | platform texts; `#if !MCVS_WINDOWS` around `using McvsOffline.Embedded.Hd;` (`:12`), `HdInstaller.Status` in `DescribeState` (`:179`), the `PrepareInstallerData(platform)` call (`:308`), `PrepareInstallerData`/`StartDataVerify` (`:429-535`), the `hd.*` defaults (`:724-731`) and the `install.*` defaults test on `IInstallerPlatform`; thread-dump wiring only when `ThreadDump` is compiled (`:616-625`); defaults gating (§6.2) (WP2) |
| `hosts/embedded/Diagnostics/NativeThreads.cs` | `CurrentTid`: `#if MCVS_WINDOWS` the one line `return (int) Win32.GetCurrentThreadId();`, else libc `gettid` as today; the `gettid` import under `#if MCVS_ANDROID \|\| !MCVS_EMBEDDED` (WP2) |
| `hosts/embedded/Diagnostics/GameThreadWatchdog.cs` | the stall snapshot reads threads through an `IThreadSnapshot` the host passes in (`NativeThreads` over `/proc` on Android and in the tests' default; `WindowsThreads`, §6.3, on PC), so a PC stall report is not just "/proc/self/task unreadable" ([WH] §4.5, §8) (WP2) |
| `hosts/embedded/Diagnostics/ThreadDumpStub.cs` (new) | `#if MCVS_WINDOWS` (only; §3.1): `ThreadDump.Capture`, `WithDumpable<T>`, `Related` with today's signatures (`ThreadDump.cs:44,74,89`) as no-ops, because `GameThreadWatchdog.cs:344` references them statically. Outside `Windows/` on purpose: under the folder rule it would be compiled into the net8 tests next to the real `ThreadDump.cs` and collide (WP2) |

The PC DLL's ModuleRefs are then a subset of `{ kernel32, user32, steam_api }` (no `libc`, `libdl`, `libmonosgen`,
`__Internal`, `liblog.so`, `nativeengine`): the only `libc` imports in shared code are those of `FileSystem`,
`SocketReuse` and `NativeThreads`, all guarded as in §3.1.

---------------------------------------------------------------------------------------------------

## 6. The Windows host (WP2)

### 6.1 Bootstrap on Windows

`McvsOffline.Bootstrap.Init()` keeps its contract (idempotent, milliseconds, never throws; `Bootstrap.cs:18`). Its
`#elif MCVS_WINDOWS` branch is one line, `McvsOffline.Embedded.Windows.WindowsBoot.Init();` (§3.1 rule), and
`WindowsBoot` (in `Windows/`, so the net8 tests run it with injected actions and a temp game root) does:

1. `InstallDiagnostics`: `HostProcess.Id` = the process id (for the log lines of §3.8), `SelfLog.SetWriter(
   McvsOffline.Embedded.Windows.DebugOutput.Write)` (OutputDebugStringW, the spike's `DebugOutputCapture` records it),
   `CrashReporter.Install()`, plus a subscription to the public `TaskScheduler.UnobservedTaskException` event ([WH] §4.4),
   then `WindowsPlatform.InstallNativeCrashFilter()`: our top-level exception filter (§6.4, WR2 F3).
2. `InitCore`: `Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory` (try/catch; removes the relative
   `args.txt` doubt for non-Steam launches, [WH] §6.3), then `EmbeddedHost.Start(new WindowsPlatform(), new
   EmbeddedHostOptions())`.

The native engine reads `args.txt` after `Class1.Start` returns, then argv (file keys win, [WH-V] C9); the managed block
written by `EmbeddedHost.WriteArgs` is the same as on Android (`ManagedArgs`, platform-neutral, [ES] §7.3). `-ClientId`
is never written: `GameApplication.cs:1190-1193` exits silently when the client id does not contain the version
([WH-V] C8).

### 6.2 `EmbeddedHost` on PC

* Platform name and texts from the platform (`EmbeddedHost.cs:262, 372, 781, 827-828`).
* `AddEmbeddedDefaults` (`:684-750`): `install.*` only for an `IInstallerPlatform`, `notifications.*` only for an
  `INotificationPlatform`, `hd.*` under `#if !MCVS_WINDOWS`, `diagnostics.publicLogCopy`/`threadDump` only when the
  platform is Android. On PC it adds `embedded.physics.keepPhysicsLayer: true` when missing.
* `lanMode`: when the platform is Windows, `Settings.Override("lanMode", false)` on the host copy and the same override
  in `ServerOptions` for the server copy (never saved), one WARN when the file says true.
* `GameHooks.Configure`, `ConfigurePhysics` as today; `PcHooks.Configure(log, boot)`; `GraphicsHooks.Configure(...)`
  plus `GraphicsHooks.ConfigureWindows(keepPhysicsLayer)` (§8.6). This wiring is chosen at run time from the platform
  (`platform.Name == "windows"`), not by `#if`, so `EmbeddedHostTests` see it (§12.2).
* `*_args.txt` ([CR] F9): at start, when the game folder holds a file matching `*_args.txt`, one WARN `[boot] <name>
  present: the engine ignores args.txt` and the same sentence in the status (the engine reads `<prefix>_args.txt`
  instead of `args.txt`, [WH] §6.2, so our managed block would be ignored and the game would go online).
* `GamePatchProbe` runs too early on PC (`Class1.Start` runs before `GameApplication` loads `Game`, [ES] §0.6, [ES-V]
  claim 7). New `GamePatchProbe.Watch(ClientCapabilities target, Log log)`: probe now; when `Game` is not loaded,
  subscribe to `AppDomain.CurrentDomain.AssemblyLoad`, fill `target.StorePatch` when it loads, unsubscribe. The host
  passes the same `ClientCapabilities` instance in `ServerOptions.ClientCapabilities`; consumers already read it per
  use (`CollectorRuntime.cs:134-139`). Android behaves as today (Game is loaded, the probe answers at once). The same
  watcher writes the `[boot] assemblies:` and `[boot] patches:` lines (§3.8) from the witnesses of §3.9.
* Diagnostics: watchdog on (its reflection targets exist on PC, [CR] F4) with `WindowsThreads` as its thread snapshot
  (§6.3); thread dump off (`ThreadDumpStub`; no in-process CLR equivalent, [WH] §4.5); `StartBackground` does nothing;
  `MirrorStatus = "not needed on Windows: the logs are plain files in <logs dir>"`.
* `PortInUseHint` (Windows): "another program holds the port, or Windows reserves it (Hyper-V/WSL excluded port
  ranges: `netsh int ipv4 show excludedportrange protocol=tcp`); change `port` and run the kit's install again".
* Optional (P2 priority): at boot, on a background thread, compare the SHA-256 of `<GameRoot>\Game.dll`,
  `Gameloft.Engine.dll`, `Gameloft.Util.dll`, `EngineBridge.dll` with `McvsOffline\install.json`
  `originals.<file>.sha256` (only the keys fixed in §3.10; `format` other than 1 → no check, one INFO) and log
  `[boot] install: current` or a WARN `stale: Steam updated the game; run the kit's uninstall` (status line too).

### 6.3 `hosts/embedded/Windows/` (namespace `McvsOffline.Embedded.Windows`; every file guarded `MCVS_WINDOWS || !MCVS_EMBEDDED`)

| file | responsibility |
|---|---|
| `WindowsPlatform.cs` | `IEmbeddedPlatform`, `IPlatformDiagnostics`, `IAdminPlatform`, `IPagePlatform` (§6.4, §6.5); **not** `IInstallerPlatform`, **not** `INotificationPlatform` ([ES] §2.5) |
| `GameRootLocator.cs` | pure: game root = `AppDomain.CurrentDomain.BaseDirectory`, else the directory of `Process.GetCurrentProcess().MainModule.FileName`; must contain `Data\MCVS.game`; `MCVS_GAME_ROOT` env override for tests |
| `DebugOutput.cs` | `OutputDebugStringW` writer (chunked like `Logcat.Write`) and `DebugOutputSink : LogSink` (`[category] message`) |
| `WindowsBoot.cs` | §6.1 (the body of the Windows `Bootstrap` branch; actions injectable for tests) |
| `Win32.cs` | P/Invokes, every one `ExactSpelling = true` with the exact export name (`*W` for text), `CharSet.Unicode` where a string is passed, and `SetLastError = true` where the error is read: kernel32 `OutputDebugStringW`, `GetCurrentThreadId`, `GlobalAlloc`/`GlobalLock`/`GlobalUnlock`/`GlobalFree`; user32 `OpenClipboard`, `EmptyClipboard`, `SetClipboardData`, `CloseClipboard`, `CreateWindowExW`/`DestroyWindow` (the clipboard's fallback owner). Checked by `PcPInvokeTests` (§12.2) |
| `WindowsClipboard.cs` | owner window: `Process.GetCurrentProcess().MainWindowHandle` (the game's top-level window); when that is zero, a message-only window (`CreateWindowExW` class `"STATIC"`, parent `HWND_MESSAGE`) created on the calling thread and destroyed after `CloseClipboard`. Never `OpenClipboard(NULL)`: `EmptyClipboard` then sets a NULL owner and Win32 documents that `SetClipboardData` fails. `CF_UNICODETEXT` (13) with `GMEM_MOVEABLE`; `OpenClipboard` 5 attempts 50 ms apart when the clipboard is held; `SetClipboardData`'s return checked, and the `HGLOBAL` freed only when it failed (on success the system owns it); bounded by `AdminHost.MaxClipboardChars` (C4: `GameManager.SetClipboard` only stores a private string). The Win32 calls sit behind an injected interface, so the order and the free-on-failure rule are unit-tested |
| `SteamOverlay.cs` | §6.5 (`ISteamApi`, `SteamApiNative` with the declarations given there) |
| `SystemBrowser.cs` | `Open(url)`: starts `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })` on a new background thread (`IsBackground = true`, `ApartmentState.STA`, name `mcvs-browser`) and returns at once; a failure is logged from that thread (WARN `admin: the system browser did not start: <type>: <message>`, never the URL). On .NET Framework, `Process.Start` with ShellExecute from a non-STA thread starts its own STA thread and joins it around `ShellExecuteEx`, so a direct call would block the game thread (the hooks' "return quickly" rule, §3.2) for as long as the browser takes to launch |
| `WindowsShare.cs` | `ShareFile`: copy to `<DataDir>\exports\<displayName>` (a ` (2)` suffix when a file of that name exists, compared ignoring case, §4.6), then, unless the subject is null (the page's "Save", `IAdminHost.ShareFile`), `explorer.exe /select,"<file>"`; `ShareText`: write `<DataDir>\exports\<subject>.txt` (sanitised: reserved names, `\/:*?"<>|`), then Explorer; results `Ok`, method `explorer`/`file`, `PublicPath = "McvsOffline\exports\<name>"` |
| `WindowsExit.cs` | §6.6 |
| `WindowsThreads.cs` | `IThreadSnapshot` for the watchdog on PC ([WH] §4.5, §8): `CurrentTid` = `GetCurrentThreadId`; the stall snapshot from `Process.GetCurrentProcess().Threads`: per `ProcessThread` its `Id`, `ThreadState`, `WaitReason` (when `Wait`) and the `TotalProcessorTime` delta since the previous snapshot; the game thread's line first. The thread source is injectable (tests feed fixed samples) |
| `RuntimeInfo.cs` | the `[boot] runtime:` line ([WH] §7.3): `Environment.Version`, mscorlib `FileVersionInfo`, registry `HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full\Release` with the name table, `RuntimeInformation.FrameworkDescription` by reflection (4.7.1+; absent in the net452 refs), `Environment.Is64BitProcess`, registry `CurrentBuildNumber` |
| `PcHooks.cs` | §3.2, §8.3, §8.5 |

The thread-dump stub is not in this folder: it is `hosts/embedded/Diagnostics/ThreadDumpStub.cs` (§5.2), so the folder
rule of §3.1 holds without exceptions.

### 6.4 `WindowsPlatform`, member by member

| member | Windows |
|---|---|
| `Name` / `EntryPoint` / `ArgsReader` | `"windows"` / `"EngineBridge.Class1.Start"` / `"MainLoop_Start"` |
| `ResolveGameRoot` | `GameRootLocator` |
| `CreateSystemLogSink` | `DebugOutputSink` (a GUI-subsystem process has no console, [ES-V] claim 10) |
| `InstallCrashHooks` | `InstallNativeCrashFilter()` (once per process; `WindowsBoot` already calls it first thing, §6.1). **Corrected after WR2 (F3):** the CLR does *not* raise `AppDomain.UnhandledException` for the game thread. `ModernCombatVersus.exe` (mixed-mode) calls `SetUnhandledExceptionFilter(0x406e10)` in WinMain (0x40341c); that filter writes `core.dmp` (CREATE_ALWAYS, type 0x1802) and returns EXCEPTION_CONTINUE_SEARCH without calling the filter it replaced (the CLR's), so a native fault or a managed exception that unwinds into native code (MenuComponent's rethrow) reaches no managed handler: WR2's three crashes left no `crash-*.txt` and no `.NET Runtime` 1026 event. `NativeCrashFilter` (installed after WinMain's call, so on top) is a WINAPI (stdcall) delegate kept as a static root plus a GCHandle. It may run on any thread, in a damaged process, and only once, so `Install` compiles its path while the process is still healthy (review of build 14): `RuntimeHelpers.PrepareMethod` on every method of the filter and of `Win32CrashFilterNative`, `Marshal.Prelink` on the kernel32 imports they call (entry points and marshalling stubs), `RuntimeHelpers.PrepareDelegate` on our delegate and the game's, and a dry run of the formatting and the path on an empty access violation; a step that fails is named on the hooks line (`; crash path not compiled ahead (<step>: <type>)`). What managed code cannot compile ahead stays lazy and is built by the runtime on first use: the reverse-P/Invoke stub behind our function pointer, the forward stub of the game's filter if `PrepareDelegate` leaves it to the first call, and the dispatch stubs of the interface calls only a crash makes (`Create`, `Write`, `Flush`, `Close`). The re-entry guard is a thread id in a static field, not a `[ThreadStatic]` (whose storage a thread allocates on first use). Once prepared it formats into preallocated buffers with no managed allocation: the code and its name, the flags, the address with its module and offset (`GetModuleHandleExW` + `GetModuleFileNameW` into a `char[]`), the access-violation operation and target, the parameters, the thread id, the pid and `CrashReporter.LastGameFatal`. It writes `logs\crash-<utc>-native.txt` (or `%TEMP%\McvsOffline\logs` before the host knows the game folder or when the game folder's is not writable; `Install` creates that folder, since `CreateFileW` does not) through `CreateFileW`/`WriteFile` with pinned arrays and flushes it. Only then does it append the host's context (which allocates) and flush the log files. Finally it calls the previous (the game's) filter and returns that filter's result, so `core.dmp` and WER are unchanged. One report per process; a filter further down that calls back into ours on the same thread gets EXCEPTION_CONTINUE_SEARCH. The `[crash] hooks:` line ends with `native filter ok (previous filter 0x…)`. |
| `FallbackLogDirectory` | `Path.GetTempPath()` (the host appends `McvsOffline\logs`: `%TEMP%\McvsOffline\logs`, D1, C15) |
| `ApplyGameLog` | `GameLogBridge.Apply` under `MCVS_GAMELOG` (identical API in the PC `Gameloft.Logs`, [ES-V] claim 6), else `"game log: bridge not compiled in"`. On PC `Log`'s static constructor enables every level (Win32NT), so `gameLog.levels` cannot narrow the game's logging: the bridge filters instead and `game.log` keeps Fatal plus `gameLog.levels` (`fileKeepsLevels: true`; Android passes false and keeps every entry). Lines are `GameLogFormat.Line`: UTC `yyyy-MM-ddTHH:mm:ss.fffZ`, redacted (also the game's own `mcvs…` access tokens) (WR2 F5, F6). On PC it also passes a `GameFatalLines`, and every Fatal entry goes there (WR2 F3). Each Fatal line becomes `CrashReporter.LastGameFatal`, which the native filter quotes. The game's `Fatal MenuComponent.OnInvokeDelegate` and `TrackUnhandledException` lines announce a rethrow into native code (PCD `MenuComponent.cs:849-890`, `GameApplication.cs:1347`). Each such incident becomes one `exception-*.txt` headed `PROBABLY FATAL` (`CrashReporter.ReportProbablyFatal`, its own budget of 3). The report holds the exception, the game's line, the managed stack of the logging thread and the context. The TrackUnhandledException line of an incident already reported is skipped. Android passes none and is unchanged |
| `JavaStacks` / `StartBackground` | `null` / nothing |
| `StatusExtras` | `steam running yes\|no, overlay enabled yes\|no\|unknown` (`SteamAPI_IsSteamRunning`, `IsOverlayEnabled`, both with `U1` return marshalling, §6.5), the `*_args.txt` warning and the install check of §6.2 when done |
| `SdkInt` / `MissingJniMembers` | `0` / empty list |
| `OpenInGameBrowserJni(url)` | `Open(url, "auto") != null` (kept for the interface; PageOpener uses `IPagePlatform`) |
| `OpenInGameBrowserWithPostData` / `IsInGameBrowserOpen` / `QuitInGameBrowser` | `false` / `false` / `false` |
| `ShareFile` / `ShareText` / `CopyToClipboard` | `WindowsShare` / `WindowsShare` / `WindowsClipboard` |
| `ShowToast` | INFO log only |
| `ExitViaGame` / `ExitViaHost` | §6.6 |
| `PublicDownloadsDir` | `<DataDir>\exports` |
| `FreeBytes(path)` | `new DriveInfo(Path.GetPathRoot(path)).AvailableFreeSpace`, -1 on failure |
| `Capabilities()` | `OpenPage, ShareFile, SaveToDownloads, ShareText, Copy, ExitViaGame, ExitViaHost, OpenInBrowser` true; `Toast` false; `RelaunchUnconfirmed` **true** until WR2 Q9 shows the relaunch works |

### 6.5 Opening the page (D2)

`PageOpener.OpenWithWorld` (shared, `hosts/embedded/Admin/PageOpener.cs:86-127`): `via = host.OpenVia` where
`AdminHost.OpenVia` (`:153-156`) accepts `auto`, `game` and, for an `IPagePlatform`, its `OpenModes`, otherwise `jni` and
`post` (an invalid value → `auto`, one WARN). Then `game` → `OpenManaged` (unchanged); an `IPagePlatform` →
`pagePlatform.Open(url, via)`; otherwise today's switch. The lease and the `admin: page opened (trigger …, via
overlay|system|game|jni|post)` line stay. Android is unchanged.

The no-page fallback (shared, in `PageOpener`, so it is tested on Linux with a clock): the overlay call has no success
signal, but a page that loaded with its session calls `IAdminHost.PageAlive()` (`AdminApi.cs:250`). When the previous
open went to `"overlay"` and no `PageAlive` arrived within 20 s of it, the next `auto` open passes `"system"` instead of
`"auto"` and the open line adds `, overlay showed no page last time`. A player who sees nothing in the overlay therefore
gets the system browser on the next click (the overlay, a lost fragment or a lost cookie, Q3, all end the same way).
A `PageAlive` resets it. The trigger `"browser"` (the page's "Open in the system browser" action, §4.6) always passes
`"system"`.

`WindowsPlatform.Open(url, mode)` (game thread or server thread; returns quickly):
* `auto`: `SteamOverlay.TryOpen(url)` → `"overlay"`; else `SystemBrowser.Open(url)` → `"system"`.
* `overlay`: `SteamOverlay.TryOpen(url)` only. `system`: `SystemBrowser.Open(url)` only.
* The overlay call is cheap and stays inline; `SystemBrowser.Open` hands the ShellExecute to its own STA thread (§6.3),
  so `"system"` means "started", and a later failure is a WARN line.

`SteamOverlay.TryOpen(url)` ([WH] §1.2, [WH-V] C1-C3; `DllImport("steam_api")` binds the module the exe already
loaded): `SteamClient()` non-null → `GetISteamUtils(client, SteamAPI_GetHSteamPipe(), "SteamUtils009")` non-null and
`SteamAPI_ISteamUtils_IsOverlayEnabled(utils)` true → `GetISteamFriends(client, SteamAPI_GetHSteamUser(),
SteamAPI_GetHSteamPipe(), "SteamFriends015")` non-null → `SteamAPI_ISteamFriends_ActivateGameOverlayToWebPage(friends,
url)` (2 parameters; no mode argument in this SDK) → true. Every step checked, everything in try/catch
(`DllNotFoundException`, `EntryPointNotFoundException`), the reason of a false logged once. It is behind an
`ISteamApi` interface (real: `SteamApiNative`) so the order is unit-tested. Never call `GameManager.InitOverlayChanged`
or `FullScreen` (one native slot each; it would replace the game's handler, [WH-V] C4, C5).

`SteamApiNative` declarations, exactly ([WH] §1.2 table and sketch): every import `CallingConvention.Cdecl`,
`ExactSpelling = true`, the export name as `EntryPoint`. `SteamAPI_ISteamUtils_IsOverlayEnabled(IntPtr)` and
`SteamAPI_IsSteamRunning()` return a C++ `bool` in `al` (the upper bytes of `eax` are whatever the call left there,
`mov eax,[eax+0x44]; pop ebp; jmp eax`), so both carry `[return: MarshalAs(UnmanagedType.U1)] bool`; the default
4-byte `BOOL` marshalling could read "enabled" for a disabled overlay and `auto` would never fall back. The interface
version strings and the URL are `[MarshalAs(UnmanagedType.LPStr)] string` with `CharSet = CharSet.Ansi,
BestFitMapping = false, ThrowOnUnmappableChar = true` (our URLs are ASCII). Handles are `IntPtr`, `HSteamPipe` and
`HSteamUser` are `int`. `PcPInvokeTests` (§12.2) checks all of this on the built PC DLL.

Notes: the call returns no success signal; with the overlay open the game's `WhenOverlay(true)` shows the pause menu in a
match only ([WH-V] C3). The URL with `#t=<ticket>` is briefly in the browser's command line on the `system` route
(one-time, 120 s; accepted, pc-port.md §3.4). The page removes the fragment as on Android. The PC game loop keeps running
while minimised (`suspendUpdateInBackground` is set only for `PlatformType.Win32`, and the Steam build reports Steam,
[CR] F3); the 45 s watchdog lease stays (harmless).

### 6.6 Exit and restart (C18)

* `ExitViaGame(relaunch)`: when `relaunch`, start the relaunch helper; then post to the game thread
  (`GameReflection.Post`, the instance `AdminHost` holds, `GameReflection.cs:115`) a reflective call of `Gameloft.Engine.MenuManager.ExitAppAndroid()` (public static on PC,
  `PC-EN/MenuManager.cs:374-379`). True once posted; `AdminHost`'s 3 s fallback then runs `ExitViaHost` if the process
  still lives (`AdminHost.cs:476-500`).
* `ExitViaHost(relaunch)`: helper (if not started), `FlushLogsNow`, `Process.GetCurrentProcess().Kill()` (the game itself
  ends with `TerminateProcess`; nothing runs at a normal exit, so nothing may rely on shutdown hooks, [WH] §4.4, §5.2).
* Relaunch helper: `powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -Command "Wait-Process -Id <pid>
  -Timeout 60 -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; Start-Process 'steam://rungameid/723780'"`,
  `UseShellExecute = false`, `CreateNoWindow = true`. If WR2 shows the helper dies with the game (a job object), start it
  with `CreateProcess(... CREATE_BREAKAWAY_FROM_JOB ...)` instead (Q9).

### 6.7 `GraphicsHooks` on PC (host side of F7)

New `hosts/embedded/Graphics/GameOptionsWindows.cs` (platform-neutral, tested on Linux), used when
`GraphicsHooks.ConfigureWindows(keepPhysicsLayer)` was called:

* `graphics.profile` `auto`: the text is unchanged except the MEM rule.
* `mid`/`high` (and `ultra` → `high`, note `"ultra is not in GameOptions_windows.json: HighGPU"`): `profiles.GPU.selection`
  becomes `[{"<MidGPU|HighGPU>": []}]` (the PC catch-all form is an empty rule: `{"LowGPU": []}`); the PC file has empty
  `priority` objects, so nothing else changes; unless `graphics.fullProfile`, the chosen block's `reducedAiUpdateFrequency`
  takes the MidGPU value (`excludedLoadingLayers` is not set in any PC GPU block).
* **MEM rule** (when `keepPhysicsLayer`, default): in every `defaults` and `overrides` block of every profile type, the
  token `Physics` is removed from a space-separated `excludedLoadingLayers` value (`"Physics VFX"` → `"VFX"`); today only
  MEM `LowEnd`, the fallback when `GPUMEM` detection fails ([HL] §5, [HL-V] §10, decoded `GameOptions_windows.json`).
  The rule counts what it removed, and the `[boot] graphics:` line says `MEM rule on, removed <n>` (for the shipped
  file the count `GameOptionsWindowsTests` records, whatever MEM profile the machine selects) or `MEM rule off`, so WR2 sees the rule run on a machine that does not
  select `LowEnd`.
* `GameOptionsApplied(object)` on PC receives the component (no `m_options` on PC, [PP] §3.4): when the argument is not
  an `IDictionary`, read the resolved options the way the PC `ApplyGameOptions` does, by reflection on
  `Gameloft.Engine.GameOptionManager` (`GetIntOptionsCount/GetIntOptionName/GetOptionInt`, float and boolean likewise,
  `MoreMarshal.NativeStringToString/DestroyNativeString`), at most 200 entries. `NativeProfiles()` adds
  `GetLoadedProfileName("MEM")`, `DeviceTotalMemory`, `DeviceGraphicsFeatureLevel`, `DeviceCPUNumCores`. The `[boot]
  graphics:` line then names GPU and MEM profiles and the final `excludedLoadingLayers` (Q6).
* **The in-game graphics quality** (WR2 F7): the native GameOptions init reads `MyLocalStorage` › `Performance` and, when
  the member exists, applies it on top of the selection (GPU and MEM). It is the PC options menu's LocalStorage user
  preference (`GamePreferencesComponent.Performance`, 0/1/2 = Low/Mid/High by the default render scales 0.4/0.6/0.8), not
  Android's AppPreferences `GameOptions_SelectedProfile`, which the PC line no longer reads. `GraphicsHooks` reads
  `Performance` (and whether the member exists), `RenderScale`, `FPS`, `Vsync` and `FullScreen` from
  `Game.GamePreferencesComponent.instance` once its `LoadPreferencesWindows` has finished (`initPerformance` ≠ -1) in a World
  with a LocalStorage (the client's), logs them once and keeps them in the status line. It is first read lazily (status
  thread, settings page), so Performance and RenderScale are the start values the component keeps (`initPerformance`,
  `initRenderScale`); a value the menu changed before that read follows as `, changed in-game to <n> (applies at the next
  start)` (RenderScale: `(applied at once)`), and a start Performance of 0 changed since reads `0 (Low) or not set (the
  game's own choice)` (an absent member loads as 0, and the menu's write makes it exist). The applied line adds `, overridden
  by the in-game graphics quality` when the loaded GPU profile differs from the optimum and nothing was forced
  (`PcHooksReflectionTests` checks every member against the real Game.dll). Whether a forced `graphics.profile` survives an
  in-game choice is Q18 (WR3).
* **Settings page text on PC** (`SettingsSchema.GroupsFor("windows")`): `graphics.profile` states the GPU selection of
  `GameOptions_windows.json` (catch-all rules: HighGPU with `GPUMEM>=700`, `CORES>=4`; MidGPU with `GPUMEM>=550`,
  `MEM_T>=4000`, `CORES>=4`; both feature level above 9.3; then LowGPU930 at 9.3, else LowGPU), that the in-game Graphics
  quality overrides it, and that High costs memory in the 32-bit process; `graphics.fullProfile` is labelled "High profiles change the bots'
  AI rate too" and says that on PC it only changes the AI update rate (no PC GPU block sets `excludedLoadingLayers`). `GameOptionsWindowsTests` checks the numbers
  against the player's real file.

---------------------------------------------------------------------------------------------------

## 7. The patcher's PC mode (WP3)

### 7.1 Shape

No existing file moves (fewer merge conflicts than [PP] §2's reorganisation). New:

```
patcher/Core/BuildProfile.cs        §3.4 (WP3)
patcher/Core/AssemblyPatcher.cs     ApkPatcher.PatchAssembly + DerivedMvid, public (WP3)
patcher/Core/PatchPlan.cs           per-build ids and order (WP4)
patcher/Core/ServerAssemblyCheck.cs CheckServerAssembly per BuildProfile (moved out of ApkPatcher.cs:566-587) (WP3)
patcher/Pc/PcAssemblyResolver.cs    §7.3 (an IAssemblyResolver of its own, D0-c)
patcher/Pc/PcInstaller.cs           Install / Uninstall / Status / Verify (§7.6), used by the CLI and the kit
patcher/Pc/PcExeConfig.cs           §7.4
patcher/Pc/InstallManifest.cs       §7.5
patcher/Pc/PcArgsBlock.cs           §3.4
patcher/Pc/GameFolder.cs            the game folder's files, sha256 + MVID reads, "is the game running", the listing
patcher/Pc/IPcFileOps.cs            write/rename/delete seam (real: System.IO; tests: failures, Windows semantics)
```

`ApkPatcher.Run` calls `AssemblyPatcher.Patch` and `ServerAssemblyCheck.Check(server, BuildProfile.Android1_17_32)`;
its Android behaviour and output stay identical (P5). The `Pc/` classes use only `Core/` and `Patches/` (no `Zip`,
`Signing`, `Manifest`, `Dex`, `Art`, `Resources`).

### 7.2 `BuildProfile.PcSteam6372049`

| field | value |
|---|---|
| `Id`, `GameVersion`, `ClientId` | `pc-steam-6372049`, `1.17.30`, `2620:70425:1.17.30:steam:steam` |
| `CorlibVersion`, `CorlibPublicKeyToken` | `4.0.0.0`, `b77a5c561934e089` |
| `PatchedOriginals` (MVID) | `EngineBridge.dll` `b5913b9a-f9dc-42d6-bd3a-435a8280e8f1`; `Game.dll` `577aad49-7a49-41ba-86f1-ff84bbbb33d9`; `Gameloft.Engine.dll` `f8984bac-0e4b-4ce8-84d8-020035dcbb92`; `Gameloft.Util.dll` `0f8f34ba-99d2-49b3-8715-9fc261b9fa5e` |
| `UnchangedWitnesses` | `GameManager.dll` `48d3404b-15f5-49a8-84cb-db6b6af913ad`; `Gameloft.OnlinePlatform.dll` `ffdcc8c6-c6b8-4694-a296-d69b2e7c0d87` |
| `OriginalSha256` | `ModernCombatVersus.exe` `884f74611cb0daccbe889b054bda16d6ba96d5da4911e1bcd38d38d675231852`; `EngineBridge.dll` `ce2655eea9ed0d31363bb3424eb683c12d583d9408ecbd7a2a082d2a4a97a595`; `Game.dll` `7459eea9478df895257c1470888bb2b96666d71c892eb9a50783f2a1a946bf9d`; `Gameloft.Engine.dll` `e38e7eeab9a22e89b5b5c67b1a7c8452205d5a224b22d69ae84405fc19cb4785`; `Gameloft.Util.dll` `8adb19442c9237edc57ef8ef62f7ea3a674282864ffb0e9e87b49c547cd82fe1`; `GameManager.dll` `3c97ab98cc74a4e9d88659facf8e9c994d298dee82515f7543a3b84929630284`; `Gameloft.OnlinePlatform.dll` `c93650f8fe8957f24c1ea5611e69197fe5ac75e61308577821f1e0a272e7c333`; `Gameloft.Logs.dll` `33314aa441830790efbad7803c73f8626d23adca6d56015645224d36533319e7` ([PP] §5.4) |
| exe characteristics | `0x0102` (no LARGE_ADDRESS_AWARE) |
| `ServerAllowedReferences` | `mscorlib`, `System`, `System.Core` (4.0.0.0, `b77a5c561934e089`), `Gameloft.Logs` (1.0.0.0, no token). Refused by name: `Mono.Android`, `Java.Interop`, `System.Runtime`, `netstandard`, `System.Windows.Forms` |
| `ServerAllowedModuleRefs` | `kernel32`, `user32`, `steam_api` |
| `GameOptionsFile` | `GameOptions_windows.json` |

Steam serves no newer build (only `public` branch, buildid 6372049, updated 2021-03-18, [CR] F1); the stale check stays
as a safety net.

`ServerAssemblyCheck` (both builds): name `McvsOffline.Server`; references within the allowed set with the profile's
corlib version and token; for PC, ModuleRefs within the allowed set; and the server's corlib scope equals the target
assemblies' corlib (name, version, token), which is what makes a second `mscorlib` impossible (P3, [PP] §2.1).

### 7.3 Resolver and the BCL directory (C12)

On a self-contained single-file kit, reading any PC assembly with `ReadingMode.Immediate` fails without a BCL directory
(custom-attribute enums need `mscorlib`); on framework-dependent Linux, Cecil silently resolves `mscorlib 4.0.0.0` to
the .NET 8 facade through the trusted platform assemblies, so a test that forgets the BCL still passes against the
wrong library ([PP-V] §9). Hence:

* `PcAssemblyResolver` implements `IAssemblyResolver` **directly** (not `BaseAssemblyResolver`, whose `Resolve` falls
  back after its search directories: to the trusted platform assemblies in Cecil's netstandard build, to the framework
  directory, `GetCorlib` and the GAC in its framework build, [PP-V] §9; the verification tool had to strip its default
  directories, `work-patcher-pc-verify/vtool/Program.cs`). It searches **only** the directories it is given, in this
  order: the BCL directory (for `mscorlib`, `System*`, `Microsoft.*`), the game folder, the server DLL's folder, for
  `<name>.dll` (then `.exe`); it reads each hit with the same `ReaderParameters` (its own resolver), caches by full name
  and disposes the cache with itself. No GAC, no TPA, no current directory, no `ResolveFailure` event, never a base
  call. A name found nowhere throws `AssemblyResolutionException` itself, which `PcInstaller` maps to exit code 3
  ("cannot patch: <name> not found in <dirs>").
* The BCL directory is mandatory: on Windows `%WINDIR%\Microsoft.NET\Framework\v4.0.30319` (the 32-bit framework the game
  runs on; must hold `mscorlib.dll`, `System.dll`, `System.Core.dll`), on Linux `MCVS_NET452_REFS`. `--bcl <dir>`
  overrides. Tests assert the resolved `mscorlib` file lies in that directory.
* `ReaderParameters { ReadingMode = Immediate, InMemory = true, AssemblyResolver = resolver }`, as the APK path.

### 7.4 Option A output and `ModernCombatVersus.exe.config`

Written files, in this order (a crash midway never leaves a config pointing at missing files, [PP] §7.2):

1. `McvsOffline\bin\EngineBridge.dll`, `Game.dll`, `Gameloft.Engine.dll`, `Gameloft.Util.dll` (patched copies),
   `McvsOffline.Server.dll` (the PC server DLL), each through `<name>.mcvs-tmp` + rename;
2. `McvsOffline\install.json` (through `McvsOffline\install.json.mcvs-tmp` + rename);
3. `ModernCombatVersus.exe.config` (through `ModernCombatVersus.exe.config.mcvs-tmp` in the game folder + rename).

A `*.mcvs-tmp` left by an interrupted run is a leftover: `status` lists it, the next `install` overwrites it and
`uninstall` deletes it (§7.6). No other temporary name exists in the game folder.

The config, exactly (UTF-8 without BOM, CRLF line ends, final CRLF; the five versions are read from the written files
with Cecil, and these are the values for build 6372049, [PP] §5.2, [PP-V] §7):

```xml
<?xml version="1.0" encoding="utf-8"?>
<!-- written by McvsOffline (PC install); 'uninstall' removes it; see McvsOffline/install.json -->
<configuration>
  <runtime>
    <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
      <dependentAssembly>
        <assemblyIdentity name="EngineBridge" culture="neutral" />
        <codeBase version="1.0.0.0" href="McvsOffline/bin/EngineBridge.dll" />
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="Game" culture="neutral" />
        <codeBase version="0.0.0.0" href="McvsOffline/bin/Game.dll" />
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="Gameloft.Engine" culture="neutral" />
        <codeBase version="0.0.0.0" href="McvsOffline/bin/Gameloft.Engine.dll" />
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="Gameloft.Util" culture="neutral" />
        <codeBase version="1.0.0.0" href="McvsOffline/bin/Gameloft.Util.dll" />
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="McvsOffline.Server" culture="neutral" />
        <codeBase version="0.0.0.0" href="McvsOffline/bin/McvsOffline.Server.dll" />
      </dependentAssembly>
    </assemblyBinding>
  </runtime>
</configuration>
```

Why: `<codeBase>` is consulted before probing and is the only way to skip a same-named file in the application base;
`<probing privatePath>` would always find the stock DLLs first ([PP] §0.6, §5.2; [PP-V] §8). The assemblies are not
strong-named, so the version is ignored but required by the schema, and the href must lie under the exe folder
(`PcExeConfig.Build` rejects absolute, rooted, `..` and backslash hrefs, as `tools/pc-spike/ExeConfig.cs:27`). Nothing
else: no `<startup>`, no `<probing>`, no `<loadFromRemoteSources>`. The spike proved the mechanism for `EngineBridge`
([CR] §1 row 1); the chained `Game`/`Gameloft.Engine`/`Gameloft.Util`/`McvsOffline.Server` binds are Load-context binds
(`PC-EN/ComponentAssemblies.cs:177`) and are WR2 Q1.

This is byte for byte the config WR2 ran (build 13, SHA-256 `3ebe99b3…f94c`, §13.2; `PcExeConfigTests.ExpectedSha256`).

WR2 answered Q1 (F1): `Gameloft.Engine`, `Gameloft.Util` and `McvsOffline.Server` came from `McvsOffline\bin` (each is bound
through a full static reference: EngineBridge → Gameloft.Engine → Gameloft.Util, the patched copies → the server), but
`Game` came from the game folder in 15 of 15 starts. The engine loads it by bare name
(`Assembly.Load(new AssemblyName("Game"))`, `PC-EN/ComponentAssemblies.cs:177`, from `Config/Assemblies.mjson`), and a
partial-name bind gets no application-config policy: it is probed in the application base only. The `gameload` IL patch
(§8.9) fixes it on its own: it loads `"Game, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"`, a full reference (the
token is given, as null), which gets the codeBase like the static references. Install still checks that every copy is
culture neutral and unsigned (as its `assemblyIdentity` says) and that the gameload table names Game's copy exactly.

**No `<qualifyAssembly>` (build 16).** Builds 14 and 15 added one `<qualifyAssembly partialName="X" fullName="X, Version=v,
Culture=neutral, PublicKeyToken=null" />` per assembly after the `<dependentAssembly>` entries (config `fda12aef…c4b6`).
On the owner's PC that kit did not start our server; copying the bin DLLs next to the exe made it work, which can only
happen when the config is not in effect (a codeBase that applies leaves the appbase copy unused, and a codeBase that
does not resolve fails the bind). The only published fusion source, SSCLI 2.0, explains it: for each `qualifyAssembly`,
`ProcessQualifyAssemblyTag` copies every field the partial name lacks from the full name, and a full name whose public
key token is the empty "null" one hits `_ASSERTE(!"Expect property!"); hr = E_UNEXPECTED` (nodefact.cpp:975-984);
`ParseXML` then throws the whole node factory away ("all traces of anything that did get parsed", policy.cpp:374-391), the
five codeBases included, so the stock `EngineBridge.dll` loads and none of our code runs. Microsoft's page asks for a
fullName "as it appears in the global assembly cache". **This is strong evidence, not proof:** the CLR 4 fusion in
clr.dll is not public; WR3 settles it (Q15). Build 16 writes the WR2 shape again; `status` and `verify` report a config
that still has a `<qualifyAssembly>` as DAMAGED ("an old build-14/15 config: run install again"), and install rewrites it
(it is ours). A `$type` that names one of our assemblies through Newtonsoft.Json's `LoadWithPartialName` stays a partial
bind (§17 R20).

`PcExeConfig.IsOurs(text)`: our comment present. `IsSpikeConfig(text)`: the spike's comment (`tools/pc-spike/
ExeConfig.cs` `OwnerComment`).

### 7.5 `McvsOffline\install.json`

```json
{
  "format": 1,
  "kind": "mcvs-pc-install",
  "installer": { "name": "McvsOffline.PcKit", "version": "0.1.0", "serverBuild": "13", "writtenUtc": "2026-11-02T18:00:00Z" },
  "build": { "id": "pc-steam-6372049", "gameVersion": "1.17.30", "clientId": "2620:70425:1.17.30:steam:steam",
             "exeSha256": "884f7461…1852" },
  "originals": {
    "EngineBridge.dll":    { "sha256": "ce2655ee…a595", "mvid": "b5913b9a-f9dc-42d6-bd3a-435a8280e8f1" },
    "Game.dll":            { "sha256": "…", "mvid": "…" },
    "Gameloft.Engine.dll": { "sha256": "…", "mvid": "…" },
    "Gameloft.Util.dll":   { "sha256": "…", "mvid": "…" }
  },
  "written": {
    "McvsOffline/bin/EngineBridge.dll":       { "sha256": "…", "mvid": "…", "version": "1.0.0.0", "patches": ["hook-pc"] },
    "McvsOffline/bin/Game.dll":               { "sha256": "…", "mvid": "…", "version": "0.0.0.0", "patches": ["headless", "bottile", "store", "signatures", "adminpage"] },
    "McvsOffline/bin/Gameloft.Engine.dll":    { "sha256": "…", "mvid": "…", "version": "0.0.0.0", "patches": ["headless-engine", "physics", "quicklaunch", "headless-diag", "nativeonline", "waits", "adminlink", "graphics"] },
    "McvsOffline/bin/Gameloft.Util.dll":      { "sha256": "…", "mvid": "…", "version": "1.0.0.0", "patches": ["geo"] },
    "McvsOffline/bin/McvsOffline.Server.dll": { "sha256": "…", "mvid": "…", "version": "0.0.0.0", "serverVersion": "0.1.0-dev", "gameLog": true },
    "ModernCombatVersus.exe.config":          { "sha256": "…" }
  },
  "options": { "port": 51780, "patches": ["…every applied id…"], "bcl": "C:\\Windows\\Microsoft.NET\\Framework\\v4.0.30319" },
  "notes": ["…PatchContext.Notes…"]
}
```

Keys in this order; paths relative to the game folder with `/`; MiniJson-compatible (the host reads it, §6.2). The keys
other readers may use are fixed in §3.10 (Day-0). `mvid` of a written assembly is its `DerivedMvid` (a fresh,
deterministic "this is our copy" marker, [PP] §2.1).

### 7.6 `PcInstaller` behaviour

**Install** (`PcInstallOptions`: game folder, server DLL bytes, BCL dir, port, optional ids, `force`, `dryRun`,
`Func<bool> gameRunning`, `IPcFileOps files` (write, rename, delete; the real one is `System.IO`, tests inject failures
and Windows semantics), log):
1. The folder holds `ModernCombatVersus.exe`, the four DLLs and `Data\MCVS.game`; the game is not running
   (`Process.GetProcessesByName("ModernCombatVersus")`) → else exit 4. A file matching `*_args.txt` in the game folder
   → exit 3, nothing written: "<name> is in the game folder; the game reads it instead of args.txt, so MCVS Offline would
   be ignored and the game would go online. Move it away and run install again" ([CR] F9; `--force` does not skip it).
2. Build identity: SHA-256 and MVID of the exe and the four DLLs (and the witnesses) equal the profile; otherwise exit 3,
   or a warning with `--force` (the site fingerprints still protect; Android parity). A changed game DLL whose SHA-256 is
   that of our copy (`install.json`'s record or the file in `McvsOffline\bin`) is a hand copy of bin, refused even with
   `--force`: "<files> are MCVS Offline's own copies from McvsOffline\bin, not the game's: you copied MCVS Offline's DLLs
   over the game's own files; Steam > Verify integrity of game files restores them, then run install".
3. Config: absent → fine; ours → replaced at the end; the spike's → noted, `McvsOffline\spike\` removed when it holds only
   `EngineBridge.dll`, replaced; anything else → refused (exit 3) with "a ModernCombatVersus.exe.config that is not ours
   exists; move it away and run install again" (never merged or silently replaced).
4. `ServerAssemblyCheck` on the server DLL.
5. `PatchPlan.For(PcSteam6372049, ids)` grouped by file; every group patched in memory with `AssemblyPatcher.Patch`
   **before** anything is written. Reference-set check of every output (P3). Any `PatchException` or resolution failure
   → exit 3, nothing written.
6. `dryRun` stops here and prints the report (with the SHA-256 each output would have). Else write (§7.4), then re-read
   every written file (SHA-256 as reported, Cecil MVID as recorded, the witnesses of §3.9 present for every applied
   patch that has one), and parse the config back (five `dependentAssembly`, hrefs exist and are the assembly they
   name, no `qualifyAssembly`). A write that fails before our config is in place (a first install, an upgrade from a
   build-14/15 config with `qualifyAssembly` entries, which most likely does not redirect, or the config write itself)
   adds "The game will start WITHOUT MCVS Offline until install succeeds"; the kit's menu then prints
   `INSTALL FAILED (exit code n): see the error above, fix it and run install again`.
7. `args.txt` is not written (the bootstrap writes it at the first start). A reinstall overwrites `bin\` files, then
   `install.json`, then the config (also a build-14/15 config with `qualifyAssembly` entries: it is ours).
8. The report ends with `exe <game>\ModernCombatVersus.exe`, `config <game>\ModernCombatVersus.exe.config (Windows reads
   it when that exe starts)` and `next start the game once, then run status: it must say activation: active`.

**Uninstall**: refuse while the game runs (exit 4); delete the config when ours (or the spike's), and stop there (exit 1,
nothing else touched) when it cannot be deleted, so it never redirects to deleted files; delete `bin\*` and
`bin\`, then `install.json`; strip the managed block from `args.txt` with `PcArgsBlock.Remove` (rewritten through
`args.txt.mcvs-tmp`), deleting the file when nothing but whitespace is left (the depot has no `args.txt`, F9); delete
every `*.mcvs-tmp` of §7.4; report anything left behind (a DLL that cannot be deleted: "quit the game and run uninstall again").
Before anything is deleted, a game DLL that is byte for byte our bin copy (a hand copy, as for install step 2) refuses
uninstall (exit 3, nothing removed: "<files> are MCVS Offline's own copies from McvsOffline\bin, not the game's: you
copied MCVS Offline's DLLs over the game's own files; Steam > Verify integrity of game files restores them, then run
uninstall again"): deleting bin and `install.json` would remove the only proof and leave the game running our copies
while status says "not installed". A `McvsOffline.Server.dll` next to the exe that is our copy (Verify keeps it) is
removed with the rest. Uninstall works from every state
(`installed`, `STALE`, `DAMAGED`, `PARTIAL`): it removes what of ours is there and skips what is missing. **The data
stays** (`settings.json`, accounts, profiles, logs, exports); `--remove-data` deletes all of `McvsOffline\` after the
confirmation `DELETE` (interactive) or `--yes`. Before that deletion the kit closes its log in `McvsOffline\kit\` and
continues it in `%TEMP%\McvsOffline\kit\pc-kit.log` (a file open for writing cannot be deleted on Windows, and the
logger must not recreate the folder); a file that still cannot be deleted is reported, never retried in a loop. An install
that was never started (nothing below `McvsOffline\` but the install's own files and the kit's `kit\`) loses the folder
without `--remove-data`; the kit moves its log to `%TEMP%` first in that case too.

**Status** (also the kit's): game folder and how it was found; build known/unknown per file; state `not installed` |
`installed, current` | `installed, OLDER BUILD` (the kit's status and verify only: `install.json`'s `serverBuild` is a
lower number than `McvsVersion.Build` of the server DLL the kit installs, its own or `--server`'s, `PcInstaller.ServerBuildOf`:
"OLDER BUILD (16; this kit installs build 17): run install to update it (your saves and settings stay)", plus a problem
line; the witnesses of patches the older build did not have are not in its record, so nothing else can tell, and a
build-16 install lacks `inbox` and sends the unlock codes to the inbox; it applies only where the state would be
`current`: DAMAGED, STALE, PARTIAL and NOT ACTIVE keep theirs and still get the problem line; verify fails on it with
exit 1; a build that is not a number on either side, or no `serverBuild`, is not compared; the patcher CLI does not
compare) | `installed, STALE` (originals differ from `install.json`: "Steam updated the game: run uninstall,
wait for a kit that knows this build"; when a changed DLL is byte for byte our bin copy, "you copied MCVS Offline's DLLs
over the game's own files; Steam > Verify integrity of game files restores them, then run install" and each such file
named) | `DAMAGED` (a written file missing or changed, or a config with `<qualifyAssembly>`: "an old build-14/15 config:
run install again", §7.4; a hand copy is checked here too and, when found, its files come first and the advice is the
Verify one above, `DAMAGED (<problems>): you copied …`, since install would refuse) | `PARTIAL` (e.g. bin without config); leftovers (also a `McvsOffline.Server.dll` next to the
exe that is a copy of ours): a foreign or spike config, an `args.txt` block while not installed, any `*.mcvs-tmp` (§7.4), any
`*_args.txt` (the engine reads `<prefix>_args.txt` instead of `args.txt`, `tools/pc-spike/ArgsTxtStep.cs:42-48`; also
when installed, as an error), the spike's data folder `%LOCALAPPDATA%\McvsOffline\spike`; listed apart, informational and
never a failure: "written by the game, not ours" (`dr0.bin`..`dr3.bin`, `OF_PP.bin`: the names the WP0 listing shows
with game-run times; `GameVersion` and `core.dmp` from the WR2 listings; `GameFolder.GameWrittenNames`), "the game's own
crash dump: core.dmp, <n> bytes, written <UTC time>" when there is one, and "other files, not ours" (any other top-level
entry that is not in the read-only install of `MCVS_PC_REFS` as recorded in `GameFolder.DepotTopLevel`, and not ours);
whether the game runs;
data folder size, settings `port` versus `install.json` `options.port`; the last `bootstrap.log` line.
**Activation** (WR2 F2: the files can be current while the game runs its own): the last start in `bootstrap.log` (from its
last "embedded host starting" line) is judged by its `[boot] assemblies:` and `patches:` lines (`PcBootActivation`). An
assembly not from `McvsOffline\bin` or a witness that says `NO` (of a patch `install.json` lists as applied, the rule of
the re-read below; `store NO` after `--patch none` is not a reason) makes a `current` install `installed, NOT ACTIVE (Game
loaded from the game folder; …)`, with an `ERROR:` line and the start's lines; a start older than `install.json`'s
`writtenUtc` is not judged; a start that did not load Game is "unknown". The `activation:` line says which. STALE,
DAMAGED and PARTIAL keep their state (the reason is still an error).
**No start since the install** (build 14: the game ran the stock files and nothing of ours wrote a line, so status said
`installed, current`). Both `bootstrap.log` files count, `<game>\McvsOffline\logs\` and the `%TEMP%\McvsOffline\logs\`
fallback (`GameFolder.BootstrapLogs`, the list `watch` follows); the newer is judged, and its newest timestamped line is
the last record of our host. With none at or after `writtenUtc`:
* hook-pc's marker `%TEMP%\McvsOffline\hook.txt` (§8.2) has a start since the install whose exe or `EngineBridge.dll`
  lies in this game folder (the file is shared by every copy of the game the user runs) → `installed, NOT ACTIVE (the
  server did not start)`, `activation: NOT ACTIVE: MCVS Offline's EngineBridge ran at <t> but the server did not start:
  <the exception line of the same pid, or "no exception recorded">`; while the game runs and that process recorded no
  exception, only `activation: NOT VERIFIED: MCVS Offline's EngineBridge ran at <t>; the game is still running: run status
  after closing it` (the start line precedes McvsOfflineInit and the host's first line, so it may still be starting);
* else a file the stock game writes in its folder (`GameFolder.GameWrittenNames`: `dr0.bin`..`dr3.bin`, `OF_PP.bin`,
  `GameVersion`, `core.dmp`) was written after the install and after our last record (whole seconds), and the game is
  not running now (a starting game may not have reached our first line yet) → `installed, NOT
  ACTIVE (the game started without MCVS Offline)`, `activation: NOT ACTIVE: the game started without MCVS Offline;
  Windows did not use <game>\ModernCombatVersus.exe.config`, a `hook:` line (no marker: "our EngineBridge has never run as
  this user"; an older one: when it last ran from this folder), the file and its time as a problem, and an `ERROR:` line.
  Without a config of ours (PARTIAL: the install stopped before the config write, or a foreign one) the reason is the
  config instead: `NOT ACTIVE: the game started without MCVS Offline: <config> is missing (install did not finish)` (or
  `is not ours`);
* else `activation: NOT VERIFIED: no start of MCVS Offline since the install (start the game once, then run status)`; the
  state stays `installed, current`.
The kit passes its own `%TEMP%` (`KitEnvironment.TempRoot`) to status and verify; the patcher CLI uses `Path.GetTempPath()`.

**Verify** = status plus the re-read of step 6, and it prints the `written` SHA-256 of every file (so WR2 compares them
with the Linux run without a file transfer, §13 step 3).

Exit codes (`patcher/Program.cs:12-49` plus one): 0 ok, 1 I/O, 2 usage, 3 unknown build / site mismatch / cannot
resolve (nothing written), 4 the game is running.

### 7.7 The patcher CLI on Linux

`McvsOffline.Patcher pc install|uninstall|status|verify --game <folder> [--server <pc dll>] [--bcl <dir>] [--port n]
[--patch default|<ids>] [--force] [--dry-run] [--remove-data --yes]`. `--bcl` defaults to `MCVS_NET452_REFS` on Linux
and the framework folder on Windows; no Steam lookup (the kit does that). The APK command line is unchanged (the `pc`
verb is new; `--apk` stays the default form).

`--port n` (patcher and kit) bakes the port into the `geo` patch, records it in `install.json` and sets `"port"` in
`McvsOffline\settings.json`, the key the game's server listens on (`EmbeddedHost`; the settings page never changes it):
only that key, through `settings.json.mcvs-tmp` + rename as the last write, the file created when missing. A
`settings.json` that is not one JSON object refuses the install before anything is written. Without `--port`
`settings.json` is left alone, and a valid (1..65535) `"port"` already in it is the port of the `geo` patch and
`install.json` (the server listens there: a reinstall after a Steam update keeps an earlier `--port`); without one, the
default 51780. `status` flags a `settings.json` port that differs from `install.json`'s (edited after the install). Every `.mcvs-tmp` → target rename retries a sharing/lock violation (and, on Windows, an
access denial: a virus scanner holding the file) up to 10 times 100 ms apart (`PcFileOps.Rename`).

---------------------------------------------------------------------------------------------------

## 8. The PC patch set (WP4)

### 8.1 Overview

| id | file | PC plan | status on PC | §, evidence |
|---|---|---|---|---|
| `hook-pc` | EngineBridge.dll | mandatory | new | §8.2; [PP] §4, [PP-V] §4 |
| `headless` | Game.dll | mandatory | per-build site table | §8.4; [HL] §6, [HL-V] |
| `headless-engine` | Gameloft.Engine.dll | mandatory | unchanged (identical fingerprints) | [HL] §2 H3/H5'/H6 |
| `physics` | Gameloft.Engine.dll | mandatory | unchanged; a no-op on PC (all physics single-threaded), kept for parity and the `[boot] physics:` line | [HL] §5 |
| `quicklaunch` | Gameloft.Engine.dll | mandatory | new: P1, P2, P3 | §8.3; [QL] §7.1, [CR] F5, F6 |
| `headless-diag` | Gameloft.Engine.dll | mandatory | new: M1, M4 | §8.5; [HL-V] M1, M4 |
| `graphics` | Gameloft.Engine.dll | **mandatory** on PC (F7) | PC variant | §8.6 |
| `gameload` | Gameloft.Engine.dll | **mandatory** | new (WR2 F1) | §8.9 |
| `nativeonline` | Gameloft.Engine.dll | default | new locator (PC shape) | §8.7 |
| `waits` | Gameloft.Engine.dll | default | new fingerprints | §8.7 |
| `adminlink` | Gameloft.Engine.dll | default | new fingerprint | §8.7 |
| `geo` | Gameloft.Util.dll | default | unchanged (shape) | [PP] §3 |
| `bottile`, `store`, `signatures` | Game.dll | default | unchanged | [PP] §3 |
| `inbox` | Game.dll | default | new (build 17): `GenerateRewardsObjects` and `GetObject` as Android's, PC fingerprints for `RefreshUI` and the `ClaimRewardsForMessage` state machine (anonymous-type numbers), and I5 (`KillSignatureRewardItem.IsRandom` gets Android's null check) on PC only | `patcher/Patches/InboxPatch.cs` |
| `adminpage` | Game.dll | default | new fingerprints, A1 offsets, `op_Equality` source | §8.7 |
| `forum` | Game.dll | optional | unchanged | [PP] §3 |
| `hook`, `finder`, `lan`, `debloat`, `targetsdk`, `game` | — | **excluded** | Android only | X; [PP] §3 |

Order inside a file (as `ApkPatcher.AllPatches`, `:66-71`, then the new ones): Game.dll `headless`, `bottile`, `store`,
`signatures`, `inbox`, `adminpage`, `forum`; Gameloft.Engine.dll `headless-engine`, `physics`, `quicklaunch`, `headless-diag`,
`nativeonline`, `waits`, `adminlink`, `graphics`, `gameload`. The PC list lives in `PatchPlan` only; `ApkPatcher.AllPatches` and its
test (`PatchTests.cs:277-303`) are not touched by the PC work.

Per-build selection inside a patch class: fingerprints stay constants next to the patch (`…Fingerprint` for Android,
`Pc…Fingerprint` for PC) and are picked with `ForBuild(ctx, android, pc)`; a null value refuses. Android sites and
outputs stay identical: with a fixed stand-in server the patched Android assemblies are byte-identical to the
`main-dev` 7f24f82 ones (P5; `AndroidOutputUnchangedTests`, §12.4).

Every inserted `if (World.Headless) …` below is the existing pattern (`ldarg.0; call Component::get_World; callvirt
World::get_Headless; brtrue <target>`, `get_Headless` through `ImportEngineMethod`, `HeadlessGamePatch.cs:242`). Where the
insertion point is a branch target, every branch that targeted it is retargeted to the first inserted instruction (Cecil's
`InsertBefore` keeps the old target otherwise).

### 8.2 `hook-pc` (`EngineBridgeHookPatch`, replaces `hook`)

Checks before any change ([PP] §4.2): type `EngineBridge.Class1`; `public instance void Start()` with fingerprint
`52063de24e52f57559dfaeb3485615933fe07e89665b7e7e4346d88a90857ac9` (`ldc.i4.0; ldnull; ldc.i4.0; call int32
MainLoop::Start(ModeType, string[], bool); pop; ret`); `.ctor` with fingerprint
`d5269750068c46d568d90f6889356b64b3ec93374d0b0175199fad928189ef8c` (the prototype did not check it, [PP-V] §4); no
`McvsOfflineInit` yet; corlib scope `mscorlib` major 4 with token `b77a5c561934e089` (as `EngineBridgeMarkerPatch.cs:83-85`);
then `ImportServerMethod(module, ctx, "McvsOffline.Bootstrap", "Init", "System.Void")`.

Result (build 16; the patched `Start` is checked against `PcPatchedStartFingerprint`
`136b34177fbd13488b65aec7855308c372ed52db218155737e848bea8a7d75dc` after the edit):

```
Start():  ldnull; call Class1::McvsOfflineHookTrace(object)                      // the marker line
          try { call Class1::McvsOfflineInit() } catch (object) { call Class1::McvsOfflineHookTrace(object) }   // the exception
          ldc.i4.0; ldnull; ldc.i4.0; call MainLoop::Start(...); pop; ret       // original body unchanged (from IL_001a)
McvsOfflineInit() [private static, NoInlining]: call [McvsOffline.Server]McvsOffline.Bootstrap::Init(); ret
McvsOfflineHookTrace(object) [private static, NoInlining]: try { McvsOfflineHookWrite(e) } catch (object) { }
McvsOfflineHookWrite(object) [private static, NoInlining]: one line appended to %TEMP%\McvsOffline\hook.txt
McvsOfflineGetCurrentProcessId() [private static, PInvokeImpl kernel32.dll!GetCurrentProcessId]: uint
```

The `[NoInlining]` helper keeps a missing or unloadable `McvsOffline.Server.dll` inside the try (the bind fails when the
helper is JIT-compiled). The MVID comes from `AssemblyPatcher` (`DerivedMvid`), never the original's.

**The marker** (build 16, after the build-14 report): `<UTC yyyy-MM-ddTHH:mm:ss.fffZ> hook-pc start pid <pid> exe
<argv[0]> dll <EngineBridge.dll's Location>` before `McvsOfflineInit`, and `<UTC> hook-pc McvsOfflineInit failed (pid
<pid>): <exception.ToString()>` from the catch, each appended to `%TEMP%\McvsOffline\hook.txt` (the folder of the host's
fallback logs). It calls mscorlib only (`Path`, `Directory`, `File.AppendAllText`, `DateTime`, `CultureInfo`,
`Environment.GetCommandLineArgs`, `Assembly.GetExecutingAssembly().Location`, `String.Concat`), plus the kernel32 import
for the pid (its own try: `?` where it fails, as on Linux in the tests), so it is written even when the server DLL
cannot be loaded. The guard catches everything, a member the JIT cannot bind in the writer included (that call is inside
its try), so it can never stop the game. It tells three cases apart: no line since the install (the stock EngineBridge
ran: the config was not used), a start line and a failure (the server did not start), a start line and a host log (ours
ran; `bootstrap.log` says the rest). Status reads it (§7.6). Tests: `RealPcPatchTests.TheInsertedIlIsExact` (the IL of
`Start`, the guard and the writer; mscorlib calls only; one module reference), `RealPcHookTests` (executed with a
recording, a throwing and a missing server: the lines and their order), ILVerify and `RealPcJitTests`.

### 8.3 `quicklaunch` (`QuickLaunchPatch`: P1, P2, P3)

Why: the PC engine lost the client plumbing that routes an Embedded link to LocalAnubis: `Prepare` never starts it,
`ClientLink.QuickLaunch` has no Finder branch, `BaseServer.Port` returns 0; the client's quick_launch went to our
Federation matchmaker (404 by design) and the error popup came up ([QL] §2 D1-D4, [QL-V] §1-§3; no args.txt change can fix
it, [QL-V] §10). The three sites, all in Gameloft.Engine.dll ([CR] F5):

| site | method | PC fingerprint | shape |
|---|---|---|---|
| P1 | `Gameloft.Engine.LocalAnubis.BaseServer::get_Port()` | `20ed09f81f4535a206c3139d229bad3202bf726b4c4e769190598ea519553f85` | `ldc.i4.0; ret` |
| P2 | `Gameloft.Engine.ClientComponent::QuickLaunch(ClientLink, CancellationToken)` | `cd9c1ab7b3985ad4a508d0c72fa0789f71ca9ef3aaba94b3a9a1ff5bba85a1e2` (identical on Android) | 22 instructions, 2 locals: the `<QuickLaunch>d__124` kick-off, ending `call AsyncTaskMethodBuilder::get_Task; ret` at `IL_0040`; not overridden in Game |
| P3 | `Gameloft.Engine.MatchmakingComponent::AddAutomatchHostSettings(AutomatchSettings, Session, JObject, CancellationToken)` | `47da5614b4727db6e26447e562bbf5d0b113fa493d2f547707f81b217030df65` (identical on Android) | 26 instructions, 3 locals; reads `host`/`port` from the request and sets them only when both are present; ends `ldc.i4.1; call Task::FromResult<bool>; ret` |

**P1.** Replace the body with Android's, verbatim ([CR] F5): `ldarg.0; call get_Listener; dup; brtrue.s L1; pop; ldnull;
br.s L2; L1: call TcpListener::get_LocalEndpoint; L2: castclass IPEndPoint; dup; brtrue.s L3; pop; ldc.i4.0; ret; L3:
call IPEndPoint::get_Port; ret` (16 instructions; WP4 copies the exact opcodes, `call` or `callvirt`, from the Android
dump in `MCVS_REFS`). `get_Listener` is required on the type (`TcpListener` return); the `System` members come from
`SystemMethod` (resolved through the BCL directory). Self-check after the edit: the method's fingerprint equals
Android's `c180c05cbf2de69be2b562e5e296a47e2f1213e32f42ea62421f57f34948da80`, else `PatchException`. Without P1 every
room advertises port 0 and both the client and the GS World dial port 0 ([QL-V] §3).

**P2.** Body becomes `ldarg.0; ldarg.1; ldarg.2; call Task Gameloft.Engine.McvsQuickLaunch::QuickLaunch(ClientComponent,
ClientLink, CancellationToken); ret` (no locals; the state-machine type stays, unused). Helper type
`Gameloft.Engine.McvsQuickLaunch` (internal, abstract sealed; its presence means "already patched"):

```
static Task QuickLaunch(ClientComponent c, ClientLink l, CancellationToken ct)
    try   { ldarg.0; ldarg.1; ldarg.2; call CallQuickLaunch; stloc.0; leave END }
    catch object { pop; ldarg.1; ldarg.2; callvirt ClientLink::QuickLaunch(CancellationToken); stloc.0; leave END }   // stock path
    END: ldloc.0; ret
[NoInlining] static Task CallQuickLaunch(ClientComponent c, ClientLink l, CancellationToken ct)
    ldarg.0; ldarg.1; ldarg.2; call [McvsOffline.Server]McvsOffline.PcHooks::QuickLaunch(object, object, CancellationToken); ret
```

(the stock body is `await link.QuickLaunch(ct)`, `PC-EN/ClientComponent.cs:790-793`; `ClientLink.QuickLaunch(CancellationToken)`
is public, `PC-EN/ClientLink.cs:391-396`).

**P3.** Prologue before `IL_0000` (not a branch target): `ldarg.1; call void Gameloft.Engine.McvsQuickLaunch::
DefaultAutomatchHost(MatchmakingComponent/AutomatchSettings)`; helper `try { CallDefault(s) } catch object { pop }`,
`[NoInlining] CallDefault` → `PcHooks::DefaultAutomatchHost(object)`. The stock body still overwrites `Host`/`Port` when the
request carries both.

**`PcHooks.QuickLaunch` (WP2)**, no `async`/`await` (repo rule): it composes with `ContinueWith` on the game thread's
`SynchronizationContext`, so every continuation, and with it LocalAnubis's own awaits, runs on the game thread as on
Android ([CR] F6; [I], WR2 Q13). Types are found from the arguments' assemblies, never by `Type.GetType(name)` (the
Gameloft.Engine assembly = the one defining `Gameloft.Engine.ClientLink` up `link.GetType()`'s base chain; the
OnlinePlatform assembly = the session's), so the tests can run it against look-alikes in a collectible
`AssemblyLoadContext`. Members used, all **[V]** in `PC-EN/` ([CR] F6): `ClientLink.Settings` (`ClientLink.cs:49`) →
`ClientSettings.ServerLocation` (`:27`), `ServerType` (`:43`); `ClientLink.Room` (get/set, `:53`);
`ClientComponent.Authentication` → `AuthenticationComponent.Session` (`ClientLink.Authentication` is private, `:194`);
`Session.GetService<T>()` (generic, `Session.cs:373`) with `MatchmakerService`; `MatchmakerService.QuickLaunch(string,
CancellationToken, string host, int? port)` → `Task<Room>` (`MatchmakerService.cs:13`); static
`LocalAnubisManager.Started`, `Start(CancellationToken)`, `Finder` (`LocalAnubisManager.cs:58-73, 113-131`);
`Finder.PublicHost` (`"localhost"` for a loopback host) and `Finder.PublicPort` (51750).

```
QuickLaunch(component, link, ct):
  try
    location = link.Settings.ServerLocation.ToString(); serverType = link.Settings.ServerType
    if location != "Embedded":                         log once  "[pc] quicklaunch: ServerLocation <x>: the game's own path"
        return (Task) link.QuickLaunch(ct)
    scheduler = SynchronizationContext.Current != null ? TaskScheduler.FromCurrentSynchronizationContext()
                                                       : TaskScheduler.Current       (WARN once "no synchronization context")
    tcs = new TaskCompletionSource<object>()
    start = LocalAnubisManager.Started ? completed task : (Task) LocalAnubisManager.Start(ct)
    start.ContinueWith(Step2, CancellationToken.None, TaskContinuationOptions.None, scheduler)
    return tcs.Task
  catch (Exception ex)                                 WARN "[pc] quicklaunch: cannot start: <ex>" ; return faulted(ex unwrapped)
Step2(start):
  faulted  → WARN "[pc] quicklaunch: LocalAnubis did not start: <inner>" (+ for HttpListenerException:
             "http.sys refused http://localhost:51750/ (WR2 Q2, PLAYER-SETUP-PC troubleshooting)"); tcs.TrySetException(inners)
  canceled → tcs.TrySetCanceled()
  else     → INFO once "[pc] quicklaunch: LocalAnubis started (Finder http://localhost:51750/, lobby port <n>, controller port <m>)"
             q = (Task) MatchmakerService.QuickLaunch(serverType, ct, Finder.PublicHost, (int?) Finder.PublicPort)
             q.ContinueWith(Step3, …, scheduler)
  try/catch around all of it: WARN "[pc] quicklaunch: …"; tcs.TrySetException(unwrapped)
Step3(q):
  faulted  → WARN "[pc] quicklaunch: the local Finder answered no room: <inner>"; TrySetException(inners)
  canceled or ct.IsCancellationRequested → TrySetCanceled()
  else     → link.Room = q.Result; INFO "[pc] quicklaunch: room <id> (controller <host>:<port>, thread <tid>)"; TrySetResult(null)
  try/catch: WARN; TrySetException
```

Exceptions keep their types (`TrySetException(inner exceptions)`; a `TargetInvocationException` is unwrapped), so
`ClientComponent.Connect`'s `ShouldCatchException` and the error popup behave as for a stock failure. A failed start
leaves LocalAnubis stopped (`Start` calls `Stop()` and rethrows, `:119-130`), so "Confirm" on the popup tries again.
Every `catch` writes a log line (the log is rate-limited per message: first 5, then every 50th).

**`PcHooks.DefaultAutomatchHost(settings)`**: when `LocalAnubisManager.Started` (only after our start) and the settings
have neither `Host` (null or empty) nor `Port`, set `Host = Finder.PublicHost`, `Port = (int?) Finder.PublicPort`; INFO the
first 3 times `"[pc] quicklaunch: automatch through the local Finder localhost:51750"`. Settings seeded from
`MatchmakingComponent.Host/Port` are kept ([QL-V] §8). Any exception: WARN, return.

### 8.4 `headless` on PC (Game.dll)

`HeadlessGamePatch.Apply` is split into site methods selected by `ctx.Build`; Android runs exactly today's sites. PC:

| site | method | PC fingerprint | edit | why |
|---|---|---|---|---|
| H1 | `Game.InputSettingsComponent::OnStart()` | `267c5ba0f22da50d67f0566be5418fea526a287ea5e3ebef25fa79529b6197b2` | type gets `[KeepInHeadlessMode(false)]` (as Android). PC shape: `os[0] ldarg.0; os[1] call Component::OnStart; os[2] ldarg.0; os[3] call Component::get_World; os[4] call UISceneExtentions::GetUI(World); os[5] ldstr "ChangeKeyAction"` | NRE at `IL_0024` (`GetUI()` is null in headless Worlds) at the TS's first level load ([HL] §2, [HL-V] §2) |
| H2 | `Game.GameplayComponent` HudGamepad calls | — | none; the check expects **0** `OfType<HudGamepadComponent>().FirstOrDefault()` sites (Android 4) | the PC replacement (key listeners) is safe ([HL] §2) |
| H5 | `Game.SoundManagerGameComponent::OnStart/OnUpdate/OnStop` | `7df996c2…858c` / `ef8927e6…21fc` / `11f42f04…20ac` (identical) | unchanged | |
| H7 | `FlameZoneComponent::BroadcastEnd`, `BacktrackTimeDistortionExplosion::FadeOutEffect` | `1bbd54ea…9d99` (path literal differs; structural check passes) / `278ecc08…980e` (identical) | unchanged | |
| H8 | `Game.GamePreferencesComponent::LoadPreferencesWindows()` | `5740df958d71ef2f7df4182ff61a491f64d24f2e9414a2687269212f2989d360` | before `IL_0025` (`ldarg.0; stsfld instance`; not a branch target): `if (World.Headless) goto IL_002b` | headless Worlds took over the static `instance` used by the native fullscreen and overlay callbacks (`OnFSChanged` `749242c0…`, `WhenOverlay` `14c003ca…`) ([HL] §3.1, [HL-V] §4) |
| H9 | `Game.SteamAchievementsComponent::OnStart()` | `58d802a47305cdce4b612c3ec5c9e0c5299c8957d8da932969197f3c4b32daae` | before `IL_0038` (block B; a branch target of `IL_0011` and `IL_0019`): `if (World.Headless) goto IL_00c5 (ret)`; retarget both branches to the inserted first instruction | server Worlds subscribed the static and C# events a second time: `AddProgressionValue` counted twice (crates, first login of the day), inflated values reached Steam ([HL-V] §6, C6, D3) |
| M3 | `Game.SteamAchievementsComponent::OnStop()` | `dfddfc3d3cdbf0718fdd5b22420c1d09980c0933b3e0f16aa91861d7357c1b11` | check: `IL_0000 ldarg.0; IL_0001 call BaseAchievementsComponent::OnStart()` is the only such call and nothing branches to `IL_0000`. Prologue: `ldarg.0; call Component::get_World; callvirt World::get_Headless; brfalse IL_0000; ldarg.0; call <nearest OnStop() of the base chain>; br IL_0006` (`IL_0006` = the instruction after the stock call). Headless (server) Worlds call the base `OnStop`; the client World keeps the stock `base.OnStart()` exactly | stock bug: every stop re-hooked the handlers in server Worlds ([HL-V] §6, M3). Server Worlds only, because the change is **not** harmless for the client World, contrary to [HL-V] §6: `BaseAchievementsComponent.OnStop` (`PC-GM/BaseAchievementsComponent.cs:78-83`) sets `mercsComponent` and `gameplayComponent` to null **before** its `IsServer()` return, while the client's block-B handlers stay subscribed (`isEventEnabled`, `SteamAchievementsComponent.cs:17, 37-46`) and the base class uses both fields unguarded (`BaseAchievementsComponent.cs:158` `mercsComponent.GetBuiltMerc`, `:1070-1088` `gameplayComponent.GameMode`): an event between a client Stop and the next Start would throw |
| H10 | `Game.GameClientComponent::OnCreate()` | `fbcb1d22d85d1cbebd6fb653f0a733e0d036d539b5ca6d5334800ede32be0198` | before `IL_0079` (`ldarg.0` that starts the PC-only tail; not a branch target; check `IL_0079..` = `ldarg.0; call get_World; call GetGameInputManager; ldstr "Config\Presets.txt"`): `if (World.Headless) goto IL_0107 (ret)` | NRE at `IL_009f` (`World.LocalStorage` is null) in every TS/GS World at construction; without H3 a silent connect timeout ([HL] §3.1, [HL-V] §3) |
| H11 | `Game.GameplayComponent::OnAppFocusLost()` | `7dc375ccfa644c88da2ab2b679c251ddb16a12b3ad0a7987460e18bfdeeb565f` | prologue (no branch into `IL_0000`): `if (World.Headless) goto IL_001c (ret)` | PC-only `AppMinimized` subscription: a minimise made the GS World tap the client's pause button too ([HL] §3.1, [HL-V] §7) |
| M2 | `Game.GameplayComponent::OnUpdate()` | `3076ed790ccb3026b3053aa350c587d32b990b2c45c51aaeb3d29786c1e10444` | before `IL_0057` (the cursor block; a branch target of `IL_0025` and `IL_0044`): `if (World.Headless) goto IL_0109`; retarget both branches. The block holds the three `InputManager::ShowCursor(bool)` calls at `IL_0097`, `IL_00fc`, `IL_0104` (check all three exist and `IL_0109` is `ldarg.0; ldfld m_isShowRoundResult`) | the process-wide cursor was driven by every World each frame; the GS World could hide it in the client's menu ([HL-V] M2) |
| H13 | `Game.TutorialComponent::OnStart()` | `7ff0b308b3b478cb06385c18afe49e8fb521bbab9e3525ca9f0dead5768fc499` | after `IL_001f` (`call GetUI`): `dup; brtrue IL_0024; pop; br IL_0033` | only a Tutorial match in a headless World reaches it; our args make `ShouldPlayTutorial` false ([HL] §3.3). **Optional**: WP4 confirms the offsets from a fresh dump ([HL] did not record the body) and may drop it; nothing depends on it |

Unchanged and identical on PC (in `headless-engine`, `physics`): H3 `Component/Events::GetSafeAction`
`456bc30eb7fc2f9c004d334d487f93b4283a730fedae23c0bdaff7941bf0c2fd`; H5' `SoundGroupSnapshotComponent` attribute; H6
`GameInputManagerComponent::OnStart/OnUpdate` `1e5551a6…a668` / `8b191aab…74e2`; physics `.ctor` `3e87166b…5468`,
`UpdateSimulationSteps` `0e91c200…5afa`. Safe PC-only sites that need no edit: [HL] §3.2 (confirmed by [HL-V] §8).

Offsets H8, H10, H11 and M2 are verified ([HL-V] §3, §4, §7, `gp-onupdate-pc.txt`); H9 and M3 in the dump
`work-headless-pc/fp-dump.txt`. Every site checks its fingerprint first.

### 8.5 `headless-diag` (`HeadlessDiagnosticsPatch`, Gameloft.Engine.dll, PC only)

Two diagnostics so a failure on the Windows engine is not silent (WR2 Q4):

| site | method | PC fingerprint | edit |
|---|---|---|---|
| M1 | `Gameloft.Engine.ServerManager/<_AddClientAsync>d__20::MoveNext()` | `5b7fcb15925910abade2da0df9f30e223147118d2844e97fc47c7b1db191abc0` (identical on Android) | the inner `catch [mscorlib]System.Exception` handler starts `IL_0319: pop; IL_031a: leave.s IL_031c`; change `IL_0319` in place (the handler start keeps pointing at it) to `call void Gameloft.Engine.McvsWorldDiagnostics::ServerWorldFailed(System.Exception)`. The `OperationCanceledException` handler (`IL_0316`) is untouched |
| M4 | `Gameloft.Engine.ComponentAssemblies::CreateInstance(GameObject, Type, bool, object[])` | `e372ca4d0596bec209721344317fcb2244ab2e2d20887515875f39e6af652cd9` | the PC-only `catch Exception` (`IL_0061: pop; IL_0062: leave.s IL_0075`): `IL_0061` in place becomes `ldarg.1`, followed by an inserted `call void Gameloft.Engine.McvsWorldDiagnostics::ComponentCreateFailed(System.Exception, System.Type)` |

`Gameloft.Engine.McvsWorldDiagnostics` (its presence means "already patched"): each method is `try { [NoInlining]
forwarder → PcHooks.<same name> } catch object { pop }; ret`. Why M1: `_AddClientAsync` swallows everything, so an
exception at TS/GS World construction (H10 without H3, a native refusal of a second World) shows only as a client
timeout plus a leaked native World ([HL-V] §3, M1; B7 in research 06). Why M4: PC swallows constructor **and**
`InitTemplate` exceptions, leaving a missing or half-initialised component ([HL-V] §9, M4). The hooks log
`server world: ServerManager._AddClientAsync failed: <ex>` (first 10, then counts) and
`component <type> was not created: <ex>` (first 20 distinct types), and mark the boot trace (the shape of the H3
`headless world:` line: `[boot] …` and `[pc] …` in bootstrap.log, `[headless]` only on the SelfLog fallback).

### 8.6 `graphics` on PC

Same `OnCreate` sites, fingerprint `ca2930ef38ded244fe404f825ac75f7ad39e12b4210980ec790eb23423694e19` (identical), same hook
`GraphicsHooks.GameOptionsJson(string)` (the text is `GameOptions_windows.json`, `PC-EN/GameOptionPerformanceProfileComponent.cs:12`).
PC has no `m_options` field ([PP] §3.4), so `McvsGraphics.OptionsApplied(component)` forwards the component itself
(`(object)component`) to `GraphicsHooks.GameOptionsApplied(object)`; the host reads the options as in §6.7. The
description texts take `BuildProfile.GameOptionsFile`. The MEM rule and profiles are host logic (§6.7).

### 8.7 The re-fingerprinted Engine/Game patches

| patch | PC facts | change |
|---|---|---|
| `nativeonline` | `OnlinePlatformComponent.Init_private`, fingerprint `645e6647fff83225925e682c2fded343cdc306b8cae7c45037c6257fc6ba6ac0`, 23 instructions: per argument `ldarg.N; brfalse.s; call Encoding::get_UTF8; ldarg.N; callvirt GetBytes(string); br.s; ldnull`, then `call OnlinePlatformComponent_Init_private(byte[], byte[], byte[])`, `ret` ([PP] §3.2, [PP-V] §6) | a PC locator: fingerprint, exactly one `call` whose target resolves to a `PInvokeImpl` named `OnlinePlatformComponent_Init_private`, last instruction `ret`; the edit (prefix guard calling `AllowNativeOnlineInit(datacenter)`) branches to the **last** instruction, not `code[4]`. WP4 checks which parameter is the datacenter in `PC-EN/OnlinePlatformComponent.cs:260-263` |
| `waits` | `WaitAndWork(Task, …)` `1b4aefd54d00ab434106c87b287e97a2b2487c048d31fdbd28ccd4d22ceb558d`; `WaitAndWork(ManualResetEventSlim, …)` `563529838ddb335c54b9db3c4d22be789b1ef1b49e546ee851dc0063d1ffbbcc`; only `dup` → `ldloc.0`/`ldloc.2` differ, offsets unchanged ([PP-V] §6) | fingerprints only; `CorlibMethod(WaitHandle.WaitAny(WaitHandle[], int))` resolves through the BCL directory |
| `adminlink` | `BrowserComponent.OpenUrl` `7ff1c8c2e34c2619e262ce8c4c1ffced5134409db7106559d8eb5e4ed032ea29`, 86 instructions, starts `ldarg.0; call Component::get_World` | fingerprint only. On PC `OpenUrl` opens the Steam overlay after `InitInGameBrowser` ([WH-V] C6); the marker is intercepted first, as on Android |
| `adminpage` | A1 `GetAccountMenuButtonList` `3af8853ab073dc76e1547fd5386e1ace5174c49d8c1f36b830f4e3ea7a9c8a4a`, single `ret` at `IL_0115`, `ldloc.0` at `IL_0114` (Android `0x150`/`0x14f`); A2 `ProcessAccountMenuButtonAction` `e1bffa0fe6ac281eb4ba35e3491c67da91d27da6c473e7e08d6f9033096692f3`, a 6-instruction stub with no `string.op_Equality` to borrow ([PP] §3.2, [PP-V] §6) | per-build fingerprints and A1 offsets; on PC `op_Equality` comes from `CorlibMethod(module, String, "op_Equality", …)`; Android keeps borrowing it (P5). Whether the PC Flash menu shows and routes the button is WR2 Q10 |

### 8.8 Excluded patches refuse cleanly

`PatchPlan.For(PcSteam6372049, …)` refuses `hook`, `finder`, `lan`, `debloat`, `targetsdk`, `game` (`PatchPlanTests`,
§12.4); and every patch class asked to apply on a build it has no table for throws `PatchException` before changing
anything (the cross-build refusals of §12.4, per patch).

### 8.9 `gameload` (`GameAssemblyLoadPatch`, Gameloft.Engine.dll, PC only; WR2 F1)

`ComponentAssemblies.GetAssembly(string fullPath, string name)`, fingerprint
`b915f891ed04967504e8f835388ebdf926ff5b2ecdc97717b754dc5a4a14ca91` (45 instructions, one `catch (object)` around
`IL_0027: ldarg.1; IL_0028: newobj AssemblyName(string); IL_002d: call Assembly::Load(AssemblyName)`). One inserted
`call string Gameloft.Engine.McvsGameLoad::Qualify(string)` after `IL_0027`, inside the try: the bare name becomes the full
display name for the names of `GameAssemblyLoadPatch.QualifiedNames`, everything else passes unchanged (the cache key and
the `Loading`/`Loaded` events keep the bare name). `Assemblies.mjson` (PC data `460951647.f`) lists `Game` (gameDlls) and
`NicePlaySDK` (managedDlls, absent on PC); of the redirected assemblies only `Game` is loaded this way, so the table is
`Game → "Game, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"`. Before any change the patch resolves the game
folder's `Game.dll` and refuses unless its identity is exactly that (the patched copy keeps it; install cross-checks the
table against the written copy). Since build 16 this patch alone is meant to carry the fix (the config has no
`<qualifyAssembly>`, §7.4): a display name that gives the token, even as `null`, should be a full reference for fusion, so
the codeBase applies as for the static references. Expected, not proven: the reasoning is the SSCLI `IsPartial` reading,
and a managed `AssemblyName` reaches fusion through the VM's `AssemblySpec` conversion, which SSCLI does not show in full;
`gameload` has never run with a config in effect (WR2 was build 13 without it, build 14's config was most likely
ignored). WR3 step 4 confirms (`Game …\McvsOffline\bin\Game.dll`) or refutes it. `McvsGameLoad` has no server dependency: `Qualify` is
`if (name == "Game") return "<full name>"; return name;` (`string.op_Equality` from the PC corlib). Its presence is the
witness (§3.9), reported in `[boot] patches: … gameload yes …`.

**Build 18: our own host loaded Game first, so `gameload` was never tested.** Build 17's owner log (config in effect: every
other assembly from `McvsOffline\bin`, `gameload yes`) still had `assemblies: Game …\Game.dll (GAME FOLDER)` at +0.187 s,
after `install: current` (+0.140) and before `offline server listening` (+0.250), i.e. on the host's start thread
(`mcvs-embedded-start`, `EmbeddedHost.RunServer`) between those two marks; WR2's log has the same order (`install: current`
+0.641, `Game` +0.672, `listening` +0.813). That thread runs beside the game thread, so the timing alone cannot tell our load
from an engine `GetAssembly` that also missed the codeBase. The load was almost certainly ours (code reading; the timing in
both logs fits; build 18's `[boot] load stack:` WARN is there to confirm it): `CreateAdminHost` called `GameReflection.Load()`, which bound `Type.GetType("Game.PlayerProfileComponent, Game", false)` (and
`Game.UISceneExtentions, Game`). That is a partial name, which gets no application-config policy, so fusion probed the
application base and loaded the stock `Game.dll` next to the exe (the witnesses, read from that first `Game`, said
`store NO, inbox NO`). What the engine's later full-name load got is not in the log (the probe reports the first load of
each name only): most likely the `Game` already in the Load context, which is what the game ran. The same call ran in
every WR2 start (build 13 had it too), so WR2's F1 (15/15 starts) was most likely ours as well; the engine's bare-name load
would have reached the same file there. `gameload` has therefore never run first: whether its full name gets the codeBase
is still open (Q1, Q15). The fix: `GameReflection.Load` binds lazily from the assemblies already loaded
(`LoadedTypes.Find`: `AppDomain.GetAssemblies()` by simple name, never a load) and binds what is missing on the next use
(the page, the notification prompt, the exit post), so it is unbound at boot on PC and bound once the engine has loaded
`Game`; on Android `Game` is loaded from the APK before the bootstrap, so the effect there is unchanged. The other
by-name lookups of the host (watchdog, threading line, graphics, IAP status, `WindowsExit`'s `MenuManager`) take
`LoadedTypes.EngineType`: the loaded `Gameloft.Engine` (on PC always loaded before the bootstrap: `EngineBridge.Class1.Start`
references it statically, so the JIT bound it through the codeBase), and only on Android, when it is not loaded yet, the
old partial `Type.GetType` (no redirect there). The patcher's injected helpers reference our assemblies statically (full
`AssemblyRef`s) and `McvsGameLoad.Qualify` only compares strings; `server/` names no game assembly. A source scan
(`GameLoadOrderTests.NoPartialNameOfARedirectedAssemblyComesBack`) fails on a `"…, Game"`-style literal of a redirected
assembly, a bare-name `Assembly.Load`/`LoadWithPartialName`, or any `Type.GetType` in `hosts/` or `server/` outside
`LoadedTypes` and `RuntimeInfo`; `PcEmbeddedAssemblyTests.OnlyRuntimeInfoLoadsByName` checks the built PC DLL's IL the same
way (no call to `Type.GetType(string…)`, `Assembly.Load*`, `AppDomain.Load`/`CreateInstance*`, `Activator.CreateInstance(string…)`
or `new AssemblyName(string)` outside `RuntimeInfo`, and none in `LoadedTypes.EngineType`, whose partial-name fallback is
Android only). If `Game` still comes from the game folder, the `[boot] load stack:` WARN (§3.8) names who loaded it.

A second signature is possible, without a stack: `assemblies: Game (no file) (elsewhere)`. The stock `Gameloft.Sequences`
(`AssemblyManager.Initialize`, from `GetAllAssemblies`) lists the `*.dll` of its own folder, the game folder, and for
`Gameloft.*` names and the names `AssemblyManager.IsExternalAssembly` accepts (`GameApplication.InitGameAssemblies` sets it
to the `ComponentAssemblies.GameAssembliesInfo` names, so `Game.dll` too) takes the loaded assembly of that name, else
`Assembly.Load(File.ReadAllBytes(...))`. While `gameload` works that is harmless: `InitTemplates` loads `Game` right after
`InitGameAssemblies` on the same thread, so `AssemblyManager` finds it loaded. When the engine's full-name bind fails
(`GetAssembly` swallows the exception and caches null), the stock `Game` is loaded from bytes, with no `Location`: that is
the `(no file) (elsewhere)` line, and it means the `gameload` bind itself failed (the open part of Q1/Q15). The load stack
is kept to the `GAME FOLDER` case (a file load whose loader we want to name); this one names its loader by its shape.

Tests: `GameLoadOrderTests` (GameReflection.cs and LoadedTypes.cs as shipped, in a load context that serves a look-alike
`Game` by partial name and records every request: no request and no load of `Game` before the engine loads it, whatever
member is used; bound once it is loaded; the control shows build 17's call is seen), `GamePatchProbeWatchTests` (the load
stack lines). `RealPcGameLoadTests` (the stock `GetAssembly`
asks the loader for the bare name and the patched one for the full name, executed on net8 with a recording load context;
the mjson and static-reference facts; the identity refusal), `RealPcPatchTests` (fingerprint, exact IL, second
application, ILVerify).

---------------------------------------------------------------------------------------------------

## 9. The PC kit (WP3, `tools/pc-kit/`)

### 9.1 Build

`tools/pc-kit/McvsOffline.PcKit.csproj`: net8 exe, `InvariantGlobalization`, references `patcher/McvsOffline.Patcher.csproj`
(Mono.Cecil 0.11.5 comes with it), `ValidateExecutableReferencesMatchSelfContained=false`; publish profile as the spike
(`tools/pc-spike/McvsOffline.PcSpike.csproj:23-33`): `-r win-x64 -p:McvsPcKitPublish=true` → one self-contained file,
`artifacts/pc-kit/McvsOffline.PcKit.exe`. The PC server DLL is an embedded resource (`LogicalName
"McvsOffline.Server.dll"`, from `artifacts/bin/McvsOffline.Server.Pc/release/`); the publish target fails when it is
missing or lacks `Gameloft.Logs` in its references (built without `MCVS_GAMELOG`), and runs the hook-contract check of
`PcHookContractTests` (§12.4) against that exact file (a small `McvsOffline.PcKit.Tests` entry point the target
invokes), so a kit is never published with a DLL that lacks a hook a PC patch imports. Linked from `tools/pc-spike/` (no
copies): `SteamLocator.cs`, `Vdf.cs`, `DebugOutputCapture.cs`, `CrashLogDecoder.cs`, `ConsoleLines.cs`, `StopSignal.cs`,
`Telemetry.cs`; WP3 may refactor a spike file only to remove a dependency on spike-only types, keeping
`McvsOffline.PcSpike.Tests` green. No game file, no reference assemblies, no download inside (the kit never ships
Gameloft binaries: the patched copies are made from the player's files on the player's PC).

### 9.2 Commands

`--game <folder>` (or `MCVS_PC_GAME`) skips the Steam lookup; double-click shows a numbered menu (as the spike). Steam
lookup: `HKCU\Software\Valve\Steam\SteamPath`, `HKLM\SOFTWARE[\WOW6432Node]\Valve\Steam\InstallPath`,
`steamapps\libraryfolders.vdf` (both layouts), `appmanifest_723780.acf` → `installdir`, then a scan of
`steamapps\common\*` (`SteamLocator`, tested by `SteamLocatorTests`). BCL: `%WINDIR%\Microsoft.NET\Framework\v4.0.30319`
(`--bcl` overrides). Kit log: `<install>\McvsOffline\kit\pc-kit.log` (or `%TEMP%\McvsOffline\kit\` before the folder
exists, and from the start of an `uninstall --remove-data`, §7.6). Temporary files: `install` writes the embedded server
DLL to `%TEMP%\mcvs-pckit-<guid>\` and deletes that folder in a `finally`; at start the kit also deletes
`mcvs-pckit-*` folders older than one day (a run that was killed).

| command | does |
|---|---|
| `install [--patch default\|<ids>] [--port n] [--dry-run] [--force]` | locate, write the embedded server DLL to a temp folder (`%TEMP%\mcvs-pckit-<guid>\`) for the resolver, `PcInstaller.Install` (§7.6), print the notes, then: "start the game from Steam; your saves will be in `<install>\McvsOffline`; uninstalling the game in Steam deletes them, use Saves › Export first" |
| `uninstall [--remove-data --yes]` | `PcInstaller.Uninstall` (the kit log moves to `%TEMP%` first with `--remove-data`, §7.6) |
| `status [--listing]` | `PcInstaller.Status`; with `--listing` also writes the game-folder listing (every top-level entry with size, time and SHA-256; per folder the file count and total size) to `McvsOffline\kit\listing-<stamp>.txt` (or `%TEMP%\McvsOffline\kit\` when `McvsOffline\` is absent) and, when an earlier listing exists, `listing-diff.txt` against the first one, classifying each difference as ours, the game's own (§7.6) or other |
| `verify` | `PcInstaller.Verify`, printing the `written` SHA-256 values (WR2 step 3) |
| `probe` | before the game runs, as the normal user: (1) http.sys: `HttpListener` with prefix `http://localhost:51750/`, `Start`, `Stop` (net8 `HttpListener` on Windows is http.sys too; Q2), reporting success, `HttpListenerException` code and message, or "port in use (is the game running?)"; (2) whether the kit runs elevated (then the probe is not representative: "run it from a normal prompt"); (3) `netsh http show urlacl` lines naming 51750 (read only); (4) bind `127.0.0.1:51780` with a `TcpListener`; `netsh int ipv4 show excludedportrange protocol=tcp` covering 51750/51780; (5) install folder writable (create and delete a probe file in `McvsOffline\`); (6) BCL folder files; (7) .NET `Release` value; (8) Steam running (`steam.exe` process); (9) tools that inject into games (process names of common capture/overlay tools, informational, [QL] §5); (10) the GPU adapters from the display class registry key (name, `HardwareInformation.qwMemorySize`/`MemorySize`, driver version and date), every display attached to the desktop with its mode and refresh rate (`EnumDisplaySettings`), the physical memory (`GlobalMemoryStatusEx`) (WR2 F7); (11) the Windows Firewall rules naming `ModernCombatVersus.exe` (`netsh advfirewall firewall show rule name=all verbose`, each rule's lines as Windows prints them, Q5) |
| `watch` | while the game runs (until it exits or Ctrl+C): memory telemetry every 2 s (private bytes, virtual size, % of 2048 MB; the spike's `Telemetry`), `OutputDebugString` capture (`DebugOutputCapture`), the game's TCP/UDP endpoints every 10 s (`netstat -ano` parsed for the PID: 25001+ VDB, Finder 51750, Lobby/Controller, GS UDP), into `McvsOffline\kit\watch-<stamp>\` (Q5, Q6). Every 2 s it also reads the new lines of `McvsOffline\logs\bootstrap.log` and of the `%TEMP%` fallback (`StallDumps`; it follows the host's rotation at each start). When a STALL line reaches 60 s, it takes one out-of-process `MiniDumpWriteDump` of the pid that the line names (an older host's line has no pid; then the only running game is dumped). The dump type is 0x1826: full memory, handles, unloaded modules, memory info and thread info. The kit is x64 and the game WOW64, so use `.effmach x86` in WinDbg. The dump goes to `%LOCALAPPDATA%\McvsOffline\dumps\ModernCombatVersus-<pid>-<stamp>-stall-<s>s.dmp`. There is one dump per stall, at most 3 per watch and 6 in the folder, and only with 5 GB free (WR2 F4) |
| `collect` | one zip on the Desktop, `mcvs-offline-pc-<yyyyMMdd-HHmmss>.zip`: `McvsOffline\logs\**`, `%TEMP%\McvsOffline\logs\**`, `McvsOffline\kit\**`, `settings.json`, `install.json`, the config, `args.txt`, a listing of the game folder (names, sizes, SHA-256 of exe and DLLs), the `status` and `probe` output, Application event-log entries for `ModernCombatVersus.exe` of the last 7 days (`wevtutil /f:xml`, read in the ANSI code page: `windows-events.txt` with `TimeCreated/@SystemTime` in UTC, `windows-events.xml` raw, each event parsed on its own and one that is not readable XML kept as text in `windows-events.raw.txt`, counted as unparsed in `summary.txt`; WR2 F9), the game's crash logs under `%LOCALAPPDATA%\Gameloft` decoded (`CrashLogDecoder`); a listing (names, sizes, dates, no contents) of `Documents\GDID.bin`, `DC.bin`, `HDIDFV.bin`, of any `Gameloft` folder under `%APPDATA%` and `%LOCALAPPDATA%`, and of every `MyLocalStorage*` file in the game folder, `McvsOffline\` and below the `%APPDATA%`, `%LOCALAPPDATA%` and Documents folders named like `gameloft` or `ModernCombat*` (`local-storage-locations.txt`). `summary.txt` starts with the kit's version and its own SHA-256 (as does every command header in `pc-kit.log`) and names `core.dmp` with size and UTC time. Never `profiles\`, `accounts\`, `tokens.json`, `core.dmp`, and never the contents of the Gameloft id files or of `MyLocalStorage`. `summary.txt` and `dumps.txt` list the stall dumps of `watch` and the game's WER LocalDumps (`%LOCALAPPDATA%\CrashDumps\ModernCombatVersus.exe.*.dmp`) with path, size and time; the zip never contains them (WR2 F4) |
| `help` | usage |

Exit codes as §7.6. The SmartScreen and antivirus caveat of `docs/PC-SPIKE.md` §2 applies.

---------------------------------------------------------------------------------------------------

## 10. WP-G: Android manifest patch `game`

From [SG] §3.3 option A and §7, with the [SG-V] corrections.

* New `patcher/Manifest/ManifestGame.cs`: `PatchId = "game"`, `IsGameId = 0x010103f4` (boolean), `AppCategoryId =
  ManifestTargetSdk.AppCategoryId` (0x01010545, enum, game = 0; `ManifestTargetSdk.cs:81`), `CategoryGame = 0`.
  `Edit(byte[] manifest, ICollection<string> notes)`: exactly one `manifest/application` element, else
  `InvalidDataException`; refuse (nothing written) when `isGame` or `appCategory` is already present, any value; insert
  `BinaryXml.NewAttribute.Android("isGame", IsGameId, Value.Boolean(true))` (TYPE_INT_BOOLEAN, 0xFFFFFFFF) and
  `Android("appCategory", AppCategoryId, Value.Integer(0))` (TYPE_INT_DEC 0), both raw value -1, as `aapt2 link` writes
  them ([SG] §3.1, [SG-V] row 5); notes with the prefix `"manifest: game: "` (keeps the `"manifest: android:"` count of
  `ManifestTargetSdkTests.cs:389-390`); `Verify`: re-parse, both attributes with exact type and data, android attribute
  ids ascending, the element list (path, `android:name`, line) unchanged, `<application>` has two attributes more and
  every original one unchanged.
* `patcher/ApkPatcher.cs`: call it after the targetsdk block (`:160`) when `options.Patches.Contains("game")`, with the
  log line `patch game: android:isGame="true" and android:appCategory="game" on <application> (Game Mode / OEM game
  detection)`. `CheckTargetSdk` (`:272-284`) refuses 36 without `game` ("target SDK 36 needs the 'game' patch
  (appCategory="game" keeps the fixed orientation on large screens)").
* `patcher/Manifest/ManifestTargetSdk.cs`: at 36 no longer inserts `appCategory` (`:205-211`) nor checks it in `Verify`
  (`:273-274`); keeps it in the pre-check (`:156-160`: the input must be stock, and `game` runs later); doc comment
  (`:29-31`).
* `patcher/PatcherOptions.cs`: `game` in `DefaultPatches` and `OptionalPatches`; `Program.cs`: one `--patch` help line.
* No `game_mode_config` (no benefit, [SG] §4).
* Docs (WP-G): `docs/PLAYER-SETUP.md` phone checks: Gaming Hub › My games (and Add game); `adb shell cmd game list-modes
  com.gameloft.android.ANMP.GloftMVHM`, which on **Android 16** before the patch prints `Package … is not of game type,
  to use the game mode commands, it must specify game category in the manifest as android:appCategory="game"` (the
  "unsupported, []" output is Android 12-15 only) and after it `current mode: standard, available game modes:
  [standard,custom]` ([SG-V] row 10); `adb shell dumpsys game`; on AOSP-derived Settings the app's storage moves to
  "Games", since `isGame` is still read there ([SG-V] "missed" 2). `docs/research/target-sdk.md:320`: appCategory now
  comes from `game`.
* Side effects to document (in `docs/STATUS.md`, which WP5b owns and writes from this list, §16): GameManager reports
  a mode; PowerHAL `Mode.GAME` in the foreground; a possible 60 Hz game-default frame-rate override (harmless at
  30 fps); games exempt from universal resizability on Android 16 at any target; a loading-boost hook only when configured ([SG] §5, [SG-V] "missed" 3). Samsung's own classifier is keyed
  by package name; the manifest is the only lever the APK has ([SG] §1.2).

---------------------------------------------------------------------------------------------------

## 11. Work packages

### 11.1 Ownership rules and integrator

* Every file below belongs to exactly one package; nobody else edits it. A package adds files only inside folders it
  owns or as listed. Existing tests a package breaks are fixed by that package.
* **Shared files with disjoint hunks** (the only exceptions, merged in §11.4 order): `patcher/ApkPatcher.cs` (WP3: the
  IL block `:95-129`, `PatchAssembly` `:194-218` (becomes the forwarder) and `CheckServerAssembly` `:566-587`; WP-G: the
  manifest block `:131-166` and `CheckTargetSdk` `:272-284`), `patcher/Program.cs` (WP3: the `pc` verb; WP-G: one help
  line) and `McvsOffline.sln` (D0-a: the two kit projects; D0-b: the PC csproj). `PatchTests.cs` and
  `RealGamePatchTests.cs` call `ApkPatcher.PatchAssembly`, which keeps its signature, so neither changes for WP3.
* **Integrator: WP5**, in two parts. **WP5a** (before WR2): `McvsVersion.Build`, the end-to-end PC run with
  `MCVS_REQUIRE_PC=1` (§12.6), the kit publish, the draft `PLAYER-SETUP-PC.md` the owner follows in WR2. **WP5b** (after
  WR2): the WR2 analysis, §13/§14 results here, every other doc outside this design and the per-package protocol notes
  (§16), `PLAYER-SETUP-PC.md` final, STATUS. Contract changes go through WP5, which updates §3 here.
* Suggested staffing (4 agents): **A** WP1 → WP5; **B** WP2; **C** WP3; **D** WP4 → WP-G. With 5 agents WP-G is its own.

### 11.2 Day-0 commits (contract-first, each < 2 h, in this order)

1. **D0-a (WP1)**: `server/McvsOffline.Server.Embedded.csproj` defines (§5.1); new `server/McvsOffline.Server.Pc.csproj`
   (the file only, **not** yet in the solution: it cannot build before D0-b); `FileSystem.cs` split (§3.1, §4.6; the
   `WindowsFiles.Rename` body may first be trial 5's `File.Replace`/`File.Move` stand-in); `HttpServer.cs`,
   `LocalAssets.cs`, `SocketReuse.cs` guards (bodies included, §3.1); `McvsConstants.ClientIdSteam`, `KnownPlatforms`,
   `IsGameClientId`; `AdminContracts.cs` additions of §3.3 (`CanOpenInBrowser`, trigger `"browser"`); skeleton projects
   `tools/pc-kit/McvsOffline.PcKit.csproj` (a `Main` that prints "not implemented") and `tests/McvsOffline.PcKit.Tests/`
   (one placeholder test), both added to `McvsOffline.sln`. Green: the solution builds as before.
2. **D0-b (WP2)**: the mechanical host split: `MCVS_EMBEDDED` → `MCVS_ANDROID` in `hosts/embedded/Android/*.cs` and
   `Bootstrap.cs` (three-way); every `#if !MCVS_WINDOWS` site of §3.1; `IEmbeddedPlatform`/`IAdminPlatform`/
   `AdminPlatformCapabilities`/`IPagePlatform`/`AndroidCapabilities` (§3.3) with `AndroidPlatform.Capabilities()` and
   `DirectoryPlatform` texts; `AdminHost` on the seam; `GameLogBridge` moved; `hosts/embedded/Windows/WindowsPlatform.cs`
   (safe defaults), `WindowsBoot.cs`, `hosts/embedded/Diagnostics/ThreadDumpStub.cs`, the `NativeThreads` guard,
   `PcHooks.cs` with the §3.2 signatures (bodies: `QuickLaunch` returns `link.QuickLaunch(ct)` by reflection, the others
   log only); then `server/McvsOffline.Server.Pc.csproj` added to `McvsOffline.sln`. **D0 check** before pushing: `dotnet
   build McvsOffline.sln` (which now builds the PC DLL with the §5.1 exclusions), the Android embedded build and the
   net8 tests all green, and the PC DLL has no `libc` ModuleRef. Day-0 commits are pushed only green.
3. **D0-c (WP3)**: `patcher/Core/BuildProfile.cs` (both profiles, full data), `PatchContext.Build` + 3-argument ctor,
   `IlPatch.ForBuild`/`SystemMethod`, `AssemblyPatcher` (moved code, `ApkPatcher.PatchAssembly` as its forwarder),
   `patcher/Pc/PcAssemblyResolver.cs`, `patcher/Pc/PcArgsBlock.cs`, `PcInstaller` option and report types with
   `NotImplementedException` bodies; `tests/McvsOffline.Embed.Tests/Support/TestEnvironment.cs` gains `PcRefs`,
   `Net452Refs`, `RequirePc`, `PcServerDll` (resolved for the test's own configuration, §12.4), `PcReader`,
   `AssertPcCorlib`; `Support/PcHookServer.cs` (§3.4).
4. **D0-d (WP4)**: `patcher/Core/PatchPlan.cs` with the PC ids and order (§8.1) and `PatchPlanTests`; skeleton classes
   `EngineBridgeHookPatch`, `QuickLaunchPatch`, `HeadlessDiagnosticsPatch` whose `Apply` throws
   `PatchException("not implemented")`; `AndroidOutputUnchangedTests` with its expected values recorded now, before any
   patch class changes (§12.4).

### 11.3 The packages

**WP1 — Server, PC build, harness** (owner A).
Files: `server/McvsOffline.Server.Pc.csproj`, `server/McvsOffline.Server.Embedded.csproj`, `McvsOffline.sln` (its D0-a
hunk), `scripts/build.sh`; `server/Core/Storage/FileSystem.cs`, `server/Core/Storage/WindowsFiles.cs` (new),
`server/Core/Http/HttpServer.cs`, `SocketReuse.cs`,
`LogRedaction.cs`, `RequestLogger.cs`, `server/Core/Content/DataPack.cs`, `server/Core/McvsConstants.cs` (not
`McvsVersion.Build`), `server/Services/Assets/LocalAssets.cs`, `server/Services/Admin/CurrentPlayerResolver.cs`,
`AdminContracts.cs` (§3.3 additions, doc comments), `SettingsSchema.cs` (platform filter, PC texts, the new label;
§4.6), `ActionsApi.cs` (`actions/open-in-browser`), `Admin/Saves/AdminFiles.cs`, `ImportUploads.cs` and `SaveTransfer.cs`
(the case audit, §4.6), `Admin/Logs/LogTail.cs`, `server/Services/Clans/ClanStore.cs`,
`server/Services/Wall/WallService.cs`, `server/Services/Eve/EveService.cs`, `server/Services/Crm/CrmNewsfeed.cs`
(`CrmDocumentValidator.cs` if the optional warning is done), `server/Services/Pandora/ServiceUrls.cs`,
`server/Services/Misc/MiscService.cs` (one line), `server/Services/Misc/ConnectivityTracker.cs` (new),
`server/Content/admin/index.html`, `docs/protocol/misc.md`, `docs/protocol/content.md`; tests under
`tests/McvsOffline.Tests/` that these change, plus `PcEmbeddedAssemblyTests.cs`, `TestFolders.cs`,
`Misc/ConnectivityTrackerTests.cs`, `Storage/WindowsFilesTests.cs`, `Admin/SettingsSchemaPlatformTests.cs`,
`Admin/CaseCollisionTests.cs`, the four loaders, `Auth/FedClientReplay.cs`, `McvsOffline.Tests.csproj`.
Provides: §3.1 defines in csprojs, §4, the PC DLL artifact, `MCVS_PC_REFS`/`MCVS_REQUIRE_PC` in `build.sh`.
Consumes: D0-b (to build the PC DLL green).

**WP2 — Windows host and hooks** (owner B).
Files: `hosts/embedded/**` (all; `ArgsFile.cs` and `ManagedArgs.cs` need no change), `McvsOffline.sln` (its D0-b
hunk), `tests/McvsOffline.Embed.Tests/Embedded/**` (new `Windows*Tests.cs`, `PcHooksTests.cs`,
`PcHooksReflectionTests.cs`, `GameOptionsWindowsTests.cs`, `GamePatchProbeWatchTests.cs`, `AndroidCapabilitiesTests.cs`,
`PcPInvokeTests.cs`, `PcHostWiringTests.cs`, `PageOpenerFallbackTests.cs`, `SteamOverlayTests.cs`,
`SystemBrowserTests.cs`, `RelaunchHelperTests.cs`; updates to `AdminHostTests.cs` (the matrix
moves to `AndroidCapabilitiesTests`), `AdminHooksContractTests.cs`, `EmbeddedHostTests.cs`, `GraphicsProfileTests.cs`,
`PInvokeAbiTests.cs`, `GameThreadWatchdog*Tests.cs` as needed).
Provides: §3.2 hooks, §3.3 interfaces, §3.8 host lines, §6. Consumes: WP1's `FileSystem` at run time only (tests run
net8).

**WP3 — Patcher PC mode and the kit** (owner C).
Files: `patcher/Core/**` except `PatchPlan.cs`, `patcher/Pc/**`, `patcher/Patches/IlPatch.cs`, `patcher/ApkPatcher.cs`
(its hunks), `patcher/Program.cs` (its hunk), `tools/pc-kit/**`, `tools/pc-spike/**` (dependency refactors only),
`tests/McvsOffline.PcKit.Tests/**`, `tests/McvsOffline.Embed.Tests/Patcher/Pc/{BuildProfileTests,PcResolverTests,
PcExeConfigTests,InstallManifestTests,PcArgsBlockTests,PcInstallerTests,PcInstallerFaultTests,ServerAssemblyCheckTests}.cs`,
`tests/McvsOffline.Embed.Tests/Support/TestEnvironment.cs`, `tests/McvsOffline.Embed.Tests/Support/PcHookServer.cs`.
Provides: §3.4 API and test support, §3.10, §7, §9. Consumes: `PatchPlan` (D0-d; the installer tests inject a plan of
the patches already done so they do not wait for WP4), the PC server DLL (tests use `PcHookServer` until WP1/WP2
deliver), the witness names of §3.9.

**WP4 — PC patch tables** (owner D).
Files: `patcher/Patches/*.cs` except `IlPatch.cs` (new `EngineBridgeHookPatch.cs`, `QuickLaunchPatch.cs`,
`HeadlessDiagnosticsPatch.cs`; PC tables in `HeadlessGamePatch.cs`, `NativeOnlinePatch.cs`, `WaitsPatch.cs`,
`AdminPagePatch.cs`, `AdminLinkPatch.cs`, `GraphicsPatch.cs`), `patcher/Core/PatchPlan.cs`,
`tests/McvsOffline.Embed.Tests/Patcher/Pc/RealPc*Tests.cs` (including `RealPcHeadlessExecutionTests.cs`,
`RealPcJitTests.cs`), `PcHookContractTests.cs`, `PatchPlanTests.cs`, `AndroidOutputUnchangedTests.cs`.
Provides: §8, the witnesses of §3.9. Consumes: D0-c (`ForBuild`, `SystemMethod`, `BuildProfile`, `PcReader`,
`AssertPcCorlib`, `PcHookServer` with the hook signatures of §3.2; `RealGamePatchTests.HookServerSource` is not
touched); the real-DLL leg of `PcHookContractTests` skips only while `MCVS_REQUIRE_PC` is unset and the artifact is
missing (§12.4).

**WP-G — Android `game` flags** (owner D, or a fifth agent).
Files: `patcher/Manifest/ManifestGame.cs` (new), `patcher/Manifest/ManifestTargetSdk.cs`, `patcher/PatcherOptions.cs`,
`patcher/ApkPatcher.cs` and `Program.cs` (its hunks), tests `Patcher/ManifestGameTests.cs` (new),
`ManifestTargetSdkTests.cs`, `RealGameTests.cs`, `PatchTests.cs`, `Support/AxmlWriter.cs`, docs
`docs/PLAYER-SETUP.md` (phone checks), `docs/research/target-sdk.md`. Depends on nothing.

**WP5a — Integration and kit publish** (owner A, after the others, before WR2).
Files: `McvsVersion.Build`, this design (§3 changes; the expected WR2 values under §13), `docs/PLAYER-SETUP-PC.md`
(new; the draft for WR2). Runs §12.6
and publishes the kit; hands the owner the §13 checklist.

**WP5b — WR2 analysis and docs** (owner A, after WR2, and after WR3 when WP6 ran).
Files: `docs/PLAYER-SETUP-PC.md` (final), `docs/STATUS.md`, `README.md`, `docs/ARCHITECTURE.md`, `docs/MODULES.md`,
`docs/ROADMAP.md`, `docs/PC-SPIKE.md`, `docs/research/pc-port.md` (a header note), `docs/design/settings-page.md` §8.10,
this design (§13/§14 results). Fixes found in WR2 go back to the owning package (WP1-WP4) as small follow-ups before
WP5b closes.

**WP6 — Finder replacement (D4 option b; only if WR2 Q2 fails)**, §15.

### 11.4 Dependencies and merge order

```
D0-a → D0-b → D0-c → D0-d → { WP1, WP2, WP3, WP4, WP-G } (parallel) → WP5a → WR2
     → (only if Q2 fails: WP6 → kit republished by WP5a → WR3) → WP5b
```

WR3 (only after WP6) repeats WR2 steps 2, 3, 4, 5, 8 and 16 with the new kit and `pc.finder: server`. Merge order after
day 0: WP1, WP2, WP4, WP3, WP-G, WP5a; WP5b last. WP3's end-to-end test with every real patch and the real PC DLL
(`PcInstallerTests.RealFolderEndToEnd`) is required only from WP5a on (`MCVS_REQUIRE_PC=1` plus the artifact, and
`MCVS_REQUIRE_PC_E2E=1`, §3.7: before it the test skips while a Day-0 skeleton patch still refuses).

---------------------------------------------------------------------------------------------------

## 12. Test plan

Everything below runs on Linux. Real-file tests read `MCVS_PC_REFS` read-only (copies into temp folders when they
write), skip without the files, and fail instead when `MCVS_REQUIRE_PC=1`. Every test that reads a PC assembly with
Cecil reads it through `TestEnvironment.PcReader` and calls `TestEnvironment.AssertPcCorlib` once (C12: without it,
Cecil on framework-dependent Linux resolves `mscorlib 4.0.0.0` to the .NET 8 facade and the test checks members against
the wrong library, [PP-V] §9). ILVerify runs as in
`RealAdminPagePatchTests.AssertIlVerifyClean` (`:593-625`): references = the 12 net452 assemblies of
`MCVS_NET452_REFS` (`mscorlib, System, System.Core, System.Net.Http, System.Xml, System.Xml.Linq, System.IO.Compression,
System.Windows.Forms, System.Drawing, System.Runtime.Serialization, System.Data, System.Numerics`) + every other PC DLL +
the server DLL, `--system-module mscorlib`, and the whole-assembly findings before and after the patch must be equal
([PP-V] §5: Engine 290, Game 1, Util 3 pre-existing findings).

### 12.1 WP1

* `PcEmbeddedAssemblyTests` (always; the PC DLL needs no game file): AssemblyRefs exactly `mscorlib`, `System`,
  `System.Core` 4.0.0.0 `b77a5c561934e089` (+ `Gameloft.Logs` 1.0.0.0 when built with `MCVS_GAMELOG`); none of the
  refused names; ModuleRefs ⊆ {`kernel32`, `user32`, `steam_api`}; `McvsOffline.Bootstrap::Init()` public static void;
  the four `PcHooks` signatures; `McvsOffline.Embedded.Windows.WindowsPlatform` present; no type from
  `McvsOffline.Embedded.Install`/`.Hd` and no `IInstallerPlatform`, no `GameReflection.Verify`; ILVerify of the whole
  DLL against net452 clean (t7 precedent). The DLL is the one of the test's own configuration
  (`artifacts/bin/McvsOffline.Server.Pc/<config>/`, `TestEnvironment.PcServerDll`).
* `WindowsFilesTests`: the rename helper with an injected native call (0, 32 then 0, 5 five times, another error) and
  the retry counts; `FileSystemRetryTests`: the retry helper (attempts, delay, the last exception);
  `SocketReuseTests` unchanged on net8.
* `SettingsSchemaPlatformTests`: `GroupsFor("windows")` has no `hd.*`, `install.*`, `notifications.*` entry, the PC
  `graphics.profile` text and the `embedded.physics.keepPhysicsLayer` label; `GroupsFor("android")` equals today's
  `Groups` output.
* `CaseCollisionTests`: at every audited site of §4.6, two names that differ only in case are treated as one name.
* `DataPackTests`: hex, decimal and plain layouts on synthetic folders; real PC (`$MCVS_PC_REFS/Data`):
  `Config/glue.json` → `187993782.f`, `Config/crmpreconfiguration.json` decodes (GCBF), `GameOptions_windows.json` →
  `462043511.f`; Android unchanged.
* `LogRedactionTests`: MAC forms (`:`, `-`, `%3A`, JSON, `global_device_id`), `mcvs…` ids untouched; SteamID64 in a
  form, JSON and `anonymous:<id>`; other 17-digit numbers and longer runs untouched; access tokens: the game's real
  `GetAccessToken` line (`RedactsTheAccessTokenOfTheGamesSessionLog`), anywhere in a line and several per line, wrong
  shapes untouched (`RedactsAccessTokensAnywhere`); idempotent; the fast path returns the same instance, also for paths
  containing `mcvs` (`PathsWithMcvsKeepTheFastPath`). `AuthorizeTests`: the 401 diagnosis lines carry `mcvs…<redacted>`. `RequestLoggerTests`: the four outputs carry no MAC (a request through `McvsServer.Handle`, a
  `MemoryLogSink`, the jsonl file).
* `ConnectivityTrackerTests`: the spike's body, a body starting with `&`, no `port`, an unknown `type`, a GET → 200 `{}`;
  a handler fault still 200; one INFO line without `device_id`; pandora locate of `online_connectivity` without warning;
  `/online_connectivity/connectionstatus` wins over the prefix alias.
* `CrmNewsfeedTests` (+ the golden baselines that contain the item): `end_date` = week start + 37 days, ISO 8601,
  ≥ 30 days after `timestamp`.
* `CurrentPlayerTests`: the session rule accepts the steam client id, refuses an unknown platform.
* Harness: the 38 real-client tests as theories over `RealClientFolders()`; with `MCVS_PC_REFS` all green; the PC-only
  newsfeed dictionary case.

### 12.2 WP2 (net8 tests compile `hosts/embedded/**` without defines, so `Windows/` is in them)

* `WindowsPlatformTests`: names and texts; not `IInstallerPlatform`/`INotificationPlatform`; game root rules
  (`Data\MCVS.game` required); fallback log directory; `Capabilities()` exactly as §6.4.
* `AndroidCapabilitiesTests`: today's `AdminHostTests.cs:60-116` matrix (the sdk 21/29/33 rules, missing JNI members),
  moved unchanged onto `AndroidCapabilities.From`; `AdminHostTests` keeps checking how `Describe` combines a fake
  platform's capabilities with the game state.
* `WindowsPageOpenerTests` (fake `ISteamApi`, fake browser): `auto` overlay when enabled, else system; `overlay` only;
  `system` only; `game` → `OpenManaged`; `jni`/`post` → `auto` with one WARN; lease taken and released on failure; the
  log line names the route; Android's switch unchanged (existing `AdminHostTests`). `PageOpenerFallbackTests` (injected
  clock): an `auto` open after an overlay open with no `PageAlive` within 20 s passes `system` and logs `overlay showed
  no page last time`; a `PageAlive` in time keeps `auto`; trigger `browser` always `system`; Android unaffected.
* `SteamOverlayTests`: call order, every null or false stops with one logged reason, exceptions caught.
* `SystemBrowserTests`: `Open` returns before an injected launcher that blocks finishes; the launcher runs on an STA
  background thread; a throwing launcher gives one WARN without the URL.
* `WindowsShareTests` (names, collisions ignoring case, sanitising), `WindowsClipboardTests` (fake Win32: owner =
  `MainWindowHandle`, else a message-only window that is destroyed; never `OpenClipboard(NULL)`; `SetClipboardData`
  failure frees the `HGLOBAL`, success does not; retries when held; truncation), `RelaunchHelperTests` (the exact
  PowerShell command line for a pid), `WindowsExitTests` (fake game: `ExitAppAndroid` posted; host path order helper →
  flush → kill, with injected actions).
* `WindowsThreadsTests`: injected thread samples → the stall lines (state, wait reason, CPU delta, game thread first);
  `GameThreadWatchdog` with an injected `IThreadSnapshot` writes them instead of the `/proc` lines.
* `PcPInvokeTests` (Cecil on the built PC DLL of the test's configuration; fails under `MCVS_REQUIRE_PC=1` when the DLL
  is missing): every `steam_api` `PInvokeImpl` is `CallConvCdecl` with an `EntryPoint` present in the export directory
  of `$MCVS_PC_REFS/steam_api.dll` (PE export table parsed by the test); `IsOverlayEnabled` and `IsSteamRunning` return
  `bool` with `MarshalAs(U1)`; the string parameters of the steam_api imports are ANSI with `BestFitMapping` off; every
  kernel32/user32 import names its exact export (`ExactSpelling`), the `*W` ones are `CharSet.Unicode`; `MoveFileExW`,
  `OpenClipboard`, `SetClipboardData`, `CreateWindowExW` have `SetLastError`. `PInvokeAbiTests` gains a module filter so
  its Android ABI rules (off_t, ssize_t) apply to `libc` imports only and do not misfire on the Windows imports the net8
  test assembly now contains.
* `PcHostWiringTests` (Cecil on the built PC DLL): `Bootstrap::Init` calls `WindowsBoot::Init`, which reaches
  `EmbeddedHost::Start` with a `newobj WindowsPlatform::.ctor`; `FileSystem::Rename` calls `WindowsFiles::Rename`, which
  calls `MoveFileExW` through `FileSystem::Retry`; no call into `Install`/`Hd` remains. With the net8 tests running
  `WindowsBoot`, `WindowsFiles` and `WindowsThreads` themselves, every `#if MCVS_WINDOWS` line is either covered here
  or a one-line delegation (§3.1).
* `PcHooksTests` (execution): a Roslyn look-alike `Gameloft.Engine` (`ClientComponent`, `ClientLink`, `ClientSettings`,
  `ServerLocation`, `LocalAnubisManager`, `Finder`, `MatchmakingComponent.AutomatchSettings`) and `Gameloft.OnlinePlatform`
  (`Session.GetService<T>`, `MatchmakerService.QuickLaunch(string, CancellationToken, string, int?)`, `Room`) in a
  collectible ALC, a single-thread test `SynchronizationContext`: Embedded → `Start` once, then `QuickLaunch("ts", ct,
  "localhost", 51750)`, `link.Room` set, every continuation on the context thread; already started → no second
  `Start`; not Embedded → `link.QuickLaunch(ct)` only; `Start` faults with `HttpListenerException` → the returned task
  faults with that exception and one WARN naming http.sys; `QuickLaunch` faults or is cancelled → same; a missing
  member → faulted task, WARN; no synchronization context → WARN and still completes; **a log line in every catch**
  (each injected failure produces exactly one WARN). `DefaultAutomatchHost`: started + empty → set; seeded → kept; not
  started → untouched; a throwing setter → WARN, no exception.
* `PcHooksReflectionTests` (real PC DLLs): every member `PcHooks` uses resolves in `Gameloft.Engine.dll` and
  `Gameloft.OnlinePlatform.dll` (as `RealGameReflectionTests`, which also runs on the PC DLLs, [CR] F4).
* `GameOptionsWindowsTests`: synthetic and real (`462043511.f` decoded through `DataPack`): `auto` + keep → only
  `"Physics"` removed from MEM `LowEnd` (`"VFX"` left), everything else equal as JSON; keep false → text unchanged;
  `mid`/`high` → single empty-rule selection; `ultra` → HighGPU with the note; idempotent; malformed input unchanged;
  the removed count on the real file is recorded and appears in the `[boot] graphics:` line (`MEM rule on, removed <n>`;
  `MEM rule off` when keep is false). `GraphicsHooks` PC applied path against a look-alike `GameOptionManager`.
* `GamePatchProbeWatchTests`: Game loaded later → `StorePatch` filled and the `[boot] patches:` line written once; the
  witness literals equal the §3.9 names.
* `EmbeddedHostTests`: a Windows-like platform gets no `install.*`/`hd.*`/`notifications.*` defaults, gets
  `embedded.physics.keepPhysicsLayer`, and `lanMode` true is overridden with one WARN; it calls
  `GraphicsHooks.ConfigureWindows(true)`, or `(false)` when the setting is false, and `PcHooks.Configure`; a
  `*_args.txt` in the game root gives the WARN and the status line. **P1 on the host side**: a Windows-like platform on a
  temp game root (a copy of the depot names, empty files), one boot and stop, then the root's top-level entries equal
  before ∪ {`args.txt`, `McvsOffline\`}, with nothing else created, renamed or deleted.
* `PcArgsBlock` contract (owned by WP3, runs here): markers equal `ArgsFile`'s; `Remove(ArgsFile.Merge(x, entries).Bytes)`
  gives back `x` for x = absent, empty, the player's own lines, CRLF and LF files.

### 12.3 WP3

* `BuildProfileTests`: both profiles' data; the PC hashes and MVIDs equal the real files (`MCVS_PC_REFS`);
  `GameFolder.DepotTopLevel` equals the top-level names of `MCVS_PC_REFS`.
* `PcResolverTests`: `mscorlib` resolves to `MCVS_NET452_REFS/mscorlib.dll` (C12), never to the .NET 8 folder; an
  absent name throws; a name that exists only in the .NET 8 shared framework (`System.Runtime`) throws
  `AssemblyResolutionException`; game and server folders searched in order; `TestEnvironment.AssertPcCorlib` fails on a
  module read with a default resolver.
* `ServerAssemblyCheckTests`: the PC DLL passes; the Android DLL fails on PC (corlib 2.0.5.0); a stand-in with
  `System.Windows.Forms` or a `libc` ModuleRef fails; the corlib-scope equality rule.
* `PcExeConfigTests`: the exact §7.4 text for build 6372049 versions; href validation; `IsOurs`/`IsSpikeConfig`.
* `InstallManifestTests`: round trip; key order; MiniJson reads it.
* `PcInstallerTests` on a temp copy of the install with a stand-in plan: the written tree is exactly §7.4; install.json
  matches; re-run = reinstall; `status` states (current, stale after one byte of `Game.dll` changes, damaged, partial);
  a foreign config refused before any write; a `*_args.txt` refused before any write (also with `--force`); a spike
  config replaced; game running → exit 4 and nothing written; uninstall leaves the copy equal to the original tree plus
  the data folder, and `args.txt` gone (only our block) or back to the player's lines; game-written files (`dr0.bin`,
  `OF_PP.bin` placed in the copy) are listed apart by `status` and left by uninstall; `--remove-data`.
  `RealFolderEndToEnd` (WP5a on): every real patch + the real PC DLL; the outputs pass the reference-set check and
  ILVerify; the `written` SHA-256 values are printed and recorded for WR2 (§12.6).
* `PcInstallerFaultTests` (injected `IPcFileOps`): a write or rename failure after each step of §7.4 (each bin file,
  `install.json`, the config) → the reported state (`PARTIAL`, or `not installed` with a `*.mcvs-tmp` leftover), no
  config that points at a missing file, and a following `uninstall` restores the original tree exactly; uninstall from
  `PARTIAL`, `DAMAGED` and `STALE`; with Windows semantics (deleting a file that is open for writing fails),
  `uninstall --remove-data` with the kit log open in `McvsOffline\kit\` leaves no `McvsOffline\` and the log continues in
  the temp folder.
* `McvsOffline.PcKit.Tests`: command parsing, the menu, `probe` result formatting (probe actions injected), `collect`
  contents and exclusions on a fake tree (including the Documents/`%APPDATA%` listing without contents), the Steam lookup
  (reusing the spike's `SteamLocatorTests` data), `install` removes its `mcvs-pckit-*` temp folder on success and on
  failure; the publish-time hook check entry point.

### 12.4 WP4

For every PC patch: fingerprints of every site on the real PC file; apply alone and in the full plan; apply on the
Android file with the PC profile and on the PC file with the Android profile both throw before any change (module
unchanged: every method fingerprint equal); a second application refused; the exact inserted IL (structural asserts);
ILVerify before/after equal; reference set (P3). Every one of these reads through `TestEnvironment.PcReader` and asserts
the resolved corlib with `AssertPcCorlib` (C12).

* `PatchPlanTests`: for `PcSteam6372049`, `Ids().Mandatory` = {`hook-pc`, `headless`, `headless-engine`, `physics`,
  `quicklaunch`, `headless-diag`, `graphics`}, `Default` and `Optional` as in §8.1, `Excluded` = {`hook`, `finder`,
  `lan`, `debloat`, `targetsdk`, `game`}; `For(build, [])` contains every mandatory id, in the §8.1 order;
  `For(build, ["forum"])` adds it at its place; each excluded id throws `ArgumentException`; for `Android1_17_32`,
  the ids and order of `For` equal `ApkPatcher.SelectPatches` for the same options (P5).

Execution on net8 with stand-ins (the Android `RealGamePatchTests` approach; net8 loads net45 images through its
facades, [PP] §6.1 item 6):
* `hook-pc`: patched real `EngineBridge.dll` + a look-alike `Gameloft.Engine` whose `MainLoop.Start` records + a
  recording server: `Init` once, then `MainLoop.Start(0, null, false)`; a throwing `Init` and a missing server DLL still
  start the game (as `MarkerPatchTests`).
* `quicklaunch` P1: the patched real `BaseServer::get_Port` invoked on an uninitialised object whose listener field holds
  a started `TcpListener(127.0.0.1, 0)` returns its port, and 0 with no listener; the fingerprint equals Android's.
  P2: the patched `ClientComponent.QuickLaunch` on an uninitialised component returns the stand-in hook's task with the
  arguments forwarded; a throwing hook falls back to `ClientLink.QuickLaunch`. P3: `AddAutomatchHostSettings` calls the
  hook with the settings object before the stock body.
* `headless-diag` M4: `CreateInstance` of a type whose constructor throws calls the stand-in with `(exception, type)` and
  returns null, when the engine's type initialisers run on net8; otherwise structural only (stated in the test).
* `RealPcHeadlessExecutionTests` (the uninitialised-World technique of `RealGamePatchTests.cs:303-321`: a
  `RuntimeHelpers.GetUninitializedObject` World with `<Headless>k__BackingField` set, a GameObject whose `world` field
  holds it, the component on that GameObject). Per site, headless = true reaches the inserted `ret`/`goto` target
  without touching the client-only code (no exception, or the exception of the target's first instruction where that is
  documented in the test), headless = false enters the original body (observed as the exception the body throws on an
  uninitialised object):
  * H8 `LoadPreferencesWindows`, H10 `GameClientComponent.OnCreate`, H11 `OnAppFocusLost`, M2 `GameplayComponent.OnUpdate`:
    both branches; for M2 also each retargeted entry branch (`IL_0025`, `IL_0044`) by asserting their operands are the
    first inserted instruction **and** by running the method with the field state that takes each branch, where an
    uninitialised object allows it (stated per branch in the test).
  * H9 `SteamAchievementsComponent.OnStart`: not executable here, because block A runs `base.OnStart()` (which reads
    the World's meta-game) before the guard is reached; the test says so and asserts, on the real patched method, that
    `IL_0011` and `IL_0019` target the first inserted instruction, that the inserted `brtrue` targets the final `ret`
    (`IL_00c5`), and that the instructions from the old `IL_0038` on are the stock ones; WR2 step 14 covers the
    behaviour (D3).
  * M3 `SteamAchievementsComponent.OnStop`: the two calls resolve to `BaseAchievementsComponent::OnStop()` (headless)
    and `::OnStart()` (client) on the real base type. Executed on an uninitialised component with both fields set to
    sentinel objects: headless = true runs the base `OnStop` (the fields are null afterwards, whatever it throws later
    on the uninitialised World), headless = false runs the stock `base.OnStart()` (the fields are not nulled: they are
    reassigned or the call throws before touching them). If `Component.OnStop` itself throws on an uninitialised object
    (before the fields are nulled), the test keeps the operand and branch asserts and says so.
  * A site where neither branch can run without a native World says why in the test, and keeps the structural assert.
* `nativeonline`: twin of `NativeOnlineInitRunsOnlyWhenTheHookAllowsIt` on the PC `Init_private` (the stand-in hook
  refuses → the native call is skipped, observed as no `EntryPointNotFoundException`; allows → the native call is
  reached, observed as that exception).
* `adminlink`: twin of `RealAdminLinkPatchTests.TheMarkerOpensThePageAndEveryOtherUrlTakesTheShippedPath` on the PC
  `BrowserComponent.OpenUrl`. `adminpage`: twin of `TheAccountActionOpensThePageAndEveryOtherActionRunsTheShippedBody`
  (the PC A2 stub, the `CorlibMethod` `op_Equality`). `graphics`: twin of
  `RealGraphicsPatchTests.TheHelpersForwardAndFallBackToTheTextAsRead`, with `OptionsApplied` forwarding the component
  object itself. `waits`: structural + ILVerify (as on Android).
* `RealPcJitTests.EveryPatchedPcMethodJits` (as `EveryPatchedMethodJits`): `RuntimeHelpers.PrepareMethod` on every
  patched PC method and every method of the helper types `EngineBridge.Class1::McvsOfflineInit`,
  `Gameloft.Engine.McvsQuickLaunch`, `McvsWorldDiagnostics`, `McvsGraphics`, and the `headless`/`adminpage`/`store`
  helpers in Game.dll, after loading the full patched plan with the stand-in server.
* `PcHookContractTests`: every `ImportServerMethod` of every PC patch resolves in `PcHookServer` and in the real PC
  server DLL of the test's own configuration (`TestEnvironment.PcServerDll`, from
  `artifacts/bin/McvsOffline.Server.Pc/<config>/`; the PC csproj is in the solution, so after `build.sh` it always
  exists): skipped when the DLL is missing only while `MCVS_REQUIRE_PC` is unset, a failure under `MCVS_REQUIRE_PC=1`.
  It also asserts that the witness constants of §3.9 equal the literals in `GamePatchProbe` and `PcInstaller`. The kit's
  publish target runs the same check on the DLL it embeds (§9.1).
* `AndroidOutputUnchangedTests` (`MCVS_REFS`): patches the Android assemblies for the default options with a **fixed**
  stand-in server (`FixtureCompiler` output of `RealGamePatchTests.HookServerSource` with its MVID pinned to a constant),
  so `DerivedMvid` no longer depends on the real server DLL that WP1/WP2 change on purpose; the SHA-256 of every
  patched assembly equals the value recorded in D0-d by running the same helper against the `main-dev` 7f24f82 patcher
  (P5). A second assertion, independent of any MVID: per patched method, the `IlPatch.Fingerprint` and the
  AssemblyRef/TypeRef/MemberRef tables equal those of a 7f24f82 output. Since build 17 this holds for the default options
  without `inbox`; a second test pins the full default set (only `Game.dll` differs, with the `Game.McvsInbox` type).

### 12.5 WP-G

[SG] §7 item 7 with the [SG-V] corrections: synthetic manifests through rename → debloat → targetsdk 35 → game, at 29
and 36 (no double `appCategory`), refusals (either attribute present, zero or two `<application>`), aapt2 xmltree and
badging when `MCVS_BUILD_TOOLS` is set (failing instead of skipping under `MCVS_REQUIRE_BUILD_TOOLS=1`, new), the real
APK (`AssertInvariant` holds, two attributes added), and the updated note counts and expected manifests in
`ManifestTargetSdkTests`, `RealGameTests` (107 → 109, 111 → 113), `PatchTests`. `ManifestGameTests` also pins P5's
manifest half: rename → debloat → targetsdk 35 **without** `game`, on the real manifest, gives exactly the bytes a
`main-dev` 7f24f82 patcher gives (recorded once as a SHA-256 in the test).

### 12.6 WP5a

* `scripts/build.sh` with `MCVS_REFS`, `MCVS_PC_REFS`, `MCVS_REQUIRE_PC=1`, `MCVS_REQUIRE_PC_E2E=1`, `MCVS_REQUIRE_REAL_CLIENT=1`,
  `MCVS_BUILD_TOOLS` and `MCVS_REQUIRE_BUILD_TOOLS=1` (the aapt2 dump of the real patched APK's manifest is mandatory
  once before release): everything green.
* `McvsOffline.Patcher pc install` (not only `--dry-run`) on a temp copy of the real folder with the DLL the kit embeds;
  its `written` SHA-256 and MVID values are recorded under §13 of this design as the **expected WR2 values** (step 3).
* The kit published.
* The Android APK rebuilt and compared entry by entry against a `main-dev` 7f24f82 build with the same options and key:
  only the entries P5 allows may differ (`McvsOffline.Server.dll`, the patched assemblies whose `AndroidOutputUnchangedTests`
  method/table check passes, `AndroidManifest.xml` with exactly the two `game` attributes added, `META-INF/*`); any other
  changed, added or removed entry fails.

---------------------------------------------------------------------------------------------------

## 13. WR3: the third Windows round (gate for WP5b)

### 13.0 What WR2 settled and what WR3 repeats

WR2 ran on 2026-10-29 with the build-13 preview kit on the owner's PC (Windows build 26200, pt-BR, .NET 4.8.1 Release
533509, UTC-4): 15 starts, two matches, the settings page twice. The analysis is in the WR2 notes of this round (the
synthesis and its verification pass); the rows below carry each step's WR2 result.

* **Settled by WR2:** the install writes exactly the tested bytes (every `written` SHA-256 of build 13's §13.1); `probe`
  (Q2: http.sys `localhost:51750` works for a standard user, so WP6 is not needed); the runtime line; quicklaunch with
  its continuations on the game thread (Q13); the MEM rule (`removed 1`, no Physics); the overlay route of the page from
  the inbox item (Q3's good branch, window mode unknown); the `GameLogBridge` attach (Q14's attach half); no tracker
  traffic with the defaults (Q8's traffic half); the lazy probe mechanism (Q7).
* **Not settled, because `Game.dll` was never ours (F1, Q1):** every step that needs a patch in `Game.dll`: the headless
  sites (Q4), the bot match (Battle › Classic) and the GS World (Q12), the Account menu entry (Q10), the inbox crash fix (`store`),
  the stall fix (`signatures`). Build 14 loads `Game` by its full name (`gameload`, §8.9; its `<qualifyAssembly>`
  entries, §7.4, are gone in build 16) and reports a start that ran the stock files as `installed, NOT ACTIVE` (§7.6, §6.2).
* **Build 14's other WR2 fixes** WR3 must see working: the native crash filter and the PROBABLY FATAL reports (F3,
  §6.4), the pid in the start and STALL lines, the kit's stall dumps (F4, §9.2), `game.log` at Fatal plus
  `gameLog.levels` with UTC times and redacted tokens (F5, F6), the PC graphics line from the in-game options (F7,
  §6.7), the kit capture fixes (F9), no admin 401 as a boot milestone and the stale-tab message (F8).

**WR3 must repeat steps 3 (with `verify`) to 6, 8, 11 to 13 and 15 with `Game.dll` active, and do 7, 8a, 8b, 9, 10, 14
and 16 to 18 for the first time** (19 is a question). Steps 4 to 18 only count when step 4 shows `Game` from
`McvsOffline\bin` and every witness `yes`.

Before starting: quit capture and overlay tools (OBS, Discord overlay, Medal, GeForce Experience and the like, [QL] §5),
do not start the game under a debugger (the `PEB.BeingDebugged` gate gives a silent dead window, [WH-V] C10), close the
game, and turn on WER LocalDumps for `ModernCombatVersus.exe` once as administrator (`DumpType` 2, `DumpCount` 3;
[PLAYER-SETUP-PC.md](../PLAYER-SETUP-PC.md) §6), so a native crash leaves a full dump (Q17). Unlock codes and the
unlock-all grant come only after the bot match (step 8b), so a stall cannot hide the match.

| # | step | record | settles | WR2 result |
|---|---|---|---|---|
| 1 | `status --listing` | leftovers of the WP0 spike (config, `McvsOffline\spike\`, args.txt block); the listing **before** install (`McvsOffline\kit\listing-<stamp>.txt`, or `%TEMP%\McvsOffline\kit\listing-<stamp>.txt` while `McvsOffline\` does not exist, as in WR2: every top-level entry with size, time and SHA-256, `not-hashed` for `core.dmp`, and per folder the file count and total size) | — | pass (the listing went to `%TEMP%\McvsOffline\kit\`; no WP0 leftovers) |
| 2 | `probe` from a normal (non-admin) prompt | http.sys result for `http://localhost:51750/`, urlacl lines, port 51780, excluded ranges, folder writable, .NET Release; GPU adapters, displays and refresh rates, physical memory, firewall rules for the game (F7, F9) | **Q2** (D4), Q5 | pass (http.sys ok as a normal user, not elevated; Release 533509) |
| 3 | `install`, then **`verify`** (mandatory: WR2 skipped it), then `status` | notes; `status` says "installed, current"; the `written` SHA-256 values `verify` prints **equal** the expected values of §13.1 (build 14, recorded from the Linux run of §12.6). Any difference blocks WP5b until explained (the tested bytes must be the shipped bytes) | R9 | partial: install byte-exact against §13.1 of build 13; `verify` **not run**; "installed, current" was shown although Game was not active (F2) |
| 4 | `watch` in a second window, then start the game from Steam; after the first start `status` | `[boot] runtime:` with the expected values: `process x86, LAA no`, `Release` ≥ 379893 (4.5.2; Windows 10/11 normally report 4.8.x, ≥ 528040); `[boot] assemblies:` every assembly from `McvsOffline\bin`, **`Game` included** (`Game …\McvsOffline\bin\Game.dll`); `[boot] patches:` every witness `yes` (`gameload yes`, `store yes`); no `NOT ACTIVE` in the `[status]` line, on the page or in the kit; `status`: `installed, current` and `activation: active at the last start`. If `Game` still comes from the game folder: stop, `collect`, and send it, with the `[boot] load stack: Game (GAME FOLDER)` lines under it (build 18: they name the loader; Q1, Q15). `assemblies: Game (no file) (elsewhere)` instead means the engine's `gameload` bind failed and `Gameloft.Sequences` loaded the stock file from bytes (§8.9): `collect` and send it too | **Q1**, Q7, **Q15**, F2 | runtime pass; **assemblies fail** (`Game` from the GAME FOLDER in 15/15 starts, F1); **patches fail** (`store NO`, a consequence) |
| 5 | reach the main menu | `[pc] quicklaunch:` lines (LocalAnubis started, room, thread id equals the game thread), no `headless world:`/`server world:`/`component … not created` lines, `[boot] graphics:` (GPU and MEM profile, `MEM rule on, removed <n>` with the expected count, `excludedLoadingLayers` without `Physics`) | **Q4** (TS), Q6, **Q13** | quicklaunch and Q13 pass (15/15); 35 headless reports, all at the stock Game sites H1/H10 (blocked by F1); graphics pass (`removed 1`, no Physics) |
| 6 | open the settings page: Settings › Account "MCVS Offline" (`auto`); then the inbox item; then the page's "Open in the system browser"; then set `admin.openVia` to `system`, then `game`, restart each time. Then with `auto`: once in fullscreen and once in windowed mode; once with Steam › Settings › In Game › "Enable the Steam Overlay while in-game" turned off (restart the game) | for each: the route in the `admin: page opened … via` line and whether the page appeared with its session (Status loads). Expected: overlay shows the page in both modes; with the overlay off, `via system` and the browser opens; `game` works after login. If `via overlay` is ever logged with nothing visible, the next click must log `via system … overlay showed no page last time`; if that happens in a normal setup, Q3's bad branch applies | **Q3**, **Q10**, R17 | inbox item pass (overlay, twice, also via the code `MCVSOFFLINE`); Account › MCVS Offline blocked (F1); the rest not tested |
| 7 | on the page: Save the game now; export a save (Explorer opens `McvsOffline\exports`); share logs; copy a text. Then once more with `admin.openVia=system` and the game **minimised** behind the browser: Save the game now | each works; whether Save now completes while minimised (the game thread keeps running, [CR] F3 [I]) | F3 | not tested |
| 8 | one bot match to the end **through Battle › Classic multiplayer** (there is no visible bot-match tile: the PRACTICE tile is hidden in the shipped data), `watch` running, before any unlock code | match ran with bots and physics (no falling through floors), rewards saved, `headless world:` lines (none expected), the watchdog, `[pc] quicklaunch: automatch through the local Finder` (1 or more), the GS World and P3 lines | **Q4** (GS), **Q12** | not run (blocked by F1): two stock client-World matches ran, rewards saved; `automatch through the local Finder 0` |
| 8a | after step 8, before any code: open the inbox; if WR2's old coupon messages are still there (UNLOCKPAID, UNLOCKCAMOS, UNLOCKPAIDCAMOS, 2026-10-29, kept 7 days), select one, stay 30 s, Claim | open time, rewards shown (30), any freeze while open or on Claim, the popup's count (30), afterwards every camo or jersey of that code owned and the message gone | the `inbox` patch's rescue on PC (its PC-only fingerprints: `RefreshUI`, the `ClaimRewardsForMessage` state machine); then 8b's code on items the claim gave (keys not in the ledger, written and claimed again: duplicate handling unverified, [protocol/unlocks.md](../protocol/unlocks.md)) | (WR2: the inbox froze for 180 s with these messages) |
| 8b | after step 8: redeem one big unlock code (time it), then open the inbox; then the unlock-all grant once | the time until the game answers again, any `[watchdog] STALL` line (and the dump `watch` takes at 60 s), whether the inbox opens without a crash (the `store` fix, WR2 crashes 1-2) | **Q16**, F4 | (WR2: 70 STALL reports and two inbox crashes, all with the stock Game.dll) |
| 9 | during the match: minimise and restore; open the in-game menu; open the Steam overlay (Shift+Tab) | one pause, cursor visible in the menu and on the end screen | H11, M2, H8 | not tested |
| 10 | in options: toggle fullscreen twice | no crash, the setting is kept | H8, [HL-V] §5 | not tested |
| 11 | firewall prompts at boot or at the match; `watch`'s endpoint lists | prompts (when, which), listeners: VDB `0.0.0.0:25001+` (one per physics World), GS UDP `[::]`, Finder 51750 | **Q5** | partial: listeners seen (VDB `0.0.0.0:25001+`, Finder 51750); no GS socket (no GS match); no prompt data |
| 12 | `watch` telemetry in the menu and through a whole match, once at the in-game Graphics quality Low and once at High; the `memory:` part of every `[status]` line (F9). Then `graphics.profile` = `high` on the page, restart, once without and once after an in-game quality choice | private bytes and virtual size, % of 2048 MB, peak per match; the `[boot] graphics:` profile and the `in-game options` line each time | **Q6**, **Q18** | partial: menu LowEnd peak virtual 1239 MB; 1727 MB (84 %) during a 180 s stall with 3 Worlds; no match or HighEnd sample |
| 13 | with the defaults (`nativeonline` applied, `embedded.nativeOnlineSdk: false`): open the in-game store, buy one item with soft currency, and open every xpromo/news screen the menu offers; then `server.log`; then once with `embedded.nativeOnlineSdk: true` | store and xpromo screens: any hang, error popup or crash; `server.log`: no `GlWebTools` request, no `connectionstatus`; with the SDK on: the tracker posts get 200 and one INFO line each, no MAC anywhere in the logs | **Q8**, R11 | partial: no `GlWebTools`/`connectionstatus` with the defaults; store, xpromo and SDK-on not tested |
| 14 | Steam achievements: open one crate (or note the first-login-of-the-day progress) and compare the stat on the Steam overlay's achievements page before and after | the progress moved by exactly one step | D3 (H9 + M3) | not tested |
| 15 | Settings page › Restart; then once more with `admin.restartVia=host` | each time the game closes and comes back through Steam within ~10 s; `game.log` present; the log names the exit path (`ExitAppAndroid`, or the host's kill) | **Q9**, **Q14** | partial: `game.log` present (bridge attaches); Restart never used |
| 16 | `collect`, then `uninstall`, then `status --listing` | the zip; the listing **after**, diffed against step 1's by the kit (`listing-diff.txt`): expected differences only `McvsOffline\` (with the data) and the game's own files (`dr0.bin`..`dr3.bin`, `OF_PP.bin`, `GameVersion`, `core.dmp` "the game's own crash dump" with its size and UTC time, or any other entry whose time is a game-run time, each named); no `args.txt`, no config, no `*.mcvs-tmp`. Anything else is a leftover of ours; `summary.txt` and the `pc-kit.log` headers name the kit's SHA-256 | F9, P1 | partial: `collect` only; no `uninstall`, no listing after |
| 17 | optional: `uninstall --remove-data --yes` on a copy of the game folder (or after the save export) | no `McvsOffline\` left; the kit log continued in `%TEMP%\McvsOffline\kit\`; no `%TEMP%\mcvs-pckit-*` folder left | §7.6 | not tested |
| 18 | optional: Steam "Verify integrity of game files" while installed, then `status` | still "installed, current" | [PP] §5.3 [I] | not tested |
| 19 | owner question: had this Steam account played MCVS on PC before (the `SACHI1` marker)? | yes/no | **Q11** | not answered |

The owner sends back the `collect` zip, the `watch` folder, the two listings, the dumps `collect` lists in `dumps.txt`
and the `AppCrash_ModernCombatVers_*` report folders; WP5b records the results in §14 and STATUS. Expected values (filled
in from §12.6 before the kit is handed over): the `written` SHA-256 of the five DLLs and the config (§13.1); the
`MEM rule on, removed <n>` count.

### 13.1 Expected WR3 values (build 18)

From the §12.6 run on Linux (`McvsOffline.Patcher pc install` on a temp copy of the read-only Steam files, with the PC
server DLL the kit embeds, byte-identical; `RealFolderEndToEnd` passed in the same run), default patch set, port 51780.
A second install gave the same bytes, and the values do not depend on the commit they are built at
(`Directory.Build.props` turns SourceLink off). The owner's copy is [PLAYER-SETUP-PC.md](../PLAYER-SETUP-PC.md) §7.

| written | sha256 | mvid |
|---|---|---|
| `McvsOffline/bin/EngineBridge.dll` | `956e2a53c2a3102cda4d230ff05e89ccd21dabed5d51d0b99e9418c2f38819c7` | `eb88ef0f-3bf3-48fc-8c6e-c53855bdfddf` |
| `McvsOffline/bin/Game.dll` | `7cd097ae53feefe6bd66df1f3463ad58f01fb58adf671fc7370c95747c00f798` | `35762546-a2f0-4ee6-a386-5629fad6a0f9` |
| `McvsOffline/bin/Gameloft.Engine.dll` | `81b4a794013b3f807d629b227b1b86504e9cfe5f029aa0671da30632b6e88f41` | `75836edd-9c53-43d4-9f14-74b375a5af8b` |
| `McvsOffline/bin/Gameloft.Util.dll` | `a7a1cd0ed73ef631d107fa3232b959d2480d1450a990b3283271c56570e38eba` | `c4cebe44-664d-4dd8-8d7f-ec67b3193f14` |
| `McvsOffline/bin/McvsOffline.Server.dll` | `a6e3991c05d7a6704fe942e9af8477c1aed10e56c336ed666298df41acfcecb4` | `c7b02878-6f38-431b-affd-4e70cedaa2a5` |
| `ModernCombatVersus.exe.config` | `3ebe99b3c93a9540cd7f7b03da55fc048a544501c17e8621457884eae456f94c` | — |

* Applied patches: `hook-pc, headless, bottile, store, signatures, inbox, adminpage, headless-engine, physics,
  quicklaunch, headless-diag, nativeonline, waits, adminlink, graphics, gameload, geo` (build 17's set); `install.json`
  `installer.serverBuild` is `18`. Build 18 changes only the server DLL (the host: `GameReflection` binds lazily from
  the loaded assemblies, `LoadedTypes`, the load stack, §8.9); no patch changed, so `Game.dll`, EngineBridge,
  Gameloft.Engine and Gameloft.Util get new MVIDs and hashes with the server DLL only, as in every build. The config is
  build 16's. Build 17's values are in §13.2.
* `MEM rule on, removed 1` (`GameOptionsWindowsTests.RealFileRemoved`, the real `GameOptions_windows.json`).
* The config is §7.4's codeBase-only text (UTF-8 without BOM, CRLF, final CRLF; five `<dependentAssembly>` entries, no
  `<qualifyAssembly>`), byte for byte the config WR2 ran (§13.2; `PcExeConfigTests.ExpectedSha256`). Builds 14 and 15
  wrote the five `<qualifyAssembly>` entries after them (`fda12aef…c4b6`, §13.2).
* **Upgrade from build 17** (the same Linux run): a build-17 install made by a `b20d12b` patcher (built in a separate
  worktree: its server DLL `a40e28c6…00ef` differs from build 17's kit's `32019567…2675` only as a build of another
  folder; config `3ebe99b3…f94c`, `serverBuild` `17`). The build-18 kit (its Linux build, `McvsOffline.PcKit.dll` with
  the same embedded server DLL) says in `status` `installed, OLDER BUILD - OLDER BUILD (17; this kit installs build 18):
  run install to update it (your saves and settings stay)` with the problem line, and `verify` fails with exit 1 on the
  same sentence (§7.6; the patcher CLI, which does not compare, still says `installed, current`). Build 18's `install`
  over it exits 0 and writes exactly the bytes of the table above (`serverBuild` `18`); then the kit's and the patcher's
  `verify` and `status` say `installed, current`, and `uninstall` restores the exact listing and every SHA-256 of the
  copy.
* The kit: `McvsOffline.PcKit.exe`, SHA-256 `186c1bcb21b46aea633aea888fc6eae531bcdcad73d5027ab660269cd117b28e`; its
  publish check passed on the embedded DLL (every PC hook (17), `Gameloft.Logs`, only allowed references), and the
  kit's assembly carries the server DLL above byte for byte.
* ILVerify of the four patched assemblies against the net452 refs reports exactly the findings of the stock files
  (EngineBridge 0, Game 1, Gameloft.Engine 290, Gameloft.Util 3; none in a patched or added method); the server DLL
  verifies clean. `verify` and `status`: `installed, current`. `uninstall` restored the exact file listing and every
  SHA-256 of the copy; with a bootstrap `args.txt` block and player data added before it, only `McvsOffline\` with that
  data remained and `args.txt` was gone. With the first 20 lines of WR2's own `bootstrap.log` as the last start after
  the install, `status` said `installed, NOT ACTIVE (Game loaded from the game folder; the 'store' patch is missing)`
  with its `ERROR:` line (F2); with the first 22 lines of the owner's build-17 start (2026-10-30, `gameload yes`),
  `installed, NOT ACTIVE (Game loaded from the game folder; the 'store' patch is missing; the 'inbox' patch is
  missing)`.
* Android (§12.6): the APK `mcvs-offline-9e21ead.apk` (SHA-256
  `5981f1098151f61a8a3d9204514e2351a510fe9f70490521f0c6354c861023b8`; cert SHA-256 `fc0db84e…7d16`, targetSdk 35,
  both ABIs, `isGame` and `appCategory=0`, zipalign OK), compared entry by entry with build 17's
  `mcvs-offline-a8742ab.apk` (same key and options): same 952 entries in the same order; changed only
  `assemblies/McvsOffline.Server.dll` (2,558,976 → 2,562,560 bytes), `META-INF/1.RSA`, `1.SF`, `MANIFEST.MF`, and
  `Game.dll`, `Gameloft.Engine.dll`, `Gameloft.Util.dll`, `NicePlaySDK.dll` in their 16 MVID bytes only.

### 13.2 WR2 values (build 13, archived)

WR2 installed exactly these bytes (`R/install.json`): EngineBridge `1d669508…2e04`, Game `f511c72b…e278`, Gameloft.Engine
`5c21dc66…abf9`, Gameloft.Util `3d4926d3…abbd`, McvsOffline.Server `6303a789…3dd5`, config `3ebe99b3…f94c`; kit
`c608fd19…715d` (build 13, `f158cb9`, APK `mcvs-offline-f158cb9.apk`). The MVIDs and the earlier builds' values are
in this file's history. Build 14 (`0b90763`, the first WR3 kit): EngineBridge `81fb59b7…1e9c`,
Game `9cdd0853…e28d`, Gameloft.Engine `a203bb06…63ecb`, Gameloft.Util `af4c5217…f068`, McvsOffline.Server
`6153643a…3a34`, config `fda12aef…c4b6` (unchanged in build 15; build 16 is back to `3ebe99b3…f94c`); kit `0aa37f24…a7be`.
Build 15 (`feb1684`, the second WR3 kit, never run on Windows): EngineBridge `4cffc829…f004e`, Game `26fac2e9…bc64c`,
Gameloft.Engine `c033977a…056c6`, Gameloft.Util `4d59932c…32b12`, McvsOffline.Server `8a6a1628…499b8c`, config
`fda12aef…c4b6` (with the five `<qualifyAssembly>` entries); kit `34131809…1baabb`; APK `mcvs-offline-1a70a5b.apk`.
Build 16 (`c2b5076`, the third WR3 kit, PC only, no APK): EngineBridge `4f5e3894…e32c`, Game `7c99c1d7…785a`,
Gameloft.Engine `f61e59e5…7d49`, Gameloft.Util `cf613f2c…dc64`, McvsOffline.Server `ed04f15f…e5f1`, config
`3ebe99b3…f94c` (the WR2 shape again); kit `06235ac9…ae41`.
Build 17 first cut (`96801b4`, replaced by the review fixes before any Windows run): EngineBridge `5c2dd7e2…af13`,
Game `39b80ecb…1618`, Gameloft.Engine `c4d37967…e5d3`, Gameloft.Util `40c1792b…99ba`, McvsOffline.Server
`3fe6146a…6478`, config `3ebe99b3…f94c`; kit `ba8a977f…50e5`; APK `mcvs-offline-80378cf.apk` (`b6bcb82b…2219`).
Build 17 (`a8742ab`, the fourth WR3 kit; on the owner's PC `Game` came from the game folder, §8.9): EngineBridge
`14e2a779…352e`, Game `14d887a3…b4a3`, Gameloft.Engine `9af0c02c…abf4`, Gameloft.Util `4fa196fc…7277`,
McvsOffline.Server `32019567…2675`, config `3ebe99b3…f94c`; kit `804dd546…3ec2`; APK `mcvs-offline-a8742ab.apk`
(`d4d332f1…16a4`).

---------------------------------------------------------------------------------------------------

## 14. Open questions and how they are settled

| # | question | settled by | if the answer is bad | status after WR2 (2026-10-29) |
|---|---|---|---|---|
| Q1 | Do `Game`, `Gameloft.Engine`, `Gameloft.Util`, `McvsOffline.Server` bind through their `<codeBase>` (chained Load-context binds)? Expected yes ([PP-V] §8) | WR2 4: **no for `Game`** (bare-name load, 15/15 starts from the game folder), yes for the others. Fixed by `gameload` (§8.9) and `<qualifyAssembly>` (§7.4); WR3 step 4 re-checks (`assemblies: Game …\McvsOffline\bin\Game.dll`, `gameload yes`, `store yes`) | option B (in place with backups) as a new installer mode; R3 | **Answered: no for `Game`** (15/15 starts from the game folder: a bare-name `Assembly.Load` gets no config policy), yes for `Gameloft.Engine`, `Gameloft.Util`, `McvsOffline.Server` and `EngineBridge`. Fixed in build 14 (`gameload`, `<qualifyAssembly>`), not option B. Build 14's kit then failed on the owner's PC (no server; copying bin next to the exe "fixed" it): the `<qualifyAssembly>` entries made .NET discard the whole config (Q15; confirmed by the owner's A/B test). Build 16: `gameload` alone, the WR2 config shape. Build 17 on the owner's PC (config in effect, `gameload yes`): `Game` still from the game folder, at +0.187 s on the host's start thread; the load was almost certainly our own `GameReflection.Load` at boot (`Type.GetType("Game.PlayerProfileComponent, Game")`, a partial name), before the engine asked (§8.9: code reading and the timing of both logs; build 18's load stack is there to confirm it). The same call ran in WR2, so WR2's "bare-name load" finding was most likely our load too, and `gameload` has never been tested. Build 18 never loads a game assembly from our code (`LoadedTypes`) and logs the loader's stack for a `GAME FOLDER` load. **Open** until a start shows `Game …\McvsOffline\bin\Game.dll` (`store yes`, `inbox yes`); if not, the `[boot] load stack:` lines name the loader, and `Game (no file) (elsewhere)` means the `gameload` bind failed (§8.9) |
| Q2 | Does http.sys accept `http://localhost:51750/` for a standard user? [I] yes ([CR] §1 row 7) | WR2/WR3 2, and step 5's log | D4: WP6 (§15) | **Settled: yes** (probe as a normal user; the Finder on 51750 in every start). WP6 not needed |
| Q3 | Overlay: `IsOverlayEnabled`, loopback, fragment and `HttpOnly; SameSite=Strict` cookie in the overlay's CEF, in fullscreen and windowed mode | WR2/WR3 6 | `auto` already falls back (overlay disabled, or no page last time, §6.5); make `system` the default | **Partly settled, good branch**: page and session via the overlay twice (window mode unknown). Open: fullscreen vs windowed, overlay off (`via system`), the no-page fallback. New: overlay tabs survive a restart (F8, stale-tab message) |
| Q4 | First headless failure on the Windows engine (R1), with every §8.4/§8.5 site | WR2/WR3 5, 8 | new sites in WP4's table; if the engine refuses a second World at all, R1's fallback is a new design | **TS: no new Engine-side site** (35 reports, all at the stock Game sites H1/H10, blocked by Q1); **GS: open**. WR3 steps 5 and 8 (expect 0 reports) |
| Q5 | Firewall prompt for the GS socket on `[::]` and the VDB listeners | WR2/WR3 11 | docs only (loopback play works if declined) | **Open**: VDB `0.0.0.0:25001+` seen, no GS socket, no prompt data. WR3 step 11 (probe lists the firewall rules) |
| Q6 | Memory with three Worlds in 2 GB; selected GPU/MEM profiles | WR2/WR3 5, 12 | layer exclusions for headless Worlds only (never `Physics`) | **Partly settled**: auto = LowGPU/LowEnd on the owner's PC; the in-game High gives HighGPU/HighEnd; LowEnd with 3 Worlds peaked at 1727 MB virtual (84 %) during a busy stall. HighEnd and in-match unmeasured. WR3 step 12; §17 R2 |
| Q7 | The lazy probe sees `Game.McvsStoreSafety` | WR2/WR3 4 | — | **Mechanism works** (`store NO` reported correctly at Game load); the positive close waits for Q1 (WR3 step 4) |
| Q8 | Tracker and native Gaia traffic gone with `nativeonline` ([CN-V] §4); and does anything PC-specific (Steam IAP, the store, xpromo/news screens) break without native online init ([CR] §3.2 Q8) | WR2/WR3 13 | traffic: keep the route (it is shared code anyway); store or xpromo broken: `nativeonline` off by default on PC (`PatchPlan` default set) | **Traffic half settled** (0 `GlWebTools`, 0 `connectionstatus`). Store purchase, xpromo screens and SDK-on open: WR3 step 13 |
| Q9 | Relaunch: helper survives the exit (job objects), `steam://rungameid/723780` 2 s after `Wait-Process` starts the game, for both `admin.restartVia` values | WR2/WR3 15 | `CREATE_BREAKAWAY_FROM_JOB` helper; another delay; `RelaunchUnconfirmed` stays true | **Open** (Restart never used). WR3 step 15 |
| Q10 | The PC Flash Account menu shows and routes `mcvs_offline` (PC list differs, [PP] §8.6) | WR2/WR3 6 | drop `adminpage` from the PC defaults; the inbox item and the forum button still reach the page | **Open, blocked by Q1** (`adminpage` is in Game.dll). Do not drop `adminpage` on WR2's evidence. WR3 step 6 |
| Q11 | Prior `SACHI1` on the owner's account (first boot runs `ResetStats(true)`, [CR] F8) | WR2/WR3 19 | docs | **Open** (owner question). WR3 step 19 |
| Q12 | The TS automatch reaches the in-process Finder (P3 + the default host) | WR2/WR3 8 | a data-free alternative: set `MatchmakingComponent.Host/Port` from a hook at TS-World start ([QL-V] §8) | **Open, blocked by Q1** (the match path needs our Game.dll; `bottile`, also in Game.dll, only matters for the hidden PRACTICE tile; `automatch through the local Finder 0`). WR3 step 8 |
| Q13 | `ContinueWith` on the game's `SynchronizationContext` keeps LocalAnubis's continuations on the game thread ([CR] F6 [I]) | WR2/WR3 5 (thread ids in the log) | run the `Start` call through `GameApplication.Instance.Context.Post` | **Settled: yes** (`[pc]` thread id = the watchdog's game thread in 15/15 starts) |
| Q14 | `GameLogBridge` attaches on PC (the `ENABLE_LOGGER`/Fatal-only story is the same code, [CR] F4) | WR2/WR3 15 (`logs/game.log`) | `gameLog` off on PC | **Attach settled** (`game.log` written in every start); the exit path untested (WR3 step 15). New: `gameLog.levels` had no effect on PC (F5, fixed in build 14) |
| Q15 | Does `<qualifyAssembly>` apply to an `AssemblyName` with only `Name` set (the engine's load)? The `gameload` IL patch does not depend on it | WR3 4 | `gameload` alone carries the fix | **Answered: worse than "does not apply"; confirmed on the owner's Windows PC** (deleting only the five `<qualifyAssembly>` lines of build 14's installed config made the server start). With a non-strong-named `fullName` (`PublicKeyToken=null`) fusion fails the parse of the whole config and keeps none of it, codeBases included (SSCLI `nodefact.cpp:975-984`, `policy.cpp:374-391`; §7.4), which matches build 14's "server not running, copying bin next to the exe fixes it", and the owner's A/B test confirms it. Build 16 writes no `<qualifyAssembly>` (the WR2 shape, `3ebe99b3…f94c`); `gameload` qualifies the Game load on its own (by the same SSCLI reading a full name with the null token is a full reference; expected, not proven: the managed `AssemblyName` path through the VM's `AssemblySpec` is not in SSCLI, and `gameload` has never run with a config in effect). WR3 step 4 confirms (`Game` from `McvsOffline\bin`) or refutes the gameload part. Build 17's start most likely did not test it: our own `GameReflection.Load` loaded the stock `Game` by a partial name before the engine's `GetAssembly` ran (§8.9, Q1); the first start of build 18, which loads nothing itself, is the first test of `gameload` |
| Q16 | Do the busy game-thread stalls of 60-180 s after big unlock codes and the unlock-all grant persist with `signatures` loaded? | WR3 8b (timed; the kit's dump at 60 s) | analyse the dump; coalesce the unlock-all `set_profile` alerts (`UnlockGranter`) | new after WR2 |
| Q17 | The native cause of the WR2 access violation in `ASValue.SetAsDictionary` (at HighGPU/HighEnd, during the unlock-all grant; `core.dmp` was 0 bytes) | a WER LocalDumps or kit dump of the next one (WR3 setup) | depends on the dump | new after WR2 |
| Q18 | Does `graphics.profile` survive an in-game Graphics quality choice (`LocalStorage` `Performance`)? | WR3 12 | document the in-game menu as the only graphics control on PC | new after WR2 |

Settled already and not reopened: unknowns 4, 5, 6, 10, 11 ([CR] §1); the owner decisions D1-D5.

---------------------------------------------------------------------------------------------------

## 15. WP6 (optional, only if WR2 Q2 fails): our server replaces the Finder routes (D4 b)

Not implemented unless the http.sys probe or the first boot shows `HttpListenerException` (access denied) for a normal
user. Sketch, so WR2 can start it at once ([CR] Q2, [QL] §7.2 variant 2b):

* Setting `pc.finder`: `httpsys` (default, §8.3) or `server`.
* With `server`, `PcHooks.QuickLaunch` does what `LocalAnubisManager.Start` does minus the Finder: open the super session
  (`new Federation(...).OpenSession(SuperCredential, SuperPassword, DeviceId, null, ["auth", "auth_admin_ro"], ct)`), set
  `LocalAnubisManager.Session` and `Lobby` (public static setters, [CR] F6), `Lobby.Start()`; then quick_launch with
  host `127.0.0.1`, port `settings.port` (our server). `DefaultAutomatchHost` tests `Lobby?.Started` instead of `Started`.
* A host feature `ILocalLobby` (in `hosts/embedded/Windows/`, registered in `context.Features`) runs, posted to the game
  thread, the Finder's own logic for `POST rooms/{cid}/quick_launch`, `POST rooms/{cid}/automatch/{mode}`, `GET locate`:
  parse the user (`auth/verify` of the caller's token through the super session), `GameConfig.FindGame` (lobbyconfigs),
  `Lobby.QuickLaunch`/`Automatch`, and returns the room JSON as the Finder would (`FinderHTTPMessageHandler.cs:28-80,
  116-128`). `MatchmakerService` (today 404 `NotEmbedded`, `MatchmakerService.cs:37, 61-66`) delegates those routes to
  `ILocalLobby` when it is registered.
* Tests: the same look-alike engine as `PcHooksTests`, plus HTTP tests of the delegated routes. Cost estimate: 2-3 days.

---------------------------------------------------------------------------------------------------

## 16. Docs to write (WP5b unless marked; WP5a writes the PLAYER-SETUP-PC draft for WR2)

* **`docs/PLAYER-SETUP-PC.md`** (new): requirements (Windows 10/11, Steam running and logged in, the game installed;
  Steam's offline mode for play without internet; .NET Framework 4.5.2+ is installed by Steam's first launch); getting
  the kit (SmartScreen, antivirus); `probe`, `install`, start from Steam; what changes in the game folder (§3.5) and
  that **uninstalling the game in Steam deletes `McvsOffline\` with the saves: export first** (Settings page › Saves ›
  Export); the settings page (overlay by default, the browser fallback, the page's "Open in the system browser", and
  that a click that showed nothing in the overlay opens the browser on the next click; `admin.openVia`); Steam achievements (D3: real
  achievements and stats unlock from offline progress, including anything the unlock tools grant; a first run on an
  account that never played on PC resets this game's Steam stats, stock behaviour); firewall (the game's own Havok debug
  listeners on `0.0.0.0:25001` and up, one per physics World, and the match's UDP socket; allowing or declining both
  work for single-player); do not start the game under a debugger; close capture tools if the game misbehaves; Steam
  updates and "Verify integrity" (`status` says when to reinstall); `uninstall` keeps the data; troubleshooting with
  `collect`; where the logs are (`McvsOffline\logs`, fallback `%TEMP%\McvsOffline\logs`, `DebugView` for
  `OutputDebugString`); LAN is not supported on PC; the game itself keeps Gameloft device-id files in Documents
  (`GDID.bin`, `DC.bin`, `HDIDFV.bin`) and data under `%APPDATA%`/`%LOCALAPPDATA%\Gameloft` (stock behaviour, not ours:
  neither our `uninstall` nor Steam's removes them, §3.5); a `*_args.txt` in the game folder makes the game ignore
  MCVS Offline (`install` refuses, §7.6).
* `docs/STATUS.md`: a PC section (build, what is implemented, the WR2 checklist §13 with a results column), the WP-G phone checks, and the side effects of the `game` flags listed in §10.
* `README.md`: status row "PC (Steam)"; a quickstart line for the kit.
* `docs/ARCHITECTURE.md`: §2 the PC mode (option A, `hook-pc`, the PC patch set), §2.1 the define scheme, the `game`
  manifest patch (and that `targetsdk` no longer adds `appCategory`).
* `docs/MODULES.md` §4: the define and guard rules of §3.1 (replacing "Android-only code goes behind `#if MCVS_EMBEDDED`").
* `docs/ROADMAP.md`, `docs/PC-SPIKE.md` (the spike is superseded by the kit), `docs/research/pc-port.md` (a header note:
  decisions and corrections in this design, §17), `docs/design/settings-page.md` §8.10 (the Steam opener and the PC
  `admin.openVia` values).
* WP1: `docs/protocol/misc.md` (the tracker route), `docs/protocol/content.md` (`end_date`). WP-G: `docs/PLAYER-SETUP.md`,
  `docs/research/target-sdk.md` (§10).

---------------------------------------------------------------------------------------------------

## 17. Risks

| id | risk | likelihood / impact | mitigation |
|---|---|---|---|
| R1 | The Windows native engine cannot run headless TS/GS Worlds in the client process (never exercised) | medium / **blocking** | M1/M4 diagnostics make the first failure visible (WR2 Q4); fallback: a second game process as a local server, a new design |
| R2 | 2 GB address space (no LAA): client + TS + GS + our server; peak virtual 939 MB at boot without the menu ([CR] §1 row 3); WR2: 1727 MB (84 %) with three Worlds during a busy stall at LowEnd | medium / high | WR2/WR3 Q6; never exclude `Physics`; the LAA bit on the Steam exe is not an option (P1). After WR2: an opt-in LAA **copy** in `McvsOffline\bin\` is the recommended next step if WR3 confirms the pressure (note below) |
| R3 | The chained `<codeBase>` binds do not apply | low / high | WR2 Q1; option B mode |
| R4 | More PC-only headless sites than §8.4 | medium / high | WR2 Q4; H3/M1/M4 keep failures non-fatal and named |
| R5 | Steam stats from offline play (D3): double counting fixed by H9 + M3; the economy and unlock tools also reach the real account; first-run `ResetStats` | medium / medium | D3 accepted; documented; WR2 14 |
| R6 | http.sys refuses the Finder prefix for a normal user | low-medium / blocking for matches | WR2 Q2; WP6 (no admin step) |
| R7 | Firewall prompts: the GS ENet socket binds `[::]` (verified), the Havok VDB listens on `0.0.0.0:25001+` (verified, [QL-V] §6); WP0 saw no prompt | medium / low | docs; loopback play works either way; no native patch (P1) |
| R8 | A system proxy intercepts loopback Federation calls | low / medium | if seen: a `<defaultProxy><bypasslist>` in the config (installer change) |
| R9 | Antivirus flags the kit or the patched DLLs; the kit on Windows (4.8 framework folder as BCL) writes other bytes than the Linux run tested | low-medium / medium | source published, deterministic outputs (WR2 step 3 compares the `written` hashes with the Linux run, §12.6), no Steam file changed |
| R10 | The 10 changed OnlinePlatform methods | retired ([CN] §4, [CN-V] §7) | the harness on the PC DLLs (§4.7) |
| R11 | `nativeonline` behaviour on PC (Steam IAP, xpromo without native online init) | unknown / medium | the native SDK stays off by default as on Android; WR2 step 13 exercises the store and xpromo screens (Q8); if broken, the `nativeonline` patch leaves the PC defaults |
| R12 | Uninstalling the game in Steam deletes the saves (D1) | certain when it happens / high | docs, the kit's install message, the page's export |
| R13 | Steam update makes the copies stale (none since 2021) | very low / high | `status` and the boot check (§6.2) detect it |
| R14 | Starting under a debugger or a tool that sets `BeingDebugged` → silent dead window | low / medium | docs |
| R15 | Anything that changes the client id or version → silent exit at boot (`GameApplication.cs:1190-1193`) | low / high | we never pass `-ClientId`; documented invariant |
| R16 | The relaunch helper dies with the game or Steam refuses the quick relaunch | medium / low | `RelaunchUnconfirmed` true; WR2 Q9; manual start |
| R17 | Overlay not available (disabled, not hooked in this graphics mode) or shows no page | medium / low | `auto` falls back to the system browser when the overlay is disabled and on the next click after an overlay open that showed no page (§6.5); the page's "Open in the system browser"; WR2 step 6 runs fullscreen, windowed and overlay-off |
| R18 | Continuations of the quicklaunch hook leave the game thread (no synchronization context) | low / medium | WARN line, WR2 Q13, `Context.Post` alternative |
| R19 | Popuplib's Internet Explorer helper blocks its caller while IE runs (stock popups only) | low / low | not on our path; noted for support ([WH-V] "other" 2) |
| R20 | A config shape real .NET Framework has not read: build 14's `<qualifyAssembly … PublicKeyToken=null>` entries most likely made fusion ignore the whole config (§7.4, Q15), and nothing before WR3 could have caught it (the tests read the file with `XmlDocument`, not fusion) | happened (build 14) / blocking | build 16 writes only the shape WR2 ran (`3ebe99b3…f94c`, pinned by a test); hook-pc's `%TEMP%\McvsOffline\hook.txt` marker and status's NOT VERIFIED / NOT ACTIVE (§7.6) make a config Windows did not use visible at the first start; the build-14 work-around (bin copied over the game's files) is named by status in every state (STALE, or DAMAGED with the old config) and refused by install and uninstall until Steam's Verify; a `$type` through Newtonsoft.Json's `LoadWithPartialName` naming one of our assemblies stays a partial bind (appbase only), not seen in WR2's logs; a Windows self-test of the config (a probe exe whose config `PcExeConfig.Build` writes) is the follow-up |

**Memory after WR2 (R2, recommendation, not implemented in build 14).** WR2's peak of 1727 MB virtual (84 % of the
2048 MB a 32-bit process without LAA gets) came with three Worlds during a 180 s stall at LowEnd; a match and the
in-game High were never measured, and nothing in that peak is attributed yet. Trimming our Worlds (headless layer
exclusions, mip caps, fewer thread stacks, coalesced unlock alerts) is worth tens to perhaps a few hundred MB and must be
measured first. The lever that changes the ceiling is an **LAA-flagged copy of the exe** in `McvsOffline\bin\`,
started by the Steam launch option `"<game>\McvsOffline\bin\ModernCombatVersus.exe" %command%`: 4096 MB, so the WR2
peak would be 42 %. P1 holds (the Steam exe is untouched; the copy is ours and uninstall removes it), but the copy needs
the stock managed and native DLLs next to it, a `rootpath` IL patch (`GameApplication.FindRootPath` would otherwise
return `bin\`), the working directory set to the game root, and a stale-copy check. Its main risk is that Gameloft's
native engine is not LAA-safe (pointer sign bugs above 2 GB, late in a match), so it would ship opt-in and be tested
with top-down allocation (`AllocationPreference`) through a High match before any default. Decide after WR3's step 12
numbers; until then R2 stays as above.

Corrections to `docs/research/pc-port.md` that this design carries ([CR] §6): 17 patches (with `graphics`, broken on PC
as is); the PC crasher is `GameClientComponent.OnCreate` (H10), and H8, H9 + M3, H11, M2 are added; the native
`OpenUrl` is the Steam overlay after login, not Internet Explorer; nothing starts the Finder on PC until P2; the define
scheme is `MCVS_EMBEDDED` + `MCVS_ANDROID` + `MCVS_WINDOWS` (not `MCVS_EMBEDDED_WIN`); the clipboard is user32
(`GameManager.SetClipboard` is dead); PC reads `GameOptions_windows.json` with every threading option off, and the
physics risk is MEM `LowEnd`; WP0's "login and menu work" was wrong (the menu needs P1 + P2); R5 is medium/medium, R7
has a verified any-address bind and debug listener, R10 is retired; the data root is the game folder (D1), not
`%LOCALAPPDATA%`.

---------------------------------------------------------------------------------------------------

## Appendix A. Review log

One review round (feasibility, completeness and tests lenses), October 2026. Every finding was checked against the code
and the evidence; 39 applied, 0 rejected. Where the applied fix differs from the reviewer's, the column says how.

| # | finding (section) | verdict | where |
|---|---|---|---|
| 1 | PC csproj excludes `Install/`/`Hd/` but shared files still use them (`AdminHost.cs:224-225`, `EmbeddedHost.cs:12/:308`, `IInstallerPlatform`); t7 did not cover this layout | applied: the complete `#if !MCVS_WINDOWS` site list, `IInstallerPlatform` guarded, the D0-b build check | §3.1, §5.1, §5.2, §11.2, §12.1 |
| 2 | `FileSystem`/`SocketReuse` guards leave unguarded callers (would not compile; would drop the Linux directory fsync) | applied as proposed (`open/fsync/close` and `FlushDirectory` under `MCVS_ANDROID \|\| !MCVS_EMBEDDED`, method bodies of `SocketReuse` guarded with `#else` results) | §3.1, §4.6 |
| 3 | `IsOverlayEnabled`/`IsSteamRunning` return a C++ bool in `al`; default marshalling reads 4 bytes | applied: exact declarations (cdecl, `U1`, ANSI, `BestFitMapping` off); the Cecil assertion lives in `PcPInvokeTests` | §6.5, §6.4, §12.2 |
| 4 | `SystemBrowser.Open` blocks the game thread (ShellExecute on an STA helper thread that is joined) | applied: ShellExecute on its own background STA thread, `"system"` returned at once, failures logged | §6.3, §6.5, §12.2 |
| 5 | `OpenClipboard(NULL)` + `EmptyClipboard` makes `SetClipboardData` fail per Win32 | applied: owner = the game's main window, fallback a message-only window; return checked; `HGLOBAL` freed only on failure | §6.3, §12.2 |
| 6 | `ThreadDumpStub.cs` contradicts the `Windows/` folder guard rule | applied: the stub moves to `Diagnostics/ThreadDumpStub.cs` (`#if MCVS_WINDOWS`); the folder rule has no exception | §3.1, §5.2, §6.3, §11.2 |
| 7 | `BaseAssemblyResolver` falls back to TPA/GAC after its directories | applied: `PcAssemblyResolver` implements `IAssemblyResolver` itself; `System.Runtime` must throw | §7.3, §12.3 |
| 8 | WR2 step 16 ignores the game's own `dr0..3.bin` | applied, with `OF_PP.bin` (also game-time in the WP0 listing) and a before/after listing diff instead of "the depot listing" | §1.3, §3.5, §7.6, §13 |
| 9 | Witness names used by WP2/WP3 are not Day-0 | applied: §3.9 table (with `graphics`), referenced from §6.2, §7.6, §8; a test keeps the three copies equal | §3.8, §3.9, §12.4 |
| 10 | `install.json` format is not Day-0 | applied: §3.10 fixes the keys other readers may use | §3.10, §6.2, §7.5 |
| 11 | WP5 and WR2 depend on each other; WP6 has no Windows round | applied: WP5a (before WR2) / WP5b (after), WR3 when WP6 runs | §1.1, §11.1, §11.3, §11.4, §13, §16 |
| 12 | Moving `PatchAssembly` breaks two test files; `log` parameter dropped; the stand-in server has no owner | applied: forwarder kept, `log` restored, `Support/PcHookServer.cs` owned by WP3 (D0-c) | §3.4, §11.1, §11.2, §11.3 |
| 13 | Most PC patches get no execution test; no JIT test | applied (merged with 29): PC twins for `nativeonline`, `adminlink`, `adminpage`, `graphics`; `EveryPatchedPcMethodJits`; per-site reasons where execution is impossible | §12.4 |
| 14 | M3 changes the client World (fields nulled while client handlers stay subscribed) | applied: M3 calls the base `OnStop` only in headless Worlds, the client keeps the stock call; [HL-V] §6's "harmless" is corrected; both branches tested | §1.1, §8.4, §12.4 |
| 15 | Native per-user files in Documents/`%APPDATA%` never mentioned | applied: §3.5 note, `collect` listing (no contents), PLAYER-SETUP-PC text | §3.5, §9.2, §16 |
| 16 | No fallback when the overlay opens but shows no page; browser not "one click away" | applied, with a stronger fallback: no `PageAlive` within 20 s after an overlay open makes the next `auto` open use the system browser; plus the page action "Open in the system browser" (session-authenticated, trigger `browser`) and its capability flag; WR2 step 6 with the overlay off | §3.3, §3.6, §4.6, §6.5, §12.2, §13 |
| 17 | The watchdog's stall snapshot reads `/proc` on Windows | applied: `WindowsThreads` (`ProcessThread` snapshot) behind `IThreadSnapshot`, with tests | §5.2, §6.2, §6.3, §12.2 |
| 18 | `*_args.txt` only a `status` leftover | applied: `install` refuses it (also with `--force`); the bootstrap WARN and status line | §3.8, §6.2, §7.6, §12.2, §12.3 |
| 19 | Duplicate of 6 | applied with 6 | §3.1 |
| 20 | Duplicate of 2 (SocketReuse bodies) | applied with 2 | §3.1, §4.6 |
| 21 | D0-a adds a PC csproj that cannot build before D0-b | applied: the csproj joins the sln in D0-b; Day-0 commits are pushed only green | §5.1, §11.1, §11.2 |
| 22 | `SettingsSchema.cs` unowned: Android/HD texts and toggles on PC, no label for the new key | applied: WP1 owns it; `GroupsFor(platform)` filter, PC wording, the label, a test | §3.6, §4.6, §11.3, §12.1 |
| 23 | C12 corlib assertion only in WP3 | applied: `PcReader` + `AssertPcCorlib` (D0-c), required by every PC real-file test | §3.4, §12 intro, §12.4 |
| 24 | Q8's Steam IAP/xpromo half dropped | applied: WR2 step 13 opens the store and xpromo screens; Q8 and R11 restored with the bad branch | §13, §14, §17 |
| 25 | WP-G side effects for STATUS have no owner | applied: WP5b's STATUS bullet includes them | §10, §16 |
| 26 | Case-insensitive NTFS collisions not carried over | applied: audit bullet with `OrdinalIgnoreCase` checks on every platform, tests | §4.6, §6.3, §11.3, §12.1 |
| 27 | `AndroidOutputUnchangedTests` cannot hold (server MVID in `DerivedMvid`); P5 "byte-identical" contradicts intended changes | applied: fixed stand-in server with a pinned MVID, an MVID-independent IL/table check, P5 rewritten with the allow-list, entry-by-entry APK diff | §1.3, §8.1, §12.4, §12.6 |
| 28 | Android capability matrix moves into code no test compiles | applied: `AndroidCapabilities.From` in the unguarded part of `AndroidJni.cs`; the `AdminHostTests` matrix moves to it unchanged | §3.3, §5.2, §12.2 |
| 29 | Headless PC edits tested structurally only | applied (merged with 13): `RealPcHeadlessExecutionTests` for H8, H10, H11, M2, M3 with the uninitialised-World technique; H9 stated as not executable (block A runs first) with exact branch asserts | §12.4 |
| 30 | `MCVS_WINDOWS`-only code never enters a test assembly; the MEM rule invisible in WR2 | applied: one-line delegation rule (`WindowsBoot`, `WindowsFiles`), `PcHostWiringTests`, `ConfigureWindows` asserted in `EmbeddedHostTests`, `MEM rule on, removed <n>` in the graphics line | §3.1, §3.8, §4.6, §6.1, §6.2, §6.7, §12.2 |
| 31 | No check of P/Invoke declarations against the x86 ABI and the real exports | applied: `PcPInvokeTests` (cdecl, exports present in `steam_api.dll`, `U1`, Unicode/`ExactSpelling`, `SetLastError`); `PInvokeAbiTests` filtered to `libc` | §6.3, §12.2 |
| 32 | Uninstall tests cover only the clean path; temp locations unspecified; no host-side P1 test | applied: `.mcvs-tmp` names and locations, status/uninstall handle them, `PcInstallerFaultTests`, uninstall from every state, the host-side root-diff test, the WR2 before/after listing | §1.3, §3.5, §7.4, §7.6, §12.2, §12.3, §13 |
| 33 | Nothing checks that the shipped bytes are the tested bytes | applied: §12.6 records the Linux `written` hashes; `verify` prints them; WR2 step 3 compares and blocks WP5b on a difference | §7.6, §9.2, §12.6, §13, §17 |
| 34 | `--remove-data` deletes the folder the kit log is open in; kit temp folders never cleaned | applied: the log moves to `%TEMP%` first; `finally` cleanup plus a stale-folder sweep; tests with Windows delete semantics; optional WR2 step 17 | §7.6, §9.2, §12.3, §13 |
| 35 | WR2 never exercises the `auto` fallback or graphics modes | applied: step 6 in fullscreen, windowed and overlay-off | §13, §14, §17 |
| 36 | Minimised game, `restartVia=host` and the runtime fields never checked | applied: step 7 minimised with `system`, step 15 with `host`, step 4 expected values. Not taken: a debug setting to force the 3 s `ExitViaHost` fallback (no new setting for one check; `WindowsExitTests` covers the order) | §13 |
| 37 | `PcHookContractTests` skips on a fixed `release/` path, even under `MCVS_REQUIRE_PC=1` | applied: the DLL of the test's configuration, a failure under `MCVS_REQUIRE_PC=1`, the same check in the kit's publish target | §9.1, §11.2, §12.4 |
| 38 | No `PatchPlanTests`; §8.8 points at a missing matrix | applied: `PatchPlanTests` (D0-d) with the sets, order, refusals and the Android equality | §8.8, §11.2, §11.3, §12.4 |
| 39 | aapt2 check optional; no test that `--patch` without `game` keeps today's manifest | applied: `MCVS_REQUIRE_BUILD_TOOLS=1` in the WP5a run; `ManifestGameTests` pins the 7f24f82 manifest bytes without `game` | §12.5, §12.6 |

Unresolved after this round: none in the design. Open by nature (settled only by WR2, §14): Q3 (the overlay's CEF with
the fragment and cookie), Q8's store/xpromo half, F3 (whether the game thread runs while minimised), and the H9 behaviour,
which no Linux test can execute (WR2 step 14).
